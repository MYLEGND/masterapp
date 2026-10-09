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
    'MIGRATION_COMPATIBILITY_UNPROVEN': 'Pending migration compatibility and recovery require review',
    'CANDIDATE_MIGRATION_CONTRACT_UNPROVEN': 'Candidate migration metadata differs from approved definitions',
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
        result = subprocess.run(['az', *args, '-o', 'json'], capture_output=True, text=True, check=False, timeout=120)
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


def observe(probe, connection, *, contract=None):
    env = os.environ | {'LEGEND_RELEASE_DB_CONNECTION': connection}
    # Retry only an explicitly classified transient SQL read failure.
    # All unknown, credential, invalid-schema and timeout failures remain fail-closed.
    for attempt in range(3):
        try:
            result = subprocess.run(['dotnet', str(probe)] + (['--contract', str(contract)] if contract else []), env=env, capture_output=True, text=True,
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


def bind_readiness(before, bundle_digest, receipt, *, completed_intent=False):
    """Fresh production observation binds retained expensive proof to this write."""
    for key in ('databaseIdentity', 'schemaIdentity'):
        if not re.fullmatch('[a-f0-9]{64}', str(before.get(key, ''))) or before[key] != receipt.get(key):
            raise RuntimeError('Migration stage unresolved: readiness-identity')
    rehearsal = receipt.get('rehearsal') or {}
    if receipt.get('pendingCount', 0) > 0 and rehearsal.get('bundleDigest') != bundle_digest:
        raise RuntimeError('Migration stage unresolved: readiness-bundle')
    if before.get('baselineIdentity') != receipt.get('baselineIdentity'):
        # Only an exact retained intent plus complete current physical proof can
        # reconcile lost acknowledgement. A changed baseline never grants replay.
        if not (before['ready'] and completed_intent and rehearsal.get('bundleDigest') == bundle_digest):
            raise RuntimeError('Migration stage unresolved: readiness-drift')
        return 'completed-intent-reconciled'
    if not re.fullmatch('[a-f0-9]{64}', str(before.get('baselineIdentity', ''))):
        raise RuntimeError('Migration stage unresolved: readiness-identity')
    return 'current-baseline-proven'


def production_readiness(before):
    authority = release_authority()
    repository = os.environ['GITHUB_REPOSITORY']
    candidate = os.environ['RELEASE_CANDIDATE_SHA']
    approved = os.environ['RELEASE_SHA']
    targets = json.loads(os.environ['SELECTED_TARGETS'])
    authority.selected_release_target_keys(targets)
    token = os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
    # Expensive proof need not expire with a database observation. The caller
    # performs a new observation and exact database/baseline comparison before
    # any mutation, preserving compatible builds and rehearsals across queues.
    with authority.evidence_lookup_budget(time.monotonic() + 120):
        scope = {key: before[key] for key in ('databaseIdentity', 'schemaIdentity', 'baselineIdentity')} if not before['ready'] else None
        proof = authority.readiness_evidence(repository, candidate, approved, targets, token, require_fresh=False, baseline=scope)
    if not proof:
        raise RuntimeError('Migration stage unresolved: readiness-missing')
    return proof['receipt']


def reconcile(bundle, probe, connection, *, observer=observe, journal_factory=None, execute=None, readiness=None):
    before = migration_stage('schema-observation', observer, probe, connection)
    material = hashlib.sha256(json.dumps(dict(schemaIdentity=before['schemaIdentity'],
        bundleDigest=hashlib.sha256(bundle.read_bytes()).hexdigest()), sort_keys=True).encode()).hexdigest()
    if journal_factory is None:
        partition = hashlib.sha256(json.dumps(dict(resourceGroup=os.environ['RELEASE_RESOURCE_GROUP'],
            authority=os.environ['DATABASE_AUTHORITY']), sort_keys=True).encode()).hexdigest()
        journal = migration_stage('child-history', journal_type(), 'migrations', material, partition_identity=partition)
    else:
        journal = migration_stage('child-history', journal_factory, 'migrations', material)
    if readiness is not None:
        bind_readiness(before, hashlib.sha256(bundle.read_bytes()).hexdigest(), readiness(before),
                       completed_intent=journal.intent is not None)
    observation = {'schemaIdentity': before['schemaIdentity']}
    if before['ready']:
        # This receipt proves desired state, never historical execution. The
        # explicit no-write marker prevents a later release mistaking this
        # successful observer for an unjournaled SQL mutation.
        if getattr(journal, 'intent', None) is None and (
                getattr(journal, 'success', None) is None or
                journal.success.get('observation', {}).get('providerVersion') == 'schema-ready-no-write'):
            observation['providerVersion'] = 'schema-ready-no-write'
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


def mutation_admission(before, bundle_digest=None):
    """One read-only admission policy, shared by PR readiness and release.

    Zero pending is absence of a required new write, not historical authorization
    or a reconstructed success receipt. Retained ambiguity never permits replay.
    """
    if before['ready']:
        return {'state': 'not-required', 'reason': 'fresh_history_and_physical_postconditions_complete'}
    if not isinstance(bundle_digest, str) or not re.fullmatch('[a-f0-9]{64}', bundle_digest):
        raise RuntimeError('Migration stage unresolved: preparation')
    material = hashlib.sha256(json.dumps(dict(schemaIdentity=before['schemaIdentity'],
        bundleDigest=bundle_digest), sort_keys=True).encode()).hexdigest()
    partition = hashlib.sha256(json.dumps(dict(resourceGroup=os.environ['RELEASE_RESOURCE_GROUP'],
        authority=os.environ['DATABASE_AUTHORITY']), sort_keys=True).encode()).hexdigest()
    journal = migration_stage('child-history', journal_type(), 'migrations', material, partition_identity=partition)
    if journal.intent is not None or journal.success is not None:
        raise RuntimeError('Migration stage unresolved: child-history')
    fence = {}
    if 'firstPendingMigrationId' in before and 'lastAppliedMigrationId' in before:
        fence = dict(first_pending_migration_id=before['firstPendingMigrationId'],
                     last_applied_migration_id=before['lastAppliedMigrationId'])
    migration_stage('mutation-admission', journal.authority.release_child_first_write_proven,
        journal.repository, journal.child, journal.identity, journal.material_identity,
        journal.run, journal.attempt, journal.token, partition_identity=journal.partition_identity,
        current_application_revision=journal.revision, **fence)
    return {'state': 'proven', 'materialIdentity': material, 'partitionIdentity': partition}


def preflight(bundle, probe, connection, *, observation=None):
    """Early, strictly READ-ONLY release gate. No intent or SQL write occurs here.

    The existing canonical first-write journal is queried using the same
    material/partition identities as actual bundle execution. Only the
    immutable validated binary can supply its material digest.
    """
    before = observation if observation is not None else migration_stage('schema-observation', observe, probe, connection)
    mutation_admission(before, hashlib.sha256(bundle.read_bytes()).hexdigest() if bundle and bundle.is_file() else None)
    print('LEGEND_MIGRATION_READINESS:READY:pending=' + str(before['pendingCount']), flush=True)
    return 'ready' if before['ready'] else 'pending'


def rehearse(bundle, probe, fixture, connection, baseline, *, execute=None):
    """Exercise exact bytes and lost-receipt recovery on a synthetic database.

    The caller owns disposal of the isolated SQL instance. This function cannot
    consume production credentials or run against arbitrary server identities.
    Fixture preparation is test code, never part of the production observer.
    """
    if (not re.fullmatch(r'Server=127\.0\.0\.1,[1-9][0-9]{3,4};Database=LegendRehearsal_[a-f0-9]{32};'
                         r'User Id=sa;Password=[A-Za-z0-9_!\-]{20,128};Encrypt=True;TrustServerCertificate=True', connection) or
        type(baseline.get('pendingCount')) is not int or not 1 <= baseline['pendingCount'] <= 1000 or
        not re.fullmatch('[a-f0-9]{64}', str(baseline.get('baselineIdentity', '')))):
        raise RuntimeError('Isolated representative migration fixture unavailable')
    env = {key: os.environ[key] for key in ('PATH', 'HOME', 'DOTNET_ROOT') if key in os.environ}
    env['LEGEND_ISOLATED_SQL'] = connection
    env['LEGEND_REHEARSAL_PENDING_COUNT'] = str(baseline['pendingCount'])

    def fixture_command(operation):
        result = subprocess.run(['dotnet', str(fixture), operation], env=env,
                                capture_output=True, text=True, timeout=150, check=False)
        if result.returncode:
            raise RuntimeError('Isolated representative migration fixture failed')

    fixture_command('initialize')
    before = observe(probe, connection)
    if any(before.get(key) != baseline.get(key) for key in
           ('schemaIdentity', 'baselineIdentity', 'pendingCount', 'firstPendingMigrationId', 'lastAppliedMigrationId')):
        raise RuntimeError('Isolated fixture differs from observed schema baseline')
    counts = {'mutations': 0, 'intents': 0, 'successReceipts': 0}

    class FaultJournal:
        intent = None
        success = None

        def before_mutation(self, *args, **kwargs):
            if counts['intents']:
                raise AssertionError('Duplicate migration intent')
            counts['intents'] += 1
            self.intent = {'phase': 'intent'}

        def record_success(self, *args):
            if args[0].get('providerVersion') == 'schema-ready-no-write':
                raise AssertionError('Executed migration was mislabeled as a no-write observation')
            counts['successReceipts'] += 1
            if counts['successReceipts'] == 1:
                raise TimeoutError('Injected isolated receipt acknowledgment loss')

    def apply(bundle, connection):
        counts['mutations'] += 1
        if execute is not None:
            execute(bundle, connection)
        else:
            result = subprocess.run([str(bundle), '--connection', connection],
                                    env=env | {'SQLCONNSTR_MasterAppDb': connection},
                                    capture_output=True, text=True, timeout=180, check=False)
            if result.returncode:
                raise RuntimeError('Isolated validated bundle failed')

    started = time.monotonic()
    journal = FaultJournal()
    try:
        reconcile(bundle, probe, connection, journal_factory=lambda *a: journal, execute=apply)
    except RuntimeError as error:
        if str(error) != 'Migration stage unresolved: success-receipt':
            raise
    else:
        raise AssertionError('Receipt failure injection was not exercised')
    outcome = reconcile(bundle, probe, connection, journal_factory=lambda *a: journal, execute=apply)
    fixture_command('verify')
    if outcome != 'preserved' or counts != {'mutations': 1, 'intents': 1, 'successReceipts': 2}:
        raise AssertionError('Isolated migration recovery replayed a completed operation')
    return dict(proven=True, isolated=True, syntheticDataOnly=True, counts=counts,
                schemaIdentity=before['schemaIdentity'],
                baselineIdentity=before['baselineIdentity'],
                bundleDigest=hashlib.sha256(bundle.read_bytes()).hexdigest(),
                elapsedSeconds=round(time.monotonic() - started, 3))



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
            result = reconcile(bundle, probe, connection_string(), readiness=production_readiness)
            print('Schema ready; validated migration child ' + result + '.')
    except Exception as exc:
        # Only locally constructed fixed labels may cross this boundary.
        stages = {'schema-observation', 'child-history', 'mutation-admission',
                  'bundle-execution', 'schema-verification', 'success-receipt',
                  'readiness-identity', 'readiness-bundle', 'readiness-drift', 'readiness-missing'}
        messages = {'Migration stage unresolved: ' + stage for stage in stages}
        messages.update(OBSERVATION_ERRORS)
        detail = str(exc) if type(exc) is RuntimeError and str(exc) in messages else 'Migration stage unresolved: preparation'
        raise SystemExit(detail + '; preserve prior evidence and reconcile without replay.') from None
