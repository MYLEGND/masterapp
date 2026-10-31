"""Exercise ambiguous deployment outcomes without contacting or mutating Azure."""
import hashlib
from contextlib import ExitStack
import importlib.util
import io
import json
import os
import subprocess
import shutil
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import Mock, patch
import zipfile

ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('deploy', ROOT / 'deploy-approved-app.py')
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)


def row(identifier, status):
    return {'id': identifier, 'status': status}


CORRELATION = {'kind': 'azure_onedeploy_deployment_tag_v1', 'value': 'c' * 64}


def prepare_supported_azure(azure):
    with patch.object(deploy.subprocess, 'run', side_effect=[
            subprocess.CompletedProcess([], 0, '    --deploymentTag : Linux deployment identifier.\n', ''),
            subprocess.CompletedProcess([], 0, json.dumps({'name': azure.app, 'reserved': True, 'kind': 'app,linux'}), '')]):
        azure.prepare_submission()
    return azure


class CrmRetainedSchemaTests(unittest.TestCase):
    def setUp(self):
        from types import SimpleNamespace
        from datetime import datetime,timezone,timedelta
        self.stack=ExitStack();self.addCleanup(self.stack.close)
        self.authority=deploy._RELEASE_AUTHORITY
        self.revision=self.authority.CRM_PREINTENT_REVISION
        self.observer=dict(runId=20,runAttempt=1,jobId=30,toolRevision='b'*40,promotionRunId=19)
        self.original=dict(run=dict(id=41131223973,head_sha=self.revision),targets=['portal','client','protect','parfait'],
            artifacts=[dict(id=13017640415,expired=False,digest='sha256:cbe0bd54e2e652908a19d84779ec10f51246cf3e01e3920f8e59d50c70b2a508')])
        now=datetime.now(timezone.utc)
        self.observation=dict(databaseIdentity='3a055450807aeaa2c6465702b983dc99968329389d7c0ae480c3c72cc3a9ff3b',
            schemaIdentity='0e4674714d917c5f2dbfa3cd42f2de954e7b3f310c8322c08ec0f427d3615451',baselineIdentity='c'*64,
            observedUtc=now.isoformat(),pendingCount=0,ready=True,mutationActivity='settled')
        self.recovery=dict(databaseIdentity=self.observation['databaseIdentity'],observedUtc=now.isoformat(),
            restorePointUtc=(now-timedelta(minutes=15)).isoformat(),earliestRestoreUtc=(now-timedelta(days=2)).isoformat(),
            retentionDays=7,restoreWindowVerified=True,restoreExecuted=False,authorizesMutation=False)
        self.receipt=dict(producingRun=41131223973,producingAttempt=3,applicationRevision=self.revision,
            child='migrations',phase='success',observation=dict(schemaIdentity=self.observation['schemaIdentity'],providerVersion='schema-ready-no-write'))
        self.report=dict(completeHistory=True,records=[dict(parent='failure',code='settled')])
        self.audit=SimpleNamespace(audit=Mock(return_value=self.report),BLOCKED_CODES=set(),STATUS_CODES={'settled'})
        self.module=SimpleNamespace(history_audit=lambda:self.audit,connection_string=Mock(return_value='test'),
            observe=Mock(side_effect=lambda *a,**k:dict(self.observation)),production_recovery=Mock(return_value=self.recovery))
        self.publisher=Mock(return_value=dict(artifactId=42))
        self.stack.enter_context(patch.dict(os.environ,GITHUB_REPOSITORY='MYLEGND/masterapp',GH_TOKEN='test',MIGRATION_PROBE_DLL='readonly.dll'))
        self.stack.enter_context(patch.object(self.authority,'assert_fast_track_recovery_live_execution'))
        self.stack.enter_context(patch.object(self.authority,'_release_history_json',return_value=self.receipt))
        self.stack.enter_context(patch.object(deploy,'fast_track_migration_gate'))
        self.stack.enter_context(patch.object(deploy.importlib.util,'spec_from_file_location',return_value=SimpleNamespace(loader=Mock())))
        self.stack.enter_context(patch.object(deploy.importlib.util,'module_from_spec',return_value=self.module))

    def call(self):
        return deploy.crm_retained_schema_gate(self.original,self.observer,deploy.time.monotonic()+1200,self.publisher)

    def test_paired_physical_reads_and_durable_readback_before_proof(self):
        proof=self.call();self.assertEqual(proof['artifactId'],42)
        self.assertEqual(self.module.observe.call_count,2)
        self.audit.audit.assert_called_once_with(self.authority,'MYLEGND/masterapp','test')
        for call in self.module.observe.call_args_list:self.assertTrue(call.kwargs['schema_only_activity'])
        name,record=self.publisher.call_args.args
        result=subprocess.run(['node',str(ROOT/'release-artifacts/transport.cjs')],input=json.dumps(dict(mode='validate',name=name,record=record)),text=True,capture_output=True)
        self.assertEqual(result.returncode,0,result.stderr)
        record['sqlExecutionAuthorized']=True
        result=subprocess.run(['node',str(ROOT/'release-artifacts/transport.cjs')],input=json.dumps(dict(mode='validate',name=name,record=record)),text=True,capture_output=True)
        self.assertEqual(result.returncode,2)

    def test_pending_drift_active_wrong_database_and_failed_receipt_block(self):
        for field,value in [('pendingCount',1),('ready',False),('mutationActivity','active'),('databaseIdentity','f'*64),('schemaIdentity','f'*64)]:
            old=self.observation[field];self.observation[field]=value
            with self.subTest(field=field),self.assertRaises(deploy.DeploymentReconciliationRequired):self.call()
            self.observation[field]=old
        self.publisher.assert_not_called()
        self.publisher.side_effect=RuntimeError('readback unavailable')
        with self.assertRaisesRegex(RuntimeError,'readback unavailable'):self.call()

    def test_stale_or_changed_scope_proof_blocks_before_intent(self):
        proof=self.call();proof['record']['observation']['observedUtc']='2020-01-01T00:00:00Z'
        with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired,'STALE'):
            deploy.crm_retained_schema_fresh(proof,self.original,self.observer)
        proof['record']['observer']=dict(self.observer,runAttempt=2)
        with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired,'CHANGED'):
            deploy.crm_retained_schema_fresh(proof,self.original,self.observer)

    def test_incomplete_or_active_history_blocks_without_sql_reads(self):
        self.report['completeHistory']=False
        with self.assertRaises(deploy.DeploymentReconciliationRequired):self.call()
        self.report['completeHistory']=True;self.report['records'][0]['parent']=None
        with self.assertRaises(deploy.DeploymentReconciliationRequired):self.call()
        self.module.observe.assert_not_called();self.publisher.assert_not_called()


class CrmRetainedPortalTests(unittest.TestCase):
    def test_receipt_and_matching_completed_deployment_are_both_required(self):
        authority=deploy._RELEASE_AUTHORITY;revision='a'*40
        original=dict(run=dict(id=10,head_sha=revision));observer=dict(runId=20,runAttempt=1)
        inventory=dict(portal=dict(operationId='b'*64,packageDigest='c'*64))
        success=dict(packageDigest='c'*64,applicationRevision=revision,target='portal',deploymentIds=['new'])
        azure=Mock();azure.observed_revision.return_value=revision
        with patch.dict(os.environ,GITHUB_REPOSITORY='o/r',GH_TOKEN='test'), \
             patch.object(authority,'release_operation_history',return_value=success) as history, \
             patch.object(authority,'assert_fast_track_recovery_live_execution'), \
             patch.object(deploy,'target_azure',return_value=azure), \
             patch.object(deploy,'read_deployments_bounded',return_value=[row('new',4)]) as rows:
            call=lambda:deploy.crm_retained_portal_gate(original,inventory,observer,deploy.time.monotonic()+120)
            call();azure.submit.assert_not_called()
            for value in [[],[row('other',4)],[row('new',1)],[row('new',4),row('busy',0)]]:
                rows.return_value=value
                with self.assertRaises(deploy.DeploymentReconciliationRequired):call()
            rows.return_value=[row('new',4)];azure.observed_revision.return_value='d'*40
            with self.assertRaises(deploy.DeploymentReconciliationRequired):call()
            history.return_value=None
            with self.assertRaises(deploy.DeploymentReconciliationRequired):call()


class CrmPortalFirstGateTests(unittest.TestCase):
    migration = 'Infrastructure/Migrations/20261030070000_CrmRelationshipHistory.cs'
    def setUp(self):
        self.run = dict(id=10, head_sha='a'*40, run_attempt=2)
        self.owner = dict(name='Affected App Deploy — AgentPortal', run_id=10,
            head_sha='a'*40, run_attempt=1, status='completed', conclusion='success',
            steps=[dict(name='Publish or reconcile exact application bytes',status='completed',conclusion='success')])
        self.stack = ExitStack(); self.addCleanup(self.stack.close)
        self.stack.enter_context(patch.dict(os.environ, GITHUB_REPOSITORY='owner/repo', GH_TOKEN='test'))
        self.authority = self.stack.enter_context(patch.object(deploy._RELEASE_AUTHORITY, 'assert_fast_track_release_execution', return_value=self.run))
        self.tree = self.stack.enter_context(patch.object(deploy.subprocess, 'check_output', return_value=''))
        self.api = self.stack.enter_context(patch.object(deploy._RELEASE_AUTHORITY, 'api_get',
            return_value=dict(total_count=1, jobs=[self.owner])))
        self.azure = Mock(); self.azure.observed_revision.return_value='a'*40
        self.provider = self.stack.enter_context(patch.object(deploy, 'target_azure', return_value=self.azure))
        self.rows = self.stack.enter_context(patch.object(deploy, 'read_deployments_bounded', return_value=[row('ok',4)]))
    def call(self, key='client', changed=None, **kwargs):
        deploy.fast_track_crm_portal_gate(key,self.run,'b'*40,
            [self.migration] if changed is None else changed,
            dict(matrix=dict(include=[dict(azure_app=deploy.TARGETS['portal']['releaseName'])])), **kwargs)
    def test_exact_successful_portal_and_fresh_idle_runtime_allow(self):
        for key in ['client','protect','parfait']:
            self.call(key)
        self.provider.assert_called_with('portal',None,'a'*40)
        self.azure.submit.assert_not_called()
    def test_unaffected_and_later_releases_do_not_wait(self):
        self.call('portal'); self.call('website'); self.call(changed=[])
        self.tree.return_value=self.migration; self.call()
        self.api.assert_not_called(); self.provider.assert_not_called()
    def test_wrong_missing_duplicate_or_truncated_owner_blocks(self):
        for field,value in [('run_id',11),('head_sha','c'*40),('run_attempt',3),('run_attempt',True)]:
            with self.subTest(field=field,value=value):
                original=self.owner[field];self.owner[field]=value
                with self.assertRaises(deploy.DeploymentReconciliationRequired):self.call()
                self.owner[field]=original
        for payload in [dict(total_count=0,jobs=[]),dict(total_count=2,jobs=[self.owner]*2),dict(total_count=2,jobs=[self.owner])]:
            self.api.return_value=payload
            with self.assertRaises(deploy.DeploymentReconciliationRequired):self.call()
        self.provider.assert_not_called()
    def test_failed_cancelled_skipped_portal_blocks(self):
        for result in ['failure','cancelled','skipped',None]:
            self.owner['conclusion']=result
            with self.assertRaises(deploy.DeploymentReconciliationRequired):self.call()
        self.provider.assert_not_called()
    def test_missing_or_skipped_publication_step_blocks(self):
        for steps in [[],[dict(name='Publish or reconcile exact application bytes',status='completed',conclusion='skipped')]]:
            self.owner['steps']=steps
            with self.assertRaises(deploy.DeploymentReconciliationRequired):self.call()
        self.provider.assert_not_called()
    def test_first_crm_without_portal_scope_blocks(self):
        with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired,'OUTSIDE_SCOPE'):
            deploy.fast_track_crm_portal_gate('client',self.run,'b'*40,[self.migration],dict(matrix=dict(include=[])))
        self.api.assert_not_called()
    def test_active_provider_or_wrong_runtime_blocks(self):
        for status in [0,1,2]:
            self.rows.return_value=[row('active',status)]
            with self.assertRaises(deploy.DeploymentReconciliationRequired):self.call()
        self.rows.return_value=[row('ok',4)];self.azure.observed_revision.return_value='c'*40
        with self.assertRaises(deploy.DeploymentReconciliationRequired):self.call()
    def test_wait_is_bounded_and_success_can_follow_pending(self):
        self.owner['status']='in_progress';self.owner['conclusion']=None
        sleep=Mock()
        with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired,'WAIT_EXHAUSTED'):
            self.call(clock=Mock(side_effect=[0,900]),sleep=sleep)
        sleep.assert_not_called();self.provider.assert_not_called()
        def finish(_):self.owner.update(status='completed',conclusion='success')
        self.call(clock=lambda:0,sleep=finish)
        self.provider.assert_called_once()
    def test_late_success_and_changed_authority_deny(self):
        now=[0]
        def late_api(*args):
            now[0]=901
            return dict(total_count=1,jobs=[self.owner])
        self.api.side_effect=late_api
        with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired,'WAIT_EXHAUSTED'): self.call(clock=lambda:now[0])
        self.provider.assert_not_called();self.api.side_effect=None
        self.authority.return_value=dict(self.run, head_sha='c'*40)
        with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired,'AUTHORITY_CHANGED'): self.call()
        self.authority.return_value=self.run
        now=[0]
        def late():now[0]=901;return 'a'*40
        self.azure.observed_revision.side_effect=late
        with self.assertRaises(Exception): self.call(clock=lambda:now[0])
    def test_failed_gate_blocks_target_effects_at_actual_entry(self):
        scope=dict(commerce=False,matrix=dict(include=[dict(azure_app=deploy.TARGETS['client']['releaseName'])]))
        with patch.dict(os.environ,BEFORE='b'*40), patch.object(deploy._RELEASE_AUTHORITY,'fast_track_scope',return_value=scope), \
             patch.object(deploy._RELEASE_AUTHORITY,'attested_website_only_recovery_scope',return_value=False), \
             patch.object(deploy._RELEASE_AUTHORITY,'fast_track_source_admission'), \
             patch.object(deploy,'verify_package',return_value='d'*64), \
             patch.object(deploy,'fast_track_crm_portal_gate',side_effect=deploy.DeploymentReconciliationRequired('hold')), \
             patch.object(deploy,'operation_journal') as journal, patch.object(deploy,'reconcile') as reconcile:
            with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired,'hold'):
                deploy.fast_track_deploy('client',Path('/unused'))
            journal.assert_not_called();reconcile.assert_not_called();self.provider.assert_not_called()
    def test_gate_precedes_any_target_journal_or_upload(self):
        import inspect
        source=inspect.getsource(deploy.fast_track_deploy)
        self.assertLess(source.index('fast_track_crm_portal_gate('),source.index('operation_journal('))
        self.assertLess(source.index('fast_track_crm_portal_gate('),source.index('result = reconcile('))


class FakeAzure:
    def __init__(self, states, revisions, accepted=True, static=False, recovery_accepted=True):
        self.states, self.revisions, self.accepted = list(states), list(revisions), accepted
        self.static, self.recovery_accepted = static, recovery_accepted
        self.uploads = 0
        self.recovery_uploads = 0
        self.preparations = 0
        self.now = 0

    def deployments(self):
        state = self.states.pop(0) if len(self.states) > 1 else self.states[0]
        if isinstance(state, Exception):
            raise state
        return state

    def revision_live(self):
        return self.revisions.pop(0) if len(self.revisions) > 1 else self.revisions[0]

    def prepare_submission(self):
        self.preparations += 1

    def submit(self, correlation=None):
        self.uploads += 1
        return self.accepted

    def submit_static_recovery(self):
        if not self.static:
            raise RuntimeError('not static')
        self.recovery_uploads += 1
        return self.recovery_accepted

    def sleep(self, seconds):
        self.now += seconds

    def run(self, **kwargs):
        return deploy.reconcile(
            self,
            clock=lambda: self.now,
            sleep=self.sleep,
            interval=1,
            timeout=10,
            **kwargs,
        )


class RetainedJournal:
    # Provider-only fixtures retain the original successful write intent.
    baseline = None
    intent = {'baselineDeploymentIds': []}
    def __init__(self): self.successes = []
    def record_success(self, ids): self.successes.append(ids)


class SubmissionCorrelationTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location('correlated_journal', ROOT / 'release-operation-evidence.py')
        self.evidence = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.evidence)
        self.records, self.events = {}, []
        self.cli_help = subprocess.CompletedProcess([], 0, '    --deploymentTag : Linux only.\n', '')
        self.host = subprocess.CompletedProcess([], 0, '{"name":"masterapp-protect","reserved":true,"kind":"app,linux"}', '')
        self.upload_result = subprocess.CompletedProcess([], 0, '{}', '')
        self.intent_readback_lost = False
        self.authority = type('Authority', (), {
            'RELEASE_TARGETS': deploy.TARGETS,
            'ReleaseOperationHistoryUnproven': deploy._RELEASE_AUTHORITY.ReleaseOperationHistoryUnproven})()
        self.authority.release_operation_history = lambda repo, operation, *args, phase='intent': (
            json.loads(json.dumps(self.records[(phase, operation)])) if (phase, operation) in self.records else None)

    def journal(self, target='protect'):
        return self.evidence.OperationJournal(target=target, application_revision='a' * 40,
            package_digest='b' * 64, baseline='c' * 40, authority=self.authority,
            environment=dict(GITHUB_RUN_ID='10', GITHUB_RUN_ATTEMPT='1', PACKAGE_PRODUCER_RUN='10',
                             GITHUB_REPOSITORY='owner/repo', GITHUB_TOKEN='synthetic'))

    def command(self, args, **kwargs):
        if args == ['az', 'webapp', 'deploy', '--help']:
            self.events.append('cli-support')
            if isinstance(self.cli_help, Exception):
                raise self.cli_help
            return self.cli_help
        if args[:3] == ['az', 'webapp', 'show']:
            self.events.append('linux-host')
            self.assertEqual('{name:name,reserved:reserved,kind:kind}', args[args.index('--query') + 1])
            self.assertEqual('masterapp-rg', args[args.index('-g') + 1])
            if isinstance(self.host, Exception):
                raise self.host
            return self.host
        if args[0] == 'node':
            payload = json.loads(kwargs['input'])
            record = payload['record']
            self.records[(record['phase'], record['operationId'])] = record
            self.events.append(record['phase'] + '-readback')
            if record['phase'] == 'intent' and self.intent_readback_lost:
                return subprocess.CompletedProcess([], 1, 'DO_NOT_ECHO_SIGNED_URL', 'DO_NOT_ECHO_TOKEN')
            return subprocess.CompletedProcess([], 0, 'LEGEND_OPERATION_RESULT={"artifactId":12}\n', '')
        if args[:3] == ['az', 'webapp', 'deploy']:
            self.events.append('upload')
            if '--deploymentTag' in args:
                tag = args[args.index('--deploymentTag') + 1]
                intent = self.records[('intent', tag)]
                self.assertEqual({'kind': 'azure_onedeploy_deployment_tag_v1', 'value': tag}, intent['providerRequestCorrelation'])
            else:
                intents = [record for (phase, _), record in self.records.items() if phase == 'intent']
                self.assertEqual(1, len(intents))
                intent = intents[0]
                self.assertEqual({'kind': 'azure_onedeploy_windows_intent_v1', 'value': intent['operationId']}, intent['submissionContract'])
                self.assertNotIn('providerRequestCorrelation', intent)
                self.assertEqual('zip', args[args.index('--type') + 1])
                self.assertEqual('true', args[args.index('--clean') + 1])
                self.assertEqual('false', args[args.index('--enable-kudu-warmup') + 1])
            self.assertEqual(deploy.TARGETS[intent['target']]['releaseName'], args[args.index('-n') + 1])
            self.assertLess(self.events.index('intent-readback'), self.events.index('upload'))
            if isinstance(self.upload_result, Exception):
                raise self.upload_result
            return self.upload_result
        raise AssertionError('Unexpected subprocess boundary')

    def reconcile(self, journal, *, completes=True):
        key = journal.identity['target']
        azure = deploy.target_azure(key, Path('/immutable.zip'), 'a' * 40)
        rows = [[row('old', 4)], [row('old', 4), row('fresh', 4)], [row('old', 4), row('fresh', 4)]] if completes else None
        revisions = ['c' * 40, 'a' * 40, 'a' * 40] if completes else None
        now = [0]
        def sleep(seconds):
            now[0] += seconds
        with patch.object(azure, 'deployments', side_effect=rows, return_value=[row('old', 4)]), \
             patch.object(azure, 'observed_revision', side_effect=revisions, return_value='c' * 40), \
             patch.object(deploy.subprocess, 'run', side_effect=self.command):
            return deploy.reconcile(azure, journal=journal, baseline='c' * 40,
                clock=lambda: now[0], sleep=sleep, interval=1, timeout=10)

    def test_actual_intent_readback_precedes_tagged_upload_and_success_keeps_same_correlation(self):
        for key in deploy.TARGETS:
            with self.subTest(target=key):
                self.events.clear()
                self.host.stdout = json.dumps({'name': deploy.TARGETS[key]['releaseName'], 'reserved': True,
                                              'kind': 'app,linux,container' if key == 'website' else 'app,linux'})
                journal = self.journal(key)
                self.assertEqual('deployed', self.reconcile(journal))
                self.assertEqual(['cli-support', 'linux-host', 'intent-readback', 'upload', 'success-readback'], self.events)
                intent = self.records[('intent', journal.operation_id)]
                receipt = self.records[('success', journal.operation_id)]
                self.assertEqual(intent['providerRequestCorrelation'], receipt['providerRequestCorrelation'])
                self.assertEqual(['fresh'], receipt['deploymentIds'])

    def test_lost_durable_readback_blocks_upload_and_restored_intent_does_not_repeat_capability_reads(self):
        journal = self.journal()
        self.intent_readback_lost = True
        with self.assertRaises(deploy.DeploymentSubmissionNotEntered) as error:
            self.reconcile(journal)
        self.assertNotIn('DO_NOT_ECHO', str(error.exception))
        self.assertEqual(['cli-support', 'linux-host', 'intent-readback'], self.events)
        self.events.clear()
        retained = json.dumps(self.records[('intent', journal.operation_id)], sort_keys=True)
        with self.assertRaises(deploy.DeploymentReconciliationRequired):
            self.reconcile(self.journal(), completes=False)
        self.assertEqual([], self.events)
        self.assertEqual(retained, json.dumps(self.records[('intent', journal.operation_id)], sort_keys=True))

    def test_upload_timeout_preserves_tagged_intent_without_replay_or_success_claim(self):
        self.upload_result = subprocess.TimeoutExpired(['az'], 180, output='DO_NOT_ECHO', stderr='DO_NOT_ECHO')
        journal = self.journal()
        with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired, 'deadline'):
            self.reconcile(journal, completes=False)
        self.assertEqual(1, self.events.count('upload'))
        self.assertNotIn(('success', journal.operation_id), self.records)
        self.events.clear()
        with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired, 'deadline'):
            self.reconcile(self.journal(), completes=False)
        self.assertEqual([], self.events)

    def test_legacy_intent_reconciles_without_cli_capability_or_retrospective_tag(self):
        journal = self.journal()
        with patch.object(deploy.subprocess, 'run', side_effect=self.command):
            journal.before_submit(['old'])
        intent = self.records[('intent', journal.operation_id)]
        del intent['providerRequestCorrelation']
        original = json.dumps(intent, sort_keys=True)
        self.events.clear()
        self.cli_help = AssertionError('Existing intent must not require fresh-submit support')
        self.host = AssertionError('Existing intent must not recheck fresh-submit host support')
        self.assertEqual('preserved', self.reconcile(self.journal()))
        self.assertEqual(['success-readback'], self.events)
        self.assertEqual(original, json.dumps(self.records[('intent', journal.operation_id)], sort_keys=True))
        self.assertNotIn('providerRequestCorrelation', self.records[('success', journal.operation_id)])

    def test_correlation_owner_diff_does_not_select_any_production_target(self):
        result = deploy._RELEASE_AUTHORITY.fast_track_scope([
            'scripts/deploy-approved-app.py', 'scripts/release-operation-evidence.py',
            'scripts/test-deploy-approved-app.py', 'scripts/test-release-operation-evidence.py',
            'scripts/validation-resume.py', 'scripts/release-artifacts/transport.cjs',
            'scripts/release-artifacts/test-transport.cjs'])
        self.assertEqual({'include': []}, result['matrix'])
        for flag in ('selected', 'commerce', 'schema', 'routing'):
            self.assertFalse(result[flag])

    def test_unsupported_cli_option_fails_before_host_read_intent_or_upload(self):
        for text in ['    --tag : deprecated\n', '    --deploymenttag : wrong casing\n',
                     'This installation might mention --deploymentTag without registering it.\n']:
            with self.subTest(help=text):
                self.events.clear()
                self.cli_help.stdout = text
                with self.assertRaises(deploy.DeploymentSubmissionNotEntered):
                    self.reconcile(self.journal())
                self.assertEqual(['cli-support'], self.events)
                self.assertEqual({}, self.records)

    def test_nonlinux_foreign_or_invalid_metadata_fails_before_intent_or_upload(self):
        for host in [{'name': 'masterapp-protect', 'reserved': False, 'kind': 'app,linux'},
                     {'name': 'masterapp-protect', 'reserved': 'true', 'kind': 'app,linux'},
                     {'name': 'another-app', 'reserved': True, 'kind': 'app,linux'},
                     *[{'name': 'masterapp-protect', 'reserved': True, 'kind': kind}
                       for kind in [None, 'app', 'functionapp,linux', 'app,linux,functionapp',
                                    'app,linux,workflowapp', 'app,Linux', 'unrelatedlinux', ['app', 'linux']]],
                     {}, [], None, 'DO_NOT_ECHO']:
            with self.subTest(host=host):
                self.events.clear()
                self.host.stdout = json.dumps(host)
                with self.assertRaises(deploy.DeploymentSubmissionNotEntered):
                    self.reconcile(self.journal())
                self.assertEqual(['cli-support', 'linux-host'], self.events)
                self.assertEqual({}, self.records)

    def test_capability_timeout_or_provider_error_has_no_new_intent_and_no_secret_output(self):
        original_help, original_host = self.cli_help, self.host
        failure = subprocess.CompletedProcess([], 1, 'DO_NOT_ECHO', 'DO_NOT_ECHO')
        timeout = subprocess.TimeoutExpired(['secret'], 20, output='DO_NOT_ECHO', stderr='DO_NOT_ECHO')
        for boundary in ['cli_help', 'host']:
            for result in [failure, timeout]:
                with self.subTest(boundary=boundary, outcome=type(result).__name__):
                    self.cli_help, self.host = original_help, original_host
                    setattr(self, boundary, result)
                    self.events.clear()
                    with patch('builtins.print') as output, self.assertRaises(deploy.DeploymentSubmissionNotEntered) as error:
                        self.reconcile(self.journal())
                    self.assertEqual({}, self.records)
                    self.assertNotIn('upload', self.events)
                    self.assertNotIn('DO_NOT_ECHO', str(error.exception) + str(output.call_args_list))

    def test_direct_submit_rejects_missing_capability_or_invalid_tag_without_command(self):
        azure = deploy.target_azure('protect', Path('/immutable.zip'), 'a' * 40)
        with patch.object(deploy.subprocess, 'run') as command, self.assertRaises(deploy.DeploymentSubmissionNotEntered):
            azure.submit(CORRELATION)
        command.assert_not_called()
        prepare_supported_azure(azure)
        for correlation in [None, {}, {'kind': 'other', 'value': 'c' * 64},
                            dict(CORRELATION, extra='DO_NOT_ECHO'), dict(CORRELATION, value='x?token=DO_NOT_ECHO')]:
            with self.subTest(correlation=correlation), patch.object(deploy.subprocess, 'run') as command, \
                    self.assertRaises(deploy.DeploymentSubmissionNotEntered):
                azure.submit(correlation)
            command.assert_not_called()

    def windows_host(self):
        self.host.stdout = json.dumps({'name': 'masterapp-protect', 'reserved': False, 'kind': 'app'})

    def test_windows_one_upload_has_truthful_intent_and_durable_provider_observation(self):
        self.windows_host()
        # Azure CLI 2.91.0 _check_zip_deployment_status returns Kudu's latest
        # deployment object after status=4, not the original POST response.
        self.upload_result.stdout = json.dumps({'id': 'fresh', 'status': 4,
            'complete': True, 'active': True, 'deployer': 'OneDeploy',
            'message': 'OneDeploy', 'end_time': '2026-10-20T04:15:31Z'})
        journal = self.journal()
        self.assertEqual('deployed', self.reconcile(journal))
        self.assertEqual(['cli-support', 'linux-host', 'intent-readback', 'upload',
                          'submitted-readback', 'success-readback'], self.events)
        self.assertNotIn('providerRequestCorrelation', journal.intent)
        self.assertEqual('fresh', self.records[('submitted', journal.operation_id)]['deploymentId'])
        self.assertEqual(['fresh'], self.records[('success', journal.operation_id)]['deploymentIds'])

    def test_windows_lost_reply_retains_intent_and_never_uploads_again(self):
        self.windows_host()
        self.upload_result = subprocess.TimeoutExpired(['az'], 180)
        journal = self.journal()
        with self.assertRaises(deploy.DeploymentReconciliationRequired):
            self.reconcile(journal, completes=False)
        self.assertEqual(1, self.events.count('upload'))
        self.assertNotIn(('submitted', journal.operation_id), self.records)
        self.events.clear()
        with self.assertRaises(deploy.DeploymentReconciliationRequired):
            self.reconcile(self.journal(), completes=False)
        self.assertEqual([], self.events)

    def test_windows_returned_identity_cannot_be_replaced_by_unrelated_live_success(self):
        self.windows_host()
        self.upload_result.stdout = '{"id":"different"}'
        with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired, 'receipt unavailable'):
            self.reconcile(self.journal())
        self.assertEqual(1, self.events.count('upload'))
        self.assertFalse(any(phase == 'success' for phase, _ in self.records))

    def test_windows_foreign_or_function_host_stays_blocked_before_intent(self):
        for name, kind in [('another-app', 'app'), ('masterapp-protect', 'functionapp'),
                           ('masterapp-protect', 'app,workflowapp'), ('masterapp-protect', 'app,linux')]:
            with self.subTest(name=name, kind=kind):
                self.host.stdout = json.dumps({'name': name, 'reserved': False, 'kind': kind})
                with self.assertRaises(deploy.DeploymentSubmissionNotEntered):
                    self.reconcile(self.journal())
                self.assertEqual({}, self.records)


