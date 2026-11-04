const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const crypto = require('node:crypto');
const source = fs.readFileSync(process.argv[2], 'utf8');
const item = {key:'tee:M',id:'tee',size:'M',quantity:1,priceCents:1000};
function fixture() {
 const bytes = new Map(), queues = new Map(), receipts = new Map(); let calls=0, deny=false, lose=false;
 const storage={getItem:k=>bytes.get(k)||null,setItem:(k,v)=>{if(deny)throw Error('storage denied');bytes.set(k,v)},removeItem:k=>bytes.delete(k)};
 const locks={request:(key,fn)=>{const next=(queues.get(key)||Promise.resolve()).then(fn);queues.set(key,next.catch(()=>{}));return next;}};
 function tab(key='cart', supported=true) {
  const window={PARFAIT_COMMERCE_CONTEXT:{cartStorageKey:key},dispatchEvent(){},addEventListener(){}};
  vm.runInNewContext(source,{window,localStorage:storage,navigator:{locks:supported?locks:null},crypto,Event:class{},location:{href:'https://local/store'},document:{referrer:''},fetch:async(url,options)=>{
   calls++;const request=JSON.parse(options.body);let receipt=receipts.get(request.eventId);
   if(!receipt){receipt={eventId:request.eventId,acceptedQuantity:request.quantity,priceCents:1000};receipts.set(request.eventId,receipt)}
   if(lose){lose=false;throw Error('response lost')};return {ok:true,json:async()=>receipt};
  }});return window.ParfaitStorefront;
 }
 return {tab,bytes,receipts,get calls(){return calls},deny:()=>deny=true,lose:()=>lose=true};
}
async function bind(s,id='attempt'){const cart=s.readCart();await s.bindCheckout(id,cart.map(({id,size,quantity})=>({id,size,quantity})),cart.revision)}
(async()=>{
 let f=fixture(), a=f.tab(), b=f.tab();
 const command=crypto.randomUUID();await Promise.all([a.addItem(item,command),b.addItem(item,command)]);
 assert.equal(a.readCart()[0].quantity,1);assert.equal(f.calls,1);
 await assert.rejects(a.addItem({...item,quantity:2},command),/does not match/);
 await bind(a);await Promise.all([a.reconcilePaidCheckout('attempt'),b.reconcilePaidCheckout('attempt')]);assert.equal(a.readCart().length,0);
 await a.addItem(item);await a.reconcilePaidCheckout('attempt');assert.equal(a.readCart()[0].quantity,1);
 await a.reconcilePaidCheckout('unbound');assert.equal(a.readCart()[0].quantity,1);
 f=fixture();a=f.tab();await a.addItem({...item,quantity:2});await bind(a);await a.updateQuantity(item.key,1);await a.updateQuantity(item.key,2);await a.reconcilePaidCheckout('attempt');assert.equal(a.readCart()[0].quantity,1);
 f=fixture();a=f.tab();await a.addItem(item);await bind(a);await a.removeItem(item.key);await a.addItem(item);await a.reconcilePaidCheckout('attempt');assert.equal(a.readCart()[0].quantity,1);
 f=fixture();a=f.tab();await a.addItem(item);await bind(a);await a.addItem({...item,key:'other:M',id:'other'});await a.addItem(item);await a.reconcilePaidCheckout('attempt');assert.equal(a.readCart().length,2);assert.equal(a.readCart().find(x=>x.id==='tee').quantity,1);
 f=fixture();a=f.tab();f.lose();await assert.rejects(a.addItem(item),/response lost/);await a.addItem(item);assert.equal(a.readCart()[0].quantity,1);assert.equal(f.receipts.size,1);
 const before=a.readCart();await a.updateQuantity(item.key,2);const result=await a.applyQuote({items:[]},before.revision);assert.equal(result.stale,true);assert.equal(a.readCart()[0].quantity,2);
 f=fixture();a=f.tab();const legacy=JSON.stringify([item]);f.bytes.set('cart',legacy);assert.equal(a.readCart()[0].quantity,1);assert.equal(f.bytes.get('cart'),legacy);f.deny();await assert.rejects(a.bindCheckout('attempt',[item],0),/storage denied/);assert.equal(f.bytes.get('cart'),legacy);assert.equal(f.calls,0);
 f=fixture();a=f.tab('cart',false);await assert.rejects(a.addItem(item),/up-to-date/);await assert.rejects(a.bindCheckout('attempt',[item],0),/up-to-date/);assert.equal(f.calls,0);assert.equal(f.bytes.size,0);
 f=fixture();a=f.tab('store-a');b=f.tab('store-b');await a.addItem(item,command);await b.addItem(item,command);await bind(a);assert.equal(await b.reconcilePaidCheckout('attempt'),false);assert.equal(b.readCart()[0].quantity,1);
 f=fixture();a=f.tab();await a.addItem(item);await bind(a);await a.updateQuantity(item.key,2);await assert.rejects(bind(a),/cart changed/);await a.removeItem(item.key);await a.addItem(item);await assert.rejects(bind(a),/cart changed/);
 f=fixture();a=f.tab();b=f.tab();await a.addItem(item);await Promise.all([a.adjustQuantity(item.key,1),b.adjustQuantity(item.key,1)]);assert.equal(a.readCart()[0].quantity,3);await Promise.all([a.adjustQuantity(item.key,-1),b.adjustQuantity(item.key,-1)]);assert.equal(a.readCart()[0].quantity,1);
 f=fixture();a=f.tab();await a.addItem(item);await bind(a);assert.equal(await a.abandonUnsubmittedCheckout('attempt'),true);await bind(a);await a.markCheckoutSubmitted('attempt');assert.equal(await a.abandonUnsubmittedCheckout('attempt'),false);assert.equal(await a.abandonUnsubmittedCheckout('unknown'),false);
 console.log('PASS canonical cart: atomic commands, cross-tab once, lot lineage, old receipts, lost response, stale quotes, migration, denied storage, unavailable locks, store isolation');
})().catch(error=>{console.error(error);process.exitCode=1});
