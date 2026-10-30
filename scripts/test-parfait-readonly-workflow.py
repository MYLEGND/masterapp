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

    def execute(self, same_merge=False):
        with patch.dict(os.environ,dict(GITHUB_REPOSITORY='owner/repo',CANDIDATE_SHA=self.sha,
                GITHUB_SHA=self.sha if same_merge else 'b'*40,GITHUB_TOKEN='synthetic'),clear=True), \
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

    def test_same_merge_requires_trusted_merge_proof_before_candidate_build(self):
        self.owner.fast_track_inventory_merge_proof=Mock()
        with self.assertRaises(SystemExit) as result: self.execute(same_merge=True)
        self.assertEqual(0,result.exception.code)
        self.owner.fast_track_inventory_merge_proof.assert_called_once_with('owner/repo',self.sha,'synthetic')
        self.owner.api_get.assert_not_called()
        self.owner.fast_track_validation_receipt.assert_not_called()
        self.owner.fast_track_inventory_merge_proof.side_effect=RuntimeError('MERGE_UNPROVEN')
        with self.assertRaisesRegex(RuntimeError,'MERGE_UNPROVEN'): self.execute(same_merge=True)
        job=text.split('  validated-observer:',1)[1].split('  production-readback:',1)[0]
        checkout=job.split('uses: actions/checkout@v4',1)[1].split('      - ',1)[0]
        self.assertIn('fetch-depth: 0',checkout)
        self.assertLess(job.index('PYVALIDATE'),job.index('ref: ${{ env.CANDIDATE_SHA }}'))


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

    def test_payment_rule_requires_exact_plural_observer_rule(self):
        self.proof['rule']='approved-prefix-reviewed-canonical-payment-entry'
        for wrong in ('approved-definitions-exact',self.proof['rule']):
            self.verified['rule']=wrong
            with self.assertRaisesRegex(SystemExit,'COMPATIBILITY_UNPROVEN'):self.execute()
        self.verified['rule']='approved-prefix-reviewed-canonical-payment-entries'
        self.execute()

    def test_unknown_rule_cannot_fall_back_to_exact_definitions(self):
        self.proof['rule']='unknown-reviewed-rule'
        with self.assertRaisesRegex(SystemExit,'COMPATIBILITY_UNPROVEN'):self.execute()
        self.assertEqual(1,len(self.commands))
        self.assertFalse((self.root/'trusted-contract-proof.json').exists())

    def test_combined_crm_rule_requires_matching_independent_observer(self):
        self.proof['rule']='approved-prefix-reviewed-diagnostics-crm-entries'
        for wrong in ('approved-definitions-exact', 'approved-prefix-reviewed-diagnostics-entry',
                      'approved-prefix-reviewed-browser-entry'):
            self.verified['rule']=wrong
            with self.subTest(rule=wrong),self.assertRaisesRegex(SystemExit,'COMPATIBILITY_UNPROVEN'):
                self.execute()
            self.assertFalse((self.root/'trusted-contract-proof.json').exists())
        self.verified['rule']=self.proof['rule']
        self.execute()
        import json
        receipt=json.loads((self.root/'trusted-contract-proof.json').read_text())
        self.assertEqual(self.proof['rule'],receipt['observer']['rule'])
        self.assertFalse(receipt['observer']['authorizesMutation'])

    def test_browser_rule_requires_exact_independent_observer_rule(self):
        import json
        self.proof['rule']='approved-prefix-reviewed-browser-entry'
        for wrong in ('approved-definitions-exact', 'approved-prefix-reviewed-receptionist-entry',
                      'approved-prefix-reviewed-management-entry', 'approved-prefix-reviewed-browser-entries'):
            self.verified['rule']=wrong
            with self.subTest(rule=wrong),self.assertRaisesRegex(SystemExit,'COMPATIBILITY_UNPROVEN'):
                self.execute()
            self.assertFalse((self.root/'trusted-contract-proof.json').exists())
        self.verified['rule']='approved-prefix-reviewed-browser-entry'
        self.execute()
        receipt=json.loads((self.root/'trusted-contract-proof.json').read_text())
        self.assertEqual(self.proof['rule'],receipt['rule'])
        self.assertEqual(self.verified,receipt['observer'])
        self.assertFalse(receipt['observer']['authorizesMutation'])

    def test_existing_static_styles_rule_remains_explicit(self):
        self.proof['rule']='approved-definitions-static-styles-only'
        self.execute()

    def test_reviewed_runtime_pair_requires_unchanged_exact_observer_rule(self):
        import json
        self.proof['rule']='approved-definitions-reviewed-runtime-pair'
        for wrong in (self.proof['rule'], 'approved-prefix-reviewed-browser-entry',
                      'approved-prefix-reviewed-receptionist-entry'):
            self.verified['rule']=wrong
            with self.subTest(rule=wrong),self.assertRaisesRegex(SystemExit,'COMPATIBILITY_UNPROVEN'):
                self.execute()
            self.assertFalse((self.root/'trusted-contract-proof.json').exists())
        self.verified['rule']='approved-definitions-exact'
        self.execute()
        receipt=json.loads((self.root/'trusted-contract-proof.json').read_text())
        self.assertEqual(self.proof['rule'],receipt['rule'])
        self.assertEqual(self.verified,receipt['observer'])
        self.assertFalse(receipt['observer']['authorizesMutation'])

    def test_reviewed_runtime_pair_still_requires_fresh_export_and_successful_trusted_cli(self):
        import subprocess
        self.proof['rule']='approved-definitions-reviewed-runtime-pair'
        self.cli_error=True
        with self.assertRaises(subprocess.CalledProcessError):self.execute()
        self.assertFalse((self.root/'trusted-contract-proof.json').exists())
        production=text.split('  production-readback:',1)[1].split('  migration-rehearsal:',1)[0]
        self.assertLess(production.index('--export-contract'),production.index("python3 - <<'PYCONTRACT'"))
        self.assertLess(production.index("'--validate-contract'"),production.index('azure/login@v2'))

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




