const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.resolve(__dirname, '../../AgentPortal/wwwroot/js/website-analytics.js'), 'utf8');
const start = source.indexOf('  function downloadCsv(');
const end = source.indexOf('  let deviceIntelligenceLoading', start);
async function exportCsv(rows) {
  let blob;
  const anchor = { click() {} };
  const sandbox = {Blob, URL: {createObjectURL(value) {blob = value; return 'blob:test';}, revokeObjectURL() {}},
    document: {createElement() {return anchor;}, body: {appendChild() {}, removeChild() {}}}};
  vm.runInNewContext(source.slice(start, end) + '\nthis.download = downloadCsv;', sandbox);
  sandbox.download('test.csv', rows, [{header: 'Name', selector: 'name'}, {header: 'Amount', selector: 'amount'}]);
  assert.equal(anchor.download, 'test.csv');
  return blob.text();
}
test('CSV uses real rows and escapes commas, quotes and embedded newlines', async () => {
  assert.equal(await exportCsv([{name: 'A,"B"\nC', amount: 12}]), '"Name","Amount"\r\n"A,""B""\nC","12"\r\n');
});
test('empty CSV exports a usable header', async () => {
  assert.equal(await exportCsv([]), '"Name","Amount"\r\n');
});
test('CSV treats formula-like inputs as text', async () => {
  const text = await exportCsv([{name: '=HYPERLINK("bad")', amount: '@SUM(1)'}, {name: '\tformula', amount: '-2+3'}]);
  assert.ok(text.includes('"\'=HYPERLINK(""bad"")"'));
  assert.ok(text.includes('"\'@SUM(1)"'));
  assert.ok(text.includes('"\'\tformula"'));
  assert.ok(text.includes('"\'-2+3"'));
});
