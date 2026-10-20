#!/usr/bin/env python3
"""Validate the read-only migration probe independently of immutable app ZIPs."""
import argparse
import contextlib
import hashlib
import importlib.util
import json
import os
import re
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
    scope = AUTHORITY.readiness_scope(revision, approved)
    if scope['state'] == 'not-required':
        print('LEGEND_REHEARSAL:NOT_REQUIRED:' + scope['reason'])
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
    if retained and retained['receipt'].get('selectedPackage'):
        AUTHORITY.pinned_package_evidence(repository, revision, retained['receipt']['selectedPackage'], token)
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
    recovered = package.restore_prepared_migration(revision, component, expected['identity'])
    if not (component / 'migration.aggregate.json').exists():
        aggregate = package.restore_aggregate_migration(revision, component)
        if not aggregate and not recovered:
            package.build_component(revision, 'migration', component)
    binding = package.verify_prepared_migration(revision, component)
    if binding:
        AUTHORITY.pinned_package_evidence(repository, revision, binding, token)
    if retained and retained['receipt']['bundleDigest'] == digest(component / package.MIGRATION_BUNDLE):
        if binding is None:
            print('LEGEND_REHEARSAL:REUSED:compatible_prepared_bundle')
            return
        # New aggregate attribution needs no repeat SQL when the already proven
        # exact bytes and representative baseline remain compatible.
        (directory / 'rehearsal.reuse.json').write_text(json.dumps(retained, sort_keys=True) + '\n')
    (directory / 'observation.json').write_text(json.dumps(baseline, sort_keys=True) + '\n')
    if output:
        with open(output, 'a') as stream:
            stream.write('artifact=legend-rehearsal-bundle-' + expected['identity'] + '-a' + os.environ['GITHUB_RUN_ATTEMPT'] + '\n')
    print('LEGEND_REHEARSAL:PREPARED:immutable_migration_component')


@contextlib.contextmanager
def isolated_sql():
    """One disposable, loopback-only SQL owner for all migration rehearsals."""
    import secrets
    import time
    import uuid
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
        yield connection
    finally:
        if started:
            docker('rm', '--force', name, timeout=30)


def rehearse_candidate(revision, directory, output):
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
    selected_package = package.verify_prepared_migration(revision, component)
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
    reuse_path = directory / 'rehearsal.reuse.json'
    if reuse_path.exists():
        prior = json.loads(reuse_path.read_text())
        proven = AUTHORITY.migration_rehearsal_evidence(repository, revision, expected, token, baseline=baseline)
        if (not proven or proven['artifactId'] != prior['artifactId'] or
                proven['runId'] != prior['runId'] or proven['receipt']['bundleDigest'] != digest(bundle)):
            raise ValueError('READINESS_REHEARSAL_REUSE_UNPROVEN')
        result = dict(proven['receipt'], state='reused-success', executedMutations=0,
            executionSource=dict(runId=proven['runId'], artifactId=proven['artifactId'], artifact=proven['artifact']))
        record_rehearsal_result(result, selected_package, baseline, bundle_source, expected, revision, directory, output)
        return
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
        with isolated_sql() as connection:
            result = module('release-migration').rehearse(component / package.MIGRATION_BUNDLE,
                root / 'probe/MigrationReleaseProbe.dll', root / 'fixture/MigrationRehearsal.dll', connection, baseline)
    result.update(state='executed-success', executedMutations=result['counts']['mutations'])
    record_rehearsal_result(result, selected_package, baseline, bundle_source, expected, revision, directory, output)


def record_rehearsal_result(result, selected_package, baseline, bundle_source, expected, revision, directory, output):
    if selected_package is not None:
        result['selectedPackage'] = selected_package
    result.update(contractDigest=baseline['contractDigest'],
                  bundleSource=bundle_source, readinessIdentity=expected['identity'],
                  candidate=revision, producingRun=int(os.environ['GITHUB_RUN_ID']),
                  producingAttempt=int(os.environ['GITHUB_RUN_ATTEMPT']), sqlImage=SQL_REHEARSAL_IMAGE)
    (directory / 'rehearsal.json').write_text(json.dumps(result, sort_keys=True) + '\n')
    if output:
        with open(output, 'a') as stream:
            stream.write('artifact=legend-migration-rehearsal-' + expected['identity'] + '-a' + os.environ['GITHUB_RUN_ATTEMPT'] + '\n')
    print(json.dumps(result, sort_keys=True))


