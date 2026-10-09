"""Exercise ambiguous deployment outcomes without contacting or mutating Azure."""
import hashlib
import importlib.util
import json
import os
import subprocess
import shutil
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


class RetainedJournal:
    # Provider-only fixtures retain the original successful write intent.
    baseline = None
    intent = {'baselineDeploymentIds': []}
    def __init__(self): self.successes = []
    def record_success(self, ids): self.successes.append(ids)


class ReconciliationTests(unittest.TestCase):
    def test_matching_live_revision_without_artifact_proof_blocks_without_upload(self):
        azure = FakeAzure([[row('old', 4)]], [True])
        with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired, 'artifact publication intent'):
            azure.run()
        self.assertEqual(0, azure.uploads)

    def test_504_and_active_deployment_are_polled_never_resubmitted(self):
        azure = FakeAzure([[], [row('new', 1)], [row('new', 2)], [row('new', 4)]],
                          [False, None, False, True, True], accepted=False)
        self.assertEqual('deployed', azure.run())
        self.assertEqual(1, azure.uploads)

    def test_preexisting_active_candidate_finishes_without_any_upload(self):
        azure = FakeAzure([[row('pending', 1)], [row('pending', 4)]], [False, True, True])
        self.assertEqual('preserved', azure.run(journal=RetainedJournal()))
        self.assertEqual(0, azure.uploads)

    def test_terminal_failure_stays_failed_even_if_runtime_responds(self):
        azure = FakeAzure([[], [row('failed', 3)]], [False, True])
        with self.assertRaisesRegex(RuntimeError, 'failed'):
            azure.run()
        self.assertEqual(1, azure.uploads)

    def test_retained_failed_upload_cannot_certify_matching_source_without_artifact_proof(self):
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

        with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired, 'does not prove'):
            azure.run(baseline='b' * 40, reconcile_only=True, journal=journal)
        self.assertEqual(0, azure.uploads)
        self.assertEqual([], journal.successes)

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
        self.assertEqual('preserved', resumed.run(reconcile_only=True, journal=RetainedJournal()))
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
                    with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired, 'artifact publication intent'):
                        azure.run(baseline='b' * 40)
                else:
                    with self.assertRaises(deploy.DeploymentReconciliationRequired):
                        azure.run(baseline='b' * 40, reconcile_only=True)
                self.assertEqual(0, azure.uploads)


class PackageTests(unittest.TestCase):
    def test_different_artifact_at_same_sha_cannot_be_certified_from_live_sha(self):
        from types import SimpleNamespace
        from unittest.mock import Mock
        revision = 'a' * 40
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            first = root / 'first'; first.mkdir()
            second = root / 'second'; second.mkdir()
            live, live_digest = self.make_package(first, revision=revision)
            requested, _ = self.make_package(second, revision=revision)
            with zipfile.ZipFile(requested, 'a') as archive:
                archive.writestr('different-compiled-output.dll', b'new toolchain bytes')
            requested_digest = hashlib.sha256(requested.read_bytes()).hexdigest()
            (second / 'SHA256SUMS').write_text(f'{requested_digest}  {requested.name}\n')
            self.assertNotEqual(live_digest, deploy.verify_package(requested, revision))
            azure = FakeAzure([[row('existing-artifact', 4)]], [True])
            azure.revision = revision
            azure.observed_revision = lambda: revision
            journal = SimpleNamespace(baseline=revision, intent=None, history_error=None,
                identity=dict(applicationRevision=revision, packageDigest=requested_digest), record_success=Mock())
            with patch.object(deploy, 'target_azure', return_value=azure):
                with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired, 'artifact is unproven'):
                    deploy.preflight_target('portal', requested, revision, revision, journal)
            with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired, 'artifact publication intent'):
                azure.run(journal=journal)
            journal.record_success.assert_not_called()
            self.assertEqual(0, azure.uploads)

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



class ComponentIsolationTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location('isolation_package_test', ROOT / 'release-package.py')
        self.package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.package)
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        for path in ('source', 'nuget', 'npm', 'artifacts', 'website-workspace', 'tools/node', 'tools/python', 'authority', 'probe'):
            (self.root / path).mkdir(parents=True)
        (self.root / 'source/Legend-Website').mkdir()
        (self.root / 'source/Legend-Website/package.json').write_text('{}')
        self.image = self.package.PACKAGE_TOOL_IMAGES['sdk']

    def command(self, phase='publish', component='website'):
        return self.package.isolated_component_command(component, self.root,
            ['python3', 'scripts/release-package.py'], phase=phase)

    def test_publish_inputs_readonly_and_modules_are_disposable_outputs(self):
        command = self.command()
        mounts = [command[index + 1] for index, value in enumerate(command) if value == '--mount']
        for target in ('/src', '/deps/nuget', '/deps/npm', '/src/Legend-Website/package.json', '/tools/node', '/tools/python', '/authority', '/probe'):
            self.assertTrue(next(row for row in mounts if ',dst=' + target + ',' in row).endswith(',readonly'))
        for target in ('/tmp/masterapp', '/src/Legend-Website'):
            self.assertTrue(any(row.endswith(',dst=' + target) for row in mounts))
        self.assertNotIn('docker.sock', '\n'.join(mounts))
        self.assertEqual(self.image, command[-3])

    def test_only_restore_has_network_and_observe_has_no_writable_bind_mount(self):
        for phase in ('restore', 'publish', 'observe'):
            command = self.command(phase)
            self.assertEqual('bridge' if phase == 'restore' else 'none', command[command.index('--network') + 1])
            if phase == 'observe':
                self.assertTrue(all(command[index + 1].endswith(',readonly')
                    for index, value in enumerate(command) if value == '--mount'))
            self.assertIn('--read-only', command)
            self.assertIn('no-new-privileges', command)

    def test_tokens_proxy_home_and_run_environment_are_never_forwarded(self):
        first = self.command()
        with patch.dict(os.environ, dict(GITHUB_TOKEN='test-token', AZURE_CLIENT_SECRET='test-secret',
                ACTIONS_RUNTIME_TOKEN='test-runtime', HTTPS_PROXY='test-proxy', HOME='/private-host',
                GITHUB_RUN_ATTEMPT='123', GITHUB_SHA='new-sha')):
            self.assertEqual(first, self.command())
        environment = [first[index + 1] for index, value in enumerate(first) if value == '--env']
        self.assertIn('npm_config_offline=true', environment)
        self.assertIn('HOME=/tmp/home', environment)
        self.assertFalse(any(value.startswith(('GITHUB_', 'AZURE_', 'ACTIONS_', 'HTTPS_PROXY=')) for value in environment))

    def test_escaped_mount_or_mutable_tool_reference_is_rejected(self):
        (self.root / 'nuget').rmdir()
        (self.root / 'nuget').symlink_to(self.root.parent, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, 'outside owned stage'):
            self.command()

    def test_tool_extraction_authenticates_exact_image_and_never_starts_container(self):
        shutil.rmtree(self.root / 'tools')
        image_id, container_id = 'sha256:' + 'c' * 64, 'd' * 64
        calls = []
        def run(command, **kwargs):
            calls.append(command)
            value = ''
            if command[:3] == ['docker', 'image', 'inspect']:
                value = json.dumps([dict(Os='linux', Architecture='amd64', Id=image_id)])
            elif command[:2] == ['docker', 'create']:
                value = container_id
            elif command[:3] == ['docker', 'container', 'inspect']:
                value = json.dumps([dict(Image=image_id, State=dict(Running=False))])
            elif command[:2] == ['docker', 'cp']:
                target = Path(command[-1]); target.mkdir()
                for relative in self.package.PACKAGE_TOOL_PATHS[target.name]:
                    file = target / relative; file.parent.mkdir(parents=True, exist_ok=True); file.write_bytes(b'pinned-tool')
            return type('Result', (), dict(stdout=value, returncode=0))()
        with patch.object(self.package.subprocess, 'run', side_effect=run):
            proof = self.package.prepare_isolated_tools(self.root)
        self.assertEqual(2, sum(command[:2] == ['docker', 'create'] for command in calls))
        self.assertEqual(2, sum(command[:2] == ['docker', 'rm'] for command in calls))
        self.assertFalse(any(command[:2] == ['docker', 'start'] for command in calls))
        self.assertEqual(self.package.PACKAGE_TOOL_IMAGES['node'], proof['tools']['node']['image'])
        self.assertEqual(image_id, proof['tools']['node']['imageId'])
        with self.assertRaises(FileExistsError):
            self.package.prepare_isolated_tools(self.root)

    def test_tool_identity_covers_content_modes_links_and_rejects_escape(self):
        import time
        tree = self.root / 'tools/node'
        binary = tree / 'compiler'; binary.write_bytes(b'original')
        link = tree / 'alias'; link.symlink_to('compiler')
        first = self.package.verified_tool_tree(tree, deadline=time.monotonic() + 10)
        self.assertEqual(first, self.package.verified_tool_tree(tree, deadline=time.monotonic() + 10))
        binary.write_bytes(b'changed')
        changed = self.package.verified_tool_tree(tree, deadline=time.monotonic() + 10)
        self.assertNotEqual(first['identity'], changed['identity'])
        binary.chmod(0o755)
        self.assertNotEqual(changed['identity'], self.package.verified_tool_tree(tree, deadline=time.monotonic() + 10)['identity'])
        link.unlink(); link.symlink_to('/etc/passwd')
        with self.assertRaisesRegex(ValueError, 'escapes verified tree'):
            self.package.verified_tool_tree(tree, deadline=time.monotonic() + 10)

    def test_source_materialization_keeps_exact_commit_without_host_state(self):
        repository = self.root / 'repository'
        repository.mkdir()
        def git(*args):
            return subprocess.check_output(['git', *args], cwd=repository, stderr=subprocess.DEVNULL, text=True).strip()
        git('init')
        git('config', 'user.name', 'Synthetic Test')
        git('config', 'user.email', 'synthetic@example.invalid')
        (repository / 'input.txt').write_text('committed input')
        git('add', 'input.txt')
        git('commit', '-m', 'synthetic source')
        revision = git('rev-parse', 'HEAD')
        git('remote', 'add', 'private', 'https://example.invalid/private')
        (repository / 'input.txt').write_text('uncommitted edit')
        (repository / 'private.txt').write_text('untracked synthetic credential')
        shutil.rmtree(self.root / 'source')
        with patch.object(self.package, 'ROOT', repository):
            result = self.package.materialize_component_source(revision, self.root)
        self.assertEqual(1, result['files'])
        self.assertEqual('committed input', (self.root / 'source/input.txt').read_text())
        self.assertFalse((self.root / 'source/private.txt').exists())
        self.assertNotIn('remote', (self.root / 'source/.git/config').read_text())
        self.assertEqual(revision, subprocess.check_output(['git', '-C', str(self.root / 'source'),
            'rev-parse', '--verify', 'HEAD^{commit}'], text=True).strip())

    def test_publish_scratch_drops_unmeasured_restore_state_and_preserves_cache(self):
        (self.root / 'website-workspace/.npmrc').write_text('injected restore setting')
        (self.root / 'website-workspace/generated.js').write_text('unmeasured restore output')
        (self.root / 'npm/retained-cache').write_text('retained verified dependency')
        self.package.prepare_website_workspace('website', self.root, phase='publish')
        self.assertEqual(['package.json'], sorted(path.name for path in (self.root / 'website-workspace').iterdir()))
        self.assertEqual('', (self.root / 'website-workspace/package.json').read_text())
        self.assertEqual('{}', (self.root / 'source/Legend-Website/package.json').read_text())
        self.assertEqual('retained verified dependency', (self.root / 'npm/retained-cache').read_text())

    def test_timeout_reclaims_only_exact_owned_container_and_never_retries(self):
        identity = 'b' * 64
        calls = []
        def run(command, **kwargs):
            calls.append(command)
            if command[:2] == ['docker', 'run']:
                Path(command[command.index('--cidfile') + 1]).write_text(identity)
                raise subprocess.TimeoutExpired(command, 1)
            self.assertEqual(['docker', 'rm', '--force', identity], command)
            return type('Result', (), {'returncode': 0})()
        with patch.object(self.package.subprocess, 'run', side_effect=run):
            with self.assertRaises(subprocess.TimeoutExpired):
                self.package.run_isolated_component('website', self.root, ['node', '--version'],
                    phase='publish', timeout=1)
        self.assertEqual(2, len(calls))
        evidence = list(self.root.glob('*.cleanup.json'))
        self.assertEqual(1, len(evidence))
        self.assertEqual(dict(containerId=identity, attempted=True, removed=True), json.loads(evidence[0].read_text()))

    def test_unknown_container_identity_never_triggers_cleanup_by_name(self):
        def run(command, **kwargs):
            Path(command[command.index('--cidfile') + 1]).write_text('other-container')
            raise KeyboardInterrupt()
        with patch.object(self.package.subprocess, 'run', side_effect=run) as execute:
            with self.assertRaises(KeyboardInterrupt):
                self.package.run_isolated_component('website', self.root, ['node', '--version'],
                    phase='publish', timeout=1)
            self.assertEqual(1, execute.call_count)
        self.assertFalse(list(self.root.glob('*.cleanup.json')))



class ComponentRestoreBoundaryTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location('restore_boundary_test', ROOT / 'release-package.py')
        self.package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.package)
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.stage = Path(self.temporary.name)
        self.obj = self.stage / 'artifacts/obj/App'
        self.obj.mkdir(parents=True)
        for name in ('project.assets.json', 'App.csproj.nuget.g.props', 'App.csproj.nuget.g.targets'):
            (self.obj / name).write_text('{}' if name.endswith('.json') else '<Project/>')
        self.material = dict(projectPaths=['App/App.csproj'])

    def test_only_verified_restore_inputs_survive_and_original_evidence_is_preserved(self):
        (self.obj / 'unmeasured-restore-script').write_text('unmeasured')
        (self.stage / 'artifacts/fake-compiled.dll').write_text('must not suppress compilation')
        identity = self.package.freeze_component_restore(self.stage, self.material)
        self.assertEqual(64, len(identity))
        self.assertEqual(3, len(list(self.obj.iterdir())))
        self.assertFalse((self.stage / 'artifacts/fake-compiled.dll').exists())
        self.assertTrue((self.stage / 'restore-observation/fake-compiled.dll').exists())
        self.assertTrue((self.stage / 'restore-observation/obj/App/unmeasured-restore-script').exists())

    def test_superseded_cache_is_unmounted_but_transitive_versions_remain(self):
        cache = self.stage / 'nuget'
        for relative in ('example/1.0', 'example/2.0', 'transitive/1.0'):
            path = cache / relative
            path.mkdir(parents=True)
            (path / 'input.dll').write_text(relative)
        for name, package in (('App', 'example/2.0'), ('Shared', 'transitive/1.0')):
            path = self.stage / 'artifacts/obj' / name / 'project.assets.json'
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(dict(version=4, libraries={package: dict(type='package', path=package)})))
        result = self.package.narrow_restore_cache(self.stage)
        self.assertEqual(['example/1.0'], result['excluded'])
        self.assertEqual(['example/2.0', 'transitive/1.0'], result['selected'])
        self.assertEqual('selected-unverified', result['state'])
        self.assertFalse((cache / 'example/1.0').exists())
        self.assertEqual('example/1.0', (self.stage / 'restore-cache-observation/example/1.0/input.dll').read_text())
        self.assertTrue((cache / 'transitive/1.0/input.dll').is_file())

    def test_cache_selection_rejects_traversal_and_symlinks_before_mutation(self):
        cache = self.stage / 'nuget'
        cache.mkdir()
        for relative in ('../escape', 'example/..', '/example/1.0'):
            (self.obj / 'project.assets.json').write_text(json.dumps(dict(version=4,
                libraries={relative: dict(type='package', path=relative)})))
            with self.assertRaisesRegex(ValueError, 'Unsafe restore selection package path'):
                self.package.narrow_restore_cache(self.stage)
            self.assertFalse((self.stage / 'restore-cache-observation').exists())
        (cache / 'alias').symlink_to(self.obj, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, 'Unsafe restore selection entry'):
            self.package.narrow_restore_cache(self.stage)
        self.assertTrue(cache.is_dir())

    def test_untrusted_inventory_and_symlink_do_not_replace_original_state(self):
        for projects in (['../outside.csproj'], ['App/App.csproj', 'Other/App.csproj'], []):
            with self.subTest(projects=projects):
                clean = self.stage / 'publish-inputs'
                if clean.exists(): shutil.rmtree(clean)
                with self.assertRaises(ValueError):
                    self.package.freeze_component_restore(self.stage, dict(projectPaths=projects))
                self.assertTrue(self.obj.is_dir())
        clean = self.stage / 'publish-inputs'
        if clean.exists(): shutil.rmtree(clean)
        (self.obj / 'project.assets.json').unlink()
        (self.obj / 'project.assets.json').symlink_to('/etc/passwd')
        with self.assertRaisesRegex(ValueError, 'Unsafe verified restore'):
            self.package.freeze_component_restore(self.stage, self.material)
        self.assertTrue(self.obj.is_dir())


class ResolvedPackageMaterialTests(unittest.TestCase):
    def setUp(self):
        import base64
        spec = importlib.util.spec_from_file_location('resolved_package_test', ROOT / 'release-package.py')
        self.package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.package)
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.repo, self.cache, self.obj, self.sdk = (self.root / name for name in ('repo', 'cache', 'artifacts', 'sdk'))
        for path in (self.repo / 'App', self.repo / 'Shared', self.sdk):
            path.mkdir(parents=True)
        (self.sdk / 'runtime.json').write_text('{}')
        for name in ('App', 'Shared'):
            (self.repo / name / (name + '.csproj')).write_text('<Project/>')
        self.directory = self.cache / 'example/1.0.0'
        self.directory.mkdir(parents=True)
        self.archive = self.directory / 'example.1.0.0.nupkg'
        (self.directory / 'lib').mkdir()
        (self.directory / 'lib/library.dll').write_bytes(b'compiler input')
        with zipfile.ZipFile(self.archive, 'w') as archive:
            archive.writestr('lib/library.dll', b'compiler input')
        self.library = dict(type='package', path='example/1.0.0', files=['lib/library.dll'],
                            sha512=base64.b64encode(hashlib.sha512(self.archive.read_bytes()).digest()).decode())
        self.assets = {}
        for name in ('App', 'Shared'):
            project = self.repo / name / (name + '.csproj')
            references = {} if name == 'Shared' else {str(self.repo / 'Shared/Shared.csproj'): {'projectPath': str(self.repo / 'Shared/Shared.csproj')}}
            data = dict(version=3, packageFolders={str(self.cache): {}}, project={'version': '1.0.0', 'restore': {'projectPath': str(project),
                'frameworks': {'net10.0': {'projectReferences': references}}},
                'frameworks': {'net10.0': {'runtimeIdentifierGraphPath': str(self.sdk / 'runtime.json')}}},
                targets={'net10.0': {'Example/1.0.0': {'compile': {'lib/library.dll': {}}}}},
                libraries={'Example/1.0.0': dict(self.library)})
            path = self.obj / 'obj' / name / 'project.assets.json'
            path.parent.mkdir(parents=True)
            path.write_text(json.dumps(data))
            for suffix in ('.nuget.g.props', '.nuget.g.targets'):
                (path.parent / (name + '.csproj' + suffix)).write_text('<Project/>')
            self.assets[name] = path
        self.package.ROOT = self.repo
        self.package.APPS = {'app': ('App/App.csproj', 'app.zip', False)}

    def identity(self):
        return self.package.resolved_restore_identity('app', self.obj, [self.cache], self.sdk)

    def test_verified_transitive_graph_is_stable_but_dependency_and_framework_changes_invalidate(self):
        first = self.identity()
        self.assertEqual(first, self.identity())
        self.assertEqual(2, first['projectCount'])
        self.assertEqual(1, first['packageCount'])
        data = json.loads(self.assets['Shared'].read_text())
        data['project']['frameworks']['net10.0']['frameworkReferences'] = {'Microsoft.AspNetCore.App': {}}
        self.assets['Shared'].write_text(json.dumps(data))
        self.assertNotEqual(first['identity'], self.identity()['identity'])
        (self.sdk / 'runtime.json').write_text('{"changed":true}')
        self.assertNotEqual(first['identity'], self.identity()['identity'])

    def test_v4_framework_aliases_remain_distinct_and_schema_changes_invalidate(self):
        first = self.identity()['identity']
        data = json.loads(self.assets['App'].read_text())
        data['version'] = 4
        data['project']['frameworks']['net10.0']['targetAlias'] = 'net10.0'
        self.assets['App'].write_text(json.dumps(data))
        second = self.identity()['identity']
        self.assertNotEqual(first, second)
        data['project']['frameworks']['alternate'] = dict(data['project']['frameworks']['net10.0'], targetAlias='alternate')
        data['targets']['alternate'] = {}
        self.assets['App'].write_text(json.dumps(data))
        self.assertNotEqual(second, self.identity()['identity'])
        data['packageFolders'][str(self.root / 'unverified-cache')] = {}
        self.assets['App'].write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'search roots differ'):
            self.identity()

    def test_generated_metadata_is_bound_and_undeclared_cache_roots_are_rejected(self):
        first = self.identity()['identity']
        (self.directory / '.nupkg.metadata').write_text('{"version":2}')
        self.assertNotEqual(first, self.identity()['identity'])
        (self.cache / 'undeclared').mkdir()
        with self.assertRaisesRegex(ValueError, 'Unmeasured input'):
            self.identity()

    def test_generated_import_mutation_changes_identity(self):
        first = self.identity()['identity']
        path = self.assets['Shared'].parent / 'Shared.csproj.nuget.g.targets'
        path.write_text('<Project><Import Project="unexpected.targets"/></Project>')
        self.assertNotEqual(first, self.identity()['identity'])

    def test_extra_cache_file_and_omitted_analyzer_input_are_rejected(self):
        injected = self.directory / 'lib/injected.dll'
        injected.write_bytes(b'injected compiler input')
        with self.assertRaisesRegex(ValueError, 'inventory differs'):
            self.identity()
        injected.unlink()
        data = json.loads(self.assets['App'].read_text())
        data['targets']['net10.0']['Example/1.0.0']['analyzers'] = {'analyzers/missing.dll': {}}
        self.assets['App'].write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'compiler input absent'):
            self.identity()

    def test_nuget_empty_asset_group_is_not_an_unverified_compiler_file(self):
        data = json.loads(self.assets['App'].read_text())
        data['targets']['net10.0']['Example/1.0.0']['compile'] = {'lib/net10.0/_._': {}}
        self.assets['App'].write_text(json.dumps(data))
        self.assertEqual(1, self.identity()['packageCount'])
        data['targets']['net10.0']['Example/1.0.0']['compile'] = {'../_._': {}}
        self.assets['App'].write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'escapes owning root'):
            self.identity()

    def test_shared_count_byte_and_deadline_budgets_fail_closed(self):
        for key, value, reason in (('projects', 1, 'project budget'), ('packages', 0, 'package budget'),
                                   ('entries', 0, 'inventory invalid'), ('fileBytes', 1, 'file budget'),
                                   ('totalBytes', 1, 'byte budget'), ('seconds', 0, 'deadline')):
            with self.subTest(boundary=key), patch.dict(self.package.RESTORE_MATERIAL_LIMITS, {key: value}):
                with self.assertRaisesRegex(ValueError, reason):
                    self.identity()

    def test_restore_material_symlink_escape_is_rejected(self):
        path = self.assets['App']
        outside = self.root / 'outside.json'
        outside.write_bytes(path.read_bytes())
        path.unlink()
        path.symlink_to(outside)
        with self.assertRaisesRegex(ValueError, 'symlink escapes'):
            self.identity()

    def test_archive_and_extracted_compile_input_corruption_are_rejected(self):
        original = self.archive.read_bytes()
        self.archive.write_bytes(original + b'tampered')
        with self.assertRaisesRegex(ValueError, 'archive digest mismatch'):
            self.identity()
        self.archive.write_bytes(original)
        (self.directory / 'lib/library.dll').write_bytes(b'tampered extracted generator')
        with self.assertRaisesRegex(ValueError, 'Extracted package content'):
            self.identity()

    def test_missing_transitive_restore_and_unsupported_schema_fail_closed(self):
        original = self.assets['Shared'].read_text()
        self.assets['Shared'].unlink()
        with self.assertRaises(FileNotFoundError):
            self.identity()
        data = json.loads(original)
        data['version'] = 99
        self.assets['Shared'].write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'Unsupported restore material'):
            self.identity()

    def test_untrusted_paths_and_conflicting_dependency_hashes_are_rejected(self):
        original = json.loads(self.assets['App'].read_text())
        for field, value in (('path', '../outside'), ('sha512', 'not-a-digest'),
                             ('files', ['../outside'])):
            data = json.loads(json.dumps(original))
            data['libraries']['Example/1.0.0'][field] = value
            self.assets['App'].write_text(json.dumps(data))
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.identity()
        self.assets['App'].write_text(json.dumps(original))
        original['project']['frameworks']['net10.0']['runtimeIdentifierGraphPath'] = str(self.root / 'outside')
        self.assets['App'].write_text(json.dumps(original))
        with self.assertRaisesRegex(ValueError, 'outside measured SDK'):
            self.identity()


