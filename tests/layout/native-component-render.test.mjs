import test from 'node:test';
import assert from 'node:assert/strict';
import {spawn, execFile} from 'node:child_process';
import {createServer} from 'node:http';
import {mkdtemp, readFile, appendFile, readdir, readlink} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {createHash} from 'node:crypto';
import vm from 'node:vm';
import {EventEmitter} from 'node:events';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const widths = [1180,1101,1200,944,901,900,841,840,590,393,320];
const hosts = ['authenticated','client','standalone'];
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

// No sockets or browser launch outside the explicitly admitted existing CI job.
function requireCI() {
  assert.equal(process.env.GITHUB_ACTIONS, 'true', 'Native rendering requires the admitted existing GitHub runner');
  assert.equal(process.env.RUNNER_OS, 'Linux');
  assert.equal(process.env.GITHUB_REPOSITORY, 'MYLEGND/masterapp');
  assert.ok(process.env.RUNNER_TEMP);
}

function allowedRequest(req, host, routes) {
  return req.method === 'GET' && req.headers.host === host && routes.has(req.url);
}

function requiresNavigation44(id, width) {
  return id === 'exploreTrigger' ? width > 840 : ['apNavToggle','clientNavToggle'].includes(id);
}

test('navigation target scope preserves desktop Explore and mobile toggle admission', () => {
  for (const width of [320,393,590,840]) assert.equal(requiresNavigation44('exploreTrigger',width),false);
  for (const width of [841,900,944,1180]) assert.equal(requiresNavigation44('exploreTrigger',width),true);
  for (const id of ['apNavToggle','clientNavToggle']) for (const width of [320,393,840]) assert.equal(requiresNavigation44(id,width),true);
  assert.equal(requiresNavigation44('unrelated',1180),false);
});

function registerCancellation(emitter, signal, cleanup, cancelled, exit) {
  const handlers=new Map(['SIGTERM','SIGINT'].map(name=>[name,()=>{cancelled(name);void cleanup().finally(()=>exit(name==='SIGTERM'?143:130));}]));
  for(const [name,handler] of handlers)emitter.once(name,handler);
  const abort=()=>{cancelled('test-abort');void cleanup();};
  signal.addEventListener('abort',abort,{once:true});
  return ()=>{for(const [name,handler] of handlers)emitter.removeListener(name,handler);signal.removeEventListener('abort',abort);};
}
test('cancellation invokes owned cleanup and removes signal handlers',async()=>{
  for(const event of ['SIGTERM','SIGINT','abort']){
    const emitter=new EventEmitter(),controller=new AbortController(),calls=[];
    const remove=registerCancellation(emitter,controller.signal,async()=>{calls.push('cleanup')},name=>calls.push(name),code=>calls.push(code));
    if(event==='abort')controller.abort();else emitter.emit(event);
    await new Promise(resolve=>setImmediate(resolve));
    assert.equal(calls.filter(x=>x==='cleanup').length,1);
    assert.equal(calls[0],event==='abort'?'test-abort':event);
    if(event!=='abort')assert.equal(calls.at(-1),event==='SIGTERM'?143:130);
    remove();assert.equal(emitter.listenerCount('SIGTERM'),0);assert.equal(emitter.listenerCount('SIGINT'),0);
  }
});

function requireCompleteScenarios(required, completed) {
  assert.deepEqual([...completed].sort(), [...required].sort(), 'Required native scenario inventory incomplete');
}
function loopbackListeners(table, inodes, port) {
  const rows=table.trim().split('\n').slice(1).map(row=>row.trim().split(/\s+/)).filter(row=>row[3]==='0A'&&inodes.has(row[9]));
  for(const row of rows) assert.ok(['0100007F','00000000000000000000000001000000'].includes(row[1].split(':')[0]),'Owned driver listener is not loopback');
  assert.ok(rows.some(row=>parseInt(row[1].split(':')[1],16)===Number(port)),'Owned driver listening port unproven');
  return rows.map(row=>({address:row[1],inode:row[9]}));
}
test('native scenario inventory refuses missing and extra completion IDs',()=>{
  requireCompleteScenarios(['a','b'],new Set(['a','b']));
  assert.throws(()=>requireCompleteScenarios(['a','b'],new Set(['a'])));
  assert.throws(()=>requireCompleteScenarios(['a'],new Set(['a','b'])));
});
test('owned listener proof rejects wildcard and missing driver socket',()=>{
  const row=address=>'header\n0: '+address+':1234 00000000:0000 0A 0 0 0 0 0 99';
  assert.equal(loopbackListeners(row('0100007F'),new Set(['99']),4660).length,1);
  assert.throws(()=>loopbackListeners(row('00000000'),new Set(['99']),4660));
  assert.throws(()=>loopbackListeners(row('0100007F'),new Set(['98']),4660));
});

function expectedCheckout(eventName,event,githubSha){
  assert.ok(['pull_request','push','workflow_dispatch'].includes(eventName),'Unsupported checkout event');
  const expected=eventName==='pull_request'?event.pull_request?.head?.sha:githubSha;
  assert.match(expected||'',/^[0-9a-f]{40}$/,'Expected checkout identity missing');
  if(eventName==='push')assert.equal(event.after,githubSha,'Push payload and workflow SHA differ');
  return expected;
}
async function setupCommand(binary,args,remaining,signal,limit=3000,options={},runner=(binary,args,options)=>new Promise(resolve=>{
  execFile(binary,args,options,(error,stdout,stderr)=>resolve({status:error?error.code:0,error,stdout,stderr}));
})){
  signal.throwIfAborted();const timeout=Math.min(limit,remaining());const started=Date.now();
  const result=await runner(binary,args,{...options,encoding:'utf8',timeout,signal,killSignal:'SIGKILL',maxBuffer:200000});
  signal.throwIfAborted();remaining();return {...result,timeout,elapsedMs:Date.now()-started};
}
test('checkout identity supports exact PR push and existing dispatch events',()=>{
  const a='a'.repeat(40),b='b'.repeat(40);
  assert.equal(expectedCheckout('pull_request',{pull_request:{head:{sha:a}}},b),a);
  assert.equal(expectedCheckout('push',{after:a},a),a);
  assert.equal(expectedCheckout('workflow_dispatch',{},a),a);
  assert.throws(()=>expectedCheckout('push',{after:b},a));
  assert.throws(()=>expectedCheckout('workflow_dispatch',{},undefined));
  assert.throws(()=>expectedCheckout('unknown',{},a));
});
test('setup subprocess budgets and cancellation fail closed before and after each probe',async()=>{
  const control=new AbortController();let calls=0,timeout;
  const runner=async(_binary,_args,options)=>{calls++;timeout=options.timeout;return {status:0,stdout:'ok'}};
  await setupCommand('mock',[],()=>25,control.signal,3000,{},runner);assert.equal(timeout,25);assert.equal(calls,1);
  control.abort();await assert.rejects(()=>setupCommand('mock',[],()=>25,control.signal,3000,{},runner));assert.equal(calls,1);
  const fresh=new AbortController();let checks=0;
  await assert.rejects(()=>setupCommand('mock',[],()=>{if(++checks>1)throw Error('late');return 20},fresh.signal,3000,{},runner),/late/);
  await assert.rejects(()=>setupCommand('mock',[],()=>{throw Error('exhausted')},fresh.signal,3000,{},runner),/exhausted/);
  assert.equal(calls,2);
});
async function binaryVersion(binary, remaining, signal, args = ['--version'], runner) {
  // Cold installed-binary probes may need more than the generic 3s setup cap.
  // This consumes the existing global budget; it neither retries nor extends it.
  const result = await setupCommand(binary,args,remaining,signal,10000,{},runner);
  const bounded=value=>String(value??'').slice(0,2048);
  const probe={binary:bounded(binary),args:args.slice(0,8).map(bounded),timeoutMs:result.timeout,
    elapsedMs:result.elapsedMs,status:result.status,errorCode:result.error?.code??null,
    signal:result.error?.signal??null,killed:result.error?.killed??false,
    stdout:bounded(result.stdout),stderr:bounded(result.stderr)};
  try {
    assert.equal(result.status, 0, `Version probe failed: ${JSON.stringify(probe)}`);
    const value = result.stdout.trim();
    assert.match(value, /\b\d+\.\d+\.\d+\.\d+\b/);
    return value;
  } catch(error) {error.versionProbe=probe;throw error;}
}

test('version probes use only their bounded global-clamped budget',async()=>{
  const control=new AbortController();const seen=[];
  const runner=async(_binary,_args,options)=>{seen.push(options);return {status:0,stdout:'Chrome 155.0.8059.39',stderr:''}};
  assert.equal(await binaryVersion('fixture',()=>20000,control.signal,undefined,runner),'Chrome 155.0.8059.39');
  await binaryVersion('fixture',()=>125,control.signal,undefined,runner);
  assert.deepEqual(seen.map(options=>options.timeout),[10000,125]);
  assert.ok(seen.every(options=>options.killSignal==='SIGKILL'&&options.signal===control.signal));
  control.abort();await assert.rejects(()=>binaryVersion('fixture',()=>20000,control.signal,undefined,runner));
  assert.equal(seen.length,2);
});
test('version probe failures preserve bounded signal and missing-binary diagnostics without retry',async()=>{
  for(const error of [{code:null,signal:'SIGKILL',killed:true},{code:'ENOENT',killed:false}]){
    const control=new AbortController();let calls=0;
    const runner=async()=>{calls++;return {status:error.code,error,stdout:'x'.repeat(3000),stderr:'y'.repeat(3000)}};
    await assert.rejects(()=>binaryVersion('fixture',()=>20000,control.signal,undefined,runner),failure=>{
      const probe=failure.versionProbe;
      assert.equal(probe.timeoutMs,10000);assert.ok(probe.elapsedMs>=0);
      assert.equal(probe.status,error.code);assert.equal(probe.errorCode,error.code);
      assert.equal(probe.signal,error.signal??null);assert.equal(probe.killed,error.killed);
      assert.equal(probe.stdout.length,2048);assert.equal(probe.stderr.length,2048);
      assert.deepEqual(probe.args,['--version']);assert.equal(probe.binary,'fixture');return true;
    });
    assert.equal(calls,1);
  }
});
test('version probes reject malformed success and cancellation or exhaustion after execution',async()=>{
  const control=new AbortController();let calls=0;
  const malformed=async()=>{calls++;return {status:0,stdout:'not a version',stderr:''}};
  await assert.rejects(()=>binaryVersion('fixture',()=>20000,control.signal,undefined,malformed),error=>{
    assert.equal(error.versionProbe.status,0);assert.equal(error.versionProbe.stdout,'not a version');return true;
  });
  let checks=0;
  await assert.rejects(()=>binaryVersion('fixture',()=>{if(++checks>1)throw Error('budget exhausted');return 20000},control.signal,undefined,malformed),/budget exhausted/);
  const aborting=async()=>{calls++;control.abort();return {status:0,stdout:'Chrome 155.0.8059.39'}};
  await assert.rejects(()=>binaryVersion('fixture',()=>20000,control.signal,undefined,aborting));
  assert.equal(calls,3);
});

// Pure contract tests can run without opening sockets or launching a browser.
test('added scenarios validate document errors before recording completion',async()=>{
  const source=await readFile(fileURLToPath(import.meta.url),'utf8');
  for(const name of ['locale-month-rollover','live-mobile-boundary','long-profile-and-offset','client-home-focus']){
    const check="assert.deepEqual(await execute('return window.__fixtureErrors'),[],'Fixture errors in "+name+"');";
    assert.ok(source.includes(check+"\n    completedScenarios.add('"+name+"');"));
  }
});

test('fixture origin permits only its exact Host, GET and fixed generated routes', () => {
  const routes = new Map([['/authenticated.html',Buffer.from('fixture')]]);
  const good = {method:'GET',headers:{host:'127.0.0.1:1234'},url:'/authenticated.html'};
  assert.equal(allowedRequest(good,'127.0.0.1:1234',routes),true);
  for (const change of [{method:'POST'},{url:'/../secret'},{url:'/%2e%2e/secret'},{url:'/authenticated.html?x=1'},{url:'/'},{headers:{host:'localhost:1234'}},{headers:{host:'evil.invalid'}}]) {
    assert.equal(allowedRequest({...good,...change},'127.0.0.1:1234',routes),false);
  }
});

test('synthetic transport accepts only finite recognized GET data and records denials', async () => {
  const builder=await readFile(path.join(root,'tests/layout/build-native-component-fixture.py'),'utf8');
  const source=builder.match(/safety = r'''<script>([\s\S]*?)<\/script>'''/);assert.ok(source,'Safety script extraction required');
  const context={URL,Response,location:{origin:'http://127.0.0.1:1234'},addEventListener(){},document:{addEventListener(){}}};context.window=context;
  vm.runInNewContext(source[1],context,{timeout:1000});
  assert.equal((await context.fetch('/mobile/agent/booking/appointments')).status,200);
  assert.equal((await context.fetch('/calendar/day-availability?date=2027-12-15&v=1')).status,200);
  for(const [url,options] of [['https://example.invalid/'],['/unrecognized'],['/calendar/day-availability?date=2027-12-15&extra=1'],['/calendar/day-availability?date=bad'],['/mobile/agent/booking/appointments',{method:'POST'}]])await assert.rejects(()=>context.fetch(url,options));
  assert.equal(context.__fixtureErrors.length,5);
  context.__fixtureCalls=Array(100).fill('synthetic');await assert.rejects(()=>context.fetch('/mobile/agent/booking/appointments'));
});

function commandRoute(endpoint) {
  const route=endpoint.replace(/^\/session\/[^/]+/, '/session/:sessionId').replace(/\/element\/[^/]+/, '/element/:elementId');
  return /^\/session(?:\/:sessionId(?:\/(?:timeouts|window\/rect|url|goog\/cdp\/execute|execute\/sync|element(?:\/:elementId\/click)?|actions))?)?$/.test(route)?route:'[unrecognized route]';
}
async function webdriverCommand({origin,method,endpoint,payload,remaining,ordinal},transport=fetch) {
  const started=Date.now();let stage='budget',budget=null,httpStatus=null,webdriverError=null;
  const token=value=>typeof value==='string'&&/^[A-Za-z][A-Za-z0-9_]{0,63}$/.test(value)?value:null;
  try {
    // Only the first exact session-creation request gets bounded cold-start time.
    const limit=ordinal===1&&method==='POST'&&endpoint==='/session'?20000:5000;
    budget=Math.min(limit,remaining());stage='fetch';
    const response=await transport(origin+endpoint,{method,headers:{'Content-Type':'application/json'},body:payload===undefined?undefined:JSON.stringify(payload),signal:AbortSignal.timeout(budget)});
    httpStatus=Number.isInteger(response.status)?response.status:null;stage='body';
    const text=await response.text();stage='global-budget';remaining();stage='parse';const data=JSON.parse(text);
    stage='status';
    const errors=new Set(['invalid session id','no such element','no such window','no such frame','unknown command','unknown error','session not created','timeout','script timeout','javascript error','invalid argument','element click intercepted','element not interactable','stale element reference','unsupported operation']);
    if(data.value?.error)webdriverError=errors.has(data.value.error)?data.value.error:'unrecognized error';
    assert.ok(response.ok&&!data.value?.error,'WebDriver response failed');return data.value;
  } catch(error) {
    const diagnostic={ordinal,method:['GET','POST','DELETE'].includes(method)?method:'[unrecognized method]',route:commandRoute(endpoint),stage,
      budgetMs:budget,elapsedMs:Date.now()-started,httpStatus,webdriverError,errorName:token(error.name),errorCode:typeof error.code==='number'?error.code:token(error.code)};
    // Never propagate response text, payloads, scripts, session IDs or error messages.
    const failure=new Error('WebDriver command failed: '+JSON.stringify(diagnostic));
    failure.name='WebDriverCommandError';failure.commandFailure=diagnostic;throw failure;
  }
}
test('WebDriver command success retains transport semantics and global-clamped budget',async()=>{
  const seen=[];let checks=0;
  const transport=async(url,options)=>{seen.push({url,options});return {status:200,ok:true,text:async()=>JSON.stringify({value:{result:'ok'}})}};
  const input={origin:'http://fixture',method:'POST',endpoint:'/session/private-id/url',payload:{url:'fixture-url'},remaining:()=>{checks++;return 125},ordinal:1};
  assert.deepEqual(await webdriverCommand(input,transport),{result:'ok'});
  assert.equal(seen[0].url,'http://fixture/session/private-id/url');assert.equal(seen[0].options.body,JSON.stringify(input.payload));
  assert.equal(seen[0].options.method,'POST');assert.ok(seen[0].options.signal instanceof AbortSignal);assert.equal(checks,2);
});
test('WebDriver command failures identify each stage with bounded redacted diagnostics and no retry',async()=>{
  const timeout=()=>Object.assign(new Error('private-id secret-payload'),{name:'TimeoutError',code:23});
  const response=(text,ok=true,status=200)=>({ok,status,text});
  const cases=[
    ['fetch',async()=>{throw timeout()}],
    ['body',async()=>response(async()=>{throw timeout()})],
    ['parse',async()=>response(async()=>'private-id secret-payload malformed')],
    ['status',async()=>response(async()=>JSON.stringify({value:{error:'session not created',message:'private-id secret-payload'}}),false,500)],
    ['global-budget',async()=>response(async()=>JSON.stringify({value:'ok'}))],
    ['budget',async()=>{throw Error('must not execute')}]
  ];
  for(const [stage,transport] of cases){let calls=0,checks=0;
    const input={origin:'http://fixture',method:'POST',endpoint:'/session/private-id/element/private-element/click',payload:{script:'secret-payload'},ordinal:7,
      remaining:()=>{checks++;if(stage==='budget'||(stage==='global-budget'&&checks===2))throw Error('secret-payload exhausted');return 125}};
    await assert.rejects(()=>webdriverCommand(input,async(...args)=>{calls++;return transport(...args)}),error=>{
      const d=error.commandFailure;assert.equal(d.stage,stage);assert.equal(d.ordinal,7);assert.equal(d.method,'POST');
      assert.equal(d.route,'/session/:sessionId/element/:elementId/click');assert.equal(d.budgetMs,stage==='budget'?null:125);assert.ok(d.elapsedMs>=0);
      assert.doesNotMatch(JSON.stringify(d)+error.stack,/private-id|private-element|secret-payload/);
      if(stage==='status'){assert.equal(d.httpStatus,500);assert.equal(d.webdriverError,'session not created');}
      if(stage==='fetch'||stage==='body'){assert.equal(d.errorName,'TimeoutError');assert.equal(d.errorCode,23);}
      return true;
    });assert.equal(calls,stage==='budget'?0:1);
  }
  assert.equal(commandRoute('/session/private-id/unknown?secret-payload'),'[unrecognized route]');
  assert.equal(commandRoute('/session'),'/session');
});
test('WebDriver command generic cap remains five seconds and untrusted error text is excluded',async()=>{
  const input={origin:'http://fixture',method:'POST',endpoint:'/session/existing/timeouts',remaining:()=>20000,ordinal:1};
  await assert.rejects(()=>webdriverCommand(input,async()=>{throw Object.assign(new Error('secret'),{name:'secret message',code:'secret message'})}),error=>{
    assert.equal(error.commandFailure.budgetMs,5000);assert.equal(error.commandFailure.errorName,null);assert.equal(error.commandFailure.errorCode,null);
    assert.doesNotMatch(error.stack,/secret/);return true;
  });
});

