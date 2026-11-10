import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { JSDOM } from 'jsdom';

test('bundle picker stages exact variants, previews availability and saves through the product form', () => {
  const view = readFileSync(new URL('../../ParfaitApp/Views/InternalModules/Products.cshtml', import.meta.url), 'utf8');
  const code = view.slice(view.indexOf('        const bundlePicker ='), view.indexOf('        function initializeRoot('));
  assert.ok(code.includes('function renderBundleLinks'));
  const dom = new JSDOM(`
    <form data-pf-product-edit-form>
      <input name="Id" value="outfit">
      <input name="BundleComponentsJson" value="[]" data-pf-bundle-recipe>
      <button type="button" data-pf-bundle-open>Link pieces</button>
      <p data-pf-bundle-summary></p>
      <div data-pf-inventory-row>
        <input data-field="Size" value="M">
        <input data-pf-enabled type="checkbox" checked>
        <input data-pf-stock value="7">
        <input data-field="OriginalStockQuantity" value="7">
      </div>
    </form>
    <dialog id="pfBundlePicker">
      <select data-bundle-size></select>
      <select data-bundle-piece>
        <option value="self" data-product="outfit" data-stock="7">Outfit</option>
        <option value="top-m" data-product="top" data-stock="5">Top · M</option>
        <option value="bottom-l" data-product="bottom" data-stock="2">Bottom · L</option>
      </select>
      <input data-bundle-units type="number">
      <button data-bundle-add>Add</button><p data-bundle-message></p><ul data-bundle-list></ul>
      <button data-bundle-cancel>Cancel</button><button data-bundle-apply>Apply</button>
    </dialog>`, { runScripts: 'outside-only' });
  const { window } = dom;
  const $ = selector => window.document.querySelector(selector);
  const picker = $('#pfBundlePicker');
  picker.showModal = () => picker.setAttribute('open', '');
  picker.close = () => picker.removeAttribute('open');
  const saves = [];
  window.updateInventoryRow = () => {};
  window.scheduleAutoSave = form => saves.push(JSON.parse(form.querySelector('[data-pf-bundle-recipe]').value));
  window.eval(code);
  try {
    $('[data-pf-bundle-open]').click();
    assert.equal($('[data-bundle-piece] option[value="self"]'), null);
    $('[data-bundle-units]').value = '2';
    $('[data-bundle-add]').click();
    $('[data-bundle-add]').click();
    assert.match($('[data-bundle-message]').textContent, /once per size/);
    $('[data-bundle-cancel]').click();
    assert.deepEqual(saves, []);
    assert.equal($('[data-pf-bundle-recipe]').value, '[]');

    $('[data-pf-bundle-open]').click();
    $('[data-bundle-units]').value = '2';
    $('[data-bundle-add]').click();
    $('[data-bundle-piece]').value = 'bottom-l';
    $('[data-bundle-units]').value = '1';
    $('[data-bundle-add]').click();
    $('[data-bundle-apply]').click();
    assert.deepEqual(saves, [[
      { BundleSize: 'M', InventoryItemId: 'top-m', Units: 2 },
      { BundleSize: 'M', InventoryItemId: 'bottom-l', Units: 1 }
    ]]);
    assert.equal($('[data-pf-stock]').value, '2');
    assert.equal($('[data-pf-stock]').readOnly, true);
  } finally {
    window.close();
  }
});
