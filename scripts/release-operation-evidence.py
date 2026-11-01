#!/usr/bin/env python3
"""Synchronous deployment journal over the canonical Actions evidence channel.

No Azure write is authorized until an immutable intent has been uploaded and
read back. Existing intent authorizes reconciliation only, never resubmission.
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import functools


@functools.lru_cache(maxsize=1)
def _authority():
    spec = importlib.util.spec_from_file_location("release_evidence_authority", Path(__file__).with_name("validation-resume.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class RecordContractError(RuntimeError):
    """Local serializer disagreement; no artifact operation was attempted."""


def validate_record(name, record, *, timeout=30):
    """Use the publication validator without publishing or granting authority."""
    try:
        result = subprocess.run(
            ['node', str(Path(__file__).with_name('release-artifacts') / 'transport.cjs')],
            input=json.dumps(dict(mode='validate', name=name, record=record)),
            capture_output=True, text=True, timeout=timeout, check=False)
    except (OSError, subprocess.TimeoutExpired):
        raise RecordContractError('RELEASE_RECORD_CONTRACT_UNAVAILABLE') from None
    if result.returncode == 2 and result.stderr.strip() == 'LEGEND_RECORD_CONTRACT_INVALID':
        raise RecordContractError('RELEASE_RECORD_CONTRACT_INVALID')
    if result.returncode or result.stdout.strip() != 'LEGEND_RECORD_CONTRACT_VALID':
        raise RecordContractError('RELEASE_RECORD_CONTRACT_UNAVAILABLE')


def publish_record(name, record, *, timeout=180):
    result = subprocess.run(
        ['node', str(Path(__file__).with_name('release-artifacts') / 'transport.cjs')],
        input=json.dumps(dict(name=name, record=record)), capture_output=True,
        text=True, timeout=timeout, check=False)
    # Do not forward SDK output: error strings can include signed URLs.
    marker = 'LEGEND_OPERATION_RESULT='
    rows = [line[len(marker):] for line in result.stdout.splitlines() if line.startswith(marker)]
    if result.returncode or len(rows) != 1:
        raise RuntimeError("Durable operation publication/readback unavailable; reconcile without writing")
    artifact = json.loads(rows[0])
    if type(artifact.get('artifactId')) is not int or artifact['artifactId'] < 1:
        raise RuntimeError("Deployment operation lacks durable readback identity")
    return artifact



def claim_record(name, record, *, timeout=180):
    """Atomically claim one immutable Actions artifact name.

    The canonical Node transport returns created=true only when this invocation
    received the provider's create acknowledgment. Lost acknowledgments or an
    already-existing exact artifact return created=false and therefore never
    grant new provider-call ownership.
    """
    result = subprocess.run(
        ['node', str(Path(__file__).with_name('release-artifacts') / 'transport.cjs')],
        input=json.dumps(dict(mode='claim', name=name, record=record)),
        capture_output=True, text=True, timeout=timeout, check=False)
    marker = 'LEGEND_OPERATION_RESULT='
    rows = [line[len(marker):] for line in result.stdout.splitlines() if line.startswith(marker)]
    if result.returncode or len(rows) != 1:
        raise RuntimeError("Durable operation claim/readback unavailable; reconcile without writing")
    artifact = json.loads(rows[0])
    if (type(artifact.get('artifactId')) is not int or artifact['artifactId'] < 1
            or type(artifact.get('created')) is not bool):
        raise RuntimeError("Deployment operation claim lacks durable ownership identity")
    return {'status': 'created' if artifact['created'] else 'existing', 'record': dict(record)}


def readiness_refresh_once(identity, *, lookup, observe, execute, publisher=publish_record, environment=None):
    """Record one read-only child rerun request; ambiguous delivery never replays.

    The serialized lifecycle supplies authenticated lookup and exact provider
    observation. This is operation evidence, not another recovery scheduler.
    """
    env = os.environ if environment is None else environment
    if (set(identity) != {'candidateRevision', 'executionAuthority', 'targetRun', 'targetAttempt', 'targetJob'} or
        any(not re.fullmatch('[a-f0-9]{40}', str(identity[key])) for key in ('candidateRevision', 'executionAuthority')) or
        any(type(identity[key]) is not int or identity[key] < 1 for key in ('targetRun', 'targetAttempt', 'targetJob'))):
        raise ValueError('Invalid readiness recovery identity')
    operation_inputs = {key: value for key, value in identity.items() if key != 'executionAuthority'}
    operation = hashlib.sha256(json.dumps(operation_inputs, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    prior = lookup(operation)
    remote = observe()
    if remote.get('id') != identity['targetRun'] or type(remote.get('run_attempt')) is not int:
        raise RuntimeError('Readiness recovery remote identity unproven')
    if remote['run_attempt'] > identity['targetAttempt']:
        return {'state': 'READINESS_REFRESH_OBSERVED', 'runId': remote['id'], 'attempt': remote['run_attempt']}
    if remote['run_attempt'] != identity['targetAttempt'] or remote.get('status') != 'completed':
        return {'state': 'READINESS_ACTIVE', 'runId': remote['id']}
    if prior is not None:
        if any(prior.get(key) != value for key, value in operation_inputs.items()) or prior.get('operationId') != operation:
            raise RuntimeError('Readiness recovery intent mismatch')
        return {'state': 'READINESS_BLOCKED', 'reason': 'rerun_acknowledgment_unresolved',
                'operationId': operation, 'resume': 'Reconcile the recorded target job/run attempt; do not submit another request'}
    record = dict(identity, schemaVersion=1, operationId=operation, phase='intent',
                  producingRun=int(env['GITHUB_RUN_ID']), producingAttempt=int(env['GITHUB_RUN_ATTEMPT']))
    publisher('legend-readiness-recovery-' + operation, record)
    try:
        execute()
    except Exception:
        # The retained intent plus exact remote observation is the only resume
        # boundary. No exception text or second POST crosses this boundary.
        remote = observe()
        if remote.get('id') == identity['targetRun'] and remote.get('run_attempt', 0) > identity['targetAttempt']:
            return {'state': 'READINESS_REFRESH_OBSERVED', 'runId': remote['id'], 'attempt': remote['run_attempt']}
        return {'state': 'READINESS_BLOCKED', 'reason': 'rerun_acknowledgment_unresolved', 'operationId': operation}
    return {'state': 'READINESS_REFRESH_REQUESTED', 'runId': identity['targetRun'], 'jobId': identity['targetJob']}


class OperationJournal:
    _publish = staticmethod(publish_record)
    def __init__(self, *, target, application_revision, package_digest, baseline,
                 authority=None, publisher=None, environment=None, candidate_revision=None, component_source=None, supersession=None, replacement=None, before_new_intent=None):
        env = os.environ if environment is None else environment
        self.authority = authority or _authority()
        self.forward_before_new_intent = before_new_intent
        if target not in self.authority.RELEASE_TARGETS:
            raise ValueError("Unknown canonical release target")
        for value, size in ((application_revision, 40), (package_digest, 64), (baseline, 40)):
            if not isinstance(value, str) or not re.fullmatch('[a-f0-9]{%d}' % size, value):
                raise ValueError("Invalid immutable deployment identity")
        self.identity = dict(target=target, applicationRevision=application_revision, packageDigest=package_digest)
        if replacement is not None and getattr(self, 'recovery_evidence', {}).get('unknownReplacement') != replacement:
            raise ValueError('Replacement requires the retained recovery producer')
        self.replacement = replacement
        self.operation_id = (self.authority.retained_operation_id(target, application_revision, package_digest, replacement)
            if replacement is not None else hashlib.sha256(json.dumps(self.identity, sort_keys=True, separators=(',', ':')).encode()).hexdigest())
        self.run = int(env['GITHUB_RUN_ID'])
        self.attempt = int(env['GITHUB_RUN_ATTEMPT'])
        self._new_intent_readback = False
        self.authorization = None
        if candidate_revision is not None or component_source is not None:
            self.authorization = self._authorization(candidate_revision, component_source)
        self.producer = (component_source['runId'] if self.authorization else
                         int(env.get('VALIDATED_PACKAGE_RUN_ID') or env['PACKAGE_PRODUCER_RUN']))
        if min(self.run, self.attempt, self.producer) < 1:
            raise ValueError("Missing immutable evidence producer")
        self.publisher = publisher or self._publish
        self.repository = env['GITHUB_REPOSITORY']
        self.token = env.get('GH_TOKEN') or env['GITHUB_TOKEN']
        self.supersession = supersession
        if supersession is not None:
            if (target != 'protect' or candidate_revision is not None
                or supersession != self.authority.fast_track_protect_supersession(application_revision)):
                raise ValueError('Replacement authorization does not bind this operation')
        self.history_error = None
        try:
            self.intent = self.authority.release_operation_history(
                self.repository, self.operation_id, application_revision, target,
                self.run, self.attempt, self.token, **({'replacement': replacement} if replacement is not None else {}))
        except self.authority.ReleaseOperationHistoryUnproven as exc:
            # Defer missing first-write proof, not malformed or conflicting
            # retained evidence. A healthy exact-candidate needs no upload.
            self.history_error = exc
            self.intent = None
        self.baseline = baseline
        if self.intent is not None:
            if (any(self.intent.get(key) != value for key, value in self.identity.items()) or
                    self.intent.get('operationId') != self.operation_id or
                    self.intent.get('schemaVersion') not in (1, 2) or self.intent.get('phase') != 'intent' or
                    not re.fullmatch('[a-f0-9]{40}', self.intent.get('baseline', ''))):
                raise ValueError("Prior deployment intent is not compatible")
            if self.intent['schemaVersion'] == 2:
                self._authorization(self.intent.get('candidateRevision'), self.intent.get('componentSource'))
                if self.intent.get('packageProducerRun') != self.intent['componentSource']['runId']:
                    raise ValueError('Prior deployment component producer mismatch')
            elif any(key in self.intent for key in ('candidateRevision', 'componentSource')):
                raise ValueError('Legacy deployment intent contains unversioned component provenance')
            if ('providerRequestCorrelation' in self.intent and
                    self.intent['providerRequestCorrelation'] != self._provider_request_correlation()):
                raise ValueError('Deployment request correlation does not bind its operation')
            if 'submissionContract' in self.intent:
                if (self.intent['submissionContract'] != self._windows_submission_contract() or
                        'providerRequestCorrelation' in self.intent):
                    raise ValueError('Windows submission contract does not bind its operation')
            self._ids(self.intent.get('baselineDeploymentIds'))
            self.baseline = self.intent['baseline']
        if self.intent is not None and self.intent.get('supersession') != self.supersession:
            raise ValueError('Replacement intent authorization changed')
        self.submission_observation = None
        if self.intent is not None and 'submissionContract' in self.intent:
            self.submission_observation = self.authority.release_operation_history(
                self.repository, self.operation_id, application_revision, target,
                self.run, self.attempt, self.token, phase='submitted')
            if self.submission_observation is not None:
                self._validate_submission_observation(self.submission_observation)

    def _provider_request_correlation(self):
        # Metadata only: Azure does not promise idempotency for deployment tags.
        return dict(kind='azure_onedeploy_deployment_tag_v1', value=self.operation_id)

    def _windows_submission_contract(self):
        # Local intent identity only. Windows OneDeploy does not accept a caller
        # deployment tag. A missing reply never permits another submission.
        return dict(kind='azure_onedeploy_windows_intent_v1', value=self.operation_id)

    def submission_correlation(self):
        # Restored intents remain reconciliation-only, including legacy records
        # which never sent a tag. Never manufacture historical request evidence.
        windows = self.intent is not None and 'submissionContract' in self.intent
        expected = self._windows_submission_contract() if windows else self._provider_request_correlation()
        if (not self._new_intent_readback or self.intent is None or
                self.intent.get('submissionContract' if windows else 'providerRequestCorrelation') != expected):
            raise RuntimeError('New durable deployment request correlation required before submit')
        return dict(expected)

    @staticmethod
    def _authorization(candidate, source):
        if not isinstance(candidate, str) or not re.fullmatch('[a-f0-9]{40}', candidate):
            raise ValueError('Exact release authorization candidate required')
        if not isinstance(source, dict) or set(source) != {
                'runId', 'runAttempt', 'artifactId', 'artifactDigest', 'artifactName',
                'receiptArtifactId', 'producingJobId'}:
            raise ValueError('Complete immutable component source required')
        if (any(type(source[key]) is not int or source[key] < 1 for key in
                ('runId', 'runAttempt', 'artifactId', 'receiptArtifactId', 'producingJobId')) or
                not re.fullmatch('sha256:[a-f0-9]{64}', source.get('artifactDigest', '')) or
                not re.fullmatch('[a-zA-Z0-9_.-]{1,256}', source.get('artifactName', ''))):
            raise ValueError('Invalid immutable component source')
        return dict(candidateRevision=candidate, componentSource=dict(source))

    @staticmethod
    def _ids(values):
        if (not isinstance(values, (list, tuple, set)) or len(values) > 1000 or
                any(not isinstance(value, str) or not re.fullmatch('[a-zA-Z0-9_.:-]{1,256}', value) for value in values)):
            raise ValueError("Invalid Azure deployment identities")
        return sorted(set(values))

    def intent_record(self, baseline_ids, *, submission_kind='azure_onedeploy_deployment_tag_v1'):
        """Pure record construction, shared by preflight and actual publication."""
        if submission_kind not in {'azure_onedeploy_deployment_tag_v1', 'azure_onedeploy_windows_intent_v1'}:
            raise ValueError('Unknown deployment submission contract')
        record = dict(self.identity, schemaVersion=1, operationId=self.operation_id,
                      baseline=self.baseline, packageProducerRun=self.producer,
                      producingRun=self.run, producingAttempt=self.attempt,
                      baselineDeploymentIds=self._ids(baseline_ids), phase='intent',
                      providerRequestCorrelation=self._provider_request_correlation())
        if submission_kind == 'azure_onedeploy_windows_intent_v1':
            del record['providerRequestCorrelation']
            record['submissionContract'] = self._windows_submission_contract()
        if self.authorization is not None:
            record.update(self.authorization, schemaVersion=2)
        if self.supersession is not None:
            record['supersession'] = dict(self.supersession)
        return record

    def submitted_record(self, intent, deployment_id):
        return dict(intent, phase='submitted', producingRun=self.run,
                    producingAttempt=self.attempt, deploymentId=deployment_id)

    def success_record(self, intent, deployment_ids):
        return dict(intent, operationId=self.operation_id, baseline=self.baseline,
                    producingRun=self.run, producingAttempt=self.attempt,
                    phase='success', deploymentIds=self._ids(deployment_ids))

    def before_submit(self, baseline_ids, *, allow_recovered_baseline=False, submission_kind='azure_onedeploy_deployment_tag_v1'):
        if submission_kind not in {'azure_onedeploy_deployment_tag_v1', 'azure_onedeploy_windows_intent_v1'}:
            raise ValueError('Unknown deployment submission contract')
        if self.history_error is not None and not allow_recovered_baseline:
            raise self.history_error
        if self.intent is not None:
            return False
        if self.supersession is not None:
            run = self.authority.assert_fast_track_release_execution(target='protect')
            if run['head_sha'] != self.identity['applicationRevision'] or run['id'] != self.run:
                raise ValueError('Replacement write authority changed')
        if self.forward_before_new_intent is not None:
            self.forward_before_new_intent(self._ids(baseline_ids))
        record = self.intent_record(baseline_ids, submission_kind=submission_kind)
        self.publisher('legend-release-operation-intent-' + self.operation_id, record)
        self.intent = record
        self._new_intent_readback = True
        if allow_recovered_baseline:
            self.history_error = None
        return True

    def _validate_submission_observation(self, record):
        validate_submission_observation(self.intent, record)

    def record_submission_observation(self, deployment_id):
        if self.intent is None or self.intent.get('submissionContract') != self._windows_submission_contract():
            raise RuntimeError('Windows write-ahead intent required before provider observation')
        record = self.submitted_record(self.intent, deployment_id)
        self._validate_submission_observation(record)
        if self.submission_observation is not None:
            if self.submission_observation['deploymentId'] != deployment_id:
                raise RuntimeError('Conflicting provider submission observations')
            return self.submission_observation
        result = self.publisher('legend-release-operation-submitted-' + self.operation_id, record)
        self.submission_observation = record
        return result

    def _success_history(self):
        return self.authority.release_operation_history(
            self.repository, self.operation_id, self.identity['applicationRevision'],
            self.identity['target'], self.run, self.attempt, self.token, phase='success')

    def record_success(self, deployment_ids):
        if self.intent is None:
            raise RuntimeError('Exact deployment intent required before artifact success')
        observed = self._ids(deployment_ids)
        baseline_ids = set(self._ids(self.intent.get('baselineDeploymentIds')))
        if len(observed) != 1 or set(observed).intersection(baseline_ids):
            raise RuntimeError('Unique successful post-intent deployment required')
        if (self.submission_observation is not None and
                observed != [self.submission_observation['deploymentId']]):
            raise RuntimeError('Successful deployment differs from retained provider observation')
        existing = self._success_history()
        if existing is not None:
            if (any(existing.get(key) != value for key, value in self.identity.items()) or
                    existing.get('baseline') != self.baseline or existing.get('phase') != 'success' or
                    any(existing.get(key) != self.intent.get(key) for key in
                        ('schemaVersion', 'packageProducerRun', 'candidateRevision', 'componentSource',
                         'providerRequestCorrelation', 'submissionContract', 'supersession'))):
                raise RuntimeError("Prior deployment success receipt identity mismatch")
            retained = set(self._ids(existing.get('deploymentIds')))
            if (self._ids(existing.get('baselineDeploymentIds')) != sorted(baseline_ids) or
                    retained - baseline_ids != set(observed)):
                raise RuntimeError('Prior artifact receipt lacks matching provider deployment proof')
            return existing
        # Reconciliation preserves the original upload authority and byte producer,
        # even when a later authorized candidate reuses the same operation.
        record = self.success_record(self.intent, observed)
        return self.publisher('legend-release-operation-success-' + self.operation_id, record)


def validate_submission_observation(intent, record):
    """Canonical validation shared by ordinary and completion-only journals."""
    keys = set(intent) - {'phase', 'producingRun', 'producingAttempt'}
    if (record.get('phase') != 'submitted' or
            any(record.get(key) != intent.get(key) for key in keys) or
            not isinstance(record.get('deploymentId'), str) or
            OperationJournal._ids([record['deploymentId']]) != [record['deploymentId']] or
            record['deploymentId'] in intent['baselineDeploymentIds']):
        raise RuntimeError('Provider submission observation does not bind the original intent')


def validate_original_submission_observation(intent, record, original_run):
    if record is None:
        return None
    validate_submission_observation(intent, record)
    if (set(record) != set(intent) | {'deploymentId'} or record.get('producingRun') != original_run['id']
        or type(record.get('producingAttempt')) is not int
        or not 1 <= record['producingAttempt'] <= original_run['run_attempt']):
        raise RuntimeError('Retained provider observation does not bind original execution')
    return record


class RecoveryJournal(OperationJournal):
    """Same immutable journal, with actual recovery producer provenance."""
    def __init__(self, *, recovery_evidence, before_new_intent, publisher=None, **kwargs):
        self.recovery_evidence = dict(recovery_evidence)
        self.before_new_intent = before_new_intent
        actual_publisher = publisher or publish_record
        def publish(name, record):
            # Never mutate the original intent object or claim its producer.
            value = self.publication_record(record)
            return actual_publisher(name, value)
        replacement = self.recovery_evidence.get('unknownReplacement')
        super().__init__(publisher=publish, **({'replacement': replacement} if replacement is not None else {}), **kwargs)
        if replacement is not None and self.baseline != replacement['baselineRevision']:
            raise ValueError('Recovery replacement baseline changed')
        if self.intent is not None and self.intent.get('recoveryEvidence', {}).get('unknownReplacement') != replacement:
            raise ValueError('Recovery replacement generation changed')
        if (self.recovery_evidence.get('runId') != self.run
            or self.recovery_evidence.get('runAttempt') != self.attempt
            or self.recovery_evidence.get('originalRun') != self.producer
            or self.recovery_evidence.get('applicationRevision') != self.identity['applicationRevision']):
            raise ValueError('Recovery journal producer/material mismatch')

    def publication_record(self, record):
        return dict(record, recoveryEvidence=dict(self.recovery_evidence))

    def validate_contract(self, baseline_ids, *, validator=validate_record):
        """Preview every reachable receipt through the same publication validator.

        Synthetic IDs are local shape probes only. No state, receipt, authority,
        callback or provider operation is changed by this check.
        """
        intents = ([self.intent] if self.intent is not None else [
            self.intent_record(baseline_ids, submission_kind=kind) for kind in
            ('azure_onedeploy_deployment_tag_v1', 'azure_onedeploy_windows_intent_v1')])
        for intent in intents:
            sample = 'contract-preview'
            while sample in intent['baselineDeploymentIds']:
                sample += '-next'
            records = []
            if self.intent is None:
                records.append(intent)
                if 'submissionContract' in intent:
                    records.append(self.submitted_record(intent, sample))
            records.append(self.success_record(intent, [sample]))
            for record in records:
                validator('legend-release-operation-' + record['phase'] + '-' + self.operation_id,
                          self.publication_record(record))

    def before_submit(self, baseline_ids, **kwargs):
        if self.intent is not None:
            return False
        if self.history_error is not None:
            raise self.history_error
        self.before_new_intent(self._ids(baseline_ids))
        return super().before_submit(baseline_ids, **kwargs)

    def _success_history(self):
        # This scope must close before record_success publishes any receipt.
        with self.authority.recovery_admission_proof():
            return super()._success_history()

    def record_submission_observation(self, deployment_id):
        if not self._new_intent_readback:
            raise RuntimeError('Inherited recovery intent is reconcile-only; no new submission observation')
        return super().record_submission_observation(deployment_id)


class CompletionJournal:
    """Success-only adapter. No intent/submission method or upload authority exists."""
    def __init__(self, original, material, observer, *, authority=None, publisher=None):
        self.authority=authority or _authority()
        self.publisher=publisher or publish_record
        self.original=original; self.material=material; self.observer=observer
        self.intent=dict(material['intent']); self.baseline=self.intent['baseline']
        self.operation_id=self.intent['operationId']; self.provider=None
        self.repository=os.environ['GITHUB_REPOSITORY']
        self.token=os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']

    def record_success(self, deployment_ids):
        observed=OperationJournal._ids(deployment_ids)
        if (len(observed)!=1 or observed[0] in self.intent['baselineDeploymentIds']
            or self.provider is None or self.provider['deploymentId']!=observed[0]):
            raise RuntimeError('Completion requires correlated original provider success')
        print('FAST_TRACK_COMPLETION_STAGE:receipt:'+self.intent['target']+':proof-start', flush=True)
        # Only this read-only decision shares metadata; raw boundary rechecks
        # complete before live authority validation or any receipt publication.
        with self.authority.recovery_admission_proof():
            if self.authority.assert_fast_track_completion_execution(self.original['run']['id'],self.intent['applicationRevision'])!=self.observer:
                raise RuntimeError('Completion observer changed during historical proof')
            submitted=self.authority.release_operation_history(self.repository,self.operation_id,
                self.intent['applicationRevision'],self.intent['target'],self.original['run']['id'],
                self.original['run']['run_attempt'],self.token,phase='submitted')
            validate_original_submission_observation(self.intent,submitted,self.original['run'])
            if submitted is not None and observed!=[submitted['deploymentId']]:
                raise RuntimeError('Completion differs from retained original provider observation')
            existing=self.authority.release_operation_history(self.repository,self.operation_id,
                self.intent['applicationRevision'],self.intent['target'],self.observer['runId'],
                self.observer['runAttempt'],self.token,phase='success')
            if existing is not None:
                self.authority.validate_original_completion_success(self.repository,self.original,self.material,existing,self.token)
                if (existing.get('deploymentIds')!=observed
                    or any(existing.get(k)!=v for k,v in self.intent.items() if k not in {'phase','producingRun','producingAttempt'})):
                    raise RuntimeError('Existing success differs from original operation')
        print('FAST_TRACK_COMPLETION_STAGE:receipt:'+self.intent['target']+':proof-verified', flush=True)
        if existing is not None:
            self.authority.assert_fast_track_completion_live_execution(self.original['run']['id'],self.intent['applicationRevision'],self.observer)
            return existing
        self.authority._completion_original_unchanged(self.repository,self.original['run'],self.token)
        proof=dict(originalRun=self.original['run']['id'],originalRunAttempt=self.original['run']['run_attempt'],
            originalPackageJobId=self.material['packageJob']['id'],originalPackageAttempt=self.material['packageAttempt'],intentArtifactId=self.material['intentArtifact']['id'],
            intentArtifactDigest=self.material['intentArtifact']['digest'],packageArtifactId=self.material['packageArtifact']['id'],
            packageArtifactDigest=self.material['packageArtifact']['digest'],originalJobId=self.material['originalJob']['id'],
            observer=dict(self.observer),provider=dict(self.provider))
        record=dict(self.intent,phase='success',producingRun=self.observer['runId'],
            producingAttempt=self.observer['runAttempt'],deploymentIds=observed,completionEvidence=proof)
        # This non-settling, uncached guard is the last operation before publish.
        self.authority.assert_fast_track_completion_live_execution(self.original['run']['id'],self.intent['applicationRevision'],self.observer)
        return self.publisher('legend-release-operation-success-'+self.operation_id,record)


class ChildJournal:
    """Release-child receipts on the same immutable Actions evidence channel.

    Dependency identity owns reuse; application revision is producer provenance.
    Callers must reconcile mutable provider state before consuming success.
    An existing intent never authorizes a second mutation.
    """
    _publish = staticmethod(publish_record)

    def __init__(self, child, material_identity, *, authority=None, publisher=None,
                 environment=None, partition_identity=None):
        env = os.environ if environment is None else environment
        self.authority = authority or _authority()
        self.child = child
        self.material_identity = material_identity
        self.partition_identity = partition_identity or material_identity
        self.revision = env['APPLICATION_RELEASE_SHA']
        self.execution_revision = env.get('RELEASE_SHA') or self.revision
        self.identity = self.authority.direct_child_operation_identity(child, self.execution_revision, material_identity)
        identity_options = {}
        if env.get('GITHUB_REF') == 'refs/heads/' + self.authority.FAST_TRACK_PR_BASE:
            identity_options['workflow'] = self.authority.FAST_TRACK_RELEASE_WORKFLOW
        self.evidence_identity = self.authority.direct_child_identity(child, self.execution_revision, material_identity, **identity_options)
        self.repository = env['GITHUB_REPOSITORY']
        self.token = env.get('GH_TOKEN') or env['GITHUB_TOKEN']
        self.run = int(env['GITHUB_RUN_ID'])
        self.attempt = int(env['GITHUB_RUN_ATTEMPT'])
        if min(self.run, self.attempt) < 1:
            raise ValueError('Missing child evidence producer')
        self.publisher = publisher or self._publish
        self.intent = self._history('intent')
        self.success = self._history('success')

    def _history(self, phase):
        return self.authority.release_child_history(self.repository, self.child, self.identity, self.token, phase)

    def _record(self, phase, observation):
        if (not isinstance(observation, dict) or set(observation) - {'providerVersion', 'schemaIdentity'} or
                any(not isinstance(value, str) or not re.fullmatch('[a-zA-Z0-9_.:-]{1,256}', value)
                    for value in observation.values())):
            raise ValueError('Invalid public child observation')
        return dict(schemaVersion=1, child=self.child, dependencyIdentity=self.identity,
                    materialIdentity=self.material_identity, evidenceIdentity=self.evidence_identity,
                    partitionIdentity=self.partition_identity,
                    applicationRevision=self.revision, executionAuthority=self.execution_revision,
                    producingRun=self.run, producingAttempt=self.attempt,
                    phase=phase, observation=observation)

    def before_mutation(self, observation, *, first_pending_migration_id=None,
                        last_applied_migration_id=None, historical_observation=None):
        if self.intent is not None or self.success is not None:
            raise RuntimeError('Retained release child requires read-only reconciliation; no mutation replay authorized')
        schema_fence = {}
        if self.child == 'migrations':
            schema_fence = dict(first_pending_migration_id=first_pending_migration_id,
                                last_applied_migration_id=last_applied_migration_id,
                                current_application_revision=self.revision)
            if historical_observation is not None:
                schema_fence['historical_observation'] = historical_observation
        self.authority.release_child_first_write_proven(self.repository, self.child,
            self.identity, self.material_identity, self.run, self.attempt, self.token,
            partition_identity=self.partition_identity, **schema_fence)
        record = self._record('intent', observation)
        self.publisher('legend-release-child-intent-' + self.identity, record)
        self.intent = record

    def record_success(self, observation):
        if self.success is not None:
            if self.success.get('observation') != observation:
                raise RuntimeError('Release child current state differs from preserved success')
            return self.success
        record = self._record('success', observation)
        self.publisher('legend-release-child-success-' + self.identity, record)
        self.success = record
        return record


class ConfigurationJournal:
    """Advance only a positively completed settings operation after fresh drift.

    The settings owner compares desired/current opaque values in memory. That
    comparison decides whether before_mutation is called. A deterministic
    successor binds the completed predecessor, never a new SHA or retry number.
    No secret-derived identity or mutable journal pointer is stored.
    """
    def __init__(self, child, material_identity, **kwargs):
        if child not in {'shared-config', 'editor-config'}:
            raise ValueError('Configuration generations require a canonical settings child')
        self.child = child
        self.material = material_identity
        self.kwargs = kwargs
        kwargs['partition_identity'] = material_identity
        self.current = ChildJournal(child, material_identity, **kwargs)
        self.next = None
        for _ in range(100):
            if self.current.success is None:
                return
            material = hashlib.sha256(json.dumps(dict(resourceMaterial=self.material,
                completedPredecessor=self.current.identity), sort_keys=True).encode()).hexdigest()
            successor = ChildJournal(child, material, **kwargs)
            if successor.intent is None and successor.success is None:
                self.next = successor
                return
            self.current = successor
        raise RuntimeError('Configuration generation history exceeded bound; no mutation authorized')

    def before_mutation(self, observation):
        # Called only when the owner has freshly observed a canonical source /
        # target mismatch. Positive terminal success permits a distinct repair;
        # an unresolved predecessor can never authorize a successor.
        if self.current.success is not None:
            if self.next is None:
                raise RuntimeError('Configuration successor history unavailable')
            self.current = self.next
            self.next = None
        self.current.before_mutation(observation)

    def record_success(self, observation):
        return self.current.record_success(observation)
