import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
const source=readFileSync(new URL('../../SHARED/wwwroot/js/messaging.js',import.meta.url),'utf8');
function implementation(name){const start=source.search(new RegExp(`^  (?:async )?function ${name}\\(`,'m'));assert.notEqual(start,-1);const tail=source.slice(start+1);const end=tail.search(/\n  (?:async )?function /);return end<0?tail:tail.slice(0,end);}
class Element {
 constructor(tag){this.tagName=tag;this.children=[];this.dataset={};this.attributes={};this.events={};this.classList={add:name=>this.className=(this.className||'')+' '+name};}
 append(...items){this.children.push(...items);}
 replaceChildren(...items){this.children=items;}
 setAttribute(name,value){this.attributes[name]=value;}
 getAttribute(name){return this.attributes[name];}
 addEventListener(name,callback){this.events[name]=callback;}
}
function environment(message){
 let calls=0;const elements=Object.fromEntries(['threadEmpty','threadContent','mute','closeConversation','threadAvatar','threadTitle','threadSubject','messages'].map(k=>[k,new Element('div')]));
 const c={state:{active:{id:'c',conversationType:'ClientAgent',messages:[{id:'m',senderUserId:'other',senderType:'Agent',body:'Tradiksyon',...message}]}},elements,document:{createElement:t=>new Element(t),createTextNode:t=>({textContent:t})},applicationCopy:v=>v,fetch:()=>{calls++;throw Error('Unexpected network request');},currentCounterparty:()=>({displayName:'Recipient'}),createAvatar:()=>new Element('span'),createPresencePill:()=>new Element('span'),renderPresence(){},refreshPresence(){},reactionBubbleObserver:{disconnect(){}},latestReadMessageIndex:()=>-1,dayLabel:()=>'',isCurrentParticipant:()=>false,participantName:()=> 'Sender',formatMessageTime:()=> '12:00',appendSharedContent(){},appendMessageInteractions(){},restoreMessageScroll(){},setComposerState(){},restoreDraft(){}};
 vm.createContext(c);for(const name of ['createTextElement','appendLinkedText','renderConversation'])vm.runInContext(implementation(name),c);c.renderConversation();return {c,elements,calls:()=>calls,card:elements.messages.children[0].children[0]};
}
test('web toggles the retained original and translation without provider calls or losing user-content scope',()=>{
 const {card,calls}=environment({body:'Hello brother',originalBody:'Bonjou frè',translation:{originalLanguage:'ht',targetLanguage:'en',provider:'Azure'}});
 const body=card.children.find(e=>e.className==='messaging-message-body'),toggle=card.children.find(e=>e.className==='messaging-translation-toggle');
 assert.equal(body.children[0].textContent,'Hello brother');assert.equal(toggle.textContent,'View original');
 toggle.events.click();assert.equal(body.children[0].textContent,'Bonjou frè');assert.equal(toggle.textContent,'View translation');assert.equal(toggle.attributes['aria-pressed'],'true');assert.equal(body.attributes.translate,'no');
 toggle.events.click();assert.equal(body.children[0].textContent,'Hello brother');assert.equal(calls(),0);
});
test('untranslated and quota-original messages have no fabricated translated toggle',()=>{
 const {card}=environment({originalBody:'Tradiksyon',translationNotice:'Translation limit reached'});
 assert.equal(card.children.some(e=>e.className==='messaging-translation-toggle'),false);assert.equal(card.children.some(e=>e.textContent==='Translation limit reached'),true);
});
test('reply preview renders only the authorized server excerpt and respects deletion',()=>{
 const visible=environment({reply:{body:'Approved excerpt',senderUserId:'other',senderType:'Agent',isDeleted:false}}).card.children.find(e=>e.className==='messaging-message-reply');
 assert.equal(visible.children[1].textContent,'Approved excerpt');assert.equal(visible.children[1].dataset.userContent,'');
 const deleted=environment({reply:{body:'must not display',isDeleted:true}}).card.children.find(e=>e.className==='messaging-message-reply');assert.equal(deleted.textContent,'Message deleted');assert.equal(deleted.children.length,0);
});

test('unchanged conversation refresh preserves original selection, changing conversation clears it',()=>{
 const {c,card,elements}=environment({body:'Hello',originalBody:'Bonjou',translation:{}});card.children.find(e=>e.className==='messaging-translation-toggle').events.click();c.renderConversation();
 let body=elements.messages.children[0].children[0].children.find(e=>e.className==='messaging-message-body');assert.equal(body.children[0].textContent,'Bonjou');
 c.state.active={...c.state.active,id:'other-conversation'};c.renderConversation();body=elements.messages.children[0].children[0].children.find(e=>e.className==='messaging-message-body');assert.equal(body.children[0].textContent,'Hello');
});
