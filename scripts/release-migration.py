#!/usr/bin/env python3
"""Reconcile schema before invoking the exact validated migration bundle."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import random
import time


# Fixed, redacted classifications from the read-only .NET probe.
# These markers cannot authorize a migration or an automatic retry.
PROBE_TERMINAL_REASONS = {
    'MUTATION_ACTIVITY_UNPROVEN': 'Database mutation activity visibility unproven',
    'MUTATION_ACTIVITY_ACTIVE': 'Database mutation activity remains active',
    'SQL_QUERY_TIMEOUT': 'Schema metadata query exceeded its bounded SQL timeout',
    'SQL_READ_DEADLINE': 'Schema observation exceeded its bounded read deadline',
    'SQL_AUTH': 'Schema probe database authentication rejected',
    'SCHEMA_DRIFT': 'Schema probe detected migration schema drift',  # historical probe
    'INPUT_UNAVAILABLE': 'Schema probe input unavailable',
    'MIGRATIONS_MISSING': 'Validated probe reports no known migrations',
    'UNKNOWN_APPLIED_MIGRATION': 'Database contains applied migration history absent from validated bundle',
    'HISTORY_SEQUENCE_DRIFT': 'Applied database migration history is not in validated sequence',
    'MIGRATION_COMPATIBILITY_UNPROVEN': 'Pending migration compatibility and recovery require review',
    'CANDIDATE_MIGRATION_CONTRACT_UNPROVEN': 'Candidate migration metadata differs from approved definitions',
    'MERCHANT_CHECKOUT_DUPLICATE_IDENTITY_REQUIRES_REVIEW': 'Existing merchant checkout identities are duplicated; reconciliation is required before migration',
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


# Historical execution and current desired state are independent facts.
# This resolver never grants SQL execution; the existing first-write authority
# remains mandatory even when the result requires a new-write admission.
def resolve_migration_boundary(history, *, schema='unobserved', activity='unknown', action='validate'):
    if history not in {'proven-nonentry', 'proven-completion', 'outcome-unknown', 'active', 'invalid'}:
        raise ValueError('Unknown migration history state')
    if schema not in {'unobserved', 'complete', 'pending', 'inconsistent'} or activity not in {'unknown', 'active', 'settled'}:
        raise ValueError('Unknown migration observation state')
    if action not in {'validate', 'reconcile'}:
        raise ValueError('Unknown migration action')
    result = dict(historicalExecution=history, currentSchema=schema, sqlExecutionAuthorized=False)
    if history in {'invalid', 'active'} or schema == 'inconsistent':
        return dict(result, state='blocked', reason='evidence_invalid_or_activity_unresolved')
    if action == 'validate':
        return dict(result, state='requires-runtime-reconciliation' if history == 'outcome-unknown' else 'historical-proof-preserved',
                    reason='current_database_and_activity_must_be_observed')
    if activity != 'settled':
        return dict(result, state='blocked', reason='mutation_activity_unresolved')
    if schema == 'complete':
        return dict(result, state='preserve-without-sql', reason='fresh_history_and_physical_postconditions_complete')
    if schema == 'pending' and history == 'proven-nonentry':
        return dict(result, state='requires-first-write-admission', reason='pending_requires_existing_governed_admission')
    return dict(result, state='blocked', reason='historical_execution_requires_reconciliation')


MIGRATION_STAGES = frozenset({
    'schema-observation', 'child-history', 'mutation-admission', 'bundle-execution',
    'schema-verification', 'success-receipt', 'preparation', 'readiness-identity',
    'readiness-bundle', 'readiness-drift', 'readiness-missing', 'mutation-activity', 'observation-receipt',
    'probe-resolution', 'probe-restoration', 'probe-verification',
})


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


def observe(probe, connection, *, contract=None, activity=False, schema_only_activity=False, deadline=None):
    if activity and schema_only_activity:
        raise RuntimeError('Migration stage unresolved: preparation')
    env = os.environ | {'LEGEND_RELEASE_DB_CONNECTION': connection}
    # Retry only an explicitly classified transient SQL read failure.
    # All unknown, credential, invalid-schema and timeout failures remain fail-closed.
    for attempt in range(3):
        remaining = 60 if deadline is None else min(60, deadline - time.monotonic())
        if remaining <= 0:
            raise RuntimeError('Schema probe process deadline exceeded')
        try:
            result = subprocess.run(['dotnet', str(probe)] + (['--contract', str(contract)] if contract else []) + (['--activity'] if activity else ['--schema-only-activity'] if schema_only_activity else []), env=env, capture_output=True, text=True,
                                    timeout=remaining, check=False)
        except subprocess.TimeoutExpired:
            raise RuntimeError('Schema probe process deadline exceeded') from None
        if result.returncode == 0:
            break
        lines = result.stderr.strip().splitlines()
        marker = lines[0] if lines else ''
        # Exact, bounded migration identifiers only: never relay arbitrary
        # provider text, SQL, connection details or unvalidated extra lines.
        if len(lines) == 2 and marker in {
                'LEGEND_SCHEMA_PROBE:SQL_READ_DEADLINE',
                'LEGEND_SCHEMA_PROBE:SQL_QUERY_TIMEOUT'}:
            timing = re.fullmatch(
                r'LEGEND_SCHEMA_TIMING:(?:setup|contract|history|history-validation|'
                r'catalog-engineering|catalog-feeds|catalog-founder|catalog-contract|identity|'
                r'activity-connection|activity-permission|activity-migration-lock|'
                r'activity-schema-locks|activity-transactions):[0-9]{1,6}:[0-9]{1,6}',
                lines[1])
            if not timing:
                raise RuntimeError('Read-only schema proof unavailable; nontransient or unclassified failure')
            print(lines[1], flush=True)
        elif len(lines) == 2 and marker == 'LEGEND_SCHEMA_PROBE:UNKNOWN_APPLIED_MIGRATION':
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
            delay = 2 * (attempt + 1)
            if deadline is not None:
                delay = min(delay, max(0, deadline - time.monotonic()))
            time.sleep(delay)
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
        if activity and value.get('mutationActivity') != 'settled':
            raise RuntimeError('Migration stage unresolved: mutation-activity')
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


def history_audit():
    spec = importlib.util.spec_from_file_location('migration_history_observation',
        Path(__file__).with_name('release-migration-history-audit.py'))
    audit = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(audit)
    return audit


def production_observation(probe, connection, *, schema_only=False, clock=time.monotonic, sleep=time.sleep, jitter=random.uniform):
    """Settle canonical workers, authenticate history, then observe stable SQL."""
    authority = release_authority()
    repository = os.environ['GITHUB_REPOSITORY']
    token = os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
    audit = history_audit()
    with authority.evidence_lookup_budget(time.monotonic() + 120):
        current = authority.assert_release_execution()
        if current.get('run_attempt') != int(os.environ['GITHUB_RUN_ATTEMPT']):
            raise RuntimeError('Migration stage unresolved: mutation-activity')
        report = audit.audit(authority, repository, token, current_execution=current)
    if (report.get('completeHistory') is not True or
            any(row['code'] in audit.BLOCKED_CODES or row['code'] not in audit.STATUS_CODES
                for row in report['records'])):
        raise RuntimeError('Migration stage unresolved: mutation-admission')
    uncertain = {'HISTORICAL_EXECUTION_REQUIRES_RECONCILIATION',
                 'POSSIBLE_SQL_WRITE_REQUIRES_LIVE_FENCE', 'EF_MIGRATION_EXECUTION_ENTERED'}
    history = 'outcome-unknown' if any(row['code'] in uncertain for row in report['records']) else 'proven-nonentry'
    # Both observations bracket physical/history reads with activity visibility.
    # A changed applied prefix/target/catalog cannot reuse the first observation.
    # A busy application is not a permanent migration defect. Retry only this
    # read-only boundary; history lookup, compilation and rehearsal stay intact.
    # One deadline also caps observe's inner transient-read retries and process.
    deadline = clock() + 90
    for attempt in range(6):
        try:
            options = (dict(schema_only_activity=True, deadline=deadline) if schema_only
                       else dict(activity=True, deadline=deadline))
            first = observe(probe, connection, **options)
            second = observe(probe, connection, **options)
            break
        except RuntimeError as error:
            if (str(error) != PROBE_TERMINAL_REASONS['MUTATION_ACTIVITY_ACTIVE']
                    or attempt == 5 or clock() >= deadline):
                raise
            delay = min(2 ** attempt + jitter(0, 1), max(0, deadline - clock()))
            print('LEGEND_MIGRATION_OBSERVATION:ACTIVE:read-only-retry=' + str(attempt + 1), flush=True)
            sleep(delay)
    for key in ('databaseIdentity', 'schemaIdentity', 'baselineIdentity', 'ready', 'pendingCount'):
        if key not in first or first[key] != second.get(key):
            raise RuntimeError('Migration stage unresolved: readiness-drift')
    second['historicalObservedAt'] = time.time()
    second['historicalRunId'] = current['id']
    second['historicalRunAttempt'] = current['run_attempt']
    second['historicalApplicationRevision'] = os.environ['APPLICATION_RELEASE_SHA']
    second['historicalExecution'] = history
    second['historicalReconciliationSources'] = [
        dict({key: row[key] for key in ('run', 'attempt', 'code', 'sourceBlob') if key in row},
             **({key: row['evidence'][key] for key in ('stateArtifactId', 'admissionId', 'migrationRecords')} if 'evidence' in row else {}))
        for row in report['records'] if row['code'] in uncertain]
    second['historicalEvidenceIdentity'] = hashlib.sha256(
        json.dumps(report, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    return second


def production_observation_no_sql(probe, connection):
    # Complete history and paired physical proof remain identical; only the
    # activity fence is appropriate for a strictly non-mutating publication.
    return production_observation(probe, connection, schema_only=True)


def production_readiness(before):
    authority = release_authority()
    repository = os.environ['GITHUB_REPOSITORY']
    candidate = os.environ['RELEASE_CANDIDATE_SHA']
    approved = os.environ['RELEASE_SHA']
    targets = json.loads(os.environ['SELECTED_TARGETS'])
    # A routing-only fast-track release still observes the database, but has no
    # app targets. Empty scope is valid only for the already-ready no-SQL path.
    if not targets and before['ready'] and os.environ.get('GITHUB_REF') == 'refs/heads/' + authority.FAST_TRACK_PR_BASE:
        authority.selected_release_target_keys(targets, allow_empty=True)
    else:
        authority.selected_release_target_keys(targets)
    token = os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
    if before['ready']:
        # This is new current-policy observation, not relabelled premerge proof.
        # No rehearsal or first-write permission is needed when no SQL is needed.
        history = before.get('historicalExecution', 'invalid')
        if not re.fullmatch('[a-f0-9]{64}', str(before.get('historicalEvidenceIdentity', ''))):
            history = 'invalid'
        resolution = resolve_migration_boundary(history, schema='complete',
            activity=before.get('mutationActivity', 'unknown'), action='reconcile')
        if resolution['state'] != 'preserve-without-sql':
            raise RuntimeError('Migration stage unresolved: mutation-activity')
        return dict(before, state='executed-success', kind='current-schema-no-write',
                    candidate=candidate, executionAuthority=approved, targets=sorted(targets),
                    resolution=resolution)
    # Expensive proof need not expire with a database observation. The caller
    # performs a new observation and exact database/baseline comparison before
    # any mutation, preserving compatible builds and rehearsals across queues.
    if os.environ.get('GITHUB_REF') == 'refs/heads/' + authority.FAST_TRACK_PR_BASE:
        source = json.loads((Path(os.environ['FAST_TRACK_MIGRATION_DIRECTORY']) / 'source.json').read_text())
        if (source.get('proven') is not True or source.get('releaseAdmission') is not False
            or source.get('authorizesMutation') is not False or not isinstance(source.get('baseline'), dict)
            or source.get('source', {}).get('contentIdentity') != authority.package_component_manifest(approved, 'migration')['contentIdentity']):
            raise RuntimeError('Migration stage unresolved: readiness-missing')
        return dict(source['baseline'], rehearsal={'bundleDigest': source['bundleDigest']})
    with authority.evidence_lookup_budget(time.monotonic() + 120):
        scope = {key: before[key] for key in ('databaseIdentity', 'schemaIdentity', 'baselineIdentity')} if not before['ready'] else None
        proof = authority.readiness_evidence(repository, candidate, approved, targets, token, require_fresh=False, baseline=scope)
    if not proof:
        raise RuntimeError('Migration stage unresolved: readiness-missing')
    return proof['receipt']


def production_recovery(database_identity, *, read=None, now=None):
    """Verify Azure's managed restore window for the observed database, without writes."""
    from datetime import datetime, timezone, timedelta
    now = now or datetime.now(timezone.utc)
    point = now - timedelta(minutes=15)
    def azure(*args):
        result = subprocess.run(['az', *args, '--only-show-errors', '-o', 'json'],
            capture_output=True, text=True, timeout=60, check=False)
        if result.returncode:
            if any(code in result.stderr for code in ('AuthorizationFailed', 'Authorization_RequestDenied', 'Forbidden')):
                raise RuntimeError('FAST_TRACK_DATABASE_RECOVERY_AZURE_READ_DENIED')
            if any(code in result.stderr for code in ('SubscriptionNotFound', 'ResourceGroupNotFound', 'ResourceNotFound')):
                raise RuntimeError('FAST_TRACK_DATABASE_RECOVERY_RESOURCE_UNAVAILABLE')
            raise RuntimeError('FAST_TRACK_DATABASE_RECOVERY_UNAVAILABLE')
        return json.loads(result.stdout)
    read = read or azure
    # SQL and web apps can live in different subscriptions. Use the configured
    # canonical SQL resource, then independently match the live physical hash.
    resource = os.environ.get('RELEASE_DATABASE_SERVER_RESOURCE_ID', '')
    parts = re.fullmatch(r'/subscriptions/([a-fA-F0-9-]{36})/resourceGroups/([A-Za-z0-9_.() -]{1,90})/providers/Microsoft.Sql/servers/([A-Za-z0-9-]{1,128})', resource)
    if parts is None:
        raise RuntimeError('FAST_TRACK_DATABASE_RECOVERY_RESOURCE_UNPROVEN')
    subscription, resource_group, server_name = parts.groups()
    server = read('sql', 'server', 'show', '--ids', resource)
    if (not isinstance(server, dict) or str(server.get('id', '')).lower() != resource.lower()
        or str(server.get('resourceGroup', '')).lower() != resource_group.lower()
        or str(server.get('name', '')).lower() != server_name.lower()):
        raise RuntimeError('FAST_TRACK_DATABASE_RECOVERY_RESOURCE_UNPROVEN')
    servers = [server]
    matches = []
    candidate_identities = []
    for server in servers:
        name = server.get('name')
        if not isinstance(name, str) or not re.fullmatch('[A-Za-z0-9-]{1,128}', name):
            raise RuntimeError('FAST_TRACK_DATABASE_RECOVERY_UNAVAILABLE')
        group = server.get('resourceGroup')
        if not isinstance(group, str) or not re.fullmatch(r'[A-Za-z0-9_.() -]{1,90}', group):
            raise RuntimeError('FAST_TRACK_DATABASE_RESOURCE_GROUP_UNPROVEN')
        databases = read('sql', 'db', 'list', '-g', group, '-s', name, '--subscription', subscription)
        if not isinstance(databases, list):
            raise RuntimeError('FAST_TRACK_DATABASE_RECOVERY_UNAVAILABLE')
        for database in databases:
            db_name = database.get('name')
            if not isinstance(db_name, str):
                raise RuntimeError('FAST_TRACK_DATABASE_RECOVERY_UNAVAILABLE')
            identity = hashlib.sha256((name + '/' + db_name).encode()).hexdigest()
            candidate_identities.append(identity)
            if identity != database_identity:
                continue
            earliest = datetime.fromisoformat(str(database.get('earliestRestoreDate', '')).replace('Z', '+00:00'))
            if database.get('status') != 'Online' or not earliest <= point < now:
                raise RuntimeError('FAST_TRACK_DATABASE_RESTORE_WINDOW_UNPROVEN')
            policy = read('sql', 'db', 'str-policy', 'show', '-g', group, '-s', name, '-n', db_name, '--subscription', subscription)
            if type(policy.get('retentionDays')) is not int or policy['retentionDays'] < 7:
                raise RuntimeError('FAST_TRACK_DATABASE_RESTORE_WINDOW_UNPROVEN')
            matches.append(dict(databaseIdentity=database_identity, observedUtc=now.isoformat(),
                restorePointUtc=point.isoformat(), earliestRestoreUtc=earliest.isoformat(),
                retentionDays=policy['retentionDays'], restoreWindowVerified=True,
                restoreExecuted=False, authorizesMutation=False))
    if len(matches) != 1:
        print('FAST_TRACK_DATABASE_RECOVERY_INVENTORY:' + json.dumps(dict(
            observedIdentity=database_identity, serverCount=len(servers),
            databaseCount=len(candidate_identities), candidateIdentities=candidate_identities[:100]), sort_keys=True))
        raise RuntimeError('FAST_TRACK_DATABASE_RECOVERY_IDENTITY_UNPROVEN')
    return matches[0]


