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

test('LEGEND static public HTML never paints an old authoring template ahead of the published revision', () => {
  assert.match(build, /data-legend-canonical-pending="true"/);
  assert.match(build, /<main id="main">\$\{businessMode\?body:''\}<\/main>/);
  assert.match(build, /<script src="\/legend-public-cms\.js\?v=\$\{cmsVersion\}" defer>/);
});
test('Protect conceals legacy Razor content until the canonical published document is applied', () => {
  assert.match(protect, /data-legend-canonical-pending="@\(!isStandaloneQuoteLanding \? "true" : null\)"/);
  assert.match(protect, /<main id="main" class="@mainClasses">\s*@RenderBody\(\)/);
});
test('Canonical CMS still applies the saved published document and does not wait for optional provider runtime', () => {
  assert.match(cms, /const response = await fetch\(url, \{ cache: 'no-store' \}\)/);
  assert.match(cms, /applyDocument\(payload\.document \|\| \{\}\);/);
  assert.match(cms, /function revealPublishedWebsite\(\)/);
  assert.match(cms, /document\.documentElement\.removeAttribute\('data-legend-canonical-pending'\)/);
  assert.match(cms, /An older version will not be shown/);
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

test('Protect emits Website Studio published SEO metadata and favicon on initial response', () => {
  assert.match(protect, /publishedSeoTitle/);
  assert.match(protect, /publishedSeoDescription/);
  assert.match(protect, /WebsiteSeoDb/);
  assert.match(protect, /WebsiteFaviconParity\.PublicUrl/);
});
test('Published CMS hydration preserves the initial favicon URL', () => {
  assert.match(cms, /const href = editorMode && value \? mediaUrl\(value\) : originalFaviconHref/);
});
test('Published LEGEND HTML uses the same studio icon authority as ClientApp and the static LEGEND homepage', () => {
  assert.match(business, /company \? publicApiBase \+ '\/api\/website-content\/public\/legend\/favicon' : '\/favicon\.jpg'/);
});

test('Business compilation uses one crawlable icon URL and structured data', () => {
  assert.match(business, /structured\.setAttribute\('type','application\/ld\+json'\)/);
  assert.match(business, /favicon\.setAttribute\('href','\/favicon\.jpg'\)/);
});
