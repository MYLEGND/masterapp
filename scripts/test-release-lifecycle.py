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
        self.statuses = []
        self.commit_statuses = {}
        self._status_id = 0

    def ref(self, name):
        return self.refs[name]

    def pages(self, path, key=None):
        value = self.pages_map.get(path, [])
        if key and isinstance(value, dict):
            return value.get(key, [])
        return value

    def api(self, path, data=None, method=None):
        if path == "rulesets":
            return [{"id": 1, "target": "branch", "enforcement": "active"}]
        if path == "rulesets/1":
            return {
                "id": 1,
                "target": "branch",
                "enforcement": "active",
                "conditions": {"ref_name": {"include": ["refs/heads/" + m.APPROVED], "exclude": []}},
                "bypass_actors": [],
                "current_user_can_bypass": "never",
                "rules": [
                    {"type": "deletion"},
                    {"type": "non_fast_forward"},
                    {"type": "pull_request", "parameters": {"allowed_merge_methods": ["merge"]}},
                    {"type": "required_status_checks", "parameters": {
                        "strict_required_status_checks_policy": True,
                        "required_status_checks": [{"context": "architecture-validation"}],
                    }},
                ],
            }
        value = self.api_map.get(path)
        if callable(value):
            return value(data, method)
        if value is not None:
            return value
        if path.startswith("commits/") and path.endswith("/status"):
            revision = path.split("/", 2)[1]
            return {"statuses": list(self.commit_statuses.get(revision, []))}
        if path.startswith("compare/") and "..." in path:
            approved, _candidate = path[len("compare/"):].split("...", 1)
            return {
                "status": "ahead",
                "merge_base_commit": {"sha": approved},
            }
        return {}

    def text(self, revision, path):
        return (Path(__file__).resolve().parents[1] / path).read_text()

    def context_status(self, revision, context, state, description):
        self._status_id += 1
        row = {
            "id": self._status_id,
            "context": context,
            "state": state,
            "description": description,
            "created_at": f"2026-10-04T00:00:{self._status_id:02d}Z",
            "updated_at": f"2026-10-04T00:00:{self._status_id:02d}Z",
        }
        self.commit_statuses.setdefault(revision, []).append(row)
        self.statuses.append((revision, context, state, description))

    def status(self, revision, state, description):
        self.context_status(revision, "architecture-validation", state, description)

    def dispatch(self, workflow, inputs=None):
        self.dispatched.append((workflow, inputs or {}))


class ApprovedHeadSynchronizationTests(unittest.TestCase):
    def pr(self, sha="b" * 40):
        return {
            "number": 42,
            "state": "open",
            "draft": False,
            "base": {"ref": m.APPROVED},
            "head": {
                "sha": sha,
                "ref": "repair/work",
                "repo": {"full_name": "MYLEGND/masterapp"},
            },
            "author_association": "OWNER",
        }

    def test_current_candidate_does_not_mutate(self):
        api = Api()
        approved = api.ref(m.APPROVED)
        pr = self.pr()
        api.api_map[f"compare/{approved}...{pr['head']['sha']}"] = {
            "status": "ahead",
            "merge_base_commit": {"sha": approved},
        }
        self.assertIsNone(m.sync_candidate_to_current_approved(api, pr))

    def test_stale_candidate_merges_current_approved_before_validation(self):
        api = Api()
        approved = api.ref(m.APPROVED)
        pr = self.pr()
        merged = "d" * 40
        api.api_map[f"compare/{approved}...{pr['head']['sha']}"] = {
            "status": "diverged",
            "merge_base_commit": {"sha": "c" * 40},
        }
        writes = []

        def merge(data, method):
            writes.append((data, method))
            return {"sha": merged}

        api.api_map["merges"] = merge
        fresh = self.pr(merged)
        api.api_map["pulls/42"] = fresh

        result = m.sync_candidate_to_current_approved(api, pr)

        self.assertEqual("BASE_SYNCED", result["state"])
        self.assertEqual(merged, result["head"])
        self.assertEqual(1, len(writes))
        self.assertEqual("repair/work", writes[0][0]["base"])
        self.assertEqual(approved, writes[0][0]["head"])
        self.assertEqual("POST", writes[0][1])


class ReleaseControlIntegrityGuard(unittest.TestCase):
    def test_repository_ruleset_requires_no_bypass_strict_merge_only_protection(self):
        api = Api()
        self.assertIsNone(m.repository_ruleset_integrity(api))
        api.api_map["rulesets/1"] = {
            "id": 1,
            "target": "branch",
            "enforcement": "active",
            "conditions": {"ref_name": {"include": ["refs/heads/" + m.APPROVED]}},
            "bypass_actors": [{"actor_id": 1}],
            "rules": [],
        }
        # The canonical Api fixture owns the normal ruleset path; direct helper below
        # proves the production guard is fail-closed by using a minimal override.
        class Unsafe(Api):
            def api(self, path, data=None, method=None):
                if path == "rulesets":
                    return [{"id": 2, "target": "branch", "enforcement": "active"}]
                if path == "rulesets/2":
                    return {
                        "conditions": {"ref_name": {"include": ["refs/heads/" + m.APPROVED]}},
                        "bypass_actors": [{"actor_id": 1}],
                        "rules": [],
                    }
                return super().api(path, data, method)
        self.assertIn("bypass", m.repository_ruleset_integrity(Unsafe()).lower())

    def test_current_control_plane_satisfies_trusted_outer_guard(self):
        api = Api()
        pr = {"head": {"sha": "b" * 40}}
        names = ["scripts/release-lifecycle.py"]
        self.assertIsNone(m.candidate_control_plane_integrity(api, pr, names))

    def test_non_control_change_still_requires_repository_safety_rails_only(self):
        api = Api()
        pr = {"head": {"sha": "b" * 40}}
        self.assertIsNone(m.candidate_control_plane_integrity(
            api, pr, ["AgentPortal/wwwroot/css/legend-app-shell.css"]
        ))



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

    def test_legacy_approved_only_request_without_targets_means_full_inventory(self):
        path = Path("Docs/releases/direct-release-request.json")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps({"releaseMode": "approved-only"}))
        m.git("add", str(path))
        m.git("commit", "-m", "legacy full-scope release")
        sha = m.git("rev-parse", "HEAD").stdout.strip()

        self.assertEqual(
            set(m.VALIDATION_AUTHORITY.release_name_map()),
            m.release_targets(sha),
        )

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


