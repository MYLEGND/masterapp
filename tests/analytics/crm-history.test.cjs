const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const source=fs.readFileSync('AgentPortal/wwwroot/js/crm-history.js','utf8');
const settle=()=>new Promise(resolve=>setImmediate(resolve));
function harness({count=2,pageSize=10}={}){
  class Element{
    constructor(){this.children=[];this.dataset={};this.style={};this.listeners={};this.fields=new Map();this.elements={namedItem:n=>{if(!this.fields.has(n))this.fields.set(n,{value:n==='group'?'none':''});return this.fields.get(n);}};this.open=false;}
    addEventListener(n,f){(this.listeners[n]??=[]).push(f);}
    emit(n){for(const f of this.listeners[n]||[])f({preventDefault(){}});}
    append(...v){this.children.push(...v);}replaceChildren(){this.children=[];this.textContent='';}
    querySelector(){return {value:'synthetic',disabled:false};}showModal(){this.open=true;}close(){this.open=false;this.emit('close');}
  }
  const elements=new Map();const get=id=>{if(!elements.has(id))elements.set(id,new Element());return elements.get(id);};
  get('crm-history').dataset={kind:'lead',relationshipId:'synthetic',timezone:'UTC',draftScope:'synthetic'};
  const requests=new Map(),queries=[];
  const fetch=async url=>{
    queries.push(url);
    if(url.includes('/Entry/'))return new Promise((resolve,reject)=>requests.set(url.match(/Entry\/([^?]+)/)[1],{resolve:data=>resolve({ok:true,json:async()=>data}),reject}));
    return {ok:true,json:async()=>url.includes('/Entries?')?{entries:['A','B'].map(id=>({id,type:'FormSubmission',source:'WebsiteSubmission',status:'Filed',occurredDate:'2026-10-30'})),count,pageSize,snapshotUtc:'2026-10-30T00:00:00Z',activeHolds:[],openObligations:0,overdueObligations:0}:{}};
  };
  vm.runInNewContext(source,{document:{getElementById:get,createElement:()=>new Element()},window:{addEventListener(){}},location:{search:''},sessionStorage:{getItem(){return null;},setItem(){},removeItem(){}},crypto:{randomUUID:()=> 'synthetic'},URLSearchParams,URL,fetch,Intl,Date,setTimeout,FormData:class{*[Symbol.iterator](){}},console});
  return {get,requests,queries,click:index=>get('history-results').children[index].children[0].emit('click')};
}
const record=id=>({entry:{id,source:'WebsiteSubmission',submissionId:id,contentJson:JSON.stringify({account:id})},submission:{associationVersion:'version-'+id,snapshotJson:'{}'}});
test('closing A then opening B discards late A content and association identity',async()=>{
  const h=harness();await settle();h.click(0);h.get('history-close').onclick();h.click(1);
  h.requests.get('B').resolve(record('B'));await settle();
  const detail=h.get('history-detail-content'),before=detail.children.length;
  h.requests.get('A').resolve(record('A'));await settle();
  assert.equal(detail.children.length,before);
  assert.equal(h.get('history-association').elements.namedItem('submissionId').value,'B');
  assert.equal(h.get('history-association').dataset.expectedVersion,'version-B');
});
test('cancelled stale request error cannot overwrite a newly opened record',async()=>{
  const h=harness();await settle();h.click(0);h.get('history-detail').emit('cancel');h.get('history-detail').close();h.click(1);
  h.requests.get('B').resolve(record('B'));await settle();h.requests.get('A').reject(new Error('stale error'));await settle();
  assert.notEqual(h.get('history-detail-content').textContent,'stale error');
  assert.equal(h.get('history-association').dataset.expectedVersion,'version-B');
});
test('filed record dialog has an accessible heading',()=>{
  const html=fs.readFileSync('AgentPortal/Views/CrmHistory/Index.cshtml','utf8');
  assert.match(html,/<dialog[^>]*aria-labelledby="history-detail-title"/);assert.match(html,/<h2 id="history-detail-title">Filed record<\/h2>/);
});

