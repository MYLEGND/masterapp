import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import vm from 'node:vm';
const script=readFileSync(new URL('../../SHARED/wwwroot/js/legend-modal.js',import.meta.url),'utf8');
function implementation(name) {
  const start=script.indexOf(`  function ${name}(`);
  assert(start>=0);
  const end=script.indexOf('\n  function ',start+1);
  return script.slice(start,end<0?undefined:end);
}
function fixture({height=900,top=200,footerTop=1000,contentTop=220,viewport,impersonation,mobile=false}={}) {
  const values=new Map();let writes=0;const attributes={};
  const element=(rect)=>({getBoundingClientRect:()=>rect});
  const context={window:{innerHeight:height,visualViewport:viewport,matchMedia:()=>({matches:mobile})},
    document:{documentElement:{style:{getPropertyValue:name=>values.get(name),setProperty:(name,value)=>{values.set(name,value);writes++;}},setAttribute:(key,value)=>attributes[key]=value}},
    header:element({top:0,bottom:top,height:top}),footer:element({top:footerTop,bottom:footerTop+60,height:60}),content:element({top:contentTop}),impersonation:impersonation?element(impersonation):null};
  vm.createContext(context);
  for(const name of ['writeVariable','syncViewportOffsets'])vm.runInContext(implementation(name),context);
  return {context,values,attributes,writes:()=>writes,refresh:()=>context.syncViewportOffsets(),number:key=>parseFloat(values.get(key))};
}
test('dialog region begins below banner/content and excludes visible footer',()=>{
  const f=fixture({footerTop:820});f.refresh();
  assert.equal(f.number('--legend-modal-area-start'),244);
  assert.equal(f.number('--legend-modal-safe-height'),552);
  assert.equal(f.number('--legend-modal-area-end'),104);
  assert.equal(f.number('--legend-modal-safe-center'),520);
});
test('offscreen footer never wastes visible workspace and scrolled header clears',()=>{
  const f=fixture({top:0,contentTop:-600,footerTop:1400});f.refresh();
  assert.equal(f.number('--legend-modal-area-start'),24);
  assert.equal(f.number('--legend-modal-safe-height'),852);
});
test('keyboard/visual viewport bounds clamp height without a 280px minimum',()=>{
  const f=fixture({height:900,top:290,contentTop:300,mobile:true,viewport:{offsetTop:100,height:240}});f.refresh();
  assert.equal(f.number('--legend-modal-area-start'),310);
  assert.equal(f.number('--legend-modal-safe-height'),20);
  assert.equal(f.number('--legend-modal-area-end'),570);
});
test('banner filling short viewport yields zero available height rather than overflow',()=>{
  const f=fixture({height:300,top:500,contentTop:520,mobile:true});f.refresh();
  assert.equal(f.number('--legend-modal-safe-height'),0);
  assert.equal(f.number('--legend-modal-area-start'),300);
});
test('sticky impersonation remains a boundary after global header scrolls out',()=>{
  const f=fixture({top:0,contentTop:-50,impersonation:{top:0,bottom:48,height:48}});f.refresh();
  assert.equal(f.number('--legend-modal-area-start'),72);
});
test('unchanged geometry does not trigger style writes and resize recalculates',()=>{
  const f=fixture();f.refresh();const writes=f.writes();f.refresh();assert.equal(f.writes(),writes);
  f.context.header={getBoundingClientRect:()=>({top:0,bottom:320,height:320})};f.refresh();assert.equal(f.number('--legend-modal-area-start'),344);
});
test('layout notifications coalesce into one scheduled geometry update',()=>{
  let scheduled,frames=0,updates=0;
  const context={viewportSyncFrame:0,window:{requestAnimationFrame:callback=>{frames++;scheduled=callback;return 1;}},syncViewportOffsets:()=>updates++};vm.createContext(context);vm.runInContext(implementation('scheduleViewportOffsets'),context);
  context.scheduleViewportOffsets();context.scheduleViewportOffsets();context.scheduleViewportOffsets();assert.equal(frames,1);scheduled();assert.equal(updates,1);assert.equal(context.viewportSyncFrame,0);
});
test('all three host layouts load the single shared owner before page scripts',()=>{
  for(const file of ['AgentPortal/Views/Shared/_Layout.cshtml','AgentPortal/Views/Shared/_ClientWorkspaceLayout.cshtml','ClientApp/Views/Shared/_Layout.cshtml']) {
    const source=readFileSync(new URL('../../'+file,import.meta.url),'utf8');
    assert.equal(source.split('~/_content/Shared/js/legend-modal.js').length-1,1,file);
    assert(source.indexOf('~/_content/Shared/js/legend-modal.js')<source.indexOf('RenderSectionAsync("Scripts"'),file);
  }
  assert.equal(existsSync(new URL('../../AgentPortal/wwwroot/js/legend-modal.js',import.meta.url)),false);
});
test('shared geometry preserves full backdrop and sizes inner Bootstrap/custom panels',()=>{
  const css=readFileSync(new URL('../../SHARED/wwwroot/css/dashboard-home-shared.css',import.meta.url),'utf8');
  assert.match(css,/\[data-legend-modal-panel\] \{\s+min-height: 0;\s+max-height: var\(--legend-modal-safe-height\)/);
  assert.match(css,/\.modal > \.modal-dialog-scrollable \{\s+height: var\(--legend-modal-safe-height\)/);
  assert.match(script,/new ResizeObserver\(scheduleViewportOffsets\)/);
  assert.match(script,/attributeFilter: \['class', 'hidden'\]/);
  assert.doesNotMatch(script,/attributeFilter:[^\n]*'style'/);
  assert.match(script,/visualViewport\?\.addEventListener\("resize"/);
  assert.match(script,/document.addEventListener\('show.bs.modal'/);
});

function dialogFixture() {
  const body={};
  const context={surfaces:new WeakSet(),document:{body},window:{getComputedStyle:node=>({position:node.position||'static'})}};
  vm.createContext(context);vm.runInContext(implementation('registerDialog'),context);
  const node=(classes='',parent=body,role=null,position='static')=>({nodeType:1,parentElement:parent,position,attributes:{},panels:[],contents:[],
    matches:selector=>selector==='.modal'&&classes.split(' ').includes('modal'),
    setAttribute(key,value){this.attributes[key]=value;},
    querySelectorAll(selector){return selector==='.modal-content'?this.contents:this.panels;}});
  return {context,node,register:node=>context.registerDialog(node)};
}
test('Bootstrap registration sizes descendants without moving nodes or altering focus semantics',()=>{
  const f=dialogFixture(),surface=f.node('modal'),dialog=f.node('modal-dialog',surface),content=f.node('modal-content',dialog);
  surface.panels=[dialog];surface.contents=[content];const parents=[dialog.parentElement,content.parentElement];f.register(surface);
  assert('data-legend-modal-surface' in surface.attributes);assert('data-legend-modal-panel' in dialog.attributes);assert('data-legend-modal-panel' in content.attributes);
  assert.equal(dialog.parentElement,parents[0]);assert.equal(content.parentElement,parents[1]);assert.equal(surface.attributes.role,undefined);
});
test('custom dialog registration places offset on the fixed overlay once, not its inner card',()=>{
  const f=dialogFixture(),overlay=f.node('custom-overlay',undefined,null,'fixed'),card=f.node('custom-card',overlay,'dialog');overlay.panels=[card];f.register(card);f.register(card);
  assert('data-legend-modal-surface' in overlay.attributes);assert('data-legend-modal-panel' in card.attributes);assert(!('data-legend-modal-surface' in card.attributes));
});
test('nested Bootstrap dialogs each retain one overlay region and their original parent relationship',()=>{
  const f=dialogFixture(),outer=f.node('modal'),panel=f.node('modal-dialog',outer),inner=f.node('modal',panel);outer.panels=[panel];
  f.register(outer);f.register(inner);assert('data-legend-modal-surface' in outer.attributes);assert('data-legend-modal-surface' in inner.attributes);assert.equal(inner.parentElement,panel);
});
