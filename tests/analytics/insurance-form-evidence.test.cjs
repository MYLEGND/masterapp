const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync('Protect-Website/wwwroot/js/insurance-form-evidence.js','utf8');
test('captures actual false, zero, unanswered, repeated and unpresented fields without tracking or hidden tokens',()=>{
  const listeners = {};
  const make=(name,type,value,visible=true,checked=false)=>({name,type,value,checked,tagName:'INPUT',id:name,disabled:!visible,getClientRects:()=>visible?[{}]:[],getAttribute:()=>null,closest:()=>null});
  const controls=[make('Agree','checkbox','true',true,false),make('Amount','number','0'),make('Drivers[0].Name','text','Synthetic'),make('Optional','text',''),make('Branch','text','',false),make('__RequestVerificationToken','hidden','never-store')];
  const form={action:'https://synthetic.test/Quote/Auto',dataset:{},elements:controls,addEventListener:(key,fn)=>listeners[key]=fn,querySelector:selector=>selector.startsWith('label')?{textContent:'Original question'}:null};
  vm.runInNewContext(source,{document:{forms:[form]},location:{href:form.action},URL,Set,WeakSet,Array,JSON,CSS:{escape:x=>x},getComputedStyle:()=>({visibility:'visible'}),MutationObserver:class{observe(){}},requestAnimationFrame:fn=>fn()});
  const data = new Map();listeners.formdata({formData:data});
  const evidence=JSON.parse(data.get('HistoryPresentationJson'));
  assert.equal(evidence.fields.length,5);
  assert.equal(evidence.fields[0].answer,false);assert.equal(evidence.fields[0].state,'Unchecked');
  assert.equal(evidence.fields[1].answer,'0');assert.equal(evidence.fields[2].name,'Drivers[0].Name');
  assert.equal(evidence.fields[3].state,'Presented but unanswered');assert.match(evidence.fields[4].state,/Not presented/);
  assert.equal(evidence.fields[0].question,'Original question');
  assert.ok(!JSON.stringify(evidence).includes('never-store'));
});
test('radiogroup keeps question, submitted code and option label distinct',()=>{
  const listeners={};const group={getAttribute:name=>name==='aria-labelledby'?'who-question':null,querySelector:()=>null};
  const fields=['self','family'].map((value,index)=>({name:'ProtectingWho',type:'radio',value,checked:index===1,tagName:'INPUT',id:'',disabled:false,getClientRects:()=>[{}],getAttribute:name=>name==='data-label'?(index?'My family':'Myself'):null,closest:selector=>selector.includes('radiogroup')?group:selector==='label'?{textContent:index?'My family':'Myself'}:null}));
  const form={action:'https://synthetic.test/Quote/Life',dataset:{},elements:fields,addEventListener:(name,fn)=>listeners[name]=fn,querySelector:()=>null};
  vm.runInNewContext(source,{document:{forms:[form],getElementById:id=>id==='who-question'?{textContent:'Who are you protecting?'}:null},location:{href:form.action},URL,Set,WeakSet,Array,JSON,CSS:{escape:x=>x},getComputedStyle:()=>({visibility:'visible'}),MutationObserver:class{observe(){}},requestAnimationFrame:fn=>fn()});
  const data=new Map();listeners.formdata({formData:data});const evidence=JSON.parse(data.get('HistoryPresentationJson'));
  assert.equal(evidence.fields[1].question,'Who are you protecting?');assert.equal(evidence.fields[1].answer,'family');assert.equal(evidence.fields[1].optionLabel,'My family');assert.equal(evidence.fields[1].selected,true);
  assert.equal(evidence.fields[0].answer,null);assert.equal(evidence.fields[0].state,'Unchecked');assert.equal(evidence.fields[0].choiceValue,'self');
});
