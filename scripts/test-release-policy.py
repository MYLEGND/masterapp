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


class FounderCloudflareCommandExecution(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location(
            'founder_cloudflare_deploy',
            ROOT / 'deploy-founder-cloudflare.py',
        )
        self.deploy = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.deploy)

    def test_non_capture_run_executes_command(self):
        completed = SimpleNamespace(stdout='ignored')
        with patch.object(self.deploy.subprocess, 'run', return_value=completed) as run:
            result = self.deploy.run('echo', 'hello', capture=False)
        run.assert_called_once()
        self.assertIsNone(result)

    def test_capture_run_executes_command_and_returns_stdout(self):
        completed = SimpleNamespace(stdout='captured')
        with patch.object(self.deploy.subprocess, 'run', return_value=completed) as run:
            result = self.deploy.run('echo', 'hello', capture=True)
        run.assert_called_once()
        self.assertEqual('captured', result)


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

    def test_any_unique_inventory_subset_is_selected_without_a_parallel_scope_registry(self):
        names = [
            self.baseline._validation_authority.RELEASE_TARGETS[key]['releaseName']
            for key in list(self.baseline._validation_authority.RELEASE_TARGETS)[::2]
        ]
        selected = self.baseline.selected_targets({
            'releaseMode': 'approved-only',
            'targets': names,
        })
        self.assertEqual(
            names,
            [self.baseline._validation_authority.RELEASE_TARGETS[row[0]]['releaseName'] for row in selected],
        )

    def test_unknown_target_still_fails_closed(self):
        with self.assertRaises(ValueError):
            self.baseline.selected_targets({
                'releaseMode': 'approved-only',
                'targets': ['not-a-canonical-target'],
            })


    def test_target_scope_is_derived_from_canonical_source_roots_and_shared_changes_expand_fail_closed(self):
        authority = self.baseline._validation_authority
        first_key = next(iter(authority.RELEASE_TARGETS))
        first = authority.RELEASE_TARGETS[first_key]
        specific = authority.release_targets_for_paths([first['sourceRoot'] + '/example.txt'])
        self.assertEqual((first['releaseName'],), specific)
        shared = authority.release_targets_for_paths(['SHARED/example.txt'])
        self.assertEqual(
            tuple(row['releaseName'] for row in authority.RELEASE_TARGETS.values()),
            shared,
        )
        self.assertEqual(tuple(), authority.release_targets_for_paths(['scripts/test-release-policy.py']))
        self.assertEqual(tuple(), authority.release_targets_for_paths(['AGENTS.md']))
        self.assertTrue(authority.release_control_authority_path('AGENTS.md'))


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

    def test_approved_automatic_release_reuses_live_identity_after_validated_control_only_change(self):
        source=(ROOT / 'approved-release-baseline.py').read_text()
        approved_block=source.split(
            "elif release_mode == 'approved-only' and github_release_context:", 1
        )[1].split("elif release_mode == 'approved-only':", 1)[0]
        self.assertIn(
            'validated_revision = validated_application_revision(',
            approved_block,
        )
        self.assertIn(
            'reusable_live_application_revision(rows, validated_revision)',
            approved_block,
        )
        self.assertIn(
            'Preserving exact live application identity across validated control/test-only changes:',
            approved_block,
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


    def test_source_only_live_noop_cannot_bypass_artifact_transaction(self):
        source = (ROOT / 'approved-release-baseline.py').read_text()
        workflow = (ROOT.parent / '.github/workflows/all-intentional-direct-release-20260918.yml').read_text()
        self.assertFalse(hasattr(self.baseline, 'exact_live_release'))
        self.assertIn('exact_live = False', source)
        self.assertNotIn('outputs.exact_live !=', workflow)
        self.assertNotIn("mode='exact-live-noop'", workflow)
        self.assertIn('Prepare complete immutable release transaction', workflow)

    def test_matching_targets_cannot_build_or_transfer_rollback_packages(self):
        workflow = (ROOT.parent / '.github/workflows/all-intentional-direct-release-20260918.yml').read_text()
        block = workflow.split('  preserve-rollback:', 1)[1].split('  release:', 1)[0]
        steps = block.split('      - ')[1:]
        self.assertEqual(4, len(steps))
        self.assertIn('scripts/release-package.py restore-rollback', block)
        self.assertNotIn('dotnet publish', block)
        self.assertNotIn('npm ci', block)
        for step in steps:
            if 'Record rollback not required' in step:
                self.assertIn('matrix.revision == matrix.desiredProducerRevision', step)
            else:
                self.assertIn('matrix.revision != matrix.desiredProducerRevision',
                              step.split('        run:', 1)[0])
        download = workflow.split('      - name: Load preserved rollback packages for transactional publication', 1)[1].split('      - name:', 1)[0]
        self.assertIn("needs.discover-live.outputs.rollback_required == 'true'", download)
        prepare = workflow.split('      - name: Prepare complete immutable release transaction', 1)[1].split('      - name:', 1)[0]
        self.assertIn("needs.discover-live.outputs.rollback_required == 'false' && steps.rollbackpackages.outcome == 'skipped'", prepare)

    def test_package_identity_is_source_bound_not_release_scope_bound(self):
        targets = (
            ("portal", "portal.mylegnd.com", "AgentPortal/AgentPortal.csproj"),
        )
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
        other_revision = self.baseline.release_package_identity("b" * 40, targets, False, "")
        self.assertEqual(first, second)
        self.assertEqual(first, routing)
        self.assertNotEqual(first, other_revision)

    def test_validated_application_revision_rejects_post_validation_product_changes(self):
        with patch.object(self.baseline, "validate_revision", side_effect=lambda value: value), \
             patch.object(self.baseline.subprocess, "run"), \
             patch.object(self.baseline.subprocess, "check_output", return_value="Docs/releases/direct-release-request.json\n"):
            self.assertEqual(
                "a" * 40,
                self.baseline.validated_application_revision("a" * 40, "b" * 40),
            )
        with patch.object(self.baseline, "validate_revision", side_effect=lambda value: value), \
             patch.object(self.baseline.subprocess, "run"), \
             patch.object(self.baseline.subprocess, "check_output", return_value="AgentPortal/Program.cs\n"):
            with self.assertRaisesRegex(ValueError, "application changes after the validated PR head"):
                self.baseline.validated_application_revision("a" * 40, "b" * 40)

    def test_different_live_target_revisions_cannot_share_application_identity(self):
        rows = [{"revision": "a" * 40}, {"revision": "b" * 40}]
        self.assertIsNone(
            self.baseline.reusable_live_application_revision(rows, "c" * 40)
        )

    def test_automatic_dispatch_does_not_expand_explicit_scope(self):
        authority=self.baseline._validation_authority
        keys=list(authority.RELEASE_TARGETS)[:3]
        requested=[authority.RELEASE_TARGETS[key]['releaseName'] for key in keys]
        request={'releaseMode':'approved-only','targets':requested}
        with tempfile.TemporaryDirectory() as directory:
            output=Path(directory)/'outputs'
            with patch('sys.argv',['baseline','--automatic','--output',str(output)]), \
                 patch.object(self.baseline,'read_request',return_value=request), \
                 patch.object(self.baseline,'observe',side_effect=lambda target:dict(app=target[0],releaseName=authority.RELEASE_TARGETS[target[0]]['releaseName'],revision='a'*40)), \
                 patch.object(self.baseline.subprocess,'check_output',return_value='b'*40), \
                 patch.object(self.baseline.subprocess,'run'),patch.dict(os.environ,{'GITHUB_ACTIONS':'false'}):
                self.baseline.main()
            outputs=dict(line.split('=',1) for line in output.read_text().splitlines() if '=' in line)
            self.assertEqual(requested,json.loads(outputs['targets']))
            self.assertEqual(requested,[row['releaseName'] for row in json.loads(outputs['matrix'])['include']])

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
                 patch.object(self.baseline, 'observe', side_effect=lambda target: dict(app=target[0], releaseName=self.baseline._validation_authority.RELEASE_TARGETS[target[0]]['releaseName'], revision='a' * 40)), \
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

    def test_candidate_validation_never_reinterprets_a_failed_required_workflow(self):
        head = 'a' * 40
        workflow = next(iter(self.lifecycle.VALIDATION_AUTHORITY.required_validation_topology(
            ['scripts/validation-resume.py']
        )['required']))
        pr = {'number': 44, 'head': {'sha': head}}

        class Api:
            def pages(self, path, key=None):
                if path == 'actions/runs?head_sha=' + head:
                    return [{
                        'id': 77,
                        'head_sha': head,
                        'event': 'pull_request',
                        'created_at': '1',
                        'path': workflow,
                        'status': 'completed',
                        'conclusion': 'failure',
                    }]
                if path == 'pulls/44/files':
                    return [{'filename': 'scripts/validation-resume.py'}]
                return []

        pending = self.lifecycle.candidate_validation(Api(), pr)
        self.assertIn(workflow, pending)


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
        security = ".github/workflows/approved-release-security-validation.yml"
        self.assertEqual(
            "Awaiting successful exact-head validation: " + security + ", " + step5,
            pending,
        )

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
    def test_step5_recovery_preserves_evidence_and_rechecks_only_introduced_classes(self):
        workflow=(ROOT.parent / '.github/workflows/step5-isolated-conversion-mapping-validation.yml').read_text()
        self.assertNotIn('scripts/validation-resume.py plan \\\n', workflow)
        self.assertIn('Resolve one canonical Step 5 resume decision', workflow)
        self.assertIn('historical_evidence_unavailable_run_full_step5', workflow)
        self.assertIn('refusing to discard completed evidence and rerun the full suite', workflow)
        self.assertIn("needs.plan.outputs.baseline_run_required", workflow)
        self.assertIn('Recheck only newly introduced Step 5 failure classes', workflow)
        self.assertIn('Bounded same-run Step 5 recheck reconciled all introduced failures.', workflow)
        self.assertIn('Preserve bounded Step 5 recovery state', workflow)
        self.assertIn('Enforce final Step 5 outcome after bounded recovery', workflow)

    def test_exact_live_targets_are_preserved_by_one_transactional_authority(self):
        workflow=(ROOT.parent / '.github/workflows/all-intentional-direct-release-20260918.yml').read_text()
        base=workflow.split('      - name: Verify current live base before publication\n',1)[1].split('      - name:',1)[0]
        self.assertIn('for attempt in range(3):', base)
        self.assertIn('{408,429,500,502,503,504}', base)
        self.assertIn("Cache-Control':'no-cache", base)
        self.assertIn('Preserve targets already live at exact candidate', workflow)
        self.assertIn("actual not in {row['revision'], candidate}", workflow)
        self.assertIn('exact candidate already live; preserve and continue reconciliation', workflow)
        self.assertIn('Live revision drifted outside preserved baseline/candidate', workflow)
        self.assertIn('scripts/validation-resume.py live-state', workflow)
        self.assertIn('Reconcile complete immutable release transaction', workflow)
        self.assertIn('Run independent auxiliary release fanout', workflow)
        self.assertIn('Deploy and activate LEGEND Founder Cloudflare baseline', workflow)
        self.assertIn('scripts/release-auxiliary.py', workflow)
        self.assertNotIn('scripts/deploy-founder-cloudflare.py deploy', workflow)
        self.assertNotIn('npx wrangler deploy --config wrangler.website-routing.jsonc', workflow)
        auxiliary=(ROOT / 'release-auxiliary.py').read_text()
        self.assertIn('ThreadPoolExecutor', auxiliary)
        self.assertIn('isolated_cloudflare_workspace("founder")', auxiliary)
        self.assertIn('isolated_cloudflare_workspace("router")', auxiliary)
        self.assertEqual(1, auxiliary.count('"npm", "ci"'))
        self.assertNotIn('install_toolchain', auxiliary)
        self.assertIn('"scripts/deploy-founder-cloudflare.py"', auxiliary)
        self.assertIn('"scripts/release-router.py"', auxiliary)
        founder_owner=(ROOT / 'deploy-founder-cloudflare.py').read_text()
        self.assertIn('LEGEND_CLOUDFLARE_ROOT', founder_owner)
        self.assertEqual(1, founder_owner.count('run("npm", "ci"'))
        self.assertNotIn('Restore Founder Cloudflare baseline after downstream release failure', workflow)
        self.assertNotIn('scripts/deploy-founder-cloudflare.py rollback', workflow)
        self.assertIn('FOUNDER_CLOUDFLARE', workflow)
        self.assertIn('--targets-json "$SELECTED_TARGETS"', workflow)
        self.assertIn('--baselines-json "$LIVE_BASELINES"', workflow)
        self.assertNotIn('Restore complete application baseline after downstream release failure', workflow)
        self.assertNotIn('--rollback-only', workflow)
        self.assertIn('--prepare-only --transaction-plan', workflow)
        self.assertIn('--finalize-only --transaction-plan', workflow)
        self.assertNotIn('steps.resumestate.outputs.portal_live', workflow)
        self.assertNotIn('steps.resumestate.outputs.client_live', workflow)
        self.assertNotIn('steps.resumestate.outputs.protect_live', workflow)
        self.assertNotIn('steps.resumestate.outputs.parfait_live', workflow)
        self.assertNotIn('steps.resumestate.outputs.website_live', workflow)

    def test_founder_worker_subdomain_is_explicitly_activated_and_restored(self):
        deploy=(ROOT.parent / 'scripts/deploy-founder-cloudflare.py').read_text()
        self.assertIn('/workers/scripts/{worker}/subdomain', deploy)
        self.assertIn('set_worker_subdomain(account, worker, True)', deploy)
        self.assertIn('founder_worker_subdomain_not_enabled', deploy)
        self.assertIn('previousWorkerSubdomainEnabled', deploy)
        self.assertNotIn('restore_worker(account, worker, previous_version, previous_subdomain_enabled)', deploy)
        self.assertIn('journal.before_mutation', deploy)

    def test_release_execution_contains_no_duplicate_target_or_infrastructure_literals(self):
        workflow=(ROOT.parent / '.github/workflows/all-intentional-direct-release-20260918.yml').read_text()
        authority=(ROOT.parent / 'scripts/validation-resume.py')
        spec=importlib.util.spec_from_file_location('validation_authority_audit',authority)
        module=importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        generator_spec=importlib.util.spec_from_file_location('workflow_generator', ROOT.parent / 'scripts/release-workflow.py')
        generator=importlib.util.module_from_spec(generator_spec)
        generator_spec.loader.exec_module(generator)
        self.assertEqual(workflow, generator.render(workflow, module.RELEASE_TARGETS))
        before,generated=workflow.split(generator.START,1)
        _,after=generated.split(generator.END,1)
        hand_authored=before+after
        for row in module.RELEASE_TARGETS.values():
            self.assertNotIn(row['releaseName'], hand_authored)
            self.assertNotIn(row['azureHost'], workflow)
        self.assertNotIn(module.RELEASE_RESOURCE_GROUP, workflow)
        self.assertNotIn(module.MIGRATION_BUNDLE_NAME, workflow)
        self.assertNotIn(module.ROUTING_WORKER_NAME, workflow)
        self.assertNotIn(module.DOMAIN_REFRESH_PROJECT, workflow)
        self.assertNotIn("paths: ['Docs/releases/direct-release-request.json']", workflow)
        self.assertIn('targets_json:', workflow)
        self.assertIn('Reconcile complete immutable release transaction', workflow)


    def test_retired_lifecycle_exits_without_mutation(self):
        import yaml, subprocess
        workflow=yaml.safe_load((ROOT.parent / '.github/workflows/legend-release-lifecycle.yml').read_text())
        script=workflow['jobs']['retired']['steps'][0]['run']
        result=subprocess.run(['bash', '-c', script],capture_output=True,text=True)
        self.assertEqual(1,result.returncode)
        self.assertIn('LEGEND_RELEASE_LIFECYCLE_RETIRED',result.stdout)
        self.assertEqual({},workflow['permissions'])

    def test_single_authenticated_probe_extracts_at_verified_root(self):
        workflow=(ROOT.parent / '.github/workflows/all-intentional-direct-release-20260918.yml').read_text()
        download=workflow.split('      - name: Reuse immutable migration probe without rebuilding\n',1)[1].split('      - name:',1)[0]
        verify=workflow.split('      - name: Verify retained migration probe identity and digest\n',1)[1].split('      - uses:',1)[0]
        # ID-based download defaults to an artifact-name child directory even
        # for one ID. The verifier/observer consume the exact root below.
        self.assertIn('artifact-ids: ${{ steps.probe.outputs.artifactId }}', download)
        self.assertIn('run-id: ${{ steps.probe.outputs.runId }}', download)
        self.assertIn('merge-multiple: true', download)
        self.assertIn('path: ${{ runner.temp }}/migration-probe', download)
        self.assertIn('--directory "$RUNNER_TEMP/migration-probe"', verify)
        self.assertNotIn('pattern:', download)

    def test_direct_release_reuses_only_exact_validated_package_evidence(self):
        workflow=(ROOT.parent / '.github/workflows/all-intentional-direct-release-20260918.yml').read_text()
        self.assertIn('Reuse exact successful validation package when available', workflow)
        authority=(ROOT.parent / 'scripts/validation-resume.py').read_text()
        self.assertIn('f"founder-diagnostics-packages-{package_identity}"', authority)
        self.assertNotIn('founder-diagnostics-packages-${PACKAGE_IDENTITY}', workflow)
        self.assertIn('path: .legend-evidence-authority', workflow)
        evidence_checkout=workflow.split('      - name: Checkout current approved evidence authority\n',1)[1].split('      - name:',1)[0]
        self.assertIn('ref: ${{ github.sha }}', evidence_checkout)
        self.assertIn('persist-credentials: false', evidence_checkout)
        self.assertIn('python3 .legend-evidence-authority/scripts/validation-resume.py validated-package', workflow)
        self.assertIn('artifact-ids: ${{ steps.reusevalidated.outputs.artifact_id }}', workflow)
        self.assertIn('--source-evidence "$RUNNER_TEMP/validated-package-evidence.json"', workflow)
        pending_sql=workflow.split('  migration-readiness:',1)[1].split('  preserve-rollback:',1)[0]
        self.assertIn('--pinned-source-json "$PINNED_PACKAGE"', pending_sql)
        self.assertIn('artifact-ids: ${{ steps.package.outputs.artifactId }}', pending_sql)
        self.assertIn('--source-evidence "$RUNNER_TEMP/migration-package-evidence.json"', pending_sql)
        self.assertIn('RELEASE_CANDIDATE_SHA: ${{ needs.discover-live.outputs.readiness_candidate }}', pending_sql)

        self.assertIn('rollback-evidence', workflow)
        self.assertIn('scripts/validation-resume.py live-state', workflow)
        self.assertIn('scripts/validation-resume.py verify-live', workflow)
        self.assertIn('REUSE_VALIDATED_PACKAGE=true', workflow)
        self.assertIn('release refuses to rebuild production bytes', workflow)
        self.assertIn('scripts/release-package.py verify', workflow)
        self.assertIn('verify-release-coverage', workflow)
        self.assertIn('Capture exact release step-state receipt', workflow)
        self.assertIn('Preserve exact release step-state receipt artifact', workflow)
        self.assertIn("'steps':steps", workflow)
        self.assertNotIn("STEP_STATE_JSON", workflow)
        self.assertIn('Retain exact approved release receipt', workflow)
        self.assertIn('legend-approved-release-${{ env.APPLICATION_RELEASE_SHA }}', workflow)
        self.assertNotIn('Retain exact deployable candidate packages', workflow)
        self.assertIn('Bind successful release to canonical validated package', workflow)
        self.assertIn('Preserve validated package release binding', workflow)
        self.assertIn('legend-approved-package-link-${{ needs.discover-live.outputs.application_release_sha }}-${{ needs.discover-live.outputs.package_identity }}', workflow)
        self.assertIn("'applicationReleaseSha':os.environ['APPLICATION_RELEASE_SHA']", workflow)
        self.assertIn('Reuse exact retained live package when available', workflow)
        self.assertIn('Checkout canonical rollback package authority', workflow)
        self.assertIn('scripts/release-package.py restore-rollback', workflow)
        self.assertNotIn('dotnet publish', workflow)
        self.assertNotIn('npm ci --prefix Legend-Website', workflow)
        self.assertNotIn('EXACT_LIVE', workflow)
        self.assertNotIn("mode='exact-live-noop'", workflow)
        self.assertIn('retention-days: 30', workflow)
        architecture=(ROOT.parent / '.github/workflows/masterapp-platform-architecture-validation.yml').read_text()
        package_block=architecture.split('      - name: Preserve immutable validated release package\n',1)[1].split('      - name:',1)[0]
        self.assertIn('retention-days: 90', package_block)

        def step(name):
            return workflow.split('      - name: ' + name + '\n', 1)[1].split('      - name:', 1)[0]

        self.assertNotIn('dotnet build', step('Build exact selected release candidate'))
        self.assertNotIn('dotnet publish', step('Publish exact selected application packages'))
        self.assertNotIn('npm ', step('Publish exact selected application packages'))
        prepublication = step('Synchronize canonical pre-publication resource lanes')
        self.assertIn('scripts/release-prepublication.py', prepublication)
        self.assertEqual(1, workflow.count('Synchronize canonical pre-publication resource lanes'))
        for stale in (
            'Synchronize selected shared authorization and publisher runtimes',
            'Synchronize selected editor ticket authority',
            'Prepare canonical business website routing authority',
            'Apply additive diagnostics migrations before restarting apps',
        ):
            self.assertNotIn(stale, workflow)

        prepublication_owner = (ROOT / 'release-prepublication.py').read_text()
        self.assertIn('ThreadPoolExecutor', prepublication_owner)
        self.assertIn('configure_all_targets', prepublication_owner)
        self.assertIn('run_migration_lane', prepublication_owner)
        self.assertIn('pool.submit(configure_all_targets)', prepublication_owner)
        self.assertIn('pool.submit(run_migration_lane)', prepublication_owner)
        self.assertIn('child_receipt(child, app, partition, prepare=True)', prepublication_owner)
        self.assertIn('"az", "webapp", "config", "appsettings", "set"', prepublication_owner)
        self.assertIn('scripts/release-package.py', prepublication_owner)
        self.assertIn('scripts/migration-probe-package.py', prepublication_owner)
        self.assertIn('scripts/release-migration.py', prepublication_owner)
        self.assertIn('release_proven', prepublication_owner)
        self.assertNotIn("dotnet-ef database update", prepublication_owner)

        migration_owner = (ROOT / 'release-migration.py').read_text()
        self.assertIn("RELEASE_RESOURCE_GROUP", migration_owner)
        self.assertIn("'SQLCONNSTR_MasterAppDb': connection", migration_owner)
        self.assertIn('capture_output=True', migration_owner)
        self.assertNotIn("print(connection)", migration_owner)

    def test_retired_parallel_package_definitions_preserve_historical_contracts(self):
        workflow=(ROOT.parent / '.github/workflows/masterapp-platform-architecture-validation.yml').read_text()
        self.assertIn('validated-release-package-plan:', workflow)
        self.assertIn('validated-release-package-components:', workflow)
        self.assertIn('validated-release-package:', workflow)
        self.assertIn('scripts/validation-resume.py package-canary-plan', workflow)
        package_plan = workflow.split('      - name: Resolve whether application bytes changed\n', 1)[1].split('      - name:', 1)[0]
        self.assertNotIn("git diff --name-only", package_plan)
        self.assertNotIn("release_control_only_path", package_plan)
        self.assertNotIn('needs: validate\n    if: github.event_name', workflow)
        self.assertIn('component: ${{ fromJSON(needs.validated-release-package-plan.outputs.component_matrix) }}', workflow)
        self.assertEqual(5, workflow.count('if: ${{ false }} # Retired approved-branch producer.'))
        self.assertIn('scripts/validation-resume.py component-sources', workflow)
        self.assertIn('artifact-ids: ${{ steps.componentsources.outputs.portal_artifact_id }}', workflow)
        self.assertIn('Record immutable component source', workflow)
        self.assertIn('scripts/release-package.py build-component', workflow)
        self.assertIn('scripts/release-package.py assemble', workflow)
        self.assertIn('merge-multiple: true', workflow)
        self.assertIn('scripts/release-package.py verify', workflow)
        self.assertIn('Preserve immutable validated release package', workflow)
        self.assertIn('retention-days: 90', workflow)
        self.assertIn('Run release web contract regressions', workflow)
        component_block = workflow.split('  validated-release-package-components:\n', 1)[1].split('  validated-release-package:\n', 1)[0]
        self.assertIn('if: ${{ false }} # Retired approved-branch producer.', component_block)
        self.assertNotIn('scripts/release-package.py build \\', component_block)
        final_block = workflow.split('  validated-release-package:\n', 1)[1].split('  wake-release-lifecycle-after-package-backfill:\n', 1)[0]
        self.assertNotIn('dotnet publish', final_block)
        self.assertNotIn('npm ci', final_block)

    def test_approved_security_validation_preserves_static_release_safety_gates(self):
        workflow=(ROOT.parent / '.github/workflows/approved-release-security-validation.yml').read_text()
        self.assertIn('scripts/validation-resume.py plan', workflow)
        self.assertIn('name: approved-release-security', workflow)
        self.assertIn('Run independent security validation fanout', workflow)
        self.assertIn('Validate database migration artifacts', workflow)
        self.assertIn('Reject skipped security tests', workflow)
        self.assertIn('Audit dependency vulnerabilities', workflow)
        self.assertIn('Scan committed configuration for secrets', workflow)
        self.assertIn('Verify shared composition authorities', workflow)
        self.assertIn('Reject inline Azure key-ring wiring', workflow)
        self.assertIn('run_simple no-skips no_skips', workflow)
        self.assertIn('run_simple secret-scan secret_scan', workflow)
        self.assertIn('run_simple composition composition', workflow)
        self.assertIn('run_simple keyring keyring', workflow)
        self.assertIn('run_simple diff-check diff_check', workflow)
        self.assertIn('VALIDATION_STEP_CONTEXT: ${{ toJSON(steps) }}', workflow)
        self.assertNotIn("steps.plan.outcome == 'success'", workflow.split('  security_database:\n',1)[1])
        self.assertIn('cancel-in-progress: true', workflow)
        self.assertNotIn('webapps-deploy', workflow)
        self.assertNotIn('database update', workflow)
        self.assertNotIn('git/ref/heads/production', workflow)

    def test_architecture_independent_gates_fan_out_without_duplicate_execution(self):
        workflow=(ROOT.parent / '.github/workflows/masterapp-platform-architecture-validation.yml').read_text()
        self.assertIn('Run independent architecture gate fanout', workflow)
        fanout=workflow.split('      - name: Run independent architecture gate fanout\n',1)[1].split(
            '      - name: Run Founder diagnostics and safe GPT Codex regressions\n',1
        )[0]
        for lane in (
            'founder-diagnostics-regressions',
            'domain-release',
            'website-regressions',
            'meta-regressions',
            'booking-regressions',
            'crm-regressions',
            'form-tracking',
            'release-web-contracts',
            'release-policy',
        ):
            self.assertIn('run_lane '+lane, fanout)
        self.assertNotIn("steps.gate_compile_regression.outcome == 'success'", fanout)
        self.assertIn('COMPILED_MATERIAL_READY: ${{ steps.gate_compile_regression.outputs.material_ready }}', fanout)
        self.assertIn('[ "$needs_compile" = true ] && [ "$COMPILED_MATERIAL_READY" != true ]', fanout)
        self.assertIn('/tmp/masterapp/bin/AgentPortal.Tests/Release', workflow)
        self.assertIn('/tmp/masterapp/obj/AgentPortal.Tests', workflow)
        self.assertNotIn('AgentPortal.Tests/bin/Release/net10.0/AgentPortal.Tests.dll', workflow)
        compile_block=workflow.split('      - name: Compile full regression test project\n',1)[1].split(
            '      - name: Preserve exact compiled regression material\n',1
        )[0]
        self.assertIn("if: always() && steps.plan.outcome == 'success' && steps.plan.outputs.regression_material == 'true'", compile_block)
        self.assertIn('echo "material_ready=true" >> "$GITHUB_OUTPUT"', compile_block)
        release_web=fanout.split('release_web_contracts() {',1)[1].split('release_policy() {',1)[0]
        self.assertNotIn('dotnet build scripts/DomainReleaseRefresh/DomainReleaseRefresh.csproj', release_web)

    def test_steps78_parallel_lanes_join_fail_closed_under_original_check_name(self):
        workflow=(ROOT.parent / '.github/workflows/steps7-8-governed-advertising-validation.yml').read_text()
        self.assertIn('advertising_dotnet:', workflow)
        self.assertIn('advertising_ui:', workflow)
        self.assertIn('advertising_evidence:', workflow)
        self.assertIn('name: advertising-validation', workflow)
        evidence=workflow.split('  advertising_evidence:\n',1)[1]
        self.assertIn('needs.advertising_dotnet.result', evidence)
        self.assertIn('needs.advertising_ui.result', evidence)
        self.assertIn('Require all advertising validation lanes', evidence)
        self.assertIn('Record canonical child execution evidence', evidence)
        self.assertLess(
            evidence.index('Record canonical child execution evidence'),
            evidence.index('Require all advertising validation lanes'),
        )

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
        names = (
            'masterapp-platform-architecture-validation.yml',
            'step6-openai-ads-execution-validation.yml',
            'steps7-8-governed-advertising-validation.yml',
            'approved-release-security-validation.yml',
        )
        for name in names:
            workflow=(ROOT.parent / '.github/workflows' / name).read_text()
            self.assertIn('scripts/validation-resume.py plan', workflow, name)
            self.assertIn('Restore PR-local validation gate evidence', workflow, name)
            self.assertIn('--resume-cache "/tmp/validation-resume-cache/validation-resume.json"', workflow, name)
            self.assertIn('--cache-output /tmp/validation-resume-cache/validation-resume.json', workflow, name)
            self.assertIn('Save PR-local validation gate evidence', workflow, name)
            self.assertNotIn('checkpoint-gate --workflow', workflow, name)
            self.assertIn('cancel-in-progress: true', workflow, name)

        for name in (
            'masterapp-platform-architecture-validation.yml',
            'step6-openai-ads-execution-validation.yml',
            'approved-release-security-validation.yml',
        ):
            workflow=(ROOT.parent / '.github/workflows' / name).read_text()
            self.assertIn('VALIDATION_STEP_CONTEXT: ${{ toJSON(steps) }}', workflow, name)

        step78=(ROOT.parent / '.github/workflows/steps7-8-governed-advertising-validation.yml').read_text()
        self.assertNotIn('VALIDATION_STEP_CONTEXT: ${{ toJSON(steps) }}', step78)
        self.assertIn('GITHUB_TOKEN: ${{ github.token }}', step78)
        self.assertIn('advertising_evidence:', step78)
        step5=(ROOT.parent / '.github/workflows/step5-isolated-conversion-mapping-validation.yml').read_text()
        self.assertNotIn('scripts/validation-resume.py plan \\\n', step5)
        self.assertEqual(1, step5.count('scripts/validation-resume.py step5-decision'))
        self.assertIn('--resume-cache /tmp/step5-resume-cache', step5)
        self.assertNotIn('PYCACHE', step5)
        self.assertNotIn('PYGRAPH', step5)
        self.assertNotIn('graphql_parent_success', step5)
        self.assertIn('Resolve one canonical Step 5 resume decision', step5)
        self.assertIn('scripts/validation-resume.py step5-baseline', step5)
        self.assertIn('candidate_restore_run', step5)
        self.assertIn('candidate_build_run', step5)
        self.assertIn('candidate_focused_run', step5)
        self.assertIn('candidate_full_run', step5)
        self.assertIn('comparison_run', step5)
        self.assertIn('cancel-in-progress: true', step5)
        self.assertIn('step5-tree-candidate-', step5)
        self.assertIn('step5-tree-baseline-', step5)
        self.assertNotIn('candidate_baseline_jobs_unchanged', step5)
        self.assertNotIn('gh api "/repos/$GITHUB_REPOSITORY/actions/artifacts?name=$baseline_name', step5)
        self.assertFalse((ROOT.parent / '.github/workflows/step5-approved-baseline-control.yml').exists())

    def test_release_lifecycle_has_no_parallel_validation_path_registry(self):
        lifecycle_script=(ROOT / 'release-lifecycle.py').read_text()
        self.assertIn('VALIDATION_AUTHORITY.required_validation_topology(names)', lifecycle_script)
        self.assertIn('def candidate_validation(api, pr):', lifecycle_script)
        self.assertIn("run.get('status') != 'completed'", lifecycle_script)
        self.assertIn("run.get('conclusion') != 'success'", lifecycle_script)
        self.assertIn('for attempt in range(4)', lifecycle_script)
        self.assertNotIn('validation_neutral_path', lifecycle_script)
        self.assertNotIn('architecture_product_validation', lifecycle_script)
        self.assertNotIn('architecture_public_website_validation', lifecycle_script)
        self.assertNotIn('STEP6_VALIDATION_PATHS', lifecycle_script)
        self.assertNotIn('STEP78_VALIDATION_PATHS', lifecycle_script)
        self.assertNotIn('VALIDATION_NEUTRAL_PATHS =', lifecycle_script)

    def test_direct_release_has_one_canonical_bounded_receipt_finalizer(self):
        workflow=(ROOT.parent / '.github/workflows/all-intentional-direct-release-20260918.yml').read_text()
        deploy=(ROOT / 'deploy-approved-app.py').read_text()
        transaction=workflow.split(
            '      - name: Reconcile complete immutable release transaction\n',1
        )[1].split('      - name:',1)[0]
        self.assertIn('--finalize-only --transaction-plan /tmp/release-transaction.json', transaction)
        self.assertNotIn('--reconcile-timeout-seconds', workflow)
        self.assertNotIn('Recover exact-live transaction receipts without publication replay', workflow)
        self.assertNotIn('transactionrecovery', workflow)
        self.assertNotIn('TRANSACTION_RECOVERY', workflow)
        self.assertIn('FINALIZE_RECONCILE_TIMEOUT_SECONDS = 90', deploy)
        self.assertIn('FINALIZE_RECONCILE_ATTEMPTS = 3', deploy)
        self.assertIn('FINALIZE_RETRY_DELAY_SECONDS = 10', deploy)
        self.assertIn('for attempt in range(1, FINALIZE_RECONCILE_ATTEMPTS + 1):', deploy)
        self.assertIn('max_status_failures=1', deploy)
        self.assertNotIn('--reconcile-timeout-seconds', deploy)

    def test_release_has_bounded_parallel_publication_and_no_alternate_entrypoints(self):
        root=ROOT.parent
        workflow=(root / '.github/workflows/all-intentional-direct-release-20260918.yml').read_text()
        deploy=(ROOT / 'deploy-approved-app.py').read_text()
        diagnostics=(root / '.github/workflows/deployment-diagnostics.yml').read_text()
        docs=(root / 'DEPLOYMENT.md').read_text()

        self.assertIn('PUBLICATION_RECONCILE_TIMEOUT_SECONDS = 420', deploy)
        self.assertIn('timeout=PUBLICATION_RECONCILE_TIMEOUT_SECONDS', deploy)
        self.assertIn('publish_prepared_targets_parallel', deploy)
        self.assertIn('ThreadPoolExecutor', deploy)
        release=workflow.split('\n  release:\n',1)[1].split('\n  release-state-receipt:',1)[0]
        self.assertIn('timeout-minutes: 30', release)
        self.assertIn('--publish-prepared-parallel', release)
        self.assertEqual(1, workflow.count('--finalize-only --transaction-plan /tmp/release-transaction.json'))

        self.assertFalse((root / 'deploy-portal.sh').exists())
        self.assertFalse((root / '.claude/settings.local.json').exists())
        self.assertFalse((root / 'AgentPortal/deploy-live-zipdeploy.json').exists())

        for token in (
            'list-publishing-credentials', '/api/vfs/', '/api/command',
            'webapp restart', 'webapp deploy', 'appsettings set',
            'connection-string set', 'zipdeploy',
        ):
            self.assertNotIn(token, diagnostics)
        self.assertIn('sole production deployment authority', docs)
        self.assertIn('legend/approved-changes', docs)

    def test_terminal_children_wake_only_the_protected_lifecycle(self):
        architecture=(ROOT.parent / '.github/workflows/masterapp-platform-architecture-validation.yml').read_text()
        direct=(ROOT.parent / '.github/workflows/all-intentional-direct-release-20260918.yml').read_text()
        lifecycle=(ROOT.parent / '.github/workflows/legend-release-lifecycle.yml').read_text()
        wake=(ROOT / 'wake-release-lifecycle.py').read_text()

        self.assertIn('wake-release-lifecycle-after-package-backfill:', architecture)
        package_wake=architecture.split(
            '  wake-release-lifecycle-after-package-backfill:\n',1
        )[1].split('\n  validated-migration-probe:',1)[0]
        self.assertIn('if: ${{ false && !inputs.preparation_only }}', package_wake)
        self.assertIn('permissions: {}', package_wake)
        for forbidden in ('actions: write', 'checkout@', 'scripts/wake-release-lifecycle.py', 'legend/approved-changes'):
            self.assertNotIn(forbidden, package_wake)

        self.assertIn('wake-release-lifecycle-after-terminal-release:', direct)
        release_wake=direct.split(
            '  wake-release-lifecycle-after-terminal-release:\n',1
        )[1]
        self.assertIn('needs: [target-release-receipts, release-state-receipt]', release_wake)
        self.assertIn('if: always()', release_wake)
        self.assertIn('actions: write', release_wake)
        self.assertIn('contents: read', release_wake)
        self.assertIn('scripts/wake-release-lifecycle.py', release_wake)
        self.assertNotIn('deploy-approved-app.py', release_wake)

        self.assertNotIn('release_run:', lifecycle)
        self.assertNotIn('workflow_run:', lifecycle)
        self.assertIn('actions/workflows/{LIFECYCLE}/dispatches', wake)
        self.assertIn('"inputs": {"release_run": RUN_ID}', wake)
        self.assertIn('APPROVED = "legend/approved-changes"', wake)
        self.assertIn('LIFECYCLE = "legend-release-lifecycle.yml"', wake)
        self.assertNotIn('deploy-approved-app', wake)
        self.assertNotIn('release-package.py', wake)

    def test_release_mutation_authorities_remain_serialized(self):
        for name in (
            'all-intentional-direct-release-20260918.yml',
        ):
            workflow=(ROOT.parent / '.github/workflows' / name).read_text()
            self.assertIn('cancel-in-progress: false', workflow, name)

    def test_retirement_does_not_reenable_orchestrator_receipt_reuse(self):
        lifecycle=(ROOT.parent / '.github/workflows/legend-release-lifecycle.yml').read_text()
        for forbidden in ('REUSE_LIFECYCLE_CONTRACTS=', 'Recover authorized direct release', 'scripts/validation-resume.py lifecycle-evidence'):
            self.assertNotIn(forbidden,lifecycle)


class RetiredBranchConsumerTests(unittest.TestCase):
    def workflow(self):
        import yaml
        return yaml.safe_load((ROOT.parent / '.github/workflows/masterapp-platform-architecture-validation.yml').read_text())

    def allowed(self, expression, event, base, preparation):
        from types import SimpleNamespace
        expression=expression.strip().removeprefix('${{').removesuffix('}}').strip()
        expression=expression.replace('&&',' and ').replace('||',' or ').replace('!',' not ')
        return bool(eval(expression, {'__builtins__': {}}, {
            'github':SimpleNamespace(event_name=event,base_ref=base),
            'inputs':SimpleNamespace(preparation_only=preparation), 'false':False,'true':True}))

    def test_event_matrix_never_reactivates_retired_producers(self):
        workflow=self.workflow();jobs=workflow['jobs']
        self.assertEqual({'pull_request','workflow_dispatch'},set(workflow[True]))
        self.assertEqual(['Legend/fast-track-release'],workflow[True]['pull_request']['branches'])
        retired=('validated-release-package-plan','validated-release-package-components',
                 'validated-release-package','migration-rehearsal-prepare','migration-rehearsal',
                 'wake-release-lifecycle-after-package-backfill')
        for event in ('pull_request','pull_request_target','push','workflow_run','workflow_dispatch','schedule'):
            for base in ('Legend/fast-track-release','legend/approved-changes','attacker'):
                for prep in (False,True):
                    with self.subTest(event=event,base=base,preparation=prep):
                        for name in retired:
                            self.assertFalse(self.allowed(jobs[name]['if'],event,base,prep),name)
                        for name in ('approved-head-preflight','validated-migration-probe'):
                            self.assertEqual(event=='pull_request' and base=='Legend/fast-track-release' and not prep,
                                             self.allowed(jobs[name]['if'],event,base,prep))
                        self.assertEqual(event=='workflow_dispatch' and prep,
                            self.allowed(jobs['nonpublishing-protect-parfait-preparation']['if'],event,base,prep))

    def test_architecture_wake_cannot_request_write_token_or_checkout(self):
        job=self.workflow()['jobs']['wake-release-lifecycle-after-package-backfill']
        self.assertEqual({},job['permissions'])
        self.assertEqual([{'run':'exit 1'}],job['steps'])
        self.assertNotIn('environment',job)

    def test_renderer_failure_remains_failure_without_baseline_network_access(self):
        import tempfile,subprocess,os
        step=next(x for x in self.workflow()['jobs']['validate']['steps'] if x.get('id')=='gate_renderer_tests')
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);(root/'Legend-Website').mkdir();(root/'bin').mkdir()
            for name,body in {'python3':'exit 1','npm':'exit 23','git':'touch "'+str(root/'git-called')+'"; exit 0'}.items():
                p=root/'bin'/name;p.write_text('#!/bin/sh\n'+body+'\n');p.chmod(0o700)
            result=subprocess.run(['bash','-e','-c',step['run']],cwd=root,
                                  env=dict(os.environ,PATH=str(root/'bin')+':'+os.environ['PATH']))
            self.assertEqual(23,result.returncode)
            self.assertFalse((root/'git-called').exists())

    def test_lifecycle_validation_retains_synthetic_history_without_live_queries(self):
        step=next(x for x in self.workflow()['jobs']['validate']['steps'] if x.get('id')=='gate_lifecycle')
        self.assertIn('python3 scripts/test-release-migration-history-audit.py',step['run'])
        self.assertIn('python3 scripts/test-release-retired-original-evidence.py',step['run'])
        self.assertNotIn('authority.api_get',step['run'])
        self.assertNotIn('python3 scripts/release-migration-history-audit.py',step['run'])


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
                 patch.object(baseline, 'observe', side_effect=lambda target: dict(app=target[0], releaseName=baseline._validation_authority.RELEASE_TARGETS[target[0]]['releaseName'], revision='a' * 40)), \
                 patch.object(baseline.subprocess, 'check_output', return_value='b' * 40), \
                 patch.object(baseline.subprocess, 'run'), patch.dict(os.environ, {'GITHUB_ACTIONS': 'false'}):
                baseline.main()
            self.assertIn('validate_only=true', output.read_text())


