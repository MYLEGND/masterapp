'use strict';
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const {publish, validateRecord} = require('./transport.cjs');
const {execFileSync} = require('node:child_process');
const crypto = require('node:crypto');

test('retained CRM schema proof publishes and reads back exact immutable bytes', async () => {
  const value={schemaVersion:1,phase:'retained-schema-read-only',originalRun:41131223973,
    applicationRevision:'3243221ae661b53d60f78cc2dab108a93bad7632',producingRun:20,producingAttempt:1,
    observer:{runId:20,runAttempt:1,jobId:30,toolRevision:'b'.repeat(40),promotionRunId:19},
    originalSuccessArtifact:13017640415,historyIdentity:'a'.repeat(64),sqlExecutionAuthorized:false,
    observation:{databaseIdentity:'3a055450807aeaa2c6465702b983dc99968329389d7c0ae480c3c72cc3a9ff3b',
      schemaIdentity:'0e4674714d917c5f2dbfa3cd42f2de954e7b3f310c8322c08ec0f427d3615451',baselineIdentity:'c'.repeat(64),
      observedUtc:'2026-10-30T23:00:00Z',pendingCount:0,ready:true,mutationActivity:'settled'},
    recovery:{databaseIdentity:'3a055450807aeaa2c6465702b983dc99968329389d7c0ae480c3c72cc3a9ff3b',
      observedUtc:'2026-10-30T23:00:00Z',restorePointUtc:'2026-10-30T22:45:00Z',earliestRestoreUtc:'2026-10-28T00:00:00Z',
      retentionDays:7,restoreWindowVerified:true,restoreExecuted:false,authorizesMutation:false}};
  const canonical=v=>Array.isArray(v)?v.map(canonical):v&&typeof v==='object'?Object.fromEntries(Object.keys(v).sort().map(k=>[k,canonical(v[k])])):v;
  const name='legend-retained-schema-proof-'+crypto.createHash('sha256').update(JSON.stringify(canonical(value))).digest('hex');
  assert.deepEqual(validateRecord(name,value),{filename:'operation.json'});
  const forward=structuredClone(value);forward.schemaVersion=2;forward.originalRun=200;forward.applicationRevision='d'.repeat(40);forward.originalSuccessArtifact=201;
  forward.observation.schemaIdentity='e'.repeat(64);
  forward.schemaReference={sourceRevision:forward.applicationRevision,runId:200,runAttempt:1,artifactId:201,artifactDigest:'sha256:'+'f'.repeat(64),artifactName:'legend-release-child-success-'+'1'.repeat(64)};
  const forwardName=v=>'legend-retained-schema-proof-'+crypto.createHash('sha256').update(JSON.stringify(canonical(v))).digest('hex');
  assert.deepEqual(validateRecord(forwardName(forward),forward),{filename:'operation.json'});
  for(const mutate of [x=>delete x.schemaReference,x=>x.schemaReference.runId=202,x=>x.schemaReference.artifactId=202,
      x=>x.schemaReference.sourceRevision='a'.repeat(40),x=>x.sqlExecutionAuthorized=true,x=>x.recovery.restoreExecuted=true,
      x=>x.observation.pendingCount=1,x=>x.schemaVersion=3]) {
    const bad=structuredClone(forward);mutate(bad);assert.throws(()=>validateRecord(forwardName(bad),bad));
  }
  let stored;const calls=[];
  const client={async listArtifacts(){calls.push('list');return {artifacts:[]};},
    async uploadArtifact(n,files){calls.push('upload');assert.equal(n,name);stored=await fs.readFile(files[0]);return {id:42};},
    async downloadArtifact(id,options){calls.push('readback');assert.equal(id,42);await fs.mkdir(options.path);await fs.writeFile(path.join(options.path,'operation.json'),stored);}};
  assert.deepEqual(await publish(client,name,value),{artifactId:42});
  assert.deepEqual(calls,['list','upload','readback']);assert.deepEqual(JSON.parse(stored),value);
  for(const change of [{sqlExecutionAuthorized:true},{originalRun:1},{producingAttempt:2},{unexpected:'data'},
    {observation:{...value.observation,pendingCount:1}},{recovery:{...value.recovery,restoreExecuted:true}}]){
    assert.throws(()=>validateRecord(name,{...structuredClone(value),...change}));
  }
  client.downloadArtifact=async(id,options)=>{await fs.mkdir(options.path);await fs.writeFile(path.join(options.path,'operation.json'),'changed');};
  await assert.rejects(publish(client,name,value),/readback mismatch/);
});

// Exercise the real Python phase serializers without invoking live authority or providers.
function pythonSupersessionRecords() {
  return JSON.parse(execFileSync('python3', ['-c', String.raw`
import ast, hashlib, importlib.util, json, pathlib
root=pathlib.Path.cwd().parent
tree=ast.parse((root/'validation-resume.py').read_text())
function=next(n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name=='fast_track_protect_supersession')
records=[ast.literal_eval(n.value) for n in ast.walk(function) if isinstance(n,ast.Assign) and any(isinstance(t,ast.Name) and t.id=='expected' for t in n.targets)]
spec=importlib.util.spec_from_file_location('journal',root/'release-operation-evidence.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
result=[]
for auth in records:
    journal=object.__new__(module.OperationJournal)
    journal.identity=dict(target='protect',applicationRevision='c'*40,packageDigest='b'*64)
    journal.operation_id=hashlib.sha256(json.dumps(journal.identity,sort_keys=True,separators=(',',':')).encode()).hexdigest()
    journal.baseline=auth['originalRevision'];journal.producer=10;journal.run=10;journal.attempt=1
    journal.authorization=None;journal.supersession=dict(auth,candidateRevision='c'*40)
    intent=journal.intent_record(['old-provider-id'],submission_kind='azure_onedeploy_windows_intent_v1')
    result.append([intent,journal.submitted_record(intent,'new-provider-id'),journal.success_record(intent,['new-provider-id'])])
print(json.dumps(result))
`], {cwd: __dirname, encoding: 'utf8', env: {...process.env, PYTHONDONTWRITEBYTECODE: '1'}}));
}

test('both canonical Python supersession generations survive all phase serializers and immutable readback', async () => {
  const generations = pythonSupersessionRecords();
  assert.equal(generations.length, 2);
  const pins = ['aa65d799221ecbafd6ec316a6bce8e7049ee5ac7202ce00bc879e068a1940737', '123a6d98e874738eb4b630550e0de706a0a053a7ed0ec128cc79ba7a2f34dc17'];
  for (const [index, phases] of generations.entries()) {
    for (const value of phases) {
      const fixed = Object.fromEntries(Object.keys(value.supersession).filter(k => k !== 'candidateRevision').sort().map(k => [k,value.supersession[k]]));
      assert.equal(crypto.createHash('sha256').update(JSON.stringify(fixed)).digest('hex'), pins[index]);
      const name = `legend-release-operation-${value.phase}-${value.operationId}`;
      assert.doesNotThrow(() => validateRecord(name,value));
      let stored; const calls=[];
      const client={
        async listArtifacts(){calls.push('list');return {artifacts:[]};},
        async uploadArtifact(n,files){calls.push('upload');assert.equal(n,name);stored=await fs.readFile(files[0]);return {id:42};},
        async downloadArtifact(id,options){calls.push('readback');assert.equal(id,42);await fs.mkdir(options.path);await fs.writeFile(path.join(options.path,'operation.json'),stored);}
      };
      assert.deepEqual(await publish(client,name,value),{artifactId:42});
      assert.deepEqual(calls,['list','upload','readback']);
      assert.deepEqual(JSON.parse(stored),value);
    }
  }
});

