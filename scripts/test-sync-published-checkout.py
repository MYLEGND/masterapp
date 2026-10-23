#!/usr/bin/env python3
"""Exercise the launcher sync against disposable real Git repositories."""
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
from contextlib import redirect_stdout

spec = importlib.util.spec_from_file_location('published_sync', Path(__file__).with_name('sync-published-checkout.py'))
sync = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sync)


class PublishedCheckoutSyncTests(unittest.TestCase):
    def setUp(self):
        environment = patch.dict(sync.os.environ, {'GITHUB_ACTIONS': 'false'})
        environment.start()
        self.addCleanup(environment.stop)
        self.temp = tempfile.TemporaryDirectory(prefix='legend-sync-test-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.remote, self.publisher, self.local = [self.root / name for name in ['remote.git', 'publisher', 'local']]
        self.git(self.root, 'init', '--bare', str(self.remote))
        self.git(self.root, 'init', '-b', 'Legend/fast-track-release', str(self.publisher))
        self.identity(self.publisher)
        self.commit(self.publisher, 'app.txt', 'original')
        self.git(self.publisher, 'remote', 'add', 'origin', str(self.remote))
        self.git(self.publisher, 'push', '-u', 'origin', 'Legend/fast-track-release')
        self.git(self.root, 'clone', '-b', 'Legend/fast-track-release', str(self.remote), str(self.local))
        self.identity(self.local)

    def use_legacy_native_checkout(self):
        self.git(self.publisher, 'branch', '-m', 'legend/approved-changes')
        self.git(self.publisher, 'push', '-u', 'origin', 'legend/approved-changes')
        self.git(self.local, 'fetch', 'origin')
        self.git(self.local, 'branch', '-m', 'legend/approved-changes')
        self.git(self.local, 'branch', '--set-upstream-to', 'origin/legend/approved-changes')

    def git(self, repo, *args):
        result = subprocess.run(['git', '-C', str(repo), *args], capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result.stdout.strip()

    def identity(self, repo):
        self.git(repo, 'config', 'user.name', 'Sync fixture')
        self.git(repo, 'config', 'user.email', 'fixture@example.invalid')

    def commit(self, repo, name, content):
        (repo / name).write_text(content)
        self.git(repo, 'add', name)
        self.git(repo, 'commit', '-m', 'fixture change')

    def publish(self):
        self.commit(self.publisher, 'app.txt', 'published')
        self.git(self.publisher, 'push', 'origin', self.git(self.publisher, 'branch', '--show-current'))

    def run_sync(self, probe=lambda: False):
        return sync.sync_checkout(self.local, process_probe=probe)

    def configure_native(self):
        self.git(self.local, 'config', 'legend.nativeTestingRef', 'refs/remotes/origin/Legend/fast-track-release')
        self.git(self.local, 'switch', '-c', 'founder/testing', '--track', 'origin/Legend/fast-track-release')

    def test_native_sync_preserves_unrelated_user_edits(self):
        self.configure_native()
        (self.local / 'local-settings.txt').write_text('keep me')
        self.publish()
        sync.sync_checkout(self.local, process_probe=lambda: False, native=True)
        self.assertEqual((self.local / 'app.txt').read_text(), 'published')
        self.assertEqual((self.local / 'local-settings.txt').read_text(), 'keep me')

    def test_native_sync_preserves_tracked_build_setting_edits(self):
        self.configure_native()
        self.commit(self.publisher, 'native.txt', 'version=33')
        self.git(self.publisher, 'push', 'origin', 'Legend/fast-track-release')
        sync.sync_checkout(self.local, process_probe=lambda: False, native=True)
        (self.local / 'native.txt').write_text('version=34')
        self.publish()
        sync.sync_checkout(self.local, process_probe=lambda: False, native=True)
        self.assertEqual((self.local / 'native.txt').read_text(), 'version=34')
        self.assertEqual((self.local / 'app.txt').read_text(), 'published')
        self.assertIn('native.txt', self.git(self.local, 'status', '--porcelain'))

    def test_native_sync_conflicting_edits_block_without_overwrite(self):
        self.configure_native()
        (self.local / 'app.txt').write_text('unsaved user work on disk')
        before = self.git(self.local, 'rev-parse', 'HEAD')
        self.publish()
        with self.assertRaises(sync.SyncSkipped):
            sync.sync_checkout(self.local, process_probe=lambda: False, native=True)
        self.assertEqual((self.local / 'app.txt').read_text(), 'unsaved user work on disk')
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), before)

    def test_native_sync_requires_configured_tracking_ref_and_idle_editors(self):
        with self.assertRaises(sync.SyncSkipped):
            sync.sync_checkout(self.local, process_probe=lambda: False, native=True)
        self.configure_native()
        before = self.git(self.local, 'rev-parse', 'HEAD')
        self.publish()
        with self.assertRaisesRegex(sync.SyncSkipped, 'running'):
            sync.sync_checkout(self.local, process_probe=lambda: True, native=True)
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), before)

    def test_native_sync_explicitly_refuses_configured_autostash(self):
        self.configure_native()
        self.git(self.local, 'config', 'merge.autoStash', 'true')
        (self.local / 'app.txt').write_text('preserve conflicting work')
        before = self.git(self.local, 'rev-parse', 'HEAD')
        self.publish()
        with self.assertRaises(sync.SyncSkipped):
            sync.sync_checkout(self.local, process_probe=lambda: False, native=True)
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), before)
        self.assertEqual((self.local / 'app.txt').read_text(), 'preserve conflicting work')
        self.assertEqual(self.git(self.local, 'stash', 'list'), '')

    def test_native_sync_pins_branch_even_when_new_branch_has_same_head(self):
        self.configure_native()
        before = self.git(self.local, 'rev-parse', 'HEAD')
        self.publish()
        authority = sync.git
        def change_branch(repo, *args, **kwargs):
            result = authority(repo, *args, **kwargs)
            if args[0] == 'fetch':
                self.git(self.local, 'switch', '-c', 'other-work', before)
                self.git(self.local, 'branch', '--set-upstream-to', 'origin/Legend/fast-track-release')
            return result
        with patch.object(sync, 'git', side_effect=change_branch):
            with self.assertRaisesRegex(sync.SyncSkipped, 'branch changed'):
                sync.sync_checkout(self.local, process_probe=lambda: False, native=True)
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), before)
        self.assertEqual(self.git(self.local, 'branch', '--show-current'), 'other-work')
        self.assertEqual((self.local / 'app.txt').read_text(), 'original')

    def test_native_sync_pins_explicit_target_across_fetch(self):
        self.configure_native()
        before = self.git(self.local, 'rev-parse', 'HEAD')
        self.publish()
        authority = sync.git
        def change_target(repo, *args, **kwargs):
            result = authority(repo, *args, **kwargs)
            if args[0] == 'fetch':
                self.git(self.local, 'config', 'legend.nativeTestingRef', 'refs/remotes/origin/new-intended-target')
            return result
        with patch.object(sync, 'git', side_effect=change_target):
            with self.assertRaisesRegex(sync.SyncSkipped, 'configured target changed'):
                sync.sync_checkout(self.local, process_probe=lambda: False, native=True)
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), before)
        self.assertEqual((self.local / 'app.txt').read_text(), 'original')

    def test_native_sync_default_probe_blocks_before_fetch(self):
        self.configure_native()
        before = self.git(self.local, 'rev-parse', 'origin/Legend/fast-track-release')
        self.publish()
        with patch.object(sync, 'native_editor_or_build_active', return_value=True):
            with self.assertRaisesRegex(sync.SyncSkipped, 'running'):
                sync.sync_checkout(self.local, native=True)
        self.assertEqual(self.git(self.local, 'rev-parse', 'origin/Legend/fast-track-release'), before)

    def test_workspace_mode_preserves_explicit_target_and_never_falls_back_on_invalid_setting(self):
        self.assertFalse(sync.workspace_uses_native_target(self.local))
        self.configure_native()
        self.assertTrue(sync.workspace_uses_native_target(self.local))
        self.git(self.local, 'config', 'legend.nativeTestingRef', '')
        self.assertTrue(sync.workspace_uses_native_target(self.local))
        with self.assertRaisesRegex(sync.SyncSkipped, 'explicit origin testing ref'):
            sync.sync_checkout(self.local, process_probe=lambda: False, native=True)

    def test_readonly_status_does_not_fetch_merge_or_create_sync_lock(self):
        self.configure_native()
        before = self.git(self.local, 'rev-parse', 'origin/Legend/fast-track-release')
        self.publish()
        with patch.object(sync, '__file__', str(self.local / 'scripts' / 'sync-published-checkout.py')), \
                patch.object(sync, 'native_editor_or_build_active', return_value=False), redirect_stdout(io.StringIO()) as output:
            self.assertEqual(sync.main(['--sync-workspace', '--status']), 0)
        report = json.loads(output.getvalue())
        self.assertEqual(report['status'], 'ready')
        self.assertEqual(report['target'], 'refs/remotes/origin/Legend/fast-track-release')
        self.assertEqual(report['remoteFreshness'], 'not_checked')
        self.assertEqual(self.git(self.local, 'rev-parse', 'origin/Legend/fast-track-release'), before)
        self.assertEqual((self.local / 'app.txt').read_text(), 'original')
        self.assertFalse((self.local / '.git' / 'legend-published-sync.lock').exists())

    def test_readonly_status_reports_editor_block_without_claiming_remote_freshness(self):
        self.configure_native()
        report = sync.sync_status(self.local, native=True, process_probe=lambda: True)
        self.assertEqual(report['status'], 'blocked')
        self.assertIn('running', report['reason'])
        self.assertEqual(report['remoteFreshness'], 'not_checked')

    def test_clean_published_checkout_fast_forwards_then_is_current(self):
        self.publish()
        self.assertIn('Updated', self.run_sync())
        self.assertEqual((self.local / 'app.txt').read_text(), 'published')
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), self.git(self.publisher, 'rev-parse', 'HEAD'))
        self.assertIn('current', self.run_sync())

    def test_dirty_staged_and_untracked_files_are_preserved_without_fetch(self):
        self.publish()
        old_remote = self.git(self.local, 'rev-parse', 'origin/Legend/fast-track-release')
        for mode in ['unstaged', 'staged', 'untracked']:
            with self.subTest(mode=mode):
                file = self.local / ('native-build.txt' if mode == 'untracked' else 'app.txt')
                file.write_text('user build 32')
                if mode == 'staged': self.git(self.local, 'add', 'app.txt')
                with self.assertRaisesRegex(sync.SyncSkipped, 'Local changes'):
                    self.run_sync()
                self.assertEqual(file.read_text(), 'user build 32')
                self.assertEqual(self.git(self.local, 'rev-parse', 'origin/Legend/fast-track-release'), old_remote)
                # Fixture restoration only; the sync implementation never restores or deletes files.
                if mode == 'untracked': file.unlink()
                else:
                    self.git(self.local, 'restore', '--staged', '--worktree', 'app.txt')

    def test_divergent_local_commit_is_never_merged(self):
        self.commit(self.local, 'app.txt', 'local committed work')
        original = self.git(self.local, 'rev-parse', 'HEAD')
        self.publish()
        with self.assertRaisesRegex(sync.SyncSkipped, 'ahead or divergent'):
            self.run_sync()
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), original)
        self.assertEqual((self.local / 'app.txt').read_text(), 'local committed work')

    def test_working_branch_and_missing_upstream_are_not_synced(self):
        self.git(self.local, 'switch', '-c', 'repair/example')
        with self.assertRaisesRegex(sync.SyncSkipped, 'working branch'): self.run_sync()
        self.git(self.local, 'switch', 'Legend/fast-track-release')
        self.git(self.local, 'branch', '--unset-upstream')
        with self.assertRaises(sync.SyncSkipped): self.run_sync()

    def test_active_app_blocks_before_fetch(self):
        self.publish()
        old_remote = self.git(self.local, 'rev-parse', 'origin/Legend/fast-track-release')
        with self.assertRaisesRegex(sync.SyncSkipped, 'running'):
            self.run_sync(lambda: True)
        self.assertEqual(self.git(self.local, 'rev-parse', 'origin/Legend/fast-track-release'), old_remote)

    def test_dirty_change_during_fetch_prevents_update(self):
        self.publish()
        calls = 0
        def probe():
            nonlocal calls
            calls += 1
            if calls == 1:
                # The first readiness check already read status; simulate an edit while fetching.
                (self.local / 'app.txt').write_text('concurrent edit')
            return False
        with self.assertRaisesRegex(sync.SyncSkipped, 'Local changes'):
            self.run_sync(probe)
        self.assertEqual((self.local / 'app.txt').read_text(), 'concurrent edit')

    def test_ignored_configuration_collision_is_not_overwritten(self):
        self.commit(self.publisher, '.gitignore', 'local-config.txt\n')
        self.git(self.publisher, 'push', 'origin', 'Legend/fast-track-release')
        self.run_sync()
        (self.local / 'local-config.txt').write_text('private local configuration')
        (self.publisher / 'local-config.txt').write_text('published configuration')
        self.git(self.publisher, 'add', '-f', 'local-config.txt')
        self.git(self.publisher, 'commit', '-m', 'fixture tracked configuration')
        self.git(self.publisher, 'push', 'origin', 'Legend/fast-track-release')
        old_head = self.git(self.local, 'rev-parse', 'HEAD')
        with self.assertRaises(sync.SyncSkipped): self.run_sync()
        self.assertEqual((self.local / 'local-config.txt').read_text(), 'private local configuration')
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), old_head)

    def test_fetch_failure_preserves_checkout_and_is_explicit(self):
        self.git(self.local, 'remote', 'set-url', 'origin', str(self.root / 'missing.git'))
        old_head = self.git(self.local, 'rev-parse', 'HEAD')
        with self.assertRaisesRegex(sync.SyncSkipped, 'fetch failed'):
            self.run_sync()
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), old_head)
        self.assertEqual((self.local / 'app.txt').read_text(), 'original')

    def test_ordinary_default_rejects_retired_branch_without_fetch(self):
        self.use_legacy_native_checkout()
        before = self.git(self.local, 'rev-parse', 'HEAD')
        with self.assertRaisesRegex(sync.SyncSkipped, 'working branch'), patch.object(sync, 'git', wraps=sync.git) as calls:
            self.run_sync()
        self.assertFalse(any('fetch' in call.args for call in calls.call_args_list))
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), before)

    def test_ordinary_readonly_status_names_current_canonical_target(self):
        report = sync.sync_status(self.local, process_probe=lambda: False)
        self.assertEqual(report['target'], 'refs/remotes/origin/Legend/fast-track-release')
        self.assertEqual(report['remoteFreshness'], 'not_checked')

    def test_native_check_reports_current_source_and_never_fetches_or_mutates(self):
        self.use_legacy_native_checkout()
        before = self.git(self.local, 'rev-parse', 'HEAD')
        remote = self.git(self.local, 'rev-parse', 'origin/legend/approved-changes')
        index = (self.local / '.git/index').read_bytes()
        (self.local / 'app.txt').write_text('current uncommitted native changes')
        self.publish()  # The checker deliberately cannot claim remote freshness.
        report = sync.check_native_checkout(self.local)
        self.assertIn('locally known ref', report)
        self.assertIn('localChanges=1', report)
        self.assertIn(str(self.local), report)
        self.assertIn(before, report)
        self.assertEqual(self.git(self.local, 'rev-parse', 'origin/legend/approved-changes'), remote)
        self.assertEqual((self.local / '.git/index').read_bytes(), index)
        self.assertEqual((self.local / 'app.txt').read_text(), 'current uncommitted native changes')

    def test_native_provenance_uses_verified_actual_head_and_no_private_paths(self):
        self.use_legacy_native_checkout()
        self.commit(self.local, 'ahead.txt', 'local candidate')
        (self.local / 'uncommitted.txt').write_text('working copy')
        output = self.root / 'DerivedData' / 'LegendBuildProvenance.json'
        before = self.git(self.local, 'status', '--porcelain=v1')
        sync.check_native_checkout(self.local, output)
        payload = json.loads(output.read_text())
        self.assertEqual(payload, {'schemaVersion': 1,
                                  'gitCommitHash': self.git(self.local, 'rev-parse', 'HEAD'),
                                  'hasLocalChanges': True})
        self.assertNotIn(str(self.local), output.read_text())
        self.assertEqual(self.git(self.local, 'status', '--porcelain=v1'), before)

    def test_native_provenance_removes_stale_artifact_when_guard_rejects_checkout(self):
        self.use_legacy_native_checkout()
        output = self.root / 'LegendBuildProvenance.json'
        sync.check_native_checkout(self.local, output)
        self.assertTrue(output.is_file())
        self.publish()
        self.git(self.local, 'fetch', 'origin')
        with self.assertRaisesRegex(sync.SyncSkipped, 'Stale or divergent'):
            sync.check_native_checkout(self.local, output)
        self.assertFalse(output.exists())

    def test_native_provenance_cannot_write_or_delete_source_or_git(self):
        self.use_legacy_native_checkout()
        for output in [self.local / 'source.json', self.local / '.git' / 'metadata.json']:
            with self.subTest(output=output):
                output.write_text('preserve existing file')
                with self.assertRaisesRegex(sync.SyncSkipped, 'outside the source'):
                    sync.check_native_checkout(self.local, output)
                self.assertEqual(output.read_text(), 'preserve existing file')

    def test_native_provenance_from_worktree_cannot_write_main_checkout(self):
        self.use_legacy_native_checkout()
        worktree = self.root / 'native-worktree'
        self.git(self.local, 'worktree', 'add', '--detach', str(worktree), 'HEAD')
        output = self.local / 'main-source.json'
        output.write_text('preserve main checkout')
        with self.assertRaisesRegex(sync.SyncSkipped, 'outside the source'):
            sync.check_native_checkout(worktree, output)
        self.assertEqual(output.read_text(), 'preserve main checkout')

    def test_native_check_fails_stale_approved_without_changing_local_work(self):
        self.use_legacy_native_checkout()
        before = self.git(self.local, 'rev-parse', 'HEAD')
        self.publish()
        self.git(self.local, 'fetch', 'origin')  # Explicit external refresh, never the build hook.
        (self.local / 'native-build.txt').write_text('user build 33')
        with self.assertRaisesRegex(sync.SyncSkipped, 'Stale or divergent native checkout'):
            sync.check_native_checkout(self.local)
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), before)
        self.assertEqual((self.local / 'native-build.txt').read_text(), 'user build 33')

    def test_native_check_uses_shared_testing_ref_and_allows_newer_working_commit(self):
        self.use_legacy_native_checkout()
        self.git(self.publisher, 'switch', '-c', 'implement/native-current')
        self.commit(self.publisher, 'native.txt', 'candidate')
        self.git(self.publisher, 'push', 'origin', 'implement/native-current')
        self.git(self.local, 'fetch', 'origin')
        target = 'refs/remotes/origin/implement/native-current'
        self.git(self.local, 'config', 'legend.nativeTestingRef', target)
        with self.assertRaisesRegex(sync.SyncSkipped, 'Stale or divergent'):
            sync.check_native_checkout(self.local)
        self.git(self.local, 'switch', '-c', 'native-test', target)
        self.commit(self.local, 'new-native.txt', 'newer current edits')
        self.assertIn('testingRef=' + target, sync.check_native_checkout(self.local))
        self.git(self.local, 'checkout', '--detach', 'HEAD')
        self.assertIn('branch=HEAD', sync.check_native_checkout(self.local))

    def test_native_check_missing_or_invalid_ref_fails_visible(self):
        self.use_legacy_native_checkout()
        for ref in ['', 'refs/remotes/origin/not-fetched', '--unsafe-option', 'HEAD~1']:
            with self.subTest(ref=ref):
                self.git(self.local, 'config', 'legend.nativeTestingRef', ref)
                with self.assertRaisesRegex(sync.SyncSkipped, 'missing or invalid'):
                    sync.check_native_checkout(self.local)


    def test_native_detached_shallow_ci_uses_existing_exact_workflow_candidate(self):
        self.use_legacy_native_checkout()
        head = self.git(self.local, 'rev-parse', 'HEAD')
        self.git(self.local, 'checkout', '--detach', head)
        self.git(self.local, 'update-ref', '-d', 'refs/remotes/origin/legend/approved-changes')
        # No ancestry beyond HEAD is required by this exact-candidate CI authority.
        (self.local / '.git/shallow').write_text(head + '\n')
        with patch.dict(sync.os.environ, {'GITHUB_ACTIONS': 'true', 'GITHUB_SHA': head, 'GITHUB_WORKSPACE': str(self.local)}):
            self.assertIn('workflow=GITHUB_SHA', sync.check_native_checkout(self.local))
            for overrides in [{'GITHUB_SHA': '0' * 40}, {'GITHUB_SHA': 'invalid'}, {'GITHUB_WORKSPACE': str(self.publisher)}]:
                with patch.dict(sync.os.environ, overrides), self.assertRaisesRegex(sync.SyncSkipped, 'existing GitHub workflow'):
                    sync.check_native_checkout(self.local)

    def test_explicit_testing_ref_cannot_be_bypassed_by_ci_environment(self):
        self.use_legacy_native_checkout()
        self.publish()
        self.git(self.local, 'fetch', 'origin')
        self.git(self.local, 'config', 'legend.nativeTestingRef', 'refs/remotes/origin/legend/approved-changes')
        with patch.dict(sync.os.environ, {'GITHUB_ACTIONS': 'true', 'GITHUB_SHA': self.git(self.local, 'rev-parse', 'HEAD'), 'GITHUB_WORKSPACE': str(self.local)}):
            with self.assertRaisesRegex(sync.SyncSkipped, 'Stale or divergent'):
                sync.check_native_checkout(self.local)