class TrustedToolPromotionPolicyTests(unittest.TestCase):
    def fixture(self, path='Infrastructure/Infrastructure.csproj', prior=None, current=None, blob=None):
        import release_policy as owner
        base,head,source='a'*40,'b'*40,'c'*40
        blob=blob or 'd'*40
        marker=dict(schemaVersion=1,baseRevision=base,sourceRevision=source,
            files={path:dict(mode='100644',blob=blob)})
        request=dict(trustedToolPromotion=marker,releaseMode='validate-only',targets=[],
            cloudflareWebsiteRouting=False,preserveLiveTargets=False,requestedBy='Founder',
            requestId='exact-reviewed-promotion',authorization='Founder exact source authorization')
        admission=dict(executionAuthority=head,candidate=source,sourcePr=626,
            validation={key:{} for key in ('approved-release-security-validation.yml',
                'masterapp-platform-architecture-validation.yml','parfait-isolated-validation.yml')})
        def git(args,**kwargs):
            command=args[1]
            if command=='rev-list':out=' '.join((head,base,source))
            elif command=='diff':out='M\0'+path+'\0M\0'+owner.REQUEST+'\0'
            elif command=='ls-tree':out='100644 blob '+blob+'\t'+args[-1]+'\0'
            elif command=='show' and prior is not None and current is not None:
                out=prior if args[2].startswith(base) else current
            elif command=='show':
                out='<Project>\n</Project>\n' if args[2].startswith(base) else '<Project>\n    <PackageReference Include="System.Security.Cryptography.Pkcs" Version="10.0.13" />\n</Project>\n'
            else:raise AssertionError(args)
            return SimpleNamespace(returncode=0,stdout=out)
        return owner,base,head,source,request,admission,git

    def invoke(self, mutate=None, git_mutate=None, fixture_kw=None):
        owner,base,head,source,request,admission,git=self.fixture(**(fixture_kw or {}))
        previous={'requestId':'older'}
        if mutate:mutate(request,admission,previous)
        def run(args,**kw):
            result=git(args,**kw)
            return git_mutate(args,result) if git_mutate else result
        with patch.object(owner,'read_request',side_effect=lambda sha:request if sha==head else previous),patch.object(owner.subprocess,'run',side_effect=run):
            return owner.trusted_tool_promotion(base,head,admission)

    def test_exact_dependency_promotion_is_nonpublishing(self):
        proof=self.invoke();self.assertFalse(proof['productionEligible']);self.assertFalse(proof['authorizesMutation'])

    def test_child_fixture_exact_path_is_nonpublishing_without_neighbor_expansion(self):
        path='scripts/test-release-children.py'
        proof=self.invoke(fixture_kw=dict(path=path))
        self.assertFalse(proof['productionEligible']);self.assertFalse(proof['authorizesMutation'])
        self.assertEqual(set(proof['files']),{path})
        for neighbor in ('scripts/test-release-children-extra.py', 'scripts/test-release-children.py/extra',
                         'scripts/release-children.py', 'scripts/unreviewed-child.py'):
            with self.subTest(path=neighbor),self.assertRaisesRegex(ValueError,'SCOPE_UNPROVEN'):
                self.invoke(fixture_kw=dict(path=neighbor))

    def test_child_fixture_retains_authorization_and_required_checks(self):
        for key,value in (('releaseMode','approved-only'), ('targets',['masterapp-protect']),
                          ('cloudflareWebsiteRouting',True), ('preserveLiveTargets',True),
                          ('requestedBy','Other')):
            with self.subTest(key=key),self.assertRaisesRegex(ValueError,'AUTHORIZATION_UNPROVEN'):
                self.invoke(fixture_kw=dict(path='scripts/test-release-children.py'),
                            mutate=lambda request,admission,previous:request.update({key:value}))
        with self.assertRaisesRegex(ValueError,'AUTHORIZATION_UNPROVEN'):
            self.invoke(fixture_kw=dict(path='scripts/test-release-children.py'),
                        mutate=lambda request,admission,previous:admission['validation'].pop('parfait-isolated-validation.yml'))
        with self.assertRaisesRegex(ValueError,'REQUEST_REPLAYED'):
            self.invoke(fixture_kw=dict(path='scripts/test-release-children.py'),
                        mutate=lambda request,admission,previous:previous.update(requestId=request['requestId']))

    def test_child_fixture_retains_exact_tree_source_and_mode_binding(self):
        def change(kind):
            def changed(args,result):
                if kind=='added' and args[1]=='diff':result.stdout=result.stdout.replace('M\0','A\0',1)
                if kind=='source' and args[1]=='ls-tree' and args[3]=='c'*40:result.stdout=result.stdout.replace('d'*40,'e'*40)
                if kind=='mode' and args[1]=='ls-tree' and args[3]=='a'*40:result.stdout=result.stdout.replace('100644','100755')
                if kind=='parent' and args[1]=='rev-list':result.stdout=result.stdout.replace('a'*40,'e'*40)
                return result
            return changed
        for kind,error in (('added','SCOPE_UNPROVEN'),('source','BLOB_UNPROVEN'),
                           ('mode','BLOB_UNPROVEN'),('parent','BASE_CHANGED')):
            with self.subTest(kind=kind),self.assertRaisesRegex(ValueError,error):
                self.invoke(fixture_kw=dict(path='scripts/test-release-children.py'),git_mutate=change(kind))

    def test_child_fixture_bootstrap_owners_remain_nonpublishing(self):
        for path in ('scripts/release_policy.py','scripts/test-release-policy.py'):
            with self.subTest(path=path):
                proof=self.invoke(fixture_kw=dict(path=path))
                self.assertFalse(proof['productionEligible']);self.assertFalse(proof['authorizesMutation'])

    def test_existing_schema_observer_owners_promote_without_publishing(self):
        paths = (
            'scripts/MigrationReleaseProbe/Program.cs',
            'scripts/MigrationReleaseProbe/CandidateContract.cs',
            'scripts/MigrationReleaseProbe/CandidateContract.Structures.cs',
            'scripts/MigrationReleaseProbe/Rehearsal/CandidateContractTests.cs',
            'scripts/MigrationReleaseProbe/Rehearsal/RehearsalFixture.cs',
        )
        for path in paths:
            with self.subTest(path=path):
                proof=self.invoke(fixture_kw=dict(path=path))
                self.assertFalse(proof['productionEligible']);self.assertFalse(proof['authorizesMutation'])
        for path in ('scripts/MigrationReleaseProbe/Unknown.cs',
                     'scripts/MigrationReleaseProbe/MigrationReleaseProbe.csproj',
                     'Infrastructure/Migrations/20261020160000_AddReceptionistCoordination.cs',
                     'Infrastructure/Migrations/MasterAppDbContextModelSnapshot.cs'):
            with self.subTest(path=path),self.assertRaisesRegex(ValueError,'SCOPE_UNPROVEN'):
                self.invoke(fixture_kw=dict(path=path))

    def test_browser_observer_additions_require_exact_absence_and_source_binding(self):
        paths = ('scripts/MigrationReleaseProbe/CandidateContract.Browser.cs',
                 'scripts/MigrationReleaseProbe/Rehearsal/RehearsalFixture.Browser.cs')
        def added(args, result):
            if args[1] == 'diff': result.stdout = result.stdout.replace('M\0', 'A\0', 1)
            if args[1] == 'ls-tree' and args[3] == 'a'*40 and args[-1] in paths: result.stdout = ''
            return result
        for path in paths:
            with self.subTest(path=path):
                proof = self.invoke(fixture_kw=dict(path=path), git_mutate=added)
                self.assertFalse(proof['productionEligible']); self.assertFalse(proof['authorizesMutation'])
                self.assertEqual(set(proof['files']), {path})
            for defect, expected in (('present', 'ADDITION_UNPROVEN'), ('executable', 'ADDITION_UNPROVEN'),
                                     ('source', 'BLOB_UNPROVEN'), ('delete', 'SCOPE_UNPROVEN'),
                                     ('rename', 'SCOPE_UNPROVEN'), ('failed_absence', 'IMMUTABLE_SOURCE_UNAVAILABLE')):
                def mutate(args, result):
                    result = added(args, result)
                    if defect == 'present' and args[1] == 'ls-tree' and args[3] == 'a'*40 and args[-1] == path:
                        result.stdout = '100644 blob ' + 'd'*40 + '\t' + path + '\0'
                    if defect == 'executable' and args[1] == 'ls-tree': result.stdout = result.stdout.replace('100644', '100755')
                    if defect == 'source' and args[1] == 'ls-tree' and args[3] == 'c'*40: result.stdout = result.stdout.replace('d'*40, 'e'*40)
                    if defect in ('delete', 'rename') and args[1] == 'diff': result.stdout = result.stdout.replace('A\0', ('D' if defect == 'delete' else 'R100') + '\0', 1)
                    if defect == 'failed_absence' and args[1] == 'ls-tree' and args[3] == 'a'*40 and args[-1] == path: result.returncode = 1
                    return result
                def request(req, admission, previous):
                    if defect == 'executable': req['trustedToolPromotion']['files'][path]['mode'] = '100755'
                with self.subTest(path=path, defect=defect), self.assertRaisesRegex(ValueError, expected):
                    self.invoke(fixture_kw=dict(path=path), git_mutate=mutate, mutate=request)
        for neighbor in ('scripts/MigrationReleaseProbe/CandidateContract.BrowserExtra.cs',
                         'scripts/MigrationReleaseProbe/Rehearsal/RehearsalFixture.Unreviewed.cs',
                         'Infrastructure/Migrations/UnreviewedBrowser.cs'):
            with self.subTest(path=neighbor), self.assertRaisesRegex(ValueError, 'SCOPE_UNPROVEN'):
                self.invoke(fixture_kw=dict(path=neighbor), git_mutate=added)

    def test_schema_tool_promotion_still_rejects_added_path_and_unbound_source(self):
        def added(args,result):
            if args[1]=='diff':result.stdout=result.stdout.replace('M\0','A\0',1)
            return result
        with self.assertRaisesRegex(ValueError,'SCOPE_UNPROVEN'):
            self.invoke(fixture_kw=dict(path='scripts/MigrationReleaseProbe/Program.cs'),git_mutate=added)
        def changed(args,result):
            if args[1]=='ls-tree' and args[3]=='c'*40:result.stdout=result.stdout.replace('d'*40,'e'*40)
            return result
        with self.assertRaisesRegex(ValueError,'BLOB_UNPROVEN'):
            self.invoke(fixture_kw=dict(path='scripts/MigrationReleaseProbe/Program.cs'),git_mutate=changed)

    def test_exact_schema_wrapper_owners_are_nonpublishing_without_neighbor_expansion(self):
        for path in ('scripts/release-migration.py', 'scripts/test-release-migration-probe-retry.py'):
            with self.subTest(path=path):
                proof=self.invoke(fixture_kw=dict(path=path))
                self.assertFalse(proof['productionEligible']);self.assertFalse(proof['authorizesMutation'])
        for path in ('scripts/release-migration-extra.py', 'scripts/test-release-migration-probe-retry-extra.py',
                     'Infrastructure/Migrations/20261020160000_AddReceptionistCoordination.cs'):
            with self.subTest(path=path),self.assertRaisesRegex(ValueError,'SCOPE_UNPROVEN'):
                self.invoke(fixture_kw=dict(path=path))
        for key,value in (('releaseMode','approved-only'), ('targets',['masterapp-protect']),
                          ('cloudflareWebsiteRouting',True), ('preserveLiveTargets',True)):
            with self.subTest(key=key),self.assertRaises(ValueError):
                self.invoke(fixture_kw=dict(path='scripts/release-migration.py'),
                            mutate=lambda request,admission,previous:request.update({key:value}))

    def test_exact_forward_correlation_owners_are_nonpublishing_without_neighbor_expansion(self):
        for path in ('scripts/deploy-approved-app.py', 'scripts/release-operation-evidence.py',
                     'scripts/test-deploy-approved-app.py', 'scripts/test-release-operation-evidence.py'):
            with self.subTest(path=path):
                proof=self.invoke(fixture_kw=dict(path=path))
                self.assertFalse(proof['productionEligible']);self.assertFalse(proof['authorizesMutation'])
        for path in ('scripts/release-operation-evidence-extra.py',
                     'scripts/test-release-operation-evidence-extra.py',
                     'scripts/test-deploy-approved-app-extra.py',
                     'scripts/deploy-unreviewed-app.py'):
            with self.subTest(path=path),self.assertRaisesRegex(ValueError,'SCOPE_UNPROVEN'):
                self.invoke(fixture_kw=dict(path=path))

    def test_exact_artifact_transport_owners_promote_without_neighbor_or_publication_expansion(self):
        for path in ('scripts/release-artifacts/transport.cjs', 'scripts/release-artifacts/test-transport.cjs'):
            with self.subTest(path=path):
                proof=self.invoke(fixture_kw=dict(path=path))
                self.assertFalse(proof['productionEligible']);self.assertFalse(proof['authorizesMutation'])
        for path in ('scripts/release-artifacts/package.json', 'scripts/release-artifacts/action.yml',
                     'scripts/release-artifacts/alternate.cjs', 'scripts/release-artifacts/transport-extra.cjs'):
            with self.subTest(path=path),self.assertRaisesRegex(ValueError,'SCOPE_UNPROVEN'):
                self.invoke(fixture_kw=dict(path=path))
        for key,value in (('releaseMode','approved-only'), ('targets',['masterapp-protect']),
                          ('cloudflareWebsiteRouting',True), ('preserveLiveTargets',True)):
            with self.subTest(key=key),self.assertRaises(ValueError):
                self.invoke(fixture_kw=dict(path='scripts/release-artifacts/transport.cjs'),
                            mutate=lambda request,admission,previous:request.update({key:value}))
        def changed(args,result):
            if args[1]=='ls-tree' and args[3]=='c'*40:result.stdout=result.stdout.replace('d'*40,'e'*40)
            return result
        with self.assertRaisesRegex(ValueError,'BLOB_UNPROVEN'):
            self.invoke(fixture_kw=dict(path='scripts/release-artifacts/transport.cjs'),git_mutate=changed)

    def test_current_fast_track_promotion_returns_before_unresolved_predecessor(self):
        import importlib.util
        spec=importlib.util.spec_from_file_location('promotion_boundary_authority',Path(__file__).with_name('validation-resume.py'))
        authority=importlib.util.module_from_spec(spec);spec.loader.exec_module(authority)
        owner,base,head,source,request,admission,git=self.fixture(path='scripts/MigrationReleaseProbe/Program.cs')
        with patch.object(authority,'_fast_track_policy',return_value=owner), \
             patch.object(owner,'read_request',side_effect=lambda sha:request if sha==head else {'requestId':'older'}), \
             patch.object(owner.subprocess,'run',side_effect=git), \
             patch.object(authority,'fast_track_source_admission',return_value=admission) as authenticate, \
             patch.object(authority.subprocess,'check_output',return_value='# legacy policy fixture'), \
             patch.object(authority,'fast_track_effective_baseline',side_effect=RuntimeError('unresolved predecessor')) as baseline:
            scope=authority.fast_track_selection(base,head)
        authenticate.assert_called_once_with(allow_nonpublishing=True);baseline.assert_not_called()
        self.assertFalse(scope['productionEligible']);self.assertFalse(scope['selected'])
        self.assertEqual({'include':[]},scope['matrix']);self.assertFalse(scope['schema']);self.assertFalse(scope['routing'])
        self.assertEqual('trusted-tool-promotion',scope['promotion']['phase'])

    def test_later_feature_release_still_requires_predecessor_reconciliation(self):
        import importlib.util
        spec=importlib.util.spec_from_file_location('promotion_transition_authority',Path(__file__).with_name('validation-resume.py'))
        authority=importlib.util.module_from_spec(spec);spec.loader.exec_module(authority)
        owner,base,head,source,prior,admission,git=self.fixture(path='scripts/MigrationReleaseProbe/Program.cs')
        release=dict(requestedBy='Founder',authorization='Existing release authorization',requestId='fresh-release',
                     releaseMode='approved-only',targets=['masterapp-portal'],cloudflareWebsiteRouting=False)
        with patch.object(authority,'_fast_track_policy',return_value=owner), \
             patch.object(owner,'read_request',side_effect=lambda sha:release if sha==head else prior), \
             patch.object(authority,'fast_track_source_admission',return_value=admission), \
             patch.object(authority.subprocess,'check_output',return_value='# legacy policy fixture'), \
             patch.object(authority,'fast_track_effective_baseline',side_effect=RuntimeError('unresolved predecessor')) as baseline:
            with self.assertRaisesRegex(RuntimeError,'unresolved predecessor'):
                authority.fast_track_selection(base,head)
        baseline.assert_called_once_with(base,head)

    def test_unbound_replayed_or_app_marker_rejected(self):
        changes=[lambda r,a,p:r.update(trustedToolPromotion=None),
            lambda r,a,p:r['trustedToolPromotion'].update(baseRevision='f'*40),
            lambda r,a,p:p.update(requestId=r['requestId']),
            lambda r,a,p:a.update(executionAuthority='f'*40),
            lambda r,a,p:r.update(targets=['website']),
            lambda r,a,p:r['trustedToolPromotion']['files'].update({'AgentPortal/Program.cs':dict(mode='100644',blob='d'*40)})]
        for change in changes:
            with self.subTest(change=change),self.assertRaises(ValueError):self.invoke(change)

    def test_missing_object_mode_blob_rename_delete_and_dependency_change_reject(self):
        def mutate(kind):
            def change(args,result):
                if kind=='missing':return SimpleNamespace(returncode=128,stdout='')
                if args[1]=='ls-tree' and kind in ('symlink','blob','chmod'):
                    result.stdout=result.stdout.replace('100644', '120000' if kind=='symlink' else '100755') if kind!='blob' else result.stdout.replace('d'*40,'e'*40)
                if args[1]=='diff' and kind in ('rename','delete'):result.stdout=result.stdout.replace('M\0','R\0' if kind=='rename' else 'D\0',1)
                if args[1]=='show' and kind=='dependency' and args[2].startswith('b'*40):result.stdout+='<!-- unrelated -->\n'
                return result
            return change
        for kind in ('missing','symlink','blob','chmod','rename','delete','dependency'):
            with self.subTest(kind=kind),self.assertRaises(ValueError):self.invoke(git_mutate=mutate(kind))


    def test_security_pins_are_nonpublishing_and_reject_unrelated_project_changes(self):
        cases = (
            ('AgentPortal/AgentPortal.csproj', 'MessagePack', '3.1.7', '3.1.9'),
            ('AgentPortal.Tests/AgentPortal.Tests.csproj', 'System.Security.Cryptography.Xml', '10.0.10', '10.0.13'),
            ('scripts/DomainReleaseRefresh/DomainReleaseRefresh.csproj', 'System.Security.Cryptography.Xml', '10.0.10', '10.0.13'),
        )
        for path, package, old, new in cases:
            ref = f'<PackageReference Include="{package}" Version="{old}" />'
            prior = '<Project>' + ref + '</Project>'
            current = prior.replace(old, new)
            with self.subTest(path=path):
                proof = self.invoke(fixture_kw=dict(path=path, prior=prior, current=current))
                self.assertFalse(proof['productionEligible'])
                self.assertFalse(proof['authorizesMutation'])
            for bad_prior, bad_current in (
                (prior, prior),
                (current, prior),
                (prior, current.replace(new, '99.0.0')),
                (prior, current.replace('/>', ' Condition="true" />')),
                (prior, current.replace('</Project>', '<ProjectReference Include="other.csproj" /></Project>')),
                (prior, current.replace('</Project>', ref.replace(old, new) + '</Project>')),
                (prior, current + '<!-- unrelated change -->'),
                (prior.replace('</Project>', ref + '</Project>'), current),
            ):
                with self.subTest(path=path, current=bad_current), self.assertRaisesRegex(ValueError, 'SECURITY_PACKAGE_SCOPE_UNPROVEN'):
                    self.invoke(fixture_kw=dict(path=path, prior=bad_prior, current=bad_current))

    def test_only_accepted_favicon_fixture_blob_is_admitted(self):
        path = 'AgentPortal.Tests/WebsiteContentEditorRoundTripTests.cs'
        proof = self.invoke(fixture_kw=dict(path=path, blob='a380649c2168e7e67b391be1147ecc40b55c0024'))
        self.assertFalse(proof['productionEligible'])
        with self.assertRaisesRegex(ValueError, 'FIXTURE_SCOPE_UNPROVEN'):
            self.invoke(fixture_kw=dict(path=path))

    def test_only_accepted_mobile_fixture_blob_is_admitted(self):
        path='tests/layout/modal-content-region.test.mjs'
        proof=self.invoke(fixture_kw=dict(path=path,blob='c21b130bda415433686f01ece3111afe52aa217a'))
        self.assertFalse(proof['productionEligible']); self.assertFalse(proof['authorizesMutation'])
        with self.assertRaisesRegex(ValueError,'MOBILE_FIXTURE_SCOPE_UNPROVEN'):
            self.invoke(fixture_kw=dict(path=path))

    def test_only_reviewed_navigation_and_crm_validation_blobs_are_admitted(self):
        pins = {'tests/layout/app-navigation-contract.test.mjs': '57c103161f271a5a00410180a99d4454032a5cef', 'tests/analytics/crm-history-browser.cjs': '4cfbb35f1286552d9c6c8e76a82c68ac0389f46c', 'tests/analytics/crm-history.test.cjs': '95d2f8ec9fb84d61b959581d5eeeb5664920f462'}
        for path, blob in pins.items():
            with self.subTest(path=path):
                proof=self.invoke(fixture_kw=dict(path=path,blob=blob))
                self.assertFalse(proof['productionEligible']); self.assertFalse(proof['authorizesMutation'])
                with self.assertRaisesRegex(ValueError,'VALIDATION_FIXTURE_SCOPE_UNPROVEN'):
                    self.invoke(fixture_kw=dict(path=path))

class FastTrackIdentityObservationTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location('identity_authority', ROOT / 'validation-resume.py')
        self.a = importlib.util.module_from_spec(spec); spec.loader.exec_module(self.a)
        self.sha = 'b' * 40
        self.env = dict(GITHUB_ACTIONS='true', GITHUB_EVENT_NAME='push', GITHUB_REF='refs/heads/Legend/fast-track-release',
            GITHUB_REPOSITORY='owner/repo', GH_TOKEN='test-only', GITHUB_SHA=self.sha, GITHUB_RUN_ID='123',
            GITHUB_RUN_ATTEMPT='3', GITHUB_WORKFLOW_REF='owner/repo/.github/workflows/legend-fast-track-ui.yml@refs/heads/Legend/fast-track-release',
            GITHUB_JOB='deploy-ui', TARGET_KEY='portal', RUNNER_NAME='runner-one')
        self.run = dict(id=123, run_attempt=3, path='.github/workflows/legend-fast-track-ui.yml',
            head_branch='Legend/fast-track-release', event='push', head_sha=self.sha, status='in_progress',
            head_repository=dict(full_name='owner/repo'))
        self.job = dict(id=456, run_id=123, run_attempt=3, head_sha=self.sha, status='in_progress',
            runner_name='runner-one', name='Affected App Deploy — AgentPortal')
        self.now = 0; self.sleeps = []; self.reads = []
    def invoke(self, jobs=None, head=None, allow=False):
        def get(repo, path, token):
            self.reads.append(path)
            self.assertGreater(self.a.evidence_remaining(30), 0)
            if path.startswith('git/'): return dict(object=dict(sha=head or self.sha))
            if '/jobs?' in path:
                rows = jobs() if callable(jobs) else ([self.job] if jobs is None else jobs)
                return dict(jobs=rows, total_count=len(rows))
            return self.run
        def sleep(delay): self.sleeps.append(delay); self.now += delay
        with patch.dict(os.environ, self.env, clear=True), patch.object(self.a, 'api_get', side_effect=get), \
             patch.object(self.a, '_fast_track_policy', return_value=SimpleNamespace(tool_promotion_requested=lambda _: allow)):
            return self.a.assert_fast_track_release_execution(clock=lambda:self.now, sleep=sleep)
    def test_in_progress_requires_exact_live_job(self):
        self.assertEqual(self.invoke(), self.run); self.assertEqual(self.sleeps, [])
    def test_queued_parent_with_exact_live_job_is_valid(self):
        self.run['status']='queued'; self.assertEqual(self.invoke(), self.run)
    def test_absent_job_can_be_observed_without_nested_budget(self):
        count=[0]
        def rows(): count[0]+=1; return [] if count[0]<3 else [self.job]
        self.invoke(rows); self.assertEqual(self.sleeps,[2,2])
    def test_pending_job_can_settle(self):
        def rows(): return [dict(self.job,status='queued',runner_name=None)] if self.now<2 else [self.job]
        self.invoke(rows); self.assertEqual(self.sleeps,[2])
    def test_absence_exhausts_single_budget(self):
        with self.assertRaises((RuntimeError,self.a.EvidenceLookupUnavailable)): self.invoke([])
        self.assertLessEqual(sum(self.sleeps),30); self.assertLessEqual(len(self.reads),48)
    def test_duplicate_identity_is_immediate_failure(self):
        with self.assertRaisesRegex(RuntimeError,'ACTIVE_JOB'): self.invoke([self.job,self.job])
        self.assertEqual(self.sleeps,[])
    def test_wrong_target_never_satisfies_ownership(self):
        with self.assertRaises(RuntimeError): self.invoke([dict(self.job,name='Affected App Deploy — Website')])
    def test_missing_target_fails_before_reads(self):
        del self.env['TARGET_KEY']
        with self.assertRaisesRegex(RuntimeError,'TARGET_UNPROVEN'): self.invoke()
        self.assertEqual(self.reads,[])
    def test_mismatched_runner_attempt_revision_or_terminal_job_fails_closed(self):
        for key,value in [('runner_name','other'),('run_attempt',2),('head_sha','c'*40),('status','completed'),('id',True)]:
            with self.subTest(key=key), self.assertRaisesRegex(RuntimeError,'ACTIVE_JOB'):
                self.invoke([dict(self.job,**{key:value})])
        self.assertEqual(self.sleeps,[])
    def test_terminal_or_wrong_run_does_not_poll(self):
        for key,value in [('status','completed'),('run_attempt',2),('head_sha','c'*40),('event','workflow_dispatch')]:
            old=self.run[key];self.run[key]=value
            with self.subTest(key=key),self.assertRaisesRegex(RuntimeError,'PRODUCER_UNPROVEN'):self.invoke()
            self.run[key]=old
        self.assertEqual(self.sleeps,[])
    def test_head_change_fails_before_success(self):
        with self.assertRaisesRegex(RuntimeError,'HEAD_CHANGED'): self.invoke(head='c'*40)
    def test_promotion_cannot_gain_publication_authority(self):
        with self.assertRaisesRegex(RuntimeError,'NONPUBLISHING'):self.invoke(allow=True)
        self.assertEqual(self.reads,[])
    def test_final_read_over_deadline_cannot_return_identity(self):
        def get(repo,path,token):
            if path.startswith('git/'):
                self.now=31
                return dict(object=dict(sha=self.sha))
            if '/jobs?' in path:return dict(jobs=[self.job],total_count=1)
            return self.run
        with patch.dict(os.environ,self.env,clear=True),patch.object(self.a,'api_get',side_effect=get),patch.object(self.a,'_fast_track_policy',return_value=SimpleNamespace(tool_promotion_requested=lambda _:False)):
            with self.assertRaises(self.a.EvidenceLookupUnavailable):
                self.a.assert_fast_track_release_execution(clock=lambda:self.now,sleep=lambda _:None)

    def test_shorter_parent_deadline_rejects_late_child_read(self):
        with self.a.evidence_lookup_budget(1,clock=lambda:self.now):
            with self.assertRaises(self.a.EvidenceLookupUnavailable): self.invoke([])
        self.assertLessEqual(sum(self.sleeps),1)
    def test_all_canonical_target_labels(self):
        for key,label in [('portal','AgentPortal'),('client','ClientApp'),('protect','Protect'),('parfait','Parfait'),('website','Website')]:
            with self.subTest(key=key):
                self.env['TARGET_KEY']=key;self.job['name']='Affected App Deploy — '+label
                self.assertEqual(self.invoke(),self.run)
    def test_wrong_workflow_ref_repository_or_event(self):
        for key,value in [('GITHUB_WORKFLOW_REF','wrong'),('GITHUB_REF','refs/heads/other'),('GITHUB_EVENT_NAME','pull_request')]:
            old=self.env[key];self.env[key]=value
            with self.subTest(key=key),self.assertRaises(RuntimeError):self.invoke()
            self.env[key]=old
        self.run['head_repository']={'full_name':'other/repo'}
        with self.assertRaises(RuntimeError):self.invoke()
    def test_nonpublishing_permission_still_requires_identity(self):
        with patch.dict(os.environ,self.env,clear=True),patch.object(self.a,'api_get',side_effect=lambda r,p,t:dict(object=dict(sha=self.sha)) if p.startswith('git/') else (dict(jobs=[self.job],total_count=1) if '/jobs?' in p else self.run)),patch.object(self.a,'_fast_track_policy',return_value=SimpleNamespace(tool_promotion_requested=lambda _:True)):
            self.assertEqual(self.a.assert_fast_track_release_execution(allow_nonpublishing=True),self.run)
    def test_malformed_job_inventory_fails_immediately(self):
        for payload in [{'jobs':[],'total_count':1},{'jobs':[None],'total_count':1},{'jobs':[],'total_count':True}]:
            def get(r,p,t):return payload if '/jobs?' in p else self.run
            with self.subTest(payload=payload),patch.dict(os.environ,self.env,clear=True),patch.object(self.a,'api_get',side_effect=get),patch.object(self.a,'_fast_track_policy',return_value=SimpleNamespace(tool_promotion_requested=lambda _:False)):
                with self.assertRaisesRegex(RuntimeError,'ACTIVE_JOB'):self.a.assert_fast_track_release_execution()
    def test_api_retry_backoff_obeys_parent_budget(self):
        def sleep(delay):self.now+=delay
        with self.a.evidence_lookup_budget(1,clock=lambda:self.now),patch.object(self.a.urllib.request,'urlopen',side_effect=TimeoutError()),patch.object(self.a.time,'sleep',side_effect=sleep):
            with self.assertRaises(self.a.EvidenceLookupUnavailable):self.a.api_get('owner/repo','actions/runs/123','test-only')
        self.assertEqual(self.now,1)

    def test_restore_has_no_second_identity_retry(self):
        spec=importlib.util.spec_from_file_location('identity_deploy',ROOT/'deploy-approved-app.py')
        d=importlib.util.module_from_spec(spec);spec.loader.exec_module(d)
        with patch.object(d._RELEASE_AUTHORITY,'assert_fast_track_release_execution',side_effect=RuntimeError('FAST_TRACK_EXECUTION_PRODUCER_UNPROVEN')) as check,patch.object(d.time,'sleep') as sleeper:
            with self.assertRaises(RuntimeError):d.fast_track_restore_package('portal',Path('/unused'))
            check.assert_called_once();sleeper.assert_not_called()

