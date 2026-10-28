import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
const source=fs.readFileSync(new URL('../../SHARED/WebsitePlatform/legend-public-cms.js',import.meta.url),'utf8');
const extract=(start,end)=>source.slice(source.indexOf(start),source.indexOf(end,source.indexOf(start)));
test('runtime-only recovery previews exact changes and posts explicit acceptance with immutable plan',async()=>{
  const nodes=[];
  const element=tag=>({tag,children:[],textContent:'',hidden:false,disabled:false,
    setAttribute(){},append(...items){this.children.push(...items)},remove(){this.removed=true},
    addEventListener(name,handler){this[name]=handler}});
  const document={createElement(tag){const node=element(tag);nodes.push(node);return node},body:{prepend(){}}};
  const plan={revision:68,sourceFingerprint:'original',repairedFingerprint:'repaired',changes:[],
    presentationChanges:[],runtimeAnchorChanges:[{pagePath:'/Quote/Auto',nodeId:'historical-id',previousType:'text',replacementType:'container',systemKey:'protect_runtime_form:quote_auto_form'}]};
  const calls=[];let loaded=0;
  const context=vm.createContext({document,URL,crypto:{randomUUID:()=> 'stable-recovery-id'},API_BASE:'https://example.test',
    editorTicket:'test-ticket',revealPublishedWebsite(){},loadEditor:async()=>{loaded++},
    fetch:async(url,options)=>{calls.push({url:String(url),options});return {ok:true,json:async()=>calls.length===1?plan:{revision:69}}}});
  vm.runInContext(extract('function showSavedDraftRecovery()', '  async function loadEditor()')+';showSavedDraftRecovery()',context);
  const button=nodes.find(n=>n.tag==='button');
  await button.click();
  assert.equal(calls.length,1);
  assert.equal(button.textContent,'Recover saved website');
  assert.ok(nodes.some(n=>n.tag==='pre'&&JSON.parse(n.textContent)[0].nodeId==='historical-id'));
  await button.click();
  const body=JSON.parse(calls[1].options.body);
  assert.deepEqual(body,{ticket:'test-ticket',expectedRevision:68,recoveryId:'stable-recovery-id',
    sourceFingerprint:'original',repairedFingerprint:'repaired',acceptRuntimeAnchorConversions:true,acceptPresentationConversions:false});
  assert.equal(loaded,1);
});
test('failed runtime recovery does not reload or automatically replay the mutation',async()=>{
  const nodes=[];const document={createElement(tag){const n={tag,append(){},setAttribute(){},addEventListener(k,f){this[k]=f}};nodes.push(n);return n},body:{prepend(){}}};
  let calls=0;
  const context=vm.createContext({document,URL,crypto:{randomUUID:()=> 'recovery'},API_BASE:'https://example.test',
    editorTicket:'test',revealPublishedWebsite(){},loadEditor(){throw Error('must not reload')},
    fetch:async()=>{calls++;if(calls===2)throw Error('ambiguous');return {ok:true,json:async()=>({revision:1,changes:[],runtimeAnchorChanges:[{}]})}}});
  vm.runInContext(extract('function showSavedDraftRecovery()', '  async function loadEditor()')+';showSavedDraftRecovery()',context);
  const button=nodes.find(n=>n.tag==='button');await button.click();await button.click();
  assert.equal(calls,2);assert.equal(button.hidden,true);
});
test('native form retains original text and controls when saved placeholder has flattened text',()=>{
  class Image {}
  let textWrites=0;
  const presentationCalls=[];
  const form={dataset:{cmsSignalOnly:'true'},tagName:'FORM',hasAttribute:()=>false};
  const model={type:'container',tag:'div',text:'historical flattened text'};
  const context=vm.createContext({SITE_KEY:'protect',pageState:()=>({systemTemplateKey:'protect_template:life_wizard'}),
    HTMLImageElement:Image,rememberOriginal:()=>({text:'native'}),
    setContentText(){textWrites++},applyDataBinding(){},applyCanonicalResponsiveAttributes(){},applyStyle(){},
    applyFormFieldPresentations(element,model){presentationCalls.push({element,model})},
    effectiveStyle(){},applyLayout(){},effectiveLayout(){},applyAnimations(){}});
  vm.runInContext(extract('function applyCompositionNode(', '  function buildLegacyExtraNode(')+';this.apply=applyCompositionNode',context);
  context.apply(form,model);
  assert.equal(textWrites,0);
  assert.equal(presentationCalls.length,1);
  assert.equal(presentationCalls[0].element,form);
  assert.equal(presentationCalls[0].model,model);
});
