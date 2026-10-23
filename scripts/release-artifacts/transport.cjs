'use strict';
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const {DefaultArtifactClient, ArtifactNotFoundError} = require('@actions/artifact');

function componentSource(source) {
  const keys = ['runId', 'runAttempt', 'artifactId', 'artifactDigest', 'artifactName', 'receiptArtifactId', 'producingJobId'];
  if (!source || typeof source !== 'object' || Object.keys(source).sort().join(',') !== keys.sort().join(',') ||
      ['runId', 'runAttempt', 'artifactId', 'receiptArtifactId', 'producingJobId'].some(key => !Number.isSafeInteger(source[key]) || source[key] < 1) ||
      !/^sha256:[a-f0-9]{64}$/.test(source.artifactDigest) || !/^[a-zA-Z0-9_.-]{1,256}$/.test(source.artifactName))
    throw new Error('Invalid immutable component source');
}

function targetMaterial(target, value) {
  if (!value || Object.keys(value).sort().join(',') !== 'contentIdentity,executionIdentity,packageDigest,producerRevision,source' ||
      !/^[a-f0-9]{40}$/.test(value.producerRevision) ||
      ['packageDigest', 'contentIdentity', 'executionIdentity'].some(key => !/^[a-f0-9]{64}$/.test(value[key])))
    throw new Error('Invalid target material');
  componentSource(value.source);
  if (value.source.artifactName !== `validated-component-bytes-${target}-${value.contentIdentity}-${value.source.runId}-a${value.source.runAttempt}`)
    throw new Error('Invalid target artifact name');
}

// Serialization validation is not producer authorization: the Python authority
// independently authenticates dispatch, promotion, source and original artifacts.
const exactKeys = (value, keys) => value && typeof value === 'object' && !Array.isArray(value) &&
  Object.keys(value).sort().join(',') === keys.split(',').sort().join(',');
const positive = value => Number.isSafeInteger(value) && value > 0;
const sha = (value, size) => typeof value === 'string' && new RegExp(`^[a-f0-9]{${size}}$`).test(value);
const digest = value => typeof value === 'string' && /^sha256:[a-f0-9]{64}$/.test(value);
const targets = ['website', 'portal', 'client', 'protect', 'parfait'];
const operationIdentity = (target, revision, packageDigest) => crypto.createHash('sha256')
  .update(JSON.stringify({applicationRevision: revision, packageDigest, target})).digest('hex');
