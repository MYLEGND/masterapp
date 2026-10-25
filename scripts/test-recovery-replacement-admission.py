import datetime as dt
import importlib.util
import json
import pathlib
import sys
import unittest

ROOT = pathlib.Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('replacement', ROOT / 'recovery-replacement-admission.py')
m = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = m
spec.loader.exec_module(m)

NOW = dt.datetime(2026, 10, 25, 16, 40, tzinfo=dt.timezone.utc)


def admission(**changes):
    value = {
        'schemaVersion': 1,
        'phase': 'replacement-admission',
        'admissionId': '0' * 64,
        'target': 'protect',
        'originalOperationId': '1' * 64,
        'applicationRevision': 'a' * 40,
        'packageDigest': '2' * 64,
        'baselineRevision': 'b' * 40,
        'expectedHead': 'a' * 40,
        'controlRevision': 'c' * 40,
        'rollbackPackageDigest': '3' * 64,
        'terminalNonresumableEvidenceDigest': '4' * 64,
        'serializationFenceEvidenceDigest': '5' * 64,
        'noSqlEvidenceDigest': '6' * 64,
        'rollbackEvidenceDigest': '7' * 64,
        'independentReviewDigest': '8' * 64,
        'contentMode': 'deterministic-full-replacement',
        'serializationKey': 'legend-approved-release',
        'observedAtUtc': '2026-10-25T16:39:00Z',
        'expiresAtUtc': '2026-10-25T16:49:00Z',
        'authorizesOneReplacement': True,
    }
    value.update(changes)
    value['admissionId'] = m.recovery_admission_id(value)
    return value


def original_intent():
    return {
        'operationId': '1' * 64,
        'target': 'protect',
        'applicationRevision': 'd' * 40,
        'packageDigest': '9' * 64,
        'baseline': 'b' * 40,
    }


class Store:
    def __init__(self):
        self.rows = {}
        self.published = []

    def lookup(self, phase, operation):
        return self.rows.get((phase, operation))

    def publish(self, name, record):
        frozen = json.loads(json.dumps(record, sort_keys=True))
        key = (record['phase'], record['operationId'])
        if key in self.rows and self.rows[key] != frozen:
            raise AssertionError('conflicting durable record')
        self.rows[key] = frozen
        self.published.append((name, frozen))
        return {'artifactId': len(self.published)}


def journal(store=None, adm=None):
    store = store or Store()
    return m.ReplacementOperationJournal(
        admission=adm or admission(),
        original_intent=original_intent(),
        lookup=store.lookup,
        publish=store.publish,
        package_producer_run=20,
        producing_run=30,
        producing_attempt=1,
        now=NOW,
    ), store


