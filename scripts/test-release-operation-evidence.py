#!/usr/bin/env python3
import datetime as dt
import importlib.util
import json
import sys
from pathlib import Path
import tempfile
import subprocess
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('journal', Path(__file__).with_name('release-operation-evidence.py'))
journal = importlib.util.module_from_spec(spec)
spec.loader.exec_module(journal)


class ReadinessRecoveryIntentTests(unittest.TestCase):
    def setUp(self):
        self.identity = dict(candidateRevision='a' * 40, executionAuthority='b' * 40,
                             targetRun=12, targetAttempt=1, targetJob=34)
        self.env = dict(GITHUB_RUN_ID='56', GITHUB_RUN_ATTEMPT='1')
        self.records, self.posts = {}, []
        self.remote = dict(id=12, run_attempt=1, status='completed')

    def invoke(self, publisher=None, execute=None):
        def publish(name, record):
            self.records[record['operationId']] = record
            return {'artifactId': 78}
        return journal.readiness_refresh_once(self.identity, lookup=self.records.get,
            observe=lambda: self.remote, execute=execute or (lambda: self.posts.append(34)),
            publisher=publisher or publish, environment=self.env)

    def test_duplicate_events_and_authority_update_preserve_one_request(self):
        self.assertEqual('READINESS_REFRESH_REQUESTED', self.invoke()['state'])
        self.env['GITHUB_RUN_ID'] = '57'
        self.identity['executionAuthority'] = 'c' * 40
        self.assertEqual('READINESS_BLOCKED', self.invoke()['state'])
        self.assertEqual([34], self.posts)
        self.assertEqual(1, len(self.records))

    def test_lost_rerun_acknowledgment_reconciles_exact_new_attempt(self):
        def execute():
            self.posts.append(34)
            self.remote = dict(id=12, run_attempt=2, status='in_progress')
            raise TimeoutError('lost acknowledgment')
        self.assertEqual('READINESS_REFRESH_OBSERVED', self.invoke(execute=execute)['state'])
        self.assertEqual('READINESS_REFRESH_OBSERVED', self.invoke()['state'])
        self.assertEqual([34], self.posts)

    def test_worker_dies_after_intent_before_request_never_guesses(self):
        def crash(name, record):
            self.records[record['operationId']] = record
            raise TimeoutError('intent readback lost')
        with self.assertRaises(TimeoutError): self.invoke(publisher=crash)
        self.assertEqual('READINESS_BLOCKED', self.invoke()['state'])
        self.assertEqual([], self.posts)


class DurableOperationProtocolTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name)
        self.env = dict(GITHUB_RUN_ID='10', GITHUB_RUN_ATTEMPT='1', PACKAGE_PRODUCER_RUN='8',
                        GITHUB_REPOSITORY='owner/repo', GH_TOKEN='test-placeholder')
        self.authority = type('Authority', (), dict(RELEASE_TARGETS={'portal': {}, 'client': {}},
                              ReleaseOperationHistoryUnproven=journal._authority().ReleaseOperationHistoryUnproven))()
        self.authority.release_operation_history = lambda repo, key, *args, phase='intent': (
            json.loads((self.path / (phase + key)).read_text()) if (self.path / (phase + key)).exists() else None)

    def publish(self, name, record):
        (self.path / (record['phase'] + record['operationId'])).write_text(json.dumps(record))
        return {'artifactId': 12}

    def make(self, **kwargs):
        args = dict(target='portal', application_revision='a' * 40, package_digest='b' * 64,
                    baseline='c' * 40, authority=self.authority, publisher=self.publish, environment=self.env)
        args.update(kwargs)
        return journal.OperationJournal(**args)

    def test_crash_after_post_fresh_run_never_replays_and_retains_original_baseline(self):
        first = self.make()
        self.assertTrue(first.before_submit(['baseline-operation']))
        writes = 1  # Azure accepted; runner crashes before a success receipt.
        self.env['GITHUB_RUN_ID'] = '11'
        resumed = self.make(baseline='d' * 40)
        self.assertEqual(resumed.baseline, 'c' * 40)
        self.assertEqual(resumed.intent['baselineDeploymentIds'], ['baseline-operation'])
        if resumed.before_submit([]):
            writes += 1
        self.assertEqual(writes, 1)

    def test_crash_between_durable_intent_and_post_stays_read_only(self):
        self.assertTrue(self.make().before_submit([]))
        self.assertFalse(self.make().before_submit([]))

    def test_untouched_other_target_has_distinct_operation(self):
        self.assertTrue(self.make().before_submit([]))
        self.assertTrue(self.make(target='client').before_submit([]))

    def test_publication_timeout_never_authorizes_write_and_next_run_recovers_intent(self):
        def timeout(name, record):
            self.publish(name, record)
            raise TimeoutError('readback interrupted')
        with self.assertRaises(TimeoutError):
            self.make(publisher=timeout).before_submit([])
        self.assertFalse(self.make().before_submit([]))

    def test_correlation_is_available_only_after_verified_new_intent(self):
        operation = self.make()
        with self.assertRaisesRegex(RuntimeError, 'New durable'):
            operation.submission_correlation()
        operation.before_submit(['old'])
        expected = {'kind': 'azure_onedeploy_deployment_tag_v1', 'value': operation.operation_id}
        self.assertEqual(expected, operation.submission_correlation())
        saved = json.loads((self.path / ('intent' + operation.operation_id)).read_text())
        self.assertEqual(expected, saved['providerRequestCorrelation'])
        copy = operation.submission_correlation()
        copy['value'] = 'f' * 64
        self.assertEqual(expected, operation.submission_correlation())
        with self.assertRaisesRegex(RuntimeError, 'New durable'):
            self.make().submission_correlation()

    def test_lost_readback_cannot_expose_new_submission_correlation(self):
        def lost(name, record):
            self.publish(name, record)
            raise TimeoutError('lost readback')
        operation = self.make(publisher=lost)
        with self.assertRaises(TimeoutError):
            operation.before_submit([])
        with self.assertRaisesRegex(RuntimeError, 'New durable'):
            operation.submission_correlation()
        with self.assertRaisesRegex(RuntimeError, 'New durable'):
            self.make().submission_correlation()

    def test_legacy_intent_and_success_bytes_are_not_annotated_or_rewritten(self):
        operation = self.make()
        operation.before_submit(['old'])
        intent_path = self.path / ('intent' + operation.operation_id)
        legacy = json.loads(intent_path.read_text())
        del legacy['providerRequestCorrelation']
        intent_path.write_text(json.dumps(legacy, indent=2))
        original = intent_path.read_bytes()
        restored = self.make()
        self.assertFalse(restored.before_submit([]))
        with self.assertRaisesRegex(RuntimeError, 'New durable'):
            restored.submission_correlation()
        restored.record_success(['fresh'])
        receipt_path = self.path / ('success' + operation.operation_id)
        receipt = receipt_path.read_bytes()
        self.assertNotIn('providerRequestCorrelation', json.loads(receipt))
        self.make().record_success(['fresh'])
        self.assertEqual(original, intent_path.read_bytes())
        self.assertEqual(receipt, receipt_path.read_bytes())

    def test_correlation_mismatch_is_rejected_without_mutating_retained_evidence(self):
        operation = self.make()
        operation.before_submit([])
        intent_path = self.path / ('intent' + operation.operation_id)
        saved = json.loads(intent_path.read_text())
        for value in [None, {'kind': 'other', 'value': operation.operation_id},
                      {'kind': 'azure_onedeploy_deployment_tag_v1', 'value': 'f' * 64}]:
            with self.subTest(value=value):
                saved['providerRequestCorrelation'] = value
                intent_path.write_text(json.dumps(saved))
                original = intent_path.read_bytes()
                with self.assertRaisesRegex(ValueError, 'correlation'):
                    self.make()
                self.assertEqual(original, intent_path.read_bytes())

    def test_success_receipt_cannot_claim_a_different_request_tag(self):
        operation = self.make()
        operation.before_submit(['old'])
        operation.record_success(['fresh'])
        receipt_path = self.path / ('success' + operation.operation_id)
        receipt = json.loads(receipt_path.read_text())
        receipt['providerRequestCorrelation']['value'] = 'f' * 64
        receipt_path.write_text(json.dumps(receipt))
        original = receipt_path.read_bytes()
        with self.assertRaisesRegex(RuntimeError, 'identity mismatch'):
            self.make().record_success(['fresh'])
        self.assertEqual(original, receipt_path.read_bytes())

    def test_history_uncertainty_never_authorizes_write(self):
        self.authority.release_operation_history = lambda *args: (_ for _ in ()).throw(RuntimeError('expired'))
        with self.assertRaisesRegex(RuntimeError, 'expired'):
            self.make()

    def test_unavailable_absence_proof_does_not_block_read_only_candidate(self):
        exception = self.authority.ReleaseOperationHistoryUnproven
        self.authority.release_operation_history = lambda *args: (_ for _ in ()).throw(exception('unavailable'))
        preserved = self.make(baseline='a' * 40)
        self.assertIsNone(preserved.intent)
        with self.assertRaises(exception):
            preserved.before_submit([])

    def test_success_receipt_is_reused_without_duplicate_upload(self):
        first = self.make()
        first.before_submit([])
        first.record_success(['successful-operation'])
        def forbidden(*args):
            raise AssertionError('Receipt upload replay')
        self.env['GITHUB_RUN_ID'] = '11'
        preserved = self.make(publisher=forbidden).record_success(['successful-operation'])
        self.assertEqual(preserved['producingRun'], 10)

    def test_source_only_or_failed_provider_cannot_create_artifact_receipt(self):
        operation = self.make()
        with self.assertRaisesRegex(RuntimeError, 'Exact deployment intent'):
            operation.record_success(['old-artifact-at-same-source'])
        self.assertEqual([], list(self.path.iterdir()))
        operation.before_submit(['old-artifact-at-same-source'])
        for ids in ([], ['old-artifact-at-same-source'], ['first', 'second']):
            with self.subTest(ids=ids), self.assertRaisesRegex(RuntimeError, 'Unique successful'):
                operation.record_success(ids)
        self.assertEqual(1, len(list(self.path.iterdir())))  # Intent only.

    def test_legacy_success_keeps_valid_post_intent_proof_without_rewriting(self):
        operation = self.make()
        operation.before_submit(['baseline'])
        operation.record_success(['deployed'])
        path = self.path / ('success' + operation.operation_id)
        receipt = json.loads(path.read_text())
        receipt['deploymentIds'] = ['baseline', 'deployed']
        path.write_text(json.dumps(receipt))
        original = path.read_bytes()
        self.assertEqual(receipt, self.make().record_success(['deployed']))
        self.assertEqual(original, path.read_bytes())
        with self.assertRaisesRegex(RuntimeError, 'matching provider'):
            self.make().record_success(['unrelated'])

    def test_new_candidate_reconciles_same_bytes_without_rewriting_original_authority(self):
        source=dict(runId=71,runAttempt=2,artifactId=81,artifactDigest='sha256:'+'d'*64,
                    artifactName='validated-component-bytes-test',receiptArtifactId=82,producingJobId=91)
        first=self.make(candidate_revision='d'*40,component_source=source)
        self.assertTrue(first.before_submit(['baseline']))
        writes=1
        self.env['GITHUB_RUN_ID']='11'
        later_source=dict(source,runId=72,artifactId=83)
        resumed=self.make(candidate_revision='e'*40,component_source=later_source)
        self.assertEqual(first.operation_id,resumed.operation_id)
        if resumed.before_submit([]): writes+=1
        resumed.record_success(['committed'])
        receipt=json.loads((self.path/('success'+resumed.operation_id)).read_text())
        self.assertEqual(1,writes)
        self.assertEqual(2,receipt['schemaVersion'])
        self.assertEqual('d'*40,receipt['candidateRevision'])
        self.assertEqual(source,receipt['componentSource'])
        self.assertEqual(71,receipt['packageProducerRun'])
        self.assertEqual(11,receipt['producingRun'])

    def test_legacy_reconciliation_preserves_original_package_producer(self):
        first=self.make(); first.before_submit([])
        self.env['PACKAGE_PRODUCER_RUN']='99'
        self.make().record_success(['committed'])
        receipt=json.loads((self.path/('success'+first.operation_id)).read_text())
        self.assertEqual(1,receipt['schemaVersion'])
        self.assertEqual(8,receipt['packageProducerRun'])

    def test_incomplete_component_source_cannot_create_intent(self):
        with self.assertRaisesRegex(ValueError,'Complete immutable component source'):
            self.make(candidate_revision='d'*40,component_source={'runId':71})
        self.assertEqual([],list(self.path.iterdir()))

    def test_sdk_error_payload_not_exposed(self):
        result = type('Result', (), dict(returncode=1, stdout='secret signed URL', stderr='secret token'))()
        with patch.object(journal.subprocess, 'run', return_value=result):
            with self.assertRaisesRegex(RuntimeError, 'unavailable') as failure:
                self.make(publisher=None)._publish('name', {})
            self.assertNotIn('secret', str(failure.exception))


    def test_windows_intent_is_not_a_caller_tag_and_response_is_not_success(self):
        operation = self.make()
        operation.before_submit(['baseline'], submission_kind='azure_onedeploy_windows_intent_v1')
        self.assertNotIn('providerRequestCorrelation', operation.intent)
        self.assertEqual('azure_onedeploy_windows_intent_v1', operation.submission_correlation()['kind'])
        operation.record_submission_observation('new-id')
        self.assertFalse((self.path / ('success' + operation.operation_id)).exists())
        resumed = self.make()
        self.assertFalse(resumed.before_submit([]))
        with self.assertRaises(RuntimeError):
            resumed.submission_correlation()
        with self.assertRaisesRegex(RuntimeError, 'differs from retained'):
            resumed.record_success(['other-id'])
        resumed.record_success(['new-id'])

    def test_windows_observation_cannot_bind_baseline_or_mutated_identity(self):
        operation = self.make()
        operation.before_submit(['baseline'], submission_kind='azure_onedeploy_windows_intent_v1')
        for reference in ['baseline', 'https://host/id?secret=hidden', '', None]:
            with self.subTest(reference=reference), self.assertRaises((RuntimeError, ValueError)):
                operation.record_submission_observation(reference)
        operation.record_submission_observation('new-id')
        path = self.path / ('submitted' + operation.operation_id)
        record = json.loads(path.read_text())
        record['packageDigest'] = 'd' * 64
        path.write_text(json.dumps(record))
        with self.assertRaisesRegex(RuntimeError, 'original intent'):
            self.make()

    def test_lost_windows_observation_readback_never_authorizes_replay(self):
        operation = self.make()
        operation.before_submit(['baseline'], submission_kind='azure_onedeploy_windows_intent_v1')
        original = (self.path / ('intent' + operation.operation_id)).read_bytes()
        def ambiguous(name, record):
            self.publish(name, record)
            raise TimeoutError('lost receipt acknowledgment')
        operation.publisher = ambiguous
        with self.assertRaises(TimeoutError):
            operation.record_submission_observation('new-id')
        resumed = self.make()
        self.assertFalse(resumed.before_submit([]))
        self.assertEqual('new-id', resumed.submission_observation['deploymentId'])
        self.assertEqual(original, (self.path / ('intent' + operation.operation_id)).read_bytes())


