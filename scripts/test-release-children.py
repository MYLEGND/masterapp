#!/usr/bin/env python3
import base64
import hashlib
import hmac
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


def load(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


journal_module = load('release-operation-evidence')
migration = load('release-migration')
cloud = load('deploy-founder-cloudflare')
router = load('release-router')


class ReleaseChildTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name)
        self.bundle = self.path / 'bundle'
        self.bundle.write_bytes(b'validated immutable bundle')
        self.records = {}
        self.published = []
        self.env = dict(APPLICATION_RELEASE_SHA='a' * 40, RELEASE_SHA='b' * 40,
                        GITHUB_REPOSITORY='owner/repo', GITHUB_RUN_ID='8', GITHUB_RUN_ATTEMPT='1', GH_TOKEN='fixture')
        self.authority = type('Authority', (), {})()
        self.authority.direct_child_identity = lambda child, revision, material: material
        self.authority.direct_child_operation_identity = lambda child, revision, material: material
        self.authority.release_child_history = lambda repo, child, identity, token, phase: self.records.get((phase, identity))
        self.authority.release_child_first_write_proven = lambda *args, **kwargs: True

    def publish(self, name, record):
        self.records[(record['phase'], record['dependencyIdentity'])] = record
        self.published.append(name)
        return {'artifactId': 1}

    def journal(self, child, material):
        return journal_module.ChildJournal(child, material, authority=self.authority,
            publisher=self.publish, environment=self.env)

    def schema(self, ready):
        return dict(ready=ready, schemaIdentity='c' * 64)

    def test_green_migration_survives_sibling_failure_and_new_head_without_bundle_replay(self):
        states = iter([self.schema(False), self.schema(True)])
        calls = []
        self.assertEqual(migration.reconcile(self.bundle, self.path, 'opaque',
            observer=lambda *args: next(states), journal_factory=self.journal,
            execute=lambda *args: calls.append('migration')), 'applied')
        self.env['GITHUB_RUN_ID'] = '9'
        self.env['APPLICATION_RELEASE_SHA'] = 'd' * 40
        self.assertEqual(migration.reconcile(self.bundle, self.path, 'opaque',
            observer=lambda *args: self.schema(True), journal_factory=self.journal,
            execute=lambda *args: calls.append('replay')), 'preserved')
        self.assertEqual(calls, ['migration'])
        self.assertEqual(len(self.published), 2)

    def test_ambiguous_migration_reconciles_ready_without_resubmission(self):
        def interrupted(*args):
            raise TimeoutError()
        with self.assertRaisesRegex(RuntimeError, "Migration stage unresolved"):
            migration.reconcile(self.bundle, self.path, 'opaque', observer=lambda *args: self.schema(False),
                                journal_factory=self.journal, execute=interrupted)
        self.assertEqual(migration.reconcile(self.bundle, self.path, 'opaque',
            observer=lambda *args: self.schema(True), journal_factory=self.journal,
            execute=lambda *args: self.fail('Repeated migration')), 'preserved')

    def test_ambiguous_migration_still_pending_never_blindly_replays(self):
        with self.assertRaisesRegex(RuntimeError, "Migration stage unresolved"):
            migration.reconcile(self.bundle, self.path, 'opaque', observer=lambda *args: self.schema(False),
                journal_factory=self.journal, execute=lambda *args: (_ for _ in ()).throw(TimeoutError()))
        with self.assertRaisesRegex(RuntimeError, 'mutation-admission'):
            migration.reconcile(self.bundle, self.path, 'opaque', observer=lambda *args: self.schema(False),
                journal_factory=self.journal, execute=lambda *args: self.fail('Repeated migration'))

    def test_migration_stage_errors_never_disclose_provider_details(self):
        for stage in ('schema-observation', 'child-history', 'mutation-admission', 'bundle-execution', 'schema-verification', 'success-receipt'):
            with self.assertRaises(RuntimeError) as caught:
                migration.migration_stage(stage, lambda: (_ for _ in ()).throw(RuntimeError('private-provider-payload')))
            self.assertEqual('Migration stage unresolved: ' + stage, str(caught.exception))
            self.assertTrue(caught.exception.__suppress_context__)

    def test_admission_diagnostic_names_prior_failed_run_without_leaking_provider(self):
        from unittest.mock import patch as mock_patch
        raw = ('Release child failed prepublication but the no-write proof was not authenticated '
               '[run=37765986494;attempt=1]')
        with mock_patch('builtins.print') as output:
            with self.assertRaisesRegex(RuntimeError, 'mutation-admission'):
                migration.migration_stage('mutation-admission',
                    lambda: (_ for _ in ()).throw(RuntimeError(raw)))
        output.assert_called_once_with(
            'LEGEND_MIGRATION_ADMISSION_DIAGNOSTIC:'
            'HISTORICAL_PREWRITE_PROOF_REJECTED:run=37765986494:attempt=1', flush=True)

    def test_admission_ambiguity_and_provider_payload_are_fail_closed(self):
        from unittest.mock import patch as mock_patch
        for reason, code in (
            ('Release child physical operation already entered; reconcile without replay',
             'PHYSICAL_OPERATION_ALREADY_ENTERED'),
            ('Release child prior partition operation unresolved; no replay authorized',
             'PRIOR_PARTITION_UNRESOLVED'),
            ('Retained release child requires read-only reconciliation; no mutation replay authorized',
             'RETAINED_INTENT_OR_SUCCESS'),
        ):
            with self.subTest(code=code), mock_patch('builtins.print') as output:
                with self.assertRaisesRegex(RuntimeError, 'mutation-admission'):
                    migration.migration_stage('mutation-admission',
                        lambda: (_ for _ in ()).throw(RuntimeError(reason)))
                output.assert_called_once_with(
                    'LEGEND_MIGRATION_ADMISSION_DIAGNOSTIC:' + code, flush=True)
        with mock_patch('builtins.print') as output:
            with self.assertRaisesRegex(RuntimeError, 'mutation-admission'):
                migration.migration_stage('mutation-admission',
                    lambda: (_ for _ in ()).throw(RuntimeError(
                        'Release child physical operation already entered; '
                        'reconcile without replay [run=123;attempt=1] Password=secret')))
        output.assert_called_once_with(
            'LEGEND_MIGRATION_ADMISSION_DIAGNOSTIC:UNCLASSIFIED_DENIAL',
            flush=True)

    def test_schema_observation_failure_never_authorizes_migration(self):
        with self.assertRaisesRegex(RuntimeError, "Migration stage unresolved"):
            migration.reconcile(self.bundle, self.path, 'opaque',
                observer=lambda *args: (_ for _ in ()).throw(TimeoutError()), journal_factory=self.journal,
                execute=lambda *args: self.fail('Write despite unavailable proof'))
        self.assertEqual(self.records, {})

    def test_cloudflare_split_traffic_cannot_be_claimed_as_exact_baseline(self):
        with patch.object(cloud, 'cloudflare', return_value={'deployments': [{'versions': [
                {'version_id': 'one', 'percentage': 50}, {'version_id': 'two', 'percentage': 50}]}]}):
            with self.assertRaisesRegex(RuntimeError, 'not_exact'):
                cloud.current_worker_version('account', 'worker')

    def test_cloudflare_exact_version_is_preserved(self):
        with patch.object(cloud, 'cloudflare', return_value={'deployments': [{'versions': [
                {'version_id': 'one', 'percentage': 100}]}]}):
            self.assertEqual(cloud.current_worker_version('account', 'worker'), 'one')

    def callback_proof_response(self, callback_key, key_id, url):
        class Response:
            def __enter__(self): return self
            def __exit__(self, *args): return False
        def open_request(request, timeout=30):
            body = json.loads(request.data)
            nonce = body['callbackProofNonce']
            message = '\n'.join(('legend-callback-equivalence.v1', key_id, url, nonce))
            signature = hmac.new(base64.b64decode(callback_key), message.encode(), hashlib.sha256).hexdigest()
            response = Response()
            response.read = lambda: json.dumps({'callbackProof': {
                'version': 'legend-callback-equivalence.v1',
                'keyId': key_id,
                'url': url,
                'nonce': nonce,
                'signature': signature,
            }}).encode()
            return response
        return open_request

    def test_founder_callback_equivalence_proof_verifies_without_persisting_secret(self):
        service_key = base64.b64encode(b's' * 32).decode()
        callback_key = base64.b64encode(b'c' * 32).decode()
        callback_url = 'https://portal.example.test/api/founder/legend-ai/cloudflare-tools'
        with patch.object(cloud.urllib.request, 'urlopen',
                          side_effect=self.callback_proof_response(callback_key, 'callback-v1', callback_url)):
            self.assertTrue(cloud.verify_callback_equivalence(
                'https://worker.example.workers.dev/v1/legend/respond',
                'account-1', 'tenant-1', 'founder-1',
                'service-v1', service_key,
                'callback-v1', callback_key, callback_url))

    def test_founder_callback_equivalence_rejects_forged_worker_proof(self):
        service_key = base64.b64encode(b's' * 32).decode()
        callback_key = base64.b64encode(b'c' * 32).decode()
        wrong_key = base64.b64encode(b'x' * 32).decode()
        callback_url = 'https://portal.example.test/api/founder/legend-ai/cloudflare-tools'
        with patch.object(cloud.urllib.request, 'urlopen',
                          side_effect=self.callback_proof_response(wrong_key, 'callback-v1', callback_url)):
            with self.assertRaisesRegex(RuntimeError, 'proof_invalid'):
                cloud.verify_callback_equivalence(
                    'https://worker.example.workers.dev/v1/legend/respond',
                    'account-1', 'tenant-1', 'founder-1',
                    'service-v1', service_key,
                    'callback-v1', callback_key, callback_url)

    def test_router_sibling_live_failure_preserves_exact_publication(self):
        first = self.journal('routing-cloudflare', 'e' * 64)
        self.assertEqual(router.prepare(first, 'old'), 'publish')
        first.record_success({'providerVersion': 'new'})
        self.env['GITHUB_RUN_ID'] = '9'
        self.env['RELEASE_SHA'] = 'd' * 40
        self.assertEqual(router.prepare(self.journal('routing-cloudflare', 'e' * 64), 'new'), 'preserve')
        self.assertEqual(len(self.published), 2)

    def test_router_third_provider_version_fails_closed(self):
        first = self.journal('routing-cloudflare', 'e' * 64)
        router.prepare(first, 'old')
        first.record_success({'providerVersion': 'new'})
        with self.assertRaisesRegex(RuntimeError, 'differs'):
            router.prepare(self.journal('routing-cloudflare', 'e' * 64), 'third')

    def test_router_ambiguous_submission_has_no_retry_write(self):
        router.prepare(self.journal('routing-cloudflare', 'e' * 64), 'old')
        with self.assertRaisesRegex(RuntimeError, 'ambiguous'):
            router.prepare(self.journal('routing-cloudflare', 'e' * 64), 'old')
        self.assertEqual(len(self.published), 1)

    def test_configuration_timeout_current_readback_can_complete_without_replay(self):
        first = self.journal('shared-config', 'e' * 64)
        first.before_mutation({})
        # The setting write succeeded while the caller lost the response.
        resumed = self.journal('shared-config', 'e' * 64)
        resumed.record_success({})  # Called only after the owner readback matches.
        with self.assertRaisesRegex(RuntimeError, 'no mutation replay'):
            resumed.before_mutation({})
        self.assertEqual(len(self.published), 2)

    def test_history_failure_never_publishes_intent_or_mutates(self):
        self.authority.release_child_first_write_proven = lambda *args, **kwargs: (_ for _ in ()).throw(RuntimeError('unknown'))
        with self.assertRaisesRegex(RuntimeError, 'unknown'):
            self.journal('migrations', 'e' * 64).before_mutation({})
        self.assertEqual(self.published, [])

    def config(self):
        return journal_module.ConfigurationJournal('shared-config', 'e' * 64,
            authority=self.authority, publisher=self.publish, environment=self.env)

    def test_configuration_unchanged_after_terminal_success_never_starts_successor(self):
        self.config().record_success({})
        self.env['RELEASE_SHA'] = 'f' * 40
        self.config().record_success({})
        self.assertEqual(len(self.published), 1)

    def test_configuration_fresh_desired_drift_has_one_successor_after_terminal_success(self):
        self.config().record_success({})
        changed = self.config()
        changed.before_mutation({})
        changed.record_success({})
        self.config().record_success({})
        self.assertEqual(len(self.published), 3)
        self.assertNotEqual(self.published[0].split('-')[-1], self.published[1].split('-')[-1])

    def test_configuration_ambiguous_successor_cannot_advance_or_replay(self):
        self.config().record_success({})
        self.config().before_mutation({})
        self.env['RELEASE_SHA'] = 'f' * 40
        with self.assertRaisesRegex(RuntimeError, 'no mutation replay'):
            self.config().before_mutation({})
        self.assertEqual(len(self.published), 2)

    def test_configuration_ambiguous_successor_readback_match_completes_without_replay(self):
        self.config().record_success({})
        self.config().before_mutation({})
        self.config().record_success({})
        self.assertEqual(len(self.published), 3)


