import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../../SHARED/WebsitePlatform/legend-public-cms.js', import.meta.url), 'utf8');
const publicCss = readFileSync(new URL('../../SHARED/WebsitePlatform/legend-public-web.css', import.meta.url), 'utf8');
const foundationCss = readFileSync(new URL('../../Legend-Design/legend-web-foundation.css', import.meta.url), 'utf8');
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
const websiteSystemTemplateAuthoritySource = readFileSync(new URL('../../Infrastructure/WebsiteEditing/WebsiteSystemTemplateAuthority.cs', import.meta.url), 'utf8');
const websiteContentSanitizerSource = readFileSync(new URL('../../Infrastructure/WebsiteEditing/WebsiteContentSanitizer.cs', import.meta.url), 'utf8');
const websiteSiteSourceSource = readFileSync(new URL('../../Infrastructure/WebsiteEditing/WebsiteSiteSource.cs', import.meta.url), 'utf8');
const websiteCreativeWorkspaceSource = readFileSync(new URL('../../Infrastructure/WebsiteEditing/WebsiteCreativeWorkspace.cs', import.meta.url), 'utf8');
const websiteMediaServiceSource = readFileSync(new URL('../../Infrastructure/WebsiteEditing/WebsiteMediaService.cs', import.meta.url), 'utf8');
const websiteImportServiceSource = readFileSync(new URL('../../Infrastructure/WebsiteEditing/WebsiteImportService.cs', import.meta.url), 'utf8');
const uploadValidationSource = readFileSync(new URL('../../Infrastructure/Security/UploadValidation/UploadValidation.cs', import.meta.url), 'utf8');

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
function sourceProjectionNode(node) {
  const copy=structuredClone(node || {});
  delete copy.signals;
  delete copy.fieldSignals;
  delete copy.systemKey;
  delete copy.systemBinding;
  if(copy.actionKey){ delete copy.href; delete copy.target; }
  if(node?.systemKey || node?.systemBinding || node?.type==='form' || (node?.signals || []).length) delete copy.dataBinding;
  copy.children=(node?.children || []).map(sourceProjectionNode);
  return copy;
}

function sourceProjectionDocument(doc) {
  return {
    schema:'legend-site-source/v1',
    version:3,
    faviconImageDataUrl:doc.faviconImageDataUrl ?? null,
    store:structuredClone(doc.store || {}),
    breakpoints:structuredClone(doc.breakpoints || []),
    theme:structuredClone(doc.theme || {}),
    shell:{
      header:(doc.shell?.header || []).map(sourceProjectionNode),
      footer:(doc.shell?.footer || []).map(sourceProjectionNode)
    },
    pages:Object.entries(doc.pages || {}).map(([path,page])=>({
      path,title:page.title ?? null,description:page.description ?? null,
      navigation:structuredClone(page.navigation || {}),
      dynamicBinding:structuredClone(page.dynamicBinding || null),
      composition:(page.composition || []).map(sourceProjectionNode)
    })),
    reusableComponents:Object.fromEntries(Object.entries(doc.reusableComponents || {}).map(([id,value])=>[
      id,{...structuredClone(value),composition:(value.composition || []).map(sourceProjectionNode)}
    ])),
    collections:structuredClone(doc.collections || {})
  };
}

function fixtureFindNodeLocation(document,id) {
  const visit=(nodes,scope,pagePath=null,reusableComponentId=null,parentId=null)=>{
    for(let index=0;index<(nodes || []).length;index++){
      const node=nodes[index];
      if(node.id===id) return {node,nodes,index,scope,pagePath,reusableComponentId,parentId};
      const child=visit(node.children,scope,pagePath,reusableComponentId,node.id);
      if(child) return child;
    }
    return null;
  };
  let found=visit(document.shell?.header || [],'shell.header'); if(found) return found;
  found=visit(document.shell?.footer || [],'shell.footer'); if(found) return found;
  for(const [pagePath,page] of Object.entries(document.pages || {})){
    found=visit(page.composition || [],'page',pagePath); if(found) return found;
  }
  for(const [reusableComponentId,component] of Object.entries(document.reusableComponents || {})){
    found=visit(component.composition || [],'component',null,reusableComponentId); if(found) return found;
  }
  return null;
}

function fixtureMutationChildren(document,operation) {
  if(operation.parentId){
    const parent=fixtureFindNodeLocation(document,operation.parentId);
    if(!parent) throw new Error('fixture parent not found: '+operation.parentId);
    parent.node.children ||= [];
    return parent.node.children;
  }
  if(operation.scope==='shell.header') return document.shell.header;
  if(operation.scope==='shell.footer') return document.shell.footer;
  if(operation.scope==='component') return document.reusableComponents[operation.reusableComponentId].composition;
  const path=operation.pagePath || '/';
  return document.pages[path].composition;
}

function fixtureApplyMutations(document,operations,ctaCatalog=[]) {
  for(const operation of operations || []){
    switch(operation.type){
      case 'setTheme': document.theme=structuredClone(operation.theme || {}); break;
      case 'setBreakpoints': document.breakpoints=structuredClone(operation.breakpoints || canonicalBreakpoints()); break;
      case 'setFavicon': document.faviconImageDataUrl=operation.faviconImageDataUrl ?? null; break;
      case 'setStorePresentation':
        document.store={...(document.store || {}),...structuredClone(operation.store || {})}; break;
      case 'createPage':
        document.pages[operation.pagePath]=structuredClone(operation.page || {navigation:{showInNavigation:true,order:0,isDeleted:false},composition:[]});
        document.pages[operation.pagePath].composition ||= [];
        break;
      case 'updatePage': {
        const page=document.pages[operation.pagePath];
        if(operation.page){
          page.title=operation.page.title ?? null;
          page.description=operation.page.description ?? null;
          page.navigation=structuredClone(operation.page.navigation || {});
          page.dynamicBinding=structuredClone(operation.page.dynamicBinding || null);
        }else{
          if(Object.hasOwn(operation,'title')) page.title=operation.title;
          if(Object.hasOwn(operation,'description')) page.description=operation.description;
          if(operation.navigation) page.navigation=structuredClone(operation.navigation);
        }
        break;
      }
      case 'movePageRoute':
        document.pages[operation.targetPath]=document.pages[operation.pagePath];
        delete document.pages[operation.pagePath];
        break;
      case 'removePage':
        if(document.pages[operation.pagePath]) {
          document.pages[operation.pagePath].navigation ||= {};
          document.pages[operation.pagePath].navigation.isDeleted=true;
        }
        break;
      case 'insertNode': {
        const children=fixtureMutationChildren(document,operation);
        children.splice(Math.max(0,Math.min(operation.index ?? children.length,children.length)),0,structuredClone(operation.node));
        break;
      }
      case 'replaceNode': {
        const found=fixtureFindNodeLocation(document,operation.nodeId);
        if(!found) throw new Error('fixture node not found: '+operation.nodeId);
        const protectedFields={
          systemKey:found.node.systemKey,systemBinding:found.node.systemBinding,
          signals:structuredClone(found.node.signals || []),
          fieldSignals:structuredClone(found.node.fieldSignals || {})
        };
        const replacement={...structuredClone(found.node),...structuredClone(operation.node),id:found.node.id};
        if(protectedFields.systemKey) replacement.systemKey=protectedFields.systemKey; else delete replacement.systemKey;
        if(protectedFields.systemBinding) replacement.systemBinding=protectedFields.systemBinding; else delete replacement.systemBinding;
        replacement.signals=protectedFields.signals;
        replacement.fieldSignals=protectedFields.fieldSignals;
        found.nodes[found.index]=replacement;
        break;
      }
      case 'removeNode': {
        const found=fixtureFindNodeLocation(document,operation.nodeId);
        if(found) found.nodes.splice(found.index,1);
        break;
      }
      case 'moveNode': {
        const found=fixtureFindNodeLocation(document,operation.nodeId);
        if(!found) break;
        const [node]=found.nodes.splice(found.index,1);
        const children=fixtureMutationChildren(document,operation);
        children.splice(Math.max(0,Math.min(operation.index ?? children.length,children.length)),0,node);
        break;
      }
      case 'setApprovedCapability': {
        const found=fixtureFindNodeLocation(document,operation.nodeId);
        if(!found) break;
        if(operation.capabilityKey==='experience.lead_capture'){
          found.node.experience ||= {};
          found.node.experience.submitCapability='lead_capture';
          break;
        }
        const key=String(operation.capabilityKey || '').replace(/^action\./,'');
        const action=ctaCatalog.find(value=>value.key===key);
        if(action){found.node.actionKey=action.key;found.node.href=action.href;found.node.target=action.openInNewTab?'_blank':'_self';}
        break;
      }
      case 'insertCapability': {
        const children=fixtureMutationChildren(document,operation);
        let node;
        if(operation.capabilityKey==='contact.inquiry.submit'){
          node={...(structuredClone(operation.node || {})),id:operation.instanceKey || 'form.fixture',type:'form',tag:'form',systemKey:'canonical_inquiry',
            text:operation.content?.submit || operation.node?.text || 'Send inquiry',
            title:operation.content?.title || operation.node?.title || 'Send an inquiry',
            signals:[],fieldSignals:{},children:structuredClone(operation.node?.children || [])};
        }else if(String(operation.capabilityKey || '').startsWith('action.')){
          const key=operation.capabilityKey.slice('action.'.length);
          const action=ctaCatalog.find(value=>value.key===key);
          node={id:operation.instanceKey || 'action.fixture',type:'cta',tag:'a',text:operation.content?.label || action?.defaultText || 'Continue',
            actionKey:key,href:action?.href || '#',target:action?.openInNewTab?'_blank':'_self',signals:[],children:[]};
        }else{
          const systemKey=String(operation.capabilityKey || '').replace(/^runtime\./,'');
          const found=[...Object.values(document.pages || {})].flatMap(page=>canonicalNodes({pages:{'/':page}},'/')).find(value=>value.systemKey===systemKey);
          if(found){ fixtureApplyMutations(document,[{type:'moveNode',nodeId:found.id,...operation}],ctaCatalog); }
          break;
        }
        children.splice(Math.max(0,Math.min(operation.index ?? children.length,children.length)),0,node);
        break;
      }
      case 'upsertReusable':
        document.reusableComponents[operation.reusableComponent.id]=structuredClone(operation.reusableComponent);
        break;
      case 'removeReusable':
        delete document.reusableComponents[operation.reusableComponentId];
        break;
      case 'insertRecipe': {
        const children=fixtureMutationChildren(document,operation);
        children.splice(Math.max(0,Math.min(operation.index ?? children.length,children.length)),0,
          canonicalNode(operation.instanceKey || 'recipe.fixture','section','section',{className:'legend-recipe-section',children:[]}));
        break;
      }
    }
  }
}

function fixtureMutationResponse(before,after,serverRevision) {
  const removedPages=Object.keys(before.pages || {}).filter(path=>!Object.hasOwn(after.pages || {},path));
  const removedComponents=Object.keys(before.reusableComponents || {}).filter(id=>!Object.hasOwn(after.reusableComponents || {},id));
  const changedScopes=[...new Set([
    ...Object.keys(after.pages || {}),...removedPages,'@theme','@breakpoints','@favicon','@shell/header','@shell/footer','@store-presentation',
    ...Object.keys(after.reusableComponents || {}).map(id=>'@component/'+id),
    ...removedComponents.map(id=>'@component/'+id)
  ])];
  return {
    source:'canonical_v3_mutation',revision:'r'+serverRevision,changedScopes,fingerprints:{},
    changes:{
      pages:structuredClone(after.pages || {}),removedPages,
      theme:structuredClone(after.theme || {}),breakpoints:structuredClone(after.breakpoints || []),
      faviconChanged:true,faviconImageDataUrl:after.faviconImageDataUrl ?? null,
      shellHeader:structuredClone(after.shell?.header || []),shellFooter:structuredClone(after.shell?.footer || []),
      reusableComponents:structuredClone(after.reusableComponents || {}),removedComponents,
      store:structuredClone(after.store || {})
    },drafts:[]
  };
}

