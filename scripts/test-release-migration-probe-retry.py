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
        payload = ('LEGEND_SCHEMA_PROBE:UNKNOWN_APPLIED_MIGRATION\n'
                   'LEGEND_SCHEMA_HISTORY:2:20260213015339_FinanceToolStates_ByClientProfile,'
                   '20260927053000_AddAdvertisingActionAuthorizations\n')
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
            'LEGEND_SCHEMA_PROBE:UNKNOWN_APPLIED_MIGRATION\n'
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

    def test_live_ef_prefix_fence_is_readonly_and_fail_closed(self):
        import json
        base = dict(schemaVersion=1, ready=False, knownCount=219,
                    appliedCount=218, pendingCount=1,
                    schemaIdentity='a' * 64,
                    lastAppliedMigrationId='20261003091500_CanonicalizeBusinessFinanceToolState',
                    firstPendingMigrationId='20261007134500_AddFounderAssistantRules')
        def observed(value):
            done = subprocess.CompletedProcess([], 0, json.dumps(value), '')
            with mock.patch.object(m.subprocess, 'run', return_value=done):
                return m.observe('probe', 'opaque')
        self.assertEqual(base, observed(base))
        for corrupt in (
            dict(base, firstPendingMigrationId='20260927090000_AddOpenAiProductFeedProjections'),
            dict(base, firstPendingMigrationId='Server=private;Password=private'),
            dict(base, firstPendingMigrationId=None),
            dict(base, lastAppliedMigrationId=None),
            {k: v for k, v in base.items() if k != 'lastAppliedMigrationId'},
            dict(base, ready=True),
        ):
            with self.subTest(corrupt=corrupt):
                with self.assertRaisesRegex(RuntimeError, 'Invalid read-only schema proof'):
                    observed(corrupt)
        source = (Path(__file__).with_name('MigrationReleaseProbe') / 'Program.cs').read_text()
        self.assertIn('lastAppliedMigrationId = appliedRegistered.LastOrDefault()', source)
        self.assertIn('firstPendingMigrationId = pendingIds.FirstOrDefault()', source)

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
        self.assertIn('unregistered = unknown.Where(id => !auditedLegacy.ContainsKey(id)).ToArray()', probe)
        self.assertIn('if (unregistered.Length > 0)', probe)
        self.assertIn('!beforeKnown || !afterKnown || !beforeApplied || !afterApplied', probe)
        self.assertIn('appliedRegistered.SequenceEqual(known.Take(appliedRegistered.Length)', probe)
        self.assertIn('throw new ProbeObservationFailure("UNKNOWN_APPLIED_MIGRATION",', probe)
        self.assertIn('safeIds, Math.Min(unregistered.Length, 9999)', probe)
        self.assertIn('throw new ProbeObservationFailure("HISTORY_SEQUENCE_DRIFT")', probe)
        # No new executable migration is fabricated for a historical-only row.
        self.assertFalse((root / 'Infrastructure/Migrations' /
                          (allowed[0] + '.cs')).exists())
        # The unregistered advertising migration has no historical production
        # evidence and MUST NOT silently join the trusted set.
        self.assertNotIn('["20260927053000_AddAdvertisingActionAuthorizations"]', probe)


