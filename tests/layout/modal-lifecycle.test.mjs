import { test } from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';
const source=readFileSync(new URL('../../SHARED/wwwroot/js/legend-modal.js',import.meta.url),'utf8');
function implementation(name){
  const start=source.indexOf(`  function ${name}(`);
  assert(start>=0,name);
  const end=source.indexOf('\n  function ',start+1);
  return source.slice(start,end);
}
function fixture(){
  const frames=[], surfaces=new Set(), focusState=new WeakMap(), layerState=new WeakMap();
  const document={activeElement:null,body:null};
  function element(name,parent=null){
    const attrs=new Map(),values=new Map();
    const node={name,nodeType:1,parentElement:parent,isConnected:true,disabled:false,hidden:false,tabIndex:0,
      css:{display:'block',visibility:'visible',position:'static',zIndex:'0'},
      getAttribute:n=>attrs.get(n)??null,hasAttribute:n=>attrs.has(n),setAttribute:(n,v)=>attrs.set(n,v),removeAttribute:n=>attrs.delete(n),
      style:{zIndex:'',getPropertyValue:n=>values.get(n)||'',setProperty:(n,v)=>values.set(n,v),removeProperty:n=>values.delete(n)},
      getClientRects:()=>node.rects===false?[]:[{}],
      contains:other=>{for(let n=other;n;n=n.parentElement)if(n===node)return true;return false;},
      closest:selector=>{for(let n=node;n;n=n.parentElement){if(selector==='[inert]'&&n.hasAttribute('inert'))return n;if(selector.includes('data-legend-modal-surface')&&surfaces.has(n))return n;}return null;},
      focus:()=>{document.activeElement=node;},
      querySelectorAll:()=>node.targets||[],querySelector:()=>node.panel||null};
    return node;
  }
  const body=element('body');document.body=body;document.activeElement=body;
  const context={document,surfaceList:surfaces,surfaceFocusState:focusState,surfaceLayerState:layerState,MODAL_LAYER_BASE:5200,MODAL_LAYER_STEP:20,
    window:{getComputedStyle:n=>({...n.css,zIndex:n.style.zIndex||n.css.zIndex}),requestAnimationFrame:f=>frames.push(f)},
    surfaceOpen:s=>s.isConnected&&s.open,requestSurfaceClose:s=>{s.open=false;context.endModalFocus(s);}};
  vm.createContext(context);
  for(const name of ['hasHiddenAncestor','setSurfaceLayer','topModalSurface','modalFocusEligible','modalFocusTargets','focusModalNode','focusModalSurface','prepareModalFocus','beginModalFocus','endModalFocus','handleModalKeyboard','captureSurfaceLayer','promoteSurfaceLayer','ensureSurfaceLayer','restoreSurfaceLayer'])vm.runInContext(implementation(name),context);
  function surface(name,z=5200,parent=body){const s=element(name,parent);s.open=true;s.css.zIndex=String(z);s.targets=[];surfaces.add(s);return s;}
  function control(name,s){const n=element(name,s);s.targets.push(n);return n;}
  function key(key,shiftKey=false){const e={key,shiftKey,preventDefault(){this.prevented=true;},stopImmediatePropagation(){this.stopped=true;}};context.handleModalKeyboard(e);return e;}
  return {context,document,body,element,surface,control,key,frames,flush(){while(frames.length)frames.shift()();},focusState};
}
test('opening focuses actual first control and close returns exact opener',()=>{
  const f=fixture(),opener=f.element('open',f.body),s=f.surface('modal'),first=f.control('first',s);f.document.activeElement=opener;
  f.context.beginModalFocus(s);f.flush();assert.equal(f.document.activeElement,first);
  s.open=false;f.context.endModalFocus(s);assert.equal(f.document.activeElement,opener);
});
test('Tab and reverse Tab wrap only the active surface',()=>{
  const f=fixture(),s=f.surface('modal'),a=f.control('a',s),b=f.control('b',s);
  f.document.activeElement=b;assert.equal(f.key('Tab').prevented,true);assert.equal(f.document.activeElement,a);
  assert.equal(f.key('Tab',true).prevented,true);assert.equal(f.document.activeElement,b);
  f.document.activeElement=a;assert.equal(f.key('Tab').prevented,undefined);
});
test('Tab from background moves into the top surface',()=>{
  const f=fixture(),s=f.surface('modal'),a=f.control('a',s);f.key('Tab');assert.equal(f.document.activeElement,a);
});
test('Escape closes only top child and restores its opener inside parent',()=>{
  const f=fixture(),parent=f.surface('parent'),opener=f.control('booking',parent),child=f.surface('child',5220),c=f.control('close',child);
  f.document.activeElement=opener;f.context.beginModalFocus(child);f.flush();assert.equal(f.document.activeElement,c);
  const e=f.key('Escape');assert(e.prevented&&e.stopped);assert.equal(parent.open,true);assert.equal(child.open,false);assert.equal(f.document.activeElement,opener);
});
test('Escape without a registered active surface is untouched',()=>{const f=fixture();assert.equal(f.key('Escape').prevented,undefined);});
test('focus excludes hidden disabled inert negative-tabindex and nested controls',()=>{
  const f=fixture(),s=f.surface('modal'),a=f.control('hidden',s),b=f.control('disabled',s),c=f.control('inert',s),d=f.control('negative',s),e=f.control('visible',s),child=f.surface('nested',5220,s),n=f.control('nested-control',child);
  a.hidden=true;b.disabled=true;c.setAttribute('inert','');d.tabIndex=-1;s.targets.push(n);
  assert.deepEqual(Array.from(f.context.modalFocusTargets(s)),[e]);
});
test('close/reopen cancels a previous scheduled focus',()=>{
  const f=fixture(),s=f.surface('modal'),a=f.control('a',s),opener=f.element('open',f.body);f.document.activeElement=opener;
  f.context.beginModalFocus(s);s.open=false;f.context.endModalFocus(s);s.open=true;f.context.beginModalFocus(s);
  f.frames.shift()();assert.equal(f.document.activeElement,opener);f.flush();assert.equal(f.document.activeElement,a);
});
test('detached surface cannot receive delayed focus',()=>{const f=fixture(),s=f.surface('modal');f.control('a',s);f.context.beginModalFocus(s);s.isConnected=false;f.flush();assert.equal(f.document.activeElement,f.body);});
test('removed opener falls back into the remaining parent',()=>{
  const f=fixture(),p=f.surface('parent'),a=f.control('parent-first',p),o=f.control('opener',p),c=f.surface('child',5220);f.control('child-first',c);f.document.activeElement=o;f.context.beginModalFocus(c);f.flush();o.isConnected=false;c.open=false;f.context.endModalFocus(c);assert.equal(f.document.activeElement,a);
});
test('empty dialog receives temporary tabindex without altering native controls',()=>{
  const f=fixture(),s=f.surface('modal');s.tabIndex=-1;f.context.beginModalFocus(s);f.flush();assert.equal(f.document.activeElement,s);assert.equal(s.getAttribute('tabindex'),'-1');s.open=false;f.context.endModalFocus(s);assert.equal(s.hasAttribute('tabindex'),false);
});
test('existing tabindex remains intact after empty dialog closes',()=>{
  const f=fixture(),s=f.surface('modal');s.setAttribute('tabindex','-1');f.context.beginModalFocus(s);f.flush();s.open=false;f.context.endModalFocus(s);assert.equal(s.getAttribute('tabindex'),'-1');
});
test('layer promotion and restore preserve original inline layer and variable',()=>{
  const f=fixture(),a=f.surface('a'),b=f.surface('b');b.style.zIndex='17';b.style.setProperty('--legend-modal-stack','19');f.context.promoteSurfaceLayer(b);assert.equal(b.style.zIndex,'5220');assert.equal(b.style.getPropertyValue('--legend-modal-stack'),'5220');f.context.restoreSurfaceLayer(b);assert.equal(b.style.zIndex,'17');assert.equal(b.style.getPropertyValue('--legend-modal-stack'),'19');
});
test('mobile structural CSS consumes the canonical dynamic layer',()=>{
  const css=readFileSync(new URL('../../SHARED/wwwroot/css/dashboard-home-shared.css',import.meta.url),'utf8');assert.match(css,/z-index: var\(--legend-modal-stack, 5200\) !important/);
  assert.equal((source.match(/document\.addEventListener\(['"]keydown['"]/g)||[]).length,1);
  assert.match(source,/document\.addEventListener\('keydown', handleModalKeyboard, true\)/);
});
test('existing feature Close is normalized once without generating another control',()=>{
  const f=fixture(),s=f.surface('modal'),button=f.control('close',s);let added=0,selectors='';
  button.classList={add(){}};button.dataset={};button.addEventListener=()=>added++;
  s.querySelectorAll=selector=>{selectors=selector;return [button];};
  f.context.surfaceOpen=surface=>surface.open;
  vm.runInContext(implementation('normalizeCloseControl'),f.context);
  f.context.normalizeCloseControl(s);f.context.normalizeCloseControl(s);
  assert.match(selectors,/button\[data-closemodal\]/);assert.equal(added,1);assert.equal(button.getAttribute('aria-label'),'Close');
});
test('parent close request never activates a nested child Close',()=>{
  const f=fixture(),p=f.surface('parent'),child=f.surface('child',5220,p),a=f.control('parent-close',p),b=f.control('child-close',child);let closed='';
  a.click=()=>closed='parent';b.click=()=>closed='child';p.querySelectorAll=()=>[b,a];f.context.window.setTimeout=()=>{};
  vm.runInContext(implementation('requestSurfaceClose'),f.context);f.context.requestSurfaceClose(p);assert.equal(closed,'parent');
});
test('stale Close fallback does not dismiss a newly reopened surface',()=>{
  const f=fixture(),s=f.surface('modal'),button=f.control('close',s),callbacks=[];let fallback=0;
  f.context.window.setTimeout=fn=>callbacks.push(fn);f.context.closeSurfaceFallback=()=>fallback++;
  f.context.beginModalFocus(s);s.querySelectorAll=()=>[button];
  button.click=()=>{s.open=false;f.context.endModalFocus(s);s.open=true;f.context.beginModalFocus(s);};
  vm.runInContext(implementation('requestSurfaceClose'),f.context);f.context.requestSurfaceClose(s);callbacks.forEach(fn=>fn());assert.equal(fallback,0);
});

test('native Bootstrap opener survives its own initial focus before shown event',()=>{
  const f=fixture(),s=f.surface('modal'),opener=f.element('open',f.body),a=f.control('a',s);
  f.document.activeElement=opener;f.context.prepareModalFocus(s);f.document.activeElement=a;
  f.context.beginModalFocus(s);f.flush();s.open=false;f.context.endModalFocus(s);assert.equal(f.document.activeElement,opener);
});
test('actual bound shown handler followed by shared shown handler preserves top layer after fade',()=>{
  const f=fixture(),parent=f.surface('parent'),booking=f.surface('booking',1115),handlers={};
  booking.dataset={};booking.addEventListener=(name,fn)=>handlers[name]=fn;
  f.body.classList={add(){}};f.context.ensureInBody=()=>booking;f.context.closeLegacyExecutionOverlays=()=>{};
  f.context.promoteBackdrop=()=>{};f.context.window.setTimeout=()=>{};f.context.registerDialogs=()=>{};f.context.syncModalSurfaceState=()=>{};
  vm.runInContext(implementation('bind'),f.context);f.context.bind('qvBookingModal',{modalZ:1115,backdropZ:1110});
  handlers['show.bs.modal']();f.context.promoteSurfaceLayer(booking);
  assert.equal(booking.style.zIndex,'5220');
  handlers['shown.bs.modal']();assert.equal(booking.style.zIndex,'1115');
  let shown;f.document.addEventListener=(name,fn)=>{assert.equal(name,'shown.bs.modal');shown=fn;};
  const start=source.indexOf("    document.addEventListener('shown.bs.modal', event => {");
  vm.runInContext(source.slice(start,source.indexOf('\n    });',start)+7),f.context);
  shown({target:booking});assert.equal(booking.style.zIndex,'5220');assert.equal(booking.style.getPropertyValue('--legend-modal-stack'),'5220');assert.equal(f.context.topModalSurface(),booking);
});

function sheetFixture(){
  const f=fixture(),sheet=f.element('sheet',f.body);
  sheet.dataset={legendSheetOpen:'false'};sheet.open=false;
  sheet.matches=selector=>selector==='[data-legend-mobile-sheet]';
  sheet.classList={contains:name=>name==='open'&&sheet.open};
  f.context.setMobileSheetSnap=()=>{};
  vm.runInContext(implementation('syncMobileSheetState'),f.context);
  return {...f,sheet,sync:()=>f.context.syncMobileSheetState(sheet)};
}
test('custom fallback hides without poisoning feature-owned reopen or authored aria',()=>{
  for(const aria of [null,'false','true']){
    const f=fixture(),s=f.surface('custom'),opener=f.element('opener',f.body),close=f.control('close',s);
    if(aria!==null)s.setAttribute('aria-hidden',aria);
    s.classList={remove(){s.open=false;},contains:name=>name==='open'&&s.open};s.matches=()=>false;s.dispatchEvent=()=>{};
    f.context.CustomEvent=function(){};f.context.syncModalSurfaceState=()=>{};
    vm.runInContext(implementation('closeSurfaceFallback'),f.context);
    vm.runInContext(implementation('surfaceOpen'),f.context);
    f.context.closeSurfaceFallback(s);
    assert.equal(s.hidden,true);assert.equal(s.getAttribute('aria-hidden'),aria);
    s.hidden=false;s.open=true;
    assert.equal(f.context.surfaceOpen(s),aria!=='true');
    if(aria!=='true'){
      f.document.activeElement=opener;f.context.beginModalFocus(s);f.flush();assert.equal(f.document.activeElement,close);
    }
  }
});
test('fallback preserves hidden aria and inert ancestor restrictions on reopen',()=>{
  for(const restriction of ['hidden','aria-hidden','inert']){
    const f=fixture(),parent=f.element('restricted',f.body),s=f.surface('custom',5200,parent),close=f.control('close',s);
    parent.setAttribute(restriction,restriction==='aria-hidden'?'true':'');
    s.matches=()=>false;s.classList={contains:()=>true};
    vm.runInContext(implementation('surfaceOpen'),f.context);
    assert.equal(f.context.focusModalNode(close),false);
    if(restriction!=='inert')assert.equal(f.context.surfaceOpen(s),false);
    assert.equal(parent.hasAttribute(restriction),true);
  }
});
test('fallback retains sheet aria contract and Bootstrap native delegation',()=>{
  const f=fixture(),sheet=f.element('sheet',f.body);sheet.matches=s=>s==='[data-legend-mobile-sheet]';
  sheet.classList={remove(){}};sheet.dispatchEvent=()=>{};f.context.CustomEvent=function(){};f.context.syncModalSurfaceState=()=>{};
  vm.runInContext(implementation('closeSurfaceFallback'),f.context);f.context.closeSurfaceFallback(sheet);
  assert.equal(sheet.getAttribute('aria-hidden'),'true');assert.equal(sheet.hidden,false);
  let hidden=0;const bootstrap=f.element('bootstrap',f.body);bootstrap.matches=s=>s==='.modal';
  f.context.window.bootstrap={Modal:{getOrCreateInstance:()=>({hide:()=>hidden++})}};
  f.context.closeSurfaceFallback(bootstrap);assert.equal(hidden,1);assert.equal(bootstrap.getAttribute('aria-hidden'),null);
});
test('sheet captures original opener once without visual modal registration',()=>{
  const f=sheetFixture(),opener=f.element('opener',f.body),inner=f.element('inner',f.sheet);
  f.document.activeElement=opener;f.sheet.open=true;f.sync();f.document.activeElement=inner;f.sync();
  assert.equal(f.focusState.get(f.sheet).opener,opener);
  assert.equal(f.context.surfaceList.has(f.sheet),false);assert.equal(f.sheet.hasAttribute('data-legend-modal-surface'),false);
  f.sheet.open=false;f.sync();assert.equal(f.document.activeElement,opener);assert.equal(f.focusState.has(f.sheet),false);
});
test('nested booking returns into sheet then outer close restores exact opener',()=>{
  const f=sheetFixture(),opener=f.element('opener',f.body),launch=f.element('booking launch',f.sheet);
  f.document.activeElement=opener;f.sheet.open=true;f.sync();f.document.activeElement=launch;
  const booking=f.surface('booking'),close=f.control('close',booking);f.context.beginModalFocus(booking);f.flush();assert.equal(f.document.activeElement,close);
  booking.open=false;f.context.endModalFocus(booking);assert.equal(f.document.activeElement,launch);
  f.sheet.open=false;f.sync();assert.equal(f.document.activeElement,opener);
});
test('sheet return respects another top dialog and disconnected hidden inert openers',()=>{
  for(const restriction of ['disconnected','hidden','inert','aria-hidden']){
    const f=sheetFixture(),opener=f.element('opener',f.body);f.document.activeElement=opener;f.sheet.open=true;f.sync();
    if(restriction==='disconnected')opener.isConnected=false;else opener.setAttribute(restriction,restriction==='aria-hidden'?'true':'');
    const inside=f.element('inside',f.sheet);f.document.activeElement=inside;f.sheet.open=false;f.sync();assert.equal(f.document.activeElement,inside);
  }
  const f=sheetFixture(),opener=f.element('opener',f.body);f.document.activeElement=opener;f.sheet.open=true;f.sync();
  const top=f.surface('other'),control=f.control('other control',top);f.sheet.open=false;f.sync();assert.equal(f.document.activeElement,control);
});
test('registered sheet and rapid repeated transitions never double restore or overwrite opener',()=>{
  const f=sheetFixture(),opener=f.element('opener',f.body);let returned=0;opener.focus=()=>{returned++;f.document.activeElement=opener;};
  f.document.activeElement=opener;f.sheet.open=true;f.context.prepareModalFocus(f.sheet);f.sync();
  f.sheet.open=false;f.context.endModalFocus(f.sheet);f.sync();assert.equal(returned,1);
  f.sheet.open=true;f.sync();const state=f.focusState.get(f.sheet);f.sync();assert.equal(f.focusState.get(f.sheet),state);
  f.sheet.open=false;f.sync();f.sheet.open=true;f.sync();assert.notEqual(f.focusState.get(f.sheet),state);
  assert.equal(f.focusState.get(f.sheet).opener,opener);assert.equal(returned,2);
});
