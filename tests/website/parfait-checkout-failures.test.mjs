import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
import {randomUUID} from 'node:crypto';
const storefrontSource=readFileSync(new URL('../../ParfaitApp/wwwroot/js/storefront.js',import.meta.url),'utf8');
const cartSource=readFileSync(new URL('../../ParfaitApp/Views/Store/Cart.cshtml',import.meta.url),'utf8').match(/<script>([\s\S]*?)<\/script>/)[1];
const checkoutSource=readFileSync(new URL('../../ParfaitApp/Views/Store/Checkout.cshtml',import.meta.url),'utf8');
const line={key:'sku:XS',id:'sku',size:'XS',quantity:1,unitPriceCents:2500,lineTotalCents:2500,name:'Fixture Jacket'};
const quote={success:true,isValid:true,items:[line],itemCount:1,subtotalCents:2500,shippingCents:0,taxCents:0,discountCents:0,totalCents:2500};
async function render(ok,body,quantity=1,delayedQuote=false){
 const elements=new Map();
 const document={getElementById(id){if(!elements.has(id)) elements.set(id,{textContent:'',innerHTML:'',value:'',attrs:{},classList:{toggle(){}},addEventListener(){},setAttribute(k,v){this.attrs[k]=v},removeAttribute(k){delete this.attrs[k]}});return elements.get(id)},querySelector(){return null}};
 const original=JSON.stringify([{...line,quantity,priceCents:2500}]);
 const bytes=new Map([['parfaitCart',original]]);let writes=0,queue=Promise.resolve();
 let requests=0,releaseFirst;
 const listeners={};
 const window={addEventListener:(k,v)=>listeners[k]=v,dispatchEvent(event){if(event.type==='parfait-cart-updated')writes++;listeners[event.type]?.(event)}};
 const context=vm.createContext({document,window,crypto:{randomUUID},Event:class{constructor(type){this.type=type}},
  localStorage:{getItem:key=>bytes.get(key)||null,setItem:(key,value)=>bytes.set(key,value),removeItem:key=>bytes.delete(key)},
  navigator:{locks:{request(key,operation){const next=queue.then(operation);queue=next.catch(()=>{});return next}}},
  fetch:async()=>{
   const request=++requests;
   if(delayedQuote && request===1) await new Promise(resolve=>releaseFirst=resolve);
   const response=delayedQuote && request>1 ? JSON.stringify({...quote,items:[{...line,quantity:2,lineTotalCents:5000}],itemCount:2,subtotalCents:5000,totalCents:5000}) : body;
   return {ok,text:async()=>response};
  }});
 // Run the actual cart authority, including revision checks, migration and async locks.
 vm.runInContext(storefrontSource,context);vm.runInContext(cartSource,context);
 if(delayedQuote){await window.ParfaitStorefront.adjustQuantity(line.key,1);releaseFirst();}
 await new Promise(resolve=>setImmediate(resolve));await queue;
 await new Promise(resolve=>setImmediate(resolve));
 return {cart:window.ParfaitStorefront.readCart(),writes,elements,original,stored:bytes.get('parfaitCart')};
}
for(const [name,ok,body] of [['500 JSON',false,'{"error":"unavailable"}'],['500 HTML',false,'<html>error</html>'],['empty 200',true,''],['malformed 200',true,'{'],['error schema',true,'{"success":false}'],['missing key',true,JSON.stringify({...quote,items:[{...line,key:undefined}]})]]){
 test(`cart preserves items and blocks checkout on ${name}`,async()=>{
  const r=await render(ok,body);assert.equal(r.writes,0);assert.equal(r.cart.length,1);assert.equal(r.stored,r.original);
  assert.match(r.elements.get('spCartItems').innerHTML,/Fixture Jacket/);
  assert.equal(r.elements.get('spCheckoutLink').attrs['aria-disabled'],'true');
 });
}
test('valid quote renders and permits checkout',async()=>{const r=await render(true,JSON.stringify(quote));assert.equal(r.writes,0);assert.equal(r.elements.get('spCartTotal').textContent,'$25.00');assert.equal(r.elements.get('spCheckoutLink').attrs['aria-disabled'],undefined)});
test('valid stock adjustment changes quantity then renders fresh quote',async()=>{const r=await render(true,JSON.stringify(quote),2);assert.equal(r.writes,1);assert.equal(r.cart[0].quantity,1);assert.equal(r.elements.get('spCheckoutLink').attrs['aria-disabled'],undefined)});
test('delayed quote cannot replace a newer cart edit',async()=>{const r=await render(true,JSON.stringify(quote),1,true);assert.equal(r.cart[0].quantity,2);assert.equal(r.writes,1);assert.equal(r.elements.get('spCartTotal').textContent,'$50.00');assert.equal(r.elements.get('spCheckoutLink').attrs['aria-disabled'],undefined)});
test('checkout uses explicit shared inquiry and never silently autosaves contact fields', () => {
 assert.match(checkoutSource, /data-legend-public-inquiry-form data-owner-label="@storeName"/);
 assert.doesNotMatch(checkoutSource, /leadEndpoint|captureLead|queueLeadCapture|buildLeadPayload/);
 assert.match(checkoutSource, /const payEndpoint = checkoutPath \+ "\/pay"/);
});
