#!/usr/bin/env python3
"""Read-only, whole-history migration admission diagnostic.

Enumerates every trusted release attempt in the canonical bounded GitHub
history. It never mutates SQL, release journals, workflow state, or deployment
resources. This is diagnostic evidence, NOT an alternative first-write authority.
Every failure is independently checked again by the canonical first-write gate.
"""
from __future__ import annotations

import concurrent.futures
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import sys
import urllib.parse

LEGACY_STEP = 'Apply additive diagnostics migrations before restarting apps'
MODERN_STEP = 'Synchronize canonical pre-publication resource lanes'
NOOP_CODES = {'LEGACY_NOOP_PROOF_REJECTED'}
BLOCKED_CODES = NOOP_CODES | {
    'HISTORICAL_PREWRITE_UNPROVEN', 'JOB_INVENTORY_INCOMPLETE',
    'RELEASE_JOB_MISSING', 'MIGRATION_STEP_MISSING',
    'EVIDENCE_READ_UNAVAILABLE', 'UNTRUSTED_PRODUCER',
    'EF_MIGRATION_EXECUTION_ENTERED', 'EF_PENDING_MODEL_CHANGE',
}
# Display names and classifications are authored here, never copied from raw
# GitHub/SQL/provider exception messages, logs, or artifact bodies.
STATUS_CODES = BLOCKED_CODES | {
    'LEGACY_NOOP_PROVEN', 'LEGACY_PREWRITE_PROVEN',
    'MODERN_PREWRITE_PROVEN', 'MODERN_NOOP_PROVEN',
    'POSSIBLE_SQL_WRITE_REQUIRES_LIVE_FENCE',
    'NO_MIGRATION_STEP_ENTERED', 'PREPUBLICATION_NOT_ENTERED',
}


