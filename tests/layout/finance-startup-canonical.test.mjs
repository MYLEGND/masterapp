import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
const src=readFileSync(new URL('../../SHARED/wwwroot/js/finance-tools.js',import.meta.url),'utf8');
const agent=readFileSync(new URL('../../AgentPortal/Views/Finance/Index.cshtml',import.meta.url),'utf8');
const client=readFileSync(new URL('../../ClientApp/Views/Finance/Index.cshtml',import.meta.url),'utf8');

function fixture({storageDenied=false,late=false}={}) {
 const listeners=new Map(), pending=[];
 const on=(key,fn)=>listeners.set(key,[...(listeners.get(key)||[]),fn]);
 const state=new Map(), all=new Map(), added=[];
 function create(id) {
  const node={id,style:{},dataset:{},classList:{add(){},remove(){},toggle(){}},
    isConnected:true,clientWidth:400,selectedIndex:0,value:'',options:[{value:'',textContent:'Open Another Tool'}],
    get innerHTML(){return this._html||''},set innerHTML(v){this._html=v},
    get textContent(){return this._text||''},set textContent(v){this._text=v},
    addEventListener(t,h){on(id+':'+t,h)},appendChild(v){if(id==='budgetDropdown'){this.options.push(v);added.push(v)}},
    setAttribute(){},blur(){},closest(){return null},getBoundingClientRect(){return{width:180}},
    querySelector(){return null},querySelectorAll(){return[]},
    dispatchEvent(e){for(const h of listeners.get(id+':'+e.type)||[]){const result=h.call(node,e);if(result?.then)pending.push(result);}return true}};
  return node;
 }
 const get=id=>all.get(id)||(all.set(id,create(id)),all.get(id));
 const root=get('financeRoot');root.dataset={financeApp:'client',financeScopeFallback:'client',clientUserId:'fixture',clientProfileId:'00000000-0000-0000-0000-000000000000',enableGrowthCalculator:'false',enableAdvancedWealthForecast:'false',enableClientPlanSearch:'false',enableDistributionPlanner:'false'};
 const document={readyState:late?'complete':'loading',visibilityState:'visible',body:get('body'),
  addEventListener(t,h){on('document:'+t,h)},querySelector(q){return ({'.finance-shell':get('finance-shell'),'.finance-tools-row':get('finance-tools-row'),'.finance-selector-row':get('finance-selector-row')})[q]||null},
  querySelectorAll(){return[]},getElementById:get,createElement:create};
 const localStorage={getItem(k){if(storageDenied)throw new Error('storage denied');return state.get(k)||null},setItem(k,v){if(storageDenied)throw new Error('storage denied');state.set(k,v)},removeItem(k){if(storageDenied)throw new Error('storage denied');state.delete(k)}};
 const window={addEventListener(){},LegendLivingBalanceSheetTool:{async render({host}){host.innerHTML='<section class="llbs-tool">LIVE TOOL</section>'}},getComputedStyle(){return{fontSize:'14px',fontWeight:'600',fontFamily:'Arial',fontStyle:'normal',letterSpacing:'0px',paddingLeft:'0px',paddingRight:'0px'}},ResizeObserver:null,MutationObserver:null};
 const fetch=async()=>({ok:true,json:async()=>({found:false})});
 const context={document,window,localStorage,performance:{getEntriesByType:()=>[{type:'navigate'}]},requestAnimationFrame:()=>{},Event:class{constructor(type){this.type=type}},fetch,console:{error(){},log(){}},setTimeout:()=>0,clearTimeout(){}};
 vm.runInNewContext(src,context,{timeout:1500});
 return {get,listeners,pending,added,async start(){for(const h of listeners.get('document:DOMContentLoaded')||[]){const p=h();if(p?.then)pending.push(p);}await Promise.allSettled(pending);await new Promise(resolve=>setImmediate(resolve));await Promise.allSettled(pending)}};
}
for(const path of [agent,client]) {
 if(!/id="budget-embed"[^>]*>.*Loading finance tools/.test(path)) throw Error('A Finance view lacks its initial accessible loading state');
}
test('shared Finance source is syntactically valid and both app views keep the same tool host',()=>{new Function(src);assert.match(agent,/id="budgetDropdown"/);assert.match(client,/id="budgetDropdown"/);});
test('initial Finance tool boot renders even when local browser storage is denied',async()=>{const f=fixture({storageDenied:true});await f.start();assert.ok(f.added.length>=9);assert.match(f.get('budget-embed').innerHTML,/llbs-tool/);});
test('Finance tool boot runs when the shared script arrives after DOMContentLoaded',async()=>{const f=fixture({late:true});await f.start();assert.ok(f.added.length>=9);assert.match(f.get('budget-embed').innerHTML,/llbs-tool/);});
test('removed dual tool control is not called from canonical rendering',()=>{assert.doesNotMatch(src,/\bsetDualToolMode\s*\(/);});
test('finance state and authority endpoints are preserved rather than reset on load',()=>{assert.match(src,/\/api\/finance-state\/load/);assert.match(src,/\/api\/finance-state\/save/);assert.match(src,/const selectedToolStateId = "__workspace__"/);assert.doesNotMatch(src,/localStorage\.clear\(/);});
