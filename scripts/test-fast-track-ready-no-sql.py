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
        self.receipt=dict(self.before, databaseIdentity='d'*64)
        with self.assertRaisesRegex(RuntimeError, 'readiness-identity'):
            self.invoke()
        self.assertFalse(self.records)
        self.assertFalse(self.published)


class CanonicalWorkflowNoSqlGateTests(unittest.TestCase):
    def test_original_rehearsed_migration_and_no_sql_are_exclusive(self):
        workflow=(ROOT/'.github/workflows/legend-fast-track-ui.yml').read_text()
        self.assertIn('schema: ${{ steps.scope.outputs.schema }}',workflow)
        self.assertIn("stream.write('schema='+str(scope['schema']).lower()",workflow)
        self.assertIn("if: ${{ needs.select-targets.outputs.schema == 'true' }}",workflow)
        self.assertIn("if: ${{ needs.select-targets.outputs.schema == 'false' }}",workflow)
        self.assertIn('run: python3 scripts/release-migration.py --no-sql',workflow)
        self.assertIn('run: python3 scripts/release-migration.py\n',workflow)
        self.assertIn('FAST_TRACK_RELEASE_BASELINE: ${{ needs.select-targets.outputs.baseline }}',workflow)

if __name__ == '__main__':
    unittest.main()
