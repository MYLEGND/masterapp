#!/usr/bin/env python3
"""Adversarial tests for the single protected approved-branch release lifecycle."""
import importlib.util
import hashlib
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


class GitHubTransportRetry(unittest.TestCase):
    class Response:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return False

        def read(self):
            return b'{"ok":true}'

    def client(self):
        with patch.dict(os.environ, {
            "GITHUB_REPOSITORY": "MYLEGND/masterapp",
            "GH_TOKEN": "fixture-token",
        }, clear=False):
            return m.GitHub()

    def test_job_pagination_requires_complete_stable_authenticated_inventory(self):
        api = self.client()
        jobs = [{'id': 1, 'name': 'admission', 'conclusion': 'success'},
                {'id': 2, 'name': 'preserve-rollback', 'conclusion': 'skipped'}]
        for payload in [{'jobs': jobs, 'total_count': 3}, {'jobs': jobs},
                        {'jobs': jobs, 'total_count': True},
                        {'jobs': jobs + [jobs[0]], 'total_count': 3}]:
            with self.subTest(payload=payload), patch.object(api, 'api', return_value=payload):
                with self.assertRaises(RuntimeError):
                    api.pages('actions/runs/98/attempts/1/jobs', 'jobs')
        full = [{'id': i + 1} for i in range(100)]
        with patch.object(api, 'api', side_effect=[{'jobs': full, 'total_count': 101},
                                                  {'jobs': [{'id': 101}], 'total_count': 102}]):
            with self.assertRaisesRegex(RuntimeError, 'changing'):
                api.pages('actions/runs/98/attempts/1/jobs', 'jobs')
        with patch.object(api, 'api', side_effect=[{'jobs': full, 'total_count': 101},
                                                  {'jobs': [{'id': 101}], 'total_count': 101}]):
            self.assertEqual(101, len(api.pages('actions/runs/98/attempts/1/jobs', 'jobs')))
        with patch.object(api, 'api', return_value=['unchanged']):
            self.assertEqual(['unchanged'], api.pages('keyless'))

    def test_remote_disconnect_retries_read_only_get_in_place(self):
        api = self.client()
        with patch.object(
            m.urllib.request,
            "urlopen",
            side_effect=[m.http.client.RemoteDisconnected("transient"), self.Response()],
        ) as request, patch.object(m.time, "sleep") as sleep:
            self.assertEqual({"ok": True}, api.api("actions/runs/1/jobs?filter=latest"))
        self.assertEqual(2, request.call_count)
        sleep.assert_called_once_with(1)

    def test_remote_disconnect_never_replays_write(self):
        api = self.client()
        with patch.object(
            m.urllib.request,
            "urlopen",
            side_effect=m.http.client.RemoteDisconnected("ambiguous write"),
        ) as request, patch.object(m.time, "sleep") as sleep:
            with self.assertRaisesRegex(RuntimeError, "transport unavailable"):
                api.api("statuses/" + "a" * 40, {"state": "success"}, method="POST")
        self.assertEqual(1, request.call_count)
        sleep.assert_not_called()


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

    def test_guard_rejects_legacy_publication_timeout(self):
        class Drift(Api):
            def text(self, revision, path):
                value = super().text(revision, path)
                if path == "scripts/deploy-approved-app.py":
                    return value.replace(
                        "PUBLICATION_RECONCILE_TIMEOUT_SECONDS = 420",
                        "PUBLICATION_RECONCILE_TIMEOUT_SECONDS = 1200",
                    )
                return value
        result = m.candidate_control_plane_integrity(
            Drift(), {"head": {"sha": "b" * 40}}, ["scripts/deploy-approved-app.py"]
        )
        self.assertIn("timing", result)

    def test_guard_rejects_workflow_owned_receipt_recovery(self):
        class Drift(Api):
            def text(self, revision, path):
                value = super().text(revision, path)
                if path == ".github/workflows/all-intentional-direct-release-20260918.yml":
                    return value + "\n# transactionrecovery\n"
                return value
        result = m.candidate_control_plane_integrity(
            Drift(), {"head": {"sha": "b" * 40}},
            [".github/workflows/all-intentional-direct-release-20260918.yml"],
        )
        self.assertIn("legacy workflow-owned receipt recovery", result)

    def test_guard_rejects_unprotecting_release_execution_inputs(self):
        class Drift(Api):
            def text(self, revision, path):
                value = super().text(revision, path)
                if path == "scripts/validation-resume.py":
                    return value.replace('    "scripts/release-workflow.py",\n', "")
                return value
        result = m.candidate_control_plane_integrity(
            Drift(), {"head": {"sha": "b" * 40}}, ["scripts/validation-resume.py"]
        )
        self.assertIn("release execution control inputs", result)

    def test_guard_rejects_write_capable_production_diagnostics(self):
        class Drift(Api):
            def text(self, revision, path):
                value = super().text(revision, path)
                if path == ".github/workflows/deployment-diagnostics.yml":
                    return value + "\n# list-publishing-credentials\n"
                return value
        result = m.candidate_control_plane_integrity(
            Drift(), {"head": {"sha": "b" * 40}},
            [".github/workflows/deployment-diagnostics.yml"],
        )
        self.assertIn("no longer read-only", result)

    def test_guard_rejects_retired_deployment_bypass_reintroduction(self):
        result = m.candidate_control_plane_integrity(
            Api(), {"head": {"sha": "b" * 40}}, ["deploy-portal.sh"]
        )
        self.assertIn("retired alternate production deployment path", result)


    def test_guard_rejects_removing_durable_publication_receipt_proof(self):
        class Drift(Api):
            def text(self, revision, path):
                value = super().text(revision, path)
                if path == "scripts/release-lifecycle.py":
                    return value.replace(
                        "        match = re.fullmatch(r'legend-release-operation-success-([a-f0-9]{64})', name)\n",
                        "        match = re.fullmatch(r'legend-release-operation-intent-([a-f0-9]{64})', name)\n",
                    )
                return value
        result = m.candidate_control_plane_integrity(
            Drift(), {"head": {"sha": "b" * 40}}, ["scripts/release-lifecycle.py"]
        )
        self.assertIn("durable historical publication proof", result)

    def test_guard_rejects_removing_strict_forward_supersession_contract(self):
        class Drift(Api):
            def text(self, revision, path):
                value = super().text(revision, path)
                if path == "scripts/release-lifecycle.py":
                    return value.replace(
                        '        return ancestor(old_revision, new_revision)\n',
                        '        return True\n',
                    )
                return value
        result = m.candidate_control_plane_integrity(
            Drift(), {"head": {"sha": "b" * 40}}, ["scripts/release-lifecycle.py"]
        )
        self.assertIn("strict descendant target-scoped stale-lease supersession", result)

    def test_guard_rejects_ephemeral_target_outcome_transaction_gate(self):
        class Drift(Api):
            def text(self, revision, path):
                value = super().text(revision, path)
                if path == ".github/workflows/all-intentional-direct-release-20260918.yml":
                    return value.replace(
                        '          python3 scripts/deploy-approved-app.py \\\n',
                        '          python3 scripts/release-workflow.py --verify-outcomes --selected-targets "$SELECTED_TARGETS"\n'
                        '          python3 scripts/deploy-approved-app.py \\\n',
                        1,
                    )
                return value
        result = m.candidate_control_plane_integrity(
            Drift(), {"head": {"sha": "b" * 40}},
            [".github/workflows/all-intentional-direct-release-20260918.yml"],
        )
        self.assertIn("target outcome", result)

    def test_guard_rejects_lifecycle_identity_that_stops_hashing_absence(self):
        class Drift(Api):
            def text(self, revision, path):
                value = super().text(revision, path)
                if path == "scripts/validation-resume.py":
                    return value.replace(
                        '            digest.update(b"absent\\0")\n',
                        '            digest.update(b"present\\0")\n',
                    )
                return value
        result = m.candidate_control_plane_integrity(
            Drift(), {"head": {"sha": "b" * 40}}, ["scripts/validation-resume.py"]
        )
        self.assertIn("lifecycle absent-path identity", result)

    def test_guard_rejects_weakened_authenticated_execution_authority(self):
        class Drift(Api):
            def text(self, revision, path):
                value = super().text(revision, path)
                if path == "scripts/validation-resume.py":
                    return value.replace(
                        'def assert_protected_release_execution():',
                        'def assert_unprotected_release_execution():',
                    )
                return value
        result = m.candidate_control_plane_integrity(
            Drift(), {"head": {"sha": "b" * 40}}, ["scripts/validation-resume.py"]
        )
        self.assertIn("authenticated canonical release execution guard", result)

    def test_guard_rejects_mutation_worker_that_drops_execution_authentication(self):
        class Drift(Api):
            def text(self, revision, path):
                value = super().text(revision, path)
                if path == "scripts/release-prepublication.py":
                    return value.replace(
                        "    release_authority().assert_protected_release_execution()\n",
                        "",
                    )
                return value
        result = m.candidate_control_plane_integrity(
            Drift(), {"head": {"sha": "b" * 40}}, ["scripts/release-prepublication.py"]
        )
        self.assertIn("lost protected release execution guard", result)


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
        api.pages_map["pulls/442/files"] = [{"filename": "AgentPortal/Program.cs"}]
        api.pages_map["pulls/450/files"] = [{"filename": "AgentPortal/Program.cs"}]

        claimed = m.claim_release_queue(api, first)
        queued = m.claim_release_queue(api, later)

        self.assertEqual("RELEASE_QUEUE_OWNER", claimed["state"])
        self.assertEqual("RELEASE_QUEUED", queued["state"])
        self.assertEqual(442, queued["ownerPr"])
        self.assertEqual(442, m.release_queue_lease(api)["ownerPr"])

    def test_owner_head_change_keeps_same_queue_ownership(self):
        api = Api()
        first = self.pr(442, "b" * 40)
        api.pages_map["pulls/442/files"] = [{"filename": "AgentPortal/Program.cs"}]
        m.claim_release_queue(api, first)
        updated = self.pr(442, "c" * 40)

        claimed = m.claim_release_queue(api, updated)

        self.assertEqual("RELEASE_QUEUE_OWNER", claimed["state"])
        self.assertEqual(442, m.release_queue_lease(api)["ownerPr"])

    def test_nonpublishing_candidate_never_claims_application_queue(self):
        api = Api()
        governance = self.pr(442, "b" * 40)
        api.pages_map["pulls/442/files"] = [
            {"filename": ".github/CODEOWNERS"},
            {"filename": ".github/agents/legend-intelligence-engineer.agent.md"},
            {"filename": ".github/agents/masterapp-chief-architect.agent.md"},
            {"filename": ".github/agents/masterapp-cross-platform-engineer.agent.md"},
            {"filename": ".github/agents/masterapp-release-reviewer.agent.md"},
            {"filename": ".github/agents/masterapp-runtime-engineer.agent.md"},
            {"filename": ".github/agents/masterapp-verification-engineer.agent.md"},
            {"filename": ".github/copilot-instructions.md"},
            {"filename": "AGENTS.md"},
        ]

        result = m.claim_release_queue(api, governance)

        self.assertEqual("RELEASE_QUEUE_NOT_REQUIRED", result["state"])
        self.assertIsNone(m.release_queue_lease(api)["ownerPr"])
        self.assertFalse(any(
            status[1] == m.RELEASE_QUEUE_REQUEST_CONTEXT
            for status in api.statuses
        ))

    @patch.object(m, "staging_only", return_value=False)
    @patch.object(m, "git", return_value=SimpleNamespace(returncode=0, stdout="", stderr=""))
    @patch.object(m, "merge_validated")
    def test_pending_updates_processes_only_active_owner(self, merge_validated, _, __):
        api = Api()
        owner = self.pr(442, "b" * 40)
        later = self.pr(450, "c" * 40)
        api.api_map["pulls/442"] = owner
        api.api_map["pulls/450"] = later
        api.pages_map["pulls/442/files"] = [{"filename": "AgentPortal/Program.cs"}]
        api.pages_map["pulls/450/files"] = [{"filename": "AgentPortal/Program.cs"}]
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

    @patch.object(m, "staging_only", return_value=False)
    @patch.object(m, "git", return_value=SimpleNamespace(returncode=0, stdout="", stderr=""))
    def test_validation_completion_merges_control_repair_past_application_lease(self, _, __):
        for merged_owner in (False, True):
            with self.subTest(merged_owner=merged_owner):
                api = Api()
                owner, repair, waiting = self.pr(505, "5" * 40), self.pr(514, "b" * 40), self.pr(513, "d" * 40)
                if merged_owner:
                    owner.update(state='closed', merged_at='2026-10-07T00:00:00Z')
                for pr in (owner, repair, waiting):
                    api.api_map[f"pulls/{pr['number']}"] = pr
                    api.pages_map[f"pulls/{pr['number']}/files"] = [{'filename':
                        'AgentPortal/Program.cs' if pr is owner else 'scripts/release-lifecycle.py'}]
                api.pages_map['pulls?state=open&base=legend%2Fapproved-changes'] = [repair, waiting]
                api.context_status(api.ref(m.APPROVED), m.RELEASE_QUEUE_CONTEXT, 'pending', 'owner-pr=505 validation-to-production')
                merged = []
                api.api_map['pulls/514/merge'] = lambda data, method: (merged.append((data, method)) or {'merged': True, 'sha': 'c' * 40})
                with patch.object(m, 'candidate_validation', return_value='pending exact-head checks'):
                    self.assertEqual('VALIDATING', m.integrate(api, 514)['state'])
                with patch.object(m, 'candidate_validation', side_effect=lambda api, pr:
                                  'unrelated checks pending' if pr['number'] == 513 else None):
                    result = m.pending_updates(api)
                self.assertEqual('MERGED', result['state'])
                self.assertEqual(514, result['mergedPr'])
                self.assertEqual([({'merge_method': 'merge', 'sha': 'b' * 40}, 'PUT')], merged)
                self.assertEqual(505, m.release_queue_lease(api)['ownerPr'])
                self.assertEqual([], api.dispatched)
                # The owning workflow refreshes approved checkout before recovery.
                api.refs[m.APPROVED] = 'c' * 40
                api.context_status('c' * 40, m.RELEASE_QUEUE_CONTEXT, 'pending', 'owner-pr=505 validation-to-production')
                with patch.object(m, 'dispatch_pending_automatic_release', return_value={'state': 'RELEASE_DISPATCHED', 'sourcePr': 505}) as recover:
                    self.assertEqual(505, m.reconcile(api)['sourcePr'])
                    recover.assert_called_once_with(api, 'c' * 40)

    @patch.object(m, "staging_only", return_value=False)
    @patch.object(m, "git", return_value=SimpleNamespace(returncode=0, stdout="", stderr=""))
    def test_ready_application_reconsidered_after_validation_without_earlier_claim(self, _, __):
        api = Api()
        pr = self.pr(514, 'b' * 40)
        api.api_map['pulls/514'] = pr
        api.pages_map['pulls/514/files'] = [{'filename': 'AgentPortal/Program.cs'}]
        api.pages_map['pulls?state=open&base=legend%2Fapproved-changes'] = [pr]
        api.api_map['pulls/514/merge'] = {'merged': True, 'sha': 'c' * 40}
        with patch.object(m, 'candidate_validation', return_value='pending'):
            self.assertEqual('VALIDATING', m.integrate(api, 514)['state'])
        self.assertIsNone(m.release_queue_lease(api)['ownerPr'])
        with patch.object(m, 'candidate_validation', return_value=None):
            self.assertEqual('MERGED', m.pending_updates(api)['state'])
        self.assertEqual(514, m.release_queue_lease(api)['ownerPr'])

    @patch.object(m, "staging_only", return_value=False)
    @patch.object(m, "git", return_value=SimpleNamespace(returncode=0, stdout="", stderr=""))
    def test_pending_control_repair_rejects_pending_integrity_and_changed_head(self, _, __):
        for condition in ('pending', 'integrity', 'changed', 'stale'):
            with self.subTest(condition=condition):
                api = Api()
                pr = self.pr(514, 'b' * 40)
                api.api_map['pulls/514'] = self.pr(514, 'd' * 40) if condition == 'changed' else pr
                api.pages_map['pulls/514/files'] = [{'filename': 'scripts/release-lifecycle.py'}]
                api.pages_map['pulls?state=open&base=legend%2Fapproved-changes'] = [pr]
                api.api_map['pulls/514/merge'] = lambda *_: self.fail('Unproven candidate merged')
                with patch.object(m, 'candidate_validation', return_value='pending' if condition == 'pending' else None), \
                     patch.object(m, 'candidate_control_plane_integrity', return_value='source failed' if condition == 'integrity' else None), \
                     patch.object(m, 'approved_head_state', return_value={'current': condition != 'stale'}), \
                     patch.object(m, 'sync_candidate_to_current_approved', side_effect=AssertionError('Unrelated head must not be mutated')):
                    result = m.pending_updates(api)
                self.assertNotEqual('MERGED', result.get('state'))
                self.assertIsNone(m.release_queue_lease(api)['ownerPr'])

    def test_released_queue_promotes_next_requested_pr(self):
        api = Api()
        owner = self.pr(442, "b" * 40)
        later = self.pr(450, "c" * 40)
        api.pages_map["pulls?state=open&base=legend%2Fapproved-changes"] = [later]
        api.pages_map["pulls/442/files"] = [{"filename": "AgentPortal/Program.cs"}]
        api.pages_map["pulls/450/files"] = [{"filename": "AgentPortal/Program.cs"}]
        api.api_map["pulls/450"] = later
        m.claim_release_queue(api, owner)
        m._request_release_queue(api, later)
        m._release_release_queue(api, api.ref(m.APPROVED), 442, "terminal-live-provenance")

        result = m.promote_next_release_queue(api)

        self.assertEqual("RELEASE_QUEUE_PROMOTED", result["state"])
        self.assertEqual(450, result["pr"])
        self.assertEqual(450, m.release_queue_lease(api)["ownerPr"])


    @patch.object(m, "staging_only", return_value=False)
    @patch.object(m, "git", return_value=SimpleNamespace(returncode=0, stdout="", stderr=""))
    @patch.object(m.time, "sleep")
    def test_legacy_nonpublishing_owner_is_released_and_product_is_promoted(self, _sleep, _, __):
        api = Api()
        governance = self.pr(442, "b" * 40)
        product = self.pr(450, "c" * 40)
        api.api_map["pulls/442"] = governance
        api.api_map["pulls/450"] = product
        api.pages_map["pulls/442/files"] = [
            {"filename": ".github/CODEOWNERS"},
            {"filename": "AGENTS.md"},
        ]
        api.pages_map["pulls/450/files"] = [{"filename": "AgentPortal/Program.cs"}]
        api.pages_map["pulls?state=open&base=legend%2Fapproved-changes"] = [governance, product]
        api.pages_map["pulls?state=closed&base=legend%2Fapproved-changes"] = []

        # Reproduce a lease written by the older lifecycle generation.
        api.context_status(
            api.ref(m.APPROVED),
            m.RELEASE_QUEUE_CONTEXT,
            "pending",
            "owner-pr=442 validation-to-production",
        )
        m._request_release_queue(api, product)

        result = m.pending_updates(api)

        self.assertEqual("RELEASE_QUEUE_PROMOTED", result["state"])
        self.assertEqual(450, result["pr"])
        self.assertEqual(450, m.release_queue_lease(api)["ownerPr"])



