#!/usr/bin/env python3
"""Exercise disposable SQL ownership without contacting Docker or production."""
import importlib.util
import json
import copy
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('migration_probe_package', Path(__file__).with_name('migration-probe-package.py'))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


class ObservedRehearsalReuseTests(unittest.TestCase):
    def setUp(self):
        import os, tempfile
        self.temporary=tempfile.TemporaryDirectory();self.addCleanup(self.temporary.cleanup)
        self.root=Path(self.temporary.name);self.observation=self.root/'observation.json'
        self.observation.write_text(json.dumps(dict(producingAttempt=2,contractDigest='c'*64)))
        self.revision='a'*40;self.baseline=dict(databaseIdentity='b'*64,pendingCount=2)
        self.run=dict(id=17,run_attempt=2,head_sha='b'*40,
            path='.github/workflows/parfait-cutover-live-readonly.yml',
            head_branch='Legend/fast-track-release',event='workflow_dispatch',
            status='in_progress',head_repository=dict(full_name='owner/repo'))
        for patcher in (patch.dict(os.environ,dict(GITHUB_SHA='b'*40,CANDIDATE_SHA=self.revision,GITHUB_REPOSITORY='owner/repo',
                GITHUB_RUN_ID='17',GITHUB_RUN_ATTEMPT='2',GITHUB_TOKEN='fixture'),clear=True),
                patch.object(m.subprocess,'check_output',return_value=self.revision),
                patch.object(m.AUTHORITY,'api_get',return_value=self.run),
                patch.object(m,'fast_track_observation_source',return_value=dict(attempt=2)),
                patch.object(m,'fast_track_baseline',return_value=self.baseline),
                patch.object(m,'verified_fast_track_observation',side_effect=lambda *args: json.loads(self.observation.read_text())),
                patch.object(m,'manifest',return_value={}),
                patch.object(m,'digest',return_value='c'*64)):

            patcher.start();self.addCleanup(patcher.stop)

    def prepare(self):
        return m.prepare_observed_fast_track(self.revision,self.root/'bundle',self.observation,self.root/'probe')

    def test_authenticated_ready_schema_requires_no_rehearsal_or_relabeling(self):
        self.baseline.update(ready=True,pendingCount=0)
        with patch.object(m,'restore_fast_track') as restore, patch.object(m,'rehearse_fast_track') as build:
            self.assertTrue(self.prepare())
            restore.assert_not_called();build.assert_not_called()

    def test_compatible_original_proof_executes_no_new_rehearsal(self):
        proof=dict(candidate='c'*40,baseline=self.baseline)
        with patch.object(m,'restore_fast_track',return_value=proof),patch.object(m,'rehearse_fast_track') as build:
            self.assertTrue(self.prepare());build.assert_not_called()
        self.assertEqual('c'*40,proof['candidate'])

    def test_changed_live_baseline_or_damaged_evidence_never_falls_back_to_build(self):
        with patch.object(m,'restore_fast_track',return_value=dict(candidate='c'*40,baseline={})), \
             patch.object(m,'rehearse_fast_track') as build:
            with self.assertRaisesRegex(ValueError,'BASELINE_CHANGED'):self.prepare()
            build.assert_not_called()
        with patch.object(m,'restore_fast_track',side_effect=ValueError('FAST_TRACK_EXACT_REHEARSAL_BYTES_UNPROVEN')), \
             patch.object(m,'rehearse_fast_track') as build:
            with self.assertRaisesRegex(ValueError,'BYTES_UNPROVEN'):self.prepare()
            build.assert_not_called()

    def test_only_missing_compatible_rehearsal_uses_original_rehearsal_owner(self):
        with patch.object(m,'restore_fast_track',side_effect=ValueError('FAST_TRACK_COMPATIBLE_REHEARSAL_MISSING')), \
             patch.object(m,'rehearse_fast_track') as build:
            self.assertFalse(self.prepare());build.assert_called_once()

    def test_wrong_producer_or_observation_never_restores(self):
        with patch.object(m,'restore_fast_track') as restore:
            self.run['head_sha']='d'*40
            with self.assertRaisesRegex(ValueError,'REVISION_CHANGED'):self.prepare()
            self.run['head_sha']='b'*40
            self.observation.write_text(json.dumps(dict(producingAttempt=1,contractDigest='c'*64)))
            with self.assertRaisesRegex(ValueError,'ATTEMPT_MISMATCH'):self.prepare()
            restore.assert_not_called()


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


