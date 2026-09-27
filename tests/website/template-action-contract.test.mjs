import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
import {JSDOM} from 'jsdom';
import {businessHome,businessPages} from '../../Legend-Website/src/business-content.mjs';
const build=readFileSync(new URL('../../Legend-Website/scripts/build.mjs',import.meta.url),'utf8');
const cms=readFileSync(new URL('../../SHARED/WebsitePlatform/legend-public-cms.js',import.meta.url),'utf8');
test('renderer declares immutable template actions while custom links have none',()=>{
 const source=[build.split('\n').find(line=>line.startsWith('const nav=')),build.split('\n').find(line=>line.startsWith('const businessNav=')),build.split('\n').find(line=>line.startsWith('function hero('))].join('\n');
 const context={businessPages};vm.createContext(context);
 vm.runInContext(source+'\nresult={legend:nav("/"),business:businessNav(),hero:hero(model)};',Object.assign(context,{model:{...businessHome.hero,actions:[...businessHome.hero.actions,{style:'ghost',href:'/contact',label:'Book now'}]}}));
 const dom=new JSDOM(context.result.legend+context.result.business+context.result.hero);
 const doc=dom.window.document;
 for(const key of ['legend_home','legend_contact','legend_protect','business_home','business_services','business_contact']) assert.ok(doc.querySelector(`[data-website-action-key="${key}"]`),key);
 const custom=[...doc.querySelectorAll('a')].find(a=>a.textContent==='Book now');assert.equal(custom.dataset.websiteActionKey,undefined);
 dom.window.close();
});
test('Protect base actions carry explicit keys alongside legacy instrumentation',()=>{
 const layout=readFileSync(new URL('../../Protect-Website/Views/Shared/_Layout.cshtml',import.meta.url),'utf8');
 const home=readFileSync(new URL('../../Protect-Website/Views/Home/Index.cshtml',import.meta.url),'utf8');
 for(const [legacy,key] of [['nav_home','protect_home'],['nav_contact','protect_contact'],['nav_quote','protect_quote']]) assert.ok(layout.includes(`data-cta="${legacy}" data-website-action-key="${key}"`));
 assert.ok(home.includes('data-cta="home_book_conversation" data-website-action-key="protect_schedule"'));
 assert.doesNotMatch(cms,/candidate\.href === href/);
});
