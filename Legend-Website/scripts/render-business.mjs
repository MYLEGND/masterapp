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
  const normalizeRoute=route=>(route.replace(/\/$/,'')||'/');
  const templateByRoute=new Map(businessPages.map(page=>[page.key==='home'?'/':'/'+page.key,page]));
  const routeSet=new Set(templateByRoute.keys());
  for(const rawRoute of Object.keys(documents)) {
    if(!/^\/(?:[a-z0-9_-]+\/?)*$/.test(rawRoute)||rawRoute.length>160)throw new Error('Invalid website page route.');
    routeSet.add(normalizeRoute(rawRoute));
  }
  if(routeSet.size>100)throw new Error('Website page limit exceeded.');
  const routeMeta=route=> {
    const page=documents[route]||{};
    const page=documents[route]||{};
    const templateRoute=typeof page.templatePath==='string'&&templateByRoute.has(normalizeRoute(page.templatePath))?normalizeRoute(page.templatePath):route;
    const built=templateByRoute.get(templateRoute);
    const navigation=page.navigation||{};
    return {
      route,
      label:navigation.label||page.title||built?.label||(route==='/'?'Home':route.split('/').filter(Boolean).at(-1)),
      showInNavigation:navigation.showInNavigation!==false,
      parentPath:typeof navigation.parentPath==='string'&&navigation.parentPath!==route?normalizeRoute(navigation.parentPath):null,
      order:Number.isFinite(Number(navigation.order))?Number(navigation.order):0,
      isDeleted:navigation.isDeleted===true,
      template:!!built
    };
  };
  const manifest=[...routeSet].map(routeMeta).filter(page=>!page.isDeleted)
    .sort((a,b)=>a.order-b.order||a.route.localeCompare(b.route));
  const routes=manifest.map(page=>page.route);
  const navigation=manifest.filter(page=>page.showInNavigation)
    .map(page=>`<a href="${page.route}"${page.parentPath?` data-nav-parent="${page.parentPath}"`:''}>${page.label}</a>`).join('');
  for(const route of routes) {
    const key=route==='/'?'home':route.slice(1).replace(/\//g,'-');
    const built=templateByRoute.get(route);
    const template=await readFile(resolve(root,'dist/business-preview',built&&key!=='home'?key:'','index.html'),'utf8');
    const {window}=parseHTML(template);
    const doc=window.document;
    if(!built)doc.querySelector('main').replaceChildren();
    const primaryNav=doc.querySelector('[data-public-nav],#primary-nav,.nav');
    if(primaryNav) primaryNav.innerHTML=navigation;
    doc.body.dataset.pageKey=key;
    const location=new URL('https://website.invalid'+route);
    // The renderer needs DOM constructors; neither network nor process is exposed.
    const sandbox={window,document:doc,location,URL,URLSearchParams,console:{warn(){},error(){}},
      HTMLElement:window.HTMLElement,HTMLImageElement:window.HTMLImageElement,HTMLVideoElement:window.HTMLVideoElement,
      HTMLAnchorElement:window.HTMLAnchorElement,HTMLInputElement:window.HTMLInputElement,
      CSS:{escape:value=>String(value).replace(/[^a-zA-Z0-9_-]/g,c=>'\\'+c)},
      getComputedStyle:el=>new Proxy(el.style,{get:(style,name)=>name==='fontSize'?'16px':style[name]||''}),
      requestAnimationFrame:()=>0,cancelAnimationFrame(){},setTimeout:()=>0,clearTimeout(){}};
    window.LEGEND_PUBLIC_CMS_CONTEXT={siteKey:'business',apiBase:'https://website.invalid',businessId:input.business.id};
    window.LEGEND_PUBLIC_CMS_RENDER_INPUT={document:input.document,business:input.business,pageKey:key,server:true};
    vm.runInNewContext(cms,sandbox,{timeout:3000,filename:'legend-public-cms.js'});
    if(window.LEGEND_PUBLIC_CMS_RENDER_COMPLETE!==true)throw new Error('Canonical renderer did not complete.');
    const title=page.title||`${input.business.displayName}${key==='home'?'':' | '+(built?.label||key)}`;
    const description=page.description||'';
    doc.title=title;
    for(const [selector,value] of [['meta[name="description"]',description],['meta[property="og:title"]',title],['meta[property="og:description"]',description],['meta[property="og:site_name"]',input.business.displayName]])doc.querySelector(selector)?.setAttribute('content',value);
    doc.querySelector('link[rel="canonical"]')?.setAttribute('href','__LEGEND_CANONICAL_URL__');
    doc.querySelector('meta[property="og:url"]')?.setAttribute('content','__LEGEND_CANONICAL_URL__');
    doc.querySelector('meta[name="robots"]')?.remove();
    doc.querySelector('link[rel="icon"]')?.remove(); // A business does not inherit the LEGEND company mark.
    doc.querySelectorAll('script').forEach(script=>{
      if(!['/legend-public-web.js','/legend-public-cms.js'].some(path=>script.getAttribute('src')?.startsWith(path)))script.remove();
    });
    doc.querySelectorAll('a[href]').forEach(link=>{
      const href=link.getAttribute('href');
      const parsed=new URL(href,location);
      if(parsed.origin===location.origin&&parsed.pathname.startsWith('/business-preview'))link.setAttribute('href',parsed.pathname.replace(/^\/business-preview\/?/,'/')+parsed.hash);
    });
    const renderInput=doc.createElement('script');
    renderInput.type='application/json';
    renderInput.id='legend-cms-published-document';
    const currentDocument={...input.document,pages:page&&Object.keys(page).length?{[route]:page}:{}};
    renderInput.textContent=JSON.stringify({document:currentDocument,business:input.business,pageKey:key,server:false,runtime:{apiBase:publicApiBase,trackingAsset:publicRuntimeAssets.tracking,metaSignalAsset:publicRuntimeAssets.metaSignal}}).replace(/</g,'\\u003c');
    doc.body.insertBefore(renderInput,doc.querySelector('script[src^="/legend-public-cms.js"]'));
    const form=doc.querySelector('[data-website-inquiry]');
    if(form){
      form.removeAttribute('data-preview');
      form.querySelectorAll('[disabled]').forEach(element=>element.removeAttribute('disabled'));
      form.querySelector('[data-preview-notice]')?.remove();
    }
    result[route]={title,description,html:doc.toString()};
  }
  return {version:2,pages:result,manifest};
}

