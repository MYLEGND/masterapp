#!/usr/bin/env python3
"""Fail-closed regression coverage for fast-track already-ready no-SQL releases."""
import importlib.util
import hashlib
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('release_migration_no_sql', ROOT / 'scripts/release-migration.py')
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


class CurrentSchemaNoSqlTests(unittest.TestCase):
    def setUp(self):
        self.identity = 'a' * 64
        self.before = dict(schemaIdentity=self.identity, baselineIdentity='b' * 64,
                           databaseIdentity='c' * 64, ready=True, pendingCount=0)
        self.receipt = dict(self.before, kind='current-schema-no-write')
        self.records = []
        self.published = []
        self.journal = type('FakeJournal', (), {
            'intent': None,
            'record_success': lambda journal, value: self.records.append(value)
        })()

    def invoke(self):
        return m.reconcile_current_schema_no_sql(
            Path('/fake-probe'), 'fake-no-production-connection',
            observer=lambda probe, connection: self.before,
            readiness=lambda observed: self.receipt,
            journal_factory=lambda *args, **kwargs: self.journal,
            publisher=lambda receipt, digest: self.published.append((receipt, digest)))

    def test_complete_schema_records_explicit_nonwrite_proof(self):
        self.assertEqual('preserved', self.invoke())
        self.assertEqual([dict(schemaIdentity=self.identity, providerVersion='schema-ready-no-write')], self.records)
        self.assertEqual(1, len(self.published))
        self.assertEqual(hashlib.sha256(b'legend:schema-ready-no-sql:v1').hexdigest(),
                         self.published[0][1])

    def test_pending_schema_never_enters_journal_or_publishes(self):
        for candidate in (dict(self.before, pendingCount=1, ready=False),
                          dict(self.before, pendingCount=1, ready=True),
                          dict(self.before, pendingCount=True)):
            with self.subTest(candidate=candidate):
                self.before = candidate
                with self.assertRaisesRegex(RuntimeError, 'readiness-drift'):
                    self.invoke()
        self.assertFalse(self.records)
        self.assertFalse(self.published)

    def test_unproven_readiness_never_publishes(self):
        for bad in ('unknown', 'rehearsal-required'):
            with self.subTest(kind=bad):
                self.receipt=dict(self.before, kind=bad)
                with self.assertRaisesRegex(RuntimeError, 'readiness-missing'):
                    self.invoke()
        self.assertFalse(self.records)
        self.assertFalse(self.published)

    def test_physical_identity_mismatch_rejected_without_receipt(self):
        self.receipt=dict(self.before, kind='current-schema-no-write', databaseIdentity='d'*64)
        with self.assertRaisesRegex(RuntimeError, 'readiness-identity'):
            self.invoke()
        self.assertFalse(self.records)
        self.assertFalse(self.published)



