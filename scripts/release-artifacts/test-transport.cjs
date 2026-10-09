'use strict';
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const {publish} = require('./transport.cjs');
const name = 'legend-release-operation-intent-' + 'a'.repeat(64);
const record = {schemaVersion: 1, phase: 'intent', operationId: 'a'.repeat(64),
  packageDigest: 'b'.repeat(64), applicationRevision: 'c'.repeat(40), baseline: 'd'.repeat(40),
  target: 'portal', producingRun: 10, producingAttempt: 1, packageProducerRun: 8};

test('publication returns only after durable content is read back by immutable ID', async () => {
  const calls = [];
  let stored;
  const client = {
    async listArtifacts() { return {artifacts: []}; },
    async uploadArtifact(n, files, root, options) {
      calls.push('upload');
      assert.equal(n, name);
      assert.equal(files.length, 1);
      assert.equal(files[0], path.join(root, 'operation.json'));
      assert.equal(options.retentionDays, 90);
      stored = await fs.readFile(files[0]);
      return {id: 42};
    },
    async downloadArtifact(id, options) {
      calls.push('readback');
      assert.equal(id, 42);
      await fs.mkdir(options.path);
      await fs.writeFile(path.join(options.path, 'operation.json'), stored);
    }
  };
  assert.deepEqual(await publish(client, name, record), {artifactId: 42});
  assert.deepEqual(calls, ['upload', 'readback']);
});

test('readback mismatch fails closed after upload', async () => {
  const client = {
    async listArtifacts() { return {artifacts: []}; },
    async uploadArtifact() { return {id: 42}; },
    async downloadArtifact(id, options) {
      await fs.mkdir(options.path);
      await fs.writeFile(path.join(options.path, 'operation.json'), '{}');
    }
  };
  await assert.rejects(publish(client, name, record), /mismatch/);
});

test('unexpected payload fields cannot become artifacts', async () => {
  await assert.rejects(publish({}, name, {token: 'forbidden'}), /Invalid/);
  await assert.rejects(publish({}, '../arbitrary', {phase: 'intent'}), /Invalid/);
});

test('scheduler admission uses the same immutable upload and readback channel', async () => {
  const receipt = {schemaVersion: 1, admissionId: 'f'.repeat(64), sourcePr: 42,
    applicationRevision: 'a'.repeat(40), authorizedSourceRevision: 'e'.repeat(40), packageIdentity: 'd'.repeat(64), executionAuthority: 'b'.repeat(40),
    sourceMergeSha: 'c'.repeat(40), selectedTargets: ['masterapp-portal'],
    resources: ['app:portal'], authorizationMode: 'automatic', producingRun: 10, producingAttempt: 1, phase: 'admission'};
  let stored;
  const client = {
    async listArtifacts() { return {artifacts: []}; },
    async uploadArtifact(name, files) {
      assert.equal(name, 'legend-release-admission-' + receipt.admissionId);
      stored = await fs.readFile(files[0]);
      return {id: 43};
    },
    async downloadArtifact(id, options) {
      assert.equal(id, 43);
      await fs.mkdir(options.path);
      await fs.writeFile(path.join(options.path, 'operation.json'), stored);
    }
  };
  assert.deepEqual(await publish(client, 'legend-release-admission-' + receipt.admissionId, receipt), {artifactId: 43});
  await assert.rejects(publish(client, 'legend-release-admission-' + receipt.admissionId,
    {...receipt, resources: ['invalid\nresource']}), /Invalid/);
});

test('child proof preserves stable operation and distinct evidence identities without credential fields', async () => {
  const receipt = {schemaVersion: 1, child: 'migrations', phase: 'success',
    dependencyIdentity: 'a'.repeat(64), materialIdentity: 'b'.repeat(64), partitionIdentity: 'b'.repeat(64), evidenceIdentity: 'c'.repeat(64),
    applicationRevision: 'd'.repeat(40), executionAuthority: 'e'.repeat(40),
    producingRun: 10, producingAttempt: 1, observation: {schemaIdentity: 'f'.repeat(64)}};
  const artifact = 'legend-release-child-success-' + receipt.dependencyIdentity;
  let stored;
  const client = {
    async listArtifacts() { return {artifacts: []}; },
    async uploadArtifact(name, files) {
      assert.equal(name, artifact);
      stored = await fs.readFile(files[0]);
      return {id: 44};
    },
    async downloadArtifact(id, options) {
      assert.equal(id, 44);
      await fs.mkdir(options.path);
      await fs.writeFile(path.join(options.path, 'operation.json'), stored);
    }
  };
  assert.deepEqual(await publish(client, artifact, receipt), {artifactId: 44});
  await assert.rejects(publish(client, artifact, {...receipt, observation: {connectionString: 'forbidden'}}), /Invalid/);
  await assert.rejects(publish(client, artifact, {...receipt, materialIdentity: 'bad'}), /Invalid/);
});

