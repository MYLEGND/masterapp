import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('local_sql', Path(__file__).with_name('local-test-database.py'))
owner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(owner)


class LocalDatabaseTests(unittest.TestCase):
    def test_missing_configuration_never_initializes_on_run(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(owner, 'STATE', Path(directory)), \
             patch.object(owner, 'canonical'), patch.object(owner, 'initialize') as init, \
             patch.object(owner.sys, 'argv', ['local-test-database.py', 'run', 'portal']):
            with self.assertRaisesRegex(RuntimeError, 'not been initialized'):
                owner.main()
            init.assert_not_called()

    def test_local_tests_require_explicit_scope_before_database_access(self):
        with patch.object(owner.sys, 'argv', ['local-test-database.py', 'test']), patch.object(owner, 'config') as config:
            with self.assertRaisesRegex(RuntimeError, 'explicit focused --filter'):
                owner.main()
            config.assert_not_called()

    def test_focused_migration_validation_preserves_existing_database_owner(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(owner, 'canonical'), \
             patch.object(owner, 'config', return_value={'password': 'synthetic', 'identity': 'synthetic-identity'}), \
             patch.object(owner, 'start'), patch.object(owner, 'verify', return_value={}), \
             patch.object(owner.subprocess, 'run') as run, \
             patch.object(owner.sys, 'argv', ['local-test-database.py', 'migrate', '--filter', 'FullyQualifiedName~Assistant', '--artifacts-root', directory, '--reviewed-migration-sha256', 'a' * 64]):
            run.return_value.returncode = 0
            with self.assertRaises(SystemExit) as result:
                owner.main()
            self.assertEqual(0, result.exception.code)
            self.assertEqual('apply', run.call_args.args[0][-1])
            self.assertEqual('a' * 64, run.call_args.kwargs['env']['MASTERAPP_REVIEWED_LOCAL_MIGRATION_SHA256'])
            env = run.call_args.kwargs['env']
            self.assertEqual('FullyQualifiedName~Assistant', env['MASTERAPP_DB_TEST_FILTER'])
            self.assertEqual(directory, env['MasterAppArtifactsRoot'])
            self.assertEqual('synthetic-identity', env['LEGEND_TEST_DB_IDENTITY'])
            self.assertIn('Initial Catalog=MasterAppLocalTest', env['MASTERAPP_LOCAL_DB_CONNECTION'])

    def test_invalid_migration_review_digest_stops_before_database_access(self):
        with patch.object(owner.sys, 'argv', ['local-test-database.py', 'migrate', '--reviewed-migration-sha256', 'stale-short-sha']), patch.object(owner, 'config') as config:
            with self.assertRaisesRegex(RuntimeError, 'full reviewed migration SHA-256'):
                owner.main()
            config.assert_not_called()

    def test_local_environment_does_not_inherit_production_credentials(self):
        with patch.dict(os.environ, {'SQLCONNSTR_MasterAppDb': 'production', 'AZURE_CLIENT_SECRET': 'production',
                                     'SQUARE_ACCESS_TOKEN': 'production', 'GRAPH_CLIENT_SECRET': 'production',
                                     'MASTERAPP_DB_TEST_FILTER': 'inherited-filter', 'LEGEND_LOCAL_MIGRATION_VALIDATION': '1', 'MASTERAPP_REVIEWED_LOCAL_MIGRATION_SHA256': 'inherited'}):
            env = owner.local_environment({'password': 'synthetic'})
        self.assertNotIn('AZURE_CLIENT_SECRET', env)
        self.assertNotIn('SQUARE_ACCESS_TOKEN', env)
        self.assertNotIn('GRAPH_CLIENT_SECRET', env)
        self.assertNotIn('MASTERAPP_DB_TEST_FILTER', env)
        self.assertNotIn('LEGEND_LOCAL_MIGRATION_VALIDATION', env)
        self.assertNotIn('MASTERAPP_REVIEWED_LOCAL_MIGRATION_SHA256', env)
        self.assertIn('127.0.0.1,14339', env['SQLCONNSTR_MasterAppDb'])
        self.assertIn('Initial Catalog=MasterAppLocalTest', env['SQLCONNSTR_MasterAppDb'])
        self.assertEqual(env['ConnectionStrings__MasterAppDb'], env['SQLCONNSTR_MasterAppDb'])

    def test_missing_database_identity_never_resets_or_falls_back(self):
        with patch.object(owner, 'inspect', return_value={'State': {'Running': True}}), \
             patch.object(owner, 'sql', return_value='wrong') as sql, patch.object(owner, 'initialize') as init:
            with self.assertRaisesRegex(RuntimeError, 'identity is missing or changed'):
                owner.verify({'identity': 'expected'})
            init.assert_not_called()
            self.assertIn('SELECT', sql.call_args.args[1])
            self.assertNotIn('CREATE', sql.call_args.args[1])


if __name__ == '__main__':
    unittest.main()
