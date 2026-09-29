import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../../SHARED/WebsitePlatform/legend-public-cms.js', import.meta.url), 'utf8');
const publicCss = readFileSync(new URL('../../SHARED/WebsitePlatform/legend-public-web.css', import.meta.url), 'utf8');
const businessBuildSource = readFileSync(new URL('../../Legend-Website/scripts/build.mjs', import.meta.url), 'utf8');
const publicInquirySource = readFileSync(new URL('../../Legend-Design/legend-public-inquiry.js', import.meta.url), 'utf8');
const publicInquiryFormSource = readFileSync(new URL('../../SHARED/WebsitePlatform/public-inquiry-form.mjs', import.meta.url), 'utf8');
const publicInquiryFormCss = readFileSync(new URL('../../SHARED/WebsitePlatform/public-inquiry-form.css', import.meta.url), 'utf8');
const protectContactSource = readFileSync(new URL('../../Protect-Website/Views/Contact/Index.cshtml', import.meta.url), 'utf8');
const protectLayoutSource = readFileSync(new URL('../../Protect-Website/Views/Shared/_Layout.cshtml', import.meta.url), 'utf8');
const parfaitContactSource = readFileSync(new URL('../../ParfaitApp/Views/Contact/Index.cshtml', import.meta.url), 'utf8');
const parfaitLayoutSource = readFileSync(new URL('../../ParfaitApp/Views/Shared/_Layout.cshtml', import.meta.url), 'utf8');
const metaSignalSource = readFileSync(new URL('../../SHARED/WebsitePlatform/meta-signal-intelligence.js', import.meta.url), 'utf8');
const editorContractsSource = readFileSync(new URL('../../Infrastructure/WebsiteEditing/WebsiteEditorContracts.cs', import.meta.url), 'utf8');
const businessRenderSource = readFileSync(new URL('../../Legend-Website/scripts/render-business.mjs', import.meta.url), 'utf8');
const businessMiddlewareSource = readFileSync(new URL('../../Infrastructure/WebsiteRuntime/BusinessWebsiteMiddleware.cs', import.meta.url), 'utf8');
const legendWebConfigSource = readFileSync(new URL('../../Legend-Website/public/web.config', import.meta.url), 'utf8');
const agentContractSource = readFileSync(new URL('../../Infrastructure/WebsiteEditing/WebsiteStudioAgentContract.cs', import.meta.url), 'utf8');
const websitePlatformControllerSource = readFileSync(new URL('../../Infrastructure/WebsiteEditing/WebsitePlatformController.cs', import.meta.url), 'utf8');

// Full DOM integration: these tests execute the same shipped editor, not copied helpers.
import { JSDOM } from 'jsdom';

const canonicalBreakpoints = () => [
  {key:'mobile',label:'Mobile',minWidth:0,maxWidth:767,isSystem:true},
  {key:'tablet',label:'Tablet',minWidth:768,maxWidth:1199,isSystem:true},
  {key:'desktop',label:'Desktop',minWidth:1200,maxWidth:null,isSystem:true}
];

function canonicalNode(id,type,tag,props={}) {
  return {
    id,type,tag,
    signals:[],
    style:{},
    breakpointStyles:{},
    layout:{mode:type==='section'?'stack':'free',direction:'column'},
    breakpointLayouts:{},
    animations:[],
    children:[],
    ...props
  };
}

function canonicalDocument({
  title='Template title',
  secondTitle='Second section',
  href='https://old.example',
  imageUrl='https://images.example/a.png',
  imageAlt='original'
}={}) {
  return {
    version:3,
    faviconImageDataUrl:null,
    store:{enabled:false,navigationLabel:'Store',cartIcon:'cart',cartIconSizePx:28},
    breakpoints:canonicalBreakpoints(),
    shell:{header:[],footer:[]},
    pages:{
      '/':{
        title:'Home',
        description:'Canonical test page',
        navigation:{label:'Home',showInNavigation:true,order:0,isDeleted:false},
        dynamicBinding:null,
        composition:[
          canonicalNode('home.section.1','section','section',{
            children:[
              canonicalNode('home.h1.node.1','heading','h1',{text:title}),
              canonicalNode('home.a.node.1','link','a',{href,target:'_self',children:[
                canonicalNode('home.a.node.1.text','text','span',{text:'Original link'})
              ]}),
              canonicalNode('home.img.node.1','image','img',{mediaUrl:imageUrl,alt:imageAlt})
            ]
          }),
          canonicalNode('home.section.2','section','section',{
            children:[canonicalNode('home.h2.node.1','heading','h2',{text:secondTitle})]
          })
        ]
      }
    },
    reusableComponents:{},
    collections:{},
    theme:{}
  };
}

function canonicalBusinessNavigation(doc=canonicalDocument()) {
  doc.shell={
    header:[canonicalNode('shell.header','container','header',{className:'site-header',children:[
      canonicalNode('shell.primary-nav','container','nav',{className:'nav',systemKey:'primary_navigation',children:[]})
    ]})],
    footer:[canonicalNode('shell.footer','container','footer',{className:'site-footer',children:[]})]
  };
  return doc;
}

function visitCanonicalNodes(nodes,visitor) {
  for(const node of nodes || []) {
    visitor(node);
    visitCanonicalNodes(node.children,visitor);
  }
}

function canonicalNodes(document,path='/') {
  const nodes=[];
  visitCanonicalNodes(document?.pages?.[path]?.composition || [],node=>nodes.push(node));
  return nodes;
}

function canonicalNodeById(document,id,path='/') {
  return canonicalNodes(document,path).find(node=>node.id===id) || null;
}

function canonicalNodeByType(document,type,path='/') {
  return canonicalNodes(document,path).find(node=>node.type===type) || null;
}
async function domFixture({siteKey='legend',doc=canonicalDocument(),store=null,denied=false,search='?legendEdit=ticket',pathname='/',origin='https://site.example',apiBase='',business=null,pages=[],agentSlug='',pagePrefix='',editorAuthorizationUrl='',ctaCatalog=[],signalCatalog=null,agentContract=null,qualityPayload=null,mediaPayload=null,mediaUploadPayload=null,sourceValidationPayload=null,sourceValidationStatus=200,capabilities=null,legacyMigration=null,signalTestPayload=null,signalHealthPayload=null,collaborationPayload=null,commentPayload=null,viewportWidth=1024,html='<!doctype html><html><head><style>h1{font-size:64px}section{padding:24px}</style></head><body data-page-key="home"><main><section><h1>Template title</h1><a href="https://old.example"><span>Original link</span></a><img src="https://images.example/a.png" alt="original"></section><section><h2>Second section</h2></section></main></body></html>'}={}) {
  const dom = new JSDOM(html, {url:origin+pathname+search,runScripts:'outside-only'});
  const {window:w}=dom; const calls=[]; const animations=[];
  Object.defineProperty(w,'innerWidth',{value:viewportWidth,writable:true,configurable:true});
  w.matchMedia=()=>({matches:false});
  w.HTMLElement.prototype.animate=function(keyframes,options){ const record={element:this,keyframes,options,cancelled:false}; animations.push(record); return {cancel(){record.cancelled=true;}}; };
  w.LEGEND_PUBLIC_CMS_CONTEXT={siteKey,apiBase,businessId: business?.id || '',pages,agentSlug,pagePrefix,editorAuthorizationUrl};
  w.HTMLDialogElement.prototype.showModal = function() {}; w.HTMLDialogElement.prototype.close = function() { this.dispatchEvent(new w.Event('close')); };
  const alerts=[];
  w.CSS={escape: v=>String(v).replaceAll('"','\\"')}; w.alert=value=>alerts.push(String(value)); w.confirm=()=>true;
  w.fetch=async(url,init={})=> { calls.push({url:String(url),...init}); const parsed=new URL(String(url)); const body=typeof init.body==='string'?JSON.parse(init.body):null; if(parsed.pathname.endsWith('/manage/quality')) return {ok:!denied,status:denied?401:200,json:async()=>qualityPayload || {source:'saved_draft_server',revision:1,errorCount:0,warningCount:0,checks:[]}}; if(parsed.pathname.endsWith('/manage/media') && (!init.method || init.method==='GET')) return {ok:!denied,status:denied?401:200,json:async()=>mediaPayload || {assets:[]}}; if(parsed.pathname.endsWith('/manage/media') && init.method==='POST'){ const file=init.body?.get?.('file'); const id='33333333-3333-3333-3333-333333333333'; return {ok:!denied,status:denied?401:200,json:async()=>mediaUploadPayload || {id,name:file?.name || 'upload',url:'https://site.example/api/website-content/media/'+id,contentType:file?.type || 'image/png',sizeBytes:file?.size || 1024,createdUtc:'2026-09-28T00:00:00Z'}}; } if(parsed.pathname.endsWith('/manage/source/validate')) { const status=denied?401:sourceValidationStatus; return {ok:status>=200&&status<300,status,json:async()=>sourceValidationPayload || {source:'legend_site_source_validation',baseRevision:'r1',persisted:false,published:false,proposedDocument:doc,sourceMap:{}}}; } if(parsed.pathname.endsWith('/manage/signals/test')) return {ok:!denied,status:denied?401:200,json:async()=>signalTestPayload || {source:'website_signal_private_dry_run',dryRun:true,persisted:false,metaDispatched:false,stages:{mappingValidated:true,browserTriggerSupported:true,browserAnalyticsWouldBeAccepted:true,browserPixelWouldInvoke:false,serverOutcomeRequired:false},destination:{ownerType:'business',browserPixelConfigured:false,serverCapiConfigured:false}}}; if(parsed.pathname.endsWith('/manage/signals/health')) return {ok:!denied,status:denied?401:200,json:async()=>signalHealthPayload || {source:'website_signal_existing_authorities',publishedVersionId:null,binding:{matchingConsent:'not_requested'},destination:{ownerType:'business',browserPixelConfigured:false,serverCapiConfigured:false},analytics:[],meta:[]}}; if(parsed.pathname.endsWith('/manage/collaboration') && (!init.method || init.method==='GET')) return {ok:!denied,status:denied?401:200,json:async()=>collaborationPayload || {source:'website_studio_collaboration',revision:'r1',role:{roleKey:'founder',label:'Founder',canPublish:true},collaborators:[{roleKey:'founder',displayName:'Founder',canPublish:true}],comments:[]}}; if(parsed.pathname.endsWith('/manage/collaboration/comments') || parsed.pathname.endsWith('/manage/collaboration/comments/status')) return {ok:!denied,status:denied?401:200,json:async()=>commentPayload || {source:'website_studio_collaboration',comment:{id:'comment-1',status:'open'}}}; return {ok:!denied,status:denied?401:200,json:async()=>({siteKey,business,store,revision:'r'+calls.length,document:body?.document || doc,legacyMigration:legacyMigration || undefined,ctaCatalog:{options:ctaCatalog},signalCatalog:signalCatalog || undefined,agentContract:agentContract || undefined,capabilities:capabilities || undefined})}; };
  w.eval(source);
  // JSDOM dispatches initial readiness itself; wait for the fetch continuation.
  await new Promise(resolve=>setTimeout(resolve,0));
  const click=selector=>w.document.querySelector(selector).dispatchEvent(new w.MouseEvent('click',{bubbles:true,cancelable:true}));
  const input=(selector,value)=> {const el=w.document.querySelector(selector);el.value=value;el.dispatchEvent(new w.Event('input',{bubbles:true}));};
  const change=(selector,value)=> {const el=w.document.querySelector(selector);el.value=value;el.dispatchEvent(new w.Event('change',{bubbles:true}));};
  const editSelected=(value)=> {const el=w.document.querySelector('.legend-cms-selected');assert.ok(el);const edit=w.document.querySelector('#legend-cms-edit-text');if(edit&&!edit.hidden)edit.click();el.textContent=value;el.dispatchEvent(new w.Event('beforeinput',{bubbles:true,cancelable:true}));el.dispatchEvent(new w.Event('input',{bubbles:true}));};
  const save=async()=>{click('#legend-cms-save');input('#legend-cms-draft-name','Test variation');click('#legend-cms-draft-submit');await new Promise(resolve=>setTimeout(resolve,0));return JSON.parse(calls.at(-1).body).document;};
  return {w,calls,animations,alerts,click,input,change,editSelected,save,close:()=>w.close()};
}

test('canonical startup fills navigation and image accessibility defaults and prevents routine width overflow',async()=>{
  const doc=canonicalDocument();
  delete doc.pages['/'].navigation.label;
  const image=canonicalNodeByType(doc,'image');
  delete image.alt;
  image.mediaUrl='/assets/hero-photo.png';

  const f=await domFixture({
    doc,
    html:'<!doctype html><html><head></head><body data-page-key="home"><main><section><h1>Home</h1><img src="/assets/hero-photo.png"><p>averylongunbrokencontenttokenaverylongunbrokencontenttokenaverylongunbrokencontenttoken</p></section></main></body></html>'
  });
  try{
    assert.equal(f.w.document.querySelector('main img')?.getAttribute('alt'),'Hero Photo');
    const saved=await f.save();
    assert.equal(saved.pages['/'].navigation.label,'Home');
    assert.equal(canonicalNodeByType(saved,'image')?.alt,'Hero Photo');
    assert.match(source,/main \*\{min-width:0;box-sizing:border-box\}/);
    assert.match(source,/overflow-wrap:anywhere;word-break:normal;white-space:normal/);
    assert.match(source,/\[data-cms-editable="true"\]\{min-width:0;max-width:100%;box-sizing:border-box;overflow-wrap:anywhere\}/);
  }finally{f.close();}
});

test('existing v3 startup artifacts are deleted only when semantically empty',async()=>{
  const doc=canonicalDocument();
  doc.pages['/'].composition[0].children.push(
    canonicalNode('home.empty.icon','text','span',{className:'icon',text:''}),
    canonicalNode('home.meaningful.icon','text','p',{className:'icon',text:'Meaningful text stays'}),
    canonicalNode('home.hero.visual','container','div',{className:'hero-mark',children:[
      canonicalNode('home.hero.halo','container','div',{className:'halo'}),
      canonicalNode('home.hero.empty','text','span',{text:''})
    ]})
  );
  const f=await domFixture({doc});
  try{
    const saved=await f.save();
    const ids=canonicalNodes(saved).map(node=>node.id);
    assert.equal(ids.includes('home.empty.icon'),false);
    assert.equal(ids.includes('home.hero.visual'),false);
    assert.equal(ids.includes('home.hero.halo'),false);
    assert.equal(ids.includes('home.hero.empty'),false);
    assert.equal(ids.includes('home.meaningful.icon'),true);
  }finally{f.close();}
});

test('materialization never invents publishable links from unmanaged runtime buttons or empty anchors',()=>{
  assert.match(source,/const unmanagedInteractiveControl\s*=\s*[\s\S]*tag === 'button' && !actionKey[\s\S]*tag === 'a' && !actionKey && !rawHref/);
  assert.match(source,/const presentationOnlyControl = runtimePresentationControl \|\| unmanagedInteractiveControl/);
  assert.match(source,/const projectedTag = presentationOnlyControl \? 'span' : tag/);
  assert.match(source,/actionKey:presentationOnlyControl \? null : \(actionKey \|\| null\)/);
  assert.match(source,/href:presentationOnlyControl \? null : \(rawHref \|\| null\)/);
});

test('Protect template-backed runtime controls remain presentation-only and cannot become free publishable links',async()=>{
  const doc=canonicalDocument();
  doc.pages['/quote/life']={
    title:'Life Insurance',
    description:'Protected runtime form',
    navigation:{label:'Life Insurance',showInNavigation:true,order:10,isDeleted:false},
    dynamicBinding:null,
    systemTemplateKey:'protect_template:life_wizard',
    composition:[]
  };
  const html='<!doctype html><html><head></head><body data-page-key="quote-life"><main><section class="quote-page"><form id="lifeWizardForm" data-form-key="quote_life" data-ajax-submit="true"><fieldset><div><button type="button">Next</button><a href="/Quote/Life/results">Review</a></div></fieldset></form></section></main></body></html>';
  const f=await domFixture({siteKey:'protect',doc,pathname:'/Quote/Life',html});
  try{
    const saved=await f.save();
    const runtime=canonicalNodes(saved,'/quote/life').find(node=>String(node.systemKey||'').startsWith('protect_runtime_form:'));
    assert.ok(runtime);
    const descendants=[];
    visitCanonicalNodes(runtime.children,node=>descendants.push(node));
    const next=descendants.find(node=>node.text==='Next');
    const review=descendants.find(node=>node.text==='Review');
    assert.ok(next);
    assert.ok(review);
    for(const control of [next,review]){
      assert.equal(control.type,'text');
      assert.equal(control.tag,'span');
      assert.equal(control.actionKey ?? null,null);
      assert.equal(control.href ?? null,null);
      assert.equal(control.dataBinding ?? null,null);
      assert.deepEqual(control.signals || [],[]);
    }
  }finally{f.close();}
});

test('v3 canonical composition renders natively and canvas/source share the same node',async()=>{
  const doc={version:3,store:{enabled:false,navigationLabel:'Store',cartIcon:'cart',cartIconSizePx:28},pages:{'/':{
    title:'Home',description:'Canonical home',navigation:{label:'Home',showInNavigation:true,order:0,isDeleted:false},composition:[
      {id:'home.hero',type:'section',tag:'section',className:'hero',signals:[],style:{},breakpointStyles:{},layout:{mode:'stack',direction:'column'},breakpointLayouts:{},animations:[],children:[
        {id:'home.hero.title',type:'heading',tag:'h1',text:'Canonical title',signals:[],style:{},breakpointStyles:{},layout:{mode:'free',direction:'column'},breakpointLayouts:{},animations:[],children:[]},
        {id:'home.hero.cta',type:'cta',tag:'a',text:'Contact us',actionKey:'legend_contact',href:'/contact',signals:[],style:{},breakpointStyles:{},layout:{mode:'free',direction:'column'},breakpointLayouts:{},animations:[],children:[]}
      ]}
    ]}},breakpoints:[{key:'mobile',label:'Mobile',minWidth:0,maxWidth:767,isSystem:true},{key:'tablet',label:'Tablet',minWidth:768,maxWidth:1199,isSystem:true},{key:'desktop',label:'Desktop',minWidth:1200,maxWidth:null,isSystem:true}],theme:{},reusableComponents:{},collections:{}};
  const f=await domFixture({doc,ctaCatalog:[{key:'legend_contact',group:'Contact',label:'Contact',defaultText:'Contact',href:'/contact',analyticsEventName:'cta_click',behaviorKey:'cta_click'}]});
  try{
    const heading=f.w.document.querySelector('main h1');
    assert.equal(heading.textContent,'Canonical title');
    assert.equal(heading.dataset.cmsCompositionId,'home.hero.title');
    assert.equal(f.w.document.querySelector('main a').dataset.websiteActionKey,'legend_contact');
    f.click('main h1');
    f.click('[data-open="source"]');
    assert.equal(f.w.document.querySelector('#legend-cms-source-scope').value,'selection');
    let parsed=JSON.parse(f.w.document.querySelector('#legend-cms-site-source').value);
    assert.equal(parsed.id,'home.hero.title');
    f.input('#legend-cms-width','55');
    parsed=JSON.parse(f.w.document.querySelector('#legend-cms-site-source').value);
    assert.equal(parsed.style.widthPercent,55);
    const saved=await f.save();
    assert.equal(saved.pages['/'].composition[0].children[0].style.widthPercent,55);
    assert.equal(Object.hasOwn(saved.pages['/'],'elements'),false);
    assert.equal(Object.hasOwn(saved.pages['/'],'extras'),false);
    assert.equal(Object.hasOwn(saved.pages['/'],'sectionOrder'),false);
  }finally{f.close();}
});

