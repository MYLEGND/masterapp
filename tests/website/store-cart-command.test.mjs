import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {JSDOM} from 'jsdom';
const source=readFileSync(new URL('../../ParfaitApp/wwwroot/js/storefront.js',import.meta.url),'utf8');
test('store cart changes local state only after accepted server command',async()=>{
 const dom=new JSDOM('<body/>',{url:'https://shopparfait.com/store',runScripts:'outside-only'});
 try {
  const w=dom.window,calls=[];let accept=false,queue=Promise.resolve();
  Object.defineProperty(w.navigator,'locks',{value:{request(key,operation){
   const next=queue.then(operation);queue=next.catch(()=>{});return next;
  }}});
  w.PARFAIT_COMMERCE_CONTEXT={storeRootPath:'/store'};
  w.fetch=async(url,init)=>{const body=JSON.parse(init.body);calls.push({url,body});return {ok:accept,json:async()=>({eventId:body.eventId,acceptedQuantity:2,priceCents:750})};};
  w.eval(source);
  const item={id:'sku',key:'sku:M',size:'M',quantity:2,priceCents:1};
  await assert.rejects(w.ParfaitStorefront.addItem(item));
  assert.equal(w.ParfaitStorefront.readCart().length,0);
  accept=true;await w.ParfaitStorefront.addItem(item);
  const cart=w.ParfaitStorefront.readCart();
  assert.equal(cart.length,1);assert.equal(cart[0].quantity,2);assert.equal(cart[0].priceCents,750);
  assert.equal(calls[1].url,'/store/cart/items');assert.match(calls[1].body.eventId,/^[a-f0-9-]{36}$/);
  assert.equal(calls[1].body.priceCents,undefined);
  assert.equal(calls[1].body.eventId,calls[0].body.eventId); // Lost/rejected reply keeps the same command.
  await Promise.all([w.ParfaitStorefront.addItem(item,calls[1].body.eventId),w.ParfaitStorefront.addItem(item,calls[1].body.eventId)]);
  assert.equal(calls.length,2);assert.equal(w.ParfaitStorefront.readCart()[0].quantity,2);
 }finally{dom.window.close();}
});

test('unsupported browser rejects cart mutation before storage or server effects',async()=>{
 const dom=new JSDOM('<body/>',{url:'https://shopparfait.com/store',runScripts:'outside-only'});
 try {
  const w=dom.window;let calls=0;
  const original=JSON.stringify([{id:'old',key:'old:M',size:'M',quantity:1,priceCents:100}]);
  w.localStorage.setItem('parfaitCart',original);
  w.fetch=async()=>{calls++;throw Error('must not reach server')};w.eval(source);
  await assert.rejects(w.ParfaitStorefront.addItem({id:'sku',key:'sku:M',size:'M',quantity:2}),/cannot safely update/);
  assert.equal(calls,0);assert.equal(w.localStorage.getItem('parfaitCart'),original);
 }finally{dom.window.close();}
});