class AzureDeploymentObservationTests(unittest.TestCase):
    revision = '71ccfc080166aff985718f48a99e22cdaa84e209'
    operation = '780c67e3fd7a4559951fde66bd1a657f'

    def setUp(self):
        environment = patch.dict(os.environ, {'GITHUB_ACTIONS': 'false'})
        environment.start()
        self.addCleanup(environment.stop)

    def command(self, payload, *, code=0):
        return subprocess.CompletedProcess([], code, json.dumps(payload), 'DO_NOT_ECHO_STDERR')

    def child(self, suffix='detail1'):
        return 'https://masterapp-client.scm.azurewebsites.net/api/deployments/' + self.operation + '/log/' + suffix

    def test_invalid_identity_never_reaches_any_provider_read(self):
        with patch.object(deploy.subprocess, 'run') as command, patch.object(deploy.Azure, 'observed_revision') as runtime:
            for target, revision, operation in [
                ('foreign-app', self.revision, self.operation), ('client', 'main', self.operation),
                ('client', self.revision, 'latest'), ('client', self.revision, '../other'),
                ('client', self.revision, self.operation + '?token=secret'),
                ('client', self.revision, ' '), ('client', self.revision, [])]:
                with self.subTest(target=target, operation=operation), self.assertRaises(ValueError):
                    deploy.observe_azure_deployment(target, revision, operation)
            command.assert_not_called()
            runtime.assert_not_called()

    def test_workflow_identity_is_checked_before_azure_login_or_read(self):
        valid = {'GITHUB_ACTIONS': 'true', 'GITHUB_EVENT_NAME': 'workflow_dispatch',
                 'GITHUB_REF': 'refs/heads/Legend/fast-track-release', 'GITHUB_SHA': 'a' * 40,
                 'GITHUB_REPOSITORY': 'MYLEGND/masterapp',
                 'GITHUB_WORKFLOW_REF': 'MYLEGND/masterapp/.github/workflows/deployment-diagnostics.yml@refs/heads/Legend/fast-track-release'}
        with patch.dict(os.environ, valid), patch.object(deploy.subprocess, 'run') as command:
            result = deploy.observe_azure_deployment('client', self.revision, self.operation, validate_only=True)
            self.assertEqual('INPUTS_VALIDATED', result['readStatus'])
            for name, bad in [('GITHUB_EVENT_NAME', 'push'), ('GITHUB_REF', 'refs/heads/foreign'),
                              ('GITHUB_WORKFLOW_REF', valid['GITHUB_WORKFLOW_REF'].replace('deployment-diagnostics', 'another'))]:
                with self.subTest(name=name), patch.dict(os.environ, {name: bad}), self.assertRaises(ValueError):
                    deploy.observe_azure_deployment('client', self.revision, self.operation, validate_only=True)
            command.assert_not_called()

    def test_exact_operation_uses_canonical_target_and_does_not_echo_provider_text(self):
        message = 'Operation timed out. Authorization: Bearer DO_NOT_ECHO_TOKEN; Password=DO_NOT_ECHO_PASSWORD'
        rows = [{'type': 2, 'log_time': '2026-10-20T04:12:00Z', 'message': message}]
        with patch.object(deploy.subprocess, 'run', return_value=self.command(rows)) as command:
            result = deploy.observe_azure_deployment('client', self.revision, self.operation)
        self.assertEqual(['TIMEOUT_ERROR'], result['reasonCodes'])
        self.assertEqual(hashlib.sha256(message.encode()).hexdigest(), result['entries'][0]['messageSha256'])
        self.assertEqual('2026-10-20T04:12:00+00:00', result['entries'][0]['time'])
        self.assertNotIn('DO_NOT_ECHO', json.dumps(result))
        self.assertFalse(result['releaseProof'])
        self.assertFalse(result['authorizesMutation'])
        self.assertTrue(result['reasonCodesAreTextMatches'])
        self.assertEqual(['az', 'webapp', 'log', 'deployment', 'show', '-g', 'masterapp-rg',
            '-n', 'masterapp-client', '--deployment-id', self.operation, '--only-show-errors', '-o', 'json'],
            command.call_args.args[0])
        self.assertEqual(30, command.call_args.kwargs['timeout'])
        command.assert_called_once()

    def test_unknown_provider_detail_is_not_guessed_or_echoed(self):
        with patch.object(deploy.subprocess, 'run', return_value=self.command([
                {'type': 'secret-type', 'log_time': 'DO_NOT_ECHO_TIME', 'message': 'DO_NOT_ECHO_UNFAMILIAR_PROVIDER_REASON'}])):
            result = deploy.observe_azure_deployment('client', self.revision, self.operation)
        self.assertEqual(['NO_CLASSIFIED_FAILURE_DETAIL'], result['reasonCodes'])
        self.assertIsNone(result['entries'][0]['type'])
        self.assertIsNone(result['entries'][0]['time'])
        self.assertNotIn('DO_NOT_ECHO', json.dumps(result))

    def test_timeout_is_one_read_without_retry_and_hides_exception_payload(self):
        error = subprocess.TimeoutExpired(['secret-command'], 30, output='DO_NOT_ECHO_BODY', stderr='DO_NOT_ECHO_TOKEN')
        with patch.object(deploy.subprocess, 'run', side_effect=error) as command:
            result = deploy.observe_azure_deployment('client', self.revision, self.operation)
        self.assertEqual('READ_TIMEOUT', result['readStatus'])
        self.assertNotIn('DO_NOT_ECHO', json.dumps(result))
        command.assert_called_once()

    def test_invalid_oversized_or_failed_log_read_never_relays_payload(self):
        results = [self.command({'message': 'DO_NOT_ECHO'}), self.command([{'message': {'token': 'DO_NOT_ECHO'}}]),
                   self.command([], code=1), subprocess.CompletedProcess([], 0, 'DO_NOT_ECHO_NOT_JSON', 'secret'),
                   self.command([{'message': 'DO_NOT_ECHO' * deploy.DIAGNOSTIC_MAX_BYTES}])]
        for response in results:
            with self.subTest(size=len(response.stdout)), patch.object(deploy.subprocess, 'run', return_value=response) as command:
                result = deploy.observe_azure_deployment('client', self.revision, self.operation)
                self.assertEqual('READ_UNAVAILABLE_OR_INVALID', result['readStatus'])
                self.assertNotIn('DO_NOT_ECHO', json.dumps(result))
                command.assert_called_once()

    def test_untrusted_detail_links_never_request_a_token_or_send_a_get(self):
        root = self.child()
        links = [root.replace('masterapp-client', 'foreign'), root.replace(self.operation, 'anotheroperation'),
                 root + '?sig=DO_NOT_ECHO', root + '#fragment', root.replace('https:', 'http:'),
                 root.replace('.net/', '.net:443/'), root.replace('https://', 'https://user:secret@'),
                 root.replace('/detail1', '/../another'), root.replace('/detail1', '/%2e%2e'), '\n' + root,
                 'https://[invalid', 123]
        with patch.object(deploy.subprocess, 'run', return_value=self.command([
                {'message': 'Deployment failed', 'details_url': url} for url in links])) as command, \
                patch.object(deploy.urllib.request, 'build_opener') as opener:
            result = deploy.observe_azure_deployment('client', self.revision, self.operation)
        self.assertEqual(len(links), result['rejectedDetailLinks'])
        self.assertEqual([], result['nestedReadStates'])
        self.assertNotIn('https:', json.dumps(result))
        command.assert_called_once()
        opener.assert_not_called()

    def test_nested_get_uses_existing_bearer_only_for_exact_origin_operation_and_bounded_links(self):
        links = [self.child('detail' + str(i)) for i in range(3)]
        primary = self.command([{'message': 'Deployment failed', 'details_url': url} for url in links + links[:1]])
        token = subprocess.CompletedProcess([], 0, 'synthetic.DO_NOT_ECHO_BEARER.signature', '')
        opener = Mock()
        opener.open.side_effect = [self.response(url, [{'type': 2, 'message': 'No space left on device /DO_NOT_ECHO_PATH'}])
                                   for url in links[:2]]
        with patch.object(deploy.subprocess, 'run', side_effect=[primary, token]) as command, \
                patch.object(deploy.urllib.request, 'build_opener', return_value=opener) as build:
            result = deploy.observe_azure_deployment('client', self.revision, self.operation)
        self.assertTrue(result['detailsTruncated'])
        self.assertIn('DISK_SPACE_ERROR', result['reasonCodes'])
        self.assertEqual(['OBSERVED', 'OBSERVED'], result['nestedReadStates'])
        self.assertEqual(2, command.call_count)
        self.assertEqual(2, opener.open.call_count)
        self.assertIsInstance(build.call_args.args[0], deploy._DiagnosticNoRedirect)
        for call, url in zip(opener.open.call_args_list, links):
            request = call.args[0]
            self.assertEqual('GET', request.get_method())
            self.assertEqual(url, request.full_url)
            self.assertEqual('Bearer ' + token.stdout, request.get_header('Authorization'))
        self.assertNotIn('DO_NOT_ECHO', json.dumps(result))
        self.assertNotIn('https:', json.dumps(result))

    def test_redirect_and_missing_auth_stay_unverified_without_following_or_leaking(self):
        primary = self.command([{'message': '', 'details_url': self.child()}])
        denied = subprocess.CompletedProcess([], 1, 'DO_NOT_ECHO_TOKEN', 'DO_NOT_ECHO_ERROR')
        with patch.object(deploy.subprocess, 'run', side_effect=[primary, denied]), \
                patch.object(deploy.urllib.request, 'build_opener') as build:
            result = deploy.observe_azure_deployment('client', self.revision, self.operation)
        self.assertEqual(['AUTHENTICATED_READ_UNAVAILABLE'], result['nestedReadStates'])
        build.assert_not_called()
        self.assertIsNone(deploy._DiagnosticNoRedirect().redirect_request(None, None, 302, None, None, 'https://foreign.invalid'))
        opener = Mock()
        opener.open.side_effect = deploy.urllib.error.HTTPError('https://DO_NOT_ECHO.invalid', 302, 'secret', {}, None)
        with patch.object(deploy.subprocess, 'run', side_effect=[primary, subprocess.CompletedProcess([], 0, 'synthetic.token', '')]), \
                patch.object(deploy.urllib.request, 'build_opener', return_value=opener):
            result = deploy.observe_azure_deployment('client', self.revision, self.operation)
        self.assertEqual(['REDIRECT_REFUSED'], result['nestedReadStates'])
        self.assertNotIn('DO_NOT_ECHO', json.dumps(result))
        opener.open.assert_called_once()

    def test_nested_network_boundary_rechecks_scope_before_credentials(self):
        with patch.object(deploy.subprocess, 'run') as command:
            entries, state = deploy._diagnostic_nested_logs(['https://foreign.invalid'], 'masterapp-client', self.operation)
        self.assertEqual([], entries)
        self.assertEqual(['DETAIL_SCOPE_REJECTED'], state)
        command.assert_not_called()

    def test_nested_response_identity_size_and_content_remain_bounded(self):
        wrong_origin = self.response('https://foreign.invalid/DO_NOT_ECHO', [])
        too_large = self.response(self.child(), [{'message': 'DO_NOT_ECHO' * deploy.DIAGNOSTIC_MAX_BYTES}])
        invalid = self.response(self.child(), {'message': 'DO_NOT_ECHO'})
        cases = [(wrong_origin, 'RESPONSE_IDENTITY_UNPROVEN'), (too_large, 'RESPONSE_TOO_LARGE'),
                 (invalid, 'READ_UNAVAILABLE_OR_INVALID')]
        for response, expected in cases:
            opener = Mock()
            opener.open.return_value = response
            with self.subTest(expected=expected), patch.object(deploy.subprocess, 'run', return_value=
                    subprocess.CompletedProcess([], 0, 'synthetic.token', 'DO_NOT_ECHO')), \
                    patch.object(deploy.urllib.request, 'build_opener', return_value=opener):
                entries, states = deploy._diagnostic_nested_logs([self.child()], 'masterapp-client', self.operation)
            self.assertEqual([], entries)
            self.assertEqual([expected], states)
            opener.open.assert_called_once()
            self.assertEqual(15, opener.open.call_args.kwargs['timeout'])
            if expected == 'RESPONSE_IDENTITY_UNPROVEN':
                response.read.assert_not_called()
            else:
                response.read.assert_called_once_with(deploy.DIAGNOSTIC_MAX_BYTES + 1)
            self.assertNotIn('DO_NOT_ECHO', json.dumps([entries, states]))

    def test_child_log_does_not_recursively_follow_untrusted_provider_links(self):
        opener = Mock()
        opener.open.return_value = self.response(self.child(), [dict(message='Deployment failed',
            details_url='https://foreign.invalid/DO_NOT_ECHO')])
        with patch.object(deploy.subprocess, 'run', return_value=
                subprocess.CompletedProcess([], 0, 'synthetic.token', '')), \
                patch.object(deploy.urllib.request, 'build_opener', return_value=opener):
            entries, states = deploy._diagnostic_nested_logs([self.child()], 'masterapp-client', self.operation)
        self.assertEqual(['OBSERVED_WITH_UNFOLLOWED_DETAILS'], states)
        self.assertEqual(['PROVIDER_FAILURE_MARKER'], entries[0]['reasonCodes'])
        self.assertNotIn('DO_NOT_ECHO', json.dumps([entries, states]))
        opener.open.assert_called_once()

    def test_no_id_reports_bounded_history_and_runtime_without_proving_nonentry(self):
        rows = [dict(id='provider' + str(i), status=3 if i == 0 else 4,
                     received_time='2026-10-20T04:00:00Z', message='DO_NOT_ECHO_PROVIDER_TEXT') for i in range(25)]
        with patch.object(deploy, 'deployment_read_process', return_value=self.command(rows)) as command, \
                patch.object(deploy.Azure, 'observed_revision', return_value=self.revision) as runtime:
            result = deploy.observe_azure_deployment('client', self.revision)
        self.assertEqual('history', result['mode'])
        self.assertEqual(20, len(result['deployments']))
        self.assertEqual(3, result['deployments'][0]['status'])
        self.assertEqual(self.revision, result['observedRuntimeRevision'])
        self.assertTrue(result['outputTruncated'])
        self.assertFalse(result['historyIsComplete'])
        self.assertFalse(result['emptyHistoryProvesNonEntry'])
        self.assertFalse(result['releaseProof'])
        self.assertNotIn('DO_NOT_ECHO', json.dumps(result))
        self.assertEqual(['az', 'webapp', 'log', 'deployment', 'list'], command.call_args.args[0][:5])
        command.assert_called_once()
        runtime.assert_called_once()

    def test_unknown_history_status_is_not_relabelled_as_a_known_provider_state(self):
        for value in [7, 'secret-status', True]:
            with self.subTest(status=value), patch.object(deploy, 'deployment_read_process', return_value=
                    self.command([dict(id='operation1', status=value, message='DO_NOT_ECHO')])), \
                    patch.object(deploy.Azure, 'observed_revision', return_value=None):
                result = deploy.observe_azure_deployment('protect', self.revision)
            self.assertEqual('READ_UNAVAILABLE_OR_INVALID', result['readStatus'])
            self.assertEqual([], result['deployments'])
            self.assertNotIn('DO_NOT_ECHO', json.dumps(result))
            self.assertFalse(result['releaseProof'])

    def test_empty_or_unavailable_history_does_not_authorize_a_new_upload(self):
        for response in [self.command([]), subprocess.TimeoutExpired(['secret'], 20)]:
            with self.subTest(response=type(response).__name__), \
                    patch.object(deploy, 'deployment_read_process', side_effect=response if isinstance(response, Exception) else None,
                                 return_value=response), patch.object(deploy.Azure, 'observed_revision', return_value='b' * 40):
                result = deploy.observe_azure_deployment('protect', self.revision, '')
            self.assertEqual([], result['deployments'])
            self.assertFalse(result['emptyHistoryProvesNonEntry'])
            self.assertFalse(result['authorizesMutation'])
            self.assertEqual('b' * 40, result['observedRuntimeRevision'])

    def test_cli_observation_cannot_enter_any_existing_mutation_authority(self):
        for operation in [self.operation, '']:
            args = ['deploy-approved-app.py', '--observe-azure-deployment', 'client',
                    '--observation-revision', self.revision, '--deployment-id', operation]
            output = io.StringIO()
            forbidden = {name: Mock(side_effect=AssertionError('Mutation authority entered')) for name in
                ('operation_journal', 'reconcile', 'fast_track_deploy', 'prepare_transaction',
                 'publish_prepared_targets_parallel', 'transaction_disposition')}
            with self.subTest(operation=operation), patch('sys.argv', args), patch('sys.stdout', output), \
                    patch.multiple(deploy, **forbidden), patch.object(deploy.Azure, 'submit') as submit, \
                    patch.object(deploy._RELEASE_AUTHORITY, 'assert_protected_release_execution') as admission, \
                    patch.object(deploy.subprocess, 'run', return_value=self.command([])), \
                    patch.object(deploy, 'deployment_read_process', return_value=self.command([])), \
                    patch.object(deploy.Azure, 'observed_revision', return_value=self.revision):
                deploy.main()
                submit.assert_not_called()
                admission.assert_not_called()
            for method in forbidden.values(): method.assert_not_called()
            self.assertFalse(json.loads(output.getvalue().split(':', 1)[1])['authorizesMutation'])

    def test_diagnostic_only_diff_has_no_application_schema_or_routing_impact(self):
        paths = ['.github/workflows/deployment-diagnostics.yml', 'scripts/deploy-approved-app.py',
                 'scripts/test-deploy-approved-app.py']
        result = deploy._RELEASE_AUTHORITY.fast_track_scope(paths)
        self.assertEqual({'include': []}, result['matrix'])
        for flag in ('selected', 'commerce', 'schema', 'routing'): self.assertFalse(result[flag])
        workflow = (ROOT.parent / paths[0]).read_text()
        jobs = deploy._RELEASE_AUTHORITY._job_blocks(workflow)
        self.assertIn("inputs.scope == 'azure_deployment' && github.ref == 'refs/heads/Legend/fast-track-release'", jobs['azure-deployment'])
        self.assertIn("github.ref == 'refs/heads/legend/approved-changes'", jobs['inspect'])
        self.assertIn("github.ref == 'refs/heads/legend/approved-changes'", jobs['migration-inventory'])
        self.assertLess(jobs['azure-deployment'].index('--validate-observation-only'), jobs['azure-deployment'].index('uses: azure/login@'))
        self.assertNotIn('actions: write', jobs['azure-deployment'])

    @staticmethod
    def response(url, rows):
        body = json.dumps(rows).encode()
        response = Mock(status=200)
        response.geturl.return_value = url
        response.read.side_effect = lambda limit: body[:limit]
        response.__enter__ = Mock(return_value=response)
        response.__exit__ = Mock(return_value=None)
        return response


class RecoveryInitialReadHoldTests(unittest.TestCase):
    def run_failure(self, *, inherited=False, no_journal=False, publication_failure=False, after_submit=False, failure=None):
        from types import SimpleNamespace
        azure = FakeAzure([[]], [False])
        journal = SimpleNamespace(baseline=None, intent={'baselineDeploymentIds': []} if inherited else None)
        failure = failure or deploy.DeploymentStatusUnavailable('read unavailable')
        callback = Mock(side_effect=RuntimeError('ambiguous publication') if publication_failure else None)
        def before_submit(ids, **kwargs):
            journal.intent = {'baselineDeploymentIds': ids}
            return True
        journal.before_submit = before_submit
        journal.submission_correlation = lambda: CORRELATION
        journal.submission_contract = lambda: None
        reads = [[], failure] if after_submit else failure
        with patch.object(deploy, 'read_deployments_bounded', side_effect=reads), self.assertRaises(type(failure)) as raised:
            azure.run(journal=None if no_journal else journal, require_receipt=False, before_intent_read_failure=callback)
        self.assertIs(raised.exception, failure)
        self.assertEqual(callback.call_count, 0 if inherited or no_journal or after_submit or not isinstance(failure, deploy.DeploymentStatusUnavailable) else 1)
        self.assertEqual(azure.uploads, 1 if after_submit else 0)
        if not after_submit:self.assertEqual(azure.preparations, 0)
    def test_initial_read_failure_calls_once_without_entering_intent(self):self.run_failure()
    def test_inherited_intent_never_emits_nonentry(self):self.run_failure(inherited=True)
    def test_no_journal_never_emits_nonentry(self):self.run_failure(no_journal=True)
    def test_ambiguous_hold_publication_preserves_original_failure(self):self.run_failure(publication_failure=True)
    def test_after_submission_never_emits_nonentry(self):self.run_failure(after_submit=True)
    def test_drift_is_not_a_read_hold(self):self.run_failure(failure=deploy.DeploymentDrift('drift'))


class ReconciliationTests(unittest.TestCase):
    def test_matching_live_revision_without_artifact_proof_blocks_without_upload(self):
        azure = FakeAzure([[row('old', 4)]], [True])
        with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired, 'artifact publication intent'):
            azure.run()
        self.assertEqual(0, azure.uploads)

    def test_504_and_active_deployment_are_polled_never_resubmitted(self):
        azure = FakeAzure([[], [row('new', 1)], [row('new', 2)], [row('new', 4)]],
                          [False, None, False, True, True], accepted=False)
        self.assertEqual('deployed', azure.run())
        self.assertEqual(1, azure.uploads)

    def test_preexisting_active_candidate_finishes_without_any_upload(self):
        azure = FakeAzure([[row('pending', 1)], [row('pending', 4)]], [False, True, True])
        self.assertEqual('preserved', azure.run(journal=RetainedJournal()))
        self.assertEqual(0, azure.uploads)

    def test_terminal_failure_stays_failed_even_if_runtime_responds(self):
        azure = FakeAzure([[], [row('failed', 3)]], [False, True])
        with self.assertRaisesRegex(RuntimeError, 'failed'):
            azure.run()
        self.assertEqual(1, azure.uploads)

    def test_retained_failed_upload_cannot_certify_matching_source_without_artifact_proof(self):
        class Journal:
            baseline = 'b' * 40
            history_error = None
            intent = {'baselineDeploymentIds': ['old']}
            def __init__(self):
                self.successes = []
            def record_success(self, deployment_ids):
                self.successes.append(list(deployment_ids))

        azure = FakeAzure(
            [[row('old', 4), row('failed', 3)]],
            [True],
        )
        azure.revision = 'a' * 40
        azure.observed_revision = lambda: 'a' * 40
        journal = Journal()

        with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired, 'does not prove'):
            azure.run(baseline='b' * 40, reconcile_only=True, journal=journal)
        self.assertEqual(0, azure.uploads)
        self.assertEqual([], journal.successes)

    def test_exact_live_receipt_transport_failure_is_deferred_without_upload_replay(self):
        class Journal:
            baseline = 'b' * 40
            history_error = None
            intent = None

            def __init__(self):
                self.receipt_attempts = 0

            def before_submit(self, baseline_ids, *, allow_recovered_baseline=False):
                self.intent = {'baselineDeploymentIds': sorted(baseline_ids)}
                return True

            def submission_correlation(self):
                return dict(CORRELATION)

            def record_success(self, deployment_ids):
                self.receipt_attempts += 1
                if self.receipt_attempts == 1:
                    raise RuntimeError('receipt transport unavailable')
                return {'phase': 'success'}

        journal = Journal()
        first = FakeAzure(
            [[row('old', 4)], [row('old', 4), row('fresh', 4)]],
            [False, True, True],
        )
        first.revision = 'a' * 40
        observed = iter(['b' * 40, 'a' * 40, 'a' * 40])
        first.observed_revision = lambda: next(observed)

        self.assertEqual(
            'deployed-receipt-pending',
            deploy.reconcile(
                first,
                baseline='b' * 40,
                journal=journal,
                require_receipt=False,
                clock=lambda: first.now,
                sleep=first.sleep,
                interval=1,
                timeout=10,
            ),
        )
        self.assertEqual(1, first.uploads)

        # The final read-only reconciliation must retry only the receipt. It
        # observes the exact candidate and never submits the package again.
        second = FakeAzure(
            [[row('old', 4), row('fresh', 4)]],
            [True, True],
        )
        second.revision = 'a' * 40
        second.observed_revision = lambda: 'a' * 40
        self.assertEqual(
            'preserved',
            deploy.reconcile(
                second,
                baseline='b' * 40,
                reconcile_only=True,
                journal=journal,
                require_receipt=True,
                clock=lambda: second.now,
                sleep=second.sleep,
                interval=1,
                timeout=10,
            ),
        )
        self.assertEqual(0, second.uploads)
        self.assertEqual(2, journal.receipt_attempts)

    def test_retained_failed_upload_still_fails_when_candidate_not_live(self):
        class Journal:
            baseline = 'b' * 40
            history_error = None
            intent = {'baselineDeploymentIds': ['old']}
            def record_success(self, deployment_ids):
                raise AssertionError('must not record success')

        azure = FakeAzure(
            [[row('old', 4), row('failed', 3)]],
            [False],
        )
        azure.revision = 'a' * 40
        azure.observed_revision = lambda: 'b' * 40

        with self.assertRaisesRegex(RuntimeError, 'failed'):
            azure.run(baseline='b' * 40, reconcile_only=True, journal=Journal())
        self.assertEqual(0, azure.uploads)

    def test_static_terminal_failure_has_no_alternate_upload_path(self):
        azure = FakeAzure([[], [row('failed', 3)]], [False, False], static=True)
        with self.assertRaisesRegex(RuntimeError, 'failed'):
            azure.run()
        self.assertEqual(1, azure.uploads)
        self.assertEqual(0, azure.recovery_uploads)

    def test_unknown_upload_outcome_never_causes_another_upload(self):
        azure = FakeAzure([[]], [False], accepted=False)
        with self.assertRaisesRegex(RuntimeError, 'deadline'):
            azure.run()
        self.assertEqual(1, azure.uploads)

    def test_runtime_unavailable_does_not_authorize_restarting_it(self):
        azure = FakeAzure([[]], [None])
        with self.assertRaisesRegex(RuntimeError, 'deadline'):
            azure.run()
        self.assertEqual(0, azure.uploads)

    def test_status_unavailable_is_bounded_and_fail_closed(self):
        azure = FakeAzure([RuntimeError('gateway unavailable')], [False])
        with self.assertRaisesRegex(deploy.DeploymentStatusUnavailable, 'bounded read wall budget'):
            azure.run()
        self.assertEqual(1, azure.now)
        self.assertEqual(0, azure.uploads)

    def test_transient_status_outage_recovers_without_upload_replay(self):
        azure = FakeAzure(
            [[], RuntimeError('gateway unavailable'), [row('new', 4)]],
            [False, True, True],
            accepted=False)
        self.assertEqual('deployed', azure.run())
        self.assertEqual(1, azure.uploads)

    def test_preflight_retries_transient_azure_history_timeout_without_upload(self):
        azure = FakeAzure(
            [subprocess.TimeoutExpired(['az', 'webapp', 'log', 'deployment', 'list'], 20), [row('old', 4)]],
            [False])
        azure.revision = 'a' * 40
        azure.observed_revision = lambda: 'b' * 40
        with patch.object(deploy, 'target_azure', return_value=azure), \
             patch.object(deploy.time, 'sleep') as sleeper:
            deploy.preflight_target('website', Path('/immutable.zip'), 'a' * 40, 'b' * 40, None)
        sleeper.assert_called_once_with(10)
        self.assertEqual(0, azure.uploads)

    def test_history_gap_allows_one_fresh_upload_only_from_exact_idle_baseline(self):
        from types import SimpleNamespace
        azure = FakeAzure([[row('old', 4)]], [False])
        azure.revision = 'a' * 40
        azure.observed_revision = lambda: 'b' * 40
        journal = SimpleNamespace(intent=None, history_error=RuntimeError('missing intent'))
        with patch.object(deploy, 'target_azure', return_value=azure):
            deploy.preflight_target('website', Path('/immutable.zip'), 'a' * 40, 'b' * 40, journal)
        self.assertEqual(0, azure.uploads)

    def test_reconcile_carries_exact_idle_baseline_proof_into_fresh_intent(self):
        class Journal:
            baseline = 'b' * 40
            intent = None
            history_error = RuntimeError('missing historical intent')
            def __init__(self):
                self.allowed = []
            def before_submit(self, baseline_ids, *, allow_recovered_baseline=False):
                self.allowed.append((set(baseline_ids), allow_recovered_baseline))
                if not allow_recovered_baseline:
                    raise self.history_error
                self.intent = {'baselineDeploymentIds': sorted(baseline_ids)}
                self.history_error = None
                return True
            def record_success(self, deployment_ids):
                return None

            def submission_correlation(self):
                return dict(CORRELATION)

        azure = FakeAzure(
            [[row('old', 4)], [row('old', 4), row('fresh', 4)]],
            [False])
        azure.revision = 'a' * 40
        observed = iter(['b' * 40, 'a' * 40, 'a' * 40])
        azure.observed_revision = lambda: next(observed)
        journal = Journal()

        self.assertEqual(
            'deployed',
            deploy.reconcile(
                azure,
                baseline='b' * 40,
                journal=journal,
                clock=lambda: azure.now,
                sleep=azure.sleep,
                interval=1,
                timeout=10,
            ),
        )
        self.assertEqual(1, azure.uploads)
        self.assertEqual([({'old'}, True)], journal.allowed)

    def test_history_gap_never_allows_replay_from_active_or_unknown_state(self):
        from types import SimpleNamespace
        journal = SimpleNamespace(intent=None, history_error=RuntimeError('missing intent'))
        for states, observed in (
            ([[row('pending', 1)]], 'b' * 40),
            ([[row('old', 4)]], None),
            ([[row('other', 4)]], 'c' * 40),
        ):
            azure = FakeAzure(states, [False])
            azure.revision = 'a' * 40
            azure.observed_revision = lambda value=observed: value
            with self.subTest(states=states, observed=observed), \
                 patch.object(deploy, 'target_azure', return_value=azure):
                with self.assertRaises(deploy.DeploymentReconciliationRequired):
                    deploy.preflight_target('website', Path('/immutable.zip'), 'a' * 40, 'b' * 40, journal)
            self.assertEqual(0, azure.uploads)

    def test_live_revision_alone_cannot_override_active_deployment(self):
        azure = FakeAzure([[row('still-running', 2)]], [True])
        with self.assertRaisesRegex(RuntimeError, 'deadline'):
            azure.run()
        self.assertEqual(0, azure.uploads)

    def test_azure_success_requires_stable_runtime_revision(self):
        azure = FakeAzure([[], [row('new', 4)]], [False, True, None, True, True])
        self.assertEqual('deployed', azure.run())
        self.assertEqual(4, azure.now)
        self.assertEqual(1, azure.uploads)

    def test_old_success_cannot_stand_in_for_new_deployment(self):
        azure = FakeAzure([[row('old', 4)]], [False, True])
        with self.assertRaisesRegex(RuntimeError, 'deadline'):
            azure.run()
        self.assertEqual(1, azure.uploads)

    def test_concurrent_publication_is_not_hidden(self):
        azure = FakeAzure([[], [row('ours', 4), row('other', 4)]], [False, True])
        with self.assertRaisesRegex(RuntimeError, 'Multiple'):
            azure.run()
        self.assertEqual(1, azure.uploads)


    def test_fresh_process_retry_never_replays_ambiguous_original_upload(self):
        first = FakeAzure([[]], [False], accepted=False)
        with self.assertRaises(deploy.DeploymentReconciliationRequired):
            first.run()
        resumed = FakeAzure([[], [row('delayed-original', 4)]], [False, True, True])
        self.assertEqual('preserved', resumed.run(reconcile_only=True, journal=RetainedJournal()))
        self.assertEqual(1, first.uploads)
        self.assertEqual(0, resumed.uploads)

    def test_retry_with_unproven_original_outcome_fails_closed(self):
        azure = FakeAzure([[]], [False])
        with self.assertRaises(deploy.DeploymentReconciliationRequired):
            azure.run(reconcile_only=True)
        self.assertEqual(0, azure.uploads)

    def test_baseline_candidate_and_third_revision_are_distinguished(self):
        for observed, allowed in [('b' * 40, True), ('a' * 40, True), ('c' * 40, False)]:
            azure = FakeAzure([[row('old', 4)]], [False])
            azure.revision = 'a' * 40
            azure.observed_revision = lambda: observed
            with self.subTest(observed=observed):
                if not allowed:
                    with self.assertRaises(deploy.DeploymentDrift):
                        azure.run(baseline='b' * 40)
                elif observed == azure.revision:
                    with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired, 'artifact publication intent'):
                        azure.run(baseline='b' * 40)
                else:
                    with self.assertRaises(deploy.DeploymentReconciliationRequired):
                        azure.run(baseline='b' * 40, reconcile_only=True)
                self.assertEqual(0, azure.uploads)


class PackageTests(unittest.TestCase):
    def test_different_artifact_at_same_sha_cannot_be_certified_from_live_sha(self):
        from types import SimpleNamespace
        from unittest.mock import Mock
        revision = 'a' * 40
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            first = root / 'first'; first.mkdir()
            second = root / 'second'; second.mkdir()
            live, live_digest = self.make_package(first, revision=revision)
            requested, _ = self.make_package(second, revision=revision)
            with zipfile.ZipFile(requested, 'a') as archive:
                archive.writestr('different-compiled-output.dll', b'new toolchain bytes')
            requested_digest = hashlib.sha256(requested.read_bytes()).hexdigest()
            (second / 'SHA256SUMS').write_text(f'{requested_digest}  {requested.name}\n')
            self.assertNotEqual(live_digest, deploy.verify_package(requested, revision))
            azure = FakeAzure([[row('existing-artifact', 4)]], [True])
            azure.revision = revision
            azure.observed_revision = lambda: revision
            journal = SimpleNamespace(baseline=revision, intent=None, history_error=None,
                identity=dict(applicationRevision=revision, packageDigest=requested_digest), record_success=Mock())
            with patch.object(deploy, 'target_azure', return_value=azure):
                with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired, 'artifact is unproven'):
                    deploy.preflight_target('portal', requested, revision, revision, journal)
            with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired, 'artifact publication intent'):
                azure.run(journal=journal)
            journal.record_success.assert_not_called()
            self.assertEqual(0, azure.uploads)

    def make_package(self, folder, static=False, revision='a' * 40):
        path = Path(folder) / 'app.zip'
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr('_deployment-provenance.txt' if static else 'wwwroot/_deployment-provenance.json',
                             revision if static else json.dumps({'releaseSha': revision}))
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        (path.parent / 'SHA256SUMS').write_text(f'{digest}  {path}\n')
        return path, digest

    def test_all_targets_use_retained_packages_with_bound_provenance(self):
        for target in deploy.TARGETS:
            with self.subTest(target=target), tempfile.TemporaryDirectory() as folder:
                path, digest = self.make_package(folder, target == 'website')
                self.assertEqual(digest, deploy.verify_package(path, 'a' * 40, target == 'website'))

    def test_wrong_revision_is_rejected_even_when_checksum_matches(self):
        with tempfile.TemporaryDirectory() as folder:
            path, _ = self.make_package(folder)
            with self.assertRaisesRegex(ValueError, 'approved revision'):
                deploy.verify_package(path, 'b' * 40)

    def test_modified_package_cannot_be_deployed(self):
        with tempfile.TemporaryDirectory() as folder:
            path, _ = self.make_package(folder)
            with path.open('ab') as stream:
                stream.write(b'changed')
            with self.assertRaisesRegex(ValueError, 'SHA256'):
                deploy.verify_package(path, 'a' * 40)

    def test_portal_submits_tracked_operation_without_second_upload_or_untrusted_success(self):
        azure = prepare_supported_azure(deploy.Azure('masterapp-portal', Path('/immutable.zip'), 'https://example.invalid', 'a' * 40))
        with patch.object(deploy.subprocess, 'run') as run:
            run.return_value.returncode = 0
            run.return_value.stdout = '{"deploymentId":"tracked-operation-123"}'
            with patch('builtins.print') as output:
                self.assertTrue(azure.submit(CORRELATION))
            command = run.call_args.args[0]
            for flag, value in [('--async', 'false'), ('--track-status', 'true'),
                                ('--enable-kudu-warmup', 'true'), ('--type', 'zip')]:
                self.assertEqual(value, command[command.index(flag) + 1])
            self.assertEqual(300, run.call_args.kwargs['timeout'])
            self.assertEqual(1, run.call_count)
            self.assertTrue(any('AZURE_TRACKED_DEPLOYMENT_REFERENCE:masterapp-portal:tracked-operation-123' in str(call.args)
                                for call in output.call_args_list))

    def test_authorized_protect_replacement_tracks_one_submission(self):
        azure=prepare_supported_azure(deploy.Azure('masterapp-protect',Path('/immutable.zip'),'https://example.invalid','a'*40))
        azure.authorized_replacement=True
        with patch.object(deploy.subprocess,'run') as run:
            run.return_value.returncode=0;run.return_value.stdout='{"deploymentId":"replacement-123"}'
            azure.submit({'kind':azure._submission_kind,'value':'b'*64})
            command=run.call_args.args[0]
            self.assertEqual('false',command[command.index('--async')+1])
            self.assertEqual('true',command[command.index('--track-status')+1])
            self.assertEqual(1,run.call_count)
            self.assertEqual('replacement-123',azure.submission_reference)

    def test_tracked_portal_timeout_never_infers_deployment_success(self):
        azure = prepare_supported_azure(deploy.Azure('masterapp-portal', Path('/immutable.zip'), 'https://example.invalid', 'a' * 40))
        with patch.object(deploy.subprocess, 'run', side_effect=subprocess.TimeoutExpired(['az','webapp','deploy'],300)) as run:
            self.assertFalse(azure.submit(CORRELATION))
            self.assertEqual(1, run.call_count)

    def test_untracked_other_app_stays_asynchronous(self):
        azure = prepare_supported_azure(deploy.Azure('masterapp-client', Path('/immutable.zip'), 'https://example.invalid', 'a' * 40))
        with patch.object(deploy.subprocess, 'run') as run:
            run.return_value.returncode = 0
            run.return_value.stdout = '{}'
            self.assertTrue(azure.submit(CORRELATION))
            command = run.call_args.args[0]
            for flag, value in [('--async', 'true'), ('--track-status', 'false'),
                                ('--enable-kudu-warmup', 'false')]:
                self.assertEqual(value, command[command.index(flag) + 1])
            self.assertEqual(180, run.call_args.kwargs['timeout'])
            self.assertEqual(1, run.call_count)

    def test_submission_has_no_synchronous_gateway_wait_or_internal_warmup_replay(self):
        azure = prepare_supported_azure(deploy.Azure('masterapp-protect', Path('/immutable.zip'), 'https://example.invalid', 'a' * 40))
        with patch.object(deploy.subprocess, 'run') as run:
            run.return_value.returncode = 0
            self.assertTrue(azure.submit(CORRELATION))
            command = run.call_args.args[0]
            for flag, value in [('--async', 'true'), ('--track-status', 'false'),
                                ('--enable-kudu-warmup', 'false'), ('--type', 'zip')]:
                self.assertEqual(value, command[command.index(flag) + 1])
            self.assertEqual(1, run.call_count)



class ComponentIsolationTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location('isolation_package_test', ROOT / 'release-package.py')
        self.package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.package)
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        for path in ('source', 'nuget', 'npm', 'artifacts', 'website-workspace', 'tools/node', 'tools/python', 'authority', 'probe'):
            (self.root / path).mkdir(parents=True)
        (self.root / 'source/Legend-Website').mkdir()
        (self.root / 'source/Legend-Website/package.json').write_text('{}')
        self.image = self.package.PACKAGE_TOOL_IMAGES['sdk']

    def command(self, phase='publish', component='website'):
        return self.package.isolated_component_command(component, self.root,
            ['python3', 'scripts/release-package.py'], phase=phase)

    def test_publish_inputs_readonly_and_modules_are_disposable_outputs(self):
        command = self.command()
        mounts = [command[index + 1] for index, value in enumerate(command) if value == '--mount']
        for target in ('/src', '/deps/nuget', '/deps/npm', '/src/Legend-Website/package.json', '/tools/node', '/tools/python', '/authority', '/probe'):
            self.assertTrue(next(row for row in mounts if ',dst=' + target + ',' in row).endswith(',readonly'))
        for target in ('/tmp/masterapp', '/src/Legend-Website'):
            self.assertTrue(any(row.endswith(',dst=' + target) for row in mounts))
        self.assertNotIn('docker.sock', '\n'.join(mounts))
        self.assertEqual(self.image, command[-3])

    def test_candidate_output_cannot_follow_host_links(self):
        output=self.root/'artifacts/component'
        output.mkdir(parents=True,exist_ok=True)
        archive=output/self.package.component_file('portal')
        with zipfile.ZipFile(archive,'w') as file:file.writestr('safe','output')
        self.assertEqual(archive.resolve(),self.package.isolated_component_archive(self.root,'portal'))
        archive.unlink();archive.symlink_to('/etc/passwd')
        with self.assertRaisesRegex(ValueError,'escapes owned stage'):
            self.package.isolated_component_archive(self.root,'portal')
        archive.unlink();output.rmdir();output.symlink_to(self.root.parent,target_is_directory=True)
        with self.assertRaisesRegex(ValueError,'escapes owned stage'):
            self.package.isolated_component_archive(self.root,'portal')

    def test_adapter_bootstrap_does_not_require_unrestored_website_mounts(self):
        command=self.package.isolated_component_command('protect',self.root,['/bin/true'],phase='bootstrap')
        mounts=[command[index+1] for index,value in enumerate(command) if value=='--mount']
        self.assertFalse(any('dst=/src/Legend-Website' in value for value in mounts))
        self.assertEqual('none',command[command.index('--network')+1])
        self.assertTrue(any('dst=/probe' in value and not value.endswith(',readonly') for value in mounts))

    def test_failure_diagnostic_keeps_fixed_reason_and_codes_not_candidate_output(self):
        log=self.root/'owned.log'
        log.write_text('synthetic-private-payload\nValueError: private arbitrary input\nerror CS1234: synthetic source text\nValueError: Restored package archive digest mismatch\n')
        result=self.package.isolated_failure_diagnostic(log,'observe',ValueError('private exception detail'))
        self.assertEqual('Restored package archive digest mismatch',result['reason'])
        self.assertEqual(['CS1234'],result['compilerCodes'])
        self.assertNotIn('private',json.dumps(result))
        self.assertNotIn('synthetic',json.dumps(result))

    def test_only_restore_has_network_and_observe_has_no_writable_bind_mount(self):
        for phase in ('restore', 'publish', 'observe'):
            command = self.command(phase)
            self.assertEqual('bridge' if phase == 'restore' else 'none', command[command.index('--network') + 1])
            if phase == 'observe':
                self.assertTrue(all(command[index + 1].endswith(',readonly')
                    for index, value in enumerate(command) if value == '--mount'))
            self.assertIn('--read-only', command)
            self.assertIn('no-new-privileges', command)

    def test_tokens_proxy_home_and_run_environment_are_never_forwarded(self):
        first = self.command()
        with patch.dict(os.environ, dict(GITHUB_TOKEN='test-token', AZURE_CLIENT_SECRET='test-secret',
                ACTIONS_RUNTIME_TOKEN='test-runtime', HTTPS_PROXY='test-proxy', HOME='/private-host',
                GITHUB_RUN_ATTEMPT='123', GITHUB_SHA='new-sha')):
            self.assertEqual(first, self.command())
        environment = [first[index + 1] for index, value in enumerate(first) if value == '--env']
        self.assertIn('npm_config_offline=true', environment)
        self.assertIn('HOME=/tmp/home', environment)
        self.assertFalse(any(value.startswith(('GITHUB_', 'AZURE_', 'ACTIONS_', 'HTTPS_PROXY=')) for value in environment))

    def test_escaped_mount_or_mutable_tool_reference_is_rejected(self):
        (self.root / 'nuget').rmdir()
        (self.root / 'nuget').symlink_to(self.root.parent, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, 'outside owned stage'):
            self.command()

    def test_tool_extraction_authenticates_exact_image_and_never_starts_container(self):
        shutil.rmtree(self.root / 'tools')
        image_id, container_id = 'sha256:' + 'c' * 64, 'd' * 64
        calls = []
        selected_image = None
        def run(command, **kwargs):
            nonlocal selected_image
            calls.append(command)
            value = ''
            if command[:3] == ['docker', 'image', 'inspect']:
                self.assertNotIn('--platform', command)
                self.assertEqual(image_id, command[-1])
                value = json.dumps([dict(Os='linux', Architecture='amd64', Id=image_id)])
            elif command[:2] == ['docker', 'create']:
                selected_image = command[-1]
                self.assertIn('--pull=missing', command)
                value = container_id
            elif command[:3] == ['docker', 'container', 'inspect']:
                value = json.dumps([dict(Image=image_id, Config=dict(Image=selected_image), State=dict(Running=False))])
            elif command[:2] == ['docker', 'cp']:
                target = Path(command[-1]); target.mkdir()
                for relative in self.package.PACKAGE_TOOL_PATHS[target.name]:
                    file = target / relative; file.parent.mkdir(parents=True, exist_ok=True); file.write_bytes(b'pinned-tool')
            return type('Result', (), dict(stdout=value, returncode=0))()
        with patch.object(self.package.subprocess, 'run', side_effect=run):
            proof = self.package.prepare_isolated_tools(self.root)
        self.assertEqual(3, sum(command[:2] == ['docker', 'create'] for command in calls))
        self.assertEqual(3, sum(command[:2] == ['docker', 'rm'] for command in calls))
        self.assertFalse(any(command[:2] == ['docker', 'start'] for command in calls))
        self.assertEqual(self.package.PACKAGE_TOOL_IMAGES['node'], proof['tools']['node']['image'])
        self.assertEqual(image_id, proof['tools']['node']['imageId'])
        with self.assertRaises(FileExistsError):
            self.package.prepare_isolated_tools(self.root)

    def test_tool_identity_covers_content_modes_links_and_rejects_escape(self):
        import time
        tree = self.root / 'tools/node'
        binary = tree / 'compiler'; binary.write_bytes(b'original')
        link = tree / 'alias'; link.symlink_to('compiler')
        first = self.package.verified_tool_tree(tree, deadline=time.monotonic() + 10)
        self.assertEqual(first, self.package.verified_tool_tree(tree, deadline=time.monotonic() + 10))
        binary.write_bytes(b'changed')
        changed = self.package.verified_tool_tree(tree, deadline=time.monotonic() + 10)
        self.assertNotEqual(first['identity'], changed['identity'])
        binary.chmod(0o755)
        self.assertNotEqual(changed['identity'], self.package.verified_tool_tree(tree, deadline=time.monotonic() + 10)['identity'])
        link.unlink(); link.symlink_to('/etc/passwd')
        with self.assertRaisesRegex(ValueError, 'escapes verified tree'):
            self.package.verified_tool_tree(tree, deadline=time.monotonic() + 10)

    def test_source_materialization_keeps_exact_commit_without_host_state(self):
        repository = self.root / 'repository'
        repository.mkdir()
        def git(*args):
            return subprocess.check_output(['git', *args], cwd=repository, stderr=subprocess.DEVNULL, text=True).strip()
        git('init')
        git('config', 'user.name', 'Synthetic Test')
        git('config', 'user.email', 'synthetic@example.invalid')
        (repository / 'input.txt').write_text('committed input')
        git('add', 'input.txt')
        git('commit', '-m', 'synthetic source')
        revision = git('rev-parse', 'HEAD')
        git('remote', 'add', 'private', 'https://example.invalid/private')
        (repository / 'input.txt').write_text('uncommitted edit')
        (repository / 'private.txt').write_text('untracked synthetic credential')
        shutil.rmtree(self.root / 'source')
        with patch.object(self.package, 'ROOT', repository):
            result = self.package.materialize_component_source(revision, self.root)
        self.assertEqual(1, result['files'])
        self.assertEqual('committed input', (self.root / 'source/input.txt').read_text())
        self.assertFalse((self.root / 'source/private.txt').exists())
        self.assertNotIn('remote', (self.root / 'source/.git/config').read_text())
        self.assertEqual(revision, subprocess.check_output(['git', '-C', str(self.root / 'source'),
            'rev-parse', '--verify', 'HEAD^{commit}'], text=True).strip())

    def test_publish_scratch_drops_unmeasured_restore_state_and_preserves_cache(self):
        (self.root / 'website-workspace/.npmrc').write_text('injected restore setting')
        (self.root / 'website-workspace/generated.js').write_text('unmeasured restore output')
        (self.root / 'npm/retained-cache').write_text('retained verified dependency')
        self.package.prepare_website_workspace('website', self.root, phase='publish')
        self.assertEqual(['package.json'], sorted(path.name for path in (self.root / 'website-workspace').iterdir()))
        self.assertEqual('', (self.root / 'website-workspace/package.json').read_text())
        self.assertEqual('{}', (self.root / 'source/Legend-Website/package.json').read_text())
        self.assertEqual('retained verified dependency', (self.root / 'npm/retained-cache').read_text())

    def test_timeout_reclaims_only_exact_owned_container_and_never_retries(self):
        identity = 'b' * 64
        calls = []
        def run(command, **kwargs):
            calls.append(command)
            if command[:2] == ['docker', 'run']:
                Path(command[command.index('--cidfile') + 1]).write_text(identity)
                raise subprocess.TimeoutExpired(command, 1)
            self.assertEqual(['docker', 'rm', '--force', identity], command)
            return type('Result', (), {'returncode': 0})()
        with patch.object(self.package.subprocess, 'run', side_effect=run):
            with self.assertRaises(subprocess.TimeoutExpired):
                self.package.run_isolated_component('website', self.root, ['node', '--version'],
                    phase='publish', timeout=1)
        self.assertEqual(2, len(calls))
        evidence = list(self.root.glob('*.cleanup.json'))
        self.assertEqual(1, len(evidence))
        self.assertEqual(dict(containerId=identity, attempted=True, removed=True), json.loads(evidence[0].read_text()))

    def test_unknown_container_identity_never_triggers_cleanup_by_name(self):
        def run(command, **kwargs):
            Path(command[command.index('--cidfile') + 1]).write_text('other-container')
            raise KeyboardInterrupt()
        with patch.object(self.package.subprocess, 'run', side_effect=run) as execute:
            with self.assertRaises(KeyboardInterrupt):
                self.package.run_isolated_component('website', self.root, ['node', '--version'],
                    phase='publish', timeout=1)
            self.assertEqual(1, execute.call_count)
        self.assertFalse(list(self.root.glob('*.cleanup.json')))



class ComponentRestoreBoundaryTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location('restore_boundary_test', ROOT / 'release-package.py')
        self.package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.package)
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.stage = Path(self.temporary.name)
        self.obj = self.stage / 'artifacts/obj/App'
        self.obj.mkdir(parents=True)
        for name in ('project.assets.json', 'App.csproj.nuget.g.props', 'App.csproj.nuget.g.targets'):
            (self.obj / name).write_text('{}' if name.endswith('.json') else '<Project/>')
        self.material = dict(projectPaths=['App/App.csproj'])

    def test_only_verified_restore_inputs_survive_and_original_evidence_is_preserved(self):
        (self.obj / 'unmeasured-restore-script').write_text('unmeasured')
        (self.stage / 'artifacts/fake-compiled.dll').write_text('must not suppress compilation')
        identity = self.package.freeze_component_restore(self.stage, self.material)
        self.assertEqual(64, len(identity))
        self.assertEqual(3, len(list(self.obj.iterdir())))
        self.assertFalse((self.stage / 'artifacts/fake-compiled.dll').exists())
        self.assertTrue((self.stage / 'restore-observation/fake-compiled.dll').exists())
        self.assertTrue((self.stage / 'restore-observation/obj/App/unmeasured-restore-script').exists())

    def test_superseded_cache_is_unmounted_but_transitive_versions_remain(self):
        cache = self.stage / 'nuget'
        for relative in ('example/1.0', 'example/2.0', 'transitive/1.0'):
            path = cache / relative
            path.mkdir(parents=True)
            (path / 'input.dll').write_text(relative)
        for name, package in (('App', 'example/2.0'), ('Shared', 'transitive/1.0')):
            path = self.stage / 'artifacts/obj' / name / 'project.assets.json'
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(dict(version=4, libraries={package: dict(type='package', path=package)})))
        result = self.package.narrow_restore_cache(self.stage)
        self.assertEqual(['example/1.0'], result['excluded'])
        self.assertEqual(['example/2.0', 'transitive/1.0'], result['selected'])
        self.assertEqual('selected-unverified', result['state'])
        self.assertFalse((cache / 'example/1.0').exists())
        self.assertEqual('example/1.0', (self.stage / 'restore-cache-observation/example/1.0/input.dll').read_text())
        self.assertTrue((cache / 'transitive/1.0/input.dll').is_file())

    def test_cache_selection_rejects_traversal_and_symlinks_before_mutation(self):
        cache = self.stage / 'nuget'
        cache.mkdir()
        for relative in ('../escape', 'example/..', '/example/1.0'):
            (self.obj / 'project.assets.json').write_text(json.dumps(dict(version=4,
                libraries={relative: dict(type='package', path=relative)})))
            with self.assertRaisesRegex(ValueError, 'Unsafe restore selection package path'):
                self.package.narrow_restore_cache(self.stage)
            self.assertFalse((self.stage / 'restore-cache-observation').exists())
        (cache / 'alias').symlink_to(self.obj, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, 'Unsafe restore selection entry'):
            self.package.narrow_restore_cache(self.stage)
        self.assertTrue(cache.is_dir())

    def test_untrusted_inventory_and_symlink_do_not_replace_original_state(self):
        for projects in (['../outside.csproj'], ['App/App.csproj', 'Other/App.csproj'], []):
            with self.subTest(projects=projects):
                clean = self.stage / 'publish-inputs'
                if clean.exists(): shutil.rmtree(clean)
                with self.assertRaises(ValueError):
                    self.package.freeze_component_restore(self.stage, dict(projectPaths=projects))
                self.assertTrue(self.obj.is_dir())
        clean = self.stage / 'publish-inputs'
        if clean.exists(): shutil.rmtree(clean)
        (self.obj / 'project.assets.json').unlink()
        (self.obj / 'project.assets.json').symlink_to('/etc/passwd')
        with self.assertRaisesRegex(ValueError, 'Unsafe verified restore'):
            self.package.freeze_component_restore(self.stage, self.material)
        self.assertTrue(self.obj.is_dir())


class ResolvedPackageMaterialTests(unittest.TestCase):
    def setUp(self):
        import base64
        spec = importlib.util.spec_from_file_location('resolved_package_test', ROOT / 'release-package.py')
        self.package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.package)
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.repo, self.cache, self.obj, self.sdk = (self.root / name for name in ('repo', 'cache', 'artifacts', 'sdk'))
        for path in (self.repo / 'App', self.repo / 'Shared', self.sdk):
            path.mkdir(parents=True)
        (self.sdk / 'runtime.json').write_text('{}')
        for name in ('App', 'Shared'):
            (self.repo / name / (name + '.csproj')).write_text('<Project/>')
        self.directory = self.cache / 'example/1.0.0'
        self.directory.mkdir(parents=True)
        self.archive = self.directory / 'example.1.0.0.nupkg'
        (self.directory / 'lib').mkdir()
        (self.directory / 'lib/library.dll').write_bytes(b'compiler input')
        with zipfile.ZipFile(self.archive, 'w') as archive:
            archive.writestr('lib/library.dll', b'compiler input')
        self.library = dict(type='package', path='example/1.0.0', files=['lib/library.dll'],
                            sha512=base64.b64encode(hashlib.sha512(self.archive.read_bytes()).digest()).decode())
        self.assets = {}
        for name in ('App', 'Shared'):
            project = self.repo / name / (name + '.csproj')
            references = {} if name == 'Shared' else {str(self.repo / 'Shared/Shared.csproj'): {'projectPath': str(self.repo / 'Shared/Shared.csproj')}}
            data = dict(version=3, packageFolders={str(self.cache): {}}, project={'version': '1.0.0', 'restore': {'projectPath': str(project),
                'frameworks': {'net10.0': {'projectReferences': references}}},
                'frameworks': {'net10.0': {'runtimeIdentifierGraphPath': str(self.sdk / 'runtime.json')}}},
                targets={'net10.0': {'Example/1.0.0': {'compile': {'lib/library.dll': {}}}}},
                libraries={'Example/1.0.0': dict(self.library)})
            path = self.obj / 'obj' / name / 'project.assets.json'
            path.parent.mkdir(parents=True)
            path.write_text(json.dumps(data))
            for suffix in ('.nuget.g.props', '.nuget.g.targets'):
                (path.parent / (name + '.csproj' + suffix)).write_text('<Project/>')
            self.assets[name] = path
        self.package.ROOT = self.repo
        self.package.APPS = {'app': ('App/App.csproj', 'app.zip', False)}

    def identity(self):
        return self.package.resolved_restore_identity('app', self.obj, [self.cache], self.sdk)

    def test_verified_transitive_graph_is_stable_but_dependency_and_framework_changes_invalidate(self):
        first = self.identity()
        self.assertEqual(first, self.identity())
        self.assertEqual(2, first['projectCount'])
        self.assertEqual(1, first['packageCount'])
        data = json.loads(self.assets['Shared'].read_text())
        data['project']['frameworks']['net10.0']['frameworkReferences'] = {'Microsoft.AspNetCore.App': {}}
        self.assets['Shared'].write_text(json.dumps(data))
        self.assertNotEqual(first['identity'], self.identity()['identity'])
        (self.sdk / 'runtime.json').write_text('{"changed":true}')
        self.assertNotEqual(first['identity'], self.identity()['identity'])

    def test_v4_framework_aliases_remain_distinct_and_schema_changes_invalidate(self):
        first = self.identity()['identity']
        data = json.loads(self.assets['App'].read_text())
        data['version'] = 4
        data['project']['frameworks']['net10.0']['targetAlias'] = 'net10.0'
        self.assets['App'].write_text(json.dumps(data))
        second = self.identity()['identity']
        self.assertNotEqual(first, second)
        data['project']['frameworks']['alternate'] = dict(data['project']['frameworks']['net10.0'], targetAlias='alternate')
        data['targets']['alternate'] = {}
        self.assets['App'].write_text(json.dumps(data))
        self.assertNotEqual(second, self.identity()['identity'])
        data['packageFolders'][str(self.root / 'unverified-cache')] = {}
        self.assets['App'].write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'search roots differ'):
            self.identity()

    def test_generated_metadata_is_bound_and_undeclared_cache_roots_are_rejected(self):
        first = self.identity()['identity']
        (self.directory / '.nupkg.metadata').write_text('{"version":2}')
        self.assertNotEqual(first, self.identity()['identity'])
        (self.cache / 'undeclared').mkdir()
        with self.assertRaisesRegex(ValueError, 'Unmeasured input'):
            self.identity()

    def test_generated_import_mutation_changes_identity(self):
        first = self.identity()['identity']
        path = self.assets['Shared'].parent / 'Shared.csproj.nuget.g.targets'
        path.write_text('<Project><Import Project="unexpected.targets"/></Project>')
        self.assertNotEqual(first, self.identity()['identity'])

    def test_extra_cache_file_and_omitted_analyzer_input_are_rejected(self):
        injected = self.directory / 'lib/injected.dll'
        injected.write_bytes(b'injected compiler input')
        with self.assertRaisesRegex(ValueError, 'inventory differs'):
            self.identity()
        injected.unlink()
        data = json.loads(self.assets['App'].read_text())
        data['targets']['net10.0']['Example/1.0.0']['analyzers'] = {'analyzers/missing.dll': {}}
        self.assets['App'].write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'compiler input absent'):
            self.identity()

    def test_nuget_empty_asset_group_is_not_an_unverified_compiler_file(self):
        data = json.loads(self.assets['App'].read_text())
        data['targets']['net10.0']['Example/1.0.0']['compile'] = {'lib/net10.0/_._': {}}
        self.assets['App'].write_text(json.dumps(data))
        self.assertEqual(1, self.identity()['packageCount'])
        data['targets']['net10.0']['Example/1.0.0']['compile'] = {'../_._': {}}
        self.assets['App'].write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'escapes owning root'):
            self.identity()

    def test_shared_count_byte_and_deadline_budgets_fail_closed(self):
        for key, value, reason in (('projects', 1, 'project budget'), ('packages', 0, 'package budget'),
                                   ('entries', 0, 'inventory invalid'), ('fileBytes', 1, 'file budget'),
                                   ('totalBytes', 1, 'byte budget'), ('seconds', 0, 'deadline')):
            with self.subTest(boundary=key), patch.dict(self.package.RESTORE_MATERIAL_LIMITS, {key: value}):
                with self.assertRaisesRegex(ValueError, reason):
                    self.identity()

    def test_restore_material_symlink_escape_is_rejected(self):
        path = self.assets['App']
        outside = self.root / 'outside.json'
        outside.write_bytes(path.read_bytes())
        path.unlink()
        path.symlink_to(outside)
        with self.assertRaisesRegex(ValueError, 'symlink escapes'):
            self.identity()

    def test_nuget_separator_normalization_preserves_content_and_rejects_alias_collision(self):
        import base64
        def bind_archive():
            digest=base64.b64encode(hashlib.sha512(self.archive.read_bytes()).digest()).decode()
            for path in self.assets.values():
                data=json.loads(path.read_text())
                data['libraries']['Example/1.0.0']['sha512']=digest
                path.write_text(json.dumps(data))
        with zipfile.ZipFile(self.archive,'w') as archive:
            archive.writestr('lib//library.dll',b'compiler input')
        bind_archive()
        self.assertEqual(1,self.identity()['packageCount'])
        with zipfile.ZipFile(self.archive,'a') as archive:
            archive.writestr('lib/library.dll',b'ambiguous alias')
        bind_archive()
        with self.assertRaisesRegex(ValueError,'archive file identity ambiguous'):
            self.identity()

    def test_archive_and_extracted_compile_input_corruption_are_rejected(self):
        original = self.archive.read_bytes()
        self.archive.write_bytes(original + b'tampered')
        with self.assertRaisesRegex(ValueError, 'archive digest mismatch'):
            self.identity()
        self.archive.write_bytes(original)
        (self.directory / 'lib/library.dll').write_bytes(b'tampered extracted generator')
        with self.assertRaisesRegex(ValueError, 'Extracted package content'):
            self.identity()

    def test_missing_transitive_restore_and_unsupported_schema_fail_closed(self):
        original = self.assets['Shared'].read_text()
        self.assets['Shared'].unlink()
        with self.assertRaises(FileNotFoundError):
            self.identity()
        data = json.loads(original)
        data['version'] = 99
        self.assets['Shared'].write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'Unsupported restore material'):
            self.identity()

    def test_untrusted_paths_and_conflicting_dependency_hashes_are_rejected(self):
        original = json.loads(self.assets['App'].read_text())
        for field, value in (('path', '../outside'), ('sha512', 'not-a-digest'),
                             ('files', ['../outside'])):
            data = json.loads(json.dumps(original))
            data['libraries']['Example/1.0.0'][field] = value
            self.assets['App'].write_text(json.dumps(data))
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.identity()
        self.assets['App'].write_text(json.dumps(original))
        original['project']['frameworks']['net10.0']['runtimeIdentifierGraphPath'] = str(self.root / 'outside')
        self.assets['App'].write_text(json.dumps(original))
        with self.assertRaisesRegex(ValueError, 'outside measured SDK'):
            self.identity()


class ComponentAssemblyIntegrationTests(unittest.TestCase):
    def setUp(self):
        spec=importlib.util.spec_from_file_location('component_assembly_test',ROOT/'release-package.py')
        self.package=importlib.util.module_from_spec(spec);spec.loader.exec_module(self.package)
        self.folder=tempfile.TemporaryDirectory();self.addCleanup(self.folder.cleanup)
        self.directory=Path(self.folder.name);self.candidate='a'*40;self.producer='b'*40
        self.sources={}
        for index,(component,(_,name,static)) in enumerate(self.package.APPS.items(),1):
            path=self.directory/name
            producer=self.candidate if component=='portal' else self.producer
            with zipfile.ZipFile(path,'w') as archive:
                archive.writestr('_deployment-provenance.txt' if static else 'wwwroot/_deployment-provenance.json',
                    producer if static else json.dumps(dict(releaseSha=producer)))
            digest=self.package.sha256_file(path)
            proof=dict(sha256=digest,component=component)
            (self.directory/(component+'.execution.json')).write_text(json.dumps(proof))
            receipt=dict(schema=self.package.CONTENT_COMPONENT_SCHEMA,producerRevision=producer,
                contentIdentity='c'*64,executionIdentity='e'*64,component=component,file=name,sha256=digest)
            (self.directory/(component+'.component.json')).write_text(json.dumps(receipt))
            source=dict(schemaVersion=1,component=component,producerRevision=producer,runId=91,runAttempt=2,
                contentIdentity='c'*64,executionIdentity='e'*64,execution=proof,sha256=digest,
                artifact=dict(id=index,name=self.package._RELEASE_AUTHORITY.component_attempt_name(component,'c'*64,91,2),digest='sha256:'+'d'*64))
            self.sources[component]=dict(state='executed-success' if component=='portal' else 'reused-success',
                sourceReceipt=source,producingJobId=100+index,receiptArtifactId=200+index)
        self.original={p.name:p.read_bytes() for p in self.directory.glob('*.zip')}
        (self.directory/self.package.MIGRATION_BUNDLE).write_bytes(b'exact rehearsal bundle')
        self.patches=[patch.object(self.package,'validate_revision',side_effect=lambda value:value),
            patch.object(self.package,'contract_hash',return_value='f'*64),
            patch.object(self.package.subprocess,'check_output',side_effect=lambda cmd,**kw:self.candidate if cmd[-1]=='HEAD' else '9'*40),
            patch.object(self.package,'component_execution_identity',return_value='e'*64),
            patch.object(self.package._RELEASE_AUTHORITY,'package_component_manifest',return_value=dict(contentIdentity='c'*64,reusable=True)),
            patch.object(self.package,'build_isolated_component',side_effect=AssertionError('Assembly rebuilt a child'))]
        for patched in self.patches:patched.start();self.addCleanup(patched.stop)
        migration=dict(schema=self.package.COMPONENT_SCHEMA,applicationReleaseSha=self.candidate,
            packageContractSha256='f'*64,packageIdentity=self.package.package_identity(self.candidate),
            component='migration',file=self.package.MIGRATION_BUNDLE,
            sha256=self.package.sha256_file(self.directory/self.package.MIGRATION_BUNDLE))
        (self.directory/'migration.component.json').write_text(json.dumps(migration))

    def test_assembly_preserves_each_original_producer_and_performs_zero_builds(self):
        manifest=self.package.assemble_components(self.candidate,self.directory,component_sources=self.sources)
        self.assertEqual(self.package.CONTENT_PACKAGE_SCHEMA,manifest['schema'])
        self.assertEqual(self.candidate,manifest['applicationReleaseSha'])
        self.assertEqual(self.candidate,manifest['targets']['portal']['producerRevision'])
        self.assertEqual(self.producer,manifest['targets']['client']['producerRevision'])
        self.assertEqual(2,manifest['targets']['client']['source']['runAttempt'])
        self.assertEqual(self.original,{p.name:p.read_bytes() for p in self.directory.glob('*.zip')})
        self.package.build_isolated_component.assert_not_called()
        self.assertEqual(manifest,self.package.verify_all(self.candidate,self.directory))

    def test_changed_child_metadata_cannot_restamp_unchanged_bytes(self):
        manifest=self.package.assemble_components(self.candidate,self.directory,component_sources=self.sources)
        manifest['targets']['client']['producerRevision']=self.candidate
        (self.directory/'manifest.json').write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError,'target map mismatch'):
            self.package.verify_all(self.candidate,self.directory)
        name=self.package.component_file('client')
        self.assertEqual(self.original[name],(self.directory/name).read_bytes())

    def test_rollback_restores_exact_component_once_without_compilation(self):
        key='client';name=self.package.component_file(key);content=self.original[name]
        digest=self.package.sha256_file(self.directory/name)
        evidence=dict(reusable=True,revision=self.producer,app=key,runId=71,
            packageArtifact='validated-component-bytes-client-source-71-a2',artifactId=81,
            packageDigest=digest,componentSource=True)
        def download(repo,run,artifact,folder,**kwargs):
            self.assertEqual({'artifact_id':81},kwargs)
            (folder/name).write_bytes(content)
        with patch.object(self.package._RELEASE_AUTHORITY,'_download_run_artifact',side_effect=download) as fetch:
            result=self.package.restore_rollback_component('owner/repo',self.producer,key,evidence,self.directory/'rollback')
        self.assertEqual(1,fetch.call_count)
        self.assertEqual(content,(self.directory/'rollback/package.zip').read_bytes())
        self.assertEqual(81,result['artifactId']);self.assertEqual(digest,result['packageDigest'])
        self.package.build_isolated_component.assert_not_called()
        with patch.object(self.package._RELEASE_AUTHORITY,'_download_run_artifact',side_effect=AssertionError('Missing evidence caused work')):
            with self.assertRaisesRegex(ValueError,'evidence unavailable'):
                self.package.restore_rollback_component('owner/repo',self.producer,key,dict(evidence,reusable=False),self.directory/'missing')

    def test_legacy_rollback_retains_checksum_contract_and_rejects_changed_bytes(self):
        key='client';name=self.package.component_file(key);content=self.original[name]
        digest=self.package.sha256_file(self.directory/name)
        evidence=dict(reusable=True,revision=self.producer,app=key,runId=71,packageArtifact='retained-legacy')
        def download(repo,run,artifact,folder,**kwargs):
            self.assertEqual({},kwargs)
            (folder/name).write_bytes(content)
            (folder/'SHA256SUMS').write_text(digest+'  '+name+'\n')
        with patch.object(self.package._RELEASE_AUTHORITY,'_download_run_artifact',side_effect=download):
            result=self.package.restore_rollback_component('owner/repo',self.producer,key,evidence,self.directory/'legacy')
            with self.assertRaisesRegex(ValueError,'content digest mismatch'):
                self.package.restore_rollback_component('owner/repo',self.producer,key,dict(evidence,packageDigest='0'*64),self.directory/'changed')
        self.assertNotIn('artifactId',result)
        self.assertFalse((self.directory/'changed').exists())
        self.package.build_isolated_component.assert_not_called()

    def test_aggregate_manifest_substitution_is_rejected_even_with_same_target_map(self):
        manifest=self.package.assemble_components(self.candidate,self.directory,component_sources=self.sources)
        evidence=dict(revision=self.candidate,packageIdentity=manifest['packageIdentity'],targetMaterials=manifest['targets'],
            artifactId=101,artifactDigest='sha256:'+'d'*64,manifestSha256=self.package.sha256_file(self.directory/'manifest.json'))
        self.package.verify_descriptor_binding(self.directory,manifest,evidence)
        (self.directory/'manifest.json').write_text(json.dumps(dict(manifest,unexpected='substitution')))
        with self.assertRaisesRegex(ValueError,'contradicts authenticated descriptor'):
            self.package.verify_descriptor_binding(self.directory,manifest,evidence)

    def test_small_descriptor_preserves_exact_target_and_provider_identity(self):
        manifest=self.package.assemble_components(self.candidate,self.directory,component_sources=self.sources)
        descriptor=self.package.record_package_source(self.candidate,self.directory,self.directory/'descriptor',
            run_id=71,attempt=2,artifact_id=101,artifact_digest='sha256:'+'d'*64)
        self.assertEqual(manifest['targets'],descriptor['targets'])
        self.assertEqual(self.package.sha256_file(self.directory/'manifest.json'),descriptor['manifestSha256'])
        self.assertEqual('founder-diagnostics-packages-'+manifest['packageIdentity']+'-71-a2',descriptor['artifact']['name'])
        self.assertEqual(101,descriptor['artifact']['id'])
        self.package.build_isolated_component.assert_not_called()
        with self.assertRaises(FileExistsError):
            self.package.record_package_source(self.candidate,self.directory,self.directory/'descriptor',
                run_id=71,attempt=2,artifact_id=101,artifact_digest='sha256:'+'d'*64)

    def test_execution_proof_must_bind_actual_component_digest(self):
        source=self.sources['client']['sourceReceipt']
        source['execution']['sha256']='f'*64
        (self.directory/'client.execution.json').write_text(json.dumps(source['execution']))
        with self.assertRaisesRegex(ValueError,'Downloaded component source mismatch'):
            self.package.assemble_components(self.candidate,self.directory,component_sources=self.sources)
        self.package.build_isolated_component.assert_not_called()

    def test_incomplete_new_map_cannot_fall_back_to_legacy(self):
        manifest=self.package.assemble_components(self.candidate,self.directory,component_sources=self.sources)
        del manifest['targets']['portal']
        with self.assertRaisesRegex(ValueError,'Unsupported target provenance'):
            self.package.package_target_material(manifest,'client')
        legacy=dict(schema=self.package.SCHEMA,applicationReleaseSha=self.producer,
            files={self.package.component_file('client'):'d'*64})
        self.assertEqual(dict(producerRevision=self.producer,packageDigest='d'*64,legacy=True),
                         self.package.package_target_material(legacy,'client'))