class ReplacementAdmissionTests(unittest.TestCase):
    def test_new_operation_identity_is_distinct_and_binds_admission(self):
        j, _ = journal()
        self.assertNotEqual('1' * 64, j.operation_id)
        changed = admission(noSqlEvidenceDigest='f' * 64)
        j2, _ = journal(adm=changed)
        self.assertNotEqual(j.operation_id, j2.operation_id)

    def test_stale_or_snapshot_only_admission_is_rejected(self):
        with self.assertRaisesRegex(m.RecoveryAdmissionError, 'fresh'):
            journal(adm=admission(expiresAtUtc='2026-10-25T16:39:30Z'))
        broken = admission()
        broken.pop('terminalNonresumableEvidenceDigest')
        broken['admissionId'] = m.recovery_admission_id(broken)
        with self.assertRaisesRegex(m.RecoveryAdmissionError, 'shape'):
            journal(adm=broken)

    def test_admission_is_consumed_once_before_provider_io_and_resume_is_reconcile_only(self):
        j, store = journal()
        self.assertTrue(j.before_submit(['old'], submission_kind='azure_onedeploy_windows_intent_v1'))
        self.assertEqual('reconcile-only', j.state().state)
        self.assertEqual(j.operation_id, j.submission_correlation()['value'])
        resumed = m.ReplacementOperationJournal(
            admission=j.admission,
            original_intent=original_intent(),
            lookup=store.lookup,
            publish=store.publish,
            package_producer_run=20,
            producing_run=31,
            producing_attempt=1,
            now=NOW,
        )
        self.assertFalse(resumed.before_submit(['old'], submission_kind='azure_onedeploy_windows_intent_v1'))
        with self.assertRaisesRegex(m.RecoveryAdmissionError, 'Fresh durable replacement intent'):
            resumed.submission_correlation()
        self.assertEqual(1, len(store.published))

    def test_provider_observation_must_be_new_and_conflicts_fail(self):
        j, _ = journal()
        j.before_submit(['old'], submission_kind='azure_onedeploy_windows_intent_v1')
        with self.assertRaisesRegex(m.RecoveryAdmissionError, 'outside the baseline'):
            j.record_submission_observation('old')
        j.record_submission_observation('new-deployment')
        with self.assertRaisesRegex(m.RecoveryAdmissionError, 'Conflicting'):
            j.record_submission_observation('different-deployment')

    def test_ambiguous_result_is_durable_reconcile_only_and_never_replays(self):
        j, store = journal()
        j.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
        j.record_ambiguous(ambiguity_evidence_digest='a' * 64)
        self.assertEqual('reconcile-only', j.state().state)
        self.assertFalse(j.state().replay_authorized)
        resumed = m.ReplacementOperationJournal(
            admission=j.admission,
            original_intent=original_intent(),
            lookup=store.lookup,
            publish=store.publish,
            package_producer_run=20,
            producing_run=31,
            producing_attempt=1,
            now=NOW,
        )
        self.assertFalse(resumed.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1'))
        with self.assertRaisesRegex(m.RecoveryAdmissionError, 'unresolved'):
            resumed.record_success(
                deployment_id='new',
                runtime_revision='a' * 40,
                live_acceptance_digest='b' * 64,
                content_verification_digest='c' * 64,
                serialization_release_digest='d' * 64,
            )

    def test_success_requires_exact_runtime_content_live_acceptance_and_new_provider_id(self):
        j, _ = journal()
        j.before_submit(['old'], submission_kind='azure_onedeploy_windows_intent_v1')
        j.record_submission_observation('new')
        with self.assertRaisesRegex(m.RecoveryAdmissionError, 'runtime'):
            j.record_success(
                deployment_id='new',
                runtime_revision='e' * 40,
                live_acceptance_digest='b' * 64,
                content_verification_digest='c' * 64,
                serialization_release_digest='d' * 64,
            )
        success = j.record_success(
            deployment_id='new',
            runtime_revision='a' * 40,
            live_acceptance_digest='b' * 64,
            content_verification_digest='c' * 64,
            serialization_release_digest='d' * 64,
        )
        self.assertEqual(4, success['providerTerminalStatus'])
        self.assertEqual('terminal-success', j.state().state)

    def test_terminal_failure_is_final_and_does_not_authorize_retry(self):
        j, _ = journal()
        j.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
        row = j.record_terminal_failure(
            failure_code='provider-terminal-failure',
            terminal_evidence_digest='e' * 64,
            serialization_release_digest='f' * 64,
        )
        self.assertFalse(row['replayAuthorized'])
        self.assertEqual('terminal-failure', j.state().state)
        self.assertFalse(j.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1'))

    def test_admission_mutation_invalidates_identity(self):
        value = admission()
        value['rollbackPackageDigest'] = 'f' * 64
        with self.assertRaisesRegex(m.RecoveryAdmissionError, 'identity mismatch'):
            journal(adm=value)

    def test_original_intent_is_preserved_not_reclassified(self):
        original = original_intent()
        before = json.dumps(original, sort_keys=True)
        j, _ = journal()
        j.before_submit([], submission_kind='azure_onedeploy_windows_intent_v1')
        self.assertEqual(before, json.dumps(original, sort_keys=True))
        self.assertEqual('1' * 64, j.intent['replacementOfOperationId'])


if __name__ == '__main__':
    unittest.main()