class FastTrackCompletionAuthorityTests(unittest.TestCase):
    def setUp(self):
        spec=importlib.util.spec_from_file_location('completion_authority_tests',ROOT/'validation-resume.py')
        self.a=importlib.util.module_from_spec(spec);spec.loader.exec_module(self.a)
        self.sha='b'*40; self.tool='c'*40; self.repo='owner/repo'
        self.run=dict(id=123,run_attempt=3,head_sha=self.sha,path='.github/workflows/legend-fast-track-ui.yml',
            head_branch='Legend/fast-track-release',event='push',head_repository=dict(full_name=self.repo),status='completed',conclusion='failure')
        self.intent=dict(target='portal',applicationRevision=self.sha,packageDigest='d'*64,schemaVersion=1,
            baseline='a'*40,packageProducerRun=123,producingRun=123,producingAttempt=1,baselineDeploymentIds=['old'],phase='intent')
        import hashlib
        op=hashlib.sha256(json.dumps({k:self.intent[k] for k in ['target','applicationRevision','packageDigest']},sort_keys=True,separators=(',',':')).encode()).hexdigest()
        self.intent.update(operationId=op,submissionContract=dict(kind='azure_onedeploy_windows_intent_v1',value=op))
        self.job=dict(id=456,run_id=123,run_attempt=1,head_sha=self.sha,status='completed',conclusion='failure',
            name='Affected App Deploy — AgentPortal',started_at='2026-10-21T13:00:00Z',completed_at='2026-10-21T13:05:00Z')
        self.job['steps']=[dict(name=name,status='completed',conclusion=conclusion,started_at=start,completed_at=end) for name,conclusion,start,end in [('Preserve immutable application bytes before upload','success','2026-10-21T13:00:00Z','2026-10-21T13:00:59Z'),('Publish or reconcile exact application bytes','failure','2026-10-21T13:01:00Z','2026-10-21T13:05:00Z')]]
        def art(id,name,time):return dict(id=id,name=name,expired=False,workflow_run=dict(id=123,head_sha=self.sha),digest='sha256:'+'e'*64,created_at=time)
        self.ia=art(10,'legend-release-operation-intent-'+op,'2026-10-21T13:01:00Z')
        self.pa=art(11,'fast-track-package-'+self.sha+'-portal','2026-10-21T13:00:30Z')
        self.original=dict(run=self.run,targets=['portal'],baseRevision='a'*40)
        self.material_value=dict(packageJob=self.job,packageAttempt=1,originalPublicationStep=self.job['steps'][1],intent=self.intent,intentArtifact=self.ia,packageArtifact=self.pa,originalJob=self.job,submissionObservation=None)
        self.observer=dict(runId=789,runAttempt=1,jobId=800,toolRevision=self.tool,promotionRunId=700)
        # Original-completion fixtures have no retained recovery summary.
        # Exercise the real reader, but never leak test-only credentials to HTTP.
        recovery_path = 'actions/artifacts?name=' + self.a.fast_track_recovery_summary_name(123, self.sha) + '&per_page=100'
        def recovery_inventory(repository, path, token):
            self.assertEqual((repository, path, token), (self.repo, recovery_path, 'test-only'))
            return dict(artifacts=[], total_count=0)
        api_patch = patch.object(self.a, 'api_get', side_effect=recovery_inventory)
        self.recovery_inventory_api = api_patch.start()
        self.addCleanup(api_patch.stop)

    def test_original_completion_fixture_recovery_inventory_is_empty_and_isolated(self):
        self.assertFalse(self.a.fast_track_recovery_completed(self.repo, self.original, 'test-only'))
        self.recovery_inventory_api.assert_called_once()
        path = self.recovery_inventory_api.call_args.args[1]
        for repository, route, token in [(self.repo, 'actions/runs/123', 'test-only'),
                                         ('other/repo', path, 'test-only'),
                                         (self.repo, path, 'unexpected')]:
            with self.subTest(repository=repository, route=route, token=token), self.assertRaises(AssertionError):
                self.a.api_get(repository, route, token)
    def material(self,artifacts=None,jobs=None,retained=None,submitted=None):
        artifacts=list([self.ia,self.pa] if artifacts is None else artifacts)
        if submitted is not None:
            artifacts.append(dict(self.ia,id=12,name='legend-release-operation-submitted-'+self.intent['operationId']))
        original=dict(self.original,artifacts=artifacts,attempts={1:[self.job] if jobs is None else jobs})
        def value(r,run,a,*args,**kw):return submitted if '-submitted-' in a['name'] else self.intent
        with patch.object(self.a,'_completion_original_unchanged'),patch.object(self.a,'component_upload_proven'),patch.object(self.a,'_release_job_log',return_value='fixture'),patch.object(self.a,'_release_history_json',side_effect=value),patch.object(self.a,'release_operation_history',side_effect=lambda *args,**kw:submitted if kw.get('phase')=='submitted' else (self.intent if retained is None else retained)):
            return self.a.fast_track_completion_material(self.repo,original,'portal','test-only')
    def test_material_requires_exact_original_intent_package_and_job(self):
        self.assertEqual(self.material(),self.material_value)
    def test_material_preserves_valid_submitted_observation_and_rejects_conflicts(self):
        submitted=dict(self.intent,phase='submitted',deploymentId='new')
        self.assertEqual(self.material(submitted=submitted)['submissionObservation'],submitted)
        for key,value in [('deploymentId','old'),('packageDigest','f'*64),('producingRun',999),('phase','intent'),('extra','bad')]:
            with self.subTest(key=key),self.assertRaises(RuntimeError):self.material(submitted=dict(submitted,**{key:value}))
    def test_historical_completion_must_match_original_submitted_id(self):
        self.material_value['submissionObservation']=dict(self.intent,phase='submitted',deploymentId='other')
        with self.assertRaisesRegex(ValueError,'CONFLICTS_WITH_ORIGINAL_SUBMISSION'):self.validate(self.receipt())
        self.material_value['submissionObservation']['deploymentId']='new'
        self.assertEqual(self.validate(self.receipt()),self.receipt())
    def test_original_success_cannot_override_conflicting_submitted_id(self):
        self.material_value['submissionObservation']=dict(self.intent,phase='submitted',deploymentId='other')
        with patch.object(self.a,'fast_track_completion_original',return_value=self.original),patch.object(self.a,'fast_track_completion_material',return_value=self.material_value),patch.object(self.a,'release_operation_history',return_value=self.receipt()):
            with self.assertRaisesRegex(ValueError,'CONFLICTS_WITH_SUBMISSION'):self.a.fast_track_completed_operation_receipts(self.repo,self.run,'test-only',require_completed_observer=False)
    def test_material_missing_expired_or_ambiguous_artifact_rejected(self):
        for artifacts in [[self.ia],[self.ia,self.pa,self.pa],[dict(self.ia,expired=True),self.pa],[dict(self.ia,digest='bad'),self.pa]]:
            with self.subTest(artifacts=artifacts),self.assertRaises(ValueError):self.material(artifacts)
    def test_material_intent_mutations_fail_closed(self):
        for key,value in [('producingRun',999),('producingAttempt',4),('packageProducerRun',999),('phase','success'),('schemaVersion',2),('baseline','bad'),('submissionContract',{}),('packageDigest','bad'),('baselineDeploymentIds',['old','old'])]:
            old=self.intent[key];self.intent[key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):self.material()
            self.intent[key]=old
    def test_material_artifact_outside_owner_window_rejected(self):
        for time in ['2026-10-21T12:59:00Z','2026-10-21T13:06:00Z','2026-10-21T13:01:00']:
            with self.subTest(time=time),self.assertRaises(ValueError):self.material([dict(self.ia,created_at=time),self.pa])
    def test_material_wrong_owner_or_competing_intent_rejected(self):
        for jobs in [[],[self.job,self.job],[dict(self.job,run_attempt=2)],[dict(self.job,head_sha=self.tool)]]:
            with self.subTest(jobs=jobs),self.assertRaises(ValueError):self.material(jobs=jobs)
        with self.assertRaises(ValueError):self.material(retained=dict(self.intent,baseline='f'*40))
    def original_call(self,jobs=None,request=None,*,baseline=None,targets=None):
        targets = targets or ['portal']
        scope=dict(matrix=dict(include=[dict(key=key,azure_app=self.a.RELEASE_TARGETS[key]['releaseName']) for key in targets]),commerce=True,routing=False)
        names=['Select exact affected applications','Admit exact validated fast-track source','Reconcile production schema before publication','Publish verified website routing']
        rows=[dict(self.job,id=600+i,name=n,conclusion='skipped' if n==names[-1] else 'success') for i,n in enumerate(names)]+[dict(self.job,id=456+i,name='Affected App Deploy — '+self.a.fast_track_target_label(key)) for i,key in enumerate(targets)]
        for row,step_names in zip(rows,[['Derive application impact exclusively from changed paths'],['Authenticate merged source and preserved required validation'],['Apply or reconcile the exact rehearsed migration bundle','Reconcile fresh already-ready schema without SQL bundle or mutation']]):
            row['steps']=[dict(name=n,status='completed',conclusion='skipped' if i else 'success') for i,n in enumerate(step_names)]
        req=dict(releaseMode='approved-only',requestedBy='Founder',preserveLiveTargets=False,targets=[self.a.RELEASE_TARGETS[key]['releaseName'] for key in targets],cloudflareWebsiteRouting=False) if request is None else request
        def api(r,p,t):
            if p.startswith('actions/runs?'):return dict(workflow_runs=[self.run],total_count=1)
            if '/artifacts?' in p:return dict(artifacts=[self.ia,self.pa],total_count=2)
            if '/jobs?' in p:
                attempt=int(p.split('/attempts/')[1].split('/')[0]);selected=[dict(row,run_attempt=attempt) for row in (rows if jobs is None else jobs)]
                return dict(jobs=selected,total_count=len(selected))
            return self.run
        def git(*args,**kwargs):return self.sha+' '+'a'*40+' '+'d'*40 if 'rev-list' in args[0] else self.a.RELEASE_REQUEST_PATH+'\n'
        with patch.object(self.a,'completed_resource_contract',return_value=None),patch.object(self.a,'api_get',side_effect=api),patch.object(self.a,'fast_track_merged_source_proof'),patch.object(self.a,'fast_track_effective_baseline',return_value=baseline or 'a'*40),patch.object(self.a.subprocess,'check_output',side_effect=git),patch.object(self.a,'fast_track_scope',return_value=scope),patch.object(self.a,'_release_history_source',return_value=json.dumps(req)):
            return self.a.fast_track_completion_original(self.repo,123,self.sha,'test-only')
    def test_original_requires_all_obligations_and_selected_targets(self):
        self.assertEqual(self.original_call()['targets'],['portal'])
        with self.assertRaises(ValueError):self.original_call(jobs=[self.job])
        with self.assertRaises(ValueError):self.original_call(request=dict(releaseMode='approved-only',targets=['masterapp-website'],cloudflareWebsiteRouting=False))
    def test_original_active_or_changed_revision_rejected(self):
        for key,value in [('status','in_progress'),('head_sha',self.tool),('run_attempt',True),('event','workflow_dispatch')]:
            old=self.run[key];self.run[key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):self.original_call()
            self.run[key]=old
    def test_carried_baseline_requires_all_original_intents_and_returns_actual_base(self):
        baseline='f'*40
        targets=['portal','client','website','protect','parfait']
        calls=[]
        def material(repo,original,key,token):
            self.assertEqual((repo,token),(self.repo,'test-only'))
            self.assertEqual(original['baseRevision'],baseline)
            self.assertEqual(original['targets'],targets)
            calls.append(key)
            return dict(intent=dict(self.intent,baseline=baseline,target=key))
        with patch.object(self.a,'fast_track_completion_material',side_effect=material):
            result=self.original_call(baseline=baseline,targets=targets)
        self.assertEqual(result['baseRevision'],baseline)
        self.assertEqual(calls,targets)
        self.assertFalse(self.a._COMPLETION_BASELINE_PROVING.get())

    def test_carried_baseline_uses_real_original_material_reader(self):
        self.run['run_attempt']=1
        self.intent['baseline']='f'*40
        real=self.a.fast_track_completion_material
        with patch.object(self.a,'_release_history_json',return_value=self.intent),patch.object(self.a,'component_upload_proven'),patch.object(self.a,'_release_job_log',return_value='fixture'),patch.object(self.a,'release_operation_history',side_effect=lambda *args,**kw:None if kw.get('phase')=='submitted' else self.intent),patch.object(self.a,'fast_track_completion_material',wraps=real) as material:
            self.assertEqual(self.original_call(baseline='f'*40)['baseRevision'],'f'*40)
            material.assert_called_once()
            self.intent['baseline']='a'*40
            with self.assertRaisesRegex(ValueError,'CARRIED_BASELINE_INTENT_MISMATCH'):
                self.original_call(baseline='f'*40)
        self.intent['baseline']='f'*40
        with patch.object(self.a,'_release_history_json',return_value=self.intent),patch.object(self.a,'component_upload_proven'),patch.object(self.a,'_release_job_log',return_value='fixture'),patch.object(self.a,'release_operation_history',return_value=dict(self.intent,baseline='a'*40)):
            with self.assertRaisesRegex(ValueError,'COMPETING_ORIGINAL_INTENT'):
                self.original_call(baseline='f'*40)
        self.assertFalse(self.a._COMPLETION_BASELINE_PROVING.get())

    def test_carried_baseline_missing_expired_competing_or_mismatched_material_holds(self):
        for problem in ['missing package','expired intent','competing original intent']:
            with self.subTest(problem=problem), patch.object(self.a,'fast_track_completion_material',side_effect=ValueError(problem)):
                with self.assertRaisesRegex(ValueError,problem):self.original_call(baseline='f'*40)
            self.assertFalse(self.a._COMPLETION_BASELINE_PROVING.get())
        with patch.object(self.a,'fast_track_completion_material',return_value=self.material_value):
            with self.assertRaisesRegex(ValueError,'CARRIED_BASELINE_INTENT_MISMATCH'):
                self.original_call(baseline='f'*40)

    def test_carried_baseline_later_sibling_mismatch_is_not_partial_admission(self):
        def material(repo,original,key,token):
            return dict(intent=dict(self.intent,baseline='a'*40 if key=='website' else 'f'*40))
        with patch.object(self.a,'fast_track_completion_material',side_effect=material):
            with self.assertRaisesRegex(ValueError,'CARRIED_BASELINE_INTENT_MISMATCH'):
                self.original_call(baseline='f'*40,targets=['portal','website'])

    def test_carried_baseline_recursive_receipt_proof_fails_closed_and_resets(self):
        def recursive(repo,original,key,token):
            return self.a.fast_track_completion_original(repo,123,self.sha,token)
        with patch.object(self.a,'fast_track_completion_material',side_effect=recursive):
            with self.assertRaisesRegex(ValueError,'CARRIED_BASELINE_PROOF_CYCLE'):
                self.original_call(baseline='f'*40)
        self.assertFalse(self.a._COMPLETION_BASELINE_PROVING.get())
        with patch.object(self.a,'fast_track_completion_material',return_value=dict(intent=dict(baseline='f'*40))):
            self.assertEqual(self.original_call(baseline='f'*40)['baseRevision'],'f'*40)

    def test_noncarried_baseline_preserves_existing_material_admission_order(self):
        with patch.object(self.a,'fast_track_completion_material') as material:
            self.assertEqual(self.original_call()['baseRevision'],'a'*40)
        material.assert_not_called()

    def test_carried_baseline_still_requires_original_selector_admission_and_schema(self):
        for label in ['Select exact affected applications','Admit exact validated fast-track source',
                      'Reconcile production schema before publication']:
            real=self.a._completion_step
            def step(job,*args,**kwargs):
                if job['name']==label:raise ValueError('original prerequisite missing')
                return real(job,*args,**kwargs)
            with self.subTest(label=label),patch.object(self.a,'_completion_step',side_effect=step),patch.object(self.a,'fast_track_completion_material') as material:
                with self.assertRaisesRegex(ValueError,'original prerequisite missing'):
                    self.original_call(baseline='f'*40)
                material.assert_not_called()

    def test_supersession_keeps_its_separate_parent_baseline_contract(self):
        request=dict(releaseMode='approved-only',requestedBy='Founder',preserveLiveTargets=False,
            targets=['masterapp-portal'],cloudflareWebsiteRouting=False,protectSupersession={})
        proof=dict(expectedReleaseParent='a'*40,originalRevision='f'*40)
        with patch.object(self.a,'fast_track_protect_supersession',return_value=proof),patch.object(self.a,'fast_track_completion_material') as material:
            self.assertEqual(self.original_call(request=request)['baseRevision'],'a'*40)
            material.assert_not_called()

    def test_carried_baseline_original_run_change_after_material_still_holds(self):
        with patch.object(self.a,'fast_track_completion_material',return_value=dict(intent=dict(baseline='f'*40))),patch.object(self.a,'_completion_original_unchanged',side_effect=ValueError('original changed')):
            with self.assertRaisesRegex(ValueError,'original changed'):self.original_call(baseline='f'*40)


    def receipt(self):
        proof=dict(originalRun=123,originalRunAttempt=3,originalPackageJobId=456,originalPackageAttempt=1,intentArtifactId=10,intentArtifactDigest=self.ia['digest'],packageArtifactId=11,
            packageArtifactDigest=self.pa['digest'],originalJobId=456,observer=self.observer,
            provider=dict(deploymentId='new',receivedTime='2026-10-21T13:02:00Z',status=4,runtimeRevision=self.sha))
        return dict(self.intent,phase='success',producingRun=789,producingAttempt=1,deploymentIds=['new'],completionEvidence=proof)
    def validate(self,record,artifact=None):
        artifact=artifact or dict(workflow_run=dict(id=789),created_at='2026-10-21T14:01:00Z')
        job=dict(id=800,started_at='2026-10-21T14:00:00Z',completed_at='2026-10-21T14:02:00Z')
        job.update(status='completed',conclusion='success',steps=[dict(name=n,status='completed',conclusion='success',started_at=job['started_at'],completed_at=job['completed_at']) for n in ['Authenticate original operations and promoted completion authority','Reconcile original bytes and record only missing success receipts']])
        job.update(getattr(self,'observer_job_changes',{}))
        with patch.object(self.a,'fast_track_completion_observer',return_value=self.observer),patch.object(self.a,'fast_track_completion_original',return_value=self.original),patch.object(self.a,'fast_track_completion_material',return_value=self.material_value),patch.object(self.a.subprocess,'run',return_value=SimpleNamespace(returncode=0)),patch.object(self.a,'api_get',return_value=dict(jobs=[job],total_count=1)):
            return self.a.validate_fast_track_completion_record(self.repo,dict(id=789),artifact,record,'test-only')
    def test_receipt_preserves_original_intent_but_records_real_observer(self):
        record=self.receipt();self.assertEqual(self.validate(record),record)
        self.assertEqual(record['producingRun'],789);self.assertEqual(record['completionEvidence']['originalRun'],123)
    def test_receipt_wrong_observer_original_artifact_or_baseline_rejected(self):
        import copy
        for key,value in [('intentArtifactId',12),('originalJobId',457),('packageArtifactDigest','sha256:'+'f'*64)]:
            record=copy.deepcopy(self.receipt());record['completionEvidence'][key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):self.validate(record)
        for key,value in [('producingRun',123),('phase','intent'),('baseline','f'*40),('deploymentIds',['old'])]:
            record=copy.deepcopy(self.receipt());record[key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):self.validate(record)
    def test_receipt_provider_time_revision_or_status_rejected(self):
        import copy
        for key,value in [('receivedTime','2026-10-21T13:06:00Z'),('runtimeRevision',self.tool),('status',3),('deploymentId','other')]:
            record=copy.deepcopy(self.receipt());record['completionEvidence']['provider'][key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):self.validate(record)
    def test_receipt_outside_observer_job_rejected(self):
        with self.assertRaises(ValueError):self.validate(self.receipt(),dict(workflow_run=dict(id=789),created_at='2026-10-21T13:01:00Z'))
    def test_all_receipts_still_require_completed_aggregate_observer(self):
        original=dict(self.original,targets=['portal'])
        with patch.object(self.a,'fast_track_completion_original',return_value=original),patch.object(self.a,'fast_track_completion_material',return_value=self.material_value),patch.object(self.a,'release_operation_history',return_value=self.receipt()),patch.object(self.a,'fast_track_completion_summary',return_value=False) as summary:
            self.assertFalse(self.a.fast_track_completed_operation_receipts(self.repo,self.run,'test-only'))
            targets=summary.call_args.args[3]
            self.assertEqual(set(targets),{'portal'})
            self.assertEqual(targets['portal']['packageDigest'],self.intent['packageDigest'])
            summary.return_value=True
            self.assertTrue(self.a.fast_track_completed_operation_receipts(self.repo,self.run,'test-only'))

    def test_predecessor_requires_every_original_target_success(self):
        original=dict(self.original,targets=['portal','client']);calls=[]
        def success(*args,**kwargs):calls.append(args[3]);return self.receipt() if args[3]=='portal' else None
        with patch.object(self.a,'fast_track_completion_original',return_value=original),patch.object(self.a,'fast_track_completion_material',return_value=self.material_value),patch.object(self.a,'release_operation_history',side_effect=success):
            self.assertFalse(self.a.fast_track_completed_operation_receipts(self.repo,self.run,'test-only'))
        self.assertEqual(calls,['portal','client'])

    def test_v4_package_step_owns_artifact_not_whole_job(self):
        for field,value in [('started_at','2026-10-21T13:00:31Z'),('completed_at','2026-10-21T13:00:20Z'),('conclusion','failure')]:
            old=self.job['steps'][0][field];self.job['steps'][0][field]=value
            with self.subTest(field=field),self.assertRaises(ValueError):self.material()
            self.job['steps'][0][field]=old
    def test_v4_duplicate_original_steps_and_conflicting_copied_execution_refused(self):
        self.job['steps'].append(dict(self.job['steps'][0]))
        with self.assertRaises(ValueError):self.material()
        self.job['steps'].pop()
        copied=dict(self.job,id=999,run_attempt=2,runner_name='different')
        context=dict(run=self.run,attempts={1:[self.job],2:[copied]})
        with self.assertRaisesRegex(ValueError,'COMPETING_ARTIFACT_EXECUTIONS'):
            self.a._completion_original_artifact_owner(context,'portal',self.pa,'Preserve immutable application bytes before upload',('success',))
    def test_v4_copied_success_keeps_earliest_real_attempt(self):
        copied=dict(self.job,id=999,run_attempt=2)
        context=dict(run=self.run,attempts={1:[self.job],2:[copied]})
        attempt,job,_=self.a._completion_original_artifact_owner(context,'portal',self.pa,'Preserve immutable application bytes before upload',('success',))
        self.assertEqual((attempt,job['id']),(1,456))
    def test_v4_exact_upload_log_cannot_substitute_artifact_id_name_or_time(self):
        line='2026-10-21T13:00:30Z Artifact '+self.pa['name']+' has been successfully uploaded! Final size is 123 bytes. Artifact ID is 11'
        self.a.component_upload_proven(self.pa,self.job,'Preserve immutable application bytes before upload',line)
        for bad in ['',line+'\n'+line,line.replace('ID is 11','ID is 12'),line.replace(self.pa['name'],'different'),line.replace('13:00:30','13:01:30')]:
            with self.subTest(log=bad),self.assertRaises(ValueError):self.a.component_upload_proven(self.pa,self.job,'Preserve immutable application bytes before upload',bad)
    def test_v4_strict_inventory_rejects_missing_count_duplicate_boolean_or_nonrow(self):
        for payload in [dict(jobs=[]),dict(jobs=[dict(id=1)],total_count=2),dict(jobs=[dict(id=1),dict(id=1)],total_count=2),dict(jobs=[dict(id=True)],total_count=1),dict(jobs=[None],total_count=1)]:
            with self.subTest(payload=payload),self.assertRaises(ValueError):self.a._completion_inventory(payload,'jobs')
    def test_v4_original_run_change_during_proof_refused(self):
        for key,value in [('run_attempt',4),('status','in_progress'),('head_sha',self.tool),('conclusion','success')]:
            with patch.object(self.a,'api_get',return_value=dict(self.run,**{key:value})),self.subTest(key=key),self.assertRaises(ValueError):self.a._completion_original_unchanged(self.repo,self.run,'test-only')
    def test_v4_original_request_founder_and_no_preservation_hold_required(self):
        req=dict(releaseMode='approved-only',requestedBy='Founder',preserveLiveTargets=False,targets=['masterapp-portal'],cloudflareWebsiteRouting=False)
        for key,value in [('requestedBy','other'),('preserveLiveTargets',True),('trustedToolPromotion',{}),('cloudflareWebsiteRouting',True)]:
            with self.subTest(key=key),self.assertRaises(ValueError):self.original_call(request=dict(req,**{key:value}))
    def test_v4_original_receipt_requires_successful_exact_step_and_public_check(self):
        self.intent['target']='website';self.job['name']='Affected App Deploy — Website';self.job['conclusion']='success';self.job['steps'][1]['conclusion']='success'
        record=dict(self.intent,phase='success',deploymentIds=['new'])
        artifact=dict(self.ia,id=13,name='legend-release-operation-success-'+self.intent['operationId'])
        context=dict(self.original,artifacts=[artifact],attempts={1:[self.job]})
        with patch.object(self.a,'_release_history_json',return_value=record):
            with self.assertRaisesRegex(ValueError,'STEP_UNPROVEN'):self.a.validate_original_completion_success(self.repo,context,self.material_value,record,'test-only')
            self.job['steps'].append(dict(name='Verify public sitemap XML and robots contract',status='completed',conclusion='success'))
            self.assertEqual(self.a.validate_original_completion_success(self.repo,context,self.material_value,record,'test-only'),record)
            self.job['steps'][1]['conclusion']='failure'
            with self.assertRaises(ValueError):self.a.validate_original_completion_success(self.repo,context,self.material_value,record,'test-only')
    def test_v4_receipt_binds_original_terminal_attempt_and_package_owner(self):
        import copy
        for key,value in [('originalRunAttempt',2),('originalPackageJobId',999),('originalPackageAttempt',2)]:
            record=copy.deepcopy(self.receipt());record['completionEvidence'][key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):self.validate(record)
    def test_v4_observer_step_interval_is_narrower_than_job_interval(self):
        record=self.receipt()
        job=dict(id=800,status='completed',conclusion='success',started_at='2026-10-21T14:00:00Z',completed_at='2026-10-21T14:05:00Z',steps=[dict(name='Authenticate original operations and promoted completion authority',status='completed',conclusion='success'),dict(name='Reconcile original bytes and record only missing success receipts',status='completed',conclusion='success',started_at='2026-10-21T14:02:00Z',completed_at='2026-10-21T14:04:00Z')])
        artifact=dict(workflow_run=dict(id=789),created_at='2026-10-21T14:01:00Z')
        with patch.object(self.a,'fast_track_completion_observer',return_value=self.observer),patch.object(self.a.subprocess,'run',return_value=SimpleNamespace(returncode=0)),patch.object(self.a,'api_get',return_value=dict(jobs=[job],total_count=1)):
            with self.assertRaisesRegex(ValueError,'OWNER_UNRESOLVED'):self.a.validate_fast_track_completion_record(self.repo,dict(id=789),artifact,record,'test-only')
    def test_v4_changed_workflow_job_or_top_level_contract_refused(self):
        source=(ROOT.parent/'.github/workflows/deployment-diagnostics.yml').read_text()
        with patch.object(self.a,'_release_history_source',return_value=source):self.a._completion_workflow_source(self.repo,self.tool,'test-only')
        for changed in [source.replace('name: Complete existing fast-track deployment receipts','name: Unexpected owner'),source.replace('  contents: read','  contents: write',1),source.replace('cancel-in-progress: false','cancel-in-progress: true')]:
            with self.subTest(),patch.object(self.a,'_release_history_source',return_value=changed),self.assertRaises(ValueError):self.a._completion_workflow_source(self.repo,self.tool,'test-only')

    def test_v5_whole_workflow_rejects_quoted_duplicate_alias_and_unrelated_changes(self):
        source=(ROOT.parent/'.github/workflows/deployment-diagnostics.yml').read_text()
        variants=['"env":\n  BASH_ENV: scripts/unreviewed-observer.sh\n'+source,
            "'defaults':\n  run:\n    shell: bash -c 'unexpected'\n"+source,
            'env: &extra {BASH_ENV: scripts/unreviewed-observer.sh}\n'+source+'\nenv: *extra\n',
            source+'\npermissions: write-all\n',source+'\n# unrelated comment\n',source+'\ninvalid: [']
        with patch.object(self.a,'_release_history_source',return_value=source):self.a._completion_workflow_source(self.repo,self.tool,'test-only')
        for changed in variants:
            with self.subTest(),patch.object(self.a,'_release_history_source',return_value=changed),self.assertRaises(ValueError):self.a._completion_workflow_source(self.repo,self.tool,'test-only')
    def test_v5_publication_window_boundaries_and_malformed_intervals(self):
        import copy
        material=copy.deepcopy(self.material_value)
        step=material['originalPublicationStep'];step.update(started_at='2026-10-21T13:59:13Z',completed_at='2026-10-21T14:08:34Z')
        material['intentArtifact']['created_at']='2026-10-21T13:59:14Z'
        material['originalJob']['completed_at']='2026-10-21T14:08:37Z'
        for time,expected in [('13:59:13.999999',False),('13:59:14',True),('14:08:34',True),('14:08:34.999999',True),('14:08:35',False),('14:08:36',False)]:
            with self.subTest(time=time):self.assertEqual(self.a.completion_provider_in_original_publication(material,'2026-10-21T'+time+'Z'),expected)
        for field,value in [('started_at','2026-10-21T14:08:35Z'),('completed_at','bad'),('status','in_progress'),('name','cleanup')]:
            bad=copy.deepcopy(material);bad['originalPublicationStep'][field]=value
            with self.subTest(field=field):self.assertFalse(self.a.completion_provider_in_original_publication(bad,'2026-10-21T14:08:34Z'))
        for time in [None,{},'invalid']:
            self.assertFalse(self.a.completion_provider_in_original_publication(material,time))
    def test_v5_historical_receipt_rejects_actual_portal_cleanup_window(self):
        self.material_value['originalPublicationStep'].update(started_at='2026-10-21T13:59:13Z',completed_at='2026-10-21T14:08:34Z')
        self.material_value['intentArtifact']['created_at']='2026-10-21T13:59:14Z'
        self.material_value['originalJob']['completed_at']='2026-10-21T14:08:37Z'
        record=self.receipt();record['completionEvidence']['provider']['receivedTime']='2026-10-21T14:08:36Z'
        with self.assertRaisesRegex(ValueError,'PROVIDER_CORRELATION_UNPROVEN'):self.validate(record)
        record['completionEvidence']['provider']['receivedTime']='2026-10-21T14:08:34.999999Z'
        self.assertEqual(self.validate(record),record)

    def test_v6_fractional_end_before_exact_and_after_historical(self):
        import copy
        for end,before,exact,after in [('13:01:59.500000','13:01:59.499999','13:01:59.500000','13:02:00'),('13:02:00.000','13:01:59.999999','13:02:00','13:02:00.000001'),('13:01:59,500000','13:01:59.499999','13:01:59.500000','13:02:00')]:
            self.material_value['originalPublicationStep']['completed_at']='2026-10-21T'+end+'Z'
            for received,allowed in [(before,True),(exact,False),(after,False)]:
                record=copy.deepcopy(self.receipt());record['completionEvidence']['provider']['receivedTime']='2026-10-21T'+received+'Z'
                with self.subTest(end=end,received=received):
                    if allowed:self.assertEqual(self.validate(record),record)
                    else:
                        with self.assertRaisesRegex(ValueError,'PROVIDER_CORRELATION_UNPROVEN'):self.validate(record)

    def test_v4_failed_observer_receipt_is_preserved_but_unusable(self):
        self.observer_job_changes=dict(conclusion='failure')
        self.assertIs(self.validate(self.receipt()),False)
        self.observer_job_changes=dict(status='in_progress',conclusion=None)
        with self.assertRaises(ValueError):self.validate(self.receipt())