function durableClient({lostAck = false, failReadback = false} = {}) {
  const state = {uploads: 0, downloads: 0, artifacts: [], content: null};
  return {state,
    async listArtifacts(options) {
      assert.deepEqual(options, {latest: false});
      return {artifacts: state.artifacts};
    },
    async uploadArtifact(name, files) {
      state.uploads++;
      state.content = await fs.readFile(files[0], 'utf8');
      state.artifacts.push({name, id: 45});
      if (lostAck) throw new Error('Connection reset after commit');
      return {id: 45};
    },
    async downloadArtifact(id, options) {
      state.downloads++;
      assert.equal(id, 45);
      if (failReadback && state.downloads === 1) throw new Error('Temporary read failure');
      await fs.mkdir(options.path);
      await fs.writeFile(path.join(options.path, 'operation.json'), state.content);
    }
  };
}

test('lost upload acknowledgment reconciles identical content without duplicate upload', async () => {
  const client = durableClient({lostAck: true});
  assert.deepEqual(await publish(client, name, record), {artifactId: 45});
  assert.equal(client.state.uploads, 1);
  assert.equal(client.state.downloads, 1);
});

test('readback failure and worker restart preserve prior upload', async () => {
  const client = durableClient({failReadback: true});
  await assert.rejects(publish(client, name, record), /Temporary read failure/);
  assert.deepEqual(await publish(client, name, record), {artifactId: 45});
  assert.equal(client.state.uploads, 1);
  assert.equal(client.state.downloads, 2);
});

test('duplicate events reuse exact receipt without a second write', async () => {
  const client = durableClient();
  await publish(client, name, record);
  await publish(client, name, record);
  assert.equal(client.state.uploads, 1);
  assert.equal(client.state.downloads, 2);
});

test('existing artifact with changed producer or tampered bytes fails closed', async () => {
  const client = durableClient();
  await publish(client, name, record);
  await assert.rejects(publish(client, name, {...record, producingAttempt: 2}), /mismatch/);
  client.state.content = '{}';
  await assert.rejects(publish(client, name, record), /mismatch/);
  assert.equal(client.state.uploads, 1);
});

test('ambiguous identity and unavailable inventory authorize no upload', async () => {
  const client = durableClient();
  client.state.artifacts = [{name, id: 45}, {name, id: 46}];
  await assert.rejects(publish(client, name, record), /Ambiguous/);
  assert.equal(client.state.uploads, 0);
  client.listArtifacts = async () => { throw new Error('Inventory unavailable'); };
  await assert.rejects(publish(client, name, record), /Inventory unavailable/);
  assert.equal(client.state.uploads, 0);
});

test('unknown upload outcome cannot authorize a retry or success', async () => {
  const client = durableClient();
  client.uploadArtifact = async () => { client.state.uploads++; throw new Error('Unknown'); };
  await assert.rejects(publish(client, name, record, {sleep: async () => {}}), /outcome unresolved/);
  assert.equal(client.state.uploads, 1);
  assert.equal(client.state.downloads, 0);
});

test('overlapping publishers converge through provider immutable-name conflict', async () => {
  const client = durableClient();
  let accepted = 0;
  let arrivals = 0;
  let open;
  const both = new Promise(resolve => { open = resolve; });
  const original = client.uploadArtifact;
  client.uploadArtifact = async (...args) => {
    arrivals++;
    if (arrivals === 2) open();
    await both;
    // Model the provider's supported create-only artifact-name constraint.
    if (accepted) throw new Error('409 immutable artifact already exists');
    accepted++;
    return original(...args);
  };
  const results = await Promise.all([publish(client, name, record), publish(client, name, record)]);
  assert.deepEqual(results, [{artifactId: 45}, {artifactId: 45}]);
  assert.equal(arrivals, 2);
  assert.equal(accepted, 1);
  assert.equal(client.state.uploads, 1);
});
