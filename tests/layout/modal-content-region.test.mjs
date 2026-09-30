import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import vm from 'node:vm';
const script=readFileSync(new URL('../../SHARED/wwwroot/js/legend-modal.js',import.meta.url),'utf8');
function implementation(name) {
  const start=script.indexOf(`  function ${name}(`);
  assert(start>=0);
  const end=script.indexOf('\n  function ',start+1);
  return script.slice(start,end<0?undefined:end);
}
function fixture({height=900,top=200,footerTop=1000,contentTop=220,viewport,impersonation,mobile=false}={}) {
  const values=new Map();let writes=0;const attributes={};
  const element=(rect)=>({getBoundingClientRect:()=>rect});
  const context={window:{innerHeight:height,visualViewport:viewport,matchMedia:()=>({matches:mobile})},
    document:{documentElement:{style:{getPropertyValue:name=>values.get(name),setProperty:(name,value)=>{values.set(name,value);writes++;}},setAttribute:(key,value)=>attributes[key]=value}},
    header:element({top:0,bottom:top,height:top}),footer:element({top:footerTop,bottom:footerTop+60,height:60}),content:element({top:contentTop}),impersonation:impersonation?element(impersonation):null};
  vm.createContext(context);
  for(const name of ['writeVariable','syncViewportOffsets'])vm.runInContext(implementation(name),context);
  return {context,values,attributes,writes:()=>writes,refresh:()=>context.syncViewportOffsets(),number:key=>parseFloat(values.get(key))};
}
test('dialog region begins below banner/content and excludes visible footer',()=>{
  const f=fixture({footerTop:820});f.refresh();
  assert.equal(f.number('--legend-modal-area-start'),244);
  assert.equal(f.number('--legend-modal-safe-height'),552);
  assert.equal(f.number('--legend-modal-area-end'),104);
  assert.equal(f.number('--legend-modal-safe-center'),520);
});
test('offscreen footer never wastes visible workspace and scrolled header clears',()=>{
  const f=fixture({top:0,contentTop:-600,footerTop:1400});f.refresh();
  assert.equal(f.number('--legend-modal-area-start'),24);
  assert.equal(f.number('--legend-modal-safe-height'),852);
});
test('keyboard/visual viewport bounds clamp height without a 280px minimum',()=>{
  const f=fixture({height:900,top:290,contentTop:300,mobile:true,viewport:{offsetTop:100,height:240}});f.refresh();
  assert.equal(f.number('--legend-modal-area-start'),310);
  assert.equal(f.number('--legend-modal-safe-height'),20);
  assert.equal(f.number('--legend-modal-area-end'),570);
});
test('banner filling short viewport yields zero available height rather than overflow',()=>{
  const f=fixture({height:300,top:500,contentTop:520,mobile:true});f.refresh();
  assert.equal(f.number('--legend-modal-safe-height'),0);
  assert.equal(f.number('--legend-modal-area-start'),300);
});
test('sticky impersonation remains a boundary after global header scrolls out',()=>{
  const f=fixture({top:0,contentTop:-50,impersonation:{top:0,bottom:48,height:48}});f.refresh();
  assert.equal(f.number('--legend-modal-area-start'),72);
});
test('unchanged geometry does not trigger style writes and resize recalculates',()=>{
  const f=fixture();f.refresh();const writes=f.writes();f.refresh();assert.equal(f.writes(),writes);
  f.context.header={getBoundingClientRect:()=>({top:0,bottom:320,height:320})};f.refresh();assert.equal(f.number('--legend-modal-area-start'),344);
});
test('layout notifications coalesce into one scheduled geometry update',()=>{
  let scheduled,frames=0,updates=0;
  const context={viewportSyncFrame:0,window:{requestAnimationFrame:callback=>{frames++;scheduled=callback;return 1;}},syncViewportOffsets:()=>updates++};vm.createContext(context);vm.runInContext(implementation('scheduleViewportOffsets'),context);
  context.scheduleViewportOffsets();context.scheduleViewportOffsets();context.scheduleViewportOffsets();assert.equal(frames,1);scheduled();assert.equal(updates,1);assert.equal(context.viewportSyncFrame,0);
});
test('shared mobile navigation is one full-height internally scrolling command surface',()=>{
  const css=readFileSync(new URL('../../SHARED/wwwroot/css/dashboard-home-shared.css',import.meta.url),'utf8');
  assert.match(css,/@media \(max-width: 840px\)[\s\S]*grid-template-areas:[\s\S]*"brand motto toggle"[\s\S]*"panel panel panel"/);
  assert.match(css,/\.legend-global-nav\.mobile-open \{[\s\S]*position: fixed;[\s\S]*height: 100dvh;[\s\S]*overflow: hidden;/);
  assert.match(css,/\.legend-global-nav\.mobile-open \.legend-mobile-nav-panel \{[\s\S]*overflow-y: auto;[\s\S]*overscroll-behavior: contain;[\s\S]*touch-action: pan-y;/);
  assert.match(css,/\.legend-global-nav\[data-legend-mobile-nav-integrated\] \.navbar-left,[\s\S]*\.explore-list \{[\s\S]*display: contents !important;/);
  assert.match(css,/\.legend-global-nav\[data-legend-mobile-nav-integrated\] \.nav-item-explore,[\s\S]*display: none !important;/);
});

test('global nav owns mobile page locking and integrates Explore without a nested sheet',()=>{
  const navScript=readFileSync(new URL('../../SHARED/wwwroot/js/legend-global-navigation.js',import.meta.url),'utf8');
  assert.match(navScript,/panel\.className = 'legend-mobile-nav-panel'/);
  assert.match(navScript,/\[drawer, left, right\]\.filter\(Boolean\)\.forEach\(node => panel\.appendChild\(node\)\)/);
  assert.match(navScript,/const scrollOwner = 'legend-global-navigation'/);
  assert.match(navScript,/window\.LegendModal\?\.lockPageScroll\?\.\(scrollOwner\)/);
  assert.match(navScript,/window\.LegendModal\?\.unlockPageScroll\?\.\(scrollOwner\)/);
  assert.match(navScript,/if \(isMobile\(\)\) \{[\s\S]*ownerNav\?\.__legendOpenNavigation\?\.\(\);[\s\S]*return;/);
  assert.match(navScript,/ownerNav\.__legendCloseNavigation\?\.\(\{ restoreFocus \}\)/);

  for(const file of [
    'AgentPortal/Views/Shared/_Layout.cshtml',
    'AgentPortal/Views/Shared/_ClientWorkspaceLayout.cshtml',
    'ClientApp/Views/Shared/_Layout.cshtml'
  ]){
    const source=readFileSync(new URL('../../'+file,import.meta.url),'utf8');
    assert.equal(source.split('data-legend-explore-close').length-1,1,file);
    assert.match(source,/id="exploreDrawer"[^>]*aria-hidden="true"/);
  }
});

test('mobile navigation dismissal restores page scroll and preserves link navigation',()=>{
  const navScript=readFileSync(new URL('../../SHARED/wwwroot/js/legend-global-navigation.js',import.meta.url),'utf8');
  assert.match(navScript,/window\.addEventListener\('resize', syncResponsiveState/);
  assert.match(navScript,/window\.visualViewport\?\.addEventListener\('resize', syncResponsiveState/);
  assert.match(navScript,/window\.addEventListener\('pagehide'[\s\S]*unlockPageScroll/);
  assert.match(navScript,/panel\.addEventListener\('click'[\s\S]*close\(\)/);

  const listHandler=navScript.slice(
    navScript.indexOf("list.addEventListener('click'"),
    navScript.indexOf("search?.addEventListener('input'")
  );
  assert.match(listHandler,/event\.target\.closest\('\.explore-item'\)[\s\S]*closeDrawer\(\)/);
  assert.doesNotMatch(listHandler,/preventDefault\(/);

  for(const file of [
    'AgentPortal/Views/Shared/_Layout.cshtml',
    'AgentPortal/Views/Shared/_ClientWorkspaceLayout.cshtml',
    'ClientApp/Views/Shared/_Layout.cshtml'
  ]){
    const source=readFileSync(new URL('../../'+file,import.meta.url),'utf8');
    const exploreLinks=[...source.matchAll(/<a\b[^>]*class="[^"]*explore-item[^"]*"[^>]*>/g)].map(match=>match[0]);
    assert(exploreLinks.length>0,file);
    for(const tag of exploreLinks){
      assert.match(tag,/(?:href\s*=|asp-controller\s*=)/,file+': '+tag);
    }
  }
});

test('legacy design shell no longer owns a competing mobile Explore drawer',()=>{
  const shell=readFileSync(new URL('../../Legend-Design/legend-app-shell.css',import.meta.url),'utf8');
  const mobile=shell.match(/@media\(max-width:840px\)\{[\s\S]*?\n\}/g)?.join('\n') || '';
  assert.doesNotMatch(mobile,/\.explore-drawer/);
  assert.doesNotMatch(mobile,/\.explore-list/);
  assert.doesNotMatch(mobile,/\.explore-close/);
});

test('every authenticated mobile page consumes the full width and retains an overflow escape hatch',()=>{
  const shell=readFileSync(new URL('../../Legend-Design/legend-app-shell.css',import.meta.url),'utf8');
  const css=readFileSync(new URL('../../SHARED/wwwroot/css/dashboard-home-shared.css',import.meta.url),'utf8');

  const mobileShell=shell.slice(shell.indexOf('@media(max-width:768px)'));
  assert.doesNotMatch(mobileShell,/body\.legend-app\{[^}]*overflow-x:clip/);
  assert.match(shell,/@media\(min-width:769px\)\{[\s\S]*body\.legend-app\{overflow-x:clip\}/);
  assert.match(shell,/@media\(max-width:768px\)\{[\s\S]*body\.legend-app\{[\s\S]*overflow-x:visible/);

  const contractStart=css.indexOf('Canonical authenticated mobile width contract');
  const contractEnd=css.indexOf('Shared native-style mobile sheet',contractStart);
  assert(contractStart>=0 && contractEnd>contractStart);
  const contract=css.slice(contractStart,contractEnd);

  assert.match(contract,/body\.legend-app \.legend-app-content > main \{[\s\S]*width: 100%[\s\S]*max-width: 100%[\s\S]*min-width: 0[\s\S]*overflow-x: auto/);
  assert.match(contract,/> main > :not\(script\):not\(style\):not\(link\):not\(\.modal\):not\(\[data-legend-modal-surface\]\) \{[\s\S]*width: 100%[\s\S]*margin-inline: 0/);
  assert.match(contract,/\.dashboard-page-shell,[\s\S]*\.home-container,[\s\S]*\.legend-workspace-page,[\s\S]*\.finance-shell,[\s\S]*width: 100%/);
  assert.match(contract,/\.table-responsive\) \{[\s\S]*overflow-x: auto/);
  assert.doesNotMatch(contract,/overflow-x:\s*(?:clip|hidden)/);
});

test('canonical mobile action authority is explicit and cannot capture unrelated page controls',()=>{
  const css=readFileSync(new URL('../../SHARED/wwwroot/css/dashboard-home-shared.css',import.meta.url),'utf8');
  const authorityStart=css.indexOf('Canonical authenticated-mobile action authority');
  const authorityEnd=css.indexOf('One close/collapse glyph everywhere',authorityStart);
  assert(authorityStart>=0 && authorityEnd>authorityStart);
  const authority=css.slice(authorityStart,authorityEnd);
  const actionAuthorityEnd=css.indexOf('html[data-legend-modal-region] body.legend-app .modal .modal-header',authorityStart);
  const actionAuthority=css.slice(authorityStart,actionAuthorityEnd);

  assert.doesNotMatch(actionAuthority,/!important/);
  assert.match(authority,/\.client-create-actions/);
  assert.match(authority,/\.dashboard-page-shell \.search-actions/);
  assert.match(authority,/\.drawer\.crm-qv-shell \.drawer-top-actions/);
  assert.match(authority,/#rbShell \.rb-hero \.hero-actions/);
  assert.match(authority,/\.savings-illustration-footer/);

  assert.doesNotMatch(authority,/\[class\$="-actions"\]/);
  assert.doesNotMatch(authority,/\[class\*="-actions "\]/);
  assert.doesNotMatch(authority,/\[class\$="__actions"\]/);
  assert.doesNotMatch(authority,/\[class\$="-buttons"\]/);
  assert.doesNotMatch(authority,/\[class\$="-action-row"\]/);
  assert.doesNotMatch(authority,/\.d-grid/);
  assert.doesNotMatch(authority,/\.btn-group/);

  for(const file of ['AgentPortal/Views/Shared/_Layout.cshtml','ClientApp/Views/Shared/_Layout.cshtml']){
    const layout=readFileSync(new URL('../../'+file,import.meta.url),'utf8');
    const featureBoundary=layout.indexOf('RenderSection("Styles"');
    const shared=layout.indexOf('~/_content/Shared/css/dashboard-home-shared.css');
    const booking=layout.indexOf('~/css/qv-booking.css');
    assert(featureBoundary>=0 && shared>featureBoundary,file+': shared mobile authority must follow page styles');
    assert(booking>=0 && shared>booking,file+': shared mobile authority must follow booking feature CSS');
  }
});

test('shared mobile structure defeats desktop Home grids without restoring per-page mobile CSS',()=>{
  const css=readFileSync(new URL('../../SHARED/wwwroot/css/dashboard-home-shared.css',import.meta.url),'utf8');

  assert.match(css,/@media \(max-width: 1180px\) \{[\s\S]*\.home-command-page \.home-hero-panel \.dashboard-command-center-top,[\s\S]*grid-template-columns: minmax\(0, 1fr\)/);
  assert.match(css,/@media \(max-width: 767\.98px\) \{[\s\S]*\.home-command-page \.home-hero-summary \{[\s\S]*grid-template-columns: minmax\(0, 1fr\)/);
  assert.match(css,/\.client-portal \.home-command-page \.home-hero-summary \{[\s\S]*grid-template-columns: repeat\(2, minmax\(0, 1fr\)\)/);
  assert.match(css,/\.client-portal \.home-command-page \.home-hero-summary \.dashboard-stat-card:last-child \{[\s\S]*grid-column: 1 \/ -1/);
  assert.match(css,/\.home-command-page \.dashboard-hero-title,[\s\S]*white-space: normal/);

  const agentHome=readFileSync(new URL('../../AgentPortal/wwwroot/css/home-command-page.css',import.meta.url),'utf8');
  assert.doesNotMatch(agentHome,/@media \(max-width: 1180px\)[\s\S]*\.home-command-page \.home-hero-panel \.dashboard-command-center-top/);

  const clientMobile=readFileSync(new URL('../../ClientApp/wwwroot/css/client-mobile.css',import.meta.url),'utf8');
  assert.doesNotMatch(clientMobile,/\.client-portal \.dashboard-command-center-top[\s\S]*grid-template-columns/);
});

test('authenticated mobile pages use full width and explicit compact action grids',()=>{
  const shared=readFileSync(new URL('../../SHARED/wwwroot/css/dashboard-home-shared.css',import.meta.url),'utf8');
  const mobile=shared.slice(shared.indexOf('@media (max-width: 768px)'));

  assert.match(mobile,/\.legend-app-content :where\(\.container,\.container-fluid\) \{[\s\S]*max-width: none;[\s\S]*padding-inline: 2px;/);
  assert.match(mobile,/\.legend-app-content :where\(\.row\) \{[\s\S]*margin-inline: 0;/);
  assert.match(mobile,/\.client-create-actions,[\s\S]*\.wa-modal-toggle-group,[\s\S]*grid-template-columns: repeat\(2, minmax\(0, 1fr\)\)/);
  assert.match(mobile,/\.dashboard-hero-stats,[\s\S]*\.membership-billing-summary[\s\S]*grid-template-columns: repeat\(2, minmax\(0, 1fr\)\)/);
  assert.doesNotMatch(mobile,/\[class\$="-actions"\]/);
  assert.doesNotMatch(mobile,/\[class\*="-actions"\]/);
});

test('finance mobile source uses horizontal space before adding vertical scroll',()=>{
  const finance=readFileSync(new URL('../../SHARED/wwwroot/css/legend-finance-shared.css',import.meta.url),'utf8');

  assert.match(finance,/@media \(max-width: 991\.98px\)[\s\S]*\.finance-selector-row,[\s\S]*grid-template-columns: repeat\(2, minmax\(0, 1fr\)\)/);
  assert.match(finance,/@media \(max-width: 760px\)[\s\S]*\.el-top-controls--personal,[\s\S]*grid-template-columns: repeat\(2, minmax\(0, 1fr\)\)/);
  assert.match(finance,/@media \(max-width: 760px\)[\s\S]*\.ft-kpi-grid\.ft-kpi-grid--four[\s\S]*grid-template-columns: repeat\(2, minmax\(0, 1fr\)\)/);
  assert.match(finance,/@media \(max-width: 620px\)[\s\S]*\.llbs-philosophy-grid,[\s\S]*\.llbs-tax-grid[\s\S]*grid-template-columns: repeat\(2, minmax\(0, 1fr\)\)/);
  assert.match(finance,/\.networth-tool\.el-shell\.legend-finance-tool-card--narrow,[\s\S]*width: 100%;[\s\S]*max-width: none;/);
  assert.match(finance,/\.llbs-status > :is\(\.llbs-save-state, \.llbs-growth-calculator-btn, \.llbs-print-btn, \.llbs-clear\)[\s\S]*min-width: 0;/);
});

test('feature styles cannot reintroduce competing mobile action-stack authority',()=>{
  const mediaBlocks=source=>{
    const out=[];let pos=0;
    while(true){
      const start=source.indexOf('@media',pos);if(start<0) break;
      const open=source.indexOf('{',start);if(open<0) break;
      let depth=1,i=open+1;
      for(;i<source.length&&depth;i++){
        if(source[i]==='{') depth++;
        else if(source[i]==='}') depth--;
      }
      out.push(source.slice(start,i));
      pos=i;
    }
    return out;
  };
  const checks=[
    ['ClientApp/wwwroot/css/client-mobile.css',[
      /\.client-create-actions[^\{]*\{[^}]*grid-template-columns:/,
      /\.preview-actions\s*\{[^}]*grid-template-columns:/
    ]],
    ['ClientApp/wwwroot/css/subscription-activation.css',[
      /\.activation-actions\s*\{[^}]*width:\s*100%/,
      /\.activation-notice-actions\s*\{[^}]*grid-template-columns:/
    ]],
    ['AgentPortal/wwwroot/css/clients-index.css',[
      /\.pipeline-head-actions\s*\{[^}]*width:\s*100%/,
      /\.drawer[^\{]*\.drawer-top-actions\s*\{[^}]*grid-template-columns:/,
      /\.queue-record-actions[^\{]*\{[^}]*flex-direction:\s*column/
    ]],
    ['AgentPortal/wwwroot/css/website-analytics.css',[
      /\.hero-link-meta-actions[^\{]*\{[^}]*flex-direction:\s*column/,
      /\.wa-modal-toggle-group\s*\{[^}]*grid-template-columns:\s*1fr/
    ]],
    ['AgentPortal/wwwroot/css/scripts-rebuttals.css',[
      /\.uw-actions\s*\{[^}]*grid-template-columns:/,
      /\.side-actions\s*\{[^}]*grid-template-columns:\s*1fr/
    ]],
    ['SHARED/wwwroot/css/legend-finance-shared.css',[
      /\.llbs-action-buttons[^\{]*\{[^}]*grid-template-columns:\s*1fr/,
      /\.savings-illustration-footer\s*\{[^}]*grid-template-columns:/
    ]]
  ];
  for(const [file,patterns] of checks){
    const source=readFileSync(new URL('../../'+file,import.meta.url),'utf8');
    const mobile=mediaBlocks(source).filter(block=>/max-width/i.test(block));
    for(const pattern of patterns){
      for(const block of mobile) assert.doesNotMatch(block,pattern,file+': '+pattern);
    }
  }
});

test('all discovered authenticated mobile modals use one full-screen shared authority',()=>{
  const css=readFileSync(new URL('../../SHARED/wwwroot/css/dashboard-home-shared.css',import.meta.url),'utf8');
  const modal=readFileSync(new URL('../../SHARED/wwwroot/js/legend-modal.js',import.meta.url),'utf8');

  const mobile=css.slice(css.indexOf('@media (max-width: 900px)'));
  assert.match(mobile,/\[data-legend-modal-surface\] \{[\s\S]*position: fixed !important;[\s\S]*inset: 0 !important;[\s\S]*width: 100vw !important;[\s\S]*height: 100dvh !important;[\s\S]*max-height: 100dvh !important;/);
  assert.match(mobile,/\.modal > \.modal-dialog\[data-legend-modal-panel\] \{[\s\S]*height: 100% !important;[\s\S]*max-height: none !important;/);
  assert.match(mobile,/\.modal > \.modal-dialog > \.modal-content\[data-legend-modal-panel\],[\s\S]*height: 100% !important;[\s\S]*overflow: hidden !important;/);
  assert.match(mobile,/\.modal \.modal-body \{[\s\S]*overflow-y: auto;/);
  assert.match(mobile,/\.legend-modal-close-control \{[\s\S]*width: 38px !important;[\s\S]*border-radius: 999px !important;/);
  assert.match(mobile,/\.legend-modal-close-control::before,[\s\S]*\.legend-modal-close-control::after/);

  assert.match(modal,/function surfaceOpen\(surface\)/);
  assert.match(modal,/function syncModalSurfaceState\(surface\)/);
  assert.match(modal,/if \(isMobileModalViewport\(\) && open\) lockPageScroll\(owner\)/);
  assert.match(modal,/else unlockPageScroll\(owner\)/);
  assert.match(modal,/normalizeCloseControl\(surface\)/);
  assert.match(modal,/data-legend-generated-modal-close/);
  assert.match(modal,/attributeFilter: \['class', 'hidden', 'aria-hidden', 'style'\]/);
});

test('feature scripts cannot own modal page scrolling anymore',()=>{
  const files=[
    'AgentPortal/wwwroot/js/home-zoom-hub.js',
    'AgentPortal/wwwroot/js/home-clients-hub.js',
    'AgentPortal/wwwroot/js/zoom-quick-popup.js',
    'AgentPortal/wwwroot/js/website-analytics-ai.js',
    'AgentPortal/wwwroot/js/website-analytics-kpi-modal.js',
    'AgentPortal/wwwroot/js/leads-index.js',
    'AgentPortal/wwwroot/js/clients-index.js',
    'AgentPortal/wwwroot/js/uw-intake.js',
    'AgentPortal/wwwroot/js/proposal-uw.js',
    'AgentPortal/Views/AgencyCommand/Index.cshtml',
    'ClientApp/Views/Home/Index.cshtml'
  ];
  for(const file of files){
    const source=readFileSync(new URL('../../'+file,import.meta.url),'utf8');
    assert.doesNotMatch(source,/document\.body\.style\.overflow\s*=/,file);
    assert.doesNotMatch(source,/body\.classList\.(?:add|remove)\(['"]overflow-hidden['"]\)/,file);
    assert.doesNotMatch(source,/document\.body\.classList\.(?:add|remove)\(["'](?:note-self-open|uw-open)["']\)/,file);
  }
});

test('feature modal sizing is desktop-only and cannot compete on phones',()=>{
  const checks=[
    ['AgentPortal/wwwroot/css/qv-booking.css',/\@media \(min-width:901px\)\{[\s\S]*\.qv-booking-modal-shell \.modal-dialog/],
    ['AgentPortal/wwwroot/css/legend-connect.css',/@media \(min-width:901px\) \{[\s\S]*\.lc-section-modal \.modal-dialog/],
    ['AgentPortal/wwwroot/css/website-analytics.css',/@media \(min-width:901px\) \{[\s\S]*\.wa-modal-dialog/],
    ['AgentPortal/wwwroot/css/home-command-page.css',/@media \(min-width:901px\) \{[\s\S]*\.home-clients-dialog/],
    ['AgentPortal/wwwroot/css/clients-index.css',/@media \(min-width:901px\)\{[\s\S]*\.actions-hub-modal \.modal-dialog/],
    ['AgentPortal/wwwroot/css/workstation-home-proposal.css',/@media \(min-width:901px\)\{#proposalDialog\.hp-dialog/],
    ['AgentPortal/wwwroot/css/scripts-rebuttals.css',/@media \(min-width:901px\)\{[\s\S]*\.note-self-modal/],
    ['SHARED/wwwroot/css/legend-finance-shared.css',/@media \(min-width:901px\) \{[\s\S]*\.finance-support-modal/]
  ];
  for(const [file,pattern] of checks){
    const source=readFileSync(new URL('../../'+file,import.meta.url),'utf8');
    assert.match(source,pattern,file);
  }
});

test('feature styles cannot reintroduce mobile modal scroll or sticky-shell authority',()=>{
  const mediaBlocks=source=>{
    const out=[];let pos=0;
    while(true){
      const start=source.indexOf('@media',pos);if(start<0) break;
      const open=source.indexOf('{',start);if(open<0) break;
      let depth=1,i=open+1;
      for(;i<source.length&&depth;i++){
        if(source[i]==='{') depth++;
        else if(source[i]==='}') depth--;
      }
      out.push(source.slice(start,i));
      pos=i;
    }
    return out;
  };
  const mobileBlocks=source=>mediaBlocks(source).filter(block=>/max-width/i.test(block));
  const booking=readFileSync(new URL('../../AgentPortal/wwwroot/css/qv-booking.css',import.meta.url),'utf8');
  const scripts=readFileSync(new URL('../../AgentPortal/wwwroot/css/scripts-rebuttals.css',import.meta.url),'utf8');
  const proposal=readFileSync(new URL('../../AgentPortal/wwwroot/css/workstation-home-proposal.css',import.meta.url),'utf8');
  const founderAi=readFileSync(new URL('../../AgentPortal/wwwroot/css/legend-founder-ai.css',import.meta.url),'utf8');
  const shared=readFileSync(new URL('../../SHARED/wwwroot/css/dashboard-home-shared.css',import.meta.url),'utf8');

  for(const block of mobileBlocks(booking))
    assert.doesNotMatch(block,/\.qv-booking-modal-shell \.modal-content\s*\{[^}]*overflow-y:\s*auto/);
  for(const block of mobileBlocks(scripts))
    assert.doesNotMatch(block,/\.proposal-dialog \.uw-head[^\{]*\{[^}]*position:\s*sticky/);
  for(const block of mobileBlocks(proposal)){
    assert.doesNotMatch(block,/#proposalDialog \.hp-dialog-head\s*\{[^}]*position:\s*sticky/);
    assert.doesNotMatch(block,/#uwDialog \.uw-head\s*\{[^}]*position:\s*sticky/);
  }
  assert.doesNotMatch(founderAi,/@media \(min-width: 821px\) and \(max-width: 1100px\)/);

  const sharedMobile=mobileBlocks(shared).find(block=>/^@media \(max-width: 900px\)/.test(block));
  assert(sharedMobile);
  assert.match(sharedMobile,/\.modal > \.modal-dialog > \.modal-content\[data-legend-modal-panel\][\s\S]*overflow:\s*hidden !important/);
  assert.match(sharedMobile,/\.modal \.modal-body\s*\{[\s\S]*overflow-y:\s*auto/);
  assert.match(sharedMobile,/\.modal \.modal-header,[\s\S]*position:\s*sticky !important/);
});

test('Explore mobile close treatment is owned only by the integrated navigation sheet',()=>{
  const shell=readFileSync(new URL('../../Legend-Design/legend-app-shell.css',import.meta.url),'utf8');
  const shared=readFileSync(new URL('../../SHARED/wwwroot/css/dashboard-home-shared.css',import.meta.url),'utf8');
  assert.doesNotMatch(shell,/@media\(max-width:840px\)[\s\S]*\.explore-close/);
  assert.match(shared,/\.legend-global-nav\[data-legend-mobile-nav-integrated\][\s\S]*\.explore-close[\s\S]*display: none !important/);
});

test('every authenticated host loads the final shared mobile authority after page styles',()=>{
  for(const file of [
    'AgentPortal/Views/Shared/_Layout.cshtml',
    'AgentPortal/Views/Shared/_ClientWorkspaceLayout.cshtml',
    'ClientApp/Views/Shared/_Layout.cshtml'
  ]){
    const source=readFileSync(new URL('../../'+file,import.meta.url),'utf8');
    const section=Math.max(source.indexOf('RenderSection("Styles"'),source.indexOf('RenderSectionAsync("Styles"'));
    const shared=source.indexOf('~/_content/Shared/css/dashboard-home-shared.css');
    assert(section>=0 && shared>section,file);
    assert.equal(source.split('~/_content/Shared/css/dashboard-home-shared.css').length-1,1,file);
  }
});


test('AgentPortal and ClientApp consume the same authenticated CSS authorities exactly once',()=>{
  const files=['AgentPortal/Views/Shared/_Layout.cshtml','ClientApp/Views/Shared/_Layout.cshtml'];
  for(const file of files){
    const source=readFileSync(new URL('../../'+file,import.meta.url),'utf8');
    for(const href of ['~/design/legend-web-foundation.css','~/design/legend-app-shell.css','~/_content/Shared/css/dashboard-home-shared.css']){
      assert.equal(source.split(href).length-1,1,file+': '+href);
    }
  }
  assert.equal(existsSync(new URL('../../ClientApp/wwwroot/css/legend-forms.css',import.meta.url)),false);
});

test('all three host layouts load the single shared owner before page scripts',()=>{
  for(const file of ['AgentPortal/Views/Shared/_Layout.cshtml','AgentPortal/Views/Shared/_ClientWorkspaceLayout.cshtml','ClientApp/Views/Shared/_Layout.cshtml']) {
    const source=readFileSync(new URL('../../'+file,import.meta.url),'utf8');
    assert.equal(source.split('~/_content/Shared/js/legend-modal.js').length-1,1,file);
    assert(source.indexOf('~/_content/Shared/js/legend-modal.js')<source.indexOf('RenderSectionAsync("Scripts"'),file);
  }
  assert.equal(existsSync(new URL('../../AgentPortal/wwwroot/js/legend-modal.js',import.meta.url)),false);
});
test('shared geometry preserves full backdrop and sizes inner Bootstrap/custom panels',()=>{
  const css=readFileSync(new URL('../../SHARED/wwwroot/css/dashboard-home-shared.css',import.meta.url),'utf8');
  assert.match(css,/\[data-legend-modal-panel\] \{\s+min-height: 0;\s+max-height: 100dvh/);
  assert.match(css,/\.modal > \.modal-dialog-scrollable \{\s+height: 100dvh/);
  assert.match(script,/new ResizeObserver\(scheduleViewportOffsets\)/);
  assert.match(script,/attributeFilter: \['class', 'hidden', 'aria-hidden', 'style'\]/);
  assert.match(script,/visualViewport\?\.addEventListener\("resize"/);
  assert.match(script,/document.addEventListener\('show.bs.modal'/);
});


test('mobile sheet behavior is globally owned and uses viewport-safe geometry',()=>{
  const css=readFileSync(new URL('../../SHARED/wwwroot/css/dashboard-home-shared.css',import.meta.url),'utf8');
  assert.match(css,/@media \(max-width: 900px\)[\s\S]*\[data-legend-mobile-sheet\]/);
  assert.match(css,/\[data-legend-mobile-sheet\][\s\S]*bottom: 0px;[\s\S]*height: 100dvh;/);
  assert.match(css,/data-legend-sheet-snap="half"[\s\S]*46%/);
  assert.match(script,/function registerMobileSheet\(/);
  assert.match(script,/addEventListener\("pointerdown"/);
  assert.match(script,/function lockPageScroll\(/);
});
test('lead and client quick views share the mobile sheet contract',()=>{
  for (const file of ['AgentPortal/Views/Leads/_LeadQuickView.cshtml','AgentPortal/Views/Clients/_ClientsQuickView.cshtml']) {
    const source=readFileSync(new URL('../../'+file,import.meta.url),'utf8');
    assert.match(source,/data-legend-mobile-sheet/);
    assert.match(source,/data-legend-sheet-scroll/);
    assert.match(source,/data-legend-sheet-close/);
  }
});
test('CRM scripts delegate page scroll locking to shared modal owner',()=>{
  for (const file of ['AgentPortal/wwwroot/js/leads-index.js','AgentPortal/wwwroot/js/clients-index.js']) {
    const source=readFileSync(new URL('../../'+file,import.meta.url),'utf8');
    assert.match(source,/LegendModal\?\.lockPageScroll\("crm-quick-view"\)/);
    assert.match(source,/LegendModal\?\.unlockPageScroll\("crm-quick-view"\)/);
    assert.doesNotMatch(source,/function lockPageScrollForQuickView/);
    assert.doesNotMatch(source,/quickViewScrollY/);
  }
});

function dialogFixture() {
  const body={};
  const context={
    surfaces:new WeakSet(),
    surfaceList:new Set(),
    modalOwner:surface=>{surface.dataset=surface.dataset||{};surface.dataset.legendModalOwner='fixture';return 'fixture';},
    normalizeCloseControl:()=>{},
    syncModalSurfaceState:()=>{},
    document:{body},
    window:{getComputedStyle:node=>({position:node.position||'static'})}
  };
  vm.createContext(context);vm.runInContext(implementation('registerDialog'),context);
  const node=(classes='',parent=body,role=null,position='static')=>({nodeType:1,parentElement:parent,position,attributes:{},panels:[],contents:[],
    matches:selector=>selector==='.modal'&&classes.split(' ').includes('modal'),
    setAttribute(key,value){this.attributes[key]=value;},
    querySelectorAll(selector){return selector==='.modal-content'?this.contents:this.panels;}});
  return {context,node,register:node=>context.registerDialog(node)};
}
test('Bootstrap registration sizes descendants without moving nodes or altering focus semantics',()=>{
  const f=dialogFixture(),surface=f.node('modal'),dialog=f.node('modal-dialog',surface),content=f.node('modal-content',dialog);
  surface.panels=[dialog];surface.contents=[content];const parents=[dialog.parentElement,content.parentElement];f.register(surface);
  assert('data-legend-modal-surface' in surface.attributes);assert('data-legend-modal-panel' in dialog.attributes);assert('data-legend-modal-panel' in content.attributes);
  assert.equal(dialog.parentElement,parents[0]);assert.equal(content.parentElement,parents[1]);assert.equal(surface.attributes.role,undefined);
});
test('custom dialog registration places offset on the fixed overlay once, not its inner card',()=>{
  const f=dialogFixture(),overlay=f.node('custom-overlay',undefined,null,'fixed'),card=f.node('custom-card',overlay,'dialog');overlay.panels=[card];f.register(card);f.register(card);
  assert('data-legend-modal-surface' in overlay.attributes);assert('data-legend-modal-panel' in card.attributes);assert(!('data-legend-modal-surface' in card.attributes));
});
test('nested Bootstrap dialogs each retain one overlay region and their original parent relationship',()=>{
  const f=dialogFixture(),outer=f.node('modal'),panel=f.node('modal-dialog',outer),inner=f.node('modal',panel);outer.panels=[panel];
  f.register(outer);f.register(inner);assert('data-legend-modal-surface' in outer.attributes);assert('data-legend-modal-surface' in inner.attributes);assert.equal(inner.parentElement,panel);
});
