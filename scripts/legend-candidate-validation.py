#!/usr/bin/env python3
"""Fixed, unprivileged candidate validation. Never dispatches or grants authority."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import signal
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET

VERSION = 'legend-candidate-validation.v1'
FIELDS = {'version', 'repository', 'baseSha', 'candidateSha', 'patchSha256',
          'requestId', 'profile', 'trustedWorkflowSha', 'approvalActionDigest'}
CLASSES = ('LegendCloudflareTransportTests', 'LegendCloudflareToolCallbackTests',
           'FounderSoftwareRepairBatchTests', 'FounderSoftwareRepairCompletionTests',
           'FounderRemediationRevocationTests')
NODE_TESTS = tuple('Legend-Cloudflare/tests/' + path for path in (
    'runtime/runtime.test.mjs', 'runtime/schema.test.mjs',
    'runtime/qualification.test.mjs', 'runtime/qualification-mode.test.mjs',
    'security/authenticate.test.mjs', 'security/approval-interop.test.mjs',
    'security/tool-broker.test.mjs', 'security/governance.test.mjs',
    'security/application.integration.mjs'))
SOURCE_FILES = {
    'Legend-Cloudflare/src/runtime/adapter.mjs',
    'Legend-Cloudflare/src/runtime/orchestrator.mjs',
    'Legend-Cloudflare/src/runtime/reliability.mjs',
    'Infrastructure/Messaging/LegendConnectCloudflareTransport.cs',
    'Domain/Messaging/LegendConnectContracts.cs',
}
DIFF_ARGS = ('diff', '--binary', '--full-index', '--no-ext-diff', '--no-textconv',
             '--no-renames', '--src-prefix=a/', '--dst-prefix=b/')
MAX_SECONDS = 900


class Rejected(ValueError):
    pass


def git(root, *args, check=True):
    env = {key: value for key, value in os.environ.items() if not key.startswith('GIT_')}
    env.update(GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull,
               GIT_TERMINAL_PROMPT='0')
    result = subprocess.run(['git', '-c', 'core.quotePath=true', '-c',
                             'core.hooksPath=/dev/null', '-C', str(root), *args],
                            env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            timeout=30, check=False)
    if check and result.returncode:
        raise Rejected('git_verification_failed:' + args[0])
    return result


def validate_request(request, repository):
    if not isinstance(request, dict) or set(request) != FIELDS:
        raise Rejected('request_fields_invalid')
    if request['version'] != VERSION or request['profile'] != 'cloudflare-contracts':
        raise Rejected('profile_invalid')
    if not isinstance(request['repository'], str) or not re.fullmatch(
            r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', request['repository']):
        raise Rejected('repository_invalid')
    if request['repository'] != repository:
        raise Rejected('repository_mismatch')
    for key in ('baseSha', 'candidateSha', 'trustedWorkflowSha'):
        if not isinstance(request[key], str) or not re.fullmatch('[0-9a-f]{40}', request[key]):
            raise Rejected('sha_invalid:' + key)
    for key in ('patchSha256', 'approvalActionDigest'):
        if not isinstance(request[key], str) or not re.fullmatch('[0-9a-f]{64}', request[key]):
            raise Rejected('digest_invalid:' + key)
    if not isinstance(request['requestId'], str) or not re.fullmatch(
            r'[A-Za-z0-9_-]{1,128}', request['requestId']):
        raise Rejected('request_id_invalid')


def eligible_path(name):
    path = PurePosixPath(name)
    if str(path) != name or '..' in path.parts or any(p.startswith('.') for p in path.parts):
        return False
    if name in SOURCE_FILES or name in {'AgentPortal.Tests/' + c + '.cs' for c in CLASSES}:
        return True
    if name.startswith('Legend-Cloudflare/tests/') and path.suffix in {'.mjs', '.json'}:
        return True
    return name.startswith('Docs/legend-cloudflare/') and path.suffix == '.md'


def verify(request, trusted, candidate, repository):
    validate_request(request, repository)
    trusted, candidate = Path(trusted).resolve(), Path(candidate).resolve()
    if trusted == candidate or trusted in candidate.parents or candidate in trusted.parents:
        raise Rejected('checkouts_must_be_separate')
    for root, expected in ((trusted, request['trustedWorkflowSha']), (candidate, request['candidateSha'])):
        if git(root, 'rev-parse', '--show-toplevel').stdout.decode().strip() != str(root):
            raise Rejected('checkout_root_invalid')
        if git(root, 'rev-parse', 'HEAD').stdout.decode().strip() != expected:
            raise Rejected('checkout_sha_mismatch')
        if git(root, 'diff', '--quiet', '--no-ext-diff', '--no-textconv', 'HEAD', '--', check=False).returncode:
            raise Rejected('checkout_dirty')
        if git(root, 'ls-files', '--others', '-z').stdout:
            raise Rejected('checkout_contains_untracked_files')
    if git(candidate, 'symbolic-ref', '-q', 'HEAD', check=False).returncode != 1:
        raise Rejected('candidate_must_be_detached')
    base, head = request['baseSha'], request['candidateSha']
    if git(candidate, 'merge-base', '--is-ancestor', request['trustedWorkflowSha'], base, check=False).returncode:
        raise Rejected('baseline_must_extend_trusted_revision')
    if base == head or git(candidate, 'merge-base', '--is-ancestor', base, head, check=False).returncode:
        raise Rejected('candidate_must_extend_baseline')
    patch = git(candidate, *DIFF_ARGS, base, head, '--').stdout
    if hashlib.sha256(patch).hexdigest() != request['patchSha256']:
        raise Rejected('patch_digest_mismatch')
    changed = git(candidate, 'diff', '--name-only', '--no-renames', '-z', base, head, '--').stdout
    cumulative = git(candidate, 'diff', '--name-only', '--no-renames', '-z', request['trustedWorkflowSha'], head, '--').stdout
    try:
        paths = [part.decode('utf-8', errors='strict') for part in changed.split(b'\0') if part]
        cumulative_paths = [part.decode('utf-8', errors='strict') for part in cumulative.split(b'\0') if part]
    except UnicodeDecodeError as exc:
        raise Rejected('path_encoding_invalid') from exc
    if not paths:
        raise Rejected('empty_candidate')
    # Inspect the entire staged tree, not just the final patch, so an earlier
    # untrusted batch commit cannot smuggle workflow/build/authority changes.
    for name in set(paths) | set(cumulative_paths):
        if not eligible_path(name):
            raise Rejected('privileged_review_required:' + name)
        for sha in (request['trustedWorkflowSha'], base, head):
            record = git(candidate, 'ls-tree', '-z', sha, '--', name).stdout
            if record and not record.startswith(b'100644 blob '):
                raise Rejected('file_mode_not_allowed:' + name)
    required = NODE_TESTS + tuple('AgentPortal.Tests/' + c + '.cs' for c in CLASSES)
    for name in required:
        if not (candidate / name).is_file() or (candidate / name).is_symlink():
            raise Rejected('required_test_missing:' + name)
    return {**request, 'changedFiles': paths, 'cumulativeChangedFiles': cumulative_paths, 'deploymentAuthorized': False,
            'mergeAuthorized': False, 'evidenceKind': 'unprivileged_validation_only'}


def commands(candidate, output):
    project = str(candidate / 'AgentPortal.Tests/AgentPortal.Tests.csproj')
    artifacts = '-p:MasterAppArtifactsRoot=' + str(output / 'dotnet-artifacts')
    selection = '|'.join('FullyQualifiedName~AgentPortal.Tests.' + name for name in CLASSES)
    return [
        ('node', ['node', '--test', '--test-concurrency=1', '--test-reporter=junit', *NODE_TESTS]),
        ('restore', ['dotnet', 'restore', project, artifacts, '--verbosity', 'minimal']),
        ('dotnet', ['dotnet', 'test', project, artifacts, '--no-restore', '--filter', selection,
                    '--logger', 'trx;LogFileName=contracts.trx', '--results-directory', str(output),
                    '--verbosity', 'minimal']),
    ]


def clean_environment(home):
    env = {key: os.environ[key] for key in ('PATH', 'LANG', 'LC_ALL', 'TMPDIR', 'SYSTEMROOT') if key in os.environ}
    env.update(HOME=str(home), DOTNET_CLI_HOME=str(home), NUGET_PACKAGES=str(home / 'nuget'),
               DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
               DOTNET_NOLOGO='1', CI='true', NO_COLOR='1', GIT_TERMINAL_PROMPT='0',
               GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_NOSYSTEM='1')
    return env


def execute(command, candidate, log, env, remaining):
    if remaining <= 0:
        raise Rejected('validation_deadline_exceeded')
    with log.open('wb') as stream:
        process = subprocess.Popen(command, cwd=candidate, env=env, stdout=stream,
                                   stderr=subprocess.STDOUT, start_new_session=True)
        try:
            code = process.wait(timeout=remaining)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired as exc:
                raise Rejected('cleanup_unverified') from exc
            raise Rejected('validation_deadline_exceeded')
    if code:
        raise Rejected('command_failed:' + command[0] + ':' + str(code))


def xml_document(path):
    data = path.read_bytes()
    if len(data) > 10_000_000 or b'<!DOCTYPE' in data or b'<!ENTITY' in data:
        raise Rejected('test_report_invalid')
    try:
        return ET.fromstring(data)
    except ET.ParseError as exc:
        raise Rejected('test_report_invalid') from exc


def test_counts(output):
    node = xml_document(output / 'node.log')
    cases = list(node.iter('testcase'))
    if not cases or any(c.find(tag) is not None for c in cases for tag in ('failure', 'error', 'skipped')):
        raise Rejected('node_tests_not_passed')
    trx = xml_document(output / 'contracts.trx')
    results = [e for e in trx.iter() if e.tag.rsplit('}', 1)[-1] == 'UnitTestResult']
    methods = [e for e in trx.iter() if e.tag.rsplit('}', 1)[-1] == 'TestMethod']
    discovered = {e.attrib.get('className', '').split(',')[0] for e in methods}
    if (not results or any(e.attrib.get('outcome') != 'Passed' for e in results)
            or any('AgentPortal.Tests.' + c not in discovered for c in CLASSES)):
        raise Rejected('dotnet_tests_not_passed_or_missing')
    return {'node': len(cases), 'dotnet': len(results)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=('verify', 'run'))
    for name in ('request', 'trusted-root', 'candidate-root', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    # GITHUB_REPOSITORY is provided by the trusted workflow, never the candidate.
    request = json.loads(args.request.read_text())
    result = verify(request, args.trusted_root, args.candidate_root, os.environ.get('GITHUB_REPOSITORY'))
    output = args.output.resolve()
    for root in (args.trusted_root.resolve(), args.candidate_root.resolve()):
        if output == root or root in output.parents:
            raise Rejected('output_must_be_outside_checkouts')
    output.parent.mkdir(parents=True, exist_ok=True)
    if args.operation == 'verify':
        result['commands'] = [command for _, command in commands(args.candidate_root.resolve(), output.parent)]
    else:
        if not re.fullmatch(r'[1-9][0-9]*', os.environ.get('GITHUB_RUN_ID', '')) or not re.fullmatch(
                r'[1-9][0-9]*', os.environ.get('GITHUB_RUN_ATTEMPT', '')):
            raise Rejected('github_run_identity_required')
        if os.environ.get('RUNNER_ENVIRONMENT') != 'github-hosted' or os.environ.get('RUNNER_OS') not in ('Linux', 'macOS'):
            raise Rejected('github_hosted_runner_required')
        result.update(runId=os.environ['GITHUB_RUN_ID'], runAttempt=os.environ['GITHUB_RUN_ATTEMPT'],
                      runnerOS=os.environ['RUNNER_OS'], conclusion='failure', testCounts={})
        deadline = time.monotonic() + MAX_SECONDS
        with tempfile.TemporaryDirectory(prefix='legend-validation-') as folder:
            home = Path(folder)
            try:
                for label, command in commands(args.candidate_root.resolve(), output.parent):
                    execute(command, args.candidate_root.resolve(), output.parent / (label + '.log'),
                            clean_environment(home), deadline - time.monotonic())
                result['testCounts'] = test_counts(output.parent)
                result['conclusion'] = 'success'
            except (Rejected, OSError) as exc:
                result['error'] = str(exc)
        # Hash refers to this canonical receipt without its artifactSha256 field.
        result['artifactSha256'] = hashlib.sha256(json.dumps(result, sort_keys=True,
            separators=(',', ':')).encode()).hexdigest()
    output.write_text(json.dumps(result, sort_keys=True, indent=2) + '\n')
    return 0 if result.get('conclusion', 'success') == 'success' else 1


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (Rejected, OSError, ValueError, subprocess.TimeoutExpired) as error:
        print('candidate_validation_rejected: ' + str(error), file=sys.stderr)
        sys.exit(1)