def trusted_fast_track_run(repository, run_id, attempt, token, *, active=False):
    """Production authority belongs only to the existing trusted workflow ref."""
    run = AUTHORITY.api_get(repository, f'actions/runs/{run_id}', token)
    if (type(run_id) is not int or run_id < 1 or type(attempt) is not int or attempt < 1
        or run.get('id') != run_id or run.get('run_attempt') != attempt
        or not re.fullmatch('[a-f0-9]{40}', str(run.get('head_sha', '')))
        or run.get('path') != '.github/workflows/parfait-cutover-live-readonly.yml'
        or run.get('head_branch') != AUTHORITY.FAST_TRACK_PR_BASE
        or run.get('event') != 'workflow_dispatch'
        or run.get('head_repository', {}).get('full_name') != repository
        or (active and run.get('status') != 'in_progress')):
        raise ValueError('FAST_TRACK_REHEARSAL_PRODUCER_UNPROVEN')
    return run


def fast_track_observation_source(repository, revision, run_id, current_attempt, token):
    """Resume an immutable successful trusted observation without Azure rereads."""
    run = trusted_fast_track_run(repository, run_id, current_attempt, token)
    inventory = AUTHORITY.api_get(repository, f'actions/runs/{run_id}/artifacts?per_page=100', token)
    rows = inventory.get('artifacts')
    if not isinstance(rows, list) or inventory.get('total_count') != len(rows):
        raise ValueError('FAST_TRACK_OBSERVATION_ARTIFACT_HISTORY_INCOMPLETE')
    prefix = 'parfait-readonly-evidence-' + revision + '-'
    matching = []
    for row in rows:
        name = row.get('name', '')
        if name.startswith(prefix):
            suffix = name[len(prefix):]
            if (not suffix.isdigit() or not 1 <= int(suffix) <= current_attempt
                or row.get('expired') is not False or type(row.get('id')) is not int or row['id'] < 1
                or row.get('workflow_run', {}).get('id') != run_id):
                raise ValueError('FAST_TRACK_OBSERVATION_ARTIFACT_UNPROVEN')
            matching.append((int(suffix), row))
    if not matching or len(set(a for a,r in matching)) != len(matching):
        raise ValueError('FAST_TRACK_OBSERVATION_ARTIFACT_UNPROVEN')
    attempt, artifact = max(matching, key=lambda value: value[0])
    jobs = AUTHORITY.api_get(repository, f'actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100', token)
    jobs = jobs if isinstance(jobs, dict) else {}
    owners = jobs.get('jobs')
    if not isinstance(owners, list) or jobs.get('total_count') != len(owners):
        raise ValueError('FAST_TRACK_OBSERVATION_JOB_HISTORY_INCOMPLETE')
    owners = [job for job in owners if job.get('name') == 'Non-mutating Azure, SQL schema and public runtime inventory']
    if len(owners) != 1 or owners[0].get('status') != 'completed' or owners[0].get('conclusion') != 'success':
        raise ValueError('FAST_TRACK_OBSERVATION_JOB_UNPROVEN')
    return dict(artifact=artifact['name'], artifactId=artifact['id'], attempt=attempt,
                toolRevision=run['head_sha'], artifacts=rows)

def verified_fast_track_observation(repository, run_id, source, observation_path):
    """Local planning input must equal the original immutable observer artifact."""
    import tempfile
    if observation_path.is_symlink() or observation_path.stat().st_size > 4 * 1024 * 1024:
        raise ValueError('FAST_TRACK_OBSERVATION_INPUT_UNPROVEN')
    report = json.loads(observation_path.read_text())
    with tempfile.TemporaryDirectory(prefix='legend-observation-proof-') as temporary:
        root = Path(temporary)
        AUTHORITY._download_run_artifact(repository, run_id, source['artifact'], root,
                                        artifact_id=source['artifactId'])
        original = root / 'sanitized-schema.json'
        if original.is_symlink() or original.stat().st_size > 4 * 1024 * 1024:
            raise ValueError('FAST_TRACK_OBSERVATION_INPUT_UNPROVEN')
        if report != json.loads(original.read_text()):
            raise ValueError('FAST_TRACK_OBSERVATION_INPUT_CHANGED')
    return report


