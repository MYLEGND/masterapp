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
    def legacy(self, *, proven=False, marker=True, recognized=True, outcome='success'):
        run = dict(id=37039060321, head_sha='a'*40, conclusion='failure')
        step = dict(name=audit.LEGACY_STEP, status='completed',
                    conclusion=outcome)
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
            legacy_migration_step_recognized=lambda *args: recognized,
            _legacy_migration_noop=lambda *args: proven,
            _release_job_log=lambda *args: (
                '2026-10-02T17:16:09.9144337Z ' +
                'No candidate migration source changed from the proven database baseline.'
                if marker else 'No untrusted executable output'),
            _attested_legacy_ef_factory_nonentry=lambda *args: False,
            _attested_retired_unscheduled_release=lambda *args: False,
            _attested_retired_legacy_failure=lambda *args: False,
            LEGACY_NOOP_MARKER='No candidate migration source changed from the database baseline; migration receipt gate is not applicable.',
            LEGACY_OLDER_NOOP_MARKER='No candidate migration source changed from the proven database baseline.',
            DIRECT_RELEASE_WORKFLOW='all-intentional-direct-release-20260918.yml')
        return audit.classify_attempt(auth, 'MYLEGND/masterapp', 'stub', run, 1)

    def test_authenticated_original_noop_is_proven(self):
        self.assertEqual(self.legacy(proven=True)['code'], 'LEGACY_NOOP_PROVEN')

    def test_skipped_legacy_step_requires_same_source_recognition_as_admission(self):
        self.assertEqual(self.legacy(outcome='skipped')['code'],
                         'NO_MIGRATION_STEP_ENTERED')
        row = self.legacy(outcome='skipped', recognized=False)
        self.assertEqual(row['code'], 'MIGRATION_STEP_MISSING')
        self.assertIn(row['code'], audit.BLOCKED_CODES)

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
        # An entered EF bundle is NOT a pre-write proof failure, but it is
        # NEVER a no-op. Retain the positive SQL-reconciliation obligation;
        # the physical catalog and child journal are still checked separately.
        self.assertIn(code, audit.STATUS_CODES)
        self.assertNotIn(code, audit.BLOCKED_CODES)
        self.assertEqual(code, 'EF_MIGRATION_EXECUTION_ENTERED')

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

    def test_runtime_excludes_only_authenticated_current_attempt(self):
        run=dict(id=8,run_attempt=2,status='in_progress',head_sha='a'*40,head_branch='legend/approved-changes',
                 event='workflow_dispatch',path='.github/workflows/all-intentional-direct-release-20260918.yml')
        def get(repo,path,token):
            if path=='actions/runs/8/attempts/1':return dict(run,run_attempt=1,status='completed')
            return dict(workflow_runs=[run],total_count=1)
        auth=SimpleNamespace(TRUSTED_PR_BASE='legend/approved-changes',DIRECT_RELEASE_WORKFLOW='all-intentional-direct-release-20260918.yml',api_get=get)
        calls=[]
        def classify(auth,repo,token,attempt_run,attempt):
            calls.append((attempt,attempt_run['status']));return dict(run=8,attempt=attempt,code='MODERN_NOOP_PROVEN')
        with patch.object(audit,'classify_attempt',side_effect=classify):
            report=audit.audit(auth,'owner/repo','fixture',current_execution=run)
        self.assertEqual([(1,'completed')],calls)
        self.assertEqual(1,report['attempts'])