class CompletionScopedDiscoveryTests(unittest.TestCase):
    def setUp(self):
        import copy
        spec=importlib.util.spec_from_file_location('completion_discovery_tests',ROOT/'validation-resume.py')
        self.a=importlib.util.module_from_spec(spec);spec.loader.exec_module(self.a)
        self.repo='owner/repo';self.sha='a'*40;self.operation='b'*64
        self.original=dict(id=10,head_sha=self.sha,run_attempt=1,status='completed',conclusion='failure',
            event='push',path='.github/workflows/legend-fast-track-ui.yml',head_branch='Legend/fast-track-release',head_repository=dict(full_name=self.repo))
        self.current=copy.deepcopy(self.original)
        self.intent=dict(id=11,name='legend-release-operation-intent-'+self.operation,expired=False,
            digest='sha256:'+'c'*64,workflow_run=dict(id=10,head_sha=self.sha))
        self.originals=dict(total_count=1,artifacts=[self.intent])
        self.run=dict(id=20,head_sha='d'*40,run_attempt=1,event='workflow_dispatch',head_repository=dict(full_name=self.repo),head_branch='Legend/fast-track-release',path='.github/workflows/deployment-diagnostics.yml',status='completed',conclusion='failure')
        self.runs=dict(total_count=1,workflow_runs=[self.run])
        self.success=dict(id=21,name='legend-release-operation-success-'+self.operation,expired=False,digest='sha256:'+'e'*64,workflow_run=dict(id=20,head_sha=self.run['head_sha']))
        self.artifacts={20:dict(total_count=1,artifacts=[self.success])}
        self.records={21:dict(applicationRevision=self.sha)}
        self.recovery=dict(total_count=0,artifacts=[])
        self.calls=[];self.reads=[];self.closes=[];self.named_overrides={}
    def api(self,repo,path,token):
        self.assertEqual(repo,self.repo);self.calls.append(path)
        if path.startswith('actions/artifacts?name='):
            from urllib.parse import parse_qs
            name=parse_qs(path.split('?',1)[1])['name'][0]
            if name==self.a.fast_track_recovery_summary_name(self.original['id'],self.sha):return self.recovery
            if name in self.named_overrides:return self.named_overrides[name]
            rows=[row for payload in self.artifacts.values() for row in payload['artifacts'] if row.get('name')==name]
            return dict(total_count=len(rows),artifacts=rows)
        if path==f"actions/runs/{self.original['id']}":return self.original
        if path==f"actions/runs/{self.original['id']}/artifacts?per_page=100":return self.originals
        if path.startswith('actions/workflows/'):return self.runs
        for run in self.runs['workflow_runs']:
            if path==f"actions/runs/{run['id']}":return run
        if path.endswith('/jobs?filter=latest&per_page=100'):return dict(total_count=0,jobs=[])
        if path.endswith('/artifacts?per_page=100'):return self.artifacts[int(path.split('/')[2])]
        raise AssertionError(path)
    def read(self,repo,run,artifact,filename,**kwargs):
        self.assertEqual((filename,kwargs),('operation.json',dict(verify_digest=True)))
        self.reads.append(artifact['id']);return self.records[artifact['id']]
    def close(self,repo,path,token):
        self.closes.append(path);return self.current
    def check(self,*,scoped=True):
        with patch.object(self.a,'api_get',side_effect=self.api),patch.object(self.a,'_api_get_uncached',side_effect=self.close),patch.object(self.a,'_release_history_json',side_effect=self.read):
            return self.a.fast_track_completion_present(self.repo,self.sha,'test-only',**({'original_run':self.original['id']} if scoped else {}))
    def test_scoped_partial_failed_and_live_receipts_remain_discoverable(self):
        for status,conclusion in [('completed','failure'),('completed','cancelled'),('in_progress',None),('completed','success')]:
            self.run.update(status=status,conclusion=conclusion)
            with self.subTest(status=status,conclusion=conclusion):self.assertTrue(self.check())
        self.assertEqual(self.reads,[21]*4);self.assertEqual(len(self.closes),4)
    def test_only_exact_original_summary_name_is_a_candidate(self):
        self.originals=dict(total_count=0,artifacts=[])
        self.success['name']=self.a.fast_track_completion_summary_name(99,self.sha)
        self.assertFalse(self.check());self.assertEqual(self.reads,[])
        self.success['name']=self.a.fast_track_completion_summary_name(10,self.sha)
        self.assertTrue(self.check());self.assertEqual(self.reads,[21])
        self.records[21]['applicationRevision']='d'*40
        self.assertFalse(self.check())
    def test_actual_cbc_empty_intent_scope_queries_exact_summary_not_unrelated_runs(self):
        import copy
        self.sha='cbc94de2f33baeecce331f0184522686b658c58f'
        self.original.update(id=40506653784,head_sha=self.sha);self.current=copy.deepcopy(self.original)
        self.originals=dict(total_count=1,artifacts=[dict(id=12736991325,name='fast-track-admission-40506653784-1',workflow_run=dict(id=40506653784,head_sha=self.sha))])
        current=dict(self.run,id=40563368913);prior=dict(self.run,id=40463688877)
        self.runs=dict(total_count=2,workflow_runs=[current,prior])
        self.artifacts={current['id']:dict(total_count=1,artifacts=[dict(id=12760978926,name='legend-release-operation-success-85ba96e6a5dca05d83949e0c1854783518177cd99928996f82ddade432cc4704')]),
            prior['id']:dict(total_count=1,artifacts=[dict(id=12715727916,name='legend-release-completion-91ce77b99c4d692ba3bf963e73b8c3b1014d4b6834971c2e3b40fe6afec45537')])}
        self.assertFalse(self.check());self.assertEqual(self.reads,[])
        name=self.a.fast_track_completion_summary_name(self.original['id'],self.sha)
        self.assertIn('actions/artifacts?name='+name+'&per_page=100',self.calls)
        for run in [current,prior]:self.assertNotIn(f"actions/runs/{run['id']}/artifacts?per_page=100",self.calls)
        self.assertFalse(any(path.startswith('actions/workflows/') for path in self.calls))
        self.assertEqual(self.closes,['actions/runs/40506653784'])
    def test_actual_a459_summary38_remains_discoverable(self):
        import copy
        self.sha='a459eafddbbbda3b599209d28f82135fbc108605'
        self.original.update(id=40444074972,head_sha=self.sha);self.current=copy.deepcopy(self.original)
        self.intent['workflow_run']=dict(id=self.original['id'],head_sha=self.sha)
        name=self.a.fast_track_completion_summary_name(self.original['id'],self.sha)
        self.assertEqual(name,'legend-release-completion-91ce77b99c4d692ba3bf963e73b8c3b1014d4b6834971c2e3b40fe6afec45537')
        self.success.update(id=12715727916,name=name);self.records={12715727916:dict(applicationRevision=self.sha)}
        self.assertTrue(self.check());self.assertEqual(self.reads,[12715727916])
    def test_unscoped_legacy_discovery_keeps_original_broad_behavior(self):
        self.success['name']='legend-release-operation-success-'+'f'*64
        self.assertFalse(self.check());self.assertEqual(self.reads,[])
        self.calls=[];self.assertTrue(self.check(scoped=False));self.assertEqual(self.reads,[21])
        self.assertFalse(any(path.startswith('actions/runs/10') for path in self.calls))
        self.assertEqual(len(self.closes),1)
    def test_retained_recovery_positive_path_is_unchanged(self):
        name=self.a.fast_track_recovery_summary_name(10,self.sha)
        self.recovery=dict(total_count=1,artifacts=[dict(id=30,name=name,expired=False)])
        self.assertTrue(self.check());self.assertEqual(len(self.calls),1)
        self.assertEqual((self.reads,self.closes),([],[]))
    def test_incomplete_duplicate_or_malformed_original_inventory_fails_closed(self):
        import copy
        good=copy.deepcopy(self.originals)
        cases=[{},dict(total_count=True,artifacts=[]),dict(total_count=2,artifacts=[self.intent]),dict(total_count=2,artifacts=[self.intent,self.intent]),dict(total_count=1,artifacts=[None])]
        for payload in cases:
            self.originals=payload
            with self.subTest(payload=payload),self.assertRaises(ValueError):self.check()
        self.originals=good;self.assertEqual(self.reads,[])
    def test_foreign_expired_duplicate_or_malformed_original_intent_fails_closed(self):
        import copy
        good=copy.deepcopy(self.intent)
        cases=[dict(name=None),dict(name='legend-release-operation-intent-invalid'),dict(expired=True),dict(expired=None),dict(digest='bad'),
            dict(workflow_run=dict(id=99,head_sha=self.sha)),dict(workflow_run=dict(id=10,head_sha='e'*40)),dict(workflow_run=None)]
        for changes in cases:
            self.intent.clear();self.intent.update(copy.deepcopy(good));self.intent.update(changes)
            with self.subTest(changes=changes),self.assertRaises(ValueError):self.check()
        self.intent.clear();self.intent.update(good)
        self.originals=dict(total_count=2,artifacts=[self.intent,dict(self.intent,id=12)])
        with self.assertRaises(ValueError):self.check()
        self.assertEqual(self.reads,[])
    def test_original_producer_must_bind_terminal_revision_and_attempt(self):
        import copy
        good=copy.deepcopy(self.original)
        cases=[dict(head_sha='e'*40),dict(status='in_progress'),dict(conclusion=None),dict(run_attempt=None),dict(run_attempt=True),dict(run_attempt=0),dict(run_attempt='1'),dict(event='workflow_dispatch'),dict(head_branch='other'),dict(path='other'),dict(head_repository=dict(full_name='foreign/repo'))]
        for changes in cases:
            self.original.clear();self.original.update(copy.deepcopy(good));self.original.update(changes)
            with self.subTest(changes=changes),self.assertRaises(ValueError):self.check()
        self.assertEqual(self.reads,[])
    def test_each_named_inventory_must_be_complete_even_for_empty_scope(self):
        self.originals=dict(total_count=0,artifacts=[])
        name=self.a.fast_track_completion_summary_name(10,self.sha)
        for payload in [dict(total_count=1,artifacts=[]),dict(total_count=True,artifacts=[]),
                        dict(total_count=101,artifacts=[self.success]*100),
                        dict(total_count=2,artifacts=[self.success,self.success]),dict(total_count=1,artifacts=[None])]:
            self.named_overrides[name]=payload
            with self.subTest(payload=payload),self.assertRaises(ValueError):self.check()
        self.assertEqual(self.reads,[])
    def test_unscoped_inventory_completeness_contract_is_unchanged(self):
        self.runs['total_count']=2
        with self.assertRaises(ValueError):self.check(scoped=False)
        self.runs['total_count']=1;self.artifacts[20]['total_count']=2
        with self.assertRaises(ValueError):self.check(scoped=False)
        self.assertEqual(self.reads,[])
    def test_closing_identity_is_uncached_and_rejects_drift_for_both_results(self):
        import copy
        good=copy.deepcopy(self.current)
        changes=[dict(id=99),dict(head_sha='e'*40),dict(run_attempt=2),dict(run_attempt=True),dict(status='in_progress'),dict(conclusion='success'),dict(event='workflow_dispatch'),dict(path='other'),dict(head_branch='other'),dict(head_repository=dict(full_name='foreign/repo'))]
        for matching in [True,False]:
            self.success['name']='legend-release-operation-success-'+(self.operation if matching else 'f'*64)
            for change in changes:
                self.current=copy.deepcopy(good);self.current.update(change)
                with self.subTest(matching=matching,change=change),self.assertRaisesRegex(ValueError,'DISCOVERY_ORIGINAL_CHANGED'):self.check()
        self.assertEqual(len(self.closes),len(changes)*2)
    def test_closing_transport_failure_and_late_deadline_fail_closed(self):
        for error in [RuntimeError('unavailable'),self.a.EvidenceLookupUnavailable('expired')]:
            with patch.object(self,'close',side_effect=error),self.assertRaises(type(error)):self.check()
        with patch.object(self.a,'evidence_remaining',side_effect=[30,self.a.EvidenceLookupUnavailable('late')]),self.assertRaises(self.a.EvidenceLookupUnavailable):self.check()
    def test_scope_discovery_never_replaces_effective_baseline_authentication(self):
        import contextlib
        summary=CompletionSummaryTests();summary.setUp()
        for forged in [True,False]:
            summary.record['originalRun']=99 if forged else 1
            summary.producer['conclusion']='success' if forged else 'failure'
            with contextlib.ExitStack() as stack:
                stack.enter_context(patch.object(self.a,'api_get',side_effect=self.api))
                stack.enter_context(patch.object(self.a,'_api_get_uncached',side_effect=self.close))
                stack.enter_context(patch.object(self.a,'_release_history_json',side_effect=self.read))
                stack.enter_context(patch.object(self.a,'fast_track_historical_producers',return_value=[self.original]))
                stack.enter_context(patch.object(self.a.subprocess,'run',return_value=SimpleNamespace(returncode=0)))
                proof=stack.enter_context(patch.object(self.a,'fast_track_completed_operation_receipts',side_effect=lambda *args:summary.check()))
                for name in ['fast_track_completed_apps_pending_routing','attested_fast_track_manual_dns_proxy_completion','attested_fast_track_brand_preupload','fast_track_completed_sql_unpublished','fast_track_unpublished_apps_with_migration_intent']:
                    stack.enter_context(patch.object(self.a,name,return_value=False))
                with self.assertRaises(ValueError if forged else RuntimeError):
                    self.a.fast_track_effective_baseline(self.sha,'d'*40,repository=self.repo,token='test-only')
                proof.assert_called_once()

    def test_every_relevant_name_must_be_checked_before_absence(self):
        self.artifacts[20]['artifacts']=[]
        self.assertFalse(self.check())
        names=[self.a.fast_track_completion_summary_name(10,self.sha),'legend-release-operation-success-'+self.operation]
        for name in names:self.assertIn('actions/artifacts?name='+name+'&per_page=100',self.calls)
        self.assertEqual(self.reads,[]);self.assertEqual(self.closes,['actions/runs/10'])
    def test_named_inventory_failure_is_not_broad_fallback(self):
        original=self.api
        def api(repo,path,token):
            if path=='actions/artifacts?name='+self.a.fast_track_completion_summary_name(10,self.sha)+'&per_page=100':
                raise self.a.EvidenceLookupUnavailable('unavailable')
            return original(repo,path,token)
        with patch.object(self,'api',side_effect=api),self.assertRaises(self.a.EvidenceLookupUnavailable):self.check()
        self.assertFalse(any(path.startswith('actions/workflows/') for path in self.calls))
        self.assertEqual((self.reads,self.closes),([],[]))
    def test_named_artifact_identity_expiry_and_digest_fail_closed(self):
        import copy
        good=copy.deepcopy(self.success);name=good['name']
        for change in [dict(name='foreign'),dict(expired=True),dict(expired=None),dict(digest=''),
                       dict(workflow_run=None),dict(workflow_run=dict(id=True,head_sha='d'*40)),
                       dict(workflow_run=dict(id=20,head_sha='invalid'))]:
            self.named_overrides[name]=dict(total_count=1,artifacts=[dict(good,**change)])
            with self.subTest(change=change),self.assertRaisesRegex(ValueError,'DISCOVERY_ARTIFACT_UNPROVEN'):self.check()
        self.assertEqual(self.reads,[])
    def test_named_producer_identity_is_authenticated_not_inferred_from_name(self):
        import copy
        good=copy.deepcopy(self.run)
        for change in [dict(id=True),dict(id=99),dict(head_sha='f'*40),dict(head_repository=dict(full_name='foreign/repo'))]:
            self.run.clear();self.run.update(copy.deepcopy(good));self.run.update(change)
            original=self.api
            def api(repo,path,token):
                if path=='actions/runs/20':return self.run
                return original(repo,path,token)
            with self.subTest(change=change),patch.object(self,'api',side_effect=api),self.assertRaisesRegex(ValueError,'DISCOVERY_PRODUCER_UNPROVEN'):self.check()
        self.assertEqual(self.reads,[])
    def test_same_name_outside_exact_diagnostics_scope_is_not_completion(self):
        for key,value in [('event','push'),('path','.github/workflows/other.yml'),('head_branch','other')]:
            old=self.run[key];self.run[key]=value
            with self.subTest(key=key):self.assertFalse(self.check())
            self.run[key]=old
        self.assertEqual(self.reads,[]);self.assertEqual(len(self.closes),3)
    def test_body_revision_and_digest_reader_remain_required(self):
        self.records[21]['applicationRevision']='f'*40
        self.assertFalse(self.check());self.assertEqual(self.reads,[21])
        with patch.object(self,'read',side_effect=ValueError('digest changed')),self.assertRaisesRegex(ValueError,'digest changed'):self.check()
    def test_positive_summary_still_reads_every_matching_operation_body(self):
        name=self.a.fast_track_completion_summary_name(10,self.sha)
        summary=dict(self.success,id=22,name=name)
        self.artifacts[20]=dict(total_count=2,artifacts=[self.success,summary]);self.records[22]=dict(applicationRevision=self.sha)
        self.assertTrue(self.check());self.assertEqual(self.reads,[22,21])
        self.assertIn('actions/artifacts?name='+self.success['name']+'&per_page=100',self.calls)
        self.records[22]['applicationRevision']='f'*40;self.reads=[]
        self.assertTrue(self.check());self.assertEqual(self.reads,[22,21])
    def test_unrelated_run_count_does_not_expand_scoped_metadata_reads(self):
        self.originals=dict(total_count=0,artifacts=[])
        self.artifacts={index:dict(total_count=1,artifacts=[dict(id=index,name='unrelated')]) for index in range(100,200)}
        self.assertFalse(self.check())
        self.assertEqual(len(self.calls),4) # recovery summary, original, original inventory, exact completion summary
        self.assertEqual(len(self.closes),1);self.assertEqual(self.reads,[])


    def test_positive_cannot_hide_malformed_matching_body_before_or_after_summary(self):
        name=self.a.fast_track_completion_summary_name(10,self.sha)
        summary=dict(self.success,id=22,name=name)
        self.artifacts[20]=dict(total_count=2,artifacts=[self.success,summary])
        self.records[22]=dict(applicationRevision=self.sha)
        original=self.read
        for broken in [21,22]:
            self.reads=[]
            def read(repo,run,artifact,filename,**kwargs):
                if artifact['id']==broken:raise ValueError('malformed matching body')
                return original(repo,run,artifact,filename,**kwargs)
            with self.subTest(broken=broken),patch.object(self,'read',side_effect=read),self.assertRaisesRegex(ValueError,'malformed matching body'):self.check()
        self.assertEqual(self.closes,[])
    def test_positive_cannot_hide_later_matching_artifact_or_query_error(self):
        name=self.a.fast_track_completion_summary_name(10,self.sha)
        summary=dict(self.success,id=22,name=name)
        self.artifacts[20]=dict(total_count=2,artifacts=[self.success,summary]);self.records[22]=dict(applicationRevision=self.sha)
        self.success['expired']=True
        with self.assertRaisesRegex(ValueError,'DISCOVERY_ARTIFACT_UNPROVEN'):self.check()
        self.success['expired']=False
        original=self.api
        def api(repo,path,token):
            if path=='actions/artifacts?name='+self.success['name']+'&per_page=100':raise self.a.EvidenceLookupUnavailable('late query unavailable')
            return original(repo,path,token)
        with patch.object(self,'api',side_effect=api),self.assertRaises(self.a.EvidenceLookupUnavailable):self.check()
        self.assertEqual(self.closes,[])
    def test_positive_still_reads_multiple_matching_bodies_and_does_not_accept_conflicts(self):
        name=self.a.fast_track_completion_summary_name(10,self.sha)
        summary=dict(self.success,id=22,name=name);conflict=dict(self.success,id=23)
        self.artifacts[20]=dict(total_count=3,artifacts=[self.success,summary,conflict])
        self.records[22]=dict(applicationRevision=self.sha)
        self.records[23]=dict(applicationRevision='f'*40)
        self.assertTrue(self.check());self.assertEqual(self.reads,[22,21,23])
        # Discovery accumulates presence only; the existing caller must still
        # execute the canonical rejecting receipt proof for conflicting evidence.
        with patch.object(self.a,'fast_track_historical_producers',return_value=[self.original]), \
             patch.object(self.a.subprocess,'run',return_value=SimpleNamespace(returncode=0)), \
             patch.object(self.a,'api_get',side_effect=self.api), \
             patch.object(self.a,'_api_get_uncached',side_effect=self.close), \
             patch.object(self.a,'_release_history_json',side_effect=self.read), \
             patch.object(self.a,'fast_track_completed_operation_receipts',side_effect=ValueError('Competing completion success evidence')) as proof:
            with self.assertRaisesRegex(ValueError,'Competing completion success evidence'):
                self.a.fast_track_effective_baseline(self.sha,'d'*40,repository=self.repo,token='test-only')
            proof.assert_called_once()


