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
        nativeSurface: false, conversationState: null, status: null, busy: false, TextDecoder,
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

test('unified model selector preserves a legacy conversation and privacy controls', t => {
    const dom = new JSDOM('<div id="modal"><select id="model"></select><input id="native"><input id="blocked"><textarea id="input"></textarea></div>', { runScripts: 'outside-only' });
    t.after(() => dom.window.close());
    const w = dom.window;
    const conversation = { id: 'existing-thread', mode: 'teacher', modelSelection: 'openai:gpt-6-astra' };
    Object.assign(w, {
        activeConversation: () => conversation, busy: false, businessConfiguration: null,
        chatModels: [{ id: 'openai:gpt-6-astra', label: 'OpenAI · gpt-6-astra', configured: true }],
        modelSelection: w.document.getElementById('model'), nativeOnly: w.document.getElementById('native'),
        externalAnsweringBlocked: w.document.getElementById('blocked'), input: w.document.getElementById('input'),
        modalElement: w.document.getElementById('modal'), send: null, sendIcon: null, newConversation: null,
        retryRequest: null, updateThinkingStatus() {}
    });
    w.eval(implementation('renderModes'));
    w.eval(implementation('setBusy'));
    w.renderModes();
    assert.equal(w.modelSelection.value, 'openai:gpt-6-astra');
    assert.equal(conversation.id, 'existing-thread');
    w.setBusy(true);
    assert.equal(w.modelSelection.disabled, true);
    w.setBusy(false);
    assert.equal(w.nativeOnly.disabled, false);
    assert.equal(w.externalAnsweringBlocked.disabled, false);
    assert.equal(w.modelSelection.disabled, false);
    conversation.mode = 'business';
    w.renderModes();
    assert.equal(w.modelSelection.disabled, true);
});

test('only exact same-origin action review links become interactive', t => {
    const w = fixture(t);
    const body = w.show('[Review exact website action](/founder/legend-ai/actions/12345678-1234-1234-1234-123456789abc) [Bad](https://untrusted.example/steal)');
    assert.equal(body.querySelectorAll('a').length, 1);
    assert.equal(body.querySelector('a').getAttribute('href'), '/founder/legend-ai/actions/12345678-1234-1234-1234-123456789abc');
    assert.equal(body.querySelector('a').rel, 'noopener');
});

test('current page capture excludes assistant, hidden content and form values', t => {
    const dom = new JSDOM(`<div id="ai"><main>Private assistant conversation</main></div>
      <div class="modal" style="display:none"><main>Hidden Messages panel</main></div>
      <main data-legend-page-content><h1>Sales overview</h1><p>Visible result: 7 inquiries</p>
      <input value="secret"><textarea>Unsent draft</textarea><select><option>Private choice</option></select>
      <div hidden>Hidden token</div><div style="display:none"><p>Hidden ancestor</p></div>
      <div contenteditable="true">Unsent editor</div><div role="textbox">Editor value</div><div data-private>Private account</div><script>secret()</script></main>`, { runScripts: 'outside-only' });
    t.after(() => dom.window.close());
    const w = dom.window; w.modalElement = w.document.getElementById('ai');
    w.nativeSurface = false; w.eval(implementation('captureCurrentPage'));
    assert.equal(w.captureCurrentPage(), 'Sales overview\nVisible result: 7 inquiries');
    w.document.querySelector('[data-legend-page-content]').removeAttribute('data-legend-page-content');
    assert.equal(w.captureCurrentPage(), 'Sales overview\nVisible result: 7 inquiries');
    w.document.querySelector('h1').textContent = 'Changed page';
    assert.ok(w.captureCurrentPage().startsWith('Changed page'));
    w.document.querySelector('p').textContent = 'x'.repeat(30000);
    assert.equal(w.captureCurrentPage().length, 24000);
});

test('mobile model placement moves one control and preserves selection and listeners', t => {
    const dom = new JSDOM('<header><span id="control"><select><option>a</option><option>b</option></select></span></header><footer></footer>', { runScripts: 'outside-only' });
    t.after(() => dom.window.close());
    const w = dom.window;
    Object.assign(w, { modelControl: w.document.getElementById('control'), modelHeaderHome: w.document.querySelector('header'),
        mobileModelSlot: w.document.querySelector('footer'), modebar: null, mobileControls: null, isMobile: () => true });
    const select = w.modelControl.querySelector('select'); select.value = 'b'; let changes = 0;
    select.addEventListener('change', () => changes++);
    w.eval(implementation('syncControlPlacement')); w.syncControlPlacement();
    assert.equal(select.closest('footer'), w.mobileModelSlot); assert.equal(select.value, 'b');
    w.isMobile = () => false; w.syncControlPlacement();
    assert.equal(select.closest('header'), w.modelHeaderHome);
    select.dispatchEvent(new w.Event('change')); assert.equal(changes, 1);
    assert.equal(w.document.querySelectorAll('select').length, 1);
});

test('embedded action review stays in the same scoped window', t => {
    const w = fixture(t); w.nativeSurface = true;
    const rendered = w.show('[Review](/founder/legend-ai/actions/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa)');
    assert.equal(rendered.querySelector('a').target, '_self');
});