test('WebDriver session budget applies only to first exact POST session request',async()=>{
  const cases=[
    ['POST','/session',1,20000],['GET','/session',1,5000],['DELETE','/session',1,5000],
    ['post','/session',1,5000],['POST','/session/',1,5000],['POST','/session?extra=1',1,5000],
    ['POST','/session/existing/timeouts',1,5000],['POST','/session',2,5000],['POST','/session',0,5000]
  ];
  for(const [method,endpoint,ordinal,expected] of cases){let calls=0;
    await assert.rejects(()=>webdriverCommand({origin:'http://fixture',method,endpoint,ordinal,remaining:()=>30000},async()=>{
      calls++;throw Object.assign(new Error('fixture timeout'),{name:'TimeoutError',code:23});
    }),error=>{assert.equal(error.commandFailure.budgetMs,expected);assert.equal(error.commandFailure.stage,'fetch');return true});
    assert.equal(calls,1);
  }
});
test('WebDriver initial session budget remains globally clamped and timeout stays fatal without retry',async()=>{
  for(const remainingMs of [125,12000,30000]){let calls=0;
    await assert.rejects(()=>webdriverCommand({origin:'http://fixture',method:'POST',endpoint:'/session',ordinal:1,remaining:()=>remainingMs},async()=>{
      calls++;throw Object.assign(new Error('fixture timeout'),{name:'TimeoutError',code:23});
    }),error=>{
      const d=error.commandFailure;assert.equal(d.budgetMs,Math.min(20000,remainingMs));
      assert.equal(d.route,'/session');assert.equal(d.ordinal,1);assert.equal(d.errorName,'TimeoutError');
      assert.equal(d.errorCode,23);assert.equal(d.httpStatus,null);assert.equal(d.webdriverError,null);return true;
    });assert.equal(calls,1);
  }
});
test('WebDriver initial session success returns unchanged session response once',async()=>{
  const value={sessionId:'fixture-session',capabilities:{browserName:'chrome'}};let calls=0,checks=0;
  assert.deepEqual(await webdriverCommand({origin:'http://fixture',method:'POST',endpoint:'/session',ordinal:1,remaining:()=>{checks++;return 30000}},async()=>{
    calls++;return {ok:true,status:200,text:async()=>JSON.stringify({value})};
  }),value);assert.equal(calls,1);assert.equal(checks,2);
});

function computedRgb(text) {
  const number=token=>{
    assert.match(token,/^[-+]?(?:\d+\.?\d*|\.\d+)(?:e[-+]?\d+)?$/i,'Invalid computed color component');
    const value=Number(token);assert.ok(Number.isFinite(value),'Nonfinite computed color component');return value;
  };
  let components,alpha=1,scale=1;
  const legacy=text.match(/^rgba?\(([^)]+)\)$/);
  const srgb=text.match(/^color\(srgb\s+([^()]+)\)$/);
  if(legacy){
    const values=legacy[1].split(',').map(value=>number(value.trim()));
    assert.ok(values.length===3||values.length===4,'Invalid computed RGB component count');
    components=values.slice(0,3);if(values.length===4)alpha=values[3];
  } else if(srgb){
    const parts=srgb[1].split('/');assert.ok(parts.length<=2,'Invalid computed sRGB alpha');
    components=parts[0].trim().split(/\s+/).map(number);assert.equal(components.length,3,'Invalid computed sRGB component count');
    if(parts.length===2)alpha=number(parts[1].trim());scale=255;
  } else assert.fail('Unsupported computed color:'+text);
  assert.equal(alpha,1,'Nonopaque contrast base');
  const maximum=scale===255?1:255;
  assert.ok(components.every(value=>value>=0&&value<=maximum),'Computed color component out of range');
  return components.map(value=>value*scale);
}
const computedLuminance=c=>computedRgb(c).map(v=>{v/=255;return v<=.04045?v/12.92:((v+.055)/1.055)**2.4}).reduce((a,v,i)=>a+v*[.2126,.7152,.0722][i],0);
const computedContrast=(a,b)=>{const x=computedLuminance(a),y=computedLuminance(b);return (Math.max(x,y)+.05)/(Math.min(x,y)+.05);};
test('computed colors normalize measured CSS Color4 sRGB and retain opaque legacy forms',()=>{
  const measured=[0.0558431,0.153961,0.349804];
  assert.deepEqual(computedRgb('color(srgb 0.0558431 0.153961 0.349804)'),measured.map(value=>value*255));
  assert.deepEqual(computedRgb('color(srgb 0 .5 1 / 1)'),[0,127.5,255]);
  assert.deepEqual(computedRgb('rgb(17, 34, 51)'),[17,34,51]);
  assert.deepEqual(computedRgb('rgba(17, 34, 51, 1)'),[17,34,51]);
  assert.deepEqual(computedRgb('rgb(0.5, 127.5, 255)'),[0.5,127.5,255]);
});
test('computed contrast retains known black white luminance and ratio',()=>{
  assert.equal(computedLuminance('color(srgb 0 0 0)'),0);
  assert.equal(computedLuminance('color(srgb 1 1 1)'),1);
  assert.equal(computedContrast('color(srgb 0 0 0)','rgb(255, 255, 255)'),21);
  assert.equal(computedContrast('rgba(255, 255, 255, 1)','color(srgb 0 0 0 / 1)'),21);
  assert.equal(computedContrast('rgb(17, 34, 51)','rgb(17, 34, 51)'),1);
});
test('computed color parsing rejects nonopaque invalid out-of-range and unsupported spaces',()=>{
  for(const value of [
    'rgba(1,2,3,0.5)','color(srgb 0 0 0 / .5)','color(srgb 0 0 0 / 0)','color(srgb 0 0 0 / 2)',
    'rgb(256,0,0)','rgb(-1,0,0)','color(srgb 1.01 0 0)','color(srgb -0.01 0 0)',
    'rgb(NaN,0,0)','rgb(Infinity,0,0)','rgb(1e999,0,0)','color(srgb NaN 0 0)','color(srgb 1e999 0 0)',
    'rgb(1,,3)','rgb(1,2)','rgb(1,2,3,1,5)','color(srgb 0 0)','color(srgb 0 0 0 0)',
    'color(srgb 0 0 0 /)','color(srgb 0 0 0 / 1 / 1)','color(display-p3 0 0 0)','color(srgb-linear 0 0 0)',
    'lab(0 0 0)','transparent','#ffffff','color(srgb 0 0 nope)'
  ])assert.throws(()=>computedRgb(value),undefined,value);
});

function assertCriticalBookingChild(child) {
  const panel=child.selectionPanel;
  if(child.selector==='.qv-booking-selection-meta'&&panel?.exact===true&&panel.display==='none'&&panel.rects===0&&child.rects===0){
    assert.ok(child.w===0&&child.h===0&&child.client===0&&child.scroll===0,'Hidden legacy selection has a layout box:'+JSON.stringify(child));
    return 'authored-hidden-selection';
  }
  assert.ok(child.rects>0&&child.visibility!=='hidden'&&child.visibility!=='collapse'&&child.w>0&&child.h>0,'Critical child not visible:'+JSON.stringify(child));
  assert.ok(child.x>=child.body.x-1&&child.r<=child.body.r+1,'Critical child escapes body:'+JSON.stringify(child));
  assert.ok(child.scroll<=child.client+1,'Critical child text/track clipped:'+JSON.stringify(child));
  return 'visible';
}
test('critical booking geometry permits only the exact authored hidden legacy selection',()=>{
  const hidden={selector:'.qv-booking-selection-meta',rects:0,w:0,h:0,client:0,scroll:0,x:0,r:0,visibility:'visible',body:{x:26,r:1154},selectionPanel:{exact:true,display:'none',rects:0}};
  assert.equal(assertCriticalBookingChild(hidden),'authored-hidden-selection');
  for(const change of [{selector:'.qv-booking-slot-focus'},{selector:'#qvBookStatus'},{selectionPanel:null},{selectionPanel:{exact:false,display:'none',rects:0}},{selectionPanel:{exact:true,display:'block',rects:0}},{selectionPanel:{exact:true,display:'none',rects:1}},{w:1},{h:1},{client:1},{scroll:1}]){
    assert.throws(()=>assertCriticalBookingChild({...hidden,...change}));
  }
});
test('critical booking geometry requires every active child including visible legacy selection',()=>{
  const visible={rects:1,w:100,h:40,client:100,scroll:100,x:26,r:126,visibility:'visible',body:{x:26,r:1154},selectionPanel:{exact:true,display:'block',rects:1}};
  for(const selector of ['.qv-booking-slot-card','.qv-booking-slot-focus','.qv-booking-selection-meta','.qv-booking-foot','#qvBookStatus','#btnBookAppointment','#qvBookSlots button']){
    assert.equal(assertCriticalBookingChild({...visible,selector}),'visible');
    for(const change of [{rects:0},{w:0},{h:0},{visibility:'hidden'},{visibility:'collapse'},{x:20},{r:1160},{scroll:102}])assert.throws(()=>assertCriticalBookingChild({...visible,selector,...change}));
  }
});

function bookingScrollSnapshot(document,window) {
  const number=value=>Number.isFinite(value)?value:null;
  const css=value=>String(value??'').slice(0,256);
  const selectors={modal:'#qvBookingModal',dialog:'#qvBookingModal > .modal-dialog',content:'.qv-booking-content',header:'.qv-booking-head',body:'.qv-booking-body'};
  const nodes={};
  for(const [name,selector] of Object.entries(selectors)){
    const element=document.querySelector(selector);if(!element)throw Error('Missing scroll diagnostic node:'+name);
    const rect=element.getBoundingClientRect(),style=window.getComputedStyle(element),animations=element.getAnimations();
    nodes[name]={rect:{x:number(rect.x),y:number(rect.y),width:number(rect.width),height:number(rect.height),right:number(rect.right),bottom:number(rect.bottom)},
      scroll:{top:number(element.scrollTop),left:number(element.scrollLeft),height:number(element.scrollHeight),clientHeight:number(element.clientHeight)},
      style:{visibility:css(style.visibility),display:css(style.display),opacity:css(style.opacity),transform:css(style.transform),transitionProperty:css(style.transitionProperty),transitionDuration:css(style.transitionDuration),transitionDelay:css(style.transitionDelay),overflowY:css(style.overflowY),position:css(style.position)},
      animationCount:animations.length,animations:animations.slice(0,8).map(animation=>({
        playState:['idle','running','paused','finished'].includes(animation.playState)?animation.playState:'unknown',
        pending:animation.pending===true,currentTime:number(animation.currentTime),startTime:number(animation.startTime),
        duration:number(animation.effect?.getComputedTiming().duration)}))};
  }
  const scrolling=document.scrollingElement,viewport=window.visualViewport;
  return {nodes,page:{x:number(window.scrollX),y:number(window.scrollY),documentTop:number(scrolling?.scrollTop),documentLeft:number(scrolling?.scrollLeft)},
    viewport:{width:number(window.innerWidth),height:number(window.innerHeight),visualWidth:number(viewport?.width),visualHeight:number(viewport?.height),offsetTop:number(viewport?.offsetTop),offsetLeft:number(viewport?.offsetLeft)},
    shownCount:window.__shown.filter(id=>id==='qvBookingModal').length};
}
function assertStableBookingHeader(before,after) {
  assert.ok(Math.abs(after.head-before.head.y)<=1,'Header moved with body scrolling');
}
test('booking scroll diagnostics are bounded and exclude DOM text URLs and animation payloads',()=>{
  const animation={playState:'running',pending:true,currentTime:12,startTime:4,effect:{getComputedTiming:()=>({duration:300,secret:'private-payload'})},id:'private-payload'};
  const node={textContent:'private-payload',url:'https://private.invalid',getBoundingClientRect:()=>({x:1,y:2,width:3,height:4,right:4,bottom:6}),scrollTop:5,scrollLeft:0,scrollHeight:100,clientHeight:80,getAnimations:()=>Array(12).fill(animation)};
  const document={querySelector:()=>node,scrollingElement:{scrollTop:7,scrollLeft:0}};
  const window={getComputedStyle:()=>({transform:'none',transitionProperty:'x'.repeat(300),transitionDuration:'.3s',transitionDelay:'0s',overflowY:'auto',position:'relative'}),scrollX:0,scrollY:7,innerWidth:1101,innerHeight:757,visualViewport:{width:1101,height:757,offsetTop:0,offsetLeft:0},__shown:['qvBookingModal','another']};
  const value=bookingScrollSnapshot(document,window);
  assert.deepEqual(Object.keys(value.nodes),['modal','dialog','content','header','body']);
  assert.equal(value.nodes.header.rect.y,2);assert.equal(value.nodes.body.scroll.top,5);assert.equal(value.page.documentTop,7);assert.equal(value.viewport.width,1101);assert.equal(value.shownCount,1);
  for(const entry of Object.values(value.nodes)){assert.equal(entry.animationCount,12);assert.equal(entry.animations.length,8);assert.equal(entry.style.transitionProperty.length,256);assert.equal(entry.animations[0].duration,300);}
  assert.doesNotMatch(JSON.stringify(value),/private-payload|private\.invalid/);
  assert.throws(()=>bookingScrollSnapshot({...document,querySelector:()=>null},window),/Missing scroll diagnostic node/);
});
test('booking header stability keeps the exact one pixel absolute predicate',()=>{
  for(const [start,end] of [[25,25],[25,26],[25,24],[25,26.001],[25,23.999],[0,NaN]]){
    const before={head:{y:start}},after={head:end},passes=Math.abs(end-start)<=1;
    if(passes)assert.doesNotThrow(()=>assertStableBookingHeader(before,after));else assert.throws(()=>assertStableBookingHeader(before,after),/Header moved with body scrolling/);
  }
});

function bookingDialogSettled(document) {
  const dialog=document.querySelector('#qvBookingModal > .modal-dialog');
  if(!dialog)throw Error('Missing booking dialog readiness node');
  // Inspect only this dialog's own transitions/animations; never disable them.
  return dialog.getAnimations().every(animation=>animation.pending===false&&animation.playState==='finished');
}
test('booking dialog readiness waits for observed running transition to finish',()=>{
  const animation={pending:false,playState:'running',currentTime:266.718};
  const document={querySelector:selector=>{assert.equal(selector,'#qvBookingModal > .modal-dialog');return {getAnimations:()=>[animation]}}};
  assert.equal(bookingDialogSettled(document),false);
  animation.playState='finished';animation.currentTime=300;
  assert.equal(bookingDialogSettled(document),true);
});
test('booking dialog readiness rejects pending paused idle and missing state but accepts no animation',()=>{
  const document=animations=>({querySelector:()=>({getAnimations:()=>animations})});
  assert.equal(bookingDialogSettled(document([])),true);
  assert.equal(bookingDialogSettled(document([{pending:false,playState:'finished'}])),true);
  for(const animation of [{pending:true,playState:'finished'},{pending:true,playState:'running'},{pending:false,playState:'paused'},{pending:false,playState:'idle'},{playState:'finished'},{}]){
    assert.equal(bookingDialogSettled(document([animation])),false);
  }
  assert.equal(bookingDialogSettled(document([{pending:false,playState:'finished'},{pending:false,playState:'running'}])),false);
  assert.throws(()=>bookingDialogSettled({querySelector:()=>null}),/Missing booking dialog readiness node/);
});

function sheetControlSnapshot(document,window) {
  const css=value=>String(value??'').slice(0,256),number=value=>Number.isFinite(value)?value:null;
  const sheet=document.querySelector('#fixtureFocusSheet'),target=document.querySelector('#fixtureSheetBooking');
  const snapshot=node=>{if(!node)return null;const r=node.getBoundingClientRect(),style=window.getComputedStyle(node);
    return {rect:{x:number(r.x),y:number(r.y),width:number(r.width),height:number(r.height)},rectCount:node.getClientRects().length,
      style:Object.fromEntries(['display','visibility','opacity','pointerEvents','top','right','bottom','left','transform'].map(k=>[k,css(style[k])]))};};
  const control=snapshot(target),surface=snapshot(sheet),rect=control?.rect;
  const center=rect?{x:rect.x+rect.width/2,y:rect.y+rect.height/2}:null;
  const viewport={width:number(window.innerWidth),height:number(window.innerHeight)};
  const inViewport=!!center&&center.x>=0&&center.y>=0&&center.x<viewport.width&&center.y<viewport.height;
  const hit=inViewport?document.elementFromPoint(center.x,center.y):null;
  let ancestor=target,ancestorBlocked=false,count=0;
  while(ancestor&&count++<64){const style=window.getComputedStyle(ancestor);
    if(ancestor.hidden||ancestor.inert||ancestor.getAttribute('aria-hidden')==='true'||style.display==='none'||style.visibility!=='visible'||Number(style.opacity)<=0)ancestorBlocked=true;
    ancestor=ancestor.parentElement;}
  if(ancestor)ancestorBlocked=true;
  const animations=sheet?.getAnimations()??[];
  return {exists:!!sheet&&!!target,control,surface,viewport,center,inViewport,hitTarget:!!hit&&(hit===target||target.contains(hit)),
    disabled:target?target.disabled===true:null,ancestorBlocked,activeId:css(document.activeElement?.id),
    animationsSettled:animations.every(a=>a.pending===false&&a.playState==='finished'),
    animationCount:animations.length,animations:animations.slice(0,8).map(a=>({pending:a.pending===true,playState:css(a.playState)}))};
}
function sheetControlInteractable(value) {
  const r=value?.control?.rect,s=value?.control?.style;
  return value?.exists===true&&value.disabled===false&&value.ancestorBlocked===false&&value.animationsSettled===true&&
    value.control?.rectCount>0&&r&&[r.x,r.y,r.width,r.height].every(Number.isFinite)&&r.width>0&&r.height>0&&
    s?.display!=='none'&&s?.visibility==='visible'&&Number(s?.opacity)>0&&s?.pointerEvents!=='none'&&value.inViewport===true&&value.hitTarget===true;
}
test('sheet real-click readiness rejects offscreen blocked hidden disabled or moving controls',()=>{
  const good={exists:true,disabled:false,ancestorBlocked:false,animationsSettled:true,inViewport:true,hitTarget:true,
    control:{rectCount:1,rect:{x:100,y:50,width:100,height:40},style:{display:'block',visibility:'visible',opacity:'1',pointerEvents:'auto'}}};
  assert.equal(sheetControlInteractable(good),true);
  for(const [key,value]of [['exists',false],['disabled',true],['ancestorBlocked',true],['animationsSettled',false],['inViewport',false],['hitTarget',false]])assert.equal(sheetControlInteractable({...good,[key]:value}),false);
  for(const [key,value]of [['display','none'],['visibility','hidden'],['opacity','0'],['pointerEvents','none']])assert.equal(sheetControlInteractable({...good,control:{...good.control,style:{...good.control.style,[key]:value}}}),false);
  assert.equal(sheetControlInteractable({...good,control:{...good.control,rect:{...good.control.rect,width:0}}}),false);
  assert.equal(sheetControlInteractable(null),false);
});

