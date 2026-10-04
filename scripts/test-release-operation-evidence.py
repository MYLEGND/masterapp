#!/usr/bin/env python3
import importlib.util
import json
from pathlib import Path
import tempfile
import subprocess
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('journal', Path(__file__).with_name('release-operation-evidence.py'))
journal = importlib.util.module_from_spec(spec)
spec.loader.exec_module(journal)


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

    def test_sdk_error_payload_not_exposed(self):
        result = type('Result', (), dict(returncode=1, stdout='secret signed URL', stderr='secret token'))()
        with patch.object(journal.subprocess, 'run', return_value=result):
            with self.assertRaisesRegex(RuntimeError, 'unavailable') as failure:
                self.make(publisher=None)._publish('name', {})
            self.assertNotIn('secret', str(failure.exception))


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

    def legacy_history(self):
        original = self.api
        def api(repo, path, token):
            if path == 'actions/runs/9/artifacts?per_page=100':
                return dict(artifacts=self.legacy_artifacts, total_count=len(self.legacy_artifacts))
            return original(repo, path, token)
        def source(repo, revision, path, token):
            return self.old_workflow if path.endswith('.yml') else self.old_deployer
        with patch.object(self.authority, 'api_get', side_effect=api), \
                patch.object(self.authority, '_release_history_json', return_value=self.state), \
                patch.object(self.authority, '_release_history_source', side_effect=source):
            return self.authority.release_operation_history('owner/repo', 'b' * 64, 'a' * 40, 'portal', 10, 1, 'placeholder')

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
        with patch.object(self.authority.subprocess, 'run',
                return_value=subprocess.CompletedProcess([], 0, log, '')) as download:
            self.assertIsNone(self.legacy_history())
        self.assertEqual(180, download.call_args.kwargs['timeout'])

    def test_modern_title_binds_checkout_when_retained_log_is_unavailable(self):
        self.legacy_fixture()
        self.run['head_sha'] = 'd' * 40
        self.run['display_title'] = 'LEGEND release pr=1 candidate=' + 'a' * 40 + ' authority=' + 'd' * 40
        self.job['id'] = 123
        self.job['steps'].insert(0, dict(name='Run actions/checkout@v4', status='completed', conclusion='success'))
        self.legacy_artifacts = self.legacy_artifacts[:1]
        with patch.object(self.authority.subprocess, 'run', side_effect=subprocess.TimeoutExpired(['gh'], 180)):
            self.assertIsNone(self.legacy_history())

    def test_modern_title_fallback_rejects_authority_not_equal_to_workflow_head(self):
        self.legacy_fixture()
        self.run['head_sha'] = 'e' * 40
        self.run['display_title'] = 'LEGEND release pr=1 candidate=' + 'a' * 40 + ' authority=' + 'd' * 40
        self.job['id'] = 123
        self.job['steps'].insert(0, dict(name='Run actions/checkout@v4', status='completed', conclusion='success'))
        self.legacy_artifacts = self.legacy_artifacts[:1]
        with patch.object(self.authority.subprocess, 'run', side_effect=subprocess.TimeoutExpired(['gh'], 180)):
            with self.assertRaisesRegex(RuntimeError, 'Historical checkout log unavailable'):
                self.legacy_history()

    def test_checkout_log_rejects_conflicting_package_and_missing_head(self):
        job = dict(id=123, steps=[dict(name='Run actions/checkout@v4', conclusion='success')])
        valid = '  RELEASE_SHA: ' + 'd' * 40 + '\n  APPLICATION_RELEASE_SHA: ' + 'f' * 40 + '\n'
        valid += '[command]/usr/bin/git log -1 --format=%H\n' + 'd' * 40 + '\n'
        for log in (valid.replace('f' * 40, 'a' * 40), valid.split('[command]')[0],
                    valid + '  RELEASE_SHA: ' + 'c' * 40 + '\n'):
            with self.subTest(log=log), patch.object(self.authority.subprocess, 'run',
                    return_value=subprocess.CompletedProcess([], 0, log, '')):
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
        target = next(row for row in self.owner['steps'] if row['name'] == 'Publish canonical target (client)')
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


if __name__ == '__main__':
    unittest.main()
