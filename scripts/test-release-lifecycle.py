#!/usr/bin/env python3
"""Adversarial branch lifecycle tests; isolated git repositories, no network writes."""
import json
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('lifecycle', Path(__file__).with_name('release-lifecycle.py'))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


# These legacy orchestration cases exercise release mode; staging has dedicated cases below.
def setUpModule():
    global release_policy_patch
    release_policy_patch = patch.object(m, 'staging_only', return_value=False)
    release_policy_patch.start()

def tearDownModule():
    release_policy_patch.stop()


class BranchSafety(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.previous = Path.cwd()
        os.chdir(self.tmp.name)
        m.git('init', '-b', 'production')
        m.git('config', 'user.name', 'Test')
        m.git('config', 'user.email', 'test@example.invalid')
        Path('base').write_text('base')
        m.git('add', '.')
        m.git('commit', '-m', 'base')
        self.base = m.git('rev-parse', 'HEAD').stdout.strip()
        m.git('switch', '-c', 'work')
        Path('web').write_text('change')
        m.git('add', '.')
        m.git('commit', '-m', 'work')
        self.work = m.git('rev-parse', 'HEAD').stdout.strip()
        self.branch = {'name': 'work', 'commit': {'sha': self.work}, 'protected': False}
        self.live = [{'revision': self.work} for _ in range(5)]

    def test_direct_only_request_is_bound_to_exact_commit(self):
        path = Path('Docs/releases/direct-release-request.json')
        path.parent.mkdir(parents=True)
        path.write_text(json.dumps({'releaseMode': 'approved-only'}))
        m.git('add', '.')
        m.git('commit', '-m', 'authorized direct-only request')
        request_sha = m.git('rev-parse', 'HEAD').stdout.strip()
        self.assertTrue(m.direct_only_request(request_sha))
        Path('web').write_text('later unrelated change')
        m.git('add', '.')
        m.git('commit', '-m', 'later update')
        self.assertFalse(m.direct_only_request(m.git('rev-parse', 'HEAD').stdout.strip()))

    def test_direct_only_reconcile_never_dispatches_production(self):
        from unittest.mock import Mock
        api = Mock()
        api.ref.side_effect = [self.base, self.work]
        with patch.object(m, 'direct_only_request', return_value=True):
            result = m.reconcile(api)
        self.assertIn('disabled', result['promotion'])
        api.dispatch.assert_not_called()
        api.api.assert_not_called()

    def tearDown(self):
        os.chdir(self.previous)
        self.tmp.cleanup()

    def allowed(self, **overrides):
        args = dict(branch=self.branch, approved=self.work, production=self.work,
                    live=self.live, open_refs=set(), active_refs=set(), failed_refs=set())
        args.update(overrides)
        return m.eligible(**args)[0]

    def test_preserved_and_deployed_branch_is_eligible(self):
        self.assertTrue(self.allowed())

    def test_unique_work_is_never_deleted(self):
        self.assertFalse(self.allowed(approved=self.base))

    def test_quick_release_waits_for_production_gates(self):
        self.assertFalse(self.allowed(production=self.base))

    def test_one_stale_app_blocks_cleanup(self):
        self.assertFalse(self.allowed(live=self.live[:4] + [{'revision': self.base}]))

    def test_absent_live_evidence_is_not_success(self):
        self.assertFalse(self.allowed(live=[]))

    def test_failed_and_cancelled_branches_survive(self):
        self.assertFalse(self.allowed(failed_refs={'work'}))

    def test_active_branch_survives(self):
        self.assertFalse(self.allowed(active_refs={'work'}))

    def test_open_pr_source_or_base_survives(self):
        self.assertFalse(self.allowed(open_refs={'work'}))

    def test_release_and_protected_branches_survive(self):
        for name in m.KEEP:
            self.assertFalse(self.allowed(branch=self.branch | {'name': name}))
        self.assertFalse(self.allowed(branch=self.branch | {'protected': True}))

    def test_unknown_commit_is_failure_not_eligible(self):
        with self.assertRaises(RuntimeError):
            self.allowed(production='0' * 40)

    def test_atomic_delete_refuses_concurrent_new_work(self):
        remote = str(Path(self.tmp.name) / 'remote.git')
        m.git('init', '--bare', remote)
        m.git('remote', 'add', 'origin', remote)
        m.git('push', 'origin', 'work')
        Path('web').write_text('new correction')
        m.git('add', '.')
        m.git('commit', '-m', 'concurrent correction')
        current = m.git('rev-parse', 'HEAD').stdout.strip()
        m.git('push', 'origin', 'work')
        result = m.git('push', '--force-with-lease=refs/heads/work:' + self.work,
                       'origin', ':refs/heads/work', check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(current, m.git('ls-remote', 'origin', 'refs/heads/work').stdout)

    def test_web_deploy_cannot_certify_mobile_or_worker_release(self):
        Path('Legend-ios').mkdir()
        Path('Legend-ios/changed.swift').write_text('native change')
        m.git('add', '.')
        m.git('commit', '-m', 'native work')
        head = m.git('rev-parse', 'HEAD').stdout.strip()
        self.assertTrue(m.undeployed_artifact_changes(head, self.base))
        self.assertFalse(m.undeployed_artifact_changes(self.work, self.base))


class ReleaseTruth(unittest.TestCase):
    def setUp(self):
        self.api = type('API', (), {'repo': 'owner/repo', 'pages': lambda *args: []})()
        self.run = {'id': 1, 'path': '.github/workflows/' + m.DIRECT,
                    'status': 'completed', 'conclusion': 'success', 'head_branch': m.APPROVED,
                    'head_repository': {'full_name': 'owner/repo'}}

    def test_green_run_with_skipped_deployment_does_not_authorize(self):
        self.assertFalse(m.successful_release(self.api, self.run))

    def test_wrong_workflow_cannot_authorize(self):
        self.assertFalse(m.successful_release(self.api, self.run | {'path': 'diagnostic.yml'}))

    def test_failed_release_does_not_authorize(self):
        self.assertFalse(m.successful_release(self.api, self.run | {'conclusion': 'failure'}))

    def test_fork_run_does_not_authorize(self):
        self.assertFalse(m.successful_release(self.api, self.run | {'head_repository': {'full_name': 'fork/repo'}}))

    def test_approved_release_requires_real_successful_jobs(self):
        self.api.pages = lambda *args: [{'name': name, 'conclusion': 'success'} for name in ('discover-live', 'release')]
        self.assertFalse(m.successful_release(self.api, self.run))
        self.api.pages = lambda *args: [
            {'name': 'discover-live', 'conclusion': 'success'},
            {'name': 'release', 'conclusion': 'success', 'steps': [
                {'name': 'Direct deploy Website', 'conclusion': 'success'},
                {'name': 'Verify every deployed target and collect all failures', 'conclusion': 'success'}]}]
        self.assertTrue(m.successful_release(self.api, self.run))

    def test_rigorous_release_requires_every_existing_gate(self):
        self.api.pages = lambda *args: [{'name': name, 'conclusion': 'success'} for name in ('security', 'build', 'merge', 'migrate', 'deploy')]
        self.assertFalse(m.successful_release(self.api, self.run | {'path': '.github/workflows/' + m.RIGOROUS}))

    def test_draft_and_untrusted_prs_are_not_approved_updates(self):
        pr = {'state': 'open', 'draft': False, 'base': {'ref': m.APPROVED},
              'head': {'ref': 'work', 'repo': {'full_name': 'owner/repo'}}, 'author_association': 'OWNER'}
        self.assertTrue(m.ready(pr, 'owner/repo', m.APPROVED))
        self.assertFalse(m.ready(pr | {'draft': True}, 'owner/repo', m.APPROVED))
        self.assertFalse(m.ready(pr | {'author_association': 'NONE'}, 'owner/repo', m.APPROVED))


class OrchestrationSafety(unittest.TestCase):
    def test_failed_authoritative_production_receipt_stops_all_cleanup(self):
        api = type('API', (), {
            'pages': lambda self, path: [{'name': 'work', 'commit': {'sha': 'a' * 40}}],
            'ref': lambda self, name: 'b' * 40})()
        with patch.object(m, 'live_revisions', return_value=[{'revision': 'c' * 40, 'app': 'portal'}]), \
             patch.object(m, 'release_proven', return_value=False), \
             patch.object(m, 'git') as commands:
            result = m.cleanup(api, apply=True)
        self.assertIn('lacks a successful', result['retained'])
        commands.assert_not_called()

    def test_failed_live_receipt_stops_cleanup_even_when_ancestry_would_pass(self):
        api = type('API', (), {'pages': lambda self, path: [], 'ref': lambda self, name: 'b' * 40})()
        with patch.object(m, 'live_revisions', return_value=[{'revision': 'c' * 40, 'app': 'portal'}]), \
             patch.object(m, 'release_proven', side_effect=[True, False]), \
             patch.object(m, 'git') as commands:
            result = m.cleanup(api, apply=True)
        self.assertIn('Live app lacks', result['retained'])
        commands.assert_not_called()

    def test_newer_failed_attempt_invalidates_old_success(self):
        runs = [{'id': 1, 'path': '.github/workflows/' + m.DIRECT, 'created_at': '2026-01-01', 'status': 'completed', 'conclusion': 'success'},
                {'id': 2, 'path': '.github/workflows/' + m.DIRECT, 'created_at': '2026-01-02', 'status': 'completed', 'conclusion': 'failure'}]
        api = type('API', (), {'pages': lambda self, path, *args: [] if path.startswith('commits/') else runs})()
        with patch.object(m, 'successful_release', side_effect=lambda api, run: run['conclusion'] == 'success'):
            self.assertFalse(m.release_proven(api, 'a' * 40))

    def test_failed_release_event_never_synchronizes(self):
        from unittest.mock import Mock
        api = Mock()
        api.api.return_value = {'id': 1}
        with patch.object(m, 'successful_release', return_value=False):
            result = m.reconcile(api, 1)
        self.assertIn('No successful', result['retained'])
        api.ref.assert_not_called()
        api.dispatch.assert_not_called()

    def test_production_merge_back_dispatches_new_direct_release(self):
        from unittest.mock import Mock
        api = Mock()
        api.api.return_value = {'id': 1, 'path': '.github/workflows/' + m.RIGOROUS}
        api.ref.side_effect = ['a' * 40, 'b' * 40]
        with patch.object(m, 'successful_release', return_value=True), \
             patch.object(m, 'release_proven', return_value=True), \
             patch.object(m, 'ancestor', return_value=False), patch.object(m, 'git'):
            result = m.reconcile(api, 1)
        self.assertTrue(result['directReleaseDispatched'])
        api.dispatch.assert_called_once_with(m.DIRECT, {'automatic': 'true'})
        self.assertEqual(api.api.call_args.args[0], 'merges')

    def test_schedule_recovers_missed_successful_production_event(self):
        from unittest.mock import Mock
        api = Mock()
        api.ref.side_effect = ['a' * 40, 'b' * 40]
        with patch.object(m, 'ancestor', return_value=False), \
             patch.object(m, 'release_proven', return_value=True), patch.object(m, 'git'):
            with patch.object(m, 'direct_only_request', return_value=False):
                result = m.reconcile(api)
        self.assertTrue(result['directReleaseDispatched'])
        api.dispatch.assert_called_once_with(m.DIRECT, {'automatic': 'true'})
        self.assertEqual(api.api.call_args.args[0], 'merges')

    def test_schedule_never_synchronizes_failed_production(self):
        from unittest.mock import Mock
        api = Mock()
        api.ref.side_effect = ['a' * 40, 'b' * 40]
        with patch.object(m, 'ancestor', return_value=False), \
             patch.object(m, 'release_proven', return_value=False):
            with patch.object(m, 'direct_only_request', return_value=False):
                result = m.reconcile(api)
        self.assertIn('not bound to successful', result['retained'])
        api.api.assert_not_called()
        api.dispatch.assert_not_called()

    def test_website_receipt_cannot_certify_portal(self):
        from unittest.mock import Mock
        run = {'id': 1, 'path': '.github/workflows/' + m.WEBSITE, 'created_at': '2026-01-01',
               'status': 'completed', 'conclusion': 'success'}
        api = Mock()
        api.pages.side_effect = [[run], []]
        self.assertFalse(m.release_proven(api, 'a' * 40, app='portal'))

    def test_website_receipt_cannot_certify_unrelated_production_diff(self):
        from unittest.mock import Mock
        run = {'id': 1, 'path': '.github/workflows/' + m.WEBSITE, 'created_at': '2026-01-01',
               'status': 'completed', 'conclusion': 'success'}
        api = Mock()
        api.pages.side_effect = [[run], []]
        with patch.object(m, 'website_only_revision', return_value=False):
            self.assertFalse(m.release_proven(api, 'a' * 40, production=True))

    def test_website_green_cannot_erase_failed_rigorous_release(self):
        from unittest.mock import Mock
        website = {'id': 2, 'path': '.github/workflows/' + m.WEBSITE, 'created_at': '2026-01-02',
                   'status': 'completed', 'conclusion': 'success'}
        rigorous = website | {'id': 1, 'path': '.github/workflows/' + m.RIGOROUS,
                              'created_at': '2026-01-01', 'conclusion': 'failure'}
        api = Mock()
        api.pages.side_effect = [[website, rigorous], []]
        with patch.object(m, 'successful_release', side_effect=lambda api, run: run['conclusion'] == 'success'):
            self.assertFalse(m.release_proven(api, 'a' * 40, production=True))

    def test_failed_new_attempt_of_older_run_invalidates_green_newer_run(self):
        from unittest.mock import Mock
        old = {'id': 1, 'path': '.github/workflows/' + m.DIRECT, 'created_at': '2026-01-01',
               'updated_at': '2026-01-03', 'status': 'completed', 'conclusion': 'failure', 'run_attempt': 2}
        new = old | {'id': 2, 'created_at': '2026-01-02', 'updated_at': '2026-01-02',
                     'conclusion': 'success', 'run_attempt': 1}
        api = Mock()
        api.pages.side_effect = [[new, old], []]
        self.assertFalse(m.release_proven(api, 'a' * 40, app='portal'))

    def test_successful_portal_only_direct_release_cannot_certify_client(self):
        api = type('API', (), {'repo': 'owner/repo', 'pages': lambda *args: [
            {'name': 'discover-live', 'conclusion': 'success'},
            {'name': 'release', 'conclusion': 'success', 'steps': [
                {'name': 'Direct deploy AgentPortal', 'conclusion': 'success'},
                {'name': 'Direct deploy ClientApp', 'conclusion': 'skipped'},
                {'name': 'Verify every deployed target and collect all failures', 'conclusion': 'success'}]}]})()
        run = {'id': 1, 'path': '.github/workflows/' + m.DIRECT, 'status': 'completed',
               'conclusion': 'success', 'head_branch': m.APPROVED, 'head_repository': {'full_name': 'owner/repo'}}
        self.assertTrue(m.successful_release(api, run, app='portal'))
        self.assertFalse(m.successful_release(api, run, app='client'))

    def test_older_success_cannot_promote_newer_unreleased_approved_tip(self):
        from unittest.mock import Mock
        api = Mock()
        api.ref.side_effect = ['a' * 40, 'b' * 40]
        api.api.return_value = {'workflow_runs': []}
        api.pages.return_value = []
        with patch.object(m, 'ancestor', side_effect=[True, False]):
            with patch.object(m, 'direct_only_request', return_value=False):
                result = m.reconcile(api)
        self.assertIn('awaiting successful', result['promotion'])
        api.dispatch.assert_not_called()
        self.assertIn('head_sha=' + 'b' * 40, api.api.call_args.args[0])


class StagingSafety(unittest.TestCase):
    def test_hold_blocks_all_automatic_mutations(self):
        from unittest.mock import Mock
        for action in [lambda api: m.integrate(api, 1), m.pending_updates,
                       m.reconcile, lambda api: m.cleanup(api, True)]:
            api = Mock()
            with patch.object(m, 'staging_only', return_value=True):
                result = action(api)
            self.assertTrue(result)
            self.assertEqual(api.mock_calls, [])

    def test_hold_blocks_production_resolution(self):
        from unittest.mock import Mock
        api = Mock()
        with patch.object(m, 'staging_only', return_value=True):
            with self.assertRaises(RuntimeError):
                m.resolve_production(api, 1, 'a' * 40)
        self.assertEqual(api.mock_calls, [])


if __name__ == '__main__':
    unittest.main()