test('supersession alteration, crossing, ambiguity and mixed contexts fail before artifact access', async () => {
  const generations=pythonSupersessionRecords();
  for (const [index, phases] of generations.entries()) for (const value of phases) {
    const variants=[];
    for (const key of Object.keys(value.supersession)) {
      const altered=structuredClone(value);altered.supersession[key]=typeof altered.supersession[key]==='number'?altered.supersession[key]+1:typeof altered.supersession[key]==='boolean'?false:'changed';variants.push(altered);
      const missing=structuredClone(value);delete missing.supersession[key];variants.push(missing);
      const crossed=structuredClone(value);crossed.supersession[key]=generations[1-index][0].supersession[key];
      if(crossed.supersession[key]!==value.supersession[key]) variants.push(crossed);
    }
    for (const change of [{target:'portal'},{schemaVersion:2},{baseline:'e'.repeat(40)},{applicationRevision:'e'.repeat(40)},{operationId:'f'.repeat(64)},{recoveryEvidence:{}},{completionEvidence:{}},{componentSource:{}},{candidateRevision:'c'.repeat(40)}]) variants.push({...structuredClone(value),...change});
    variants.push({...structuredClone(value),supersession:{...value.supersession,extra:'not-allowed'}});
    variants.push({...structuredClone(value),supersession:null});
    variants.push({...structuredClone(value),supersession:[]});
    variants.push({...structuredClone(value),unexpected:'not-allowed'});
    variants.push({...structuredClone(value),baselineDeploymentIds:['x'.repeat(33000)]});
    for(const bad of variants){
      let calls=0;const client=new Proxy({}, {get(){calls++;throw new Error('artifact access forbidden');}});
      await assert.rejects(publish(client,`legend-release-operation-${bad.phase}-${bad.operationId}`,bad),/Invalid/);
      assert.equal(calls,0);
    }
  }
});
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

test('actual Python journal records cross the strict artifact transport for Linux and Windows', async () => {
  const {execFileSync} = require('node:child_process');
  const script = `
import importlib.util,json,sys
spec=importlib.util.spec_from_file_location('journal',sys.argv[1])
owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
authority=type('Authority',(),{'RELEASE_TARGETS':{'protect':{}},'ReleaseOperationHistoryUnproven':RuntimeError})()
authority.release_operation_history=lambda *args,**kwargs:None
records=[]
def publisher(name,record):
    records.append({'name':name,'record':record})
    return {'artifactId':1}
env=dict(GITHUB_RUN_ID='10',GITHUB_RUN_ATTEMPT='1',PACKAGE_PRODUCER_RUN='8',GITHUB_REPOSITORY='owner/repo',GITHUB_TOKEN='synthetic')
for kind in ('azure_onedeploy_deployment_tag_v1','azure_onedeploy_windows_intent_v1'):
    journal=owner.OperationJournal(target='protect',application_revision='a'*40,package_digest='b'*64,baseline='c'*40,authority=authority,publisher=publisher,environment=env)
    journal.before_submit(['old'],submission_kind=kind)
    if kind=='azure_onedeploy_windows_intent_v1':journal.record_submission_observation('fresh')
    journal.record_success(['fresh'])
print(json.dumps(records))
`;
  const records = JSON.parse(execFileSync('python3', ['-c', script,
    path.join(__dirname, '..', 'release-operation-evidence.py')], {encoding: 'utf8'}));
  assert.equal(records.length, 5);
  for (const entry of records) {
    const client = durableClient();
    assert.deepEqual(await publish(client, entry.name, entry.record), {artifactId: 45});
    assert.deepEqual(JSON.parse(client.state.content), entry.record);
    assert.equal(client.state.uploads, 1);
    assert.equal(client.state.downloads, 1);
  }
});

test('deployment contract and provider observation reject malformed or widened identities before upload', async () => {
  const windows = {...record, submissionContract: {kind: 'azure_onedeploy_windows_intent_v1', value: record.operationId},
    baselineDeploymentIds: ['old']};
  const submitted = {...windows, phase: 'submitted', deploymentId: 'fresh'};
  const bad = [
    {...windows, submissionContract: {kind: 'wrong', value: record.operationId}},
    {...windows, submissionContract: {kind: 'azure_onedeploy_windows_intent_v1', value: 'b'.repeat(64)}},
    {...windows, submissionContract: {...windows.submissionContract, token: 'forbidden'}},
    {...windows, submissionContract: null},
    {...windows, providerRequestCorrelation: {kind: 'azure_onedeploy_deployment_tag_v1', value: record.operationId}},
    {...windows, deploymentId: 'fresh'},
    {...submitted, deploymentId: 'old'},
    {...submitted, deploymentId: 'https://host/id?secret=forbidden'},
    {...submitted, deploymentIds: ['fresh']},
    {...submitted, baselineDeploymentIds: undefined},
    {...submitted, submissionContract: undefined},
    {...submitted, token: 'forbidden'},
  ];
  for (const value of bad) {
    const client = durableClient();
    await assert.rejects(publish(client, `legend-release-operation-${value.phase}-${value.operationId}`, value), /Invalid|Conflicting|incorrect phase/);
    assert.equal(client.state.uploads, 0);
  }
});

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


test('pinned SDK delayed artifact visibility recovers without a second upload', async () => {
  const {ArtifactNotFoundError} = require('@actions/artifact');
  const client = durableClient();
  const download = client.downloadArtifact;
  let attempts = 0;
  client.downloadArtifact = async (...args) => {
    if (++attempts === 1) throw new ArtifactNotFoundError();
    return download(...args);
  };
  assert.deepEqual(await publish(client, name, record, {sleep: async () => {}}), {artifactId: 45});
  assert.equal(attempts, 2);
  assert.equal(client.state.uploads, 1);
});

test('completion summary binds all original targets to actual observer and immutable bytes', async () => {
  const crypto = require('node:crypto');
  const value = {schemaVersion: 1, phase: 'success', originalRun: 1, applicationRevision: 'a'.repeat(40),
    producingRun: 2, producingAttempt: 1,
    observer: {runId: 2, runAttempt: 1, jobId: 20, toolRevision: 'b'.repeat(40), promotionRunId: 3},
    targets: {website: {operationId: 'c'.repeat(64), packageDigest: 'd'.repeat(64), deploymentIds: ['new'], intentArtifactId: 10, packageArtifactId: 11}}};
  const summaryName = 'legend-release-completion-' + crypto.createHash('sha256').update(JSON.stringify({applicationRevision: value.applicationRevision, originalRun: 1})).digest('hex');
  let stored;
  const client = {
    async listArtifacts() { return {artifacts: []}; },
    async uploadArtifact(n, files) { assert.equal(n, summaryName); stored = await fs.readFile(files[0]); return {id: 42}; },
    async downloadArtifact(id, options) { assert.equal(id, 42); await fs.mkdir(options.path); await fs.writeFile(path.join(options.path, 'operation.json'), stored); }
  };
  assert.deepEqual(await publish(client, summaryName, value), {artifactId: 42});
  for (const mutate of [x => x.phase = 'intent', x => x.observer.runId = 9, x => x.targets = {},
      x => x.targets.website.packageDigest = 'wrong', x => x.targets.website.deploymentIds.push('other'),
      x => x.targets.unknown = x.targets.website, x => x.unrecognized = 'field']) {
    const invalid = JSON.parse(JSON.stringify(value)); mutate(invalid);
    await assert.rejects(publish({}, summaryName, invalid));
  }
  await assert.rejects(publish({}, summaryName.slice(0,-1)+'0', value));
});

