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


    def test_release_control_only_changes_preserve_live_application_identity(self):
        rows = [{"revision": "a" * 40}]
        with patch.object(
            self.baseline.subprocess,
            "check_output",
            return_value=(
                ".github/workflows/all-intentional-direct-release-20260918.yml\n"
                "scripts/validation-resume.py\n"
                "Docs/releases/direct-release-request.json\n"
            ),
        ):
            self.assertEqual(
                "a" * 40,
                self.baseline.reusable_live_application_revision(rows, "b" * 40),
            )

    def test_runtime_change_invalidates_live_application_identity(self):
        rows = [{"revision": "a" * 40}]
        with patch.object(
            self.baseline.subprocess,
            "check_output",
            return_value="AgentPortal/Services/Engineering/LegendEngineeringOrchestrator.cs\n",
        ):
            self.assertIsNone(
                self.baseline.reusable_live_application_revision(rows, "b" * 40)
            )


    def test_package_identity_is_scope_and_contract_bound(self):
        targets = (
            ("portal", "portal.mylegnd.com", "AgentPortal/AgentPortal.csproj"),
        )
        with patch.object(self.baseline, "release_package_contract_hash", return_value="c" * 64):
            first = self.baseline.release_package_identity("a" * 40, targets, False, "")
            second = self.baseline.release_package_identity(
                "a" * 40,
                targets + (("client", "client.mylegnd.com", "ClientApp/ClientApp.csproj"),),
                False,
                "",
            )
            routing = self.baseline.release_package_identity(
                "a" * 40,
                targets,
                True,
                "camoexterior.com",
            )
        self.assertNotEqual(first, second)
        self.assertNotEqual(first, routing)

    def test_different_live_target_revisions_cannot_share_application_identity(self):
        rows = [{"revision": "a" * 40}, {"revision": "b" * 40}]
        self.assertIsNone(
            self.baseline.reusable_live_application_revision(rows, "c" * 40)
        )

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

        def fake_git(*args, **kwargs):
            if args[:4] == ('rev-list', '--parents', '-n', '1'):
                return SimpleNamespace(returncode=0, stdout=f'{head} {merged}\n')
            if args and args[0] == 'diff-tree':
                return SimpleNamespace(returncode=0, stdout='Docs/releases/direct-release-request.json\n')
            return SimpleNamespace(returncode=1, stdout='')

        class Api:
            def pages(self, path, key=None):
                self.path = path
                if path == 'commits/' + merged + '/pulls':
                    return [pr]
                if path == 'pulls/308/files':
                    return [{'filename': 'SHARED/wwwroot/css/dashboard-home-shared.css'}]
                return []

        api = Api()
        with patch.object(self.lifecycle, 'direct_only_request', side_effect=lambda sha: sha == head), \
             patch.object(self.lifecycle, 'git', side_effect=fake_git):
            resolved = self.lifecycle.direct_release_approved_pr(api, head)

        self.assertEqual(resolved['number'], 308)
        self.assertEqual(api.path, 'pulls/308/files')

    def test_release_control_merge_with_architecture_workflow_resolves_product_pr(self):
        head = 'a' * 40
        control_merge = 'b' * 40
        product_merge = 'c' * 40
        control_side = 'd' * 40
        product_side = 'e' * 40
        product_base = 'f' * 40
        control_pr = {
            'number': 319,
            'merged_at': '2026-09-30T07:40:42Z',
            'merge_commit_sha': control_merge,
            'base': {'ref': self.lifecycle.APPROVED},
        }
        product_pr = {
            'number': 318,
            'merged_at': '2026-09-30T07:34:36Z',
            'merge_commit_sha': product_merge,
            'base': {'ref': self.lifecycle.APPROVED},
        }

        class Api:
            def pages(self, path, key=None):
                if path == 'commits/' + control_merge + '/pulls':
                    return [control_pr]
                if path == 'pulls/319/files':
                    return [
                        {'filename': '.github/workflows/masterapp-platform-architecture-validation.yml'},
                        {'filename': 'scripts/release-lifecycle.py'},
                        {'filename': 'scripts/test-release-policy.py'},
                    ]
                if path == 'commits/' + product_merge + '/pulls':
                    return [product_pr]
                if path == 'pulls/318/files':
                    return [{'filename': 'AgentPortal/wwwroot/css/legend-app-shell.css'}]
                return []

        def fake_git(*args, **kwargs):
            if args[:4] == ('rev-list', '--parents', '-n', '1'):
                current = args[4]
                if current == head:
                    return SimpleNamespace(returncode=0, stdout=f'{head} {control_merge}\n')
                if current == control_merge:
                    return SimpleNamespace(returncode=0, stdout=f'{control_merge} {product_merge} {control_side}\n')
                if current == product_merge:
                    return SimpleNamespace(returncode=0, stdout=f'{product_merge} {product_base} {product_side}\n')
            if args and args[0] == 'diff-tree':
                return SimpleNamespace(returncode=0, stdout='Docs/releases/direct-release-request.json\n')
            return SimpleNamespace(returncode=1, stdout='')

        with patch.object(self.lifecycle, 'direct_only_request', side_effect=lambda sha: sha == head), \
             patch.object(self.lifecycle, 'git', side_effect=fake_git):
            resolved = self.lifecycle.direct_release_approved_pr(Api(), head)

        self.assertEqual(resolved['number'], 318)

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
                if path == 'commits/' + merged + '/pulls':
                    return [pr]
                if path == 'pulls/310/files':
                    return [{'filename': 'AgentPortal/wwwroot/css/clients-index.css'}]
                return []

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
        self.assertEqual(api.path, 'pulls/310/files')

    def test_architecture_product_validation_accepts_only_release_policy_failure(self):
        run = {'id': 77, 'status': 'completed', 'conclusion': 'failure'}
        steps = [
            {'name': name, 'conclusion': 'success'}
            for name in self.lifecycle.ARCHITECTURE_PRODUCT_STEPS
        ]
        steps.append({
            'name': 'Verify consolidated release scope and routing policy',
            'conclusion': 'failure',
        })

        class Api:
            def pages(self, path, key=None):
                self.path = path
                return [{'name': 'validate', 'steps': steps}]

        self.assertTrue(self.lifecycle.architecture_product_validation(Api(), run))

    def test_architecture_product_validation_rejects_failed_product_step(self):
        run = {'id': 78, 'status': 'completed', 'conclusion': 'failure'}
        steps = [
            {'name': name, 'conclusion': 'success'}
            for name in self.lifecycle.ARCHITECTURE_PRODUCT_STEPS
        ]
        steps[0]['conclusion'] = 'failure'
        steps.append({
            'name': 'Verify consolidated release scope and routing policy',
            'conclusion': 'failure',
        })

        class Api:
            def pages(self, path, key=None):
                return [{'name': 'validate', 'steps': steps}]

        self.assertFalse(self.lifecycle.architecture_product_validation(Api(), run))

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
                        {'filename': '.github/workflows/masterapp-platform-architecture-validation.yml'},
                        {'filename': 'scripts/release-lifecycle.py'},
                        {'filename': 'scripts/test-release-policy.py'},
                        {'filename': 'scripts/test-release-lifecycle.py'},
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

    def test_shared_resume_authority_requires_every_consuming_validation(self):
        head = "a" * 40
        pr = {
            "number": 400,
            "head": {"sha": head},
        }
        architecture = ".github/workflows/masterapp-platform-architecture-validation.yml"
        step5 = ".github/workflows/step5-isolated-conversion-mapping-validation.yml"
        step6 = ".github/workflows/step6-openai-ads-execution-validation.yml"
        step78 = ".github/workflows/steps7-8-governed-advertising-validation.yml"

        class Api:
            def pages(self, path, key=None):
                if path == "actions/runs?head_sha=" + head:
                    return [
                        {"head_sha": head, "event": "pull_request", "path": architecture,
                         "status": "completed", "conclusion": "success", "created_at": "4", "id": 4},
                        {"head_sha": head, "event": "pull_request", "path": step5,
                         "status": "completed", "conclusion": "success", "created_at": "3", "id": 3},
                        {"head_sha": head, "event": "pull_request", "path": step78,
                         "status": "completed", "conclusion": "success", "created_at": "2", "id": 2},
                    ]
                if path == "pulls/400/files":
                    return [{"filename": "scripts/validation-resume.py"}]
                if path == "pulls/400/commits":
                    return [{"sha": head}]
                raise AssertionError(path)

        pending = self.lifecycle.candidate_validation(Api(), pr)
        self.assertIn(step6, pending)

    def test_step5_workflow_change_requires_exact_step5_validation(self):
        head = "b" * 40
        pr = {"number": 401, "head": {"sha": head}}
        architecture = ".github/workflows/masterapp-platform-architecture-validation.yml"
        step5 = ".github/workflows/step5-isolated-conversion-mapping-validation.yml"

        class Api:
            def pages(self, path, key=None):
                if path == "actions/runs?head_sha=" + head:
                    return [
                        {"head_sha": head, "event": "pull_request", "path": architecture,
                         "status": "completed", "conclusion": "success", "created_at": "2", "id": 2},
                    ]
                if path == "pulls/401/files":
                    return [{"filename": step5}]
                if path == "pulls/401/commits":
                    return [{"sha": head}]
                raise AssertionError(path)

        pending = self.lifecycle.candidate_validation(Api(), pr)
        self.assertEqual("Exact-head full-suite comparison has not started", pending)

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

    def test_direct_release_reuses_only_exact_validated_package_evidence(self):
        workflow=(ROOT.parent / '.github/workflows/all-intentional-direct-release-20260918.yml').read_text()
        self.assertIn('Reuse exact successful validation package when available', workflow)
        self.assertIn('founder-diagnostics-packages-${PACKAGE_IDENTITY}', workflow)
        self.assertIn('REUSE_VALIDATED_PACKAGE=true', workflow)
        self.assertIn('sha256sum -c SHA256SUMS', workflow)
        self.assertIn('overwrite: true', workflow)
        self.assertIn('SourceRevisionId="$APPLICATION_RELEASE_SHA"', workflow)
        self.assertIn('verify-release-coverage', workflow)
        self.assertIn('Retain exact approved release receipt', workflow)
        self.assertIn('legend-approved-release-${{ env.APPLICATION_RELEASE_SHA }}', workflow)
        self.assertIn("'applicationReleaseSha':os.environ['APPLICATION_RELEASE_SHA']", workflow)

    def test_approved_security_validation_preserves_static_release_safety_gates(self):
        workflow=(ROOT.parent / '.github/workflows/approved-release-security-validation.yml').read_text()
        self.assertIn('scripts/validation-resume.py plan', workflow)
        self.assertIn('Validate database migration artifacts', workflow)
        self.assertIn('Reject skipped security tests', workflow)
        self.assertIn('Audit dependency vulnerabilities', workflow)
        self.assertIn('Scan committed configuration for secrets', workflow)
        self.assertIn('Verify shared composition authorities', workflow)
        self.assertIn('Reject inline Azure key-ring wiring', workflow)
        self.assertIn('cancel-in-progress: false', workflow)
        self.assertNotIn('webapps-deploy', workflow)
        self.assertNotIn('database update', workflow)
        self.assertNotIn('git/ref/heads/production', workflow)

    def test_old_second_release_workflows_are_removed(self):
        workflows=ROOT.parent / '.github/workflows'
        self.assertFalse((workflows / 'agentportal-production-deploy.yml').exists())
        self.assertFalse((workflows / 'legend-website-production-deploy.yml').exists())
        self.assertFalse((workflows / 'legend-canonical-branch-parity.yml').exists())

    def test_android_build_reuse_is_bound_to_source_config_and_certificate_identity(self):
        workflow=(ROOT.parent / '.github/workflows/legend-android-internal-testing.yml').read_text()
        self.assertIn('Resolve exact Android validation identity', workflow)
        self.assertIn('Reuse exact signed Android bundle when available', workflow)
        self.assertIn('legend-android-signed-$GITHUB_SHA-$config_hash', workflow)
        self.assertIn('REUSE_ANDROID_BUNDLE=true', workflow)
        self.assertIn('Validate upload keystore before building', workflow)

    def test_feature_validation_workflows_use_canonical_resume_authority(self):
        for name in (
            'masterapp-platform-architecture-validation.yml',
            'step6-openai-ads-execution-validation.yml',
            'steps7-8-governed-advertising-validation.yml',
            'approved-release-security-validation.yml',
        ):
            workflow=(ROOT.parent / '.github/workflows' / name).read_text()
            self.assertIn('scripts/validation-resume.py plan', workflow, name)
            self.assertIn('cancel-in-progress: false', workflow, name)
        step5=(ROOT.parent / '.github/workflows/step5-isolated-conversion-mapping-validation.yml').read_text()
        self.assertIn('scripts/validation-resume.py job-unchanged', step5)
        self.assertIn('cancel-in-progress: false', step5)
        self.assertIn('mode=reuse', step5)
        self.assertIn('Search backward for the newest complete evidence pair', step5)
        self.assertFalse((ROOT.parent / '.github/workflows/step5-approved-baseline-control.yml').exists())

    def test_release_orchestrator_contract_checks_are_receipt_reusable(self):
        lifecycle=(ROOT.parent / '.github/workflows/legend-release-lifecycle.yml').read_text()
        self.assertIn('REUSE_LIFECYCLE_CONTRACTS=true', lifecycle)
        self.assertIn('legend-lifecycle-contracts-', lifecycle)
        self.assertIn('scripts/validation-resume.py', lifecycle)
        self.assertIn('.github/workflows/step5-isolated-conversion-mapping-validation.yml', lifecycle)
        self.assertIn('.github/workflows/approved-release-security-validation.yml', lifecycle)
        self.assertIn('.github/workflows/all-intentional-direct-release-20260918.yml', lifecycle)
        self.assertIn('Recover authorized direct release when needed', lifecycle)
        self.assertNotIn('production gates', lifecycle.lower())


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