class RehearsalAggregateBindingTests(unittest.TestCase):
    def test_rehearsal_restores_exact_aggregate_bundle_without_restamping_or_build(self):
        spec=importlib.util.spec_from_file_location('rehearsal_package',ROOT/'release-package.py')
        package=importlib.util.module_from_spec(spec);spec.loader.exec_module(package)
        candidate,producer='a'*40,'b'*40
        body=b'original retained migration bundle A';digest=hashlib.sha256(body).hexdigest()
        selected=dict(reusable=True,revision=producer,runId=71,artifact='founder-diagnostics-packages-'+'c'*64,packageIdentity='c'*64)
        artifact=dict(id=81,name=selected['artifact'],digest='sha256:'+'d'*64,expired=False)
        receipt=dict(schema=package.COMPONENT_SCHEMA,applicationReleaseSha=producer,packageContractSha256='e'*64,
            packageIdentity='c'*64,component='migration',file=package.MIGRATION_BUNDLE,sha256=digest)
        def download(repo,run,name,root,*,artifact_id):
            self.assertEqual(81,artifact_id)
            (root/package.MIGRATION_BUNDLE).write_bytes(body)
            (root/'migration.component.json').write_text(json.dumps(receipt))
        with tempfile.TemporaryDirectory() as temporary, \
             patch.dict(os.environ,GITHUB_REPOSITORY='owner/repo',GITHUB_TOKEN='placeholder'), \
             patch.object(package,'validate_revision',side_effect=lambda value:value), \
             patch.object(package,'contract_hash',return_value='e'*64), \
             patch.object(package,'package_identity',return_value='c'*64), \
             patch.object(package._RELEASE_AUTHORITY,'compute_validated_package_evidence',return_value=selected), \
             patch.object(package._RELEASE_AUTHORITY,'package_inputs_compatible',return_value=True), \
             patch.object(package._RELEASE_AUTHORITY,'api_get',return_value=dict(artifacts=[artifact],total_count=1)), \
             patch.object(package._RELEASE_AUTHORITY,'_download_run_artifact',side_effect=download) as transfers, \
             patch.object(package,'verify_all',return_value=dict(files={package.MIGRATION_BUNDLE:digest})) as verify, \
             patch.object(package,'verify_descriptor_binding') as descriptor, \
             patch.object(package,'build_migration_bundle') as build:
            output=Path(temporary)
            binding=package.restore_aggregate_migration(candidate,output)
            self.assertEqual(body,(output/package.MIGRATION_BUNDLE).read_bytes())
            self.assertEqual(receipt,json.loads((output/'migration.component.json').read_text()))
            self.assertEqual(binding,package.verify_prepared_migration(candidate,output))
            self.assertEqual(producer,binding['producerRevision']);self.assertEqual(1,transfers.call_count)
            verify.assert_called_once();descriptor.assert_called_once();build.assert_not_called()
            (output/package.MIGRATION_BUNDLE).write_bytes(b'different bundle B')
            with self.assertRaises(ValueError):package.verify_prepared_migration(candidate,output)
            build.assert_not_called()


    def test_prepared_aggregate_child_survives_worker_loss_without_transfer_rebuild(self):
        spec=importlib.util.spec_from_file_location('prepared_package',ROOT/'release-package.py')
        package=importlib.util.module_from_spec(spec);spec.loader.exec_module(package)
        binding=dict(producerRevision='b'*40,artifactId=81)
        def download(repo,run,name,root,*,artifact_id):
            (root/'migration').mkdir()
            (root/'migration'/package.MIGRATION_BUNDLE).write_bytes(b'preserved A')
            (root/'migration/migration.component.json').write_text(json.dumps({'applicationReleaseSha':'b'*40}))
            (root/'migration/migration.aggregate.json').write_text(json.dumps(binding))
        with tempfile.TemporaryDirectory() as temporary, \
             patch.dict(os.environ,GITHUB_REPOSITORY='owner/repo',GITHUB_TOKEN='placeholder'), \
             patch.object(package._RELEASE_AUTHORITY,'api_get',return_value=dict(workflow_runs=[])), \
             patch.object(package._RELEASE_AUTHORITY,'readiness_artifact_candidates',return_value=[(dict(id=71),dict(id=91,name='legend-rehearsal-bundle-x-a1'))]), \
             patch.object(package._RELEASE_AUTHORITY,'readiness_child_succeeded',return_value=True), \
             patch.object(package._RELEASE_AUTHORITY,'_download_run_artifact',side_effect=download) as transfer, \
             patch.object(package,'verify_prepared_migration',return_value=binding) as verify, \
             patch.object(package,'build_migration_bundle') as build:
            output=Path(temporary)
            proof=package.restore_prepared_migration('a'*40,output,'x')
            self.assertEqual('reused-success',proof['state'])
            self.assertEqual(binding,json.loads((output/'migration.aggregate.json').read_text()))
            self.assertEqual(b'preserved A',(output/package.MIGRATION_BUNDLE).read_bytes())
            self.assertEqual(1,transfer.call_count);verify.assert_called_once();build.assert_not_called()