test('completion success transport preserves original operation and names the real observer', async () => {
  const value = {...record, phase: 'success', producingRun: 22, producingAttempt: 2,
    baselineDeploymentIds: ['old'], deploymentIds: ['new'],
    submissionContract: {kind: 'azure_onedeploy_windows_intent_v1', value: record.operationId},
    completionEvidence: {originalRun: record.packageProducerRun, originalRunAttempt: 3, originalPackageJobId: 80, originalPackageAttempt: 1, originalJobId: 80, intentArtifactId: 81, packageArtifactId: 82,
      intentArtifactDigest: 'sha256:'+'e'.repeat(64), packageArtifactDigest: 'sha256:'+'f'.repeat(64),
      observer: {runId: 22, runAttempt: 2, jobId: 90, toolRevision: 'a'.repeat(40), promotionRunId: 21},
      provider: {deploymentId: 'new', receivedTime: '2026-10-21T13:01:00Z', status: 4, runtimeRevision: record.applicationRevision}}};
  const successName = 'legend-release-operation-success-' + record.operationId;
  let stored;
  const client = {
    async listArtifacts() { return {artifacts: []}; },
    async uploadArtifact(n, files) { assert.equal(n, successName); stored = await fs.readFile(files[0]); return {id: 42}; },
    async downloadArtifact(id, options) { await fs.mkdir(options.path); await fs.writeFile(path.join(options.path, 'operation.json'), stored); }
  };
  assert.deepEqual(await publish(client, successName, value), {artifactId: 42});
  for (const mutate of [x => x.completionEvidence.observer.runId = 8,
      x => x.completionEvidence.originalRun = 99, x => delete x.completionEvidence.originalRunAttempt,
      x => x.completionEvidence.originalPackageJobId = 0, x => x.completionEvidence.originalPackageAttempt = true, x => x.completionEvidence.provider.status = 3,
      x => x.completionEvidence.provider.runtimeRevision = 'f'.repeat(40),
      x => x.completionEvidence.provider.extra = 'unapproved', x => x.completionEvidence.intentArtifactDigest = 'wrong',
      x => x.baselineDeploymentIds.push('new')]) {
    const invalid = JSON.parse(JSON.stringify(value)); mutate(invalid);
    await assert.rejects(publish({}, successName, invalid));
  }
  await assert.rejects(publish({}, name, {...value, phase: 'intent'}));
});

