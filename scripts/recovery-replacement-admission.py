#!/usr/bin/env python3
"""Fail-closed replacement-publication admission state machine.

This module is deliberately not a production publisher. It defines the durable
identity and one-use evidence contract that a future, independently reviewed
integration may consume after provider terminality/nonresumability, serialization,
no-SQL, rollback and exact-control review have all been authenticated.

An existing ambiguous deployment intent is historical evidence and is never
rewritten or reclassified by this module.
"""
from __future__ import annotations

import datetime as _dt
import hashlib
import json
import re
from dataclasses import dataclass
from typing import Callable, Mapping, Optional

_SHA40 = re.compile(r"^[a-f0-9]{40}$")
_SHA64 = re.compile(r"^[a-f0-9]{64}$")
_PROVIDER_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,255}$")
_TARGET = re.compile(r"^[a-z][a-z0-9-]{0,63}$")
_ALLOWED_SUBMISSION_KINDS = {
    "azure_onedeploy_deployment_tag_v1",
    "azure_onedeploy_windows_intent_v1",
}
_ALLOWED_FAILURE_CODES = {
    "provider-terminal-failure",
    "post-publication-runtime-mismatch",
    "post-publication-live-acceptance-failure",
    "post-publication-content-verification-failure",
}
_ADMISSION_WINDOW = _dt.timedelta(minutes=15)


class RecoveryAdmissionError(RuntimeError):
    """The replacement operation is not safely admitted."""


def _canonical(value: Mapping) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode()


def _identity(value: Mapping) -> str:
    return hashlib.sha256(_canonical(value)).hexdigest()


def _utc(value: str) -> _dt.datetime:
    if not isinstance(value, str) or not value.endswith("Z"):
        raise RecoveryAdmissionError("Recovery admission timestamp must be UTC")
    try:
        parsed = _dt.datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as exc:
        raise RecoveryAdmissionError("Recovery admission timestamp is invalid") from exc
    return parsed.astimezone(_dt.timezone.utc)


def _sha(value: object, size: int, label: str) -> str:
    pattern = _SHA40 if size == 40 else _SHA64
    if not isinstance(value, str) or not pattern.fullmatch(value):
        raise RecoveryAdmissionError(f"Invalid {label}")
    return value


def _digest(value: object, label: str) -> str:
    return _sha(value, 64, label)


def _provider_id(value: object, label: str) -> str:
    if not isinstance(value, str) or not _PROVIDER_ID.fullmatch(value):
        raise RecoveryAdmissionError(f"Invalid {label}")
    return value


def recovery_admission_id(record: Mapping) -> str:
    """Compute the immutable admission identity over every field except admissionId."""
    body = dict(record)
    body.pop("admissionId", None)
    return _identity(body)


def replacement_operation_id(*, target: str, application_revision: str, package_digest: str,
                             original_operation_id: str, admission_id: str) -> str:
    """A replacement is a distinct operation, never a relabelled old intent."""
    return _identity({
        "admissionId": admission_id,
        "applicationRevision": application_revision,
        "originalOperationId": original_operation_id,
        "packageDigest": package_digest,
        "target": target,
    })


