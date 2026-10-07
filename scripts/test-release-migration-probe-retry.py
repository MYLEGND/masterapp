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
        for marker in ('LEGEND_SCHEMA_PROBE:SQL_AUTH', 'LEGEND_SCHEMA_PROBE:SCHEMA_DRIFT', 'LEGEND_SCHEMA_PROBE:UNCLASSIFIED'):
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

if __name__ == '__main__':
    main()
