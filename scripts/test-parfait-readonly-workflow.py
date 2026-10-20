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
        self.assertLess(production.index('TRUSTED_SCHEMA_CONTRACT_COMPATIBILITY:PROVEN:'),production.index('azure/login@v2'))
        self.assertIn('fetch-depth: 0',production)
        self.assertIn('candidate_migration_contract_proven(revision,approved,contract,trusted)',production)
        self.assertIn("'--validate-contract'",production)
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
        self.contract=dict(schemaVersion=3,migrations=[dict(id='20260101000000_Original')])
        self.path.write_text(json.dumps(self.contract))
        (self.root/'trusted-contract.json').write_text(json.dumps(self.contract))
        self.candidate='a'*40;self.approved='b'*40;self.checkout=self.approved
        self.proof=dict(state='proven',rule='approved-definitions-exact',runtimeIdentity='c'*64,approvedRuntimeIdentity='c'*64)
        self.verified=dict(schemaVersion=1,purpose='trusted-schema-contract-compatibility',authorizesMutation=False,
            rule='approved-definitions-exact',trustedContractSha256='d'*64,candidateContractSha256='d'*64)
        self.owner=SimpleNamespace(FAST_TRACK_PR_BASE='Legend/fast-track-release',
            api_get=Mock(return_value=dict(object=dict(sha=self.approved))),
            candidate_migration_contract_proven=Mock(side_effect=lambda *args:self.proof))
        self.commands=[];self.cli_error=False;self.ancestor=True
        self.source=text.split("python3 - <<'PYCONTRACT'\n",1)[1].split('          PYCONTRACT',1)[0]
        self.source='\n'.join(line[10:] for line in self.source.splitlines())

    def execute(self):
        import subprocess,json
        def run(command,**kwargs):
            self.commands.append(command)
            if command==['git','merge-base','--is-ancestor',self.approved,self.candidate]:
                if not self.ancestor:raise subprocess.CalledProcessError(1,command)
                return SimpleNamespace(returncode=0)
            expected=['dotnet',str(self.root/'parfait-trusted-observer/MigrationReleaseProbe.dll'),'--validate-contract',str(self.path)]
            if command==expected:
                if self.cli_error:raise subprocess.CalledProcessError(1,command)
                return SimpleNamespace(returncode=0,stdout=json.dumps(self.verified))
            raise AssertionError('No candidate, provider or SQL execution is permitted')
        with patch.dict(os.environ,dict(RUNNER_TEMP=str(self.root),CANDIDATE_SHA=self.candidate,GITHUB_SHA=self.approved,
                 GITHUB_REPOSITORY='owner/repo',GITHUB_TOKEN='synthetic'),clear=True), \
             patch.object(subprocess,'check_output',return_value=self.checkout), \
             patch.object(subprocess,'run',side_effect=run), \
             patch.object(importlib.util,'spec_from_file_location',return_value=Mock()), \
             patch.object(importlib.util,'module_from_spec',return_value=self.owner), \
             contextlib.redirect_stdout(io.StringIO()):
            exec(compile(self.source,'trusted-contract-gate','exec'),{})

    def test_exact_inputs_and_real_trusted_cli_are_both_required(self):
        self.execute()
        self.owner.candidate_migration_contract_proven.assert_called_once_with(self.candidate,self.approved,self.contract,self.contract)
        self.assertEqual(2,len(self.commands))
        self.assertIn('parfait-trusted-observer',self.commands[1][1])
        self.assertTrue((self.root/'trusted-contract-proof.json').is_file())

    def test_same_contract_ignores_json_object_key_order(self):
        self.path.write_text('{"migrations":[{"id":"20260101000000_Original"}],"schemaVersion":3}')
        self.execute()

    def test_reviewed_append_needs_matching_independent_cli_rule(self):
        self.proof['rule']='approved-prefix-reviewed-receptionist-entry'
        with self.assertRaisesRegex(SystemExit,'COMPATIBILITY_UNPROVEN'):self.execute()
        self.verified['rule']=self.proof['rule'];self.execute()

    def test_changed_dependency_or_metadata_stops_before_any_cli_or_sign_in(self):
        self.owner.candidate_migration_contract_proven.side_effect=ValueError('CANDIDATE_MIGRATION_CONTRACT_UNPROVEN')
        with self.assertRaisesRegex(ValueError,'UNPROVEN'):self.execute()
        self.assertEqual(1,len(self.commands))

    def test_changed_checkout_current_head_or_ancestry_is_rejected(self):
        import subprocess
        self.checkout='c'*40
        with self.assertRaisesRegex(SystemExit,'CHECKOUT_CHANGED'):self.execute()
        self.checkout=self.approved
        self.owner.api_get.return_value=dict(object=dict(sha='c'*40))
        with self.assertRaisesRegex(SystemExit,'HEAD_CHANGED'):self.execute()
        self.owner.api_get.return_value=dict(object=dict(sha=self.approved));self.ancestor=False
        with self.assertRaises(subprocess.CalledProcessError):self.execute()
        self.owner.candidate_migration_contract_proven.assert_not_called()

    def test_cli_failure_wrong_purpose_or_admission_claim_is_rejected(self):
        import subprocess
        self.cli_error=True
        with self.assertRaises(subprocess.CalledProcessError):self.execute()
        self.cli_error=False
        for key,value in [('purpose','observation'),('authorizesMutation',True),('trustedContractSha256','not-a-hash'),('schemaVersion',2)]:
            old=self.verified[key];self.verified[key]=value
            with self.subTest(key=key),self.assertRaisesRegex(SystemExit,'COMPATIBILITY_UNPROVEN'):self.execute()
            self.verified[key]=old

    def test_malformed_oversized_or_symlink_contract_fails_closed(self):
        import json
        self.path.write_text('invalid')
        with self.assertRaises(json.JSONDecodeError):self.execute()
        self.path.write_text(' '* (4*1024*1024+1))
        with self.assertRaisesRegex(SystemExit,'CONTRACT_UNPROVEN'):self.execute()
        self.path.unlink();self.path.symlink_to(self.root/'trusted-contract.json')
        with self.assertRaisesRegex(SystemExit,'CONTRACT_UNPROVEN'):self.execute()

    def test_duplicate_top_level_or_nested_properties_are_rejected(self):
        for raw in ('{"schemaVersion":3,"schemaVersion":3,"migrations":[]}',
                    '{"schemaVersion":3,"migrations":[{"id":"first","id":"second"}]}'):
            self.path.write_text(raw)
            with self.subTest(raw=raw),self.assertRaisesRegex(SystemExit,'CONTRACT_UNPROVEN'):self.execute()
        self.owner.candidate_migration_contract_proven.assert_not_called()

if __name__=='__main__':
    unittest.main()
