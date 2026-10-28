#!/usr/bin/env python3
"""Adversarial fast-track admission, immutable recovery and provider readback."""
import hashlib
import importlib.util
import os
import json
import tempfile
import subprocess
import textwrap
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
cloudflare = load('cloudflare-routing-authority')
migration = load('release-migration')
deploy = load('deploy-approved-app')


class UniversalFastTrackAdmissionTests(unittest.TestCase):
    def test_training_shared_sources_select_every_managed_consumer_without_widening_unknown_paths(self):
        paths = ['Domain/Billing/BillingEnums.cs', 'Domain/Billing/ClientSubscriptionScope.cs', 'Domain/Billing/TrainingPlanTerms.cs', 'Domain/Entities/ClientSubscription.cs', 'Infrastructure/Billing/ClientBillingNotificationService.cs', 'Infrastructure/Billing/ClientPaymentMethodService.cs', 'Infrastructure/Billing/ClientSubscriptionActivationPolicyOptions.cs', 'Infrastructure/Data/MasterAppSqliteSchemaBootstrapper.cs', 'Infrastructure/Households/HouseholdReconciliationService.cs', 'Infrastructure/Identity/ClientEntraProvisioningRecoveryHostedService.cs', 'Infrastructure/Messaging/TranslationEntitlementAuthority.cs']
        managed = {key for key, row in authority.RELEASE_TARGETS.items() if not row['static']}
        for path in paths:
            with self.subTest(path=path):
                scope = authority.fast_track_scope([path])
                self.assertEqual(managed, {row['key'] for row in scope['matrix']['include']})
                self.assertTrue(scope['commerce'])
        with self.assertRaisesRegex(ValueError, 'Unmapped fast-track source path'):
            authority.fast_track_scope(['Domain/Billing/UnreviewedTrainingSibling.cs'])

    def invoke(self, paths, admission_error=None, migration_error=None):
        run = {'id': 123, 'head_sha': 'b' * 40, 'run_attempt': 1}
        with patch.dict(os.environ, {'BEFORE': 'a' * 40}), \
             patch.object(deploy._RELEASE_AUTHORITY, 'assert_fast_track_release_execution', return_value=run), \
             patch.object(deploy.subprocess, 'check_output', return_value=paths), \
             patch.object(deploy._RELEASE_AUTHORITY, 'attested_website_only_recovery_scope', return_value=None), \
             patch.object(deploy._RELEASE_AUTHORITY, 'fast_track_source_admission', side_effect=admission_error) as admission, \
             patch.object(deploy, 'fast_track_migration_gate', side_effect=migration_error) as schema, \
             patch.object(deploy, 'verify_package', return_value='digest'), \
             patch.object(deploy, 'target_azure', side_effect=RuntimeError('PROVIDER_BOUNDARY')) as azure:
            key = 'protect' if paths.startswith('Infrastructure/') else 'website'
            with self.assertRaisesRegex(Exception, admission_error.args[0] if admission_error else migration_error.args[0] if migration_error else 'PROVIDER_BOUNDARY'):
                deploy.fast_track_deploy(key, Path('/synthetic-package'))
            admission.assert_called_once_with()
            if admission_error:
                schema.assert_not_called(); azure.assert_not_called()
            elif paths.startswith('Infrastructure/'):
                schema.assert_called_once_with(run)
                if migration_error: azure.assert_not_called()
            else:
                schema.assert_not_called(); azure.assert_called_once()

    def test_ui_only_source_denial_precedes_any_provider_access(self):
        self.invoke('Legend-Design/legend-web-foundation.css', ValueError('FAST_TRACK_SOURCE_DENIED'))

    def test_ui_only_accepted_source_does_not_invent_sql_gate(self):
        self.invoke('Legend-Design/legend-web-foundation.css')

    def test_backend_still_requires_successful_schema_after_source(self):
        self.invoke('Infrastructure/Commerce/Payments/MerchantCheckoutService.cs', migration_error=ValueError('FAST_TRACK_SCHEMA_RELEASE_GATE_UNPROVEN'))


    def test_workflow_admits_all_publications_and_bounds_publisher(self):
        workflow = Path('.github/workflows/legend-fast-track-ui.yml').read_text()
        gates = workflow.split('  commerce-gates:', 1)[1].split('  migrations:', 1)[0]
        self.assertIn("needs.select-targets.outputs.selected == 'true' || needs.select-targets.outputs.routing == 'true'", gates)
        publisher = workflow.split('  deploy-ui:', 1)[1].split('  routing-cloudflare:', 1)[0]
        condition = next(line for line in publisher.splitlines() if line.strip().startswith('if:'))
        self.assertIn("needs.commerce-gates.result == 'success'", condition)
        self.assertNotIn("needs.commerce-gates.result == 'skipped'", condition)
        self.assertIn('timeout-minutes: 30', publisher)
        self.assertIn('group: legend-approved-release', workflow)
        self.assertEqual(420, deploy.PUBLICATION_RECONCILE_TIMEOUT_SECONDS)

    def test_every_control_promotion_request_schedules_canonical_selector(self):
        import fnmatch, re
        workflow = Path('.github/workflows/legend-fast-track-ui.yml').read_text()
        push = workflow.split('  push:', 1)[1].split('\npermissions:', 1)[0]
        triggers = re.findall(r'^      - (.+)$', push.split('    paths:', 1)[1], re.M)
        policy = load('release_policy')
        # Every accepted promotion changes this existing request, even when its
        # control files themselves do not match a publication trigger.
        self.assertIn(policy.REQUEST, triggers)
        for control in policy.TRUSTED_TOOL_PATHS:
            changed = [control, policy.REQUEST]
            self.assertTrue(any(fnmatch.fnmatchcase(path, pattern)
                for path in changed for pattern in triggers), control)
        self.assertFalse(any(fnmatch.fnmatchcase('Docs/unrelated.md', pattern)
            for pattern in triggers))
        self.assertEqual(workflow.count('  workflow_dispatch:'), 1)
        jobs=authority._job_blocks(workflow)
        self.assertIn("if: github.event_name == 'push'", jobs['select-targets'])
        recovery=jobs['recover-fast-track']
        self.assertIn("if: github.event_name == 'workflow_dispatch'", recovery)
        self.assertIn('--prepare-recovery-only', recovery)
        self.assertIn('--recover-fast-track', recovery)
        self.assertNotIn('dotnet publish', recovery)
        self.assertNotIn('npm run build', recovery)
        self.assertIn('group: legend-approved-release', workflow)
        self.assertIn('cancel-in-progress: false', workflow)

    def test_existing_isolated_validation_covers_every_fast_track_trigger(self):
        import fnmatch, re
        def paths(file):
            text = Path('.github/workflows', file).read_text().split('    paths:', 1)[1]
            text = text.split('  workflow_dispatch:', 1)[0].split('\npermissions:', 1)[0]
            return re.findall(r'^      - (.+)$', text, re.M)
        validation = paths('parfait-isolated-validation.yml')
        for trigger in paths('legend-fast-track-ui.yml'):
            with self.subTest(trigger=trigger):
                self.assertTrue(any(fnmatch.fnmatchcase(trigger, covered) for covered in validation), trigger)


class ObserverCompatibilityTests(unittest.TestCase):
    def compatible(self, changed=False, runtime=False, body=False):
        def source(ref, path):
            suffix = 'return 2' if body and ref == 'new' else 'return 1'
            return '\n'.join('def '+name+'():\n    '+suffix for name in ['rehearse','rehearse_fast_track','isolated_sql','trusted_fast_track_run','fast_track_observation_source','verified_fast_track_observation','fast_track_baseline','manifest'])
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


class RouterOnlyAdmissionTests(unittest.TestCase):
    def test_canonical_router_control_change_requires_gates_without_app_deploy(self):
        scope=authority.fast_track_scope(['scripts/release-router.py'])
        self.assertEqual(scope['matrix']['include'],[])
        self.assertFalse(scope['selected'])
        self.assertFalse(scope['schema'])
        self.assertTrue(scope['routing'])
        self.assertTrue(scope['commerce'])



class HistoricManualProxyReadbackTests(unittest.TestCase):
    def test_exact_source_pinned_manual_proxy_resolution_requires_all_evidence(self):
        prior = {'id':38276359453,'head_sha':'1f08cd5a2518a4d206ecf4bccdad4024b7cba26e',
                 'status':'completed','conclusion':'failure','run_attempt':2}
        outcomes = {'Select exact affected applications':'success',
                    'Admit exact validated fast-track source':'success',
                    'Reconcile production schema before publication':'success',
                    'Affected App Deploy — ${{ matrix.target }}':'skipped',
                    'Publish verified website routing':'failure'}
        jobs=[{'name':name,'conclusion':outcome,'status':'completed'} for name,outcome in outcomes.items()]
        history={'total_count':1,'jobs':[{'id':114904843387,'name':'Publish verified website routing',
                                         'conclusion':'failure'}]}
        diagnostic={'total_count':1,'jobs':[{'id':114910241289,
             'name':'Read live publication routes and proxied DNS without writes',
             'status':'completed','conclusion':'success'}]}
        def read_api(repo,path,token):
            return history if '/38276359453/' in path else diagnostic
        first='FAST_TRACK_ROUTER:preserved\nFAST_TRACK_DNS_PROXY_RECONCILIATION:apex_and_www_only\nreturned HTTP 403\nWebsite DNS proxy requires DNS Edit authority or recovery: mylegnd.com'
        second='FAST_TRACK_RELEASE_HEAD_CHANGED'
        records=[{'type':'A','name':'mylegnd.com','proxied':True},
                 {'type':'A','name':'mylegnd.com','proxied':True},
                 {'type':'CNAME','name':'www.mylegnd.com','proxied':True}]
        def diagnostic_log(records):
            lines=[]
            for host in ('mylegnd.com','www.mylegnd.com'):
                entry={'url':'https://'+host+'/','status':200,'publicationId':'v1',
                       'containsPublishedDocument':True,'containsLoadingMarker':False,
                       'cfRayPresent':True}
                lines.append('PUBLIC_ROUTE_DIAGNOSTIC='+json.dumps(entry))
            lines.append('CLOUDFLARE_PUBLIC_DNS='+json.dumps(records))
            lines.append('CLOUDFLARE_WORKER_ROUTES='+json.dumps([
                {'pattern':'*/*','script':'legend-business-website-router'}]))
            return '\n'.join(lines)
        logs={114904843387:first,114926219880:second,114910241289:diagnostic_log(records)}
        with patch.object(authority,'is_fast_track_producer',return_value=True), \
             patch.object(authority,'api_get',side_effect=read_api), \
             patch.object(authority,'_release_job_log',side_effect=lambda repo,id,token:logs[id]):
            self.assertTrue(authority.attested_fast_track_manual_dns_proxy_completion('owner/repo',prior,jobs,'token'))
            records[0]['proxied']=False
            logs[114910241289]=diagnostic_log(records)
            self.assertFalse(authority.attested_fast_track_manual_dns_proxy_completion('owner/repo',prior,jobs,'token'))
            records[0]['proxied']=True
            logs[114910241289]=diagnostic_log(records)
            altered=dict(prior,head_sha='f'*40)
            self.assertFalse(authority.attested_fast_track_manual_dns_proxy_completion('owner/repo',altered,jobs,'token'))



class RoutineSqlPollingImpactTests(unittest.TestCase):
    def test_exact_shared_polling_sources_require_managed_apps_and_backend_admission(self):
        paths = ['Infrastructure/Diagnostics/RoutineSqlPolling.cs',
                 'Infrastructure/Messaging/LegendCallSignalDeliveryHostedService.cs',
                 'Infrastructure/Notifications/ApplePushDeliveryHostedService.cs',
                 'Infrastructure/Notifications/FirebasePushDeliveryHostedService.cs']
        for path in paths:
            with self.subTest(path=path):
                scope = authority.fast_track_scope([path])
                self.assertEqual({r['key'] for r in scope['matrix']['include']},
                                 {'portal', 'client', 'protect', 'parfait'})
                self.assertTrue(scope['commerce'])
                self.assertFalse(scope['schema'])
                self.assertFalse(scope['routing'])
        for path in ['Infrastructure/Diagnostics/Unknown.cs', 'Infrastructure/Notifications/Unknown.cs',
                     'Infrastructure/Messaging/Unknown.cs']:
            with self.subTest(path=path), self.assertRaisesRegex(ValueError, 'Unmapped'):
                authority.fast_track_scope([path])


class FoundationCssImpactTests(unittest.TestCase):
    def test_shared_foundation_targets_only_actual_public_and_authenticated_consumers(self):
        scope=authority.fast_track_scope(['Legend-Design/legend-web-foundation.css'])
        self.assertEqual(set(scope['impacts']['Legend-Design/legend-web-foundation.css']),
                         {'website','protect','portal','client'})
        self.assertEqual({row['key'] for row in scope['matrix']['include']},
                         {'website','protect','portal','client'})
        self.assertNotIn('parfait', scope['impacts']['Legend-Design/legend-web-foundation.css'])



class WebsiteOriginalNonentryRepairTests(unittest.TestCase):
    def test_website_only_scope_never_selects_protect(self):
        selected=authority.website_only_recovery_scope(authority.fast_track_scope(
            ['scripts/deploy-approved-app.py','scripts/validation-resume.py',
             'scripts/test-fast-track-release.py','.github/workflows/legend-fast-track-ui.yml']))
        self.assertEqual([row['key'] for row in selected['matrix']['include']],['website'])
        self.assertFalse(selected['commerce'])
        self.assertFalse(selected['routing'])
        self.assertFalse(selected['schema'])

    def test_tracked_website_submit_does_not_use_untracked_async_cli(self):
        azure=deploy.Azure('masterapp-website',Path('/tmp/exact-package.zip'),
                           'https://website.example/provenance','a'*40,static=True)
        class Result:
            returncode=0
            stderr=''
            def __init__(self, stdout):
                self.stdout=stdout
        correlation={'kind':'azure_onedeploy_deployment_tag_v1','value':'c'*64}
        with patch.object(deploy.subprocess,'run',side_effect=[
                Result('    --deploymentTag : Linux deployment identifier.\n'),
                Result(json.dumps({'name':'masterapp-website','reserved':True,'kind':'app,linux'})),
                Result('{}')]) as runner:
            azure.prepare_submission()
            self.assertTrue(azure.submit(correlation))
        self.assertEqual(runner.call_count,3)
        command=runner.call_args.args[0]
        self.assertEqual(command[command.index('--async')+1],'false')
        self.assertEqual(command[command.index('--track-status')+1],'true')
        self.assertEqual(command[command.index('--enable-kudu-warmup')+1],'true')
        self.assertEqual(command[command.index('--deploymentTag')+1],correlation['value'])

    def test_prior_unverified_deployment_forces_fresh_noentry_preflight_failure(self):
        azure=deploy.Azure('masterapp-website',Path('/tmp/exact-package.zip'),
                           'https://website.example/provenance','a'*40,static=True)
        from datetime import datetime
        old=datetime.fromisoformat(authority.WEBSITE_MISSING_UPLOAD_AFTER)
        self.assertEqual(old.isoformat(),'2026-10-12T19:38:11+00:00')
        self.assertEqual(authority.WEBSITE_MISSING_UPLOAD_REVISION,
                         '631258bdcdd8c42a64570baef0db49b05e1ea829')
        # A new provider row is NEVER negative evidence, even if failed.
        row={'id':'new','status':3,'received_time':'2026-10-12T20:00:00Z'}
        received=datetime.fromisoformat(row['received_time'].replace('Z','+00:00'))
        self.assertGreaterEqual(received,old)

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




