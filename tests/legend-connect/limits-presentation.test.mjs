import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const read=path=>readFileSync(new URL('../../'+path,import.meta.url),'utf8');
const css=read('AgentPortal/wwwroot/css/legend-connect.css');
const view=read('AgentPortal/Views/LegendConnect/TranslationLimits.cshtml');
const index=read('AgentPortal/Views/LegendConnect/Index.cshtml');
function rule(selector){const start=css.indexOf('\n'+selector+' {')+1;assert(start>0,selector);return css.slice(start,css.indexOf('}',start)+1);}
test('body-ported dialogs retain one shared token declaration and readable control colors',()=>{
  assert.match(css,/\.legend-connect-page,\s*\.lc-section-modal\s*\{/);
  assert.equal(css.split('--lc-navy-950:').length-1,1);
  assert.match(rule('.lc-button'),/background: var\(--lc-navy-800\)/);
  assert.match(rule('.lc-button'),/color: var\(--lc-white\)/);
  assert.doesNotMatch(view,/dashboard-command-action|btn-carrier-settings|dashboard-analytics-panel/);
  assert.match(view,/class="lc-button"[^>]*aria-label="Manage @account.DisplayName"/);
});
test('limits alone use expanded horizontal width without changing shared viewport bounds',()=>{
  assert.match(index,/class="modal fade lc-section-modal lc-limits-modal" id="translationLimitsModal"/);
  assert.match(rule('.lc-limits-modal'),/1760px/);
  assert.match(rule('.lc-section-modal .modal-dialog'),/max-width: var\(--lc-dialog-max-width, 1380px\)/);
  assert.equal(index.split('lc-limits-modal').length-1,1);
});
test('account grid has a hard three-column ceiling and narrows with actual container width',()=>{
  assert.match(rule('.translation-account-list'),/repeat\(3, minmax\(0, 1fr\)\)/);
  assert.doesNotMatch(rule('.translation-account-list'),/auto-fit|auto-fill/);
  assert.match(css,/@container \(max-width: 960px\)\s*\{\s*\.translation-account-list, \.translation-limits-summary \{ grid-template-columns: repeat\(2/);
  assert.match(css,/@container \(max-width: 620px\)\s*\{\s*\.translation-account-list, \.translation-limits-summary \{ grid-template-columns: minmax\(0, 1fr\)/);
  assert.match(rule('.translation-account-metrics'),/repeat\(4, minmax\(0, 1fr\)\)/);
});
test('summary and accounts use compact isolated styles and preserve management functionality',()=>{
  assert.match(rule('.translation-policy-card, .translation-accounts-panel'),/padding: \.75rem/);
  assert.match(rule('.translation-account-row'),/padding: \.65rem/);
  for(const target of ['#globalTranslationLimit','#translationAccount@(index)'])assert(view.includes(`data-bs-target="${target}"`));
  for(const field of ['ConsumedCharacters','ReservedCharacters','RemainingCharacters','CharacterAllowance'])assert(view.includes(field));
  assert.match(css,/\.lc-button:focus-visible/);
});
test('Manage button token colors maintain readable normal-text contrast',()=>{
  const token=name=>new RegExp('--'+name+': (#[0-9a-f]{6})').exec(css)[1];
  const luminance=color=>{
    const channels=color.slice(1).match(/../g).map(v=>parseInt(v,16)/255).map(v=>v<=.04045?v/12.92:((v+.055)/1.055)**2.4);
    return channels[0]*.2126+channels[1]*.7152+channels[2]*.0722;
  };
  const contrast=(luminance(token('lc-white'))+.05)/(luminance(token('lc-navy-800'))+.05);
  assert(contrast>=4.5,`contrast ${contrast}`);
});

test('wide policy summary includes relay alongside defaults without removing live controls or cost information',()=>{
  assert.match(rule('.translation-limits-summary'),/repeat\(3, minmax\(0, 1fr\)\)/);
  const summaryStart=view.indexOf('class="translation-limits-summary"');
  const relayStart=view.indexOf('class="lc-relay-card"');
  const summaryEnd=view.indexOf('</section>',summaryStart);
  assert(relayStart>summaryStart && relayStart<summaryEnd);
  assert.match(view,/data-relay-start/);assert.match(view,/data-relay-stop/);assert.match(view,/data-relay-status/);
  assert.match(view,/Starting incurs Azure compute and bandwidth charges/);
});