class CompletionSummaryTests(unittest.TestCase):
    def setUp(self):
        spec=importlib.util.spec_from_file_location('completion_summary_tests',ROOT/'validation-resume.py')
        self.a=importlib.util.module_from_spec(spec);spec.loader.exec_module(self.a)
        self.repo='owner/repo';self.sha='a'*40;self.tool='b'*40
        self.original=dict(id=1,head_sha=self.sha)
        self.observer=dict(runId=2,runAttempt=1,jobId=20,toolRevision=self.tool,promotionRunId=3)
        self.producer=dict(id=2,status='completed',conclusion='success')
        self.targets={k:dict(operationId='c'*64,packageDigest='d'*64,deploymentIds=['new'],intentArtifactId=10,packageArtifactId=11) for k in ['portal','website']}
        self.name=self.a.fast_track_completion_summary_name(1,self.sha)
        self.artifact=dict(id=30,name=self.name,expired=False,digest='sha256:'+'e'*64,workflow_run=dict(id=2),created_at='2026-10-21T13:05:00Z')
        self.record=dict(schemaVersion=1,phase='success',originalRun=1,applicationRevision=self.sha,producingRun=2,producingAttempt=1,observer=self.observer,targets=self.targets)
        self.job=dict(id=20,status='completed',conclusion='success',started_at='2026-10-21T13:00:00Z',completed_at='2026-10-21T13:06:00Z',steps=[dict(name='Reconcile original bytes and record only missing success receipts',status='completed',conclusion='success',started_at='2026-10-21T13:01:00Z',completed_at='2026-10-21T13:06:00Z')])
    def check(self,artifacts=None):
        artifacts=[self.artifact] if artifacts is None else artifacts
        def get(r,p,t):
            if p.startswith('actions/artifacts?'):return dict(artifacts=artifacts,total_count=len(artifacts))
            if p.endswith('/jobs?per_page=100'):return dict(jobs=[self.job],total_count=1)
            return self.producer
        with patch.object(self.a,'api_get',side_effect=get),patch.object(self.a,'fast_track_completion_observer',return_value=self.observer),patch.object(self.a,'_release_history_json',return_value=self.record) as read,patch.object(self.a.subprocess,'run',return_value=SimpleNamespace(returncode=0)):
            result=self.a.fast_track_completion_summary(self.repo,self.original,'test-only',self.targets)
            if artifacts:read.assert_called_with(self.repo,2,self.artifact,'operation.json',verify_digest=True)
            return result
    def test_all_targets_only_after_terminal_success(self):
        self.assertTrue(self.check())
        for status,conclusion in [('in_progress',None),('completed','failure'),('completed','cancelled')]:
            self.producer.update(status=status,conclusion=conclusion);self.assertFalse(self.check())
    def test_missing_summary_never_clears_predecessor(self):self.assertFalse(self.check([]))
    def test_missing_extra_or_changed_target_rejected(self):
        import copy
        for mutation in [lambda x:x.pop('website'),lambda x:x.update(client=x['portal']),lambda x:x['portal'].update(packageDigest='f'*64)]:
            targets=copy.deepcopy(self.targets);mutation(targets);self.record['targets']=targets
            with self.assertRaises(ValueError):self.check()
    def test_wrong_observer_original_or_phase_rejected(self):
        for key,value in [('originalRun',99),('applicationRevision',self.tool),('phase','intent'),('producingRun',99),('observer',dict(self.observer,jobId=99))]:
            old=self.record[key];self.record[key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):self.check()
            self.record[key]=old
    def test_duplicate_expired_or_digestless_summary_rejected(self):
        with self.assertRaises(ValueError):self.check([self.artifact,self.artifact])
        for key,value in [('expired',True),('digest',''),('name','wrong')]:
            old=self.artifact[key];self.artifact[key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):self.check()
            self.artifact[key]=old
    def test_successful_run_requires_exact_completed_completion_step(self):
        for steps in [[],self.job['steps']*2,[dict(name=self.job['steps'][0]['name'],conclusion='failure')]]:
            self.job['steps']=steps;self.assertFalse(self.check())
    def test_summary_outside_job_cannot_clear_predecessor(self):
        self.artifact['created_at']='2026-10-21T13:07:00Z';self.assertFalse(self.check())