class CanonicalHistoryTests(unittest.TestCase):
    def setUp(self):
        self.authority = journal._authority()
        self.authority._RELEASE_HISTORY_VERIFIED_PACKAGES.clear()
        self.authority._RELEASE_HISTORY_SOURCES.clear()
        self.authority._RELEASE_HISTORY_API.clear()
        self.authority._RELEASE_HISTORY_TERMINAL_RUNS.clear()
        self.authority._RELEASE_HISTORY_RECEIPTS.clear()
        self.authority._RELEASE_HISTORY_LOG_BINDINGS.clear()
        self.authority._RELEASE_HISTORY_EXCLUSIONS.clear()
        self.job = dict(name='release', status='completed', conclusion='failure', steps=[
            dict(name='Publish canonical target (portal)', status='completed', conclusion='skipped')])
        self.run = dict(id=9, run_attempt=1, head_branch=self.authority.TRUSTED_PR_BASE,
                        event='workflow_dispatch', head_sha='e' * 40, status='completed',
                        path='.github/workflows/' + self.authority.DIRECT_RELEASE_WORKFLOW,
                        head_repository=dict(full_name='owner/repo'),
                        display_title='LEGEND release pr=1 candidate=' + 'a' * 40 + ' authority=' + 'd' * 40)
        self.artifacts = []

    def api(self, repository, path, token):
        if path == 'actions/runs?branch=Legend%2Ffast-track-release&per_page=100':
            # Complete branch inventory with no canonical fast-track producer.
            unrelated = dict(id=8, head_sha='f' * 40,
                head_branch=self.authority.FAST_TRACK_PR_BASE, event='workflow_dispatch',
                path='.github/workflows/unrelated.yml',
                head_repository=dict(full_name=repository))
            return dict(workflow_runs=[unrelated], total_count=1)
        if path.startswith('actions/artifacts?'):
            return dict(artifacts=self.artifacts, total_count=len(self.artifacts))
        if path.startswith('actions/workflows/'):
            return dict(workflow_runs=[self.run], total_count=1)
        if path == 'actions/runs/9/artifacts?per_page=100':
            return dict(artifacts=[], total_count=0)
        if path.startswith('compare/'):
            self.assertIn('...' + 'd' * 40, path)  # Actual checkout, not event head.
            return dict(status='ahead')
        if '/jobs?' in path:
            return dict(jobs=[self.job], total_count=1)
        if path == 'actions/runs/9':
            return self.run
        raise AssertionError(path)

    def history(self):
        with patch.object(self.authority, 'api_get', side_effect=self.api):
            return self.authority.release_operation_history('owner/repo', 'b' * 64, 'a' * 40, 'portal', 10, 1, 'placeholder')

    def test_positive_target_never_started_allows_first_write(self):
        self.assertIsNone(self.history())

    def test_submission_observation_requires_authenticated_producer_and_exact_operation(self):
        name = 'legend-release-operation-submitted-' + 'b' * 64
        self.artifacts = [dict(name=name, expired=False, workflow_run=dict(id=9))]
        record = dict(operationId='b' * 64, target='portal', applicationRevision='a' * 40,
                      producingRun=9, producingAttempt=1, phase='submitted',
                      packageDigest='c' * 64, baseline='d' * 40, deploymentId='provider-id')
        def download(repo, run, artifact_name, directory):
            self.assertEqual(name, artifact_name)
            (directory / 'operation.json').write_text(json.dumps(record))
        def observe():
            with patch.object(self.authority, 'api_get', side_effect=self.api), \
                 patch.object(self.authority, '_download_run_artifact', side_effect=download):
                return self.authority.release_operation_history(
                    'owner/repo', 'b' * 64, 'a' * 40, 'portal', 10, 1, 'placeholder', phase='submitted')
        self.assertEqual(record, observe())
        self.authority._RELEASE_HISTORY_API.clear()
        record['target'] = 'client'
        with self.assertRaisesRegex(RuntimeError, 'bind its producer'):
            observe()
        self.authority._RELEASE_HISTORY_API.clear()
        record['target'] = 'portal'
        self.run['head_repository']['full_name'] = 'untrusted/repo'
        with self.assertRaisesRegex(RuntimeError, 'untrusted producer'):
            observe()

    def test_missing_submission_observation_is_not_missing_intent_authority(self):
        self.job['steps'][0]['conclusion'] = 'failure'
        with patch.object(self.authority, 'api_get', side_effect=self.api):
            self.assertIsNone(self.authority.release_operation_history(
                'owner/repo', 'b' * 64, 'a' * 40, 'portal', 10, 1, 'placeholder', phase='submitted'))
        with self.assertRaises(RuntimeError):
            self.history()

    def test_expired_submission_observation_cannot_be_discarded(self):
        self.artifacts = [dict(name='legend-release-operation-submitted-' + 'b' * 64,
                               expired=True, workflow_run=dict(id=9))]
        with patch.object(self.authority, 'api_get', side_effect=self.api), self.assertRaisesRegex(RuntimeError, 'expired'):
            self.authority.release_operation_history(
                'owner/repo', 'b' * 64, 'a' * 40, 'portal', 10, 1, 'placeholder', phase='submitted')

    def test_started_target_without_retained_intent_blocks_replay(self):
        self.job['steps'][0]['conclusion'] = 'failure'
        with self.assertRaisesRegex(RuntimeError, 'No authenticated legacy package'):
            self.history()

    def test_unknown_execution_owner_cannot_prove_absence(self):
        self.job['name'] = 'renamed-owner'
        with self.assertRaisesRegex(RuntimeError, 'publication owner'):
            self.history()

    def test_terminal_attempt_with_complete_empty_job_inventory_never_published(self):
        original = self.api
        def api(repo, path, token):
            if '/jobs?' in path:
                return {'jobs': [], 'total_count': 0}
            return original(repo, path, token)
        with patch.object(self.authority, 'api_get', side_effect=api):
            self.assertIsNone(self.authority.release_operation_history(
                'owner/repo', 'b' * 64, 'a' * 40, 'portal', 10, 1, 'placeholder'))

    def test_cancelled_job_with_explicit_empty_steps_never_published(self):
        self.job.update(conclusion='cancelled', steps=[])
        self.assertIsNone(self.history())

    def test_cancelled_job_without_step_inventory_stays_unproven(self):
        self.job.update(conclusion='cancelled')
        del self.job['steps']
        with self.assertRaises(RuntimeError):
            self.history()

    def test_legacy_positive_unstarted_publication_needs_no_artifact(self):
        self.run['display_title'] = 'old release'
        self.assertIsNone(self.history())

    def test_legacy_entered_publication_cannot_use_event_sha(self):
        self.run['display_title'] = 'old release'
        self.job['steps'][0]['conclusion'] = 'failure'
        with self.assertRaisesRegex(RuntimeError, 'No authenticated legacy package'):
            self.history()

    def test_expired_intent_does_not_become_missing_intent(self):
        self.artifacts = [dict(name='legend-release-operation-intent-' + 'b' * 64,
                               expired=True, workflow_run=dict(id=9))]
        with self.assertRaisesRegex(RuntimeError, 'expired'):
            self.history()

    def legacy_fixture(self, revision='f' * 40):
        self.run['display_title'] = 'old release'
        self.job['steps'] = [
            dict(name='Verify restored immutable validation package', status='completed', conclusion='success'),
            dict(name='Publish selected head as one transaction', status='completed', conclusion='failure')]
        self.state = dict(schemaVersion=2, runId=9, runAttempt=1, applicationReleaseSha=revision,
                          releaseJobConclusion='failure', steps=self.job['steps'])
        self.legacy_artifacts = [dict(name='legend-release-step-state-' + revision + '-9-1', expired=False),
                                 dict(name='translation-direct-release-' + 'd' * 40, expired=False)]
        self.old_workflow = subprocess.run(['git', 'show', 'HEAD:.github/workflows/' + self.authority.DIRECT_RELEASE_WORKFLOW],
                                           text=True, capture_output=True, check=True).stdout
        self.old_deployer = subprocess.run(['git', 'show', 'HEAD:scripts/deploy-approved-app.py'],
                                           text=True, capture_output=True, check=True).stdout
        self.old_evidence = subprocess.run(['git', 'show', 'HEAD:scripts/release-operation-evidence.py'],
                                           text=True, capture_output=True, check=True).stdout

    def legacy_history(self, reader=None):
        original = self.api
        def api(repo, path, token):
            if path == 'actions/runs/9/artifacts?per_page=100':
                return dict(artifacts=self.legacy_artifacts, total_count=len(self.legacy_artifacts))
            return original(repo, path, token)
        def source(repo, revision, path, token):
            if path.endswith('.yml'):
                return self.old_workflow
            if path.endswith('release-operation-evidence.py'):
                return self.old_evidence
            return self.old_deployer
        with patch.object(self.authority, 'api_get', side_effect=api), \
                patch.object(self.authority, '_release_history_json', return_value=self.state), \
                patch.object(self.authority, '_release_history_source', side_effect=source):
            if reader is not None:
                return reader()
            return self.authority.release_operation_history('owner/repo', 'b' * 64, 'a' * 40, 'portal', 10, 1, 'placeholder')

    def test_mixed_producer_history_blocks_replay_of_reused_bytes(self):
        self.legacy_fixture('f'*40)
        self.old_workflow=Path('.github/workflows/'+self.authority.DIRECT_RELEASE_WORKFLOW).read_text()
        self.old_deployer=Path('scripts/deploy-approved-app.py').read_text()
        self.job['steps']=[
            dict(name='Verify restored immutable validation package',status='completed',conclusion='success'),
            dict(name='Submit canonical selected targets in parallel',status='completed',conclusion='failure'),
            dict(name='Confirm first-pass durable publication receipt (portal)',status='completed',conclusion='failure')]
        source=dict(runId=71,runAttempt=2,artifactId=81,artifactDigest='sha256:'+'d'*64,
            artifactName=self.authority.component_attempt_name('portal','1'*64,71,2),
            receiptArtifactId=82,producingJobId=91)
        material=dict(producerRevision='a'*40,packageDigest='b'*64,contentIdentity='1'*64,executionIdentity='2'*64,source=source)
        self.state.update(schemaVersion=3,steps=self.job['steps'],selectedTargets=['masterapp-portal'],
                          targetMaterials={'portal':material})
        with self.assertRaisesRegex(RuntimeError,'may have written'):
            self.legacy_history()
        self.authority._RELEASE_HISTORY_VERIFIED_PACKAGES.clear()
        material['producerRevision']='c'*40
        self.assertIsNone(self.legacy_history())
        self.authority._RELEASE_HISTORY_VERIFIED_PACKAGES.clear()
        source['artifactName']=self.authority.component_attempt_name('website','1'*64,71,2)
        self.state.update(selectedTargets=[self.authority.RELEASE_TARGETS['website']['releaseName']],targetMaterials={'website':material})
        def read(aggregate):
            return self.authority._release_attempt_package_revision('owner/repo',self.run,1,self.job,'placeholder','portal',aggregate=aggregate)
        self.assertEqual('f'*40,self.legacy_history(reader=lambda:read(True)))
        with self.assertRaisesRegex(RuntimeError,'scope is incomplete'):
            self.legacy_history(reader=lambda:read(False))


    def test_authenticated_legacy_different_verified_package_does_not_block_new_operation(self):
        self.legacy_fixture()
        self.assertIsNone(self.legacy_history())

    def test_retained_rollback_identity_binds_checkout_without_translation(self):
        self.legacy_fixture()
        self.legacy_artifacts[1]['name'] = 'diagnostics-rollback-portal-' + 'd' * 40
        self.assertIsNone(self.legacy_history())

    def test_rollback_checkout_proof_still_blocks_same_package_replay(self):
        self.legacy_fixture('a' * 40)
        self.legacy_artifacts[1]['name'] = 'diagnostics-rollback-portal-' + 'd' * 40
        with self.assertRaisesRegex(RuntimeError, 'may have written this immutable package'):
            self.legacy_history()

    def test_rollback_checkout_proof_requires_original_workflow_binding(self):
        self.legacy_fixture()
        self.legacy_artifacts[1]['name'] = 'diagnostics-rollback-portal-' + 'd' * 40
        self.old_workflow = self.old_workflow.replace(
            'name: diagnostics-rollback-${{ matrix.app }}-${{ env.RELEASE_SHA }}',
            'name: unrelated-artifact')
        with self.assertRaisesRegex(RuntimeError, 'recognized producer binding'):
            self.legacy_history()

    def test_conflicting_checkout_artifacts_do_not_authorize_exclusion(self):
        self.legacy_fixture()
        self.legacy_artifacts.append(dict(name='diagnostics-rollback-portal-' + 'c' * 40, expired=False))
        with self.assertRaisesRegex(RuntimeError, 'checkout/producer revision'):
            self.legacy_history()

    def test_retained_log_binds_checkout_when_artifacts_expire(self):
        self.legacy_fixture()
        self.job['id'] = 123
        self.job['steps'].insert(0, dict(name='Run actions/checkout@v4', status='completed', conclusion='success'))
        self.legacy_artifacts = self.legacy_artifacts[:1]
        log = ('2026-10-02T12:00:00.000Z   RELEASE_SHA: ' + 'd' * 40 + '\n'
               '2026-10-02T12:00:00.001Z   APPLICATION_RELEASE_SHA: ' + 'f' * 40 + '\n'
               '2026-10-02T12:00:00.002Z [command]/usr/bin/git log -1 --format=%H\n'
               '2026-10-02T12:00:00.003Z ' + 'd' * 40 + '\n')
        with patch.object(self.authority, '_release_job_log', return_value=log):
            self.assertIsNone(self.legacy_history())

    def test_checkout_log_rejects_conflicting_package_and_missing_head(self):
        job = dict(id=123, steps=[dict(name='Run actions/checkout@v4', conclusion='success')])
        valid = '  RELEASE_SHA: ' + 'd' * 40 + '\n  APPLICATION_RELEASE_SHA: ' + 'f' * 40 + '\n'
        valid += '[command]/usr/bin/git log -1 --format=%H\n' + 'd' * 40 + '\n'
        for log in (valid.replace('f' * 40, 'a' * 40), valid.split('[command]')[0],
                    valid + '  RELEASE_SHA: ' + 'c' * 40 + '\n'):
            with self.subTest(log=log), patch.object(self.authority, '_release_job_log', return_value=log):
                with self.assertRaisesRegex(RuntimeError, 'missing or contradictory'):
                    self.authority._release_checkout_from_job_log('owner/repo', job, 'f' * 40, 'placeholder')

    def test_retired_inline_package_requires_success_and_exact_revision_binding(self):
        workflow = '''jobs:
  release:
    steps:
      - name: Build exact selected release candidate
        run: dotnet build AgentPortal/AgentPortal.csproj -p:SourceRevisionId="$RELEASE_SHA"
      - name: Publish exact selected application packages
        run: |
          dotnet publish AgentPortal/AgentPortal.csproj -c Release --no-build --no-restore -o /tmp/agentportal-publish -p:SourceRevisionId="$RELEASE_SHA"
          apps+=(agentportal)
          (cd "/tmp/$app-publish" && zip -qr "/tmp/diagnostics-packages/$app.zip" .)
          sha256sum /tmp/diagnostics-packages/*.zip > /tmp/diagnostics-packages/SHA256SUMS
      - name: Direct deploy AgentPortal
        uses: azure/webapps-deploy@v3
        with:
          package: /tmp/agentportal-publish
'''
        job = {'steps': [{'name': name, 'conclusion': 'success'} for name in (
            'Build exact selected release candidate', 'Publish exact selected application packages',
            'Direct deploy AgentPortal')]}
        verify = self.authority._legacy_inline_package_revision
        self.assertEqual('d' * 40, verify(workflow, job, 'portal', 'd' * 40))
        self.assertIsNone(verify(workflow.replace('$RELEASE_SHA', '$OTHER_SHA'), job, 'portal', 'd' * 40))
        job['steps'][1]['conclusion'] = 'failure'
        self.assertIsNone(verify(workflow, job, 'portal', 'd' * 40))
        job['steps'][1]['conclusion'] = 'success'
        job['steps'].append({'name': 'Load exact preserved deployable package', 'conclusion': 'success'})
        self.assertIsNone(verify(workflow, job, 'portal', 'd' * 40))

    def test_legacy_target_skip_does_not_need_package_receipts(self):
        self.run['display_title'] = 'old release'
        self.job['steps'][0]['name'] = 'Direct deploy AgentPortal'
        self.assertIsNone(self.history())

    def test_authenticated_legacy_same_package_missing_intent_blocks_replay(self):
        self.legacy_fixture('a' * 40)
        with self.assertRaisesRegex(RuntimeError, 'may have written this immutable package'):
            self.legacy_history()

    def test_authenticated_same_package_preupload_failure_allows_first_write(self):
        self.legacy_fixture('a' * 40)
        self.job['id'] = 123
        self.old_workflow = self.old_workflow.replace(
            'Publish selected head as one transaction', 'Publish canonical target (portal)')
        self.job['steps'][1]['name'] = 'Publish canonical target (portal)'
        log = (
            '2026-10-05T03:19:14.000Z masterapp-portal: approved revision ' + 'a' * 40 +
            ', ZIP sha256 ' + 'b' * 64 + '\n'
            '2026-10-05T03:20:58.000Z DeploymentStatusUnavailable: Azure deployment status remained unavailable '
            'for 3 consecutive reads (before any upload). No deployment or rollback write was replayed; '
            'resume by reconciling the exact revision.\n'
            '2026-10-05T03:20:58.001Z ##[error]Process completed with exit code 1.\n'
        )
        with patch.object(self.authority, '_release_job_log', return_value=log):
            self.assertIsNone(self.legacy_history())
            original = self.old_deployer
            self.old_deployer = original.replace('def reconcile(', 'def unrecognized_reconcile(', 1)
            self.authority._RELEASE_HISTORY_EXCLUSIONS.clear()
            with self.assertRaisesRegex(RuntimeError, 'may have written'):
                self.legacy_history()
            self.old_deployer = original
            self.old_evidence = self.old_evidence.replace('def before_submit(', 'def changed_before_submit(', 1)
            self.authority._RELEASE_HISTORY_EXCLUSIONS.clear()
            with self.assertRaisesRegex(RuntimeError, 'may have written'):
                self.legacy_history()

    def test_preupload_failure_proof_rejects_any_submit_marker(self):
        self.legacy_fixture('a' * 40)
        self.job['id'] = 123
        self.old_workflow = self.old_workflow.replace(
            'Publish selected head as one transaction', 'Publish canonical target (portal)')
        self.job['steps'][1]['name'] = 'Publish canonical target (portal)'
        log = (
            '2026-10-05T03:19:14.000Z masterapp-portal: approved revision ' + 'a' * 40 +
            ', ZIP sha256 ' + 'b' * 64 + '\n'
            '2026-10-05T03:19:20.000Z Submitting the verified immutable ZIP once.\n'
            '2026-10-05T03:20:58.000Z DeploymentStatusUnavailable: Azure deployment status remained unavailable '
            'for 3 consecutive reads (before any upload). No deployment or rollback write was replayed; '
            'resume by reconciling the exact revision.\n'
            '2026-10-05T03:20:58.001Z ##[error]Process completed with exit code 1.\n'
        )
        with patch.object(self.authority, '_release_job_log', return_value=log):
            with self.assertRaisesRegex(RuntimeError, 'may have written this immutable package'):
                self.legacy_history()

    def test_legacy_receipt_attempt_mismatch_is_tampering_not_absence(self):
        self.legacy_fixture()
        self.state['runAttempt'] = 2
        with self.assertRaisesRegex(RuntimeError, 'producer identity mismatch'):
            self.legacy_history()

    def test_legacy_verifier_changed_cannot_exclude_prior_publication(self):
        self.legacy_fixture()
        self.old_deployer = self.old_deployer.replace("raise ValueError('Package does not contain the approved revision')", 'pass')
        with self.assertRaisesRegex(RuntimeError, 'verifier contract is incompatible'):
            self.legacy_history()

    def test_modern_unrelated_package_does_not_block_queued_older_candidate(self):
        self.legacy_fixture()
        self.run['display_title'] = 'LEGEND release pr=2 candidate=' + 'f' * 40 + ' authority=' + 'd' * 40
        self.old_workflow = self.old_workflow.replace('Publish selected head as one transaction', 'Publish canonical target (portal)')
        self.job['steps'][1]['name'] = 'Publish canonical target (portal)'
        self.assertIsNone(self.legacy_history())

    def test_pre_reuse_translation_identity_requires_source_and_verified_package_binding(self):
        self.legacy_fixture()
        self.legacy_artifacts = [self.legacy_artifacts[1]]
        self.old_workflow = self.old_workflow.replace(
            '      APPLICATION_RELEASE_SHA: ${{ needs.discover-live.outputs.application_release_sha }}\n', '')
        self.old_workflow = self.old_workflow.replace('APPLICATION_RELEASE_SHA', 'RELEASE_SHA')
        self.old_deployer = self.old_deployer.replace(
            "os.environ.get('APPLICATION_RELEASE_SHA') or os.environ.get('RELEASE_SHA')", "os.environ['RELEASE_SHA']")
        self.assertIsNone(self.legacy_history())
        self.authority._RELEASE_HISTORY_VERIFIED_PACKAGES.clear()
        self.authority._RELEASE_HISTORY_EXCLUSIONS.clear()
        self.authority._RELEASE_HISTORY_API.clear()
        self.job['steps'][0]['conclusion'] = 'failure'
        with self.assertRaisesRegex(RuntimeError, 'did not pass embedded revision verification'):
            self.legacy_history()

    def test_transaction_snapshot_reuses_terminal_proof_after_fresh_inventory(self):
        self.assertIsNone(self.history())
        snapshot = self.authority.export_release_history_snapshot('a' * 40)
        self.authority._RELEASE_HISTORY_API.clear()
        self.authority._RELEASE_HISTORY_EXCLUSIONS.clear()
        self.authority.import_release_history_snapshot(snapshot, 'a' * 40)
        paths = []
        def api(repo, path, token):
            paths.append(path)
            return self.api(repo, path, token)
        with patch.object(self.authority, 'api_get', side_effect=api):
            self.authority.release_operation_history('owner/repo', 'b' * 64, 'a' * 40, 'portal', 10, 1, 'placeholder')
        self.assertTrue(any('actions/workflows/' in path for path in paths))
        self.assertFalse(any('/jobs?' in path or path.startswith('compare/') for path in paths))

    def test_new_attempt_invalidates_imported_terminal_exclusion(self):
        self.assertIsNone(self.history())
        snapshot = self.authority.export_release_history_snapshot('a' * 40)
        self.authority._RELEASE_HISTORY_API.clear()
        self.authority.import_release_history_snapshot(snapshot, 'a' * 40)
        self.run['run_attempt'] = 2
        self.job['steps'][0]['conclusion'] = 'failure'
        with self.assertRaisesRegex(RuntimeError, 'No authenticated legacy package'):
            self.history()


