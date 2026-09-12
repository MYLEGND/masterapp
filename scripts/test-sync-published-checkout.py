#!/usr/bin/env python3
"""Exercise the launcher sync against disposable real Git repositories."""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('published_sync', Path(__file__).with_name('sync-published-checkout.py'))
sync = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sync)


class PublishedCheckoutSyncTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='legend-sync-test-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.remote, self.publisher, self.local = [self.root / name for name in ['remote.git', 'publisher', 'local']]
        self.git(self.root, 'init', '--bare', str(self.remote))
        self.git(self.root, 'init', '-b', 'production', str(self.publisher))
        self.identity(self.publisher)
        self.commit(self.publisher, 'app.txt', 'original')
        self.git(self.publisher, 'remote', 'add', 'origin', str(self.remote))
        self.git(self.publisher, 'push', '-u', 'origin', 'production')
        self.git(self.root, 'clone', '-b', 'production', str(self.remote), str(self.local))
        self.identity(self.local)

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
        self.git(self.publisher, 'push', 'origin', 'production')

    def run_sync(self, probe=lambda: False):
        return sync.sync_checkout(self.local, process_probe=probe)

    def test_clean_published_checkout_fast_forwards_then_is_current(self):
        self.publish()
        self.assertIn('Updated', self.run_sync())
        self.assertEqual((self.local / 'app.txt').read_text(), 'published')
        self.assertEqual(self.git(self.local, 'rev-parse', 'HEAD'), self.git(self.publisher, 'rev-parse', 'HEAD'))
        self.assertIn('current', self.run_sync())

    def test_dirty_staged_and_untracked_files_are_preserved_without_fetch(self):
        self.publish()
        old_remote = self.git(self.local, 'rev-parse', 'origin/production')
        for mode in ['unstaged', 'staged', 'untracked']:
            with self.subTest(mode=mode):
                file = self.local / ('native-build.txt' if mode == 'untracked' else 'app.txt')
                file.write_text('user build 32')
                if mode == 'staged': self.git(self.local, 'add', 'app.txt')
                with self.assertRaisesRegex(sync.SyncSkipped, 'Local changes'):
                    self.run_sync()
                self.assertEqual(file.read_text(), 'user build 32')
                self.assertEqual(self.git(self.local, 'rev-parse', 'origin/production'), old_remote)
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
        self.git(self.local, 'switch', 'production')
        self.git(self.local, 'branch', '--unset-upstream')
        with self.assertRaises(sync.SyncSkipped): self.run_sync()

    def test_active_app_blocks_before_fetch(self):
        self.publish()
        old_remote = self.git(self.local, 'rev-parse', 'origin/production')
        with self.assertRaisesRegex(sync.SyncSkipped, 'running'):
            self.run_sync(lambda: True)
        self.assertEqual(self.git(self.local, 'rev-parse', 'origin/production'), old_remote)

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
        self.git(self.publisher, 'push', 'origin', 'production')
        self.run_sync()
        (self.local / 'local-config.txt').write_text('private local configuration')
        (self.publisher / 'local-config.txt').write_text('published configuration')
        self.git(self.publisher, 'add', '-f', 'local-config.txt')
        self.git(self.publisher, 'commit', '-m', 'fixture tracked configuration')
        self.git(self.publisher, 'push', 'origin', 'production')
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

    def test_dotnet_process_blocks_without_reading_arguments(self):
        with patch.object(sync.subprocess, 'run', return_value=
                          subprocess.CompletedProcess([], 0, stdout='/usr/local/share/dotnet/dotnet\n')) as run:
            self.assertTrue(sync.active_build_or_app())
            run.assert_called_once()


if __name__ == '__main__':
    unittest.main()
