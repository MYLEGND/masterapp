import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {JSDOM} from 'jsdom';
const source=readFileSync(new URL('../../ParfaitApp/wwwroot/js/storefront.js',import.meta.url),'utf8');
test('store cart changes local state only after accepted server command',async()=>{
 const dom=new JSDOM('<body/>',{url:'https://shopparfait.com/store',runScripts:'outside-only'});
 try {
  const w=dom.window,calls=[];let accept=false;
  w.PARFAIT_COMMERCE_CONTEXT={storeRootPath:'/store'};
  w.fetch=async(url,init)=>{calls.push({url,body:JSON.parse(init.body)});return {ok:accept,json:async()=>({quantity:2,priceCents:750})};};
  w.eval(source);
  const item={id:'sku',key:'sku:M',size:'M',quantity:2,priceCents:1};
  await assert.rejects(w.ParfaitStorefront.addItem(item));
  assert.equal(w.ParfaitStorefront.readCart().length,0);
  accept=true;await w.ParfaitStorefront.addItem(item);
  const cart=w.ParfaitStorefront.readCart();
  assert.equal(cart.length,1);assert.equal(cart[0].quantity,2);assert.equal(cart[0].priceCents,750);
  assert.equal(calls[1].url,'/store/cart/items');assert.match(calls[1].body.eventId,/^[a-f0-9-]{36}$/);
  assert.equal(calls[1].body.priceCents,undefined);
 }finally{dom.window.close();}
});