// Exercise the actual Python producers through the actual JS serialization gate.
// Only external authority/artifact I/O is injected; producer payloads are not hand-copied.
function retainedProducerPayloads(corrected = false, preview = false) {
  const {spawnSync} = require('node:child_process');
  const script = String.raw`
import ast,importlib.util,json,sys
from pathlib import Path
from types import SimpleNamespace
root=Path(sys.argv[1])
corrected=sys.argv[2]=='true'
preview=sys.argv[3]=='true'
def load(name,path):
 spec=importlib.util.spec_from_file_location(name,path);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m
owner=load('journal',root/'release-operation-evidence.py')
authority=load('authority',root/'validation-resume.py')
observer=dict(runId=20,runAttempt=1,jobId=30,promotionRunId=19,toolRevision='b'*40)
original=dict(run=dict(id=10,run_attempt=5,head_sha='a'*40))
env=dict(GITHUB_RUN_ID='20',GITHUB_RUN_ATTEMPT='1',PACKAGE_PRODUCER_RUN='10',GITHUB_REPOSITORY='owner/repo',GITHUB_TOKEN='test-only')
fake=SimpleNamespace(RELEASE_TARGETS=authority.RELEASE_TARGETS,ReleaseOperationHistoryUnproven=RuntimeError,release_operation_history=lambda *a,**k:None,recovery_admission_proof=__import__('contextlib').nullcontext)
rows=[];records={};previews=[]
for n,target in enumerate(['portal','client','protect','parfait','website']):
 material=dict(packageArtifact=dict(id=40+n,digest='sha256:'+'c'*64))
 if corrected and target=='protect':
  module=ast.parse((root/'validation-resume.py').read_text())
  authfn=next(n for n in module.body if isinstance(n,ast.FunctionDef) and n.name=='fast_track_protect_supersession')
  authassign=next(n for n in authfn.body if isinstance(n,ast.Assign) and any(isinstance(t,ast.Name) and t.id=='expected' for t in n.targets))
  authorization=dict(ast.literal_eval(authassign.value),candidateRevision='a'*40)
  baselinefn=next(n for n in module.body if isinstance(n,ast.FunctionDef) and n.name=='protect_recovery_baseline_proof')
  returned=next(n for n in baselinefn.body if isinstance(n,ast.Return))
  context=dict(authorization=authorization,PROTECT_BASELINE_REVISION='e'*40,anchor=dict(id=50),
   rollback=dict(id=51,name='fast-track-package-'+'e'*40+'-protect',digest='sha256:'+'f'*64),
   receipt=dict(id=52,digest='sha256:'+'1'*64),expected=dict(packageDigest='2'*64),run=original['run'],package=material['packageArtifact'])
  material['protectBaselineCorrection']=eval(compile(ast.Expression(returned.value),'actual-baseline-proof','eval'),context)
 proof=authority.fast_track_recovery_record_proof(original,material,observer)
 journal=owner.RecoveryJournal(target=target,application_revision='a'*40,package_digest='d'*64,baseline='e'*40,
  recovery_evidence=proof,before_new_intent=lambda ids:None,authority=fake,environment=env,
  publisher=lambda name,record:rows.append(dict(name=name,record=record)))
 journal.validate_contract(['old'],validator=lambda name,record:previews.append(dict(name=name,record=record)))
 assert journal.intent is None and journal._new_intent_readback is False and not journal.submission_observation
 journal.before_submit(['old'],submission_kind='azure_onedeploy_windows_intent_v1')
 journal.record_submission_observation('new-'+target)
 journal.record_success(['new-'+target])
 linux=owner.RecoveryJournal(target=target,application_revision='a'*40,package_digest='d'*64,baseline='e'*40,
  recovery_evidence=proof,before_new_intent=lambda ids:None,authority=fake,environment=env,
  publisher=lambda name,record:rows.append(dict(name=name,record=record)))
 linux.before_submit(['old'])
 linux.record_success(['new-'+target])
 records[target]=dict(operationId=journal.operation_id,packageDigest='d'*64,packageArtifactId=40+n,
                     packageArtifactDigest='sha256:'+'c'*64,deploymentIds=['new-'+target])
consumer=load('consumer',root/'deploy-approved-app.py')
summary=consumer.recovery_summary_record(10,original,'a'*40,observer,records)
rows.append(dict(name=authority.fast_track_recovery_summary_name(10,'a'*40),record=summary))
print(json.dumps(previews if preview else rows))
`;
  const result = spawnSync('python3', ['-c', script, path.resolve(__dirname, '..'), String(corrected), String(preview)], {encoding:'utf8', timeout:10000});
  assert.equal(result.status, 0, result.stderr);
  return JSON.parse(result.stdout);
}
async function retainedRoundTrip(item) {
  const calls=[];let bytes;
  const client={async listArtifacts(){calls.push('list');return {artifacts:[]}},
    async uploadArtifact(name, files){calls.push('upload');assert.equal(name,item.name);bytes=await fs.readFile(files[0]);return {id:99}},
    async downloadArtifact(id,options){calls.push('readback');assert.equal(id,99);await fs.mkdir(options.path);await fs.writeFile(path.join(options.path,'operation.json'),bytes)}};
  assert.deepEqual(await publish(client,item.name,item.record),{artifactId:99});
  assert.deepEqual(JSON.parse(bytes),item.record);assert.deepEqual(calls,['list','upload','readback']);
}
test('actual RecoveryJournal intent submitted success and five-target consumer summary round trip',async()=>{
  const rows=retainedProducerPayloads();assert.equal(rows.length,26);
  for(const row of rows)await retainedRoundTrip(row);
});
test('retained operation identity proof and phase mutations reject before artifact access',async()=>{
  const rows=retainedProducerPayloads();
  const mutations=[r=>r.recoveryEvidence.originalRun++,r=>r.recoveryEvidence.runId++,r=>r.recoveryEvidence.runAttempt++,
    r=>r.recoveryEvidence.applicationRevision='f'.repeat(40),r=>r.recoveryEvidence.packageArtifactDigest='bad',
    r=>r.recoveryEvidence.packageArtifactId=0,r=>r.recoveryEvidence.jobId=true,r=>r.recoveryEvidence.toolRevision='bad',
    r=>r.recoveryEvidence.extra='no',r=>delete r.recoveryEvidence.originalAttempt,r=>r.operationId='f'.repeat(64),
    r=>r.target='unknown',r=>delete r.baselineDeploymentIds,r=>r.schemaVersion=2,
    r=>r.baselineDeploymentIds=['new-'+r.target],r=>r.extra='no'];
  for(const row of rows.slice(0,3)){
    for(const mutate of mutations){const bad=structuredClone(row);mutate(bad.record);
      // An unchanged baseline for intent is valid; only success/submitted binds the new provider ID.
      if(bad.record.phase==='intent'&&bad.record.baselineDeploymentIds?.[0]==='new-'+bad.record.target)continue;
      await assert.rejects(publish({},bad.name,bad.record),/Invalid/);
    }
    const bad=structuredClone(row);
    if(bad.record.phase==='intent')bad.record.deploymentIds=['new'];
    else if(bad.record.phase==='submitted')bad.record.deploymentIds=['new'];
    else delete bad.record.deploymentIds;
    await assert.rejects(publish({},bad.name,bad.record),/Invalid/);
  }
});
test('retained summary all target identities and observer bindings reject before artifact access',async()=>{
  const row=retainedProducerPayloads().at(-1);
  const mutations=[r=>r.originalRun++,r=>r.originalAttempt=0,r=>r.producingRun++,r=>r.observer.runAttempt++,
    r=>r.observer.extra='no',r=>r.applicationRevision='f'.repeat(40),r=>r.targets={},r=>r.targets.unknown=r.targets.portal,
    r=>r.extra='no',r=>r.phase='intent'];
  for(const key of Object.keys(row.record.targets))for(const field of ['operationId','packageDigest','packageArtifactId','packageArtifactDigest','deploymentIds'])
    mutations.push(r=>{r.targets[key][field]=field==='packageArtifactId'?0:field==='deploymentIds'?[]:'bad'});
  for(const mutate of mutations){const bad=structuredClone(row);mutate(bad.record);await assert.rejects(publish({},bad.name,bad.record),/Invalid/);}
  await assert.rejects(publish({},row.name.replace(/[a-f0-9]{64}$/,'f'.repeat(64)),row.record),/Invalid/);
});

function preflightProducerPayloads() {
  const {spawnSync}=require('node:child_process');
  const script=String.raw`
import importlib.util,json,hashlib,sys
from pathlib import Path
p=Path(sys.argv[1])/'deploy-approved-app.py';spec=importlib.util.spec_from_file_location('deploy',p);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
observer=dict(runId=20,runAttempt=1,jobId=30,promotionRunId=19,toolRevision='b'*40)
proof=dict(originalRun=10,originalAttempt=5,applicationRevision='a'*40,observer=observer,targets={})
for target in ['website','portal','client','protect','parfait']:
 identity=dict(target=target,applicationRevision='a'*40,packageDigest='d'*64)
 proof['targets'][target]=dict(operationId=hashlib.sha256(json.dumps(identity,sort_keys=True,separators=(',',':')).encode()).hexdigest(),packageDigest='d'*64,packageArtifactId=40+len(proof['targets']),packageArtifactDigest='sha256:'+'e'*64)
rows=[]
for reason in sorted(m.RECOVERY_PREFLIGHT_REASONS):
 observation=dict(historyReadComplete=True,historyCount=1,activeCount=0,runtimeRevision='c'*40)
 if reason in ['provider-read-timeout','provider-read-unavailable','preflight-authority-unproven','phase-deadline']:observation=None
 elif reason=='history-empty':observation.update(historyCount=0)
 elif reason=='provider-active':observation.update(activeCount=1)
 elif reason=='runtime-unavailable':observation.update(runtimeRevision=None)
 elif reason=='runtime-candidate-unproven':observation.update(runtimeRevision='a'*40)
 name,record=m.recovery_preflight_nonentry_record(proof,'website',m.RecoveryPreflightFailure(reason,observation))
 rows.append(dict(name=name,record=record))
print(json.dumps(rows))
`;
  const result=spawnSync('python3',['-c',script,path.resolve(__dirname,'..')],{encoding:'utf8',timeout:10000});
  assert.equal(result.status,0,result.stderr);return JSON.parse(result.stdout);
}
test('actual Python terminal preflight producer all reasons and five materials round trip',async()=>{
  const rows=preflightProducerPayloads();assert.equal(rows.length,13);
  for(const item of rows)await retainedRoundTrip(item);
});
test('terminal preflight cannot authorize mutation or carry unknown producer/material/observation',async()=>{
  const item=preflightProducerPayloads()[0];
  const mutations=[r=>r.authorizesMutation=true,r=>r.writePhaseEntered=true,r=>r.phase='success',r=>r.stage='worker',r=>r.reason='arbitrary log text',r=>r.extra='secret',r=>r.observer.runId++,r=>r.producingRun++,r=>r.originalRun++,r=>r.originalAttempt=false,r=>r.failedTarget='missing',r=>r.failedTarget=['website'],r=>r.failedTarget={},r=>r.failedTarget=1,r=>r.targets.website.packageDigest='a'.repeat(64),r=>r.targets.website.extra='no',r=>r.observation.extra='no',r=>r.observation.runtimeRevision='https://secret',r=>r.observation.historyCount=-1];
  for(const mutate of mutations){const bad=structuredClone(item);mutate(bad.record);await assert.rejects(publish({},bad.name,bad.record),/Invalid|Inconsistent/);}
});