async function domFixture({siteKey='legend',doc=canonicalDocument(),store=null,denied=false,search='?legendEdit=ticket',pathname='/',origin='https://site.example',apiBase='',business=null,pages=[],agentSlug='',pagePrefix='',editorAuthorizationUrl='',ctaCatalog=[],signalCatalog=null,agentContract=null,dataCatalog=null,dataCollections=null,qualityPayload=null,mediaPayload=null,mediaUploadPayload=null,mutationSequence=null,capabilities=null,legacyMigration=null,signalTestPayload=null,signalHealthPayload=null,collaborationPayload=null,commentPayload=null,viewportWidth=1024,html='<!doctype html><html><head><style>h1{font-size:64px}section{padding:24px}</style></head><body data-page-key="home"><main><section><h1>Template title</h1><a href="https://old.example"><span>Original link</span></a><img src="https://images.example/a.png" alt="original"></section><section><h2>Second section</h2></section></main></body></html>'}={}) {
  const dom = new JSDOM(html, {url:origin+pathname+search,runScripts:'outside-only'});
  const {window:w}=dom; const calls=[]; const animations=[];
  Object.defineProperty(w,'innerWidth',{value:viewportWidth,writable:true,configurable:true});
  w.matchMedia=()=>({matches:false});
  w.HTMLElement.prototype.animate=function(keyframes,options){ const record={element:this,keyframes,options,cancelled:false}; animations.push(record); return {cancel(){record.cancelled=true;}}; };
  w.LEGEND_PUBLIC_CMS_CONTEXT={siteKey,apiBase,businessId: business?.id || '',pages,agentSlug,pagePrefix,editorAuthorizationUrl};
  w.HTMLDialogElement.prototype.showModal = function() {}; w.HTMLDialogElement.prototype.close = function() { this.dispatchEvent(new w.Event('close')); };
  const alerts=[]; let mutationCall=0; let serverDoc=structuredClone(doc); let serverRevision=1;
  w.CSS={escape: v=>String(v).replaceAll('"','\\"')}; w.alert=value=>alerts.push(String(value)); w.confirm=()=>true;
  w.fetch=async(url,init={})=> {
    calls.push({url:String(url),...init});
    const parsed=new URL(String(url));
    const body=typeof init.body==='string'?JSON.parse(init.body):null;
    const method=init.method || 'GET';
    const reject=payload=>({ok:false,status:denied?401:400,json:async()=>payload});
    if(denied) return {ok:false,status:401,json:async()=>({error:'unauthorized'})};

    if(parsed.pathname.endsWith('/manage/agent/node') && method==='GET'){
      const id=parsed.searchParams.get('id');
      const found=fixtureFindNodeLocation(serverDoc,id);
      if(!found) return {ok:false,status:404,json:async()=>({error:'website_node_not_found'})};
      return {ok:true,status:200,json:async()=>({schema:'legend-node-source/v1',revision:'r'+serverRevision,fingerprint:'fp-'+serverRevision+'-'+id,location:{scope:found.scope,pagePath:found.pagePath,reusableComponentId:found.reusableComponentId,parentId:found.parentId,index:found.index},node:sourceProjectionNode(found.node)})};
    }
    if(parsed.pathname.endsWith('/manage/agent/summary') && method==='GET')
      return {ok:true,status:200,json:async()=>({schema:'legend-creative-workspace/v1',siteKey,revision:'r'+serverRevision,pages:Object.keys(serverDoc.pages || {}).map(path=>({path})),capabilities:{capabilities:[]}})};
    if(parsed.pathname.endsWith('/manage/agent/recipes') && method==='GET')
      return {ok:true,status:200,json:async()=>({schema:'legend-website-recipes/v1',recipes:[],artDirections:['roadster-precision'],capabilities:{capabilities:[]}})};
    if(parsed.pathname.endsWith('/manage/agent/contract') && method==='GET')
      return {ok:true,status:200,json:async()=>agentContract || {schema:'legend-website-studio-agent/v1',promptTemplate:'compact'}};
    if(parsed.pathname.endsWith('/manage/agent/design-quality') && method==='GET')
      return {ok:true,status:200,json:async()=>({
        schema:'legend-design-quality/v1',
        revision:qualityPayload?.revision ?? ('r'+serverRevision),
        structural:{checks:qualityPayload?.checks || []},
        design:{checks:qualityPayload?.designChecks || [],conversionPaths:qualityPayload?.conversionPaths || []}
      })};
    if(parsed.pathname.endsWith('/manage/data-catalog') && method==='GET')
      return {ok:true,status:200,json:async()=>({
        source:'website_business_data_catalog',
        dataCatalog:dataCatalog || [
          {key:'business_facts',label:'Business details',isList:false,fields:['contactEmail','phone','hours','locations','services']},
          {key:'commerce_products',label:'Products',isList:true,fields:['id','name','slug','description','priceLabel','primaryImageUrl','primaryImageAlt']}
        ],
        collections:dataCollections || []
      })};
    if(parsed.pathname.endsWith('/manage/signal-catalog') && method==='GET')
      return {ok:true,status:200,json:async()=>signalCatalog || {events:[],matchingFields:[],runtimeEnabled:true}};
    if(parsed.pathname.endsWith('/manage/quality'))
      return {ok:true,status:200,json:async()=>qualityPayload || {source:'saved_draft_server',revision:serverRevision,errorCount:0,warningCount:0,checks:[]}};
    if(parsed.pathname.endsWith('/manage/media') && method==='GET')
      return {ok:true,status:200,json:async()=>mediaPayload || {assets:[]}};
    if(parsed.pathname.endsWith('/manage/media') && method==='POST'){
      const file=init.body?.get?.('file'); const id='33333333-3333-3333-3333-333333333333';
      return {ok:true,status:200,json:async()=>mediaUploadPayload || {id,name:file?.name || 'upload',url:'https://site.example/api/website-content/media/'+id,contentType:file?.type || 'image/png',sizeBytes:file?.size || 1024,createdUtc:'2026-09-28T00:00:00Z'}};
    }
    if(parsed.pathname.endsWith('/manage/mutations') && method==='POST'){
      const step=Array.isArray(mutationSequence) && mutationSequence.length ? mutationSequence[Math.min(mutationCall++,mutationSequence.length-1)] : null;
      if(step?.status && (step.status<200 || step.status>=300))
        return {ok:false,status:step.status,json:async()=>step.payload || {error:'scope_revision_conflict',revision:'r'+serverRevision}};
      const before=structuredClone(serverDoc);
      fixtureApplyMutations(serverDoc,body?.operations || [],ctaCatalog);
      serverRevision++;
      return {ok:true,status:200,json:async()=>step?.payload || fixtureMutationResponse(before,serverDoc,serverRevision)};
    }
    if(parsed.pathname.endsWith('/manage/design-plan') && method==='POST'){
      serverRevision++;
      return {ok:true,status:200,json:async()=>fixtureMutationResponse(serverDoc,serverDoc,serverRevision)};
    }
    if(parsed.pathname.endsWith('/manage/source') && method==='GET'){
      const projected=sourceProjectionDocument(serverDoc);
      return {ok:true,status:200,json:async()=>({source:'legend_site_source',revision:'r'+serverRevision,requiresMaterialization:false,schema:'legend-site-source/v1',text:JSON.stringify(projected,null,2),sourceMap:{}})};
    }
    if(parsed.pathname.endsWith('/manage/signals') && method==='POST'){
      const target=canonicalNodeById(serverDoc,body?.elementId,body?.pagePath || '/');
      if(!target) return {ok:false,status:404,json:async()=>({error:'website_signal_target_not_found'})};
      const signals=structuredClone(body?.signals || []); const fieldKey=body?.fieldKey ? String(body.fieldKey).toLowerCase() : null;
      if(fieldKey){ target.fieldSignals ||= {}; if(signals.length) target.fieldSignals[fieldKey]=signals; else delete target.fieldSignals[fieldKey]; }
      else target.signals=signals;
      serverRevision++;
      return {ok:true,status:200,json:async()=>({source:'website_signal_configuration',revision:'r'+serverRevision,document:structuredClone(serverDoc),elementId:body.elementId,fieldKey,signals})};
    }
    if(parsed.pathname.endsWith('/manage/signals/test'))
      return {ok:true,status:200,json:async()=>signalTestPayload || {source:'website_signal_private_dry_run',dryRun:true,persisted:false,metaDispatched:false,stages:{mappingValidated:true,browserTriggerSupported:true,browserAnalyticsWouldBeAccepted:true,browserPixelWouldInvoke:false,serverOutcomeRequired:false},destination:{ownerType:'business',browserPixelConfigured:false,serverCapiConfigured:false}}};
    if(parsed.pathname.endsWith('/manage/signals/health'))
      return {ok:true,status:200,json:async()=>signalHealthPayload || {source:'website_signal_existing_authorities',publishedVersionId:null,binding:{matchingConsent:'not_requested'},destination:{ownerType:'business',browserPixelConfigured:false,serverCapiConfigured:false},analytics:[],meta:[]}};
    if(parsed.pathname.endsWith('/manage/collaboration') && method==='GET')
      return {ok:true,status:200,json:async()=>collaborationPayload || {source:'website_studio_collaboration',revision:'r'+serverRevision,role:{roleKey:'founder',label:'Founder',canPublish:true},collaborators:[{roleKey:'founder',displayName:'Founder',canPublish:true}],comments:[]}};
    if(parsed.pathname.endsWith('/manage/collaboration/comments') || parsed.pathname.endsWith('/manage/collaboration/comments/status'))
      return {ok:true,status:200,json:async()=>commentPayload || {source:'website_studio_collaboration',comment:{id:'comment-1',status:'open'}}};
    if(parsed.pathname.endsWith('/manage/publish') && method==='POST'){
      serverRevision++;
      return {ok:true,status:200,json:async()=>({revision:'r'+serverRevision,publishedRevision:serverRevision,document:structuredClone(serverDoc)})};
    }
    if(parsed.pathname.endsWith('/manage') && method==='POST' && body?.document){
      serverDoc=structuredClone(body.document); serverRevision++;
      return {ok:true,status:200,json:async()=>({siteKey,business,store,revision:'r'+serverRevision,document:structuredClone(serverDoc),legacyMigration:legacyMigration || undefined,ctaCatalog:{options:ctaCatalog},signalCatalog:signalCatalog || undefined,agentContract:agentContract || undefined,capabilities:capabilities || undefined,drafts:[]})};
    }
    return {ok:true,status:200,json:async()=>({siteKey,business,store,revision:'r'+serverRevision,document:structuredClone(serverDoc),legacyMigration:legacyMigration || undefined,ctaCatalog:{options:ctaCatalog},signalCatalog:signalCatalog || undefined,agentContract:agentContract || undefined,capabilities:capabilities || undefined,drafts:[]})};
  };
  w.eval(source);
  // JSDOM dispatches initial readiness itself; wait for the fetch continuation.
  await new Promise(resolve=>setTimeout(resolve,0));
  const click=selector=>w.document.querySelector(selector).dispatchEvent(new w.MouseEvent('click',{bubbles:true,cancelable:true}));
  const input=(selector,value)=> {const el=w.document.querySelector(selector);el.value=value;el.dispatchEvent(new w.Event('input',{bubbles:true}));};
  const change=(selector,value)=> {const el=w.document.querySelector(selector);el.value=value;el.dispatchEvent(new w.Event('change',{bubbles:true}));};
  const editSelected=(value)=> {const el=w.document.querySelector('.legend-cms-selected');assert.ok(el);const edit=w.document.querySelector('#legend-cms-edit-text');if(edit&&!edit.hidden)edit.click();el.textContent=value;el.dispatchEvent(new w.Event('beforeinput',{bubbles:true,cancelable:true}));el.dispatchEvent(new w.Event('input',{bubbles:true}));};
  const save=async()=>{click('#legend-cms-save');input('#legend-cms-draft-name','Test variation');click('#legend-cms-draft-submit');await new Promise(resolve=>setTimeout(resolve,0));await new Promise(resolve=>setTimeout(resolve,0));return structuredClone(serverDoc);};
  return {w,calls,animations,alerts,click,input,change,editSelected,save,serverDocument:()=>structuredClone(serverDoc),close:()=>w.close()};
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
    assert.match(publicCss,/h1,h2,h3,p\{overflow-wrap:break-word;word-break:normal\}/);
    assert.match(publicCss,/h1,h2,h3\{[^}]*min-inline-size:min\(8ch,100%\)/);
    assert.match(source,/\[data-cms-editable="true"\]\{min-width:0;max-width:100%;box-sizing:border-box;overflow-wrap:anywhere\}/);
  }finally{f.close();}
});

