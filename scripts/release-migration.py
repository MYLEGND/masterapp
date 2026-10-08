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
    'PHYSICAL_SCHEMA_DRIFT': 'Production physical schema disagrees with validated EF history',
    'RUNTIME_INVALID_OPERATION': 'Schema probe runtime invalid operation; history status unknown',
}
OBSERVATION_ERRORS = set(PROBE_TERMINAL_REASONS.values()) | {
    'Schema probe process deadline exceeded',
    'Transient SQL schema read exhausted bounded retries',
    'Read-only schema proof unavailable; nontransient or unclassified failure',
}



# Stable operator codes for the *owning* first-write journal. Descriptions are
# locally authored, never sourced from SQL, GitHub logs, tokens or provider text.
MIGRATION_ADMISSION_DENIAL_CODES = {
    'Retained release child requires read-only reconciliation; no mutation replay authorized': 'RETAINED_INTENT_OR_SUCCESS',
    'Release child execution history unavailable': 'RELEASE_HISTORY_UNAVAILABLE',
    'Release child attempt history unavailable': 'RELEASE_ATTEMPT_INVENTORY_UNAVAILABLE',
    'Release child execution history incomplete': 'RELEASE_ATTEMPT_JOBS_INCOMPLETE',
    'Release child owner unproven': 'RELEASE_OWNER_NOT_PROVEN',
    'Release child execution detail unavailable': 'MIGRATION_STEP_NOT_PROVEN',
    'Release child original generation inventory incomplete': 'PRIOR_INTENT_INVENTORY_INCOMPLETE',
    'Release child original intent expired; no replay authorized': 'PRIOR_INTENT_EXPIRED',
    'Release child historical partition unproven; no replay authorized': 'PRIOR_PARTITION_UNPROVEN',
    'Release child prior partition operation unresolved; no replay authorized': 'PRIOR_PARTITION_UNRESOLVED',
    'Release child failed prepublication but the no-write proof was not authenticated': 'HISTORICAL_PREWRITE_PROOF_REJECTED',
    'Release child may have written; missing intent cannot authorize replay': 'PRIOR_EXECUTION_WITHOUT_INTENT',
    'Release child physical operation already entered; reconcile without replay': 'PHYSICAL_OPERATION_ALREADY_ENTERED',
    'Release child history truncated; no mutation authorized': 'RELEASE_HISTORY_TRUNCATED',
}


def safe_migration_admission_detail(exc):
    """Classify exact owned denial, never echo an exception's arbitrary text."""
    if type(exc) is RuntimeError:
        for message, code in MIGRATION_ADMISSION_DENIAL_CODES.items():
            match = re.fullmatch(
                re.escape(message) +
                r'(?: \[run=([1-9][0-9]{0,12})(?:;attempt=([1-9][0-9]{0,2}))?\])?',
                str(exc),
            )
            if match:
                result = 'LEGEND_MIGRATION_ADMISSION_DIAGNOSTIC:' + code
                if match.group(1):
                    result += ':run=' + match.group(1)
                if match.group(2):
                    result += ':attempt=' + match.group(2)
                return result
    # Do not reclassify a provider/transport exception as successful admission.
    return 'LEGEND_MIGRATION_ADMISSION_DIAGNOSTIC:UNCLASSIFIED_DENIAL'

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
        # New read-only probe generations can carry bounded public EF migration
        # identifiers to fence older completed operations. An absent fence is
        # backward-compatible for nonhistorical paths but grants NO exception.
        has_first = 'firstPendingMigrationId' in value
        has_last = 'lastAppliedMigrationId' in value
        if has_first != has_last:
            valid = False
        elif has_first:
            migration_id = re.compile(r'^[0-9]{8,14}_[A-Za-z0-9_]{1,128}$')
            first, last = value['firstPendingMigrationId'], value['lastAppliedMigrationId']
            if not (first is None or (isinstance(first, str) and migration_id.fullmatch(first))):
                valid = False
            if not (last is None or (isinstance(last, str) and migration_id.fullmatch(last))):
                valid = False
            if (value['pendingCount'] == 0) != (first is None):
                valid = False
            if value['appliedCount'] > 4 and last is None:
                valid = False
            if first is not None and last is not None and first <= last:
                valid = False
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
        if stage == 'mutation-admission':
            print(safe_migration_admission_detail(exc), flush=True)
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
    migration_fence = {}
    if 'firstPendingMigrationId' in before and 'lastAppliedMigrationId' in before:
        migration_fence = dict(
            first_pending_migration_id=before['firstPendingMigrationId'],
            last_applied_migration_id=before['lastAppliedMigrationId'],
        )
    migration_stage('mutation-admission', journal.before_mutation, observation,
                    **migration_fence)
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


def preflight(bundle, probe, connection):
    """Early, strictly READ-ONLY release gate. No intent or SQL write occurs here.

    The existing canonical first-write journal is queried using the same
    material/partition identities as actual bundle execution. Only the
    immutable validated binary can supply its material digest.
    """
    before = migration_stage('schema-observation', observe, probe, connection)
    if before['ready']:
        print('LEGEND_MIGRATION_READINESS:READY:pending=0', flush=True)
        return 'ready'

    if bundle is None or not bundle.is_file():
        raise RuntimeError('Migration stage unresolved: preparation')
    material = hashlib.sha256(json.dumps(dict(
        schemaIdentity=before['schemaIdentity'],
        bundleDigest=hashlib.sha256(bundle.read_bytes()).hexdigest(),
    ), sort_keys=True).encode()).hexdigest()
    partition = hashlib.sha256(json.dumps(dict(
        resourceGroup=os.environ['RELEASE_RESOURCE_GROUP'],
        authority=os.environ['DATABASE_AUTHORITY'],
    ), sort_keys=True).encode()).hexdigest()
    journal = migration_stage('child-history', journal_type(), 'migrations',
                              material, partition_identity=partition)
    if journal.intent is not None or journal.success is not None:
        raise RuntimeError('Migration stage unresolved: child-history')
    fence = {}
    if 'firstPendingMigrationId' in before and 'lastAppliedMigrationId' in before:
        fence = dict(first_pending_migration_id=before['firstPendingMigrationId'],
                     last_applied_migration_id=before['lastAppliedMigrationId'])
    migration_stage('mutation-admission',
        journal.authority.release_child_first_write_proven,
        journal.repository, journal.child, journal.identity, journal.material_identity,
        journal.run, journal.attempt, journal.token,
        partition_identity=journal.partition_identity,
        current_application_revision=journal.revision, **fence)
    print('LEGEND_MIGRATION_READINESS:READY:pending=' + str(before['pendingCount']), flush=True)
    return 'pending'



if __name__ == '__main__':
    try:
        release_authority().assert_protected_release_execution()
        import sys
        is_preflight = sys.argv[1:] == ['--preflight']
        if sys.argv[1:] and not is_preflight:
            raise RuntimeError('Migration stage unresolved: preparation')
        bundle = Path('/tmp/diagnostics-packages') / os.environ['MIGRATION_BUNDLE']
        probe = Path(os.environ.get('MIGRATION_PROBE_DLL', '/tmp/migration-probe/MigrationReleaseProbe.dll'))
        if not probe.is_file() or (not is_preflight and not bundle.is_file()):
            raise RuntimeError('Validated migration bundle or read-only probe unavailable')
        if is_preflight:
            result = preflight(bundle if bundle.is_file() else None, probe, connection_string())
        else:
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
