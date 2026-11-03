import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
const context={window:{}};
vm.runInNewContext(readFileSync(new URL('../../AgentPortal/wwwroot/js/crm-contact-card.js',import.meta.url),'utf8'),context);
const render=context.window.LegendCrmContactCard.render;
test('contact summary prioritizes phone and excludes email',()=>{
 const html=render({id:'qa',name:'QA Contact',phone:'2025550101',email:'qa@example.invalid'});
 assert.match(html,/\(202\) 555-0101/);assert.match(html,/href="tel:2025550101"/);
 assert.doesNotMatch(html,/qa@example|mailto:/);
});
test('star and payment summary occupy separate regions',()=>{
 const html=render({id:'qa',name:'QA Contact',starred:true,productionHtml:'<span>Paid $100</span>'});
 assert.match(html,/aria-pressed="true"/);
 assert.ok(html.indexOf('</button>')<html.indexOf('data-prod-card'));
 assert.match(html,/<\/button>\s*<\/div>\s*<div class="lead-prod-badge/);
});
test('untrusted contact fields are escaped and missing phone has a useful state',()=>{
 const html=render({id:'" onclick="evil',name:'<img src=x onerror=evil>'});
 assert.doesNotMatch(html,/<img|id="" onclick/);assert.match(html,/&lt;img/);assert.match(html,/No phone/);
});