test('real five-target preflight failure through Node readback then full Python attestor',async()=>{
  const {spawnSync}=require('node:child_process');
  const producer=String.raw`import importlib.util,json,sys
from pathlib import Path
p=Path(sys.argv[1])/'test-deploy-approved-app.py';s=importlib.util.spec_from_file_location('fixture',p);m=importlib.util.module_from_spec(s);s.loader.exec_module(m)
print(json.dumps(m.preflight_five_target_payload()))`;
  const made=spawnSync('python3',['-c',producer,path.resolve(__dirname,'..')],{encoding:'utf8',timeout:10000});
  assert.equal(made.status,0,made.stderr);const item=JSON.parse(made.stdout);let bytes;
  const client={async listArtifacts(){return {artifacts:[]}},async uploadArtifact(name,files){assert.equal(name,item.name);bytes=await fs.readFile(files[0]);return{id:99}},
    async downloadArtifact(id,options){await fs.mkdir(options.path);await fs.writeFile(path.join(options.path,'operation.json'),bytes)}};
  await publish(client,item.name,item.record);
  const consumer=String.raw`import importlib.util,json,sys
from pathlib import Path
p=Path(sys.argv[1])/'test-validation-resume.py';s=importlib.util.spec_from_file_location('fixture',p);m=importlib.util.module_from_spec(s);s.loader.exec_module(m)
f=m.RecoveryPreflightTerminalConsumerTests();f.setUp();f.record=json.load(sys.stdin)
assert f.invoke(), 'Full attestor rejected literal Node readback'
print('full-attestor-accepted')`;
  const accepted=spawnSync('python3',['-c',consumer,path.resolve(__dirname,'..')],{input:bytes,encoding:'utf8',timeout:10000});
  assert.equal(accepted.status,0,accepted.stderr);assert.equal(accepted.stdout.trim(),'full-attestor-accepted');
});

function actualTargetHoldPayload() {
  const {spawnSync}=require('node:child_process');
  const script=String.raw`import importlib.util,json,sys
from pathlib import Path
p=Path(sys.argv[1])/'test-deploy-approved-app.py';s=importlib.util.spec_from_file_location('fixture',p);m=importlib.util.module_from_spec(s);s.loader.exec_module(m)
print(json.dumps(m.target_hold_five_target_payload()))`;
  const made=spawnSync('python3',['-c',script,path.resolve(__dirname,'..')],{encoding:'utf8',timeout:10000});
  assert.equal(made.status,0,made.stderr);return JSON.parse(made.stdout);
}
test('target-only preintent producer passes actual transport and full sibling-aware consumer',async()=>{
  const {spawnSync}=require('node:child_process');const item=actualTargetHoldPayload();let bytes;
  const client={async listArtifacts(){return{artifacts:[]}},async uploadArtifact(name,files){assert.equal(name,item.name);bytes=await fs.readFile(files[0]);return{id:99}},async downloadArtifact(id,options){await fs.mkdir(options.path);await fs.writeFile(path.join(options.path,'operation.json'),bytes)}};
  await publish(client,item.name,item.record);
  const script=String.raw`import importlib.util,json,sys
from pathlib import Path
p=Path(sys.argv[1])/'test-validation-resume.py';s=importlib.util.spec_from_file_location('fixture',p);m=importlib.util.module_from_spec(s);s.loader.exec_module(m)
f=m.RecoveryTargetHoldTests();f.setUp();f.record=json.load(sys.stdin)
assert f.invoke(), 'Full target consumer rejected actual transport readback'
print('target-hold-consumer-accepted')`;
  const accepted=spawnSync('python3',['-c',script,path.resolve(__dirname,'..')],{input:bytes,encoding:'utf8',timeout:10000});assert.equal(accepted.status,0,accepted.stderr);assert.equal(accepted.stdout.trim(),'target-hold-consumer-accepted');
});
test('target hold never claims global nonentry and rejects substituted scope before artifact I/O',async()=>{
  const item=actualTargetHoldPayload();
  for(const mutate of [r=>r.writePhaseEntered=false,r=>r.targetWritePhaseEntered=true,r=>delete r.targetWritePhaseEntered,r=>r.stage='all-target-preflight',r=>r.phase='preflight-nonentry',r=>r.failedTarget='portal',r=>r.failedTarget=['website'],r=>r.targets.website.packageArtifactDigest='bad',r=>r.observer.toolRevision='unknown',r=>r.producingAttempt++,r=>r.extra='unknown']){
    const bad=structuredClone(item);mutate(bad.record);await assert.rejects(publish({},bad.name,bad.record),/Invalid|Inconsistent/);
  }
});


test('replacement recovery records use canonical artifact transport and claim disposition', async () => {
  const admission = {
    schemaVersion: 1, phase: 'replacement-admission', admissionId: '1'.repeat(64),
    target: 'protect', originalOperationId: '2'.repeat(64),
    applicationRevision: 'a'.repeat(40), packageDigest: '3'.repeat(64),
    baselineRevision: 'b'.repeat(40), expectedHead: 'a'.repeat(40),
    controlRevision: 'c'.repeat(40), rollbackPackageDigest: '4'.repeat(64),
    terminalNonresumableEvidenceDigest: '5'.repeat(64),
    serializationFenceEvidenceDigest: '6'.repeat(64), noSqlEvidenceDigest: '7'.repeat(64),
    rollbackEvidenceDigest: '8'.repeat(64), independentReviewDigest: '9'.repeat(64),
    contentMode: 'deterministic-full-replacement', serializationKey: 'legend-approved-release',
    observedAtUtc: '2026-10-25T18:29:00Z', expiresAtUtc: '2026-10-25T18:39:00Z',
    authorizesOneReplacement: true
  };
  const operationId = 'd'.repeat(64);
  const intent = {
    schemaVersion: 1, phase: 'intent', operationId, target: 'protect',
    applicationRevision: admission.applicationRevision, packageDigest: admission.packageDigest,
    baseline: admission.baselineRevision, packageProducerRun: 20,
    producingRun: 30, producingAttempt: 1, baselineDeploymentIds: ['old'],
    replacementOfOperationId: admission.originalOperationId,
    recoveryAdmissionId: admission.admissionId, recoveryAdmission: admission,
    submissionContract: {kind: 'azure_onedeploy_windows_intent_v1', value: operationId}
  };
  const artifact = 'legend-replacement-operation-intent-' + operationId;
  const client = durableClient();
  assert.deepEqual(await publish(client, artifact, intent, {claim: true}),
    {artifactId: 45, created: true});
  assert.deepEqual(await publish(client, artifact, intent, {claim: true}),
    {artifactId: 45, created: false});
  assert.equal(client.state.uploads, 1);
  await assert.rejects(publish(client, artifact, {...intent, token: 'forbidden'}, {claim: true}), /Invalid/);
});

