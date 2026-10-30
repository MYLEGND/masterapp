'use strict';
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');

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
// Fixed generation only. Serializer shape validation is not producer authority.
const protect161Replacement = {"acceptLateCompletionRisk": true, "applicationRevision": "c8fde29a67fbb500bbd7577c17298703d04e825c", "authorizationId": "protect-release161-one-replacement-20261029", "baselineReceiptDigest": "sha256:55652cc0e83f0170717ce8789217c44d3c5f04ac0064dc2529a172388eb764f8", "baselineReceiptId": 12926290946, "baselineRevision": "10c5c3634f8899fdeecdfd2f84e2e1a5333114d7", "candidateArtifactDigest": "sha256:5e4fbb337ad3d5348380f7a69ad55abd0ad9b869f49bb65af95ca3fe95c769b3", "candidateArtifactId": 12929203115, "historicalOutcome": "UNKNOWN", "originalIntentArtifactDigest": "sha256:f1215af45675fea2020761f0c065db6a5b7cb798ce0b117df74761ed2bce3862", "originalIntentArtifactId": 12929917437, "originalOperationId": "68ef98f88a78383b645d7d1f48e75a4d2c7c0e7766746d55e2e81e5adb3eff7e", "originalRun": 40946628155, "packageDigest": "726c3bfd496979dee7ad0c7b079025dc9d9ee51b4ec52b5edc677a986d3b61d2", "rollbackArtifactDigest": "sha256:e7806c97811b6b0fe787f0135ea46dfa1e9664410a2d3e7b28f6482d13701010", "rollbackArtifactId": 12924409528, "rollbackPackageDigest": "c989444f9218bb6db17fcbbf9ce330d2b6f510c7fee47543316be03115b41eee", "rollbackRun": 40935932531, "schemaVersion": 1, "target": "protect"};
function retainedOperationIdentity(target, revision, packageDigest, originalRun) {
  const p = protect161Replacement;
  if (originalRun !== p.originalRun || target !== p.target || revision !== p.applicationRevision || packageDigest !== p.packageDigest)
    return operationIdentity(target, revision, packageDigest);
  return crypto.createHash('sha256').update(JSON.stringify({applicationRevision: revision,
    authorizationId: p.authorizationId, packageDigest, supersededOperationId: p.originalOperationId, target})).digest('hex');
}
function unknownReplacement(record, value) {
  const p = protect161Replacement;
  if (!exactKeys(value, Object.keys(p).join(',')) || Object.keys(p).some(k => value[k] !== p[k]) ||
      record.target !== p.target || record.applicationRevision !== p.applicationRevision ||
      record.packageDigest !== p.packageDigest || record.baseline !== p.baselineRevision ||
      record.recoveryEvidence.originalRun !== p.originalRun ||
      record.recoveryEvidence.packageArtifactId !== p.candidateArtifactId ||
      record.recoveryEvidence.packageArtifactDigest !== p.candidateArtifactDigest)
    throw new Error('Invalid exact Protect 161 replacement evidence');
}
function recoveryObserver(value, run, attempt) {
  return exactKeys(value, 'jobId,promotionRunId,runAttempt,runId,toolRevision') &&
    ['jobId', 'promotionRunId', 'runAttempt', 'runId'].every(key => positive(value[key])) &&
    sha(value.toolRevision, 40) && value.runId === run && value.runAttempt === attempt;
}
function recoveryEvidence(record) {
  const proof = record.recoveryEvidence;
  const replacement = proof?.unknownReplacement;
  if (replacement !== undefined) unknownReplacement(record, replacement);
  const correction = proof?.protectBaselineCorrection;
  if (correction !== undefined) protectBaselineCorrection(record, correction);
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
  if (!exactKeys(proof, 'schemaVersion,originalRun,originalAttempt,applicationRevision,packageArtifactId,packageArtifactDigest,jobId,promotionRunId,runAttempt,runId,toolRevision' + (correction === undefined ? '' : ',protectBaselineCorrection') + (replacement === undefined ? '' : ',unknownReplacement')) ||
      proof.schemaVersion !== 1 || record.schemaVersion !== 1 || record.completionEvidence !== undefined ||
      !['originalRun', 'originalAttempt', 'packageArtifactId'].every(key => positive(proof[key])) ||
      !digest(proof.packageArtifactDigest) || proof.originalRun !== record.packageProducerRun ||
      proof.originalRun === record.producingRun || proof.applicationRevision !== record.applicationRevision ||
      !targets.includes(record.target) || record.operationId !== (replacement === undefined ? operationIdentity(record.target, record.applicationRevision, record.packageDigest) : retainedOperationIdentity(record.target, record.applicationRevision, record.packageDigest, proof.originalRun)) ||
      !recoveryObserver(Object.fromEntries(['jobId', 'promotionRunId', 'runAttempt', 'runId', 'toolRevision'].map(key => [key, proof[key]])), record.producingRun, record.producingAttempt))
    throw new Error('Invalid retained recovery operation evidence');
}
function protectBaselineCorrection(record, value) {
  const proof = record.recoveryEvidence;
  const keys = 'schemaVersion,authorization,baselineRevision,rollbackRun,rollbackArtifactId,rollbackArtifactName,rollbackArtifactDigest,rollbackPackageDigest,baselineReceiptId,baselineReceiptDigest,originalRun,applicationRevision,candidateArtifactId,candidateArtifactDigest';
  const auth = value?.authorization;
  if (!exactKeys(value, keys) || value.schemaVersion !== 1 || record.target !== 'protect' ||
      value.baselineRevision !== record.baseline || !sha(value.baselineRevision, 40) ||
      value.applicationRevision !== record.applicationRevision || value.originalRun !== proof.originalRun ||
      value.candidateArtifactId !== proof.packageArtifactId || value.candidateArtifactDigest !== proof.packageArtifactDigest ||
      !['rollbackRun','rollbackArtifactId','baselineReceiptId','originalRun','candidateArtifactId'].every(k => positive(value[k])) ||
      !['rollbackArtifactDigest','baselineReceiptDigest','candidateArtifactDigest'].every(k => digest(value[k])) ||
      !sha(value.rollbackPackageDigest,64) || value.rollbackArtifactName !== `fast-track-package-${value.baselineRevision}-protect` ||
      !exactKeys(auth,'authorizationId,authorizedBy,target,originalRun,originalOperationId,originalRevision,expectedReleaseParent,historicalOutcome,authorizeOneReplacement,rollbackArtifactId,rollbackArtifactDigest,candidateRevision') ||
      auth.authorizationId !== 'ENG-FORMS-RELEASE-MANUAL-HANDOFF-20261023:v1:protect-replacement-20261026' ||
      auth.authorizedBy !== 'Founder' || auth.target !== 'protect' || auth.historicalOutcome !== 'UNKNOWN' ||
      auth.authorizeOneReplacement !== true || auth.candidateRevision !== record.applicationRevision ||
      !positive(auth.originalRun) || !positive(auth.rollbackArtifactId) || !digest(auth.rollbackArtifactDigest) ||
      !sha(auth.originalOperationId,64) || !sha(auth.originalRevision,40) || !sha(auth.expectedReleaseParent,40))
    throw new Error('Invalid Protect baseline correction');
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
        row.operationId !== retainedOperationIdentity(target, record.applicationRevision, row.packageDigest, record.originalRun) ||
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
  for(const [key,row] of Object.entries(record.targets))if(!targets.includes(key)||!exactKeys(row,'operationId,packageDigest,packageArtifactId,packageArtifactDigest')||!sha(row.packageDigest,64)||!positive(row.packageArtifactId)||!digest(row.packageArtifactDigest)||row.operationId!==retainedOperationIdentity(key,record.applicationRevision,row.packageDigest,record.originalRun))throw new Error('Invalid terminal preflight material');
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


function replacementAdmission(value) {
  const keys = 'admissionId,applicationRevision,authorizesOneReplacement,baselineRevision,contentMode,controlRevision,expectedHead,expiresAtUtc,independentReviewDigest,noSqlEvidenceDigest,observedAtUtc,originalOperationId,packageDigest,phase,rollbackEvidenceDigest,rollbackPackageDigest,schemaVersion,serializationFenceEvidenceDigest,serializationKey,target,terminalNonresumableEvidenceDigest';
  if (!exactKeys(value, keys) || value.schemaVersion !== 1 || value.phase !== 'replacement-admission' ||
      value.target !== 'protect' || value.authorizesOneReplacement !== true ||
      value.contentMode !== 'deterministic-full-replacement' || value.serializationKey !== 'legend-approved-release' ||
      !['applicationRevision','baselineRevision','controlRevision','expectedHead'].every(k => sha(value[k],40)) ||
      !['admissionId','independentReviewDigest','noSqlEvidenceDigest','originalOperationId','packageDigest',
         'rollbackEvidenceDigest','rollbackPackageDigest','serializationFenceEvidenceDigest',
         'terminalNonresumableEvidenceDigest'].every(k => sha(value[k],64)) ||
      typeof value.observedAtUtc !== 'string' || !Number.isFinite(Date.parse(value.observedAtUtc)) ||
      typeof value.expiresAtUtc !== 'string' || !Number.isFinite(Date.parse(value.expiresAtUtc)))
    throw new Error('Invalid replacement recovery admission');
}
function replacementOperation(name, record) {
  const match = /^legend-replacement-operation-(intent|submitted|ambiguous|success|failure|resolution-success|resolution-failure)-([a-f0-9]{64})$/.exec(name);
  if (!match || !record || record.schemaVersion !== 1 || record.phase !== match[1] ||
      record.operationId !== match[2] || record.target !== 'protect' ||
      !sha(record.applicationRevision,40) || !sha(record.packageDigest,64) || !sha(record.baseline,40) ||
      !sha(record.replacementOfOperationId,64) || !sha(record.recoveryAdmissionId,64) ||
      !positive(record.packageProducerRun) || !positive(record.producingRun) || !positive(record.producingAttempt) ||
      !Array.isArray(record.baselineDeploymentIds) ||
      record.baselineDeploymentIds.some(v => typeof v !== 'string' || !/^[a-zA-Z0-9_.:-]{1,256}$/.test(v)))
    throw new Error('Invalid replacement recovery operation');
  replacementAdmission(record.recoveryAdmission);
  if (record.recoveryAdmission.admissionId !== record.recoveryAdmissionId ||
      record.recoveryAdmission.originalOperationId !== record.replacementOfOperationId ||
      record.recoveryAdmission.applicationRevision !== record.applicationRevision ||
      record.recoveryAdmission.packageDigest !== record.packageDigest ||
      record.recoveryAdmission.baselineRevision !== record.baseline)
    throw new Error('Replacement recovery admission does not bind operation');
  const contracts = ['providerRequestCorrelation','submissionContract'].filter(k => record[k] !== undefined);
  if (contracts.length !== 1) throw new Error('Replacement recovery operation requires one submission contract');
  const contract = record[contracts[0]];
  const kind = contracts[0] === 'submissionContract' ? 'azure_onedeploy_windows_intent_v1' : 'azure_onedeploy_deployment_tag_v1';
  if (!exactKeys(contract,'kind,value') || contract.kind !== kind || contract.value !== record.operationId)
    throw new Error('Invalid replacement recovery submission contract');
  const common = ['schemaVersion','phase','operationId','target','applicationRevision','packageDigest','baseline',
    'packageProducerRun','producingRun','producingAttempt','baselineDeploymentIds','replacementOfOperationId',
    'recoveryAdmissionId','recoveryAdmission',contracts[0]];
  const extra = {
    intent: [],
    submitted: ['deploymentId'],
    ambiguous: ['ambiguityEvidenceDigest','replayAuthorized'],
    success: ['deploymentIds','providerTerminalStatus','runtimeRevision','liveAcceptanceDigest','contentVerificationDigest','serializationReleaseDigest'],
    failure: ['failureCode','terminalEvidenceDigest','serializationReleaseDigest','replayAuthorized'],
    'resolution-success': ['deploymentIds','providerTerminalStatus','runtimeRevision','liveAcceptanceDigest','contentVerificationDigest','serializationReleaseDigest','ambiguityRecordDigest','reconciliationEvidenceDigest'],
    'resolution-failure': ['failureCode','terminalEvidenceDigest','serializationReleaseDigest','replayAuthorized','ambiguityRecordDigest','reconciliationEvidenceDigest'],
  }[record.phase];
  if (!exactKeys(record, common.concat(extra).join(','))) throw new Error('Invalid replacement recovery phase shape');
  for (const key of ['ambiguityEvidenceDigest','liveAcceptanceDigest','contentVerificationDigest','serializationReleaseDigest',
                     'terminalEvidenceDigest','ambiguityRecordDigest','reconciliationEvidenceDigest'])
    if (record[key] !== undefined && !sha(record[key],64)) throw new Error('Invalid replacement recovery evidence digest');
  if (record.deploymentId !== undefined && (typeof record.deploymentId !== 'string' || record.baselineDeploymentIds.includes(record.deploymentId)))
    throw new Error('Invalid replacement recovery deployment observation');
  if (record.deploymentIds !== undefined && (!Array.isArray(record.deploymentIds) || record.deploymentIds.length !== 1 ||
      record.baselineDeploymentIds.includes(record.deploymentIds[0]))) throw new Error('Invalid replacement recovery deployment result');
  if ((record.phase === 'success' || record.phase === 'resolution-success') &&
      (record.providerTerminalStatus !== 4 || record.runtimeRevision !== record.applicationRevision))
    throw new Error('Invalid replacement recovery terminal success');
  if ((record.phase === 'ambiguous' || record.phase === 'failure' || record.phase === 'resolution-failure') &&
      record.replayAuthorized !== false) throw new Error('Replacement recovery replay must remain disabled');
  return true;
}

// JSON arrives on stdin, never as an arbitrary upload path. Only the bounded
// release operation record can be persisted; credentials cannot enter it.
function supersessionRecord(record) {
  const value = record.supersession;
  const keys = 'authorizationId,authorizedBy,target,originalRun,originalOperationId,originalRevision,expectedReleaseParent,historicalOutcome,authorizeOneReplacement,rollbackArtifactId,rollbackArtifactDigest,candidateRevision';
  if (!exactKeys(value, keys) || record.schemaVersion !== 1 || record.target !== 'protect' ||
      !sha(value.candidateRevision, 40) || value.candidateRevision !== record.applicationRevision ||
      record.baseline !== value.originalRevision ||
      ['recoveryEvidence', 'completionEvidence', 'componentSource', 'candidateRevision'].some(key => record[key] !== undefined))
    throw new Error('Invalid Protect supersession record');
  // Serializer pins only: Python retains source, ancestry and live writer authority.
  const pins = new Set([
    'aa65d799221ecbafd6ec316a6bce8e7049ee5ac7202ce00bc879e068a1940737', // protect-replacement-20261026
    '123a6d98e874738eb4b630550e0de706a0a053a7ed0ec128cc79ba7a2f34dc17' // fresh-protect-20261028
  ]);
  const fixed = Object.fromEntries(Object.keys(value).filter(key => key !== 'candidateRevision').sort().map(key => [key, value[key]]));
  const fingerprint = crypto.createHash('sha256').update(JSON.stringify(fixed)).digest('hex');
  const identity = {applicationRevision: record.applicationRevision, packageDigest: record.packageDigest, target: record.target};
  if (!pins.has(fingerprint) || crypto.createHash('sha256').update(JSON.stringify(identity)).digest('hex') !== record.operationId)
    throw new Error('Invalid Protect supersession identity');
}

function validateRecord(name, record) {
  if (/^legend-retained-schema-proof-[a-f0-9]{64}$/.test(name)) {
    const canonical = value => Array.isArray(value) ? value.map(canonical) :
      value && typeof value === 'object' ? Object.fromEntries(Object.keys(value).sort().map(key => [key,canonical(value[key])])) : value;
    const keys='schemaVersion,phase,originalRun,applicationRevision,producingRun,producingAttempt,observer,originalSuccessArtifact,observation,historyIdentity,recovery,sqlExecutionAuthorized';
    if (!record || !exactKeys(record,keys) || JSON.stringify(record).length>32768 || record.schemaVersion!==1 ||
        record.phase!=='retained-schema-read-only' || record.originalRun!==41131223973 ||
        record.applicationRevision!=='3243221ae661b53d60f78cc2dab108a93bad7632' || record.originalSuccessArtifact!==13017640415 ||
        !positive(record.producingRun) || !positive(record.producingAttempt) || record.producingRun===record.originalRun ||
        !recoveryObserver(record.observer,record.producingRun,record.producingAttempt) ||
        !sha(record.historyIdentity,64) || record.sqlExecutionAuthorized!==false)
      throw new Error('Invalid retained schema proof');
    const o=record.observation,r=record.recovery;
    if (!o || !exactKeys(o,'databaseIdentity,schemaIdentity,baselineIdentity,observedUtc,pendingCount,ready,mutationActivity') ||
        o.databaseIdentity!=='3a055450807aeaa2c6465702b983dc99968329389d7c0ae480c3c72cc3a9ff3b' ||
        o.schemaIdentity!=='0e4674714d917c5f2dbfa3cd42f2de954e7b3f310c8322c08ec0f427d3615451' ||
        !sha(o.baselineIdentity,64) || !Number.isFinite(Date.parse(o.observedUtc)) ||
        o.pendingCount!==0 || o.ready!==true || o.mutationActivity!=='settled' ||
        !r || !exactKeys(r,'databaseIdentity,observedUtc,restorePointUtc,earliestRestoreUtc,retentionDays,restoreWindowVerified,restoreExecuted,authorizesMutation') ||
        r.databaseIdentity!==o.databaseIdentity || !['observedUtc','restorePointUtc','earliestRestoreUtc'].every(k=>Number.isFinite(Date.parse(r[k]))) ||
        Date.parse(r.earliestRestoreUtc)>Date.parse(r.restorePointUtc) || Date.parse(r.restorePointUtc)>=Date.parse(r.observedUtc) ||
        !Number.isSafeInteger(r.retentionDays) || r.retentionDays<7 || r.restoreWindowVerified!==true ||
        r.restoreExecuted!==false || r.authorizesMutation!==false ||
        name!==`legend-retained-schema-proof-${crypto.createHash('sha256').update(JSON.stringify(canonical(record))).digest('hex')}`)
      throw new Error('Invalid retained schema observation');
    return {filename:'operation.json'};
  }
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
  const replacement = /^legend-replacement-operation-(intent|submitted|ambiguous|success|failure|resolution-success|resolution-failure)-[a-f0-9]{64}$/.test(name);
  if (!targetHold && !preflight && !retained && !completion && !observation && !locator && !admission && !child && !plan && !recovery && !replacement && !/^legend-release-operation-(intent|success|submitted)-[a-f0-9]{64}$/.test(name))
    throw new Error('Invalid release operation artifact identity');
  const observationKeys = ['schemaVersion', 'phase', 'kind', 'observationIdentity', 'candidate', 'executionAuthority', 'databaseIdentity', 'schemaIdentity', 'baselineIdentity', 'observedUtc', 'pendingCount', 'mutationActivity', 'historicalExecution', 'historicalEvidenceIdentity', 'historicalReconciliationSources', 'bundleDigest', 'sqlExecutionAuthorized', 'producingRun', 'producingAttempt'];
  if (replacement) replacementOperation(name, record);
  const allowed = new Set(replacement ? Object.keys(record) : targetHold ? targetHoldKeys.split(',') : preflight ? preflightKeys.split(',') : retained ? ['schemaVersion', 'phase', 'originalRun', 'originalAttempt', 'applicationRevision', 'producingRun', 'producingAttempt', 'observer', 'targets'] : completion ? ['schemaVersion', 'phase', 'originalRun', 'applicationRevision', 'producingRun', 'producingAttempt', 'observer', 'targets'] : observation ? observationKeys : locator ? ['schemaVersion', 'component', 'contentIdentity', 'runId'] : recovery ? ['schemaVersion', 'operationId', 'candidateRevision', 'executionAuthority', 'targetRun', 'targetAttempt', 'targetJob', 'producingRun', 'producingAttempt', 'phase'] : plan ? ['schemaVersion', 'planId', 'candidateRevision', 'producingRun', 'producingAttempt', 'targets', 'historySnapshot'] : child ? ['schemaVersion', 'child', 'dependencyIdentity', 'materialIdentity', 'evidenceIdentity', 'partitionIdentity',
    'applicationRevision', 'executionAuthority', 'producingRun', 'producingAttempt', 'phase', 'observation'] : admission ? ['schemaVersion', 'admissionId', 'sourcePr',
    'applicationRevision', 'authorizedSourceRevision', 'packageIdentity', 'executionAuthority', 'sourceMergeSha', 'selectedTargets',
    'resources', 'authorizationMode', 'producingRun', 'producingAttempt', 'phase'] : ['schemaVersion', 'operationId', 'target', 'applicationRevision',
    'packageDigest', 'baseline', 'packageProducerRun', 'producingRun', 'producingAttempt',
    'baselineDeploymentIds', 'phase', 'deploymentIds', 'candidateRevision', 'componentSource',
    'providerRequestCorrelation', 'submissionContract', 'deploymentId', 'completionEvidence', 'recoveryEvidence', 'supersession']);
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
  } else if (!replacement && (![1, 2].includes(record.schemaVersion) || !['intent', 'success', 'submitted'].includes(record.phase) ||
      name !== `legend-release-operation-${record.phase}-${record.operationId}` ||
      !/^[a-f0-9]{64}$/.test(record.operationId) ||
      !/^[a-f0-9]{64}$/.test(record.packageDigest) ||
      !/^[a-f0-9]{40}$/.test(record.applicationRevision) ||
      !/^[a-f0-9]{40}$/.test(record.baseline) ||
      !/^[a-z][a-z0-9-]{0,63}$/.test(record.target) ||
      ['producingRun', 'producingAttempt', 'packageProducerRun'].some(key =>
        !Number.isSafeInteger(record[key]) || record[key] < 1)))
    throw new Error('Invalid release operation identity');
  if (!preflight && !retained && !completion && !observation && !locator && !recovery && !plan && !child && !admission && !replacement) {
    if (record.supersession !== undefined) supersessionRecord(record);
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
  return {filename: plan ? 'release-transaction.json' : 'operation.json'};
}

async function publish(client, name, record, {sleep = ms => new Promise(resolve => setTimeout(resolve, ms)), claim = false} = {}) {
  const {filename} = validateRecord(name, record);
  const {ArtifactNotFoundError} = require('@actions/artifact');
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'legend-operation-'));
  try {
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
    let created = false;
    if (artifactId === null) {
      try {
        const result = await client.uploadArtifact(name, [source], directory, {retentionDays: 90});
        if (!Number.isSafeInteger(result.id) || result.id <= 0)
          throw new Error('Artifact upload did not return durable identity');
        artifactId = result.id;
        created = true;
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
      if (created && claim) {
        let confirmed = null;
        for (let visibility = 0; visibility < 3; visibility++) {
          if (visibility) await sleep(visibility * 1000);
          confirmed = await existing();
          if (confirmed !== null) break;
        }
        if (confirmed !== artifactId) throw new Error('Created artifact identity not uniquely confirmed');
      }
      return claim ? {artifactId, created} : {artifactId};
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
    const {mode, name, record} = JSON.parse(input);
    if (mode === 'validate') {
      try { validateRecord(name, record); } catch {
        process.stderr.write('LEGEND_RECORD_CONTRACT_INVALID\n');
        process.exitCode = 2;
        return;
      }
      process.stdout.write('LEGEND_RECORD_CONTRACT_VALID\n');
      return;
    }
    if (mode !== undefined && mode !== 'claim') throw new Error('Unknown release operation transport mode');
    const {DefaultArtifactClient} = require('@actions/artifact');
    const result = await publish(new DefaultArtifactClient(), name, record, {claim: mode === 'claim'});
    process.stdout.write(`LEGEND_OPERATION_RESULT=${JSON.stringify(result)}\n`);
  })().catch(() => {
    // SDK errors can contain signed URLs. Never forward those to release logs.
    process.stderr.write('Durable release operation publication/readback failed; no deployment write authorized.\n');
    process.exitCode = 1;
  });
}
module.exports = {publish, validateRecord};