class MergedFailedQueueOwnerRecovery(unittest.TestCase):
    def setUp(self):
        self.api = Api()
        self.owner = {
            'number': 522, 'state': 'closed', 'merged_at': '2026-10-07T23:33:45Z',
            'head': {'sha': 'b' * 40}, 'merge_commit_sha': 'c' * 40,
        }
        self.run = {
            'id': 37703022717, 'status': 'completed', 'conclusion': 'failure',
            'head_sha': 'c' * 40,
            'display_title': m.release_dispatch_identity(522, 'b' * 40, 'c' * 40),
        }
        self.record = {
            'sourcePr': 522, 'authorizedSourceRevision': 'b' * 40,
            'sourceMergeSha': 'c' * 40, 'applicationRevision': 'b' * 40,
            'executionAuthority': 'c' * 40,
        }

    def test_positive_terminal_no_write_receipt_allows_old_owner_to_yield(self):
        with patch.object(m, 'direct_release_runs', return_value=[self.run]), \
             patch.object(m, '_admission_records', return_value=[self.record]), \
             patch.object(m, '_historical_fenced_prepublication_nonentry', return_value=True) as proof:
            self.assertTrue(m._merged_owner_terminal_nonentry_proven(self.api, self.owner))
            proof.assert_called_once_with(self.api, self.run, self.record)

    def test_proven_reused_immutable_package_does_not_have_to_equal_pr_head(self):
        # The admission reader independently authenticates the exact source PR,
        # canonical package-input equivalence and immutable package evidence.
        reused = dict(self.record, applicationRevision='d' * 40)
        with patch.object(m, 'direct_release_runs', return_value=[self.run]), \
             patch.object(m, '_admission_records', return_value=[reused]) as admission, \
             patch.object(m, '_historical_fenced_prepublication_nonentry',
                          return_value=True) as no_write:
            self.assertTrue(m._merged_owner_terminal_nonentry_proven(
                self.api, self.owner))
        admission.assert_called_once_with(self.api, self.run)
        no_write.assert_called_once_with(self.api, self.run, reused)

    def test_reused_package_cannot_change_authorized_pr_source(self):
        reused = dict(self.record, applicationRevision='d' * 40,
                      authorizedSourceRevision='e' * 40)
        with patch.object(m, 'direct_release_runs', return_value=[self.run]), \
             patch.object(m, '_admission_records', return_value=[reused]), \
             patch.object(m, '_historical_fenced_prepublication_nonentry',
                          side_effect=AssertionError('Untrusted source must not reach proof')):
            self.assertFalse(m._merged_owner_terminal_nonentry_proven(
                self.api, self.owner))

    def test_active_duplicate_or_excessive_history_keeps_ownership(self):
        for attempts in (
            [dict(self.run, status='in_progress', conclusion=None)],
            [self.run, dict(self.run)],  # Duplicate run identity
            [dict(self.run, id=37703022718 + index)
             for index in range(17)],  # excessive inventory never auto-releases
        ):
            with self.subTest(attempts=attempts), \
                 patch.object(m, 'direct_release_runs', return_value=attempts), \
                 patch.object(m, '_admission_records', side_effect=AssertionError('No ambiguous history read')):
                self.assertFalse(m._merged_owner_terminal_nonentry_proven(self.api, self.owner))
        for records in ([], [self.record, self.record],
                        [dict(self.record, executionAuthority='d' * 40)]):
            with self.subTest(records=records), \
                 patch.object(m, 'direct_release_runs', return_value=[self.run]), \
                 patch.object(m, '_admission_records', return_value=records), \
                 patch.object(m, '_historical_fenced_prepublication_nonentry',
                              side_effect=AssertionError('Invalid record cannot prove non-entry')):
                self.assertFalse(m._merged_owner_terminal_nonentry_proven(self.api, self.owner))

    def test_failed_and_cancelled_nonentry_attempts_each_require_exact_proof(self):
        cancelled = dict(
            self.run, id=37718975034, conclusion='cancelled',
            head_sha='d' * 40)
        later_failure = dict(
            self.run, id=37719140124, head_sha='d' * 40)
        later_record = dict(self.record, executionAuthority='d' * 40)
        for cancellation_proven in (True, False):
            with self.subTest(cancellation_proven=cancellation_proven), \
                 patch.object(m, 'direct_release_runs',
                              return_value=[self.run, cancelled, later_failure]), \
                 patch.object(m, '_admission_records',
                              side_effect=[[self.record], [later_record]]) as records, \
                 patch.object(m, '_historical_fenced_prepublication_nonentry',
                              side_effect=[True, True]), \
                 patch.object(m, '_cancelled_before_admission_nonentry',
                              return_value=cancellation_proven) as no_entry, \
                 patch.object(m, 'ancestor', return_value=True):
                self.assertEqual(cancellation_proven,
                                 m._merged_owner_terminal_nonentry_proven(
                                     self.api, self.owner))
            no_entry.assert_called_once_with(self.api, cancelled)
            self.assertEqual(2 if cancellation_proven else 1, records.call_count)

    def test_two_terminal_no_write_runs_under_descendant_authorities_yield(self):
        followup = dict(
            self.run, id=37706920756, head_sha='d' * 40,
            display_title=m.release_dispatch_identity(522, 'b' * 40, 'd' * 40),
        )
        next_record = dict(self.record, executionAuthority='d' * 40)
        with patch.object(m, 'direct_release_runs',
                          return_value=[self.run, followup]), \
             patch.object(m, '_admission_records',
                          side_effect=[[self.record], [next_record]]) as read, \
             patch.object(m, 'ancestor', return_value=True) as lineage, \
             patch.object(m, '_historical_fenced_prepublication_nonentry',
                          side_effect=[True, True]) as proof:
            self.assertTrue(m._merged_owner_terminal_nonentry_proven(
                self.api, self.owner))
        self.assertEqual(2, read.call_count)
        self.assertEqual(2, proof.call_count)
        lineage.assert_called_once_with('c' * 40, 'd' * 40)

    def test_two_runs_do_not_yield_with_one_unproven_mutation(self):
        second = dict(self.run, id=37706920756, head_sha='d' * 40)
        next_record = dict(self.record, executionAuthority='d' * 40)
        with patch.object(m, 'direct_release_runs',
                          return_value=[self.run, second]), \
             patch.object(m, '_admission_records',
                          side_effect=[[self.record], [next_record]]), \
             patch.object(m, 'ancestor', return_value=True), \
             patch.object(m, '_historical_fenced_prepublication_nonentry',
                          side_effect=[True, False]):
            self.assertFalse(m._merged_owner_terminal_nonentry_proven(
                self.api, self.owner))

    def test_divergent_forward_release_authority_remains_blocking(self):
        forward = dict(self.run, id=37706920756, head_sha='d' * 40)
        with patch.object(m, 'direct_release_runs',
                          return_value=[self.run, forward]), \
             patch.object(m, '_admission_records',
                          return_value=[self.record]), \
             patch.object(m, 'ancestor', return_value=False):
            self.assertFalse(m._merged_owner_terminal_nonentry_proven(
                self.api, self.owner))

    def test_entered_or_unknown_mutation_never_disposes_merged_queue(self):
        with patch.object(m, 'direct_release_runs', return_value=[self.run]), \
             patch.object(m, '_admission_records', return_value=[self.record]), \
             patch.object(m, '_historical_fenced_prepublication_nonentry', return_value=False):
            self.assertFalse(m._merged_owner_terminal_nonentry_proven(self.api, self.owner))

    @patch.object(m, 'staging_only', return_value=False)
    @patch.object(m, 'git', return_value=SimpleNamespace(returncode=0, stdout='', stderr=''))
    def test_proven_no_write_owner_promotes_validated_waiting_pr(self, _git, _staging):
        product = {
            'number': 523, 'state': 'open', 'draft': False,
            'author_association': 'OWNER', 'base': {'ref': m.APPROVED},
            'head': {'sha': 'd' * 40, 'ref': 'repair/probe',
                     'repo': {'full_name': self.api.repo}},
        }
        self.api.api_map['pulls/522'] = self.owner
        self.api.api_map['pulls/523'] = product
        self.api.pages_map['pulls/523/files'] = [{'filename': 'AgentPortal/Program.cs'}]
        self.api.pages_map['pulls?state=open&base=legend%2Fapproved-changes'] = [product]
        self.api.context_status(self.api.ref(m.APPROVED),
                                m.RELEASE_QUEUE_CONTEXT, 'pending',
                                'owner-pr=522 validation-to-production')
        m._request_release_queue(self.api, product)
        with patch.object(m, '_merged_owner_terminal_nonentry_proven', return_value=True), \
             patch.object(m, 'candidate_validation', return_value=None), \
             patch.object(m, 'sync_candidate_to_current_approved', return_value=None), \
             patch.object(m, '_rerun_required_validations', return_value={'rerun': [], 'missing': []}):
            result = m.pending_updates(self.api)
        self.assertEqual('RELEASE_QUEUE_PROMOTED', result['state'])
        self.assertEqual(523, result['pr'])
        self.assertEqual(523, m.release_queue_lease(self.api)['ownerPr'])


