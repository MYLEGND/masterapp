"""Release holds must persist, fail closed, and survive automatic dispatch."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace
from release_policy import read_request, staging_only

ROOT = Path(__file__).resolve().parent


class ReleaseScopeSelection(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location('baseline_scope', ROOT / 'approved-release-baseline.py')
        self.baseline = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.baseline)

    def test_centralized_commerce_scope_is_reviewed_and_supported(self):
        selected = self.baseline.selected_targets({
            'releaseMode': 'approved-only',
            'targets': ['masterapp-protect', 'masterapp-parfait', 'masterapp-website']
        })
        self.assertEqual(
            [row[0] for row in selected],
            ['protect', 'parfait', 'website'])

    def test_meta_seo_website_scope_is_reviewed_and_supported(self):
        selected = self.baseline.selected_targets({
            'releaseMode': 'approved-only',
            'targets': ['masterapp-portal', 'masterapp-protect', 'masterapp-website']
        })
        self.assertEqual(
            [row[0] for row in selected],
            ['portal', 'protect', 'website'])

    def test_canonical_ad_optimization_scope_is_reviewed_and_supported(self):
        selected = self.baseline.selected_targets({
            'releaseMode': 'approved-only',
            'targets': ['masterapp-portal', 'masterapp-protect', 'masterapp-parfait', 'masterapp-website']
        })
        self.assertEqual(
            [row[0] for row in selected],
            ['portal', 'protect', 'parfait', 'website'])

    def test_unreviewed_scope_still_fails_closed(self):
        with self.assertRaises(ValueError):
            self.baseline.selected_targets({
                'releaseMode': 'approved-only',
                'targets': ['masterapp-client', 'masterapp-parfait']
            })

    def test_automatic_dispatch_does_not_expand_explicit_scope(self):
        request={'releaseMode':'approved-only','targets':['masterapp-portal','masterapp-client','masterapp-protect']}
        with tempfile.TemporaryDirectory() as directory:
            output=Path(directory)/'outputs'
            with patch('sys.argv',['baseline','--automatic','--output',str(output)]), \
                 patch.object(self.baseline,'read_request',return_value=request), \
                 patch.object(self.baseline,'observe',side_effect=lambda target:dict(app=target[0],revision='a'*40)), \
                 patch.object(self.baseline.subprocess,'check_output',return_value='b'*40), \
                 patch.object(self.baseline.subprocess,'run'),patch.dict(os.environ,{'GITHUB_ACTIONS':'false'}):
                self.baseline.main()
            text=output.read_text()
            self.assertIn('masterapp-protect',text)
            self.assertNotIn('masterapp-parfait',text)
            self.assertNotIn('masterapp-website',text)

    def test_complete_inventory_can_include_cloudflare_routing_in_one_release(self):
        request = {
            'releaseMode': 'approved-only',
            'cloudflareWebsiteRouting': True,
            'websiteRoutingCanaryHost': 'camoexterior.com',
            'targets': ['masterapp-portal', 'masterapp-client', 'masterapp-protect',
                        'masterapp-parfait', 'masterapp-website'],
        }
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'release-outputs'
            with patch('sys.argv', ['baseline', '--output', str(output)]), \
                 patch.object(self.baseline, 'read_request', return_value=request), \
                 patch.object(self.baseline, 'observe', side_effect=lambda target: dict(app=target[0], revision='a' * 40)), \
                 patch.object(self.baseline.subprocess, 'check_output', return_value='b' * 40), \
                 patch.object(self.baseline.subprocess, 'run'), patch.dict(os.environ, {'GITHUB_ACTIONS': 'false'}):
                self.baseline.main()
            values = output.read_text()
            self.assertIn('website_routing=true', values)
            self.assertIn('"masterapp-website"', values)


class DirectReleaseAuthorizationResolution(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location('release_lifecycle', ROOT / 'release-lifecycle.py')
        self.lifecycle = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.lifecycle)

    def test_control_only_authorization_resolves_immediately_preceding_merged_pr(self):
        head = 'a' * 40
        merged = 'b' * 40
        pr = {
            'number': 308,
            'merged_at': '2026-09-30T06:01:03Z',
            'merge_commit_sha': merged,
            'base': {'ref': self.lifecycle.APPROVED},
        }

        class Api:
            def pages(self, path, key=None):
                self.path = path
                return [pr]

        api = Api()
        with patch.object(self.lifecycle, 'direct_only_request', return_value=True), \
             patch.object(self.lifecycle, 'git', return_value=SimpleNamespace(
                 returncode=0, stdout=f'{head} {merged}\n')):
            resolved = self.lifecycle.direct_release_approved_pr(api, head)

        self.assertEqual(resolved['number'], 308)
        self.assertEqual(api.path, 'commits/' + merged + '/pulls')

    def test_chained_control_only_authorizations_resolve_nearest_merged_pr(self):
        head = 'a' * 40
        prior = 'b' * 40
        merged = 'c' * 40
        pr = {
            'number': 310,
            'merged_at': '2026-09-30T06:20:00Z',
            'merge_commit_sha': merged,
            'base': {'ref': self.lifecycle.APPROVED},
        }

        class Api:
            def pages(self, path, key=None):
                self.path = path
                return [pr]

        def fake_git(*args, **kwargs):
            if args[:4] == ('rev-list', '--parents', '-n', '1'):
                current = args[4]
                if current == head:
                    return SimpleNamespace(returncode=0, stdout=f'{head} {prior}\n')
                if current == prior:
                    return SimpleNamespace(returncode=0, stdout=f'{prior} {merged}\n')
            if args and args[0] == 'diff-tree':
                return SimpleNamespace(returncode=0, stdout='Docs/releases/direct-release-request.json\n')
            return SimpleNamespace(returncode=1, stdout='')

        api = Api()
        with patch.object(self.lifecycle, 'direct_only_request',
                          side_effect=lambda sha: sha in {head, prior}), \
             patch.object(self.lifecycle, 'git', side_effect=fake_git):
            resolved = self.lifecycle.direct_release_approved_pr(api, head)

        self.assertEqual(resolved['number'], 310)
        self.assertEqual(api.path, 'commits/' + merged + '/pulls')

    def test_chained_release_resolution_rejects_intervening_product_commit(self):
        head = 'a' * 40
        parent = 'b' * 40

        class Api:
            def pages(self, path, key=None):
                raise AssertionError('product-bearing control ancestry must fail before GitHub lookup')

        def fake_git(*args, **kwargs):
            if args[:4] == ('rev-list', '--parents', '-n', '1'):
                return SimpleNamespace(returncode=0, stdout=f'{head} {parent}\n')
            if args and args[0] == 'diff-tree':
                return SimpleNamespace(returncode=0, stdout='Docs/releases/direct-release-request.json\nInfrastructure/WebsiteEditing/WebsiteSiteSource.cs\n')
            return SimpleNamespace(returncode=1, stdout='')

        with patch.object(self.lifecycle, 'direct_only_request', return_value=True), \
             patch.object(self.lifecycle, 'git', side_effect=fake_git):
            self.assertIsNone(self.lifecycle.direct_release_approved_pr(Api(), head))

    def test_release_control_pr_is_skipped_in_favor_of_nearest_product_pr(self):
        head = 'a' * 40
        control_request = 'b' * 40
        control_merge = 'c' * 40
        before_control = 'd' * 40
        product_merge = 'e' * 40
        product_pr = {
            'number': 302,
            'merged_at': '2026-09-30T06:14:36Z',
            'merge_commit_sha': product_merge,
            'base': {'ref': self.lifecycle.APPROVED},
        }
        control_pr = {
            'number': 311,
            'merged_at': '2026-09-30T06:36:22Z',
            'merge_commit_sha': control_merge,
            'base': {'ref': self.lifecycle.APPROVED},
        }

        class Api:
            def pages(self, path, key=None):
                if path == 'commits/' + control_merge + '/pulls':
                    return [control_pr]
                if path == 'pulls/311/files':
                    return [
                        {'filename': 'scripts/release-lifecycle.py'},
                        {'filename': 'scripts/test-release-policy.py'},
                    ]
                if path == 'commits/' + product_merge + '/pulls':
                    return [product_pr]
                if path == 'pulls/302/files':
                    return [{'filename': 'Infrastructure/WebsiteEditing/WebsiteSiteSource.cs'}]
                raise AssertionError(path)

        def fake_git(*args, **kwargs):
            if args[:4] == ('rev-list', '--parents', '-n', '1'):
                current = args[4]
                mapping = {
                    head: head + ' ' + control_request + '\n',
                    control_request: control_request + ' ' + control_merge + '\n',
                    control_merge: control_merge + ' ' + before_control + ' ' + ('9' * 40) + '\n',
                    before_control: before_control + ' ' + product_merge + '\n',
                    product_merge: product_merge + ' ' + ('8' * 40) + ' ' + ('7' * 40) + '\n',
                }
                return SimpleNamespace(returncode=0, stdout=mapping[current])
            if args and args[0] == 'diff-tree':
                return SimpleNamespace(returncode=0, stdout='Docs/releases/direct-release-request.json\n')
            return SimpleNamespace(returncode=1, stdout='')

        with patch.object(self.lifecycle, 'direct_only_request',
                          side_effect=lambda sha: sha in {head, control_request, before_control}), \
             patch.object(self.lifecycle, 'git', side_effect=fake_git):
            resolved = self.lifecycle.direct_release_approved_pr(Api(), head)

        self.assertEqual(resolved['number'], 302)
    def test_direct_release_authorization_rejects_extra_changed_files(self):
        path = 'Docs/releases/direct-release-request.json'
        with patch.object(self.lifecycle, 'git', return_value=SimpleNamespace(
                returncode=0, stdout=path + '\nAgentPortal/Program.cs\n')):
            self.assertFalse(self.lifecycle.direct_only_request('c' * 40))

    def test_direct_release_resolution_rejects_octopus_authorization_commit(self):
        head = 'd' * 40

        class Api:
            def pages(self, path, key=None):
                raise AssertionError('ambiguous authorization lineage must fail before GitHub lookup')

        with patch.object(self.lifecycle, 'direct_only_request', return_value=True), \
             patch.object(self.lifecycle, 'git', return_value=SimpleNamespace(
                 returncode=0, stdout=head + ' ' + ('e' * 40) + ' ' + ('f' * 40) + ' ' + ('9' * 40) + '\n')):
            self.assertIsNone(self.lifecycle.direct_release_approved_pr(Api(), head))


class ApprovedReleaseResumePolicy(unittest.TestCase):
    def test_exact_live_targets_are_preserved_across_retries(self):
        workflow=(ROOT.parent / '.github/workflows/all-intentional-direct-release-20260918.yml').read_text()
        self.assertIn('Preserve targets already live at exact candidate', workflow)
        self.assertIn("steps.resumestate.outputs.portal_live != 'true'", workflow)
        self.assertIn("steps.resumestate.outputs.client_live != 'true'", workflow)
        self.assertIn("steps.resumestate.outputs.protect_live != 'true'", workflow)
        self.assertIn("steps.resumestate.outputs.parfait_live != 'true'", workflow)
        self.assertIn("steps.resumestate.outputs.website_live != 'true'", workflow)
        self.assertIn("preservedExactLiveTargets", workflow)
        self.assertIn("was already live at the exact candidate but redeployed", workflow)


class ReleasePolicy(unittest.TestCase):
    def test_hold_survives_descendant_until_explicit_release(self):
        with tempfile.TemporaryDirectory() as directory:
            previous = Path.cwd()
            os.chdir(directory)
            try:
                def git(*args):
                    return subprocess.check_output(['git', *args], stderr=subprocess.DEVNULL, text=True).strip()
                git('init', '-b', 'approved')
                git('config', 'user.email', 'test@example.invalid')
                git('config', 'user.name', 'Test')
                path = Path('Docs/releases/direct-release-request.json')
                path.parent.mkdir(parents=True)
                path.write_text(json.dumps({'releaseMode': 'validate-only'}))
                git('add', '.')
                git('commit', '-m', 'hold')
                held = git('rev-parse', 'HEAD')
                Path('other').write_text('unrelated edit')
                git('add', '.')
                git('commit', '-m', 'descendant')
                self.assertTrue(staging_only('HEAD'))
                path.write_text(json.dumps({'releaseMode': 'approved-only'}))
                git('add', '.')
                git('commit', '-m', 'explicit release')
                self.assertFalse(staging_only('HEAD'))
                self.assertTrue(staging_only(held))
                with self.assertRaises(RuntimeError):
                    staging_only('missing-ref')
                path.write_text('{"releaseMode":"unknown"}')
                with self.assertRaises(ValueError):
                    read_request()
            finally:
                os.chdir(previous)

    def test_automatic_dispatch_cannot_discard_hold(self):
        spec = importlib.util.spec_from_file_location('baseline', ROOT / 'approved-release-baseline.py')
        baseline = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(baseline)
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'outputs'
            with patch('sys.argv', ['baseline', '--automatic', '--output', str(output)]), \
                 patch.object(baseline, 'read_request', return_value={'releaseMode': 'validate-only'}), \
                 patch.object(baseline, 'observe', side_effect=lambda target: dict(app=target[0], revision='a' * 40)), \
                 patch.object(baseline.subprocess, 'check_output', return_value='b' * 40), \
                 patch.object(baseline.subprocess, 'run'), patch.dict(os.environ, {'GITHUB_ACTIONS': 'false'}):
                baseline.main()
            self.assertIn('validate_only=true', output.read_text())


if __name__ == '__main__':
    unittest.main()
