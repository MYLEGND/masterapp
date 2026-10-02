#!/usr/bin/env python3
"""Adversarial tests for the single protected approved-branch release lifecycle."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

spec = importlib.util.spec_from_file_location(
    "lifecycle", Path(__file__).with_name("release-lifecycle.py")
)
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


def canonical_name(key):
    return m.VALIDATION_AUTHORITY.RELEASE_TARGETS[key]["releaseName"]


def successful_jobs(target_step="Publish selected head as one transaction"):
    return [
        {"name": "discover-live", "conclusion": "success", "steps": []},
        {
            "name": "release",
            "conclusion": "success",
            "steps": [
                {"name": target_step, "conclusion": "success"},
                {"name": "Verify every deployed target and collect all failures", "conclusion": "success"},
                {"name": "Enforce complete direct deployment outcome", "conclusion": "success"},
            ],
        },
    ]


class Api:
    repo = "MYLEGND/masterapp"

    def __init__(self):
        self.refs = {m.APPROVED: "a" * 40}
        self.pages_map = {}
        self.api_map = {}
        self.dispatched = []

    def ref(self, name):
        return self.refs[name]

    def pages(self, path, key=None):
        value = self.pages_map.get(path, [])
        if key and isinstance(value, dict):
            return value.get(key, [])
        return value

    def api(self, path, data=None, method=None):
        value = self.api_map.get(path)
        if callable(value):
            return value(data, method)
        if value is None:
            return {}
        return value

    def dispatch(self, workflow, inputs=None):
        self.dispatched.append((workflow, inputs or {}))


class DirectAuthorization(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.old = Path.cwd()
        os.chdir(self.tmp.name)
        m.git("init", "-b", m.APPROVED)
        m.git("config", "user.name", "Test")
        m.git("config", "user.email", "test@example.invalid")
        Path("base.txt").write_text("base")
        m.git("add", ".")
        m.git("commit", "-m", "base")
        self.base = m.git("rev-parse", "HEAD").stdout.strip()

    def tearDown(self):
        os.chdir(self.old)
        self.tmp.cleanup()

    def authorize(self):
        path = Path("Docs/releases/direct-release-request.json")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps({"releaseMode": "approved-only", "targets": [canonical_name("portal")]}))
        m.git("add", str(path))
        m.git("commit", "-m", "authorize release")
        return m.git("rev-parse", "HEAD").stdout.strip()

    def test_request_only_authorization_is_exact(self):
        sha = self.authorize()
        self.assertTrue(m.direct_only_request(sha))
        Path("other.txt").write_text("not release authority")
        m.git("add", "other.txt")
        m.git("commit", "-m", "ordinary change")
        self.assertFalse(m.direct_only_request(m.git("rev-parse", "HEAD").stdout.strip()))

    def test_single_parent_authorization_rejects_extra_files(self):
        path = Path("Docs/releases/direct-release-request.json")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps({"releaseMode": "approved-only"}))
        Path("extra.txt").write_text("extra")
        m.git("add", ".")
        m.git("commit", "-m", "bad combined authorization")
        self.assertFalse(m.direct_only_request(m.git("rev-parse", "HEAD").stdout.strip()))


    def test_product_merge_can_carry_release_authorization_with_application_files(self):
        m.git("checkout", "-b", "product")
        path = Path("Docs/releases/direct-release-request.json")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps({
            "releaseMode": "approved-only",
            "targets": [canonical_name("portal")],
        }))
        Path("product.txt").write_text("validated product change")
        m.git("add", ".")
        m.git("commit", "-m", "validated product plus release request")
        m.git("checkout", m.APPROVED)
        m.git("merge", "--no-ff", "product", "-m", "merge validated product")
        merged = m.git("rev-parse", "HEAD").stdout.strip()

        self.assertTrue(m.direct_only_request(merged))

    def test_product_merge_without_request_change_is_not_new_authorization(self):
        self.authorize()
        m.git("checkout", "-b", "product")
        Path("product.txt").write_text("validated product change")
        m.git("add", ".")
        m.git("commit", "-m", "validated product only")
        m.git("checkout", m.APPROVED)
        m.git("merge", "--no-ff", "product", "-m", "merge validated product")
        merged = m.git("rev-parse", "HEAD").stdout.strip()

        self.assertFalse(m.direct_only_request(merged))


class BranchSafety(unittest.TestCase):
    def branch(self, name="work", sha="b" * 40, protected=False):
        return {"name": name, "commit": {"sha": sha}, "protected": protected}

    @patch.object(m, "ancestor", return_value=True)
    def test_fully_preserved_branch_is_eligible(self, _):
        ok, reason = m.eligible(
            self.branch(), "a" * 40,
            [{"revision": "c" * 40}], set(), set(), set()
        )
        self.assertTrue(ok)
        self.assertIn("approved changes", reason)

    @patch.object(m, "ancestor", side_effect=[False])
    def test_unique_history_is_never_deleted(self, _):
        ok, reason = m.eligible(
            self.branch(), "a" * 40,
            [{"revision": "c" * 40}], set(), set(), set()
        )
        self.assertFalse(ok)
        self.assertIn("unique history", reason)

    def test_approved_and_protected_branches_are_never_cleanup_candidates(self):
        for branch in (
            self.branch(m.APPROVED),
            self.branch("protected-work", protected=True),
        ):
            ok, _ = m.eligible(branch, "a" * 40, [{"revision": "c" * 40}], set(), set(), set())
            self.assertFalse(ok)

    @patch.object(m, "ancestor", return_value=True)
    def test_open_active_or_failed_branch_is_retained(self, _):
        branch = self.branch()
        for open_refs, active, failed in [
            ({"work"}, set(), set()),
            (set(), {"work"}, set()),
            (set(), set(), {"work"}),
        ]:
            ok, _ = m.eligible(branch, "a" * 40, [{"revision": "c" * 40}], open_refs, active, failed)
            self.assertFalse(ok)

    @patch.object(m, "ancestor", side_effect=[True, False])
    def test_branch_not_covered_by_every_live_revision_is_retained(self, _):
        ok, reason = m.eligible(
            self.branch(), "a" * 40,
            [{"revision": "c" * 40}, {"revision": "d" * 40}],
            set(), set(), set()
        )
        self.assertFalse(ok)
        self.assertIn("every live", reason)


class IntegrationReplaySafety(unittest.TestCase):
    def merged_pr(self, api, merge_sha="c" * 40):
        return {
            "number": 382,
            "state": "closed",
            "draft": False,
            "merged_at": "2026-10-02T15:19:07Z",
            "merge_commit_sha": merge_sha,
            "author_association": "OWNER",
            "base": {"ref": m.APPROVED},
            "head": {
                "ref": "perf/release-publication-fastpath-20261002",
                "sha": "b" * 40,
                "repo": {"full_name": api.repo},
            },
        }

    @patch.object(m, "staging_only", return_value=False)
    @patch.object(m, "ancestor", return_value=True)
    @patch.object(m, "merge_validated")
    def test_stale_already_merged_event_is_verified_noop(self, merge_validated, ancestor, _):
        api = Api()
        api.refs[m.APPROVED] = "d" * 40
        api.api_map["pulls/382"] = self.merged_pr(api)

        result = m.integrate(api, 382)

        self.assertTrue(result["replayed"])
        self.assertEqual(382, result["mergedPr"])
        self.assertFalse(result["releaseDispatched"])
        self.assertEqual([], api.dispatched)
        ancestor.assert_called_once_with("c" * 40, "d" * 40)
        merge_validated.assert_not_called()

    @patch.object(m, "staging_only", return_value=False)
    @patch.object(m, "ancestor", return_value=False)
    def test_merged_pr_outside_current_approved_lineage_still_fails_closed(self, _, __):
        api = Api()
        api.refs[m.APPROVED] = "d" * 40
        api.api_map["pulls/382"] = self.merged_pr(api)

        with self.assertRaisesRegex(RuntimeError, "Only ready"):
            m.integrate(api, 382)


class PendingUpdateFairness(unittest.TestCase):
    @patch.object(m, "staging_only", return_value=False)
    @patch.object(m, "ready", return_value=True)
    @patch.object(m, "integrate")
    def test_retained_older_pr_does_not_starve_later_validated_pr(self, integrate, _, __):
        api = Api()
        older = {"number": 10}
        newer = {"number": 11}
        # GitHub returns newer first; lifecycle intentionally scans oldest first.
        api.pages_map["pulls?state=open&base=legend%2Fapproved-changes"] = [newer, older]
        api.pages_map["pulls?state=closed&base=legend%2Fapproved-changes"] = []
        integrate.side_effect = [
            {"retained": "Awaiting successful exact-head validation"},
            {"mergedPr": 11, "sha": "f" * 40},
        ]

        result = m.pending_updates(api)

        self.assertEqual(11, result["mergedPr"])
        self.assertEqual(2, integrate.call_count)
        self.assertEqual(10, integrate.call_args_list[0].args[1])
        self.assertEqual(11, integrate.call_args_list[1].args[1])


    @patch.object(m, "staging_only", return_value=False)
    @patch.object(m, "ancestor", side_effect=[False, True])
    def test_blocked_correction_pr_does_not_starve_release_recovery(self, _, __):
        api = Api()
        api.refs[m.APPROVED] = "a" * 40
        api.pages_map["pulls?state=open&base=legend%2Fapproved-changes"] = []
        api.pages_map["pulls?state=closed&base=legend%2Fapproved-changes"] = [{
            "number": 12,
            "state": "closed",
            "merged_at": "2026-10-01T00:00:00Z",
            "author_association": "OWNER",
            "head": {
                "ref": "retained-work",
                "sha": "b" * 40,
                "repo": {"full_name": api.repo},
            },
        }]
        api.pages_map["branches"] = [{
            "name": "retained-work",
            "commit": {"sha": "c" * 40},
        }]

        def blocked_pull(_data, _method):
            raise RuntimeError("GitHub POST pulls: HTTP 403")

        api.api_map["pulls"] = blocked_pull
        result = m.pending_updates(api)

        self.assertEqual(
            "no validated ready changes or retained-branch corrections",
            result["integration"],
        )
        self.assertEqual(1, len(result["retainedCandidates"]))
        self.assertEqual("retained-work", result["retainedCandidates"][0]["branch"])
        self.assertIn("blocked", result["retainedCandidates"][0]["reason"].lower())

class AutomaticMergeRelease(unittest.TestCase):
    @patch.object(m, "candidate_validation", return_value=None)
    @patch.object(m, "automatic_release_targets")
    def test_green_merge_dispatches_release_without_second_command(self, targets, _):
        api = Api()
        target = next(iter(m.VALIDATION_AUTHORITY.RELEASE_TARGETS.values()))["releaseName"]
        targets.return_value = (target,)
        pr = {"number": 77, "head": {"sha": "b" * 40}}
        api.api_map["pulls/77/merge"] = {
            "merged": True,
            "sha": "c" * 40,
        }
        api.pages_map["pulls/77/files"] = []

        result = m.merge_validated(api, pr)

        self.assertTrue(result["automaticRelease"])
        self.assertEqual([target], result["targets"])
        self.assertEqual(1, len(api.dispatched))
        workflow, inputs = api.dispatched[0]
        self.assertEqual(m.DIRECT, workflow)
        self.assertEqual("true", inputs["automatic"])
        self.assertEqual("77", inputs["source_pr"])
        self.assertEqual("b" * 40, inputs["validated_sha"])
        self.assertEqual("c" * 40, inputs["source_merge_sha"])
        self.assertEqual("c" * 40, inputs["merge_sha"])
        self.assertEqual([target], json.loads(inputs["targets_json"]))




    @patch.object(m, "candidate_validation", return_value=None)
    @patch.object(m, "automatic_release_targets", return_value=())
    @patch.object(m, "dispatch_pending_legacy_release")
    def test_control_only_green_merge_defers_recovery_until_refreshed_checkout(
        self, recover, _, __
    ):
        api = Api()
        pr = {"number": 78, "head": {"sha": "e" * 40}}
        api.api_map["pulls/78/merge"] = {
            "merged": True,
            "sha": "f" * 40,
        }
        api.pages_map["pulls/78/files"] = []

        result = m.merge_validated(api, pr)

        self.assertFalse(result["releaseDispatched"])
        self.assertFalse(result["automaticRelease"])
        self.assertEqual(
            "deferred until refreshed approved checkout",
            result["release"]["releaseRecovery"],
        )
        recover.assert_not_called()


    @patch.object(m, "candidate_validation", return_value=None)
    @patch.object(m, "automatic_release_targets", return_value=())
    def test_nonmergeable_validated_pr_is_retained_not_fatal(self, _, __):
        api = Api()
        pr = {"number": 323, "head": {"sha": "b" * 40}}

        def blocked_merge(_data, _method):
            raise RuntimeError("GitHub PUT pulls/323/merge: HTTP 405")

        api.api_map["pulls/323/merge"] = blocked_merge
        api.pages_map["pulls/323/files"] = []

        result = m.merge_validated(api, pr)

        self.assertIn("retained", result)
        self.assertEqual(323, result["pr"])
        self.assertIn("not currently mergeable", result["retained"])

class ReleaseTruth(unittest.TestCase):
    def setUp(self):
        request = json.dumps({"releaseMode": "approved-only", "targets": [canonical_name("portal")]})
        self.git_patch = patch.object(
            m, "git", return_value=SimpleNamespace(returncode=0, stdout=request, stderr="")
        )
        self.git_patch.start()

    def tearDown(self):
        self.git_patch.stop()

    def release_run(self, **overrides):
        row = {
            "id": 10,
            "status": "completed",
            "conclusion": "success",
            "path": ".github/workflows/" + m.DIRECT,
            "head_branch": m.APPROVED,
            "head_sha": "d" * 40,
            "head_repository": {"full_name": "MYLEGND/masterapp"},
        }
        row.update(overrides)
        return row

    def test_only_direct_release_can_prove_deployment(self):
        api = Api()
        api.pages_map["actions/runs/10/jobs?filter=latest"] = successful_jobs()
        self.assertTrue(m.successful_release(api, self.release_run(), app="portal"))
        self.assertFalse(m.successful_release(
            api, self.release_run(path=".github/workflows/approved-release-security-validation.yml"), app="portal"
        ))

    def test_wrong_branch_or_repository_cannot_prove_deployment(self):
        api = Api()
        api.pages_map["actions/runs/10/jobs?filter=latest"] = successful_jobs()
        self.assertFalse(m.successful_release(api, self.release_run(head_branch="production")))
        self.assertFalse(m.successful_release(
            api, self.release_run(head_repository={"full_name": "fork/masterapp"})
        ))

    def test_missing_final_live_or_enforcement_proof_fails_closed(self):
        api = Api()
        api.pages_map["actions/runs/10/jobs?filter=latest"] = [
            {"name": "discover-live", "conclusion": "success", "steps": []},
            {"name": "release", "conclusion": "success", "steps": [
                {"name": "Publish selected head as one transaction", "conclusion": "success"},
            ]},
        ]
        self.assertFalse(m.successful_release(api, self.release_run(), app="portal"))

    def test_app_receipt_is_target_specific(self):
        api = Api()
        revision = "c" * 40
        passed = self.release_run(id=10, head_sha="d" * 40)
        api.pages_map["actions/artifacts?name=legend-approved-release-" + revision + "-" + canonical_name("portal")] = [
            {"expired": False, "workflow_run": {"id": 10}}
        ]
        api.pages_map["actions/artifacts?name=legend-approved-release-" + revision + "-" + canonical_name("client")] = []
        api.api_map["actions/runs/10"] = passed
        api.pages_map["actions/runs/10/jobs?filter=latest"] = successful_jobs()
        self.assertTrue(m.release_proven(api, revision, app="portal"))
        self.assertFalse(m.release_proven(api, revision, app="client"))

    def test_release_proven_resolves_application_revision_receipt_not_workflow_head(self):
        api = Api()
        revision = "c" * 40
        passed = self.release_run(id=10, head_sha="d" * 40)
        api.pages_map["actions/artifacts?name=legend-approved-release-" + revision + "-" + canonical_name("portal")] = [
            {"expired": False, "workflow_run": {"id": 10}}
        ]
        api.api_map["actions/runs/10"] = passed
        api.pages_map["actions/runs/10/jobs?filter=latest"] = successful_jobs()
        self.assertTrue(m.release_proven(api, revision, app="portal"))


    def test_release_proven_accepts_exact_head_success_before_receipt_artifact(self):
        api = Api()
        revision = "c" * 40
        passed = self.release_run(id=11, head_sha=revision)
        api.pages_map["actions/artifacts?name=legend-approved-release-" + revision + "-" + canonical_name("portal")] = []
        api.pages_map["actions/runs?head_sha=" + revision] = [passed]
        api.pages_map["actions/runs/11/jobs?filter=latest"] = successful_jobs()
        self.assertTrue(m.release_proven(api, revision, app="portal"))
        self.assertFalse(m.release_proven(api, revision, app="client"))

    def test_release_proven_exact_head_fallback_rejects_mismatched_run_identity(self):
        api = Api()
        revision = "c" * 40
        mismatched = self.release_run(id=12, head_sha="d" * 40)
        api.pages_map["actions/artifacts?name=legend-approved-release-" + revision] = []
        api.pages_map["actions/runs?head_sha=" + revision] = [mismatched]
        api.pages_map["actions/runs/12/jobs?filter=latest"] = successful_jobs()
        self.assertFalse(m.release_proven(api, revision, app="portal"))


class HistoricalReleaseRecovery(unittest.TestCase):
    @patch.object(m, "release_proven", return_value=False)
    @patch.object(m, "release_targets")
    @patch.object(m, "candidate_validation", return_value=None)
    @patch.object(m, "direct_release_approved_pr")
    @patch.object(m, "direct_only_request")
    @patch.object(m, "git")
    def test_nearest_unreleased_authorization_survives_control_only_descendants(
        self, git, direct_only, approved_pr, _, targets, __
    ):
        approved = "a" * 40
        authorization = "b" * 40
        target = canonical_name("portal")
        git.return_value = SimpleNamespace(
            returncode=0,
            stdout=approved + "\n" + authorization + "\n",
            stderr="",
        )
        direct_only.side_effect = lambda sha: sha == authorization
        approved_pr.return_value = {
            "number": 42,
            "head": {"sha": "c" * 40},
        }
        targets.return_value = {target}

        result = m.pending_legacy_release_authorization(Api(), approved)

        self.assertEqual(authorization, result["authorizationSha"])
        self.assertEqual("c" * 40, result["applicationRevision"])
        self.assertEqual([target], result["targets"])
        self.assertEqual(42, result["sourcePr"])
        history_args = git.call_args.args
        self.assertEqual("rev-list", history_args[0])
        self.assertIn("--first-parent", history_args)
        self.assertNotIn(m.VALIDATION_AUTHORITY.RELEASE_REQUEST_PATH, history_args)
        self.assertNotIn("-n", history_args)


    @patch.object(m, "authorization_release_proven", return_value=True)
    @patch.object(m, "release_proven", return_value=False)
    @patch.object(m, "release_targets")
    @patch.object(m, "candidate_validation", return_value=None)
    @patch.object(m, "direct_release_approved_pr")
    @patch.object(m, "direct_only_request")
    @patch.object(m, "git")
    def test_newest_satisfied_authorization_never_resurrects_older_release(
        self, git, direct_only, approved_pr, _, targets, __, ___
    ):
        approved = "a" * 40
        newest = "b" * 40
        older = "c" * 40
        target = canonical_name("portal")
        git.return_value = SimpleNamespace(
            returncode=0,
            stdout=approved + "\n" + newest + "\n" + older + "\n",
            stderr="",
        )
        direct_only.side_effect = lambda sha: sha in {newest, older}
        approved_pr.return_value = {
            "number": 42,
            "head": {"sha": "d" * 40},
        }
        targets.return_value = {target}

        result = m.pending_legacy_release_authorization(Api(), approved)

        self.assertIsNone(result)
        self.assertEqual(1, approved_pr.call_count)
        self.assertEqual(newest, approved_pr.call_args.args[1])

    @patch.object(m, "_validated_package_evidence", return_value={"reusable": True, "runId": 77})
    @patch.object(m, "pending_legacy_release_authorization")
    def test_pending_authorization_dispatches_exact_historical_release_sha(self, pending, _):
        api = Api()
        target = canonical_name("portal")
        pending.return_value = {
            "authorizationSha": "b" * 40,
            "applicationRevision": "c" * 40,
            "targets": [target],
            "sourcePr": 42,
        }

        result = m.dispatch_pending_legacy_release(api, "a" * 40)

        self.assertIn("directRelease", result)
        self.assertEqual(77, result["packageEvidenceRunId"])
        self.assertEqual(
            [(m.DIRECT, {"automatic": "false", "merge_sha": "b" * 40})],
            api.dispatched,
        )

    @patch.object(m, "_package_backfill_running", return_value=False)
    @patch.object(m, "_validated_package_evidence", return_value={"reusable": False, "reason": "exact_validated_package_missing"})
    @patch.object(m, "pending_automatic_release")
    def test_missing_automatic_package_dispatches_package_backfill_before_release(self, pending, _, __):
        api = Api()
        pending.return_value = {
            "authorizationSha": "b" * 40,
            "applicationRevision": "c" * 40,
            "targets": [canonical_name("portal")],
            "sourcePr": 385,
        }

        result = m.dispatch_pending_automatic_release(api, "a" * 40)

        self.assertEqual(
            "dispatched for exact green automatic application revision",
            result["packageBackfill"],
        )
        self.assertEqual(
            [(m.PACKAGE_VALIDATION, {"package_revision": "c" * 40})],
            api.dispatched,
        )

    @patch.object(m, "_package_backfill_running", return_value=True)
    @patch.object(m, "_validated_package_evidence", return_value={"reusable": False, "reason": "exact_validated_package_missing"})
    @patch.object(m, "pending_automatic_release")
    def test_running_automatic_package_backfill_does_not_duplicate_dispatch(self, pending, _, __):
        api = Api()
        pending.return_value = {
            "authorizationSha": "b" * 40,
            "applicationRevision": "c" * 40,
            "targets": [canonical_name("portal")],
            "sourcePr": 385,
        }

        result = m.dispatch_pending_automatic_release(api, "a" * 40)

        self.assertEqual("already queued or running", result["packageBackfill"])
        self.assertEqual([], api.dispatched)

    @patch.object(m, "_package_backfill_running", return_value=False)
    @patch.object(m, "_validated_package_evidence", return_value={"reusable": False, "reason": "exact_validated_package_missing"})
    @patch.object(m, "pending_legacy_release_authorization")
    def test_missing_package_dispatches_package_only_architecture_recovery(self, pending, _, __):
        api = Api()
        target = canonical_name("portal")
        pending.return_value = {
            "authorizationSha": "b" * 40,
            "applicationRevision": "c" * 40,
            "targets": [target],
            "sourcePr": 42,
        }

        result = m.dispatch_pending_legacy_release(api, "a" * 40)

        self.assertIn("packageBackfill", result)
        self.assertEqual(
            [(m.PACKAGE_VALIDATION, {"package_revision": "c" * 40})],
            api.dispatched,
        )

    @patch.object(m, "_package_backfill_running", return_value=True)
    @patch.object(m, "_validated_package_evidence", return_value={"reusable": False, "reason": "exact_validated_package_missing"})
    @patch.object(m, "pending_legacy_release_authorization")
    def test_running_package_backfill_is_preserved_without_duplicate_dispatch(self, pending, _, __):
        api = Api()
        pending.return_value = {
            "authorizationSha": "b" * 40,
            "applicationRevision": "c" * 40,
            "targets": [canonical_name("portal")],
            "sourcePr": 42,
        }

        result = m.dispatch_pending_legacy_release(api, "a" * 40)

        self.assertEqual("already queued or running", result["packageBackfill"])
        self.assertEqual([], api.dispatched)


class ReconcileSafety(unittest.TestCase):
    @patch.object(m, "staging_only", return_value=False)
    @patch.object(m, "dispatch_pending_legacy_release", return_value=None)
    @patch.object(m, "dispatch_pending_automatic_release", return_value=None)
    def test_no_pending_release_means_no_release(self, _, __, ___):
        api = Api()
        self.assertEqual(
            {"release": "no application publication required for exact approved head"},
            m.reconcile(api),
        )
        self.assertEqual([], api.dispatched)

    @patch.object(m, "staging_only", return_value=False)
    @patch.object(m, "dispatch_pending_legacy_release")
    @patch.object(m, "dispatch_pending_automatic_release", return_value=None)
    def test_control_only_head_recovers_historical_release(self, _, recover, __):
        api = Api()
        recovered = {
            "directRelease": "recovered nearest still-unreleased historical authorization",
            "authorizationSha": "b" * 40,
        }
        recover.return_value = recovered
        api.pages_map["actions/runs?head_sha=" + "a" * 40] = []
        api.pages_map["commits/" + "a" * 40 + "/pulls"] = []

        result = m.reconcile(api)

        self.assertEqual(recovered, result)
        recover.assert_called_once_with(api, "a" * 40)

    @patch.object(m, "staging_only", return_value=False)
    @patch.object(m, "dispatch_pending_automatic_release")
    def test_control_only_head_recovers_nearest_unreleased_automatic_merge(self, recover, _):
        api = Api()
        recovered = {
            "directRelease": "recovered nearest still-unreleased automatic validated merge",
            "authorizationSha": "b" * 40,
            "sourcePr": 385,
            "targets": [canonical_name("portal")],
        }
        recover.return_value = recovered
        api.pages_map["actions/runs?head_sha=" + "a" * 40] = []
        api.pages_map["commits/" + "a" * 40 + "/pulls"] = []

        result = m.reconcile(api)

        self.assertEqual(recovered, result)
        recover.assert_called_once_with(api, "a" * 40)

    @patch.object(m, "staging_only", return_value=False)
    @patch.object(m, "direct_only_request", return_value=True)
    def test_failed_exact_release_is_not_auto_replayed(self, _, __):
        api = Api()
        api.pages_map["actions/runs?head_sha=" + "a" * 40] = [{
            "id": 8,
            "status": "completed",
            "conclusion": "failure",
            "path": ".github/workflows/" + m.DIRECT,
            "head_branch": m.APPROVED,
            "updated_at": "2026-10-01T12:00:00Z",
        }]
        result = m.reconcile(api)
        self.assertIn("retained", result)
        self.assertEqual([], api.dispatched)

    @patch.object(m, "staging_only", return_value=False)
    def test_failed_package_backfill_trigger_is_not_auto_replayed(self, _):
        api = Api()
        api.api_map["actions/runs/100"] = {
            "path": ".github/workflows/" + m.PACKAGE_VALIDATION,
            "event": "workflow_dispatch",
            "status": "completed",
            "conclusion": "failure",
        }
        result = m.reconcile(api, 100)
        self.assertIn("retained", result)
        self.assertEqual([], api.dispatched)

    @patch.object(m, "staging_only", return_value=False)
    @patch.object(m, "successful_release", return_value=False)
    def test_failed_trigger_never_creates_another_release_path(self, _, __):
        api = Api()
        api.api_map["actions/runs/99"] = {
            "path": ".github/workflows/" + m.DIRECT,
            "status": "completed",
            "conclusion": "failure",
        }
        result = m.reconcile(api, 99)
        self.assertIn("retained", result)
        self.assertEqual([], api.dispatched)


class CandidateValidation(unittest.TestCase):
    def pr(self, files):
        return {
            "number": 7,
            "head": {"sha": "b" * 40},
        }, files

    def test_broad_product_change_requires_architecture_step5_and_security(self):
        pr, files = self.pr(["AgentPortal/Program.cs"])
        api = Api()
        api.pages_map["actions/runs?head_sha=" + "b" * 40] = []
        api.pages_map["pulls/7/files"] = [{"filename": path} for path in files]
        api.pages_map["pulls/7/commits"] = [{"sha": "b" * 40}]
        pending = m.candidate_validation(api, pr)
        self.assertIn("architecture", pending.lower())

        runs = [
            {"id": 1, "head_sha": "b" * 40, "event": "pull_request", "created_at": "3",
             "path": ".github/workflows/masterapp-platform-architecture-validation.yml",
             "status": "completed", "conclusion": "success"},
            {"id": 2, "head_sha": "b" * 40, "event": "pull_request", "created_at": "2",
             "path": ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
             "status": "completed", "conclusion": "success"},
            {"id": 3, "head_sha": "b" * 40, "event": "pull_request", "created_at": "1",
             "path": ".github/workflows/approved-release-security-validation.yml",
             "status": "completed", "conclusion": "success"},
        ]
        api.pages_map["actions/runs?head_sha=" + "b" * 40] = runs
        self.assertIsNone(m.candidate_validation(api, pr))

    def test_security_authority_change_requires_security_validator(self):
        pr, files = self.pr([".github/workflows/approved-release-security-validation.yml"])
        api = Api()
        api.pages_map["pulls/7/files"] = [{"filename": path} for path in files]
        api.pages_map["pulls/7/commits"] = [{"sha": "b" * 40}]
        api.pages_map["actions/runs?head_sha=" + "b" * 40] = [{
            "id": 1, "head_sha": "b" * 40, "event": "pull_request", "created_at": "2",
            "path": ".github/workflows/masterapp-platform-architecture-validation.yml",
            "status": "completed", "conclusion": "success",
        }]
        pending = m.candidate_validation(api, pr)
        self.assertIn("approved-release-security-validation.yml", pending)


class SingleBranchTopology(unittest.TestCase):
    def test_lifecycle_has_no_production_branch_authority(self):
        source = Path(__file__).with_name("release-lifecycle.py").read_text()
        self.assertNotIn("PRODUCTION =", source)
        self.assertNotIn("resolve-production", source)
        self.assertNotIn("branch-parity", source)
        self.assertNotIn("base=production", source)
        self.assertNotIn("refs/heads/production", source)

    def test_lifecycle_workflow_uses_only_approved_release_authorities(self):
        workflow = (Path(__file__).resolve().parents[1] / ".github/workflows/legend-release-lifecycle.yml").read_text()
        self.assertIn("LEGEND approved direct release", workflow)
        self.assertIn("LEGEND approved release security validation", workflow)
        self.assertNotIn("Validate, merge, and deploy AgentPortal to production", workflow)
        self.assertNotIn("production gates", workflow.lower())


    def test_validation_completions_do_not_run_full_branch_cleanup(self):
        workflow = (Path(__file__).resolve().parents[1] / ".github/workflows/legend-release-lifecycle.yml").read_text()
        cleanup = workflow.split(
            "      - name: Retire only preserved successfully deployed branches\n", 1
        )[1].split("      - name:", 1)[0]
        self.assertIn("github.event.workflow_run.name == 'LEGEND approved direct release'", cleanup)
        self.assertIn("github.event.workflow_run.conclusion == 'success'", cleanup)
        self.assertIn("github.event_name == 'schedule'", cleanup)
        self.assertIn("github.event_name == 'workflow_dispatch'", cleanup)
        self.assertNotIn("github.event_name != 'workflow_run'", cleanup)

    def test_lifecycle_refreshes_to_newly_merged_approved_code_before_recovery(self):
        workflow = (Path(__file__).resolve().parents[1] / ".github/workflows/legend-release-lifecycle.yml").read_text()
        refresh = workflow.split(
            "      - name: Refresh after automatically integrated corrections\n", 1
        )[1].split("      - name:", 1)[0]
        self.assertIn("github.event.repository.default_branch", refresh)
        self.assertIn("module.TRUSTED_PR_BASE", refresh)
        self.assertIn('git reset --hard "origin/$APPROVED_REF"', refresh)
        self.assertLess(
            workflow.index("Refresh after automatically integrated corrections"),
            workflow.index("Recover authorized direct release when needed"),
        )


class StagingSafety(unittest.TestCase):
    @patch.object(m, "staging_only", return_value=True)
    def test_hold_blocks_automatic_mutations(self, _):
        api = Api()
        self.assertIn("retained", m.integrate(api, 1))
        self.assertIn("retained", m.pending_updates(api))
        self.assertIn("disabled", m.reconcile(api)["release"])
        self.assertIn("retained", m.cleanup(api))


if __name__ == "__main__":
    unittest.main()
