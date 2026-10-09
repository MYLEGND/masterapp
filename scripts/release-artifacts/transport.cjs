'use strict';
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const {DefaultArtifactClient} = require('@actions/artifact');

// JSON arrives on stdin, never as an arbitrary upload path. Only the bounded
// release operation record can be persisted; credentials cannot enter it.
async function publish(client, name, record, {sleep = ms => new Promise(resolve => setTimeout(resolve, ms))} = {}) {
  const admission = /^legend-release-admission-[a-f0-9]{64}$/.test(name);
  const child = /^legend-release-child-(intent|success)-[a-f0-9]{64}$/.test(name);
  const plan = /^legend-release-transaction-plan-[a-f0-9]{64}$/.test(name);
  if (!admission && !child && !plan && !/^legend-release-operation-(intent|success)-[a-f0-9]{64}$/.test(name))
    throw new Error('Invalid release operation artifact identity');
  const allowed = new Set(plan ? ['schemaVersion', 'planId', 'candidateRevision', 'producingRun', 'producingAttempt', 'targets', 'historySnapshot'] : child ? ['schemaVersion', 'child', 'dependencyIdentity', 'materialIdentity', 'evidenceIdentity', 'partitionIdentity',
    'applicationRevision', 'executionAuthority', 'producingRun', 'producingAttempt', 'phase', 'observation'] : admission ? ['schemaVersion', 'admissionId', 'sourcePr',
    'applicationRevision', 'authorizedSourceRevision', 'packageIdentity', 'executionAuthority', 'sourceMergeSha', 'selectedTargets',
    'resources', 'authorizationMode', 'producingRun', 'producingAttempt', 'phase'] : ['schemaVersion', 'operationId', 'target', 'applicationRevision',
    'packageDigest', 'baseline', 'packageProducerRun', 'producingRun', 'producingAttempt',
    'baselineDeploymentIds', 'phase', 'deploymentIds']);
  if (!record || Object.keys(record).some(key => !allowed.has(key)) ||
      JSON.stringify(record).length > (plan ? 524288 : 32768)) throw new Error('Invalid release operation record');
  if (plan) {
    if (record.schemaVersion !== 1 || name !== `legend-release-transaction-plan-${record.planId}` ||
        !/^[a-f0-9]{64}$/.test(record.planId) || !/^[a-f0-9]{40}$/.test(record.candidateRevision) ||
        ['producingRun', 'producingAttempt'].some(key => !Number.isSafeInteger(record[key]) || record[key] < 1) ||
        !Array.isArray(record.targets) || !record.targets.length || record.targets.length > 100)
      throw new Error('Invalid transaction plan identity');
    const seen = new Set();
    for (const row of record.targets) {
      if (!row || Object.keys(row).some(key => !['app', 'revision', 'packageDigest', 'rollbackEvidence'].includes(key)) ||
          !/^[a-z][a-z0-9-]{0,63}$/.test(row.app) || seen.has(row.app) ||
          !/^[a-f0-9]{40}$/.test(row.revision) || !/^[a-f0-9]{64}$/.test(row.packageDigest))
        throw new Error('Invalid transaction target');
      seen.add(row.app);
      const rollback = row.rollbackEvidence;
      if (row.revision === record.candidateRevision) {
        if (rollback !== null) throw new Error('Unexpected candidate rollback evidence');
      } else if (!rollback || Object.keys(rollback).some(key => !['artifact', 'runId', 'revision', 'packageDigest'].includes(key)) ||
                 rollback.revision !== row.revision || !Number.isSafeInteger(rollback.runId) || rollback.runId < 1 ||
                 !/^[a-zA-Z0-9_.-]{1,256}$/.test(rollback.artifact) || !/^[a-f0-9]{64}$/.test(rollback.packageDigest))
        throw new Error('Invalid preserved rollback package proof');
    }
    const snapshot = record.historySnapshot;
    if (!snapshot || Object.keys(snapshot).sort().join(',') !== 'candidateRevision,digest,entries,schemaVersion' ||
        snapshot.schemaVersion !== 1 || snapshot.candidateRevision !== record.candidateRevision ||
        !Array.isArray(snapshot.entries) || snapshot.entries.length > 2000)
      throw new Error('Invalid terminal history snapshot');
    const runs = new Set();
    for (const row of snapshot.entries) {
      if (!row || Object.keys(row).sort().join(',') !== 'headSha,runAttempt,runId,targets' ||
          !Number.isSafeInteger(row.runId) || row.runId < 1 || runs.has(row.runId) ||
          !Number.isSafeInteger(row.runAttempt) || row.runAttempt < 1 || !/^[a-f0-9]{40}$/.test(row.headSha) ||
          !Array.isArray(row.targets) || !row.targets.length || row.targets.some(key => !seen.has(key)) ||
          new Set(row.targets).size !== row.targets.length)
        throw new Error('Invalid terminal history exclusion');
      runs.add(row.runId);
    }
    const canonical = value => Array.isArray(value) ? value.map(canonical) :
      value && typeof value === 'object' ? Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])])) : value;
    const body = {schemaVersion: snapshot.schemaVersion, candidateRevision: snapshot.candidateRevision, entries: snapshot.entries};
    if (crypto.createHash('sha256').update(JSON.stringify(canonical(body))).digest('hex') !== snapshot.digest)
      throw new Error('Terminal history snapshot digest mismatch');
  } else if (child) {
    if (record.schemaVersion !== 1 || !['intent', 'success'].includes(record.phase) ||
        name !== `legend-release-child-${record.phase}-${record.dependencyIdentity}` ||
        !/^[a-f0-9]{64}$/.test(record.dependencyIdentity) ||
        ['materialIdentity', 'evidenceIdentity', 'partitionIdentity'].some(key => !/^[a-f0-9]{64}$/.test(record[key])) ||
        !/^[a-z][a-z0-9-]{0,63}$/.test(record.child) ||
        ['applicationRevision', 'executionAuthority'].some(key => !/^[a-f0-9]{40}$/.test(record[key])) ||
        ['producingRun', 'producingAttempt'].some(key => !Number.isSafeInteger(record[key]) || record[key] < 1) ||
        !record.observation || Array.isArray(record.observation) ||
        Object.entries(record.observation).some(([key, value]) =>
          !['providerVersion', 'schemaIdentity'].includes(key) || typeof value !== 'string' ||
          !/^[a-zA-Z0-9_.:-]{1,256}$/.test(value)))
      throw new Error('Invalid release child evidence');
  } else if (admission) {
    if (record.schemaVersion !== 1 || record.phase !== 'admission' ||
        !['automatic', 'explicit'].includes(record.authorizationMode) ||
        name !== `legend-release-admission-${record.admissionId}` ||
        !/^[a-f0-9]{64}$/.test(record.admissionId) ||
        ['applicationRevision', 'authorizedSourceRevision', 'executionAuthority', 'sourceMergeSha'].some(key => !/^[a-f0-9]{40}$/.test(record[key])) ||
        !/^[a-f0-9]{64}$/.test(record.packageIdentity) ||
        !Number.isSafeInteger(record.sourcePr) || record.sourcePr < 0 ||
        ['producingRun', 'producingAttempt'].some(key => !Number.isSafeInteger(record[key]) || record[key] < 1) ||
        ['selectedTargets', 'resources'].some(key => !Array.isArray(record[key]) || !record[key].length || record[key].length > 100 ||
          record[key].some(value => typeof value !== 'string' || !/^[a-zA-Z0-9:/_.-]{1,128}$/.test(value))))
      throw new Error('Invalid release admission identity');
  } else if (record.schemaVersion !== 1 || !['intent', 'success'].includes(record.phase) ||
      name !== `legend-release-operation-${record.phase}-${record.operationId}` ||
      !/^[a-f0-9]{64}$/.test(record.operationId) ||
      !/^[a-f0-9]{64}$/.test(record.packageDigest) ||
      !/^[a-f0-9]{40}$/.test(record.applicationRevision) ||
      !/^[a-f0-9]{40}$/.test(record.baseline) ||
      !/^[a-z][a-z0-9-]{0,63}$/.test(record.target) ||
      ['producingRun', 'producingAttempt', 'packageProducerRun'].some(key =>
        !Number.isSafeInteger(record[key]) || record[key] < 1))
    throw new Error('Invalid release operation identity');
  for (const key of ['baselineDeploymentIds', 'deploymentIds']) {
    if (record[key] !== undefined && (!Array.isArray(record[key]) || record[key].length > 1000 ||
        record[key].some(value => typeof value !== 'string' || !/^[a-zA-Z0-9_.:-]{1,256}$/.test(value))))
      throw new Error('Invalid provider operation identities');
  }
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'legend-operation-'));
  try {
    const filename = plan ? 'release-transaction.json' : 'operation.json';
    const source = path.join(directory, filename);
    const content = JSON.stringify(record);
    await fs.writeFile(source, content, {mode: 0o600});
    // Internal SDK inventory is scoped to this Actions run. Never select the
    // latest matching name: duplicate identities are an unresolved write.
    async function existing() {
      const inventory = await client.listArtifacts({latest: false});
      if (!Array.isArray(inventory.artifacts)) throw new Error('Artifact inventory unavailable');
      const matches = inventory.artifacts.filter(item => item.name === name);
      if (matches.length > 1) throw new Error('Ambiguous release operation artifact identity');
      if (!matches.length) return null;
      if (!Number.isSafeInteger(matches[0].id) || matches[0].id <= 0)
        throw new Error('Invalid durable artifact identity');
      return matches[0].id;
    }
    let artifactId = await existing();
    if (artifactId === null) {
      try {
        const result = await client.uploadArtifact(name, [source], directory, {retentionDays: 90});
        if (!Number.isSafeInteger(result.id) || result.id <= 0)
          throw new Error('Artifact upload did not return durable identity');
        artifactId = result.id;
      } catch {
        // The upload may have committed. Reconcile with bounded read-only observation by exact immutable
        // name and bytes; absence or uncertainty never permits another upload.
        for (let attempt = 0; attempt < 3; attempt++) {
          if (attempt) await sleep(attempt * 1000);
          artifactId = await existing();
          if (artifactId !== null) break;
        }
        if (artifactId === null) throw new Error('Artifact upload outcome unresolved');
      }
    }
    const destination = path.join(directory, 'verified');
    await client.downloadArtifact(artifactId, {path: destination});
    const readback = await fs.readFile(path.join(destination, filename), 'utf8');
    if (readback !== content) throw new Error('Release operation readback mismatch');
    return {artifactId};
  } finally { await fs.rm(directory, {recursive: true, force: true}); }
}

if (require.main === module) {
  (async () => {
    let input = '';
    for await (const chunk of process.stdin) {
      input += chunk;
      if (input.length > 524288) throw new Error('Operation record too large');
    }
    const {name, record} = JSON.parse(input);
    const result = await publish(new DefaultArtifactClient(), name, record);
    process.stdout.write(`LEGEND_OPERATION_RESULT=${JSON.stringify(result)}\n`);
  })().catch(() => {
    // SDK errors can contain signed URLs. Never forward those to release logs.
    process.stderr.write('Durable release operation publication/readback failed; no deployment write authorized.\n');
    process.exitCode = 1;
  });
}
module.exports = {publish};
