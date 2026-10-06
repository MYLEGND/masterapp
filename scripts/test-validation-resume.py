#!/usr/bin/env python3
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("validation_resume", ROOT / "scripts" / "validation-resume.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


class ApprovedHeadPreflightTests(unittest.TestCase):
    def test_current_candidate_requires_exact_current_approved_ancestry(self):
        approved = "a" * 40
        candidate = "b" * 40

        def api_get(_repository, path, _token):
            if path.startswith("branches/"):
                return {"commit": {"sha": approved}}
            if path.startswith("compare/"):
                return {
                    "status": "ahead",
                    "merge_base_commit": {"sha": approved},
                }
            raise AssertionError(path)

        with patch.object(m, "api_get", side_effect=api_get):
            result = m.approved_head_preflight("owner/repo", candidate, "token")
        self.assertTrue(result["current"])
        self.assertEqual(approved, result["approvedHeadSha"])

    def test_diverged_candidate_fails_before_validation_planning(self):
        approved = "a" * 40
        candidate = "b" * 40

        def api_get(_repository, path, _token):
            if path.startswith("branches/"):
                return {"commit": {"sha": approved}}
            if path.startswith("compare/"):
                return {
                    "status": "diverged",
                    "merge_base_commit": {"sha": "c" * 40},
                }
            raise AssertionError(path)

        with patch.object(m, "api_get", side_effect=api_get):
            result = m.approved_head_preflight("owner/repo", candidate, "token")
        self.assertFalse(result["current"])

    def test_non_pr_preflight_is_noop_without_github_lookup(self):
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(
                repository="owner/repo",
                current_sha="a" * 40,
                event="workflow_dispatch",
                output=str(Path(directory) / "preflight.json"),
            )
            with patch.object(m, "approved_head_preflight") as check:
                m.cmd_approved_head_preflight(args)
            check.assert_not_called()
            result = m.json.loads(Path(args.output).read_text())
            self.assertTrue(result["current"])
            self.assertEqual("not_applicable", result["compareStatus"])


class Step5DecisionFastFailTests(unittest.TestCase):
    def test_changed_step5_job_skips_expensive_baseline_history_scan(self):
        candidate = {
            "runId": 99,
            "headSha": "a" * 40,
            "artifact": "step5-candidate-" + "a" * 40,
        }
        with patch.dict(m.os.environ, {"GITHUB_TOKEN": "fixture-token"}), \
             patch.object(m, "_step5_prior_candidate_evidence", return_value=candidate), \
             patch.object(m, "_step5_jobs_unchanged", return_value=False), \
             patch.object(m, "compute_step5_baseline_evidence") as baseline:
            result = m.compute_step5_decision(
                "owner/repo",
                "b" * 40,
                "c" * 40,
                100,
                "repair/work",
            )
        baseline.assert_not_called()
        self.assertEqual("full", result["mode"])
        self.assertEqual("candidate_or_baseline_job_changed", result["reason"])
        self.assertEqual(99, result["priorRunId"])
        self.assertEqual("a" * 40, result["priorHeadSha"])


class ValidationResumePlannerTests(unittest.TestCase):
    def test_planner_errors_stop_before_expensive_children(self):
        for command, computation in ((m.cmd_plan, "prior_evidence"),
                                     (m.cmd_step5_decision, "compute_step5_decision"),
                                     (m.cmd_step5_baseline, "compute_step5_baseline_evidence")):
            with self.subTest(command=command.__name__), tempfile.TemporaryDirectory() as directory:
                args = SimpleNamespace(output=str(Path(directory) / "plan.json"),
                    workflow="step5-isolated-conversion-mapping-validation.yml",
                    repository="owner/repo", current_sha="a" * 40, base_sha="b" * 40,
                    current_run_id=4, head_branch="repair")
                with patch.object(m, computation, side_effect=TimeoutError()), self.assertRaises(SystemExit) as stopped:
                    command(args)
                self.assertEqual(1, stopped.exception.code)
                record = m.json.loads(Path(args.output).read_text())
                self.assertEqual("blocked", record["mode"])
                self.assertNotIn("gates", record)

    def test_evidence_get_retries_transient_timeout_only(self):
        import io
        with patch.object(m.urllib.request, "urlopen", side_effect=[TimeoutError(), io.BytesIO(b'{"ok":true}')]) as request, patch.object(m.time, "sleep"):
            self.assertEqual({"ok": True}, m.api_get("owner/repo", "actions/runs", "fixture"))
            self.assertEqual(2, request.call_count)
        with patch.object(m.urllib.request, "urlopen", side_effect=TimeoutError()) as request, patch.object(m.time, "sleep"):
            with self.assertRaises(m.EvidenceLookupUnavailable):
                m.api_get("owner/repo", "actions/runs", "fixture")
            self.assertEqual(3, request.call_count)
        denied = m.urllib.error.HTTPError("https://api.github.com", 403, "denied", {}, None)
        with patch.object(m.urllib.request, "urlopen", side_effect=denied) as request:
            with self.assertRaises(m.EvidenceLookupUnavailable):
                m.api_get("owner/repo", "actions/runs", "fixture")
            self.assertEqual(1, request.call_count)

    def test_denied_evidence_reports_status_and_path_without_credentials(self):
        denied = m.urllib.error.HTTPError("https://api.github.com", 403, "denied", {}, None)
        with patch.object(m.urllib.request, "urlopen", side_effect=denied):
            with self.assertRaises(m.EvidenceLookupUnavailable) as caught:
                m.api_get("owner/repo", "actions/runs/7/jobs?per_page=100", "secret-fixture")
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(output=str(Path(directory) / "plan.json"))
            with self.assertRaises(SystemExit):
                m._stop_unresolved_planning(args, caught.exception)
            content = Path(args.output).read_text()
            record = m.json.loads(content)
            self.assertEqual(403, record["evidenceHttpStatus"])
            self.assertEqual("actions/runs/7/jobs", record["evidenceEndpoint"])
            self.assertNotIn("secret-fixture", content)
            self.assertNotIn("per_page", content)

    def test_rate_limited_pr_plan_blocks_before_expensive_children(self):
        error = m.EvidenceLookupUnavailable(
            "rate limited", status=403, endpoint="actions/workflows/example/runs"
        )
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(
                output=str(Path(directory) / "plan.json"),
                workflow="approved-release-security-validation.yml",
                repository="owner/repo",
                current_sha=m.subprocess.check_output(
                    ["git", "rev-parse", "HEAD"], text=True
                ).strip(),
                current_run_id=4,
                run_attempt=1,
                head_branch="repair",
                event="pull_request",
                resume_cache=None,
            )
            with patch.object(m, "prior_evidence", side_effect=error) as lookup, \
                 patch.object(m.time, "sleep") as sleeper, \
                 self.assertRaises(SystemExit) as stopped:
                m.cmd_plan(args)
            self.assertEqual(1, stopped.exception.code)
            self.assertEqual(4, lookup.call_count)
            self.assertEqual(3, sleeper.call_count)
            plan = m.json.loads(Path(args.output).read_text())
        self.assertEqual("blocked", plan["mode"])
        self.assertEqual("planner_unavailable_resume_planning_only", plan["reason"])
        self.assertEqual(403, plan["evidenceHttpStatus"])
        self.assertNotIn("gates", plan)

    def test_partial_pr_local_gate_cache_reuses_only_checkpointed_children(self):
        workflow = "approved-release-security-validation.yml"
        head = m.subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
        with tempfile.TemporaryDirectory() as directory:
            cache = Path(directory) / "validation-resume.json"
            cache.write_text(m.json.dumps({
                "schemaVersion": 2,
                "workflow": workflow,
                "headSha": head,
                "runId": 41,
                "runAttempt": 1,
                "gates": {"no-skips": {"result": "success"}},
            }))
            args = SimpleNamespace(
                output=str(Path(directory) / "plan.json"),
                workflow=workflow,
                repository="owner/repo",
                current_sha=head,
                current_run_id=42,
                run_attempt=1,
                head_branch="repair",
                event="pull_request",
                resume_cache=str(cache),
            )
            with patch.object(m, "prior_evidence") as remote:
                m.cmd_plan(args)
            remote.assert_not_called()
            plan = m.json.loads(Path(args.output).read_text())
        self.assertFalse(plan["gates"]["no-skips"]["run"])
        self.assertEqual("pr_local_gate_cache", plan["gates"]["no-skips"]["evidenceSource"])
        self.assertTrue(plan["gates"]["secret-scan"]["run"])
        self.assertTrue(plan["gates"]["composition"]["run"])

    def test_gate_cache_carries_only_proven_successful_children(self):
        workflow = "step6-openai-ads-execution-validation.yml"
        plan = {
            "workflow": workflow,
            "gates": {
                "restore": {"run": False, "producerReceipt": {"result": "success"}},
                "build": {"run": True, "receipt": {"result": "success"}},
                "tests": {"run": True, "receipt": {"result": "failure"}},
            },
        }
        payload = m._gate_cache_payload(plan, workflow, "a" * 40, 77, 2)
        self.assertEqual({"restore", "build"}, set(payload["gates"]))
        self.assertNotIn("tests", payload["gates"])

    def test_rate_limited_step5_decision_falls_back_to_full_validation(self):
        error = m.EvidenceLookupUnavailable(
            "rate limited", status=403, endpoint="actions/workflows/step5/runs"
        )
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(
                output=str(Path(directory) / "decision.json"),
                repository="owner/repo",
                current_sha="a" * 40,
                base_sha="b" * 40,
                current_run_id=4,
                head_branch="repair",
            )
            with patch.object(m, "compute_step5_decision", side_effect=error):
                m.cmd_step5_decision(args)
            decision = m.json.loads(Path(args.output).read_text())
        self.assertEqual("full", decision["mode"])
        self.assertEqual(
            "historical_evidence_unavailable_run_full_step5",
            decision["reason"],
        )
        self.assertEqual(403, decision["evidenceFallback"]["httpStatus"])

    def test_rate_limited_step5_baseline_runs_fresh_baseline(self):
        error = m.EvidenceLookupUnavailable(
            "rate limited", status=403, endpoint="actions/artifacts"
        )
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(
                output=str(Path(directory) / "baseline.json"),
                repository="owner/repo",
                base_sha="b" * 40,
            )
            with patch.object(m, "compute_step5_baseline_evidence", side_effect=error):
                m.cmd_step5_baseline(args)
            result = m.json.loads(Path(args.output).read_text())
        self.assertFalse(result["reusable"])
        self.assertEqual(
            "historical_evidence_unavailable_run_fresh_baseline",
            result["reason"],
        )
        self.assertEqual(403, result["evidenceFallback"]["httpStatus"])

    def test_rate_limited_migration_probe_plan_builds_fresh_instead_of_failing(self):
        probe_spec = importlib.util.spec_from_file_location(
            "migration_probe_package",
            ROOT / "scripts" / "migration-probe-package.py",
        )
        probe = importlib.util.module_from_spec(probe_spec)
        probe_spec.loader.exec_module(probe)
        identity = {
            "artifact": "legend-migration-probe-" + "d" * 64,
            "identity": "d" * 64,
        }
        error = probe.AUTHORITY.EvidenceLookupUnavailable(
            "rate limited", status=403, endpoint="actions/workflows/example/runs"
        )
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "outputs"
            args = [
                "migration-probe-package.py", "plan",
                "--tool-revision", "a" * 40,
                "--application-revision", "a" * 40,
                "--directory", str(Path(directory) / "probe"),
                "--output", str(output),
            ]
            with patch.object(probe.AUTHORITY, "migration_probe_identity", return_value=identity), \
                 patch.object(probe.AUTHORITY, "migration_probe_evidence", side_effect=error), \
                 patch.dict(probe.os.environ, {"GITHUB_REPOSITORY": "owner/repo"}), \
                 patch("sys.argv", args):
                probe.main()
            values = dict(
                line.split("=", 1)
                for line in output.read_text().splitlines()
                if "=" in line
            )
        self.assertEqual("true", values["needed"])
        self.assertEqual(identity["artifact"], values["artifact"])
        self.assertEqual(identity["identity"], values["identity"])

    def test_candidate_artifact_transport_failure_is_not_missing_evidence(self):
        run = {"id": 7, "head_sha": "a" * 40}
        with patch.object(m, "api_get", return_value={"workflow_runs": [run]}), \
             patch.object(m, "_trusted_lineage_run", return_value=True), \
             patch.object(m, "step5_dependency_change", return_value=[]), \
             patch.object(m, "_step5_jobs_unchanged", return_value=True), \
             patch.object(m, "_run_artifact_names", side_effect=m.EvidenceLookupUnavailable()):
            with self.assertRaises(m.EvidenceLookupUnavailable):
                m._step5_prior_candidate_evidence("owner/repo", 9, "repair", "fixture", "b" * 40)

    def test_historical_lookup_transport_failure_cannot_return_full_plan(self):
        args = SimpleNamespace(event="pull_request")
        with patch.dict(m.os.environ, {"GITHUB_TOKEN": "fixture"}), \
             patch.object(m, "_trusted_historical_runs", side_effect=m.EvidenceLookupUnavailable()):
            with self.assertRaises(m.EvidenceLookupUnavailable):
                m._apply_content_equivalent_evidence(args, {"gates": {"one": {"run": True}}})

    def test_baseline_artifact_transport_failure_is_not_missing_evidence(self):
        with patch.dict(m.os.environ, {"GITHUB_TOKEN": "fixture"}), \
             patch.object(m, "_artifact_rows", return_value=[{"workflow_run": {"id": 7}}]), \
             patch.object(m, "api_get", side_effect=m.EvidenceLookupUnavailable()):
            with self.assertRaises(m.EvidenceLookupUnavailable):
                m.compute_step5_baseline_evidence("owner/repo", "a" * 40)

    def test_only_extension_methods_create_name_only_dependency_edges(self):
        source = """
        public static Result CreateClient() => new();
        protected override Task SendAsync(Request request) => null;
        public static Result Configure(this Client client) => null;
        internal static async Task ApplyAsync<T>(this T client) => null;
        """
        self.assertEqual({"Configure", "ApplyAsync"}, m._step5_extension_method_names(source))

    def test_complete_discovery_excludes_helpers_without_hiding_new_test_classes(self):
        classes = ["Tests.RealTests", "Tests.HelperController"]
        names = ["Tests.RealTests.Check"]
        self.assertEqual(["Tests.RealTests"], m._step5_discovered_repair_classes(
            classes, names, ["scripts/validation-resume.py"]))
        self.assertIsNone(m._step5_discovered_repair_classes(
            classes, names, ["AgentPortal.Tests/NewTests.cs"]))

    def successful_steps(self, workflow):
        return {
            gate["step"]: "success"
            for gate in m.WORKFLOWS[workflow]["gates"].values()
        }

    def prior(self, sha="a" * 40):
        return {"id": 17, "head_sha": sha, "run_attempt": 1}

    def write_trx(self, rows):
        with tempfile.NamedTemporaryFile("w", suffix=".trx", delete=False) as handle:
            failed = sum(outcome == "Failed" for _, outcome in rows)
            passed = sum(outcome == "Passed" for _, outcome in rows)
            not_executed = sum(outcome == "NotExecuted" for _, outcome in rows)
            handle.write(
                '<TestRun><Results>' +
                ''.join(f'<UnitTestResult testName="{name}" outcome="{outcome}" />' for name, outcome in rows) +
                '</Results><ResultSummary outcome="Completed"><Counters ' +
                f'total="{len(rows)}" executed="{passed + failed}" passed="{passed}" failed="{failed}" ' +
                f'notExecuted="{not_executed}" error="0" timeout="0" aborted="0" disconnected="0" ' +
                'inProgress="0" pending="0" /></ResultSummary></TestRun>'
            )
            return Path(handle.name)

    def test_step5_trx_concordant_duplicate_identity_collapses_safely(self):
        path = self.write_trx([
            ("AgentPortal.Tests.ExampleTests.Case", "Passed"),
            ("AgentPortal.Tests.ExampleTests.Case", "Passed"),
        ])
        self.addCleanup(path.unlink, missing_ok=True)
        self.assertEqual(
            {"AgentPortal.Tests.ExampleTests.Case": "Passed"},
            m.read_step5_results(path),
        )

    def test_step5_trx_conflicting_duplicate_identity_fails_closed(self):
        path = self.write_trx([
            ("AgentPortal.Tests.ExampleTests.Case", "Passed"),
            ("AgentPortal.Tests.ExampleTests.Case", "Failed"),
        ])
        self.addCleanup(path.unlink, missing_ok=True)
        with self.assertRaisesRegex(ValueError, "Ambiguous duplicate test identity"):
            m.read_step5_results(path)

    def test_record_evidence_defers_current_run_403_without_fabricating_success(self):
        plan = {
            "workflow": "approved-release-security-validation.yml",
            "gates": {
                "diff-check": {
                    "step": "Verify patch whitespace integrity",
                    "run": True,
                    "reason": "gate_inputs_changed",
                }
            },
        }
        with tempfile.TemporaryDirectory() as directory:
            plan_path = Path(directory) / "plan.json"
            output_path = Path(directory) / "out.json"
            plan_path.write_text(__import__("json").dumps(plan))
            error = m.urllib.error.HTTPError(
                "https://api.github.com/example", 403, "Forbidden", {}, None
            )
            args = SimpleNamespace(
                plan=str(plan_path),
                output=str(output_path),
                repository="MYLEGND/masterapp",
                run_id=123,
            )
            with patch.object(m.urllib.request, "urlopen", side_effect=error), \
                 patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
                m.cmd_record_evidence(args)
            recorded = __import__("json").loads(output_path.read_text())
        self.assertEqual(
            "current_run_actions_observation_forbidden",
            recorded["receiptRecordingDeferred"],
        )
        self.assertNotIn("receipt", recorded["gates"]["diff-check"])

    def historical_plan_steps(self, parent_conclusion, gates, **metadata):
        run = {
            "id": 77,
            "run_attempt": 1,
            "conclusion": parent_conclusion,
        }
        args = SimpleNamespace(
            workflow="approved-release-security-validation.yml",
            repository="MYLEGND/masterapp",
        )
        artifact = "validation-resume-security-77-1"
        stored = {"workflow": args.workflow, "gates": gates, **metadata}
        def download(_repo, _run_id, _artifact, directory):
            Path(directory, "validation-resume.json").write_text(
                __import__("json").dumps(stored)
            )
        with patch.object(m, "_run_artifact_names", return_value={artifact}), \
             patch.object(m, "_download_run_artifact", side_effect=download):
            return m._historical_plan_steps(args, run, "token")

    def test_successful_parent_plan_proves_executed_child_without_jobs_api(self):
        steps = self.historical_plan_steps("success", {
            "diff-check": {
                "step": "Verify patch whitespace integrity",
                "run": True,
            }
        })
        self.assertEqual("success", steps["Verify patch whitespace integrity"])
        self.assertEqual(
            77,
            steps.producers["Verify patch whitespace integrity"]["runId"],
        )

    def test_failed_parent_retains_its_exact_recorded_child_results(self):
        gates = {}
        for index, result in enumerate(("success", "failure", "cancelled"), 1):
            gates[result] = {"step": result, "run": True, "receipt": {
                "result": result, "producingRunId": 77, "producerJobId": 900,
                "producerStepNumber": index, "recordingJobId": 900,
                "stepNumber": index, "reused": False}}
        steps = self.historical_plan_steps("failure", gates,
                                          receiptSchemaVersion=1, recordingRunId=77)
        self.assertEqual({name: name for name in gates}, dict(steps))
        self.assertEqual(900, steps.producers["success"]["jobId"])
        self.assertEqual(77, steps.producers["success"]["runId"])
        self.assertEqual(1, steps.producers["success"]["stepNumber"])

    def test_failed_parent_rejects_mismatched_or_incomplete_executed_receipts(self):
        valid = {"result": "success", "producingRunId": 77, "producerJobId": 900,
                 "producerStepNumber": 4, "recordingJobId": 900, "stepNumber": 4, "reused": False}
        for key, value in (("producingRunId", 78), ("producerJobId", None),
                           ("recordingJobId", 901), ("stepNumber", 5), ("reused", True),
                           ("result", "unproven")):
            with self.subTest(key=key):
                receipt = dict(valid, **{key: value})
                steps = self.historical_plan_steps("failure", {
                    "gate": {"step": "gate", "run": True, "receipt": receipt}},
                    receiptSchemaVersion=1, recordingRunId=77)
                self.assertNotIn("gate", steps)
        steps = self.historical_plan_steps("failure", {
            "gate": {"step": "gate", "run": True, "receipt": valid}},
            receiptSchemaVersion=1, recordingRunId=78)
        self.assertNotIn("gate", steps)

    def test_failed_parent_plan_preserves_only_prior_green_child(self):
        steps = self.historical_plan_steps("failure", {
            "executed-later-failure": {
                "step": "A gate that ran in failed parent",
                "run": True,
            },
            "preserved": {
                "step": "An older preserved green gate",
                "run": False,
                "evidenceRunId": 44,
                "producerReceipt": {
                    "result": "success",
                    "runId": 44,
                },
            },
        })
        self.assertNotIn("A gate that ran in failed parent", steps)
        self.assertEqual("success", steps["An older preserved green gate"])
        self.assertEqual(
            44,
            steps.producers["An older preserved green gate"]["runId"],
        )

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

    def test_computed_single_file_readers_do_not_poison_gate_with_entire_repository(self):
        source = """
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "Protect-Website", "Controllers", fileName);
        var text = File.ReadAllText(path);
        var info = new DirectoryInfo(Directory.GetCurrentDirectory());
        Assert.False(File.Exists(path));
        """
        self.assertNotIn("**", m._test_file_dependency_patterns(source))
        self.assertEqual(
            ("**",),
            m._test_file_dependency_patterns("Directory.GetFiles(root);"),
        )

    def test_compile_regression_does_not_claim_runtime_source_contract_files(self):
        gate = m.WORKFLOWS["masterapp-platform-architecture-validation.yml"]["gates"]["compile-regression"]
        self.assertFalse(gate["runtime_file_dependencies"])

    def test_successful_parent_is_complete_gate_proof_without_plan_artifact_download(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        run = {"id": 77, "conclusion": "success"}
        steps = m._successful_parent_steps(workflow, run)
        expected = {gate["step"] for gate in m.WORKFLOWS[workflow]["gates"].values()}
        self.assertEqual(expected, set(steps))
        self.assertTrue(all(value == "success" for value in steps.values()))
        self.assertTrue(all(row["runId"] == 77 for row in steps.producers.values()))

    def test_content_equivalent_lookup_stops_at_nearest_successful_parent(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        args = SimpleNamespace(
            event="pull_request",
            workflow=workflow,
            repository="MYLEGND/masterapp",
            current_run_id=99,
            current_sha="c" * 40,
        )
        plan = {
            "workflow": workflow,
            "gates": {
                key: {"step": gate["step"], "run": True, "reason": "no_prior_success_evidence"}
                for key, gate in m.WORKFLOWS[workflow]["gates"].items()
            },
        }
        candidate = {
            "workflow": workflow,
            "gates": {
                key: {"step": gate["step"], "run": True, "reason": "gate_inputs_changed"}
                for key, gate in m.WORKFLOWS[workflow]["gates"].items()
            },
        }
        runs = [
            {"id": 77, "head_sha": "a" * 40, "conclusion": "success", "run_attempt": 1},
            {"id": 66, "head_sha": "b" * 40, "conclusion": "success", "run_attempt": 1},
        ]
        with patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}), \
             patch.object(m, "_trusted_historical_runs", return_value=runs), \
             patch.object(m, "_historical_plan_steps",
                          side_effect=AssertionError("successful parent must not download plan artifact")), \
             patch.object(m, "_plan_against_prior", return_value=candidate) as compare, \
             patch.object(m, "merge_content_equivalent_evidence", return_value=True):
            result = m._apply_content_equivalent_evidence(args, plan)
        self.assertIs(result, plan)
        self.assertEqual(1, compare.call_count)
        self.assertEqual(1, result["historicalEvidenceRunsExamined"])

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
        config = {"gates": {"a": {"step": "Gate A"}, "b": {"step": "Gate B"}}}
        self.assertNotEqual(m._gate_execution_contract(prior, config, "a"), m._gate_execution_contract(current, config, "a"))
        self.assertEqual(m._gate_execution_contract(prior, config, "b"), m._gate_execution_contract(current, config, "b"))
        environment_edit = "env:\n  MODE: changed\n" + current
        self.assertNotEqual(m._gate_execution_contract(prior, config, "b"), m._gate_execution_contract(environment_edit, config, "b"))

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
        self.assertNotIn("scripts/validation-resume.py plan", workflow)
        self.assertEqual(1, workflow.count("scripts/validation-resume.py step5-decision"))
        self.assertIn("scripts/validation-resume.py step5-baseline", workflow)
        self.assertNotIn('gh api "/repos/$GITHUB_REPOSITORY/actions/artifacts?name=$baseline_name', workflow)
        self.assertIn("historical_evidence_unavailable_run_full_step5", workflow)
        self.assertIn("refusing to discard completed evidence and rerun the full suite", workflow)
        self.assertIn("baseline_run_required", workflow)
        self.assertIn("candidate_restore_run", workflow)
        self.assertIn("candidate_build_run", workflow)
        self.assertIn("candidate_focused_run", workflow)
        self.assertIn("candidate_full_run", workflow)
        self.assertIn("comparison_run", workflow)
        self.assertIn("Recheck only newly introduced Step 5 failure classes", workflow)
        self.assertIn("Preserve effective Step 5 candidate evidence", workflow)
        self.assertIn("Preserve effective Step 5 baseline evidence", workflow)
        self.assertIn("Preserve bounded Step 5 recovery state", workflow)
        self.assertIn("Enforce final Step 5 outcome after bounded recovery", workflow)
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
        self.assertIn("Reconcile complete immutable release transaction", release)
        import subprocess
        subprocess.run(["python3", str(ROOT / "scripts/release-workflow.py"), "--check"], check=True, capture_output=True)
        for row in m.RELEASE_TARGETS.values():
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
        self.assertIn("def candidate_validation(api, pr):", lifecycle)
        self.assertIn("run.get('status') != 'completed'", lifecycle)
        self.assertIn("run.get('conclusion') != 'success'", lifecycle)
        self.assertIn("for attempt in range(4)", lifecycle)
        self.assertNotIn("validation_neutral_path", lifecycle)
        self.assertNotIn("architecture_product_validation", lifecycle)
        self.assertNotIn("architecture_public_website_validation", lifecycle)
        self.assertNotIn("STEP6_VALIDATION_PATHS", lifecycle)
        self.assertNotIn("STEP78_VALIDATION_PATHS", lifecycle)
        self.assertNotIn("VALIDATION_NEUTRAL_PATHS =", lifecycle)

    def test_historical_run_discovery_prefers_candidate_lineage_without_pull_lookup(self):
        args = SimpleNamespace(
            event="pull_request",
            workflow="approved-release-security-validation.yml",
            repository="MYLEGND/masterapp",
            current_run_id=99,
            current_sha="b" * 40,
        )
        run = {
            "id": 77,
            "head_sha": "a" * 40,
            "status": "completed",
            "event": "pull_request",
            "path": ".github/workflows/approved-release-security-validation.yml",
            "head_repository": {"full_name": "MYLEGND/masterapp"},
            "updated_at": "2026-10-03T00:00:00Z",
        }
        with patch.object(m, "api_get", return_value={"workflow_runs": [run]}), \
             patch.object(m, "_trusted_lineage_run", return_value=True), \
             patch.object(m, "_trusted_pr_run", side_effect=AssertionError("pull lookup should not run")):
            rows = m._trusted_historical_runs(args, "token")
        self.assertEqual([77], [row["id"] for row in rows])

    def test_validation_resume_test_change_requires_architecture_and_security(self):
        topology = m.required_validation_topology([
            "scripts/test-validation-resume.py",
        ])
        self.assertEqual(
            {
                ".github/workflows/masterapp-platform-architecture-validation.yml",
                ".github/workflows/approved-release-security-validation.yml",
            },
            set(topology["required"]),
        )

    def test_terminal_lifecycle_wake_is_control_only_and_requires_owning_validation(self):
        path="scripts/wake-release-lifecycle.py"
        topology=m.required_validation_topology([path])
        self.assertEqual(
            {
                ".github/workflows/masterapp-platform-architecture-validation.yml",
                ".github/workflows/approved-release-security-validation.yml",
            },
            set(topology["required"]),
        )
        self.assertTrue(topology["releaseControlAuthorityChange"])
        self.assertEqual((), m.release_targets_for_paths([path]))
        self.assertTrue(m.release_control_only_path(path))

    def test_lifecycle_control_change_requires_architecture_and_security(self):
        topology = m.required_validation_topology([
            "scripts/release-lifecycle.py",
            "scripts/test-release-lifecycle.py",
        ])
        self.assertEqual(
            {
                ".github/workflows/masterapp-platform-architecture-validation.yml",
                ".github/workflows/approved-release-security-validation.yml",
            },
            set(topology["required"]),
        )
        self.assertTrue(topology["releaseControlAuthorityChange"])

    def test_release_package_change_requires_step5_and_security(self):
        topology = m.required_validation_topology([
            "scripts/release-package.py",
        ])
        self.assertEqual(
            {
                ".github/workflows/masterapp-platform-architecture-validation.yml",
                ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
                ".github/workflows/approved-release-security-validation.yml",
            },
            set(topology["required"]),
        )

    def test_deploy_control_test_change_requires_architecture_and_security(self):
        topology = m.required_validation_topology([
            "scripts/test-deploy-approved-app.py",
        ])
        self.assertEqual(
            {
                ".github/workflows/masterapp-platform-architecture-validation.yml",
                ".github/workflows/approved-release-security-validation.yml",
            },
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

    def test_control_only_descendant_reuses_package_without_builder_equivalence_recheck(self):
        producer = "a" * 40
        revision = "b" * 40
        run = {
            "id": 77,
            "head_sha": producer,
            "updated_at": "2026-10-03T00:00:00Z",
        }
        package_identity = "c" * 64
        artifact = "founder-diagnostics-packages-" + package_identity
        def api_get(_repository, path, _token):
            if path.startswith("actions/workflows/"):
                return {"workflow_runs": [run]}
            raise AssertionError(path)
        with patch.object(m, "api_get", side_effect=api_get), \
             patch.object(m, "_trusted_lineage_run", return_value=True), \
             patch.object(m, "_successful_package_child", return_value=True), \
             patch.object(m, "git_changed", return_value=["scripts/test-validation-resume.py"]), \
             patch.object(m, "_run_artifact_names", return_value={artifact}), \
             patch.object(m, "package_inputs_compatible",
                          side_effect=AssertionError("No byte input changed")):
            result = m.compatible_package_producer(
                "MYLEGND/masterapp", revision, "token"
            )
        self.assertTrue(result["reusable"])
        self.assertEqual(77, result["runId"])
        self.assertEqual(package_identity, result["packageIdentity"])

    def test_compatible_package_producer_uses_trusted_run_artifacts_not_repository_artifact_listing(self):
        producer = "a" * 40
        revision = "b" * 40
        run = {
            "id": 77,
            "head_sha": producer,
            "updated_at": "2026-10-03T00:00:00Z",
        }
        package_identity = "c" * 64
        artifact = "founder-diagnostics-packages-" + package_identity
        seen = []
        def api_get(_repository, path, _token):
            seen.append(path)
            if path.startswith("actions/workflows/"):
                return {"workflow_runs": [run]}
            raise AssertionError(path)
        with patch.object(m, "api_get", side_effect=api_get), \
             patch.object(m, "_trusted_lineage_run", return_value=True), \
             patch.object(m, "_successful_package_child", return_value=True), \
             patch.object(m, "package_inputs_compatible", return_value=True), \
             patch.object(m, "_run_artifact_names", return_value={artifact}), \
             patch.object(m.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout="")):
            result = m.compatible_package_producer("MYLEGND/masterapp", revision, "token")
        self.assertTrue(result["reusable"])
        self.assertEqual(77, result["runId"])
        self.assertEqual(package_identity, result["packageIdentity"])
        self.assertFalse(any(path.startswith("actions/artifacts?") for path in seen))

    def test_migration_probe_evidence_uses_trusted_run_artifacts_not_repository_artifact_listing(self):
        identity = {
            "schemaVersion": 1,
            "runtimeIdentity": "a" * 64,
            "toolIdentity": "b" * 64,
            "executionIdentity": "c" * 64,
            "identity": "d" * 64,
            "artifact": "legend-migration-probe-" + "d" * 64,
        }
        run = {"id": 88, "head_sha": "e" * 40, "updated_at": "2026-10-03T00:00:00Z"}
        seen = []
        def api_get(_repository, path, _token):
            seen.append(path)
            if path.startswith("actions/workflows/"):
                return {"workflow_runs": [run]}
            raise AssertionError(path)
        with patch.object(m, "api_get", side_effect=api_get), \
             patch.object(m, "_trusted_lineage_run", return_value=True), \
             patch.object(m, "migration_probe_identity", return_value=identity), \
             patch.object(m, "_run_artifact_names", return_value={identity["artifact"]}), \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            result = m.migration_probe_evidence("MYLEGND/masterapp", identity)
        self.assertTrue(result["reusable"])
        self.assertEqual(88, result["runId"])
        self.assertFalse(any(path.startswith("actions/artifacts?") for path in seen))
        self.assertFalse(any("/jobs?" in path for path in seen))

    def test_current_probe_identity_still_requires_child_authority(self):
        with patch.object(m.subprocess, "check_output", return_value=""), \
             patch.object(m, "git_show_file", return_value="jobs:\n  other:\n    runs-on: ubuntu-latest\n"):
            with self.assertRaisesRegex(m.MigrationProbeAuthorityMissing, "child authority missing"):
                m.migration_probe_identity("a" * 40, "a" * 40)

    def test_probe_history_without_child_does_not_abort_new_candidate(self):
        identity = {"identity": "d" * 64, "artifact": "legend-migration-probe-" + "d" * 64}
        old = {"id": 88, "head_sha": "e" * 40, "updated_at": "2026-10-03T01:00:00Z"}
        valid = {"id": 77, "head_sha": "f" * 40, "updated_at": "2026-10-03T00:00:00Z"}
        with patch.object(m, "api_get", return_value={"workflow_runs": [old, valid]}), \
             patch.object(m, "_trusted_lineage_run", return_value=True), \
             patch.object(m, "migration_probe_identity", side_effect=[m.MigrationProbeAuthorityMissing("missing"), identity]), \
             patch.object(m, "_run_artifact_names", return_value={identity["artifact"]}) as artifacts, \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            result = m.migration_probe_evidence("MYLEGND/masterapp", identity)
        self.assertTrue(result["reusable"])
        self.assertEqual(77, result["runId"])
        artifacts.assert_called_once_with("MYLEGND/masterapp", 77, "token")

    def test_probe_history_without_child_requires_fresh_build(self):
        identity = {"identity": "d" * 64, "artifact": "legend-migration-probe-" + "d" * 64}
        run = {"id": 88, "head_sha": "e" * 40}
        with patch.object(m, "api_get", return_value={"workflow_runs": [run]}), \
             patch.object(m, "_trusted_lineage_run", return_value=True), \
             patch.object(m, "migration_probe_identity", side_effect=m.MigrationProbeAuthorityMissing("missing")), \
             patch.object(m, "_run_artifact_names") as artifacts, \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            self.assertEqual({"reusable": False, "artifact": identity["artifact"]},
                             m.migration_probe_evidence("MYLEGND/masterapp", identity))
        artifacts.assert_not_called()

    def test_probe_history_other_identity_errors_remain_fatal(self):
        with patch.object(m, "api_get", return_value={"workflow_runs": [{"id": 88, "head_sha": "e" * 40}]}), \
             patch.object(m, "_trusted_lineage_run", return_value=True), \
             patch.object(m, "migration_probe_identity", side_effect=ValueError("runtime mismatch")), \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            with self.assertRaisesRegex(ValueError, "runtime mismatch"):
                m.migration_probe_evidence("MYLEGND/masterapp", {})

    def test_trusted_lineage_run_requires_same_repo_workflow_and_ancestor(self):
        run = {
            "path": ".github/workflows/" + m.PACKAGE_VALIDATION_WORKFLOW,
            "event": "pull_request",
            "status": "completed",
            "head_sha": "a" * 40,
            "head_repository": {"full_name": "MYLEGND/masterapp"},
        }
        with patch.object(m.subprocess, "run", return_value=SimpleNamespace(returncode=0)):
            self.assertTrue(m._trusted_lineage_run(
                "MYLEGND/masterapp", run,
                ".github/workflows/" + m.PACKAGE_VALIDATION_WORKFLOW,
                "b" * 40,
            ))
        with patch.object(m.subprocess, "run", return_value=SimpleNamespace(returncode=1)):
            self.assertFalse(m._trusted_lineage_run(
                "MYLEGND/masterapp", run,
                ".github/workflows/" + m.PACKAGE_VALIDATION_WORKFLOW,
                "b" * 40,
            ))
        foreign = dict(run, head_repository={"full_name": "outsider/fork"})
        with patch.object(m.subprocess, "run", return_value=SimpleNamespace(returncode=0)):
            self.assertFalse(m._trusted_lineage_run(
                "MYLEGND/masterapp", foreign,
                ".github/workflows/" + m.PACKAGE_VALIDATION_WORKFLOW,
                "b" * 40,
            ))

    def test_package_canary_preserves_original_producer_for_control_only_change(self):
        producer = {'runId': 91, 'revision': 'a' * 40, 'packageIdentity': 'c' * 64,
                    'artifact': 'original-package', 'reason': 'dependency_equivalent_immutable_package_producer'}
        with patch.object(m, 'compatible_package_producer', return_value=producer), \
             patch.dict(m.os.environ, {'GITHUB_TOKEN': 'token'}):
            plan = m.compute_package_canary_plan('MYLEGND/masterapp', 'b' * 40, '0' * 40, 100, 'fix')
        self.assertFalse(plan['needed'])
        self.assertEqual('a' * 40, plan['evidenceHeadSha'])
        self.assertEqual('c' * 64, plan['packageIdentity'])
        self.assertEqual('original-package', plan['exactPackageArtifact'])

    def test_package_canary_requires_build_when_no_compatible_artifact_exists(self):
        with patch.object(m, 'compatible_package_producer', return_value=None), \
             patch.object(m, 'git_changed', return_value=['scripts/test-release-policy.py']), \
             patch.dict(m.os.environ, {'GITHUB_TOKEN': 'token'}):
            plan = m.compute_package_canary_plan('MYLEGND/masterapp', 'b' * 40, '0' * 40, 100, 'fix')
        self.assertTrue(plan['needed'])
        self.assertEqual('compatible_immutable_package_missing', plan['reason'])
        self.assertEqual([], plan['changedInputs'])

    def test_package_canary_reports_application_inputs_when_no_compatible_package(self):
        with patch.object(m, 'compatible_package_producer', return_value=None), \
             patch.object(m, 'git_changed', return_value=['AgentPortal/Program.cs']), \
             patch.dict(m.os.environ, {'GITHUB_TOKEN': 'token'}):
            plan = m.compute_package_canary_plan('MYLEGND/masterapp', 'b' * 40, '0' * 40, 100, 'fix')
        self.assertTrue(plan['needed'])
        self.assertEqual(['AgentPortal/Program.cs'], plan['changedInputs'])

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

        with patch.object(m, "_successful_package_child", return_value=True), \
             patch.object(m, "_trusted_pr_run", return_value=True), \
             patch.object(m, "_artifact_rows", side_effect=artifacts), \
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
        with patch.object(m, "_successful_package_child", return_value=True), \
             patch.object(m, "_artifact_rows", return_value=[{"workflow_run": {"id": 77}}]), \
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

    def test_inline_execution_environment_and_defaults_invalidate_proof(self):
        workflow = "step5-isolated-conversion-mapping-validation.yml"
        text = (ROOT / ".github/workflows" / workflow).read_text()
        for field, first, second in (
            ("env", "{TEST_OVERRIDE: a}", "{TEST_OVERRIDE: b}"),
            ("defaults", "{run: {shell: bash}}", "{run: {shell: sh}}"),
        ):
            before = f"{field}: {first}\n" + text
            after = f"{field}: {second}\n" + text
            self.assertNotEqual(m._step5_execution_contract(before), m._step5_execution_contract(after))
            self.assertNotEqual(
                m._gate_execution_contract(before, m.WORKFLOWS[workflow], "candidate-restore"),
                m._gate_execution_contract(after, m.WORKFLOWS[workflow], "candidate-restore"))

    def test_founder_cloudflare_release_trigger_is_canonical_and_narrow(self):
        self.assertTrue(m.founder_cloudflare_release_required([
            "Legend-Cloudflare/src/runtime/registry.mjs",
        ]))
        self.assertTrue(m.founder_cloudflare_release_required([
            "Legend-Cloudflare/wrangler.founder-baseline.jsonc",
        ]))
        self.assertFalse(m.founder_cloudflare_release_required([
            "scripts/deploy-founder-cloudflare.py",
        ]))
        self.assertFalse(m.founder_cloudflare_release_required([
            "Legend-Cloudflare/tests/runtime/qualification-mode.test.mjs",
            "Legend-Cloudflare/scripts/founder-canary.mjs",
            "AgentPortal/Program.cs",
        ]))

    def test_founder_cloudflare_release_scope_is_portal_only(self):
        self.assertEqual(
            (),
            m.release_targets_for_paths(["scripts/deploy-founder-cloudflare.py"]),
        )
        self.assertEqual(
            ("masterapp-portal",),
            m.release_targets_for_paths(["Legend-Cloudflare/src/runtime/registry.mjs"]),
        )
        self.assertEqual(
            ("masterapp-client",),
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

    def test_release_web_contract_change_reruns_release_web_with_its_local_dotnet_build_chain(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["tests/legend-connect/example.test.mjs"],
            "prior_run",
        )
        for key in ("release-web-contracts", "compile-regression", "restore-dotnet"):
            self.assertTrue(plan["gates"][key]["run"], key)
        for key, gate in plan["gates"].items():
            if key not in {"release-web-contracts", "compile-regression", "restore-dotnet"}:
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
            current_sha="d" * 40,
        )

        def response(repository, path, token):
            self.assertEqual(args.repository, repository)
            self.assertEqual("token", token)
            if path.startswith("actions/workflows/"):
                return {
                    "workflow_runs": [{
                        "id": 88,
                        "event": "pull_request",
                        "conclusion": "failure",
                        "status": "completed",
                        "path": m.WORKFLOW_PATHS[args.workflow],
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
                        "sha": "d" * 40,
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
             patch.object(m, "_trusted_pr_run", return_value=True), \
             patch.object(m, "_step5_artifact_complete", return_value=True), \
             patch.object(m, "step5_dependency_change", return_value=[]):
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
             patch.object(m, "_trusted_pr_run", return_value=True), \
             patch.object(m, "_step5_artifact_complete", return_value=True), \
             patch.object(m, "step5_dependency_change", return_value=None):
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
             patch.object(m, "step5_dependency_change", return_value=[failing_class]):
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
            "replace_only_dependency_invalidated_classes",
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


class Step5DependencyBehaviorTests(unittest.TestCase):
    def setUp(self):
        import tempfile
        import os
        self.temp = tempfile.TemporaryDirectory()
        self.old = os.getcwd()
        os.chdir(self.temp.name)
        self.git("init", "-q")
        self.git("config", "user.email", "fixture@example.invalid")
        self.git("config", "user.name", "Fixture")
        self.write("AgentPortal.Tests/AgentPortal.Tests.csproj", '<Project><ItemGroup><None Include="../.github/workflows/all-intentional-direct-release-20260918.yml" Link="release.yml" CopyToOutputDirectory="PreserveNewest" /></ItemGroup></Project>')
        self.write("AgentPortal.Tests/One.cs", 'namespace AgentPortal.Tests; public class One { [Fact] public void Case() { Shared.Value(); } }')
        self.write("AgentPortal.Tests/Two.cs", 'namespace AgentPortal.Tests; public class Two { [Fact] public void Case() {} }')
        self.write("AgentPortal.Tests/Shared.cs", 'namespace AgentPortal.Tests; public class Shared { public void Value() {} }')
        self.write("AgentPortal.Tests/Release.cs", 'namespace AgentPortal.Tests; public class Release { [Fact] public void Case() { File.ReadAllText("release.yml"); } }')
        self.write("AgentPortal.Tests/Direct.cs", 'namespace AgentPortal.Tests; public class Direct { [Fact] public void Case() { File.ReadAllText("direct-release-request.json"); } }')
        self.write(".github/workflows/all-intentional-direct-release-20260918.yml", "initial")
        self.write("Docs/releases/direct-release-request.json", "initial")
        self.write("scripts/release-lifecycle.py", "initial")
        self.write("scripts/validation-resume.py", (ROOT / "scripts/validation-resume.py").read_text())
        self.write(m.WORKFLOW_PATHS["step5-isolated-conversion-mapping-validation.yml"],
                   (ROOT / m.WORKFLOW_PATHS["step5-isolated-conversion-mapping-validation.yml"]).read_text())
        self.base = self.commit()

    def tearDown(self):
        import os
        os.chdir(self.old)
        self.temp.cleanup()

    def git(self, *args):
        import subprocess
        return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout.strip()

    def write(self, path, content):
        file = Path(path)
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text(content)

    def commit(self):
        self.git("add", ".")
        self.git("commit", "-qm", "fixture", "--allow-empty")
        return self.git("rev-parse", "HEAD")

    def test_only_changed_test_class_invalidates_its_proof(self):
        path = "AgentPortal.Tests/One.cs"
        self.write(path, Path(path).read_text().replace("Shared.Value();", "Shared.Value(); Shared.Value();"))
        self.assertEqual(["AgentPortal.Tests.One"], m.step5_dependency_change(self.base, self.commit()))

    def test_shared_fixture_change_requires_full_proof(self):
        path = "AgentPortal.Tests/Shared.cs"
        self.write(path, Path(path).read_text().replace("Value() {}", "Value() { return; }"))
        self.assertIsNone(m.step5_dependency_change(self.base, self.commit()))

    def test_release_workflow_only_reruns_its_control_consumers(self):
        self.write(".github/workflows/all-intentional-direct-release-20260918.yml", "changed")
        head = self.commit()
        self.assertEqual(["AgentPortal.Tests.Release"], m.step5_dependency_change(self.base, head))
        self.assertFalse(m._step5_baseline_inputs_equivalent(self.base, head))

    def test_baseline_short_circuit_preserves_dependency_decision(self):
        for path in ('Docs/releases/direct-release-request.json',
                     'scripts/release-lifecycle.py', 'AgentPortal/Program.cs'):
            with self.subTest(path=path):
                self.write(path, 'changed')
                head = self.commit()
                self.assertEqual(m.step5_dependency_change(self.base, head) == [],
                                 m._step5_baseline_inputs_equivalent(self.base, head))

    def test_direct_repository_read_is_not_neutral_documentation(self):
        self.write("Docs/releases/direct-release-request.json", "changed")
        self.assertEqual(["AgentPortal.Tests.Direct"], m.step5_dependency_change(self.base, self.commit()))

    def test_unconsumed_control_change_and_equivalent_base_preserve_suite(self):
        self.write("scripts/release-lifecycle.py", "changed")
        head = self.commit()
        self.assertEqual([], m.step5_dependency_change(self.base, head))
        self.assertTrue(m._step5_baseline_inputs_equivalent(self.base, head))
        self.assertEqual([], m.step5_dependency_change(head, self.commit()))

    def test_control_only_change_short_circuits_before_test_archive(self):
        path = "scripts/validation-resume.py"
        self.write(path, Path(path).read_text() + "\n# control-only fixture change\n")
        head = self.commit()
        original_run = m.subprocess.run

        def guarded_run(args, *pargs, **kwargs):
            if list(args[:2]) == ["git", "archive"]:
                raise AssertionError("Step 5 control-authority proof must not archive the test graph")
            return original_run(args, *pargs, **kwargs)

        with patch.object(m.subprocess, "run", side_effect=guarded_run):
            self.assertEqual([], m.step5_dependency_change(self.base, head))

    def test_manifest_content_identity_survives_unrelated_commit(self):
        workflow = "step5-isolated-conversion-mapping-validation.yml"
        prior = m.gate_dependency_manifests(workflow, self.base)
        self.write("scripts/release-lifecycle.py", "control-only")
        current = m.gate_dependency_manifests(workflow, self.commit())
        self.assertEqual(prior["candidate-full"]["contentIdentity"], current["candidate-full"]["contentIdentity"])
        self.write(".github/workflows/all-intentional-direct-release-20260918.yml", "new-control")
        changed = m.gate_dependency_manifests(workflow, self.commit())
        self.assertNotEqual(prior["candidate-full"]["sourceIdentity"], changed["candidate-full"]["sourceIdentity"])

    def test_real_gate_planner_invalidates_newly_derived_control_input(self):
        workflow = "step5-isolated-conversion-mapping-validation.yml"
        self.write(".github/workflows/all-intentional-direct-release-20260918.yml", "changed contract")
        current = self.commit()
        steps = {gate["step"]: "success" for gate in m.WORKFLOWS[workflow]["gates"].values()}
        plan = m._plan_against_prior(workflow, current, {"id": 71, "head_sha": self.base}, steps, "fixture")
        self.assertTrue(plan["gates"]["candidate-full"]["run"])
        self.assertEqual("dependency_identity_changed", plan["gates"]["candidate-full"]["reason"])
        self.assertTrue(plan["gates"]["comparison"]["run"])
        self.assertFalse(plan["gates"]["candidate-focused"]["run"])

    def test_post_gate_receipt_change_does_not_invalidate_suite_execution(self):
        workflow = "step5-isolated-conversion-mapping-validation.yml"
        path = m.WORKFLOW_PATHS[workflow]
        # A validation-job-only change must not replace the candidate job proof.
        self.write(path, Path(path).read_text().replace("      - name: Preserve effective Step 5 baseline evidence", "      - name: Preserve effective Step 5 baseline evidence (receipt metadata)"))
        current = self.commit()
        steps = {gate["step"]: "success" for gate in m.WORKFLOWS[workflow]["gates"].values()}
        plan = m._plan_against_prior(workflow, current, {"id": 71, "head_sha": self.base}, steps, "fixture")
        self.assertFalse(plan["gates"]["candidate-full"]["run"])
        self.assertTrue(plan["gates"]["comparison"]["run"])

    def test_execution_contract_rejects_unknown_mutation_step(self):
        text = Path(m.WORKFLOW_PATHS["step5-isolated-conversion-mapping-validation.yml"]).read_text()
        mutated = text.replace("      - name: Run full AgentPortal candidate suite", "      - name: Unknown mutation\n        run: touch AgentPortal/Program.cs\n\n      - name: Run full AgentPortal candidate suite")
        self.assertNotEqual(m._step5_execution_contract(text), m._step5_execution_contract(mutated))
        rescheduled = text.replace("if: needs.plan.outputs.comparison_run == 'true' && needs.baseline-evidence.outputs.reusable != 'true'", "if: false")
        self.assertEqual(m._step5_execution_contract(text), m._step5_execution_contract(rescheduled))

    def test_assembly_global_and_application_inputs_fail_closed(self):
        path = "AgentPortal.Tests/One.cs"
        self.write(path, '[assembly: CollectionBehavior(DisableTestParallelization = true)]' + Path(path).read_text())
        self.assertIsNone(m.step5_dependency_change(self.base, self.commit()))
        self.git("reset", "--hard", self.base)
        self.write("Domain/Entity.cs", "changed")
        self.assertIsNone(m.step5_dependency_change(self.base, self.commit()))


class Step5ChildEvidenceTests(unittest.TestCase):
    @staticmethod
    def trx(outcomes, summary="Completed"):
        rows = ''.join(f'<UnitTestResult testName="{name}" outcome="{outcome}" />' for name, outcome in outcomes.items())
        return f'<TestRun><Results>{rows}</Results><ResultSummary outcome="{summary}"><Counters total="{len(outcomes)}" /></ResultSummary></TestRun>'

    def test_receipt_preserves_producer_and_records_only_observed_runtime(self):
        import tempfile
        import json
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "receipt.json"
            path.write_text(json.dumps({"priorRunId": 71, "gates": {
                "green": {"step": "Green child", "run": False, "evidenceRunId": 71,
                          "producerReceipt": {"result": "success", "jobId": 700, "runId": 71, "stepNumber": 5}},
                "failed": {"step": "Failed child", "run": True},
            }}))
            jobs = {"jobs": [{"id": 901, "steps": [
                {"name": "Green child", "conclusion": "success", "number": 2},
                {"name": "Failed child", "conclusion": "failure", "number": 3},
            ]}]}
            with patch.dict(m.os.environ, {"GITHUB_TOKEN": "fixture"}), \
                 patch.object(m, "api_get", return_value=jobs), \
                 patch.object(m.subprocess, "run", side_effect=FileNotFoundError):
                m.cmd_record_evidence(SimpleNamespace(plan=str(path), output=str(path), repository="MYLEGND/masterapp", run_id=99))
            evidence = json.loads(path.read_text())["gates"]
            self.assertEqual(71, evidence["green"]["receipt"]["producingRunId"])
            self.assertEqual(700, evidence["green"]["receipt"]["producerJobId"])
            self.assertEqual("success", evidence["green"]["receipt"]["result"])
            self.assertIsNone(evidence["green"]["receipt"]["actualToolchain"])
            self.assertEqual("failure", evidence["failed"]["receipt"]["result"])
            self.assertNotIn("dotnet", evidence["failed"]["receipt"]["actualToolchain"])

    def test_partial_aborted_and_empty_trx_are_rejected(self):
        import tempfile
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "candidate.trx"
            for content in (self.trx({"Suite.Case": "Passed"}, "Aborted"), "<TestRun />", self.trx({})):
                path.write_text(content)
                with self.assertRaises(ValueError):
                    m.read_step5_results(path)

    def test_cross_branch_search_skips_incompatible_and_untrusted_parent(self):
        repository = "MYLEGND/masterapp"
        def run(number, branch, conclusion="failure", trusted=True):
            sha = str(number) * 40
            return {"id": number, "head_sha": sha, "head_branch": branch,
                "path": m.WORKFLOW_PATHS["step5-isolated-conversion-mapping-validation.yml"],
                "event": "pull_request", "status": "completed", "conclusion": conclusion,
                "head_repository": {"full_name": repository if trusted else "outsider/fork"},
                "pull_requests": [{"base": {"ref": m.TRUSTED_PR_BASE}, "head": {"sha": sha, "repo": {"full_name": repository}}}]}
        runs = [run(3, "fork", trusted=False), run(2, "new-incompatible"), run(1, "older-compatible")]
        def api(repo, path, token):
            if "workflows/" in path:
                self.assertNotIn("branch=", path)
                return {"workflow_runs": runs}
            if "jobs?" in path:
                return {"jobs": [{"steps": [{"name": "Preserve completed candidate results", "conclusion": "success"}]}]}
            raise AssertionError(path)
        def download(repo, number, artifact, directory):
            (directory / "candidate.trx").write_text(self.trx({"AgentPortal.Tests.One.Case": "Passed"}))
        with patch.object(m, "api_get", side_effect=api), \
             patch.object(m, "step5_dependency_change", side_effect=lambda prior, current: [] if prior == "1" * 40 else None), \
             patch.object(m, "_step5_jobs_unchanged", return_value=True), \
             patch.object(m, "_run_artifact_names", side_effect=lambda repo, number, token: {"step5-candidate-" + str(number) * 40}), \
             patch.object(m, "_download_run_artifact", side_effect=download):
            result = m._step5_prior_candidate_evidence(repository, 99, "current", "token", "a" * 40)
        self.assertEqual(1, result["runId"])

    def test_green_repaired_child_survives_failed_sibling_comparison(self):
        import tempfile
        import subprocess
        import os
        workflow = (ROOT / ".github/workflows/step5-isolated-conversion-mapping-validation.yml").read_text()
        block = workflow.split("      - name: Prove Step 5 adds no full-suite failures", 1)[1]
        body = block.split("          python3 - <<'PY'\n", 1)[1].split("          PY\n", 1)[0]
        body = "\n".join(line[10:] for line in body.splitlines())
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for directory, filename, outcomes in (
                ("step5-prior-candidate", "candidate.trx", {"AgentPortal.Tests.One.Case": "Failed", "AgentPortal.Tests.Two.Case": "Failed"}),
                ("step5-prior-baseline", "baseline.trx", {"AgentPortal.Tests.One.Case": "Passed", "AgentPortal.Tests.Two.Case": "Passed"}),
                ("step5-repair", "repair.trx", {"AgentPortal.Tests.One.Case": "Passed"}),
            ):
                target = root / directory
                target.mkdir()
                (target / filename).write_text(self.trx(outcomes))
            body = body.replace("/tmp/step5-", str(root / "step5-"))
            result = subprocess.run(["python3", "-c", body], cwd=ROOT, capture_output=True, text=True,
                env={**os.environ, "VALIDATION_MODE": "repair", "REPAIR_CLASSES": "AgentPortal.Tests.One", "GITHUB_OUTPUT": str(root / "outputs")})
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            effective = m.read_step5_results(root / "step5-effective/candidate.trx")
            self.assertEqual("Passed", effective["AgentPortal.Tests.One.Case"])
            self.assertEqual("Failed", effective["AgentPortal.Tests.Two.Case"])
            outputs = (root / "outputs").read_text()
            self.assertIn("effective_evidence=true", outputs)
            self.assertIn("introduced=true", outputs)
            self.assertIn("introduced_classes=AgentPortal.Tests.Two", outputs)


if __name__ == "__main__":
    unittest.main()
