#!/usr/bin/env python3
"""Execute the trusted workflow gate with synthetic exact-candidate evidence."""
import contextlib
import importlib.util
import io
import os
from pathlib import Path
from types import SimpleNamespace
import time
import unittest
from unittest.mock import Mock, patch

text=(Path(__file__).resolve().parents[1]/'.github/workflows/parfait-cutover-live-readonly.yml').read_text()
source=text.split("python3 - <<'PYVALIDATE'\n",1)[1].split('          PYVALIDATE',1)[0]
source='\n'.join(line[10:] for line in source.splitlines())

class ExactSourceProofTests(unittest.TestCase):
    def setUp(self):
        self.sha='a'*40
        self.pull=dict(number=626,head=dict(sha=self.sha,ref='isolation/candidate',repo=dict(full_name='owner/repo')),
            base=dict(ref='Legend/fast-track-release',repo=dict(full_name='owner/repo')))
        self.rows=[dict(id=i,run_attempt=1,head_sha=self.sha,head_branch='isolation/candidate',
            head_repository=dict(full_name='owner/repo'),path='.github/workflows/'+path,
            event='pull_request',status='completed',conclusion='success') for i,path in enumerate((
            'parfait-isolated-validation.yml','approved-release-security-validation.yml',
            'masterapp-platform-architecture-validation.yml'),1)]
        self.owner=SimpleNamespace(FAST_TRACK_PR_BASE='Legend/fast-track-release',
            api_get=Mock(side_effect=lambda repo,path,token: [self.pull] if '/pulls?' in path
                else dict(total_count=len(self.rows),workflow_runs=self.rows)),
            fast_track_validation_receipt=Mock(return_value=41))

    def execute(self):
        with patch.dict(os.environ,dict(GITHUB_REPOSITORY='owner/repo',CANDIDATE_SHA=self.sha,
                GITHUB_SHA='b'*40,GITHUB_TOKEN='synthetic'),clear=True), \
             patch.object(importlib.util,'spec_from_file_location',return_value=Mock()), \
             patch.object(importlib.util,'module_from_spec',return_value=self.owner), \
             patch.object(time,'monotonic',side_effect=[0,1801]), \
             contextlib.redirect_stdout(io.StringIO()):
            exec(compile(source,'readonly-source-gate','exec'),{})

    def test_three_exact_candidate_producers_and_canonical_receipts_required(self):
        self.execute()
        self.assertEqual(3,self.owner.fast_track_validation_receipt.call_count)
        self.assertEqual({c.args[3] for c in self.owner.fast_track_validation_receipt.call_args_list},{self.sha})
        self.assertEqual({c.args[4] for c in self.owner.fast_track_validation_receipt.call_args_list},{626})

    def test_other_candidate_fork_push_or_branch_does_not_satisfy_gate(self):
        for field,value in [('head_sha','c'*40),('event','push'),('head_branch','other'),
                            ('head_repository',dict(full_name='other/repo'))]:
            original=self.rows[0][field];self.rows[0][field]=value
            with self.subTest(field=field),self.assertRaisesRegex(SystemExit,'NOT_READY'):self.execute()
            self.rows[0][field]=original

    def test_latest_failed_check_cannot_be_masked_by_old_success(self):
        self.rows.append(dict(self.rows[0],id=100,conclusion='failure'))
        with self.assertRaisesRegex(SystemExit,'VALIDATION_FAILED'):self.execute()

    def test_protected_or_foreign_pr_rejected(self):
        self.pull['base']['ref']='legend/approved-changes'
        with self.assertRaisesRegex(SystemExit,'SOURCE_UNPROVEN'):self.execute()

    def test_damaged_canonical_receipt_blocks_production(self):
        self.owner.fast_track_validation_receipt.side_effect=RuntimeError('SOURCE_SCOPE_UNPROVEN')
        with self.assertRaisesRegex(RuntimeError,'SOURCE_SCOPE_UNPROVEN'):self.execute()

    def test_incomplete_history_and_invalid_sha_rejected(self):
        self.owner.api_get.side_effect=lambda repo,path,token: [self.pull] if '/pulls?' in path else dict(total_count=4,workflow_runs=self.rows)
        with self.assertRaisesRegex(SystemExit,'HISTORY_INCOMPLETE'):self.execute()
        self.sha='branch-name'
        with self.assertRaisesRegex(SystemExit,'CANDIDATE_INVALID'):self.execute()

