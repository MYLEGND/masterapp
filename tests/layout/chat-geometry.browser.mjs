// Run with: node tests/layout/chat-geometry.browser.mjs (requires Playwright Chromium).
// Synthetic content only; loads production styles and interaction controllers.
import { chromium } from 'playwright';
import { readFileSync } from 'node:fs';
import assert from 'node:assert/strict';
const root = new URL('../../', import.meta.url);
const read = path => readFileSync(new URL(path, root), 'utf8');
const styles = ['AgentPortal/wwwroot/lib/bootstrap/dist/css/bootstrap.min.css',
  'Legend-Design/legend-web-foundation.css', 'Legend-Design/legend-app-shell.css',
  'AgentPortal/wwwroot/css/site.css', 'AgentPortal/wwwroot/css/legend-founder-ai.css',
  'SHARED/wwwroot/css/dashboard-home-shared.css'].map(read).join('\n');
const view = read('AgentPortal/Views/Shared/_LegendFounderAiModal.cshtml')
  .split('\n').map(line => line.includes('@') ? (line.trim().endsWith('>') ? '>' : '') : line).join('\n');
const browser = await chromium.launch({headless:true});
let cases = 0;
try {
  for (const width of [320, 390, 820, 860, 900, 901, 1363]) {
    const page = await browser.newPage({viewport:{width,height:844}});
    page.setDefaultTimeout(5000);
    page.on('pageerror', error => console.error(error.message));
    await page.route('**/*', route => route.fulfill({status:200,contentType:'application/json',body:'[]'}));
    await page.route('http://localhost/', route => route.fulfill({contentType:'text/html',body:`<html data-legend-modal-region="true"><head><style>${styles}</style></head>
      <body class="legend-app"><header class="legend-global-nav">Test navigation</header>
      <button id="legendFounderAiTrigger">Open AI</button><main class="legend-app-content"></main>${view}</body></html>`}));
    await page.goto('http://localhost/');
    await page.addScriptTag({content:read('AgentPortal/wwwroot/lib/bootstrap/dist/js/bootstrap.bundle.min.js')});
    await page.addScriptTag({content:read('SHARED/wwwroot/js/legend-modal.js')});
    await page.addScriptTag({content:read('AgentPortal/wwwroot/js/legend-founder-ai.js')});
    await page.locator('#legendFounderAiTrigger').click();
    await page.locator('#legendFounderAiModal').waitFor({state:'visible'});
    await page.waitForTimeout(750);
    const measure = () => page.evaluate(() => {
      const rect = selector => {const r=document.querySelector(selector).getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height,bottom:r.bottom,right:r.right};};
      return {shell:rect('.legend-founder-ai-shell'),title:rect('#legendFounderAiTitle'),input:rect('#legendFounderAiInput'),send:rect('#legendFounderAiSend'),
        scrim:{...rect('#legendFounderAiSidebarScrim'),opacity:getComputedStyle(document.querySelector('#legendFounderAiSidebarScrim')).opacity},
        sendRadius:getComputedStyle(document.querySelector('#legendFounderAiSend')).borderRadius};
    });
    let m=await measure();
    assert(m.shell.y>=-1 && m.shell.bottom<=845, `${width}: shell outside viewport ${JSON.stringify(m)}`);
    assert(m.title.height>0 && m.title.y>=m.shell.y, `${width}: title hidden`);
    assert(m.input.bottom<=m.shell.bottom && m.send.bottom<=m.shell.bottom, `${width}: composer clipped ${JSON.stringify(m)}`);
    assert.equal(m.sendRadius,'50%',`${width}: send shape overridden`);
    if(width<=900){
      assert.equal(m.scrim.opacity,'0',`${width}: scrim rendered as extra close`);
      const menu=page.locator('#legendFounderAiMobileMenu');
      await menu.click();assert.equal(await menu.getAttribute('aria-expanded'),'true');
      await menu.click();assert.equal(await menu.getAttribute('aria-expanded'),'false');
      const handle=page.locator('.legend-mobile-sheet-handle');
      const handleBox=await handle.boundingBox();
      await page.mouse.move(handleBox.x+handleBox.width/2,handleBox.y+10);
      await page.mouse.down();await page.mouse.move(handleBox.x+handleBox.width/2,handleBox.y+110,{steps:10});await page.mouse.up();await page.waitForTimeout(320);
      assert.equal(await page.locator('.legend-founder-ai-shell').getAttribute('data-legend-sheet-snap'),'half');
      await handle.focus();await page.keyboard.press('Enter');await page.waitForTimeout(320);
      await handle.focus();
      await page.keyboard.press('Enter');await page.waitForTimeout(320);
      m=await measure();assert(m.input.bottom<=845,`${width}: half sheet composer clipped ${JSON.stringify(m)}`);
      await page.keyboard.press('Enter');await page.waitForTimeout(320);
      // Model Safari keyboard + visual viewport panning through its public events.
      await page.evaluate(()=>{Object.defineProperty(window.visualViewport,'height',{configurable:true,value:420});Object.defineProperty(window.visualViewport,'offsetTop',{configurable:true,value:20});window.visualViewport.dispatchEvent(new Event('resize'));window.visualViewport.dispatchEvent(new Event('scroll'));});
      m=await measure();assert(m.input.bottom<=441 && m.send.bottom<=441,`${width}: keyboard clips composer ${JSON.stringify(m)}`);
    }
    await page.locator('.legend-founder-ai-close').click();
    await page.locator('#legendFounderAiModal').waitFor({state:'hidden'});
    await page.locator('#legendFounderAiTrigger').click();
    await page.locator('#legendFounderAiModal').waitFor({state:'visible'});
    console.log(`PASS ${width}px: bounds, title, composer, control shape, open/close${width<=900?', hamburger, half sheet, keyboard':''}`);
    cases++;await page.close();
  }
  for(const width of [390,1363]) {
    const page=await browser.newPage({viewport:{width,height:844}});
    await page.setContent(`<html data-legend-modal-region="true"><style>${styles}</style><body class="legend-app"><div class="messaging-command-center-modal" data-legend-modal-surface data-legend-modal-open="true"><div class="messaging-command-center-window" data-legend-modal-panel><header class="messaging-command-center-header"><h2>Messages</h2></header><div class="messaging-command-center-grid"><aside class="messaging-command-center-sidebar"><input class="messaging-command-center-search" placeholder="Search"><div class="messaging-conversation-list"><button class="messaging-conversation-item">Test contact</button></div></aside><section class="messaging-command-center-thread"><div class="messaging-thread-empty"><h3>Choose a contact</h3></div><form class="messaging-send-form"><textarea placeholder="Write a message"></textarea><div class="messaging-send-actions"><button class="messaging-primary-button">Send</button></div></form></section></div></div></div><div id="surface" style="background:var(--legend-app-surface)"></div><div id="raised" style="background:var(--legend-app-surface-elevated);color:var(--legend-app-on-dark)"></div></body></html>`);
    const colors=await page.evaluate(()=>{
      const style=s=>getComputedStyle(document.querySelector(s));
      return {surface:style('#surface').backgroundColor,raised:style('#raised').backgroundColor,text:style('#raised').color,sidebar:style('.messaging-command-center-sidebar').backgroundColor,input:style('.messaging-send-form textarea').backgroundColor,search:style('.messaging-command-center-search').backgroundColor,title:style('.messaging-thread-empty h3').color};
    });
    assert.equal(colors.sidebar,colors.surface,`${width}: sidebar forks palette`);
    assert.equal(colors.input,colors.raised,`${width}: composer forks palette`);
    assert.equal(colors.search,colors.raised,`${width}: search forks palette`);
    assert.equal(colors.title,colors.text,`${width}: title lacks contrast`);
    console.log(`PASS ${width}px Messages: canonical sidebar, fields and text palette`);
    cases++;await page.close();
  }
} finally {await browser.close();}
console.log(`${cases} viewport cases passed`);