class CompletionObserverTests(unittest.TestCase):
    def setUp(self):
        spec=importlib.util.spec_from_file_location('completion_observer_tests',ROOT/'validation-resume.py')
        self.a=importlib.util.module_from_spec(spec);spec.loader.exec_module(self.a)
        self.repo='owner/repo';self.sha='a'*40
        self.run=dict(id=2,run_attempt=1,head_sha=self.sha,status='in_progress',event='workflow_dispatch',head_repository=dict(full_name=self.repo),head_branch='Legend/fast-track-release',path='.github/workflows/deployment-diagnostics.yml')
        self.job=dict(id=20,run_id=2,run_attempt=1,head_sha=self.sha,name=self.a.FAST_TRACK_COMPLETION_JOB,status='in_progress',runner_name='runner')
        self.promotion=dict(self.run,id=3,event='push',path='.github/workflows/legend-fast-track-ui.yml',status='completed',conclusion='success')
        self.promotions=[self.promotion];self.jobs=[self.job];self.head=self.sha;self.promoted='b'*40
    def check(self):
        def get(r,p,t):
            if p.startswith('actions/runs?'):return dict(workflow_runs=self.promotions,total_count=len(self.promotions))
            if p.startswith('git/'):return dict(object=dict(sha=self.head))
            return dict(jobs=self.jobs,total_count=len(self.jobs))
        with patch.object(self.a,'_release_history_source',return_value=(ROOT.parent/'.github/workflows/deployment-diagnostics.yml').read_text()),patch.dict(os.environ,{'RUNNER_NAME':'runner'}),patch.object(self.a,'api_get',side_effect=get),patch.object(self.a,'fast_track_promotion_parent',return_value=self.promoted):
            return self.a.fast_track_completion_observer(self.repo,self.run,'test-only',live=True,attempt=1)
    def test_exact_promoted_observer(self):self.assertEqual(self.check()['jobId'],20)
    def test_wrong_event_ref_repository_workflow_rejected(self):
        for key,value in [('event','push'),('head_branch','other'),('head_repository',dict(full_name='other/repo')),('path','.github/workflows/other.yml'),('status','completed')]:
            old=self.run[key];self.run[key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):self.check()
            self.run[key]=old
    def test_missing_duplicate_or_unpromoted_tool_rejected(self):
        # Complete branch inventory may contain no matching canonical producer.
        unrelated=dict(self.promotion,id=4,path='.github/workflows/unrelated.yml')
        for values in [[unrelated],[self.promotion,dict(self.promotion,id=4)]]:
            self.promotions=values
            with self.assertRaises(ValueError):self.check()
        self.promotions=[self.promotion];self.promoted=None
        with self.assertRaises(ValueError):self.check()
    def test_duplicate_inventory_id_remains_invalid(self):
        self.promotions=[self.promotion,self.promotion]
        with self.assertRaisesRegex(RuntimeError,'FAST_TRACK_HISTORY_PRODUCER_UNPROVEN'):self.check()

    def test_ambiguous_changed_or_wrong_runner_owner_rejected(self):
        self.jobs=[self.job,self.job]
        with self.assertRaises(ValueError):self.check()
        self.jobs=[self.job]
        for key,value in [('run_id',99),('run_attempt',2),('head_sha','b'*40),('runner_name','other'),('status','completed')]:
            old=self.job[key];self.job[key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):self.check()
            self.job[key]=old
    def test_wrong_promotion_source_revision_rejected(self):
        self.promotion['head_sha']='b'*40
        with self.assertRaises(ValueError):self.check()
    def test_tool_head_change_rejected(self):
        self.head='b'*40
        with self.assertRaises(ValueError):self.check()

    def execute(self,*,event=None,dirty=False,late=False,original=123,revision=None):
        self.now=0
        event=event if event is not None else dict(inputs=dict(scope='complete_fast_track',original_run='123',application_revision='b'*40))
        with tempfile.TemporaryDirectory() as temp:
            path=Path(temp)/'event.json';path.write_text(json.dumps(event))
            env=dict(GITHUB_ACTIONS='true',GITHUB_EVENT_NAME='workflow_dispatch',GITHUB_REF='refs/heads/Legend/fast-track-release',GITHUB_JOB='complete-fast-track',GITHUB_REPOSITORY=self.repo,GITHUB_TOKEN='test-only',RUNNER_NAME='runner',GITHUB_RUN_ID='2',GITHUB_RUN_ATTEMPT='1',GITHUB_SHA=self.sha,GITHUB_WORKFLOW_REF=self.repo+'/.github/workflows/deployment-diagnostics.yml@refs/heads/Legend/fast-track-release',GITHUB_EVENT_PATH=str(path))
            timeline=list(getattr(self,'inventory_timeline',[self.jobs]))
            def api(r,p,t):
                if p=='actions/runs/2':return self.run
                if p.startswith('actions/runs?'):return dict(workflow_runs=self.promotions,total_count=len(self.promotions))
                if p.startswith('git/'):
                    if late:self.now=31
                    return dict(object=dict(sha=self.head))
                rows=timeline.pop(0) if len(timeline)>1 else timeline[0]
                return dict(jobs=rows,total_count=len(rows))
            def sleep(seconds):self.now+=seconds
            # Keep the real nested reader on the same injected clock domain.
            historical=self.a.fast_track_historical_producers
            def history(r,revision,t):return historical(r,revision,t,clock=lambda:self.now)
            with patch.object(self.a,'fast_track_historical_producers',side_effect=history),patch.dict(os.environ,env,clear=True),patch.object(self.a,'api_get',side_effect=api),patch.object(self.a,'_api_get_uncached',side_effect=api),patch.object(self.a,'_release_history_source',return_value=(ROOT.parent/'.github/workflows/deployment-diagnostics.yml').read_text()),patch.object(self.a,'fast_track_promotion_parent',return_value=self.promoted),patch.object(self.a.subprocess,'check_output',return_value=self.sha),patch.object(self.a.subprocess,'run',return_value=SimpleNamespace(returncode=int(dirty))):
                return self.a.assert_fast_track_completion_execution(original,revision or 'b'*40,clock=lambda:self.now,sleep=sleep)
    def test_v4_observer_waits_for_its_exact_runner_job(self):
        self.run['status']='queued';self.inventory_timeline=[[],[dict(self.job,status='queued',runner_name='')],[self.job]]
        self.assertEqual(self.execute()['jobId'],20);self.assertEqual(self.now,10)
    def test_v4_observer_pending_exhaustion_never_authenticates(self):
        self.jobs=[]
        with self.assertRaises((ValueError,self.a.EvidenceLookupUnavailable)):self.execute()
        self.assertEqual(self.now,30)
    def test_v4_final_head_read_after_deadline_rejected(self):
        with self.assertRaises(self.a.EvidenceLookupUnavailable):self.execute(late=True)
    def test_v4_observer_respects_shorter_parent_deadline(self):
        self.now=0;self.jobs=[]
        with self.a.evidence_lookup_budget(3,clock=lambda:self.now):
            with self.assertRaises(self.a.EvidenceLookupUnavailable):self.execute()
        self.assertEqual(self.now,3)
    def test_v4_dispatch_original_revision_scope_and_target_are_bound(self):
        inputs=dict(scope='complete_fast_track',original_run='123',application_revision='b'*40)
        for key,value in [('scope','deployment'),('original_run','124'),('application_revision','c'*40),('target','portal'),('deployment_id','new')]:
            with self.subTest(key=key),self.assertRaisesRegex(ValueError,'DISPATCH_UNPROVEN'):self.execute(event=dict(inputs=dict(inputs,**{key:value})))
    def test_v4_dirty_checked_source_never_authenticates(self):
        with self.assertRaisesRegex(ValueError,'CHECKED_SOURCE_UNPROVEN'):self.execute(dirty=True)

