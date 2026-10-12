import assert from 'node:assert/strict';
import test from 'node:test';
import { buildBridgeRequest, handleWebsiteRouting } from '../../src/website-routing/bridge.mjs';

const secret = '0123456789abcdef0123456789abcdef';
const env = {
  LEGEND_WEBSITE_BRIDGE_SECRET: secret,
  LEGEND_WEBSITE_ORIGIN: 'https://masterapp-protect.azurewebsites.net',
  LEGEND_COMMERCE_ORIGIN: 'https://masterapp-parfait.azurewebsites.net'
};
const publishedPage = (html = '<main><h1>Current publication</h1></main>') =>
  new Response(html, {status:200,headers:{
    'Content-Type':'text/html; charset=utf-8',
    'X-Legend-Publication-Id':'11111111-2222-4333-8444-555555555555',
    'Cache-Control':'no-store'
  }});

test('external SaaS hostname is bridged to the single Protect origin with authenticated original host', async () => {
  const request = new Request('https://camoexterior.com/services?utm_source=test', {
    headers: {
      'User-Agent': 'bridge-test',
      'X-Legend-Original-Host': 'spoof.example',
      'X-Legend-Website-Bridge': 'spoof'
    }
  });
  const result = buildBridgeRequest(request, env);
  assert.equal(result.error, undefined);
  assert.equal(result.request.url, 'https://masterapp-protect.azurewebsites.net/services?utm_source=test');
  assert.equal(result.request.headers.get('X-Legend-Original-Host'), 'camoexterior.com');
  assert.equal(result.request.headers.get('X-Legend-Website-Bridge'), secret);
  assert.equal(result.request.headers.get('User-Agent'), 'bridge-test');
});

test('POST body and content type survive the bridge', async () => {
  const request = new Request('https://client-business.example/api/website-inquiries/public', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ submissionId: 'abc' })
  });
  const result = buildBridgeRequest(request, env);
  assert.equal(result.request.method, 'POST');
  assert.equal(result.request.headers.get('Content-Type'), 'application/json');
  assert.equal(await result.request.text(), JSON.stringify({ submissionId: 'abc' }));
});

test('LEGEND-owned hosts bypass the bridge unchanged', async () => {
  const request = new Request('https://protect.mylegnd.com/');
  let seen;
  const response = await handleWebsiteRouting(request, env, async forwarded => {
    seen = forwarded;
    return new Response('ok');
  });
  assert.equal(await response.text(), 'ok');
  assert.equal(seen.url, request.url);
  assert.equal(seen.headers.get('X-Legend-Original-Host'), null);
});

test('business custom-domain storefront stays on its domain while transport targets Parfait', async () => {
  const request = new Request('https://camoexterior.com/store/product/window-cleaning?utm_source=test');
  const result = buildBridgeRequest(request, env);
  assert.equal(result.error, undefined);
  assert.equal(result.commerce, true);
  assert.equal(result.request.url, 'https://masterapp-parfait.azurewebsites.net/store/product/window-cleaning?utm_source=test');
  assert.equal(result.request.headers.get('X-Legend-Original-Host'), 'camoexterior.com');
  assert.equal(result.request.headers.get('X-Legend-Website-Bridge'), secret);
});

test('LEGEND storefront retains Parfait while company HTML uses the publication authority', async () => {
  const store = new Request('https://mylegnd.com/store/cart');
  let storeSeen;
  await handleWebsiteRouting(store, env, async forwarded => {
    storeSeen = forwarded;
    return new Response('store');
  });
  assert.equal(storeSeen.url, 'https://masterapp-parfait.azurewebsites.net/store/cart');
  assert.equal(storeSeen.headers.get('X-Legend-Original-Host'), 'mylegnd.com');

  const home = new Request('https://mylegnd.com/');
  let homeSeen;
  await handleWebsiteRouting(home, env, async forwarded => {
    homeSeen = forwarded;
    return publishedPage();
  });
  assert.equal(homeSeen.url, 'https://masterapp-protect.azurewebsites.net/api/website-content/public/legend/page?path=%2F');
  assert.equal(homeSeen.headers.get('X-Legend-Original-Host'), null);
});

test('Protect keeps the scoped store path but transports it to Parfait on the Protect host', async () => {
  const request = new Request('https://protect.mylegnd.com/store/s/agent-store/cart');
  let seen;
  await handleWebsiteRouting(request, env, async forwarded => {
    seen = forwarded;
    return new Response('ok');
  });
  assert.equal(seen.url, 'https://masterapp-parfait.azurewebsites.net/store/s/agent-store/cart');
  assert.equal(seen.headers.get('X-Legend-Original-Host'), 'protect.mylegnd.com');
});

test('portal and client store-looking paths remain on their own applications', async () => {
  for (const url of ['https://portal.mylegnd.com/store', 'https://client.mylegnd.com/store']) {
    const request = new Request(url);
    let seen;
    await handleWebsiteRouting(request, env, async forwarded => {
      seen = forwarded;
      return new Response('ok');
    });
    assert.equal(seen.url, request.url);
    assert.equal(seen.headers.get('X-Legend-Original-Host'), null);
  }
});

