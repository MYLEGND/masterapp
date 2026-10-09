#!/usr/bin/env python3
"""Validate the read-only migration probe independently of immutable app ZIPs."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess


def authority():
    spec = importlib.util.spec_from_file_location('validation_resume', Path(__file__).with_name('validation-resume.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


AUTHORITY = authority()


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def manifest(directory, tool_revision, application_revision):
    root = directory.resolve(strict=True)
    entries = list(directory.rglob('*'))
    if (len(entries) > 10000 or any(path.is_symlink() or not path.resolve().is_relative_to(root)
                                   for path in entries)):
        raise ValueError('Migration probe artifact contains an unsafe path')
    data = json.loads((directory / 'manifest.json').read_text())
    expected = AUTHORITY.migration_probe_identity(tool_revision, application_revision)
    for key, value in expected.items():
        if data.get(key) != value:
            raise ValueError('Migration probe dependency identity mismatch: ' + key)
    files = data.get('files')
    if not isinstance(files, dict) or 'MigrationReleaseProbe.dll' not in files:
        raise ValueError('Migration probe inventory incomplete')
    actual = {str(path.relative_to(directory)): digest(path) for path in directory.rglob('*')
              if path.is_file() and path != directory / 'manifest.json'}
    if actual != files:
        raise ValueError('Migration probe immutable digest mismatch')
    return data


SQL_REHEARSAL_IMAGE = 'mcr.microsoft.com/mssql/server@sha256:db9a8fe3098b7e8bbde41106bdc7caee942e97124e5fdb71b872ca208de3092d'


def prepare_rehearsal_candidate(revision, directory, output):
    import secrets
    import time
    import uuid
    import tempfile
    token = os.environ.get('GITHUB_TOKEN') or os.environ.get('GH_TOKEN')
    repository = os.environ['GITHUB_REPOSITORY']
    preflight = AUTHORITY.approved_head_preflight(repository, revision, token)
    if not preflight['current']:
        raise ValueError('READINESS_APPROVED_BASE_CHANGED')
    approved = preflight['approvedHeadSha']
    if 'READINESS_SCHEMA = 1' not in AUTHORITY.git_show_file(approved, 'scripts/validation-resume.py'):
        print('LEGEND_REHEARSAL:NOT_REQUIRED:initial_approved_authority_rollout')
        return
    targets = AUTHORITY.readiness_targets(revision, approved)
    expected = AUTHORITY.readiness_identity(revision, approved, targets)
    deadline = time.monotonic() + AUTHORITY.READINESS_WAIT_SECONDS
    while True:
        with AUTHORITY.evidence_lookup_budget(deadline):
            baseline = AUTHORITY.readiness_observation_evidence(repository, revision, approved, targets, token)
        if baseline:
            break
        if time.monotonic() >= deadline:
            raise RuntimeError('READINESS_OBSERVATION_UNAVAILABLE')
        time.sleep(min(10, max(0, deadline - time.monotonic())))
    if baseline['pendingCount'] == 0:
        print('LEGEND_REHEARSAL:NOT_REQUIRED:no_pending_migration')
        return
    with AUTHORITY.evidence_lookup_budget(deadline):
        retained = AUTHORITY.migration_rehearsal_evidence(repository, revision, expected, token, baseline=baseline)
    if retained:
        print('LEGEND_REHEARSAL:REUSED:source=' + str(retained['runId']) + ':compatible_content_and_observed_baseline')
        return
    def module(name):
        spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + '.py'))
        value = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(value)
        return value
    directory.mkdir(parents=True, exist_ok=False)
    package = module('release-package')
    component = directory / 'migration'
    component.mkdir()
    # Canonical package owner produces the exact bundle once, before app fanout.
    if not package.restore_prepared_migration(revision, component, expected['identity']):
        package.build_component(revision, 'migration', component, reuse_rehearsal=False)
    (directory / 'observation.json').write_text(json.dumps(baseline, sort_keys=True) + '\n')
    if output:
        with open(output, 'a') as stream:
            stream.write('artifact=legend-rehearsal-bundle-' + expected['identity'] + '-a' + os.environ['GITHUB_RUN_ATTEMPT'] + '\n')
    print('LEGEND_REHEARSAL:PREPARED:immutable_migration_component')


def rehearse_candidate(revision, directory, output):
    import secrets
    import time
    import uuid
    import tempfile
    repository = os.environ['GITHUB_REPOSITORY']
    baseline = json.loads((directory / 'observation.json').read_text())
    token = os.environ.get('GITHUB_TOKEN') or os.environ.get('GH_TOKEN')
    preflight = AUTHORITY.approved_head_preflight(repository, revision, token)
    if not preflight['current']:
        raise ValueError('READINESS_APPROVED_BASE_CHANGED')
    targets = AUTHORITY.readiness_targets(revision, preflight['approvedHeadSha'])
    expected = AUTHORITY.readiness_identity(revision, preflight['approvedHeadSha'], targets)
    if baseline.get('candidate') != revision or any(baseline.get(k) != v for k, v in expected.items()):
        raise ValueError('READINESS_PREPARED_COMPONENT_INPUTS_CHANGED')
    def module(name):
        spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + '.py'))
        value = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(value)
        return value
    package = module('release-package')
    component = directory / 'migration'
    receipt = json.loads((component / 'migration.component.json').read_text())
    bundle = component / package.MIGRATION_BUNDLE
    expected_component = dict(schema=package.COMPONENT_SCHEMA, applicationReleaseSha=revision,
        packageContractSha256=package.contract_hash(), packageIdentity=package.package_identity(revision),
        component='migration', file=package.MIGRATION_BUNDLE, sha256=digest(bundle))
    if receipt != expected_component:
        raise ValueError('READINESS_PREPARED_COMPONENT_CORRUPT')
    run_id = int(os.environ['GITHUB_RUN_ID'])
    prepared_name = os.environ['PREPARED_MIGRATION_ARTIFACT']
    inventory = AUTHORITY.api_get(repository, f'actions/runs/{run_id}/artifacts?per_page=100', token)
    artifacts = inventory.get('artifacts', [])
    prepared = [row for row in artifacts if row.get('name') == prepared_name and row.get('expired') is False]
    if inventory.get('total_count') != len(artifacts) or len(prepared) != 1 or type(prepared[0].get('id')) is not int:
        raise ValueError('READINESS_PREPARED_COMPONENT_SOURCE_UNPROVEN')
    bundle_source = dict(runId=run_id, artifact=prepared_name, artifactId=prepared[0]['id'],
                         producingAttempt=int(prepared_name.rsplit('-a', 1)[1]))
    bundle.chmod(0o755)
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        proof = baseline['candidateProbe']
        AUTHORITY._download_run_artifact(repository, proof['runId'], proof['artifact'], root / 'probe', artifact_id=proof['artifactId'])
        manifest(root / 'probe', revision, revision)
        contract = root / 'probe/migration-contract.json'
        if digest(contract) != baseline['contractDigest']:
            raise ValueError('READINESS_CONTRACT_CHANGED')
        subprocess.run(['dotnet', 'publish', 'scripts/MigrationReleaseProbe/Rehearsal/MigrationRehearsal.csproj',
                        '-c', 'Release', '--nologo', '-o', str(root / 'fixture'), '-m:1', '-nr:false',
                        '-p:UseSharedCompilation=false'], check=True)
        nonce = uuid.uuid4().hex
        name = 'legend-rehearsal-' + nonce
        password = secrets.token_urlsafe(28) + 'Aa1!'
        env = os.environ | {'MSSQL_SA_PASSWORD': password, 'SQLCMDPASSWORD': password}
        def docker(*args, timeout=120):
            result = subprocess.run(['docker', *args], env=env, capture_output=True, text=True,
                                    timeout=timeout, check=False)
            if result.returncode:
                raise RuntimeError('Isolated SQL provider command failed')
            return result.stdout
        started = False
        try:
            docker('run', '--detach', '--name', name, '--platform', 'linux/amd64',
                   '-p', '127.0.0.1::1433', '-e', 'ACCEPT_EULA=Y', '-e', 'MSSQL_PID=Developer',
                   '-e', 'MSSQL_SA_PASSWORD', SQL_REHEARSAL_IMAGE)
            started = True
            ports = json.loads(docker('inspect', '--format', '{{json .NetworkSettings.Ports}}', name))
            binding = ports['1433/tcp']
            if len(binding) != 1 or binding[0]['HostIp'] != '127.0.0.1':
                raise RuntimeError('Isolated SQL loopback binding unproven')
            port = binding[0]['HostPort']
            if not port.isdigit() or not 1024 <= int(port) <= 65535:
                raise RuntimeError('Isolated SQL loopback port invalid')
            connection = ('Server=127.0.0.1,' + port + ';Database=LegendRehearsal_' + nonce +
                          ';User Id=sa;Password=' + password + ';Encrypt=True;TrustServerCertificate=True')
            end = time.monotonic() + 90
            while True:
                ready = subprocess.run(['docker', 'exec', '-e', 'SQLCMDPASSWORD', name,
                    '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-C', '-Q', 'SELECT 1'],
                    env=env, capture_output=True, timeout=10, check=False)
                if ready.returncode == 0:
                    break
                if time.monotonic() >= end:
                    raise RuntimeError('Isolated SQL readiness deadline exceeded')
                time.sleep(min(2, max(0, end - time.monotonic())))
            result = module('release-migration').rehearse(component / package.MIGRATION_BUNDLE,
                root / 'probe/MigrationReleaseProbe.dll', root / 'fixture/MigrationRehearsal.dll', connection, baseline)
        finally:
            if started:
                docker('rm', '--force', name, timeout=30)
    result.update(contractDigest=baseline['contractDigest'],
                  bundleSource=bundle_source, readinessIdentity=expected['identity'],
                  candidate=revision, producingRun=int(os.environ['GITHUB_RUN_ID']),
                  producingAttempt=int(os.environ['GITHUB_RUN_ATTEMPT']), sqlImage=SQL_REHEARSAL_IMAGE)
    (directory / 'rehearsal.json').write_text(json.dumps(result, sort_keys=True) + '\n')
    if output:
        with open(output, 'a') as stream:
            stream.write('artifact=legend-migration-rehearsal-' + expected['identity'] + '-a' + os.environ['GITHUB_RUN_ATTEMPT'] + '\n')
    print(json.dumps(result, sort_keys=True))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['build', 'plan', 'resolve', 'verify', 'prepare-rehearsal', 'rehearse'])
    parser.add_argument('--tool-revision', required=True)
    parser.add_argument('--application-revision', required=True)
    parser.add_argument('--directory', required=True, type=Path)
    parser.add_argument('--output')
    args = parser.parse_args()
    identity = AUTHORITY.migration_probe_identity(args.tool_revision, args.application_revision)
    if args.command == 'prepare-rehearsal':
        prepare_rehearsal_candidate(args.application_revision, args.directory, args.output)
        return
    if args.command == 'rehearse':
        rehearse_candidate(args.application_revision, args.directory, args.output)
        return
    if args.command == 'build':
        head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
        if head != args.tool_revision or args.tool_revision != args.application_revision:
            raise ValueError('Probe must build once from exact checked-out validated source')
        args.directory.mkdir(parents=True, exist_ok=False)
        subprocess.run(['dotnet', 'publish', 'scripts/MigrationReleaseProbe/MigrationReleaseProbe.csproj',
                        '-c', 'Release', '--nologo', '-o', str(args.directory), '-m:1', '-nr:false',
                        '-p:UseSharedCompilation=false'], check=True)
        exported = subprocess.run(['dotnet', str(args.directory / 'MigrationReleaseProbe.dll'), '--export-contract'],
                                  capture_output=True, text=True, timeout=60, check=False)
        if exported.returncode:
            raise ValueError('Candidate migration contract export failed')
        contract = json.loads(exported.stdout)
        (args.directory / 'migration-contract.json').write_text(json.dumps(contract, sort_keys=True) + '\n')
        identity['files'] = {str(path.relative_to(args.directory)): digest(path)
                             for path in args.directory.rglob('*') if path.is_file()}
        (args.directory / 'manifest.json').write_text(json.dumps(identity, sort_keys=True) + '\n')
        manifest(args.directory, args.tool_revision, args.application_revision)
        result = {'artifact': identity['artifact'], 'identity': identity['identity']}
    elif args.command in {'resolve', 'plan'}:
        try:
            result = AUTHORITY.migration_probe_evidence(os.environ['GITHUB_REPOSITORY'], identity)
        except AUTHORITY.EvidenceLookupUnavailable as exc:
            if args.command == 'resolve':
                raise
            result = {
                'reusable': False,
                'artifact': identity['artifact'],
                'identity': identity['identity'],
                'reason': 'historical_evidence_unavailable_build_fresh_probe',
                'evidenceHttpStatus': exc.code,
                'evidenceEndpoint': exc.endpoint,
            }
        result['needed'] = str(not result.get('reusable')).lower()
        if args.command == 'resolve' and not result.get('reusable'):
            raise ValueError('Validated migration probe missing; release cannot rebuild it')
    else:
        manifest(args.directory, args.tool_revision, args.application_revision)
        return
    if args.output:
        with open(args.output, 'a') as stream:
            for key in ('artifact', 'artifactId', 'identity', 'runId', 'needed'):
                if key in result:
                    stream.write(f'{key}={result[key]}\n')
    print(json.dumps(result, sort_keys=True))


if __name__ == '__main__':
    main()