class FastTrackObservationResumeTests(unittest.TestCase):
    def source(self, artifacts, jobs, attempt=2):
        with patch.object(m.AUTHORITY, 'api_get', side_effect=[
            dict(id=10,run_attempt=attempt,head_sha='b'*40,
                path='.github/workflows/parfait-cutover-live-readonly.yml',
                head_branch='Legend/fast-track-release',event='workflow_dispatch',
                head_repository=dict(full_name='owner/repo')),
            dict(total_count=len(artifacts), artifacts=artifacts),dict(total_count=len(jobs),jobs=jobs)]):
            return m.fast_track_observation_source('owner/repo', 'a'*40, 10, attempt, 'token')

    def artifact(self, attempt):
        return dict(id=100+attempt,workflow_run=dict(id=10),name='parfait-readonly-evidence-'+'a'*40+'-'+str(attempt),expired=False)

    def job(self, conclusion='success'):
        return dict(name='Non-mutating Azure, SQL schema and public runtime inventory',
                    status='completed',conclusion=conclusion)

    def test_reuses_prior_successful_attempt_without_repeating_observation(self):
        result=self.source([self.artifact(1)],[self.job()])
        self.assertEqual(result['attempt'],1)

    def test_failed_latest_observation_cannot_fall_back_to_older_success(self):
        with self.assertRaisesRegex(ValueError, 'JOB_UNPROVEN'):
            self.source([self.artifact(1),self.artifact(2)],[self.job('failure')])

    def test_expired_duplicate_or_future_artifact_rejected(self):
        expired=self.artifact(1);expired['expired']=True
        for artifacts in ([expired],[self.artifact(1),self.artifact(1)],[self.artifact(3)]):
            with self.subTest(artifacts=artifacts),self.assertRaisesRegex(ValueError, 'ARTIFACT_UNPROVEN'):
                self.source(artifacts,[self.job()])


class FastTrackBaselineTests(unittest.TestCase):
    def setUp(self):
        self.row = dict(observed=True, databaseIdentity='a'*64, schemaIdentity='b'*64,
            baselineIdentity='c'*64, ready=False, pendingCount=1,
            firstPendingMigrationId='20261011064911_AddBusinessScopedMerchantPayments',
            lastAppliedMigrationId='20261007134500_AddFounderAssistantRules')
        self.report = dict(schemaVersion=1, purpose='read-only-schema-plan', authorizesMutation=False,
            candidate='d'*40, runId='123', producingAttempt=1, sharedDatabase=True,
            applications={'masterapp-parfait': dict(self.row), 'masterapp-protect': dict(self.row)},
            toolRevision='e'*40,contractDigest='f'*64,contractArtifactId=41,observerArtifactId=42)
        self.source=dict(toolRevision='e'*40,attempt=1,artifacts=[
            dict(id=41,name='parfait-readonly-contract-'+'d'*40+'-1',expired=False,workflow_run=dict(id=123)),
            dict(id=42,name='parfait-readonly-schema-observer-'+'d'*40+'-1',expired=False,workflow_run=dict(id=123))])

    def test_same_run_same_database_baseline_accepted(self):
        baseline=m.fast_track_baseline(self.report, 'd'*40, 123,self.source)
        self.assertEqual(baseline['pendingCount'], 1)
        self.assertNotIn('observed', baseline)

    def test_wrong_candidate_run_and_mutation_claim_rejected(self):
        for key,value in [('candidate','e'*40),('runId','124'),('authorizesMutation',True),
                          ('sharedDatabase',False),('purpose','release-admission')]:
            report=copy.deepcopy(self.report);report[key]=value
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, 'IDENTITY_UNPROVEN'):
                m.fast_track_baseline(report,'d'*40,123,self.source)

    def test_unobserved_or_inconsistent_schema_rejected(self):
        for key,value in [('observed',False),('ready',True),('pendingCount',True),
                          ('baselineIdentity','invalid'),('firstPendingMigrationId','unsafe')]:
            report=copy.deepcopy(self.report);report['applications']['masterapp-protect'][key]=value
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, 'SCHEMA_UNPROVEN'):
                m.fast_track_baseline(report,'d'*40,123,self.source)

    def test_different_database_or_baseline_rejected(self):
        for key in ('databaseIdentity','schemaIdentity','baselineIdentity'):
            report=copy.deepcopy(self.report);report['applications']['masterapp-protect'][key]='f'*64
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, 'BASELINES_DIFFER'):
                m.fast_track_baseline(report,'d'*40,123,self.source)

    def test_unexpected_host_rejected(self):
        self.report['applications']['unrelated-app']=self.row
        with self.assertRaisesRegex(ValueError, 'IDENTITY_UNPROVEN'):
            m.fast_track_baseline(self.report,'d'*40,123,self.source)



