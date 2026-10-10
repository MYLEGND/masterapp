import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

const runtime=readFileSync(new URL('../../Legend-Design/legend-public-inquiry.js',import.meta.url),'utf8');
const css=readFileSync(new URL('../../SHARED/WebsitePlatform/public-inquiry-form.css',import.meta.url),'utf8');

function fixture(transport) {
  const listeners=new Map(), calls=[];
  let html='<span>Get a quote</span>', label='Get a quote', resetCount=0;
  const button={
    dataset:{},disabled:false,
    get innerHTML(){return html;},
    set innerHTML(v){html=v;label=v.includes('Get a quote')?'Get a quote':v;},
    get textContent(){return label;},
    set textContent(v){html=v;label=v;},
    setAttribute(k,v){this[k]=v;},
    removeAttribute(k){if(k==='data-legend-inquiry-state')delete this.dataset.legendInquiryState;else delete this[k];}
  };
  const status={dataset:{},textContent:''};
  const form={
    dataset:{formKey:'website_inquiry'},
    addEventListener(k,v){listeners.set(k,v);},
    reportValidity(){return true;},
    querySelector(k){return k==='[type="submit"]'?button:k==='[role="status"]'?status:null;},
    querySelectorAll(){return [];},
    reset(){resetCount++;}
  };
  const values={FirstName:'Jane',LastName:'Doe',Phone:'6025550199',Email:'jane@example.com',Message:'Please send a quote',consent:'on'};
  class FormFields {get(k){return values[k]??null;}}
  runInNewContext(runtime,{
    window:{LEGEND_PUBLIC_CMS_CONTEXT:{apiBase:'https://masterapp-protect.azurewebsites.net'},addEventListener(){}},
    document:{cookie:'',querySelectorAll(q){return q.includes('[data-website-inquiry]')?[form]:[];}},
    location:{search:'',pathname:'/contact',origin:'https://business.example'},
    URL,URLSearchParams,FormData:FormFields,CSS:{escape:x=>x},
    crypto:{randomUUID:()=> '11111111-1111-4111-8111-111111111111'},
    sessionStorage:{getItem(){return null;}},
    fetch:(url,options)=>{calls.push({url,options});return transport(url,options);}
  });
  return {button,status,calls,submit:()=>listeners.get('submit')({preventDefault(){}}),
    edit:()=>listeners.get('input')(),get resets(){return resetCount;}};
}

test('same submit button displays sending then green saved confirmation, without duplicate POST',async()=>{
  let complete;
  const f=fixture(()=>new Promise(resolve=>{complete=resolve;}));
  const pending=f.submit();
  assert.equal(f.button.textContent,'Sending inquiry…');
  assert.equal(f.button.dataset.legendInquiryState,'sending');
  assert.equal(f.button.disabled,true);
  await f.submit();
  assert.equal(f.calls.length,1);
  complete({ok:true,json:async()=>({accepted:true,notificationSent:false})});
  await pending;
  assert.equal(f.button.textContent,'✓ Inquiry sent');
  assert.equal(f.button.dataset.legendInquiryState,'sent');
  assert.equal(f.button.disabled,true);
  assert.equal(f.resets,1);
  assert.equal(f.status.dataset.legendInquiryAnnouncement,'true');
  assert.match(f.status.textContent,/received/);
  assert.match(css,/background:#147d49/);
  assert.match(css,/clip-path:inset\(50%\)/);
  f.edit();
  assert.equal(f.button.textContent,'Get a quote');
  assert.equal(f.button.disabled,false);
});

test('failed request keeps visitor data, visibly offers retry and uses original submission ID',async()=>{
  let attempts=0;
  const f=fixture(async()=>++attempts===1
    ? Promise.reject(new Error('network uncertain'))
    : {ok:true,json:async()=>({accepted:true})});
  await f.submit();
  assert.equal(f.button.dataset.legendInquiryState,'error');
  assert.equal(f.button.textContent,'Not confirmed — retry');
  assert.equal(f.button.disabled,false);
  assert.equal(f.resets,0);
  await f.submit();
  assert.equal(attempts,2);
  assert.equal(JSON.parse(f.calls[0].options.body).submissionId,JSON.parse(f.calls[1].options.body).submissionId);
  assert.equal(f.button.dataset.legendInquiryState,'sent');
});
