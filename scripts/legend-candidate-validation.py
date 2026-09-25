#!/usr/bin/env python3
"""Fixed, unprivileged candidate validation. Never dispatches or grants authority."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import signal
import selectors
import shutil
import io
import tarfile
import uuid
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
MAX_REPORT_BYTES = 10_000_000
SDK_IMAGE = 'mcr.microsoft.com/dotnet/sdk@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d'
# Official 10.0-noble manifest digest read from MCR on 2026-09-18.
# Linux x64 only; no host Docker socket or credential directory enters a container.


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
    if name in SOURCE_FILES:
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
            'mergeAuthorized': False, 'evidenceKind': 'isolated_candidate_reported_validation',
            'correctnessAuthenticated': False, 'reportTrust': 'candidate-reported-not-proof-of-correctness'}


def commands(candidate=None, output=None):
    project = '/source/AgentPortal.Tests/AgentPortal.Tests.csproj'
    artifacts = '-p:MasterAppArtifactsRoot=/state/artifacts'
    selection = '|'.join('FullyQualifiedName~AgentPortal.Tests.' + name for name in CLASSES)
    return [
        ('restore', ['dotnet', 'restore', project, artifacts, '--verbosity', 'minimal']),
        ('node', ['/usr/local/bin/node', '--test', '--test-concurrency=1', '--test-reporter=junit', *NODE_TESTS]),
        ('dotnet', ['dotnet', 'test', project, artifacts, '--no-restore', '--filter', selection,
                    '--logger', 'trx;LogFileName=contracts.trx', '--results-directory', '/state/reports',
                    '--verbosity', 'minimal']),
    ]


def clean_environment(home):
    # This is the host Docker CLI environment, never forwarded wholesale to containers.
    env = {key: os.environ[key] for key in ('PATH', 'LANG', 'LC_ALL') if key in os.environ}
    env.update(HOME=str(home), DOCKER_CONFIG=str(home / 'docker'),
               DOTNET_CLI_TELEMETRY_OPTOUT='1', GIT_TERMINAL_PROMPT='0')
    return env


def capture(command, env, remaining, maximum=MAX_REPORT_BYTES):
    """Bound output before allocation; kill the CLI on deadline/output overflow."""
    if remaining <= 0:
        raise Rejected('validation_deadline_exceeded')
    process = subprocess.Popen(command, env=env, stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT, start_new_session=True)
    deadline = time.monotonic() + remaining
    data = bytearray()
    try:
        with selectors.DefaultSelector() as selector:
            selector.register(process.stdout, selectors.EVENT_READ)
            while selector.get_map():
                if time.monotonic() >= deadline:
                    raise Rejected('validation_deadline_exceeded')
                for key, _ in selector.select(min(0.2, max(0, deadline - time.monotonic()))):
                    block = os.read(key.fileobj.fileno(), 65536)
                    if not block:
                        selector.unregister(key.fileobj)
                    elif len(data) + len(block) > maximum:
                        raise Rejected('validation_output_limit_exceeded')
                    else:
                        data.extend(block)
            code = process.wait(timeout=max(0.01, deadline - time.monotonic()))
        return code, bytes(data)
    finally:
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
        process.stdout.close()


def docker_checked(arguments, env, deadline, maximum=MAX_REPORT_BYTES):
    code, data = capture(['docker', *arguments], env, deadline - time.monotonic(), maximum)
    if code:
        raise Rejected('container_operation_failed:' + arguments[0])
    return data


def container_command(name, source, volume, node, command, network=False):
    # Only trusted restore may use the network. No candidate code is mounted in that phase.
    return ['run', '--name', name, '--platform', 'linux/amd64', '--pull=never',
            '--network=' + ('bridge' if network else 'none'), '--read-only',
            '--user', '65532:65532', '--cap-drop=ALL', '--security-opt=no-new-privileges',
            '--cpus=2', '--memory=3g', '--memory-swap=3g', '--pids-limit=256',
            '--ulimit', 'nofile=2048:2048', '--ulimit', 'fsize=268435456:268435456',
            '--log-driver=none', '--workdir=/source',
            '--mount', 'type=bind,source=' + str(source) + ',target=/source,readonly',
            '--mount', 'type=volume,source=' + volume + ',target=/state',
            '--mount', 'type=bind,source=' + str(node) + ',target=/usr/local/bin/node,readonly',
            '--tmpfs', '/tmp:rw,noexec,nosuid,size=134217728,uid=65532,gid=65532,mode=700',
            '--env', 'HOME=/state/home', '--env', 'DOTNET_CLI_HOME=/state/home',
            '--env', 'NUGET_PACKAGES=/state/nuget', '--env', 'DOTNET_CLI_TELEMETRY_OPTOUT=1',
            '--env', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1', '--env', 'DOTNET_NOLOGO=1',
            '--env', 'CI=true', '--env', 'NO_COLOR=1', SDK_IMAGE, *command]


def extract_trx(data):
    """Never extract candidate tar paths/symlinks into the parent filesystem."""
    try:
        with tarfile.open(fileobj=io.BytesIO(data), mode='r:') as archive:
            members = archive.getmembers()
            if len(members) != 1 or not members[0].isfile() or members[0].name != 'contracts.trx':
                raise Rejected('test_report_archive_invalid')
            if members[0].size > MAX_REPORT_BYTES:
                raise Rejected('test_report_invalid')
            return archive.extractfile(members[0]).read(MAX_REPORT_BYTES + 1)
    except tarfile.TarError as exc:
        raise Rejected('test_report_archive_invalid') from exc


def run_isolated(trusted, candidate, output, env, deadline):
    node = Path(shutil.which('node') or '/missing-node').resolve()
    if not node.is_file() or node.is_symlink() or ',' in str(node):
        raise Rejected('setup_node_binary_missing')
    for root in (trusted, candidate):
        if ',' in str(root):
            raise Rejected('mount_path_invalid')
    volume = 'legend-validation-' + uuid.uuid4().hex
    names = []
    volume_created = False
    try:
        docker_checked(['pull', '--platform=linux/amd64', SDK_IMAGE], env, deadline)
        volume_created = True  # Cleanup also covers an ambiguous create timeout.
        docker_checked(['volume', 'create', '--driver=local', '--opt=type=tmpfs',
                        '--opt=device=tmpfs', '--opt=o=size=2147483648,uid=65532,gid=65532,mode=700', volume], env, deadline)
        # Verify the setup-node 24 ELF actually executes inside the pinned Linux SDK image.
        for label, command in [('node-runtime', ['/usr/local/bin/node', '-p',
                "process.platform + ':' + process.arch + ':' + process.versions.node.split('.')[0]"]), *commands()]:
            name = volume + '-' + label
            names.append(name)
            source = trusted if label in ('restore', 'node-runtime') else candidate
            arguments = container_command(name, source, volume, node, command, network=label == 'restore')
            code, data = capture(['docker', *arguments], env, deadline - time.monotonic())
            if label == 'node-runtime':
                if code or data.strip() != b'linux:x64:24':
                    raise Rejected('setup_node_container_incompatible')
            else:
                (output / (label + '.log')).write_bytes(data)
                if code:
                    raise Rejected('command_failed:' + label + ':' + str(code))
                if label == 'dotnet':
                    archive = docker_checked(['cp', name + ':/state/reports/contracts.trx', '-'], env,
                                             deadline, MAX_REPORT_BYTES + 65536)
                    (output / 'contracts.trx').write_bytes(extract_trx(archive))
            # Killing/removing each container also kills any orphaned candidate child processes.
            docker_checked(['rm', '--force', name], env, deadline)
            names.remove(name)
    finally:
        # Cleanup has an independent bounded opportunity even after a validation timeout.
        cleanup_deadline = time.monotonic() + 30
        failed = False
        for name in names:
            try:
                docker_checked(['rm', '--force', name], env, cleanup_deadline)
            except (Rejected, OSError, subprocess.TimeoutExpired):
                failed = True
        if volume_created:
            try:
                docker_checked(['volume', 'rm', '--force', volume], env, cleanup_deadline)
            except (Rejected, OSError, subprocess.TimeoutExpired):
                failed = True
        if failed:
            raise Rejected('container_cleanup_unverified')


def xml_document(path):
    if path.is_symlink() or not path.is_file() or path.stat().st_size > MAX_REPORT_BYTES:
        raise Rejected('test_report_invalid')
    with path.open('rb') as stream:
        data = stream.read(MAX_REPORT_BYTES + 1)
    if len(data) > MAX_REPORT_BYTES or b'<!DOCTYPE' in data or b'<!ENTITY' in data:
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
        if os.environ.get('RUNNER_ENVIRONMENT') != 'github-hosted' or os.environ.get('RUNNER_OS') != 'Linux':
            raise Rejected('github_hosted_runner_required')
        result.update(runId=os.environ['GITHUB_RUN_ID'], runAttempt=os.environ['GITHUB_RUN_ATTEMPT'],
                      runnerOS=os.environ['RUNNER_OS'], conclusion='failure', testCounts={},
                      sandboxImage=SDK_IMAGE, testNetwork='none', trustedTestsUnchanged=True)
        deadline = time.monotonic() + MAX_SECONDS
        with tempfile.TemporaryDirectory(prefix='legend-validation-') as folder:
            home = Path(folder)
            try:
                run_isolated(args.trusted_root.resolve(), args.candidate_root.resolve(),
                             output.parent, clean_environment(home), deadline)
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