class TrustedProducerRegressionTests(unittest.TestCase):
    def test_wrong_trusted_identity_fails_closed(self):
        run=dict(id=17,run_attempt=2,head_sha='b'*40,path='.github/workflows/parfait-cutover-live-readonly.yml',
            head_branch='Legend/fast-track-release',event='workflow_dispatch',status='in_progress',
            head_repository=dict(full_name='owner/repo'))
        for key,value in [('id',18),('run_attempt',1),('head_sha','branch'),('path','other.yml'),
            ('head_branch','isolation/candidate'),('event','push'),('head_repository',dict(full_name='fork/repo')),
            ('status','completed')]:
            with self.subTest(key=key),patch.object(m.AUTHORITY,'api_get',return_value=dict(run,**{key:value})), \
                 self.assertRaisesRegex(ValueError,'PRODUCER_UNPROVEN'):
                m.trusted_fast_track_run('owner/repo',17,2,'fixture',active=True)

    def test_oidc_or_azure_credential_env_rejected_before_provider_calls(self):
        import os
        for key in ('ACTIONS_ID_TOKEN_REQUEST_TOKEN','ACTIONS_ID_TOKEN_REQUEST_URL','AZURE_CLIENT_SECRET',
                    'LEGEND_RELEASE_DB_CONNECTION'):
            with self.subTest(key=key),patch.dict(os.environ,{key:'synthetic'},clear=True), \
                 patch.object(m.AUTHORITY,'api_get') as api, \
                 patch.object(m.subprocess,'run') as command:
                for fn in (m.prepare_observed_fast_track,m.rehearse_fast_track):
                    with self.assertRaisesRegex(ValueError,'CREDENTIAL_FREE'):
                        fn('a'*40,Path('/unused'),Path('/unused'),Path('/unused'))
                api.assert_not_called();command.assert_not_called()

    def test_supplied_observation_must_equal_immutable_download(self):
        import tempfile
        with tempfile.TemporaryDirectory() as temporary:
            path=Path(temporary)/'observation.json';path.write_text('{"candidate":"changed"}')
            def download(repo,run,name,directory,*,artifact_id):
                self.assertEqual(71,artifact_id)
                (directory/'sanitized-schema.json').write_text('{"candidate":"original"}')
            with patch.object(m.AUTHORITY,'_download_run_artifact',side_effect=download), \
                 self.assertRaisesRegex(ValueError,'INPUT_CHANGED'):
                m.verified_fast_track_observation('owner/repo',17,dict(artifact='original',artifactId=71),path)