class CredentialBoundaryTests(unittest.TestCase):
    def test_only_trusted_job_has_production_or_oidc(self):
        candidate=text.split('  validated-observer:',1)[1].split('  production-readback:',1)[0]
        production=text.split('  production-readback:',1)[1].split('  migration-rehearsal:',1)[0]
        rehearsal=text.split('  migration-rehearsal:',1)[1]
        self.assertEqual(1,text.count('id-token: write'))
        for part in (candidate,rehearsal):
            self.assertNotIn('id-token: write',part)
            self.assertNotIn('environment: Production',part)
            self.assertNotIn('secrets.',part)
        self.assertIn("github.ref == 'refs/heads/Legend/fast-track-release'",production)
        self.assertIn('ref: ${{ github.sha }}',production)
        self.assertNotIn('ref: ${{ env.CANDIDATE_SHA }}',production)
        self.assertNotIn('artifact-ids: ${{ needs.validated-observer.outputs.observer-id }}',production)
        self.assertIn('outputs.contract-id',production)
        self.assertLess(production.index('TRUSTED_SCHEMA_CONTRACT_PROMOTION_REQUIRED'),production.index('azure/login@v2'))
        self.assertIn("contract=root/'parfait-candidate-contract/migration-contract.json'",production)
        self.assertIn('--tool-revision "$CANDIDATE_SHA" --application-revision "$CANDIDATE_SHA"',candidate)
        self.assertNotIn('  push:',text)

    def test_no_publication_or_production_sql_mutation_command(self):
        self.assertNotIn('az webapp deploy',text)
        self.assertNotIn('database update',text)
        self.assertNotIn('deploy-approved-app.py',text)
        self.assertIn('authorizesMutation=False',text)
        self.assertIn("toolRevision=os.environ['GITHUB_SHA']",text)


class TrustedContractExecutionTests(unittest.TestCase):
    def setUp(self):
        import tempfile,json
        self.temporary=tempfile.TemporaryDirectory();self.addCleanup(self.temporary.cleanup)
        self.root=Path(self.temporary.name)
        (self.root/'parfait-candidate-contract').mkdir()
        self.path=self.root/'parfait-candidate-contract/migration-contract.json'
        self.path.write_text('{"migrations":[],"schemaVersion":2}')
        (self.root/'trusted-contract.json').write_text('{"schemaVersion":2,"migrations":[]}')
        self.source=text.split("python3 - <<'PYCONTRACT'\n",1)[1].split('          PYCONTRACT',1)[0]
        self.source='\n'.join(line[10:] for line in self.source.splitlines())

    def execute(self):
        import subprocess
        with patch.dict(os.environ,dict(RUNNER_TEMP=str(self.root)),clear=True), \
             patch.object(subprocess,'run',side_effect=AssertionError('No provider or SQL execution')):
            exec(compile(self.source,'trusted-contract-gate','exec'),{})

    def test_same_contract_ignores_json_object_key_order(self):self.execute()

    def test_changed_definition_requires_trusted_promotion(self):
        self.path.write_text('{"schemaVersion":2,"migrations":[{"id":"changed"}]}')
        with self.assertRaisesRegex(SystemExit,'PROMOTION_REQUIRED'):self.execute()

    def test_malformed_oversized_or_symlink_contract_fails_closed(self):
        import json
        self.path.write_text('invalid')
        with self.assertRaises(json.JSONDecodeError):self.execute()
        self.path.write_text(' '* (4*1024*1024+1))
        with self.assertRaisesRegex(SystemExit,'CONTRACT_UNPROVEN'):self.execute()
        self.path.unlink();self.path.symlink_to(self.root/'trusted-contract.json')
        with self.assertRaisesRegex(SystemExit,'CONTRACT_UNPROVEN'):self.execute()

if __name__=='__main__':
    unittest.main()