class ModernUncertainHistoryTests(unittest.TestCase):
    def classify(self, *, status='completed', source=True, complete=True, evidence=True):
        step=dict(name=audit.MODERN_STEP,status='completed',conclusion='failure')
        job=dict(id=22,name='release',status=status,conclusion='failure',steps=[step])
        auth=SimpleNamespace(_trusted_child_producer=lambda *args: None,
            api_get=lambda repo,path,token: (dict(total_count=1 if complete else 2,jobs=[job]) if '/jobs?' in path else
                dict(total_count=1,artifacts=[dict(id=44,name='legend-release-step-state-'+ 'a'*40 + '-11-1', expired=not evidence,workflow_run=dict(id=11))])),
            _release_attempt_package_revision=lambda *args,**kwargs: 'a'*40,
            release_attempt_never_entered=lambda *args: False,
            _attested_retired_unscheduled_release=lambda *args: False,
            _release_history_source=lambda *args: 'trusted original workflow',
            _job_blocks=lambda *args: {'release':'original'},
            named_step_blocks=lambda *args: {audit.MODERN_STEP:'python3 scripts/release-prepublication.py' if source else 'unknown'},
            _attested_migration_prewrite_failure=lambda *args: False,
            _historical_migration_prewrite_proven=lambda *args: False,
            DIRECT_RELEASE_WORKFLOW='all-intentional-direct-release-20260918.yml')
        with patch.object(audit,'authenticated_admission',return_value='f'*64):
            return audit.classify_attempt(auth,'owner/repo','fixture',dict(id=11,head_sha='a'*40,status=status,conclusion='failure',run_attempt=1),1)

    def test_unknown_terminal_execution_requires_runtime_not_false_nonentry(self):
        result=self.classify()
        self.assertEqual('HISTORICAL_EXECUTION_REQUIRES_RECONCILIATION',result['code'])
        self.assertEqual('unresolved',result['historicalAuthorization'])
        self.assertEqual('outcome-unknown',result['resolution']['historicalExecution'])
        self.assertFalse(result['resolution']['sqlExecutionAuthorized'])
        self.assertIsNotNone(result['sourceBlob'])

    def test_active_incomplete_or_unknown_entrypoint_remains_blocked(self):
        for args in [dict(status='in_progress'),dict(complete=False),dict(source=False),dict(evidence=False)]:
            with self.subTest(args=args):
                self.assertIn(self.classify(**args)['code'],audit.BLOCKED_CODES)


class UncertainEnvelopeTests(unittest.TestCase):
    def setUp(self):
        self.admission=patch.object(audit,'authenticated_admission',return_value='f'*64)
        self.admission.start();self.addCleanup(self.admission.stop)
        self.run=dict(id=11,run_attempt=1)
        self.state=dict(id=44,name='legend-release-step-state-'+ 'a'*40 + '-11-1',expired=False,workflow_run=dict(id=11))
        self.artifacts=[self.state]
        self.record=dict(child='migrations',producingAttempt=1,dependencyIdentity='d'*64)
        self.auth=SimpleNamespace(api_get=lambda *args:dict(total_count=len(self.artifacts),artifacts=self.artifacts),
            _release_attempt_package_revision=lambda *args,**kwargs:'a'*40,
            _release_history_json=lambda *args:self.record,
            _validate_child_generation=lambda *args,**kwargs:'d'*64)

    def check(self):
        return audit.authenticate_uncertain_attempt(self.auth,'owner/repo','fixture',self.run,1,dict(status='completed'))

    def test_missing_expired_duplicate_or_substituted_state_blocks(self):
        for artifacts in [[],[dict(self.state,expired=True)],[self.state,self.state],
                          [dict(self.state,workflow_run=dict(id=12))]]:
            with self.subTest(artifacts=artifacts):
                self.artifacts=artifacts
                with self.assertRaises(RuntimeError): self.check()

    def test_missing_or_invalid_admission_is_blocked(self):
        with patch.object(audit,'authenticated_admission',side_effect=RuntimeError('admission invalid')):
            with self.assertRaises(RuntimeError): self.check()

    def test_state_authentication_failure_never_becomes_unknown(self):
        with patch.object(self.auth,'_release_attempt_package_revision',side_effect=RuntimeError('mismatched steps')):
            with self.assertRaises(RuntimeError): self.check()

    def test_existing_migration_intent_is_preserved_not_interpreted_as_nonentry(self):
        self.artifacts.append(dict(id=45,name='legend-release-child-intent-'+'d'*64,expired=False,workflow_run=dict(id=11)))
        result=self.check()
        self.assertEqual([dict(artifactId=45,phase='intent',producingAttempt=1,dependencyIdentity='d'*64)],result['migrationRecords'])
        with patch.object(self.auth,'_validate_child_generation',side_effect=RuntimeError('tampered')):
            with self.assertRaises(RuntimeError): self.check()

    def test_unrelated_expired_artifact_does_not_invalidate_execution_evidence(self):
        self.artifacts.append(dict(id=46,name='unrelated-package',expired=True,workflow_run=dict(id=11)))
        self.assertEqual(44,self.check()['stateArtifactId'])


if __name__ == '__main__':
    unittest.main()