class HistoricalLogTransportTests(unittest.TestCase):
    def setUp(self):
        self.authority = journal._authority()

    def test_redirect_strips_credentials_and_requires_https(self):
        request = self.authority.urllib.request.Request('https://api.github.com/repos/owner/repo/actions/jobs/9/logs')
        request.add_unredirected_header('Authorization', 'Bearer test-placeholder')
        handler = self.authority._ReleaseLogRedirect()
        redirected = handler.redirect_request(request, None, 302, 'Found', {}, 'https://logs.example.invalid/log')
        self.assertIsNone(redirected.get_header('Authorization'))
        with self.assertRaisesRegex(RuntimeError, 'HTTPS'):
            handler.redirect_request(request, None, 302, 'Found', {}, 'http://logs.example.invalid/log')

    def test_log_read_retries_transport_failure_and_keeps_auth_on_initial_request(self):
        import io
        from unittest.mock import Mock
        opener = Mock()
        opener.open.side_effect = [TimeoutError('signed-url-must-stay-private'), io.BytesIO(b'log metadata')]
        with patch.object(self.authority.urllib.request, 'build_opener', return_value=opener), \
             patch.object(self.authority.time, 'sleep'):
            self.assertEqual('log metadata', self.authority._release_job_log('owner/repo', 9, 'test-placeholder'))
        self.assertEqual(2, opener.open.call_count)
        request = opener.open.call_args.args[0]
        self.assertEqual('Bearer test-placeholder', request.get_header('Authorization'))
        self.assertNotIn('Authorization', request.headers)

    def test_operation_journal_recovered_baseline_can_publish_one_fresh_intent(self):
        authority = self.authority
        env = {
            'GITHUB_RUN_ID': '10',
            'GITHUB_RUN_ATTEMPT': '1',
            'PACKAGE_PRODUCER_RUN': '11',
            'GITHUB_REPOSITORY': 'owner/repo',
            'GITHUB_TOKEN': 'placeholder',
        }
        published = []
        with patch.object(
            authority,
            'release_operation_history',
            side_effect=authority.ReleaseOperationHistoryUnproven('missing intent'),
        ):
            operation = journal.OperationJournal(
                target='portal',
                application_revision='a' * 40,
                package_digest='b' * 64,
                baseline='c' * 40,
                authority=authority,
                publisher=lambda name, record: published.append((name, record)),
                environment=env,
            )
        self.assertTrue(operation.before_submit({'old'}, allow_recovered_baseline=True))
        self.assertIsNone(operation.history_error)
        self.assertEqual(1, len(published))
        self.assertFalse(operation.before_submit({'old'}, allow_recovered_baseline=True))

    def test_permission_failure_reports_only_job_and_status_without_retry(self):
        from unittest.mock import Mock
        opener = Mock()
        opener.open.side_effect = self.authority.urllib.error.HTTPError(
            'https://logs.example.invalid/private-signed-url', 403, 'private response', {}, None)
        with patch.object(self.authority.urllib.request, 'build_opener', return_value=opener):
            with self.assertRaisesRegex(RuntimeError, 'job 9; HTTP 403') as failure:
                self.authority._release_job_log('owner/repo', 9, 'test-placeholder')
        self.assertEqual(1, opener.open.call_count)
        self.assertNotIn('private', str(failure.exception))
        self.assertNotIn('test-placeholder', str(failure.exception))

    def test_log_size_is_bounded_before_parsing(self):
        import io
        from unittest.mock import Mock
        opener = Mock()
        opener.open.return_value = io.BytesIO(b'x' * (32 * 1024 * 1024 + 1))
        with patch.object(self.authority.urllib.request, 'build_opener', return_value=opener):
            with self.assertRaisesRegex(RuntimeError, 'exceeds evidence limit'):
                self.authority._release_job_log('owner/repo', 9, 'test-placeholder')