class ImmutableRestoreRegressionTests(unittest.TestCase):
    def setUp(self):
        import hashlib,os,tempfile
        self.temporary=tempfile.TemporaryDirectory();self.addCleanup(self.temporary.cleanup)
        self.root=Path(self.temporary.name);self.candidate='a'*40;self.tool='b'*40;self.target='c'*40
        self.bundle=b'exact synthetic bundle';self.contract=b'{"schemaVersion":2}'
        self.bundle_hash=hashlib.sha256(self.bundle).hexdigest();self.contract_hash=hashlib.sha256(self.contract).hexdigest()
        self.run=dict(id=17,run_attempt=1,head_sha=self.tool,path='.github/workflows/parfait-cutover-live-readonly.yml',
            head_branch='Legend/fast-track-release',event='workflow_dispatch',status='completed',conclusion='success',
            head_repository=dict(full_name='owner/repo'))
        def artifact(id,prefix):
            return dict(id=id,name=prefix+self.candidate+'-1',expired=False,workflow_run=dict(id=17))
        self.artifacts=[artifact(51,'parfait-fast-track-migration-'),artifact(52,'parfait-readonly-evidence-'),
                        artifact(53,'parfait-readonly-schema-observer-'),artifact(54,'parfait-readonly-contract-')]
        self.row=dict(observed=True,databaseIdentity='d'*64,schemaIdentity='e'*64,baselineIdentity='f'*64,
            ready=False,pendingCount=1,firstPendingMigrationId='20261011064911_Pending',lastAppliedMigrationId='20261007134500_Applied')
        self.report=dict(schemaVersion=1,purpose='read-only-schema-plan',authorizesMutation=False,
            candidate=self.candidate,toolRevision=self.tool,runId='17',producingAttempt=1,sharedDatabase=True,
            contractDigest=self.contract_hash,contractArtifactId=54,observerArtifactId=53,
            applications={'masterapp-protect':dict(self.row),'masterapp-parfait':dict(self.row)})
        self.receipt=dict(schemaVersion=1,purpose='fast-track-migration-rehearsal',proven=True,isolated=True,
            syntheticDataOnly=True,authorizesMutation=False,releaseAdmission=False,candidate=self.candidate,
            toolRevision=self.tool,producingRun=17,producingAttempt=1,observationRun=17,observationAttempt=1,
            observationArtifactId=52,observerArtifactId=53,contractDigest=self.contract_hash,
            baseline={key:value for key,value in self.row.items() if key!='observed'},
            sqlImage=m.SQL_REHEARSAL_IMAGE,counts=dict(mutations=1,intents=1,successReceipts=2),bundleDigest=self.bundle_hash)
        self.downloads=[];self.corrupt_probe=False
        self.jobs=[dict(name=name,status='completed',conclusion='success') for name in (
            'Rehearse exact migration bundle and recovery without production credentials',
            'Non-mutating Azure, SQL schema and public runtime inventory')]
        self.env=patch.dict(os.environ,dict(GITHUB_REPOSITORY='owner/repo',GITHUB_TOKEN='fixture'),clear=True)
        self.env.start();self.addCleanup(self.env.stop)

    def api(self,repo,path,token):
        if path.startswith('actions/runs?'):return dict(workflow_runs=[self.run])
        if path.endswith('/artifacts?per_page=100'):return dict(total_count=len(self.artifacts),artifacts=self.artifacts)
        if '/jobs?' in path:return dict(total_count=len(self.jobs),jobs=self.jobs)
        if path=='actions/runs/17':return self.run
        raise AssertionError(path)

    def download(self,repo,run,name,directory,*,artifact_id):
        import hashlib
        directory.mkdir(parents=True,exist_ok=True);self.downloads.append(artifact_id)
        if artifact_id==51:
            (directory/m.AUTHORITY.MIGRATION_BUNDLE_NAME).write_bytes(self.bundle)
            (directory/'migration.component.json').write_text(json.dumps(dict(applicationReleaseSha=self.candidate,
                component='migration',sha256=self.bundle_hash)))
            (directory/'rehearsal.json').write_text(json.dumps(self.receipt))
        elif artifact_id==52:
            (directory/'sanitized-schema.json').write_text(json.dumps(self.report))
        elif artifact_id==53:
            (directory/'MigrationReleaseProbe.dll').write_bytes(b'synthetic trusted inventory')
            (directory/'migration-contract.json').write_bytes(self.contract)
            files={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in directory.iterdir()}
            (directory/'manifest.json').write_text(json.dumps(dict(identity='probe',files=files)))
            if self.corrupt_probe:(directory/'MigrationReleaseProbe.dll').write_bytes(b'tampered')
        else:raise AssertionError(artifact_id)

    def restore(self):
        with patch.object(m.AUTHORITY,'api_get',side_effect=self.api), \
             patch.object(m.AUTHORITY,'package_component_manifest',return_value=dict(reusable=True,contentIdentity='same')), \
             patch.object(m.AUTHORITY,'migration_probe_identity',return_value=dict(identity='probe')), \
             patch.object(m.AUTHORITY,'_download_run_artifact',side_effect=self.download), \
             patch.object(m.subprocess,'run',return_value=SimpleNamespace(returncode=0)), \
             patch.object(m,'fast_track_probe_compatible',return_value=True):
            return m.restore_fast_track(self.target,self.root/'restored',self.root/'probe')

    def test_restore_binds_candidate_separately_from_trusted_run_head(self):
        receipt=self.restore()
        self.assertEqual(self.candidate,receipt['candidate']);self.assertEqual(self.tool,receipt['toolRevision'])
        self.assertEqual([51,52,53],self.downloads)
        self.assertEqual(self.bundle,(self.root/'restored'/m.AUTHORITY.MIGRATION_BUNDLE_NAME).read_bytes())
        self.assertEqual(self.candidate,receipt['source']['producerRevision'])

    def test_swapped_candidate_tool_attempt_digest_or_observation_rejected(self):
        for key,value in [('candidate',self.tool),('toolRevision',self.candidate),('producingAttempt',2),
            ('observationArtifactId',99),('contractDigest','0'*64),('bundleDigest','0'*64)]:
            old=self.receipt[key];self.receipt[key]=value
            with self.subTest(key=key),self.assertRaisesRegex(ValueError,'BYTES_UNPROVEN'):self.restore()
            self.receipt[key]=old
        self.assertFalse((self.root/'restored').exists())

    def test_probe_digest_and_failed_observer_are_not_reused(self):
        self.corrupt_probe=True
        with self.assertRaisesRegex(ValueError,'immutable digest mismatch'):self.restore()
        self.corrupt_probe=False;self.jobs[1]['conclusion']='failure'
        with self.assertRaisesRegex(ValueError,'OBSERVATION_JOB_UNPROVEN'):self.restore()
        self.assertFalse((self.root/'restored').exists())

    def test_duplicate_or_wrong_run_artifact_rejected(self):
        self.artifacts.append(dict(self.artifacts[0]))
        with self.assertRaisesRegex(ValueError,'DUPLICATED'):self.restore()
        self.artifacts.pop();self.artifacts[0]['workflow_run']['id']=99
        with self.assertRaisesRegex(ValueError,'ATTEMPT_UNPROVEN'):self.restore()