def fast_track_baseline(report, revision, run_id, source):
    """Bind a same-run, non-mutating observation to both affected SQL hosts."""
    if (report.get('schemaVersion') != 1 or report.get('purpose') != 'read-only-schema-plan'
        or report.get('authorizesMutation') is not False or report.get('candidate') != revision
        or str(report.get('runId')) != str(run_id) or type(report.get('producingAttempt')) is not int
        or report['producingAttempt'] < 1 or report.get('sharedDatabase') is not True
        or set(report.get('applications', {})) != {'masterapp-parfait', 'masterapp-protect'}):
        raise ValueError('FAST_TRACK_OBSERVATION_IDENTITY_UNPROVEN')
    if (report.get('toolRevision') != source['toolRevision']
        or report.get('producingAttempt') != source['attempt']
        or not re.fullmatch('[a-f0-9]{64}', str(report.get('contractDigest', '')))
        or type(report.get('contractArtifactId')) is not int or report['contractArtifactId'] < 1
        or type(report.get('observerArtifactId')) is not int or report['observerArtifactId'] < 1
        or report['contractArtifactId'] == report['observerArtifactId']):
        raise ValueError('FAST_TRACK_OBSERVATION_TRUSTED_IDENTITY_UNPROVEN')
    builds = []
    for field, prefix in (('contractArtifactId', 'parfait-readonly-contract-'),
                          ('observerArtifactId', 'parfait-readonly-schema-observer-')):
        rows = [row for row in source['artifacts'] if row.get('id') == report[field]]
        match = re.fullmatch(prefix + revision + r'-([1-9][0-9]*)', rows[0].get('name', '')) if len(rows) == 1 else None
        if (not match or int(match[1]) > source['attempt'] or rows[0].get('expired') is not False
            or rows[0].get('workflow_run', {}).get('id') != run_id):
            raise ValueError('FAST_TRACK_OBSERVATION_INPUT_ARTIFACT_UNPROVEN')
        builds.append(int(match[1]))
    if len(set(builds)) != 1:
        raise ValueError('FAST_TRACK_OBSERVATION_INPUT_ATTEMPT_MISMATCH')
    rows = list(report['applications'].values())
    keys = ('databaseIdentity', 'schemaIdentity', 'baselineIdentity', 'ready', 'pendingCount',
            'firstPendingMigrationId', 'lastAppliedMigrationId')
    for row in rows:
        if (row.get('observed') is not True or type(row.get('ready')) is not bool
            or type(row.get('pendingCount')) is not int or not 0 <= row['pendingCount'] <= 1000
            or row['ready'] != (row['pendingCount'] == 0)
            or any(not re.fullmatch('[a-f0-9]{64}', str(row.get(key, '')))
                   for key in ('databaseIdentity', 'schemaIdentity', 'baselineIdentity'))
            or not re.fullmatch('[0-9]{8,14}_[A-Za-z0-9_]{1,128}', str(row.get('lastAppliedMigrationId', '')))
            or (row['pendingCount'] and not re.fullmatch('[0-9]{8,14}_[A-Za-z0-9_]{1,128}',
                str(row.get('firstPendingMigrationId', ''))))):
            raise ValueError('FAST_TRACK_OBSERVATION_SCHEMA_UNPROVEN')
    baseline = {key: rows[0].get(key) for key in keys}
    if any(row.get(key) != value for row in rows[1:] for key, value in baseline.items()):
        raise ValueError('FAST_TRACK_SQL_HOST_BASELINES_DIFFER')
    return baseline


