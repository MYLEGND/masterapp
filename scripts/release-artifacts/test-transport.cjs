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

test('readiness rerun intent uses canonical immutable transport and rejects widened payloads', async () => {
  const client = durableClient({lostAck: true});
  const intent = {schemaVersion: 1, phase: 'intent', operationId: 'e'.repeat(64),
    candidateRevision: 'a'.repeat(40), executionAuthority: 'b'.repeat(40),
    targetRun: 12, targetAttempt: 1, targetJob: 34, producingRun: 56, producingAttempt: 1};
  const artifact = 'legend-readiness-recovery-' + intent.operationId;
  assert.deepEqual(await publish(client, artifact, intent), {artifactId: 45});
  assert.equal(client.state.uploads, 1);
  assert.equal(client.state.downloads, 1);
  await assert.rejects(publish(client, artifact, {...intent, targetJob: '34'}), /Invalid/);
  await assert.rejects(publish(client, artifact, {...intent, command: 'arbitrary'}), /Invalid/);
  assert.equal(client.state.uploads, 1);
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


test('component operation retains actual producer and separate authorization through durable transport', async () => {
  const component = {...record, schemaVersion: 2, candidateRevision: 'e'.repeat(40),
    componentSource: {runId: 8, runAttempt: 2, artifactId: 9, artifactDigest: 'sha256:'+'f'.repeat(64),
      artifactName: 'validated-component-bytes-test', receiptArtifactId: 10, producingJobId: 11}};
  let stored; let uploads = 0;
  const client = {
    async listArtifacts() { return {artifacts: []}; },
    async uploadArtifact(n, files) { uploads++; stored = await fs.readFile(files[0]); return {id: 42}; },
    async downloadArtifact(id, options) {
      await fs.mkdir(options.path); await fs.writeFile(path.join(options.path, 'operation.json'), stored);
    }
  };
  assert.deepEqual(await publish(client, name, component), {artifactId: 42});
  assert.deepEqual(JSON.parse(stored), component);
  assert.equal(uploads, 1);
  await assert.rejects(publish({}, name, {...component, packageProducerRun: 99}), /authorization\/producer/);
  await assert.rejects(publish({}, name, {...component, schemaVersion: 1}), /unversioned/);
});

test('mixed producer plan compares rollback with producer, not authorization candidate', async () => {
  const crypto = require('node:crypto');
  const candidate = 'a'.repeat(40), producer = 'b'.repeat(40), content = 'c'.repeat(64);
  const source = {runId: 8, runAttempt: 2, artifactId: 9, artifactDigest: 'sha256:'+'f'.repeat(64),
    artifactName: `validated-component-bytes-portal-${content}-8-a2`, receiptArtifactId: 10, producingJobId: 11};
  const snapshot = {candidateRevision: candidate, entries: [], schemaVersion: 1};
  snapshot.digest = crypto.createHash('sha256').update(JSON.stringify(snapshot)).digest('hex');
  const row = {app: 'portal', revision: producer, packageDigest: 'd'.repeat(64), rollbackEvidence: null,
    targetMaterial: {producerRevision: producer, packageDigest: 'd'.repeat(64), contentIdentity: content,
      executionIdentity: 'e'.repeat(64), source}};
  const plan = {schemaVersion: 2, planId: '1'.repeat(64), candidateRevision: candidate,
    producingRun: 12, producingAttempt: 1, targets: [row], historySnapshot: snapshot};
  let stored, uploads = 0;
  const client = {
    async listArtifacts() { return {artifacts: []}; },
    async uploadArtifact(n, files) { uploads++; stored = await fs.readFile(files[0]); return {id: 42}; },
    async downloadArtifact(id, options) {
      await fs.mkdir(options.path); await fs.writeFile(path.join(options.path, 'release-transaction.json'), stored);
    }
  };
  const planName = 'legend-release-transaction-plan-' + plan.planId;
  assert.deepEqual(await publish(client, planName, plan), {artifactId: 42});
  assert.equal(uploads, 1);
  const wrong = structuredClone(plan); wrong.targets[0].targetMaterial.source.artifactName = 'wrong';
  await assert.rejects(publish({}, planName, wrong), /artifact name/);
  const old = structuredClone(plan); old.schemaVersion = 1;
  await assert.rejects(publish({}, planName, old), /transaction target/);
});

test('component locator rerun preserves one immutable index without claiming success', async () => {
  const locator = {schemaVersion: 1, component: 'portal', contentIdentity: 'f'.repeat(64), runId: 17};
  const locatorName = 'validated-component-index-portal-' + locator.contentIdentity;
  let stored, uploads = 0;
  const client = {
    async listArtifacts() { return {artifacts: uploads ? [{name: locatorName, id: 42}] : []}; },
    async uploadArtifact(n, files) { uploads++; stored = await fs.readFile(files[0]); return {id: 42}; },
    async downloadArtifact(id, options) {
      await fs.mkdir(options.path); await fs.writeFile(path.join(options.path, 'operation.json'), stored);
    }
  };
  await publish(client, locatorName, locator);
  await publish(client, locatorName, locator);
  assert.equal(uploads, 1);
  assert.deepEqual(JSON.parse(stored), locator);
  await assert.rejects(publish({}, locatorName, {...locator, state: 'success'}), /Invalid/);
});


test('Python migration observation survives actual transport and lost acknowledgment without duplicate upload', async () => {
  const {execFileSync} = require('node:child_process');
  const python = `import importlib.util,json,os,pathlib
p=pathlib.Path('scripts/release-migration.py')
s=importlib.util.spec_from_file_location('migration',p);m=importlib.util.module_from_spec(s);s.loader.exec_module(m)
os.environ.update(GITHUB_RUN_ID='8',GITHUB_RUN_ATTEMPT='2')
r=dict(candidate='a'*40,executionAuthority='b'*40,databaseIdentity='c'*64,schemaIdentity='d'*64,baselineIdentity='e'*64,observedUtc='2026-10-09T12:00:00.1234567+00:00',pendingCount=0,mutationActivity='settled',historicalExecution='outcome-unknown',historicalEvidenceIdentity='f'*64,historicalReconciliationSources=[dict(run=7,attempt=1,code='HISTORICAL_EXECUTION_REQUIRES_RECONCILIATION',sourceBlob='a'*40,stateArtifactId=9,admissionId='b'*64)])
m.retain_current_observation(r,'0'*64,publisher=lambda name,record,**kwargs:print(json.dumps(dict(name=name,record=record))))`;
  const payload = JSON.parse(execFileSync('python3', ['-c', python], {cwd: path.resolve(__dirname, '../..'), encoding: 'utf8'}));
  const client = durableClient({lostAck: true});
  assert.deepEqual(await publish(client, payload.name, payload.record, {sleep: async () => {}}), {artifactId: 45});
  await publish(client, payload.name, payload.record);
  assert.equal(client.state.uploads, 1);
  assert.deepEqual(JSON.parse(client.state.content), payload.record);
  for (const delta of [{sqlExecutionAuthorized: true}, {phase: 'intent'}, {pendingCount: 1},
                       {producingAttempt: 0}, {token: 'forbidden'}, {databaseIdentity: '1'.repeat(64)},
                       {historicalReconciliationSources: [{run: 7,attempt: 1,code:'UNKNOWN',sourceBlob:'a'.repeat(40)}]}]) {
    await assert.rejects(publish({}, payload.name, {...payload.record, ...delta}), /Invalid/);
  }
});

test('transient post-upload readback recovers in place with one upload', async () => {
  const client = durableClient();
  const download = client.downloadArtifact;
  let attempts = 0;
  client.downloadArtifact = async (...args) => {
    if (++attempts < 3) throw Object.assign(new Error('unavailable'), {statusCode: 503});
    return download(...args);
  };
  const delays = [];
  assert.deepEqual(await publish(client, name, record, {sleep: async ms => delays.push(ms)}), {artifactId: 45});
  assert.equal(client.state.uploads, 1);
  assert.equal(attempts, 3);
  assert.deepEqual(delays, [1000, 2000]);
});

test('exhausted readback preserves exact artifact for a later reader without reupload', async () => {
  const client = durableClient();
  const download = client.downloadArtifact;
  let attempts = 0;
  client.downloadArtifact = async () => { attempts++; throw Object.assign(new Error('unavailable'), {code: 'ECONNRESET'}); };
  await assert.rejects(publish(client, name, record, {sleep: async () => {}}), /unavailable/);
  assert.equal(attempts, 3);
  assert.equal(client.state.uploads, 1);
  client.downloadArtifact = download;
  assert.deepEqual(await publish(client, name, record), {artifactId: 45});
  assert.equal(client.state.uploads, 1);
});

test('authorization and unknown read errors never trigger retry or another upload', async () => {
  for (const statusCode of [401, 403, undefined]) {
    const client = durableClient();
    let attempts = 0;
    client.downloadArtifact = async () => { attempts++; throw Object.assign(new Error('blocked'), {statusCode}); };
    await assert.rejects(publish(client, name, record, {sleep: async () => assert.fail('unexpected retry')}), /blocked/);
    assert.equal(attempts, 1);
    assert.equal(client.state.uploads, 1);
  }
});