class AutomaticMergeRelease(unittest.TestCase):
    @patch.object(m, "candidate_validation", return_value=None)
    def test_green_merge_defers_one_dispatch_to_same_workflow_reconciliation(self, _):
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

    def test_exact_live_failed_release_gets_one_bounded_proof_recovery(self):
        api = Api()
        approved = "c" * 40
        pr = {"number": 77, "head": {"sha": "b" * 40}}
        identity = m.release_dispatch_identity(77, pr["head"]["sha"], approved)
        failed = {
            "id": 100,
            "display_title": identity,
            "head_sha": approved,
            "status": "completed",
            "conclusion": "failure",
        }
        with patch.object(m, "_never_admitted", return_value=False), \
             patch.object(m, "_release_exact_live_terminal", return_value=True):
            self.assertIsNone(
                m.automatic_release_admission(api, pr, approved, [failed])
            )
            second = dict(failed, id=101)
            blocked = m.automatic_release_admission(
                api, pr, approved, [failed, second]
            )
        self.assertEqual("FAILED_NEEDS_REPAIR", blocked["state"])
        self.assertIn("bounded non-entry/exact-live recovery exhausted", blocked["retained"])

    def test_nonentered_exact_release_gets_one_bounded_admission_retry(self):
        api = Api()
        approved = "c" * 40
        pr = {"number": 77, "head": {"sha": "b" * 40}}
        identity = m.release_dispatch_identity(77, pr["head"]["sha"], approved)
        failed = {
            "id": 100,
            "display_title": identity,
            "head_sha": approved,
            "status": "completed",
            "conclusion": "success",
        }
        with patch.object(m, "_never_admitted", return_value=True) as never, \
             patch.object(m, "_release_exact_live_terminal", side_effect=AssertionError("non-entry needs no live proof")):
            self.assertIsNone(
                m.automatic_release_admission(api, pr, approved, [failed])
            )
            second = dict(failed, id=101)
            blocked = m.automatic_release_admission(
                api, pr, approved, [failed, second]
            )
        never.assert_called_once_with(api, failed)
        self.assertEqual("FAILED_NEEDS_REPAIR", blocked["state"])
        self.assertIn("bounded non-entry/exact-live recovery exhausted", blocked["retained"])

    @patch.object(m, "candidate_validation", return_value=None)
    def test_control_only_green_merge_defers_recovery_until_refreshed_checkout(self, _):
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


    @patch.object(m, "candidate_validation", return_value=None)
    def test_product_merge_failure_releases_publication_lease(self, _):
        api = Api()
        pr = {"number": 79, "head": {"sha": "d" * 40}}
        api.pages_map["pulls/79/files"] = [{"filename": "AgentPortal/Program.cs"}]

        def blocked_merge(_data, _method):
            raise RuntimeError("GitHub PUT pulls/79/merge: HTTP 405")

        api.api_map["pulls/79/merge"] = blocked_merge

        result = m.merge_validated(api, pr)

        self.assertIn("retained", result)
        self.assertIsNone(m.release_queue_lease(api)["ownerPr"])
        self.assertTrue(any(
            status[1] == m.RELEASE_QUEUE_CONTEXT
            and status[2] == "success"
            and "merge-not-completed" in status[3]
            for status in api.statuses
        ))


    @patch.object(m, "candidate_validation", return_value=None)
    def test_nonmergeable_validated_pr_is_retained_not_fatal(self, _):
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
    runs_path = "actions/runs?branch=legend%2Fapproved-changes&event=workflow_dispatch"

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
        self.assertTrue(all("event=workflow_dispatch" in path for path in requested if path.startswith("actions/runs?branch=")))
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

    def test_nonmutating_current_approved_release_does_not_block_queue_promotion(self):
        run = {
            "id": 99,
            "run_attempt": 1,
            "path": ".github/workflows/" + m.DIRECT,
            "head_branch": m.APPROVED,
            "head_sha": self.approved,
            "status": "completed",
            "conclusion": "failure",
        }
        self.api.pages_map[self.runs_path] = [run]
        promoted = {"state": "RELEASE_QUEUE_ACQUIRED", "pr": 487}
        with patch.object(m, "release_queue_lease", return_value={
                 "approved": self.approved, "ownerPr": None, "status": None}), \
             patch.object(m, "dispatch_pending_automatic_release", return_value=None), \
             patch.object(m, "dispatch_pending_legacy_release", return_value=None), \
             patch.object(m, "_never_admitted", return_value=True) as never, \
             patch.object(m, "promote_next_release_queue", return_value=promoted) as promote:
            result = m.reconcile(self.api)
        self.assertEqual(promoted, result)
        never.assert_called_once_with(self.api, run)
        promote.assert_called_once_with(self.api)

    def test_ambiguous_current_approved_release_remains_blocking(self):
        run = {
            "id": 99,
            "run_attempt": 1,
            "path": ".github/workflows/" + m.DIRECT,
            "head_branch": m.APPROVED,
            "head_sha": self.approved,
            "status": "completed",
            "conclusion": "failure",
        }
        self.api.pages_map[self.runs_path] = [run]
        with patch.object(m, "release_queue_lease", return_value={
                 "approved": self.approved, "ownerPr": None, "status": None}), \
             patch.object(m, "dispatch_pending_automatic_release", return_value=None), \
             patch.object(m, "dispatch_pending_legacy_release", return_value=None), \
             patch.object(m, "_never_admitted", return_value=False), \
             patch.object(m, "release_execution_state", return_value="FAILED_NEEDS_REPAIR"), \
             patch.object(m, "promote_next_release_queue") as promote:
            result = m.reconcile(self.api)
        self.assertEqual("FAILED_NEEDS_REPAIR", result["state"])
        self.assertIn("terminal proof", result["retained"])
        promote.assert_not_called()

    def test_changed_source_pr_identity_is_retained_after_queue_discovery(self):
        self.candidate(1, "b" * 40, "d" * 40, ["Protect-Website/Program.cs"])
        self.api.api_map["pulls/1"] = {"number": 1, "head": {"sha": "f" * 40}}
        result = m.reconcile(self.api)
        self.assertIn("changed", result["pendingCandidates"][0]["retained"])
        self.assertEqual([], self.api.dispatched)