test('lost replacement claim acknowledgment never returns created ownership', async () => {
  const admission = {
    schemaVersion: 1, phase: 'replacement-admission', admissionId: '1'.repeat(64),
    target: 'protect', originalOperationId: '2'.repeat(64),
    applicationRevision: 'a'.repeat(40), packageDigest: '3'.repeat(64),
    baselineRevision: 'b'.repeat(40), expectedHead: 'a'.repeat(40),
    controlRevision: 'c'.repeat(40), rollbackPackageDigest: '4'.repeat(64),
    terminalNonresumableEvidenceDigest: '5'.repeat(64),
    serializationFenceEvidenceDigest: '6'.repeat(64), noSqlEvidenceDigest: '7'.repeat(64),
    rollbackEvidenceDigest: '8'.repeat(64), independentReviewDigest: '9'.repeat(64),
    contentMode: 'deterministic-full-replacement', serializationKey: 'legend-approved-release',
    observedAtUtc: '2026-10-25T18:29:00Z', expiresAtUtc: '2026-10-25T18:39:00Z',
    authorizesOneReplacement: true
  };
  const operationId = 'e'.repeat(64);
  const intent = {
    schemaVersion: 1, phase: 'intent', operationId, target: 'protect',
    applicationRevision: admission.applicationRevision, packageDigest: admission.packageDigest,
    baseline: admission.baselineRevision, packageProducerRun: 20,
    producingRun: 30, producingAttempt: 1, baselineDeploymentIds: [],
    replacementOfOperationId: admission.originalOperationId,
    recoveryAdmissionId: admission.admissionId, recoveryAdmission: admission,
    submissionContract: {kind: 'azure_onedeploy_windows_intent_v1', value: operationId}
  };
  const client = durableClient({lostAck: true});
  const artifact = 'legend-replacement-operation-intent-' + operationId;
  assert.deepEqual(await publish(client, artifact, intent, {claim: true}),
    {artifactId: 45, created: false});
  assert.equal(client.state.uploads, 1);
});

test('concurrent replacement claims expose one created owner and one existing observer', async () => {
  const admission = {
    schemaVersion: 1, phase: 'replacement-admission', admissionId: '1'.repeat(64),
    target: 'protect', originalOperationId: '2'.repeat(64),
    applicationRevision: 'a'.repeat(40), packageDigest: '3'.repeat(64),
    baselineRevision: 'b'.repeat(40), expectedHead: 'a'.repeat(40),
    controlRevision: 'c'.repeat(40), rollbackPackageDigest: '4'.repeat(64),
    terminalNonresumableEvidenceDigest: '5'.repeat(64),
    serializationFenceEvidenceDigest: '6'.repeat(64), noSqlEvidenceDigest: '7'.repeat(64),
    rollbackEvidenceDigest: '8'.repeat(64), independentReviewDigest: '9'.repeat(64),
    contentMode: 'deterministic-full-replacement', serializationKey: 'legend-approved-release',
    observedAtUtc: '2026-10-25T18:29:00Z', expiresAtUtc: '2026-10-25T18:39:00Z',
    authorizesOneReplacement: true
  };
  const operationId = 'f'.repeat(64);
  const intent = {
    schemaVersion: 1, phase: 'intent', operationId, target: 'protect',
    applicationRevision: admission.applicationRevision, packageDigest: admission.packageDigest,
    baseline: admission.baselineRevision, packageProducerRun: 20,
    producingRun: 30, producingAttempt: 1, baselineDeploymentIds: [],
    replacementOfOperationId: admission.originalOperationId,
    recoveryAdmissionId: admission.admissionId, recoveryAdmission: admission,
    submissionContract: {kind: 'azure_onedeploy_windows_intent_v1', value: operationId}
  };
  const artifact = 'legend-replacement-operation-intent-' + operationId;
  const client = durableClient();
  let accepted = 0, arrivals = 0, open;
  const both = new Promise(resolve => { open = resolve; });
  const original = client.uploadArtifact;
  client.uploadArtifact = async (...args) => {
    arrivals++;
    if (arrivals === 2) open();
    await both;
    if (accepted) throw new Error('409 immutable artifact already exists');
    accepted++;
    return original(...args);
  };
  const results = await Promise.all([
    publish(client, artifact, intent, {claim: true}),
    publish(client, artifact, intent, {claim: true})
  ]);
  assert.deepEqual(results.map(x => x.created).sort(), [false, true]);
  assert.equal(accepted, 1);
  assert.equal(client.state.uploads, 1);
});

// The correction's real Python return shape must cross the JS gate intact.
test('Protect baseline correction actual producer crosses every journal phase', async () => {
  for (const row of retainedProducerPayloads(true)) await retainedRoundTrip(row);
});
test('Protect correction rejects malformed fields and inconsistent bindings before I/O', async () => {
  const rows = retainedProducerPayloads(true).filter(x => x.record.target === 'protect');
  const mutations = [r => r.target = 'portal', r => r.baseline = '0'.repeat(40),
    r => r.recoveryEvidence.protectBaselineCorrection = null];
  const correction = rows[0].record.recoveryEvidence.protectBaselineCorrection;
  for (const key of Object.keys(correction)) mutations.push(r => {delete r.recoveryEvidence.protectBaselineCorrection[key]});
  for (const key of Object.keys(correction.authorization)) mutations.push(r => {delete r.recoveryEvidence.protectBaselineCorrection.authorization[key]});
  for (const [key,value] of Object.entries({extra:1,schemaVersion:2,originalRun:99,applicationRevision:'f'.repeat(40),candidateArtifactId:99,
      candidateArtifactDigest:'sha256:'+'0'.repeat(64),rollbackRun:0,rollbackArtifactId:true,rollbackArtifactName:'wrong',
      rollbackArtifactDigest:'bad',rollbackPackageDigest:'bad',baselineReceiptId:0,baselineReceiptDigest:'bad'}))
    mutations.push(r => r.recoveryEvidence.protectBaselineCorrection[key] = value);
  for (const [key,value] of Object.entries({extra:1,historicalOutcome:'SUCCESS',authorizeOneReplacement:false,target:'portal',
      authorizedBy:'other',authorizationId:'other',candidateRevision:'f'.repeat(40),originalRun:0,originalOperationId:'bad',
      originalRevision:'bad',expectedReleaseParent:'bad',rollbackArtifactId:0,rollbackArtifactDigest:'bad'}))
    mutations.push(r => r.recoveryEvidence.protectBaselineCorrection.authorization[key] = value);
  for (const row of rows) for (const mutate of mutations) {
    const bad = structuredClone(row); mutate(bad.record);
    await assert.rejects(publish({}, bad.name, bad.record), /Invalid/);
  }
});


