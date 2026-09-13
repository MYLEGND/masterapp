import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const source=readFileSync(new URL('../../SHARED/wwwroot/js/social-post.js',import.meta.url),'utf8');
class Node {
  constructor(tag='div') { this.tagName=tag;this.textContent='';this.dataset={};this.children=[];this.attributes={};this.isConnected=true;this.classList={toggle(){}}; }
  append(...nodes) { this.children.push(...nodes); }
  replaceChildren(...nodes) { this.children=nodes; }
  setAttribute(key,value) { this.attributes[key]=value; }
  addEventListener() {}
  querySelectorAll() { return []; }
  remove() { this.isConnected=false; }
}
function fixture() {
  const nodes=new Map(),events=new Map(),calls=[];const root=new Node();root.dataset.postId='post-id';
  root.querySelector=selector=>{if(selector==='[data-social-action="comments"]')return null;if(!nodes.has(selector))nodes.set(selector,new Node());return nodes.get(selector);};
  root.addEventListener=(event,callback)=>events.set(event,callback);
  root.querySelector('input[name="__RequestVerificationToken"]').value='csrf';
  const document={title:'Original post',querySelector:()=>root,createElement:tag=>new Node(tag),createTextNode:value=>Object.assign(new Node('#text'),{textContent:value}),createDocumentFragment:()=>new Node('#fragment')};
  const post={id:'post-id',body:'Verified caption',media:[],reactionCount:3,commentCount:0,reactedByCurrentActor:true,savedByCurrentActor:false,repostedByCurrentActor:false,comments:[],commentsEnabled:true,metrics:{shareCount:2,repostCount:0}};
  const context={document,navigator:{},location:{origin:'https://portal.example'},URL,FormData,AbortSignal,fetch:async(url,options)=>{calls.push({url,options});return {ok:true,json:async()=>post};}};
  vm.createContext(context);vm.runInContext(source,context);
  const clickShare=()=>events.get('click')({target:{closest:selector=>selector==='[data-social-share]'?new Node('button'):null}});
  return {context,nodes,calls,root,events,post,clickShare};
}
const tick=()=>new Promise(resolve=>setImmediate(resolve));
test('cancelled native share does not record a share receipt',async()=>{
  const f=fixture();f.context.navigator.share=async()=>{throw Object.assign(new Error('cancel'),{name:'AbortError'});};
  await f.clickShare();await tick();assert.equal(f.calls.length,0);
});
test('completed copy records share once with antiforgery and updates server counters',async()=>{
  const f=fixture();const copied=[];f.context.navigator.clipboard={writeText:async url=>copied.push(url)};
  await f.clickShare();await tick();
  assert.deepEqual(copied,['https://portal.example/Social/Posts/post-id']);assert.equal(f.calls.length,1);
  assert.equal(f.calls[0].url,'/Social/Posts/post-id/share');assert.equal(f.calls[0].options.body.get('__RequestVerificationToken'),'csrf');
  assert.equal(f.nodes.get('[data-social-share-count]').textContent,2);assert.equal(f.nodes.get('[data-social-reactions]').textContent,3);
});
test('pending share sheet cannot launch duplicate share operations',async()=>{
  const f=fixture();let resolve;const held=new Promise(done=>resolve=done);let opened=0;
  f.context.navigator.share=()=>{opened++;return held;};const first=f.clickShare();await f.clickShare();assert.equal(opened,1);
  resolve();await first;await tick();assert.equal(f.calls.length,1);
});
test('denied mutation clears original media/comments/identity and reports unavailable',async()=>{
  const f=fixture();f.context.navigator.clipboard={writeText:async()=>{}};
  for(const selector of ['[data-social-post]','[data-social-comments-section]','.social-original-identity'])f.root.querySelector(selector).append(new Node('private'));
  f.context.fetch=async()=>({ok:false,status:403});await f.clickShare();await tick();
  for(const selector of ['[data-social-post]','[data-social-comments-section]','.social-original-identity'])assert.equal(f.nodes.get(selector).children.length,0);
  assert.match(f.nodes.get('[data-social-status]').textContent,/no longer available/);
});
test('failed mutation cannot optimistically increment authoritative counters',async()=>{
  const f=fixture();f.context.navigator.clipboard={writeText:async()=>{}};
  f.root.querySelector('[data-social-share-count]').textContent='7';
  f.context.fetch=async()=>({ok:false,status:503,json:async()=>({errorMessage:'Temporarily unavailable'})});
  await f.clickShare();await tick();assert.equal(f.nodes.get('[data-social-share-count]').textContent,'7');
  assert.equal(f.nodes.get('[data-social-status]').textContent,'Temporarily unavailable');
});

test('fresh comment authors and bodies retain user-content localization boundaries',async()=>{
  const f=fixture();f.context.navigator.clipboard={writeText:async()=>{}};
  f.post.comments=[{id:'comment',author:{displayName:'Like'},body:'Save',createdUtc:'2026-09-11T12:00:00Z'}];
  await f.clickShare();await tick();
  const article=f.nodes.get('[data-social-comments]').children[0].children[0];
  assert.equal(article.children[0].children[0].dataset.userContent,'');
  assert.equal(article.children.find(node=>node.tagName==='p').dataset.userContent,'');
});
test('new dynamic interface labels are admitted to the canonical catalog',()=>{
  const manifest=JSON.parse(readFileSync(new URL('../../Legend-Design/legend-application-copy.json',import.meta.url),'utf8'));
  for(const label of ['Replying to','Saving…','Updated.','Open original post','Open original story','Open original Hac','Loading recent messages…','The action timed out and may have completed. Refresh before retrying.']) {
    assert(manifest.entries.some(entry=>entry.source===label&&entry.context==='visual interface copy'),label);
  }
  const view=readFileSync(new URL('../../SHARED/Views/SocialSharedContent/Open.cshtml',import.meta.url),'utf8');
  assert.match(view,/data-social-post-body data-user-content/);
  assert.match(view,/<p data-social-body data-user-content>@comment.Body/);
});
