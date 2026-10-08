#!/usr/bin/env python3
"""Fail-closed tests for the read-only whole-history migration audit."""
import importlib.util
import pathlib
import unittest
from types import SimpleNamespace
from unittest.mock import patch

ROOT = pathlib.Path(__file__).parent
spec = importlib.util.spec_from_file_location(
    'migration_history_audit', ROOT / 'release-migration-history-audit.py')
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class AuditTests(unittest.TestCase):
    def legacy(self, *, proven=False, marker=True):
        run = dict(id=37039060321, head_sha='a'*40, conclusion='failure')
        step = dict(name=audit.LEGACY_STEP, status='completed',
                    conclusion='success')
        job = dict(name='release', id=123, status='completed',
                   conclusion='failure', steps=[step])
        def get(repo, path, token):
            if '/jobs?' in path:
                return {'total_count': 1, 'jobs': [job]}
            raise AssertionError('unexpected remote read')
        auth = SimpleNamespace(
            _trusted_child_producer=lambda repo, run: None,
            api_get=get,
            release_attempt_never_entered=lambda jobs: False,
            _release_history_source=lambda *args: 'immutable source',
            _legacy_migration_noop=lambda *args: proven,
            _release_job_log=lambda *args: (
                '2026-10-02T17:16:09.9144337Z ' +
                'No candidate migration source changed from the proven database baseline.'
                if marker else 'No untrusted executable output'),
            _attested_legacy_ef_factory_nonentry=lambda *args: False,
            LEGACY_NOOP_MARKER='No candidate migration source changed from the database baseline; migration receipt gate is not applicable.',
            LEGACY_OLDER_NOOP_MARKER='No candidate migration source changed from the proven database baseline.',
            DIRECT_RELEASE_WORKFLOW='all-intentional-direct-release-20260918.yml')
        return audit.classify_attempt(auth, 'MYLEGND/masterapp', 'stub', run, 1)

    def test_authenticated_original_noop_is_proven(self):
        self.assertEqual(self.legacy(proven=True)['code'], 'LEGACY_NOOP_PROVEN')

    def test_original_marker_without_authentication_is_blocked(self):
        result = self.legacy(proven=False)
        self.assertEqual(result['code'], 'LEGACY_NOOP_PROOF_REJECTED')
        self.assertIn(result['code'], audit.BLOCKED_CODES)
        self.assertEqual(result['run'], 37039060321)

    def test_no_marker_does_not_fabricate_no_write(self):
        self.assertEqual(self.legacy(marker=False)['code'],
                         'POSSIBLE_SQL_WRITE_REQUIRES_LIVE_FENCE')

    def test_actual_ef_entry_is_never_claimed_no_write(self):
        row = {}
        auth = SimpleNamespace(_release_job_log=lambda *args:
            "2026-10-01T13:20:53.6483219Z Applying migration "
            "'20261001070000_AddLegendEngineeringControlPlane'.")
        code = audit.legacy_failure_reason(
            auth, 'MYLEGND/masterapp', {'id': 99}, 'stub', row)
        self.assertEqual(code, 'EF_MIGRATION_EXECUTION_ENTERED')
        self.assertEqual(row['attemptedMigrationIds'],
                         ['20261001070000_AddLegendEngineeringControlPlane'])
        self.assertIn(code, audit.BLOCKED_CODES)

    def test_model_drift_is_not_misclassified_as_sql_execution(self):
        row = {}
        auth = SimpleNamespace(_release_job_log=lambda *args:
            "2026-09-25T19:01:57.9698892Z System.InvalidOperationException: "
            "Microsoft.EntityFrameworkCore.Migrations.PendingModelChangesWarning")
        code = audit.legacy_failure_reason(
            auth, 'MYLEGND/masterapp', {'id': 90}, 'stub', row)
        self.assertEqual(code, 'EF_PENDING_MODEL_CHANGE')
        self.assertNotIn('attemptedMigrationIds', row)

    def test_incomplete_history_fails_not_skips(self):
        auth = SimpleNamespace(
            TRUSTED_PR_BASE='legend/approved-changes',
            DIRECT_RELEASE_WORKFLOW='all-intentional-direct-release-20260918.yml',
            api_get=lambda *args: {'workflow_runs': [], 'total_count': 1})
        with self.assertRaisesRegex(RuntimeError, 'RELEASE_HISTORY_TRUNCATED'):
            audit.audit(auth, 'MYLEGND/masterapp', 'stub')

    def test_all_attempts_are_reported_not_first_only(self):
        runs = [
            dict(id=2, run_attempt=1, head_branch='legend/approved-changes',
                 event='workflow_dispatch',
                 path='.github/workflows/all-intentional-direct-release-20260918.yml'),
            dict(id=1, run_attempt=1, head_branch='legend/approved-changes',
                 event='workflow_dispatch',
                 path='.github/workflows/all-intentional-direct-release-20260918.yml'),
        ]
        auth = SimpleNamespace(
            TRUSTED_PR_BASE='legend/approved-changes',
            DIRECT_RELEASE_WORKFLOW='all-intentional-direct-release-20260918.yml',
            api_get=lambda *args: {'workflow_runs': runs, 'total_count': 2})
        def classify(*args):
            run, attempt = args[3], args[4]
            return {'run': run['id'], 'attempt': attempt,
                    'code': 'LEGACY_NOOP_PROOF_REJECTED'}
        with patch.object(audit, 'classify_attempt', side_effect=classify):
            report = audit.audit(auth, 'MYLEGND/masterapp', 'stub')
        self.assertEqual(report['attempts'], 2)
        self.assertEqual([r['run'] for r in report['records']], [2, 1])


if __name__ == '__main__':
    unittest.main()
