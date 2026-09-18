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
