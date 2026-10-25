#!/usr/bin/env python3
"""Dormant canonical adapter for the accepted replacement recovery primitive.

This module is source-only. It is intentionally not imported by the release
workflow or deploy publisher. A future reviewed integration may call it only
inside the existing LEGEND fast-track workflow, whose run-level concurrency
group is the canonical publication fence.

It reuses:
- GitHub Actions artifacts as the durable journal/evidence store;
- release-operation-evidence.py for create/readback and append/readback;
- validation-resume.py for authenticated Actions reads;
- the existing legend-approved-release workflow concurrency fence.

It does NOT manufacture missing live evidence. In particular, evidence digests
are compared only after the referenced artifact has been authenticated.
"""
from __future__ import annotations

import hashlib
import importlib.util
import json
import os
from dataclasses import dataclass
from pathlib import Path
import re
import sys
from typing import Callable, Mapping, Optional

ROOT = Path(__file__).resolve().parent


def _load(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / filename)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


PRIMITIVE = _load("replacement_recovery_primitive", "recovery-replacement-admission.py")
EVIDENCE = _load("replacement_recovery_evidence", "release-operation-evidence.py")
AUTHORITY = _load("replacement_recovery_authority", "validation-resume.py")

_SHA40 = re.compile(r"^[a-f0-9]{40}$")
_SHA64 = re.compile(r"^[a-f0-9]{64}$")
_FAST_TRACK_PATH = ".github/workflows/" + AUTHORITY.FAST_TRACK_RELEASE_WORKFLOW
_MUTEX_FRAGMENT = """concurrency:
  group: legend-approved-release
  cancel-in-progress: false"""


class RecoveryAdapterError(RuntimeError):
    pass


class MissingProductionEvidence(RecoveryAdapterError):
    pass