class ReleaseExecutionStateTests(unittest.TestCase):
    def test_renamed_parallel_mutation_fanout_reports_deploying(self):
        api = Api()
        run = {'id': 99, 'status': 'in_progress', 'conclusion': None}
        api.pages_map['actions/runs/99/jobs?filter=latest'] = [{
            'name': 'release',
            'steps': [{
                'name': 'Submit canonical selected targets in parallel',
                'status': 'in_progress',
                'conclusion': None,
            }],
        }]
        self.assertEqual('DEPLOYING', m.release_execution_state(api, run))



class GeneratedPublicationStages(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location('workflow_generation', Path(__file__).with_name('release-workflow.py'))
        self.generator = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.generator)

    def test_workflow_target_steps_and_outcome_checks_are_derived_from_inventory(self):
        text = self.generator.WORKFLOW.read_text()
        self.assertEqual(text, self.generator.render(text, m.VALIDATION_AUTHORITY.RELEASE_TARGETS))

    def test_parallel_publication_is_one_mutation_fanout_with_target_specific_result_gates(self):
        text = self.generator.WORKFLOW.read_text()
        self.assertEqual(1, text.count('name: Submit canonical selected targets in parallel'))
        self.assertEqual(1, text.count('--publish-prepared-parallel'))
        self.assertNotIn('name: Publish canonical target (', text)
        self.assertIn("result.get('durableReceiptProven') is not True", text)
        parallel = text.index('name: Submit canonical selected targets in parallel')
        for key in m.VALIDATION_AUTHORITY.RELEASE_TARGETS:
            child = text.index(f'name: Confirm first-pass durable publication receipt ({key})')
            self.assertGreater(child, parallel)
            self.assertIn(f'/tmp/release-target-results/{key}.json', text)

    def test_parallel_publication_is_classified_as_mutation_for_historical_fail_closed_proof(self):
        source = self.generator.WORKFLOW.read_text()
        mutation = m._historical_release_mutation_steps(source)
        self.assertIsNotNone(mutation)
        self.assertIn('Submit canonical selected targets in parallel', mutation)

    def test_historical_serial_prepublication_generation_remains_recognizable(self):
        source = self.generator.WORKFLOW.read_text()
        blocks = m.VALIDATION_AUTHORITY.named_step_blocks(
            m.VALIDATION_AUTHORITY._job_blocks(source)['release']
        )
        current = 'Synchronize canonical pre-publication resource lanes'
        self.assertIn(current, blocks)
        legacy_names = (
            'Synchronize selected shared authorization and publisher runtimes',
            'Synchronize selected editor ticket authority',
            'Prepare canonical business website routing authority',
            'Apply additive diagnostics migrations before restarting apps',
        )
        legacy = ''.join(
            f"      - name: {name}\n        run: echo historical-owner\n"
            for name in legacy_names
        )
        historical = source.replace(blocks[current], legacy)
        mutation = m._historical_release_mutation_steps(historical)
        self.assertIsNotNone(mutation)
        self.assertNotIn(current, mutation)
        for name in legacy_names:
            self.assertIn(name, mutation)

    def test_historical_serial_auxiliary_generation_remains_recognizable(self):
        source = self.generator.WORKFLOW.read_text()
        blocks = m.VALIDATION_AUTHORITY.named_step_blocks(
            m.VALIDATION_AUTHORITY._job_blocks(source)['release']
        )
        current = 'Run independent auxiliary release fanout'
        self.assertIn(current, blocks)
        legacy_names = (
            'Deploy and activate LEGEND Founder Cloudflare baseline',
            'Reconcile public custom-hostname Cloudflare policy',
            'Deploy shared Cloudflare business website router',
        )
        legacy = ''.join(
            f"      - name: {name}\n        run: echo historical-auxiliary-owner\n"
            for name in legacy_names
        )
        historical = source.replace(blocks[current], legacy)
        mutation = m._historical_release_mutation_steps(historical)
        self.assertIsNotNone(mutation)
        self.assertNotIn(current, mutation)
        for name in legacy_names:
            self.assertIn(name, mutation)

    def test_new_inventory_target_generates_its_own_durable_step_and_gate(self):
        text = self.generator.WORKFLOW.read_text()
        targets = {**m.VALIDATION_AUTHORITY.RELEASE_TARGETS,
                   'extra': {'releaseName': 'isolated-extra-app'}}
        rendered = self.generator.render(text, targets)
        self.assertIn('name: Confirm first-pass durable publication receipt (extra)', rendered)
        self.assertNotIn('TARGET_OUTCOME_EXTRA', rendered)
        self.assertIn("contains(fromJSON(env.SELECTED_TARGETS), 'isolated-extra-app')", rendered)

    def test_ephemeral_target_outcomes_cannot_gate_transaction_finalization(self):
        workflow = self.generator.WORKFLOW.read_text()
        transaction = workflow.split(
            '      - name: Reconcile complete immutable release transaction\n', 1
        )[1].split('      - name: Run independent auxiliary release fanout\n', 1)[0]
        self.assertEqual(
            1,
            transaction.count('--finalize-only --transaction-plan /tmp/release-transaction.json'),
        )
        self.assertNotIn('TARGET_OUTCOME_', transaction)
        self.assertNotIn('steps.publish_', transaction)
        self.assertNotIn('--verify-outcomes', transaction)

    def test_workflow_generator_has_no_second_target_outcome_authority(self):
        source = Path(__file__).with_name('release-workflow.py').read_text()
        workflow = self.generator.WORKFLOW.read_text()
        self.assertIn('OUTCOME_START', source)
        self.assertIn('OUTCOME_END', source)
        self.assertNotIn('TARGET_OUTCOME_', source)
        self.assertNotIn('--verify-outcomes', source)
        diagnostics = workflow.split(
            '          # BEGIN GENERATED CANONICAL TARGET OUTCOMES\n', 1
        )[1].split(
            '          # END GENERATED CANONICAL TARGET OUTCOMES\n', 1
        )[0]
        self.assertEqual('', diagnostics)
        self.assertNotIn('TARGET_OUTCOME_', workflow)
        self.assertNotIn('--verify-outcomes', workflow)



