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
  let manifest,chromeVersion,driverVersion,checked,driverBindings,versionProbeFailure;
  const requiredScenarios=[...hosts.flatMap(host=>widths.map(width=>'viewport:'+host+':'+width)),'modal-order-and-wrap','locale-month-rollover','long-profile-and-offset','live-mobile-boundary','client-home-focus'];
  const completedScenarios=new Set();
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
  const chrome = '/usr/bin/google-chrome';
  const driverBinary = process.env.CHROMEWEBDRIVER ? path.join(process.env.CHROMEWEBDRIVER,'chromedriver') : 'chromedriver';
  chromeVersion=await binaryVersion(chrome,remaining,setupAbort.signal);driverVersion=await binaryVersion(driverBinary,remaining,setupAbort.signal);
  assert.equal(chromeVersion.match(/\d+\./)[0],driverVersion.match(/\d+\./)[0],'Installed browser/driver major versions must match');
    remaining();
    server=createServer((req,res)=>{
      if(!allowedRequest(req,serverHost,routes)){
        rejected.push({method:req.method,url:req.url,host:req.headers.host});res.writeHead(404);res.end();return;
      }
      res.writeHead(200,{'Content-Type':'text/html; charset=utf-8','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});res.end(routes.get(req.url));
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
      const childBounds=await execute(`const body=document.querySelector('.qv-booking-body').getBoundingClientRect();const selectors=['.qv-booking-slot-card','.qv-booking-slot-focus','.qv-booking-selection-meta','.qv-booking-foot','#qvBookStatus','#btnBookAppointment','#qvBookSlots button'];return selectors.map(selector=>{const e=document.querySelector(selector);if(!e)throw Error('Missing critical child:'+selector);const b=e.getBoundingClientRect();return {selector,x:b.x,r:b.right,w:b.width,client:e.clientWidth,scroll:e.scrollWidth,body:{x:body.x,r:body.right}}});`);
      for(const child of childBounds){assert.ok(child.x>=child.body.x-1&&child.r<=child.body.r+1,'Critical child escapes body:'+JSON.stringify(child));assert.ok(child.scroll<=child.client+1,'Critical child text/track clipped:'+JSON.stringify(child));}

      const before=await execute(`const box=e=>{const b=e.getBoundingClientRect();return {x:b.x,y:b.y,w:b.width,h:b.height,r:b.right,b:b.bottom}};const body=document.querySelector('.qv-booking-body');return {body:box(body),head:box(document.querySelector('.qv-booking-head')),content:box(document.querySelector('.qv-booking-content')),action:box(document.querySelector('#btnBookAppointment')),nav:[...document.querySelectorAll('.qv-booking-calendar-nav')].map(box),dates:[...document.querySelectorAll('[data-qv-booking-date]')].map(box),grid:box(document.querySelector('#qvBookingCalendar')),client:body.clientHeight,scroll:body.scrollHeight,horizontal:body.scrollWidth-body.clientWidth,errors:window.__fixtureErrors};`);
      assert.equal(new Set(before.dates.map(d=>Math.round(d.x*10))).size,7,'Calendar must retain exactly seven distinct date columns');
      assert.deepEqual(before.errors,[]);assert.ok(before.horizontal<=1,'Body horizontal overflow');
      for(const n of before.nav)assert.ok(n.w>=43.9&&n.h>=43.9,'Booking navigation target below44');
      for(const d of before.dates){if(width>=393)assert.ok(d.w>=43.9&&d.h>=43.9,'Date target below44 at admitted393px/desktop widths');assert.ok(d.x>=before.grid.x-1&&d.r<=before.grid.r+1,'Date escapes seven-column grid');}
      await wheel('.qv-booking-body',6000);await new Promise(r=>setTimeout(r,80));
      const after=await execute(`const body=document.querySelector('.qv-booking-body'),a=document.querySelector('#btnBookAppointment').getBoundingClientRect(),h=document.querySelector('.qv-booking-head').getBoundingClientRect(),b=body.getBoundingClientRect();return {scroll:body.scrollTop,action:{top:a.top,bottom:a.bottom},body:{top:b.top,bottom:b.bottom},head:h.top};`);
      assert.ok(before.scroll<=before.client+1||after.scroll>0,'Native wheel did not scroll body');
      assert.ok(after.action.top>=after.body.top-1&&after.action.bottom<=after.body.bottom+1,'Final action clipped');
      assert.ok(Math.abs(after.head-before.head.y)<=1,'Header moved with body scrolling');
      assert.ok(after.head>=-1&&before.head.b<=(width<600?640:757)+1,'Header inaccessible');
      await keys(['\uE004']);assert.equal(await execute("return document.querySelector('#qvBookingModal').contains(document.activeElement)"),true,'Tab focus left modal');
      await keys(['\uE00C']);await waitFor("!document.querySelector('#qvBookingModal').classList.contains('show')");
      assert.deepEqual(await execute('return window.__fixtureErrors'),[]);
      completedScenarios.add('viewport:'+host+':'+width);
      records.push({host,width,before,after,mobileExploreMeasurement,status:'PASS',target44Required:width>=393,target44Observed:before.dates.every(d=>d.w>=43.9&&d.h>=43.9)});
      console.log(JSON.stringify({host,width,status:'PASS'}));
    }
    // Real Bootstrap fade/MutationObserver/shown ordering and canonical bound layers.
    await command('POST','/session/'+session+'/goog/cdp/execute',{cmd:'Emulation.setDeviceMetricsOverride',params:{width:1180,height:757,deviceScaleFactor:1,mobile:false}});
    await command('POST','/session/'+session+'/url',{url:'http://'+serverHost+'/authenticated.html'});
    await execute(`window.__shown=[];document.addEventListener('shown.bs.modal',e=>window.__shown.push(e.target.id));const original=document.querySelector('#qvBookingModal'),lower=original.cloneNode(true);lower.id='fixtureLowerModal';lower.removeAttribute('data-legend-modal-bound');lower.querySelectorAll('[id]').forEach(e=>e.id='fixtureLower-'+e.id);lower.setAttribute('aria-labelledby','fixtureLower-qvBookingTitle');const launch=lower.querySelector('.qv-booking-submit');launch.setAttribute('data-qv-booking-open','');launch.id='fixtureLowerLaunch';launch.textContent='Open nested booking';document.body.append(lower);window.LegendModal.bind(lower.id,{modalZ:1115,backdropZ:1110});window.__lower=bootstrap.Modal.getOrCreateInstance(lower);window.__lower.show();`);
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
    completedScenarios.add('modal-order-and-wrap');
    console.log('NATIVE_MODAL_ORDER PASS actual shown priority,forward/reverse boundary wraps,top-only Escape,focus restoration,cancelled hide,native close');
    // Locale and year rollover use the shipped Intl/calendar handlers unchanged.
    await command('POST','/session/'+session+'/goog/cdp/execute',{cmd:'Emulation.setLocaleOverride',params:{locale:'de-DE'}});
    await command('POST','/session/'+session+'/url',{url:'http://'+serverHost+'/authenticated.html'});
    await click('#fixtureBookingOpen');await waitFor("document.querySelector('[data-qv-booking-date]')&&document.querySelector('#qvBookSlots button')");
    const initialMonth=await execute("return document.querySelector('#qvBookingMonthLabel').textContent");
    const labels=[];
    for(let i=0;i<13;i++){
      const prior=await execute("return document.querySelector('#qvBookingMonthLabel').textContent");
      await click('#qvBookNextMonth');await waitFor('document.querySelector("#qvBookingMonthLabel").textContent!=='+JSON.stringify(prior));
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
    const offsetBefore=await execute(`return {clearance:parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--legend-modal-clearance-top')),content:document.querySelector('.layout-content').getBoundingClientRect().top,header:document.querySelector('body>header').getBoundingClientRect().height}`);
    await execute(`const h=document.querySelector('body>header');h.style.minHeight=(h.getBoundingClientRect().height+40)+'px'`);
    await waitFor('parseFloat(getComputedStyle(document.documentElement).getPropertyValue("--legend-modal-clearance-top"))>'+offsetBefore.clearance);
    const offsetAfter=await execute(`return {clearance:parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--legend-modal-clearance-top')),content:document.querySelector('.layout-content').getBoundingClientRect().top,header:document.querySelector('body>header').getBoundingClientRect().bottom,area:parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--legend-modal-area-start'))}`);
    assert.ok(offsetAfter.content>=offsetBefore.content+39,'Content did not track grown header');assert.ok(offsetAfter.clearance>=offsetAfter.header-1&&offsetAfter.area>=offsetAfter.clearance,'Modal region missed actual header');
    await click('#fixtureBookingOpen');await waitFor("document.querySelector('#qvBookingModal').classList.contains('show')");
    const modalTop=await execute("return document.querySelector('.qv-booking-head').getBoundingClientRect().top");assert.ok(modalTop>=offsetAfter.header-1,'Modal under grown header');
    assert.deepEqual(await execute('return window.__fixtureErrors'),[],'Fixture errors in long-profile-and-offset');
    completedScenarios.add('long-profile-and-offset');
    await command('POST','/session/'+session+'/url',{url:'http://'+serverHost+'/client.html'});
    await command('POST','/session/'+session+'/goog/cdp/execute',{cmd:'Emulation.setDeviceMetricsOverride',params:{width:1180,height:757,deviceScaleFactor:1,mobile:false}});
    await click('.home-focus-trigger');await waitFor("document.querySelector('#homeFocusHub').classList.contains('open')&&document.querySelector('#homeFocusHub').contains(document.activeElement)");
    assert.equal(await execute("return document.querySelector('#homeFocusTitle').textContent"),'Synthetic Focus Area');
    await click('#homeFocusDismiss');await waitFor("document.querySelector('#homeFocusHub').hidden&&document.activeElement.matches('.home-focus-trigger')");
    await click('.home-focus-trigger');await waitFor("document.querySelector('#homeFocusHub').classList.contains('open')");
    await keys(['\uE00C']);await waitFor("document.querySelector('#homeFocusHub').hidden&&document.activeElement.matches('.home-focus-trigger')");
    await click('.home-focus-trigger');await waitFor("document.querySelector('#homeFocusHub').classList.contains('open')");
    await click('#homeFocusClose');await waitFor("document.querySelector('#homeFocusHub').hidden&&document.activeElement.matches('.home-focus-trigger')");
    assert.deepEqual(await execute('return window.__fixtureErrors'),[],'Fixture errors in client-home-focus');
    completedScenarios.add('client-home-focus');
    assert.equal(records.length,hosts.length*widths.length,'Missing required viewport/host case');
    // No unknown HTTP requests: favicon is deliberately not a served route either.
    assert.deepEqual(rejected.filter(x=>x.url!=='/favicon.ico'),[],'Unexpected browser requests');
    requireCompleteScenarios(requiredScenarios,completedScenarios);
    acceptanceComplete=true;
  } catch(error) {commandFailure=error.commandFailure;versionProbeFailure=error.versionProbe;failure=String(error.stack||error);throw error;} finally {
    clearTimeout(watchdog);let cleanupError;
    try{await cleanup();}catch(error){cleanupError=error;acceptanceComplete=false;failure=(failure||'')+' Cleanup failed: '+error.message;}
    removeCancellation();
    const report={status:acceptanceComplete?'PASS':'INCOMPLETE_OR_FAILED',failure,versionProbeFailure,commandFailure,checkout:manifest?.checkout,nodeVersion:process.version,driverBindings,requiredScenarios,completedScenarios:[...completedScenarios],image:process.env.ImageVersion,chromeVersion,driverVersion,manifest,completed:records.length,required:hosts.length*widths.length,records,limits:['Component fixture; no Razor/server/authentication/business/API proof','System font fallback; no pixel-identical production claim','Live journeys and native safe-area device inset remain required']};
    console.log('NATIVE_COMPONENT_REPORT '+JSON.stringify(report));
    if(process.env.GITHUB_STEP_SUMMARY)await appendFile(process.env.GITHUB_STEP_SUMMARY,'\nNative component rendering: '+records.length+'/'+hosts.length*widths.length+' cases completed. Checkout '+(manifest?.checkout||'UNPROVEN')+'. See job log for exact inputs and assertions.\n');
    if(cleanupError)throw cleanupError;
  }
});
