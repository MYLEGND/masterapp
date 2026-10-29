import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {JSDOM} from 'jsdom';

const layout = await readFile(new URL('../../Protect-Website/Views/Shared/_Layout.cshtml', import.meta.url), 'utf8');
const css = await readFile(new URL('../../Protect-Website/wwwroot/css/site.css', import.meta.url), 'utf8');
const markup = layout.match(/(<div id="psalmsModal"[\s\S]*?)\n\n@if \(compiledPublication\?\.Footer/)[1];
const scripts = [...layout.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(m => m[1]);
const script = scripts.find(s => s.includes("const trigger = document.getElementById('psalmsTrigger');"));
function fixture(trigger = true, initialize = true) {
  const dom = new JSDOM(`<style>${css}</style><button id="before">Before</button>${trigger ? '<button id="psalmsTrigger">Open</button>' : ''}${markup}<button id="after">After</button>`, {url: 'https://fixture.invalid', runScripts: 'outside-only'});
  // jsdom cannot perform native layout/Tab. Native CI tests own those claims.
  Object.defineProperty(dom.window.HTMLElement.prototype, 'inert', {
    get() { return this.hasAttribute('inert'); },
    set(value) { this.toggleAttribute('inert', !!value); }
  });
  dom.window.HTMLElement.prototype.getClientRects = function () { return this.closest('[hidden], [inert]') ? [] : [{}]; };
  if (initialize) dom.window.eval(script);
  return dom;
}
test('actual Protect server markup excludes closed descendants before JS and without trigger', () => {
  for (const initialize of [false, true]) {
    const dom = fixture(false, initialize), modal = dom.window.document.getElementById('psalmsModal');
    assert.equal(modal.hidden, true);
    assert.equal(modal.hasAttribute('inert'), true);
    assert.equal(modal.getAttribute('aria-hidden'), 'true');
    assert.equal(dom.window.getComputedStyle(modal).display, 'none');
    dom.window.close();
  }
});
test('actual synchronous lifecycle preserves content, opener and pre-existing scroll ownership', () => {
  for (const locked of [false, true]) {
    const dom = fixture(), {document} = dom.window;
    const modal = document.getElementById('psalmsModal'), trigger = document.getElementById('psalmsTrigger');
    document.body.classList.toggle('no-scroll', locked);
    trigger.focus(); trigger.click();
    assert.equal(modal.hidden, false); assert.equal(modal.inert, false);
    assert.equal(modal.getAttribute('aria-hidden'), 'false');
    assert.equal(document.activeElement.className, 'scripture-card');
    assert.ok(document.getElementById('psalmsText').children.length > 0);
    const content = document.getElementById('psalmsText').innerHTML;
    const streak = dom.window.localStorage.getItem('psalmsStreak');
    trigger.click(); // repeated open must not capture the dialog as the opener
    document.getElementById('psalmsClose').click();
    assert.equal(modal.hidden, true); assert.equal(modal.inert, true);
    assert.equal(document.activeElement, trigger);
    assert.equal(document.body.classList.contains('no-scroll'), locked);
    trigger.click(); modal.click();
    assert.equal(document.activeElement, trigger);
    assert.equal(document.getElementById('psalmsText').innerHTML, content);
    assert.equal(dom.window.localStorage.getItem('psalmsStreak'), streak);
    dom.window.close();
  }
});
test('Tab containment and Escape operate only on the open scripture dialog', () => {
  const dom = fixture(), {document, KeyboardEvent} = dom.window;
  const trigger = document.getElementById('psalmsTrigger'), modal = document.getElementById('psalmsModal');
  const key = (value, shiftKey = false) => { const e = new KeyboardEvent('keydown', {key: value, shiftKey, bubbles: true, cancelable: true}); document.dispatchEvent(e); return e; };
  document.getElementById('after').focus();
  assert.equal(key('Tab').defaultPrevented, false); assert.equal(key('Escape').defaultPrevented, false);
  assert.equal(document.activeElement.id, 'after');
  trigger.focus(); trigger.click();
  for (const shift of [false, true]) {
    assert.equal(key('Tab', shift).defaultPrevented, true);
    assert.equal(document.activeElement.id, 'psalmsClose');
  }
  assert.equal(key('Escape').defaultPrevented, true);
  assert.equal(modal.hidden, true); assert.equal(document.activeElement, trigger);
  dom.window.close();
});