test('v3 selected source applies only validated server projection then uses normal save authority',async()=>{
  const original={version:3,pages:{'/':{title:'Home',description:'',navigation:{label:'Home',showInNavigation:true,order:0,isDeleted:false},composition:[
    {id:'hero',type:'section',tag:'section',signals:[],style:{},breakpointStyles:{},layout:{mode:'stack',direction:'column'},breakpointLayouts:{},animations:[],children:[
      {id:'hero.title',type:'heading',tag:'h1',text:'Before',signals:[],style:{},breakpointStyles:{},layout:{mode:'free',direction:'column'},breakpointLayouts:{},animations:[],children:[]}
    ]}
  ]}},breakpoints:[],theme:{},reusableComponents:{},collections:{},store:{enabled:false}};
  const proposed=structuredClone(original); proposed.pages['/'].composition[0].children[0].text='After';
  const f=await domFixture({doc:original,sourceValidationPayload:{source:'legend_site_source_validation',baseRevision:'r1',persisted:false,published:false,proposedDocument:proposed,sourceMap:{}}});
  try{
    f.click('main h1'); f.click('[data-open="source"]');
    const sourceInput=f.w.document.querySelector('#legend-cms-site-source');
    const selected=JSON.parse(sourceInput.value); selected.text='After';
    sourceInput.value=JSON.stringify(selected,null,2);
    sourceInput.dispatchEvent(new f.w.Event('input',{bubbles:true}));
    f.click('#legend-cms-source-apply');
    await new Promise(resolve=>setTimeout(resolve,0));
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.equal(f.w.document.querySelector('main h1').textContent,'After');
    const validation=f.calls.find(call=>call.method==='POST' && call.url.endsWith('/manage/source/validate'));
    assert.ok(validation);
    const save=f.calls.find(call=>{
      if(call.method!=='POST' || !call.url.endsWith('/manage')) return false;
      const persisted=JSON.parse(call.body).document;
      return persisted?.version===3 &&
        Array.isArray(persisted?.pages?.['/']?.composition) &&
        !Object.hasOwn(persisted.pages['/'],'elements') &&
        !Object.hasOwn(persisted.pages['/'],'extras') &&
        !Object.hasOwn(persisted.pages['/'],'sectionOrder');
    });
    assert.ok(save);
  }finally{f.close();}
});

test('Protect agent-prefixed home is the canonical root during one-way v3 materialization',async()=>{
  const f=await domFixture({
    siteKey:'protect',
    pathname:'/a/legend',
    agentSlug:'legend',
    pagePrefix:'/a/legend',
    legacyMigration:{},
    capabilities:{compositionV3:true}
  });
  try{
    await new Promise(resolve=>setTimeout(resolve,20));
    assert.deepEqual(f.alerts,[]);
    const save=f.calls.find(call=>{
      if(call.method!=='POST' || !call.url.endsWith('/api/website-content/manage')) return false;
      const body=JSON.parse(call.body);
      return body.document?.version===3;
    });
    assert.ok(save,'materialization should save canonical v3 without waiting on a hidden root frame');
    const persisted=JSON.parse(save.body).document;
    assert.ok(persisted.pages['/']);
    assert.equal(Object.hasOwn(persisted.pages,'/a/legend'),false);
  }finally{f.close();}
});

test('Protect nested agent URL resolves to the canonical page route',async()=>{
  const doc=canonicalDocument();
  doc.pages['/coverage']={
    title:'Coverage',
    description:'Canonical coverage',
    navigation:{label:'Coverage',showInNavigation:true,order:1,isDeleted:false},
    dynamicBinding:null,
    composition:[canonicalNode('coverage.hero','section','section',{children:[
      canonicalNode('coverage.title','heading','h1',{text:'Coverage canonical'})
    ]})]
  };
  const f=await domFixture({
    siteKey:'protect',
    doc,
    pathname:'/a/legend/coverage',
    agentSlug:'legend',
    pagePrefix:'/a/legend'
  });
  try{
    assert.equal(f.w.document.querySelector('main h1')?.textContent,'Coverage canonical');
    assert.deepEqual(f.alerts,[]);
  }finally{f.close();}
});


test('Protect collapses legacy agent-prefixed v3 page keys to the canonical page identity',async()=>{
  const doc=canonicalDocument();
  doc.pages['/a/legend/contact']={
    title:'Contact',
    description:'Scoped legacy key',
    navigation:{label:'Contact',showInNavigation:true,order:10,isDeleted:false},
    dynamicBinding:null,
    composition:[canonicalNode('contact.section','section','section',{children:[
      canonicalNode('contact.title','heading','h1',{text:'Canonical contact from scoped key'})
    ]})]
  };
  delete doc.pages['/'];
  const f=await domFixture({
    siteKey:'protect',
    doc,
    pathname:'/a/legend/contact',
    agentSlug:'legend',
    pagePrefix:'/a/legend'
  });
  try{
    assert.equal(f.w.document.querySelector('main h1')?.textContent,'Canonical contact from scoped key');
    const saved=await f.save();
    assert.ok(saved.pages['/contact']);
    assert.equal(Object.hasOwn(saved.pages,'/a/legend/contact'),false);
  }finally{f.close();}
});

test('runtime navigation toggle is deleted from canonical v3 and recreated only as locked shell chrome',async()=>{
  const doc=canonicalBusinessNavigation();
  const header=doc.shell.header[0];
  header.children.unshift(canonicalNode('shell.header.button.node.75','link','button',{
    className:'nav-toggle',
    text:'Menu'
  }));
  const f=await domFixture({doc});
  try{
    const runtimeToggle=f.w.document.querySelector('[data-public-nav-toggle]');
    assert.ok(runtimeToggle);
    assert.equal(runtimeToggle.dataset.cmsCompositionId,undefined);
    assert.equal(runtimeToggle.dataset.cmsLocked,'true');
    const saved=await f.save();
    const serialized=JSON.stringify(saved);
    assert.equal(serialized.includes('shell.header.button.node.75'),false);
    assert.equal(serialized.includes('"className":"nav-toggle"'),false);
  }finally{f.close();}
});

test('business header navigation projects one canonical page catalog without preview-title duplicates',async()=>{
  const doc=canonicalBusinessNavigation();
  doc.pages['/about']={
    title:'About | Business website preview',
    description:'',
    navigation:{label:null,showInNavigation:true,order:10,isDeleted:false},
    dynamicBinding:null,
    composition:[canonicalNode('about.section','section','section',{children:[
      canonicalNode('about.title','heading','h1',{text:'About'})
    ]})]
  };
  const business={id:'11111111-1111-1111-1111-111111111111',displayName:'CAMO'};
  const f=await domFixture({
    siteKey:'business',
    doc,
    business,
    pages:[{path:'/',label:'Home',order:0},{path:'/about',label:'About',order:10}],
    pathname:'/business-preview/about',
    search:'?businessId=11111111-1111-1111-1111-111111111111&legendEdit=ticket',
    html:'<!doctype html><html><head></head><body data-page-key="about"><header class="site-header"><nav class="nav" data-public-nav><a>Stale Home</a></nav><nav class="nav" data-public-nav><a>Duplicate</a></nav></header><main></main></body></html>'
  });
  try{
    const navs=f.w.document.querySelectorAll('.site-header nav.nav,[data-public-nav]');
    assert.equal(navs.length,1);
    const labels=[...navs[0].querySelectorAll('[data-legend-page-nav="true"]')].map(node=>node.textContent);
    assert.deepEqual(labels,['Home','About']);
    assert.equal(labels.some(label=>label.includes('Business website preview')),false);
    const saved=await f.save();
    assert.equal(saved.pages['/about'].title,'About');
  }finally{f.close();}
});

