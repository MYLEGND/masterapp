#!/usr/bin/env python3
"""Real original GitHub evidence tests; strictly read-only, never SQL admission.

The migration IDs/fence below are historical fixtures from authenticated
inventory 37822944519 (also independently re-observed on attempt 2).
This is NOT the live production mutation authority. The later guarded release
uses a new physical SQL observation and a new journal readback.
"""
import importlib.util
import os
import subprocess
from pathlib import Path

spec = importlib.util.spec_from_file_location(
    'authority', Path(__file__).with_name('validation-resume.py'))
a = importlib.util.module_from_spec(spec)
spec.loader.exec_module(a)
repo, token = os.environ['GITHUB_REPOSITORY'], os.environ['GH_TOKEN']
fixtures = {
    # A failing pre-bundle baseline, entered migrations subsequently applied,
    # and a pending-model guard raised before any migration was entered.
    36980409187: 'prebundle',
    36866789827: 'live-prefix',
    36305472201: 'live-prefix',
    36175813329: 'prebundle',
}
for run_id, kind in fixtures.items():
    run = a.api_get(repo, f'actions/runs/{run_id}', token)
    jobs = a.api_get(repo, f'actions/runs/{run_id}/attempts/1/jobs?per_page=100', token)
    owners = [j for j in jobs.get('jobs', []) if j.get('name') == 'release']
    if jobs.get('total_count') != len(jobs.get('jobs', [])) or len(owners) != 1:
        raise SystemExit('RETIRED_EVIDENCE:INCOMPLETE_RUN')
    job = owners[0]
    steps = [step for step in job.get('steps', [])
             if step.get('name') == 'Apply additive diagnostics migrations before restarting apps']
    if len(steps) != 1:
        raise SystemExit('RETIRED_EVIDENCE:INCOMPLETE_STEP')
    source = a._release_history_source(
        repo, run['head_sha'], '.github/workflows/' + a.DIRECT_RELEASE_WORKFLOW, token)
    # Live prefix fixture is test data only. Never feed this to a live operation.
    positive = a._attested_retired_legacy_failure(
        repo, run, job, steps[0], source, 1, token,
        first_pending_migration_id='20261007134500_AddFounderAssistantRules',
        last_applied_migration_id='20261003091500_CanonicalizeBusinessFinanceToolState',
        current_application_revision=subprocess.check_output(
            ['git', 'rev-parse', 'HEAD'], text=True).strip())
    if not positive:
        raise SystemExit('RETIRED_EVIDENCE:ORIGINAL_PROOF_REJECTED:run=' + str(run_id))
    # An entered EF attempt must NOT become the other no-write classifier.
    if kind == 'live-prefix' and a._legacy_migration_noop(
            repo, run, job, steps[0], source, token):
        raise SystemExit('RETIRED_EVIDENCE:EF_ENTERED_MISCLASSIFIED_NOOP')
    print('RETIRED_EVIDENCE:AUTHENTICATED:' + kind + ':run=' + str(run_id))

# Authenticate a completed SQL no-op where the original release selected
# a protected older approved commit than the workflow event's triggering head.
# The original no-change marker occurs before the historical EF bundle.
run_id = 36981319917
run = a.api_get(repo, f'actions/runs/{run_id}', token)
response = a.api_get(repo, f'actions/runs/{run_id}/attempts/1/jobs?per_page=100', token)
jobs = response.get('jobs', [])
owners = [j for j in jobs if j.get('name') == 'release']
if response.get('total_count') != len(jobs) or len(owners) != 1:
    raise SystemExit('RETIRED_EVIDENCE:SUCCESSFUL_NOOP_OWNER_UNPROVEN')
job = owners[0]
steps = [x for x in job.get('steps', [])
         if x.get('name') == 'Apply additive diagnostics migrations before restarting apps']
source = a._release_history_source(
    repo, run['head_sha'], '.github/workflows/' + a.DIRECT_RELEASE_WORKFLOW, token)
if len(steps) != 1 or not a._legacy_migration_noop(
        repo, run, job, steps[0], source, token):
    raise SystemExit('RETIRED_EVIDENCE:SUCCESSFUL_NOOP_PROOF_REJECTED:run=36981319917')
print('RETIRED_EVIDENCE:ORIGINAL_SUCCESSFUL_NOOP:run=36981319917')

for run_id in (36278311858, 36275526793):
    run = a.api_get(repo, f'actions/runs/{run_id}', token)
    response = a.api_get(
        repo, f'actions/runs/{run_id}/attempts/1/jobs?per_page=100', token)
    jobs = response.get('jobs', [])
    owners = [j for j in jobs if j.get('name') == 'release']
    if (response.get('total_count') != len(jobs) or len(owners) != 1
        or not a._attested_retired_unscheduled_release(
            repo, run, owners[0], jobs, 1, token)):
        raise SystemExit('RETIRED_EVIDENCE:UNSCHEDULED_PROOF_REJECTED:run=' + str(run_id))
    print('RETIRED_EVIDENCE:ORIGINAL_UNSCHEDULED:run=' + str(run_id))