test('all five journal contract previews use the publication gate without authorizing writes', async () => {
  for (const corrected of [false, true]) {
    const rows = retainedProducerPayloads(corrected, true);
    assert.equal(rows.length, 25);
    assert.deepEqual(new Set(rows.map(x => x.record.target)), new Set(['portal','client','protect','parfait','website']));
    for (const row of rows) {
      const before = JSON.stringify(row);
      const original = fs.mkdtemp;
      fs.mkdtemp = () => {throw new Error('validation must not create files')};
      try { validateRecord(row.name, row.record); } finally { fs.mkdtemp = original; }
      assert.equal(JSON.stringify(row), before);
      await retainedRoundTrip(row);
      const bad = structuredClone(row); bad.record.recoveryEvidence.futureUnrecognizedField = true;
      assert.throws(() => validateRecord(bad.name, bad.record), /Invalid/);
      await assert.rejects(publish({}, bad.name, bad.record), /Invalid/);
    }
  }
});

test('validation CLI is read-only and distinguishes contract rejection without SDK output', () => {
  const {spawnSync} = require('node:child_process');
  const item = retainedProducerPayloads(true, true).find(x => x.record.target === 'protect');
  const run = record => spawnSync(process.execPath, [path.join(__dirname, 'transport.cjs')], {
    input: JSON.stringify({mode:'validate', name:item.name, record}), encoding:'utf8',
    env: {PATH:process.env.PATH, NODE_PATH:process.env.NODE_PATH || '', TMPDIR:'/nonexistent/no-write-permitted'}, timeout:10000});
  const good = run(item.record);
  assert.equal(good.status, 0, good.stderr);
  assert.equal(good.stdout, 'LEGEND_RECORD_CONTRACT_VALID\n');
  assert.equal(good.stderr, '');
  const bad = run({...item.record, secret:'must-never-be-logged'});
  assert.equal(bad.status, 2);
  assert.equal(bad.stdout, '');
  assert.equal(bad.stderr, 'LEGEND_RECORD_CONTRACT_INVALID\n');
});

test('exact161 production journal roundtrip and fixed Python/JS generation parity', () => {
  const records = JSON.parse(execFileSync('python3', ['-c', String.raw`
import importlib.util,json,pathlib
root=pathlib.Path.cwd().parent
def module(name):
 s=importlib.util.spec_from_file_location(name,root/(name+'.py'));m=importlib.util.module_from_spec(s);s.loader.exec_module(m);return m
a=module('validation-resume');j=module('release-operation-evidence');p=a.PROTECT_161_REPLACEMENT
owner=object.__new__(j.RecoveryJournal)
owner.identity=dict(target=p['target'],applicationRevision=p['applicationRevision'],packageDigest=p['packageDigest'])
owner.operation_id=a.retained_operation_id(p['target'],p['applicationRevision'],p['packageDigest'],p)
owner.baseline=p['baselineRevision'];owner.producer=p['originalRun'];owner.run=999;owner.attempt=1;owner.authorization=None;owner.supersession=None
owner.recovery_evidence=a.fast_track_recovery_record_proof(dict(run=dict(id=p['originalRun'],head_sha=p['applicationRevision'],run_attempt=2)),dict(packageArtifact=dict(id=p['candidateArtifactId'],digest=p['candidateArtifactDigest']),unknownReplacement=p),dict(runId=999,runAttempt=1,jobId=998,promotionRunId=997,toolRevision='a'*40))
i=owner.intent_record(['old'],submission_kind='azure_onedeploy_windows_intent_v1')
print(json.dumps([owner.publication_record(i),owner.publication_record(owner.submitted_record(i,'new')),owner.publication_record(owner.success_record(i,['new']))]))
`], {encoding:'utf8',cwd:__dirname}));
  for (const record of records) {
    const name=`legend-release-operation-${record.phase}-${record.operationId}`;
    assert.doesNotThrow(()=>validateRecord(name,record));
    for(const key of Object.keys(record.recoveryEvidence.unknownReplacement)) {
      const changed=structuredClone(record);changed.recoveryEvidence.unknownReplacement[key]=null;
      assert.throws(()=>validateRecord(name,changed));
    }
    for(const mutate of [r=>delete r.recoveryEvidence.unknownReplacement,
      r=>r.recoveryEvidence.unknownReplacement.extra=true,r=>r.recoveryEvidence.unknownReplacement.schemaVersion=true,
      r=>r.target='website',r=>r.baseline='f'.repeat(40),r=>r.operationId=record.recoveryEvidence.unknownReplacement.originalOperationId,
      r=>r.recoveryEvidence.packageArtifactId++,r=>r.recoveryEvidence.originalRun++]) {
      const changed=structuredClone(record);mutate(changed);assert.throws(()=>validateRecord(name,changed));
    }
  }
});

test('exact161 summary and preflight use new Protect generation and unchanged Website operation', () => {
  const payload=JSON.parse(execFileSync('python3',['-c',String.raw`
import importlib.util,json,pathlib
root=pathlib.Path.cwd().parent
s=importlib.util.spec_from_file_location('deploy',root/'deploy-approved-app.py');d=importlib.util.module_from_spec(s);s.loader.exec_module(d)
a=d._RELEASE_AUTHORITY;p=a.PROTECT_161_REPLACEMENT
observer=dict(runId=999,runAttempt=1,jobId=998,promotionRunId=997,toolRevision='a'*40)
targets={}
for key,digest,artifact,archive in [('protect',p['packageDigest'],p['candidateArtifactId'],p['candidateArtifactDigest']),('website','d'*64,42,'sha256:'+'e'*64)]:
 targets[key]=dict(operationId=a.retained_operation_id(key,p['applicationRevision'],digest,p if key=='protect' else None),packageDigest=digest,packageArtifactId=artifact,packageArtifactDigest=archive)
original=dict(run=dict(run_attempt=2))
summary=d.recovery_summary_record(p['originalRun'],original,p['applicationRevision'],observer,{k:dict(v,deploymentIds=['new-'+k])for k,v in targets.items()})
proof=dict(originalRun=p['originalRun'],originalAttempt=2,applicationRevision=p['applicationRevision'],observer=observer,targets=targets)
name,preflight=d.recovery_preflight_nonentry_record(proof,'protect',d.RecoveryPreflightFailure('runtime-unavailable'))
assert a.validate_recovery_preflight_nonentry(preflight)==name
print(json.dumps(dict(summaryName=a.fast_track_recovery_summary_name(p['originalRun'],p['applicationRevision']),summary=summary,preflightName=name,preflight=preflight,old=p['originalOperationId'])))
`],{encoding:'utf8',cwd:__dirname}));
  assert.doesNotThrow(()=>validateRecord(payload.summaryName,payload.summary));
  assert.doesNotThrow(()=>validateRecord(payload.preflightName,payload.preflight));
  const website=payload.summary.targets.website;
  assert.equal(website.operationId,crypto.createHash('sha256').update(JSON.stringify({applicationRevision:payload.summary.applicationRevision,packageDigest:website.packageDigest,target:'website'})).digest('hex'));
  for(const [name,value] of [[payload.summaryName,payload.summary],[payload.preflightName,payload.preflight]]) {
    const old=structuredClone(value);old.targets.protect.operationId=payload.old;assert.throws(()=>validateRecord(name,old));
    const changed=structuredClone(value);changed.targets.website.operationId=value.targets.protect.operationId;assert.throws(()=>validateRecord(name,changed));
  }
});

