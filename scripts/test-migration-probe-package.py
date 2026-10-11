#!/usr/bin/env python3
"""Exercise disposable SQL ownership without contacting Docker or production."""
import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('migration_probe_package', Path(__file__).with_name('migration-probe-package.py'))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


class IsolatedSqlTests(unittest.TestCase):
    def command(self, args, **kwargs):
        self.calls.append(args)
        if args[1] == 'inspect':
            return SimpleNamespace(returncode=0, stdout=json.dumps({'1433/tcp': [
                {'HostIp': self.host, 'HostPort': '15433'}]}))
        return SimpleNamespace(returncode=0, stdout='')

    def setUp(self):
        self.calls = []
        self.host = '127.0.0.1'

    def test_loopback_scope_and_disposal(self):
        with patch.object(m.subprocess, 'run', side_effect=self.command):
            with m.isolated_sql() as connection:
                self.assertTrue(connection.startswith('Server=127.0.0.1,15433;Database=LegendRehearsal_'))
                self.assertNotIn('rm', [c[1] for c in self.calls])
        self.assertEqual('rm', self.calls[-1][1])
        self.assertIn('127.0.0.1::1433', self.calls[0])
        self.assertIn(m.SQL_REHEARSAL_IMAGE, self.calls[0])

    def test_fixture_failure_still_disposes(self):
        with patch.object(m.subprocess, 'run', side_effect=self.command):
            with self.assertRaisesRegex(ValueError, 'fixture failure'):
                with m.isolated_sql():
                    raise ValueError('fixture failure')
        self.assertEqual('rm', self.calls[-1][1])

    def test_public_binding_rejected_and_disposed(self):
        self.host = '0.0.0.0'
        with patch.object(m.subprocess, 'run', side_effect=self.command):
            with self.assertRaisesRegex(RuntimeError, 'loopback binding unproven'):
                with m.isolated_sql():
                    self.fail('unsafe server accepted')
        self.assertEqual('rm', self.calls[-1][1])

    def test_failed_start_never_runs_fixture(self):
        with patch.object(m.subprocess, 'run', return_value=SimpleNamespace(returncode=1)) as run:
            with self.assertRaisesRegex(RuntimeError, 'provider command failed'):
                with m.isolated_sql():
                    self.fail('failed provider accepted')
        self.assertEqual(1, run.call_count)


if __name__ == '__main__':
    unittest.main()
