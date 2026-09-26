import { readFile, access } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { resolve } from 'node:path';
const root=resolve(import.meta.dirname,'..');
const routes=['','about','contact','logo','privacy-terms'];
const businessPreviewRoute='business-preview';
for(const route of routes){const f=resolve(root,'dist',route,'index.html');await access(f);const s=await readFile(f,'utf8');for(const required of ['site-header','site-footer','brand-wordmark','LEGEND®'])if(!s.includes(required))throw new Error(`${f} missing ${required}`);}
{
  const f=resolve(root,'dist',businessPreviewRoute,'index.html');
  await access(f);
  const s=await readFile(f,'utf8');
  for(const required of ['site-header','site-footer','siteKey:"business"','businessId','data-business-name','noindex,nofollow'])
    if(!s.includes(required))throw new Error(`${f} missing ${required}`);
}
for(const excluded of ['store','team']){try{await access(resolve(root,'dist',excluded,'index.html'));throw new Error(`Excluded route generated: ${excluded}`)}catch(e){if(e.code!=='ENOENT')throw e;}}
const css=await readFile(resolve(root,'dist/site.css'),'utf8');
if(css.includes('overflow:hidden}body')) throw new Error('Global body scroll accidentally disabled.');
console.log('Route, business preview, global chrome, exclusion, and scroll checks passed.');

const index=await readFile(resolve(root,'dist','index.html'),'utf8');
for(const required of ['legend-public-web.js','legend-public-cms.js','instagram.com/legend.vault','apps.apple.com/us/app/legend/id6798419225'])if(!index.includes(required))throw new Error(`dist/index.html missing ${required}`);

const observer=await readFile(resolve(root,'dist/js/page-health.js'),'utf8');
const authority=await readFile(resolve(root,'../SHARED/wwwroot/js/page-health.js'),'utf8');
if(observer!==authority)throw new Error('Static diagnostics must copy the existing shared observer exactly.');
const provenance=JSON.parse(await readFile(resolve(root,'dist/build-provenance.json'),'utf8'));
if(!/^[a-f0-9]{40}$/.test(provenance.gitCommitHash))throw new Error('Missing static build provenance.');
for(const route of routes){
  const html=await readFile(resolve(root,'dist',route,'index.html'),'utf8');
  if((html.match(/src="\/js\/page-health.js"/g)||[]).length!==1)throw new Error('Expected exactly one shared observer.');
  for(const value of [`data-route="/${route}"`, `data-git-commit-hash="${provenance.gitCommitHash}"`, 'data-app="Legend-Website"'])
    if(!html.includes(value))throw new Error('Missing static diagnostics metadata: '+value);
  const apiBase=/apiBase:"(https:\/\/[^"/]+)"/.exec(html)?.[1];
  if(!apiBase || !html.includes(`data-endpoint="${apiBase}/api/runtime-diagnostics"`) || !html.includes(`data-bootstrap="${apiBase}/api/runtime-diagnostics/bootstrap"`))
    throw new Error('Diagnostics and CMS must share the existing API base.');
  if(html.indexOf('src="/js/page-health.js"')>html.indexOf('src="/legend-public-web.js?v='))throw new Error('Observer must load before app scripts.');
}
console.log('Shared observer copy, bounded route metadata, existing API base, and build identity checks passed.');

for (const file of ['legend-public-cms.js','legend-public-web.js','site.css']) {
  const bytes=await readFile(resolve(root,'dist',file));
  const version=createHash('sha256').update(bytes).digest('hex');
  if(file.endsWith('.js') && !bytes.equals(await readFile(resolve(root,'../SHARED/WebsitePlatform',file))))throw new Error('Shared asset differs from authority: '+file);
  for(const route of routes){
    const html=await readFile(resolve(root,'dist',route,'index.html'),'utf8');
    if(!html.includes(`/${file}?v=${version}`))throw new Error('Missing exact asset version: '+file);
  }
}
console.log('Shared editor identity and content-versioned assets passed on every route.');

for (const [built, authority] of [
  ['legend-public-tracking.js','../SHARED/WebsitePlatform/tracking.js'],
  ['legend-public-meta-signal-intelligence.js','../SHARED/WebsitePlatform/meta-signal-intelligence.js']
]) {
  const builtBytes=await readFile(resolve(root,'dist',built));
  const authorityBytes=await readFile(resolve(root,authority));
  if(!builtBytes.equals(authorityBytes))throw new Error('Public runtime must copy the Protect Website analytics authority exactly: '+built);
}
for(const route of routes){
  const html=await readFile(resolve(root,'dist',route,'index.html'),'utf8');
  for(const required of ['trackingAsset:"/legend-public-tracking.js?v=','metaSignalAsset:"/legend-public-meta-signal-intelligence.js?v='])
    if(!html.includes(required))throw new Error('LEGEND public route missing canonical Protect runtime asset: '+required);
}
console.log('Shared platform tracking and Meta intelligence are the exact runtime source for Protect, LEGEND, and business builds.');

const businessPreview=await readFile(resolve(root,'dist',businessPreviewRoute,'index.html'),'utf8');
for(const file of ['legend-public-cms.js','legend-public-web.js','site.css']){
  const bytes=await readFile(resolve(root,'dist',file));
  const version=createHash('sha256').update(bytes).digest('hex');
  if(!businessPreview.includes(`/${file}?v=${version}`))throw new Error('Business preview missing exact shared asset version: '+file);
}
const sitemap=await readFile(resolve(root,'dist','sitemap.xml'),'utf8');
if(sitemap.includes('/business-preview'))throw new Error('Business preview must not enter the public sitemap.');
console.log('Business preview consumes the same shared renderer assets without entering the public sitemap.');

for(const route of ['','about','services','contact']){
  const html=await readFile(resolve(root,'dist/business-preview',route,'index.html'),'utf8');
  if(!html.includes(`data-page-key="${route||'home'}"`))throw new Error('Business page scope is missing: '+route);
  if(/Berthony|MyLegnd, LLC|Christ-centered|connect@mylegnd/.test(html))throw new Error('LEGEND company facts leaked into business template: '+route);
  if(!html.includes('noindex,nofollow')||!html.includes('<html lang="en" hidden>'))throw new Error('Business draft must wait for authenticated/published content: '+route);
}
const config=await readFile(resolve(root,'public/web.config'),'utf8');
if(!config.includes('Reject unrecognized website host'))throw new Error('Static LEGEND origin must reject foreign hosts.');
console.log('Business page scoping, draft gating, company-fact separation and static host rejection passed.');