class RunningProcessDetectionTests(unittest.TestCase):
    def test_idle_java_daemon_does_not_block_but_active_gradle_wrapper_does(self):
        for arguments, expected in [
            ('java org.gradle.launcher.daemon.bootstrap.GradleDaemon 8.13', False),
            ('java org.gradle.wrapper.GradleWrapperMain bundleRelease', True),
        ]:
            with self.subTest(arguments=arguments), patch.object(sync.subprocess, 'run', side_effect=[
                subprocess.CompletedProcess([], 0, stdout='/usr/bin/java\n'),
                subprocess.CompletedProcess([], 0, stdout=arguments),
            ]):
                self.assertEqual(sync.active_build_or_app(), expected)

    def test_native_editor_gate_includes_dotnet_but_does_not_block_idle_code_or_codex(self):
        for executable, expected in [
            ('/Applications/Xcode.app/Contents/MacOS/Xcode', True),
            ('/Applications/Android Studio.app/Contents/MacOS/studio', True),
            ('/usr/local/share/dotnet/dotnet', True),
            ('/Applications/Visual Studio Code.app/Contents/MacOS/Electron', False),
            ('/usr/local/bin/codex', False),
        ]:
            with self.subTest(executable=executable), patch.object(sync.subprocess, 'run', return_value=
                    subprocess.CompletedProcess([], 0, stdout=executable + '\n')):
                self.assertEqual(sync.native_editor_or_build_active(), expected)

    def test_native_editor_gate_fails_closed_if_process_inspection_fails(self):
        with patch.object(sync.subprocess, 'run', return_value=subprocess.CompletedProcess([], 1, stdout='')):
            with self.assertRaisesRegex(sync.SyncSkipped, 'Cannot verify'):
                sync.native_editor_or_build_active()

    def test_unknown_dotnet_process_still_blocks(self):
        with patch.object(sync.subprocess, 'run', return_value=
                          subprocess.CompletedProcess([], 0, stdout='/usr/local/share/dotnet/dotnet\n')) as run:
            self.assertTrue(sync.active_build_or_app())
            self.assertEqual(run.call_count, 2)

    def test_idle_dotnet_servers_do_not_block_but_real_work_does(self):
        idle = '/usr/bin/dotnet /sdk/MSBuild.dll /nodemode:1\n/usr/bin/dotnet exec /sdk/VBCSCompiler.dll\n'
        for arguments, expected in [(idle, False), (idle + '/usr/bin/dotnet build MASTERAPP.sln', True),
                                    ('/usr/bin/dotnet /publish/AgentPortal.dll', True),
                                    ('/usr/bin/dotnet unknown', True)]:
            with self.subTest(arguments=arguments), patch.object(sync.subprocess, 'run', side_effect=[
                subprocess.CompletedProcess([], 0, stdout='/usr/bin/dotnet\n'),
                subprocess.CompletedProcess([], 0, stdout=arguments),
            ]):
                self.assertEqual(sync.native_editor_or_build_active(), expected)