test('storefront asset namespace is transported to Parfait without taking over generic website assets', async () => {
  const storeAsset = buildBridgeRequest(new Request('https://camoexterior.com/store-assets/css/site.css'), env);
  assert.equal(storeAsset.request.url, 'https://masterapp-parfait.azurewebsites.net/store-assets/css/site.css');

  const manager = buildBridgeRequest(new Request('https://business.example/commerce/manage/products?ticket=test-ticket'), env);
  assert.equal(manager.commerce, true);
  assert.equal(manager.request.url, 'https://masterapp-parfait.azurewebsites.net/commerce/manage/products?ticket=test-ticket');
  assert.equal(manager.request.headers.get('X-Legend-Original-Host'), 'business.example');

  const websiteAsset = buildBridgeRequest(new Request('https://camoexterior.com/site.css'), env);
  assert.equal(websiteAsset.request.url, 'https://masterapp-protect.azurewebsites.net/site.css');
});

test('external host fails closed when the shared bridge secret is unavailable', async () => {
  const request = new Request('https://business.example/');
  let called = false;
  const response = await handleWebsiteRouting(request, { LEGEND_WEBSITE_ORIGIN: env.LEGEND_WEBSITE_ORIGIN }, async () => {
    called = true;
    return new Response('unexpected');
  });
  assert.equal(response.status, 503);
  assert.equal(called, false);
});

test('invalid upstream configuration fails closed instead of becoming an open proxy', () => {
  const result = buildBridgeRequest(new Request('https://business.example/'), {
    LEGEND_WEBSITE_BRIDGE_SECRET: secret,
    LEGEND_WEBSITE_ORIGIN: 'https://example.com'
  });
  assert.equal(result.error.status, 503);
});

test('external HTTP requests are upgraded before proxying', async () => {
  const request = new Request('http://business.example/path?q=1');
  const response = await handleWebsiteRouting(request, env, async () => new Response('unexpected'));
  assert.equal(response.status, 308);
  assert.equal(response.headers.get('location'), 'https://business.example/path?q=1');
});

test('Parfait commerce cutover is explicit and never moves other tenant storefronts', () => {
  const staged = {
    ...env,
    LEGEND_COMMERCE_CUTOVER_ENABLED: 'true',
    LEGEND_COMMERCE_CUTOVER_HOSTS: 'shopparfait.com,www.shopparfait.com'
  };
  for (const host of ['shopparfait.com', 'www.shopparfait.com']) {
    const result = buildBridgeRequest(new Request('https://' + host + '/store/checkout/pay', {
      method: 'POST', headers: {'Content-Type': 'application/json'}, body: '{"test":true}'
    }), staged);
    assert.equal(result.error, undefined);
    assert.equal(result.sharedCommerce, true);
    assert.equal(result.request.url, 'https://masterapp-protect.azurewebsites.net/store/checkout/pay');
    assert.equal(result.request.headers.get('X-Legend-Original-Host'), host);
  }
  for (const host of ['camoexterior.com', 'business.example', 'mylegnd.com', 'protect.mylegnd.com']) {
    const result = buildBridgeRequest(new Request('https://' + host + '/store'), staged);
    assert.equal(result.sharedCommerce, false);
    assert.equal(result.request.url, 'https://masterapp-parfait.azurewebsites.net/store');
  }
  assert.equal(buildBridgeRequest(new Request('https://shopparfait.com/'), staged).request.url,
    'https://masterapp-protect.azurewebsites.net/');
});

test('A malformed or disabled cutover host list never moves commerce traffic', () => {
  const commerce = new Request('https://shopparfait.com/store');
  for (const overrides of [
    { LEGEND_COMMERCE_CUTOVER_ENABLED: 'false', LEGEND_COMMERCE_CUTOVER_HOSTS: 'shopparfait.com' },
    { LEGEND_COMMERCE_CUTOVER_ENABLED: 'true', LEGEND_COMMERCE_CUTOVER_HOSTS: 'shopparfait.com,*.com' },
    { LEGEND_COMMERCE_CUTOVER_ENABLED: 'true', LEGEND_COMMERCE_CUTOVER_HOSTS: 'shopparfait.com,shopparfait.com' },
    { LEGEND_COMMERCE_CUTOVER_ENABLED: 'true', LEGEND_COMMERCE_CUTOVER_HOSTS: 'shopparfait.com,portal.mylegnd.com' },
    { LEGEND_COMMERCE_CUTOVER_ENABLED: 'true', LEGEND_COMMERCE_CUTOVER_HOSTS: '' }
  ]) {
    const result = buildBridgeRequest(commerce, { ...env, ...overrides });
    assert.equal(result.sharedCommerce, false);
    assert.equal(result.request.url, 'https://masterapp-parfait.azurewebsites.net/store');
  }
});

