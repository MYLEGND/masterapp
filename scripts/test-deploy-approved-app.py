"""Exercise ambiguous deployment outcomes without contacting or mutating Azure."""
import hashlib
import importlib.util
import json
import os
import subprocess
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('deploy', ROOT / 'deploy-approved-app.py')
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)


def row(identifier, status):
    return {'id': identifier, 'status': status}


class FakeAzure:
    def __init__(self, states, revisions, accepted=True, static=False, recovery_accepted=True):
        self.states, self.revisions, self.accepted = list(states), list(revisions), accepted
        self.static, self.recovery_accepted = static, recovery_accepted
        self.uploads = 0
        self.recovery_uploads = 0
        self.now = 0

    def deployments(self):
        state = self.states.pop(0) if len(self.states) > 1 else self.states[0]
        if isinstance(state, Exception):
            raise state
        return state

    def revision_live(self):
        return self.revisions.pop(0) if len(self.revisions) > 1 else self.revisions[0]

    def submit(self):
        self.uploads += 1
        return self.accepted

    def submit_static_recovery(self):
        if not self.static:
            raise RuntimeError('not static')
        self.recovery_uploads += 1
        return self.recovery_accepted

    def sleep(self, seconds):
        self.now += seconds

    def run(self, **kwargs):
        return deploy.reconcile(
            self,
            clock=lambda: self.now,
            sleep=self.sleep,
            interval=1,
            timeout=10,
            **kwargs,
        )