class SchemaOnlyActivityFenceTests(unittest.TestCase):
    def test_no_sql_keeps_history_audit_and_double_observation(self):
        from types import SimpleNamespace
        from unittest.mock import Mock, patch
        from contextlib import nullcontext
        import os
        original=dict(databaseIdentity='a'*64, schemaIdentity='b'*64,
                      baselineIdentity='c'*64, ready=True, pendingCount=0,
                      mutationActivity='settled')
        owner=SimpleNamespace(
            evidence_lookup_budget=lambda *args, **kwargs:nullcontext(),
            assert_release_execution=lambda:dict(id=11,run_attempt=1))
        audit=SimpleNamespace(
            audit=lambda *args, **kwargs:dict(completeHistory=True,records=[]),
            BLOCKED_CODES=set(),STATUS_CODES={'OK'})
        with patch.dict(os.environ, dict(GITHUB_REPOSITORY='owner/repo',
                GITHUB_TOKEN='fixture', GITHUB_RUN_ATTEMPT='1',
                APPLICATION_RELEASE_SHA='f'*40)), \
             patch.object(m,'release_authority',return_value=owner), \
             patch.object(m,'history_audit',return_value=audit), \
             patch.object(m,'observe',side_effect=[dict(original),dict(original)]) as observe:
            result=m.production_observation('probe','connection',schema_only=True)
            self.assertTrue(result['ready'])
            self.assertEqual(2,observe.call_count)
            for call in observe.call_args_list:
                self.assertTrue(call.kwargs.get('schema_only_activity'))
                self.assertNotIn('activity',call.kwargs)
        with patch.dict(os.environ, dict(GITHUB_REPOSITORY='owner/repo',
                GITHUB_TOKEN='fixture', GITHUB_RUN_ATTEMPT='1',
                APPLICATION_RELEASE_SHA='f'*40)), \
             patch.object(m,'release_authority',return_value=owner), \
             patch.object(m,'history_audit',return_value=audit), \
             patch.object(m,'observe',side_effect=[dict(original),dict(original)]) as observe:
            m.production_observation('probe','connection')
            for call in observe.call_args_list:
                self.assertTrue(call.kwargs.get('activity'))
                self.assertNotIn('schema_only_activity',call.kwargs)

    def test_probe_keeps_exclusive_migration_and_schema_modification_lock_checks(self):
        source=(ROOT/'scripts/MigrationReleaseProbe/Program.cs').read_text()
        self.assertIn("APPLOCK_TEST('public','__EFMigrationsLock','Exclusive','Session')",source)
        self.assertIn('sys.dm_tran_locks AS l',source)
        self.assertIn("l.request_mode = 'Sch-M'",source)
        self.assertIn("l.request_status IN ('GRANT','WAIT','CONVERT')",source)
        self.assertIn('if (schemaOnly)',source)
        self.assertIn('sys.dm_exec_requests AS r',source)
        self.assertIn('if (inspectActivity || inspectSchemaOnly)',source)


class CanonicalWorkflowNoSqlGateTests(unittest.TestCase):
    def test_original_rehearsed_migration_and_no_sql_are_exclusive(self):
        workflow=(ROOT/'.github/workflows/legend-fast-track-ui.yml').read_text()
        self.assertIn('schema: ${{ steps.scope.outputs.schema }}',workflow)
        self.assertIn("stream.write('schema='+str(scope['schema']).lower()",workflow)
        self.assertIn("if: ${{ steps.migration-plan.outputs.mode == 'rehearsal-required' }}",workflow)
        self.assertIn("if: ${{ steps.migration-plan.outputs.mode == 'no-sql' }}",workflow)
        self.assertIn('run: python3 scripts/release-migration.py --no-sql',workflow)
        self.assertIn('run: python3 scripts/release-migration.py\n',workflow)
        self.assertIn('FAST_TRACK_RELEASE_BASELINE: ${{ needs.select-targets.outputs.baseline }}',workflow)