class WebsiteDnsProxyTests(unittest.TestCase):
    def test_existing_proxy_is_verified_without_writes_and_mx_is_not_touched(self):
        records={
            'mylegnd.com':[
                {'id':'a','name':'mylegnd.com','type':'A','proxied':True},
                {'id':'b','name':'mylegnd.com','type':'A','proxied':True},
                {'id':'mail','name':'mylegnd.com','type':'MX','proxied':False}],
            'www.mylegnd.com':[{'id':'c','name':'www.mylegnd.com','type':'CNAME','proxied':True}]}
        def api(method,url,token,body=None):
            self.assertEqual(method,'GET')
            if '?' in url:
                host='www.mylegnd.com' if 'www.mylegnd.com' in url else 'mylegnd.com'
                return {'result':records[host],'result_info':{'total_pages':1}}
            key=url.split('/')[-1]
            return {'result':next(r for rows in records.values() for r in rows if r['id']==key)}
        with patch.object(cloudflare,'api_request',side_effect=api):
            cloudflare.reconcile_public_web_proxy('token','zone')

    def test_only_unproxied_company_address_records_change_once(self):
        records={'mylegnd.com':[{'id':'a','name':'mylegnd.com','type':'A','proxied':False}],
                 'www.mylegnd.com':[{'id':'c','name':'www.mylegnd.com','type':'CNAME','proxied':False}]}
        writes=[]
        def api(method,url,token,body=None):
            if '?' in url:
                host='www.mylegnd.com' if 'www.mylegnd.com' in url else 'mylegnd.com'
                return {'result':records[host],'result_info':{'total_pages':1}}
            record=next(r for rows in records.values() for r in rows if r['id']==url.split('/')[-1])
            if method=='PATCH':
                self.assertEqual(body,{'proxied':True})
                writes.append(record['id']);record['proxied']=True
            return {'result':record}
        with patch.object(cloudflare,'api_request',side_effect=api):
            cloudflare.reconcile_public_web_proxy('token','zone')
            cloudflare.reconcile_public_web_proxy('token','zone')
        self.assertEqual(writes,['a','c'])

    def test_mixed_www_addresses_stop_before_any_dns_edit(self):
        records={'mylegnd.com':[{'id':'a','name':'mylegnd.com','type':'A','proxied':False}],
                 'www.mylegnd.com':[{'id':'c','name':'www.mylegnd.com','type':'CNAME','proxied':False},
                                     {'id':'d','name':'www.mylegnd.com','type':'AAAA','proxied':False}]}
        writes=[]
        def api(method,url,token,body=None):
            if method!='GET':writes.append(url)
            host='www.mylegnd.com' if 'www.mylegnd.com' in url else 'mylegnd.com'
            return {'result':records[host],'result_info':{'total_pages':1}}
        with patch.object(cloudflare,'api_request',side_effect=api):
            with self.assertRaisesRegex(cloudflare.CloudflareError,'inventory differs'):
                cloudflare.reconcile_public_web_proxy('token','zone')
        self.assertEqual(writes,[])


class CompanyRendererAdmissionTests(unittest.TestCase):
    def test_http_success_without_published_html_is_not_renderer_readiness(self):
        from unittest.mock import MagicMock
        current=(b'<html><body><main><h1>Published now</h1></main>'
                 b'<script id="legend-cms-published-document">{}</script></body></html>')
        for body, headers, succeeds in [
            (b'<main>stale static page</main>',{'Content-Type':'text/html'},False),
            (current,{'Content-Type':'text/html'},False),  # No immutable publication identity.
            (current.replace(b'<html>',b'<html data-legend-canonical-pending="true">'),
             {'Content-Type':'text/html','X-Legend-Publication-Id':'version-1'},False),
            (current,{'Content-Type':'text/html','X-Legend-Publication-Id':'version-1'},True),
        ]:
            response=MagicMock();response.__enter__.return_value=response
            response.status=200;response.headers=headers;response.read.return_value=body
            with self.subTest(succeeds=succeeds,headers=headers,body=body[:80]), \
                 patch.object(router.urllib.request,'urlopen',return_value=response):
                if succeeds: router.company_render_ready(authority)
                else:
                    with self.assertRaises(RuntimeError): router.company_render_ready(authority)


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


class FastTrackInventoryMergeTests(unittest.TestCase):
    def setUp(self):
        self.merge, self.base, self.head, self.tree = [c * 40 for c in 'abcd']
        self.branch = self.merge
        self.local = self.merge
        self.parents = [self.base, self.head]
        self.local_parents = [self.merge, self.base, self.head]
        self.trees = [self.tree, self.tree]
        self.ancestor = True
        self.pull = dict(number=691, merged_at='2026-10-26T00:00:00Z', merge_commit_sha=self.merge,
            base=dict(ref=authority.FAST_TRACK_PR_BASE, repo=dict(full_name='owner/repo')),
            head=dict(sha=self.head, ref='isolation/task', repo=dict(full_name='owner/repo')))
        self.pulls = [self.pull]
        self.rows = [dict(id=i, run_attempt=1, path='.github/workflows/' + name,
            event='pull_request', head_sha=self.head, head_branch='isolation/task',
            head_repository=dict(full_name='owner/repo'), status='completed', conclusion='success')
            for i, name in enumerate(('approved-release-security-validation.yml',
                'masterapp-platform-architecture-validation.yml', 'parfait-isolated-validation.yml'), 1)]
        self.count = 3
        self.env = dict(GITHUB_SHA=self.merge, GITHUB_REPOSITORY='owner/repo',
            GITHUB_REF='refs/heads/' + authority.FAST_TRACK_PR_BASE, GITHUB_EVENT_NAME='workflow_dispatch',
            GITHUB_WORKFLOW_REF='owner/repo/.github/workflows/parfait-cutover-live-readonly.yml@refs/heads/' + authority.FAST_TRACK_PR_BASE)
        self.branch_reads = 0
        self.move_at_end = False

    def api(self, repo, path, token):
        self.assertEqual(('owner/repo', 'fixture'), (repo, token))
        if path.startswith('git/ref/'):
            self.branch_reads += 1
            return dict(object=dict(sha='f'*40 if self.move_at_end and self.branch_reads > 1 else self.branch))
        if path == 'git/commits/' + self.merge:
            return dict(sha=self.merge, tree=dict(sha=self.tree), parents=[dict(sha=p) for p in self.parents])
        if path.startswith('commits/'):
            return self.pulls
        if path.startswith('actions/runs?'):
            return dict(total_count=self.count, workflow_runs=self.rows)
        raise AssertionError(path)

    def git(self, command, **kwargs):
        if command == ['git', 'rev-parse', 'HEAD']: return self.local
        if command == ['git', 'rev-list', '--parents', '-n', '1', self.merge]: return ' '.join(self.local_parents)
        if command == ['git', 'rev-parse', self.merge+'^{tree}', self.head+'^{tree}']: return '\n'.join(self.trees)
        raise AssertionError(command)

    def invoke(self, receipt_error=None):
        with patch.dict(os.environ, self.env, clear=True), \
             patch.object(authority, 'api_get', side_effect=self.api), \
             patch.object(authority.subprocess, 'check_output', side_effect=self.git), \
             patch.object(authority.subprocess, 'run', side_effect=lambda args, **kw:
                SimpleNamespace(returncode=0 if args[1]=='diff' or self.ancestor else 1)), \
             patch.object(authority, 'fast_track_validation_receipt', side_effect=receipt_error, return_value=81) as receipts:
            result = authority.fast_track_inventory_merge_proof('owner/repo', self.merge, 'fixture')
            self.assertEqual(3, receipts.call_count)
            self.assertEqual({self.head}, {call.args[3] for call in receipts.call_args_list})
            return result

    def test_exact_current_two_parent_merge_and_three_receipts(self):
        result = self.invoke()
        self.assertEqual((self.base, self.head, self.tree), (result['baseline'], result['candidate'], result['tree']))
        self.assertFalse(result['authorizesMutation'])

    def test_execution_identity_and_manual_replay_rejected(self):
        for field in self.env:
            with self.subTest(field=field):
                old=self.env[field]; self.env[field]='other'
                with self.assertRaisesRegex(RuntimeError, 'EXECUTION_CHANGED'): self.invoke()
                self.env[field]=old
        self.local='e'*40
        with self.assertRaisesRegex(RuntimeError, 'EXECUTION_CHANGED'): self.invoke()

    def test_branch_movement_before_or_during_receipt_verification(self):
        self.branch='e'*40
        with self.assertRaisesRegex(RuntimeError, 'HEAD_CHANGED'): self.invoke()
        self.branch=self.merge; self.branch_reads=0; self.move_at_end=True
        with self.assertRaisesRegex(RuntimeError, 'HEAD_CHANGED'): self.invoke()

    def test_missing_extra_reordered_substituted_or_duplicate_parents(self):
        for parents in ([self.base], [self.base,self.head,'e'*40], [self.head,self.base],
                        ['e'*40,self.head], [self.base,self.base]):
            with self.subTest(parents=parents):
                self.parents=parents
                with self.assertRaisesRegex(RuntimeError, 'PARENTS_'): self.invoke()

    def test_changed_tree_or_unrelated_base(self):
        self.trees=[self.tree,'e'*40]
        with self.assertRaisesRegex(RuntimeError, 'TREE_OR_BASE_CHANGED'): self.invoke()
        self.trees=[self.tree,self.tree]; self.ancestor=False
        with self.assertRaisesRegex(RuntimeError, 'TREE_OR_BASE_CHANGED'): self.invoke()

    def test_foreign_base_or_changed_reviewed_source_rejected(self):
        self.pull['base']['repo']['full_name']='foreign/repo'
        with self.assertRaisesRegex(RuntimeError, 'SOURCE_UNPROVEN'): self.invoke()
        self.pull['base']['repo']['full_name']='owner/repo'; self.pull['head']['sha']='e'*40
        with self.assertRaisesRegex(RuntimeError, 'SOURCE_UNPROVEN'): self.invoke()

    def test_incomplete_pr_or_ci_inventory_rejected(self):
        self.pulls=[self.pull]*100
        with self.assertRaisesRegex(RuntimeError, 'PR_INCOMPLETE'): self.invoke()
        self.pulls=[self.pull]; self.count=4
        with self.assertRaisesRegex(RuntimeError, 'INVENTORY_INCOMPLETE'): self.invoke()

    def test_wrong_head_stale_failed_or_forged_ci_receipts_rejected(self):
        self.rows[0]['head_sha']='e'*40
        with self.assertRaisesRegex(RuntimeError, 'REQUIRED_VALIDATION_UNPROVEN'): self.invoke()
        self.rows[0]['head_sha']=self.head
        self.rows.append(dict(self.rows[0],id=99,conclusion='failure')); self.count=4
        with self.assertRaisesRegex(RuntimeError, 'REQUIRED_VALIDATION_UNPROVEN'): self.invoke()
        self.rows.pop(); self.count=3
        with self.assertRaisesRegex(RuntimeError, 'FORGED_RECEIPT'): self.invoke(RuntimeError('FORGED_RECEIPT'))


class FastTrackArchitectureAdmissionTests(unittest.TestCase):
    def admission(self, architecture=True, isolated_event="pull_request"):
        candidate, revision = 'a' * 40, 'b' * 40
        pull = dict(number=580, merged_at='2026-10-13T00:00:00Z', merge_commit_sha=revision,
            base=dict(ref=authority.FAST_TRACK_PR_BASE),
            head=dict(sha=candidate, ref='isolation/task', repo=dict(full_name='owner/repo')))
        workflows = [('approved-release-security-validation.yml', 'pull_request'),
            ('parfait-isolated-validation.yml', isolated_event)]
        if architecture:
            workflows.append(('masterapp-platform-architecture-validation.yml', 'pull_request'))
        rows = [dict(id=i+1, run_attempt=1, path='.github/workflows/'+workflow, event=event,
            head_sha=candidate, head_branch='isolation/task', head_repository=dict(full_name='owner/repo'),
            status='completed', conclusion='success') for i,(workflow,event) in enumerate(workflows)]
        with patch.dict(os.environ, {'GITHUB_REPOSITORY':'owner/repo','GH_TOKEN':'fixture'}), \
             patch.object(authority, 'assert_fast_track_release_execution', return_value=dict(head_sha=revision)), \
             patch.object(authority, 'api_get', side_effect=[[pull], dict(total_count=len(rows),workflow_runs=rows)]), \
             patch.object(authority.subprocess, 'run', return_value=SimpleNamespace(returncode=0)), \
             patch.object(authority, 'fast_track_validation_receipt', return_value=81):
            return authority.fast_track_source_admission()

    def test_all_three_exact_source_workflows_are_mandatory(self):
        self.assertEqual({'approved-release-security-validation.yml', 'parfait-isolated-validation.yml',
            'masterapp-platform-architecture-validation.yml'}, set(self.admission()['validation']))

    def test_security_and_isolated_success_cannot_replace_architecture(self):
        with self.assertRaisesRegex(RuntimeError, 'REQUIRED_VALIDATION_UNPROVEN:masterapp-platform'):
            self.admission(False)

    def test_push_only_isolated_validation_cannot_admit_release(self):
        with self.assertRaisesRegex(RuntimeError, 'REQUIRED_VALIDATION_UNPROVEN:parfait-isolated'):
            self.admission(isolated_event='push')

    def test_architecture_workflow_uses_fast_track_base_without_old_release_jobs(self):
        source = Path('.github/workflows/masterapp-platform-architecture-validation.yml').read_text()
        self.assertIn('      - Legend/fast-track-release', source.split('  workflow_dispatch:',1)[0])
        self.assertIn('--base-branch "${{ github.base_ref', source)
        self.assertIn('--validation-base "${{ github.base_ref', source)
        for gate in ('renderer-tests', 'lifecycle'):
            step = authority.WORKFLOWS['masterapp-platform-architecture-validation.yml']['gates'][gate]['step']
            self.assertIn('      - name: ' + step + '\n', source)
        package = source.split('  validated-release-package-plan:',1)[1].split('    runs-on:',1)[0]
        self.assertIn('if: ${{ false }} # Retired approved-branch producer.', package)
        rehearsal = source.split('  migration-rehearsal-prepare:',1)[1].split('    runs-on:',1)[0]
        self.assertIn('if: ${{ false }} # Retired approved-branch producer.', rehearsal)

    def run_architecture_step(self, step, base, renderer_status=0, preserved=False):
        source = Path('.github/workflows/masterapp-platform-architecture-validation.yml').read_text()
        body = source.split('        id: ' + step + '\n', 1)[1].split('        run: |\n', 1)[1]
        body = body.split('\n      - ', 1)[0]
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'Legend-Website').mkdir()
            log = root / 'calls'
            for name, script in {
                'python3': 'echo "python3 $*" >> "$CALL_LOG"\n'
                    'if [ "$2" = preserved ]; then exit "$PRESERVED_STATUS"; fi\n'
                    'if [ "$1" = - ]; then echo forbidden-live-history >> "$CALL_LOG"; exit 91; fi\n',
                'npm': 'echo "npm $*" >> "$CALL_LOG"\n'
                    'if [ "$2" = test:renderer ]; then exit "$RENDERER_STATUS"; fi\n',
                'git': 'echo "git $*" >> "$CALL_LOG"\nexit 92\n',
                'node': 'echo "node $*" >> "$CALL_LOG"\n'
            }.items():
                path = root / name
                path.write_text('#!/bin/bash\n' + script)
                path.chmod(0o700)
            result = subprocess.run(['/bin/bash', '-e', '-c', textwrap.dedent(body)], cwd=root,
                env=os.environ | dict(PATH=str(root) + ':' + os.environ['PATH'], CALL_LOG=str(log),
                    RUNNER_TEMP=str(root), VALIDATION_BASE=base, RENDERER_STATUS=str(renderer_status),
                    PRESERVED_STATUS='0' if preserved else '1'), capture_output=True, text=True)
            return result.returncode, log.read_text() if log.exists() else ''

    def test_fast_track_renderer_failure_is_not_excused_by_old_branch(self):
        status, calls = self.run_architecture_step('gate_renderer_tests', authority.FAST_TRACK_PR_BASE, 7)
        self.assertEqual(7, status)
        self.assertNotIn('git ', calls)

    def test_fast_track_static_safety_does_not_query_live_legacy_history(self):
        status, calls = self.run_architecture_step('gate_lifecycle', authority.FAST_TRACK_PR_BASE)
        self.assertEqual(0, status, calls)
        self.assertNotIn('forbidden-live-history', calls)
        self.assertNotIn('python3 scripts/release-migration-history-audit.py', calls)
        self.assertIn('python3 scripts/test-release-migration-history-audit.py', calls)
        self.assertIn('python3 scripts/test-release-retired-original-evidence.py', calls)
        self.assertIn('python3 scripts/test-release-lifecycle.py', calls)

    def test_preserved_lifecycle_gate_avoids_repeat_static_and_live_work(self):
        status, calls = self.run_architecture_step('gate_lifecycle', authority.FAST_TRACK_PR_BASE, preserved=True)
        self.assertEqual(0, status)
        self.assertEqual(1, len(calls.splitlines()))


