import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {JSDOM} from 'jsdom';

const source = readFileSync(new URL('../../SHARED/WebsitePlatform/legend-public-cms.js', import.meta.url), 'utf8');
const frameSource = source.slice(source.indexOf('  const nativeFormFrames ='), source.indexOf('  function restoreNativeFormContinuations()'));

// Deterministic layout inputs exercise the shipped runtime state machine.
// These are not browser geometry/no-clipping acceptance; that remains in CI.
function fixture({id='autoQuoteForm', nested=false, ambiguous=false, site='protect', materialize=false}={}) {
  const body = '<input value="retained"><div class="mini-card">nested content</div>';
  const step = index => nested
    ? `<div data-cms-native-step="${index}"><div class="section-card">${body}</div>${ambiguous?'<div class="section-card"></div>':''}</div>`
    : `<div class="section-card" data-cms-native-step="${index}">${body}</div>`;
  const dom = new JSDOM(`<main><form id="${id}">${step(1)}${step(2)}</form><div class="section-card" id="unrelated"></div></main>`,{runScripts:'outside-only'});
  const w=dom.window, form=w.document.querySelector('form'), steps=[...form.children];
  const cards=steps.map(s=>nested?s.firstElementChild:s);
  let width=800, active=0, frames=[], observer;
  const natural=[500,200];
  Object.defineProperty(form,'clientWidth',{get:()=>width});
  cards.forEach((card,i)=>{
    card.style.boxSizing='border-box';
    Object.defineProperty(card,'offsetHeight',{get:()=>{
      if (active!==i) return 0;
      const px=[...card.style.minHeight.matchAll(/([\d.]+)px/g)].map(m=>Number(m[1]));
      const inset=card.style.boxSizing==='content-box'?24:0;
      const authoredHeight=parseFloat(card.style.height);
      return Math.max(Number.isFinite(authoredHeight)?authoredHeight+inset:natural[i], ...(px.length?px.map(x=>x+inset):[0]));
    }});
    Object.defineProperty(card,'scrollHeight',{get:()=>active===i?Math.max(natural[i],card.offsetHeight)-(card.style.boxSizing==='content-box'?4:0):0});
  });
  w.nativeFormSequences=[{form,parent:form,steps}];
  w.SITE_KEY=site;w.renderInput=null;w.materializeMode=materialize;
  w.requestAnimationFrame=fn=>{frames.push(fn);};
  w.ResizeObserver=class{constructor(fn){observer=fn;}observe(){}unobserve(){}disconnect(){}};
  w.eval(frameSource+'\nwindow.frameApi={schedule:scheduleNativeFormFrames,restore:restoreNativeFormFrames,capture:withNativeFormFramesRestored};');
  const flush=()=>{const pending=frames;frames=[];pending.forEach(fn=>fn());};
  const notify=()=>{observer?.();flush();};
  return {w,form,steps,cards,natural,flush,notify,api:w.frameApi,
    start(){w.frameApi.schedule();flush();},step(i){active=i;notify();},width(value){width=value;notify();},close(){w.close();}};
}

for(const [id,nested] of [['autoQuoteForm',false],['homeQuoteForm',false],['commercialQuoteForm',true],['riskAssessmentForm',true]]) {
  test(`${id}: visible card remains tall through shorter steps and dynamic content`,()=>{
    const f=fixture({id,nested});
    try {
      const input=f.cards[0].querySelector('input');input.focus();
      f.start();assert.equal(f.cards[0].offsetHeight,500);
      assert.equal(f.form.style.minHeight,'','no invisible outer spacer');
      f.step(1);assert.equal(f.cards[1].offsetHeight,500);
      f.natural[1]=760;f.notify();assert.equal(f.cards[1].offsetHeight,760);
      f.natural[1]=120;f.notify();assert.equal(f.cards[1].offsetHeight,760);
      f.step(0);assert.equal(f.cards[0].offsetHeight,760);
      assert.equal(f.cards[0].querySelector('input'),input);
      assert.equal(input.value,'retained');assert.equal(input.disabled,false);
      assert.equal(f.w.document.activeElement,input);
      assert.equal(f.w.document.querySelector('#unrelated').style.minHeight,'');
      assert.equal(f.cards[0].querySelector('.mini-card').style.minHeight,'');
      assert.equal(f.steps.some(s=>s.hidden),false,'runtime never writes step visibility');
      assert.equal(f.cards.some(c=>c.style.height || c.style.overflow),false);
    } finally {f.close();}
  });
}