class ReconciliationTests(unittest.TestCase):
    def test_exact_live_revision_is_preserved_without_upload(self):
        azure = FakeAzure([[row('old', 4)]], [True])
        self.assertEqual('preserved', azure.run())
        self.assertEqual(0, azure.uploads)

    def test_504_and_active_deployment_are_polled_never_resubmitted(self):
        azure = FakeAzure([[], [row('new', 1)], [row('new', 2)], [row('new', 4)]],
                          [False, None, False, True, True], accepted=False)
        self.assertEqual('deployed', azure.run())
        self.assertEqual(1, azure.uploads)

    def test_preexisting_active_candidate_finishes_without_any_upload(self):
        azure = FakeAzure([[row('pending', 1)], [row('pending', 4)]], [False, True, True])
        self.assertEqual('preserved', azure.run())
        self.assertEqual(0, azure.uploads)

    def test_terminal_failure_stays_failed_even_if_runtime_responds(self):
        azure = FakeAzure([[], [row('failed', 3)]], [False, True])
        with self.assertRaisesRegex(RuntimeError, 'failed'):
            azure.run()
        self.assertEqual(1, azure.uploads)

    def test_retained_failed_upload_preserves_exact_live_candidate_without_replay(self):
        class Journal:
            baseline = 'b' * 40
            history_error = None
            intent = {'baselineDeploymentIds': ['old']}
            def __init__(self):
                self.successes = []
            def record_success(self, deployment_ids):
                self.successes.append(list(deployment_ids))

        azure = FakeAzure(
            [[row('old', 4), row('failed', 3)]],
            [True],
        )
        azure.revision = 'a' * 40
        azure.observed_revision = lambda: 'a' * 40
        journal = Journal()

        self.assertEqual(
            'preserved',
            azure.run(baseline='b' * 40, reconcile_only=True, journal=journal),
        )
        self.assertEqual(0, azure.uploads)
        self.assertEqual([[]], journal.successes)

    def test_exact_live_receipt_transport_failure_is_deferred_without_upload_replay(self):
        class Journal:
            baseline = 'b' * 40
            history_error = None
            intent = None

            def __init__(self):
                self.receipt_attempts = 0

            def before_submit(self, baseline_ids, *, allow_recovered_baseline=False):
                self.intent = {'baselineDeploymentIds': sorted(baseline_ids)}
                return True

            def record_success(self, deployment_ids):
                self.receipt_attempts += 1
                if self.receipt_attempts == 1:
                    raise RuntimeError('receipt transport unavailable')
                return {'phase': 'success'}

        journal = Journal()
        first = FakeAzure(
            [[row('old', 4)], [row('old', 4), row('fresh', 4)]],
            [False, True, True],
        )
        first.revision = 'a' * 40
        observed = iter(['b' * 40, 'a' * 40, 'a' * 40])
        first.observed_revision = lambda: next(observed)

        self.assertEqual(
            'deployed-receipt-pending',
            deploy.reconcile(
                first,
                baseline='b' * 40,
                journal=journal,
                require_receipt=False,
                clock=lambda: first.now,
                sleep=first.sleep,
                interval=1,
                timeout=10,
            ),
        )
        self.assertEqual(1, first.uploads)

        # The final read-only reconciliation must retry only the receipt. It
        # observes the exact candidate and never submits the package again.
        second = FakeAzure(
            [[row('old', 4), row('fresh', 4)]],
            [True, True],
        )
        second.revision = 'a' * 40
        second.observed_revision = lambda: 'a' * 40
        self.assertEqual(
            'preserved',
            deploy.reconcile(
                second,
                baseline='b' * 40,
                reconcile_only=True,
                journal=journal,
                require_receipt=True,
                clock=lambda: second.now,
                sleep=second.sleep,
                interval=1,
                timeout=10,
            ),
        )
        self.assertEqual(0, second.uploads)
        self.assertEqual(2, journal.receipt_attempts)

    def test_retained_failed_upload_still_fails_when_candidate_not_live(self):
        class Journal:
            baseline = 'b' * 40
            history_error = None
            intent = {'baselineDeploymentIds': ['old']}
            def record_success(self, deployment_ids):
                raise AssertionError('must not record success')

        azure = FakeAzure(
            [[row('old', 4), row('failed', 3)]],
            [False],
        )
        azure.revision = 'a' * 40
        azure.observed_revision = lambda: 'b' * 40

        with self.assertRaisesRegex(RuntimeError, 'failed'):
            azure.run(baseline='b' * 40, reconcile_only=True, journal=Journal())
        self.assertEqual(0, azure.uploads)

    def test_static_terminal_failure_has_no_alternate_upload_path(self):
        azure = FakeAzure([[], [row('failed', 3)]], [False, False], static=True)
        with self.assertRaisesRegex(RuntimeError, 'failed'):
            azure.run()
        self.assertEqual(1, azure.uploads)
        self.assertEqual(0, azure.recovery_uploads)

    def test_unknown_upload_outcome_never_causes_another_upload(self):
        azure = FakeAzure([[]], [False], accepted=False)
        with self.assertRaisesRegex(RuntimeError, 'deadline'):
            azure.run()
        self.assertEqual(1, azure.uploads)

    def test_runtime_unavailable_does_not_authorize_restarting_it(self):
        azure = FakeAzure([[]], [None])
        with self.assertRaisesRegex(RuntimeError, 'deadline'):
            azure.run()
        self.assertEqual(0, azure.uploads)

    def test_status_unavailable_is_bounded_and_fail_closed(self):
        azure = FakeAzure([RuntimeError('gateway unavailable')], [False])
        with self.assertRaisesRegex(deploy.DeploymentStatusUnavailable, '3 consecutive reads'):
            azure.run()
        self.assertEqual(2, azure.now)
        self.assertEqual(0, azure.uploads)

    def test_transient_status_outage_recovers_without_upload_replay(self):
        azure = FakeAzure(
            [[], RuntimeError('gateway unavailable'), RuntimeError('gateway unavailable'), [row('new', 4)]],
            [False, True, True],
            accepted=False)
        self.assertEqual('deployed', azure.run())
        self.assertEqual(1, azure.uploads)

    def test_preflight_retries_transient_azure_history_timeout_without_upload(self):
        azure = FakeAzure(
            [subprocess.TimeoutExpired(['az', 'webapp', 'log', 'deployment', 'list'], 20), [row('old', 4)]],
            [False])
        azure.revision = 'a' * 40
        azure.observed_revision = lambda: 'b' * 40
        with patch.object(deploy, 'target_azure', return_value=azure), \
             patch.object(deploy.time, 'sleep') as sleeper:
            deploy.preflight_target('website', Path('/immutable.zip'), 'a' * 40, 'b' * 40, None)
        sleeper.assert_called_once_with(15)
        self.assertEqual(0, azure.uploads)

    def test_history_gap_allows_one_fresh_upload_only_from_exact_idle_baseline(self):
        from types import SimpleNamespace
        azure = FakeAzure([[row('old', 4)]], [False])
        azure.revision = 'a' * 40
        azure.observed_revision = lambda: 'b' * 40
        journal = SimpleNamespace(intent=None, history_error=RuntimeError('missing intent'))
        with patch.object(deploy, 'target_azure', return_value=azure):
            deploy.preflight_target('website', Path('/immutable.zip'), 'a' * 40, 'b' * 40, journal)
        self.assertEqual(0, azure.uploads)

    def test_reconcile_carries_exact_idle_baseline_proof_into_fresh_intent(self):
        class Journal:
            baseline = 'b' * 40
            intent = None
            history_error = RuntimeError('missing historical intent')
            def __init__(self):
                self.allowed = []
            def before_submit(self, baseline_ids, *, allow_recovered_baseline=False):
                self.allowed.append((set(baseline_ids), allow_recovered_baseline))
                if not allow_recovered_baseline:
                    raise self.history_error
                self.intent = {'baselineDeploymentIds': sorted(baseline_ids)}
                self.history_error = None
                return True
            def record_success(self, deployment_ids):
                return None

        azure = FakeAzure(
            [[row('old', 4)], [row('old', 4), row('fresh', 4)]],
            [False])
        azure.revision = 'a' * 40
        observed = iter(['b' * 40, 'a' * 40, 'a' * 40])
        azure.observed_revision = lambda: next(observed)
        journal = Journal()

        self.assertEqual(
            'deployed',
            deploy.reconcile(
                azure,
                baseline='b' * 40,
                journal=journal,
                clock=lambda: azure.now,
                sleep=azure.sleep,
                interval=1,
                timeout=10,
            ),
        )
        self.assertEqual(1, azure.uploads)
        self.assertEqual([({'old'}, True)], journal.allowed)

    def test_history_gap_never_allows_replay_from_active_or_unknown_state(self):
        from types import SimpleNamespace
        journal = SimpleNamespace(intent=None, history_error=RuntimeError('missing intent'))
        for states, observed in (
            ([[row('pending', 1)]], 'b' * 40),
            ([[row('old', 4)]], None),
            ([[row('other', 4)]], 'c' * 40),
        ):
            azure = FakeAzure(states, [False])
            azure.revision = 'a' * 40
            azure.observed_revision = lambda value=observed: value
            with self.subTest(states=states, observed=observed), \
                 patch.object(deploy, 'target_azure', return_value=azure):
                with self.assertRaises(deploy.DeploymentReconciliationRequired):
                    deploy.preflight_target('website', Path('/immutable.zip'), 'a' * 40, 'b' * 40, journal)
            self.assertEqual(0, azure.uploads)

    def test_live_revision_alone_cannot_override_active_deployment(self):
        azure = FakeAzure([[row('still-running', 2)]], [True])
        with self.assertRaisesRegex(RuntimeError, 'deadline'):
            azure.run()
        self.assertEqual(0, azure.uploads)

    def test_azure_success_requires_stable_runtime_revision(self):
        azure = FakeAzure([[], [row('new', 4)]], [False, True, None, True, True])
        self.assertEqual('deployed', azure.run())
        self.assertEqual(4, azure.now)
        self.assertEqual(1, azure.uploads)

    def test_old_success_cannot_stand_in_for_new_deployment(self):
        azure = FakeAzure([[row('old', 4)]], [False, True])
        with self.assertRaisesRegex(RuntimeError, 'deadline'):
            azure.run()
        self.assertEqual(1, azure.uploads)

    def test_concurrent_publication_is_not_hidden(self):
        azure = FakeAzure([[], [row('ours', 4), row('other', 4)]], [False, True])
        with self.assertRaisesRegex(RuntimeError, 'Multiple'):
            azure.run()
        self.assertEqual(1, azure.uploads)


    def test_fresh_process_retry_never_replays_ambiguous_original_upload(self):
        first = FakeAzure([[]], [False], accepted=False)
        with self.assertRaises(deploy.DeploymentReconciliationRequired):
            first.run()
        resumed = FakeAzure([[], [row('delayed-original', 4)]], [False, True, True])
        self.assertEqual('preserved', resumed.run(reconcile_only=True))
        self.assertEqual(1, first.uploads)
        self.assertEqual(0, resumed.uploads)

    def test_retry_with_unproven_original_outcome_fails_closed(self):
        azure = FakeAzure([[]], [False])
        with self.assertRaises(deploy.DeploymentReconciliationRequired):
            azure.run(reconcile_only=True)
        self.assertEqual(0, azure.uploads)

    def test_baseline_candidate_and_third_revision_are_distinguished(self):
        for observed, allowed in [('b' * 40, True), ('a' * 40, True), ('c' * 40, False)]:
            azure = FakeAzure([[row('old', 4)]], [False])
            azure.revision = 'a' * 40
            azure.observed_revision = lambda: observed
            with self.subTest(observed=observed):
                if not allowed:
                    with self.assertRaises(deploy.DeploymentDrift):
                        azure.run(baseline='b' * 40)
                elif observed == azure.revision:
                    self.assertEqual('preserved', azure.run(baseline='b' * 40))
                else:
                    with self.assertRaises(deploy.DeploymentReconciliationRequired):
                        azure.run(baseline='b' * 40, reconcile_only=True)
                self.assertEqual(0, azure.uploads)