class MergedSourceReceiptTests(unittest.TestCase):
    def setUp(self):
        self.run=dict(id=71,run_attempt=1,event="pull_request",pull_requests=[])
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
    def test_push_producer_cannot_forge_pr_source_scope(self):
        self.run['event'] = 'push'
        with self.assertRaisesRegex(RuntimeError, 'PR_EVENT_REQUIRED'):
            self.verify()

    def test_development_or_malformed_source_envelope_is_not_admission(self):
        self.receipt['developmentValidationScope'] = {'releaseAdmission':False}
        with self.assertRaisesRegex(RuntimeError, 'SOURCE_SCOPE_UNPROVEN'):
            self.verify()
        del self.receipt['developmentValidationScope']
        for scope in (None, {}, [], {'phase':'development-validation-only'}):
            self.receipt['sourceValidationScope'] = scope
            with self.subTest(scope=scope), self.assertRaisesRegex(RuntimeError, 'SOURCE_SCOPE_UNPROVEN'):
                self.verify()

    def test_architecture_receipt_has_its_own_exact_workflow_prefix(self):
        self.workflow = 'masterapp-platform-architecture-validation.yml'
        self.receipt['workflow'] = self.workflow
        self.artifact['name'] = 'validation-resume-architecture-71-1'
        self.assertEqual(81, self.verify())

    def test_unknown_workflow_cannot_reuse_parfait_receipt(self):
        self.workflow = 'unknown.yml'
        self.receipt['workflow'] = self.workflow
        self.artifact['name'] = 'validation-resume-parfait-71-1'
        with self.assertRaisesRegex(RuntimeError, 'WORKFLOW_UNSUPPORTED'):
            self.verify()

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


class TwoAttemptNoSqlPreflightHistoryTests(unittest.TestCase):
    def setUp(self):
        self.run = dict(id=38178839491,
            head_sha='910f93fc6bd247f826fffb9d9701676cf4d33112',
            run_attempt=2, status='completed', conclusion='failure')
        self.names=['Select exact affected applications',
            'Admit exact validated fast-track source',
            'Reconcile production schema before publication',
            'Affected App Deploy — ${{ matrix.target }}',
            'Publish verified website routing']
        self.artifacts=[dict(name=n,id=k,expired=False,
            workflow_run=dict(id=38178839491)) for n,k in (
            ('fast-track-admission-38178839491-1',11716507721),
            ('fast-track-database-recovery-38178839491-1',11716567678),
            ('fast-track-database-recovery-38178839491-2',11716923766))]
        self.steps=[dict(name=n, conclusion=result) for n,result in (
            ('Run actions/checkout@v4','success'),
            ('Build current read-only schema observer for source-only release','success'),
            ('Verify current database recovery window before retention readers','success'),
            ('Preserve recovery evidence before SQL or retention publication','success'),
            ('Check migration admission without entering a write step','failure'),
            ('Apply or reconcile the exact rehearsed migration bundle','skipped'),
            ('Reconcile fresh already-ready schema without SQL bundle or mutation','skipped'))]
        self.jobs={}
        for attempt in (1,2):
            rows=[dict(name=name,id=attempt*100+i,run_attempt=attempt,status='completed',
                conclusion=('failure' if i==2 else 'skipped' if i>=3 else 'success'),
                steps=[dict(row) for row in self.steps] if i==2 else [])
                for i,name in enumerate(self.names)]
            self.jobs[attempt]=rows
        self.log=('2026-10-11T22:12:01.001Z Database mutation activity remains active; '
                  'preserve prior evidence and reconcile without replay.\n')
        self.workflow=('Check migration admission without entering a write step\n'
                       'Apply or reconcile the exact rehearsed migration bundle\n'
                       'Reconcile fresh already-ready schema without SQL bundle or mutation\n')

    def prove(self):
        def api(repository,path,token):
            if '/artifacts?' in path:
                return dict(total_count=len(self.artifacts),artifacts=self.artifacts)
            for attempt in (1,2):
                if '/attempts/'+str(attempt)+'/jobs?' in path:
                    return dict(total_count=len(self.jobs[attempt]),jobs=self.jobs[attempt])
            raise AssertionError(path)
        with patch.object(authority,'is_fast_track_producer',return_value=True), \
             patch.object(authority,'api_get',side_effect=api), \
             patch.object(authority,'_release_history_source',return_value=self.workflow), \
             patch.object(authority,'_release_job_log',return_value=self.log):
            return authority.attested_fast_track_two_attempt_no_sql_preflight(
                'owner/repo',self.run,'token')

    def test_exact_two_attempts_are_nonentry(self):
        self.assertTrue(self.prove())

    def test_entered_sql_step_or_app_publication_blocks_proof(self):
        self.jobs[2][2]['steps'][-1]['conclusion']='success'
        self.assertFalse(self.prove())
        self.jobs[2][2]['steps'][-1]['conclusion']='skipped'
        self.jobs[1][3]['conclusion']='success'
        self.assertFalse(self.prove())

    def test_missing_artifact_log_or_attempt_blocks_proof(self):
        self.artifacts.pop()
        self.assertFalse(self.prove())
        self.setUp()
        self.log='read-only check failed without authenticated classification\n'
        self.assertFalse(self.prove())
        self.setUp()
        self.jobs[2][0]['status']='in_progress'
        self.assertFalse(self.prove())

    def test_changed_sha_or_unreviewed_attempt_blocks_proof(self):
        self.run['head_sha']='a'*40
        self.assertFalse(self.prove())
        self.run['head_sha']='910f93fc6bd247f826fffb9d9701676cf4d33112'
        self.run['run_attempt']=3
        self.assertFalse(self.prove())


class ExactBrandPreuploadRecoveryTests(unittest.TestCase):
    def setUp(self):
        self.run = dict(id=38318950445,head_sha='bdc87f64cb9150bfeac26b26044c826b835cbb13',
            run_attempt=1,status='completed',conclusion='failure')
        self.apps = []
        for name in ('AgentPortal','ClientApp','Protect','Website'):
            self.apps.append(dict(name='Affected App Deploy — '+name,status='completed',conclusion='failure',
                steps=[dict(name='Verify canonical brand registry and four-app identity',conclusion='failure')] +
                [dict(name=step,conclusion='skipped') for step in (
                    'Restore existing immutable package on resumed attempts',
                    'Compile application and produce ZIP',
                    'Preserve immutable application bytes before upload',
                    'Sign in to Azure Production',
                    'Publish or reconcile exact application bytes')]))
        self.migration = dict(name='Reconcile production schema before publication',
            status='completed',conclusion='success',steps=[
                dict(name='Apply or reconcile the exact rehearsed migration bundle',conclusion='skipped'),
                dict(name='Reconcile fresh already-ready schema without SQL bundle or mutation',conclusion='success')])
        self.jobs = [dict(name='Select exact affected applications',status='completed',conclusion='success'),
            dict(name='Admit exact validated fast-track source',status='completed',conclusion='success'),
            self.migration,*self.apps,
            dict(name='Publish verified website routing',status='completed',conclusion='skipped')]
        self.artifacts = [
            dict(name='legend-migration-observation-a96af08e6b3f871ff92be084434b4b576278cfe09630fd751ae3a03c6f47c37f',
                 id=11778505571,expired=False),
            dict(name='fast-track-database-recovery-38318950445-1',id=11776974677,expired=False),
            dict(name='fast-track-admission-38318950445-1',id=11776869414,expired=False)]
    def prove(self):
        with patch.object(authority,'is_fast_track_producer',return_value=True), \
             patch.object(authority,'api_get',return_value=dict(
                 total_count=len(self.artifacts),artifacts=self.artifacts)):
            return authority.attested_fast_track_brand_preupload(
                'owner/repo',self.run,self.jobs,'fixture')

    def test_exact_source_no_sql_brand_preupload_is_recoverable(self):
        self.assertTrue(self.prove())
    def test_changed_revision_or_second_attempt_never_inherits_scope(self):
        self.run['head_sha']='b'*40
        self.assertFalse(self.prove())
        self.run['head_sha']='bdc87f64cb9150bfeac26b26044c826b835cbb13'
        self.run['run_attempt']=2
        self.assertFalse(self.prove())
    def test_missing_app_or_entered_upload_fails_closed(self):
        removed=self.jobs.pop()
        self.assertFalse(self.prove())
        self.jobs.append(removed)
        self.apps[0]['steps'][-1]['conclusion']='failure'
        self.assertFalse(self.prove())
    def test_sql_entry_or_router_entry_fails_closed(self):
        self.migration['steps'][0]['conclusion']='success'
        self.assertFalse(self.prove())
        self.migration['steps'][0]['conclusion']='skipped'
        self.jobs[-1]['conclusion']='success'
        self.assertFalse(self.prove())
    def test_missing_retained_artifact_or_ambiguous_brand_result_fails_closed(self):
        self.artifacts.pop()
        self.assertFalse(self.prove())
        self.setUp()
        self.apps[0]['steps'][0]['conclusion']='success'
        self.assertFalse(self.prove())


