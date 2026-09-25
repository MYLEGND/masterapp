import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { canonicalJson } from '../../src/security/crypto.mjs';
import { toolActionDigest } from '../../src/security/tool-broker.mjs';

const fixture = JSON.parse(await readFile(new URL('./action-digest-vectors.json', import.meta.url), 'utf8'));
for (const vector of fixture.vectors) {
  test(`frozen Azure/Worker action digest vector: ${vector.name}`, async () => {
    assert.equal(canonicalJson(vector.action), vector.canonicalAction);
    assert.equal(createHash('sha256').update(vector.canonicalAction, 'utf8').digest('hex'), vector.sha256);
    const { scope, requestId, environment, name, arguments: args } = vector.action;
    assert.equal(await toolActionDigest({ ...scope, requestId }, { name, arguments: args }, environment), vector.sha256);
  });
}