function recoveryObserver(value, run, attempt) {
  return exactKeys(value, 'jobId,promotionRunId,runAttempt,runId,toolRevision') &&
    ['jobId', 'promotionRunId', 'runAttempt', 'runId'].every(key => positive(value[key])) &&
    sha(value.toolRevision, 40) && value.runId === run && value.runAttempt === attempt;
}
function recoveryEvidence(record) {
  const proof = record.recoveryEvidence;
  const keys = ['schemaVersion', 'operationId', 'target', 'applicationRevision', 'packageDigest', 'baseline',
    'packageProducerRun', 'producingRun', 'producingAttempt', 'baselineDeploymentIds', 'phase', 'recoveryEvidence'];
  for (const key of ['providerRequestCorrelation', 'submissionContract']) if (record[key] !== undefined) keys.push(key);
  if (record.phase === 'submitted') keys.push('deploymentId');
  if (record.phase === 'success') keys.push('deploymentIds');
  if (!exactKeys(record, keys.join(',')) || !Array.isArray(record.baselineDeploymentIds) ||
      (record.phase === 'intent' && record.providerRequestCorrelation === undefined && record.submissionContract === undefined) ||
      (record.phase === 'success' && (!Array.isArray(record.deploymentIds) || record.deploymentIds.length !== 1 ||
        record.baselineDeploymentIds.includes(record.deploymentIds[0]))))
    throw new Error('Invalid retained recovery phase fields');
  if (!exactKeys(proof, 'schemaVersion,originalRun,originalAttempt,applicationRevision,packageArtifactId,packageArtifactDigest,jobId,promotionRunId,runAttempt,runId,toolRevision') ||
      proof.schemaVersion !== 1 || record.schemaVersion !== 1 || record.completionEvidence !== undefined ||
      !['originalRun', 'originalAttempt', 'packageArtifactId'].every(key => positive(proof[key])) ||
      !digest(proof.packageArtifactDigest) || proof.originalRun !== record.packageProducerRun ||
      proof.originalRun === record.producingRun || proof.applicationRevision !== record.applicationRevision ||
      !targets.includes(record.target) || record.operationId !== operationIdentity(record.target, record.applicationRevision, record.packageDigest) ||
      !recoveryObserver(Object.fromEntries(['jobId', 'promotionRunId', 'runAttempt', 'runId', 'toolRevision'].map(key => [key, proof[key]])), record.producingRun, record.producingAttempt))
    throw new Error('Invalid retained recovery operation evidence');
}
function retainedSummary(name, record) {
  if (!exactKeys(record, 'schemaVersion,phase,originalRun,originalAttempt,applicationRevision,producingRun,producingAttempt,observer,targets') ||
      record.schemaVersion !== 1 || record.phase !== 'success' || !sha(record.applicationRevision, 40) ||
      !['originalRun', 'originalAttempt', 'producingRun', 'producingAttempt'].every(key => positive(record[key])) ||
      record.originalRun === record.producingRun || !recoveryObserver(record.observer, record.producingRun, record.producingAttempt) ||
      !record.targets || typeof record.targets !== 'object' || Array.isArray(record.targets) ||
      !Object.keys(record.targets).length || Object.keys(record.targets).length > targets.length)
    throw new Error('Invalid retained recovery summary identity');
  const identity = crypto.createHash('sha256').update(JSON.stringify({applicationRevision: record.applicationRevision, originalRun: record.originalRun})).digest('hex');
  if (name !== `legend-retained-recovery-success-${identity}`) throw new Error('Invalid retained recovery summary name');
  for (const [target, row] of Object.entries(record.targets)) {
    if (!targets.includes(target) || !exactKeys(row, 'operationId,packageDigest,packageArtifactId,packageArtifactDigest,deploymentIds') ||
        !sha(row.packageDigest, 64) || !positive(row.packageArtifactId) || !digest(row.packageArtifactDigest) ||
        row.operationId !== operationIdentity(target, record.applicationRevision, row.packageDigest) ||
        !Array.isArray(row.deploymentIds) || row.deploymentIds.length !== 1 ||
        typeof row.deploymentIds[0] !== 'string' || !/^[a-zA-Z0-9_.:-]{1,256}$/.test(row.deploymentIds[0]))
      throw new Error('Invalid retained recovery target evidence');
  }
}

const preflightKeys='schemaVersion,phase,originalRun,originalAttempt,applicationRevision,producingRun,producingAttempt,observer,targets,failedTarget,stage,authorizesMutation,writePhaseEntered,reason,observation';
const preflightReasons=['provider-read-timeout','provider-read-unavailable','history-empty','provider-active','runtime-unavailable','runtime-candidate-unproven','baseline-not-ancestor','original-window-unproven','provider-history-time-unproven','newer-provider-operation','baseline-drift','preflight-authority-unproven','phase-deadline'];
function preflightNonentry(name,record) {
  if(!exactKeys(record,preflightKeys)||record.schemaVersion!==1||record.phase!=='preflight-nonentry'||record.stage!=='all-target-preflight'||record.authorizesMutation!==false||record.writePhaseEntered!==false||!preflightReasons.includes(record.reason)||!sha(record.applicationRevision,40)||!['originalRun','originalAttempt','producingRun','producingAttempt'].every(k=>positive(record[k]))||record.originalRun===record.producingRun||!recoveryObserver(record.observer,record.producingRun,record.producingAttempt)||!record.targets||Array.isArray(record.targets)||!Object.keys(record.targets).length||Object.keys(record.targets).length>5||typeof record.failedTarget!=='string'||!targets.includes(record.failedTarget)||!Object.hasOwn(record.targets,record.failedTarget))throw new Error('Invalid terminal preflight identity');
  const identity={applicationRevision:record.applicationRevision,originalRun:record.originalRun,producingAttempt:record.producingAttempt,producingRun:record.producingRun};
  if(name!=='legend-retained-preflight-nonentry-'+crypto.createHash('sha256').update(JSON.stringify(identity)).digest('hex'))throw new Error('Invalid terminal preflight name');
  for(const [key,row] of Object.entries(record.targets))if(!targets.includes(key)||!exactKeys(row,'operationId,packageDigest,packageArtifactId,packageArtifactDigest')||!sha(row.packageDigest,64)||!positive(row.packageArtifactId)||!digest(row.packageArtifactDigest)||row.operationId!==operationIdentity(key,record.applicationRevision,row.packageDigest))throw new Error('Invalid terminal preflight material');
  const o=record.observation;
  if(!exactKeys(o,'historyReadComplete,historyCount,activeCount,runtimeRevision')||typeof o.historyReadComplete!=='boolean'||(o.runtimeRevision!==null&&!sha(o.runtimeRevision,40))||
      (o.historyReadComplete?(!Number.isSafeInteger(o.historyCount)||o.historyCount<0||o.historyCount>100000||!Number.isSafeInteger(o.activeCount)||o.activeCount<0||o.activeCount>o.historyCount):(o.historyCount!==null||o.activeCount!==null)))throw new Error('Invalid terminal preflight observation');
  if((['provider-read-timeout','provider-read-unavailable'].includes(record.reason)&&o.historyReadComplete)||
     (record.reason==='history-empty'&&(!o.historyReadComplete||o.historyCount!==0))||
     (record.reason==='provider-active'&&(!o.historyReadComplete||o.activeCount<1))||
     (record.reason==='runtime-unavailable'&&o.runtimeRevision!==null)||
     (record.reason==='runtime-candidate-unproven'&&o.runtimeRevision!==record.applicationRevision))throw new Error('Inconsistent terminal preflight observation');
}

