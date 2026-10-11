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
    def baseline(self, entered=False, attempt=1, duplicate=False):
        before='a'*40;head='b'*40;original='c'*40
        prior=dict(id=7,run_attempt=attempt,head_sha=before,head_branch=authority.FAST_TRACK_PR_BASE,event='push',
            path='.github/workflows/'+authority.FAST_TRACK_RELEASE_WORKFLOW,
            head_repository={'full_name':'owner/repo'},status='completed',conclusion='failure')
        jobs=[dict(name='Admit exact validated fast-track source',conclusion='failure'),
              dict(name='Reconcile production schema before publication',conclusion='failure' if entered else 'skipped'),
              dict(name='Affected App Deploy — ${{ matrix.target }}',conclusion='skipped'),
              dict(name='Publish verified website routing',conclusion='skipped')]
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


class RecoveryWindowTests(unittest.TestCase):
    def test_exact_database_with_managed_restore_window_is_read_only_proof(self):
        from datetime import datetime,timezone
        now=datetime(2026,10,11,tzinfo=timezone.utc)
        calls=[]
        def read(*args):
            calls.append(args)
            if args[:3]==('sql','server','list'):return [dict(name='server')]
            if args[:3]==('sql','db','list'):return [dict(name='db',status='Online',earliestRestoreDate='2026-10-01T00:00:00Z')]
            return dict(retentionDays=7)
        identity=hashlib.sha256(b'server/db').hexdigest()
        with patch.dict(os.environ,RELEASE_RESOURCE_GROUP='group'):
            proof=migration.production_recovery(identity,read=read,now=now)
            self.assertFalse(proof['restoreExecuted']);self.assertFalse(proof['authorizesMutation'])
            self.assertTrue(proof['restoreWindowVerified'])
            with self.assertRaisesRegex(RuntimeError,'IDENTITY'):
                migration.production_recovery('a'*64,read=read,now=now)
        self.assertTrue(all('list' in c or 'show' in c for c in calls))


if __name__ == '__main__':unittest.main()