class ReleaseQueueSerialization(unittest.TestCase):
    def pr(self, number, sha=None, ref=None):
        return {
            "number": number,
            "state": "open",
            "draft": False,
            "author_association": "OWNER",
            "base": {"ref": m.APPROVED},
            "head": {
                "sha": sha or (hex(number)[2:] * 40)[:40],
                "ref": ref or f"repair/pr-{number}",
                "repo": {"full_name": Api.repo},
            },
        }

    def test_first_candidate_owns_queue_and_later_candidate_is_queued(self):
        api = Api()
        first = self.pr(442, "b" * 40)
        later = self.pr(450, "c" * 40)

        claimed = m.claim_release_queue(api, first)
        queued = m.claim_release_queue(api, later)

        self.assertEqual("RELEASE_QUEUE_OWNER", claimed["state"])
        self.assertEqual("RELEASE_QUEUED", queued["state"])
        self.assertEqual(442, queued["ownerPr"])
        self.assertEqual(442, m.release_queue_lease(api)["ownerPr"])

    def test_owner_head_change_keeps_same_queue_ownership(self):
        api = Api()
        first = self.pr(442, "b" * 40)
        m.claim_release_queue(api, first)
        updated = self.pr(442, "c" * 40)

        claimed = m.claim_release_queue(api, updated)

        self.assertEqual("RELEASE_QUEUE_OWNER", claimed["state"])
        self.assertEqual(442, m.release_queue_lease(api)["ownerPr"])

    def test_non_owner_cannot_merge_even_with_green_validation(self):
        api = Api()
        owner = self.pr(442, "b" * 40)
        later = self.pr(450, "c" * 40)
        m.claim_release_queue(api, owner)

        result = m._release_queue_guard(api, later)

        self.assertEqual("RELEASE_QUEUED", result["state"])
        self.assertEqual(442, result["ownerPr"])

    @patch.object(m, "staging_only", return_value=False)
    @patch.object(m, "git", return_value=SimpleNamespace(returncode=0, stdout="", stderr=""))
    @patch.object(m, "merge_validated")
    def test_pending_updates_processes_only_active_owner(self, merge_validated, _, __):
        api = Api()
        owner = self.pr(442, "b" * 40)
        later = self.pr(450, "c" * 40)
        api.api_map["pulls/442"] = owner
        api.api_map["pulls/450"] = later
        api.pages_map["pulls?state=open&base=legend%2Fapproved-changes"] = [later, owner]
        api.pages_map["pulls?state=closed&base=legend%2Fapproved-changes"] = []
        m.claim_release_queue(api, owner)
        m._request_release_queue(api, later)
        merge_validated.return_value = {
            "state": "VALIDATING",
            "retained": "Awaiting successful exact-head validation",
        }

        result = m.pending_updates(api)

        self.assertEqual("VALIDATING", result["state"])
        merge_validated.assert_called_once_with(api, owner)

    def test_released_queue_promotes_next_requested_pr(self):
        api = Api()
        owner = self.pr(442, "b" * 40)
        later = self.pr(450, "c" * 40)
        api.pages_map["pulls?state=open&base=legend%2Fapproved-changes"] = [later]
        api.pages_map["pulls/450/files"] = []
        api.api_map["pulls/450"] = later
        m.claim_release_queue(api, owner)
        m._request_release_queue(api, later)
        m._release_release_queue(api, api.ref(m.APPROVED), 442, "terminal-live-provenance")

        result = m.promote_next_release_queue(api)

        self.assertEqual("RELEASE_QUEUE_PROMOTED", result["state"])
        self.assertEqual(450, result["pr"])
        self.assertEqual(450, m.release_queue_lease(api)["ownerPr"])


class AutomaticMergeRelease(unittest.TestCase):
    @patch.object(m, "_release_queue_guard", return_value=None)
    @patch.object(m, "candidate_validation", return_value=None)
    def test_green_merge_defers_one_dispatch_to_same_workflow_reconciliation(self, _, __):
        api = Api()
        target = canonical_name("portal")
        pr = {"number": 77, "head": {"sha": "b" * 40}}
        api.api_map["pulls/77/merge"] = {
            "merged": True,
            "sha": "c" * 40,
        }
        api.pages_map["pulls/77/files"] = [{"filename": "AgentPortal/Program.cs"}]

        result = m.merge_validated(api, pr)

        self.assertTrue(result["automaticRelease"])
        self.assertEqual([target], result["targets"])
        self.assertEqual('MERGED', result['state'])
        self.assertFalse(result['releaseDispatched'])
        self.assertEqual([], api.dispatched)
        admitted = m.admit_automatic_release(api, pr, 'c' * 40, [target], runs=[])
        m.admit_automatic_release(api, pr, 'c' * 40, [target], runs=[])
        self.assertEqual('RELEASE_DISPATCHED', admitted['state'])
        self.assertEqual(1, len(api.dispatched))
        self.assertEqual('77', api.dispatched[0][1]['source_pr'])

    @patch.object(m, "_release_queue_guard", return_value=None)
    @patch.object(m, "candidate_validation", return_value=None)
    def test_control_only_green_merge_defers_recovery_until_refreshed_checkout(self, _, __):
        api = Api()
        pr = {"number": 78, "head": {"sha": "e" * 40}}
        api.api_map["pulls/78/merge"] = {
            "merged": True,
            "sha": "f" * 40,
        }
        api.pages_map["pulls/78/files"] = [
            {"filename": "scripts/deploy-founder-cloudflare.py"},
        ]

        result = m.merge_validated(api, pr)

        self.assertFalse(result["releaseDispatched"])
        self.assertFalse(result["automaticRelease"])
        self.assertEqual(
            "deferred until refreshed approved checkout",
            result["release"]["releaseRecovery"],
        )


    @patch.object(m, "_release_queue_guard", return_value=None)
    @patch.object(m, "candidate_validation", return_value=None)
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


