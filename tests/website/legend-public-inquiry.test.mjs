import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {JSDOM} from 'jsdom';
import {publicInquiryForm} from '../../SHARED/WebsitePlatform/public-inquiry-form.mjs';
const source = readFileSync(new URL('../../Legend-Design/legend-public-inquiry.js', import.meta.url), 'utf8');
const tick = () => new Promise(resolve => setTimeout(resolve, 0));
function fixture() {
  const dom = new JSDOM(publicInquiryForm(), {url:'https://www.mylegnd.com/contact', runScripts:'outside-only'});
  const w = dom.window;
  for (const [name,value] of Object.entries({FirstName:'Test',LastName:'Visitor',Phone:'6025550199',Email:'test@example.test',Message:'Please contact me'}))
    w.document.querySelector(`[name="${name}"]`).value=value;
  w.document.querySelector('[name="consent"]').checked=true;
  const calls = [];
  let attribution = 'first';
  w.LegendAnalytics = {ids:{getAttribution:() => ({utmSource:attribution})}};
  w.fetch = async (_url, options) => { calls.push(options); throw new Error('uncertain'); };
  w.eval(source);
  const form = w.document.querySelector('form');
  return {w, calls, form, submit:() => form.dispatchEvent(new w.Event('submit', {cancelable:true})), attribution:value=>attribution=value, close:()=>w.close()};
}
test('uncertain inquiry retries reuse request identity and attribution; success alone confirms lifecycle', async()=>{
  const f=fixture();
  try {
    f.submit(); await tick();
    const original=JSON.parse(f.calls[0].body);
    f.attribution('changed');
    f.submit(); await tick();
    assert.deepEqual(JSON.parse(f.calls[1].body),original);
    assert.match(f.w.document.querySelector('[role=status]').textContent, /could not be confirmed/);
    let confirmed=0;
    f.w.legendFormTracking={markSubmitted:()=>confirmed++};
    f.w.fetch=async()=>({ok:true,json:async()=>({accepted:true})});
    f.submit(); await tick();
    assert.equal(confirmed,1);
    assert.equal(f.w.document.querySelector('[role=status]').textContent,'Your inquiry has been received.');
  } finally {f.close();}
});
test('pending inquiry cannot dispatch twice and timeout leaves a retryable request', async()=>{
  const f=fixture();
  try {
    let abort;
    f.w.setTimeout=callback=>{abort=callback;return 1;};
    f.w.clearTimeout=()=>{};
    f.w.fetch=(_url,options)=>{
      f.calls.push(options);
      return new Promise((_resolve,reject)=>options.signal.addEventListener('abort',()=>reject(new Error('timeout'))));
    };
    f.submit();f.submit();
    assert.equal(f.calls.length,1);
    abort();await tick();
    assert.equal(f.form.querySelector('button').disabled,false);
    const first=f.calls[0].body;
    f.submit();assert.equal(f.calls[1].body,first);
    abort();await tick();
  } finally {f.close();}
});

test('nonboolean accepted receipt never confirms inquiry lifecycle', async()=>{
  const f=fixture();
  try {
    let confirmed=0;
    f.w.legendFormTracking={markSubmitted:()=>confirmed++};
    f.w.fetch=async()=>({ok:true,json:async()=>({accepted:'false'})});
    f.submit(); await tick();
    assert.equal(confirmed,0);
    assert.match(f.w.document.querySelector('[role=status]').textContent,/could not be confirmed/);
    assert.equal(f.form.querySelector('button').disabled,false);
  } finally {f.close();}
});

for (const [label,response] of [
  ['HTTP error',{ok:false,json:async()=>({accepted:true})}],
  ['boolean false',{ok:true,json:async()=>({accepted:false})}],
  ['missing acceptance',{ok:true,json:async()=>({})}],
  ['string true',{ok:true,json:async()=>({accepted:'true'})}],
  ['object acceptance',{ok:true,json:async()=>({accepted:{}})}],
  ['malformed JSON',{ok:true,json:async()=>{throw new SyntaxError('fixture malformed')}}],
  ['null body',{ok:true,json:async()=>null}]
]) test(`${label} preserves visitor data and retry identity without false completion`,async()=>{
  const f=fixture();
  try {
    const button=f.form.querySelector('button');
    let confirmed=0, metaCompleted=0,resets=0;
    f.w.legendFormTracking={markSubmitted:()=>confirmed++};
    f.w.LEGEND_PUBLIC_META_SESSION={markSubmitted:()=>metaCompleted++};
    f.form.addEventListener('reset',()=>resets++);
    f.w.fetch=async(_url,options)=>{f.calls.push(options);return response;};
    f.submit();await tick();
    assert.equal(confirmed,0);assert.equal(metaCompleted,0);assert.equal(resets,0);
    assert.equal(button.dataset.legendInquiryState,'error');assert.equal(button.disabled,false);
    assert.equal(f.form.querySelector('[name=Email]').value,'test@example.test');
    const initial=JSON.parse(f.calls[0].body);
    f.w.fetch=async(_url,options)=>{f.calls.push(options);return {ok:true,json:async()=>({accepted:true,notificationSent:false})};};
    f.submit();await tick();
    assert.deepEqual(JSON.parse(f.calls[1].body),initial);
    assert.equal(confirmed,1);assert.equal(metaCompleted,1);assert.equal(resets,1);
    assert.equal(f.form.querySelector('button'),button);
    assert.equal(button.dataset.legendInquiryState,'sent');assert.equal(button.textContent,'✓ Inquiry sent');
    assert.equal(button.disabled,true);assert.equal(button.getAttribute('aria-busy'),'false');
  } finally {f.close();}
});

test('repeated initialization and double clicks retain one pending request and one confirmed lifecycle',async()=>{
  const f=fixture();
  try {
    let resolve,confirmed=0;
    f.w.legendFormTracking={markSubmitted:()=>confirmed++};
    f.w.eval(source);f.w.dispatchEvent(new f.w.Event('legend:website-content-rendered'));
    f.w.fetch=(_url,options)=>{f.calls.push(options);return new Promise(done=>{resolve=done;});};
    f.submit();f.submit();assert.equal(f.calls.length,1);
    assert.equal(f.form.querySelector('button').dataset.legendInquiryState,'sending');
    resolve({ok:true,json:async()=>({accepted:true})});await tick();
    f.submit();await tick();assert.equal(f.calls.length,1);assert.equal(confirmed,1);
  } finally {f.close();}
});