class PackageTests(unittest.TestCase):
    def make_package(self, folder, static=False, revision='a' * 40):
        path = Path(folder) / 'app.zip'
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr('_deployment-provenance.txt' if static else 'wwwroot/_deployment-provenance.json',
                             revision if static else json.dumps({'releaseSha': revision}))
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        (path.parent / 'SHA256SUMS').write_text(f'{digest}  {path}\n')
        return path, digest

    def test_all_targets_use_retained_packages_with_bound_provenance(self):
        for target in deploy.TARGETS:
            with self.subTest(target=target), tempfile.TemporaryDirectory() as folder:
                path, digest = self.make_package(folder, target == 'website')
                self.assertEqual(digest, deploy.verify_package(path, 'a' * 40, target == 'website'))

    def test_wrong_revision_is_rejected_even_when_checksum_matches(self):
        with tempfile.TemporaryDirectory() as folder:
            path, _ = self.make_package(folder)
            with self.assertRaisesRegex(ValueError, 'approved revision'):
                deploy.verify_package(path, 'b' * 40)

    def test_modified_package_cannot_be_deployed(self):
        with tempfile.TemporaryDirectory() as folder:
            path, _ = self.make_package(folder)
            with path.open('ab') as stream:
                stream.write(b'changed')
            with self.assertRaisesRegex(ValueError, 'SHA256'):
                deploy.verify_package(path, 'a' * 40)

    def test_submission_has_no_synchronous_gateway_wait_or_internal_warmup_replay(self):
        azure = deploy.Azure('masterapp-protect', Path('/immutable.zip'), 'https://example.invalid', 'a' * 40)
        with patch.object(deploy.subprocess, 'run') as run:
            run.return_value.returncode = 0
            self.assertTrue(azure.submit())
            command = run.call_args.args[0]
            for flag, value in [('--async', 'true'), ('--track-status', 'false'),
                                ('--enable-kudu-warmup', 'false'), ('--type', 'zip')]:
                self.assertEqual(value, command[command.index(flag) + 1])
            self.assertEqual(1, run.call_count)