class CurrentStateMigrationPlanTests(unittest.TestCase):
    def setUp(self):
        from datetime import datetime, timezone
        import time
        self.now=int(time.time())
        self.before=dict(databaseIdentity='a'*64,schemaIdentity='b'*64,
            baselineIdentity='c'*64,ready=True,pendingCount=0)
        self.recovery=dict(databaseIdentity='a'*64,restoreWindowVerified=True,
            restoreExecuted=False,authorizesMutation=False,
            observedUtc=datetime.fromtimestamp(self.now,timezone.utc).isoformat())

    def plan(self, before=None, recovery=None, now=None):
        from unittest.mock import patch
        with patch.object(m,'production_readiness',side_effect=lambda value:dict(value,kind='current-schema-no-write')):
            return m.fast_track_migration_plan(before or self.before,recovery or self.recovery,
                now=lambda:self.now if now is None else now)

    def test_historical_source_schema_change_uses_current_ready_state(self):
        from unittest.mock import patch
        import os
        with patch.dict(os.environ,FAST_TRACK_SCHEMA_CHANGED='true'):
            self.assertEqual('no-sql',self.plan())

    def test_real_readiness_authority_requires_complete_historical_proof(self):
        import os
        from types import SimpleNamespace
        from unittest.mock import patch
        owner=SimpleNamespace(FAST_TRACK_PR_BASE='Legend/fast-track-release',
            selected_release_target_keys=lambda targets:None)
        observed=dict(self.before,historicalExecution='proven-nonentry',
            historicalEvidenceIdentity='e'*64,mutationActivity='settled')
        with patch.dict(os.environ,dict(GITHUB_REPOSITORY='owner/repo',
                RELEASE_CANDIDATE_SHA='a'*40,RELEASE_SHA='b'*40,
                SELECTED_TARGETS='["Website"]',GITHUB_TOKEN='fixture')),              patch.object(m,'release_authority',return_value=owner):
            self.assertEqual('no-sql',m.fast_track_migration_plan(observed,self.recovery,now=lambda:self.now))
            for change in (dict(historicalEvidenceIdentity='invalid'),
                           dict(historicalExecution='invalid'),dict(mutationActivity='unknown')):
                with self.subTest(change=change),self.assertRaises(RuntimeError):
                    m.fast_track_migration_plan(dict(observed,**change),self.recovery,now=lambda:self.now)

    def test_pending_requires_existing_exact_rehearsal_without_readiness_shortcut(self):
        from unittest.mock import patch
        with patch.object(m,'production_readiness',side_effect=AssertionError('must not accept pending')):
            self.assertEqual('rehearsal-required',m.fast_track_migration_plan(
                dict(self.before,ready=False,pendingCount=1),self.recovery,now=lambda:self.now))

    def test_inconsistent_pending_or_physical_identity_fails_closed(self):
        for value in (dict(self.before,pendingCount=1),dict(self.before,ready=False),
                      dict(self.before,pendingCount=True),dict(self.before,schemaIdentity='invalid')):
            with self.subTest(value=value),self.assertRaises(RuntimeError):
                self.plan(before=value)

    def test_stale_or_future_recovery_cannot_select_no_sql(self):
        for now in (self.now+901,self.now-1):
            with self.subTest(now=now),self.assertRaises(RuntimeError):
                self.plan(now=now)

    def test_recovery_database_identity_and_no_write_proof_must_match(self):
        for change in (dict(databaseIdentity='d'*64),dict(restoreWindowVerified=False),
                       dict(restoreExecuted=True),dict(authorizesMutation=True)):
            with self.subTest(change=change),self.assertRaises(RuntimeError):
                self.plan(recovery=dict(self.recovery,**change))

    def test_scoped_retained_observation_rejects_stale_or_mismatched_candidate(self):
        import tempfile
        env={key:'fixture' for key in ('GITHUB_RUN_ID','GITHUB_RUN_ATTEMPT','RELEASE_SHA',
             'APPLICATION_RELEASE_SHA','DATABASE_AUTHORITY','RELEASE_RESOURCE_GROUP')}
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'observation.json'
            m.retain_preflight_observation(path,self.before,environment=env,now=lambda:self.now)
            self.assertEqual(self.before,m.load_preflight_observation(path,environment=env,now=lambda:self.now))
            with self.assertRaises(RuntimeError):
                m.load_preflight_observation(path,environment=env,now=lambda:self.now+901)
            with self.assertRaises(RuntimeError):
                m.load_preflight_observation(path,environment=dict(env,APPLICATION_RELEASE_SHA='other'),now=lambda:self.now)

    def test_reobservation_drift_cannot_journal_or_publish(self):
        from unittest.mock import Mock,patch
        planned=dict(self.before,recoveryEvidence=self.recovery)
        for key in ('databaseIdentity','schemaIdentity','baselineIdentity'):
            journal=Mock()
            publisher=Mock()
            with self.subTest(key=key),patch.object(m,'production_readiness',side_effect=lambda value:dict(value,kind='current-schema-no-write')):
                with self.assertRaises(RuntimeError):
                    m.reconcile_current_schema_no_sql('probe','connection',
                        observer=lambda *args:dict(self.before,**{key:'d'*64}),
                        planned=planned,journal_factory=journal,publisher=publisher)
                journal.assert_not_called()
                publisher.assert_not_called()

    def test_workflow_observes_and_proves_recovery_before_rehearsal(self):
        workflow=(ROOT/'.github/workflows/legend-fast-track-ui.yml').read_text()
        self.assertLess(workflow.index('owner.production_recovery('),workflow.index('owner.production_observation_no_sql('))
        self.assertLess(workflow.index('owner.fast_track_migration_plan('),workflow.index('owner.restore_fast_track('))
        self.assertIn('owner.retain_preflight_observation(',workflow)


if __name__ == '__main__':
    unittest.main()