def rehearse_fast_track(revision, directory, observation_path, probe_directory):
    """Prepare exact bundle bytes through existing owners; never admit production."""
    import tempfile
    if any(os.environ.get(key) for key in ('LEGEND_RELEASE_DB_CONNECTION',
        'SQLCONNSTR_MasterAppDb', 'ConnectionStrings__MasterAppDb', 'AZURE_CLIENT_ID',
        'AZURE_TENANT_ID', 'AZURE_SUBSCRIPTION_ID', 'AZURE_CLIENT_SECRET',
        'ACTIONS_ID_TOKEN_REQUEST_TOKEN', 'ACTIONS_ID_TOKEN_REQUEST_URL')):
        raise ValueError('FAST_TRACK_REHEARSAL_REQUIRES_CREDENTIAL_FREE_JOB')
    if (not re.fullmatch('[a-f0-9]{40}', revision)
        or subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip() != revision
        or os.environ.get('CANDIDATE_SHA') != revision):
        raise ValueError('FAST_TRACK_REHEARSAL_CANDIDATE_CHANGED')
    repository = os.environ['GITHUB_REPOSITORY']
    token = os.environ.get('GITHUB_TOKEN') or os.environ['GH_TOKEN']
    run_id = int(os.environ['GITHUB_RUN_ID'])
    attempt = int(os.environ['GITHUB_RUN_ATTEMPT'])
    run = trusted_fast_track_run(repository, run_id, attempt, token, active=True)
    if os.environ.get('GITHUB_SHA') != run['head_sha']:
        raise ValueError('FAST_TRACK_TRUSTED_WORKFLOW_REVISION_CHANGED')
    branch = AUTHORITY.api_get(repository, 'git/ref/heads/Legend/fast-track-release', token)
    base = branch.get('object', {}).get('sha', '')
    if not re.fullmatch('[a-f0-9]{40}', base):
        raise ValueError('FAST_TRACK_BASE_UNPROVEN')
    subprocess.run(['git', 'merge-base', '--is-ancestor', base, revision], check=True)
    changed = subprocess.check_output(['git', 'diff', '--name-only', base, revision], text=True).splitlines()
    scope = AUTHORITY.fast_track_scope(changed)
    source = fast_track_observation_source(repository, revision, run_id, attempt, token)
    report = verified_fast_track_observation(repository, run_id, source, observation_path)
    if report.get('producingAttempt') != source['attempt']:
        raise ValueError('FAST_TRACK_OBSERVATION_ATTEMPT_MISMATCH')
    baseline = fast_track_baseline(report, revision, run_id, source)
    manifest(probe_directory, revision, revision)
    if digest(probe_directory / 'migration-contract.json') != report['contractDigest']:
        raise ValueError('FAST_TRACK_OBSERVER_CONTRACT_CHANGED')
    directory.mkdir(parents=True, exist_ok=False)
    if not baseline['pendingCount']:
        (directory / 'rehearsal.json').write_text(json.dumps(dict(schemaVersion=1,
            purpose='fast-track-migration-rehearsal', state='not-required', releaseAdmission=False,
            authorizesMutation=False, candidate=revision, fastTrackHead=base, baseline=baseline,
            producingRun=run_id, producingAttempt=attempt, observationAttempt=source['attempt'],
            toolRevision=run['head_sha'], observationArtifactId=source['artifactId'],
            contractDigest=report['contractDigest'], observerArtifactId=report['observerArtifactId'])) + '\n')
        print('FAST_TRACK_MIGRATION_REHEARSAL:NOT_REQUIRED:current_schema_complete')
        return
    if not scope['schema']:
        raise ValueError('FAST_TRACK_PENDING_SCHEMA_OUTSIDE_CANDIDATE')
    def module(name):
        spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + '.py'))
        value = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(value)
        return value
    package = module('release-package')
    component = package.build_component(revision, 'migration', directory)
    with tempfile.TemporaryDirectory(prefix='legend-fast-track-fixture-') as temporary:
        fixture = Path(temporary)
        subprocess.run(['dotnet', 'publish', 'scripts/MigrationReleaseProbe/Rehearsal/MigrationRehearsal.csproj',
            '-c', 'Release', '--nologo', '-o', str(fixture), '-m:1', '-nr:false',
            '-p:UseSharedCompilation=false'], check=True)
        with isolated_sql() as connection:
            result = module('release-migration').rehearse(directory / package.MIGRATION_BUNDLE,
                probe_directory / 'MigrationReleaseProbe.dll', fixture / 'MigrationRehearsal.dll', connection, baseline)
    if (result.get('proven') is not True or result.get('bundleDigest') != component['sha256']
        or result.get('counts') != {'mutations': 1, 'intents': 1, 'successReceipts': 2}):
        raise ValueError('FAST_TRACK_EXACT_BUNDLE_REHEARSAL_UNPROVEN')
    result.update(schemaVersion=1, purpose='fast-track-migration-rehearsal', releaseAdmission=False,
        authorizesMutation=False, candidate=revision, fastTrackHead=base,
        producingRun=run_id, producingAttempt=attempt, observationRun=run_id, observationAttempt=source['attempt'],
        toolRevision=run['head_sha'], observationArtifactId=source['artifactId'],
        contractDigest=report['contractDigest'], observerArtifactId=report['observerArtifactId'],
        sqlImage=SQL_REHEARSAL_IMAGE, baseline=baseline,
        targets=[row['azure_app'] for row in scope['matrix']['include']])
    (directory / 'rehearsal.json').write_text(json.dumps(result, sort_keys=True) + '\n')
    print('FAST_TRACK_EXACT_BUNDLE_REHEARSAL:PASS:lost-receipt-recovery-without-repeat-sql')