class AdmissionRepairScopeTests(unittest.TestCase):
    def baseline(self, entered=False, attempt=1, duplicate=False, recovery=False, mutation="skipped", selector=False):
        before='a'*40;head='b'*40;original='c'*40
        prior=dict(id=7,run_attempt=attempt,head_sha=before,head_branch=authority.FAST_TRACK_PR_BASE,event='push',
            path='.github/workflows/'+authority.FAST_TRACK_RELEASE_WORKFLOW,
            head_repository={'full_name':'owner/repo'},status='completed',conclusion='failure')
        jobs=[dict(name='Admit exact validated fast-track source',conclusion='failure'),
              dict(name='Reconcile production schema before publication',conclusion='failure' if entered else 'skipped'),
              dict(name='Affected App Deploy — ${{ matrix.target }}',conclusion='skipped'),
              dict(name='Publish verified website routing',conclusion='skipped')]
        if selector:
            jobs.insert(0,dict(name='Select exact affected applications',status='completed',conclusion='failure'))
            jobs[1]['conclusion']='skipped'
        if recovery:
            jobs[1 if selector else 0]['conclusion']='success'
            jobs[1].update(conclusion='failure',steps=[dict(name='Apply or reconcile the exact rehearsed migration bundle',conclusion=mutation)])
        def get(repo,path,token):
            # This predecessor fixture has no completion-observer runs.
            summary = authority.fast_track_recovery_summary_name(prior['id'], before)
            completion = authority.fast_track_completion_summary_name(prior['id'], before)
            if path in {f'actions/artifacts?name={name}&per_page=100' for name in (summary,completion)}:
                return dict(total_count=0, artifacts=[])
            if path == 'actions/workflows/deployment-diagnostics.yml/runs?event=workflow_dispatch&per_page=100':
                return dict(total_count=0, workflow_runs=[])
            if path == 'actions/runs/7':
                return dict(prior)
            if path == 'actions/runs/7/artifacts?per_page=100':
                return dict(total_count=0, artifacts=[])
            if path.startswith('actions/runs?'):
                runs=[prior,dict(prior,id=8)] if duplicate else [prior]
                return dict(total_count=len(runs),workflow_runs=runs)
            if '/jobs?' in path:return dict(total_count=len(jobs),jobs=jobs)
            if path.startswith('commits/'):
                return [dict(merged_at='time',merge_commit_sha=before,base=dict(ref=authority.FAST_TRACK_PR_BASE),head=dict(repo=dict(full_name='owner/repo')))]
            raise AssertionError(path)
        with patch.dict(os.environ,GITHUB_REPOSITORY='owner/repo',GITHUB_TOKEN='fixture'), \
             patch.object(authority,'api_get',side_effect=get), \
             patch.object(authority,'_api_get_uncached',side_effect=get), \
             patch.object(authority,'fast_track_unpublished_apps_with_migration_intent',return_value=False), \
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
        with self.assertRaisesRegex(ValueError,'HISTORICAL_PRODUCER_AMBIGUOUS'):self.baseline(duplicate=True)

    def test_longer_proven_prewrite_repair_chain_keeps_original_application_scope(self):
        heads=[format(i+1,'040x') for i in range(9)]
        original='c'*40;head='b'*40
        jobs=[dict(name='Admit exact validated fast-track source',conclusion='failure'),
              dict(name='Reconcile production schema before publication',conclusion='skipped'),
              dict(name='Affected App Deploy — target',conclusion='skipped'),
              dict(name='Publish verified website routing',conclusion='skipped')]
        rows=[dict(id=index+1,run_attempt=1,
            head_sha=candidate,head_branch=authority.FAST_TRACK_PR_BASE,event='push',
            path='.github/workflows/'+authority.FAST_TRACK_RELEASE_WORKFLOW,
            head_repository={'full_name':'owner/repo'},status='completed',conclusion='failure')
            for index,candidate in enumerate(heads)]
        def get(repo,path,token):
            # This predecessor fixture has no completion-observer runs.
            summaries = {name for index, candidate in enumerate(heads) for name in (
                authority.fast_track_recovery_summary_name(index + 1, candidate),
                authority.fast_track_completion_summary_name(index + 1, candidate))}
            if path in {f'actions/artifacts?name={name}&per_page=100' for name in summaries}:
                return dict(total_count=0, artifacts=[])
            if path == 'actions/workflows/deployment-diagnostics.yml/runs?event=workflow_dispatch&per_page=100':
                return dict(total_count=0, workflow_runs=[])
            for row in rows:
                if path == f"actions/runs/{row['id']}":
                    return dict(row)
                if path == f"actions/runs/{row['id']}/artifacts?per_page=100":
                    return dict(total_count=0, artifacts=[])
            if path.startswith('actions/runs?'):
                return dict(total_count=len(rows),workflow_runs=rows)
            if '/jobs?' in path:return dict(total_count=len(jobs),jobs=jobs)
            if path.startswith('commits/'):
                candidate=path.split('/')[1]
                return [dict(merged_at='time',merge_commit_sha=candidate,base=dict(ref=authority.FAST_TRACK_PR_BASE),
                    head=dict(repo=dict(full_name='owner/repo')))]
            raise AssertionError(path)
        def parents(args,**kwargs):
            candidate=args[-1]
            if candidate==original:return original
            i=heads.index(candidate)
            return candidate+' '+(heads[i+1] if i+1<len(heads) else original)+' '+head
        with patch.dict(os.environ,GITHUB_REPOSITORY='owner/repo',GITHUB_TOKEN='fixture'), \
             patch.object(authority,'api_get',side_effect=get), \
             patch.object(authority,'_api_get_uncached',side_effect=get), \
             patch.object(authority.subprocess,'run',return_value=SimpleNamespace(returncode=0)), \
             patch.object(authority.subprocess,'check_output',side_effect=parents):
            self.assertEqual(original,authority.fast_track_effective_baseline(heads[0],head))


    def test_failed_selector_with_all_downstream_writes_skipped_carries_unpublished_scope(self):
        self.assertEqual('c'*40,self.baseline(selector=True))

    def test_failed_selector_cannot_carry_scope_after_any_writer_or_rerun(self):
        with self.assertRaisesRegex(RuntimeError,'RECONCILIATION'):
            self.baseline(selector=True,entered=True)
        with self.assertRaisesRegex(RuntimeError,'RECONCILIATION'):
            self.baseline(selector=True,attempt=2)

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
            # The canonical authority owns the single bounded observation budget.
            check.assert_called_once_with(target='portal')
            sleep.assert_not_called()


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

    def modern_admission_failure(self):
        self.admission_failure()
        head='ba4a4bdd9941f314f0aab769d638504a53b0ec8d'
        self.log=self.log.replace(self.run['head_sha'],head).replace('line 122,','line 158,').replace(
            'line 111,','line 115,').replace('line 1819,','line 1930,')
        self.run['head_sha']=head
    def test_current_admission_body_survives_unrelated_source_line_movement(self):
        self.modern_admission_failure()
        self.assertTrue(self.prove())
        source=authority._release_history_source
        shifted=self.log.replace('line 1930,','line 1932,')
        with patch.object(authority,'_release_history_source',side_effect=lambda repo,head,path,token:
                '# unrelated registry note\n\n'+source(repo,head,path,token)
                if path=='scripts/validation-resume.py' else source(repo,head,path,token)):
            self.assertFalse(self.prove())  # Original line cannot be relabelled.
            self.log=shifted; self.assertTrue(self.prove())
        self.log=self.log.replace('line 1932,','line 1930,')
        with patch.object(authority,'_release_history_source',side_effect=lambda repo,head,path,token:
                source(repo,head,path,token).replace(
                    '    gate = DIRECT_RELEASE_CHILDREN[child]\n    partition_identity',
                    '    gate = dict(DIRECT_RELEASE_CHILDREN[child])\n    partition_identity')
                if path=='scripts/validation-resume.py' else source(repo,head,path,token)):
            self.assertFalse(self.prove())
    def test_original_source_admission_head_change_precedes_provider_entry(self):
        head='1f08cd5a2518a4d206ecf4bccdad4024b7cba26e'
        self.run['head_sha']=head
        prefix=self.log.split('2026-10-11T18:00:30Z Traceback')[0].replace(self.head,head)
        lines=['Traceback (most recent call last):',
            '  File "release-router.py", line 158, in <module>', '    fast_track()',
            '  File "release-router.py", line 82, in fast_track', '    authority.fast_track_source_admission()',
            '  File "validation-resume.py", line 4023, in fast_track_source_admission',
            '    run = assert_fast_track_release_execution()',
            '  File "validation-resume.py", line 3691, in assert_fast_track_release_execution',
            "    raise RuntimeError('FAST_TRACK_RELEASE_HEAD_CHANGED')",
            'RuntimeError: FAST_TRACK_RELEASE_HEAD_CHANGED']
        self.log=prefix+''.join('2026-10-11T18:00:30Z '+line+'\n' for line in lines)
        self.assertTrue(self.prove())
        original=self.log
        for changed in (original.replace('line 82,','line 83,'),original.replace('line 3691,','line 3692,'),
                original.replace('18:00:30Z   File "validation','17:59:30Z   File "validation'),
                original.replace('FAST_TRACK_RELEASE_HEAD_CHANGED','FAST_TRACK_UNKNOWN')):
            self.log=changed;self.assertFalse(self.prove())
    def test_five_completed_apps_use_actual_current_prewrite_proof(self):
        self.modern_admission_failure();self.run['run_attempt']=3
        apps=[dict(name='Affected App Deploy — '+name,status='completed',conclusion='success')
              for name in ('AgentPortal','ClientApp','Protect','Parfait','Website')]
        apps += [dict(name=name,status='completed',conclusion='success') for name in
                 ('Admit exact validated fast-track source','Reconcile production schema before publication')]
        earlier=dict(self.job,conclusion='skipped')
        def get(repo,path,token):
            if '/artifacts?' in path:return dict(total_count=0,artifacts=[])
            rows=[self.job] if '/attempts/3/' in path else [earlier]
            return dict(total_count=len(rows),jobs=rows)
        with patch.object(authority,'api_get',side_effect=get),patch.object(authority,'_release_job_log',return_value=self.log):
            self.assertTrue(authority.fast_track_completed_apps_pending_routing('owner/repo',self.run,apps+[self.job],'fixture'))
            earlier['conclusion']='failure'
            earlier['steps']=[dict(self.job['steps'][0],started_at='2026-10-10T18:00:00Z',
                                   completed_at='2026-10-10T18:01:00Z')]
            self.assertFalse(authority.fast_track_completed_apps_pending_routing('owner/repo',self.run,apps+[self.job],'fixture'))


class PreservedRouterGenerationTests(unittest.TestCase):
    def setUp(self):
        import copy
        self.copy=copy.deepcopy
        self.material='b'*64
        self.original='4910a01a7929ce263f497873f4165e04f35ea81d'
        self.historical='1f08cd5a2518a4d206ecf4bccdad4024b7cba26e'
        self.current='ba4a4bdd9941f314f0aab769d638504a53b0ec8d'
        self.producer=self.producer_run(10,self.original,'2026-10-12T15:00:00Z')
        self.consumer=self.producer_run(20,self.historical,'2026-10-12T16:00:00Z')
        self.active=self.producer_run(30,self.current,'2026-10-17T17:00:00Z');self.active['status']='in_progress'
        self.producers=[self.active,self.consumer,self.producer]
        self.direct=[]
        self.artifacts={10:[],20:[],30:[]};self.records={}
        self.add_pair(self.producer,self.material)
        step=dict(name=authority.FAST_TRACK_CHILD_STEPS['routing-cloudflare'][1],status='completed',conclusion='failure',
                  started_at='2026-10-12T16:23:25Z',completed_at='2026-10-12T16:23:48Z')
        self.job=dict(id=90,name=authority.FAST_TRACK_CHILD_STEPS['routing-cloudflare'][0],
                      status='completed',conclusion='failure',steps=[step])
        self.log=('2026-10-12T16:23:00Z [command]/usr/bin/git log -1 --format=%H\n'
                  +'2026-10-12T16:23:01Z '+self.historical+'\n')
        lines=['FAST_TRACK_COMPANY_RENDERER:VERIFIED_BEFORE_ROUTING','FAST_TRACK_ROUTER:preserved',
               'FAST_TRACK_DNS_PROXY_RECONCILIATION:apex_and_www_only',
               'cloudflare-routing-authority.CloudflareError: Website DNS proxy requires DNS Edit authority or recovery: mylegnd.com']
        self.log+=''.join('2026-10-12T16:23:47Z '+line+'\n' for line in lines)
        self.incomplete=False
    def producer_run(self,number,head,created):
        return dict(id=number,head_sha=head,head_branch=authority.FAST_TRACK_PR_BASE,event='push',
            path='.github/workflows/'+authority.FAST_TRACK_RELEASE_WORKFLOW,head_repository=dict(full_name='owner/repo'),
            status='completed',conclusion='failure',run_attempt=1,created_at=created)
    def add_pair(self,producer,material):
        generation=authority.direct_child_operation_identity('routing-cloudflare',producer['head_sha'],material)
        for phase,version in [('intent','aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'),
                              ('success','bbbbbbbb-cccc-dddd-eeee-ffffffffffff')]:
            record=dict(schemaVersion=1,child='routing-cloudflare',dependencyIdentity=generation,
                materialIdentity=material,partitionIdentity=material,applicationRevision=producer['head_sha'],
                executionAuthority=producer['head_sha'],producingRun=producer['id'],producingAttempt=1,phase=phase,
                evidenceIdentity=authority.direct_child_identity('routing-cloudflare',producer['head_sha'],material,
                    workflow=(authority.FAST_TRACK_RELEASE_WORKFLOW if producer['event']=='push'
                              else authority.DIRECT_RELEASE_WORKFLOW)),observation=dict(providerVersion=version))
            artifact=dict(id=100+len(self.records),name='legend-release-child-'+phase+'-'+generation,expired=False,
                          created_at='2026-10-12T15:18:10Z',workflow_run=dict(id=producer['id']))
            self.artifacts.setdefault(producer['id'],[]).append(artifact);self.records[artifact['id']]=record
    def api(self,repo,path,token):
        if path == 'actions/runs?branch=Legend%2Ffast-track-release&per_page=100':
            return dict(total_count=len(self.producers),workflow_runs=self.producers)
        if 'event=workflow_dispatch&' in path:return dict(total_count=len(self.direct),workflow_runs=self.direct)
        if path.startswith('actions/artifacts?name='):
            name=__import__('urllib.parse',fromlist=['unquote']).unquote(path.split('name=',1)[1].split('&')[0])
            rows=[a for values in self.artifacts.values() for a in values if a['name']==name]
            return dict(total_count=len(rows),artifacts=rows)
        import re
        match=re.fullmatch(r'actions/runs/(\d+)(/artifacts\?per_page=100)?',path)
        if match:
            number=int(match[1])
            if match[2]:
                rows=self.artifacts[number]
                return dict(total_count=len(rows)+(1 if self.incomplete else 0),artifacts=rows)
            return next(row for row in self.producers+self.direct if row['id']==number)
        if path.startswith('compare/'):return dict(status='identical')
        raise AssertionError('Unexpected fixture read: '+path)
    def download(self,repo,run_id,name,directory,*,artifact_id=None):
        rows=[a for a in self.artifacts[run_id] if a['name']==name and (artifact_id is None or a['id']==artifact_id)]
        if len(rows)!=1:raise AssertionError('Nonunique fixture archive')
        (directory/'operation.json').write_text(__import__('json').dumps(self.records[rows[0]['id']]))
    def check(self,material=None,revision=None):
        from contextlib import ExitStack
        with ExitStack() as stack:
            stack.enter_context(patch.object(authority,'api_get',side_effect=self.api))
            stack.enter_context(patch.object(authority,'_release_job_log',return_value=self.log))
            stack.enter_context(patch.object(authority,'_download_run_artifact',side_effect=self.download))
            stack.enter_context(patch.dict(authority._RELEASE_HISTORY_API,{},clear=True))
            stack.enter_context(patch.dict(authority._RELEASE_HISTORY_RECEIPTS,{},clear=True))
            stack.enter_context(patch.object(authority,'fast_track_child_attempts',
                return_value=[(self.consumer,1,self.job,self.job['steps'][0])]))
            material=material or self.material;revision=revision or self.current
            return authority.release_child_first_write_proven('owner/repo','routing-cloudflare',
                authority.direct_child_operation_identity('routing-cloudflare',revision,material),
                material,30,1,'fixture',partition_identity=material)
    def test_original_pair_allows_only_a_distinct_physical_generation(self):
        self.assertTrue(self.check())
        with self.assertRaisesRegex(RuntimeError,'physical operation already entered'):
            self.check(revision=self.historical)
    def test_missing_expired_future_or_success_only_original_pair_cannot_authorize(self):
        original=self.copy(self.artifacts)
        for change in ('missing','expired','future','success-only'):
            with self.subTest(change=change):
                self.artifacts=self.copy(original)
                if change=='missing':self.artifacts[10]=[]
                elif change=='expired':self.artifacts[10][0]['expired']=True
                elif change=='future':self.artifacts[10][1]['created_at']='2026-10-12T16:23:49Z'
                else:self.artifacts[10]=self.artifacts[10][1:]
                with self.assertRaises(RuntimeError):self.check()
        self.artifacts=original;self.incomplete=True
        with self.assertRaisesRegex(RuntimeError,'inventory incomplete'):self.check()
    def test_other_original_material_is_not_discarded_to_select_current_material(self):
        other=self.producer_run(11,self.original,'2026-10-12T15:00:00Z');self.producers.append(other)
        self.add_pair(other,'c'*64)
        with self.assertRaisesRegex(RuntimeError,'not unique'):self.check()
        self.artifacts[11]=self.artifacts[11][1:]  # A competing partial group still counts.
        with self.assertRaisesRegex(RuntimeError,'not unique'):self.check()
    def test_direct_release_original_candidate_participates_in_uniqueness(self):
        other=self.producer_run(11,self.original,'2026-10-12T15:00:00Z')
        other.update(head_branch=authority.TRUSTED_PR_BASE,event='workflow_dispatch',
            path='.github/workflows/'+authority.DIRECT_RELEASE_WORKFLOW,
            display_title='LEGEND release pr=1 candidate='+self.original+' authority='+self.original)
        self.direct.append(other);self.add_pair(other,'c'*64)
        with self.assertRaisesRegex(RuntimeError,'not unique'):self.check()
    def test_canonical_record_changed_after_catalog_read_is_rejected(self):
        history=authority.release_child_history
        def changed(*args,**kwargs):
            record=history(*args,**kwargs)
            return dict(record,observation=dict(providerVersion='cccccccc-dddd-eeee-ffff-aaaaaaaaaaaa')) if args[4]=='success' else record
        with patch.object(authority,'release_child_history',side_effect=changed):
            with self.assertRaisesRegex(RuntimeError,'canonical receipt changed'):self.check()
    def test_conflicting_originals_in_the_same_phase_are_rejected(self):
        other=self.producer_run(11,self.original,'2026-10-12T15:00:00Z');self.producers.append(other)
        self.add_pair(other,self.material);self.artifacts[11]=self.artifacts[11][:1]
        with self.assertRaisesRegex(RuntimeError,'incomplete or competing'):self.check()
    def test_wrong_producer_phase_partition_or_observation_is_rejected(self):
        original=self.copy(self.records)
        for key,value in [('producingRun',20),('phase','success'),('partitionIdentity','c'*64),
                          ('observation',dict(providerVersion='invalid'))]:
            with self.subTest(key=key):
                self.records=self.copy(original);self.records[100][key]=value
                with self.assertRaises(RuntimeError):self.check()
        self.records=original;self.records[101]['materialIdentity']='c'*64
        with self.assertRaises(RuntimeError):self.check()
    def test_unproven_preserved_execution_never_infers_absence(self):
        original=self.log
        for changed in (original.replace('FAST_TRACK_ROUTER:preserved','FAST_TRACK_ROUTER:verified'),
                original+original.splitlines(True)[3],original.replace('16:23:47Z FAST_TRACK_ROUTER','16:22:47Z FAST_TRACK_ROUTER'),
                original.replace(self.historical,'a'*40)):
            with self.subTest(log=changed):
                self.log=changed
                with self.assertRaisesRegex(RuntimeError,'missing intent'):self.check()
        self.log=original
        source=authority._release_history_source
        with patch.object(authority,'_release_history_source',side_effect=lambda repo,head,path,token:
                source(repo,head,path,token)+'\n# changed owner\n' if path=='scripts/release-router.py' else source(repo,head,path,token)):
            with self.assertRaisesRegex(RuntimeError,'missing intent'):self.check()
    def test_dns_denial_is_not_a_whole_step_prewrite_proof(self):
        with patch.object(authority,'api_get',side_effect=self.api),patch.object(authority,'_release_job_log',return_value=self.log):
            self.assertFalse(authority.attested_fast_track_router_prewrite('owner/repo',self.consumer,[self.job],'fixture'))

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