class RecoveryEvidenceTests(unittest.TestCase):
    def execute(self, proof, *, missing=False):
        import json,tempfile
        source=text.split("python3 - <<'PYRECOVERY'\n",1)[1].split('          PYRECOVERY',1)[0]
        source='\n'.join(line[10:] for line in source.splitlines())
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)/'parfait-proof';root.mkdir()
            (root/'sanitized-schema.json').write_text(json.dumps(dict(sharedDatabase=True,
                applications={'masterapp-protect':{'databaseIdentity':'a'*64}})))
            owner=SimpleNamespace(production_recovery=Mock(return_value=proof,
                side_effect=RuntimeError('RECOVERY_UNAVAILABLE') if missing else None))
            with patch('importlib.util.spec_from_file_location',return_value=Mock()), \
                 patch('importlib.util.module_from_spec',return_value=owner), \
                 patch.dict(os.environ,{'RUNNER_TEMP':directory}),contextlib.redirect_stdout(io.StringIO()):
                try:exec(compile(source,'recovery-evidence','exec'),{})
                except BaseException:
                    self.assertFalse((root/'sanitized-recovery.json').exists())
                    raise
            self.assertEqual(proof,json.loads((root/'sanitized-recovery.json').read_text()))

    def proof(self):
        return dict(databaseIdentity='a'*64,observedUtc='2026-10-26T01:00:00+00:00',
            restorePointUtc='2026-10-26T00:45:00+00:00',earliestRestoreUtc='2026-10-20T00:00:00+00:00',
            retentionDays=7,restoreWindowVerified=True,restoreExecuted=False,authorizesMutation=False)

    def test_valid_sanitized_recovery_is_retained_without_retention_or_cleanup_change(self):
        self.execute(self.proof())
        upload=text.split('      - name: Preserve sanitized read-only evidence',1)[1].split('      - name:',1)[0]
        self.assertIn('${{ runner.temp }}/parfait-proof/sanitized-recovery.json',upload)
        self.assertIn('retention-days: 30',upload)
        self.assertNotIn('settings.json',upload)
        self.assertIn('Remove private temporary provider responses',text)

    def test_absent_or_malformed_recovery_never_writes_receipt(self):
        with self.assertRaisesRegex(RuntimeError,'RECOVERY_UNAVAILABLE'):self.execute(None,missing=True)
        for change in ({'databaseIdentity':'b'*64},{'restoreExecuted':True},{'authorizesMutation':True},
                       {'retentionDays':True},{'retentionDays':6},{'restoreWindowVerified':False},
                       {'secret':'must-not-retain'},{'restorePointUtc':'2026-10-27T00:00:00+00:00'}):
            with self.subTest(change=change),self.assertRaisesRegex(SystemExit,'RECOVERY_METADATA_UNPROVEN'):
                self.execute(self.proof()|change)
        with self.assertRaisesRegex(SystemExit,'RECOVERY_METADATA_UNPROVEN'):self.execute(None)


