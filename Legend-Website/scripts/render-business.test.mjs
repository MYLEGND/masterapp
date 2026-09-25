import test from 'node:test';
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {parseHTML} from 'linkedom';
import {compileBusiness} from './render-business.mjs';
const business={id:'b72b8796-2b35-4eed-8d6d-7260976084ea',displayName:'Sample & Business',legalName:'Sample LLC'};
const document=()=>({elements:{},sectionOrder:{},extras:[],theme:{},pages:{}});

test('publication compiler process consumes stdin and returns compiled pages',()=>{
  const scriptPath=fileURLToPath(new URL('./render-business-cli.mjs',import.meta.url));
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


test('route manifest honors navigation order visibility nesting deletion and custom routes',async()=>{
  const value=document();
  value.pages['/']={title:'Home',navigation:{label:'Start',showInNavigation:true,order:20},elements:{},sectionOrder:{},extras:[]};
  value.pages['/about']={title:'About',navigation:{label:'About us',showInNavigation:false,order:10},elements:{},sectionOrder:{},extras:[]};
  value.pages['/services']={title:'Services',navigation:{label:'Work',showInNavigation:true,order:30,isDeleted:true},elements:{},sectionOrder:{},extras:[]};
  value.pages['/team']={title:'Team',navigation:{label:'Team',showInNavigation:true,parentPath:'/about',order:15},elements:{},sectionOrder:{},extras:[{id:'team-section',type:'section',sectionId:'home.root',style:{}},{id:'team-text',type:'text',sectionId:'extra:team-section',text:'Our team',style:{}}]};
  const result=await compileBusiness({business,document:value});
  assert.equal(result.version,2);
  assert.deepEqual(Object.keys(result.pages),['/','/about','/contact','/team']);
  assert.deepEqual(result.manifest.map(page=>page.route),['/contact','/about','/team','/']);
  assert.equal(result.manifest.find(page=>page.route==='/team').parentPath,'/about');
  assert.equal(result.manifest.some(page=>page.route==='/services'),false);
  const home=parseHTML(result.pages['/'].html).document;
  const links=[...home.querySelectorAll('.nav a')];
  assert.deepEqual(links.map(link=>link.textContent),['Contact','Team','Start']);
  assert.equal(links.find(link=>link.textContent==='Team').getAttribute('data-nav-parent'),'/about');
  assert.equal(links.some(link=>link.textContent==='About us'),false);
  assert.equal(result.pages['/services'],undefined);
  assert.match(result.pages['/team'].html,/Our team/);
});

test('template route can be renamed by tombstoning the old path and publishing the new path',async()=>{
  const value=document();
  value.pages['/services']={navigation:{isDeleted:true},elements:{},sectionOrder:{},extras:[]};
  value.pages['/work']={title:'Our work',templatePath:'/services',navigation:{label:'Our work',showInNavigation:true,order:5},elements:{},sectionOrder:{},extras:[{id:'work-text',type:'text',sectionId:'services.section.1',text:'Renamed services content',style:{}}]};
  const result=await compileBusiness({business,document:value});
  assert.equal(result.pages['/services'],undefined);
  assert.ok(result.pages['/work']);
  assert.equal(result.manifest.some(page=>page.route==='/services'),false);
  assert.equal(result.manifest.some(page=>page.route==='/work'),true);
  const work=parseHTML(result.pages['/work'].html).document;
  assert.ok(work.querySelector('.card-grid'));
  assert.match(result.pages['/work'].html,/Renamed services content/);
});


test('published reusable component resolves from one definition without copying component content into page extras',async()=>{
  const value=document();
  value.reusableComponents={
    'shared-callout':{
      id:'shared-callout',name:'Shared callout',kind:'block',elements:{},sectionOrder:{},
      extras:[{id:'root',type:'text',sectionId:'component.root',text:'One synchronized message',style:{widthPercent:80}}]
    }
  };
  value.pages['/']={
    title:'Home',navigation:{label:'Home',showInNavigation:true,order:0},elements:{},sectionOrder:{},
    extras:[{id:'callout-instance',type:'reusable',sectionId:'home.section.1',syncSourceId:'shared-callout',style:{widthPercent:100}}]
  };
  const result=await compileBusiness({business,document:value});
  const home=parseHTML(result.pages['/'].html).document;
  assert.match(home.querySelector('.cms-reusable-instance').textContent,/One synchronized message/);
  const embedded=JSON.parse(home.querySelector('#legend-cms-published-document').textContent).document;
  assert.equal(embedded.pages['/'].extras.filter(extra=>extra.type==='reusable').length,1);
  assert.equal(embedded.pages['/'].extras.some(extra=>extra.text==='One synchronized message'),false);
  assert.equal(embedded.reusableComponents['shared-callout'].extras[0].text,'One synchronized message');
});


test('dynamic product collection expands scoped item routes and binds each item without using the first row globally',async()=>{
  const value=document();
  value.collections={
    products:{id:'products',name:'Products',source:'commerce_products',fields:['slug','name','description']}
  };
  value.pages['/catalog']={
    title:'Catalog template',
    navigation:{label:'Catalog',showInNavigation:false,order:50},
    dynamicBinding:{collectionId:'products',itemKeyField:'slug',routePattern:'/products/{item}'},
    elements:{},sectionOrder:{},
    extras:[
      {id:'catalog-section',type:'section',sectionId:'catalog.root',style:{}},
      {id:'product-name',type:'text',sectionId:'extra:catalog-section',text:'Product name',dataBinding:{collectionId:'products',field:'name',target:'text'},style:{}},
      {id:'product-description',type:'text',sectionId:'extra:catalog-section',text:'Product description',dataBinding:{collectionId:'products',field:'description',target:'text'},style:{}}
    ]
  };
  const collections=[{
    id:'products',source:'commerce_products',isList:true,
    items:[
      {key:'red-shirt',fields:{slug:'red-shirt',name:'Red Shirt',description:'Red product'}},
      {key:'blue-shirt',fields:{slug:'blue-shirt',name:'Blue Shirt',description:'Blue product'}}
    ]
  }];
  const result=await compileBusiness({business,document:value,collections});
  assert.ok(result.pages['/products/red-shirt']);
  assert.ok(result.pages['/products/blue-shirt']);
  assert.match(result.pages['/products/red-shirt'].html,/Red Shirt/);
  assert.match(result.pages['/products/red-shirt'].html,/Red product/);
  assert.doesNotMatch(result.pages['/products/red-shirt'].html,/Blue product/);
  assert.match(result.pages['/products/blue-shirt'].html,/Blue Shirt/);
  assert.equal(result.manifest.find(page=>page.route==='/products/red-shirt').dynamic,true);
  assert.equal(result.manifest.find(page=>page.route==='/products/red-shirt').showInNavigation,false);
});

test('dynamic route compilation fails closed on unavailable collection invalid key or route collision',async()=>{
  const base=document();
  base.collections={products:{id:'products',name:'Products',source:'commerce_products',fields:['slug','name']}};
  base.pages['/catalog']={title:'Catalog',navigation:{showInNavigation:false},dynamicBinding:{collectionId:'products',itemKeyField:'slug',routePattern:'/products/{item}'},elements:{},sectionOrder:{},extras:[]};
  await assert.rejects(()=>compileBusiness({business,document:base,collections:[]}),/Dynamic website collection is unavailable/);

  await assert.rejects(()=>compileBusiness({business,document:base,collections:[{
    id:'products',source:'commerce_products',isList:true,items:[{key:'bad',fields:{slug:'bad/slug',name:'Bad'}}]
  }]}),/Dynamic website route key is invalid/);

  const collision=structuredClone(base);
  collision.pages['/products/red']={title:'Static collision',elements:{},sectionOrder:{},extras:[]};
  await assert.rejects(()=>compileBusiness({business,document:collision,collections:[{
    id:'products',source:'commerce_products',isList:true,items:[{key:'red',fields:{slug:'red',name:'Red'}}]
  }]}),/Dynamic website route conflicts/);
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
