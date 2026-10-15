import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from 'jsdom';

const source = readFileSync(new URL('../../AgentPortal/wwwroot/js/legend-founder-ai.js', import.meta.url), 'utf8');
function implementation(name) {
    const start = source.search(new RegExp(`^    (?:async )?function ${name}\\(`, 'm'));
    assert.notEqual(start, -1, name);
    const tail = source.slice(start);
    const end = tail.search(/\n    }\n/);
    assert.notEqual(end, -1, name);
    return tail.slice(0, end + 6);
}
function fixture(t) {
    const dom = new JSDOM('<main id="transcript"></main>', { runScripts: 'outside-only' });
    t.after(() => dom.window.close());
    const w = dom.window;
    Object.assign(w, {
        transcript: w.document.getElementById('transcript'), logoSource: '/legend.svg',
        userInitials: 'FO', userAvatarSource: '', retryRequest: null, welcome: null,
        conversationState: null, status: null, busy: false, TextDecoder,
        applyOperationalProgress() {}, updateThinkingStatus() {}
    });
    for (const name of ['appendAssistantInline', 'renderAssistantContent', 'appendBubble',
        'scrollToBottom', 'renderConversation', 'storedMessage', 'consumeChatResultStream']) {
        w.eval(implementation(name));
    }
    w.show = (text, role = 'assistant') => {
        w.transcript.replaceChildren();
        w.appendBubble(role, text, false);
        return w.transcript.querySelector('.legend-founder-ai-bubble').firstElementChild;
    };
    return w;
}

test('assistant answers render headings, paragraphs, bold, code and both list forms', t => {
    const w = fixture(t);
    const body = w.show('## Result\n\n**Verified:** 4 leads with `CRM`.\n\n- CRM confirmed\n- Revenue unknown\n\n3. Inspect\n4. Review');
    assert.equal(body.querySelector('h2').textContent, 'Result');
    assert.equal(body.querySelector('strong').textContent, 'Verified:');
    assert.equal(body.querySelector('p code').textContent, 'CRM');
    assert.equal(body.querySelectorAll('ul li').length, 2);
    assert.equal(body.querySelector('ol').start, 3);
    assert.equal(body.querySelector('ol li:last-child').hasAttribute('value'), false);
    assert.equal(body.hasAttribute('data-user-content'), true);
    const repeated = w.show('1. Inspect\n1. Review\n1. Validate');
    assert.equal(repeated.querySelector('ol').start, 1);
    assert.equal(repeated.querySelectorAll('ol li').length, 3);
    assert.equal(repeated.querySelectorAll('ol li[value]').length, 0);
});

test('plain answers and malformed delimiters remain readable without dropping content', t => {
    const w = fixture(t);
    assert.equal(w.show('Four leads are verified.').textContent, 'Four leads are verified.');
    const literal = 'Unclosed **bold and `code; [broken]( and ###noheading';
    assert.equal(w.show(literal).textContent, literal);
    assert.equal(w.show('** spaced **').textContent, '** spaced **');
    assert.equal(w.show('\\*literal\\* and \\**not bold\\**').textContent, '*literal* and **not bold**');
    assert.equal(w.transcript.querySelectorAll('strong').length, 0);
});

test('code preserves markup, backslashes and HTML as literal text', t => {
    const w = fixture(t);
    const code = '<script>alert(1)</script>\n**literal** \\*value\\*';
    const body = w.show('```html\n' + code + '\n```\n\n``a ` b``');
    assert.equal(body.querySelector('pre code').textContent, code);
    assert.equal(body.querySelector('p code').textContent, 'a ` b');
    const nested = w.show('**Result: `a**b`**');
    assert.equal(nested.querySelector('strong').textContent, 'Result: a**b');
    assert.equal(nested.querySelector('strong code').textContent, 'a**b');
    assert.equal(body.querySelectorAll('script,strong').length, 0);
    assert.equal(w.show('~~~\n**unfinished fence**').querySelector('pre code').textContent, '**unfinished fence**');
});

test('raw HTML, dangerous URLs and remote image Markdown never create active elements', t => {
    const w = fixture(t);
    const content = '<img src=x onerror="window.pwned=1"><script>window.pwned=1</script>\n\n[run](javascript:alert(1)) [data](data:text/html,evil) ![remote](https://evil.example/pixel)';
    const body = w.show(content);
    assert.equal(body.querySelectorAll('script,img,a,iframe,style,svg').length, 0);
    assert.ok(body.textContent.includes('<img src=x'));
    assert.ok(body.textContent.includes('javascript:alert(1)'));
    assert.equal(w.pwned, undefined);
});

test('user and service messages preserve exact literal text; JSON retains machine format', t => {
    const w = fixture(t);
    const content = '## User\n\n**literal** `code` \\*escaped\\* <b>tag</b>';
    for (const role of ['user', 'service']) {
        const body = w.show(content, role);
        assert.equal(body.textContent, content);
        assert.equal(body.children.length, 0);
        assert.equal(body.hasAttribute('data-user-content'), role === 'user');
    }
    const scalar = '"**literal**"';
    assert.equal(w.show(scalar).querySelector('pre code').textContent, scalar);
    const json = '{\n  "answer": "**literal** <tag>"\n}';
    const body = w.show(json);
    assert.equal(body.querySelector('pre code').textContent, json);
    assert.equal(body.querySelectorAll('strong,tag').length, 0);
});

test('streamed final result and persisted history use the same canonical renderer', async t => {
    const w = fixture(t);
    const content = '## Result\n\n**Verified:** 4 leads.\n\n- CRM confirmed\n- Revenue unknown';
    const response = new Response(JSON.stringify({ type: 'result', result: { succeeded: true, message: content } }) + '\n');
    const result = await w.consumeChatResultStream(response, new AbortController().signal);
    w.activeConversation = () => ({ messages: [{ role: 'assistant', content: result.message }] });
    w.renderConversation();
    const finalMarkup = w.transcript.innerHTML;
    const stored = w.storedMessage({ authorKind: 'Assistant', body: content, responseProvenance: { responseAuthority: 'legend' } });
    w.activeConversation = () => ({ messages: [stored] });
    w.renderConversation();
    assert.equal(w.transcript.innerHTML, finalMarkup);
    assert.equal(w.transcript.querySelectorAll('h2,strong,ul').length, 3);
});