function bookingLocaleClickSnapshot(document,window) {
  const number=value=>Number.isFinite(value)?value:null;
  const css=value=>String(value??'').slice(0,256);
  const target=document.querySelector('#qvBookNextMonth'),modal=document.querySelector('#qvBookingModal'),dialog=document.querySelector('#qvBookingModal > .modal-dialog');
  const rect=target?.getBoundingClientRect(),style=target?window.getComputedStyle(target):null;
  const animations=dialog?.getAnimations()??[];
  return {target:'#qvBookNextMonth',exists:!!target,rect:rect?{x:number(rect.x),y:number(rect.y),width:number(rect.width),height:number(rect.height)}:null,
    rectCount:target?target.getClientRects().length:0,disabled:target?target.disabled===true:null,
    style:style?{display:css(style.display),visibility:css(style.visibility),opacity:css(style.opacity),pointerEvents:css(style.pointerEvents)}:null,
    modal:{exists:!!modal,show:modal?.classList.contains('show')??false,fade:modal?.classList.contains('fade')??false,shownCount:(window.__shown??[]).filter(id=>id==='qvBookingModal').length},
    dialog:{exists:!!dialog,transform:dialog?css(window.getComputedStyle(dialog).transform):null,animationCount:animations.length,
      animations:animations.slice(0,8).map(animation=>({playState:['idle','running','paused','finished'].includes(animation.playState)?animation.playState:'unknown',pending:animation.pending===true,currentTime:number(animation.currentTime),duration:number(animation.effect?.getComputedTiming().duration)}))}};
}
test('booking locale diagnostics retain bounded control state without page content',()=>{
  const animation={playState:'running',pending:true,currentTime:2,effect:{getComputedTiming:()=>({duration:300})},id:'private-payload'};
  const node={disabled:true,textContent:'private-payload',getBoundingClientRect:()=>({x:1,y:2,width:46,height:46}),getClientRects:()=>[{}],classList:{contains:value=>value==='show'},getAnimations:()=>Array(12).fill(animation)};
  const document={querySelector:()=>node},window={__shown:['qvBookingModal'],getComputedStyle:()=>({display:'grid',visibility:'visible',opacity:'1',pointerEvents:'auto',transform:'x'.repeat(300)})};
  const value=bookingLocaleClickSnapshot(document,window);
  assert.equal(value.disabled,true);assert.equal(value.rect.width,46);assert.equal(value.modal.show,true);assert.equal(value.modal.fade,false);assert.equal(value.modal.shownCount,1);
  assert.equal(value.dialog.animationCount,12);assert.equal(value.dialog.animations.length,8);assert.equal(value.dialog.transform.length,256);assert.doesNotMatch(JSON.stringify(value),/private-payload/);
  const missing=bookingLocaleClickSnapshot({querySelector:()=>null},{});
  assert.equal(missing.exists,false);assert.equal(missing.rect,null);assert.equal(missing.modal.exists,false);assert.equal(missing.dialog.exists,false);
});

function assertMobileBookingPlacement(snapshot) {
  const viewport=snapshot?.viewport,modal=snapshot?.nodes?.modal?.rect,header=snapshot?.nodes?.header?.rect;
  assert.ok(viewport&&Number.isFinite(viewport.width)&&Number.isFinite(viewport.height)&&viewport.width>0&&viewport.height>0,'Missing or invalid mobile viewport');
  for(const [name,rect] of [['modal',modal],['header',header]]){
    const style=snapshot.nodes[name]?.style;
    assert.ok(style&&style.visibility==='visible'&&style.display&&style.display!=='none'&&style.opacity!==''&&Number.isFinite(Number(style.opacity))&&Number(style.opacity)>0&&Number(style.opacity)<=1,'Mobile '+name+' must be visibly painted');
    assert.ok(rect&&['x','y','width','height','right','bottom'].every(key=>Number.isFinite(rect[key]))&&rect.width>0&&rect.height>0,'Missing or invalid mobile '+name+' rectangle');
    assert.ok(Math.abs(rect.right-rect.x-rect.width)<=1&&Math.abs(rect.bottom-rect.y-rect.height)<=1,'Inconsistent mobile '+name+' rectangle');
  }
  assert.ok(Math.abs(modal.x)<=1&&Math.abs(modal.y)<=1&&Math.abs(modal.right-viewport.width)<=1&&Math.abs(modal.bottom-viewport.height)<=1,'Mobile modal must fill viewport');
  assert.ok(header.x>=Math.max(0,modal.x)-1&&header.y>=Math.max(0,modal.y)-1&&header.right<=Math.min(viewport.width,modal.right)+1&&header.bottom<=Math.min(viewport.height,modal.bottom)+1,'Booking header outside mobile modal or viewport');
}
test('mobile booking placement enforces fullscreen edges and positive contained header',()=>{
  const snapshot={viewport:{width:900,height:757},nodes:{modal:{rect:{x:0,y:0,width:900,height:757,right:900,bottom:757}},header:{rect:{x:0,y:0,width:900,height:100,right:900,bottom:100}}}};
  for(const node of Object.values(snapshot.nodes))node.style={visibility:'visible',display:'flex',opacity:'1'};
  assert.doesNotThrow(()=>assertMobileBookingPlacement(snapshot));
  const shifted=structuredClone(snapshot);Object.assign(shifted.nodes.modal.rect,{x:1,y:1,right:901,bottom:758});assert.doesNotThrow(()=>assertMobileBookingPlacement(shifted));
  for(const [node,change] of [['modal',{x:2,width:898}],['modal',{y:2,height:755}],['modal',{right:898,width:898}],['modal',{bottom:755,height:755}],['header',{x:-2,width:902}],['header',{y:-2,height:102}],['header',{right:902,width:902}],['header',{bottom:759,height:759}],['header',{width:0}],['header',{height:0}],['header',{y:NaN}]]){
    const invalid=structuredClone(snapshot);Object.assign(invalid.nodes[node].rect,change);assert.throws(()=>assertMobileBookingPlacement(invalid),undefined,JSON.stringify({node,change}));
  }
  for(const name of ['modal','header'])for(const style of [{visibility:'hidden'},{visibility:'collapse'},{display:'none'},{opacity:'0'},{opacity:''},{opacity:'NaN'},null]){
    const invalid=structuredClone(snapshot);invalid.nodes[name].style=style===null?null:{...invalid.nodes[name].style,...style};assert.throws(()=>assertMobileBookingPlacement(invalid));
  }
  for(const invalid of [null,{}, {...snapshot,viewport:{width:0,height:757}}, {...snapshot,nodes:{}}, {...snapshot,nodes:{modal:snapshot.nodes.modal}}])assert.throws(()=>assertMobileBookingPlacement(invalid));
});

function homeFocusOpenReady(document, window) {
  const hub=document.querySelector('#homeFocusHub'),close=document.querySelector('#homeFocusClose');
  if(!hub||!close||hub.hidden||!hub.classList.contains('open')||hub.closest('[hidden],[inert],[aria-hidden="true"]'))return false;
  const style=window.getComputedStyle(hub);
  return style.display!=='none'&&style.visibility==='visible'&&Number(style.opacity)>0&&
    hub.getClientRects().length>0&&close.getClientRects().length>0&&document.activeElement===close;
}
test('every repeated Home open requires accessible visible surface and exact entry focus',()=>{
  const close={getClientRects:()=>[{}]},hub={hidden:false,classList:{contains:()=>true},closest:()=>null,getClientRects:()=>[{}]};
  const document={querySelector:selector=>selector==='#homeFocusHub'?hub:close,activeElement:close};
  const style={display:'block',visibility:'visible',opacity:'1'},window={getComputedStyle:()=>style};
  assert.equal(homeFocusOpenReady(document,window),true);
  for(const blocked of ['hidden','inert','aria-hidden']){hub.closest=()=>({blocked});assert.equal(homeFocusOpenReady(document,window),false);}
  hub.closest=()=>null;document.activeElement={};assert.equal(homeFocusOpenReady(document,window),false);document.activeElement=close;
  for(const [key,value] of [['display','none'],['visibility','hidden'],['opacity','0']]){const before=style[key];style[key]=value;assert.equal(homeFocusOpenReady(document,window),false);style[key]=before;}
  hub.getClientRects=()=>[];assert.equal(homeFocusOpenReady(document,window),false);
});

test('quote source projection retains exact cold mounts and rejects unknown Razor branches',async()=>{
  const result=await new Promise(resolve=>execFile('python3',['-c',String.raw`
import importlib.util,re,sys
from pathlib import Path
root=Path(sys.argv[1]);spec=importlib.util.spec_from_file_location('fixture',root/'tests/layout/build-native-component-fixture.py');m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
for view in ['Life','Disability','DentalVisionHearing']:
 text=(root/('Protect-Website/Views/Quote/'+view+'.cshtml')).read_text()
 anchor='<div class="lq-hero-shell" data-protect-form-shell>' if view=='Life' else '<div class="dq-shell" data-protect-form-shell>'
 assert text.count(anchor)==1
 start=text.index(anchor);end=text.index('<script',start) if view=='Life' else text.index('@section Scripts',start)
 values,conditions=m.quote_projection_values();markup=m.quote_razor_projection(text[start:end],values,conditions)
 assert len(re.findall(r'data-cms-native-step="[0-9]+"',markup))==5
 assert '<figure' in markup and '<svg' in markup and '<img' in markup and '<form' in markup
 if view=='Life':
  assert len(re.findall(r'\sid="lifeStep2EstimateSlot"',markup))==1 and 'id="lifeStep2EstimateSummary"' not in markup
  assert 'data-cms-progress-label="Who depends on this"' in markup and 'data-cms-progress-label="What matters most"' in markup
 assert m.quote_razor_projection('@if (isLandingView) {first} else if (usesContactFirstShell) {second} else {third}',values,conditions).strip()=='first'
 assert 'else if (' not in markup
 for bad in ['@if (unknownBranch) {<span>bad</span>}','@(unknownExpression)','@unknownModelValue','@foreach (var x in unknownOptions) {<span>bad</span>}','@{ var unknown = 1; }']:
  try:m.quote_razor_projection(bad,values,conditions)
  except ValueError:pass
  else:raise AssertionError('Unknown Razor accepted: '+bad)
print('3 canonical regions;15 unknown-source rejection checks PASS;no browser/assets/Razor execution')
`,root],{timeout:5000,maxBuffer:4096},(error,stdout,stderr)=>resolve({error,stdout,stderr})));
  assert.equal(result.error,null,result.stderr||result.error?.message);assert.match(result.stdout,/15 unknown-source rejection checks PASS/);
});

// Quote checks are source-component geometry, not full Razor route or browser-zoom acceptance.
function bookingHiddenComplete(state, hiddenBefore) {
  return !!state && state.sameNode===true && Number.isSafeInteger(hiddenBefore) && hiddenBefore>=0 && Number.isSafeInteger(state.hidden) && state.hidden>hiddenBefore && state.show===false && state.activeId==='fixtureSheetBooking';
}

function bookingLifecycleSnapshot(document,window) {
  const node=document.getElementById('qvBookingModal'),sequence=window.__bookingLifecycle;
  if(!node)return null;
  const style=window.getComputedStyle(node),instance=window.bootstrap?.Modal.getInstance(node);
  return {sameNode:sequence?.node===node,shown:sequence?.shown,hidden:sequence?.hidden,show:node.classList.contains('show'),ariaHidden:node.getAttribute('aria-hidden'),display:style.display,activeId:document.activeElement?.id||'',transitioning:instance?._isTransitioning??null,animations:node.getAnimations({subtree:true}).slice(0,8).map(a=>({playState:a.playState,pending:a.pending}))};
}

test('booking close requires a fresh same-node hidden completion, not class removal or restored focus',()=>{
  const good={sameNode:true,hidden:3,show:false,activeId:'fixtureSheetBooking'};
  assert.equal(bookingHiddenComplete(good,2),true);
  for(const bad of [null,{...good,hidden:0},{...good,hidden:2},{...good,sameNode:false},{...good,show:true},{...good,activeId:'elsewhere'}])assert.equal(bookingHiddenComplete(bad,2),false);
  assert.equal(bookingHiddenComplete(good,-1),false);
});

function quoteGeometryValid(value, {desktop = true, expectedWidth = null, sidebar = false} = {}) {
  const rect = r => r && ['x','y','width','height','right','bottom'].every(k=>Number.isFinite(r[k])) && r.width>0 && r.height>0;
  if (!value || !rect(value.shell) || !rect(value.canvas) || value.style?.display==='none' || value.style?.visibility!=='visible' || (!Number.isFinite(Number(value.style?.opacity))||Number(value.style?.opacity)<=0)) return false;
  const s=value.shell,c=value.canvas;
  if (s.x<c.x-1 || s.right>c.right+1 || value.overflow>1) return false;
  if (desktop && (Math.abs(s.width-(expectedWidth??c.width*.75))>1 || Math.abs((s.x+s.right)-(c.x+c.right))>2)) return false;
  if (sidebar || value.editing) {
    const usableRight=sidebar?value.panel?.x:value.viewport?.width;
    if (!sidebar && value.panelHidden!==true) return false;
    if (!Number.isFinite(usableRight) || !Number.isFinite(value.viewport?.width) || usableRight>value.viewport.width+1 || !rect(value.available) || !rect(value.canvasOuter)) return false;
    if (Math.abs(value.available.x)>1 || Math.abs(value.available.right-usableRight)>1 || c.x<value.available.x-1 || c.right>value.available.right+1 || value.canvasOuter.x<value.available.x-1 || value.canvasOuter.right>value.available.right+1 || Math.abs((value.canvasOuter.x+value.canvasOuter.right)-(value.available.x+value.available.right))>2) return false;
    if (!['','1440px','1550px'].includes(value.layout?.maxWidth) || Math.abs(value.canvasOuter.width-Math.min(value.available.width,parseFloat(value.layout.maxWidth)||value.available.width))>1) return false;
  }
  if (sidebar && (!rect(value.panel) || (s.right>value.panel.x+1 && s.x<value.panel.right-1))) return false;
  return true;
}

test('quote geometry rejects missing, clipped, hidden, wrong-width and overlapping source components',()=>{
  const good={shell:{x:150,y:18,width:900,height:400,right:1050,bottom:418},canvas:{x:0,y:0,width:1200,height:900,right:1200,bottom:900},panel:{x:1200,y:0,width:384,height:900,right:1584,bottom:900},style:{display:'block',visibility:'visible',opacity:'1'},canvasOuter:{x:0,y:0,width:1200,height:900,right:1200,bottom:900},available:{x:0,y:0,width:1200,height:900,right:1200,bottom:900},layout:{maxWidth:''},viewport:{width:1584,height:900},overflow:0};
  assert.equal(quoteGeometryValid(good,{sidebar:true}),true);
  for(const change of [{shell:null},{overflow:2},{style:{...good.style,visibility:'hidden'}},{shell:{...good.shell,width:899-1}},{panel:{...good.panel,x:1000}},{canvas:{...good.canvas,x:-2}},{canvas:{...good.canvas,right:1300,width:1300}},{available:null},{available:{...good.available,right:1300,width:1300}},{canvasOuter:{...good.canvasOuter,x:10,width:1190}},{canvasOuter:null},{layout:{maxWidth:'900px'}},{available:{...good.available,x:2}},{available:{...good.available,right:1198,width:1198}}])assert.equal(quoteGeometryValid({...good,...change},{sidebar:true}),false);
  assert.equal(quoteGeometryValid({...good,shell:{...good.shell,x:240,width:720,right:960}},{expectedWidth:720,sidebar:true}),true);
  const hidden={...good,editing:true,panelHidden:true,viewport:{width:1200,height:900}};
  assert.equal(quoteGeometryValid(hidden),true);
  assert.equal(quoteGeometryValid({...hidden,viewport:{width:1584,height:900}}),false);
  assert.equal(quoteGeometryValid({...hidden,panelHidden:false}),false);
  const capped={...good,layout:{maxWidth:'1440px'},viewport:{width:1920,height:900},panel:{...good.panel,x:1536,right:1920},available:{...good.available,width:1536,right:1536},canvasOuter:{...good.canvasOuter,x:48,width:1440,right:1488},canvas:{...good.canvas,x:48,width:1425,right:1473},shell:{...good.shell,x:226.125,width:1068.75,right:1294.875}};
  assert.equal(quoteGeometryValid(capped,{sidebar:true}),true,'Classic scrollbar client box need not share outer preview center');
  assert.equal(quoteGeometryValid({...capped,canvasOuter:{...capped.canvasOuter,x:240,right:1680}},{sidebar:true}),false,'Whole-window-centered preview overlaps reserved sidebar');
  const narrow={...good,viewport:{width:1214,height:900},panel:{...good.panel,x:830,right:1214},available:{...good.available,width:830,right:830},canvasOuter:{...good.canvasOuter,width:830,right:830},canvas:{...good.canvas,width:830,right:830},shell:{...good.shell,x:16,width:798,right:814},layout:{maxWidth:'1440px'}};
  assert.equal(quoteGeometryValid(narrow,{sidebar:true,desktop:false}),true,'Named preview clamps to real available canvas');
});