class TrustedOfflineContractGateTests(unittest.TestCase):
    def setUp(self):
        import tempfile
        self.temporary=tempfile.TemporaryDirectory();self.addCleanup(self.temporary.cleanup)
        self.root=Path(self.temporary.name);self.calls=[];self.sql_entered=False
        self.test_stdout='LEGEND_REVIEWED_APPEND_TEST:NOT_PRESENT_IN_ASSEMBLY\nLEGEND_TRUSTED_CONTRACT_TESTS:PASS\n'
        self.current=dict(schemaVersion=3,migrations=[dict(id='20260101000000_Original')])
        self.test_exit=0;self.source_present=False
        self.fixture_exit=0;self.fixture_stderr=''

    def process(self,command,**kwargs):
        self.calls.append((command,kwargs))
        if command[1]=='publish':return SimpleNamespace(returncode=0)
        if command[-1]=='--test-contract-compatibility':return SimpleNamespace(returncode=self.test_exit,stdout=self.test_stdout)
        if command[-1]=='--export-contract':return SimpleNamespace(returncode=0,stdout=json.dumps(self.current))
        if command[-1]=='migration-test':return SimpleNamespace(returncode=self.fixture_exit,stdout='synthetic secret',stderr=self.fixture_stderr)
        raise AssertionError(command)

    def gate(self):
        import contextlib,os
        @contextlib.contextmanager
        def sql():
            self.sql_entered=True
            yield 'Server=127.0.0.1,15433;Database=Synthetic'
        with patch.object(m.subprocess,'run',side_effect=self.process),patch.object(m,'isolated_sql',side_effect=sql), \
             patch.object(Path,'is_file',return_value=self.source_present), \
             patch.dict(os.environ,dict(PATH='/synthetic',HOME='/synthetic',AZURE_CLIENT_SECRET='synthetic-parent-only'),clear=True):
            m.test_schema_contract(self.root)

    def test_actual_cli_contract_tests_precede_existing_sql_fixture(self):
        self.gate()
        self.assertTrue(self.sql_entered)
        self.assertEqual('--test-contract-compatibility',self.calls[1][0][-1])
        self.assertEqual('--export-contract',self.calls[2][0][-1])
        self.assertEqual('migration-test',self.calls[-1][0][-1])
        for command,kwargs in (self.calls[1],self.calls[2],self.calls[-1]):
            self.assertNotIn('AZURE_CLIENT_SECRET',kwargs['env'])
        self.assertIn('LEGEND_ISOLATED_SQL',self.calls[-1][1]['env'])

    def test_sql_failure_identifies_stage_without_exposing_provider_output(self):
        self.fixture_exit=1
        self.fixture_stderr='password=synthetic-secret\nLEGEND_ISOLATED_FIXTURE:UNPROVEN:migrate:SQL:547\n'
        with self.assertRaises(RuntimeError) as failed:self.gate()
        self.assertIn('LEGEND_ISOLATED_FIXTURE:UNPROVEN:migrate:SQL:547',str(failed.exception))
        self.assertNotIn('secret',str(failed.exception))
        self.assertEqual('migration-test',self.calls[-1][0][-1])
        self.assertEqual(300,self.calls[-1][1]['timeout'])

    def test_unrecognized_or_ambiguous_provider_output_is_never_forwarded(self):
        self.fixture_exit=1
        for output in ('connection=synthetic-secret',
            'LEGEND_ISOLATED_FIXTURE:UNPROVEN:migrate:SQL:547 password=synthetic-secret',
            'LEGEND_ISOLATED_FIXTURE:UNPROVEN:migrate:SQL:547\nLEGEND_ISOLATED_FIXTURE:UNPROVEN:verify:SQL:208',
            'LEGEND_ISOLATED_FIXTURE:UNPROVEN:synthetic-secret:SQL:547'):
            self.fixture_stderr=output
            with self.subTest(output=output),self.assertRaises(RuntimeError) as failed:self.gate()
            self.assertTrue(str(failed.exception).endswith('UNPROVEN:dispatch:UNCLASSIFIED'))
            self.assertNotIn('secret',str(failed.exception))

    def test_failed_or_missing_cli_pass_marker_stops_before_sql(self):
        for failed in ('exit','marker'):
            self.test_exit=1 if failed=='exit' else 0
            self.test_stdout='' if failed=='marker' else 'LEGEND_TRUSTED_CONTRACT_TESTS:PASS\n'
            with self.subTest(failed=failed),self.assertRaisesRegex(RuntimeError,'compatibility contract failed'):self.gate()
            self.assertFalse(self.sql_entered)

    def test_present_migration_cannot_be_omitted_from_actual_export(self):
        self.source_present=True
        with self.assertRaisesRegex(RuntimeError,'Reviewed migration entry contract failed'):self.gate()
        self.assertFalse(self.sql_entered)

    def test_actual_generated_entry_must_match_python_pin_and_csharp_test(self):
        import hashlib
        entry=dict(id='20261020160000_AddReceptionistCoordination',supported=True)
        self.current['migrations'].append(entry);self.source_present=True
        pin=hashlib.sha256(json.dumps(entry,sort_keys=True,separators=(',',':')).encode()).hexdigest()
        with patch.object(m.AUTHORITY,'REVIEWED_RECEPTIONIST_ENTRY',pin):
            with self.assertRaisesRegex(RuntimeError,'Reviewed migration entry contract failed'):self.gate()
            self.assertFalse(self.sql_entered)
            self.test_stdout='LEGEND_REVIEWED_APPEND_TEST:PROVEN\nLEGEND_TRUSTED_CONTRACT_TESTS:PASS\n'
            self.gate();self.assertTrue(self.sql_entered)
        self.sql_entered=False
        with self.assertRaisesRegex(RuntimeError,'Reviewed migration entry contract failed'):self.gate()
        self.assertFalse(self.sql_entered)


if __name__ == '__main__':
    unittest.main()