class TransactionPreflightResumeTests(unittest.TestCase):
    def setUp(self):
        self.authority = journal._authority()
        self.source = Path('.github/workflows/' + self.authority.DIRECT_RELEASE_WORKFLOW).read_text()
        blocks = self.authority.named_step_blocks(self.authority._job_blocks(self.source)['release'])
        prepare = 'Prepare complete immutable release transaction'
        later = list(blocks)[list(blocks).index(prepare) + 1:]
        self.owner = {'name': 'release', 'status': 'completed', 'conclusion': 'failure', 'steps': [
            {'name': prepare, 'status': 'completed', 'conclusion': 'failure'},
            *[{'name': name, 'status': 'completed', 'conclusion': 'skipped'} for name in later]]}
        for row in self.owner['steps']:
            if row['name'] == 'Refresh Azure OIDC before transactional publication':
                row['conclusion'] = 'success'
            if row['name'] == 'Enforce complete direct deployment outcome':
                row['conclusion'] = 'failure'

    def prove(self):
        return self.authority._failed_transaction_preparation_without_writes(self.source, self.owner)

    def test_failed_preflight_with_positive_skipped_effects_is_resumable(self):
        self.assertTrue(self.prove())

    def test_started_or_missing_publication_does_not_authorize_new_baseline(self):
        target = next(row for row in self.owner['steps'] if row['name'] == 'Submit canonical selected targets in parallel')
        target['conclusion'] = 'failure'
        self.assertFalse(self.prove())
        self.owner['steps'].remove(target)
        self.assertFalse(self.prove())

    def test_configuration_write_or_successful_preparation_still_requires_plan(self):
        self.owner['steps'][1]['conclusion'] = 'success'
        self.assertFalse(self.prove())
        self.owner['steps'][1]['conclusion'] = 'skipped'
        self.owner['steps'][0]['conclusion'] = 'success'
        self.assertFalse(self.prove())

    def test_changed_observer_body_cannot_hide_a_write(self):
        self.source = self.source.replace('Refresh Azure OIDC before transactional publication',
                                          'Unknown execution after preparation')
        self.assertFalse(self.prove())

    def test_missing_plan_after_failed_preflight_resumes_but_started_effect_blocks(self):
        import hashlib
        revision, digests = 'a' * 40, {'portal': 'b' * 64}
        identity = hashlib.sha256(json.dumps({'candidateRevision': revision, 'packageDigests': digests},
            sort_keys=True, separators=(',', ':')).encode()).hexdigest()
        prior = {'id': 9, 'run_attempt': 1, 'head_sha': 'c' * 40,
                 'head_branch': self.authority.TRUSTED_PR_BASE, 'event': 'workflow_dispatch',
                 'path': '.github/workflows/' + self.authority.DIRECT_RELEASE_WORKFLOW,
                 'head_repository': {'full_name': 'owner/repo'}}
        def api(repo, path, token):
            if '/jobs?' in path:
                return {'jobs': [self.owner], 'total_count': 1}
            return {'artifacts': [], 'total_count': 0}
        with patch.object(self.authority, '_release_history_api', side_effect=api), \
             patch.object(self.authority, '_release_history_runs', return_value=[prior]), \
             patch.object(self.authority, '_release_history_source', return_value=self.source), \
             patch.object(self.authority, '_release_attempt_package_revision', return_value=revision):
            self.assertIsNone(self.authority.release_transaction_plan_history(
                'owner/repo', identity, revision, digests, 10, 1, 'placeholder'))
            self.owner['steps'][1]['conclusion'] = 'success'
            with self.assertRaisesRegex(RuntimeError, 'Original transaction plan is missing'):
                self.authority.release_transaction_plan_history(
                    'owner/repo', identity, revision, digests, 10, 1, 'placeholder')