class SessionContinuityTests(unittest.TestCase):
    git = PublishedCheckoutSyncTests.git
    identity = PublishedCheckoutSyncTests.identity
    commit = PublishedCheckoutSyncTests.commit

    def setUp(self):
        PublishedCheckoutSyncTests.setUp(self)
        self.branch = 'isolation/fixture'
        self.git(self.local, 'switch', '-c', self.branch)
        self.git(self.local, 'push', 'origin', self.branch)
        self.base = self.git(self.local, 'rev-parse', 'HEAD')
        allowed = patch.object(sync, 'CANONICAL_REMOTES', {str(self.remote)})
        allowed.start()
        self.addCleanup(allowed.stop)

    def run_session(self, mode='preflight', *extra):
        return sync.session_main(['--session', mode, '--repo', str(self.local),
                                  '--branch', self.branch, '--base', self.base, *extra])

    def advance_remote(self):
        self.git(self.publisher, 'fetch', 'origin', self.branch)
        self.git(self.publisher, 'switch', '-c', self.branch, 'FETCH_HEAD')
        self.commit(self.publisher, 'remote.txt', 'remote work')
        self.git(self.publisher, 'push', 'origin', self.branch)
        return self.git(self.publisher, 'rev-parse', 'HEAD')

    def finish_args(self):
        head = self.git(self.local, 'rev-parse', 'HEAD')
        return ['--accepted', head, '--implementation', head, '--scope', 'local.txt',
                '--review-accepted', '--authorize-push']

    def test_fresh_and_missing_github_cli(self):
        # No gh invocation exists; guard every subprocess invocation explicitly.
        original = sync.subprocess.run
        def no_gh(command, **kwargs):
            self.assertNotEqual(command[0], 'gh')
            return original(command, **kwargs)
        with patch.object(sync.subprocess, 'run', side_effect=no_gh):
            self.assertEqual(self.run_session(), 0)

    def test_unpublished_work_requires_durable_preservation(self):
        self.commit(self.local, 'local.txt', 'keep')
        head = self.git(self.local, 'rev-parse', 'HEAD')
        self.assertEqual(self.run_session(), 1)
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), head)
        self.assertEqual(sync.remote_head(self.local, self.branch), self.base)

    def test_uncommitted_work_preserved(self):
        (self.local / 'local.txt').write_text('keep')
        self.assertEqual(self.run_session(), 1)
        self.assertEqual((self.local / 'local.txt').read_text(), 'keep')

    def test_missing_authentication_is_sanitized(self):
        original = sync.git
        def denied(repo, *args, **kwargs):
            if args[0] == 'ls-remote':
                raise sync.SyncSkipped('Git ls-remote failed; check native authentication.')
            return original(repo, *args, **kwargs)
        with patch.object(sync, 'git', side_effect=denied):
            self.assertEqual(self.run_session(), 1)

    def test_remote_advanced_explicit_fast_forward(self):
        remote = self.advance_remote()
        self.assertEqual(self.run_session(), 1)
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), self.base)
        with patch.object(sync, 'active_build_or_app', return_value=False):
            self.assertEqual(self.run_session('recover'), 0)
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), remote)

    def test_divergence_reports_exact_sides_without_mutation(self):
        self.commit(self.local, 'local.txt', 'local')
        local = self.git(self.local, 'rev-parse', 'HEAD')
        remote = self.advance_remote()
        output = io.StringIO()
        with redirect_stdout(output):
            self.assertEqual(self.run_session('recover'), 1)
        self.assertIn(local, output.getvalue())
        self.assertIn(remote, output.getvalue())
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), local)

    def test_accepted_push_and_exact_readback(self):
        self.commit(self.local, 'local.txt', 'accepted')
        self.assertEqual(self.run_session('finish', *self.finish_args()), 0)
        self.assertEqual(sync.remote_head(self.local, self.branch), self.git(self.local, 'rev-parse', 'HEAD'))

    def test_unaccepted_and_wrong_scope_do_not_push(self):
        self.commit(self.local, 'local.txt', 'unaccepted')
        self.assertEqual(self.run_session('finish'), 1)
        self.assertEqual(self.run_session('finish', *self.finish_args(), '--scope', 'wrong.txt'), 1)
        self.assertEqual(sync.remote_head(self.local, self.branch), self.base)

    def test_failed_push_exports_once_without_retry(self):
        self.commit(self.local, 'local.txt', 'accepted')
        hook = self.remote / 'hooks' / 'pre-receive'
        hook.write_text('#!/bin/sh\nexit 1\n')
        hook.chmod(0o755)
        with patch.object(sync, 'export_session') as export:
            self.assertEqual(self.run_session('finish', *self.finish_args()), 1)
            export.assert_called_once()
        self.assertEqual(sync.remote_head(self.local, self.branch), self.base)

    def test_interrupted_push_reconciles_remote_without_retry(self):
        self.commit(self.local, 'local.txt', 'accepted')
        original = sync.git
        pushes = []
        def interrupted(repo, *args, **kwargs):
            result = original(repo, *args, **kwargs)
            if args[0] == 'push':
                pushes.append(args)
                raise sync.SyncSkipped('Transport interrupted after receive.')
            return result
        with patch.object(sync, 'git', side_effect=interrupted):
            self.assertEqual(self.run_session('finish', *self.finish_args()), 0)
        self.assertEqual(len(pushes), 1)

    def test_missing_and_invalid_bundle(self):
        for path in (self.root / 'missing.bundle', self.root / 'bad.bundle'):
            if path.name == 'bad.bundle':
                path.write_text('invalid')
            self.assertEqual(self.run_session('restore', '--bundle', str(path), '--sha256', '0' * 64,
                                             '--accepted', self.base), 1)
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), self.base)

    def test_verified_full_bundle_restore(self):
        self.git(self.publisher, 'switch', '-c', self.branch)
        self.commit(self.publisher, 'recovered.txt', 'original')
        head = self.git(self.publisher, 'rev-parse', 'HEAD')
        bundle = self.root / 'good.bundle'
        self.git(self.publisher, 'bundle', 'create', str(bundle), 'refs/heads/' + self.branch)
        with patch.object(sync, 'active_build_or_app', return_value=False):
            self.assertEqual(self.run_session('restore', '--bundle', str(bundle), '--sha256',
                                             sync.bundle_digest(bundle), '--accepted', head), 0)
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), head)
        self.assertTrue(sync.ancestor(self.local, self.base, head))

    def test_temporary_storage_is_not_durable(self):
        with self.assertRaisesRegex(sync.SyncSkipped, 'temporary'):
            sync.export_session(self.local, self.branch, self.base, self.base, self.root, True)

    def test_wrong_remote_and_branch_block(self):
        with patch.object(sync, 'CANONICAL_REMOTES', set()):
            self.assertEqual(self.run_session(), 1)
        self.git(self.local, 'switch', 'legend/approved-changes')
        self.assertEqual(self.run_session(), 1)


    def test_recovery_branch_switch_race_is_blocked(self):
        self.advance_remote()
        def switch_branch():
            self.git(self.local, 'switch', 'legend/approved-changes')
            return False
        with patch.object(sync, 'active_build_or_app', side_effect=switch_branch):
            self.assertEqual(self.run_session('recover'), 1)
        self.assertEqual(self.git(self.local, 'rev-parse', 'legend/approved-changes'), self.base)
        self.assertEqual(self.git(self.local, 'rev-parse', self.branch), self.base)

    def test_real_failed_push_bundle_export_and_empty_restore(self):
        self.commit(self.local, 'local.txt', 'accepted')
        head = self.git(self.local, 'rev-parse', 'HEAD')
        hook = self.remote / 'hooks' / 'pre-receive'
        hook.write_text('#!/bin/sh\nexit 1\n')
        hook.chmod(0o755)
        # Fixture-only storage outside system temp and the disposable Git checkout.
        with tempfile.TemporaryDirectory(prefix='.continuity-test-storage-', dir=Path(__file__).resolve().parent) as storage:
            self.assertEqual(self.run_session('finish', *self.finish_args(), '--recovery-dir', storage,
                                             '--external-storage-confirmed'), 1)
            bundles = list(Path(storage).glob('*/LEGEND.bundle'))
            self.assertEqual(len(bundles), 1)
            bundle = bundles[0]
            digest = sync.bundle_digest(bundle)
            self.assertEqual(bundle.with_suffix('.bundle.sha256').read_text(), digest + '  LEGEND.bundle\n')
            sync.verify_bundle(self.local, bundle, digest, self.branch, head, self.base)
            second = sync.export_session(self.local, self.branch, head, self.base, storage, True)
            self.assertNotEqual(bundle, second)
            self.assertEqual(sync.bundle_digest(bundle), digest)
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), head)
        self.assertEqual(sync.remote_head(self.local, self.branch), self.base)

    def test_offline_completion_preserves_accepted_bundle(self):
        self.commit(self.local, 'local.txt', 'accepted')
        original = sync.git
        def offline(repo, *args, **kwargs):
            if args[0] == 'ls-remote':
                raise sync.SyncSkipped('Native Git transport unavailable.')
            return original(repo, *args, **kwargs)
        with tempfile.TemporaryDirectory(prefix='.continuity-test-storage-', dir=Path(__file__).resolve().parent) as storage:
            with patch.object(sync, 'git', side_effect=offline):
                self.assertEqual(self.run_session('finish', *self.finish_args(), '--recovery-dir', storage,
                                                 '--external-storage-confirmed'), 1)
            self.assertEqual(len(list(Path(storage).glob('*/LEGEND.bundle'))), 1)

    def test_incremental_and_wrong_ref_bundles_rejected(self):
        self.commit(self.local, 'local.txt', 'accepted')
        head = self.git(self.local, 'rev-parse', 'HEAD')
        partial = self.root / 'partial.bundle'
        self.git(self.local, 'bundle', 'create', str(partial), 'refs/heads/' + self.branch, '^' + self.base)
        with self.assertRaises(sync.SyncSkipped):
            sync.verify_bundle(self.local, partial, sync.bundle_digest(partial), self.branch, head, self.base)
        wrong = self.root / 'wrong.bundle'
        self.git(self.local, 'bundle', 'create', str(wrong), 'refs/heads/legend/approved-changes')
        with self.assertRaises(sync.SyncSkipped):
            sync.verify_bundle(self.local, wrong, sync.bundle_digest(wrong), self.branch, head, self.base)

    def test_dirty_restore_preserves_work(self):
        bundle = self.root / 'restore.bundle'
        self.git(self.local, 'bundle', 'create', str(bundle), 'refs/heads/' + self.branch)
        (self.local / 'dirty.txt').write_text('preserve')
        with patch.object(sync, 'active_build_or_app', return_value=False):
            self.assertEqual(self.run_session('restore', '--bundle', str(bundle), '--sha256',
                                             sync.bundle_digest(bundle), '--accepted', self.base), 1)
        self.assertEqual((self.local / 'dirty.txt').read_text(), 'preserve')


    def test_remote_movement_before_push_stops_and_exports(self):
        self.commit(self.local, 'local.txt', 'accepted')
        original = sync.remote_head
        reads = []
        def moved(repo, branch):
            reads.append(branch)
            if len(reads) == 3:
                return 'f' * 40
            return original(repo, branch)
        with patch.object(sync, 'remote_head', side_effect=moved), patch.object(sync, 'export_session') as export:
            self.assertEqual(self.run_session('finish', *self.finish_args()), 1)
            export.assert_called_once()
        self.assertEqual(sync.remote_head(self.local, self.branch), self.base)

    def test_restore_branch_switch_race_is_blocked(self):
        self.git(self.publisher, 'switch', '-c', self.branch)
        self.commit(self.publisher, 'recovered.txt', 'original')
        head = self.git(self.publisher, 'rev-parse', 'HEAD')
        bundle = self.root / 'race.bundle'
        self.git(self.publisher, 'bundle', 'create', str(bundle), 'refs/heads/' + self.branch)
        def switch_branch():
            self.git(self.local, 'switch', 'legend/approved-changes')
            return False
        with patch.object(sync, 'active_build_or_app', side_effect=switch_branch):
            self.assertEqual(self.run_session('restore', '--bundle', str(bundle), '--sha256',
                                             sync.bundle_digest(bundle), '--accepted', head), 1)
        self.assertEqual(self.git(self.local, 'rev-parse', 'legend/approved-changes'), self.base)
        self.assertEqual(self.git(self.local, 'rev-parse', self.branch), self.base)


    def test_push_cannot_publish_implicit_tags_or_submodule_refs(self):
        self.commit(self.local, 'local.txt', 'accepted')
        self.git(self.local, 'tag', '-a', 'unreviewed-tag', '-m', 'fixture tag')
        self.git(self.local, 'config', 'push.followTags', 'true')
        self.git(self.local, 'config', 'push.recurseSubmodules', 'on-demand')
        original = sync.git
        pushes = []
        def scoped(repo, *args, **kwargs):
            if args[0] == 'push':
                pushes.append(args)
            return original(repo, *args, **kwargs)
        with patch.object(sync, 'git', side_effect=scoped):
            self.assertEqual(self.run_session('finish', *self.finish_args()), 0)
        self.assertEqual(self.git(self.local, 'ls-remote', 'origin', 'refs/tags/*'), '')
        self.assertEqual(len(pushes), 1)
        self.assertIn('--no-follow-tags', pushes[0])
        self.assertIn('--recurse-submodules=no', pushes[0])