def retain_current_observation(receipt, bundle_digest, *, publisher=None):
    """Durable desired-state proof, never an execution receipt or SQL intent."""
    record = {key: receipt[key] for key in (
        'candidate', 'executionAuthority', 'databaseIdentity', 'schemaIdentity',
        'baselineIdentity', 'observedUtc', 'pendingCount', 'mutationActivity',
        'historicalExecution', 'historicalEvidenceIdentity', 'historicalReconciliationSources')}
    # Internal admission records are not part of the public observation schema.
    record['historicalReconciliationSources'] = [
        {key: value for key, value in row.items() if key != 'migrationRecords'}
        for row in record['historicalReconciliationSources']]
    record.update(schemaVersion=1, phase='observation', kind='current-schema-no-write',
                  bundleDigest=bundle_digest, sqlExecutionAuthorized=False,
                  producingRun=int(os.environ['GITHUB_RUN_ID']),
                  producingAttempt=int(os.environ['GITHUB_RUN_ATTEMPT']))
    identity = hashlib.sha256(json.dumps(record, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    record['observationIdentity'] = identity
    # An ambiguous acknowledgement stops this boundary. Reobservation is safe;
    # neither a missing observation nor its recovery authorizes a SQL command.
    return (publisher or journal_type()._publish)(
        'legend-migration-observation-' + identity, record, timeout=120)


def fast_track_migration_plan(observation, recovery, *, now=time.time):
    """Select by current physical/history state, never inherited source impact.

    Inputs come from this authenticated release job. Retained inputs must first
    pass load_preflight_observation; this function grants no mutation authority.
    """
    for key in ('databaseIdentity', 'schemaIdentity', 'baselineIdentity'):
        if not re.fullmatch('[a-f0-9]{64}', str(observation.get(key, ''))):
            raise RuntimeError('Migration stage unresolved: readiness-identity')
    pending = observation.get('pendingCount')
    if (type(pending) is not int or pending < 0
        or type(observation.get('ready')) is not bool
        or observation['ready'] != (pending == 0)):
        raise RuntimeError('Migration stage unresolved: readiness-drift')
    from datetime import datetime
    try:
        observed = datetime.fromisoformat(recovery['observedUtc'].replace('Z', '+00:00')).timestamp()
    except (KeyError, TypeError, ValueError, AttributeError):
        raise RuntimeError('Migration stage unresolved: readiness-missing') from None
    if (recovery.get('databaseIdentity') != observation['databaseIdentity']
        or recovery.get('restoreWindowVerified') is not True
        or recovery.get('restoreExecuted') is not False
        or recovery.get('authorizesMutation') is not False
        or not 0 <= now() - observed <= 900):
        raise RuntimeError('Migration stage unresolved: readiness-identity')
    if pending:
        return 'rehearsal-required'
    receipt = production_readiness(observation)
    if receipt.get('kind') != 'current-schema-no-write' or receipt.get('pendingCount') != 0:
        raise RuntimeError('Migration stage unresolved: readiness-missing')
    bind_readiness(observation, hashlib.sha256(b'legend:schema-ready-no-sql:v1').hexdigest(), receipt)
    return 'no-sql'


def reconcile_current_schema_no_sql(probe, connection, *, observer=None, readiness=None,
                                    journal_factory=None, publisher=None, planned=None):
    """Journal fresh, complete physical/history observation without requiring SQL bundle bytes.

    This path cannot execute SQL and cannot accept pending or unsettled schema.
    The caller authenticates the exact fast-track source and current plan.
    Pending migrations retain the original rehearsed-bundle path.
    """
    observer = observer or production_observation_no_sql
    readiness = readiness or production_readiness
    before = migration_stage('schema-observation', observer, probe, connection)
    if before.get('ready') is not True or type(before.get('pendingCount')) is not int or before['pendingCount'] != 0:
        raise RuntimeError('Migration stage unresolved: readiness-drift')
    if planned is not None:
        if fast_track_migration_plan(planned, planned.get('recoveryEvidence', {})) != 'no-sql':
            raise RuntimeError('Migration stage unresolved: readiness-drift')
        bind_readiness(before, hashlib.sha256(b'legend:schema-ready-no-sql:v1').hexdigest(), planned)
    receipt = migration_stage('readiness-missing', readiness, before)
    if receipt.get('kind') != 'current-schema-no-write' or receipt.get('pendingCount') != 0:
        raise RuntimeError('Migration stage unresolved: readiness-missing')
    # No executable migration component exists on this branch. This distinct
    # fixed marker cannot be confused with the SHA-256 of a migration bundle.
    no_sql_digest = hashlib.sha256(b'legend:schema-ready-no-sql:v1').hexdigest()
    material = hashlib.sha256(json.dumps(dict(schemaIdentity=before['schemaIdentity'],
        noSqlDigest=no_sql_digest), sort_keys=True).encode()).hexdigest()
    if journal_factory is None:
        partition = hashlib.sha256(json.dumps(dict(resourceGroup=os.environ['RELEASE_RESOURCE_GROUP'],
            authority=os.environ['DATABASE_AUTHORITY']), sort_keys=True).encode()).hexdigest()
        journal = migration_stage('child-history', journal_type(), 'migrations', material,
                                  partition_identity=partition)
    else:
        journal = migration_stage('child-history', journal_factory, 'migrations', material)
    bind_readiness(before, no_sql_digest, receipt,
                   completed_intent=journal.intent is not None)
    migration_stage('observation-receipt', publisher or retain_current_observation, receipt, no_sql_digest)
    observation = dict(schemaIdentity=before['schemaIdentity'], providerVersion='schema-ready-no-write')
    migration_stage('success-receipt', journal.record_success, observation)
    return 'preserved'


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
        bundle_digest = hashlib.sha256(bundle.read_bytes()).hexdigest()
        receipt = readiness(before)
        bind_readiness(before, bundle_digest, receipt, completed_intent=journal.intent is not None)
        if receipt.get('kind') == 'current-schema-no-write':
            migration_stage('observation-receipt', retain_current_observation, receipt, bundle_digest)
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
    if before.get('historicalExecution') == 'outcome-unknown':
        migration_fence['historical_observation'] = before
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
        current_application_revision=journal.revision, historical_observation=before, **fence)
    return {'state': 'proven', 'materialIdentity': material, 'partitionIdentity': partition}


def preflight_observation_scope(environment):
    keys = ('GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT', 'RELEASE_SHA',
            'APPLICATION_RELEASE_SHA', 'DATABASE_AUTHORITY', 'RELEASE_RESOURCE_GROUP')
    scope = {key: environment.get(key) for key in keys}
    if any(not isinstance(value, str) or not value for value in scope.values()):
        raise RuntimeError('Migration stage unresolved: preparation')
    return scope


def retain_preflight_observation(path, observation, *, environment=None, now=time.time):
    """Retain one trusted job's read for its admission check, never mutation."""
    scope = preflight_observation_scope(os.environ if environment is None else environment)
    record = dict(schemaVersion=1, scope=scope, observedAt=now(), observation=observation)
    path.write_text(json.dumps(record, sort_keys=True) + '\n')


def load_preflight_observation(path, *, environment=None, now=time.time):
    scope = preflight_observation_scope(os.environ if environment is None else environment)
    if path.stat().st_size > 4 * 1024 * 1024:
        raise RuntimeError('Migration stage unresolved: preparation')
    record = json.loads(path.read_text())
    stamp = record.get('observedAt')
    if (record.get('schemaVersion') != 1 or record.get('scope') != scope or
            type(stamp) not in (int, float) or not 0 <= now() - stamp <= 900 or
            not isinstance(record.get('observation'), dict)):
        raise RuntimeError('Migration stage unresolved: preparation')
    return record['observation']


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
        release_authority().assert_release_execution()
        import sys
        is_preflight = sys.argv[1:] == ['--preflight']
        no_sql = sys.argv[1:] == ['--no-sql']
        if sys.argv[1:] and not (is_preflight or no_sql):
            raise RuntimeError('Migration stage unresolved: preparation')
        if no_sql:
            authority = release_authority()
            baseline = os.environ.get('FAST_TRACK_RELEASE_BASELINE', '')
            revision = os.environ.get('GITHUB_SHA', '')
            if (os.environ.get('FAST_TRACK_SCHEMA_CHANGED') not in ('true', 'false')
                or os.environ.get('GITHUB_REF') != 'refs/heads/' + authority.FAST_TRACK_PR_BASE
                or not re.fullmatch('[a-f0-9]{40}', baseline)
                or not re.fullmatch('[a-f0-9]{40}', revision)
                or subprocess.run(['git', 'merge-base', '--is-ancestor', baseline, revision],
                                  capture_output=True).returncode != 0):
                raise RuntimeError('Migration stage unresolved: preparation')
            changed = subprocess.check_output(
                ['git', 'diff', '--name-only', baseline, revision], text=True).splitlines()
            if str(authority.fast_track_scope(changed)['schema']).lower() != os.environ['FAST_TRACK_SCHEMA_CHANGED']:
                raise RuntimeError('Migration stage unresolved: preparation')
            retained = os.environ.get('MIGRATION_READINESS_OBSERVATION')
            if not retained:
                raise RuntimeError('Migration stage unresolved: preparation')
            planned = load_preflight_observation(Path(retained))
            if fast_track_migration_plan(planned, planned.get('recoveryEvidence', {})) != 'no-sql':
                raise RuntimeError('Migration stage unresolved: readiness-drift')
        bundle = Path('/tmp/diagnostics-packages') / os.environ['MIGRATION_BUNDLE']
        probe = Path(os.environ.get('MIGRATION_PROBE_DLL', '/tmp/migration-probe/MigrationReleaseProbe.dll'))
        if not probe.is_file() or (not (is_preflight or no_sql) and not bundle.is_file()):
            raise RuntimeError('Validated migration bundle or read-only probe unavailable')
        if is_preflight:
            retained = os.environ.get('MIGRATION_READINESS_OBSERVATION')
            observation = (load_preflight_observation(Path(retained)) if retained else
                           (production_observation_no_sql(probe, connection_string())
                            if os.environ.get('FAST_TRACK_SCHEMA_CHANGED') == 'false' and
                            os.environ.get('GITHUB_REF') == 'refs/heads/Legend/fast-track-release'
                            else production_observation(probe, connection_string())))
            result = preflight(bundle if bundle.is_file() else None, probe,
                               None if observation is not None else connection_string(), observation=observation)
        elif no_sql:
            result = reconcile_current_schema_no_sql(probe, connection_string(), planned=planned)
            print('Schema ready; validated no-SQL migration child ' + result + '.')
        else:
            result = reconcile(bundle, probe, connection_string(),
                observer=production_observation, readiness=production_readiness)
            print('Schema ready; validated migration child ' + result + '.')
    except Exception as exc:
        # Only locally constructed fixed labels may cross this boundary.
        messages = {'Migration stage unresolved: ' + stage for stage in MIGRATION_STAGES}
        messages.update(OBSERVATION_ERRORS)
        detail = str(exc) if type(exc) is RuntimeError and str(exc) in messages else 'Migration stage unresolved: preparation'
        raise SystemExit(detail + '; preserve prior evidence and reconcile without replay.') from None
