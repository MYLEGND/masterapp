#!/usr/bin/env python3
"""Fail-closed replacement-publication admission state machine.

This module is deliberately dormant: it defines an evidence/serialization contract
but performs no provider I/O and is not wired into production publication.

The integration which supplies claim/append/lookup callbacks must authenticate the
referenced evidence and provide real write-time serialization. Digest shape alone
is never evidence authenticity.

An existing ambiguous deployment intent is historical evidence and is never
rewritten, deleted, or reclassified by this module.
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
_PHASES = (
    "intent", "submitted", "ambiguous", "success", "failure",
    "resolution-success", "resolution-failure",
)


class RecoveryAdmissionError(RuntimeError):
    """The replacement operation is not safely admitted."""


def _canonical(value: Mapping) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode()


def _identity(value: Mapping) -> str:
    return hashlib.sha256(_canonical(value)).hexdigest()


def _record_digest(value: Mapping) -> str:
    return _identity(dict(value))


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


def validate_recovery_admission(record: Mapping, *, now: Optional[_dt.datetime] = None,
                                require_fresh: bool = True) -> dict:
    """Validate the immutable admission envelope.

    Authenticity of evidence digests is intentionally outside this primitive and
    must be established by the future reviewed production integration.
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
    if require_fresh:
        current = now or _dt.datetime.now(_dt.timezone.utc)
        if current.tzinfo is None:
            raise RecoveryAdmissionError("Recovery admission clock must be timezone-aware")
        current = current.astimezone(_dt.timezone.utc)
        if current < observed - _dt.timedelta(seconds=5) or current > expires:
            raise RecoveryAdmissionError("Recovery admission is not currently fresh")
    if record["admissionId"] != recovery_admission_id(record):
        raise RecoveryAdmissionError("Recovery admission identity mismatch")
    return dict(record)


@dataclass(frozen=True)
class RecoveryState:
    state: str
    operation_id: str
    replay_authorized: bool = False