class MigrationInventoryObservationTests(TestCase):
    def evidence(self):
        import datetime, hashlib
        ids = [f'20260101{i:06d}_Migration' for i in range(219)]
        known, applied = ids, ids[:-1]
        return dict(schemaVersion=1, purpose='read-only-migration-inventory',
            authorizesMutation=False, observedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
            migrationHistory=[dict(migrationId=x, productVersion='10.0.0') for x in applied],
            appliedMigrationIds=applied, recognizedMigrationIds=known,
            pendingMigrationIds=ids[-1:], unrecognizedAppliedMigrationIds=[],
            schemaIdentity=hashlib.sha256(chr(10).join(known).encode()).hexdigest())

    def execute_inventory(self, value, stderr='', exitcode=0):
        import contextlib, io, json, os, tempfile, textwrap, types
        root = Path(__file__).resolve().parents[1]
        workflow = (root / '.github/workflows/deployment-diagnostics.yml').read_text()
        source = textwrap.dedent(workflow.split("python3 - <<'PYINVENTORY'", 1)[1].split('          PYINVENTORY', 1)[0])
        with tempfile.TemporaryDirectory() as tmp:
            owner = types.SimpleNamespace(connection_string=lambda: 'opaque-test-connection')
            loader = types.SimpleNamespace(exec_module=lambda module: None)
            spec = types.SimpleNamespace(loader=loader)
            result = subprocess.CompletedProcess([], exitcode, json.dumps(value), stderr)
            with mock.patch('importlib.util.spec_from_file_location', return_value=spec), mock.patch('importlib.util.module_from_spec', return_value=owner), mock.patch('subprocess.run', return_value=result) as run, mock.patch.dict(os.environ, dict(RUNNER_TEMP=tmp, TOOL_REVISION='a'*40, APPLICATION_REVISION='b'*40, GITHUB_RUN_ID='1', GITHUB_RUN_ATTEMPT='1')), contextlib.redirect_stdout(io.StringIO()) as out:
                exec(compile(source, '<inventory-workflow>', 'exec'), {})
            run.assert_called_once()
            self.assertEqual(run.call_args.args[0][-1], '--inventory')
            saved=json.loads((Path(tmp) / 'migration-inventory.json').read_text())
            self.assertNotIn('opaque-test-connection', json.dumps(saved) + out.getvalue())
            return saved

    def test_all_218_applied_ids_are_preserved_without_authorizing_mutation(self):
        value = self.evidence()
        saved = self.execute_inventory(value)
        self.assertEqual(saved['appliedMigrationIds'], value['appliedMigrationIds'])
        self.assertEqual(len(saved['migrationHistory']), 218)
        self.assertFalse(saved['authorizesMutation'])
        self.assertNotIn('ready', saved)

    def test_unknown_history_is_reported_without_becoming_release_authority(self):
        value = self.evidence()
        unknown = '20270101000000_Unknown'
        value['appliedMigrationIds'].append(unknown)
        value['migrationHistory'].append(dict(migrationId=unknown, productVersion='10.0.0'))
        value['unrecognizedAppliedMigrationIds'] = [unknown]
        saved = self.execute_inventory(value)
        self.assertEqual(saved['unrecognizedAppliedMigrationIds'], [unknown])
        self.assertFalse(saved['authorizesMutation'])

    def test_incomplete_conflicting_stale_or_sensitive_inventory_is_rejected(self):
        import copy
        baseline = self.evidence()
        edits = [
            dict(pendingMigrationIds=[]), dict(schemaIdentity='0'*64),
            dict(appliedMigrationIds=baseline['appliedMigrationIds']*2),
            dict(migrationHistory=baseline['migrationHistory'][:-1]),
            dict(observedUtc='2000-01-01T00:00:00+00:00'),
            dict(authorizesMutation=True), dict(secret='not-allowed'),
            dict(unrecognizedAppliedMigrationIds=['Password=private']),
            dict(migrationHistory=[dict(migrationId=x, productVersion='Password=private') for x in baseline['appliedMigrationIds']]),
        ]
        for edit in edits:
            with self.subTest(edit=list(edit)):
                value = copy.deepcopy(baseline)
                value.update(edit)
                with self.assertRaisesRegex(SystemExit, '^Complete read-only migration inventory unavailable; no mutation authorized.$'):
                    self.execute_inventory(value)
        for stderr, code in [('private provider failure', 0), ('', 1)]:
            with self.assertRaisesRegex(SystemExit, '^Complete read-only migration inventory unavailable; no mutation authorized.$'):
                self.execute_inventory(baseline, stderr=stderr, exitcode=code)

if __name__ == '__main__':
    main()