class PackageContractTests(unittest.TestCase):
    def test_candidate_component_build_requires_no_evidence_credential(self):
        spec = importlib.util.spec_from_file_location('release_package_test', ROOT / 'release-package.py')
        package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(package)
        with tempfile.TemporaryDirectory() as temporary, \
             patch.dict(os.environ, {'GITHUB_ACTIONS': 'true', 'GITHUB_EVENT_NAME': 'pull_request'}, clear=True), \
             patch.object(package, 'validate_revision', side_effect=lambda value: value), \
             patch.object(package, 'contract_hash', return_value='b' * 64), \
             patch.object(package, 'package_identity', return_value='c' * 64), \
             patch.object(package._RELEASE_AUTHORITY, 'require_readiness', side_effect=AssertionError('Credentialed lookup in build')) as lookup, \
             patch.object(package, 'build_migration_bundle', side_effect=lambda output: (output / package.MIGRATION_BUNDLE).write_bytes(b'candidate')) as build:
            receipt = package.build_component('a' * 40, 'migration', Path(temporary))
            self.assertEqual(hashlib.sha256(b'candidate').hexdigest(), receipt['sha256'])
            self.assertEqual(1, build.call_count)
            lookup.assert_not_called()

    def test_application_component_delegates_once_to_isolated_owner(self):
        spec = importlib.util.spec_from_file_location('isolated_owner_test', ROOT / 'release-package.py')
        package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(package)
        with tempfile.TemporaryDirectory() as folder, \
             patch.object(package, 'validate_revision', side_effect=lambda value: value), \
             patch.object(package, 'contract_hash', return_value='b' * 64), \
             patch.object(package, 'package_identity', return_value='c' * 64), \
             patch.object(package, 'build_static', side_effect=AssertionError('host build')), \
             patch.object(package, 'build_dotnet', side_effect=AssertionError('host build')), \
             patch.object(package, 'build_isolated_component', side_effect=lambda revision, component, output:
                 (output / package.component_file(component)).write_bytes(b'verified isolated bytes')) as isolated:
            output = Path(folder)
            for component in package.APPS:
                receipt = package.build_component('a' * 40, component, output)
                self.assertEqual(hashlib.sha256(b'verified isolated bytes').hexdigest(), receipt['sha256'])
                isolated.assert_called_with('a' * 40, component, output)
            self.assertEqual(len(package.APPS), isolated.call_count)

    def test_content_component_keeps_actual_producer_and_rejects_unsafe_reuse(self):
        spec = importlib.util.spec_from_file_location('release_package_test', ROOT / 'release-package.py')
        package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(package)
        candidate, producer = 'a' * 40, 'b' * 40
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            component = 'portal'
            path = directory / package.component_file(component)
            with zipfile.ZipFile(path, 'w') as archive:
                archive.writestr('wwwroot/_deployment-provenance.json', json.dumps({'releaseSha': producer}))
            receipt = dict(schema=package.CONTENT_COMPONENT_SCHEMA, producerRevision=producer,
                contentIdentity='c' * 64, executionIdentity='e' * 64, component=component, file=path.name,
                sha256=package.sha256_file(path))
            receipt_path = directory / (component + '.component.json')
            receipt_path.write_text(json.dumps(receipt))
            original = path.read_bytes()
            with patch.object(package, 'validate_revision', side_effect=lambda value: value), \
                 patch.object(package._RELEASE_AUTHORITY, 'package_component_manifest',
                    return_value=dict(contentIdentity='c' * 64, reusable=True)) as closure:
                with self.assertRaisesRegex(ValueError, 'execution environment unproven'):
                    package.verify_component(candidate, component, directory)
                with self.assertRaisesRegex(ValueError, 'content receipt mismatch'):
                    package.verify_component(candidate, component, directory, execution_identity='f' * 64)
                self.assertEqual(receipt, package.verify_component(candidate, component, directory, execution_identity='e' * 64))
                self.assertEqual(original, path.read_bytes())
                self.assertEqual(producer, package.embedded_revision(path, False))
                closure.side_effect = [dict(contentIdentity='d' * 64, reusable=True), dict(contentIdentity='c' * 64, reusable=True)]
                with self.assertRaisesRegex(ValueError, 'dependencies changed'):
                    package.verify_component(candidate, component, directory, execution_identity='e' * 64)
                closure.side_effect = None
                closure.return_value = dict(contentIdentity='c' * 64, reusable=False)
                with self.assertRaisesRegex(ValueError, 'closure unproven'):
                    package.verify_component(candidate, component, directory, execution_identity='e' * 64)
                closure.return_value = dict(contentIdentity='c' * 64, reusable=True)
                receipt['producerRevision'] = candidate
                receipt_path.write_text(json.dumps(receipt))
                with self.assertRaisesRegex(ValueError, 'embedded producer mismatch'):
                    package.verify_component(candidate, component, directory, execution_identity='e' * 64)
                receipt['producerRevision'] = producer
                receipt['sha256'] = 'f' * 64
                receipt_path.write_text(json.dumps(receipt))
                with self.assertRaisesRegex(ValueError, 'content receipt mismatch'):
                    package.verify_component(candidate, component, directory, execution_identity='e' * 64)

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
        spec = importlib.util.spec_from_file_location('release_package_test', ROOT / 'release-package.py')
        package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(package)
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)
            (output / package.MIGRATION_BUNDLE).write_bytes(b'isolated fixture')
            with patch.object(package, 'run') as execute:
                package.build_migration_bundle(output)
            calls = [call.args for call in execute.call_args_list]
            startup = 'scripts/MigrationReleaseProbe/MigrationReleaseProbe.csproj'
            self.assertEqual(('dotnet', 'restore', startup, '--nologo'), calls[0])
            self.assertEqual(('dotnet', 'tool', 'restore'), calls[1])
            self.assertEqual(startup, calls[2][calls[2].index('--startup-project') + 1])
            self.assertEqual(3, len(calls))
            self.assertNotIn('AgentPortal/AgentPortal.csproj', sum((list(row) for row in calls), []))


    def test_rehearsed_migration_component_is_promoted_without_rebuilding(self):
        spec = importlib.util.spec_from_file_location('release_package_test', ROOT / 'release-package.py')
        package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(package)
        revision, body = 'a' * 40, b'exact validated migration bytes'
        digest = hashlib.sha256(body).hexdigest()
        receipt = dict(schema=package.COMPONENT_SCHEMA, applicationReleaseSha=revision,
            packageContractSha256='b' * 64, packageIdentity='c' * 64,
            component='migration', file=package.MIGRATION_BUNDLE, sha256=digest)
        proof = {'receipt': {'rehearsalSource': {'runId': 91, 'artifact': 'retained-rehearsal', 'artifactId': 123},
                             'rehearsal': {'bundleDigest': digest}}}
        def download(repo, run, name, root, *, artifact_id):
            self.assertEqual(123, artifact_id)
            self.assertEqual((91, 'retained-rehearsal'), (run, name))
            (root / 'migration').mkdir()
            (root / 'migration' / package.MIGRATION_BUNDLE).write_bytes(body)
            (root / 'migration/migration.component.json').write_text(json.dumps(receipt))
        with tempfile.TemporaryDirectory() as temporary, \
             patch.dict(os.environ, {'GITHUB_ACTIONS': 'true', 'GITHUB_EVENT_NAME': 'pull_request', 'GITHUB_REPOSITORY': 'MYLEGND/masterapp'}), \
             patch.object(package, 'validate_revision', return_value=revision), \
             patch.object(package, 'contract_hash', return_value='b' * 64), \
             patch.object(package, 'package_identity', return_value='c' * 64), \
             patch.object(package._RELEASE_AUTHORITY, 'require_readiness', return_value=proof), \
             patch.object(package._RELEASE_AUTHORITY, '_download_run_artifact', side_effect=download) as restore, \
             patch.object(package, 'build_migration_bundle') as build:
            output = Path(temporary)
            self.assertEqual(receipt, package.promote_rehearsed_migration(revision, output, proof))
            self.assertEqual(body, (output / package.MIGRATION_BUNDLE).read_bytes())
            self.assertEqual('reused-success', json.loads((output / 'migration.reuse.json').read_text())['state'])
            self.assertEqual(1, restore.call_count)
            build.assert_not_called()
            # A new job attempt retains the same validated bytes without trying
            # to recreate the previous attempt's immutable upload identity.
            first = package.promoted_migration_artifact(revision, 1)
            second = package.promoted_migration_artifact(revision, 2)
            self.assertNotEqual(first, second)
            self.assertEqual(second, package.promoted_migration_artifact(revision, 2))
            self.assertEqual(receipt, package.promote_rehearsed_migration(revision, output, proof))
            self.assertEqual(body, (output / package.MIGRATION_BUNDLE).read_bytes())
            build.assert_not_called()

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
                self.assertTrue(payload['liveProven'])
                self.assertTrue(payload['durableReceiptProven'])
                self.assertEqual('publication', payload['phase'])
                self.assertEqual('a' * 40, payload['candidateRevision'])
                self.assertEqual(key, payload['target'])
                self.assertNotIn('success', payload)

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
        self.assertTrue(all(
            result['liveProven'] and result['durableReceiptProven']
            for result in results.values()
        ))

    def test_receipt_pending_is_never_reported_as_deployment_success(self):
        keys = ('portal',)
        names = [deploy.TARGETS['portal']['releaseName']]
        with tempfile.TemporaryDirectory() as directory, \
             patch.object(deploy._RELEASE_AUTHORITY, 'selected_release_target_keys', return_value=keys), \
             patch.object(deploy, 'publish_prepared_target', return_value='deployed-receipt-pending'):
            results = deploy.publish_prepared_targets_parallel(
                names,
                'a' * 40,
                Path(directory),
                self.plan(keys),
                Path(directory) / 'results',
            )
            payload = results['portal']
            self.assertTrue(payload['liveProven'])
            self.assertFalse(payload['durableReceiptProven'])
            self.assertEqual('deployed-receipt-pending', payload['outcome'])
            self.assertNotIn('success', payload)

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
            portal = json.loads((root / 'portal.json').read_text())
            self.assertTrue(portal['liveProven'])
            self.assertTrue(portal['durableReceiptProven'])
            failed = json.loads((root / 'protect.json').read_text())
            self.assertFalse(failed['liveProven'])
            self.assertFalse(failed['durableReceiptProven'])
            self.assertNotIn('success', failed)
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

    def test_finalizer_reconstructs_missing_receipt_without_upload_or_sibling_replay(self):
        plan = self.plan()
        journals, providers = {}, {}
        for index, target in enumerate(plan['targets']):
            key = target['app']
            class Journal:
                baseline = 'b' * 40
                intent = {'baselineDeploymentIds': ['old']}
                def __init__(self, fail_first):
                    self.fail_first, self.receipt_attempts = fail_first, 0
                def record_success(self, ids):
                    self.receipt_attempts += 1
                    if self.fail_first and self.receipt_attempts == 1:
                        raise RuntimeError('lost receipt acknowledgment')
                    return {'phase': 'success'}
            journals[key] = Journal(index == 1)
            provider = FakeAzure([[row('old', 4), row('new', 4)]], [True])
            provider.revision = 'a' * 40
            provider.observed_revision = lambda: 'a' * 40
            providers[key] = provider
        original_reconcile = deploy.reconcile
        def bounded(provider, **kwargs):
            self.assertIsNotNone(kwargs.get('journal'))
            return original_reconcile(provider, clock=lambda: provider.now,
                                      sleep=provider.sleep, interval=1, **kwargs)
        with patch.object(deploy, 'verify_package', return_value='c' * 64), \
             patch.object(deploy, 'operation_journal', side_effect=lambda key, *_: journals[key]), \
             patch.object(deploy, 'target_azure', side_effect=lambda key, *_: providers[key]), \
             patch.object(deploy, 'reconcile', side_effect=bounded):
            deploy.finalize_prepared_transaction(plan, Path('/packages'), 'a' * 40, sleep=lambda _: None)
        self.assertEqual([1, 2], [j.receipt_attempts for j in journals.values()])
        self.assertEqual([0, 0], [p.uploads for p in providers.values()])

    def test_finalization_is_read_only_and_retries_only_unresolved_targets(self):
        plan = self.plan()
        calls = {}
        sleeps = []
        journal = object()

        class FinalizeAzure:
            def __init__(self, key):
                self.key = key

        def target_azure(key, *_):
            return FinalizeAzure(key)

        def reconcile(azure, **kwargs):
            calls[azure.key] = calls.get(azure.key, 0) + 1
            self.assertTrue(kwargs['reconcile_only'])
            self.assertEqual(deploy.FINALIZE_RECONCILE_TIMEOUT_SECONDS, kwargs['timeout'])
            self.assertEqual(1, kwargs['max_status_failures'])
            self.assertIs(journal, kwargs['journal'])
            self.assertTrue(kwargs['require_receipt'])
            if azure.key == plan['targets'][1]['app'] and calls[azure.key] < 3:
                raise deploy.DeploymentReconciliationRequired('receipt pending')
            return 'preserved'

        with patch.object(deploy, 'verify_package', return_value='c' * 64), \
             patch.object(deploy, 'operation_journal', return_value=journal):
            with patch.object(deploy, 'target_azure', side_effect=target_azure):
                with patch.object(deploy, 'reconcile', side_effect=reconcile):
                    deploy.finalize_prepared_transaction(
                        plan, Path('/packages'), 'a' * 40, sleep=sleeps.append)

        self.assertEqual(1, calls[plan['targets'][0]['app']])
        self.assertEqual(3, calls[plan['targets'][1]['app']])
        self.assertEqual([10, 20], sleeps)

    def test_finalization_drift_fails_without_retry(self):
        plan = self.plan([list(deploy.TARGETS)[0]])
        with patch.object(deploy, 'verify_package', return_value='c' * 64), \
             patch.object(deploy, 'operation_journal', return_value=object()):
            with patch.object(deploy, 'reconcile', side_effect=deploy.DeploymentDrift('drift')) as reconcile:
                with self.assertRaisesRegex(deploy.DeploymentDrift, 'drift'):
                    deploy.finalize_prepared_transaction(
                        plan, Path('/packages'), 'a' * 40, sleep=lambda _: None)
        self.assertEqual(1, reconcile.call_count)

    def test_finalization_cannot_commit_without_a_durable_journal(self):
        with patch.object(deploy, 'verify_package', return_value='c' * 64), \
             patch.object(deploy, 'operation_journal', return_value=None), \
             patch.object(deploy, 'reconcile') as reconcile:
            with self.assertRaisesRegex(ValueError, 'durable deployment journal'):
                deploy.finalize_prepared_transaction(
                    self.plan(), Path('/packages'), 'a' * 40, sleep=lambda _: None)
        reconcile.assert_not_called()

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


class DeploymentReadinessTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location('readiness_prepublication_test', ROOT / 'release-prepublication.py')
        self.owner = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.owner)
        self.targets = [row['releaseName'] for row in deploy.TARGETS.values()]
        self.profile = deploy._RELEASE_AUTHORITY.release_runtime_profile(self.targets)
        self.env = {'RELEASE_RESOURCE_GROUP': self.profile['resourceGroup'],
                    'DATABASE_AUTHORITY': self.profile['databaseAuthority']}

    def test_target_readiness_uses_only_provider_reads_and_canonical_configuration(self):
        calls = []
        def observe(*args):
            calls.append(args)
            if args[:2] == ('webapp', 'show'):
                name = args[args.index('-n') + 1]
                return {'id': '/subscriptions/test/resourceGroups/test/providers/Microsoft.Web/sites/' + name, 'state': 'Running'}
            self.assertEqual(('webapp', 'log', 'deployment', 'list'), args[:4])
            return []
        with patch.dict(os.environ, self.env), patch.object(self.owner, 'az_json', side_effect=observe), \
             patch.object(self.owner, 'source_authority') as source, patch.object(self.owner, 'app_settings'), \
             patch.object(self.owner, 'configure_target') as mutate:
            result = self.owner.deployment_readiness(self.targets)
        self.assertEqual('executed-success', result['state'])
        self.assertEqual(set(self.targets), {row['target'] for row in result['targets']})
        self.assertEqual(2 * len(self.targets), len(calls))
        self.assertEqual(1, source.call_count)
        mutate.assert_not_called()

    def test_missing_target_blocks_without_configuration_mutation(self):
        with patch.dict(os.environ, self.env), patch.object(self.owner, 'az_json', return_value={}), \
             patch.object(self.owner, 'source_authority'), patch.object(self.owner, 'configure_target') as mutate:
            with self.assertRaisesRegex(RuntimeError, 'missing or not running'):
                self.owner.deployment_readiness(self.targets[:1])
        mutate.assert_not_called()

    def test_wrong_environment_is_rejected_before_provider_access(self):
        with patch.dict(os.environ, self.env | {'RELEASE_RESOURCE_GROUP': 'wrong'}), \
             patch.object(self.owner, 'az_json') as provider:
            with self.assertRaisesRegex(RuntimeError, 'canonical inventory'):
                self.owner.deployment_readiness(self.targets)
        provider.assert_not_called()


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


class MigrationMetadataAdmissionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location(
            'prepublication_metadata_admission_test', ROOT / 'release-prepublication.py')
        cls.owner = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.owner)

    def test_probe_restore_uses_immutable_id_and_rejects_missing_identity(self):
        owner = self.owner
        from types import SimpleNamespace
        from unittest.mock import Mock
        from contextlib import nullcontext
        download = Mock()
        budget = Mock(side_effect=lambda deadline: nullcontext())
        with patch.dict(os.environ, {'GITHUB_REPOSITORY': 'owner/repo'}), \
             patch.object(owner.time, 'monotonic', return_value=1000), \
             patch.object(owner, 'release_authority', return_value=SimpleNamespace(_download_run_artifact=download, evidence_lookup_budget=budget)):
            owner.restore_migration_probe(dict(runId=7, artifact='probe', artifactId=19))
            download.assert_called_once_with('owner/repo', 7, 'probe', Path('/tmp/migration-probe'), artifact_id=19)
            for value in (None, 0, -1, True, '19'):
                with self.subTest(value=value), self.assertRaisesRegex(RuntimeError, 'immutable identity'):
                    owner.restore_migration_probe(dict(runId=7, artifact='probe', artifactId=value))
            self.assertEqual(1, download.call_count)
            budget.assert_called_once_with(1240)

    def test_registered_metadata_and_context_changes_require_probe(self):
        owner = self.owner
        paths = (
            'Infrastructure/Data/LegacyMetaAttributionMigrationMetadata.cs\n'
            'Infrastructure/Data/MasterAppDbContext.cs\n'
        )
        with patch.object(owner, 'run', return_value=paths) as git:
            self.assertTrue(owner.migration_metadata_changed('a' * 40, 'b' * 40))
        git.assert_called_once_with([
            'git', 'diff', '--name-only', 'a' * 40, 'b' * 40,
            '--', 'Infrastructure/Data', 'Infrastructure/Migrations',
        ], capture=True)

    def test_delayed_probe_provider_exhausts_one_budget_before_migration(self):
        from types import SimpleNamespace
        owner = self.owner
        authority = owner.release_authority()
        budget = authority.evidence_lookup_budget
        elapsed, calls = [0.0], []
        def get(*args, **kwargs):
            calls.append(kwargs['timeout'])
            elapsed[0] += min(119, kwargs['timeout'])
            return SimpleNamespace(returncode=1, stderr='HTTP 503')
        environment = dict(PRESERVE_LIVE_TARGETS='false', SELECTED_DATABASE_DEPENDENT='true',
            EXPECTED_DB_BASE_SHA='a' * 40, APPLICATION_RELEASE_SHA='b' * 40,
            DATABASE_AUTHORITY='portal', GITHUB_REPOSITORY='owner/repo', MIGRATION_READINESS_PENDING='false')
        with tempfile.TemporaryDirectory() as temporary, \
             patch.dict(os.environ, environment), \
             patch.object(owner, 'git_ok', return_value=True), \
             patch.object(owner, 'changed_migrations', return_value=[]), \
             patch.object(owner, 'release_proven'), \
             patch.object(owner, 'run', return_value=json.dumps(dict(runId=7, artifact='probe', artifactId=19))), \
             patch.object(owner, 'Path', return_value=Path(temporary)), \
             patch.object(owner, 'release_authority', return_value=authority), \
             patch.object(owner.time, 'monotonic', side_effect=lambda: elapsed[0]), \
             patch.object(authority, 'evidence_lookup_budget', side_effect=lambda deadline: budget(deadline, clock=lambda: elapsed[0])), \
             patch.object(authority.time, 'sleep', side_effect=lambda delay: elapsed.__setitem__(0, elapsed[0] + delay)), \
             patch.object(authority.subprocess, 'run', side_effect=get), \
             patch.object(owner, '_invoke_migration_bundle') as migrate:
            with self.assertRaises(authority.EvidenceLookupUnavailable):
                owner.run_migration_lane()
            self.assertEqual(240, elapsed[0])
            self.assertEqual(2, len(calls))
            migrate.assert_not_called()

    def test_designer_only_migration_metadata_change_enters_existing_probe(self):
        owner = self.owner
        with patch.object(owner, 'run', return_value=(
            'Infrastructure/Migrations/20260516100000_AddMetaAttributionReconciliation.Designer.cs\n')):
            self.assertTrue(owner.migration_metadata_changed('a' * 40, 'b' * 40))

    def test_unrelated_migration_source_metadata_is_not_misclassified(self):
        owner = self.owner
        with patch.object(owner, 'run', return_value=(
            'Infrastructure/Migrations/README.md\n'
            'Infrastructure/Migrations/AnyOldHelper.cs\n')):
            self.assertFalse(owner.migration_metadata_changed('a' * 40, 'b' * 40))

    def test_unrelated_data_changes_do_not_trigger_schema_probe(self):
        owner = self.owner
        with patch.object(owner, 'run', return_value=(
            'Infrastructure/Data/SomeRepository.cs\n'
            'Infrastructure/Data/Info.txt\n')):
            self.assertFalse(owner.migration_metadata_changed('a' * 40, 'b' * 40))

    def test_metadata_only_release_reconciles_schema_without_replay(self):
        owner = self.owner
        environment = {
            'PRESERVE_LIVE_TARGETS': 'false',
            'SELECTED_DATABASE_DEPENDENT': 'true',
            'EXPECTED_DB_BASE_SHA': 'a' * 40,
            'APPLICATION_RELEASE_SHA': 'b' * 40,
            'DATABASE_AUTHORITY': 'masterapp-portal',
            'GITHUB_REPOSITORY': 'MYLEGND/masterapp',
            # Fresh production SQL readiness, not changed source metadata,
            # authorizes the actual pending migration to be reconciled.
            'MIGRATION_READINESS_PENDING': 'true',
        }
        def read(command, **kwargs):
            if 'resolve' in command:
                return json.dumps({'runId': 123, 'artifact': 'validated-probe'})
            if command[:3] == ['git', 'rev-parse', 'HEAD']:
                return 'c' * 40
            return ''

        with (
            patch.dict(os.environ, environment),
            patch.object(owner, 'git_ok', return_value=True),
            patch.object(owner, 'changed_migrations', return_value=[]),
            patch.object(owner, 'migration_metadata_changed', return_value=True),
            patch.object(owner, 'release_proven') as release_proof,
            patch.object(owner, 'run', side_effect=read) as command,
            patch.object(owner, 'restore_migration_probe'),
            patch.object(owner, '_invoke_migration_bundle') as migrate,
        ):
            result = owner.run_migration_lane()
        self.assertEqual({'status': 'reconciled', 'changedMigrations': []}, result)
        release_proof.assert_called_once()
        migrate.assert_called_once()
        self.assertTrue(any('verify' in call.args[0] for call in command.call_args_list))

    def test_non_schema_changes_still_require_fresh_schema_reconciliation(self):
        owner = self.owner
        environment = {
            'PRESERVE_LIVE_TARGETS': 'false',
            'SELECTED_DATABASE_DEPENDENT': 'true',
            'EXPECTED_DB_BASE_SHA': 'a' * 40,
            'APPLICATION_RELEASE_SHA': 'b' * 40,
            'MIGRATION_READINESS_PENDING': 'false',
            'DATABASE_AUTHORITY': 'masterapp-portal',
            'GITHUB_REPOSITORY': 'MYLEGND/masterapp',
        }
        with (
            patch.dict(os.environ, environment),
            patch.object(owner, 'git_ok', return_value=True),
            patch.object(owner, 'changed_migrations', return_value=[]),
            patch.object(owner, 'migration_metadata_changed', return_value=False),
            patch.object(owner, '_invoke_migration_bundle') as migrate,
            patch.object(owner, 'release_proven') as release_proof,
            patch.object(owner, 'run', return_value=json.dumps({'runId': 123, 'artifact': 'validated-probe'})),
            patch.object(owner, 'restore_migration_probe'),
        ):
            result = owner.run_migration_lane()
        self.assertEqual({'status': 'reconciled', 'changedMigrations': []}, result)
        migrate.assert_called_once()  # Canonical ready-state tests prove zero SQL execution.
        release_proof.assert_called_once()


class RedactedPrepublicationDiagnosticsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import io
        from types import SimpleNamespace
        cls.io = io
        cls.SimpleNamespace = SimpleNamespace
        spec = importlib.util.spec_from_file_location(
            'prepublication_diagnostics_test', ROOT / 'release-prepublication.py')
        cls.owner = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.owner)

    def test_canonical_schema_reason_is_preserved_without_retry(self):
        owner = self.owner
        text = ('Schema probe runtime invalid operation; history status unknown'
                '; preserve prior evidence and reconcile without replay.')
        result = subprocess.CompletedProcess([], 1, '', text)
        with patch.object(owner.subprocess, 'run', return_value=result) as call:
            with self.assertRaisesRegex(RuntimeError, 'LEGEND_PREPUBLICATION_MIGRATION:Schema probe runtime invalid operation'):
                owner._invoke_migration_bundle()
        call.assert_called_once()

    def test_mutation_admission_reason_is_reported_without_exposing_provider_text(self):
        owner = self.owner
        import io
        marker = ('LEGEND_MIGRATION_ADMISSION_DIAGNOSTIC:'
                  'HISTORICAL_PREWRITE_PROOF_REJECTED:run=37765986494:attempt=1')
        failure = ('Migration stage unresolved: mutation-admission'
                   '; preserve prior evidence and reconcile without replay.')
        output = io.StringIO()
        response = subprocess.CompletedProcess([], 1, marker + '\n', failure)
        with patch.object(owner.subprocess, 'run', return_value=response), \
             patch('sys.stdout', output):
            with self.assertRaisesRegex(RuntimeError,
                                        'LEGEND_PREPUBLICATION_MIGRATION:Migration stage unresolved: mutation-admission'):
                owner._invoke_migration_bundle()
        self.assertIn(marker, output.getvalue())
        self.assertIn('::error title=LEGEND migration admission::', output.getvalue())
        output = io.StringIO()
        response = subprocess.CompletedProcess([], 1, marker + '\nPassword=secret', failure)
        with patch.object(owner.subprocess, 'run', return_value=response), \
             patch('sys.stdout', output):
            with self.assertRaises(RuntimeError):
                owner._invoke_migration_bundle()
        self.assertEqual('', output.getvalue())

    def test_unknown_provider_text_never_reaches_diagnostics(self):
        owner = self.owner
        result = subprocess.CompletedProcess([], 1, '',
            'Authentication error at https://secret.invalid/?sig=hidden')
        with patch.object(owner.subprocess, 'run', return_value=result) as call:
            with self.assertRaisesRegex(RuntimeError, 'LEGEND_PREPUBLICATION_MIGRATION:UNKNOWN_FAILURE') as caught:
                owner._invoke_migration_bundle()
        self.assertNotIn('hidden', str(caught.exception))
        call.assert_called_once()

    def test_both_lanes_are_observed_even_when_configuration_fails(self):
        owner = self.owner
        output = self.io.StringIO()
        errors = self.io.StringIO()
        authority = self.SimpleNamespace(assert_protected_release_execution=lambda: None)
        safe = 'LEGEND_PREPUBLICATION_MIGRATION:Applied database migration history is not in validated sequence'
        with patch.object(owner, 'release_authority', return_value=authority), \
             patch.object(owner, 'configure_all_targets', side_effect=RuntimeError('private config detail')), \
             patch.object(owner, 'run_migration_lane', side_effect=RuntimeError(safe)), \
             patch('sys.stdout', output), patch('sys.stderr', errors):
            with self.assertRaisesRegex(RuntimeError, 'Pre-publication lanes require exact reconciliation'):
                owner.main()
        self.assertIn('LEGEND_PREPUBLICATION:CONFIGURATION:FAILED', errors.getvalue())
        self.assertIn(safe, errors.getvalue())
        self.assertNotIn('private config detail', errors.getvalue())

    def test_successful_configuration_is_preserved_when_migrations_fail(self):
        owner = self.owner
        output = self.io.StringIO()
        errors = self.io.StringIO()
        authority = self.SimpleNamespace(assert_protected_release_execution=lambda: None)
        with patch.object(owner, 'release_authority', return_value=authority), \
             patch.object(owner, 'configure_all_targets', return_value={'targets': []}), \
             patch.object(owner, 'run_migration_lane', side_effect=RuntimeError('secret DB provider detail')), \
             patch('sys.stdout', output), patch('sys.stderr', errors):
            with self.assertRaises(RuntimeError):
                owner.main()
        self.assertIn('LEGEND_PREPUBLICATION:CONFIGURATION:READY', output.getvalue())
        self.assertIn('LEGEND_PREPUBLICATION:MIGRATION:FAILED', errors.getvalue())
        self.assertNotIn('secret DB provider detail', errors.getvalue())



    def test_only_bound_unknown_history_ids_cross_prepublication_log(self):
        owner = self.owner
        valid = ('LEGEND_SCHEMA_HISTORY:1:20260927053000_AddAdvertisingActionAuthorizations')
        fixed = ('Database contains applied migration history absent from validated bundle'
                 '; preserve prior evidence and reconcile without replay.')
        output = self.io.StringIO()
        result = subprocess.CompletedProcess([], 1, valid + '\n', fixed)
        with patch.object(owner.subprocess, 'run', return_value=result) as job, \
             patch('sys.stdout', output):
            with self.assertRaisesRegex(RuntimeError, 'LEGEND_PREPUBLICATION_MIGRATION:Database contains applied'):
                owner._invoke_migration_bundle()
        self.assertEqual(valid + '\n', output.getvalue())
        job.assert_called_once()

    def test_prepublication_never_relays_unvalidated_db_provider_values(self):
        owner = self.owner
        fixed = ('Database contains applied migration history absent from validated bundle'
                 '; preserve prior evidence and reconcile without replay.')
        for raw in ('LEGEND_SCHEMA_HISTORY:1:Password=private',
                    'LEGEND_SCHEMA_HISTORY:2:20260927053000_OK;Server=private',
                    'random secret text'):
            with self.subTest(raw=raw):
                output = self.io.StringIO()
                with patch.object(owner.subprocess, 'run',
                        return_value=subprocess.CompletedProcess([], 1, raw, fixed)), \
                     patch('sys.stdout', output):
                    with self.assertRaises(RuntimeError):
                        owner._invoke_migration_bundle()
                self.assertEqual('', output.getvalue())


if __name__ == '__main__':
    unittest.main()
