import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

for (const page of ['clients', 'leads']) {
  const source = readFileSync(new URL(`../../AgentPortal/wwwroot/js/${page}-index.js`, import.meta.url), 'utf8');

  test(`${page}: starred contacts are a priority tier, not a star-time sort`, () => {
    assert.match(source, /function rowIsStarred\(row\)/);
    assert.match(source, /return starred\.concat\(regular\)/);
    assert.match(source, /ordered\.filter\(rowIsStarred\)\.concat\(ordered\.filter\(row => !rowIsStarred\(row\)\)\)/);
    assert.doesNotMatch(source, /starredAt|starredUtc|starTime/i);
  });

  test(`${page}: drag reorder auto-scrolls and persists normalized tier order`, () => {
    assert.match(source, /function autoScrollPipelineDrag\(e, lane\)/);
    assert.match(source, /zone\.scrollBy\(\{ top: delta, behavior: "auto" \}\)/);
    assert.match(source, /window\.scrollBy\(\{ top: -18, behavior: "auto" \}\)/);
    assert.match(source, /window\.scrollBy\(\{ top: 18, behavior: "auto" \}\)/);
    assert.match(source, /normalizeStarredOrderIds\(targetOrder\)/);
    assert.match(source, /persistOrder\(targetStage, normalizedTargetOrder\)/);
  });

  test(`${page}: star action is isolated from card-open behavior`, () => {
    assert.match(source, /data-star-contact=/);
    assert.match(source, /e\.stopImmediatePropagation\(\)/);
    assert.match(source, /if \(e\.target\.closest\("\[data-star-contact\]"\)\) return;/);
  });
}