class HistoricalMigrationPrewriteProofTests(unittest.TestCase):
    def setUp(self):
        self.authority = load('validation-resume')
        self.run_id = 37765986494
        self.attempt = 1
        self.run = dict(
            id=self.run_id, run_attempt=1, status='completed',
            conclusion='failure', head_sha='901a0d86567567cb54c7640127943db632fe6f28',
            head_branch=self.authority.TRUSTED_PR_BASE,
            event='workflow_dispatch',
            path='.github/workflows/' + self.authority.DIRECT_RELEASE_WORKFLOW,
            head_repository={'full_name': 'owner/repo'},
            display_title='LEGEND release pr=525 candidate=' + 'a' * 40
                + ' authority=901a0d86567567cb54c7640127943db632fe6f28',
        )
        self.step = dict(
            name='Synchronize canonical pre-publication resource lanes',
            status='completed', conclusion='failure',
            started_at='2026-10-08T11:00:10Z',
            completed_at='2026-10-08T11:00:15Z',
        )
        self.job = dict(
            id=113276473989, run_attempt=self.attempt, status='completed', conclusion='failure',
            steps=[
                self.step,
                dict(name='Submit canonical selected targets in parallel',
                     status='completed', conclusion='skipped'),
                dict(name='Run independent auxiliary release fanout',
                     status='completed', conclusion='skipped'),
            ],
        )
        self.receipts = [dict(
            name=f'legend-release-step-state-{"b" * 40}-{self.run_id}-1',
            expired=False, workflow_run={"id": self.run_id},
        )]
        self.message = ('2026-10-08T11:00:15.7025934Z '
            'LEGEND_PREPUBLICATION_MIGRATION:Migration stage unresolved: mutation-admission')
        self.sources = {
            path: (Path(__file__).resolve().parents[1] / path).read_text()
            for path in (
                '.github/workflows/' + self.authority.DIRECT_RELEASE_WORKFLOW,
                'scripts/release-prepublication.py',
                'scripts/release-migration.py',
            )
        }

    def proven(self):
        with (patch.object(self.authority, '_release_history_source',
                           side_effect=lambda repo, sha, path, token: self.sources[path]),
              patch.object(self.authority, '_release_job_log', return_value=self.message),
              patch.object(self.authority, 'api_get',
                           return_value={'artifacts': self.receipts,
                                         'total_count': len(self.receipts)})):
            return self.authority._historical_migration_prewrite_proven(
                'owner/repo', self.run, self.job, self.step, self.attempt, 'fixture')

    def test_exact_failed_admission_with_authenticated_no_write_evidence(self):
        self.assertTrue(self.proven())
        self.message = ('2026-10-08T11:00:15.7025934Z '
            'LEGEND_PREPUBLICATION_MIGRATION:'
            'Database contains applied migration history absent from validated bundle')
        self.assertTrue(self.proven())

    def test_unknown_writer_generation_cannot_claim_no_write(self):
        self.sources['scripts/release-migration.py'] += '\\n# changed writer\\n'
        self.assertFalse(self.proven())

    def test_started_publish_or_auxiliary_cannot_claim_no_write(self):
        for step in self.job['steps'][1:]:
            step['conclusion'] = 'success'
            self.assertFalse(self.proven())
            step['conclusion'] = 'skipped'

    def test_missing_or_ambiguous_artifact_never_authorizes_replay(self):
        original = self.receipts.pop()
        self.assertFalse(self.proven())
        self.receipts.append(original)
        for name in ('legend-release-child-intent-', 'legend-release-child-success-',
                     'legend-release-operation-intent-'):
            self.receipts.append(dict(name=name + 'c' * 64, expired=False,
                                      workflow_run={'id': self.run_id}))
            self.assertFalse(self.proven())
            self.receipts.pop()
        self.receipts[0]['expired'] = True
        self.assertFalse(self.proven())

    def test_untrusted_history_log_and_attempts_are_denied(self):
        self.message = ('2026-10-08T11:00:15.7025934Z '
                        'LEGEND_PREPUBLICATION_MIGRATION:UNKNOWN_FAILURE')
        self.assertFalse(self.proven())
        self.message = ('2026-10-08T11:00:15.7025934Z '
            'LEGEND_PREPUBLICATION_MIGRATION:Migration stage unresolved: mutation-admission')
        self.run['head_repository'] = {'full_name': 'attacker/repo'}
        self.assertFalse(self.proven())
        self.run['head_repository'] = {'full_name': 'owner/repo'}
        self.run['run_attempt'] = 3
        self.attempt = 4
        self.assertFalse(self.proven())

    def test_attempt_specific_history_must_use_original_attempt(self):
        # A retried workflow may have an earlier failure with independent
        # job evidence. Do not read the latest attempt's receipt as its proof.
        self.run['run_attempt'] = 2
        self.assertTrue(self.proven())
        self.receipts[0]['name'] = (
            f'legend-release-step-state-{"b" * 40}-{self.run_id}-2')
        self.assertFalse(self.proven())
        self.receipts[0]['name'] = (
            f'legend-release-step-state-{"b" * 40}-{self.run_id}-1')
        self.job['run_attempt'] = 2
        self.assertFalse(self.proven())

    def test_attempt_artifact_provenance_must_match_producer(self):
        self.receipts[0]['workflow_run'] = {'id': 99}
        self.assertFalse(self.proven())

    def test_time_and_terminal_step_are_required(self):
        self.step['started_at'] = '2026-10-08T11:01:00Z'
        self.assertFalse(self.proven())
        self.step['started_at'] = '2026-10-08T11:00:10Z'
        self.step['conclusion'] = 'success'
        self.assertFalse(self.proven())