class ReplacementOperationJournal:
    """One-use replacement journal with atomic claim and append-only reconciliation.

    lookup(phase, operation_id) -> authenticated record or None.
    claim(name, record) -> {"status": "created"|"existing", "record": exact_record}.
      "created" MUST mean exclusive durable creation and exact validated readback.
      Any exception/uncertain write fails closed; this class never assumes ownership.
    append(name, record) -> synchronously append/read back the exact immutable record.

    The future production integration must provide real provider serialization and
    evidence authentication. This module cannot execute a deployment by itself.
    """

    def __init__(self, *, admission: Mapping, original_intent: Mapping,
                 lookup: Callable[[str, str], Optional[Mapping]],
                 claim: Callable[[str, Mapping], Mapping],
                 append: Callable[[str, Mapping], object],
                 package_producer_run: int, producing_run: int, producing_attempt: int,
                 clock: Optional[Callable[[], _dt.datetime]] = None):
        self.clock = clock or (lambda: _dt.datetime.now(_dt.timezone.utc))
        # Structural validation only: an expired consumed operation must still load
        # for reconciliation. Freshness is checked again immediately before claim.
        self.admission = validate_recovery_admission(admission, require_fresh=False)
        self.lookup = lookup
        self.claim = claim
        self.append = append
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
        self.resolution_success = self._read("resolution-success")
        self.resolution_failure = self._read("resolution-failure")
        self._submission_authority = False
        self._validate_existing_state()

    def _now(self) -> _dt.datetime:
        value = self.clock()
        if not isinstance(value, _dt.datetime) or value.tzinfo is None:
            raise RecoveryAdmissionError("Recovery clock must return timezone-aware datetime")
        return value.astimezone(_dt.timezone.utc)

    def _read(self, phase: str) -> Optional[dict]:
        if phase not in _PHASES:
            raise RecoveryAdmissionError("Unknown replacement evidence phase")
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

    @staticmethod
    def _positive(value: object, label: str) -> int:
        if type(value) is not int or value < 1:
            raise RecoveryAdmissionError(f"Invalid {label}")
        return value

    def _contract(self, record: Mapping) -> dict:
        present = [key for key in ("submissionContract", "providerRequestCorrelation") if key in record]
        if len(present) != 1:
            raise RecoveryAdmissionError("Replacement record must contain exactly one submission contract")
        value = record[present[0]]
        if (not isinstance(value, Mapping) or set(value) != {"kind", "value"} or
                value.get("kind") not in _ALLOWED_SUBMISSION_KINDS or
                value.get("value") != self.operation_id):
            raise RecoveryAdmissionError("Replacement submission contract is not bound")
        expected_key = ("submissionContract" if value["kind"] == "azure_onedeploy_windows_intent_v1"
                        else "providerRequestCorrelation")
        if present[0] != expected_key:
            raise RecoveryAdmissionError("Replacement submission contract key is inconsistent")
        return dict(value)

    def _validate_common(self, record: Mapping, phase: str) -> None:
        if not isinstance(record, Mapping):
            raise RecoveryAdmissionError(f"Retained replacement {phase} is not an object")
        required = {
            "schemaVersion", "phase", "operationId", "target", "applicationRevision",
            "packageDigest", "baseline", "packageProducerRun", "producingRun",
            "producingAttempt", "baselineDeploymentIds", "replacementOfOperationId",
            "recoveryAdmissionId", "recoveryAdmission",
        }
        if not required.issubset(record):
            raise RecoveryAdmissionError(f"Retained replacement {phase} shape is incomplete")
        if (record.get("schemaVersion") != 1 or record.get("operationId") != self.operation_id or
                record.get("phase") != phase or record.get("target") != self.admission["target"] or
                record.get("applicationRevision") != self.admission["applicationRevision"] or
                record.get("packageDigest") != self.admission["packageDigest"] or
                record.get("baseline") != self.admission["baselineRevision"] or
                record.get("replacementOfOperationId") != self.admission["originalOperationId"] or
                record.get("recoveryAdmissionId") != self.admission["admissionId"] or
                record.get("recoveryAdmission") != self.admission):
            raise RecoveryAdmissionError(f"Retained replacement {phase} identity mismatch")
        self._positive(record.get("packageProducerRun"), "packageProducerRun")
        self._positive(record.get("producingRun"), "producingRun")
        self._positive(record.get("producingAttempt"), "producingAttempt")
        self._ids(record.get("baselineDeploymentIds"))
        self._contract(record)

    def _validate_phase(self, record: Mapping, phase: str) -> None:
        self._validate_common(record, phase)
        base = {
            "schemaVersion", "phase", "operationId", "target", "applicationRevision",
            "packageDigest", "baseline", "packageProducerRun", "producingRun",
            "producingAttempt", "baselineDeploymentIds", "replacementOfOperationId",
            "recoveryAdmissionId", "recoveryAdmission",
        }
        contract_key = ("submissionContract" if "submissionContract" in record
                        else "providerRequestCorrelation")
        allowed = set(base) | {contract_key}
        if phase == "submitted":
            allowed |= {"deploymentId"}
            deployment = _provider_id(record.get("deploymentId"), "replacement deploymentId")
            if deployment in record["baselineDeploymentIds"]:
                raise RecoveryAdmissionError("Replacement deployment must be outside the baseline set")
        elif phase == "ambiguous":
            allowed |= {"ambiguityEvidenceDigest", "replayAuthorized"}
            _digest(record.get("ambiguityEvidenceDigest"), "ambiguity evidence digest")
            if record.get("replayAuthorized") is not False:
                raise RecoveryAdmissionError("Ambiguous replacement cannot authorize replay")
        elif phase in {"success", "resolution-success"}:
            allowed |= {
                "deploymentIds", "providerTerminalStatus", "runtimeRevision",
                "liveAcceptanceDigest", "contentVerificationDigest",
                "serializationReleaseDigest",
            }
            if phase == "resolution-success":
                allowed |= {"ambiguityRecordDigest", "reconciliationEvidenceDigest"}
                _digest(record.get("ambiguityRecordDigest"), "ambiguity record digest")
                _digest(record.get("reconciliationEvidenceDigest"), "reconciliation evidence digest")
            ids = self._ids(record.get("deploymentIds"))
            if len(ids) != 1 or ids[0] in record["baselineDeploymentIds"]:
                raise RecoveryAdmissionError("Successful replacement requires one new provider deployment")
            if record.get("providerTerminalStatus") != 4:
                raise RecoveryAdmissionError("Successful replacement requires terminal provider status4")
            _sha(record.get("runtimeRevision"), 40, "runtime revision")
            if record["runtimeRevision"] != self.admission["applicationRevision"]:
                raise RecoveryAdmissionError("Exact replacement runtime revision is not live")
            _digest(record.get("liveAcceptanceDigest"), "live acceptance digest")
            _digest(record.get("contentVerificationDigest"), "content verification digest")
            _digest(record.get("serializationReleaseDigest"), "serialization release digest")
        elif phase in {"failure", "resolution-failure"}:
            allowed |= {
                "failureCode", "terminalEvidenceDigest",
                "serializationReleaseDigest", "replayAuthorized",
            }
            if phase == "resolution-failure":
                allowed |= {"ambiguityRecordDigest", "reconciliationEvidenceDigest"}
                _digest(record.get("ambiguityRecordDigest"), "ambiguity record digest")
                _digest(record.get("reconciliationEvidenceDigest"), "reconciliation evidence digest")
            if record.get("failureCode") not in _ALLOWED_FAILURE_CODES:
                raise RecoveryAdmissionError("Unrecognized terminal replacement failure")
            _digest(record.get("terminalEvidenceDigest"), "terminal failure evidence digest")
            _digest(record.get("serializationReleaseDigest"), "serialization release digest")
            if record.get("replayAuthorized") is not False:
                raise RecoveryAdmissionError("Terminal replacement failure cannot authorize replay")
        elif phase != "intent":
            raise RecoveryAdmissionError("Unknown replacement evidence phase")
        if set(record) != allowed:
            raise RecoveryAdmissionError(f"Retained replacement {phase} contains missing or extra phase fields")

    def _same_lineage(self, left: Mapping, right: Mapping) -> None:
        immutable = (
            "operationId", "target", "applicationRevision", "packageDigest", "baseline",
            "packageProducerRun", "baselineDeploymentIds", "replacementOfOperationId",
            "recoveryAdmissionId", "recoveryAdmission",
        )
        if any(left.get(key) != right.get(key) for key in immutable):
            raise RecoveryAdmissionError("Replacement evidence lineage conflict")
        if self._contract(left) != self._contract(right):
            raise RecoveryAdmissionError("Replacement submission contract conflict")

    def _validate_existing_state(self) -> None:
        phases = [
            ("intent", self.intent), ("submitted", self.submitted),
            ("ambiguous", self.ambiguous), ("success", self.success),
            ("failure", self.failure), ("resolution-success", self.resolution_success),
            ("resolution-failure", self.resolution_failure),
        ]
        present = [(phase, record) for phase, record in phases if record is not None]
        for phase, record in present:
            self._validate_phase(record, phase)
        if any(phase != "intent" for phase, _ in present) and self.intent is None:
            raise RecoveryAdmissionError("Replacement outcome exists without durable intent")
        if self.intent is not None:
            for phase, record in present:
                if phase != "intent":
                    self._same_lineage(self.intent, record)
        if self.submitted is not None:
            deployment = self.submitted["deploymentId"]
            for terminal in (self.success, self.resolution_success):
                if terminal is not None and terminal["deploymentIds"] != [deployment]:
                    raise RecoveryAdmissionError("Replacement success conflicts with submitted deployment")
        direct_terminal = [x for x in (self.success, self.failure) if x is not None]
        if len(direct_terminal) > 1:
            raise RecoveryAdmissionError("Competing direct terminal replacement outcomes")
        resolutions = [x for x in (self.resolution_success, self.resolution_failure) if x is not None]
        if len(resolutions) > 1:
            raise RecoveryAdmissionError("Competing ambiguity resolution outcomes")
        if self.ambiguous is not None:
            if direct_terminal:
                raise RecoveryAdmissionError("Ambiguous history cannot coexist with direct terminal outcome")
            digest = _record_digest(self.ambiguous)
            for resolution in resolutions:
                if resolution.get("ambiguityRecordDigest") != digest:
                    raise RecoveryAdmissionError("Ambiguity resolution does not bind retained ambiguity")
        elif resolutions:
            raise RecoveryAdmissionError("Ambiguity resolution exists without retained ambiguity")

    def state(self) -> RecoveryState:
        if self.success is not None or self.resolution_success is not None:
            return RecoveryState("terminal-success", self.operation_id)
        if self.failure is not None or self.resolution_failure is not None:
            return RecoveryState("terminal-failure", self.operation_id)
        if self.ambiguous is not None or self.submitted is not None or self.intent is not None:
            return RecoveryState("reconcile-only", self.operation_id)
        return RecoveryState("admitted-not-consumed", self.operation_id, replay_authorized=True)

    def _revoke_submit(self) -> None:
        self._submission_authority = False

    def before_submit(self, baseline_ids, *, submission_kind: str) -> bool:
        """Atomically consume the one-use admission before provider I/O."""
        if submission_kind not in _ALLOWED_SUBMISSION_KINDS:
            raise RecoveryAdmissionError("Unsupported replacement submission contract")
        if self.intent is not None or any(x is not None for x in (
                self.submitted, self.ambiguous, self.success, self.failure,
                self.resolution_success, self.resolution_failure)):
            return False
        # Freshness is intentionally checked at claim time, not construction time.
        validate_recovery_admission(self.admission, now=self._now(), require_fresh=True)
        ids = self._ids(baseline_ids)
        record = self._base_record(phase="intent", baseline_ids=ids)
        contract_key = ("submissionContract" if submission_kind == "azure_onedeploy_windows_intent_v1"
                        else "providerRequestCorrelation")
        record[contract_key] = {"kind": submission_kind, "value": self.operation_id}
        name = "legend-release-operation-intent-" + self.operation_id
        try:
            result = self.claim(name, record)
        except Exception as exc:
            # Claim may have committed. Re-read exact identity once and fail closed
            # regardless: uncertain ownership never grants provider authority.
            observed = self._read("intent")
            if observed is not None:
                self._validate_phase(observed, "intent")
                self._same_lineage(record, observed)
                if observed != record:
                    raise RecoveryAdmissionError("Uncertain replacement claim conflicts with durable intent") from exc
                self.intent = observed
            self._revoke_submit()
            raise RecoveryAdmissionError("Replacement intent claim outcome uncertain; reconcile-only") from exc
        if not isinstance(result, Mapping) or set(result) != {"status", "record"}:
            self._revoke_submit()
            raise RecoveryAdmissionError("Atomic replacement claim did not return validated ownership")
        status = result.get("status")
        readback = result.get("record")
        if status not in {"created", "existing"} or not isinstance(readback, Mapping):
            self._revoke_submit()
            raise RecoveryAdmissionError("Atomic replacement claim result is invalid")
        readback = dict(readback)
        self._validate_phase(readback, "intent")
        self._same_lineage(record, readback)
        if readback != record:
            self._revoke_submit()
            raise RecoveryAdmissionError("Atomic replacement claim readback mismatch")
        self.intent = readback
        if status == "existing":
            self._revoke_submit()
            return False
        # Only an exclusive "created" claim with exact readback grants one provider call.
        self._submission_authority = True
        return True

    def submission_correlation(self) -> dict:
        if (not self._submission_authority or self.intent is None or
                any(x is not None for x in (
                    self.submitted, self.ambiguous, self.success, self.failure,
                    self.resolution_success, self.resolution_failure))):
            raise RecoveryAdmissionError("Replacement submission authority is not available")
        return self._contract(self.intent)

    def record_submission_observation(self, deployment_id: str) -> dict:
        _provider_id(deployment_id, "replacement deploymentId")
        if self.intent is None:
            raise RecoveryAdmissionError("Durable replacement intent required")
        if not self._submission_authority:
            raise RecoveryAdmissionError("Replacement submission authority is not available")
        if deployment_id in self.intent["baselineDeploymentIds"]:
            raise RecoveryAdmissionError("Replacement deployment must be outside the baseline set")
        self._revoke_submit()
        if self.submitted is not None:
            if self.submitted.get("deploymentId") != deployment_id:
                raise RecoveryAdmissionError("Conflicting replacement provider observation")
            return self.submitted
        record = dict(self.intent, phase="submitted", deploymentId=deployment_id,
                      producingRun=self.producing_run, producingAttempt=self.producing_attempt)
        self._validate_phase(record, "submitted")
        self.append("legend-release-operation-submitted-" + self.operation_id, record)
        self.submitted = record
        return record

    def record_ambiguous(self, *, ambiguity_evidence_digest: str) -> dict:
        _digest(ambiguity_evidence_digest, "ambiguity evidence digest")
        if self.intent is None:
            raise RecoveryAdmissionError("Durable replacement intent required")
        if self.success is not None or self.failure is not None or self.resolution_success is not None or self.resolution_failure is not None:
            raise RecoveryAdmissionError("Terminal replacement outcome already exists")
        self._revoke_submit()
        if self.ambiguous is not None:
            return self.ambiguous
        record = dict(self.intent, phase="ambiguous",
                      ambiguityEvidenceDigest=ambiguity_evidence_digest,
                      replayAuthorized=False,
                      producingRun=self.producing_run, producingAttempt=self.producing_attempt)
        self._validate_phase(record, "ambiguous")
        self.append("legend-release-operation-ambiguous-" + self.operation_id, record)
        self.ambiguous = record
        return record

    def _success_record(self, phase: str, *, deployment_id: str, runtime_revision: str,
                        live_acceptance_digest: str, content_verification_digest: str,
                        serialization_release_digest: str, reconciliation_evidence_digest: Optional[str] = None) -> dict:
        _provider_id(deployment_id, "successful replacement deploymentId")
        _sha(runtime_revision, 40, "runtime revision")
        for value, label in (
            (live_acceptance_digest, "live acceptance digest"),
            (content_verification_digest, "content verification digest"),
            (serialization_release_digest, "serialization release digest"),
        ):
            _digest(value, label)
        if runtime_revision != self.admission["applicationRevision"]:
            raise RecoveryAdmissionError("Exact replacement runtime revision is not live")
        if deployment_id in self.intent["baselineDeploymentIds"]:
            raise RecoveryAdmissionError("Successful replacement must be a new provider deployment")
        if self.submitted is not None and self.submitted.get("deploymentId") != deployment_id:
            raise RecoveryAdmissionError("Successful replacement differs from retained provider observation")
        record = dict(self.intent, phase=phase, deploymentIds=[deployment_id],
                      providerTerminalStatus=4, runtimeRevision=runtime_revision,
                      liveAcceptanceDigest=live_acceptance_digest,
                      contentVerificationDigest=content_verification_digest,
                      serializationReleaseDigest=serialization_release_digest,
                      producingRun=self.producing_run, producingAttempt=self.producing_attempt)
        if phase == "resolution-success":
            _digest(reconciliation_evidence_digest, "reconciliation evidence digest")
            record.update(
                ambiguityRecordDigest=_record_digest(self.ambiguous),
                reconciliationEvidenceDigest=reconciliation_evidence_digest,
            )
        self._validate_phase(record, phase)
        return record

    def record_success(self, *, deployment_id: str, runtime_revision: str,
                       live_acceptance_digest: str, content_verification_digest: str,
                       serialization_release_digest: str) -> dict:
        if self.intent is None or self.ambiguous is not None or self.failure is not None or self.resolution_failure is not None:
            raise RecoveryAdmissionError("Replacement success cannot override unresolved or terminal evidence")
        self._revoke_submit()
        if self.success is not None:
            return self.success
        record = self._success_record(
            "success", deployment_id=deployment_id, runtime_revision=runtime_revision,
            live_acceptance_digest=live_acceptance_digest,
            content_verification_digest=content_verification_digest,
            serialization_release_digest=serialization_release_digest)
        self.append("legend-release-operation-success-" + self.operation_id, record)
        self.success = record
        return record

    def resolve_ambiguous_success(self, *, deployment_id: str, runtime_revision: str,
                                  live_acceptance_digest: str, content_verification_digest: str,
                                  serialization_release_digest: str,
                                  reconciliation_evidence_digest: str) -> dict:
        """Append authenticated terminal resolution without erasing ambiguity."""
        if self.intent is None or self.ambiguous is None:
            raise RecoveryAdmissionError("Retained ambiguous replacement evidence is required")
        if self.success is not None or self.failure is not None or self.resolution_failure is not None:
            raise RecoveryAdmissionError("Conflicting replacement terminal outcome already exists")
        self._revoke_submit()
        if self.resolution_success is not None:
            return self.resolution_success
        record = self._success_record(
            "resolution-success", deployment_id=deployment_id, runtime_revision=runtime_revision,
            live_acceptance_digest=live_acceptance_digest,
            content_verification_digest=content_verification_digest,
            serialization_release_digest=serialization_release_digest,
            reconciliation_evidence_digest=reconciliation_evidence_digest)
        self.append("legend-release-operation-resolution-success-" + self.operation_id, record)
        self.resolution_success = record
        return record

    def _failure_record(self, phase: str, *, failure_code: str, terminal_evidence_digest: str,
                        serialization_release_digest: str, reconciliation_evidence_digest: Optional[str] = None) -> dict:
        if failure_code not in _ALLOWED_FAILURE_CODES:
            raise RecoveryAdmissionError("Unrecognized terminal replacement failure")
        _digest(terminal_evidence_digest, "terminal failure evidence digest")
        _digest(serialization_release_digest, "serialization release digest")
        record = dict(self.intent, phase=phase, failureCode=failure_code,
                      terminalEvidenceDigest=terminal_evidence_digest,
                      serializationReleaseDigest=serialization_release_digest,
                      replayAuthorized=False,
                      producingRun=self.producing_run, producingAttempt=self.producing_attempt)
        if phase == "resolution-failure":
            _digest(reconciliation_evidence_digest, "reconciliation evidence digest")
            record.update(
                ambiguityRecordDigest=_record_digest(self.ambiguous),
                reconciliationEvidenceDigest=reconciliation_evidence_digest,
            )
        self._validate_phase(record, phase)
        return record

    def record_terminal_failure(self, *, failure_code: str, terminal_evidence_digest: str,
                                serialization_release_digest: str) -> dict:
        if self.intent is None or self.ambiguous is not None or self.success is not None or self.resolution_success is not None:
            raise RecoveryAdmissionError("Replacement failure cannot override unresolved or terminal evidence")
        self._revoke_submit()
        if self.failure is not None:
            return self.failure
        record = self._failure_record(
            "failure", failure_code=failure_code,
            terminal_evidence_digest=terminal_evidence_digest,
            serialization_release_digest=serialization_release_digest)
        self.append("legend-release-operation-failure-" + self.operation_id, record)
        self.failure = record
        return record

    def resolve_ambiguous_failure(self, *, failure_code: str, terminal_evidence_digest: str,
                                  serialization_release_digest: str,
                                  reconciliation_evidence_digest: str) -> dict:
        """Append authenticated terminal failure resolution without erasing ambiguity."""
        if self.intent is None or self.ambiguous is None:
            raise RecoveryAdmissionError("Retained ambiguous replacement evidence is required")
        if self.success is not None or self.failure is not None or self.resolution_success is not None:
            raise RecoveryAdmissionError("Conflicting replacement terminal outcome already exists")
        self._revoke_submit()
        if self.resolution_failure is not None:
            return self.resolution_failure
        record = self._failure_record(
            "resolution-failure", failure_code=failure_code,
            terminal_evidence_digest=terminal_evidence_digest,
            serialization_release_digest=serialization_release_digest,
            reconciliation_evidence_digest=reconciliation_evidence_digest)
        self.append("legend-release-operation-resolution-failure-" + self.operation_id, record)
        self.resolution_failure = record
        return record
