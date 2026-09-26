// Publication compiler: executes the exact browser CMS renderer against the
// same built templates. It never downloads content or accepts submitted HTML.
import {readFile} from 'node:fs/promises';
import {resolve} from 'node:path';
import vm from 'node:vm';
import {parseHTML} from 'linkedom';
import {businessPages} from '../src/business-content.mjs';
import {publicApiBase,publicRuntimeAssets} from '../src/runtime-config.mjs';

export async function compileBusiness(input, root=resolve(import.meta.dirname,'..')) {
  if (!input?.business?.id || !input.business.displayName || !input.document) throw new Error('A business and document are required.');
  const cms=await readFile(resolve(root,'dist/legend-public-cms.js'),'utf8');
  const result={};
  const documents=input.document.pages||{};
  const projections=new Map((Array.isArray(input.collections)?input.collections:[])
    .filter(value=>value?.id).map(value=>[value.id,value]));
  const normalizeRoute=route=>(String(route||'').replace(/\/$/,'')||'/');
  const staticRoute=/^\/(?:[a-z0-9_-]+\/?)*$/;
  const dynamicPattern=/^\/(?:[a-z0-9_-]+\/)*\{item\}\/?$/;
  const templateByRoute=new Map(businessPages.map(page=>[page.key==='home'?'/':'/'+page.key,page]));
  const descriptors=new Map();

  for(const route of templateByRoute.keys()) descriptors.set(route,{route,sourceRoute:route,dynamicItem:null});
  for(const rawRoute of Object.keys(documents)) {
    if(!staticRoute.test(rawRoute)||rawRoute.length>160)throw new Error('Invalid website page route.');
    const route=normalizeRoute(rawRoute);
    descriptors.set(route,{route,sourceRoute:route,dynamicItem:null});
  }

  for(const [rawSourceRoute,page] of Object.entries(documents)) {
    const sourceRoute=normalizeRoute(rawSourceRoute);
    const binding=page?.dynamicBinding;
    if(!binding?.collectionId || !binding.itemKeyField || !binding.routePattern) continue;
    if(!dynamicPattern.test(binding.routePattern)||binding.routePattern.length>160)
      throw new Error('Invalid dynamic website route pattern.');
    const projection=projections.get(binding.collectionId);
    if(!projection || projection.isList!==true)
      throw new Error('Dynamic website collection is unavailable.');
    for(const item of Array.isArray(projection.items)?projection.items:[]) {
      const rawKey=item?.fields?.[binding.itemKeyField];
      const key=String(rawKey??'').trim().toLowerCase();
      if(!/^[a-z0-9_-]{1,80}$/.test(key)) throw new Error('Dynamic website route key is invalid.');
      const route=normalizeRoute(binding.routePattern.replace('{item}',key));
      if(!staticRoute.test(route)) throw new Error('Generated website route is invalid.');
      if(descriptors.has(route) && descriptors.get(route).sourceRoute!==sourceRoute)
        throw new Error('Dynamic website route conflicts with another page.');
      descriptors.set(route,{route,sourceRoute,dynamicItem:{collectionId:binding.collectionId,key:item.key,fields:item.fields||{}}});
    }
  }

  if(descriptors.size>500)throw new Error('Website page limit exceeded.');

  const descriptorMeta=descriptor=>{
    const page=documents[descriptor.sourceRoute]||{};
    const templatePath=typeof page.templatePath==='string'?normalizeRoute(page.templatePath):null;
    const templateRoute=templatePath&&templateByRoute.has(templatePath)
      ? templatePath
      : templateByRoute.has(descriptor.sourceRoute)?descriptor.sourceRoute:null;
    const built=templateRoute?templateByRoute.get(templateRoute):null;
    const navigation=page.navigation||{};
    const dynamic=!!descriptor.dynamicItem;
    return {
      ...descriptor,
      templateRoute,
      label:dynamic
        ? String(descriptor.dynamicItem.fields?.name||page.title||descriptor.dynamicItem.key||descriptor.route)
        : navigation.label||page.title||built?.label||(descriptor.route==='/'?'Home':descriptor.route.split('/').filter(Boolean).at(-1)),
      showInNavigation:dynamic?false:navigation.showInNavigation!==false,
      parentPath:dynamic
        ? descriptor.sourceRoute
        : typeof navigation.parentPath==='string'&&navigation.parentPath!==descriptor.route?normalizeRoute(navigation.parentPath):null,
      order:Number.isFinite(Number(navigation.order))?Number(navigation.order):0,
      isDeleted:navigation.isDeleted===true,
      template:!!built,
      dynamic
    };
  };

  const manifest=[...descriptors.values()].map(descriptorMeta).filter(page=>!page.isDeleted)
    .sort((a,b)=>a.order-b.order||a.route.localeCompare(b.route));
  const navEntries=manifest.filter(page=>page.showInNavigation);
  const storeEnabled=input.document?.store?.enabled===true && !!input.business?.key;
  const storeLabel=String(input.document?.store?.navigationLabel||'Store').trim().slice(0,40)||'Store';
  const storeRoot=storeEnabled?'/store':null;
  const storeCartIcon=['cart','bag','basket'].includes(String(input.document?.store?.cartIcon||'').toLowerCase())
    ? String(input.document.store.cartIcon).toLowerCase()
    : 'cart';
  const storeContext=storeEnabled?{
    enabled:true,label:storeLabel,cartIcon:storeCartIcon,commerceBusinessId:input.business.id,businessKey:input.business.key,
    storefrontUrl:storeRoot,cartUrl:storeRoot+'/cart'
  }:null;

  const renderEntries=[...manifest].sort((a,b)=>(a.route==='/'?-1:b.route==='/'?1:a.route.localeCompare(b.route)));
  for(const entry of renderEntries) {
    const {route,sourceRoute,dynamicItem}=entry;
    const page=documents[sourceRoute]||{};
    const built=entry.templateRoute?templateByRoute.get(entry.templateRoute):null;
    const templateDirectory=built&&built.key!=='home'?built.key:'';
    const template=await readFile(resolve(root,'dist/business-preview',templateDirectory,'index.html'),'utf8');
    const {window}=parseHTML(template);
    const doc=window.document;
    if(!built)doc.querySelector('main').replaceChildren();

    const primaryNav=doc.querySelector('[data-public-nav],#primary-nav,.nav');
    if(primaryNav){
      primaryNav.replaceChildren();
      for(const navEntry of navEntries){
        const link=doc.createElement('a');
        link.setAttribute('href',navEntry.route);
        if(navEntry.parentPath)link.setAttribute('data-nav-parent',navEntry.parentPath);
        link.textContent=navEntry.label;
        primaryNav.appendChild(link);
      }
    }

    const routeKey=route==='/'?'home':route.slice(1).replace(/\//g,'-');
    const renderPageKey=built?.key || (sourceRoute==='/'?'home':sourceRoute.slice(1).replace(/\//g,'-'));
    doc.body.dataset.pageKey=renderPageKey;
    const location=new URL('https://website.invalid'+route);
    const sandbox={window,document:doc,location,URL,URLSearchParams,console:{warn(){},error(){}},
      HTMLElement:window.HTMLElement,HTMLImageElement:window.HTMLImageElement,HTMLVideoElement:window.HTMLVideoElement,
      HTMLAnchorElement:window.HTMLAnchorElement,HTMLInputElement:window.HTMLInputElement,
      CSS:{escape:value=>String(value).replace(/[^a-zA-Z0-9_-]/g,c=>'\\'+c)},
      getComputedStyle:el=>new Proxy(el.style,{get:(style,name)=>name==='fontSize'?'16px':style[name]||''}),
      requestAnimationFrame:()=>0,cancelAnimationFrame(){},setTimeout:()=>0,clearTimeout(){}};
    window.LEGEND_PUBLIC_CMS_CONTEXT={siteKey:'business',apiBase:'https://website.invalid',businessId:input.business.id};
    const currentDocument={...input.document,pages:page&&Object.keys(page).length?{[route]:page}:{}};
    window.LEGEND_PUBLIC_CMS_RENDER_INPUT={
      document:currentDocument,business:input.business,collections:input.collections||[],
      store:storeContext,dynamicItem,pageKey:renderPageKey,server:true
    };
    vm.runInNewContext(cms,sandbox,{timeout:3000,filename:'legend-public-cms.js'});
    if(window.LEGEND_PUBLIC_CMS_RENDER_COMPLETE!==true)throw new Error('Canonical renderer did not complete.');

    const title=dynamicItem?.fields?.name||page.title||`${input.business.displayName}${routeKey==='home'?'':' | '+(built?.label||routeKey)}`;
    const description=dynamicItem?.fields?.description||page.description||'';
    doc.title=String(title);
    for(const [selector,value] of [['meta[name="description"]',description],['meta[property="og:title"]',title],['meta[property="og:description"]',description],['meta[property="og:site_name"]',input.business.displayName]])
      doc.querySelector(selector)?.setAttribute('content',String(value||''));
    doc.querySelector('link[rel="canonical"]')?.setAttribute('href','__LEGEND_CANONICAL_URL__');
    doc.querySelector('meta[property="og:url"]')?.setAttribute('content','__LEGEND_CANONICAL_URL__');
    doc.querySelector('meta[name="robots"]')?.remove();
    const faviconUrl=typeof input.document?.faviconImageDataUrl==='string' ? input.document.faviconImageDataUrl.trim() : '';
    let favicon=doc.querySelector('link[rel~="icon"]');
    if(faviconUrl){
      if(!favicon){ favicon=doc.createElement('link'); favicon.setAttribute('rel','icon'); doc.head.appendChild(favicon); }
      favicon.setAttribute('href',faviconUrl);
      favicon.removeAttribute('type');
    } else {
      favicon?.remove();
    }
    doc.querySelectorAll('script').forEach(script=>{
      if(!['/legend-public-web.js','/legend-public-cms.js'].some(path=>script.getAttribute('src')?.startsWith(path)))script.remove();
    });
    doc.querySelectorAll('a[href]').forEach(link=>{
      const href=link.getAttribute('href');
      const parsed=new URL(href,location);
      if(parsed.origin===location.origin&&parsed.pathname.startsWith('/business-preview'))
        link.setAttribute('href',parsed.pathname.replace(/^\/business-preview\/?/,'/')+parsed.hash);
    });

    const renderInput=doc.createElement('script');
    renderInput.type='application/json';
    renderInput.id='legend-cms-published-document';
    const runtimeCollections=dynamicItem
      ? (input.collections||[]).map(collection=>collection?.id===dynamicItem.collectionId
          ? {...collection,items:(Array.isArray(collection.items)?collection.items:[]).filter(item=>item?.key===dynamicItem.key)}
          : collection)
      : (input.collections||[]);
    renderInput.textContent=JSON.stringify({
      document:currentDocument,business:input.business,collections:runtimeCollections,store:storeContext,dynamicItem,
      pageKey:renderPageKey,server:false,
      runtime:{apiBase:publicApiBase,trackingAsset:publicRuntimeAssets.tracking,metaSignalAsset:publicRuntimeAssets.metaSignal}
    }).replace(/</g,'\\u003c');
    doc.body.insertBefore(renderInput,doc.querySelector('script[src^="/legend-public-cms.js"]'));
    const form=doc.querySelector('[data-website-inquiry]');
    if(form){
      form.removeAttribute('data-preview');
      form.querySelectorAll('[disabled]').forEach(element=>element.removeAttribute('disabled'));
      form.querySelector('[data-preview-notice]')?.remove();
    }
    result[route]={title:String(title),description:String(description||''),html:doc.toString()};
  }

  return {version:2,pages:result,manifest:manifest.map(({sourceRoute,dynamicItem,templateRoute,...entry})=>entry)};
}