class PackageContractTests(unittest.TestCase):
    def test_deployment_and_workflow_orchestration_changes_preserve_package_identity(self):
        spec = importlib.util.spec_from_file_location('release_package_test', ROOT / 'release-package.py')
        package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(package)
        with tempfile.TemporaryDirectory() as folder:
            checkout = Path(folder)
            for relative in package.CONTRACT_INPUTS:
                dest = checkout / relative
                dest.parent.mkdir(parents=True, exist_ok=True)
                dest.write_bytes((ROOT.parent / relative).read_bytes())
            with patch.object(package, 'ROOT', checkout):
                original = package.package_identity('a' * 40)
                (checkout / 'scripts/deploy-approved-app.py').write_text('changed deployment reconciliation')
                self.assertEqual(original, package.package_identity('a' * 40))
                workflow = checkout / package._RELEASE_AUTHORITY.PACKAGE_BUILD_WORKFLOW
                content = workflow.read_text()
                workflow.write_text(content + '\n# unrelated orchestration comment\n')
                self.assertEqual(original, package.package_identity('a' * 40))
                workflow.write_text(content.replace("dotnet-version: '10.0.401'", "dotnet-version: '10.0.402'"))
                self.assertNotEqual(original, package.package_identity('a' * 40))

    def test_package_contract_includes_migration_bundle_identity(self):
        spec = importlib.util.spec_from_file_location('release_package_test', ROOT / 'release-package.py')
        package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(package)
        original = package.contract_hash()
        with patch.object(package, 'MIGRATION_BUNDLE', 'changed-migration-bundle'):
            self.assertNotEqual(original, package.contract_hash())


    def test_isolated_migration_component_owns_its_project_restore(self):
        source = (ROOT / 'release-package.py').read_text()
        block = source.split('def build_migration_bundle(output: Path):', 1)[1].split(
            '\ndef ', 1
        )[0]
        restore = block.index('run("dotnet", "restore", "AgentPortal/AgentPortal.csproj"')
        bundle = block.index('"migrations", "bundle"')
        self.assertLess(restore, bundle)
        self.assertIn('previously serialized app publish', block)


    def test_reused_immutable_package_keeps_original_manifest_and_bytes(self):
        spec = importlib.util.spec_from_file_location('release_package_test', ROOT / 'release-package.py')
        package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(package)
        revision = 'a' * 40
        declared_contract = 'd' * 64
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            files = {}
            for app, (_, filename, static) in package.APPS.items():
                with zipfile.ZipFile(directory / filename, 'w') as archive:
                    archive.writestr('_deployment-provenance.txt' if static else 'wwwroot/_deployment-provenance.json',
                                     revision if static else json.dumps({'releaseSha': revision}))
                files[filename] = package.sha256_file(directory / filename)
            (directory / package.MIGRATION_BUNDLE).write_bytes(b'preserved bundle')
            files[package.MIGRATION_BUNDLE] = package.sha256_file(directory / package.MIGRATION_BUNDLE)
            manifest = {'schema': package.SCHEMA, 'applicationReleaseSha': revision, 'applicationTreeSha': 'tree',
                        'packageContractSha256': declared_contract, 'files': files}
            manifest['packageIdentity'] = hashlib.sha256(json.dumps({'schema': package.SCHEMA,
                'applicationReleaseSha': revision, 'packageContractSha256': declared_contract}, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
            (directory / 'manifest.json').write_text(json.dumps(manifest))
            (directory / 'SHA256SUMS').write_text(''.join(digest + '  ' + name + '\n' for name, digest in files.items()))
            before = {path.name: path.read_bytes() for path in directory.iterdir()}
            with patch.object(package, 'validate_revision', return_value=revision), \
                 patch.object(package.subprocess, 'check_output', side_effect=['tree', 'b' * 40]), \
                 patch.object(package._RELEASE_AUTHORITY, 'package_inputs_compatible', return_value=True):
                actual = package.verify_all(revision, directory)
            self.assertEqual(revision, actual['applicationReleaseSha'])
            self.assertEqual(declared_contract, actual['packageContractSha256'])
            self.assertEqual(before, {path.name: path.read_bytes() for path in directory.iterdir()})


class PackageProducerTests(unittest.TestCase):
    def test_green_package_child_survives_red_parent_and_reuses_original_producer(self):
        authority = deploy._RELEASE_AUTHORITY
        producer = 'a' * 40
        run = {'id': 12, 'head_sha': producer, 'conclusion': 'failure',
               'path': '.github/workflows/' + authority.PACKAGE_VALIDATION_WORKFLOW,
               'event': 'pull_request', 'status': 'completed',
               'head_repository': {'full_name': 'MYLEGND/masterapp'}}
        artifact = {'name': 'founder-diagnostics-packages-' + 'c' * 64,
                    'workflow_run': {'id': 12}, 'expired': False}
        jobs = {'jobs': [{'name': 'validated-release-package', 'conclusion': 'success', 'steps': [
            {'name': name, 'conclusion': 'success'} for name in (
                'Build immutable validated release package', 'Verify immutable validated release package',
                'Preserve immutable validated release package')]}]}
        def api(repository, path, token):
            if path.startswith('actions/workflows/'):
                return {'workflow_runs': [run]}
            if path.startswith('actions/artifacts?') or '/artifacts?' in path:
                return {'artifacts': [artifact]}
            if '/jobs?' in path:
                return jobs
            return run
        with patch.object(authority, 'api_get', side_effect=api), \
             patch.object(authority.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0)), \
             patch.object(authority, 'git_changed', return_value=[]), \
             patch.object(authority, 'package_inputs_compatible', return_value=True):
            result = authority.compatible_package_producer('MYLEGND/masterapp', 'b' * 40, 'test-token')
            self.assertEqual(producer, result['revision'])
            self.assertEqual('c' * 64, result['packageIdentity'])
            jobs['jobs'][0]['steps'][1]['conclusion'] = 'failure'
            self.assertIsNone(authority.compatible_package_producer('MYLEGND/masterapp', 'b' * 40, 'test-token'))

    def test_historical_package_workflow_shape_is_incompatible_not_planner_failure(self):
        authority = deploy._RELEASE_AUTHORITY
        with patch.object(authority, 'git_changed', return_value=[authority.PACKAGE_BUILD_WORKFLOW]), \
             patch.object(authority, 'git_show_file', side_effect=['old workflow', 'new workflow']), \
             patch.object(authority, 'package_builder_workflow_contract', side_effect=ValueError('old shape')):
            self.assertFalse(authority.package_inputs_compatible('a' * 40, 'b' * 40))

    def test_builder_globals_and_invoked_helpers_invalidate_compatibility(self):
        authority = deploy._RELEASE_AUTHORITY
        source = (ROOT / 'release-package.py').read_text()
        with patch.object(authority, 'git_changed', return_value=['scripts/release-package.py']):
            for changed in (source.replace('ROOT = Path(__file__).resolve().parents[1]', 'ROOT = Path("/different-project")'),
                            source.replace('def run(*args, env=None):', 'def run(*args, env=None):\n    print("changed")'),
                            source.replace('def verify_all(revision: str, directory: Path):', 'def verify_all(revision: str, directory: Path):\n    (directory / "agentportal.zip").write_bytes(b"changed")'),
                            source.replace('bundle.chmod(0o755)', 'bundle.chmod(0o644)')):
                with patch.object(authority, 'git_show_file', side_effect=[source, changed]):
                    self.assertFalse(authority.package_inputs_compatible('a' * 40, 'b' * 40))


class ParallelPublicationTests(unittest.TestCase):
    def plan(self, keys):
        return {'targets': [{'app': key} for key in keys]}

    def test_parallel_publication_settles_every_prepared_target_and_records_results(self):
        keys = ('portal', 'client', 'protect')
        names = [deploy.TARGETS[key]['releaseName'] for key in keys]
        with tempfile.TemporaryDirectory() as directory, \
             patch.object(deploy._RELEASE_AUTHORITY, 'selected_release_target_keys', return_value=keys), \
             patch.object(deploy, 'publish_prepared_target',
                          side_effect=lambda key, revision, package_root, plan: 'deployed'):
            results = deploy.publish_prepared_targets_parallel(
                names,
                'a' * 40,
                Path(directory),
                self.plan(keys),
                Path(directory) / 'results',
            )
            self.assertEqual(set(keys), set(results))
            for key in keys:
                payload = json.loads((Path(directory) / 'results' / f'{key}.json').read_text())
                self.assertTrue(payload['success'])
                self.assertEqual(key, payload['target'])

    def test_parallel_publication_enters_all_selected_targets_before_any_worker_finishes(self):
        keys = ('portal', 'client', 'protect', 'parfait', 'website')
        names = [deploy.TARGETS[key]['releaseName'] for key in keys]
        barrier = threading.Barrier(len(keys))
        entered = []
        entered_lock = threading.Lock()

        def publish(key, revision, package_root, plan):
            with entered_lock:
                entered.append(key)
            barrier.wait(timeout=5)
            return 'deployed'

        with tempfile.TemporaryDirectory() as directory, \
             patch.object(deploy._RELEASE_AUTHORITY, 'selected_release_target_keys', return_value=keys), \
             patch.object(deploy, 'publish_prepared_target', side_effect=publish):
            results = deploy.publish_prepared_targets_parallel(
                names,
                'a' * 40,
                Path(directory),
                self.plan(keys),
                Path(directory) / 'results',
            )

        self.assertEqual(set(keys), set(entered))
        self.assertEqual(set(keys), set(results))
        self.assertTrue(all(result['success'] for result in results.values()))

    def test_parallel_publication_preserves_successful_sibling_when_one_target_fails(self):
        keys = ('portal', 'protect')
        names = [deploy.TARGETS[key]['releaseName'] for key in keys]

        def publish(key, revision, package_root, plan):
            if key == 'protect':
                raise deploy.DeploymentReconciliationRequired('fixture failure')
            return 'deployed'

        with tempfile.TemporaryDirectory() as directory, \
             patch.object(deploy._RELEASE_AUTHORITY, 'selected_release_target_keys', return_value=keys), \
             patch.object(deploy, 'publish_prepared_target', side_effect=publish):
            with self.assertRaisesRegex(RuntimeError, 'protect'):
                deploy.publish_prepared_targets_parallel(
                    names,
                    'a' * 40,
                    Path(directory),
                    self.plan(keys),
                    Path(directory) / 'results',
                )
            root = Path(directory) / 'results'
            self.assertTrue(json.loads((root / 'portal.json').read_text())['success'])
            failed = json.loads((root / 'protect.json').read_text())
            self.assertFalse(failed['success'])
            self.assertEqual('DeploymentReconciliationRequired', failed['errorType'])


class MigrationProbeEvidenceTests(unittest.TestCase):
    def test_probe_tool_changes_do_not_relabel_application_or_accept_runtime_drift(self):
        authority = deploy._RELEASE_AUTHORITY
        workflow = (ROOT.parent / '.github/workflows/masterapp-platform-architecture-validation.yml').read_text()
        runtime = '100644 blob ' + '1' * 40 + '\tInfrastructure/Migrations/Example.cs\0'
        helper = '100644 blob ' + '2' * 40 + '\tscripts/MigrationReleaseProbe/Program.cs\0'
        def tree(command, **kwargs):
            return runtime + (helper if command[-1] == 'b' * 40 else '')
        with patch.object(authority.subprocess, 'check_output', side_effect=tree), \
             patch.object(authority, 'git_show_file', return_value=workflow):
            identity = authority.migration_probe_identity('b' * 40, 'a' * 40)
            self.assertEqual(identity, authority.migration_probe_identity('b' * 40, 'b' * 40))
        with patch.object(authority.subprocess, 'check_output', side_effect=[runtime, runtime.replace('1' * 40, '3' * 40)]), \
             patch.object(authority, 'git_show_file', return_value=workflow):
            with self.assertRaisesRegex(ValueError, 'different candidate'):
                authority.migration_probe_identity('b' * 40, 'a' * 40)


class DisjointReleaseRevisionTests(unittest.TestCase):
    def test_queued_immutable_candidate_survives_unrelated_target_movement(self):
        spec = importlib.util.spec_from_file_location('release_baseline_test', ROOT / 'approved-release-baseline.py')
        baseline = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(baseline)
        with patch.object(baseline.subprocess, 'run'), \
             patch.object(baseline.subprocess, 'check_output', return_value='Protect-Website/Program.cs\n'):
            self.assertEqual('a' * 40, baseline.validated_application_revision('a' * 40, 'b' * 40, ['masterapp-client']))
            with self.assertRaisesRegex(ValueError, 'application changes'):
                baseline.validated_application_revision('a' * 40, 'b' * 40, ['masterapp-protect'])
        with patch.object(baseline.subprocess, 'run'), \
             patch.object(baseline.subprocess, 'check_output', return_value='Infrastructure/Services/Runtime.cs\n'):
            with self.assertRaisesRegex(ValueError, 'application changes'):
                baseline.validated_application_revision('a' * 40, 'b' * 40, ['masterapp-client'])


class PreparedTransactionTests(unittest.TestCase):
    def plan(self, keys=None):
        plan = {'schemaVersion': 1, 'candidateRevision': 'a' * 40,
                'targets': [{'app': key, 'revision': 'b' * 40, 'packageDigest': 'c' * 64}
                            for key in (keys or list(deploy.TARGETS)[:2])]}
        identity = {'candidateRevision': plan['candidateRevision'], 'packageDigests': {row['app']: row['packageDigest'] for row in plan['targets']}}
        plan['planId'] = hashlib.sha256(json.dumps(identity, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
        return plan

    def test_finalization_is_read_only_even_after_partial_failure(self):
        plan = self.plan()
        with patch.object(deploy, 'verify_package', return_value='c' * 64), \
             patch.object(deploy, 'reconcile', side_effect=['preserved', deploy.DeploymentReconciliationRequired('unverified')]) as reconcile:
            with self.assertRaises(deploy.DeploymentReconciliationRequired):
                deploy.finalize_prepared_transaction(plan, Path('/packages'), 'a' * 40)
        self.assertEqual(2, reconcile.call_count)
        self.assertTrue(all(call.kwargs['reconcile_only'] for call in reconcile.call_args_list))

    def test_target_cannot_publish_modified_package_after_preflight(self):
        plan = self.plan()
        with patch.object(deploy, 'verify_package', return_value='d' * 64), patch.object(deploy, 'deploy_one') as publish:
            with self.assertRaisesRegex(ValueError, 'package changed'):
                deploy.publish_prepared_target(plan['targets'][0]['app'], 'a' * 40, Path('/packages'), plan)
        publish.assert_not_called()

    def test_reconcile_only_flag_survives_prepared_target_entrypoint(self):
        plan = self.plan()
        with patch.object(deploy, 'verify_package', return_value='c' * 64), patch.object(deploy, 'deploy_one') as publish:
            deploy.publish_prepared_target(plan['targets'][0]['app'], 'a' * 40, Path('/packages'), plan, reconcile_only=True)
        self.assertTrue(publish.call_args.kwargs['reconcile_only'])

    def test_prepare_restores_original_untouched_target_baseline_and_rollback_reference(self):
        prior = self.plan(['portal', 'client'])
        rollback = {'artifact': 'original-rollback', 'runId': 17, 'revision': 'b' * 40, 'packageDigest': 'd' * 64}
        for row in prior['targets']:
            row['rollbackEvidence'] = rollback
        env = {'GITHUB_ACTIONS': 'true', 'GITHUB_REPOSITORY': 'MYLEGND/masterapp',
               'GITHUB_RUN_ID': '22', 'GITHUB_RUN_ATTEMPT': '1', 'GITHUB_TOKEN': 'test-token'}
        names = [deploy.TARGETS[row['app']]['releaseName'] for row in prior['targets']]
        observed = json.dumps([{'app': row['app'], 'revision': 'c' * 40} for row in prior['targets']])
        with tempfile.TemporaryDirectory() as folder, patch.dict(os.environ, env), \
             patch.object(deploy, 'verify_package', return_value='c' * 64), \
             patch.object(deploy._RELEASE_AUTHORITY, 'release_transaction_plan_history', return_value=prior), \
             patch.object(deploy, 'operation_journal', return_value=None), \
             patch.object(deploy, 'retained_rollback_package', return_value=rollback) as restore, \
             patch.object(deploy, 'preflight_target') as preflight:
            plan = deploy.prepare_transaction(names, observed, Path('/packages'), Path('/rollback'), 'a' * 40, Path(folder) / 'plan.json')
        self.assertEqual(['b' * 40, 'b' * 40], [row['revision'] for row in plan['targets']])
        self.assertTrue(all(call.args[3] is rollback for call in restore.call_args_list))
        self.assertTrue(all(call.args[3] == 'b' * 40 for call in preflight.call_args_list))

    def test_terminal_disposition_retries_transient_azure_history_timeout_without_writes(self):
        from types import SimpleNamespace
        plan = self.plan(['parfait'])
        journal = SimpleNamespace(intent={'baselineDeploymentIds': ['old']}, history_error=None)
        azure = FakeAzure(
            [subprocess.TimeoutExpired(['az', 'webapp', 'log', 'deployment', 'list'], 20),
             [row('old', 4), row('ours', 4)]],
            [False])
        azure.observed_revision = lambda: 'a' * 40
        with patch.object(deploy, 'verify_package', return_value='c' * 64), \
             patch.object(deploy, 'operation_journal', return_value=journal), \
             patch.object(deploy, 'target_azure', return_value=azure), \
             patch.object(deploy.time, 'sleep') as sleeper:
            result = deploy.transaction_disposition(plan, Path('/packages'), 'a' * 40)

        self.assertTrue(result['terminal'])
        self.assertEqual([{'target': 'parfait', 'revision': 'a' * 40, 'idle': True}], result['targets'])
        sleeper.assert_called_once_with(15)
        self.assertEqual(0, azure.uploads)
        self.assertEqual(0, azure.recovery_uploads)

    def test_idle_baseline_cannot_discharge_an_ambiguous_prior_upload(self):
        from types import SimpleNamespace
        plan = self.plan(['portal'])
        journal = SimpleNamespace(intent={'baselineDeploymentIds': ['old']}, history_error=None)
        azure = FakeAzure([[row('old', 4)]], [False])
        azure.observed_revision = lambda: 'b' * 40
        with patch.object(deploy, 'verify_package', return_value='c' * 64), \
             patch.object(deploy, 'operation_journal', return_value=journal), \
             patch.object(deploy, 'target_azure', return_value=azure):
            with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired, 'ambiguous'):
                deploy.transaction_disposition(plan, Path('/packages'), 'a' * 40)
            azure.states = [[row('old', 4), row('ours', 3)]]
            result = deploy.transaction_disposition(plan, Path('/packages'), 'a' * 40)
            self.assertTrue(result['terminal'])
            self.assertEqual(0, azure.uploads)

    def test_prepared_plan_rejects_other_candidate_and_target(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'plan.json'
            path.write_text(json.dumps(self.plan(['portal'])))
            with self.assertRaises(ValueError):
                deploy.read_transaction_plan(path, 'd' * 40)
            with self.assertRaises(ValueError):
                deploy.read_transaction_plan(path, 'a' * 40, 'client')


class SettingsIdempotenceTests(unittest.TestCase):
    def run_settings(self, drift=False):
        authority = deploy._RELEASE_AUTHORITY
        all_names = [row['releaseName'] for row in deploy.TARGETS.values()]
        profile = authority.release_runtime_profile(all_names)
        shared_target = profile['sharedAuthTargets'][0]
        editor_target = next(name for name in profile['editorTargets'] if name != shared_target)

        spec = importlib.util.spec_from_file_location(
            'release_prepublication_test',
            ROOT / 'release-prepublication.py',
        )
        prepublication = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(prepublication)

        common = {
            'FOUNDER_OID': 'test-founder',
            'Founder__Upn': 'test@example.invalid',
            'DataProtection__BlobUri': 'test-blob',
            'DataProtection__KeyVaultKeyId': 'test-key',
            'MarketingDataProtection__BlobUri': 'test-blob',
            'MarketingDataProtection__KeyVaultKeyId': 'test-key',
            'WebsiteEditorDataProtection__BlobUri': 'test-blob',
            'WebsiteEditorDataProtection__KeyVaultKeyId': 'test-key',
            'Analytics__SharedSecret': 'test-secret',
            'Tracking__SharedSecret': 'test-secret',
            'Tracking:SharedSecret': 'test-secret',
            'WEBSITE_NODE_DEFAULT_VERSION': '~24',
        }
        state = {row['releaseName']: dict(common) for row in deploy.TARGETS.values()}
        if drift:
            state[shared_target]['Tracking:SharedSecret'] = 'stale'
            state[editor_target]['WebsiteEditorDataProtection__BlobUri'] = 'stale'

        writes = []
        lock = threading.Lock()

        def fake_settings(app):
            with lock:
                return dict(state[app])

        def fake_run(command, *, capture=False, timeout=300, env=None):
            self.assertEqual(['az', 'webapp', 'config', 'appsettings', 'set'], command[:5])
            app = command[command.index('-n') + 1]
            pairs = command[command.index('--settings') + 1:command.index('--output')]
            updates = dict(pair.split('=', 1) for pair in pairs)
            with lock:
                self.assertTrue(updates)
                self.assertTrue(all(state[app].get(key) != value for key, value in updates.items()))
                state[app].update(updates)
                writes.append({'app': app, 'keys': sorted(updates)})
            return ''

        environment = {
            'SELECTED_TARGETS': json.dumps(list(state)),
            'RELEASE_RESOURCE_GROUP': profile['resourceGroup'],
            'DATABASE_AUTHORITY': profile['databaseAuthority'],
            'SHARED_AUTH_TARGETS': json.dumps(profile['sharedAuthTargets']),
            'MARKETING_TARGETS': json.dumps(profile['marketingTargets']),
            'EDITOR_TARGETS': json.dumps(profile['editorTargets']),
            'ROUTING_TARGETS': '[]',
            'WEBSITE_ROUTING': 'false',
            'PRESERVE_LIVE_TARGETS': 'false',
        }
        with patch.dict(os.environ, environment, clear=False), \
             patch.object(prepublication, 'app_settings', side_effect=fake_settings), \
             patch.object(prepublication, 'run', side_effect=fake_run), \
             patch.object(prepublication, 'child_receipt'):
            prepublication.configure_all_targets()
            prepublication.configure_all_targets()

        return sorted(writes, key=lambda row: row['app']), shared_target, editor_target

    def test_matching_settings_make_zero_azure_writes_across_repeated_runs(self):
        writes, _, _ = self.run_settings()
        self.assertEqual([], writes)

    def test_only_drifted_keys_are_updated_once_and_then_preserved(self):
        writes, shared_target, editor_target = self.run_settings(drift=True)
        expected = sorted([
            {'app': shared_target, 'keys': ['Tracking:SharedSecret']},
            {'app': editor_target, 'keys': ['WebsiteEditorDataProtection__BlobUri']},
        ], key=lambda row: row['app'])
        self.assertEqual(expected, writes)

if __name__ == '__main__':
    unittest.main()