const targetHoldKeys=preflightKeys.replace('writePhaseEntered','targetWritePhaseEntered');
function targetPreintent(name,record) {
  const identity={applicationRevision:record.applicationRevision,failedTarget:record.failedTarget,originalRun:record.originalRun,producingAttempt:record.producingAttempt,producingRun:record.producingRun};
  if(!exactKeys(record,targetHoldKeys)||record.targetWritePhaseEntered!==false||record.phase!=='target-preintent-hold'||record.stage!=='before-new-intent'||name!=='legend-retained-target-preintent-'+crypto.createHash('sha256').update(JSON.stringify(identity)).digest('hex'))throw new Error('Invalid target preintent identity');
  const translated={...record,phase:'preflight-nonentry',stage:'all-target-preflight',writePhaseEntered:false};delete translated.targetWritePhaseEntered;
  const globalIdentity={applicationRevision:record.applicationRevision,originalRun:record.originalRun,producingAttempt:record.producingAttempt,producingRun:record.producingRun};
  preflightNonentry('legend-retained-preflight-nonentry-'+crypto.createHash('sha256').update(JSON.stringify(globalIdentity)).digest('hex'),translated);
}

// JSON arrives on stdin, never as an arbitrary upload path. Only the bounded
// release operation record can be persisted; credentials cannot enter it.
async function publish(client, name, record, {sleep = ms => new Promise(resolve => setTimeout(resolve, ms))} = {}) {
  const targetHold = /^legend-retained-target-preintent-[a-f0-9]{64}$/.test(name);
  const preflight = /^legend-retained-preflight-nonentry-[a-f0-9]{64}$/.test(name);
  const retained = /^legend-retained-recovery-success-[a-f0-9]{64}$/.test(name);
  const completion = /^legend-release-completion-[a-f0-9]{64}$/.test(name);
  const observation = /^legend-migration-observation-[a-f0-9]{64}$/.test(name);
  const locator = /^validated-component-index-[a-z][a-z0-9-]{0,63}-[a-f0-9]{64}$/.test(name);
  const admission = /^legend-release-admission-[a-f0-9]{64}$/.test(name);
  const child = /^legend-release-child-(intent|success)-[a-f0-9]{64}$/.test(name);
  const plan = /^legend-release-transaction-plan-[a-f0-9]{64}$/.test(name);
  const recovery = /^legend-readiness-recovery-[a-f0-9]{64}$/.test(name);
  if (!targetHold && !preflight && !retained && !completion && !observation && !locator && !admission && !child && !plan && !recovery && !/^legend-release-operation-(intent|success|submitted)-[a-f0-9]{64}$/.test(name))
    throw new Error('Invalid release operation artifact identity');
  const observationKeys = ['schemaVersion', 'phase', 'kind', 'observationIdentity', 'candidate', 'executionAuthority', 'databaseIdentity', 'schemaIdentity', 'baselineIdentity', 'observedUtc', 'pendingCount', 'mutationActivity', 'historicalExecution', 'historicalEvidenceIdentity', 'historicalReconciliationSources', 'bundleDigest', 'sqlExecutionAuthorized', 'producingRun', 'producingAttempt'];
  const allowed = new Set(targetHold ? targetHoldKeys.split(',') : preflight ? preflightKeys.split(',') : retained ? ['schemaVersion', 'phase', 'originalRun', 'originalAttempt', 'applicationRevision', 'producingRun', 'producingAttempt', 'observer', 'targets'] : completion ? ['schemaVersion', 'phase', 'originalRun', 'applicationRevision', 'producingRun', 'producingAttempt', 'observer', 'targets'] : observation ? observationKeys : locator ? ['schemaVersion', 'component', 'contentIdentity', 'runId'] : recovery ? ['schemaVersion', 'operationId', 'candidateRevision', 'executionAuthority', 'targetRun', 'targetAttempt', 'targetJob', 'producingRun', 'producingAttempt', 'phase'] : plan ? ['schemaVersion', 'planId', 'candidateRevision', 'producingRun', 'producingAttempt', 'targets', 'historySnapshot'] : child ? ['schemaVersion', 'child', 'dependencyIdentity', 'materialIdentity', 'evidenceIdentity', 'partitionIdentity',
    'applicationRevision', 'executionAuthority', 'producingRun', 'producingAttempt', 'phase', 'observation'] : admission ? ['schemaVersion', 'admissionId', 'sourcePr',
    'applicationRevision', 'authorizedSourceRevision', 'packageIdentity', 'executionAuthority', 'sourceMergeSha', 'selectedTargets',
    'resources', 'authorizationMode', 'producingRun', 'producingAttempt', 'phase'] : ['schemaVersion', 'operationId', 'target', 'applicationRevision',
    'packageDigest', 'baseline', 'packageProducerRun', 'producingRun', 'producingAttempt',
    'baselineDeploymentIds', 'phase', 'deploymentIds', 'candidateRevision', 'componentSource',
    'providerRequestCorrelation', 'submissionContract', 'deploymentId', 'completionEvidence', 'recoveryEvidence']);
  if (!record || Object.keys(record).some(key => !allowed.has(key)) ||
      JSON.stringify(record).length > (plan ? 524288 : 32768)) throw new Error('Invalid release operation record');
  if (targetHold) {
    targetPreintent(name, record);
  } else if (preflight) {
    preflightNonentry(name, record);
  } else if (retained) {
    retainedSummary(name, record);
  } else if (completion) {
    const keys = 'applicationRevision,observer,originalRun,phase,producingAttempt,producingRun,schemaVersion,targets';
    if (Object.keys(record).sort().join(',') !== keys || record.schemaVersion !== 1 || record.phase !== 'success' ||
        !/^[a-f0-9]{40}$/.test(record.applicationRevision) ||
        ['originalRun', 'producingRun', 'producingAttempt'].some(k => !Number.isSafeInteger(record[k]) || record[k] < 1) ||
        !record.observer || Object.keys(record.observer).sort().join(',') !== 'jobId,promotionRunId,runAttempt,runId,toolRevision' ||
        ['jobId', 'promotionRunId', 'runAttempt', 'runId'].some(k => !Number.isSafeInteger(record.observer[k]) || record.observer[k] < 1) ||
        !/^[a-f0-9]{40}$/.test(record.observer.toolRevision) || record.observer.runId !== record.producingRun ||
        record.observer.runAttempt !== record.producingAttempt || !record.targets || Array.isArray(record.targets) ||
        !Object.keys(record.targets).length || Object.keys(record.targets).length > 10)
      throw new Error('Invalid completion-only summary identity');
    const expected = crypto.createHash('sha256').update(JSON.stringify({applicationRevision: record.applicationRevision, originalRun: record.originalRun})).digest('hex');
    if (name !== `legend-release-completion-${expected}`) throw new Error('Invalid completion-only summary name');
    for (const [key, target] of Object.entries(record.targets)) {
      if (!['website', 'portal', 'client', 'protect', 'parfait'].includes(key) || !target ||
          Object.keys(target).sort().join(',') !== 'deploymentIds,intentArtifactId,operationId,packageArtifactId,packageDigest' ||
          ['intentArtifactId', 'packageArtifactId'].some(k => !Number.isSafeInteger(target[k]) || target[k] < 1) ||
          ['operationId', 'packageDigest'].some(k => !/^[a-f0-9]{64}$/.test(target[k])) ||
          !Array.isArray(target.deploymentIds) || target.deploymentIds.length !== 1 ||
          !/^[a-zA-Z0-9_.:-]{1,256}$/.test(target.deploymentIds[0]))
        throw new Error('Invalid completion-only target evidence');
    }
  } else if (observation) {
    if (Object.keys(record).sort().join(',') !== observationKeys.sort().join(',') ||
        record.schemaVersion !== 1 || record.phase !== 'observation' || record.kind !== 'current-schema-no-write' ||
        record.pendingCount !== 0 || record.mutationActivity !== 'settled' || record.sqlExecutionAuthorized !== false ||
        !['proven-nonentry', 'proven-completion', 'outcome-unknown'].includes(record.historicalExecution) ||
        ['candidate', 'executionAuthority'].some(key => !/^[a-f0-9]{40}$/.test(record[key])) ||
        ['observationIdentity', 'databaseIdentity', 'schemaIdentity', 'baselineIdentity', 'historicalEvidenceIdentity', 'bundleDigest'].some(key => !/^[a-f0-9]{64}$/.test(record[key])) ||
        ['producingRun', 'producingAttempt'].some(key => !Number.isSafeInteger(record[key]) || record[key] < 1) ||
        typeof record.observedUtc !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/.test(record.observedUtc) ||
        !Number.isFinite(Date.parse(record.observedUtc)) ||
        !Array.isArray(record.historicalReconciliationSources) || record.historicalReconciliationSources.length > 1000)
      throw new Error('Invalid migration observation');
    const seen = new Set();
    for (const source of record.historicalReconciliationSources) {
      if (!source || Object.keys(source).some(key => !['run', 'attempt', 'code', 'sourceBlob', 'stateArtifactId', 'admissionId'].includes(key)) ||
          ['run', 'attempt'].some(key => !Number.isSafeInteger(source[key]) || source[key] < 1) ||
          !/^[a-f0-9]{40}$/.test(source.sourceBlob) ||
          !['HISTORICAL_EXECUTION_REQUIRES_RECONCILIATION', 'POSSIBLE_SQL_WRITE_REQUIRES_LIVE_FENCE', 'EF_MIGRATION_EXECUTION_ENTERED'].includes(source.code) ||
          (source.stateArtifactId !== undefined && (!Number.isSafeInteger(source.stateArtifactId) || source.stateArtifactId < 1)) ||
          (source.admissionId !== undefined && !/^[a-f0-9]{64}$/.test(source.admissionId)) ||
          (source.code === 'HISTORICAL_EXECUTION_REQUIRES_RECONCILIATION' && (source.stateArtifactId === undefined || source.admissionId === undefined)) ||
          seen.has(`${source.run}/${source.attempt}`))
        throw new Error('Invalid migration observation source');
      seen.add(`${source.run}/${source.attempt}`);
    }
    const canonical = value => Array.isArray(value) ? value.map(canonical) :
      value && typeof value === 'object' ? Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])])) : value;
    const body = {...record}; delete body.observationIdentity;
    if (name !== `legend-migration-observation-${record.observationIdentity}` ||
        crypto.createHash('sha256').update(JSON.stringify(canonical(body))).digest('hex') !== record.observationIdentity)
      throw new Error('Invalid migration observation digest');
  } else if (locator) {
    if (record.schemaVersion !== 1 || !/^[a-z][a-z0-9-]{0,63}$/.test(record.component) ||
        !/^[a-f0-9]{64}$/.test(record.contentIdentity) || !Number.isSafeInteger(record.runId) || record.runId < 1 ||
        name !== `validated-component-index-${record.component}-${record.contentIdentity}`)
      throw new Error('Invalid component run locator');
  } else if (recovery) {
    if (record.schemaVersion !== 1 || record.phase !== 'intent' || name !== `legend-readiness-recovery-${record.operationId}` ||
        !/^[a-f0-9]{64}$/.test(record.operationId) ||
        ['candidateRevision', 'executionAuthority'].some(key => !/^[a-f0-9]{40}$/.test(record[key])) ||
        ['targetRun', 'targetAttempt', 'targetJob', 'producingRun', 'producingAttempt'].some(key => !Number.isSafeInteger(record[key]) || record[key] < 1))
      throw new Error('Invalid readiness recovery intent');
  } else if (plan) {
    if (![1, 2].includes(record.schemaVersion) || name !== `legend-release-transaction-plan-${record.planId}` ||
        !/^[a-f0-9]{64}$/.test(record.planId) || !/^[a-f0-9]{40}$/.test(record.candidateRevision) ||
        ['producingRun', 'producingAttempt'].some(key => !Number.isSafeInteger(record[key]) || record[key] < 1) ||
        !Array.isArray(record.targets) || !record.targets.length || record.targets.length > 100)
      throw new Error('Invalid transaction plan identity');
    const seen = new Set();
    for (const row of record.targets) {
      if (!row || Object.keys(row).some(key => !(record.schemaVersion === 2 ? ['app', 'revision', 'packageDigest', 'rollbackEvidence', 'targetMaterial'] : ['app', 'revision', 'packageDigest', 'rollbackEvidence']).includes(key)) ||
          !/^[a-z][a-z0-9-]{0,63}$/.test(row.app) || seen.has(row.app) ||
          !/^[a-f0-9]{40}$/.test(row.revision) || !/^[a-f0-9]{64}$/.test(row.packageDigest))
        throw new Error('Invalid transaction target');
      seen.add(row.app);
      if (record.schemaVersion === 2) {
        targetMaterial(row.app, row.targetMaterial);
        if (row.targetMaterial.packageDigest !== row.packageDigest) throw new Error('Transaction material digest mismatch');
      }
      const rollback = row.rollbackEvidence;
      if (row.revision === (record.schemaVersion === 2 ? row.targetMaterial.producerRevision : record.candidateRevision)) {
        if (rollback !== null) throw new Error('Unexpected candidate rollback evidence');
      } else if (!rollback || Object.keys(rollback).some(key => !['artifact', 'runId', 'revision', 'packageDigest', 'artifactId'].includes(key)) ||
                 (rollback.artifactId !== undefined && (!Number.isSafeInteger(rollback.artifactId) || rollback.artifactId < 1)) ||
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
  } else if (![1, 2].includes(record.schemaVersion) || !['intent', 'success', 'submitted'].includes(record.phase) ||
      name !== `legend-release-operation-${record.phase}-${record.operationId}` ||
      !/^[a-f0-9]{64}$/.test(record.operationId) ||
      !/^[a-f0-9]{64}$/.test(record.packageDigest) ||
      !/^[a-f0-9]{40}$/.test(record.applicationRevision) ||
      !/^[a-f0-9]{40}$/.test(record.baseline) ||
      !/^[a-z][a-z0-9-]{0,63}$/.test(record.target) ||
      ['producingRun', 'producingAttempt', 'packageProducerRun'].some(key =>
        !Number.isSafeInteger(record[key]) || record[key] < 1))
    throw new Error('Invalid release operation identity');
  if (!preflight && !retained && !completion && !observation && !locator && !recovery && !plan && !child && !admission) {
    if (record.recoveryEvidence !== undefined) recoveryEvidence(record);
    if (record.completionEvidence !== undefined) {
      const proof = record.completionEvidence;
      if (record.schemaVersion !== 1 || record.phase !== 'success' || !proof ||
          Object.keys(proof).sort().join(',') !== 'intentArtifactDigest,intentArtifactId,observer,originalJobId,originalPackageAttempt,originalPackageJobId,originalRun,originalRunAttempt,packageArtifactDigest,packageArtifactId,provider' ||
          ['originalRun', 'originalRunAttempt', 'originalPackageJobId', 'originalPackageAttempt', 'originalJobId', 'intentArtifactId', 'packageArtifactId'].some(k => !Number.isSafeInteger(proof[k]) || proof[k] < 1) ||
          ['intentArtifactDigest', 'packageArtifactDigest'].some(k => !/^sha256:[a-f0-9]{64}$/.test(proof[k])) ||
          proof.originalRun !== record.packageProducerRun || !proof.observer ||
          Object.keys(proof.observer).sort().join(',') !== 'jobId,promotionRunId,runAttempt,runId,toolRevision' ||
          ['runId', 'runAttempt', 'jobId', 'promotionRunId'].some(k => !Number.isSafeInteger(proof.observer[k]) || proof.observer[k] < 1) ||
          proof.observer.runId !== record.producingRun || proof.observer.runAttempt !== record.producingAttempt ||
          !/^[a-f0-9]{40}$/.test(proof.observer.toolRevision) || !proof.provider ||
          Object.keys(proof.provider).sort().join(',') !== 'deploymentId,receivedTime,runtimeRevision,status' ||
          proof.provider.status !== 4 || proof.provider.runtimeRevision !== record.applicationRevision ||
          typeof proof.provider.receivedTime !== 'string' || !Number.isFinite(Date.parse(proof.provider.receivedTime)) ||
          !Array.isArray(record.deploymentIds) || record.deploymentIds.length !== 1 ||
          record.deploymentIds[0] !== proof.provider.deploymentId || !Array.isArray(record.baselineDeploymentIds) ||
          record.baselineDeploymentIds.includes(proof.provider.deploymentId))
        throw new Error('Invalid completion-only operation evidence');
    }
    for (const [key, kind] of [['providerRequestCorrelation', 'azure_onedeploy_deployment_tag_v1'],
                              ['submissionContract', 'azure_onedeploy_windows_intent_v1']]) {
      if (record[key] !== undefined && (!record[key] || typeof record[key] !== 'object' || Array.isArray(record[key]) ||
          Object.keys(record[key]).sort().join(',') !== 'kind,value' || record[key].kind !== kind ||
          typeof record[key].value !== 'string' || record[key].value !== record.operationId))
        throw new Error('Invalid deployment submission contract');
    }
    if (record.providerRequestCorrelation !== undefined && record.submissionContract !== undefined)
      throw new Error('Conflicting deployment submission contracts');
    if (record.phase === 'submitted') {
      if (record.submissionContract === undefined || record.deploymentIds !== undefined ||
          !Array.isArray(record.baselineDeploymentIds) || typeof record.deploymentId !== 'string' ||
          !/^[a-zA-Z0-9_.:-]{1,256}$/.test(record.deploymentId) ||
          record.baselineDeploymentIds.includes(record.deploymentId))
        throw new Error('Invalid provider submission observation');
    } else if (record.deploymentId !== undefined) {
      throw new Error('Provider observation identity has incorrect phase');
    }
    if (record.schemaVersion === 2) {
      componentSource(record.componentSource);
      if (!/^[a-f0-9]{40}$/.test(record.candidateRevision) || record.packageProducerRun !== record.componentSource.runId)
        throw new Error('Invalid operation authorization/producer');
    } else if (record.candidateRevision !== undefined || record.componentSource !== undefined) {
      throw new Error('Legacy operation has unversioned component provenance');
    }
  }
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
    // Retry only recognized transient reads of the SAME immutable artifact.
    // The Python owner retains the overall subprocess deadline. No readback
    // error returns to upload, and malformed content is never retried.
    for (let attempt = 0; attempt < 3; attempt++) {
      const destination = path.join(directory, `verified-${attempt}`);
      try {
        await client.downloadArtifact(artifactId, {path: destination});
      } catch (error) {
        const status = error.statusCode ?? error.status ?? error.response?.status;
        const transient = error instanceof ArtifactNotFoundError ||
          [404, 408, 429, 500, 502, 503, 504].includes(status) ||
          ['ECONNRESET', 'ETIMEDOUT', 'EAI_AGAIN', 'UND_ERR_CONNECT_TIMEOUT'].includes(error.code);
        if (!transient || attempt === 2) throw error;
        await sleep((attempt + 1) * 1000);
        continue;
      }
      const readback = await fs.readFile(path.join(destination, filename), 'utf8');
      if (readback !== content) throw new Error('Release operation readback mismatch');
      return {artifactId};
    }
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