test('width rebases the floor; height-only viewport changes retain it',()=>{
  const f=fixture();try{
    f.start();f.step(1);
    f.w.innerHeight=420;f.notify();assert.equal(f.cards[1].offsetHeight,500);
    f.width(360);assert.equal(f.cards[1].offsetHeight,200);
    f.natural[1]=620;f.notify();f.step(0);assert.equal(f.cards[0].offsetHeight,620);
    f.width(800);assert.equal(f.cards[0].offsetHeight,500);
  }finally{f.close();}
});

test('capture restores exact authored minimum/priority and reapplies without persisting the floor',()=>{
  const f=fixture();try{
    f.cards[0].style.setProperty('min-height','320px','important');
    const original=f.form.outerHTML;
    f.start();assert.equal(f.cards[0].style.getPropertyPriority('min-height'),'important');
    assert.equal(f.api.capture(()=>f.form.outerHTML),original);
    assert.throws(()=>f.api.capture(()=>{throw new Error('capture failed');}),/capture failed/);
    f.flush();assert.equal(f.cards[0].offsetHeight,500);
    f.api.restore(true);assert.equal(f.form.outerHTML,original);
    f.cards[0].style.minHeight='900px';f.start();assert.equal(f.cards[0].offsetHeight,900);
    assert.equal(f.api.capture(()=>f.cards[0].style.minHeight),'900px');
  }finally{f.close();}
});

test('content-box targets subtract their own padding/borders; authored sizing is retained',()=>{
  const f=fixture();try{
    f.cards[1].style.cssText='box-sizing:content-box;padding:10px;border:2px solid;min-height:600px';
    f.start();f.step(1);assert.equal(f.cards[1].offsetHeight,624);
    f.step(0);assert.equal(f.cards[0].offsetHeight,624);
    assert.equal(f.api.capture(()=>f.cards[1].style.minHeight),'600px');
  }finally{f.close();}
});

test('unowned, ambiguous and materialization-only forms get no runtime sizing',()=>{
  for(const options of [{id:'unrelatedForm'},{site:'business'},{nested:true,ambiguous:true},{materialize:true}]) {
    const f=fixture(options);try{
      const original=f.form.outerHTML;f.start();assert.equal(f.form.outerHTML,original);
    }finally{f.close();}
  }
});

test('external authored writes survive restoration, including priority-only edits',()=>{
  const f=fixture();try{
    f.start();f.cards[0].style.setProperty('min-height','810px','important');
    f.api.restore();assert.equal(f.cards[0].style.minHeight,'810px');
    assert.equal(f.cards[0].style.getPropertyPriority('min-height'),'important');
  }finally{f.close();}
});

test('authored height survives while overflowing content raises the visible card floor',async()=>{
  const f=fixture();try{
    f.cards[0].style.height='300px';f.natural[0]=700;
    f.start();assert.equal(f.cards[0].offsetHeight,700);
    assert.equal(f.cards[0].style.height,'300px');
    f.natural[0]=940;f.cards[0].appendChild(f.w.document.createElement('div'));
    await Promise.resolve();f.flush();assert.equal(f.cards[0].offsetHeight,940);
    f.step(1);assert.equal(f.cards[1].offsetHeight,940);
    f.api.restore();assert.equal(f.cards[0].style.height,'300px');
  }finally{f.close();}
});

test('CSSOM length normalization does not cause repeated equivalent observer writes',()=>{
  const f=fixture();try{
    const card=f.cards[1];
    card.style.boxSizing='content-box';card.style.paddingTop='10.333333333px';
    let writes=0;
    const set=card.style.setProperty.bind(card.style);
    card.style.setProperty=(key,value,priority)=>{
      if(key==='min-height') {writes++;value=value.replace(/\d+\.\d+px/g, n=>Number(parseFloat(n).toFixed(4))+'px');}
      set(key,value,priority);
    };
    f.start();const initial=writes;
    for(let i=0;i<5;i++) f.notify();
    assert.equal(writes,initial,'serialized rounding must not trigger equivalent writes');
  }finally{f.close();}
});