class PackageContractTests(unittest.TestCase):
    def test_candidate_component_build_requires_no_evidence_credential(self):
        spec = importlib.util.spec_from_file_location('release_package_test', ROOT / 'release-package.py')
        package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(package)
        with tempfile.TemporaryDirectory() as temporary, \
             patch.dict(os.environ, {'GITHUB_ACTIONS': 'true', 'GITHUB_EVENT_NAME': 'pull_request'}, clear=True), \
             patch.object(package, 'validate_revision', side_effect=lambda value: value), \
             patch.object(package, 'contract_hash', return_value='b' * 64), \
             patch.object(package, 'package_identity', return_value='c' * 64), \
             patch.object(package._RELEASE_AUTHORITY, 'require_readiness', side_effect=AssertionError('Credentialed lookup in build')) as lookup, \
             patch.object(package, 'build_migration_bundle', side_effect=lambda output: (output / package.MIGRATION_BUNDLE).write_bytes(b'candidate')) as build:
            receipt = package.build_component('a' * 40, 'migration', Path(temporary))
            self.assertEqual(hashlib.sha256(b'candidate').hexdigest(), receipt['sha256'])
            self.assertEqual(1, build.call_count)
            lookup.assert_not_called()

    def test_application_component_delegates_once_to_isolated_owner(self):
        spec = importlib.util.spec_from_file_location('isolated_owner_test', ROOT / 'release-package.py')
        package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(package)
        with tempfile.TemporaryDirectory() as folder, \
             patch.object(package, 'validate_revision', side_effect=lambda value: value), \
             patch.object(package, 'contract_hash', return_value='b' * 64), \
             patch.object(package, 'package_identity', return_value='c' * 64), \
             patch.object(package, 'build_static', side_effect=AssertionError('host build')), \
             patch.object(package, 'build_dotnet', side_effect=AssertionError('host build')), \
             patch.object(package, 'build_isolated_component', side_effect=lambda revision, component, output:
                 (output / package.component_file(component)).write_bytes(b'verified isolated bytes')) as isolated:
            output = Path(folder)
            for component in package.APPS:
                receipt = package.build_component('a' * 40, component, output)
                self.assertEqual(hashlib.sha256(b'verified isolated bytes').hexdigest(), receipt['sha256'])
                isolated.assert_called_with('a' * 40, component, output)
            self.assertEqual(len(package.APPS), isolated.call_count)

    def test_content_component_keeps_actual_producer_and_rejects_unsafe_reuse(self):
        spec = importlib.util.spec_from_file_location('release_package_test', ROOT / 'release-package.py')
        package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(package)
        candidate, producer = 'a' * 40, 'b' * 40
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            component = 'portal'
            path = directory / package.component_file(component)
            with zipfile.ZipFile(path, 'w') as archive:
                archive.writestr('wwwroot/_deployment-provenance.json', json.dumps({'releaseSha': producer}))
            receipt = dict(schema=package.CONTENT_COMPONENT_SCHEMA, producerRevision=producer,
                contentIdentity='c' * 64, executionIdentity='e' * 64, component=component, file=path.name,
                sha256=package.sha256_file(path))
            receipt_path = directory / (component + '.component.json')
            receipt_path.write_text(json.dumps(receipt))
            original = path.read_bytes()
            with patch.object(package, 'validate_revision', side_effect=lambda value: value), \
                 patch.object(package._RELEASE_AUTHORITY, 'package_component_manifest',
                    return_value=dict(contentIdentity='c' * 64, reusable=True)) as closure:
                with self.assertRaisesRegex(ValueError, 'execution environment unproven'):
                    package.verify_component(candidate, component, directory)
                with self.assertRaisesRegex(ValueError, 'content receipt mismatch'):
                    package.verify_component(candidate, component, directory, execution_identity='f' * 64)
                self.assertEqual(receipt, package.verify_component(candidate, component, directory, execution_identity='e' * 64))
                self.assertEqual(original, path.read_bytes())
                self.assertEqual(producer, package.embedded_revision(path, False))
                closure.side_effect = [dict(contentIdentity='d' * 64, reusable=True), dict(contentIdentity='c' * 64, reusable=True)]
                with self.assertRaisesRegex(ValueError, 'dependencies changed'):
                    package.verify_component(candidate, component, directory, execution_identity='e' * 64)
                closure.side_effect = None
                closure.return_value = dict(contentIdentity='c' * 64, reusable=False)
                with self.assertRaisesRegex(ValueError, 'closure unproven'):
                    package.verify_component(candidate, component, directory, execution_identity='e' * 64)
                closure.return_value = dict(contentIdentity='c' * 64, reusable=True)
                receipt['producerRevision'] = candidate
                receipt_path.write_text(json.dumps(receipt))
                with self.assertRaisesRegex(ValueError, 'embedded producer mismatch'):
                    package.verify_component(candidate, component, directory, execution_identity='e' * 64)
                receipt['producerRevision'] = producer
                receipt['sha256'] = 'f' * 64
                receipt_path.write_text(json.dumps(receipt))
                with self.assertRaisesRegex(ValueError, 'content receipt mismatch'):
                    package.verify_component(candidate, component, directory, execution_identity='e' * 64)

    def test_deployment_and_workflow_orchestration_changes_preserve_package_identity(self):
        spec = importlib.util.spec_from_file_location('release_package_test', ROOT / 'release-package.py')
        package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(package)
        with tempfile.TemporaryDirectory() as folder:
            checkout = Path(folder)
            for relative in package.CONTRACT_INPUTS:
                dest = checkout / relative
                dest.parent.mkdir(parents=True, exist_ok=True)
                dest.write_bytes((ROOT.parent / relative).read_bytes())
            with patch.object(package, 'ROOT', checkout):
                original = package.package_identity('a' * 40)
                (checkout / 'scripts/deploy-approved-app.py').write_text('changed deployment reconciliation')
                self.assertEqual(original, package.package_identity('a' * 40))
                workflow = checkout / package._RELEASE_AUTHORITY.PACKAGE_BUILD_WORKFLOW
                content = workflow.read_text()
                workflow.write_text(content + '\n# unrelated orchestration comment\n')
                self.assertEqual(original, package.package_identity('a' * 40))
                workflow.write_text(content.replace("dotnet-version: '10.0.401'", "dotnet-version: '10.0.402'"))
                self.assertNotEqual(original, package.package_identity('a' * 40))

    def test_package_contract_includes_migration_bundle_identity(self):
        spec = importlib.util.spec_from_file_location('release_package_test', ROOT / 'release-package.py')
        package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(package)
        original = package.contract_hash()
        with patch.object(package, 'MIGRATION_BUNDLE', 'changed-migration-bundle'):
            self.assertNotEqual(original, package.contract_hash())


    def test_isolated_migration_component_owns_its_project_restore(self):
        spec = importlib.util.spec_from_file_location('release_package_test', ROOT / 'release-package.py')
        package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(package)
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)
            (output / package.MIGRATION_BUNDLE).write_bytes(b'isolated fixture')
            with patch.object(package, 'run') as execute:
                package.build_migration_bundle(output)
            calls = [call.args for call in execute.call_args_list]
            startup = 'scripts/MigrationReleaseProbe/MigrationReleaseProbe.csproj'
            self.assertEqual(('dotnet', 'restore', startup, '--nologo'), calls[0])
            self.assertEqual(('dotnet', 'tool', 'restore'), calls[1])
            self.assertEqual(startup, calls[2][calls[2].index('--startup-project') + 1])
            self.assertEqual(3, len(calls))
            self.assertNotIn('AgentPortal/AgentPortal.csproj', sum((list(row) for row in calls), []))


    def test_rehearsed_migration_component_is_promoted_without_rebuilding(self):
        spec = importlib.util.spec_from_file_location('release_package_test', ROOT / 'release-package.py')
        package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(package)
        revision, body = 'a' * 40, b'exact validated migration bytes'
        digest = hashlib.sha256(body).hexdigest()
        receipt = dict(schema=package.COMPONENT_SCHEMA, applicationReleaseSha=revision,
            packageContractSha256='b' * 64, packageIdentity='c' * 64,
            component='migration', file=package.MIGRATION_BUNDLE, sha256=digest)
        proof = {'receipt': {'rehearsalSource': {'runId': 91, 'artifact': 'retained-rehearsal', 'artifactId': 123},
                             'rehearsal': {'bundleDigest': digest}}}
        def download(repo, run, name, root, *, artifact_id):
            self.assertEqual(123, artifact_id)
            self.assertEqual((91, 'retained-rehearsal'), (run, name))
            (root / 'migration').mkdir()
            (root / 'migration' / package.MIGRATION_BUNDLE).write_bytes(body)
            (root / 'migration/migration.component.json').write_text(json.dumps(receipt))
        with tempfile.TemporaryDirectory() as temporary, \
             patch.dict(os.environ, {'GITHUB_ACTIONS': 'true', 'GITHUB_EVENT_NAME': 'pull_request', 'GITHUB_REPOSITORY': 'MYLEGND/masterapp'}), \
             patch.object(package, 'validate_revision', return_value=revision), \
             patch.object(package, 'contract_hash', return_value='b' * 64), \
             patch.object(package, 'package_identity', return_value='c' * 64), \
             patch.object(package._RELEASE_AUTHORITY, 'require_readiness', return_value=proof), \
             patch.object(package._RELEASE_AUTHORITY, '_download_run_artifact', side_effect=download) as restore, \
             patch.object(package, 'build_migration_bundle') as build:
            output = Path(temporary)
            self.assertEqual(receipt, package.promote_rehearsed_migration(revision, output, proof))
            self.assertEqual(body, (output / package.MIGRATION_BUNDLE).read_bytes())
            self.assertEqual('reused-success', json.loads((output / 'migration.reuse.json').read_text())['state'])
            self.assertEqual(1, restore.call_count)
            build.assert_not_called()
            # A new job attempt retains the same validated bytes without trying
            # to recreate the previous attempt's immutable upload identity.
            first = package.promoted_migration_artifact(revision, 1)
            second = package.promoted_migration_artifact(revision, 2)
            self.assertNotEqual(first, second)
            self.assertEqual(second, package.promoted_migration_artifact(revision, 2))
            self.assertEqual(receipt, package.promote_rehearsed_migration(revision, output, proof))
            self.assertEqual(body, (output / package.MIGRATION_BUNDLE).read_bytes())
            build.assert_not_called()

    def test_reused_immutable_package_keeps_original_manifest_and_bytes(self):
        spec = importlib.util.spec_from_file_location('release_package_test', ROOT / 'release-package.py')
        package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(package)
        revision = 'a' * 40
        declared_contract = 'd' * 64
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            files = {}
            for app, (_, filename, static) in package.APPS.items():
                with zipfile.ZipFile(directory / filename, 'w') as archive:
                    archive.writestr('_deployment-provenance.txt' if static else 'wwwroot/_deployment-provenance.json',
                                     revision if static else json.dumps({'releaseSha': revision}))
                files[filename] = package.sha256_file(directory / filename)
            (directory / package.MIGRATION_BUNDLE).write_bytes(b'preserved bundle')
            files[package.MIGRATION_BUNDLE] = package.sha256_file(directory / package.MIGRATION_BUNDLE)
            manifest = {'schema': package.SCHEMA, 'applicationReleaseSha': revision, 'applicationTreeSha': 'tree',
                        'packageContractSha256': declared_contract, 'files': files}
            manifest['packageIdentity'] = hashlib.sha256(json.dumps({'schema': package.SCHEMA,
                'applicationReleaseSha': revision, 'packageContractSha256': declared_contract}, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
            (directory / 'manifest.json').write_text(json.dumps(manifest))
            (directory / 'SHA256SUMS').write_text(''.join(digest + '  ' + name + '\n' for name, digest in files.items()))
            before = {path.name: path.read_bytes() for path in directory.iterdir()}
            with patch.object(package, 'validate_revision', return_value=revision), \
                 patch.object(package.subprocess, 'check_output', side_effect=['tree', 'b' * 40]), \
                 patch.object(package._RELEASE_AUTHORITY, 'package_inputs_compatible', return_value=True):
                actual = package.verify_all(revision, directory)
            self.assertEqual(revision, actual['applicationReleaseSha'])
            self.assertEqual(declared_contract, actual['packageContractSha256'])
            self.assertEqual(before, {path.name: path.read_bytes() for path in directory.iterdir()})


class PackageProducerTests(unittest.TestCase):
    def test_green_package_child_survives_red_parent_and_reuses_original_producer(self):
        authority = deploy._RELEASE_AUTHORITY
        producer = 'a' * 40
        run = {'id': 12, 'head_sha': producer, 'conclusion': 'failure',
               'path': '.github/workflows/' + authority.PACKAGE_VALIDATION_WORKFLOW,
               'event': 'pull_request', 'status': 'completed',
               'head_repository': {'full_name': 'MYLEGND/masterapp'}}
        artifact = {'name': 'founder-diagnostics-packages-' + 'c' * 64,
                    'workflow_run': {'id': 12}, 'expired': False}
        jobs = {'jobs': [{'name': 'validated-release-package', 'conclusion': 'success', 'steps': [
            {'name': name, 'conclusion': 'success'} for name in (
                'Build immutable validated release package', 'Verify immutable validated release package',
                'Preserve immutable validated release package')]}]}
        def api(repository, path, token):
            if path.startswith('actions/workflows/'):
                return {'workflow_runs': [run]}
            if path.startswith('actions/artifacts?') or '/artifacts?' in path:
                return {'artifacts': [artifact]}
            if '/jobs?' in path:
                return jobs
            return run
        with patch.object(authority, 'api_get', side_effect=api), \
             patch.object(authority.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0)), \
             patch.object(authority, 'git_changed', return_value=[]), \
             patch.object(authority, 'package_inputs_compatible', return_value=True):
            result = authority.compatible_package_producer('MYLEGND/masterapp', 'b' * 40, 'test-token')
            self.assertEqual(producer, result['revision'])
            self.assertEqual('c' * 64, result['packageIdentity'])
            jobs['jobs'][0]['steps'][1]['conclusion'] = 'failure'
            self.assertIsNone(authority.compatible_package_producer('MYLEGND/masterapp', 'b' * 40, 'test-token'))

    def test_historical_package_workflow_shape_is_incompatible_not_planner_failure(self):
        authority = deploy._RELEASE_AUTHORITY
        with patch.object(authority, 'git_changed', return_value=[authority.PACKAGE_BUILD_WORKFLOW]), \
             patch.object(authority, 'git_show_file', side_effect=['old workflow', 'new workflow']), \
             patch.object(authority, 'package_builder_workflow_contract', side_effect=ValueError('old shape')):
            self.assertFalse(authority.package_inputs_compatible('a' * 40, 'b' * 40))

    def test_builder_globals_and_invoked_helpers_invalidate_compatibility(self):
        authority = deploy._RELEASE_AUTHORITY
        source = (ROOT / 'release-package.py').read_text()
        with patch.object(authority, 'git_changed', return_value=['scripts/release-package.py']):
            for changed in (source.replace('ROOT = Path(__file__).resolve().parents[1]', 'ROOT = Path("/different-project")'),
                            source.replace('def run(*args, env=None):', 'def run(*args, env=None):\n    print("changed")'),
                            source.replace('def verify_all(revision: str, directory: Path):', 'def verify_all(revision: str, directory: Path):\n    (directory / "agentportal.zip").write_bytes(b"changed")'),
                            source.replace('bundle.chmod(0o755)', 'bundle.chmod(0o644)')):
                with patch.object(authority, 'git_show_file', side_effect=[source, changed]):
                    self.assertFalse(authority.package_inputs_compatible('a' * 40, 'b' * 40))


class ParallelPublicationTests(unittest.TestCase):
    def plan(self, keys):
        return {'schemaVersion': 1, 'targets': [{'app': key, 'packageDigest': 'c'*64} for key in keys]}

    def test_parallel_publication_settles_every_prepared_target_and_records_results(self):
        keys = ('portal', 'client', 'protect')
        names = [deploy.TARGETS[key]['releaseName'] for key in keys]
        with tempfile.TemporaryDirectory() as directory, \
             patch.object(deploy._RELEASE_AUTHORITY, 'selected_release_target_keys', return_value=keys), \
             patch.object(deploy, 'publish_prepared_target',
                          side_effect=lambda key, revision, package_root, plan: 'deployed'):
            results = deploy.publish_prepared_targets_parallel(
                names,
                'a' * 40,
                Path(directory),
                self.plan(keys),
                Path(directory) / 'results',
            )
            self.assertEqual(set(keys), set(results))
            for key in keys:
                payload = json.loads((Path(directory) / 'results' / f'{key}.json').read_text())
                self.assertTrue(payload['liveProven'])
                self.assertTrue(payload['durableReceiptProven'])
                self.assertEqual('publication', payload['phase'])
                self.assertEqual('a' * 40, payload['candidateRevision'])
                self.assertEqual(key, payload['target'])
                self.assertNotIn('success', payload)

    def test_parallel_publication_enters_all_selected_targets_before_any_worker_finishes(self):
        keys = ('portal', 'client', 'protect', 'parfait', 'website')
        names = [deploy.TARGETS[key]['releaseName'] for key in keys]
        barrier = threading.Barrier(len(keys))
        entered = []
        entered_lock = threading.Lock()

        def publish(key, revision, package_root, plan):
            with entered_lock:
                entered.append(key)
            barrier.wait(timeout=5)
            return 'deployed'

        with tempfile.TemporaryDirectory() as directory, \
             patch.object(deploy._RELEASE_AUTHORITY, 'selected_release_target_keys', return_value=keys), \
             patch.object(deploy, 'publish_prepared_target', side_effect=publish):
            results = deploy.publish_prepared_targets_parallel(
                names,
                'a' * 40,
                Path(directory),
                self.plan(keys),
                Path(directory) / 'results',
            )

        self.assertEqual(set(keys), set(entered))
        self.assertEqual(set(keys), set(results))
        self.assertTrue(all(
            result['liveProven'] and result['durableReceiptProven']
            for result in results.values()
        ))

    def test_receipt_pending_is_never_reported_as_deployment_success(self):
        keys = ('portal',)
        names = [deploy.TARGETS['portal']['releaseName']]
        with tempfile.TemporaryDirectory() as directory, \
             patch.object(deploy._RELEASE_AUTHORITY, 'selected_release_target_keys', return_value=keys), \
             patch.object(deploy, 'publish_prepared_target', return_value='deployed-receipt-pending'):
            results = deploy.publish_prepared_targets_parallel(
                names,
                'a' * 40,
                Path(directory),
                self.plan(keys),
                Path(directory) / 'results',
            )
            payload = results['portal']
            self.assertTrue(payload['liveProven'])
            self.assertFalse(payload['durableReceiptProven'])
            self.assertEqual('deployed-receipt-pending', payload['outcome'])
            self.assertNotIn('success', payload)

    def test_parallel_publication_preserves_successful_sibling_when_one_target_fails(self):
        keys = ('portal', 'protect')
        names = [deploy.TARGETS[key]['releaseName'] for key in keys]

        def publish(key, revision, package_root, plan):
            if key == 'protect':
                raise deploy.DeploymentReconciliationRequired('fixture failure')
            return 'deployed'

        with tempfile.TemporaryDirectory() as directory, \
             patch.object(deploy._RELEASE_AUTHORITY, 'selected_release_target_keys', return_value=keys), \
             patch.object(deploy, 'publish_prepared_target', side_effect=publish):
            with self.assertRaisesRegex(RuntimeError, 'protect'):
                deploy.publish_prepared_targets_parallel(
                    names,
                    'a' * 40,
                    Path(directory),
                    self.plan(keys),
                    Path(directory) / 'results',
                )
            root = Path(directory) / 'results'
            portal = json.loads((root / 'portal.json').read_text())
            self.assertTrue(portal['liveProven'])
            self.assertTrue(portal['durableReceiptProven'])
            failed = json.loads((root / 'protect.json').read_text())
            self.assertFalse(failed['liveProven'])
            self.assertFalse(failed['durableReceiptProven'])
            self.assertNotIn('success', failed)
            self.assertEqual('DeploymentReconciliationRequired', failed['errorType'])


class SubmissionBoundaryEvidenceTests(unittest.TestCase):
    def test_intent_readback_failure_proves_only_this_invocation_did_not_submit(self):
        class Journal:
            baseline = 'b' * 40
            intent = None
            history_error = None
            def before_submit(self, *args, **kwargs):
                raise RuntimeError('secret provider response must not escape')
        azure = FakeAzure([[row('old', 4)]], [False])
        azure.revision = 'a' * 40
        azure.observed_revision = lambda: 'b' * 40
        with self.assertRaises(deploy.DeploymentSubmissionNotEntered):
            azure.run(journal=Journal())
        self.assertEqual(0, azure.uploads)

    def test_retained_intent_never_becomes_invocation_nonentry_proof(self):
        azure = FakeAzure([[row('old', 4)]], [False])
        journal = RetainedJournal()
        journal.intent = {'baselineDeploymentIds': ['old']}
        with self.assertRaises(deploy.DeploymentReconciliationRequired) as caught:
            azure.run(journal=journal)
        self.assertNotIsInstance(caught.exception, deploy.DeploymentSubmissionNotEntered)
        self.assertEqual(0, azure.uploads)

    def test_parallel_failure_retains_sibling_and_sanitized_boundary(self):
        keys = ('portal', 'protect')
        for error, state in [(deploy.DeploymentSubmissionNotEntered('private payload'), 'not-entered'),
                             (RuntimeError('private payload'), 'may-have-entered')]:
            calls = []
            def publish(key, *args):
                calls.append(key)
                if key == 'protect':
                    raise error
                return 'preserved'
            with tempfile.TemporaryDirectory() as directory, \
                 patch.object(deploy._RELEASE_AUTHORITY, 'selected_release_target_keys', return_value=keys), \
                 patch.object(deploy, 'publish_prepared_target', side_effect=publish):
                root = Path(directory)
                with self.assertRaises(RuntimeError):
                    deploy.publish_prepared_targets_parallel([], 'a' * 40, root,
                        ParallelPublicationTests().plan(keys), root)
                failed = json.loads((root / 'protect.json').read_text())
                self.assertEqual(state, failed['failure']['invocationSubmission'])
                self.assertEqual('unresolved', failed['failure']['operationOutcome'])
                self.assertFalse(failed['failure']['replayAuthorized'])
                self.assertNotIn('private payload', json.dumps(failed))
                self.assertTrue(json.loads((root / 'portal.json').read_text())['durableReceiptProven'])
                self.assertCountEqual(keys, calls)


class MigrationProbeEvidenceTests(unittest.TestCase):
    def test_probe_tool_changes_do_not_relabel_application_or_accept_runtime_drift(self):
        authority = deploy._RELEASE_AUTHORITY
        workflow = (ROOT.parent / '.github/workflows/masterapp-platform-architecture-validation.yml').read_text()
        runtime = '100644 blob ' + '1' * 40 + '\tInfrastructure/Migrations/Example.cs\0'
        helper = '100644 blob ' + '2' * 40 + '\tscripts/MigrationReleaseProbe/Program.cs\0'
        def tree(command, **kwargs):
            return runtime + (helper if command[-1] == 'b' * 40 else '')
        with patch.object(authority.subprocess, 'check_output', side_effect=tree), \
             patch.object(authority, 'git_show_file', return_value=workflow):
            identity = authority.migration_probe_identity('b' * 40, 'a' * 40)
            self.assertEqual(identity, authority.migration_probe_identity('b' * 40, 'b' * 40))
        with patch.object(authority.subprocess, 'check_output', side_effect=[runtime, runtime.replace('1' * 40, '3' * 40)]), \
             patch.object(authority, 'git_show_file', return_value=workflow):
            with self.assertRaisesRegex(ValueError, 'different candidate'):
                authority.migration_probe_identity('b' * 40, 'a' * 40)


class DisjointReleaseRevisionTests(unittest.TestCase):
    def test_queued_immutable_candidate_survives_unrelated_target_movement(self):
        spec = importlib.util.spec_from_file_location('release_baseline_test', ROOT / 'approved-release-baseline.py')
        baseline = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(baseline)
        with patch.object(baseline.subprocess, 'run'), \
             patch.object(baseline.subprocess, 'check_output', return_value='Protect-Website/Program.cs\n'):
            self.assertEqual('a' * 40, baseline.validated_application_revision('a' * 40, 'b' * 40, ['masterapp-client']))
            with self.assertRaisesRegex(ValueError, 'application changes'):
                baseline.validated_application_revision('a' * 40, 'b' * 40, ['masterapp-protect'])
        with patch.object(baseline.subprocess, 'run'), \
             patch.object(baseline.subprocess, 'check_output', return_value='Infrastructure/Services/Runtime.cs\n'):
            with self.assertRaisesRegex(ValueError, 'application changes'):
                baseline.validated_application_revision('a' * 40, 'b' * 40, ['masterapp-client'])


class PreparedTransactionTests(unittest.TestCase):
    def plan(self, keys=None):
        plan = {'schemaVersion': 1, 'candidateRevision': 'a' * 40,
                'targets': [{'app': key, 'revision': 'b' * 40, 'packageDigest': 'c' * 64}
                            for key in (keys or list(deploy.TARGETS)[:2])]}
        identity = {'candidateRevision': plan['candidateRevision'], 'packageDigests': {row['app']: row['packageDigest'] for row in plan['targets']}}
        plan['planId'] = hashlib.sha256(json.dumps(identity, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
        return plan

    def mixed_plan(self):
        plan=self.plan();plan['schemaVersion']=2
        for index,row in enumerate(plan['targets'],1):
            key=row['app']; producer=('d' if index==1 else 'e')*40
            source=dict(runId=71,runAttempt=2,artifactId=80+index,artifactDigest='sha256:'+'f'*64,
                artifactName=deploy._RELEASE_AUTHORITY.component_attempt_name(key,'1'*64,71,2),
                receiptArtifactId=90+index,producingJobId=100+index)
            row['targetMaterial']=dict(producerRevision=producer,packageDigest=row['packageDigest'],
                contentIdentity='1'*64,executionIdentity='2'*64,source=source)
        plan['planId']=deploy._RELEASE_AUTHORITY.release_transaction_identity(plan['candidateRevision'],
            {row['app']:row['packageDigest'] for row in plan['targets']},
            {row['app']:row['targetMaterial'] for row in plan['targets']})
        return plan

    def test_mixed_producer_plan_binds_exact_material_and_rejects_restamping(self):
        plan=self.mixed_plan()
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'plan.json';path.write_text(json.dumps(plan))
            self.assertEqual(plan,deploy.read_transaction_plan(path,'a'*40))
            plan['targets'][0]['targetMaterial']['producerRevision']='a'*40
            path.write_text(json.dumps(plan))
            with self.assertRaisesRegex(ValueError,'content identity mismatch'):
                deploy.read_transaction_plan(path,'a'*40)

    def test_mixed_producer_publication_uses_original_bytes_and_separate_authorization(self):
        plan=self.mixed_plan();row=plan['targets'][0];material=row['targetMaterial'];journal=object()
        with patch.object(deploy,'verify_package',return_value='c'*64) as verify, \
             patch.object(deploy,'operation_journal',return_value=journal) as evidence, \
             patch.object(deploy,'deploy_one',return_value='preserved') as publish:
            self.assertEqual('preserved',deploy.publish_prepared_target(row['app'],'a'*40,Path('/packages'),plan))
        self.assertEqual(material['producerRevision'],verify.call_args.args[1])
        evidence.assert_called_once_with(row['app'],material['producerRevision'],'c'*64,'b'*40,
            candidate_revision='a'*40,component_source=material['source'])
        publish.assert_called_once_with(row['app'],material['producerRevision'],Path('/packages'),
            baseline='b'*40,reconcile_only=False,journal=journal)

    def test_live_proof_observes_each_intended_producer(self):
        import types
        plan=self.mixed_plan();authority=deploy._RELEASE_AUTHORITY
        expected={row['app']:row['targetMaterial']['producerRevision'] for row in plan['targets']}
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'plan.json';path.write_text(json.dumps(plan));output=Path(directory)/'live.json'
            args=types.SimpleNamespace(revision='a'*40,selected_targets=json.dumps([
                deploy.TARGETS[key]['releaseName'] for key in expected]),transaction_plan=path,
                output=output,timeout_seconds=1,poll_seconds=0)
            with patch.object(authority,'_read_provenance',side_effect=lambda host,target,revision:revision) as observe:
                authority.cmd_verify_live(args)
            rows=json.loads(output.read_text())
            self.assertEqual(set(expected.values()),{row['actual'] for row in rows})
            self.assertTrue(all(row['candidateRevision']=='a'*40 for row in rows))
            self.assertNotIn('a'*40,{call.args[2] for call in observe.call_args_list})

    def test_mixed_producer_finalization_preserves_successful_sibling(self):
        plan=self.mixed_plan();calls={};expected={row['app']:row['targetMaterial']['producerRevision'] for row in plan['targets']}
        def azure(key,path,producer):
            self.assertEqual(expected[key],producer)
            return key
        def reconcile(key,**kwargs):
            self.assertTrue(kwargs['reconcile_only']);self.assertTrue(kwargs['require_receipt'])
            calls[key]=calls.get(key,0)+1
            if key=='client' and calls[key]==1:
                raise deploy.DeploymentReconciliationRequired('lost receipt')
        with patch.object(deploy,'verify_package',return_value='c'*64), \
             patch.object(deploy,'operation_journal',return_value=object()), \
             patch.object(deploy,'target_azure',side_effect=azure), \
             patch.object(deploy,'reconcile',side_effect=reconcile):
            deploy.finalize_prepared_transaction(plan,Path('/packages'),'a'*40,sleep=lambda _:None)
        self.assertEqual({'portal':1,'client':2},calls)

    def test_finalizer_reconstructs_missing_receipt_without_upload_or_sibling_replay(self):
        plan = self.plan()
        journals, providers = {}, {}
        for index, target in enumerate(plan['targets']):
            key = target['app']
            class Journal:
                baseline = 'b' * 40
                intent = {'baselineDeploymentIds': ['old']}
                def __init__(self, fail_first):
                    self.fail_first, self.receipt_attempts = fail_first, 0
                def record_success(self, ids):
                    self.receipt_attempts += 1
                    if self.fail_first and self.receipt_attempts == 1:
                        raise RuntimeError('lost receipt acknowledgment')
                    return {'phase': 'success'}
            journals[key] = Journal(index == 1)
            provider = FakeAzure([[row('old', 4), row('new', 4)]], [True])
            provider.revision = 'a' * 40
            provider.observed_revision = lambda: 'a' * 40
            providers[key] = provider
        original_reconcile = deploy.reconcile
        def bounded(provider, **kwargs):
            self.assertIsNotNone(kwargs.get('journal'))
            return original_reconcile(provider, clock=lambda: provider.now,
                                      sleep=provider.sleep, interval=1, **kwargs)
        with patch.object(deploy, 'verify_package', return_value='c' * 64), \
             patch.object(deploy, 'operation_journal', side_effect=lambda key, *_: journals[key]), \
             patch.object(deploy, 'target_azure', side_effect=lambda key, *_: providers[key]), \
             patch.object(deploy, 'reconcile', side_effect=bounded):
            deploy.finalize_prepared_transaction(plan, Path('/packages'), 'a' * 40, sleep=lambda _: None)
        self.assertEqual([1, 2], [j.receipt_attempts for j in journals.values()])
        self.assertEqual([0, 0], [p.uploads for p in providers.values()])

    def test_finalization_is_read_only_and_retries_only_unresolved_targets(self):
        plan = self.plan()
        calls = {}
        sleeps = []
        journal = object()

        class FinalizeAzure:
            def __init__(self, key):
                self.key = key

        def target_azure(key, *_):
            return FinalizeAzure(key)

        def reconcile(azure, **kwargs):
            calls[azure.key] = calls.get(azure.key, 0) + 1
            self.assertTrue(kwargs['reconcile_only'])
            self.assertEqual(deploy.FINALIZE_RECONCILE_TIMEOUT_SECONDS, kwargs['timeout'])
            self.assertEqual(1, kwargs['max_status_failures'])
            self.assertIs(journal, kwargs['journal'])
            self.assertTrue(kwargs['require_receipt'])
            if azure.key == plan['targets'][1]['app'] and calls[azure.key] < 3:
                raise deploy.DeploymentReconciliationRequired('receipt pending')
            return 'preserved'

        with patch.object(deploy, 'verify_package', return_value='c' * 64), \
             patch.object(deploy, 'operation_journal', return_value=journal):
            with patch.object(deploy, 'target_azure', side_effect=target_azure):
                with patch.object(deploy, 'reconcile', side_effect=reconcile):
                    deploy.finalize_prepared_transaction(
                        plan, Path('/packages'), 'a' * 40, sleep=sleeps.append)

        self.assertEqual(1, calls[plan['targets'][0]['app']])
        self.assertEqual(3, calls[plan['targets'][1]['app']])
        self.assertEqual([10, 20], sleeps)

    def test_finalization_drift_fails_without_retry(self):
        plan = self.plan([list(deploy.TARGETS)[0]])
        with patch.object(deploy, 'verify_package', return_value='c' * 64), \
             patch.object(deploy, 'operation_journal', return_value=object()):
            with patch.object(deploy, 'reconcile', side_effect=deploy.DeploymentDrift('drift')) as reconcile:
                with self.assertRaisesRegex(deploy.DeploymentDrift, 'drift'):
                    deploy.finalize_prepared_transaction(
                        plan, Path('/packages'), 'a' * 40, sleep=lambda _: None)
        self.assertEqual(1, reconcile.call_count)

    def test_finalization_cannot_commit_without_a_durable_journal(self):
        with patch.object(deploy, 'verify_package', return_value='c' * 64), \
             patch.object(deploy, 'operation_journal', return_value=None), \
             patch.object(deploy, 'reconcile') as reconcile:
            with self.assertRaisesRegex(ValueError, 'durable deployment journal'):
                deploy.finalize_prepared_transaction(
                    self.plan(), Path('/packages'), 'a' * 40, sleep=lambda _: None)
        reconcile.assert_not_called()

    def test_target_cannot_publish_modified_package_after_preflight(self):
        plan = self.plan()
        with patch.object(deploy, 'verify_package', return_value='d' * 64), patch.object(deploy, 'deploy_one') as publish:
            with self.assertRaisesRegex(ValueError, 'package changed'):
                deploy.publish_prepared_target(plan['targets'][0]['app'], 'a' * 40, Path('/packages'), plan)
        publish.assert_not_called()

    def test_reconcile_only_flag_survives_prepared_target_entrypoint(self):
        plan = self.plan()
        with patch.object(deploy, 'verify_package', return_value='c' * 64), patch.object(deploy, 'deploy_one') as publish:
            deploy.publish_prepared_target(plan['targets'][0]['app'], 'a' * 40, Path('/packages'), plan, reconcile_only=True)
        self.assertTrue(publish.call_args.kwargs['reconcile_only'])

    def test_prepare_restores_original_untouched_target_baseline_and_rollback_reference(self):
        prior = self.plan(['portal', 'client'])
        rollback = {'artifact': 'original-rollback', 'runId': 17, 'revision': 'b' * 40, 'packageDigest': 'd' * 64}
        for row in prior['targets']:
            row['rollbackEvidence'] = rollback
        env = {'GITHUB_ACTIONS': 'true', 'GITHUB_REPOSITORY': 'MYLEGND/masterapp',
               'GITHUB_RUN_ID': '22', 'GITHUB_RUN_ATTEMPT': '1', 'GITHUB_TOKEN': 'test-token'}
        names = [deploy.TARGETS[row['app']]['releaseName'] for row in prior['targets']]
        observed = json.dumps([{'app': row['app'], 'revision': 'c' * 40} for row in prior['targets']])
        with tempfile.TemporaryDirectory() as folder, patch.dict(os.environ, env), \
             patch.object(deploy, 'verify_package', return_value='c' * 64), \
             patch.object(deploy._RELEASE_AUTHORITY, 'release_transaction_plan_history', return_value=prior), \
             patch.object(deploy, 'operation_journal', return_value=None), \
             patch.object(deploy, 'retained_rollback_package', return_value=rollback) as restore, \
             patch.object(deploy, 'preflight_target') as preflight:
            plan = deploy.prepare_transaction(names, observed, Path('/packages'), Path('/rollback'), 'a' * 40, Path(folder) / 'plan.json')
        self.assertEqual(['b' * 40, 'b' * 40], [row['revision'] for row in plan['targets']])
        self.assertTrue(all(call.args[3] is rollback for call in restore.call_args_list))
        self.assertTrue(all(call.args[3] == 'b' * 40 for call in preflight.call_args_list))

    def test_terminal_disposition_retries_transient_azure_history_timeout_without_writes(self):
        from types import SimpleNamespace
        plan = self.plan(['parfait'])
        journal = SimpleNamespace(intent={'baselineDeploymentIds': ['old']}, history_error=None)
        azure = FakeAzure(
            [subprocess.TimeoutExpired(['az', 'webapp', 'log', 'deployment', 'list'], 20),
             [row('old', 4), row('ours', 4)]],
            [False])
        azure.observed_revision = lambda: 'a' * 40
        with patch.object(deploy, 'verify_package', return_value='c' * 64), \
             patch.object(deploy, 'operation_journal', return_value=journal), \
             patch.object(deploy, 'target_azure', return_value=azure), \
             patch.object(deploy.time, 'sleep') as sleeper:
            result = deploy.transaction_disposition(plan, Path('/packages'), 'a' * 40)

        self.assertTrue(result['terminal'])
        self.assertEqual([{'target': 'parfait', 'revision': 'a' * 40, 'idle': True}], result['targets'])
        sleeper.assert_called_once_with(10)
        self.assertEqual(0, azure.uploads)
        self.assertEqual(0, azure.recovery_uploads)

    def test_idle_baseline_cannot_discharge_an_ambiguous_prior_upload(self):
        from types import SimpleNamespace
        plan = self.plan(['portal'])
        journal = SimpleNamespace(intent={'baselineDeploymentIds': ['old']}, history_error=None)
        azure = FakeAzure([[row('old', 4)]], [False])
        azure.observed_revision = lambda: 'b' * 40
        with patch.object(deploy, 'verify_package', return_value='c' * 64), \
             patch.object(deploy, 'operation_journal', return_value=journal), \
             patch.object(deploy, 'target_azure', return_value=azure):
            with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired, 'ambiguous'):
                deploy.transaction_disposition(plan, Path('/packages'), 'a' * 40)
            azure.states = [[row('old', 4), row('ours', 3)]]
            result = deploy.transaction_disposition(plan, Path('/packages'), 'a' * 40)
            self.assertTrue(result['terminal'])
            self.assertEqual(0, azure.uploads)

    def test_prepared_plan_rejects_other_candidate_and_target(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'plan.json'
            path.write_text(json.dumps(self.plan(['portal'])))
            with self.assertRaises(ValueError):
                deploy.read_transaction_plan(path, 'd' * 40)
            with self.assertRaises(ValueError):
                deploy.read_transaction_plan(path, 'a' * 40, 'client')


class DeploymentReadinessTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location('readiness_prepublication_test', ROOT / 'release-prepublication.py')
        self.owner = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.owner)
        self.targets = [row['releaseName'] for row in deploy.TARGETS.values()]
        self.profile = deploy._RELEASE_AUTHORITY.release_runtime_profile(self.targets)
        self.env = {'RELEASE_RESOURCE_GROUP': self.profile['resourceGroup'],
                    'DATABASE_AUTHORITY': self.profile['databaseAuthority']}

    def test_target_readiness_uses_only_provider_reads_and_canonical_configuration(self):
        calls = []
        def observe(*args):
            calls.append(args)
            if args[:2] == ('webapp', 'show'):
                name = args[args.index('-n') + 1]
                return {'id': '/subscriptions/test/resourceGroups/test/providers/Microsoft.Web/sites/' + name, 'state': 'Running'}
            self.assertEqual(('webapp', 'log', 'deployment', 'list'), args[:4])
            return []
        with patch.dict(os.environ, self.env), patch.object(self.owner, 'az_json', side_effect=observe), \
             patch.object(self.owner, 'source_authority') as source, patch.object(self.owner, 'app_settings'), \
             patch.object(self.owner, 'configure_target') as mutate:
            result = self.owner.deployment_readiness(self.targets)
        self.assertEqual('executed-success', result['state'])
        self.assertEqual(set(self.targets), {row['target'] for row in result['targets']})
        self.assertEqual(2 * len(self.targets), len(calls))
        self.assertEqual(1, source.call_count)
        mutate.assert_not_called()

    def test_missing_target_blocks_without_configuration_mutation(self):
        with patch.dict(os.environ, self.env), patch.object(self.owner, 'az_json', return_value={}), \
             patch.object(self.owner, 'source_authority'), patch.object(self.owner, 'configure_target') as mutate:
            with self.assertRaisesRegex(RuntimeError, 'missing or not running'):
                self.owner.deployment_readiness(self.targets[:1])
        mutate.assert_not_called()

    def test_wrong_environment_is_rejected_before_provider_access(self):
        with patch.dict(os.environ, self.env | {'RELEASE_RESOURCE_GROUP': 'wrong'}), \
             patch.object(self.owner, 'az_json') as provider:
            with self.assertRaisesRegex(RuntimeError, 'canonical inventory'):
                self.owner.deployment_readiness(self.targets)
        provider.assert_not_called()


class SettingsIdempotenceTests(unittest.TestCase):
    def run_settings(self, drift=False):
        authority = deploy._RELEASE_AUTHORITY
        all_names = [row['releaseName'] for row in deploy.TARGETS.values()]
        profile = authority.release_runtime_profile(all_names)
        shared_target = profile['sharedAuthTargets'][0]
        editor_target = next(name for name in profile['editorTargets'] if name != shared_target)

        spec = importlib.util.spec_from_file_location(
            'release_prepublication_test',
            ROOT / 'release-prepublication.py',
        )
        prepublication = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(prepublication)

        common = {
            'FOUNDER_OID': 'test-founder',
            'Founder__Upn': 'test@example.invalid',
            'DataProtection__BlobUri': 'test-blob',
            'DataProtection__KeyVaultKeyId': 'test-key',
            'MarketingDataProtection__BlobUri': 'test-blob',
            'MarketingDataProtection__KeyVaultKeyId': 'test-key',
            'WebsiteEditorDataProtection__BlobUri': 'test-blob',
            'WebsiteEditorDataProtection__KeyVaultKeyId': 'test-key',
            'Analytics__SharedSecret': 'test-secret',
            'Tracking__SharedSecret': 'test-secret',
            'Tracking:SharedSecret': 'test-secret',
            'WEBSITE_NODE_DEFAULT_VERSION': '~24',
        }
        state = {row['releaseName']: dict(common) for row in deploy.TARGETS.values()}
        if drift:
            state[shared_target]['Tracking:SharedSecret'] = 'stale'
            state[editor_target]['WebsiteEditorDataProtection__BlobUri'] = 'stale'

        writes = []
        lock = threading.Lock()

        def fake_settings(app):
            with lock:
                return dict(state[app])

        def fake_run(command, *, capture=False, timeout=300, env=None):
            self.assertEqual(['az', 'webapp', 'config', 'appsettings', 'set'], command[:5])
            app = command[command.index('-n') + 1]
            pairs = command[command.index('--settings') + 1:command.index('--output')]
            updates = dict(pair.split('=', 1) for pair in pairs)
            with lock:
                self.assertTrue(updates)
                self.assertTrue(all(state[app].get(key) != value for key, value in updates.items()))
                state[app].update(updates)
                writes.append({'app': app, 'keys': sorted(updates)})
            return ''

        environment = {
            'SELECTED_TARGETS': json.dumps(list(state)),
            'RELEASE_RESOURCE_GROUP': profile['resourceGroup'],
            'DATABASE_AUTHORITY': profile['databaseAuthority'],
            'SHARED_AUTH_TARGETS': json.dumps(profile['sharedAuthTargets']),
            'MARKETING_TARGETS': json.dumps(profile['marketingTargets']),
            'EDITOR_TARGETS': json.dumps(profile['editorTargets']),
            'ROUTING_TARGETS': '[]',
            'WEBSITE_ROUTING': 'false',
            'PRESERVE_LIVE_TARGETS': 'false',
        }
        with patch.dict(os.environ, environment, clear=False), \
             patch.object(prepublication, 'app_settings', side_effect=fake_settings), \
             patch.object(prepublication, 'run', side_effect=fake_run), \
             patch.object(prepublication, 'child_receipt'):
            prepublication.configure_all_targets()
            prepublication.configure_all_targets()

        return sorted(writes, key=lambda row: row['app']), shared_target, editor_target

    def test_matching_settings_make_zero_azure_writes_across_repeated_runs(self):
        writes, _, _ = self.run_settings()
        self.assertEqual([], writes)

    def test_only_drifted_keys_are_updated_once_and_then_preserved(self):
        writes, shared_target, editor_target = self.run_settings(drift=True)
        expected = sorted([
            {'app': shared_target, 'keys': ['Tracking:SharedSecret']},
            {'app': editor_target, 'keys': ['WebsiteEditorDataProtection__BlobUri']},
        ], key=lambda row: row['app'])
        self.assertEqual(expected, writes)


class MigrationMetadataAdmissionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location(
            'prepublication_metadata_admission_test', ROOT / 'release-prepublication.py')
        cls.owner = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.owner)

    def test_probe_restore_uses_immutable_id_and_rejects_missing_identity(self):
        owner = self.owner
        from types import SimpleNamespace
        from unittest.mock import Mock
        from contextlib import nullcontext
        download = Mock()
        budget = Mock(side_effect=lambda deadline: nullcontext())
        with patch.dict(os.environ, {'GITHUB_REPOSITORY': 'owner/repo'}), \
             patch.object(owner.time, 'monotonic', return_value=1000), \
             patch.object(owner, 'release_authority', return_value=SimpleNamespace(_download_run_artifact=download, evidence_lookup_budget=budget)):
            owner.restore_migration_probe(dict(runId=7, artifact='probe', artifactId=19))
            download.assert_called_once_with('owner/repo', 7, 'probe', Path('/tmp/migration-probe'), artifact_id=19)
            for value in (None, 0, -1, True, '19'):
                with self.subTest(value=value), self.assertRaisesRegex(RuntimeError, 'immutable identity'):
                    owner.restore_migration_probe(dict(runId=7, artifact='probe', artifactId=value))
            self.assertEqual(1, download.call_count)
            budget.assert_called_once_with(1240)

    def test_registered_metadata_and_context_changes_require_probe(self):
        owner = self.owner
        paths = (
            'Infrastructure/Data/LegacyMetaAttributionMigrationMetadata.cs\n'
            'Infrastructure/Data/MasterAppDbContext.cs\n'
        )
        with patch.object(owner, 'run', return_value=paths) as git:
            self.assertTrue(owner.migration_metadata_changed('a' * 40, 'b' * 40))
        git.assert_called_once_with([
            'git', 'diff', '--name-only', 'a' * 40, 'b' * 40,
            '--', 'Infrastructure/Data', 'Infrastructure/Migrations',
        ], capture=True)

    def test_probe_preparation_preserves_typed_recovery_metadata_and_redacts_message(self):
        owner = self.owner
        authority = owner.release_authority()
        error = authority.EvidenceLookupUnavailable('untrusted provider detail',
            status=503, endpoint='artifact-read', rate={'remaining': '0'})
        with patch.object(owner, 'release_authority', return_value=authority), \
             self.assertRaises(authority.EvidenceLookupUnavailable) as caught:
            owner.migration_preparation('probe-restoration', lambda: (_ for _ in ()).throw(error))
        self.assertIs(error, caught.exception)
        self.assertEqual(503, error.code)
        self.assertEqual('artifact-read', error.endpoint)
        self.assertEqual({'remaining': '0'}, error.rate)
        self.assertEqual('LEGEND_PREPUBLICATION_MIGRATION:Migration stage unresolved: probe-restoration', str(error))
        self.assertNotIn('untrusted', str(error))

    def test_delayed_probe_provider_exhausts_one_budget_before_migration(self):
        from types import SimpleNamespace
        owner = self.owner
        authority = owner.release_authority()
        budget = authority.evidence_lookup_budget
        elapsed, calls = [0.0], []
        def get(*args, **kwargs):
            calls.append(kwargs['timeout'])
            elapsed[0] += min(119, kwargs['timeout'])
            return SimpleNamespace(returncode=1, stderr='HTTP 503')
        environment = dict(PRESERVE_LIVE_TARGETS='false', SELECTED_DATABASE_DEPENDENT='true',
            EXPECTED_DB_BASE_SHA='a' * 40, APPLICATION_RELEASE_SHA='b' * 40,
            DATABASE_AUTHORITY='portal', GITHUB_REPOSITORY='owner/repo', MIGRATION_READINESS_PENDING='false')
        with tempfile.TemporaryDirectory() as temporary, \
             patch.dict(os.environ, environment), \
             patch.object(owner, 'git_ok', return_value=True), \
             patch.object(owner, 'changed_migrations', return_value=[]), \
             patch.object(owner, 'release_proven'), \
             patch.object(owner, 'run', return_value=json.dumps(dict(runId=7, artifact='probe', artifactId=19))), \
             patch.object(owner, 'Path', return_value=Path(temporary)), \
             patch.object(owner, 'release_authority', return_value=authority), \
             patch.object(owner.time, 'monotonic', side_effect=lambda: elapsed[0]), \
             patch.object(authority, 'evidence_lookup_budget', side_effect=lambda deadline: budget(deadline, clock=lambda: elapsed[0])), \
             patch.object(authority.time, 'sleep', side_effect=lambda delay: elapsed.__setitem__(0, elapsed[0] + delay)), \
             patch.object(authority.subprocess, 'run', side_effect=get), \
             patch.object(owner, '_invoke_migration_bundle') as migrate:
            with self.assertRaises(authority.EvidenceLookupUnavailable):
                owner.run_migration_lane()
            self.assertEqual(240, elapsed[0])
            self.assertEqual(2, len(calls))
            migrate.assert_not_called()

    def test_designer_only_migration_metadata_change_enters_existing_probe(self):
        owner = self.owner
        with patch.object(owner, 'run', return_value=(
            'Infrastructure/Migrations/20260516100000_AddMetaAttributionReconciliation.Designer.cs\n')):
            self.assertTrue(owner.migration_metadata_changed('a' * 40, 'b' * 40))

    def test_unrelated_migration_source_metadata_is_not_misclassified(self):
        owner = self.owner
        with patch.object(owner, 'run', return_value=(
            'Infrastructure/Migrations/README.md\n'
            'Infrastructure/Migrations/AnyOldHelper.cs\n')):
            self.assertFalse(owner.migration_metadata_changed('a' * 40, 'b' * 40))

    def test_unrelated_data_changes_do_not_trigger_schema_probe(self):
        owner = self.owner
        with patch.object(owner, 'run', return_value=(
            'Infrastructure/Data/SomeRepository.cs\n'
            'Infrastructure/Data/Info.txt\n')):
            self.assertFalse(owner.migration_metadata_changed('a' * 40, 'b' * 40))

    def test_metadata_only_release_reconciles_schema_without_replay(self):
        owner = self.owner
        environment = {
            'PRESERVE_LIVE_TARGETS': 'false',
            'SELECTED_DATABASE_DEPENDENT': 'true',
            'EXPECTED_DB_BASE_SHA': 'a' * 40,
            'APPLICATION_RELEASE_SHA': 'b' * 40,
            'DATABASE_AUTHORITY': 'masterapp-portal',
            'GITHUB_REPOSITORY': 'MYLEGND/masterapp',
            # Fresh production SQL readiness, not changed source metadata,
            # authorizes the actual pending migration to be reconciled.
            'MIGRATION_READINESS_PENDING': 'true',
        }
        def read(command, **kwargs):
            if 'resolve' in command:
                return json.dumps({'runId': 123, 'artifact': 'validated-probe'})
            if command[:3] == ['git', 'rev-parse', 'HEAD']:
                return 'c' * 40
            return ''

        with (
            patch.dict(os.environ, environment),
            patch.object(owner, 'git_ok', return_value=True),
            patch.object(owner, 'changed_migrations', return_value=[]),
            patch.object(owner, 'migration_metadata_changed', return_value=True),
            patch.object(owner, 'release_proven') as release_proof,
            patch.object(owner, 'run', side_effect=read) as command,
            patch.object(owner, 'restore_migration_probe'),
            patch.object(owner, '_invoke_migration_bundle') as migrate,
        ):
            result = owner.run_migration_lane()
        self.assertEqual({'status': 'reconciled', 'changedMigrations': []}, result)
        release_proof.assert_called_once()
        migrate.assert_called_once()
        self.assertTrue(any('verify' in call.args[0] for call in command.call_args_list))

    def test_non_schema_changes_still_require_fresh_schema_reconciliation(self):
        owner = self.owner
        environment = {
            'PRESERVE_LIVE_TARGETS': 'false',
            'SELECTED_DATABASE_DEPENDENT': 'true',
            'EXPECTED_DB_BASE_SHA': 'a' * 40,
            'APPLICATION_RELEASE_SHA': 'b' * 40,
            'MIGRATION_READINESS_PENDING': 'false',
            'DATABASE_AUTHORITY': 'masterapp-portal',
            'GITHUB_REPOSITORY': 'MYLEGND/masterapp',
        }
        with (
            patch.dict(os.environ, environment),
            patch.object(owner, 'git_ok', return_value=True),
            patch.object(owner, 'changed_migrations', return_value=[]),
            patch.object(owner, 'migration_metadata_changed', return_value=False),
            patch.object(owner, '_invoke_migration_bundle') as migrate,
            patch.object(owner, 'release_proven') as release_proof,
            patch.object(owner, 'run', return_value=json.dumps({'runId': 123, 'artifact': 'validated-probe'})),
            patch.object(owner, 'restore_migration_probe'),
        ):
            result = owner.run_migration_lane()
        self.assertEqual({'status': 'reconciled', 'changedMigrations': []}, result)
        migrate.assert_called_once()  # Canonical ready-state tests prove zero SQL execution.
        release_proof.assert_called_once()


class RedactedPrepublicationDiagnosticsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import io
        from types import SimpleNamespace
        cls.io = io
        cls.SimpleNamespace = SimpleNamespace
        spec = importlib.util.spec_from_file_location(
            'prepublication_diagnostics_test', ROOT / 'release-prepublication.py')
        cls.owner = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.owner)

    def test_canonical_schema_reason_is_preserved_without_retry(self):
        owner = self.owner
        text = ('Schema probe runtime invalid operation; history status unknown'
                '; preserve prior evidence and reconcile without replay.')
        result = subprocess.CompletedProcess([], 1, '', text)
        with patch.object(owner.subprocess, 'run', return_value=result) as call:
            with self.assertRaisesRegex(RuntimeError, 'LEGEND_PREPUBLICATION_MIGRATION:Schema probe runtime invalid operation'):
                owner._invoke_migration_bundle()
        call.assert_called_once()

    def test_mutation_admission_reason_is_reported_without_exposing_provider_text(self):
        owner = self.owner
        import io
        marker = ('LEGEND_MIGRATION_ADMISSION_DIAGNOSTIC:'
                  'HISTORICAL_PREWRITE_PROOF_REJECTED:run=37765986494:attempt=1')
        failure = ('Migration stage unresolved: mutation-admission'
                   '; preserve prior evidence and reconcile without replay.')
        output = io.StringIO()
        response = subprocess.CompletedProcess([], 1, marker + '\n', failure)
        with patch.object(owner.subprocess, 'run', return_value=response), \
             patch('sys.stdout', output):
            with self.assertRaisesRegex(RuntimeError,
                                        'LEGEND_PREPUBLICATION_MIGRATION:Migration stage unresolved: mutation-admission'):
                owner._invoke_migration_bundle()
        self.assertIn(marker, output.getvalue())
        self.assertIn('::error title=LEGEND migration admission::', output.getvalue())
        output = io.StringIO()
        response = subprocess.CompletedProcess([], 1, marker + '\nPassword=secret', failure)
        with patch.object(owner.subprocess, 'run', return_value=response), \
             patch('sys.stdout', output):
            with self.assertRaises(RuntimeError):
                owner._invoke_migration_bundle()
        self.assertEqual('', output.getvalue())

    def test_unknown_provider_text_never_reaches_diagnostics(self):
        owner = self.owner
        result = subprocess.CompletedProcess([], 1, '',
            'Authentication error at https://secret.invalid/?sig=hidden')
        with patch.object(owner.subprocess, 'run', return_value=result) as call:
            with self.assertRaisesRegex(RuntimeError, 'LEGEND_PREPUBLICATION_MIGRATION:UNKNOWN_FAILURE') as caught:
                owner._invoke_migration_bundle()
        self.assertNotIn('hidden', str(caught.exception))
        call.assert_called_once()

    def test_both_lanes_are_observed_even_when_configuration_fails(self):
        owner = self.owner
        output = self.io.StringIO()
        errors = self.io.StringIO()
        authority = self.SimpleNamespace(assert_protected_release_execution=lambda: None)
        safe = 'LEGEND_PREPUBLICATION_MIGRATION:Applied database migration history is not in validated sequence'
        with patch.object(owner, 'release_authority', return_value=authority), \
             patch.object(owner, 'configure_all_targets', side_effect=RuntimeError('private config detail')), \
             patch.object(owner, 'run_migration_lane', side_effect=RuntimeError(safe)), \
             patch('sys.stdout', output), patch('sys.stderr', errors):
            with self.assertRaisesRegex(RuntimeError, 'Pre-publication lanes require exact reconciliation'):
                owner.main()
        self.assertIn('LEGEND_PREPUBLICATION:CONFIGURATION:FAILED', errors.getvalue())
        self.assertIn(safe, errors.getvalue())
        self.assertNotIn('private config detail', errors.getvalue())

    def test_successful_configuration_is_preserved_when_migrations_fail(self):
        owner = self.owner
        output = self.io.StringIO()
        errors = self.io.StringIO()
        authority = self.SimpleNamespace(assert_protected_release_execution=lambda: None)
        with patch.object(owner, 'release_authority', return_value=authority), \
             patch.object(owner, 'configure_all_targets', return_value={'targets': []}), \
             patch.object(owner, 'run_migration_lane', side_effect=RuntimeError('secret DB provider detail')), \
             patch('sys.stdout', output), patch('sys.stderr', errors):
            with self.assertRaises(RuntimeError):
                owner.main()
        self.assertIn('LEGEND_PREPUBLICATION:CONFIGURATION:READY', output.getvalue())
        self.assertIn('LEGEND_PREPUBLICATION:MIGRATION:FAILED', errors.getvalue())
        self.assertNotIn('secret DB provider detail', errors.getvalue())



    def test_only_bound_unknown_history_ids_cross_prepublication_log(self):
        owner = self.owner
        valid = ('LEGEND_SCHEMA_HISTORY:1:20260927053000_AddAdvertisingActionAuthorizations')
        fixed = ('Database contains applied migration history absent from validated bundle'
                 '; preserve prior evidence and reconcile without replay.')
        output = self.io.StringIO()
        result = subprocess.CompletedProcess([], 1, valid + '\n', fixed)
        with patch.object(owner.subprocess, 'run', return_value=result) as job, \
             patch('sys.stdout', output):
            with self.assertRaisesRegex(RuntimeError, 'LEGEND_PREPUBLICATION_MIGRATION:Database contains applied'):
                owner._invoke_migration_bundle()
        self.assertEqual(valid + '\n', output.getvalue())
        job.assert_called_once()

    def test_prepublication_never_relays_unvalidated_db_provider_values(self):
        owner = self.owner
        fixed = ('Database contains applied migration history absent from validated bundle'
                 '; preserve prior evidence and reconcile without replay.')
        for raw in ('LEGEND_SCHEMA_HISTORY:1:Password=private',
                    'LEGEND_SCHEMA_HISTORY:2:20260927053000_OK;Server=private',
                    'random secret text'):
            with self.subTest(raw=raw):
                output = self.io.StringIO()
                with patch.object(owner.subprocess, 'run',
                        return_value=subprocess.CompletedProcess([], 1, raw, fixed)), \
                     patch('sys.stdout', output):
                    with self.assertRaises(RuntimeError):
                        owner._invoke_migration_bundle()
                self.assertEqual('', output.getvalue())