def authority():
    path = Path(__file__).with_name('validation-resume.py')
    spec = importlib.util.spec_from_file_location('migration_audit_authority', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def blob_sha(source):
    data = source.encode('utf-8')
    return hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()


def original_output_lines(raw):
    # A workflow source echo is NOT a producer output; the original attestors
    # independently check checkout identity, marker, exact step and timestamps.
    return [line.partition('Z ')[2] for line in raw.splitlines()
            if re.match(r'^\d{4}-\d\d-\d\dT\d\d:\d\d:', line)]


def legacy_failure_reason(auth, repo, job, token, row):
    """Differentiate actual EF entry from pre-write uncertainty.

    A timestamped runtime 'Applying migration' line is evidence of attempted
    database migration, not success or permission to replay. Never disclose
    provider exceptions, SQL, connection details, or arbitrary original logs.
    """
    outputs = original_output_lines(auth._release_job_log(repo, job['id'], token))
    ids = []
    for message in outputs:
        match = re.fullmatch(
            r"Applying migration '([0-9]{8,14}_[A-Za-z0-9_]{1,128})'\.",
            message)
        if match and match.group(1) not in ids:
            ids.append(match.group(1))
    if ids:
        row['attemptedMigrationIds'] = ids
        return 'EF_MIGRATION_EXECUTION_ENTERED'
    if any('Microsoft.EntityFrameworkCore.Migrations.PendingModelChangesWarning'
           in message and ('System.InvalidOperationException:' in message
                           or 'An error was generated for warning' in message)
           for message in outputs):
        return 'EF_PENDING_MODEL_CHANGE'
    return 'HISTORICAL_PREWRITE_UNPROVEN'


def classify_attempt(auth, repo, token, run, attempt):
    rid = run['id']
    row = {'run': rid, 'attempt': attempt, 'parent': run.get('conclusion'),
           'code': 'EVIDENCE_READ_UNAVAILABLE', 'sourceBlob': None}
    try:
        auth._trusted_child_producer(repo, run)
    except (KeyError, TypeError, ValueError, RuntimeError, AttributeError):
        row['code'] = 'UNTRUSTED_PRODUCER'
        return row
    try:
        result = auth.api_get(
            repo, f'actions/runs/{rid}/attempts/{attempt}/jobs?per_page=100',
            token)
        jobs, count = result.get('jobs'), result.get('total_count')
        if (not isinstance(jobs, list) or type(count) is not int
                or count != len(jobs) or any(not isinstance(x, dict) for x in jobs)):
            row['code'] = 'JOB_INVENTORY_INCOMPLETE'
            return row
        if auth.release_attempt_never_entered(jobs):
            row['code'] = 'PREPUBLICATION_NOT_ENTERED'
            return row
        owners = [job for job in jobs if job.get('name') == 'release']
        if not owners:
            row['code'] = 'NO_MIGRATION_STEP_ENTERED'
            return row
        if len(owners) != 1:
            row['code'] = 'RELEASE_JOB_MISSING'
            return row
        job = owners[0]
        if auth._attested_retired_unscheduled_release(
                repo, run, job, jobs, attempt, token):
            row['code'] = 'PREPUBLICATION_NOT_ENTERED'
            row['originalNoRunner'] = True
            return row
        if job.get('status') == 'queued' or job.get('conclusion') == 'skipped':
            row['code'] = 'NO_MIGRATION_STEP_ENTERED'
            return row
        steps = job.get('steps')
        if not isinstance(steps, list):
            row['code'] = 'JOB_INVENTORY_INCOMPLETE'
            return row
        legacy = [x for x in steps if x.get('name') == LEGACY_STEP]
        modern = [x for x in steps if x.get('name') == MODERN_STEP]
        if len(legacy) + len(modern) != 1:
            row['code'] = 'MIGRATION_STEP_MISSING'
            return row
        step = (legacy or modern)[0]
        row['step'] = 'legacy' if legacy else 'canonical'
        row['stepOutcome'] = step.get('conclusion')
        if step.get('conclusion') == 'skipped':
            row['code'] = 'NO_MIGRATION_STEP_ENTERED'
            return row
        if legacy:
            source = auth._release_history_source(
                repo, run['head_sha'],
                '.github/workflows/' + auth.DIRECT_RELEASE_WORKFLOW, token)
            row['sourceBlob'] = blob_sha(source)
            if (step.get('conclusion') == 'failure'
                    and (auth._attested_legacy_ef_factory_nonentry(
                        repo, run, job, step, attempt, token)
                         or auth._legacy_migration_noop(
                             repo, run, job, step, source, token))):
                row['code'] = 'LEGACY_PREWRITE_PROVEN'
                return row
            if step.get('conclusion') == 'success':
                if auth._legacy_migration_noop(repo, run, job, step, source, token):
                    row['code'] = 'LEGACY_NOOP_PROVEN'
                    return row
                outputs = original_output_lines(
                    auth._release_job_log(repo, job['id'], token))
                if (auth.LEGACY_NOOP_MARKER in outputs
                        or auth.LEGACY_OLDER_NOOP_MARKER in outputs):
                    row['code'] = 'LEGACY_NOOP_PROOF_REJECTED'
                else:
                    row['code'] = 'POSSIBLE_SQL_WRITE_REQUIRES_LIVE_FENCE'
                return row
            if auth._attested_retired_legacy_failure(
                    repo, run, job, step, source, attempt, token):
                row['code'] = 'LEGACY_PREWRITE_PROVEN'
                return row
            row['code'] = legacy_failure_reason(auth, repo, job, token, row)
            return row
        if step.get('conclusion') == 'failure':
            if (auth._attested_migration_prewrite_failure(
                    repo, run, job, step, attempt, token)
                    or auth._historical_migration_prewrite_proven(
                        repo, run, job, step, attempt, token)):
                row['code'] = 'MODERN_PREWRITE_PROVEN'
            else:
                row['code'] = 'HISTORICAL_PREWRITE_UNPROVEN'
            return row
        if (step.get('conclusion') == 'success'
                and auth._attested_migration_noop_success(
                    repo, run, job, step, attempt, token)):
            row['code'] = 'MODERN_NOOP_PROVEN'
        elif step.get('conclusion') == 'success':
            row['code'] = 'POSSIBLE_SQL_WRITE_REQUIRES_LIVE_FENCE'
        else:
            row['code'] = 'HISTORICAL_PREWRITE_UNPROVEN'
        return row
    except (KeyError, TypeError, ValueError, RuntimeError, AttributeError,
            OSError, UnicodeError, IndexError):
        row['code'] = 'EVIDENCE_READ_UNAVAILABLE'
        return row


def audit(auth, repo, token):
    branch = urllib.parse.quote(auth.TRUSTED_PR_BASE, safe='')
    runs, seen, expected = [], 0, None
    for page in range(1, 11):
        payload = auth.api_get(
            repo,
            f'actions/runs?branch={branch}&event=workflow_dispatch&per_page=100&page={page}',
            token)
        batch = payload.get('workflow_runs')
        total = payload.get('total_count')
        if (not isinstance(batch, list) or type(total) is not int
                or total < 0 or (expected is not None and total != expected)):
            raise RuntimeError('RELEASE_HISTORY_INVENTORY_INCOMPLETE')
        expected = total
        seen += len(batch)
        for run in batch:
            if (run.get('head_branch') == auth.TRUSTED_PR_BASE
                    and run.get('event') == 'workflow_dispatch'
                    and run.get('path', '').split('@')[0] ==
                    '.github/workflows/' + auth.DIRECT_RELEASE_WORKFLOW):
                runs.append(run)
        if len(batch) < 100:
            break
    if expected is None or seen != expected:
        raise RuntimeError('RELEASE_HISTORY_TRUNCATED')
    tasks = []
    for run in runs:
        attempts = run.get('run_attempt', 1)
        if type(attempts) is not int or not 1 <= attempts <= 20:
            raise RuntimeError('RELEASE_ATTEMPT_INVENTORY_INCOMPLETE')
        for attempt in range(1, attempts + 1):
            tasks.append((run, attempt))
    # Bounded parallelism; output is deterministically sorted after all reads.
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        futures = [pool.submit(classify_attempt, auth, repo, token, run, attempt)
                   for run, attempt in tasks]
        rows = [f.result() for f in futures]
    rows.sort(key=lambda x: (-x['run'], x['attempt']))
    return {'schemaVersion': 1, 'scope': 'READ_ONLY_HISTORICAL_MIGRATION',
            'completeHistory': True, 'historicalRuns': len(runs),
            'attempts': len(rows), 'records': rows}


def main():
    auth = authority()
    report = audit(auth, os.environ['GITHUB_REPOSITORY'], os.environ['GH_TOKEN'])
    records = report['records']
    blockers = [r for r in records if r['code'] in BLOCKED_CODES]
    possible = [r for r in records
                if r['code'] == 'POSSIBLE_SQL_WRITE_REQUIRES_LIVE_FENCE']
    report['blockingCount'] = len(blockers)
    report['requiresPhysicalSqlFence'] = len(possible)
    output = os.environ.get('LEGEND_MIGRATION_AUDIT_OUTPUT', '').strip()
    if output:
        Path(output).write_text(json.dumps(report, sort_keys=True, indent=2) + '\n')
    print('LEGEND_MIGRATION_AUDIT:COMPLETE:historicalRuns=' +
          str(report['historicalRuns']) + ':attempts=' + str(report['attempts']) +
          ':unproven=' + str(len(blockers)) +
          ':requiresPhysicalSQL=' + str(len(possible)), flush=True)
    for row in blockers:
        print('LEGEND_MIGRATION_AUDIT:BLOCKED:run=' + str(row['run']) +
              ':attempt=' + str(row['attempt']) + ':code=' + row['code'] +
              ':sourceBlob=' + (row.get('sourceBlob') or 'unknown'), flush=True)
    # Evidence inventory is OBSERVATION only: a historical EF SQL attempt
    # expectedly remains listed until the canonical protected release checks
    # the fresh physical schema and journal. These findings are NOT a code
    # compilation failure, and must not force another PR for each historical
    # run. The production first-write gate remains fail-closed regardless.
    print('LEGEND_MIGRATION_AUDIT:OBSERVATION_ONLY:unresolved=' +
          str(len(blockers)) + ':SQL_WRITE_NOT_AUTHORIZED', flush=True)
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (KeyError, TypeError, ValueError, RuntimeError, AttributeError,
            OSError, UnicodeError):
        print('LEGEND_MIGRATION_AUDIT:INCOMPLETE:history-unavailable', flush=True)
        sys.exit(1)
