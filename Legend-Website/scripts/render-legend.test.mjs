import test from 'node:test';
import assert from 'node:assert/strict';
import {parseHTML} from 'linkedom';
import {compileWebsite} from './render-business.mjs';

const node=(id,type,tag,extra={})=>({id,type,tag,signals:[],style:{},breakpointStyles:{},layout:{mode:'stack',direction:'column'},breakpointLayouts:{},children:[],...extra});
const publication=(headline='Published coaching website')=>({
  siteKey:'legend',
  ctaCatalog:{options:[{key:'legend_contact',label:'Contact',href:'/contact',target:'_self'}]},
  document:{version:3,theme:{},reusableComponents:{},collections:{},
    breakpoints:[{key:'mobile',label:'Mobile',minWidth:0,maxWidth:767,isSystem:true}],
    store:{enabled:false},
    shell:{header:[node('header','container','header',{className:'site-header',children:[
      node('nav','container','nav',{className:'nav',children:[node('nav.contact','cta','a',{text:'Contact',href:'/contact',actionKey:'legend_contact'})]})
    ]})],footer:[node('footer','container','footer',{children:[node('footer.text','text','p',{text:'Published footer'})]})]},
    pages:{'/':{title:'Published title',description:'Published description',navigation:{label:'Home'},composition:[
      node('hero','section','section',{children:[node('hero.title','heading','h1',{text:headline,style:{fontSize:56},breakpointStyles:{mobile:{fontSize:30}}}),node('hero.cta','cta','a',{text:'Get my plan',href:'/contact',actionKey:'legend_contact'})]})
    ]},'/contact':{title:'Contact',description:'Inquire',navigation:{label:'Contact'},composition:[node('inquiry','form','form',{title:'Send inquiry',systemKey:'canonical_inquiry'})]},
    '/removed':{title:'Removed',navigation:{isDeleted:true},composition:[]}}
  }
});

test('LEGEND first response contains published HTML, metadata, responsive content and functioning inquiry markup',async()=>{
  const result=await compileWebsite(publication());
  assert.deepEqual(Object.keys(result.pages),['/','/contact']);
  const home=parseHTML(result.pages['/'].html).document;
  assert.equal(home.querySelector('h1').textContent,'Published coaching website');
  assert.equal(home.title,'Published title');
  assert.equal(home.querySelector('meta[name="description"]').content,'Published description');
  assert.equal(home.documentElement.hidden,false);
  assert.ok(!home.body.textContent.includes('BUILD A LIFE WORTHY OF YOUR LEGACY'));
  assert.equal(home.querySelector('[data-website-action-key="legend_contact"]').getAttribute('href'),'/contact');
  assert.ok(home.querySelector('script[src="/js/page-health.js"]'));
  assert.ok(home.querySelector('script[src="/js/legend-site-tools.js"]'));
  assert.ok(home.querySelector('script[type="application/ld+json"]'));
  const embedded=JSON.parse(home.querySelector('#legend-cms-published-document').textContent);
  assert.equal(embedded.siteKey,'legend');
  assert.equal(embedded.business,undefined);
  assert.equal(embedded.document.pages['/'].composition[0].children[0].breakpointStyles.mobile.fontSize,30);
  assert.equal(embedded.runtime.apiBase,'https://masterapp-protect.azurewebsites.net');
  assert.equal(embedded.ctaCatalog.options[0].key,'legend_contact');
  const contact=parseHTML(result.pages['/contact'].html).document;
  assert.ok(contact.querySelector('form[data-website-inquiry]'));
  assert.equal(contact.querySelector('script[src="/js/page-health.js"]').getAttribute('data-route'),'/contact');
  assert.ok(contact.querySelector('script[src^="/legend-public-inquiry.js"]'));
  assert.equal(contact.querySelector('form[data-website-inquiry] [disabled]'),null);
  assert.equal(contact.querySelector('[data-preview-notice]'),null);
});

test('a different publication replaces the page without retaining old content',async()=>{
  const first=await compileWebsite(publication('First published'));
  const second=await compileWebsite(publication('Second published'));
  assert.equal(parseHTML(first.pages['/'].html).document.querySelector('h1').textContent,'First published');
  assert.equal(parseHTML(second.pages['/'].html).document.querySelector('h1').textContent,'Second published');
  assert.ok(!second.pages['/'].html.includes('First published'));
});

test('company publication refuses legacy defaults and escapes embedded content',async()=>{
  await assert.rejects(compileWebsite({siteKey:'legend',document:{pages:{}}}),/Canonical published content/);
  const result=await compileWebsite(publication('</script><script>unsafe()</script>'));
  const html=parseHTML(result.pages['/'].html).document;
  assert.equal(html.querySelector('h1').textContent,'</script><script>unsafe()</script>');
  assert.ok(![...html.querySelectorAll('script')].some(script=>script.textContent==='unsafe()'));
});
