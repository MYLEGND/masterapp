"""Exercise ambiguous deployment outcomes without contacting or mutating Azure."""
import hashlib
import importlib.util
import json
import os
import subprocess
import textwrap
from pathlib import Path
import tempfile
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

    def run(self):
        return deploy.reconcile(self, clock=lambda: self.now, sleep=self.sleep, interval=1, timeout=10)


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

    def test_static_terminal_onedeploy_failure_uses_one_verified_recovery_then_requires_live_revision(self):
        azure = FakeAzure(
            [[], [row('failed', 3)], [row('recovery', 1)], [row('recovery', 4)]],
            [False, False, None, True, True],
            accepted=False,
            static=True)
        self.assertEqual('deployed', azure.run())
        self.assertEqual(1, azure.uploads)
        self.assertEqual(1, azure.recovery_uploads)

    def test_static_recovery_terminal_failure_is_not_retried_again(self):
        azure = FakeAzure(
            [[], [row('failed', 3)], [row('recovery-failed', 3)]],
            [False, False, False],
            accepted=False,
            static=True,
            recovery_accepted=False)
        with self.assertRaisesRegex(RuntimeError, 'failed'):
            azure.run()
        self.assertEqual(1, azure.uploads)
        self.assertEqual(1, azure.recovery_uploads)

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

    def test_status_unavailable_is_fail_closed(self):
        azure = FakeAzure([RuntimeError('gateway unavailable')], [False])
        with self.assertRaisesRegex(RuntimeError, 'deadline'):
            azure.run()
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

    def test_static_recovery_uses_kudu_config_zip_once_with_same_package(self):
        azure = deploy.Azure('masterapp-website', Path('/immutable.zip'), 'https://example.invalid', 'a' * 40, static=True)
        with patch.object(deploy.subprocess, 'run') as run:
            run.return_value.returncode = 0
            self.assertTrue(azure.submit_static_recovery())
            command = run.call_args.args[0]
            self.assertEqual(['az','webapp','deployment','source','config-zip'], command[:5])
            self.assertEqual('/immutable.zip', command[command.index('--src') + 1])
            self.assertEqual(1, run.call_count)



class SettingsIdempotenceTests(unittest.TestCase):
    def run_settings(self, drift=False):
        workflow = (ROOT.parent / '.github/workflows/all-intentional-direct-release-20260918.yml').read_text()
        names = ['Synchronize Protect shared website authorization and publisher runtime',
                 'Synchronize shared website editor ticket authority']
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            common = {'FOUNDER_OID': 'test-founder', 'Founder__Upn': 'test@example.invalid',
                      'DataProtection__BlobUri': 'test-blob', 'DataProtection__KeyVaultKeyId': 'test-key',
                      'MarketingDataProtection__BlobUri': 'test-blob',
                      'MarketingDataProtection__KeyVaultKeyId': 'test-key',
                      'WebsiteEditorDataProtection__BlobUri': 'test-blob',
                      'WebsiteEditorDataProtection__KeyVaultKeyId': 'test-key',
                      'Analytics__SharedSecret': 'test-secret', 'Tracking__SharedSecret': 'test-secret',
                      'Tracking:SharedSecret': 'test-secret', 'WEBSITE_NODE_DEFAULT_VERSION': '~24'}
            state = {app: dict(common) for app, _, _ in deploy.TARGETS.values()}
            if drift:
                state['masterapp-protect']['Tracking:SharedSecret'] = 'stale'
                state['masterapp-parfait']['WebsiteEditorDataProtection__BlobUri'] = 'stale'
            (directory / 'state.json').write_text(json.dumps(state))
            (directory / 'writes.json').write_text('[]')
            executable = directory / 'az'
            executable.write_text("""#!/usr/bin/env python3
import json, os, sys
from pathlib import Path
root = Path(os.environ['FAKE_AZ_ROOT'])
state = json.loads((root / 'state.json').read_text())
args = sys.argv[1:]
assert args[:3] == ['webapp', 'config', 'appsettings']
app = args[args.index('-n') + 1]
if args[3] == 'list':
    print(json.dumps([{'name': key, 'value': value} for key, value in state[app].items()]))
elif args[3] == 'set':
    pairs = args[args.index('--settings') + 1:args.index('--output')]
    updates = dict(pair.split('=', 1) for pair in pairs)
    assert updates and all(state[app].get(k) != v for k, v in updates.items()), 'Unnecessary setting write'
    state[app].update(updates)
    (root / 'state.json').write_text(json.dumps(state))
    writes = json.loads((root / 'writes.json').read_text())
    writes.append({'app': app, 'keys': list(updates)})
    (root / 'writes.json').write_text(json.dumps(writes))
else:
    raise AssertionError('Unexpected Azure mutation')
""")
            executable.chmod(0o755)
            env = os.environ | {'PATH': str(directory) + os.pathsep + os.environ['PATH'],
                                'FAKE_AZ_ROOT': folder, 'SELECTED_TARGETS': json.dumps(list(state))}
            for _ in range(2):
                for name in names:
                    block = workflow.split('      - name: ' + name + '\n', 1)[1].split('      - name:', 1)[0]
                    script = textwrap.dedent(block.split('        run: |\n', 1)[1])
                    script = script.replace('/tmp/', folder + '/')
                    result = subprocess.run(['bash', '-c', script], env=env, text=True, capture_output=True)
                    self.assertEqual(0, result.returncode, result.stderr)
            return json.loads((directory / 'writes.json').read_text())

    def test_matching_settings_make_zero_azure_writes_across_repeated_runs(self):
        self.assertEqual([], self.run_settings())

    def test_only_drifted_keys_are_updated_once_and_then_preserved(self):
        self.assertEqual([{'app': 'masterapp-protect', 'keys': ['Tracking:SharedSecret']},
                          {'app': 'masterapp-parfait', 'keys': ['WebsiteEditorDataProtection__BlobUri']}],
                         self.run_settings(drift=True))

if __name__ == '__main__':
    unittest.main()