class CancelledBeforeAdmissionNonentryProof(unittest.TestCase):
    def setUp(self):
        self.api = Api()
        self.run = {
            'id': 37718975034, 'run_attempt': 1,
            'status': 'completed', 'conclusion': 'cancelled',
            'event': 'workflow_dispatch', 'head_branch': m.APPROVED,
            'head_sha': 'a' * 40, 'path': '.github/workflows/' + m.DIRECT,
            'head_repository': {'full_name': self.api.repo},
        }
        self.run_id = self.run['id']
        self.jobs = [
            {'name': 'admission', 'status': 'completed',
             'conclusion': 'cancelled', 'steps': []},
            {'name': 'discover-live', 'status': 'completed',
             'conclusion': 'skipped', 'steps': []},
            {'name': 'preserve-rollback', 'status': 'completed',
             'conclusion': 'skipped', 'steps': []},
            {'name': 'release', 'status': 'completed',
             'conclusion': 'skipped', 'steps': []},
            {'name': 'release-state-receipt', 'status': 'completed',
             'conclusion': 'success', 'steps': []},
            {'name': 'target-release-receipts (${{ matrix.app }})',
             'status': 'completed', 'conclusion': 'skipped', 'steps': []},
            {'name': 'wake-release-lifecycle-after-terminal-release',
             'status': 'completed', 'conclusion': 'success', 'steps': []},
        ]
        self.artifacts = [{
            'name': f"legend-release-step-state-{self.run['head_sha']}-{self.run_id}-1",
            'expired': False, 'workflow_run': {'id': self.run_id},
        }]
        self.api.pages_map[f"actions/runs/{self.run_id}/attempts/1/jobs"] = self.jobs
        self.api.pages_map[f"actions/runs/{self.run_id}/artifacts"] = self.artifacts
        self.source = (Path(__file__).resolve().parents[1] /
                       '.github/workflows' / m.DIRECT).read_text()

    def proven(self):
        with patch.object(self.api, 'text', return_value=self.source):
            return m._cancelled_before_admission_nonentry(self.api, self.run)

    def test_cancelled_unstarted_admission_with_exact_terminal_receipt(self):
        self.assertTrue(self.proven())

    def test_any_started_admission_step_is_not_no_write_proof(self):
        self.jobs[0]['steps'] = [{'name': 'Admit release', 'conclusion': 'success'}]
        self.assertFalse(self.proven())

    def test_unknown_or_entered_publish_job_blocks_owner_discharge(self):
        self.jobs[3]['conclusion'] = 'success'
        self.assertFalse(self.proven())

    def test_extra_intent_or_missing_receipt_blocks_owner_discharge(self):
        self.artifacts.append({
            'name': 'legend-release-operation-intent-' + 'b' * 64,
            'expired': False, 'workflow_run': {'id': self.run_id}})
        self.assertFalse(self.proven())
        self.artifacts.pop()
        self.artifacts[0]['expired'] = True
        self.assertFalse(self.proven())

    def test_unknown_observer_source_is_not_proven(self):
        self.source = 'unverified release workflow source'
        self.assertFalse(self.proven())


