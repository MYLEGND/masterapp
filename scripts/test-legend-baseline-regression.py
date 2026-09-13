#!/usr/bin/env python3
"""Negative validator regressions; optional read-only validation of a captured TRX.

--actual-trx PATH --actual-source-root PATH runs an additional observation against
an ephemeral manifest derived from that capture, not a release authorization.
"""
import argparse
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

SPEC = importlib.util.spec_from_file_location('validator', Path(__file__).with_name('legend-baseline-regression.py'))
V = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(V)
NS = V.NS
TAG = '{' + NS['t'] + '}'
BASE = '262428f38c8e1dd0ee72a362983b277f493fd362'
ACTUAL = None
ACTUAL_ROOT = None


def element(parent, tag, **attrs):
    return ET.SubElement(parent, TAG + tag, attrs)


def manifest_for(root, source_commit, frozen):
    tests = []
    for result in root.findall('t:Results/t:UnitTestResult', NS):
        item = {'name': result.get('testName'), 'outcome': result.get('outcome')}
        if item['outcome'] == 'Failed':
            import re
            item['reason'] = re.search(r'; reason=([a-z0-9_]+);', V.message(result))[1]
            item['failureMessageSha256'] = V.digest(V.message(result).encode())
        tests.append(item)
    return {'schemaVersion': 1, 'sourceCommit': source_commit, 'productionBaseCommit': BASE,
            'allowedPolicyPaths': sorted(V.POLICY_PATHS), 'frozenFiles': frozen,
            'rosterSha256': V.roster_digest(item['name'] for item in tests), 'tests': tests}


class RegressionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name)
        self.source = self.path / 'repo'
        self.source.mkdir()
        self.frozen = self.source / 'frozen.cs'
        self.frozen.write_text('unchanged held-out contract\n')
        for args in (['init', '-q'], ['add', 'frozen.cs'],
                     ['-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'fixture']):
            subprocess.run(['git', *args], cwd=self.source, check=True, capture_output=True)
        commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=self.source, text=True).strip()
        self.started = datetime.now(timezone.utc) - timedelta(minutes=2)
        self.earliest = (self.started - timedelta(seconds=1)).isoformat()
        self.root = ET.Element(TAG + 'TestRun')
        element(self.root, 'Times', start=self.started.isoformat(), finish=(self.started + timedelta(seconds=10)).isoformat())
        results = element(self.root, 'Results')
        definitions = element(self.root, 'TestDefinitions')
        entries = element(self.root, 'TestEntries')
        outcomes = ['Failed'] * 9 + ['NotExecuted'] * 4 + ['Passed'] * 2
        for index, outcome in enumerate(outcomes):
            method = 'Case' + str(index) if index < 13 else 'DuplicateDisplay'
            name = V.PREFIX + method
            test_id, execution_id = 'test-' + str(index), 'execution-' + str(index)
            result = element(results, 'UnitTestResult', testName=name, testId=test_id,
                             executionId=execution_id, outcome=outcome, duration='00:00:01.000',
                             startTime=(self.started + timedelta(seconds=1)).isoformat(),
                             endTime=(self.started + timedelta(seconds=2)).isoformat())
            if outcome == 'Failed':
                reason = 'meaning_graph_component_unknown' if index < 7 else 'semantic_transition_not_supported'
                error = element(element(result, 'Output'), 'ErrorInfo')
                element(error, 'Message').text = 'Native capability required; reason=' + reason + '; stage=native_only_blocked'
            definition = element(definitions, 'UnitTest', id=test_id, name=name)
            element(definition, 'Execution', id=execution_id)
            element(definition, 'TestMethod', className=V.PREFIX[:-1], name=method)
            element(entries, 'TestEntry', testId=test_id, executionId=execution_id)
        summary = element(self.root, 'ResultSummary', outcome='Failed')
        element(summary, 'Counters', total='15', executed='11', passed='2', failed='9', notExecuted='0',
                aborted='0', error='0', timeout='0', inconclusive='0', passedButRunAborted='0',
                notRunnable='0', disconnected='0', warning='0', completed='0', inProgress='0', pending='0')
        self.manifest = manifest_for(self.root, commit, {'frozen.cs': V.digest(self.frozen.read_bytes())})
        self.runner = 1
        self.base = BASE

    def check(self):
        trx, manifest = self.path / 'run.trx', self.path / 'manifest.json'
        ET.ElementTree(self.root).write(trx, encoding='utf-8', xml_declaration=True)
        manifest.write_text(json.dumps(self.manifest))
        return V.validate(trx, manifest, self.source, self.runner, self.earliest, self.base)

    def rejected(self):
        with self.assertRaises((ValueError, KeyError)):
            self.check()

    def result(self, index=0):
        return self.root.find('t:Results', NS)[index]

    def test_exact_baseline_retains_nine_failed_outcomes_and_four_skips(self):
        report = self.check()
        self.assertEqual('AcceptedKnownBaseline', report['status'])
        self.assertEqual((9, 4, 15), (report['failed'], report['notExecuted'], report['total']))
        self.assertFalse(report['allTestsPassed'])
        self.assertEqual(9, len(report['knownFailures']))
        self.assertTrue(all('message' not in item and len(item['failureMessageSha256']) == 64
                            for item in report['knownFailures']))

    def test_known_failure_may_improve_but_is_not_relabelled_in_input(self):
        self.result().set('outcome', 'Passed')
        self.result().remove(self.result().find('t:Output', NS))
        counters = self.root.find('t:ResultSummary/t:Counters', NS)
        counters.set('failed', '8'); counters.set('passed', '3')
        self.assertEqual(8, self.check()['failed'])

    def test_changed_reason_and_changed_message_each_reject(self):
        error = self.result().find('t:Output/t:ErrorInfo/t:Message', NS)
        original = error.text
        error.text = original.replace('meaning_graph_component_unknown', 'sql_timeout')
        self.rejected()
        error.text = original + '; unexpected_provider_call=true'
        self.rejected()

    def test_new_failure_rejects(self):
        self.result(13).set('outcome', 'Failed')
        self.rejected()

    def test_new_skip_and_existing_skip_changed_to_pass_reject(self):
        self.result(13).set('outcome', 'NotExecuted'); self.rejected()
        self.result(13).set('outcome', 'Passed')
        self.result(9).set('outcome', 'Passed'); self.rejected()

    def test_incomplete_empty_and_duplicate_results_reject(self):
        original = deepcopy(self.root)
        for mutation in ('missing', 'duplicate', 'empty'):
            self.root = deepcopy(original)
            results = self.root.find('t:Results', NS)
            if mutation == 'missing': results.remove(results[0])
            elif mutation == 'duplicate': results.append(deepcopy(results[0]))
            else: results.clear()
            self.rejected()

    def test_unknown_outcome_aborted_counter_and_runner_exit_reject(self):
        self.result().set('outcome', 'Timeout'); self.rejected()
        self.result().set('outcome', 'Failed')
        counters = self.root.find('t:ResultSummary/t:Counters', NS)
        counters.set('aborted', '1'); self.rejected()
        counters.set('aborted', '0'); self.runner = 0; self.rejected()

    def test_roster_and_counter_mismatch_reject(self):
        self.manifest['rosterSha256'] = '0' * 64; self.rejected()
        self.manifest['rosterSha256'] = V.roster_digest(x['name'] for x in self.manifest['tests'])
        self.root.find('t:ResultSummary/t:Counters', NS).set('total', '16'); self.rejected()

    def test_stale_run_and_unfinished_result_reject(self):
        self.earliest = (self.started + timedelta(seconds=1)).isoformat(); self.rejected()
        self.earliest = (self.started - timedelta(seconds=1)).isoformat()
        self.result().set('endTime', (self.started + timedelta(seconds=11)).isoformat()); self.rejected()

    def test_source_hash_and_runtime_changes_reject(self):
        self.manifest['frozenFiles']['frozen.cs'] = '0' * 64; self.rejected()
        self.manifest['frozenFiles']['frozen.cs'] = V.digest(self.frozen.read_bytes())
        self.frozen.write_text('modified runtime contract'); self.rejected()

    def test_policy_changes_allowed_but_production_advance_expires(self):
        path = self.source / 'scripts/legend-baseline-regression.py'
        path.parent.mkdir(); path.write_text('# authorized policy-only change\n')
        subprocess.run(['git', 'add', str(path)], cwd=self.source, check=True, capture_output=True)
        self.assertEqual(9, self.check()['failed'])
        self.base = 'f' * 40; self.rejected()

    def test_result_definition_and_entry_mismatch_reject(self):
        self.result().set('executionId', 'foreign'); self.rejected()
        self.result().set('executionId', 'execution-0')
        self.root.find('t:TestEntries', NS)[0].set('testId', 'foreign'); self.rejected()

    def test_malformed_truncated_xml_and_cli_rejection_report(self):
        self.check()
        trx = self.path / 'run.trx'
        trx.write_bytes(trx.read_bytes()[:-30])
        output = self.path / 'report.json'
        result = subprocess.run(['python3', str(Path(V.__file__)), '--trx', str(trx),
            '--manifest', str(self.path / 'manifest.json'), '--source-root', str(self.source),
            '--runner-exit-code', '1', '--run-start-utc', self.earliest,
            '--production-base-sha', BASE, '--output', str(output)], capture_output=True)
        self.assertEqual(1, result.returncode)
        self.assertEqual('Rejected', json.loads(output.read_text())['status'])

    def test_runner_diagnostic_rejects(self):
        infos = element(self.root.find('t:ResultSummary', NS), 'RunInfos')
        element(infos, 'RunInfo', outcome='Error')
        self.rejected()

    def make_zero_failure(self):
        self.runner = 0
        for result in self.root.find('t:Results', NS):
            if result.get('outcome') == 'Failed':
                result.set('outcome', 'Passed')
                result.remove(result.find('t:Output', NS))
        self.root.find('t:ResultSummary', NS).set('outcome', 'Completed')
        counters = self.root.find('t:ResultSummary/t:Counters', NS)
        counters.set('failed', '0'); counters.set('passed', '11')

    def test_normal_zero_allows_changed_source_base_and_new_passing_test(self):
        self.make_zero_failure()
        self.frozen.write_text('future repaired runtime')
        self.base = 'f' * 40
        # Add an entirely new passing result and its execution metadata.
        result = deepcopy(self.result(13))
        name = V.PREFIX + 'NewPassingControl'
        result.set('testName', name)
        result.set('testId', 'future-test'); result.set('executionId', 'future-execution')
        self.root.find('t:Results', NS).append(result)
        definition = deepcopy(self.root.find('t:TestDefinitions', NS)[13])
        definition.set('name', name); definition.set('id', 'future-test')
        definition.find('t:Execution', NS).set('id', 'future-execution')
        definition.find('t:TestMethod', NS).set('name', 'NewPassingControl')
        self.root.find('t:TestDefinitions', NS).append(definition)
        element(self.root.find('t:TestEntries', NS), 'TestEntry', testId='future-test', executionId='future-execution')
        counters = self.root.find('t:ResultSummary/t:Counters', NS)
        counters.set('total', '16'); counters.set('executed', '12'); counters.set('passed', '12')
        for key in ('sourceCommit', 'frozenFiles', 'rosterSha256', 'productionBaseCommit', 'allowedPolicyPaths'):
            self.manifest.pop(key)
        report = self.check()
        self.assertEqual('AcceptedZeroFailureRun', report['status'])
        self.assertEqual((0, 4), (report['failed'], report['notExecuted']))
        self.assertFalse(report['allTestsPassed'])

    def test_normal_zero_allows_approved_skip_to_execute(self):
        self.make_zero_failure()
        self.result(9).set('outcome', 'Passed')
        counters = self.root.find('t:ResultSummary/t:Counters', NS)
        counters.set('executed', '12'); counters.set('passed', '12')
        self.assertEqual(3, self.check()['notExecuted'])

    def test_normal_zero_rejects_failure_new_skip_missing_result_and_counter_error(self):
        original = deepcopy(self.root)
        self.runner = 0; self.rejected()
        self.root = deepcopy(original); self.make_zero_failure()
        self.result(13).set('outcome', 'NotExecuted'); self.rejected()
        self.root = deepcopy(original); self.make_zero_failure()
        results = self.root.find('t:Results', NS); results.remove(results[0]); self.rejected()
        self.root = deepcopy(original); self.make_zero_failure()
        self.root.find('t:ResultSummary/t:Counters', NS).set('error', '1'); self.rejected()

    def test_actual_capture(self):
        if not ACTUAL:
            self.skipTest('Pass both actual capture arguments for observation')
        root = ET.parse(ACTUAL).getroot()
        source = Path(ACTUAL_ROOT)
        manifest = manifest_for(root, '5e2fca048d1cee2480b1f577d445cfb9d9d58c4e', {
            'AgentPortal.Tests/LegendFounderAiHeldOutOperationMatrixTests.cs': V.digest(
                (source / 'AgentPortal.Tests/LegendFounderAiHeldOutOperationMatrixTests.cs').read_bytes())})
        path = self.path / 'actual-manifest.json'; path.write_text(json.dumps(manifest))
        start = V.timestamp(root.find('t:Times', NS).get('start')) - timedelta(seconds=1)
        report = V.validate(ACTUAL, path, source, 1, start.isoformat(), BASE)
        self.assertEqual((len(root.findall('t:Results/t:UnitTestResult', NS)), 9, 4),
                         (report['total'], report['failed'], report['notExecuted']))
        self.assertEqual('AcceptedKnownBaseline', report['status'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--actual-trx')
    parser.add_argument('--actual-source-root')
    args, remaining = parser.parse_known_args()
    ACTUAL, ACTUAL_ROOT = args.actual_trx, args.actual_source_root
    if bool(ACTUAL) != bool(ACTUAL_ROOT):
        parser.error('Both actual capture arguments are required together')
    unittest.main(argv=[__file__, *remaining])
