"""Release holds must persist, fail closed, and survive automatic dispatch."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
from release_policy import read_request, staging_only

ROOT = Path(__file__).resolve().parent


class ReleaseScopeSelection(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location('baseline_scope', ROOT / 'approved-release-baseline.py')
        self.baseline = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.baseline)

    def test_centralized_commerce_scope_is_reviewed_and_supported(self):
        selected = self.baseline.selected_targets({
            'releaseMode': 'approved-only',
            'targets': ['masterapp-protect', 'masterapp-parfait', 'masterapp-website']
        })
        self.assertEqual(
            [row[0] for row in selected],
            ['protect', 'parfait', 'website'])

    def test_unreviewed_scope_still_fails_closed(self):
        with self.assertRaises(ValueError):
            self.baseline.selected_targets({
                'releaseMode': 'approved-only',
                'targets': ['masterapp-client', 'masterapp-parfait']
            })


class ReleasePolicy(unittest.TestCase):
    def test_hold_survives_descendant_until_explicit_release(self):
        with tempfile.TemporaryDirectory() as directory:
            previous = Path.cwd()
            os.chdir(directory)
            try:
                def git(*args):
                    return subprocess.check_output(['git', *args], stderr=subprocess.DEVNULL, text=True).strip()
                git('init', '-b', 'approved')
                git('config', 'user.email', 'test@example.invalid')
                git('config', 'user.name', 'Test')
                path = Path('Docs/releases/direct-release-request.json')
                path.parent.mkdir(parents=True)
                path.write_text(json.dumps({'releaseMode': 'validate-only'}))
                git('add', '.')
                git('commit', '-m', 'hold')
                held = git('rev-parse', 'HEAD')
                Path('other').write_text('unrelated edit')
                git('add', '.')
                git('commit', '-m', 'descendant')
                self.assertTrue(staging_only('HEAD'))
                path.write_text(json.dumps({'releaseMode': 'approved-only'}))
                git('add', '.')
                git('commit', '-m', 'explicit release')
                self.assertFalse(staging_only('HEAD'))
                self.assertTrue(staging_only(held))
                with self.assertRaises(RuntimeError):
                    staging_only('missing-ref')
                path.write_text('{"releaseMode":"unknown"}')
                with self.assertRaises(ValueError):
                    read_request()
            finally:
                os.chdir(previous)

    def test_automatic_dispatch_cannot_discard_hold(self):
        spec = importlib.util.spec_from_file_location('baseline', ROOT / 'approved-release-baseline.py')
        baseline = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(baseline)
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'outputs'
            with patch('sys.argv', ['baseline', '--automatic', '--output', str(output)]), \
                 patch.object(baseline, 'read_request', return_value={'releaseMode': 'validate-only'}), \
                 patch.object(baseline, 'observe', side_effect=lambda target: dict(app=target[0], revision='a' * 40)), \
                 patch.object(baseline.subprocess, 'check_output', return_value='b' * 40), \
                 patch.object(baseline.subprocess, 'run'), patch.dict(os.environ, {'GITHUB_ACTIONS': 'false'}):
                baseline.main()
            self.assertIn('validate_only=true', output.read_text())


if __name__ == '__main__':
    unittest.main()
