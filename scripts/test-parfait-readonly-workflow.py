#!/usr/bin/env python3
"""Execute the actual workflow source-proof gate with synthetic GitHub responses."""
import contextlib
import io
import json
import os
from pathlib import Path
import time
import unittest
import urllib.request
from unittest.mock import patch

text = (Path(__file__).resolve().parents[1] / '.github/workflows/parfait-cutover-live-readonly.yml').read_text()
source = text.split("python3 - <<'PYVALIDATE'\n", 1)[1].split('          PYVALIDATE', 1)[0]
source = '\n'.join(line[10:] for line in source.splitlines())


class ExactSourceProofTests(unittest.TestCase):
    def setUp(self):
        self.sha = 'a' * 40
        self.rows = [dict(id=i, head_sha=self.sha, head_repository=dict(full_name='owner/repo'),
            path='.github/workflows/' + path, event=event, status='completed', conclusion='success',
            pull_requests=[dict(base=dict(ref='Legend/fast-track-release'))]) for i, path, event in [
                (1, 'parfait-isolated-validation.yml', 'push'),
                (2, 'approved-release-security-validation.yml', 'pull_request')]]

    def execute(self):
        body = json.dumps(dict(workflow_runs=self.rows)).encode()
        with patch.dict(os.environ, GITHUB_REPOSITORY='owner/repo', GITHUB_SHA=self.sha, GITHUB_TOKEN='synthetic'), \
             patch.object(urllib.request, 'urlopen', return_value=io.BytesIO(body)), \
             patch.object(time, 'monotonic', side_effect=[0, 1801]), \
             contextlib.redirect_stdout(io.StringIO()):
            exec(compile(source, 'readonly-source-gate', 'exec'), {})

    def test_both_exact_candidate_producers_required(self):
        self.execute()

    def test_other_candidate_does_not_satisfy_gate(self):
        self.rows[0]['head_sha'] = 'b' * 40
        with self.assertRaisesRegex(SystemExit, 'NOT_READY'):
            self.execute()

    def test_failed_check_rejected(self):
        self.rows[1]['conclusion'] = 'failure'
        with self.assertRaisesRegex(SystemExit, 'VALIDATION_FAILED'):
            self.execute()

    def test_protected_branch_producer_rejected(self):
        self.rows[1]['pull_requests'][0]['base']['ref'] = 'legend/approved-changes'
        with self.assertRaisesRegex(SystemExit, 'SOURCE_UNPROVEN'):
            self.execute()

    def test_fork_producer_rejected(self):
        self.rows[0]['head_repository']['full_name'] = 'other/repo'
        with self.assertRaisesRegex(SystemExit, 'NOT_READY'):
            self.execute()


if __name__ == '__main__':
    unittest.main()
