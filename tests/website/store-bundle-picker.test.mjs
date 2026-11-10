import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { JSDOM } from 'jsdom';

// Exercise the actual handlers in a DOM fixture. This is not rendered browser acceptance.
const view = readFileSync(new URL('../../ParfaitApp/Views/InternalModules/Products.cshtml', import.meta.url), 'utf8');
const handlers = view.slice(view.indexOf('        function refreshBundleEditor('), view.indexOf('        function initializeRoot('));
const change = view.slice(view.indexOf('        document.addEventListener("change",'), view.indexOf('        document.addEventListener("submit",'));
function fixture(t) {
  const dom = new JSDOM(`<section class="pf-product-modal"><form data-pf-product-form data-pf-product-edit-form>
    <details data-pf-link-products><summary>Link products</summary>
      <input type="hidden" name="LinkedProductIdsJson" value="[]" data-pf-linked-products disabled>
      <label><input type="checkbox" value="top" data-pf-link-choice>Top</label>
      <label><input type="checkbox" value="bottom" data-pf-link-choice>Bottom</label>
      <p data-pf-link-feedback></p><button type="button" data-pf-link-discard>Discard</button>
      <button type="button" data-pf-link-apply>Use selected products</button>
    </details><div data-pf-inventory-row><input data-pf-stock value="7"></div>
  </form></section>`, { runScripts: 'outside-only' });
  t.after(() => dom.window.close());
  const { window } = dom, $ = selector => window.document.querySelector(selector);
  const choices = [...window.document.querySelectorAll('[data-pf-link-choice]')];
  choices[0].dataset.variants = JSON.stringify([{ Size: 'S', InventoryItemId: 'top-s', Stock: 4 }, { Size: 'M', InventoryItemId: 'top-m', Stock: 0 }]);
  choices[1].dataset.variants = JSON.stringify([{ Size: 'S', InventoryItemId: 'bottom-s', Stock: 3 }, { Size: 'M', InventoryItemId: 'bottom-m', Stock: 5 }, { Size: 'L', InventoryItemId: 'bottom-l', Stock: 2 }]);
  $('[data-pf-link-products]').dataset.savedRecipe = '[]';
  const saves = [];
  window.scheduleAutoSave = form => saves.push(JSON.parse(new window.FormData(form).get('LinkedProductIdsJson')));
  window.eval(handlers + '\n' + change);
  const form = $('form'), update = () => window.refreshBundleEditor(form);
  return { window, $, form, choices, saves, update };
}
test('multiple products preview common sizes, zero stock and unmatched sizes; apply saves only IDs', t => {
  const f = fixture(t); f.choices.forEach(choice => choice.checked = true); f.update();
  assert.match(f.$('[data-pf-link-feedback]').textContent, /S: 3 available/);
  assert.match(f.$('[data-pf-link-feedback]').textContent, /M: unavailable/);
  assert.match(f.$('[data-pf-link-feedback]').textContent, /Not matched across every product: L/);
  assert.equal(new f.window.FormData(f.form).has('LinkedProductIdsJson'), false);
  assert.deepEqual(f.saves, []);
  f.$('[data-pf-link-apply]').click();
  assert.deepEqual(f.saves, [['top', 'bottom']]);
  assert.equal(f.$('[data-pf-stock]').disabled, true);
  assert.equal(new f.window.FormData(f.form).has('BundleComponentsJson'), false);
});
test('discard and editor dismissal restore the saved selection without autosaving drafts', t => {
  const f = fixture(t); f.$('[data-pf-linked-products]').value = '["top"]';
  f.choices.forEach(choice => choice.checked = true);
  f.$('[data-pf-link-discard]').click();
  assert.equal(f.choices[1].checked, false);
  f.choices[1].checked = true;
  f.$('.pf-product-modal').dispatchEvent(new f.window.Event('pf:editor-dismiss', { bubbles: true }));
  assert.equal(f.choices[1].checked, false);
  assert.equal(f.$('[data-pf-linked-products]').value, '["top"]');
  assert.equal(new f.window.FormData(f.form).has('LinkedProductIdsJson'), false);
  assert.deepEqual(f.saves, []);
});
test('checkbox change stays outside the existing autosave path', t => {
  const f = fixture(t); f.choices[0].click();
  assert.equal(f.$('[data-pf-linked-products]').value, '[]');
  assert.deepEqual(f.saves, []);
});
test('no common size prevents apply with an actionable explanation', t => {
  const f = fixture(t); f.choices.forEach(choice => choice.checked = true);
  f.choices[1].dataset.variants = JSON.stringify([{ Size: 'XL', InventoryItemId: 'other', Stock: 5 }]);
  f.update(); f.$('[data-pf-link-apply]').click();
  assert.match(f.$('[data-pf-link-feedback]').textContent, /No common enabled size/);
  assert.equal(f.$('[data-pf-link-apply]').disabled, true);
  assert.equal(f.$('[data-pf-linked-products]').value, '[]');
  assert.deepEqual(f.saves, []);
});
test('saved quantities change preview availability without being rewritten', t => {
  const f = fixture(t); f.choices.forEach(choice => choice.checked = true);
  const recipe = '[{"BundleSize":"S","InventoryItemId":"top-s","Units":2}]';
  f.$('[data-pf-link-products]').dataset.savedRecipe = recipe; f.update();
  assert.match(f.$('[data-pf-link-feedback]').textContent, /S: 2 available/);
  assert.equal(f.$('[data-pf-link-products]').dataset.savedRecipe, recipe);
});