function headerRegionSnapshot(document,window) {
  const header=document.querySelector('body>header'),content=document.querySelector('.layout-content');
  if(!header||!content)return null;
  const h=header.getBoundingClientRect(),c=content.getBoundingClientRect(),style=window.getComputedStyle(document.documentElement);
  return {clearance:parseFloat(style.getPropertyValue('--legend-modal-clearance-top')),area:parseFloat(style.getPropertyValue('--legend-modal-area-start')),content:c.top,header:h.bottom,height:h.height};
}

function headerRegionSettled(previous,current,before=null) {
  const coherent=value=>value&&['clearance','area','content','header','height'].every(key=>Number.isFinite(value[key]))&&
    value.clearance>=value.header-1&&value.area>=value.clearance;
  return !!(coherent(previous)&&coherent(current)&&JSON.stringify(previous)===JSON.stringify(current)&&
    (!before||current.content>=before.content+39));
}

test('header geometry observation waits for the shipped queued resize update without refreshing it',async()=>{
  const source=await readFile(path.join(root,'SHARED/wwwroot/js/legend-modal.js'),'utf8');
  const implementation=name=>{const a=source.indexOf('  function '+name+'('),b=source.indexOf('\n  function ',a+1);assert.ok(a>=0&&b>a);return source.slice(a,b);};
  const queue=[],values=new Map();let height=100;
  const header={getBoundingClientRect:()=>({top:0,bottom:height,height})},content={getBoundingClientRect:()=>({top:height})};
  const style={getPropertyValue:key=>values.get(key),setProperty:(key,value)=>values.set(key,value)};
  const document={documentElement:{style,setAttribute:()=>{}},querySelector:selector=>selector==='body>header'?header:selector==='.layout-content'?content:null,querySelectorAll:()=>[]};
  const window={innerHeight:757,matchMedia:()=>({matches:true}),getComputedStyle:()=>style,requestAnimationFrame:callback=>{queue.push(callback);return queue.length;}};
  const context={document,window,header,content,footer:null,impersonation:null,viewportSyncFrame:0};
  vm.createContext(context);
  for(const name of ['writeVariable','syncMobileSheetViewport','syncViewportOffsets','scheduleViewportOffsets'])vm.runInContext(implementation(name),context);
  context.syncViewportOffsets();
  height=140;context.scheduleViewportOffsets();const unreadyBaseline=headerRegionSnapshot(document,window);
  assert.equal(headerRegionSettled(unreadyBaseline,unreadyBaseline),false,'Profile reflow may precede its observer frame');
  queue.shift()();const baseline=headerRegionSnapshot(document,window);assert.equal(headerRegionSettled(baseline,baseline),true);
  height=180;context.scheduleViewportOffsets();const stale=headerRegionSnapshot(document,window);
  assert.ok(stale.clearance>unreadyBaseline.clearance,'The old increased-clearance predicate can pass an earlier update');
  assert.equal(headerRegionSettled(stale,stale,baseline),false,'Stable stale tokens cannot establish header clearance');
  queue.shift()();const updated=headerRegionSnapshot(document,window);
  assert.equal(headerRegionSettled(stale,updated,baseline),false);assert.equal(headerRegionSettled(updated,headerRegionSnapshot(document,window),baseline),true);
  assert.equal(headerRegionSettled(updated,{...updated,area:updated.clearance-1},baseline),false);
  assert.equal(headerRegionSettled(updated,{...updated,content:baseline.content},baseline),false);
  assert.equal(headerRegionSettled(null,updated),false);
  assert.equal(headerRegionSettled({...updated,height:NaN},{...updated,height:NaN}),false);
});

function quoteCanvasSettled(previous,current) {
  const aligned=value=>value?.selected && value?.frame && !value.frame.hidden &&
    ['left','top','width','height'].every(k=>Number.isFinite(value.selected[k]) && Number.isFinite(value.frame[k]) && Math.abs(value.selected[k]-value.frame[k])<=1);
  return !!(aligned(previous) && aligned(current) &&
    JSON.stringify(previous)===JSON.stringify(current));
}

const quoteCanvasObservationScript = `
const canvas=document.querySelector('.legend-cms-preview'),selected=document.querySelector('main .legend-cms-selected'),frame=canvas?.querySelector('.legend-cms-selection-frame');
if(!canvas||!selected||!frame)return null;
const c=canvas.getBoundingClientRect(),s=selected.getBoundingClientRect();
return {canvas:{x:c.x,y:c.y,width:c.width,height:c.height,clientWidth:canvas.clientWidth,clientHeight:canvas.clientHeight,scrollWidth:canvas.scrollWidth,scrollHeight:canvas.scrollHeight,scrollLeft:canvas.scrollLeft,scrollTop:canvas.scrollTop},
selected:{left:s.left-c.left+canvas.scrollLeft,top:s.top-c.top+canvas.scrollTop,width:Math.max(s.width,1),height:Math.max(s.height,1)},
frame:{hidden:frame.hidden,left:parseFloat(frame.style.left),top:parseFloat(frame.style.top),width:parseFloat(frame.style.width),height:parseFloat(frame.style.height)}};`;

const quoteOverflowDiagnosticScript = `
const canvas=document.querySelector('.legend-cms-preview')||document.documentElement,c=canvas.getBoundingClientRect(),right=c.left+canvas.clientLeft+canvas.clientWidth;
const describe=e=>{const r=e.getBoundingClientRect();return {tag:e.tagName,id:e.id,classes:typeof e.className==='string'?e.className:'',left:r.left,right:r.right,width:r.width,position:getComputedStyle(e).position,pseudos:['::before','::after'].map(p=>{const s=getComputedStyle(e,p);return {pseudo:p,generated:s.content!=='none'&&s.content!=='normal',display:s.display,position:s.position,left:s.left,right:s.right,width:s.width,transform:s.transform};})};};
return {frame:canvas.querySelector('.legend-cms-selection-frame')?describe(canvas.querySelector('.legend-cms-selection-frame')):null,selected:document.querySelector('main .legend-cms-selected')?describe(document.querySelector('main .legend-cms-selected')):null,offenders:[...canvas.querySelectorAll('*')].filter(e=>e.getClientRects().length&&e.getBoundingClientRect().right>right+1).slice(0,12).map(describe)};`;

test('sidebar observation waits for the shipped queued selection-frame refresh and stable canvas geometry',async()=>{
  const source=await readFile(path.join(root,'SHARED/WebsitePlatform/legend-public-cms.js'),'utf8');
  const section=(start,end)=>{const a=source.indexOf(start),b=source.indexOf(end,a);assert.ok(a>=0&&b>a,'Shipped source boundary missing');return source.slice(a,b);};
  const queue=[],selectionFrame={hidden:false,style:{},dataset:{}};
  let width=1100,refreshes=0,headerRefreshes=0;
  const context={refreshEditorHeaderProjection:()=>headerRefreshes++,selectionFrame,editorPreview:{scrollLeft:0,scrollTop:0,getBoundingClientRect:()=>({left:0,top:0})},selected:{dataset:{},closest:()=>null,getBoundingClientRect:()=>({left:16,top:20,width,height:40})},gridOverlay:null,directGesture:null,inlineEditNode:null,refreshScaledElements:()=>refreshes++,requestAnimationFrame:callback=>queue.push(callback)};
  vm.runInNewContext(section('  function previewRelativeRect(el) {','  function positionGridOverlay(section) {')+section('  function updateDirectCanvasUi() {','  function lockPreviewHorizontalScroll() {')+section('    const refreshPanelCanvas = () => {',"    panelToggle.addEventListener('click',")+'\nthis.refreshPanelCanvas=refreshPanelCanvas;this.updateDirectCanvasUi=updateDirectCanvasUi;',context);
  const observe=()=>({canvas:{width:width+32},selected:{left:16,top:20,width,height:40},frame:{hidden:selectionFrame.hidden,...Object.fromEntries(['left','top','width','height'].map(k=>[k,parseFloat(selectionFrame.style[k])]))}});
  context.updateDirectCanvasUi();const before=observe();assert.equal(quoteCanvasSettled(before,before),true);
  width=798;context.refreshPanelCanvas();assert.equal(queue.length,1);assert.equal(refreshes,0);
  const stale=observe();assert.equal(stale.frame.width,1100);assert.equal(quoteCanvasSettled(stale,stale),false,'Stable stale overlay is not ready');
  queue.shift()();assert.equal(refreshes,1);assert.equal(headerRefreshes,1);const refreshed=observe();assert.equal(refreshed.frame.width,798);
  assert.equal(quoteCanvasSettled(stale,refreshed),false);assert.equal(quoteCanvasSettled(refreshed,observe()),true);
  assert.equal(quoteCanvasSettled(null,refreshed),false);
  assert.equal(quoteCanvasSettled(refreshed,{...refreshed,canvas:{width:831}}),false);
  assert.equal(quoteCanvasSettled({...refreshed,frame:{...refreshed.frame,hidden:true}},{...refreshed,frame:{...refreshed.frame,hidden:true}}),false);
});

function quoteBreakpointReady(state,key) {
  const caps={base:'',desktop:'1440px','fixture-wide':'1550px'};
  return Object.hasOwn(caps,key) && !!state && state.selected===true && state.disabled===false && state.value===key && state.maxWidth===caps[key] && state.width==='';
}

test('quote breakpoint readiness rejects disabled, unselected and stale preview states',()=>{
  const state={selected:true,disabled:false,value:'base',maxWidth:'',width:''};
  assert.equal(quoteBreakpointReady(state,'base'),true);
  for(const key of ['desktop','fixture-wide']) assert.equal(quoteBreakpointReady({...state,value:key,maxWidth:key==='desktop'?'1440px':'1550px'},key),true);
  for(const change of [{selected:false},{disabled:true},{value:'desktop'},{maxWidth:'1440px'},{width:'100%'}]) assert.equal(quoteBreakpointReady({...state,...change},'base'),false);
  assert.equal(quoteBreakpointReady(null,'base'),false);assert.equal(quoteBreakpointReady(state,'unknown'),false);
});

function quotePhaseValid(p,index) {
  if(!p || p.selected!==String(index) || !Array.isArray(p.steps) || p.steps.length!==5) return false;
  const question=index>=1&&index<=5, final=index>=7;
  if(p.entry!==(index===0)||p.question!==question||p.processing!==(index===6)||p.contact!==final) return false;
  if(question && (!Array.isArray(p.labels)||p.steps.some((v,i)=>v!==(i===index-1)) || p.progress!==(p.labels[index-1]?p.labels[index-1]+' · '+index+' of 5':'Question '+index+' of 5')))return false;
  if(!question&&p.steps.some(Boolean))return false;
  return !final || (p.review&&p.sample&&p.illustrative&&Number.isInteger(p.expectedMetrics)&&p.expectedMetrics>0&&p.metrics===p.expectedMetrics);
}
test('quote phase policy rejects noop navigation, hidden active steps, wrong progress and missing real review',()=>{
  const question={selected:'1',entry:false,question:true,processing:false,contact:false,steps:[true,false,false,false,false],labels:['Who','Goal','Range','Age','Health'],progress:'Who · 1 of 5'};
  assert.equal(quotePhaseValid(question,1),true);
  for(const delta of [{selected:'0'},{question:false},{steps:[true,true,false,false,false]},{progress:'Who · 2 of 5'}])assert.equal(quotePhaseValid({...question,...delta},1),false);
  assert.equal(quotePhaseValid({...question,labels:Array(5).fill(null),progress:'Question 1 of 5'},1),true);
  assert.equal(quotePhaseValid({...question,labels:Array(5).fill(null),progress:'Wrong question'},1),false);
  const review={selected:'7',entry:false,question:false,processing:false,contact:true,steps:Array(5).fill(false),review:true,sample:true,illustrative:true,metrics:2,expectedMetrics:2};
  assert.equal(quotePhaseValid(review,7),true);
  assert.equal(quotePhaseValid({...review,metrics:3,expectedMetrics:3},7),true);
  assert.equal(quotePhaseValid({...review,metrics:2,expectedMetrics:3},7),false);
  for(const delta of [{review:false},{sample:false},{illustrative:false},{metrics:0}])assert.equal(quotePhaseValid({...review,...delta},7),false);
});

function quoteControlSnapshots(document,window) {
  const text=value=>String(value??'').slice(0,128),css=node=>{const s=window.getComputedStyle(node);return Object.fromEntries(['display','visibility','opacity','position','pointerEvents'].map(k=>[k,text(s[k])]));};
  const box=node=>{const r=node.getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height,right:r.right,bottom:r.bottom};};
  const visibleAncestors=node=>{let count=0;for(;node&&count++<64;node=node.parentElement){const s=window.getComputedStyle(node);if(node.hidden||node.inert||node.getAttribute('aria-hidden')==='true'||s.display==='none'||s.visibility!=='visible'||Number(s.opacity)<=0)return false;}return !node;};
  return [...document.querySelectorAll('main button,main input:not([type=hidden]),main select')]
    .filter(e=>e.getClientRects().length&&window.getComputedStyle(e).visibility==='visible').slice(0,40).map(input=>{
      const style=css(input),native={tag:text(input.tagName),type:text(input.type),name:text(input.name),id:text(input.id),rect:box(input),style,disabled:input.disabled===true,tabIndex:input.tabIndex};
      const authoredRadio=input.tagName==='INPUT'&&input.type==='radio'&&style.position==='absolute'&&Number(style.opacity)===0&&style.pointerEvents==='none'&&!!input.closest('.lq-card');
      if(!authoredRadio)return {native,representation:'native',rect:native.rect,style,visible:visibleAncestors(input)};
      const labels=[...(input.labels||[])],label=labels.length===1?labels[0]:null;
      const associated=!!label&&label.tagName==='LABEL'&&label.matches('.lq-card')&&label.control===input&&input.form!==null&&label.closest('form')===input.form&&label===input.closest('label.lq-card');
      const r=label?box(label):null,center=r?{x:r.x+r.width/2,y:r.y+r.height/2}:null;
      const centerInViewport=!!center&&center.x>=0&&center.y>=0&&center.x<window.innerWidth&&center.y<window.innerHeight;
      const hit=centerInViewport?document.elementFromPoint(center.x,center.y):null;
      return {native,representation:'native-radio-label',labelCount:labels.length,associated,
        semantic:!!input.name&&input.tabIndex>=0&&!input.disabled&&input.getAttribute('aria-hidden')!=='true'&&visibleAncestors(input.parentElement),
        label:label?{tag:text(label.tagName),id:text(label.id),rect:r,style:css(label),rectCount:label.getClientRects().length,
          hasNativeCopy:!!label.querySelector('.lq-card-copy,.lq-card-title')?.textContent.trim(),visible:visibleAncestors(label),centerInViewport,hitTarget:!!hit&&(hit===label||label.contains(hit))}:null};
    });
}
function quoteControlFits(control,canvas,panel,sidebar=false) {
  const visible=style=>style?.display!=='none'&&style?.visibility==='visible'&&Number(style?.opacity)>0;
  let r=control?.rect;
  if(control?.representation==='native-radio-label'){
    const n=control.native,l=control.label;
    if(n?.tag!=='INPUT'||n.type!=='radio'||n.style?.position!=='absolute'||Number(n.style?.opacity)!==0||n.style?.pointerEvents!=='none'||
      control.labelCount!==1||control.associated!==true||control.semantic!==true||!l||l.tag!=='LABEL'||l.rectCount<=0||l.hasNativeCopy!==true||l.visible!==true||!visible(l.style)||typeof l.centerInViewport!=='boolean'||(l.centerInViewport&&l.hitTarget!==true))return false;
    r=l.rect;
  }else if(control?.representation!=='native'||control.visible!==true||!visible(control.style))return false;
  return !!r&&[r.x,r.y,r.width,r.height,r.right,r.bottom].every(Number.isFinite)&&r.width>0&&r.height>0&&r.x>=canvas.x-1&&r.right<=canvas.right+1&&(!sidebar||r.right<=panel.x+1);
}
test('quote control geometry admits only genuine visible associated native radio labels',()=>{
  const style={display:'block',visibility:'visible',opacity:'1',position:'static',pointerEvents:'auto'},rect={x:10,y:20,width:100,height:40,right:110,bottom:60},canvas={x:0,right:500},panel={x:500};
  const native={tag:'INPUT',type:'radio',style:{...style,opacity:'0',position:'absolute',pointerEvents:'none'},rect:{...rect,width:0,height:0,right:10,bottom:20}};
  const good={native,representation:'native-radio-label',labelCount:1,associated:true,semantic:true,label:{tag:'LABEL',rect,style,rectCount:1,hasNativeCopy:true,visible:true,centerInViewport:true,hitTarget:true}};
  assert.equal(quoteControlFits(good,canvas,panel,true),true);
  for(const change of [{label:null},{labelCount:0},{labelCount:2},{associated:false},{semantic:false}])assert.equal(quoteControlFits({...good,...change},canvas,panel,true),false);
  for(const change of [{visible:false},{centerInViewport:null},{hitTarget:false},{hasNativeCopy:false},{rectCount:0},{style:{...style,opacity:'0'}},{style:{...style,visibility:'hidden'}},{rect:{...rect,width:0}},{rect:{...rect,x:-2}},{rect:{...rect,right:502}}])assert.equal(quoteControlFits({...good,label:{...good.label,...change}},canvas,panel,true),false);
  // Horizontal geometry does not claim below-fold reachability/keyboard proof.
  assert.equal(quoteControlFits({...good,label:{...good.label,rect:{...rect,y:1000,bottom:1040},centerInViewport:false,hitTarget:false}},canvas,panel,true),true);
  const ordinary={native:{tag:'BUTTON'},representation:'native',rect,style,visible:true};
  assert.equal(quoteControlFits(ordinary,canvas,panel,true),true);
  assert.equal(quoteControlFits({...ordinary,rect:native.rect},canvas,panel,true),false);
  assert.equal(quoteControlFits({...ordinary,rect:{...rect,right:502}},canvas,panel,true),false);
  assert.equal(quoteControlFits({...good,native:{...native,type:'checkbox'}},canvas,panel,true),false);
});

