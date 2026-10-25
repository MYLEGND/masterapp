#!/usr/bin/env python3
"""Dormant canonical adapter for the accepted replacement recovery primitive.

Source-only: no release workflow imports this module and no real provider call is
made by its tests. A future production integration must separately authenticate
live evidence and receive independent adapter/execution admission.

This adapter reuses the existing Actions artifact journal, validation authority,
and legend-approved-release concurrency fence. It does not introduce a second
store, lock service, credential, or publication path.
"""
from __future__ import annotations

import datetime as dt
import hashlib
import importlib.util
import json
import os
from dataclasses import dataclass
from pathlib import Path
import re
import sys
from typing import Callable, Mapping, Optional
import urllib.parse

ROOT = Path(__file__).resolve().parent.parent


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
_PROVIDER_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,255}$")
_FAST_TRACK_PATH = ".github/workflows/" + AUTHORITY.FAST_TRACK_RELEASE_WORKFLOW
_EVIDENCE_FRESHNESS = dt.timedelta(minutes=15)
_VERIFIED_FACTORY_TOKEN = object()


class RecoveryAdapterError(RuntimeError):
    pass


class MissingProductionEvidence(RecoveryAdapterError):
    pass


def _canonical_digest(value) -> str:
    return hashlib.sha256(json.dumps(
        value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def _utc(value: object, label: str) -> dt.datetime:
    if not isinstance(value, str):
        raise RecoveryAdapterError(f"{label} timestamp is missing")
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise RecoveryAdapterError(f"{label} timestamp is invalid") from exc
    if parsed.tzinfo is None:
        raise RecoveryAdapterError(f"{label} timestamp must be offset-aware")
    return parsed.astimezone(dt.timezone.utc)


def _artifact_name(phase: str, operation_id: str) -> str:
    if phase not in {
        "intent", "submitted", "ambiguous", "success", "failure",
        "resolution-success", "resolution-failure",
    } or not _SHA64.fullmatch(operation_id):
        raise RecoveryAdapterError("Invalid replacement operation phase or identity")
    return f"legend-replacement-operation-{phase}-{operation_id}"


def _record_provenance(value: Mapping) -> dict:
    return {
        "runId": value["runId"], "runAttempt": value["runAttempt"],
        "jobId": value["jobId"], "stepName": value["stepName"],
        "sourceRevision": value["sourceRevision"],
        "artifactId": value["artifactId"], "artifactDigest": value["artifactDigest"],
    }


@dataclass(frozen=True)
class EvidenceRef:
    run_id: int
    run_attempt: int
    job_id: int
    step_name: str
    source_revision: str
    artifact_id: int
    artifact_name: str
    artifact_digest: str
    filename: str = "operation.json"

    @classmethod
    def from_mapping(cls, value: Mapping):
        expected = {
            "runId", "runAttempt", "jobId", "stepName", "sourceRevision",
            "artifactId", "artifactName", "artifactDigest",
        }
        if not isinstance(value, Mapping) or set(value) not in (expected, expected | {"filename"}):
            raise RecoveryAdapterError("Malformed authenticated evidence reference")
        ref = cls(
            run_id=value["runId"], run_attempt=value["runAttempt"],
            job_id=value["jobId"], step_name=value["stepName"],
            source_revision=value["sourceRevision"], artifact_id=value["artifactId"],
            artifact_name=value["artifactName"], artifact_digest=value["artifactDigest"],
            filename=value.get("filename", "operation.json"))
        if (any(type(x) is not int or x < 1 for x in
                (ref.run_id, ref.run_attempt, ref.job_id, ref.artifact_id)) or
                not isinstance(ref.step_name, str) or not 1 <= len(ref.step_name) <= 128 or
                not _SHA40.fullmatch(ref.source_revision) or
                not isinstance(ref.artifact_name, str) or
                not re.fullmatch(r"[A-Za-z0-9_.:-]{1,256}", ref.artifact_name) or
                not isinstance(ref.artifact_digest, str) or
                not re.fullmatch(r"sha256:[a-f0-9]{64}", ref.artifact_digest) or
                ref.filename not in {"operation.json", "migration-observation.json"}):
            raise RecoveryAdapterError("Invalid authenticated evidence reference")
        return ref

    def provenance(self) -> dict:
        return {
            "runId": self.run_id, "runAttempt": self.run_attempt,
            "jobId": self.job_id, "stepName": self.step_name,
            "sourceRevision": self.source_revision,
            "artifactId": self.artifact_id, "artifactDigest": self.artifact_digest,
        }


class CanonicalActionsReader:
    """Authenticate exact immutable evidence and its producer step."""

    def __init__(self, *, authority=AUTHORITY, repository=None, token=None):
        self.authority = authority
        self.repository = repository or os.environ["GITHUB_REPOSITORY"]
        self.token = token or os.environ.get("GH_TOKEN") or os.environ["GITHUB_TOKEN"]

    def read(self, ref: EvidenceRef, *, allowed_workflows) -> dict:
        run = self.authority.api_get(
            self.repository, f"actions/runs/{ref.run_id}", self.token)
        if (run.get("path") not in allowed_workflows or
                run.get("head_sha") != ref.source_revision or
                run.get("run_attempt") != ref.run_attempt or
                run.get("head_repository", {}).get("full_name", "").lower() != self.repository.lower()):
            raise RecoveryAdapterError("Evidence producer source/run identity changed")
        jobs_payload = self.authority.api_get(
            self.repository,
            f"actions/runs/{ref.run_id}/attempts/{ref.run_attempt}/jobs?per_page=100",
            self.token)
        jobs = jobs_payload.get("jobs")
        if (not isinstance(jobs, list) or jobs_payload.get("total_count") != len(jobs)):
            raise RecoveryAdapterError("Evidence producer job inventory is incomplete")
        jobs = [job for job in jobs if job.get("id") == ref.job_id]
        if len(jobs) != 1 or jobs[0].get("status") != "completed" or jobs[0].get("conclusion") != "success":
            raise RecoveryAdapterError("Evidence producer job is not successful")
        steps = [step for step in jobs[0].get("steps", []) if step.get("name") == ref.step_name]
        if len(steps) != 1 or steps[0].get("status") != "completed" or steps[0].get("conclusion") != "success":
            raise RecoveryAdapterError("Evidence producer step is not successful")

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
        in_step = getattr(self.authority, "_completion_artifact_in_step", None)
        if not callable(in_step) or not in_step(artifact, steps[0]):
            raise RecoveryAdapterError("Evidence artifact is outside its approved producer step")
        record = self.authority._release_history_json(
            self.repository, ref.run_id, artifact, ref.filename,
            verify_digest=True, max_bytes=32768)
        if not isinstance(record, dict):
            raise RecoveryAdapterError("Authenticated evidence record is not an object")
        if (record.get("producingRun") != ref.run_id or
                record.get("producingAttempt") != ref.run_attempt):
            raise RecoveryAdapterError("Authenticated evidence does not bind its producing attempt")
        return {"record": record, "provenance": ref.provenance()}


def _fresh_for_admission(value: object, admission: Mapping, label: str) -> None:
    observed = _utc(value, label)
    admitted = _utc(admission["observedAtUtc"], "admission observed")
    expires = _utc(admission["expiresAtUtc"], "admission expiry")
    if observed > admitted or admitted - observed > _EVIDENCE_FRESHNESS or expires - observed > _EVIDENCE_FRESHNESS:
        raise RecoveryAdapterError(f"{label} evidence is stale for this admission")


def _validate_bundle(admission: Mapping, entries: Mapping) -> str:
    required = {"terminalNonresumable", "noSql", "rollback", "independentReview"}
    if not isinstance(entries, Mapping) or set(entries) != required:
        raise MissingProductionEvidence("Complete authenticated recovery evidence bundle required")
    for key, entry in entries.items():
        if (not isinstance(entry, Mapping) or set(entry) != {"record", "provenance"} or
                not isinstance(entry["record"], Mapping) or not isinstance(entry["provenance"], Mapping)):
            raise RecoveryAdapterError(f"Authenticated {key} bundle entry is malformed")
        provenance = entry["provenance"]
        if (entry["record"].get("producingRun") != provenance.get("runId") or
                entry["record"].get("producingAttempt") != provenance.get("runAttempt")):
            raise RecoveryAdapterError(f"Authenticated {key} record/provenance producer mismatch")
        if (set(provenance) != {
                "runId", "runAttempt", "jobId", "stepName", "sourceRevision",
                "artifactId", "artifactDigest"} or
                any(type(provenance[k]) is not int or provenance[k] < 1
                    for k in ("runId", "runAttempt", "jobId", "artifactId")) or
                not _SHA40.fullmatch(provenance.get("sourceRevision", "")) or
                not re.fullmatch(r"sha256:[a-f0-9]{64}", provenance.get("artifactDigest", ""))):
            raise RecoveryAdapterError(f"Authenticated {key} provenance is malformed")

    terminal = entries["terminalNonresumable"]["record"]
    no_sql = entries["noSql"]["record"]
    rollback = entries["rollback"]["record"]
    review = entries["independentReview"]["record"]
    if _canonical_digest(terminal) != admission["terminalNonresumableEvidenceDigest"]:
        raise RecoveryAdapterError("Terminal/nonresumable evidence does not bind admission")
    if _canonical_digest(no_sql) != admission["noSqlEvidenceDigest"]:
        raise RecoveryAdapterError("No-SQL evidence does not bind admission")
    if _canonical_digest(rollback) != admission["rollbackEvidenceDigest"]:
        raise RecoveryAdapterError("Rollback evidence does not bind admission")
    if _canonical_digest(review) != admission["independentReviewDigest"]:
        raise RecoveryAdapterError("Independent review evidence does not bind admission")

    if (terminal.get("schemaVersion") != 1 or terminal.get("phase") != "terminal-nonresumable" or
            terminal.get("target") != "protect" or
            terminal.get("originalOperationId") != admission["originalOperationId"] or
            terminal.get("candidateRevision") != admission["applicationRevision"] or
            terminal.get("controlRevision") != admission["controlRevision"] or
            terminal.get("terminalNonresumable") is not True or
            not isinstance(terminal.get("baselineDeploymentIds"), list) or
            len(terminal["baselineDeploymentIds"]) != len(set(terminal["baselineDeploymentIds"])) or
            any(not isinstance(value, str) or not _PROVIDER_ID.fullmatch(value)
                for value in terminal["baselineDeploymentIds"])):
        raise RecoveryAdapterError("Prior Protect work is not authenticated terminal/nonresumable evidence")
    _fresh_for_admission(terminal.get("observedAtUtc"), admission, "terminal/nonresumable")

    if (no_sql.get("schemaVersion") != 1 or no_sql.get("phase") != "observation" or
            no_sql.get("kind") != "current-schema-no-write" or
            no_sql.get("candidate") != admission["applicationRevision"] or
            no_sql.get("pendingCount") != 0 or no_sql.get("mutationActivity") != "settled" or
            no_sql.get("sqlExecutionAuthorized") is not False or
            no_sql.get("historicalExecution") not in {"proven-nonentry", "proven-completion"} or
            any(not _SHA64.fullmatch(no_sql.get(key, ""))
                for key in ("databaseIdentity", "schemaIdentity", "baselineIdentity", "bundleDigest"))):
        raise RecoveryAdapterError("Fresh exact-candidate no-SQL inventory is not ready")
    _fresh_for_admission(no_sql.get("observedUtc"), admission, "no-SQL")

    if (rollback.get("schemaVersion") != 1 or rollback.get("phase") != "rollback-readiness" or
            rollback.get("target") != "protect" or
            rollback.get("baselineRevision") != admission["baselineRevision"] or
            rollback.get("packageDigest") != admission["rollbackPackageDigest"] or
            rollback.get("recoverable") is not True or rollback.get("packageAvailable") is not True or
            type(rollback.get("packageArtifactId")) is not int or rollback["packageArtifactId"] < 1 or
            not re.fullmatch(r"sha256:[a-f0-9]{64}", rollback.get("packageArtifactDigest", ""))):
        raise RecoveryAdapterError("Exact retained baseline bytes are not currently authenticated/recoverable")
    _fresh_for_admission(rollback.get("observedAtUtc"), admission, "rollback availability")

    pre_review = {key: entries[key] for key in ("terminalNonresumable", "noSql", "rollback")}
    bundle_digest = _canonical_digest(pre_review)
    if (review.get("schemaVersion") != 1 or review.get("phase") != "independent-review" or
            review.get("accepted") is not True or review.get("target") != "protect" or
            review.get("originalOperationId") != admission["originalOperationId"] or
            review.get("applicationRevision") != admission["applicationRevision"] or
            review.get("packageDigest") != admission["packageDigest"] or
            review.get("controlRevision") != admission["controlRevision"] or
            review.get("evidenceBundleDigest") != bundle_digest or
            not isinstance(review.get("reviewerIdentity"), str) or not review["reviewerIdentity"]):
        raise RecoveryAdapterError("Independent review does not bind the exact adapter/evidence bundle")
    _fresh_for_admission(review.get("observedAtUtc"), admission, "independent review")
    if entries["independentReview"]["provenance"]["sourceRevision"] != admission["controlRevision"]:
        raise RecoveryAdapterError("Independent review producer revision does not bind control revision")
    return _canonical_digest(entries)


@dataclass(frozen=True)
class AuthenticatedRecoveryEvidence:
    terminal_nonresumable: dict
    no_sql: dict
    rollback: dict
    independent_review: dict
    entries: dict
    verification_digest: str
    _factory_token: object

    def __post_init__(self):
        if self._factory_token is not _VERIFIED_FACTORY_TOKEN:
            raise RecoveryAdapterError("Authenticated recovery evidence must come from verified factory")

    def revalidate(self, admission: Mapping) -> None:
        digest = _validate_bundle(admission, self.entries)
        if digest != self.verification_digest:
            raise RecoveryAdapterError("Authenticated recovery evidence bundle changed")
        for record, key in ((self.terminal_nonresumable, "terminalNonresumable"),
                            (self.no_sql, "noSql"), (self.rollback, "rollback"),
                            (self.independent_review, "independentReview")):
            if _canonical_digest(record) != _canonical_digest(self.entries[key]["record"]):
                raise RecoveryAdapterError("Consumed recovery evidence differs from authenticated record")


def authenticate_recovery_evidence(admission: Mapping, refs: Mapping,
                                   *, reader: CanonicalActionsReader) -> AuthenticatedRecoveryEvidence:
    required = {"terminalNonresumable", "noSql", "rollback", "independentReview"}
    if not isinstance(refs, Mapping) or set(refs) != required:
        raise MissingProductionEvidence("Complete authenticated recovery evidence references required")
    fast = {_FAST_TRACK_PATH}
    direct = {".github/workflows/" + AUTHORITY.DIRECT_RELEASE_WORKFLOW}
    entries = {
        "terminalNonresumable": reader.read(
            EvidenceRef.from_mapping(refs["terminalNonresumable"]), allowed_workflows=fast),
        "noSql": reader.read(
            EvidenceRef.from_mapping(refs["noSql"]), allowed_workflows=fast | direct),
        "rollback": reader.read(
            EvidenceRef.from_mapping(refs["rollback"]), allowed_workflows=fast | direct),
        "independentReview": reader.read(
            EvidenceRef.from_mapping(refs["independentReview"]), allowed_workflows=fast),
    }
    verification = _validate_bundle(admission, entries)
    return AuthenticatedRecoveryEvidence(
        terminal_nonresumable=dict(entries["terminalNonresumable"]["record"]),
        no_sql=dict(entries["noSql"]["record"]),
        rollback=dict(entries["rollback"]["record"]),
        independent_review=dict(entries["independentReview"]["record"]),
        entries=json.loads(json.dumps(entries)),
        verification_digest=verification,
        _factory_token=_VERIFIED_FACTORY_TOKEN)


def _canonical_mutex_source(source: str) -> None:
    """Structurally validate the one top-level canonical concurrency block."""
    if not isinstance(source, str):
        raise RecoveryAdapterError("Canonical publication workflow source unavailable")
    lines = source.splitlines()
    indexes = [i for i, line in enumerate(lines) if line == "concurrency:"]
    if len(indexes) != 1:
        raise RecoveryAdapterError("Canonical publication mutex top-level block changed")
    i = indexes[0] + 1
    fields = {}
    while i < len(lines):
        line = lines[i]
        if not line.strip() or line.lstrip().startswith("#"):
            i += 1
            continue
        if not line.startswith("  "):
            break
        match = re.fullmatch(r"  (group|cancel-in-progress):\s*(.+)", line)
        if not match or match.group(1) in fields:
            raise RecoveryAdapterError("Canonical publication mutex structure changed")
        fields[match.group(1)] = match.group(2)
        i += 1
    if fields != {"group": "legend-approved-release", "cancel-in-progress": "false"}:
        raise RecoveryAdapterError("Canonical publication mutex contract changed")


class CanonicalPublisherFence:
    """Authenticate the current run and structural shared publication mutex.

    This fence serializes LEGEND publication runs. It does NOT prove old external
    provider work is terminal/nonresumable; that remains separate evidence.
    """

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
        if (run.get("path") != _FAST_TRACK_PATH or run.get("head_sha") != self.sha or
                run.get("status") != "in_progress" or run.get("run_attempt") != self.attempt or
                run.get("event") not in {"push", "workflow_dispatch"} or
                run.get("head_repository", {}).get("full_name", "").lower() != self.repository.lower()):
            raise RecoveryAdapterError("Canonical publisher fence run ownership is not active")
        source = self.authority._release_history_source(
            self.repository, self.sha, _FAST_TRACK_PATH, self.token)
        _canonical_mutex_source(source)
        return {
            "runId": self.run_id, "runAttempt": self.attempt,
            "revision": self.sha, "sourceDigest": hashlib.sha256(source.encode()).hexdigest(),
            "group": "legend-approved-release",
        }


class CanonicalReplacementStore:
    """Existing Actions store with repository-global consumption proof under the mutex.

    The SDK upload is run-scoped and is NOT treated as a cross-run CAS. Global
    one-use comes from complete repository artifact lookup before and after the
    run-scoped claim while the canonical publication mutex is continuously valid.
    """

    def __init__(self, *, fence: CanonicalPublisherFence, expected_fence_digest: str,
                 authority=AUTHORITY, evidence_module=EVIDENCE, repository=None, token=None):
        self.fence = fence
        self.expected_fence_digest = expected_fence_digest
        self.authority = authority
        self.evidence_module = evidence_module
        self.repository = repository or fence.repository
        self.token = token or fence.token

    def _verify_fence(self) -> dict:
        value = self.fence.verify()
        if _canonical_digest(value) != self.expected_fence_digest:
            raise RecoveryAdapterError("Canonical publisher fence evidence changed")
        return value

    def lookup(self, phase: str, operation_id: str) -> Optional[dict]:
        name = _artifact_name(phase, operation_id)
        payload = self.authority.api_get(
            self.repository,
            "actions/artifacts?name=" + urllib.parse.quote(name, safe="") + "&per_page=100",
            self.token)
        artifacts = payload.get("artifacts")
        if (not isinstance(artifacts, list) or type(payload.get("total_count")) is not int or
                payload["total_count"] != len(artifacts)):
            raise RecoveryAdapterError("Replacement evidence inventory is incomplete")
        if len(artifacts) > 1:
            raise RecoveryAdapterError("Competing replacement evidence artifacts require reconciliation")
        if not artifacts:
            return None
        artifact = artifacts[0]
        run_id = artifact.get("workflow_run", {}).get("id")
        if (artifact.get("name") != name or artifact.get("expired") is not False or
                type(run_id) is not int or run_id < 1 or
                not re.fullmatch(r"sha256:[a-f0-9]{64}", artifact.get("digest", ""))):
            raise RecoveryAdapterError("Replacement evidence artifact identity is invalid")
        run = self.authority.api_get(self.repository, f"actions/runs/{run_id}", self.token)
        if (run.get("path") != _FAST_TRACK_PATH or
                run.get("head_repository", {}).get("full_name", "").lower() != self.repository.lower()):
            raise RecoveryAdapterError("Replacement evidence producer is not canonical fast-track")
        record = self.authority._release_history_json(
            self.repository, run_id, artifact, "operation.json",
            verify_digest=True, max_bytes=32768)
        if (record.get("phase") != phase or record.get("operationId") != operation_id or
                record.get("producingRun") != run_id or
                record.get("recoveryAdmission", {}).get("controlRevision") != run.get("head_sha")):
            raise RecoveryAdapterError("Replacement evidence does not bind its canonical producer")
        return record

    @staticmethod
    def _same(record: Mapping, other: Mapping) -> bool:
        return dict(record) == dict(other)

    def claim(self, _primitive_name: str, record: Mapping) -> Mapping:
        self._verify_fence()
        prior = self.lookup("intent", record["operationId"])
        if prior is not None:
            if not self._same(prior, record):
                raise RecoveryAdapterError("Global replacement intent conflicts with requested claim")
            return {"status": "existing", "record": prior}
        self._verify_fence()
        result = self.evidence_module.claim_record(
            _artifact_name("intent", record["operationId"]), record)
        # A run-scoped created acknowledgment is insufficient by itself.
        self._verify_fence()
        durable = self.lookup("intent", record["operationId"])
        if durable is None or not self._same(durable, record):
            raise RecoveryAdapterError("Global replacement intent ownership is unproven")
        if (not isinstance(result, Mapping) or result.get("status") not in {"created", "existing"} or
                result.get("record") != dict(record)):
            raise RecoveryAdapterError("Run-scoped replacement claim disposition is invalid")
        if result["status"] != "created":
            return {"status": "existing", "record": durable}
        fence = self._verify_fence()
        if (durable.get("producingRun") != fence["runId"] or
                durable.get("producingAttempt") != fence["runAttempt"]):
            raise RecoveryAdapterError("Created replacement intent is not owned by current serialized run")
        return {"status": "created", "record": durable}

    def append(self, _primitive_name: str, record: Mapping) -> Mapping:
        self._verify_fence()
        phase = record["phase"]
        prior = self.lookup(phase, record["operationId"])
        if prior is not None:
            if not self._same(prior, record):
                raise RecoveryAdapterError("Global replacement append conflicts with durable record")
            return {"record": prior}
        self._verify_fence()
        self.evidence_module.publish_record(
            _artifact_name(phase, record["operationId"]), record)
        self._verify_fence()
        durable = self.lookup(phase, record["operationId"])
        if durable is None or not self._same(durable, record):
            raise RecoveryAdapterError("Replacement append durable readback is unproven")
        return {"record": durable}


class CanonicalReplacementAdapter:
    """Bind accepted primitive to authenticated evidence/store/fence without dispatching."""

    def __init__(self, *, admission: Mapping, original_intent: Mapping,
                 authenticated_evidence: AuthenticatedRecoveryEvidence,
                 fence: CanonicalPublisherFence, store: CanonicalReplacementStore,
                 package_producer_run: int, producing_run: int, producing_attempt: int,
                 clock=None):
        authenticated_evidence.revalidate(admission)
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

    def _record_ambiguity_or_hold(self, digest: str, cause: BaseException) -> None:
        try:
            self.journal.record_ambiguous(ambiguity_evidence_digest=digest)
        except Exception as append_error:
            # Durable intent already prevents replay. Never claim ambiguity saved.
            raise RecoveryAdapterError(
                "Provider outcome requires reconciliation; ambiguity append was not durably proven"
            ) from append_error
        raise cause

    def call_provider_once(self, provider_call: Callable[[Mapping], str],
                           *, baseline_deployment_ids, ambiguity_evidence_digest: str) -> str:
        """Own at most one fake/future provider callback under the shared fence.

        No production caller exists today. The provider callback is set as entered
        immediately before invocation; every later failure is reconcile-only.
        """
        if self.provider_call_entered:
            raise RecoveryAdapterError("Provider call ownership already consumed")
        expected_baseline = list(self.evidence.terminal_nonresumable["baselineDeploymentIds"])
        if list(baseline_deployment_ids) != expected_baseline:
            raise RecoveryAdapterError("Provider-call baseline differs from authenticated terminal evidence")
        self.fence.verify()  # before claim
        self.evidence.revalidate(self.journal.admission)
        if expected_baseline != self.evidence.entries["terminalNonresumable"]["record"]["baselineDeploymentIds"]:
            raise RecoveryAdapterError("Consumed baseline differs from authenticated terminal evidence")
        if not self.journal.before_submit(
                expected_baseline,
                submission_kind="azure_onedeploy_windows_intent_v1"):
            raise RecoveryAdapterError("Replacement intent already consumed; reconcile only")
        correlation = self.journal.submission_correlation()
        self.fence.verify()  # immediately before provider I/O
        self.evidence.revalidate(self.journal.admission)
        if expected_baseline != self.evidence.entries["terminalNonresumable"]["record"]["baselineDeploymentIds"]:
            raise RecoveryAdapterError("Consumed baseline differs from authenticated terminal evidence")
        self.provider_call_entered = True
        try:
            deployment_id = provider_call(correlation)
        except Exception as provider_error:
            self._record_ambiguity_or_hold(ambiguity_evidence_digest, provider_error)
        try:
            self.fence.verify()  # detects fence loss during/after provider I/O
        except Exception as fence_error:
            self._record_ambiguity_or_hold(ambiguity_evidence_digest, fence_error)
        self.journal.record_submission_observation(deployment_id)
        return deployment_id


def prepare_canonical_adapter(*, admission: Mapping, original_revision: str,
                              current_run: int, current_attempt: int,
                              evidence_refs: Mapping, fence: CanonicalPublisherFence,
                              reader: Optional[CanonicalActionsReader] = None,
                              authority=AUTHORITY, evidence_module=EVIDENCE, clock=None):
    """Authenticate existing authority and construct the dormant adapter only."""
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
    fence_evidence = fence.verify()
    if _canonical_digest(fence_evidence) != admission["serializationFenceEvidenceDigest"]:
        raise RecoveryAdapterError("Current canonical fence does not bind admission")
    store = CanonicalReplacementStore(
        fence=fence, expected_fence_digest=admission["serializationFenceEvidenceDigest"],
        authority=authority, evidence_module=evidence_module,
        repository=repository, token=token)
    return CanonicalReplacementAdapter(
        admission=admission, original_intent=original,
        authenticated_evidence=authenticated, fence=fence, store=store,
        package_producer_run=original["packageProducerRun"],
        producing_run=current_run, producing_attempt=current_attempt, clock=clock)