class CompletionJournalTests(unittest.TestCase):
    def setUp(self):
        from types import SimpleNamespace
        from unittest.mock import Mock
        self.intent=dict(target='portal',applicationRevision='a'*40,packageDigest='b'*64,baseline='c'*40,
            operationId='d'*64,phase='intent',schemaVersion=1,producingRun=1,producingAttempt=1,
            packageProducerRun=1,baselineDeploymentIds=['old'],submissionContract=dict(kind='azure_onedeploy_windows_intent_v1',value='d'*64))
        self.material=dict(intent=self.intent,intentArtifact=dict(id=10,digest='sha256:'+'e'*64),
            packageJob=dict(id=12),packageAttempt=1,packageArtifact=dict(id=11,digest='sha256:'+'f'*64),originalJob=dict(id=12))
        self.observer=dict(runId=2,runAttempt=1,jobId=20,toolRevision='f'*40,promotionRunId=3)
        self.success=None;self.submitted=None
        self.authority=SimpleNamespace(_completion_original_unchanged=Mock(),validate_original_completion_success=Mock(),assert_fast_track_completion_execution=Mock(return_value=self.observer),release_operation_history=Mock(side_effect=lambda *args,**kw:self.submitted if kw.get('phase')=='submitted' else self.success))
        self.publish=Mock(return_value=dict(artifactId=42));self.env=dict(GITHUB_REPOSITORY='owner/repo',GH_TOKEN='test-only')
    def owner(self):
        import os
        with patch.dict(os.environ,self.env):
            owner=journal.CompletionJournal(dict(run=dict(id=1,run_attempt=1)),self.material,self.observer,authority=self.authority,publisher=self.publish)
        owner.provider=dict(deploymentId='new',receivedTime='2026-10-21T13:02:00Z',status=4,runtimeRevision='a'*40)
        return owner
    def test_completion_writes_success_only_with_real_observer(self):
        owner=self.owner();owner.record_success(['new']);name,record=self.publish.call_args.args
        self.assertEqual(name,'legend-release-operation-success-'+'d'*64)
        self.assertEqual(record['producingRun'],2);self.assertEqual(record['completionEvidence']['originalRun'],1)
        self.assertEqual(self.intent['phase'],'intent');self.assertEqual(self.intent['producingRun'],1)
        self.assertFalse(hasattr(owner,'before_submit'));self.assertFalse(hasattr(owner,'record_submission_observation'))
    def test_original_success_preserved_without_new_record(self):
        success=dict(self.intent,phase='success',deploymentIds=['new'])
        self.success=success
        self.assertEqual(self.owner().record_success(['new']),success);self.publish.assert_not_called()
    def test_missing_or_mismatched_provider_never_writes(self):
        for provider,ids in [(None,['new']),(dict(deploymentId='other'),['new']),(dict(deploymentId='old'),['old']),(dict(deploymentId='new'),['new','other'])]:
            owner=self.owner();owner.provider=provider
            with self.subTest(provider=provider),self.assertRaises(RuntimeError):owner.record_success(ids)
        self.publish.assert_not_called()
    def test_changed_observer_or_conflicting_success_never_writes(self):
        self.authority.assert_fast_track_completion_execution.return_value=dict(self.observer,jobId=99)
        with self.assertRaises(RuntimeError):self.owner().record_success(['new'])
        self.authority.assert_fast_track_completion_execution.return_value=self.observer
        self.success=dict(self.intent,phase='success',deploymentIds=['other'])
        with self.assertRaises(RuntimeError):self.owner().record_success(['new'])
        self.publish.assert_not_called()
    def test_retained_submitted_id_must_match_completion(self):
        self.submitted=dict(self.intent,phase='submitted',deploymentId='other')
        with self.assertRaisesRegex(RuntimeError,'differs from retained'):self.owner().record_success(['new'])
        self.publish.assert_not_called()
        self.submitted['deploymentId']='new'
        self.owner().record_success(['new']);self.publish.assert_called_once()
    def test_invalid_submitted_identity_blocks_completion(self):
        self.submitted=dict(self.intent,phase='submitted',deploymentId='new',producingRun=999)
        with self.assertRaisesRegex(RuntimeError,'original execution'):self.owner().record_success(['new'])
        self.publish.assert_not_called()
    def test_website_existing_submitted_and_success_are_preserved(self):
        self.submitted=dict(self.intent,phase='submitted',deploymentId='new')
        self.success=dict(self.intent,phase='success',deploymentIds=['new'])
        self.assertEqual(self.owner().record_success(['new']),self.success);self.publish.assert_not_called()
    def test_ambiguous_journal_publication_is_not_retried(self):
        self.publish.side_effect=TimeoutError()
        with self.assertRaises(TimeoutError):self.owner().record_success(['new'])
        self.publish.assert_called_once()

class RetainedRecoveryJournalTests(DurableOperationProtocolTests):
    def recovery(self, **kwargs):
        evidence = dict(runId=int(self.env['GITHUB_RUN_ID']), runAttempt=1, originalRun=8,
                        applicationRevision='a' * 40, toolRevision='d' * 40)
        args = dict(target='portal', application_revision='a' * 40, package_digest='b' * 64,
                    baseline='c' * 40, authority=self.authority, publisher=self.publish, environment=self.env,
                    recovery_evidence=evidence, before_new_intent=lambda ids: None)
        args.update(kwargs)
        return journal.RecoveryJournal(**args)

    def test_new_intent_has_actual_recovery_producer(self):
        owner = self.recovery()
        self.assertTrue(owner.before_submit(['old']))
        stored = json.loads((self.path / ('intent' + owner.operation_id)).read_text())
        self.assertEqual(stored['producingRun'], 10)
        self.assertEqual(stored['packageProducerRun'], 8)
        self.assertEqual(stored['recoveryEvidence']['toolRevision'], 'd' * 40)

    def test_existing_original_intent_is_byte_preserved_and_reconcile_only(self):
        original = self.make()
        original.before_submit(['old'])
        path = self.path / ('intent' + original.operation_id)
        before = path.read_bytes()
        def forbidden(*args): raise AssertionError('No new write boundary for inherited intent')
        owner = self.recovery(before_new_intent=forbidden, publisher=forbidden)
        self.assertFalse(owner.before_submit(['different']))
        self.assertEqual(path.read_bytes(), before)
        with self.assertRaisesRegex(RuntimeError, 'reconcile-only'):
            owner.record_submission_observation('provider')

    def test_previous_recovery_intent_is_immutable_on_resume(self):
        owner = self.recovery()
        owner.before_submit(['old'], submission_kind='azure_onedeploy_windows_intent_v1')
        owner.record_submission_observation('provider')
        path = self.path / ('intent' + owner.operation_id)
        before = path.read_bytes()
        self.env['GITHUB_RUN_ID'] = '11'
        resumed = self.recovery()
        self.assertFalse(resumed.before_submit(['other']))
        with self.assertRaisesRegex(RuntimeError, 'reconcile-only'):
            resumed.record_submission_observation('provider')
        self.assertEqual(path.read_bytes(), before)

    def test_failed_fresh_barrier_never_publishes_intent(self):
        def failed(ids): raise RuntimeError('provider changed')
        owner = self.recovery(before_new_intent=failed)
        with self.assertRaisesRegex(RuntimeError, 'provider changed'):
            owner.before_submit(['old'])
        self.assertFalse((self.path / ('intent' + owner.operation_id)).exists())

    def test_post_intent_timeout_preserves_reconcile_only_on_next_invocation(self):
        first=self.recovery()
        self.assertTrue(first.before_submit(['old'],submission_kind='azure_onedeploy_windows_intent_v1'))
        path=self.path/('intent'+first.operation_id);before=path.read_bytes()
        # Provider ACK timed out after entering exactly once; no observation is invented.
        entered=1
        self.env['GITHUB_RUN_ID']='11'
        resumed=self.recovery()
        if resumed.before_submit(['old']):entered+=1
        self.assertEqual(entered,1);self.assertEqual(path.read_bytes(),before)
        self.assertIsNone(resumed.submission_observation)

    def test_submitted_receipt_timeout_preserves_intent_for_next_owner(self):
        def publish(name,record):
            if record['phase']=='submitted':raise TimeoutError('receipt deadline')
            return self.publish(name,record)
        first=self.recovery(publisher=publish)
        first.before_submit(['old'],submission_kind='azure_onedeploy_windows_intent_v1')
        path=self.path/('intent'+first.operation_id);before=path.read_bytes()
        with self.assertRaises(TimeoutError):first.record_submission_observation('provider')
        self.env['GITHUB_RUN_ID']='11'
        resumed=self.recovery()
        self.assertFalse(resumed.before_submit(['old']))
        self.assertEqual(path.read_bytes(),before)
        self.assertIsNone(resumed.submission_observation)

    def test_parallel_target_failure_preserves_each_original_intent(self):
        import concurrent.futures
        owners={key:self.recovery(target=key) for key in ('portal','client')}
        def worker(key):
            owners[key].before_submit(['old'])
            if key=='portal':raise TimeoutError('provider ACK deadline')
            return 'receipt-pending'
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
            futures={key:executor.submit(worker,key) for key in owners}
            with self.assertRaises(TimeoutError):futures['portal'].result()
            self.assertEqual(futures['client'].result(),'receipt-pending')
        original={key:(self.path/('intent'+owner.operation_id)).read_bytes() for key,owner in owners.items()}
        self.env['GITHUB_RUN_ID']='11'
        for key in owners:
            resumed=self.recovery(target=key)
            self.assertFalse(resumed.before_submit(['different']))
            self.assertEqual((self.path/('intent'+resumed.operation_id)).read_bytes(),original[key])

    def test_wrong_actual_producer_rejected(self):
        with self.assertRaisesRegex(ValueError, 'producer/material'):
            self.recovery(recovery_evidence=dict(runId=99, runAttempt=1, originalRun=8,
                                                applicationRevision='a' * 40))



class ReplacementRecoveryAdmissionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        replacement_spec = importlib.util.spec_from_file_location(
            'replacement_recovery_admission',
            Path(__file__).with_name('recovery-replacement-admission.py'))
        cls.replacement = importlib.util.module_from_spec(replacement_spec)
        sys.modules[replacement_spec.name] = cls.replacement
        replacement_spec.loader.exec_module(cls.replacement)
        cls.now = dt.datetime(2026, 10, 25, 16, 40, tzinfo=dt.timezone.utc)

    def admission(self, **changes):
        value = {
            'schemaVersion': 1, 'phase': 'replacement-admission', 'admissionId': '0' * 64,
            'target': 'protect', 'originalOperationId': '1' * 64,
            'applicationRevision': 'a' * 40, 'packageDigest': '2' * 64,
            'baselineRevision': 'b' * 40, 'expectedHead': 'a' * 40,
            'controlRevision': 'c' * 40, 'rollbackPackageDigest': '3' * 64,
            'terminalNonresumableEvidenceDigest': '4' * 64,
            'serializationFenceEvidenceDigest': '5' * 64, 'noSqlEvidenceDigest': '6' * 64,
            'rollbackEvidenceDigest': '7' * 64, 'independentReviewDigest': '8' * 64,
            'contentMode': 'deterministic-full-replacement',
            'serializationKey': 'legend-approved-release',
            'observedAtUtc': '2026-10-25T16:39:00Z',
            'expiresAtUtc': '2026-10-25T16:49:00Z', 'authorizesOneReplacement': True,
        }
        value.update(changes)
        value['admissionId'] = self.replacement.recovery_admission_id(value)
        return value

    @staticmethod
    def original_intent():
        return {'operationId': '1' * 64, 'target': 'protect',
                'applicationRevision': 'd' * 40, 'packageDigest': '9' * 64,
                'baseline': 'b' * 40}

    class Store:
        def __init__(self):
            import threading
            self.rows, self.published = {}, []
            self.lock = threading.Lock()
            self.fail_claim_after_create = False
            self.claim_mode = None
            self.append_mode = None
            self.claim_calls = 0

        def lookup(self, phase, operation):
            with self.lock:
                row = self.rows.get((phase, operation))
                return None if row is None else json.loads(json.dumps(row))

        def claim(self, name, record):
            frozen = json.loads(json.dumps(record, sort_keys=True))
            key = ('intent', record['operationId'])
            with self.lock:
                self.claim_calls += 1
                if key in self.rows:
                    return {'status': 'existing',
                            'record': json.loads(json.dumps(self.rows[key]))}
                self.rows[key] = frozen
                self.published.append((name, frozen))
                if self.fail_claim_after_create or self.claim_mode == 'commit-timeout':
                    raise TimeoutError('claim readback lost')
                if self.claim_mode == 'malformed':
                    return {'status': 'created', 'record': None}
                if self.claim_mode == 'mismatch':
                    wrong = json.loads(json.dumps(frozen))
                    wrong['baseline'] = 'e' * 40
                    return {'status': 'created', 'record': wrong}
                return {'status': 'created', 'record': json.loads(json.dumps(frozen))}

        def append(self, name, record):
            frozen = json.loads(json.dumps(record, sort_keys=True))
            key = (record['phase'], record['operationId'])
            with self.lock:
                if self.append_mode == 'no-commit-timeout':
                    raise TimeoutError('append write unavailable')
                if key in self.rows and self.rows[key] != frozen:
                    raise AssertionError('conflicting durable record')
                self.rows[key] = frozen
                self.published.append((name, frozen))
                if self.append_mode == 'commit-timeout':
                    raise TimeoutError('append readback lost')
                if self.append_mode == 'malformed':
                    return {'unexpected': True}
                if self.append_mode == 'mismatch':
                    wrong = json.loads(json.dumps(frozen))
                    wrong['producingAttempt'] = wrong['producingAttempt'] + 1
                    return {'record': wrong}
                return {'record': json.loads(json.dumps(frozen))}

    def owner(self, store=None, admission=None, run=30, clock=None):
        store = store or self.Store()
        return self.replacement.ReplacementOperationJournal(
            admission=admission or self.admission(), original_intent=self.original_intent(),
            lookup=store.lookup, claim=store.claim, append=store.append,
            package_producer_run=20, producing_run=run, producing_attempt=1,
            clock=clock or (lambda: self.now)), store

    def test_replacement_operation_is_distinct_and_binds_all_admission_evidence(self):
        owner, _ = self.owner()
        changed, _ = self.owner(admission=self.admission(noSqlEvidenceDigest='f' * 64))
        self.assertNotEqual('1' * 64, owner.operation_id)
        self.assertNotEqual(owner.operation_id, changed.operation_id)

    def test_freshness_is_rechecked_at_claim_not_only_construction(self):
        moment = [self.now]
        owner, store = self.owner(clock=lambda: moment[0])
        moment[0] = dt.datetime(2026, 10, 25, 16, 50, tzinfo=dt.timezone.utc)
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'fresh'):
            owner.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
        self.assertEqual(0, store.claim_calls)

    def test_expired_consumed_operation_loads_reconcile_only_without_renewed_authority(self):
        store = self.Store()
        first, _ = self.owner(store=store)
        self.assertTrue(first.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1'))
        expired = dt.datetime(2026, 10, 25, 16, 55, tzinfo=dt.timezone.utc)
        resumed, _ = self.owner(store=store, admission=first.admission, run=31, clock=lambda: expired)
        self.assertEqual('reconcile-only', resumed.state().state)
        self.assertFalse(resumed.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1'))
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'authority'):
            resumed.submission_correlation()

    def test_preconstructed_journals_share_one_atomic_claim(self):
        store = self.Store()
        left, _ = self.owner(store=store)
        right, _ = self.owner(store=store)
        self.assertTrue(left.before_submit(['old'], submission_kind='azure_onedeploy_windows_intent_v1'))
        self.assertFalse(right.before_submit(['old'], submission_kind='azure_onedeploy_windows_intent_v1'))
        self.assertEqual(2, store.claim_calls)
        self.assertEqual(1, len([x for x in store.published if x[1]['phase'] == 'intent']))
        self.assertEqual(left.operation_id, left.submission_correlation()['value'])
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'authority'):
            right.submission_correlation()

    def test_concurrent_journals_cannot_both_obtain_submission_authority(self):
        import concurrent.futures
        import threading
        store = self.Store()
        owners = [self.owner(store=store)[0], self.owner(store=store)[0]]
        barrier = threading.Barrier(2)
        def claim(owner):
            barrier.wait()
            return owner.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
            results = list(executor.map(claim, owners))
        self.assertEqual([False, True], sorted(results))
        authority = 0
        for owner in owners:
            try:
                owner.submission_correlation()
                authority += 1
            except self.replacement.RecoveryAdmissionError:
                pass
        self.assertEqual(1, authority)

    def test_uncertain_claim_write_fails_closed_even_when_intent_committed(self):
        store = self.Store()
        store.fail_claim_after_create = True
        owner, _ = self.owner(store=store)
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'uncertain'):
            owner.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
        self.assertEqual('reconcile-only', owner.state().state)
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'reload'):
            owner.submission_correlation()
        resumed, _ = self.owner(store=store, admission=owner.admission, run=31)
        self.assertEqual('reconcile-only', resumed.state().state)
        self.assertFalse(resumed.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1'))

    def test_provider_observation_must_be_new_and_revokes_submission_authority(self):
        owner, _ = self.owner()
        owner.before_submit(['old'], submission_kind='azure_onedeploy_windows_intent_v1')
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'outside the baseline'):
            owner.record_submission_observation('old')
        owner.record_submission_observation('new-deployment')
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'authority'):
            owner.submission_correlation()
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'authority'):
            owner.record_submission_observation('different-deployment')

    def test_malformed_terminal_success_is_rejected_on_resume(self):
        owner, store = self.owner()
        owner.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
        bad = dict(owner.intent, phase='success')
        store.rows[('success', owner.operation_id)] = bad
        with self.assertRaises(self.replacement.RecoveryAdmissionError):
            self.owner(store=store, admission=owner.admission, run=31)

    def test_runtime_provider_and_producer_conflicts_are_rejected_on_resume(self):
        owner, store = self.owner()
        owner.before_submit(['old'], submission_kind='azure_onedeploy_windows_intent_v1')
        submitted = dict(owner.intent, phase='submitted', deploymentId='new')
        submitted['producingRun'] = owner.intent['producingRun'] + 1
        store.rows[('submitted', owner.operation_id)] = submitted
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'producer'):
            self.owner(store=store, admission=owner.admission, run=31)
        store.rows.pop(('submitted', owner.operation_id))
        success = dict(owner.intent, phase='success', deploymentIds=['new'],
            providerTerminalStatus=4, runtimeRevision='e' * 40,
            liveAcceptanceDigest='a' * 64, contentVerificationDigest='b' * 64,
            serializationReleaseDigest='c' * 64)
        store.rows[('success', owner.operation_id)] = success
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'runtime'):
            self.owner(store=store, admission=owner.admission, run=31)
        success['runtimeRevision'] = 'a' * 40
        success['deploymentIds'] = ['old']
        store.rows[('success', owner.operation_id)] = success
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'new provider'):
            self.owner(store=store, admission=owner.admission, run=31)

    def test_submitted_and_success_provider_conflict_is_rejected(self):
        owner, store = self.owner()
        owner.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
        owner.record_submission_observation('submitted-id')
        success = dict(owner.intent, phase='success', deploymentIds=['other-id'],
            providerTerminalStatus=4, runtimeRevision='a' * 40,
            liveAcceptanceDigest='a' * 64, contentVerificationDigest='b' * 64,
            serializationReleaseDigest='c' * 64,
            producingRun=31, producingAttempt=1)
        store.rows[('success', owner.operation_id)] = success
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'conflicts'):
            self.owner(store=store, admission=owner.admission, run=31)

    def test_ambiguous_result_revokes_authority_and_can_only_resolve_append_only(self):
        owner, store = self.owner()
        owner.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
        owner.record_ambiguous(ambiguity_evidence_digest='a' * 64)
        ambiguity = json.loads(json.dumps(owner.ambiguous))
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'authority'):
            owner.submission_correlation()
        resolution = owner.resolve_ambiguous_success(
            deployment_id='new', runtime_revision='a' * 40,
            live_acceptance_digest='b' * 64, content_verification_digest='c' * 64,
            serialization_release_digest='d' * 64, reconciliation_evidence_digest='e' * 64)
        self.assertEqual('terminal-success', owner.state().state)
        self.assertEqual(ambiguity, store.rows[('ambiguous', owner.operation_id)])
        self.assertEqual(self.replacement._record_digest(ambiguity), resolution['ambiguityRecordDigest'])
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'Conflicting'):
            owner.resolve_ambiguous_failure(
                failure_code='provider-terminal-failure',
                terminal_evidence_digest='f' * 64,
                serialization_release_digest='1' * 64,
                reconciliation_evidence_digest='2' * 64)

    def test_terminal_failure_revokes_authority_and_rejects_later_submission(self):
        owner, _ = self.owner()
        owner.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
        owner.record_terminal_failure(
            failure_code='provider-terminal-failure',
            terminal_evidence_digest='e' * 64, serialization_release_digest='f' * 64)
        self.assertEqual('terminal-failure', owner.state().state)
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'authority'):
            owner.submission_correlation()
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'authority'):
            owner.record_submission_observation('new')
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'terminal'):
            owner.record_success(
                deployment_id='new', runtime_revision='a' * 40,
                live_acceptance_digest='a' * 64, content_verification_digest='b' * 64,
                serialization_release_digest='c' * 64)

    def test_repeated_authority_read_is_only_valid_before_any_later_phase(self):
        owner, _ = self.owner()
        owner.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
        first = owner.submission_correlation()
        second = owner.submission_correlation()
        self.assertEqual(first, second)
        owner.record_ambiguous(ambiguity_evidence_digest='a' * 64)
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'authority'):
            owner.submission_correlation()

    def test_success_commit_before_timeout_poison_blocks_opposite_terminal_until_reload(self):
        owner, store = self.owner()
        owner.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
        store.append_mode = 'commit-timeout'
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'reload'):
            owner.record_success(
                deployment_id='new', runtime_revision='a' * 40,
                live_acceptance_digest='a' * 64, content_verification_digest='b' * 64,
                serialization_release_digest='c' * 64)
        self.assertIn(('success', owner.operation_id), store.rows)
        self.assertEqual('reconcile-only', owner.state().state)
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'reload'):
            owner.record_terminal_failure(
                failure_code='provider-terminal-failure',
                terminal_evidence_digest='d' * 64, serialization_release_digest='e' * 64)
        store.append_mode = None
        resumed, _ = self.owner(store=store, admission=owner.admission, run=31)
        self.assertEqual('terminal-success', resumed.state().state)
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'cannot override'):
            resumed.record_terminal_failure(
                failure_code='provider-terminal-failure',
                terminal_evidence_digest='d' * 64, serialization_release_digest='e' * 64)

    def test_no_commit_timeout_poison_requires_reload_then_allows_same_evidence_append(self):
        owner, store = self.owner()
        owner.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
        store.append_mode = 'no-commit-timeout'
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'reload'):
            owner.record_success(
                deployment_id='new', runtime_revision='a' * 40,
                live_acceptance_digest='a' * 64, content_verification_digest='b' * 64,
                serialization_release_digest='c' * 64)
        self.assertNotIn(('success', owner.operation_id), store.rows)
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'reload'):
            owner.record_ambiguous(ambiguity_evidence_digest='d' * 64)
        store.append_mode = None
        resumed, _ = self.owner(store=store, admission=owner.admission, run=31)
        success = resumed.record_success(
            deployment_id='new', runtime_revision='a' * 40,
            live_acceptance_digest='a' * 64, content_verification_digest='b' * 64,
            serialization_release_digest='c' * 64)
        self.assertEqual(['new'], success['deploymentIds'])
        self.assertEqual('terminal-success', resumed.state().state)

    def test_malformed_append_readback_poison_blocks_all_later_mutation(self):
        owner, store = self.owner()
        owner.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
        store.append_mode = 'malformed'
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'reload'):
            owner.record_ambiguous(ambiguity_evidence_digest='a' * 64)
        self.assertIn(('ambiguous', owner.operation_id), store.rows)
        for action in (
            lambda: owner.record_terminal_failure(
                failure_code='provider-terminal-failure',
                terminal_evidence_digest='b' * 64, serialization_release_digest='c' * 64),
            lambda: owner.submission_correlation(),
        ):
            with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'reload'):
                action()
        store.append_mode = None
        resumed, _ = self.owner(store=store, admission=owner.admission, run=31)
        self.assertEqual('reconcile-only', resumed.state().state)

    def test_mismatched_append_readback_poison_then_valid_reload_uses_durable_record(self):
        owner, store = self.owner()
        owner.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
        store.append_mode = 'mismatch'
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'reload'):
            owner.record_terminal_failure(
                failure_code='provider-terminal-failure',
                terminal_evidence_digest='a' * 64, serialization_release_digest='b' * 64)
        self.assertIn(('failure', owner.operation_id), store.rows)
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'reload'):
            owner.record_success(
                deployment_id='new', runtime_revision='a' * 40,
                live_acceptance_digest='c' * 64, content_verification_digest='d' * 64,
                serialization_release_digest='e' * 64)
        store.append_mode = None
        resumed, _ = self.owner(store=store, admission=owner.admission, run=31)
        self.assertEqual('terminal-failure', resumed.state().state)
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'cannot override'):
            resumed.record_success(
                deployment_id='new', runtime_revision='a' * 40,
                live_acceptance_digest='c' * 64, content_verification_digest='d' * 64,
                serialization_release_digest='e' * 64)

    def test_malformed_and_mismatched_claim_readback_poison_instance(self):
        for mode in ('malformed', 'mismatch'):
            with self.subTest(mode=mode):
                store = self.Store()
                store.claim_mode = mode
                owner, _ = self.owner(store=store)
                with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'reload'):
                    owner.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
                self.assertEqual('reconcile-only', owner.state().state)
                with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'reload'):
                    owner.record_ambiguous(ambiguity_evidence_digest='a' * 64)
                with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'reload'):
                    owner.submission_correlation()

    def test_nonstring_claim_status_poison_requires_reload_and_blocks_mutators(self):
        for bad_status in ([], {}):
            with self.subTest(status=type(bad_status).__name__):
                store = self.Store()
                original_claim = store.claim
                def claim(name, record, status=bad_status):
                    created = original_claim(name, record)
                    self.assertEqual('created', created['status'])
                    return {'status': status, 'record': created['record']}
                store.claim = claim
                owner, _ = self.owner(store=store)
                with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'reload'):
                    owner.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
                self.assertEqual('reconcile-only', owner.state().state)
                self.assertIn(('intent', owner.operation_id), store.rows)
                for action in (
                    lambda: owner.submission_correlation(),
                    lambda: owner.record_ambiguous(ambiguity_evidence_digest='a' * 64),
                    lambda: owner.record_terminal_failure(
                        failure_code='provider-terminal-failure',
                        terminal_evidence_digest='b' * 64,
                        serialization_release_digest='c' * 64),
                ):
                    with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'reload'):
                        action()
                resumed, _ = self.owner(store=store, admission=owner.admission, run=31)
                self.assertEqual('reconcile-only', resumed.state().state)
                self.assertFalse(resumed.before_submit(
                    [], submission_kind='azure_onedeploy_windows_intent_v1'))
                with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'authority'):
                    resumed.submission_correlation()

    def test_contradictory_durable_records_are_rejected_on_fresh_reload(self):
        owner, store = self.owner()
        owner.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
        store.append_mode = 'commit-timeout'
        with self.assertRaises(self.replacement.RecoveryAdmissionError):
            owner.record_success(
                deployment_id='new', runtime_revision='a' * 40,
                live_acceptance_digest='a' * 64, content_verification_digest='b' * 64,
                serialization_release_digest='c' * 64)
        store.append_mode = None
        failure = dict(owner.intent, phase='failure',
            failureCode='provider-terminal-failure',
            terminalEvidenceDigest='d' * 64, serializationReleaseDigest='e' * 64,
            replayAuthorized=False, producingRun=31, producingAttempt=1)
        store.rows[('failure', owner.operation_id)] = failure
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'Competing direct terminal'):
            self.owner(store=store, admission=owner.admission, run=31)

    def test_admission_mutation_invalidates_identity(self):
        value = self.admission()
        value['rollbackPackageDigest'] = 'f' * 64
        with self.assertRaisesRegex(self.replacement.RecoveryAdmissionError, 'identity mismatch'):
            self.owner(admission=value)

    def test_original_ambiguous_intent_is_preserved_not_reclassified(self):
        original = self.original_intent()
        before = json.dumps(original, sort_keys=True)
        store = self.Store()
        owner = self.replacement.ReplacementOperationJournal(
            admission=self.admission(), original_intent=original, lookup=store.lookup,
            claim=store.claim, append=store.append, package_producer_run=20,
            producing_run=30, producing_attempt=1, clock=lambda: self.now)
        owner.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
        self.assertEqual(before, json.dumps(original, sort_keys=True))
        self.assertEqual('1' * 64, owner.intent['replacementOfOperationId'])


class CanonicalReplacementAdapterTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        adapter_spec = importlib.util.spec_from_file_location(
            'replacement_recovery_adapter',
            Path(__file__).with_name('release-artifacts') / 'recovery-replacement-adapter.py')
        cls.adapter = importlib.util.module_from_spec(adapter_spec)
        sys.modules[adapter_spec.name] = cls.adapter
        adapter_spec.loader.exec_module(cls.adapter)
        cls.now = dt.datetime(2026, 10, 25, 18, 30, tzinfo=dt.timezone.utc)

    def admission(self):
        value = {
            'schemaVersion': 1, 'phase': 'replacement-admission', 'admissionId': '0' * 64,
            'target': 'protect', 'originalOperationId': '1' * 64,
            'applicationRevision': 'a' * 40, 'packageDigest': '2' * 64,
            'baselineRevision': 'b' * 40, 'expectedHead': 'a' * 40,
            'controlRevision': 'c' * 40, 'rollbackPackageDigest': '3' * 64,
            'terminalNonresumableEvidenceDigest': '0' * 64,
            'serializationFenceEvidenceDigest': '5' * 64, 'noSqlEvidenceDigest': '0' * 64,
            'rollbackEvidenceDigest': '0' * 64, 'independentReviewDigest': '0' * 64,
            'contentMode': 'deterministic-full-replacement',
            'serializationKey': 'legend-approved-release',
            'observedAtUtc': '2026-10-25T18:29:00Z',
            'expiresAtUtc': '2026-10-25T18:39:00Z',
            'authorizesOneReplacement': True,
        }
        return value

    @staticmethod
    def original_intent():
        return {'operationId': '1' * 64, 'target': 'protect',
                'applicationRevision': 'd' * 40, 'packageDigest': '9' * 64,
                'baseline': 'b' * 40, 'packageProducerRun': 20,
                'producingRun': 20, 'producingAttempt': 1,
                'baselineDeploymentIds': ['old'], 'phase': 'intent',
                'schemaVersion': 1,
                'submissionContract': {'kind': 'azure_onedeploy_windows_intent_v1',
                                       'value': '1' * 64}}

    class Fence:
        def __init__(self): self.calls = 0
        def verify(self):
            self.calls += 1
            return {'runId': 30, 'runAttempt': 1, 'revision': 'c' * 40,
                    'group': 'legend-approved-release'}

    class Store:
        def __init__(self):
            self.rows = {}
            self.claims = 0
        def lookup(self, phase, operation):
            value = self.rows.get((phase, operation))
            return None if value is None else json.loads(json.dumps(value))
        def claim(self, name, record):
            self.claims += 1
            key = ('intent', record['operationId'])
            if key in self.rows:
                return {'status': 'existing', 'record': json.loads(json.dumps(self.rows[key]))}
            frozen = json.loads(json.dumps(record))
            self.rows[key] = frozen
            return {'status': 'created', 'record': frozen}
        def append(self, name, record):
            frozen = json.loads(json.dumps(record))
            self.rows[(record['phase'], record['operationId'])] = frozen
            return {'record': frozen}

    def evidence(self):
        return self.adapter.AuthenticatedRecoveryEvidence(
            terminal_nonresumable={'terminalNonresumable': True, 'baselineDeploymentIds': ['old']},
            no_sql={'pendingCount': 0}, rollback={'recoverable': True},
            independent_review={'accepted': True})

    def owner(self):
        admission = self.admission()
        fence, store = self.Fence(), self.Store()
        admission['serializationFenceEvidenceDigest'] = self.adapter._canonical_digest(fence.verify())
        admission['admissionId'] = self.adapter.PRIMITIVE.recovery_admission_id(admission)
        owner = self.adapter.CanonicalReplacementAdapter(
            admission=admission, original_intent=self.original_intent(),
            authenticated_evidence=self.evidence(), fence=fence, store=store,
            package_producer_run=20, producing_run=30, producing_attempt=1,
            clock=lambda: self.now)
        return owner, fence, store

    def test_adapter_owns_exactly_one_fake_provider_call_under_same_fence(self):
        owner, fence, store = self.owner()
        calls = []
        result = owner.call_provider_once(
            lambda correlation: calls.append(dict(correlation)) or 'new-deployment',
            baseline_deployment_ids=['old'], ambiguity_evidence_digest='e' * 64)
        self.assertEqual('new-deployment', result)
        self.assertEqual(1, len(calls))
        self.assertEqual('reconcile-only', owner.journal.state().state)
        self.assertGreaterEqual(fence.calls, 3)
        with self.assertRaisesRegex(self.adapter.RecoveryAdapterError, 'already consumed'):
            owner.call_provider_once(lambda _: self.fail('provider called twice'),
                                     baseline_deployment_ids=['old'],
                                     ambiguity_evidence_digest='f' * 64)
        self.assertEqual(1, len(calls))
        self.assertIn(('submitted', owner.journal.operation_id), store.rows)

    def test_adapter_provider_exception_records_ambiguity_and_never_replays(self):
        owner, _, store = self.owner()
        calls = [0]
        def fail(_):
            calls[0] += 1
            raise TimeoutError('synthetic provider timeout')
        with self.assertRaises(TimeoutError):
            owner.call_provider_once(fail, baseline_deployment_ids=['old'],
                                     ambiguity_evidence_digest='e' * 64)
        self.assertEqual(1, calls[0])
        self.assertEqual('reconcile-only', owner.journal.state().state)
        self.assertIn(('ambiguous', owner.journal.operation_id), store.rows)
        with self.assertRaisesRegex(self.adapter.RecoveryAdapterError, 'already consumed'):
            owner.call_provider_once(
                fail, baseline_deployment_ids=['old'],
                ambiguity_evidence_digest='e' * 64)
        self.assertEqual(1, calls[0])

    def test_fence_loss_after_fake_provider_call_records_ambiguity_and_never_replays(self):
        owner, fence, store = self.owner()
        original_verify = fence.verify
        calls = [0]
        def verify():
            calls[0] += 1
            if calls[0] >= 3:
                raise self.adapter.RecoveryAdapterError('synthetic fence loss')
            return original_verify()
        fence.verify = verify
        with self.assertRaisesRegex(self.adapter.RecoveryAdapterError, 'fence loss'):
            owner.call_provider_once(
                lambda _: 'new-deployment', baseline_deployment_ids=['old'],
                ambiguity_evidence_digest='e' * 64)
        self.assertEqual('reconcile-only', owner.journal.state().state)
        self.assertIn(('ambiguous', owner.journal.operation_id), store.rows)
        with self.assertRaisesRegex(self.adapter.RecoveryAdapterError, 'already consumed'):
            owner.call_provider_once(
                lambda _: self.fail('provider called twice'),
                baseline_deployment_ids=['old'], ambiguity_evidence_digest='f' * 64)

    def test_canonical_fence_authenticates_run_and_exact_mutex_source(self):
        from types import SimpleNamespace
        env = dict(GITHUB_REPOSITORY='MYLEGND/masterapp', GITHUB_TOKEN='synthetic',
                   GITHUB_RUN_ID='30', GITHUB_RUN_ATTEMPT='1', GITHUB_SHA='c' * 40)
        source = 'name: x\nconcurrency:\n  group: legend-approved-release\n  cancel-in-progress: false\njobs: {}\n'
        authority = SimpleNamespace(
            FAST_TRACK_RELEASE_WORKFLOW='legend-fast-track-ui.yml',
            api_get=lambda repo, path, token: {
                'path': '.github/workflows/legend-fast-track-ui.yml',
                'head_sha': 'c' * 40, 'status': 'in_progress', 'run_attempt': 1,
                'head_repository': {'full_name': 'MYLEGND/masterapp'}},
            _release_history_source=lambda repo, sha, path, token: source)
        fence = self.adapter.CanonicalPublisherFence(
            authority=authority, environment=env)
        self.assertEqual('legend-approved-release', fence.verify()['group'])
        authority._release_history_source = lambda *args: source.replace(
            'legend-approved-release', 'other')
        with self.assertRaisesRegex(self.adapter.RecoveryAdapterError, 'mutex'):
            fence.verify()

    def test_authenticated_evidence_is_read_before_digest_binding_and_semantics(self):
        admission = self.admission()
        terminal = {'target': 'protect', 'originalOperationId': '1' * 64,
                    'terminalNonresumable': True, 'baselineDeploymentIds': ['old'],
                    'producingRun': 1}
        no_sql = {'pendingCount': 0, 'mutationActivity': 'settled',
                  'sqlExecutionAuthorized': False, 'producingRun': 1}
        rollback = {'target': 'protect', 'recoverable': True,
                    'packageDigest': '3' * 64, 'producingRun': 1}
        review = {'accepted': True, 'controlRevision': 'c' * 40, 'producingRun': 1}
        admission.update(
            terminalNonresumableEvidenceDigest=self.adapter._canonical_digest(terminal),
            noSqlEvidenceDigest=self.adapter._canonical_digest(no_sql),
            rollbackEvidenceDigest=self.adapter._canonical_digest(rollback),
            independentReviewDigest=self.adapter._canonical_digest(review))
        records = dict(terminalNonresumable=terminal, noSql=no_sql,
                       rollback=rollback, independentReview=review)
        class Reader:
            def read(self, ref, *, allowed_workflows):
                return {'record': records[ref.artifact_name],
                        'run': {'path': next(iter(allowed_workflows))},
                        'artifact': {'id': ref.artifact_id}}
        refs = {key: {'runId': 1, 'artifactId': i + 1, 'artifactName': key,
                      'artifactDigest': 'sha256:' + str(i + 1) * 64}
                for i, key in enumerate(records)}
        value = self.adapter.authenticate_recovery_evidence(
            admission, refs, reader=Reader())
        self.assertTrue(value.terminal_nonresumable['terminalNonresumable'])
        records['noSql'] = dict(no_sql, pendingCount=1)
        with self.assertRaises(self.adapter.RecoveryAdapterError):
            self.adapter.authenticate_recovery_evidence(admission, refs, reader=Reader())

    def test_missing_live_evidence_blocks_adapter_preparation_without_store_or_provider_write(self):
        admission = self.admission()
        admission['admissionId'] = self.adapter.PRIMITIVE.recovery_admission_id(admission)
        class Fence(self.Fence):
            repository = 'MYLEGND/masterapp'
            token = 'synthetic'
        from types import SimpleNamespace
        authority = SimpleNamespace(
            release_operation_history=lambda *args, **kwargs: self.original_intent())
        with self.assertRaises(self.adapter.MissingProductionEvidence):
            self.adapter.prepare_canonical_adapter(
                admission=admission, original_revision='d' * 40,
                current_run=30, current_attempt=1, evidence_refs={},
                fence=Fence(), authority=authority)


if __name__ == '__main__':
    unittest.main()
