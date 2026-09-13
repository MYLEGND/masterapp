#!/usr/bin/env python3
"""Validate complete zero-failure runs or an explicitly authorized known-failure baseline.

Manifest v1: frozenFiles {relative_path: sha256}, rosterSha256, tests [{name,
outcome, reason?, failureMessageSha256?}]. The roster digest is SHA256 of UTF-8
json.dumps(sorted(names), ensure_ascii=False, separators=(',', ':')). Failed
entries require the exact error-message hash and native reason. Four exact
NotExecuted entries remain unexecuted evidence. Manifest/source-tree approval
and selection of the current unfiltered run's TRX are the caller's release authority.
Zero-exit runs do not inherit the waiver's source/base/roster restrictions; they
require complete zero-failure evidence and only a subset of the four known skips.
"""
import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import sys
import subprocess
import xml.etree.ElementTree as ET

NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
PREFIX = 'AgentPortal.Tests.LegendFounderAiHeldOutOperationMatrixTests.'
OUTCOMES = {'Passed', 'Failed', 'NotExecuted'}
POLICY_PATHS = {
    '.github/workflows/agentportal-production-deploy.yml',
    '.github/legend-baseline-release.json',
    'scripts/legend-baseline-regression.py',
    'scripts/test-legend-baseline-regression.py',
    'docs/legend-connect/repair-20260910/baseline-release-policy.md',
    'AgentPortal.Tests/LegendFounderCurriculumSqlServerE2ETests.cs',
    'AgentPortal.Tests/LegendFounderAiContractTests.cs',
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def roster_digest(names):
    return digest(json.dumps(sorted(names), ensure_ascii=False, separators=(',', ':')).encode())


def timestamp(value):
    # .NET timestamps carry seven fractional digits; Python 3.9 accepts six.
    value = re.sub(r'(\.\d{6})\d+(?=[+-]|Z|$)', r'\1', value)
    value = datetime.fromisoformat(value.replace('Z', '+00:00'))
    require(value.tzinfo is not None, 'Timestamp must include timezone')
    return value.astimezone(timezone.utc)


def message(result):
    return result.findtext('t:Output/t:ErrorInfo/t:Message', '', NS)


def unique_json(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'Duplicate manifest JSON key: ' + key)
        result[key] = value
    return result


def validate(trx_path, manifest_path, source_root, runner_exit_code, run_start_utc, production_base_sha):
    manifest = json.loads(Path(manifest_path).read_text(), object_pairs_hook=unique_json)
    require(isinstance(manifest, dict), 'Manifest must be an object')
    require(manifest.get('schemaVersion') == 1, 'Unsupported manifest schema')
    waiver = runner_exit_code != 0
    if waiver:
        frozen = manifest.get('frozenFiles')
        require(isinstance(frozen, dict) and frozen, 'Frozen source hashes are required')
        source_root = Path(source_root).resolve()
        require(isinstance(manifest.get('sourceCommit'), str) and re.fullmatch('[0-9a-f]{40}', manifest['sourceCommit']), 'Invalid source commit')
        require(isinstance(manifest.get('productionBaseCommit'), str) and re.fullmatch('[0-9a-f]{40}', manifest['productionBaseCommit']), 'Invalid production base commit')
        require(production_base_sha == manifest['productionBaseCommit'], 'Production base advanced; baseline authorization expired')
        require(isinstance(manifest.get('allowedPolicyPaths'), list) and set(manifest['allowedPolicyPaths']) == POLICY_PATHS,
                'Policy path exception differs from the authorized set')
        for revision in (manifest['sourceCommit'], 'HEAD'):
            subprocess.run(['git', 'cat-file', '-e', revision + '^{commit}'], cwd=source_root, check=True, capture_output=True)
        for revisions in ([manifest['sourceCommit'], 'HEAD'], ['HEAD']):
            changed = subprocess.run(['git', 'diff', '--name-only', '-z', *revisions, '--'],
                                     cwd=source_root, check=True, capture_output=True).stdout.decode().split('\0')
            require(set(filter(None, changed)) <= POLICY_PATHS, 'Runtime source differs from the one-time authorized baseline')
        for relative, expected in frozen.items():
            require(isinstance(expected, str) and re.fullmatch('[0-9a-f]{64}', expected), 'Invalid source hash')
            path = (source_root / relative).resolve()
            require(not Path(relative).is_absolute() and path.is_relative_to(source_root), 'Invalid frozen source path')
            require(path.is_file() and digest(path.read_bytes()) == expected, 'Frozen source hash mismatch: ' + relative)
    baseline = manifest.get('tests')
    require(isinstance(baseline, list) and baseline, 'Explicit full test roster is required')
    for item in baseline:
        require(isinstance(item, dict) and isinstance(item.get('name'), str) and item['name'], 'Invalid roster identity')
        require(item.get('outcome') in OUTCOMES, 'Invalid baseline outcome')
    names = [item['name'] for item in baseline]
    known = [item for item in baseline if item['outcome'] == 'Failed']
    skipped = [item for item in baseline if item['outcome'] == 'NotExecuted']
    require(len(skipped) == 4 and len({item['name'] for item in skipped}) == 4, 'Exactly four explicit allowed skip identities are required')
    if waiver:
        require(manifest.get('rosterSha256') == roster_digest(names), 'Baseline roster hash mismatch')
        require(len(known) == 9 and len(skipped) == 4, 'Authorization requires exactly nine known failures and four skips')
        require(len({item['name'] for item in known + skipped}) == 13, 'Ambiguous exception identities')
        require(all(names.count(item['name']) == 1 for item in known + skipped), 'Exception identity duplicated in roster')
        require(Counter(item.get('reason') for item in known) == Counter({
            'meaning_graph_component_unknown': 7, 'semantic_transition_not_supported': 2}), 'Invalid authorized reason set')
        for item in known:
            require(item['name'].startswith(PREFIX), 'Failure is outside the authorized held-out suite')
            require(isinstance(item.get('failureMessageSha256'), str) and re.fullmatch('[0-9a-f]{64}', item['failureMessageSha256']), 'Exact failure message hash is required')

    trx_bytes = Path(trx_path).read_bytes()
    require(b'<!DOCTYPE' not in trx_bytes.upper() and b'<!ENTITY' not in trx_bytes.upper(), 'TRX entity declarations are forbidden')
    root = ET.fromstring(trx_bytes)
    require(root.tag == '{' + NS['t'] + '}TestRun', 'Unexpected TRX root')
    require(len(root.findall('t:Times', NS)) == 1 and len(root.findall('t:ResultSummary', NS)) == 1, 'Missing or duplicate run metadata')
    times = root.find('t:Times', NS)
    started, finished = timestamp(times.attrib['start']), timestamp(times.attrib['finish'])
    earliest = timestamp(run_start_utc)
    require(earliest <= started <= finished <= datetime.now(timezone.utc), 'Stale, unfinished, or future TRX run')
    summary = root.find('t:ResultSummary', NS)
    require(len(summary.findall('t:Counters', NS)) == 1, 'Missing or duplicate counters')
    for section in ('Results', 'TestDefinitions', 'TestEntries'):
        require(len(root.findall('t:' + section, NS)) == 1, 'Missing or duplicate TRX section: ' + section)
    results = root.findall('t:Results/t:UnitTestResult', NS)
    require(len(root.find('t:Results', NS)) == len(results), 'Unsupported result structure')
    require(bool(results), 'Empty test results')
    if waiver:
        require(Counter(r.get('testName') for r in results) == Counter(names), 'Full result roster differs from approved baseline')
    definitions = root.findall('t:TestDefinitions/t:UnitTest', NS)
    entries = root.findall('t:TestEntries/t:TestEntry', NS)
    require(len(definitions) == len(entries) == len(results), 'Incomplete test definitions or entries')
    definition_map = {}
    for definition in definitions:
        test_id = definition.get('id')
        require(test_id and test_id not in definition_map, 'Missing or duplicate definition ID')
        execution = definition.find('t:Execution', NS)
        method = definition.find('t:TestMethod', NS)
        require(execution is not None and method is not None, 'Missing definition execution or method')
        require(method.get('className') and method.get('name'), 'Missing qualified method identity')
        full_method = method.get('className') + '.' + method.get('name')
        require(definition.get('name') == full_method or definition.get('name', '').startswith(full_method + '('), 'Definition name does not match its method')
        definition_map[test_id] = (definition.get('name'), execution.get('id'))
    entry_pairs = [(item.get('testId'), item.get('executionId')) for item in entries]
    require(len(set(entry_pairs)) == len(entries), 'Duplicate test entries')
    execution_ids = set()
    actual_pairs = []
    counts = Counter()
    failures = []
    known_by_name = {item['name']: item for item in known}
    skipped_names = {item['name'] for item in skipped}
    for result in results:
        name, outcome = result.get('testName'), result.get('outcome')
        require(outcome in OUTCOMES, 'Incomplete or unsupported test outcome: ' + str(outcome))
        test_id, execution_id = result.get('testId'), result.get('executionId')
        require(execution_id and execution_id not in execution_ids, 'Missing or duplicate result execution ID')
        execution_ids.add(execution_id)
        require(definition_map.get(test_id) == (name, execution_id), 'Result/definition identity mismatch')
        actual_pairs.append((test_id, execution_id))
        require(started <= timestamp(result.attrib['startTime']) <= timestamp(result.attrib['endTime']) <= finished, 'Result timestamps outside completed run')
        require(bool(re.fullmatch(r'\d+:[0-5]\d:[0-5]\d(?:\.\d+)?', result.get('duration', ''))), 'Missing or malformed result duration')
        counts[outcome] += 1
        if waiver:
            require((name in skipped_names) == (outcome == 'NotExecuted'), 'New skip or changed approved skip: ' + name)
        elif outcome == 'NotExecuted':
            require(name in skipped_names, 'New skip: ' + name)
        if outcome == 'Failed':
            require(waiver, 'Zero-exit run contains a failed test: ' + name)
            expected = known_by_name.get(name)
            require(expected is not None, 'New failure: ' + name)
            error = message(result)
            reasons = re.findall(r'(?:^|;\s*)reason=([a-z0-9_]+)(?=;|$)', error)
            require(reasons == [expected['reason']], 'Known case changed failure reason: ' + name)
            require(digest(error.encode()) == expected['failureMessageSha256'], 'Known case changed failure message: ' + name)
            failures.append({'name': name, 'outcome': outcome, 'reason': reasons[0], 'failureMessageSha256': digest(error.encode())})
        elif outcome == 'Passed':
            require(result.find('t:Output/t:ErrorInfo', NS) is None, 'Passed result contains failure evidence')
    actual_skips = Counter(r.get('testName') for r in results if r.get('outcome') == 'NotExecuted')
    require(all(count == 1 for count in actual_skips.values()), 'Duplicate skipped test identity')
    # xUnit emits one RunInfo per failed/skipped test. Only those exact,
    # already-validated result summaries may accompany an accepted baseline.
    reported_diagnostics = set()
    actual_outcomes = {result.get('testName'): result.get('outcome') for result in results}
    for info in summary.findall('t:RunInfos/t:RunInfo', NS):
        match = re.fullmatch(r'\[xUnit\.net \d+:\d{2}:\d{2}(?:\.\d+)?\]\s+(.+) \[(FAIL|SKIP)\]',
                             info.findtext('t:Text', '', NS))
        require(match is not None, 'Unknown runner diagnostic')
        name, label = match.groups()
        require(name not in reported_diagnostics, 'Duplicate runner diagnostic')
        reported_diagnostics.add(name)
        require(actual_outcomes.get(name) == ('Failed' if label == 'FAIL' else 'NotExecuted'), 'Runner diagnostic disagrees with result')
        require(info.get('outcome') == ('Error' if label == 'FAIL' else 'Warning'), 'Runner diagnostic severity mismatch')
    require(Counter(actual_pairs) == Counter(entry_pairs), 'Result/entry identity mismatch')
    counters = summary.find('t:Counters', NS).attrib
    required_counters = set('total executed passed failed error timeout aborted inconclusive passedButRunAborted notRunnable notExecuted disconnected warning completed inProgress pending'.split())
    require(set(counters) == required_counters, 'Missing or unknown runner counters')
    expected_counts = {'total': len(results), 'executed': counts['Passed'] + counts['Failed'],
                       'passed': counts['Passed'], 'failed': counts['Failed']}
    for field, expected in expected_counts.items():
        require(field in counters and int(counters[field]) == expected, 'Counter mismatch: ' + field)
    # VSTest xUnit reports zero notExecuted despite the explicit NotExecuted results.
    require('notExecuted' in counters and int(counters['notExecuted']) in (0, counts['NotExecuted']), 'Skip counter mismatch')
    for field, value in counters.items():
        if field not in expected_counts and field != 'notExecuted':
            require(int(value) == 0, 'Nonzero runner counter: ' + field)
    require(summary.get('outcome') == ('Failed' if failures else 'Completed'), 'Run summary outcome mismatch')
    require(runner_exit_code == (1 if failures else 0), 'Runner exit disagrees with test outcomes')
    require(counts['Passed'] > 0, 'No passing test executed')
    return {'status': 'AcceptedKnownBaseline' if failures else 'AcceptedZeroFailureRun',
            'allTestsPassed': not failures and counts['NotExecuted'] == 0, 'failedOutcomesRetained': True,
            'total': len(results), 'passed': counts['Passed'], 'failed': counts['Failed'],
            'notExecuted': counts['NotExecuted'], 'knownFailures': failures,
            'skippedTests': sorted(r.get('testName') for r in results if r.get('outcome') == 'NotExecuted'),
            'rosterSha256': roster_digest(r.get('testName') for r in results),
            'trxSha256': digest(trx_bytes), 'manifestSha256': digest(Path(manifest_path).read_bytes()),
            'runStartUtc': started.isoformat(), 'runFinishUtc': finished.isoformat()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--trx', required=True)
    parser.add_argument('--manifest', required=True)
    parser.add_argument('--source-root', required=True)
    parser.add_argument('--runner-exit-code', type=int, required=True)
    parser.add_argument('--run-start-utc', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--production-base-sha', required=True)
    args = parser.parse_args()
    try:
        report = validate(args.trx, args.manifest, args.source_root, args.runner_exit_code, args.run_start_utc, args.production_base_sha)
        code = 0
    except (ValueError, KeyError, TypeError, OSError, ET.ParseError, subprocess.CalledProcessError) as error:
        report = {'status': 'Rejected', 'allTestsPassed': False, 'error': str(error)}
        code = 1
    Path(args.output).write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n')
    print(json.dumps(report, ensure_ascii=False))
    return code


if __name__ == '__main__':
    sys.exit(main())