class CompletionProofReuseTests(unittest.TestCase):
    def test_efficiency_reporting_failure_never_replaces_outcome(self):
        a=deploy._RELEASE_AUTHORITY
        with patch.object(a,'report_recovery_efficiency',side_effect=RuntimeError('report failed')), \
             patch.object(deploy,'_complete_fast_track',side_effect=ValueError('original failure')):
            with self.assertRaisesRegex(ValueError,'original failure'):
                deploy.complete_fast_track(1,'a'*40,Path('/unused'))
        with patch.object(a,'report_recovery_efficiency',side_effect=RuntimeError('report failed')), \
             patch.object(deploy,'_complete_fast_track',return_value={'state':'ok'}):
            self.assertEqual(deploy.complete_fast_track(1,'a'*40,Path('/unused'))['state'],'ok')
    def fixture(self):
        a=deploy._RELEASE_AUTHORITY
        revision='a'*40
        run=dict(id=1,run_attempt=1,head_sha=revision,status='completed',conclusion='failure',
            path='.github/workflows/legend-fast-track-ui.yml',head_branch='Legend/fast-track-release',
            event='push',head_repository=dict(full_name='owner/repo'))
        job=dict(id=11,run_id=1,run_attempt=1,head_sha=revision,status='completed',conclusion='failure',
            started_at='2026-10-27T03:00:00Z',completed_at='2026-10-27T03:01:00Z')
        record=dict(operationId='b'*64,target='portal',applicationRevision=revision,
            producingRun=1,producingAttempt=1,phase='intent')
        artifact=dict(id=10,name='legend-release-operation-intent-'+'b'*64,expired=False,
            workflow_run=dict(id=1),digest='sha256:'+'c'*64)
        counts=dict(download=0,log=0,metadata=0)
        def api(repo,path,token):
            counts['metadata']+=1
            if path=='actions/runs/1':return dict(run)
            if path=='actions/jobs/11':return dict(job)
            if path.startswith('actions/artifacts?'):return dict(artifacts=[dict(artifact)],total_count=1)
            raise AssertionError(path)
        def download(repo,run_id,name,directory,**kwargs):
            counts['download']+=1
            (directory/'operation.json').write_text(json.dumps(record))
        def log(*args):counts['log']+=1;return 'authenticated original log'
        def read():
            value=a.release_operation_history('owner/repo','b'*64,revision,'portal',2,1,'test-only')
            self.assertEqual(value,record)
            self.assertEqual(a._release_job_log('owner/repo',11,'test-only'),'authenticated original log')
        return a,run,job,artifact,counts,api,download,log,read

    def test_counted_immutable_bytes_reused_but_metadata_decisions_are_fresh(self):
        a,run,job,artifact,counts,api,download,log,read=self.fixture()
        with patch.dict(os.environ,dict(GITHUB_RUN_ID='2')),patch.object(a,'_api_get_uncached',side_effect=api),patch.object(a,'_download_run_artifact',side_effect=download),patch.object(a,'_release_job_log_uncached',side_effect=log):
            a._RELEASE_HISTORY_API.clear()
            # Count the same real history/log transports without reuse.
            for _ in range(68):read()
            self.assertEqual((counts['download'],counts['log']),(68,68))
            counts.update(download=0,log=0,metadata=0)
            def body(*args,**kwargs):
                for _ in range(2):
                    with a.recovery_admission_proof():
                        for _ in range(34):read()
                return dict(counts)
            with patch.object(deploy,'_complete_fast_track',side_effect=body):
                first=deploy.complete_fast_track(1,'a'*40,Path('/unused'))
                self.assertEqual((first['download'],first['log']),(1,1))
                self.assertGreaterEqual(first['metadata'],12) # each decision + raw exit readback
                second=deploy.complete_fast_track(1,'a'*40,Path('/unused'))
                self.assertEqual((second['download'],second['log']),(2,2)) # no cross-invocation cache
            self.assertIsNone(a._RECOVERY_RECEIPT_BYTES.get())
            self.assertIsNone(a._RECOVERY_LOG_BYTES.get())
            self.assertIsNone(a._RECOVERY_PROOF_VIEW.get())

    def test_current_observer_and_ref_never_reuse_mutable_metadata(self):
        a=deploy._RELEASE_AUTHORITY; calls=[]
        def api(repo,path,token):
            calls.append(path)
            return dict(id=2,status='in_progress',run_attempt=len(calls)) if path=='actions/runs/2' else dict(object=dict(sha=str(len(calls))))
        def body(*args,**kwargs):
            with a.recovery_admission_proof():
                for path in ['actions/runs/2','git/ref/heads/Legend/fast-track-release']:
                    self.assertNotEqual(a.api_get('owner/repo',path,'test-only'),a.api_get('owner/repo',path,'test-only'))
        with patch.dict(os.environ,dict(GITHUB_RUN_ID='2')),patch.object(a,'_api_get_uncached',side_effect=api),patch.object(deploy,'_complete_fast_track',side_effect=body):
            deploy.complete_fast_track(1,'a'*40,Path('/unused'))
        self.assertEqual(len(calls),4)

    def test_terminal_identity_change_between_decisions_revokes_cached_log(self):
        a,run,job,artifact,counts,api,download,log,read=self.fixture()
        def body(*args,**kwargs):
            with a.recovery_admission_proof():read()
            job['status']='in_progress'
            with a.recovery_admission_proof():
                with self.assertRaisesRegex(a.ReleaseOperationHistoryUnproven,'TERMINAL_IDENTITY'):
                    read()
        with patch.dict(os.environ,dict(GITHUB_RUN_ID='2')),patch.object(a,'_api_get_uncached',side_effect=api),patch.object(a,'_download_run_artifact',side_effect=download),patch.object(a,'_release_job_log_uncached',side_effect=log),patch.object(deploy,'_complete_fast_track',side_effect=body):
            deploy.complete_fast_track(1,'a'*40,Path('/unused'))
        self.assertEqual(counts['log'],1)

    def test_artifact_expiry_and_digest_change_do_not_use_stale_bytes(self):
        a,run,job,artifact,counts,api,download,log,read=self.fixture()
        def body(*args,**kwargs):
            with a.recovery_admission_proof():read()
            artifact['expired']=True
            with a.recovery_admission_proof(),self.assertRaises(a.ReleaseOperationHistoryUnproven):read()
            artifact['expired']=False;artifact['digest']='sha256:'+'d'*64
            with a.recovery_admission_proof():read()
        with patch.dict(os.environ,dict(GITHUB_RUN_ID='2')),patch.object(a,'_api_get_uncached',side_effect=api),patch.object(a,'_download_run_artifact',side_effect=download),patch.object(a,'_release_job_log_uncached',side_effect=log),patch.object(deploy,'_complete_fast_track',side_effect=body):
            deploy.complete_fast_track(1,'a'*40,Path('/unused'))
        self.assertEqual(counts['download'],2)

    def test_invocation_exception_discards_all_byte_caches(self):
        a=deploy._RELEASE_AUTHORITY
        with patch.object(deploy,'_complete_fast_track',side_effect=ValueError('hold')):
            with self.assertRaisesRegex(ValueError,'hold'):deploy.complete_fast_track(1,'a'*40,Path('/unused'))
        self.assertIsNone(a._RECOVERY_RECEIPT_BYTES.get())
        self.assertIsNone(a._RECOVERY_LOG_BYTES.get())
        self.assertIsNone(a._RECOVERY_PROOF_VIEW.get())


class CompletionOnlyTests(unittest.TestCase):
    def test_readonly_facade_never_submits(self):
        from types import SimpleNamespace
        facade=deploy.CompletionObservation(SimpleNamespace(revision='a'*40),SimpleNamespace())
        for method in [facade.submit,facade.prepare_submission]:
            with self.assertRaises(deploy.DeploymentSubmissionNotEntered):method()
    def fixture(self,status=4,received='2026-10-21T13:02:00Z',revision='a'*40,extra=False):
        from types import SimpleNamespace
        journal=SimpleNamespace(baseline='b'*40,intent=dict(baselineDeploymentIds=['old']),provider=None,
            material=dict(intentArtifact=dict(created_at='2026-10-21T13:01:00Z'),originalPublicationStep=dict(name='Publish or reconcile exact application bytes',status='completed',conclusion='failure',started_at='2026-10-21T13:00:00Z',completed_at='2026-10-21T13:03:00Z'),originalJob=dict(completed_at='2026-10-21T13:03:00Z')),record_success=Mock())
        rows=[dict(id='old',status=4),dict(id='new',status=status,received_time=received)]
        if extra:rows.append(dict(id='other',status=4,received_time=received))
        azure=SimpleNamespace(revision='a'*40,deployments=Mock(return_value=rows),observed_revision=Mock(return_value=revision),submit=Mock(),prepare_submission=Mock())
        facade=deploy.CompletionObservation(azure,journal);now=[0]
        def run():return deploy.reconcile(facade,journal=journal,reconcile_only=True,clock=lambda:now[0],sleep=lambda n:now.__setitem__(0,now[0]+n),interval=1,timeout=3)
        return azure,journal,run
    def test_v5_actual_portal_cleanup_operation_never_records_success(self):
        azure,journal,run=self.fixture(received='2026-10-21T14:08:36Z')
        journal.material['originalPublicationStep'].update(started_at='2026-10-21T13:59:13Z',completed_at='2026-10-21T14:08:34Z')
        journal.material['intentArtifact']['created_at']='2026-10-21T13:59:14Z'
        journal.material['originalJob']['completed_at']='2026-10-21T14:08:37Z'
        with self.assertRaises(deploy.DeploymentDrift):run()
        journal.record_success.assert_not_called();azure.submit.assert_not_called();azure.prepare_submission.assert_not_called()
    def test_v5_final_represented_second_is_allowed_but_next_second_is_not(self):
        for received,allowed in [('2026-10-21T13:03:00.999999Z',True),('2026-10-21T13:03:01Z',False)]:
            azure,journal,run=self.fixture(received=received)
            with self.subTest(received=received):
                if allowed:self.assertEqual(run(),'preserved');journal.record_success.assert_called_once_with(['new'])
                else:
                    with self.assertRaises(deploy.DeploymentDrift):run()
                    journal.record_success.assert_not_called()
    def test_v5_missing_or_inverted_publication_proof_never_records(self):
        for missing in [True,False]:
            azure,journal,run=self.fixture()
            if missing:journal.material.pop('originalPublicationStep')
            else:journal.material['originalPublicationStep']['started_at']='2026-10-21T13:04:00Z'
            with self.subTest(missing=missing),self.assertRaises(deploy.DeploymentDrift):run()
            journal.record_success.assert_not_called()

    def test_v6_fractional_end_before_exact_and_after_live(self):
        for end,before,exact,after in [('14:08:34.500000','14:08:34.499999','14:08:34.500000','14:08:35.250000'),('14:08:34.000','14:08:33.999999','14:08:34','14:08:34.000001'),('14:08:34,500000','14:08:34.499999','14:08:34.500000','14:08:35.250000')]:
            for received,allowed in [(before,True),(exact,False),(after,False)]:
                azure,journal,run=self.fixture(received='2026-10-21T'+received+'Z')
                journal.material['originalPublicationStep'].update(started_at='2026-10-21T13:59:13Z',completed_at='2026-10-21T'+end+'Z')
                journal.material['intentArtifact']['created_at']='2026-10-21T13:59:14Z'
                journal.material['originalJob']['completed_at']='2026-10-21T14:08:37Z'
                with self.subTest(end=end,received=received):
                    if allowed:self.assertEqual(run(),'preserved');journal.record_success.assert_called_once_with(['new'])
                    else:
                        with self.assertRaises(deploy.DeploymentDrift):run()
                        journal.record_success.assert_not_called()
                    azure.submit.assert_not_called();azure.prepare_submission.assert_not_called()

    def test_exact_original_idle_deployment_records_after_stable_reads(self):
        azure,journal,run=self.fixture();self.assertEqual(run(),'preserved')
        self.assertEqual(azure.deployments.call_count,2);journal.record_success.assert_called_once_with(['new'])
        self.assertEqual(journal.provider['runtimeRevision'],'a'*40)
        azure.submit.assert_not_called();azure.prepare_submission.assert_not_called()
    def test_missing_active_failed_concurrent_or_wrong_revision_never_records(self):
        for kwargs in [dict(status=2),dict(status=3),dict(extra=True),dict(revision='c'*40),dict(revision=None)]:
            azure,journal,run=self.fixture(**kwargs)
            with self.subTest(kwargs=kwargs),self.assertRaises((deploy.DeploymentDrift,deploy.DeploymentReconciliationRequired)):run()
            journal.record_success.assert_not_called();azure.submit.assert_not_called();azure.prepare_submission.assert_not_called()
    def test_deployment_outside_original_window_never_records(self):
        for received in ['2026-10-21T13:00:00Z','2026-10-21T13:04:00Z',None]:
            azure,journal,run=self.fixture(received=received)
            with self.subTest(received=received),self.assertRaises((ValueError,deploy.DeploymentDrift,deploy.DeploymentReconciliationRequired)):run()
            journal.record_success.assert_not_called();azure.submit.assert_not_called()
    def test_changed_or_disappearing_post_intent_id_never_records(self):
        first=[dict(id='old',status=4),dict(id='A',status=4,received_time='2026-10-21T13:02:00Z')]
        for later in [[dict(id='old',status=4),dict(id='B',status=4,received_time='2026-10-21T13:02:00Z')],[dict(id='old',status=4)],[]]:
            azure,journal,run=self.fixture();azure.deployments.side_effect=[first,later]
            with self.subTest(later=later),self.assertRaises(deploy.DeploymentDrift):run()
            journal.record_success.assert_not_called();azure.submit.assert_not_called();azure.prepare_submission.assert_not_called()
    def test_identity_conflict_is_terminal_even_if_original_id_reappears(self):
        azure,journal,_=self.fixture();facade=deploy.CompletionObservation(azure,journal)
        a=[dict(id='A',status=4,received_time='2026-10-21T13:02:00Z')]
        azure.deployments.side_effect=[a,[dict(id='B',status=4,received_time='2026-10-21T13:02:00Z')],a]
        facade.deployments()
        with self.assertRaises(deploy.DeploymentDrift):facade.deployments()
        with self.assertRaises(deploy.DeploymentDrift):facade.deployments()
        self.assertEqual(azure.deployments.call_count,2);journal.record_success.assert_not_called()
    def test_active_original_id_cannot_be_replaced_by_successful_other_id(self):
        azure,journal,run=self.fixture()
        azure.deployments.side_effect=[[dict(id='A',status=2)],[dict(id='B',status=4,received_time='2026-10-21T13:02:00Z')]]
        with self.assertRaises(deploy.DeploymentDrift):run()
        journal.record_success.assert_not_called();azure.submit.assert_not_called()
    def test_missing_receipt_fails_without_replay(self):
        azure,journal,run=self.fixture();journal.record_success.side_effect=TimeoutError()
        with self.assertRaises(deploy.DeploymentReconciliationRequired):run()
        journal.record_success.assert_called_once();azure.submit.assert_not_called()
    def test_prelogin_validation_verifies_package_without_provider_effect(self):
        from types import SimpleNamespace
        a=deploy._RELEASE_AUTHORITY
        with patch.dict(os.environ,dict(GITHUB_REPOSITORY='owner/repo',GH_TOKEN='test-only')),patch.object(a,'assert_fast_track_completion_execution',return_value=dict(toolRevision='b'*40)),patch.object(a,'assert_fast_track_completion_live_execution'),patch.object(a,'fast_track_completion_original',return_value=dict(targets=['portal'])),patch.object(a,'fast_track_completion_material',return_value=dict(packageArtifact=dict(id=10,name='package',digest='sha256:'+'d'*64),intent=dict(packageDigest='e'*64))),patch.object(deploy,'verify_package',return_value='e'*64),patch.object(Path,'is_file',return_value=True),patch.object(Path,'is_symlink',return_value=False),patch.object(deploy.subprocess,'run',return_value=SimpleNamespace(returncode=0)),patch.object(a,'_download_run_artifact') as download,patch.object(deploy,'target_azure') as provider:
            self.assertEqual(deploy.complete_fast_track(1,'a'*40,Path('/unused'),validate_only=True)['state'],'COMPLETION_PACKAGES_VERIFIED')
            download.assert_called_once();provider.assert_not_called()
    def test_bad_original_package_blocks_all_provider_observation(self):
        from types import SimpleNamespace
        a=deploy._RELEASE_AUTHORITY;material=dict(packageArtifact=dict(name='package',id=10,digest='sha256:'+'d'*64),intent=dict(packageDigest='e'*64))
        with patch.dict(os.environ,dict(GITHUB_REPOSITORY='owner/repo',GH_TOKEN='test-only')),patch.object(a,'assert_fast_track_completion_execution',return_value=dict(toolRevision='b'*40)),patch.object(a,'assert_fast_track_completion_live_execution'),patch.object(a,'fast_track_completion_original',return_value=dict(targets=['portal'])),patch.object(a,'fast_track_completion_material',return_value=material),patch.object(deploy.subprocess,'run',return_value=SimpleNamespace(returncode=0)),patch.object(a,'_download_run_artifact') as download,patch.object(deploy,'verify_package',return_value='wrong'),patch.object(deploy,'target_azure') as provider:
            with patch.object(Path,'is_file',return_value=True),patch.object(Path,'is_symlink',return_value=False):
                with self.assertRaisesRegex(ValueError,'PACKAGE_CHANGED'):deploy.complete_fast_track(1,'a'*40,Path('/unused'),validate_only=True)
            download.assert_called_once();provider.assert_not_called()

class CompletionPublicMetadataTests(unittest.TestCase):
    def responses(self,key):
        from unittest.mock import MagicMock
        origin='https://www.mylegnd.com' if key=='website' else 'https://protect.mylegnd.com'
        sitemap=(f'<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>{origin}/</loc></url></urlset>').encode()
        robots=('User-agent: *\nAllow: /\nSitemap: '+origin+'/sitemap.xml\n').encode()
        result=[]
        for path,mime,raw in [('/sitemap.xml','application/xml',sitemap),('/robots.txt','text/plain',robots)]:
            response=MagicMock(status=200,url=origin+path,headers={'Content-Type':mime})
            response.__enter__.return_value=response;response.read.return_value=raw
            result.append(response)
        return result

    def test_exact_original_request_contract_for_both_public_targets(self):
        for key in ['website','protect']:
            responses=self.responses(key)
            with self.subTest(key=key),patch.object(deploy,'verify_fast_track_favicon') as favicon,patch.object(deploy.urllib.request,'urlopen',side_effect=responses) as request:
                deploy.verify_fast_track_public_metadata(key)
                favicon.assert_called_once_with(key);self.assertEqual(request.call_count,2)
                for call,response,accept in zip(request.call_args_list,responses,['application/xml,text/xml','text/plain']):
                    self.assertEqual(call.args[0].full_url,response.url)
                    self.assertEqual(call.args[0].get_method(),'GET')
                    self.assertIsNone(call.args[0].data)
                    self.assertEqual(dict(call.args[0].header_items()),{'Accept':accept,'Cache-control':'no-cache','User-agent':'LegendSitemapReleaseVerification/1.0'})
                    self.assertEqual(call.kwargs,{'timeout':22})
                    response.read.assert_called_once_with(1048577)

    def test_http403_is_fatal_without_retry_or_subsequent_read(self):
        for key in ['website','protect']:
            for failure_index in [0,1]:
                responses=self.responses(key)
                error=deploy.urllib.error.HTTPError(responses[failure_index].url,403,'Forbidden',{},None)
                with self.subTest(key=key,path=responses[failure_index].url),patch.object(deploy,'verify_fast_track_favicon'),patch.object(deploy.urllib.request,'urlopen',side_effect=responses[:failure_index]+[error]) as request,patch.object(deploy.time,'sleep') as sleep:
                    with self.assertRaises(deploy.urllib.error.HTTPError) as caught:deploy.verify_fast_track_public_metadata(key)
                    self.assertIs(caught.exception,error)
                    self.assertEqual(request.call_count,failure_index+1);sleep.assert_not_called()

    def test_unchanged_transport_and_content_rejections(self):
        import xml.etree.ElementTree as ET
        for key in ['website','protect']:
            for defect in ['status','redirect','sitemap-mime','sitemap-large','xml','namespace','empty','duplicate','foreign-host','robots-status','robots-redirect','robots-mime','robots-large','robots-sitemap']:
                responses=self.responses(key);sitemap,robots=responses
                if defect=='status':sitemap.status=204
                elif defect=='redirect':sitemap.url+='?redirected=1'
                elif defect=='sitemap-mime':sitemap.headers={'Content-Type':'text/html'}
                elif defect=='sitemap-large':sitemap.read.return_value=b'x'*1048577
                elif defect=='xml':sitemap.read.return_value=b'<broken'
                elif defect=='namespace':sitemap.read.return_value=b'<urlset/>'
                elif defect=='empty':sitemap.read.return_value=b'<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"/>'
                elif defect=='duplicate':
                    raw=sitemap.read.return_value;url=raw[raw.index(b'<url>'):raw.index(b'</url>')+6];sitemap.read.return_value=raw.replace(b'</urlset>',url+b'</urlset>')
                elif defect=='foreign-host':sitemap.read.return_value=sitemap.read.return_value.replace(b'mylegnd.com',b'example.invalid')
                elif defect=='robots-status':robots.status=204
                elif defect=='robots-redirect':robots.url+='?redirected=1'
                elif defect=='robots-mime':robots.headers={'Content-Type':'text/html'}
                elif defect=='robots-large':robots.read.return_value=b'x'*1048577
                elif defect=='robots-sitemap':robots.read.return_value=b'User-agent: *\nAllow: /\nSitemap: https://example.invalid/sitemap.xml\n'
                with self.subTest(key=key,defect=defect),patch.object(deploy,'verify_fast_track_favicon'),patch.object(deploy.urllib.request,'urlopen',side_effect=responses):
                    with self.assertRaises(ET.ParseError if defect=='xml' else ValueError):deploy.verify_fast_track_public_metadata(key)

    def test_favicon_failure_precedes_metadata_requests(self):
        with patch.object(deploy,'verify_fast_track_favicon',side_effect=ValueError('favicon')),patch.object(deploy.urllib.request,'urlopen') as request:
            with self.assertRaisesRegex(ValueError,'favicon'):deploy.verify_fast_track_public_metadata('website')
            request.assert_not_called()

    def test_other_targets_keep_only_the_existing_favicon_obligation(self):
        for key in ['portal','client','parfait']:
            with self.subTest(key=key),patch.object(deploy,'verify_fast_track_favicon') as favicon,patch.object(deploy.urllib.request,'urlopen') as request:
                deploy.verify_fast_track_public_metadata(key)
                favicon.assert_called_once_with(key);request.assert_not_called()