class SchemaFailureClassificationTests(unittest.TestCase):
    def test_actual_schema_step_classifies_only_authored_runtime_errors(self):
        import json, tempfile
        schema=text.split("python3 - <<'PYSCHEMA'\n",1)[1].split('          PYSCHEMA',1)[0]
        schema='\n'.join(line[10:] for line in schema.splitlines())
        expected = {
            'Schema probe process deadline exceeded':'PROBE_PROCESS_DEADLINE',
            'Invalid read-only schema proof':'PROBE_INVALID_PROOF',
            'Read-only schema proof unavailable; nontransient or unclassified failure':'PROBE_OUTPUT_UNRECOGNIZED',
            'Transient SQL schema read exhausted bounded retries':'PROBE_TRANSIENT_EXHAUSTED',
            'Schema probe unclassified exception; readiness unproven':'UNCLASSIFIED',
        }
        cases=[(RuntimeError(message), code) for message,code in expected.items()]
        cases += [(ValueError('Invalid read-only schema proof'),'CANONICAL_SCHEMA_OBSERVATION_UNPROVEN'),
                  (RuntimeError('Server=private;Password=private'),'CANONICAL_SCHEMA_OBSERVATION_UNPROVEN')]
        for error,code in cases:
            with self.subTest(code=code,errorType=type(error).__name__), tempfile.TemporaryDirectory() as directory:
                root=Path(directory);(root/'parfait-proof').mkdir();(root/'parfait-candidate-contract').mkdir()
                (root/'trusted-contract-proof.json').write_text('{}')
                (root/'parfait-candidate-contract/migration-contract.json').write_text('{}')
                module=SimpleNamespace(PROBE_TERMINAL_REASONS={'UNCLASSIFIED':
                    'Schema probe unclassified exception; readiness unproven'},
                    connection_string=Mock(return_value='opaque'), observe=Mock(side_effect=error))
                spec=SimpleNamespace(loader=SimpleNamespace(exec_module=lambda value:None))
                stdout=io.StringIO()
                with patch('importlib.util.spec_from_file_location',return_value=spec), \
                     patch('importlib.util.module_from_spec',return_value=module), \
                     patch('subprocess.run',return_value=SimpleNamespace(returncode=1)), \
                     patch.dict(os.environ,{'RUNNER_TEMP':directory,'CANDIDATE_SHA':'a'*40,'GITHUB_SHA':'b'*40,
                         'CONTRACT_ARTIFACT_ID':'1','OBSERVER_ARTIFACT_ID':'2','GITHUB_RUN_ID':'3','GITHUB_RUN_ATTEMPT':'1'}), \
                     contextlib.redirect_stdout(stdout), self.assertRaisesRegex(SystemExit,'SQL_SCHEMA_PLAN_INCOMPLETE'):
                    exec(compile(schema,'trusted-schema-step','exec'),{})
                result=json.loads((root/'parfait-proof/sanitized-schema.json').read_text())
                self.assertFalse(result['authorizesMutation']);self.assertIsNone(result['sharedDatabase'])
                for app in result['applications'].values():
                    self.assertEqual(code,app['reason']);self.assertFalse(app['observed'])
                self.assertNotIn('private',stdout.getvalue())
                self.assertEqual(2,module.observe.call_count)

if __name__=='__main__':
    unittest.main()