class AutomaticReleaseRecovery(unittest.TestCase):
    @patch.object(m, "candidate_validation", return_value=None)
    @patch.object(m, "release_proven", return_value=False)
    @patch.object(m, "_validated_package_evidence")
    @patch.object(m, "git")
    def test_unpackaged_control_correction_does_not_supersede_packaged_founder_source(
        self, git, package, _, __
    ):
        api = Api()
        approved = "a" * 40
        newest_merge = "b" * 40
        older_merge = "c" * 40
        newest_revision = "d" * 40
        older_revision = "e" * 40
        git.return_value = SimpleNamespace(
            returncode=0,
            stdout=approved + "\n" + newest_merge + "\n" + older_merge + "\n",
            stderr="",
        )
        api.pages_map["commits/" + approved + "/pulls"] = []
        api.pages_map["commits/" + newest_merge + "/pulls"] = [{
            "number": 391,
            "merged_at": "2026-10-02T19:20:00Z",
            "merge_commit_sha": newest_merge,
            "base": {"ref": m.APPROVED},
            "head": {"sha": newest_revision},
        }]
        api.pages_map["pulls/391/files"] = [
            {"filename": "scripts/deploy-founder-cloudflare.py"},
            {"filename": "scripts/test-release-policy.py"},
        ]
        api.pages_map["commits/" + older_merge + "/pulls"] = [{
            "number": 385,
            "merged_at": "2026-10-02T17:47:47Z",
            "merge_commit_sha": older_merge,
            "base": {"ref": m.APPROVED},
            "head": {"sha": older_revision},
        }]
        api.pages_map["pulls/385/files"] = [
            {"filename": "Legend-Cloudflare/src/runtime/registry.mjs"},
        ]
        package.side_effect = lambda _api, revision: {
            "reusable": revision == older_revision,
            "runId": 77 if revision == older_revision else None,
        }

        api.pages_map["pulls?state=closed&base=legend%2Fapproved-changes"] = (
            api.pages_map["commits/" + newest_merge + "/pulls"] +
            api.pages_map["commits/" + older_merge + "/pulls"])
        result = m.pending_automatic_releases(api, approved)[0]

        self.assertEqual(385, result["sourcePr"])
        self.assertEqual(older_revision, result["applicationRevision"])
        self.assertEqual([canonical_name("portal")], result["targets"])


class DurableCandidateQueue(unittest.TestCase):
    closed_path = "pulls?state=closed&base=legend%2Fapproved-changes"
    runs_path = "actions/runs?branch=legend%2Fapproved-changes"

    def test_direct_release_history_uses_repository_run_inventory_and_filters_canonical_workflow(self):
        api = Api()
        api.pages_map[self.runs_path] = [
            {"id": 1, "head_branch": m.APPROVED,
             "path": ".github/workflows/" + m.DIRECT},
            {"id": 2, "head_branch": m.APPROVED,
             "path": ".github/workflows/other.yml"},
            {"id": 3, "head_branch": "other",
             "path": ".github/workflows/" + m.DIRECT},
        ]
        with patch.object(api, "pages", wraps=api.pages) as pages:
            runs = m.direct_release_runs(api)

        self.assertEqual([1], [row["id"] for row in runs])
        requested = [call.args[0] for call in pages.call_args_list]
        self.assertIn(self.runs_path, requested)
        self.assertFalse(any(path.startswith("actions/workflows/") for path in requested))

    def setUp(self):
        self.api = Api()
        self.approved = "a" * 40
        for name, value in (("staging_only", False), ("candidate_validation", None),
                            ("release_proven", False), ("_validated_package_evidence", {"reusable": True})):
            fixture = patch.object(m, name, return_value=value)
            fixture.start()
            self.addCleanup(fixture.stop)
        history = patch.object(m, "git", return_value=SimpleNamespace(
            returncode=0, stdout="c" * 40 + "\n" + "b" * 40 + "\n", stderr=""))
        history.start()
        self.addCleanup(history.stop)

    def candidate(self, number, merge, revision, paths):
        pr = {"number": number, "merged_at": "2026-10-03T10:00:00Z",
              "merge_commit_sha": merge, "base": {"ref": m.APPROVED},
              "head": {"sha": revision}}
        self.api.pages_map.setdefault(self.closed_path, []).append(pr)
        self.api.pages_map[f"pulls/{number}/files"] = [{"filename": path} for path in paths]
        self.api.api_map[f"pulls/{number}"] = pr
        return pr

    def release_run(self, pr, status="completed", conclusion="failure", run_id=99):
        return {"id": run_id, "path": ".github/workflows/" + m.DIRECT,
                "head_branch": m.APPROVED, "head_sha": self.approved,
                "status": status, "conclusion": conclusion,
                "display_title": m.release_dispatch_identity(pr["number"], pr["head"]["sha"], self.approved)}

    def test_complete_frontier_does_not_query_superseded_history(self):
        self.candidate(2, "c" * 40, "e" * 40, ["Infrastructure/Example.cs", "Legend-Website/src/example.ts"])
        with patch.object(self.api, "pages", wraps=self.api.pages) as pages:
            queue = m.pending_automatic_releases(self.api, self.approved)
        self.assertEqual([2], [row["sourcePr"] for row in queue])
        self.assertEqual(set(m.VALIDATION_AUTHORITY.RELEASE_TARGETS),
                         set(m.VALIDATION_AUTHORITY.selected_release_target_keys(queue[0]['targets'])))
        self.assertNotIn("commits/" + "b" * 40 + "/pulls", [call.args[0] for call in pages.call_args_list])

    def test_completed_newer_target_does_not_hide_disjoint_pending_target(self):
        self.candidate(1, "b" * 40, "d" * 40, ["Protect-Website/Program.cs"])
        self.candidate(2, "c" * 40, "e" * 40, ["ClientApp/Program.cs"])
        with patch.object(m, "release_proven", side_effect=lambda api, revision, app: revision == "e" * 40):
            queue = m.pending_automatic_releases(self.api, self.approved)
        self.assertEqual([1], [row["sourcePr"] for row in queue])

    def test_independent_candidates_are_queued_in_deterministic_history_order(self):
        self.candidate(2, "c" * 40, "e" * 40, ["ClientApp/Program.cs"])
        self.candidate(1, "b" * 40, "d" * 40, ["Protect-Website/Program.cs"])
        queue = m.pending_automatic_releases(self.api, self.approved)
        self.assertEqual([1, 2], [row["sourcePr"] for row in queue])

    def test_partial_atomic_overlap_is_retained_not_silently_dropped(self):
        self.candidate(1, "b" * 40, "d" * 40, ["Protect-Website/Program.cs", "ClientApp/Program.cs"])
        self.candidate(2, "c" * 40, "e" * 40, ["ClientApp/Program.cs"])
        queue = m.pending_automatic_releases(self.api, self.approved)
        self.assertEqual([1, 2], [row["sourcePr"] for row in queue])
        self.assertIn("combined successor", queue[0]["retained"])
        self.assertIn(canonical_name("protect"), queue[0]["targets"])

    def test_fully_superseded_candidate_cannot_downgrade_newer_target(self):
        self.candidate(1, "b" * 40, "d" * 40, ["ClientApp/Program.cs"])
        self.candidate(2, "c" * 40, "e" * 40, ["ClientApp/Program.cs"])
        self.assertEqual([2], [row["sourcePr"] for row in m.pending_automatic_releases(self.api, self.approved)])

    def test_active_legacy_run_blocks_without_canceling_or_dispatching_pending(self):
        pr = self.candidate(1, "b" * 40, "d" * 40, ["Protect-Website/Program.cs"])
        active = self.release_run(pr, status="in_progress", conclusion=None)
        active.pop("display_title")
        self.api.pages_map[self.runs_path] = [active]
        result = m.reconcile(self.api)
        self.assertEqual([99], result["blockingRuns"])
        self.assertEqual([], self.api.dispatched)

    def test_failed_completion_wakes_other_candidate_without_replaying_failed_head(self):
        first = self.candidate(1, "b" * 40, "d" * 40, ["Protect-Website/Program.cs"])
        self.candidate(2, "c" * 40, "e" * 40, ["ClientApp/Program.cs"])
        failed = self.release_run(first)
        self.api.pages_map[self.runs_path] = [failed]
        self.api.api_map["actions/runs/99"] = failed
        result = m.reconcile(self.api, 99)
        self.assertEqual(2, result["sourcePr"])
        self.assertEqual(1, len(self.api.dispatched))
        self.assertEqual("2", self.api.dispatched[0][1]["source_pr"])

    def test_successful_completion_wakes_other_candidate_without_revalidation_dispatch(self):
        first = self.candidate(1, "b" * 40, "d" * 40, ["Protect-Website/Program.cs"])
        self.candidate(2, "c" * 40, "e" * 40, ["ClientApp/Program.cs"])
        completed = self.release_run(first, conclusion="success")
        self.api.pages_map[self.runs_path] = [completed]
        with patch.object(m, "release_proven", side_effect=lambda api, revision, app: revision == "d" * 40):
            result = m.reconcile(self.api, 99)
        self.assertEqual(2, result["sourcePr"])
        self.assertEqual([m.DIRECT], [workflow for workflow, _ in self.api.dispatched])

    def test_changed_source_pr_identity_is_retained_after_queue_discovery(self):
        self.candidate(1, "b" * 40, "d" * 40, ["Protect-Website/Program.cs"])
        self.api.api_map["pulls/1"] = {"number": 1, "head": {"sha": "f" * 40}}
        result = m.reconcile(self.api)
        self.assertIn("changed", result["pendingCandidates"][0]["retained"])
        self.assertEqual([], self.api.dispatched)