class CanonicalPaymentImpactTests(unittest.TestCase):
    payment_paths = ('Domain/Billing/BillingModels.cs', 'Domain/Entities/SubscriptionPayment.cs',
        'Domain/Entities/SubscriptionPaymentCompletionEffect.cs', 'Domain/Entities/SubscriptionPaymentRefund.cs',
        'Infrastructure/Billing/BillingReconciliationService.cs')

    def test_exact_shared_payment_sources_select_four_managed_consumers(self):
        for path in self.payment_paths:
            with self.subTest(path=path):
                scope=authority.fast_track_scope([path])
                self.assertEqual({'portal','client','protect','parfait'},
                    {row['key'] for row in scope['matrix']['include']})
                self.assertTrue(scope['commerce'])
                self.assertFalse(scope['schema']);self.assertFalse(scope['routing'])

    def test_exact_test_and_control_paths_do_not_create_deployment_impact(self):
        for path in ('Legend-Cloudflare/tests/runtime/runtime.test.mjs','scripts/recovery-replacement-admission.py'):
            with self.subTest(path=path):
                scope=authority.fast_track_scope([path])
                self.assertEqual({'include':[]},scope['matrix'])
                self.assertFalse(scope['selected']);self.assertFalse(scope['commerce'])
                self.assertFalse(scope['schema']);self.assertFalse(scope['routing'])
        for path in ('Domain/Billing/Unreviewed.cs','Domain/Entities/SubscriptionPaymentOther.cs',
            'Infrastructure/Billing/Unreviewed.cs','Legend-Cloudflare/tests/runtime/other.mjs',
            'scripts/recovery-replacement-other.py'):
            with self.subTest(path=path),self.assertRaisesRegex(ValueError,'Unmapped'):
                authority.fast_track_scope([path])

    def test_payment_migration_and_shared_studio_preserve_full_combined_impact(self):
        scope=authority.fast_track_scope([*self.payment_paths,
            'Infrastructure/Migrations/20261026000134_AddCanonicalSubscriptionPaymentCompletion.cs',
            'SHARED/WebsitePlatform/legend-public-cms.js'])
        self.assertEqual({'portal','client','protect','parfait','website'},
            {row['key'] for row in scope['matrix']['include']})
        self.assertTrue(scope['schema']);self.assertTrue(scope['commerce'])
        self.assertFalse(scope['routing'])


class FastTrackNoSqlInputInventoryTests(unittest.TestCase):
    def test_no_sql_regression_is_control_only_not_unmapped_or_runtime_owned(self):
        path='scripts/test-fast-track-ready-no-sql.py'
        self.assertTrue(authority.release_control_only_path(path))
        scope=authority.fast_track_scope([path])
        self.assertEqual([],scope['impacts'][path])
        self.assertFalse(scope['selected'])
        self.assertFalse(scope['commerce'])
        self.assertFalse(scope['schema'])



class ReleaseAdmissionScopeRegressionTests(unittest.TestCase):
    def test_known_shared_sources_and_controls_preserve_real_dependencies(self):
        shared = (
            'Domain/Entities/ClientSubscriptionOffer.cs',
            'Infrastructure/Billing/BillingEntitlementService.cs',
            'Infrastructure/Billing/BillingStateMapper.cs',
            'Infrastructure/Billing/MasterAppBillingOrchestrator.cs',
            'Infrastructure/Businesses/BusinessWorkspaceControllerBase.cs',
            'Infrastructure/Businesses/CommerceBusinessProvisioningService.cs',
            'Infrastructure/Households/HouseholdMembershipService.cs',
            'Infrastructure/Infrastructure.csproj',
            'Infrastructure/Leads/WebsiteInquiryAuthority.cs',
            'Infrastructure/Leads/WebsiteLeadAppNotificationService.cs',
            'Infrastructure/Leads/WebsiteLeadEmailTemplate.cs',
            'Infrastructure/Leads/WebsiteLeadNotificationAuthority.cs',
            'Infrastructure/Leads/WebsiteLeadSubmission.cs',
            'SHARED/Analytics/AiSafeAnalyticsPayload.cs',
            'SHARED/Analytics/GrowthEconomicsAndOpenAiFeedContracts.cs',
            'SHARED/Analytics/MarketingManagerContracts.cs',
            'Infrastructure/Reception/ReceptionistContentReview.cs',
            'SHARED/Analytics/ReceptionistContentReview.cs')
        controls = ('LEGEND-REVENUE-LAUNCH-CHECKPOINT.md',
            'scripts/DomainReleaseRefresh/DomainReleaseRefresh.csproj',
            'scripts/sync-published-checkout.py','scripts/test-sync-published-checkout.py')
        for path in shared:
            with self.subTest(path=path):
                scope=authority.fast_track_scope([path])
                self.assertEqual(set(scope['impacts'][path]),{'portal','client','protect','parfait'})
                self.assertEqual({row['key'] for row in scope['matrix']['include']},
                                 {'portal','client','protect','parfait'})
                self.assertTrue(scope['selected'])
                self.assertTrue(scope['commerce'])
                self.assertFalse(scope['routing'])
                self.assertEqual(scope['schema'],path=='Infrastructure/Infrastructure.csproj')
        for path in controls:
            with self.subTest(path=path):
                scope=authority.fast_track_scope([path])
                self.assertEqual(scope['impacts'][path],[])
                self.assertFalse(scope['selected'])
                self.assertFalse(scope['commerce'])
        combined=authority.fast_track_scope([*shared,*controls,
            'Legend-Cloudflare/src/website-routing/index.ts',
            'Infrastructure/Migrations/20261011064911_AddBusinessScopedMerchantPayments.cs'])
        self.assertTrue(combined['routing']);self.assertTrue(combined['schema'])
        self.assertEqual({r['key'] for r in combined['matrix']['include']},{'portal','client','protect','parfait'})

    def test_unknown_adjacent_sources_still_fail_closed(self):
        for path in ('Infrastructure/Billing/Unreviewed.cs','Domain/Entities/Unreviewed.cs',
                     'Infrastructure/Businesses/Unreviewed.cs','Infrastructure/Leads/Unreviewed.cs',
                     'SHARED/Analytics/Unreviewed.cs','Infrastructure/Reception/Unreviewed.cs','scripts/unreviewed.py'):
            with self.subTest(path=path),self.assertRaisesRegex(ValueError,'Unmapped'):
                authority.fast_track_scope([path])

    def test_scope_is_zero_write_planning(self):
        with patch.object(authority.subprocess,'run',side_effect=AssertionError('No command in scope planning')), \
             patch.object(authority,'api_get',side_effect=AssertionError('No remote call in scope planning')):
            scope=authority.fast_track_scope(['Infrastructure/Infrastructure.csproj'])
        self.assertTrue(scope['schema'])

    def test_management_entities_and_model_have_exact_shared_schema_ownership(self):
        for path in ('Domain/Entities/ReceptionistManagement.cs',
                     'Infrastructure/Data/Configurations/ReceptionistModelConfiguration.Management.cs'):
            scope=authority.fast_track_scope([path])
            self.assertEqual(set(scope['impacts'][path]), {'portal','client','protect','parfait'})
            self.assertTrue(scope['commerce'])
            self.assertEqual(scope['schema'], path.startswith('Infrastructure/'))
            with self.assertRaisesRegex(ValueError, 'Unmapped'):
                authority.fast_track_scope([path.replace('.cs','.Unreviewed.cs')])

    def test_browser_and_management_partials_have_exact_shared_ownership(self):
        paths = (
            'Infrastructure/Businesses/BusinessWorkspaceControllerBase.ReceptionistManagement.cs',
            'Infrastructure/Reception/ReceptionistAdmissionService.Browser.cs',
            'Infrastructure/Reception/ReceptionistBrowserController.cs',
            'Infrastructure/Reception/ReceptionistOperatorService.Management.cs',
            'Infrastructure/Reception/ReceptionistOperatorService.Stored.cs',
            'SHARED/Analytics/ReceptionistManagement.cs')
        for path in paths:
            with self.subTest(path=path):
                scope = authority.fast_track_scope([path])
                self.assertEqual(set(scope['impacts'][path]), {'portal', 'client', 'protect', 'parfait'})
                self.assertTrue(scope['commerce'])
                self.assertFalse(scope['schema']); self.assertFalse(scope['routing'])
                with self.assertRaisesRegex(ValueError, 'Unmapped'):
                    authority.fast_track_scope([path.replace('.cs', '.Unreviewed.cs')])
        scope = authority.fast_track_scope([*paths, 'Legend-Design/legend-public-inquiry.js'])
        self.assertEqual({r['key'] for r in scope['matrix']['include']},
                         {'portal', 'client', 'protect', 'parfait', 'website'})

    def test_receptionist_content_candidate_selects_only_four_managed_hosts(self):
        paths = ('AgentPortal/Controllers/WebsiteAnalyticsController.Receptionist.cs', 'AgentPortal.Tests/ReceptionistApplicationServicesTests.cs', 'AgentPortal.Tests/ReceptionistContentReviewTests.cs', 'AgentPortal.Tests/ReceptionistSetupAndAuthorityTests.cs', 'Infrastructure/Analytics/MarketingConnectionStore.ReceptionistContent.cs', 'Infrastructure/Businesses/BusinessWorkspaceControllerBase.Receptionist.cs', 'Infrastructure/Reception/ReceptionistApplicationServices.cs', 'Infrastructure/Reception/ReceptionistContentReview.cs', 'Infrastructure/Reception/ReceptionistContracts.cs', 'Infrastructure/Reception/ReceptionistOperatorService.cs', 'Infrastructure/Reception/ReceptionistRuntimeAuthority.cs', 'Infrastructure/Reception/ReceptionistSetupForms.cs', 'SHARED/Analytics/ReceptionistConfiguration.cs', 'SHARED/Analytics/ReceptionistContentReview.cs', 'SHARED/Analytics/ReceptionistSetup.cs', 'SHARED/Views/Shared/ReceptionistContentReview.cshtml', 'SHARED/Views/Shared/ReceptionistSetup.cshtml')
        scope = authority.fast_track_scope(paths)
        self.assertEqual({row['key'] for row in scope['matrix']['include']}, {'portal', 'client', 'protect', 'parfait'})
        self.assertTrue(scope['selected']); self.assertTrue(scope['commerce'])
        self.assertFalse(scope['schema']); self.assertFalse(scope['routing'])

