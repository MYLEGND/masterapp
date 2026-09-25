#!/usr/bin/env python3
"""Synthetic local Git fixtures; no Actions dispatch, provider calls or builds."""
import importlib.util
import hashlib
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import io
import tarfile
import time

spec = importlib.util.spec_from_file_location('candidate_validation', Path(__file__).with_name('legend-candidate-validation.py'))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


class CandidateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.trusted, self.candidate = self.root / 'trusted', self.root / 'candidate'
        self.trusted.mkdir()
        self.git(self.trusted, 'init', '-q')
        self.git(self.trusted, 'config', 'user.email', 'simulation@example.invalid')
        self.git(self.trusted, 'config', 'user.name', 'Synthetic fixture')
        for name in m.NODE_TESTS + tuple('AgentPortal.Tests/' + c + '.cs' for c in m.CLASSES):
            self.write(self.trusted, name, '// simulation fixture only\n')
        self.write(self.trusted, 'Legend-Cloudflare/src/runtime/adapter.mjs', 'export const fixture = 1;\n')
        self.git(self.trusted, 'add', '.')
        self.git(self.trusted, 'commit', '-qm', 'reviewed synthetic baseline')
        self.base = self.git(self.trusted, 'rev-parse', 'HEAD')
        self.git(self.root, 'clone', '-q', str(self.trusted), str(self.candidate))
        self.git(self.candidate, 'config', 'user.email', 'simulation@example.invalid')
        self.git(self.candidate, 'config', 'user.name', 'Synthetic fixture')
        self.git(self.candidate, 'checkout', '--detach', '-q', self.base)
        self.write(self.candidate, 'Legend-Cloudflare/src/runtime/adapter.mjs', 'export const fixture = 2;\n')
        self.commit()

    @staticmethod
    def git(root, *args):
        env = dict(os.environ, GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_NOSYSTEM='1')
        for name in tuple(env):
            if name.startswith('GIT_') and name not in ('GIT_CONFIG_GLOBAL', 'GIT_CONFIG_NOSYSTEM'):
                del env[name]
        return subprocess.check_output(['git', '-C', str(root), *args], env=env, stderr=subprocess.PIPE).decode().strip()

    @staticmethod
    def write(root, name, content):
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content)

    def commit(self):
        self.git(self.candidate, 'add', '.')
        self.git(self.candidate, 'commit', '-qm', 'synthetic candidate')
        self.head = self.git(self.candidate, 'rev-parse', 'HEAD')
        self.request = dict(version=m.VERSION, repository='fixture/repository', baseSha=self.base,
            candidateSha=self.head, trustedWorkflowSha=self.base, requestId='synthetic-1',
            profile='cloudflare-contracts', approvalActionDigest='a' * 64,
            patchSha256=hashlib.sha256(m.git(self.candidate, *m.DIFF_ARGS, self.base, self.head, '--').stdout).hexdigest())

    def verify(self):
        return m.verify(self.request, self.trusted, self.candidate, 'fixture/repository')

    def test_exact_detached_candidate_passes_without_authority(self):
        result = self.verify()
        self.assertEqual(result['changedFiles'], ['Legend-Cloudflare/src/runtime/adapter.mjs'])
        self.assertFalse(result['mergeAuthorized'])
        self.assertFalse(result['deploymentAuthorized'])

    def test_digest_tampering_rejected(self):
        self.request['patchSha256'] = 'b' * 64
        with self.assertRaisesRegex(m.Rejected, 'patch_digest_mismatch'):
            self.verify()

    def test_actual_candidate_sha_must_match(self):
        self.request['candidateSha'] = self.base
        with self.assertRaisesRegex(m.Rejected, 'checkout_sha_mismatch'):
            self.verify()

    def test_baseline_must_descend_from_trusted_revision(self):
        self.request['baseSha'] = '1' * 40
        with self.assertRaisesRegex(m.Rejected, 'baseline_must_extend_trusted_revision'):
            self.verify()

    def test_accumulated_baseline_allowed_but_cumulative_privileged_change_rejected(self):
        trusted_sha = self.base
        self.base = self.head
        self.write(self.candidate, 'Legend-Cloudflare/src/runtime/adapter.mjs', 'export const fixture = 3;\n')
        self.commit()
        self.request['trustedWorkflowSha'] = trusted_sha
        self.assertFalse(self.verify()['mergeAuthorized'])
        self.write(self.candidate, '.github/workflows/unsafe.yml', 'privileged change')
        self.commit()
        self.base = self.head
        self.write(self.candidate, 'Legend-Cloudflare/src/runtime/adapter.mjs', 'export const fixture = 4;\n')
        self.commit()
        self.request['trustedWorkflowSha'] = trusted_sha
        with self.assertRaisesRegex(m.Rejected, 'privileged_review_required'):
            self.verify()

    def test_same_checkout_rejected(self):
        with self.assertRaisesRegex(m.Rejected, 'checkouts_must_be_separate'):
            m.verify(self.request, self.trusted, self.trusted, 'fixture/repository')

    def test_branch_checkout_rejected(self):
        self.git(self.candidate, 'checkout', '-qb', 'mutable')
        with self.assertRaisesRegex(m.Rejected, 'candidate_must_be_detached'):
            self.verify()

    def test_dirty_and_untracked_files_rejected(self):
        path = self.candidate / 'Legend-Cloudflare/src/runtime/adapter.mjs'
        path.write_text('dirty')
        with self.assertRaisesRegex(m.Rejected, 'checkout_dirty'):
            self.verify()
        self.git(self.candidate, 'checkout', '--', str(path))
        self.write(self.candidate, 'ignored-or-extra.txt', 'untrusted')
        with self.assertRaisesRegex(m.Rejected, 'checkout_contains_untracked_files'):
            self.verify()

    def test_workflow_build_authority_and_registry_changes_rejected(self):
        for name in ('.github/workflows/validate.yml', 'scripts/legend-candidate-validation.py',
                     'Directory.Build.props', 'Legend-Cloudflare/package.json',
                     'Legend-Cloudflare/src/security/authenticate.mjs',
                     'Legend-Cloudflare/src/runtime/registry.mjs',
                     'AgentPortal/Services/FounderSoftwareRemediationService.cs'):
            with self.subTest(path=name):
                self.git(self.candidate, 'reset', '--hard', self.head)
                self.write(self.candidate, name, 'privileged change')
                original = self.head
                self.commit()
                with self.assertRaisesRegex(m.Rejected, 'privileged_review_required'):
                    self.verify()
                self.git(self.candidate, 'reset', '--hard', original)
                self.head = original

    def test_symlink_rejected_even_for_eligible_path(self):
        path = self.candidate / 'Legend-Cloudflare/src/runtime/adapter.mjs'
        path.unlink()
        path.symlink_to('/etc/passwd')
        self.commit()
        with self.assertRaisesRegex(m.Rejected, 'file_mode_not_allowed'):
            self.verify()

    def test_required_tests_cannot_be_deleted(self):
        (self.candidate / m.NODE_TESTS[0]).unlink()
        self.commit()
        with self.assertRaisesRegex(m.Rejected, 'privileged_review_required'):
            self.verify()

    def test_foreign_history_rejected(self):
        self.git(self.candidate, 'checkout', '--orphan', 'unrelated')
        self.git(self.candidate, 'commit', '-qam', 'unrelated synthetic history')
        sha = self.git(self.candidate, 'rev-parse', 'HEAD')
        self.git(self.candidate, 'checkout', '--detach', '-q', sha)
        self.request['candidateSha'] = sha
        with self.assertRaisesRegex(m.Rejected, 'candidate_must_extend_baseline'):
            self.verify()

    def test_unknown_fields_and_repository_mismatch_rejected(self):
        self.request['command'] = 'arbitrary shell'
        with self.assertRaisesRegex(m.Rejected, 'request_fields_invalid'):
            self.verify()
        del self.request['command']
        self.request['repository'] = 'other/repository'
        with self.assertRaisesRegex(m.Rejected, 'repository_mismatch'):
            self.verify()

    def test_commands_are_fixed_and_exclude_live_qualification(self):
        commands = m.commands(self.candidate, self.root / 'results')
        self.assertEqual([name for name, _ in commands], ['restore', 'node', 'dotnet'])
        joined = ' '.join(part for _, command in commands for part in command)
        self.assertNotIn('LiveQualification', joined)
        self.assertNotIn('npm', joined)
        self.assertNotIn('workerd.integration', joined)
        for cls in m.CLASSES:
            self.assertIn('AgentPortal.Tests.' + cls, joined)

    def test_child_environment_excludes_tokens_and_provider_settings(self):
        from unittest.mock import patch
        with patch.dict(os.environ, {'GITHUB_TOKEN': 'synthetic', 'AZURE_CLIENT_SECRET': 'synthetic',
                'LEGEND_INTEGRATION_ROOT': '/untrusted', 'NODE_OPTIONS': '--import=evil', 'NUGET_AUTH_TOKEN': 'synthetic'}):
            env = m.clean_environment(self.root)
        for key in ('GITHUB_TOKEN', 'AZURE_CLIENT_SECRET', 'LEGEND_INTEGRATION_ROOT', 'NODE_OPTIONS', 'NUGET_AUTH_TOKEN'):
            self.assertNotIn(key, env)

    def test_test_sources_and_fixture_changes_require_privileged_review(self):
        for name in (m.NODE_TESTS[0], 'AgentPortal.Tests/' + m.CLASSES[0] + '.cs',
                     'Legend-Cloudflare/tests/runtime/fixtures/held-out.v1.json'):
            with self.subTest(path=name):
                self.assertFalse(m.eligible_path(name))
        self.write(self.candidate, m.NODE_TESTS[0], '// weakened test')
        self.commit()
        with self.assertRaisesRegex(m.Rejected, 'privileged_review_required'):
            self.verify()

    def test_container_bounds_and_mounts_exclude_credentials_parent_and_socket(self):
        command = m.container_command('test', self.candidate, 'bounded-volume',
                                      Path('/setup/node'), ['node', '--version'])
        joined = ' '.join(command)
        for flag in ('--network=none', '--read-only', '65532:65532', '--cap-drop=ALL',
                     '--security-opt=no-new-privileges', '--cpus=2', '--memory=3g',
                     '--memory-swap=3g', '--pids-limit=256', '--log-driver=none'):
            self.assertIn(flag, command)
        self.assertIn(m.SDK_IMAGE, command)
        self.assertIn('@sha256:', m.SDK_IMAGE)
        self.assertNotIn('/var/run/docker.sock', joined)
        self.assertNotIn('GITHUB_TOKEN', joined)
        self.assertNotIn('target=/host', joined)
        mounts = [command[i + 1] for i, value in enumerate(command) if value == '--mount']
        self.assertEqual(len(mounts), 3)
        self.assertTrue(all('readonly' in value for value in mounts if value.startswith('type=bind')))
        self.assertNotIn(str(self.trusted), joined)

    @staticmethod
    def trx_archive(name='contracts.trx', content=b'<TestRun/>', kind=tarfile.REGTYPE):
        stream = io.BytesIO()
        with tarfile.open(fileobj=stream, mode='w') as archive:
            info = tarfile.TarInfo(name)
            info.type = kind
            info.size = len(content) if kind == tarfile.REGTYPE else 0
            info.linkname = '/private/secret' if kind == tarfile.SYMTYPE else ''
            archive.addfile(info, io.BytesIO(content) if info.size else None)
        return stream.getvalue()

    def test_report_copy_rejects_links_and_paths_instead_of_extracting(self):
        self.assertEqual(m.extract_trx(self.trx_archive()), b'<TestRun/>')
        for archive in (self.trx_archive('../receipt.json'), self.trx_archive('/receipt.json'),
                        self.trx_archive(kind=tarfile.SYMTYPE), b'not a tar file'):
            with self.assertRaises(m.Rejected):
                m.extract_trx(archive)

    def test_parent_capture_bounds_output_and_deadline(self):
        import sys
        with self.assertRaisesRegex(m.Rejected, 'validation_output_limit_exceeded'):
            m.capture([sys.executable, '-c', 'print("x" * 4096)'], {}, 5, maximum=32)
        with self.assertRaisesRegex(m.Rejected, 'validation_deadline_exceeded'):
            m.capture([sys.executable, '-c', 'import time; time.sleep(5)'], {}, 0.05)

    def test_xml_size_checked_before_read_and_symlink_rejected(self):
        path = self.root / 'large.xml'
        with path.open('wb') as stream:
            stream.truncate(m.MAX_REPORT_BYTES + 1)
        with self.assertRaisesRegex(m.Rejected, 'test_report_invalid'):
            m.xml_document(path)
        link = self.root / 'report.xml'
        link.symlink_to(path)
        with self.assertRaisesRegex(m.Rejected, 'test_report_invalid'):
            m.xml_document(link)

    def isolated_mock(self, fail_label=None):
        node = self.root / 'node'
        node.write_text('synthetic node binary')
        operations = []
        def fake_capture(command, env, remaining, maximum=m.MAX_REPORT_BYTES):
            operations.append(command)
            if command[1] == 'run':
                name = command[command.index('--name') + 1]
                if name.endswith('-' + str(fail_label)):
                    raise m.Rejected('synthetic_timeout')
                if name.endswith('-node-runtime'):
                    return 0, b'linux:x64:24\n'
                return 0, b'<testsuites/>'
            if command[1] == 'cp':
                return 0, self.trx_archive()
            return 0, b''
        with patch.object(m.shutil, 'which', return_value=str(node)), patch.object(m, 'capture', side_effect=fake_capture):
            if fail_label:
                with self.assertRaisesRegex(m.Rejected, 'synthetic_timeout'):
                    m.run_isolated(self.trusted, self.candidate, self.root, {}, time.monotonic() + 20)
            else:
                m.run_isolated(self.trusted, self.candidate, self.root, {}, time.monotonic() + 20)
        return operations

    def test_restore_uses_only_trusted_source_and_candidate_steps_are_offline(self):
        operations = self.isolated_mock()
        runs = [command for command in operations if command[1] == 'run']
        self.assertEqual(len(runs), 4)
        for command in runs:
            joined = ' '.join(command)
            restore = command[command.index('--name') + 1].endswith('-restore')
            probe = command[command.index('--name') + 1].endswith('-node-runtime')
            self.assertIn('--network=' + ('bridge' if restore else 'none'), command)
            self.assertIn('source=' + str(self.trusted if restore or probe else self.candidate) + ',target=/source,readonly', joined)
            self.assertNotIn('receipt.json', joined)
        creates = [c for c in operations if c[1:3] == ['volume', 'create']]
        self.assertEqual(len(creates), 1)
        self.assertIn('--opt=type=tmpfs', creates[0])
        self.assertTrue(any('size=2147483648' in v for v in creates[0]))
        self.assertEqual(len([c for c in operations if c[1:3] == ['rm', '--force']]), 4)
        self.assertEqual(operations[-1][1:4], ['volume', 'rm', '--force'])

    def test_failure_removes_active_container_and_bounded_state_volume(self):
        operations = self.isolated_mock(fail_label='node')
        removals = [c for c in operations if c[1:3] == ['rm', '--force']]
        self.assertTrue(any(c[-1].endswith('-node') for c in removals))
        self.assertEqual(operations[-1][1:4], ['volume', 'rm', '--force'])

    def reports(self, missing_class=False, failed=False):
        self.write(self.root, 'node.log', '<testsuites><testsuite><testcase name="synthetic"/></testsuite></testsuites>')
        classes = m.CLASSES[:-1] if missing_class else m.CLASSES
        methods = ''.join('<TestMethod className="AgentPortal.Tests.' + c + '"/>' for c in classes)
        result = '<UnitTestResult outcome="' + ('Failed' if failed else 'Passed') + '"/>'
        self.write(self.root, 'contracts.trx', '<TestRun xmlns="urn:synthetic">' + methods + result + '</TestRun>')

    def test_reports_require_every_dotnet_class_and_no_failures(self):
        self.reports()
        self.assertEqual(m.test_counts(self.root), {'node': 1, 'dotnet': 1})
        self.reports(missing_class=True)
        with self.assertRaisesRegex(m.Rejected, 'dotnet_tests_not_passed_or_missing'):
            m.test_counts(self.root)
        self.reports(failed=True)
        with self.assertRaisesRegex(m.Rejected, 'dotnet_tests_not_passed_or_missing'):
            m.test_counts(self.root)

    def test_zero_node_discovery_and_xml_entities_rejected(self):
        self.reports()
        self.write(self.root, 'node.log', '<testsuites/>')
        with self.assertRaisesRegex(m.Rejected, 'node_tests_not_passed'):
            m.test_counts(self.root)
        self.write(self.root, 'node.log', '<!DOCTYPE root []><testsuites/>')
        with self.assertRaisesRegex(m.Rejected, 'test_report_invalid'):
            m.test_counts(self.root)


if __name__ == '__main__':
    unittest.main()
