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
                 authority=None, publisher=None, environment=None):
        env = os.environ if environment is None else environment
        self.authority = authority or _authority()
        if target not in self.authority.RELEASE_TARGETS:
            raise ValueError("Unknown canonical release target")
        for value, size in ((application_revision, 40), (package_digest, 64), (baseline, 40)):
            if not isinstance(value, str) or not re.fullmatch('[a-f0-9]{%d}' % size, value):
                raise ValueError("Invalid immutable deployment identity")
        self.identity = dict(target=target, applicationRevision=application_revision, packageDigest=package_digest)
        self.operation_id = hashlib.sha256(json.dumps(self.identity, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
        self.run = int(env['GITHUB_RUN_ID'])
        self.attempt = int(env['GITHUB_RUN_ATTEMPT'])
        self.producer = int(env.get('VALIDATED_PACKAGE_RUN_ID') or env['PACKAGE_PRODUCER_RUN'])
        if min(self.run, self.attempt, self.producer) < 1:
            raise ValueError("Missing immutable evidence producer")
        self.publisher = publisher or self._publish
        self.repository = env['GITHUB_REPOSITORY']
        self.token = env.get('GH_TOKEN') or env['GITHUB_TOKEN']
        self.history_error = None
        try:
            self.intent = self.authority.release_operation_history(
                self.repository, self.operation_id, application_revision, target,
                self.run, self.attempt, self.token)
        except self.authority.ReleaseOperationHistoryUnproven as exc:
            # Defer missing first-write proof, not malformed or conflicting
            # retained evidence. A healthy exact-candidate needs no upload.
            self.history_error = exc
            self.intent = None
        self.baseline = baseline
        if self.intent is not None:
            if (any(self.intent.get(key) != value for key, value in self.identity.items()) or
                    self.intent.get('operationId') != self.operation_id or
                    self.intent.get('schemaVersion') != 1 or self.intent.get('phase') != 'intent' or
                    not re.fullmatch('[a-f0-9]{40}', self.intent.get('baseline', ''))):
                raise ValueError("Prior deployment intent is not compatible")
            self._ids(self.intent.get('baselineDeploymentIds'))
            self.baseline = self.intent['baseline']

    @staticmethod
    def _ids(values):
        if (not isinstance(values, (list, tuple, set)) or len(values) > 1000 or
                any(not isinstance(value, str) or not re.fullmatch('[a-zA-Z0-9_.:-]{1,256}', value) for value in values)):
            raise ValueError("Invalid Azure deployment identities")
        return sorted(set(values))

    def before_submit(self, baseline_ids, *, allow_recovered_baseline=False):
        if self.history_error is not None and not allow_recovered_baseline:
            raise self.history_error
        if self.intent is not None:
            return False
        record = dict(self.identity, schemaVersion=1, operationId=self.operation_id,
                      baseline=self.baseline, packageProducerRun=self.producer,
                      producingRun=self.run, producingAttempt=self.attempt,
                      baselineDeploymentIds=self._ids(baseline_ids), phase='intent')
        self.publisher('legend-release-operation-intent-' + self.operation_id, record)
        self.intent = record
        if allow_recovered_baseline:
            self.history_error = None
        return True

    def record_success(self, deployment_ids):
        if self.intent is None:
            raise RuntimeError('Exact deployment intent required before artifact success')
        observed = self._ids(deployment_ids)
        baseline_ids = set(self._ids(self.intent.get('baselineDeploymentIds')))
        if len(observed) != 1 or set(observed).intersection(baseline_ids):
            raise RuntimeError('Unique successful post-intent deployment required')
        existing = self.authority.release_operation_history(
            self.repository, self.operation_id, self.identity['applicationRevision'],
            self.identity['target'], self.run, self.attempt, self.token, phase='success')
        if existing is not None:
            if (any(existing.get(key) != value for key, value in self.identity.items()) or
                    existing.get('baseline') != self.baseline or existing.get('phase') != 'success'):
                raise RuntimeError("Prior deployment success receipt identity mismatch")
            retained = set(self._ids(existing.get('deploymentIds')))
            if (self._ids(existing.get('baselineDeploymentIds')) != sorted(baseline_ids) or
                    retained - baseline_ids != set(observed)):
                raise RuntimeError('Prior artifact receipt lacks matching provider deployment proof')
            return existing
        record = dict(self.intent, schemaVersion=1,
                      operationId=self.operation_id, baseline=self.baseline,
                      packageProducerRun=self.producer, producingRun=self.run,
                      producingAttempt=self.attempt, phase='success',
                      deploymentIds=observed)
        return self.publisher('legend-release-operation-success-' + self.operation_id, record)


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
        self.evidence_identity = self.authority.direct_child_identity(child, self.execution_revision, material_identity)
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
                        last_applied_migration_id=None):
        if self.intent is not None or self.success is not None:
            raise RuntimeError('Retained release child requires read-only reconciliation; no mutation replay authorized')
        schema_fence = {}
        if self.child == 'migrations':
            schema_fence = dict(first_pending_migration_id=first_pending_migration_id,
                                last_applied_migration_id=last_applied_migration_id,
                                current_application_revision=self.revision)
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
