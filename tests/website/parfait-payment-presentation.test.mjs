import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
const checkout=readFileSync(new URL('../../ParfaitApp/Views/Store/Checkout.cshtml',import.meta.url),'utf8');
const css=readFileSync(new URL('../../ParfaitApp/wwwroot/css/storefront.css',import.meta.url),'utf8');
const initialization=checkout.slice(checkout.indexOf('                if (!hosted) {',checkout.indexOf('async function startSquare()')),checkout.indexOf('                ensureCheckoutAttemptId();',checkout.indexOf('async function startSquare()')));
const AsyncFunction=Object.getPrototypeOf(async function(){}).constructor;
async function initialize({failure=false,pendingReference=null,hosted=false}={}) {
 const status={hidden:false,textContent:'Loading secure payment form…'};
 const target={children:[]};
 const card={async attach(selector){assert.equal(selector,'#card-container');if(failure)throw new Error('Provider unavailable');target.children.push({tagName:'IFRAME'});}};
 const document={getElementById(id){assert.equal(id,'spPaymentStatus');return status;}};
 const window={Square:{payments(){return {async card(){return card;}};}}};
 const run=new AsyncFunction('window','document','appId','locationId','hosted','pendingReference',`let card; ${initialization}`);
 return {status,target,run:()=>run(window,document,'app','location',hosted,pendingReference)};
}
test('embedded payment readiness hides loading text and preserves the attached card iframe',async()=>{
 const r=await initialize();await r.run();assert.equal(r.status.hidden,true);assert.equal(r.target.children[0].tagName,'IFRAME');
 assert.match(checkout,/<div id="card-container"><\/div>/);
});
test('provider initialization failure removes stale loading text and fails new payment startup',async()=>{
 const r=await initialize({failure:true});await assert.rejects(r.run(),/Provider unavailable/);assert.equal(r.status.hidden,true);assert.equal(r.target.children.length,0);
});
test('pending payment recovery can continue when the card provider cannot initialize',async()=>{
 const r=await initialize({failure:true,pendingReference:{orderNumber:'existing-order'}});await r.run();assert.equal(r.status.hidden,true);
});
test('hosted provider guidance stays visible without mounting a card iframe',async()=>{
 const r=await initialize({hosted:true});await r.run();assert.equal(r.status.hidden,false);assert.match(r.status.textContent,/secure payment provider/);assert.equal(r.target.children.length,0);
});
function luminance(hex){const channels=hex.match(/[a-f\d]{2}/gi).map(x=>parseInt(x,16)/255).map(x=>x<=.04045?x/12.92:((x+.055)/1.055)**2.4);return channels.reduce((sum,x,i)=>sum+x*[.2126,.7152,.0722][i],0);}
for(const kind of ['success','warning','danger'])test(`${kind} status text remains readable independently of the page theme`,()=>{
 const foreground=css.match(new RegExp(`--sp-${kind}-text: (#[a-f0-9]{6});`))[1];
 const background=css.match(new RegExp(`--sp-${kind}-surface: (#[a-f0-9]{6});`))[1];
 const values=[luminance(foreground),luminance(background)].sort((a,b)=>b-a);assert.ok((values[0]+.05)/(values[1]+.05)>=4.5);
});