def fast_track_probe_compatible(source, revision):
    """Bundle identity and observer/rehearsal implementation are separate proofs."""
    import ast
    probe_paths = ['scripts/MigrationReleaseProbe', 'Directory.Build.props', 'Directory.Packages.props', 'global.json',
                   '.github/workflows/parfait-cutover-live-readonly.yml']
    if subprocess.run(['git', 'diff', '--quiet', source, revision, '--', *probe_paths], capture_output=True).returncode:
        return False
    if AUTHORITY.migration_probe_identity(source, source)['runtimeIdentity'] != AUTHORITY.migration_probe_identity(revision, revision)['runtimeIdentity']:
        return False
    def function_contract(ref, path, name):
        parsed = ast.parse(AUTHORITY.git_show_file(ref, path))
        selected = [n for n in parsed.body if isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef)) and n.name == name]
        return ast.dump(selected[0], include_attributes=False) if len(selected) == 1 else None
    for path, name in (('scripts/release-migration.py', 'rehearse'),
                       ('scripts/migration-probe-package.py', 'rehearse_fast_track'),
                       ('scripts/migration-probe-package.py', 'isolated_sql'),
                       ('scripts/migration-probe-package.py', 'trusted_fast_track_run'),
                       ('scripts/migration-probe-package.py', 'fast_track_observation_source'),
                       ('scripts/migration-probe-package.py', 'verified_fast_track_observation'),
                       ('scripts/migration-probe-package.py', 'fast_track_baseline'),
                       ('scripts/migration-probe-package.py', 'manifest')):
        prior = function_contract(source, path, name)
        if prior is None or prior != function_contract(revision, path, name):
            return False
    return True