class CompletionInheritedPromotionTests(unittest.TestCase):
    """Real synthetic Git objects; no historical objects or provider calls."""
    def setUp(self):
        import contextlib
        spec=importlib.util.spec_from_file_location('completion_inheritance_tests',ROOT/'validation-resume.py')
        self.a=importlib.util.module_from_spec(spec);spec.loader.exec_module(self.a)
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.directory=Path(self.temp.name);self.real_run=subprocess.run
        self.git('init','-q');self.git('config','user.name','Completion fixture')
        self.git('config','user.email','fixture@example.invalid')
        self.prior=dict(requestedBy='Founder',authorization='fixture promotion',requestId='promotion',
                        releaseMode='validate-only',targets=[],cloudflareWebsiteRouting=False,
                        preserveLiveTargets=False,trustedToolPromotion=dict(schemaVersion=1))
        self.request=dict(requestedBy='Founder',authorization='fixture application',requestId='application',
                          releaseMode='approved-only',targets=['masterapp-portal'],
                          cloudflareWebsiteRouting=False,preserveLiveTargets=False)
        for path in ['scripts/validation-resume.py','.github/workflows/deployment-diagnostics.yml',
                     'scripts/release-artifacts/package-lock.json','.config/tool.json','App/source.cs']:
            self.write(path,'fixture\n')
        self.write(self.a.RELEASE_REQUEST_PATH,json.dumps(self.prior));self.git('add','.')
        self.git('commit','-qm','synthetic promoted parent');self.parent=self.git('rev-parse','HEAD').strip()
        self.make_child()
        self.repo='owner/repo';self.calls=[];self.raw_reads=[];self.head=self.revision
        self.source_error=None;self.promotion_error=None;self.git_error=None;self.raw_override=None
        self.fresh_mutation=None;self.commit_mutation=None;self.admission_mutation=None
        self.history_mutation=None;self.direct=False
        self.workflow=(ROOT.parent/'.github/workflows/deployment-diagnostics.yml').read_text()
        self.policy=self.a._fast_track_policy()
        self.stack=contextlib.ExitStack();self.addCleanup(self.stack.close)
        self.refresh_rows()

    def git(self,*args):
        return self.real_run(['git',*args],cwd=self.directory,text=True,stdout=subprocess.PIPE,
                             stderr=subprocess.PIPE,check=True).stdout

    def write(self,path,value):
        p=self.directory/path;p.parent.mkdir(parents=True,exist_ok=True);p.write_text(value)

    def make_child(self,stage=True):
        self.write(self.a.RELEASE_REQUEST_PATH,json.dumps(self.request))
        if stage:self.git('add','-A')
        self.git('commit','-qm','synthetic reviewed request')
        self.candidate=self.git('rev-parse','HEAD').strip()
        tree=self.git('rev-parse','HEAD^{tree}').strip()
        self.revision=self.git('commit-tree',tree,'-p',self.parent,'-p',self.candidate,
                               '-m','synthetic protected merge').strip()

    def refresh_rows(self):
        self.producer=dict(id=10,run_attempt=1,head_sha=self.revision,status='completed',conclusion='failure',
            event='push',path='.github/workflows/'+self.a.FAST_TRACK_RELEASE_WORKFLOW,
            head_branch=self.a.FAST_TRACK_PR_BASE,head_repository=dict(full_name=self.repo))
        self.promotion=dict(self.producer,id=9,head_sha=self.parent,conclusion='success')
        self.run=dict(self.producer,id=20,event='workflow_dispatch',status='in_progress',conclusion=None,
                      path='.github/workflows/'+self.a.FAST_TRACK_COMPLETION_WORKFLOW)
        self.job=dict(id=30,run_id=20,run_attempt=1,head_sha=self.revision,name=self.a.FAST_TRACK_COMPLETION_JOB,
                      status='in_progress',conclusion=None,runner_name='fixture-runner')
        self.head=self.revision

    def history(self,repository,revision,token):
        self.assertEqual((repository,token),(self.repo,'fixture'))
        rows=[self.producer] if revision==self.revision else [self.promotion] if revision==self.parent else []
        return self.history_mutation(revision,rows) if self.history_mutation else rows

    def promote(self,repository,run,token):
        self.calls.append(('promotion',run['id']))
        if run['head_sha']==self.revision:
            if self.promotion_error:raise self.promotion_error
            return 'f'*40 if self.direct else None
        self.assertEqual(run,self.promotion)
        if self.promotion_error:raise self.promotion_error
        return 'f'*40

    def admission(self,repository,revision,token):
        self.calls.append(('source',revision))
        if self.source_error:raise self.source_error
        value=dict(candidate=self.candidate,executionAuthority=self.revision,sourcePr=7,validation={})
        return self.admission_mutation(value) if self.admission_mutation else value

    def api(self,repository,path,token):
        self.assertEqual((repository,token),(self.repo,'fixture'))
        if path=='git/commits/'+self.revision:
            value=dict(sha=self.revision,parents=[dict(sha=self.parent),dict(sha=self.candidate)])
            return self.commit_mutation(value) if self.commit_mutation else value
        if path=='git/ref/heads/'+self.a.FAST_TRACK_PR_BASE:return dict(object=dict(sha=self.head))
        if path=='actions/runs/20/attempts/1/jobs?per_page=100':return dict(total_count=1,jobs=[self.job])
        raise AssertionError(path)

    def fresh(self,repository,path,token):
        self.assertEqual((repository,token),(self.repo,'fixture'));self.raw_reads.append(path)
        value=dict(self.producer if path=='actions/runs/10' else self.promotion if path=='actions/runs/9' else {})
        self.assertTrue(value)
        return self.fresh_mutation(path,value) if self.fresh_mutation else value

    def git_run(self,args,**kwargs):
        self.calls.append(('git',args))
        if self.git_error and self.git_error in args:
            return SimpleNamespace(returncode=128,stdout='',stderr='fixture unavailable')
        return self.real_run(args,cwd=self.directory,**kwargs)

    def git_output(self,args,**kwargs):
        self.calls.append(('git',args))
        if self.git_error and self.git_error in args:raise subprocess.CalledProcessError(128,args)
        if '--raw' in args and self.raw_override is not None:return self.raw_override
        return self.real_run(args,cwd=self.directory,stdout=subprocess.PIPE,stderr=subprocess.PIPE,
                             check=True,**kwargs).stdout

    def environment(self):
        import contextlib
        stack=contextlib.ExitStack()
        for name,value in [('fast_track_historical_producers',self.history),('fast_track_promotion_parent',self.promote),
                           ('fast_track_merged_source_proof',self.admission),('api_get',self.api),('_api_get_uncached',self.fresh)]:
            stack.enter_context(patch.object(self.a,name,side_effect=value))
        stack.enter_context(patch.object(self.a,'_release_history_source',return_value=self.workflow))
        stack.enter_context(patch.object(self.a,'_fast_track_policy',return_value=self.policy))
        stack.enter_context(patch.object(self.a.subprocess,'run',side_effect=self.git_run))
        stack.enter_context(patch.object(self.a.subprocess,'check_output',side_effect=self.git_output))
        stack.enter_context(patch.dict(os.environ,{'RUNNER_NAME':'fixture-runner'}))
        return stack

    def observe(self,live=True):
        with self.environment():
            return self.a.fast_track_completion_observer(self.repo,self.run,'fixture',live=live,attempt=1)

    def test_exact_request_only_merge_keeps_current_tool_and_original_promotion(self):
        self.assertEqual(self.observe(),dict(runId=20,runAttempt=1,jobId=30,
                         toolRevision=self.revision,promotionRunId=9))
        self.assertEqual(self.raw_reads,['actions/runs/10','actions/runs/9'])
        self.assertIn(('source',self.revision),self.calls)
        diffs=[args for kind,args in self.calls if kind=='git' and 'diff' in args]
        for args in diffs:
            for flag in ['--no-ext-diff','--no-textconv','--ignore-submodules=none']:self.assertIn(flag,args)
        self.assertTrue(any(':(top)' in args for args in diffs))

    def test_direct_promoted_path_does_not_enter_new_inheritance(self):
        self.direct=True
        with patch.object(self.a,'_completion_inherited_promotion',side_effect=AssertionError('not inherited')):
            self.assertEqual(self.observe()['promotionRunId'],10)
        self.assertFalse(self.raw_reads);self.assertNotIn(('source',self.revision),self.calls)

    def test_missing_duplicate_wrong_current_or_parent_producer_rejected(self):
        for ref in [self.revision,self.parent]:
            for change in [lambda rows:[],lambda rows:rows*2,lambda rows:[dict(rows[0],head_sha='f'*40)]]:
                self.history_mutation=lambda revision,rows,ref=ref,change=change:change(rows) if revision==ref else rows
                with self.subTest(ref=ref,change=change),self.assertRaises(ValueError):self.observe()

    def test_noncanonical_or_active_current_producer_rejected(self):
        for key,value in [('event','workflow_dispatch'),('path','other'),('head_branch','other'),
                          ('head_repository',dict(full_name='other/repo')),('status','in_progress'),('conclusion',None)]:
            old=self.producer[key];self.producer[key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):self.observe()
            self.producer[key]=old

    def test_failed_claimed_promotion_never_falls_back(self):
        self.promotion_error=ValueError('promotion receipt/source/nonentry unproven')
        with patch.object(self.a,'_completion_inherited_promotion',side_effect=AssertionError('no fallback')):
            with self.assertRaisesRegex(ValueError,'receipt/source/nonentry'):self.observe()

    def test_missing_or_malformed_current_attempt_never_inherits(self):
        for value in [None,True,0,-1,'1']:
            self.producer['run_attempt']=value
            with self.subTest(value=value),self.assertRaisesRegex(ValueError,'INHERITED_PRODUCER_UNPROVEN'):
                self.observe()
        self.producer.pop('run_attempt')
        with self.assertRaisesRegex(ValueError,'INHERITED_PRODUCER_UNPROVEN'):self.observe()

    def test_ancestor_promotion_failure_remains_fatal(self):
        original=self.promote
        def promote(repository,run,token):
            if run['head_sha']==self.parent:raise ValueError('ancestor receipt invalid')
            return original(repository,run,token)
        with patch.object(self,'promote',side_effect=promote):
            with self.assertRaisesRegex(ValueError,'ancestor receipt invalid'):self.observe()

    def test_current_merged_source_required_ci_and_candidate_binding(self):
        self.source_error=RuntimeError('required CI unavailable')
        with self.assertRaisesRegex(RuntimeError,'required CI'):self.observe()
        self.source_error=None
        for field in ['candidate','executionAuthority']:
            self.admission_mutation=lambda value,field=field:dict(value,**{field:'f'*40})
            with self.subTest(field=field),self.assertRaisesRegex(ValueError,'SOURCE_UNPROVEN'):self.observe()

    def test_abnormal_reordered_missing_or_forged_parent_binding(self):
        for change in [lambda x:dict(x,sha='f'*40),lambda x:dict(x,parents=x['parents'][:1]),
                       lambda x:dict(x,parents=x['parents']*2),lambda x:dict(x,parents=list(reversed(x['parents']))),
                       lambda x:dict(x,parents=[dict(sha='f'*40),x['parents'][1]])]:
            self.commit_mutation=change
            with self.subTest(change=change),self.assertRaises(ValueError):self.observe()

    def test_fresh_founder_request_transition_is_not_duplicated_or_relaxed(self):
        original=dict(self.request)
        for key,value in [('requestedBy','other'),('requestId','promotion'),('requestId',''),
                          ('authorization',''),('releaseMode','validate-only'),('targets',[]),
                          ('targets','masterapp-portal'),('cloudflareWebsiteRouting',None),('trustedToolPromotion',{})]:
            self.request=dict(original,**{key:value});self.make_child();self.refresh_rows()
            with self.subTest(key=key,value=value),self.assertRaises(ValueError):self.observe()

    def test_any_other_blob_inventory_mode_or_type_change_rejected_by_real_git(self):
        cases=['script','workflow','lock','hidden','app','add','delete','rename','mode','symlink','gitlink']
        for case in cases:
            self.git('reset','--hard',self.candidate);self.git('clean','-fdq')
            path={'script':'scripts/validation-resume.py','workflow':'.github/workflows/deployment-diagnostics.yml',
                  'lock':'scripts/release-artifacts/package-lock.json','hidden':'.config/tool.json','app':'App/source.cs'}.get(case)
            if path:self.write(path,'changed\n')
            elif case=='add':self.write('.hidden-dependency','new\n')
            elif case=='delete':(self.directory/'App/source.cs').unlink()
            elif case=='rename':(self.directory/'App/source.cs').rename(self.directory/'App/renamed.cs')
            elif case=='mode':(self.directory/'App/source.cs').chmod(0o755)
            elif case=='symlink':(self.directory/'outside-link').symlink_to('/outside-fixture')
            elif case=='gitlink':
                self.git('update-index','--add','--cacheinfo','160000,'+self.parent+',hidden-module')
                self.git('config','diff.ignoreSubmodules','all')
            self.make_child(stage=case!='gitlink');self.refresh_rows()
            with self.subTest(case=case),self.assertRaisesRegex(ValueError,'CLOSURE_CHANGED'):self.observe()
            # Reset to the genuine request-only candidate, not the last negative tree.
            self.git('reset','--hard',self.parent);self.git('clean','-fdq');self.make_child();self.refresh_rows()

    def test_request_mode_and_absent_change_rejected(self):
        (self.directory/self.a.RELEASE_REQUEST_PATH).chmod(0o755);self.make_child();self.refresh_rows()
        with self.assertRaisesRegex(ValueError,'REQUEST_DIFF'):self.observe()
        self.raw_override=''
        with self.assertRaisesRegex(ValueError,'REQUEST_DIFF'):self.observe()

    def test_git_failures_and_malformed_or_truncated_raw_inventory_fail_closed(self):
        for command in ['rev-list','merge-base','--quiet','--raw']:
            self.git_error=command
            with self.subTest(command=command),self.assertRaises((ValueError,subprocess.CalledProcessError)):self.observe()
        self.git_error=None
        valid=':100644 100644 '+'a'*40+' '+'b'*40+' M\0'+self.a.RELEASE_REQUEST_PATH+'\0'
        for raw in [valid[:-1],valid+'extra\0',valid*2,valid.replace('100644','120000',1),
                    valid.replace('a'*40,'short'),valid.replace(' M\0',' A\0'),valid.replace('b'*40,'a'*40)]:
            self.raw_override=raw
            with self.subTest(raw=raw),self.assertRaisesRegex(ValueError,'REQUEST_DIFF'):self.observe()

    def test_fresh_uncached_boundary_rejects_producer_rerun_drift_or_late_response(self):
        for path in ['actions/runs/10','actions/runs/9']:
            for key,value in [('run_attempt',2),('status','in_progress'),('conclusion','failure'),
                              ('head_sha','f'*40),('head_repository',dict(full_name='other/repo'))]:
                if path.endswith('/10') and key=='conclusion':value='success'
                self.fresh_mutation=lambda p,row,path=path,key=key,value=value:dict(row,**{key:value}) if p==path else row
                with self.subTest(path=path,key=key),self.assertRaisesRegex(ValueError,'PRODUCER_CHANGED'):self.observe()
        now=[0]
        def late(path,row):now[0]=31;return row
        self.fresh_mutation=late
        with self.a.evidence_lookup_budget(30,clock=lambda:now[0]):
            with self.assertRaises(self.a.EvidenceLookupUnavailable):self.observe()

    def test_live_ref_and_job_fences_still_apply_after_inheritance(self):
        self.head=self.parent
        with self.assertRaisesRegex(ValueError,'HEAD_CHANGED'):self.observe()
        self.head=self.revision
        for key,value in [('run_id',99),('run_attempt',2),('head_sha',self.parent),('runner_name','other')]:
            old=self.job[key];self.job[key]=value
            with self.subTest(key=key),self.assertRaisesRegex(ValueError,'JOB_UNPROVEN'):self.observe()
            self.job[key]=old

    def test_historical_summary_reauthenticates_inherited_identity_and_success_owner(self):
        self.run.update(status='completed',conclusion='success');self.job.update(status='completed',conclusion='success')
        self.job['steps']=[dict(name='Reconcile original bytes and record only missing success receipts',
            status='completed',conclusion='success',started_at='2026-10-21T13:00:00Z',completed_at='2026-10-21T13:06:00Z')]
        original=dict(id=10,head_sha=self.revision);targets={'portal':dict(operationId='a'*64)}
        name=self.a.fast_track_completion_summary_name(10,self.revision)
        artifact=dict(id=40,name=name,expired=False,digest='sha256:'+'b'*64,workflow_run=dict(id=20),created_at='2026-10-21T13:05:00Z')
        observer=dict(runId=20,runAttempt=1,jobId=30,toolRevision=self.revision,promotionRunId=9)
        record=dict(schemaVersion=1,phase='success',originalRun=10,applicationRevision=self.revision,
                    producingRun=20,producingAttempt=1,observer=observer,targets=targets)
        api=self.api
        def history_api(repository,path,token):
            if path.startswith('actions/artifacts?'):return dict(artifacts=[artifact],total_count=1)
            if path=='actions/runs/20':return self.run
            return api(repository,path,token)
        with self.environment(),patch.object(self.a,'api_get',side_effect=history_api), \
             patch.object(self.a,'_release_history_json',return_value=record) as read:
            self.assertTrue(self.a.fast_track_completion_summary(self.repo,original,'fixture',targets))
            self.assertTrue(read.call_args.kwargs['verify_digest'])
            for key,value in [('toolRevision',self.parent),('promotionRunId',10)]:
                record['observer']=dict(observer,**{key:value})
                with self.subTest(key=key),self.assertRaisesRegex(ValueError,'SUMMARY_IDENTITY'):
                    self.a.fast_track_completion_summary(self.repo,original,'fixture',targets)
            record['observer']=observer;self.job['conclusion']='failure'
            self.assertFalse(self.a.fast_track_completion_summary(self.repo,original,'fixture',targets))

    def test_write_capable_retained_recovery_cannot_inherit(self):
        self.run['path']='.github/workflows/'+self.a.FAST_TRACK_RELEASE_WORKFLOW
        with self.environment(),patch.object(self.a,'_release_history_source',return_value=(ROOT.parent/self.run['path']).read_text()), \
             patch.object(self.a,'_completion_inherited_promotion',side_effect=AssertionError('write owner must not inherit')):
            with self.assertRaisesRegex(ValueError,'TOOL_NOT_PROMOTED'):
                self.a.fast_track_recovery_producer(self.repo,self.run,'fixture',attempt=1)


class DiagnosticsBootstrapTests(unittest.TestCase):
    """Real immutable Git objects; never a mock SQL/provider success marker."""
    @classmethod
    def setUpClass(cls):
        import release_policy as policy
        cls.policy = policy
        cls.tmp = tempfile.TemporaryDirectory()
        cls.repo = Path(cls.tmp.name) / 'repo.git'
        subprocess.run(['git', 'clone', '--bare', '--shared', str(ROOT.parent), str(cls.repo)], check=True, capture_output=True)
        cls.env = dict(os.environ, GIT_DIR=str(cls.repo), GIT_INDEX_FILE=str(cls.repo / 'fixture-index'),
                       GIT_AUTHOR_NAME='Synthetic fixture', GIT_AUTHOR_EMAIL='fixture@example.invalid',
                       GIT_COMMITTER_NAME='Synthetic fixture', GIT_COMMITTER_EMAIL='fixture@example.invalid')
        cls.paths = set(policy.DIAGNOSTICS_BOOTSTRAP_TOOLS) | set(policy.DIAGNOSTICS_BOOTSTRAP_RAZOR) | {'scripts/release_policy.py'}
        cls.base = policy.DIAGNOSTICS_BOOTSTRAP_BASE
        cls.git('read-tree', cls.base)
        cls.files = {}
        for path in cls.paths:
            # This one-use bootstrap fixture must retain its original reviewed
            # blobs after later ordinary tool promotions change the working tree.
            pinned = policy.DIAGNOSTICS_BOOTSTRAP_TOOLS.get(path)
            if path in policy.DIAGNOSTICS_BOOTSTRAP_RAZOR:
                pinned = policy.DIAGNOSTICS_BOOTSTRAP_RAZOR[path][1]
            content = cls.git('cat-file', 'blob', pinned).encode() if pinned else (ROOT.parent / path).read_bytes()
            blob = cls.git('hash-object', '-w', '--stdin', data=content).strip()
            cls.files[path] = dict(mode='100644', blob=blob)
            cls.git('update-index', '--add', '--cacheinfo', '100644', blob, path)
        tree = cls.git('write-tree').strip()
        cls.source = cls.git('commit-tree', tree, '-p', cls.base, data=b'synthetic exact source\n').strip()
        cls.request = dict(policy.DIAGNOSTICS_BOOTSTRAP_REQUEST, trustedToolPromotion=dict(
            schemaVersion=1, baseRevision=cls.base, sourceRevision=cls.source, files=cls.files))
        cls.revision = cls.candidate(cls.request)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    @classmethod
    def git(cls, *args, data=None):
        return subprocess.run(['git', *args], input=data, env=cls.env, check=True, capture_output=True).stdout.decode()

    @classmethod
    def candidate(cls, request, changes=None):
        cls.git('read-tree', cls.source)
        blob = cls.git('hash-object', '-w', '--stdin', data=json.dumps(request).encode()).strip()
        cls.git('update-index', '--add', '--cacheinfo', '100644', blob, cls.policy.REQUEST)
        for path, mode, content in changes or []:
            blob = cls.git('hash-object', '-w', '--stdin', data=content).strip()
            cls.git('update-index', '--add', '--cacheinfo', mode, blob, path)
        tree = cls.git('write-tree').strip()
        return cls.git('commit-tree', tree, '-p', cls.source, data=b'synthetic bound request\n').strip()

    def scope(self, revision=None, base=None):
        original = subprocess.run
        def run(args, **kw):
            kw['env'] = self.env
            return original(args, **kw)
        with patch.object(self.policy.subprocess, 'run', side_effect=run):
            return self.policy.diagnostics_bootstrap_scope(base or self.base, revision or self.revision)

    def promotion(self, revision=None, mutation=None):
        candidate = revision or self.revision
        self.git('read-tree', candidate)
        tree = self.git('write-tree').strip()
        merge = self.git('commit-tree', tree, '-p', self.base, '-p', candidate, data=b'synthetic merge\n').strip()
        admission = dict(executionAuthority=merge, candidate=candidate, sourcePr=1,
            validation={key:{} for key in ('approved-release-security-validation.yml',
                'masterapp-platform-architecture-validation.yml', 'parfait-isolated-validation.yml')})
        if mutation: mutation(admission)
        original = subprocess.run
        def run(args, **kw):
            kw['env'] = self.env
            return original(args, **kw)
        with patch.object(self.policy.subprocess, 'run', side_effect=run):
            return self.policy.trusted_tool_promotion(self.base, merge, admission)

    def test_full_authenticated_merge_promotion_still_has_zero_effects(self):
        proof = self.promotion()
        self.assertFalse(proof['productionEligible'])
        self.assertFalse(proof['authorizesMutation'])
        self.assertEqual(self.source, proof['sourceRevision'])
        self.assertEqual(self.files, proof['files'])

    def test_merge_admission_drift_still_rejected(self):
        for mutation in [lambda a:a.update(executionAuthority='f'*40),
                         lambda a:a.update(candidate=None),
                         lambda a:a.update(sourcePr=0),
                         lambda a:a.update(validation={})]:
            with self.subTest(mutation=mutation), self.assertRaisesRegex(ValueError, 'AUTHORIZATION_UNPROVEN'):
                self.promotion(mutation=mutation)

    def test_merge_rejects_extra_runtime_even_if_request_targets_stay_empty(self):
        candidate = self.candidate(self.request, [('AgentPortal/Program.cs', '100644', b'changed')])
        with self.assertRaisesRegex(ValueError, 'BOOTSTRAP'):
            self.promotion(candidate)

    def test_readiness_matches_same_exact_case_and_drift_requires_provider_proof(self):
        spec = importlib.util.spec_from_file_location('bootstrap_readiness_fixture', ROOT / 'validation-resume.py')
        authority = importlib.util.module_from_spec(spec); spec.loader.exec_module(authority)
        original = subprocess.run
        def run(args, **kw):
            kw['env'] = self.env
            return original(args, **kw)
        with patch.object(authority.subprocess, 'run', side_effect=run):
            result = authority.readiness_scope(self.revision, self.base)
            self.assertEqual('not-required', result['state'])
            self.assertEqual('exact_nonpublishing_diagnostics_bootstrap', result['reason'])
            self.assertFalse(result['authorizesMutation'])
            drift = self.candidate(self.request, [('AgentPortal/Program.cs', '100644', b'changed')])
            self.assertEqual('required', authority.readiness_scope(drift, self.base)['state'])
            self.assertEqual('required', authority.readiness_scope(self.revision, self.source)['state'])

    def test_exact_source_is_nonpublishing_and_bound_to_full_tree(self):
        proof = self.scope()
        self.assertFalse(proof['productionEligible'])
        self.assertFalse(proof['authorizesMutation'])
        self.assertEqual(14, len(proof['paths']))
        self.assertEqual(self.source, proof['sourceRevision'])
        self.assertEqual(40, len(proof['sourceTree']))

    def test_base_change_or_replay_cannot_match(self):
        self.assertIsNone(self.scope(base=self.source))
        self.assertIsNone(self.scope(base=self.revision))

    def test_razor_paths_never_enter_generic_tool_allowlist(self):
        self.assertTrue(set(self.policy.DIAGNOSTICS_BOOTSTRAP_RAZOR).isdisjoint(self.policy.TRUSTED_TOOL_PATHS))

    def test_request_drift_rejected(self):
        import copy
        for key, value in [('targets', ['masterapp-portal']), ('cloudflareWebsiteRouting', True),
                           ('releaseMode', 'approved-only'), ('preserveLiveTargets', True),
                           ('authorization', 'changed'), ('purpose', 'changed'), ('unexpected', True)]:
            req = copy.deepcopy(self.request); req[key] = value
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, 'BOOTSTRAP'):
                self.scope(self.candidate(req))

    def test_manifest_or_source_drift_rejected(self):
        import copy
        for key, value in [('baseRevision', 'f'*40), ('sourceRevision', self.revision),
                           ('schemaVersion', True), ('files', {})]:
            req = copy.deepcopy(self.request); req['trustedToolPromotion'][key] = value
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, 'BOOTSTRAP'):
                self.scope(self.candidate(req))

    def test_extra_application_migration_model_or_mode_rejected(self):
        cases = [('AgentPortal/Program.cs', '100644', b'changed'),
                 ('Infrastructure/Migrations/MasterAppDbContextModelSnapshot.cs', '100644', b'changed'),
                 ('Infrastructure/Migrations/20261030033900_RuntimeDiagnosticPerformanceEvidence.cs', '100644', b'changed'),
                 ('SHARED/Views/Shared/ReceptionistManagement.cshtml', '100755', b'changed'),
                 ('scripts/MigrationReleaseProbe/Program.cs', '100644', b'changed')]
        for change in cases:
            with self.subTest(path=change[0]), self.assertRaisesRegex(ValueError, 'BOOTSTRAP'):
                self.scope(self.candidate(self.request, [change]))


if __name__ == '__main__':
    unittest.main()
