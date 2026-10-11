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
            dict(total_count=len(artifacts), artifacts=artifacts),dict(total_count=len(jobs),jobs=jobs)]):
            return m.fast_track_observation_source('owner/repo', 'a'*40, 10, attempt, 'token')

    def artifact(self, attempt):
        return dict(name='parfait-readonly-evidence-'+'a'*40+'-'+str(attempt),expired=False)

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
            applications={'masterapp-parfait': dict(self.row), 'masterapp-protect': dict(self.row)})

    def test_same_run_same_database_baseline_accepted(self):
        baseline=m.fast_track_baseline(self.report, 'd'*40, 123)
        self.assertEqual(baseline['pendingCount'], 1)
        self.assertNotIn('observed', baseline)

    def test_wrong_candidate_run_and_mutation_claim_rejected(self):
        for key,value in [('candidate','e'*40),('runId','124'),('authorizesMutation',True),
                          ('sharedDatabase',False),('purpose','release-admission')]:
            report=copy.deepcopy(self.report);report[key]=value
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, 'IDENTITY_UNPROVEN'):
                m.fast_track_baseline(report,'d'*40,123)

    def test_unobserved_or_inconsistent_schema_rejected(self):
        for key,value in [('observed',False),('ready',True),('pendingCount',True),
                          ('baselineIdentity','invalid'),('firstPendingMigrationId','unsafe')]:
            report=copy.deepcopy(self.report);report['applications']['masterapp-protect'][key]=value
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, 'SCHEMA_UNPROVEN'):
                m.fast_track_baseline(report,'d'*40,123)

    def test_different_database_or_baseline_rejected(self):
        for key in ('databaseIdentity','schemaIdentity','baselineIdentity'):
            report=copy.deepcopy(self.report);report['applications']['masterapp-protect'][key]='f'*64
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, 'BASELINES_DIFFER'):
                m.fast_track_baseline(report,'d'*40,123)

    def test_unexpected_host_rejected(self):
        self.report['applications']['unrelated-app']=self.row
        with self.assertRaisesRegex(ValueError, 'IDENTITY_UNPROVEN'):
            m.fast_track_baseline(self.report,'d'*40,123)


if __name__ == '__main__':
    unittest.main()
