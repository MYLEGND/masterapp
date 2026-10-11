#!/usr/bin/env python3
"""Adversarial fast-track admission, immutable recovery and provider readback."""
import hashlib
import importlib.util
import os
import json
import tempfile
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch


def load(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


authority = load('validation-resume')
probe = load('migration-probe-package')
router = load('release-router')
migration = load('release-migration')
deploy = load('deploy-approved-app')


class ObserverCompatibilityTests(unittest.TestCase):
    def compatible(self, changed=False, runtime=False, body=False):
        def source(ref, path):
            suffix = 'return 2' if body and ref == 'new' else 'return 1'
            return '\n'.join('def '+name+'():\n    '+suffix for name in ['rehearse','rehearse_fast_track','isolated_sql'])
        with patch.object(probe.subprocess, 'run', return_value=SimpleNamespace(returncode=int(changed))), \
             patch.object(probe.AUTHORITY, 'migration_probe_identity', side_effect=lambda r,*args: {'runtimeIdentity':r if runtime else 'same'}), \
             patch.object(probe.AUTHORITY, 'git_show_file', side_effect=source):
            return probe.fast_track_probe_compatible('old','new')

    def test_changed_probe_fixture_or_candidate_contract_rejects_stale_observer(self):
        self.assertFalse(self.compatible(changed=True))

    def test_changed_runtime_or_rehearsal_rejects_old_observer(self):
        self.assertFalse(self.compatible(runtime=True))
        self.assertFalse(self.compatible(body=True))

    def test_wiring_only_changes_preserve_rehearsed_bytes(self):
        self.assertTrue(self.compatible())


class RouterRecoveryTests(unittest.TestCase):
    def journal(self):
        j=SimpleNamespace(identity='a'*64, intent=None, success=None)
        def intent(observation): j.intent={'observation':observation}
        def success(observation): j.success={'observation':observation}
        j.before_mutation=Mock(side_effect=intent)
        j.record_success=Mock(side_effect=success)
        return j

    def test_lost_publication_ack_reconciles_and_repeated_call_preserves(self):
        j=self.journal();live=['old',''];writes=[]
        def publish(identity):
            self.assertIsNotNone(j.intent)
            writes.append(identity);live[:]=['new',identity]
            raise TimeoutError('lost response')
        self.assertEqual('verified',router.reconcile_tagged_router(j,lambda:live,publish,sleep=lambda _:None))
        self.assertEqual('preserved',router.reconcile_tagged_router(j,lambda:live,publish,sleep=lambda _:None))
        self.assertEqual([j.identity],writes)

    def test_existing_uncertain_intent_never_reuploads(self):
        j=self.journal();j.intent={};publish=Mock()
        with self.assertRaisesRegex(RuntimeError,'unresolved'):
            router.reconcile_tagged_router(j,lambda:('old','other-operation'),publish,sleep=lambda _:None)
        publish.assert_not_called();j.record_success.assert_not_called()

    def test_durable_intent_failure_blocks_upload(self):
        j=self.journal();j.before_mutation.side_effect=RuntimeError('readback missing');publish=Mock()
        with self.assertRaisesRegex(RuntimeError,'readback missing'):
            router.reconcile_tagged_router(j,lambda:('old',''),publish,sleep=lambda _:None)
        publish.assert_not_called()

    def test_success_drift_is_not_repaired_with_another_upload(self):
        j=self.journal();j.success={'observation':{'providerVersion':'original'}};publish=Mock()
        with self.assertRaisesRegex(RuntimeError,'drift'):
            router.reconcile_tagged_router(j,lambda:('replacement',j.identity),publish,sleep=lambda _:None)
        publish.assert_not_called()


class RouterConfigurationTests(unittest.TestCase):
    def test_secret_flag_is_unknown_not_false(self):
        for binding in [dict(type='secret_text'), dict(type='plain_text',text='true'), dict(type='json',json=False)]:
            with self.subTest(binding=binding), self.assertRaises(RuntimeError):
                router.require_disabled_cutover([dict(binding,name='LEGEND_COMMERCE_CUTOVER_ENABLED')])
        router.require_disabled_cutover([])
        router.require_disabled_cutover([dict(name='LEGEND_COMMERCE_CUTOVER_ENABLED',type='plain_text',text='false')])


class CompanyRendererAdmissionTests(unittest.TestCase):
    def test_http_success_without_published_html_is_not_renderer_readiness(self):
        from unittest.mock import MagicMock
        for body, succeeds in [(b'<main>stale static page</main>',False),
                (b'<main>Published</main><script id="legend-cms-published-document">{}</script>',True)]:
            response=MagicMock();response.__enter__.return_value=response
            response.status=200;response.headers={'Content-Type':'text/html'};response.read.return_value=body
            with patch.object(router.urllib.request,'urlopen',return_value=response):
                if succeeds:router.company_render_ready(authority)
                else:
                    with self.assertRaises(RuntimeError):router.company_render_ready(authority)


class SuccessfulSiblingTests(unittest.TestCase):
    def test_prior_successful_migration_is_preserved_for_failed_app_retry(self):
        run=dict(id=12,head_sha='a'*40,run_attempt=3)
        job=dict(name='Reconcile production schema before publication',run_id=12,head_sha='a'*40,
                 run_attempt=1,status='completed',conclusion='success')
        with patch.dict(os.environ,GITHUB_REPOSITORY='owner/repo',GITHUB_TOKEN='fixture'), \
             patch.object(deploy._RELEASE_AUTHORITY,'api_get',return_value=dict(total_count=1,jobs=[job])) as api:
            self.assertEqual(job,deploy.fast_track_migration_gate(run))
            self.assertIn('filter=latest',api.call_args.args[1])
            job['conclusion']='failure'
            with self.assertRaises(ValueError):deploy.fast_track_migration_gate(run)
            job['conclusion']='success';job['head_sha']='b'*40
            with self.assertRaises(ValueError):deploy.fast_track_migration_gate(run)


class FastTrackHistoryTests(unittest.TestCase):
    def setUp(self):
        self.run=dict(id=10,run_attempt=1,status='completed',event='push',head_sha='a'*40,
            head_branch=authority.FAST_TRACK_PR_BASE,path='.github/workflows/'+authority.FAST_TRACK_RELEASE_WORKFLOW,
            head_repository={'full_name':'owner/repo'})
        self.step=dict(name=authority.FAST_TRACK_CHILD_STEPS['migrations'][1],conclusion='failure',status='completed')
        self.job=dict(name=authority.FAST_TRACK_CHILD_STEPS['migrations'][0],steps=[self.step],status='completed',conclusion='failure')

    def attempts(self, complete=True):
        with patch.object(authority,'fast_track_execution_history',return_value=[self.run]), \
             patch.object(authority,'_release_history_source',return_value='source'), \
             patch.object(authority,'named_step_blocks',return_value={self.step['name']:'source'}), \
             patch.object(authority,'api_get',return_value={'total_count':1 if complete else 2,'jobs':[self.job]}):
            return authority.fast_track_child_attempts('owner/repo','migrations','fixture',current_run=11,current_attempt=1)

    def test_failed_entered_step_is_still_a_possible_write(self):
        self.assertEqual(1,len(self.attempts()))

    def test_skipped_step_proves_no_entry(self):
        self.step['conclusion']='skipped'
        self.assertEqual([],self.attempts())

    def test_incomplete_inventory_or_active_previous_worker_denies_admission(self):
        with self.assertRaisesRegex(RuntimeError,'INCOMPLETE'):self.attempts(False)
        self.run['status']='in_progress'
        with self.assertRaisesRegex(RuntimeError,'ACTIVE'):self.attempts()

    def test_partial_rerun_does_not_erase_successful_migration_sibling(self):
        self.run['run_attempt']=2
        self.step['conclusion']='success';self.job['conclusion']='success'
        def get(repo,path,token):
            jobs=[self.job] if '/attempts/1/' in path else [dict(name='Affected App Deploy — Protect')]
            return dict(total_count=len(jobs),jobs=jobs)
        with patch.object(authority,'fast_track_execution_history',return_value=[self.run]), \
             patch.object(authority,'_release_history_source',return_value='source'), \
             patch.object(authority,'named_step_blocks',return_value={self.step['name']:'source'}), \
             patch.object(authority,'api_get',side_effect=get):
            rows=authority.fast_track_child_attempts('owner/repo','migrations','fixture')
            self.assertEqual([1],[row[1] for row in rows])

    def test_missing_or_expired_intent_after_entry_cannot_authorize_mutation(self):
        for artifacts in [[],[dict(name='legend-release-child-intent-'+ 'c'*64,expired=True)]]:
            with self.subTest(artifacts=artifacts), \
                 patch.object(authority,'fast_track_child_attempts',return_value=[(self.run,1,self.job,self.step)]), \
                 patch.object(authority,'api_get',return_value=dict(total_count=len(artifacts),artifacts=artifacts)), \
                 self.assertRaises(RuntimeError):
                authority.release_child_first_write_proven('owner/repo','migrations','b'*64,'c'*64,11,1,'fixture')


class MergedSourceReceiptTests(unittest.TestCase):
    def setUp(self):
        self.run=dict(id=71,run_attempt=1,pull_requests=[])
        self.workflow='approved-release-security-validation.yml'
        self.name='validation-resume-security-71-1'
        self.artifact=dict(id=81,name=self.name,expired=False,workflow_run=dict(id=71))
        self.receipt=dict(workflow=self.workflow,currentSha='a'*40,validationBase=authority.FAST_TRACK_PR_BASE,
            sourceValidationScope=dict(phase='source-validation-only',releaseAdmission=False,
                baseBranch=authority.FAST_TRACK_PR_BASE,candidate='a'*40,pullRequest=580))
    def verify(self):
        def download(repo,run,name,path,**kwargs):
            self.assertEqual(81,kwargs['artifact_id'])
            (path/'validation-resume.json').write_text(json.dumps(self.receipt))
        with patch.object(authority,'api_get',return_value=dict(total_count=1,artifacts=[self.artifact])), \
             patch.object(authority,'_download_run_artifact',side_effect=download):
            return authority.fast_track_validation_receipt('owner/repo',self.run,self.workflow,'a'*40,580,'fixture')
    def test_merge_cleared_pr_links_are_replaced_by_original_bound_receipt(self):
        self.assertEqual(81,self.verify())
    def test_wrong_pr_base_or_candidate_cannot_relabel_validation(self):
        for key,value in [('pullRequest',581),('baseBranch','legend/approved-changes'),('candidate','b'*40),('releaseAdmission',True)]:
            original=dict(self.receipt['sourceValidationScope'])
            self.receipt['sourceValidationScope'][key]=value
            with self.subTest(key=key),self.assertRaises(RuntimeError):self.verify()
            self.receipt['sourceValidationScope']=original
    def test_expired_receipt_is_not_replaced_by_empty_pr_links(self):
        self.artifact['expired']=True
        with self.assertRaisesRegex(RuntimeError,'MISSING'):self.verify()


class AdmissionRepairScopeTests(unittest.TestCase):
    def baseline(self, entered=False, attempt=1, duplicate=False, recovery=False, mutation="skipped"):
        before='a'*40;head='b'*40;original='c'*40
        prior=dict(id=7,run_attempt=attempt,head_sha=before,head_branch=authority.FAST_TRACK_PR_BASE,event='push',
            path='.github/workflows/'+authority.FAST_TRACK_RELEASE_WORKFLOW,
            head_repository={'full_name':'owner/repo'},status='completed',conclusion='failure')
        jobs=[dict(name='Admit exact validated fast-track source',conclusion='failure'),
              dict(name='Reconcile production schema before publication',conclusion='failure' if entered else 'skipped'),
              dict(name='Affected App Deploy — ${{ matrix.target }}',conclusion='skipped'),
              dict(name='Publish verified website routing',conclusion='skipped')]
        if recovery:
            jobs[0]['conclusion']='success'
            jobs[1].update(conclusion='failure',steps=[dict(name='Apply or reconcile the exact rehearsed migration bundle',conclusion=mutation)])
        def get(repo,path,token):
            if path.startswith('actions/runs?'):
                runs=([prior,dict(prior,id=8)] if duplicate else [prior]) if before in path else []
                return dict(total_count=len(runs),workflow_runs=runs)
            if '/jobs?' in path:return dict(total_count=len(jobs),jobs=jobs)
            if path.startswith('commits/'):
                return [dict(merged_at='time',merge_commit_sha=before,base=dict(ref=authority.FAST_TRACK_PR_BASE),head=dict(repo=dict(full_name='owner/repo')))]
            raise AssertionError(path)
        with patch.dict(os.environ,GITHUB_REPOSITORY='owner/repo',GITHUB_TOKEN='fixture'), \
             patch.object(authority,'api_get',side_effect=get), \
             patch.object(authority.subprocess,'run',return_value=SimpleNamespace(returncode=0)), \
             patch.object(authority.subprocess,'check_output',return_value=before+' '+original+' '+head):
            return authority.fast_track_effective_baseline(before,head)
    def test_repair_inherits_only_proven_unpublished_predecessor_scope(self):
        self.assertEqual('c'*40,self.baseline())
    def test_any_entered_writer_requires_operation_reconciliation(self):
        with self.assertRaisesRegex(RuntimeError,'RECONCILIATION'):self.baseline(entered=True)

    def test_rerun_predecessor_requires_operation_reconciliation(self):
        with self.assertRaisesRegex(RuntimeError,'RECONCILIATION'):self.baseline(attempt=2)
    def test_multiple_runs_require_operation_reconciliation(self):
        with self.assertRaisesRegex(RuntimeError,'RECONCILIATION'):self.baseline(duplicate=True)

    def test_longer_proven_prewrite_repair_chain_keeps_original_application_scope(self):
        heads=[format(i+1,'040x') for i in range(9)]
        original='c'*40;head='b'*40
        jobs=[dict(name='Admit exact validated fast-track source',conclusion='failure'),
              dict(name='Reconcile production schema before publication',conclusion='skipped'),
              dict(name='Affected App Deploy — target',conclusion='skipped'),
              dict(name='Publish verified website routing',conclusion='skipped')]
        def get(repo,path,token):
            if path.startswith('actions/runs?'):
                candidate=path.split('head_sha=')[1].split('&')[0]
                rows=[] if candidate==original else [dict(id=heads.index(candidate)+1,run_attempt=1,
                    head_sha=candidate,head_branch=authority.FAST_TRACK_PR_BASE,event='push',
                    path='.github/workflows/'+authority.FAST_TRACK_RELEASE_WORKFLOW,
                    head_repository={'full_name':'owner/repo'},status='completed',conclusion='failure')]
                return dict(total_count=len(rows),workflow_runs=rows)
            if '/jobs?' in path:return dict(total_count=len(jobs),jobs=jobs)
            candidate=path.split('/')[1]
            return [dict(merged_at='time',merge_commit_sha=candidate,base=dict(ref=authority.FAST_TRACK_PR_BASE),
                head=dict(repo=dict(full_name='owner/repo')))]
        def parents(args,**kwargs):
            candidate=args[-1]
            if candidate==original:return original
            i=heads.index(candidate)
            return candidate+' '+(heads[i+1] if i+1<len(heads) else original)+' '+head
        with patch.dict(os.environ,GITHUB_REPOSITORY='owner/repo',GITHUB_TOKEN='fixture'), \
             patch.object(authority,'api_get',side_effect=get), \
             patch.object(authority.subprocess,'run',return_value=SimpleNamespace(returncode=0)), \
             patch.object(authority.subprocess,'check_output',side_effect=parents):
            self.assertEqual(original,authority.fast_track_effective_baseline(heads[0],head))


    def test_prewrite_recovery_failure_preserves_unpublished_scope(self):
        self.assertEqual('c'*40,self.baseline(recovery=True))
    def test_entered_migration_cannot_inherit_scope(self):
        with self.assertRaisesRegex(RuntimeError,'RECONCILIATION'):self.baseline(recovery=True,mutation='failure')


class FastTrackPrewriteEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.head='3bae3fd25ce92f2140b23230f9ef94e45888c5e9'
        self.run=dict(id=38155548986,head_sha=self.head,head_branch=authority.FAST_TRACK_PR_BASE,
            event='push',path='.github/workflows/'+authority.FAST_TRACK_RELEASE_WORKFLOW,
            head_repository={'full_name':'owner/repo'},status='completed',conclusion='failure',run_attempt=1)
        self.step=dict(name=authority.FAST_TRACK_CHILD_STEPS['migrations'][1],status='completed',conclusion='failure',
            started_at='2026-10-11T16:33:54Z',completed_at='2026-10-11T16:38:19Z')
        self.jobs=[dict(id=91,name=authority.FAST_TRACK_CHILD_STEPS['migrations'][0],status='completed',conclusion='failure',steps=[self.step]),
            dict(name='Affected App Deploy — target',conclusion='skipped'),
            dict(name='Publish verified website routing',conclusion='skipped')]
        self.log=('2026-10-11T16:32:00Z [command]/usr/bin/git log -1 --format=%H\n'
            +'2026-10-11T16:32:01Z '+self.head+'\n'
            +'2026-10-11T16:38:18Z Migration stage unresolved: mutation-admission; preserve prior evidence and reconcile without replay.\n')
        self.inventory=dict(total_count=0,artifacts=[])
    def prove(self):
        with patch.object(authority,'api_get',return_value=self.inventory), \
             patch.object(authority,'_release_job_log',return_value=self.log):
            return authority.attested_fast_track_migration_prewrite('owner/repo',self.run,self.jobs,'fixture')
    def test_exact_pre_bundle_failure_proves_nonentry(self):
        self.assertTrue(self.prove())
    def test_absence_alone_changed_source_or_out_of_step_marker_cannot_prove_nonentry(self):
        original=self.log
        for text in ('',original.replace('mutation-admission','bundle-execution'),
                     original.replace('16:38:18Z','16:32:18Z'),original+original.splitlines()[-1]+'\n',
                     original.replace(self.head,'f'*40)):
            self.log=text
            self.assertFalse(self.prove())
        self.log=original
        with patch.object(authority,'_release_history_source',return_value='changed original owner'):
            self.assertFalse(self.prove())
    def test_retained_intent_incomplete_inventory_or_entered_publication_still_blocks(self):
        for inventory in (dict(total_count=1,artifacts=[]),dict(total_count=1,artifacts=[dict(id=1,name='legend-release-child-intent-'+'a'*64)])):
            self.inventory=inventory;self.assertFalse(self.prove())
        self.inventory=dict(total_count=0,artifacts=[])
        self.jobs[1]['conclusion']='failure';self.assertFalse(self.prove())
        self.jobs[1]['conclusion']='skipped'
        self.run['run_attempt']=2;self.assertFalse(self.prove())
    def test_same_proof_controls_unpublished_scope(self):
        with patch.object(authority,'attested_fast_track_migration_prewrite',return_value=True):
            self.assertEqual('c'*40,AdmissionRepairScopeTests().baseline(recovery=True,mutation='failure'))
    def test_each_fast_track_mutation_job_prepares_artifact_runtime_before_writes(self):
        source=Path('.github/workflows/legend-fast-track-ui.yml').read_text()
        jobs=authority._job_blocks(source)
        for job,write in [('migrations','Apply or reconcile the exact rehearsed migration bundle'),
                          ('deploy-ui','Sign in to Azure Production'),
                          ('routing-cloudflare','Publish or reconcile canonical website routing')]:
            body=jobs[job]
            self.assertEqual(1,body.count('uses: ./scripts/release-artifacts'))
            self.assertLess(body.index('uses: ./scripts/release-artifacts'),body.index(write))


class RecoveryWindowTests(unittest.TestCase):
    def test_exact_database_with_managed_restore_window_is_read_only_proof(self):
        from datetime import datetime,timezone
        now=datetime(2026,10,11,tzinfo=timezone.utc)
        calls=[]
        subscription='11111111-1111-1111-1111-111111111111'
        resource='/subscriptions/'+subscription+'/resourceGroups/data-group/providers/Microsoft.Sql/servers/server'
        def read(*args):
            calls.append(args)
            self.assertNotIn('app-group', args)
            if args[:3]==('sql','server','show'):
                self.assertEqual(args, ('sql','server','show','--ids',resource))
                return dict(id=resource,name='server',resourceGroup='data-group')
            self.assertEqual(args[-2:], ('--subscription',subscription))
            self.assertIn('data-group', args)
            if args[:3]==('sql','db','list'):return [dict(name='db',status='Online',earliestRestoreDate='2026-10-01T00:00:00Z')]
            return dict(retentionDays=7)
        identity=hashlib.sha256(b'server/db').hexdigest()
        with patch.dict(os.environ,RELEASE_DATABASE_SERVER_RESOURCE_ID=resource,RELEASE_RESOURCE_GROUP='app-group'):
            proof=migration.production_recovery(identity,read=read,now=now)
            self.assertFalse(proof['restoreExecuted']);self.assertFalse(proof['authorizesMutation'])
            self.assertTrue(proof['restoreWindowVerified'])
            with self.assertRaisesRegex(RuntimeError,'RESOURCE_UNPROVEN'):
                migration.production_recovery(identity,read=lambda *args:dict(id=resource+'/wrong',name='server',resourceGroup='data-group'),now=now)
            with patch.object(migration.subprocess,'run',return_value=SimpleNamespace(returncode=1,stderr='AuthorizationFailed',stdout='')):
                with self.assertRaisesRegex(RuntimeError,'AZURE_READ_DENIED'):
                    migration.production_recovery(identity,now=now)
            with self.assertRaisesRegex(RuntimeError,'IDENTITY'):
                migration.production_recovery('a'*64,read=read,now=now)
        self.assertTrue(all('list' in c or 'show' in c for c in calls))



class SkippedMatrixRecoveryTests(unittest.TestCase):
    def setUp(self):
        self.jobs=[dict(name='Affected App Deploy — ${{ matrix.target }}',status='completed',conclusion='skipped',steps=[])]
        self.run=dict(id=17,head_sha='a'*40,run_attempt=3)
    def restore(self):
        def get(repo,path,token):
            if '/artifacts?' in path:return dict(total_count=0,artifacts=[])
            return dict(total_count=len(self.jobs),jobs=self.jobs)
        with patch.dict(os.environ,GITHUB_REPOSITORY='owner/repo',GITHUB_TOKEN='fixture'), \
             patch.object(deploy._RELEASE_AUTHORITY,'assert_fast_track_release_execution',return_value=self.run), \
             patch.object(deploy._RELEASE_AUTHORITY,'api_get',side_effect=get):
            return deploy.fast_track_restore_package('portal',Path('/unused'))
    def test_unexpanded_skipped_matrix_allows_first_build(self):
        self.assertFalse(self.restore())
    def test_missing_active_or_executed_matrix_cannot_prove_nonentry(self):
        for key,value in [('name','unknown'),('status','in_progress'),('conclusion','failure'),('steps',[dict(name='upload')])]:
            original=dict(self.jobs[0]);self.jobs[0][key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):self.restore()
            self.jobs[0]=original
    def test_entered_upload_without_package_never_rebuilds(self):
        self.jobs=[dict(name='Affected App Deploy — AgentPortal',status='completed',conclusion='failure',
            steps=[dict(name='Publish or reconcile exact application bytes',conclusion='failure')])]
        with self.assertRaisesRegex(ValueError,'NO_REBUILD'):self.restore()
    def test_missing_already_built_package_never_rebuilds(self):
        self.jobs=[dict(name='Affected App Deploy — AgentPortal',status='completed',conclusion='failure',steps=[
            dict(name='Compile application and produce ZIP',conclusion='success'),
            dict(name='Publish or reconcile exact application bytes',conclusion='skipped')])]
        with self.assertRaisesRegex(ValueError,'NO_REBUILD'):self.restore()

    def test_readonly_identity_refresh_is_bounded_and_does_not_relax_admission(self):
        with patch.object(deploy._RELEASE_AUTHORITY,'assert_fast_track_release_execution',
                side_effect=RuntimeError('FAST_TRACK_EXECUTION_PRODUCER_UNPROVEN')) as check, \
             patch.object(deploy.time,'sleep') as sleep:
            with self.assertRaisesRegex(RuntimeError,'PRODUCER_UNPROVEN'):
                deploy.fast_track_restore_package('portal',Path('/unused'))
            self.assertEqual(3,check.call_count);self.assertEqual(2,sleep.call_count)


class CompletedSqlScopeTests(unittest.TestCase):
    def setUp(self):
        self.run=dict(id=17,run_attempt=3,head_sha='a'*40,status='completed',conclusion='failure')
        self.routing=dict(name='Publish verified website routing',status='completed',conclusion='skipped')
        self.first=[dict(name='Affected App Deploy — ${{ matrix.target }}',status='completed',conclusion='skipped',steps=[]),self.routing]
        self.apps=[dict(name='Affected App Deploy — '+name,status='completed',conclusion='failure',steps=[
            dict(name='Publish or reconcile exact application bytes',status='completed',conclusion='skipped')])
            for name in ('AgentPortal','ClientApp','Protect','Parfait','Website')]
        self.latest=self.apps+[self.routing,dict(name=authority.FAST_TRACK_CHILD_STEPS['migrations'][0],conclusion='success',run_attempt=3)]
        self.intent=dict(producingRun=17,producingAttempt=2,partitionIdentity='e'*64,dependencyIdentity='b'*64,materialIdentity='c'*64,
            executionAuthority='a'*40,applicationRevision='a'*40)
        self.success=dict(self.intent,producingAttempt=3)
        self.inventory=dict(total_count=1,artifacts=[dict(name='legend-release-child-intent-'+'b'*64,expired=False)])
        self.source=authority.subprocess.check_output(['git','show','39fc10572cb8ffc7de812d3b50eb26992367c2d1:.github/workflows/legend-fast-track-ui.yml'],text=True)
    def prove(self):
        def get(repo,path,token):
            if '/artifacts?' in path:return self.inventory
            jobs=self.latest if '/attempts/3/' in path else self.first
            return dict(total_count=len(jobs),jobs=jobs)
        with patch.object(authority,'api_get',side_effect=get), \
             patch.object(authority,'_release_history_source',return_value=self.source), \
             patch.object(authority,'release_child_history',side_effect=lambda *args:self.intent if args[-1]=='intent' else self.success):
            return authority.fast_track_completed_sql_unpublished('owner/repo',self.run,self.latest,'fixture')
    def test_completed_sql_preserves_scope_without_claiming_sql_nonentry(self):
        self.assertTrue(self.prove())
    def test_any_earlier_publication_missing_owner_or_changed_original_source_blocks(self):
        self.first=self.latest
        self.apps[0]['steps'][0]['conclusion']='failure';self.assertFalse(self.prove())
        self.apps[0]['steps'][0]['conclusion']='skipped'
        self.latest.remove(self.apps[1]);self.assertFalse(self.prove())
    def test_missing_or_conflicting_sql_success_blocks(self):
        self.success=None;self.assertFalse(self.prove())
        self.success=dict(self.intent,materialIdentity='d'*64);self.assertFalse(self.prove())
    def test_other_partition_or_unbound_success_attempt_blocks(self):
        self.success['partitionIdentity']='f'*64;self.assertFalse(self.prove())
        self.success['partitionIdentity']='e'*64
        self.success['producingAttempt']=2;self.assertFalse(self.prove())

    def test_changed_source_incomplete_artifacts_or_entered_routing_blocks(self):
        original=self.source;self.source+='\n# changed';self.assertFalse(self.prove());self.source=original
        self.inventory['total_count']=2;self.assertFalse(self.prove());self.inventory['total_count']=1
        self.routing['conclusion']='success';self.assertFalse(self.prove())


class UntriggeredRepairTests(unittest.TestCase):
    def prove(self,paths=None,source_changed=False,merged=True):
        revision='a'*40;parent='b'*40
        source=authority.subprocess.check_output(['git','show','39fc10572cb8ffc7de812d3b50eb26992367c2d1:.github/workflows/legend-fast-track-ui.yml'],text=True)
        pull=dict(merged_at='time',merge_commit_sha=revision,base=dict(ref=authority.FAST_TRACK_PR_BASE),head=dict(repo=dict(full_name='owner/repo')))
        with patch.object(authority.subprocess,'check_output',side_effect=[revision+' '+parent+' '+'c'*40,paths or 'scripts/validation-resume.py']), \
             patch.object(authority,'_release_history_source',return_value=source+('changed' if source_changed else '')), \
             patch.object(authority,'api_get',return_value=[pull] if merged else []):
            return authority.fast_track_untriggered_repair_parent('owner/repo',revision,'fixture')
    def test_control_only_untriggered_merge_carries_parent_scope(self):
        self.assertEqual('b'*40,self.prove())
    def test_application_change_unknown_source_or_unmerged_commit_cannot_be_skipped(self):
        self.assertIsNone(self.prove(paths='ParfaitApp/Program.cs'))
        self.assertIsNone(self.prove(source_changed=True))
        with self.assertRaisesRegex(RuntimeError,'MERGE_UNPROVEN'):self.prove(merged=False)
    def test_canonical_trigger_watches_release_owners(self):
        text=Path('.github/workflows/legend-fast-track-ui.yml').read_text().split('permissions:')[0]
        for path in ('validation-resume.py','deploy-approved-app.py','migration-probe-package.py','release-migration.py','release-router.py','release-operation-evidence.py'):
            self.assertIn('      - scripts/'+path+'\n',text)


class RouterPrewriteRecoveryTests(unittest.TestCase):
    def setUp(self):
        self.head='3017d7e7c5527b1f34dc3b120a7d166b90c2a053'
        self.run=dict(id=17,head_sha=self.head,head_branch=authority.FAST_TRACK_PR_BASE,event='push',
            path='.github/workflows/'+authority.FAST_TRACK_RELEASE_WORKFLOW,
            head_repository=dict(full_name='owner/repo'),status='completed',conclusion='failure',run_attempt=2)
        self.job=dict(id=9,name=authority.FAST_TRACK_CHILD_STEPS['routing-cloudflare'][0],status='completed',conclusion='failure',steps=[
            dict(name=authority.FAST_TRACK_CHILD_STEPS['routing-cloudflare'][1],status='completed',conclusion='failure',
                started_at='2026-10-11T18:00:00Z',completed_at='2026-10-11T18:01:00Z')])
        self.log=('2026-10-11T17:59:00Z [command]/usr/bin/git log -1 --format=%H\n'
            +'2026-10-11T17:59:01Z '+self.head+'\n'
            +'2026-10-11T18:00:30Z Traceback (most recent call last):\n'
            +'2026-10-11T18:00:30Z   File "release-router.py", line 122, in <module>\n'
            +'2026-10-11T18:00:30Z     fast_track()\n'
            +'2026-10-11T18:00:30Z   File "release-router.py", line 98, in fast_track\n'
            +'2026-10-11T18:00:30Z     company_render_ready(authority)\n'
            +'2026-10-11T18:00:30Z   File "release-router.py", line 68, in company_render_ready\n'
            +'2026-10-11T18:00:30Z urllib.error.HTTPError: HTTP Error 500: Internal Server Error\n')
    def prove(self):
        with patch.object(authority,'api_get',return_value=dict(total_count=0,artifacts=[])), \
             patch.object(authority,'_release_job_log',return_value=self.log):
            return authority.attested_fast_track_router_prewrite('owner/repo',self.run,[self.job],'fixture')
    def test_original_renderer_failure_proves_no_router_write(self):
        self.assertTrue(self.prove())
    def test_new_workflow_renderer_timeout_and_unavailable_precede_intent(self):
        head='a9ced61b188dcad851fd9d1062ce8beaa77f135d'
        self.run['head_sha']=head
        original=self.log.replace(self.head,head)
        for marker in ('TimeoutError: The read operation timed out',
                       'urllib.error.HTTPError: HTTP Error 503: Service Unavailable'):
            self.log=original.replace('urllib.error.HTTPError: HTTP Error 500: Internal Server Error',marker)
            self.assertTrue(self.prove())
            self.log=self.log.replace('line 68, in company_render_ready','line 109, in publish')
            self.assertFalse(self.prove())
    def admission_failure(self):
        head='a9ced61b188dcad851fd9d1062ce8beaa77f135d'
        self.run['head_sha']=head
        self.log=self.log.split('2026-10-11T18:00:30Z Traceback')[0].replace(self.head,head)
        lines=['Traceback (most recent call last):',
            '  File "release-router.py", line 122, in <module>',
            '    fast_track()',
            '  File "release-router.py", line 111, in fast_track',
            '    result = reconcile_tagged_router(journal, observe, publish)',
            '  File "release-router.py", line 40, in reconcile_tagged_router',
            "    journal.before_mutation({'providerVersion': version or 'absent'})",
            '  File "release-operation-evidence.py", line 262, in before_mutation',
            '    self.authority.release_child_first_write_proven(self.repository, self.child,',
            '  File "validation-resume.py", line 1819, in release_child_first_write_proven',
            "    raise rejection('Release child may have written; missing intent cannot authorize replay', run['id'], attempt)",
            'RuntimeError: Release child may have written; missing intent cannot authorize replay']
        self.log+=''.join('2026-10-11T18:00:30Z '+line+'\n' for line in lines)
    def test_exact_admission_denial_precedes_intent_publication(self):
        self.admission_failure(); self.assertTrue(self.prove())
        original=self.log
        for changed in (original.replace('line 262, in before_mutation','line 267, in before_mutation'),
                        original.replace('in release_child_first_write_proven','in publish_record'),
                        original.replace('18:00:30Z   File "validation','17:59:30Z   File "validation'),
                        original+original.split('2026-10-11T18:00:30Z Traceback')[1]):
            self.log=changed; self.assertFalse(self.prove())
        self.log=original
        source=authority._release_history_source
        for changed_path in ('scripts/release-operation-evidence.py','scripts/validation-resume.py'):
            with patch.object(authority,'_release_history_source',side_effect=lambda repo,head,path,token:
                    'changed' if path==changed_path else source(repo,head,path,token)):
                self.assertFalse(self.prove())
    def test_original_legacy_history_denial_precedes_publication(self):
        self.admission_failure()
        old='a9ced61b188dcad851fd9d1062ce8beaa77f135d'
        head='fcb6cee99357d44b6449ebe5e9b9a6e326210743'
        self.run['head_sha']=head
        self.log=self.log.replace(old,head).replace('line 1819,','line 1920,').replace(
            "raise rejection('Release child may have written; missing intent cannot authorize replay', run['id'], attempt)",
            'raise rejection("Release child execution detail unavailable", run_id, attempt)').replace(
            'RuntimeError: Release child may have written; missing intent cannot authorize replay',
            'RuntimeError: Release child execution detail unavailable')
        self.assertTrue(self.prove())
        self.log=self.log.replace('line 1920,','line 1921,')
        self.assertFalse(self.prove())
    def test_incomplete_inventory_or_retained_write_never_proves_nonentry(self):
        self.admission_failure()
        for inventory in (dict(total_count=1,artifacts=[]),
                dict(total_count=1,artifacts=[dict(id=1,name='legend-release-child-intent-'+'a'*64)]),
                dict(total_count=1,artifacts=[dict(id=1,name='legend-release-child-success-'+'a'*64)])):
            with patch.object(authority,'api_get',return_value=inventory),patch.object(authority,'_release_job_log',return_value=self.log):
                self.assertFalse(authority.attested_fast_track_router_prewrite('owner/repo',self.run,[self.job],'fixture'))
    def test_unbound_frames_changed_source_or_other_error_cannot_prove_nonentry(self):
        original=self.log
        for changed in (original.replace('18:00:30Z     company','17:59:30Z     company'),
                original.replace('in company_render_ready','in publish'),original.replace('Error 500','Error 403'),
                original.replace(self.head,'a'*40)):
            self.log=changed;self.assertFalse(self.prove())
        self.log=original
        with patch.object(authority,'_release_history_source',return_value='changed'):self.assertFalse(self.prove())
    def test_scope_preserves_apps_only_when_every_router_attempt_never_wrote(self):
        jobs=[dict(name='Affected App Deploy — '+label,status='completed',conclusion='success')
            for label in ('AgentPortal','ClientApp','Protect','Parfait','Website')]
        jobs += [dict(name=name,status='completed',conclusion='success') for name in
            ('Admit exact validated fast-track source','Reconcile production schema before publication')]
        jobs.append(self.job)
        earlier=dict(self.job,conclusion='skipped')
        def get(repo,path,token):
            rows=[earlier] if '/attempts/1/' in path else [self.job]
            return dict(total_count=len(rows),jobs=rows)
        def attest(repo,run,rows,token):return rows[-1] is self.job
        with patch.object(authority,'api_get',side_effect=get),patch.object(authority,'attested_fast_track_router_prewrite',side_effect=attest):
            self.assertTrue(authority.fast_track_completed_apps_pending_routing('owner/repo',self.run,jobs,'fixture'))
            earlier['conclusion']='success'
            self.assertFalse(authority.fast_track_completed_apps_pending_routing('owner/repo',self.run,jobs,'fixture'))
    def test_selected_package_repair_preserves_completed_apps(self):
        jobs=[dict(name='Affected App Deploy — Protect',status='completed',conclusion='success')]
        jobs += [dict(name=name,status='completed',conclusion='success') for name in
            ('Admit exact validated fast-track source','Reconcile production schema before publication')]
        jobs.append(self.job)
        with patch.object(authority,'api_get',return_value=dict(total_count=len(jobs),jobs=jobs)), \
             patch.object(authority,'attested_fast_track_router_prewrite',return_value=True):
            self.assertTrue(authority.fast_track_completed_apps_pending_routing('owner/repo',self.run,jobs,'fixture'))
            jobs[0]['conclusion']='failure'
            self.assertFalse(authority.fast_track_completed_apps_pending_routing('owner/repo',self.run,jobs,'fixture'))
            jobs[0]['conclusion']='success'; jobs.append(jobs[0])
            self.assertFalse(authority.fast_track_completed_apps_pending_routing('owner/repo',self.run,jobs,'fixture'))
    def test_empty_selected_matrix_preserves_routing_only_remainder(self):
        jobs=[dict(name='Affected App Deploy — ${{ matrix.target }}',status='completed',conclusion='skipped',steps=[])]
        jobs += [dict(name=name,status='completed',conclusion='success') for name in
            ('Admit exact validated fast-track source','Reconcile production schema before publication')]
        jobs.append(self.job)
        with patch.object(authority,'api_get',return_value=dict(total_count=len(jobs),jobs=jobs)), \
             patch.object(authority,'attested_fast_track_router_prewrite',return_value=True):
            self.assertTrue(authority.fast_track_completed_apps_pending_routing('owner/repo',self.run,jobs,'fixture'))
            jobs[0]['steps']=[dict(name='unexpected',conclusion='success')]
            self.assertFalse(authority.fast_track_completed_apps_pending_routing('owner/repo',self.run,jobs,'fixture'))
    def test_routing_only_resume_requires_explicit_empty_app_selection(self):
        source=Path('.github/workflows/legend-fast-track-ui.yml').read_text().split('  routing-cloudflare:')[1]
        self.assertIn("needs.select-targets.outputs.selected == 'false' && needs.deploy-ui.result == 'skipped'",source)
    def test_empty_routing_scope_requires_ready_database_and_fast_track(self):
        before=dict(ready=True,mutationActivity='settled',historicalExecution='outcome-unknown',historicalEvidenceIdentity='f'*64)
        env=dict(GITHUB_REPOSITORY='owner/repo',RELEASE_CANDIDATE_SHA='a'*40,RELEASE_SHA='b'*40,
                 SELECTED_TARGETS='[]',GH_TOKEN='fixture',GITHUB_REF='refs/heads/'+authority.FAST_TRACK_PR_BASE)
        with patch.dict(os.environ,env),patch.object(migration,'release_authority',return_value=authority):
            result=migration.production_readiness(before)
            self.assertEqual([],result['targets'])
            self.assertFalse(result['resolution']['sqlExecutionAuthorized'])
            with self.assertRaises(ValueError):migration.production_readiness(dict(before,ready=False))
            with patch.dict(os.environ,GITHUB_REF='refs/heads/'+authority.TRUSTED_PR_BASE):
                with self.assertRaises(ValueError):migration.production_readiness(before)
            with self.assertRaisesRegex(RuntimeError,'mutation-activity'):
                migration.production_readiness(dict(before,mutationActivity='active'))

class LegacyRouterHistoryTests(unittest.TestCase):
    def source(self, revision):
        return authority.git_show_file(revision,'.github/workflows/'+authority.DIRECT_RELEASE_WORKFLOW)
    def test_skipped_original_publishers_are_recognized_but_observers_are_not(self):
        step=dict(name='Deploy shared Cloudflare business website router',status='completed',conclusion='skipped')
        for revision in ('d71f45d35976e24f358a4d68ef2f1e90e6422b1b','d406e911417e0e9348ddebfb7e24117bb5dd22ca',
                         'ea4ec1a0126947f4d7e19a29f5a225484a1547ef','ce0e812ad2e399ae99021de72964b842a87dc56f',
                         'a31e7ca5c1f6537ec5b55a5b1f655ac18d592739','b4a62141b57fb1e2c03e927fb25972dc76ef7006',
                         '1322a49e84d861980a971b549fc9237c4f2ffc6b','f2b426791e6820a94331f5e72f9a042cd360d2af',
                         '74df7397d71ce695f6e6033c9a03682a96d83922'):
            source=self.source(revision)
            self.assertTrue(authority.legacy_router_step_never_entered(source,step))
            self.assertFalse(authority.legacy_router_step_never_entered(source.replace('npx wrangler deploy','npx unknown deploy'),step))
            for conclusion in ('success','failure','cancelled',None):
                self.assertFalse(authority.legacy_router_step_never_entered(source,dict(step,conclusion=conclusion)))
        for revision in ('2412d6c2d9a6a973b3e99e5c542c6f43d35eb776','289924832b5c2f8d32e4081890a2ac07709556b1'):
            self.assertFalse(authority.legacy_router_step_never_entered(self.source(revision),step))
    def completed(self):
        head='f2b426791e6820a94331f5e72f9a042cd360d2af'
        run=dict(status='completed',head_sha=head)
        job=dict(id=7,status='completed')
        step=dict(name='Deploy shared Cloudflare business website router',status='completed',conclusion='success',
                  started_at='2026-09-26T01:02:00Z',completed_at='2026-09-26T01:03:00Z')
        lines=['[command]/usr/bin/git log -1 --format=%H',head,
               'Uploaded legend-business-website-router (1.41 sec)',
               'Deployed legend-business-website-router triggers (1.43 sec)',
               'Current Version ID: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee']
        lines += [host+' 1 200 1025' for host in authority.ROUTING_PASS_THROUGH_HOSTS]
        lines += ['Cloudflare website router deployed and LEGEND-owned host pass-through verified.']
        log=''.join('2026-09-26T01:02:30Z '+line+'\n' for line in lines)
        return run,job,step,self.source(head),log
    def prove(self,run,job,step,source,log,generation='a'*64):
        with patch.object(authority,'_release_job_log',return_value=log), \
             patch.object(authority,'direct_child_operation_identity',return_value=generation):
            return authority.legacy_router_completed_generation('owner/repo',run,job,step,source,'fixture','b'*64,'c'*64)
    def test_completed_older_generation_requires_bound_provider_and_host_proof(self):
        values=self.completed();self.assertTrue(self.prove(*values))
        self.assertFalse(self.prove(*values,generation='b'*64))
        run,job,step,source,log=values
        for changed in (log.replace(run['head_sha'],'e'*40),log.replace('Current Version ID:','Missing version:'),
                        log.replace('portal.mylegnd.com 1 200','portal.mylegnd.com 1 503'),
                        log.replace('01:02:30Z Cloudflare','01:04:30Z Cloudflare'),
                        log+'2026-09-26T01:02:31Z Restored previous Cloudflare Worker version.\n'):
            self.assertFalse(self.prove(run,job,step,source,changed))
        self.assertFalse(self.prove(run,job,step,source+'changed',log))
        for conclusion in ('skipped','failure','cancelled'):
            self.assertFalse(self.prove(run,job,dict(step,conclusion=conclusion),source,log))


class PublishedCompilerBrandingTests(unittest.TestCase):
    def test_packaged_content_module_resolves_canonical_registry(self):
        import shutil,xml.etree.ElementTree as ET,subprocess
        project=ET.parse('Protect-Website/ProtectWebsite.csproj').getroot()
        entries=[e for e in project.iter('Content') if e.get('Include')=='../SHARED/Branding/legend-brand.json']
        self.assertEqual(1,len(entries));entry=entries[0]
        self.assertEqual('PreserveNewest',entry.get('CopyToPublishDirectory'))
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);module=root/'WebsiteCompiler/src/content.mjs';module.parent.mkdir(parents=True)
            shutil.copyfile('Legend-Website/src/content.mjs',module)
            destination=root/entry.get('Link');destination.parent.mkdir(parents=True)
            shutil.copyfile('SHARED/Branding/legend-brand.json',destination)
            result=subprocess.run(['node','--input-type=module','-e',
                'const m=await import('+json.dumps(module.as_uri())+');if(!m.pages)process.exit(1);'],capture_output=True,text=True)
            self.assertEqual(0,result.returncode,result.stderr)


class FastTrackNoSqlInputInventoryTests(unittest.TestCase):
    def test_no_sql_regression_is_control_only_not_unmapped_or_runtime_owned(self):
        path='scripts/test-fast-track-ready-no-sql.py'
        self.assertTrue(authority.release_control_only_path(path))
        scope=authority.fast_track_scope([path])
        self.assertEqual([],scope['impacts'][path])
        self.assertFalse(scope['selected'])
        self.assertFalse(scope['commerce'])
        self.assertFalse(scope['schema'])


if __name__ == '__main__':unittest.main()