class CompletionAdapterTests(unittest.TestCase):
    def run_adapter(self,*,probe_failure=False,observer_change=False,probe_failure_at=4,metadata_error=None,proof_change=False,live_failure_at=0):
        from types import SimpleNamespace
        a=deploy._RELEASE_AUTHORITY;revision='a'*40
        observer=dict(runId=2,runAttempt=1,jobId=20,toolRevision='b'*40,promotionRunId=3)
        original=dict(targets=['portal','website'],run=dict(id=1))
        material=dict(packageArtifact=dict(name='package',id=10,digest='sha256:'+'d'*64),intent=dict(packageDigest='e'*64))
        timeline=[];publish=Mock();owner=SimpleNamespace(publish_record=publish,CompletionJournal=Mock(side_effect=lambda *args,**kwargs:SimpleNamespace(baseline='f'*40)))
        spec=SimpleNamespace(loader=SimpleNamespace(exec_module=lambda _:None))
        completed={key:dict(operationId='c'*64) for key in original['targets']}
        count=[0]
        def metadata(key):
            timeline.append('probe:'+key);count[0]+=1
            if probe_failure and count[0]==probe_failure_at:raise metadata_error or ValueError('mock public metadata failure')
        def verify(*args):timeline.append('package');return 'e'*64
        def provider(*args):timeline.append('provider');return SimpleNamespace(revision=revision)
        auth_calls=[0]
        def auth(*args):
            self.assertIsNotNone(a._RECOVERY_PROOF_VIEW.get())
            auth_calls[0]+=1
            return dict(observer,jobId=99) if observer_change and auth_calls[0]>1 else observer
        reads=[0]
        def metadata_api(*args):
            reads[0]+=1
            return dict(id=999,status='completed',run_attempt=2 if reads[0]>1 else 1)
        def completed_proof(*args,**kwargs):
            if proof_change:
                a.api_get('owner/repo','actions/runs/999','test-only')
                a.api_get('owner/repo','actions/runs/999','test-only')
            return completed
        live_calls=[0]
        def live(*args):
            self.assertIsNone(a._RECOVERY_PROOF_VIEW.get());live_calls[0]+=1
            if live_calls[0]==live_failure_at:raise ValueError('live changed')
        with patch.object(Path,'is_file',return_value=True),patch.object(Path,'is_symlink',return_value=False),patch.dict(os.environ,dict(GITHUB_REPOSITORY='owner/repo',GH_TOKEN='test-only')),patch.object(a,'assert_fast_track_completion_execution',side_effect=auth),patch.object(a,'assert_fast_track_completion_live_execution',side_effect=live),patch.object(a,'_api_get_uncached',side_effect=metadata_api),patch.object(a,'fast_track_completion_original',return_value=original),patch.object(a,'fast_track_completion_material',return_value=material),patch.object(a,'fast_track_completed_operation_receipts',side_effect=completed_proof),patch.object(deploy.subprocess,'run',return_value=SimpleNamespace(returncode=0)),patch.object(a,'_download_run_artifact'),patch.object(deploy,'verify_package',side_effect=verify),patch.object(deploy,'target_azure',side_effect=provider),patch.object(deploy,'reconcile',return_value='preserved') as reconcile,patch.object(deploy,'verify_fast_track_public_metadata',side_effect=metadata),patch.object(deploy.importlib.util,'spec_from_file_location',return_value=spec),patch.object(deploy.importlib.util,'module_from_spec',return_value=owner):
            if probe_failure or observer_change or proof_change or live_failure_at:
                with self.assertRaises(a.ReleaseOperationHistoryUnproven if proof_change else type(metadata_error) if metadata_error else ValueError):deploy.complete_fast_track(1,revision,Path('/unused'))
                publish.assert_not_called()
                if probe_failure:self.assertEqual(reconcile.call_count,probe_failure_at//2)
            else:
                result=deploy.complete_fast_track(1,revision,Path('/unused'))
                self.assertEqual(result['state'],'ORIGINAL_COMPLETIONS_VERIFIED')
                name,record=publish.call_args.args
                self.assertEqual(record['targets'],completed);self.assertEqual(record['observer'],observer)
                self.assertEqual(record['originalRun'],1);self.assertEqual(record['producingRun'],2)
                self.assertEqual(reconcile.call_count,2)
                for call in reconcile.call_args_list:
                    self.assertTrue(call.kwargs['reconcile_only']);self.assertTrue(call.kwargs['require_receipt'])
            self.assertEqual(timeline[:2],['package','package'])
    def test_all_bytes_before_observation_and_aggregate_only_after_all_probes(self):self.run_adapter()
    def test_summary_live_guard_failure_never_publishes(self):self.run_adapter(live_failure_at=2)
    def test_last_public_probe_failure_never_emits_aggregate(self):self.run_adapter(probe_failure=True)
    def test_http403_before_or_after_website_reconciliation_never_emits_aggregate(self):
        for probe in [3,4]:
            with self.subTest(probe=probe):
                self.run_adapter(probe_failure=True,probe_failure_at=probe,metadata_error=deploy.urllib.error.HTTPError('https://www.mylegnd.com/sitemap.xml',403,'Forbidden',{},None))
    def test_changed_observer_never_emits_aggregate(self):self.run_adapter(observer_change=True)
    def test_final_summary_metadata_drift_never_emits_aggregate(self):self.run_adapter(proof_change=True)

class CompletionBoundaryTests(unittest.TestCase):
    def test_completion_cli_routes_only_to_completion_owner(self):
        with patch('sys.argv',['deploy','--complete-fast-track','123','--completion-revision','a'*40,'--validate-completion-only']),patch.object(deploy,'complete_fast_track',return_value={'state':'INPUTS_VALIDATED'}) as complete,patch.object(deploy,'target_azure',side_effect=AssertionError('provider')):
            deploy.main()
            self.assertEqual(complete.call_args.args[:2],(123,'a'*40));self.assertTrue(complete.call_args.kwargs['validate_only'])
    def test_mixed_completion_modes_fail_before_owner(self):
        for extra in [['--target','portal'],['--reconcile-only'],['--prepare-only'],['--validate-observation-only']]:
            with self.subTest(extra=extra),patch('sys.argv',['deploy','--complete-fast-track','123','--completion-revision','a'*40]+extra),patch.object(deploy,'complete_fast_track') as complete:
                with self.assertRaises(SystemExit):deploy.main()
                complete.assert_not_called()
    def test_workflow_exact_observer_and_no_deployment_commands(self):
        text=(ROOT.parent/'.github/workflows/deployment-diagnostics.yml').read_text().split('  complete-fast-track:',1)[1]
        for required in ['Complete existing fast-track deployment receipts',"github.ref == 'refs/heads/Legend/fast-track-release'",'group: legend-approved-release','cancel-in-progress: false','ref: ${{ github.sha }}','persist-credentials: false','pull-requests: read','--validate-completion-only','Reconcile original bytes and record only missing success receipts']:
            self.assertIn(required,text)
        for forbidden in ['az webapp deploy','az webapp restart','dotnet publish','dotnet build','config appsettings','workflow run']:
            self.assertNotIn(forbidden,text)
        self.assertLess(text.index('--validate-completion-only'),text.index('azure/login@'))

class CompletionPackagePhaseV4Tests(unittest.TestCase):
    def setup_call(self,root,download,*,validate=True):
        from contextlib import ExitStack
        from types import SimpleNamespace
        a=deploy._RELEASE_AUTHORITY;stack=ExitStack();self.addCleanup(stack.close)
        material=dict(packageArtifact=dict(name='original',id=10,digest='sha256:'+'d'*64),intent=dict(packageDigest=self.digest))
        stack.enter_context(patch.dict(os.environ,dict(GITHUB_REPOSITORY='owner/repo',GH_TOKEN='test-only')))
        for name,value in [('assert_fast_track_completion_execution',dict(toolRevision='b'*40)),('fast_track_completion_original',dict(targets=['portal'])),('fast_track_completion_material',material)]:stack.enter_context(patch.object(a,name,return_value=value))
        stack.enter_context(patch.object(a,'assert_fast_track_completion_live_execution'))
        stack.enter_context(patch.object(deploy.subprocess,'run',return_value=SimpleNamespace(returncode=0)))
        self.download=stack.enter_context(patch.object(a,'_download_run_artifact',side_effect=download))
        self.provider=stack.enter_context(patch.object(deploy,'target_azure',side_effect=AssertionError('provider reached')))
        return lambda:deploy.complete_fast_track(1,'a'*40,root,validate_only=validate)
    def make(self,root,revision='a'*40):
        import zipfile,hashlib
        directory=root/'portal';directory.mkdir(exist_ok=True)
        with zipfile.ZipFile(directory/'deploy.zip','w') as z:z.writestr('wwwroot/_deployment-provenance.json',json.dumps(dict(releaseSha=revision)))
        self.digest=hashlib.sha256((directory/'deploy.zip').read_bytes()).hexdigest()
        (directory/'SHA256SUMS').write_text(self.digest+'  deploy.zip\n')
    def test_actual_prelogin_bytes_are_hashed_without_provider(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);self.make(root)
            call=self.setup_call(root,lambda *args,**kw:None)
            self.assertEqual(call()['state'],'COMPLETION_PACKAGES_VERIFIED');self.download.assert_called_once();self.provider.assert_not_called()
    def test_corrupt_or_wrong_revision_package_blocks_prelogin(self):
        for wrong in ['digest','revision']:
            with self.subTest(wrong=wrong),tempfile.TemporaryDirectory() as temp:
                root=Path(temp);self.make(root,'b'*40 if wrong=='revision' else 'a'*40)
                if wrong=='digest':(root/'portal/deploy.zip').write_bytes(b'changed')
                call=self.setup_call(root,lambda *args,**kw:None)
                with self.assertRaises(ValueError):call()
                self.provider.assert_not_called()
    def test_retained_changed_missing_or_symlink_bytes_block_without_download(self):
        for wrong in ['changed','rehash','missing','symlink']:
            with self.subTest(wrong=wrong),tempfile.TemporaryDirectory() as temp:
                root=Path(temp);self.make(root);package=root/'portal/deploy.zip'
                if wrong=='changed':package.write_bytes(b'changed')
                elif wrong=='rehash':
                    import zipfile,hashlib
                    with zipfile.ZipFile(package,'a') as z:z.writestr('changed-asset.txt','changed')
                    (package.parent/'SHA256SUMS').write_text(hashlib.sha256(package.read_bytes()).hexdigest()+'  deploy.zip\n')
                elif wrong=='missing':package.unlink()
                else:
                    package.rename(root/'elsewhere.zip');package.symlink_to(root/'elsewhere.zip')
                call=self.setup_call(root,lambda *args,**kw:None,validate=False)
                with self.assertRaises(ValueError):call()
                self.download.assert_not_called();self.provider.assert_not_called()
    def test_restore_and_deploy_forward_exact_target_before_any_effect(self):
        for function in [deploy.fast_track_restore_package,deploy.fast_track_deploy]:
            with patch.object(deploy._RELEASE_AUTHORITY,'assert_fast_track_release_execution',side_effect=RuntimeError('identity')) as auth,patch.object(deploy,'target_azure') as provider,patch.object(deploy,'verify_package') as package:
                with self.assertRaisesRegex(RuntimeError,'identity'):function('portal',Path('/unused'))
                auth.assert_called_once_with(target='portal');provider.assert_not_called();package.assert_not_called()
    def test_workflow_uses_same_retained_directory_before_and_after_login(self):
        source=(Path(__file__).resolve().parent.parent/'.github/workflows/deployment-diagnostics.yml').read_text().split('  complete-fast-track:',1)[1]
        self.assertLess(source.index('--validate-completion-only'),source.index('uses: azure/login@v2'))
        self.assertEqual(source.count('--package-root "$RUNNER_TEMP/original-completion-packages"'),2)

class RetainedRecoveryConsumerTests(unittest.TestCase):
    def setUp(self):
        self.original=dict(run=dict(id=10,run_attempt=5,head_sha='a'*40),targets=['website'],attempts={1:[
            dict(name='Affected App Deploy — Website',steps=[dict(name='Publish or reconcile exact application bytes',started_at='2026-10-23T10:00:00Z')])]})
        self.observer=dict(runId=20,runAttempt=1,jobId=30,toolRevision='b'*40)
    def baseline(self,rows,revision='c'*40,expected=None):
        azure=Mock();azure.observed_revision.return_value=revision
        with patch.object(deploy,'read_deployments_bounded',return_value=rows),patch.object(deploy.subprocess,'run',return_value=Mock(returncode=0)):
            return deploy.recovery_preupload_baseline(azure,self.original,'website',expected=expected)
    def test_fresh_idle_ancestor_baseline(self):
        self.assertEqual(self.baseline([dict(id='old',status=4,received_time='2026-10-23T09:00:00Z')]),dict(revision='c'*40,deploymentIds=['old']))
    def test_active_empty_unknown_or_newer_baseline_holds(self):
        for rows in [[],[dict(id='active',status=1)],[dict(id='unknown',status=4)],
                     [dict(id='new',status=4,received_time='2026-10-23T11:00:00Z')]]:
            with self.subTest(rows=rows),self.assertRaises((deploy.DeploymentDrift,deploy.DeploymentReconciliationRequired)):
                self.baseline(rows)
    def test_baseline_change_before_intent_holds(self):
        with self.assertRaises(deploy.DeploymentDrift):
            self.baseline([dict(id='old',status=4,received_time='2026-10-23T09:00:00Z')],expected=dict(revision='d'*40,deploymentIds=['old']))
    def test_deadline_requires_exact_live_owner_and_preserves_original_ceiling(self):
        authority=deploy._RELEASE_AUTHORITY
        job=dict(run_id=20,run_attempt=1,head_sha='b'*40,status='in_progress',name=authority.FAST_TRACK_RECOVERY_JOB,started_at='2026-10-23T10:00:00Z')
        with patch.object(authority,'api_get',return_value=job):
            now=deploy.datetime.datetime.fromisoformat('2026-10-23T10:05:00+00:00')
            self.assertEqual(deploy.recovery_job_deadline(authority,'owner/repo',self.observer,'token',now=now,clock=lambda:10),1510)
            job['run_attempt']=2
            with self.assertRaises(deploy.DeploymentReconciliationRequired):
                deploy.recovery_job_deadline(authority,'owner/repo',self.observer,'token',now=now)
    def test_phase_reserve_threshold_and_late_hold(self):
        self.assertEqual(deploy.recovery_phase_remaining(670,660,clock=lambda:10),660)
        with self.assertRaises(deploy.DeploymentReconciliationRequired):
            deploy.recovery_phase_remaining(670,660,clock=lambda:10.001)
        with self.assertRaises(deploy.DeploymentReconciliationRequired):
            deploy.recovery_phase_remaining(10,clock=lambda:10)
    def test_late_provider_submission_never_enters_io(self):
        azure=Mock();bounded=deploy.RecoveryDeadlineAzure(azure,100)
        with patch.object(deploy,'recovery_phase_remaining',side_effect=deploy.DeploymentReconciliationRequired('late')):
            with self.assertRaises(deploy.DeploymentReconciliationRequired):bounded.submit({})
        azure.submit.assert_not_called()
    def test_expired_read_deadline_never_sleeps_or_enters_provider_io(self):
        azure=Mock();bounded=deploy.RecoveryDeadlineAzure(azure,100);sleeps=[]
        with patch.object(deploy,'recovery_phase_remaining',side_effect=deploy.DeploymentReconciliationRequired('late')):
            with self.assertRaises(deploy.DeploymentStatusUnavailable):
                deploy.read_deployments_bounded(bounded,sleep=sleeps.append)
        azure.deployments.assert_not_called()
        self.assertEqual(sleeps,[])
        self.assertLess(sum(sleeps),deploy.RECOVERY_FINAL_RESERVE_SECONDS)

    def test_missing_original_package_never_rebuilds_or_contacts_provider(self):
        authority=deploy._RELEASE_AUTHORITY
        material=dict(packageArtifact=dict(id=40,name='original',digest='sha256:'+'c'*64),originalIntent=None)
        with tempfile.TemporaryDirectory() as temporary, \
             patch.dict(os.environ,dict(GITHUB_REPOSITORY='owner/repo',GH_TOKEN='token')), \
             patch.object(authority,'assert_fast_track_recovery_execution',return_value=self.observer), \
             patch.object(authority,'assert_fast_track_recovery_live_execution',side_effect=lambda original,revision,observer:observer), \
             patch.object(deploy,'recovery_job_deadline',return_value=deploy.time.monotonic()+1800), \
             patch.object(authority,'fast_track_completion_original',return_value=self.original), \
             patch.object(authority,'fast_track_recovery_material',return_value=material), \
             patch.object(authority,'_download_run_artifact') as download, \
             patch.object(deploy,'target_azure') as azure,patch.object(deploy.subprocess,'run') as process:
            with self.assertRaisesRegex(ValueError,'MISSING_NO_REBUILD'):
                deploy.recover_fast_track(10,'a'*40,Path(temporary))
            download.assert_not_called();azure.assert_not_called();process.assert_not_called()

class RetainedRecoveryIntegratedTests(unittest.TestCase):
    def test_efficiency_report_failure_preserves_recovery_outcome(self):
        authority=deploy._RELEASE_AUTHORITY
        with patch.dict(os.environ,dict(GITHUB_REPOSITORY='owner/repo',GH_TOKEN='fixture')), \
             patch.object(authority,'assert_fast_track_recovery_execution',return_value={}), \
             patch.object(authority,'assert_fast_track_recovery_live_execution'), \
             patch.object(deploy,'recovery_job_deadline',side_effect=lambda *args:deploy.time.monotonic()+1800), \
             patch.object(authority,'report_recovery_efficiency',side_effect=RuntimeError('report failed')) as report:
            with patch.object(deploy,'_recover_fast_track',return_value={'state':'verified'}):
                self.assertEqual(deploy.recover_fast_track(10,'a'*40,Path('/unused')),{'state':'verified'})
            report.assert_called_with('success')
            with patch.object(deploy,'_recover_fast_track',side_effect=ValueError('original failure')):
                with self.assertRaisesRegex(ValueError,'original failure'):
                    deploy.recover_fast_track(10,'a'*40,Path('/unused'))
            report.assert_called_with('failure')
        self.assertIsNone(authority._RECOVERY_PROOF_VIEW.get())
        self.assertIsNone(authority._RECOVERY_RECEIPT_BYTES.get())

    def run_case(self, *, entered=False, race=False, expired=False, preflight_failure=False, publication_failure=False, read_failure=False, proof_failure=0, live_failure=False, material_failure=False):
        from types import SimpleNamespace
        authority=deploy._RELEASE_AUTHORITY
        observer=dict(runId=20,runAttempt=1,jobId=30,toolRevision='b'*40,promotionRunId=19)
        original=dict(run=dict(id=10,run_attempt=5,head_sha='a'*40),targets=['website'])
        identity=dict(target='website',applicationRevision='a'*40,packageDigest='d'*64)
        operation=hashlib.sha256(json.dumps(identity,sort_keys=True,separators=(',',':')).encode()).hexdigest()
        prior=dict(identity,operationId=operation,packageProducerRun=10,baseline='c'*40,baselineDeploymentIds=['old']) if entered else None
        material=dict(packageArtifact=dict(id=40,name='immutable-original',digest='sha256:'+'e'*64),originalIntent=prior)
        events=[]; intent_reads=0; proof_boundaries=[]; proof_active=0; live_reads=0
        import contextlib
        @contextlib.contextmanager
        def proof_view():
            nonlocal proof_active
            proof_boundaries.append('enter')
            number=len(proof_boundaries)
            proof_active+=1
            try:
                yield
                if (proof_failure and number==proof_failure+2) or (material_failure and number==2):
                    raise authority.ReleaseOperationHistoryUnproven('RECOVERY_PROOF_CHANGED_AT_BOUNDARY')
            finally:proof_active-=1
        def authenticate(*args):
            self.assertGreater(proof_active,0)
            self.assertIsNotNone(authority._RECOVERY_RECEIPT_BYTES.get())
            return observer
        def original_proof(*args):
            self.assertGreater(proof_active,0)
            return original
        def material_proof(*args):
            self.assertGreater(proof_active,0)
            return material
        def live_owner(*args):
            nonlocal live_reads
            live_reads+=1
            self.assertEqual(proof_active,0)
            if live_failure and live_reads==3:raise ValueError('FAST_TRACK_RECOVERY_LIVE_OWNER_CHANGED')
            return observer
        def history(*args,phase='intent'):
            nonlocal intent_reads
            if phase=='success':return dict(packageDigest='d'*64,deploymentIds=['new'])
            intent_reads+=1
            return prior or (dict(baseline='c'*40) if race and intent_reads>=2 else None)
        class Journal:
            def __init__(self,**kwargs):
                self.intent=prior;self.baseline=kwargs['baseline'];self.history_error=None
                self.callback=kwargs['before_new_intent']
            def validate_contract(self, *args, **kwargs):
                pass
            def before_submit(self):
                if self.intent is not None:return False
                self.callback(['old']);events.append('intent');self.intent={};return True
        def reconcile(azure,**kwargs):
            self.assertEqual(proof_active,0)
            events.append(('reconcile_only',kwargs['reconcile_only']))
            self.assertLessEqual(kwargs['timeout'],420)
            if read_failure:
                failure = deploy.DeploymentStatusUnavailable('bounded read unavailable')
                failure.__cause__ = subprocess.TimeoutExpired('az', 39)
                kwargs['before_intent_read_failure'](failure)
                raise failure
            if kwargs['journal'].before_submit():events.append('upload')
            return 'preserved' if entered else 'deployed'
        def download(repo,run,name,directory,**kwargs):
            self.assertEqual(proof_active,0)
            self.assertEqual((run,name,kwargs['artifact_id'],kwargs['expected_digest']),
                             (10,'immutable-original',40,'sha256:'+'e'*64))
            directory.mkdir(parents=True);(directory/'deploy.zip').write_bytes(b'exact fixture');(directory/'SHA256SUMS').write_text('fixture')
        owner=SimpleNamespace(RecoveryJournal=Journal,publish_record=Mock(),validate_record=Mock())
        with ExitStack() as stack:
            temporary=stack.enter_context(tempfile.TemporaryDirectory())
            stack.enter_context(patch.dict(os.environ,dict(GITHUB_REPOSITORY='owner/repo',GH_TOKEN='token')))
            stack.enter_context(patch.object(authority,'assert_fast_track_recovery_execution',side_effect=authenticate))
            stack.enter_context(patch.object(authority,'assert_fast_track_recovery_live_execution',side_effect=live_owner))
            stack.enter_context(patch.object(deploy,'recovery_job_deadline',side_effect=lambda *args:deploy.time.monotonic()+1800))
            stack.enter_context(patch.object(authority,'fast_track_completion_original',side_effect=original_proof))
            stack.enter_context(patch.object(authority,'fast_track_recovery_material',side_effect=material_proof))
            package_download=stack.enter_context(patch.object(authority,'_download_run_artifact',side_effect=download))
            stack.enter_context(patch.object(authority,'release_operation_history',side_effect=history))
            stack.enter_context(patch.object(authority,'_completion_original_unchanged'))
            stack.enter_context(patch.object(authority,'recovery_admission_proof',side_effect=proof_view))
            stack.enter_context(patch.object(authority,'fast_track_recovery_record_proof',return_value={}))
            stack.enter_context(patch.object(deploy,'verify_package',return_value='d'*64))
            stack.enter_context(patch.object(deploy,'target_azure',return_value=Mock()))
            baseline=stack.enter_context(patch.object(deploy,'recovery_preupload_baseline',return_value=dict(revision='c'*40,deploymentIds=['old'])))
            stack.enter_context(patch.object(deploy,'recovery_host_flags'))
            stack.enter_context(patch.object(deploy,'reconcile',side_effect=reconcile))
            stack.enter_context(patch.object(deploy,'verify_fast_track_public_metadata'))
            stack.enter_context(patch.object(deploy.importlib.util,'spec_from_file_location',return_value=SimpleNamespace(loader=Mock())))
            stack.enter_context(patch.object(deploy.importlib.util,'module_from_spec',return_value=owner))
            root=Path(temporary)
            if material_failure:
                with self.assertRaisesRegex(authority.ReleaseOperationHistoryUnproven,'CHANGED_AT_BOUNDARY'):
                    deploy.recover_fast_track(10,'a'*40,root,prepare_only=True)
                package_download.assert_not_called();baseline.assert_not_called();owner.publish_record.assert_not_called()
                self.assertEqual(events,[]);self.assertEqual(proof_active,0)
                return
            deploy.recover_fast_track(10,'a'*40,root,prepare_only=True)
            proof_boundaries.clear()
            if proof_failure:
                with self.assertRaises(authority.ReleaseOperationHistoryUnproven):deploy.recover_fast_track(10,'a'*40,root)
                self.assertNotIn('upload',events);self.assertNotIn('intent',events)
                owner.publish_record.assert_called_once()
                record=owner.publish_record.call_args.args[1]
                self.assertEqual(record['phase'],'preflight-nonentry' if proof_failure==1 else 'target-preintent-hold')
                self.assertFalse(record['authorizesMutation'])
                self.assertEqual(len(proof_boundaries),proof_failure+2)
            elif live_failure:
                with self.assertRaisesRegex(ValueError,'LIVE_OWNER_CHANGED'):deploy.recover_fast_track(10,'a'*40,root)
                self.assertNotIn('intent',events);self.assertNotIn('upload',events)
                owner.publish_record.assert_called_once()
                self.assertEqual(owner.publish_record.call_args.args[1]['phase'],'target-preintent-hold')
            elif preflight_failure:
                baseline.side_effect=deploy.RecoveryPreflightFailure('provider-read-timeout')
                if publication_failure:owner.publish_record.side_effect=RuntimeError('durable receipt unavailable')
                with self.assertRaises(RuntimeError):deploy.recover_fast_track(10,'a'*40,root)
                self.assertEqual(events,[])
                owner.publish_record.assert_called_once()
                name,record=owner.publish_record.call_args.args
                self.assertEqual(authority.validate_recovery_preflight_nonentry(record),name)
                self.assertFalse(record['authorizesMutation']);self.assertFalse(record['writePhaseEntered'])
                self.assertEqual(record['failedTarget'],'website')
                self.assertLessEqual(owner.publish_record.call_args.kwargs['timeout'],180)
            elif read_failure:
                with self.assertRaises(deploy.DeploymentStatusUnavailable):deploy.recover_fast_track(10,'a'*40,root)
                self.assertEqual(events, [('reconcile_only', False)])
                owner.publish_record.assert_called_once()
                name, record = owner.publish_record.call_args.args
                self.assertEqual(authority.validate_recovery_target_preintent(record), name)
                self.assertEqual(record['reason'], 'provider-read-timeout')
                self.assertEqual(record['observation'], dict(historyReadComplete=False, historyCount=None, activeCount=None, runtimeRevision=None))
                self.assertFalse(record['targetWritePhaseEntered']);self.assertFalse(record['authorizesMutation'])
                self.assertNotIn('writePhaseEntered', record)
                self.assertLessEqual(owner.publish_record.call_args.kwargs['timeout'], 180)
            elif race:
                with self.assertRaises(deploy.DeploymentReconciliationRequired):deploy.recover_fast_track(10,'a'*40,root)
                self.assertNotIn('upload',events);self.assertNotIn('intent',events)
                owner.publish_record.assert_called_once()
                self.assertEqual(owner.publish_record.call_args.args[1]['phase'],'target-preintent-hold')
            else:
                result=deploy.recover_fast_track(10,'a'*40,root)
                self.assertEqual(result['state'],'RETAINED_RECOVERY_VERIFIED')
                self.assertEqual(events.count('upload'),0 if entered else 1)
                self.assertEqual(events.count('intent'),0 if entered else 1)
                self.assertIn(('reconcile_only',entered),events)
                self.assertEqual(baseline.call_count,0 if entered else 2)
    def test_fresh_owner_drift_after_history_readback_prevents_intent(self):self.run_case(live_failure=True)
    def test_original_material_readback_drift_stops_before_package_restore(self):self.run_case(material_failure=True)
    def test_original_immutable_package_first_write_after_fresh_barriers(self):self.run_case()
    def test_initial_proof_readback_drift_holds_before_workers(self):self.run_case(proof_failure=1)
    def test_preintent_proof_readback_drift_holds_before_intent(self):self.run_case(proof_failure=2)
    def test_worker_read_failure_emits_target_only_receipt(self):self.run_case(read_failure=True)
    def test_existing_intent_never_reuploads_or_rebases(self):self.run_case(entered=True)
    def test_newly_appeared_intent_holds_before_write(self):self.run_case(race=True)

    def test_preflight_failure_publishes_terminal_receipt_without_worker_or_intent(self):self.run_case(preflight_failure=True)
    def test_terminal_receipt_failure_cannot_fall_through_to_worker(self):self.run_case(preflight_failure=True,publication_failure=True)

class DeploymentReadErrorMetadataTests(unittest.TestCase):
    def metadata(self, text, code=1):
        return deploy.deployment_read_error_metadata(subprocess.CompletedProcess([], code, '', text))

    def test_reported_provider_codes_do_not_infer_http_status(self):
        for code, category in [('AuthenticationFailed', 'authentication'),
                               ('AuthorizationFailed', 'authorization'),
                               ('ResourceNotFound', 'not_found'), ('TooManyRequests', 'throttling'),
                               ('ServiceUnavailable', 'service_error'), ('BadRequest', 'invalid_request')]:
            with self.subTest(code=code):
                result = self.metadata(f'ERROR: ({code}) synthetic detail\r\n')
                self.assertEqual((result['providerCode'], result['category']), (code, category))
                self.assertIsNone(result['httpStatus'])

    def test_reported_http_status_does_not_infer_provider_code_or_timeout(self):
        for phrase, status in [('Unauthorized', 401), ('Forbidden', 403), ('Not Found', 404),
                               ('Too Many Requests', 429), ('Service Unavailable', 503),
                               ('Gateway Timeout', 504)]:
            with self.subTest(status=status):
                result = self.metadata(f"ERROR: Operation returned an invalid status '{phrase}'")
                self.assertEqual(result['httpStatus'], status)
                self.assertIsNone(result['providerCode'])
                self.assertNotEqual(result['category'], 'timeout')

    def test_unrecognized_multiple_conflicting_and_embedded_errors_stay_unknown(self):
        for text in ['', 'ERROR: unknown', 'customer AuthenticationFailed 403',
                     'prefix ERROR: (Forbidden) detail', 'ERROR: (NewProviderCode) detail',
                     'ERROR: (Forbidden) detail\nERROR: (AuthenticationFailed) detail',
                     'ERROR: (Forbidden) detail\nERROR: unknown',
                     'ERROR: (Forbidden) detail ERROR: (AuthenticationFailed) detail',
                     "ERROR: (Forbidden) Operation returned an invalid status 'Unauthorized'",
                     'ERROR: (Forbidden) Code: AuthenticationFailed',
                     'ERROR: (Forbidden) HTTP/1.1 401']:
            with self.subTest(text=text):
                result = self.metadata(text)
                self.assertEqual(result['category'], 'unknown')
                self.assertIsNone(result['providerCode']); self.assertIsNone(result['httpStatus'])

    def test_oversized_and_malformed_inputs_never_classify_or_stringify(self):
        class Unprintable:
            def __str__(self):
                raise AssertionError('must not stringify')
        for text in [None, b'ERROR: (Forbidden)', Unprintable(), 'ERROR: (Forbidden) ' + 'x' * 8192]:
            result = self.metadata(text)
            self.assertEqual(result['category'], 'unknown')
            self.assertEqual(result['inputTooLarge'], type(text) is str)
        for code in [True, '1', Unprintable(), 256, -256]:
            result = self.metadata('ERROR: (Forbidden) detail', code)
            self.assertIsNone(result['returnCode']); self.assertEqual(result['category'], 'unknown')

    def test_only_allowlisted_metadata_reaches_output_and_failure_is_unchanged(self):
        secret = 'SYNTHETIC_SECRET_DO_NOT_LOG'
        stderr = 'ERROR: (Forbidden) Authorization: Bearer ' + secret + ' https://example.invalid/?token=' + secret
        azure = deploy.Azure('app', Path('/immutable'), 'https://example.invalid', 'a' * 40)
        output = io.StringIO()
        with patch.object(deploy, 'deployment_read_process', return_value=
                          subprocess.CompletedProcess([], 1, secret, stderr)) as process, \
                patch('sys.stdout', output):
            with self.assertRaisesRegex(RuntimeError, '^Azure deployment status unavailable$'):
                azure.deployments()
        self.assertNotIn(secret, output.getvalue()); self.assertNotIn('https:', output.getvalue())
        self.assertNotIn('Bearer', output.getvalue()); self.assertNotIn('Authorization:', output.getvalue())
        payload = json.loads(output.getvalue().split(':', 1)[1])
        self.assertEqual(payload, dict(schemaVersion=1, source='azure-cli-reported', category='authorization',
                         returnCode=1, providerCode='Forbidden', httpStatus=None, inputTooLarge=False,
                         unknownReason=None, stderrLineCount=1))
        self.assertEqual(process.call_args.args[0], ['az', 'webapp', 'log', 'deployment', 'list',
                         '-g', deploy._RELEASE_AUTHORITY.RELEASE_RESOURCE_GROUP, '-n', 'app', '--only-show-errors', '-o', 'json'])
        self.assertEqual(process.call_args.kwargs, {'timeout': 40})

    def test_multiline_finite_shapes_never_emit_secret_details(self):
        secret = 'SYNTHETIC_SECRET_DO_NOT_LOG'
        cases = [('ERROR: (Forbidden) '+secret, 'authorization', None),
                 ("ERROR: Operation returned an invalid status 'Unauthorized'", 'authentication', None),
                 ('ERROR: unrecognized arguments: --'+secret, 'cli_argument', 'UNRECOGNIZED_ARGUMENTS'),
                 ('ERROR: argument --secret: expected one argument', 'cli_argument', 'EXPECTED_ONE_ARGUMENT'),
                 ('ERROR: the following arguments are required: --'+secret, 'cli_argument', 'REQUIRED_ARGUMENTS')]
        for code in ('AADSTS700016', 'AADSTS7000215', 'AADSTS7000222'):
            cases.append(('ERROR: '+code+': '+secret, 'authentication_reported', None))
        for ending in ('\n', '\r\n'):
            for line, category, kind in cases:
                with self.subTest(line=line, ending=ending):
                    result = self.metadata(ending.join([line, 'Trace: '+secret,
                        'https://example.invalid/?token='+secret])+ending)
                    self.assertEqual(result['category'], category)
                    self.assertEqual(result.get('cliArgumentKind'), kind)
                    self.assertEqual(result['stderrLineCount'], 3)
                    self.assertIsNone(result['unknownReason'])
                    for value in (secret, 'https:', '--secret', 'token='):
                        self.assertNotIn(value, json.dumps(result))
                    if category in ('cli_argument', 'authentication_reported'):
                        self.assertIsNone(result['providerCode']); self.assertIsNone(result['httpStatus'])

    def test_multiline_conflicts_and_malformed_shapes_stay_unknown(self):
        first = 'ERROR: (Forbidden) safe'
        markers = ('ERROR: (Forbidden) same', ' error: unknown', 'Code: AuthenticationFailed',
                   'HTTP/1.1 401', 'Operation returned an invalid status', 'AADSTS999999: detail',
                   'HTTP status: 401', 'Status: 401', 'StatusCode: 401',
                   'AuthenticationError: detail', 'authentication error: detail', 'AuthError: detail')
        cases = [first+'\n'+marker for marker in markers]
        cases += [first+' '+marker for marker in markers]
        cases += [first+ending for ending in ('\r', '\x1b[0m', '\u2028detail', '\vdetail', '\x85detail')]
        cases += ['ERROR: argument --x: expected one arguments',
                  'ERROR: unrecognized arguments:', 'ERROR: the following arguments are required:',
                  'ERROR: AADSTS999999: detail', 'prefix ERROR: (Forbidden) detail',
                  'ERROR: AADSTS700016:evil', first+'\n'+'detail\n'*31+'ERROR: other']
        for text in cases:
            with self.subTest(text=text):
                result = self.metadata(text)
                self.assertEqual(result['category'], 'unknown')
                self.assertIsNone(result['providerCode']); self.assertIsNone(result['httpStatus'])
                self.assertNotIn('authenticationCode', result)
                self.assertNotIn('cliArgumentKind', result)

    def test_multiline_bounds_never_classify_truncated_prefix(self):
        first = 'ERROR: (Forbidden) safe'
        result = self.metadata(first+'\n'+'detail\n'*31)
        self.assertEqual(result['category'], 'authorization')
        self.assertEqual(result['stderrLineCount'], 32)
        for tail in ('detail', 'ERROR: other'):
            result = self.metadata(first+'\n'+'detail\n'*31+tail)
            self.assertEqual(result['unknownReason'], 'LINE_LIMIT')
            self.assertEqual(result['stderrLineCount'], 33)
        exact = 'ERROR: (Forbidden) '+'x'*(8192-len('ERROR: (Forbidden) '))
        self.assertEqual(self.metadata(exact)['category'], 'authorization')
        self.assertEqual(self.metadata(exact+'x')['unknownReason'], 'OVERSIZED')

    def test_diagnostic_failure_cannot_replace_original_failure(self):
        azure = deploy.Azure('app', Path('/immutable'), 'https://example.invalid', 'a' * 40)
        with patch.object(deploy, 'deployment_read_process', return_value=subprocess.CompletedProcess([], 1, '', '')), \
                patch.object(deploy, 'deployment_read_error_metadata', side_effect=ValueError('synthetic secret')):
            with self.assertRaisesRegex(RuntimeError, '^Azure deployment status unavailable$'):
                azure.deployments()

    def test_success_does_not_classify_or_log_stderr(self):
        azure = deploy.Azure('app', Path('/immutable'), 'https://example.invalid', 'a' * 40)
        with patch.object(deploy, 'deployment_read_process', return_value=
                          subprocess.CompletedProcess([], 0, '[{"id":"old","status":4}]', 'ERROR: (Forbidden) detail')), \
                patch.object(deploy, 'deployment_read_error_metadata') as classify:
            self.assertEqual(azure.deployments(), [row('old', 4)])
        classify.assert_not_called()


class DeploymentReadWallBudgetTests(unittest.TestCase):
    def run_read(self,durations,*,deadline=None,fail=True):
        from types import SimpleNamespace
        now=[0];caps=[];sleeps=[]
        def read():
            cap=deploy._DEPLOYMENT_READ_CAP.get();caps.append(cap)
            duration=durations[min(len(caps)-1,len(durations)-1)];now[0]+=min(duration,cap)
            if fail or duration>cap:raise subprocess.TimeoutExpired('sanitized',cap)
            return [row('old',4)]
        def sleep(seconds):sleeps.append(seconds);now[0]+=seconds
        azure=SimpleNamespace(deployments=read)
        try:result=deploy.read_deployments_bounded(azure,deadline=deadline,clock=lambda:now[0],sleep=sleep)
        except deploy.DeploymentStatusUnavailable:result='held'
        return result,now[0],caps,sleeps
    def test_success_after_old_twenty_second_cap(self):
        result,elapsed,caps,sleeps=self.run_read([30],fail=False)
        self.assertEqual(result,[row('old',4)]);self.assertEqual((elapsed,caps,sleeps),(30,[40],[]))
    def test_total_ninety_seconds_and_two_attempts_only(self):
        self.assertEqual(self.run_read([40]),('held',90,[40,40],[10]))
    def test_phase_clip_and_no_read_without_cleanup_reserve(self):
        self.assertEqual(self.run_read([40],deadline=55),('held',55,[40,5],[10]))
        self.assertEqual(self.run_read([40],deadline=1),('held',0,[],[]))
    def test_owned_process_timeout_kills_group_and_reaps_inside_cap(self):
        process=Mock(pid=123,returncode=-9);process.communicate.side_effect=[subprocess.TimeoutExpired('az',39),('','')]
        with patch.object(deploy.subprocess,'Popen',return_value=process) as launch,patch.object(deploy.os,'killpg') as kill:
            with self.assertRaises(subprocess.TimeoutExpired):deploy.deployment_read_process(['az','read'],40)
        launch.assert_called_once_with(['az','read'],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,start_new_session=True)
        kill.assert_called_once_with(123,deploy.signal.SIGKILL)
        self.assertEqual([x.kwargs['timeout'] for x in process.communicate.call_args_list],[39,1])
    def test_cancel_reaps_and_never_converts_to_retry(self):
        process=Mock(pid=123);process.communicate.side_effect=[KeyboardInterrupt(),('','')]
        with patch.object(deploy.subprocess,'Popen',return_value=process),patch.object(deploy.os,'killpg') as kill:
            with self.assertRaises(KeyboardInterrupt):deploy.deployment_read_process(['az'],40)
        kill.assert_called_once();self.assertEqual(process.communicate.call_count,2)
    def test_unproven_cleanup_never_starts_second_read(self):
        from types import SimpleNamespace
        read=Mock(side_effect=deploy.DeploymentReadCleanupFailure('unproven'))
        with self.assertRaises(deploy.DeploymentReadCleanupFailure):deploy.read_deployments_bounded(SimpleNamespace(deployments=read))
        read.assert_called_once()
    def test_successful_process_and_invalid_rows_keep_original_validation(self):
        azure=deploy.Azure('app',Path('/immutable'),'https://example.invalid','a'*40)
        with patch.object(deploy,'deployment_read_process',return_value=subprocess.CompletedProcess([],0,'[{"id":"old","status":4}]','')) as process:
            self.assertEqual(azure.deployments(),[row('old',4)]);self.assertEqual(process.call_args.kwargs['timeout'],40)
        for content in ['{}','[{"id":"old","status":true}]','[{"id":"old","status":9}]']:
            with patch.object(deploy,'deployment_read_process',return_value=subprocess.CompletedProcess([],0,content,'')),self.assertRaises(RuntimeError):azure.deployments()

class DeploymentReadCleanupAndReserveTests(unittest.TestCase):
    def test_group_kill_failure_is_terminal_even_if_parent_reaped(self):
        process=Mock(pid=123);process.communicate.side_effect=[subprocess.TimeoutExpired('az',39),('','')]
        azure=deploy.Azure('app',Path('/immutable'),'https://example.invalid','a'*40)
        with patch.object(deploy.subprocess,'Popen',return_value=process) as launch,patch.object(deploy.os,'killpg',side_effect=PermissionError('denied')):
            with self.assertRaises(deploy.DeploymentReadCleanupFailure):deploy.read_deployments_bounded(azure)
        launch.assert_called_once();self.assertEqual(process.communicate.call_count,2)
    def test_cleanup_reap_timeout_is_terminal(self):
        process=Mock(pid=123);process.communicate.side_effect=subprocess.TimeoutExpired('az',1)
        azure=deploy.Azure('app',Path('/immutable'),'https://example.invalid','a'*40)
        with patch.object(deploy.subprocess,'Popen',return_value=process) as launch,patch.object(deploy.os,'killpg'):
            with self.assertRaises(deploy.DeploymentReadCleanupFailure):deploy.read_deployments_bounded(azure)
        launch.assert_called_once();self.assertEqual(process.communicate.call_count,2)
    def test_new_baseline_preserves_write_reserve_before_read(self):
        azure=Mock();bounded=deploy.RecoveryDeadlineAzure(azure,deploy.time.monotonic()+659)
        with self.assertRaises(deploy.RecoveryPhaseDeadline):deploy.recovery_preupload_baseline(bounded,{},'website')
        azure.deployments.assert_not_called();azure.observed_revision.assert_not_called()
    def test_new_baseline_read_facade_subtracts_exact_reserve(self):
        azure=Mock();deadline=deploy.time.monotonic()+1000;bounded=deploy.RecoveryDeadlineAzure(azure,deadline)
        with patch.object(deploy,'read_deployments_bounded',side_effect=deploy.DeploymentStatusUnavailable('held')) as read:
            with self.assertRaises(deploy.RecoveryPreflightFailure):deploy.recovery_preupload_baseline(bounded,{},'website')
        self.assertEqual(read.call_args.args[0].deadline,deadline-660)
        self.assertEqual(bounded.deadline,deadline)

def preflight_five_target_payload():
    """Execute the real sequential failure with four byte-preserved intents."""
    from types import SimpleNamespace
    authority=deploy._RELEASE_AUTHORITY;observer=dict(runId=20,runAttempt=1,jobId=30,toolRevision='b'*40,promotionRunId=19)
    keys=['portal','client','protect','parfait','website'];original=dict(run=dict(id=10,run_attempt=5,head_sha='a'*40),targets=keys)
    materials={};targets={};priors={}
    for n,key in enumerate(keys):
        identity=dict(target=key,applicationRevision='a'*40,packageDigest='d'*64)
        op=hashlib.sha256(json.dumps(identity,sort_keys=True,separators=(',',':')).encode()).hexdigest()
        priors[key]=None if key=='website' else dict(identity,operationId=op,packageProducerRun=10,baseline='c'*40,baselineDeploymentIds=['old'])
        materials[key]=dict(originalIntent=priors[key],packageArtifact=dict(id=40+n,name='immutable-'+key,digest='sha256:'+'e'*64))
        targets[key]=dict(operationId=op,packageDigest='d'*64,packageArtifactId=40+n,packageArtifactDigest='sha256:'+'e'*64)
    before=json.dumps(priors,sort_keys=True);proof=dict(originalRun=10,originalAttempt=5,applicationRevision='a'*40,observer=observer,targets=targets)
    owner=SimpleNamespace(publish_record=Mock(),RecoveryJournal=Mock(side_effect=lambda **kw:SimpleNamespace(intent=priors[kw['target']],history_error=None,validate_contract=Mock())))
    with tempfile.TemporaryDirectory() as temporary,patch.dict(os.environ,dict(GITHUB_REPOSITORY='owner/repo',GH_TOKEN='token')), \
         patch.object(authority,'fast_track_completion_original',return_value=original), \
         patch.object(authority,'fast_track_recovery_material',side_effect=lambda repo,o,key,t:materials[key]), \
         patch.object(authority,'release_operation_history',side_effect=lambda repo,op,rev,key,*args:priors[key]), \
         patch.object(authority,'fast_track_recovery_record_proof',return_value={}), \
         patch.object(deploy,'verify_package',return_value='d'*64),patch.object(deploy,'target_azure',return_value=Mock()), \
         patch.object(deploy,'recovery_preupload_baseline',side_effect=deploy.RecoveryPreflightFailure('provider-read-timeout')), \
         patch.object(deploy,'reconcile') as reconcile,patch.object(deploy.concurrent.futures,'ThreadPoolExecutor') as workers, \
         patch.object(deploy.importlib.util,'spec_from_file_location',return_value=SimpleNamespace(loader=Mock())), \
         patch.object(deploy.importlib.util,'module_from_spec',return_value=owner):
        root=Path(temporary)
        for key in keys:
            (root/key).mkdir();(root/key/'deploy.zip').write_bytes(b'exact');(root/key/'SHA256SUMS').write_text('fixture')
        (root/'retained-recovery-plan.json').write_text(json.dumps(proof))
        try:deploy._recover_fast_track(10,'a'*40,root,observer,deploy.time.monotonic()+1800)
        except deploy.RecoveryPreflightFailure:pass
        else:raise AssertionError('Terminal preflight fell through')
        workers.assert_not_called();reconcile.assert_not_called();owner.publish_record.assert_called_once()
        assert owner.RecoveryJournal.call_count==4
        assert json.dumps(priors,sort_keys=True)==before
        name,record=owner.publish_record.call_args.args
        return dict(name=name,record=record)

class PreflightFiveTargetBoundaryTests(unittest.TestCase):
    def test_four_intents_preserved_and_no_workers_before_website_failure(self):
        item=preflight_five_target_payload()
        self.assertEqual(len(item['record']['targets']),5)
        self.assertEqual(item['record']['failedTarget'],'website')
        self.assertEqual(deploy._RELEASE_AUTHORITY.validate_recovery_preflight_nonentry(item['record']),item['name'])

def target_hold_five_target_payload(*, callback_failure=True, publication_failure=False):
    """Real five-target worker/callback and unchanged RecoveryJournal, no provider."""
    from types import SimpleNamespace
    authority=deploy._RELEASE_AUTHORITY
    spec=importlib.util.spec_from_file_location('actual_hold_journal',ROOT/'release-operation-evidence.py');owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
    observer=dict(runId=20,runAttempt=1,jobId=30,toolRevision='b'*40,promotionRunId=19)
    keys=['portal','client','protect','parfait','website'];original=dict(run=dict(id=10,run_attempt=5,head_sha='a'*40),targets=keys)
    materials={};targets={};priors={};calls=[]
    for n,key in enumerate(keys):
        identity=dict(target=key,applicationRevision='a'*40,packageDigest='d'*64)
        op=hashlib.sha256(json.dumps(identity,sort_keys=True,separators=(',',':')).encode()).hexdigest()
        priors[key]=None if key=='website' else dict(identity,operationId=op,packageProducerRun=10,baseline='c'*40,schemaVersion=1,phase='intent',producingRun=10,producingAttempt=1,baselineDeploymentIds=['old'],submissionContract=dict(kind='azure_onedeploy_windows_intent_v1',value=op))
        materials[key]=dict(originalIntent=priors[key],packageArtifact=dict(id=40+n,name='immutable-'+key,digest='sha256:'+'e'*64))
        targets[key]=dict(operationId=op,packageDigest='d'*64,packageArtifactId=40+n,packageArtifactDigest='sha256:'+'e'*64)
    before=json.dumps(priors,sort_keys=True);proof=dict(originalRun=10,originalAttempt=5,applicationRevision='a'*40,observer=observer,targets=targets)
    reads=0
    def baseline(*args,**kwargs):
        nonlocal reads
        reads+=1
        if callback_failure and reads==2:raise deploy.RecoveryPhaseDeadline('cutoff before fresh provider read')
        return dict(revision='c'*40,deploymentIds=['old'])
    def publish(name,record,**kwargs):
        calls.append(dict(name=name,record=record))
        if publication_failure or not callback_failure:raise RuntimeError('ambiguous publication readback')
        return record
    def reconcile(azure,**kwargs):
        journal=kwargs['journal']
        if journal.intent is not None:
            assert kwargs['reconcile_only'] is True;return 'inherited-reconcile-only'
        assert kwargs['reconcile_only'] is False
        journal.before_submit(['old'])
        raise AssertionError('Provider submit boundary unexpectedly reached')
    with tempfile.TemporaryDirectory()as temporary,patch.dict(os.environ,dict(GITHUB_REPOSITORY='owner/repo',GH_TOKEN='token',GITHUB_RUN_ID='20',GITHUB_RUN_ATTEMPT='1',PACKAGE_PRODUCER_RUN='10')), \
         patch.object(authority,'fast_track_completion_original',return_value=original), \
         patch.object(authority,'fast_track_recovery_material',side_effect=lambda repo,o,key,t:materials[key]), \
         patch.object(authority,'release_operation_history',side_effect=lambda repo,op,rev,key,*args,phase='intent':priors[key]if phase=='intent'else None), \
         patch.object(authority,'_completion_original_unchanged'),patch.object(authority,'assert_fast_track_recovery_execution',return_value=observer), \
         patch.object(authority,'assert_fast_track_recovery_live_execution',side_effect=lambda original,revision,observer:observer), \
         patch.object(deploy,'verify_package',return_value='d'*64),patch.object(deploy,'target_azure',return_value=Mock()), \
         patch.object(deploy,'recovery_preupload_baseline',side_effect=baseline),patch.object(deploy,'recovery_host_flags'), \
         patch.object(deploy,'reconcile',side_effect=reconcile),patch.object(deploy,'verify_fast_track_public_metadata'), \
         patch.object(owner,'publish_record',side_effect=publish), patch.object(owner,'validate_record'), \
         patch.object(deploy.importlib.util,'spec_from_file_location',return_value=SimpleNamespace(loader=Mock())), \
         patch.object(deploy.importlib.util,'module_from_spec',return_value=owner):
        root=Path(temporary)
        for key in keys:
            (root/key).mkdir();(root/key/'deploy.zip').write_bytes(b'exact');(root/key/'SHA256SUMS').write_text('fixture')
        (root/'retained-recovery-plan.json').write_text(json.dumps(proof))
        try:deploy._recover_fast_track(10,'a'*40,root,observer,deploy.time.monotonic()+1800)
        except (deploy.RecoveryPhaseDeadline,RuntimeError):pass
        else:raise AssertionError('Failed callback/publication fell through')
    assert json.dumps(priors,sort_keys=True)==before
    assert len(calls)==1
    if callback_failure:
        assert calls[0]['record']['phase']=='target-preintent-hold'
        assert authority.validate_recovery_target_preintent(calls[0]['record'])==calls[0]['name']
    else:assert calls[0]['record']['phase']=='intent'
    return calls[0]

class TargetPreintentProducerTests(unittest.TestCase):
    def test_real_callback_hold_preserves_four_entered_siblings(self):
        row=target_hold_five_target_payload();self.assertEqual(row['record']['failedTarget'],'website');self.assertEqual(len(row['record']['targets']),5)
    def test_hold_publication_failure_cannot_fall_through(self):target_hold_five_target_payload(publication_failure=True)
    def test_callback_success_then_ambiguous_intent_publisher_never_emits_hold(self):target_hold_five_target_payload(callback_failure=False)

class FreshProtectRetainedReplacementTests(unittest.TestCase):
    revision='9af32d2e6bf608cb790eed59a43e90cdd9d877e6'
    baseline='8f94550fdcb0dfbe1b1b1c71aa43171a60661096'
    def exercise(self, *, active=False, bad_baseline=False, bad_rollback=False, expired=False, target='protect', intent=None):
        from types import SimpleNamespace
        observed_caps=[]
        def history():
            observed_caps.append(deploy._DEPLOYMENT_READ_CAP.get())
            return [row('original-provider',1 if active else 4)]
        provider=SimpleNamespace(app=deploy.TARGETS[target]['releaseName'],deployments=history)
        azure=deploy.RecoveryDeadlineAzure(provider,deploy.time.monotonic()+(-1 if expired else 25))
        original=dict(run=dict(id=40703908687,head_sha=self.revision))
        material=dict(originalIntent=intent)
        with tempfile.TemporaryDirectory() as root, patch.dict(os.environ,GITHUB_REPOSITORY='MYLEGND/masterapp'), \
             patch.object(deploy._RELEASE_AUTHORITY,'_download_run_artifact') as download, \
             patch.object(deploy,'verify_package',side_effect=ValueError('invalid rollback') if bad_rollback else None,return_value='d'*64), \
             patch.object(deploy,'read_deployments_bounded',side_effect=lambda bounded,**kwargs:bounded.deployments()) as read:
            def invoke():
                deploy.prepare_protect_retained_replacement(azure,original,material,
                    dict(revision='f'*40 if bad_baseline else self.baseline),Path(root))
            if target!='protect' or intent is not None:
                invoke();read.assert_not_called();download.assert_not_called()
                self.assertFalse(getattr(provider,'authorized_replacement',False));return
            if active or bad_baseline or bad_rollback or expired:
                with self.assertRaises((deploy.DeploymentDrift,deploy.DeploymentReconciliationRequired,ValueError)):invoke()
                self.assertFalse(getattr(provider,'authorized_replacement',False))
                if active or bad_baseline or expired:download.assert_not_called()
            else:
                # Both existing checkpoints must execute the same complete preparation.
                invoke();invoke();self.assertEqual(download.call_count,2)
                self.assertTrue(provider.authorized_replacement)
                self.assertNotIn('authorized_replacement',azure.__dict__)
                self.assertTrue(all(0<cap<=25 for cap in observed_caps))
                self.assertEqual(read.call_args.args[0],azure)
                self.assertEqual(download.call_args.kwargs,dict(artifact_id=12749425188,expected_digest='sha256:59af04b03aa6d815144cd253ed6cb8594b22d930e8420289de7a0b6a6562acee'))
    def test_both_checks_preserve_read_deadline_and_underlying_tracked_submit(self):self.exercise()
    def test_changed_baseline_active_provider_and_bad_rollback_never_enable_submit(self):
        for kwargs in [dict(active=True),dict(bad_baseline=True),dict(bad_rollback=True),dict(expired=True)]:
            with self.subTest(kwargs=kwargs):self.exercise(**kwargs)
    def test_other_targets_and_existing_intent_do_not_gain_replacement_authority(self):
        self.exercise(target='portal');self.exercise(intent={})


class ProtectRetainedBaselineTests(unittest.TestCase):
    def invoke(self, *, baseline='c'*40, digest='d'*64, flags_error=False):
        from types import SimpleNamespace
        proof=dict(originalRun=10, applicationRevision='a'*40, baselineRevision='c'*40,
            rollbackRun=8, rollbackArtifactId=9, rollbackArtifactName='retained-baseline',
            rollbackArtifactDigest='sha256:'+'e'*64, rollbackPackageDigest='d'*64)
        provider=SimpleNamespace()
        azure=deploy.RecoveryDeadlineAzure(provider,deploy.time.monotonic()+1000)
        with tempfile.TemporaryDirectory() as root, patch.dict(os.environ,GITHUB_REPOSITORY='owner/repo'), \
             patch.object(deploy._RELEASE_AUTHORITY,'_download_run_artifact') as download, \
             patch.object(deploy,'verify_package',return_value=digest), \
             patch.object(deploy,'recovery_host_flags',side_effect=ValueError('flags enabled') if flags_error else None):
            if baseline!='c'*40 or digest!='d'*64 or flags_error:
                with self.assertRaises((deploy.DeploymentDrift,ValueError)):
                    deploy.prepare_protect_retained_replacement(azure,dict(run=dict(id=10,head_sha='a'*40)),
                        dict(protectBaselineCorrection=proof),dict(revision=baseline),Path(root))
                self.assertFalse(getattr(provider,'authorized_replacement',False))
                if baseline!='c'*40:download.assert_not_called()
            else:
                deploy.prepare_protect_retained_replacement(azure,dict(run=dict(id=10,head_sha='a'*40)),
                    dict(protectBaselineCorrection=proof),dict(revision=baseline),Path(root))
                self.assertTrue(provider.authorized_replacement)
                self.assertEqual(download.call_args.kwargs,dict(artifact_id=9,expected_digest='sha256:'+'e'*64))
    def test_exact_retained_rollback_and_idle_baseline_enable_tracked_submit(self):self.invoke()
    def test_changed_live_baseline_cannot_enable_write(self):self.invoke(baseline='b'*40)
    def test_changed_rollback_bytes_cannot_enable_write(self):self.invoke(digest='f'*64)
    def test_enabled_host_flag_cannot_enable_write(self):self.invoke(flags_error=True)


class RecoveryContractBarrierTests(unittest.TestCase):
    def test_late_target_contract_drift_blocks_every_worker_and_intent(self):
        from types import SimpleNamespace
        authority = deploy._RELEASE_AUTHORITY
        spec = importlib.util.spec_from_file_location('contract_journal', ROOT/'release-operation-evidence.py')
        owner = importlib.util.module_from_spec(spec); spec.loader.exec_module(owner)
        observer = dict(runId=20,runAttempt=1,jobId=30,toolRevision='b'*40,promotionRunId=19)
        keys = list(deploy.TARGETS)
        original = dict(run=dict(id=10,run_attempt=1,head_sha='a'*40),targets=keys)
        materials = {key:dict(originalIntent=None,packageArtifact=dict(id=40+n,name='immutable-'+key,digest='sha256:'+'e'*64)) for n,key in enumerate(keys)}
        targets = {}
        for key in keys:
            identity = dict(target=key,applicationRevision='a'*40,packageDigest='d'*64)
            targets[key] = dict(operationId=hashlib.sha256(json.dumps(identity,sort_keys=True,separators=(',',':')).encode()).hexdigest(),
                packageDigest='d'*64,packageArtifactId=materials[key]['packageArtifact']['id'],packageArtifactDigest='sha256:'+'e'*64)
        original_builder = authority.fast_track_recovery_record_proof
        def drift(original, material, observer):
            proof = original_builder(original, material, observer)
            if material is materials[keys[-1]]: proof['newUnrecognizedField'] = True
            return proof
        validated = []
        actual_validator = owner.validate_record
        def validate(name, record, **kwargs):
            actual_validator(name, record, **kwargs)
            validated.append(record['target'])
        with tempfile.TemporaryDirectory() as temporary, ExitStack() as stack:
            root = Path(temporary)
            for key in keys:
                (root/key).mkdir(); (root/key/'deploy.zip').write_bytes(b'fixture'); (root/key/'SHA256SUMS').write_text('fixture')
            proof = dict(originalRun=10,originalAttempt=1,applicationRevision='a'*40,observer=observer,targets=targets)
            (root/'retained-recovery-plan.json').write_text(json.dumps(proof))
            stack.enter_context(patch.dict(os.environ,dict(GITHUB_REPOSITORY='owner/repo',GH_TOKEN='synthetic',GITHUB_RUN_ID='20',GITHUB_RUN_ATTEMPT='1')))
            stack.enter_context(patch.object(authority,'fast_track_completion_original',return_value=original))
            stack.enter_context(patch.object(authority,'fast_track_recovery_material',side_effect=lambda repo,o,key,t:materials[key]))
            stack.enter_context(patch.object(authority,'fast_track_recovery_record_proof',side_effect=drift))
            stack.enter_context(patch.object(authority,'release_operation_history',return_value=None))
            stack.enter_context(patch.object(deploy,'verify_package',return_value='d'*64))
            stack.enter_context(patch.object(deploy,'target_azure',return_value=Mock()))
            stack.enter_context(patch.object(deploy,'recovery_preupload_baseline',return_value=dict(revision='c'*40,deploymentIds=['old'])))
            stack.enter_context(patch.object(deploy,'prepare_protect_retained_replacement'))
            stack.enter_context(patch.object(owner,'validate_record',side_effect=validate))
            published = stack.enter_context(patch.object(owner,'publish_record'))
            workers = stack.enter_context(patch.object(deploy.concurrent.futures,'ThreadPoolExecutor'))
            reconcile = stack.enter_context(patch.object(deploy,'reconcile'))
            stack.enter_context(patch.object(deploy.importlib.util,'spec_from_file_location',return_value=SimpleNamespace(loader=Mock())))
            stack.enter_context(patch.object(deploy.importlib.util,'module_from_spec',return_value=owner))
            with self.assertRaisesRegex(owner.RecordContractError,'RELEASE_RECORD_CONTRACT_INVALID'):
                deploy._recover_fast_track(10,'a'*40,root,observer,deploy.time.monotonic()+1800)
            self.assertEqual(set(validated),set(keys[:-1]))
            workers.assert_not_called(); reconcile.assert_not_called()
            published.assert_called_once()
            failure = published.call_args.args[1]
            self.assertEqual(failure['phase'],'preflight-nonentry')
            self.assertEqual(failure['failedTarget'],keys[-1])
            self.assertFalse(failure['writePhaseEntered'])


class CompletionDiagnosticContextTests(unittest.TestCase):
    def state(self):
        return dict(started=100.0,baseline=deploy._diagnostic_snapshot(),executable='OPT_AZ',
                    packageVersion='2.80.0',imageVersion='20261025.353.1',count=0)

    def test_unknown_shapes_are_bounded_and_never_copy_untrusted_text(self):
        secret='SYNTHETIC_SECRET_HTTPS_TOKEN'
        cases=[('', 'EMPTY'),('ERROR: '+secret,'UNRECOGNIZED_FORMAT'),
               ('ERROR: (UnknownProvider) '+secret,'UNKNOWN_PROVIDER_CODE'),
               ('ERROR: (Forbidden) '+secret+'\nERROR: '+secret,'CONFLICTING_MARKERS'),
               ('ERROR: (Forbidden) '+secret+' HTTP/1.1 401','CONFLICTING_MARKERS'),
               ('ERROR: (Forbidden) '+secret+' ERROR: second','CONFLICTING_MARKERS'),
               (secret*1000,'OVERSIZED'),(b'ERROR: '+secret.encode(),'INVALID_TYPE')]
        for stderr,reason in cases:
            with self.subTest(reason=reason):
                result=deploy.deployment_read_error_metadata(subprocess.CompletedProcess([],1,secret,stderr))
                self.assertEqual(result['unknownReason'],reason)
                self.assertEqual(result['category'],'unknown')
                self.assertIsNone(result['providerCode']);self.assertIsNone(result['httpStatus'])
                self.assertNotIn(secret,json.dumps(result))
                self.assertTrue(result['stderrLineCount'] is None or result['stderrLineCount']<=33)
        result=deploy.deployment_read_error_metadata(subprocess.CompletedProcess([],True,'','ERROR: (Forbidden)'))
        self.assertEqual(result['unknownReason'],'INVALID_RETURN_CODE')

    def test_context_redaction_equality_unknown_and_event_cap(self):
        secret='SYNTHETIC_SECRET_DO_NOT_EMIT'
        env={'PATH':'/usr/bin','AZURE_CONFIG_DIR':'/private/'+secret,'AZURE_CLOUD_NAME':'azurecloud',
             'GH_TOKEN':secret,'ACTIONS_RUNTIME_TOKEN':secret,'ImageVersion':'20261025.353.1'}
        output=io.StringIO()
        with patch.dict(os.environ,env,clear=True),patch.object(deploy,'_diagnostic_executable',return_value='OPT_AZ'), \
             patch.object(deploy.time,'monotonic',return_value=110.0),patch('sys.stdout',output):
            state=self.state();token=deploy._COMPLETION_DIAGNOSTICS.set(state)
            try:
                deploy.completion_diagnostic('completion-entry')
                os.environ['AZURE_CONFIG_DIR']='/changed/'+secret
                os.environ['PATH']='x'*8193
                for _ in range(30):deploy.completion_diagnostic('target-entry','protect')
            finally:deploy._COMPLETION_DIAGNOSTICS.reset(token)
        self.assertNotIn(secret,output.getvalue());self.assertNotIn('/private/',output.getvalue())
        rows=[json.loads(line.split(':',1)[1]) for line in output.getvalue().splitlines()]
        self.assertEqual(len(rows),24)
        self.assertTrue(rows[0]['contextEqualsEntry']['AZURE_CONFIG_DIR'])
        self.assertFalse(rows[1]['contextEqualsEntry']['AZURE_CONFIG_DIR'])
        self.assertIsNone(rows[1]['contextEqualsEntry']['PATH'])
        self.assertEqual(rows[0]['elapsedSinceEntrySeconds'],10.0)
        self.assertFalse(rows[0]['contextIsAuthorizationProof'])
        self.assertEqual(rows[1]['target'],'protect')

    def test_no_credential_variables_are_read_and_absence_is_not_equality(self):
        class RestrictedEnv(dict):
            def get(self,key,*args):
                if key not in {'PATH','AZURE_CONFIG_DIR','AZURE_CLOUD_NAME','ImageVersion'}:
                    raise AssertionError('unexpected environment access')
                return super().get(key,*args)
        with patch.object(deploy.os,'environ',RestrictedEnv(PATH='/usr/bin')), \
             patch.object(deploy,'_diagnostic_executable',return_value=None), \
             patch.object(deploy,'_diagnostic_package_version',return_value=None),patch('sys.stdout',new_callable=io.StringIO) as output:
            state=deploy._start_completion_diagnostics();self.assertIsNotNone(state)
            token=deploy._COMPLETION_DIAGNOSTICS.set(state)
            try:deploy.completion_diagnostic('completion-entry')
            finally:deploy._COMPLETION_DIAGNOSTICS.reset(token)
            report=json.loads(output.getvalue().split(':',1)[1])
            self.assertIsNone(report['contextEqualsEntry']['AZURE_CONFIG_DIR'])
            self.assertIsNone(report['executableEqualsEntry'])

    def test_noncompletion_has_no_context_events_or_metadata_reads(self):
        with patch.object(deploy,'_diagnostic_snapshot') as snapshot,patch('sys.stdout',new_callable=io.StringIO) as output:
            deploy.completion_diagnostic('deployment-read','protect')
            snapshot.assert_not_called();self.assertEqual(output.getvalue(),'')

    def test_context_resets_after_success_and_original_failure(self):
        from contextlib import nullcontext
        for error in [None,RuntimeError('original failure')]:
            with patch.object(deploy,'_start_completion_diagnostics',return_value=self.state()), \
                 patch.object(deploy._RELEASE_AUTHORITY,'recovery_material_reuse',return_value=nullcontext()), \
                 patch.object(deploy._RELEASE_AUTHORITY,'report_recovery_efficiency'), \
                 patch.object(deploy,'_complete_fast_track',side_effect=error,return_value={'original':'result'}), \
                 patch.object(deploy,'_diagnostic_snapshot',side_effect=ValueError('diagnostic secret')):
                if error:
                    with self.assertRaisesRegex(RuntimeError,'original failure'):deploy.complete_fast_track(1,'a'*40,Path('/x'))
                else:self.assertEqual(deploy.complete_fast_track(1,'a'*40,Path('/x')),{'original':'result'})
                self.assertIsNone(deploy._COMPLETION_DIAGNOSTICS.get())

    def test_diagnostic_errors_do_not_change_read_command_cap_or_result(self):
        azure=deploy.target_azure('protect',None,'a'*40)
        for code in [0,1]:
            with patch.object(deploy,'completion_diagnostic',side_effect=RuntimeError('secret')), \
                 patch.object(deploy,'deployment_read_process',return_value=subprocess.CompletedProcess([],code,'[]','')) as process, \
                 patch('sys.stdout',new_callable=io.StringIO):
                if code:
                    with self.assertRaisesRegex(RuntimeError,'^Azure deployment status unavailable$'):azure.deployments()
                else:self.assertEqual(azure.deployments(),[])
                self.assertEqual(process.call_args.args[0],['az','webapp','log','deployment','list','-g',
                    'masterapp-rg','-n','masterapp-protect','--only-show-errors','-o','json'])
                self.assertEqual(process.call_args.kwargs,{'timeout':40})

    def test_executable_resolution_rejects_unbounded_or_unknown_path(self):
        for path in ['x'*8193,':'.join(['x']*65)]:
            with patch.dict(os.environ,{'PATH':path}),patch.object(deploy.shutil,'which') as which:
                self.assertIsNone(deploy._diagnostic_executable());which.assert_not_called()
        with patch.dict(os.environ,{'PATH':'/usr/bin'}),patch.object(deploy.shutil,'which',return_value='/untrusted/az'):
            self.assertIsNone(deploy._diagnostic_executable())

    def test_diagnostic_elapsed_time_consumes_read_cap_and_exhaustion_starts_no_child(self):
        azure=deploy.target_azure('protect',None,'a'*40)
        token=deploy._COMPLETION_DIAGNOSTICS.set({})
        try:
            with patch.object(deploy.time,'monotonic',side_effect=[100.0,103.0]), \
                 patch.object(deploy,'completion_diagnostic'), \
                 patch.object(deploy,'deployment_read_process',return_value=subprocess.CompletedProcess([],0,'[]','')) as read:
                self.assertEqual(azure.deployments(),[])
                self.assertEqual(read.call_args.kwargs,{'timeout':37.0})
            with patch.object(deploy.time,'monotonic',side_effect=[100.0,140.0]), \
                 patch.object(deploy,'completion_diagnostic',side_effect=RuntimeError('diagnostic failed')), \
                 patch.object(deploy.subprocess,'Popen') as child:
                with self.assertRaises(subprocess.TimeoutExpired):azure.deployments()
                child.assert_not_called()
        finally:deploy._COMPLETION_DIAGNOSTICS.reset(token)

    def test_package_metadata_identity_and_ambiguity_redaction(self):
        real_scandir=os.scandir
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);packages=root/'python3.13/site-packages';packages.mkdir(parents=True)
            dist=packages/'azure_cli-2.80.0.dist-info';dist.mkdir();metadata=dist/'METADATA'
            def scan(path):return real_scandir(root if str(path)=='/opt/az/lib' else path)
            with patch.object(deploy.os,'scandir',side_effect=scan), \
                 patch.object(deploy.subprocess,'Popen',side_effect=AssertionError('no diagnostic process')):
                for content,expected in [('Name: azure-cli\nVersion: 2.80.0\n','2.80.0'),
                    ('Name: other\nVersion: 2.80.0\n',None),
                    ('Name: azure-cli\nVersion: 2.80.0\nVersion: 2.80.0\n',None),
                    ('Name: azure-cli\nVersion: 2.79.0\n',None),('x'*8193,None)]:
                    metadata.write_text(content)
                    self.assertEqual(deploy._diagnostic_package_version('OPT_AZ'),expected)
                metadata.write_text('Name: azure-cli\nVersion: 2.80.0\n')
                other=packages/'azure_cli-2.79.0.dist-info';other.mkdir()
                (other/'METADATA').write_text('Name: azure-cli\nVersion: 2.79.0\n')
                self.assertIsNone(deploy._diagnostic_package_version('OPT_AZ'))
                shutil.rmtree(other);metadata.unlink();metadata.symlink_to(root/'secret')
                (root/'secret').write_text('Name: azure-cli\nVersion: 2.80.0\n')
                with self.assertRaises(OSError):deploy._diagnostic_package_version('OPT_AZ')