test('stable baseline admission is four-target durable evidence, never live completion', async () => {
  const revision='a'.repeat(40), baseline='b'.repeat(40), packageDigest='c'.repeat(64);
  const observer={runId:900,runAttempt:1,jobId:901,toolRevision:'d'.repeat(40),promotionRunId:899};
  const value={schemaVersion:1,phase:'baseline-admission',authorizesMutation:false,liveStable:false,
    originalRun:800,applicationRevision:revision,observer,schemaProofIdentity:'e'.repeat(64),
    baselineAdoption:{generation:'stable-base-example-v1',authorization:'Sentinel_'+'f'.repeat(32),materialIdentity:'1'.repeat(64)},targets:{}};
  for(const target of ['portal','client','protect','parfait']) value.targets[target]={
    operationId:crypto.createHash('sha256').update(JSON.stringify({applicationRevision:revision,packageDigest,target})).digest('hex'),
    packageDigest,packageArtifactId:12,packageArtifactDigest:'sha256:'+'2'.repeat(64),baseline,baselineDeploymentIds:['prior'],
    rollbackEvidence:{revision:baseline,artifactId:13,artifactDigest:'sha256:'+'3'.repeat(64),
      artifact:`fast-track-package-${baseline}-${target}`,runId:14,packageDigest:'4'.repeat(64)}};
  const name='legend-stable-baseline-admission-900-1';
  assert.deepEqual(validateRecord(name,value),{filename:'operation.json'});
  const current=structuredClone(value);current.currentStateRoot={version:1,priorToolRevision:'5'.repeat(40),priorPromotionRun:99,terminalNonentryRun:100,terminalNonentryAttempt:1,terminalNonentryIdentity:'6'.repeat(64),history:{'Legend/fast-track-release':{count:249,identity:'7'.repeat(64)},'legend/approved-changes':{count:349,identity:'8'.repeat(64)}}};
  for(const row of Object.values(current.targets))row.providerInventoryIdentity='9'.repeat(64);
  assert.deepEqual(validateRecord(name,current),{filename:'operation.json'});
  for(const mutate of [x=>delete x.targets.portal.providerInventoryIdentity,x=>x.currentStateRoot.version=2,x=>x.currentStateRoot.terminalNonentryAttempt=2,x=>x.currentStateRoot.history['Legend/fast-track-release'].identity='UNKNOWN']){
    const bad=structuredClone(current);mutate(bad);assert.throws(()=>validateRecord(name,bad));
  }
  const carried=structuredClone(current);carried.carriedPortalSuccess={observer:{...observer,runId:700,jobId:701,promotionRunId:699},
    admissionArtifactId:70,admissionArtifactDigest:'sha256:'+'a'.repeat(64),successArtifactId:71,successArtifactDigest:'sha256:'+'b'.repeat(64),deploymentId:'deployed',
    currentServingObservation:{observer,observedUtc:'2026-10-31T20:00:00Z',revision,deploymentIds:['deployed'],providerInventoryIdentity:'c'.repeat(64)}};
  assert.deepEqual(validateRecord(name,carried),{filename:'operation.json'});
  for(const mutate of [x=>x.carriedPortalSuccess.currentServingObservation.revision=baseline,x=>x.carriedPortalSuccess.currentServingObservation.deploymentIds=[],
      x=>x.carriedPortalSuccess.observer=observer,x=>x.carriedPortalSuccess.currentServingObservation.observer={...observer,jobId:5},x=>delete x.currentStateRoot]) {
    const bad=structuredClone(carried);mutate(bad);assert.throws(()=>validateRecord(name,bad));
  }
  let bytes;const client={async listArtifacts(){return {artifacts:[]};},async uploadArtifact(n,files){bytes=await fs.readFile(files[0]);return {id:42};},
    async downloadArtifact(id,options){await fs.mkdir(options.path);await fs.writeFile(path.join(options.path,'operation.json'),bytes);}};
  assert.deepEqual(await publish(client,name,value),{artifactId:42});
  for(const change of [{liveStable:true},{authorizesMutation:true},{phase:'success'},{schemaProofIdentity:'UNKNOWN'},
    {observer:{...observer,runAttempt:2}},{targets:{...value.targets,website:value.targets.portal}}])
    assert.throws(()=>validateRecord(name,{...structuredClone(value),...change}));
  for(const field of ['rollbackEvidence','baselineDeploymentIds']){
    const bad=structuredClone(value);bad.targets.portal[field]=field==='rollbackEvidence'?null:[];
    assert.throws(()=>validateRecord(name,bad));
  }
  client.downloadArtifact=async(id,options)=>{await fs.mkdir(options.path);await fs.writeFile(path.join(options.path,'operation.json'),'changed');};
  await assert.rejects(publish(client,name,value),/readback mismatch/);
});

test('completed target acceptance is separate from provider success and binds all material', () => {
  const crypto=require('node:crypto');
  const canonical=v=>Array.isArray(v)?v.map(canonical):v&&typeof v==='object'?Object.fromEntries(Object.keys(v).sort().map(k=>[k,canonical(v[k])])):v;
  const ref=(revision,name)=>({sourceRevision:revision,runId:100,runAttempt:1,artifactId:101,artifactDigest:'sha256:'+'a'.repeat(64),artifactName:name});
  const revision='b'.repeat(40),baseline='c'.repeat(40);
  const value={schemaVersion:1,phase:'target-acceptance',target:'client',applicationRevision:revision,
    originalRun:90,originalAttempt:1,producingRun:200,producingAttempt:1,
    observer:{runId:200,runAttempt:1,jobId:201,toolRevision:'d'.repeat(40),promotionRunId:199},
    successReference:ref(revision,'legend-release-operation-success-'+'e'.repeat(64)),
    baselineReference:ref(baseline,'legend-release-operation-success-'+'f'.repeat(64)),
    rollbackEvidence:{revision:baseline,artifactId:102,artifactDigest:'sha256:'+'a'.repeat(64),artifact:`fast-track-package-${baseline}-client`,runId:99,packageDigest:'1'.repeat(64)},
    schemaProof:{artifactId:103,contentIdentity:'4'.repeat(64)},
    observation:{deploymentId:'provider-id',packageDigest:'2'.repeat(64),runtimeRevision:revision,providerInventoryIdentity:'3'.repeat(64),observedUtc:'2026-11-01T00:00:00Z',publicMetadataVerified:true}};
  const name=v=>'legend-release-target-acceptance-'+crypto.createHash('sha256').update(JSON.stringify(canonical(v))).digest('hex');
  assert.deepEqual(validateRecord(name(value),value),{filename:'operation.json'});
  for(const mutate of [v=>v.phase='success',v=>v.observation.publicMetadataVerified=false,v=>v.observation.runtimeRevision=baseline,
      v=>v.successReference.artifactName='legend-release-operation-intent-'+'e'.repeat(64),v=>v.rollbackEvidence.revision=revision,
      v=>v.observer.runId=999,v=>delete v.baselineReference,v=>v.observation.extra=true]) {
    const bad=structuredClone(value);mutate(bad);assert.throws(()=>validateRecord(name(bad),bad));
  }
  const bad=structuredClone(value);bad.observation.deploymentId='different';assert.throws(()=>validateRecord(name(value),bad));
});