class GeneratedPublicationStages(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location('workflow_generation', Path(__file__).with_name('release-workflow.py'))
        self.generator = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.generator)

    def test_workflow_target_steps_and_outcome_checks_are_derived_from_inventory(self):
        text = self.generator.WORKFLOW.read_text()
        self.assertEqual(text, self.generator.render(text, m.VALIDATION_AUTHORITY.RELEASE_TARGETS))

    def test_new_inventory_target_generates_its_own_durable_step_and_gate(self):
        text = self.generator.WORKFLOW.read_text()
        targets = {**m.VALIDATION_AUTHORITY.RELEASE_TARGETS,
                   'extra': {'releaseName': 'isolated-extra-app'}}
        rendered = self.generator.render(text, targets)
        self.assertIn('name: Publish canonical target (extra)', rendered)
        self.assertIn('TARGET_OUTCOME_EXTRA: ${{ steps.publish_extra.outcome }}', rendered)
        self.assertIn("contains(fromJSON(env.SELECTED_TARGETS), 'isolated-extra-app')", rendered)

    def test_selected_failure_cannot_be_hidden_by_continue_on_error(self):
        import subprocess
        path = Path(__file__).with_name('release-workflow.py')
        with patch.dict(os.environ, {'TARGET_OUTCOME_CLIENT': 'failure'}, clear=False):
            result = subprocess.run(['python3', str(path), '--verify-outcomes',
                                     '--selected-targets', json.dumps([canonical_name('client')])],
                                    capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('did not succeed: client', result.stderr)

    def test_unselected_failure_does_not_invalidate_selected_success(self):
        import subprocess
        path = Path(__file__).with_name('release-workflow.py')
        with patch.dict(os.environ, {'TARGET_OUTCOME_CLIENT': 'success', 'TARGET_OUTCOME_PORTAL': 'failure'}, clear=False):
            result = subprocess.run(['python3', str(path), '--verify-outcomes',
                                     '--selected-targets', json.dumps([canonical_name('client')])],
                                    capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)


class ResourceAdmission(unittest.TestCase):
    def resources(self, paths):
        authority = m.VALIDATION_AUTHORITY
        return authority.release_admission_resources(paths, list(authority.release_targets_for_paths(paths)))

    def setUp(self):
        self.api = Api()
        self.run = {'id': 98, 'run_attempt': 1, 'status': 'in_progress', 'conclusion': None,
                    'path': '.github/workflows/' + m.DIRECT, 'head_branch': m.APPROVED,
                    'event': 'workflow_dispatch',
                    'head_repository': {'full_name': self.api.repo}}
        self.api.pages_map[DurableCandidateQueue.runs_path] = [self.run]
        self.candidate = {'applicationRevision': 'b' * 40, 'selectedTargets': [canonical_name('client')],
                          'resources': self.resources(['ClientApp/Program.cs'])}
        self.prior = {'applicationRevision': 'c' * 40, 'selectedTargets': [canonical_name('protect')],
                      'resources': self.resources(['Protect-Website/Program.cs']),
                      'admissionId': 'e' * 64, 'producingAttempt': 1}

    def test_historical_admission_scope_uses_recorded_canonical_lease_not_current_path_classifier(self):
        record = {
            'selectedTargets': [canonical_name('client')],
            'resources': self.resources(['ClientApp/Program.cs']),
        }
        with patch.object(
            m.VALIDATION_AUTHORITY,
            'release_targets_for_paths',
            side_effect=AssertionError('historical admission must not be reclassified'),
        ):
            self.assertEqual(('client',), m._validate_admission_record_scope(record))

    def test_historical_admission_scope_rejects_missing_selected_target_write(self):
        record = {
            'selectedTargets': [canonical_name('client')],
            'resources': ['read/schema/masterapp'],
        }
        with self.assertRaisesRegex(RuntimeError, 'missing canonical target ownership'):
            m._validate_admission_record_scope(record)

    def test_disjoint_apps_read_schema_concurrently_without_conflicting_writes(self):
        self.assertFalse(m.VALIDATION_AUTHORITY.release_resources_overlap(
            self.candidate['resources'], self.prior['resources']))
        with patch.object(m, '_admission_records', return_value=[self.prior]), \
             patch.object(m, '_admission_settled', return_value=False):
            self.assertEqual([], m.admission_conflicts(self.api, self.candidate, current_run=99))

    def test_client_database_consumption_conflicts_with_shared_schema_writer(self):
        self.assertIn('read/schema/masterapp', self.candidate['resources'])
        self.assertTrue(m.VALIDATION_AUTHORITY.release_resources_overlap(
            self.candidate['resources'], ['write/schema/masterapp']))

    def test_settings_source_reader_conflicts_with_source_application_writer(self):
        portal = self.resources(['AgentPortal/Program.cs'])
        self.assertTrue(m.VALIDATION_AUTHORITY.release_resources_overlap(portal, self.prior['resources']))

    def test_overlapping_active_lease_blocks(self):
        self.prior['resources'] = self.candidate['resources']
        with patch.object(m, '_admission_records', return_value=[self.prior]), \
             patch.object(m, '_admission_settled', return_value=False):
            blocked = m.admission_conflicts(self.api, self.candidate, current_run=99)
        self.assertEqual(98, blocked[0]['runId'])

    def test_completed_nonmutating_history_is_discharged_before_old_scope_is_reinterpreted(self):
        self.run.update(status='completed', conclusion='failure')
        with patch.object(m, '_admission_nonmutating_terminal', return_value=True), \
             patch.object(m, '_admission_records', side_effect=AssertionError('historical scope must not be replayed')) as records:
            self.assertEqual([], m.admission_conflicts(self.api, self.candidate, current_run=99))
        records.assert_not_called()

    def test_failed_parent_does_not_release_unsettled_resources(self):
        self.run.update(status='completed', conclusion='failure')
        self.prior['resources'] = self.candidate['resources']
        with patch.object(m, '_admission_records', return_value=[self.prior]), \
             patch.object(m, '_admission_settled', return_value=False):
            self.assertTrue(m.admission_conflicts(self.api, self.candidate, current_run=99))

    def test_failed_but_positively_settled_provider_wakes_conflicting_candidate(self):
        self.run.update(status='completed', conclusion='failure')
        self.prior['resources'] = self.candidate['resources']
        with patch.object(m, '_admission_records', return_value=[self.prior]), \
             patch.object(m, '_admission_settled', return_value=True):
            self.assertEqual([], m.admission_conflicts(self.api, self.candidate, current_run=99))

    def test_same_immutable_transaction_can_continue_without_releasing_other_resources(self):
        self.run.update(status='completed', conclusion='failure')
        self.prior.update(self.candidate)
        with patch.object(m, '_admission_records', return_value=[self.prior]), \
             patch.object(m, '_admission_settled', return_value=False):
            self.assertEqual([], m.admission_conflicts(self.api, self.candidate, current_run=99))

    def test_skipped_latest_attempt_does_not_erase_earlier_mutation(self):
        self.run.update(run_attempt=2, status='completed', conclusion='success')
        skipped = [{'name': 'admission', 'conclusion': 'success'},
                   {'name': 'discover-live', 'conclusion': 'skipped'},
                   {'name': 'release', 'conclusion': 'skipped'}]
        self.api.pages_map['actions/runs/98/attempts/2/jobs'] = skipped
        self.api.pages_map['actions/runs/98/attempts/1/jobs'] = [
            {'name': 'admission', 'conclusion': 'success'},
            {'name': 'discover-live', 'conclusion': 'success'},
            {'name': 'release', 'conclusion': 'failure'}]
        self.assertFalse(m._never_admitted(self.api, self.run))
        self.api.pages_map['actions/runs/98/attempts/1/jobs'] = skipped
        self.assertTrue(m._never_admitted(self.api, self.run))

    def test_own_retry_after_cancelled_admission_does_not_block_itself(self):
        self.run.update(run_attempt=2)
        self.api.pages_map['actions/runs/98/attempts/1/jobs'] = [
            {'name': 'admission', 'conclusion': 'cancelled'},
            {'name': 'discover-live', 'conclusion': 'skipped'},
            {'name': 'release', 'conclusion': 'skipped'}]
        with patch.dict(os.environ, {'GITHUB_RUN_ATTEMPT': '2'}), \
             patch.object(m, '_admission_records', return_value=[]):
            self.assertEqual([], m.admission_conflicts(self.api, self.candidate, current_run=98))

    def test_own_retry_with_missing_or_entered_prior_jobs_remains_blocked(self):
        self.run.update(run_attempt=2)
        for jobs in ([], [
            {'name': 'admission', 'conclusion': 'success'},
            {'name': 'discover-live', 'conclusion': 'success'},
            {'name': 'release', 'conclusion': 'failure'}]):
            with self.subTest(jobs=jobs):
                self.api.pages_map['actions/runs/98/attempts/1/jobs'] = jobs
                with patch.dict(os.environ, {'GITHUB_RUN_ATTEMPT': '2'}), \
                     patch.object(m, '_admission_records', return_value=[]):
                    self.assertTrue(m.admission_conflicts(self.api, self.candidate, current_run=98))

    def test_own_prior_attempt_cannot_silently_change_resource_scope(self):
        self.run.update(run_attempt=2)
        self.prior.update(applicationRevision=self.candidate['applicationRevision'],
                          resources=self.candidate['resources'])
        # Same app revision alone cannot authorize replacing the prior selection.
        with patch.dict(os.environ, {'GITHUB_RUN_ATTEMPT': '2'}), \
             patch.object(m, '_admission_records', return_value=[self.prior]), \
             patch.object(m, '_admission_settled', return_value=False):
            self.assertTrue(m.admission_conflicts(self.api, self.candidate, current_run=98))

    def test_completed_admission_with_skipped_release_is_nonmutating_terminal(self):
        self.run.update(status='completed', conclusion='failure',
                        head_sha=m.git('rev-parse', 'HEAD').stdout.strip())
        self.api.pages_map['actions/runs/98/artifacts'] = []
        self.api.pages_map['actions/runs/98/attempts/1/jobs'] = [
            {'name': 'admission', 'conclusion': 'success'},
            {'name': 'discover-live', 'conclusion': 'failure'},
            {'name': 'release', 'conclusion': 'skipped', 'steps': []},
        ]
        self.assertTrue(m._admission_nonmutating_terminal(self.api, self.run))

    def test_historical_workflow_generation_can_prove_nonmutation_without_matching_current_text(self):
        self.run.update(status='completed', conclusion='failure', head_sha='b' * 40)
        self.api.pages_map['actions/runs/98/artifacts'] = []
        source = Path('.github/workflows/' + m.DIRECT).read_text()
        historical = source + "\n# historical trusted generation\n"
        blocks = m.VALIDATION_AUTHORITY.named_step_blocks(
            m.VALIDATION_AUTHORITY._job_blocks(historical)['release'])
        mutation = m._historical_release_mutation_steps(historical)
        safe_steps = [
            {
                'name': name,
                'conclusion': (
                    'failure'
                    if name == 'Prepare complete immutable release transaction'
                    else 'skipped' if name in mutation else 'success'
                ),
            }
            for name in blocks
        ]
        self.api.pages_map['actions/runs/98/attempts/1/jobs'] = [
            {'name': 'release', 'status': 'completed', 'conclusion': 'failure', 'steps': safe_steps},
        ]
        with patch.object(
            m, 'git',
            return_value=SimpleNamespace(returncode=0, stdout=historical, stderr=''),
        ):
            self.assertTrue(m._admission_nonmutating_terminal(self.api, self.run))

    def test_failure_before_transaction_disposes_lease_when_every_mutation_step_skipped(self):
        self.run.update(status='completed', conclusion='failure',
                        head_sha=m.git('rev-parse', 'HEAD').stdout.strip())
        self.api.pages_map['actions/runs/98/artifacts'] = []
        source = Path('.github/workflows/' + m.DIRECT).read_text()
        blocks = m.VALIDATION_AUTHORITY.named_step_blocks(
            m.VALIDATION_AUTHORITY._job_blocks(source)['release'])
        mutation = m._historical_release_mutation_steps(source)
        failed = 'Verify current live base before publication'
        safe_steps = [
            {
                'name': name,
                'conclusion': (
                    'failure' if name == failed
                    else 'skipped' if name in mutation
                    else 'success'
                ),
            }
            for name in blocks
        ]
        self.api.pages_map['actions/runs/98/attempts/1/jobs'] = [
            {'name': 'release', 'status': 'completed', 'conclusion': 'failure', 'steps': safe_steps},
        ]
        self.assertTrue(m._admission_nonmutating_terminal(self.api, self.run))

    def test_historical_child_intent_keeps_lease_blocking_even_when_steps_are_skipped(self):
        self.run.update(status='completed', conclusion='failure',
                        head_sha=m.git('rev-parse', 'HEAD').stdout.strip())
        source = Path('.github/workflows/' + m.DIRECT).read_text()
        blocks = m.VALIDATION_AUTHORITY.named_step_blocks(
            m.VALIDATION_AUTHORITY._job_blocks(source)['release'])
        mutation = m._historical_release_mutation_steps(source)
        safe_steps = [
            {
                'name': name,
                'conclusion': (
                    'failure'
                    if name == 'Prepare complete immutable release transaction'
                    else 'skipped' if name in mutation else 'success'
                ),
            }
            for name in blocks
        ]
        self.api.pages_map['actions/runs/98/attempts/1/jobs'] = [
            {'name': 'release', 'status': 'completed', 'conclusion': 'failure', 'steps': safe_steps},
        ]
        self.api.pages_map['actions/runs/98/artifacts'] = [
            {'name': 'legend-release-child-intent-' + 'a' * 64, 'expired': False},
        ]
        self.assertFalse(m._admission_nonmutating_terminal(self.api, self.run))

    def test_failed_transaction_prepare_releases_lease_only_before_any_mutation(self):
        self.run.update(status='completed', conclusion='failure',
                        head_sha=m.git('rev-parse', 'HEAD').stdout.strip())
        self.api.pages_map['actions/runs/98/artifacts'] = []
        source = Path('.github/workflows/' + m.DIRECT).read_text()
        blocks = m.VALIDATION_AUTHORITY.named_step_blocks(
            m.VALIDATION_AUTHORITY._job_blocks(source)['release'])
        safe_steps = [{'name': name, 'conclusion':
                       'failure' if name == 'Prepare complete immutable release transaction' else 'skipped'}
                      for name in blocks]
        self.api.pages_map['actions/runs/98/attempts/1/jobs'] = [
            {'name': 'release', 'status': 'completed', 'conclusion': 'failure', 'steps': safe_steps},
        ]
        self.assertTrue(m._admission_nonmutating_terminal(self.api, self.run))
        self.api.pages_map['actions/runs/98/artifacts'] = [
            {'name': 'legend-release-operation-intent-' + 'a' * 64, 'expired': False},
        ]
        self.assertFalse(m._admission_nonmutating_terminal(self.api, self.run))
        self.api.pages_map['actions/runs/98/artifacts'] = []
        unsafe_steps = list(safe_steps)
        unsafe_steps = [dict(step, conclusion='success') if step['name'] == 'Publish canonical target (client)'
                        else step for step in safe_steps]
        self.api.pages_map['actions/runs/98/attempts/1/jobs'] = [
            {'name': 'release', 'status': 'completed', 'conclusion': 'failure', 'steps': unsafe_steps},
        ]
        self.assertFalse(m._admission_nonmutating_terminal(self.api, self.run))

    def test_nonmutating_terminal_rejects_missing_steps_and_earlier_writes(self):
        self.run.update(status='completed', conclusion='failure', run_attempt=2,
                        head_sha=m.git('rev-parse', 'HEAD').stdout.strip())
        self.api.pages_map['actions/runs/98/attempts/2/jobs'] = [
            {'name': 'release', 'conclusion': 'skipped'}]
        for steps in (None, [], [
                {'name': 'Prepare complete immutable release transaction', 'conclusion': 'failure'}]):
            self.api.pages_map['actions/runs/98/attempts/1/jobs'] = [
                {'name': 'release', 'status': 'completed', 'conclusion': 'failure', 'steps': steps}]
            self.assertFalse(m._admission_nonmutating_terminal(self.api, self.run))

    def test_nonmutating_terminal_rejects_unknown_source_and_expired_intent(self):
        self.run.update(status='completed', conclusion='failure',
                        head_sha=m.git('rev-parse', 'HEAD').stdout.strip())
        self.api.pages_map['actions/runs/98/attempts/1/jobs'] = [
            {'name': 'release', 'conclusion': 'skipped'}]
        with patch.object(m, 'git', return_value=SimpleNamespace(returncode=0, stdout='unknown workflow')):
            self.assertFalse(m._admission_nonmutating_terminal(self.api, self.run))
        self.api.pages_map['actions/runs/98/artifacts'] = [
            {'name': 'legend-release-operation-intent-' + 'a' * 64, 'expired': True}]
        self.assertFalse(m._admission_nonmutating_terminal(self.api, self.run))

    def test_expired_lease_is_not_absence_proof(self):
        self.api.pages_map['actions/runs/98/artifacts'] = [
            {'name': 'legend-release-admission-' + 'e' * 64, 'expired': True}]
        with self.assertRaisesRegex(RuntimeError, 'expired'):
            m._admission_records(self.api, self.run)

    def test_valid_hash_cannot_hide_empty_resource_ownership(self):
        revision = 'b' * 40
        record = {'schemaVersion': 1, 'phase': 'admission', 'authorizationMode': 'automatic',
                  'sourcePr': 1, 'authorizedSourceRevision': revision,
                  'applicationRevision': revision, 'packageIdentity': 'f' * 64,
                  'executionAuthority': 'a' * 40, 'sourceMergeSha': 'c' * 40,
                  'selectedTargets': [canonical_name('client')], 'resources': [],
                  'producingRun': 98, 'producingAttempt': 1}
        record['admissionId'] = m._admission_identity(record)
        self.run.update(event='workflow_dispatch', head_sha='a' * 40,
                        head_repository={'full_name': self.api.repo})
        self.api.api_map['pulls/1'] = {'number': 1, 'merged_at': '2026-10-03',
                                    'base': {'ref': m.APPROVED}, 'head': {'sha': revision},
                                    'merge_commit_sha': 'c' * 40}
        self.api.pages_map['pulls/1/files'] = [{'filename': 'ClientApp/Program.cs'}]
        self.api.pages_map['actions/runs/98/artifacts'] = [
            {'name': 'legend-release-admission-' + record['admissionId'], 'expired': False}]
        def download(repo, run, name, directory):
            (directory / 'operation.json').write_text(json.dumps(record))
        with patch.object(m.VALIDATION_AUTHORITY, '_download_run_artifact', side_effect=download), \
             patch.object(m, 'ancestor', return_value=True), \
             patch.object(m.VALIDATION_AUTHORITY, 'package_inputs_compatible', return_value=True), \
             patch.object(m.VALIDATION_AUTHORITY, 'compute_validated_package_evidence', return_value={
                 'reusable': True, 'revision': revision, 'packageIdentity': 'f' * 64}):
            with self.assertRaisesRegex(RuntimeError, 'resource ownership'):
                m._admission_records(self.api, self.run)


class WorkerAdmissionPackageIdentity(unittest.TestCase):
    def test_retained_reused_package_checks_inputs_and_exact_producer(self):
        api = Api()
        source, producer, authority, merge = ('b' * 40, 'd' * 40, 'a' * 40, 'c' * 40)
        target = canonical_name('client')
        paths = ['ClientApp/Program.cs']
        record = {'schemaVersion': 1, 'phase': 'admission', 'authorizationMode': 'automatic',
                  'sourcePr': 7, 'authorizedSourceRevision': source,
                  'applicationRevision': producer, 'packageIdentity': 'e' * 64,
                  'executionAuthority': authority, 'sourceMergeSha': merge,
                  'selectedTargets': [target],
                  'resources': m.VALIDATION_AUTHORITY.release_admission_resources(paths, [target]),
                  'producingRun': 98, 'producingAttempt': 1}
        record['admissionId'] = m._admission_identity(record)
        run = {'id': 98, 'run_attempt': 1, 'event': 'workflow_dispatch',
               'head_sha': authority, 'head_repository': {'full_name': api.repo}}
        api.api_map['pulls/7'] = {'number': 7, 'merged_at': '2026-10-03',
            'base': {'ref': m.APPROVED}, 'head': {'sha': source}, 'merge_commit_sha': merge}
        api.pages_map['pulls/7/files'] = [{'filename': path} for path in paths]
        api.pages_map['actions/runs/98/artifacts'] = [
            {'name': 'legend-release-admission-' + record['admissionId'], 'expired': False}]
        def download(repo, run, name, directory):
            (directory / 'operation.json').write_text(json.dumps(record))
        with patch.object(m.VALIDATION_AUTHORITY, '_download_run_artifact', side_effect=download), \
             patch.object(m, 'ancestor', return_value=True), \
             patch.object(m.VALIDATION_AUTHORITY, 'package_inputs_compatible', return_value=True) as compatible, \
             patch.object(m.VALIDATION_AUTHORITY, 'compute_validated_package_evidence', return_value={
                 'reusable': True, 'revision': producer, 'packageIdentity': 'e' * 64}) as evidence:
            self.assertEqual([record], m._admission_records(api, run))
            compatible.assert_called_once_with(producer, source)
            evidence.assert_called_once_with(api.repo, producer, 'e' * 64, allow_equivalent=False)
            compatible.return_value = False
            with self.assertRaisesRegex(RuntimeError, 'not equivalent'):
                m._admission_records(api, run)
            compatible.return_value = True
            evidence.return_value = {'reusable': True, 'revision': source, 'packageIdentity': 'e' * 64}
            with self.assertRaisesRegex(RuntimeError, 'binding is missing or changed'):
                m._admission_records(api, run)
            evidence.return_value = {'reusable': False, 'revision': producer, 'packageIdentity': 'e' * 64}
            with self.assertRaisesRegex(RuntimeError, 'binding is missing or changed'):
                m._admission_records(api, run)

    def test_lease_binds_authorized_source_and_preserved_package_producer_separately(self):
        api = Api()
        authority, source, merge, producer = ('a' * 40, 'b' * 40, 'c' * 40, 'd' * 40)
        target = canonical_name('client')
        pr = {'number': 7, 'merged_at': '2026-10-03', 'base': {'ref': m.APPROVED},
              'head': {'sha': source}, 'merge_commit_sha': merge}
        api.api_map['pulls/7'] = pr
        api.pages_map['pulls/7/files'] = [{'filename': 'ClientApp/Program.cs'}]
        env = {'RELEASE_SHA': authority, 'AUTOMATIC_RELEASE': 'true', 'AUTOMATIC_SOURCE_PR': '7',
               'AUTOMATIC_VALIDATED_SHA': source, 'AUTOMATIC_SOURCE_MERGE_SHA': merge,
               'AUTOMATIC_TARGETS_JSON': json.dumps([target]), 'GITHUB_RUN_ID': '99', 'GITHUB_RUN_ATTEMPT': '1'}
        with patch.dict(os.environ, env), patch.object(m, 'staging_only', return_value=False), \
             patch.object(m, 'git', return_value=SimpleNamespace(stdout=authority)), \
             patch.object(m, 'ancestor', return_value=True), patch.object(m, 'candidate_validation', return_value=None), \
             patch.object(m, '_validated_package_evidence', return_value={
                 'reusable': True, 'revision': producer, 'packageIdentity': 'e' * 64}), \
             patch.object(m, 'admission_conflicts', return_value=[]), \
             patch.object(m.subprocess, 'run', return_value=SimpleNamespace(returncode=0, stdout='LEGEND_OPERATION_RESULT={"artifactId":1}')) as publish:
            result = m.admit_worker(api)
        self.assertTrue(result['admitted'])
        self.assertEqual(source, result['admission']['authorizedSourceRevision'])
        self.assertEqual(producer, result['admission']['applicationRevision'])
        self.assertEqual('e' * 64, result['admission']['packageIdentity'])
        self.assertEqual(result['admission']['admissionId'], m._admission_identity(result['admission']))
        published = json.loads(publish.call_args.kwargs['input'])
        self.assertEqual(result['admission'], published['record'])


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

    @patch.object(m, "_package_backfill_preflight", return_value={"allowed": True})
    @patch.object(m, "_package_backfill_running", return_value=False)
    @patch.object(m, "_validated_package_evidence", return_value={"reusable": False, "reason": "exact_validated_package_missing"})
    @patch.object(m, "pending_automatic_releases")
    def test_missing_automatic_package_dispatches_package_backfill_before_release(self, pending, _, __, ___):
        api = Api()
        pending.return_value = [{
            "authorizationSha": "b" * 40,
            "applicationRevision": "c" * 40,
            "targets": [canonical_name("portal")],
            "sourcePr": 385,
        }]

        result = m.dispatch_pending_automatic_release(api, "a" * 40)

        self.assertEqual(
            "dispatched for exact green automatic application revision",
            result["packageBackfill"],
        )
        self.assertEqual(
            [(m.PACKAGE_VALIDATION, {"package_revision": "c" * 40})],
            api.dispatched,
        )

    @patch.object(m, "_package_backfill_preflight", return_value={"allowed": True})
    @patch.object(m, "_package_backfill_running", return_value=True)
    @patch.object(m, "_validated_package_evidence", return_value={"reusable": False, "reason": "exact_validated_package_missing"})
    @patch.object(m, "pending_automatic_releases")
    def test_running_automatic_package_backfill_does_not_duplicate_dispatch(self, pending, _, __, ___):
        api = Api()
        pending.return_value = [{
            "authorizationSha": "b" * 40,
            "applicationRevision": "c" * 40,
            "targets": [canonical_name("portal")],
            "sourcePr": 385,
        }]

        result = m.dispatch_pending_automatic_release(api, "a" * 40)

        self.assertEqual("already queued or running", result["pendingCandidates"][0]["packageBackfill"])
        self.assertEqual([], api.dispatched)

    @patch.object(m, "_package_backfill_preflight", return_value={"allowed": True})
    @patch.object(m, "_package_backfill_running", return_value=False)
    @patch.object(m, "_validated_package_evidence", return_value={"reusable": False, "reason": "exact_validated_package_missing"})
    @patch.object(m, "pending_legacy_release_authorization")
    def test_missing_package_dispatches_package_only_architecture_recovery(self, pending, _, __, ___):
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

    @patch.object(m, "_package_backfill_preflight", return_value={"allowed": True})
    @patch.object(m, "_package_backfill_running", return_value=True)
    @patch.object(m, "_validated_package_evidence", return_value={"reusable": False, "reason": "exact_validated_package_missing"})
    @patch.object(m, "pending_legacy_release_authorization")
    def test_running_package_backfill_is_preserved_without_duplicate_dispatch(self, pending, _, __, ___):
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
    @patch.object(m, "_package_backfill_preflight", return_value={
        "allowed": False,
        "reason": "application_inputs_changed_since_validated_revision",
    })
    @patch.object(m, "_validated_package_evidence", return_value={
        "reusable": False,
        "reason": "exact_validated_package_missing",
    })
    @patch.object(m, "pending_legacy_release_authorization")
    def test_superseded_legacy_revision_never_dispatches_package_backfill(self, pending, _, __):
        api = Api()
        pending.return_value = {
            "authorizationSha": "b" * 40,
            "applicationRevision": "c" * 40,
            "targets": [canonical_name("portal")],
            "sourcePr": 42,
        }

        result = m.dispatch_pending_legacy_release(api, "a" * 40)

        self.assertEqual("SUPERSEDED", result["state"])
        self.assertEqual("not dispatched", result["packageBackfill"])
        self.assertEqual(
            "application_inputs_changed_since_validated_revision",
            result["packageReason"],
        )
        self.assertEqual([], api.dispatched)

    @patch.object(m, "_package_backfill_preflight", return_value={
        "allowed": False,
        "reason": "application_inputs_changed_since_validated_revision",
    })
    @patch.object(m, "_validated_package_evidence", return_value={
        "reusable": False,
        "reason": "exact_validated_package_missing",
    })
    @patch.object(m, "pending_automatic_releases")
    def test_superseded_automatic_revision_never_dispatches_package_backfill(self, pending, _, __):
        api = Api()
        pending.return_value = [{
            "authorizationSha": "b" * 40,
            "applicationRevision": "c" * 40,
            "targets": [canonical_name("portal")],
            "sourcePr": 385,
        }]

        result = m.dispatch_pending_automatic_release(api, "a" * 40)

        self.assertEqual("WAITING_FOR_DEPENDENCY", result["state"])
        self.assertEqual([], api.dispatched)
        self.assertEqual(
            "application_inputs_changed_since_validated_revision",
            result["pendingCandidates"][0]["packageReason"],
        )
        self.assertEqual("not dispatched", result["pendingCandidates"][0]["packageBackfill"])



class ReconcileSafety(unittest.TestCase):
    def setUp(self):
        queue = patch.object(m, "pending_automatic_releases", return_value=[])
        queue.start()
        self.addCleanup(queue.stop)

    @patch.object(m, "staging_only", return_value=False)
    @patch.object(m, "dispatch_pending_legacy_release", return_value=None)
    @patch.object(m, "dispatch_pending_automatic_release", return_value=None)
    def test_no_pending_release_means_no_release(self, _, __, ___):
        api = Api()
        self.assertEqual(
            {"state": "READY", "release": "no application publication required for exact approved head"},
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
    @patch.object(m, "release_queue_lease", return_value={"ownerPr": 463, "approved": "a" * 40})
    @patch.object(m, "promote_next_release_queue", return_value=None)
    @patch.object(m, "dispatch_pending_automatic_release")
    def test_control_only_owner_immediately_recovers_pending_application_release(self, recover, _, __, ___):
        api = Api()
        api.api_map["pulls/463"] = {
            "number": 463,
            "merged_at": "2026-10-05T02:37:22Z",
        }
        api.pages_map["pulls/463/files"] = [
            {"filename": "scripts/deploy-approved-app.py"},
            {"filename": "scripts/test-deploy-approved-app.py"},
        ]
        recovered = {
            "state": "RELEASE_DISPATCHED",
            "sourcePr": 462,
            "applicationRevision": "5" * 40,
            "directRelease": "automatic validated-merge release",
        }
        recover.return_value = recovered

        result = m.reconcile(api)

        self.assertEqual(recovered, result)
        recover.assert_called_once_with(api, "a" * 40)
        self.assertTrue(any(status[2] == "success" for status in api.statuses))

    @patch.object(m, "staging_only", return_value=False)
    @patch.object(m, "direct_only_request", return_value=True)
    def test_failed_exact_release_is_not_auto_replayed(self, _, __):
        api = Api()
        api.pages_map[DurableCandidateQueue.runs_path] = [{
            "id": 8,
            "head_sha": "a" * 40,
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