class TrustedToolPromotionAdmissionTests(unittest.TestCase):
    def test_promotion_never_enters_recovery_or_provider(self):
        policy=Mock();policy.tool_promotion_requested.return_value=True
        policy.trusted_tool_promotion.return_value={'productionEligible':False}
        with patch.object(authority,'_fast_track_policy',return_value=policy),patch.object(authority,'fast_track_source_admission',return_value={}) as proof,patch.object(authority,'fast_track_effective_baseline',side_effect=AssertionError('recovery')),patch.object(authority,'fast_track_pending_routing',side_effect=AssertionError('routing')),patch.object(authority.subprocess,'check_output',side_effect=AssertionError('git diff')):
            result=authority.fast_track_selection('a'*40,'b'*40)
        proof.assert_called_once_with(allow_nonpublishing=True)
        self.assertFalse(result['productionEligible'])
        self.assertFalse(any(result[key] for key in ('selected','commerce','schema','routing')))
        self.assertEqual({'include':[]},result['matrix'])

    def test_bad_marker_never_falls_back_to_application_release(self):
        policy=Mock();policy.tool_promotion_requested.return_value=True
        policy.trusted_tool_promotion.side_effect=ValueError('bad marker')
        with patch.object(authority,'_fast_track_policy',return_value=policy),patch.object(authority,'fast_track_source_admission',return_value={}),patch.object(authority,'fast_track_effective_baseline',side_effect=AssertionError('recovery')):
            with self.assertRaisesRegex(ValueError,'bad marker'):authority.fast_track_selection('a'*40,'b'*40)

    def history(self, mutate=None, carry=False, recovery=False):
        before,head='a'*40,'b'*40
        run=dict(id=77,run_attempt=1,head_sha=head,status='completed',conclusion='success',
            head_branch=authority.FAST_TRACK_PR_BASE,event='push',
            path='.github/workflows/'+authority.FAST_TRACK_RELEASE_WORKFLOW,
            head_repository=dict(full_name='MYLEGND/masterapp'))
        proof={'phase':'trusted-tool-promotion','baseRevision':before}
        jobs=[dict(name=name,conclusion='success' if index==0 else 'skipped',run_id=77,run_attempt=1,head_sha=head) for index,name in enumerate(('Select exact affected applications','Admit exact validated fast-track source','Reconcile production schema before publication','Publish verified website routing','Affected App Deploy'))]
        if recovery:
            jobs.append(dict(name=authority.FAST_TRACK_RECOVERY_JOB, status='completed', conclusion='skipped',
                             steps=[], run_id=77, run_attempt=1, head_sha=head))
        artifact=dict(id=99,name='fast-track-admission-77-1',expired=False,workflow_run={'id':77})
        receipt=dict(proof,producingRun=77,producingAttempt=1)
        if mutate:mutate(run,jobs,artifact,receipt)
        policy=Mock();policy.tool_promotion_requested.side_effect=lambda sha:sha==head
        policy.read_request.return_value={'trustedToolPromotion':{'baseRevision':before}}
        policy.trusted_tool_promotion.return_value=proof
        def api(repo,path,token):
            if '/artifacts?' in path:return {'total_count':1,'artifacts':[artifact]}
            if '/jobs?' in path:return {'total_count':len(jobs),'jobs':jobs}
            if path == 'actions/runs?branch=Legend%2Ffast-track-release&per_page=100':
                return {'total_count':1,'workflow_runs':[run]}
            raise AssertionError(path)
        def download(repo,runid,name,root,**kwargs):
            self.assertEqual(99,kwargs['artifact_id']);(root/'fast-track-admission.json').write_text(json.dumps(receipt))
        with patch.object(authority,'_release_history_source',return_value='jobs:\n  recover-fast-track:\n' if recovery else ''),patch.object(authority,'_fast_track_policy',return_value=policy),patch.object(authority,'is_fast_track_producer',return_value=True),patch.object(authority,'fast_track_merged_source_proof',return_value={}),patch.object(authority,'api_get',side_effect=api),patch.object(authority,'_download_run_artifact',side_effect=download),patch.object(authority,'fast_track_untriggered_repair_parent',return_value=None),patch.object(authority.subprocess,'run',return_value=SimpleNamespace(returncode=0)),patch.dict(os.environ,{'GITHUB_REPOSITORY':'MYLEGND/masterapp','GH_TOKEN':'fixture'}):
            return authority.fast_track_effective_baseline(head,'c'*40) if carry else authority.fast_track_promotion_parent('MYLEGND/masterapp',run,'fixture')

    def test_successful_nonpublishing_promotion_preserves_original_recovery_baseline(self):
        self.assertEqual('a'*40,self.history(carry=True))

    def test_promoted_recovery_owner_must_be_conclusively_skipped(self):
        self.assertEqual('a'*40,self.history(recovery=True))
        for mutation in [lambda r,j,a,p:j[-1].update(conclusion='success'),
                         lambda r,j,a,p:j[-1].update(status='in_progress'),
                         lambda r,j,a,p:j[-1].update(steps=[{}]),lambda r,j,a,p:j.pop()]:
            with self.subTest(mutation=mutation),self.assertRaises(ValueError):
                self.history(mutation,recovery=True)

    def test_historical_receipt_and_job_identities_fail_closed(self):
        mutations=[lambda r,j,a,p:a.update(id=0),lambda r,j,a,p:a.update(workflow_run={'id':78}),
            lambda r,j,a,p:j[0].update(run_id=78),lambda r,j,a,p:j[0].update(run_attempt=2),
            lambda r,j,a,p:j[0].update(head_sha='c'*40),lambda r,j,a,p:j.append(dict(name='unexpected')),
            lambda r,j,a,p:j[-1].update(conclusion='success'),lambda r,j,a,p:p.update(producingAttempt=2)]
        for mutation in mutations:
            with self.subTest(mutation=mutation),self.assertRaises(ValueError):self.history(mutation)

    def test_normal_infrastructure_keeps_application_and_schema_scope(self):
        scope=authority.fast_track_scope(['Infrastructure/Infrastructure.csproj'])
        self.assertTrue(scope['schema']);self.assertTrue(scope['commerce']);self.assertTrue(scope['selected'])
        self.assertEqual(4,len(scope['matrix']['include']))

    def test_following_release_selects_recovered_application_and_routing(self):
        policy=Mock();policy.tool_promotion_requested.side_effect=lambda sha:sha=='b'*40
        policy.read_request.return_value={}
        normal=authority.fast_track_scope(['Infrastructure/Infrastructure.csproj'])
        policy.release_after_tool_promotion.return_value={'targets':[r['azure_app'] for r in normal['matrix']['include']],'cloudflareWebsiteRouting':True}
        with patch.object(authority,'_fast_track_policy',return_value=policy),patch.object(authority,'fast_track_source_admission',return_value={}),patch.object(authority,'fast_track_effective_baseline',return_value='a'*40),patch.object(authority.subprocess,'check_output',return_value='Infrastructure/Infrastructure.csproj\n'),patch.object(authority,'attested_website_only_recovery_scope',return_value=False),patch.object(authority,'fast_track_pending_routing',return_value=True) as routing:
            result=authority.fast_track_selection('b'*40,'c'*40)
        routing.assert_called_once_with('a'*40)
        self.assertTrue(result['productionEligible']);self.assertTrue(result['routing']);self.assertTrue(result['schema'])
    def test_every_production_job_requires_positive_eligibility_and_selector_has_no_oidc(self):
        text=Path('.github/workflows/legend-fast-track-ui.yml').read_text()
        selector=text.split('  select-targets:',1)[1].split('  commerce-gates:',1)[0]
        self.assertNotIn('id-token: write',selector)
        self.assertNotIn('id-token: write',text.split('jobs:',1)[0])
        for job in ('commerce-gates','migrations','deploy-ui','routing-cloudflare'):
            block=text.split('  '+job+':',1)[1]
            condition=next(line for line in block.splitlines() if line.startswith('    if:'))
            self.assertIn("needs.select-targets.outputs.production-eligible == 'true'",condition)




class BoundedFastTrackHistoryTests(unittest.TestCase):
    def test_parallel_complete_inventory_preserves_run_and_attempt_order_and_budget(self):
        import threading
        runs = [dict(id=i, run_attempt=2, status='completed', head_sha=format(i, '040x')) for i in range(1, 13)]
        barrier = threading.Barrier(6)
        lock = threading.Lock()
        active = peak = 0
        seen = []
        budget = (123.0, lambda: 100.0)
        def get(repo, path, token):
            nonlocal active, peak
            run, attempt = int(path.split('/')[2]), int(path.split('/')[4])
            self.assertEqual(budget, authority._EVIDENCE_BUDGET.get())
            with lock:
                active += 1
                peak = max(peak, active)
                seen.append((run, attempt))
            try:
                if run <= 6 and attempt == 1:
                    barrier.wait(timeout=3)
                return dict(total_count=1, jobs=[dict(name=authority.FAST_TRACK_CHILD_STEPS['migrations'][0],
                    status='completed', conclusion='success', steps=[dict(
                        name=authority.FAST_TRACK_CHILD_STEPS['migrations'][1], status='completed', conclusion='success')])])
            finally:
                with lock: active -= 1
        with patch.object(authority, 'fast_track_execution_history', return_value=runs), \
             patch.object(authority, '_release_history_source', return_value='source'), \
             patch.object(authority, 'named_step_blocks', return_value={authority.FAST_TRACK_CHILD_STEPS['migrations'][1]: 'body'}), \
             patch.object(authority, 'attested_fast_track_migration_prewrite', return_value=False), \
             patch.object(authority, 'api_get', side_effect=get), \
             authority.evidence_lookup_budget(budget[0], clock=budget[1]):
            result = authority.fast_track_child_attempts('owner/repo', 'migrations', 'fixture')
        expected = [(i, a) for i in range(1, 13) for a in (1, 2)]
        self.assertEqual(expected, [(r['id'], a) for r, a, job, step in result])
        self.assertEqual(expected, sorted(seen))
        self.assertEqual(6, peak)

    def test_worker_failure_never_returns_partial_evidence(self):
        with patch.object(authority, 'fast_track_execution_history', return_value=[dict(id=1, status='completed', head_sha='a'*40, run_attempt=1)]), \
             patch.object(authority, '_release_history_source', side_effect=authority.EvidenceLookupUnavailable('unavailable')):
            with self.assertRaisesRegex(authority.EvidenceLookupUnavailable, 'unavailable'):
                authority.fast_track_child_attempts('owner/repo', 'migrations', 'fixture')

    def test_deadline_wait_cancels_pending_work_without_unbounded_join(self):
        import concurrent.futures
        pool = Mock()
        future = pool.submit.return_value
        future.result.side_effect = concurrent.futures.TimeoutError()
        with patch.object(authority, 'fast_track_execution_history', return_value=[dict(id=1)]), \
             patch.object(authority.concurrent.futures, 'ThreadPoolExecutor', return_value=pool), \
             authority.evidence_lookup_budget(12, clock=lambda: 10):
            with self.assertRaises(authority.EvidenceLookupUnavailable):
                authority.fast_track_child_attempts('owner/repo', 'migrations', 'fixture')
        future.result.assert_called_once_with(timeout=2)
        pool.shutdown.assert_called_once_with(wait=False, cancel_futures=True)


class AllAttemptPrewriteScopeTests(unittest.TestCase):
    def setUp(self):
        self.run = dict(id=7, head_sha='a'*40, run_attempt=2, status='completed', conclusion='failure',
            event='push', head_branch=authority.FAST_TRACK_PR_BASE,
            path='.github/workflows/'+authority.FAST_TRACK_RELEASE_WORKFLOW,
            head_repository={'full_name':'owner/repo'})
        self.steps = [dict(name=name, status='completed', conclusion='skipped') for name in (
            'Apply or reconcile the exact rehearsed migration bundle',
            'Reconcile fresh already-ready schema without SQL bundle or mutation')]
        self.payloads = {}
        for attempt in (1, 2):
            jobs = [dict(name=name, status='completed', conclusion=result, run_id=7,
                run_attempt=attempt, head_sha='a'*40) for name, result in (
                ('Select exact affected applications', 'success'),
                ('Admit exact validated fast-track source', 'success'),
                ('Reconcile production schema before publication', 'failure'),
                ('Affected App Deploy — ${{ matrix.target }}', 'skipped'),
                ('Publish verified website routing', 'skipped'))]
            jobs[2]['steps'] = [dict(step) for step in self.steps]
            self.payloads[attempt] = dict(total_count=5, jobs=jobs)

    def invoke(self, source='canonical'):
        def get(repo, path, token):
            return self.payloads[int(path.split('/')[4])]
        with patch.object(authority, '_release_history_source', return_value=source), \
             patch.object(authority, 'git_show_file', return_value='canonical'), \
             patch.object(authority, 'named_step_blocks', return_value={s['name']: 'body' for s in self.steps}), \
             patch.object(authority, 'api_get', side_effect=get):
            return authority.fast_track_attempts_proven_prewrite('owner/repo', self.run, 'b'*40, 'fixture')

    def test_complete_two_attempt_positive_nonentry_allows_scope_only(self):
        self.assertTrue(self.invoke())

    def test_any_entered_unknown_missing_or_duplicated_mutation_step_blocks(self):
        import copy
        original = copy.deepcopy(self.payloads)
        for attempt in (1, 2):
            for state in ('failure', 'success', 'cancelled', None):
                with self.subTest(attempt=attempt, state=state):
                    self.payloads = copy.deepcopy(original)
                    self.payloads[attempt]['jobs'][2]['steps'][0]['conclusion'] = state
                    self.assertFalse(self.invoke())
            for change in ('missing', 'duplicate', 'active'):
                self.payloads = copy.deepcopy(original)
                steps = self.payloads[attempt]['jobs'][2]['steps']
                if change == 'missing': steps.pop()
                elif change == 'duplicate': steps.append(dict(steps[0]))
                else: steps[0]['status'] = 'in_progress'
                self.assertFalse(self.invoke())

    def test_incomplete_foreign_active_or_started_publication_evidence_blocks(self):
        import copy
        original = copy.deepcopy(self.payloads)
        changes = [lambda p: p.update(total_count=6), lambda p: p['jobs'].pop(),
            lambda p: p['jobs'][0].update(run_id=8), lambda p: p['jobs'][0].update(head_sha='c'*40),
            lambda p: p['jobs'][0].update(run_attempt=1), lambda p: p['jobs'][0].update(status='in_progress'),
            lambda p: p['jobs'][3].update(conclusion='success'), lambda p: p['jobs'][4].update(conclusion='failure')]
        for change in changes:
            self.payloads = copy.deepcopy(original)
            change(self.payloads[2])
            self.assertFalse(self.invoke())
        self.payloads = original
        self.assertFalse(self.invoke(source='different executable'))
        self.run['status'] = 'in_progress'
        self.assertFalse(self.invoke())

    def effective_baseline(self):
        before, head, original = 'a'*40, 'b'*40, 'c'*40
        def get(repo, path, token):
            # This predecessor fixture has no completion-observer runs.
            if path == 'actions/workflows/deployment-diagnostics.yml/runs?event=workflow_dispatch&per_page=100':
                return dict(total_count=0, workflow_runs=[])
            if path == f"actions/runs/{self.run['id']}":
                return dict(self.run)
            if path.startswith('actions/runs?'):
                rows = [self.run]
                return dict(total_count=len(rows), workflow_runs=rows)
            if '/attempts/' in path:
                return self.payloads[int(path.split('/')[4])]
            if '/jobs?' in path:
                return self.payloads[2]
            if '/artifacts?' in path:
                return dict(total_count=0, artifacts=[])
            if path.startswith('commits/'):
                return [dict(merged_at='time', merge_commit_sha=before,
                    base=dict(ref=authority.FAST_TRACK_PR_BASE), head=dict(repo=dict(full_name='owner/repo')))]
            raise AssertionError(path)
        with patch.dict(os.environ, GITHUB_REPOSITORY='owner/repo', GITHUB_TOKEN='fixture'), \
             patch.object(authority, 'api_get', side_effect=get), \
             patch.object(authority, '_api_get_uncached', side_effect=get), \
             patch.object(authority, '_release_history_source', return_value='canonical'), \
             patch.object(authority, 'git_show_file', return_value='canonical'), \
             patch.object(authority, 'named_step_blocks', return_value={s['name']: 'body' for s in self.steps}), \
             patch.object(authority, 'fast_track_untriggered_repair_parent', return_value=None), \
             patch.object(authority.subprocess, 'run', return_value=SimpleNamespace(returncode=0)), \
             patch.object(authority.subprocess, 'check_output', return_value=before+' '+original+' '+head):
            return authority.fast_track_effective_baseline(before, head)

    def test_two_attempt_recovery_failure_carries_unpublished_scope_through_canonical_owner(self):
        self.assertEqual('c'*40, self.effective_baseline())

    def test_latest_skipped_step_cannot_hide_prior_entered_attempt_during_scope_carry(self):
        self.payloads[1]['jobs'][2]['steps'][0]['conclusion'] = 'failure'
        with self.assertRaisesRegex(RuntimeError, 'REQUIRES_OPERATION_RECONCILIATION'):
            self.effective_baseline()

    def test_evidence_read_error_is_not_positive_nonentry(self):
        with patch.object(authority, '_release_history_source', side_effect=authority.EvidenceLookupUnavailable('deadline')):
            with self.assertRaises(authority.EvidenceLookupUnavailable):
                authority.fast_track_attempts_proven_prewrite('owner/repo', self.run, 'b'*40, 'fixture')