test('Website Studio canvas keeps public viewport typography and mobile controls stay inside the viewport',()=>{
  assert.ok(source.includes('body.legend-cms-editing{display:block'));
  assert.ok(source.includes('.legend-cms-preview{width:100vw'));
  assert.equal(source.includes('grid-template-columns:minmax(0,1fr) minmax(20rem,24rem)'),false);
  assert.equal(publicCss.includes('@container legend-public-preview'),false);
  assert.equal(publicCss.includes('container:legend-public-preview'),false);
  assert.ok(source.includes('.legend-cms-agent-contract pre{display:block;width:100%;max-width:100%;min-width:0;margin:0;white-space:pre-wrap;overflow-wrap:anywhere;word-break:break-word'));
  assert.match(source,/\.legend-cms-panel>\*\{min-width:0;max-width:100%\}/);
  assert.match(source,/\.legend-cms-panel\{[\s\S]*max-height:min\(46dvh,430px\)[\s\S]*border-radius:0 0 14px 14px/);
  assert.match(source,/\.legend-cms-bar\{[\s\S]*grid-template-columns:repeat\(5,minmax\(0,1fr\)\)[\s\S]*position:sticky/);
  assert.match(source,/\.legend-cms-primary-tabs\{[\s\S]*display:flex[\s\S]*overflow-x:auto/);
  assert.match(source,/\.legend-cms-menu\{grid-template-columns:repeat\(3,minmax\(0,1fr\)\)/);
  assert.equal(source.includes('padding:max(72px,calc(env(safe-area-inset-top) + 60px))'),false);
  assert.equal(source.includes('grid-template-columns:repeat(2,minmax(0,1fr));position:sticky;top:max(56px'),false);
});

test('GPT workspace consumes one canonical node grammar and teaches creative safe authoring',async()=>{
  const prompt='SERVER CANONICAL CONTRACT: actionKey systemKey signals form_field_semantics publish_authority';
  const f=await domFixture({agentContract:{schema:'legend-website-studio-agent/v1',promptTemplate:prompt}});
  try{
    f.click('[data-open="gpt"]');
    assert.equal(f.w.document.querySelector('#legend-cms-agent-contract-script')?.textContent,prompt);
    assert.match(agentContractSource,/Turn the user's intent into a polished, distinctive, responsive website/);
    assert.match(agentContractSource,/Modify the existing canonical graph whenever possible/);
    assert.match(agentContractSource,/GRAPH STRUCTURE/);
    assert.match(agentContractSource,/CANONICAL NODE TYPE -> TAG GRAMMAR/);
    assert.match(agentContractSource,/WebsiteCompositionSchema\.PromptGrammar/);
    assert.match(agentContractSource,/Every node ID must be non-empty and globally unique/);
    assert.match(agentContractSource,/Do not create duplicate desktop\/mobile copies/);
    assert.match(agentContractSource,/VALID SOURCE SHAPE EXAMPLE/);
    assert.match(agentContractSource,/EDITING ALGORITHM/);
    assert.match(agentContractSource,/Never solve validation by deleting protected semantics/);
    assert.match(agentContractSource,/FirstName, LastName, Phone, Email, Message/);
    assert.match(agentContractSource,/Keep the real runtime form mounted/);
    assert.match(agentContractSource,/zero-production-signal environment/);
    assert.match(editorContractsSource,/public static class WebsiteCompositionSchema/);
    for(const type of ['section','container','heading','text','cta','link','image','video','form','embed','spacer','reusable'])
      assert.ok(editorContractsSource.includes(`["${type}"]`));
  }finally{f.close();}
});

test('LEGEND static host permits only same-origin Website Studio materialization frames',()=>{
  assert.match(legendWebConfigSource,/X-Frame-Options" value="SAMEORIGIN"/);
  assert.equal(/X-Frame-Options" value="DENY"/.test(legendWebConfigSource),false);
});

test('pre-v3 migration is an explicit read-only one-way materialization boundary',()=>{
  assert.match(source,/payload\.capabilities\?\.compositionV3===true/);
  assert.match(source,/legendMaterialize/);
  assert.match(source,/materializeCanonicalSite/);
  assert.match(source,/if\(!legacyMigration\) return true;/);
  assert.match(source,/next\.version=3;/);
  assert.match(source,/legacyMigration=null;/);
  assert.equal(source.includes('page.elements={}'),false);
  assert.equal(source.includes('page.extras=[]'),false);
  assert.equal(source.includes('page.sectionOrder={}'),false);
});

test('public runtime binds autonomous Meta contact tracking to the actual generated inquiry form id',()=>{
  assert.ok(source.includes("formId: inquiryForm?.id || inquiryForm?.dataset.formKey || ''"));
  assert.ok(metaSignalSource.includes('function wireContactInputs()'));
  assert.ok(metaSignalSource.includes("emitSignal('ContactInputStarted'"));
  assert.ok(metaSignalSource.includes("emitSignal('PhoneFieldCompleted'"));
  assert.ok(metaSignalSource.includes("emitSignal('RequiredContactFieldsCompleted'"));
  assert.ok(source.includes('Automatic form analytics'));
  assert.ok(source.includes('No mapping is required.'));
});

test('published visual signal executor has no browser path for confirmed server outcomes',()=>{
  assert.ok(source.includes("const allowedTriggers = new Set(["));
  for(const trigger of ['viewed','click','form_started','submit_attempt','field_started','validation_failed','field_completed','scroll_threshold'])
    assert.ok(source.includes(`'${trigger}'`));
  assert.equal(source.includes("case 'submission_saved':"),false);
  assert.equal(source.includes("case 'booking_confirmed':"),false);
  assert.equal(source.includes("case 'payment_confirmed':"),false);
  assert.ok(source.includes("analytics.trackBinding(binding"));
  assert.ok(source.includes("source: 'website_signal_binding'"));
});

test('canonical v3 runtime inherits base style and switches breakpoint style and layout on resize',async()=>{
  const doc=canonicalDocument({title:'Responsive heading'});
  const headingModel=canonicalNodeById(doc,'home.h1.node.1');
  headingModel.style={widthPercent:80};
  headingModel.breakpointStyles={mobile:{widthPercent:100,fontScale:0.75},desktop:{widthPercent:60}};
  const sectionModel=canonicalNodeById(doc,'home.section.1');
  sectionModel.layout={mode:'grid',columns:3,gapPx:24};
  sectionModel.breakpointLayouts={mobile:{mode:'stack',direction:'column',gapPx:12}};
  const f=await domFixture({doc,search:'',viewportWidth:500});
  try {
    const heading=f.w.document.querySelector('main h1');
    const section=f.w.document.querySelector('main section');
    assert.equal(heading.textContent,'Responsive heading');
    assert.equal(heading.style.width,'100%');
    assert.equal(section.style.display,'flex');
    assert.equal(section.style.flexDirection,'column');
    assert.equal(section.style.gap,'12px');
    f.w.innerWidth=1400;
    f.w.dispatchEvent(new f.w.Event('resize'));
    assert.equal(heading.style.width,'60%');
    assert.equal(section.style.display,'grid');
    assert.equal(section.style.gridTemplateColumns,'repeat(3,minmax(0,1fr))');
    assert.equal(section.style.gap,'24px');
  } finally { f.close(); }
});



test('signal editor private test saves draft first but sends no production signal and shows dry-run result',async()=>{
  const catalog={events:[{name:'ViewContent',category:'page',metaEligible:true,requiresServerOutcome:false,triggers:['viewed']}],matchingFields:[],runtimeEnabled:true};
  const f=await domFixture({
    signalCatalog:catalog,
    signalTestPayload:{
      source:'website_signal_private_dry_run',dryRun:true,persisted:false,metaDispatched:false,
      destination:{ownerType:'business',browserPixelConfigured:false,serverCapiConfigured:false},
      stages:{mappingValidated:true,browserTriggerSupported:true,browserAnalyticsWouldBeAccepted:true,browserPixelWouldInvoke:false,serverOutcomeRequired:false}
    }
  });
  try{
    f.click('main h1');
    f.click('[data-open="signals"]');
    const add=[...f.w.document.querySelectorAll('#legend-cms-signal-controls button')].find(button=>button.textContent==='Add advanced custom mapping');
    assert.ok(add); add.click();
    const send=f.w.document.querySelector('#legend-cms-signal-controls select');
    send.value='analytics'; send.dispatchEvent(new f.w.Event('change',{bubbles:true}));
    const testButton=f.w.document.querySelector('[data-signal-test]');
    assert.ok(testButton);
    testButton.click();
    await new Promise(resolve=>setTimeout(resolve,0));
    await new Promise(resolve=>setTimeout(resolve,0));

    const saveCall=f.calls.find(call=>call.method==='POST' && new URL(call.url).pathname==='/api/website-content/manage');
    const testCall=f.calls.find(call=>new URL(call.url).pathname.endsWith('/manage/signals/test'));
    assert.ok(saveCall);
    assert.ok(testCall);
    const request=JSON.parse(testCall.body);
    assert.equal(request.pagePath,'/');
    assert.equal(request.elementId,'home.h1.node.1');
    assert.equal(request.bindingId,testButton.dataset.signalTest);
    assert.equal(f.calls.some(call=>new URL(call.url).pathname==='/analytics/meta-signal'),false);
    const status=f.w.document.querySelector(`[data-signal-diagnostics="${testButton.dataset.signalTest}"]`).textContent;
    assert.match(status,/PRIVATE TEST/);
    assert.match(status,/no analytics or Meta event sent/);
    assert.match(status,/Analytics ingest: would accept/);
  } finally { f.close(); }
});

test('signal editor delivery health renders existing authoritative evidence without credential material',async()=>{
  const bindingId='11111111111111111111111111111111';
  const doc=canonicalDocument();
  canonicalNodeById(doc,'home.h1.node.1').signals=[{id:bindingId,trigger:'viewed',eventName:'ViewContent',deliveryMode:'analytics',oncePerSession:true,matchingFields:[]}];
  const catalog={events:[{name:'ViewContent',category:'page',metaEligible:true,requiresServerOutcome:false,triggers:['viewed']}],matchingFields:[],runtimeEnabled:true};
  const f=await domFixture({
    doc,signalCatalog:catalog,
    signalHealthPayload:{
      source:'website_signal_existing_authorities',
      publishedVersionId:'22222222-2222-2222-2222-222222222222',
      binding:{matchingConsent:'not_requested'},
      destination:{ownerType:'business',browserPixelConfigured:true,serverCapiConfigured:true,testEventCodeConfigured:false},
      analytics:[{eventType:'ViewContent',receivedUtc:'2026-09-24T20:00:00Z'}],
      meta:[{eventName:'ViewContent',metaBrowserSent:true,metaServerSent:false,dispatch:{status:'not_server_authority'}}]
    }
  });
  try{
    f.click('main h1'); f.click('[data-open="signals"]');
    const health=f.w.document.querySelector(`[data-signal-health="${bindingId}"]`);
    assert.ok(health); health.click();
    await new Promise(resolve=>setTimeout(resolve,0));
    const host=f.w.document.querySelector(`[data-signal-diagnostics="${bindingId}"]`);
    assert.match(host.textContent,/Destination owner: business/);
    assert.match(host.textContent,/Pixel: configured/);
    assert.match(host.textContent,/CAPI: configured/);
    assert.match(host.textContent,/Analytics accepted/);
    assert.match(host.textContent,/Meta signal/);
    assert.doesNotMatch(host.textContent,/token|ciphertext/i);
  } finally { f.close(); }
});

test('Collaboration reads canonical roles and posts private selection-anchored comments',async()=>{
  const collaborationPayload={
    source:'website_studio_collaboration',revision:'r1',
    role:{roleKey:'owner',label:'Owner',canComment:true,canResolveAll:true,canPublish:true},
    collaborators:[
      {roleKey:'owner',displayName:'Business Owner',canManageStorefront:true,canPublish:true},
      {roleKey:'member',displayName:'Website Editor',canManageStorefront:true,canPublish:false}
    ],
    comments:[]
  };
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'},collaborationPayload});
  try{
    f.click('main h1');
    f.click('[data-open="collaboration"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.match(f.w.document.querySelector('#legend-cms-collaboration-role').textContent,/Owner · can publish/);
    assert.match(f.w.document.querySelector('#legend-cms-collaboration-roster').textContent,/Business Owner/);
    assert.match(f.w.document.querySelector('#legend-cms-collaboration-roster').textContent,/Website Editor/);
    f.input('#legend-cms-collaboration-body','Review this hero before publishing.');
    f.click('#legend-cms-collaboration-add');
    await new Promise(resolve=>setTimeout(resolve,0));
    const call=f.calls.find(call=>call.method==='POST' && new URL(call.url).pathname.endsWith('/manage/collaboration/comments'));
    assert.ok(call);
    const body=JSON.parse(call.body);
    assert.equal(body.ticket,'ticket');
    assert.equal(body.expectedRevision,'r1');
    assert.equal(body.pagePath,'/');
    assert.equal(body.elementId,'home.h1.node.1');
    assert.equal(body.body,'Review this hero before publishing.');
    assert.equal(body.parentCommentId,null);
    assert.equal(JSON.stringify(body).includes('document'),false);
  }finally{f.close();}
});

test('Collaboration source remains private management metadata and never joins published document serialization',()=>{
  assert.ok(source.includes('/manage/collaboration/comments'));
  assert.ok(source.includes('/manage/collaboration/comments/status'));
  assert.equal(source.includes('documentState.comments'),false);
  assert.equal(source.includes('pageState().comments'),false);
});

test('GPT browser workspace exposes the canonical editor without any app-side OpenAI request path', async()=>{
  const f=await domFixture();
  try{
    f.click('[data-open="gpt"]');
    const workspace=f.w.document.querySelector('#legend-cms-browser-agent-workspace');
    assert.ok(workspace);
    assert.equal(workspace.dataset.agentWorkspace,'browser-only');
    assert.equal(workspace.dataset.externalAiApi,'false');
    assert.match(workspace.textContent,/authorized browser session/i);
    assert.match(workspace.textContent,/No website content is sent to OpenAI by this application/i);
    assert.ok(f.w.document.querySelector('[data-agent-action="master-source"]'));
    assert.ok(f.w.document.querySelector('[data-agent-action="media-library"]'));
    assert.ok(f.w.document.querySelector('[data-agent-action="quality-preflight"]'));
    assert.ok(f.w.document.querySelector('[data-agent-action="publish-workspace"]'));
    f.click('#legend-cms-agent-master-source');
    assert.equal(f.w.document.querySelector('[data-cms-view="source"]').hidden,false);
    assert.equal(f.w.document.querySelector('#legend-cms-source-scope').value,'site');
    assert.equal(f.calls.some(call=>new URL(call.url).pathname.includes('/manage/ai/')),false);
  } finally { f.close(); }
});

test('shared Website Studio contains no app-side OpenAI proposal endpoint',()=>{
  assert.equal(source.includes('/manage/ai/propose'),false);
  assert.equal(source.includes('ai_proposal_preview'),false);
  assert.equal(source.includes('pendingAiProposal'),false);
  assert.ok(source.includes('externalAiApi'));
  assert.ok(source.includes('browser-only'));
});

test('writable Website Studio exposes only the v3 composition authority',()=>{
  for(const retired of [
    'pageState().elements',
    'pageState().extras',
    'pageState().sectionOrder',
    'documentState.elements',
    'documentState.extras',
    'documentState.sectionOrder'
  ]) assert.equal(source.includes(retired),false,retired+' must never return as a writer');

  assert.equal(source.includes('legacyMigration.elements ='),false);
  assert.equal(source.includes('legacyMigration.extras ='),false);
  assert.equal(source.includes('legacyMigration.sectionOrder ='),false);
  assert.equal(source.includes('function createExtra('),false);
  assert.equal(source.includes('function createLegacyExtra('),false);
  assert.equal(source.includes('ov.imageDataUrl'),false);
  assert.equal(source.includes('ov.videoUrl'),false);
  assert.equal(source.includes('legend-cms-videoUrl'),false);
  assert.equal(source.includes('function readImage('),false);
  assert.equal(source.includes('function restoreHistory('),false);
  assert.ok(source.includes('function restoreCanonicalV3History('));
  assert.ok(source.includes('function cloneCanonicalValue('));
  assert.equal((source.match(/structuredClone\(/g) || []).length,1);
  assert.equal(source.includes('function applyPlacement('),false);
  assert.equal(source.includes('function applyLegacyPlacement('),false);
  assert.ok(source.includes('function legacyRecordAsCanonicalModel('));
  const canonicalApply=source.slice(
    source.indexOf('function applyCompositionNode('),
    source.indexOf('function buildLegacyExtraNode(')
  );
  assert.equal(canonicalApply.includes('imageDataUrl'),false);
  assert.equal(canonicalApply.includes('videoUrl'),false);
  assert.equal(source.includes('node.mediaUrl=asset.url'),false);
  assert.equal(source.includes('mediaUrl:asset.url'),false);
  assert.ok(source.includes('node.mediaAssetId=asset.id'));
  assert.match(source,/async function save\([\s\S]*?if \(legacyMigration\)[\s\S]*?return false;/);
  assert.match(source,/function buildEditor\(\) \{[\s\S]*?if \(legacyMigration\)[\s\S]*?throw new Error/);
  assert.match(source,/READ-ONLY PRE-V3 COMPATIBILITY BOUNDARY/);
});

test('public declarative click motion plays once and is not duplicated by responsive refresh',async()=>{
  const id='11111111111111111111111111111111';
  const doc=canonicalDocument();
  canonicalNodeById(doc,'home.h1.node.1').animations=[{id,trigger:'click',effect:'slide-up',durationMs:500,delayMs:25,distancePx:30,easing:'ease-out',once:true}];
  const f=await domFixture({doc,search:''});
  try{
    const heading=f.w.document.querySelector('main h1');
    heading.dispatchEvent(new f.w.MouseEvent('click',{bubbles:true,cancelable:true}));
    assert.equal(f.animations.length,1);
    assert.equal(f.animations[0].options.duration,500);
    assert.equal(f.animations[0].options.delay,25);
    assert.equal(f.animations[0].options.easing,'ease-out');
    assert.match(String(f.animations[0].keyframes[0].transform),/translateY\(30px\)/);
    f.w.dispatchEvent(new f.w.Event('resize'));
    heading.dispatchEvent(new f.w.MouseEvent('click',{bubbles:true,cancelable:true}));
    assert.equal(f.animations.length,1);
  } finally { f.close(); }
});

test('Studio motion panel writes typed motion and preview does not create analytics requests',async()=>{
  const f=await domFixture();
  try{
    f.click('main h1');
    f.click('[data-open="motion"]');
    f.click('#legend-cms-motion-controls > button');
    const row=f.w.document.querySelector('.legend-cms-motion-row');
    assert.ok(row);
    const selects=row.querySelectorAll('select');
    selects[0].value='hover'; selects[0].dispatchEvent(new f.w.Event('change',{bubbles:true}));
    selects[1].value='scale'; selects[1].dispatchEvent(new f.w.Event('change',{bubbles:true}));
    const numberInputs=row.querySelectorAll('input[type="number"]');
    numberInputs[0].value='650'; numberInputs[0].dispatchEvent(new f.w.Event('change',{bubbles:true}));
    f.click('.legend-cms-motion-row .legend-cms-row button');
    assert.equal(f.animations.length,1);
    assert.equal(f.calls.some(call=>new URL(call.url).pathname.includes('/tracking/')),false);
    const saved=await f.save();
    const model=canonicalNodeById(saved,'home.h1.node.1');
    assert.ok(model);
    const motion=model.animations[0];
    assert.equal(motion.trigger,'hover');
    assert.equal(motion.effect,'scale');
    assert.equal(motion.durationMs,650);
    assert.match(motion.id,/^[a-f0-9]{32}$/);
  } finally { f.close(); }
});

test('motion runtime explicitly respects reduced-motion preference and never injects arbitrary scripts',()=>{
  assert.ok(source.includes("prefers-reduced-motion: reduce"));
  assert.ok(source.includes("typeof el.animate !== 'function' || prefersReducedMotion()"));
  assert.equal(source.includes('eval(binding'),false);
  assert.equal(source.includes('new Function(binding'),false);
});

test('canonical public stylesheet preserves authored spaces, tabs and line breaks',()=>{
  assert.match(publicCss,/\[data-cms-preserve-whitespace="true"\]\{white-space:pre-wrap;tab-size:4;overflow-wrap:anywhere\}/);
});

test('shared business inquiry uses Protect contact identity and two-column rows',()=>{
  assert.ok(publicInquiryFormSource.includes('id="website_inquiry"'));
  for (const field of ['FirstName','LastName','Phone','Email']) assert.ok(publicInquiryFormSource.includes(`name="${field}"`));
  assert.ok(publicInquirySource.includes("fields.get('FirstName')"));
  assert.ok(publicInquirySource.includes("fields.get('LastName')"));
  assert.ok(publicInquirySource.includes("fields.get('Phone')"));
  assert.ok(publicInquirySource.includes("fields.get('Email')"));
  assert.ok(source.includes("requiredContactFields: inquiryForm ? ['FirstName','LastName','Phone','Email'] : []"));
  assert.match(publicInquiryFormCss,/\.public-form-grid\s*\{[\s\S]*?display:grid;[\s\S]*?grid-template-columns:repeat\(2,minmax\(0,1fr\)\)/);
  assert.match(publicInquiryFormCss, /@media\(max-width:650px\)[\s\S]*?\.public-form-grid\s*\{[\s\S]*?grid-template-columns:1fr;[\s\S]*?gap:14px/);
  assert.doesNotMatch(publicCss, /@container legend-public-preview/);
  assert.equal(publicCss.includes('.public-form-grid'),false);
});

test('Founder and business websites use one shared inquiry runtime with no hard-coded founder email form path',()=>{
  assert.ok(businessBuildSource.includes("import { publicInquiryForm } from '../../SHARED/WebsitePlatform/public-inquiry-form.mjs'"));
  assert.ok(publicInquiryFormSource.includes('data-website-inquiry data-form-key="website_inquiry"'));
  assert.ok(businessBuildSource.includes('${publicInquiryForm()}</section>'));
  assert.ok(businessBuildSource.includes('publicInquiryForm({preview:true,business:true})'));
  assert.ok(businessBuildSource.includes('/legend-public-inquiry.js?v='));
  assert.ok(publicInquiryFormSource.includes('name="FirstName"'));
  assert.ok(publicInquiryFormSource.includes('name="LastName"'));
  assert.ok(publicInquiryFormSource.includes('name="Phone"'));
  assert.ok(publicInquiryFormSource.includes('name="Email"'));
  assert.ok(publicInquiryFormSource.includes('name="Message"'));
  assert.ok(protectContactSource.includes('data-legend-public-inquiry-form'));
  assert.ok(parfaitContactSource.includes('data-legend-public-inquiry-form'));
  assert.ok(publicInquiryFormSource.includes("window.addEventListener('legend:website-content-rendered', start)"));
  assert.ok(publicInquiryFormSource.includes("document.querySelector('[data-website-inquiry]:not([data-preview])')"));
  assert.ok(protectLayoutSource.includes('~/js/public-inquiry-form.mjs'));
  assert.ok(protectLayoutSource.includes('~/css/public-inquiry-form.css'));
  assert.ok(parfaitLayoutSource.includes('~/js/public-inquiry-form.mjs'));
  assert.ok(parfaitLayoutSource.includes('~/css/public-inquiry-form.css'));
  assert.equal(protectContactSource.includes('public-inquiry-form.mjs'),false);
  assert.equal(parfaitContactSource.includes('public-inquiry-form.mjs'),false);
  assert.equal(protectContactSource.includes('mailto:'),false);
  assert.equal(parfaitContactSource.includes('mailto:'),false);
  assert.equal(businessBuildSource.includes('mailto:connect@mylegnd.com'),false);
  assert.equal(editorContractsSource.includes('legend_email'),false);
  assert.ok(publicInquirySource.includes("new URLSearchParams(location.search).has('legendEdit')"));
  assert.ok(publicInquirySource.includes("document.querySelectorAll('[data-website-inquiry]:not([data-preview])')"));
  assert.ok(publicInquirySource.includes("new URL('/api/website-inquiries/public', resolveApiBase())"));
  assert.ok(publicInquirySource.includes("form._trackSubmitAttempt?.(true, 0)"));
  assert.ok(publicInquirySource.includes("window.legendFormTracking?.markSubmitted?.("));
  assert.equal(publicInquirySource.includes("EventType: 'lead_form_submit_success'"),false);
});

test('published business rendering activates the shared inquiry path without injecting a second runtime',()=>{
  assert.ok(businessRenderSource.includes("doc.querySelector('[data-website-inquiry]')"));
  assert.ok(businessRenderSource.includes("form.removeAttribute('data-preview')"));
  assert.ok(businessRenderSource.includes("form.querySelectorAll('[disabled]').forEach(element=>element.removeAttribute('disabled'))"));
  assert.ok(businessRenderSource.includes("form.querySelector('[data-preview-notice]')?.remove()"));
  assert.equal(businessRenderSource.includes("script.src='/business-inquiry.js'"),false);
});

test('custom code blocks use opaque data frames instead of weakening the page script policy',()=>{
  assert.ok(source.includes("frame.src = 'data:text/html;charset=utf-8,' + encodeURIComponent(source)"));
  assert.equal(source.includes('allow-same-origin'),false);
  assert.ok(businessMiddlewareSource.includes("frame-src 'self' data:; object-src 'none'"));
  const policy=businessMiddlewareSource.match(/default-src 'self'; script-src[^"]+/)?.[0] || '';
  assert.equal(policy.includes("script-src 'self' 'unsafe-inline'"),false);
  assert.equal(policy.includes("script-src 'self' 'unsafe-eval'"),false);
});

test('shared mobile navigation opens as compact horizontal button grids',()=>{
  assert.ok(publicCss.includes('.nav[data-open=true]{display:grid}'));
  assert.ok(publicCss.includes('grid-template-columns:repeat(4,minmax(0,1fr))'));
  assert.ok(publicCss.includes('.nav{grid-template-columns:repeat(3,minmax(0,1fr));gap:8px}'));
  assert.equal(publicCss.includes('.nav[data-open=true]{display:flex}'),false);
});
for (const siteKey of ['legend','protect','business']) {
  test(`${siteKey}: shared editor round-trips authored whitespace exactly`,async()=>{
    const business=siteKey==='business'?{id:'business-id',displayName:'Fixture business'}:null;
    const f=await domFixture({siteKey,business}); let saved;
    const value='Line one\n\tLine two  with  spaces';
    try {
      f.click('main h1'); f.editSelected(value);
      const heading=f.w.document.querySelector('main h1');
      assert.equal(heading.textContent,value);
      assert.equal(heading.dataset.cmsPreserveWhitespace,'true');
      saved=await f.save();
      assert.equal(canonicalNodeById(saved,'home.h1.node.1').text,value);
    } finally { f.close(); }
    const published=await domFixture({siteKey,business,doc:saved,search:''});
    try {
      assert.equal(published.w.document.querySelector('main h1').textContent,value);
      assert.equal(published.w.document.querySelector('main h1').dataset.cmsPreserveWhitespace,'true');
    } finally { published.close(); }
  });
}
test('shared CTA chooser starts neutral, applies one canonical action, and keeps chosen wording independently editable',async()=>{
  const actions=[
    {key:'business_contact',group:'Contact',label:'Contact',defaultText:'Contact',textVariants:['Contact','Contact Us','Get in Touch'],href:'/contact',openInNewTab:false,analyticsEventName:'cta_click',metaIntentEventName:'ContactStepReached'},
    {key:'business_call',group:'Call',label:'Call',defaultText:'Call',textVariants:['Call','Call Now','Call Us'],href:'tel:+16025550199',openInNewTab:false,analyticsEventName:'cta_click',metaIntentEventName:'ContactStepReached'}
  ];
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'},ctaCatalog:actions,signalCatalog:{events:[],matchingFields:[],runtimeEnabled:true}});
  try {
    f.click('main h1'); f.click('[data-add="button"]');
    const button=f.w.document.querySelector('.legend-cms-selected');
    const buttonId=button?.dataset?.cmsCompositionId;
    assert.ok(buttonId);
    assert.ok(button.classList.contains('primary'));
    assert.equal(button.getAttribute('href'),null);
    assert.equal(button.textContent,'Button');
    assert.equal(f.w.document.querySelector('#legend-cms-action').value,'');

    f.change('#legend-cms-action','managed:business_call:1');
    assert.equal(button.getAttribute('href'),'tel:+16025550199');
    assert.equal(button.textContent,'Call Now');
    f.editSelected('Talk with our team');
    assert.equal(button.textContent,'Talk with our team');

    f.click('[data-open="signals"]');
    assert.match(f.w.document.querySelector('#legend-cms-signal-controls').textContent,/Automatic action analytics/);
    const advanced=[...f.w.document.querySelectorAll('#legend-cms-signal-controls button')]
      .find(node=>node.textContent==='Add advanced custom mapping');
    assert.ok(advanced); assert.equal(advanced.hidden,true);

    const saved=await f.save(); const buttonNode=canonicalNodeById(saved,buttonId);
    assert.equal(buttonNode.actionKey,'business_call');
    assert.equal(buttonNode.href,'tel:+16025550199');
    assert.equal(buttonNode.text,'Talk with our team');
    assert.equal(Object.hasOwn(saved.pages['/'],'extras'),false);
  } finally { f.close(); }
});
test('button action picker groups human CTA phrases by one backend wiring contract and keeps navigation separate',async()=>{
  const actions=[
    {key:'business_contact',group:'Contact',label:'Contact',defaultText:'Contact',textVariants:['Contact','Contact Us','Get in Touch','Reach Out'],href:'/contact',openInNewTab:false,analyticsEventName:'cta_click',metaIntentEventName:'ContactStepReached'},
    {key:'business_quote',group:'Quote',label:'Quote',defaultText:'Quote',textVariants:['Quote','Free Quote','Get a Quote','Get a Free Quote'],href:'/contact',openInNewTab:false,analyticsEventName:'cta_click',metaIntentEventName:'ContactStepReached'},
    {key:'business_schedule',group:'Schedule',label:'Schedule',defaultText:'Schedule',textVariants:['Schedule','Book Now','Schedule a Meeting'],href:'https://book.example.test/',openInNewTab:true,analyticsEventName:'cta_click',metaIntentEventName:'ContactStepReached'}
  ];
  const pages=[{path:'/',label:'Home'},{path:'/contact',label:'Contact'}];
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'},pages,ctaCatalog:actions});
  try{
    f.click('main h1'); f.click('[data-add="button"]');
    const select=f.w.document.querySelector('#legend-cms-action');
    const groups=[...select.querySelectorAll('optgroup')];
    assert.deepEqual(groups.map(group=>group.label),[
      'CONTACT — Automatic analytics',
      'QUOTE — Automatic analytics',
      'SCHEDULE — Automatic analytics',
      'WEBSITE PAGES',
      'OTHER'
    ]);

    const contactTexts=[...groups[0].querySelectorAll('option')].map(option=>option.textContent);
    assert.deepEqual(contactTexts,['Contact','Contact Us','Get in Touch','Reach Out']);
    const quoteTexts=[...groups[1].querySelectorAll('option')].map(option=>option.textContent);
    assert.deepEqual(quoteTexts,['Quote','Free Quote','Get a Quote','Get a Free Quote']);
    const allOptionText=[...select.querySelectorAll('option')].map(option=>option.textContent).join(' ');
    assert.doesNotMatch(allOptionText,/AUTO|Contact LEGEND|Page ·|This website ·/);

    f.change('#legend-cms-action','managed:business_quote:3');
    const button=f.w.document.querySelector('.legend-cms-selected');
    assert.equal(button.textContent,'Get a Free Quote');
    assert.equal(button.getAttribute('href'),'/contact');
    assert.equal(f.w.document.querySelector('#legend-cms-action').disabled,true);
    assert.match(f.w.document.querySelector('#legend-cms-action-wiring').textContent,/Protected wiring/);

    f.change('#legend-cms-action','custom');
    assert.equal(button.dataset.websiteActionKey,'business_quote');
    assert.equal(button.getAttribute('href'),'/contact');
  }finally{f.close();}
});

test('selecting another canvas child preserves the active side-panel tool instead of forcing Content',async()=>{
  const html='<!doctype html><html><body data-page-key="home"><main><section><a href="/contact"><span>Contact</span></a><h2>Heading</h2></section></main></body></html>';
  const f=await domFixture({html});
  try{
    f.click('[data-open="appearance"]');
    const appearance=f.w.document.querySelector('[data-cms-view="appearance"]');
    const content=f.w.document.querySelector('[data-cms-view="content"]');
    assert.equal(appearance.hidden,false);
    f.click('main h2');
    assert.equal(appearance.hidden,false);
    assert.equal(content.hidden,true);
    f.click('main a span');
    assert.equal(appearance.hidden,false);
    assert.equal(content.hidden,true);
    assert.equal(f.w.document.querySelector('.legend-cms-selected').tagName,'A');

    const same=f.w.document.querySelector('main a span');
    same.dispatchEvent(new f.w.MouseEvent('click',{bubbles:true,cancelable:true,detail:1}));
    assert.equal(content.hidden,false);
  }finally{f.close();}
});

test('double-click text-edit mode disables move and resize gestures until editing ends',async()=>{
  const f=await domFixture();
  try{
    f.click('main h1');
    const heading=f.w.document.querySelector('main h1');
    const section=heading.closest('[data-cms-section]');
    const preview=f.w.document.querySelector('.legend-cms-preview');
    heading.getBoundingClientRect=()=>({left:100,top:100,right:300,bottom:140,width:200,height:40});
    section.getBoundingClientRect=()=>({left:50,top:50,right:650,bottom:450,width:600,height:400});
    preview.getBoundingClientRect=()=>({left:0,top:0,right:1000,bottom:800,width:1000,height:800});

    heading.dispatchEvent(new f.w.MouseEvent('dblclick',{bubbles:true,cancelable:true}));
    const frame=f.w.document.querySelector('.legend-cms-selection-frame');
    assert.equal(frame.dataset.textEditing,'true');

    const move=frame.querySelector('.legend-cms-move-handle');
    move.dispatchEvent(new f.w.MouseEvent('pointerdown',{bubbles:true,cancelable:true,clientX:100,clientY:100,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointermove',{bubbles:true,cancelable:true,clientX:180,clientY:150,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointerup',{bubbles:true,cancelable:true,clientX:180,clientY:150,button:0}));
    let saved=await f.save();
    let style=canonicalNodeById(saved,'home.h1.node.1')?.style || {};
    assert.equal(style.offsetXPercent,undefined);
    assert.equal(style.offsetYPx,undefined);

    f.w.document.dispatchEvent(new f.w.KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}));
    assert.equal(frame.dataset.textEditing,'false');
    move.dispatchEvent(new f.w.MouseEvent('pointerdown',{bubbles:true,cancelable:true,clientX:100,clientY:100,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointermove',{bubbles:true,cancelable:true,clientX:130,clientY:112,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointerup',{bubbles:true,cancelable:true,clientX:130,clientY:112,button:0}));
    saved=await f.save();
    style=canonicalNodeById(saved,'home.h1.node.1').style;
    assert.equal(style.offsetXPercent,5);
    assert.equal(style.offsetYPx,12);
  }finally{f.close();}
});

test('Duplicate selected is visible with the selected content controls and preserves managed CTA wiring',async()=>{
  const actions=[{key:'legend_contact',group:'Contact',label:'Contact',defaultText:'Contact',textVariants:['Contact','Contact Us'],href:'/contact',openInNewTab:false,analyticsEventName:'cta_click',metaIntentEventName:'ContactStepReached'}];
  const f=await domFixture({ctaCatalog:actions});
  try{
    f.click('main h1'); f.click('[data-add="button"]');
    f.change('#legend-cms-action','managed:legend_contact:1');
    const originalId=f.w.document.querySelector('.legend-cms-selected').dataset.cmsCompositionId;
    const duplicate=f.w.document.querySelector('#legend-cms-duplicate');
    assert.equal(duplicate.closest('[data-cms-view="content"]')!==null,true);
    assert.equal(duplicate.disabled,false);
    f.click('#legend-cms-duplicate');
    const saved=await f.save();
    const copies=canonicalNodes(saved).filter(node=>node.type==='cta' && node.actionKey==='legend_contact');
    assert.equal(copies.length,2);
    assert.ok(copies.some(node=>node.id===originalId));
    assert.notEqual(copies[0].id,copies[1].id);
    assert.ok(copies.every(node=>node.href==='/contact'));
    assert.ok(copies.every(node=>Array.isArray(node.signals)));
  }finally{f.close();}
});

test('manual destination remains available and intentionally leaves managed CTA routing',async()=>{
  const actions=[{key:'legend_contact',group:'Contact',label:'Contact',defaultText:'Contact',textVariants:['Contact','Contact Us'],href:'/contact',openInNewTab:false}];
  const f=await domFixture({ctaCatalog:actions});
  try {
    f.click('main h1'); f.click('[data-add="button"]');
    const nodeId=f.w.document.querySelector('.legend-cms-selected').dataset.cmsCompositionId;
    f.input('#legend-cms-href','https://example.com/custom');
    const saved=await f.save(); const link=canonicalNodeById(saved,nodeId);
    assert.equal(link.actionKey,null);
    assert.equal(link.href,'https://example.com/custom');
  } finally { f.close(); }
});

test('new section inserts directly after the selected canonical section and survives reload ordering',async()=>{
  const doc=canonicalDocument();
  doc.pages['/'].composition=[
    canonicalNode('section.first','section','section',{children:[canonicalNode('section.first.title','heading','h2',{text:'First'})]}),
    canonicalNode('section.middle','section','section',{children:[canonicalNode('section.middle.title','heading','h2',{text:'Middle'})]}),
    canonicalNode('section.last','section','section',{children:[canonicalNode('section.last.title','heading','h2',{text:'Last'})]})
  ];
  const f=await domFixture({doc}); let saved;
  try{
    const middle=f.w.document.querySelectorAll('main > section')[1];
    f.click('main > section:nth-of-type(2) h2');
    f.click('[data-add="section"]');
    const sections=[...f.w.document.querySelectorAll('main > section')];
    assert.equal(sections.length,4);
    const added=sections[2];
    assert.ok(added);
    assert.equal(added.classList.contains('section'),true);
    assert.equal(sections[1].dataset.cmsCompositionId,middle.dataset.cmsCompositionId);
    saved=await f.save();
    const order=saved.pages['/'].composition.map(node=>node.id);
    assert.equal(order.indexOf('section.middle'),1);
    assert.equal(order.indexOf(added.dataset.cmsCompositionId),2);
    assert.equal(Object.hasOwn(saved.pages['/'],'sectionOrder'),false);
  }finally{f.close();}
  const loaded=await domFixture({doc:saved,search:''});
  try{
    const sections=[...loaded.w.document.querySelectorAll('main > section')];
    assert.equal(sections.length,4);
    assert.match(sections[1].textContent,/Middle/);
    assert.match(sections[3].textContent,/Last/);
  }finally{loaded.close();}
});

test('Layers lists and reorders only whole sections, never individual fields or actions',async()=>{
  const html='<!doctype html><html><body data-page-key="home"><main><section><h2>First</h2><form><input name="Email"><button>Send</button></form></section><section><h2>Second</h2><a href="/contact">Contact</a></section></main></body></html>';
  const f=await domFixture({html});
  try{
    f.click('[data-open="layers"]');
    let rows=[...f.w.document.querySelectorAll('.legend-cms-section-layer')];
    assert.equal(rows.length,2);
    assert.doesNotMatch(f.w.document.querySelector('#legend-cms-layers').textContent,/Email|Send|Contact/);
    assert.ok(rows.every(row=>row.draggable===true));
    const transfer={value:'',setData(_type,value){this.value=value;},getData(){return this.value;}};
    const start=new f.w.Event('dragstart',{bubbles:true,cancelable:true});
    Object.defineProperty(start,'dataTransfer',{value:transfer});
    rows[1].dispatchEvent(start);
    const over=new f.w.Event('dragover',{bubbles:true,cancelable:true});
    rows[0].dispatchEvent(over);
    const drop=new f.w.Event('drop',{bubbles:true,cancelable:true});
    Object.defineProperty(drop,'dataTransfer',{value:transfer});
    rows[0].dispatchEvent(drop);
    const sections=[...f.w.document.querySelectorAll('main > section')];
    assert.match(sections[0].textContent,/Second/);
    const saved=await f.save();
    assert.deepEqual(saved.pages['/'].composition.slice(0,2).map(node=>node.id),
      sections.slice(0,2).map(section=>section.dataset.cmsCompositionId));
    assert.equal(Object.hasOwn(saved.pages['/'],'sectionOrder'),false);
  }finally{f.close();}
});

test('new button is appended to the selected canonical container with no placement side-store',async()=>{
  const doc=canonicalDocument();
  const section=canonicalNodeById(doc,'home.section.1');
  section.children=[
    canonicalNode('home.chosen','container','div',{className:'chosen',children:[
      canonicalNode('home.chosen.title','heading','h1',{text:'Headline'}),
      canonicalNode('home.chosen.copy','text','p',{text:'Copy'})
    ]}),
    canonicalNode('home.other','container','div',{className:'other',children:[
      canonicalNode('home.other.copy','text','p',{text:'Other'})
    ]})
  ];
  const f=await domFixture({doc});
  try {
    f.click('.chosen h1'); f.click('[data-add="button"]');
    const container=f.w.document.querySelector('.chosen');
    const button=container.querySelector('.legend-cms-selected');
    assert.ok(button); assert.equal(container.lastElementChild,button);
    const saved=await f.save();
    const chosen=canonicalNodeById(saved,'home.chosen');
    const inserted=chosen.children.at(-1);
    assert.equal(inserted.type,'cta');
    assert.equal(inserted.id,button.dataset.cmsCompositionId);
    assert.equal(Object.hasOwn(inserted,'placement'),false);
  } finally { f.close(); }
});
test('duplicated service subtree receives fresh IDs and child geometry stays inside that subtree',async()=>{
  const doc=canonicalDocument();
  const section=canonicalNodeById(doc,'home.section.1');
  section.children=[
    canonicalNode('services.grid','container','div',{className:'card-grid',children:[
      canonicalNode('service.one','container','article',{className:'card',children:[
        canonicalNode('service.one.title','heading','h3',{text:'Service one'}),
        canonicalNode('service.one.copy','text','p',{text:'First description'})
      ]})
    ]})
  ];
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'},doc});
  try{
    f.click('.card-grid > article.card h3');
    f.click('#legend-cms-duplicate');
    const cards=[...f.w.document.querySelectorAll('.card-grid > article.card')];
    assert.equal(cards.length,2);
    const copyCard=cards[1];
    const copyId=copyCard.dataset.cmsCompositionId;
    assert.notEqual(copyId,'service.one');
    const heading=copyCard.querySelector('h3');
    f.click('.card-grid > article.card:nth-of-type(2) h3');
    const sectionEl=heading.closest('[data-cms-section]');
    const preview=f.w.document.querySelector('.legend-cms-preview');
    heading.getBoundingClientRect=()=>({left:100,top:100,right:300,bottom:140,width:200,height:40});
    sectionEl.getBoundingClientRect=()=>({left:50,top:50,right:650,bottom:450,width:600,height:400});
    preview.getBoundingClientRect=()=>({left:0,top:0,right:1000,bottom:800,width:1000,height:800});
    const move=f.w.document.querySelector('.legend-cms-move-handle');
    move.dispatchEvent(new f.w.MouseEvent('pointerdown',{bubbles:true,cancelable:true,clientX:100,clientY:100,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointermove',{bubbles:true,cancelable:true,clientX:140,clientY:120,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointerup',{bubbles:true,cancelable:true,clientX:140,clientY:120,button:0}));
    f.editSelected('Duplicated service title');
    const saved=await f.save();
    const copy=canonicalNodeById(saved,copyId);
    assert.ok(copy);
    assert.equal(copy.type,'container');
    assert.notEqual(copy.children[0].id,'service.one.title');
    assert.equal(copy.children[0].text,'Duplicated service title');
    assert.equal(copy.children[0].style.offsetXPercent,6.667);
    assert.equal(copy.children[0].style.offsetYPx,20);
  }finally{f.close();}
});

test('business services duplicate and delete as complete canonical card subtrees',async()=>{
  const doc=canonicalDocument();
  const section=canonicalNodeById(doc,'home.section.1');
  section.children=[
    canonicalNode('services.grid','container','div',{className:'card-grid',children:[
      canonicalNode('service.one','container','article',{className:'card',children:[
        canonicalNode('service.one.title','heading','h3',{text:'Service one'}),
        canonicalNode('service.one.copy','text','p',{text:'First description'})
      ]}),
      canonicalNode('service.two','container','article',{className:'card',children:[
        canonicalNode('service.two.title','heading','h3',{text:'Service two'}),
        canonicalNode('service.two.copy','text','p',{text:'Second description'})
      ]})
    ]})
  ];
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'},doc});
  try {
    f.click('.card-grid > article.card h3');
    assert.equal(f.w.document.querySelector('#legend-cms-duplicate').textContent,'Duplicate service');
    assert.equal(f.w.document.querySelector('#legend-cms-remove').textContent,'Delete service');
    f.click('#legend-cms-duplicate');
    let cards=[...f.w.document.querySelectorAll('.card-grid > article.card')];
    assert.equal(cards.length,3);
    assert.equal(cards[1].querySelector('h3').textContent,'Service one');
    assert.equal(cards[1].querySelector('.icon'),null);

    let saved=await f.save();
    let grid=canonicalNodeById(saved,'services.grid');
    assert.equal(grid.children.length,3);
    assert.equal(grid.children[1].children[0].text,'Service one');
    assert.equal(grid.children[1].hidden,undefined);

    f.click('.card-grid > article.card:first-child h3');
    f.click('#legend-cms-remove');
    cards=[...f.w.document.querySelectorAll('.card-grid > article.card')];
    assert.equal(cards.length,2);
    saved=await f.save();
    grid=canonicalNodeById(saved,'services.grid');
    assert.equal(grid.children.some(node=>node.id==='service.one'),false);
    assert.equal(grid.children.some(node=>node.hidden===true),false);
  } finally { f.close(); }
});

for(const siteKey of ['legend','protect']) {
  test(`${siteKey}: nested existing links edit label and URL, reject unsafe destinations, draft uses revision`,async()=>{
    const f=await domFixture({siteKey}); try {
      f.click('main a span'); f.editSelected('Book a visit'); f.input('#legend-cms-href','https://business.example/book');
      assert.equal(f.w.document.querySelector('main a').textContent,'Book a visit');
      assert.equal(f.w.document.querySelector('main a').href,'https://business.example/book');
      f.input('#legend-cms-href','javascript:alert(1)');
      assert.equal(f.w.document.querySelector('main a').href,'https://business.example/book');
      const saved=await f.save(); assert.equal(canonicalNodeById(saved,'home.a.node.1').href,'https://business.example/book');
      assert.equal(JSON.parse(f.calls.at(-1).body).expectedRevision,'r1');
      assert.ok(f.calls.every(x=>!x.url.includes('/public/')));
    } finally {f.close();}
  });
}
test('autosave persists edits before domain connection and panel can collapse to a full-page preview', async()=>{
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'}});
  try {
    f.click('main h1');
    f.editSelected('Persisted before domain');
    await new Promise(resolve=>setTimeout(resolve,1000));
    const saveCall=f.calls.find(call=>call.method==='POST' && call.url.endsWith('/manage'));
    assert.ok(saveCall);
    assert.equal(canonicalNodeById(JSON.parse(saveCall.body).document,'home.h1.node.1').text,'Persisted before domain');
    const toggle=f.w.document.querySelector('#legend-cms-panel-toggle');
    assert.ok(toggle);
    f.click('#legend-cms-panel-toggle');
    assert.equal(f.w.document.body.classList.contains('legend-cms-panel-hidden'),true);
    assert.equal(toggle.textContent,'Open controls');
    assert.ok(f.w.document.querySelector('.legend-cms-selected'));
    f.editSelected('Still editing full width');
    assert.equal(f.w.document.querySelector('main h1').textContent,'Still editing full width');
    f.click('#legend-cms-panel-toggle');
    assert.equal(f.w.document.body.classList.contains('legend-cms-panel-hidden'),false);
    assert.equal(toggle.textContent,'Full-page canvas');
  } finally { f.close(); }
});
test('breakpoint editor writes responsive style and layout without replacing base geometry',async()=>{
  const f=await domFixture();
  try {
    f.click('main h1');
    f.click('[data-open="layout"]');
    f.change('#legend-cms-breakpoint','mobile');
    f.input('#legend-cms-width','55');
    f.input('#legend-cms-layout-mode','stack');
    f.input('#legend-cms-layout-gap','16');
    const saved=await f.save();
    const model=canonicalNodeById(saved,'home.h1.node.1');
    assert.ok(model);
    assert.equal(model.style?.widthPercent,undefined);
    assert.equal(model.breakpointStyles.mobile.widthPercent,55);
    assert.equal(model.breakpointLayouts.mobile.mode,'stack');
    assert.equal(model.breakpointLayouts.mobile.gapPx,16);
  } finally { f.close(); }
});

test('custom breakpoint can be added used and removed without leaving hidden responsive state',async()=>{
  const f=await domFixture();
  try {
    f.click('main h1');
    f.click('[data-open="layout"]');
    f.input('#legend-cms-breakpoint-label','Large tablet');
    f.input('#legend-cms-breakpoint-key','large-tablet');
    f.input('#legend-cms-breakpoint-min','900');
    f.input('#legend-cms-breakpoint-max','1100');
    f.click('#legend-cms-breakpoint-add');
    assert.equal(f.w.document.querySelector('#legend-cms-breakpoint').value,'large-tablet');
    f.input('#legend-cms-width','66');
    f.click('#legend-cms-breakpoint-remove');
    const saved=await f.save();
    assert.equal(saved.breakpoints.some(value=>value.key==='large-tablet'),false);
    assert.equal(canonicalNodes(saved).some(value=>value.breakpointStyles?.['large-tablet']),false);
  } finally { f.close(); }
});
test('section deletion removes the canonical node without leaving a hidden parallel record',async()=>{
  const f=await domFixture();try {
    f.click('main h1');f.click('#legend-cms-container');
    const sectionId=f.w.document.querySelector('.legend-cms-selected').dataset.cmsCompositionId;
    f.click('#legend-cms-remove');
    const doc=await f.save();
    assert.equal(canonicalNodeById(doc,sectionId),null);
    assert.equal(Object.hasOwn(doc.pages['/'],'elements'),false);
    assert.equal(Object.hasOwn(doc.pages['/'],'extras'),false);
  }finally{f.close();}
});
test('inquiry form builder is autonomous and exposes no required manual mapping',async()=>{
  const catalog={events:[
    {name:'LeadFormStart',category:'lead',metaEligible:true,requiresServerOutcome:false,triggers:['form_started']},
    {name:'ContactInputStarted',category:'lead',metaEligible:true,requiresServerOutcome:false,triggers:['field_started']},
    {name:'PhoneFieldCompleted',category:'lead',metaEligible:true,requiresServerOutcome:false,triggers:['field_completed']},
    {name:'RequiredContactFieldsCompleted',category:'lead',metaEligible:true,requiresServerOutcome:false,triggers:['field_completed']},
    {name:'SubmitAttempt',category:'submit',metaEligible:true,requiresServerOutcome:false,triggers:['submit_attempt']},
    {name:'Lead',category:'conversion',metaEligible:true,requiresServerOutcome:true,triggers:['submission_saved']}
  ],matchingFields:['email','phone','firstName','lastName'],runtimeEnabled:true};
  const f=await domFixture({signalCatalog:catalog});
  try{
    f.click('main h1');
    f.click('[data-add="form"]');
    const form=f.w.document.querySelector('form.legend-cms-inquiry-form[data-website-inquiry]');
    assert.ok(form);
    assert.ok(form.id.startsWith('website_inquiry_'));
    assert.equal(form.dataset.formKey,'website_inquiry');
    assert.equal(form.dataset.preview,'');
    assert.equal(form.querySelector('fieldset').disabled,true);
    for(const name of ['FirstName','LastName','Phone','Email','Message'])
      assert.ok(form.querySelector(`[name="${name}"]`));
    assert.ok(form.querySelector('[name="consent"]'));
    f.click('[data-open="signals"]');
    const status=f.w.document.querySelector('.legend-cms-signal-presets');
    assert.ok(status);
    assert.match(status.textContent,/LeadFormStart/);
    assert.match(status.textContent,/ContactInputStarted/);
    assert.match(status.textContent,/PhoneFieldCompleted/);
    assert.match(status.textContent,/SubmitAttempt/);
    assert.match(status.textContent,/Lead/);
    const advanced=[...f.w.document.querySelectorAll('#legend-cms-signal-controls button')]
      .find(button=>button.textContent==='Add advanced custom mapping');
    assert.ok(advanced);
    assert.equal(advanced.hidden,true);
    const saved=await f.save();
    const formNode=canonicalNodes(saved).find(value=>value.type==='form');
    assert.ok(formNode);
    assert.equal(formNode.systemKey,'canonical_inquiry');
    assert.deepEqual(formNode.signals,[]);
    assert.equal(Object.hasOwn(saved.pages['/'],'extras'),false);
  }finally{f.close();}
});

test('direct canvas replaces designated drop controls and persists shared geometry',async()=>{
  const f=await domFixture();let saved;try {
    f.click('main h1');
    assert.equal(f.w.document.querySelector('#legend-cms-destination'),null);
    assert.equal(f.w.document.querySelector('#legend-cms-column'),null);
    assert.equal(f.w.document.querySelector('#legend-cms-place'),null);
    assert.ok(f.w.document.querySelector('.legend-cms-selection-frame'));
    assert.ok(f.w.document.querySelector('.legend-cms-grid-overlay'));
    assert.equal(f.w.document.querySelectorAll('[data-cms-gesture]').length,9);
    f.input('#legend-cms-width','50');
    f.input('#legend-cms-height','240');
    f.input('#legend-cms-offset-x','25');
    f.input('#legend-cms-offset-y','48');
    saved=await f.save();
    const style=canonicalNodeById(saved,'home.h1.node.1').style;
    assert.equal(style.widthPercent,50);assert.equal(style.heightPx,240);assert.equal(style.offsetXPercent,25);assert.equal(style.offsetYPx,48);
  }finally{f.close();}
  const loaded=await domFixture({doc:saved,search:''});try {
    const heading=loaded.w.document.querySelector('main h1');
    assert.equal(heading.style.width,'50%');assert.equal(heading.style.height,'240px');assert.equal(heading.style.left,'25%');assert.equal(heading.style.top,'48px');
    assert.equal(loaded.w.document.querySelector('.legend-cms-panel'),null);
  }finally{loaded.close();}
});
test('direct canvas resize is continuous with no 12-column or 24px snap',()=>{
  assert.doesNotMatch(source,/const cell = sectionWidth \/ 12/);
  assert.doesNotMatch(source,/const verticalStep = 24/);
  assert.doesNotMatch(source,/snappedWidth/);
  assert.match(source,/Math\.round\(rawWidth \* 1000\) \/ 1000/);
  assert.match(source,/Math\.round\(\(gesture\.startHeightPx \+ heightDelta\) \* 1000\) \/ 1000/);
});

test('section resize changes the canvas height without creating a section offset',async()=>{
  const f=await domFixture();
  try{
    f.click('main section');
    const section=f.w.document.querySelector('main section');
    const preview=f.w.document.querySelector('.legend-cms-preview');
    section.getBoundingClientRect=()=>({left:50,top:50,right:650,bottom:450,width:600,height:400});
    preview.getBoundingClientRect=()=>({left:0,top:0,right:1000,bottom:800,width:1000,height:800});
    const frame=f.w.document.querySelector('.legend-cms-selection-frame');
    const bottom=frame.querySelector('.legend-cms-edge-bottom');
    bottom.dispatchEvent(new f.w.MouseEvent('pointerdown',{bubbles:true,cancelable:true,clientX:300,clientY:450,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointermove',{bubbles:true,cancelable:true,clientX:300,clientY:327,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointerup',{bubbles:true,cancelable:true,clientX:300,clientY:327,button:0}));
    const saved=await f.save();
    const style=canonicalNodeById(saved,'home.section.1').style;
    assert.equal(style.heightPx,277);
    assert.equal(style.offsetYPx,undefined);
    assert.equal(section.style.height,'277px');
    assert.equal(section.style.overflow,'visible');
  }finally{f.close();}
});

test('selected content drag preserves free-form placement instead of forcing grid cells',async()=>{
  const f=await domFixture();
  try{
    f.click('main h1');
    const heading=f.w.document.querySelector('main h1');
    const section=heading.closest('[data-cms-section]');
    const preview=f.w.document.querySelector('.legend-cms-preview');
    heading.getBoundingClientRect=()=>({left:100,top:100,right:300,bottom:140,width:200,height:40});
    section.getBoundingClientRect=()=>({left:50,top:50,right:650,bottom:450,width:600,height:400});
    preview.getBoundingClientRect=()=>({left:0,top:0,right:1000,bottom:800,width:1000,height:800});
    const move=f.w.document.querySelector('.legend-cms-move-handle');
    move.dispatchEvent(new f.w.MouseEvent('pointerdown',{bubbles:true,cancelable:true,clientX:100,clientY:100,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointermove',{bubbles:true,cancelable:true,clientX:137,clientY:113,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointerup',{bubbles:true,cancelable:true,clientX:137,clientY:113,button:0}));
    const saved=await f.save();
    const style=canonicalNodeById(saved,'home.h1.node.1').style;
    assert.equal(style.offsetXPercent,6.167);
    assert.equal(style.offsetYPx,13);
  }finally{f.close();}
});

test('selected content can be pointer-dragged and is clamped inside its section frame',async()=>{
  const f=await domFixture();
  try{
    f.click('main h1');
    const heading=f.w.document.querySelector('main h1');
    const section=heading.closest('[data-cms-section]');
    const preview=f.w.document.querySelector('.legend-cms-preview');
    assert.ok(section&&preview);
    heading.getBoundingClientRect=()=>({left:100,top:100,right:300,bottom:140,width:200,height:40});
    section.getBoundingClientRect=()=>({left:50,top:50,right:650,bottom:450,width:600,height:400});
    preview.getBoundingClientRect=()=>({left:0,top:0,right:1000,bottom:800,width:1000,height:800});
    const move=f.w.document.querySelector('.legend-cms-move-handle');
    move.dispatchEvent(new f.w.MouseEvent('pointerdown',{bubbles:true,cancelable:true,clientX:100,clientY:100,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointermove',{bubbles:true,cancelable:true,clientX:1000,clientY:700,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointerup',{bubbles:true,cancelable:true,clientX:1000,clientY:700,button:0}));
    const saved=await f.save();
    const style=canonicalNodeById(saved,'home.h1.node.1').style;
    assert.equal(style.offsetXPercent,58.333);
    assert.equal(style.offsetYPx,310);
  }finally{f.close();}
});

test('selection movement resizing and text editing use separate non-competing gestures',async()=>{
  const f=await domFixture();
  try{
    f.click('main h1');
    const heading=f.w.document.querySelector('main h1');
    const section=heading.closest('[data-cms-section]');
    const preview=f.w.document.querySelector('.legend-cms-preview');
    heading.getBoundingClientRect=()=>({left:100,top:100,right:300,bottom:140,width:200,height:40});
    section.getBoundingClientRect=()=>({left:50,top:50,right:650,bottom:450,width:600,height:400});
    preview.getBoundingClientRect=()=>({left:0,top:0,right:1000,bottom:800,width:1000,height:800});

    assert.equal(heading.getAttribute('contenteditable'),null);
    heading.dispatchEvent(new f.w.MouseEvent('pointerdown',{bubbles:true,cancelable:true,clientX:110,clientY:110,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointermove',{bubbles:true,cancelable:true,clientX:180,clientY:150,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointerup',{bubbles:true,cancelable:true,clientX:180,clientY:150,button:0}));
    let saved=await f.save();
    const first=canonicalNodeById(saved,'home.h1.node.1') || {};
    assert.equal(first.style?.offsetXPercent,undefined);
    assert.equal(first.style?.offsetYPx,undefined);

    f.click('main h1');
    heading.dispatchEvent(new f.w.MouseEvent('dblclick',{bubbles:true,cancelable:true}));
    assert.equal(heading.getAttribute('contenteditable'),'plaintext-only');
    heading.dispatchEvent(new f.w.Event('blur',{bubbles:false}));

    f.click('main h1');
    const move=f.w.document.querySelector('.legend-cms-move-handle');
    move.dispatchEvent(new f.w.MouseEvent('pointerdown',{bubbles:true,cancelable:true,clientX:100,clientY:100,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointermove',{bubbles:true,cancelable:true,clientX:130,clientY:112,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointerup',{bubbles:true,cancelable:true,clientX:130,clientY:112,button:0}));
    saved=await f.save();
    const moved=canonicalNodeById(saved,'home.h1.node.1');
    assert.equal(moved.style.offsetXPercent,5);
    assert.equal(moved.style.offsetYPx,12);

    f.click('main h1');
    const right=f.w.document.querySelector('.legend-cms-edge-right');
    right.dispatchEvent(new f.w.MouseEvent('pointerdown',{bubbles:true,cancelable:true,clientX:300,clientY:120,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointermove',{bubbles:true,cancelable:true,clientX:360,clientY:120,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointerup',{bubbles:true,cancelable:true,clientX:360,clientY:120,button:0}));
    saved=await f.save();
    const resized=canonicalNodeById(saved,'home.h1.node.1');
    assert.ok(resized.style.widthPercent>33);
    assert.equal(resized.style.offsetXPercent,5);
    assert.equal(resized.style.offsetYPx,12);
  }finally{f.close();}
});

test('canonical containers are directly selectable and remain scoped to their exact section',async()=>{
  const doc=canonicalDocument();
  doc.pages['/'].composition=[
    canonicalNode('first','section','section',{children:[
      canonicalNode('first.outer','container','div',{className:'outer',children:[
        canonicalNode('first.inner','container','div',{className:'inner'}),
        canonicalNode('first.title','heading','h2',{text:'First'})
      ]})
    ]}),
    canonicalNode('second','section','section',{children:[
      canonicalNode('second.other','container','div',{className:'other'}),
      canonicalNode('second.title','heading','h2',{text:'Second'})
    ]})
  ];
  const f=await domFixture({doc});
  try{
    const outer=f.w.document.querySelector('.outer');
    assert.equal(outer.dataset.cmsEditable,'true');
    f.click('.outer');
    const selected=f.w.document.querySelector('.legend-cms-selected');
    assert.equal(selected.dataset.cmsCompositionId,'first.outer');
    assert.equal(selected.closest('[data-cms-section]').dataset.cmsCompositionId,'first');
    assert.notEqual(selected.closest('[data-cms-section]').dataset.cmsCompositionId,'second');
  }finally{f.close();}
});

test('mobile runtime contains canonical moved geometry without creating horizontal page scroll',async()=>{
  const doc=canonicalDocument();
  canonicalNodeById(doc,'home.h1.node.1').style={widthPercent:180,offsetXPercent:75};
  const f=await domFixture({doc,search:'',viewportWidth:390});
  try{
    const heading=f.w.document.querySelector('main h1');
    assert.equal(heading.style.width,'25%');
    assert.equal(heading.style.maxWidth,'25%');
    assert.equal(heading.style.left,'75%');
    assert.match(source,/html,body\{width:100%;max-width:100%;overflow-x:hidden;overscroll-behavior-x:none\}/);
    assert.match(source,/main,main>section,[^}]*overflow-x:clip/);
    assert.match(source,/body\{touch-action:pan-y pinch-zoom\}/);
  }finally{f.close();}
});

test('editor preview is horizontally locked to the rendered website at every breakpoint',async()=>{
  const f=await domFixture({viewportWidth:390});
  try{
    const preview=f.w.document.querySelector('.legend-cms-preview');
    assert.ok(preview);
    assert.equal(f.w.getComputedStyle(preview).overflowX,'clip');
    assert.equal(f.w.getComputedStyle(preview).maxWidth,'none');
    preview.scrollLeft=140;
    preview.dispatchEvent(new f.w.Event('scroll'));
    assert.equal(preview.scrollLeft,0);
    f.w.innerWidth=1200;
    preview.scrollLeft=60;
    preview.dispatchEvent(new f.w.Event('scroll'));
    assert.equal(preview.scrollLeft,0);
    f.w.dispatchEvent(new f.w.Event('resize'));
    assert.equal(preview.scrollLeft,0);
    assert.match(source,/\.legend-cms-preview\{width:100vw;max-width:none;min-width:100vw;[^}]*overflow-x:clip;[^}]*touch-action:pan-y pinch-zoom/);
    assert.match(source,/@media\(max-width:800px\)[\s\S]*?\.legend-cms-preview\{width:100%;max-width:100%;[^}]*overflow-x:hidden/);
    assert.doesNotMatch(source,/window\.innerWidth > 800/);
    assert.doesNotMatch(source,/body\.legend-cms-editing\{[^}]*grid-template/);
    assert.doesNotMatch(source,/body\.legend-cms-editing\.legend-cms-panel-hidden\{[^}]*grid-template/);
  }finally{f.close();}
});

test('canonical editor geometry can move anywhere inside the section while rendered width consumes remaining space',async()=>{
  const doc=canonicalDocument();
  canonicalNodeById(doc,'home.h1.node.1').style={widthPercent:80,offsetXPercent:75};
  const f=await domFixture({doc,viewportWidth:1280});
  try{
    const heading=f.w.document.querySelector('main h1');
    assert.equal(heading.style.width,'25%');
    assert.equal(heading.style.maxWidth,'25%');
    assert.equal(heading.style.left,'75%');
    f.click('main section');
    assert.equal(f.w.document.querySelector('#legend-cms-width').disabled,true);
    assert.equal(f.w.document.querySelector('#legend-cms-offset-x').disabled,true);
    assert.equal(f.w.document.querySelector('#legend-cms-width').value,'100');
    assert.equal(f.w.document.querySelector('#legend-cms-offset-x').value,'0');
    assert.match(source,/const availableWidth = Math\.max\(5, 100 - horizontalOffset\)/);
    assert.match(source,/style\.offsetXPercent = Math\.max\(0, Math\.min\(95, rawOffset\)\)/);
  }finally{f.close();}
});

test('business entity name remains profile-owned while canonical shell typography stays editable',async()=>{
  const doc=canonicalBusinessNavigation(canonicalDocument());
  doc.shell.header[0].children.unshift(
    canonicalNode('shell.business-name','text','strong',{
      text:'Template business',
      systemBinding:'business_name',
      style:{fontScale:2,fontFamily:'Georgia'}
    })
  );
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Canonical Business Name'},doc});
  try{
    const name=f.w.document.querySelector('[data-business-name]');
    assert.equal(name.textContent,'Canonical Business Name');
    f.click('[data-business-name]');
    assert.equal(f.w.document.querySelector('#legend-cms-inline-help').hidden,true);
    assert.equal(f.w.document.querySelector('#legend-cms-scale').disabled,false);
    assert.equal(f.w.document.querySelector('#legend-cms-link-group').hidden,true);
    assert.match(source,/entityBound = el\.hasAttribute\?\.\('data-business-name'\)/);
  }finally{f.close();}
});

test('store and cart are presentation-editable controls and SVG clicks never navigate in edit mode',async()=>{
  const html='<!doctype html><html><body data-page-key="home"><header><nav id="primary-nav" class="nav" data-public-nav></nav></header><main><section><h1>Heading</h1></section></main></body></html>';
  const doc=canonicalDocument();
  doc.store.enabled=true;
  doc.store.navigationLabel='Store';
  doc.store.cartIcon='cart';
  const store={enabled:true,label:'Store',businessKey:'fixture',storefrontUrl:'/store',cartUrl:'/store/cart'};
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'},doc,store,html});
  try{
    const storeLink=f.w.document.querySelector('[data-legend-store-nav="store"]');
    const cart=f.w.document.querySelector('[data-legend-store-nav="cart"]');
    assert.equal(storeLink.dataset.cmsEditable,'true');
    assert.equal(cart.dataset.cmsEditable,'true');
    assert.equal(storeLink.dataset.cmsLocked,undefined);
    assert.equal(cart.dataset.cmsLocked,undefined);
    const svgChild=cart.querySelector('path');
    const event=new f.w.MouseEvent('click',{bubbles:true,cancelable:true});
    assert.equal(svgChild.dispatchEvent(event),false);
    assert.equal(f.w.document.querySelector('.legend-cms-selected'),cart);
    assert.equal(f.w.document.querySelector('#legend-cms-link-group').hidden,true);
    f.input('#legend-cms-width','65');
    const saved=await f.save();
    assert.equal(saved.store.cartNavigation.style.widthPercent,65);
    assert.equal(saved.store.storeNavigation.style.widthPercent,undefined);
    assert.equal(canonicalNodes(saved).some(node=>node.actionKey==='commerce_cart'),false);
  }finally{f.close();}
});

test('store navigation is one far-right website-owned cluster and defaults to the exact Parfait cart glyph',()=>{
  assert.match(source,/cluster\.className='legend-store-nav-cluster'/);
  assert.match(source,/cluster\.append\(store,cart\);\s*nav\.appendChild\(cluster\)/);
  assert.match(source,/M6\.5 6\.5h15l-1\.8 8\.2a2 2 0 0 1-2 1\.6H9\.2a2 2 0 0 1-2-1\.7L5\.7 3\.8H3/);
  assert.match(source,/\['cart','bag','basket'\]/);
  assert.match(source,/id='legend-cms-store-cart-icon'/);
  assert.equal((source.match(/\.legend-store-nav-cluster\{/g)||[]).length,1);
});

test('store cart icon is persisted in the canonical website document and sanitized server-side',()=>{
  assert.match(editorContractsSource,/public string CartIcon \{ get; set; \} = "cart"/);
  assert.match(source,/cartIcon:cartIconValue \|\| effectiveCartIcon\(\)/);
  assert.match(businessRenderSource,/cartIcon:storeCartIcon/);
});

test('publish saves unsaved draft first then calls the explicit publish action',async()=>{
 const f=await domFixture();try{f.click('main h1');f.editSelected('New draft');f.click('#legend-cms-publish');await new Promise(r=>setTimeout(r,0));assert.equal(f.calls.length,3);assert.ok(f.calls[1].url.endsWith('/manage'));assert.ok(f.calls[2].url.endsWith('/manage/publish'));assert.equal(JSON.parse(f.calls[2].body).expectedRevision,'r2');}finally{f.close();}
});
test('editing current page preserves independent canonical page composition',async()=>{
 const doc=canonicalDocument();
 doc.pages['/about']={
   title:'About',description:'About page',
   navigation:{label:'About',showInNavigation:true,order:10,isDeleted:false},
   dynamicBinding:null,
   composition:[canonicalNode('about.section','section','section',{children:[
     canonicalNode('about.h1','heading','h1',{text:'Other page'})
   ]})]
 };
 const f=await domFixture({doc});try{
   f.click('main h1');f.editSelected('Home edit');
   const saved=await f.save();
   assert.equal(canonicalNodeById(saved,'about.h1','/about').text,'Other page');
   assert.equal(canonicalNodeById(saved,'home.h1.node.1').text,'Home edit');
 }finally{f.close();}
});
test('undo and redo restore content and leave other page drafts intact',async()=>{
 const f=await domFixture();try{f.click('main h1');f.editSelected('First edit');f.editSelected('Second edit');f.click('#legend-cms-undo');assert.equal(f.w.document.querySelector('main h1').textContent,'First edit');f.click('#legend-cms-redo');assert.equal(f.w.document.querySelector('main h1').textContent,'Second edit');}finally{f.close();}
});
test('selected blocks expose a dedicated move control plus independent resize zones and a quiet snap grid',async()=>{
 const f=await domFixture();try {
  f.click('main h1');
  const heading=f.w.document.querySelector('main h1');
  assert.equal(heading.draggable,false);
  const frame=f.w.document.querySelector('.legend-cms-selection-frame');
  assert.ok(frame);
  assert.equal(frame.querySelectorAll('.legend-cms-edge-handle').length,8);
  assert.ok(frame.querySelector('.legend-cms-move-handle'));
  assert.equal(frame.querySelector('.legend-cms-resize-handle'),null);
  assert.ok(source.includes('.legend-cms-move-handle{'));
  assert.equal(source.includes('.legend-cms-resize-handle{'),false);
  assert.match(source,/\.legend-cms-selection-frame\{[^}]*border:1px solid #d4ad45/);
  assert.match(source,/\.legend-cms-edge-right\{right:-6px\}/);
  assert.match(source,/\.legend-cms-corner-ne\{[^}]*cursor:nesw-resize/);
  assert.equal(/legend-cms-(?:resize|edge)[^\n]*border-radius:50%/.test(source),false);
  assert.match(source,/background-size:calc\(100% \/ 12\) 100%,100% 24px/);
  assert.match(source,/legend-cms-grid-overlay::before[^}]*opacity:0/);
  assert.equal(f.w.document.querySelector('#legend-cms-drag'),null);
 }finally{f.close();}
});

test('sandboxed code block saves source and previews without same-origin access',async()=>{
 const f=await domFixture();try {
  f.click('main h1');f.click('[data-add="code"]');
  const dialog=f.w.document.querySelector('.legend-cms-code-dialog');
  assert.ok(dialog);
  const sourceInput=dialog.querySelector('.legend-cms-code-source');
  sourceInput.value='<style>body{margin:0}</style><form><input aria-label="Custom field"></form><script>document.body.dataset.ready="1"<\/script>';
  f.click('.legend-cms-code-actions button');
  const block=f.w.document.querySelector('.legend-cms-embed');
  const frame=block.querySelector('iframe');
  assert.ok(frame.src.startsWith('data:text/html;charset=utf-8,'));
  assert.equal(decodeURIComponent(frame.src.slice(frame.src.indexOf(',')+1)),sourceInput.value);
  assert.equal(frame.getAttribute('sandbox').includes('allow-same-origin'),false);
  const saved=await f.save();const embed=canonicalNodes(saved).find(x=>x.type==='embed');
  assert.ok(embed);assert.equal(embed.text,sourceInput.value);assert.equal(embed.style.widthPercent,100);assert.equal(embed.style.heightPx,320);
  assert.equal(Object.hasOwn(saved.pages['/'],'extras'),false);
 }finally{f.close();}
});

test('solid section color replaces template gradient and reset restores the original',async()=>{
 const f=await domFixture();try {
  const section=f.w.document.querySelector('main section');section.style.backgroundImage='linear-gradient(black, blue)';
  f.click('main h1');f.click('#legend-cms-container');f.input('[data-style-key="backgroundColor"]','#000000');
  assert.equal(section.style.backgroundColor,'rgb(0, 0, 0)');assert.equal(section.style.backgroundImage,'none');
  const saved=await f.save();const loaded=await domFixture({doc:saved,search:''});try{assert.equal(loaded.w.document.querySelector('main section').style.backgroundImage,'none');}finally{loaded.close();}
 }finally{f.close();}
});
test('managed media tickets are render-only and never leak into canonical media identity',async()=>{
 const managedId='11111111-1111-1111-1111-111111111111';
 const doc=canonicalDocument();
 const section=canonicalNodeById(doc,'home.section.1');
 section.children=[
   canonicalNode('media.owned','image','img',{mediaAssetId:managedId,alt:'Owned'}),
   canonicalNode('media.external','image','img',{mediaUrl:'https://external.example/photo.png',alt:'External'})
 ];
 const f=await domFixture({doc});try{
   assert.equal(new URL(f.w.document.querySelector('[data-cms-id="media.owned"]').src).searchParams.get('ticket'),'ticket');
   assert.equal(new URL(f.w.document.querySelector('[data-cms-id="media.external"]').src).search,'');
   const saved=await f.save();
   assert.equal(canonicalNodeById(saved,'media.owned').mediaAssetId,managedId);
   assert.equal(JSON.stringify(saved).includes('?ticket'),false);
 }finally{f.close();}
});

for (const siteKey of ['legend', 'protect', 'business']) {
  test(`${siteKey}: shared studio keeps navigation, theme and metadata available without selection`, async () => {
    const f = await domFixture({siteKey, business: siteKey === 'business' ? {id: 'business-id', displayName: 'Fixture business'} : null});
    try {
      assert.equal(f.w.document.querySelectorAll('.legend-cms-tabs [data-open]').length, 5);
      assert.deepEqual([...f.w.document.querySelectorAll('.legend-cms-tabs [data-open]')].map(button=>button.textContent),['GPT Workspace','Source','Media','Publish','Advanced']);
      assert.ok(f.w.document.querySelector('[data-open="signals"]'));
      assert.ok(f.w.document.querySelector('[data-open="quality"]'));
      assert.ok(f.w.document.querySelector('[data-open="collaboration"]'));
      f.click('[data-open="page"]');
      assert.equal(f.w.document.querySelector('#legend-cms-page-title').disabled, false);
      f.input('#legend-cms-page-title', 'A title <with text>');
      f.input('#legend-cms-page-description', 'Services for our community.');
      f.click('[data-open="theme"]');
      assert.equal(f.w.document.querySelector('[data-theme-key="fontFamily"]').disabled, false);
      f.input('[data-theme-key="fontFamily"]', 'Georgia');
      const saved = await f.save();
      assert.equal(saved.pages['/'].title, 'A title <with text>');
      assert.equal(saved.pages['/'].description, 'Services for our community.');
      assert.equal(saved.theme.fontFamily, 'Georgia');
      assert.equal(Object.hasOwn(saved.pages, 'home'), false);
    } finally { f.close(); }
  });
}


test('business Pages manager stores navigation metadata in the canonical document', async()=>{
  const f=await domFixture({
    siteKey:'business',
    business:{id:'business-id',displayName:'Fixture business'},
    pages:[{path:'/',label:'Home'},{path:'/about',label:'About'},{path:'/services',label:'Services'}]
  });
  try {
    f.click('[data-open="page"]');
    assert.equal(f.w.document.querySelector('#legend-cms-page-business-tools').hidden,false);
    f.input('#legend-cms-page-nav-label','Start Here');
    f.input('#legend-cms-page-order','25');
    const visible=f.w.document.querySelector('#legend-cms-page-nav-visible');
    visible.checked=false;
    visible.dispatchEvent(new f.w.Event('input',{bubbles:true}));
    f.change('#legend-cms-page-parent','/about');
    const saved=await f.save();
    assert.equal(saved.pages['/'].navigation.label,'Start Here');
    assert.equal(saved.pages['/'].navigation.order,25);
    assert.equal(saved.pages['/'].navigation.showInNavigation,false);
    assert.equal(saved.pages['/'].navigation.parentPath,'/about');
  } finally { f.close(); }
});

test('business Pages manager creates a real custom page draft and saves before navigation', async()=>{
  const f=await domFixture({
    siteKey:'business',
    business:{id:'business-id',displayName:'Fixture business'},
    pages:[{path:'/',label:'Home'},{path:'/about',label:'About'}]
  });
  try {
    f.click('[data-open="page"]');
    f.input('#legend-cms-page-nav-label','Team');
    f.input('#legend-cms-page-slug','/team');
    f.click('#legend-cms-page-create');
    await new Promise(resolve=>setTimeout(resolve,0));
    const saveCall=f.calls.find(call=>call.method==='POST' && call.url.endsWith('/manage'));
    assert.ok(saveCall);
    const saved=JSON.parse(saveCall.body).document;
    assert.equal(saved.pages['/team'].title,'Team');
    assert.equal(saved.pages['/team'].navigation.label,'Team');
    assert.equal(saved.pages['/team'].navigation.isDeleted,false);
    assert.ok(saved.pages['/team'].composition.some(node=>node.type==='section'));
    assert.ok(canonicalNodes(saved,'/team').some(node=>node.type==='heading'&&node.text==='Team'));
    assert.equal(Object.hasOwn(saved.pages['/team'],'extras'),false);
  } finally { f.close(); }
});

test('business Pages manager renames the canonical page record without retaining a shadow route', async()=>{
  const html='<!doctype html><html><head></head><body data-page-key="services"><main><section><h1>Services</h1></section></main></body></html>';
  const f=await domFixture({
    siteKey:'business',
    business:{id:'business-id',displayName:'Fixture business'},
    pathname:'/business-preview/services/',
    pages:[{path:'/',label:'Home'},{path:'/services',label:'Services'}],
    html
  });
  try {
    f.click('[data-open="page"]');
    f.input('#legend-cms-page-slug','/work');
    f.click('#legend-cms-page-rename');
    await new Promise(resolve=>setTimeout(resolve,0));
    const saveCall=f.calls.find(call=>call.method==='POST' && call.url.endsWith('/manage'));
    assert.ok(saveCall);
    const saved=JSON.parse(saveCall.body).document;
    assert.ok(saved.pages['/work']);
    assert.equal(saved.pages['/services'],undefined);
    assert.equal(Object.hasOwn(saved.pages['/work'],'templatePath'),false);
  } finally { f.close(); }
});

test('business Pages manager cannot delete or rename the home route', async()=>{
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'},pages:[{path:'/',label:'Home'}]});
  try {
    f.click('[data-open="page"]');
    const remove=f.w.document.querySelector('#legend-cms-page-delete');
    assert.equal(remove.disabled,true);
    f.input('#legend-cms-page-slug','/new-home');
    f.click('#legend-cms-page-rename');
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.equal(f.calls.some(call=>call.method==='POST' && call.url.endsWith('/manage')),false);
  } finally { f.close(); }
});

test('LEGEND and Protect Pages views do not advertise arbitrary route creation before publication support exists', async()=>{
  for(const siteKey of ['legend','protect']){
    const f=await domFixture({siteKey,pages:[{path:'/',label:'Home'},{path:'/about',label:'About'}]});
    try{
      f.click('[data-open="page"]');
      assert.equal(f.w.document.querySelector('#legend-cms-page-business-tools').hidden,true);
      assert.equal(f.w.document.querySelector('#legend-cms-page-fixed-notice').hidden,false);
    } finally { f.close(); }
  }
});



test('media library reuses scoped image asset by canonical MediaAssetId only', async()=>{
  const assetId='11111111-1111-1111-1111-111111111111';
  const assetUrl='https://site.example/api/website-content/media/'+assetId;
  const f=await domFixture({mediaPayload:{assets:[{id:assetId,name:'team-logo.png',url:assetUrl,contentType:'image/png',sizeBytes:2048,createdUtc:'2026-09-24T00:00:00Z'}]}});
  try{
    f.click('main img');
    f.click('[data-open="media"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.match(f.w.document.querySelector('#legend-cms-media-status').textContent,/1 asset/);
    const preview=f.w.document.querySelector('.legend-cms-media-card img');
    assert.equal(new URL(preview.src).searchParams.get('ticket'),'ticket');
    f.click('.legend-cms-media-card button');
    const saved=await f.save();
    const image=canonicalNodeById(saved,'home.img.node.1');
    assert.equal(image.mediaAssetId,assetId);
    assert.equal(image.mediaUrl,undefined);
    assert.equal(JSON.stringify(saved).includes('imageDataUrl'),false);
    assert.equal(JSON.stringify(saved).includes('ticket='),false);
  } finally { f.close(); }
});


test('media library inserts existing video as canonical composition with MediaAssetId only', async()=>{
  const assetId='22222222-2222-2222-2222-222222222222';
  const assetUrl='https://site.example/api/website-content/media/'+assetId;
  const f=await domFixture({mediaPayload:{assets:[{id:assetId,name:'intro.mp4',url:assetUrl,contentType:'video/mp4',sizeBytes:8192,createdUtc:'2026-09-24T00:00:00Z'}]}});
  try{
    f.click('main h1');
    f.click('[data-open="media"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    f.click('.legend-cms-media-card button');
    const saved=await f.save();
    const video=canonicalNodeByType(saved,'video');
    assert.ok(video);
    assert.equal(video.mediaAssetId,assetId);
    assert.equal(video.mediaUrl,undefined);
    assert.equal(JSON.stringify(saved).includes('videoUrl'),false);
    assert.equal(JSON.stringify(saved).includes('extras'),false);
    assert.equal(JSON.stringify(saved).includes('ticket='),false);
  } finally { f.close(); }
});


test('canonical block becomes one composition reusable definition and inserted instance remains a reference', async()=>{
  const f=await domFixture();
  try{
    f.click('main h1');
    f.click('[data-add="text"]');
    f.editSelected('Reusable promise');
    f.click('[data-open="components"]');
    f.input('#legend-cms-component-name','Promise block');
    f.click('#legend-cms-component-save');
    const rows=f.w.document.querySelectorAll('.legend-cms-component-row');
    assert.equal(rows.length,1);
    rows[0].querySelectorAll('button')[0].dispatchEvent(new f.w.MouseEvent('click',{bubbles:true,cancelable:true}));
    const saved=await f.save();
    const definition=Object.values(saved.reusableComponents)[0];
    assert.equal(definition.name,'Promise block');
    assert.equal(definition.kind,'block');
    assert.equal(definition.composition.length,1);
    assert.equal(definition.composition[0].text,'Reusable promise');
    assert.deepEqual(definition.composition[0].signals,[]);
    const instances=canonicalNodes(saved).filter(node=>node.type==='reusable');
    assert.equal(instances.length,1);
    assert.equal(instances[0].syncSourceId,definition.id);
    assert.equal(canonicalNodes(saved).filter(node=>node.type==='text'&&node.text==='Reusable promise').length,1);
    assert.equal(JSON.stringify(saved).includes('"extras"'),false);
  } finally { f.close(); }
});


test('updating canonical reusable definition refreshes instances and definition cannot delete while used', async()=>{
  const f=await domFixture();
  try{
    f.click('main h1');
    f.click('[data-add="text"]');
    f.editSelected('First component copy');
    f.click('[data-open="components"]');
    f.input('#legend-cms-component-name','Shared text');
    f.click('#legend-cms-component-save');
    let row=f.w.document.querySelector('.legend-cms-component-row');
    row.querySelectorAll('button')[0].dispatchEvent(new f.w.MouseEvent('click',{bubbles:true,cancelable:true}));
    assert.equal(f.w.document.querySelector('.cms-reusable-instance').textContent,'First component copy');
    const original=[...f.w.document.querySelectorAll('[data-cms-id]')].find(node=>node.dataset.cmsCompositionId && node.textContent==='First component copy' && !node.closest('.cms-reusable-instance'));
    original.dispatchEvent(new f.w.MouseEvent('click',{bubbles:true,cancelable:true}));
    f.editSelected('Updated component copy');
    f.click('[data-open="components"]');
    row=f.w.document.querySelector('.legend-cms-component-row');
    row.querySelectorAll('button')[1].dispatchEvent(new f.w.MouseEvent('click',{bubbles:true,cancelable:true}));
    assert.equal(f.w.document.querySelector('.cms-reusable-instance').textContent,'Updated component copy');
    row=f.w.document.querySelector('.legend-cms-component-row');
    assert.equal(row.querySelectorAll('button')[2].disabled,true);
    const saved=await f.save();
    const definition=Object.values(saved.reusableComponents)[0];
    assert.equal(definition.composition[0].text,'Updated component copy');
    assert.equal(canonicalNodes(saved).filter(node=>node.type==='reusable').length,1);
  } finally { f.close(); }
});

test('protected system components cannot be serialized into reusable component storage', async()=>{
  const doc=canonicalDocument();
  canonicalNodeById(doc,'home.section.1').children.push(
    canonicalNode('home.form','form','form',{systemKey:'canonical_inquiry',title:'Contact us',text:'Send inquiry'})
  );
  const f=await domFixture({doc});
  try{
    f.click('form[data-website-inquiry]');
    f.click('[data-open="components"]');
    f.input('#legend-cms-component-name','Should not copy system authority');
    f.click('#legend-cms-component-save');
    assert.match(f.w.document.querySelector('#legend-cms-component-status').textContent,/Protected platform components cannot become reusable content/);
    const saved=await f.save();
    assert.deepEqual(saved.reusableComponents,{});
  } finally { f.close(); }
});

test('missing canonical reusable definition renders warning without copied fallback content', async()=>{
  const doc=canonicalDocument();
  canonicalNodeById(doc,'home.section.1').children.push(canonicalNode('missing-instance','reusable','div',{syncSourceId:'missing-component'}));
  const f=await domFixture({doc});
  try{
    assert.match(f.w.document.querySelector('.cms-reusable-instance').textContent,/Reusable component is unavailable/);
    const saved=await f.save();
    const instance=canonicalNodeById(saved,'missing-instance');
    assert.equal(instance.syncSourceId,'missing-component');
    assert.equal(canonicalNodes(saved).filter(node=>node.id==='missing-instance').length,1);
  } finally { f.close(); }
});

test('quality inspector keeps saved-server checks separate from rendered canonical-canvas checks', async () => {
  const doc=canonicalDocument();
  const section=canonicalNodeById(doc,'home.section.1');
  section.children=[
    canonicalNode('quality.h1','heading','h1',{text:'Title'}),
    canonicalNode('quality.image','image','img',{mediaUrl:'https://images.example/a.png',alt:''}),
    canonicalNode('quality.link','link','a',{text:'Broken',href:'#'})
  ];
  const qualityPayload={source:'saved_draft_server',revision:7,errorCount:1,warningCount:1,checks:[
    {code:'dynamic_collection_missing',severity:'error',message:'Saved draft dynamic collection is unavailable.'},
    {code:'page_title_missing',severity:'warning',message:'Saved draft page title is missing.'}
  ]};
  const f=await domFixture({doc,qualityPayload});
  try {
    f.click('[data-open="quality"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    const savedMeta=f.w.document.querySelector('#legend-cms-quality-saved-meta').textContent;
    const liveMeta=f.w.document.querySelector('#legend-cms-quality-live-meta').textContent;
    const savedText=f.w.document.querySelector('#legend-cms-quality-saved').textContent;
    const liveText=f.w.document.querySelector('#legend-cms-quality-live').textContent;
    assert.match(savedMeta,/Saved draft checks \(server\) · revision 7 · 1 errors · 1 warnings/);
    assert.match(liveMeta,/Live page checks \(rendered canvas\)/);
    assert.match(savedText,/Saved draft dynamic collection is unavailable/);
    assert.doesNotMatch(savedText,/missing alternative text|no working destination/);
    assert.match(liveText,/missing alternative text/);
    assert.match(liveText,/no working destination/);
    assert.ok(f.calls.some(call=>new URL(call.url).pathname.endsWith('/manage/quality')));
  } finally { f.close(); }
});

test('layers recover a hidden canonical section without losing its descendants', async () => {
  const f=await domFixture();
  try {
    f.click('main section');
    const hidden=f.w.document.querySelector('#legend-cms-hidden');
    hidden.checked=true;
    hidden.dispatchEvent(new f.w.Event('input',{bubbles:true}));
    f.click('[data-open="layers"]');
    f.click('#legend-cms-layers button[aria-label^="Show Section"]');
    assert.equal(f.w.document.querySelector('main section').hidden,false);
    assert.equal(f.w.document.querySelector('main h1').textContent,'Template title');
    const saved=await f.save();
    assert.equal(canonicalNodeById(saved,'home.section.1').hidden,false);
  } finally { f.close(); }
});


test('duplicate canonical link has independent stable identity and history restores each canonical transaction', async () => {
  const f=await domFixture();
  try {
    f.click('main a');
    f.input('#legend-cms-href','https://business.example/book');
    const originalId=f.w.document.querySelector('main a').dataset.cmsCompositionId;
    f.click('#legend-cms-duplicate');
    const duplicate=f.w.document.querySelector('.legend-cms-selected');
    const duplicateId=duplicate.dataset.cmsCompositionId;
    assert.notEqual(duplicateId,originalId);
    f.input('#legend-cms-href','https://business.example/second');
    const saved=await f.save();
    const links=canonicalNodes(saved).filter(node=>node.type==='link');
    assert.equal(links.length,2);
    assert.equal(links.find(node=>node.id===originalId).href,'https://business.example/book');
    assert.equal(links.find(node=>node.id===duplicateId).href,'https://business.example/second');
    f.click('#legend-cms-undo');
    assert.equal(f.w.document.querySelectorAll('main a').length,2);
    f.click('#legend-cms-undo');
    assert.equal(f.w.document.querySelectorAll('main a').length,1);
  } finally { f.close(); }
});

test('canonical visual style preserves numeric font weight and signed letter spacing', async () => {
  const f=await domFixture();
  try {
    f.click('main h1');
    f.input('[data-style-key="fontWeight"]','700');
    f.input('[data-style-key="letterSpacing"]','-1.25');
    const saved=await f.save();
    const style=canonicalNodeById(saved,'home.h1.node.1').style;
    assert.equal(style.fontWeight,700);
    assert.equal(style.letterSpacing,-1.25);
  } finally { f.close(); }
});

test('deleting an added canonical block removes the node from save and published reload', async () => {
  const f=await domFixture(); let saved;
  try {
    f.click('main h1');
    f.click('[data-add="button"]');
    const addedId=f.w.document.querySelector('.legend-cms-selected').dataset.cmsCompositionId;
    f.click('#legend-cms-remove');
    saved=await f.save();
    assert.equal(canonicalNodeById(saved,addedId),null);
    assert.equal(JSON.stringify(saved).includes('"extras"'),false);
  } finally { f.close(); }
  const published=await domFixture({doc:saved,search:''});
  try { assert.equal(published.w.document.querySelectorAll('[data-cms-id]').length>0,true); }
  finally { published.close(); }
});


test('canonical link editing rejects editor credentials and insecure absolute URLs before save', async () => {
  const f=await domFixture();
  try {
    f.click('main a');
    f.input('#legend-cms-href','/contact');
    for(const value of ['http://external.example','/?legendEdit=secret','/?ticket=secret','//external.example']){
      f.input('#legend-cms-href',value);
      assert.equal(f.w.document.querySelector('main a').getAttribute('href'),'/contact');
    }
    const saved=await f.save();
    assert.equal(canonicalNodeById(saved,'home.a.node.1').href,'/contact');
    assert.equal(JSON.stringify(saved).includes('secret'),false);
  } finally { f.close(); }
});

for (const siteKey of ['legend', 'protect', 'business']) {
 test(`${siteKey}: page selector uses supplied catalog, including pages absent from navigation`, async () => {
  const business=siteKey==='business'?{id:'business-test',displayName:'Business'}:null;
  const f=await domFixture({siteKey,business,pages:[{path:'/',label:'Home'},{path:'/unlinked-page',label:'Unlinked page'}]});
  try { const options=[...f.w.document.querySelector('#legend-cms-page-select').options];assert.ok(options.some(o=>o.value==='/unlinked-page'&&o.textContent==='Unlinked page'));assert.ok(!options.some(o=>o.value==='/contact')); }
  finally {f.close();}
 });
}
test('page selector updates immediately from canonical page metadata instead of supplied catalog labels',async()=>{
 const doc=canonicalDocument();
 doc.pages['/'].title='Live Home';
 doc.pages['/about']={
   title:'Live About',description:'About',
   navigation:{label:null,showInNavigation:true,order:10,isDeleted:false},
   dynamicBinding:null,
   composition:[canonicalNode('about.section','section','section',{children:[
     canonicalNode('about.title','heading','h1',{text:'About'})
   ]})]
 };
 const f=await domFixture({siteKey:'legend',pages:[{path:'/',label:'Hard coded home'},{path:'/about',label:'Hard coded about'}],doc});
 try{
   let home=[...f.w.document.querySelector('#legend-cms-page-select').options].find(o=>o.value==='/');
   assert.equal(home.textContent,'Live Home');
   f.input('#legend-cms-page-title','Renamed Live Home');
   home=[...f.w.document.querySelector('#legend-cms-page-select').options].find(o=>o.value==='/');
   assert.equal(home.textContent,'Renamed Live Home');
 }finally{f.close();}
});

test('business canonical navigation-label edits immediately repaint the shared page selector',async()=>{
 const doc=canonicalBusinessNavigation(canonicalDocument());
 doc.pages['/'].title='Home title';
 doc.pages['/'].navigation={label:'Home',showInNavigation:true,order:0,isDeleted:false};
 const f=await domFixture({siteKey:'business',business:{id:'business-test',displayName:'Business'},pages:[{path:'/',label:'Template Home'}],doc});
 try{
   f.click('[data-open="page"]');
   f.input('#legend-cms-page-nav-label','Start Here');
   const home=[...f.w.document.querySelector('#legend-cms-page-select').options].find(o=>o.value==='/');
   assert.equal(home.textContent,'Start Here');
 }finally{f.close();}
});

test('business selector includes canonical custom routes from only the authorized draft',async()=>{
 const doc=canonicalBusinessNavigation(canonicalDocument());
 doc.pages['/special-offer']={
   title:'Special offer',description:'Special offer',
   navigation:{label:'Special offer',showInNavigation:true,order:10,isDeleted:false},
   dynamicBinding:null,
   composition:[canonicalNode('special.section','section','section',{children:[
     canonicalNode('special.title','heading','h1',{text:'Special offer'})
   ]})]
 };
 const f=await domFixture({siteKey:'business',business:{id:'business-test',displayName:'Business'},pages:[{path:'/',label:'Home'}],doc});
 try {assert.ok([...f.w.document.querySelector('#legend-cms-page-select').options].some(o=>o.value==='/special-offer'&&o.textContent==='Special offer'));}finally{f.close();}
});

test('manually resized canonical sections reclaim space and never become internal scroll containers',async()=>{
  const doc=canonicalDocument();
  canonicalNodeById(doc,'home.section.1').style={heightPx:180,offsetYPx:72};
  const f=await domFixture({doc});
  try{
    const section=f.w.document.querySelector('main section');
    assert.equal(section.style.height,'180px');
    assert.equal(section.style.minHeight,'0');
    assert.equal(section.style.overflow,'visible');
    assert.equal(section.style.top,'');
  }finally{f.close();}
});

test('published business v3 hydration keeps the full custom-domain page catalog on every route',async()=>{
  const pageCatalog=[
    {route:'/',label:'Home',template:true,showInNavigation:true,order:0},
    {route:'/about',label:'About',template:true,showInNavigation:true,order:10},
    {route:'/services',label:'Services',template:true,showInNavigation:true,order:20},
    {route:'/contact',label:'Contact',template:true,showInNavigation:true,order:30}
  ];
  const document=canonicalBusinessNavigation(canonicalDocument());
  document.pages['/about']={
    title:'About',description:'About',
    navigation:{label:'About',showInNavigation:true,order:10,isDeleted:false},
    dynamicBinding:null,
    composition:[canonicalNode('about.section','section','section',{children:[
      canonicalNode('about.title','heading','h1',{text:'About'})
    ]})]
  };
  const html='<!doctype html><html><body data-page-key="about"><header class="site-header"><nav id="primary-nav" class="nav" data-public-nav></nav></header><main></main><footer class="site-footer"></footer><script id="legend-cms-published-document" type="application/json"></script></body></html>';
  const dom=new JSDOM(html,{url:'https://camo.example/about',runScripts:'outside-only'});
  const {window:w}=dom;
  w.document.getElementById('legend-cms-published-document').textContent=JSON.stringify({
    document,
    business:{id:'business-id',displayName:'CAMO'},
    pageCatalog,
    pageKey:'about',
    server:false,
    runtime:{apiBase:'https://protect.example.test'}
  });
  w.CSS={escape:value=>String(value)};
  w.fetch=async()=>({ok:true,json:async()=>({})});
  w.eval(source);
  await new Promise(resolve=>setTimeout(resolve,0));
  try{
    const links=[...w.document.querySelectorAll('#primary-nav>a[data-legend-page-nav="true"]')];
    assert.deepEqual(links.map(link=>link.textContent),['Home','About','Services','Contact']);
    assert.deepEqual(links.map(link=>new URL(link.href).origin),Array(4).fill('https://camo.example'));
    assert.deepEqual(links.map(link=>new URL(link.href).pathname),['/','/about','/services','/contact']);
  }finally{w.close();}
});

test('business editor canonical navigation tabs do not follow links and drag order writes canonical page order',async()=>{
  const pages=[{path:'/',label:'Home'},{path:'/about',label:'About'},{path:'/services',label:'Services'}];
  const doc=canonicalBusinessNavigation(canonicalDocument());
  doc.pages['/'].navigation={label:'Home',showInNavigation:true,order:0,isDeleted:false};
  doc.pages['/about']={title:'About',description:'',navigation:{label:'About',showInNavigation:true,order:10,isDeleted:false},dynamicBinding:null,composition:[]};
  doc.pages['/services']={title:'Services',description:'',navigation:{label:'Services',showInNavigation:true,order:20,isDeleted:false},dynamicBinding:null,composition:[]};
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'CAMO'},pages,doc});
  try{
    const nav=f.w.document.querySelector('#primary-nav');
    const links=[...nav.querySelectorAll('[data-legend-page-nav="true"]')];
    assert.ok(links.every(link=>link.tagName==='BUTTON'));
    assert.ok(links.every(link=>!link.hasAttribute('href')));

    for(const [index,link] of links.entries()){
      link.getBoundingClientRect=()=>({left:index*100,right:index*100+80,width:80,top:0,bottom:30,height:30});
    }
    const about=links[1];
    about.dispatchEvent(new f.w.MouseEvent('pointerdown',{bubbles:true,cancelable:true,clientX:140,clientY:15,button:0}));
    nav.dispatchEvent(new f.w.MouseEvent('pointermove',{bubbles:true,cancelable:true,clientX:245,clientY:15,button:0}));
    nav.dispatchEvent(new f.w.MouseEvent('pointerup',{bubbles:true,cancelable:true,clientX:245,clientY:15,button:0}));
    assert.deepEqual([...nav.querySelectorAll('[data-legend-page-nav="true"]')].map(link=>link.textContent),['Home','Services','About']);
    const saved=await f.save();
    assert.equal(saved.pages['/'].navigation.order,0);
    assert.equal(saved.pages['/services'].navigation.order,10);
    assert.equal(saved.pages['/about'].navigation.order,20);
  }finally{f.close();}
});

test('editor navigation uses non-link controls and opens pages only on double-click',()=>{
  assert.match(source,/createElement\(editorMode \? 'button' : 'a'\)/);
  assert.match(source,/link\.title='Drag to reorder · double-click to edit this page'/);
  assert.match(source,/addEventListener\('dblclick',[\s\S]*navigateToEditorPage\(entry\.route\)/);
});

test('text scaling stays unbounded while manually resized sections remain non-scrolling canvases',()=>{
  assert.match(source,/id="legend-cms-scale" type="number" min="0" step="any"/);
  assert.doesNotMatch(source,/id="legend-cms-scale"[^>]*max=/);
  assert.match(source,/scaledElements\.set\(el, style\.fontScale\)/);
  assert.match(source,/el\.style\.minHeight = '0';[\s\S]*el\.style\.height = `\$\{style\.heightPx\}px`;[\s\S]*el\.style\.overflow = 'visible';/);
});

test('business header navigation renders once from the canonical v3 page catalog and discards stale DOM links',async()=>{
  const html='<!doctype html><html><body data-page-key="home"><header class="site-header"><nav id="primary-nav" class="nav" data-public-nav><a href="/">Stale Home</a><a href="/about">Stale About</a><a href="/">Duplicate Home</a><a href="/services">Stale Services</a></nav></header><main></main><footer class="site-footer"></footer></body></html>';
  const doc=canonicalBusinessNavigation(canonicalDocument());
  doc.pages['/'].title='Home';
  doc.pages['/'].navigation={label:'Home',showInNavigation:true,order:0,isDeleted:false};
  doc.pages['/team']={title:'Team',description:'',navigation:{label:'Our Team',showInNavigation:true,order:10,isDeleted:false},dynamicBinding:null,composition:[]};
  doc.pages['/hidden']={title:'Hidden',description:'',navigation:{label:'Hidden',showInNavigation:false,order:20,isDeleted:false},dynamicBinding:null,composition:[]};
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'},pages:[{path:'/',label:'Home'},{path:'/team',label:'Team'}],doc,html,search:''});
  try{
    const links=[...f.w.document.querySelectorAll('#primary-nav>a')];
    assert.deepEqual(links.map(x=>x.textContent),['Home','Our Team']);
    assert.equal(new Set(links.map(x=>x.getAttribute('href'))).size,links.length);
    assert.equal(links.some(x=>x.textContent.startsWith('Stale')||x.textContent==='Duplicate Home'),false);
  }finally{f.close();}
});

test('business page list directly manages canonical navigation label visibility and deletion from one page record',async()=>{
  const doc=canonicalBusinessNavigation(canonicalDocument());
  doc.pages['/'].title='Home';
  doc.pages['/'].navigation={label:'Home',showInNavigation:true,order:0,isDeleted:false};
  doc.pages['/about']={title:'About',description:'',navigation:{label:'About',showInNavigation:true,order:10,isDeleted:false},dynamicBinding:null,composition:[]};
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'},pages:[{path:'/',label:'Home'},{path:'/about',label:'About'}],doc});
  try{
    const rows=[...f.w.document.querySelectorAll('#legend-cms-page-list .legend-cms-page-row')];
    assert.equal(rows.length,2);
    const about=rows.find(row=>row.querySelector('button')?.textContent==='/about');
    assert.ok(about);
    const label=about.querySelector('input[type="text"]');
    label.value='Our Story';
    label.dispatchEvent(new f.w.Event('change',{bubbles:true}));
    assert.deepEqual([...f.w.document.querySelectorAll('#primary-nav>[data-legend-page-nav="true"]')].map(x=>x.textContent),['Home','Our Story']);
    const saved=await f.save();
    assert.equal(saved.pages['/about'].navigation.label,'Our Story');
    const refreshed=[...f.w.document.querySelectorAll('#legend-cms-page-list .legend-cms-page-row')].find(row=>row.querySelector('button')?.textContent==='/about');
    refreshed.querySelector('button:last-child').click();
    const deleted=await f.save();
    assert.equal(deleted.pages['/about'].navigation.isDeleted,true);
    assert.equal([...f.w.document.querySelectorAll('#primary-nav>[data-legend-page-nav="true"]')].some(x=>x.dataset.legendPageRoute==='/about'),false);
  }finally{f.close();}
});

test('canonical sections duplicate directly in composition while shared shell remains immutable',async()=>{
  const doc=canonicalDocument({title:'Hero title'});
  doc.pages['/'].composition=[canonicalNode('hero','section','section',{className:'hero',children:[
    canonicalNode('hero.title','heading','h1',{text:'Hero title'}),
    canonicalNode('hero.copy','text','p',{text:'Hero copy'})
  ]})];
  doc.shell={
    header:[canonicalNode('shell.header','container','header',{className:'site-header',children:[canonicalNode('shell.header.label','text','strong',{text:'Header'})]})],
    footer:[canonicalNode('shell.footer','container','footer',{className:'site-footer',children:[canonicalNode('shell.footer.label','text','span',{text:'Footer'})]})]
  };
  const html='<!doctype html><html><body data-page-key="home"><header class="site-header"></header><main></main><footer class="site-footer"></footer></body></html>';
  const f=await domFixture({doc,html});
  try{
    f.click('main section');
    const duplicate=f.w.document.querySelector('#legend-cms-duplicate');
    assert.equal(duplicate.disabled,false);
    assert.equal(duplicate.textContent,'Duplicate section');
    const originalId=f.w.document.querySelector('main section').dataset.cmsCompositionId;
    f.click('#legend-cms-duplicate');
    const saved=await f.save();
    assert.equal(saved.pages['/'].composition.length,2);
    assert.equal(saved.pages['/'].composition[0].id,originalId);
    assert.notEqual(saved.pages['/'].composition[1].id,originalId);
    assert.equal(saved.pages['/'].composition[1].children[0].text,'Hero title');
    assert.equal(f.w.document.querySelectorAll('main>section').length,2);
    f.click('.site-header');
    assert.equal(f.w.document.querySelector('#legend-cms-duplicate').disabled,true);
    assert.equal(f.w.document.querySelector('#legend-cms-remove').disabled,true);
    assert.equal(f.w.document.querySelector('#legend-cms-remove').textContent,'Global shell · cannot delete');
    f.click('.site-footer');
    assert.equal(f.w.document.querySelector('#legend-cms-duplicate').disabled,true);
    assert.equal(f.w.document.querySelector('#legend-cms-remove').disabled,true);
    assert.equal(JSON.stringify(saved).includes('"extras"'),false);
  }finally{f.close();}
});

test('shared public stylesheet keeps footer at viewport bottom without fixing it over content',()=>{
  assert.match(publicCss,/body\{min-height:100dvh;display:flex;flex-direction:column;overflow-x:clip\}/);
  assert.match(publicCss,/main,\.public-main,\.layout-content\{flex:1 0 auto;min-height:0\}/);
  assert.match(publicCss,/\.site-footer\{flex:0 0 auto;margin-top:auto\}/);
  assert.doesNotMatch(publicCss,/\.site-footer\{[^}]*position:fixed/);
});


test('header and footer edits persist in canonical shared shell composition across pages',async()=>{
  const doc=canonicalDocument();
  doc.shell={
    header:[canonicalNode('shell.header','container','header',{className:'site-header',children:[
      canonicalNode('shell.header.brand','text','strong',{text:'Brand'})
    ]})],
    footer:[canonicalNode('shell.footer','container','footer',{className:'site-footer',children:[
      canonicalNode('shell.footer.copy','text','p',{text:'Footer copy'})
    ]})]
  };
  const html='<!doctype html><html><body data-page-key="home"><header class="site-header"></header><main></main><footer class="site-footer"></footer></body></html>';
  const f=await domFixture({doc,siteKey:'business',business:{id:'business-id',displayName:'Business'},pages:[{path:'/',label:'Home'},{path:'/about',label:'About'}],html});
  try{
    f.click('.site-header strong'); f.input('[data-style-key="fontSize"]','31');
    f.click('.site-footer p'); f.input('[data-style-key="fontSize"]','19');
    const saved=await f.save();
    const header=(()=>{let hit=null;visitCanonicalNodes(saved.shell.header,node=>{if(node.id==='shell.header.brand')hit=node;});return hit;})();
    const footer=(()=>{let hit=null;visitCanonicalNodes(saved.shell.footer,node=>{if(node.id==='shell.footer.copy')hit=node;});return hit;})();
    assert.equal(header.style.fontSize,31);
    assert.equal(footer.style.fontSize,19);
    assert.equal(Object.hasOwn(saved,'elements'),false);
  }finally{f.close();}
});

test('cart icon launches larger and stores adjustable size through canonical v3 store settings',async()=>{
  const doc=canonicalBusinessNavigation(canonicalDocument());
  doc.store={enabled:true,navigationLabel:'Store',cartIcon:'cart',cartIconSizePx:28,storeNavigation:{style:{}},cartNavigation:{style:{}}};
  const store={enabled:true,label:'Store',cartIcon:'cart',cartIconSizePx:28,businessKey:'fixture',storefrontUrl:'/store',cartUrl:'/store/cart',managerUrl:'/commerce/manage/products?ticket=ticket'};
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Business'},doc,store});
  try{
    const svg=f.w.document.querySelector('.legend-store-cart-icon');
    assert.equal(svg.getAttribute('width'),'28');
    assert.equal(svg.getAttribute('height'),'28');
    f.change('#legend-cms-store-cart-size','42');
    await new Promise(resolve=>setTimeout(resolve,0));
    const call=f.calls.find(call=>call.method==='POST' && call.url.includes('/manage/store/enable'));
    assert.ok(call);
    assert.equal(JSON.parse(call.body).cartIconSizePx,42);
  }finally{f.close();}
});

test('site palette does not retain the prior default blue gradient stop',async()=>{
 const doc=canonicalDocument();
 doc.theme={navy:'#000000',navyDeep:'#000000'};
 const f=await domFixture({doc});
 try {assert.equal(f.w.document.documentElement.style.getPropertyValue('--web-navy-royal'),'#000000');}finally{f.close();}
});

test('scoped favicon is projected from the canonical v3 website document without leaking editor tickets',async()=>{
  const media='https://site.example/api/website-content/media/11111111-1111-1111-1111-111111111111';
  const doc=canonicalDocument();
  doc.faviconImageDataUrl=media;
  const html='<!doctype html><html><head><link rel="icon" href="/favicon.svg" type="image/svg+xml"></head><body data-page-key="home"><main></main></body></html>';
  const f=await domFixture({doc,html});
  try {
    const link=f.w.document.querySelector('link[rel~="icon"]');
    assert.equal(new URL(link.href).pathname,'/api/website-content/media/11111111-1111-1111-1111-111111111111');
    assert.equal(new URL(link.href).searchParams.get('ticket'),'ticket');
    assert.equal(link.hasAttribute('type'),false);
    const saved=await f.save();
    assert.equal(saved.faviconImageDataUrl,media);
    assert.equal(saved.faviconImageDataUrl.includes('ticket='),false);
  } finally { f.close(); }
});

test('removing a scoped favicon restores the canonical fallback before publication',async()=>{
  const media='https://site.example/api/website-content/media/11111111-1111-1111-1111-111111111111';
  const html='<!doctype html><html><head><link rel="icon" href="/favicon.svg" type="image/svg+xml"></head><body data-page-key="home"><main><section><h1>Title</h1></section></main></body></html>';
  const f=await domFixture({doc:{faviconImageDataUrl:media},html});
  try {
    f.click('#legend-cms-favicon-remove');
    const link=f.w.document.querySelector('link[rel~="icon"]');
    assert.equal(link.getAttribute('href'),'/favicon.svg');
    assert.equal(link.getAttribute('type'),'image/svg+xml');
    const saved=await f.save();
    assert.equal(saved.faviconImageDataUrl,null);
  } finally { f.close(); }
});


test('managed canonical action identity survives copy styling and cannot be downgraded to custom wiring',async()=>{
  const actions=[{key:'business_schedule',group:'Schedule',label:'Schedule',defaultText:'Book consultation',href:'https://booking.example/confirmed-flow',analyticsEventName:'cta_click',behaviorKey:'cta_click'}];
  const f=await domFixture({ctaCatalog:actions});
  try {
    f.click('main h1'); f.click('[data-add="button"]');
    f.change('#legend-cms-action','managed:business_schedule:0');
    const button=f.w.document.querySelector('.legend-cms-selected');
    const elementId=button.dataset.cmsCompositionId;
    f.editSelected('Pay now and complete my application');
    const style=f.w.document.querySelector('[data-style-key="fontSize"]');
    style.value='29'; style.dispatchEvent(new f.w.Event('input',{bubbles:true}));
    f.input('#legend-cms-href','https://unrelated.example');
    f.change('#legend-cms-action','custom');
    const saved=await f.save();
    const cta=canonicalNodeById(saved,elementId);
    assert.equal(cta.actionKey,'business_schedule');
    assert.equal(cta.href,'https://booking.example/confirmed-flow');
    assert.equal(cta.text,'Pay now and complete my application');
    assert.equal(cta.style.fontSize,29);
    assert.equal(button.dataset.cmsCompositionId,elementId);
    assert.equal(f.w.document.querySelector('#legend-cms-action').disabled,true);
  }finally{f.close();}
});



test('signal-only nodes are protected in the editor and presentation duplication never clones hidden mappings',async()=>{
  const doc=canonicalDocument();
  const tracked=doc.pages['/'].composition[0].children[0];
  tracked.signals=[{id:'signal-only-binding',eventName:'cta_click',actionKey:'cta_click',trigger:'click',deliveryMode:'analytics',oncePerSession:true,matchingFields:[]}];
  const f=await domFixture({doc});
  try{
    f.click('main h1');
    const remove=f.w.document.querySelector('#legend-cms-remove');
    assert.equal(remove.disabled,true);
    assert.match(remove.textContent,/Protected wiring/);
    assert.match(source,/Array\.isArray\(current\.signals\) && current\.signals\.length > 0/);
    assert.match(source,/function containsProtectedSystemNode[\s\S]*Array\.isArray\(node\.signals\) && node\.signals\.length > 0/);
    assert.match(source,/Duplicating presentation never duplicates hidden analytics\/provider wiring[\s\S]*current\.signals=\[\]/);
  }finally{f.close();}
});

test('server-rejected GPT source edit renders the canonical red correction in Source and GPT workspaces',async()=>{
  const correction='CANONICAL CORRECTION REQUIRED: preserve the stable node ID and edit presentation only.';
  const payload={
    error:'website_site_source_invalid',
    message:"Protected component 'home.h1.node.1' cannot be removed because its canonical behavior is platform-owned.",
    canonicalProtectionViolation:true,
    correction
  };
  const contract={schema:'legend-website-studio-agent/v1',promptTemplate:'contract',protectedEditCorrection:correction};
  const f=await domFixture({agentContract:contract,sourceValidationStatus:400,sourceValidationPayload:payload});
  try{
    f.click('[data-open="source"]');
    f.click('#legend-cms-source-apply');
    await new Promise(resolve=>setTimeout(resolve,0));
    const warnings=[...f.w.document.querySelectorAll('[data-canonical-protection-warning]')].filter(node=>!node.hidden);
    assert.ok(warnings.length>=1);
    assert.ok(warnings.every(node=>node.textContent.includes('CANONICAL PROTECTION BLOCKED THIS EDIT')));
    assert.ok(warnings.every(node=>node.textContent.includes('GPT REDIRECT')));
    assert.ok(warnings.every(node=>node.textContent.includes(correction)));
    assert.equal(f.w.LEGEND_WEBSITE_STUDIO_PROTECTION_VIOLATION.correction,correction);
    assert.match(source,/\.legend-cms-protection-warning\{[^}]*color:#ff8f8f!important/);
    assert.match(websitePlatformControllerSource,/canonicalProtectionViolation = true[\s\S]*WebsiteStudioAgentContract\.ProtectedEditCorrection/);
  }finally{f.close();}
});

test('expired Founder or Agent editor authorization exposes only the authenticated portal recovery authority',async()=>{
  const authorization='https://portal.example.test/Account/EditProtectWebsite';
  const f=await domFixture({siteKey:'protect',denied:true,editorAuthorizationUrl:authorization});
  try{
    const link=f.w.document.querySelector('[data-legend-editor-reauthorize]');
    assert.ok(link);
    assert.equal(link.href,authorization);
    assert.equal(f.w.LEGEND_WEBSITE_EDITOR_REAUTHORIZE_URL,authorization);
    assert.equal(f.w.LEGEND_WEBSITE_STUDIO_MODE,true);
    assert.match(protectLayoutSource,/editorAuthorizationUrl:[\s\S]*Account\/EditProtectWebsite/);
    assert.match(businessBuildSource,/editorAuthorizationUrl:[^\n]*Account\/EditLegendWebsite/);
    assert.doesNotMatch(source,/new WebsiteEditorTicket|WebsiteEditorTicketProtector/);
  }finally{f.close();}
});

test('GPT contract is conversion-first on desktop and mobile and obeys the canonical red-warning redirect',()=>{
  for(const phrase of [
    'CONVERSION-FIRST EXPERIENCE',
    'first viewport as the highest-value impression',
    'one dominant primary action per decision moment',
    'Mobile must feel designed, not collapsed',
    'Never invent testimonials',
    'BROWSER AUTHORIZATION',
    'CANONICAL VIOLATION RESPONSE',
    'red canonical-protection warning',
    'ProtectedEditCorrection'
  ]) assert.ok(agentContractSource.includes(phrase),phrase);
  assert.ok(agentContractSource.includes('protected Signals'));
  assert.ok(agentContractSource.includes('signal_bearing_node_identity'));
});


test('mobile Website Studio is a compact top sheet that preserves visible canvas below it',()=>{
  assert.match(source,/@media\(max-width:800px\)[\s\S]*\.legend-cms-panel\{[\s\S]*height:auto;max-height:min\(46dvh,430px\)/);
  assert.match(source,/\.legend-cms-preview\{width:100%;max-width:100%;height:100dvh;overflow-y:auto/);
  assert.match(source,/\.legend-cms-bar button\{[\s\S]*min-height:32px[\s\S]*font-size:11px/);
  assert.match(source,/\.legend-cms-primary-tabs button\{[\s\S]*min-height:32px/);
  assert.match(source,/\.legend-cms-menu button,\.legend-cms-panel section>button\{[\s\S]*min-height:34px/);
});