test('canonical startup repairs dead links while preserving desktop brand geometry and clearing unsafe mobile shell geometry',async()=>{
  const doc=canonicalBusinessNavigation(canonicalDocument({href:'#'}));
  const dead=canonicalNodeById(doc,'home.a.node.1');
  dead.href='#';
  doc.pages['/'].composition[0].children.push(
    canonicalNode('home.dynamic-link','link','a',{text:'Dynamic destination',href:null,dataBinding:{collectionId:'items',field:'url',target:'href'}})
  );
  doc.shell.header[0].children.unshift(
    canonicalNode('shell.brand','container','div',{className:'brand-wordmark',style:{widthPercent:4,offsetXPercent:91},breakpointStyles:{mobile:{widthPercent:3,offsetXPercent:95}},children:[
      canonicalNode('shell.brand.copy','text','span',{text:'Canonical Business'})
    ]})
  );
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Canonical Business'},doc});
  try{
    const saved=await f.save();
    const repaired=canonicalNodeById(saved,'home.a.node.1');
    assert.equal(repaired.type,'text');
    assert.equal(repaired.tag,'span');
    assert.equal(repaired.href ?? null,null);
    const dynamic=canonicalNodeById(saved,'home.dynamic-link');
    assert.equal(dynamic.type,'link');
    assert.equal(dynamic.dataBinding.target,'href');
    const brand=saved.shell.header[0].children.find(node=>node.id==='shell.brand');
    assert.equal(brand.style.widthPercent,4);
    assert.equal(brand.style.offsetXPercent,91);
    assert.equal(brand.breakpointStyles.mobile ?? null,null);
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

test('materialization never invents publishable links from unmanaged runtime buttons or placeholder anchors',()=>{
  assert.match(source,/const unmanagedInteractiveControl\s*=\s*[\s\S]*tag === 'button' && !actionKey[\s\S]*tag === 'a' && !actionKey && !dynamicHref && \(!rawHref \|\| rawHref === '#'\)/);
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
    assert.equal(f.w.document.querySelector('[data-cms-view="content"]').hidden,false);
    assert.equal(f.w.document.querySelector('[data-open="content"]').getAttribute('aria-pressed'),'true');
    f.editSelected('Canonical canvas edit');
    f.click('[data-open="source"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.equal(f.w.document.querySelector('#legend-cms-source-scope').value,'selection');
    assert.equal(f.w.document.querySelector('#legend-cms-site-source').readOnly,false);
    let parsed=JSON.parse(f.w.document.querySelector('#legend-cms-site-source').value);
    assert.equal(parsed.id,'home.hero.title');
    assert.equal(parsed.text,'Canonical canvas edit');
    f.input('#legend-cms-width','55');
    parsed=JSON.parse(f.w.document.querySelector('#legend-cms-site-source').value);
    assert.equal(parsed.style.widthPercent,55);
    await new Promise(resolve=>setTimeout(resolve,0));
    const saved=await f.save();
    assert.equal(saved.pages['/'].composition[0].children[0].style.widthPercent,55);
    assert.equal(Object.hasOwn(saved.pages['/'],'elements'),false);
    assert.equal(Object.hasOwn(saved.pages['/'],'extras'),false);
    assert.equal(Object.hasOwn(saved.pages['/'],'sectionOrder'),false);
  }finally{f.close();}
});

test('Master Source stays color guided while Selected Source is a normal editable code surface',async()=>{
  const doc=canonicalDocument();
  const heading=canonicalNodeById(doc,'home.h1.node.1');
  heading.style={fontFamily:'Georgia',widthPercent:64,color:'#123456'};
  const f=await domFixture({doc});
  try{
    f.click('[data-open="source"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    const scope=f.w.document.querySelector('#legend-cms-source-scope');
    const sourceInput=f.w.document.querySelector('#legend-cms-site-source');
    const apply=f.w.document.querySelector('#legend-cms-source-apply');
    assert.equal(scope.value,'site');
    assert.equal(sourceInput.readOnly,true);
    assert.equal(apply.hidden,true);
    assert.match(f.w.document.querySelector('#legend-cms-source-status').textContent,/read only/i);
    f.click('#legend-cms-source-apply');
    assert.equal(websitePlatformControllerSource.includes('manage/source/validate'),false);
    assert.equal(f.calls.some(call=>call.url.endsWith('/manage/source/validate')),false);

    f.click('main h1');
    f.click('[data-open="source"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.equal(scope.value,'selection');
    assert.equal(sourceInput.readOnly,false);
    assert.equal(apply.hidden,false);
    const tones=[...f.w.document.querySelectorAll('#legend-cms-source-highlight [data-tone]')].map(node=>node.dataset.tone);
    for(const tone of ['content','style','color','size']) assert.ok(tones.includes(tone),tone);
    for(const tone of ['content','style','color','size','layout','media','behavior','structure','protected'])
      assert.ok(f.w.document.querySelector(`.legend-cms-source-key [data-tone="${tone}"]`));
    assert.match(source,/\.legend-cms-source-content\{color:#78e2a7\}/);
    assert.match(source,/\.legend-cms-source-style\{color:#7fb5ff\}/);
    assert.match(source,/\.legend-cms-source-color\{color:#ff8fa8\}/);
    assert.match(source,/\.legend-cms-source-size\{color:#ffb86b\}/);
    assert.match(source,/\.legend-cms-source-structure\{color:#9bd67d\}/);
  }finally{f.close();}
});

test('Selected Source supports modern typing, indentation, wrapping, and visible caret text',async()=>{
  const f=await domFixture();
  try{
    f.click('main h1');
    f.click('[data-open="source"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    const input=f.w.document.querySelector('#legend-cms-site-source');
    assert.equal(input.readOnly,false);
    assert.equal(input.getAttribute('spellcheck'),'false');
    assert.equal(input.getAttribute('autocapitalize'),'off');
    assert.equal(input.getAttribute('autocomplete'),'off');
    assert.equal(input.getAttribute('autocorrect'),'off');
    assert.equal(input.getAttribute('wrap'),'soft');

    input.value='{"style": {\n"fontWeight": 800\n}}';
    input.setSelectionRange(12,12);
    input.dispatchEvent(new f.w.KeyboardEvent('keydown',{key:'Tab',bubbles:true,cancelable:true}));
    assert.match(input.value,/\n  "fontWeight"/);

    const open=input.value.indexOf('{',1)+1;
    input.setSelectionRange(open,open);
    input.dispatchEvent(new f.w.KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true}));
    assert.ok(input.value.includes('\n  '));
    assert.equal(f.w.document.querySelector('#legend-cms-source-highlight').hidden,false);
  }finally{f.close();}
});

test('Selected Source CSS uses the textarea as the visible editable surface and hides the overlay only while editing',()=>{
  assert.match(source,/\.legend-cms-site-source\{[^}]*background:#07162b!important[^}]*color:#dce6f4!important[^}]*-webkit-text-fill-color:currentColor/);
  assert.match(source,/\.legend-cms-site-source\[readonly\]\{[^}]*color:transparent!important[^}]*-webkit-text-fill-color:transparent/);
  assert.match(source,/:has\(\.legend-cms-site-source:not\(\[readonly\]\)\) \.legend-cms-source-highlight\{display:none\}/);
  assert.match(source,/white-space:pre-wrap/);
  assert.match(source,/sourceTextarea\?\.addEventListener\('keydown',handleSourceEditorKeydown\)/);
});

test('v3 Selected Source applies one-node mutations without whole-site validation or save',async()=>{
  const original={version:3,pages:{'/':{title:'Home',description:'',navigation:{label:'Home',showInNavigation:true,order:0,isDeleted:false},composition:[
    {id:'hero',type:'section',tag:'section',signals:[],style:{},breakpointStyles:{},layout:{mode:'stack',direction:'column'},breakpointLayouts:{},animations:[],children:[
      {id:'hero.title',type:'heading',tag:'h1',text:'Before',signals:[],style:{},breakpointStyles:{},layout:{mode:'free',direction:'column'},breakpointLayouts:{},animations:[],children:[]}
    ]}
  ]}},breakpoints:canonicalBreakpoints(),theme:{},reusableComponents:{},collections:{},shell:{header:[],footer:[]},store:{enabled:false,navigationLabel:'Store',cartIcon:'cart',cartIconSizePx:28}};
  const f=await domFixture({doc:original});
  try{
    f.click('main h1'); f.click('[data-open="source"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    const sourceInput=f.w.document.querySelector('#legend-cms-site-source');
    const selected=JSON.parse(sourceInput.value); selected.text='After'; selected.style.fontWeight=700;
    sourceInput.value=JSON.stringify(selected,null,2);
    sourceInput.dispatchEvent(new f.w.Event('input',{bubbles:true}));
    f.click('#legend-cms-source-apply');
    await new Promise(resolve=>setTimeout(resolve,0));
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.equal(f.w.document.querySelector('main h1').textContent,'After');
    assert.equal(f.w.document.querySelector('main h1').style.fontWeight,'700');
    const nodeRead=f.calls.find(call=>call.url.includes('/manage/agent/node'));
    assert.ok(nodeRead);
    const mutation=f.calls.find(call=>call.method==='POST' && call.url.endsWith('/manage/mutations'));
    assert.ok(mutation);
    const operation=JSON.parse(mutation.body).operations[0];
    assert.equal(operation.type,'replaceNode');
    assert.equal(operation.nodeId,'hero.title');
    assert.equal(operation.node.text,'After');
    assert.equal(Object.hasOwn(operation.node,'signals'),false);
    assert.equal(Object.hasOwn(operation.node,'fieldSignals'),false);
    assert.equal(Object.hasOwn(operation.node,'systemKey'),false);
    assert.equal(f.calls.some(call=>call.url.endsWith('/manage/source/validate')),false);
    assert.equal(f.calls.some(call=>call.method==='POST' && call.url.endsWith('/manage') && JSON.parse(call.body || '{}').document),false);
    assert.equal(canonicalNodeById(f.serverDocument(),'hero.title').text,'After');
  }finally{f.close();}
});

test('Selected Source reports exact scope conflicts without whole-site revalidation loops',async()=>{
  const original=canonicalDocument();
  const f=await domFixture({
    doc:original,
    mutationSequence:[{status:409,payload:{error:'scope_revision_conflict',revision:'r2',kind:'node',targetId:'home.h1.node.1',fingerprint:'new-fingerprint'}}]
  });
  try{
    f.click('main h1'); f.click('[data-open="source"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    const sourceInput=f.w.document.querySelector('#legend-cms-site-source');
    const selected=JSON.parse(sourceInput.value);
    selected.style.fontWeight=700;
    sourceInput.value=JSON.stringify(selected,null,2);
    sourceInput.dispatchEvent(new f.w.Event('input',{bubbles:true}));
    f.click('#legend-cms-source-apply');
    await new Promise(resolve=>setTimeout(resolve,0));
    await new Promise(resolve=>setTimeout(resolve,0));
    const mutations=f.calls.filter(call=>call.method==='POST' && call.url.endsWith('/manage/mutations'));
    assert.equal(mutations.length,1);
    assert.equal(f.calls.some(call=>call.url.endsWith('/manage/source/validate')),false);
    assert.equal(f.w.LEGEND_WEBSITE_STUDIO_PROTECTION_VIOLATION,undefined);
    assert.match(f.w.document.querySelector('#legend-cms-source-status').textContent,/changed elsewhere|refresh/i);
    assert.equal(JSON.parse(sourceInput.value).style.fontWeight,700);
  }finally{f.close();}
});

test('Source authoring reads the server canonical projection and has no browser-owned site projection',()=>{
  assert.match(source,/api\/website-content\/manage\/source/);
  assert.match(source,/async function loadCanonicalSourceSnapshot\(/);
  assert.doesNotMatch(source,/function siteSourceProjection\(/);
  assert.doesNotMatch(source,/function sourceProjectionNode\(/);
});

test('managed CTA instances are authorable while system and signal authority stay locked',()=>{
  assert.match(source,/const lockedManaged = !!key && !!byKey && \(!!model\?\.systemKey/);
  assert.match(source,/Managed action: choose any approved catalog action for this CTA instance/);
  assert.doesNotMatch(source,/if \(ov\.actionKey\) \{ syncEditorControls\(\); return; \}/);
  assert.match(source,/const protectedSemantic = !!selected\.dataset\.cmsSignalOnly \|\| !!ov\.systemKey \|\| !!ov\.systemBinding/);
});

test('protected form fields expose typed presentation without exposing execution',()=>{
  assert.match(source,/function formFieldPresentationForElement\(/);
  assert.match(source,/function applyFormFieldPresentations\(/);
  assert.match(source,/fieldPresentations/);
  assert.match(editorContractsSource,/FieldPresentations/);
  assert.match(editorContractsSource,/FieldLabels/);
  assert.match(source,/sandbox','allow-scripts'/);
  assert.doesNotMatch(source,/allow-forms/);
  assert.match(source,/form-action 'none'/);
  assert.match(source,/connect-src 'none'/);
});

test('Analytics mappings use a dedicated canonical mutation path and generic save never owns signals',()=>{
  assert.match(source,/api\/website-content\/manage\/signals/);
  assert.match(source,/async function persistSelectedSignals\(/);
  assert.match(source,/fieldKey:context\.fieldKey/);
  assert.doesNotMatch(source,/binding\.deliveryMode = value; markDirty\(\)/);
  assert.match(websitePlatformControllerSource,/\[HttpPost\("manage\/signals"\)\]/);
  assert.match(websitePlatformControllerSource,/WebsiteSignalBindingPolicy\.Validate\(request\.Signals\)/);
  assert.match(editorContractsSource,/FieldSignals/);
});

test('normal Canvas and Selected Source converge on the canonical mutation authority',()=>{
  assert.match(source,/manage\/mutations/);
  assert.match(source,/buildCreativeMutationOperations\(persistedDocumentState,submittedState\)/);
  assert.match(websitePlatformControllerSource,/\[HttpPost\("manage\/mutations"\)\]/);
  assert.match(websitePlatformControllerSource,/WebsiteDocumentMutationService\.Apply\(/);
  assert.match(websitePlatformControllerSource,/ValidateNewCompositionMediaOwnershipAsync\(/);
  assert.match(websitePlatformControllerSource,/website_mutation_protected/);
  assert.match(source,/materializationSavePending \|\| !persistedDocumentState/);
});

test('bulk imports are ingestion only and pass through the canonical protected authority',()=>{
  assert.equal((websitePlatformControllerSource.match(/ReconcileImportedDocumentAsync\(actor, baseline, result\.Document/g)||[]).length,2);
  assert.match(websitePlatformControllerSource,/private async Task<WebsiteContentDocument> ReconcileImportedDocumentAsync/);
  assert.match(websitePlatformControllerSource,/WebsiteSiteSource\.Serialize\([\s\S]*?WebsiteContentSanitizer\.Sanitize\(imported\)\)/);
  assert.match(websitePlatformControllerSource,/WebsiteSiteSource\.Parse\([\s\S]*?baseline,[\s\S]*?actions\)\.Document/);
  assert.match(websitePlatformControllerSource,/WebsiteSiteSource\.ValidateCanonical\(reconciled, actions\)/);
  assert.match(websitePlatformControllerSource,/ValidateCompositionMediaOwnershipAsync\(actor, reconciled/);
  assert.match(websitePlatformControllerSource,/website_import_protected/);
});

test('named draft restore is a governed restore, never a canonical v3 snapshot writer',()=>{
  assert.match(websitePlatformControllerSource,/restored = Read\(draft\.DocumentJson\)/);
  assert.match(websitePlatformControllerSource,/if \(restored\.LegacyMigration is null\)[\s\S]*WebsiteSiteSource\.Serialize\(restored\)[\s\S]*WebsiteSiteSource\.Parse\(source, baseline, actions\)\.Document/);
  assert.match(websitePlatformControllerSource,/WebsiteSystemTemplateAuthority\.Apply\(actor\.SiteKey, restored\)/);
  assert.match(websitePlatformControllerSource,/WebsiteSiteSource\.ValidateCanonical\(restored, actions\)/);
  assert.match(websitePlatformControllerSource,/ValidateCompositionMediaOwnershipAsync\(actor, restored/);
  assert.match(websitePlatformControllerSource,/Historical pre-v3 named drafts remain eligible only for the[\s\S]*existing explicit one-way materialization boundary/);
});

test('creative mutations use one per-batch indexed mutation path',()=>{
  assert.match(websiteCreativeWorkspaceSource,/var index = new WebsiteMutationIndex\(document\)/);
  assert.match(websiteCreativeWorkspaceSource,/index\.Insert\(/);
  assert.match(websiteCreativeWorkspaceSource,/index\.Replace\(/);
  assert.match(websiteCreativeWorkspaceSource,/index\.Remove\(/);
  assert.doesNotMatch(websiteCreativeWorkspaceSource,/WebsiteDocumentIndex\.ResolveChildren/);
  assert.doesNotMatch(websiteCreativeWorkspaceSource,/WebsiteDocumentIndex\.Remove\(/);
  assert.doesNotMatch(websiteCreativeWorkspaceSource,/public static List<WebsiteCompositionNode> ResolveChildren\(/);
});



test('source protection UI separates protected authority failures from exact scope conflicts',()=>{
  assert.match(source,/payload\.error==='scope_revision_conflict'/);
  assert.match(source,/sourceEditorBaseFingerprint/);
  assert.match(source,/type:'replaceNode'[\s\S]*expectedFingerprint:sourceEditorBaseFingerprint/);
  assert.match(source,/if\(payload\.canonicalProtectionViolation===true\)[\s\S]*showCanonicalProtectionViolation/);
  assert.match(websitePlatformControllerSource,/catch \(WebsiteMutationConflictException ex\)[\s\S]*scope_revision_conflict/);
  assert.match(websitePlatformControllerSource,/catch \(WebsiteSiteSourceProtectionException ex\)[\s\S]*canonicalProtectionViolation = true/);
});

test('canonical header defaults are server-owned and the browser has no duplicate shell writer',()=>{
  assert.match(websiteSystemTemplateAuthoritySource,/ApplyHeaderTypographyDefaults/);
  assert.match(websiteSystemTemplateAuthoritySource,/FontScale = 3\.5m/);
  assert.match(websiteSystemTemplateAuthoritySource,/FontScale = 1\.6m/);
  assert.match(websiteSystemTemplateAuthoritySource,/FontScale = 1\.35m/);
  assert.match(websiteSystemTemplateAuthoritySource,/FontScale = 1\.15m/);
  assert.doesNotMatch(source,/applyCanonicalHeaderTypographyDefaults/);
  assert.doesNotMatch(source,/canonicalizeMobileHeaderChrome/);
  assert.match(publicCss,/\.brand-wordmark strong\{[^}]*font-weight:800/);
  assert.match(publicCss,/\.nav\{[^}]*font-weight:800/);
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


test('canonical v3 route keys are not silently repaired by the browser',()=>{
  const normalizeStart=source.indexOf('function normalizeDocument(');
  const normalizeEnd=source.indexOf('function normalizePageRoute(',normalizeStart);
  const normalizeSource=source.slice(normalizeStart,normalizeEnd);
  assert.doesNotMatch(normalizeSource,/canonicalSiteRoute\(rawRoute\)/);
  assert.doesNotMatch(normalizeSource,/agent-prefixed v3 page keys/);
  assert.match(source,/function legacyPageForRoute\([\s\S]*?canonicalSiteRoute\(rawPath\)/);
  assert.match(source,/async function materializeCanonicalSite\(/);
});

test('runtime chrome is rejected by server authority instead of silently deleted by the browser',()=>{
  assert.doesNotMatch(source,/isRuntimeShellChromeNode/);
  assert.match(websiteSiteSourceSource,/"nav-toggle"/);
  assert.match(websiteSiteSourceSource,/cannot invent platform runtime class/);
  assert.match(websiteSiteSourceSource,/cannot remove platform runtime class/);
  assert.match(websiteContentSanitizerSource,/website_v3_noncanonical_persisted_document/);
  assert.match(source,/function ensurePublicNavigationToggle\(/);
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

test('Protect hides only canonical published-document pages before hydration',()=>{
  assert.match(protectLayoutSource,/hidden="@\(!isStandaloneQuoteLanding \? "hidden" : null\)"/);
  assert.match(protectLayoutSource,/data-legend-canonical-pending="@\(!isStandaloneQuoteLanding \? "true" : null\)"/);
  assert.match(protectLayoutSource,/@if \(!isStandaloneQuoteLanding\)[\s\S]*html\[hidden\]\{display:block!important\}/);
});

test('canonical responsive hierarchy resets inherited desktop geometry on mobile but preserves explicit mobile intent',async()=>{
  const doc=canonicalDocument();
  const section=canonicalNodeById(doc,'home.section.1');
  section.layout={mode:'grid',columns:4,gapPx:32,direction:'row'};
  const heading=canonicalNodeById(doc,'home.h1.node.1');
  heading.style={widthPercent:38,offsetXPercent:48,offsetYPx:80,heightPx:120,fontSize:70};
  const action=canonicalNodeById(doc,'home.a.node.1');
  action.type='cta';
  action.style={widthPercent:18,offsetXPercent:62,heightPx:88,fontSize:34};
  const media=canonicalNodeById(doc,'home.img.node.1');
  media.style={widthPercent:44,offsetXPercent:48,heightPx:900};
  const f=await domFixture({doc,viewportWidth:390});
  try{
    f.change('#legend-cms-breakpoint','mobile');
    await new Promise(resolve=>setTimeout(resolve,0));
    const h=f.w.document.querySelector('main h1');
    const a=f.w.document.querySelector('main a');
    const img=f.w.document.querySelector('main img');
    const root=f.w.document.querySelector('main section');
    assert.equal(root.style.display,'grid');
    assert.equal(root.style.gridTemplateColumns,'repeat(1,minmax(0,1fr))');
    assert.equal(h.style.width,'100%');
    assert.equal(h.style.left,'0%');
    assert.equal(h.style.top,'0px');
    assert.equal(h.style.height,'');
    assert.equal(h.style.fontSize,'54px');
    assert.equal(a.style.width,'100%');
    assert.equal(a.style.left,'0%');
    assert.equal(a.style.height,'');
    assert.equal(a.style.fontSize,'20px');
    assert.equal(a.dataset.legendContentRole,'action');
    assert.equal(img.style.width,'100%');
    assert.equal(img.style.left,'0%');
    assert.equal(img.style.height,'');
    assert.equal(img.style.maxWidth,'560px');
    assert.equal(root.dataset.legendResponsiveFlow,'true');
  }finally{f.close();}
});

test('explicit mobile styling renders independently from desktop base geometry',async()=>{
  const doc=canonicalDocument();
  const action=canonicalNodeById(doc,'home.a.node.1');
  action.type='cta';
  action.style={widthPercent:18,offsetXPercent:62,heightPx:88,fontSize:34};
  action.breakpointStyles.mobile={widthPercent:72,offsetXPercent:8,fontSize:18,heightPx:64};
  const section=canonicalNodeById(doc,'home.section.1');
  section.layout={mode:'grid',columns:4,gapPx:32};
  section.breakpointLayouts.mobile={mode:'grid',columns:2,gapPx:14};
  const f=await domFixture({doc,viewportWidth:390});
  try{
    f.change('#legend-cms-breakpoint','mobile');
    await new Promise(resolve=>setTimeout(resolve,0));
    const a=f.w.document.querySelector('main a');
    const root=f.w.document.querySelector('main section');
    assert.equal(a.style.width,'72%');
    assert.equal(a.style.left,'8%');
    assert.equal(a.style.top,'0px');
    assert.equal(a.style.fontSize,'18px');
    assert.equal(a.style.height,'64px');
    assert.equal(root.style.gridTemplateColumns,'repeat(2,minmax(0,1fr))');
    assert.equal(root.style.gap,'14px');
  }finally{f.close();}
});

test('published mobile presentation preserves explicit free-canvas geometry without mutating desktop base',async()=>{
  const doc=canonicalDocument();
  const section=canonicalNodeById(doc,'home.section.1');
  section.layout={mode:'free',gapPx:8};
  section.breakpointStyles.mobile={heightPx:980,offsetYPx:-100,marginTop:-50,minHeightPx:900,maxHeightPx:1200};
  section.breakpointLayouts.mobile={mode:'free',gapPx:16};
  const heading=canonicalNodeById(doc,'home.h1.node.1');
  heading.style={widthPercent:70,offsetXPercent:4,fontSize:64};
  heading.breakpointStyles.mobile={widthPercent:36,offsetXPercent:58,offsetYPx:-180,heightPx:150,fontSize:88};
  const action=canonicalNodeById(doc,'home.a.node.1');
  action.type='cta';
  action.breakpointStyles.mobile={widthPercent:24,offsetXPercent:5,offsetYPx:-260,heightPx:150,fontSize:28,borderRadius:999};
  const media=canonicalNodeById(doc,'home.img.node.1');
  media.breakpointStyles.mobile={widthPercent:92,offsetXPercent:12,offsetYPx:-420,heightPx:760,maxWidthPx:900};
  const f=await domFixture({doc,viewportWidth:390});
  try{
    f.change('#legend-cms-breakpoint','mobile');
    await new Promise(resolve=>setTimeout(resolve,0));
    const root=f.w.document.querySelector('main section');
    const h=f.w.document.querySelector('main h1');
    const a=f.w.document.querySelector('main a');
    const img=f.w.document.querySelector('main img');
    assert.notEqual(root.style.display,'flex');
    assert.equal(root.style.height,'980px');
    assert.equal(root.style.marginTop,'-50px');
    assert.equal(root.style.minHeight,'900px');
    assert.equal(root.style.maxHeight,'1200px');
    assert.equal(h.style.left,'58%');
    assert.equal(h.style.top,'-180px');
    assert.equal(h.style.width,'36%');
    assert.equal(h.style.height,'150px');
    assert.equal(h.style.fontSize,'88px');
    assert.equal(a.style.left,'5%');
    assert.equal(a.style.top,'-260px');
    assert.equal(a.style.width,'24%');
    assert.equal(a.style.height,'150px');
    assert.equal(a.style.fontSize,'28px');
    assert.equal(img.style.left,'12%');
    assert.equal(img.style.top,'-420px');
    assert.equal(img.style.width,'88%');
    assert.equal(img.style.height,'760px');
    assert.equal(img.style.maxWidth,'900px');

    f.click('[data-editor-viewport="base"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.equal(h.style.width,'70%');
    assert.equal(h.style.left,'4%');
    assert.equal(h.style.fontSize,'64px');
  }finally{f.close();}
});

test('Desktop and Mobile viewport buttons write to separate presentation layers',async()=>{
  const doc=canonicalDocument();
  const heading=canonicalNodeById(doc,'home.h1.node.1');
  heading.style={widthPercent:80,textAlign:'left',backgroundColor:'#111111'};
  heading.breakpointStyles.mobile={widthPercent:64,textAlign:'center',backgroundColor:'#222222'};
  const f=await domFixture({doc,viewportWidth:1024});
  try{
    f.click('main h1');
    f.click('[data-editor-viewport="mobile"]');
    f.input('#legend-cms-width','44');
    f.input('#legend-cms-align','right');
    f.input('[data-style-key="backgroundColor"]','#333333');
    const mobileSaved=await f.save();
    const mobileHeading=canonicalNodeById(mobileSaved,'home.h1.node.1');
    assert.equal(mobileHeading.style.widthPercent,80);
    assert.equal(mobileHeading.style.textAlign,'left');
    assert.equal(mobileHeading.style.backgroundColor,'#111111');
    assert.equal(mobileHeading.breakpointStyles.mobile.widthPercent,44);
    assert.equal(mobileHeading.breakpointStyles.mobile.textAlign,'right');
    assert.equal(mobileHeading.breakpointStyles.mobile.backgroundColor,'#333333');
    assert.doesNotMatch(source,/data-color-hex/);

    f.click('[data-editor-viewport="base"]');
    assert.equal(f.w.document.querySelector('[data-editor-viewport="base"]').getAttribute('aria-pressed'),'true');
    assert.equal(f.w.document.querySelector('[data-editor-viewport="mobile"]').getAttribute('aria-pressed'),'false');
  }finally{f.close();}
});

test('looped uploaded video hides native controls while preserving source audio',async()=>{
  const doc=canonicalDocument();
  const section=canonicalNodeById(doc,'home.section.1');
  section.children.push(canonicalNode('home.video.node.1','video','video',{
    mediaUrl:'https://media.example/clip.mp4',
    videoLoop:true
  }));
  const f=await domFixture({doc,search:'',viewportWidth:1024});
  try{
    const video=f.w.document.querySelector('main video');
    assert.ok(video);
    assert.equal(video.loop,true);
    assert.equal(video.autoplay,true);
    assert.equal(video.controls,false);
    assert.equal(video.muted,false);
    assert.equal(video.playsInline,true);
    assert.equal(video.preload,'auto');
  }finally{f.close();}
});

test('business compiler resolves published media through the canonical public API instead of the invalid placeholder origin',()=>{
  assert.match(businessRenderSource,/apiBase:publicApiBase/);
  assert.doesNotMatch(businessRenderSource,/apiBase:'https:\/\/website\.invalid'/);
});

test('Website Studio exposes the same iPhone video containers already recognized by the shared validator',()=>{
  assert.match(websiteMediaServiceSource,/AllowedExtensions[\s\S]*"\.mp4"[\s\S]*"\.m4v"[\s\S]*"\.mov"[\s\S]*"\.webm"/);
  assert.match(uploadValidationSource,/"video\/mp4" => extension is "\.mp4" or "\.m4v" or "\.mov"/);
  assert.match(source,/video\/quicktime,video\/x-m4v,\.mov,\.m4v/);
});

test('website media upload transport is parsed inside scoped authority instead of inferred IFormFile binding',()=>{
  assert.match(websitePlatformControllerSource,/Request\.HasFormContentType/);
  assert.match(websitePlatformControllerSource,/Request\.ReadFormAsync\(cancellationToken\)/);
  assert.match(websitePlatformControllerSource,/form\.Files\.GetFile\("file"\)/);
  assert.doesNotMatch(websitePlatformControllerSource,/UploadMedia\(\[FromForm\]/);
});

test('canonical page first paint reapplies responsive hierarchy after the page graph is mounted',()=>{
  assert.match(source,/function renderCanonicalCompositionPage\(\)[\s\S]*for\(const root of roots\)[\s\S]*walkComposition\(\[root\],node=>applyCompositionNode\(findEditableElement\(node\.id\),node\)\)/);
});

test('desktop preserves authored geometry while mobile uses conversion-first semantic roles',async()=>{
  const doc=canonicalDocument();
  const action=canonicalNodeById(doc,'home.a.node.1');
  action.type='cta';
  action.style={widthPercent:38,offsetXPercent:12};
  const desktop=await domFixture({doc,viewportWidth:1440});
  try{
    const a=desktop.w.document.querySelector('main a');
    assert.equal(a.style.width,'38%');
    assert.equal(a.style.left,'12%');
    assert.equal(a.dataset.legendContentRole,'action');
  }finally{desktop.close();}
  assert.match(publicCss,/\[data-legend-content-role="action"\][\s\S]*min-inline-size:min\(100%,var\(--public-action-min\)\)/);
  assert.match(publicCss,/@media\(max-width:650px\)[\s\S]*\[data-legend-content-role="heading"\]\{order:20\}[\s\S]*\[data-legend-content-role="action"\][\s\S]*order:40[\s\S]*\[data-legend-content-role="media"\]\{order:50\}/);
});

test('business banner and shell typography derive from shared tokens and server shell authority',()=>{
  assert.match(foundationCss,/--web-public-banner-pad-block:10px/);
  assert.match(foundationCss,/--web-public-banner-title-weight:800/);
  assert.match(publicCss,/\.business-brand-banner\{[\s\S]*padding:var\(--public-banner-pad-block\) var\(--public-banner-pad-inline\)/);
  assert.match(publicCss,/\.business-brand-banner strong\{[\s\S]*font-size:var\(--public-banner-title-size\)[\s\S]*font-weight:var\(--public-banner-title-weight\)/);
  assert.match(websiteSystemTemplateAuthoritySource,/ApplyHeaderTypographyDefaults/);
  assert.doesNotMatch(source,/applyCanonicalHeaderTypographyDefaults/);
});

test('mobile shell persistence is server-owned while responsive browser safety is render-only',()=>{
  assert.match(websiteSystemTemplateAuthoritySource,/CanonicalizeMobileHeaderChrome/);
  assert.match(websiteSystemTemplateAuthoritySource,/mobile\.WidthPercent = null/);
  assert.match(websiteSystemTemplateAuthoritySource,/node\.BreakpointLayouts\["mobile"\]/);
  assert.doesNotMatch(source,/function constrainDocumentGeometry/);
  assert.doesNotMatch(source,/canonicalizeMobileHeaderChrome/);
  assert.match(source,/function responsiveShellChromeKind\(model, el = null\)/);
  assert.match(source,/function effectiveStyle\([\s\S]*?Math\.min\(100, Number\(style\.widthPercent\)\)/);
  assert.match(source,/function effectiveLayout\([\s\S]*?responsiveShellChromeKind\(model,el\)/);
});

test('Website Studio and GPT contract expose one responsive authority with protected mobile shell geometry',()=>{
  assert.match(source,/Canonical responsive hierarchy/);
  assert.match(source,/Desktop\/Base and Mobile are independent authoring surfaces for page content/);
  assert.match(source,/global public header, brand fit, Menu trigger, and primary navigation geometry are platform shell chrome/);
  assert.match(agentContractSource,/Mobile is an independent first-class editing surface for page content/);
  assert.match(agentContractSource,/A Mobile edit must write only breakpointStyles\.mobile or breakpointLayouts\.mobile/);
  assert.match(agentContractSource,/global public header frame, brand fit, Menu trigger, and primary navigation are platform shell chrome on Mobile/);
  assert.match(agentContractSource,/Outside that protected mobile shell geometry/);
});

test('public startup styling has one responsive authority and one palette authority',()=>{
  assert.equal((publicCss.match(/@media\(max-width:980px\)/g)||[]).length,1);
  assert.equal((publicCss.match(/@media\(max-width:650px\)/g)||[]).length,1);
  assert.doesNotMatch(publicCss,/:root\[data-legend-site="business"\]/);
  assert.match(foundationCss,/:root\[data-legend-site="business"\]\{/);
  assert.match(publicCss,/\.card::before\{content:none\}/);
  assert.match(publicCss,/\.card \.icon:empty,\.contact-card \.icon:empty\{display:none\}/);
  assert.match(publicCss,/\.section\{padding:var\(--public-section-y\) var\(--public-page-pad\)\}/);
  assert.match(publicCss,/--public-page-pad:var\(--web-public-page-pad,var\(--page-pad\)\)/);
  assert.match(publicCss,/@media\(max-width:650px\)[\s\S]*\.section\{padding-top:52px;padding-bottom:52px\}/);
  assert.doesNotMatch(source,/--accent:#b8955a/);
  assert.match(source,/const defaultCodeBlock = '[\s\S]*--navy-deep:#081a3a;--gold:#d4ad45/);
  assert.doesNotMatch(publicCss,/legend-recipe-(?:hero-split|feature-split|card-grid|stat-grid|step-grid|bento-grid)\\{[^}]*grid-template-columns:[^}]*!important/);

});

test('Website Studio canvas keeps public viewport typography and mobile controls stay inside the viewport',()=>{
  assert.ok(source.includes('body.legend-cms-editing{display:block'));
  assert.ok(source.includes('.legend-cms-preview{width:100vw'));
  assert.equal(source.includes('grid-template-columns:minmax(0,1fr) minmax(20rem,24rem)'),false);
  assert.equal(publicCss.includes('@container legend-public-preview'),false);
  assert.equal(publicCss.includes('container:legend-public-preview'),false);
  assert.ok(source.includes('.legend-cms-agent-contract pre{display:block;width:100%;max-width:100%;min-width:0;margin:0;white-space:pre-wrap;overflow-wrap:anywhere;word-break:break-word'));
  assert.match(source,/\.legend-cms-panel>\*\{min-width:0;max-width:100%\}/);
  assert.match(source,/\.legend-cms-panel\{[\s\S]*--legend-cms-sheet-compact:min\(46dvh,430px\)[\s\S]*height:var\(--legend-cms-sheet-height,var\(--legend-cms-sheet-compact\)\)[\s\S]*border-radius:0 0 14px 14px/);
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

test('browser creative workspace exposes whole-site quality media and safe-repair commands without executable audit authority',()=>{
  assert.match(source,/schema:'legend-creative-browser\/v1'/);
  assert.match(source,/getSiteSummary:\(\)=>creativeWorkspaceRequest\('manage\/agent\/summary'\)/);
  assert.match(source,/applyDesignPlan:creativeApplyDesignPlan/);
  assert.match(source,/planSafeQualityRepairs:\(\)=>creativeWorkspaceRequest\('manage\/agent\/design-quality\/repairs'\)/);
  assert.match(source,/applySafeQualityRepairs:async\(\)=>/);
  assert.match(source,/runResponsiveQuality:runResponsiveQualityAudit/);
  assert.match(source,/runSiteResponsiveQuality:runSiteResponsiveQualityAudit/);
  assert.match(source,/fullBleedMedia=[\s\S]*legend-recipe-hero-cinematic-media/);
  assert.match(source,/else if\(fullBleedMedia\)[\s\S]*delete style\.maxWidthPx[\s\S]*delete style\.maxHeightPx/);
  assert.match(source,/runPreflight:runWholeSitePreflight/);
  assert.match(source,/runQuality:runWholeSitePreflight/);
  assert.doesNotMatch(source,/async function refreshQualityInspector\(/);
  assert.match(source,/inspectConversionHealth:\(\)=>creativeWorkspaceRequest\('manage\/agent\/conversion-readiness'\)/);
  assert.match(source,/getSignalCatalog:\(\)=>creativeWorkspaceRequest\('manage\/signal-catalog'\)/);
  assert.match(source,/setSignalMappings:updateSignalMappings/);
  assert.match(source,/testSignalMapping:/);
  assert.match(source,/getSignalHealth:/);
  assert.match(source,/function applySignalConfigurationDelta\(payload\)/);
  assert.match(websitePlatformControllerSource,/nodeSignals = savedTarget\.Signals/);
  assert.match(websitePlatformControllerSource,/fieldSignals = savedTarget\.FieldSignals/);
  assert.doesNotMatch(websitePlatformControllerSource,/source = "website_signal_configuration",[\s\S]*?\n\s*document,/);
  assert.match(source,/listMedia:\(query=\{\}\)=>creativeWorkspaceRequest\('manage\/media',\{query:\{\.\.\.query,designMetadata:query\.designMetadata!==false\}\}\)/);
  assert.match(source,/uploadMedia:async file=>/);
  assert.match(source,/importImage:url=>creativeWorkspaceRequest\('manage\/media\/import'/);
  assert.match(source,/legendAudit/);
  assert.match(source,/legend-site-responsive-audit/);
  assert.match(source,/function applyBreakpointPreview\(\)[\s\S]*refreshResponsiveComposition\(\)[\s\S]*if\(editorPreview\) syncEditorControls\(\)/);
  assert.doesNotMatch(source,/function applyBreakpointPreview\(\) \{\s*if \(!editorPreview\) return;/);
  assert.match(websitePlatformControllerSource,/\[HttpGet\("manage\/agent\/conversion-readiness"\)\]/);
  assert.match(websitePlatformControllerSource,/\[HttpPost\("manage\/media\/import"\)\]/);
  assert.match(websiteImportServiceSource,/ImportImageAsync/);
  assert.match(websiteImportServiceSource,/LegendConnectResearchNetworkPolicy\.CreatePublicReadOnlyHandler/);
  assert.match(source,/sandbox','allow-same-origin'/);
  assert.match(source,/script-src \\'none\\'/);
  assert.match(source,/form-action \\'none\\'/);
  assert.match(source,/connect-src \\'none\\'/);
  assert.match(source,/frame\.remove\(\)/);
  assert.doesNotMatch(source,/sandbox','allow-same-origin allow-scripts'/);
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



test('signal editor persists through canonical signal authority and private test sends no production signal',async()=>{
  const catalog={events:[{name:'ViewContent',actionKey:'page_view',category:'page',metaEligible:true,requiresServerOutcome:false,triggers:['viewed']}],matchingFields:[],runtimeEnabled:true};
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
    await new Promise(resolve=>setTimeout(resolve,0));
    await new Promise(resolve=>setTimeout(resolve,0));

    let send=f.w.document.querySelector('#legend-cms-signal-controls select');
    assert.ok(send);
    send.value='analytics'; send.dispatchEvent(new f.w.Event('change',{bubbles:true}));
    await new Promise(resolve=>setTimeout(resolve,0));
    await new Promise(resolve=>setTimeout(resolve,0));

    const signalCalls=f.calls.filter(call=>call.method==='POST' && new URL(call.url).pathname.endsWith('/manage/signals'));
    assert.ok(signalCalls.length>=2);
    assert.equal(f.calls.some(call=>call.method==='POST' && new URL(call.url).pathname==='/api/website-content/manage'),false);

    const testButton=f.w.document.querySelector('[data-signal-test]');
    assert.ok(testButton);
    testButton.click();
    await new Promise(resolve=>setTimeout(resolve,0));
    await new Promise(resolve=>setTimeout(resolve,0));

    const testCall=f.calls.find(call=>new URL(call.url).pathname.endsWith('/manage/signals/test'));
    assert.ok(testCall);
    const request=JSON.parse(testCall.body);
    assert.equal(request.pagePath,'/');
    assert.equal(request.elementId,'home.h1.node.1');
    assert.equal(request.fieldKey,null);
    assert.equal(request.bindingId,testButton.dataset.signalTest);
    assert.equal(f.calls.some(call=>new URL(call.url).pathname==='/analytics/meta-signal'),false);
    const status=f.w.document.querySelector(`[data-signal-diagnostics="${testButton.dataset.signalTest}"]`).textContent;
    assert.match(status,/PRIVATE TEST/);
    assert.match(status,/no analytics or Meta event sent/);
    assert.match(status,/Analytics ingest: would accept/);
  } finally { f.close(); }
});

test('form-field signals persist on the owning canonical form node through fieldKey authority',async()=>{
  const doc=canonicalDocument();
  canonicalNodeById(doc,'home.section.1').children.push(canonicalNode('home.form','form','form',{
    systemKey:'canonical_inquiry',
    fieldSignals:{}
  }));
  const catalog={events:[{name:'ContactInputStarted',actionKey:'contact_input_started',category:'lead',metaEligible:true,requiresServerOutcome:false,triggers:['field_started']}],matchingFields:[],runtimeEnabled:true};
  const f=await domFixture({doc,signalCatalog:catalog});
  try{
    const phone=f.w.document.querySelector('form[data-cms-id="home.form"] input[name="Phone"]');
    assert.ok(phone);
    phone.dispatchEvent(new f.w.MouseEvent('click',{bubbles:true,cancelable:true}));
    f.click('[data-open="signals"]');
    const add=[...f.w.document.querySelectorAll('#legend-cms-signal-controls button')].find(button=>button.textContent==='Add advanced custom mapping');
    assert.ok(add); add.click();
    await new Promise(resolve=>setTimeout(resolve,0));
    await new Promise(resolve=>setTimeout(resolve,0));

    const call=f.calls.find(call=>call.method==='POST' && new URL(call.url).pathname.endsWith('/manage/signals'));
    assert.ok(call);
    const request=JSON.parse(call.body);
    assert.equal(request.elementId,'home.form');
    assert.equal(request.fieldKey,'phone');

    const savedForm=canonicalNodeById(f.serverDocument(),'home.form');
    assert.ok(savedForm?.fieldSignals?.phone);
    const current=JSON.parse(call.body).signals;
    assert.equal(current.length,1);
    assert.equal(current[0].trigger,'field_started');
    assert.equal(f.calls.some(entry=>entry.method==='POST' && new URL(entry.url).pathname==='/api/website-content/manage'),false);
  }finally{f.close();}
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
    await new Promise(resolve=>setTimeout(resolve,0));
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.equal(f.w.document.querySelector('[data-cms-view="source"]').hidden,false);
    assert.equal(f.w.document.querySelector('#legend-cms-source-scope').value,'site');
    assert.equal(f.calls.some(call=>new URL(call.url).pathname.includes('/manage/ai/')),false);
  } finally { f.close(); }
});


test('Website Studio Content exposes canonical typography and spacing controls without a second style store',()=>{
  for(const key of ['fontFamily','fontSize','fontWeight','lineHeight','letterSpacing','color','textTransform','textDecoration','paddingLeft','paddingRight','marginTop','marginBottom'])
    assert.ok(source.includes(`data-style-key="${key}"`),key);
  assert.ok(source.includes('Weight / thickness'));
  assert.ok(source.includes('canonicalMaterializationIds'));
  assert.ok(source.includes('createMaterializationIdentityContext'));
  assert.ok(source.includes('claimMaterializationId'));
  assert.ok(source.includes("pageKey + '.root.' + (++index)"));
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
  assert.equal(source.includes('templateRepairPending'),false);
  assert.equal(source.includes('synchronizeCanonicalSharedPresentation'),false);
  assert.equal(source.includes('rebuildCanonicalSharedPresentationIndex'),false);
  assert.equal(source.includes('constrainDocumentGeometry'),false);
  assert.equal(source.includes('patchPageStructuralMutation'),false);
  assert.ok(source.includes('renderPageStructuralMutationIncrementally'));
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
  assert.ok(publicInquiryFormSource.includes('[data-website-inquiry]:not([data-preview])'));
  assert.ok(publicInquiryFormSource.includes('[data-website-experience-form][data-submit-capability="lead_capture"]:not([data-preview])'));
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
  assert.ok(source.includes("frame.src = 'data:text/html;charset=utf-8,' + encodeURIComponent(secureEmbedSource(source))"));
  assert.equal(source.includes('allow-same-origin'),false);
  assert.ok(businessMiddlewareSource.includes("frame-src 'self' data:; object-src 'none'"));
  const policy=businessMiddlewareSource.match(/default-src 'self'; script-src[^"]+/)?.[0] || '';
  assert.equal(policy.includes("script-src 'self' 'unsafe-inline'"),false);
  assert.equal(policy.includes("script-src 'self' 'unsafe-eval'"),false);
});

test('shared public mobile navigation uses the compact dropdown contract',()=>{
  assert.ok(publicCss.includes('.nav[data-open=true]{display:grid}'));
  assert.ok(publicCss.includes('grid-template-columns:repeat(2,minmax(0,1fr))'));
  assert.ok(publicCss.includes('white-space:nowrap;overflow:hidden;text-overflow:ellipsis'));
  assert.ok(publicCss.includes('box-shadow:0 18px 44px rgba(0,0,0,.34)'));
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
    assert.equal(f.w.document.querySelector('#legend-cms-action').disabled,false);
    assert.match(f.w.document.querySelector('#legend-cms-action-wiring').textContent,/Managed action/);

    f.change('#legend-cms-action','custom');
    assert.equal(button.dataset.websiteActionKey,undefined);
    f.input('#legend-cms-href','https://example.com/custom');
    assert.equal(button.getAttribute('href'),'https://example.com/custom');
  }finally{f.close();}
});

test('first canvas selection always opens Content for the newly selected canonical node',async()=>{
  const html='<!doctype html><html><body data-page-key="home"><main><section><a href="/contact"><span>Contact</span></a><h2>Heading</h2></section></main></body></html>';
  const f=await domFixture({html});
  try{
    f.click('[data-open="appearance"]');
    const appearance=f.w.document.querySelector('[data-cms-view="appearance"]');
    const content=f.w.document.querySelector('[data-cms-view="content"]');
    assert.equal(appearance.hidden,false);

    f.click('main h2');
    assert.equal(content.hidden,false);
    assert.equal(appearance.hidden,true);
    assert.equal(f.w.document.querySelector('.legend-cms-selected').tagName,'H2');

    f.click('[data-open="appearance"]');
    f.click('main a span');
    assert.equal(content.hidden,false);
    assert.equal(appearance.hidden,true);
    assert.equal(f.w.document.querySelector('.legend-cms-selected').tagName,'A');
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
test('autosave persists changed scopes through canonical mutations before domain connection', async()=>{
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'}});
  try {
    f.click('main h1');
    f.editSelected('Persisted before domain');
    await new Promise(resolve=>setTimeout(resolve,1000));
    const saveCall=f.calls.find(call=>call.method==='POST' && call.url.endsWith('/manage/mutations'));
    assert.ok(saveCall);
    const operations=JSON.parse(saveCall.body).operations;
    assert.ok(operations.some(operation=>operation.type==='replaceNode' && operation.nodeId==='home.h1.node.1'));
    assert.equal(canonicalNodeById(f.serverDocument(),'home.h1.node.1').text,'Persisted before domain');
    assert.equal(f.calls.some(call=>call.method==='POST' && call.url.endsWith('/manage') && JSON.parse(call.body || '{}').document),false);
    const toggle=f.w.document.querySelector('#legend-cms-panel-toggle');
    assert.ok(toggle);
    f.click('#legend-cms-panel-toggle');
    assert.equal(f.w.document.body.classList.contains('legend-cms-panel-hidden'),true);
    assert.equal(toggle.textContent,'');
    assert.equal(toggle.getAttribute('aria-label'),'Open Website Studio controls');
    assert.match(toggle.innerHTML,/<svg/);
    assert.ok(f.w.document.querySelector('.legend-cms-selected'));
    f.editSelected('Still editing full width');
    assert.equal(f.w.document.querySelector('main h1').textContent,'Still editing full width');
    f.click('#legend-cms-panel-toggle');
    assert.equal(f.w.document.body.classList.contains('legend-cms-panel-hidden'),false);
    assert.equal(toggle.textContent,'');
    assert.equal(toggle.getAttribute('aria-label'),'Hide Website Studio controls');
    assert.match(toggle.innerHTML,/<svg/);
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
  const loaded=await domFixture({doc:saved,search:'',viewportWidth:1440});try {
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
    assert.equal(heading.style.width,'100%');
    assert.equal(heading.style.maxWidth,'100%');
    assert.equal(heading.style.left,'0%');
    assert.equal(heading.dataset.legendContentRole,'heading');
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
    assert.match(publicCss,/\.brand-wordmark strong\{[^}]*white-space:nowrap[^}]*text-overflow:ellipsis/);
    assert.match(publicCss,/\.brand\{[^}]*min-width:0[^}]*max-width:min\(58vw,38rem\)/);
    assert.match(businessBuildSource,/brand-wordmark business-brand-banner/);
    assert.match(publicCss,/\.business-brand-banner\{[\s\S]*border:1px solid color-mix\(in srgb,var\(--gold\) 42%,transparent\)[\s\S]*background:linear-gradient\(110deg/);
    assert.match(publicCss,/\.business-brand-banner strong\{[\s\S]*font-family:var\(--font\)[\s\S]*font-weight:var\(--public-banner-title-weight\)[\s\S]*letter-spacing:var\(--public-banner-title-tracking\)/);
    assert.match(publicCss,/@media\(max-width:650px\)[\s\S]*\.brand\{flex:1 1 auto;max-width:calc\(100% - 58px\)\}[\s\S]*\.business-brand-banner\{width:100%;max-width:100%/);
    assert.match(publicCss,/\.nav-toggle\{[^}]*flex:0 0 auto[^}]*white-space:nowrap/);
  }finally{f.close();}
});

test('disabled store control is a single compact Add Store action with no redundant explanation',async()=>{
  const f=await domFixture();
  try{
    const host=f.w.document.querySelector('#legend-cms-store-controls');
    assert.ok(host);
    assert.equal(host.querySelectorAll('button').length,1);
    assert.equal(host.querySelector('#legend-cms-store-toggle')?.textContent,'Add Store');
    assert.equal(host.querySelector('small'),null);
    assert.doesNotMatch(source,/Adds one scoped Store page/);
    assert.match(source,/#legend-cms-store-toggle\{[^}]*justify-self:start/);
  }finally{f.close();}
});

test('primary navigation keeps one behavior authority while its presentation is editable',async()=>{
  const doc=canonicalBusinessNavigation(canonicalDocument());
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'},doc,pages:[{path:'/',label:'Home'}]});
  try{
    const nav=f.w.document.querySelector('#primary-nav');
    assert.ok(nav);
    assert.equal(nav.dataset.cmsLocked,undefined);
    assert.equal(nav.dataset.cmsBehaviorLocked,'true');
    const link=nav.querySelector('[data-legend-page-nav="true"]');
    assert.ok(link);
    link.dispatchEvent(new f.w.MouseEvent('click',{bubbles:true,cancelable:true}));
    assert.equal(f.w.document.querySelector('.legend-cms-selected'),nav);
    f.input('#legend-cms-scale','1.9');
    const saved=await f.save();
    const navNode=saved.shell.header[0].children.find(node=>node.id==='shell.primary-nav');
    assert.equal(navNode.style.fontScale,1.9);
    assert.equal(navNode.systemKey,'primary_navigation');
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

test('publish flushes unsaved mutations first then calls the explicit publish action',async()=>{
 const f=await domFixture();try{
   f.click('main h1');f.editSelected('New draft');f.click('#legend-cms-publish');
   await new Promise(r=>setTimeout(r,0));await new Promise(r=>setTimeout(r,0));
   const mutationIndex=f.calls.findIndex(call=>call.method==='POST' && call.url.endsWith('/manage/mutations'));
   const publishIndex=f.calls.findIndex(call=>call.method==='POST' && call.url.endsWith('/manage/publish'));
   assert.ok(mutationIndex>0);assert.ok(publishIndex>mutationIndex);
   assert.equal(JSON.parse(f.calls[publishIndex].body).expectedRevision,'r2');
   assert.equal(f.calls.some(call=>call.method==='POST' && call.url.endsWith('/manage') && JSON.parse(call.body || '{}').document),false);
 }finally{f.close();}
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
test('undo and redo replay inverse canonical mutation batches without document snapshots',async()=>{
 const f=await domFixture();try{
   f.click('main h1');f.editSelected('First edit');await f.save();
   f.click('main h1');f.editSelected('Second edit');
   f.click('#legend-cms-undo');
   await new Promise(r=>setTimeout(r,0));await new Promise(r=>setTimeout(r,0));
   assert.equal(f.w.document.querySelector('main h1').textContent,'First edit');
   f.click('#legend-cms-redo');
   await new Promise(r=>setTimeout(r,0));await new Promise(r=>setTimeout(r,0));
   assert.equal(f.w.document.querySelector('main h1').textContent,'Second edit');
   assert.equal(source.includes('function historySnapshot'),false);
   assert.match(source,/undoStack\.push\(\{undo,redo\}\)/);
   assert.match(source,/creativeApplyMutationBatch\(operations\)/);
 }finally{f.close();}
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
  const previewSource=decodeURIComponent(frame.src.slice(frame.src.indexOf(',')+1));
  assert.ok(previewSource.includes(sourceInput.value));
  assert.match(previewSource,/Content-Security-Policy/);
  assert.match(previewSource,/connect-src 'none'/);
  assert.match(previewSource,/form-action 'none'/);
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

test('business page selector switches existing canonical routes without a second manage bootstrap', async()=>{
  const doc=canonicalDocument();
  doc.pages['/about']={title:'About',description:'About',navigation:{label:'About',showInNavigation:true,order:10,isDeleted:false},dynamicBinding:null,composition:[
    canonicalNode('about.section','section','section',{children:[canonicalNode('about.h1','heading','h1',{text:'About us'})]})
  ]};
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'},doc});
  try{
    const before=f.calls.filter(call=>(call.method||'GET')==='GET' && new URL(call.url).pathname.endsWith('/manage')).length;
    f.change('#legend-cms-page-select','/about');
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.equal(f.w.document.querySelector('main h1')?.textContent,'About us');
    assert.equal(f.w.document.querySelector('#legend-cms-page-select').value,'/about');
    assert.equal(new URL(f.w.location.href).searchParams.get('cmsPage'),'/about');
    const after=f.calls.filter(call=>(call.method||'GET')==='GET' && new URL(call.url).pathname.endsWith('/manage')).length;
    assert.equal(after,before);
  }finally{f.close();}
});

test('Studio bootstrap uses compact context and lazy advanced data/signal catalogs',()=>{
  assert.match(websitePlatformControllerSource,/signalCatalog = \(object\?\)null, agentContract = WebsiteStudioAgentContract\.CompactPayload/);
  assert.match(websitePlatformControllerSource,/LoadAsync\(draft, business\.Id, cancellationToken\)/);
  assert.match(websitePlatformControllerSource,/\[HttpGet\("manage\/agent\/contract"\)\]/);
  assert.match(websitePlatformControllerSource,/\[HttpGet\("manage\/data-catalog"\)\]/);
  assert.match(source,/async function ensureSignalCatalog\(\)/);
  assert.match(source,/async function ensureBusinessDataCatalog\(\)/);
  assert.match(source,/getFullContract:\(\)=>creativeWorkspaceRequest\('manage\/agent\/contract'\)/);
  assert.match(source,/listBusinessData:async\(\)=>/);
});

test('business Pages manager creates a canonical page by mutation and switches in place', async()=>{
  const f=await domFixture({
    siteKey:'business',
    business:{id:'business-id',displayName:'Fixture business'},
    pages:[{path:'/',label:'Home'},{path:'/about',label:'About'}]
  });
  try {
    const bootstrapCount=f.calls.filter(call=>!call.method && new URL(call.url).pathname.endsWith('/manage')).length;
    f.click('[data-open="page"]');
    f.input('#legend-cms-page-nav-label','Team');
    f.input('#legend-cms-page-slug','/team');
    f.click('#legend-cms-page-create');
    await new Promise(resolve=>setTimeout(resolve,0));await new Promise(resolve=>setTimeout(resolve,0));
    const saveCall=f.calls.find(call=>call.method==='POST' && call.url.endsWith('/manage/mutations'));
    assert.ok(saveCall);
    const saved=f.serverDocument();
    assert.equal(saved.pages['/team'].title,'Team');
    assert.equal(saved.pages['/team'].navigation.label,'Team');
    assert.equal(saved.pages['/team'].navigation.isDeleted,false);
    assert.ok(saved.pages['/team'].composition.some(node=>node.type==='section'));
    assert.ok(canonicalNodes(saved,'/team').some(node=>node.type==='heading'&&node.text==='Team'));
    assert.equal(f.w.document.querySelector('#legend-cms-page-select').value,'/team');
    assert.equal(f.calls.filter(call=>!call.method && new URL(call.url).pathname.endsWith('/manage')).length,bootstrapCount);
    assert.equal(Object.hasOwn(saved.pages['/team'],'extras'),false);
  } finally { f.close(); }
});

test('business Pages manager renames the canonical page by mutation without a shadow route or rebootstrap', async()=>{
  const doc=canonicalDocument();
  doc.pages['/services']={title:'Services',description:'',navigation:{label:'Services',showInNavigation:true,order:10,isDeleted:false},dynamicBinding:null,composition:[
    canonicalNode('services.section','section','section',{children:[canonicalNode('services.h1','heading','h1',{text:'Services'})]})
  ]};
  const f=await domFixture({
    siteKey:'business',doc,
    business:{id:'business-id',displayName:'Fixture business'},
    search:'?legendEdit=ticket&cmsPage=/services',
    pages:[{path:'/',label:'Home'},{path:'/services',label:'Services'}]
  });
  try {
    const bootstrapCount=f.calls.filter(call=>!call.method && new URL(call.url).pathname.endsWith('/manage')).length;
    f.click('[data-open="page"]');
    f.input('#legend-cms-page-slug','/work');
    f.click('#legend-cms-page-rename');
    await new Promise(resolve=>setTimeout(resolve,0));await new Promise(resolve=>setTimeout(resolve,0));
    const saveCall=f.calls.find(call=>call.method==='POST' && call.url.endsWith('/manage/mutations'));
    assert.ok(saveCall);
    const saved=f.serverDocument();
    assert.ok(saved.pages['/work']);
    assert.equal(saved.pages['/services'],undefined);
    assert.equal(Object.hasOwn(saved.pages['/work'],'templatePath'),false);
    assert.equal(f.calls.filter(call=>!call.method && new URL(call.url).pathname.endsWith('/manage')).length,bootstrapCount);
  } finally { f.close(); }
});

test('business page duplication re-resolves protected inquiry capability instead of copying backend wiring', async()=>{
  const doc=canonicalDocument();
  doc.pages['/'].composition[0].children.push(canonicalNode('home.form','form','form',{
    systemKey:'canonical_inquiry',
    title:'Talk with us',
    text:'Send',
    signals:[{id:'11111111111111111111111111111111',trigger:'click',eventName:'cta_click',actionKey:'cta_click',deliveryMode:'analytics'}]
  }));
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'},doc});
  try{
    f.click('[data-open="page"]');
    f.input('#legend-cms-page-slug','/copy');
    f.click('#legend-cms-page-duplicate');
    await new Promise(resolve=>setTimeout(resolve,0));await new Promise(resolve=>setTimeout(resolve,0));
    const mutation=f.calls.find(call=>call.method==='POST' && call.url.endsWith('/manage/mutations'));
    assert.ok(mutation);
    const operations=JSON.parse(mutation.body).operations;
    assert.ok(operations.some(operation=>operation.type==='insertCapability' && operation.capabilityKey==='contact.inquiry.submit'));
    const copied=f.serverDocument().pages['/copy'];
    const form=canonicalNodes({pages:{'/copy':copied}},'/copy').find(node=>node.type==='form');
    assert.ok(form);
    assert.equal(form.systemKey,'canonical_inquiry');
    assert.deepEqual(form.signals || [],[]);
  }finally{f.close();}
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
    assert.equal(f.calls.some(call=>call.method==='POST' && call.url.endsWith('/manage/mutations')),false);
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
    assert.equal(image.className ?? null,null);
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
    assert.equal(video.className ?? null,null);
    assert.equal(JSON.stringify(saved).includes('videoUrl'),false);
    assert.equal(JSON.stringify(saved).includes('extras'),false);
    assert.equal(JSON.stringify(saved).includes('ticket='),false);
  } finally { f.close(); }
});


test('media upload has one multipart transport and bypasses inferred ApiController form binding', ()=>{
  const uploadStart=source.indexOf('async function uploadMedia(file)');
  const uploadEnd=source.indexOf('async function uploadImageAsset',uploadStart);
  const upload=source.slice(uploadStart,uploadEnd);
  assert.ok(upload.includes('new FormData()'));
  assert.ok(upload.includes("body.append('file', file, file.name || 'website-media')"));
  assert.doesNotMatch(upload,/['"]Content-Type['"]\s*:/);
  assert.ok(upload.includes("headers: { Accept: 'application/json' }"));
  assert.match(websitePlatformControllerSource,/public async Task<IActionResult> UploadMedia\(CancellationToken/);
  assert.doesNotMatch(websitePlatformControllerSource,/UploadMedia\(\[FromForm\]/);
  assert.match(websitePlatformControllerSource,/MultipartUploadTransport\.ReadAsync\(Request, cancellationToken\)/);
  assert.match(websitePlatformControllerSource,/GetRequiredService<WebsiteMediaService>\(\)/);
  assert.match(uploadValidationSource,/public static class MultipartUploadTransport/);
  assert.match(uploadValidationSource,/TryResolveVisualMediaType/);
  assert.match(uploadValidationSource,/image\/heic/);
  assert.match(uploadValidationSource,/image\/heif/);
  assert.match(uploadValidationSource,/image\/avif/);
  assert.match(source,/accept="image\/\*,\.heic,\.heif,\.avif"/);
  assert.doesNotMatch(source,/\^image\\\/\(jpeg\|png\|webp\)\$/);
});

test('media insertion never invents reserved legend-cms runtime classes', ()=>{
  assert.equal(source.includes("className:'legend-cms-image'"),false);
  assert.equal(source.includes("className:isImage?'legend-cms-image':null"),false);
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
    assert.deepEqual(definition.composition[0].fieldSignals || {},{});
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
    assert.match(savedMeta,/Saved canonical quality · revision 7 · 1 errors · 1 warnings/);
    assert.match(liveMeta,/Live page checks \(rendered canvas\)/);
    assert.match(savedText,/Saved draft dynamic collection is unavailable/);
    assert.doesNotMatch(savedText,/missing alternative text|no working destination/);
    assert.doesNotMatch(liveText,/missing alternative text|no working destination/);
    assert.equal(f.w.document.querySelector('main a'),null);
    assert.equal(f.w.document.querySelector('main span')?.textContent,'Broken');
    const renderedImage=f.w.document.querySelector('main img');
    assert.ok(renderedImage?.hasAttribute('alt'));
    assert.equal(renderedImage.getAttribute('alt'),'');
    assert.ok(f.calls.some(call=>new URL(call.url).pathname.endsWith('/manage/agent/design-quality')));
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
    await new Promise(resolve=>setTimeout(resolve,0));await new Promise(resolve=>setTimeout(resolve,0));
    assert.equal(f.w.document.querySelectorAll('main a').length,2);
    f.click('#legend-cms-undo');
    await new Promise(resolve=>setTimeout(resolve,0));await new Promise(resolve=>setTimeout(resolve,0));
    assert.equal(f.w.document.querySelectorAll('main a').length,1);
  } finally { f.close(); }
});

test('public publication renders without stale-template flash and HTML is never cached',()=>{
  assert.match(businessBuildSource,/<html lang="en" data-legend-site="\$\{siteKey\}" hidden>/);
  assert.match(protectLayoutSource,/hidden="@\(!isStandaloneQuoteLanding \? "hidden" : null\)"/);
  assert.match(protectLayoutSource,/data-legend-canonical-pending="@\(!isStandaloneQuoteLanding \? "true" : null\)"/);
  assert.match(protectLayoutSource,/<noscript><style>html\[hidden\]\{display:block!important\}<\/style><\/noscript>/);
  assert.match(source,/function unavailable\(error\) \{[\s\S]*document\.documentElement\.hidden = false;/);
  assert.match(source,/fetch\(url, \{ cache: 'no-store' \}\)/);
  assert.match(businessMiddlewareSource,/CacheControl = "no-store,no-cache,must-revalidate,max-age=0"/);
  assert.match(legendWebConfigSource,/name="Do not cache published HTML"/);
  assert.match(legendWebConfigSource,/RESPONSE_Cache_Control/);
  assert.match(legendWebConfigSource,/no-store, no-cache, must-revalidate, max-age=0/);
});

test('canonical public design authority uses wider canvas, tighter rhythm, and crisp perimeter accents',()=>{
  assert.match(foundationCss,/--web-public-page-pad:clamp\(24px,4\.25vw,72px\)/);
  assert.match(foundationCss,/--web-public-section-y:clamp\(52px,6vw,88px\)/);
  assert.match(foundationCss,/--web-public-body-weight:500/);
  assert.match(foundationCss,/--web-public-heading-weight:800/);
  assert.match(publicCss,/\.section\{padding:var\(--public-section-y\) var\(--public-page-pad\)\}/);
  assert.match(publicCss,/\.card-grid\{display:grid;grid-template-columns:repeat\(auto-fit,minmax\(min\(100%,280px\),1fr\)\)/);
  assert.match(publicCss,/border:1px solid var\(--public-card-border\)/);
  assert.match(publicCss,/\.card::before\{content:none\}/);
  assert.doesNotMatch(publicCss,/height:2px;background:linear-gradient\(90deg,var\(--gold\)/);
});

test('Website Studio style controls mutate only the selected canonical node and survive save', async () => {
  const f=await domFixture();
  try {
    f.click('main h1');
    f.input('[data-style-key="fontFamily"]','Georgia');
    f.input('[data-style-key="fontSize"]','54');
    f.input('[data-style-key="fontWeight"]','800');
    f.click('main h2');
    f.input('[data-style-key="fontFamily"]','Inter');
    f.input('[data-style-key="fontSize"]','36');
    f.input('[data-style-key="fontWeight"]','600');
    const saved=await f.save();
    const first=canonicalNodeById(saved,'home.h1.node.1').style;
    const second=canonicalNodeById(saved,'home.h2.node.1').style;
    assert.equal(first.fontFamily,'Georgia');
    assert.equal(first.fontSize,54);
    assert.equal(first.fontWeight,800);
    assert.equal(second.fontFamily,'Inter');
    assert.equal(second.fontSize,36);
    assert.equal(second.fontWeight,600);
  } finally { f.close(); }
});

test('canonical visual style preserves numeric font weight and signed letter spacing', async () => {
  const f=await domFixture();
  try {
    f.click('main h1');
    f.input('[data-style-key="fontWeight"]','700');
    f.input('[data-style-key="letterSpacing"]','-1.25');
    const heading=f.w.document.querySelector('main h1');
    assert.equal(heading.style.letterSpacing,'-1.25px');
    const saved=await f.save();
    const style=canonicalNodeById(saved,'home.h1.node.1').style;
    assert.equal(style.fontWeight,700);
    assert.equal(style.letterSpacing,-1.25);
    assert.match(source,/['"]maxHeight['"]/);
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
  assert.match(source,/id="legend-cms-scale" type="number" min="0\.05" step="any"/);
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
  assert.match(publicCss,/body\{min-height:100dvh;display:flex;flex-direction:column;overflow-x:clip;[^}]*font-family:var\(--font\);[^}]*font-weight:var\(--web-public-body-weight,500\)/);
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


test('signal-bound managed action identity remains locked while presentation stays editable',async()=>{
  const actions=[{key:'business_schedule',group:'Schedule',label:'Schedule',defaultText:'Book consultation',href:'https://booking.example/confirmed-flow',openInNewTab:false,analyticsEventName:'cta_click',behaviorKey:'cta_click'}];
  const doc=canonicalDocument();
  doc.pages['/'].composition[0].children.push(canonicalNode('home.schedule','cta','a',{
    text:'Book consultation',
    actionKey:'business_schedule',
    href:'https://booking.example/confirmed-flow',
    signals:[{id:'11111111111111111111111111111111',eventName:'cta_click',actionKey:'cta_click',trigger:'click',deliveryMode:'analytics',oncePerSession:true,matchingFields:[]}]
  }));
  const f=await domFixture({siteKey:'business',doc,ctaCatalog:actions,business:{id:'business-id',displayName:'Fixture business'}});
  try {
    f.click('[data-cms-id="home.schedule"]');
    const button=f.w.document.querySelector('.legend-cms-selected');
    const elementId=button.dataset.cmsCompositionId;
    assert.equal(f.w.document.querySelector('#legend-cms-action').disabled,true);
    assert.match(f.w.document.querySelector('#legend-cms-action-wiring').textContent,/Protected wiring/);

    f.editSelected('Pay now and complete my application');
    const style=f.w.document.querySelector('[data-style-key="fontSize"]');
    style.value='29'; style.dispatchEvent(new f.w.Event('input',{bubbles:true}));
    f.change('#legend-cms-action','custom');
    f.input('#legend-cms-href','https://unrelated.example');

    const saved=await f.save();
    const cta=canonicalNodeById(saved,elementId);
    assert.equal(cta.actionKey,'business_schedule');
    assert.equal(cta.href,'https://booking.example/confirmed-flow');
    assert.equal(cta.text,'Pay now and complete my application');
    assert.equal(cta.style.fontSize,29);
    assert.equal(button.dataset.cmsCompositionId,elementId);
  }finally{f.close();}
});


test('duplicate and reusable presentation cloning strips node and field signal mappings',()=>{
  assert.match(source,/current\.signals=\[\];[\s\S]*current\.fieldSignals=\{\}/);
  assert.match(source,/copy\.signals=\[\];[\s\S]*copy\.fieldSignals=\{\}/);
  assert.match(source,/containsProtectedSystemNode[\s\S]*fieldSignals/);
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
    error:'website_mutation_protected',
    message:"Protected component 'home.h1.node.1' cannot be removed because its canonical behavior is platform-owned.",
    canonicalProtectionViolation:true,
    correction
  };
  const contract={schema:'legend-website-studio-agent/v1',promptTemplate:'contract',protectedEditCorrection:correction};
  const f=await domFixture({agentContract:contract,mutationSequence:[{status:400,payload}]});
  try{
    f.click('main h1');
    f.click('[data-open="source"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    const sourceInput=f.w.document.querySelector('#legend-cms-site-source');
    assert.equal(sourceInput.readOnly,false);
    const selected=JSON.parse(sourceInput.value);
    selected.text='Rejected edit';
    sourceInput.value=JSON.stringify(selected,null,2);
    sourceInput.dispatchEvent(new f.w.Event('input',{bubbles:true}));
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
    'Prefer a clear primary action per decision moment as a default.',
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


test('mobile Website Studio is a draggable compact-to-expanded top sheet with an icon-only hide control',()=>{
  assert.match(source,/@media\(max-width:800px\)[\s\S]*--legend-cms-sheet-compact:min\(46dvh,430px\)[\s\S]*--legend-cms-sheet-expanded:min\(88dvh,calc\(100dvh - 12px\)\)/);
  assert.match(source,/body\.legend-cms-panel-expanded \.legend-cms-panel\{--legend-cms-sheet-height:var\(--legend-cms-sheet-expanded\)\}/);
  assert.match(source,/\.legend-cms-sheet-handle\{[\s\S]*touch-action:none[\s\S]*cursor:ns-resize/);
  assert.match(source,/sheetHandle\?\.addEventListener\('pointerdown'/);
  assert.match(source,/sheetHandle\?\.addEventListener\('pointermove'/);
  assert.match(source,/setSheetExpanded\(expand\)/);
  assert.match(source,/panelToggleIcon = hidden => hidden[\s\S]*<svg/);
  assert.match(source,/aria-label',hidden \? 'Open Website Studio controls' : 'Hide Website Studio controls'/);
  assert.doesNotMatch(source,/panelToggle\.textContent = 'Full-page canvas'/);
  assert.match(source,/\.legend-cms-preview\{width:100%;max-width:100%;height:100dvh;overflow-y:auto/);
  assert.match(source,/\.legend-cms-bar button\{[\s\S]*min-height:32px[\s\S]*font-size:11px/);
});


test('native experience renders declarative controls calculations and protected CTA references without embed execution',async()=>{
  const doc=canonicalDocument();
  canonicalNodeById(doc,'home.section.1').children.push(
    canonicalNode('home.experience','experience','form',{
      title:'Project estimator',
      text:'A native interactive flow',
      experience:{
        kind:'calculator',
        submitCapability:null,
        steps:[{key:'main',title:'Project',description:'Choose details',controlKeys:['project_type','project_size','schedule']}],
        controls:[
          {key:'project_type',type:'choice',label:'Project type',required:true,options:[
            {value:'installation',label:'New installation'},
            {value:'repair',label:'Repair / upgrade'}
          ]},
          {key:'project_size',type:'number',label:'Project size',required:true,min:100,max:10000,defaultValue:1500},
          {key:'schedule',type:'cta',label:'Schedule',action:{type:'cta',actionKey:'legend_contact'}}
        ],
        calculations:{
          estimate:{op:'multiply',values:[{op:'ref',ref:'project_size'},{op:'value',value:2}]}
        },
        results:[
          {key:'estimate',label:'Preliminary estimate',format:'currency',expression:{op:'ref',ref:'calc.estimate'}}
        ]
      }
    })
  );

  const f=await domFixture({
    doc,
    ctaCatalog:[{key:'legend_contact',group:'Contact',label:'Contact',defaultText:'Contact',href:'/contact',analyticsEventName:'cta_click',behaviorKey:'cta_click'}]
  });
  try{
    const experience=f.w.document.querySelector('form.legend-native-experience[data-website-experience-id="home.experience"]');
    assert.ok(experience);
    assert.equal(experience.querySelectorAll('[data-experience-control]').length,3);
    assert.equal(experience.querySelector('[data-website-action-key="legend_contact"]')?.textContent,'Schedule');
    const size=experience.querySelector('[data-cms-field-key="project_size"]');
    size.value='1800';
    size.dispatchEvent(new f.w.Event('input',{bubbles:true}));
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.match(experience.querySelector('[data-experience-result="estimate"]').textContent,/3[,\s]?600|3600/);
    assert.equal(experience.querySelector('iframe'),null);
    assert.equal(experience.dataset.cmsSignalOnly,undefined);
    experience.dispatchEvent(new f.w.MouseEvent('click',{bubbles:true,cancelable:true}));
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.equal(f.w.document.querySelector('#legend-cms-duplicate')?.disabled,false);
    assert.equal(f.w.document.querySelector('#legend-cms-remove')?.disabled,false);
    assert.match(source,/function buildNativeExperience\(node\)/);
    assert.match(source,/evaluateExperienceExpression/);
    assert.match(publicInquirySource,/data-website-experience-form/);
    assert.match(publicInquiryFormCss,/\.legend-native-experience/);
  }finally{f.close();}
});

test('native experience authoring exposes broad declarative freedom while backend authority remains absent',()=>{
  assert.match(editorContractsSource,/\["experience"\] = \["form"\]/);
  assert.match(agentContractSource,/native declarative interactive experience/i);
  assert.match(agentContractSource,/questions, options, steps, branching, calculations, results/i);
  assert.match(agentContractSource,/Selected Source never writes Signals\/FieldSignals/i);
  assert.doesNotMatch(source,/eval\(.*experience/i);
  assert.doesNotMatch(source,/new Function\(/);
});


test('Protect native experiences reuse public CTA catalog, canonical bindings, and shared inquiry runtime',()=>{
  assert.match(websitePlatformControllerSource,/ctaCatalog\s*=\s*new\s*\{\s*options\s*=\s*publicActions\s*\}/);
  assert.match(source,/ctaCatalog\s*=\s*Array\.isArray\(payload\.ctaCatalog\?\.options\)/);
  assert.match(source,/window\.__legendTrackingInitialized\s*===\s*true[\s\S]*installPublishedSignalBindings\(\)/);
  assert.match(publicInquiryFormSource,/data-website-experience-form/);
  assert.match(publicInquiryFormSource,/data-submit-capability="lead_capture"/);
  assert.match(protectLayoutSource,/src="~\/js\/tracking\.js"/);
});


test('unapplied Selected Source blocks publication without publishing the older draft',async()=>{
  const f=await domFixture();
  try {
    f.click('main h1'); f.click('[data-open="source"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    const input=f.w.document.querySelector('#legend-cms-site-source');
    const value=JSON.parse(input.value); value.text='Unapplied source';
    input.value=JSON.stringify(value); input.dispatchEvent(new f.w.Event('input',{bubbles:true}));
    f.click('#legend-cms-publish');
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.equal(f.calls.some(call=>call.url.endsWith('/manage/publish')),false);
    assert.match(f.w.document.querySelector('#legend-cms-status').textContent,/Apply or discard/);
  } finally {f.close();}
});

test('protected native fields remain real controls after label edits and structural rendering',async()=>{
  const doc=canonicalDocument();
  doc.pages['/quote/life']={title:'Life',systemTemplateKey:'protect_template:life_wizard',navigation:{isDeleted:false},composition:[]};
  const html='<!doctype html><html><body><main><section><form id="lifeWizardForm" data-form-key="quote_life"><label data-cms-id="life.name">Your name <input name="FirstName" required></label><button type="submit">Continue <svg aria-hidden="true"></svg></button></form></section></main></body></html>';
  const f=await domFixture({siteKey:'protect',doc,pathname:'/Quote/Life',html});
  try {
    const control=f.w.document.querySelector('input[name="FirstName"]');
    let changes=0;control.addEventListener('change',()=>changes++);
    f.click('main section');f.click('[data-add="text"]');
    const saved=await f.save();
    assert.equal(f.w.document.querySelector('input[name="FirstName"]'),control);
    assert.equal(control.required,true);
    control.dispatchEvent(new f.w.Event('change'));assert.equal(changes,1);
    const runtime=canonicalNodes(saved,'/quote/life').find(node=>node.systemKey==='protect_runtime_form:quote_life');
    assert.ok(runtime.fieldPresentations.firstname);
    assert.equal(f.w.document.querySelector('main label').textContent.trim(),'Your name');
    assert.ok(f.w.document.querySelector('button[type="submit"] svg'));
  } finally {f.close();}
});

test('business preview navigation retains its business scope for authored pages',async()=>{
  const doc=canonicalBusinessNavigation();
  doc.pages['/custom-offer']={title:'Offer',navigation:{label:'Offer',order:2,showInNavigation:true},composition:[canonicalNode('offer.title','heading','h1',{text:'Offer'})]};
  const f=await domFixture({siteKey:'business',business:{id:'11111111-1111-1111-1111-111111111111',displayName:'Scoped'},doc,pathname:'/business-preview/',search:'?businessId=11111111-1111-1111-1111-111111111111',pages:[{path:'/',label:'Home'}]});
  try {
    const link=f.w.document.querySelector('[data-legend-page-route="/custom-offer"]');
    const url=new URL(link.href);
    assert.equal(url.pathname,'/business-preview/');
    assert.equal(url.searchParams.get('cmsPage'),'/custom-offer');
    assert.equal(url.searchParams.get('businessId'),'11111111-1111-1111-1111-111111111111');
    assert.equal(url.searchParams.has('legendEdit'),false);
  } finally {f.close();}
});


test('custom-domain asset authority serves the canonical form stylesheet referenced by compiled pages',()=>{
  assert.match(businessBuildSource,/href="\/public-inquiry-form\.css/);
  assert.match(businessMiddlewareSource,/path is "\/site\.css" or "\/public-inquiry-form\.css"/);
});


test('template label presentation updates preserve native inputs and button icons',async()=>{
  const doc=canonicalDocument();
  doc.pages['/quote/life']={title:'Life',systemTemplateKey:'protect_template:life_wizard',navigation:{isDeleted:false},composition:[
    canonicalNode('life.section','section','section',{children:[
      canonicalNode('runtime.form.quote_life','container','div',{systemKey:'protect_runtime_form:quote_life',style:{backgroundColor:'#112233'},children:[
        canonicalNode('life.label','text','label',{text:'Your first name'}),
        canonicalNode('life.submit','text','span',{text:'Get started'})
      ]})
    ]})
  ]};
  const html='<!doctype html><html><body><main><section data-cms-id="life.section"><p data-cms-id="removed.copy">Stale copy</p><form id="lifeWizardForm" data-form-key="quote_life"><label data-cms-id="life.label">Old label <input name="FirstName" required></label><button data-cms-id="life.submit" type="submit">Old submit <svg aria-hidden="true"></svg></button></form></section></main></body></html>';
  const f=await domFixture({siteKey:'protect',doc,pathname:'/Quote/Life',html});
  try {
    assert.equal(f.w.document.querySelector('[data-cms-id="removed.copy"]'),null);
    assert.equal(f.w.document.querySelector('main form').style.backgroundColor,'rgb(17, 34, 51)');
    assert.equal(f.w.document.querySelector('main label').textContent,'Your first name');
    assert.ok(f.w.document.querySelector('main label input[required]'));
    assert.equal(f.w.document.querySelector('main button[type="submit"]').textContent,'Get started');
    assert.ok(f.w.document.querySelector('main button[type="submit"] svg'));
  } finally {f.close();}
});


test('global website chrome cleanup is owned by one server authority, never a browser autosave',()=>{
  assert.match(websiteSystemTemplateAuthoritySource,/ApplySharedShellAuthority/);
  assert.match(websiteSystemTemplateAuthoritySource,/IsPageLocalShellCopy/);
  assert.match(websiteSystemTemplateAuthoritySource,/primary_navigation/);
  assert.doesNotMatch(source,/normalizePageCompositionNodes/);
  assert.doesNotMatch(source,/templateRepairPending/);
});

test('canonical inquiry presentations are independently authored and never browser-synchronized across pages',()=>{
  assert.doesNotMatch(source,/synchronizeCanonicalSharedPresentation/);
  assert.doesNotMatch(source,/sharedPresentationIndex/);
  assert.match(source,/function formFieldPresentationForElement\(el, create = true\)/);
  assert.match(source,/selectedFieldPresentation=selected\?\.dataset\?\.cmsSignalOnly \? formFieldPresentationForElement\(selected,false\)/);
  assert.match(source,/formNode\.fieldPresentations\[key\] \|\|= normalizeControlPresentation\(null\)/);
});

test('LEGEND website credits are immutable through the server shell authority',()=>{
  assert.match(websiteSystemTemplateAuthoritySource,/legend-platform-attribution/);
  assert.match(websiteSystemTemplateAuthoritySource,/node\.Text = "Legend®"/);
  assert.match(websiteSystemTemplateAuthoritySource,/node\.Href = "https:\/\/www\.mylegnd\.com\/"/);
  assert.match(websiteSiteSourceSource,/"legend-platform-attribution"/);
  assert.doesNotMatch(source,/enforceLegendAttributionNode/);
  assert.match(publicCss,/\.legend-platform-attribution\{text-decoration:underline!important/);
  assert.match(businessBuildSource,/Website Designed by/);
  assert.match(businessBuildSource,/Powered by/);
});

test('public mobile navigation is one compact viewport-safe dropdown instead of a side rail',()=>{
  assert.match(publicCss,/@media\(max-width:980px\)[\s\S]*?\.site-header\{[^}]*grid-template-columns:minmax\(0,1fr\) auto/);
  assert.match(publicCss,/@media\(max-width:980px\)[\s\S]*?\.nav\{[^}]*top:calc\(100% \+ 6px\)[^}]*left:max\(12px,env\(safe-area-inset-left\)\)[^}]*right:max\(12px,env\(safe-area-inset-right\)\)/);
  assert.match(publicCss,/\.nav\{[^}]*grid-template-columns:repeat\(2,minmax\(0,1fr\)\)[^}]*max-height:min\(68dvh,520px\)/);
  assert.match(publicCss,/\.nav a,\.nav button\{[^}]*width:100%[^}]*text-align:center[^}]*justify-content:center[^}]*white-space:nowrap/);
  assert.match(publicCss,/\.business-brand-banner strong\{[^}]*white-space:nowrap[^}]*text-overflow:ellipsis/);
});