class Protect161ProductionRecoveryTests(unittest.TestCase):
    def test_exact_rollback_material_enables_tracked_not_async_submission(self):
        from types import SimpleNamespace
        p=dict(deploy._RELEASE_AUTHORITY.PROTECT_161_REPLACEMENT)
        for baseline,digest,okay in [(p['baselineRevision'],p['rollbackPackageDigest'],True),('a'*40,p['rollbackPackageDigest'],False),(p['baselineRevision'],'b'*64,False)]:
            provider=SimpleNamespace();azure=deploy.RecoveryDeadlineAzure(provider,deploy.time.monotonic()+1000)
            with tempfile.TemporaryDirectory() as directory,patch.dict(os.environ,GITHUB_REPOSITORY='MYLEGND/masterapp'),patch.object(deploy._RELEASE_AUTHORITY,'_download_run_artifact') as download,patch.object(deploy,'verify_package',return_value=digest),patch.object(deploy,'recovery_host_flags'):
                def invoke():deploy.prepare_protect_retained_replacement(azure,dict(run=dict(id=p['originalRun'],head_sha=p['applicationRevision'])),dict(unknownReplacement=p),dict(revision=baseline),Path(directory))
                if okay:
                    invoke();self.assertTrue(provider.authorized_replacement)
                    self.assertEqual(download.call_args.kwargs,dict(artifact_id=p['rollbackArtifactId'],expected_digest=p['rollbackArtifactDigest']))
                else:
                    with self.assertRaises(deploy.DeploymentDrift):invoke()
                    self.assertFalse(getattr(provider,'authorized_replacement',False))
    def test_late_original_success_active_or_slow_read_never_authorizes_replacement(self):
        p=deploy._RELEASE_AUTHORITY.PROTECT_161_REPLACEMENT
        original=dict(run=dict(head_sha=p['applicationRevision']),attempts={1:[dict(name='Affected App Deploy — Protect',steps=[dict(name='Publish or reconcile exact application bytes',started_at='2026-10-29T20:22:00Z')])]})
        for observed,rows in [(p['applicationRevision'],[dict(id='late',status=4,received_time='2026-10-29T20:30:00Z')]),(None,[dict(id='old',status=4,received_time='2026-10-29T19:11:00Z')]),(p['baselineRevision'],[dict(id='active',status=1)])]:
            azure=Mock();azure.observed_revision.return_value=observed
            with patch.object(deploy,'read_deployments_bounded',return_value=rows),patch.object(deploy.subprocess,'run',return_value=Mock(returncode=0)),self.assertRaises(deploy.DeploymentReconciliationRequired):
                deploy.recovery_preupload_baseline(azure,original,'protect')
            azure.submit.assert_not_called()
    def test_preparation_reuses_original_bytes_but_selects_new_protect_identity_only(self):
        a=deploy._RELEASE_AUTHORITY;p=dict(a.PROTECT_161_REPLACEMENT)
        original=dict(run=dict(id=p['originalRun'],head_sha=p['applicationRevision'],run_attempt=2),targets=['website','protect'])
        materials={key:dict(packageArtifact=dict(id=p['candidateArtifactId'] if key=='protect' else 42,name='retained-'+key,digest=p['candidateArtifactDigest'] if key=='protect' else 'sha256:'+'a'*64),originalIntent=dict(target=key,applicationRevision=p['applicationRevision'],packageDigest=p['packageDigest'] if key=='protect' else 'd'*64,packageProducerRun=p['originalRun'])) for key in original['targets']}
        for key,value in materials.items():value['originalIntent']['operationId']=a.retained_operation_id(key,p['applicationRevision'],value['originalIntent']['packageDigest'])
        materials['protect']['unknownReplacement']=p
        before=json.dumps(materials,sort_keys=True)
        def download(repo,run,name,path,**kw):
            path.mkdir(parents=True);(path/'deploy.zip').write_bytes(b'fixture');(path/'SHA256SUMS').write_text('fixture')
        with tempfile.TemporaryDirectory() as root,patch.dict(os.environ,GITHUB_REPOSITORY='MYLEGND/masterapp',GH_TOKEN='test-only'),patch.object(a,'fast_track_completion_original',return_value=original),patch.object(a,'fast_track_recovery_material',side_effect=lambda repo,o,key,token:materials[key]),patch.object(a,'_download_run_artifact',side_effect=download) as restored,patch.object(deploy,'verify_package',side_effect=lambda path,*args:materials[path.parent.name]['originalIntent']['packageDigest']),patch.object(deploy,'target_azure') as provider:
            result=deploy._recover_fast_track(p['originalRun'],p['applicationRevision'],Path(root),dict(runId=999),deploy.time.monotonic()+1000,prepare_only=True)
            self.assertEqual(result['targets']['protect']['operationId'],a.retained_operation_id('protect',p['applicationRevision'],p['packageDigest'],p))
            self.assertEqual(result['targets']['website']['operationId'],materials['website']['originalIntent']['operationId'])
            self.assertEqual(restored.call_count,2);provider.assert_not_called()
        self.assertEqual(json.dumps(materials,sort_keys=True),before)

class StableBaselineRollbackTests(unittest.TestCase):
    def test_legacy_scope_no_new_rollback_route(self):
        self.assertIsNone(deploy.prepare_stable_baseline_rollback({},'portal',{},Path('/unused')))
    def test_missing_or_expired_exact_rollback_blocks_before_download(self):
        authority=deploy._RELEASE_AUTHORITY
        with patch.dict(os.environ,{'GITHUB_REPOSITORY':'MYLEGND/masterapp','GITHUB_TOKEN':'test'}), patch.object(authority,'api_get',return_value=dict(total_count=0,artifacts=[])), patch.object(authority,'_download_run_artifact') as download:
            with self.assertRaises(deploy.DeploymentReconciliationRequired):
                deploy.prepare_stable_baseline_rollback({'baselineAdoption':{}},'portal',{'revision':'a'*40},Path('/unused'))
            download.assert_not_called()
    def test_rollback_verified_bytes_and_changed_readback_rejected(self):
        authority=deploy._RELEASE_AUTHORITY;revision='a'*40
        artifact=dict(id=7,name='fast-track-package-'+revision+'-portal',expired=False,digest='sha256:'+'b'*64,workflow_run=dict(id=8,head_sha=revision))
        run=dict(id=8,head_sha=revision,status='completed')
        with tempfile.TemporaryDirectory() as temp, patch.dict(os.environ,{'GITHUB_REPOSITORY':'MYLEGND/masterapp','GITHUB_TOKEN':'test'}), patch.object(authority,'api_get',side_effect=lambda repo,path,token:dict(total_count=1,artifacts=[artifact]) if path.startswith('actions/artifacts') else run), patch.object(authority,'is_fast_track_producer',return_value=True), patch.object(authority,'_completion_jobs',return_value={}), patch.object(authority,'_completion_original_artifact_owner'), patch.object(deploy,'verify_package',return_value='c'*64):
            root=Path(temp);directory=root/'stable-rollback-portal';directory.mkdir();(directory/'deploy.zip').write_bytes(b'retained-fixture')
            proof=deploy.prepare_stable_baseline_rollback({'baselineAdoption':{}},'portal',{'revision':revision},root)
            self.assertEqual(proof['artifactId'],7);self.assertEqual(proof['packageDigest'],'c'*64)
            with self.assertRaises(deploy.DeploymentDrift):
                deploy.prepare_stable_baseline_rollback({'baselineAdoption':{}},'portal',{'revision':revision},root,expected=dict(proof,packageDigest='d'*64))


if __name__ == '__main__':
    unittest.main()