test('quote snapshot resolves actual native label association without inventing a wrapper',()=>{
  const style={display:'block',visibility:'visible',opacity:'1',position:'static',pointerEvents:'auto'},rect={x:10,y:20,width:100,height:40,right:110,bottom:60};
  const node=(tag,ownStyle=style,ownRect=rect)=>({tagName:tag,hidden:false,inert:false,parentElement:null,style:ownStyle,
    getAttribute:()=>null,getBoundingClientRect:()=>ownRect,getClientRects:()=>[ownRect]});
  const form=node('FORM'),label=node('LABEL'),input=node('INPUT',{...style,opacity:'0',position:'absolute',pointerEvents:'none'},{...rect,width:0,height:0,right:10,bottom:20});
  Object.assign(input,{type:'radio',name:'ProtectingWho',id:'',tabIndex:0,disabled:false,form,labels:[label],parentElement:label,closest:()=>label});
  Object.assign(label,{control:input,parentElement:form,matches:s=>s==='.lq-card',closest:s=>s==='form'?form:null,querySelector:()=>({textContent:'Native option'}),contains:n=>n===input});
  const document={querySelectorAll:()=>[input],elementFromPoint:()=>label},window={innerWidth:1000,innerHeight:800,getComputedStyle:n=>n.style};
  const snapshot=()=>quoteControlSnapshots(document,window)[0],fits=()=>quoteControlFits(snapshot(),{x:0,right:500},{x:500},true);
  assert.equal(fits(),true);assert.equal(snapshot().native.name,'ProtectingWho');assert.equal(snapshot().native.rect.width,0);assert.equal(snapshot().label.rect.width,100);
  input.labels=[];assert.equal(fits(),false);input.labels=[label];
  label.control={};assert.equal(fits(),false);label.control=input;
  label.closest=()=>({});assert.equal(fits(),false);label.closest=()=>form;
  form.inert=true;assert.equal(fits(),false);form.inert=false;
  label.hidden=true;assert.equal(fits(),false);label.hidden=false;
  label.querySelector=()=>null;assert.equal(fits(),false);
});

const quoteSnapshotScript = `
const box=e=>{if(!e)return null;const r=e.getBoundingClientRect();return{x:r.x,y:r.y,width:r.width,height:r.height,right:r.right,bottom:r.bottom};};
const shell=document.querySelector('[data-protect-form-shell]'),canvas=document.querySelector('.legend-cms-preview')||document.documentElement,panel=document.querySelector('.legend-cms-panel');
const css=shell?getComputedStyle(shell):null;const outer=canvas.getBoundingClientRect(),canvasCss=getComputedStyle(canvas),pl=parseFloat(canvasCss.paddingLeft)||0,pr=parseFloat(canvasCss.paddingRight)||0;const canvasBox={x:outer.x+canvas.clientLeft+pl,y:outer.y+canvas.clientTop,width:canvas.clientWidth-pl-pr,height:canvas.clientHeight};canvasBox.right=canvasBox.x+canvasBox.width;canvasBox.bottom=canvasBox.y+canvasBox.height;
const form=document.querySelector('main form[data-cms-presentation]'),visible=e=>!!(e&&e.getClientRects().length&&getComputedStyle(e).visibility==='visible'&&Number(getComputedStyle(e).opacity)>0),find=key=>document.getElementById(form?.dataset[key]||'');
const steps=[...document.querySelectorAll('main [data-cms-native-step]')];
const phase={selected:document.querySelector('#legend-cms-form-navigator select')?.value,entry:visible(find('cmsEntryId')),question:visible(find('cmsQuestionId')),processing:visible(find('cmsProcessingId')),contact:visible(find('cmsContactId')),review:visible(find('cmsReviewId')),steps:steps.map(n=>visible(n)),progress:find('cmsProgressId')?.textContent,labels:steps.map(n=>n.dataset.cmsProgressLabel),sample:!!find('cmsReviewId')?.querySelector('[data-cms-preview-only]'),illustrative:find('cmsReviewId')?.textContent.includes('Illustrative Studio review — not a quote'),expectedMetrics:(form?.dataset.cmsReviewMetrics||'').split('|').filter(Boolean).length,metrics:find('cmsReviewId')?.querySelectorAll('.lq-contact-preview-mini-metric,.dq-review-stat').length};
const breakpoint=document.querySelector('#legend-cms-breakpoint');
const layout={selected:!!document.querySelector('main .legend-cms-selected[data-cms-editable="true"]'),disabled:breakpoint?.disabled,value:breakpoint?.value,width:canvas.style.width,maxWidth:canvas.style.maxWidth,justifySelf:canvas.style.justifySelf};
return {editing:document.body.classList.contains('legend-cms-editing'),layout,phase,canvasOuter:box(canvas),available:box(document.querySelector('.legend-cms-canvas')),shell:box(shell),canvas:canvasBox,panel:box(panel),style:css?{display:css.display,visibility:css.visibility,opacity:css.opacity}:null,
overflow:Math.max(0,canvas.scrollWidth-canvas.clientWidth),viewport:{width:innerWidth,height:innerHeight},
errors:window.__quoteErrors,requests:window.__quoteRequests,inputDocumentUnchanged:JSON.stringify(window.__quoteDocumentObject)===window.__quoteDocument,
identity:(window.__quoteOriginalNodes||[]).every(n=>n.isConnected),duplicateIds:[...document.querySelectorAll('main [id]')].map(n=>n.id).filter((id,i,a)=>a.indexOf(id)!==i).slice(0,10),
images:[...document.querySelectorAll('main img')].map(n=>({loaded:n.complete&&n.naturalWidth>0,src:n.getAttribute('src')})),
nativeSteps:document.querySelectorAll('main [data-cms-native-step]').length,
phaseOptions:[...document.querySelectorAll('#legend-cms-form-navigator select option')].map(n=>n.value),
canvasScrollTop:canvas.scrollTop,
panelHidden:document.body.classList.contains('legend-cms-panel-hidden'),
controls:(${quoteControlSnapshots.toString()})(document,window)};`;

const quoteSelectableContentScript = `const nodes=[...document.querySelectorAll('main p.lq-hero-step-copy-helper,main h1,main h2,main h3,main h4,main h5,main h6,main .lq-hero-step-copy-title,main .lq-contact-capture-title,main .dq-contact-title,main .lq-step-title')];const n=nodes.find(e=>e.closest('[data-cms-editable="true"]')&&!e.closest('[hidden],[inert],[aria-hidden="true"],[data-cms-preview-only]')&&e.getClientRects().length&&getComputedStyle(e).visibility==='visible'&&Number(getComputedStyle(e).opacity)>0);return n?{node:n,target:n.closest('[data-cms-editable="true"]')}:null;`;

test('quote selection discovers actual editable native paragraph without fabricating a title',()=>{
  const owner={id:'native-owner'},paragraph={closest:s=>s==='[data-cms-editable="true"]'?owner:null,getClientRects:()=>[{}]};
  let selector;
  const context={document:{querySelectorAll:s=>{selector=s;return s.includes('main p.lq-hero-step-copy-helper')?[paragraph]:[];}},getComputedStyle:()=>({visibility:'visible',opacity:'1'})};
  const find=()=>vm.runInNewContext('(function(){'+quoteSelectableContentScript+'})()',context);
  const found=find();assert.equal(found.node,paragraph);assert.equal(found.target,owner);
  assert.ok(selector.includes('main h1'));assert.ok(!selector.includes('input'));
  paragraph.closest=()=>null;assert.equal(find(),null);
  paragraph.closest=s=>s==='[data-cms-editable="true"]'?owner:{};assert.equal(find(),null);
  paragraph.closest=s=>s==='[data-cms-editable="true"]'?owner:null;paragraph.getClientRects=()=>[];assert.equal(find(),null);
});