class HistoricalCanonicalNoWriteTests(unittest.TestCase):
    def setUp(self):
        import copy
        self.copy = copy.deepcopy
        revision = '93fda2026f5ec2e088af6dab014f006be5118467'
        self.sources = {path: authority.git_show_file(revision, path) for path in (
            'scripts/release-migration.py', '.github/workflows/legend-fast-track-ui.yml')}
        self.run = dict(id=123, head_sha=revision, head_branch=authority.FAST_TRACK_PR_BASE,
                        path='.github/workflows/' + authority.FAST_TRACK_RELEASE_WORKFLOW,
                        event='push', head_repository={'full_name': 'test/repo'}, run_attempt=2)
        self.step = dict(name=authority.FAST_TRACK_CHILD_STEPS['migrations'][1], number=13,
                         status='completed', conclusion='success',
                         started_at='2026-10-11T20:47:56Z', completed_at='2026-10-11T20:49:36Z')
        self.job = dict(id=22, name=authority.FAST_TRACK_CHILD_STEPS['migrations'][0],
                        run_id=123, run_attempt=2, head_sha=revision, status='completed',
                        conclusion='success', steps=[self.step])
        self.original = self.copy(self.job); self.original.update(id=11, run_attempt=1)
        self.admitted = dict(id=10, name='Admit exact validated fast-track source', run_id=123,
                             run_attempt=1, head_sha=revision, status='completed', conclusion='success',
                             steps=[dict(name='Authenticate merged source and preserved required validation',
                                         status='completed', conclusion='success')])
        self.record = dict(candidate='c'*40, executionAuthority=revision, databaseIdentity='d'*64,
                           schemaIdentity='e'*64, baselineIdentity='f'*64, observedUtc='2026-10-11T20:49:30Z',
                           pendingCount=0, mutationActivity='settled', historicalExecution='outcome-unknown',
                           historicalEvidenceIdentity='1'*64, historicalReconciliationSources=[],
                           schemaVersion=1, phase='observation', kind='current-schema-no-write',
                           bundleDigest='2'*64, sqlExecutionAuthorized=False, producingRun=123, producingAttempt=1)
        self.observation = dict(id=33, expired=False, digest='sha256:'+'3'*64,
                                workflow_run=dict(id=123, head_sha=revision))
        self.admission = dict(id=34, expired=False, name='fast-track-admission-123-1', digest='sha256:'+'4'*64,
                              workflow_run=dict(id=123, head_sha=revision))
        self.proof = dict(candidate='c'*40, executionAuthority=revision, sourcePr=55, validation={})
        self.artifacts = [self.observation, self.admission]
        self.seal()

    def seal(self):
        self.record.pop('observationIdentity', None)
        identity = hashlib.sha256(json.dumps(self.record, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
        self.record['observationIdentity'] = identity
        self.observation['name'] = 'legend-migration-observation-' + identity

    def invoke(self):
        def api(repo, path, token):
            rows = [self.original, self.admitted]
            return dict(total_count=len(rows), jobs=rows)
        def receipt(repo, run_id, artifact, filename, **kwargs):
            self.assertTrue(kwargs['verify_digest'])
            return self.record if artifact['id'] == 33 else self.proof
        with patch.object(authority, '_release_history_source', side_effect=lambda repo, sha, path, token: self.sources[path]), \
             patch.object(authority, 'api_get', side_effect=api), \
             patch.object(authority, '_release_history_json', side_effect=receipt):
            return authority.attested_fast_track_no_write_observation('test/repo', self.run, 2, self.job,
                self.step, self.artifacts, 'token', {'databaseIdentity': 'd'*64})

    def test_original_observation_authenticates_exact_carried_execution(self):
        self.assertTrue(self.invoke())

    def test_changed_main_or_workflow_denies(self):
        for path in self.sources:
            with self.subTest(path=path):
                original = self.sources[path]
                self.sources[path] += '\nprint("changed")\n' if path.endswith('.py') else '\n  migrations:\n    steps: []\n'
                self.assertFalse(self.invoke()); self.sources[path] = original

    def test_changed_original_attempt_or_execution_interval_denies(self):
        self.original['run_attempt'] = 2
        self.assertFalse(self.invoke())
        self.original['run_attempt'] = 1
        self.original['steps'][0]['completed_at'] = '2026-10-11T20:49:35Z'
        self.assertFalse(self.invoke())

    def test_future_observation_or_changed_database_denies(self):
        for key, value in [('observedUtc', '2026-10-11T20:50:00Z'), ('databaseIdentity', '0'*64),
                           ('producingAttempt', 3), ('pendingCount', 1), ('sqlExecutionAuthorized', True)]:
            with self.subTest(key=key):
                old = self.record[key]; self.record[key] = value; self.seal()
                self.assertFalse(self.invoke()); self.record[key] = old; self.seal()

    def test_duplicate_observation_is_ambiguous(self):
        self.artifacts.append(self.copy(self.observation))
        self.assertFalse(self.invoke())

    def test_candidate_and_admission_carry_are_bound(self):
        self.proof['candidate'] = '0'*40
        self.assertFalse(self.invoke())
        self.proof['candidate'] = 'c'*40; self.admitted['run_id'] = 456
        self.assertFalse(self.invoke())

    def test_unhashed_or_unknown_record_fields_deny(self):
        self.record['observedUtc'] = '2026-10-11T20:49:31Z'
        with self.assertRaisesRegex(RuntimeError, 'identity mismatch'): self.invoke()
        self.seal(); self.record['unknown'] = True; self.seal()
        with self.assertRaisesRegex(RuntimeError, 'schema unproven'): self.invoke()

    def test_digest_requires_immutable_artifact_and_matching_bytes(self):
        import io, zipfile
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError):
                authority._download_run_artifact('test/repo', 123, 'receipt', Path(directory), expected_digest='sha256:'+'0'*64)
            archive = io.BytesIO()
            with zipfile.ZipFile(archive, 'w') as contents: contents.writestr('operation.json', '{}')
            def download(*args, **kwargs):
                kwargs['stdout'].write(archive.getvalue()); return SimpleNamespace(returncode=0)
            with patch.object(authority.subprocess, 'run', side_effect=download):
                with self.assertRaisesRegex(RuntimeError, 'digest mismatch'):
                    authority._download_run_artifact('test/repo', 123, 'receipt', Path(directory),
                        artifact_id=33, expected_digest='sha256:'+'0'*64)
                authority._download_run_artifact('test/repo', 123, 'receipt', Path(directory),
                    artifact_id=33, expected_digest='sha256:'+hashlib.sha256(archive.getvalue()).hexdigest())
                self.assertEqual((Path(directory)/'operation.json').read_text(), '{}')

    def test_original_observation_failure_only_not_postwrite_verification(self):
        self.run['status'] = 'completed'
        failed = self.copy(self.original); failed['conclusion'] = 'failure'
        failed['steps'][0]['conclusion'] = 'failure'
        def invoke(marker, stamp='2026-10-11T20:49:30Z'):
            log = ('2026-10-11T20:40:00Z [command]/usr/bin/git log -1 --format=%H\n'
                   + '2026-10-11T20:40:00Z ' + self.run['head_sha'] + '\n' + stamp + ' ' + marker + '\n')
            with patch.object(authority, '_release_history_source', side_effect=lambda repo, sha, path, token: self.sources[path]), \
                 patch.object(authority, '_release_job_log', return_value=log):
                return authority._attested_fast_track_observation_failure('test/repo', self.run, [failed], 1, 'token')
        suffix = '; preserve prior evidence and reconcile without replay.'
        self.assertTrue(invoke('Database mutation activity remains active' + suffix))
        self.assertTrue(invoke('Migration stage unresolved: schema-observation' + suffix))
        self.assertFalse(invoke('Migration stage unresolved: schema-verification' + suffix))
        self.assertFalse(invoke('Migration stage unresolved: bundle-execution' + suffix))
        self.assertFalse(invoke('Database mutation activity remains active' + suffix, '2026-10-11T20:40:00Z'))
        failed['run_attempt'] = 2
        self.assertFalse(invoke('Database mutation activity remains active' + suffix))

    def test_first_write_uses_reader_but_preserves_prior_entered_attempt(self):
        def inventory(repo, path, token):
            if '/artifacts?' in path: return dict(artifacts=self.artifacts, total_count=len(self.artifacts))
            return dict(workflow_runs=[], total_count=0)
        attempts = [(self.run, 2, self.job, self.step)]
        with patch.object(authority, 'fast_track_child_attempts', return_value=attempts), \
             patch.object(authority, 'api_get', side_effect=inventory), \
             patch.object(authority, 'attested_fast_track_no_write_observation', return_value=True) as recognize:
            authority.release_child_first_write_proven('test/repo', 'migrations', 'a'*64, 'b'*64,
                456, 1, 'token', historical_observation={'databaseIdentity': 'd'*64})
            self.assertEqual(recognize.call_count, 1)
            attempts.insert(0, (self.run, 1, self.original, self.original['steps'][0]))
            recognize.side_effect = [False, True]
            with self.assertRaisesRegex(RuntimeError, 'missing intent'):
                authority.release_child_first_write_proven('test/repo', 'migrations', 'a'*64, 'b'*64,
                    456, 1, 'token', historical_observation={'databaseIdentity': 'd'*64})


class CurrentObservationPrewriteTests(unittest.TestCase):
    def setUp(self):
        import copy
        self.copy = copy.deepcopy
        self.revision = 'b91b52c3d3b093d5d2c8034613a76575654082f4'
        self.sources = {path: authority.git_show_file(self.revision, path) for path in (
            'scripts/release-migration.py', '.github/workflows/legend-fast-track-ui.yml')}
        self.run = dict(id=7, head_sha=self.revision, run_attempt=1, status='completed', conclusion='failure',
            event='push', head_branch=authority.FAST_TRACK_PR_BASE,
            path='.github/workflows/'+authority.FAST_TRACK_RELEASE_WORKFLOW,
            head_repository={'full_name':'owner/repo'})
        self.jobs = [dict(id=10+i, name=name, status='completed', conclusion=result, run_id=7,
            run_attempt=1, head_sha=self.revision) for i, (name, result) in enumerate((
                ('Select exact affected applications', 'success'),
                ('Admit exact validated fast-track source', 'success'),
                ('Reconcile production schema before publication', 'failure'),
                ('Affected App Deploy — ${{ matrix.target }}', 'skipped'),
                ('Publish verified website routing', 'skipped')))]
        self.jobs[2]['steps'] = [dict(name=name, status='completed', conclusion=result,
             started_at='2026-10-20T21:15:27Z', completed_at='2026-10-20T21:17:28Z') for name,result in (
             ('Apply or reconcile the exact rehearsed migration bundle','failure'),
             ('Reconcile fresh already-ready schema without SQL bundle or mutation','skipped'))]
        self.marker = 'Database mutation activity remains active'

    def log(self):
        return ('2026-10-20T21:00:00Z [command]/usr/bin/git log -1 --format=%H\n'
            + '2026-10-20T21:00:00Z '+self.revision+'\n2026-10-20T21:17:28.007Z '+self.marker
            + '; preserve prior evidence and reconcile without replay.\n')

    def invoke(self, **kwargs):
        with patch.object(authority,'_release_history_source',side_effect=lambda repo,sha,path,token:self.sources[path]), \
             patch.object(authority,'_release_job_log',return_value=self.log()):
            return authority.attested_fast_track_migration_prewrite('owner/repo',self.run,self.jobs,'fixture',**kwargs)

    def test_completed_latest_default_and_exact_initial_marker(self):
        self.assertTrue(self.invoke())
        self.marker='Migration stage unresolved: schema-observation'
        self.assertTrue(self.invoke())
        for marker in ('Migration stage unresolved: schema-verification','Migration stage unresolved: bundle-execution'):
            self.marker=marker;self.assertFalse(self.invoke())

    def test_current_contract_does_not_expand_historical_no_write_reader(self):
        with patch.object(authority,'_release_history_source',side_effect=lambda repo,sha,path,token:self.sources[path]):
            self.assertFalse(authority._audited_fast_track_observer_source('owner/repo',self.run,'fixture'))
            self.assertTrue(authority._audited_current_fast_track_observation_failure_source('owner/repo',self.run,'fixture'))
        self.sources['scripts/release-migration.py']+='\nprint("changed")\n'
        self.assertFalse(self.invoke())

    def test_active_parent_requires_exact_current_context_and_earlier_attempt(self):
        self.run.update(status='in_progress',run_attempt=2,conclusion=None)
        self.assertFalse(self.invoke(attempt=1))
        self.assertFalse(self.invoke(attempt=1,current_run=8,current_attempt=2))
        self.assertFalse(self.invoke(attempt=1,current_run=7,current_attempt=3))
        self.assertTrue(self.invoke(attempt=1,current_run=7,current_attempt=2))
        self.jobs[2]['run_attempt']=2
        self.assertFalse(self.invoke(attempt=1,current_run=7,current_attempt=2))

    def test_canonical_inventory_accepts_original_self_attempt_but_rejects_external_active_worker(self):
        self.run.update(status='in_progress',run_attempt=2,conclusion=None)
        with patch.object(authority,'_release_history_source',side_effect=lambda repo,sha,path,token:self.sources[path]), \
             patch.object(authority,'_release_job_log',return_value=self.log()), \
             patch.object(authority,'fast_track_execution_history',return_value=[self.run]), \
             patch.object(authority,'api_get',return_value=dict(total_count=5,jobs=self.jobs)):
            self.assertEqual([],authority.fast_track_child_attempts('owner/repo','migrations','fixture',current_run=7,current_attempt=2))
            with self.assertRaisesRegex(RuntimeError,'WORKER_ACTIVE'):
                authority.fast_track_child_attempts('owner/repo','migrations','fixture',current_run=8,current_attempt=2)

    def test_all_attempt_scope_carry_requires_every_original_attempt_prewrite(self):
        self.run['run_attempt']=2
        latest=self.copy(self.jobs)
        for job in latest:job['run_attempt']=2
        latest[2]['steps'][0]['conclusion']='skipped'
        payloads={1:self.jobs,2:latest}
        with patch.object(authority,'_release_history_source',side_effect=lambda repo,sha,path,token:self.sources[path]), \
             patch.object(authority,'git_show_file',return_value=self.sources['.github/workflows/legend-fast-track-ui.yml']), \
             patch.object(authority,'_release_job_log',side_effect=lambda *args:self.log()), \
             patch.object(authority,'api_get',side_effect=lambda repo,path,token:dict(total_count=5,jobs=payloads[int(path.split('/')[4])])):
            self.assertTrue(authority.fast_track_attempts_proven_prewrite('owner/repo',self.run,'c'*40,'fixture'))
            self.marker='Migration stage unresolved: schema-verification'
            self.assertFalse(authority.fast_track_attempts_proven_prewrite('owner/repo',self.run,'c'*40,'fixture'))

    def test_completed_initial_failure_carries_scope_through_effective_baseline(self):
        def api(repo,path,token):
            # This predecessor fixture has no completion-observer runs.
            summary = authority.fast_track_recovery_summary_name(self.run['id'], self.revision)
            completion = authority.fast_track_completion_summary_name(self.run['id'], self.revision)
            if path in {f'actions/artifacts?name={name}&per_page=100' for name in (summary,completion)}:
                return dict(total_count=0, artifacts=[])
            if path == 'actions/workflows/deployment-diagnostics.yml/runs?event=workflow_dispatch&per_page=100':
                return dict(total_count=0, workflow_runs=[])
            if path == f"actions/runs/{self.run['id']}":
                return dict(self.run)
            if path == f"actions/runs/{self.run['id']}/artifacts?per_page=100":
                return dict(total_count=0, artifacts=[])
            if path.startswith('actions/runs?'):
                rows=[self.run]
                return dict(total_count=len(rows),workflow_runs=rows)
            if '/jobs?' in path:return dict(total_count=5,jobs=self.jobs)
            if path.startswith('commits/'):
                return [dict(merged_at='time',merge_commit_sha=self.revision,base=dict(ref=authority.FAST_TRACK_PR_BASE),
                             head=dict(repo=dict(full_name='owner/repo')))]
            raise AssertionError(path)
        with patch.dict(os.environ,GITHUB_REPOSITORY='owner/repo',GITHUB_TOKEN='fixture'), \
             patch.object(authority,'api_get',side_effect=api), \
             patch.object(authority,'_api_get_uncached',side_effect=api), \
             patch.object(authority,'fast_track_unpublished_apps_with_migration_intent',return_value=False), \
             patch.object(authority,'_release_history_source',side_effect=lambda repo,sha,path,token:self.sources[path]), \
             patch.object(authority,'_release_job_log',side_effect=lambda *args:self.log()), \
             patch.object(authority,'fast_track_untriggered_repair_parent',return_value=None), \
             patch.object(authority.subprocess,'run',return_value=SimpleNamespace(returncode=0)), \
             patch.object(authority.subprocess,'check_output',return_value=self.revision+' '+'c'*40+' '+'d'*40):
            self.assertEqual('c'*40,authority.fast_track_effective_baseline(self.revision,'d'*40))
            self.marker='Migration stage unresolved: schema-verification'
            with self.assertRaisesRegex(RuntimeError,'REQUIRES_OPERATION_RECONCILIATION'):
                authority.fast_track_effective_baseline(self.revision,'d'*40)

class UnpublishedAppsWithMigrationIntentTests(unittest.TestCase):
    def setUp(self):
        import copy
        self.copy = copy.deepcopy
        self.run = dict(id=701, head_sha='a'*40, status='completed', conclusion='failure', run_attempt=3,
            event='push',path='.github/workflows/'+authority.FAST_TRACK_RELEASE_WORKFLOW,
            head_branch=authority.FAST_TRACK_PR_BASE,head_repository=dict(full_name='o/r'))
        self.names = ['Select exact affected applications','Admit exact validated fast-track source',
            'Reconcile production schema before publication','Affected App Deploy — ${{ matrix.target }}',
            'Publish verified website routing']
        self.mutation = 'Apply or reconcile the exact rehearsed migration bundle'
        self.no_sql = 'Reconcile fresh already-ready schema without SQL bundle or mutation'
        self.jobs = {}
        for attempt in (1,2,3):
            rows = [dict(name=n,run_id=701,head_sha='a'*40,run_attempt=attempt,status='completed',
                conclusion=('success' if i<2 else 'failure' if i==2 else 'skipped'),steps=[])
                for i,n in enumerate(self.names)]
            rows[2]['steps'] = [dict(name=self.mutation,status='completed',conclusion='failure' if attempt==1 else 'skipped'),
                               dict(name=self.no_sql,status='completed',conclusion='skipped')]
            self.jobs[attempt] = dict(jobs=rows,total_count=5)
        self.artifact = dict(id=801,name='legend-release-child-intent-'+'b'*64,expired=False,
            workflow_run=dict(id=self.run['id'],head_sha=self.run['head_sha']))
        self.inventory = dict(artifacts=[self.artifact],total_count=1)
        self.record = dict(child='migrations',producingAttempt=1,producingRun=701,phase='intent',
            executionAuthority='a'*40,applicationRevision='a'*40,partitionIdentity='c'*64)

    def api(self, repository, path, token):
        # This predecessor fixture has no completion-observer runs.
        summary = authority.fast_track_recovery_summary_name(self.run['id'], self.run['head_sha'])
        completion = authority.fast_track_completion_summary_name(self.run['id'], self.run['head_sha'])
        if path in {f'actions/artifacts?name={name}&per_page=100' for name in (summary,completion)}:
            return dict(total_count=0, artifacts=[])
        if path == 'actions/workflows/deployment-diagnostics.yml/runs?event=workflow_dispatch&per_page=100':
            return dict(total_count=0, workflow_runs=[])
        if path == f"actions/runs/{self.run['id']}":return dict(self.run)
        if '/attempts/' in path:return self.jobs[int(path.split('/attempts/')[1].split('/')[0])]
        if '/artifacts?' in path:return self.inventory
        if path == 'actions/runs?branch=Legend%2Ffast-track-release&per_page=100':
            return dict(workflow_runs=[self.run],total_count=1)
        if path.endswith('/jobs?filter=latest&per_page=100'):return self.jobs[3]
        if path.startswith('commits/'):
            return [dict(merged_at='date',merge_commit_sha='a'*40,base=dict(ref=authority.FAST_TRACK_PR_BASE),
                         head=dict(repo=dict(full_name='o/r')))]
        raise AssertionError(path)

    def context(self):
        from contextlib import ExitStack
        stack=ExitStack()
        for name,kwargs in [
            ('is_fast_track_producer',dict(return_value=True)),
            ('_release_history_source',dict(return_value='immutable workflow')),
            ('git_show_file',dict(return_value='immutable workflow')),
            ('named_step_blocks',dict(return_value={self.mutation:'block',self.no_sql:'block'})),
            ('api_get',dict(side_effect=self.api)),
            ('_api_get_uncached',dict(side_effect=self.api)),
            ('_release_history_json',dict(return_value=self.record)),
            ('_validate_child_generation',dict(return_value='b'*64))]:
            stack.enter_context(patch.object(authority,name,**kwargs))
        return stack

    def invoke(self):
        return authority.fast_track_unpublished_apps_with_migration_intent('o/r',self.run,'d'*40,'token')

    def test_scope_only_accounts_original_intent_all_attempts(self):
        with self.context():
            self.assertTrue(self.invoke())
            authority._release_history_json.assert_called_once_with('o/r',701,self.artifact,'operation.json',verify_digest=True)
            authority._validate_child_generation.assert_called_once()

    def test_every_original_publication_owner_must_be_skipped(self):
        original=self.copy(self.jobs)
        with self.context():
            for attempt in (1,2,3):
                for index in (3,4):
                    for conclusion in ('success','failure','cancelled',None):
                        self.jobs=self.copy(original);self.jobs[attempt]['jobs'][index]['conclusion']=conclusion
                        self.assertFalse(self.invoke())
            self.jobs=self.copy(original);self.jobs[1]['jobs'].pop();self.assertFalse(self.invoke())

    def test_current_worker_changed_source_and_inventory_gap_deny(self):
        with self.context():
            self.run['status']='in_progress';self.assertFalse(self.invoke());self.run['status']='completed'
            self.jobs[2]['total_count']=6;self.assertFalse(self.invoke());self.jobs[2]['total_count']=5
            with patch.object(authority,'git_show_file',return_value='changed workflow'):self.assertFalse(self.invoke())
            self.jobs[1]['jobs'][3]['head_sha']='e'*40;self.assertFalse(self.invoke())

    def test_missing_duplicate_changed_or_unverified_intent_deny(self):
        with self.context():
            for key,value in [('producingAttempt',2),('producingRun',702),('phase','success'),('applicationRevision','e'*40),('child','portal')]:
                old=self.record[key];self.record[key]=value;self.assertFalse(self.invoke());self.record[key]=old
            self.inventory['artifacts']=[];self.inventory['total_count']=0;self.assertFalse(self.invoke())
            self.inventory['artifacts']=[self.artifact,self.artifact];self.inventory['total_count']=2;self.assertFalse(self.invoke())
            self.inventory['artifacts']=[self.artifact];self.inventory['total_count']=1
            with patch.object(authority,'_validate_child_generation',side_effect=RuntimeError('unverified generation')):
                with self.assertRaisesRegex(RuntimeError,'unverified generation'):self.invoke()

    def test_no_sql_entry_and_unaccounted_mutation_attempt_deny(self):
        with self.context():
            self.jobs[2]['jobs'][2]['steps'][0]['conclusion']='failure';self.assertFalse(self.invoke())
            self.jobs[2]['jobs'][2]['steps'][0]['conclusion']='skipped'
            self.jobs[2]['jobs'][2]['steps'][1]['conclusion']='success';self.assertFalse(self.invoke())

    def test_effective_baseline_carries_only_unpublished_scope(self):
        with self.context(),patch.dict(os.environ,{'GITHUB_REPOSITORY':'o/r','GITHUB_TOKEN':'token'}), \
             patch.object(authority,'fast_track_completed_apps_pending_routing',return_value=False), \
             patch.object(authority,'attested_fast_track_manual_dns_proxy_completion',return_value=False), \
             patch.object(authority,'attested_fast_track_brand_preupload',return_value=False), \
             patch.object(authority,'fast_track_attempts_proven_prewrite',return_value=False), \
             patch.object(authority,'fast_track_completed_sql_unpublished',return_value=False), \
             patch.object(authority,'fast_track_untriggered_repair_parent',return_value=None), \
             patch.object(authority.subprocess,'run',return_value=SimpleNamespace(returncode=0)), \
             patch.object(authority.subprocess,'check_output',return_value='a'*40+' '+'c'*40+' '+'d'*40):
            self.assertEqual('c'*40,authority.fast_track_effective_baseline('a'*40,'d'*40))
            self.jobs[1]['jobs'][3]['conclusion']='failure'
            with self.assertRaisesRegex(RuntimeError,'REQUIRES_OPERATION_RECONCILIATION'):
                authority.fast_track_effective_baseline('a'*40,'d'*40)

    def test_pending_with_existing_intent_still_denies_mutation(self):
        with patch.object(migration,'journal_type',return_value=Mock(return_value=SimpleNamespace(intent=self.record,success=None))), \
             patch.dict(os.environ,{'RELEASE_RESOURCE_GROUP':'group','DATABASE_AUTHORITY':'app'}):
            with self.assertRaisesRegex(RuntimeError,'child-history'):
                migration.mutation_admission(dict(ready=False,schemaIdentity='c'*64),'b'*64)



class FounderProtectSupersessionTests(unittest.TestCase):
    def setUp(self):
        # Fixed original request: tool-only promotions must not change this fixture.
        self.request=json.loads(r'''
{
  "requestedBy": "Founder",
  "authorization": "Founder ENG-FORMS-RELEASE-MANUAL-HANDOFF-20261023:v1 continuing combined release as new baseline. Codex sole implementer, Astra independent acceptance. Preserve mandatory human MYLEGND Production review, current-head checks, migration and recovery evidence, and candidate-specific writer fencing. No new spend or access changes. Superseding Founder instruction authorizes completely replacing the previous Protect release despite its UNKNOWN outcome; no further search for its missing receipt.",
  "purpose": "Complete canonical checkout inquiry and scoped training through existing billing/payment owners; preserve all prior Studio/forms/receptionist changes. Training remains default-off until all affected consumers are compatible. Old Protect UNKNOWN history is retained without replay. Use candidate-bound one-time supersession admission; retain all other production gates.",
  "requestId": "founder-training-checkout-forward-baseline-20261026-v1",
  "releaseMode": "approved-only",
  "targets": [
    "masterapp-portal",
    "masterapp-client",
    "masterapp-protect",
    "masterapp-parfait",
    "masterapp-website"
  ],
  "cloudflareWebsiteRouting": false,
  "preserveLiveTargets": false,
  "protectSupersession": {
    "authorizationId": "ENG-FORMS-RELEASE-MANUAL-HANDOFF-20261023:v1:protect-replacement-20261026",
    "authorizedBy": "Founder",
    "target": "protect",
    "originalRun": 40095849719,
    "originalOperationId": "1618e118093e091b4eb6852d90514e0d156d8d53a6513d991ff8e4803d8bf1cd",
    "originalRevision": "7dcfe753f90ee559bc80a5350d6012779cb36426",
    "expectedReleaseParent": "95d534023a59fda7d231fca69cafe84d10038520",
    "historicalOutcome": "UNKNOWN",
    "authorizeOneReplacement": true,
    "rollbackArtifactId": 12561256924,
    "rollbackArtifactDigest": "sha256:03321a68f9e5f39ff237c93def0d683b633f938710bf35053c2ade48db0803ca"
  }
}
''')
        self.revision='a'*40
        self.parent=self.request['protectSupersession']['expectedReleaseParent']

    def invoke(self, request=None, parent=None):
        with patch.object(authority.subprocess,'check_output',return_value=f'{self.revision} {parent or self.parent} '+ 'b'*40), patch.object(authority.subprocess,'run',return_value=SimpleNamespace(returncode=0)):
            return authority.fast_track_protect_supersession(self.revision,request=self.request if request is None else request)

    def test_explicit_replacement_preserves_unknown_and_binds_candidate(self):
        before=json.dumps(self.request,sort_keys=True)
        proof=self.invoke()
        self.assertEqual(self.revision,proof['candidateRevision'])
        self.assertEqual('UNKNOWN',proof['historicalOutcome'])
        self.assertEqual(before,json.dumps(self.request,sort_keys=True))

    def test_wrong_operation_target_or_authorization_denied(self):
        for key,value in [('target','portal'),('originalRun',1),('originalOperationId','f'*64),('originalRevision','c'*40),('authorizeOneReplacement',False),('historicalOutcome','success'),('rollbackArtifactId',1)]:
            with self.subTest(key=key):
                request=json.loads(json.dumps(self.request));request['protectSupersession'][key]=value
                with self.assertRaisesRegex(ValueError,'AUTHORIZATION_CHANGED'):self.invoke(request)

    def test_later_release_cannot_reuse_authorization(self):
        with self.assertRaisesRegex(ValueError,'CANDIDATE_CHANGED'):self.invoke(parent='c'*40)

    def test_missing_authorization_does_not_change_existing_admission(self):
        self.request.pop('protectSupersession')
        self.assertIsNone(self.invoke())

    def test_selector_uses_only_authorized_baseline_and_keeps_source_gate(self):
        proof=self.invoke();policy=Mock();policy.tool_promotion_requested.return_value=False
        policy.read_request.return_value=self.request
        with patch.object(authority,'_fast_track_policy',return_value=policy), patch.object(authority,'fast_track_protect_supersession',return_value=proof), patch.object(authority,'fast_track_source_admission') as source, patch.object(authority,'fast_track_effective_baseline',side_effect=AssertionError('No old receipt search')) as old, patch.object(authority.subprocess,'check_output',return_value='Infrastructure/Infrastructure.csproj\n'), patch.object(authority,'attested_website_only_recovery_scope',return_value=False), patch.object(authority,'fast_track_pending_routing',return_value=False):
            result=authority.fast_track_selection(self.parent,self.revision)
            self.assertEqual(proof['originalRevision'],result['baseline'])
            self.assertTrue(result['schema']);self.assertTrue(result['commerce'])
            source.assert_called_once();old.assert_not_called()
            source.side_effect=RuntimeError('required validation missing')
            with self.assertRaisesRegex(RuntimeError,'required validation missing'):
                authority.fast_track_selection(self.parent,self.revision)
            with self.assertRaisesRegex(ValueError,'PARENT_CHANGED'):
                authority.fast_track_selection('d'*40,self.revision)

    def test_active_provider_blocks_before_rollback_or_write(self):
        proof=self.invoke();azure=Mock()
        with patch.object(deploy._RELEASE_AUTHORITY,'fast_track_protect_supersession',return_value=proof), patch.object(deploy,'read_deployments_bounded',return_value=[{'status':1}]), patch.object(deploy._RELEASE_AUTHORITY,'_download_run_artifact') as download:
            with self.assertRaisesRegex(deploy.DeploymentReconciliationRequired,'PROVIDER_ACTIVE'):
                deploy.prepare_protect_replacement(self.revision,proof['originalRevision'],azure)
            download.assert_not_called();azure.submit.assert_not_called()

    def test_changed_live_baseline_blocks_replacement(self):
        proof=self.invoke()
        with patch.object(deploy._RELEASE_AUTHORITY,'fast_track_protect_supersession',return_value=proof),patch.object(deploy,'read_deployments_bounded') as read:
            with self.assertRaisesRegex(deploy.DeploymentDrift,'LIVE_BASELINE_CHANGED'):
                deploy.prepare_protect_replacement(self.revision,'c'*40,Mock())
            read.assert_not_called()

if __name__ == '__main__':unittest.main()