test('startup diagnostics classify failures without leaking driver text or identifiers',()=>{
  const browser=fs.readFileSync('tests/analytics/crm-history-browser.cjs','utf8');
  const start=browser.indexOf('function startupDiagnostics('),end=browser.indexOf('(async()=>{',start);
  assert.ok(start>0 && end>start);
  const context={};vm.runInNewContext(browser.slice(start,end),context);
  const diagnostic=context.startupDiagnostics;
  const secret='https://private.invalid/token?secret=private /private/path SESSION_PRIVATE';
  const value=diagnostic('ChromeDriver started successfully on port 12345 '+secret,{exitCode:null,signalCode:null},8001);
  assert.equal(value.ready,true);assert.equal(value.elapsedMs,8001);assert.equal(value.exited,false);
  assert.equal(JSON.stringify(value).includes('private'),false);assert.equal(JSON.stringify(value).includes('12345'),false);
  for(const [message,key] of [['only supports Chrome version 1','versionMismatch'],['No usable sandbox','sandboxFailure'],['Resource temporarily unavailable','resourceFailure'],["DevToolsActivePort file doesn't exist",'chromeExited'],['session not created','sessionRejected']]){
    const result=diagnostic(message+secret,{exitCode:1,signalCode:'PRIVATE_SIGNAL'},Infinity);
    assert.equal(result[key],true);assert.equal(result.exitCode,1);assert.equal(result.signaled,true);
    assert.equal(result.elapsedMs,null);assert.equal(JSON.stringify(result).includes('PRIVATE'),false);
  }
  const unknown=diagnostic(secret,undefined,-1);
  assert.equal(unknown.ready,false);assert.equal(unknown.versionMismatch,false);assert.equal(unknown.elapsedMs,0);
  assert.equal(unknown.exitCode,null);assert.equal(unknown.exited,false);
  assert.ok(browser.includes('AbortSignal.timeout(8000)'));
  assert.ok(browser.includes('deadline=started+150000'));
});

test('record field grid preserves nested values, unknown evidence and literal untrusted text',async()=>{
  const h=harness();await settle();h.click(0);
  const payload=record('A');payload.entry.contentJson=JSON.stringify({account:'<img src=x onerror=alert(1)>',missing:null,empty:'',count:0,confirmed:false,nested:{original:'retained',items:['first','second']},narrative:'long narrative '.repeat(20)});
  h.requests.get('A').resolve(payload);await settle();
  const nodes=[];function visit(node){nodes.push(node);for(const child of node.children||[])visit(child);}visit(h.get('history-detail-content'));
  const text=nodes.map(n=>n.textContent);
  for(const value of ['<img src=x onerror=alert(1)>','Evidence unavailable','Empty submitted value','0','false','retained','first','second'])assert.ok(text.includes(value),value);
  assert.ok(nodes.some(n=>n.className==='history-fact-grid'));
  assert.ok(nodes.some(n=>n.className==='history-fact history-fact-wide'));
  assert.ok(nodes.every(n=>n.innerHTML===undefined));
});
test('category colors describe record kinds without asserting provider success',()=>{
  const start=source.indexOf('  function recordCategory('),end=source.indexOf('  function renderSubmission',start);
  const category=vm.runInNewContext('('+source.slice(start,end).trim()+')');
  for(const [entry,expected] of [[{type:'FormSubmission'},'Submission'],[{type:'Note'},'Interaction'],[{type:'FollowUp'},'Follow-up'],[{type:'TaskCompleted'},'Follow-up activity'],[{type:'TaskCanceled'},'Follow-up activity'],[{type:'Complaint'},'Complaint'],[{source:'ProtectedDocument'},'Evidence'],[{type:'Access'},'Record']])assert.equal(category(entry)[1],expected);
  assert.equal(category({type:'Unknown',status:'Succeeded',evidenceStatus:'Unavailable'})[1],'Record');
});


test('history requests bounded selected pages and uses server size for navigation',async()=>{
  const h=harness({count:26,pageSize:25});await settle();
  assert.ok(h.queries.some(q=>q.includes('pageSize=10')&&q.includes('activityView=all')));
  h.get('history-filters').elements.namedItem('pageSize').value='25';
  h.get('history-filters').elements.namedItem('activityView').value='case';
  h.get('history-filters').emit('submit');await settle();
  assert.ok(h.queries.some(q=>q.includes('pageSize=25')&&q.includes('activityView=case')));
  assert.equal(h.get('history-next').disabled,false);
  h.get('history-next').onclick();await settle();
  assert.equal(h.get('history-next').disabled,true);
  assert.ok(h.queries.some(q=>q.includes('page=1')&&q.includes('pageSize=25')));
});