test('actual canonical frontend components render in the existing sandboxed CI browser', {timeout:160000}, async t => {
  const start = Date.now();
  const deadline = start + 160000;
  let cancelled=false;const setupAbort=new AbortController();
  const remaining = () => {
    assert.equal(cancelled,false,'Native test cancelled');
    const value = deadline - Date.now();
    assert.ok(value > 10000, 'Native render budget exhausted; reserve cleanup time');
    return value - 10000;
  };
  let manifest,chromeVersion,driverVersion,checked,driverBindings,versionProbeFailure,scrollDiagnostic,localeDiagnostic,placementDiagnostic,lifecycleDiagnostic,quoteDiagnostic;
  const requiredScenarios=[...hosts.flatMap(host=>widths.map(width=>'viewport:'+host+':'+width)),'modal-order-and-wrap','locale-month-rollover','long-profile-and-offset','live-mobile-boundary','client-home-focus',...['life','disability','dental-vision-hearing'].map(route=>'quote-source:'+route),'quote-responsive-studio','protect-scripture-keyboard','studio-header-projection'];
  const completedScenarios=new Set();
  const quoteRecords=[];
  const records=[]; let acceptanceComplete=false; let failure; let server,driver,session,driverOrigin; let serverHost; let driverLog='';
  const rejected=[];
  let cleanupPromise;
  async function cleanup() {
    if(cleanupPromise)return cleanupPromise;
    cleanupPromise=(async()=>{
      if(session && driverOrigin) await fetch(driverOrigin+'/session/'+session,{method:'DELETE',signal:AbortSignal.timeout(1500)}).catch(()=>{});
      if(server){server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
      const stopOwnedGroup=signal=>{if(driver?.pid){try{process.kill(-driver.pid,signal)}catch(error){if(error.code!=='ESRCH')throw error;}}};
      stopOwnedGroup('SIGTERM');
      if(driver && driver.exitCode===null){
        await Promise.race([new Promise(resolve=>driver.once('exit',resolve)),new Promise(resolve=>setTimeout(resolve,1000))]);
        if(driver.exitCode===null){stopOwnedGroup('SIGKILL');await Promise.race([new Promise(resolve=>driver.once('exit',resolve)),new Promise(resolve=>setTimeout(resolve,1000))]);}
        assert.ok(driver.exitCode!==null||driver.signalCode!==null,'Owned ChromeDriver did not terminate');
      }
      stopOwnedGroup('SIGKILL');
    })();
    return cleanupPromise;
  }
  const removeCancellation=registerCancellation(process,t.signal,cleanup,name=>{cancelled=true;setupAbort.abort();failure='Cancelled: '+name;},code=>process.exit(code));
  const watchdog=setTimeout(()=>{cancelled=true;setupAbort.abort();void cleanup();},150000-(Date.now()-start));
  let commandOrdinal=0,commandFailure;
  const command=(method,endpoint,payload)=>webdriverCommand({origin:driverOrigin,method,endpoint,payload,remaining,ordinal:++commandOrdinal});
  const execute = (script,args=[])=>command('POST','/session/'+session+'/execute/sync',{script,args});
  const waitFor=async(script)=>{
    const until=Math.min(Date.now()+3000,deadline-10000);
    while(Date.now()<until){if(await execute('return '+script))return;await new Promise(r=>setTimeout(r,40));}
    assert.fail('Browser condition timed out: '+script);
  };
  const element=async(selector)=>(await command('POST','/session/'+session+'/element',{using:'css selector',value:selector}))['element-6066-11e4-a52e-4f735466cecf'];
  const click=async(selector)=>command('POST','/session/'+session+'/element/'+await element(selector)+'/click',{});
  const keys=async(value)=>command('POST','/session/'+session+'/actions',{actions:[{type:'key',id:'keyboard',actions:value.flatMap(v=>[{type:'keyDown',value:v},{type:'keyUp',value:v}])}]});
  const wheel=async(selector,delta)=>command('POST','/session/'+session+'/actions',{actions:[{type:'wheel',id:'wheel',actions:[{type:'scroll',origin:{'element-6066-11e4-a52e-4f735466cecf':await element(selector)},x:0,y:0,deltaX:0,deltaY:delta,duration:60}]}]});
  try {
    requireCI();
  const event = JSON.parse(await readFile(process.env.GITHUB_EVENT_PATH,'utf8'));
  assert.equal(event.repository?.private,false,'Public repository classification required for $0 runner compute');
  const dir = await mkdtemp(path.join(process.env.RUNNER_TEMP,'legend-native-components-'));
  const build = await setupCommand('python3',[path.join(root,'tests/layout/build-native-component-fixture.py'),'--root',root,'--out',dir],remaining,setupAbort.signal,15000);
  assert.equal(build.status,0,build.stderr || build.error?.message);
  manifest = JSON.parse(await readFile(path.join(dir,'manifest.json'),'utf8'));
  const headProbe=await setupCommand('git',['rev-parse','HEAD'],remaining,setupAbort.signal,3000,{cwd:root});assert.equal(headProbe.status,0,'Git HEAD probe failed');checked=headProbe.stdout.trim();
  const expected=expectedCheckout(process.env.GITHUB_EVENT_NAME,event,process.env.GITHUB_SHA);
  assert.equal(checked,expected,'Checkout differs from exact event head');
  assert.equal(manifest.checkout,checked);
  for(const file of ['tests/layout/native-component-render.test.mjs','tests/layout/build-native-component-fixture.py']){
    const bytes=await readFile(path.join(root,file));manifest.inputs[file]={bytes:bytes.length,sha256:hash(bytes)};
  }
  for(const file of Object.keys(manifest.inputs)){
    const tracked=await setupCommand('git',['ls-files','--error-unmatch','--',file],remaining,setupAbort.signal,3000,{cwd:root});assert.equal(tracked.status,0,'Untracked fixture input:'+file);
    const clean=await setupCommand('git',['diff','--quiet','HEAD','--',file],remaining,setupAbort.signal,3000,{cwd:root});assert.equal(clean.status,0,'Dirty fixture input:'+file);
    assert.equal(hash(await readFile(path.join(root,file))),manifest.inputs[file].sha256,'Fixture input changed:'+file);
  }
  const routes = new Map();
  for(const host of hosts) {
    const bytes=await readFile(path.join(dir,host+'.html'));
    assert.equal(hash(bytes),manifest.outputs[host+'.html']);
    routes.set('/'+host+'.html',bytes);
  }
  assert.equal(Object.keys(manifest.quotes.routes).length,3,'Three source-template component projections required');
  assert.equal(manifest.quotes.routeMapping.length,8,'Eight source route mappings required (not eight rendered route proofs)');
  for(const [route,file] of Object.entries(manifest.quotes.routes)) {
    assert.match(route,/^\/quote\/[a-z-]+$/);assert.match(file,/^quote-[a-z-]+\.html$/);
    const bytes=await readFile(path.join(dir,file));assert.equal(hash(bytes),manifest.outputs[file]);
    for(const query of ['?legendEdit=ticket&legendMaterialize=1','?legendEdit=ticket','?legendEdit=ticket&width=720','?public=1','?public=1&width=720'])routes.set(route+query,bytes);
    if(route==='/quote/disability')for(const tenant of ['legend','other'])routes.set(route+'?legendEdit=fixture&header=1&tenant='+tenant,bytes);
  }
  for (const [mode,file] of Object.entries(manifest.quotes.scripture)) {
    assert.ok(['cold','no-trigger','trigger'].includes(mode));
    const bytes=await readFile(path.join(dir,file));assert.equal(hash(bytes),manifest.outputs[file]);
    routes.set('/scripture-'+mode+'.html',bytes);
  }
  for(const [route,file] of Object.entries(manifest.quotes.assets)) {
    assert.match(route,/^\/images\/ads\/[a-z-]+\.png$/);assert.match(file,/^quote-asset-[a-z-]+\.png$/);
    const bytes=await readFile(path.join(dir,file));assert.equal(hash(bytes),manifest.outputs[file]);routes.set(route,bytes);
  }
  const chrome = '/usr/bin/google-chrome';
  const driverBinary = process.env.CHROMEWEBDRIVER ? path.join(process.env.CHROMEWEBDRIVER,'chromedriver') : 'chromedriver';
  chromeVersion=await binaryVersion(chrome,remaining,setupAbort.signal);driverVersion=await binaryVersion(driverBinary,remaining,setupAbort.signal);
  assert.equal(chromeVersion.match(/\d+\./)[0],driverVersion.match(/\d+\./)[0],'Installed browser/driver major versions must match');
    remaining();
    server=createServer((req,res)=>{
      if(!allowedRequest(req,serverHost,routes)){
        rejected.push({method:req.method,url:req.url,host:req.headers.host});res.writeHead(404);res.end();return;
      }
      res.writeHead(200,{'Content-Type':req.url.endsWith('.png')?'image/png':'text/html; charset=utf-8','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});res.end(routes.get(req.url));
    });
    server.requestTimeout=2000;server.headersTimeout=2000;server.keepAliveTimeout=500;
    await new Promise((resolve,reject)=>{server.once('error',reject);server.listen(0,'127.0.0.1',resolve);});
    const address=server.address();assert.equal(address.address,'127.0.0.1');assert.ok(address.port>0);serverHost='127.0.0.1:'+address.port;
    driver=spawn(driverBinary,['--port=0'],{stdio:['ignore','pipe','pipe'],detached:true});
    driver.on('error',error=>{driverLog+='\n'+error.message;});
    for(const stream of [driver.stdout,driver.stderr])stream.on('data',chunk=>{driverLog=(driverLog+chunk.toString()).slice(-20000);});
    const startup=Date.now()+5000;
    while(!/started successfully on port (\d+)/i.test(driverLog)&&Date.now()<startup){remaining();if(driver.exitCode!==null)break;await new Promise(r=>setTimeout(r,30));}
    const port=driverLog.match(/started successfully on port (\d+)/i)?.[1];assert.ok(port,'ChromeDriver startup failed: '+driverLog);
    const ownedInodes=new Set();
    for(const fd of await readdir('/proc/'+driver.pid+'/fd')){
      const link=await readlink('/proc/'+driver.pid+'/fd/'+fd).catch(()=> '');const match=link.match(/^socket:\[(\d+)\]$/);if(match)ownedInodes.add(match[1]);
    }
    const tables=await Promise.all(['tcp','tcp6'].map(name=>readFile('/proc/'+driver.pid+'/net/'+name,'utf8')));
    driverBindings=loopbackListeners(tables[0]+'\n'+tables[1].split('\n').slice(1).join('\n'),ownedInodes,port);
    driverOrigin='http://127.0.0.1:'+port;
    const created=await command('POST','/session',{capabilities:{alwaysMatch:{browserName:'chrome','goog:chromeOptions':{binary:chrome,args:['--headless=new','--window-size=1180,900']}}}});
    session=created.sessionId;assert.ok(session);
    await command('POST','/session/'+session+'/timeouts',{implicit:0,pageLoad:5000,script:3000});
    for(const host of hosts)for(const width of widths){
      scrollDiagnostic={host,width};
      remaining();
      await command('POST','/session/'+session+'/window/rect',{width,height:900});
      await command('POST','/session/'+session+'/url',{url:'http://'+serverHost+'/'+host+'.html'});
      // Chromium's native window minimum exceeds320px; device metrics changes only
      // the real CSS viewport. All pointer/keyboard/wheel input remains WebDriver.
      await command('POST','/session/'+session+'/goog/cdp/execute',{cmd:'Emulation.setDeviceMetricsOverride',params:{width,height:width<600?640:757,deviceScaleFactor:1,mobile:false}});
      assert.equal(await execute('return innerWidth'),width);
      let mobileExploreMeasurement=null;
      if(host!=='standalone'){
        if(width<=840){await click('[data-legend-nav-toggle]');await waitFor("document.querySelector('[data-legend-global-nav]').classList.contains('mobile-open')");}
        const nav=await execute(`const visible=e=>e.getClientRects().length&&getComputedStyle(e).visibility!=='hidden';const box=e=>{const b=e.getBoundingClientRect();return {x:b.x,y:b.y,w:b.width,h:b.height,r:b.right,b:b.bottom}};const nodes=[...document.querySelectorAll('header .navbar-brand,header .nav-link,header .header-psalms,header .explore-trigger,header .nav-toggle,header .profile-chip,header .legend-founder-ai-nav-button,header .messaging-nav-trigger')].filter(visible);return nodes.map(e=>({text:e.textContent.trim().slice(0,40),id:e.id,box:box(e)}));`);
        for(const n of nav){assert.ok(n.box.x>=-1&&n.box.r<=width+1,JSON.stringify({host,width,nav}));}
        for(let i=0;i<nav.length;i++)for(let j=i+1;j<nav.length;j++){
          const a=nav[i].box,b=nav[j].box;assert.ok(Math.min(a.r,b.r)-Math.max(a.x,b.x)<=1||Math.min(a.b,b.b)-Math.max(a.y,b.y)<=1,'Overlapping navigation '+JSON.stringify({host,width,a:nav[i],b:nav[j]}));
        }
        for(const n of nav.filter(x=>requiresNavigation44(x.id,width)))assert.ok(n.box.w>=43.9&&n.box.h>=43.9,'Navigation target below44');
        if(width<=840) mobileExploreMeasurement=nav.filter(x=>x.id==='exploreTrigger').map(x=>({box:x.box,target44Observed:x.box.w>=43.9&&x.box.h>=43.9,status:'OBSERVED_EXISTING_UNADMITTED_TARGET_GAP'}));
        if(width<=840){await keys(['\uE00C']);await waitFor("!document.querySelector('[data-legend-global-nav]').classList.contains('mobile-open')");}
        if(width===1180){
          const paint=await execute(`const css=s=>{const e=document.querySelector(s);if(!e)throw Error('Missing contrast element:'+s);const c=getComputedStyle(e);return {color:c.color,background:c.backgroundColor,image:c.backgroundImage}};return {card:css('#fixtureNavyCard'),body:css('#fixtureNavyCard .card-body'),explicit:css('#fixtureExplicitCard .card-body'),label:css('.legend-shell label'),intro:css('.legend-shell .text-muted'),input:css('#fixtureInput'),connect:css('.lc-panel'),shell:css('.legend-shell')};`);
          const contrast=computedContrast;
          assert.ok(contrast(paint.body.color,paint.card.background)>=4.5,'Shared card contrast');
          assert.equal(paint.explicit.color,'rgb(17, 34, 51)','Explicit consumer token preserved');
          for(const key of ['label','intro'])assert.ok(contrast(paint[key].color,paint.shell.background)>=4.5,'Onboarding '+key+' contrast');
          assert.ok(contrast(paint.input.color,paint.input.background)>=4.5,'Input contrast');
          // The canonical lc-panel paints translucent gradients, not an opaque
          // backgroundColor. Bound contrast over both black/white underlays and
          // the actual radial overlay instead of pretending transparent is black.
          const stops=[...paint.connect.image.matchAll(/rgba?\(([^)]+)\)/g)].map(m=>m[1].split(',').map(Number)).map(c=>[...c.slice(0,3),c.length===4?c[3]:1]);
          const bases=stops.filter(c=>c[3]>=.9),overlays=stops.filter(c=>c[3]>0&&c[3]<.9);
          assert.equal(bases.length,2,'Canonical gradient base changed');assert.equal(overlays.length,1,'Canonical radial overlay changed');
          const mix=(front,back)=>front.slice(0,3).map((v,i)=>v*front[3]+back[i]*(1-front[3]));
          for(const base of bases)for(const under of [[0,0,0],[255,255,255]])for(const overlay of [null,overlays[0]]){
            const color=mix(base,under),painted=overlay?mix(overlay,color):color;
            assert.ok(contrast(paint.connect.color,'rgb('+painted.join(',')+')')>=4.5,'Legend Connect gradient contrast bound');
          }
          console.log('COMPUTED_PAINT '+JSON.stringify({host,paint,limits:'Solid computed base colors; gradient/text antialias and public Razor journey remain separate.'}));
        }
        await click('#fixtureBookingOpen');
      }
      await waitFor("document.querySelector('#qvBookingModal').classList.contains('show') && document.querySelector('[data-qv-booking-date]') && document.querySelector('#qvBookSlots button')");
      await waitFor("window.__shown.includes('qvBookingModal')");
      if(host==='standalone'){
        await waitFor("document.querySelectorAll('#mobileAppointmentList .mobile-appointment').length>0");
        const hostProof=await execute("return {hidden:document.querySelector('.qv-booking-body #mobileClientAppointments')?.hidden,rows:document.querySelectorAll('#mobileAppointmentList .mobile-appointment').length,calls:window.__fixtureCalls}");
        assert.equal(hostProof.hidden,false);assert.equal(hostProof.rows,1);
        assert.ok(hostProof.calls.includes('/mobile/agent/booking/appointments'));assert.ok(hostProof.calls.includes('/mobile/agent/booking/availability'));
      }
      const childBounds=await execute(`const body=document.querySelector('.qv-booking-body').getBoundingClientRect();const selectors=['.qv-booking-slot-card','.qv-booking-slot-focus','.qv-booking-selection-meta','.qv-booking-foot','#qvBookStatus','#btnBookAppointment','#qvBookSlots button'];return selectors.map(selector=>{const e=document.querySelector(selector);if(!e)throw Error('Missing critical child:'+selector);const b=e.getBoundingClientRect();const panel=selector==='.qv-booking-selection-meta'?e.closest('.qv-booking-panel-selection'):null;return {selector,x:b.x,r:b.right,w:b.width,h:b.height,rects:e.getClientRects().length,visibility:getComputedStyle(e).visibility,client:e.clientWidth,scroll:e.scrollWidth,body:{x:body.x,r:body.right},selectionPanel:panel?{exact:e.id==='qvBookingSelectedMeta'&&e.parentElement===panel&&panel.classList.contains('qv-booking-panel'),display:getComputedStyle(panel).display,rects:panel.getClientRects().length}:null}});`);
      for(const child of childBounds)assertCriticalBookingChild(child);

      await waitFor(`(${bookingDialogSettled.toString()})(document)`);
      const before=await execute(`const box=e=>{const b=e.getBoundingClientRect();return {x:b.x,y:b.y,w:b.width,h:b.height,r:b.right,b:b.bottom}};const body=document.querySelector('.qv-booking-body');return {body:box(body),head:box(document.querySelector('.qv-booking-head')),content:box(document.querySelector('.qv-booking-content')),action:box(document.querySelector('#btnBookAppointment')),nav:[...document.querySelectorAll('.qv-booking-calendar-nav')].map(box),dates:[...document.querySelectorAll('[data-qv-booking-date]')].map(box),grid:box(document.querySelector('#qvBookingCalendar')),client:body.clientHeight,scroll:body.scrollHeight,horizontal:body.scrollWidth-body.clientWidth,errors:window.__fixtureErrors,scrollDiagnostic:(${bookingScrollSnapshot.toString()})(document,window)};`);
      scrollDiagnostic.before=before.scrollDiagnostic;
      assert.equal(new Set(before.dates.map(d=>Math.round(d.x*10))).size,7,'Calendar must retain exactly seven distinct date columns');
      assert.deepEqual(before.errors,[]);assert.ok(before.horizontal<=1,'Body horizontal overflow');
      for(const n of before.nav)assert.ok(n.w>=43.9&&n.h>=43.9,'Booking navigation target below44');
      for(const d of before.dates){if(width>=393)assert.ok(d.w>=43.9&&d.h>=43.9,'Date target below44 at admitted393px/desktop widths');assert.ok(d.x>=before.grid.x-1&&d.r<=before.grid.r+1,'Date escapes seven-column grid');}
      await wheel('.qv-booking-body',6000);await new Promise(r=>setTimeout(r,80));
      const after=await execute(`const body=document.querySelector('.qv-booking-body'),a=document.querySelector('#btnBookAppointment').getBoundingClientRect(),h=document.querySelector('.qv-booking-head').getBoundingClientRect(),b=body.getBoundingClientRect();return {scroll:body.scrollTop,action:{top:a.top,bottom:a.bottom},body:{top:b.top,bottom:b.bottom},head:h.top,scrollDiagnostic:(${bookingScrollSnapshot.toString()})(document,window)};`);
      scrollDiagnostic.after=after.scrollDiagnostic;
      assert.ok(before.scroll<=before.client+1||after.scroll>0,'Native wheel did not scroll body');
      assert.ok(after.action.top>=after.body.top-1&&after.action.bottom<=after.body.bottom+1,'Final action clipped');
      assertStableBookingHeader(before,after);
      assert.ok(after.head>=-1&&before.head.b<=(width<600?640:757)+1,'Header inaccessible');
      await keys(['\uE004']);assert.equal(await execute("return document.querySelector('#qvBookingModal').contains(document.activeElement)"),true,'Tab focus left modal');
      await keys(['\uE00C']);await waitFor("!document.querySelector('#qvBookingModal').classList.contains('show')");
      assert.deepEqual(await execute('return window.__fixtureErrors'),[]);
      completedScenarios.add('viewport:'+host+':'+width);
      records.push({host,width,before,after,mobileExploreMeasurement,status:'PASS',target44Required:width>=393,target44Observed:before.dates.every(d=>d.w>=43.9&&d.h>=43.9)});
      console.log(JSON.stringify({host,width,status:'PASS'}));
    }
    scrollDiagnostic=undefined;
    // Real Bootstrap fade/MutationObserver/shown ordering and canonical bound layers.
    await command('POST','/session/'+session+'/goog/cdp/execute',{cmd:'Emulation.setDeviceMetricsOverride',params:{width:1180,height:757,deviceScaleFactor:1,mobile:false}});
    await command('POST','/session/'+session+'/url',{url:'http://'+serverHost+'/authenticated.html'});
    await execute(`window.__shown=[];document.addEventListener('shown.bs.modal',e=>window.__shown.push(e.target.id));const original=document.querySelector('#qvBookingModal');window.__bookingLifecycle={node:original,shown:0,hidden:0};for(const type of ['shown','hidden'])original.addEventListener(type+'.bs.modal',e=>{if(e.target===original)window.__bookingLifecycle[type]++;});const lower=original.cloneNode(true);lower.id='fixtureLowerModal';lower.removeAttribute('data-legend-modal-bound');lower.querySelectorAll('[id]').forEach(e=>e.id='fixtureLower-'+e.id);lower.setAttribute('aria-labelledby','fixtureLower-qvBookingTitle');const launch=lower.querySelector('.qv-booking-submit');launch.setAttribute('data-qv-booking-open','');launch.id='fixtureLowerLaunch';launch.textContent='Open nested booking';document.body.append(lower);window.LegendModal.bind(lower.id,{modalZ:1115,backdropZ:1110});window.__lower=bootstrap.Modal.getOrCreateInstance(lower);window.__lower.show();`);
    await waitFor("document.querySelector('#fixtureLowerModal').classList.contains('show')");
    await waitFor("window.__shown.includes('fixtureLowerModal')");
    await click('#fixtureLowerLaunch');
    await waitFor("document.querySelector('#qvBookingModal').classList.contains('show')");
    await waitFor("window.__shown.includes('qvBookingModal')");
    const layers=await execute(`return ['fixtureLowerModal','qvBookingModal'].map(id=>Number(getComputedStyle(document.getElementById(id)).zIndex));`);
    assert.ok(layers[1]>layers[0],'Bound shown handler reset new modal beneath existing surface');
    const focusEnds=await execute(`const modal=document.querySelector('#qvBookingModal');const nodes=[...modal.querySelectorAll('a[href],button,input:not([type=hidden]),select,textarea,[tabindex]')].filter(e=>e.tabIndex>=0&&!e.disabled&&e.getClientRects().length&&getComputedStyle(e).visibility!=='hidden');if(nodes.length<2)throw Error('Insufficient native trap controls');nodes[0].setAttribute('data-fixture-first','');nodes.at(-1).setAttribute('data-fixture-last','');nodes.at(-1).focus({preventScroll:true});return nodes.length;`);
    assert.ok(focusEnds>2);
    await keys(['\uE004']);assert.equal(await execute("return document.activeElement.hasAttribute('data-fixture-first')"),true,'Last-to-first native Tab wrap failed');
    await command('POST','/session/'+session+'/actions',{actions:[{type:'key',id:'keyboard',actions:[{type:'keyDown',value:'\uE008'},{type:'keyDown',value:'\uE004'},{type:'keyUp',value:'\uE004'},{type:'keyUp',value:'\uE008'}]}]});
    assert.equal(await execute("return document.activeElement.hasAttribute('data-fixture-last')"),true,'First-to-last native Shift+Tab wrap failed');
    await keys(['\uE00C']);await waitFor("!document.querySelector('#qvBookingModal').classList.contains('show')");
    assert.equal(await execute("return document.querySelector('#fixtureLowerModal').classList.contains('show')"),true,'Escape closed more than top modal');
    await new Promise(r=>setTimeout(r,350));
    assert.equal(await execute("return document.querySelector('#fixtureLowerModal').contains(document.activeElement)"),true,'Focus did not restore into lower surface');
    await execute(`window.__cancelHide=e=>e.preventDefault();document.querySelector('#fixtureLowerModal').addEventListener('hide.bs.modal',window.__cancelHide);`);
    await keys(['\uE00C']);assert.equal(await execute("return document.querySelector('#fixtureLowerModal').classList.contains('show')"),true,'Cancelled hide closed surface');
    await execute(`document.querySelector('#fixtureLowerModal').removeEventListener('hide.bs.modal',window.__cancelHide);`);
    await click('#fixtureLowerModal [data-bs-dismiss="modal"]');await waitFor("!document.querySelector('#fixtureLowerModal').classList.contains('show')");
    assert.deepEqual(await execute('return window.__fixtureErrors'),[]);
    // Component-level sheet lifecycle: real pointer/Enter openers and booking
    // controls, shared controller unchanged; not an authenticated CRM data flow.
    await execute(`const opener=document.createElement('button');opener.id='fixtureSheetOpener';opener.type='button';opener.textContent='Open synthetic sheet';document.body.append(opener);const sheet=document.createElement('aside');sheet.id='fixtureFocusSheet';sheet.setAttribute('data-legend-mobile-sheet','');sheet.setAttribute('aria-hidden','true');sheet.style.cssText='display:none;position:fixed;background:white;z-index:5100';sheet.innerHTML='<button type="button" id="fixtureSheetBooking" data-qv-booking-open>Open booking</button><button type="button" id="fixtureSheetClose" data-legend-sheet-close>Close sheet</button>';document.body.append(sheet);opener.addEventListener('click',()=>{sheet.style.removeProperty('display');sheet.setAttribute('aria-hidden','false');sheet.classList.add('open');});sheet.querySelector('#fixtureSheetClose').addEventListener('click',()=>{sheet.classList.remove('open');sheet.setAttribute('aria-hidden','true');sheet.style.display='none';});`);
    await waitFor("document.querySelector('#fixtureFocusSheet').dataset.legendSheetOpen==='false'");
    for(const width of [1180,393]){
      await command('POST','/session/'+session+'/goog/cdp/execute',{cmd:'Emulation.setDeviceMetricsOverride',params:{width,height:757,deviceScaleFactor:1,mobile:false}});
      for(const activation of ['pointer','Enter']){
        lifecycleDiagnostic={scenario:'modal-order-and-wrap',phase:'sheet-open',width,activation};
        if(activation==='pointer')await click('#fixtureSheetOpener');
        else {assert.equal(await execute("return document.activeElement.id"),'fixtureSheetOpener');await keys(['\uE007']);}
        await waitFor("document.querySelector('#fixtureFocusSheet').dataset.legendSheetOpen==='true'");
        assert.equal(await execute("return document.querySelector('#fixtureFocusSheet').hasAttribute('data-legend-modal-surface')"),false,'Sheet focus must not register modal geometry');
        lifecycleDiagnostic.phase='nested-booking';
        await waitFor(`(${sheetControlInteractable.toString()})((${sheetControlSnapshot.toString()})(document,window))`);
        lifecycleDiagnostic.control=await execute(`return (${sheetControlSnapshot.toString()})(document,window)`);
        lifecycleDiagnostic.bookingBefore=await execute(`return (${bookingLifecycleSnapshot.toString()})(document,window)`);
        const shownBefore=await execute("return window.__shown.filter(id=>id==='qvBookingModal').length");
        await click('#fixtureSheetBooking');
        await waitFor("document.querySelector('#qvBookingModal').classList.contains('show')&&window.__shown.filter(id=>id==='qvBookingModal').length>"+shownBefore);
        await waitFor(`(${bookingDialogSettled.toString()})(document)`);
        const hiddenBefore=await execute('return window.__bookingLifecycle.hidden');
        lifecycleDiagnostic.phase='nested-booking-close';lifecycleDiagnostic.hiddenBefore=hiddenBefore;
        await keys(['\uE00C']);
        await waitFor(`(${bookingHiddenComplete.toString()})((${bookingLifecycleSnapshot.toString()})(document,window),${hiddenBefore})`);
        lifecycleDiagnostic.bookingAfter=await execute(`return (${bookingLifecycleSnapshot.toString()})(document,window)`);
        lifecycleDiagnostic.phase='sheet-close';
        await click('#fixtureSheetClose');
        await waitFor("document.querySelector('#fixtureFocusSheet').dataset.legendSheetOpen==='false'&&document.activeElement.id==='fixtureSheetOpener'");
      }
    }
    lifecycleDiagnostic=undefined;
    await command('POST','/session/'+session+'/goog/cdp/execute',{cmd:'Emulation.setDeviceMetricsOverride',params:{width:1180,height:757,deviceScaleFactor:1,mobile:false}});
    await execute("document.querySelector('#fixtureFocusSheet').remove();document.querySelector('#fixtureSheetOpener').remove()");
    assert.deepEqual(await execute('return window.__fixtureErrors'),[],'Fixture errors in nested sheet focus');
    completedScenarios.add('modal-order-and-wrap');
    console.log('NATIVE_MODAL_ORDER PASS actual shown priority,forward/reverse boundary wraps,top-only Escape,focus restoration,cancelled hide,native close');
    // Locale and year rollover use the shipped Intl/calendar handlers unchanged.
    await command('POST','/session/'+session+'/goog/cdp/execute',{cmd:'Emulation.setLocaleOverride',params:{locale:'de-DE'}});
    await command('POST','/session/'+session+'/url',{url:'http://'+serverHost+'/authenticated.html'});
    await click('#fixtureBookingOpen');await waitFor("document.querySelector('[data-qv-booking-date]')&&document.querySelector('#qvBookSlots button')");
    await waitFor("document.querySelector('#qvBookingModal').classList.contains('show')&&window.__shown.includes('qvBookingModal')");
    await waitFor(`(${bookingDialogSettled.toString()})(document)`);
    const initialMonth=await execute("return document.querySelector('#qvBookingMonthLabel').textContent");
    const labels=[];
    for(let i=0;i<13;i++){
      const prior=await execute("return document.querySelector('#qvBookingMonthLabel').textContent");
      try { await click('#qvBookNextMonth'); } catch(error) {
        localeDiagnostic={scenario:'locale-month-rollover',iteration:i+1,target:'#qvBookNextMonth'};
        try { localeDiagnostic.control=await execute(`return (${bookingLocaleClickSnapshot.toString()})(document,window)`); }
        catch { localeDiagnostic.snapshotUnavailable=true; }
        console.log('NATIVE_LOCALE_CLICK_FAILURE '+JSON.stringify(localeDiagnostic));
        throw error;
      }
      await waitFor('document.querySelector("#qvBookingMonthLabel").textContent!=='+JSON.stringify(prior));
      labels.push(await execute("return document.querySelector('#qvBookingMonthLabel').textContent"));
    }
    assert.equal(new Set(labels).size,13);assert.notEqual(labels.at(-1).match(/\d{4}/)?.[0],initialMonth.match(/\d{4}/)?.[0],'Year rollover missing');
    assert.equal(await execute("return Intl.DateTimeFormat().resolvedOptions().locale.startsWith('de')"),true,'Requested locale not active');
    const localeBounds=await execute(`const e=document.querySelector('#qvBookingMonthLabel'),b=e.getBoundingClientRect(),p=e.parentElement.getBoundingClientRect();return {text:e.textContent,x:b.x,r:b.right,parent:{x:p.x,r:p.right},scroll:e.scrollWidth,client:e.clientWidth,labels:[...document.querySelectorAll('[data-qv-booking-date]')].map(e=>e.getAttribute('aria-label'))}`);
    assert.ok(localeBounds.x>=localeBounds.parent.x-1&&localeBounds.r<=localeBounds.parent.r+1&&localeBounds.scroll<=localeBounds.client+1,'Localized month label clipped');
    assert.ok(localeBounds.labels.every(label=>label&&/\d{4}/.test(label)),'Localized full date labels missing');
    assert.deepEqual(await execute('return window.__fixtureErrors'),[],'Fixture errors in locale-month-rollover');
    completedScenarios.add('locale-month-rollover');
    await command('POST','/session/'+session+'/goog/cdp/execute',{cmd:'Emulation.setLocaleOverride',params:{locale:''}});
    // Same open panel crosses the existing breakpoint in both directions.
    await command('POST','/session/'+session+'/url',{url:'http://'+serverHost+'/authenticated.html'});
    await command('POST','/session/'+session+'/goog/cdp/execute',{cmd:'Emulation.setDeviceMetricsOverride',params:{width:840,height:757,deviceScaleFactor:1,mobile:false}});
    await click('[data-legend-nav-toggle]');await waitFor("document.querySelector('[data-legend-global-nav]').classList.contains('mobile-open')");
    const navIdentity=await execute(`window.__navNodes=[...document.querySelectorAll('header .nav-link')];return window.__navNodes.length`);assert.ok(navIdentity>0);
    await command('POST','/session/'+session+'/goog/cdp/execute',{cmd:'Emulation.setDeviceMetricsOverride',params:{width:841,height:757,deviceScaleFactor:1,mobile:false}});
    await waitFor("!document.querySelector('[data-legend-global-nav]').classList.contains('mobile-open')&&!document.querySelector('[data-legend-global-nav]').hasAttribute('data-legend-mobile-nav-integrated')");
    assert.equal(await execute("return window.__navNodes.every(e=>e.isConnected&&document.querySelector('header').contains(e))"),true);
    await command('POST','/session/'+session+'/goog/cdp/execute',{cmd:'Emulation.setDeviceMetricsOverride',params:{width:840,height:757,deviceScaleFactor:1,mobile:false}});
    await click('[data-legend-nav-toggle]');await waitFor("document.querySelector('[data-legend-global-nav]').classList.contains('mobile-open')");
    assert.equal(await execute("return window.__navNodes.every(e=>e.isConnected&&e.getClientRects().length)"),true);
    await keys(['\uE00C']);await waitFor("!document.querySelector('[data-legend-global-nav]').classList.contains('mobile-open')");
    assert.deepEqual(await execute('return window.__fixtureErrors'),[],'Fixture errors in live-mobile-boundary');
    completedScenarios.add('live-mobile-boundary');
    // Long harmless identity text, then a controlled header-growth stimulus, proves
    // ResizeObserver updates canonical geometry. It is fixture input, not CSS shipped to an app.
    await command('POST','/session/'+session+'/goog/cdp/execute',{cmd:'Emulation.setDeviceMetricsOverride',params:{width:900,height:757,deviceScaleFactor:1,mobile:false}});
    await command('POST','/session/'+session+'/url',{url:'http://'+serverHost+'/authenticated.html'});
    await execute(`document.querySelector('.profile-name').textContent='Synthetic Alexandra Maximiliana Long Profile Name';`);
    const longProfile=await execute(`const e=document.querySelector('.profile-chip'),b=e.getBoundingClientRect();return {x:b.x,r:b.right}`);assert.ok(longProfile.x>=-1&&longProfile.r<=900+1,'Long profile escapes header');
    placementDiagnostic={scenario:'long-profile-and-offset',phase:'header-baseline',host:'authenticated',width:900};
    const settleHeaderRegion=async(before=null)=>{
      const until=Math.min(Date.now()+3000,deadline-10000);let previous=null,current=null;
      while(Date.now()<until){
        current=await execute(`return (${headerRegionSnapshot.toString()})(document,window)`);
        placementDiagnostic.previous=previous;placementDiagnostic.observation=current;
        if(headerRegionSettled(previous,current,before))return current;
        previous=current;await new Promise(resolve=>setTimeout(resolve,40));
      }
      assert.fail('Header/content/modal region did not settle: '+JSON.stringify(placementDiagnostic));
    };
    const offsetBefore=await settleHeaderRegion();
    placementDiagnostic.offsetBefore=offsetBefore;placementDiagnostic.phase='header-growth';
    await execute(`const h=document.querySelector('body>header');h.style.minHeight=(h.getBoundingClientRect().height+40)+'px'`);
    const offsetAfter=await settleHeaderRegion(offsetBefore);
    placementDiagnostic.offsetAfter=offsetAfter;
    assert.ok(offsetAfter.content>=offsetBefore.content+39,'Content did not track grown header');assert.ok(offsetAfter.clearance>=offsetAfter.header-1&&offsetAfter.area>=offsetAfter.clearance,'Modal region missed actual header');
    await click('#fixtureBookingOpen');await waitFor("document.querySelector('#qvBookingModal').classList.contains('show')");
    await waitFor("window.__shown.includes('qvBookingModal')");
    await waitFor(`(${bookingDialogSettled.toString()})(document)`);
    // At the accepted <=900px boundary the modal owns the viewport, not the
    // page-header clearance region. Header growth/content tracking above stays mandatory.
    placementDiagnostic={scenario:'long-profile-and-offset',host:'authenticated',width:900,offsetBefore,offsetAfter};
    placementDiagnostic.geometry=await execute(`return (${bookingScrollSnapshot.toString()})(document,window)`);
    placementDiagnostic.tokens=await execute(`const s=getComputedStyle(document.documentElement);return {clearance:parseFloat(s.getPropertyValue('--legend-modal-clearance-top')),area:parseFloat(s.getPropertyValue('--legend-modal-area-start')),safeHeight:parseFloat(s.getPropertyValue('--legend-modal-safe-height'))}`);
    assertMobileBookingPlacement(placementDiagnostic.geometry);
    placementDiagnostic=undefined;
    assert.deepEqual(await execute('return window.__fixtureErrors'),[],'Fixture errors in long-profile-and-offset');
    completedScenarios.add('long-profile-and-offset');
    await command('POST','/session/'+session+'/url',{url:'http://'+serverHost+'/client.html'});
    await command('POST','/session/'+session+'/goog/cdp/execute',{cmd:'Emulation.setDeviceMetricsOverride',params:{width:1180,height:757,deviceScaleFactor:1,mobile:false}});
    const openHome=async phase=>{
      lifecycleDiagnostic={scenario:'client-home-focus',phase};
      await click('.home-focus-trigger');await waitFor(`(${homeFocusOpenReady.toString()})(document,window)`);
    };
    const homeClosed="document.querySelector('#homeFocusHub').hidden&&document.activeElement.matches('.home-focus-trigger')";
    await openHome('initial');
    assert.equal(await execute("return document.querySelector('#homeFocusTitle').textContent"),'Synthetic Focus Area');
    await click('#homeFocusDismiss');await waitFor(homeClosed);
    await openHome('after-dismiss');await keys(['\uE00C']);await waitFor(homeClosed);
    await openHome('after-Escape');await click('#homeFocusClose');await waitFor(homeClosed);
    await openHome('after-explicit-Close');
    await command('POST','/session/'+session+'/actions',{actions:[{type:'key',id:'keyboard',actions:[{type:'keyDown',value:'\uE008'},{type:'keyDown',value:'\uE004'},{type:'keyUp',value:'\uE004'},{type:'keyUp',value:'\uE008'}]}]});
    assert.equal(await execute('return document.activeElement.id'),'homeFocusDismiss');
    await keys(['\uE007']);await waitFor(homeClosed);
    await openHome('after-keyboard-Back-To-Home');await click('#homeFocusClose');await waitFor(homeClosed);
    lifecycleDiagnostic=undefined;
    assert.deepEqual(await execute('return window.__fixtureErrors'),[],'Fixture errors in client-home-focus');
    completedScenarios.add('client-home-focus');
    assert.equal(records.length,hosts.length*widths.length,'Missing required viewport/host case');
    // No unknown HTTP requests: favicon is deliberately not a served route either.
    assert.deepEqual(rejected.filter(x=>x.url!=='/favicon.ico'),[],'Unexpected browser requests');
    // Existing 38 cases remain mandatory. These additional cases use actual source
    // components and the shipped editor's own materializer/navigation, not a Razor server.
    const quoteRoutes=Object.keys(manifest.quotes.routes);
    const metrics=async(width,height=932)=>command('POST','/session/'+session+'/goog/cdp/execute',{cmd:'Emulation.setDeviceMetricsOverride',params:{width,height,deviceScaleFactor:1,mobile:false}});
    const quoteGo=async(route,query)=>{
      quoteDiagnostic={label:route+query,phase:'navigation'};
      assert.ok(routes.has(route+query),'Quote fixture URL must be explicitly admitted: '+route+query);
      try{
      await command('POST','/session/'+session+'/url',{url:'http://'+serverHost+route+query});
      if(query.includes('legendMaterialize'))await waitFor('window.__quoteMaterialized!==null');
      else if(query.includes('public=1'))await waitFor('window.LEGEND_PUBLIC_CMS_RENDER_COMPLETE===true');
      else await waitFor("document.querySelector('#legend-cms-form-navigator select')?.options.length===10");
      await waitFor("[...document.querySelectorAll('main img')].every(n=>n.complete&&n.naturalWidth>0)");
      }catch(error){
        try{quoteDiagnostic.navigation=await execute("return {url:location.pathname+location.search,title:document.title,options:document.querySelector('#legend-cms-form-navigator select')?.options.length,errors:(window.__quoteErrors||[]).slice(-8),requests:(window.__quoteRequests||[]).slice(-8),body:document.body?.innerText.slice(0,800)}");}catch{quoteDiagnostic.snapshotUnavailable=true;}
        quoteDiagnostic.rejected=rejected.slice(-8);throw error;
      }
    };
    const inspectQuote=async(label,options={})=>{
      const value=await execute(quoteSnapshotScript);quoteDiagnostic={label,...value};
      if(!quoteGeometryValid(value,options))quoteDiagnostic.overflowDiagnostic=await execute(quoteOverflowDiagnosticScript);
      assert.ok(quoteGeometryValid(value,options),'Quote component geometry: '+JSON.stringify(quoteDiagnostic));
      assert.deepEqual(value.errors,[],'Quote fixture/runtime errors');assert.ok(value.requests.every(r=>r.method==='GET'),'Quote fixture must never submit/save/publish');
      assert.ok(value.controls.every(c=>quoteControlFits(c,value.canvas,value.panel,!!options.sidebar)),'Visible native control clipped or beneath sidebar');
      assert.equal(value.inputDocumentUnchanged,true,'Offline input document object changed (internal CMS preservation covered by website contract suite)');assert.equal(value.identity,true,'Native form/media node identity replaced');
      assert.deepEqual(value.duplicateIds,[],'Duplicate native IDs after reapply');assert.equal(value.nativeSteps,5);
      assert.ok(value.images.length>0&&value.images.every(i=>i.loaded),'Actual native banner assets required');
      quoteRecords.push({label,layout:value.layout,available:value.available,canvasOuter:value.canvasOuter,shell:value.shell,canvas:value.canvas,panel:value.panel,viewport:value.viewport,requestCount:value.requests.length});
      return value;
    };
    const settleQuoteCanvas=async(label)=>{
      const until=Math.min(Date.now()+3000,deadline-10000);let previous=null,current=null;
      while(Date.now()<until){
        current=await execute(quoteCanvasObservationScript);
        if(quoteCanvasSettled(previous,current))return;
        previous=current;await new Promise(resolve=>setTimeout(resolve,40));
      }
      quoteDiagnostic={label,stage:'canvas-settlement',observation:current,...await execute(quoteSnapshotScript)};
      assert.fail('Selection frame/canvas did not settle: '+JSON.stringify(quoteDiagnostic));
    };
    let selectedQuoteNode;
    const selectQuoteContent=async(label)=>{
      quoteDiagnostic={label,stage:'native-content-discovery',...await execute(quoteSnapshotScript)};
      // Discover real noninteractive native title/copy, then use WebDriver's
      // pointer click. The shipped selection owner may select its editable parent.
      const found=await execute(quoteSelectableContentScript);
      assert.ok(found,'Actual selectable native text missing: '+JSON.stringify(quoteDiagnostic));
      await command('POST','/session/'+session+'/element/'+found.node['element-6066-11e4-a52e-4f735466cecf']+'/click',{});
      selectedQuoteNode=found.target;
      assert.equal(await execute('return arguments[0].isConnected && arguments[0]===document.querySelector("main .legend-cms-selected")',[selectedQuoteNode]),true,'Native click did not select the exact discovered content owner');
      await waitFor(`document.querySelector('#legend-cms-breakpoint')?.disabled===false`);
    };
    const verifyBreakpoint=async(key,label)=>{
      assert.equal(await execute('return arguments[0]?.isConnected && arguments[0]===document.querySelector("main .legend-cms-selected")',[selectedQuoteNode]),true,'Selected native content identity changed');
      try {await waitFor(`(${quoteBreakpointReady.toString()})((()=>{const b=document.querySelector('#legend-cms-breakpoint'),c=document.querySelector('.legend-cms-preview');return {selected:!!document.querySelector('main .legend-cms-selected[data-cms-editable="true"]'),disabled:b?.disabled,value:b?.value,width:c?.style.width,maxWidth:c?.style.maxWidth};})(),${JSON.stringify(key)})`);} catch(error) {quoteDiagnostic={label,...await execute(quoteSnapshotScript)};throw error;}
      const value=await execute(quoteSnapshotScript);quoteDiagnostic={label,...value};
      assert.ok(quoteBreakpointReady(value.layout,key),'Unverified quote breakpoint: '+JSON.stringify(quoteDiagnostic));
    };
    const publicAtCanvas=async(route,targetWidth,label)=>{
      await metrics(Math.round(targetWidth));await quoteGo(route,'?public=1');
      let measured=await execute(quoteSnapshotScript);
      // One bounded setup correction for an actual classic scrollbar, not a
      // style override or repeated search for a passing width.
      if(Math.abs(measured.canvas.width-targetWidth)>1){
        const adjusted=Math.round(measured.viewport.width+targetWidth-measured.canvas.width);
        assert.ok(adjusted>=targetWidth&&adjusted<=targetWidth+40,'Unexpected public canvas chrome delta');
        await metrics(adjusted);await quoteGo(route,'?public=1');
      }
      const value=await inspectQuote(label,{desktop:targetWidth>=1024});
      assert.ok(Math.abs(value.canvas.width-targetWidth)<=1,'Effective public/Studio canvas width mismatch');
      return value;
    };
    for(const route of quoteRoutes){
      await metrics(1440);await quoteGo(route,'?legendEdit=ticket&legendMaterialize=1');
      await quoteGo(route,'?legendEdit=ticket');
      const baseline=await inspectQuote(route+':1440:base',{sidebar:true});
      for(let phase=0;phase<9;phase++){
        await click('#legend-cms-form-navigator select option[value="'+phase+'"]');
        const snap=await inspectQuote(route+':1440:phase:'+phase,{sidebar:true});
        assert.ok(quotePhaseValid(snap.phase,phase),'Actual source phase failed: '+JSON.stringify(snap.phase));
        assert.ok(Math.abs((snap.shell.y-snap.canvas.y+snap.canvasScrollTop)-(baseline.shell.y-baseline.canvas.y+baseline.canvasScrollTop))<=1,'Quote phase moved outer shell offset');
      }
      // Return to source-authored cold presentation before comparing with public.
      await click('#legend-cms-form-navigator select option[value="-1"]');
      const studio=await inspectQuote(route+':1440:restored',{sidebar:true});
      const publicValue=await publicAtCanvas(route,studio.canvas.width,route+':equal-canvas-public');
      assert.ok(Math.abs(publicValue.shell.width-studio.shell.width)<=1,'Public/Studio equal-canvas shell width differs');
      completedScenarios.add('quote-source:'+route.split('/').at(-1));
    }
    for(const route of ['/quote/life','/quote/disability','/quote/dental-vision-hearing']){
      for(const width of [1920,1214]){
        await metrics(width);await quoteGo(route,'?legendEdit=ticket');
        await selectQuoteContent(route+':'+width+':cold-base-selection');await verifyBreakpoint('base',route+':'+width+':base-ready');
        const before=await inspectQuote(route+':'+width+':base',{desktop:width-384>=1024,sidebar:true});
        await click('[data-editor-viewport="desktop"]');
        await verifyBreakpoint('desktop',route+':'+width+':desktop-ready');
        await inspectQuote(route+':'+width+':desktop',{desktop:before.canvas.width>=1024,sidebar:true});
        await click('#legend-cms-form-navigator select option[value="8"]');
        await selectQuoteContent(route+':'+width+':contact-phase8-selection');
        await click('[data-open="advanced"]');await click('[data-open="layout"]');
        await click('#legend-cms-breakpoint option[value="fixture-wide"]');
        await verifyBreakpoint('fixture-wide',route+':'+width+':named-ready');
        await inspectQuote(route+':'+width+':named',{desktop:before.canvas.width>=1024,sidebar:true});
        await click('#legend-cms-breakpoint option[value="base"]');
        await verifyBreakpoint('base',route+':'+width+':base-restored-ready');
        const baseRestored=await inspectQuote(route+':'+width+':base-restored',{desktop:before.canvas.width>=1024,sidebar:true});
        assert.ok(Math.abs(baseRestored.shell.width-before.shell.width)<=1,'Returning to verified Base changed source shell width');
        await click('#legend-cms-panel-toggle');
        await verifyBreakpoint('base',route+':'+width+':hidden-base-ready');
        await settleQuoteCanvas(route+':'+width+':sidebar-hidden');
        const hidden=await inspectQuote(route+':'+width+':sidebar-hidden',{desktop:true});assert.equal(hidden.panelHidden,true);
        await click('#legend-cms-panel-toggle');
        await verifyBreakpoint('base',route+':'+width+':restored-base-ready');
        await settleQuoteCanvas(route+':'+width+':sidebar-restored');
        const restored=await inspectQuote(route+':'+width+':sidebar-restored',{desktop:before.canvas.width>=1024,sidebar:true});
        assert.ok(Math.abs(restored.shell.width-baseRestored.shell.width)<=1,'Sidebar toggle changed source shell width');
        const publicValue=await publicAtCanvas(route,before.canvas.width,route+':'+width+':equal-canvas-public');
        assert.ok(Math.abs(publicValue.shell.width-before.shell.width)<=1,'Public/Studio equal-canvas responsive width differs');
      }
      await metrics(1440);await quoteGo(route,'?legendEdit=ticket&width=720');
      await inspectQuote(route+':tenant720',{sidebar:true,expectedWidth:720});
      // Effective viewport reduction exercises reflow, NOT actual 200% browser zoom.
      for(const [width,height] of [[607,466],[393,500]]){
        await metrics(width,height);await quoteGo(route,'?legendEdit=ticket');
        await click('#legend-cms-form-navigator select option[value="8"]');
        await click('#legend-cms-panel-toggle');
        await waitFor("document.body.classList.contains('legend-cms-panel-hidden')");
        const small=await inspectQuote(route+':small-canvas:'+width,{desktop:false});
        assert.ok(quotePhaseValid(small.phase,8),'Small fallback must expose actual Contact phase');
        const reachable=await execute(`const c=document.querySelector('.legend-cms-preview');const controls=[...document.querySelectorAll('main form button[type=submit]')].filter(e=>e.getClientRects().length&&getComputedStyle(e).visibility==='visible'&&!e.disabled);const last=controls.at(-1);if(!last)return null;last.scrollIntoView({block:'center',inline:'nearest'});const r=last.getBoundingClientRect(),b=c.getBoundingClientRect();return {x:r.x,right:r.right,y:r.y,bottom:r.bottom,left:b.x,canvasRight:b.right,top:b.y,canvasBottom:b.bottom,scroll:c.scrollTop};`);
        assert.ok(reachable&&reachable.x>=reachable.left-1&&reachable.right<=reachable.canvasRight+1&&reachable.bottom>reachable.top&&reachable.y<reachable.canvasBottom,'Essential source control unreachable after native scroll');
      }
    }
    assert.deepEqual(rejected,[],'No unexpected HTTP after quote scenarios');
    completedScenarios.add('quote-responsive-studio');

    // The real Protect layout modal was previously omitted from quote fixtures.
    // Cold and no-trigger pages prove native focus exclusion independently of JS.
    const reverseTab=()=>command('POST','/session/'+session+'/actions',{actions:[{type:'key',id:'keyboard',actions:[{type:'keyDown',value:'\uE008'},{type:'keyDown',value:'\uE004'},{type:'keyUp',value:'\uE004'},{type:'keyUp',value:'\uE008'}]}]});
    await metrics(1188,761);
    for (const mode of ['cold','no-trigger']) {
      await command('POST','/session/'+session+'/url',{url:'http://'+serverHost+'/scripture-'+mode+'.html'});
      assert.deepEqual(await execute("const m=document.getElementById('psalmsModal');return [m.hidden,m.inert,m.getAttribute('aria-hidden'),getComputedStyle(m).display]"),[true,true,'true','none']);
      await execute("document.getElementById('before').focus()");
      await keys(['\uE004']);assert.equal(await execute('return document.activeElement.id'),'after');
      await reverseTab();assert.equal(await execute('return document.activeElement.id'),'before');
      await keys(['\uE00C']);assert.equal(await execute('return document.activeElement.id'),'before','Closed Escape changed focus');
    }
    await command('POST','/session/'+session+'/url',{url:'http://'+serverHost+'/scripture-trigger.html'});
    for (const dismissal of ['escape','close','backdrop']) {
      // Bootstrap's :root smooth scrolling otherwise leaves this immediate
      // precondition at zero before the animation has advanced.
      await execute("window.scrollTo({top:250,left:0,behavior:'instant'});document.getElementById('psalmsTrigger').focus({preventScroll:true})");
      const scrollBefore=await execute('return window.scrollY');
      assert.ok(scrollBefore>0,'Scroll restoration requires an actually scrolled page');
      const openerRect=await execute("const r=document.getElementById('psalmsTrigger').getBoundingClientRect();return {top:r.top,bottom:r.bottom,height:r.height,viewport:innerHeight}");
      assert.ok(openerRect.height>0&&openerRect.top>=0&&openerRect.bottom<=openerRect.viewport,'Keyboard opener must be visible: '+JSON.stringify(openerRect));
      const scrollTrace=[];
      const assertScroll=async phase=>{
        const state=await execute("const r=document.getElementById('psalmsTrigger').getBoundingClientRect();return {scroll:scrollY,active:document.activeElement.id,hidden:document.getElementById('psalmsModal').hidden,openerTop:r.top,openerBottom:r.bottom}");
        scrollTrace.push({phase,...state});
        assert.equal(state.scroll,scrollBefore,'Scripture '+dismissal+' scroll ownership: '+JSON.stringify(scrollTrace));
      };
      await keys(['\uE007']);
      assert.equal(await execute("return !document.getElementById('psalmsModal').hidden && document.activeElement.classList.contains('scripture-card')"),true);
      await assertScroll('open');
      await keys(['\uE004']);assert.equal(await execute('return document.activeElement.id'),'psalmsClose');
      await assertScroll('Tab');
      await reverseTab();assert.equal(await execute('return document.activeElement.id'),'psalmsClose');
      await assertScroll('ShiftTab');
      // A repeated opener event must not replace the original focus/lock owner.
      await execute("document.getElementById('psalmsTrigger').click()");
      await assertScroll('repeated-open');
      if(dismissal==='escape')await keys(['\uE00C']);
      else if(dismissal==='close')await click('#psalmsClose');
      else await command('POST','/session/'+session+'/actions',{actions:[{type:'pointer',id:'pointer',parameters:{pointerType:'mouse'},actions:[{type:'pointerMove',origin:'viewport',x:2,y:2},{type:'pointerDown',button:0},{type:'pointerUp',button:0}]}]});
      await assertScroll('dismissed');
      assert.deepEqual(await execute("return [document.getElementById('psalmsModal').hidden,document.getElementById('psalmsModal').inert,document.activeElement.id,document.body.classList.contains('no-scroll'),window.scrollY]"),[true,true,'psalmsTrigger',false,scrollBefore]);
      await keys(['\uE004']);assert.equal(await execute('return document.activeElement.id'),'after');
    }
    await execute("document.body.classList.add('no-scroll');document.getElementById('psalmsTrigger').focus()");
    await keys(['\uE007','\uE00C']);
    assert.equal(await execute("return document.body.classList.contains('no-scroll')"),true,'Removed another scroll owner');
    // Exercise the source Q2 preview with the actual layout in place. Full
    // AgeRange-to-Q2 production sequencing is a separate live acceptance check.
    await quoteGo('/quote/disability','?legendEdit=ticket');
    await click('#legend-cms-form-navigator select option[value="2"]');
    await click('#legend-cms-panel-toggle');
    await waitFor("document.body.classList.contains('legend-cms-panel-hidden')");
    const originalAnswers=await execute("return [...document.querySelectorAll('main input')].map(e=>[e.name,e.value,e.checked])");
    assert.equal(await execute("const step=document.querySelector('[data-cms-native-step=\"1\"]');const radio=step?.querySelector('input[type=radio]');if(!radio||radio.disabled||!radio.getClientRects().length||getComputedStyle(radio).visibility!=='visible')return false;radio.focus();return document.querySelector('#legend-cms-form-navigator select').value==='2'&&document.activeElement===radio&&step.getClientRects().length>0"),true,'Actual Q2 radio must be visible, enabled and focused');
    for(let i=0;i<4;i++) {await keys(['\uE004']);assert.equal(await execute("return !!document.activeElement.closest('#psalmsModal')"),false);}
    for(let i=0;i<4;i++) {await reverseTab();assert.equal(await execute("return !!document.activeElement.closest('#psalmsModal')"),false);}
    assert.deepEqual(await execute("return [...document.querySelectorAll('main input')].map(e=>[e.name,e.value,e.checked])"),originalAnswers);
    assert.deepEqual(await execute('return window.__quoteErrors'),[]);
    assert.ok((await execute('return window.__quoteRequests')).every(r=>r.method==='GET'));
    assert.deepEqual(rejected,[],'Unexpected HTTP in scripture source fixtures');
    completedScenarios.add('protect-scripture-keyboard');

    // Bounded authored-header reproduction. Exercise the shipped controller;
    // never set data-open directly, save a draft, or submit a quote.
    for(const tenant of ['legend','other']){
      await metrics(1188,761);
      await quoteGo('/quote/disability','?legendEdit=fixture&header=1&tenant='+tenant);
      const headerSnapshot=()=>execute(`const h=document.querySelector('.site-header'),b=h.querySelector('.brand-wordmark'),n=h.querySelector('.nav'),t=h.querySelector('[data-public-nav-toggle]'),p=document.querySelector('.legend-cms-preview');const rect=e=>{const r=e.getBoundingClientRect();return {left:r.left,right:r.right,top:r.top,bottom:r.bottom,width:r.width,height:r.height};};return {header:rect(h),brand:rect(b),nav:rect(n),toggle:rect(t),canvas:rect(p),position:getComputedStyle(n).position,headerPosition:getComputedStyle(h).position,open:n.dataset.open,expanded:t.getAttribute('aria-expanded'),font:getComputedStyle(b.querySelector('strong')).fontSize,textFits:b.querySelector('strong').scrollWidth<=b.querySelector('strong').clientWidth+1,textRect:rect(b.querySelector('strong')),undo:document.querySelector('#legend-cms-undo').disabled,unchanged:JSON.stringify(window.__quoteDocumentObject)===window.__quoteDocument,identity:window.__quoteOriginalNodes.every(e=>e.isConnected)};`);
      const assertCompact=async()=>{
        const v=await headerSnapshot();quoteDiagnostic={scenario:'studio-header-projection',tenant,...v};
        assert.equal(v.headerPosition,'sticky');assert.equal(v.position,'absolute');
        assert.equal(v.font,tenant==='other'?'32px':'44px');assert.ok(v.textFits&&v.textRect.width>0&&v.textRect.height>0,'Authored wordmark must remain legible without truncation');
        assert.ok(v.brand.left>=v.header.left-1&&v.brand.right<=v.toggle.left+1);
        assert.ok(v.brand.top>=v.header.top-1&&v.brand.bottom<=v.header.bottom+1);
        assert.ok(v.toggle.width>=44&&v.toggle.height>=44);
        assert.ok(v.unchanged&&v.identity&&v.undo);return v;
      };
      await assertCompact();
      for(const action of ['pointer','Enter','Space']){
        if(action==='pointer')await click('[data-public-nav-toggle]');
        else {await execute("document.querySelector('[data-public-nav-toggle]').focus()");await keys([action==='Enter'?'\uE007':' ']);}
        await waitFor("document.querySelector('.site-header .nav').dataset.open==='true'");
        const open=await assertCompact();assert.equal(open.expanded,'true');assert.ok(open.nav.top>=open.header.bottom-1);
        await keys(['\uE00C']);await waitFor("document.querySelector('.site-header .nav').dataset.open==='false'");
        assert.equal(await execute("return document.activeElement.hasAttribute('data-public-nav-toggle')"),true);
      }
      for(const width of [1363,1364,1365,1188]){
        await metrics(width,761);await settleQuoteCanvas('header:'+width);
        const v=await headerSnapshot();
        if(v.canvas.width<=980)await assertCompact();
        else assert.ok(v.brand.width<v.canvas.width*.2,'Expanded Base restores authored14% width');
      }
      await click('#legend-cms-form-navigator select option[value="2"]');
      const formState=()=>execute("return {step:document.querySelector('#legend-cms-form-navigator select').value,answers:[...document.querySelectorAll('main input,main select,main textarea')].map(e=>[e.id,e.value,e.checked]),hidden:[...document.querySelectorAll('main [data-cms-native-step]')].map(e=>[e.dataset.cmsNativeStep,e.hidden])}");
      const beforeSidebar=await formState();
      await click('#legend-cms-panel-toggle');await settleQuoteCanvas('header:sidebar-hidden');
      assert.ok((await headerSnapshot()).brand.width<240);
      await click('#legend-cms-panel-toggle');await settleQuoteCanvas('header:sidebar-restored');await assertCompact();
      assert.deepEqual(await formState(),beforeSidebar,'Sidebar projection must preserve the actual selected step and answers');
      assert.ok((await execute('return window.__quoteRequests')).every(r=>r.method==='GET'));
      assert.deepEqual(await execute('return window.__quoteErrors'),[]);
    }
    completedScenarios.add('studio-header-projection');

    requireCompleteScenarios(requiredScenarios,completedScenarios);
    acceptanceComplete=true;
  } catch(error) {if(lifecycleDiagnostic){try{lifecycleDiagnostic.bookingFailure=await execute(`return (${bookingLifecycleSnapshot.toString()})(document,window)`);if(lifecycleDiagnostic.phase==='nested-booking')lifecycleDiagnostic.control=await execute(`return (${sheetControlSnapshot.toString()})(document,window)`);lifecycleDiagnostic.state=await execute(`return {activeId:document.activeElement?.id||'',surfaces:['homeFocusHub','fixtureFocusSheet','qvBookingModal'].map(id=>{const e=document.getElementById(id);return e?{id,hidden:e.hidden,ariaHidden:e.getAttribute('aria-hidden'),open:e.classList.contains('open'),show:e.classList.contains('show'),rects:e.getClientRects().length}:null})}`);}catch{lifecycleDiagnostic.snapshotUnavailable=true;}console.log('NATIVE_LIFECYCLE_FAILURE '+JSON.stringify(lifecycleDiagnostic));}if(placementDiagnostic)console.log('NATIVE_MOBILE_PLACEMENT_FAILURE '+JSON.stringify(placementDiagnostic));if(scrollDiagnostic)console.log('NATIVE_SCROLL_FAILURE '+JSON.stringify(scrollDiagnostic));commandFailure=error.commandFailure;versionProbeFailure=error.versionProbe;failure=String(error.stack||error);throw error;} finally {
    clearTimeout(watchdog);let cleanupError;
    try{await cleanup();}catch(error){cleanupError=error;acceptanceComplete=false;failure=(failure||'')+' Cleanup failed: '+error.message;}
    removeCancellation();
    const report={status:acceptanceComplete?'PASS':'INCOMPLETE_OR_FAILED',failure,versionProbeFailure,commandFailure,scrollDiagnostic,localeDiagnostic,placementDiagnostic,lifecycleDiagnostic,quoteDiagnostic,quoteRecords,checkout:manifest?.checkout,nodeVersion:process.version,driverBindings,requiredScenarios,completedScenarios:[...completedScenarios],image:process.env.ImageVersion,chromeVersion,driverVersion,manifest,completed:records.length,required:hosts.length*widths.length,records,limits:['Component fixture; no Razor/server/authentication/business/API proof','System font fallback; no pixel-identical production claim','Live journeys and native safe-area device inset remain required']};
    console.log('NATIVE_COMPONENT_REPORT '+JSON.stringify(report));
    if(process.env.GITHUB_STEP_SUMMARY)await appendFile(process.env.GITHUB_STEP_SUMMARY,'\nNative component rendering: '+records.length+'/'+hosts.length*widths.length+' cases completed. Checkout '+(manifest?.checkout||'UNPROVEN')+'. See job log for exact inputs and assertions.\n');
    if(cleanupError)throw cleanupError;
  }
});