def restore_fast_track(revision, directory, probe_directory):
    """Restore candidate bytes from immutable receipts under trusted workflow runs."""
    import tempfile
    import shutil
    repository = os.environ['GITHUB_REPOSITORY']
    token = os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
    expected = AUTHORITY.package_component_manifest(revision, 'migration')
    if not expected['reusable']:
        raise ValueError('FAST_TRACK_MIGRATION_DEPENDENCIES_UNPROVEN')
    branch = AUTHORITY.FAST_TRACK_PR_BASE.replace('/', '%2F')
    for page in range(1, 11):
        payload = AUTHORITY.api_get(repository, f'actions/runs?branch={branch}&per_page=100&page={page}', token)
        runs = payload.get('workflow_runs')
        if not isinstance(runs, list):
            raise ValueError('FAST_TRACK_REHEARSAL_HISTORY_UNAVAILABLE')
        for listed in runs:
            if (listed.get('path') != '.github/workflows/parfait-cutover-live-readonly.yml'
                or listed.get('head_branch') != AUTHORITY.FAST_TRACK_PR_BASE
                or listed.get('event') != 'workflow_dispatch'
                or listed.get('head_repository', {}).get('full_name') != repository
                or listed.get('status') != 'completed' or listed.get('conclusion') != 'success'):
                continue
            run = trusted_fast_track_run(repository, listed['id'], listed['run_attempt'], token)
            if run.get('status') != 'completed' or run.get('conclusion') != 'success':
                raise ValueError('FAST_TRACK_REHEARSAL_PRODUCER_CHANGED')
            inventory = AUTHORITY.api_get(repository, f"actions/runs/{run['id']}/artifacts?per_page=100", token)
            rows = inventory.get('artifacts')
            if not isinstance(rows, list) or inventory.get('total_count') != len(rows):
                raise ValueError('FAST_TRACK_REHEARSAL_ARTIFACTS_INCOMPLETE')
            retained = {}
            for artifact in rows:
                match = re.fullmatch(r'parfait-fast-track-migration-([a-f0-9]{40})-([1-9][0-9]*)', artifact.get('name', ''))
                if not match or artifact.get('expired') is not False:
                    continue
                source, attempt = match[1], int(match[2])
                if (attempt > run['run_attempt'] or type(artifact.get('id')) is not int or artifact['id'] < 1
                    or artifact.get('workflow_run', {}).get('id') != run['id']):
                    raise ValueError('FAST_TRACK_REHEARSAL_ATTEMPT_UNPROVEN')
                if (source, attempt) in retained:
                    raise ValueError('FAST_TRACK_REHEARSAL_ARTIFACT_DUPLICATED')
                retained[source, attempt] = artifact
            # Candidate identity comes from the immutable artifact, never from
            # head_sha: that SHA belongs to the trusted workflow implementation.
            for source in dict.fromkeys(source for source, attempt in retained):
                attempt = max(a for candidate, a in retained if candidate == source)
                artifact = retained[source, attempt]
                if subprocess.run(['git', 'merge-base', '--is-ancestor', source, revision], capture_output=True).returncode:
                    continue
                prior = AUTHORITY.package_component_manifest(source, 'migration')
                if not prior['reusable'] or prior['contentIdentity'] != expected['contentIdentity']:
                    continue
                if not fast_track_probe_compatible(source, revision):
                    continue
                jobs_payload = AUTHORITY.api_get(repository, f"actions/runs/{run['id']}/attempts/{attempt}/jobs?per_page=100", token)
                jobs = jobs_payload.get('jobs')
                if not isinstance(jobs, list) or jobs_payload.get('total_count') != len(jobs):
                    raise ValueError('FAST_TRACK_REHEARSAL_JOBS_INCOMPLETE')
                owners = [j for j in jobs if j.get('name') == 'Rehearse exact migration bundle and recovery without production credentials']
                if len(owners) != 1 or owners[0].get('status') != 'completed' or owners[0].get('conclusion') != 'success':
                    raise ValueError('FAST_TRACK_REHEARSAL_JOB_UNPROVEN')
                observation = fast_track_observation_source(repository, source, run['id'], run['run_attempt'], token)
                with tempfile.TemporaryDirectory(prefix='legend-rehearsal-restore-') as temporary:
                    root = Path(temporary)
                    restored = root / 'bundle'
                    AUTHORITY._download_run_artifact(repository, run['id'], artifact['name'], restored, artifact_id=artifact['id'])
                    AUTHORITY._download_run_artifact(repository, run['id'], observation['artifact'], root / 'observation', artifact_id=observation['artifactId'])
                    report = json.loads((root / 'observation/sanitized-schema.json').read_text())
                    baseline = fast_track_baseline(report, source, run['id'], observation)
                    receipt = json.loads((restored / 'rehearsal.json').read_text())
                    if receipt.get('state') == 'not-required':
                        continue
                    component = json.loads((restored / 'migration.component.json').read_text())
                    bundle = restored / AUTHORITY.MIGRATION_BUNDLE_NAME
                    if (receipt.get('purpose') != 'fast-track-migration-rehearsal' or receipt.get('proven') is not True
                        or receipt.get('isolated') is not True or receipt.get('syntheticDataOnly') is not True
                        or receipt.get('authorizesMutation') is not False or receipt.get('releaseAdmission') is not False
                        or receipt.get('candidate') != source or receipt.get('toolRevision') != run['head_sha']
                        or receipt.get('producingRun') != run['id'] or receipt.get('producingAttempt') != attempt
                        or receipt.get('observationRun') != run['id'] or receipt.get('observationAttempt') != observation['attempt']
                        or receipt.get('observationArtifactId') != observation['artifactId']
                        or receipt.get('observerArtifactId') != report['observerArtifactId']
                        or receipt.get('contractDigest') != report['contractDigest'] or receipt.get('baseline') != baseline
                        or receipt.get('sqlImage') != SQL_REHEARSAL_IMAGE
                        or receipt.get('counts') != {'mutations': 1, 'intents': 1, 'successReceipts': 2}
                        or component.get('applicationReleaseSha') != source or component.get('component') != 'migration'
                        or digest(bundle) != receipt.get('bundleDigest') or component.get('sha256') != receipt.get('bundleDigest')):
                        raise ValueError('FAST_TRACK_EXACT_REHEARSAL_BYTES_UNPROVEN')
                    observer = next(row for row in observation['artifacts'] if row['id'] == report['observerArtifactId'])
                    AUTHORITY._download_run_artifact(repository, run['id'], observer['name'], root / 'probe', artifact_id=observer['id'])
                    manifest(root / 'probe', source, source)
                    if digest(root / 'probe/migration-contract.json') != report['contractDigest']:
                        raise ValueError('FAST_TRACK_OBSERVER_CONTRACT_CHANGED')
                    directory.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(bundle, directory / bundle.name)
                    (directory / bundle.name).chmod(0o700)
                    shutil.copyfile(restored / 'rehearsal.json', directory / 'rehearsal.json')
                    receipt['source'] = dict(runId=run['id'], runAttempt=attempt, artifactId=artifact['id'],
                        contentIdentity=expected['contentIdentity'], producerRevision=source, toolRevision=run['head_sha'])
                    (directory / 'source.json').write_text(json.dumps(receipt, sort_keys=True) + '\n')
                    shutil.copytree(root / 'probe', probe_directory, dirs_exist_ok=True)
                    return receipt
        if len(runs) < 100:
            break
    raise ValueError('FAST_TRACK_COMPATIBLE_REHEARSAL_MISSING')

