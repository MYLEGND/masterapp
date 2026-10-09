#!/usr/bin/env python3
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("validation_resume", ROOT / "scripts" / "validation-resume.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)



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
        with patch.object(m, 'migration_probe_identity', side_effect=[{'runtimeIdentity': 'new'}, {'runtimeIdentity': 'old'}]):
            with self.assertRaisesRegex(ValueError, 'dependency content changed'):
                m.candidate_migration_contract_proven(self.candidate, self.approved, {}, {})

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
            with patch.object(m, "prior_evidence") as remote:
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
        self.assertIn("scripts/validation-resume.py lifecycle-evidence", lifecycle)
        identity_step = lifecycle.split("      - name: Resolve lifecycle validation authority identity\n", 1)[1].split("      - name:", 1)[0]
        self.assertNotIn("gh api", identity_step)
        self.assertNotIn("sha256sum", identity_step)

        release = (ROOT / ".github" / "workflows" / "all-intentional-direct-release-20260918.yml").read_text()
        validated = release.split("      - name: Reuse exact successful validation package when available\n", 1)[1].split("      - name:", 1)[0]
        rollback = release.split("      - name: Reuse exact retained live package when available\n", 1)[1].split("      - uses:", 1)[0]
        self.assertIn('git show "${GITHUB_SHA}:scripts/validation-resume.py"', validated)
        self.assertIn('python3 "$RUNNER_TEMP/current-validation-resume.py" validated-package', validated)
        self.assertIn("rollback-evidence", rollback)
        self.assertIn('git show "${RELEASE_SHA}:scripts/validation-resume.py"', release)
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

        with patch.object(m, "_successful_package_child", return_value=True), \
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

    def history(self, attempts, *, total_extra=0, untrusted=False, run_attempt=1, malformed_attempt=False, missing_count=False):
        prior = {'id': 8, 'head_branch': m.TRUSTED_PR_BASE, 'event': 'workflow_dispatch',
                 'head_repository': {'full_name': 'owner/repo'}, 'head_sha': 'b' * 40,
                 'path': '.github/workflows/' + m.DIRECT_RELEASE_WORKFLOW,
                 'run_attempt': len(attempts), 'status': 'completed'}
        if malformed_attempt:
            prior['run_attempt'] = run_attempt
        if untrusted:
            prior['head_repository']['full_name'] = 'other/repo'
        def api(repository, path, token):
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
        with patch.object(m, '_release_history_api', side_effect=api), \
             patch.object(m, '_release_history_runs', return_value=[prior]), \
             patch.object(m, '_release_history_source', return_value=self.source), \
             patch.object(m, '_release_attempt_package_revision', return_value=revision), \
             patch.object(m, '_remember_release_exclusion'), \
             patch.dict(m._RELEASE_HISTORY_EXCLUSIONS, {}, clear=True):
            if kind == 'transaction':
                return m.release_transaction_plan_history('owner/repo', plan_id, revision, targets, 99, 1, 'token')
            return m.release_operation_history('owner/repo', 'op', revision, next(iter(targets)), 99, 1, 'token')

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


if __name__ == "__main__":
    unittest.main()
