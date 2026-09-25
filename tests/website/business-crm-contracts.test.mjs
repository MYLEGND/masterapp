import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from 'jsdom';

for (const page of ['clients', 'leads']) {
  function fixture(business = 'tenant-a') {
    const source = readFileSync(new URL(`../../AgentPortal/wwwroot/js/${page}-index.js`, import.meta.url), 'utf8');
    const dom = new JSDOM(`<div id="legendWrap" data-business-id="${business}" data-crm-api-base="/business/tenant-a/crm/api"></div>
      <tr></tr><div class="client-row" data-client-id="a" data-business-revision="old-a"></div>
      <div class="client-row" data-client-id="b" data-business-revision="old-b"></div>`, { runScripts: 'outside-only' });
    dom.window.eval(source.slice(0, source.indexOf('/* ========= UTIL ========= */')));
    return dom.window;
  }
  test(`${page}: preserve displayed revisions for single and batch writes`, () => {
    const window = fixture();
    assert.equal(window.businessWritePayload({ clientUserId: 'a' }).revision, 'old-a');
    const batch = window.businessWritePayload({ ids: ['b', 'a', 'missing'] });
    assert.deepEqual(JSON.parse(JSON.stringify(batch.revisions)), { b: 'old-b', a: 'old-a', missing: '' });
    window.rememberBusinessRevisions({ revisions: { a: 'new-a' } });
    assert.equal(window.businessWritePayload({ clientUserId: 'a' }).revision, 'new-a');
    assert.equal(window.businessWritePayload({ clientUserId: 'b' }).revision, 'old-b');
    window.close();
  });
  test(`${page}: leave agent contracts unchanged`, () => {
    const window = fixture('');
    const request = { clientUserId: 'a', pipelineStage: 'Contacted' };
    assert.equal(window.businessWritePayload(request), request);
    window.rememberBusinessRevisions({ clientUserId: 'a', revision: 'unrelated' });
    assert.equal(window.document.querySelector('.client-row').dataset.businessRevision, 'old-a');
    window.close();
  });
}
