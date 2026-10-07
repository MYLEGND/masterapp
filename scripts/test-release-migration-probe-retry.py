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

    def test_exact_readonly_unknown_id_metadata_is_redacted_and_rejected(self):
        payload = ('LEGEND_SCHEMA_PROBE:UNKNOWN_APPLIED_MIGRATION\\n'
                   'LEGEND_SCHEMA_HISTORY:2:20260213015339_FinanceToolStates_ByClientProfile,'
                   '20260927053000_AddAdvertisingActionAuthorizations\\n')
        failure = subprocess.CompletedProcess([], 1, '', payload)
        with mock.patch.object(m.subprocess, 'run', return_value=failure) as run, \
             mock.patch('builtins.print') as notice:
            with self.assertRaisesRegex(RuntimeError, 'Database contains applied migration history absent'):
                m.observe('probe', 'conn')
        run.assert_called_once()
        notice.assert_called_once_with(
            'LEGEND_SCHEMA_HISTORY:2:20260213015339_FinanceToolStates_ByClientProfile,'
            '20260927053000_AddAdvertisingActionAuthorizations', flush=True)

    def test_unknown_provider_message_never_reaches_schema_log(self):
        failure = subprocess.CompletedProcess([], 1, '',
            'LEGEND_SCHEMA_PROBE:UNKNOWN_APPLIED_MIGRATION\\n'
            'LEGEND_SCHEMA_HISTORY:1:Server=private;Password=private')
        with mock.patch.object(m.subprocess, 'run', return_value=failure) as run, \
             mock.patch('builtins.print') as notice:
            with self.assertRaisesRegex(RuntimeError, 'unclassified failure'):
                m.observe('probe', 'conn')
        run.assert_called_once()
        notice.assert_not_called()

    def test_unknown_applied_migration_inventory_is_readonly_and_bounded(self):
        root = Path(__file__).resolve().parents[1]
        source = (root / 'scripts/MigrationReleaseProbe/Program.cs').read_text()
        self.assertIn('unregistered.Take(16)', source)
        self.assertIn('Math.Min(unregistered.Length, 9999)', source)
        self.assertIn('LEGEND_SCHEMA_HISTORY:', source)
        self.assertIn('throw new ProbeObservationFailure("UNKNOWN_APPLIED_MIGRATION"', source)

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
    def test_frozen_historical_sources_remain_unchanged_and_canonical(self):
        root = Path(__file__).resolve().parents[1]
        # Frozen hand-authored migration sources must not be modified merely
        # to suppress legitimate historic __EFMigrationsHistory evidence.
        identifiers = (
            '20260321020000_AddAgentAssistants',
            '20260329093000_ExecutionMvp',
            '20260330094500_RepairAgentProfilesSqlite',
        )
        manual = (root / 'scripts/db-legacy-manual-migrations.txt').read_text()
        for identifier in identifiers:
            self.assertIn(identifier + '.cs', manual)
            source = (root / 'Infrastructure/Migrations' / (identifier + '.cs')).read_text()
            self.assertIn('[Migration("' + identifier + '")]', source)
            self.assertNotIn('[DbContext(typeof(MasterAppDbContext))]', source)

    def test_only_production_audited_legacy_stamps_have_readonly_compatibility(self):
        root = Path(__file__).resolve().parents[1]
        probe = (root / 'scripts/MigrationReleaseProbe/Program.cs').read_text()
        audit = (root / 'Infrastructure/MigrationAudit/production-migrations-current.txt').read_text()
        allowed = (
            '20260213015339_FinanceToolStates_ByClientProfile',
            '20260321020000_AddAgentAssistants',
            '20260329093000_ExecutionMvp',
            '20260330094500_RepairAgentProfilesSqlite',
        )
        for identifier in allowed:
            self.assertIn(identifier, audit.splitlines())
            self.assertIn('["' + identifier + '"]', probe)
        self.assertIn('unknown.Any(id => !auditedLegacy.ContainsKey(id))', probe)
        self.assertIn('!beforeKnown || !afterKnown || !beforeApplied || !afterApplied', probe)
        self.assertIn('appliedRegistered.SequenceEqual(known.Take(appliedRegistered.Length)', probe)
        self.assertIn('throw new ProbeObservationFailure("UNKNOWN_APPLIED_MIGRATION")', probe)
        self.assertIn('throw new ProbeObservationFailure("HISTORY_SEQUENCE_DRIFT")', probe)
        # No new executable migration is fabricated for a historical-only row.
        self.assertFalse((root / 'Infrastructure/Migrations' /
                          (allowed[0] + '.cs')).exists())
        # The unregistered advertising migration has no historical production
        # evidence and MUST NOT silently join the trusted set.
        self.assertNotIn('["20260927053000_AddAdvertisingActionAuthorizations"]', probe)

if __name__ == '__main__':
    main()