class TwoPhaseRecoveryTests(unittest.TestCase):
    git = PublishedCheckoutSyncTests.git
    identity = PublishedCheckoutSyncTests.identity
    commit = PublishedCheckoutSyncTests.commit
    run_session = SessionContinuityTests.run_session

    def setUp(self):
        SessionContinuityTests.setUp(self)
        self.commit(self.local, 'implementation.txt', 'accepted implementation')
        self.implementation = self.git(self.local, 'rev-parse', 'HEAD')
        self.commit(self.local, 'checkpoint.txt', 'accepted checkpoint')
        self.head = self.git(self.local, 'rev-parse', 'HEAD')
        self.codespace = 'bookish-fiesta-97pj77j5vpwj3pjv6'
        self.stage_args = ['--accepted',self.head,'--implementation',self.implementation,
                           '--required-commit',self.head]

    def stage(self):
        import shutil
        output=io.StringIO()
        with patch.dict(sync.os.environ, {'CODESPACES':'true','CODESPACE_NAME':self.codespace}),redirect_stdout(output):
            self.assertEqual(0,self.run_session('stage-local',*self.stage_args))
        result=json.loads(output.getvalue())
        self.addCleanup(shutil.rmtree,result['stagedDirectory'])
        return result

    def external(self, staged, change=None, transport=None):
        import shutil
        storage=tempfile.TemporaryDirectory(prefix='.external-recovery-test-',dir=Path.home())
        self.addCleanup(storage.cleanup)
        self.external_storage=Path(storage.name)
        args=['--session','verify-external','--branch',self.branch,'--base',self.base,
            '--accepted',self.head,'--implementation',self.implementation,'--required-commit',self.head,
            '--codespace',self.codespace,'--staged-dir',staged['stagedDirectory'],
            '--sha256',staged['sha256'],'--verifier-sha256',staged['verifierSha256'],
            '--recovery-dir',storage.name,'--external-storage-confirmed']
        if change:change(args)
        calls=[]
        def gh(*command):
            calls.append(command)
            if transport:
                intercepted=transport(command)
                if intercepted is not None:return intercepted
            if command==('auth','status','--hostname','github.com'):return ''
            if command==('api','--hostname','github.com','user'):return json.dumps({'login':'Founder'})
            if command==('api','--hostname','github.com','user/codespaces/'+self.codespace):
                return json.dumps({'name':self.codespace,'state':'Available','repository':{'full_name':'MYLEGND/masterapp'},'owner':{'login':'Founder'}})
            self.assertEqual(('codespace','cp','-c',self.codespace),command[:4])
            self.assertTrue(command[4].startswith('remote:'+staged['stagedDirectory']+'/'))
            shutil.copyfile(command[4].removeprefix('remote:'),command[5])
            return ''
        output=io.StringIO()
        with patch.object(sync.sys,'platform','darwin'),patch.dict(sync.os.environ,{},clear=True),patch.object(sync,'external_gh',side_effect=gh),redirect_stdout(output):
            code=sync.session_main(args)
        return code,output.getvalue(),calls

    def test_real_stage_is_never_durable_or_preflight_admission(self):
        result=self.stage()
        self.assertEqual('STAGED_LOCAL_NOT_DURABLE',result['state'])
        self.assertFalse(result['durable']);self.assertFalse(result['continuationAllowed']);self.assertFalse(result['remotePreserved'])
        self.assertEqual(self.head,self.git(self.local,'rev-parse','HEAD'))
        self.assertEqual(self.base,self.git(self.remote,'rev-parse','refs/heads/'+self.branch))
        self.assertEqual(1,self.run_session())
        with patch.object(sync,'remote_head',side_effect=sync.SyncSkipped('offline')):
            self.assertEqual(1,self.run_session('finish','--accepted',self.head,'--implementation',self.implementation,
                '--scope','implementation.txt','--scope','checkpoint.txt','--review-accepted','--authorize-push'))

    def test_stage_rejects_dirty_stale_and_nonancestor_identities(self):
        with patch.dict(sync.os.environ,{'CODESPACES':'true','CODESPACE_NAME':self.codespace}):
            (self.local/'dirty.txt').write_text('uncommitted')
            self.assertEqual(1,self.run_session('stage-local',*self.stage_args))
            (self.local/'dirty.txt').unlink()
            args=list(self.stage_args);args[1]=self.base
            self.assertEqual(1,self.run_session('stage-local',*args))
            tree=self.git(self.local,'rev-parse','HEAD^{tree}')
            orphan=self.git(self.local,'commit-tree',tree,'-m','unrelated')
            args=list(self.stage_args);args[-1]=orphan
            self.assertEqual(1,self.run_session('stage-local',*args))
            shallow=Path(self.git(self.local,'rev-parse','--absolute-git-dir'))/'shallow'
            shallow.write_text(self.head+'\n')
            self.assertEqual(1,self.run_session('stage-local',*self.stage_args))
            shallow.unlink()

    def test_mixed_staging_privilege_flags_are_rejected(self):
        for extra in ('--authorize-push','--external-storage-confirmed','--review-accepted'):
            with self.subTest(flag=extra),self.assertRaises(SystemExit):
                self.run_session('stage-local',*self.stage_args,extra)

    def test_real_independent_transfer_and_empty_restore_prove_all_commits(self):
        staged=self.stage();code,output,calls=self.external(staged)
        self.assertEqual(0,code)
        receipt=json.loads(output)
        self.assertEqual('VERIFIED_EXTERNAL',receipt['state'])
        self.assertTrue(receipt['emptyRepositoryRestore']);self.assertTrue(receipt['ancestryVerified'])
        self.assertTrue(receipt['recoveryOnly']);self.assertFalse(receipt['remotePreserved'])
        self.assertEqual([self.head],receipt['requiredCommits'])
        self.assertEqual(2,len([c for c in calls if c[:2]==('codespace','cp')]))
        self.assertEqual(staged['sha256'],sync.bundle_digest(receipt['recoveryBundle']))
        self.assertEqual(self.base,self.git(self.remote,'rev-parse','refs/heads/'+self.branch))

    def test_external_wrong_digest_head_checkpoint_and_script_reject(self):
        staged=self.stage()
        for flag,value in (('--sha256','f'*64),('--accepted',self.base),('--required-commit','e'*40),('--verifier-sha256','f'*64)):
            def change(args):args[args.index(flag)+1]=value
            with self.subTest(flag=flag):
                code,output,_=self.external(staged,change)
                self.assertEqual(1,code);self.assertNotIn('VERIFIED_EXTERNAL',output)

    def test_authenticated_transfer_failure_or_wrong_origin_emits_no_success(self):
        staged=self.stage()
        def failure(command):
            if command[:2]==('codespace','cp'):raise sync.SyncSkipped('fixture transfer failed')
        def foreign(command):
            if command[:3]==('api','--hostname','github.com') and command[-1].startswith('user/codespaces/'):
                return json.dumps({'name':self.codespace,'state':'Available','repository':{'full_name':'other/repo'},'owner':{'login':'Founder'}})
        def malformed(command):
            if command[:3]==('api','--hostname','github.com') and command[-1].startswith('user/codespaces/'):
                return json.dumps({'name':self.codespace,'state':'Available','repository':[], 'owner':None})
        for transport in (failure,foreign,malformed):
            code,output,_=self.external(staged,transport=transport)
            self.assertEqual(1,code);self.assertNotIn('VERIFIED_EXTERNAL',output)

    def test_partial_and_wrong_ref_bundles_cannot_be_external_receipts(self):
        staged=self.stage();bundle=Path(staged['recoveryBundle'])
        for ref,exclude in (('refs/heads/'+self.branch,['^'+self.base]),('refs/heads/legend/approved-changes',[])):
            bundle.unlink()
            self.git(self.local,'bundle','create',str(bundle),ref,*exclude)
            staged['sha256']=sync.bundle_digest(bundle)
            bundle.with_suffix('.bundle.sha256').write_text(staged['sha256']+'  LEGEND.bundle\n')
            code,output,_=self.external(staged)
            self.assertEqual(1,code);self.assertNotIn('VERIFIED_EXTERNAL',output)

    def test_external_untrusted_path_temp_destination_or_execution_host_reject(self):
        staged=self.stage()
        for flag,value in (('--staged-dir','/tmp/../unsafe'),('--codespace','bad;command'),('--recovery-dir',str(self.root))):
            def change(args):args[args.index(flag)+1]=value
            code,output,calls=self.external(staged,change)
            self.assertEqual(1,code);self.assertEqual([],calls);self.assertNotIn('VERIFIED_EXTERNAL',output)
        from types import SimpleNamespace
        for platform,env in (('linux',{}),('darwin',{'CODESPACES':'true'}),('darwin',{'GITHUB_ACTIONS':'true'})):
            with patch.object(sync.sys,'platform',platform),patch.dict(sync.os.environ,env,clear=True),self.assertRaises(sync.SyncSkipped):
                sync.verify_external_session(SimpleNamespace())


    def test_failed_receipt_persistence_never_leaves_success_marker(self):
        staged=self.stage()
        original_fsync=sync.os.fsync
        def fsync(descriptor):
            # Fail after the atomic receipt rename to exercise cleanup of the
            # success marker, not merely an earlier artifact write.
            if hasattr(self,'external_storage') and list(self.external_storage.glob('*/external-verification.json')):
                raise OSError('fixture receipt directory sync failure')
            return original_fsync(descriptor)
        with patch.object(sync.os,'fsync',side_effect=fsync):
            code,output,_=self.external(staged)
        self.assertEqual(1,code);self.assertNotIn('VERIFIED_EXTERNAL',output)
        self.assertEqual([],list(self.external_storage.glob('*/external-verification.json')))
        self.assertEqual([],list(self.external_storage.glob('*/.external-verification.pending')))


class CloudRecoveryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='legend-cloud-test-')
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name) / 'fixture'
        self.repo.mkdir()
        self.branch = 'isolation/cloud-synthetic'
        self.g('init', '-b', self.branch)
        self.g('config', 'user.name', 'Synthetic recovery fixture')
        self.g('config', 'user.email', 'fixture@example.invalid')
        self.g('remote', 'add', 'origin', 'https://github.com/MYLEGND/masterapp.git')
        for name, body in {'text.txt':'base\n', 'delete.txt':'delete\n', 'mode.sh':'echo fixture\n', '.gitignore':'saved-buffer.txt\n'}.items():
            (self.repo/name).write_text(body)
        self.g('add', '.')
        self.g('commit', '-m', 'synthetic base')
        self.base = self.g('rev-parse', 'HEAD')
        self.blobs = {}
        owner = self
        class BlobFixture:
            def transfer(self, name, path, upload=False):
                if upload:
                    if name in owner.blobs:
                        raise OSError('immutable conflict')
                    owner.blobs[name] = Path(path).read_bytes()
                    return 'synthetic-etag'
                Path(path).write_bytes(owner.blobs[name])
        self.client = BlobFixture
        from types import SimpleNamespace
        self.args = SimpleNamespace(branch=self.branch, base=self.base, capture_path=[])

    def g(self, *args):
        r = subprocess.run(['git','-C',str(self.repo),*args],capture_output=True,text=True)
        self.assertEqual(0,r.returncode,r.stderr)
        return r.stdout.strip()

    def mixed(self, role):
        (self.repo/'text.txt').write_text(role+' staged\n')
        (self.repo/'binary.bin').write_bytes(bytes(range(256)))
        self.g('add','text.txt','binary.bin')
        (self.repo/'text.txt').write_text(role+' unstaged\n')
        (self.repo/'delete.txt').unlink()
        (self.repo/'mode.sh').chmod(0o755)
        (self.repo/'untracked.txt').write_text(role+' unfinished\n')
        (self.repo/'saved-buffer.txt').write_text(role+' exported editor buffer\n')
        self.args.capture_path=['untracked.txt','saved-buffer.txt']

    def export(self, client=None):
        out=io.StringIO()
        with patch.object(sync,'RecoveryBlobClient',client or self.client), patch.dict(sync.os.environ,{'CODESPACE_NAME':'bookish-fiesta-97pj77j5vpwj3pjv6'}), redirect_stdout(out):
            sync.cloud_export_session(self.repo,self.args)
        return json.loads(out.getvalue())

    def direction(self, role):
        self.mixed(role)
        before=sync.cloud_state(self.repo,self.args.capture_path)
        receipt=self.export()
        self.assertFalse(receipt['independentHostVerified'])
        archive=Path(self.temp.name)/'external-only.zip'
        archive.write_bytes(self.blobs[receipt['object']])
        result=sync.cloud_restore_archive(archive,receipt['sha256'],self.branch,self.base,self.base)
        self.assertEqual('FRESH_DISPOSABLE_RESTORATION_VERIFIED',result['status'])
        self.assertEqual(2,result['untrackedOrIgnoredFiles'])
        self.assertTrue(result['staged'] and result['unstaged'])
        self.assertEqual(before,sync.cloud_state(self.repo,self.args.capture_path))

    def test_astra_to_reserve_saved_state(self): self.direction('Astra to Reserve')
    def test_reserve_to_astra_saved_state(self): self.direction('Reserve to Astra')

    def test_corrupt_and_wrong_base_rejected(self):
        receipt=self.export()
        archive=Path(self.temp.name)/'download.zip'
        archive.write_bytes(self.blobs[receipt['object']])
        with self.assertRaises(sync.SyncSkipped): sync.cloud_restore_archive(archive,'0'*64,self.branch,self.base,self.base)
        with self.assertRaises(sync.SyncSkipped): sync.cloud_restore_archive(archive,receipt['sha256'],self.branch,'0'*40,self.base)
        archive.write_bytes(archive.read_bytes()+b'corrupt')
        with self.assertRaises(sync.SyncSkipped): sync.cloud_restore_archive(archive,receipt['sha256'],self.branch,self.base,self.base)

    def test_unclassified_or_secret_saved_files_rejected(self):
        (self.repo/'untracked.txt').write_text('unfinished')
        with self.assertRaises(sync.SyncSkipped): self.export()
        self.args.capture_path=['untracked.txt']
        (self.repo/'untracked.txt').write_text('-----BEGIN ' + 'PRIVATE KEY-----')
        with self.assertRaises(sync.SyncSkipped): self.export()
        self.assertEqual({},self.blobs)

    def test_unsafe_paths_rejected(self):
        for path in ['../escape','/absolute','.git/config','.env','key.pem','a/../b']:
            with self.assertRaises(sync.SyncSkipped): sync.cloud_safe_path(path)

    def test_failed_authentication_retains_originals(self):
        self.mixed('credential failure')
        before=sync.cloud_state(self.repo,self.args.capture_path)
        def denied(): raise sync.SyncSkipped('synthetic credential failure')
        with self.assertRaises(sync.SyncSkipped): self.export(denied)
        self.assertEqual({},self.blobs)
        self.assertEqual(before,sync.cloud_state(self.repo,self.args.capture_path))

    def test_network_quota_and_interruption_never_publish_receipt(self):
        self.mixed('interruption')
        for error in [OSError('network'), OSError('quota'), KeyboardInterrupt()]:
            parent=self.client
            class Interrupted(parent):
                def transfer(inner,name,path,upload=False):
                    if name.endswith('recovery.zip') and not upload: raise error
                    return super().transfer(name,path,upload)
            with self.assertRaises(type(error)): self.export(Interrupted)
            self.assertFalse(any(x.endswith('/receipt.json') for x in self.blobs))
        receipt=self.export()
        self.assertIn(receipt['object'],self.blobs)

    def test_changed_during_capture_rejected_before_upload(self):
        original=sync.cloud_state
        calls=0
        def changing(repo,include):
            nonlocal calls
            calls+=1
            if calls==2: (self.repo/'text.txt').write_text('concurrent edit')
            return original(repo,include)
        with patch.object(sync,'cloud_state',changing),self.assertRaises(sync.SyncSkipped): self.export()
        self.assertEqual({},self.blobs)



if __name__ == '__main__':
    unittest.main()