class ChildHistorySafetyTests(unittest.TestCase):
    def setUp(self):
        self.authority = load('validation-resume')
        self.child = 'migrations'
        self.identity = 'a' * 64
        self.material = 'b' * 64
        self.run = dict(id=7, head_sha='c' * 40, head_branch=self.authority.TRUSTED_PR_BASE,
            event='workflow_dispatch', path='.github/workflows/' + self.authority.DIRECT_RELEASE_WORKFLOW,
            head_repository={'full_name': 'owner/repo'}, run_attempt=1, status='completed',
            display_title='LEGEND release pr=1 candidate=' + 'd' * 40 + ' authority=' + 'e' * 40)
        self.step = dict(name=self.authority.DIRECT_RELEASE_CHILDREN[self.child]['step'],
            status='completed', conclusion='success')
        self.artifacts = []
        self.records = {}
        self.total = 1

    def api(self, repo, path, token):
        if path.startswith('actions/runs?branch=legend%2Fapproved-changes&event=workflow_dispatch&per_page=100&page='):
            return dict(workflow_runs=[self.run], total_count=self.total)
        if '/jobs?' in path:
            return dict(jobs=[dict(name='release', status='completed', conclusion='failure', steps=[self.step])], total_count=1)
        if '/artifacts?' in path:
            return dict(artifacts=self.artifacts, total_count=len(self.artifacts))
        if path.startswith('compare/'):
            return dict(status='ahead')
        raise AssertionError(path)

    def check(self, current_run=9, attempt=1):
        with patch.object(self.authority, 'api_get', side_effect=self.api), \
             patch.object(self.authority, '_release_history_json', side_effect=lambda repo, run, artifact, file: self.records[artifact['name']]), \
             patch.object(self.authority, 'direct_child_operation_identity', side_effect=lambda child, revision, material: material):
            return self.authority.release_child_first_write_proven('owner/repo', self.child,
                self.identity, self.material, current_run, attempt, 'fixture')

    def retained(self, operation, attempt=1):
        name = 'legend-release-child-intent-' + operation
        self.artifacts.append(dict(name=name, id=11, expired=False))
        self.records[name] = dict(schemaVersion=1, child=self.child, dependencyIdentity=operation,
            materialIdentity=operation, partitionIdentity=operation, executionAuthority='e' * 40, phase='intent', producingRun=7,
            producingAttempt=attempt)

    def test_repository_history_filters_out_unrelated_workflows(self):
        self.run['path'] = '.github/workflows/unrelated-workflow.yml'
        self.assertTrue(self.check())  # No trusted release child can enter via another workflow.

    def test_attested_successful_prepublication_noop_is_not_a_sql_write(self):
        import subprocess
        head = '0f8eb4026a36b191a837f06a66e0c16ba78e584e'
        run_id = 37630867225
        paths = (
            '.github/workflows/' + self.authority.DIRECT_RELEASE_WORKFLOW,
            'scripts/release-prepublication.py',
        )
        sources = {path: subprocess.check_output(
            ['git', 'show', head + ':' + path], text=True) for path in paths}
        run = dict(self.run, id=run_id, head_sha=head,
                   status='completed', conclusion='success', run_attempt=2)
        job = dict(id=112827564296, name='release', status='completed',
                   conclusion='failure')
        step = dict(self.step, status='completed', conclusion='success',
                    started_at='2026-10-07T13:57:25Z',
                    completed_at='2026-10-07T13:57:42Z')
        names = [
            'legend-release-admission-' + 'a' * 64,
            f'legend-release-step-state-{"d" * 40}-{run_id}-1',
            f'legend-release-step-state-{"d" * 40}-{run_id}-2',
            *(f'diagnostics-rollback-{name}-{head}'
              for name in ('portal', 'client', 'protect', 'parfait', 'website')),
        ]
        artifacts = [dict(name=name, expired=False, workflow_run=dict(id=run_id))
                     for name in names]
        marker = ('{"changedTargets": [], "configuredTargets": ["masterapp-portal"], '
                  '"migrationStatus": "not-applicable"}')
        log = '2026-10-07T13:57:42.6797561Z ' + marker + '\n'
        def read(repo, path, token):
            self.assertEqual(path, f'actions/runs/{run_id}/artifacts?per_page=100')
            return dict(artifacts=artifacts, total_count=len(artifacts))
        with patch.object(self.authority, '_release_history_source',
                          side_effect=lambda repo, revision, path, token: sources[path]), \
             patch.object(self.authority, 'api_get', side_effect=read), \
             patch.object(self.authority, '_release_job_log', return_value=log) as original_log:
            check = lambda: self.authority._attested_migration_noop_success(
                'owner/repo', run, job, step, 1, 'token')
            self.assertTrue(check())
            original_log.assert_called_with('owner/repo', job['id'], 'token')
            step['conclusion'] = 'failure'
            self.assertFalse(check())
            step['conclusion'] = 'success'
            sources[paths[1]] += '\n# modified'
            self.assertFalse(check())
            sources[paths[1]] = subprocess.check_output(
                ['git', 'show', head + ':' + paths[1]], text=True)
            artifacts.append(dict(name='legend-release-child-intent-' + 'f' * 64,
                                  expired=False, workflow_run=dict(id=run_id)))
            self.assertFalse(check())
            artifacts.pop()
            artifacts[0]['expired'] = True
            self.assertFalse(check())
            artifacts[0]['expired'] = False
            original_step_receipt = artifacts.pop(1)
            self.assertFalse(check())
            artifacts.insert(1, original_step_receipt)
            with patch.object(self.authority, '_release_job_log',
                              return_value='2026-10-07T13:57:42.6797561Z {"migrationStatus":"reconciled"}'):
                self.assertFalse(check())
            with patch.object(self.authority, '_release_job_log',
                              return_value='2026-10-07T13:57:12.6797561Z ' + marker):
                self.assertFalse(check())
            with patch.object(self.authority, '_release_job_log',
                              return_value=log + log):
                self.assertFalse(check())

    def test_old_wrapper_noop_does_not_require_future_rollback_format(self):
        import subprocess
        cases = (
            ('be520caaf351e88d409ca77b1810f5ffc98515e0', 37556061199,
             112590269307, '2026-10-07T01:45:55.6778670Z', 'success', 2),
            ('e59606b8d15c872631f3cb1a79b95573c2457ee2', 37527468541,
             112490642070, '2026-10-06T20:45:06.3502002Z', 'failure', 1),
        )
        for head, run_id, job_id, stamp, outcome, attempt in cases:
            with self.subTest(run_id=run_id):
                paths = (
                    '.github/workflows/' + self.authority.DIRECT_RELEASE_WORKFLOW,
                    'scripts/release-prepublication.py',
                )
                sources = {
                    p: subprocess.check_output(['git', 'show', head + ':' + p], text=True)
                    for p in paths
                }
                run = dict(self.run, id=run_id, head_sha=head, status='completed',
                           conclusion=outcome, run_attempt=attempt)
                job = dict(id=job_id, status='completed',
                           conclusion=outcome)
                step = dict(self.step, status='completed', conclusion='success',
                            started_at=stamp[:19] + 'Z',
                            completed_at=stamp[:19] + 'Z')
                names = [
                    'legend-release-admission-' + 'a' * 64,
                    f'legend-release-step-state-{"d"*40}-{run_id}-{attempt}',
                ]
                # Neither original protected release had five app rollbacks;
                # they remain irrelevant to the proof that EF never executed.
                artifacts = [
                    dict(name=n, expired=False, workflow_run=dict(id=run_id))
                    for n in names
                ]
                log = (stamp + ' {"changedTargets": [], "configuredTargets": '
                       '["masterapp-portal"], "migrationStatus": "not-applicable"}\n')
                def inventory(repo, path, token):
                    self.assertEqual(path, f'actions/runs/{run_id}/artifacts?per_page=100')
                    return dict(artifacts=artifacts, total_count=len(artifacts))
                with patch.object(self.authority, '_release_history_source',
                                  side_effect=lambda repo, sha, p, token: sources[p]), \
                     patch.object(self.authority, 'api_get', side_effect=inventory), \
                     patch.object(self.authority, '_release_job_log', return_value=log):
                    verify = lambda: self.authority._attested_migration_noop_success(
                        'owner/repo', run, job, step, attempt, 'token')
                    self.assertTrue(verify())
                    sources[paths[0]] += '\n# unrelated workflow change'
                    self.assertFalse(verify())
                    sources[paths[0]] = subprocess.check_output(
                        ['git', 'show', head + ':' + paths[0]], text=True)
                    artifacts[0]['expired'] = True
                    self.assertFalse(verify())

    def test_second_attempt_prewrite_history_uses_original_attempt_receipt(self):
        import subprocess
        head = '261fd32ba559d6f2bab0324b538068ee7d7541d7'
        run_id = 37690711809
        sources = {
            path: subprocess.check_output(['git', 'show', head + ':' + path], text=True)
            for path in (
                '.github/workflows/' + self.authority.DIRECT_RELEASE_WORKFLOW,
                'scripts/release-migration.py',
                'scripts/release-prepublication.py',
                'scripts/release-operation-evidence.py',
            )
        }
        run = dict(self.run, id=run_id, head_sha=head, status='completed',
                   conclusion='failure', run_attempt=2)
        job = dict(id=113033191016, status='completed', conclusion='failure',
                   run_attempt=2)
        step = dict(self.step, status='completed', conclusion='failure')
        artifacts = [dict(name=name, expired=False, workflow_run=dict(id=run_id))
                     for name in (
                         'legend-release-admission-' + 'a' * 64,
                         f'legend-release-step-state-{"d"*40}-{run_id}-1',
                         f'legend-release-step-state-{"d"*40}-{run_id}-2',
                         'legend-release-transaction-plan-' + 'b' * 64,
                         *(f'diagnostics-rollback-{app}-{head}'
                           for app in ('portal', 'client', 'protect', 'parfait', 'website')),
                     )]
        marker = ('2026-10-07T21:52:09.2500317Z '
                  'Schema probe detected migration schema drift; '
                  'preserve prior evidence and reconcile without replay.\n')
        def inventory(repo, path, token):
            self.assertEqual(path, f'actions/runs/{run_id}/artifacts?per_page=100')
            return dict(artifacts=artifacts, total_count=len(artifacts))
        with patch.object(self.authority, '_release_history_source',
                          side_effect=lambda repo, rev, path, token: sources[path]), \
             patch.object(self.authority, 'api_get', side_effect=inventory), \
             patch.object(self.authority, '_release_job_log', return_value=marker):
            proof = lambda attempt: self.authority._attested_migration_prewrite_failure(
                'owner/repo', run, job, step, attempt, 'token')
            self.assertTrue(proof(2))
            self.assertFalse(proof(1))  # Never borrow another attempt's job.
            artifacts[2]['expired'] = True
            self.assertFalse(proof(2))
            artifacts[2]['expired'] = False
            artifacts.pop(2)
            self.assertFalse(proof(2))  # Missing original attempt receipt.

    def test_historical_success_without_noop_proof_still_blocks_sql_replay(self):
        self.run.update(status='completed', conclusion='success')
        with patch.object(self.authority, '_attested_migration_noop_success',
                          return_value=True) as proof:
            self.assertTrue(self.check())
            proof.assert_called_once()
        with patch.object(self.authority, '_attested_migration_noop_success',
                          return_value=False):
            with self.assertRaisesRegex(RuntimeError, 'missing intent'):
                self.check()

    def test_failed_started_direct_release_still_requires_original_child_intent(self):
        self.step['conclusion'] = 'failure'
        with self.assertRaisesRegex(RuntimeError, 'no-write proof was not authenticated'):
            self.check()

    def test_only_authentic_prewrite_proof_can_skip_missing_migration_intent(self):
        self.step['conclusion'] = 'failure'
        with patch.object(self.authority, '_historical_migration_prewrite_proven',
                          return_value=True) as proof:
            self.assertTrue(self.check())
            proof.assert_called_once()
        with patch.object(self.authority, '_historical_migration_prewrite_proven',
                          return_value=False):
            with self.assertRaisesRegex(RuntimeError, 'no-write proof was not authenticated'):
                self.check()

    def test_exact_failed_prepublication_migration_is_prewrite_only_with_positive_proof(self):
        import subprocess
        head = 'fe115eb9f3f7ebd753307ec193f7cf4121ee9470'
        run_id = 37722599537
        historic = {}
        for path in (
            '.github/workflows/' + self.authority.DIRECT_RELEASE_WORKFLOW,
            'scripts/release-migration.py',
            'scripts/release-prepublication.py',
            'scripts/release-operation-evidence.py',
        ):
            historic[path] = subprocess.check_output(
                ['git', 'show', head + ':' + path], text=True)
        run = dict(self.run, id=run_id, head_sha=head, status='completed',
                   conclusion='failure', run_attempt=1,
                   head_repository={'full_name': 'owner/repo'})
        job = dict(id=113135272248, name='release', status='completed',
                   conclusion='failure')
        step = dict(name=self.authority.DIRECT_RELEASE_CHILDREN['migrations']['step'],
                    status='completed', conclusion='failure')
        application_revision = 'f4d546ccd50dd35ff20eafecb4d51e6fbdd0e54b'
        names = [
            'legend-release-admission-' + 'a' * 64,
            f'legend-release-step-state-{application_revision}-{run_id}-1',
            'legend-release-transaction-plan-' + 'b' * 64,
            *(f'diagnostics-rollback-{key}-{head}'
              for key in ('portal', 'client', 'protect', 'parfait', 'website')),
        ]
        artifacts = [
            dict(name=name, expired=False, workflow_run=dict(id=run_id))
            for name in names
        ]
        marker = 'LEGEND_PREPUBLICATION_MIGRATION:Migration stage unresolved: mutation-admission'
        evidence = '2026-10-08T03:38:48.4471578Z ' + marker + '\n'
        def read(repo, path, token):
            self.assertEqual(repo, 'owner/repo')
            self.assertEqual(path, f'actions/runs/{run_id}/artifacts?per_page=100')
            return dict(artifacts=artifacts, total_count=len(artifacts))
        with patch.object(self.authority, '_release_history_source',
                          side_effect=lambda repo, sha, path, token: historic[path]), \
             patch.object(self.authority, 'api_get', side_effect=read), \
             patch.object(self.authority, '_release_job_log', return_value=evidence) as log:
            self.assertTrue(self.authority._attested_migration_prewrite_failure(
                'owner/repo', run, job, step, 1, 'token'))
            log.assert_called_once_with('owner/repo', job['id'], 'token')
            # A changed migration owner, even with the same human-friendly
            # failure message, cannot discharge a historical first-write lease.
            original = historic['scripts/release-migration.py']
            historic['scripts/release-migration.py'] = original + '\n# altered'
            self.assertFalse(self.authority._attested_migration_prewrite_failure(
                'owner/repo', run, job, step, 1, 'token'))
            historic['scripts/release-migration.py'] = original
            artifacts.append(dict(name='legend-release-child-intent-' + 'f' * 64,
                                  expired=False, workflow_run=dict(id=run_id)))
            self.assertFalse(self.authority._attested_migration_prewrite_failure(
                'owner/repo', run, job, step, 1, 'token'))
            artifacts.pop()
            artifacts[0]['workflow_run']['id'] = run_id + 1
            self.assertFalse(self.authority._attested_migration_prewrite_failure(
                'owner/repo', run, job, step, 1, 'token'))
            artifacts[0]['workflow_run']['id'] = run_id
            artifacts.pop()
            self.assertFalse(self.authority._attested_migration_prewrite_failure(
                'owner/repo', run, job, step, 1, 'token'))
            artifacts.append(dict(name=names[-1], expired=False,
                                  workflow_run=dict(id=run_id)))
            step['conclusion'] = 'success'
            self.assertFalse(self.authority._attested_migration_prewrite_failure(
                'owner/repo', run, job, step, 1, 'token'))

    def test_older_observation_rejected_before_first_migration_write(self):
        import subprocess
        head = '924af2bdab12bbfc001ecc83769f03a8d7866b70'
        run_id = 37703022717
        historical_paths = (
            '.github/workflows/' + self.authority.DIRECT_RELEASE_WORKFLOW,
            'scripts/release-prepublication.py',
            'scripts/release-migration.py',
            'scripts/release-operation-evidence.py',
        )
        sources = {
            path: subprocess.check_output(
                ['git', 'show', head + ':' + path], text=True)
            for path in historical_paths
        }
        run = dict(self.run, id=run_id, head_sha=head, status='completed',
                   conclusion='failure', run_attempt=1)
        job = dict(id=113072142308, status='completed', conclusion='failure')
        step = dict(self.step, conclusion='failure')
        artifacts = [
            dict(name=name, expired=False, workflow_run=dict(id=run_id))
            for name in [
                'legend-release-admission-' + 'a' * 64,
                f'legend-release-step-state-{"d" * 40}-{run_id}-1',
                *(f'diagnostics-rollback-{key}-{head}'
                  for key in ('portal', 'client', 'protect', 'parfait', 'website')),
            ]
        ]
        def history(repo, path, token):
            self.assertEqual(path, f'actions/runs/{run_id}/artifacts?per_page=100')
            return dict(artifacts=artifacts, total_count=len(artifacts))
        message = ('LEGEND_PREPUBLICATION_MIGRATION:'
                   'Database contains applied migration history absent from validated bundle')
        evidence = '2026-10-07T23:45:35.8948393Z ' + message + '\n'
        with patch.object(self.authority, '_release_history_source',
                          side_effect=lambda repo, sha, path, token: sources[path]), \
             patch.object(self.authority, 'api_get', side_effect=history), \
             patch.object(self.authority, '_release_job_log', return_value=evidence):
            self.assertTrue(self.authority._attested_migration_prewrite_failure(
                'owner/repo', run, job, step, 1, 'token'))
            current = sources['scripts/release-migration.py']
            # No arbitrary combination of individually audited source
            # generations may earn the same write exemption.
            newer = subprocess.check_output(
                ['git', 'show',
                 'fe115eb9f3f7ebd753307ec193f7cf4121ee9470:scripts/release-migration.py'],
                text=True)
            sources['scripts/release-migration.py'] = newer
            self.assertFalse(self.authority._attested_migration_prewrite_failure(
                'owner/repo', run, job, step, 1, 'token'))
            sources['scripts/release-migration.py'] = current

    def test_original_prewrite_owners_reconcile_only_with_pinned_source_and_receipts(self):
        import subprocess
        cases = (
            ('a0bec5ab2e66a394b6ac64c937333cbe01c49809', 37674186895,
             'Migration child unresolved; preserve prior evidence and reconcile without replay.'),
            ('8b4a85f3be500fcd873afa70b72393264225f738', 37683353078,
             'Migration stage unresolved: schema-observation; preserve prior evidence and reconcile without replay.'),
            ('261fd32ba559d6f2bab0324b538068ee7d7541d7', 37690711809,
             'Schema probe detected migration schema drift; preserve prior evidence and reconcile without replay.'),
            ('f2b18bf3b1a236fa172616dae0b49abecfcf5730', 37695940420,
             'Database contains applied migration history absent from validated bundle; preserve prior evidence and reconcile without replay.'),
        )
        for head, run_id, message in cases:
            with self.subTest(run_id=run_id):
                paths = ('.github/workflows/' + self.authority.DIRECT_RELEASE_WORKFLOW,
                         'scripts/release-prepublication.py', 'scripts/release-migration.py',
                         'scripts/release-operation-evidence.py')
                sources = {
                    p: subprocess.check_output(['git', 'show', head + ':' + p], text=True)
                    for p in paths
                }
                run = dict(self.run, id=run_id, head_sha=head,
                           status='completed', conclusion='failure', run_attempt=1)
                job = dict(id=run_id, status='completed', conclusion='failure')
                step = dict(self.step, conclusion='failure')
                names = [
                    'legend-release-admission-' + 'a' * 64,
                    f'legend-release-step-state-{"d" * 40}-{run_id}-1',
                    *(f'diagnostics-rollback-{key}-{head}'
                      for key in ('portal', 'client', 'protect', 'parfait', 'website')),
                ]
                artifacts = [dict(name=n, expired=False, workflow_run=dict(id=run_id))
                             for n in names]
                def inventory(repo, path, token):
                    self.assertEqual(path, f'actions/runs/{run_id}/artifacts?per_page=100')
                    return dict(artifacts=artifacts, total_count=len(artifacts))
                log = '2026-10-07T21:52:09.2500317Z ' + message + '\n'
                with patch.object(self.authority, '_release_history_source',
                                  side_effect=lambda repo, sha, path, token: sources[path]), \
                     patch.object(self.authority, 'api_get', side_effect=inventory), \
                     patch.object(self.authority, '_release_job_log', return_value=log):
                    self.assertTrue(self.authority._attested_migration_prewrite_failure(
                        'owner/repo', run, job, step, 1, 'token'))
                    artifacts[1]['expired'] = True
                    self.assertFalse(self.authority._attested_migration_prewrite_failure(
                        'owner/repo', run, job, step, 1, 'token'))

    def test_migration_history_can_only_skip_after_attested_failure(self):
        self.step['conclusion'] = 'failure'
        with patch.object(self.authority, '_attested_migration_prewrite_failure',
                          return_value=True) as proof:
            self.assertTrue(self.check())
            proof.assert_called_once()
        with patch.object(self.authority, '_attested_migration_prewrite_failure',
                          return_value=False):
            with self.assertRaisesRegex(RuntimeError, 'no-write proof was not authenticated'):
                self.check()

    def test_child_first_write_reuses_observed_admission_nonentry_without_hiding_prior_entry(self):
        source = Path(__file__).with_name('..').resolve() / '.github/workflows' / self.authority.DIRECT_RELEASE_WORKFLOW
        source = source.read_text()
        jobs = [dict(name='admission', status='completed', conclusion='failure', steps=[]),
                *[dict(name=name, status='completed', conclusion='skipped', steps=[])
                  for name in ('discover-live', 'preserve-rollback', 'release', 'target-release-receipts (${{ matrix.app }})')],
                *[dict(name=name, status='completed', conclusion='success', steps=[])
                  for name in ('release-state-receipt', 'wake-release-lifecycle-after-terminal-release')]]
        original = self.api
        def api(repo, path, token):
            if '/jobs?' in path:
                return dict(jobs=jobs, total_count=len(jobs))
            return original(repo, path, token)
        self.api = api
        with patch.object(self.authority, '_release_history_source', return_value=source):
            self.assertTrue(self.check())
            jobs[:] = [jobs[0], jobs[2]]  # Actual omitted-downstream generation.
            self.assertTrue(self.check())
            jobs.append(dict(name='unexpected-write', conclusion='success'))
            with self.assertRaisesRegex(RuntimeError, 'owner unproven'):
                self.check()
        self.api = original
        self.run['run_attempt'] = 2
        self.step['conclusion'] = 'failure'
        with self.assertRaisesRegex(RuntimeError, 'missing intent'):
            self.check(current_run=7, attempt=2)

    def test_actual_legacy_schema_neutral_step_requires_bounded_positive_log(self):
        import subprocess
        source = subprocess.check_output(['git', 'show', 'f26b3f0bdd68879c01b78cf3a101cc39988c65e5:.github/workflows/' + self.authority.DIRECT_RELEASE_WORKFLOW], text=True)
        run = dict(self.run, head_sha='f26b3f0bdd68879c01b78cf3a101cc39988c65e5')
        step = dict(name='Apply additive diagnostics migrations before restarting apps', status='completed', conclusion='success',
                    started_at='2026-10-04T05:40:58Z', completed_at='2026-10-04T05:40:58Z')
        job = dict(id=111371429877, status='completed')
        checkout = '2026-10-04T05:36:00.0000000Z [command]/usr/bin/git log -1 --format=%H\n2026-10-04T05:36:00.0100000Z ' + run['head_sha'] + '\n'
        marker = 'No candidate migration source changed from the database baseline; migration receipt gate is not applicable.'
        log = checkout + '2026-10-04T05:40:58.6667345Z ' + marker + '\n'
        for evidence, expected in ((log, True),
                                   (log.replace('Z ' + marker, 'Z echo "' + marker + '"'), False),
                                   (log.replace('05:40:58.6667345', '05:40:59.0000000'), False),
                                   (log.replace(run['head_sha'], 'a' * 40), False),
                                   (log + log, False)):
            with patch.object(self.authority, '_RELEASE_CHILD_NOOP_PROOFS', set()), \
                 patch.object(self.authority, '_release_job_log', return_value=evidence):
                self.assertEqual(expected, self.authority._legacy_migration_noop('owner/repo', run, job, step, source, 'fixture'))
        with patch.object(self.authority, '_release_job_log', return_value=log), patch.object(self.authority, '_RELEASE_CHILD_NOOP_PROOFS', set()):
            self.assertFalse(self.authority._legacy_migration_noop('owner/repo', run, job, step,
                source.replace('python3 scripts/release-migration.py', 'python3 scripts/unknown.py'), 'fixture'))

    def test_child_nonentry_requires_complete_exact_attempt_inventory(self):
        original = self.api
        for count in (None, True, 0, 2):
            def api(repo, path, token):
                result = original(repo, path, token)
                if '/jobs?' in path:
                    result['total_count'] = count
                return result
            self.api = api
            with self.assertRaisesRegex(RuntimeError, 'history incomplete'):
                self.check()
        self.api = original

    def test_authenticated_legacy_migration_step_requires_original_intent(self):
        import subprocess
        source = subprocess.check_output(['git', 'show', 'd406e911:.github/workflows/' + self.authority.DIRECT_RELEASE_WORKFLOW], text=True)
        self.step['name'] = 'Apply additive diagnostics migrations before restarting apps'
        with patch.object(self.authority, '_release_history_source', return_value=source):
            with self.assertRaisesRegex(RuntimeError, 'missing intent'):
                self.check()
            self.step['conclusion'] = 'skipped'
            self.assertTrue(self.check())
        with patch.object(self.authority, '_release_history_source', return_value=source.replace('python3 scripts/release-migration.py', 'python3 scripts/unknown-migration.py')):
            with self.assertRaisesRegex(RuntimeError, 'execution detail unavailable'):
                self.check()

    def test_deleted_artifact_after_started_child_cannot_authorize_replay(self):
        with self.assertRaisesRegex(RuntimeError, 'missing intent'):
            self.check()

    def test_named_child_never_started_is_positive_proof_even_on_legacy_run(self):
        self.run.pop('display_title')
        self.step.update(conclusion='skipped')
        self.assertTrue(self.check())

    def test_failed_started_child_and_current_run_prior_attempt_fail_closed(self):
        self.step.update(conclusion='failure')
        self.run['run_attempt'] = 2
        with self.assertRaisesRegex(RuntimeError, 'missing intent'):
            self.check(current_run=7, attempt=2)

    def test_distinct_authenticated_original_physical_generation_allows_new_work(self):
        self.retained('f' * 64)
        self.assertTrue(self.check())

    def test_same_partition_unresolved_generation_cannot_authorize_new_write(self):
        self.retained('f' * 64)
        record = self.records[self.artifacts[0]['name']]
        record['partitionIdentity'] = self.material
        with patch.object(self.authority, 'release_child_history', return_value=None):
            with self.assertRaisesRegex(RuntimeError, 'prior partition operation unresolved'):
                self.check()

    def test_completed_same_partition_generation_allows_successor(self):
        self.retained('f' * 64)
        record = self.records[self.artifacts[0]['name']]
        record['partitionIdentity'] = self.material
        with patch.object(self.authority, 'release_child_history', return_value=dict(record, phase='success')):
            self.assertTrue(self.check())

    def test_missing_historical_partition_cannot_prove_independence(self):
        self.retained('f' * 64)
        self.records[self.artifacts[0]['name']].pop('partitionIdentity')
        with self.assertRaisesRegex(RuntimeError, 'historical partition unproven'):
            self.check()

    def test_same_physical_generation_cannot_replay_after_verifier_change(self):
        self.retained(self.identity)
        with self.assertRaisesRegex(RuntimeError, 'already entered'):
            self.check()

    def test_expired_or_missing_original_material_fails_closed(self):
        self.retained('f' * 64)
        self.artifacts[0]['expired'] = True
        with self.assertRaisesRegex(RuntimeError, 'expired'):
            self.check()
        self.artifacts[0]['expired'] = False
        self.records[self.artifacts[0]['name']].pop('materialIdentity')
        with self.assertRaisesRegex(RuntimeError, 'malformed'):
            self.check()

    def test_title_alone_never_establishes_distinct_material(self):
        self.run['display_title'] = 'LEGEND release pr=3 candidate=' + 'f' * 40 + ' authority=' + 'a' * 40
        with self.assertRaisesRegex(RuntimeError, 'missing intent'):
            self.check()

    def test_untrusted_or_truncated_actions_history_never_authorizes_write(self):
        self.run['head_repository'] = {'full_name': 'outsider/fork'}
        with self.assertRaisesRegex(RuntimeError, 'untrusted'):
            self.check()
        self.run['head_repository'] = {'full_name': 'owner/repo'}
        self.step.update(conclusion='skipped')
        self.total = 5
        with self.assertRaisesRegex(RuntimeError, 'truncated'):
            self.check()


if __name__ == '__main__':
    unittest.main()
