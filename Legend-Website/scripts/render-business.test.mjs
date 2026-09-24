import test from 'node:test';
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {parseHTML} from 'linkedom';
import {compileBusiness,isDirectExecution} from './render-business.mjs';
const business={id:'b72b8796-2b35-4eed-8d6d-7260976084ea',displayName:'Sample & Business',legalName:'Sample LLC'};
const document=()=>({elements:{},sectionOrder:{},extras:[],theme:{},pages:{}});

test('publication compiler direct-execution detection uses filesystem-safe file URLs',()=>{
  const scriptUrl=new URL('./render-business.mjs',import.meta.url).href;
  const scriptPath=fileURLToPath(scriptUrl);
  assert.equal(isDirectExecution(scriptPath,scriptUrl),true);
  assert.equal(isDirectExecution(scriptPath+'-other',scriptUrl),false);
});

test('publication compiler process consumes stdin and returns compiled pages',()=>{
  const scriptPath=fileURLToPath(new URL('./render-business.mjs',import.meta.url));
  const stdout=execFileSync(process.execPath,[scriptPath],{
    cwd:fileURLToPath(new URL('..',import.meta.url)),
    input:JSON.stringify({business,document:document()}),
    encoding:'utf8',
    maxBuffer:40*1024*1024
  });
  const result=JSON.parse(stdout);
  assert.deepEqual(Object.keys(result.pages),['/','/about','/services','/contact']);
});


test('all normal business pages use canonical components, actual scoped name and public navigation without LEGEND facts',async()=>{
  const result=await compileBusiness({business,document:document()});
  assert.deepEqual(Object.keys(result.pages),['/','/about','/services','/contact']);
  for(const page of Object.values(result.pages)){
    const dom=parseHTML(page.html).document;
    assert.equal(dom.querySelector('.brand strong').textContent,business.displayName);
    assert.equal(dom.querySelector('meta[property="og:site_name"]').content,business.displayName);
    assert.ok(!/Berthony|MyLegnd, LLC|Christ-centered|Faith Fuels|connect@mylegnd/.test(page.html));
    assert.ok(!page.html.includes('legendEdit'));
    assert.equal(dom.querySelector('meta[name="robots"]'),null);
    assert.equal(dom.querySelector('link[rel="canonical"]').href,'__LEGEND_CANONICAL_URL__');
    for(const link of dom.querySelectorAll('.nav a'))assert.match(link.getAttribute('href'),/^\/(?:about|contact|services)?\/?$/);
    const published=dom.querySelector('#legend-cms-published-document');
    assert.ok(published);
    const runtime=JSON.parse(published.textContent).runtime;
    assert.equal(runtime.apiBase,'https://masterapp-protect.azurewebsites.net');
    assert.equal(runtime.trackingAsset,'/legend-public-tracking.js');
    assert.equal(runtime.metaSignalAsset,'/legend-public-meta-signal-intelligence.js');
    assert.equal(dom.querySelector('script[data-cms-context]'),null);
    assert.ok(dom.querySelector('script[src^="/legend-public-cms.js"]'));
  }
  assert.ok(parseHTML(result.pages['/'].html).document.querySelector('.hero .hero-copy'));
  assert.equal(parseHTML(result.pages['/'].html).document.querySelectorAll('#services .card .icon').length,0);
  assert.equal(parseHTML(result.pages['/services'].html).document.querySelectorAll('.card .icon').length,0);
  assert.ok(parseHTML(result.pages['/about'].html).document.querySelector('.story .story-rail'));
  assert.equal(parseHTML(result.pages['/contact'].html).document.querySelector('fieldset').hasAttribute('disabled'),false);
});

test('published text, URLs, section color and imported page are rendered before browser hydration',async()=>{
  const initial=await compileBusiness({business,document:document()});
  const dom=parseHTML(initial.pages['/'].html).document;
  const heading=dom.querySelector('h1').dataset.cmsId;
  const link=dom.querySelector('.hero .actions a').dataset.cmsId;
  const value=document();
  value.faviconImageDataUrl='https://masterapp-protect.azurewebsites.net/api/website-content/media/11111111-1111-1111-1111-111111111111';
  value.pages['/']={title:'Custom title',description:'Verified description',elements:{[heading]:{text:'Actual business headline'},[link]:{text:'Book an appointment',href:'https://example.com/book'},'section:home.section.1':{style:{backgroundColor:'#123456'}}},sectionOrder:{},extras:[]};
  value.pages['/team/history']={title:'Our history',description:'Our actual story',elements:{},sectionOrder:{},extras:[{id:'imported-section',type:'section',sectionId:'',style:{}},{id:'imported-text',type:'text',sectionId:'extra:imported-section',text:'Verified imported information',style:{}}]};
  const result=await compileBusiness({business,document:value});
  const page=parseHTML(result.pages['/'].html).document;
  assert.equal(page.title,'Custom title');
  assert.equal(page.querySelector('h1').textContent,'Actual business headline');
  assert.equal(page.querySelector('.hero .actions a').href,'https://example.com/book');
  assert.equal(page.querySelector('.hero').style.backgroundColor,'#123456');
  assert.equal(page.querySelector('.hero').style.backgroundImage,'none');
  assert.equal(page.querySelector('link[rel~="icon"]').href,value.faviconImageDataUrl);
  assert.equal(page.querySelector('link[rel~="icon"]').hasAttribute('type'),false);
  assert.ok(page.querySelector('.site-header').compareDocumentPosition(page.querySelector('main')) & 4);
  assert.ok(page.querySelector('main').compareDocumentPosition(page.querySelector('.site-footer')) & 4);
  assert.match(result.pages['/team/history'].html,/Verified imported information/);
  assert.ok(!parseHTML(result.pages['/team/history'].html).document.querySelector('main .hero'));
});

test('untrusted business facts remain text and unsafe page routes reject publication',async()=>{
  const result=await compileBusiness({business:{...business,displayName:'</script><script>alert(1)</script>'},document:document()});
  const dom=parseHTML(result.pages['/'].html).document;
  assert.equal(dom.querySelector('.brand strong').textContent,'</script><script>alert(1)</script>');
  assert.equal(dom.querySelectorAll('script:not([src]):not([type="application/json"])').length,0);
  for(const route of ['/../private','//other.example','/x?legendEdit=secret']){
    const value=document();value.pages[route]={};
    await assert.rejects(compileBusiness({business,document:value}),/Invalid website page route/);
  }
  await assert.rejects(compileBusiness({business:{},document:document()}),/business and document/);
});
