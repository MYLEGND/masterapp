import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const read = path => readFileSync(new URL('../../' + path, import.meta.url), 'utf8');
const source = read('SHARED/wwwroot/js/messaging.js');
const markup = read('SHARED/Views/Messaging/_CommandCenter.cshtml');
const css = read('SHARED/wwwroot/css/dashboard-home-shared.css');
function fixture(count) {
  class Element {
    constructor() { this.children=[];this.attributes={};this.dataset={};this.classList={add(){}}; }
    append(...children) { this.children.push(...children); }
    replaceChildren(...children) { this.children=children; }
    setAttribute(name,value) { this.attributes[name]=value; }
    addEventListener(_,callback) { this.click=callback; }
  }
  const list=new Element(); let selected;
  const conversation={id:'thread',unreadCount:count,counterparty:{displayName:'Private name'},lastMessagePreview:'Original message'};
  const context={document:{createElement:()=>new Element()},elements:{list},state:{conversations:[conversation],drafts:{}},
    isConversationInRecipientScope:()=>true,recipientScopeDescription:()=>({}),
    createAvatar:participant=>Object.assign(new Element(),{className:'messaging-avatar',participant}),
    createTextElement:(_,className,textContent)=>Object.assign(new Element(),{className,textContent}),
    createPresencePill:()=>new Element(),formatConversationTime:()=>'',selectConversationForCurrentIntent:value=>selected=value,
    renderPresence(){},refreshPresence(){}};
  vm.runInNewContext(source.slice(source.indexOf('  function renderConversations()'),source.indexOf('  function participantName('))+'\nrenderConversations();',context);
  return {row:list.children[0],conversation,selected:()=>selected};
}
for (const count of [0,1,12,125]) test(`avatar unread ${count} preserves identity, full accessible count and action`,()=>{
  const f=fixture(count),identity=f.row.children[0],avatar=identity.children[0],meta=f.row.children[1];
  assert.equal(avatar.className,'messaging-conversation-avatar');
  assert.equal(avatar.children[0].participant,f.conversation.counterparty);
  assert.equal(identity.children[1].children[0].dataset.userContent,'');
  assert.equal(meta.children.length,1,'unread badge is not duplicated in trailing metadata');
  assert.equal(avatar.children.length,count?2:1);
  if(count){const badge=avatar.children[1];assert.equal(badge.textContent,count>99?'99+':String(count));assert.equal(badge.attributes['aria-label'],String(count));assert.equal(badge.attributes['aria-describedby'],'messagingUnreadDescription');}
  f.row.click();assert.equal(f.selected(),f.conversation);
});
test('header retains accessible heading and every existing action in one scrolling row',()=>{
  const header=markup.slice(markup.indexOf('<header class="messaging-command-center-header">'),markup.indexOf('</header>'));
  assert.match(header,/<h2 id="messagingCommandCenterTitle" class="visually-hidden">Messages<\/h2>/);
  assert.match(header,/<span id="messagingUnreadDescription" class="visually-hidden">Unread messages<\/span>/);
  const actions=header.slice(header.indexOf('<div class="messaging-command-center-header-actions">'),header.indexOf('@if (!string.IsNullOrWhiteSpace'));
  for(const id of ['messagingCallingProfile','messagingChooseVoiceCall','messagingChooseVideoCall','messagingJourneyCirclesOpen','messagingCommandCenterClose'])assert.equal(actions.split(`id="${id}"`).length,2,id);
  assert.equal((header.match(/data-messaging-recipient-scope="/g)||[]).length,2);
  assert.match(css,/\.messaging-command-center-header-actions \{[^}]*flex-wrap: nowrap;[^}]*overflow-x: auto;/);
});
test('badge is centered on circular edge and original avatar sizes stay scoped',()=>{
  const badge=css.match(/\.messaging-conversation-avatar > \.messaging-unread-count \{([^}]+)\}/)[1];
  const x=Number(badge.match(/left: ([\d.]+)%/)[1])/100,y=Number(badge.match(/top: ([\d.]+)%/)[1])/100;
  assert.ok(Math.abs(Math.hypot(x-.5,y-.5)-.5)<1e-9);
  assert.match(badge,/translate\(-50%, -50%\)/);
  assert.match(css,/(?:^|\n)\.messaging-conversation-avatar \{\s*position: relative;/);
  assert.match(css,/\.messaging-journey-card-identity \.messaging-avatar \{\s*width: 2rem;\s*height: 2rem;/);
  assert.match(css,/(?:^|\n)\.messaging-avatar \{[^}]*width: 2.35rem;[^}]*height: 2.35rem;/);
  assert.equal(JSON.parse(read('Legend-Design/legend-design.tokens.json')).sizes.unreadBadge,22);
});
