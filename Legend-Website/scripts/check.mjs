import { readFile, access } from 'node:fs/promises';
import { resolve } from 'node:path';
const root=resolve(import.meta.dirname,'..');
const routes=['','about','contact','logo','privacy-terms'];
for(const route of routes){const f=resolve(root,'dist',route,'index.html');await access(f);const s=await readFile(f,'utf8');for(const required of ['site-header','site-footer','brand-wordmark','LEGEND®'])if(!s.includes(required))throw new Error(`${f} missing ${required}`);}
for(const excluded of ['store','team']){try{await access(resolve(root,'dist',excluded,'index.html'));throw new Error(`Excluded route generated: ${excluded}`)}catch(e){if(e.code!=='ENOENT')throw e;}}
const css=await readFile(resolve(root,'dist/site.css'),'utf8');
if(css.includes('overflow:hidden}body')) throw new Error('Global body scroll accidentally disabled.');
console.log('Route, global chrome, exclusion, and scroll checks passed.');

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
  if(html.indexOf('src="/js/page-health.js"')>html.indexOf('src="/legend-public-web.js"'))throw new Error('Observer must load before app scripts.');
}
console.log('Shared observer copy, bounded route metadata, existing API base, and build identity checks passed.');