def prepare_observed_fast_track(revision, directory, observation_path, probe_directory):
    """Reuse compatible rehearsal against fresh observation; build only if absent."""
    if any(os.environ.get(key) for key in ('LEGEND_RELEASE_DB_CONNECTION',
        'SQLCONNSTR_MasterAppDb', 'ConnectionStrings__MasterAppDb', 'AZURE_CLIENT_ID',
        'AZURE_TENANT_ID', 'AZURE_SUBSCRIPTION_ID', 'AZURE_CLIENT_SECRET',
        'ACTIONS_ID_TOKEN_REQUEST_TOKEN', 'ACTIONS_ID_TOKEN_REQUEST_URL')):
        raise ValueError('FAST_TRACK_REHEARSAL_REQUIRES_CREDENTIAL_FREE_JOB')
    if (not re.fullmatch('[a-f0-9]{40}', revision)
        or subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip() != revision
        or os.environ.get('CANDIDATE_SHA') != revision):
        raise ValueError('FAST_TRACK_REHEARSAL_CANDIDATE_CHANGED')
    repository = os.environ['GITHUB_REPOSITORY']
    token = os.environ.get('GITHUB_TOKEN') or os.environ['GH_TOKEN']
    run_id, attempt = int(os.environ['GITHUB_RUN_ID']), int(os.environ['GITHUB_RUN_ATTEMPT'])
    run = trusted_fast_track_run(repository, run_id, attempt, token, active=True)
    if os.environ.get('GITHUB_SHA') != run['head_sha']:
        raise ValueError('FAST_TRACK_TRUSTED_WORKFLOW_REVISION_CHANGED')
    source = fast_track_observation_source(repository, revision, run_id, attempt, token)
    report = verified_fast_track_observation(repository, run_id, source, observation_path)
    baseline = fast_track_baseline(report, revision, run_id, source)
    manifest(probe_directory, revision, revision)
    if digest(probe_directory / 'migration-contract.json') != report['contractDigest']:
        raise ValueError('FAST_TRACK_OBSERVER_CONTRACT_CHANGED')
    if report.get('producingAttempt') != source['attempt']:
        raise ValueError('FAST_TRACK_OBSERVATION_ATTEMPT_MISMATCH')
    if baseline.get('ready') is True and baseline.get('pendingCount') == 0:
        print('FAST_TRACK_REHEARSAL_NOT_REQUIRED:authenticated-current-schema-ready')
        # No new artifact: original SQL evidence remains with its original producer.
        return True
    try:
        proof = restore_fast_track(revision, directory, directory / 'preserved-probe')
    except ValueError as error:
        if str(error) != 'FAST_TRACK_COMPATIBLE_REHEARSAL_MISSING':
            raise
        rehearse_fast_track(revision, directory, observation_path, probe_directory)
        return False
    if proof.get('baseline') != baseline:
        raise ValueError('FAST_TRACK_REUSED_REHEARSAL_BASELINE_CHANGED')
    print('FAST_TRACK_REHEARSAL_REUSED:producer=' + proof['candidate'])
    # Keep original producer identity; never upload old bytes as a new rehearsal.
    return True


