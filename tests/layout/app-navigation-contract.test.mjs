import test from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync, existsSync } from 'node:fs';
import { join, basename } from 'node:path';

const ROOT = new URL('../../', import.meta.url).pathname;

function walk(dir, suffix) {
  const out=[];
  for (const entry of readdirSync(dir,{withFileTypes:true})) {
    const path=join(dir,entry.name);
    if(entry.isDirectory()) out.push(...walk(path,suffix));
    else if(path.endsWith(suffix)) out.push(path);
  }
  return out;
}

function attrs(tag) {
  const map=new Map();
  for(const m of tag.matchAll(/([:@\w-]+)\s*=\s*"([^"]*)"/g)) map.set(m[1].toLowerCase(),m[2]);
  for(const m of tag.matchAll(/([:@\w-]+)\s*=\s*'([^']*)'/g)) map.set(m[1].toLowerCase(),m[2]);
  return map;
}

function controllerActions(appDir) {
  const controllers=join(ROOT,appDir,'Controllers');
  const index=new Map();
  if(!existsSync(controllers)) return index;
  for(const file of walk(controllers,'.cs')) {
    const src=readFileSync(file,'utf8');
    const classMatch=src.match(/class\s+(\w+)Controller\b/);
    if(!classMatch) continue;
    const name=classMatch[1];
    const actions=new Set();
    for(const m of src.matchAll(/(?:public|internal)\s+(?:async\s+)?(?:Task<\s*)?(?:IActionResult|ActionResult(?:<[^>]+>)?|Task|JsonResult|ContentResult|FileResult|RedirectResult)[^\n{]*?\s+(\w+)\s*\(/g)) actions.add(m[1]);
    for(const m of src.matchAll(/\[(?:HttpGet|HttpPost|HttpPut|HttpDelete|HttpPatch)\s*\([^\]]*Name\s*=\s*"([^"]+)"/g)) actions.add(m[1]);
    index.set(name,actions);
  }
  return index;
}

const apps=['AgentPortal','ClientApp'];

test('AgentPortal and ClientApp views contain no placeholder navigation targets',()=>{
  const bad=[];
  for(const app of apps) {
    for(const file of walk(join(ROOT,app,'Views'),'.cshtml')) {
      const src=readFileSync(file,'utf8');
      for(const m of src.matchAll(/<a\b[^>]*>/gi)) {
        const a=attrs(m[0]);
        const href=(a.get('href')||'').trim();
        if(href==='#' || /^javascript:\s*(?:void\s*\(\s*0\s*\)|;?)$/i.test(href)) bad.push(file+': '+m[0]);
        if(!href && !a.get('asp-controller') && !a.get('asp-action') && !a.get('asp-page') && !a.get('data-bs-toggle') && !a.get('data-bs-dismiss') && !a.get('role')) {
          bad.push(file+': anchor has no destination or explicit UI role: '+m[0]);
        }
      }
    }
  }
  assert.deepEqual(bad,[]);
});

test('Razor controller/action links resolve to real controller action source',()=>{
  const bad=[];
  for(const app of apps) {
    const index=controllerActions(app);
    for(const file of walk(join(ROOT,app,'Views'),'.cshtml')) {
      const src=readFileSync(file,'utf8');
      for(const m of src.matchAll(/<(?:a|form|button)\b[^>]*>/gi)) {
        const a=attrs(m[0]);
        const controller=a.get('asp-controller');
        const action=a.get('asp-action');
        if(!controller || !action) continue;
        // Razor expressions are runtime-owned and cannot be resolved statically.
        if(/[(@{]/.test(controller+action)) continue;
        if(!index.has(controller)) {
          bad.push(file+': missing '+controller+'Controller for '+m[0]);
          continue;
        }
        if(!index.get(controller).has(action)) bad.push(file+': missing '+controller+'Controller.'+action+' for '+m[0]);
      }
    }
  }
  assert.deepEqual(bad,[]);
});

test('type=button controls expose an explicit interaction hook',()=>{
  const bad=[];
  const hook=/^(?:id|onclick|data-[\w-]+|aria-controls|form)$/;
  for(const app of apps) {
    for(const file of walk(join(ROOT,app,'Views'),'.cshtml')) {
      const src=readFileSync(file,'utf8');
      for(const m of src.matchAll(/<button\b[^>]*>/gi)) {
        const a=attrs(m[0]);
        const type=(a.get('type')||'submit').toLowerCase();
        if(type!=='button') continue;
        if([...a.keys()].some(k=>hook.test(k))) continue;
        bad.push(file+': type=button has no route, id, data hook, onclick, form, or aria-controls: '+m[0]);
      }
    }
  }
  assert.deepEqual(bad,[]);
});

test('mobile shared authority keeps controls clickable and modal content reachable',()=>{
  const css=readFileSync(join(ROOT,'SHARED/wwwroot/css/dashboard-home-shared.css'),'utf8');
  const nav=readFileSync(join(ROOT,'SHARED/wwwroot/js/legend-global-navigation.js'),'utf8');
  const modal=readFileSync(join(ROOT,'SHARED/wwwroot/js/legend-modal.js'),'utf8');
  const mobile=css.slice(css.indexOf('@media (max-width: 900px)'));

  assert.doesNotMatch(mobile,/\[data-legend-modal-panel\][^}]*pointer-events:\s*none/);
  assert.match(mobile,/\.modal \.modal-body\s*\{[\s\S]*overflow-y:\s*auto/);
  assert.match(mobile,/\.legend-modal-close-control\s*\{[\s\S]*cursor:\s*pointer/);
  assert.match(nav,/panel\.addEventListener\('click'/);
  assert.match(modal,/button\.addEventListener\("click"/);
});
