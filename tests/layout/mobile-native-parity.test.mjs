import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';

const read = path => readFileSync(new URL('../../' + path, import.meta.url), 'utf8');

const layouts = [
  'AgentPortal/Views/Shared/_Layout.cshtml',
  'AgentPortal/Views/Shared/_ClientWorkspaceLayout.cshtml',
  'ClientApp/Views/Shared/_Layout.cshtml',
  'Protect-Website/Views/Shared/_Layout.cshtml',
];

test('all web apps consume one shared mobile web authority', () => {
  for (const file of layouts) {
    const source = read(file);
    assert.equal(source.split('~/_content/Shared/css/legend-mobile-platform.css').length - 1, 1, file);
    assert.equal(source.split('~/_content/Shared/js/legend-mobile-platform.js').length - 1, 1, file);
    assert.match(source, /legend-web-app/, file);
    assert(
      source.indexOf('legend-mobile-platform.js') < source.indexOf('legend-modal.js'),
      `${file} must initialize the mobile authority before LegendModal`);
  }

  assert.equal(existsSync(new URL('../../ClientApp/wwwroot/css/client-mobile.css', import.meta.url)), false);
  assert.equal(existsSync(new URL('../../AgentPortal/wwwroot/css/mobile-client-booking.css', import.meta.url)), false);
});

test('ClientApp has one phone authority and no compatibility duplicate', () => {
  const layout = read('ClientApp/Views/Shared/_Layout.cshtml');
  assert.equal(existsSync(new URL('../../ClientApp/wwwroot/css/client-mobile.css', import.meta.url)), false);
  assert.equal(existsSync(new URL('../../ClientApp/wwwroot/css/client-desktop-compat.css', import.meta.url)), false);
  assert.doesNotMatch(layout, /client-(?:mobile|desktop-compat)\.css/);
});

test('standalone mobile booking uses the shared authority instead of a fourth shell', () => {
  const source = read('AgentPortal/Views/Calendar/MobileBooking.cshtml');
  assert.match(source, /legend-web-app legend-agent-portal mobile-client-booking/);
  assert.match(source, /~\/_content\/Shared\/css\/legend-mobile-platform\.css/);
  assert.match(source, /~\/_content\/Shared\/js\/legend-mobile-platform\.js/);
  assert.match(source, /~\/_content\/Shared\/js\/legend-modal\.js/);
  assert.doesNotMatch(source, /mobile-client-booking\.css/);
});

test('mobile presentation reads the exact cross-platform token source', () => {
  const script = read('SHARED/wwwroot/js/legend-mobile-platform.js');
  for (const project of ['AgentPortal/AgentPortal.csproj', 'ClientApp/ClientApp.csproj']) {
    const source = read(project);
    assert.match(source, /Legend-Design[\\/]legend-design\.tokens\.json/);
    assert.match(source, /wwwroot[\\/]design[\\/]legend-design\.tokens\.json/);
  }

  assert.match(script, /const tokenUrl = "\/design\/legend-design\.tokens\.json"/);
  assert.match(script, /spacing\.pageHorizontal/);
  assert.match(script, /spacing\.pageTop/);
  assert.match(script, /spacing\.pageBottom/);
  assert.match(script, /radii\.sheet/);
  assert.match(script, /sizes\.minimumTapTarget/);
  assert.match(script, /sizes\.controlHeight/);
  assert.match(script, /motion\.standardSeconds/);
  assert.match(script, /root\.dataset\.legendDesignSource/);
});

test('shared modal and navigation controllers delegate mobile behavior to the authority', () => {
  const modal = read('SHARED/wwwroot/js/legend-modal.js');
  const nav = read('SHARED/wwwroot/js/legend-global-navigation.js');
  assert.match(modal, /LegendMobilePlatform\?\.decorateModal/);
  assert.match(modal, /LegendMobilePlatform\?\.isMobile/);
  assert.match(modal, /legend:mobilemodechange/);
  assert.match(nav, /LegendMobilePlatform\?\.isMobile/);
  assert.match(nav, /legend:mobilemodechange/);
});

