import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {JSDOM} from 'jsdom';
const source = readFileSync(new URL('../../Legend-Design/legend-public-inquiry.js', import.meta.url), 'utf8');
const tick = () => new Promise(resolve => setTimeout(resolve, 0));
function fixture() {
  const dom = new JSDOM('<form data-website-inquiry><input name="FirstName" value="Test"><input name="Email" value="test@example.test"><button type="submit">Send</button><p role="status"></p></form>', {url:'https://www.mylegnd.com/contact', runScripts:'outside-only'});
  const w = dom.window;
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
    assert.match(f.w.document.querySelector('[role=status]').textContent, new RegExp(original.submissionId));
    let confirmed=0;
    f.w.legendFormTracking={markSubmitted:()=>confirmed++};
    f.w.fetch=async()=>({ok:true,json:async()=>({accepted:true})});
    f.submit(); await tick();
    assert.equal(confirmed,1);
    assert.equal(f.w.document.querySelector('[role=status]').textContent,'Your inquiry has been sent.');
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
