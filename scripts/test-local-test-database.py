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

    def test_local_environment_does_not_inherit_production_credentials(self):
        with patch.dict(os.environ, {'SQLCONNSTR_MasterAppDb': 'production', 'AZURE_CLIENT_SECRET': 'production',
                                     'SQUARE_ACCESS_TOKEN': 'production', 'GRAPH_CLIENT_SECRET': 'production'}):
            env = owner.local_environment({'password': 'synthetic'})
        self.assertNotIn('AZURE_CLIENT_SECRET', env)
        self.assertNotIn('SQUARE_ACCESS_TOKEN', env)
        self.assertNotIn('GRAPH_CLIENT_SECRET', env)
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
