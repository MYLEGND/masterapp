import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
const cartSource=readFileSync(new URL('../../ParfaitApp/Views/Store/Cart.cshtml',import.meta.url),'utf8').match(/<script>([\s\S]*?)<\/script>/)[1];
const checkoutSource=readFileSync(new URL('../../ParfaitApp/Views/Store/Checkout.cshtml',import.meta.url),'utf8');
const line={key:'sku:XS',id:'sku',size:'XS',quantity:1,unitPriceCents:2500,lineTotalCents:2500,name:'Fixture Jacket'};
const quote={success:true,isValid:true,items:[line],itemCount:1,subtotalCents:2500,shippingCents:0,taxCents:0,discountCents:0,totalCents:2500};
async function render(ok,body,quantity=1){
 const elements=new Map();
 const document={getElementById(id){if(!elements.has(id)) elements.set(id,{textContent:'',innerHTML:'',value:'',attrs:{},classList:{toggle(){}},addEventListener(){},setAttribute(k,v){this.attrs[k]=v},removeAttribute(k){delete this.attrs[k]}});return elements.get(id)},querySelector(){return null}};
 let cart=[{...line,quantity,priceCents:2500}],writes=0;
 const listeners={};
 const store={context:{},readCart:()=>cart,writeCart(next){cart=next;writes++;listeners['parfait-cart-updated']?.()},readDiscountCode:()=>'',money:n=>String(n),itemCount:c=>c.reduce((s,x)=>s+x.quantity,0),subtotal:c=>c.reduce((s,x)=>s+x.quantity*x.priceCents,0)};
 vm.runInNewContext(cartSource,{document,window:{ParfaitStorefront:store,addEventListener:(k,v)=>listeners[k]=v},fetch:async()=>({ok,text:async()=>body})});
 await new Promise(resolve=>setImmediate(resolve));
 return {cart,writes,elements};
}
for(const [name,ok,body] of [['500 JSON',false,'{"error":"unavailable"}'],['500 HTML',false,'<html>error</html>'],['empty 200',true,''],['malformed 200',true,'{'],['error schema',true,'{"success":false}'],['missing key',true,JSON.stringify({...quote,items:[{...line,key:undefined}]})]]){
 test(`cart preserves items and blocks checkout on ${name}`,async()=>{
  const r=await render(ok,body);assert.equal(r.writes,0);assert.equal(r.cart.length,1);
  assert.match(r.elements.get('spCartItems').innerHTML,/Fixture Jacket/);
  assert.equal(r.elements.get('spCheckoutLink').attrs['aria-disabled'],'true');
 });
}
test('valid quote renders and permits checkout',async()=>{const r=await render(true,JSON.stringify(quote));assert.equal(r.writes,0);assert.equal(r.elements.get('spCartTotal').textContent,'2500');assert.equal(r.elements.get('spCheckoutLink').attrs['aria-disabled'],undefined)});
test('valid stock adjustment changes quantity then renders fresh quote',async()=>{const r=await render(true,JSON.stringify(quote),2);assert.equal(r.writes,1);assert.equal(r.cart[0].quantity,1);assert.equal(r.elements.get('spCheckoutLink').attrs['aria-disabled'],undefined)});
test('failed contact autosave can retry identical payload; success deduplicates',async()=>{
 const start=checkoutSource.indexOf('const captureLead = async () => {');
 const end=checkoutSource.indexOf('const queueLeadCapture',start);
 let ok=false,calls=0;
 const context=vm.createContext({buildLeadPayload:()=>({email:'fixture@example.invalid'}),leadEndpoint:'/fixture',tokenInput:null,fetch:async()=>{calls++;return {ok}}});
 vm.runInContext('let lastLeadSignature="";'+checkoutSource.slice(start,end)+'globalThis.capture=captureLead;',context);
 await context.capture();await context.capture();assert.equal(calls,2);
 ok=true;await context.capture();await context.capture();assert.equal(calls,3);
});
