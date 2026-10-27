#!/usr/bin/env python3
import importlib.util
import hashlib
import json
import os
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("validation_resume", ROOT / "scripts" / "validation-resume.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)



class ParfaitPushCoverageTests(unittest.TestCase):
    def setUp(self):
        self.sha = 'a' * 40
        self.repo = 'MYLEGND/masterapp'
        self.branch = 'isolation/example'
        self.pr = dict(number=691, state='open',
            head=dict(sha=self.sha, ref=self.branch, repo=dict(full_name=self.repo)),
            base=dict(ref=m.FAST_TRACK_PR_BASE, sha='b'*40, repo=dict(full_name=self.repo)))
        self.run = dict(id=99, run_attempt=1, event='pull_request', head_sha=self.sha,
            path='.github/workflows/parfait-isolated-validation.yml',
            head_repository=dict(full_name=self.repo), pull_requests=[self.pr],
            status='in_progress', conclusion=None)
        self.job = dict(id=100, name='build-and-focused-tests', status='queued', conclusion=None)

    def api(self, repository, path, token):
        if path.startswith('git/ref/heads/'): return dict(object=dict(sha='b'*40))
        if path.startswith('commits/'): return [self.pr]
        if path.startswith('actions/workflows/'): return dict(workflow_runs=[self.run])
        if '/jobs?' in path: return dict(jobs=[self.job])
        if path == 'pulls/691': return self.pr
        raise AssertionError(path)

    def plan(self, event='push'):
        with patch.object(m, 'api_get', side_effect=self.api):
            return m.parfait_push_coverage(self.repo, self.sha, self.branch, event, 'fixture')

    def test_exact_active_pr_lane_delegates_only_scheduling(self):
        result = self.plan()
        self.assertTrue(result['skip'])
        self.assertFalse(result['validationAccepted'])
        self.assertEqual(99, result['sourceRun'])
        self.assertFalse(self.plan('pull_request')['skip'])

    def test_no_pr_or_unavailable_lookup_runs_push(self):
        for result in ([], None, [None], [dict(head=None)], [dict(state="open",head=dict(repo=None))]):
            with patch.object(m, 'api_get', return_value=result):
                self.assertFalse(m.parfait_push_coverage(self.repo,self.sha,self.branch,'push','fixture')['skip'])
        with patch.object(m, 'api_get', side_effect=RuntimeError('unavailable')):
            self.assertFalse(m.parfait_push_coverage(self.repo,self.sha,self.branch,'push','fixture')['skip'])

    def test_wrong_identity_failed_cancelled_skipped_or_missing_job_runs_push(self):
        for row, field, value in ((self.pr['head'],'sha','b'*40),
            (self.pr['head']['repo'],'full_name','other/repo'),
            (self.pr['base'],'ref','main'), (self.pr,'state','closed'),
            (self.pr['base'],'sha','c'*40),
            (self.run,'head_sha','b'*40),(self.run,'event','push'),
            (self.run,'path','.github/workflows/other.yml'),
            (self.job,'name','other')):
            old=row[field]; row[field]=value
            with self.subTest(field=field,value=value): self.assertFalse(self.plan()['skip'])
            row[field]=old
        for row in (self.run,self.job):
            for conclusion in ('failure','cancelled','skipped','neutral'):
                row.update(status='completed',conclusion=conclusion)
                self.assertFalse(self.plan()['skip'])
            row.update(status='in_progress',conclusion=None)

    def test_newer_failed_run_prevents_reusing_older_success(self):
        old=dict(self.run,id=98,status='completed',conclusion='success')
        self.run.update(status='completed',conclusion='failure')
        original=self.api
        def api(repo,path,token):
            if path.startswith('actions/workflows/'): return dict(workflow_runs=[old,self.run])
            return original(repo,path,token)
        with patch.object(m,'api_get',side_effect=api):
            self.assertFalse(m.parfait_push_coverage(self.repo,self.sha,self.branch,'push','fixture')['skip'])

    def test_workflow_keeps_required_pr_lane_and_separate_receipts(self):
        import yaml
        workflow=yaml.safe_load((ROOT/'.github/workflows/parfait-isolated-validation.yml').read_text())
        steps=workflow['jobs']['build-and-focused-tests']['steps']
        planner=next(s for s in steps if s.get('id')=='resume')
        self.assertIn('push_coverage.outputs.skip',planner['if'])
        for step in steps:
            if step.get('id','').startswith('gate_'):
                self.assertIn('push_coverage.outputs.skip',step['if'])
        record=next(s for s in steps if s.get('name')=='Record canonical child execution evidence')
        self.assertIn("steps.resume.outcome == 'success'",record['if'])


class NonpublishingPreparationTests(unittest.TestCase):
    def setUp(self):
        self.sha = 'b' * 40
        self.branch = 'isolation/receptionist-quality-pro-20261024'
        self.pr = dict(number=691, state='open', merged_at=None, draft=True,
            head=dict(sha=self.sha, ref=self.branch, repo=dict(full_name='MYLEGND/masterapp')),
            base=dict(ref=m.FAST_TRACK_PR_BASE, repo=dict(full_name='MYLEGND/masterapp')))
        self.mutate_run = lambda row: None
        self.api_calls = 0

    def api(self, repo, path, token):
        if path == 'pulls/691':
            self.api_calls += 1
            return self.pr
        workflow = path.split('/')[2]
        run = dict(id=17, run_attempt=1, head_sha=self.sha, event='pull_request',
            path='.github/workflows/' + workflow, head_repository=dict(full_name=repo),
            status='completed', conclusion='success', pull_requests=[self.pr])
        self.mutate_run(run)
        return dict(total_count=1, workflow_runs=[run])

    def plan(self, **changes):
        args=dict(repository='MYLEGND/masterapp', revision=self.sha, current_sha=self.sha,
            branch=self.branch, preparation_only=True)
        args.update(changes)
        with patch.dict(os.environ, {'GITHUB_TOKEN':'fixture'}), \
             patch.object(m, 'api_get', side_effect=self.api), \
             patch.object(m, 'approved_head_preflight', return_value=dict(current=True)):
            return m.compute_package_preparation_plan(**args)

    def test_exact_green_draft_prepares_fixed_combined_pair(self):
        plan=self.plan()
        self.assertEqual(['protect','parfait'], plan['components'])
        self.assertEqual('legend-nonpublishing-package-preparation.v2',plan['schema'])
        self.assertFalse(plan['publicationAuthorized'])
        self.assertFalse(plan['releaseAdmission'])
        self.assertEqual(3, len(plan['validationRuns']))
        self.assertEqual(2, self.api_calls)
        self.assertFalse(m.package_artifact_match('nonpublishing-protect-parfait-preparation-' + self.sha))

    def test_rejects_invalid_dispatch_and_pr_binding(self):
        for changes in ({'preparation_only':False}, {'revision':'bad'},
                        {'current_sha':'c'*40}, {'branch':m.TRUSTED_PR_BASE},
                        {'repository':'attacker/repo'}):
            with self.subTest(changes=changes), self.assertRaises(ValueError): self.plan(**changes)
        for field,value in [('state','closed'),('merged_at','today'),('draft',False)]:
            old=self.pr[field];self.pr[field]=value
            with self.assertRaises(ValueError): self.plan()
            self.pr[field]=old
        self.pr['head']['repo']['full_name']='attacker/repo'
        with self.assertRaises(ValueError): self.plan()

    def test_failed_wrong_head_or_unbound_ci_never_prepares(self):
        for change in (dict(conclusion='failure'), dict(status='in_progress'),
                       dict(head_sha='c'*40), dict(pull_requests=[])):
            self.mutate_run=lambda row: row.update(change)
            with self.subTest(change=change), self.assertRaises(ValueError): self.plan()

    def test_workflow_has_no_provider_or_promotion_in_preparation_lane(self):
        import yaml
        workflow=yaml.safe_load((ROOT / '.github/workflows/masterapp-platform-architecture-validation.yml').read_text())
        jobs=workflow['jobs'];lane=jobs['nonpublishing-protect-parfait-preparation']
        self.assertTrue(all(value=='read' for value in lane['permissions'].values()))
        text=json.dumps(lane)
        for prohibited in ('azure/login', 'id-token', 'environment', 'wake-release', 'deploy-approved-app', 'secrets.'):
            self.assertNotIn(prohibited,text)
        self.assertIn('build-component --revision',text)
        self.assertIn('--component protect',text)
        self.assertIn('--component parfait',text)
        for name in ('approved-head-preflight','validate',
                     'wake-release-lifecycle-after-package-backfill','validated-migration-probe'):
            self.assertIn('!inputs.preparation_only',jobs[name]['if'])

    def test_combined_manifest_requires_both_verified_receipts(self):
        import yaml
        workflow=yaml.safe_load((ROOT / '.github/workflows/masterapp-platform-architecture-validation.yml').read_text())
        step=next(s for s in workflow['jobs']['nonpublishing-protect-parfait-preparation']['steps']
                  if s.get('name')=='Bind verified bytes to nonpublishing preparation')
        body=step['run']
        code=body[body.index("root=pathlib.Path"):body.rindex('PYPREPARE')]
        for fault in (None, 'missing', 'mismatch'):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as tmp:
                root=Path(tmp)/'combined-preparation';root.mkdir()
                original=self.plan();(root/'preparation.json').write_text(json.dumps(original))
                for component in ('protect','parfait'):
                    if component=='parfait' and fault=='missing': continue
                    (root/(component+'.component.json')).write_text(json.dumps(dict(file=component+'.zip',sha256='a'*64)))
                calls=[]
                def verify(revision,component,directory):
                    self.assertEqual(self.sha,revision);calls.append(component)
                    if component=='parfait' and fault=='mismatch': raise ValueError('receipt mismatch')
                context=dict(pathlib=__import__('pathlib'),json=json,os=os,package=SimpleNamespace(verify_component=verify))
                with patch.dict(os.environ,dict(RUNNER_TEMP=tmp,GITHUB_SHA=self.sha,GITHUB_RUN_ID='1',GITHUB_RUN_ATTEMPT='1')):
                    if fault:
                        with self.assertRaises((ValueError,FileNotFoundError)): exec(code,context)
                        self.assertEqual(original,json.loads((root/'preparation.json').read_text()))
                    else:
                        exec(code,context)
                        result=json.loads((root/'preparation.json').read_text())
                        self.assertEqual({'protect','parfait'},set(result['packages']))
                        self.assertFalse(result['publicationAuthorized'])
                self.assertEqual(['protect','parfait'],calls)


class PackageDescriptorIntegrationTests(unittest.TestCase):
    def setUp(self):
        self.identity='a'*64;self.revision='b'*40
        self.name='founder-diagnostics-packages-'+self.identity+'-71-a2'
        self.result=dict(artifact=self.name,packageIdentity=self.identity,runId=71,revision=self.revision,reusable=True)
        self.run=dict(id=71,run_attempt=2)
        self.payload=dict(id=101,name=self.name,digest='sha256:'+'c'*64,expired=False)
        self.descriptor=dict(id=102,name='validated-package-source-'+self.identity+'-71-a2',expired=False)
        steps=['Build immutable validated release package','Verify immutable validated release package','Record immutable package source']
        self.job=dict(id=501,name='validated-release-package',status='completed',conclusion='success',
            steps=[dict(name=name,conclusion='success') for name in steps])
        self.material={}
        for key in m.RELEASE_TARGETS:
            self.material[key]=dict(producerRevision='d'*40,packageDigest='e'*64,contentIdentity='f'*64,executionIdentity='1'*64,
                source=dict(runId=61,runAttempt=1,artifactId=91,artifactDigest='sha256:'+'2'*64,
                    artifactName=m.component_attempt_name(key,'f'*64,61,1),receiptArtifactId=92,producingJobId=401))
        self.source=dict(schemaVersion=1,packageSchema='legend-validated-release-package.v2',revision=self.revision,
            packageIdentity=self.identity,runId=71,runAttempt=2,manifestSha256='3'*64,targets=self.material,
            artifact={key:self.payload[key] for key in ('id','name','digest')})
    def invoke(self):
        def api(repo,path,token):
            if '/artifacts?' in path:return dict(artifacts=[self.payload,self.descriptor],total_count=2)
            return dict(jobs=[self.job],total_count=1)
        with patch.object(m,'trusted_component_run',return_value=True),patch.object(m,'api_get',side_effect=api), \
             patch.object(m,'_release_job_log',return_value='authenticated upload log'), \
             patch.object(m,'component_upload_proven') as proof, \
             patch.object(m,'_release_history_json',return_value=self.source):
            result=m.attach_package_descriptor('owner/repo',self.result,self.run,'placeholder')
            self.assertEqual(2,proof.call_count)
            return result
    def test_descriptor_supplies_actual_producers_without_downloading_packages(self):
        result=self.invoke()
        self.assertEqual(self.material,result['targetMaterials'])
        self.assertEqual(101,result['artifactId']);self.assertEqual(102,result['descriptorArtifactId'])
    def test_substituted_artifact_cannot_be_admitted(self):
        self.source['artifact']['id']=999
        with self.assertRaisesRegex(ValueError,'descriptor producer/material mismatch'):self.invoke()
    def test_failed_producer_is_not_a_valid_descriptor(self):
        self.job['conclusion']='failure'
        with self.assertRaises(m.EvidenceLookupUnavailable):self.invoke()
    def test_expired_component_restores_identical_aggregate_without_rebuild(self):
        material=self.material['portal'];producer=material['producerRevision'];release=m.RELEASE_TARGETS['portal']['releaseName']
        receipt=dict(schemaVersion=3,applicationReleaseSha=self.revision,packageIdentity=self.identity,
            target=release,transaction='committed-or-preserved',liveProven=True,authority='post-live-verification',targetMaterial=material)
        run=dict(id=99,path='.github/workflows/'+m.DIRECT_RELEASE_WORKFLOW,head_branch=m.TRUSTED_PR_BASE,
            status='completed',conclusion='success',head_repository=dict(full_name='owner/repo'))
        aggregate=dict(self.result,artifactId=101,targetMaterials=self.material)
        with patch.dict(m.os.environ,GITHUB_TOKEN='placeholder'), \
             patch.object(m,'_artifact_rows',return_value=[dict(id=901,workflow_run=dict(id=99))]), \
             patch.object(m,'api_get',return_value=run),patch.object(m,'_release_history_json',return_value=receipt), \
             patch.object(m,'compatible_component_evidence',return_value=dict(state='required')), \
             patch.object(m,'compute_validated_package_evidence',return_value=aggregate) as lookup:
            result=m.compute_rollback_evidence('owner/repo',producer,'portal')
            self.assertEqual(101,result['artifactId']);self.assertEqual(material['packageDigest'],result['packageDigest'])
            self.assertEqual('identical_target_bytes_restored_from_authenticated_aggregate',result['reason'])
            lookup.assert_called_once_with('owner/repo',self.revision,self.identity,allow_equivalent=False)
            aggregate['targetMaterials']=dict(self.material,portal=dict(material,packageDigest='0'*64))
            with self.assertRaises(m.EvidenceLookupUnavailable):
                m.compute_rollback_evidence('owner/repo',producer,'portal')

    def test_failed_later_aggregate_preserves_successful_prior_attempt(self):
        prior=self.name.replace('-a2','-a1')
        run=dict(self.run,head_sha=self.revision)
        def attach(repo,result,actual_run,token):
            if result['artifact']==self.name:
                raise m.IncompletePackageAttempt('terminal failed descriptor writer')
            return dict(result,artifactId=77)
        with patch.object(m,'api_get',return_value=dict(workflow_runs=[run])), \
             patch.object(m,'_trusted_lineage_run',return_value=True), \
             patch.object(m,'git_changed',return_value=[]), \
             patch.object(m,'_run_artifact_names',return_value=[prior,self.name]), \
             patch.object(m,'_successful_package_child',side_effect=AssertionError('Latest attempt is not child evidence')), \
             patch.object(m,'attach_package_descriptor',side_effect=attach) as verify:
            result=m.compatible_package_producer('owner/repo',self.revision,'placeholder')
            self.assertEqual(prior,result['artifact']);self.assertEqual(77,result['artifactId'])
            self.assertEqual(2,verify.call_count)
            for error in (m.EvidenceLookupUnavailable('provider outage'),ValueError('tampered descriptor')):
                verify.side_effect=error;verify.reset_mock()
                with self.assertRaises(type(error)):
                    m.compatible_package_producer('owner/repo',self.revision,'placeholder')
                self.assertEqual(1,verify.call_count)

    def test_legacy_name_retains_original_strict_contract(self):
        old=dict(self.result,artifact='founder-diagnostics-packages-'+self.identity)
        with patch.object(m,'api_get',side_effect=AssertionError('Legacy evidence reinterpreted')):
            self.assertEqual(old,m.attach_package_descriptor('owner/repo',old,self.run,'placeholder'))


class PartialReleaseRollbackTests(unittest.TestCase):
    def setUp(self):
        self.revision = 'a' * 40; self.identity = 'b' * 64; self.operation = 'c' * 64
        self.run = dict(id=88, run_attempt=1, head_sha='d' * 40, event='workflow_dispatch',
            path='.github/workflows/' + m.DIRECT_RELEASE_WORKFLOW, head_branch=m.TRUSTED_PR_BASE,
            status='completed', conclusion='failure', head_repository=dict(full_name='owner/repo'))
        self.state_artifact = dict(id=91, name=f'legend-release-step-state-{self.revision}-88-1')
        self.success_artifact = dict(id=92, name='legend-release-operation-success-' + self.operation)
        self.state = dict(schemaVersion=2, applicationReleaseSha=self.revision, runId=88, runAttempt=1,
            packageIdentity=self.identity, selectedTargets=['masterapp-portal', 'masterapp-protect'])
        self.record = dict(schemaVersion=1, phase='success', operationId=self.operation, target='portal',
            applicationRevision=self.revision, producingRun=88, producingAttempt=1,
            packageDigest='e' * 64, packageProducerRun=77)
        self.package = dict(reusable=True, revision=self.revision, packageIdentity=self.identity,
            runId=77, artifact='founder-diagnostics-packages-' + self.identity)
        self.artifacts = [self.state_artifact, self.success_artifact]
        self.inventory_count = None
        self.producer = self.revision
        self.success = self.record

    def invoke(self, app='portal'):
        def api(repo, path, token):
            if '/artifacts?' in path:
                return dict(artifacts=self.artifacts, total_count=(len(self.artifacts)
                    if self.inventory_count is None else self.inventory_count))
            return dict(jobs=[dict(name='release', status='completed', conclusion='failure')], total_count=1)
        def read(repo, run_id, artifact, filename):
            return self.state if filename == 'legend-release-step-state.json' else self.record
        with patch.object(m, '_release_history_runs', return_value=[self.run]), \
             patch.object(m, '_release_history_api', side_effect=api), \
             patch.object(m, '_release_history_json', side_effect=read), \
             patch.object(m, '_release_attempt_package_revision', return_value=self.producer), \
             patch.object(m, 'release_operation_history', return_value=self.success), \
             patch.object(m, 'compute_validated_package_evidence', return_value=self.package) as package:
            result = m._partial_release_rollback_evidence('owner/repo', self.revision, app,
                dict(revision=self.revision, app=app, reusable=False), 'placeholder')
            if result is not None:
                package.assert_called_once_with('owner/repo', self.revision, self.identity, allow_equivalent=False)
            return result

    def test_failed_parent_preserves_durable_successful_sibling_exact_bytes(self):
        result = self.invoke()
        self.assertTrue(result['reusable']); self.assertEqual(77, result['runId'])
        self.assertEqual(88, result['releaseRunId'])
        self.assertEqual(self.record['packageDigest'], result['packageDigest'])

    def test_unresolved_sibling_has_no_rollback_success(self):
        self.assertIsNone(self.invoke('protect'))

    def test_upload_intent_alone_cannot_authorize_rollback(self):
        self.success_artifact['name'] = 'legend-release-operation-intent-' + self.operation
        self.assertIsNone(self.invoke())

    def test_untrusted_or_running_release_is_not_evidence(self):
        for key, value in [('head_branch', 'feature'), ('event', 'pull_request'), ('status', 'in_progress')]:
            original = self.run[key]; self.run[key] = value
            self.assertIsNone(self.invoke()); self.run[key] = original

    def test_incomplete_inventory_and_wrong_producer_fail_closed(self):
        self.inventory_count = 3
        with self.assertRaises(m.EvidenceLookupUnavailable): self.invoke()
        self.inventory_count = None; self.producer = 'f' * 40
        with self.assertRaisesRegex(ValueError, 'producer mismatch'): self.invoke()

    def test_state_and_success_identity_must_bind_exact_attempt(self):
        self.state['runId'] = 99
        with self.assertRaisesRegex(ValueError, 'state identity'): self.invoke()
        self.state['runId'] = 88; self.record['applicationRevision'] = 'f' * 40
        with self.assertRaisesRegex(ValueError, 'durable success'): self.invoke()

    def test_missing_or_conflicting_durable_success_cannot_authorize_reuse(self):
        self.success = None
        with self.assertRaisesRegex(ValueError, 'success authority'): self.invoke()

    def test_equivalent_or_different_package_cannot_replace_live_bytes(self):
        for key, value in [('revision', 'f' * 40), ('runId', 99), ('packageIdentity', 'f' * 64), ('reusable', False)]:
            original = self.package[key]; self.package[key] = value
            with self.assertRaises(m.EvidenceLookupUnavailable): self.invoke()
            self.package[key] = original

    def test_mixed_producer_state_is_not_reinterpreted_as_legacy(self):
        self.state['schemaVersion'] = 3
        self.assertIsNone(self.invoke())


class LegacyRollbackReceiptTests(unittest.TestCase):
    def setUp(self):
        self.revision='a'*40; self.identity='b'*64
        self.receipt=dict(schemaVersion=1,applicationReleaseSha=self.revision,
            target=m.RELEASE_TARGETS['portal']['releaseName'],transaction='committed-or-preserved')
        self.run=dict(id=88,run_attempt=2,head_sha='c'*40,event='workflow_dispatch',
            path='.github/workflows/'+m.DIRECT_RELEASE_WORKFLOW,head_branch=m.TRUSTED_PR_BASE,
            status='completed',conclusion='success',head_repository=dict(full_name='owner/repo'))
        self.job=dict(name='release',status='completed',conclusion='success',steps=[
            dict(name=name,status='completed',conclusion='success') for name in (
                'Verify every deployed target and collect all failures',
                'Enforce complete direct deployment outcome')])
        self.link='legend-approved-package-link-'+self.revision+'-'+self.identity
        self.validated=dict(reusable=True,runId=77,revision=self.revision,
            artifact='founder-diagnostics-packages-'+self.identity,packageIdentity=self.identity)
        self.producer=self.revision

    def invoke(self):
        with patch.dict(m.os.environ,GITHUB_TOKEN='placeholder'), \
             patch.object(m,'_artifact_rows',return_value=[dict(id=901,name='receipt',workflow_run=dict(id=88))]), \
             patch.object(m,'api_get',return_value=self.run), \
             patch.object(m,'_release_history_json',return_value=self.receipt), \
             patch.object(m,'_release_history_api',return_value=dict(total_count=1,jobs=[self.job])), \
             patch.object(m,'_release_attempt_package_revision',return_value=self.producer) as proof, \
             patch.object(m,'_run_artifact_names',return_value={self.link}), \
             patch.object(m,'_release_history_runs',return_value=[]), \
             patch.object(m,'compute_validated_package_evidence',return_value=self.validated):
            result=m.compute_rollback_evidence('owner/repo',self.revision,'portal')
            if result['reusable']:
                proof.assert_called_once_with('owner/repo',self.run,2,self.job,'placeholder','portal')
            return result

    def test_original_receipt_preserves_authenticated_package_without_rebuild(self):
        result=self.invoke()
        self.assertTrue(result['reusable']);self.assertEqual(77,result['runId'])
        self.assertEqual(88,result['releaseRunId']);self.assertEqual(self.identity,result['packageIdentity'])

    def test_malformed_or_unversioned_legacy_receipts_cannot_authorize_reuse(self):
        original=dict(self.receipt)
        for delta in ({'schemaVersion':0},{'schemaVersion':True},{'applicationReleaseSha':'d'*40},
                      {'target':'masterapp-client'},{'transaction':'uploaded'},
                      {'targetMaterial':{}},{'extra':'unrecognized'}):
            with self.subTest(delta=delta):
                self.receipt=original|delta
                with self.assertRaises(ValueError):self.invoke()

    def test_skipped_cancelled_or_failed_live_proof_is_not_success(self):
        for conclusion in ('skipped','cancelled','failure'):
            with self.subTest(conclusion=conclusion):
                self.job['steps'][0]['conclusion']=conclusion
                with self.assertRaisesRegex(ValueError,'live verification'):self.invoke()

    def test_original_package_producer_must_match_live_revision(self):
        self.producer='d'*40
        with self.assertRaisesRegex(ValueError,'package producer mismatch'):self.invoke()

    def test_incomplete_or_active_release_is_not_legacy_proof(self):
        self.job['status']='in_progress'
        with self.assertRaisesRegex(ValueError,'successful release'):self.invoke()

    def test_wrong_package_link_does_not_restore_unrelated_bytes(self):
        self.link='legend-approved-package-link-'+'d'*40+'-'+self.identity
        self.assertFalse(self.invoke()['reusable'])

    def test_missing_validated_package_remains_unavailable(self):
        self.validated={'reusable':False}
        self.assertFalse(self.invoke()['reusable'])


class RehearsedPackagePlanTests(unittest.TestCase):
    def test_reused_bundle_must_match_exact_rehearsal_before_any_build(self):
        candidate,producer='a'*40,'b'*40
        source=dict(schemaVersion=1,candidateRevision=candidate,producerRevision=producer,packageIdentity='c'*64,
            runId=71,artifact='founder-diagnostics-packages-'+'c'*64,artifactId=81,
            artifactDigest='sha256:'+'d'*64,bundleDigest='e'*64)
        plan=dict(needed=False,reason='dependency_equivalent_immutable_package_producer')
        readiness=dict(receipt=dict(pendingCount=1,rehearsal=dict(bundleDigest='f'*64,selectedPackage=source)))
        with patch.object(m,'compute_validated_package_evidence') as lookup:
            with self.assertRaisesRegex(ValueError,'digest mismatch'):
                m.bind_rehearsed_package_plan('owner/repo',candidate,plan,readiness,'placeholder')
            lookup.assert_not_called()
        readiness['receipt']['rehearsal']['bundleDigest']='e'*64
        preserved=dict(reusable=True,revision=producer,runId=71,artifact=source['artifact'],packageIdentity='c'*64)
        artifact=dict(id=81,name=source['artifact'],digest=source['artifactDigest'],expired=False,workflow_run=dict(id=71))
        def exact_api(repo,path,token):
            if path == 'actions/runs/71':return dict(id=71,head_sha=producer)
            if path == 'actions/artifacts/81':return artifact
            raise AssertionError('Unexpected unpinned lookup: '+path)
        with patch.object(m,'package_inputs_compatible',return_value=True), \
             patch.object(m,'compute_validated_package_evidence',side_effect=AssertionError('A newer aggregate B must not replace rehearsed A')), \
             patch.object(m,'_trusted_lineage_run',return_value=True), \
             patch.object(m,'_successful_package_child',return_value=True), \
             patch.object(m,'api_get',side_effect=exact_api):
            result=m.bind_rehearsed_package_plan('owner/repo',candidate,plan,readiness,'placeholder')
            self.assertFalse(result['needed']);self.assertEqual(producer,result['evidenceHeadSha'])
            self.assertEqual(71,result['exactPackageRunId'])
            artifact['digest']='sha256:'+'0'*64
            with self.assertRaisesRegex(m.EvidenceLookupUnavailable,'unavailable or changed'):
                m.bind_rehearsed_package_plan('owner/repo',candidate,plan,readiness,'placeholder')
        readiness['receipt']['rehearsal'].pop('selectedPackage')
        with self.assertRaisesRegex(ValueError,'no exact rehearsed'):
            m.bind_rehearsed_package_plan('owner/repo',candidate,plan,readiness,'placeholder')
        fresh=dict(needed=True)
        self.assertIs(fresh,m.bind_rehearsed_package_plan('owner/repo',candidate,fresh,readiness,'placeholder'))
        self.assertIs(plan,m.bind_rehearsed_package_plan('owner/repo',candidate,plan,dict(receipt=dict(pendingCount=0)),'placeholder'))


class ComponentPlanIntegrationTests(unittest.TestCase):
    def test_missing_matrix_preserves_successful_children(self):
        def select(repo,revision,key,token,**kwargs):
            return dict(state='required' if key=='portal' else 'reused-success',reason='fixture')
        with patch.object(m,'compatible_component_evidence',side_effect=select) as selector:
            result=m.plan_component_children('owner/repo','a'*40,12,'placeholder')
        self.assertEqual(['portal'],result['requiredComponents'])
        self.assertEqual(set(m.RELEASE_TARGETS)-{'portal'},set(result['reusedComponents']))
        self.assertEqual(len(m.RELEASE_TARGETS),selector.call_count)

    def test_evidence_outage_has_no_fallback_build_decision(self):
        with patch.object(m,'compatible_component_evidence',side_effect=m.EvidenceLookupUnavailable('outage')):
            with self.assertRaises(m.EvidenceLookupUnavailable):
                m.plan_component_children('owner/repo','a'*40,12,'placeholder')

    def test_bootstrap_owner_mismatch_does_not_activate_component_protocol(self):
        with patch.object(m,'git_show_file',side_effect=lambda revision,path:revision):
            self.assertFalse(m.component_protocol_ready('a'*40,'b'*40))
        with patch.object(m,'git_show_file',return_value='CONTENT_PACKAGE_SCHEMA approved owner'):
            self.assertTrue(m.component_protocol_ready('a'*40,'b'*40))

    def test_assembly_requires_all_authenticated_children_without_building(self):
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'sources.json'
            args=SimpleNamespace(repository='owner/repo',revision='a'*40,current_run_id=12,output=path)
            with patch.object(m,'plan_component_children',return_value=dict(requiredComponents=['portal'])):
                with self.assertRaisesRegex(RuntimeError,'COMPONENT_ASSEMBLY_BLOCKED'):
                    m.cmd_component_sources(args)
            self.assertFalse(path.exists())


class PackageComponentDependencyTests(unittest.TestCase):
    def setUp(self):
        self.files = {
            'Portal/Portal.csproj': '<Project><ItemGroup><ProjectReference Include="../Shared/Shared.csproj" /></ItemGroup></Project>',
            'Portal/page.cshtml': 'original view',
            'Portal/private.cs': 'original portal',
            'Client/Client.csproj': '<Project><ItemGroup><ProjectReference Include="../Shared/Shared.csproj" /><Content Include="../Portal/*.cshtml" /></ItemGroup></Project>',
            'Client/client.cs': 'original client',
            'Shared/Shared.csproj': '<Project />', 'Shared/shared.cs': 'original shared',
            'Website/package-lock.json': 'locked', 'Website/index.js': 'original static',
            'Directory.Build.props': '<Project />', 'global.json': '{"sdk":"pinned"}',
            'scripts/release-package.py': 'SCHEMA = "v1"\ndef build_component(): return 1\ndef assemble_components(): return 1\n',
            'scripts/validation-resume.py': 'PACKAGE_AUTHORITY_PATHS = ()\ndef package_component_manifest(): return 1\n',
            m.PACKAGE_BUILD_WORKFLOW: 'name: Build\njobs:\n  validated-release-package-components:\n    runs-on: pinned\n    steps:\n      - run: build-component\n  validated-release-package:\n    steps:\n      - run: aggregate\n',
        }
        self.targets = {key: dict(project=project, sourceRoot=root, static=static) for key, project, root, static in (
            ('portal','Portal/Portal.csproj','Portal',False), ('client','Client/Client.csproj','Client',False),
            ('website','static','Website',True))}

    def manifests(self, revision='a' * 40):
        import hashlib
        raw = ''.join('100644 blob ' + hashlib.sha1(content.encode()).hexdigest() + '\t' + path + '\0'
                      for path, content in sorted(self.files.items())).encode()
        with patch.object(m, 'RELEASE_TARGETS', self.targets), \
             patch.object(m.subprocess, 'check_output', return_value=raw), \
             patch.object(m, 'git_show_file', side_effect=lambda revision, path: self.files[path]):
            return {key: m.package_component_manifest(revision, key) for key in self.targets}

    def changed(self, before, after):
        return {key for key in before if before[key]['contentIdentity'] != after[key]['contentIdentity']}

    def test_new_revision_and_parent_aggregation_preserve_children(self):
        before = self.manifests()
        self.files[m.PACKAGE_BUILD_WORKFLOW] = self.files[m.PACKAGE_BUILD_WORKFLOW].replace('run: aggregate','run: revised-aggregate')
        self.files['scripts/release-package.py'] = self.files['scripts/release-package.py'].replace('assemble_components(): return 1','assemble_components(): return 2')
        after = self.manifests('b' * 40)
        self.assertEqual(set(), self.changed(before, after))
        self.assertTrue(all(row['reusable'] for row in after.values()))

    def test_linked_discovery_and_shared_dependency_close_real_consumers(self):
        before = self.manifests()
        self.files['Portal/new.cshtml'] = 'new linked view'
        self.assertEqual({'portal','client'}, self.changed(before, self.manifests()))
        before = self.manifests()
        self.files['Shared/shared.cs'] = 'changed shared'
        self.assertEqual({'portal','client'}, self.changed(before, self.manifests()))
        before = self.manifests()
        self.files['Portal/private.cs'] = 'changed portal only'
        self.assertEqual({'portal'}, self.changed(before, self.manifests()))
        before = self.manifests()
        del self.files['Portal/new.cshtml']
        self.assertEqual({'portal','client'}, self.changed(before, self.manifests()))

    def test_toolchain_and_authority_change_reject_affected_old_identity(self):
        before = self.manifests()
        self.files['global.json'] = '{"sdk":"changed"}'
        self.assertEqual({'portal','client'}, self.changed(before, self.manifests()))
        before = self.manifests()
        self.files['scripts/release-package.py'] = self.files['scripts/release-package.py'].replace('build_component(): return 1','build_component(): return 2')
        self.assertEqual(set(self.targets), self.changed(before, self.manifests()))

    def test_unknown_input_blocks_only_its_consumer_reuse(self):
        self.files['Client/Client.csproj'] = '<Project><Import Project="$(Unproven)/input.props" /></Project>'
        rows = self.manifests()
        self.assertFalse(rows['client']['reusable'])
        self.assertEqual(['unresolved_msbuild_input:Client/Client.csproj'], rows['client']['invalidationReasons'])
        self.assertTrue(rows['portal']['reusable'])
        self.assertTrue(rows['website']['reusable'])

    def test_cli_semantics_are_component_inputs(self):
        self.files['scripts/release-package.py'] += 'def main(): return build_component()\n'
        before = self.manifests()
        self.files['scripts/release-package.py'] = self.files['scripts/release-package.py'].replace(
            'def main(): return build_component()', 'def main():\n import os\n os.environ["BUILD_SETTING"] = "changed"\n return build_component()')
        self.assertEqual(set(self.targets), self.changed(before, self.manifests()))

    def test_unknown_sdk_property_and_custom_task_cannot_hide_external_input(self):
        for xml in ('<PropertyGroup><ApplicationManifest>../external.manifest</ApplicationManifest></PropertyGroup>',
                    '<Target Name="Custom"><CustomTask Input="../external.txt" /></Target>'):
            with self.subTest(xml=xml):
                self.files['Client/Client.csproj'] = '<Project>' + xml + '</Project>'
                rows = self.manifests()
                self.assertFalse(rows['client']['reusable'])
                self.assertTrue(rows['portal']['reusable'])
                self.assertTrue(rows['website']['reusable'])

    def test_checkout_and_compiler_config_discovery_invalidate_real_consumers(self):
        before = self.manifests()
        self.files['.gitattributes'] = '*.cs text eol=lf'
        self.assertEqual(set(self.targets), self.changed(before, self.manifests()))
        for path in ('.editorconfig', 'new.globalconfig', 'NuGet.config'):
            before = self.manifests()
            self.files[path] = 'new configuration'
            self.assertEqual({'portal','client'}, self.changed(before, self.manifests()))

    def test_nested_import_directory_and_new_project_ancestors_are_closed(self):
        self.files['Client/Client.csproj'] = '<Project><Import Project="build/input.props" /></Project>'
        self.files['Client/build/input.props'] = '<Project><Import Project="$(MSBuildThisFileDirectory)next.props" /></Project>'
        self.files['Client/build/next.props'] = '<Project><ItemGroup><ProjectReference Include="../../Extra/App.csproj" /></ItemGroup></Project>'
        self.files['Extra/App.csproj'] = '<Project />'
        self.files['Extra/Directory.Build.props'] = '<Project><ItemGroup><Content Include="../external.txt" /></ItemGroup></Project>'
        self.files['external.txt'] = 'original'
        before = self.manifests()
        self.assertTrue(before['client']['reusable'])
        self.assertIn('external.txt', before['client']['inputs'])
        self.files['external.txt'] = 'changed'
        self.assertEqual({'client'}, self.changed(before, self.manifests()))

    def test_static_external_inputs_are_recorded_without_claiming_executable_closure(self):
        self.files['Website/scripts/build.mjs'] = "import x from '../../Shared/external.mjs'; const x=resolve(repoRoot,'Design/tokens.json');"
        self.files['Shared/external.mjs'] = 'export default 1'
        self.files['Design/tokens.json'] = '{}'
        before = self.manifests()
        self.assertFalse(before['website']['reusable'])
        for path in ('Shared/external.mjs', 'Design/tokens.json'):
            self.files[path] += 'changed'
            self.assertIn('website', self.changed(before, self.manifests()))
        before = self.manifests()
        self.files['Portal/private.cs'] += 'changed'
        self.assertNotIn('website', self.changed(before, self.manifests()))


class ComponentEvidenceIntegrationTests(unittest.TestCase):
    def setUp(self):
        self.component, self.identity = 'portal', 'c' * 64
        self.producer, self.candidate = 'a' * 40, 'b' * 40
        self.index = dict(id=1, name=m.component_index_name(self.component, self.identity),
                          expired=False, workflow_run=dict(id=91))
        self.payload = dict(id=10, name=m.component_attempt_name(self.component,self.identity,91,2),
                            digest='sha256:'+'d'*64,expired=False)
        self.receipt_artifact = dict(id=11,name=m.component_attempt_name(self.component,self.identity,91,2,receipt=True),expired=False)
        self.receipt = dict(schemaVersion=1,component=self.component,contentIdentity=self.identity,
            producerRevision=self.producer,runId=91,runAttempt=2,sha256='e'*64,
            executionIdentity='f'*64,artifact={key:self.payload[key] for key in ('id','name','digest')})
        self.run = dict(id=91, head_sha=self.producer, run_attempt=2, status='completed', conclusion='failure',
            pull_requests=[dict(base=dict(ref=m.TRUSTED_PR_BASE),head=dict(repo=dict(full_name='owner/repo')))])
        names = ('Build immutable validated release package component','Preserve immutable validated release package component',
                 'Record immutable component source','Preserve immutable component source')
        self.job=dict(id=100,status='completed',conclusion='success',name='validated-release-package-components (portal)',
                      steps=[dict(name=name,conclusion='success') for name in names])
        self.artifacts=[self.index,self.payload,self.receipt_artifact]
        spec = importlib.util.spec_from_file_location('component_proof_test', ROOT / 'scripts/release-package.py')
        package = importlib.util.module_from_spec(spec);spec.loader.exec_module(package)
        paths = ('release-package.py','validation-resume.py','PackageRestoreProbe/PackageRestoreProbe.csproj','PackageRestoreProbe/Program.cs')
        tool_rows = {key:dict(image=image,imageId='sha256:'+'1'*64) for key,image in package.PACKAGE_TOOL_IMAGES.items()}
        for key in ('node','python'):
            tool_rows[key].update(paths=list(package.PACKAGE_TOOL_PATHS[key]),tree=dict(identity='2'*64))
        proof=dict(schema='legend-package-component-execution.v1',state='executed-success',component=self.component,
            candidateRevision=self.producer,source=dict(revision=self.producer),
            authorityInputs={path:hashlib.sha256(b'approved source').hexdigest() for path in paths},
            tools=dict(schemaVersion=1,platform='linux/amd64',tools=tool_rows),
            dependencyMaterial=dict(dotnet=dict(schemaVersion=1,identity='3'*64)),
            file=package.component_file(self.component),sha256=self.receipt['sha256'])
        with patch.object(package.subprocess,'check_output',return_value=b'approved source'):
            self.receipt['executionIdentity']=package.component_execution_identity(proof,self.producer,self.component)
        self.receipt['execution']=proof
        self.log=''
        for artifact,step_name in ((self.payload,'Preserve immutable validated release package component'),
                                  (self.receipt_artifact,'Preserve immutable component source')):
            step=next(row for row in self.job['steps'] if row['name']==step_name)
            step.update(started_at='2026-10-09T00:00:00Z',completed_at='2026-10-09T00:00:02Z')
            artifact['created_at']='2026-10-09T00:00:01Z'
            self.log+=f"2026-10-09T00:00:01Z Artifact {artifact['name']} has been successfully uploaded! Final size is 100 bytes. Artifact ID is {artifact['id']}\n"
        self.calls=[]
        self.outage=False
    def api(self, repository, endpoint, token):
        self.calls.append(endpoint)
        if self.outage: raise m.EvidenceLookupUnavailable('provider unavailable')
        if endpoint.startswith('actions/artifacts?'): return dict(total_count=1,artifacts=[self.index])
        if endpoint=='actions/runs/91':return self.run
        if endpoint=='actions/runs/91/artifacts?per_page=100':return dict(total_count=len(self.artifacts),artifacts=self.artifacts)
        if '/attempts/2/jobs?' in endpoint:return dict(total_count=1,jobs=[self.job])
        if '/attempts/1/jobs?' in endpoint:return dict(total_count=1,jobs=[dict(self.job,conclusion='failure')])
        raise AssertionError(endpoint)
    def select(self, **kwargs):
        with patch.object(m,'api_get',side_effect=self.api), \
             patch.object(m,'package_component_manifest',return_value=dict(contentIdentity=self.identity,reusable=True)), \
             patch.object(m,'trusted_component_run',return_value=True), \
             patch.object(m,'_release_job_log',return_value=self.log), \
             patch.object(m.subprocess,'check_output',return_value=b'approved source'), \
             patch.object(m,'_release_history_json',return_value=self.receipt):
            return m.compatible_component_evidence('owner/repo',self.candidate,self.component,'token',**kwargs)
    def test_exact_source_bypasses_locator_outage_without_relaxing_artifact_proof(self):
        original=self.api
        def no_index(repo,path,token):
            if path.startswith('actions/artifacts?'):
                raise m.EvidenceLookupUnavailable('index unavailable')
            return original(repo,path,token)
        self.api=no_index
        source=dict(runId=91,runAttempt=2,artifactId=10,artifactDigest=self.payload['digest'],
            artifactName=self.payload['name'],receiptArtifactId=11,producingJobId=100)
        self.assertEqual('reused-success',self.select(expected_source=source)['state'])
        self.assertFalse(any(path.startswith('actions/artifacts?') for path in self.calls))
        with self.assertRaisesRegex(ValueError,'immutable identity mismatch'):
            self.select(expected_source=dict(source,artifactId=999))

    def test_successful_child_survives_failed_parent_and_index_from_failed_attempt(self):
        self.artifacts.append(dict(id=8,name=m.component_attempt_name(self.component,self.identity,91,1,receipt=True),expired=False))
        result=self.select()
        self.assertEqual('reused-success',result['state'])
        self.assertEqual(2,result['sourceReceipt']['runAttempt'])
        self.assertEqual(10,result['sourceReceipt']['artifact']['id'])
        self.assertEqual(self.producer,result['compatibilityProof']['producerRevision'])
        self.assertEqual(self.candidate,result['compatibilityProof']['candidateRevision'])
        self.assertEqual(4,len(self.calls))
        self.assertEqual(1,self.index['id'])
    def test_failed_or_skipped_producer_never_establishes_success(self):
        for state in ('failure','skipped','cancelled'):
            with self.subTest(state=state):
                self.job['conclusion']=state
                self.assertEqual('required',self.select()['state'])
        self.job['conclusion']='success';self.job['steps'][0]['conclusion']='skipped'
        self.assertEqual('required',self.select()['state'])
    def test_substitution_and_receipt_attempt_mismatch_fail_closed(self):
        self.payload['digest']='sha256:'+'0'*64
        with self.assertRaisesRegex(ValueError,'substitution'):self.select()
        self.payload['digest']=self.receipt['artifact']['digest']
        self.receipt['runAttempt']=1
        with self.assertRaisesRegex(ValueError,'producer mismatch'):self.select()
    def test_missing_expired_payload_is_scoped_but_evidence_outage_cannot_select_build(self):
        self.payload['expired']=True
        self.assertEqual('required',self.select()['state'])
        self.outage=True
        with self.assertRaises(m.EvidenceLookupUnavailable):self.select()
    def test_changed_dependency_does_not_reuse_unrelated_identity(self):
        with patch.object(m,'api_get',side_effect=self.api), \
             patch.object(m,'package_component_manifest',side_effect=[dict(contentIdentity=self.identity,reusable=True),dict(contentIdentity='0'*64,reusable=True)]), \
             patch.object(m,'trusted_component_run',return_value=True):
            result=m.compatible_component_evidence('owner/repo',self.candidate,self.component,'token')
        self.assertEqual('required',result['state'])
        self.assertEqual(2,len(self.calls))


    def test_producer_uses_actual_approved_base_and_unmodified_authority(self):
        url='https://api.github.com/repos/owner/repo'
        self.run['pull_requests']=[dict(base=dict(ref=m.TRUSTED_PR_BASE,sha='0'*40,repo=dict(url=url)),
            head=dict(sha=self.producer,repo=dict(url=url)))]
        with patch.object(m,'_trusted_lineage_run',return_value=True), \
             patch.object(m.subprocess,'run',return_value=SimpleNamespace(returncode=0)) as ancestry, \
             patch.object(m,'git_show_file',return_value='approved executor') as source:
            self.assertTrue(m.trusted_component_run('owner/repo',self.run,self.candidate))
            source.side_effect=lambda revision,path: 'candidate executor' if revision==self.producer else 'approved executor'
            self.assertFalse(m.trusted_component_run('owner/repo',self.run,self.candidate))
            source.side_effect=None
            self.run['pull_requests'][0]['head']['sha']='9'*40
            self.assertTrue(m.trusted_component_run('owner/repo',self.run,self.candidate))
            self.assertIn(['git', 'merge-base', '--is-ancestor', self.producer, '9'*40], [call.args[0] for call in ancestry.call_args_list])
            ancestry.return_value=SimpleNamespace(returncode=1)
            self.assertFalse(m.trusted_component_run('owner/repo',self.run,self.candidate))
            ancestry.return_value=SimpleNamespace(returncode=0)
            self.run['pull_requests'][0]['head']['sha']='not-a-sha'
            self.assertFalse(m.trusted_component_run('owner/repo',self.run,self.candidate))
            self.run['pull_requests'][0]['head']['sha']=self.producer
            self.run['pull_requests'][0]['base']['ref']='unapproved'
            self.assertFalse(m.trusted_component_run('owner/repo',self.run,self.candidate))

    def test_advanced_pr_base_uses_only_approved_first_parent_ancestor(self):
        current, historical = '8'*40, '0'*40
        url='https://api.github.com/repos/owner/repo'
        self.run['pull_requests']=[dict(base=dict(ref=m.TRUSTED_PR_BASE,sha=current,repo=dict(url=url)),
            head=dict(sha=self.candidate,repo=dict(url=url)))]
        def ancestry(args, **kwargs):
            return SimpleNamespace(returncode=0 if args[-2:] in ([self.producer,self.candidate], [historical,self.producer]) else 1)
        with patch.object(m,'_trusted_lineage_run',return_value=True), \
             patch.object(m.subprocess,'run',side_effect=ancestry), \
             patch.object(m.subprocess,'check_output',return_value=current+'\n'+historical+'\n') as history, \
             patch.object(m,'git_show_file',return_value='approved executor') as source:
            self.assertTrue(m.trusted_component_run('owner/repo',self.run,self.candidate))
            history.assert_called_once_with(['git','rev-list','--first-parent',current],text=True)
            self.assertTrue(any(call.args[0]==historical for call in source.call_args_list))
            source.side_effect=lambda sha,path: 'tampered' if sha==self.producer else 'approved'
            self.assertFalse(m.trusted_component_run('owner/repo',self.run,self.candidate))
            source.side_effect=None
            history.return_value=current+'\n'
            self.assertFalse(m.trusted_component_run('owner/repo',self.run,self.candidate))

    def test_merged_run_recovers_commit_bound_pr_and_rejects_ambiguous_associations(self):
        url='https://api.github.com/repos/owner/repo'
        pull=dict(base=dict(ref=m.TRUSTED_PR_BASE,sha='0'*40,repo=dict(url=url)),
                  head=dict(sha=self.candidate,repo=dict(url=url)))
        self.run['pull_requests']=[]
        with patch.dict(os.environ, GITHUB_TOKEN='synthetic-test-token'), \
             patch.object(m,'api_get',return_value=[pull]) as api, \
             patch.object(m,'_trusted_lineage_run',return_value=True), \
             patch.object(m.subprocess,'run',return_value=SimpleNamespace(returncode=0)), \
             patch.object(m,'git_show_file',return_value='approved executor'):
            self.assertTrue(m.trusted_component_run('owner/repo',self.run,self.candidate))
            api.assert_called_once_with('owner/repo',f'commits/{self.producer}/pulls?per_page=100','synthetic-test-token')
            for rows in ([], [pull,pull], [dict(pull,base=dict(ref='unapproved'))]):
                api.return_value=rows
                self.assertFalse(m.trusted_component_run('owner/repo',self.run,self.candidate))
            api.return_value=[pull]*100
            with self.assertRaises(m.EvidenceLookupUnavailable):
                m.trusted_component_run('owner/repo',self.run,self.candidate)
            api.side_effect=m.EvidenceLookupUnavailable('provider unavailable')
            with self.assertRaises(m.EvidenceLookupUnavailable):
                m.trusted_component_run('owner/repo',self.run,self.candidate)

    def test_arbitrary_execution_hash_and_later_upload_are_rejected(self):
        original=self.receipt['executionIdentity']
        self.receipt['executionIdentity']='9'*64
        with self.assertRaisesRegex(ValueError,'execution identity mismatch'):self.select()
        self.receipt['executionIdentity']=original
        self.payload['created_at']='2026-10-09T00:01:00Z'
        with self.assertRaisesRegex(ValueError,'outside producing upload'):self.select()

    def test_fractional_upload_in_completion_second_is_valid_but_next_second_is_not(self):
        self.log=self.log.replace('T00:00:01Z','T00:00:02.999Z')
        self.assertEqual('reused-success',self.select()['state'])
        self.log=self.log.replace('T00:00:02.999Z','T00:00:03Z')
        with self.assertRaisesRegex(ValueError,'producing upload log'):self.select()

    def test_other_job_substitution_cannot_claim_original_upload(self):
        self.payload['id']=99
        self.receipt['artifact']['id']=99
        with self.assertRaisesRegex(ValueError,'producing upload log'):self.select()

    def test_dependency_and_pinned_tool_proof_cannot_be_fabricated(self):
        self.receipt['execution']['tools']['tools']['sdk']['image']='unapproved:latest'
        with self.assertRaisesRegex(ValueError,'pinned tool identity'):self.select()


class EarlyReadinessEvidenceTests(unittest.TestCase):
    def setUp(self):
        import datetime
        self.now = datetime.datetime(2026, 10, 9, tzinfo=datetime.timezone.utc)
        self.expected = {'schemaVersion': 1, 'identity': 'a' * 64}
        self.receipt = dict(self.expected, state='executed-success',
            observedUtc=self.now.isoformat(), databaseIdentity='b' * 64,
            baselineIdentity='f' * 64,
            schemaIdentity='c' * 64, contractDigest='d' * 64, pendingCount=0,
            mutationAdmission='not-required', deploymentReadiness='proven')
        self.repo = 'MYLEGND/masterapp'
        self.approved = '1' * 40
        self.candidate = '2' * 40
        repo = {'url': 'https://api.github.com/repos/' + self.repo}
        self.run = dict(id=42, event='pull_request_target', head_branch='repair/example',
            head_sha=self.candidate, head_repository={'full_name': self.repo},
            path='.github/workflows/legend-release-lifecycle.yml',
            pull_requests=[dict(head=dict(ref='repair/example', sha=self.candidate, repo=repo),
                                base=dict(ref=m.TRUSTED_PR_BASE, sha=self.approved, repo=repo))])

    def validate(self):
        return m.validate_readiness_receipt(self.receipt, self.expected, now=self.now)

    def test_failure_classification_requires_successful_exact_attempt_preservation(self):
        self.run['run_attempt'] = 2
        name = 'legend-readiness-failure-' + self.candidate + '-a2'
        artifact = dict(id=19, name=name, expired=False)
        upload = dict(name='Preserve classified readiness failure', status='completed', conclusion='success')
        producer = dict(name='readiness-observe', status='completed', conclusion='failure', steps=[upload])
        failure = dict(schemaVersion=1, candidate=self.candidate, executionAuthority=self.approved,
            producingRun=42, producingAttempt=2, classification='transient-provider-read')
        for fault in ('none', 'skipped-upload', 'failed-upload', 'wrong-job', 'successful-job', 'wrong-attempt', 'wrong-authority', 'missing-id'):
            import copy
            a, p, f = copy.deepcopy(artifact), copy.deepcopy(producer), dict(failure)
            if fault == 'skipped-upload': p['steps'][0]['conclusion'] = 'skipped'
            elif fault == 'failed-upload': p['steps'][0]['conclusion'] = 'failure'
            elif fault == 'wrong-job': p['name'] = 'candidate-controlled-name'
            elif fault == 'successful-job': p['conclusion'] = 'success'
            elif fault == 'wrong-attempt': f['producingAttempt'] = 1
            elif fault == 'wrong-authority': f['executionAuthority'] = 'f' * 40
            elif fault == 'missing-id': a.pop('id')
            with self.subTest(fault=fault), patch.object(m, 'trusted_readiness_run', return_value=True), \
                 patch.object(m, 'api_get', side_effect=[dict(total_count=1, artifacts=[a]), dict(total_count=1, jobs=[p])]), \
                 patch.object(m, '_release_history_json', return_value=f):
                if fault in ('wrong-attempt', 'wrong-authority'):
                    with self.assertRaisesRegex(ValueError, 'producer identity mismatch'):
                        m.readiness_failure_kind(self.repo, self.run, self.approved, 'token')
                else:
                    result = m.readiness_failure_kind(self.repo, self.run, self.approved, 'token')
                    self.assertEqual('transient-provider-read' if fault == 'none' else 'untrusted', result)

    def test_compatible_current_receipt_reused_without_execution(self):
        self.assertIs(self.receipt, self.validate())

    def test_skipped_failed_cancelled_blocked_do_not_prove_readiness(self):
        for state in ('skipped', 'failed', 'cancelled', 'blocked', 'not-required'):
            with self.subTest(state=state):
                self.receipt['state'] = state
                with self.assertRaisesRegex(ValueError, 'READINESS_NOT_SUCCESSFUL'):
                    self.validate()

    def test_expired_future_or_naive_time_rejected(self):
        for stamp, code in [('2026-10-08T23:44:59Z', 'EXPIRED'),
                            ('2026-10-09T00:00:31Z', 'EXPIRED'),
                            ('2026-10-09T00:00:00', 'TIME_UNPROVEN'), (None, 'TIME_UNPROVEN')]:
            with self.subTest(stamp=stamp):
                self.receipt['observedUtc'] = stamp
                with self.assertRaisesRegex(ValueError, code): self.validate()

    def test_missing_or_tampered_scope_rejected(self):
        self.receipt['identity'] = 'e' * 64
        with self.assertRaisesRegex(ValueError, 'INPUTS_CHANGED'): self.validate()

    def test_missing_count_cannot_hide_pending_migrations(self):
        for count in (None, -1, True, '0', 10001):
            with self.subTest(count=count):
                self.receipt['pendingCount'] = count
                with self.assertRaisesRegex(ValueError, 'PROOF_INCOMPLETE'): self.validate()

    def test_candidate_contract_cannot_omit_or_reclassify_approved_operations(self):
        import copy
        approved = {'schemaVersion': 1, 'migrations': [dict(id='20261007134500_AddFounderAssistantRules',
            supported=True, columns=[dict(name='Rules', type='nvarchar(max)', nullable=False, default='[]')])]}
        for kind in ('omit', 'invent', 'supported', 'type', 'nullable', 'default'):
            contract = copy.deepcopy(approved)
            if kind == 'omit': contract['migrations'] = []
            elif kind == 'invent': contract['migrations'][0]['id'] = '20261008134500_Fabricated'
            elif kind == 'supported': contract['migrations'][0]['supported'] = False
            else: contract['migrations'][0]['columns'][0][kind] = 'altered'
            with self.subTest(kind=kind), patch.object(m, 'migration_probe_identity', return_value={'runtimeIdentity': 'a'}):
                with self.assertRaisesRegex(ValueError, 'CANDIDATE_MIGRATION_CONTRACT_UNPROVEN'):
                    m.candidate_migration_contract_proven(self.candidate, self.approved, contract, approved)

    def test_changed_discovery_or_runtime_requires_reviewed_extraction_rule(self):
        with patch.object(m, 'migration_probe_identity', side_effect=[{'runtimeIdentity': 'new'}, {'runtimeIdentity': 'old'}]), patch.object(m, 'migration_contract_static_styles_only', return_value=False):
            with self.assertRaisesRegex(ValueError, 'dependency content changed'):
                m.candidate_migration_contract_proven(self.candidate, self.approved, {}, {})

    def test_static_style_rule_keeps_contract_and_tooling_checks(self):
        current = dict(runtimeIdentity='new', toolIdentity='tool', executionIdentity='exec')
        trusted = dict(current, runtimeIdentity='old')
        with patch.object(m, 'migration_probe_identity', side_effect=[current, trusted]), patch.object(m, 'migration_contract_static_styles_only', return_value=True):
            self.assertEqual('approved-definitions-static-styles-only', m.candidate_migration_contract_proven(self.candidate, self.approved, {}, {})['rule'])
        for changed in ('toolIdentity', 'executionIdentity'):
            with patch.object(m, 'migration_probe_identity', side_effect=[dict(current, **{changed: 'changed'}), trusted]), patch.object(m, 'migration_contract_static_styles_only', return_value=True):
                with self.assertRaisesRegex(ValueError, 'UNPROVEN'):
                    m.candidate_migration_contract_proven(self.candidate, self.approved, {}, {})
        with patch.object(m, 'migration_probe_identity', side_effect=[current, trusted]), patch.object(m, 'migration_contract_static_styles_only', return_value=True):
            with self.assertRaisesRegex(ValueError, 'metadata differs'):
                m.candidate_migration_contract_proven(self.candidate, self.approved, {'changed': True}, {})

    def test_static_style_delta_rejects_code_build_changes_and_symlinks(self):
        css = 'SHARED/wwwroot/css/dashboard-home-shared.css'
        def tree(path, oid, mode='100644'):
            return mode + ' blob ' + oid * 40 + '\t' + path + '\0'
        base = tree(css, 'a')
        for candidate, expected in (
            (tree(css, 'b'), True), (base, False),
            (tree(css, 'b', '120000'), False), ('', False),
            (tree(css, 'b') + tree('Infrastructure/Migrations/Test.cs', 'b'), False),
            (tree(css, 'b') + tree('Directory.Build.targets', 'b'), False),
            (tree(css, 'b') + tree('SHARED/Shared.csproj', 'b'), False),
            (tree(css, 'b') + tree('SHARED/wwwroot/payload.js', 'b'), False)):
            with self.subTest(candidate=candidate), patch.object(m.subprocess, 'check_output', side_effect=[base, candidate]):
                self.assertEqual(expected, m.migration_contract_static_styles_only(self.candidate, self.approved))

    def test_unchanged_migration_definitions_allow_new_candidate_without_metadata_trust(self):
        contract = {'schemaVersion': 1, 'migrations': []}
        with patch.object(m, 'migration_probe_identity', return_value={'runtimeIdentity': 'same'}):
            self.assertEqual('proven', m.candidate_migration_contract_proven(self.candidate, self.approved, contract, contract)['state'])

    def test_rehearsal_producer_inputs_are_independently_recomputed(self):
        import copy
        run = dict(self.run, event='pull_request', path='.github/workflows/' + m.PACKAGE_VALIDATION_WORKFLOW)
        expected = dict(self.expected, targets=['portal'])
        with patch.object(m, '_trusted_lineage_run', return_value=True), \
             patch.object(m.subprocess, 'run', return_value=SimpleNamespace(returncode=0)), \
             patch.object(m, 'readiness_identity', return_value=expected) as compute:
            self.assertTrue(m.trusted_rehearsal_run(self.repo, run, self.candidate, expected))
            self.assertEqual([(self.candidate, self.approved, ['portal']),
                              (self.approved, self.approved, ['portal'])], [call.args for call in compute.call_args_list])
            compute.return_value = dict(expected, identity='changed-semantics')
            self.assertFalse(m.trusted_rehearsal_run(self.repo, run, self.candidate, expected))
            compute.side_effect = [dict(expected, rehearsalExecutionIdentity='candidate-fabricated'),
                                   dict(expected, rehearsalExecutionIdentity='approved')]
            self.assertFalse(m.trusted_rehearsal_run(self.repo, run, self.candidate,
                dict(expected, rehearsalExecutionIdentity='candidate-fabricated')))
            compute.side_effect = None
            wrong = copy.deepcopy(run)
            wrong['pull_requests'][0]['base']['ref'] = 'unapproved'
            compute.reset_mock()
            self.assertFalse(m.trusted_rehearsal_run(self.repo, wrong, self.candidate, expected))
            compute.assert_not_called()

    def test_pending_requires_exact_rehearsal_inputs(self):
        self.receipt['pendingCount'] = 1
        self.receipt['mutationAdmission'] = 'proven'
        with self.assertRaisesRegex(ValueError, 'REHEARSAL_MISSING'): self.validate()
        self.receipt['rehearsal'] = dict(proven=True, contractDigest='d' * 64,
            schemaIdentity='c' * 64, baselineIdentity='f' * 64, bundleDigest='e' * 64)
        self.validate()
        self.receipt['rehearsal']['schemaIdentity'] = 'f' * 64
        with self.assertRaisesRegex(ValueError, 'REHEARSAL_MISSING'): self.validate()

    def test_actual_pr_target_metadata_authenticates_base_not_head(self):
        self.assertTrue(m.trusted_readiness_run(self.repo, self.run, self.approved))
        self.assertNotEqual(self.approved, self.run['head_sha'])

    def test_fork_wrong_base_and_non_target_event_rejected(self):
        import copy
        for kind in ('fork', 'base', 'event', 'head', 'workflow'):
            run = copy.deepcopy(self.run)
            if kind == 'fork': run['pull_requests'][0]['head']['repo']['url'] += '-fork'
            if kind == 'base': run['pull_requests'][0]['base']['sha'] = '3' * 40
            if kind == 'event': run['event'] = 'pull_request'
            if kind == 'head': run['head_sha'] = '4' * 40
            if kind == 'workflow': run['path'] = '.github/workflows/other.yml'
            with self.subTest(kind=kind):
                self.assertFalse(m.trusted_readiness_run(self.repo, run, self.approved))

    def test_blocker_stops_planning_before_expensive_work(self):
        args = SimpleNamespace(repository=self.repo, current_sha=self.candidate, event='pull_request')
        with patch.object(m, 'require_readiness', side_effect=RuntimeError('READINESS_BLOCKED')), \
             patch.object(m, '_compute_validation_plan_once') as plan:
            with self.assertRaisesRegex(RuntimeError, 'READINESS_BLOCKED'): m.cmd_plan(args)
            plan.assert_not_called()

    def test_package_blocker_is_not_converted_into_build_fallback(self):
        args = SimpleNamespace(repository=self.repo, current_sha=self.candidate)
        with patch.object(m, 'require_readiness', side_effect=RuntimeError('READINESS_BLOCKED')), \
             patch.object(m, 'package_inputs_compatible') as package:
            with self.assertRaisesRegex(RuntimeError, 'READINESS_BLOCKED'): m.cmd_package_canary_plan(args)
            package.assert_not_called()

    def test_package_lookup_failure_preserves_prior_plan_and_selects_zero_builds(self):
        for failure in (m.EvidenceLookupUnavailable('provider read unavailable'),
                        ValueError('untrusted receipt payload')):
            with self.subTest(failure=type(failure).__name__), tempfile.TemporaryDirectory() as directory:
                output = Path(directory) / 'plan.json'
                prior = dict(needed=False, evidenceRunId=123, reason='prior compatible evidence')
                output.write_text(json.dumps(prior))
                args = SimpleNamespace(repository=self.repo, current_sha=self.candidate,
                    base_sha=self.approved, current_run_id=456, head_branch='repair', output=str(output))
                with patch.object(m, 'require_readiness', return_value={'receipt': self.receipt}), \
                     patch.object(m, 'compute_package_canary_plan', side_effect=failure) as plan:
                    with self.assertRaisesRegex(RuntimeError, 'PACKAGE_PLANNING_BLOCKED'):
                        m.cmd_package_canary_plan(args)
                    self.assertEqual(1, plan.call_count)
                self.assertEqual(prior, json.loads(output.read_text()))
                blocked = json.loads(Path(str(output) + '.blocked.json').read_text())
                self.assertEqual('blocked', blocked['state'])
                self.assertFalse(blocked['needed'])
                self.assertEqual('validated-release-package-plan', blocked['resumeBoundary'])
                self.assertNotIn('untrusted receipt payload', json.dumps(blocked))

    def test_blocked_package_plan_is_retained_by_its_exact_attempt(self):
        text = (ROOT / '.github/workflows/masterapp-platform-architecture-validation.yml').read_text()
        block = m.named_step_blocks(text)['Preserve blocked package planning evidence']
        self.assertIn("if: failure() && steps.packageplan.outcome == 'failure'", block)
        self.assertIn('validated-release-package-plan-blocked-${{ github.run_id }}-a${{ github.run_attempt }}', block)
        self.assertIn('package-canary-plan.json.blocked.json', block)
        self.assertNotIn('overwrite: true', block)

    def test_successful_child_survives_failed_parent_and_new_attempt(self):
        run = dict(self.run, run_attempt=3, status='completed', conclusion='failure')
        jobs = {'total_count': 1, 'jobs': [{'name': 'release-readiness', 'status': 'completed', 'conclusion': 'success'}]}
        with patch.object(m, 'api_get', return_value=jobs) as api:
            self.assertTrue(m.readiness_child_succeeded(self.repo, run, 'release-readiness', 1, 'token'))
            self.assertIn('/attempts/1/jobs', api.call_args.args[1])
            self.assertFalse(m.readiness_child_succeeded(self.repo, run, 'release-readiness', 4, 'token'))
            self.assertEqual(1, api.call_count)

    def test_skipped_or_incomplete_producer_job_never_counts_as_success(self):
        for conclusion, total in [('skipped', 1), ('success', 2), ('cancelled', 1), ('failure', 1)]:
            jobs = {'total_count': total, 'jobs': [{'name': 'release-readiness', 'status': 'completed', 'conclusion': conclusion}]}
            with patch.object(m, 'api_get', return_value=jobs):
                self.assertFalse(m.readiness_child_succeeded(self.repo, self.run, 'release-readiness', 1, 'token'))

    def test_immutable_attempt_artifacts_preserve_older_children(self):
        run = dict(self.run, run_attempt=3)
        names = {'proof-' + 'a' * 64 + '-a1', 'proof-' + 'a' * 64 + '-a3',
                 'proof-' + 'b' * 64 + '-a2'}
        artifacts = [dict(id=i + 1, name=name, expired=False) for i, name in enumerate(sorted(names))]
        with patch.object(m, 'api_get', return_value={'total_count': len(artifacts), 'artifacts': artifacts}):
            rows = list(m.readiness_artifact_candidates(self.repo, [run], 'proof-', 'a' * 64, 'token', lambda r: True))
        self.assertEqual(['-a3', '-a1'], [artifact['name'][-3:] for _, artifact in rows])

    def test_attempt_substitution_rejected_before_successful_child_can_authorize(self):
        import json
        import hashlib
        body = b'validated-bundle'
        for reader, filename in ((m.readiness_evidence, 'readiness.json'),
                                  (m.readiness_observation_evidence, 'observation.json'),
                                  (m.migration_rehearsal_evidence, 'rehearsal.json')):
            receipt = dict(self.receipt, producingRun=42, producingAttempt=1,
                executionAuthority=self.approved, candidate=self.candidate,
                readinessIdentity=self.expected['identity'], proven=True,
                bundleDigest=hashlib.sha256(body).hexdigest(),
                counts={'mutations': 1, 'intents': 1, 'successReceipts': 2})
            artifact = dict(id=123, name='proof-' + self.expected['identity'] + '-a2')
            def download(repo, run, name, root, *, artifact_id):
                self.assertEqual(123, artifact_id)
                (root / filename).write_text(json.dumps(receipt))
                (root / 'migration').mkdir()
                (root / 'migration' / m.MIGRATION_BUNDLE_NAME).write_bytes(body)
            with self.subTest(reader=reader.__name__), \
                 patch.object(m, 'readiness_identity', return_value=self.expected), \
                 patch.object(m, 'api_get', return_value={}), \
                 patch.object(m, 'readiness_artifact_candidates', return_value=[(dict(self.run, run_attempt=2), artifact)]), \
                 patch.object(m, '_download_run_artifact', side_effect=download), \
                 patch.object(m, 'validate_readiness_receipt'), \
                 patch.object(m, 'readiness_child_succeeded', return_value=True) as child:
                args = (self.repo, self.candidate, self.expected, 'token') if reader == m.migration_rehearsal_evidence else (self.repo, self.candidate, self.approved, [], 'token')
                with self.assertRaisesRegex(ValueError, 'ARTIFACT_ATTEMPT_MISMATCH'):
                    reader(*args)
                child.assert_not_called()

    def test_duplicate_or_incomplete_artifact_inventory_is_not_reuse(self):
        row = dict(id=1, name='proof-' + self.expected['identity'] + '-a1', expired=False)
        for data, error in (({'total_count': 2, 'artifacts': [row]}, m.EvidenceLookupUnavailable),
                            ({'total_count': 2, 'artifacts': [row, dict(row, id=2)]}, ValueError)):
            with patch.object(m, 'api_get', return_value=data), self.assertRaises(error):
                list(m.readiness_artifact_candidates(self.repo, [self.run], 'proof-', self.expected['identity'], 'token', lambda r: True))

    def test_readiness_deadline_preserves_blocked_state_without_fresh_plan(self):
        with patch.dict(m.os.environ, {'GITHUB_ACTIONS': 'true', 'GITHUB_EVENT_NAME': 'pull_request'}), \
             patch.object(m, 'approved_head_preflight', return_value={'current': True, 'approvedHeadSha': self.approved}), \
             patch.object(m, 'git_show_file', return_value='READINESS_SCHEMA = 1'), \
             patch.object(m, 'git_changed', return_value=[]), \
             patch.object(m, 'readiness_evidence', return_value=None) as lookup, \
             patch.object(m, 'READINESS_WAIT_SECONDS', 1):
            with self.assertRaisesRegex(RuntimeError, 'READINESS_BLOCKED'):
                m.require_readiness(self.repo, self.candidate, clock=iter([0, 1]).__next__, sleep=lambda _: None)
            self.assertEqual(1, lookup.call_count)

    def test_step5_blocker_stops_partition_execution(self):
        args = SimpleNamespace(repository=self.repo, current_sha=self.candidate)
        with patch.object(m, 'require_readiness', side_effect=RuntimeError('READINESS_BLOCKED')):
            with self.assertRaisesRegex(RuntimeError, 'READINESS_BLOCKED'): m.cmd_step5_decision(args)


class ProbeAttemptAndBudgetTests(unittest.TestCase):
    def test_probe_retains_original_success_after_parent_rerun_failure(self):
        artifact = dict(id=8, name='probe', expired=False, created_at='2026-10-09T00:01:00Z')
        first = dict(id=10, name='validated-migration-probe', status='completed', conclusion='success',
            run_attempt=1, started_at='2026-10-09T00:00:00Z', completed_at='2026-10-09T00:02:00Z')
        later = dict(first, id=20, run_attempt=2, conclusion='failure',
            started_at='2026-10-09T01:00:00Z', completed_at='2026-10-09T01:02:00Z')
        run = dict(id=42, run_attempt=2, conclusion='failure')
        for outcome, expected in (('success', {'artifactId': 8, 'producingAttempt': 1, 'producerJobId': 10}),
                                  ('skipped', None), ('cancelled', None), ('failure', None)):
            with self.subTest(outcome=outcome), patch.object(m, 'api_get', side_effect=[
                {'total_count': 1, 'artifacts': [artifact]},
                {'total_count': 2, 'jobs': [later, dict(first, conclusion=outcome)]}]):
                self.assertEqual(expected, m.migration_probe_artifact('owner/repo', run, 'probe', 'token'))

    def test_nested_lookup_cannot_extend_outer_deadline(self):
        with m.evidence_lookup_budget(10, clock=lambda: 7):
            self.assertEqual(3, m.evidence_remaining(30))
            with m.evidence_lookup_budget(100, clock=lambda: 9):
                self.assertEqual(1, m.evidence_remaining(120))
            self.assertEqual(3, m.evidence_remaining(120))
        self.assertEqual(120, m.evidence_remaining(120))

    def test_expired_budget_prevents_provider_read(self):
        with m.evidence_lookup_budget(10, clock=lambda: 10), \
             patch.object(m.urllib.request, 'urlopen') as remote:
            with self.assertRaisesRegex(m.EvidenceLookupUnavailable, 'deadline exhausted'):
                m.api_get('owner/repo', 'actions/runs', 'token')
            remote.assert_not_called()

    def test_immutable_artifact_get_extracts_bytes_and_rejects_traversal(self):
        import zipfile
        for path, valid in (('proof.json', True), ('../escape', False)):
            def read(command, **kwargs):
                self.assertEqual(['gh', 'api', 'repos/owner/repo/actions/artifacts/123/zip'], command)
                with zipfile.ZipFile(kwargs['stdout'], 'w') as archive:
                    archive.writestr(path, 'evidence')
                return SimpleNamespace(returncode=0, stderr='')
            with tempfile.TemporaryDirectory() as directory, patch.object(m.subprocess, 'run', side_effect=read) as get:
                root = Path(directory) / 'artifact'
                if valid:
                    m._download_run_artifact('owner/repo', 42, 'name-is-not-identity', root, artifact_id=123)
                    self.assertEqual('evidence', (root / path).read_text())
                else:
                    with self.assertRaisesRegex(ValueError, 'unsafe entries'):
                        m._download_run_artifact('owner/repo', 42, 'name-is-not-identity', root, artifact_id=123)
                    self.assertFalse((Path(directory) / 'escape').exists())
                self.assertEqual(1, get.call_count)


class ArtifactEvidenceReadRetryTests(unittest.TestCase):
    def get(self, values):
        import os
        with tempfile.TemporaryDirectory() as directory, \
             patch.object(m.subprocess, 'run', side_effect=values) as run, \
             patch.object(m.time, 'sleep') as sleep:
            try:
                m._download_run_artifact('MYLEGND/masterapp', 42, 'proof', Path(directory))
                return run.call_count, sleep.call_count, None
            except m.EvidenceLookupUnavailable as exc:
                return run.call_count, sleep.call_count, str(exc)

    def test_exact_transient_http_503_retries_without_exposing_signed_url(self):
        transient = SimpleNamespace(returncode=1, stderr='HTTP 503: account egress temporarily exceeded https://secret.example/?sig=private')
        success = SimpleNamespace(returncode=0, stderr='')
        count, sleeps, error = self.get([transient, transient, success])
        self.assertEqual((3, 2, None), (count, sleeps, error))

    def test_bounded_transient_http_503_exhaustion_is_not_silent_success(self):
        transient = SimpleNamespace(returncode=1, stderr='HTTP 503: https://secret.example/?sig=private')
        count, sleeps, error = self.get([transient, transient, transient])
        self.assertEqual((3, 2, 'Transient artifact evidence read exhausted bounded retries'),
                         (count, sleeps, error))
        self.assertNotIn('private', error)

    def test_unauthorized_and_missing_artifacts_never_retry(self):
        for msg in ('HTTP 401: invalid credentials', 'HTTP 404: artifact missing',
                    'provider returned bad proof', 'HTTP 403 forbidden'):
            with self.subTest(msg=msg):
                result = SimpleNamespace(returncode=1, stderr=msg)
                self.assertEqual((1, 0, 'Artifact evidence read unavailable'), self.get([result]))

    def test_process_timeout_does_not_restart_unknown_artifact_operation(self):
        count, sleeps, error = self.get([m.subprocess.TimeoutExpired('gh', 120)])
        self.assertEqual((1, 0, 'Artifact evidence read unavailable'), (count, sleeps, error))


class PackageJobSchedulingTests(unittest.TestCase):
    def test_retired_backfill_jobs_cannot_be_scheduled_by_ancestor_results(self):
        import yaml
        jobs = yaml.safe_load((ROOT / m.PACKAGE_BUILD_WORKFLOW).read_text())['jobs']
        # Inspect the real YAML condition, not an implicit-success simulator:
        # even successful ancestors must never activate this retired producer.
        for name in ('validated-release-package-plan', 'validated-release-package-components',
                     'validated-release-package'):
            with self.subTest(job=name):
                self.assertEqual('${{ false }}', jobs[name]['if'])


class Step5JobSchedulingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import re
        cls.re = re
        workflow = (ROOT / '.github/workflows/step5-isolated-conversion-mapping-validation.yml').read_text()
        cls.blocks = m._job_blocks(workflow)

    def expression(self, job):
        block = self.blocks[job]
        return self.re.search(r"    if: (.*?)\n    runs-on:", block, self.re.S).group(1).removeprefix('>-').strip()

    def evaluate(self, expression, fields, cancelled=False, job=False):
        # GitHub injects success() for job conditions without a status function.
        # Preserve that behavior so removing always() reproduces the live defect.
        if job and not self.re.search(r'\b(always|cancelled|success|failure)\(', expression):
            if any(value != 'success' for key, value in fields.items() if key.endswith('.result')):
                return False
        expression = self.re.sub(r'needs\.[\w.-]+', lambda match: repr(fields.get(match.group(), '')), expression)
        expression = expression.replace('always()', 'True').replace('cancelled()', repr(cancelled))
        expression = expression.replace('&&', ' and ').replace('||', ' or ')
        expression = self.re.sub(r'!(?!=)', ' not ', expression)
        return bool(eval(' '.join(expression.split()), {'__builtins__': {}}, {}))

    def fields(self, mode='repair', **overrides):
        fields = {
            'needs.plan.result': 'success',
            'needs.plan.outputs.mode': mode,
            'needs.plan.outputs.comparison_run': 'true',
            'needs.plan.outputs.baseline_run_required': 'true',
            'needs.baseline-evidence.result': 'success' if mode == 'full' else 'skipped',
            'needs.baseline-evidence.outputs.reusable': 'false',
            'needs.baseline.result': 'success',
            'needs.candidate.result': 'success',
        }
        fields.update(overrides)
        return fields

    def test_baseline_runs_only_when_planned_and_dependencies_are_valid(self):
        for mode in ('full', 'repair', 'reuse'):
            fields = self.fields(mode)
            with self.subTest(mode=mode):
                self.assertTrue(self.evaluate(self.expression('baseline'), fields, job=True))
                self.assertFalse(self.evaluate(self.expression('baseline'), fields, cancelled=True, job=True))
                for result in ('failure', 'cancelled'):
                    for dependency in ('plan', 'baseline-evidence'):
                        invalid = dict(fields, **{f'needs.{dependency}.result': result})
                        self.assertFalse(self.evaluate(self.expression('baseline'), invalid, job=True))
                fields['needs.plan.outputs.comparison_run'] = 'false'
                self.assertFalse(self.evaluate(self.expression('baseline'), fields, job=True))
            fields = self.fields(mode)
            fields['needs.baseline-evidence.outputs.reusable'] = 'true'
            fields['needs.plan.outputs.baseline_run_required'] = 'false'
            self.assertFalse(self.evaluate(self.expression('baseline'), fields, job=True))

    def test_missing_required_baseline_fails_existing_terminal_check_instead_of_skipping(self):
        import os
        import subprocess
        block = self.blocks['validate']
        ready = self.re.search(r'DEPENDENCIES_READY: >-\s*\$\{\{(.*?)\}\}', block, self.re.S).group(1)
        script = self.re.search(r'        run: \|\n(.*?)(?=\n      - name:)', block, self.re.S).group(1)
        script = '\n'.join(line[10:] for line in script.splitlines())
        for mode in ('full', 'repair', 'reuse'):
            for baseline in ('success', 'skipped', 'failure', 'cancelled'):
                with self.subTest(mode=mode, baseline=baseline):
                    fields = self.fields(mode, **{'needs.baseline.result': baseline})
                    self.assertTrue(self.evaluate(self.expression('validate'), fields, job=True))
                    allowed = self.evaluate(ready, fields)
                    result = subprocess.run(['bash', '-c', script], env=dict(os.environ, DEPENDENCIES_READY=str(allowed).lower()), capture_output=True)
                    self.assertEqual(0 if baseline == 'success' else 1, result.returncode)
            fields = self.fields(mode, **{'needs.plan.result': 'failure'})
            self.assertFalse(self.evaluate(self.expression('validate'), fields, job=True))
            self.assertFalse(self.evaluate(self.expression('validate'), self.fields(mode), cancelled=True, job=True))

    def test_failed_dependency_cannot_be_masked_by_reusable_evidence(self):
        ready = self.re.search(r'DEPENDENCIES_READY: >-\s*\$\{\{(.*?)\}\}', self.blocks['validate'], self.re.S).group(1)
        for mode in ('full', 'repair', 'reuse'):
            for dependency in ('candidate', 'baseline', 'baseline-evidence'):
                for result in ('failure', 'cancelled'):
                    fields = self.fields(mode, **{
                        f'needs.{dependency}.result': result,
                        'needs.plan.outputs.baseline_run_required': 'false',
                        'needs.baseline-evidence.outputs.reusable': 'true',
                    })
                    self.assertFalse(self.evaluate(ready, fields))

    def test_preserved_baseline_does_not_require_rerun(self):
        ready = self.re.search(r'DEPENDENCIES_READY: >-\s*\$\{\{(.*?)\}\}', self.blocks['validate'], self.re.S).group(1)
        for mode in ('full', 'repair', 'reuse'):
            fields = self.fields(mode, **{
                'needs.baseline.result': 'skipped',
                'needs.plan.outputs.baseline_run_required': 'false',
                'needs.baseline-evidence.outputs.reusable': 'true',
            })
            self.assertFalse(self.evaluate(self.expression('baseline'), fields, job=True))
            self.assertTrue(self.evaluate(ready, fields))


class ApprovedHeadPreflightTests(unittest.TestCase):
    def test_current_candidate_requires_exact_current_approved_ancestry(self):
        approved = "a" * 40
        candidate = "b" * 40

        def api_get(_repository, path, _token):
            if path.startswith("branches/"):
                return {"commit": {"sha": approved}}
            if path.startswith("compare/"):
                return {
                    "status": "ahead",
                    "merge_base_commit": {"sha": approved},
                }
            raise AssertionError(path)

        with patch.object(m, "api_get", side_effect=api_get):
            result = m.approved_head_preflight("owner/repo", candidate, "token")
        self.assertTrue(result["current"])
        self.assertEqual(approved, result["approvedHeadSha"])

    def test_diverged_candidate_fails_before_validation_planning(self):
        approved = "a" * 40
        candidate = "b" * 40

        def api_get(_repository, path, _token):
            if path.startswith("branches/"):
                return {"commit": {"sha": approved}}
            if path.startswith("compare/"):
                return {
                    "status": "diverged",
                    "merge_base_commit": {"sha": "c" * 40},
                }
            raise AssertionError(path)

        with patch.object(m, "api_get", side_effect=api_get):
            result = m.approved_head_preflight("owner/repo", candidate, "token")
        self.assertFalse(result["current"])

    def test_non_pr_preflight_is_noop_without_github_lookup(self):
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(
                repository="owner/repo",
                current_sha="a" * 40,
                event="workflow_dispatch",
                output=str(Path(directory) / "preflight.json"),
            )
            with patch.object(m, "approved_head_preflight") as check:
                m.cmd_approved_head_preflight(args)
            check.assert_not_called()
            result = m.json.loads(Path(args.output).read_text())
            self.assertTrue(result["current"])
            self.assertEqual("not_applicable", result["compareStatus"])


class ParfaitValidationResumeTests(unittest.TestCase):
    workflow='parfait-isolated-validation.yml'

    def test_control_fix_preserves_application_and_sql_evidence(self):
        steps={row['step']:'success' for row in m.WORKFLOWS[self.workflow]['gates'].values()}
        plan=m.compute_plan(self.workflow,'b'*40,dict(id=1,head_sha='a'*40),steps,
            ['scripts/validation-resume.py','scripts/test-validation-resume.py'], 'authenticated-prior')
        self.assertEqual({key for key,row in plan['gates'].items() if row['run']},{'migration-contract'})

    def test_application_change_invalidates_actual_consumers(self):
        steps={row['step']:'success' for row in m.WORKFLOWS[self.workflow]['gates'].values()}
        plan=m.compute_plan(self.workflow,'b'*40,dict(id=1,head_sha='a'*40),steps,
            ['CommerceCore/Controllers/StoreCheckoutController.cs'],'authenticated-prior')
        self.assertTrue(plan['gates']['build']['run'])
        self.assertTrue(plan['gates']['payments']['run'])
        self.assertTrue(plan['gates']['publish']['run'])
        self.assertFalse(plan['gates']['routing']['run'])

    def test_failed_gate_is_never_preserved(self):
        steps={row['step']:'success' for row in m.WORKFLOWS[self.workflow]['gates'].values()}
        steps['Verify selective Cloudflare commerce routing without deployment']='failure'
        plan=m.compute_plan(self.workflow,'b'*40,dict(id=1,head_sha='a'*40),steps,[],'authenticated-prior')
        self.assertTrue(plan['gates']['routing']['run'])

    def test_planner_instrumentation_does_not_hide_source_mutation(self):
        source=(ROOT/m.WORKFLOW_PATHS[self.workflow]).read_text();config=m.WORKFLOWS[self.workflow]
        safe=m._gate_execution_contract(source,config,'build')
        altered=source.replace("          PYPLAN", "          PYPLAN\n          echo source-mutation >> CommerceCore/Controllers/StoreCheckoutController.cs",1)
        self.assertNotEqual(safe,m._gate_execution_contract(altered,config,'build'))

    def test_wrong_gate_skip_condition_invalidates_execution_contract(self):
        source=(ROOT/m.WORKFLOW_PATHS[self.workflow]).read_text();config=m.WORKFLOWS[self.workflow]
        changed=source.replace(".gates['build'].run", ".gates['routing'].run",1)
        self.assertNotEqual(m._gate_execution_contract(source,config,'build'),
                            m._gate_execution_contract(changed,config,'build'))

    def test_checkout_ref_change_invalidates_execution_contract(self):
        source=(ROOT/m.WORKFLOW_PATHS[self.workflow]).read_text();config=m.WORKFLOWS[self.workflow]
        changed=source.replace('          fetch-depth: 0','          fetch-depth: 0\n          ref: other-branch',1)
        self.assertNotEqual(m._gate_execution_contract(source,config,'build'),
                            m._gate_execution_contract(changed,config,'build'))


class FastTrackExecutionIdentityTests(unittest.TestCase):
    def setUp(self):
        self.policy = m._fast_track_policy()
        self.request = {'releaseMode':'approved-only','requestId':'synthetic-normal-release'}
        request_patch = patch.object(self.policy,'read_request',side_effect=lambda sha:self.request)
        request_patch.start();self.addCleanup(request_patch.stop)
        policy_patch = patch.object(m,'_fast_track_policy',return_value=self.policy)
        policy_patch.start();self.addCleanup(policy_patch.stop)
        self.env=dict(GITHUB_ACTIONS='true',GITHUB_EVENT_NAME='push',
            GITHUB_REF='refs/heads/Legend/fast-track-release',GITHUB_REPOSITORY='owner/repo',
            GITHUB_JOB='deploy-ui',TARGET_KEY='parfait',RUNNER_NAME='runner-fixture',GITHUB_TOKEN='test-token',GITHUB_SHA='a'*40,GITHUB_RUN_ID='123',GITHUB_RUN_ATTEMPT='2',
            GITHUB_WORKFLOW_REF='owner/repo/.github/workflows/legend-fast-track-ui.yml@refs/heads/Legend/fast-track-release')
        self.run=dict(id=123,run_attempt=2,path='.github/workflows/legend-fast-track-ui.yml',
            head_branch='Legend/fast-track-release',event='push',head_sha='a'*40,status='in_progress',
            head_repository=dict(full_name='owner/repo'))

    def invoke(self, run=None, head=None):
        def api(repo,path,token):
            if path.startswith('git/ref/'):return dict(object=dict(sha=head or 'a'*40))
            if '/jobs?' in path:return dict(total_count=1,jobs=[dict(id=1,run_id=123,run_attempt=2,head_sha='a'*40,status='in_progress',runner_name='runner-fixture',name='Affected App Deploy — Parfait')])
            return self.run if run is None else run
        with patch.dict(os.environ,self.env,clear=True),patch.object(m,'api_get',side_effect=api):
            return m.assert_fast_track_release_execution()

    def test_exact_current_fast_track_run_authenticated(self):
        self.assertEqual(self.invoke(),self.run)
        self.assertNotIn('releaseAdmission',self.invoke())

    def test_other_branch_event_or_workflow_rejected_before_provider_read(self):
        for key,value in [('GITHUB_REF','refs/heads/legend/approved-changes'),
            ('GITHUB_EVENT_NAME','workflow_dispatch'),('GITHUB_WORKFLOW_REF','owner/repo/.github/workflows/untrusted.yml@refs/heads/Legend/fast-track-release'),
            ('GITHUB_ACTIONS','false'),('GITHUB_RUN_ATTEMPT','0')]:
            with self.subTest(key=key),patch.dict(os.environ,self.env|{key:value},clear=True),patch.object(m,'api_get') as api:
                with self.assertRaisesRegex(RuntimeError,'IDENTITY_INCOMPLETE'):
                    m.assert_fast_track_release_execution()
                api.assert_not_called()

    def test_remote_revision_attempt_event_and_fork_cannot_be_forged(self):
        for key,value in [('head_sha','b'*40),('run_attempt',1),('event','pull_request'),
                          ('head_repository',dict(full_name='fork/repo')),('status','completed')]:
            with self.subTest(key=key),self.assertRaisesRegex(RuntimeError,'PRODUCER_UNPROVEN'):
                self.invoke(self.run|{key:value})

    def test_queued_parent_requires_exact_current_active_runner(self):
        job=dict(id=1,run_id=123,run_attempt=2,head_sha='a'*40,status='in_progress',
            runner_name='runner-fixture',name='Affected App Deploy — Parfait')
        def invoke(jobs,job_key='deploy-ui'):
            now=[0]
            def api(repo,path,token):
                if '/jobs?' in path:return dict(total_count=len(jobs),jobs=jobs)
                if path.startswith('git/ref/'):return dict(object=dict(sha='a'*40))
                return self.run|dict(status='queued')
            with patch.dict(os.environ,self.env|dict(GITHUB_JOB=job_key,TARGET_KEY='parfait',RUNNER_NAME='runner-fixture'),clear=True), \
                 patch.object(m,'api_get',side_effect=api):
                return m.assert_fast_track_release_execution(clock=lambda:now[0],sleep=lambda n:now.__setitem__(0,now[0]+n))
        self.assertEqual('queued',invoke([job])['status'])
        for key,value in [('run_id',124),('run_attempt',1),('head_sha','b'*40),
                ('status','queued'),('runner_name','other'),('name','unknown')]:
            with self.subTest(key=key),self.assertRaisesRegex(RuntimeError,'ACTIVE_JOB_UNPROVEN|JOB_UNPROVEN|deadline exhausted'):
                invoke([job|{key:value}])
        with self.assertRaisesRegex(RuntimeError,'ACTIVE_JOB_UNPROVEN|JOB_UNPROVEN|deadline exhausted'):invoke([])
        with self.assertRaisesRegex(RuntimeError,'ACTIVE_JOB_UNPROVEN|JOB_UNPROVEN|deadline exhausted'):invoke([job,job])
        with self.assertRaisesRegex(RuntimeError,'ACTIVE_JOB_UNPROVEN|JOB_UNPROVEN|deadline exhausted'):invoke([job],job_key='unknown')
        with self.assertRaisesRegex(RuntimeError,'ACTIVE_JOB_UNPROVEN|JOB_UNPROVEN|deadline exhausted'):invoke([None])

    def test_advanced_release_head_stops_stale_execution(self):
        with self.assertRaisesRegex(RuntimeError,'HEAD_CHANGED'):
            self.invoke(head='b'*40)

    def test_active_promotion_hold_cannot_enter_mutation_execution(self):
        self.request['trustedToolPromotion'] = {'schemaVersion':1}
        with self.assertRaisesRegex(RuntimeError,'TRUSTED_TOOL_PROMOTION_NONPUBLISHING'):
            self.invoke()



class SelectorAttemptNonentryTests(unittest.TestCase):
    """Scope carry needs complete authenticated nonentry across every attempt."""
    def setUp(self):
        self.repository='MYLEGND/masterapp';self.revision='b'*40;self.now=0.0;self.calls=[]
        self.run=dict(id=39792159170,run_attempt=2,workflow_id=123,head_sha='a'*40,
            status='completed',conclusion='failure',event='push',head_branch=m.FAST_TRACK_PR_BASE,
            path='.github/workflows/'+m.FAST_TRACK_RELEASE_WORKFLOW,
            head_repository=dict(full_name=self.repository),repository=dict(full_name=self.repository))
        self.source=(ROOT/self.run['path']).read_text()
        self.endpoint=f"actions/runs/{self.run['id']}"
        self.responses={self.endpoint:self.clone(self.run)}
        names=['Select exact affected applications','Admit exact validated fast-track source',
               'Reconcile production schema before publication',
               'Affected App Deploy — ${{ matrix.target }}','Publish verified website routing']
        for attempt in (1,2):
            self.responses[f'{self.endpoint}/attempts/{attempt}']=dict(self.run,run_attempt=attempt)
            self.responses[self.jobpath(attempt)]=dict(total_count=5,jobs=[
                dict(id=100*attempt+i,name=name,run_id=self.run['id'],head_sha=self.run['head_sha'],
                    run_attempt=attempt,status='completed',conclusion='failure' if i==0 else 'skipped',steps=[])
                for i,name in enumerate(names)])
    @staticmethod
    def clone(value):return json.loads(json.dumps(value))
    def jobpath(self,attempt):return f'{self.endpoint}/attempts/{attempt}/jobs?per_page=100'
    def api(self,repository,path,token):
        self.assertEqual(repository,self.repository);self.calls.append(path)
        return self.clone(self.responses[path])
    def invoke(self):
        with patch.object(m,'api_get',side_effect=self.api), \
             patch.object(m,'_release_history_source',return_value=self.source), \
             patch.object(m,'git_show_file',return_value=(ROOT/self.run['path']).read_text()):
            return m.fast_track_selector_attempts_proven_nonentry(
                self.repository,self.run,self.revision,'placeholder',clock=lambda:self.now)
    def test_both_failed_attempts_have_positive_complete_nonentry(self):
        self.assertTrue(self.invoke())
        self.assertEqual(self.calls,[self.endpoint,f'{self.endpoint}/attempts/1',self.jobpath(1),
            f'{self.endpoint}/attempts/2',self.jobpath(2),self.endpoint])
    def test_only_multi_attempt_terminal_failure_is_eligible(self):
        original=self.clone(self.run)
        for key,value in [('run_attempt',1),('run_attempt',101),('run_attempt',True),('run_attempt','2'),
            ('status','in_progress'),('conclusion','success'),('workflow_id',True),('workflow_id',0),
            ('event','workflow_dispatch'),('head_branch','other'),('head_repository',{}),('repository',{})]:
            with self.subTest(key=key,value=value):
                self.run=dict(original,**{key:value});self.assertFalse(self.invoke())
        self.run=original;self.assertEqual(self.calls,[])
    def test_wrong_attempt_run_identity_refuses(self):
        for attempt in (1,2):
            path=f'{self.endpoint}/attempts/{attempt}';original=self.clone(self.responses[path])
            for key,value in [('id',5),('run_attempt',3),('run_attempt',True),('head_sha','c'*40),
                ('workflow_id',9),('head_branch','other'),('event','pull_request'),
                ('path','.github/workflows/other.yml'),('head_repository',dict(full_name='other/repo')),
                ('repository',dict(full_name='other/repo')),('status','in_progress'),('conclusion','cancelled')]:
                with self.subTest(attempt=attempt,key=key):
                    self.responses[path]=dict(original,**{key:value});self.assertFalse(self.invoke())
            self.responses[path]=original
    def test_missing_attempt_transport_error_is_not_skipped(self):
        del self.responses[f'{self.endpoint}/attempts/1']
        with self.assertRaises(KeyError):self.invoke()
    def test_incomplete_duplicate_extra_or_malformed_jobs_refuse(self):
        path=self.jobpath(1);original=self.clone(self.responses[path])
        variants=[None,{},dict(total_count=6,jobs=original['jobs']),dict(total_count=True,jobs=original['jobs']),
            dict(total_count=4,jobs=original['jobs'][:-1]),dict(total_count=6,jobs=original['jobs']+[original['jobs'][0]]),
            dict(total_count=5,jobs=[original['jobs'][0]]*5),dict(total_count=5,jobs=[None]*5)]
        for value in variants:
            with self.subTest(value=value):self.responses[path]=value;self.assertFalse(self.invoke())
    def test_every_job_has_exact_run_attempt_head_and_distinct_id(self):
        for attempt in (1,2):
            path=self.jobpath(attempt);original=self.clone(self.responses[path])
            for key,value in [('run_id',6),('run_id',True),('run_attempt',3),('run_attempt',True),
                ('head_sha','c'*40),('status','queued'),('id',0),('id',True),('id',original['jobs'][0]['id'])]:
                with self.subTest(attempt=attempt,key=key):
                    self.responses[path]=self.clone(original)
                    self.responses[path]['jobs'][1][key]=value;self.assertFalse(self.invoke())
            self.responses[path]=original
    def test_job_id_reused_across_attempts_refuses(self):
        self.responses[self.jobpath(2)]['jobs'][0]['id']=self.responses[self.jobpath(1)]['jobs'][0]['id']
        self.assertFalse(self.invoke())
    def test_any_downstream_entry_in_either_attempt_refuses(self):
        for attempt in (1,2):
            path=self.jobpath(attempt);original=self.clone(self.responses[path])
            for i in range(1,5):
                for conclusion in ('success','failure','cancelled',None):
                    with self.subTest(attempt=attempt,owner=i,conclusion=conclusion):
                        self.responses[path]=self.clone(original)
                        self.responses[path]['jobs'][i]['conclusion']=conclusion;self.assertFalse(self.invoke())
            self.responses[path]=original
    def test_skipped_job_must_have_empty_steps(self):
        for steps in (None,[dict(name='mutation',conclusion='success')],[dict(name='mutation',conclusion='skipped')]):
            self.responses[self.jobpath(1)]['jobs'][2]['steps']=steps;self.assertFalse(self.invoke())
    def test_each_selector_must_be_failed_not_success_cancelled_or_skipped(self):
        for attempt in (1,2):
            owner=self.responses[self.jobpath(attempt)]['jobs'][0]
            for conclusion in ('success','cancelled','skipped',None):
                with self.subTest(attempt=attempt,conclusion=conclusion):
                    owner['conclusion']=conclusion;self.assertFalse(self.invoke())
            owner['conclusion']='failure'
    def test_changed_historical_workflow_refuses(self):
        self.source+='\n# changed historical execution\n';self.assertFalse(self.invoke())
    def test_latest_run_must_match_before_proof(self):
        self.responses[self.endpoint]['run_attempt']=3;self.assertFalse(self.invoke())
        self.assertEqual(self.calls,[self.endpoint])
    def test_new_attempt_or_active_state_during_proof_refuses(self):
        original=self.api
        for key,value in [('run_attempt',3),('status','in_progress'),('head_sha','c'*40),('workflow_id',8)]:
            self.calls=[]
            def api(repo,path,token):
                result=original(repo,path,token)
                if path==self.endpoint and len(self.calls)>1:result[key]=value
                return result
            self.api=api
            with self.subTest(key=key):self.assertFalse(self.invoke())
            self.api=original
    def test_late_final_read_rejected_at_shared_deadline(self):
        original=self.api
        def api(repo,path,token):
            result=original(repo,path,token)
            if path==self.endpoint and len(self.calls)>1:self.now=30.0
            return result
        self.api=api
        with self.assertRaises(m.EvidenceLookupUnavailable):self.invoke()
    def test_shorter_enclosing_budget_is_honored(self):
        original=self.api
        def api(repo,path,token):
            result=original(repo,path,token)
            if path==self.jobpath(1):self.now=7.0
            return result
        self.api=api
        with m.evidence_lookup_budget(7.0,clock=lambda:self.now):
            with self.assertRaises(m.EvidenceLookupUnavailable):self.invoke()
        self.assertNotIn(self.jobpath(2),self.calls)
    def test_transport_unavailable_propagates_without_retry_loop(self):
        self.api=lambda *args: (_ for _ in ()).throw(m.EvidenceLookupUnavailable('offline unavailable'))
        with self.assertRaisesRegex(m.EvidenceLookupUnavailable,'offline unavailable'):self.invoke()


    def effective_baseline(self, *, merged=True):
        from contextlib import ExitStack
        before=self.run['head_sha'];parent='c'*40;original=self.api
        def api(repo,path,token):
            if path=='actions/runs?branch=Legend%2Ffast-track-release&per_page=100':
                return dict(total_count=2,workflow_runs=[self.run,
                    dict(self.run,id=8,head_sha=parent,conclusion='success')])
            if path==self.endpoint+'/jobs?filter=latest&per_page=100':return self.clone(self.responses[self.jobpath(2)])
            if path==f'commits/{before}/pulls?per_page=100':
                return [dict(merged_at='2026-10-22T14:00:00Z',merge_commit_sha=before,
                    base=dict(ref=m.FAST_TRACK_PR_BASE),head=dict(repo=dict(full_name=self.repository)))] if merged else []
            return original(repo,path,token)
        with ExitStack() as stack:
            stack.enter_context(patch.object(m,'api_get',side_effect=api))
            stack.enter_context(patch.object(m,'_release_history_source',return_value=self.source))
            stack.enter_context(patch.object(m,'git_show_file',return_value=self.source))
            stack.enter_context(patch.object(m.subprocess,'run',return_value=SimpleNamespace(returncode=0)))
            stack.enter_context(patch.object(m.subprocess,'check_output',return_value=f'{before} {parent} {self.revision}'))
            stack.enter_context(patch.object(m,'fast_track_promotion_parent',return_value=None))
            for name in ['fast_track_completion_present','fast_track_completed_apps_pending_routing',
                'attested_fast_track_manual_dns_proxy_completion','attested_fast_track_brand_preupload',
                'fast_track_completed_sql_unpublished','fast_track_unpublished_apps_with_migration_intent']:
                stack.enter_context(patch.object(m,name,return_value=False))
            return m.fast_track_effective_baseline(before,self.revision,repository=self.repository,token='placeholder')
    def test_positive_nonentry_carries_only_scope_through_canonical_owner(self):
        self.assertEqual(self.effective_baseline(),'c'*40)
    def test_prior_downstream_entry_still_blocks_scope_carry(self):
        self.responses[self.jobpath(1)]['jobs'][1]['conclusion']='success'
        with self.assertRaisesRegex(RuntimeError,'FAST_TRACK_PREDECESSOR_REQUIRES_OPERATION_RECONCILIATION'):
            self.effective_baseline()
    def test_positive_nonentry_does_not_bypass_merged_parent_proof(self):
        with self.assertRaisesRegex(RuntimeError,'FAST_TRACK_PREDECESSOR_MERGE_UNPROVEN'):
            self.effective_baseline(merged=False)


class SelectorVisibilityPollingTests(unittest.TestCase):
    def setUp(self):
        self.now=0; self.sleeps=[]; self.reads=[]; self.head='a'*40
        self.run=dict(id=123,run_attempt=2,head_sha=self.head,status='queued',event='push',
            path='.github/workflows/'+m.FAST_TRACK_RELEASE_WORKFLOW,head_branch=m.FAST_TRACK_PR_BASE,
            head_repository=dict(full_name='owner/repo'))
        self.job=dict(id=10,run_id=123,run_attempt=2,head_sha=self.head,status='in_progress',
            name='Select exact affected applications',runner_name='runner-fixture')
        self.env=dict(GITHUB_ACTIONS='true',GITHUB_EVENT_NAME='push',GITHUB_REPOSITORY='owner/repo',
            GITHUB_TOKEN='test-token',GITHUB_SHA=self.head,GITHUB_RUN_ID='123',GITHUB_RUN_ATTEMPT='2',
            GITHUB_REF='refs/heads/'+m.FAST_TRACK_PR_BASE,GITHUB_JOB='select-targets',RUNNER_NAME='runner-fixture',
            GITHUB_WORKFLOW_REF='owner/repo/.github/workflows/'+m.FAST_TRACK_RELEASE_WORKFLOW+'@refs/heads/'+m.FAST_TRACK_PR_BASE)
        self.runs=[self.run]; self.inventories=[dict(total_count=1,jobs=[self.job])]
        self.policy=SimpleNamespace(tool_promotion_requested=lambda revision:False)
    def sleep(self,seconds):
        self.sleeps.append(seconds); self.now+=seconds
    def api(self,repo,path,token):
        m.evidence_remaining(1); self.reads.append(path)
        if path.startswith('git/ref/'):
            return dict(object=dict(sha=self.head))
        seq=self.inventories if '/jobs?' in path else self.runs
        return seq.pop(0) if len(seq)>1 else seq[0]
    def invoke(self,api=None):
        with patch.dict(os.environ,self.env,clear=True),patch.object(m,'api_get',side_effect=api or self.api), \
             patch.object(m,'_fast_track_policy',return_value=self.policy):
            return m.assert_fast_track_release_execution(clock=lambda:self.now,sleep=self.sleep)
    def pending(self):
        return dict(total_count=1,jobs=[self.job|dict(status='queued',runner_name=None)])
    def test_pending_observations_are_discarded_before_fresh_full_proof(self):
        self.inventories=[self.pending(),dict(total_count=1,jobs=[self.job])]
        self.assertEqual(self.run,self.invoke());self.assertEqual([5],self.sleeps)
        self.assertEqual(['actions/runs/123','actions/runs/123/attempts/2/jobs?per_page=100']*3+
            ['git/ref/heads/'+m.FAST_TRACK_PR_BASE],self.reads)
    def test_pending_beyond_old_thirty_second_window_can_settle_without_admitting_queued(self):
        self.inventories=[self.pending() for _ in range(8)]+[dict(total_count=1,jobs=[self.job])]
        self.assertEqual(self.run,self.invoke());self.assertEqual(40,self.now)
        self.assertEqual(1,sum(p.startswith('git/ref/') for p in self.reads))
    def test_pending_timeout_has_no_head_read_or_admission(self):
        self.inventories=[self.pending()]
        with self.assertRaisesRegex(RuntimeError,'SELECTOR_VISIBILITY_UNPROVEN'):self.invoke()
        self.assertEqual(300,self.now);self.assertLessEqual(len(self.reads),122)
        self.assertFalse(any(p.startswith('git/ref/') for p in self.reads))
    def test_missing_candidate_and_runner_assignment_are_pending_only(self):
        for payload in [dict(total_count=0,jobs=[]),dict(total_count=1,jobs=[self.job|dict(runner_name='')])]:
            with self.subTest(payload=payload):
                self.setUp();self.inventories=[payload,dict(total_count=1,jobs=[self.job])]
                self.invoke();self.assertEqual([5],self.sleeps)
    def test_conflicting_run_identity_and_terminal_states_fail_immediately(self):
        for key,value in [('run_attempt',1),('head_sha','b'*40),('id',124),('event','pull_request'),
                ('path','untrusted'),('head_repository',dict(full_name='fork/repo')),
                ('status','completed'),('conclusion','failure')]:
            with self.subTest(key=key):
                self.setUp();self.runs=[self.run|{key:value}]
                with self.assertRaisesRegex(RuntimeError,'PRODUCER_UNPROVEN'):self.invoke()
                self.assertEqual([],self.sleeps);self.assertEqual(1,len(self.reads))
    def test_pending_then_changed_attempt_fails_without_another_sleep(self):
        self.inventories=[self.pending()];self.runs=[self.run,self.run|dict(run_attempt=3)]
        with self.assertRaisesRegex(RuntimeError,'PRODUCER_UNPROVEN'):self.invoke()
        self.assertEqual([5],self.sleeps)
    def test_wrong_runner_ambiguous_malformed_and_terminal_jobs_fail_immediately(self):
        cases=[dict(total_count=2,jobs=[self.job,self.job|dict(id=11)]),dict(total_count=2,jobs=[self.job]),
            dict(total_count=1,jobs=[None])]
        cases += [dict(total_count=1,jobs=[self.job|{key:value}]) for key,value in
            [('runner_name','other'),('run_attempt',1),('run_id',124),('head_sha','b'*40),
             ('status','completed'),('conclusion','failure')]]
        for payload in cases:
            with self.subTest(payload=payload):
                self.setUp();self.inventories=[payload]
                with self.assertRaisesRegex(RuntimeError,'ACTIVE_JOB_UNPROVEN'):self.invoke()
                self.assertEqual([],self.sleeps)
    def test_fresh_proof_rechecks_run_instead_of_reusing_positive_wait_evidence(self):
        self.runs=[self.run,self.run|dict(head_sha='b'*40)]
        with self.assertRaisesRegex(RuntimeError,'PRODUCER_UNPROVEN'):self.invoke()
        self.assertEqual(3,len(self.reads));self.assertFalse(any(p.startswith('git/ref/') for p in self.reads))
    def test_fresh_proof_rechecks_job_runner(self):
        self.inventories += [dict(total_count=1,jobs=[self.job|dict(runner_name='other')])]
        with self.assertRaisesRegex(RuntimeError,'runner_mismatch'):self.invoke()
        self.assertEqual(4,len(self.reads))
    def test_final_fresh_head_remains_mandatory(self):
        self.head='b'*40
        with self.assertRaisesRegex(RuntimeError,'HEAD_CHANGED'):self.invoke()
        self.assertTrue(self.reads[-1].startswith('git/ref/'))
    def test_late_positive_pending_read_never_starts_fresh_proof(self):
        def slow(*args):
            value=self.api(*args)
            if '/jobs?' in args[1]:self.now=301
            return value
        with self.assertRaises(m.EvidenceLookupUnavailable):self.invoke(slow)
        self.assertEqual(2,len(self.reads))
    def test_final_head_read_past_fresh_thirty_second_deadline_cannot_admit(self):
        def slow(*args):
            value=self.api(*args)
            if args[1].startswith('git/ref/'):self.now+=31
            return value
        with self.assertRaises(m.EvidenceLookupUnavailable):self.invoke(slow)
        self.assertEqual(5,len(self.reads))
    def test_shorter_enclosing_budget_caps_pending_wait(self):
        self.inventories=[self.pending()]
        with m.evidence_lookup_budget(7,clock=lambda:self.now):
            with self.assertRaises(m.EvidenceLookupUnavailable):self.invoke()
        self.assertEqual(7,self.now);self.assertEqual([5,2],self.sleeps)
    def test_shorter_enclosing_budget_is_not_reset_for_fresh_proof(self):
        self.inventories=[self.pending(),dict(total_count=1,jobs=[self.job])]
        def slow(*args):
            value=self.api(*args)
            if len(self.reads)==6:self.now=8
            return value
        with m.evidence_lookup_budget(7,clock=lambda:self.now):
            with self.assertRaises(m.EvidenceLookupUnavailable):self.invoke(slow)
        self.assertFalse(any(p.startswith('git/ref/') for p in self.reads))
    def test_transport_timeout_does_not_become_pending_or_success(self):
        calls=[]
        def timeout(*args,**kwargs):
            calls.append(kwargs['timeout']);self.now+=kwargs['timeout'];raise TimeoutError()
        with patch.dict(os.environ,self.env,clear=True),patch.object(m,'_fast_track_policy',return_value=self.policy), \
             patch.object(m.urllib.request,'urlopen',side_effect=timeout),patch.object(m.time,'sleep',side_effect=self.sleep):
            with m.evidence_lookup_budget(7,clock=lambda:self.now):
                with self.assertRaises(m.EvidenceLookupUnavailable):
                    m.assert_fast_track_release_execution(clock=lambda:self.now,sleep=self.sleep)
        self.assertEqual([7],calls);self.assertEqual(7,self.now)
    def test_pending_phase_is_selector_only(self):
        self.env.update(GITHUB_JOB='deploy-ui',TARGET_KEY='portal')
        self.job['name']='Affected App Deploy — AgentPortal'
        self.invoke();self.assertEqual(3,len(self.reads));self.assertEqual([],self.sleeps)
    def test_selector_pending_regression_during_fresh_proof_does_not_read_head_early(self):
        good=dict(total_count=1,jobs=[self.job])
        self.inventories=[good,self.pending(),good]
        self.invoke();self.assertEqual([2],self.sleeps)
        self.assertEqual(1,sum(p.startswith('git/ref/') for p in self.reads))
    def test_long_pending_phase_does_not_consume_fresh_proof_budget(self):
        self.inventories=[self.pending() for _ in range(59)]+[dict(total_count=1,jobs=[self.job])]
        def slow_head(*args):
            value=self.api(*args)
            if args[1].startswith('git/ref/'):self.now+=29
            return value
        self.assertEqual(self.run,self.invoke(slow_head));self.assertEqual(324,self.now)
        self.assertEqual(295,sum(self.sleeps))
    def test_positive_wait_read_at_exact_pending_deadline_is_rejected(self):
        def slow(*args):
            value=self.api(*args)
            if '/jobs?' in args[1]:self.now=300
            return value
        with self.assertRaises(m.EvidenceLookupUnavailable):self.invoke(slow)
        self.assertEqual(2,len(self.reads))
    def test_observation_count_is_bounded_even_if_injected_clock_does_not_advance(self):
        self.inventories=[self.pending()]
        with patch.object(self,'sleep'):
            with self.assertRaisesRegex(RuntimeError,'pending_deadline_exhausted'):self.invoke()
        self.assertEqual(122,len(self.reads))


class LiveWorkflowIdentityPollingTests(unittest.TestCase):
    def setUp(self):
        self.now=0; self.sleeps=[]; self.reads=[]
        self.revision='a'*40
        self.run=dict(id=123,run_attempt=2,head_sha=self.revision,status='queued',event='push',
            path='.github/workflows/'+m.FAST_TRACK_RELEASE_WORKFLOW,head_branch=m.FAST_TRACK_PR_BASE,
            head_repository=dict(full_name='owner/repo'))
        self.env=dict(GITHUB_ACTIONS='true',GITHUB_EVENT_NAME='push',GITHUB_REPOSITORY='owner/repo',
            GITHUB_TOKEN='test-token',GITHUB_SHA=self.revision,GITHUB_RUN_ID='123',GITHUB_RUN_ATTEMPT='2',
            GITHUB_REF='refs/heads/'+m.FAST_TRACK_PR_BASE,GITHUB_JOB='deploy-ui',TARGET_KEY='portal',RUNNER_NAME='shared',
            GITHUB_WORKFLOW_REF='owner/repo/.github/workflows/'+m.FAST_TRACK_RELEASE_WORKFLOW+'@refs/heads/'+m.FAST_TRACK_PR_BASE)
        self.job=dict(id=10,run_id=123,run_attempt=2,head_sha=self.revision,status='in_progress',
            conclusion=None,name='Affected App Deploy — AgentPortal',runner_name='shared')
        self.runs=[self.run];self.inventories=[dict(total_count=1,jobs=[self.job])]
        self.head=self.revision;self.pr=None
    def sleep(self,seconds):
        self.sleeps.append(seconds);self.now+=seconds
    def api(self,repository,path,token):
        self.reads.append(path)
        if '/jobs?' in path:
            return self.inventories.pop(0) if len(self.inventories)>1 else self.inventories[0]
        if path.startswith('git/ref/'):
            return dict(object=dict(sha=self.head))
        if path.startswith('pulls/'):
            return self.pr
        return self.runs.pop(0) if len(self.runs)>1 else self.runs[0]
    def production(self,target='portal'):
        with patch.dict(os.environ,self.env,clear=True),patch.object(m,'api_get',side_effect=self.api), \
             patch.object(m,'_fast_track_policy') as policy:
            policy.return_value.tool_promotion_requested.return_value=False
            return m.assert_fast_track_release_execution(target=target,clock=lambda:self.now,sleep=self.sleep)
    def source(self,workflow='masterapp-platform-architecture-validation.yml',key='validate',name='architecture-validation',
               source=None,preflight=True):
        self.env.update(GITHUB_JOB=key)
        self.run.update(event='pull_request',path='.github/workflows/'+workflow,
            pull_requests=[dict(number=7,base=dict(ref=m.FAST_TRACK_PR_BASE),head=dict(sha=self.revision))])
        self.pr=self.pr or dict(number=7,state='open',base=dict(ref=m.FAST_TRACK_PR_BASE),
            head=dict(sha=self.revision,repo=dict(full_name='owner/repo')))
        self.job['name']=name or key
        source=source if source is not None else ('jobs:\n  '+key+':\n'+('    name: '+name+'\n' if name else '')+
            '    steps:\n      - name: Plan exact validation resume\n        run: python3 scripts/validation-resume.py plan\n')
        args=SimpleNamespace(repository='owner/repo',current_run_id=123,run_attempt=2,current_sha=self.revision,
            workflow=workflow,event='pull_request')
        with patch.dict(os.environ,self.env,clear=True),patch.object(m,'api_get',side_effect=self.api), \
             patch.object(m,'_release_history_source',return_value=source), \
             patch.object(m,'approved_head_preflight',return_value=dict(current=preflight,approvedHeadSha='b'*40)):
            return m.fast_track_source_validation_scope(args,clock=lambda:self.now,sleep=self.sleep)
    def test_empty_then_unassigned_then_exact_active_target_is_only_success_boundary(self):
        self.inventories=[dict(total_count=0,jobs=[]),dict(total_count=1,jobs=[self.job|dict(status='queued',runner_name='')]),self.inventories[0]]
        self.assertEqual('queued',self.production()['status']);self.assertEqual([2,2],self.sleeps)
    def test_shared_runner_cannot_substitute_another_application(self):
        sibling=self.job|dict(id=11,name='Affected App Deploy — Parfait')
        self.inventories=[dict(total_count=2,jobs=[sibling,self.job])]
        self.assertEqual('queued',self.production()['status'])
        self.inventories=[dict(total_count=1,jobs=[sibling])]
        with self.assertRaises((RuntimeError,m.EvidenceLookupUnavailable)):self.production()
        self.assertEqual(30,self.now);self.assertLessEqual(len(self.sleeps),15)
    def test_target_binding_is_required_even_for_in_progress_parent_before_reads(self):
        self.run['status']='in_progress'
        for value in ('','unknown','protect'):
            with self.subTest(value=value):
                self.env['TARGET_KEY']=value
                with self.assertRaisesRegex(RuntimeError,'ACTIVE_JOB_(?:TARGET_)?UNPROVEN'):self.production()
        self.assertEqual([],self.reads)
    def test_truncated_malformed_duplicate_foreign_or_terminal_job_fails_without_poll(self):
        cases=[dict(total_count=2,jobs=[self.job]),dict(total_count=1,jobs=[None]),
            dict(total_count=2,jobs=[self.job,self.job|dict(id=11)])]
        cases += [dict(total_count=1,jobs=[self.job|{key:value}]) for key,value in (
            ('head_sha','b'*40),('run_attempt',1),('run_id',124),('runner_name','other'),
            ('status','completed'),('status','unknown'),('id',None))]
        for payload in cases:
            with self.subTest(payload=payload):
                self.inventories=[payload]
                with self.assertRaisesRegex(RuntimeError,'ACTIVE_JOB_(?:TARGET_)?UNPROVEN'):self.production()
                self.assertEqual([],self.sleeps)
    def test_pending_then_immutable_or_terminal_mismatch_never_retries_mismatch(self):
        self.inventories=[dict(total_count=0,jobs=[])]
        for change in (dict(status='completed'),dict(head_sha='b'*40),dict(run_attempt=3),
                       dict(head_repository=dict(full_name='fork/repo')),dict(path='untrusted')):
            with self.subTest(change=change):
                self.sleeps=[];self.runs=[self.run,self.run|change]
                with self.assertRaisesRegex(RuntimeError,'PRODUCER_UNPROVEN'):self.production()
                self.assertEqual([2],self.sleeps)
    def test_release_head_is_rechecked_after_pending_observation(self):
        self.inventories=[dict(total_count=0,jobs=[]),self.inventories[0]];self.head='b'*40
        with self.assertRaisesRegex(RuntimeError,'HEAD_CHANGED'):self.production()
        self.assertEqual([],self.sleeps)
    def test_source_still_queued_requires_its_exact_planner_display_name(self):
        for workflow,key,name in [('masterapp-platform-architecture-validation.yml','validate','architecture-validation'),
                ('approved-release-security-validation.yml','validation_plan','security-validation-plan'),
                ('parfait-isolated-validation.yml','build-and-focused-tests',None)]:
            with self.subTest(workflow=workflow):
                result=self.source(workflow,key,name)
                self.assertEqual('source-validation-only',result['phase']);self.assertFalse(result['releaseAdmission'])
        self.assertEqual([],self.sleeps)
    def test_source_queued_then_in_progress_retains_source_only_scope(self):
        self.inventories=[dict(total_count=0,jobs=[])]
        self.runs=[self.run,self.run|dict(status='in_progress',event='pull_request',
            path='.github/workflows/masterapp-platform-architecture-validation.yml',
            pull_requests=[dict(number=7,base=dict(ref=m.FAST_TRACK_PR_BASE),head=dict(sha=self.revision))])]
        self.assertFalse(self.source()['releaseAdmission']);self.assertEqual([5],self.sleeps)
    def test_source_sibling_only_never_grants_scope(self):
        self.inventories=[dict(total_count=1,jobs=[self.job|dict(name='Approved-head preflight')])]
        with self.assertRaisesRegex(ValueError,'pending_deadline_exhausted'):self.source()
        self.assertEqual(30,self.now)
    def test_source_unknown_dynamic_or_ambiguous_planner_metadata_fails_without_poll(self):
        for source in ('jobs:\n  sibling:\n    steps: []\n',
                'jobs:\n  validate:\n    name: ${{ matrix.name }}\n    steps:\n      - name: Plan exact validation resume\n',
                'jobs:\n  validate:\n    name: first\n    name: second\n    steps:\n      - name: Plan exact validation resume\n'):
            with self.subTest(source=source):
                with self.assertRaisesRegex(ValueError,'ACTIVE_JOB_UNPROVEN'):self.source(source=source)
                self.assertEqual([],self.sleeps)
    def test_pr_and_base_change_after_wait_still_fail(self):
        for mutation in ('head','base'):
            with self.subTest(mutation=mutation):
                self.inventories=[dict(total_count=0,jobs=[]),dict(total_count=1,jobs=[self.job])]
                self.pr=dict(number=7,state='open',base=dict(ref=m.FAST_TRACK_PR_BASE),
                    head=dict(sha='b'*40 if mutation=='head' else self.revision,repo=dict(full_name='owner/repo')))
                with self.assertRaisesRegex(ValueError,'PR_CHANGED' if mutation=='head' else 'BASE_CHANGED'):
                    self.source(preflight=mutation!='base')
    def test_nested_transport_backoff_consumes_the_same_deadline(self):
        def timeout(*args,**kwargs):
            self.assertEqual(30,kwargs['timeout']);self.now+=29;raise TimeoutError()
        with patch.dict(os.environ,self.env,clear=True),patch.object(m.urllib.request,'urlopen',side_effect=timeout) as read, \
             patch.object(m.time,'sleep',side_effect=self.sleep):
            with self.assertRaises(m.EvidenceLookupUnavailable):
                m.assert_fast_track_release_execution(target='portal',allow_nonpublishing=True,clock=lambda:self.now,sleep=self.sleep)
        read.assert_called_once();self.assertEqual(30,self.now)
    def test_success_returned_after_deadline_cannot_authorize(self):
        api=self.api
        def slow(*args):
            value=api(*args)
            if '/jobs?' in args[1]:self.now=31
            return value
        with patch.object(self,'api',side_effect=slow):
            with self.assertRaises(m.EvidenceLookupUnavailable):self.production()
        self.assertFalse(any(path.startswith('git/ref/') for path in self.reads))
    def test_terminal_conclusion_in_pending_run_or_job_is_immediately_rejected(self):
        for status in ('queued','in_progress'):
            self.run.update(status=status,conclusion='failure')
            with self.assertRaisesRegex(RuntimeError,'PRODUCER_UNPROVEN'):self.production()
        self.run.update(status='queued',conclusion=None)
        self.inventories=[dict(total_count=1,jobs=[self.job|dict(status='queued',runner_name='',conclusion='failure')])]
        with self.assertRaisesRegex(RuntimeError,'contradictory_job_state'):self.production()
        self.assertEqual([],self.sleeps)


class FastTrackImpactTests(unittest.TestCase):
    def scope(self, *paths):
        return m.fast_track_scope(paths)

    def targets(self, *paths):
        return {row['target'] for row in self.scope(*paths)['matrix']['include']}

    def test_finance_changes_select_only_finance_consumers(self):
        self.assertEqual(self.targets('SHARED/wwwroot/js/finance-shell.js'), {'AgentPortal', 'ClientApp'})

    def test_linked_storefront_asset_selects_both_runtime_hosts(self):
        self.assertEqual(self.targets('ParfaitApp/wwwroot/js/parfait-agentportal-analytics.js'), {'Parfait', 'Protect'})

    def test_reincluded_company_icon_selects_original_presentation_host(self):
        self.assertEqual(self.targets('ParfaitApp/wwwroot/images/company-icons/sample.svg'), {'Parfait', 'Protect'})

    def test_single_star_view_link_does_not_include_nested_directory(self):
        self.assertEqual(self.targets('ParfaitApp/Views/Store/Unlinked/Nested.cshtml'), {'Parfait'})

    def test_shared_business_authorization_selects_four_dotnet_consumers(self):
        result = self.scope('Infrastructure/Businesses/BusinessWorkspaceAccess.cs')
        self.assertTrue(result['commerce'])
        self.assertEqual({r['target'] for r in result['matrix']['include']}, {'AgentPortal', 'ClientApp', 'Protect', 'Parfait'})

    def test_payment_schema_impact_is_explicit_and_grants_no_admission(self):
        result = self.scope('Infrastructure/Migrations/20261011064911_AddBusinessScopedMerchantPayments.cs')
        self.assertTrue(result['schema'])
        self.assertTrue(result['commerce'])
        self.assertNotIn('releaseAdmission', result)
        self.assertEqual(len(result['matrix']['include']), 4)

    def test_cms_bundle_selects_only_its_two_consumers(self):
        self.assertEqual(self.targets('SHARED/WebsitePlatform/legend-public-cms.js'), {'Protect', 'Website'})

    def test_reporting_and_retention_changes_select_all_shared_server_consumers(self):
        for path in ('Infrastructure/Analytics/MarketingMeasurementEvidenceService.cs',
                     'Infrastructure/WebsitePublishing/WebsiteSnapshotRetention.cs',
                     'Domain/Entities/WebsiteContentState.cs'):
            self.assertEqual(self.targets(path), {'AgentPortal', 'ClientApp', 'Protect', 'Parfait'})

    def test_routing_is_explicit_without_application_upload(self):
        result = self.scope('Legend-Cloudflare/src/website-routing/bridge.mjs')
        self.assertTrue(result['routing'])
        self.assertTrue(result['commerce'])
        self.assertFalse(result['selected'])

    def test_test_and_documentation_only_select_no_applications(self):
        self.assertFalse(self.scope('docs/parfait-canonical-transition/business-scoped-payments.sql',
            'diagnostics/MerchantPaymentCoreTests/ControllerTestHelpers.cs',
            'Legend-Cloudflare/tests/website-routing/bridge.test.mjs',
            'scripts/test-parfait-readonly-workflow.py')['selected'])

    def test_unknown_shared_source_blocks_instead_of_blanket_deploy(self):
        with self.assertRaisesRegex(ValueError, 'Unmapped fast-track source path'):
            self.scope('Infrastructure/UnreviewedDependency.cs')


class ReceptionistReleaseImpactTests(unittest.TestCase):
    # Complete reviewed voice candidate, including its migration and CI inputs.
    # This fixture exercises the real aggregate boundary, not a service-only subset.
    voice_paths = (
        'Domain/Entities/MarketingConnection.cs',
        'Domain/Entities/ReceptionistCoordination.cs',
        'SHARED/Analytics/ReceptionistConfiguration.cs',
        'Infrastructure/Analytics/MarketingConnectionStore.cs',
        'Infrastructure/Analytics/MarketingConnectionStore.Receptionist.cs',
        'Infrastructure/Reception/ReceptionistContracts.cs',
        'Infrastructure/Reception/ReceptionistSql.cs',
        'Infrastructure/Reception/ReceptionistAdmissionService.cs',
        'Infrastructure/Reception/ReceptionistAdmissionService.Accounting.cs',
        'Infrastructure/Reception/ReceptionistAdmissionService.ProviderWork.cs',
        'Infrastructure/Data/Configurations/ReceptionistModelConfiguration.cs',
        'Infrastructure/Data/MasterAppDbContext.cs',
        'AgentPortal.Tests/ReceptionistCoordinationTests.cs',
        'Infrastructure/Migrations/20261020160000_AddReceptionistCoordination.cs',
        'Infrastructure/Migrations/20261020160000_AddReceptionistCoordination.Designer.cs',
        'Infrastructure/Migrations/MasterAppDbContextModelSnapshot.cs',
        'Infrastructure/Reception/ReceptionistCredentialEnvelope.cs',
        'Infrastructure/Reception/ReceptionistOperatorContracts.cs',
        'Infrastructure/Reception/ReceptionistOperatorService.cs',
        'Infrastructure/Reception/ReceptionistAdmissionService.Operator.cs',
        'Infrastructure/Analytics/MarketingConnectionStore.ReceptionistOperator.cs',
        'AgentPortal.Tests/ReceptionistOperatorTests.cs',
        'Infrastructure/Leads/WebsiteLeadSubmission.cs',
        'Infrastructure/Leads/IWebsiteLifeLeadCaptureService.cs',
        'Infrastructure/Leads/WebsiteLifeLeadCaptureService.cs',
        'Infrastructure/Leads/WorkstationLeadBuckets.cs',
        'Infrastructure/Leads/WebsiteIntakeRecipientResolver.cs',
        'Infrastructure/Leads/WebsiteLeadNotificationAuthority.cs',
        'Infrastructure/Leads/WebsiteLeadAppNotificationService.cs',
        'Infrastructure/Leads/WebsiteLeadEmailTemplate.cs',
        'Infrastructure/Leads/WebsiteLeadServiceRegistration.cs',
        'Infrastructure/Leads/ReceptionistInquiryContracts.cs',
        'Infrastructure/Leads/ReceptionistInquiryAuthority.cs',
        'AgentPortal.Tests/ReceptionistInquiryTests.cs',
        'AgentPortal/Controllers/LeadsController.cs',
        'AgentPortal/Views/Leads/_LeadPipelineView.cshtml',
        'AgentPortal/Views/Leads/_LeadQuickView.cshtml',
        'AgentPortal/wwwroot/js/leads-index.js',
        'Infrastructure/Reception/ReceptionistApplicationServices.cs',
        'Infrastructure/Reception/ReceptionistApplicationContracts.cs',
        'AgentPortal.Tests/ReceptionistApplicationServicesTests.cs',
        'AgentPortal.Tests/ReceptionistRuntimeWorkerTests.cs',
        'AgentPortal.Tests/ReceptionistSetupAndAuthorityTests.cs',
        'AgentPortal.Tests/ReceptionistTransportTests.cs',
        'AgentPortal/Controllers/WebsiteAnalyticsController.Receptionist.cs',
        'AgentPortal/Controllers/WebsiteAnalyticsController.cs',
        'AgentPortal/Program.cs',
        'AgentPortal/Views/WebsiteAnalytics/Index.cshtml',
        'AgentPortal/wwwroot/js/website-analytics.js',
        'Infrastructure/Businesses/BusinessWorkspaceControllerBase.Receptionist.cs',
        'Infrastructure/Reception/IOpenAiReceptionistClient.cs',
        'Infrastructure/Reception/OpenAiReceptionistClient.cs',
        'Infrastructure/Reception/OpenAiReceptionistProtocol.cs',
        'Infrastructure/Reception/ReceptionistAdmissionService.Runtime.cs',
        'Infrastructure/Reception/ReceptionistIngressController.cs',
        'Infrastructure/Reception/ReceptionistRuntimeAuthority.cs',
        'Infrastructure/Reception/ReceptionistRuntimeWorker.cs',
        'Infrastructure/Reception/ReceptionistServiceRegistration.cs',
        'Infrastructure/Reception/ReceptionistSetupForms.cs',
        'Infrastructure/Reception/ReceptionistSidebandProtocol.cs',
        'SHARED/Analytics/ReceptionistSetup.cs',
        'SHARED/Views/Shared/BusinessWorkspaceSettings.cshtml',
        'SHARED/Views/Shared/ReceptionistSetup.cshtml',
        'AgentPortal/Controllers/FounderEngineeringController.cs',
        'AgentPortal/Controllers/FounderEngineeringController.Receptionist.cs',
        'AgentPortal/Models/FounderReceptionistOperatorViewModel.cs',
        'AgentPortal/Views/FounderEngineering/Index.cshtml',
        'AgentPortal/Views/FounderEngineering/Receptionist.cshtml',
        '.github/workflows/masterapp-platform-architecture-validation.yml',
        'scripts/validation-resume.py',
        'scripts/test-validation-resume.py',
    )
    managed = {'portal', 'client', 'protect', 'parfait'}
    model = 'Infrastructure/Data/Configurations/ReceptionistModelConfiguration.cs'
    migration_audit_paths = (
        'scripts/MigrationReleaseProbe/CandidateContract.cs',
        'scripts/MigrationReleaseProbe/CandidateContract.Structures.cs',
        'scripts/MigrationReleaseProbe/Rehearsal/CandidateContractTests.cs',
        'scripts/MigrationReleaseProbe/Rehearsal/RehearsalFixture.cs',
    )

    def test_complete_voice_inventory_requires_exact_managed_apps_and_schema(self):
        self.assertEqual(71, len(set(self.voice_paths)))
        with patch.object(m, 'api_get') as provider, patch.object(m, 'assert_fast_track_release_execution') as admit:
            result = m.fast_track_scope(self.voice_paths)
        self.assertEqual(self.managed, {row['key'] for row in result['matrix']['include']})
        self.assertTrue(result['selected'])
        self.assertTrue(result['commerce'])
        self.assertTrue(result['schema'])
        self.assertFalse(result['routing'])
        self.assertNotIn('releaseAdmission', result)
        self.assertEqual(set(self.voice_paths), set(result['impacts']))
        provider.assert_not_called()
        admit.assert_not_called()

    def test_migration_probe_audit_overlay_preserves_exact_voice_publication_scope(self):
        original = m.fast_track_scope(self.voice_paths)
        expanded = m.fast_track_scope(self.voice_paths + self.migration_audit_paths)
        self.assertEqual(75, len(expanded['impacts']))
        for key in ('matrix', 'selected', 'commerce', 'schema', 'routing'):
            self.assertEqual(original[key], expanded[key], key)
        for path in self.migration_audit_paths:
            self.assertEqual([], expanded['impacts'][path])
        audit_only = m.fast_track_scope(self.migration_audit_paths)
        self.assertEqual({'include': []}, audit_only['matrix'])
        for flag in ('selected', 'commerce', 'schema', 'routing'):
            self.assertFalse(audit_only[flag], flag)

    def test_shared_server_inputs_preserve_four_consumers_without_implied_schema_write(self):
        runtime = [path for path in self.voice_paths
                   if path.startswith(('Infrastructure/', 'Domain/', 'SHARED/Analytics/'))
                   and not path.startswith('Infrastructure/Migrations/')
                   and path not in {self.model, 'Infrastructure/Data/MasterAppDbContext.cs'}]
        for path in runtime:
            with self.subTest(path=path):
                result = m.fast_track_scope([path])
                self.assertEqual(self.managed, set(result['impacts'][path]))
                self.assertTrue(result['commerce'])
                self.assertFalse(result['schema'])
                self.assertFalse(result['routing'])
                self.assertNotIn('releaseAdmission', result)

    def test_actual_ef_model_mapping_requires_schema_readiness_without_admission(self):
        result = m.fast_track_scope([self.model])
        self.assertEqual(self.managed, set(result['impacts'][self.model]))
        self.assertTrue(result['commerce'])
        self.assertTrue(result['schema'])
        self.assertFalse(result['routing'])
        self.assertNotIn('releaseAdmission', result)

    def test_bounded_booking_and_privacy_delta_preserves_all_managed_consumers(self):
        paths = (
            'Infrastructure/Bookings/BusinessBookingAccess.cs',
            'Infrastructure/Bookings/MicrosoftCalendarConnectionAuthority.cs',
            'Infrastructure/Bookings/ReceptionistBookingAuthority.cs',
            'Infrastructure/Bookings/ReceptionistBookingContracts.cs',
            'Infrastructure/Bookings/ReceptionistBookingReadinessReader.cs',
            'Infrastructure/Identity/RestrictedAiAccessBoundary.cs',
            'Infrastructure/Leads/ReceptionistInquiryAuthority.Outcomes.cs',
            'Infrastructure/Reception/ReceptionistAdmissionService.Booking.cs',
            'Infrastructure/Reception/ReceptionistAdmissionService.Projections.cs',
            'Infrastructure/Reception/ReceptionistApplicationSession.Booking.cs',
        )
        for path in paths:
            with self.subTest(path=path):
                result = m.fast_track_scope([path])
                self.assertEqual(self.managed, set(result['impacts'][path]))
                self.assertTrue(result['commerce'])
                self.assertFalse(result['schema'])
                self.assertFalse(result['routing'])
                self.assertNotIn('releaseAdmission', result)
        with self.assertRaisesRegex(ValueError, 'Unmapped fast-track source path'):
            m.fast_track_scope(['Infrastructure/Bookings/UnreviewedAdapter.cs'])

    def test_shared_consumers_are_proven_by_actual_project_references(self):
        import posixpath
        import xml.etree.ElementTree as ET
        def references(project):
            root = ET.fromstring((ROOT / project).read_text())
            return {posixpath.normpath(str(Path(project).parent) + '/' + row.attrib['Include'].replace('\\', '/'))
                    for row in root.iter('ProjectReference')}
        self.assertTrue({'Domain/Domain.csproj', 'SHARED/Shared.csproj'} <= references('Infrastructure/Infrastructure.csproj'))
        for key in self.managed:
            with self.subTest(target=key):
                self.assertTrue({'Infrastructure/Infrastructure.csproj', 'Domain/Domain.csproj'} <=
                                references(m.RELEASE_TARGETS[key]['project']))
        self.assertTrue(m.RELEASE_TARGETS['website']['static'])

    def test_new_unknown_siblings_remain_unmapped(self):
        for path in ('Infrastructure/Reception/UnreviewedProvider.cs',
                     'Infrastructure/Leads/UnreviewedCapture.cs',
                     'Infrastructure/Businesses/UnreviewedWorkspace.cs',
                     'Infrastructure/Data/Configurations/UnreviewedModel.cs',
                     'Domain/Entities/UnreviewedCoordination.cs',
                     'SHARED/Analytics/UnreviewedConfiguration.cs'):
            with self.subTest(path=path), self.assertRaisesRegex(ValueError, 'Unmapped fast-track source path'):
                m.fast_track_scope([path])

    def test_diagnostic_three_file_change_stays_nonpublishing(self):
        paths = ['.github/workflows/deployment-diagnostics.yml', 'scripts/deploy-approved-app.py',
                 'scripts/test-deploy-approved-app.py']
        result = m.fast_track_scope(paths)
        self.assertEqual({'include': []}, result['matrix'])
        self.assertEqual(dict.fromkeys(paths, []), result['impacts'])
        for flag in ('selected', 'commerce', 'schema', 'routing'):
            self.assertFalse(result[flag], flag)

    def test_receptionist_test_and_ci_inputs_alone_stay_nonpublishing(self):
        result = m.fast_track_scope([path for path in self.voice_paths if path.startswith(
            ('AgentPortal.Tests/', '.github/', 'scripts/'))])
        self.assertEqual({'include': []}, result['matrix'])
        for flag in ('selected', 'commerce', 'schema', 'routing'):
            self.assertFalse(result[flag], flag)


class FastTrackSourceValidationTests(unittest.TestCase):
    def setUp(self):
        self.sha = 'b' * 40
        self.args = SimpleNamespace(workflow='approved-release-security-validation.yml',
            event='pull_request', repository='owner/repo', current_run_id=91, current_sha=self.sha, run_attempt=1)
        self.run = dict(id=91, run_attempt=1, event='pull_request', path='.github/workflows/' + self.args.workflow,
            head_sha=self.sha, head_repository=dict(full_name='owner/repo'), status='in_progress',
            pull_requests=[dict(number=580, base=dict(ref=m.FAST_TRACK_PR_BASE), head=dict(sha=self.sha))])
        self.pr = dict(number=580, state='open', base=dict(ref=m.FAST_TRACK_PR_BASE),
            head=dict(sha=self.sha, repo=dict(full_name='owner/repo')))

    def scope(self):
        with patch.dict(m.os.environ, {'GITHUB_ACTIONS': 'true', 'GITHUB_TOKEN': 'test-token'}), \
             patch.object(m, 'api_get', side_effect=[self.run, self.pr]), \
             patch.object(m, 'approved_head_preflight', return_value={'current': True, 'approvedHeadSha': 'a' * 40}) as preflight:
            result = m.fast_track_source_validation_scope(self.args)
            preflight.assert_called_once_with('owner/repo', self.sha, 'test-token', base_branch=m.FAST_TRACK_PR_BASE)
            return result

    def test_architecture_pr_uses_exact_fast_track_scope_without_old_readiness(self):
        self.args.workflow = 'masterapp-platform-architecture-validation.yml'
        self.run['path'] = '.github/workflows/' + self.args.workflow
        self.assertEqual(m.FAST_TRACK_PR_BASE, self.scope()['baseBranch'])

    def development(self):
        self.args.workflow = 'parfait-isolated-validation.yml'
        self.args.event = 'push'
        self.args.head_branch = 'isolation/task'
        self.run.update(event='push', path='.github/workflows/' + self.args.workflow,
            head_branch=self.args.head_branch, pull_requests=[])

    def test_push_without_pr_is_development_only_and_never_queries_pr_or_base(self):
        self.development()
        with patch.dict(m.os.environ, {'GITHUB_ACTIONS':'true','GITHUB_TOKEN':'test-token'}), \
             patch.object(m, 'api_get', return_value=self.run) as api, \
             patch.object(m, 'approved_head_preflight') as preflight:
            scope = m.fast_track_source_validation_scope(self.args)
        self.assertEqual('development-validation-only', scope['phase'])
        self.assertFalse(scope['releaseAdmission'])
        self.assertNotIn('pullRequest', scope)
        api.assert_called_once()
        preflight.assert_not_called()

    def test_genuine_isolated_pr_uses_strict_source_contract(self):
        self.args.workflow = 'parfait-isolated-validation.yml'
        self.run['path'] = '.github/workflows/' + self.args.workflow
        self.assertEqual('source-validation-only', self.scope()['phase'])

    def test_wrong_attempt_is_rejected(self):
        self.run['run_attempt'] = 2
        with self.assertRaisesRegex(ValueError, 'IDENTITY_MISMATCH'):
            self.scope()

    def test_stale_run_candidate_is_rejected(self):
        self.run['head_sha'] = 'c' * 40
        with self.assertRaisesRegex(ValueError, 'IDENTITY_MISMATCH'):
            self.scope()

    def test_changed_current_base_is_rejected(self):
        with patch.dict(m.os.environ, {'GITHUB_ACTIONS':'true','GITHUB_TOKEN':'test-token'}), \
             patch.object(m, 'api_get', side_effect=[self.run,self.pr]), \
             patch.object(m, 'approved_head_preflight', return_value={'current':False}), \
             self.assertRaisesRegex(ValueError, 'FAST_TRACK_BASE_CHANGED'):
            m.fast_track_source_validation_scope(self.args)

    def test_development_branch_and_repository_cannot_be_forged(self):
        self.development()
        for branch, repository in [('Legend/fast-track-release','owner/repo'), ('isolation/task','fork/repo')]:
            self.args.head_branch = branch
            self.run.update(head_branch=branch, head_repository=dict(full_name=repository))
            with self.subTest(branch=branch, repository=repository), \
                 patch.dict(m.os.environ, {'GITHUB_ACTIONS':'true','GITHUB_TOKEN':'test-token'}), \
                 patch.object(m, 'api_get', return_value=self.run), self.assertRaises(ValueError):
                m.fast_track_source_validation_scope(self.args)

    def test_development_plan_has_no_source_admission_envelope(self):
        self.development()
        with tempfile.TemporaryDirectory() as directory:
            self.args.validation_base = m.FAST_TRACK_PR_BASE
            self.args.output = str(Path(directory) / 'plan.json')
            with patch.dict(m.os.environ, {'GITHUB_ACTIONS':'true','GITHUB_TOKEN':'test-token'}), \
                 patch.object(m, 'api_get', return_value=self.run), \
                 patch.object(m, '_compute_validation_plan_once', return_value={'sourceValidationScope':{'forged':True}}), \
                 patch.object(m, 'require_readiness') as readiness:
                m.cmd_plan(self.args)
            plan = json.loads(Path(self.args.output).read_text())
            self.assertNotIn('sourceValidationScope', plan)
            self.assertFalse(plan['developmentValidationScope']['releaseAdmission'])
            readiness.assert_not_called()

    def test_workflow_binds_pr_head_in_checkout_plan_and_sql_contract(self):
        source = (ROOT / '.github/workflows/parfait-isolated-validation.yml').read_text()
        self.assertIn('  pull_request:\n    branches:\n      - Legend/fast-track-release', source)
        candidate = '${{ github.event.pull_request.head.sha || github.sha }}'
        for prefix in ('ref: ', '--current-sha "', '--tool-revision "', '--application-revision "'):
            self.assertIn(prefix + candidate, source)
        self.assertIn('VALIDATION_HEAD_BRANCH: ${{ github.head_ref || github.ref_name }}', source)
        self.assertIn('--head-branch "$VALIDATION_HEAD_BRANCH"', source)
        self.assertNotIn('--head-branch "${{', source)

    def test_authenticated_fast_track_source_proof_is_not_release_admission(self):
        result = self.scope()
        self.assertFalse(result['releaseAdmission'])
        self.assertEqual('source-validation-only', result['phase'])

    def test_changed_pr_head_is_rejected(self):
        self.pr['head']['sha'] = 'c' * 40
        with self.assertRaisesRegex(ValueError, 'PR_CHANGED'):
            self.scope()

    def test_other_base_is_rejected(self):
        self.run['pull_requests'][0]['base']['ref'] = m.TRUSTED_PR_BASE
        with self.assertRaisesRegex(ValueError, 'PR_UNPROVEN'):
            self.scope()

    def test_unrelated_workflow_identity_is_rejected(self):
        self.run['path'] = '.github/workflows/unrelated.yml'
        with self.assertRaisesRegex(ValueError, 'IDENTITY_MISMATCH'):
            self.scope()

    def test_fast_track_preflight_never_queries_protected_base(self):
        with patch.object(m, 'api_get', side_effect=[{'commit': {'sha': 'a'*40}},
                {'status': 'ahead', 'merge_base_commit': {'sha': 'a'*40}}]) as api:
            result = m.approved_head_preflight('owner/repo', self.sha, 'token', base_branch=m.FAST_TRACK_PR_BASE)
        self.assertTrue(result['current'])
        self.assertEqual('branches/Legend%2Ffast-track-release', api.call_args_list[0].args[1])

    def test_fork_source_is_rejected(self):
        self.pr['head']['repo']['full_name'] = 'other/repo'
        with self.assertRaisesRegex(ValueError, 'PR_CHANGED'):
            self.scope()

    def test_protected_base_cache_is_not_reused_for_fast_track(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'cache.json'
            path.write_text(m.json.dumps(dict(workflow=self.args.workflow, headSha=self.sha, runId=80,
                schemaVersion=2, validationBase=m.TRUSTED_PR_BASE, gates={})))
            self.args.resume_cache = str(path)
            with m._validation_base_scope(m.FAST_TRACK_PR_BASE), patch.object(m.subprocess, 'run') as git:
                self.assertIsNone(m._cached_success_evidence(self.args))
                git.assert_not_called()

    def test_validation_base_scope_cannot_change_production_authority(self):
        with m._validation_base_scope(m.FAST_TRACK_PR_BASE):
            self.assertEqual(m.FAST_TRACK_PR_BASE, m._VALIDATION_PR_BASE.get())
            self.assertEqual('legend/approved-changes', m.TRUSTED_PR_BASE)
            self.assertFalse(m._trusted_lineage_run('owner/repo', dict(self.run,
                pull_requests=[dict(base=dict(ref=m.TRUSTED_PR_BASE))]), self.run['path'], self.sha))
        self.assertEqual(m.TRUSTED_PR_BASE, m._VALIDATION_PR_BASE.get())


class IsolatedDevelopmentReuseTests(unittest.TestCase):
    def setUp(self):
        self.workflow = 'parfait-isolated-validation.yml'
        self.sha = 'b' * 40
        self.args = SimpleNamespace(workflow=self.workflow,event='pull_request',repository='owner/repo',
            current_run_id=91,current_sha=self.sha,run_attempt=1,head_branch='isolation/task')
        self.run = dict(id=81,run_attempt=1,event='push',path='.github/workflows/'+self.workflow,
            head_sha=self.sha,head_branch='isolation/task',head_repository=dict(full_name='owner/repo'),
            status='completed',conclusion='success')
        self.runs = [self.run]
        self.artifact = dict(id=101,name='validation-resume-parfait-81-1',expired=False,workflow_run=dict(id=81))
        self.artifacts = [self.artifact]
        self.step = m.WORKFLOWS[self.workflow]['gates']['routing']['step']
        self.receipt = dict(producingRunId=81,reused=False,producerJobId=201,recordingJobId=201,
            producerStepNumber=8,stepNumber=8,result='success')
        self.stored = dict(workflow=self.workflow,currentSha=self.sha,validationBase=m.FAST_TRACK_PR_BASE,
            developmentValidationScope=dict(phase='development-validation-only',releaseAdmission=False,
                candidate=self.sha,branch='isolation/task',runId=81,runAttempt=1),
            receiptSchemaVersion=1,recordingRunId=81,
            gates={'routing':dict(step=self.step,run=True,receipt=self.receipt)})

    def prior(self):
        def api(repo,path,token):
            if 'event=pull_request' in path:
                return dict(total_count=0,workflow_runs=[])
            if 'event=push' in path:
                return dict(total_count=len(self.runs),workflow_runs=self.runs)
            if path == 'actions/runs/81/artifacts?per_page=100':
                return dict(total_count=len(self.artifacts),artifacts=self.artifacts)
            raise AssertionError(path)
        def download(repo,run,name,path,**kwargs):
            self.assertEqual(101,kwargs['artifact_id'])
            (path/'validation-resume.json').write_text(json.dumps(self.stored))
        with patch.dict(m.os.environ, {'GITHUB_TOKEN':'fixture'}), \
             patch.object(m,'api_get',side_effect=api), \
             patch.object(m,'_download_run_artifact',side_effect=download):
            return m.prior_evidence(self.args)

    def test_exact_push_child_reused_through_existing_dependency_authority(self):
        prior, steps, source = self.prior()
        self.assertEqual('exact_development_child_receipts', source)
        self.assertEqual(81, steps.producers[self.step]['runId'])
        identities = {key:{'contentIdentity':'a'*64} for key in m.WORKFLOWS[self.workflow]['gates']}
        with patch.object(m,'git_changed',return_value=[]), \
             patch.object(m,'gate_dependency_manifests',return_value=identities), \
             patch.object(m,'_historical_workflow_definition',return_value=m.WORKFLOWS[self.workflow]):
            plan = m._plan_against_prior(self.workflow,self.sha,prior,steps,source)
        self.assertFalse(plan['gates']['routing']['run'])
        self.assertTrue(plan['gates']['payments']['run'])
        self.assertEqual(81,plan['gates']['routing']['producerReceipt']['runId'])

    def test_green_push_without_actual_child_receipt_is_not_reused(self):
        self.stored['gates']['routing'].pop('receipt')
        self.assertIsNone(self.prior()[0])

    def test_latest_failed_child_cannot_borrow_older_green_push(self):
        self.runs.append(dict(self.run,id=71))
        self.run['conclusion'] = 'failure'
        self.receipt['result'] = 'failure'
        prior,steps,source = self.prior()
        self.assertEqual(81,prior['id'])
        self.assertEqual('failure',steps[self.step])
        plan = m.compute_plan(self.workflow,self.sha,prior,steps,[],source)
        self.assertTrue(plan['gates']['routing']['run'])

    def test_full_planner_does_not_mask_latest_development_failure_with_old_pr(self):
        self.run['conclusion'] = 'failure'
        self.receipt['result'] = 'failure'
        prior = self.prior()
        with patch.object(m,'_cached_success_evidence',return_value=None), \
             patch.object(m,'prior_evidence',return_value=prior), \
             patch.object(m,'git_changed',return_value=[]), \
             patch.object(m,'gate_dependency_manifests',return_value={}), \
             patch.object(m,'_apply_content_equivalent_evidence') as overlay:
            plan=m._compute_validation_plan_once(self.args)
        self.assertTrue(plan['gates']['routing']['run'])
        self.assertTrue(plan['gates']['payments']['run'])
        overlay.assert_not_called()

    def test_other_candidate_branch_repository_or_event_is_not_reused(self):
        for key,value in [('head_sha','c'*40),('head_branch','isolation/other'),
                          ('head_repository',dict(full_name='fork/repo')),('event','workflow_dispatch')]:
            old=self.run[key];self.run[key]=value
            with self.subTest(key=key):self.assertIsNone(self.prior()[0])
            self.run[key]=old

    def test_missing_expired_duplicate_or_unbound_artifact_is_not_reused(self):
        for rows in ([],[dict(self.artifact,expired=True)], [self.artifact,self.artifact],
                     [dict(self.artifact,workflow_run=dict(id=71))]):
            self.artifacts=rows
            with self.subTest(rows=rows):self.assertIsNone(self.prior()[0])

    def test_changed_or_forged_development_envelope_is_not_reused(self):
        original=dict(self.stored['developmentValidationScope'])
        for key,value in [('candidate','c'*40),('runId',71),('runAttempt',2),('releaseAdmission',True)]:
            self.stored['developmentValidationScope']=original | {key:value}
            with self.subTest(key=key):self.assertIsNone(self.prior()[0])
        self.stored['developmentValidationScope']=original
        self.stored['sourceValidationScope']={'phase':'source-validation-only'}
        self.assertIsNone(self.prior()[0])

    def test_changed_dependency_identity_invalidates_push_child(self):
        prior,steps,source=self.prior()
        ids=lambda value:{key:{'contentIdentity':value*64} for key in m.WORKFLOWS[self.workflow]['gates']}
        with patch.object(m,'git_changed',return_value=[]), \
             patch.object(m,'gate_dependency_manifests',side_effect=[ids('a'),ids('b')]), \
             patch.object(m,'_historical_workflow_definition',return_value=m.WORKFLOWS[self.workflow]):
            plan=m._plan_against_prior(self.workflow,self.sha,prior,steps,source)
        self.assertTrue(plan['gates']['routing']['run'])


class GitHubEvidenceTransportTests(unittest.TestCase):
    def test_transient_remote_disconnect_is_retried_without_restarting_evidence_flow(self):
        class Response:
            def __enter__(self):
                return self

            def __exit__(self, *_):
                return False

            def read(self):
                return b'{"ok":true}'

        with patch.object(
            m.urllib.request,
            "urlopen",
            side_effect=[m.http.client.RemoteDisconnected("transient"), Response()],
        ) as request, patch.object(m.time, "sleep") as sleep:
            result = m.api_get("owner/repo", "branches/legend%2Fapproved-changes", "token")

        self.assertEqual({"ok": True}, result)
        self.assertEqual(2, request.call_count)
        sleep.assert_called_once_with(1)


class ProtectedReleaseExecutionTests(unittest.TestCase):
    def environment(self):
        return {
            "GITHUB_ACTIONS": "true",
            "GITHUB_EVENT_NAME": "workflow_dispatch",
            "GITHUB_REF": "refs/heads/legend/approved-changes",
            "GITHUB_REPOSITORY": "MYLEGND/masterapp",
            "GITHUB_TOKEN": "fixture-token",
            "GITHUB_WORKFLOW_REF": (
                "MYLEGND/masterapp/.github/workflows/"
                + m.DIRECT_RELEASE_WORKFLOW
                + "@refs/heads/legend/approved-changes"
            ),
            "GITHUB_SHA": "a" * 40,
            "GITHUB_RUN_ID": "42",
        }

    def run_record(self, **changes):
        value = {
            "id": 42,
            "path": ".github/workflows/" + m.DIRECT_RELEASE_WORKFLOW,
            "head_branch": m.TRUSTED_PR_BASE,
            "event": "workflow_dispatch",
            "head_sha": "a" * 40,
            "head_repository": {"full_name": "MYLEGND/masterapp"},
            "status": "in_progress",
        }
        value.update(changes)
        return value

    def test_authenticated_protected_release_execution_is_accepted(self):
        with patch.dict(m.os.environ, self.environment(), clear=True), \
             patch.object(m, "api_get", return_value=self.run_record()) as api:
            result = m.assert_protected_release_execution()
        self.assertEqual(42, result["id"])
        api.assert_called_once_with("MYLEGND/masterapp", "actions/runs/42", "fixture-token")

    def test_local_or_wrong_workflow_execution_is_rejected(self):
        with patch.dict(m.os.environ, {}, clear=True), \
             patch.object(m, "api_get") as api:
            with self.assertRaisesRegex(RuntimeError, "GitHub Actions"):
                m.assert_protected_release_execution()
        api.assert_not_called()

        env = self.environment()
        env["GITHUB_WORKFLOW_REF"] = (
            "MYLEGND/masterapp/.github/workflows/deployment-diagnostics.yml"
            "@refs/heads/legend/approved-changes"
        )
        with patch.dict(m.os.environ, env, clear=True), \
             patch.object(m, "api_get") as api:
            with self.assertRaisesRegex(RuntimeError, "identity is incomplete"):
                m.assert_protected_release_execution()
        api.assert_not_called()

    def test_forged_environment_without_matching_run_is_rejected(self):
        with patch.dict(m.os.environ, self.environment(), clear=True), \
             patch.object(m, "api_get", return_value=self.run_record(head_branch="other")):
            with self.assertRaisesRegex(RuntimeError, "not owned"):
                m.assert_protected_release_execution()


class Step5DecisionFastFailTests(unittest.TestCase):
    def test_changed_step5_job_skips_expensive_baseline_history_scan(self):
        candidate = {
            "runId": 99,
            "headSha": "a" * 40,
            "artifact": "step5-candidate-" + "a" * 40,
        }
        with patch.dict(m.os.environ, {"GITHUB_TOKEN": "fixture-token"}), \
             patch.object(m, "_step5_prior_candidate_evidence", return_value=candidate), \
             patch.object(m, "_step5_candidate_producer_compatible", return_value=False), \
             patch.object(m, "compute_step5_baseline_evidence") as baseline:
            result = m.compute_step5_decision(
                "owner/repo",
                "b" * 40,
                "c" * 40,
                100,
                "repair/work",
            )
        baseline.assert_not_called()
        self.assertEqual("full", result["mode"])
        self.assertEqual("candidate_or_baseline_job_changed", result["reason"])
        self.assertEqual(99, result["priorRunId"])
        self.assertEqual("a" * 40, result["priorHeadSha"])


class ValidationResumePlannerTests(unittest.TestCase):
    def test_planner_errors_stop_before_expensive_children(self):
        for command, computation in ((m.cmd_plan, "prior_evidence"),
                                     (m.cmd_step5_decision, "compute_step5_decision"),
                                     (m.cmd_step5_baseline, "compute_step5_baseline_evidence")):
            with self.subTest(command=command.__name__), tempfile.TemporaryDirectory() as directory:
                args = SimpleNamespace(output=str(Path(directory) / "plan.json"),
                    workflow="step5-isolated-conversion-mapping-validation.yml",
                    repository="owner/repo", current_sha="a" * 40, base_sha="b" * 40,
                    current_run_id=4, head_branch="repair")
                with patch.object(m, computation, side_effect=TimeoutError()), self.assertRaises(SystemExit) as stopped:
                    command(args)
                self.assertEqual(1, stopped.exception.code)
                record = m.json.loads(Path(args.output).read_text())
                self.assertEqual("blocked", record["mode"])
                self.assertNotIn("gates", record)

    def test_evidence_get_retries_transient_timeout_only(self):
        import io
        with patch.object(m.urllib.request, "urlopen", side_effect=[TimeoutError(), io.BytesIO(b'{"ok":true}')]) as request, patch.object(m.time, "sleep"):
            self.assertEqual({"ok": True}, m.api_get("owner/repo", "actions/runs", "fixture"))
            self.assertEqual(2, request.call_count)
        with patch.object(m.urllib.request, "urlopen", side_effect=TimeoutError()) as request, patch.object(m.time, "sleep"):
            with self.assertRaises(m.EvidenceLookupUnavailable):
                m.api_get("owner/repo", "actions/runs", "fixture")
            self.assertEqual(3, request.call_count)
        denied = m.urllib.error.HTTPError("https://api.github.com", 403, "denied", {}, None)
        with patch.object(m.urllib.request, "urlopen", side_effect=denied) as request:
            with self.assertRaises(m.EvidenceLookupUnavailable):
                m.api_get("owner/repo", "actions/runs", "fixture")
            self.assertEqual(1, request.call_count)

    def test_denied_evidence_reports_status_and_path_without_credentials(self):
        denied = m.urllib.error.HTTPError("https://api.github.com", 403, "denied", {}, None)
        with patch.object(m.urllib.request, "urlopen", side_effect=denied):
            with self.assertRaises(m.EvidenceLookupUnavailable) as caught:
                m.api_get("owner/repo", "actions/runs/7/jobs?per_page=100", "secret-fixture")
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(output=str(Path(directory) / "plan.json"))
            with self.assertRaises(SystemExit):
                m._stop_unresolved_planning(args, caught.exception)
            content = Path(args.output).read_text()
            record = m.json.loads(content)
            self.assertEqual(403, record["evidenceHttpStatus"])
            self.assertEqual("actions/runs/7/jobs", record["evidenceEndpoint"])
            self.assertNotIn("secret-fixture", content)
            self.assertNotIn("per_page", content)

    def test_rate_limited_pr_plan_blocks_before_expensive_children(self):
        error = m.EvidenceLookupUnavailable(
            "rate limited", status=403, endpoint="actions/workflows/example/runs"
        )
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(
                output=str(Path(directory) / "plan.json"),
                workflow="approved-release-security-validation.yml",
                repository="owner/repo",
                current_sha=m.subprocess.check_output(
                    ["git", "rev-parse", "HEAD"], text=True
                ).strip(),
                current_run_id=4,
                run_attempt=1,
                head_branch="repair",
                event="pull_request",
                resume_cache=None,
            )
            with patch.object(m, "prior_evidence", side_effect=error) as lookup, \
                 patch.object(m.time, "sleep") as sleeper, \
                 self.assertRaises(SystemExit) as stopped:
                m.cmd_plan(args)
            self.assertEqual(1, stopped.exception.code)
            self.assertEqual(4, lookup.call_count)
            self.assertEqual(3, sleeper.call_count)
            plan = m.json.loads(Path(args.output).read_text())
        self.assertEqual("blocked", plan["mode"])
        self.assertEqual("planner_unavailable_resume_planning_only", plan["reason"])
        self.assertEqual(403, plan["evidenceHttpStatus"])
        self.assertNotIn("gates", plan)

    def test_partial_pr_local_gate_cache_reuses_only_checkpointed_children(self):
        workflow = "approved-release-security-validation.yml"
        head = m.subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
        with tempfile.TemporaryDirectory() as directory:
            cache = Path(directory) / "validation-resume.json"
            cache.write_text(m.json.dumps({
                "schemaVersion": 2,
                "workflow": workflow,
                "headSha": head,
                "runId": 41,
                "runAttempt": 1,
                "gates": {"no-skips": {"result": "success"}},
            }))
            args = SimpleNamespace(
                output=str(Path(directory) / "plan.json"),
                workflow=workflow,
                repository="owner/repo",
                current_sha=head,
                current_run_id=42,
                run_attempt=1,
                head_branch="repair",
                event="pull_request",
                resume_cache=str(cache),
            )
            # This fixture has no additional historical receipts. Exercise the
            # actual local child cache and dependency checks without querying a
            # synthetic owner/repo; the adjacent test owns historical backfill.
            with patch.object(m, "prior_evidence") as remote, \
                 patch.object(m, "_apply_content_equivalent_evidence", side_effect=lambda args, plan: plan):
                m.cmd_plan(args)
            remote.assert_not_called()
            plan = m.json.loads(Path(args.output).read_text())
        self.assertFalse(plan["gates"]["no-skips"]["run"])
        self.assertEqual("pr_local_gate_cache", plan["gates"]["no-skips"]["evidenceSource"])
        self.assertTrue(plan["gates"]["secret-scan"]["run"])
        self.assertTrue(plan["gates"]["composition"]["run"])

    def test_partial_cache_backfills_missing_unchanged_gates_from_trusted_history(self):
        workflow = "approved-release-security-validation.yml"
        prior = {"id": 41, "head_sha": "a" * 40, "run_attempt": 1}
        steps = m._StepEvidence()
        steps["Reject skipped security tests"] = "success"
        steps.producers["Reject skipped security tests"] = {
            "result": "success", "runId": 41, "jobId": None, "stepNumber": None,
        }
        initial = {
            "workflow": workflow,
            "gates": {
                key: {"step": gate["step"], "run": key != "no-skips", "reason": "prior_gate_not_successful"}
                for key, gate in m.WORKFLOWS[workflow]["gates"].items()
            },
        }
        backfilled = {
            **initial,
            "gates": {
                key: {**row, "run": False, "reason": "content_equivalent_success"}
                for key, row in initial["gates"].items()
            },
        }
        args = SimpleNamespace(
            workflow=workflow,
            current_sha="b" * 40,
            event="pull_request",
            repository="owner/repo",
            current_run_id=42,
            head_branch="repair",
            resume_cache="fixture",
        )
        with patch.object(m, "_cached_success_evidence",
                          return_value=(prior, steps, "pr_local_gate_cache")), \
             patch.object(m, "_plan_against_prior", return_value=initial), \
             patch.object(m, "_stamp_evidence"), \
             patch.object(m, "_apply_content_equivalent_evidence", return_value=backfilled) as backfill, \
             patch.object(m, "gate_dependency_manifests", return_value={}):
            plan = m._compute_validation_plan_once(args)
        backfill.assert_called_once_with(args, initial)
        self.assertTrue(all(not gate["run"] for gate in plan["gates"].values()))

    def test_gate_cache_carries_only_proven_successful_children(self):
        workflow = "step6-openai-ads-execution-validation.yml"
        plan = {
            "workflow": workflow,
            "gates": {
                "restore": {"run": False, "producerReceipt": {"result": "success"}},
                "build": {"run": True, "receipt": {"result": "success"}},
                "tests": {"run": True, "receipt": {"result": "failure"}},
            },
        }
        payload = m._gate_cache_payload(plan, workflow, "a" * 40, 77, 2)
        self.assertEqual({"restore", "build"}, set(payload["gates"]))
        self.assertNotIn("tests", payload["gates"])

    def test_rate_limited_step5_decision_falls_back_to_full_validation(self):
        error = m.EvidenceLookupUnavailable(
            "rate limited", status=403, endpoint="actions/workflows/step5/runs"
        )
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(
                output=str(Path(directory) / "decision.json"),
                repository="owner/repo",
                current_sha="a" * 40,
                base_sha="b" * 40,
                current_run_id=4,
                head_branch="repair",
            )
            with patch.object(m, "compute_step5_decision", side_effect=error):
                m.cmd_step5_decision(args)
            decision = m.json.loads(Path(args.output).read_text())
        self.assertEqual("full", decision["mode"])
        self.assertEqual(
            "historical_evidence_unavailable_run_full_step5",
            decision["reason"],
        )
        self.assertEqual(403, decision["evidenceFallback"]["httpStatus"])

    def test_rate_limited_step5_baseline_runs_fresh_baseline(self):
        error = m.EvidenceLookupUnavailable(
            "rate limited", status=403, endpoint="actions/artifacts"
        )
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(
                output=str(Path(directory) / "baseline.json"),
                repository="owner/repo",
                base_sha="b" * 40,
            )
            with patch.object(m, "compute_step5_baseline_evidence", side_effect=error):
                m.cmd_step5_baseline(args)
            result = m.json.loads(Path(args.output).read_text())
        self.assertFalse(result["reusable"])
        self.assertEqual(
            "historical_evidence_unavailable_run_fresh_baseline",
            result["reason"],
        )
        self.assertEqual(403, result["evidenceFallback"]["httpStatus"])

    def test_rate_limited_migration_probe_plan_builds_fresh_instead_of_failing(self):
        probe_spec = importlib.util.spec_from_file_location(
            "migration_probe_package",
            ROOT / "scripts" / "migration-probe-package.py",
        )
        probe = importlib.util.module_from_spec(probe_spec)
        probe_spec.loader.exec_module(probe)
        identity = {
            "artifact": "legend-migration-probe-" + "d" * 64,
            "identity": "d" * 64,
        }
        error = probe.AUTHORITY.EvidenceLookupUnavailable(
            "rate limited", status=403, endpoint="actions/workflows/example/runs"
        )
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "outputs"
            args = [
                "migration-probe-package.py", "plan",
                "--tool-revision", "a" * 40,
                "--application-revision", "a" * 40,
                "--directory", str(Path(directory) / "probe"),
                "--output", str(output),
            ]
            with patch.object(probe.AUTHORITY, "migration_probe_identity", return_value=identity), \
                 patch.object(probe.AUTHORITY, "migration_probe_evidence", side_effect=error), \
                 patch.dict(probe.os.environ, {"GITHUB_REPOSITORY": "owner/repo"}), \
                 patch("sys.argv", args):
                probe.main()
            values = dict(
                line.split("=", 1)
                for line in output.read_text().splitlines()
                if "=" in line
            )
        self.assertEqual("true", values["needed"])
        self.assertEqual(identity["artifact"], values["artifact"])
        self.assertEqual(identity["identity"], values["identity"])

    def test_candidate_artifact_transport_failure_is_not_missing_evidence(self):
        run = {"id": 7, "head_sha": "a" * 40}
        with patch.object(m, "api_get", return_value={"workflow_runs": [run]}), \
             patch.object(m, "_trusted_lineage_run", return_value=True), \
             patch.object(m, "step5_dependency_change", return_value=[]), \
             patch.object(m, "_step5_candidate_producer_compatible", return_value=True), \
             patch.object(m, "_run_artifact_names", side_effect=m.EvidenceLookupUnavailable()):
            with self.assertRaises(m.EvidenceLookupUnavailable):
                m._step5_prior_candidate_evidence("owner/repo", 9, "repair", "fixture", "b" * 40)

    def test_historical_lookup_transport_failure_cannot_return_full_plan(self):
        args = SimpleNamespace(event="pull_request")
        with patch.dict(m.os.environ, {"GITHUB_TOKEN": "fixture"}), \
             patch.object(m, "_trusted_historical_runs", side_effect=m.EvidenceLookupUnavailable()):
            with self.assertRaises(m.EvidenceLookupUnavailable):
                m._apply_content_equivalent_evidence(args, {"gates": {"one": {"run": True}}})

    def test_baseline_artifact_transport_failure_is_not_missing_evidence(self):
        with patch.dict(m.os.environ, {"GITHUB_TOKEN": "fixture"}), \
             patch.object(m, "_artifact_rows", return_value=[{"workflow_run": {"id": 7}}]), \
             patch.object(m, "api_get", side_effect=m.EvidenceLookupUnavailable()):
            with self.assertRaises(m.EvidenceLookupUnavailable):
                m.compute_step5_baseline_evidence("owner/repo", "a" * 40)

    def test_only_extension_methods_create_name_only_dependency_edges(self):
        source = """
        public static Result CreateClient() => new();
        protected override Task SendAsync(Request request) => null;
        public static Result Configure(this Client client) => null;
        internal static async Task ApplyAsync<T>(this T client) => null;
        """
        self.assertEqual({"Configure", "ApplyAsync"}, m._step5_extension_method_names(source))

    def test_complete_discovery_excludes_helpers_without_hiding_new_test_classes(self):
        classes = ["Tests.RealTests", "Tests.HelperController"]
        names = ["Tests.RealTests.Check"]
        self.assertEqual(["Tests.RealTests"], m._step5_discovered_repair_classes(
            classes, names, ["scripts/validation-resume.py"]))
        self.assertIsNone(m._step5_discovered_repair_classes(
            classes, names, ["AgentPortal.Tests/NewTests.cs"]))

    def successful_steps(self, workflow):
        return {
            gate["step"]: "success"
            for gate in m.WORKFLOWS[workflow]["gates"].values()
        }

    def prior(self, sha="a" * 40):
        return {"id": 17, "head_sha": sha, "run_attempt": 1}

    def write_trx(self, rows):
        with tempfile.NamedTemporaryFile("w", suffix=".trx", delete=False) as handle:
            failed = sum(outcome == "Failed" for _, outcome in rows)
            passed = sum(outcome == "Passed" for _, outcome in rows)
            not_executed = sum(outcome == "NotExecuted" for _, outcome in rows)
            handle.write(
                '<TestRun><Results>' +
                ''.join(f'<UnitTestResult testName="{name}" outcome="{outcome}" />' for name, outcome in rows) +
                '</Results><ResultSummary outcome="Completed"><Counters ' +
                f'total="{len(rows)}" executed="{passed + failed}" passed="{passed}" failed="{failed}" ' +
                f'notExecuted="{not_executed}" error="0" timeout="0" aborted="0" disconnected="0" ' +
                'inProgress="0" pending="0" /></ResultSummary></TestRun>'
            )
            return Path(handle.name)

    def test_step5_trx_concordant_duplicate_identity_collapses_safely(self):
        path = self.write_trx([
            ("AgentPortal.Tests.ExampleTests.Case", "Passed"),
            ("AgentPortal.Tests.ExampleTests.Case", "Passed"),
        ])
        self.addCleanup(path.unlink, missing_ok=True)
        self.assertEqual(
            {"AgentPortal.Tests.ExampleTests.Case": "Passed"},
            m.read_step5_results(path),
        )

    def test_step5_trx_conflicting_duplicate_identity_fails_closed(self):
        path = self.write_trx([
            ("AgentPortal.Tests.ExampleTests.Case", "Passed"),
            ("AgentPortal.Tests.ExampleTests.Case", "Failed"),
        ])
        self.addCleanup(path.unlink, missing_ok=True)
        with self.assertRaisesRegex(ValueError, "Ambiguous duplicate test identity"):
            m.read_step5_results(path)

    def test_record_evidence_defers_current_run_403_without_fabricating_success(self):
        plan = {
            "workflow": "approved-release-security-validation.yml",
            "gates": {
                "diff-check": {
                    "step": "Verify patch whitespace integrity",
                    "run": True,
                    "reason": "gate_inputs_changed",
                }
            },
        }
        with tempfile.TemporaryDirectory() as directory:
            plan_path = Path(directory) / "plan.json"
            output_path = Path(directory) / "out.json"
            plan_path.write_text(__import__("json").dumps(plan))
            error = m.urllib.error.HTTPError(
                "https://api.github.com/example", 403, "Forbidden", {}, None
            )
            args = SimpleNamespace(
                plan=str(plan_path),
                output=str(output_path),
                repository="MYLEGND/masterapp",
                run_id=123,
            )
            with patch.object(m.urllib.request, "urlopen", side_effect=error), \
                 patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
                m.cmd_record_evidence(args)
            recorded = __import__("json").loads(output_path.read_text())
        self.assertEqual(
            "current_run_actions_observation_forbidden",
            recorded["receiptRecordingDeferred"],
        )
        self.assertNotIn("receipt", recorded["gates"]["diff-check"])

    def historical_plan_steps(self, parent_conclusion, gates, **metadata):
        run = {
            "id": 77,
            "run_attempt": 1,
            "conclusion": parent_conclusion,
        }
        args = SimpleNamespace(
            workflow="approved-release-security-validation.yml",
            repository="MYLEGND/masterapp",
        )
        artifact = "validation-resume-security-77-1"
        stored = {"workflow": args.workflow, "gates": gates, **metadata}
        def download(_repo, _run_id, _artifact, directory):
            Path(directory, "validation-resume.json").write_text(
                __import__("json").dumps(stored)
            )
        with patch.object(m, "_run_artifact_names", return_value={artifact}), \
             patch.object(m, "_download_run_artifact", side_effect=download):
            return m._historical_plan_steps(args, run, "token")

    def test_successful_parent_plan_proves_executed_child_without_jobs_api(self):
        steps = self.historical_plan_steps("success", {
            "diff-check": {
                "step": "Verify patch whitespace integrity",
                "run": True,
            }
        })
        self.assertEqual("success", steps["Verify patch whitespace integrity"])
        self.assertEqual(
            77,
            steps.producers["Verify patch whitespace integrity"]["runId"],
        )

    def test_failed_parent_retains_its_exact_recorded_child_results(self):
        gates = {}
        for index, result in enumerate(("success", "failure", "cancelled"), 1):
            gates[result] = {"step": result, "run": True, "receipt": {
                "result": result, "producingRunId": 77, "producerJobId": 900,
                "producerStepNumber": index, "recordingJobId": 900,
                "stepNumber": index, "reused": False}}
        steps = self.historical_plan_steps("failure", gates,
                                          receiptSchemaVersion=1, recordingRunId=77)
        self.assertEqual({name: name for name in gates}, dict(steps))
        self.assertEqual(900, steps.producers["success"]["jobId"])
        self.assertEqual(77, steps.producers["success"]["runId"])
        self.assertEqual(1, steps.producers["success"]["stepNumber"])

    def test_failed_parent_rejects_mismatched_or_incomplete_executed_receipts(self):
        valid = {"result": "success", "producingRunId": 77, "producerJobId": 900,
                 "producerStepNumber": 4, "recordingJobId": 900, "stepNumber": 4, "reused": False}
        for key, value in (("producingRunId", 78), ("producerJobId", None),
                           ("recordingJobId", 901), ("stepNumber", 5), ("reused", True),
                           ("result", "unproven")):
            with self.subTest(key=key):
                receipt = dict(valid, **{key: value})
                steps = self.historical_plan_steps("failure", {
                    "gate": {"step": "gate", "run": True, "receipt": receipt}},
                    receiptSchemaVersion=1, recordingRunId=77)
                self.assertNotIn("gate", steps)
        steps = self.historical_plan_steps("failure", {
            "gate": {"step": "gate", "run": True, "receipt": valid}},
            receiptSchemaVersion=1, recordingRunId=78)
        self.assertNotIn("gate", steps)

    def test_failed_parent_plan_preserves_only_prior_green_child(self):
        steps = self.historical_plan_steps("failure", {
            "executed-later-failure": {
                "step": "A gate that ran in failed parent",
                "run": True,
            },
            "preserved": {
                "step": "An older preserved green gate",
                "run": False,
                "evidenceRunId": 44,
                "producerReceipt": {
                    "result": "success",
                    "runId": 44,
                },
            },
        })
        self.assertNotIn("A gate that ran in failed parent", steps)
        self.assertEqual("success", steps["An older preserved green gate"])
        self.assertEqual(
            44,
            steps.producers["An older preserved green gate"]["runId"],
        )

    def test_effective_steps_keeps_latest_executed_failure_and_backfills_only_skips(self):
        effective = m._effective_steps([
            {
                "gate-a": "failure",
                "gate-b": "skipped",
                "gate-c": "success",
            },
            {
                "gate-a": "success",
                "gate-b": "success",
                "gate-c": "failure",
            },
        ])
        self.assertEqual("failure", effective["gate-a"])
        self.assertEqual("success", effective["gate-b"])
        self.assertEqual("success", effective["gate-c"])

    def test_computed_single_file_readers_do_not_poison_gate_with_entire_repository(self):
        source = """
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "Protect-Website", "Controllers", fileName);
        var text = File.ReadAllText(path);
        var info = new DirectoryInfo(Directory.GetCurrentDirectory());
        Assert.False(File.Exists(path));
        """
        self.assertNotIn("**", m._test_file_dependency_patterns(source))
        self.assertEqual(
            ("**",),
            m._test_file_dependency_patterns("Directory.GetFiles(root);"),
        )

    def test_compile_regression_does_not_claim_runtime_source_contract_files(self):
        gate = m.WORKFLOWS["masterapp-platform-architecture-validation.yml"]["gates"]["compile-regression"]
        self.assertFalse(gate["runtime_file_dependencies"])

    def test_regression_consumers_materialize_compile_without_semantic_sibling_invalidation(self):
        gates = m.WORKFLOWS["masterapp-platform-architecture-validation.yml"]["gates"]
        for key in (
            "founder-diagnostics-regressions",
            "website-regressions",
            "meta-regressions",
            "booking-regressions",
            "crm-regressions",
        ):
            self.assertEqual(("compile-regression",), gates[key]["materializes"], key)
            self.assertNotIn("requires", gates[key], key)
            self.assertNotIn("consumes", gates[key], key)
        self.assertEqual(
            ("compile-regression",),
            gates["release-web-contracts"]["materializes"],
        )
        self.assertEqual(("domain-release",), gates["release-web-contracts"]["consumes"])
        self.assertNotIn("requires", gates["release-web-contracts"])

    def test_successful_parent_is_complete_gate_proof_without_plan_artifact_download(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        run = {"id": 77, "conclusion": "success"}
        steps = m._successful_parent_steps(workflow, run)
        expected = {gate["step"] for gate in m.WORKFLOWS[workflow]["gates"].values()}
        self.assertEqual(expected, set(steps))
        self.assertTrue(all(value == "success" for value in steps.values()))
        self.assertTrue(all(row["runId"] == 77 for row in steps.producers.values()))

    def test_content_equivalent_lookup_stops_at_nearest_successful_parent(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        args = SimpleNamespace(
            event="pull_request",
            workflow=workflow,
            repository="MYLEGND/masterapp",
            current_run_id=99,
            current_sha="c" * 40,
        )
        plan = {
            "workflow": workflow,
            "gates": {
                key: {"step": gate["step"], "run": True, "reason": "no_prior_success_evidence"}
                for key, gate in m.WORKFLOWS[workflow]["gates"].items()
            },
        }
        candidate = {
            "workflow": workflow,
            "gates": {
                key: {"step": gate["step"], "run": True, "reason": "gate_inputs_changed"}
                for key, gate in m.WORKFLOWS[workflow]["gates"].items()
            },
        }
        runs = [
            {"id": 77, "head_sha": "a" * 40, "conclusion": "success", "run_attempt": 1},
            {"id": 66, "head_sha": "b" * 40, "conclusion": "success", "run_attempt": 1},
        ]
        with patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}), \
             patch.object(m, "_trusted_historical_runs", return_value=runs), \
             patch.object(m, "_historical_plan_steps",
                          side_effect=AssertionError("successful parent must not download plan artifact")), \
             patch.object(m, "_plan_against_prior", return_value=candidate) as compare, \
             patch.object(m, "merge_content_equivalent_evidence", return_value=True):
            result = m._apply_content_equivalent_evidence(args, plan)
        self.assertIs(result, plan)
        self.assertEqual(1, compare.call_count)
        self.assertEqual(1, result["historicalEvidenceRunsExamined"])

    def test_unchanged_successes_are_preserved(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "a" * 40,
            self.prior(),
            self.successful_steps(workflow),
            [],
            "prior_run",
        )
        self.assertEqual("incremental", plan["mode"])
        self.assertTrue(all(not gate["run"] for gate in plan["gates"].values()))

    def test_failed_gate_is_invalidated_while_unrelated_green_gate_is_preserved(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        steps = self.successful_steps(workflow)
        steps["Run booking authority regressions"] = "failure"
        plan = m.compute_plan(
            workflow,
            "a" * 40,
            self.prior(),
            steps,
            [],
            "prior_attempt",
        )
        self.assertTrue(plan["gates"]["booking-regressions"]["run"])
        self.assertFalse(plan["gates"]["compile-regression"]["run"])
        self.assertFalse(plan["gates"]["restore-dotnet"]["run"])
        self.assertFalse(plan["gates"]["renderer-tests"]["run"])
        self.assertFalse(plan["gates"]["cms-tests"]["run"])
        self.assertFalse(plan["gates"]["form-tracking"]["run"])

    def test_test_only_fix_reruns_only_affected_test_and_required_build_chain(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal.Tests/PublicBookingResolverTests.cs"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["booking-regressions"]["run"])
        self.assertTrue(plan["gates"]["compile-regression"]["run"])
        self.assertTrue(plan["gates"]["restore-dotnet"]["run"])
        self.assertFalse(plan["gates"]["meta-regressions"]["run"])
        self.assertFalse(plan["gates"]["website-regressions"]["run"])
        self.assertFalse(plan["gates"]["crm-regressions"]["run"])
        self.assertFalse(plan["gates"]["renderer-tests"]["run"])

    def test_receptionist_test_only_fix_reruns_meta_and_required_build_chain(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        for test_class in (
            "ReceptionistCoordinationTests",
            "ReceptionistInquiryTests",
            "ReceptionistApplicationServicesTests",
            "ReceptionistTransportTests",
            "ReceptionistSetupAndAuthorityTests",
            "ReceptionistRuntimeWorkerTests",
            "ReceptionistOperatorTests",
        ):
            with self.subTest(test_class=test_class):
                plan = m.compute_plan(
                    workflow,
                    "b" * 40,
                    self.prior(),
                    self.successful_steps(workflow),
                    [f"AgentPortal.Tests/{test_class}.cs"],
                    "prior_run",
                )
                self.assertTrue(plan["gates"]["meta-regressions"]["run"])
                self.assertEqual("gate_inputs_changed", plan["gates"]["meta-regressions"]["reason"])
                self.assertTrue(plan["gates"]["compile-regression"]["run"])
                self.assertTrue(plan["gates"]["restore-dotnet"]["run"])
                for gate in (
                    "website-regressions", "booking-regressions", "crm-regressions",
                    "renderer-tests", "founder-diagnostics-regressions",
                ):
                    self.assertFalse(plan["gates"][gate]["run"], gate)

    def test_diagnostics_only_source_change_preserves_unrelated_domain_regressions(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal/Services/FounderSoftwareRemediationService.cs"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["build-hosts"]["run"])
        self.assertTrue(plan["gates"]["compile-regression"]["run"])
        self.assertTrue(plan["gates"]["restore-dotnet"]["run"])
        self.assertTrue(plan["gates"]["founder-diagnostics-regressions"]["run"])
        self.assertFalse(plan["gates"]["website-regressions"]["run"])
        self.assertFalse(plan["gates"]["meta-regressions"]["run"])
        self.assertFalse(plan["gates"]["booking-regressions"]["run"])
        self.assertFalse(plan["gates"]["crm-regressions"]["run"])
        self.assertFalse(plan["gates"]["renderer-tests"]["run"])

    def test_diagnostics_test_fix_reruns_only_diagnostics_test_gate_and_build_chain(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal.Tests/FounderRepositoryInspectionTests.cs"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["founder-diagnostics-regressions"]["run"])
        self.assertTrue(plan["gates"]["compile-regression"]["run"])
        self.assertTrue(plan["gates"]["restore-dotnet"]["run"])
        self.assertFalse(plan["gates"]["website-regressions"]["run"])
        self.assertFalse(plan["gates"]["meta-regressions"]["run"])
        self.assertFalse(plan["gates"]["booking-regressions"]["run"])
        self.assertFalse(plan["gates"]["crm-regressions"]["run"])

    def test_backend_source_change_keeps_existing_dotnet_regression_coverage(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal/Controllers/HomeController.cs"],
            "prior_run",
        )
        for gate in (
            "build-hosts",
            "compile-regression",
            "website-regressions",
            "meta-regressions",
            "booking-regressions",
            "crm-regressions",
        ):
            self.assertTrue(plan["gates"][gate]["run"], gate)
        self.assertFalse(plan["gates"]["renderer-tests"]["run"])
        self.assertFalse(plan["gates"]["cms-tests"]["run"])


    def test_single_gate_definition_change_invalidates_only_that_gate(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            [],
            "prior_run",
            {"Verify consolidated release scope and routing policy"},
            False,
        )
        self.assertEqual("incremental", plan["mode"])
        self.assertTrue(plan["gates"]["release-policy"]["run"])
        self.assertEqual("gate_definition_changed", plan["gates"]["release-policy"]["reason"])
        for key, gate in plan["gates"].items():
            if key != "release-policy":
                self.assertFalse(gate["run"], key)

    def test_workflow_structure_change_still_fails_closed(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            [],
            "prior_run",
            set(),
            True,
        )
        self.assertEqual("full", plan["mode"])
        self.assertTrue(all(gate["run"] for gate in plan["gates"].values()))

    def test_gate_scope_masks_only_configured_step_bodies(self):
        prior = """name: X
jobs:
  validate:
    steps:
      - name: Gate A
        run: echo old
      - name: Gate B
        run: echo same
"""
        current = prior.replace("echo old", "echo new")
        config = {"gates": {"a": {"step": "Gate A"}, "b": {"step": "Gate B"}}}
        self.assertNotEqual(m._gate_execution_contract(prior, config, "a"), m._gate_execution_contract(current, config, "a"))
        self.assertEqual(m._gate_execution_contract(prior, config, "b"), m._gate_execution_contract(current, config, "b"))
        environment_edit = "env:\n  MODE: changed\n" + current
        self.assertNotEqual(m._gate_execution_contract(prior, config, "b"), m._gate_execution_contract(environment_edit, config, "b"))

    def test_job_definition_comparison_is_exact_and_bounded(self):
        text = """jobs:
  candidate:
    runs-on: ubuntu-latest
    steps:
      - run: echo candidate
  baseline:
    runs-on: ubuntu-latest
    steps:
      - run: echo baseline
  validate:
    runs-on: ubuntu-latest
    steps:
      - run: echo validate
"""
        blocks = m._job_blocks(text)
        self.assertIn("candidate", blocks)
        self.assertIn("baseline", blocks)
        self.assertNotEqual(blocks["candidate"], blocks["baseline"])

    def test_job_definition_parser_preserves_blank_separated_jobs(self):
        text = """jobs:
  plan:
    runs-on: ubuntu-latest

  candidate:
    runs-on: ubuntu-latest
    steps:
      - run: echo candidate

  baseline:
    runs-on: ubuntu-latest
    steps:
      - run: echo baseline

  validate:
    runs-on: ubuntu-latest
"""
        blocks = m._job_blocks(text)
        self.assertEqual({"plan", "candidate", "baseline", "validate"}, set(blocks))

    def test_real_step5_workflow_exposes_candidate_and_baseline_jobs(self):
        path = ROOT / ".github" / "workflows" / "step5-isolated-conversion-mapping-validation.yml"
        blocks = m._job_blocks(path.read_text())
        for name in ("plan", "baseline-evidence", "candidate", "baseline", "validate"):
            self.assertIn(name, blocks)
        self.assertIn("Run full AgentPortal candidate suite", blocks["candidate"])
        self.assertIn("Run identical suite on approved baseline", blocks["baseline"])

    def test_release_workflow_policy_covers_every_named_direct_release_step(self):
        path = ROOT / ".github" / "workflows" / "all-intentional-direct-release-20260918.yml"
        policies = m.verify_release_policy_coverage(
            "all-intentional-direct-release-20260918.yml",
            path.read_text(),
        )
        self.assertEqual(
            set(m.named_step_blocks(path.read_text())),
            set(policies),
        )

    def test_release_lifecycle_policy_covers_every_named_step(self):
        path = ROOT / ".github" / "workflows" / "legend-release-lifecycle.yml"
        policies = m.verify_release_policy_coverage(
            "legend-release-lifecycle.yml",
            path.read_text(),
        )
        self.assertEqual(
            set(m.named_step_blocks(path.read_text())),
            set(policies),
        )


    def test_step5_candidate_change_invalidates_comparison_but_preserves_unrelated_children(self):
        workflow = "step5-isolated-conversion-mapping-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal.Tests/SomeUnrelatedRegressionTests.cs"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["candidate-build"]["run"])
        self.assertTrue(plan["gates"]["candidate-full"]["run"])
        self.assertTrue(plan["gates"]["comparison"]["run"])
        self.assertEqual(
            "evidence_dependency_invalidated:candidate-full",
            plan["gates"]["comparison"]["reason"],
        )
        self.assertFalse(plan["gates"]["candidate-focused"]["run"])

    def test_step5_workflow_delegates_resume_and_baseline_decisions_to_canonical_authority(self):
        path = ROOT / ".github" / "workflows" / "step5-isolated-conversion-mapping-validation.yml"
        workflow = path.read_text()
        self.assertNotIn("scripts/validation-resume.py plan", workflow)
        self.assertEqual(1, workflow.count("scripts/validation-resume.py step5-decision"))
        self.assertIn("--resume-cache /tmp/step5-resume-cache", workflow)
        self.assertNotIn("PYCACHE", workflow)
        self.assertNotIn("PYGRAPH", workflow)
        self.assertNotIn("graphql_parent_success", workflow)
        self.assertIn("scripts/validation-resume.py step5-baseline", workflow)
        self.assertNotIn('gh api "/repos/$GITHUB_REPOSITORY/actions/artifacts?name=$baseline_name', workflow)
        self.assertIn("historical_evidence_unavailable_run_full_step5", workflow)
        self.assertIn("refusing to discard completed evidence and rerun the full suite", workflow)
        self.assertIn("baseline_run_required", workflow)
        self.assertIn("candidate_restore_run", workflow)
        self.assertIn("candidate_build_run", workflow)
        self.assertIn("candidate_focused_run", workflow)
        self.assertIn("candidate_full_run", workflow)
        self.assertIn("comparison_run", workflow)
        self.assertIn("Recheck only newly introduced Step 5 failure classes", workflow)
        self.assertIn("Preserve effective Step 5 candidate evidence", workflow)
        self.assertIn("Preserve effective Step 5 baseline evidence", workflow)
        self.assertIn("Preserve bounded Step 5 recovery state", workflow)
        self.assertIn("Enforce final Step 5 outcome after bounded recovery", workflow)
        self.assertIn("/tmp/step5-effective/candidate.trx", workflow)
        self.assertIn("/tmp/step5-effective/baseline.trx", workflow)

    def test_new_release_step_is_automatically_fail_closed_without_registry_edit(self):
        path = ROOT / ".github" / "workflows" / "all-intentional-direct-release-20260918.yml"
        workflow = path.read_text() + """
      - name: Future automatically governed release child
        run: echo future
"""
        policies = m.verify_release_policy_coverage(
            "all-intentional-direct-release-20260918.yml",
            workflow,
        )
        self.assertEqual(
            "fail_closed_execute",
            policies["Future automatically governed release child"],
        )

    def test_release_inventory_is_single_canonical_source_for_baseline_and_live_proof(self):
        self.assertTrue(m.RELEASE_TARGETS)
        names = [row["releaseName"] for row in m.RELEASE_TARGETS.values()]
        self.assertEqual(len(names), len(set(names)))
        self.assertTrue(all(row.get("proofHosts") for row in m.RELEASE_TARGETS.values()))
        self.assertTrue(all(row.get("sourceRoot") for row in m.RELEASE_TARGETS.values()))
        self.assertTrue(all(row.get("package") for row in m.RELEASE_TARGETS.values()))

        baseline = (ROOT / "scripts" / "approved-release-baseline.py").read_text()
        self.assertIn("_validation_authority.release_target_rows()", baseline)
        self.assertIn("_validation_authority.selected_release_target_keys", baseline)
        self.assertNotIn("ALLOWED_RELEASE_TARGET_SETS", baseline)

        release = (ROOT / ".github" / "workflows" / "all-intentional-direct-release-20260918.yml").read_text()
        self.assertIn("scripts/validation-resume.py live-state", release)
        self.assertIn("scripts/validation-resume.py verify-live", release)
        self.assertIn("Reconcile complete immutable release transaction", release)
        import subprocess
        subprocess.run(["python3", str(ROOT / "scripts/release-workflow.py"), "--check"], check=True, capture_output=True)
        for row in m.RELEASE_TARGETS.values():
            self.assertNotIn(row["azureHost"], release)
        self.assertNotIn(m.RELEASE_RESOURCE_GROUP, release)
        self.assertNotIn(m.MIGRATION_BUNDLE_NAME, release)
        self.assertNotIn(m.ROUTING_WORKER_NAME, release)
        self.assertNotIn(m.DOMAIN_REFRESH_PROJECT, release)

    def test_lifecycle_identity_hashes_required_absence_and_reintroduction_changes_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            present = Path(directory) / "present.py"
            retired = Path(directory) / "retired.sh"
            present.write_text("canonical")
            with patch.object(m, "LIFECYCLE_AUTHORITY_PATHS", (str(present), str(retired))):
                absent_identity = m.lifecycle_authority_identity()
                self.assertEqual(absent_identity, m.lifecycle_authority_identity())
                retired.write_text("legacy bypass")
                restored_identity = m.lifecycle_authority_identity()
                self.assertNotEqual(absent_identity, restored_identity)
                retired.unlink()
                self.assertEqual(absent_identity, m.lifecycle_authority_identity())

    def test_lifecycle_evidence_keeps_deterministic_artifact_when_protected_path_is_absent(self):
        with tempfile.TemporaryDirectory() as directory:
            present = Path(directory) / "present.py"
            retired = Path(directory) / "retired.sh"
            present.write_text("canonical")
            with patch.object(m, "LIFECYCLE_AUTHORITY_PATHS", (str(present), str(retired))), \
                 patch.dict(m.os.environ, {"GITHUB_TOKEN": ""}, clear=False):
                result = m.compute_lifecycle_evidence("MYLEGND/masterapp")
        self.assertFalse(result["reusable"])
        self.assertEqual("github_token_unavailable", result["reason"])
        self.assertRegex(result["identity"], r"^[0-9a-f]{64}$")
        self.assertEqual(
            "legend-lifecycle-contracts-" + result["identity"],
            result["artifact"],
        )

    def test_lifecycle_and_release_evidence_lookup_are_canonicalized(self):
        lifecycle = (ROOT / ".github" / "workflows" / "legend-release-lifecycle.yml").read_text()
        self.assertNotIn('scripts/validation-resume.py lifecycle-evidence', lifecycle)
        self.assertIn('LEGEND_RELEASE_LIFECYCLE_RETIRED', lifecycle)

        release = (ROOT / ".github" / "workflows" / "all-intentional-direct-release-20260918.yml").read_text()
        validated = release.split("      - name: Reuse exact successful validation package when available\n", 1)[1].split("      - name:", 1)[0]
        rollback = release.split("      - name: Reuse exact retained live package when available\n", 1)[1].split("      - uses:", 1)[0]
        self.assertIn('Checkout current approved evidence authority', release)
        self.assertIn('python3 .legend-evidence-authority/scripts/validation-resume.py validated-package', validated)
        self.assertIn("rollback-evidence", rollback)
        self.assertIn('Checkout canonical rollback package authority', release)
        self.assertIn('scripts/release-package.py restore-rollback', rollback)
        self.assertNotIn('source-equivalent rollback', release)
        self.assertNotIn("gh api", validated)
        self.assertNotIn("gh api", rollback)

    def test_consumed_evidence_invalidates_forward_without_invalidating_siblings(self):
        workflow = "step5-isolated-conversion-mapping-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal.Tests/SomeUnrelatedRegressionTests.cs"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["candidate-full"]["run"])
        self.assertTrue(plan["gates"]["comparison"]["run"])
        self.assertFalse(plan["gates"]["candidate-focused"]["run"])

    def test_merge_readiness_consumes_one_canonical_validation_topology(self):
        topology = m.required_validation_topology(["scripts/validation-resume.py"])
        self.assertEqual(
            {
                ".github/workflows/masterapp-platform-architecture-validation.yml",
                ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
                ".github/workflows/step6-openai-ads-execution-validation.yml",
                ".github/workflows/steps7-8-governed-advertising-validation.yml",
                ".github/workflows/approved-release-security-validation.yml",
            },
            set(topology["required"]),
        )

        lifecycle = (ROOT / "scripts" / "release-lifecycle.py").read_text()
        self.assertIn("VALIDATION_AUTHORITY.required_validation_topology(names)", lifecycle)
        self.assertIn("def candidate_validation(api, pr):", lifecycle)
        self.assertIn("run.get('status') != 'completed'", lifecycle)
        self.assertIn("run.get('conclusion') != 'success'", lifecycle)
        self.assertIn("for attempt in range(4)", lifecycle)
        self.assertNotIn("validation_neutral_path", lifecycle)
        self.assertNotIn("architecture_product_validation", lifecycle)
        self.assertNotIn("architecture_public_website_validation", lifecycle)
        self.assertNotIn("STEP6_VALIDATION_PATHS", lifecycle)
        self.assertNotIn("STEP78_VALIDATION_PATHS", lifecycle)
        self.assertNotIn("VALIDATION_NEUTRAL_PATHS =", lifecycle)

    def test_historical_run_discovery_prefers_candidate_lineage_without_pull_lookup(self):
        args = SimpleNamespace(
            event="pull_request",
            workflow="approved-release-security-validation.yml",
            repository="MYLEGND/masterapp",
            current_run_id=99,
            current_sha="b" * 40,
        )
        run = {
            "id": 77,
            "head_sha": "a" * 40,
            "status": "completed",
            "event": "pull_request",
            "path": ".github/workflows/approved-release-security-validation.yml",
            "head_repository": {"full_name": "MYLEGND/masterapp"},
            "updated_at": "2026-10-03T00:00:00Z",
        }
        with patch.object(m, "api_get", return_value={"workflow_runs": [run]}), \
             patch.object(m, "_trusted_lineage_run", return_value=True), \
             patch.object(m, "_trusted_pr_run", side_effect=AssertionError("pull lookup should not run")):
            rows = m._trusted_historical_runs(args, "token")
        self.assertEqual([77], [row["id"] for row in rows])

    def test_historical_run_discovery_stops_after_nearest_trusted_success(self):
        args = SimpleNamespace(
            event="pull_request",
            workflow="approved-release-security-validation.yml",
            repository="MYLEGND/masterapp",
            current_run_id=99,
            current_sha="d" * 40,
        )
        newer_failed = {
            "id": 88,
            "head_sha": "c" * 40,
            "status": "completed",
            "conclusion": "failure",
            "event": "pull_request",
            "path": ".github/workflows/approved-release-security-validation.yml",
            "head_repository": {"full_name": "MYLEGND/masterapp"},
            "updated_at": "2026-10-04T02:00:00Z",
        }
        nearest_success = dict(newer_failed, id=77, head_sha="b" * 40,
                               conclusion="success", updated_at="2026-10-04T01:00:00Z")
        older_success = dict(newer_failed, id=66, head_sha="a" * 40,
                             conclusion="success", updated_at="2026-10-04T00:00:00Z")
        seen = []
        def trusted(_repo, run, _path, _sha):
            seen.append(run["id"])
            return True
        with patch.object(m, "api_get", return_value={"workflow_runs": [older_success, newer_failed, nearest_success]}), \
             patch.object(m, "_trusted_lineage_run", side_effect=trusted), \
             patch.object(m, "_trusted_pr_run", side_effect=AssertionError("lineage proof should be enough")):
            rows = m._trusted_historical_runs(args, "token")
        self.assertEqual([88, 77], [row["id"] for row in rows])
        self.assertEqual([88, 77], seen)

    def test_validation_resume_test_change_requires_architecture_and_security(self):
        topology = m.required_validation_topology([
            "scripts/test-validation-resume.py",
        ])
        self.assertEqual(
            {
                ".github/workflows/masterapp-platform-architecture-validation.yml",
                ".github/workflows/approved-release-security-validation.yml",
            },
            set(topology["required"]),
        )

    def test_terminal_lifecycle_wake_is_control_only_and_requires_owning_validation(self):
        path="scripts/wake-release-lifecycle.py"
        topology=m.required_validation_topology([path])
        self.assertEqual(
            {
                ".github/workflows/masterapp-platform-architecture-validation.yml",
                ".github/workflows/approved-release-security-validation.yml",
            },
            set(topology["required"]),
        )
        self.assertTrue(topology["releaseControlAuthorityChange"])
        self.assertEqual((), m.release_targets_for_paths([path]))
        self.assertTrue(m.release_control_only_path(path))

    def test_readiness_tooling_classification_preserves_validation_authority(self):
        adapter = (
            "scripts/PackageRestoreProbe/PackageRestoreProbe.csproj",
            "scripts/PackageRestoreProbe/Program.cs",
        )
        paths = adapter + (
            "scripts/release-migration-history-audit.py",
            "scripts/test-release-migration-history-audit.py",
            "scripts/test-release-migration-probe-retry.py",
            "scripts/test-release-retired-original-evidence.py",
        )
        for path in paths:
            with self.subTest(path=path):
                self.assertTrue(m.release_control_only_path(path))
                self.assertTrue(m.release_control_authority_path(path))
                self.assertEqual((), m.release_targets_for_paths([path]))
                topology = m.required_validation_topology([path])
                self.assertTrue(topology["releaseControlAuthorityChange"])
                self.assertIn(".github/workflows/masterapp-platform-architecture-validation.yml", topology["required"])
                self.assertIn(".github/workflows/approved-release-security-validation.yml", topology["required"])
        for path in adapter:
            self.assertIn(path, m.PACKAGE_AUTHORITY_PATHS)
            self.assertTrue(m.package_canary_input_path(path))
        for path in m.PACKAGE_AUTHORITY_PATHS:
            self.assertTrue((ROOT / path).is_file())
        self.assertEqual(len(m.RELEASE_EXECUTION_CONTROL_INPUTS),
                         len(set(m.RELEASE_EXECUTION_CONTROL_INPUTS)))
        for path in (
            "scripts/PackageRestoreProbe/Unexpected.cs",
            "scripts/release-migration-history-audit-new.py",
            "AgentPortal/Program.cs",
            "Infrastructure/Migrations/NewMigration.cs",
        ):
            with self.subTest(unclassified=path):
                self.assertFalse(m.release_control_only_path(path))
                self.assertTrue(m.package_canary_input_path(path))
                self.assertTrue(m.release_targets_for_paths([path]))

    def test_ai_governance_files_never_select_application_publication(self):
        paths = [
            ".github/CODEOWNERS",
            ".github/agents/legend-intelligence-engineer.agent.md",
            ".github/agents/masterapp-chief-architect.agent.md",
            ".github/agents/masterapp-cross-platform-engineer.agent.md",
            ".github/agents/masterapp-release-reviewer.agent.md",
            ".github/agents/masterapp-runtime-engineer.agent.md",
            ".github/agents/masterapp-verification-engineer.agent.md",
            ".github/copilot-instructions.md",
            "AGENTS.md",
        ]

        self.assertTrue(all(m.release_control_only_path(path) for path in paths))
        self.assertEqual((), m.release_targets_for_paths(paths))
        self.assertTrue(all(not m.package_canary_input_path(path) for path in paths))

    def test_lifecycle_control_change_requires_architecture_and_security(self):
        topology = m.required_validation_topology([
            "scripts/release-lifecycle.py",
            "scripts/test-release-lifecycle.py",
        ])
        self.assertEqual(
            {
                ".github/workflows/masterapp-platform-architecture-validation.yml",
                ".github/workflows/approved-release-security-validation.yml",
            },
            set(topology["required"]),
        )
        self.assertTrue(topology["releaseControlAuthorityChange"])

    def test_release_package_change_requires_step5_and_security(self):
        topology = m.required_validation_topology([
            "scripts/release-package.py",
        ])
        self.assertEqual(
            {
                ".github/workflows/masterapp-platform-architecture-validation.yml",
                ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
                ".github/workflows/approved-release-security-validation.yml",
            },
            set(topology["required"]),
        )

    def test_deploy_control_test_change_requires_architecture_and_security(self):
        topology = m.required_validation_topology([
            "scripts/test-deploy-approved-app.py",
        ])
        self.assertEqual(
            {
                ".github/workflows/masterapp-platform-architecture-validation.yml",
                ".github/workflows/approved-release-security-validation.yml",
            },
            set(topology["required"]),
        )

    def test_public_website_only_scope_does_not_expand_into_unrelated_validations(self):
        topology = m.required_validation_topology([
            "Infrastructure/WebsiteEditing/WebsiteSiteSource.cs",
        ])
        self.assertTrue(topology["publicWebsiteOnly"])
        self.assertEqual(
            {".github/workflows/masterapp-platform-architecture-validation.yml"},
            set(topology["required"]),
        )

    def test_control_only_descendant_reuses_package_without_builder_equivalence_recheck(self):
        producer = "a" * 40
        revision = "b" * 40
        run = {
            "id": 77,
            "head_sha": producer,
            "updated_at": "2026-10-03T00:00:00Z",
        }
        package_identity = "c" * 64
        artifact = "founder-diagnostics-packages-" + package_identity
        def api_get(_repository, path, _token):
            if path.startswith("actions/workflows/"):
                return {"workflow_runs": [run]}
            raise AssertionError(path)
        with patch.object(m, "api_get", side_effect=api_get), \
             patch.object(m, "_trusted_lineage_run", return_value=True), \
             patch.object(m, "_successful_package_child", return_value=True), \
             patch.object(m, "git_changed", return_value=["scripts/test-validation-resume.py"]), \
             patch.object(m, "_run_artifact_names", return_value={artifact}), \
             patch.object(m, "package_inputs_compatible",
                          side_effect=AssertionError("No byte input changed")):
            result = m.compatible_package_producer(
                "MYLEGND/masterapp", revision, "token"
            )
        self.assertTrue(result["reusable"])
        self.assertEqual(77, result["runId"])
        self.assertEqual(package_identity, result["packageIdentity"])

    def test_compatible_package_producer_uses_trusted_run_artifacts_not_repository_artifact_listing(self):
        producer = "a" * 40
        revision = "b" * 40
        run = {
            "id": 77,
            "head_sha": producer,
            "updated_at": "2026-10-03T00:00:00Z",
        }
        package_identity = "c" * 64
        artifact = "founder-diagnostics-packages-" + package_identity
        seen = []
        def api_get(_repository, path, _token):
            seen.append(path)
            if path.startswith("actions/workflows/"):
                return {"workflow_runs": [run]}
            raise AssertionError(path)
        with patch.object(m, "api_get", side_effect=api_get), \
             patch.object(m, "_trusted_lineage_run", return_value=True), \
             patch.object(m, "_successful_package_child", return_value=True), \
             patch.object(m, "package_inputs_compatible", return_value=True), \
             patch.object(m, "_run_artifact_names", return_value={artifact}), \
             patch.object(m.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout="")):
            result = m.compatible_package_producer("MYLEGND/masterapp", revision, "token")
        self.assertTrue(result["reusable"])
        self.assertEqual(77, result["runId"])
        self.assertEqual(package_identity, result["packageIdentity"])
        self.assertFalse(any(path.startswith("actions/artifacts?") for path in seen))

    def test_migration_probe_evidence_uses_trusted_run_artifacts_not_repository_artifact_listing(self):
        identity = {
            "schemaVersion": 1,
            "runtimeIdentity": "a" * 64,
            "toolIdentity": "b" * 64,
            "executionIdentity": "c" * 64,
            "identity": "d" * 64,
            "artifact": "legend-migration-probe-" + "d" * 64,
        }
        run = {"id": 88, "head_sha": "e" * 40, "updated_at": "2026-10-03T00:00:00Z"}
        seen = []
        def api_get(_repository, path, _token):
            seen.append(path)
            if path.startswith("actions/workflows/"):
                return {"workflow_runs": [run]}
            raise AssertionError(path)
        with patch.object(m, "api_get", side_effect=api_get), \
             patch.object(m, "_trusted_lineage_run", return_value=True), \
             patch.object(m, "migration_probe_identity", return_value=identity), \
             patch.object(m, "migration_probe_artifact", return_value={'artifactId': 123, 'producingAttempt': 1, 'producerJobId': 7}), \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            result = m.migration_probe_evidence("MYLEGND/masterapp", identity)
        self.assertTrue(result["reusable"])
        self.assertEqual(88, result["runId"])
        self.assertFalse(any(path.startswith("actions/artifacts?") for path in seen))
        self.assertFalse(any("/jobs?" in path for path in seen))

    def test_current_probe_identity_still_requires_child_authority(self):
        with patch.object(m.subprocess, "check_output", return_value=""), \
             patch.object(m, "git_show_file", return_value="jobs:\n  other:\n    runs-on: ubuntu-latest\n"):
            with self.assertRaisesRegex(m.MigrationProbeAuthorityMissing, "child authority missing"):
                m.migration_probe_identity("a" * 40, "a" * 40)

    def test_probe_history_without_child_does_not_abort_new_candidate(self):
        identity = {"identity": "d" * 64, "artifact": "legend-migration-probe-" + "d" * 64}
        old = {"id": 88, "head_sha": "e" * 40, "updated_at": "2026-10-03T01:00:00Z"}
        valid = {"id": 77, "head_sha": "f" * 40, "updated_at": "2026-10-03T00:00:00Z"}
        with patch.object(m, "api_get", return_value={"workflow_runs": [old, valid]}), \
             patch.object(m, "_trusted_lineage_run", return_value=True), \
             patch.object(m, "migration_probe_identity", side_effect=[m.MigrationProbeAuthorityMissing("missing"), identity]), \
             patch.object(m, "migration_probe_artifact", return_value={'artifactId': 123}) as artifacts, \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            result = m.migration_probe_evidence("MYLEGND/masterapp", identity)
        self.assertTrue(result["reusable"])
        self.assertEqual(77, result["runId"])
        artifacts.assert_called_once_with("MYLEGND/masterapp", valid, identity['artifact'], "token")

    def test_probe_history_without_child_requires_fresh_build(self):
        identity = {"identity": "d" * 64, "artifact": "legend-migration-probe-" + "d" * 64}
        run = {"id": 88, "head_sha": "e" * 40}
        with patch.object(m, "api_get", return_value={"workflow_runs": [run]}), \
             patch.object(m, "_trusted_lineage_run", return_value=True), \
             patch.object(m, "migration_probe_identity", side_effect=m.MigrationProbeAuthorityMissing("missing")), \
             patch.object(m, "_run_artifact_names") as artifacts, \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            self.assertEqual({"reusable": False, "artifact": identity["artifact"]},
                             m.migration_probe_evidence("MYLEGND/masterapp", identity))
        artifacts.assert_not_called()

    def test_probe_history_other_identity_errors_remain_fatal(self):
        with patch.object(m, "api_get", return_value={"workflow_runs": [{"id": 88, "head_sha": "e" * 40}]}), \
             patch.object(m, "_trusted_lineage_run", return_value=True), \
             patch.object(m, "migration_probe_identity", side_effect=ValueError("runtime mismatch")), \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            with self.assertRaisesRegex(ValueError, "runtime mismatch"):
                m.migration_probe_evidence("MYLEGND/masterapp", {})

    def test_trusted_lineage_run_requires_same_repo_workflow_and_ancestor(self):
        run = {
            "path": ".github/workflows/" + m.PACKAGE_VALIDATION_WORKFLOW,
            "event": "pull_request",
            "status": "completed",
            "head_sha": "a" * 40,
            "head_repository": {"full_name": "MYLEGND/masterapp"},
        }
        with patch.object(m.subprocess, "run", return_value=SimpleNamespace(returncode=0)):
            self.assertTrue(m._trusted_lineage_run(
                "MYLEGND/masterapp", run,
                ".github/workflows/" + m.PACKAGE_VALIDATION_WORKFLOW,
                "b" * 40,
            ))
        with patch.object(m.subprocess, "run", return_value=SimpleNamespace(returncode=1)):
            self.assertFalse(m._trusted_lineage_run(
                "MYLEGND/masterapp", run,
                ".github/workflows/" + m.PACKAGE_VALIDATION_WORKFLOW,
                "b" * 40,
            ))
        foreign = dict(run, head_repository={"full_name": "outsider/fork"})
        with patch.object(m.subprocess, "run", return_value=SimpleNamespace(returncode=0)):
            self.assertFalse(m._trusted_lineage_run(
                "MYLEGND/masterapp", foreign,
                ".github/workflows/" + m.PACKAGE_VALIDATION_WORKFLOW,
                "b" * 40,
            ))

    def test_package_canary_preserves_compatible_producer_for_changed_package_input(self):
        producer = {'runId': 91, 'revision': 'a' * 40, 'packageIdentity': 'c' * 64,
                    'artifact': 'original-package', 'reason': 'dependency_equivalent_immutable_package_producer'}
        with patch.object(m, 'compatible_package_producer', return_value=producer), \
             patch.object(m, 'git_changed', return_value=['AgentPortal/Program.cs']), \
             patch.dict(m.os.environ, {'GITHUB_TOKEN': 'token'}):
            plan = m.compute_package_canary_plan('MYLEGND/masterapp', 'b' * 40, '0' * 40, 100, 'fix')
        self.assertFalse(plan['needed'])
        self.assertEqual(['AgentPortal/Program.cs'], plan['changedInputs'])
        self.assertEqual('a' * 40, plan['evidenceHeadSha'])
        self.assertEqual('c' * 64, plan['packageIdentity'])
        self.assertEqual('original-package', plan['exactPackageArtifact'])

    def test_package_canary_skips_control_only_head_when_artifact_lookup_misses(self):
        with patch.object(m, 'compatible_package_producer',
                          side_effect=AssertionError('zero-input head must not need artifact lookup')), \
             patch.object(m, 'git_changed', return_value=['scripts/test-release-policy.py']), \
             patch.dict(m.os.environ, {'GITHUB_TOKEN': 'token'}):
            plan = m.compute_package_canary_plan('MYLEGND/masterapp', 'b' * 40, '0' * 40, 100, 'fix')
        self.assertFalse(plan['needed'])
        self.assertEqual('no_package_producing_inputs_changed', plan['reason'])
        self.assertEqual([], plan['changedInputs'])

    def test_package_canary_reports_application_inputs_when_no_compatible_package(self):
        with patch.object(m, 'compatible_package_producer', return_value=None), \
             patch.object(m, 'git_changed', return_value=['AgentPortal/Program.cs']), \
             patch.dict(m.os.environ, {'GITHUB_TOKEN': 'token'}):
            plan = m.compute_package_canary_plan('MYLEGND/masterapp', 'b' * 40, '0' * 40, 100, 'fix')
        self.assertTrue(plan['needed'])
        self.assertEqual(['AgentPortal/Program.cs'], plan['changedInputs'])

    def test_migration_history_control_changes_do_not_build_or_publish_applications(self):
        paths = ['scripts/release-migration-history-audit.py',
                 'scripts/test-release-migration-history-audit.py',
                 'scripts/test-release-retired-original-evidence.py',
                 'scripts/test-release-migration-probe-retry.py']
        with patch.object(m, 'git_changed', return_value=paths), \
             patch.object(m, 'compatible_package_producer') as lookup:
            plan = m.compute_package_canary_plan('MYLEGND/masterapp', 'b' * 40, 'a' * 40, 100, 'fix')
        self.assertFalse(plan['needed'])
        self.assertEqual((), m.release_targets_for_paths(paths))
        self.assertEqual('no_package_producing_inputs_changed', plan['reason'])
        lookup.assert_not_called()
        self.assertTrue(all(m.release_control_authority_path(path) for path in paths))
        self.assertIn('.github/workflows/approved-release-security-validation.yml',
                      m.required_validation_topology(paths)['required'])

    def test_live_state_probes_selected_targets_concurrently_and_keeps_inventory_order(self):
        selected = ['masterapp-portal', 'masterapp-client']
        calls = []

        def read(host, target, revision):
            calls.append(host)
            return revision if target['releaseName'] == 'masterapp-portal' else 'older'

        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'live.json'
            github_output = Path(directory) / 'github-output'
            args = SimpleNamespace(
                selected_targets=__import__('json').dumps(selected),
                revision='a' * 40,
                output=str(output),
                github_output=str(github_output),
            )
            with patch.object(m, '_read_provenance', side_effect=read):
                m.cmd_live_state(args)

            payload = __import__('json').loads(output.read_text())
            self.assertEqual(['portal', 'client', 'protect', 'parfait', 'website'],
                             list(payload['targets']))
            self.assertTrue(payload['targets']['portal']['alreadyLive'])
            self.assertFalse(payload['targets']['client']['alreadyLive'])
            self.assertEqual(
                {m.RELEASE_TARGETS['portal']['host'], m.RELEASE_TARGETS['client']['host']},
                set(calls),
            )
            lines = github_output.read_text().splitlines()
            self.assertEqual('portal_live=true', lines[0])
            self.assertEqual('client_live=false', lines[1])

    def test_package_backfill_requires_green_exact_revision_and_control_only_descendants(self):
        revision = "a" * 40
        current = "b" * 40
        run = {
            "id": 123,
            "path": ".github/workflows/" + m.PACKAGE_VALIDATION_WORKFLOW,
            "event": "pull_request",
            "status": "completed",
            "conclusion": "success",
            "head_sha": revision,
            "head_repository": {"full_name": "MYLEGND/masterapp"},
            "updated_at": "2026-10-02T00:00:00Z",
        }
        pr = {
            "number": 364,
            "merged_at": "2026-10-02T01:10:41Z",
            "base": {"ref": m.TRUSTED_PR_BASE},
            "head": {
                "sha": revision,
                "repo": {"full_name": "MYLEGND/masterapp"},
            },
        }
        def api_get(_repository, path, _token):
            if path.startswith("actions/runs?"):
                return {"workflow_runs": [run]}
            if path == f"commits/{revision}/pulls":
                return [pr]
            raise AssertionError(path)

        with patch.object(m.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout="", stderr="")), \
             patch.object(m, "git_changed", return_value=["scripts/release-lifecycle.py"]), \
             patch.object(m, "api_get", side_effect=api_get), \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            plan = m.compute_package_backfill_plan("MYLEGND/masterapp", revision, current)

        self.assertTrue(plan["allowed"])
        self.assertEqual(123, plan["validationRunId"])
        self.assertEqual(364, plan["sourcePr"])
        self.assertEqual([], plan["changedApplicationInputs"])

    def test_package_backfill_fails_closed_on_application_drift(self):
        revision = "a" * 40
        current = "b" * 40
        with patch.object(m.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout="", stderr="")), \
             patch.object(m, "git_changed", return_value=["AgentPortal/Program.cs"]), \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            plan = m.compute_package_backfill_plan("MYLEGND/masterapp", revision, current)

        self.assertFalse(plan["allowed"])
        self.assertEqual(["AgentPortal/Program.cs"], plan["changedApplicationInputs"])
        self.assertEqual("application_inputs_changed_since_validated_revision", plan["reason"])

    def test_rollback_evidence_uses_release_receipt_link_to_validated_package(self):
        revision = "a" * 40
        identity = "b" * 64
        release_run_id = 88
        validation_run_id = 77
        release_name = m.RELEASE_TARGETS["portal"]["releaseName"]
        receipt = f"legend-approved-release-{revision}-{release_name}"
        package_artifact = f"founder-diagnostics-packages-{identity}"
        package_link = f"legend-approved-package-link-{revision}-{identity}"

        release_run = {
            "id": release_run_id,
            "path": ".github/workflows/all-intentional-direct-release-20260918.yml",
            "head_branch": m.TRUSTED_PR_BASE,
            "status": "completed",
            "conclusion": "success",
            "head_repository": {"full_name": "MYLEGND/masterapp"},
        }
        validation_run = {
            "id": validation_run_id,
            "path": ".github/workflows/" + m.PACKAGE_VALIDATION_WORKFLOW,
            "event": "pull_request",
            "status": "completed",
            "conclusion": "success",
            "head_sha": revision,
            "head_repository": {"full_name": "MYLEGND/masterapp"},
        }

        def artifacts(_repository, name, _token):
            if name == receipt:
                return [{"workflow_run": {"id": release_run_id}}]
            if name == package_artifact:
                return [{"workflow_run": {"id": validation_run_id}}]
            return []

        def api_get(_repository, path, _token):
            if path == f"actions/runs/{release_run_id}":
                return release_run
            if path == f"actions/runs/{validation_run_id}":
                return validation_run
            raise AssertionError(path)

        with patch.object(m, "_release_history_json", return_value={'schemaVersion':2}), \
             patch.object(m, "_successful_package_child", return_value=True), \
             patch.object(m, "_trusted_pr_run", return_value=True), \
             patch.object(m, "_artifact_rows", side_effect=artifacts), \
             patch.object(m, "api_get", side_effect=api_get), \
             patch.object(m, "_run_artifact_names", return_value={package_link}), \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            evidence = m.compute_rollback_evidence(
                "MYLEGND/masterapp",
                revision,
                "portal",
            )

        self.assertTrue(evidence["reusable"])
        self.assertEqual(validation_run_id, evidence["runId"])
        self.assertEqual(release_run_id, evidence["releaseRunId"])
        self.assertEqual(package_artifact, evidence["packageArtifact"])
        self.assertEqual(identity, evidence["packageIdentity"])
        self.assertEqual(
            "exact_target_release_receipt_with_validated_package_link",
            evidence["reason"],
        )

    def test_validated_package_accepts_receipt_backed_approved_backfill(self):
        revision = "a" * 40
        identity = "b" * 64
        artifact = f"founder-diagnostics-packages-{identity}"
        receipt = m.package_backfill_receipt_name(revision, identity)
        run = {
            "id": 77,
            "path": ".github/workflows/" + m.PACKAGE_VALIDATION_WORKFLOW,
            "event": "workflow_dispatch",
            "status": "completed",
            "conclusion": "success",
            "head_branch": m.TRUSTED_PR_BASE,
            "head_repository": {"full_name": "MYLEGND/masterapp"},
        }
        with patch.object(m, "_successful_package_child", return_value=True), \
             patch.object(m, "_artifact_rows", return_value=[{"workflow_run": {"id": 77}}]), \
             patch.object(m, "api_get", return_value=run), \
             patch.object(m, "_run_artifact_names", return_value={artifact, receipt}), \
             patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}):
            evidence = m.compute_validated_package_evidence(
                "MYLEGND/masterapp", revision, identity
            )

        self.assertTrue(evidence["reusable"])
        self.assertEqual(77, evidence["runId"])
        self.assertEqual(
            "validated_package_backfill_from_exact_green_revision",
            evidence["reason"],
        )

    def test_inline_execution_environment_and_defaults_invalidate_proof(self):
        workflow = "step5-isolated-conversion-mapping-validation.yml"
        text = (ROOT / ".github/workflows" / workflow).read_text()
        for field, first, second in (
            ("env", "{TEST_OVERRIDE: a}", "{TEST_OVERRIDE: b}"),
            ("defaults", "{run: {shell: bash}}", "{run: {shell: sh}}"),
        ):
            before = f"{field}: {first}\n" + text
            after = f"{field}: {second}\n" + text
            self.assertNotEqual(m._step5_execution_contract(before), m._step5_execution_contract(after))
            self.assertNotEqual(
                m._gate_execution_contract(before, m.WORKFLOWS[workflow], "candidate-restore"),
                m._gate_execution_contract(after, m.WORKFLOWS[workflow], "candidate-restore"))

    def test_founder_cloudflare_release_trigger_is_canonical_and_narrow(self):
        self.assertTrue(m.founder_cloudflare_release_required([
            "Legend-Cloudflare/src/runtime/registry.mjs",
        ]))
        self.assertTrue(m.founder_cloudflare_release_required([
            "Legend-Cloudflare/wrangler.founder-baseline.jsonc",
        ]))
        self.assertFalse(m.founder_cloudflare_release_required([
            "scripts/deploy-founder-cloudflare.py",
        ]))
        self.assertFalse(m.founder_cloudflare_release_required([
            "Legend-Cloudflare/tests/runtime/qualification-mode.test.mjs",
            "Legend-Cloudflare/scripts/founder-canary.mjs",
            "AgentPortal/Program.cs",
        ]))

    def test_founder_cloudflare_release_scope_is_portal_only(self):
        self.assertEqual(
            (),
            m.release_targets_for_paths(["scripts/deploy-founder-cloudflare.py"]),
        )
        self.assertEqual(
            ("masterapp-portal",),
            m.release_targets_for_paths(["Legend-Cloudflare/src/runtime/registry.mjs"]),
        )
        self.assertEqual(
            ("masterapp-client",),
            m.release_targets_for_paths([
                "scripts/deploy-founder-cloudflare.py",
                "ClientApp/Program.cs",
            ]),
        )

    def test_auxiliary_release_fanout_is_control_only_and_never_expands_app_scope(self):
        self.assertTrue(m.release_control_only_path("scripts/release-auxiliary.py"))
        self.assertEqual((), m.release_targets_for_paths(["scripts/release-auxiliary.py"]))

    def test_cloudflare_routing_authority_is_release_control_not_package_input(self):
        path = "scripts/cloudflare-routing-authority.py"
        self.assertTrue(m.release_control_authority_path(path))
        self.assertTrue(m.release_control_only_path(path))
        self.assertFalse(m.package_canary_input_path(path))
        self.assertEqual((), m.release_targets_for_paths([path]))

    def test_pr488_shaped_control_plane_changes_have_zero_application_targets(self):
        paths = [
            ".github/workflows/all-intentional-direct-release-20260918.yml",
            ".github/workflows/approved-release-security-validation.yml",
            ".github/workflows/masterapp-platform-architecture-validation.yml",
            ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
            ".github/workflows/steps7-8-governed-advertising-validation.yml",
            "AgentPortal.Tests/ClientAppDeploymentWorkflowTests.cs",
            "AgentPortal.Tests/LegendFounderAiContractTests.cs",
            "AgentPortal.Tests/ScopedParfaitCommerceAuthorityTests.cs",
            "scripts/deploy-founder-cloudflare.py",
            "scripts/cloudflare-routing-authority.py",
            "scripts/release-auxiliary.py",
            "scripts/release-lifecycle.py",
            "scripts/release-package.py",
            "scripts/release-prepublication.py",
            "scripts/release-workflow.py",
            "scripts/test-deploy-approved-app.py",
            "scripts/test-release-lifecycle.py",
            "scripts/test-release-policy.py",
            "scripts/test-validation-resume.py",
            "scripts/validation-resume.py",
        ]
        self.assertTrue(all(m.release_control_only_path(path) for path in paths))
        self.assertEqual((), m.release_targets_for_paths(paths))

    def test_release_baseline_delegates_application_identity_classification(self):
        baseline = (ROOT / "scripts" / "approved-release-baseline.py").read_text()
        self.assertIn("_validation_authority.release_control_only_path(path)", baseline)
        self.assertNotIn('path.startswith(".github/workflows/")', baseline)

    def test_step5_workflow_only_change_is_neutral_to_architecture(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            [".github/workflows/step5-isolated-conversion-mapping-validation.yml"],
            "prior_run",
        )
        self.assertEqual("incremental", plan["mode"])
        self.assertTrue(all(not gate["run"] for gate in plan["gates"].values()))

    def test_direct_release_workflow_change_reruns_only_lifecycle_gate(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            [".github/workflows/all-intentional-direct-release-20260918.yml"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["lifecycle"]["run"])
        self.assertEqual("gate_inputs_changed", plan["gates"]["lifecycle"]["reason"])
        for key, gate in plan["gates"].items():
            if key != "lifecycle":
                self.assertFalse(gate["run"], key)

    def test_resume_test_change_reruns_only_lifecycle_contract_gate(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["scripts/test-validation-resume.py"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["lifecycle"]["run"])
        self.assertFalse(plan["gates"]["build-hosts"]["run"])
        self.assertFalse(plan["gates"]["meta-regressions"]["run"])

    def test_release_package_change_reruns_only_release_authority_gates(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["scripts/release-package.py"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["lifecycle"]["run"])
        self.assertTrue(plan["gates"]["release-policy"]["run"])
        for key, gate in plan["gates"].items():
            if key not in {"lifecycle", "release-policy"}:
                self.assertFalse(gate["run"], key)

    def test_release_web_runtime_change_reuses_content_identical_compiled_graph(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["tests/legend-connect/example.test.mjs"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["release-web-contracts"]["run"])
        self.assertFalse(plan["gates"]["compile-regression"]["run"])
        self.assertFalse(plan["gates"]["restore-dotnet"]["run"])
        for key, gate in plan["gates"].items():
            if key != "release-web-contracts":
                self.assertFalse(gate["run"], key)

    def test_validation_authority_change_reruns_only_declared_consumers(self):
        architecture = "masterapp-platform-architecture-validation.yml"
        architecture_plan = m.compute_plan(
            architecture,
            "b" * 40,
            self.prior(),
            self.successful_steps(architecture),
            ["scripts/validation-resume.py"],
            "prior_run",
        )
        self.assertEqual("incremental", architecture_plan["mode"])
        self.assertTrue(architecture_plan["gates"]["lifecycle"]["run"])
        for key, gate in architecture_plan["gates"].items():
            if key != "lifecycle":
                self.assertFalse(gate["run"], key)

        step5 = "step5-isolated-conversion-mapping-validation.yml"
        step5_plan = m.compute_plan(
            step5,
            "b" * 40,
            self.prior(),
            self.successful_steps(step5),
            ["scripts/validation-resume.py"],
            "prior_run",
        )
        self.assertEqual("incremental", step5_plan["mode"])
        self.assertTrue(step5_plan["gates"]["comparison"]["run"])
        self.assertFalse(step5_plan["gates"]["candidate-restore"]["run"])
        self.assertFalse(step5_plan["gates"]["candidate-build"]["run"])
        self.assertFalse(step5_plan["gates"]["candidate-focused"]["run"])
        self.assertFalse(step5_plan["gates"]["candidate-full"]["run"])

    def test_unknown_change_fails_closed_to_full(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["Some-New-Unclassified-System/file.bin"],
            "prior_run",
        )
        self.assertEqual("full", plan["mode"])
        self.assertTrue(all(gate["run"] for gate in plan["gates"].values()))

    def test_step6_unrelated_commit_preserves_all_successful_step6_evidence(self):
        workflow = "step6-openai-ads-execution-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["scripts/test-release-policy.py"],
            "prior_run",
        )
        self.assertEqual("incremental", plan["mode"])
        self.assertTrue(all(not gate["run"] for gate in plan["gates"].values()))

    def test_step6_test_fix_preserves_unrelated_workflows_but_rebuilds_test_graph(self):
        workflow = "step6-openai-ads-execution-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal.Tests/OpenAiAdsExecutionServiceTests.cs"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["tests"]["run"])
        self.assertTrue(plan["gates"]["build"]["run"])
        self.assertTrue(plan["gates"]["restore"]["run"])

    def test_security_validator_preserves_unaffected_successful_gates(self):
        workflow = "approved-release-security-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal/Services/Engineering/LegendEngineeringOrchestrator.cs"],
            "prior_run",
        )
        self.assertEqual("incremental", plan["mode"])
        self.assertFalse(plan["gates"]["db-validation"]["run"])
        self.assertFalse(plan["gates"]["no-skips"]["run"])
        self.assertFalse(plan["gates"]["vulnerabilities"]["run"])
        self.assertFalse(plan["gates"]["secret-scan"]["run"])
        self.assertFalse(plan["gates"]["keyring"]["run"])
        self.assertFalse(plan["gates"]["composition"]["run"])
        self.assertTrue(plan["gates"]["diff-check"]["run"])

    def test_security_project_graph_change_reruns_restore_and_vulnerability_audit(self):
        workflow = "approved-release-security-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal/AgentPortal.csproj"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["restore"]["run"])
        self.assertTrue(plan["gates"]["vulnerabilities"]["run"])
        self.assertFalse(plan["gates"]["composition"]["run"])

    def test_security_program_change_reruns_only_composition_keyring_and_diff(self):
        workflow = "approved-release-security-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["AgentPortal/Program.cs"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["composition"]["run"])
        self.assertTrue(plan["gates"]["keyring"]["run"])
        self.assertTrue(plan["gates"]["diff-check"]["run"])
        self.assertFalse(plan["gates"]["db-validation"]["run"])
        self.assertFalse(plan["gates"]["no-skips"]["run"])

    def test_step78_ui_only_fix_does_not_repeat_dotnet_validation(self):
        workflow = "steps7-8-governed-advertising-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["Legend-Design/legend-website-management.js"],
            "prior_run",
        )
        self.assertTrue(plan["gates"]["website-ui-tests"]["run"])
        self.assertFalse(plan["gates"]["governance-tests"]["run"])
        self.assertFalse(plan["gates"]["build"]["run"])
        self.assertFalse(plan["gates"]["restore"]["run"])


    @patch.object(m, "api_get")
    def test_trusted_historical_run_recovers_pr_identity_when_github_omits_linkage(self, api_get):
        head = "c" * 40
        args = SimpleNamespace(
            event="pull_request",
            workflow="masterapp-platform-architecture-validation.yml",
            repository="MYLEGND/masterapp",
            current_run_id=99,
            current_sha="d" * 40,
        )

        def response(repository, path, token):
            self.assertEqual(args.repository, repository)
            self.assertEqual("token", token)
            if path.startswith("actions/workflows/"):
                return {
                    "workflow_runs": [{
                        "id": 88,
                        "event": "pull_request",
                        "conclusion": "failure",
                        "status": "completed",
                        "path": m.WORKFLOW_PATHS[args.workflow],
                        "head_repository": {"full_name": args.repository},
                        "pull_requests": [],
                        "head_sha": head,
                        "updated_at": "2026-10-02T00:00:00Z",
                    }]
                }
            if path == f"commits/{head}/pulls?per_page=100":
                return [{
                    "base": {"ref": m.TRUSTED_PR_BASE},
                    "head": {
                        "sha": "d" * 40,
                        "repo": {"full_name": args.repository},
                    },
                }]
            raise AssertionError(path)

        api_get.side_effect = response
        runs = m._trusted_historical_runs(args, "token")

        self.assertEqual([88], [run["id"] for run in runs])
        self.assertEqual(head, runs[0]["head_sha"])

    def test_step5_baseline_reuses_prior_artifact_for_control_only_base_change(self):
        prior_base = "a" * 40
        current_base = "b" * 40
        run_head = "c" * 40
        artifact = "step5-baseline-" + prior_base
        run = {
            "id": 77,
            "path": ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
            "event": "pull_request",
            "status": "completed",
            "head_sha": run_head,
            "head_repository": {"full_name": "MYLEGND/masterapp"},
            "updated_at": "2026-10-02T00:00:00Z",
        }

        def api_get(repository, path, token):
            if path.startswith("actions/artifacts?name="):
                return {"artifacts": []}
            if path.startswith("actions/workflows/"):
                return {"workflow_runs": [run]}
            raise AssertionError(path)

        with patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}, clear=False), \
             patch.object(m, "api_get", side_effect=api_get), \
             patch.object(m, "_run_artifact_names", return_value={artifact}), \
             patch.object(m, "_step5_baseline_producer_compatible", return_value=True), \
             patch.object(m, "_trusted_pr_run", return_value=True), \
             patch.object(m, "_step5_artifact_results", return_value={"Tests.Example.Case": "Passed"}), \
             patch.object(m, "step5_dependency_change", return_value=[]):
            result = m.compute_step5_baseline_evidence("MYLEGND/masterapp", current_base)

        self.assertTrue(result["reusable"])
        self.assertEqual(77, result["evidenceRunId"])
        self.assertEqual(artifact, result["evidenceArtifact"])
        self.assertEqual(prior_base, result["evidenceBaseSha"])
        self.assertEqual("content_identical_step5_inputs", result["reason"])

    def test_step5_baseline_rejects_prior_artifact_when_test_inputs_changed(self):
        prior_base = "a" * 40
        current_base = "b" * 40
        run_head = "c" * 40
        artifact = "step5-baseline-" + prior_base
        run = {
            "id": 78,
            "path": ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
            "event": "pull_request",
            "status": "completed",
            "head_sha": run_head,
            "head_repository": {"full_name": "MYLEGND/masterapp"},
            "updated_at": "2026-10-02T00:00:00Z",
        }

        def api_get(repository, path, token):
            if path.startswith("actions/artifacts?name="):
                return {"artifacts": []}
            if path.startswith("actions/workflows/"):
                return {"workflow_runs": [run]}
            raise AssertionError(path)

        with patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}, clear=False), \
             patch.object(m, "api_get", side_effect=api_get), \
             patch.object(m, "_run_artifact_names", return_value={artifact}), \
             patch.object(m, "_step5_jobs_unchanged", return_value=True), \
             patch.object(m, "_trusted_pr_run", return_value=True), \
             patch.object(m, "_step5_artifact_complete", return_value=True), \
             patch.object(m, "step5_dependency_change", return_value=None):
            result = m.compute_step5_baseline_evidence("MYLEGND/masterapp", current_base)

        self.assertFalse(result["reusable"])
        self.assertEqual("no_content_identical_baseline_artifact", result["reason"])


    def test_step5_frontend_node_tests_are_neutral_to_dotnet_candidate(self):
        workflow = "step5-isolated-conversion-mapping-validation.yml"
        for path in (
            "tests/layout/modal-content-region.test.mjs",
            "tests/legend-connect/limits-presentation.test.mjs",
        ):
            plan = m.compute_plan(
                workflow,
                "b" * 40,
                self.prior(),
                self.successful_steps(workflow),
                [path],
                "prior_run",
            )
            self.assertEqual("incremental", plan["mode"])
            self.assertTrue(all(not gate["run"] for gate in plan["gates"].values()))

    def test_step5_cloudflare_test_only_change_is_neutral_to_dotnet_candidate(self):
        workflow = "step5-isolated-conversion-mapping-validation.yml"
        plan = m.compute_plan(
            workflow,
            "b" * 40,
            self.prior(),
            self.successful_steps(workflow),
            ["Legend-Cloudflare/tests/security/founder-control.test.mjs"],
            "prior_run",
        )
        self.assertEqual("incremental", plan["mode"])
        self.assertTrue(all(not gate["run"] for gate in plan["gates"].values()))

    def test_step5_repair_uses_independent_candidate_and_baseline_evidence(self):
        prior_head = "a" * 40
        current_head = "b" * 40
        failing_class = "AgentPortal.Tests.LegendFounderAiModeIsolationTests"
        failing_test = (
            failing_class
            + ".HeldOutFoundation_ResearchFailureSurvivesSubsequentProviderFailure"
        )
        candidate = {
            "runId": 71,
            "headSha": prior_head,
            "artifact": "step5-candidate-" + prior_head,
        }
        baseline = {
            "reusable": True,
            "evidenceRunId": 44,
            "evidenceArtifact": "step5-baseline-" + ("c" * 40),
            "evidenceBaseSha": "c" * 40,
        }
        changed = [
            ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
            "AgentPortal.Tests/LegendFounderPretrainedAcceptanceTests.cs",
            "scripts/test-validation-resume.py",
            "scripts/validation-resume.py",
            "tests/layout/modal-content-region.test.mjs",
        ]

        def download(_repository, _run_id, _name, directory):
            directory.mkdir(parents=True, exist_ok=True)
            filename = "candidate.trx" if directory.name == "candidate" else "baseline.trx"
            (directory / filename).write_text("<TestRun />")

        def failures(path):
            return {failing_test} if path.name == "candidate.trx" else set()

        with patch.dict(m.os.environ, {"GITHUB_TOKEN": "token"}, clear=False), \
             patch.object(m, "_step5_prior_candidate_evidence", return_value=candidate), \
             patch.object(m, "compute_step5_baseline_evidence", return_value=baseline), \
             patch.object(m, "_step5_candidate_producer_compatible", return_value=True), \
             patch.object(m, "git_changed", return_value=changed), \
             patch.object(m, "_download_run_artifact", side_effect=download), \
             patch.object(m, "step5_dependency_change", return_value=[failing_class]):
            decision = m.compute_step5_decision(
                "MYLEGND/masterapp",
                current_head,
                "d" * 40,
                99,
                "hardening/example",
            )

        self.assertEqual("repair", decision["mode"])
        self.assertEqual(71, decision["priorRunId"])
        self.assertEqual(44, decision["baselineEvidenceRunId"])
        self.assertEqual(baseline["evidenceArtifact"], decision["baselineEvidenceArtifact"])
        self.assertEqual([failing_class], decision["repairClasses"])
        self.assertEqual(
            "replace_only_dependency_invalidated_classes",
            decision["reason"],
        )

    def test_step5_workflow_repair_reads_independent_baseline_evidence(self):
        workflow = (
            ROOT / ".github" / "workflows"
            / "step5-isolated-conversion-mapping-validation.yml"
        ).read_text()
        self.assertIn("baseline_evidence_run_id", workflow)
        self.assertIn("baseline_evidence_artifact", workflow)
        self.assertIn("Load independently proven approved baseline results", workflow)
        self.assertIn("needs.plan.outputs.baseline_evidence_run_id", workflow)
        self.assertIn("needs.plan.outputs.baseline_evidence_artifact", workflow)

    def test_content_equivalent_evidence_reuses_only_proven_gates_and_keeps_runtime_requirements(self):
        workflow = "masterapp-platform-architecture-validation.yml"
        current = m.compute_plan(
            workflow,
            "b" * 40,
            None,
            {},
            [],
            "no_prior_completed_run",
        )
        candidate = m.compute_plan(
            workflow,
            "b" * 40,
            None,
            {},
            [],
            "no_prior_completed_run",
        )
        candidate["gates"]["renderer-tests"] = {
            "step": m.WORKFLOWS[workflow]["gates"]["renderer-tests"]["step"],
            "run": False,
            "reason": "preserved_prior_success",
        }
        candidate["gates"]["restore-dotnet"] = {
            "step": m.WORKFLOWS[workflow]["gates"]["restore-dotnet"]["step"],
            "run": False,
            "reason": "preserved_prior_success",
        }
        run = {"id": 88, "head_sha": "c" * 40}
        self.assertTrue(m.merge_content_equivalent_evidence(current, candidate, run))
        m._enforce_runtime_requirements(current)
        self.assertFalse(current["gates"]["renderer-tests"]["run"])
        self.assertEqual(88, current["gates"]["renderer-tests"]["evidenceRunId"])
        self.assertEqual("c" * 40, current["gates"]["renderer-tests"]["evidenceHeadSha"])
        self.assertEqual("trusted_pr_history", current["gates"]["renderer-tests"]["evidenceSource"])
        self.assertTrue(current["gates"]["restore-dotnet"]["run"])
        self.assertTrue(current["gates"]["booking-regressions"]["run"])

    def test_content_equivalent_evidence_never_reuses_unproven_candidate_gate(self):
        workflow = "step6-openai-ads-execution-validation.yml"
        current = m.compute_plan(
            workflow,
            "b" * 40,
            None,
            {},
            [],
            "no_prior_completed_run",
        )
        candidate = m.compute_plan(
            workflow,
            "b" * 40,
            None,
            {},
            [],
            "no_prior_completed_run",
        )
        run = {"id": 89, "head_sha": "d" * 40}
        self.assertFalse(m.merge_content_equivalent_evidence(current, candidate, run))
        self.assertTrue(all(gate["run"] for gate in current["gates"].values()))

    def test_step5_private_static_helpers_remain_class_local_for_bounded_repair(self):
        source = """
namespace AgentPortal.Tests;
public sealed class ScopedTests
{
    [Fact]
    public void Case()
    {
        Assert.Equal("ok", ReadValue());
    }

    // Public/static are prose here, not declarations; comments must not poison isolation.
    private static string ReadValue() => "ok";
}
"""
        self.assertTrue(m._step5_isolated_test_source(source))

    def test_step5_static_state_still_requires_full_suite_proof(self):
        source = """
namespace AgentPortal.Tests;
public sealed class ScopedTests
{
    private static int Counter;

    [Fact]
    public void Case()
    {
        Counter++;
    }
}
"""
        self.assertFalse(m._step5_isolated_test_source(source))


class Step5DependencyBehaviorTests(unittest.TestCase):
    def setUp(self):
        import tempfile
        import os
        self.temp = tempfile.TemporaryDirectory()
        self.old = os.getcwd()
        os.chdir(self.temp.name)
        self.git("init", "-q")
        # Background git maintenance can race TemporaryDirectory cleanup on
        # hosted runners, creating .git/objects after the fixture exits.
        # Disable it ONLY in this temporary test repository.
        self.git("config", "gc.auto", "0")
        self.git("config", "maintenance.auto", "false")
        self.git("config", "user.email", "fixture@example.invalid")
        self.git("config", "user.name", "Fixture")
        self.write("AgentPortal.Tests/AgentPortal.Tests.csproj", '<Project><ItemGroup><None Include="../.github/workflows/all-intentional-direct-release-20260918.yml" Link="release.yml" CopyToOutputDirectory="PreserveNewest" /></ItemGroup></Project>')
        self.write("AgentPortal.Tests/One.cs", 'namespace AgentPortal.Tests; public class One { [Fact] public void Case() { Shared.Value(); } }')
        self.write("AgentPortal.Tests/Two.cs", 'namespace AgentPortal.Tests; public class Two { [Fact] public void Case() {} }')
        self.write("AgentPortal.Tests/Shared.cs", 'namespace AgentPortal.Tests; public class Shared { public void Value() {} }')
        self.write("AgentPortal.Tests/Release.cs", 'namespace AgentPortal.Tests; public class Release { [Fact] public void Case() { File.ReadAllText("release.yml"); } }')
        self.write("AgentPortal.Tests/Direct.cs", 'namespace AgentPortal.Tests; public class Direct { [Fact] public void Case() { File.ReadAllText("direct-release-request.json"); } }')
        self.write(".github/workflows/all-intentional-direct-release-20260918.yml", "initial")
        self.write("Docs/releases/direct-release-request.json", "initial")
        self.write("scripts/release-lifecycle.py", "initial")
        self.write("scripts/validation-resume.py", (ROOT / "scripts/validation-resume.py").read_text())
        self.write(m.WORKFLOW_PATHS["step5-isolated-conversion-mapping-validation.yml"],
                   (ROOT / m.WORKFLOW_PATHS["step5-isolated-conversion-mapping-validation.yml"]).read_text())
        self.base = self.commit()

    def tearDown(self):
        import errno
        import os
        import time
        os.chdir(self.old)
        # Only absorb a transient directory-entry race. Other cleanup errors
        # still fail the test; no validation assertions are relaxed.
        for attempt in range(5):
            try:
                self.temp.cleanup()
                break
            except OSError as exc:
                if exc.errno != errno.ENOTEMPTY or attempt == 4:
                    raise
                time.sleep(0.05 * (attempt + 1))

    def git(self, *args):
        import subprocess
        return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout.strip()

    def write(self, path, content):
        file = Path(path)
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text(content)

    def commit(self):
        self.git("add", ".")
        self.git("commit", "-qm", "fixture", "--allow-empty")
        return self.git("rev-parse", "HEAD")

    def test_only_changed_test_class_invalidates_its_proof(self):
        path = "AgentPortal.Tests/One.cs"
        self.write(path, Path(path).read_text().replace("Shared.Value();", "Shared.Value(); Shared.Value();"))
        self.assertEqual(["AgentPortal.Tests.One"], m.step5_dependency_change(self.base, self.commit()))

    def test_test_fix_mixed_with_step5_control_changes_stays_bounded(self):
        path = "AgentPortal.Tests/Two.cs"
        self.write(path, Path(path).read_text().replace("Case() {}", "Case() { Assert.True(true); }"))
        control = "scripts/validation-resume.py"
        self.write(control, Path(control).read_text() + "\n# planner-only fixture change\n")
        self.assertEqual(["AgentPortal.Tests.Two"], m.step5_dependency_change(self.base, self.commit()))

    def test_prior_discovery_drops_unchanged_helper_without_escalating_changed_test(self):
        path = "AgentPortal.Tests/Two.cs"
        self.write(path, Path(path).read_text().replace("Case() {}", "Case() { Assert.True(true); }"))
        self.assertEqual(
            ["AgentPortal.Tests.Two"],
            m._step5_discovered_repair_classes(
                ["AgentPortal.Tests.Two", "AgentPortal.Tests.Shared"],
                ["AgentPortal.Tests.Two.Case"],
                [path],
            ),
        )

    def test_pr_local_cache_uses_canonical_bounded_repair_decision(self):
        import json
        path = "AgentPortal.Tests/Two.cs"
        self.write(path, Path(path).read_text().replace("Case() {}", "Case() { Assert.True(true); }"))
        control = "scripts/validation-resume.py"
        self.write(control, Path(control).read_text() + "\n# planner-only fixture change\n")
        head = self.commit()
        capsule = Path("step5-cache")
        capsule.mkdir()
        (capsule / "metadata.json").write_text(json.dumps({
            "schemaVersion": 1,
            "headSha": self.base,
            "baseSha": self.base,
            "runId": 71,
        }))
        trx = (
            '<TestRun><Results>'
            '<UnitTestResult testName="AgentPortal.Tests.Two.Case" outcome="Passed" />'
            '</Results><ResultSummary outcome="Completed">'
            '<Counters total="1" passed="1" failed="0" error="0" timeout="0" '
            'aborted="0" disconnected="0" inProgress="0" pending="0" />'
            '</ResultSummary></TestRun>'
        )
        (capsule / "candidate.trx").write_text(trx)
        (capsule / "baseline.trx").write_text(trx)

        decision, rejection = m._step5_cached_decision(head, self.base, str(capsule))
        self.assertIsNone(rejection)
        self.assertEqual("repair", decision["mode"])
        self.assertEqual(["AgentPortal.Tests.Two"], decision["repairClasses"])
        self.assertTrue(decision["resumeCache"])
        self.assertEqual("pr_local_cached_child_evidence", decision["reason"])

    def test_shared_fixture_change_requires_full_proof(self):
        path = "AgentPortal.Tests/Shared.cs"
        self.write(path, Path(path).read_text().replace("Value() {}", "Value() { return; }"))
        self.assertIsNone(m.step5_dependency_change(self.base, self.commit()))

    def test_release_workflow_only_reruns_its_control_consumers(self):
        self.write(".github/workflows/all-intentional-direct-release-20260918.yml", "changed")
        head = self.commit()
        self.assertEqual(["AgentPortal.Tests.Release"], m.step5_dependency_change(self.base, head))
        self.assertFalse(m._step5_baseline_inputs_equivalent(self.base, head))

    def test_baseline_short_circuit_preserves_dependency_decision(self):
        for path in ('Docs/releases/direct-release-request.json',
                     'scripts/release-lifecycle.py', 'AgentPortal/Program.cs'):
            with self.subTest(path=path):
                self.write(path, 'changed')
                head = self.commit()
                self.assertEqual(m.step5_dependency_change(self.base, head) == [],
                                 m._step5_baseline_inputs_equivalent(self.base, head))

    def test_direct_repository_read_is_not_neutral_documentation(self):
        self.write("Docs/releases/direct-release-request.json", "changed")
        self.assertEqual(["AgentPortal.Tests.Direct"], m.step5_dependency_change(self.base, self.commit()))

    def test_unconsumed_control_change_and_equivalent_base_preserve_suite(self):
        self.write("scripts/release-lifecycle.py", "changed")
        head = self.commit()
        self.assertEqual([], m.step5_dependency_change(self.base, head))
        self.assertTrue(m._step5_baseline_inputs_equivalent(self.base, head))
        self.assertEqual([], m.step5_dependency_change(head, self.commit()))

    def test_control_only_change_short_circuits_before_test_archive(self):
        path = "scripts/validation-resume.py"
        self.write(path, Path(path).read_text() + "\n# control-only fixture change\n")
        head = self.commit()
        original_run = m.subprocess.run

        def guarded_run(args, *pargs, **kwargs):
            if list(args[:2]) == ["git", "archive"]:
                raise AssertionError("Step 5 control-authority proof must not archive the test graph")
            return original_run(args, *pargs, **kwargs)

        with patch.object(m.subprocess, "run", side_effect=guarded_run):
            self.assertEqual([], m.step5_dependency_change(self.base, head))

    def test_manifest_content_identity_survives_unrelated_commit(self):
        workflow = "step5-isolated-conversion-mapping-validation.yml"
        prior = m.gate_dependency_manifests(workflow, self.base)
        self.write("scripts/release-lifecycle.py", "control-only")
        current = m.gate_dependency_manifests(workflow, self.commit())
        self.assertEqual(prior["candidate-full"]["contentIdentity"], current["candidate-full"]["contentIdentity"])
        self.write(".github/workflows/all-intentional-direct-release-20260918.yml", "new-control")
        changed = m.gate_dependency_manifests(workflow, self.commit())
        self.assertNotEqual(prior["candidate-full"]["sourceIdentity"], changed["candidate-full"]["sourceIdentity"])

    def test_real_gate_planner_invalidates_newly_derived_control_input(self):
        workflow = "step5-isolated-conversion-mapping-validation.yml"
        self.write(".github/workflows/all-intentional-direct-release-20260918.yml", "changed contract")
        current = self.commit()
        steps = {gate["step"]: "success" for gate in m.WORKFLOWS[workflow]["gates"].values()}
        plan = m._plan_against_prior(workflow, current, {"id": 71, "head_sha": self.base}, steps, "fixture")
        self.assertTrue(plan["gates"]["candidate-full"]["run"])
        self.assertEqual("dependency_identity_changed", plan["gates"]["candidate-full"]["reason"])
        self.assertTrue(plan["gates"]["comparison"]["run"])
        self.assertFalse(plan["gates"]["candidate-focused"]["run"])

    def test_post_gate_receipt_change_does_not_invalidate_suite_execution(self):
        workflow = "step5-isolated-conversion-mapping-validation.yml"
        path = m.WORKFLOW_PATHS[workflow]
        # A validation-job-only change must not replace the candidate job proof.
        self.write(path, Path(path).read_text().replace("      - name: Preserve effective Step 5 baseline evidence", "      - name: Preserve effective Step 5 baseline evidence (receipt metadata)"))
        current = self.commit()
        steps = {gate["step"]: "success" for gate in m.WORKFLOWS[workflow]["gates"].values()}
        plan = m._plan_against_prior(workflow, current, {"id": 71, "head_sha": self.base}, steps, "fixture")
        self.assertFalse(plan["gates"]["candidate-full"]["run"])
        self.assertTrue(plan["gates"]["comparison"]["run"])

    def test_execution_contract_rejects_unknown_mutation_step(self):
        text = Path(m.WORKFLOW_PATHS["step5-isolated-conversion-mapping-validation.yml"]).read_text()
        mutated = text.replace("      - name: Run full AgentPortal candidate suite", "      - name: Unknown mutation\n        run: touch AgentPortal/Program.cs\n\n      - name: Run full AgentPortal candidate suite")
        self.assertNotEqual(m._step5_execution_contract(text), m._step5_execution_contract(mutated))
        rescheduled = text.replace("if: needs.plan.outputs.comparison_run == 'true' && needs.baseline-evidence.outputs.reusable != 'true'", "if: false")
        self.assertEqual(m._step5_execution_contract(text), m._step5_execution_contract(rescheduled))

    def test_assembly_global_and_application_inputs_fail_closed(self):
        path = "AgentPortal.Tests/One.cs"
        self.write(path, '[assembly: CollectionBehavior(DisableTestParallelization = true)]' + Path(path).read_text())
        self.assertIsNone(m.step5_dependency_change(self.base, self.commit()))
        self.git("reset", "--hard", self.base)
        self.write("Domain/Entity.cs", "changed")
        self.assertIsNone(m.step5_dependency_change(self.base, self.commit()))


class Step5ChildEvidenceTests(unittest.TestCase):
    @staticmethod
    def trx(outcomes, summary="Completed"):
        rows = ''.join(f'<UnitTestResult testName="{name}" outcome="{outcome}" />' for name, outcome in outcomes.items())
        return f'<TestRun><Results>{rows}</Results><ResultSummary outcome="{summary}"><Counters total="{len(outcomes)}" /></ResultSummary></TestRun>'

    def test_directional_baseline_upgrade_preserves_distinct_identities_and_rejects_drift(self):
        import subprocess
        workflow = '.github/workflows/step5-isolated-conversion-mapping-validation.yml'
        old = subprocess.check_output(['git', 'show', '8ea059cbe36be1c921e1f53f87095375e66f7520:' + workflow], text=True)
        current = Path(workflow).read_text()
        self.assertNotEqual(m._step5_child_contract(old, 'baseline'), m._step5_child_contract(current, 'baseline'))
        self.assertEqual(m._step5_child_contract(old, 'candidate'), m._step5_child_contract(current, 'candidate'))
        old_helper = subprocess.check_output(['git', 'show', '8ea059cbe36be1c921e1f53f87095375e66f7520:scripts/validation-resume.py'], text=True)
        with patch.object(m, 'git_show_file', side_effect=lambda sha, path: old if path == workflow else old_helper):
            self.assertTrue(m._step5_baseline_producer_compatible('producer', workflow))
            original = Path.read_text
            for before, after in [('10.0.401', '10.0.999'), ('dotnet restore', 'echo changed; dotnet restore'), ('ref: ${{ github.event.pull_request.base.sha }}', 'ref: main')]:
                altered = current.replace(before, after)
                with patch.object(Path, 'read_text', lambda path, *args, **kwargs: altered if str(path) == workflow else original(path, *args, **kwargs)):
                    self.assertFalse(m._step5_baseline_producer_compatible('producer', workflow))

    def test_candidate_materialization_binds_current_and_historical_validate_and_parser(self):
        import subprocess
        workflow = '.github/workflows/step5-isolated-conversion-mapping-validation.yml'
        helper = 'scripts/validation-resume.py'
        revision = '8ea059cbe36be1c921e1f53f87095375e66f7520'
        old = {path: subprocess.check_output(['git', 'show', revision + ':' + path], text=True)
               for path in (workflow, helper)}
        current = {path: Path(path).read_text() for path in (workflow, helper)}
        def prove(producer):
            with patch.object(m, 'git_show_file', side_effect=lambda sha, path: producer[path]):
                candidate = m._step5_candidate_producer_compatible('producer', workflow)
                self.assertEqual(candidate, m._step5_baseline_producer_compatible('producer', workflow))
                return candidate
        self.assertTrue(prove(old))
        self.assertTrue(prove(current))
        for producer in (old, current):
            for path, before, after in (
                (workflow, '  validate:', '  validate:\n    env:\n      UNKNOWN: changed'),
                (helper, 'def read_step5_results(', 'def read_step5_results_changed('),
            ):
                altered = dict(producer)
                self.assertIn(before, altered[path])
                altered[path] = altered[path].replace(before, after, 1)
                self.assertFalse(prove(altered))
        original = Path.read_text
        for path, before, after in (
            (helper, 'def cmd_step5_merge(args):', 'def cmd_step5_merge(args):\n    Path(args.output).write_text(\"forged\")'),
            (workflow, '  validate:', '  validate:\n    env:\n      UNKNOWN: changed'),
            (helper, 'def read_step5_results(', 'def read_step5_results_changed('),
        ):
            with patch.object(Path, 'read_text', lambda file, *a, **kw:
                              current[path].replace(before, after, 1) if str(file) == path else original(file, *a, **kw)):
                self.assertFalse(prove(old))
                self.assertFalse(prove(current))

    def test_whole_step5_reuse_requires_both_materialization_authorities(self):
        workflow = '.github/workflows/step5-isolated-conversion-mapping-validation.yml'
        current = Path(workflow).read_text()
        for candidate, baseline in ((False, True), (True, False), (True, True)):
            with patch.object(m, 'git_show_file', return_value=current), \
                 patch.object(m, '_step5_candidate_producer_compatible', return_value=candidate), \
                 patch.object(m, '_step5_baseline_producer_compatible', return_value=baseline):
                self.assertEqual(candidate and baseline, m._step5_jobs_unchanged('producer', workflow))

    def test_effective_summary_tracks_repaired_outcomes_in_both_directions(self):
        import xml.etree.ElementTree as ET
        with tempfile.TemporaryDirectory() as folder:
            prior, repair, output = [Path(folder) / name for name in ('prior.trx', 'repair.trx', 'out.trx')]
            for before, after in (('Failed', 'Passed'), ('Passed', 'Failed')):
                prior.write_text(self.trx({'Tests.A.Case': before}, before))
                repair.write_text(self.trx({'Tests.A.Case': after}, after))
                m.merge_step5_class_results(prior, repair, ['Tests.A'], output)
                self.assertEqual(after, ET.parse(output).find('ResultSummary').get('outcome'))
                self.assertEqual({'Tests.A.Case': after}, m.read_step5_results(output))

    def test_bounded_baseline_merges_only_affected_rows_and_rejects_incomplete_proof(self):
        with tempfile.TemporaryDirectory() as folder:
            prior, repair, output = [Path(folder) / name for name in ('prior.trx', 'repair.trx', 'out.trx')]
            prior.write_text(self.trx({'Tests.A.Old': 'Failed', 'Tests.B.Keep': 'Passed'}))
            repair.write_text(self.trx({'Tests.A.Old': 'Passed', 'Tests.A.New': 'Passed'}))
            m.merge_step5_class_results(prior, repair, ['Tests.A'], output)
            self.assertEqual({'Tests.A.Old': 'Passed', 'Tests.A.New': 'Passed', 'Tests.B.Keep': 'Passed'}, m.read_step5_results(output))
            for rows in ({'Tests.A.New': 'Passed'}, {'Tests.B.Keep': 'Passed'}, {'Tests.A.Old': 'Unknown'}, {'Tests.A.Old': 'Passed', 'Tests.B.Keep': 'Failed'}):
                repair.write_text(self.trx(rows))
                with self.assertRaises(ValueError):
                    m.merge_step5_class_results(prior, repair, ['Tests.A'], output)
            repair.write_text(self.trx({'Tests.A.Old': 'NotExecuted'}))
            m.merge_step5_class_results(prior, repair, ['Tests.A'], output)
            self.assertEqual('NotExecuted', m.read_step5_results(output)['Tests.A.Old'])

    def test_baseline_discovery_reads_approved_revision_not_candidate_checkout(self):
        with patch.object(m, 'git_show_file', return_value='namespace Tests; public class NewTests {}') as read:
            result = m._step5_discovered_repair_classes(['Tests.NewTests'], (), ['AgentPortal.Tests/New.cs'], source_revision='approved')
        self.assertIsNone(result)
        read.assert_called_once_with('approved', 'AgentPortal.Tests/New.cs')

    def test_baseline_bounded_evidence_keeps_producer_and_requires_fresh_classes(self):
        prior, current, head = 'a' * 40, 'b' * 40, 'c' * 40
        artifact = 'step5-baseline-' + prior
        run = {'id': 77, 'head_sha': head}
        with patch.dict(m.os.environ, {'GITHUB_TOKEN': 'fixture'}), \
             patch.object(m, '_artifact_rows', return_value=[]), \
             patch.object(m, 'api_get', return_value={'workflow_runs': [run]}), \
             patch.object(m, '_run_artifact_names', return_value={artifact}), \
             patch.object(m, '_trusted_pr_run', return_value=True), \
             patch.object(m, '_step5_baseline_producer_compatible', return_value=True), \
             patch.object(m, 'step5_dependency_change', return_value=['Tests.A']), \
             patch.object(m, 'git_changed', return_value=['.github/workflows/example.yml']), \
             patch.object(m, '_step5_artifact_results', return_value={'Tests.A.Old': 'Passed', 'Tests.B.Keep': 'Passed'}):
            result = m.compute_step5_baseline_evidence('owner/repo', current)
        self.assertFalse(result['reusable'])
        self.assertEqual(['Tests.A'], result['repairClasses'])
        self.assertEqual(prior, result['evidenceBaseSha'])
        self.assertEqual(77, result['evidenceRunId'])

    def test_receipt_preserves_producer_and_records_only_observed_runtime(self):
        import tempfile
        import json
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "receipt.json"
            path.write_text(json.dumps({"priorRunId": 71, "gates": {
                "green": {"step": "Green child", "run": False, "evidenceRunId": 71,
                          "producerReceipt": {"result": "success", "jobId": 700, "runId": 71, "stepNumber": 5}},
                "failed": {"step": "Failed child", "run": True},
            }}))
            jobs = {"jobs": [{"id": 901, "steps": [
                {"name": "Green child", "conclusion": "success", "number": 2},
                {"name": "Failed child", "conclusion": "failure", "number": 3},
            ]}]}
            with patch.dict(m.os.environ, {"GITHUB_TOKEN": "fixture"}), \
                 patch.object(m, "api_get", return_value=jobs), \
                 patch.object(m.subprocess, "run", side_effect=FileNotFoundError):
                m.cmd_record_evidence(SimpleNamespace(plan=str(path), output=str(path), repository="MYLEGND/masterapp", run_id=99))
            evidence = json.loads(path.read_text())["gates"]
            self.assertEqual(71, evidence["green"]["receipt"]["producingRunId"])
            self.assertEqual(700, evidence["green"]["receipt"]["producerJobId"])
            self.assertEqual("success", evidence["green"]["receipt"]["result"])
            self.assertIsNone(evidence["green"]["receipt"]["actualToolchain"])
            self.assertEqual("failure", evidence["failed"]["receipt"]["result"])
            self.assertNotIn("dotnet", evidence["failed"]["receipt"]["actualToolchain"])

    def test_partial_aborted_and_empty_trx_are_rejected(self):
        import tempfile
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "candidate.trx"
            for content in (self.trx({"Suite.Case": "Passed"}, "Aborted"), "<TestRun />", self.trx({})):
                path.write_text(content)
                with self.assertRaises(ValueError):
                    m.read_step5_results(path)

    def test_cross_branch_search_skips_incompatible_and_untrusted_parent(self):
        repository = "MYLEGND/masterapp"
        def run(number, branch, conclusion="failure", trusted=True):
            sha = str(number) * 40
            return {"id": number, "head_sha": sha, "head_branch": branch,
                "path": m.WORKFLOW_PATHS["step5-isolated-conversion-mapping-validation.yml"],
                "event": "pull_request", "status": "completed", "conclusion": conclusion,
                "head_repository": {"full_name": repository if trusted else "outsider/fork"},
                "pull_requests": [{"base": {"ref": m.TRUSTED_PR_BASE}, "head": {"sha": sha, "repo": {"full_name": repository}}}]}
        runs = [run(3, "fork", trusted=False), run(2, "new-incompatible"), run(1, "older-compatible")]
        def api(repo, path, token):
            if "workflows/" in path:
                self.assertNotIn("branch=", path)
                return {"workflow_runs": runs}
            if "jobs?" in path:
                return {"jobs": [{"steps": [{"name": "Preserve completed candidate results", "conclusion": "success"}]}]}
            raise AssertionError(path)
        def download(repo, number, artifact, directory):
            (directory / "candidate.trx").write_text(self.trx({"AgentPortal.Tests.One.Case": "Passed"}))
        with patch.object(m, "api_get", side_effect=api), \
             patch.object(m, "step5_dependency_change", side_effect=lambda prior, current: [] if prior == "1" * 40 else None), \
             patch.object(m, "_step5_candidate_producer_compatible", return_value=True), \
             patch.object(m, "_run_artifact_names", side_effect=lambda repo, number, token: {"step5-candidate-" + str(number) * 40}), \
             patch.object(m, "_download_run_artifact", side_effect=download):
            result = m._step5_prior_candidate_evidence(repository, 99, "current", "token", "a" * 40)
        self.assertEqual(1, result["runId"])

    def test_green_repaired_child_survives_failed_sibling_comparison(self):
        import tempfile
        import subprocess
        import os
        workflow = (ROOT / ".github/workflows/step5-isolated-conversion-mapping-validation.yml").read_text()
        block = workflow.split("      - name: Prove Step 5 adds no full-suite failures", 1)[1]
        body = block.split("          python3 - <<'PY'\n", 1)[1].split("          PY\n", 1)[0]
        body = "\n".join(line[10:] for line in body.splitlines())
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for directory, filename, outcomes in (
                ("step5-prior-candidate", "candidate.trx", {"AgentPortal.Tests.One.Case": "Failed", "AgentPortal.Tests.Two.Case": "Failed"}),
                ("step5-prior-baseline", "baseline.trx", {"AgentPortal.Tests.One.Case": "Passed", "AgentPortal.Tests.Two.Case": "Passed"}),
                ("step5-repair", "repair.trx", {"AgentPortal.Tests.One.Case": "Passed"}),
            ):
                target = root / directory
                target.mkdir()
                (target / filename).write_text(self.trx(outcomes))
            body = body.replace("/tmp/step5-", str(root / "step5-"))
            result = subprocess.run(["python3", "-c", body], cwd=ROOT, capture_output=True, text=True,
                env={**os.environ, "VALIDATION_MODE": "repair", "REPAIR_CLASSES": "AgentPortal.Tests.One", "GITHUB_OUTPUT": str(root / "outputs")})
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            effective = m.read_step5_results(root / "step5-effective/candidate.trx")
            self.assertEqual("Passed", effective["AgentPortal.Tests.One.Case"])
            self.assertEqual("Failed", effective["AgentPortal.Tests.Two.Case"])
            outputs = (root / "outputs").read_text()
            self.assertIn("effective_evidence=true", outputs)
            self.assertIn("introduced=true", outputs)
            self.assertIn("introduced_classes=AgentPortal.Tests.Two", outputs)


class ReleaseAttemptNonentryTests(unittest.TestCase):
    def setUp(self):
        # These fixtures represent old admission-only runs. Their source must
        # remain pinned, even when the current release uses migration-first DAG.
        import subprocess
        self.source = subprocess.check_output([
            'git', 'show',
            '88936a82f9a94b93dedb18fcfe73f18a89410c91:.github/workflows/'
            + m.DIRECT_RELEASE_WORKFLOW], text=True)
        self.omitted = [{'name': 'admission', 'conclusion': 'success'},
                        {'name': 'preserve-rollback', 'conclusion': 'skipped'}]
        self.skipped = [{'name': 'admission', 'conclusion': 'success'},
                        {'name': 'discover-live', 'conclusion': 'skipped'},
                        {'name': 'release', 'conclusion': 'skipped'}]

    def observer_jobs(self):
        # Sanitized Actions shape from admission-only failure 37671062294.
        return [{'conclusion': 'failure',
          'name': 'admission',
          'status': 'completed',
          'steps': [{'conclusion': 'success', 'name': 'Set up job', 'status': 'completed'},
                    {'conclusion': 'success', 'name': 'Run actions/checkout@v4', 'status': 'completed'},
                    {'conclusion': 'success',
                     'name': 'Verify selected authority belongs to protected event history',
                     'status': 'completed'},
                    {'conclusion': 'success',
                     'name': 'Install canonical admission evidence transport',
                     'status': 'completed'},
                    {'conclusion': 'success', 'name': 'Bind admission evidence runtime', 'status': 'completed'},
                    {'conclusion': 'failure', 'name': 'Admit canonical release resource ownership', 'status': 'completed'},
                    {'conclusion': 'success', 'name': 'Post Run actions/checkout@v4', 'status': 'completed'},
                    {'conclusion': 'success', 'name': 'Complete job', 'status': 'completed'}]},
         {'conclusion': 'skipped', 'name': 'discover-live', 'status': 'completed', 'steps': []},
         {'conclusion': 'skipped', 'name': 'preserve-rollback', 'status': 'completed', 'steps': []},
         {'conclusion': 'success',
          'name': 'release-state-receipt',
          'status': 'completed',
          'steps': [{'conclusion': 'success', 'name': 'Set up job', 'status': 'completed'},
                    {'conclusion': 'success', 'name': 'Capture exact release step-state receipt', 'status': 'completed'},
                    {'conclusion': 'success',
                     'name': 'Preserve exact release step-state receipt artifact',
                     'status': 'completed'},
                    {'conclusion': 'skipped',
                     'name': 'Bind successful release to canonical validated package',
                     'status': 'completed'},
                    {'conclusion': 'skipped', 'name': 'Preserve validated package release binding', 'status': 'completed'},
                    {'conclusion': 'success', 'name': 'Complete job', 'status': 'completed'}]},
         {'conclusion': 'skipped', 'name': 'release', 'status': 'completed', 'steps': []},
         {'conclusion': 'skipped',
          'name': 'target-release-receipts (${{ matrix.app }})',
          'status': 'completed',
          'steps': []},
         {'conclusion': 'success',
          'name': 'wake-release-lifecycle-after-terminal-release',
          'status': 'completed',
          'steps': [{'conclusion': 'success', 'name': 'Set up job', 'status': 'completed'},
                    {'conclusion': 'success',
                     'name': 'Checkout protected lifecycle wake authority',
                     'status': 'completed'},
                    {'conclusion': 'success',
                     'name': 'Wake protected lifecycle after terminal direct release',
                     'status': 'completed'},
                    {'conclusion': 'success',
                     'name': 'Post Checkout protected lifecycle wake authority',
                     'status': 'completed'},
                    {'conclusion': 'success', 'name': 'Complete job', 'status': 'completed'}]}]

    def test_current_nonentry_preserves_reviewed_observers_and_rejects_mutation(self):
        source=(ROOT / '.github/workflows' / m.DIRECT_RELEASE_WORKFLOW).read_text()
        jobs=self.observer_jobs()+[dict(name='migration-readiness',status='completed',conclusion='skipped',steps=[])]
        self.assertTrue(m.release_attempt_never_entered(jobs,source))
        self.assertFalse(m.release_attempt_never_entered(jobs[:-1],source))
        for before,after in (
            ('run: python3 scripts/wake-release-lifecycle.py','run: python3 scripts/deploy-approved-app.py'),
            ('      actions: write','      actions: write\n      id-token: write'),
            ('      contents: read\n      actions: write','      contents: write\n      actions: write')):
            self.assertFalse(m.release_attempt_never_entered(jobs,source.replace(before,after)))
        jobs[-1]['conclusion']='success'
        self.assertFalse(m.release_attempt_never_entered(jobs,source))

    def test_actual_nonentry_with_authenticated_terminal_observers(self):
        jobs = self.observer_jobs()
        self.assertFalse(m.release_attempt_never_entered(jobs))
        self.assertTrue(m.release_attempt_never_entered(jobs, self.source))
        for kind in ('transaction', 'operation'):
            self.assertIsNone(self.check_history(kind, [jobs]))

    def test_observer_nonentry_rejects_unknown_partial_active_and_mutating_evidence(self):
        import copy
        jobs = self.observer_jobs()
        variants = [jobs[:-1], jobs + [jobs[-1]], jobs + [{'name': 'schema-write'}]]
        for job_name, field, value in (
            ('release', 'conclusion', 'failure'),
            ('release-state-receipt', 'status', 'in_progress'),
            ('target-release-receipts (${{ matrix.app }})', 'conclusion', 'success'),
            ('release-state-receipt', 'steps', [{'name': 'Deploy Azure', 'status': 'completed', 'conclusion': 'success'}]),
        ):
            changed = copy.deepcopy(jobs)
            next(row for row in changed if row['name'] == job_name)[field] = value
            variants.append(changed)
        for changed in variants:
            self.assertFalse(m.release_attempt_never_entered(changed, self.source))
        for before, after in (
            ('run: python3 scripts/wake-release-lifecycle.py', 'run: python3 scripts/deploy-approved-app.py'),
            ('actions: read', 'actions: write'),
            ('      actions: write', '      actions: write\n      id-token: write'),
            ('      contents: read\n      actions: write', '      contents: write\n      actions: write'),
            ('          GH_TOKEN: ${{ github.token }}', '          GH_TOKEN: ${{ github.token }}\n          AZURE_CREDENTIALS: ${{ secrets.AZURE_CREDENTIALS }}'),
            ('Capture exact release step-state receipt', 'Capture and mutate release'),
        ):
            self.assertIn(before, self.source)
            self.assertFalse(m.release_attempt_never_entered(jobs, self.source.replace(before, after)))
        entered = copy.deepcopy(jobs)
        next(row for row in entered if row['name'] == 'release')['conclusion'] = 'success'
        for kind in ('transaction', 'operation'):
            with self.assertRaises(m.ReleaseOperationHistoryUnproven):
                self.check_history(kind, [entered, jobs])

    def test_exact_omitted_and_complete_skipped_shapes(self):
        for jobs in [self.omitted, self.skipped, self.skipped + [self.omitted[1]]]:
            with self.subTest(jobs=jobs):
                self.assertTrue(m.release_attempt_never_entered(jobs, self.source))

    def test_ambiguous_entered_duplicate_and_partial_shapes_fail_closed(self):
        invalid = [None, [], self.omitted[1:], self.omitted + [self.omitted[0]],
                   self.omitted[:1], self.skipped[:2], self.omitted + [{'name': 'schema-write', 'conclusion': 'skipped'}],
                   [self.omitted[0], {'name': 'preserve-rollback', 'conclusion': 'success'}],
                   self.skipped + [{'name': 'schema-write', 'conclusion': 'success'}],
                   [self.skipped[0], self.skipped[1], {'name': 'release', 'conclusion': 'failure'}],
                   [self.omitted[0], dict(self.omitted[1], steps=[{'name': 'write', 'conclusion': 'success'}])]]
        for jobs in invalid:
            with self.subTest(jobs=jobs):
                self.assertFalse(m.release_attempt_never_entered(jobs, self.source))

    def test_omitted_jobs_require_the_exact_historical_admission_gates(self):
        self.assertFalse(m.release_attempt_never_entered(self.omitted))
        for source in ['', self.source.replace("needs: admission", "needs: unknown"),
                       self.source.replace("needs.admission.outputs.admitted == 'true'", "always()"),
                       self.source + "\n  schema-write:\n    runs-on: ubuntu-latest\n"]:
            self.assertFalse(m.release_attempt_never_entered(self.omitted, source))
        self.assertTrue(m.release_attempt_never_entered(self.skipped))

    def history(self, attempts, *, total_extra=0, untrusted=False, run_attempt=1, malformed_attempt=False, missing_count=False, status="completed"):
        prior = {'id': 8, 'head_branch': m.TRUSTED_PR_BASE, 'event': 'workflow_dispatch',
                 'head_repository': {'full_name': 'owner/repo'}, 'head_sha': 'b' * 40,
                 'path': '.github/workflows/' + m.DIRECT_RELEASE_WORKFLOW,
                 'run_attempt': len(attempts), 'status': status}
        if malformed_attempt:
            prior['run_attempt'] = run_attempt
        if untrusted:
            prior['head_repository']['full_name'] = 'other/repo'
        def api(repository, path, token):
            # A complete branch inventory may have no canonical push producer.
            if path == 'actions/runs?branch=Legend%2Ffast-track-release&per_page=100':
                observer = dict(id=7, head_branch=m.FAST_TRACK_PR_BASE,
                    event='workflow_dispatch', head_repository=dict(full_name='owner/repo'),
                    head_sha='d'*40, path='.github/workflows/deployment-diagnostics.yml')
                return {'workflow_runs': [observer], 'total_count': 1}
            if path.startswith('actions/artifacts?'):
                return {'artifacts': [], 'total_count': 0}
            if '/attempts/' in path:
                number = int(path.split('/attempts/')[1].split('/')[0])
                result = {'jobs': attempts[number-1], 'total_count': len(attempts[number-1]) + total_extra}
                if missing_count:
                    result.pop('total_count')
                return result
            if path == 'actions/runs/8/artifacts?per_page=100':
                return {'artifacts': [], 'total_count': 0}
            raise AssertionError(path)
        return prior, api

    def check_history(self, kind, attempts, **kwargs):
        prior, api = self.history(attempts, **kwargs)
        revision = 'a' * 40
        targets = {'portal': 'c' * 64}
        # Use the real target authority, avoiding a fabricated application key.
        targets = {next(iter(m.RELEASE_TARGETS)): 'c' * 64}
        identity = {'candidateRevision': revision, 'packageDigests': dict(sorted(targets.items()))}
        plan_id = m.hashlib.sha256(m.json.dumps(identity, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
        with patch.object(m, 'api_get', side_effect=api), \
             patch.object(m, '_release_history_api', side_effect=api), \
             patch.object(m, '_release_history_runs', return_value=[prior]), \
             patch.object(m, '_release_history_source', return_value=self.source), \
             patch.object(m, '_release_attempt_package_revision', return_value=revision), \
             patch.object(m, '_remember_release_exclusion'), \
             patch.dict(m._RELEASE_HISTORY_EXCLUSIONS, {}, clear=True):
            if kind == 'transaction':
                return m.release_transaction_plan_history('owner/repo', plan_id, revision, targets, 99, 1, 'token')
            return m.release_operation_history('owner/repo', 'op', revision, next(iter(targets)), 99, 1, 'token')

    def test_terminal_zero_job_attempt_is_not_a_missing_publication_owner(self):
        for kind in ('transaction', 'operation'):
            self.assertIsNone(self.check_history(kind, [self.skipped, []]))
            self.assertIsNone(self.check_history(kind, [[]]))

    def test_active_empty_attempt_and_incomplete_inventory_remain_unproven(self):
        for options in ({'status': 'in_progress'}, {'total_extra': 1}, {'missing_count': True}):
            with self.assertRaises(m.ReleaseOperationHistoryUnproven):
                self.check_history('transaction', [[]], **options)
        self.assertIsNone(self.check_history('transaction', [[], self.skipped], status='in_progress'))

    def test_empty_later_attempt_does_not_erase_earlier_publication(self):
        entered = [dict(name='release', status='completed', conclusion='failure', steps=[
            dict(name='Prepare complete immutable release transaction', status='completed', conclusion='success')])]
        with self.assertRaisesRegex(m.ReleaseOperationHistoryUnproven, 'Original transaction plan is missing'):
            self.check_history('transaction', [entered, []])

    def test_missing_owner_reason_identifies_exact_run_and_attempt(self):
        with self.assertRaisesRegex(m.ReleaseOperationHistoryUnproven, 'run=8 attempt=1'):
            self.check_history('transaction', [[dict(name='admission', conclusion='failure')]])

    def test_both_history_owners_accept_exact_proven_nonentry(self):
        for kind in ['transaction', 'operation']:
            for jobs in [self.omitted, self.skipped]:
                with self.subTest(kind=kind, jobs=jobs):
                    self.assertIsNone(self.check_history(kind, [jobs]))

    def test_both_history_owners_reject_missing_duplicate_extra_partial_and_incomplete(self):
        for kind in ['transaction', 'operation']:
            for jobs in [self.omitted[1:], self.omitted + [self.omitted[0]],
                         self.omitted + [{'name': 'schema-write', 'conclusion': 'success'}], self.skipped[:2]]:
                with self.subTest(kind=kind, jobs=jobs):
                    with self.assertRaises(m.ReleaseOperationHistoryUnproven):
                        self.check_history(kind, [jobs])
            with self.subTest(kind=kind, incomplete=True):
                with self.assertRaises(m.ReleaseOperationHistoryUnproven):
                    self.check_history(kind, [self.omitted], total_extra=1)
            with self.subTest(kind=kind, untrusted=True):
                with self.assertRaises(RuntimeError):
                    self.check_history(kind, [self.omitted], untrusted=True)

    def test_both_history_owners_require_complete_positive_attempt_inventory(self):
        for kind in ['transaction', 'operation']:
            for count in [0, -1, None, True, '1']:
                with self.subTest(kind=kind, count=count):
                    with self.assertRaises(m.ReleaseOperationHistoryUnproven):
                        self.check_history(kind, [self.omitted], run_attempt=count, malformed_attempt=True)
            for kwargs in [{'missing_count': True}, {'total_extra': -1}]:
                with self.subTest(kind=kind, **kwargs):
                    with self.assertRaises(m.ReleaseOperationHistoryUnproven):
                        self.check_history(kind, [self.omitted], **kwargs)

    def test_latest_skipped_attempt_does_not_erase_entered_prior_transaction(self):
        entered = [self.omitted[0], {'name': 'release', 'status': 'completed', 'conclusion': 'success',
                   'steps': [{'name': 'Prepare complete immutable release transaction', 'conclusion': 'success'}]}]
        for kind in ['transaction', 'operation']:
            with self.subTest(kind=kind):
                with self.assertRaises(m.ReleaseOperationHistoryUnproven):
                    self.check_history(kind, [entered, self.omitted])


class HistoricalParallelVerifierTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import subprocess
        cls.checkout = '0f8eb4026a36b191a837f06a66e0c16ba78e584e'
        cls.old = subprocess.check_output(['git', 'show', cls.checkout + ':scripts/deploy-approved-app.py'], text=True)
        cls.workflow = subprocess.check_output(['git', 'show', cls.checkout + ':.github/workflows/' + m.DIRECT_RELEASE_WORKFLOW], text=True)
        cls.current = (ROOT / 'scripts/deploy-approved-app.py').read_text()

    def prove(self, deployment=None, workflow=None):
        workflow = self.workflow if workflow is None else workflow
        deployment = self.old if deployment is None else deployment
        steps = [{'name': name, 'status': 'completed', 'conclusion': 'success'}
                 for name in m.named_step_blocks(m._job_blocks(workflow)['release'])]
        job = {'status': 'completed', 'conclusion': 'success', 'steps': steps}
        state = {'schemaVersion': 2, 'runId': 71, 'runAttempt': 1,
                 'steps': steps, 'releaseJobConclusion': 'success', 'applicationReleaseSha': 'b' * 40}
        artifacts = [{'name': 'legend-release-step-state-' + self.checkout + '-71-1'},
                     {'name': 'translation-direct-release-' + self.checkout}]
        with patch.object(m, '_release_history_api', return_value={'artifacts': artifacts, 'total_count': 2}), \
             patch.object(m, '_release_history_json', return_value=state), \
             patch.object(m, '_release_history_source', side_effect=lambda repo, sha, path, token:
                          workflow if path.endswith('.yml') else deployment), \
             patch.dict(m._RELEASE_HISTORY_VERIFIED_PACKAGES, {}, clear=True):
            return m._release_attempt_package_revision('owner/repo', {'id': 71, 'head_sha': self.checkout, 'run_attempt': 1}, 1, job, 'token', 'portal')

    def test_reviewed_historical_parallel_reporting_preserves_package_proof(self):
        self.assertEqual('b' * 40, self.prove())
        self.assertEqual('b' * 40, self.prove(self.current))

    def test_pre_diagnostic_nonentry_still_requires_exact_target_witness(self):
        import subprocess
        start = 'masterapp-protect: approved revision ' + 'b' * 40 + ', ZIP sha256 ' + 'c' * 64
        witness = ('DeploymentStatusUnavailable: Azure deployment status remained unavailable for 3 consecutive reads '
                   '(before any upload). No deployment or rollback write was replayed; resume by reconciling the exact revision.')
        ending = '\n##[error]Process completed with exit code 1.'
        for revision in ('4fe015cdbf81b5253d64558bf056cb5fcc80fa87',
                         'ee32e1c5f9e08d17369855c250dc99d8c3e16ce0'):
            def source(repo, checkout, path, token):
                return subprocess.check_output(['git', 'show', revision + ':' + path], text=True)
            for log, expected in [(start + '\n' + witness + ending, True),
                                  (start + '\nDeploymentReconciliationRequired: unknown' + ending, False),
                                  (start + '\nSubmitting the verified immutable ZIP once.\n' + witness + ending, False),
                                  (witness + ending, False)]:
                with self.subTest(revision=revision, log=log), \
                     patch.object(m, '_release_history_source', side_effect=source), \
                     patch.object(m, '_release_job_log', return_value=log):
                    self.assertEqual(expected, m._historical_publication_failed_before_first_write(
                        'owner/repo', {'id': 71, 'status': 'completed'}, 'b' * 40, 'protect', revision, 'token'))

    def test_historical_nonentry_rejects_changed_intent_or_upload_order_before_logs(self):
        import subprocess
        deploy_path = 'scripts/deploy-approved-app.py'
        evidence_path = 'scripts/release-operation-evidence.py'
        publish = "        self.publisher('legend-release-operation-intent-' + self.operation_id, record)\n"
        intent = '        self.intent = record\n'
        guard = 'if self.history_error is not None and not allow_recovered_baseline:'
        for revision in ('4fe015cdbf81b5253d64558bf056cb5fcc80fa87',
                         'ee32e1c5f9e08d17369855c250dc99d8c3e16ce0'):
            sources = {path: subprocess.check_output(['git', 'show', revision + ':' + path], text=True)
                       for path in (deploy_path, evidence_path)}
            changes = (
                (evidence_path, publish + intent, intent + publish),
                (evidence_path, guard, 'if False:'),
                (deploy_path, '    submitted = False\n', '    azure.submit()\n    submitted = False\n'),
            )
            for path, before, after in changes:
                self.assertEqual(1, sources[path].count(before))
                altered = dict(sources, **{path: sources[path].replace(before, after, 1)})
                with self.subTest(revision=revision, changed=path, before=before), \
                     patch.object(m, '_release_history_source', side_effect=lambda repo, checkout, path, token: altered[path]), \
                     patch.object(m, '_release_job_log') as logs:
                    self.assertFalse(m._historical_publication_failed_before_first_write(
                        'owner/repo', {'id': 71, 'status': 'completed'}, 'b' * 40, 'protect', revision, 'token'))
                    logs.assert_not_called()

    def test_pre_diagnostic_component_workers_preserve_successful_receipt_compatibility(self):
        import subprocess
        prior = subprocess.check_output(['git', 'show',
            '4fe015cdbf81b5253d64558bf056cb5fcc80fa87:scripts/deploy-approved-app.py'], text=True)
        self.assertEqual('b' * 40, self.prove(prior))
        for before, after in (
            ("if digest != row['packageDigest']:", 'if False:'),
            ('outcome = publish_prepared_target(key, revision, package_root, plan)', "outcome = 'preserved'"),
        ):
            with self.subTest(before=before):
                self.assertIn(before, prior)
                with self.assertRaises(m.ReleaseOperationHistoryUnproven):
                    self.prove(prior.replace(before, after))

    def test_unknown_parallel_or_immutable_target_verifier_edits_fail_closed(self):
        for source in (self.old, self.current):
            for before, after in (
                ('outcome = publish_prepared_target(key, revision, package_root, plan)',
                 "outcome = 'preserved'"),
                ("if digest != row['packageDigest']:", 'if False:'),
                ('max_workers=max(1, len(keys))', 'max_workers=100'),
            ):
                with self.subTest(before=before, historical=source == self.old):
                    self.assertIn(before, source)
                    with self.assertRaises(m.ReleaseOperationHistoryUnproven):
                        self.prove(source.replace(before, after))

    def test_historical_package_revision_still_requires_verified_workflow_step(self):
        with self.assertRaises(m.ReleaseOperationHistoryUnproven):
            self.prove(workflow=self.workflow.replace('scripts/release-package.py verify', 'scripts/release-package.py inspect'))


class ReviewedBrowserContractTests(unittest.TestCase):
    def test_browser_extension_requires_reviewed_closure_and_inherited_tools(self):
        import copy
        prior = dict(schemaVersion=3, migrations=[dict(id='20261026210738_AddReceptionistManagement', supported=True)])
        entry = dict(id='20261026223148_ReceptionistBrowserCreation', supported=True)
        contract = dict(schemaVersion=3, migrations=[*prior['migrations'], entry])
        trusted = dict(runtimeIdentity='a'*64, toolIdentity='c'*64, executionIdentity='d'*64)
        current = dict(trusted, runtimeIdentity='b'*64)
        def prove(value=contract, base=prior):
            return m.reviewed_browser_contract_extension('e'*40, 'f'*40, value, base, current, trusted)
        self.assertFalse(prove())  # Unreviewed closure remains closed in production.
        digest=hashlib.sha256(json.dumps(entry,sort_keys=True,separators=(',',':')).encode()).hexdigest()
        with patch.multiple(m, REVIEWED_BROWSER_RUNTIME_PAIRS=frozenset({('a'*64,'b'*64)}), REVIEWED_BROWSER_ENTRY=digest), \
             patch.object(m.subprocess, 'run', return_value=SimpleNamespace(returncode=0)):
            self.assertTrue(prove())
            for field in ('toolIdentity','executionIdentity','runtimeIdentity'):
                saved=current[field]; current[field]='0'*64
                self.assertFalse(prove()); current[field]=saved
            for field in ('supported','id'):
                bad=copy.deepcopy(contract); bad['migrations'][-1][field]=False
                self.assertFalse(prove(bad))
            bad=copy.deepcopy(contract); bad['migrations'][0]['supported']=False
            self.assertFalse(prove(bad))
            self.assertFalse(prove(dict(contract, migrations=contract['migrations'][:-1])))
            self.assertFalse(prove(dict(contract, migrations=contract['migrations'][::-1])))
            with patch.object(m.subprocess,'run',return_value=SimpleNamespace(returncode=1)):
                self.assertFalse(prove())


class ReviewedReceptionistContractTests(unittest.TestCase):
    def setUp(self):
        import copy
        self.copy=copy.deepcopy
        self.candidate='a'*40;self.approved='b'*40
        self.trusted=dict(runtimeIdentity=m.REVIEWED_RECEPTIONIST_BASE_RUNTIME,toolIdentity='c'*64,executionIdentity='d'*64)
        self.current=dict(self.trusted,runtimeIdentity=m.REVIEWED_RECEPTIONIST_RUNTIME)
        self.prior=dict(schemaVersion=3,migrations=[dict(id='20260101000000_Original',supported=False),dict(id='20260201000000_Original',supported=False)])
        self.entry=dict(id='20261020160000_AddReceptionistCoordination',supported=True,
            columns=[dict(schema='dbo',table='Synthetic',name='Key',type='uniqueidentifier',nullable=True,
                default=None,precision=None,scale=None,collation=None)],tables=[],indexes=[],guards=[],foreignKeys=[])
        self.contract=dict(schemaVersion=3,migrations=self.copy(self.prior['migrations'])+[self.copy(self.entry)])
        # The actual accepted entry is checked against both unpatched production
        # pins by the existing real-C# contract gate. This synthetic unit shape
        # isolates source/identity/refusal behavior without embedding a second
        # production migration definition in Python tests.
        digest=hashlib.sha256(json.dumps(self.entry,sort_keys=True,separators=(',',':')).encode()).hexdigest()
        pin=patch.object(m,'REVIEWED_RECEPTIONIST_ENTRY',digest);pin.start();self.addCleanup(pin.stop)

    def prove(self,contract=None,prior=None,returns=(0,0)):
        with patch.object(m,'migration_probe_identity',side_effect=[self.current,self.trusted]), \
             patch.object(m,'migration_contract_static_styles_only',return_value=False), \
             patch.object(m.subprocess,'run',side_effect=[SimpleNamespace(returncode=r) for r in returns]) as run:
            result=m.candidate_migration_contract_proven(self.candidate,self.approved,
                self.contract if contract is None else contract,self.prior if prior is None else prior)
            self.calls=[c.args[0] for c in run.call_args_list]
            return result

    def test_exact_reviewed_closure_prefix_entry_and_inherited_authority_are_required(self):
        proof=self.prove()
        self.assertEqual('approved-prefix-reviewed-receptionist-entry',proof['rule'])
        self.assertEqual(m.REVIEWED_RECEPTIONIST_RUNTIME,proof['runtimeIdentity'])
        self.assertEqual(m.REVIEWED_RECEPTIONIST_BASE_RUNTIME,proof['approvedRuntimeIdentity'])
        self.assertEqual([
            ['git','merge-base','--is-ancestor',self.approved,self.candidate],
            ['git','diff','--quiet',self.approved,self.candidate,'--','scripts','.github']],self.calls)

    def test_reviewed_runtime_cannot_omit_suffix_by_claiming_old_contract_equality(self):
        with self.assertRaisesRegex(ValueError,'CANDIDATE_MIGRATION_CONTRACT_UNPROVEN'):
            self.prove(contract=self.copy(self.prior))

    def test_unknown_runtime_baseline_tool_or_execution_never_uses_entry_pin(self):
        for target,key,value in [('current','runtimeIdentity','e'*64),('trusted','runtimeIdentity','e'*64),
                ('current','toolIdentity','e'*64),('current','executionIdentity','e'*64),
                ('current','toolIdentity',None),('current','executionIdentity','')]:
            row=getattr(self,target);old=row[key];row[key]=value
            with self.subTest(target=target,key=key),self.assertRaisesRegex(ValueError,'UNPROVEN'):self.prove()
            row[key]=old

    def test_altered_reordered_removed_prefix_and_extra_entry_fail(self):
        for kind in ('change','reorder','remove','extra','replace'):
            value=self.copy(self.contract)
            if kind=='change':value['migrations'][0]['supported']=True
            elif kind=='reorder':value['migrations'][:2]=value['migrations'][:2][::-1]
            elif kind=='remove':value['migrations'].pop(0)
            elif kind=='extra':value['migrations'].append(self.copy(self.entry))
            else:value['migrations'][0]['id']='20260101000000_Altered'
            with self.subTest(kind=kind),self.assertRaisesRegex(ValueError,'UNPROVEN'):self.prove(contract=value)

    def test_every_appended_entry_field_is_bound_including_unknown_metadata(self):
        for kind in ('supported','identity','type','default','foreignKeys','missing','unknown'):
            value=self.copy(self.contract);entry=value['migrations'][-1]
            if kind=='supported':entry['supported']=False
            elif kind=='identity':entry['id']='20261020160000_Unreviewed'
            elif kind in ('type','default'):entry['columns'][0][kind]='changed'
            elif kind=='foreignKeys':entry['foreignKeys']=[{'name':'unexpected'}]
            elif kind=='missing':entry.pop('guards')
            else:entry['unknown']=False
            with self.subTest(kind=kind),self.assertRaisesRegex(ValueError,'UNPROVEN'):self.prove(contract=value)

    def test_contract_versions_types_and_extra_top_level_fields_fail(self):
        for value in (dict(self.contract,schemaVersion=2),dict(self.contract,schemaVersion=True),
                      dict(self.contract,migrations={}),dict(self.contract,extra=False),[]):
            with self.subTest(value=value),self.assertRaisesRegex(ValueError,'UNPROVEN'):self.prove(contract=value)

    def test_current_trusted_ancestor_and_unchanged_control_source_are_mandatory(self):
        for values in ((1,0),(128,0),(0,1),(0,128)):
            with self.subTest(values=values),self.assertRaisesRegex(ValueError,'UNPROVEN'):self.prove(returns=values)

    def test_unchanged_runtime_still_cannot_invent_a_reviewed_suffix(self):
        self.current=dict(self.trusted)
        with self.assertRaisesRegex(ValueError,'metadata differs'):self.prove()

    def test_existing_full_runtime_hash_detects_unknown_source_mode_and_build_inputs(self):
        original='100644 blob '+'1'*40+'\tInfrastructure/Original.cs\0'
        workflow='jobs:\n  validated-migration-probe:\n    runs-on: ubuntu-latest\n    steps: []\n'
        def identity(rows):
            with patch.object(m.subprocess,'check_output',return_value=rows),patch.object(m,'git_show_file',return_value=workflow):
                return m.migration_probe_identity(self.candidate,self.candidate)['runtimeIdentity']
        expected=identity(original)
        for rows in ('',original.replace('100644','120000'),original.replace('1'*40,'2'*40),
                     original+'100644 blob '+'3'*40+'\tInfrastructure/UnknownDiscovery.cs\0',
                     original+'100644 blob '+'3'*40+'\tDirectory.Build.targets\0',
                     original+'100644 blob '+'3'*40+'\tSHARED/Unknown.cs\0'):
            with self.subTest(rows=rows):self.assertNotEqual(expected,identity(rows))


class ReviewedCanonicalPaymentContractTests(unittest.TestCase):
    def setUp(self):
        import copy
        self.copy = copy.deepcopy
        before, after = sorted(m.REVIEWED_CANONICAL_PAYMENT_RUNTIME_PAIRS)[0]
        self.trusted = dict(runtimeIdentity=before, toolIdentity='c'*64, executionIdentity='d'*64)
        self.current = dict(self.trusted, runtimeIdentity=after)
        self.prior = dict(schemaVersion=3, migrations=[dict(id='20261020160000_AddReceptionistCoordination', supported=True)])
        self.entry = dict(id='20261026000134_AddCanonicalSubscriptionPaymentCompletion', supported=True, columns=[], tables=[], indexes=[], guards=[], foreignKeys=[])
        self.contract = dict(schemaVersion=3, migrations=self.copy(self.prior['migrations'])+[self.entry])
        pin = patch.object(m, 'REVIEWED_CANONICAL_PAYMENT_ENTRY', hashlib.sha256(json.dumps(self.entry,
            sort_keys=True, separators=(',', ':')).encode()).hexdigest())
        pin.start(); self.addCleanup(pin.stop)

    def prove(self, contract=None, prior=None, returns=(0,0)):
        with patch.object(m, 'migration_probe_identity', side_effect=[self.current,self.trusted]), \
             patch.object(m, 'migration_contract_static_styles_only', return_value=False), \
             patch.object(m.subprocess, 'run', side_effect=[SimpleNamespace(returncode=r) for r in returns]):
            return m.candidate_migration_contract_proven('a'*40, 'b'*40,
                self.contract if contract is None else contract, self.prior if prior is None else prior)

    def test_exact_pair_and_inherited_tooling_prove_only_payment_suffix(self):
        for before,after in m.REVIEWED_CANONICAL_PAYMENT_RUNTIME_PAIRS:
            self.trusted['runtimeIdentity']=before;self.current['runtimeIdentity']=after
            with self.subTest(before=before):
                self.assertEqual('approved-prefix-reviewed-canonical-payment-entry', self.prove()['rule'])

    def test_only_independently_reviewed_live_and_prior_pairs_are_pinned(self):
        candidate='e097be398e774e6226b9a7cb72b41b7eec92eb8a3043af8e16e412381ca0051f'
        self.assertEqual({
            ('f6ae0f4012706ad346d743fb69dfbff4cc46238aa46a9f60687aede0a8cc57eb',candidate),
            ('3fbe9ce6512a2ebb7db43f0f45a5227786094ac6b287ccc3eca40c59da05f6e0',candidate),
        },m.REVIEWED_CANONICAL_PAYMENT_RUNTIME_PAIRS)

    def test_changed_runtime_tool_execution_or_missing_identity_rejected(self):
        for target, key, value in [('current','runtimeIdentity','e'*64),('trusted','runtimeIdentity','e'*64),
            ('current','toolIdentity','e'*64),('current','executionIdentity','e'*64),('current','toolIdentity',None)]:
            row=getattr(self,target); old=row[key]; row[key]=value
            with self.subTest(key=key),self.assertRaisesRegex(ValueError,'UNPROVEN'): self.prove()
            row[key]=old

    def test_missing_duplicate_changed_prefix_and_unknown_metadata_rejected(self):
        for kind in ('missing','duplicate','prefix','metadata','key_length','reordered'):
            value=self.copy(self.contract)
            if kind=='missing': value=self.copy(self.prior)
            elif kind=='duplicate': value['migrations'].append(self.copy(self.entry))
            elif kind=='prefix': value['migrations'][0]['supported']=False
            elif kind=='metadata': value['migrations'][-1]['unknown']=True
            elif kind=='reordered': value['migrations'].reverse()
            else: value['migrations'][-1]['columns']=[dict(type='nvarchar(159)')]
            with self.subTest(kind=kind),self.assertRaisesRegex(ValueError,'UNPROVEN'): self.prove(contract=value)

    def test_missing_receptionist_even_with_equal_prefix_rejected(self):
        prior=self.copy(self.prior);prior['migrations'][0]['id']='20260101000000_Other'
        value=self.copy(self.contract);value['migrations'][0]=prior['migrations'][0]
        with self.assertRaisesRegex(ValueError,'UNPROVEN'): self.prove(value,prior)

    def test_live_pair_rejects_changed_base_candidate_tool_and_omitted_prior_migration(self):
        self.trusted['runtimeIdentity']='f6ae0f4012706ad346d743fb69dfbff4cc46238aa46a9f60687aede0a8cc57eb'
        for target,key in (('trusted','runtimeIdentity'),('current','runtimeIdentity'),('current','toolIdentity')):
            row=getattr(self,target);original=row[key];row[key]='f'*64
            with self.subTest(target=target,key=key),self.assertRaisesRegex(ValueError,'UNPROVEN'):self.prove()
            row[key]=original
        prior=self.copy(self.prior);prior['migrations'].insert(0,dict(id='20260101000000_Original',supported=True))
        with self.assertRaisesRegex(ValueError,'UNPROVEN'):self.prove(prior=prior)

    def test_ancestry_and_control_source_changes_rejected(self):
        for values in ((1,0),(128,0),(0,1),(0,128)):
            with self.subTest(values=values),self.assertRaisesRegex(ValueError,'UNPROVEN'): self.prove(returns=values)

    def test_malformed_contract_rejected(self):
        for value in (dict(self.contract,schemaVersion=True),dict(self.contract,migrations={}),dict(self.contract,extra=1),[]):
            with self.subTest(value=value),self.assertRaisesRegex(ValueError,'UNPROVEN'): self.prove(contract=value)


class HistoricalProducerInventoryTests(unittest.TestCase):
    def setUp(self):
        self.repository='owner/repo';self.revision='a'*40;self.now=0;self.calls=[]
        self.path='actions/runs?branch=Legend%2Ffast-track-release&per_page=100'
        self.run=dict(id=7,head_sha=self.revision,head_branch=m.FAST_TRACK_PR_BASE,event='push',
            path='.github/workflows/'+m.FAST_TRACK_RELEASE_WORKFLOW,head_repository=dict(full_name=self.repository))
        self.rows=[self.run];self.payloads=None
    def api(self,repository,path,token):
        self.assertEqual(repository,self.repository);self.calls.append(path)
        if self.payloads is not None:return self.payloads[len(self.calls)-1]
        return dict(total_count=len(self.rows),workflow_runs=self.rows)
    def invoke(self):
        with patch.object(m,'api_get',side_effect=self.api):
            return m.fast_track_historical_producers(self.repository,self.revision,'fixture',clock=lambda:self.now)
    def test_default_first_page_omits_unreliable_head_and_event_filters(self):
        self.assertEqual(self.invoke(),[self.run]);self.assertEqual(self.calls,[self.path])
    def test_local_filter_separates_observer_other_head_and_other_workflow(self):
        self.rows += [dict(self.run,id=8,event='workflow_dispatch',path='.github/workflows/deployment-diagnostics.yml'),dict(self.run,id=9,head_sha='b'*40),dict(self.run,id=10,path='.github/workflows/other.yml')]
        self.assertEqual(self.invoke(),[self.run])
    def test_absent_exact_head_requires_complete_nonempty_branch(self):
        self.rows=[dict(self.run,head_sha='b'*40)];self.assertEqual(self.invoke(),[])
    def test_empty_branch_cannot_prove_absence(self):
        self.rows=[]
        with self.assertRaisesRegex(RuntimeError,'INCOMPLETE'):self.invoke()
    def test_complete_two_page_inventory(self):
        rows=[dict(self.run,id=i+1,head_sha='b'*40) for i in range(100)]
        self.payloads=[dict(total_count=101,workflow_runs=rows),dict(total_count=101,workflow_runs=[self.run|dict(id=101)])]
        self.assertEqual(self.invoke()[0]['id'],101);self.assertEqual(self.calls,[self.path,self.path+'&page=2'])
    def test_page_cannot_hide_conflicting_exact_producer(self):
        rows=[dict(self.run,id=i+1,head_sha='b'*40) for i in range(99)]+[self.run|dict(id=100)]
        self.payloads=[dict(total_count=101,workflow_runs=rows),dict(total_count=101,workflow_runs=[self.run|dict(id=101)])]
        with self.assertRaisesRegex(ValueError,'AMBIGUOUS'):self.invoke()
    def test_conflicting_success_and_failure_are_still_ambiguous(self):
        self.rows=[dict(self.run,conclusion='failure'),dict(self.run,id=8,conclusion='success')]
        with self.assertRaisesRegex(ValueError,'AMBIGUOUS'):self.invoke()
    def test_duplicate_ids_reject_even_if_other_head(self):
        self.rows.append(dict(self.run,head_sha='b'*40))
        with self.assertRaisesRegex(RuntimeError,'PRODUCER_UNPROVEN'):self.invoke()
    def test_duplicate_ids_across_pages(self):
        rows=[dict(self.run,id=i+1,head_sha='b'*40) for i in range(100)]
        self.payloads=[dict(total_count=101,workflow_runs=rows),dict(total_count=101,workflow_runs=[rows[0]])]
        with self.assertRaisesRegex(RuntimeError,'PRODUCER_UNPROVEN'):self.invoke()
    def test_changed_total_rejects(self):
        rows=[dict(self.run,id=i+1,head_sha='b'*40) for i in range(100)]
        self.payloads=[dict(total_count=101,workflow_runs=rows),dict(total_count=102,workflow_runs=[self.run|dict(id=101),self.run|dict(id=102)])]
        with self.assertRaisesRegex(RuntimeError,'INCOMPLETE'):self.invoke()
    def test_short_first_page_rejects(self):
        self.payloads=[dict(total_count=101,workflow_runs=[self.run])]
        with self.assertRaisesRegex(RuntimeError,'INCOMPLETE'):self.invoke()
    def test_missing_last_page_rejects(self):
        rows=[dict(self.run,id=i+1,head_sha='b'*40) for i in range(100)]
        self.payloads=[dict(total_count=101,workflow_runs=rows),dict(total_count=101,workflow_runs=[])]
        with self.assertRaisesRegex(RuntimeError,'INCOMPLETE'):self.invoke()
    def test_malformed_count_and_payload_reject(self):
        for payload in (None,[],{},dict(total_count=True,workflow_runs=[self.run]),dict(total_count='1',workflow_runs=[self.run]),dict(total_count=-1,workflow_runs=[]),dict(total_count=1001,workflow_runs=[]),dict(total_count=1,workflow_runs={})):
            with self.subTest(payload=payload):
                self.calls=[];self.payloads=[payload]
                with self.assertRaises(RuntimeError):self.invoke()
    def test_wrong_branch_repository_and_malformed_row_reject(self):
        for row in (None,{},dict(self.run,id=True),dict(self.run,id=0),dict(self.run,head_sha='x'),dict(self.run,head_branch='main'),dict(self.run,head_repository={}),dict(self.run,head_repository=dict(full_name='other/repo')),dict(self.run,event=''),dict(self.run,path=None)):
            with self.subTest(row=row):
                self.calls=[];self.rows=[row]
                with self.assertRaises(RuntimeError):self.invoke()
    def test_invalid_requested_revision_rejects_before_read(self):
        self.revision='x'
        with self.assertRaises(ValueError):self.invoke()
        self.assertEqual(self.calls,[])
    def test_repository_case_is_normalized(self):
        self.run['head_repository']['full_name']='OWNER/REPO';self.assertEqual(self.invoke(),[self.run])
    def test_late_first_response_rejects(self):
        original=self.api
        def api(*args):
            result=original(*args);self.now=30;return result
        with patch.object(m,'api_get',side_effect=api),self.assertRaises(m.EvidenceLookupUnavailable):
            m.fast_track_historical_producers(self.repository,self.revision,'fixture',clock=lambda:self.now)
    def test_shorter_enclosing_deadline_remains_binding(self):
        original=self.api
        def api(*args):
            result=original(*args);self.now=3;return result
        with m.evidence_lookup_budget(3,clock=lambda:self.now),patch.object(m,'api_get',side_effect=api),self.assertRaises(m.EvidenceLookupUnavailable):
            m.fast_track_historical_producers(self.repository,self.revision,'fixture',clock=lambda:self.now)
    def test_transport_error_does_not_become_absence_or_new_retry(self):
        with patch.object(m,'api_get',side_effect=m.EvidenceLookupUnavailable('transport')) as api,self.assertRaises(m.EvidenceLookupUnavailable):
            m.fast_track_historical_producers(self.repository,self.revision,'fixture',clock=lambda:self.now)
        self.assertEqual(api.call_count,1)
    def test_four_callers_share_canonical_helper(self):
        import inspect
        for name in ('fast_track_pending_routing','fast_track_completion_original','fast_track_completion_observer','fast_track_effective_baseline'):
            source=inspect.getsource(getattr(m,name));self.assertIn('fast_track_historical_producers(',source);self.assertNotIn('actions/runs?head_sha=',source)

    def test_original_run_identity_cannot_be_replaced_by_unique_other_id(self):
        direct=dict(self.run,status='completed',conclusion='failure')
        self.rows=[dict(self.run,id=8)]
        def api(repo,path,token):return direct if path=='actions/runs/7' else self.api(repo,path,token)
        with patch.object(m,'api_get',side_effect=api),patch.object(m,'fast_track_merged_source_proof') as proof:
            with self.assertRaisesRegex(ValueError,'ORIGINAL_PRODUCER_AMBIGUOUS'):
                m.fast_track_completion_original(self.repository,7,self.revision,'fixture')
        proof.assert_not_called()
    def test_original_positive_identity_still_requires_merged_source_proof(self):
        direct=dict(self.run,status='completed',conclusion='failure')
        def api(repo,path,token):return direct if path=='actions/runs/7' else self.api(repo,path,token)
        with patch.object(m,'api_get',side_effect=api),patch.object(m,'fast_track_merged_source_proof',side_effect=RuntimeError('next immutable source gate')) as proof:
            with self.assertRaisesRegex(RuntimeError,'next immutable source gate'):
                m.fast_track_completion_original(self.repository,7,self.revision,'fixture')
        proof.assert_called_once_with(self.repository,self.revision,'fixture')
    def test_final_page_late_response_rejects(self):
        rows=[dict(self.run,id=i+1,head_sha='b'*40) for i in range(100)]
        self.payloads=[dict(total_count=101,workflow_runs=rows),dict(total_count=101,workflow_runs=[self.run|dict(id=101)])]
        original=self.api
        def api(*args):
            result=original(*args)
            if len(self.calls)==2:self.now=30
            return result
        with patch.object(m,'api_get',side_effect=api),self.assertRaises(m.EvidenceLookupUnavailable):
            m.fast_track_historical_producers(self.repository,self.revision,'fixture',clock=lambda:self.now)
    def test_maximum_ten_pages_complete_without_extra_probe(self):
        rows=[dict(self.run,id=i+1,head_sha='b'*40) for i in range(1000)]
        rows[-1]['head_sha']=self.revision
        self.payloads=[dict(total_count=1000,workflow_runs=rows[i:i+100]) for i in range(0,1000,100)]
        self.assertEqual(self.invoke()[0]['id'],1000);self.assertEqual(len(self.calls),10)


    def test_canonical_workflow_wrong_event_or_suffix_still_rejects(self):
        for changes in (dict(event='workflow_dispatch'),dict(path=self.run['path']+'@wrong')):
            self.rows=[dict(self.run,**changes)]
            with self.subTest(changes=changes),self.assertRaisesRegex(RuntimeError,'HISTORY_PRODUCER_UNPROVEN'):self.invoke()
    def test_existing_child_attempt_consumer_propagates_incomplete_history(self):
        with patch.object(m,'api_get',return_value=dict(total_count=2,workflow_runs=[self.run])):
            with self.assertRaisesRegex(RuntimeError,'HISTORY_INCOMPLETE'):
                m.fast_track_child_attempts(self.repository,'migrations','fixture')
    def test_existing_child_record_consumer_propagates_incomplete_history(self):
        with patch.object(m,'api_get',return_value=dict(total_count=2,workflow_runs=[self.run])):
            with self.assertRaisesRegex(RuntimeError,'HISTORY_INCOMPLETE'):
                m._release_child_original_records(self.repository,'migrations','fixture',self.revision,99,1)
    def test_existing_operation_history_cannot_infer_absence_from_incomplete_history(self):
        with patch.object(m,'api_get',return_value=dict(total_count=2,workflow_runs=[self.run])), \
             patch.object(m,'_release_history_api',return_value=dict(total_count=0,artifacts=[])):
            with self.assertRaisesRegex(RuntimeError,'HISTORY_INCOMPLETE'):
                m.release_operation_history(self.repository,'operation',self.revision,'portal',99,1,'fixture')


    def test_revised_legacy_only_fixture_proves_complete_nonproducer_branch(self):
        _, api = ReleaseAttemptNonentryTests().history([[]])
        with patch.object(m,'api_get',side_effect=api):
            self.assertEqual(m.fast_track_execution_history(self.repository,'fixture'),[])

    def test_exact_head_filter_rechecks_shared_deadline_after_history(self):
        def history(*args,**kwargs):
            self.now=30;return [self.run]
        with patch.object(m,'fast_track_execution_history',side_effect=history):
            with self.assertRaises(m.EvidenceLookupUnavailable):self.invoke()

class RetainedRecoveryAuthorityTests(unittest.TestCase):
    revision = 'a' * 40
    marker = ('DeploymentStatusUnavailable: Azure deployment status remained unavailable for 3 consecutive reads '
              '(before any upload). No deployment or rollback write was replayed; resume by reconciling the exact revision.')
    def log(self):
        rows = ['[command]/usr/bin/git log -1 --format=%H', self.revision,
                'in fast_track_deploy', 'in reconcile', 'in read_deployments_bounded',
                self.marker, 'Process completed with exit code 1.']
        return '\n'.join('2026-10-23T10:00:01Z ' + row for row in rows)
    def proven(self, log):
        return m._fast_track_preupload_log_proven(log, self.revision,
            dict(started_at='2026-10-23T10:00:00Z', completed_at='2026-10-23T10:00:02Z'))
    def test_exact_terminal_preupload_signature(self):
        self.assertTrue(self.proven(self.log()))
    def test_wrong_revision_rejected(self):
        self.assertFalse(self.proven(self.log().replace(self.revision, 'b' * 40)))
    def test_duplicate_terminal_rejected(self):
        self.assertFalse(self.proven(self.log() + '\n2026-10-23T10:00:01Z ' + self.marker))
    def test_submission_or_provider_ack_rejected(self):
        for text in ['Submitting the verified immutable ZIP once.', 'AZURE_TRACKED_DEPLOYMENT_REFERENCE: anything']:
            with self.subTest(text=text):
                self.assertFalse(self.proven(self.log() + '\n2026-10-23T10:00:01Z ' + text))
    def test_outside_exact_failed_step_rejected(self):
        self.assertFalse(self.proven(self.log().replace('10:00:01', '11:00:01')))
    def test_missing_stack_or_exit_rejected(self):
        for text in ['in fast_track_deploy', 'in reconcile', 'in read_deployments_bounded', 'Process completed with exit code 1.']:
            with self.subTest(text=text):
                self.assertFalse(self.proven(self.log().replace(text, 'unrelated')))
    def test_new_normal_push_inventory_only_skipped_recovery(self):
        normal = dict(name='ordinary')
        recovery = dict(name=m.FAST_TRACK_RECOVERY_JOB, status='completed', conclusion='skipped', steps=[])
        source = 'jobs:\n  recover-fast-track:\n'
        self.assertEqual(m._without_skipped_recovery_job([normal, recovery], source), [normal])
        self.assertEqual(m._without_skipped_recovery_job([normal], ''), [normal])
        for bad in [[], [dict(recovery, conclusion='success')], [dict(recovery, status='in_progress')],
                    [dict(recovery, steps=[{}])], [recovery, recovery]]:
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                m._without_skipped_recovery_job([normal] + bad, source)

class RetainedRecoveryProducerTests(unittest.TestCase):
    def setUp(self):
        self.run = dict(id=20, run_attempt=1, head_sha='b'*40, path='.github/workflows/'+m.FAST_TRACK_RELEASE_WORKFLOW,
            event='workflow_dispatch', head_branch=m.FAST_TRACK_PR_BASE, head_repository=dict(full_name='owner/repo'))
        names = ['Select exact affected applications', 'Admit exact validated fast-track source',
                 'Reconcile production schema before publication', 'Publish verified website routing',
                 'Affected App Deploy — ${{ matrix.target }}', m.FAST_TRACK_RECOVERY_JOB]
        self.jobs = [dict(id=i+1, name=name, run_id=20, run_attempt=1, head_sha='b'*40,
                         status='completed', conclusion='skipped', steps=[]) for i,name in enumerate(names)]
        self.source = (ROOT/'.github/workflows'/m.FAST_TRACK_RELEASE_WORKFLOW).read_text()
    def invoke(self, *, source=None, promotions=None, promoted=True):
        with patch.object(m, '_release_history_source', return_value=self.source if source is None else source), \
             patch.object(m, 'fast_track_historical_producers', return_value=[dict(id=19,head_sha='b'*40)] if promotions is None else promotions), \
             patch.object(m, 'fast_track_promotion_parent', return_value={} if promoted else None), \
             patch.object(m, 'api_get', return_value=dict(total_count=len(self.jobs), jobs=self.jobs)):
            return m.fast_track_recovery_producer('owner/repo',self.run,'token',attempt=1)
    def test_exact_separate_producer(self):
        proof=self.invoke()
        self.assertEqual(proof,dict(runId=20,runAttempt=1,jobId=6,toolRevision='b'*40,promotionRunId=19))
        self.assertFalse(m.is_fast_track_producer('owner/repo',self.run))
    def test_wrong_event_branch_repository_and_source_rejected(self):
        for key,value in [('event','push'),('head_branch','other'),('head_repository',dict(full_name='other/repo'))]:
            previous=self.run[key];self.run[key]=value
            with self.subTest(key=key), self.assertRaises(ValueError):self.invoke()
            self.run[key]=previous
        with self.assertRaisesRegex(ValueError,'WORKFLOW_CHANGED'):self.invoke(source='changed')
    def test_missing_ambiguous_or_unadmitted_promotion_rejected(self):
        for rows in [[],[dict(id=1,head_sha='b'*40),dict(id=2,head_sha='b'*40)],[dict(id=1,head_sha='c'*40)]]:
            with self.subTest(rows=rows),self.assertRaises(ValueError):self.invoke(promotions=rows)
        with self.assertRaises(ValueError):self.invoke(promoted=False)
    def test_incomplete_inventory_or_active_competitor_rejected(self):
        self.jobs.pop()
        with self.assertRaises(ValueError):self.invoke()
        self.setUp();self.jobs[0]['status']='in_progress'
        with self.assertRaisesRegex(ValueError,'COMPETING_JOB'):self.invoke()
    def test_wrong_job_attempt_and_tool_sha_rejected(self):
        for key,value in [('run_attempt',2),('head_sha','c'*40),('run_id',21)]:
            previous=self.jobs[0][key];self.jobs[0][key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):self.invoke()
            self.jobs[0][key]=previous

class RetainedRecoveryRecordTests(unittest.TestCase):
    def setUp(self):
        self.original=dict(run=dict(id=10,run_attempt=5,head_sha='a'*40))
        self.observer=dict(runId=20,runAttempt=1,jobId=30,toolRevision='b'*40,promotionRunId=19)
        self.material=dict(packageArtifact=dict(id=40,digest='sha256:'+'c'*64))
        self.run=dict(id=20,display_title='LEGEND retained recovery original=10 application='+'a'*40)
        self.record=dict(target='website',applicationRevision='a'*40,packageDigest='d'*64,
                         producingRun=20,producingAttempt=1,packageProducerRun=10,
                         recoveryEvidence=m.fast_track_recovery_record_proof(self.original,self.material,self.observer))
        self.job=dict(id=30,steps=[dict(name='Recover exact retained application bytes',status='completed',
                    conclusion='success',started_at='2026-10-23T10:00:00Z',completed_at='2026-10-23T10:01:00Z')])
    def invoke(self):
        with patch.object(m,'fast_track_recovery_producer',return_value=self.observer), \
             patch.object(m,'fast_track_completion_original',return_value=self.original), \
             patch.object(m,'fast_track_recovery_material',return_value=self.material), \
             patch.object(m,'fast_track_recovery_package_digest',return_value='d'*64), \
             patch.object(m,'api_get',return_value=dict(total_count=1,jobs=[self.job])):
            return m.validate_fast_track_recovery_record('owner/repo',self.run,
                dict(created_at='2026-10-23T10:00:30Z'),self.record,'token')
    def test_exact_original_bytes_and_actual_tool_owner(self):
        self.assertTrue(self.invoke())
    def test_wrong_package_digest_original_producer_actual_producer(self):
        for key,value in [('packageDigest','e'*64),('packageProducerRun',11),('producingRun',21),('applicationRevision','f'*40)]:
            previous=self.record[key];self.record[key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):self.invoke()
            self.record[key]=previous
    def test_wrong_tool_artifact_or_future_original_attempt(self):
        for key,value in [('toolRevision','f'*40),('packageArtifactId',41),('packageArtifactDigest','sha256:'+'e'*64),('originalAttempt',6)]:
            previous=self.record['recoveryEvidence'][key];self.record['recoveryEvidence'][key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):self.invoke()
            self.record['recoveryEvidence'][key]=previous
    def test_wrong_dispatch_or_outside_owner_window(self):
        self.run['display_title']='arbitrary'
        with self.assertRaises(ValueError):self.invoke()
        self.setUp();self.job['steps'][0]['completed_at']='2026-10-23T10:00:01Z'
        with self.assertRaises(ValueError):self.invoke()

class RetainedRecoveryHistoryTests(unittest.TestCase):
    def invoke(self,run,step):
        with patch.object(m,'fast_track_execution_history',return_value=[run]), \
             patch.object(m,'fast_track_recovery_producer',return_value=dict(jobId=3)), \
             patch.object(m,'api_get',return_value=dict(total_count=1,jobs=[dict(id=3,steps=step)])):
            return m.recovery_history_no_unjournaled_entry('owner/repo','a'*40,20,2,'token')
    def run_fixture(self):
        return dict(id=20,event='workflow_dispatch',run_attempt=2,status='completed',
                    display_title='LEGEND retained recovery original=10 application='+'a'*40)
    def test_only_conclusively_skipped_prior_owner_allows_no_entry(self):
        self.assertIsNone(self.invoke(self.run_fixture(),[dict(name='Recover exact retained application bytes',conclusion='skipped')]))
    def test_failed_missing_active_or_unknown_prior_owner_holds(self):
        for steps in [[],[dict(name='Recover exact retained application bytes',conclusion='failure')],
                      [dict(name='Recover exact retained application bytes',conclusion=None)]]:
            with self.subTest(steps=steps),self.assertRaises(m.ReleaseOperationHistoryUnproven):self.invoke(self.run_fixture(),steps)
        run=self.run_fixture();run['status']='in_progress'
        with self.assertRaises(m.ReleaseOperationHistoryUnproven):self.invoke(run,[])
        run=self.run_fixture();run['display_title']='unknown'
        with self.assertRaises(m.ReleaseOperationHistoryUnproven):self.invoke(run,[])

class RetainedPreuploadAttestationTests(unittest.TestCase):
    def setUp(self):
        import io,zipfile
        self.run=dict(id=10,run_attempt=1,status='completed',head_sha='a'*40,event='push',
                      path='.github/workflows/'+m.FAST_TRACK_RELEASE_WORKFLOW,
                      head_branch=m.FAST_TRACK_PR_BASE,head_repository=dict(full_name='owner/repo'))
        self.step=dict(name='Publish or reconcile exact application bytes',status='completed',conclusion='failure',
                       started_at='2026-10-23T10:00:00Z',completed_at='2026-10-23T10:00:02Z')
        self.job=dict(id=11,run_id=10,run_attempt=1,head_sha='a'*40,status='completed',conclusion='failure',
                      name='Affected App Deploy — Website',steps=[self.step])
        self.package=dict(id=40,name='fast-track-package-'+'a'*40+'-website',expired=False,
                          digest='sha256:'+'e'*64,workflow_run=dict(id=10,head_sha='a'*40))
        buffer=io.BytesIO()
        with zipfile.ZipFile(buffer,'w') as archive:archive.writestr('_deployment-provenance.txt','a'*40)
        self.archive=buffer.getvalue();self.digest=hashlib.sha256(self.archive).hexdigest()
        self.operation=hashlib.sha256(json.dumps(dict(target='website',applicationRevision='a'*40,packageDigest=self.digest),sort_keys=True,separators=(',',':')).encode()).hexdigest()
    def invoke(self, *, source='reviewed-source', owner=True, extra_intent=False, complete=True, bad_checksum=False):
        rows=[self.package]+([dict(name='legend-release-operation-intent-any')] if extra_intent else [])
        def download(repo,run,name,directory,**kwargs):
            self.assertEqual((run,kwargs['artifact_id'],kwargs['expected_digest']),(10,40,'sha256:'+'e'*64))
            (directory/'deploy.zip').write_bytes(self.archive)
            (directory/'SHA256SUMS').write_text(('0'*64 if bad_checksum else self.digest)+'  deploy.zip\n')
        with patch.object(m,'FAST_TRACK_PREUPLOAD_SOURCE_GENERATIONS',({'scripts/deploy-approved-app.py':hashlib.sha256(b'reviewed-source').hexdigest()},)), \
             patch.object(m,'_release_history_source',return_value=source), \
             patch.object(m,'api_get',return_value=dict(total_count=len(rows)+(0 if complete else 1),artifacts=rows)), \
             patch.object(m,'_completion_jobs',return_value={1:[self.job]}), \
             patch.object(m,'_completion_original_artifact_owner',side_effect=None if owner else ValueError('missing owner')), \
             patch.object(m,'_release_history_json',return_value=dict(target='website')), \
             patch.object(m,'_download_run_artifact',side_effect=download), \
             patch.object(m,'_release_job_log',return_value=RetainedRecoveryAuthorityTests().log()):
            return m.attested_fast_track_preupload_failure('owner/repo',self.run,self.job,1,'website',self.operation,'token')
    def test_original_and_recovery_share_only_verified_bytes_with_fresh_guards(self):
        with m.recovery_material_reuse(),patch.object(m,'_verified_recovery_package_digest',wraps=m._verified_recovery_package_digest)as verify:
            self.assertTrue(self.invoke());self.assertTrue(self.invoke());self.assertEqual(verify.call_count,1)
            for kwargs in [dict(owner=False),dict(extra_intent=True),dict(complete=False),dict(source='changed')]:self.assertFalse(self.invoke(**kwargs))
            self.package['expired']=True;self.assertFalse(self.invoke());self.assertEqual(verify.call_count,1)
    def test_source_pinned_package_bound_positive_no_entry(self):self.assertTrue(self.invoke())
    def test_changed_source_missing_owner_intent_incomplete_inventory_or_checksum_holds(self):
        for kwargs in [dict(source='changed'),dict(owner=False),dict(extra_intent=True),dict(complete=False),dict(bad_checksum=True)]:
            with self.subTest(kwargs=kwargs):self.assertFalse(self.invoke(**kwargs))
    def test_wrong_attempt_revision_and_operation_holds(self):
        self.job['run_attempt']=2;self.assertFalse(self.invoke())
        self.setUp();self.job['head_sha']='b'*40;self.assertFalse(self.invoke())
        self.setUp();self.operation='f'*64;self.assertFalse(self.invoke())

class RecoveryTransportNonentryTests(unittest.TestCase):
    def setUp(self):
        self.revision='a'*40;self.tool='b'*40
        self.observer=dict(runId=20,runAttempt=1,jobId=30,promotionRunId=19,toolRevision=self.tool)
        self.run=dict(id=20,run_attempt=1,status='completed',conclusion='failure',head_sha=self.tool,
            event='workflow_dispatch',head_branch=m.FAST_TRACK_PR_BASE,path='.github/workflows/'+m.FAST_TRACK_RELEASE_WORKFLOW,
            head_repository=dict(full_name='owner/repo'),display_title='LEGEND retained recovery original=10 application='+self.revision)
        names=[('Set up job','success'),('Check out promoted recovery tool authority','success'),('Install Node 24','success'),
            ('Validate original authority and restore retained packages without rebuilding','success'),
            ('Install canonical durable evidence transport','success'),('Prepare canonical durable evidence transport','success'),
            ('Sign in to Azure Production','success'),('Recover exact retained application bytes','failure'),
            ('Post Sign in to Azure Production','success'),('Post Install Node 24','skipped'),
            ('Post Check out promoted recovery tool authority','success'),('Complete job','success')]
        self.job=dict(id=30,run_id=20,run_attempt=1,head_sha=self.tool,name=m.FAST_TRACK_RECOVERY_JOB,status='completed',conclusion='failure',
            started_at='2026-10-23T10:00:00Z',completed_at='2026-10-23T10:03:00Z',
            steps=[dict(name=name,status='completed',conclusion=state,started_at='2026-10-23T10:00:00Z',completed_at='2026-10-23T10:03:00Z') for name,state in names])
        self.job['steps'][3].update(started_at='2026-10-23T10:00:10Z',completed_at='2026-10-23T10:01:00Z')
        self.job['steps'][7].update(started_at='2026-10-23T10:01:10Z',completed_at='2026-10-23T10:02:50Z')
        self.original=dict(run=dict(id=10,run_attempt=5,head_sha=self.revision),targets=['portal','client','protect','parfait','website'])
        self.materials={};self.targets={}
        for n,key in enumerate(self.original['targets']):
            digest='d'*64
            identity=dict(target=key,applicationRevision=self.revision,packageDigest=digest)
            operation=hashlib.sha256(json.dumps(identity,sort_keys=True,separators=(',',':')).encode()).hexdigest()
            self.materials[key]=dict(packageArtifact=dict(id=40+n,digest='sha256:'+'e'*64),originalIntent=None if key=='website' else dict(identity,operationId=operation,packageProducerRun=10))
            self.targets[key]=dict(operationId=operation,packageDigest=digest,packageArtifactId=40+n,packageArtifactDigest='sha256:'+'e'*64)
        self.proof=dict(state='ORIGINAL_RECOVERY_MATERIALS_VERIFIED',originalRun=10,originalAttempt=5,applicationRevision=self.revision,
                        observer=self.observer,targets=self.targets)
        self.source={key:'reviewed '+key for key in m.FAST_TRACK_RECOVERY_PREINTENT_GENERATIONS[0]}
        self.generations=({key:hashlib.sha256(value.encode()).hexdigest() for key,value in self.source.items()},)
        self.inventory=dict(artifacts=[],total_count=0)
    def log(self):
        command='##[group]Run python3 scripts/deploy-approved-app.py --recover-fast-track "$ORIGINAL_RUN" --recovery-revision "$APPLICATION_REVISION" --package-root "$RUNNER_TEMP/retained-recovery"'
        rows=["Current runner version: '2.338.0'",'[command]/usr/bin/git log -1 --format=%H',self.tool,
            command+' --prepare-recovery-only',json.dumps(self.proof),command,'Traceback (most recent call last):']
        frames=[('deploy-approved-app.py','reconcile'),('release-operation-evidence.py','before_submit'),
            ('release-operation-evidence.py','before_submit'),('release-operation-evidence.py','publish'),
            ('deploy-approved-app.py','publish_phase_record'),('release-operation-evidence.py','publish_record')]
        rows += [f'  File "/home/runner/work/repo/repo/scripts/{file}", line 1, in {function}' for file,function in frames]
        rows += ['RuntimeError: Durable operation publication/readback unavailable; reconcile without writing',
            'The above exception was the direct cause of the following exception:', 'Traceback (most recent call last):',
            '  File "/home/runner/work/repo/repo/scripts/deploy-approved-app.py", line 1, in recover_target',
            'DeploymentSubmissionNotEntered: Durable upload intent could not be proven; no write authorized',
            '##[error]Process completed with exit code 1.','Cleaning up orphan processes']
        return '\n'.join(('2026-10-23T10:00:30Z ' if i < 5 else '2026-10-23T10:02:00Z ')+row for i,row in enumerate(rows))
    def parse(self,log=None):return m._recovery_preintent_log_proof(log or self.log(),self.run,self.job,self.observer)
    def invoke(self, *, log=None, producer=None, uncertain=False, changed=False, artifacts_late=False):
        reads=[]
        def api(repo,path,token):
            self.assertEqual((repo,token),('owner/repo','token'))
            self.assertEqual(path,'actions/runs/20/artifacts?per_page=100');reads.append(path)
            if uncertain:raise m.EvidenceLookupUnavailable('unavailable')
            return dict(artifacts=[dict(id=99)],total_count=1) if artifacts_late and len(reads)>1 else self.inventory
        with patch.object(m,'FAST_TRACK_RECOVERY_PREINTENT_GENERATIONS',self.generations), \
             patch.object(m,'fast_track_recovery_producer',side_effect=producer or (lambda *a,**k:self.observer)), \
             patch.object(m,'_release_history_source',side_effect=lambda r,s,p,t:self.source[p]), \
             patch.object(m,'api_get',side_effect=api),patch.object(m,'_release_job_log',return_value=log or self.log()), \
             patch.object(m,'fast_track_completion_original',return_value=self.original), \
             patch.object(m,'fast_track_recovery_material',side_effect=lambda r,o,k,t:self.materials[k]), \
             patch.object(m,'fast_track_recovery_package_digest',return_value='d'*64) as package, \
             patch.object(m,'_completion_original_unchanged',side_effect=ValueError('changed') if changed else None):
            result=m.attested_recovery_preintent_failure('owner/repo',self.run,self.job,1,self.revision,'token')
            if result:self.assertEqual(package.call_count,1)
            return result
    def test_positive_exact_source_flow_and_all_five_original_materials(self):
        self.assertEqual(self.parse(),self.proof);self.assertTrue(self.invoke())
    def test_spoofed_truncated_wrong_checkout_and_incomplete_trace_logs(self):
        log=self.log()
        variants=[log.split('\n',1)[1],log.rsplit('\n',1)[0],log.replace(self.tool,'f'*40),log+'\nnot timestamped',
            log.replace('in publish_phase_record','in unrelated'),log.replace('in recover_target','in unrelated'),
            log.replace('  File "/home/runner/work/repo/repo/scripts/deploy-approved-app.py", line 1, in reconcile', '  File "/tmp/unknown.py", line 1, in unexpected'),
            log.replace('The above exception was the direct cause of the following exception:','unrelated'),
            log.replace('##[error]Process completed with exit code 1.','unrelated'),
            log+'\n2026-10-23T10:01:00Z Submitting the verified immutable ZIP once.',
            log+'\n2026-10-23T10:01:00Z '+json.dumps(self.proof),
            log.replace('##[group]Run python3','missing-command',1),
            log.replace('2026-10-23T10:02:00Z','2026-10-23T09:00:00Z',1)]
        for value in variants:
            with self.subTest(log=value):self.assertFalse(self.invoke(log=value))
    def test_source_unknown_and_producer_unproven_never_admit(self):
        self.source['scripts/release-artifacts/transport.cjs']='unknown';self.assertFalse(self.invoke())
        self.setUp();self.assertFalse(self.invoke(producer=lambda *a,**k:(_ for _ in ()).throw(ValueError('unpromoted'))))
        self.setUp();self.observer['jobId']=31;self.assertFalse(self.invoke())
    def test_existing_uncertain_incomplete_or_late_artifact_inventory_holds(self):
        for payload in [dict(artifacts=[dict(id=90)],total_count=1),dict(artifacts=[]),dict(artifacts=[],total_count=1)]:
            self.inventory=payload
            with self.subTest(payload=payload):self.assertFalse(self.invoke())
        self.setUp();self.assertFalse(self.invoke(uncertain=True));self.assertFalse(self.invoke(artifacts_late=True))
    def test_original_material_observer_revision_and_snapshot_cannot_be_substituted(self):
        mutations=[lambda:self.proof.update(originalRun=11),lambda:self.proof.update(originalAttempt=6),
            lambda:self.proof.update(applicationRevision='f'*40),lambda:self.proof.update(observer=dict(self.observer,runId=21)),
            lambda:self.targets.pop('client'),lambda:self.targets['website'].update(packageArtifactId=999),
            lambda:self.targets['website'].update(packageDigest='f'*64),lambda:self.targets['portal'].update(operationId='f'*64)]
        for mutate in mutations:
            self.setUp();mutate()
            with self.subTest(mutation=mutate):self.assertFalse(self.invoke())
        self.setUp();self.assertFalse(self.invoke(changed=True))
    def test_active_wrong_attempt_and_incomplete_step_owner_holds(self):
        for key,value in [('status','in_progress'),('run_attempt',2),('head_sha','c'*40),('run_id',99),('conclusion','cancelled')]:
            self.setUp();self.job[key]=value
            with self.subTest(key=key):self.assertFalse(self.invoke())
        self.setUp();self.job['steps'].pop();self.assertFalse(self.invoke())
    def test_history_guard_uses_attestation_only_for_exact_failed_owner(self):
        with patch.object(m,'fast_track_execution_history',return_value=[self.run]), \
             patch.object(m,'fast_track_recovery_producer',return_value=self.observer), \
             patch.object(m,'api_get',return_value=dict(jobs=[self.job],total_count=1)), \
             patch.object(m,'attested_recovery_preintent_failure',return_value=True) as attest:
            self.assertIsNone(m.recovery_history_no_unjournaled_entry('owner/repo',self.revision,99,1,'token'))
            attest.assert_called_once_with('owner/repo',self.run,self.job,1,self.revision,'token',target=None,operation_id=None)
            attest.return_value=False
            with self.assertRaises(m.ReleaseOperationHistoryUnproven):m.recovery_history_no_unjournaled_entry('owner/repo',self.revision,99,1,'token')

class RecoveryTargetReadHistoricalTests(unittest.TestCase):
    def setUp(self):
        f = RecoveryTransportNonentryTests();f.setUp();self.f = f
        f.tool = '3460c10cebf9e0aa247604991a607eafaa047014'
        f.revision = '38756300c87073e5a52a9153046aa09ebe803024'
        f.observer.update(runId=40012751935, jobId=120617015463, toolRevision=f.tool)
        f.run.update(id=40012751935, head_sha=f.tool, display_title='LEGEND retained recovery original=39960308664 application='+f.revision)
        f.job.update(id=120617015463, run_id=f.run['id'], head_sha=f.tool)
        f.original['run'].update(id=39960308664, head_sha=f.revision);f.original['artifacts']=[]
        for key in f.targets:
            identity=dict(target=key,applicationRevision=f.revision,packageDigest='d'*64)
            operation=hashlib.sha256(json.dumps(identity,sort_keys=True,separators=(',',':')).encode()).hexdigest()
            f.targets[key]['operationId']=operation
            if f.materials[key]['originalIntent'] is not None:
                f.materials[key]['originalIntent']=dict(identity,operationId=operation,packageProducerRun=39960308664)
        f.proof.update(originalRun=39960308664,applicationRevision=f.revision)
    def log(self):
        f=self.f
        prefix=f.log().split('2026-10-23T10:02:00Z Traceback',1)[0]
        # Sanitized fixture of the authenticated failure, including Python's
        # exception cause and exact source frames; no provider/customer payload.
        first=[('deploy-approved-app.py',124,'read_deployments_bounded'),('deploy-approved-app.py',1444,'deployments'),
            ('deploy-approved-app.py',175,'deployments'),('deploy-approved-app.py',148,'deployment_read_process'),
            ('/usr/lib/python3.12/subprocess.py',1209,'communicate'),('/usr/lib/python3.12/subprocess.py',2116,'_communicate'),
            ('/usr/lib/python3.12/subprocess.py',1253,'_check_timeout')]
        second=[('deploy-approved-app.py',1706,'<module>'),('deploy-approved-app.py',1621,'main'),
            ('deploy-approved-app.py',1464,'recover_fast_track'),('deploy-approved-app.py',1566,'_recover_fast_track'),
            ('/usr/lib/python3.12/concurrent/futures/_base.py',449,'result'),('/usr/lib/python3.12/concurrent/futures/_base.py',401,'__get_result'),
            ('/usr/lib/python3.12/concurrent/futures/thread.py',58,'run'),('deploy-approved-app.py',1556,'recover_target'),
            ('deploy-approved-app.py',501,'reconcile'),('deploy-approved-app.py',138,'read_deployments_bounded')]
        def frames(items):
            return [f'  File "{path if path.startswith("/") else "/home/runner/work/masterapp/masterapp/scripts/"+path}", line {line}, in {fn}' for path,line,fn in items]
        rows=['Traceback (most recent call last):']+frames(first)+[
            "subprocess.TimeoutExpired: Command '['az', 'webapp', 'log', 'deployment', 'list', '-g', 'masterapp-rg', '-n', 'masterapp-website', '--only-show-errors', '-o', 'json']' timed out after 39 seconds",
            'The above exception was the direct cause of the following exception:', 'Traceback (most recent call last):']+frames(second)+[
            '    result = reconcile(azure, baseline=journal.baseline, journal=journal,', '    rows = read_deployments_bounded(',
            'DeploymentStatusUnavailable: Azure deployment status unavailable within the bounded read wall budget deadline (before any upload). No deployment or rollback write was replayed; reconcile the exact revision.',
            '##[error]Process completed with exit code 1.', 'Cleaning up orphan processes']
        return prefix+'\n'.join('2026-10-23T10:02:00Z '+row for row in rows)
    def invoke(self, *, log=None, target='website', operation=None, late=False, original_drift=False, sibling_conflict=False):
        f=self.f;reads=[]
        def api(repo,path,token):
            if path==f"actions/runs/{f.original['run']['id']}/artifacts?per_page=100":
                return dict(artifacts=[dict(id=999)] if original_drift else [],total_count=1 if original_drift else 0)
            self.assertEqual(path,f"actions/runs/{f.run['id']}/artifacts?per_page=100");reads.append(path)
            return dict(artifacts=[dict(id=999)],total_count=1) if late and len(reads)>1 else f.inventory
        with patch.object(m,'FAST_TRACK_RECOVERY_TARGET_READ_GENERATIONS',f.generations), \
             patch.object(m,'fast_track_recovery_producer',return_value=f.observer), \
             patch.object(m,'_release_history_source',side_effect=lambda r,s,p,t:f.source[p]), \
             patch.object(m,'api_get',side_effect=api),patch.object(m,'_release_job_log',return_value=log or self.log()), \
             patch.object(m,'fast_track_completion_original',return_value=f.original), \
             patch.object(m,'fast_track_recovery_material',side_effect=lambda r,o,k,t:f.materials[k]), \
             patch.object(m,'fast_track_completion_material',side_effect=lambda r,o,k,t:dict(intent={} if sibling_conflict else f.materials[k]['originalIntent'])), \
             patch.object(m,'fast_track_recovery_package_digest',return_value='d'*64),patch.object(m,'_completion_original_unchanged'):
            return m.attested_recovery_preintent_failure('MYLEGND/masterapp',f.run,f.job,1,f.revision,'token',
                target=target,operation_id=operation or f.targets['website']['operationId'])
    def test_exact_historical_target_and_all_material(self):self.assertTrue(self.invoke())
    def test_wrong_target_operation_and_identity(self):
        self.assertFalse(self.invoke(target='portal'));self.assertFalse(self.invoke(operation='f'*64))
        for obj,key,value in [('run','id',99),('run','run_attempt',2),('job','id',99),('run','head_sha','f'*40)]:
            self.setUp();getattr(self.f,obj)[key]=value
            with self.subTest(key=key):self.assertFalse(self.invoke())
    def test_complete_trace_command_phase_and_cleanup_required(self):
        log=self.log()
        variants=[log.split('\n',1)[1],log.rsplit('\n',1)[0],log.replace('line 501','line 502'),
            log.replace('masterapp-website','masterapp-portal'),log.replace('before any upload','after immutable upload'),
            log.replace('in recover_target','in before_write'),log.replace('39 seconds','40 seconds'),
            log.replace('The above exception was the direct cause of the following exception:','missing cause'),
            log.replace('    rows = read_deployments_bounded(','missing boundary'),
            log.replace('##[group]Run python3','missing invocation',1),
            log.replace('2026-10-23T10:02:00Z','2026-10-23T09:00:00Z',1),
            log+'\n2026-10-23T10:02:00Z Submitting the verified immutable ZIP once.',
            log+'\n2026-10-23T10:02:00Z AZURE_TRACKED_DEPLOYMENT_REFERENCE:unexpected']
        for value in variants:
            with self.subTest(log=value[-120:]):self.assertFalse(self.invoke(log=value))
    def test_uncertain_effects_and_sibling_material_never_disappear(self):
        self.assertFalse(self.invoke(late=True));self.assertFalse(self.invoke(original_drift=True));self.assertFalse(self.invoke(sibling_conflict=True))
        self.f.inventory=dict(artifacts=[dict(id=999)],total_count=1);self.assertFalse(self.invoke())
        self.setUp();self.f.materials['portal']['originalIntent']=None;self.assertFalse(self.invoke())
        self.setUp();self.f.targets['website']['packageArtifactId']=999;self.assertFalse(self.invoke())
        self.setUp();self.f.source['scripts/deploy-approved-app.py']='unknown';self.assertFalse(self.invoke())
    def test_receipt_source_closure_rejects_mixed_or_unknown_generation(self):
        paths=('scripts/deploy-approved-app.py','scripts/validation-resume.py','scripts/release-artifacts/transport.cjs')
        source={p:(Path(__file__).parent/p.removeprefix('scripts/')).read_text() for p in paths}
        with patch.object(m,'_release_history_source',side_effect=lambda r,s,p,t:source[p]):
            self.assertTrue(m._recovery_receipt_source_closure('owner/repo','a'*40,'token'))
            source[paths[1]]='unknown';self.assertFalse(m._recovery_receipt_source_closure('owner/repo','a'*40,'token'))


class RecoveryPreflightHistoricalTests(RecoveryTransportNonentryTests):
    def setUp(self):
        super().setUp()
        self.generation_patch=patch.object(m,'FAST_TRACK_RECOVERY_PREFLIGHT_GENERATIONS',self.generations)
        self.generation_patch.start();self.addCleanup(self.generation_patch.stop)
    def log(self):
        old=super().log();prefix=old.split('2026-10-23T10:02:00Z Traceback',1)[0]
        rows=['Traceback (most recent call last):']
        rows += [f'  File "/home/runner/work/repo/repo/scripts/deploy-approved-app.py", line {line}, in {name}'
                 for line,name in [(1577,'<module>'),(1492,'main'),(1350,'recover_fast_track'),(1395,'_recover_fast_track'),(1259,'recovery_preupload_baseline')]]
        rows += ['    states[key] = recovery_preupload_baseline(azure, original, key)',
                 "    raise DeploymentReconciliationRequired('RECOVERY_BASELINE_NOT_IDLE_OR_UNPROVEN')",
                 'DeploymentReconciliationRequired: RECOVERY_BASELINE_NOT_IDLE_OR_UNPROVEN',
                 '##[error]Process completed with exit code 1.','Cleaning up orphan processes']
        return prefix+'\n'.join('2026-10-23T10:02:00Z '+row for row in rows)
    def parse(self,log=None):return m._recovery_preintent_log_proof(log or self.log(),self.run,self.job,self.observer,kind='preflight')
    def test_spoofed_truncated_wrong_checkout_and_incomplete_trace_logs(self):
        log=self.log()
        variants=[log.split('\n',1)[1],log.rsplit('\n',1)[0],log.replace(self.tool,'f'*40),
                  log.replace('line 1395','line 1396'),log.replace('in _recover_fast_track','in recover_target'),
                  log.replace('states[key] = recovery_preupload_baseline(azure, original, key)','unknown'),
                  log.replace('##[group]Run python3','unknown',1),
                  log.replace('2026-10-23T10:02:00Z','2026-10-23T09:00:00Z',1),
                  log+'\n2026-10-23T10:02:00Z Submitting the verified immutable ZIP once.']
        for value in variants:
            with self.subTest(log=value):self.assertFalse(self.invoke(log=value))

class RecoveryPreflightRecordTests(unittest.TestCase):
    def setUp(self):
        path=Path(__file__).with_name('deploy-approved-app.py')
        spec=importlib.util.spec_from_file_location('preflight_producer',path)
        self.producer=importlib.util.module_from_spec(spec);spec.loader.exec_module(self.producer)
        fixture=RecoveryTransportNonentryTests();fixture.setUp();self.fixture=fixture
        self.proof=json.loads(json.dumps({k:v for k,v in fixture.proof.items() if k!='state'}))
        self.name,self.record=self.producer.recovery_preflight_nonentry_record(self.proof,'website',self.producer.RecoveryPreflightFailure('provider-read-timeout'))
    def test_actual_producer_consumer_and_source_boundary(self):
        self.assertEqual(m.validate_recovery_preflight_nonentry(self.record),self.name)
        source=Path(self.producer.__file__).read_text();journal=Path(self.producer.__file__).with_name('release-operation-evidence.py').read_text()
        self.assertTrue(m._recovery_preflight_source_contract(source,journal))
        for bad in [source.replace("RECOVERY_PREFLIGHT_NONENTRY_CONTRACT = 'retained-recovery-preflight/v1'", "RECOVERY_PREFLIGHT_NONENTRY_CONTRACT = 'unknown'"),
                    source.replace('name, record = recovery_preflight_nonentry_record(proof, key, failure)','name, record = unknown(proof, key, failure)'),
                    source.replace('states[key] = recovery_preupload_baseline(azure, original, key)','azure.submit()')]:
            self.assertFalse(m._recovery_preflight_source_contract(bad,journal))
        self.assertFalse(m._recovery_preflight_source_contract(source,journal+'unknown'))
    def test_mutation_authority_and_non_string_target_reject(self):
        for field,value in [('failedTarget',['website']),('failedTarget',{}),('failedTarget',1),('authorizesMutation',True),('writePhaseEntered',True),('phase','success'),('originalRun',20),('schemaVersion',True)]:
            bad=json.loads(json.dumps(self.record));bad[field]=value
            with self.subTest(field=field,value=value),self.assertRaises(ValueError):m.validate_recovery_preflight_nonentry(bad)
        bad=json.loads(json.dumps(self.record));bad['observation']['historyComplete']=False
        with self.assertRaises(ValueError):m.validate_recovery_preflight_nonentry(bad)

class RecoveryPreflightTerminalConsumerTests(RecoveryPreflightRecordTests):
    def invoke(self,*,late=False,wrong_source=False,producer_bad=False,artifact_change=None,incomplete=False,mixed=False,closure_bad=None):
        f=self.fixture;artifact=dict(id=99,name=self.name,expired=False,digest='sha256:'+'f'*64,
            workflow_run=dict(id=20,head_sha=f.tool),created_at='2026-10-23T10:02:00Z')
        if artifact_change:artifact.update(artifact_change)
        inventory=dict(artifacts=[artifact],total_count=2 if incomplete else 1);reads=[]
        if mixed:inventory=dict(artifacts=[artifact,dict(artifact,id=100,name='unknown-operation')],total_count=2)
        source=Path(self.producer.__file__).read_text();journal=Path(self.producer.__file__).with_name('release-operation-evidence.py').read_text()
        def api(repo,path,token):
            if path=='actions/runs/20/attempts/1/jobs?per_page=100':return dict(jobs=[f.job],total_count=1)
            self.assertEqual(path,'actions/runs/20/artifacts?per_page=100');reads.append(path)
            return dict(artifacts=[],total_count=0)if late and len(reads)>1 else inventory
        with patch.object(m,'_release_history_source',side_effect=lambda repo,sha,path,token:('unknown' if path==closure_bad else ('unknown' if wrong_source else source)if path.endswith('deploy-approved-app.py')else journal if path.endswith('release-operation-evidence.py')else (Path(__file__).parent/path.removeprefix('scripts/')).read_text())), \
             patch.object(m,'fast_track_completion_original',return_value=f.original), \
             patch.object(m,'fast_track_recovery_material',side_effect=lambda repo,o,key,t:f.materials[key]), \
             patch.object(m,'fast_track_recovery_package_digest',return_value='d'*64), \
             patch.object(m,'_release_history_json',return_value=self.record) as read, \
             patch.object(m,'fast_track_recovery_producer',return_value=dict(f.observer,jobId=999)if producer_bad else f.observer), \
             patch.object(m,'_completion_original_unchanged'),patch.object(m,'api_get',side_effect=api):
            result=m.attested_recovery_terminal_preflight('owner/repo',f.run,f.job,1,f.revision,'token')
            if result:self.assertTrue(read.call_args.kwargs['verify_digest'])
            return result
    def test_contract_rejection_receipt_authenticates_current_source_without_write_authority(self):
        self.name,self.record=self.producer.recovery_preflight_nonentry_record(
            self.proof,'website',RuntimeError('RELEASE_RECORD_CONTRACT_INVALID'))
        self.assertFalse(self.record['writePhaseEntered'])
        self.assertFalse(self.record['authorizesMutation'])
        self.assertTrue(self.invoke())
        self.assertFalse(self.invoke(wrong_source=True))
        self.assertFalse(self.invoke(closure_bad='scripts/release-artifacts/transport.cjs'))

    def test_authenticated_full_consumer_positive_and_changed_final_inventory(self):
        self.assertTrue(self.invoke());self.assertFalse(self.invoke(late=True))
    def test_source_producer_material_and_target_bindings(self):
        self.assertFalse(self.invoke(wrong_source=True));self.assertFalse(self.invoke(producer_bad=True))
        for mutate in [lambda r:r.update(originalAttempt=6),lambda r:r.update(producingAttempt=2),
                       lambda r:r['targets']['website'].update(packageArtifactId=999),lambda r:r['targets'].pop('portal'),
                       lambda r:r['observer'].update(toolRevision='f'*40),lambda r:r.update(writePhaseEntered=True)]:
            self.setUp();mutate(self.record)
            with self.subTest(mutate=mutate):self.assertFalse(self.invoke())
    def test_artifact_scope_digest_timing_and_complete_inventory(self):
        for change in [dict(expired=True),dict(workflow_run=dict(id=99,head_sha=self.fixture.tool)),
                       dict(workflow_run=dict(id=20,head_sha='f'*40)),dict(digest='unknown'),
                       dict(created_at='2026-10-23T09:00:00Z'),dict(name='unknown')]:
            with self.subTest(change=change):self.assertFalse(self.invoke(artifact_change=change))
        self.assertFalse(self.invoke(incomplete=True));self.assertFalse(self.invoke(mixed=True))
        for path in ['scripts/validation-resume.py','scripts/release-artifacts/transport.cjs']:self.assertFalse(self.invoke(closure_bad=path))
        self.fixture.job['steps'].pop()
        self.assertFalse(self.invoke())
    def test_complete_generation_rejects_writes_anywhere_and_helper_changes(self):
        source=Path(self.producer.__file__).read_text();journal=Path(self.producer.__file__).with_name('release-operation-evidence.py').read_text()
        for bad in [source.replace('# Versioned terminal receipt boundary','submit_unreviewed_provider_write()\n    # Versioned terminal receipt boundary'),
                    source.replace('        raise\n    def recover_target','        azure.submit()\n        raise\n    def recover_target'),
                    source.replace("stage='all-target-preflight'","stage='unreviewed'"),source+'\nopen("unreviewed", "w")\n']:
            self.assertFalse(m._recovery_preflight_source_contract(bad,journal))

class RecoveryPackageReuseTests(unittest.TestCase):
    def setUp(self):
        self.run=dict(id=10,head_sha='a'*40)
        self.artifact=dict(id=40,name='fast-track-package-'+'a'*40+'-website',digest='sha256:'+'e'*64,expired=False,workflow_run=dict(id=10,head_sha='a'*40))
        self.original=dict(run=self.run,targets=['website'],artifacts=[self.artifact]);self.material=dict(packageArtifact=self.artifact)
    def get(self):return m.fast_track_recovery_package_digest('owner/repo',self.original,self.material,'website','token')
    def test_verified_digest_reused_only_inside_invocation(self):
        with patch.object(m,'_verified_recovery_package_digest',return_value='d'*64)as verify:
            with m.recovery_material_reuse():self.assertEqual(self.get(),self.get());self.assertEqual(verify.call_count,1)
            with m.recovery_material_reuse():self.get()
            self.assertEqual(verify.call_count,2)
    def test_expiry_scope_and_changed_material_checked_on_every_hit(self):
        for change in [lambda:self.artifact.update(expired=True),lambda:self.artifact.update(digest='bad'),lambda:self.original.update(targets=[]),lambda:self.artifact['workflow_run'].update(id=99)]:
            self.setUp()
            with patch.object(m,'_verified_recovery_package_digest',return_value='d'*64)as verify,m.recovery_material_reuse():
                self.get();change()
                with self.assertRaises(ValueError):self.get()
                self.assertEqual(verify.call_count,1)
    def test_changed_exact_artifact_key_is_reverified(self):
        with patch.object(m,'_verified_recovery_package_digest',return_value='d'*64)as verify,m.recovery_material_reuse():
            self.get();self.artifact['id']=41;self.get();self.artifact['digest']='sha256:'+'f'*64;self.get()
            self.assertEqual(verify.call_count,3)
    def test_cache_bound_and_waiter_deadline(self):
        import concurrent.futures
        with patch.object(m,'_verified_recovery_package_digest',return_value='d'*64)as verify,m.recovery_material_reuse():
            for i in range(100):self.artifact['id']=40+i;self.get()
            self.artifact['id']=140
            with self.assertRaisesRegex(ValueError,'CACHE_BOUND'):self.get()
            self.assertEqual(verify.call_count,100)
        self.setUp()
        with m.recovery_material_reuse(),patch.object(m,'evidence_remaining',return_value=.001):
            lock,entries=m._RECOVERY_PACKAGE_DIGESTS.get();key=('owner/repo',10,'a'*40,'website',40,self.artifact['name'],self.artifact['digest'])
            future=concurrent.futures.Future();entries[key]=future
            with self.assertRaises(concurrent.futures.TimeoutError):self.get()
            self.assertFalse(future.done(),'Timed-out waiter must not cancel shared owner verification')
    def test_concurrent_single_flight_and_failure_eviction(self):
        import concurrent.futures,contextvars,threading,time
        for fail in [False,True]:
            entered=threading.Event();release=threading.Event()
            def verify(*args):
                entered.set();release.wait(2)
                if fail:raise ValueError('corrupt immutable bytes')
                return 'd'*64
            with patch.object(m,'_verified_recovery_package_digest',side_effect=verify)as verify_call,m.recovery_material_reuse(),concurrent.futures.ThreadPoolExecutor(max_workers=2)as pool:
                a=pool.submit(contextvars.copy_context().run,self.get);self.assertTrue(entered.wait(2))
                b=pool.submit(contextvars.copy_context().run,self.get);time.sleep(.02);release.set()
                for future in [a,b]:
                    if fail:
                        with self.assertRaises(ValueError):future.result()
                    else:self.assertEqual(future.result(),'d'*64)
                self.assertEqual(verify_call.call_count,1)
                if fail:
                    with self.assertRaises(ValueError):self.get()
                    self.assertEqual(verify_call.call_count,2)

class RecoveryTargetHoldTests(unittest.TestCase):
    def setUp(self):
        fixture=RecoveryPreflightRecordTests();fixture.setUp();self.producer=fixture.producer;self.fixture=fixture.fixture;self.proof=fixture.proof;self.fixture.original['artifacts']=[]
        self.name,self.record=self.producer.recovery_target_preintent_record(self.proof,'website',self.producer.RecoveryPhaseDeadline('cutoff'))
    def invoke(self,*,late=False,wrong_source=False,producer_bad=False,artifact_change=None,incomplete=False,mixed=False,closure_bad=None,missing_sibling=False,conflict=False,extra_record=None,original_changed=False,effect_records=None):
        f=self.fixture;artifact=dict(id=99,name=self.name,expired=False,digest='sha256:'+'f'*64,workflow_run=dict(id=20,head_sha=f.tool),created_at='2026-10-23T10:02:00Z')
        if artifact_change:artifact.update(artifact_change)
        inventory=dict(artifacts=[artifact],total_count=2 if incomplete else 1);reads=[]
        if mixed:inventory=dict(artifacts=[artifact,dict(artifact,id=100,name='unknown-operation')],total_count=2)
        if extra_record:inventory=dict(artifacts=[artifact,dict(artifact,id=100,name=extra_record[0])],total_count=2)
        extra_by_id={}
        if effect_records:
            for n,(name,record) in enumerate(effect_records,200):
                inventory['artifacts'].append(dict(artifact,id=n,name=name));extra_by_id[n]=record
            inventory['total_count']=len(inventory['artifacts'])
        if missing_sibling:f.materials['portal']['originalIntent']=None
        if conflict:f.materials['website']['originalIntent']=dict(target='website',applicationRevision=f.revision,packageDigest='d'*64,operationId=f.targets['website']['operationId'],packageProducerRun=10)
        def api(repo,path,token):
            if path=='actions/runs/10/artifacts?per_page=100':return dict(artifacts=[dict(id=999)]if original_changed else f.original['artifacts'],total_count=1 if original_changed else len(f.original['artifacts']))
            if path.startswith('actions/runs/20/attempts/'):
                i=int(path.split('/')[4]);return dict(jobs=[dict(f.job,run_attempt=i,id=29+i)],total_count=1)
            self.assertEqual(path,'actions/runs/20/artifacts?per_page=100');reads.append(path)
            return dict(artifacts=[],total_count=0)if late and len(reads)>1 else inventory
        def source(repo,sha,path,token):return 'unknown'if wrong_source or path==closure_bad else(Path(__file__).parent/path.removeprefix('scripts/')).read_text()
        with patch.object(m,'_release_history_source',side_effect=source),patch.object(m,'fast_track_completion_original',return_value=f.original), \
             patch.object(m,'fast_track_recovery_material',side_effect=lambda repo,o,key,t:f.materials[key]), \
             patch.object(m,'fast_track_completion_material',side_effect=lambda repo,o,key,t:dict(intent=f.materials[key]['originalIntent'])), \
             patch.object(m,'fast_track_recovery_package_digest',return_value='d'*64), \
             patch.object(m,'validate_fast_track_recovery_record',return_value=True),patch.object(m,'_release_history_json',side_effect=lambda repo,run,a,*args,**kw:extra_by_id[a['id']]if a['id']in extra_by_id else extra_record[1]if extra_record and a['id']==100 else self.record), \
             patch.object(m,'fast_track_recovery_producer',side_effect=lambda repo,run,t,attempt:dict(f.observer,runAttempt=attempt,jobId=999 if producer_bad else 29+attempt)), \
             patch.object(m,'_completion_original_unchanged'),patch.object(m,'api_get',side_effect=api):
            return m.attested_recovery_target_hold('owner/repo',f.run,f.job,1,f.revision,'token','website',f.targets['website']['operationId'])
    def test_actual_target_schema_and_full_consumer(self):
        self.assertEqual(m.validate_recovery_target_preintent(self.record),self.name);self.assertNotIn('writePhaseEntered',self.record)
        self.assertTrue(self.invoke());self.assertFalse(self.invoke(late=True))
    def test_target_scope_flags_names_and_unknown_data(self):
        for field,value in [('targetWritePhaseEntered',True),('writePhaseEntered',False),('phase','preflight-nonentry'),('stage','all-target-preflight'),('failedTarget',['website']),('authorizesMutation',True)]:
            bad=json.loads(json.dumps(self.record));bad[field]=value
            with self.subTest(field=field),self.assertRaises(ValueError):m.validate_recovery_target_preintent(bad)
    def test_all_sibling_coverage_and_conflicting_intent_fail_closed(self):
        self.assertFalse(self.invoke(missing_sibling=True));self.setUp();self.assertFalse(self.invoke(conflict=True))
        self.setUp();self.fixture.run['run_attempt']=2;self.assertFalse(self.invoke())
        self.setUp();other=json.loads(json.dumps(self.record));self.assertFalse(self.invoke(extra_record=(self.name,other)))
    def test_two_attempt_holds_bind_each_attempt_without_erasing_prior_effects(self):
        self.fixture.run['run_attempt']=2
        proof=json.loads(json.dumps(self.proof));proof['observer'].update(runAttempt=2,jobId=31)
        name,record=self.producer.recovery_target_preintent_record(proof,'website',self.producer.RecoveryPhaseDeadline('cutoff'))
        self.assertTrue(self.invoke(extra_record=(name,record)))
        record['targetWritePhaseEntered']=True;self.assertFalse(self.invoke(extra_record=(name,record)))
    def test_authenticated_sibling_intent_stays_entered_across_attempts(self):
        f=self.fixture;f.run['run_attempt']=2
        proof=json.loads(json.dumps(self.proof));proof['observer'].update(runAttempt=2,jobId=31)
        held=self.producer.recovery_target_preintent_record(proof,'website',self.producer.RecoveryPhaseDeadline('cutoff'))
        # Receipt provenance validator has its own real-source tests; this seam
        # isolates sibling/attempt accounting after independently valid evidence.
        intent=dict(schemaVersion=1,target='portal',phase='intent',operationId=f.targets['portal']['operationId'],packageDigest='d'*64,applicationRevision=f.revision,packageProducerRun=self.proof['originalRun'],producingRun=20,producingAttempt=1,baseline='c'*40,baselineDeploymentIds=[],recoveryEvidence={},submissionContract=dict(kind='azure_onedeploy_windows_intent_v1',value=f.targets['portal']['operationId']))
        row=('legend-release-operation-intent-'+intent['operationId'],intent)
        self.assertTrue(self.invoke(missing_sibling=True,extra_record=held,effect_records=[row]))
        submitted=('legend-release-operation-submitted-'+intent['operationId'],dict(intent,phase='submitted',producingAttempt=2,deploymentId='new-provider'))
        success=('legend-release-operation-success-'+intent['operationId'],dict(intent,phase='success',producingAttempt=2,deploymentIds=['new-provider']))
        self.assertTrue(self.invoke(missing_sibling=True,extra_record=held,effect_records=[row,submitted,success]))
        self.assertFalse(self.invoke(missing_sibling=True,extra_record=held,effect_records=[submitted,success]))
        badsuccess=(success[0],dict(success[1],deploymentIds=['conflicting-provider']))
        self.assertFalse(self.invoke(missing_sibling=True,extra_record=held,effect_records=[row,submitted,badsuccess]))
        for field in ['schemaVersion','baseline','baselineDeploymentIds','submissionContract']:
            bad=dict(intent);bad.pop(field)
            with self.subTest(missing=field):self.assertFalse(self.invoke(missing_sibling=True,extra_record=held,effect_records=[(row[0],bad)]))
        for changes in [dict(extra='unreviewed'),dict(baseline='invalid'),dict(baselineDeploymentIds=['bad id']),dict(deploymentIds=['phase-wrong']),dict(submissionContract={'kind':'unknown','value':intent['operationId']})]:
            with self.subTest(changes=changes):self.assertFalse(self.invoke(missing_sibling=True,extra_record=held,effect_records=[(row[0],dict(intent,**changes))]))
        for changes in [dict(deploymentId=''),dict(deploymentId='old',baselineDeploymentIds=['old']),dict(providerRequestCorrelation={'kind':'azure_onedeploy_deployment_tag_v1','value':intent['operationId']})]:
            self.assertFalse(self.invoke(missing_sibling=True,extra_record=held,effect_records=[row,(submitted[0],dict(submitted[1],**changes))]))
        duplicate=(row[0],dict(intent,producingAttempt=2))
        self.assertFalse(self.invoke(missing_sibling=True,extra_record=held,effect_records=[row,duplicate]))
        later_hold=self.producer.recovery_target_preintent_record(proof,'portal',self.producer.RecoveryPhaseDeadline('cutoff'))
        self.assertFalse(self.invoke(missing_sibling=True,extra_record=held,effect_records=[row,later_hold]))
        wrong=(row[0],dict(intent,operationId='f'*64))
        self.assertFalse(self.invoke(missing_sibling=True,extra_record=held,effect_records=[wrong]))
    def test_missing_owner_cleanup_and_late_original_inventory_hold(self):
        self.fixture.job['steps'].pop();self.assertFalse(self.invoke());self.setUp();self.assertFalse(self.invoke(original_changed=True))
    def test_source_producer_inventory_and_material_negatives(self):
        self.assertFalse(self.invoke(wrong_source=True));self.assertFalse(self.invoke(producer_bad=True));self.assertFalse(self.invoke(mixed=True));self.assertFalse(self.invoke(incomplete=True))
        for path in ['scripts/deploy-approved-app.py','scripts/release-operation-evidence.py','scripts/validation-resume.py','scripts/release-artifacts/transport.cjs']:self.assertFalse(self.invoke(closure_bad=path))
        for change in [dict(expired=True),dict(digest='bad'),dict(created_at='2026-10-23T09:00:00Z'),dict(workflow_run=dict(id=99,head_sha=self.fixture.tool)),dict(name='unknown')]:self.assertFalse(self.invoke(artifact_change=change))
        for mutate in [lambda r:r['targets'].pop('portal'),lambda r:r['targets']['website'].update(packageArtifactId=999),lambda r:r.update(producingAttempt=2),lambda r:r['observer'].update(toolRevision='f'*40)]:
            self.setUp();mutate(self.record);self.assertFalse(self.invoke())

class RecoveryReceiptReuseTests(unittest.TestCase):
    def setUp(self):
        self.artifact=dict(id=40,name='receipt',digest='sha256:'+'e'*64,expired=False,workflow_run=dict(id=10))
        self.raw=b'{"items":[1]}'
    def download(self, repository, run, name, directory, **kw):
        self.assertEqual(kw,dict(artifact_id=self.artifact['id'],expected_digest=self.artifact['digest']))
        (directory/'operation.json').write_bytes(self.raw)
        (directory/'validation-resume.json').write_bytes(self.raw)
    def get(self, repository='owner/repo', run=10, filename='operation.json', size=32768):
        return m._release_history_json(repository,run,self.artifact,filename,verify_digest=True,max_bytes=size)
    def test_reuse_independent_values_and_invocation_exit(self):
        with patch.object(m,'_download_run_artifact',side_effect=self.download) as download:
            with m.recovery_material_reuse():
                self.get()['items'].append(9)
                self.assertEqual(self.get(),dict(items=[1]));self.assertEqual(download.call_count,1)
            with m.recovery_material_reuse():self.get()
            self.assertEqual(download.call_count,2)
        self.assertIsNone(m._RECOVERY_RECEIPT_BYTES.get())
    def test_every_identity_dimension_and_size_policy_is_distinct(self):
        with patch.object(m,'_download_run_artifact',side_effect=self.download) as download,m.recovery_material_reuse():
            self.get();self.get(repository='other/repo')
            self.artifact['workflow_run']['id']=11;self.get(run=11)
            self.artifact['workflow_run']['id']=10;self.artifact['id']=41;self.get()
            self.artifact['digest']='sha256:'+'f'*64;self.get()
            self.artifact['name']='other-receipt';self.get()
            self.get(filename='validation-resume.json');self.get(size=131072)
            self.assertEqual(download.call_count,8)
    def test_invalid_metadata_fails_after_prior_hit(self):
        for change in [dict(expired=True),dict(expired=None),dict(id=True),dict(digest='bad'),dict(name=''),dict(workflow_run=dict(id=99))]:
            self.setUp()
            with patch.object(m,'_download_run_artifact',side_effect=self.download) as download,m.recovery_material_reuse():
                self.get();self.artifact.update(change)
                with self.assertRaises(m.ReleaseOperationHistoryUnproven):self.get()
                self.assertEqual(download.call_count,1)
    def test_raw_byte_limit_and_failure_eviction(self):
        self.raw=b'{"items":[]}' + b' '*32768
        with patch.object(m,'_download_run_artifact',side_effect=self.download) as download,m.recovery_material_reuse():
            self.get(size=131072)
            for _ in range(2):
                with self.assertRaisesRegex(RuntimeError,'oversized'):self.get()
            self.assertEqual(download.call_count,3)
        self.raw=b'not json'
        with patch.object(m,'_download_run_artifact',side_effect=self.download) as download,m.recovery_material_reuse():
            with self.assertRaises(ValueError):self.get()
            self.raw=b'{"items":[1]}';self.get();self.get()
            self.assertEqual(download.call_count,2)
        with patch.object(m,'_download_run_artifact',side_effect=RuntimeError('digest mismatch')) as download,m.recovery_material_reuse():
            for _ in range(2):
                with self.assertRaisesRegex(RuntimeError,'digest mismatch'):self.get()
            self.assertEqual(download.call_count,2)
    def test_concurrent_single_flight_and_waiter_deadline(self):
        import concurrent.futures,contextvars,threading
        entered=threading.Event();release=threading.Event()
        def download(*args,**kw):
            entered.set();release.wait(2);self.download(*args,**kw)
        with patch.object(m,'_download_run_artifact',side_effect=download) as downloads,m.recovery_material_reuse(),concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            first=pool.submit(contextvars.copy_context().run,self.get);self.assertTrue(entered.wait(2))
            with patch.object(m,'evidence_remaining',return_value=.001):
                with self.assertRaises(concurrent.futures.TimeoutError):self.get()
            second=pool.submit(contextvars.copy_context().run,self.get);release.set()
            self.assertEqual(first.result(),second.result());self.assertEqual(downloads.call_count,1)
    def test_fresh_source_inventory_and_scope_are_not_cached(self):
        run=dict(id=10,run_attempt=1,event='pull_request');workflow='approved-release-security-validation.yml'
        self.artifact['name']='validation-resume-security-10-1'
        receipt=dict(workflow=workflow,currentSha='a'*40,validationBase=m.FAST_TRACK_PR_BASE,
            sourceValidationScope=dict(phase='source-validation-only',releaseAdmission=False,
                baseBranch=m.FAST_TRACK_PR_BASE,candidate='a'*40,pullRequest=679))
        self.raw=json.dumps(receipt).encode();inventory=dict(total_count=1,artifacts=[self.artifact])
        with patch.object(m,'api_get',return_value=inventory) as api,patch.object(m,'_download_run_artifact',side_effect=self.download) as download,m.recovery_material_reuse():
            def get(candidate='a'*40):return m.fast_track_validation_receipt('owner/repo',run,workflow,candidate,679,'token')
            self.assertEqual(get(),40);self.assertEqual(get(),40)
            with self.assertRaisesRegex(RuntimeError,'SOURCE_SCOPE'):get('b'*40)
            inventory['total_count']=2
            with self.assertRaisesRegex(RuntimeError,'INVENTORY_INCOMPLETE'):get()
            self.assertEqual(api.call_count,4);self.assertEqual(download.call_count,1)
    def test_fresh_operation_inventory_and_producer_are_not_cached(self):
        operation='b'*64;revision='a'*40;self.artifact['name']='legend-release-operation-intent-'+operation
        self.raw=json.dumps(dict(operationId=operation,target='website',applicationRevision=revision,
            producingRun=10,producingAttempt=1,phase='intent')).encode()
        inventory=dict(total_count=1,artifacts=[self.artifact]);run=dict(id=10,head_sha=revision,run_attempt=1)
        def api(repository,path,token):return inventory if path.startswith('actions/artifacts?') else run
        with patch.object(m,'_release_history_api',side_effect=api) as reads,patch.object(m,'is_fast_track_producer',return_value=True),patch.object(m,'_download_run_artifact',side_effect=self.download) as downloads,m.recovery_material_reuse():
            def get():return m.release_operation_history('owner/repo',operation,revision,'website',20,1,'token')
            self.assertEqual(get(),get());self.assertEqual(downloads.call_count,1)
            run['run_attempt']=0
            with self.assertRaisesRegex(RuntimeError,'unbound revision or attempt'):get()
            run['run_attempt']=1;inventory['total_count']=2
            with self.assertRaises(m.ReleaseOperationHistoryUnproven):get()
            self.assertEqual(reads.call_count,7)
    def test_cache_bound(self):
        with patch.object(m,'_download_run_artifact',side_effect=self.download) as downloads,m.recovery_material_reuse():
            for i in range(100):self.artifact['id']=40+i;self.get()
            self.artifact['id']=140
            with self.assertRaisesRegex(ValueError,'CACHE_BOUND'):self.get()
            self.assertEqual(downloads.call_count,100)

class RecoveryAdmissionProofTests(unittest.TestCase):
    def test_promotion_receipt_download_reuse_preserves_exact_acceptance(self):
        import contextlib
        run=dict(id=10,run_attempt=1,head_sha='a'*40,status='completed',conclusion='success',event='push',
            path='.github/workflows/'+m.FAST_TRACK_RELEASE_WORKFLOW,head_branch=m.FAST_TRACK_PR_BASE,head_repository=dict(full_name='owner/repo'))
        artifact=dict(id=40,name='fast-track-admission-10-1',expired=False,digest='sha256:'+'d'*64,workflow_run=dict(id=10))
        names=['Select exact affected applications','Admit exact validated fast-track source','Reconcile production schema before publication',
            'Publish verified website routing','Affected App Deploy — ${{ matrix.target }}']
        jobs=[dict(id=i+1,run_id=10,run_attempt=1,head_sha='a'*40,name=name,status='completed',
            conclusion='success' if i==0 else 'skipped',steps=[]) for i,name in enumerate(names)]
        expected=dict(authorizesMutation=False,productionEligible=False)
        policy=SimpleNamespace(tool_promotion_requested=lambda rev:True,read_request=lambda rev:dict(trustedToolPromotion=dict(baseRevision='b'*40)),
            trusted_tool_promotion=lambda *args:expected)
        def api(repo,path,token):
            if path=='actions/runs/10':return run
            return dict(total_count=1,artifacts=[artifact]) if '/artifacts?' in path else dict(total_count=len(jobs),jobs=jobs)
        def download(repo,ident,name,directory,**kw):
            if m._RECOVERY_RECEIPT_BYTES.get() is not None:self.assertEqual(kw['expected_digest'],artifact['digest'])
            (directory/'fast-track-admission.json').write_text(json.dumps(dict(authorizesMutation=False,productionEligible=False,producingRun=10,producingAttempt=1)))
        with patch.object(m,'_fast_track_policy',return_value=policy),patch.object(m,'fast_track_merged_source_proof',return_value={}), \
             patch.object(m,'_release_history_source',return_value='jobs: {}'),patch.object(m,'_api_get_uncached',side_effect=api), \
             patch.object(m,'_download_run_artifact',side_effect=download) as downloads:
            for _ in range(20):self.assertEqual(m.fast_track_promotion_parent('owner/repo',run,'fixture'),'b'*40)
            self.assertEqual(downloads.call_count,20);downloads.reset_mock()
            with m.recovery_material_reuse():
                with m.recovery_admission_proof():
                    for _ in range(20):self.assertEqual(m.fast_track_promotion_parent('owner/repo',run,'fixture'),'b'*40)
                self.assertEqual(downloads.call_count,1)
                expected['authorizesMutation']=True
                with self.assertRaisesRegex(ValueError,'RECEIPT_IDENTITY_CHANGED'):
                    m.fast_track_promotion_parent('owner/repo',run,'fixture')
                artifact['expired']=True
                with self.assertRaisesRegex(ValueError,'RECEIPT_UNPROVEN'):
                    m.fast_track_promotion_parent('owner/repo',run,'fixture')

    def test_nested_original_authority_chain_measures_five_attempt_reuse(self):
        import contextlib
        repo='owner/repo';revision='a'*40;candidate='b'*40;base='c'*40
        targets=list(m.RELEASE_TARGETS)
        run=dict(id=10,run_attempt=5,head_sha=revision,status='completed',conclusion='failure',
            event='push',path='.github/workflows/'+m.FAST_TRACK_RELEASE_WORKFLOW,
            head_branch=m.FAST_TRACK_PR_BASE,head_repository=dict(full_name=repo))
        rows={ 'actions/runs/10':run,
            f'commits/{revision}/pulls?per_page=100':[dict(number=7,merged_at='now',merge_commit_sha=revision,
                base=dict(ref=m.FAST_TRACK_PR_BASE),head=dict(sha=candidate,ref='candidate',repo=dict(full_name=repo)))] }
        workflows=['approved-release-security-validation.yml','masterapp-platform-architecture-validation.yml','parfait-isolated-validation.yml']
        prefixes=['security','architecture','parfait'];validation=[];receipts={}
        for ident,workflow,prefix in zip(range(101,104),workflows,prefixes):
            vr=dict(id=ident,run_attempt=1,head_sha=candidate,status='completed',conclusion='success',
                event='pull_request',path='.github/workflows/'+workflow,head_branch='candidate',head_repository=dict(full_name=repo))
            validation.append(vr);rows[f'actions/runs/{ident}']=vr
            artifact=dict(id=ident+100,name=f'validation-resume-{prefix}-{ident}-1',expired=False,digest='sha256:'+'d'*64,workflow_run=dict(id=ident))
            rows[f'actions/runs/{ident}/artifacts?per_page=100']=dict(total_count=1,artifacts=[artifact])
            receipts[ident]=dict(workflow=workflow,currentSha=candidate,validationBase=m.FAST_TRACK_PR_BASE,
                sourceValidationScope=dict(phase='source-validation-only',releaseAdmission=False,baseBranch=m.FAST_TRACK_PR_BASE,candidate=candidate,pullRequest=7))
        rows[f'actions/runs?head_sha={candidate}&per_page=100']=dict(total_count=3,workflow_runs=validation)
        specs=[('Select exact affected applications','Derive application impact exclusively from changed paths'),
            ('Admit exact validated fast-track source','Authenticate merged source and preserved required validation'),
            ('Reconcile production schema before publication',None),('Publish verified website routing',None)]
        specs += [('Affected App Deploy — '+m.fast_track_target_label(k),None) for k in targets]
        for attempt in range(1,6):
            jobs=[dict(id=attempt*1000+i+1,run_id=10,run_attempt=attempt,head_sha=revision,name=name,status='completed',
                conclusion='success' if step else 'skipped',steps=[dict(name=step,status='completed',conclusion='success')] if step else []) for i,(name,step) in enumerate(specs)]
            rows[f'actions/runs/10/attempts/{attempt}/jobs?per_page=100']=dict(total_count=len(jobs),jobs=jobs)
        rows['actions/runs/10/artifacts?per_page=100']=dict(total_count=0,artifacts=[])
        request=dict(releaseMode='approved-only',requestedBy='Founder',preserveLiveTargets=False,
            targets=[m.RELEASE_TARGETS[k]['releaseName'] for k in targets],cloudflareWebsiteRouting=False)
        calls=[];downloads=[]
        def transport(repo,path,token):calls.append(path);return json.loads(json.dumps(rows[path]))
        def download(repo,ident,name,directory,**kwargs):
            downloads.append(ident);(directory/'validation-resume.json').write_text(json.dumps(receipts[ident]))
        def source(repo,rev,path,token):return json.dumps(request) if path==m.RELEASE_REQUEST_PATH else 'jobs: {}'
        def git(args,**kwargs):return (revision+' '+base+' '+candidate) if args[1]=='rev-list' else m.RELEASE_REQUEST_PATH+'\n'
        def exercise(reuse):
            calls.clear();downloads.clear()
            with contextlib.ExitStack() as stack:
                stack.enter_context(patch.object(m,'_api_get_uncached',side_effect=transport))
                stack.enter_context(patch.object(m,'_download_run_artifact',side_effect=download))
                stack.enter_context(patch.object(m,'_release_history_source',side_effect=source))
                stack.enter_context(patch.object(m,'fast_track_historical_producers',return_value=[run]))
                stack.enter_context(patch.object(m,'fast_track_effective_baseline',return_value=base))
                stack.enter_context(patch.object(m,'fast_track_scope',return_value=dict(matrix=dict(include=[dict(key=k) for k in targets]),routing=False,commerce=False)))
                stack.enter_context(patch.object(m.subprocess,'check_output',side_effect=git))
                stack.enter_context(patch.object(m.subprocess,'run',return_value=SimpleNamespace(returncode=0)))
                stack.enter_context(m.recovery_material_reuse())
                if reuse:stack.enter_context(m.recovery_admission_proof())
                # Repeated original-authority reconstruction reached by four
                # inherited sibling consumers across five historical attempts.
                for _ in range(5):
                    for sibling in targets[:4]:
                        result=m.fast_track_completion_original(repo,10,revision,'fixture')
                        self.assertEqual(len(result['attempts']),5)
                        self.assertIn(sibling,result['targets'])
            return len(calls),len(set(calls)),len(downloads)
        baseline=exercise(False);reused=exercise(True)
        self.assertLess(reused[0],baseline[0]//3)
        self.assertLessEqual(reused[0],reused[1]*2)
        self.assertTrue(all(calls.count(path)<=2 for path in set(calls)))
        self.assertEqual(baseline[2],3);self.assertEqual(reused[2],3)
        print('RECOVERY_NESTED_ORIGINAL_CALL_COUNTS:'+json.dumps(dict(baseline=baseline,reused=reused)))

    def setUp(self):
        self.run = dict(id=10, status='completed', run_attempt=5, head_sha='a'*40)
        self.job = dict(id=30, run_id=10, run_attempt=1, head_sha='a'*40,
            status='completed', conclusion='success', started_at='start', completed_at='end')
        self.data = {'actions/runs/10': self.run, 'actions/jobs/30': self.job,
            'actions/runs/10/artifacts?per_page=100': dict(total_count=1, artifacts=[
                dict(id=40, expired=False, workflow_run=dict(id=10))]),
            'actions/runs/10/attempts/1/jobs?per_page=100': dict(total_count=1, jobs=[self.job])}
        self.calls = []
    def transport(self, repository, path, token):
        self.calls.append(path)
        return json.loads(json.dumps(self.data[path]))
    def read(self, path='actions/runs/10'):
        return m.api_get('owner/repo', path, 'fixture')
    def test_nested_views_close_with_raw_readback_and_do_not_leak(self):
        with patch.object(m, '_api_get_uncached', side_effect=self.transport):
            with m.recovery_admission_proof():
                self.read()['run_attempt'] = 999
                with m.recovery_admission_proof():
                    self.assertEqual(self.read()['run_attempt'], 5)
                self.assertEqual(len(self.calls), 1)
            self.assertEqual(len(self.calls), 2)
            with m.recovery_admission_proof(): self.read()
            self.assertEqual(len(self.calls), 3)
        self.assertIsNone(m._RECOVERY_PROOF_VIEW.get())
    def test_changed_attempt_inventory_expiry_and_job_identity_stop_at_exit(self):
        for path, change in [('actions/runs/10', lambda x:x.update(run_attempt=6)),
            ('actions/runs/10/artifacts?per_page=100', lambda x:x['artifacts'][0].update(expired=True)),
            ('actions/runs/10/artifacts?per_page=100', lambda x:x.update(total_count=2)),
            ('actions/runs/10/attempts/1/jobs?per_page=100', lambda x:x['jobs'][0].update(head_sha='b'*40))]:
            self.setUp()
            with patch.object(m, '_api_get_uncached', side_effect=self.transport):
                with self.assertRaisesRegex(m.ReleaseOperationHistoryUnproven, 'CHANGED_AT_BOUNDARY'):
                    with m.recovery_admission_proof():
                        self.read(path); self.read(path); change(self.data[path])
            self.assertIsNone(m._RECOVERY_PROOF_VIEW.get())
    def test_active_incomplete_current_owner_and_unknown_reads_never_reused(self):
        cases = [dict(status='in_progress'), dict(id=99)]
        for change in cases:
            self.setUp(); self.run.update(change)
            with patch.dict(os.environ, GITHUB_RUN_ID='99'), patch.object(m, '_api_get_uncached', side_effect=self.transport), m.recovery_admission_proof():
                self.read(); self.read(); self.assertEqual(len(self.calls), 2)
        self.setUp(); path='actions/runs/10/attempts/1/jobs?per_page=100'; self.data[path]['total_count']=2
        with patch.object(m, '_api_get_uncached', side_effect=self.transport), m.recovery_admission_proof():
            self.read(path); self.read(path); self.assertEqual(len(self.calls), 2)
        self.setUp(); path='git/ref/heads/'+m.FAST_TRACK_PR_BASE; self.data[path]=dict(object=dict(sha='a'*40))
        with patch.object(m, '_api_get_uncached', side_effect=self.transport), m.recovery_admission_proof():
            self.read(path); self.read(path); self.assertEqual(len(self.calls), 2)
    def test_global_history_cache_cannot_satisfy_boundary_or_hide_changes(self):
        key=('owner/repo','actions/runs/10'); old=m._RELEASE_HISTORY_API.get(key)
        m._RELEASE_HISTORY_API[key]=dict(self.run,run_attempt=999)
        try:
            with patch.object(m,'_api_get_uncached',side_effect=self.transport),m.recovery_admission_proof():
                self.assertEqual(m._release_history_api(*key,'fixture')['run_attempt'],5)
                self.assertEqual(m._release_history_api(*key,'fixture')['run_attempt'],5)
            self.assertEqual(len(self.calls),2)
        finally:
            if old is None:m._RELEASE_HISTORY_API.pop(key,None)
            else:m._RELEASE_HISTORY_API[key]=old
    def test_failure_discards_context_without_reusing_partial_result(self):
        with patch.object(m,'_api_get_uncached',side_effect=self.transport):
            with self.assertRaisesRegex(ValueError,'stop'):
                with m.recovery_admission_proof():self.read();raise ValueError('stop')
            with m.recovery_admission_proof():self.read()
        self.assertEqual(len(self.calls),2)
    def test_artifact_owner_and_storage_limits_are_enforced(self):
        path='actions/runs/10/artifacts?per_page=100'
        self.run['status']='in_progress'
        with patch.object(m,'_api_get_uncached',side_effect=self.transport),m.recovery_admission_proof():
            self.read(path);self.read(path)
            self.assertEqual(self.calls.count(path),2)
        self.setUp()
        with patch.object(m,'_api_get_uncached',side_effect=self.transport):
            with self.assertRaisesRegex(m.ReleaseOperationHistoryUnproven,'STORAGE_BOUND'):
                with m.recovery_admission_proof():
                    m._RECOVERY_PROOF_VIEW.get()['bytes']=32*1024*1024
                    self.read()
        self.assertIsNone(m._RECOVERY_PROOF_VIEW.get())
    def test_log_bytes_singleflight_fresh_owner_failure_eviction_and_bounds(self):
        import concurrent.futures,contextvars,threading
        entered=threading.Event();release=threading.Event()
        def download(*args):entered.set();release.wait(2);return 'complete log'
        def get():
            with m.recovery_admission_proof():return m._release_job_log('owner/repo',30,'fixture')
        with patch.object(m,'_api_get_uncached',side_effect=self.transport),patch.object(m,'_release_job_log_uncached',side_effect=download) as reads,m.recovery_material_reuse(),concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            first=pool.submit(contextvars.copy_context().run,get);self.assertTrue(entered.wait(2))
            second=pool.submit(contextvars.copy_context().run,get);release.set()
            self.assertEqual(first.result(),second.result());self.assertEqual(reads.call_count,1)
            self.job['head_sha']='b'*40
            with self.assertRaisesRegex(m.ReleaseOperationHistoryUnproven,'TERMINAL_IDENTITY'):get()
        self.setUp()
        with patch.object(m,'_api_get_uncached',side_effect=self.transport),patch.object(m,'_release_job_log_uncached',side_effect=[ValueError('failed'),'ok','new']) as reads,m.recovery_material_reuse():
            with self.assertRaises(ValueError):get()
            self.assertEqual(get(),'ok');self.assertEqual(get(),'ok');self.assertEqual(reads.call_count,2)
            self.job['completed_at']='new-end';m._RECOVERY_LOG_BYTES.get()['bytes']=64*1024*1024
            with self.assertRaisesRegex(m.ReleaseOperationHistoryUnproven,'STORAGE_BOUND'):get()


class RecoveryCompleteHistoryTests(unittest.TestCase):
    def setUp(self):
        self.repository='owner/repo';self.revision='b'*40;self.application='a'*40
        self.workflow=(ROOT/'.github/workflows'/m.FAST_TRACK_RELEASE_WORKFLOW).read_text()
        self.run=dict(id=20,run_attempt=1,head_sha=self.revision,status='in_progress',conclusion=None,
            path='.github/workflows/'+m.FAST_TRACK_RELEASE_WORKFLOW,event='workflow_dispatch',
            head_branch=m.FAST_TRACK_PR_BASE,head_repository=dict(full_name=self.repository),
            display_title='LEGEND retained recovery original=10 application='+self.application)
        self.promotion=dict(self.run,id=19,event='push',status='completed',conclusion='success')
        self.rows=[self.run,self.promotion]
        self.rows += [dict(self.run,id=1000+i,head_sha='f'*40,path='.github/workflows/other.yml',event='push',status='completed',conclusion='success') for i in range(101)]
        self.rows[-1].update(status='queued',conclusion=None)
        self.rows.append(dict(self.run,id=22,head_sha='c'*40,status='completed',conclusion='failure'))
        names=['Select exact affected applications','Admit exact validated fast-track source',
            'Reconcile production schema before publication','Publish verified website routing',
            'Affected App Deploy — ${{ matrix.target }}',m.FAST_TRACK_RECOVERY_JOB]
        self.jobs=[dict(id=i+1,name=name,run_id=20,run_attempt=1,head_sha=self.revision,
            status='completed',conclusion='skipped',steps=[]) for i,name in enumerate(names)]
        self.jobs[-1].update(status='in_progress',conclusion=None,runner_name='fixture-runner')
        self.promotion_jobs=[dict(j,run_id=19,status='completed',conclusion='success' if i==0 else 'skipped') for i,j in enumerate(self.jobs)]
        self.calls=[];self.downloads=[];self.ref=self.revision;self.git_result=0
        candidate='e'*40;self.data={
            'actions/runs/20':self.run,'actions/runs/19':self.promotion,
            'actions/runs/20/attempts/1/jobs?per_page=100':dict(total_count=6,jobs=self.jobs),
            'actions/runs/19/attempts/1/jobs?per_page=100':dict(total_count=6,jobs=self.promotion_jobs),
            'commits/'+self.revision+'/pulls?per_page=100':[dict(number=7,merged_at='now',merge_commit_sha=self.revision,base=dict(ref=m.FAST_TRACK_PR_BASE),head=dict(sha=candidate,ref='candidate',repo=dict(full_name=self.repository)))]}
        self.receipts={};validations=[]
        for ident,workflow,prefix in zip(range(101,104),['approved-release-security-validation.yml','masterapp-platform-architecture-validation.yml','parfait-isolated-validation.yml'],['security','architecture','parfait']):
            run=dict(id=ident,run_attempt=1,head_sha=candidate,status='completed',conclusion='success',event='pull_request',path='.github/workflows/'+workflow,head_branch='candidate',head_repository=dict(full_name=self.repository))
            validations.append(run);self.data[f'actions/runs/{ident}']=run
            artifact=dict(id=ident+100,name=f'validation-resume-{prefix}-{ident}-1',expired=False,digest='sha256:'+'d'*64,workflow_run=dict(id=ident))
            self.data[f'actions/runs/{ident}/artifacts?per_page=100']=dict(total_count=1,artifacts=[artifact])
            self.receipts[ident]=dict(workflow=workflow,currentSha=candidate,validationBase=m.FAST_TRACK_PR_BASE,
                sourceValidationScope=dict(phase='source-validation-only',releaseAdmission=False,baseBranch=m.FAST_TRACK_PR_BASE,candidate=candidate,pullRequest=7))
        self.data[f'actions/runs?head_sha={candidate}&per_page=100']=dict(total_count=3,workflow_runs=validations)
        artifact=dict(id=40,name='fast-track-admission-19-1',expired=False,digest='sha256:'+'d'*64,workflow_run=dict(id=19))
        self.data['actions/runs/19/artifacts?per_page=100']=dict(total_count=1,artifacts=[artifact])
        self.expected=dict(authorizesMutation=False,productionEligible=False)
        self.policy=SimpleNamespace(tool_promotion_requested=lambda rev:True,read_request=lambda rev:dict(trustedToolPromotion=dict(baseRevision='d'*40)),trusted_tool_promotion=lambda *args:self.expected)
    def transport(self,repo,path,token):
        self.calls.append(path)
        if path.startswith('actions/runs?branch='):
            page=int(path.rsplit('&page=',1)[1]) if '&page=' in path else 1
            value=dict(total_count=len(self.rows),workflow_runs=self.rows[(page-1)*100:page*100])
        elif path.startswith('git/ref/'):
            value=dict(object=dict(sha=self.ref))
        else:value=self.data[path]
        return json.loads(json.dumps(value))
    def download(self,repo,ident,name,directory,**kwargs):
        self.downloads.append(ident)
        filename='fast-track-admission.json' if ident==19 else 'validation-resume.json'
        value=dict(self.expected,producingRun=19,producingAttempt=1) if ident==19 else self.receipts[ident]
        (directory/filename).write_text(json.dumps(value))
    @__import__('contextlib').contextmanager
    def environment(self):
        with tempfile.TemporaryDirectory() as temporary:
            event=Path(temporary)/'event.json';event.write_text(json.dumps(dict(inputs=dict(original_run='10',application_revision=self.application))))
            env=dict(GITHUB_ACTIONS='true',GITHUB_EVENT_NAME='workflow_dispatch',GITHUB_REF='refs/heads/'+m.FAST_TRACK_PR_BASE,
                GITHUB_JOB=m.FAST_TRACK_RECOVERY_JOB_ID,GITHUB_WORKFLOW_REF=self.repository+'/.github/workflows/'+m.FAST_TRACK_RELEASE_WORKFLOW+'@refs/heads/'+m.FAST_TRACK_PR_BASE,
                GITHUB_REPOSITORY=self.repository,GH_TOKEN='fixture',RUNNER_NAME='fixture-runner',GITHUB_RUN_ID='20',GITHUB_RUN_ATTEMPT='1',GITHUB_SHA=self.revision,GITHUB_EVENT_PATH=str(event))
            with patch.dict(os.environ,env),patch.object(m,'_api_get_uncached',side_effect=self.transport), \
                 patch.object(m,'_download_run_artifact',side_effect=self.download), \
                 patch.object(m,'_release_history_source',return_value=self.workflow), \
                 patch.object(m,'_fast_track_policy',return_value=self.policy), \
                 patch.object(m.subprocess,'check_output',return_value=self.revision), \
                 patch.object(m.subprocess,'run',side_effect=lambda *a,**k:SimpleNamespace(returncode=self.git_result)):
                yield
    def history(self):return m.fast_track_historical_producers(self.repository,self.revision,'fixture')
    def test_actual_execution_orchestration_counts_complete_history(self):
        import time
        def exercise(new):
            self.calls.clear();self.downloads.clear();started=time.perf_counter()
            def inherited():
                for _ in range(20):
                    self.assertEqual(self.history()[0]['id'],19)
                    m.fast_track_promotion_parent(self.repository,self.promotion,'fixture')
            with self.environment(),__import__('contextlib').ExitStack() as stack:
                if not new:
                    stack.enter_context(patch.object(m,'_fast_track_history_observation',side_effect=lambda repo,token,clock: m._read_fast_track_history_inventory(repo,token,clock=clock)))
                    observer=m.assert_fast_track_recovery_execution(10,self.application)
                stack.enter_context(m.recovery_material_reuse())
                if new:
                    with m.recovery_admission_proof():observer=m.assert_fast_track_recovery_execution(10,self.application)
                    m.assert_fast_track_recovery_live_execution(10,self.application,observer)
                with m.recovery_admission_proof():inherited()
                with m.recovery_admission_proof():
                    inherited()
                    if new:self.assertEqual(m.assert_fast_track_recovery_execution(10,self.application),observer)
                if new:m.assert_fast_track_recovery_live_execution(10,self.application,observer)
                else:self.assertEqual(m.assert_fast_track_recovery_execution(10,self.application),observer)
                if new:
                    with m.recovery_admission_proof():
                        self.assertEqual(m.assert_fast_track_recovery_execution(10,self.application),observer)
                        inherited()
                    m.assert_fast_track_recovery_live_execution(10,self.application,observer)
                else:
                    self.assertEqual(m.assert_fast_track_recovery_execution(10,self.application),observer)
                    inherited()
            return dict(historyPageReads=sum(p.startswith('actions/runs?branch=') for p in self.calls),apiReads=len(self.calls),downloads=len(self.downloads),fixtureSeconds=round(time.perf_counter()-started,4))
        old=exercise(False);new=exercise(True)
        self.assertEqual(old['historyPageReads'],126);self.assertEqual(new['historyPageReads'],16)
        self.assertEqual(new['downloads'],4);self.assertLess(new['downloads'],old['downloads'])
        print('RECOVERY_ACTUAL_EXECUTION_HISTORY_COUNTS:'+json.dumps(dict(before=old,after=new)))
    def test_complete_snapshot_drift_holds_and_next_boundary_refetches(self):
        changes=[lambda:self.rows.append(dict(self.rows[-1],id=5000)),lambda:self.rows.pop(),
            lambda:self.rows[-1].update(id=5001),lambda:self.run.update(run_attempt=2),
            lambda:self.run.update(status='completed'),lambda:self.rows[-2].update(status='in_progress'),
            lambda:self.rows[-1].update(id=self.rows[0]['id'])]
        for index in range(len(changes)):
            self.setUp();changes=[lambda:self.rows.append(dict(self.rows[-1],id=5000)),lambda:self.rows.pop(),lambda:self.rows[-1].update(id=5001),lambda:self.run.update(run_attempt=2),lambda:self.run.update(status='completed'),lambda:self.rows[-2].update(status='in_progress'),lambda:self.rows[-1].update(id=self.rows[0]['id'])]
            with self.subTest(change=index),self.environment(),self.assertRaises(RuntimeError):
                with m.recovery_admission_proof():
                    self.history();self.history();changes[index]()
            self.assertIsNone(m._RECOVERY_PROOF_VIEW.get())
        self.setUp()
        with self.environment():
            for _ in range(2):
                with m.recovery_admission_proof():self.history();self.history()
        self.assertEqual(sum(p.startswith('actions/runs?branch=') for p in self.calls),8)
    def test_snapshot_copies_and_order_only_change_preserve_full_rows(self):
        with self.environment(),m.recovery_admission_proof():
            first=self.history();first[0]['head_sha']='0'*40
            self.assertEqual(self.history()[0]['head_sha'],self.revision)
            self.rows.reverse()
    def test_final_full_read_rejects_missing_or_changed_late_page(self):
        for fault in ('missing','changed-count'):
            self.setUp()
            def broken(repo,path,token):
                value=self.transport(repo,path,token)
                if '&page=2' in path:
                    if fault=='missing':value['workflow_runs']=[]
                    else:value['total_count']+=1
                return value
            with self.subTest(fault=fault),self.environment(),__import__('contextlib').ExitStack() as patches:
                with self.assertRaisesRegex(RuntimeError,'HISTORY_INCOMPLETE'):
                    with m.recovery_admission_proof():
                        self.history()
                        patches.enter_context(patch.object(m,'_api_get_uncached',side_effect=broken))
    def test_full_read_late_response_remains_bounded(self):
        now=[0]
        def late(repo,path,token):
            result=self.transport(repo,path,token)
            if '&page=2' in path:now[0]=31
            return result
        with self.environment(),patch.object(m,'_api_get_uncached',side_effect=late),self.assertRaises(m.EvidenceLookupUnavailable):
            m._read_fast_track_history_inventory(self.repository,'fixture',clock=lambda:now[0],uncached=True)

    def test_fresh_owner_ref_runner_job_attempt_and_source_cannot_reuse_history(self):
        changes=[lambda:self.run.update(status='completed'),lambda:self.run.update(run_attempt=2),
            lambda:setattr(self,'ref','c'*40),lambda:self.jobs[-1].update(runner_name='other'),
            lambda:self.jobs[-1].update(id=99),lambda:self.jobs[0].update(status='in_progress'),
            lambda:setattr(self,'git_result',1),lambda:self.run.update(event='push')]
        for index in range(len(changes)):
            self.setUp();changes=[lambda:self.run.update(status='completed'),lambda:self.run.update(run_attempt=2),lambda:setattr(self,'ref','c'*40),lambda:self.jobs[-1].update(runner_name='other'),lambda:self.jobs[-1].update(id=99),lambda:self.jobs[0].update(status='in_progress'),lambda:setattr(self,'git_result',1),lambda:self.run.update(event='push')]
            with self.subTest(change=index),self.environment(),m.recovery_material_reuse():
                with m.recovery_admission_proof():observer=m.assert_fast_track_recovery_execution(10,self.application)
                changes[index]()
                with self.assertRaises(ValueError):m.assert_fast_track_recovery_live_execution(10,self.application,observer)


class RetainedRecoveryDiscoveryTests(unittest.TestCase):
    def setUp(self):
        self.before='a'*40; self.revision='b'*40; self.parent='c'*40
        self.prior=dict(id=71,head_sha=self.before,status='completed',conclusion='failure',run_attempt=1)
        self.name=m.fast_track_recovery_summary_name(71,self.before)
        self.artifacts=[dict(id=101,name=self.name,expired=False)]
        self.jobs=[dict(name='Select exact affected applications',status='completed',conclusion='success'),
            dict(name='Admit exact validated fast-track source',conclusion='success'),
            *[dict(name='Affected App Deploy — '+key,conclusion='success') for key in ('portal','client','website')]]
        self.calls=[];self.complete=True;self.count=None
    def api(self,repo,path,token):
        self.calls.append(path)
        if path.startswith('actions/artifacts?name='):
            self.assertEqual(path,'actions/artifacts?name='+self.name+'&per_page=100')
            return dict(total_count=len(self.artifacts) if self.count is None else self.count,artifacts=self.artifacts)
        if path.startswith('actions/workflows/'):
            return dict(total_count=0,workflow_runs=[])
        if path=='actions/runs/71/jobs?filter=latest&per_page=100':
            return dict(total_count=len(self.jobs),jobs=self.jobs)
        if path==f'commits/{self.before}/pulls?per_page=100':
            return [dict(merged_at='2026-10-23T00:00:00Z',merge_commit_sha=self.before,
                base=dict(ref=m.FAST_TRACK_PR_BASE),head=dict(repo=dict(full_name='owner/repo')))]
        raise AssertionError(path)
    def invoke(self):
        from contextlib import ExitStack
        with ExitStack() as stack:
            stack.enter_context(patch.object(m,'api_get',side_effect=self.api))
            stack.enter_context(patch.object(m.subprocess,'run',return_value=SimpleNamespace(returncode=0)))
            stack.enter_context(patch.object(m.subprocess,'check_output',return_value=f'{self.before} {self.parent} {self.revision}'))
            stack.enter_context(patch.object(m,'fast_track_historical_producers',side_effect=lambda repo,head,token:[self.prior] if head==self.before else []))
            stack.enter_context(patch.object(m,'fast_track_untriggered_repair_parent',return_value=None))
            for name in ('fast_track_completed_apps_pending_routing','attested_fast_track_manual_dns_proxy_completion',
                'attested_fast_track_brand_preupload','fast_track_completed_sql_unpublished','fast_track_unpublished_apps_with_migration_intent'):
                stack.enter_context(patch.object(m,name,return_value=False))
            validator=stack.enter_context(patch.object(m,'fast_track_completed_operation_receipts',
                side_effect=self.complete if isinstance(self.complete,Exception) else None,return_value=self.complete))
            self.validator=validator
            return m.fast_track_effective_baseline(self.before,self.revision,repository='owner/repo',token='placeholder')
    def test_recovery_only_summary_reaches_canonical_validator(self):
        self.assertEqual(self.invoke(),self.before)
        self.validator.assert_called_once_with('owner/repo',self.prior,'placeholder')
    def test_summary_presence_does_not_replace_canonical_proof(self):
        self.complete=False
        with self.assertRaisesRegex(RuntimeError,'REQUIRES_OPERATION_RECONCILIATION'):self.invoke()
        self.validator.assert_called_once()
    def test_canonical_validation_error_is_not_swallowed(self):
        self.complete=ValueError('RECOVERY_PRODUCER_UNPROVEN')
        with self.assertRaisesRegex(ValueError,'RECOVERY_PRODUCER_UNPROVEN'):self.invoke()
    def test_absent_summary_preserves_prewrite_carry_forward(self):
        self.artifacts=[];self.jobs[0]['conclusion']='failure'
        for job in self.jobs[1:]:job['conclusion']='skipped'
        self.assertEqual(self.invoke(),self.parent)
        self.validator.assert_not_called()
    def test_malformed_inventory_fails_closed(self):
        for rows,count in (([None],1),([self.artifacts[0]]*2,2),(self.artifacts,2)):
            with self.subTest(rows=rows,count=count):
                self.artifacts=rows;self.count=count
                with self.assertRaisesRegex(ValueError,'INVENTORY_INCOMPLETE'):self.invoke()
                self.validator.assert_not_called()
    def test_wrong_or_expired_summary_cannot_clear_baseline(self):
        for artifact in (dict(id=101,name='unrelated',expired=False),dict(id=101,name=self.name,expired=True)):
            with self.subTest(artifact=artifact):
                self.artifacts=[artifact]
                with self.assertRaisesRegex(ValueError,'RECOVERY_SUMMARY_ARTIFACT_UNPROVEN'):self.invoke()
                self.validator.assert_not_called()
    def test_existing_diagnostics_still_use_canonical_proof(self):
        self.artifacts=[];original=self.api
        def api(repo,path,token):
            if path.startswith('actions/workflows/'):
                return dict(total_count=1,workflow_runs=[dict(id=81,head_branch=m.FAST_TRACK_PR_BASE,
                    path='.github/workflows/'+m.FAST_TRACK_COMPLETION_WORKFLOW)])
            if path=='actions/runs/81/artifacts?per_page=100':
                return dict(total_count=1,artifacts=[dict(id=102,name='legend-release-completion-fixture')])
            return original(repo,path,token)
        self.api=api
        with patch.object(m,'_release_history_json',return_value=dict(applicationRevision=self.before)):
            self.assertEqual(self.invoke(),self.before)
        self.validator.assert_called_once()


class ReviewedTrainingContractTests(unittest.TestCase):
    def setUp(self):
        self.prior = {'schemaVersion':3,'migrations':[{'id':'20261026000134_AddCanonicalSubscriptionPaymentCompletion','supported':True}]}
        self.entry = {'id':'20261026063902_AddScopedTrainingSubscriptions','supported':True,'columns':[]}
        self.contract = {'schemaVersion':3,'migrations':self.prior['migrations']+[self.entry]}
        self.current = {'runtimeIdentity':'a'*64,'toolIdentity':'c'*64,'executionIdentity':'d'*64}
        self.trusted = dict(self.current, runtimeIdentity='b'*64)
        self.pin = hashlib.sha256(json.dumps(self.entry,sort_keys=True,separators=(',',':')).encode()).hexdigest()

    def prove(self):
        with patch.object(m,'REVIEWED_TRAINING_ENTRY',self.pin), patch.object(m.subprocess,'run',return_value=SimpleNamespace(returncode=0)):
            return m.reviewed_training_contract_extension('e'*40,'f'*40,self.contract,self.prior,self.current,self.trusted)

    def test_unreviewed_runtime_pair_is_refused(self):
        with patch.object(m,'REVIEWED_TRAINING_RUNTIME_PAIRS',frozenset()): self.assertFalse(self.prove())

    def test_exact_reviewed_pair_and_suffix_are_required(self):
        with patch.object(m,'REVIEWED_TRAINING_RUNTIME_PAIRS',frozenset({('b'*64,'a'*64)})):
            self.assertTrue(self.prove())
            for key in ('runtimeIdentity','toolIdentity','executionIdentity'):
                old=self.current[key];self.current[key]='0'*64
                self.assertFalse(self.prove());self.current[key]=old
            self.entry['supported']=False;self.assertFalse(self.prove())

    def test_changed_prefix_or_missing_predecessor_is_refused(self):
        with patch.object(m,'REVIEWED_TRAINING_RUNTIME_PAIRS',frozenset({('b'*64,'a'*64)})):
            self.prior['migrations'][0]['id']='20260101000000_Unknown'
            self.assertFalse(self.prove())

    def test_git_ancestry_and_inherited_tools_are_required(self):
        with patch.object(m,'REVIEWED_TRAINING_RUNTIME_PAIRS',frozenset({('b'*64,'a'*64)})), patch.object(m,'REVIEWED_TRAINING_ENTRY',self.pin):
            for results in ([1],[0,1]):
                with patch.object(m.subprocess,'run',side_effect=[SimpleNamespace(returncode=x) for x in results]):
                    self.assertFalse(m.reviewed_training_contract_extension('e'*40,'f'*40,self.contract,self.prior,self.current,self.trusted))


class ProtectBaselineRecoveryTests(unittest.TestCase):
    def log(self):
        rows = ['[command]/usr/bin/git log -1 --format=%H', m.PROTECT_RECOVERY_REVISION,
                'in fast_track_deploy', 'in prepare_protect_replacement',
                'DeploymentDrift: FAST_TRACK_SUPERSESSION_LIVE_BASELINE_CHANGED',
                'Process completed with exit code 1.']
        return '\n'.join('2026-10-26T14:58:37Z ' + row for row in rows)
    def proven(self, log, **kwargs):
        return m._fast_track_preupload_log_proven(log, m.PROTECT_RECOVERY_REVISION,
            dict(started_at='2026-10-26T14:57:00Z', completed_at='2026-10-26T14:59:00Z'), **kwargs)
    def test_positive_fixed_baseline_failure_requires_specific_classifier(self):
        self.assertTrue(self.proven(self.log(), protect_baseline=True))
        self.assertFalse(self.proven(self.log()))
    def test_missing_stack_wrong_head_or_any_submission_rejected(self):
        for text in ['in fast_track_deploy', 'in prepare_protect_replacement', m.PROTECT_RECOVERY_REVISION,
                     'DeploymentDrift: FAST_TRACK_SUPERSESSION_LIVE_BASELINE_CHANGED', 'Process completed with exit code 1.']:
            with self.subTest(text=text):
                self.assertFalse(self.proven(self.log().replace(text, 'unproven'), protect_baseline=True))
        for text in ['Submitting the verified immutable ZIP once.', 'AZURE_TRACKED_DEPLOYMENT_REFERENCE:any']:
            self.assertFalse(self.proven(self.log()+'\n2026-10-26T14:58:37Z '+text, protect_baseline=True))
    def test_exact_correction_is_bound_inside_recovery_receipt(self):
        original = dict(run=dict(id=m.PROTECT_RECOVERY_RUN, run_attempt=2, head_sha=m.PROTECT_RECOVERY_REVISION))
        material = dict(packageArtifact=dict(id=12661220624, digest='sha256:'+'a'*64),
                        protectBaselineCorrection=dict(baselineRevision=m.PROTECT_BASELINE_REVISION, historicalOutcome='UNKNOWN'))
        proof = m.fast_track_recovery_record_proof(original, material, dict(runId=99))
        self.assertEqual(proof['protectBaselineCorrection'], material['protectBaselineCorrection'])
        self.assertEqual(proof['originalRun'], m.PROTECT_RECOVERY_RUN)
    def test_baseline_receipt_and_both_artifact_digests_are_authenticated(self):
        original=dict(run=dict(id=m.PROTECT_RECOVERY_RUN,head_sha=m.PROTECT_RECOVERY_REVISION))
        package=dict(id=12661220624,digest='sha256:d01034901695028c32960fdf1b55aff4b2e26ec65782db475bec524765e89122')
        anchor=dict(id=40063411585,head_sha=m.PROTECT_BASELINE_REVISION,status='completed')
        rollback=dict(id=12548189496,name='fast-track-package-'+m.PROTECT_BASELINE_REVISION+'-protect',
            digest='sha256:e3df0d1cc7b3573aeae7883bc4e3810444a619a105e47d0fece3112a5c99eae2',expired=False,workflow_run=anchor)
        receipt=dict(id=12548958892,name='legend-release-operation-success-eee55365520f03613d09d8deed68ea56e85e05da0a323737c5b562c415f455db',
            digest='sha256:ed43f4f504badd5d41eb5216a1c10550db2576fd3a7dc69017f6fc0af32b02a4',expired=False,workflow_run=anchor)
        record=dict(target='protect', applicationRevision=m.PROTECT_BASELINE_REVISION,
            packageDigest='fe81a03662c3657c7b9ae9012cc38198987b353acc44725d84f8a4f9d87bb584',
            operationId='eee55365520f03613d09d8deed68ea56e85e05da0a323737c5b562c415f455db',
            packageProducerRun=40063411585,producingRun=40063411585,producingAttempt=5,
            phase='success',deploymentIds=['fcba568e391649b4aa08fa3dec82e156'])
        def invoke():
            with patch.object(m,'_release_history_source',return_value='{}'), \
                 patch.object(m,'fast_track_protect_supersession',return_value=dict(historicalOutcome='UNKNOWN')), \
                 patch.object(m,'api_get',side_effect=[anchor,dict(total_count=2,artifacts=[rollback,receipt])]), \
                 patch.object(m,'is_fast_track_producer',return_value=True), \
                 patch.object(m,'_completion_jobs',return_value={1:[],5:[]}), \
                 patch.object(m,'_completion_original_artifact_owner') as owner, \
                 patch.object(m,'_release_history_json',return_value=record) as read:
                result=m.protect_recovery_baseline_proof('MYLEGND/masterapp',original,package,'token')
                self.assertEqual(owner.call_count,2)
                self.assertEqual(owner.call_args.args[0]['attempts'],{1:[],5:[]})
                self.assertTrue(read.call_args.kwargs['verify_digest'])
                return result
        self.assertEqual(invoke()['authorization']['historicalOutcome'],'UNKNOWN')
        for obj,key,value in [(rollback,'digest','sha256:'+'a'*64),(receipt,'digest','sha256:'+'a'*64),
                              (rollback,'expired',True),(record,'packageDigest','b'*64),
                              (record,'deploymentIds',['different']),(record,'producingAttempt',4),
                              (anchor,'head_sha','c'*40)]:
            old=obj[key];obj[key]=value
            try:
                with self.subTest(key=key),self.assertRaises(ValueError):invoke()
            finally:obj[key]=old

    def test_correction_rejects_other_run_revision_package_and_repository(self):
        original = dict(run=dict(id=m.PROTECT_RECOVERY_RUN, head_sha=m.PROTECT_RECOVERY_REVISION))
        package = dict(id=12661220624, digest='sha256:d01034901695028c32960fdf1b55aff4b2e26ec65782db475bec524765e89122')
        cases = [('other/repo', original, package),
                 ('MYLEGND/masterapp', dict(run=dict(original['run'], id=1)), package),
                 ('MYLEGND/masterapp', dict(run=dict(original['run'], head_sha='b'*40)), package),
                 ('MYLEGND/masterapp', original, dict(package,id=1)),
                 ('MYLEGND/masterapp', original, dict(package,digest='sha256:'+'b'*64))]
        for repo, context, artifact in cases:
            with self.subTest(repo=repo,context=context,artifact=artifact), patch.object(m,'api_get') as api:
                with self.assertRaisesRegex(ValueError,'ORIGINAL_MATERIAL_CHANGED'):
                    m.protect_recovery_baseline_proof(repo,context,artifact,'token')
                api.assert_not_called()


class ProtectCorrectionSerializerNonentryTests(unittest.TestCase):
    def setUp(self):
        f=RecoveryTransportNonentryTests();f.setUp();self.f=f
        f.tool='6c21a702e28a0fb4379eb73cbc59c174636f1412';f.revision=m.PROTECT_RECOVERY_REVISION
        f.observer.update(runId=40356096191,jobId=121688862786,toolRevision=f.tool)
        f.run.update(id=40356096191,head_sha=f.tool,display_title='LEGEND retained recovery original='+str(m.PROTECT_RECOVERY_RUN)+' application='+f.revision)
        f.job.update(id=121688862786,run_id=f.run['id'],head_sha=f.tool)
        f.original['run'].update(id=m.PROTECT_RECOVERY_RUN,head_sha=f.revision,run_attempt=2);f.original['artifacts']=[]
        for key in f.targets:
            digest='21965d54a0641bb7e720b1314ddd45a1f3894c3b5d7044edb6c19b78e6d9e3a4' if key=='protect' else 'd'*64
            identity=dict(target=key,applicationRevision=f.revision,packageDigest=digest)
            operation=hashlib.sha256(json.dumps(identity,sort_keys=True,separators=(',',':')).encode()).hexdigest()
            f.targets[key].update(operationId=operation,packageDigest=digest)
            f.materials[key]['originalIntent']=None if key=='protect' else dict(identity,operationId=operation,packageProducerRun=m.PROTECT_RECOVERY_RUN)
        f.materials['protect']['protectBaselineCorrection']={'authenticated':'fixture'}
        f.proof.update(originalRun=m.PROTECT_RECOVERY_RUN,originalAttempt=2,applicationRevision=f.revision)
    def invoke(self, *, target='protect', operation=None, log=None, late=False, drift=False):
        f=self.f;reads=[]
        def api(repo,path,token):
            if path==f"actions/runs/{m.PROTECT_RECOVERY_RUN}/artifacts?per_page=100":return dict(total_count=1 if drift else 0,artifacts=[dict(id=99)] if drift else [])
            reads.append(path)
            return dict(total_count=1,artifacts=[dict(id=99)]) if late and len(reads)>1 else f.inventory
        with patch.object(m,'FAST_TRACK_RECOVERY_PROTECT_SERIALIZER_GENERATIONS',f.generations), \
             patch.object(m,'fast_track_recovery_producer',return_value=f.observer), \
             patch.object(m,'_release_history_source',side_effect=lambda r,s,p,t:f.source[p]), \
             patch.object(m,'api_get',side_effect=api),patch.object(m,'_release_job_log',return_value=log or f.log()), \
             patch.object(m,'fast_track_completion_original',return_value=f.original), \
             patch.object(m,'fast_track_recovery_material',side_effect=lambda r,o,k,t:f.materials[k]), \
             patch.object(m,'fast_track_completion_material',side_effect=lambda r,o,k,t:dict(intent=f.materials[k]['originalIntent'])), \
             patch.object(m,'protect_recovery_baseline_proof',return_value={'authenticated':'fixture'}), \
             patch.object(m,'fast_track_recovery_package_digest',return_value=f.targets['protect']['packageDigest']), \
             patch.object(m,'_completion_original_unchanged'):
            return m.attested_recovery_preintent_failure('MYLEGND/masterapp',f.run,f.job,1,f.revision,'token',target=target,operation_id=operation or f.targets['protect']['operationId'])
    def test_exact_protect_nonentry_with_original_sibling_intents(self):self.assertTrue(self.invoke())
    def test_no_global_sibling_or_different_operation_exception(self):
        for target in [None,'portal','client','website','parfait']:self.assertFalse(self.invoke(target=target))
        self.assertFalse(self.invoke(operation='f'*64))
    def test_exact_execution_and_original_snapshot_required(self):
        for obj,key,value in [('run','id',99),('run','run_attempt',2),('job','id',99),('run','head_sha','f'*40),('proof','originalAttempt',1)]:
            self.setUp();getattr(self.f,obj)[key]=value
            with self.subTest(key=key):self.assertFalse(self.invoke())
    def test_original_intents_correction_and_materials_must_survive(self):
        self.f.materials['website']['originalIntent']=None;self.assertFalse(self.invoke())
        self.setUp();self.f.materials['protect']['protectBaselineCorrection']={};self.assertFalse(self.invoke())
        self.setUp();self.f.targets['protect']['packageArtifactId']=999;self.assertFalse(self.invoke())
        self.setUp();self.assertFalse(self.invoke(drift=True))
    def test_source_log_and_empty_inventory_are_required(self):
        self.assertFalse(self.invoke(late=True))
        self.assertFalse(self.invoke(log=self.f.log()+'\n2026-10-23T10:02:00Z Submitting the verified immutable ZIP once.'))
        self.assertFalse(self.invoke(log=self.f.log().replace('in publish_phase_record','in unrelated')))
        self.f.inventory=dict(total_count=1,artifacts=[dict(id=99)]);self.assertFalse(self.invoke())
        self.setUp();self.f.source['scripts/release-artifacts/transport.cjs']='unknown';self.assertFalse(self.invoke())


class FastTrackCarriedMigrationTests(unittest.TestCase):
    def setUp(self):
        self.run = dict(id=10, run_attempt=2, workflow_id=30, head_sha='a'*40,
            path='.github/workflows/'+m.FAST_TRACK_RELEASE_WORKFLOW,
            head_branch=m.FAST_TRACK_PR_BASE, event='push', status='completed',
            head_repository=dict(full_name='owner/repo'))
        self.details = {i: dict(self.run, run_attempt=i,
            run_started_at=f'2026-10-26T{hour}:00:00Z') for i,hour in [(1,'14'),(2,'15')]}
        self.step = dict(name=m.FAST_TRACK_CHILD_STEPS['migrations'][1], status='completed',
            conclusion='success', started_at='2026-10-26T14:20:00Z', completed_at='2026-10-26T14:40:00Z')
        self.original = dict(id=100, run_id=10, run_attempt=1, head_sha='a'*40,
            name=m.FAST_TRACK_CHILD_STEPS['migrations'][0], status='completed', conclusion='success',
            runner_id=40, runner_name='fixture-runner', created_at='2026-10-26T14:01:00Z',
            started_at='2026-10-26T14:20:00Z', completed_at='2026-10-26T14:41:00Z', steps=[self.step])
        self.carried = json.loads(json.dumps(dict(self.original,id=101,run_attempt=2,
            created_at='2026-10-26T15:00:03Z')))
        self.jobs = {1:[self.original],2:[self.carried]}
        self.logs = {100:'2026-10-26T14:40:00Z Schema ready; validated migration child applied.',
                     101:'2026-10-26T14:40:00Z Schema ready; validated migration child applied.'}
        self.artifacts=[]; self.incomplete=False
    def api(self,repo,path,token):
        if '/jobs?' in path:
            rows=self.jobs[int(path.split('/attempts/')[1].split('/')[0])]
            return dict(jobs=rows,total_count=len(rows)+int(self.incomplete))
        if '/attempts/' in path:return self.details[int(path.rsplit('/',1)[1])]
        if '/artifacts?' in path:return dict(total_count=len(self.artifacts),artifacts=self.artifacts)
        if path.startswith('actions/runs?'):return dict(total_count=0,workflow_runs=[])
        raise AssertionError(path)
    def environment(self):
        import contextlib
        stack=contextlib.ExitStack()
        stack.enter_context(patch.object(m,'fast_track_execution_history',return_value=[self.run]))
        stack.enter_context(patch.object(m,'_release_history_source',return_value='fixture'))
        stack.enter_context(patch.object(m,'named_step_blocks',return_value={self.step['name']:'fixture'}))
        stack.enter_context(patch.object(m,'attested_fast_track_migration_prewrite',return_value=False))
        stack.enter_context(patch.object(m,'api_get',side_effect=self.api))
        stack.enter_context(patch.object(m,'_release_job_log',side_effect=lambda repo,job,token:self.logs[job]))
        return stack
    def inventory(self):
        with self.environment():return m.fast_track_child_attempts('owner/repo','migrations','fixture')
    def test_carried_row_keeps_only_original_execution(self):
        rows=self.inventory()
        self.assertEqual([(attempt,job['id']) for _,attempt,job,_ in rows],[(1,100)])
    def test_original_intent_is_still_required(self):
        with self.environment(), patch.object(m,'attested_fast_track_no_write_observation',return_value=False):
            with self.assertRaisesRegex(RuntimeError,r'missing intent.*run=10;attempt=1'):
                m.release_child_first_write_proven('owner/repo','migrations','b'*64,'c'*64,99,1,'fixture')
    def test_original_intent_is_authenticated_not_reissued(self):
        self.artifacts=[dict(name='legend-release-child-intent-'+'d'*64,expired=False)]
        record=dict(child='migrations',producingAttempt=1,dependencyIdentity='d'*64,partitionIdentity='e'*64)
        with self.environment(), patch.object(m,'_release_history_json',return_value=record), \
                patch.object(m,'_validate_child_generation',return_value='d'*64) as validate, \
                patch.object(m,'_reconciled_retained_migration_intent',return_value=False):
            m.release_child_first_write_proven('owner/repo','migrations','b'*64,'c'*64,99,1,'fixture')
            validate.assert_called_once_with('owner/repo',self.run,self.artifacts[0],record,'migrations','fixture','intent')
    def test_mismatches_are_not_collapsed(self):
        cases=[('head_sha','f'*40),('runner_id',41),('created_at','2026-10-26T14:00:00Z'),
               ('started_at','2026-10-26T15:00:04Z'),('run_attempt',1)]
        for key,value in cases:
            self.setUp();self.carried[key]=value
            with self.subTest(key=key):self.assertEqual(len(self.inventory()),2)
        self.setUp();self.logs[101]+=' changed';self.assertEqual(len(self.inventory()),2)
        self.setUp();self.logs={100:'',101:''};self.assertEqual(len(self.inventory()),2)
        self.setUp();self.carried['steps'][0]['completed_at']='2026-10-26T14:39:00Z'
        self.assertEqual(len(self.inventory()),2)
    def test_changed_attempt_provenance_is_not_collapsed(self):
        for key,value in [('head_sha','f'*40),('event','pull_request'),('status','in_progress'),
                          ('head_repository',dict(full_name='fork/repo')),('workflow_id',31)]:
            self.setUp();self.details[2][key]=value
            with self.subTest(key=key):self.assertEqual(len(self.inventory()),2)
    def test_incomplete_inventory_remains_denied(self):
        self.incomplete=True
        with self.assertRaisesRegex(RuntimeError,'JOBS_INCOMPLETE'):self.inventory()
    def test_ambiguous_original_is_not_collapsed(self):
        with self.environment():
            prior=[(self.run,1,self.original,self.step)]*2
            self.assertFalse(m._fast_track_child_carried_forward('owner/repo',self.run,2,self.carried,prior,'fixture'))


if __name__ == "__main__":
    # Individual hosted scenarios explicitly opt in and mock their providers.
    # Synthetic planner revisions must never query the real CI repository.
    with patch.dict(os.environ, {"GITHUB_ACTIONS": "false"}):
        unittest.main()

