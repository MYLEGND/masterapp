import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from 'jsdom';

// Exercise the shipped private normalizer in its browser realm without starting
// networking or an editor session. The hook changes only the test VM bootstrap.
const source = readFileSync(new URL('../../../SHARED/WebsitePlatform/legend-public-cms.js', import.meta.url), 'utf8');
const anchor = '\n  if (renderInput && !studioIsolationMode) {';
assert.equal(source.split(anchor).length, 2, 'canonical CMS bootstrap anchor must be unique');
const dom = new JSDOM('<!doctype html><html><head></head><body></body></html>', {
  url: 'https://site.example/', runScripts: 'outside-only'
});
try {
  dom.window.LEGEND_PUBLIC_CMS_CONTEXT = { siteKey: 'legend', apiBase: '' };
  dom.window.fetch = () => { throw new Error('Normalization bridge must not perform network requests'); };
  dom.window.eval(source.replace(anchor,
    '\n  globalThis.__normalizeForPreservationBridge = normalizeDocument; return;' + anchor));
  const input = JSON.parse(readFileSync(0, 'utf8'));
  const output = dom.window.__normalizeForPreservationBridge(input);
  assert.equal(output.version, 3);
  process.stdout.write(JSON.stringify(output));
} finally {
  dom.window.close();
}
