#!/usr/bin/env python3
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("validation_resume", ROOT / "scripts" / "validation-resume.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


class ValidationResumePlannerTests(unittest.TestCase):
    def successful_steps(self, workflow):
        return {
            gate["step"]: "success"
            for gate in m.WORKFLOWS[workflow]["gates"].values()
        }

    def prior(self, sha="a" * 40):
        return {"id": 17, "head_sha": sha, "run_attempt": 1}

    def test_effective_steps_keeps_latest_executed_failure_and_backfills_only_skips(self):
        effective = m._effective_steps([
            {
                "gate-a": "failure",
                "gate-b": "skipped",
                "gate-c": "success",
            },
            {
                "gate-a": "success",
                "gate-b": "success",
                "gate-c": "failure",
            },
        ])
        self.assertEqual("failure", effective["gate-a"])
        self.assertEqual("success", effective["gate-b"])
        self.assertEqual("success", effective["gate-c"])

    def test_unchanged_successes_are_preserved(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "a" * 40,
            self.prior(),
            self.successful_steps(workflow),
            [],
            "prior_run",
        )
        self.assertEqual("incremental", plan["mode"])
        self.assertTrue(all(not gate["run"] for gate in plan["gates"].values()))

    def test_failed_gate_is_invalidated_while_unrelated_green_gate_is_preserved(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        steps = self.successful_steps(workflow)
        steps["Run booking authority regressions"] = "failure"
        plan = m.compute_plan(
            workflow,
            "a" * 40,
            self.prior(),
            steps,
            [],
            "prior_attempt",
        )
        self.assertTrue(plan["gates"]["booking-regressions"]["run"])
        self.assertTrue(plan["gates"]["compile-regression"]["run"])
        self.assertTrue(plan["gates"]["restore-dotnet"]["run"])
        self.assertFalse(plan["gates"]["renderer-tests"]["run"])
        self.assertFalse(plan["gates"]["cms-tests"]["run"])
        self.assertFalse(plan["gates"]["form-tracking"]["run"])

    def test_test_only_fix_reruns_only_affected_test_and_required_build_chain(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal.Tests/PublicBookingResolverTests.cs"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["booking-regressions"]["run"])
        self.assertTrue(plan["gates"]["compile-regression"]["run"])
        self.assertTrue(plan["gates"]["restore-dotnet"]["run"])
        self.assertFalse(plan["gates"]["meta-regressions"]["run"])
        self.assertFalse(plan["gates"]["website-regressions"]["run"])
        self.assertFalse(plan["gates"]["crm-regressions"]["run"])
        self.assertFalse(plan["gates"]["renderer-tests"]["run"])

    def test_diagnostics_only_source_change_preserves_unrelated_domain_regressions(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal/Services/FounderSoftwareRemediationService.cs"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["build-hosts"]["run"])
        self.assertTrue(plan["gates"]["compile-regression"]["run"])
        self.assertTrue(plan["gates"]["restore-dotnet"]["run"])
        self.assertTrue(plan["gates"]["founder-diagnostics-regressions"]["run"])
        self.assertFalse(plan["gates"]["website-regressions"]["run"])
        self.assertFalse(plan["gates"]["meta-regressions"]["run"])
        self.assertFalse(plan["gates"]["booking-regressions"]["run"])
        self.assertFalse(plan["gates"]["crm-regressions"]["run"])
        self.assertFalse(plan["gates"]["renderer-tests"]["run"])

    def test_diagnostics_test_fix_reruns_only_diagnostics_test_gate_and_build_chain(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal.Tests/FounderRepositoryInspectionTests.cs"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["founder-diagnostics-regressions"]["run"])
        self.assertTrue(plan["gates"]["compile-regression"]["run"])
        self.assertTrue(plan["gates"]["restore-dotnet"]["run"])
        self.assertFalse(plan["gates"]["website-regressions"]["run"])
        self.assertFalse(plan["gates"]["meta-regressions"]["run"])
        self.assertFalse(plan["gates"]["booking-regressions"]["run"])
        self.assertFalse(plan["gates"]["crm-regressions"]["run"])

    def test_backend_source_change_keeps_existing_dotnet_regression_coverage(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal/Controllers/HomeController.cs"],
            "prior_run",
        )
        for gate in (
            "build-hosts",
            "compile-regression",
            "website-regressions",
            "meta-regressions",
            "booking-regressions",
            "crm-regressions",
        ):
            self.assertTrue(plan["gates"][gate]["run"], gate)
        self.assertFalse(plan["gates"]["renderer-tests"]["run"])
        self.assertFalse(plan["gates"]["cms-tests"]["run"])


    def test_single_gate_definition_change_invalidates_only_that_gate(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            [],
            "prior_run",
            {"Verify consolidated release scope and routing policy"},
            False,
        )
        self.assertEqual("incremental", plan["mode"])
        self.assertTrue(plan["gates"]["release-policy"]["run"])
        self.assertEqual("gate_definition_changed", plan["gates"]["release-policy"]["reason"])
        for key, gate in plan["gates"].items():
            if key != "release-policy":
                self.assertFalse(gate["run"], key)

    def test_workflow_structure_change_still_fails_closed(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            [],
            "prior_run",
            set(),
            True,
        )
        self.assertEqual("full", plan["mode"])
        self.assertTrue(all(gate["run"] for gate in plan["gates"].values()))

    def test_gate_scope_masks_only_configured_step_bodies(self):
        prior = """name: X
jobs:
  validate:
    steps:
      - name: Gate A
        run: echo old
      - name: Gate B
        run: echo same
"""
        current = prior.replace("echo old", "echo new")
        changed, structure = m.workflow_gate_change_scope(prior, current, {"Gate A", "Gate B"})
        self.assertEqual({"Gate A"}, changed)
        self.assertFalse(structure)

        structure_edit = current.replace("name: X", "name: Y")
        _, structure = m.workflow_gate_change_scope(prior, structure_edit, {"Gate A", "Gate B"})
        self.assertTrue(structure)

    def test_job_definition_comparison_is_exact_and_bounded(self):
        text = """jobs:
  candidate:
    runs-on: ubuntu-latest
    steps:
      - run: echo candidate
  baseline:
    runs-on: ubuntu-latest
    steps:
      - run: echo baseline
  validate:
    runs-on: ubuntu-latest
    steps:
      - run: echo validate
"""
        blocks = m._job_blocks(text)
        self.assertIn("candidate", blocks)
        self.assertIn("baseline", blocks)
        self.assertNotEqual(blocks["candidate"], blocks["baseline"])

    def test_job_definition_parser_preserves_blank_separated_jobs(self):
        text = """jobs:
  plan:
    runs-on: ubuntu-latest

  candidate:
    runs-on: ubuntu-latest
    steps:
      - run: echo candidate

  baseline:
    runs-on: ubuntu-latest
    steps:
      - run: echo baseline

  validate:
    runs-on: ubuntu-latest
"""
        blocks = m._job_blocks(text)
        self.assertEqual({"plan", "candidate", "baseline", "validate"}, set(blocks))

    def test_real_step5_workflow_exposes_candidate_and_baseline_jobs(self):
        path = ROOT / ".github" / "workflows" / "step5-isolated-conversion-mapping-validation.yml"
        blocks = m._job_blocks(path.read_text())
        for name in ("plan", "baseline-evidence", "candidate", "baseline", "validate"):
            self.assertIn(name, blocks)
        self.assertIn("Run full AgentPortal candidate suite", blocks["candidate"])
        self.assertIn("Run identical suite on approved baseline", blocks["baseline"])

    def test_release_workflow_policy_covers_every_named_direct_release_step(self):
        path = ROOT / ".github" / "workflows" / "all-intentional-direct-release-20260918.yml"
        policies = m.verify_release_policy_coverage(
            "all-intentional-direct-release-20260918.yml",
            path.read_text(),
        )
        self.assertEqual(
            set(m.named_step_blocks(path.read_text())),
            set(policies),
        )

    def test_release_lifecycle_policy_covers_every_named_step(self):
        path = ROOT / ".github" / "workflows" / "legend-release-lifecycle.yml"
        policies = m.verify_release_policy_coverage(
            "legend-release-lifecycle.yml",
            path.read_text(),
        )
        self.assertEqual(
            set(m.named_step_blocks(path.read_text())),
            set(policies),
        )


    def test_step5_candidate_change_invalidates_comparison_but_preserves_unrelated_children(self):
        workflow = "step5-isolated-conversion-mapping-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal.Tests/SomeUnrelatedRegressionTests.cs"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["candidate-build"]["run"])
        self.assertTrue(plan["gates"]["candidate-full"]["run"])
        self.assertTrue(plan["gates"]["comparison"]["run"])
        self.assertEqual(
            "evidence_dependency_invalidated:candidate-full",
            plan["gates"]["comparison"]["reason"],
        )
        self.assertFalse(plan["gates"]["candidate-focused"]["run"])

    def test_step5_workflow_delegates_resume_and_baseline_decisions_to_canonical_authority(self):
        path = ROOT / ".github" / "workflows" / "step5-isolated-conversion-mapping-validation.yml"
        workflow = path.read_text()
        self.assertIn("scripts/validation-resume.py plan", workflow)
        self.assertIn("scripts/validation-resume.py step5-decision", workflow)
        self.assertIn("scripts/validation-resume.py step5-baseline", workflow)
        self.assertNotIn('gh api "/repos/$GITHUB_REPOSITORY/actions/artifacts?name=$baseline_name', workflow)
        self.assertIn("candidate_restore_run", workflow)
        self.assertIn("candidate_build_run", workflow)
        self.assertIn("candidate_focused_run", workflow)
        self.assertIn("candidate_full_run", workflow)
        self.assertIn("comparison_run", workflow)
        self.assertIn("Preserve effective Step 5 candidate evidence", workflow)
        self.assertIn("Preserve effective Step 5 baseline evidence", workflow)
        self.assertIn("/tmp/step5-effective/candidate.trx", workflow)
        self.assertIn("/tmp/step5-effective/baseline.trx", workflow)

    def test_new_release_step_is_automatically_fail_closed_without_registry_edit(self):
        path = ROOT / ".github" / "workflows" / "all-intentional-direct-release-20260918.yml"
        workflow = path.read_text() + """
      - name: Future automatically governed release child
        run: echo future
"""
        policies = m.verify_release_policy_coverage(
            "all-intentional-direct-release-20260918.yml",
            workflow,
        )
        self.assertEqual(
            "fail_closed_execute",
            policies["Future automatically governed release child"],
        )

    def test_release_inventory_is_single_canonical_source_for_baseline_and_live_proof(self):
        self.assertTrue(m.RELEASE_TARGETS)
        names = [row["releaseName"] for row in m.RELEASE_TARGETS.values()]
        self.assertEqual(len(names), len(set(names)))
        self.assertTrue(all(row.get("proofHosts") for row in m.RELEASE_TARGETS.values()))
        self.assertTrue(all(row.get("sourceRoot") for row in m.RELEASE_TARGETS.values()))
        self.assertTrue(all(row.get("package") for row in m.RELEASE_TARGETS.values()))

        baseline = (ROOT / "scripts" / "approved-release-baseline.py").read_text()
        self.assertIn("_validation_authority.release_target_rows()", baseline)
        self.assertIn("_validation_authority.selected_release_target_keys", baseline)
        self.assertNotIn("ALLOWED_RELEASE_TARGET_SETS", baseline)

        release = (ROOT / ".github" / "workflows" / "all-intentional-direct-release-20260918.yml").read_text()
        self.assertIn("scripts/validation-resume.py live-state", release)
        self.assertIn("scripts/validation-resume.py verify-live", release)
        self.assertIn("Publish selected head as one transaction", release)
        for row in m.RELEASE_TARGETS.values():
            self.assertNotIn(row["releaseName"], release)
            self.assertNotIn(row["azureHost"], release)
        self.assertNotIn(m.RELEASE_RESOURCE_GROUP, release)
        self.assertNotIn(m.MIGRATION_BUNDLE_NAME, release)
        self.assertNotIn(m.ROUTING_WORKER_NAME, release)
        self.assertNotIn(m.DOMAIN_REFRESH_PROJECT, release)

    def test_lifecycle_and_release_evidence_lookup_are_canonicalized(self):
        lifecycle = (ROOT / ".github" / "workflows" / "legend-release-lifecycle.yml").read_text()
        self.assertIn("scripts/validation-resume.py lifecycle-evidence", lifecycle)
        identity_step = lifecycle.split("      - name: Resolve lifecycle validation authority identity\n", 1)[1].split("      - name:", 1)[0]
        self.assertNotIn("gh api", identity_step)
        self.assertNotIn("sha256sum", identity_step)

        release = (ROOT / ".github" / "workflows" / "all-intentional-direct-release-20260918.yml").read_text()
        validated = release.split("      - name: Reuse exact successful validation package when available\n", 1)[1].split("      - name:", 1)[0]
        rollback = release.split("      - name: Reuse exact retained live package when available\n", 1)[1].split("      - uses:", 1)[0]
        self.assertIn('git show "${GITHUB_SHA}:scripts/validation-resume.py"', validated)
        self.assertIn('python3 "$RUNNER_TEMP/current-validation-resume.py" validated-package', validated)
        self.assertIn("rollback-evidence", rollback)
        self.assertIn('git show "${RELEASE_SHA}:scripts/validation-resume.py"', release)
        self.assertNotIn("gh api", validated)
        self.assertNotIn("gh api", rollback)

    def test_consumed_evidence_invalidates_forward_without_invalidating_siblings(self):
        workflow = "step5-isolated-conversion-mapping-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal.Tests/SomeUnrelatedRegressionTests.cs"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["candidate-full"]["run"])
        self.assertTrue(plan["gates"]["comparison"]["run"])
        self.assertFalse(plan["gates"]["candidate-focused"]["run"])

    def test_merge_readiness_consumes_one_canonical_validation_topology(self):
        topology = m.required_validation_topology(["scripts/validation-resume.py"])
        self.assertEqual(
            {
                ".github/workflows/masterapp-platform-architecture-validation.yml",
                ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
                ".github/workflows/step6-openai-ads-execution-validation.yml",
                ".github/workflows/steps7-8-governed-advertising-validation.yml",
                ".github/workflows/approved-release-security-validation.yml",
            },
            set(topology["required"]),
        )

        lifecycle = (ROOT / "scripts" / "release-lifecycle.py").read_text()
        self.assertIn("VALIDATION_AUTHORITY.required_validation_topology(names)", lifecycle)
        self.assertIn("latest[path].get('conclusion') != 'success'", lifecycle)
        self.assertNotIn("validation_neutral_path", lifecycle)
        self.assertNotIn("architecture_product_validation", lifecycle)
        self.assertNotIn("architecture_public_website_validation", lifecycle)
        self.assertNotIn("STEP6_VALIDATION_PATHS", lifecycle)
        self.assertNotIn("STEP78_VALIDATION_PATHS", lifecycle)
        self.assertNotIn("VALIDATION_NEUTRAL_PATHS =", lifecycle)

    def test_lifecycle_only_change_requires_architecture_only(self):
        topology = m.required_validation_topology([
            "scripts/release-lifecycle.py",
            "scripts/test-release-lifecycle.py",
        ])
        self.assertEqual(
            {".github/workflows/masterapp-platform-architecture-validation.yml"},
            set(topology["required"]),
        )

    def test_release_package_change_requires_step5_but_not_security(self):
        topology = m.required_validation_topology([
            "scripts/release-package.py",
        ])
        self.assertEqual(
            {
                ".github/workflows/masterapp-platform-architecture-validation.yml",
                ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
            },
            set(topology["required"]),
        )

    def test_deploy_control_test_change_requires_architecture_only(self):
        topology = m.required_validation_topology([
            "scripts/test-deploy-approved-app.py",
        ])
        self.assertEqual(
            {".github/workflows/masterapp-platform-architecture-validation.yml"},
            set(topology["required"]),
        )

    def test_public_website_only_scope_does_not_expand_into_unrelated_validations(self):
        topology = m.required_validation_topology([
            "Infrastructure/WebsiteEditing/WebsiteSiteSource.cs",
        ])
        self.assertTrue(topology["publicWebsiteOnly"])
        self.assertEqual(
            {".github/workflows/masterapp-platform-architecture-validation.yml"},
            set(topology["required"]),
        )

    def test_package_canary_preserves_prior_child_proof_for_control_only_change(self):
        proof = {
            "id": 91,
            "head_sha": "a" * 40,
            "updated_at": "2026-10-02T00:00:00Z",
        }
        identity = "c" * 64
        with patch.object(m, "_package_canary_proof_runs", return_value=[proof]), \
             patch.object(m, "git_changed", return_value=["scripts/test-release-policy.py"]), \
             patch.object(m, "package_identity_for_revision", return_value=identity), \
             patch.object(m, "compute_validated_package_evidence", return_value={
                 "reusable": True,
                 "runId": 90,
                 "artifact": "founder-diagnostics-packages-" + identity,
             }), \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            plan = m.compute_package_canary_plan(
                "MYLEGND/masterapp",
                "b" * 40,
                "0" * 40,
                100,
                "hardening/example",
            )
        self.assertFalse(plan["needed"])
        self.assertEqual(91, plan["evidenceRunId"])
        self.assertEqual(90, plan["exactPackageRunId"])
        self.assertEqual(identity, plan["packageIdentity"])
        self.assertEqual("preserved_prior_package_canary", plan["reason"])

    def test_package_canary_builds_exact_current_revision_when_preserved_inputs_lack_artifact(self):
        proof = {
            "id": 93,
            "head_sha": "a" * 40,
            "updated_at": "2026-10-02T00:00:00Z",
        }
        identity = "d" * 64
        with patch.object(m, "_package_canary_proof_runs", return_value=[proof]), \
             patch.object(m, "git_changed", return_value=["scripts/test-release-policy.py"]), \
             patch.object(m, "package_identity_for_revision", return_value=identity), \
             patch.object(m, "compute_validated_package_evidence", return_value={
                 "reusable": False,
                 "runId": None,
                 "artifact": "founder-diagnostics-packages-" + identity,
                 "reason": "exact_validated_package_missing",
             }), \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            plan = m.compute_package_canary_plan(
                "MYLEGND/masterapp",
                "b" * 40,
                "0" * 40,
                102,
                "hardening/example",
            )
        self.assertTrue(plan["needed"])
        self.assertEqual(identity, plan["packageIdentity"])
        self.assertEqual(
            "exact_revision_package_missing_despite_preserved_inputs",
            plan["reason"],
        )
        self.assertEqual([], plan["changedInputs"])

    def test_package_canary_invalidates_only_for_package_or_application_inputs(self):
        proof = {
            "id": 92,
            "head_sha": "a" * 40,
            "updated_at": "2026-10-02T00:00:00Z",
        }
        with patch.object(m, "_package_canary_proof_runs", return_value=[proof]), \
             patch.object(m, "git_changed", return_value=["AgentPortal/Program.cs"]), \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            plan = m.compute_package_canary_plan(
                "MYLEGND/masterapp",
                "b" * 40,
                "0" * 40,
                101,
                "hardening/example",
            )
        self.assertTrue(plan["needed"])
        self.assertEqual(["AgentPortal/Program.cs"], plan["changedInputs"])
        self.assertEqual("package_or_application_inputs_changed_since_proof", plan["reason"])

    def test_package_backfill_requires_green_exact_revision_and_control_only_descendants(self):
        revision = "a" * 40
        current = "b" * 40
        run = {
            "id": 123,
            "path": ".github/workflows/" + m.PACKAGE_VALIDATION_WORKFLOW,
            "event": "pull_request",
            "status": "completed",
            "conclusion": "success",
            "head_sha": revision,
            "head_repository": {"full_name": "MYLEGND/masterapp"},
            "updated_at": "2026-10-02T00:00:00Z",
        }
        pr = {
            "number": 364,
            "merged_at": "2026-10-02T01:10:41Z",
            "base": {"ref": m.TRUSTED_PR_BASE},
            "head": {
                "sha": revision,
                "repo": {"full_name": "MYLEGND/masterapp"},
            },
        }
        def api_get(_repository, path, _token):
            if path.startswith("actions/runs?"):
                return {"workflow_runs": [run]}
            if path == f"commits/{revision}/pulls":
                return [pr]
            raise AssertionError(path)

        with patch.object(m.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout="", stderr="")), \
             patch.object(m, "git_changed", return_value=["scripts/release-lifecycle.py"]), \
             patch.object(m, "api_get", side_effect=api_get), \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            plan = m.compute_package_backfill_plan("MYLEGND/masterapp", revision, current)

        self.assertTrue(plan["allowed"])
        self.assertEqual(123, plan["validationRunId"])
        self.assertEqual(364, plan["sourcePr"])
        self.assertEqual([], plan["changedApplicationInputs"])

    def test_package_backfill_fails_closed_on_application_drift(self):
        revision = "a" * 40
        current = "b" * 40
        with patch.object(m.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout="", stderr="")), \
             patch.object(m, "git_changed", return_value=["AgentPortal/Program.cs"]), \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            plan = m.compute_package_backfill_plan("MYLEGND/masterapp", revision, current)

        self.assertFalse(plan["allowed"])
        self.assertEqual(["AgentPortal/Program.cs"], plan["changedApplicationInputs"])
        self.assertEqual("application_inputs_changed_since_validated_revision", plan["reason"])

    def test_rollback_evidence_uses_release_receipt_link_to_validated_package(self):
        revision = "a" * 40
        identity = "b" * 64
        release_run_id = 88
        validation_run_id = 77
        release_name = m.RELEASE_TARGETS["portal"]["releaseName"]
        receipt = f"legend-approved-release-{revision}-{release_name}"
        package_artifact = f"founder-diagnostics-packages-{identity}"
        package_link = f"legend-approved-package-link-{revision}-{identity}"

        release_run = {
            "id": release_run_id,
            "path": ".github/workflows/all-intentional-direct-release-20260918.yml",
            "head_branch": m.TRUSTED_PR_BASE,
            "status": "completed",
            "conclusion": "success",
            "head_repository": {"full_name": "MYLEGND/masterapp"},
        }
        validation_run = {
            "id": validation_run_id,
            "path": ".github/workflows/" + m.PACKAGE_VALIDATION_WORKFLOW,
            "event": "pull_request",
            "status": "completed",
            "conclusion": "success",
            "head_sha": revision,
            "head_repository": {"full_name": "MYLEGND/masterapp"},
        }

        def artifacts(_repository, name, _token):
            if name == receipt:
                return [{"workflow_run": {"id": release_run_id}}]
            if name == package_artifact:
                return [{"workflow_run": {"id": validation_run_id}}]
            return []

        def api_get(_repository, path, _token):
            if path == f"actions/runs/{release_run_id}":
                return release_run
            if path == f"actions/runs/{validation_run_id}":
                return validation_run
            raise AssertionError(path)

        with patch.object(m, "_artifact_rows", side_effect=artifacts), \
             patch.object(m, "api_get", side_effect=api_get), \
             patch.object(m, "_run_artifact_names", return_value={package_link}), \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            evidence = m.compute_rollback_evidence(
                "MYLEGND/masterapp",
                revision,
                "portal",
            )

        self.assertTrue(evidence["reusable"])
        self.assertEqual(validation_run_id, evidence["runId"])
        self.assertEqual(release_run_id, evidence["releaseRunId"])
        self.assertEqual(package_artifact, evidence["packageArtifact"])
        self.assertEqual(identity, evidence["packageIdentity"])
        self.assertEqual(
            "exact_target_release_receipt_with_validated_package_link",
            evidence["reason"],
        )

    def test_validated_package_accepts_receipt_backed_approved_backfill(self):
        revision = "a" * 40
        identity = "b" * 64
        artifact = f"founder-diagnostics-packages-{identity}"
        receipt = m.package_backfill_receipt_name(revision, identity)
        run = {
            "id": 77,
            "path": ".github/workflows/" + m.PACKAGE_VALIDATION_WORKFLOW,
            "event": "workflow_dispatch",
            "status": "completed",
            "conclusion": "success",
            "head_branch": m.TRUSTED_PR_BASE,
            "head_repository": {"full_name": "MYLEGND/masterapp"},
        }
        with patch.object(m, "_artifact_rows", return_value=[{"workflow_run": {"id": 77}}]), \
             patch.object(m, "api_get", return_value=run), \
             patch.object(m, "_run_artifact_names", return_value={artifact, receipt}), \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            evidence = m.compute_validated_package_evidence(
                "MYLEGND/masterapp", revision, identity
            )

        self.assertTrue(evidence["reusable"])
        self.assertEqual(77, evidence["runId"])
        self.assertEqual(
            "validated_package_backfill_from_exact_green_revision",
            evidence["reason"],
        )

    def test_founder_cloudflare_release_trigger_is_canonical_and_narrow(self):
        self.assertTrue(m.founder_cloudflare_release_required([
            "Legend-Cloudflare/src/runtime/registry.mjs",
        ]))
        self.assertTrue(m.founder_cloudflare_release_required([
            "Legend-Cloudflare/wrangler.founder-baseline.jsonc",
        ]))
        self.assertTrue(m.founder_cloudflare_release_required([
            "scripts/deploy-founder-cloudflare.py",
        ]))
        self.assertFalse(m.founder_cloudflare_release_required([
            "Legend-Cloudflare/tests/runtime/qualification-mode.test.mjs",
            "Legend-Cloudflare/scripts/founder-canary.mjs",
            "AgentPortal/Program.cs",
        ]))

    def test_founder_cloudflare_release_scope_is_portal_only(self):
        self.assertEqual(
            ("masterapp-portal",),
            m.release_targets_for_paths(["scripts/deploy-founder-cloudflare.py"]),
        )
        self.assertEqual(
            ("masterapp-portal",),
            m.release_targets_for_paths(["Legend-Cloudflare/src/runtime/registry.mjs"]),
        )
        self.assertEqual(
            ("masterapp-portal", "masterapp-client"),
            m.release_targets_for_paths([
                "scripts/deploy-founder-cloudflare.py",
                "ClientApp/Program.cs",
            ]),
        )

    def test_release_baseline_delegates_application_identity_classification(self):
        baseline = (ROOT / "scripts" / "approved-release-baseline.py").read_text()
        self.assertIn("_validation_authority.release_control_only_path(path)", baseline)
        self.assertNotIn('path.startswith(".github/workflows/")', baseline)

    def test_step5_workflow_only_change_is_neutral_to_architecture(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            [".github/workflows/step5-isolated-conversion-mapping-validation.yml"],
            "prior_run",
        )
        self.assertEqual("incremental", plan["mode"])
        self.assertTrue(all(not gate["run"] for gate in plan["gates"].values()))

    def test_direct_release_workflow_change_reruns_only_lifecycle_gate(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            [".github/workflows/all-intentional-direct-release-20260918.yml"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["lifecycle"]["run"])
        self.assertEqual("gate_inputs_changed", plan["gates"]["lifecycle"]["reason"])
        for key, gate in plan["gates"].items():
            if key != "lifecycle":
                self.assertFalse(gate["run"], key)

    def test_resume_test_change_reruns_only_lifecycle_contract_gate(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["scripts/test-validation-resume.py"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["lifecycle"]["run"])
        self.assertFalse(plan["gates"]["build-hosts"]["run"])
        self.assertFalse(plan["gates"]["meta-regressions"]["run"])

    def test_release_package_change_reruns_only_release_authority_gates(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["scripts/release-package.py"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["lifecycle"]["run"])
        self.assertTrue(plan["gates"]["release-policy"]["run"])
        for key, gate in plan["gates"].items():
            if key not in {"lifecycle", "release-policy"}:
                self.assertFalse(gate["run"], key)

    def test_release_web_contract_change_invalidates_only_release_web_gate(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["tests/legend-connect/example.test.mjs"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["release-web-contracts"]["run"])
        for key, gate in plan["gates"].items():
            if key != "release-web-contracts":
                self.assertFalse(gate["run"], key)

    def test_validation_authority_change_reruns_only_declared_consumers(self):
        architecture = "masterapp-platform-architecture-validation.yml"
        architecture_plan = m.compute_plan(
            architecture,
            "b" * 40,
            self.prior(),
            self.successful_steps(architecture),
            ["scripts/validation-resume.py"],
            "prior_run",
        )
        self.assertEqual("incremental", architecture_plan["mode"])
        self.assertTrue(architecture_plan["gates"]["lifecycle"]["run"])
        for key, gate in architecture_plan["gates"].items():
            if key != "lifecycle":
                self.assertFalse(gate["run"], key)

        step5 = "step5-isolated-conversion-mapping-validation.yml"
        step5_plan = m.compute_plan(
            step5,
            "b" * 40,
            self.prior(),
            self.successful_steps(step5),
            ["scripts/validation-resume.py"],
            "prior_run",
        )
        self.assertEqual("incremental", step5_plan["mode"])
        self.assertTrue(step5_plan["gates"]["comparison"]["run"])
        self.assertFalse(step5_plan["gates"]["candidate-restore"]["run"])
        self.assertFalse(step5_plan["gates"]["candidate-build"]["run"])
        self.assertFalse(step5_plan["gates"]["candidate-focused"]["run"])
        self.assertFalse(step5_plan["gates"]["candidate-full"]["run"])

    def test_unknown_change_fails_closed_to_full(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["Some-New-Unclassified-System/file.bin"],
            "prior_run",
        )
        self.assertEqual("full", plan["mode"])
        self.assertTrue(all(gate["run"] for gate in plan["gates"].values()))

    def test_step6_unrelated_commit_preserves_all_successful_step6_evidence(self):
        workflow = "step6-openai-ads-execution-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["scripts/test-release-policy.py"],
            "prior_run",
        )
        self.assertEqual("incremental", plan["mode"])
        self.assertTrue(all(not gate["run"] for gate in plan["gates"].values()))

    def test_step6_test_fix_preserves_unrelated_workflows_but_rebuilds_test_graph(self):
        workflow = "step6-openai-ads-execution-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal.Tests/OpenAiAdsExecutionServiceTests.cs"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["tests"]["run"])
        self.assertTrue(plan["gates"]["build"]["run"])
        self.assertTrue(plan["gates"]["restore"]["run"])

    def test_security_validator_preserves_unaffected_successful_gates(self):
        workflow = "approved-release-security-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal/Services/Engineering/LegendEngineeringOrchestrator.cs"],
            "prior_run",
        )
        self.assertEqual("incremental", plan["mode"])
        self.assertFalse(plan["gates"]["db-validation"]["run"])
        self.assertFalse(plan["gates"]["no-skips"]["run"])
        self.assertFalse(plan["gates"]["vulnerabilities"]["run"])
        self.assertFalse(plan["gates"]["secret-scan"]["run"])
        self.assertFalse(plan["gates"]["keyring"]["run"])
        self.assertFalse(plan["gates"]["composition"]["run"])
        self.assertTrue(plan["gates"]["diff-check"]["run"])

    def test_security_project_graph_change_reruns_restore_and_vulnerability_audit(self):
        workflow = "approved-release-security-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal/AgentPortal.csproj"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["restore"]["run"])
        self.assertTrue(plan["gates"]["vulnerabilities"]["run"])
        self.assertFalse(plan["gates"]["composition"]["run"])

    def test_security_program_change_reruns_only_composition_keyring_and_diff(self):
        workflow = "approved-release-security-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal/Program.cs"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["composition"]["run"])
        self.assertTrue(plan["gates"]["keyring"]["run"])
        self.assertTrue(plan["gates"]["diff-check"]["run"])
        self.assertFalse(plan["gates"]["db-validation"]["run"])
        self.assertFalse(plan["gates"]["no-skips"]["run"])

    def test_step78_ui_only_fix_does_not_repeat_dotnet_validation(self):
        workflow = "steps7-8-governed-advertising-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["Legend-Design/legend-website-management.js"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["website-ui-tests"]["run"])
        self.assertFalse(plan["gates"]["governance-tests"]["run"])
        self.assertFalse(plan["gates"]["build"]["run"])
        self.assertFalse(plan["gates"]["restore"]["run"])


    @patch.object(m, "api_get")
    def test_trusted_historical_run_recovers_pr_identity_when_github_omits_linkage(self, api_get):
        head = "c" * 40
        args = SimpleNamespace(
            event="pull_request",
            workflow="masterapp-platform-architecture-validation.yml",
            repository="MYLEGND/masterapp",
            current_run_id=99,
        )

        def response(repository, path, token):
            self.assertEqual(args.repository, repository)
            self.assertEqual("token", token)
            if path.startswith("actions/workflows/"):
                return {
                    "workflow_runs": [{
                        "id": 88,
                        "event": "pull_request",
                        "conclusion": "success",
                        "head_repository": {"full_name": args.repository},
                        "pull_requests": [],
                        "head_sha": head,
                        "updated_at": "2026-10-02T00:00:00Z",
                    }]
                }
            if path == f"commits/{head}/pulls?per_page=100":
                return [{
                    "base": {"ref": m.TRUSTED_PR_BASE},
                    "head": {
                        "sha": head,
                        "repo": {"full_name": args.repository},
                    },
                }]
            raise AssertionError(path)

        api_get.side_effect = response
        runs = m._trusted_historical_runs(args, "token")

        self.assertEqual([88], [run["id"] for run in runs])
        self.assertEqual(head, runs[0]["head_sha"])

    def test_step5_baseline_reuses_prior_artifact_for_control_only_base_change(self):
        prior_base = "a" * 40
        current_base = "b" * 40
        run_head = "c" * 40
        artifact = "step5-baseline-" + prior_base
        run = {
            "id": 77,
            "path": ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
            "event": "pull_request",
            "status": "completed",
            "head_sha": run_head,
            "head_repository": {"full_name": "MYLEGND/masterapp"},
            "updated_at": "2026-10-02T00:00:00Z",
        }

        def api_get(repository, path, token):
            if path.startswith("actions/artifacts?name="):
                return {"artifacts": []}
            if path.startswith("actions/workflows/"):
                return {"workflow_runs": [run]}
            raise AssertionError(path)

        with patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}, clear=False), \
             patch.object(m, "api_get", side_effect=api_get), \
             patch.object(m, "_run_artifact_names", return_value={artifact}), \
             patch.object(m, "_step5_jobs_unchanged", return_value=True), \
             patch.object(m, "git_changed", return_value=["scripts/release-lifecycle.py"]):
            result = m.compute_step5_baseline_evidence("MYLEGND/masterapp", current_base)

        self.assertTrue(result["reusable"])
        self.assertEqual(77, result["evidenceRunId"])
        self.assertEqual(artifact, result["evidenceArtifact"])
        self.assertEqual(prior_base, result["evidenceBaseSha"])
        self.assertEqual("content_identical_step5_inputs", result["reason"])

    def test_step5_baseline_rejects_prior_artifact_when_test_inputs_changed(self):
        prior_base = "a" * 40
        current_base = "b" * 40
        run_head = "c" * 40
        artifact = "step5-baseline-" + prior_base
        run = {
            "id": 78,
            "path": ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
            "event": "pull_request",
            "status": "completed",
            "head_sha": run_head,
            "head_repository": {"full_name": "MYLEGND/masterapp"},
            "updated_at": "2026-10-02T00:00:00Z",
        }

        def api_get(repository, path, token):
            if path.startswith("actions/artifacts?name="):
                return {"artifacts": []}
            if path.startswith("actions/workflows/"):
                return {"workflow_runs": [run]}
            raise AssertionError(path)

        with patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}, clear=False), \
             patch.object(m, "api_get", side_effect=api_get), \
             patch.object(m, "_run_artifact_names", return_value={artifact}), \
             patch.object(m, "_step5_jobs_unchanged", return_value=True), \
             patch.object(m, "git_changed", return_value=["AgentPortal/Program.cs"]):
            result = m.compute_step5_baseline_evidence("MYLEGND/masterapp", current_base)

        self.assertFalse(result["reusable"])
        self.assertEqual("no_content_identical_baseline_artifact", result["reason"])


    def test_step5_frontend_node_tests_are_neutral_to_dotnet_candidate(self):
        workflow = "step5-isolated-conversion-mapping-validation.yml"
        for path in (
            "tests/layout/modal-content-region.test.mjs",
            "tests/legend-connect/limits-presentation.test.mjs",
        ):
            plan = m.compute_plan(
                workflow,
                "b" * 40,
                self.prior(),
                self.successful_steps(workflow),
                [path],
                "prior_run",
            )
            self.assertEqual("incremental", plan["mode"])
            self.assertTrue(all(not gate["run"] for gate in plan["gates"].values()))

    def test_step5_cloudflare_test_only_change_is_neutral_to_dotnet_candidate(self):
        workflow = "step5-isolated-conversion-mapping-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["Legend-Cloudflare/tests/security/founder-control.test.mjs"],
            "prior_run",
        )
        self.assertEqual("incremental", plan["mode"])
        self.assertTrue(all(not gate["run"] for gate in plan["gates"].values()))

    def test_step5_partial_class_source_mapping_covers_split_fixture_files(self):
        files = m._step5_class_source_files(
            "AgentPortal.Tests.LegendFounderAiModeIsolationTests"
        )
        self.assertIn(
            "AgentPortal.Tests/LegendFounderAiModeIsolationTests.cs",
            files,
        )
        self.assertIn(
            "AgentPortal.Tests/LegendFounderPretrainedAcceptanceTests.cs",
            files,
        )

    def test_step5_repair_uses_independent_candidate_and_baseline_evidence(self):
        prior_head = "a" * 40
        current_head = "b" * 40
        failing_class = "AgentPortal.Tests.LegendFounderAiModeIsolationTests"
        failing_test = (
            failing_class
            + ".HeldOutFoundation_ResearchFailureSurvivesSubsequentProviderFailure"
        )
        candidate = {
            "runId": 71,
            "headSha": prior_head,
            "artifact": "step5-candidate-" + prior_head,
        }
        baseline = {
            "reusable": True,
            "evidenceRunId": 44,
            "evidenceArtifact": "step5-baseline-" + ("c" * 40),
            "evidenceBaseSha": "c" * 40,
        }
        changed = [
            ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
            "AgentPortal.Tests/LegendFounderPretrainedAcceptanceTests.cs",
            "scripts/test-validation-resume.py",
            "scripts/validation-resume.py",
            "tests/layout/modal-content-region.test.mjs",
        ]

        def download(_repository, _run_id, _name, directory):
            directory.mkdir(parents=True, exist_ok=True)
            filename = "candidate.trx" if directory.name == "candidate" else "baseline.trx"
            (directory / filename).write_text("<TestRun />")

        def failures(path):
            return {failing_test} if path.name == "candidate.trx" else set()

        with patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}, clear=False), \
             patch.object(m, "_step5_prior_candidate_evidence", return_value=candidate), \
             patch.object(m, "compute_step5_baseline_evidence", return_value=baseline), \
             patch.object(m, "_step5_jobs_unchanged", return_value=True), \
             patch.object(m, "git_changed", return_value=changed), \
             patch.object(m, "_download_run_artifact", side_effect=download), \
             patch.object(m, "_trx_failed", side_effect=failures), \
             patch.object(m, "_step5_class_source_files", return_value={
                 "AgentPortal.Tests/LegendFounderPretrainedAcceptanceTests.cs"
             }), \
             patch.object(m, "_git_name_status", return_value=[
                 ("M", path) for path in changed
             ]):
            decision = m.compute_step5_decision(
                "MYLEGND/masterapp",
                current_head,
                "d" * 40,
                99,
                "hardening/example",
            )

        self.assertEqual("repair", decision["mode"])
        self.assertEqual(71, decision["priorRunId"])
        self.assertEqual(44, decision["baselineEvidenceRunId"])
        self.assertEqual(baseline["evidenceArtifact"], decision["baselineEvidenceArtifact"])
        self.assertEqual([failing_class], decision["repairClasses"])
        self.assertEqual(
            "replace_only_previously_failing_classes",
            decision["reason"],
        )

    def test_step5_workflow_repair_reads_independent_baseline_evidence(self):
        workflow = (
            ROOT / ".github" / "workflows"
            / "step5-isolated-conversion-mapping-validation.yml"
        ).read_text()
        self.assertIn("baseline_evidence_run_id", workflow)
        self.assertIn("baseline_evidence_artifact", workflow)
        self.assertIn("Load independently proven approved baseline results", workflow)
        self.assertIn("needs.plan.outputs.baseline_evidence_run_id", workflow)
        self.assertIn("needs.plan.outputs.baseline_evidence_artifact", workflow)

    def test_content_equivalent_evidence_reuses_only_proven_gates_and_keeps_runtime_requirements(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        current = m.compute_plan(
            workflow,
            "b" * 40,
            None,
            {},
            [],
            "no_prior_completed_run",
        )
        candidate = m.compute_plan(
            workflow,
            "b" * 40,
            None,
            {},
            [],
            "no_prior_completed_run",
        )
        candidate["gates"]["renderer-tests"] = {
            "step": m.WORKFLOWS[workflow]["gates"]["renderer-tests"]["step"],
            "run": False,
            "reason": "preserved_prior_success",
        }
        candidate["gates"]["restore-dotnet"] = {
            "step": m.WORKFLOWS[workflow]["gates"]["restore-dotnet"]["step"],
            "run": False,
            "reason": "preserved_prior_success",
        }
        run = {"id": 88, "head_sha": "c" * 40}
        self.assertTrue(m.merge_content_equivalent_evidence(current, candidate, run))
        m._enforce_runtime_requirements(current)
        self.assertFalse(current["gates"]["renderer-tests"]["run"])
        self.assertEqual(88, current["gates"]["renderer-tests"]["evidenceRunId"])
        self.assertEqual("c" * 40, current["gates"]["renderer-tests"]["evidenceHeadSha"])
        self.assertEqual("trusted_pr_history", current["gates"]["renderer-tests"]["evidenceSource"])
        self.assertTrue(current["gates"]["restore-dotnet"]["run"])
        self.assertTrue(current["gates"]["booking-regressions"]["run"])

    def test_content_equivalent_evidence_never_reuses_unproven_candidate_gate(self):
        workflow = "step6-openai-ads-execution-validation.yml"
        current = m.compute_plan(
            workflow,
            "b" * 40,
            None,
            {},
            [],
            "no_prior_completed_run",
        )
        candidate = m.compute_plan(
            workflow,
            "b" * 40,
            None,
            {},
            [],
            "no_prior_completed_run",
        )
        run = {"id": 89, "head_sha": "d" * 40}
        self.assertFalse(m.merge_content_equivalent_evidence(current, candidate, run))
        self.assertTrue(all(gate["run"] for gate in current["gates"].values()))


if __name__ == "__main__":
    unittest.main()
