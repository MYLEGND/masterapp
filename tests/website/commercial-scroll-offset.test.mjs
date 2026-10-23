import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const source=readFileSync(new URL('../../Protect-Website/Views/Quote/Commercial.cshtml',import.meta.url),'utf8');
const start=source.indexOf('function scrollCommercialFlowIntoView()');
assert(start>=0);
const implementation=source.slice(start,source.indexOf('\n\n',start));
function fixture({position='sticky',bottom=96,height=96,display='block',visibility='visible',header=true,connected=true,scrollY=300,shellTop=100,viewport=757}={}){
  const calls=[];
  const context={quoteFlowShell:{isConnected:connected,getBoundingClientRect:()=>({top:shellTop})},document:{querySelector:s=>{assert.equal(s,'.site-header');return header?{getBoundingClientRect:()=>({bottom,height})}:null;}},window:{innerHeight:viewport,scrollY,getComputedStyle:()=>({position,display,visibility}),scrollTo:x=>calls.push(x)}};
  vm.runInNewContext(implementation+'\nscrollCommercialFlowIntoView();',context);return calls;
}
test('current hydrated sticky header clears the revealed flow',()=>{const c=fixture();assert.equal(c[0].top,304);assert.equal(c[0].behavior,'smooth');});
test('changed header height is measured rather than fixed',()=>{assert.equal(fixture({bottom:198.6,height:198.6})[0].top,201.4);});
test('fixed header also clears content',()=>{assert.equal(fixture({position:'fixed',bottom:72})[0].top,328);});
for(const options of [{header:false},{position:'static'},{display:'none'},{visibility:'hidden'},{height:0},{bottom:-12}])test(`nonoccluding header ${JSON.stringify(options)} adds no inset`,()=>{assert.equal(fixture(options)[0].top,400);});
test('detached destination is not scrolled',()=>{assert.equal(fixture({connected:false}).length,0);});
test('target is clamped to document start',()=>{assert.equal(fixture({scrollY:0,shellTop:20})[0].top,0);});
test('CTA calls shipped helper after reveal and selection without changing focus',()=>{
  const i=source.indexOf("startButton.addEventListener('click'");const handler=source.slice(i,source.indexOf('\n\t                });',i));
  assert.match(handler,/setQuoteStartedState\(true\);[\s\S]*setStep\(startStep\);[\s\S]*scrollCommercialFlowIntoView\(\)/);
  assert.doesNotMatch(handler,/\.focus\(|submit\(/);assert.equal((handler.match(/trackCommercialCta\(/g)||[]).length,1);
});
