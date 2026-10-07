#!/usr/bin/env python3
"""Bounded, non-mutating schema observation regression tests."""
import importlib.util
from pathlib import Path
from unittest import TestCase, main, mock
import subprocess

spec = importlib.util.spec_from_file_location('release_migration', Path(__file__).with_name('release-migration.py'))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)

class ProbeObservationTests(TestCase):
    def test_transient_read_twice_then_success(self):
        good = '{"schemaVersion":1,"ready":true,"knownCount":1,"appliedCount":1,"pendingCount":0,"schemaIdentity":"' + 'a'*64 + '"}'
        bad = subprocess.CompletedProcess([], 1, '', 'LEGEND_SCHEMA_PROBE:TRANSIENT_SQL_READ\n')
        ok = subprocess.CompletedProcess([], 0, good, '')
        with mock.patch.object(m.subprocess, 'run', side_effect=[bad,bad,ok]) as run, mock.patch.object(m.time, 'sleep') as sleep:
            self.assertTrue(m.observe('probe', 'conn')['ready'])
            self.assertEqual(run.call_count, 3)
            self.assertEqual(sleep.call_count, 2)

    def test_transient_retry_exhaustion_stops(self):
        bad = subprocess.CompletedProcess([], 1, '', 'LEGEND_SCHEMA_PROBE:TRANSIENT_SQL_READ\n')
        with mock.patch.object(m.subprocess, 'run', return_value=bad) as run, mock.patch.object(m.time, 'sleep'):
            with self.assertRaisesRegex(RuntimeError, 'exhausted bounded retries'):
                m.observe('probe', 'conn')
            self.assertEqual(run.call_count, 3)

    def test_auth_and_drift_never_retry(self):
        for marker in (
            'LEGEND_SCHEMA_PROBE:SQL_AUTH',
            'LEGEND_SCHEMA_PROBE:SCHEMA_DRIFT',
            'LEGEND_SCHEMA_PROBE:INPUT_UNAVAILABLE',
            'LEGEND_SCHEMA_PROBE:MIGRATIONS_MISSING',
            'LEGEND_SCHEMA_PROBE:UNKNOWN_APPLIED_MIGRATION',
            'LEGEND_SCHEMA_PROBE:HISTORY_SEQUENCE_DRIFT',
            'LEGEND_SCHEMA_PROBE:RUNTIME_INVALID_OPERATION',
            'LEGEND_SCHEMA_PROBE:UNCLASSIFIED',
        ):
            with self.subTest(marker=marker):
                bad = subprocess.CompletedProcess([], 1, '', marker)
                with mock.patch.object(m.subprocess, 'run', return_value=bad) as run, mock.patch.object(m.time, 'sleep') as sleep:
                    with self.assertRaises(RuntimeError):
                        m.observe('probe', 'conn')
                    self.assertEqual(run.call_count, 1)
                    sleep.assert_not_called()

    def test_timeout_is_not_blindly_retried(self):
        with mock.patch.object(m.subprocess, 'run', side_effect=subprocess.TimeoutExpired('dotnet', 60)) as run:
            with self.assertRaisesRegex(RuntimeError, 'deadline exceeded'):
                m.observe('probe', 'conn')
            self.assertEqual(run.call_count, 1)


    def test_permanent_history_failure_preserves_exact_safe_classification(self):
        observed = {
            'MIGRATIONS_MISSING': 'Validated probe reports no known migrations',
            'UNKNOWN_APPLIED_MIGRATION': 'Database contains applied migration history absent from validated bundle',
            'HISTORY_SEQUENCE_DRIFT': 'Applied database migration history is not in validated sequence',
            'RUNTIME_INVALID_OPERATION': 'Schema probe runtime invalid operation; history status unknown',
        }
        for marker, reason in observed.items():
            with self.subTest(marker=marker):
                result = subprocess.CompletedProcess([], 1, '', 'LEGEND_SCHEMA_PROBE:' + marker)
                with mock.patch.object(m.subprocess, 'run', return_value=result):
                    with self.assertRaisesRegex(RuntimeError, reason):
                        m.observe('probe', 'conn')



class MigrationAssemblyHistoryContractTests(TestCase):
    def test_four_existing_migrations_have_exact_ef_discovery_identity(self):
        root = Path(__file__).resolve().parents[1]
        for filename, identity in (
            ('20260321020000_AddAgentAssistants.cs', '20260321020000_AddAgentAssistants'),
            ('20260329093000_ExecutionMvp.cs', '20260329093000_ExecutionMvp'),
            ('20260330094500_RepairAgentProfilesSqlite.cs', '20260330094500_RepairAgentProfilesSqlite'),
            ('20260927053000_AddAdvertisingActionAuthorizations.cs',
             '20260927053000_AddAdvertisingActionAuthorizations'),
        ):
            with self.subTest(filename=filename):
                source = (root / 'Infrastructure' / 'Migrations' / filename).read_text()
                self.assertIn('[DbContext(typeof(MasterAppDbContext))]', source)
                self.assertIn('[Migration("' + identity + '")]', source)

    def test_only_immutably_audited_production_history_may_be_legacy_applied(self):
        root = Path(__file__).resolve().parents[1]
        probe = (root / 'scripts/MigrationReleaseProbe/Program.cs').read_text()
        audit = (root / 'Infrastructure/MigrationAudit/production-migrations-current.txt').read_text()
        legacy = '20260213015339_FinanceToolStates_ByClientProfile'
        self.assertIn(legacy, audit.splitlines())
        self.assertIn('const string auditedLegacy = "' + legacy + '";', probe)
        self.assertIn('unknown.Length == 1', probe)
        self.assertIn('!legacyApplied', probe)
        self.assertIn('appliedRegistered.SequenceEqual(known.Take(appliedRegistered.Length)', probe)
        self.assertIn('throw new ProbeObservationFailure("UNKNOWN_APPLIED_MIGRATION")', probe)
        self.assertIn('throw new ProbeObservationFailure("HISTORY_SEQUENCE_DRIFT")', probe)
        # No fabricated executable migration or raw production IDs are introduced.
        self.assertFalse((root / 'Infrastructure/Migrations' / (legacy + '.cs')).exists())


if __name__ == '__main__':
    main()
