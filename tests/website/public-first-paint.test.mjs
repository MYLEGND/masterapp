import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';

const read = path => readFileSync(new URL('../../'+path,import.meta.url),'utf8');
const build = read('Legend-Website/scripts/build.mjs');
const protect = read('Protect-Website/Views/Shared/_Layout.cshtml');
const cms = read('SHARED/WebsitePlatform/legend-public-cms.js');
const business = read('Legend-Website/scripts/render-business.mjs');
const layoutPaths = [
  'AgentPortal/Views/Shared/_Layout.cshtml',
  'ClientApp/Views/Shared/_Layout.cshtml',
  'ParfaitApp/Views/Shared/_Layout.cshtml'
];

test('LEGEND static site has a visible, server-built first paint before deferred CMS and tracking', () => {
  assert.match(build, /<html lang="en" data-legend-site="\$\{siteKey\}"><head>/);
  assert.doesNotMatch(build, /<html lang="en" data-legend-site="\$\{siteKey\}" hidden>/);
  assert.match(build, /<main id="main">\$\{body\}<\/main>/);
  assert.match(build, /<script src="\/legend-public-cms\.js\?v=\$\{cmsVersion\}" defer>/);
});
test('Protect renders the native first page without a document-wide hidden gate', () => {
  assert.match(protect, /<html lang="en">\s*<head>/);
  assert.doesNotMatch(protect, /data-legend-canonical-pending|hidden="@\(!isStandaloneQuoteLanding/);
  assert.match(protect, /<main id="main" class="@mainClasses">\s*@RenderBody\(\)/);
});
test('Canonical CMS still applies the saved published document and does not wait for optional provider runtime', () => {
  assert.match(cms, /const response = await fetch\(url, \{ cache: 'no-store' \}\)/);
  assert.match(cms, /applyDocument\(payload\.document \|\| \{\}\);/);
  assert.match(cms, /await loadPublic\(\);\s*\/\/ Published content is independent of measurement\/provider startup\.\s*void startPublicRuntime\(\);/);
  assert.match(business, /window\.LEGEND_PUBLIC_CMS_RENDER_INPUT=/);
  assert.match(business, /window\.LEGEND_PUBLIC_CMS_RENDER_COMPLETE!==true/);
});
test('Other three web app layouts never hide their whole document while loading scripts', () => {
  for(const path of layoutPaths){
    const source=read(path);
    assert.match(source, /<html lang="en">/);
    assert.doesNotMatch(source, /<html lang="en" hidden|data-legend-canonical-pending/);
  }
});