class HistoricalPrepublicationLeaseProof(unittest.TestCase):
    """The two prior generation fingerprints establish *non-entry*, not success."""

    HEAD = 'a0bec5ab2e66a394b6ac64c937333cbe01c49809'
    BLOBS = {
        '.github/workflows/' + m.DIRECT: 'bd84c42297a50b29dfa20c2ed926b8233074720e',
        'scripts/release-prepublication.py': '29bb5e5b5a0c44d4ebc951250a54eb07aee7620b',
        'scripts/release-child-receipt.py': 'b1e262458f8ccac1132f7f71cb434d47b805116a',
        'scripts/release-operation-evidence.py': 'ed61e19c3e6f19c433e9fb489c80cd13b7e084b9',
        'scripts/release-migration.py': '42efc3425a97f9ba8b35ba2a6dde6e41272032b1',
        'scripts/deploy-approved-app.py': '39d5b972bf47d9f29146fe44929e005843ffd234',
        'scripts/validation-resume.py': '34e30ff044dada73593f3662f71fc3f442f869b2',
    }

    def setUp(self):
        self.api = Api()
        self.workflow = (Path(__file__).resolve().parents[1] / '.github/workflows' / m.DIRECT).read_text()
        mutation = m._historical_release_mutation_steps(self.workflow)
        self.assertIn('Synchronize canonical pre-publication resource lanes', mutation)
        self.steps = [
            {'name': name, 'conclusion': (
                'failure' if name == 'Synchronize canonical pre-publication resource lanes'
                else 'success' if name in (
                    'Prepare complete immutable release transaction',
                    'Reconcile complete immutable release transaction',
                ) else 'skipped')}
            for name in sorted(mutation)
        ]
        self.steps.extend([
            {'name': name, 'conclusion': 'skipped'}
            for name in ('Reconcile terminal release resource disposition',
                         'Preserve terminal release resource disposition',
                         'Retain exact approved release receipt')
        ])
        self.run = dict(id=37674186895, run_attempt=1, status='completed', conclusion='failure',
                        head_sha=self.HEAD, path='.github/workflows/' + m.DIRECT,
                        head_branch=m.APPROVED, event='workflow_dispatch',
                        head_repository={'full_name': self.api.repo})
        self.record = dict(
            admissionId='9' * 64,
            applicationRevision='9e1fe088ff640893706f7c469a69417cfa24ee8e',
            producingAttempt=1,
            selectedTargets=[canonical_name(key) for key in ('portal','client','protect','parfait','website')],
            resources=sorted(['write/schema/masterapp'] + [
                'write/app/' + canonical_name(key)
                for key in ('portal','client','protect','parfait','website')
            ]),
        )
        names = [
            'legend-release-admission-' + self.record['admissionId'],
            f"legend-release-step-state-{self.record['applicationRevision']}-{self.run['id']}-1",
            'legend-release-transaction-plan-' + '8' * 64,
        ]
        names.extend(
            f"diagnostics-rollback-{key}-{self.HEAD}"
            for key in ('portal','client','protect','parfait','website')
        )
        self.api.pages_map[f"actions/runs/{self.run['id']}/artifacts"] = [
            {'name': name, 'expired': False} for name in names
        ]
        self.api.pages_map[f"actions/runs/{self.run['id']}/attempts/1/jobs"] = [
            {'name':'release', 'status':'completed','conclusion':'failure','steps': self.steps}
        ]

    def git(self, *args, **_):
        if args[0] == 'rev-parse':
            path = args[1].split(':',1)[1]
            return SimpleNamespace(returncode=0, stdout=self.BLOBS.get(path, ''))
        if args[0] == 'show':
            return SimpleNamespace(returncode=0, stdout=self.workflow)
        return SimpleNamespace(returncode=1, stdout='')

    def proven(self):
        with patch.object(m, 'git', side_effect=self.git):
            return m._historical_fenced_prepublication_nonentry(
                self.api, self.run, self.record
            )

    def test_exact_diagnostic_only_prepublication_generation_is_recognized(self):
        with patch.dict(self.BLOBS, {
            'scripts/release-prepublication.py': 'bd98fb920bfa67eb5e4f7a3ab27f2a46db13e087',
            'scripts/release-migration.py': 'd108377faf267915d86c856523a7992a4a6d500f',
            'scripts/validation-resume.py': '3b78151ca2f2b463d0d553a9967dc0254e577156',
        }):
            self.assertTrue(self.proven())
            self.BLOBS['scripts/release-prepublication.py'] = 'f' * 40
            self.assertFalse(self.proven())

    def test_audited_20261008_readonly_schema_reporting_generation(self):
        # These immutable Git objects differ from previously attested versions
        # only by bounded redacted schema-history observation, never provider writes.
        with patch.dict(self.BLOBS, {
            'scripts/release-prepublication.py': '2f60d22e05e2917a9c48db0db1ba58632ab57d02',
            'scripts/release-migration.py': '819fa223f62e6b97fbbdd28092f765b1f57e6f90',
            'scripts/validation-resume.py': '3b78151ca2f2b463d0d553a9967dc0254e577156',
        }):
            self.assertTrue(self.proven())
            for path in ('scripts/release-prepublication.py',
                         'scripts/release-migration.py'):
                actual = self.BLOBS[path]
                self.BLOBS[path] = 'f' * 40
                self.assertFalse(self.proven())
                self.BLOBS[path] = actual

    def test_exact_historical_no_write_failure_disposes_old_lease(self):
        self.assertTrue(self.proven())
        self.api.pages_map[f"actions/runs/{self.run['id']}/artifacts"] = [
            row for row in self.api.pages_map[f"actions/runs/{self.run['id']}/artifacts"]
            if not row['name'].startswith('legend-release-transaction-plan-')
        ]
        self.assertTrue(self.proven())  # Reused immutable plan is valid history.

    def test_any_durable_intent_blocks_discharge(self):
        artifacts = self.api.pages_map[f"actions/runs/{self.run['id']}/artifacts"]
        for prefix in ('legend-release-child-intent-', 'legend-release-operation-intent-'):
            with self.subTest(prefix=prefix):
                artifacts.append({'name': prefix + 'f' * 64, 'expired': False})
                self.assertFalse(self.proven())
                artifacts.pop()

    def test_cloudflare_routing_or_founder_scope_never_uses_app_nonentry_proof(self):
        original = list(self.record['resources'])
        for resource in ('write/cloudflare/router', 'write/cloudflare/founder'):
            with self.subTest(resource=resource):
                self.record['resources'] = original + [resource]
                self.assertFalse(self.proven())
        self.record['resources'] = original

    def test_incomplete_artifacts_or_untrusted_history_blocks_discharge(self):
        artifacts = self.api.pages_map[f"actions/runs/{self.run['id']}/artifacts"]
        missing = artifacts.pop()
        self.assertFalse(self.proven())
        artifacts.append(missing)
        with patch.dict(self.BLOBS, {'scripts/release-migration.py': 'a' * 40}):
            self.assertFalse(self.proven())
        self.run['head_repository'] = {'full_name': 'attacker/repo'}
        self.assertFalse(self.proven())

    def test_publication_entry_or_missing_step_blocks_discharge(self):
        self.steps.append({'name': 'Submit canonical selected targets in parallel', 'conclusion': 'success'})
        self.assertFalse(self.proven())
        self.steps.pop()
        self.steps[:] = [step for step in self.steps if step['name'] != 'Synchronize canonical pre-publication resource lanes']
        self.assertFalse(self.proven())

    def test_latest_proven_nonpublishing_release_generation_preserves_history(self):
        # Exact content from run 37695940420: no app upload, configuration
        # intent, migration intent, or Cloudflare/router lease is asserted.
        source = 'f2b18bf3b1a236fa172616dae0b49abecfcf5730'
        for row in self.api.pages_map[f"actions/runs/{self.run['id']}/artifacts"]:
            if row['name'].startswith('diagnostics-rollback-'):
                row['name'] = row['name'].replace(self.HEAD, source)
        self.run['head_sha'] = source
        self.BLOBS.update({
            'scripts/release-migration.py': 'd108377faf267915d86c856523a7992a4a6d500f',
            'scripts/validation-resume.py': '3b78151ca2f2b463d0d553a9967dc0254e577156',
        })
        self.assertTrue(self.proven())
        self.record['resources'].append('write/cloudflare/router')
        self.assertFalse(self.proven())

    def test_two_failed_attempts_discharge_only_if_each_proves_no_write(self):
        self.run['head_sha'] = '261fd32ba559d6f2bab0324b538068ee7d7541d7'
        self.run['run_attempt'] = 2
        self.BLOBS.update({
            'scripts/release-migration.py': '4d04187b13f1212c709237d4632c509e5c9696b9',
            'scripts/validation-resume.py': 'db82785acd1dcf8c2f84a43ec22d8c5116959199',
        })
        path = f"actions/runs/{self.run['id']}/artifacts"
        for row in self.api.pages_map[path]:
            if row['name'].startswith('diagnostics-rollback-'):
                row['name'] = row['name'].replace(self.HEAD, self.run['head_sha'])
        self.api.pages_map[path].append({
            'name': f"legend-release-step-state-{self.record['applicationRevision']}-{self.run['id']}-2",
            'expired': False,
        })
        prior = [
            dict(step, conclusion=(
                'failure' if step['name'] == 'Prepare complete immutable release transaction'
                else 'skipped'))
            for step in self.steps
        ]
        self.api.pages_map[f"actions/runs/{self.run['id']}/attempts/1/jobs"] = [{
            'name':'release', 'status':'completed', 'conclusion':'failure', 'steps': prior
        }]
        self.api.pages_map[f"actions/runs/{self.run['id']}/attempts/2/jobs"] = [{
            'name':'release', 'status':'completed', 'conclusion':'failure', 'steps': self.steps
        }]
        self.assertTrue(self.proven())

        # A single entered mutation step in any attempt restores the lease.
        prior[0]['conclusion'] = 'success'
        self.assertFalse(self.proven())
        prior[0]['conclusion'] = 'failure'

        # Missing the earlier immutable attempt receipt also retains ownership.
        self.api.pages_map[path] = [
            row for row in self.api.pages_map[path]
            if not row['name'].endswith(f"-{self.run['id']}-1")
        ]
        self.assertFalse(self.proven())

    def test_multiple_attempts_and_schema_write_intent_remain_blocked(self):
        self.run['run_attempt'] = 2
        self.assertFalse(self.proven())
        self.run['run_attempt'] = 1
        self.record['producingAttempt'] = 2
        self.assertFalse(self.proven())