def validate_recovery_admission(record: Mapping, *, now: Optional[_dt.datetime] = None) -> dict:
    """Validate the authenticated admission envelope, not the provider facts themselves.

    The issuer/reviewer of this record remains responsible for authenticating the
    referenced evidence. This module prevents missing, stale, mutated or
    under-scoped evidence from becoming an operation intent.
    """
    if not isinstance(record, Mapping):
        raise RecoveryAdmissionError("Recovery admission must be an object")
    expected = {
        "schemaVersion", "phase", "admissionId", "target", "originalOperationId",
        "applicationRevision", "packageDigest", "baselineRevision", "expectedHead",
        "controlRevision", "rollbackPackageDigest", "terminalNonresumableEvidenceDigest",
        "serializationFenceEvidenceDigest", "noSqlEvidenceDigest", "rollbackEvidenceDigest",
        "independentReviewDigest", "contentMode", "serializationKey", "observedAtUtc",
        "expiresAtUtc", "authorizesOneReplacement",
    }
    if set(record) != expected:
        raise RecoveryAdmissionError("Recovery admission shape is incomplete or widened")
    if record["schemaVersion"] != 1 or record["phase"] != "replacement-admission":
        raise RecoveryAdmissionError("Unsupported recovery admission schema or phase")
    if record["target"] != "protect" or not _TARGET.fullmatch(record["target"]):
        raise RecoveryAdmissionError("Replacement admission is scoped to Protect only")
    for key in ("applicationRevision", "baselineRevision", "expectedHead", "controlRevision"):
        _sha(record[key], 40, key)
    for key in (
        "packageDigest", "rollbackPackageDigest", "terminalNonresumableEvidenceDigest",
        "serializationFenceEvidenceDigest", "noSqlEvidenceDigest", "rollbackEvidenceDigest",
        "independentReviewDigest", "originalOperationId",
    ):
        _digest(record[key], key)
    if record["contentMode"] != "deterministic-full-replacement":
        raise RecoveryAdmissionError("Recovery must use reviewed deterministic full replacement")
    if record["serializationKey"] != "legend-approved-release":
        raise RecoveryAdmissionError("Recovery must retain the canonical publication mutex")
    if record["authorizesOneReplacement"] is not True:
        raise RecoveryAdmissionError("Recovery admission must authorize exactly one replacement")
    if record["expectedHead"] != record["applicationRevision"]:
        raise RecoveryAdmissionError("Recovery admission must bind the exact candidate head")
    observed = _utc(record["observedAtUtc"])
    expires = _utc(record["expiresAtUtc"])
    if expires <= observed or expires - observed > _ADMISSION_WINDOW:
        raise RecoveryAdmissionError("Recovery admission freshness window is invalid")
    current = now or _dt.datetime.now(_dt.timezone.utc)
    if current.tzinfo is None:
        raise RecoveryAdmissionError("Recovery admission clock must be timezone-aware")
    current = current.astimezone(_dt.timezone.utc)
    if current < observed - _dt.timedelta(seconds=5) or current > expires:
        raise RecoveryAdmissionError("Recovery admission is not currently fresh")
    expected_id = recovery_admission_id(record)
    if record["admissionId"] != expected_id:
        raise RecoveryAdmissionError("Recovery admission identity mismatch")
    return dict(record)


@dataclass(frozen=True)
class RecoveryState:
    state: str
    operation_id: str
    replay_authorized: bool = False