def _canonical_digest(value: Mapping) -> str:
    return hashlib.sha256(json.dumps(
        value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def _artifact_name(phase: str, operation_id: str) -> str:
    if phase not in {
        "intent", "submitted", "ambiguous", "success", "failure",
        "resolution-success", "resolution-failure",
    } or not _SHA64.fullmatch(operation_id):
        raise RecoveryAdapterError("Invalid replacement operation phase or identity")
    return f"legend-replacement-operation-{phase}-{operation_id}"


@dataclass(frozen=True)
class EvidenceRef:
    run_id: int
    artifact_id: int
    artifact_name: str
    artifact_digest: str
    filename: str = "operation.json"

    @classmethod
    def from_mapping(cls, value: Mapping):
        if (not isinstance(value, Mapping) or set(value) not in (
                {"runId", "artifactId", "artifactName", "artifactDigest"},
                {"runId", "artifactId", "artifactName", "artifactDigest", "filename"})):
            raise RecoveryAdapterError("Malformed authenticated evidence reference")
        ref = cls(
            run_id=value["runId"], artifact_id=value["artifactId"],
            artifact_name=value["artifactName"], artifact_digest=value["artifactDigest"],
            filename=value.get("filename", "operation.json"))
        if (type(ref.run_id) is not int or ref.run_id < 1 or
                type(ref.artifact_id) is not int or ref.artifact_id < 1 or
                not isinstance(ref.artifact_name, str) or not ref.artifact_name or
                not isinstance(ref.artifact_digest, str) or
                not re.fullmatch(r"sha256:[a-f0-9]{64}", ref.artifact_digest) or
                ref.filename not in {"operation.json", "migration-observation.json"}):
            raise RecoveryAdapterError("Invalid authenticated evidence reference")
        return ref


class CanonicalActionsReader:
    """Authenticate exact immutable evidence from the existing Actions store."""

    def __init__(self, *, authority=AUTHORITY, repository=None, token=None):
        self.authority = authority
        self.repository = repository or os.environ["GITHUB_REPOSITORY"]
        self.token = token or os.environ.get("GH_TOKEN") or os.environ["GITHUB_TOKEN"]

    def read(self, ref: EvidenceRef, *, allowed_workflows) -> dict:
        payload = self.authority.api_get(
            self.repository, f"actions/runs/{ref.run_id}/artifacts?per_page=100", self.token)
        artifacts = payload.get("artifacts")
        if (not isinstance(artifacts, list) or payload.get("total_count") != len(artifacts)):
            raise RecoveryAdapterError("Incomplete authenticated evidence inventory")
        matches = [row for row in artifacts if row.get("id") == ref.artifact_id]
        if len(matches) != 1:
            raise RecoveryAdapterError("Authenticated evidence artifact identity is unavailable")
        artifact = matches[0]
        if (artifact.get("name") != ref.artifact_name or artifact.get("digest") != ref.artifact_digest
                or artifact.get("expired") is not False
                or artifact.get("workflow_run", {}).get("id") != ref.run_id):
            raise RecoveryAdapterError("Authenticated evidence artifact metadata changed")
        run = self.authority.api_get(self.repository, f"actions/runs/{ref.run_id}", self.token)
        if (run.get("path") not in allowed_workflows or
                run.get("head_repository", {}).get("full_name", "").lower() != self.repository.lower()):
            raise RecoveryAdapterError("Evidence producer is outside canonical release authority")
        record = self.authority._release_history_json(
            self.repository, ref.run_id, artifact, ref.filename,
            verify_digest=True, max_bytes=32768)
        if not isinstance(record, dict):
            raise RecoveryAdapterError("Authenticated evidence record is not an object")
        return {"record": record, "run": run, "artifact": artifact}


@dataclass(frozen=True)
class AuthenticatedRecoveryEvidence:
    terminal_nonresumable: dict
    no_sql: dict
    rollback: dict
    independent_review: dict


def authenticate_recovery_evidence(admission: Mapping, refs: Mapping,
                                   *, reader: CanonicalActionsReader) -> AuthenticatedRecoveryEvidence:
    """Authenticate the four records bound by the primitive admission.

    No digest is trusted until its source artifact and producer are authenticated.
    The repository currently has no producer for the terminal/nonresumable record;
    callers therefore cannot construct this bundle until that evidence exists.
    """
    required = {"terminalNonresumable", "noSql", "rollback", "independentReview"}
    if not isinstance(refs, Mapping) or set(refs) != required:
        raise MissingProductionEvidence("Complete authenticated recovery evidence references required")
    fast = {_FAST_TRACK_PATH}
    terminal = reader.read(EvidenceRef.from_mapping(refs["terminalNonresumable"]),
                           allowed_workflows=fast)["record"]
    no_sql = reader.read(EvidenceRef.from_mapping(refs["noSql"]),
                         allowed_workflows=fast | {".github/workflows/" + AUTHORITY.DIRECT_RELEASE_WORKFLOW})["record"]
    rollback = reader.read(EvidenceRef.from_mapping(refs["rollback"]),
                           allowed_workflows=fast)["record"]
    review = reader.read(EvidenceRef.from_mapping(refs["independentReview"]),
                         allowed_workflows=fast)["record"]
    checks = (
        (terminal, admission["terminalNonresumableEvidenceDigest"]),
        (no_sql, admission["noSqlEvidenceDigest"]),
        (rollback, admission["rollbackEvidenceDigest"]),
        (review, admission["independentReviewDigest"]),
    )
    if any(_canonical_digest(record) != expected for record, expected in checks):
        raise RecoveryAdapterError("Authenticated evidence digest does not bind admission")
    baseline_ids = terminal.get("baselineDeploymentIds")
    if (terminal.get("target") != "protect" or
            terminal.get("originalOperationId") != admission["originalOperationId"] or
            terminal.get("terminalNonresumable") is not True or
            not isinstance(baseline_ids, list) or
            any(not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9_.:-]{1,256}", value)
                for value in baseline_ids) or len(baseline_ids) != len(set(baseline_ids))):
        raise RecoveryAdapterError("Prior Protect work is not authenticated terminal/nonresumable evidence")
    if (no_sql.get("pendingCount") != 0 or no_sql.get("mutationActivity") != "settled"
            or no_sql.get("sqlExecutionAuthorized") is not False):
        raise RecoveryAdapterError("Fresh no-SQL evidence is not ready")
    if (rollback.get("target") != "protect" or rollback.get("recoverable") is not True
            or rollback.get("packageDigest") != admission["rollbackPackageDigest"]):
        raise RecoveryAdapterError("Authenticated retained baseline/package recovery evidence is incomplete")
    if (review.get("accepted") is not True or review.get("controlRevision") != admission["controlRevision"]):
        raise RecoveryAdapterError("Independent adapter review evidence does not bind control revision")
    return AuthenticatedRecoveryEvidence(terminal, no_sql, rollback, review)


class CanonicalPublisherFence:
    """Verify the current run is inside the existing run-level publication mutex."""

    def __init__(self, *, authority=AUTHORITY, repository=None, token=None, environment=None):
        self.authority = authority
        self.environment = os.environ if environment is None else environment
        self.repository = repository or self.environment["GITHUB_REPOSITORY"]
        self.token = token or self.environment.get("GH_TOKEN") or self.environment["GITHUB_TOKEN"]
        self.run_id = int(self.environment["GITHUB_RUN_ID"])
        self.attempt = int(self.environment["GITHUB_RUN_ATTEMPT"])
        self.sha = self.environment["GITHUB_SHA"]
        if not _SHA40.fullmatch(self.sha):
            raise RecoveryAdapterError("Canonical fence source revision is invalid")

    def verify(self) -> dict:
        run = self.authority.api_get(self.repository, f"actions/runs/{self.run_id}", self.token)
        if (run.get("path") != _FAST_TRACK_PATH or run.get("head_sha") != self.sha
                or run.get("status") != "in_progress"
                or run.get("head_repository", {}).get("full_name", "").lower() != self.repository.lower()
                or run.get("run_attempt") != self.attempt):
            raise RecoveryAdapterError("Canonical publisher fence run ownership is not active")
        source = self.authority._release_history_source(
            self.repository, self.sha, _FAST_TRACK_PATH, self.token)
        if _MUTEX_FRAGMENT not in source:
            raise RecoveryAdapterError("Canonical publication mutex source contract changed")
        return {"runId": self.run_id, "runAttempt": self.attempt,
                "revision": self.sha, "group": "legend-approved-release"}


class CanonicalReplacementStore:
    """Primitive callbacks backed by the existing immutable Actions artifact store."""

    def __init__(self, *, fence: CanonicalPublisherFence, authority=AUTHORITY,
                 evidence_module=EVIDENCE, repository=None, token=None):
        self.fence = fence
        self.authority = authority
        self.evidence_module = evidence_module
        self.repository = repository or fence.repository
        self.token = token or fence.token

    def lookup(self, phase: str, operation_id: str) -> Optional[dict]:
        name = _artifact_name(phase, operation_id)
        payload = self.authority.api_get(
            self.repository, "actions/artifacts?name=" + name + "&per_page=100", self.token)
        artifacts = payload.get("artifacts")
        if not isinstance(artifacts, list) or payload.get("total_count") != len(artifacts):
            raise RecoveryAdapterError("Replacement evidence inventory is incomplete")
        if len(artifacts) > 1:
            raise RecoveryAdapterError("Competing replacement evidence artifacts require reconciliation")
        if not artifacts:
            return None
        artifact = artifacts[0]
        run_id = artifact.get("workflow_run", {}).get("id")
        if (artifact.get("name") != name or artifact.get("expired") is not False
                or type(run_id) is not int or run_id < 1):
            raise RecoveryAdapterError("Replacement evidence artifact identity is invalid")
        run = self.authority.api_get(self.repository, f"actions/runs/{run_id}", self.token)
        if (run.get("path") != _FAST_TRACK_PATH or
                run.get("head_repository", {}).get("full_name", "").lower() != self.repository.lower()):
            raise RecoveryAdapterError("Replacement evidence producer is not canonical fast-track")
        record = self.authority._release_history_json(
            self.repository, run_id, artifact, "operation.json",
            verify_digest=True, max_bytes=32768)
        if (record.get("phase") != phase or record.get("operationId") != operation_id
                or record.get("producingRun") != run_id):
            raise RecoveryAdapterError("Replacement evidence does not bind its producing run")
        return record

    def claim(self, _primitive_name: str, record: Mapping) -> Mapping:
        self.fence.verify()
        return self.evidence_module.claim_record(
            _artifact_name("intent", record["operationId"]), record)

    def append(self, _primitive_name: str, record: Mapping) -> Mapping:
        self.fence.verify()
        self.evidence_module.publish_record(
            _artifact_name(record["phase"], record["operationId"]), record)
        # publish_record already synchronously validates exact bytes.
        return {"record": dict(record)}


class CanonicalReplacementAdapter:
    """Bind primitive + canonical store + run-level fence without dispatching anything."""

    def __init__(self, *, admission: Mapping, original_intent: Mapping,
                 authenticated_evidence: AuthenticatedRecoveryEvidence,
                 fence: CanonicalPublisherFence, store: CanonicalReplacementStore,
                 package_producer_run: int, producing_run: int, producing_attempt: int,
                 clock=None):
        self.evidence = authenticated_evidence
        self.fence = fence
        self.store = store
        self.provider_call_entered = False
        fence_evidence = self.fence.verify()
        if _canonical_digest(fence_evidence) != admission["serializationFenceEvidenceDigest"]:
            raise RecoveryAdapterError("Active canonical publisher fence does not bind admission")
        self.journal = PRIMITIVE.ReplacementOperationJournal(
            admission=admission, original_intent=original_intent,
            lookup=store.lookup, claim=store.claim, append=store.append,
            package_producer_run=package_producer_run,
            producing_run=producing_run, producing_attempt=producing_attempt,
            clock=clock)

    def call_provider_once(self, provider_call: Callable[[Mapping], str],
                           *, baseline_deployment_ids, ambiguity_evidence_digest: str) -> str:
        """Own exactly one provider call while the canonical run fence is active.

        No production code calls this method today. Tests must use a fake callback.
        """
        if self.provider_call_entered:
            raise RecoveryAdapterError("Provider call ownership already consumed")
        expected_baseline = self.evidence.terminal_nonresumable["baselineDeploymentIds"]
        if list(baseline_deployment_ids) != expected_baseline:
            raise RecoveryAdapterError("Provider-call baseline differs from authenticated terminal evidence")
        self.fence.verify()
        if not self.journal.before_submit(
                expected_baseline,
                submission_kind="azure_onedeploy_windows_intent_v1"):
            raise RecoveryAdapterError("Replacement intent already consumed; reconcile only")
        correlation = self.journal.submission_correlation()
        self.fence.verify()
        self.provider_call_entered = True
        try:
            deployment_id = provider_call(correlation)
        except Exception:
            self.journal.record_ambiguous(
                ambiguity_evidence_digest=ambiguity_evidence_digest)
            raise
        self.fence.verify()
        self.journal.record_submission_observation(deployment_id)
        return deployment_id


def prepare_canonical_adapter(*, admission: Mapping, original_revision: str,
                              current_run: int, current_attempt: int,
                              evidence_refs: Mapping, fence: CanonicalPublisherFence,
                              reader: Optional[CanonicalActionsReader] = None,
                              authority=AUTHORITY, evidence_module=EVIDENCE, clock=None):
    """Authenticate real existing evidence and construct the dormant adapter.

    This does not publish, dispatch, restart, deploy, or call a provider.
    """
    repository = fence.repository
    token = fence.token
    original = authority.release_operation_history(
        repository, admission["originalOperationId"], original_revision, "protect",
        current_run, current_attempt, token)
    if original is None:
        raise MissingProductionEvidence("Original Protect ambiguous intent is unavailable")
    authenticated = authenticate_recovery_evidence(
        admission, evidence_refs, reader=reader or CanonicalActionsReader(
            authority=authority, repository=repository, token=token))
    fence.verify()
    store = CanonicalReplacementStore(
        fence=fence, authority=authority, evidence_module=evidence_module,
        repository=repository, token=token)
    return CanonicalReplacementAdapter(
        admission=admission, original_intent=original,
        authenticated_evidence=authenticated, fence=fence, store=store,
        package_producer_run=original["packageProducerRun"],
        producing_run=current_run, producing_attempt=current_attempt, clock=clock)
