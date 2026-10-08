#!/usr/bin/env python3
"""Reconcile schema before invoking the exact validated migration bundle."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import time


# Fixed, redacted classifications from the read-only .NET probe.
# These markers cannot authorize a migration or an automatic retry.
PROBE_TERMINAL_REASONS = {
    'SQL_AUTH': 'Schema probe database authentication rejected',
    'SCHEMA_DRIFT': 'Schema probe detected migration schema drift',  # historical probe
    'INPUT_UNAVAILABLE': 'Schema probe input unavailable',
    'MIGRATIONS_MISSING': 'Validated probe reports no known migrations',
    'UNKNOWN_APPLIED_MIGRATION': 'Database contains applied migration history absent from validated bundle',
    'HISTORY_SEQUENCE_DRIFT': 'Applied database migration history is not in validated sequence',
    'RUNTIME_INVALID_OPERATION': 'Schema probe runtime invalid operation; history status unknown',
}
OBSERVATION_ERRORS = set(PROBE_TERMINAL_REASONS.values()) | {
    'Schema probe process deadline exceeded',
    'Transient SQL schema read exhausted bounded retries',
    'Read-only schema proof unavailable; nontransient or unclassified failure',
}


def release_authority():
    spec = importlib.util.spec_from_file_location('release_execution_authority', Path(__file__).with_name('validation-resume.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def journal_type():
    spec = importlib.util.spec_from_file_location('release_journal', Path(__file__).with_name('release-operation-evidence.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.ChildJournal


def connection_string():
    def az(*args):
        result = subprocess.run(['az', *args, '-o', 'json'], capture_output=True, text=True, check=False)
        if result.returncode:
            raise RuntimeError('Database configuration observation unavailable')
        return json.loads(result.stdout)
    group, app = os.environ['RELEASE_RESOURCE_GROUP'], os.environ['DATABASE_AUTHORITY']
    rows = az('webapp', 'config', 'connection-string', 'list', '-g', group, '-n', app)
    connection = next((row['value'] for row in rows if row['name'] == 'MasterAppDb'), None)
    if not connection:
        settings = {row['name']: row['value'] for row in az('webapp', 'config', 'appsettings', 'list', '-g', group, '-n', app)}
        connection = next((settings[key] for key in ('ConnectionStrings__MasterAppDb', 'MasterAppDb', 'SQLCONNSTR_MasterAppDb') if settings.get(key)), None)
    if not connection or '\n' in connection:
        raise RuntimeError('Production SQL connection unavailable')
    return connection


def observe(probe, connection):
    env = os.environ | {'LEGEND_RELEASE_DB_CONNECTION': connection}
    # Retry only an explicitly classified transient SQL read failure.
    # All unknown, credential, invalid-schema and timeout failures remain fail-closed.
    for attempt in range(3):
        try:
            result = subprocess.run(['dotnet', str(probe)], env=env, capture_output=True, text=True,
                                    timeout=60, check=False)
        except subprocess.TimeoutExpired:
            raise RuntimeError('Schema probe process deadline exceeded') from None
        if result.returncode == 0:
            break
        lines = result.stderr.strip().splitlines()
        marker = lines[0] if lines else ''
        # Exact, bounded migration identifiers only: never relay arbitrary
        # provider text, SQL, connection details or unvalidated extra lines.
        if len(lines) == 2 and marker == 'LEGEND_SCHEMA_PROBE:UNKNOWN_APPLIED_MIGRATION':
            safe = re.fullmatch(
                r'LEGEND_SCHEMA_HISTORY:[1-9][0-9]{0,3}:'
                r'(?:[0-9]{8,14}_[A-Za-z0-9_]{1,128}|NONCANONICAL)'
                r'(?:,(?:[0-9]{8,14}_[A-Za-z0-9_]{1,128}|NONCANONICAL)){0,15}',
                lines[1],
            )
            if safe:
                print(lines[1], flush=True)
            else:
                raise RuntimeError('Read-only schema proof unavailable; nontransient or unclassified failure')
        elif len(lines) != 1:
            raise RuntimeError('Read-only schema proof unavailable; nontransient or unclassified failure')
        if marker == 'LEGEND_SCHEMA_PROBE:TRANSIENT_SQL_READ' and attempt < 2:
            time.sleep(2 * (attempt + 1))
            continue
        if marker == 'LEGEND_SCHEMA_PROBE:TRANSIENT_SQL_READ':
            raise RuntimeError('Transient SQL schema read exhausted bounded retries')
        classification = marker.removeprefix('LEGEND_SCHEMA_PROBE:')
        if marker.startswith('LEGEND_SCHEMA_PROBE:') and classification in PROBE_TERMINAL_REASONS:
            raise RuntimeError(PROBE_TERMINAL_REASONS[classification])
        raise RuntimeError('Read-only schema proof unavailable; nontransient or unclassified failure')
    try:
        value = json.loads(result.stdout)
        valid = (value['schemaVersion'] == 1 and type(value['ready']) is bool and
                 all(type(value[key]) is int and value[key] >= 0 for key in ('knownCount', 'appliedCount', 'pendingCount')) and
                 value['knownCount'] > 0 and value['knownCount'] == value['appliedCount'] + value['pendingCount'] and
                 value['ready'] == (value['pendingCount'] == 0) and
                 isinstance(value['schemaIdentity'], str) and len(value['schemaIdentity']) == 64 and
                 all(char in '0123456789abcdef' for char in value['schemaIdentity']))
        if not valid:
            raise ValueError()
        return value
    except (KeyError, ValueError, TypeError):
        raise RuntimeError('Invalid read-only schema proof') from None


def migration_stage(stage, action, *args, **kwargs):
    """Expose only fixed owning-stage labels, never exception/provider payloads."""
    try:
        return action(*args, **kwargs)
    except Exception as exc:
        # Fixed classifications only; never forward provider-controlled text.
        if stage == 'schema-observation' and type(exc) is RuntimeError and str(exc) in OBSERVATION_ERRORS:
            raise RuntimeError(str(exc)) from None
        raise RuntimeError('Migration stage unresolved: ' + stage) from None


def reconcile(bundle, probe, connection, *, observer=observe, journal_factory=None, execute=None):
    before = migration_stage('schema-observation', observer, probe, connection)
    material = hashlib.sha256(json.dumps(dict(schemaIdentity=before['schemaIdentity'],
        bundleDigest=hashlib.sha256(bundle.read_bytes()).hexdigest()), sort_keys=True).encode()).hexdigest()
    if journal_factory is None:
        partition = hashlib.sha256(json.dumps(dict(resourceGroup=os.environ['RELEASE_RESOURCE_GROUP'],
            authority=os.environ['DATABASE_AUTHORITY']), sort_keys=True).encode()).hexdigest()
        journal = migration_stage('child-history', journal_type(), 'migrations', material, partition_identity=partition)
    else:
        journal = migration_stage('child-history', journal_factory, 'migrations', material)
    observation = {'schemaIdentity': before['schemaIdentity']}
    if before['ready']:
        migration_stage('success-receipt', journal.record_success, observation)
        return 'preserved'
    # A retained success plus missing history is drift. A retained intent with
    # pending work is ambiguous. Neither authorizes rerunning an EF side effect.
    migration_stage('mutation-admission', journal.before_mutation, observation)
    if execute is None:
        env = os.environ | {'DOTNET_ENVIRONMENT': 'Development',
                            'ASPNETCORE_ENVIRONMENT': 'Development',
                            'SQLCONNSTR_MasterAppDb': connection}
        result = migration_stage('bundle-execution', subprocess.run, [str(bundle), '--connection', connection], env=env,
                                capture_output=True, text=True, timeout=600, check=False)
        # Provider output and argv may include connection strings. Do not print.
        if result.returncode:
            raise RuntimeError('Migration stage unresolved: bundle-execution')
    else:
        migration_stage('bundle-execution', execute, bundle, connection)
    after = migration_stage('schema-verification', observer, probe, connection)
    if not after['ready'] or after['schemaIdentity'] != before['schemaIdentity']:
        raise RuntimeError('Migration stage unresolved: schema-verification')
    migration_stage('success-receipt', journal.record_success, observation)
    return 'applied'


if __name__ == '__main__':
    try:
        release_authority().assert_protected_release_execution()
        bundle = Path('/tmp/diagnostics-packages') / os.environ['MIGRATION_BUNDLE']
        probe = Path(os.environ.get('MIGRATION_PROBE_DLL', '/tmp/migration-probe/MigrationReleaseProbe.dll'))
        if not bundle.is_file() or not probe.is_file():
            raise RuntimeError('Validated migration bundle or read-only probe unavailable')
        result = reconcile(bundle, probe, connection_string())
        print('Schema ready; validated migration child ' + result + '.')
    except Exception as exc:
        # Only locally constructed fixed labels may cross this boundary.
        stages = {'schema-observation', 'child-history', 'mutation-admission',
                  'bundle-execution', 'schema-verification', 'success-receipt'}
        messages = {'Migration stage unresolved: ' + stage for stage in stages}
        messages.update(OBSERVATION_ERRORS)
        detail = str(exc) if type(exc) is RuntimeError and str(exc) in messages else 'Migration stage unresolved: preparation'
        raise SystemExit(detail + '; preserve prior evidence and reconcile without replay.') from None