class ReplacementOperationJournal:
    """One-use replacement journal with fail-closed ambiguity semantics.

    lookup(phase, operation_id) must return an authenticated durable record or None.
    publish(name, record) must synchronously publish and read back the exact record.
    Neither callback is implemented here so this module cannot mutate production by itself.
    """

    def __init__(self, *, admission: Mapping, original_intent: Mapping,
                 lookup: Callable[[str, str], Optional[Mapping]],
                 publish: Callable[[str, Mapping], object],
                 package_producer_run: int, producing_run: int, producing_attempt: int,
                 now: Optional[_dt.datetime] = None):
        self.admission = validate_recovery_admission(admission, now=now)
        self.lookup = lookup
        self.publish = publish
        self.package_producer_run = package_producer_run
        self.producing_run = producing_run
        self.producing_attempt = producing_attempt
        if any(type(v) is not int or v < 1 for v in
               (package_producer_run, producing_run, producing_attempt)):
            raise RecoveryAdmissionError("Replacement operation producer identity is invalid")
        if not isinstance(original_intent, Mapping):
            raise RecoveryAdmissionError("Original ambiguous intent is required")
        for key in ("operationId", "target", "applicationRevision", "packageDigest", "baseline"):
            if key not in original_intent:
                raise RecoveryAdmissionError("Original ambiguous intent identity is incomplete")
        _digest(original_intent["operationId"], "original operationId")
        if original_intent["operationId"] != self.admission["originalOperationId"]:
            raise RecoveryAdmissionError("Admission does not bind the preserved original intent")
        if original_intent["target"] != self.admission["target"]:
            raise RecoveryAdmissionError("Admission target differs from preserved original intent")
        self.original_intent = dict(original_intent)
        self.operation_id = replacement_operation_id(
            target=self.admission["target"],
            application_revision=self.admission["applicationRevision"],
            package_digest=self.admission["packageDigest"],
            original_operation_id=self.admission["originalOperationId"],
            admission_id=self.admission["admissionId"],
        )
        if self.operation_id == self.admission["originalOperationId"]:
            raise RecoveryAdmissionError("Replacement operation must have a distinct identity")
        self.intent = self._read("intent")
        self.submitted = self._read("submitted")
        self.ambiguous = self._read("ambiguous")
        self.success = self._read("success")
        self.failure = self._read("failure")
        self._new_intent_readback = False
        self._validate_existing_state()

    def _read(self, phase: str) -> Optional[dict]:
        value = self.lookup(phase, self.operation_id)
        return None if value is None else dict(value)

    def _base_record(self, *, phase: str, baseline_ids: list[str]) -> dict:
        return {
            "schemaVersion": 1,
            "phase": phase,
            "operationId": self.operation_id,
            "target": self.admission["target"],
            "applicationRevision": self.admission["applicationRevision"],
            "packageDigest": self.admission["packageDigest"],
            "baseline": self.admission["baselineRevision"],
            "packageProducerRun": self.package_producer_run,
            "producingRun": self.producing_run,
            "producingAttempt": self.producing_attempt,
            "baselineDeploymentIds": sorted(set(baseline_ids)),
            "replacementOfOperationId": self.admission["originalOperationId"],
            "recoveryAdmissionId": self.admission["admissionId"],
            "recoveryAdmission": dict(self.admission),
        }

    @staticmethod
    def _ids(values) -> list[str]:
        if (not isinstance(values, (list, tuple, set)) or len(values) > 1000 or
                any(not isinstance(v, str) or not _PROVIDER_ID.fullmatch(v) for v in values)):
            raise RecoveryAdmissionError("Invalid provider deployment identity set")
        return sorted(set(values))

    def _validate_record_identity(self, record: Mapping, phase: str) -> None:
        if (record.get("operationId") != self.operation_id or record.get("phase") != phase or
                record.get("target") != self.admission["target"] or
                record.get("applicationRevision") != self.admission["applicationRevision"] or
                record.get("packageDigest") != self.admission["packageDigest"] or
                record.get("baseline") != self.admission["baselineRevision"] or
                record.get("replacementOfOperationId") != self.admission["originalOperationId"] or
                record.get("recoveryAdmissionId") != self.admission["admissionId"] or
                record.get("recoveryAdmission") != self.admission):
            raise RecoveryAdmissionError(f"Retained replacement {phase} identity mismatch")
        self._ids(record.get("baselineDeploymentIds"))

    def _validate_existing_state(self) -> None:
        phases = [("intent", self.intent), ("submitted", self.submitted),
                  ("ambiguous", self.ambiguous), ("success", self.success), ("failure", self.failure)]
        for phase, record in phases:
            if record is not None:
                self._validate_record_identity(record, phase)
        terminal = [x for x in (self.success, self.failure) if x is not None]
        if len(terminal) > 1:
            raise RecoveryAdmissionError("Competing terminal replacement outcomes require reconciliation")
        if self.ambiguous is not None and terminal:
            raise RecoveryAdmissionError("Ambiguous and terminal replacement outcomes conflict")
        if any(x is not None for x in (self.submitted, self.ambiguous, self.success, self.failure)) and self.intent is None:
            raise RecoveryAdmissionError("Replacement outcome exists without durable intent")

    def state(self) -> RecoveryState:
        if self.success is not None:
            return RecoveryState("terminal-success", self.operation_id)
        if self.failure is not None:
            return RecoveryState("terminal-failure", self.operation_id)
        if self.ambiguous is not None or self.submitted is not None or self.intent is not None:
            return RecoveryState("reconcile-only", self.operation_id)
        return RecoveryState("admitted-not-consumed", self.operation_id, replay_authorized=True)

    def before_submit(self, baseline_ids, *, submission_kind: str) -> bool:
        """Atomically consume the one-use admission before provider I/O."""
        if submission_kind not in _ALLOWED_SUBMISSION_KINDS:
            raise RecoveryAdmissionError("Unsupported replacement submission contract")
        if self.intent is not None or any(x is not None for x in (self.submitted, self.ambiguous, self.success, self.failure)):
            return False
        ids = self._ids(baseline_ids)
        record = self._base_record(phase="intent", baseline_ids=ids)
        contract_key = ("submissionContract" if submission_kind == "azure_onedeploy_windows_intent_v1"
                        else "providerRequestCorrelation")
        record[contract_key] = {"kind": submission_kind, "value": self.operation_id}
        self.publish("legend-release-operation-intent-" + self.operation_id, record)
        self.intent = record
        self._new_intent_readback = True
        return True

    def submission_correlation(self) -> dict:
        if not self._new_intent_readback or self.intent is None:
            raise RecoveryAdmissionError("Fresh durable replacement intent required before provider I/O")
        key = "submissionContract" if "submissionContract" in self.intent else "providerRequestCorrelation"
        value = self.intent.get(key)
        if (not isinstance(value, dict) or value.get("value") != self.operation_id or
                value.get("kind") not in _ALLOWED_SUBMISSION_KINDS):
            raise RecoveryAdmissionError("Replacement submission contract is not bound")
        return dict(value)

    def record_submission_observation(self, deployment_id: str) -> dict:
        _provider_id(deployment_id, "replacement deploymentId")
        if self.intent is None:
            raise RecoveryAdmissionError("Durable replacement intent required")
        if not self._new_intent_readback:
            raise RecoveryAdmissionError("Inherited replacement intent is reconcile-only")
        if deployment_id in self.intent["baselineDeploymentIds"]:
            raise RecoveryAdmissionError("Replacement deployment must be outside the baseline set")
        if self.submitted is not None:
            if self.submitted.get("deploymentId") != deployment_id:
                raise RecoveryAdmissionError("Conflicting replacement provider observation")
            return self.submitted
        record = dict(self.intent, phase="submitted", deploymentId=deployment_id,
                      producingRun=self.producing_run, producingAttempt=self.producing_attempt)
        self.publish("legend-release-operation-submitted-" + self.operation_id, record)
        self.submitted = record
        return record

    def record_ambiguous(self, *, ambiguity_evidence_digest: str) -> dict:
        """Persist ambiguity when possible; intent alone already forbids replay."""
        _digest(ambiguity_evidence_digest, "ambiguity evidence digest")
        if self.intent is None:
            raise RecoveryAdmissionError("Durable replacement intent required")
        if self.success is not None or self.failure is not None:
            raise RecoveryAdmissionError("Terminal replacement outcome already exists")
        if self.ambiguous is not None:
            return self.ambiguous
        record = dict(self.intent, phase="ambiguous",
                      ambiguityEvidenceDigest=ambiguity_evidence_digest,
                      replayAuthorized=False,
                      producingRun=self.producing_run, producingAttempt=self.producing_attempt)
        self.publish("legend-release-operation-ambiguous-" + self.operation_id, record)
        self.ambiguous = record
        return record

    def record_success(self, *, deployment_id: str, runtime_revision: str,
                       live_acceptance_digest: str, content_verification_digest: str,
                       serialization_release_digest: str) -> dict:
        if self.intent is None or self.ambiguous is not None or self.failure is not None:
            raise RecoveryAdmissionError("Replacement success cannot override unresolved or terminal evidence")
        _provider_id(deployment_id, "successful replacement deploymentId")
        _sha(runtime_revision, 40, "runtime revision")
        for value, label in ((live_acceptance_digest, "live acceptance digest"),
                             (content_verification_digest, "content verification digest"),
                             (serialization_release_digest, "serialization release digest")):
            _digest(value, label)
        if runtime_revision != self.admission["applicationRevision"]:
            raise RecoveryAdmissionError("Exact replacement runtime revision is not live")
        if deployment_id in self.intent["baselineDeploymentIds"]:
            raise RecoveryAdmissionError("Successful replacement must be a new provider deployment")
        if self.submitted is not None and self.submitted.get("deploymentId") != deployment_id:
            raise RecoveryAdmissionError("Successful replacement differs from retained provider observation")
        if self.success is not None:
            return self.success
        record = dict(self.intent, phase="success", deploymentIds=[deployment_id],
                      providerTerminalStatus=4, runtimeRevision=runtime_revision,
                      liveAcceptanceDigest=live_acceptance_digest,
                      contentVerificationDigest=content_verification_digest,
                      serializationReleaseDigest=serialization_release_digest,
                      producingRun=self.producing_run, producingAttempt=self.producing_attempt)
        self.publish("legend-release-operation-success-" + self.operation_id, record)
        self.success = record
        return record

    def record_terminal_failure(self, *, failure_code: str, terminal_evidence_digest: str,
                                serialization_release_digest: str) -> dict:
        if self.intent is None or self.ambiguous is not None or self.success is not None:
            raise RecoveryAdmissionError("Replacement failure cannot override unresolved or terminal evidence")
        if failure_code not in _ALLOWED_FAILURE_CODES:
            raise RecoveryAdmissionError("Unrecognized terminal replacement failure")
        _digest(terminal_evidence_digest, "terminal failure evidence digest")
        _digest(serialization_release_digest, "serialization release digest")
        if self.failure is not None:
            return self.failure
        record = dict(self.intent, phase="failure", failureCode=failure_code,
                      terminalEvidenceDigest=terminal_evidence_digest,
                      serializationReleaseDigest=serialization_release_digest,
                      replayAuthorized=False,
                      producingRun=self.producing_run, producingAttempt=self.producing_attempt)
        self.publish("legend-release-operation-failure-" + self.operation_id, record)
        self.failure = record
        return record