class ResourceAdmission(unittest.TestCase):
    def resources(self, paths):
        authority = m.VALIDATION_AUTHORITY
        return authority.release_admission_resources(paths, list(authority.release_targets_for_paths(paths)))

    def setUp(self):
        self.api = Api()
        self.run = {'id': 98, 'head_sha': 'b' * 40, 'run_attempt': 1, 'status': 'in_progress', 'conclusion': None,
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

    def test_historical_admission_scope_accepts_schema_write_as_stronger_schema_ownership(self):
        paths = ['ClientApp/Program.cs', 'Infrastructure/Migrations/20261007134500_AddFounderAssistantRules.cs']
        record = {
            'selectedTargets': list(m.VALIDATION_AUTHORITY.release_targets_for_paths(paths)),
            'resources': self.resources(paths),
        }
        self.assertIn('write/schema/masterapp', record['resources'])
        self.assertNotIn('read/schema/masterapp', record['resources'])
        self.assertEqual(
            m.VALIDATION_AUTHORITY.selected_release_target_keys(record['selectedTargets']),
            m._validate_admission_record_scope(record),
        )

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

    def operation_success_receipts(self, revision, *keys):
        artifacts = []
        records = {}
        for index, key in enumerate(keys, 1):
            digest = f"{index:064x}"
            operation_id = hashlib.sha256(json.dumps(
                {'target': key, 'applicationRevision': revision, 'packageDigest': digest},
                sort_keys=True, separators=(',', ':'),
            ).encode()).hexdigest()
            name = 'legend-release-operation-success-' + operation_id
            artifacts.append({'name': name, 'expired': False})
            records[name] = {
                'schemaVersion': 1,
                'phase': 'success',
                'operationId': operation_id,
                'target': key,
                'applicationRevision': revision,
                'packageDigest': digest,
                'producingRun': 98,
                'producingAttempt': 1,
            }
        self.api.pages_map['actions/runs/98/artifacts'] = artifacts

        def download(_repo, run_id, name, directory):
            self.assertEqual(98, run_id)
            (directory / 'operation.json').write_text(json.dumps(records[name]))
        return patch.object(
            m.VALIDATION_AUTHORITY,
            '_download_run_artifact',
            side_effect=download,
        )

    def test_completed_failed_app_release_is_discharged_when_durable_success_and_live_provenance_cover_it(self):
        self.run.update(status='completed', conclusion='failure')
        self.prior.update(
            applicationRevision='c' * 40,
            selectedTargets=[canonical_name('client')],
            resources=self.candidate['resources'],
        )
        live = [{'app': 'client', 'revision': 'd' * 40}]
        with self.operation_success_receipts('c' * 40, 'client'), \
             patch.object(m, '_admission_records', return_value=[self.prior]), \
             patch.object(m, '_admission_settled', return_value=False), \
             patch.object(m, '_admission_nonmutating_terminal', return_value=False), \
             patch.object(m, '_admission_superseded_by_terminal_success', return_value=False), \
             patch.object(m, '_forward_supersedes_completed_app_lease', return_value=False), \
             patch.object(m, 'live_revisions', return_value=live) as observed, \
             patch.object(m, 'ancestor', return_value=True) as lineage:
            self.assertEqual([], m.admission_conflicts(self.api, self.candidate, current_run=99))
        observed.assert_called_once_with()
        lineage.assert_called_with('c' * 40, 'd' * 40)

    def test_live_provenance_cannot_discharge_without_durable_operation_success(self):
        self.run.update(status='completed', conclusion='failure')
        self.prior.update(
            applicationRevision='c' * 40,
            selectedTargets=[canonical_name('client')],
            resources=self.candidate['resources'],
        )
        self.api.pages_map['actions/runs/98/artifacts'] = []
        with patch.object(m, '_admission_records', return_value=[self.prior]), \
             patch.object(m, '_admission_settled', return_value=False), \
             patch.object(m, '_admission_nonmutating_terminal', return_value=False), \
             patch.object(m, '_admission_superseded_by_terminal_success', return_value=False), \
             patch.object(m, '_forward_supersedes_completed_app_lease', return_value=False), \
             patch.object(m, 'live_revisions', side_effect=AssertionError('live proof must not run')) as observed:
            blocked = m.admission_conflicts(self.api, self.candidate, current_run=99)
        self.assertEqual(98, blocked[0]['runId'])
        observed.assert_not_called()

    def test_live_provenance_failure_or_non_descendant_remains_blocking(self):
        self.run.update(status='completed', conclusion='failure')
        self.prior.update(
            applicationRevision='c' * 40,
            selectedTargets=[canonical_name('client')],
            resources=self.candidate['resources'],
        )
        common = (
            patch.object(m, '_admission_records', return_value=[self.prior]),
            patch.object(m, '_admission_settled', return_value=False),
            patch.object(m, '_admission_nonmutating_terminal', return_value=False),
            patch.object(m, '_admission_superseded_by_terminal_success', return_value=False),
            patch.object(m, '_forward_supersedes_completed_app_lease', return_value=False),
        )
        for ctx in common:
            ctx.start()
        receipt = self.operation_success_receipts('c' * 40, 'client')
        receipt.start()
        try:
            with patch.object(m, 'live_revisions', side_effect=RuntimeError('unreachable')):
                self.assertTrue(m.admission_conflicts(self.api, self.candidate, current_run=99))
            with patch.object(m, 'live_revisions', return_value=[{'app': 'client', 'revision': 'd' * 40}]), \
                 patch.object(m, 'ancestor', return_value=False):
                self.assertTrue(m.admission_conflicts(self.api, self.candidate, current_run=99))
        finally:
            receipt.stop()
            for ctx in reversed(common):
                ctx.stop()

    def test_live_app_provenance_cannot_settle_auxiliary_resource_lease(self):
        self.run.update(status='completed', conclusion='failure')
        self.prior.update(
            applicationRevision='c' * 40,
            selectedTargets=[canonical_name('client')],
            resources=self.candidate['resources'] + ['write/cloudflare/router'],
        )
        with patch.object(m, '_admission_records', return_value=[self.prior]), \
             patch.object(m, '_admission_settled', return_value=False), \
             patch.object(m, '_admission_nonmutating_terminal', return_value=False), \
             patch.object(m, '_admission_superseded_by_terminal_success', return_value=False), \
             patch.object(m, '_forward_supersedes_completed_app_lease', return_value=False), \
             patch.object(m, 'live_revisions', side_effect=AssertionError('auxiliary lease cannot use app provenance')) as observed:
            blocked = m.admission_conflicts(self.api, self.candidate, current_run=99)
        self.assertEqual(98, blocked[0]['runId'])
        observed.assert_not_called()

    def test_newer_overlapping_target_descendant_supersedes_completed_stale_app_lease(self):
        self.run.update(status='completed', conclusion='failure')
        self.prior.update(
            applicationRevision='c' * 40,
            selectedTargets=[canonical_name('client')],
            resources=self.candidate['resources'],
        )
        self.candidate['applicationRevision'] = 'd' * 40
        with patch.object(m, '_admission_records', return_value=[self.prior]), \
             patch.object(m, '_admission_settled', return_value=False), \
             patch.object(m, '_admission_nonmutating_terminal', return_value=False), \
             patch.object(m, '_admission_superseded_by_terminal_success', return_value=False), \
             patch.object(m, 'ancestor', return_value=True) as lineage, \
             patch.object(m, 'live_revisions', side_effect=AssertionError('roll-forward needs no live shortcut')) as observed:
            self.assertEqual([], m.admission_conflicts(self.api, self.candidate, current_run=99))
        lineage.assert_called_with('c' * 40, 'd' * 40)
        observed.assert_not_called()

    def test_descendant_schema_writer_rolls_forward_completed_app_lease_without_stale_read_lock(self):
        self.run.update(status='completed', conclusion='failure')
        self.prior.update(
            applicationRevision='c' * 40,
            selectedTargets=[canonical_name('client')],
            resources=self.resources(['ClientApp/Program.cs']),
        )
        paths = ['ClientApp/Program.cs', 'Infrastructure/Migrations/20261007134500_AddFounderAssistantRules.cs']
        self.candidate.update(
            applicationRevision='d' * 40,
            selectedTargets=list(m.VALIDATION_AUTHORITY.release_targets_for_paths(paths)),
            resources=self.resources(paths),
        )
        self.assertIn('read/schema/masterapp', self.prior['resources'])
        self.assertIn('write/schema/masterapp', self.candidate['resources'])
        with patch.object(m, '_admission_records', return_value=[self.prior]), \
             patch.object(m, '_admission_settled', return_value=False), \
             patch.object(m, '_admission_nonmutating_terminal', return_value=False), \
             patch.object(m, '_admission_superseded_by_terminal_success', return_value=False), \
             patch.object(m, 'ancestor', return_value=True) as lineage, \
             patch.object(m, 'live_revisions', side_effect=AssertionError('roll-forward needs no live shortcut')) as observed:
            self.assertEqual([], m.admission_conflicts(self.api, self.candidate, current_run=99))
        lineage.assert_called_with('c' * 40, 'd' * 40)
        observed.assert_not_called()

    def test_descendant_target_slice_rolls_forward_without_claiming_unrelated_stale_apps(self):
        self.run.update(status='completed', conclusion='failure')
        all_paths = [
            'AgentPortal/Program.cs',
            'ClientApp/Program.cs',
            'Protect-Website/Program.cs',
            'ParfaitApp/Program.cs',
            'Legend-Website/package.json',
        ]
        old = dict(
            self.prior,
            applicationRevision='c' * 40,
            selectedTargets=[
                canonical_name('portal'),
                canonical_name('client'),
                canonical_name('protect'),
                canonical_name('parfait'),
                canonical_name('website'),
            ],
            resources=self.resources(all_paths),
        )
        portal_candidate = {
            'applicationRevision': 'd' * 40,
            'selectedTargets': [canonical_name('portal')],
            'resources': self.resources(['AgentPortal/Program.cs']),
        }
        with patch.object(m, '_admission_records', return_value=[old]), \
             patch.object(m, '_admission_settled', return_value=False), \
             patch.object(m, '_admission_nonmutating_terminal', return_value=False), \
             patch.object(m, '_admission_superseded_by_terminal_success', return_value=False), \
             patch.object(m, 'ancestor', return_value=True) as lineage, \
             patch.object(m, 'live_revisions', side_effect=AssertionError('target roll-forward needs no global live shortcut')) as observed:
            self.assertEqual([], m.admission_conflicts(self.api, portal_candidate, current_run=99))
        lineage.assert_called_with('c' * 40, 'd' * 40)
        observed.assert_not_called()

    def test_forward_supersession_rejects_divergence_and_auxiliary_writes(self):
        self.run.update(status='completed', conclusion='failure')
        base_prior = dict(
            self.prior,
            applicationRevision='c' * 40,
            selectedTargets=[canonical_name('client')],
            resources=self.candidate['resources'],
        )
        self.candidate['applicationRevision'] = 'd' * 40
        with patch.object(m, '_admission_records', return_value=[base_prior]), \
             patch.object(m, '_admission_settled', return_value=False), \
             patch.object(m, '_admission_nonmutating_terminal', return_value=False), \
             patch.object(m, '_admission_superseded_by_terminal_success', return_value=False), \
             patch.object(m, 'ancestor', return_value=False), \
             patch.object(m, 'live_revisions', side_effect=RuntimeError('no live settlement')):
            self.assertTrue(m.admission_conflicts(self.api, self.candidate, current_run=99))

        auxiliary = dict(base_prior, resources=base_prior['resources'] + ['write/cloudflare/router'])
        with patch.object(m, '_admission_records', return_value=[auxiliary]), \
             patch.object(m, '_admission_settled', return_value=False), \
             patch.object(m, '_admission_nonmutating_terminal', return_value=False), \
             patch.object(m, '_admission_superseded_by_terminal_success', return_value=False), \
             patch.object(m, 'ancestor', return_value=True), \
             patch.object(m, 'live_revisions', side_effect=RuntimeError('no live settlement')):
            self.assertTrue(m.admission_conflicts(self.api, self.candidate, current_run=99))

    def test_later_successful_same_validated_pr_discharges_stale_historical_lease(self):
        source = 'a' * 40
        authority_one = '1' * 40
        authority_two = '2' * 40
        self.run.update(
            status='completed',
            conclusion='failure',
            id=98,
            display_title=f'LEGEND release pr=462 candidate={source} authority={authority_one}',
        )
        self.prior.update(
            sourcePr=462,
            authorizedSourceRevision=source,
            resources=self.candidate['resources'],
            selectedTargets=[canonical_name('client')],
        )
        later = {
            'id': 120,
            'status': 'completed',
            'conclusion': 'success',
            'path': '.github/workflows/' + m.DIRECT,
            'head_branch': m.APPROVED,
            'event': 'workflow_dispatch',
            'head_repository': {'full_name': self.api.repo},
            'display_title': f'LEGEND release pr=462 candidate={source} authority={authority_two}',
        }
        self.api.pages_map[DurableCandidateQueue.runs_path] = [self.run, later]

        def successful(_api, run, app=None):
            return run.get('id') == 120 and app in (None, 'client')

        with patch.object(m, '_admission_records', return_value=[self.prior]), \
             patch.object(m, '_admission_settled', return_value=False), \
             patch.object(m, '_admission_nonmutating_terminal', return_value=False), \
             patch.object(m, 'successful_release', side_effect=successful):
            self.assertEqual([], m.admission_conflicts(self.api, self.candidate, current_run=99))

    def test_later_success_of_different_candidate_does_not_discharge_stale_lease(self):
        source = 'a' * 40
        other = 'd' * 40
        authority_one = '1' * 40
        authority_two = '2' * 40
        self.run.update(
            status='completed',
            conclusion='failure',
            id=98,
            display_title=f'LEGEND release pr=462 candidate={source} authority={authority_one}',
        )
        self.prior.update(
            sourcePr=462,
            authorizedSourceRevision=source,
            resources=self.candidate['resources'],
            selectedTargets=[canonical_name('client')],
        )
        later = {
            'id': 120,
            'status': 'completed',
            'conclusion': 'success',
            'path': '.github/workflows/' + m.DIRECT,
            'head_branch': m.APPROVED,
            'event': 'workflow_dispatch',
            'head_repository': {'full_name': self.api.repo},
            'display_title': f'LEGEND release pr=462 candidate={other} authority={authority_two}',
        }
        self.api.pages_map[DurableCandidateQueue.runs_path] = [self.run, later]

        def successful(_api, run, app=None):
            return run.get('id') == 120 and app in (None, 'client')

        with patch.object(m, '_admission_records', return_value=[self.prior]), \
             patch.object(m, '_admission_settled', return_value=False), \
             patch.object(m, '_admission_nonmutating_terminal', return_value=False), \
             patch.object(m, 'successful_release', side_effect=successful):
            blocked = m.admission_conflicts(self.api, self.candidate, current_run=99)
        self.assertEqual([98], [row['runId'] for row in blocked])

    def test_terminal_success_uses_durable_admission_scope_not_stale_request(self):
        source = 'a' * 40
        self.run.update(
            status='completed',
            conclusion='failure',
            id=98,
            display_title='LEGEND release pr=462 candidate=' + source + ' authority=' + '1' * 40,
        )
        self.prior.update(
            sourcePr=462,
            authorizedSourceRevision=source,
            applicationRevision='b' * 40,
            selectedTargets=self.candidate['selectedTargets'],
            resources=self.candidate['resources'],
        )
        later = {
            'id': 120,
            'status': 'completed',
            'conclusion': 'success',
            'path': '.github/workflows/' + m.DIRECT,
            'head_branch': m.APPROVED,
            'event': 'workflow_dispatch',
            'head_repository': {'full_name': self.api.repo},
            'display_title': 'LEGEND release pr=462 candidate=' + source + ' authority=' + '2' * 40,
        }
        later_record = dict(self.prior, admissionId='f' * 64)
        self.api.pages_map[DurableCandidateQueue.runs_path] = [self.run, later]

        def records(_api, run):
            return [later_record] if run['id'] == 120 else [self.prior]

        with patch.object(m, '_admission_records', side_effect=records), \
             patch.object(m, '_admission_settled', return_value=False), \
             patch.object(m, '_admission_nonmutating_terminal', return_value=False), \
             patch.object(m, 'successful_release', side_effect=lambda _api, run, app=None: run['id'] == 120 and app is None), \
             patch.object(m, 'release_targets', side_effect=AssertionError('automatic scope must come from durable admission evidence')):
            self.assertEqual([], m.admission_conflicts(self.api, self.candidate, current_run=99))

    def test_same_immutable_transaction_can_continue_without_releasing_other_resources(self):
        self.run.update(status='completed', conclusion='failure')
        self.prior.update(self.candidate)
        with patch.object(m, '_admission_records', return_value=[self.prior]), \
             patch.object(m, '_admission_settled', return_value=False):
            self.assertEqual([], m.admission_conflicts(self.api, self.candidate, current_run=99))

    def test_admission_false_gate_with_omitted_downstream_jobs_is_nonentry(self):
        self.run.update(status='completed', conclusion='failure')
        self.api.pages_map['actions/runs/98/attempts/1/jobs'] = [
            {'name': 'admission', 'conclusion': 'success'},
            {'name': 'preserve-rollback', 'conclusion': 'skipped'},
        ]
        self.assertTrue(m._never_admitted(self.api, self.run))

    def test_omitted_attempt_requires_historical_source_and_positive_attempt_inventory(self):
        self.api.pages_map['actions/runs/98/attempts/1/jobs'] = [
            {'name': 'admission', 'conclusion': 'success'},
            {'name': 'preserve-rollback', 'conclusion': 'skipped'},
        ]
        with patch.object(self.api, 'text', return_value='unknown historical source'):
            self.assertFalse(m._never_admitted(self.api, self.run))
        for count in [0, -1, None, True]:
            with self.subTest(count=count):
                self.assertFalse(m._never_admitted(self.api, {**self.run, 'run_attempt': count}))
        with patch.object(self.api, 'pages', side_effect=RuntimeError('incomplete pagination')):
            with self.assertRaisesRegex(RuntimeError, 'incomplete'):
                m._never_admitted(self.api, self.run)

    def test_omitted_downstream_jobs_with_unexpected_wrapper_remain_blocking(self):
        self.run.update(status='completed', conclusion='failure')
        for jobs in (
            [{'name': 'admission', 'conclusion': 'success'}],
            [
                {'name': 'admission', 'conclusion': 'success'},
                {'name': 'preserve-rollback', 'conclusion': 'success'},
            ],
            [
                {'name': 'admission', 'conclusion': 'success'},
                {'name': 'unexpected-release-wrapper', 'conclusion': 'skipped'},
            ],
        ):
            with self.subTest(jobs=jobs):
                self.api.pages_map['actions/runs/98/attempts/1/jobs'] = jobs
                self.assertFalse(m._never_admitted(self.api, self.run))

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
        unsafe_steps = [dict(step, conclusion='success') if step['name'] == 'Submit canonical selected targets in parallel'
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

    @patch.object(m.time, "sleep")
    def test_broad_product_change_requires_architecture_step5_and_security(self, _sleep):
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

    def test_validation_readiness_retries_observation_only_until_green(self):
        pr, files = self.pr(["AgentPortal/Program.cs"])
        api = Api()
        api.pages_map["pulls/7/files"] = [{"filename": path} for path in files]
        required = (
            ".github/workflows/masterapp-platform-architecture-validation.yml",
            ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
            ".github/workflows/approved-release-security-validation.yml",
        )
        green = [
            {
                "id": index,
                "head_sha": "b" * 40,
                "event": "pull_request",
                "created_at": str(index),
                "path": path,
                "status": "completed",
                "conclusion": "success",
            }
            for index, path in enumerate(required, 1)
        ]
        original_pages = api.pages
        observations = iter([[], [], green])

        def pages(path, key=None):
            if path == "actions/runs?head_sha=" + "b" * 40:
                return next(observations)
            return original_pages(path, key)

        with patch.object(api, "pages", side_effect=pages) as lookup, \
             patch.dict(m.os.environ, {"GITHUB_ACTIONS": "true"}), \
             patch.object(m.time, "sleep") as sleeper:
            self.assertIsNone(m.candidate_validation(api, pr))
        self.assertEqual(4, lookup.call_count)  # one file inventory + three run observations
        self.assertEqual([5, 10], [row.args[0] for row in sleeper.call_args_list])

    def test_validation_readiness_never_retries_completed_failure(self):
        pr, files = self.pr(["AgentPortal/Program.cs"])
        api = Api()
        api.pages_map["pulls/7/files"] = [{"filename": path} for path in files]
        api.pages_map["actions/runs?head_sha=" + "b" * 40] = [{
            "id": 1,
            "head_sha": "b" * 40,
            "event": "pull_request",
            "created_at": "1",
            "path": ".github/workflows/masterapp-platform-architecture-validation.yml",
            "status": "completed",
            "conclusion": "failure",
        }]
        with patch.dict(m.os.environ, {"GITHUB_ACTIONS": "true"}), \
             patch.object(m.time, "sleep") as sleeper:
            pending = m.candidate_validation(api, pr)
        self.assertIn("architecture", pending)
        sleeper.assert_not_called()

    @patch.object(m.time, "sleep")
    def test_security_authority_change_requires_security_validator(self, _sleep):
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