def test_schema_contract(directory):
    # Execute the same trusted offline boundary used before Production sign-in.
    # The future-entry cases run when that actual migration is in the assembly;
    # the observer-only precursor still exercises exact/unknown/lossless checks.
    observer = directory / 'trusted-contract-observer'
    subprocess.run(['dotnet', 'publish', 'scripts/MigrationReleaseProbe/MigrationReleaseProbe.csproj',
        '-c', 'Release', '--nologo', '-o', str(observer), '-m:1', '-nr:false',
        '-p:UseSharedCompilation=false'], check=True)
    env = {key: os.environ[key] for key in ('PATH', 'HOME', 'DOTNET_ROOT') if key in os.environ}
    tested = subprocess.run(['dotnet', str(observer / 'MigrationReleaseProbe.dll'), '--test-contract-compatibility'],
        env=env, capture_output=True, text=True, timeout=120, check=False)
    if tested.returncode or 'LEGEND_TRUSTED_CONTRACT_TESTS:PASS' not in tested.stdout.splitlines():
        raise RuntimeError('Trusted schema compatibility contract failed')
    exported = subprocess.run(['dotnet', str(observer / 'MigrationReleaseProbe.dll'), '--export-contract'],
        env=env, capture_output=True, text=True, timeout=120, check=True)
    current = json.loads(exported.stdout)
    reviewed_id = '20261020160000_AddReceptionistCoordination'
    entries = [row for row in current['migrations'] if row.get('id') == reviewed_id]
    present = Path('Infrastructure/Migrations/' + reviewed_id + '.cs').is_file()
    if present or entries:
        if (len(entries) != 1 or current['migrations'][-1] != entries[0]
            or hashlib.sha256(json.dumps(entries[0], sort_keys=True, separators=(',', ':'),
                ensure_ascii=True, allow_nan=False).encode()).hexdigest() != AUTHORITY.REVIEWED_RECEPTIONIST_ENTRY
            or 'LEGEND_REVIEWED_APPEND_TEST:PROVEN' not in tested.stdout.splitlines()):
            raise RuntimeError('Reviewed migration entry contract failed')
    subprocess.run(['dotnet', 'publish', 'scripts/MigrationReleaseProbe/Rehearsal/MigrationRehearsal.csproj',
        '-c', 'Release', '--nologo', '-o', str(directory), '-m:1', '-nr:false',
        '-p:UseSharedCompilation=false'], check=True)
    with isolated_sql() as connection:
        env = {key: os.environ[key] for key in ('PATH', 'HOME', 'DOTNET_ROOT') if key in os.environ}
        env.update(LEGEND_ISOLATED_SQL=connection, LEGEND_REHEARSAL_PENDING_COUNT='payment-and-successors')
        result = subprocess.run(['dotnet', str(directory / 'MigrationRehearsal.dll'), 'migration-test'],
            env=env, capture_output=True, text=True, timeout=300, check=False)
        if result.returncode:
            # Forward only a fixed stage and classification, never raw provider output.
            allowed = r'LEGEND_ISOLATED_FIXTURE:UNPROVEN:(?:dispatch|initialize|precondition|migrate|verify|adversarial):(?:SQL:-?[0-9]{1,10}|INPUT_UNAVAILABLE|MIGRATION_COMPATIBILITY_UNPROVEN|PHYSICAL_SCHEMA_DRIFT|MERCHANT_CHECKOUT_DUPLICATE_IDENTITY_REQUIRES_REVIEW|TIMEOUT|INVALID_OPERATION|UNCLASSIFIED)'
            exception_type = r'[A-Za-z_][A-Za-z0-9_.+]{0,159}'
            allowed += r'(?::TYPES:' + exception_type + r'(?:>' + exception_type + r'){0,3})?'
            allowed += r'(?::LOCATION:(?:UNKNOWN|(?:RehearsalFixture|CandidateContract|CandidateContract\.Structures)\.cs:[1-9][0-9]{0,4}))?'
            reasons = [line for line in result.stderr.splitlines() if re.fullmatch(allowed, line)]
            reason = reasons[0] if len(reasons) == 1 else 'LEGEND_ISOLATED_FIXTURE:UNPROVEN:dispatch:UNCLASSIFIED'
            raise RuntimeError('Synthetic payment migration SQL contract failed: ' + reason)
    print('LEGEND_PAYMENT_SQL_CONTRACT:PASS:synthetic-only:not-release-admission')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['build', 'plan', 'resolve', 'verify', 'prepare-rehearsal', 'rehearse', 'test-contract', 'fast-track-rehearse', 'fast-track-prepare'])
    parser.add_argument('--tool-revision', required=True)
    parser.add_argument('--application-revision', required=True)
    parser.add_argument('--directory', required=True, type=Path)
    parser.add_argument('--output')
    parser.add_argument('--observation', type=Path)
    parser.add_argument('--probe-directory', type=Path)
    args = parser.parse_args()
    if args.command in {'fast-track-rehearse', 'fast-track-prepare'}:
        if args.tool_revision != args.application_revision or args.observation is None or args.probe_directory is None:
            parser.error('fast-track-rehearse requires same exact revision, observation, and probe directory')
        if args.command == 'fast-track-prepare':
            reused = prepare_observed_fast_track(args.application_revision, args.directory, args.observation, args.probe_directory)
            with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
                output.write('reused=' + str(reused).lower() + '\n')
        else:
            rehearse_fast_track(args.application_revision, args.directory, args.observation, args.probe_directory)
        return
    if args.command == 'test-contract':
        test_schema_contract(args.directory)
        return
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