test('Approved Parfait cutover also migrates private management and stale analytics transport', () => {
  const staged = {
    ...env, LEGEND_COMMERCE_CUTOVER_ENABLED: 'true',
    LEGEND_COMMERCE_CUTOVER_HOSTS: 'shopparfait.com'
  };
  for (const path of ['/commerce/manage/products?ticket=original', '/parfait-analytics/summary']) {
    const result = buildBridgeRequest(new Request('https://shopparfait.com' + path), staged);
    assert.equal(result.sharedCommerce, true);
    assert.equal(result.request.url, 'https://masterapp-protect.azurewebsites.net' + path);
  }

  for (const path of ['/commerce/manage/products?ticket=original', '/parfait-analytics/summary']) {
    const other = buildBridgeRequest(new Request('https://camoexterior.com' + path), staged);
    assert.equal(other.sharedCommerce, false);
    assert.equal(other.request.url, 'https://masterapp-parfait.azurewebsites.net' + path);
  }

  const disabled = buildBridgeRequest(new Request('https://shopparfait.com/commerce/manage/dashboard'), env);
  assert.equal(disabled.sharedCommerce, false);
  assert.equal(disabled.request.url, 'https://masterapp-parfait.azurewebsites.net/commerce/manage/dashboard');
});


test('company HTML transports only its route, never visitor credentials or campaign parameters', async () => {
  for (const hostname of ['mylegnd.com','www.mylegnd.com']) {
    let seen;
    const response=await handleWebsiteRouting(new Request(`https://${hostname}/contact/?utm_source=test`, {
      headers:{Cookie:'private=value',Authorization:'Bearer test','X-Legend-Original-Host':'spoof.example'}
    }),env,async request=>{seen=request;return publishedPage('<h1>Published</h1>');});
    assert.equal(await response.text(),'<h1>Published</h1>');
    assert.equal(seen.url,'https://masterapp-protect.azurewebsites.net/api/website-content/public/legend/page?path=%2Fcontact%2F');
    for(const header of ['Cookie','Authorization','X-Legend-Original-Host']) assert.equal(seen.headers.get(header),null);
  }
});

test('a 200 without an immutable published version or HTML body is never treated as the current website',async()=>{
  const home=new Request('https://www.mylegnd.com/');
  const stale=await handleWebsiteRouting(home,env,async()=>new Response('<h1>Static fallback</h1>',{
    status:200,headers:{'Content-Type':'text/html'}
  }));
  assert.equal(stale.status,503);
  assert.equal(stale.headers.get('Cache-Control'),'no-store');
  const invalid=await handleWebsiteRouting(home,env,async()=>new Response('not html',{
    status:200,headers:{'X-Legend-Publication-Id':'valid','Content-Type':'text/plain'}
  }));
  assert.equal(invalid.status,503);
  const fresh=await handleWebsiteRouting(home,env,async()=>publishedPage());
  assert.equal(fresh.status,200);
  assert.equal(fresh.headers.get('X-Legend-Publication-Id'),'11111111-2222-4333-8444-555555555555');
});

test('Studio, static assets, business previews and other applications retain their origins',async()=>{
  for(const path of ['/?legendEdit=test-ticket','/contact?legendEdit=test-ticket&legendAudit=1','/site.css','/legend-public-cms.js','/business-preview/']) {
    const request=new Request('https://www.mylegnd.com'+path);
    let seen;
    await handleWebsiteRouting(request,env,async value=>{seen=value;return new Response('ok');});
    assert.equal(seen.url,request.url);
  }
});

test('company renderer failure cannot fall back to stale static HTML',async()=>{
  let calls=0;
  const response=await handleWebsiteRouting(new Request('https://www.mylegnd.com/'),env,async()=>{calls++;throw new Error('offline');});
  assert.equal(response.status,503);
  assert.equal(response.headers.get('Cache-Control'),'no-store');
  assert.equal(calls,1);
  const missing=await handleWebsiteRouting(new Request('https://www.mylegnd.com/missing'),env,async()=>new Response('missing',{status:404}));
  assert.equal(missing.status,404);
});

test('HEAD remains HEAD and company page writes are not proxied',async()=>{
  let seen;
  await handleWebsiteRouting(new Request('https://www.mylegnd.com/',{method:'HEAD'}),env,async request=>{seen=request;return publishedPage('');});
  assert.equal(seen.method,'HEAD');
  const response=await handleWebsiteRouting(new Request('https://www.mylegnd.com/',{method:'POST'}),env,async()=>{throw new Error('unexpected');});
  assert.equal(response.status,405);
});


test('explicit index files cannot expose the old static homepage',async()=>{
  for(const [path,route] of [['/index.html','/'],['/contact/index.html','/contact/']]) {
    let seen;
    await handleWebsiteRouting(new Request('https://www.mylegnd.com'+path),env,async request=>{seen=request;return publishedPage();});
    assert.equal(new URL(seen.url).pathname,'/api/website-content/public/legend/page');
    assert.equal(new URL(seen.url).searchParams.get('path'),route);
  }
});