test('shared mobile CSS owns generic page, controls, scrolling and sheets only at phone width', () => {
  const css = read('SHARED/wwwroot/css/legend-mobile-platform.css');
  assert.match(css, /@media \(max-width: 840px\)/);
  assert.match(css, /body\.legend-web-app > header/);
  assert.match(css, /\.layout-content/);
  assert.match(css, /--legend-mobile-page-horizontal/);
  assert.match(css, /--legend-mobile-sheet-radius/);
  assert.match(css, /--legend-mobile-tap-target/);
  assert.match(css, /-webkit-overflow-scrolling: touch/);
  assert.match(css, /overscroll-behavior-y: contain/);
  assert.match(css, /data-legend-mobile-sheet/);
  assert.match(css, /data-legend-mobile-sheet-panel/);
  assert.match(css, /\.drawer\.crm-qv-shell\[data-legend-mobile-sheet\]/);
  assert.match(css, /> \.dbody \{[\s\S]*?overflow-y: auto/);
  assert.doesNotMatch(css, /#[0-9a-fA-F]{3,8}\b/);
});

test('page-specific AgentPortal mobile CSS no longer owns modal viewport geometry', () => {
  const booking = read('AgentPortal/wwwroot/css/qv-booking.css');
  const carrier = read('AgentPortal/wwwroot/css/dashboard-carrier-settings.css');
  const founder = read('AgentPortal/wwwroot/css/legend-founder-ai.css');
  const clients = read('AgentPortal/wwwroot/css/clients-index.css');

  assert.doesNotMatch(booking, /@media \(max-width:760px\)[\s\S]*?\.qv-booking-modal-shell \.modal-dialog\s*\{[\s\S]*?100vw/);
  assert.doesNotMatch(carrier, /@media \(max-width: 720px\)\s*\{\s*\.legend-modal\.carrier-settings-modal \.modal-dialog/);
  assert.doesNotMatch(founder, /@media \(max-width: 820px\)[\s\S]*?\.legend-founder-ai-modal \.modal-dialog/);
  assert.doesNotMatch(clients, /@media \(max-width: 820px\)[\s\S]*?\.modal\.crm-command-modal\s*\{\s*width:/);
  assert.doesNotMatch(clients, /@media \(max-width: 640px\)[\s\S]*?\.modal\.crm-command-modal\s*\{\s*top:/);

  // Desktop geometry remains present; the repair is phone-only.
  assert.match(booking, /\.qv-booking-modal-shell \.modal-dialog\s*\{\s*width:min\(92vw, 1260px\)/);
  assert.match(founder, /@media \(min-width: 821px\) and \(max-width: 1100px\)/);
  assert.match(clients, /@media \(min-width: 841px\) \{[\s\S]*?\.actions-hub-modal\.modal/);

  const workstation = read('AgentPortal/wwwroot/css/workstation-home-proposal.css');
  assert.doesNotMatch(workstation, /@media \(max-width: 700px\)\s*\{\s*#drawer\.drawer\[data-workstation-drawer/);
  assert.match(workstation, /@media \(min-width: 841px\) \{[\s\S]*?#proposalOverlay\.hp-overlay/);
});

test('Explore and generic phone shell geometry have one shared owner', () => {
  const shared = read('SHARED/wwwroot/css/legend-mobile-platform.css');
  const agent = read('AgentPortal/Views/Shared/_Layout.cshtml');
  const workspace = read('AgentPortal/Views/Shared/_ClientWorkspaceLayout.cshtml');
  const clientInline = read('ClientApp/wwwroot/css/layout-inline.css');
  const clientSite = read('ClientApp/wwwroot/css/site.css');

  assert.match(shared, /\.explore-drawer \{/);
  assert.match(shared, /\.explore-list \{/);
  assert.doesNotMatch(agent, /\.explore-drawer\s*\{\s*top:\s*154px/);
  assert.doesNotMatch(workspace, /\.explore-(?:drawer|trigger)\s*\{\s*top:\s*215px/);
  assert.doesNotMatch(clientInline, /\.explore-drawer\s*\{\s*top:\s*12px/);
  assert.doesNotMatch(clientSite, /\.layout-content\s*\{\s*padding-top:\s*1rem;\s*padding-bottom:\s*1\.5rem;/);
});


test('Workstation and analytics no longer own phone viewport shells', () => {
  const rebuttals = read('AgentPortal/wwwroot/css/scripts-rebuttals.css');
  const analytics = read('AgentPortal/wwwroot/css/website-analytics.css');
  const clients = read('AgentPortal/wwwroot/css/clients-index.css');

  assert.doesNotMatch(rebuttals, /\.note-self-overlay,\s*\/\*[\s\S]*?Term vs Whole/);
  assert.doesNotMatch(rebuttals, /@media \(max-width: 900px\)[\s\S]{0,500}?html\.lead-bridge-mobile #rbShell\s*\{\s*height:\s*100dvh/);
  assert.doesNotMatch(rebuttals, /@media \(max-width: 900px\)[\s\S]{0,500}?#rbShell\s*\{\s*height:\s*100dvh/);
  assert.doesNotMatch(rebuttals, /@media \(max-width: 700px\)[\s\S]{0,500}?data-workstation-drawer="1"[\s\S]{0,300}?100dvh/);
  assert.match(rebuttals, /@media \(min-width: 841px\)[\s\S]*?data-workstation-drawer="1"/);

  assert.doesNotMatch(analytics, /@media \(max-width: 480px\)\s*\{\s*\.ai-drawer\s*\{\s*width:\s*100vw/);
  assert.match(analytics, /@media \(min-width: 841px\)[\s\S]*?\.ai-drawer/);
  assert.doesNotMatch(analytics, /@media \(max-width: 991px\)[\s\S]{0,400}?#deviceIntelligenceModal \.modal-dialog\s*\{[\s\S]{0,200}?100vh/);

  assert.doesNotMatch(clients, /@media \(max-width: 640px\)[\s\S]{0,600}?\.drawer\.crm-qv-shell\s*\{[\s\S]{0,250}?(?:100vw|max-height)/);
  assert.match(clients, /@media \(min-width: 841px\)[\s\S]*?\.drawer\.crm-qv-shell/);
});

test('CRM quick views are semantic dialogs with shared mobile regions', () => {
  for (const file of [
    'AgentPortal/Views/Leads/_LeadQuickView.cshtml',
    'AgentPortal/Views/Clients/_ClientsQuickView.cshtml',
  ]) {
    const source = read(file);
    assert.match(source, /class="drawer crm-qv-shell[^"]*" id="drawer" role="dialog" aria-modal="true"/, file);
    assert.match(source, /class="dhead" data-dialog-header/, file);
    assert.match(source, /class="dbody" data-dialog-body/, file);
  }

  const platform = read('SHARED/wwwroot/js/legend-mobile-platform.js');
  assert.match(platform, /data-dialog-header/);
  assert.match(platform, /data-dialog-body/);
  assert.match(platform, /data-legend-mobile-sheet-open/);
  assert.match(platform, /legend-mobile-sheet-open/);
});


test('mobile web dimensions are sourced from the exact iOS design tokens', () => {
  const tokens = JSON.parse(read('Legend-Design/legend-design.tokens.json'));
  assert.equal(tokens.spacing.pageHorizontal, 16);
  assert.equal(tokens.spacing.pageTop, 12);
  assert.equal(tokens.spacing.cardContent, 14);
  assert.equal(tokens.radii.control, 16);
  assert.equal(tokens.radii.card, 20);
  assert.equal(tokens.radii.sheet, 28);
  assert.equal(tokens.sizes.minimumTapTarget, 44);
  assert.equal(tokens.sizes.compactControlHeight, 36);
  assert.equal(tokens.sizes.controlHeight, 46);
  assert.equal(tokens.typography.title.size, 22);
  assert.equal(tokens.typography.body.size, 16);
  assert.equal(tokens.typography.supporting.size, 14);

  const platform = read('SHARED/wwwroot/js/legend-mobile-platform.js');
  for (const binding of [
    'sizes.compactControlHeight',
    'spacing.cardContent',
    'next?.typography?.title?.size',
    'next?.typography?.body?.size',
    'next?.typography?.supporting?.size',
  ]) {
    assert.match(platform, new RegExp(binding.replace(/[?.]/g, '\\$&')));
  }
});

test('CRM phone geometry has one owner and Quick View body remains scrollable', () => {
  const clients = read('AgentPortal/wwwroot/css/clients-index.css');
  const shared = read('SHARED/wwwroot/css/legend-mobile-platform.css');

  for (const width of [640, 650, 700, 720, 760, 800, 820, 840]) {
    const pattern = new RegExp('@media \\(max-width:\\s*' + width + 'px\\)[\\s\\S]*?\\.drawer\\.crm-qv-shell');
    assert.doesNotMatch(clients, pattern);
  }

  assert.match(shared, /\.drawer\.crm-qv-shell\[data-legend-mobile-sheet\] > \.dbody \{[\s\S]*?flex:\s*1 1 auto;[\s\S]*?min-height:\s*0;[\s\S]*?overflow-y:\s*auto;/);
  assert.match(shared, /touch-action:\s*pan-y/);
});
