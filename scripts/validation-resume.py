#!/usr/bin/env python3
"""Fail-closed validation resumption for LEGEND GitHub Actions.

The canonical gate model preserves independently successful children from trusted
PR producers when source, execution and toolchain-policy content identities are
equivalent. A new commit, branch, parent failure or receipt-only workflow change
is not itself invalidation. Unknown dependencies or missing proof fail closed.
"""
from __future__ import annotations

import argparse
import ast
import functools
import concurrent.futures
import fnmatch
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import urllib.parse
import urllib.request
import urllib.error
import contextlib
import contextvars
import threading


TRUSTED_PR_BASE = "legend/approved-changes"
FAST_TRACK_PR_BASE = "Legend/fast-track-release"
FAST_TRACK_RELEASE_WORKFLOW = "legend-fast-track-ui.yml"
FAST_TRACK_CHILD_STEPS = {
    'migrations': ('Reconcile production schema before publication', 'Apply or reconcile the exact rehearsed migration bundle'),
    'routing-cloudflare': ('Publish verified website routing', 'Publish or reconcile canonical website routing'),
}
# Reviewed UI-only generations: Azure ZIP publication and read-only probes;
# none owns a SQL or routing write. Unknown source generations are not inferred.
FAST_TRACK_PRE_MIGRATION_SOURCES = frozenset({
    '009caa30172a0876256d73fe5d65da7b78e0d0a3', 'a4dc70671ffc1c6831aaac08c0ecd174b2977399',
    '754bbd32ad17db693acbd9347828c738e27ac3f8', '3c17ce177b365f341f1e341bb57dbce6e9efea73',
    '3c24505f36f40d1771a177a6fde557739001b42a', 'e8b844209b0eab795c0261682a999e96de035fee',
    '16c8e432d5f8d70ec62b56e0b45724baeaea4a12', 'e9c28127fa54cecf2be26ea1d029462d7fd297a0',
    'afa578e0a2cff0964281d197f48e2bf01e26b07b', 'ebcfe155cd5fed20f3e422b5ce6c8cf995930ff0',
    'ef527573a0a22c5b4ebf78254fbd2a26228d7cc7', '86b4e0a179d2588e07eaf1db260360279abf9190',
    'f6bc9aac6a6142b225162f0096396be7447df1e3', '624e1495285ec8778675029b05d3cfb51657ef9b',
})
_VALIDATION_PR_BASE = contextvars.ContextVar("validation_pr_base", default=TRUSTED_PR_BASE)
DIRECT_RELEASE_WORKFLOW = "all-intentional-direct-release-20260918.yml"
PACKAGE_VALIDATION_WORKFLOW = "masterapp-platform-architecture-validation.yml"
RELEASE_REQUEST_PATH = "Docs/releases/direct-release-request.json"
RELEASE_RESOURCE_GROUP = "masterapp-rg"
MIGRATION_BUNDLE_NAME = "masterapp-migrations"
ROUTING_WORKER_NAME = "legend-business-website-router"
ROUTING_PASS_THROUGH_HOSTS = (
    "mylegnd.com",
    "www.mylegnd.com",
    "protect.mylegnd.com",
    "portal.mylegnd.com",
    "client.mylegnd.com",
)
DOMAIN_REFRESH_PROJECT = "scripts/DomainReleaseRefresh/DomainReleaseRefresh.csproj"

# Release children use the same content-based evidence authority as validation.
# Mutable provider reality is reconciled by each child before a retained receipt
# can suppress a write; a receipt alone never proves current configuration.
DIRECT_RELEASE_CHILDREN = {
    "founder-cloudflare": {
        "step": "Run independent auxiliary release fanout",
        "paths": ("Legend-Cloudflare/", "scripts/deploy-founder-cloudflare.py", "scripts/release-auxiliary.py"),
        "operation_paths": ("Legend-Cloudflare/src/", "Legend-Cloudflare/wrangler.founder-baseline.jsonc", "Legend-Cloudflare/package.json", "Legend-Cloudflare/package-lock.json"),
        "operation_exclusions": ("Legend-Cloudflare/src/website-routing/",),
    },
    "migrations": {
        "step": "Synchronize canonical pre-publication resource lanes",
        "paths": (
            "scripts/MigrationReleaseProbe/",
            "scripts/release-migration.py",
            "scripts/release-prepublication.py",
        ),
    },
    "shared-config": {
        "step": "Synchronize canonical pre-publication resource lanes",
        "paths": ("scripts/release-child-receipt.py", "scripts/release-prepublication.py"),
    },
    "editor-config": {
        "step": "Synchronize canonical pre-publication resource lanes",
        "paths": ("scripts/release-child-receipt.py", "scripts/release-prepublication.py"),
    },
    "routing-cloudflare": {
        "step": "Run independent auxiliary release fanout",
        "paths": ("Legend-Cloudflare/", "scripts/cloudflare-routing-authority.py", "scripts/release-router.py", "scripts/release-auxiliary.py"),
        "operation_paths": ("Legend-Cloudflare/src/website-routing/", "Legend-Cloudflare/wrangler.website-routing.jsonc", "Legend-Cloudflare/package.json", "Legend-Cloudflare/package-lock.json"),
    },
    "live-proof": {
        "step": "Verify every deployed target and collect all failures",
        "paths": ("scripts/release-child-receipt.py",),
    },
}


def direct_child_operation_identity(child, revision, material_identity):
    """Physical desired operation survives changes to its verifier/control plane."""
    gate = DIRECT_RELEASE_CHILDREN[child]
    if not re.fullmatch(r"[a-f0-9]{64}", material_identity):
        raise ValueError("Invalid release child material identity")
    raw = subprocess.run(["git", "ls-tree", "-r", "-z", revision], check=True, capture_output=True).stdout.decode()
    sources = {}
    for entry in raw.split("\0"):
        if entry:
            meta, path = entry.split("\t", 1)
            if direct_child_operation_matches(child, path):
                sources[path] = meta
    return hashlib.sha256(json.dumps(dict(child=child, materialIdentity=material_identity,
        sources=sources), sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def direct_child_operation_matches(child, path):
    gate = DIRECT_RELEASE_CHILDREN[child]
    def includes(prefix):
        return path == prefix or (prefix.endswith("/") and path.startswith(prefix))
    return (any(includes(prefix) for prefix in gate.get("operation_paths", ())) and
            not any(includes(prefix) for prefix in gate.get("operation_exclusions", ())))


def direct_child_identity(child, revision, material_identity, *, workflow=None):
    """Bind a child to its inputs and owning executable block, not its SHA.

    material_identity is a public immutable package/schema/provider resource
    identity, never a configuration value or digest of a secret.
    """
    gate = DIRECT_RELEASE_CHILDREN[child]
    if not re.fullmatch(r"[a-f0-9]{64}", material_identity):
        raise ValueError("Invalid release child material identity")
    raw = subprocess.run(["git", "ls-tree", "-r", "-z", revision],
                         check=True, capture_output=True).stdout.decode()
    inputs = {}
    for entry in raw.split("\0"):
        if not entry:
            continue
        meta, path = entry.split("\t", 1)
        if any(path == prefix or (prefix.endswith("/") and path.startswith(prefix))
               for prefix in gate["paths"]):
            inputs[path] = meta
    workflow = workflow or DIRECT_RELEASE_WORKFLOW
    if workflow not in {DIRECT_RELEASE_WORKFLOW, FAST_TRACK_RELEASE_WORKFLOW}:
        raise ValueError('Unknown child execution authority')
    text = git_show_file(revision, ".github/workflows/" + workflow)
    step = FAST_TRACK_CHILD_STEPS[child][1] if workflow == FAST_TRACK_RELEASE_WORKFLOW else gate['step']
    block = named_step_blocks(text).get(step)
    if not block:
        raise ValueError("Canonical release child owner missing")
    contract = dict(child=child, definition=gate, inputs=inputs,
                    executionContract=block, materialIdentity=material_identity)
    return hashlib.sha256(json.dumps(contract, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


# Retained-material recovery is a distinct producer, never a push producer.
FAST_TRACK_RECOVERY_JOB = 'Recover retained fast-track application materials'
FAST_TRACK_RECOVERY_JOB_ID = 'recover-fast-track'
FAST_TRACK_PREUPLOAD_SOURCE_GENERATIONS = ({
    '.github/workflows/legend-fast-track-ui.yml': '4f551f6cda7b2161abe7d36d5dbba510f081b10fa92ab9a64eb2e3205f6081d2',
    'scripts/deploy-approved-app.py': 'cf6335e11503c862d7f4f743e520aeb543f56fa3583f0f3ece65b934324c2d51',
    'scripts/release-operation-evidence.py': '6f7a92db9b48255825412321516176a1795935bbfaeb24c2268a4437dcb239b3',
},)


PROTECT_RECOVERY_RUN = 40336437677
PROTECT_RECOVERY_REVISION = 'b41228e2f852c95b38890d3a8b16ec96a4c46dbd'
PROTECT_BASELINE_REVISION = '924a03c0af22787e514e3b0560b98f79c376b55c'
PROTECT_PREUPLOAD_SOURCE = {
    '.github/workflows/legend-fast-track-ui.yml': 'cdf5bde7daa601450ac37df35c0aabde044d7dd5a728ad22fc12433f467fe7fd',
    'scripts/deploy-approved-app.py': '6bec907bd7048c7a0e221f9bcfd84a9546eb750f654075a02260f2c606679dbb',
    'scripts/release-operation-evidence.py': 'cf73cff19073d290ddff5efe3fdd54f7a901d674f804821e2f0abf32474060c0',
}


def _fast_track_preupload_log_proven(log, revision, step, *, protect_baseline=False):
    """Interpret only the fixed exception from an independently pinned producer."""
    import datetime
    start, end = (_completion_time(step.get(key)) for key in ('started_at', 'completed_at'))
    if end < start:
        return False
    clean = '\n'.join(re.sub(r'^\d{4}-\d{2}-\d{2}T[0-9:.]+Z ', '', line) for line in log.splitlines())
    heads = set(re.findall(r'(?m)^\[command\]/usr/bin/git log -1 --format=%H\n([a-f0-9]{40})$', clean))
    if heads != {revision}:
        return False
    marker = ('DeploymentStatusUnavailable: Azure deployment status remained unavailable for 3 consecutive reads '
              '(before any upload). No deployment or rollback write was replayed; resume by reconciling the exact revision.')
    if protect_baseline:
        marker = "DeploymentDrift: FAST_TRACK_SUPERSESSION_LIVE_BASELINE_CHANGED"
    events = []
    for line in log.splitlines():
        stamp, sep, text = line.partition(' ')
        if not sep or not re.fullmatch(r'\d{4}-\d{2}-\d{2}T[0-9:.]+Z', stamp):
            continue
        observed = _completion_time(stamp)
        if start <= observed <= end + datetime.timedelta(seconds=1):
            events.append(text)
    # Positive terminal control-flow evidence, not an absence-of-intent shortcut.
    # In this pinned generation submitted is set BEFORE submit and never reset;
    # the literal before-upload phase is impossible after entering that write.
    return (events.count(marker) == 1 and clean.count(marker) == 1
        and not any('Submitting the verified immutable ZIP once.' in line
                    or 'AZURE_TRACKED_DEPLOYMENT_REFERENCE:' in line for line in events)
        and any('in fast_track_deploy' in line for line in events)
        and (any('in prepare_protect_replacement' in line for line in events) if protect_baseline else
             (any('in reconcile' in line for line in events)
              and any('in read_deployments_bounded' in line for line in events)))
        and any('Process completed with exit code 1.' in line for line in events))


def attested_fast_track_preupload_failure(repository, run, job, attempt, target, operation_id, token):
    """Generic reviewed pre-upload read failure; unknown generations stay held."""
    import tempfile
    import zipfile
    try:
        revision = run.get('head_sha')
        if (not is_fast_track_producer(repository, run) or run.get('status') != 'completed'
            or type(attempt) is not int or not 1 <= attempt <= run.get('run_attempt', 0)
            or target not in RELEASE_TARGETS or job.get('run_id') != run['id']
            or job.get('run_attempt') != attempt or job.get('head_sha') != revision
            or job.get('name') != 'Affected App Deploy — ' + fast_track_target_label(target)
            or job.get('status') != 'completed' or job.get('conclusion') != 'failure'):
            return False
        step = _completion_step(job, 'Publish or reconcile exact application bytes', conclusions=('failure',))
        sources = {path: hashlib.sha256(_release_history_source(repository, revision, path, token).encode()).hexdigest()
                   for path in FAST_TRACK_PREUPLOAD_SOURCE_GENERATIONS[0]}
        protect_baseline = (target == 'protect' and run['id'] == PROTECT_RECOVERY_RUN
            and revision == PROTECT_RECOVERY_REVISION and sources == PROTECT_PREUPLOAD_SOURCE)
        if sources not in FAST_TRACK_PREUPLOAD_SOURCE_GENERATIONS and not protect_baseline:
            return False
        inventory = _completion_inventory(api_get(repository, f"actions/runs/{run['id']}/artifacts?per_page=100", token), 'artifacts')
        package = _completion_artifact(inventory, 'fast-track-package-' + revision + '-' + target, run)
        attempts = _completion_jobs(repository, run, token)
        _completion_original_artifact_owner(dict(run=run, attempts=attempts), target, package,
            'Preserve immutable application bytes before upload', ('success',))
        # The caller has also checked repository-wide exact-operation history.
        # Confirm no original target record is hidden under another operation ID.
        for artifact in inventory:
            if artifact.get('name', '').startswith('legend-release-operation-'):
                record = _release_history_json(repository, run['id'], artifact, 'operation.json', verify_digest=True)
                if record.get('target') == target:
                    return False
        digest=fast_track_recovery_package_digest(repository,dict(run=run,targets=[target],artifacts=inventory),
            dict(packageArtifact=package),target,token)
        identity = dict(target=target, applicationRevision=revision, packageDigest=digest)
        if hashlib.sha256(json.dumps(identity, sort_keys=True, separators=(',', ':')).encode()).hexdigest() != operation_id:
            return False
        return _fast_track_preupload_log_proven(_release_job_log(repository, job['id'], token), revision, step,
            protect_baseline=protect_baseline)
    except (KeyError, TypeError, ValueError, RuntimeError, OSError, UnicodeError, zipfile.BadZipFile, subprocess.SubprocessError):
        return False


def fast_track_recovery_producer(repository, run, token, *, attempt, live=False):
    """Authenticate this separate dispatch, without changing push provenance."""
    revision = run.get('head_sha', '')
    if (run.get('path') != '.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW
        or run.get('event') != 'workflow_dispatch' or run.get('head_branch') != FAST_TRACK_PR_BASE
        or run.get('head_repository', {}).get('full_name', '').lower() != repository.lower()
        or type(attempt) is not int or not 1 <= attempt <= run.get('run_attempt', 0)
        or not re.fullmatch('[a-f0-9]{40}', revision)):
        raise ValueError('FAST_TRACK_RECOVERY_PRODUCER_UNPROVEN')
    path = '.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW
    if _release_history_source(repository, revision, path, token) != (Path(__file__).resolve().parent.parent / path).read_text():
        raise ValueError('FAST_TRACK_RECOVERY_WORKFLOW_CHANGED')
    promotions = fast_track_historical_producers(repository, revision, token)
    if (len(promotions) != 1 or promotions[0].get('head_sha') != revision
        or fast_track_promotion_parent(repository, promotions[0], token) is None):
        raise ValueError('FAST_TRACK_RECOVERY_TOOL_NOT_PROMOTED')
    owner = _fast_track_recovery_owner(repository, run, token, attempt=attempt, live=live)
    return dict(runId=run['id'], runAttempt=attempt, jobId=owner['id'], toolRevision=revision,
                promotionRunId=promotions[0]['id'])


def _fast_track_recovery_owner(repository, run, token, *, attempt, live):
    revision = run['head_sha']
    jobs = _completion_rows(api_get(repository, f"actions/runs/{run['id']}/attempts/{attempt}/jobs?per_page=100", token), 'jobs')
    names = {'Select exact affected applications', 'Admit exact validated fast-track source',
             'Reconcile production schema before publication', 'Publish verified website routing',
             'Affected App Deploy — ${{ matrix.target }}', FAST_TRACK_RECOVERY_JOB}
    if (len(jobs) != len(names) or {job.get('name') for job in jobs} != names
        or any(job.get('run_id') != run['id'] or job.get('run_attempt') != attempt
               or job.get('head_sha') != revision for job in jobs)):
        raise ValueError('FAST_TRACK_RECOVERY_JOB_INVENTORY_UNPROVEN')
    owner = next(job for job in jobs if job['name'] == FAST_TRACK_RECOVERY_JOB)
    if any(job.get('status') != 'completed' or job.get('conclusion') != 'skipped' or job.get('steps') != []
           for job in jobs if job is not owner):
        raise ValueError('FAST_TRACK_RECOVERY_COMPETING_JOB')
    if live:
        if (run.get('status') not in {'in_progress', 'queued'} or run.get('run_attempt') != attempt
            or owner.get('status') != 'in_progress' or owner.get('runner_name') != os.environ.get('RUNNER_NAME')
            or api_get(repository, 'git/ref/heads/' + FAST_TRACK_PR_BASE, token).get('object', {}).get('sha') != revision):
            raise ValueError('FAST_TRACK_RECOVERY_LIVE_OWNER_CHANGED')
    return owner


def _fast_track_recovery_execution_input(original_run, application_revision):
    env = os.environ
    repository = env.get('GITHUB_REPOSITORY', '')
    token = env.get('GH_TOKEN') or env.get('GITHUB_TOKEN')
    ref = 'refs/heads/' + FAST_TRACK_PR_BASE
    if (env.get('GITHUB_ACTIONS') != 'true' or env.get('GITHUB_EVENT_NAME') != 'workflow_dispatch'
        or env.get('GITHUB_REF') != ref or env.get('GITHUB_JOB') != FAST_TRACK_RECOVERY_JOB_ID
        or env.get('GITHUB_WORKFLOW_REF') != repository + '/.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW + '@' + ref
        or not token or not env.get('RUNNER_NAME') or not env.get('GITHUB_RUN_ID', '').isdigit()
        or not env.get('GITHUB_RUN_ATTEMPT', '').isdigit() or type(original_run) is not int or original_run < 1
        or not re.fullmatch('[a-f0-9]{40}', application_revision or '')):
        raise ValueError('FAST_TRACK_RECOVERY_EXECUTION_UNPROVEN')
    inputs = json.loads(Path(env['GITHUB_EVENT_PATH']).read_text()).get('inputs')
    if inputs != dict(original_run=str(original_run), application_revision=application_revision):
        raise ValueError('FAST_TRACK_RECOVERY_DISPATCH_UNPROVEN')
    run = api_get(repository, 'actions/runs/' + env['GITHUB_RUN_ID'], token)
    if run.get('id') != int(env['GITHUB_RUN_ID']):
        raise ValueError('FAST_TRACK_RECOVERY_RUN_ID_CHANGED')
    expected_title = 'LEGEND retained recovery original=' + str(original_run) + ' application=' + application_revision
    if run.get('display_title') != expected_title:
        raise ValueError('FAST_TRACK_RECOVERY_DISPATCH_TITLE_CHANGED')
    if (run.get('path') != '.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW
        or run.get('event') != 'workflow_dispatch' or run.get('head_branch') != FAST_TRACK_PR_BASE
        or run.get('head_repository', {}).get('full_name', '').lower() != repository.lower()
        or not re.fullmatch('[a-f0-9]{40}', run.get('head_sha', ''))):
        raise ValueError('FAST_TRACK_RECOVERY_PRODUCER_UNPROVEN')
    return repository, token, run


def _assert_fast_track_recovery_checked_source(run, proof, application_revision):
    env = os.environ
    paths = ['.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW, 'scripts/validation-resume.py',
             'scripts/deploy-approved-app.py', 'scripts/release-operation-evidence.py', 'scripts/release_policy.py',
             'scripts/release-artifacts/transport.cjs']
    if (run.get('head_sha') != env.get('GITHUB_SHA')
        or subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip() != proof['toolRevision']
        or subprocess.run(['git', 'diff', '--quiet', 'HEAD', '--', *paths], capture_output=True).returncode
        or subprocess.run(['git', 'merge-base', '--is-ancestor', application_revision, proof['toolRevision']], capture_output=True).returncode):
        raise ValueError('FAST_TRACK_RECOVERY_CHECKED_SOURCE_UNPROVEN')


def assert_fast_track_recovery_execution(original_run, application_revision):
    repository, token, run = _fast_track_recovery_execution_input(original_run, application_revision)
    proof = fast_track_recovery_producer(repository, run, token,
        attempt=int(os.environ['GITHUB_RUN_ATTEMPT']), live=True)
    _assert_fast_track_recovery_checked_source(run, proof, application_revision)
    return proof


def assert_fast_track_recovery_live_execution(original_run, application_revision, observer):
    """Fresh mutable owner/source checks after historical proof readback."""
    repository, token, run = _fast_track_recovery_execution_input(original_run, application_revision)
    attempt = int(os.environ['GITHUB_RUN_ATTEMPT'])
    if (run.get('run_attempt') != attempt or run.get('id') != observer.get('runId')
        or attempt != observer.get('runAttempt') or run.get('head_sha') != observer.get('toolRevision')):
        raise ValueError('FAST_TRACK_RECOVERY_LIVE_OWNER_CHANGED')
    owner = _fast_track_recovery_owner(repository, run, token, attempt=attempt, live=True)
    if owner.get('id') != observer.get('jobId'):
        raise ValueError('FAST_TRACK_RECOVERY_LIVE_OWNER_CHANGED')
    _assert_fast_track_recovery_checked_source(run, observer, application_revision)
    return observer


def protect_recovery_baseline_proof(repository, original, package, token):
    """Correct one unused replacement's rollback anchor, never its old outcome."""
    run = original['run']
    if (repository.lower() != 'mylegnd/masterapp' or run['id'] != PROTECT_RECOVERY_RUN
        or run['head_sha'] != PROTECT_RECOVERY_REVISION or package['id'] != 12661220624
        or package['digest'] != 'sha256:d01034901695028c32960fdf1b55aff4b2e26ec65782db475bec524765e89122'):
        raise ValueError('PROTECT_RECOVERY_ORIGINAL_MATERIAL_CHANGED')
    request = json.loads(_release_history_source(repository, run['head_sha'], RELEASE_REQUEST_PATH, token))
    authorization = fast_track_protect_supersession(run['head_sha'], request=request)
    if authorization is None:
        raise ValueError('PROTECT_RECOVERY_AUTHORIZATION_MISSING')
    anchor = api_get(repository, 'actions/runs/40063411585', token)
    if (not is_fast_track_producer(repository, anchor) or anchor.get('id') != 40063411585
        or anchor.get('head_sha') != PROTECT_BASELINE_REVISION or anchor.get('status') != 'completed'):
        raise ValueError('PROTECT_RECOVERY_BASELINE_PRODUCER_CHANGED')
    artifacts = _completion_inventory(api_get(repository, 'actions/runs/40063411585/artifacts?per_page=100', token), 'artifacts')
    rollback = _completion_artifact(artifacts, 'fast-track-package-' + PROTECT_BASELINE_REVISION + '-protect', anchor)
    receipt = _completion_artifact(artifacts, 'legend-release-operation-success-eee55365520f03613d09d8deed68ea56e85e05da0a323737c5b562c415f455db', anchor)
    if (rollback['id'] != 12548189496
        or rollback['digest'] != 'sha256:e3df0d1cc7b3573aeae7883bc4e3810444a619a105e47d0fece3112a5c99eae2'
        or receipt['id'] != 12548958892
        or receipt['digest'] != 'sha256:ed43f4f504badd5d41eb5216a1c10550db2576fd3a7dc69017f6fc0af32b02a4'):
        raise ValueError('PROTECT_RECOVERY_BASELINE_ARTIFACT_CHANGED')
    context = dict(run=anchor, attempts=_completion_jobs(repository, anchor, token))
    _completion_original_artifact_owner(context, 'protect', rollback, 'Preserve immutable application bytes before upload', ('success',))
    _completion_original_artifact_owner(context, 'protect', receipt, 'Publish or reconcile exact application bytes', ('success',))
    record = _release_history_json(repository, anchor['id'], receipt, 'operation.json', verify_digest=True)
    expected = dict(target='protect', applicationRevision=PROTECT_BASELINE_REVISION,
        packageDigest='fe81a03662c3657c7b9ae9012cc38198987b353acc44725d84f8a4f9d87bb584',
        operationId='eee55365520f03613d09d8deed68ea56e85e05da0a323737c5b562c415f455db',
        packageProducerRun=40063411585, producingRun=40063411585, producingAttempt=5,
        phase='success', deploymentIds=['fcba568e391649b4aa08fa3dec82e156'])
    if any(record.get(key) != value for key, value in expected.items()):
        raise ValueError('PROTECT_RECOVERY_BASELINE_RECEIPT_CHANGED')
    return dict(schemaVersion=1, authorization=authorization, baselineRevision=PROTECT_BASELINE_REVISION,
        rollbackRun=anchor['id'], rollbackArtifactId=rollback['id'], rollbackArtifactName=rollback['name'],
        rollbackArtifactDigest=rollback['digest'], rollbackPackageDigest=expected['packageDigest'],
        baselineReceiptId=receipt['id'], baselineReceiptDigest=receipt['digest'],
        originalRun=run['id'], applicationRevision=run['head_sha'], candidateArtifactId=package['id'],
        candidateArtifactDigest=package['digest'])


def fast_track_recovery_material(repository, original, key, token):
    """Original bytes are mandatory. No rebuild or newly labelled package exists."""
    if key not in original['targets']:
        raise ValueError('FAST_TRACK_RECOVERY_TARGET_OUTSIDE_SCOPE')
    run = original['run']
    package = _completion_artifact(original['artifacts'], 'fast-track-package-' + run['head_sha'] + '-' + key, run)
    _completion_original_artifact_owner(original, key, package, 'Preserve immutable application bytes before upload', ('success',))
    intents = []
    for artifact in original['artifacts']:
        if artifact.get('name', '').startswith('legend-release-operation-intent-'):
            value = _release_history_json(repository, run['id'], artifact, 'operation.json', verify_digest=True)
            if value.get('target') == key:
                intents.append(value)
    if len(intents) > 1:
        raise ValueError('FAST_TRACK_RECOVERY_COMPETING_INTENT')
    material = dict(packageArtifact=package, originalIntent=intents[0] if intents else None)
    if run['id'] == PROTECT_RECOVERY_RUN:
        if key != 'protect' and not intents:
            raise ValueError('PROTECT_RECOVERY_SUCCESSFUL_SIBLING_INTENT_MISSING')
        if key == 'protect':
            material['protectBaselineCorrection'] = protect_recovery_baseline_proof(repository, original, package, token)
    return material


_RECOVERY_PACKAGE_DIGESTS = contextvars.ContextVar('recovery_package_digests', default=None)
_RECOVERY_RECEIPT_BYTES = contextvars.ContextVar('recovery_receipt_bytes', default=None)
_RECOVERY_LOG_BYTES = contextvars.ContextVar('recovery_log_bytes', default=None)
_RECOVERY_PROOF_VIEW = contextvars.ContextVar('recovery_proof_view', default=None)


def _recovery_metadata_endpoint(path):
    """Explicit historical read classes; live refs and unknown endpoints bypass."""
    return bool(re.fullmatch(
        r'actions/runs/\d+(?:/attempts/\d+/jobs\?per_page=100|/artifacts\?per_page=100)?'
        r'|actions/jobs/\d+'
        r'|actions/(?:runs|workflows/[^/?]+/runs)\?[^#]+'
        r'|actions/artifacts\?name=[^&]+&per_page=100'
        r'|pulls/\d+'
        r'|commits/[a-f0-9]{40}/pulls\?per_page=100', path))


def _recovery_metadata_reusable(repository, path, value, token):
    if not isinstance(value, (dict, list)):
        return False
    current = os.environ.get('GITHUB_RUN_ID')
    def terminal(row):
        return (isinstance(row, dict) and row.get('status') == 'completed'
            and str(row.get('run_id', row.get('id'))) != current)
    if re.fullmatch(r'actions/(?:runs|jobs)/\d+', path):
        return terminal(value)
    if isinstance(value, dict):
        for field in ('jobs', 'workflow_runs'):
            if field in value:
                rows = value[field]
                return (isinstance(rows, list) and type(value.get('total_count')) is int
                    and value['total_count'] == len(rows) and all(terminal(row) for row in rows))
        if 'artifacts' in value:
            rows = value['artifacts']
            if (not isinstance(rows, list) or type(value.get('total_count')) is not int
                or value['total_count'] != len(rows)):
                return False
            parent = re.fullmatch(r'actions/runs/(\d+)/artifacts\?per_page=100', path)
            ids = {int(parent[1])} if parent else set()
            for row in rows:
                if not isinstance(row, dict):
                    return False
                owner = row.get('workflow_run', {}).get('id')
                if type(owner) is not int or owner < 1 or (parent and owner != int(parent[1])):
                    return False
                ids.add(owner)
            if not ids or any(str(owner) == current for owner in ids):
                return False
            return all(terminal(api_get(repository, f'actions/runs/{owner}', token)) for owner in ids)
        if path.startswith('pulls/'):
            return bool(value.get('merged_at'))
    return (isinstance(value, list) and all(isinstance(row, dict)
        and row.get('merged_at') for row in value))


@contextlib.contextmanager
def recovery_admission_proof():
    """One read-only decision boundary, never an invocation-wide approval."""
    if _RECOVERY_PROOF_VIEW.get() is not None:
        yield
        return
    state = dict(entries={}, histories={}, bytes=0)
    handle = _RECOVERY_PROOF_VIEW.set(state)
    try:
        yield
        # A paginated branch inventory is one observation, never cached pages.
        # Include active/queued rows and reject their drift just like terminal rows.
        for repository, entry in state['histories'].items():
            current = _read_fast_track_history_inventory(repository, entry['token'], uncached=True)
            if _fast_track_history_raw(current) != entry['raw']:
                raise ReleaseOperationHistoryUnproven('RECOVERY_HISTORY_CHANGED_AT_BOUNDARY')
        # Use raw transport, not either metadata cache. Compare the complete
        # responses, including pagination counts, expiry and attempt identities.
        for (repository, path), entry in state['entries'].items():
            if entry['uses'] > 1:
                current = _api_get_uncached(repository, path, entry['token'])
                raw = json.dumps(current, sort_keys=True, separators=(',', ':'))
                if raw != entry['raw']:
                    raise ReleaseOperationHistoryUnproven('RECOVERY_PROOF_CHANGED_AT_BOUNDARY')
    finally:
        _RECOVERY_PROOF_VIEW.reset(handle)


def _recovery_proof_read(repository, path, token):
    state = _RECOVERY_PROOF_VIEW.get()
    if state is None or not _recovery_metadata_endpoint(path):
        return _api_get_uncached(repository, path, token)
    key = (repository, path)
    if key in state['entries']:
        entry = state['entries'][key]
        entry['uses'] += 1
        evidence_remaining(30)
        return json.loads(entry['raw'])
    value = _api_get_uncached(repository, path, token)
    if _recovery_metadata_reusable(repository, path, value, token):
        raw = json.dumps(value, sort_keys=True, separators=(',', ':'))
        size = len(raw.encode())
        if len(state['entries']) >= 512 or size > 8 * 1024 * 1024 or state['bytes'] + size > 32 * 1024 * 1024:
            raise ReleaseOperationHistoryUnproven('RECOVERY_PROOF_STORAGE_BOUND')
        state['entries'][key] = dict(raw=raw, uses=1, token=token)
        state['bytes'] += size
        return json.loads(raw)
    return value


@contextlib.contextmanager
def recovery_material_reuse():
    """One invocation, shared by its copied worker contexts; never mutable proof."""
    token = _RECOVERY_PACKAGE_DIGESTS.set((threading.Lock(), {}))
    receipt_token = _RECOVERY_RECEIPT_BYTES.set((threading.Lock(), {}))
    log_token = _RECOVERY_LOG_BYTES.set(dict(entries={}, bytes=0, lock=threading.Lock()))
    try:
        yield
    finally:
        _RECOVERY_LOG_BYTES.reset(log_token)
        _RECOVERY_RECEIPT_BYTES.reset(receipt_token)
        _RECOVERY_PACKAGE_DIGESTS.reset(token)


def fast_track_recovery_package_digest(repository, original, material, key, token):
    state=_RECOVERY_PACKAGE_DIGESTS.get()
    if state is None:return _verified_recovery_package_digest(repository,original,material,key,token)
    run = original['run']; artifact = material['packageArtifact']
    if key not in original['targets'] or not re.fullmatch('[a-f0-9]{40}',run.get('head_sha','')):
        raise ValueError('RECOVERY_PACKAGE_CACHE_SCOPE')
    expected = _completion_artifact(original['artifacts'], 'fast-track-package-' + run['head_sha'] + '-' + key, run)
    if (artifact != expected or artifact.get('expired') is not False
        or type(artifact.get('id')) is not int or artifact['id'] < 1
        or not re.fullmatch('sha256:[a-f0-9]{64}',artifact.get('digest',''))):
        raise ValueError('RECOVERY_PACKAGE_CACHE_IDENTITY')
    identity=(repository,run['id'],run['head_sha'],key,artifact['id'],artifact['name'],artifact['digest'])
    lock, entries=state
    with lock:
        future=entries.get(identity); owns=future is None
        if owns:
            if len(entries)>=100:raise ValueError('RECOVERY_PACKAGE_CACHE_BOUND')
            future=concurrent.futures.Future();entries[identity]=future
    if not owns:return future.result(timeout=evidence_remaining(180))
    try:
        result=_verified_recovery_package_digest(repository,original,material,key,token)
        future.set_result(result)
        return result
    except BaseException as error:
        future.set_exception(error)
        with lock:
            if entries.get(identity) is future:del entries[identity]
        raise


def _verified_recovery_package_digest(repository, original, material, key, token):
    """Independently bind a receipt to original package bytes, not its own claim."""
    import tempfile
    import zipfile
    artifact = material['packageArtifact']
    with tempfile.TemporaryDirectory(prefix='legend-recovery-material-') as temporary:
        root = Path(temporary)
        _download_run_artifact(repository, original['run']['id'], artifact['name'], root,
                               artifact_id=artifact['id'], expected_digest=artifact['digest'])
        package, sums = root / 'deploy.zip', root / 'SHA256SUMS'
        if package.is_symlink() or sums.is_symlink():
            raise ValueError('RECOVERY_PACKAGE_SYMLINK')
        with package.open('rb') as stream:
            digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        matches = [line.split()[0] for line in sums.read_text().splitlines()
                   if len(line.split()) == 2 and Path(line.split()[1]).name == 'deploy.zip']
        if matches != [digest]:
            raise ValueError('RECOVERY_PACKAGE_CHECKSUM_CHANGED')
        with zipfile.ZipFile(package) as archive:
            if archive.testzip() is not None:
                raise ValueError('RECOVERY_PACKAGE_CORRUPT')
            static = RELEASE_TARGETS[key]['static']
            value = archive.read('_deployment-provenance.txt' if static else 'wwwroot/_deployment-provenance.json').decode().strip()
            embedded = value if static else json.loads(value)['releaseSha']
            if embedded != original['run']['head_sha']:
                raise ValueError('RECOVERY_PACKAGE_REVISION_CHANGED')
        return digest


def _without_skipped_recovery_job(jobs, source):
    """Only the explicitly authored inert owner may extend an old push envelope."""
    if FAST_TRACK_RECOVERY_JOB_ID not in _job_blocks(source):
        return jobs
    rows = [job for job in jobs if job.get('name') == FAST_TRACK_RECOVERY_JOB]
    if (len(rows) != 1 or rows[0].get('status') != 'completed'
        or rows[0].get('conclusion') != 'skipped' or rows[0].get('steps') != []):
        raise ValueError('FAST_TRACK_PUSH_RECOVERY_NONENTRY_UNPROVEN')
    return [job for job in jobs if job is not rows[0]]


def fast_track_recovery_record_proof(original, material, observer):
    correction = ({'protectBaselineCorrection': material['protectBaselineCorrection']}
                  if 'protectBaselineCorrection' in material else {})
    return dict(schemaVersion=1, originalRun=original['run']['id'], originalAttempt=original['run']['run_attempt'],
                applicationRevision=original['run']['head_sha'], packageArtifactId=material['packageArtifact']['id'],
                packageArtifactDigest=material['packageArtifact']['digest'], **observer, **correction)


def validate_fast_track_recovery_record(repository, run, artifact, record, token):
    proof = record.get('recoveryEvidence')
    if not isinstance(proof, dict) or proof.get('schemaVersion') != 1:
        raise ValueError('FAST_TRACK_RECOVERY_RECORD_UNPROVEN')
    observer = fast_track_recovery_producer(repository, run, token, attempt=record.get('producingAttempt'))
    expected_title = 'LEGEND retained recovery original=' + str(proof.get('originalRun')) + ' application=' + str(record.get('applicationRevision'))
    if run.get('display_title') != expected_title:
        raise ValueError('FAST_TRACK_RECOVERY_RECORD_DISPATCH_CHANGED')
    original = fast_track_completion_original(repository, proof.get('originalRun'), record.get('applicationRevision'), token)
    material = fast_track_recovery_material(repository, original, record.get('target'), token)
    if record.get('packageDigest') != fast_track_recovery_package_digest(repository, original, material, record.get('target'), token):
        raise ValueError('FAST_TRACK_RECOVERY_RECORD_PACKAGE_DIGEST_CHANGED')
    expected = fast_track_recovery_record_proof(original, material, observer)
    # Later original read-only reruns may append attempts; the record must bind
    # a real earlier terminal snapshot, never claim future original history.
    if type(proof.get('originalAttempt')) is not int or not 1 <= proof['originalAttempt'] <= expected['originalAttempt']:
        raise ValueError('FAST_TRACK_RECOVERY_ORIGINAL_ATTEMPT_CHANGED')
    expected['originalAttempt'] = proof['originalAttempt']
    if (proof != expected or record.get('producingRun') != run['id']
        or record.get('packageProducerRun') != original['run']['id']
        or record.get('applicationRevision') != original['run']['head_sha']):
        raise ValueError('FAST_TRACK_RECOVERY_MATERIAL_IDENTITY_CHANGED')
    if 'protectBaselineCorrection' in expected and record.get('baseline') != PROTECT_BASELINE_REVISION:
        raise ValueError('PROTECT_RECOVERY_RECEIPT_BASELINE_CHANGED')
    jobs = _completion_rows(api_get(repository, f"actions/runs/{run['id']}/attempts/{observer['runAttempt']}/jobs?per_page=100", token), 'jobs')
    job = next(job for job in jobs if job['id'] == observer['jobId'])
    steps = [step for step in job.get('steps', []) if step.get('name') == 'Recover exact retained application bytes']
    if len(steps) != 1:
        raise ValueError('FAST_TRACK_RECOVERY_RECORD_OUTSIDE_OWNER_STEP')
    step = dict(steps[0])
    if step.get('status') == 'in_progress' and step.get('completed_at') is None:
        import datetime
        if (str(run['id']) != os.environ.get('GITHUB_RUN_ID')
            or str(observer['runAttempt']) != os.environ.get('GITHUB_RUN_ATTEMPT')):
            raise ValueError('FAST_TRACK_RECOVERY_RECORD_ACTIVE_OTHER_OWNER')
        step['completed_at'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    if not _completion_artifact_in_step(artifact, step):
        raise ValueError('FAST_TRACK_RECOVERY_RECORD_OUTSIDE_OWNER_STEP')
    return True



def fast_track_recovery_summary_name(original_run, revision):
    value = dict(originalRun=original_run, applicationRevision=revision)
    return 'legend-retained-recovery-success-' + hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def fast_track_recovery_completed(repository, original, token):
    """A genuine successful recovery consumer, never a relabelled original job."""
    run = original['run']; name = fast_track_recovery_summary_name(run['id'], run['head_sha'])
    artifacts = _completion_inventory(api_get(repository, 'actions/artifacts?name=' + urllib.parse.quote(name, safe='') + '&per_page=100', token), 'artifacts')
    completed = False
    for artifact in artifacts:
        producer_id = artifact.get('workflow_run', {}).get('id')
        if artifact.get('name') != name or artifact.get('expired') is not False or type(producer_id) is not int:
            raise ValueError('RECOVERY_SUMMARY_ARTIFACT_UNPROVEN')
        producer = api_get(repository, f'actions/runs/{producer_id}', token)
        record = _release_history_json(repository, producer_id, artifact, 'operation.json', verify_digest=True)
        observer = fast_track_recovery_producer(repository, producer, token, attempt=record.get('producingAttempt'))
        if (record.get('schemaVersion') != 1 or record.get('phase') != 'success'
            or record.get('originalRun') != run['id'] or record.get('applicationRevision') != run['head_sha']
            or record.get('producingRun') != producer_id or record.get('observer') != observer
            or type(record.get('originalAttempt')) is not int or not 1 <= record['originalAttempt'] <= run['run_attempt']
            or not isinstance(record.get('targets'), dict) or set(record['targets']) != set(original['targets'])):
            raise ValueError('RECOVERY_SUMMARY_IDENTITY_CHANGED')
        for key, row in record['targets'].items():
            material = fast_track_recovery_material(repository, original, key, token)
            if (set(row) != {'operationId','packageDigest','packageArtifactId','packageArtifactDigest','deploymentIds'}
                or row['packageArtifactId'] != material['packageArtifact']['id']
                or row['packageArtifactDigest'] != material['packageArtifact']['digest']):
                raise ValueError('RECOVERY_SUMMARY_PACKAGE_CHANGED')
            if row['packageDigest'] != fast_track_recovery_package_digest(repository, original, material, key, token):
                raise ValueError('RECOVERY_SUMMARY_PACKAGE_DIGEST_CHANGED')
            identity = dict(target=key, applicationRevision=run['head_sha'], packageDigest=row['packageDigest'])
            if row['operationId'] != hashlib.sha256(json.dumps(identity, sort_keys=True, separators=(',', ':')).encode()).hexdigest():
                raise ValueError('RECOVERY_SUMMARY_OPERATION_CHANGED')
            receipt = release_operation_history(repository, row['operationId'], run['head_sha'], key,
                                                producer_id, observer['runAttempt'], token, phase='success')
            if (receipt is None or receipt.get('packageDigest') != row['packageDigest']
                or receipt.get('deploymentIds') != row['deploymentIds'] or len(row['deploymentIds']) != 1
                or row['deploymentIds'][0] in receipt.get('baselineDeploymentIds', [])):
                raise ValueError('RECOVERY_SUMMARY_RECEIPT_UNPROVEN')
        jobs = _completion_rows(api_get(repository, f"actions/runs/{producer_id}/attempts/{observer['runAttempt']}/jobs?per_page=100", token), 'jobs')
        owner = next(job for job in jobs if job['id'] == observer['jobId'])
        step = _completion_step(owner, 'Recover exact retained application bytes')
        if (producer.get('status') == 'completed' and producer.get('conclusion') == 'success'
            and owner.get('conclusion') == 'success' and _completion_artifact_in_step(artifact, step)):
            completed = True
    return completed


FAST_TRACK_RECOVERY_PREINTENT_GENERATIONS = ({'.github/workflows/legend-fast-track-ui.yml': 'cdf5bde7daa601450ac37df35c0aabde044d7dd5a728ad22fc12433f467fe7fd', 'scripts/deploy-approved-app.py': 'd563b924ba2f477fb93945675b931a903331d825511e4c67a31884b1d00736cd', 'scripts/release-operation-evidence.py': '506796abeadfd87f09236f74ca8c79a496a1c5b813f1191d0f0ce39cf5a9483d', 'scripts/validation-resume.py': '21d52d576d91ecb842f2b4ad4266643eb6e8de94c73da0a8cb79b1e11d317b39', 'scripts/release-artifacts/transport.cjs': '3a71bff71cabec628b184504de381bd0d41aad0827fc6bb440205eb0cde90ce6'},)

FAST_TRACK_RECOVERY_PREFLIGHT_GENERATIONS = ({'.github/workflows/legend-fast-track-ui.yml': 'cdf5bde7daa601450ac37df35c0aabde044d7dd5a728ad22fc12433f467fe7fd', 'scripts/deploy-approved-app.py': 'd563b924ba2f477fb93945675b931a903331d825511e4c67a31884b1d00736cd', 'scripts/release-operation-evidence.py': '506796abeadfd87f09236f74ca8c79a496a1c5b813f1191d0f0ce39cf5a9483d', 'scripts/validation-resume.py': 'e134012c82b4760dde39fdd22fdb5f8a335a3e1addcd304b0d52ec9d08702268', 'scripts/release-artifacts/transport.cjs': 'c853257dddfe833a8356f94ea3566d93acb7aef666d86e2c8f0dd67763884769'},)

FAST_TRACK_RECOVERY_TARGET_DEADLINE_GENERATIONS = ({'.github/workflows/legend-fast-track-ui.yml': 'cdf5bde7daa601450ac37df35c0aabde044d7dd5a728ad22fc12433f467fe7fd', 'scripts/deploy-approved-app.py': 'a4f1ff6424123fd00f61cf24550c970f544ce25247e1a1a2f755e47f18bdc917', 'scripts/release-operation-evidence.py': '506796abeadfd87f09236f74ca8c79a496a1c5b813f1191d0f0ce39cf5a9483d', 'scripts/validation-resume.py': '5617f2173b30626d14a5b5efbae182aadc065e707d9ffe2667fa81002303cfb1', 'scripts/release-artifacts/transport.cjs': '9feb936ada29c549c55d876c9112ef6b9b096a1114c2f07ef199b1ce6cb55e44'},)

FAST_TRACK_RECOVERY_TARGET_READ_GENERATIONS = ({
    '.github/workflows/legend-fast-track-ui.yml': 'cdf5bde7daa601450ac37df35c0aabde044d7dd5a728ad22fc12433f467fe7fd',
    'scripts/deploy-approved-app.py': '15ba27c53564223cb41502bb5528949e6435f0e6bccd56a8daea994536dfaa38',
    'scripts/release-operation-evidence.py': '506796abeadfd87f09236f74ca8c79a496a1c5b813f1191d0f0ce39cf5a9483d',
    'scripts/validation-resume.py': '13e4765e67eba52cdac0cf8294dee4a703a82ce672052f6990d9841a997bee44',
    'scripts/release-artifacts/transport.cjs': 'f1b22bcc777114af2437280ee71550555d6b8e2d1701964915eb76de28248557',
},)


# Only Protect carried the rejected extension; siblings remain reconcile-only.
FAST_TRACK_RECOVERY_PROTECT_SERIALIZER_GENERATIONS = ({'.github/workflows/legend-fast-track-ui.yml': 'cdf5bde7daa601450ac37df35c0aabde044d7dd5a728ad22fc12433f467fe7fd', 'scripts/deploy-approved-app.py': '827b760af5db22e44fad4d5edbb46d0e1b4809213c4659d10eda29c77c0de750', 'scripts/release-operation-evidence.py': 'cf73cff19073d290ddff5efe3fdd54f7a901d674f804821e2f0abf32474060c0', 'scripts/validation-resume.py': '5441d151391f8ca60d4b5fa889d1549eecec34b5daef7405b1e5b4b8d2b40572', 'scripts/release-artifacts/transport.cjs': 'a307995fd926511e35a48f2877e4bef580d4cbf61752c34b2282c62a0f774aef'},)


def _recovery_preintent_log_proof(log, run, job, observer, *, kind='serializer'):
    """Only authenticated full logs of the pinned pre-transport rejection flow."""
    import datetime
    if not isinstance(log, str) or len(log) > 8 * 1024 * 1024:
        raise ValueError('RECOVERY_PREINTENT_LOG_UNPROVEN')
    rows = []
    for line in log.lstrip('\ufeff').splitlines():
        stamp, sep, text = line.partition(' ')
        if not sep or not re.fullmatch(r'\d{4}-\d{2}-\d{2}T[0-9:.]+Z', stamp):
            raise ValueError('RECOVERY_PREINTENT_LOG_TRUNCATED')
        rows.append((_completion_time(stamp), text))
    if (not rows or not rows[0][1].startswith("Current runner version: '")
        or any(rows[i][0] > rows[i+1][0] for i in range(len(rows)-1))
        or sum(text == 'Cleaning up orphan processes' for _, text in rows) != 1):
        raise ValueError('RECOVERY_PREINTENT_LOG_INCOMPLETE')
    clean = '\n'.join(text for _, text in rows)
    if set(re.findall(r'(?m)^\[command\]/usr/bin/git log -1 --format=%H\n([a-f0-9]{40})$', clean)) != {run['head_sha']}:
        raise ValueError('RECOVERY_PREINTENT_CHECKOUT_CHANGED')
    start, end = (_completion_time(job[key]) for key in ('started_at', 'completed_at'))
    if end < start or any(stamp < start - datetime.timedelta(seconds=1) or stamp > end + datetime.timedelta(seconds=1) for stamp, _ in rows):
        raise ValueError('RECOVERY_PREINTENT_LOG_OUTSIDE_JOB')
    prep = _completion_step(job, 'Validate original authority and restore retained packages without rebuilding')
    step = _completion_step(job, 'Recover exact retained application bytes', conclusions=('failure',))
    prep_start, prep_end = (_completion_time(prep[key]) for key in ('started_at', 'completed_at'))
    step_start, step_end = (_completion_time(step[key]) for key in ('started_at', 'completed_at'))
    command = '##[group]Run python3 scripts/deploy-approved-app.py --recover-fast-track "$ORIGINAL_RUN" --recovery-revision "$APPLICATION_REVISION" --package-root "$RUNNER_TEMP/retained-recovery"'
    if not (start <= prep_start <= prep_end <= step_start <= step_end <= end):
        raise ValueError('RECOVERY_PREINTENT_STEP_ORDER_CHANGED')
    for expected, lower, upper in [(command + ' --prepare-recovery-only', prep_start, prep_end),
                                    (command, step_start, step_end)]:
        matches = [stamp for stamp,text in rows if text == expected]
        if len(matches) != 1 or not lower <= matches[0] <= upper + datetime.timedelta(seconds=1):
            raise ValueError('RECOVERY_PREINTENT_INVOCATION_UNPROVEN')
    proofs = []
    for stamp, text in rows:
        if text.startswith('{') and 'ORIGINAL_RECOVERY_MATERIALS_VERIFIED' in text:
            if not prep_start <= stamp <= prep_end + datetime.timedelta(seconds=1):
                raise ValueError('RECOVERY_PREINTENT_MATERIAL_OUTSIDE_STEP')
            proofs.append(json.loads(text))
    if len(proofs) != 1:
        raise ValueError('RECOVERY_PREINTENT_MATERIAL_LOG_AMBIGUOUS')
    proof = proofs[0]
    if (set(proof) != {'state','originalRun','originalAttempt','applicationRevision','observer','targets'}
        or proof['state'] != 'ORIGINAL_RECOVERY_MATERIALS_VERIFIED' or proof['observer'] != observer
        or type(proof['originalRun']) is not int or proof['originalRun'] < 1
        or type(proof['originalAttempt']) is not int or proof['originalAttempt'] < 1
        or run.get('display_title') != 'LEGEND retained recovery original=' + str(proof['originalRun']) + ' application=' + str(proof['applicationRevision'])):
        raise ValueError('RECOVERY_PREINTENT_MATERIAL_LOG_CHANGED')
    events = [text for stamp, text in rows if step_start <= stamp <= step_end + datetime.timedelta(seconds=1)]
    if kind == 'target-read':
        terminal = 'DeploymentStatusUnavailable: Azure deployment status unavailable within the bounded read wall budget deadline (before any upload). No deployment or rollback write was replayed; reconcile the exact revision.'
        rejected = "subprocess.TimeoutExpired: Command '['az', 'webapp', 'log', 'deployment', 'list', '-g', 'masterapp-rg', '-n', 'masterapp-website', '--only-show-errors', '-o', 'json']' timed out after 39 seconds"
        cause = 'The above exception was the direct cause of the following exception:'
        if (events.count(terminal) != 1 or clean.count(terminal) != 1 or events.count(rejected) != 1 or clean.count(rejected) != 1
            or events.count('Traceback (most recent call last):') != 2 or events.count(cause) != 1
            or events.count('##[error]Process completed with exit code 1.') != 1
            or any(marker in clean for marker in ['Submitting the verified immutable ZIP once.', 'AZURE_TRACKED_DEPLOYMENT_REFERENCE:'])):
            raise ValueError('RECOVERY_TARGET_READ_TERMINAL_UNPROVEN')
        traces = [i for i, text in enumerate(events) if text == 'Traceback (most recent call last):']
        if not (traces[0] < events.index(rejected) < events.index(cause) < traces[1] < events.index(terminal)
                < events.index('##[error]Process completed with exit code 1.')) or clean.index('Cleaning up orphan processes') < clean.index(terminal):
            raise ValueError('RECOVERY_TARGET_READ_ORDER')
        frames = re.findall(r'  File "([^"\n]+)", line ([0-9]+), in ([^\n]+)', '\n'.join(events))
        normalized = [(path.split('/scripts/', 1)[1] if '/scripts/' in path else path, line, fn) for path, line, fn in frames]
        expected = [('deploy-approved-app.py', '124', 'read_deployments_bounded'), ('deploy-approved-app.py', '1444', 'deployments'),
            ('deploy-approved-app.py', '175', 'deployments'), ('deploy-approved-app.py', '148', 'deployment_read_process'),
            ('/usr/lib/python3.12/subprocess.py', '1209', 'communicate'), ('/usr/lib/python3.12/subprocess.py', '2116', '_communicate'),
            ('/usr/lib/python3.12/subprocess.py', '1253', '_check_timeout'), ('deploy-approved-app.py', '1706', '<module>'),
            ('deploy-approved-app.py', '1621', 'main'), ('deploy-approved-app.py', '1464', 'recover_fast_track'),
            ('deploy-approved-app.py', '1566', '_recover_fast_track'), ('/usr/lib/python3.12/concurrent/futures/_base.py', '449', 'result'),
            ('/usr/lib/python3.12/concurrent/futures/_base.py', '401', '__get_result'), ('/usr/lib/python3.12/concurrent/futures/thread.py', '58', 'run'),
            ('deploy-approved-app.py', '1556', 'recover_target'), ('deploy-approved-app.py', '501', 'reconcile'),
            ('deploy-approved-app.py', '138', 'read_deployments_bounded')]
        if normalized != expected or sum(text.startswith('  File ') for text in events) != len(expected):
            raise ValueError('RECOVERY_TARGET_READ_SOURCE_CHAIN')
        if events.count('    rows = read_deployments_bounded(') != 1 or events.count('    result = reconcile(azure, baseline=journal.baseline, journal=journal,') != 1:
            raise ValueError('RECOVERY_TARGET_READ_BOUNDARY')
        return proof
    if kind == 'target-deadline':
        rejected='RecoveryPhaseDeadline: RECOVERY_PHASE_DEADLINE_HOLD_RECONCILE_ONLY'
        terminal='DeploymentSubmissionNotEntered: Durable upload intent could not be proven; no write authorized'
        if (events.count(rejected)!=1 or clean.count(rejected)!=1 or events.count(terminal)!=1 or clean.count(terminal)!=1
            or events.count('Traceback (most recent call last):')!=2 or events.count('The above exception was the direct cause of the following exception:')!=1
            or events.count('##[error]Process completed with exit code 1.')!=1
            or any(marker in clean for marker in ['Submitting the verified immutable ZIP once.','AZURE_TRACKED_DEPLOYMENT_REFERENCE:'])):
            raise ValueError('RECOVERY_TARGET_DEADLINE_TERMINAL_UNPROVEN')
        if not events.index(rejected)<events.index(terminal)<events.index('##[error]Process completed with exit code 1.') or clean.index('Cleaning up orphan processes')<clean.index(terminal):
            raise ValueError('RECOVERY_TARGET_DEADLINE_ORDER')
        traces=[i for i,text in enumerate(events) if text=='Traceback (most recent call last):']
        cause=events.index('The above exception was the direct cause of the following exception:')
        if not traces[0]<events.index(rejected)<cause<traces[1]<events.index(terminal):raise ValueError('RECOVERY_TARGET_DEADLINE_TRACE_ORDER')
        frames=re.findall(r'  File "([^"\n]+)", line ([0-9]+), in ([^\n]+)', '\n'.join(events))
        normalized=[(path.split('/scripts/',1)[1] if '/scripts/'in path else path,line,fn)for path,line,fn in frames]
        expected=[('deploy-approved-app.py','574','reconcile'),('release-operation-evidence.py','309','before_submit'),('deploy-approved-app.py','1517','before_write'),('deploy-approved-app.py','1341','recovery_preupload_baseline'),('deploy-approved-app.py','1406','recovery_phase_remaining'),('deploy-approved-app.py','1689','<module>'),('deploy-approved-app.py','1604','main'),('deploy-approved-app.py','1454','recover_fast_track'),('deploy-approved-app.py','1549','_recover_fast_track'),('/usr/lib/python3.12/concurrent/futures/_base.py','449','result'),('/usr/lib/python3.12/concurrent/futures/_base.py','401','__get_result'),('/usr/lib/python3.12/concurrent/futures/thread.py','58','run'),('deploy-approved-app.py','1539','recover_target'),('deploy-approved-app.py','582','reconcile')]
        if normalized!=expected or sum(text.startswith('  File ')for text in events)!=len(expected):
            raise ValueError('RECOVERY_TARGET_DEADLINE_SOURCE_CHAIN')
        if events.count('    recovery_phase_remaining(azure.deadline,1)')!=1 or events.count('    fresh = recovery_preupload_baseline(azure, original, key, expected=states[key])')!=1:
            raise ValueError('RECOVERY_TARGET_DEADLINE_BOUNDARY')
        return proof
    if kind == 'preflight':
        terminal = 'DeploymentReconciliationRequired: RECOVERY_BASELINE_NOT_IDLE_OR_UNPROVEN'
        if (events.count(terminal)!=1 or clean.count(terminal)!=1 or events.count('Traceback (most recent call last):')!=1
            or events.count('##[error]Process completed with exit code 1.')!=1
            or any(any(marker in text for marker in ['Submitting the verified immutable ZIP once.','AZURE_TRACKED_DEPLOYMENT_REFERENCE:','Deployment state after ','in recover_target','in before_submit']) for _,text in rows)):
            raise ValueError('RECOVERY_PREFLIGHT_TERMINAL_CHAIN_UNPROVEN')
        if not events.index('Traceback (most recent call last):') < events.index(terminal) < events.index('##[error]Process completed with exit code 1.') or clean.index('Cleaning up orphan processes') < clean.index(terminal):
            raise ValueError('RECOVERY_PREFLIGHT_TERMINAL_ORDER_CHANGED')
        frames=re.findall(r'  File "[^"\n]*/scripts/([^"/]+)", line ([0-9]+), in ([^\n]+)', '\n'.join(events))
        expected=[('deploy-approved-app.py','1577','<module>'),('deploy-approved-app.py','1492','main'),('deploy-approved-app.py','1350','recover_fast_track'),('deploy-approved-app.py','1395','_recover_fast_track'),('deploy-approved-app.py','1259','recovery_preupload_baseline')]
        if frames!=expected or sum(text.startswith('  File ') for text in events)!=len(expected):
            raise ValueError('RECOVERY_PREFLIGHT_CALL_CHAIN_CHANGED')
        if events.count('    states[key] = recovery_preupload_baseline(azure, original, key)')!=1:
            raise ValueError('RECOVERY_PREFLIGHT_BOUNDARY_UNPROVEN')
        return proof
    if kind != 'serializer':raise ValueError('Unknown recovery historical contract')
    terminal = 'DeploymentSubmissionNotEntered: Durable upload intent could not be proven; no write authorized'
    rejected = 'RuntimeError: Durable operation publication/readback unavailable; reconcile without writing'
    if (events.count(terminal) != 1 or clean.count(terminal) != 1 or events.count(rejected) != 1
        or clean.count(rejected) != 1 or events.count('Traceback (most recent call last):') != 2
        or events.count('The above exception was the direct cause of the following exception:') != 1
        or events.count('##[error]Process completed with exit code 1.') != 1
        or any('Submitting the verified immutable ZIP once.' in text or 'AZURE_TRACKED_DEPLOYMENT_REFERENCE:' in text for _,text in rows)):
        raise ValueError('RECOVERY_PREINTENT_TERMINAL_CHAIN_UNPROVEN')
    if not (events.index(rejected) < events.index('The above exception was the direct cause of the following exception:')
            < events.index(terminal) < events.index('##[error]Process completed with exit code 1.')):
        raise ValueError('RECOVERY_PREINTENT_TERMINAL_ORDER_CHANGED')
    if clean.index('Cleaning up orphan processes') < clean.index(terminal):
        raise ValueError('RECOVERY_PREINTENT_CLEANUP_ORDER_CHANGED')
    first = events[events.index('Traceback (most recent call last):') + 1:events.index(rejected)]
    frames = re.findall(r'  File "[^"\n]*/scripts/([^"/]+)", line [0-9]+, in ([^\n]+)', '\n'.join(first))
    expected = [('deploy-approved-app.py','reconcile'), ('release-operation-evidence.py','before_submit'),
                ('release-operation-evidence.py','before_submit'), ('release-operation-evidence.py','publish'),
                ('deploy-approved-app.py','publish_phase_record'), ('release-operation-evidence.py','publish_record')]
    if (frames != expected or sum(text.startswith('  File ') for text in first) != len(expected)
        or not any('in recover_target' in text for text in events)):
        raise ValueError('RECOVERY_PREINTENT_CALL_CHAIN_CHANGED')
    return proof


def attested_recovery_preintent_failure(repository, run, job, attempt, revision, token, *, target=None, operation_id=None):
    """Recognize reviewed pre-entry source flows, never generic missing intent.

    Every new journal record in this exact generation contains recoveryEvidence;
    its pinned transport rejects that field before inventory/upload and before
    the journal can authorize the provider. A separately pinned pre-worker
    generation terminates in sequential baseline initialization. Unknown flows stay held.
    """
    try:
        if (run.get('status') != 'completed' or run.get('conclusion') != 'failure' or job.get('status') != 'completed'
            or job.get('conclusion') != 'failure' or job.get('run_id') != run['id']
            or job.get('run_attempt') != attempt or job.get('head_sha') != run['head_sha']
            or job.get('name') != FAST_TRACK_RECOVERY_JOB):
            return False
        observer = fast_track_recovery_producer(repository, run, token, attempt=attempt)
        if observer['jobId'] != job['id']:
            return False
        expected_steps = [('Set up job','success'), ('Check out promoted recovery tool authority','success'),
            ('Install Node 24','success'), ('Validate original authority and restore retained packages without rebuilding','success'),
            ('Install canonical durable evidence transport','success'), ('Prepare canonical durable evidence transport','success'),
            ('Sign in to Azure Production','success'), ('Recover exact retained application bytes','failure'),
            ('Post Sign in to Azure Production','success'), ('Post Install Node 24','skipped'),
            ('Post Check out promoted recovery tool authority','success'), ('Complete job','success')]
        if ([(step.get('name'),step.get('conclusion')) for step in job.get('steps',[])] != expected_steps
            or any(step.get('status') != 'completed' for step in job['steps'])):
            return False
        source = {path: hashlib.sha256(_release_history_source(repository,run['head_sha'],path,token).encode()).hexdigest()
                  for path in FAST_TRACK_RECOVERY_PREINTENT_GENERATIONS[0]}
        if source not in FAST_TRACK_RECOVERY_PREINTENT_GENERATIONS + FAST_TRACK_RECOVERY_PREFLIGHT_GENERATIONS + FAST_TRACK_RECOVERY_TARGET_DEADLINE_GENERATIONS + FAST_TRACK_RECOVERY_TARGET_READ_GENERATIONS + FAST_TRACK_RECOVERY_PROTECT_SERIALIZER_GENERATIONS:
            return False
        protect_serializer = source in FAST_TRACK_RECOVERY_PROTECT_SERIALIZER_GENERATIONS
        if protect_serializer and (repository != 'MYLEGND/masterapp' or run['id'] != 40356096191 or attempt != 1
            or run['run_attempt'] != 1 or job['id'] != 121688862786
            or run['head_sha'] != '6c21a702e28a0fb4379eb73cbc59c174636f1412' or target != 'protect'
            or revision != PROTECT_RECOVERY_REVISION
            or operation_id != '266170544c4702529f9db56d7af347bc9fc1b0e803a432f149b99f9905a32cb0'):
            return False
        target_read = source in FAST_TRACK_RECOVERY_TARGET_READ_GENERATIONS
        target_scoped = protect_serializer or target_read or source in FAST_TRACK_RECOVERY_TARGET_DEADLINE_GENERATIONS
        if target_read and (repository != 'MYLEGND/masterapp' or run['id'] != 40012751935 or attempt != 1
            or run['run_attempt'] != 1 or job['id'] != 120617015463
            or run['head_sha'] != '3460c10cebf9e0aa247604991a607eafaa047014' or target != 'website'
            or revision != '38756300c87073e5a52a9153046aa09ebe803024'):
            return False
        # An uncertain or retained artifact is NOT erased by a terminal log.
        if _completion_inventory(api_get(repository,f"actions/runs/{run['id']}/artifacts?per_page=100",token),'artifacts'):
            return False
        proof = _recovery_preintent_log_proof(_release_job_log(repository,job['id'],token),run,job,observer,
            kind='serializer' if protect_serializer else 'target-read' if target_read else 'target-deadline' if target_scoped else 'preflight' if source in FAST_TRACK_RECOVERY_PREFLIGHT_GENERATIONS else 'serializer')
        if proof['applicationRevision'] != revision:
            return False
        original = fast_track_completion_original(repository,proof['originalRun'],revision,token)
        if target_read and (proof['originalRun'] != 39960308664 or proof['originalAttempt'] != 5 or set(proof['targets']) != set(RELEASE_TARGETS)):
            return False
        if (not isinstance(proof['targets'],dict) or not 1 <= proof['originalAttempt'] <= original['run']['run_attempt']
            or set(proof['targets']) != set(original['targets'])):
            return False
        if protect_serializer and (proof['originalRun'] != PROTECT_RECOVERY_RUN or proof['originalAttempt'] != 2
            or set(proof['targets']) != set(RELEASE_TARGETS)):
            return False
        held=[]
        for key, row in proof['targets'].items():
            if not isinstance(row,dict) or set(row) != {'operationId','packageDigest','packageArtifactId','packageArtifactDigest'}:
                return False
            material = fast_track_recovery_material(repository,original,key,token)
            if (row['packageArtifactId'] != material['packageArtifact']['id']
                or row['packageArtifactDigest'] != material['packageArtifact']['digest']):
                return False
            # Existing authenticated original intents bind their package digest;
            # otherwise independently restore/check the original immutable ZIP.
            intent = material['originalIntent']
            if protect_serializer and key == 'protect':
                if material.get('protectBaselineCorrection') != protect_recovery_baseline_proof(repository, original, material['packageArtifact'], token):
                    return False
            if target_scoped:
                if intent is None:held.append(key)
                elif fast_track_completion_material(repository,original,key,token)['intent'] != intent:return False
            digest = intent['packageDigest'] if intent is not None else fast_track_recovery_package_digest(repository,original,material,key,token)
            identity = dict(target=key,applicationRevision=revision,packageDigest=digest)
            operation = hashlib.sha256(json.dumps(identity,sort_keys=True,separators=(',',':')).encode()).hexdigest()
            if intent is not None and any(intent.get(name) != value for name,value in
                    dict(identity,operationId=operation,packageProducerRun=original['run']['id']).items()):
                return False
            if row['packageDigest'] != digest or row['operationId'] != operation:
                return False
        if target_scoped and (held!=[target] or proof['targets'][target]['operationId']!=operation_id):return False
        if target_scoped and _completion_inventory(api_get(repository,f"actions/runs/{original['run']['id']}/artifacts?per_page=100",token),'artifacts')!=original['artifacts']:return False
        _completion_original_unchanged(repository, original['run'], token)
        _completion_original_unchanged(repository, run, token)
        if _completion_inventory(api_get(repository,f"actions/runs/{run['id']}/artifacts?per_page=100",token),'artifacts'):
            return False
        return True
    except (KeyError,TypeError,ValueError,RuntimeError,OSError,UnicodeError,subprocess.SubprocessError):
        return False


RECOVERY_PREFLIGHT_NONENTRY_KEYS = frozenset('schemaVersion phase originalRun originalAttempt applicationRevision producingRun producingAttempt observer targets failedTarget stage authorizesMutation writePhaseEntered reason observation'.split())
RECOVERY_PREFLIGHT_NONENTRY_REASONS = frozenset('provider-read-timeout provider-read-unavailable history-empty provider-active runtime-unavailable runtime-candidate-unproven baseline-not-ancestor original-window-unproven provider-history-time-unproven newer-provider-operation baseline-drift preflight-authority-unproven phase-deadline'.split())


def recovery_preflight_nonentry_name(original_run, revision, run, attempt):
    identity=dict(applicationRevision=revision,originalRun=original_run,producingRun=run,producingAttempt=attempt)
    return 'legend-retained-preflight-nonentry-'+hashlib.sha256(json.dumps(identity,sort_keys=True,separators=(',',':')).encode()).hexdigest()


def validate_recovery_preflight_nonentry(record):
    positive=lambda value:type(value) is int and value>0
    sha=lambda value,size:isinstance(value,str) and re.fullmatch('[a-f0-9]{%d}'%size,value) is not None
    if (not isinstance(record,dict) or set(record)!=RECOVERY_PREFLIGHT_NONENTRY_KEYS or record['schemaVersion']!=1
        or type(record['schemaVersion']) is not int or record['phase']!='preflight-nonentry' or record['stage']!='all-target-preflight'
        or record['authorizesMutation'] is not False or record['writePhaseEntered'] is not False
        or record['reason'] not in RECOVERY_PREFLIGHT_NONENTRY_REASONS or not sha(record['applicationRevision'],40)
        or not all(positive(record[k])for k in ['originalRun','originalAttempt','producingRun','producingAttempt'])
        or record['originalRun']==record['producingRun']):raise ValueError('RECOVERY_PREFLIGHT_RECORD_IDENTITY')
    observer=record['observer'];material=record['targets'];o=record['observation']
    if (not isinstance(observer,dict) or set(observer)!={'runId','runAttempt','jobId','toolRevision','promotionRunId'}
        or not all(positive(observer[k])for k in ['runId','runAttempt','jobId','promotionRunId'])
        or not sha(observer['toolRevision'],40) or observer['runId']!=record['producingRun'] or observer['runAttempt']!=record['producingAttempt']
        or not isinstance(material,dict) or not 1<=len(material)<=5 or not isinstance(record['failedTarget'],str) or record['failedTarget'] not in material):raise ValueError('RECOVERY_PREFLIGHT_RECORD_SCOPE')
    for key,row in material.items():
        if (key not in RELEASE_TARGETS or not isinstance(row,dict) or set(row)!={'operationId','packageDigest','packageArtifactId','packageArtifactDigest'}
            or not sha(row['packageDigest'],64) or not positive(row['packageArtifactId'])
            or not isinstance(row['packageArtifactDigest'],str) or not re.fullmatch('sha256:[a-f0-9]{64}',row['packageArtifactDigest'])):raise ValueError('RECOVERY_PREFLIGHT_RECORD_MATERIAL')
        identity=dict(target=key,applicationRevision=record['applicationRevision'],packageDigest=row['packageDigest'])
        if row['operationId']!=hashlib.sha256(json.dumps(identity,sort_keys=True,separators=(',',':')).encode()).hexdigest():raise ValueError('RECOVERY_PREFLIGHT_RECORD_OPERATION')
    if (not isinstance(o,dict) or set(o)!={'historyReadComplete','historyCount','activeCount','runtimeRevision'}
        or type(o['historyReadComplete']) is not bool or (o['runtimeRevision'] is not None and not sha(o['runtimeRevision'],40))):raise ValueError('RECOVERY_PREFLIGHT_RECORD_OBSERVATION')
    if o['historyReadComplete']:
        if type(o['historyCount']) is not int or not 0<=o['historyCount']<=100000 or type(o['activeCount']) is not int or not 0<=o['activeCount']<=o['historyCount']:raise ValueError('RECOVERY_PREFLIGHT_RECORD_COUNTS')
    elif o['historyCount'] is not None or o['activeCount'] is not None:raise ValueError('RECOVERY_PREFLIGHT_RECORD_UNKNOWN_HISTORY')
    reason=record['reason']
    if ((reason in {'provider-read-timeout','provider-read-unavailable'} and o['historyReadComplete'])
        or (reason=='history-empty' and (not o['historyReadComplete'] or o['historyCount']!=0))
        or (reason=='provider-active' and (not o['historyReadComplete'] or o['activeCount']<1))
        or (reason=='runtime-unavailable' and o['runtimeRevision'] is not None)
        or (reason=='runtime-candidate-unproven' and o['runtimeRevision']!=record['applicationRevision'])):raise ValueError('RECOVERY_PREFLIGHT_RECORD_REASON')
    return recovery_preflight_nonentry_name(record['originalRun'],record['applicationRevision'],record['producingRun'],record['producingAttempt'])


def validate_recovery_target_preintent(record):
    if not isinstance(record,dict) or record.get('phase')!='target-preintent-hold' or record.get('stage')!='before-new-intent':
        raise ValueError('RECOVERY_TARGET_HOLD_STAGE')
    if 'writePhaseEntered' in record or record.get('targetWritePhaseEntered') is not False:raise ValueError('RECOVERY_TARGET_HOLD_SCOPE')
    translated=dict(record);translated.pop('targetWritePhaseEntered')
    validate_recovery_preflight_nonentry(dict(translated,phase='preflight-nonentry',stage='all-target-preflight',writePhaseEntered=False))
    identity={k:record[k] for k in ('applicationRevision','originalRun','producingRun','producingAttempt','failedTarget')}
    return 'legend-retained-target-preintent-'+hashlib.sha256(json.dumps(identity,sort_keys=True,separators=(',',':')).encode()).hexdigest()


RECOVERY_PREFLIGHT_PRODUCER_SHA256 = '15ba27c53564223cb41502bb5528949e6435f0e6bccd56a8daea994536dfaa38'


def _recovery_preflight_source_contract(source, journal_source):
    """Closed reviewed source generation, including every helper and write path."""
    # Shared serializer preflight; exact paired generation, never arbitrary helpers.
    if (hashlib.sha256(source.encode()).hexdigest(), hashlib.sha256(journal_source.encode()).hexdigest()) == (
        'dd6c8154aadc3f829204bee307936630d6620e06c416681be65cce2d32fab487',
        '95117a0cd4e1f21bf6407f5324107fdcdfeba582d9ce2123975d567b00664f73'):
        return True
    return (hashlib.sha256(source.encode()).hexdigest() in (RECOVERY_PREFLIGHT_PRODUCER_SHA256, 'a4b0f7958b39790f7fe07fc62df775b32e2e26ba2db2c1bb2a2f4a4dfc6fd850', 'e75833a3c3a4d8827f48b7395a8af7e61eeab9e5cfbb4e1f6a0ebb4af49901ed', 'ef930621e8c36e447fd884ef99bd03f5ce666bf1feda3431158af8e86d5ab6ab',
        # Reviewed supersession generation retains the existing recovery preflight path.
        '6bec907bd7048c7a0e221f9bcfd84a9546eb750f654075a02260f2c606679dbb',
        # Bounded retained Protect baseline correction; unchanged intent boundary.
        '827b760af5db22e44fad4d5edbb46d0e1b4809213c4659d10eda29c77c0de750')
        and hashlib.sha256(journal_source.encode()).hexdigest() in (
            '506796abeadfd87f09236f74ca8c79a496a1c5b813f1191d0f0ce39cf5a9483d',
            '1d84e59c997e1f8b9111ab047277b93a78ae8e73d9e7a9f33c1bbc283eeb543c',
            'cf73cff19073d290ddff5efe3fdd54f7a901d674f804821e2f0abf32474060c0'))


def _recovery_receipt_source_closure(repository, revision, token):
    paths = ('scripts/deploy-approved-app.py', 'scripts/validation-resume.py', 'scripts/release-artifacts/transport.cjs')
    source = {path: hashlib.sha256(_release_history_source(repository, revision, path, token).encode()).hexdigest() for path in paths}
    current = {path: hashlib.sha256((Path(__file__).parent / path.removeprefix('scripts/')).read_bytes()).hexdigest() for path in paths}
    previous = {path: FAST_TRACK_RECOVERY_TARGET_READ_GENERATIONS[0][path] for path in paths}
    read_hold_generation = dict(zip(paths, (
        'a4b0f7958b39790f7fe07fc62df775b32e2e26ba2db2c1bb2a2f4a4dfc6fd850',
        'e940347d70b4fcea6059ca7cb4102c9a9ecd479d4e26e29f016967830f149531',
        'f1b22bcc777114af2437280ee71550555d6b8e2d1701964915eb76de28248557')))
    receipt_reuse_generation = dict(zip(paths, (
        'a4b0f7958b39790f7fe07fc62df775b32e2e26ba2db2c1bb2a2f4a4dfc6fd850',
        '9d6f763cfdf5c738eea6f5073666d542f348fe35a74063f31bc854e60fc15266',
        'f1b22bcc777114af2437280ee71550555d6b8e2d1701964915eb76de28248557')))
    proof_context_generation = dict(zip(paths, (
        'e75833a3c3a4d8827f48b7395a8af7e61eeab9e5cfbb4e1f6a0ebb4af49901ed',
        '45b7270f4f5640bdc7104f0bab0a1602a4f006a86a68fc2b63a28f970d0ad9ab',
        'f1b22bcc777114af2437280ee71550555d6b8e2d1701964915eb76de28248557')))
    return source in (current, previous, read_hold_generation, receipt_reuse_generation, proof_context_generation)


def attested_recovery_terminal_preflight(repository, run, job, attempt, revision, token):
    """Discharge only authenticated no-worker attempts, never prior intent/history."""
    try:
        if (run.get('status')!='completed' or run.get('conclusion')!='failure' or job.get('status')!='completed'
            or job.get('conclusion')!='failure' or job.get('run_id')!=run['id'] or job.get('run_attempt')!=attempt
            or job.get('head_sha')!=run['head_sha'] or job.get('name')!=FAST_TRACK_RECOVERY_JOB):return False
        match=re.fullmatch(r'LEGEND retained recovery original=([1-9][0-9]*) application=([a-f0-9]{40})',run.get('display_title',''))
        if not match or match[2]!=revision:return False
        if not _recovery_preflight_source_contract(_release_history_source(repository,run['head_sha'],'scripts/deploy-approved-app.py',token),_release_history_source(repository,run['head_sha'],'scripts/release-operation-evidence.py',token)):return False
        # The promoted observer must recognize the complete producer authority
        # and transport generation, not merely the outer deployment function.
        if not _recovery_receipt_source_closure(repository, run['head_sha'], token):return False
        original=fast_track_completion_original(repository,int(match[1]),revision,token)
        expected={}
        for key in original['targets']:
            material=fast_track_recovery_material(repository,original,key,token);intent=material['originalIntent']
            digest=intent['packageDigest']if intent is not None else fast_track_recovery_package_digest(repository,original,material,key,token)
            identity=dict(target=key,applicationRevision=revision,packageDigest=digest)
            operation=hashlib.sha256(json.dumps(identity,sort_keys=True,separators=(',',':')).encode()).hexdigest()
            if intent is not None and any(intent.get(k)!=v for k,v in dict(identity,operationId=operation,packageProducerRun=original['run']['id']).items()):return False
            expected[key]=dict(operationId=operation,packageDigest=digest,packageArtifactId=material['packageArtifact']['id'],packageArtifactDigest=material['packageArtifact']['digest'])
        path=f"actions/runs/{run['id']}/artifacts?per_page=100"
        inventory=_completion_inventory(api_get(repository,path,token),'artifacts')
        if not inventory:return False
        seen=set();matched=False
        # All retained artifacts must be independently valid terminal receipts for
        # this exact dispatch/material. Older attempts are not silently discarded.
        for artifact in inventory:
            if (artifact.get('expired')is not False or artifact.get('workflow_run',{}).get('id')!=run['id']
                or artifact.get('workflow_run',{}).get('head_sha')!=run['head_sha']
                or not re.fullmatch('sha256:[a-f0-9]{64}',str(artifact.get('digest','')))):return False
            record=_release_history_json(repository,run['id'],artifact,'operation.json',verify_digest=True)
            if artifact.get('name')!=validate_recovery_preflight_nonentry(record):return False
            producing=record['producingAttempt']
            if producing in seen or producing>run['run_attempt']:return False
            seen.add(producing)
            observer=fast_track_recovery_producer(repository,run,token,attempt=producing)
            if (record['observer']!=observer or record['producingRun']!=run['id'] or record['originalRun']!=original['run']['id']
                or record['applicationRevision']!=revision or not 1<=record['originalAttempt']<=original['run']['run_attempt']or record['targets']!=expected):return False
            jobs=_completion_rows(api_get(repository,f"actions/runs/{run['id']}/attempts/{producing}/jobs?per_page=100",token),'jobs')
            owner=next(x for x in jobs if x['id']==observer['jobId'])
            expected_steps = [('Set up job','success'), ('Check out promoted recovery tool authority','success'),
                ('Install Node 24','success'), ('Validate original authority and restore retained packages without rebuilding','success'),
                ('Install canonical durable evidence transport','success'), ('Prepare canonical durable evidence transport','success'),
                ('Sign in to Azure Production','success'), ('Recover exact retained application bytes','failure'),
                ('Post Sign in to Azure Production','success'), ('Post Install Node 24','skipped'),
                ('Post Check out promoted recovery tool authority','success'), ('Complete job','success')]
            if ([(step.get('name'),step.get('conclusion')) for step in owner.get('steps',[])] != expected_steps
                or any(step.get('status') != 'completed' for step in owner['steps'])):
                return False
            step=_completion_step(owner,'Recover exact retained application bytes',conclusions=('failure',))
            _completion_step(owner,'Validate original authority and restore retained packages without rebuilding')
            if owner.get('status')!='completed'or owner.get('conclusion')!='failure'or not _completion_artifact_in_step(artifact,step):return False
            if producing==attempt:
                if owner!=job:return False
                matched=True
        _completion_original_unchanged(repository,original['run'],token);_completion_original_unchanged(repository,run,token)
        if _completion_inventory(api_get(repository,path,token),'artifacts')!=inventory:return False
        return matched
    except (KeyError,TypeError,ValueError,RuntimeError,OSError,UnicodeError,StopIteration,subprocess.SubprocessError):return False


def recovery_effect_contract(record):
    """Strict retained-operation wire contract before sibling effect coverage.

    Mirrors the existing journal/transport phase contract; provenance is checked
    separately against authenticated producer, package and artifact evidence.
    """
    if not isinstance(record, dict):return False
    phase=record.get('phase')
    if phase not in {'intent','submitted','success'}:return False
    keys=set('schemaVersion operationId target applicationRevision packageDigest baseline packageProducerRun producingRun producingAttempt baselineDeploymentIds phase recoveryEvidence'.split())
    contracts=[key for key in ('providerRequestCorrelation','submissionContract') if key in record]
    if len(contracts)!=1:return False
    keys.update(contracts)
    if phase=='submitted':keys.add('deploymentId')
    if phase=='success':keys.add('deploymentIds')
    if set(record)!=keys or type(record['schemaVersion'])is not int or record['schemaVersion']!=1:return False
    for key,size in [('operationId',64),('packageDigest',64),('applicationRevision',40),('baseline',40)]:
        if not isinstance(record[key],str) or not re.fullmatch('[a-f0-9]{'+str(size)+'}',record[key]):return False
    if any(type(record[key])is not int or not 1<=record[key]<=9007199254740991 for key in ('packageProducerRun','producingRun','producingAttempt')):return False
    ids=record['baselineDeploymentIds']
    def valid_ids(values):
        return isinstance(values,list) and len(values)<=1000 and all(isinstance(v,str) and re.fullmatch(r'[a-zA-Z0-9_.:-]{1,256}',v) for v in values)
    if not valid_ids(ids) or len(set(ids))!=len(ids):return False
    key=contracts[0];kind='azure_onedeploy_windows_intent_v1' if key=='submissionContract' else 'azure_onedeploy_deployment_tag_v1'
    if record[key]!={'kind':kind,'value':record['operationId']}:return False
    if phase=='submitted':
        if key!='submissionContract' or not valid_ids([record['deploymentId']]) or record['deploymentId']in ids:return False
    if phase=='success':
        values=record['deploymentIds']
        if not valid_ids(values) or len(values)!=1 or values[0]in ids:return False
    return True


def attested_recovery_target_hold(repository, run, job, attempt, revision, token, target, operation_id):
    """One held operation; every sibling/attempt effect stays independently bound."""
    try:
        if (run.get('status')!='completed' or run.get('conclusion')!='failure' or job.get('status')!='completed'
            or job.get('conclusion')!='failure' or job.get('run_id')!=run['id'] or job.get('run_attempt')!=attempt
            or job.get('head_sha')!=run['head_sha'] or job.get('name')!=FAST_TRACK_RECOVERY_JOB):return False
        if not _recovery_preflight_source_contract(_release_history_source(repository,run['head_sha'],'scripts/deploy-approved-app.py',token),_release_history_source(repository,run['head_sha'],'scripts/release-operation-evidence.py',token)):return False
        if not _recovery_receipt_source_closure(repository, run['head_sha'], token):return False
        match=re.fullmatch(r'LEGEND retained recovery original=([1-9][0-9]*) application=([a-f0-9]{40})',run.get('display_title',''))
        if not match or match[2]!=revision:return False
        original=fast_track_completion_original(repository,int(match[1]),revision,token)
        expected={}; inherited={}
        for key in original['targets']:
            material=fast_track_recovery_material(repository,original,key,token);intent=material['originalIntent']
            digest=intent['packageDigest'] if intent else fast_track_recovery_package_digest(repository,original,material,key,token)
            identity=dict(target=key,applicationRevision=revision,packageDigest=digest)
            op=hashlib.sha256(json.dumps(identity,sort_keys=True,separators=(',',':')).encode()).hexdigest()
            expected[key]=dict(operationId=op,packageDigest=digest,packageArtifactId=material['packageArtifact']['id'],packageArtifactDigest=material['packageArtifact']['digest'])
            if intent:
                bound=fast_track_completion_material(repository,original,key,token)
                if bound['intent']!=intent or any(intent.get(k)!=v for k,v in dict(identity,operationId=op,packageProducerRun=original['run']['id']).items()):return False
                inherited[key]=intent
        if target not in expected or target in inherited or expected[target]['operationId']!=operation_id:return False
        path=f"actions/runs/{run['id']}/artifacts?per_page=100"
        inventory=_completion_inventory(api_get(repository,path,token),'artifacts')
        count=run.get('run_attempt')
        if type(count)is not int or not 1<=count<=100:return False
        coverage={i:{k:'entered' for k in inherited}for i in range(1,count+1)}
        holds=set();effects={};owners={}
        for i in coverage:
            observer=fast_track_recovery_producer(repository,run,token,attempt=i)
            jobs=_completion_rows(api_get(repository,f"actions/runs/{run['id']}/attempts/{i}/jobs?per_page=100",token),'jobs')
            owner=next(x for x in jobs if x['id']==observer['jobId'])
            expected_steps=[('Set up job','success'),('Check out promoted recovery tool authority','success'),('Install Node 24','success'),('Validate original authority and restore retained packages without rebuilding','success'),('Install canonical durable evidence transport','success'),('Prepare canonical durable evidence transport','success'),('Sign in to Azure Production','success'),('Recover exact retained application bytes','failure'),('Post Sign in to Azure Production','success'),('Post Install Node 24','skipped'),('Post Check out promoted recovery tool authority','success'),('Complete job','success')]
            if (owner.get('status')!='completed' or owner.get('conclusion')!='failure'
                or [(x.get('name'),x.get('conclusion'))for x in owner.get('steps',[])]!=expected_steps
                or any(x.get('status')!='completed' for x in owner['steps'])):return False
            step=_completion_step(owner,'Recover exact retained application bytes',conclusions=('failure',))
            _completion_step(owner,'Validate original authority and restore retained packages without rebuilding')
            owners[i]=(observer,owner,step)
        for artifact in inventory:
            if (artifact.get('expired')is not False or artifact.get('workflow_run',{}).get('id')!=run['id']
                or artifact.get('workflow_run',{}).get('head_sha')!=run['head_sha']
                or not re.fullmatch('sha256:[a-f0-9]{64}',str(artifact.get('digest','')))):return False
            record=_release_history_json(repository,run['id'],artifact,'operation.json',verify_digest=True)
            i=record.get('producingAttempt')
            if type(i)is not int or i not in owners:return False
            observer,owner,step=owners[i]
            if not _completion_artifact_in_step(artifact,step):return False
            if record.get('phase')=='target-preintent-hold':
                key=record.get('failedTarget')
                if (artifact.get('name')!=validate_recovery_target_preintent(record) or record['targets']!=expected
                    or record['observer']!=observer or record['producingRun']!=run['id'] or record['originalRun']!=original['run']['id']
                    or record['applicationRevision']!=revision or not 1<=record['originalAttempt']<=original['run']['run_attempt']
                    or key in inherited or (i,key) in holds or any(k[0]==i and k[1]==key for k in effects)):return False
                holds.add((i,key));coverage[i][key]='held'
            else:
                key=record.get('target');phase=record.get('phase')
                if (key not in expected or phase not in {'intent','submitted','success'} or (i,key)in holds
                    or artifact.get('name')!='legend-release-operation-'+phase+'-'+expected[key]['operationId']
                    or (i,key,phase)in effects or not recovery_effect_contract(record) or not validate_fast_track_recovery_record(repository,run,artifact,record,token)):return False
                if record.get('operationId')!=expected[key]['operationId'] or record.get('packageDigest')!=expected[key]['packageDigest']:return False
                if key in inherited and phase=='intent':return False
                effects[(i,key,phase)]=record
                if phase=='intent':coverage[i][key]='entered'
        entered=dict(inherited);observed_provider={}
        for i in coverage:
            for key in expected:
                new=effects.get((i,key,'intent'))
                if new is not None:
                    if key in entered:return False
                    entered[key]=new
                if key in entered:
                    if (i,key)in holds:return False
                    coverage[i][key]='entered'
            if set(coverage[i])!=set(expected):return False
            for key in expected:
                intent=entered.get(key)
                for phase in ('submitted','success'):
                    value=effects.get((i,key,phase))
                    if value is not None and (intent is None or any(value.get(k)!=v for k,v in intent.items() if k not in {'phase','producingRun','producingAttempt','recoveryEvidence'})):return False
                    if value is not None:
                        provider=value['deploymentId'] if phase=='submitted' else value['deploymentIds'][0]
                        if key in observed_provider and observed_provider[key]!=provider:return False
                        observed_provider[key]=provider
        if (attempt,target)not in holds or owners[attempt][1]!=job:return False
        _completion_original_unchanged(repository,original['run'],token);_completion_original_unchanged(repository,run,token)
        if _completion_inventory(api_get(repository,f"actions/runs/{original['run']['id']}/artifacts?per_page=100",token),'artifacts')!=original['artifacts']:return False
        if _completion_inventory(api_get(repository,path,token),'artifacts')!=inventory:return False
        return True
    except (KeyError,TypeError,ValueError,RuntimeError,OSError,UnicodeError,StopIteration,subprocess.SubprocessError):return False


def recovery_history_no_unjournaled_entry(repository, revision, current_run, current_attempt, token, *, target=None, operation_id=None):
    """No forgotten recovery attempt may be erased by original preflight proof."""
    for run in fast_track_execution_history(repository, token, include_recovery=True):
        if run.get('event') != 'workflow_dispatch':
            continue
        match = re.fullmatch(r'LEGEND retained recovery original=([1-9][0-9]*) application=([a-f0-9]{40})', run.get('display_title', ''))
        if not match:
            raise ReleaseOperationHistoryUnproven('Recovery dispatch scope unavailable')
        if match.group(2) != revision:
            continue
        count = run.get('run_attempt')
        if type(count) is not int or not 1 <= count <= 100:
            raise ReleaseOperationHistoryUnproven('Recovery attempts incomplete')
        for attempt in range(1, count + 1):
            if run['id'] == current_run and attempt == current_attempt:
                continue
            if run.get('status') != 'completed':
                raise ReleaseOperationHistoryUnproven('Competing recovery still active')
            proof = fast_track_recovery_producer(repository, run, token, attempt=attempt)
            jobs = _completion_rows(api_get(repository, f"actions/runs/{run['id']}/attempts/{attempt}/jobs?per_page=100", token), 'jobs')
            owner = next(job for job in jobs if job['id'] == proof['jobId'])
            steps = [step for step in owner.get('steps', []) if step.get('name') == 'Recover exact retained application bytes']
            if len(steps) != 1 or (steps[0].get('conclusion') != 'skipped'
                and not attested_recovery_terminal_preflight(repository, run, owner, attempt, revision, token)
                and not attested_recovery_preintent_failure(repository, run, owner, attempt, revision, token, target=target, operation_id=operation_id)
                and not attested_recovery_target_hold(repository, run, owner, attempt, revision, token, target, operation_id)):
                raise ReleaseOperationHistoryUnproven('Recovery publication entered without retained intent')


def is_fast_track_producer(repository, run):
    return (run.get('path') == '.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW
        and run.get('head_branch') == FAST_TRACK_PR_BASE and run.get('event') == 'push'
        and run.get('head_repository', {}).get('full_name', '').lower() == repository.lower()
        and type(run.get('id')) is int and run['id'] > 0
        and re.fullmatch('[a-f0-9]{40}', run.get('head_sha', '')) is not None)


def _read_fast_track_history_inventory(repository, token, *, clock=time.monotonic, uncached=False):
    path = ('actions/runs?branch=' + urllib.parse.quote(FAST_TRACK_PR_BASE, safe='')
            + '&per_page=100')
    rows = []
    seen = set()
    total = None
    with evidence_lookup_budget(clock() + 30, clock=clock):
        for page in range(1, 11):
            evidence_remaining(30)
            read = _api_get_uncached if uncached else api_get
            payload = read(repository, path if page == 1 else path + f'&page={page}', token)
            evidence_remaining(30)  # A late successful response is not timely proof.
            if not isinstance(payload, dict):
                raise RuntimeError('FAST_TRACK_HISTORY_INCOMPLETE')
            count = payload.get('total_count')
            entries = payload.get('workflow_runs')
            if (type(count) is not int or not 1 <= count <= 1000
                or (total is not None and count != total)
                or not isinstance(entries, list)
                or len(entries) != min(100, count - len(rows))):
                raise RuntimeError('FAST_TRACK_HISTORY_INCOMPLETE')
            total = count
            for row in entries:
                if (not isinstance(row, dict) or type(row.get('id')) is not int
                    or row['id'] < 1 or row['id'] in seen
                    or not isinstance(row.get('head_sha'), str)
                    or not re.fullmatch('[a-f0-9]{40}', row['head_sha'])
                    or row.get('head_branch') != FAST_TRACK_PR_BASE
                    or not isinstance(row.get('head_repository'), dict)
                    or not isinstance(row['head_repository'].get('full_name'), str)
                    or row['head_repository']['full_name'].lower() != repository.lower()
                    or not isinstance(row.get('event'), str) or not row['event']
                    or not isinstance(row.get('path'), str) or not row['path']):
                    raise RuntimeError('FAST_TRACK_HISTORY_PRODUCER_UNPROVEN')
                seen.add(row['id'])
                rows.append(row)
            if len(rows) == total:
                break
        if len(rows) != total:
            raise RuntimeError('FAST_TRACK_HISTORY_INCOMPLETE')
        return rows


def _fast_track_history_raw(rows):
    return json.dumps(sorted(rows, key=lambda row: row['id']), sort_keys=True, separators=(',', ':'))


def _fast_track_history_observation(repository, token, *, clock=time.monotonic):
    state = _RECOVERY_PROOF_VIEW.get()
    if state is None:
        return _read_fast_track_history_inventory(repository, token, clock=clock)
    prior = state['histories'].get(repository)
    if prior is not None:
        evidence_remaining(30)
        return json.loads(prior['raw'])
    rows = _read_fast_track_history_inventory(repository, token, clock=clock, uncached=True)
    raw = _fast_track_history_raw(rows)
    size = len(raw.encode())
    if len(state['histories']) >= 4 or size > 16 * 1024 * 1024 or state['bytes'] + size > 32 * 1024 * 1024:
        raise ReleaseOperationHistoryUnproven('RECOVERY_PROOF_STORAGE_BOUND')
    state['histories'][repository] = dict(raw=raw, token=token)
    state['bytes'] += size
    return json.loads(raw)


def fast_track_execution_history(repository, token, *, clock=time.monotonic, include_recovery=False):
    """Complete bounded branch inventory; missing pages never authorize a write.

    Omit unreliable server head/event filters and the optional first-page marker.
    Validate every page before applying the existing canonical producer predicate.
    One 30-second observation honors shorter enclosing budgets; late replies fail.
    """
    with evidence_lookup_budget(clock() + 30, clock=clock):
        rows = _fast_track_history_observation(repository, token, clock=clock)
        producers = []
        for row in rows:
            if row['path'].split('@')[0] == '.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW:
                if row.get('event') == 'workflow_dispatch':
                    path = '.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW
                    if (row['path'] != path
                        or not re.fullmatch(r'LEGEND retained recovery original=[1-9][0-9]* application=[a-f0-9]{40}', row.get('display_title', ''))
                        or _release_history_source(repository, row['head_sha'], path, token)
                        != (Path(__file__).resolve().parent.parent / path).read_text()):
                        raise RuntimeError('FAST_TRACK_RECOVERY_HISTORY_PRODUCER_UNPROVEN')
                    if include_recovery:
                        producers.append(row)
                    continue
                if not is_fast_track_producer(repository, row):
                    raise RuntimeError('FAST_TRACK_HISTORY_PRODUCER_UNPROVEN')
                producers.append(row)
        evidence_remaining(30)
        return producers



def _audited_fast_track_observer_source(repository, run, token):
    source = _release_history_source(repository, run['head_sha'], 'scripts/release-migration.py', token)
    raw = source.encode()
    blob = hashlib.sha1(b'blob ' + str(len(raw)).encode() + b'\0' + raw).hexdigest()
    # Audited original no-SQL command flow, including main, observation and helpers.
    if blob not in {'6e5fcce433bfbbc83bab2f44067536efbc56c9eb',
                    '0ca98c14d866aee8f2140bd1a1b9efcce1c6c3a1'}:
        return False
    workflow = _release_history_source(repository, run['head_sha'],
                                       '.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW, token)
    blocks = _job_blocks(workflow)
    # Exact original checkout, candidate output/environment, and no alternate command.
    for name, fingerprint in {
        'commerce-gates': '0294271bb94dfede07ae039185352e372bc1ab2d46054ea8bf5a9198e49c7748',
        'migrations': '59c1c0a38da79324a1a8bc08e1f27903d1ce068f983bd32c6a8ff1147cf9c747',
    }.items():
        if hashlib.sha256(blocks.get(name, '').encode()).hexdigest() != fingerprint:
            return False

    return True


def _audited_current_fast_track_observation_failure_source(repository, run, token):
    """Current command compatibility is separate from old no-write receipts."""
    source = _release_history_source(repository, run['head_sha'], 'scripts/release-migration.py', token)
    raw = source.encode()
    if hashlib.sha1(b'blob ' + str(len(raw)).encode() + b'\0' + raw).hexdigest() != '27ab61878183951f0b4bda885637a9ef50c517ea':
        return False
    workflow = _release_history_source(repository, run['head_sha'],
                                       '.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW, token)
    blocks = _job_blocks(workflow)
    return all(hashlib.sha256(blocks.get(name, '').encode()).hexdigest() == digest for name, digest in {
        'commerce-gates': '5dcd32afac471f3401fec56abe10d572c17ff3d7cdcb2f7cc33c9c04475ecb6e',
        'migrations': 'f38fc0e86db32fe62676f44c3e409ae776a417b81159cf2ae7df41c811142ea7',
    }.items())


def _attested_fast_track_observation_failure(repository, run, jobs, attempt, token, *,
                                               current_run=None, current_attempt=None):
    import datetime
    count = run.get('run_attempt')
    current_prior = (run.get('id') == current_run and type(current_attempt) is int
                     and current_attempt == count and type(attempt) is int and attempt < current_attempt)
    if (not is_fast_track_producer(repository, run) or type(attempt) is not int
            or type(count) is not int or not 1 <= attempt <= count <= 100
            or (run.get('status') != 'completed' and not current_prior)):
        return False
    owners = [row for row in jobs if row.get('name') == FAST_TRACK_CHILD_STEPS['migrations'][0]]
    if len(owners) != 1:
        return False
    job = owners[0]
    steps = [row for row in job.get('steps', []) if row.get('name') == FAST_TRACK_CHILD_STEPS['migrations'][1]]
    if (job.get('status') != 'completed' or job.get('conclusion') != 'failure'
            or job.get('run_id') != run['id'] or job.get('run_attempt') != attempt
            or job.get('head_sha') != run['head_sha'] or len(steps) != 1
            or steps[0].get('status') != 'completed' or steps[0].get('conclusion') != 'failure'
            or not (_audited_fast_track_observer_source(repository, run, token)
                    or _audited_current_fast_track_observation_failure_source(repository, run, token))):
        return False
    # In these audited complete sources, migration_stage preserves raw active
    # only during initial schema-observation. Post-bundle observation always
    # becomes schema-verification, which is deliberately not accepted here.
    markers = {
        'Database mutation activity remains active; preserve prior evidence and reconcile without replay.',
        'Migration stage unresolved: schema-observation; preserve prior evidence and reconcile without replay.',
    }
    try:
        start = datetime.datetime.fromisoformat(steps[0]['started_at'].replace('Z', '+00:00'))
        end = datetime.datetime.fromisoformat(steps[0]['completed_at'].replace('Z', '+00:00'))
        if start.tzinfo is None or end.tzinfo is None or end < start:
            return False
        log = _release_job_log(repository, job['id'], token)
        clean = '\n'.join(re.sub(r'^\d{4}-\d{2}-\d{2}T[0-9:.]+Z ', '', line) for line in log.splitlines())
        heads = set(re.findall(r'(?m)^\[command\]/usr/bin/git log -1 --format=%H\n([a-f0-9]{40})$', clean))
        if heads != {run['head_sha']}:
            return False
        terminal = []
        for line in log.splitlines():
            stamp, sep, message = line.partition(' ')
            if sep and message.endswith('; preserve prior evidence and reconcile without replay.'):
                date = datetime.datetime.fromisoformat(stamp.replace('Z', '+00:00'))
                if start <= date <= end + datetime.timedelta(seconds=2):
                    terminal.append(message)
        return len(terminal) == 1 and terminal[0] in markers
    except (KeyError, TypeError, ValueError):
        return False


def attested_fast_track_migration_prewrite(repository, run, jobs, token, *, attempt=None,
                                            current_run=None, current_attempt=None):
    """Authenticate a terminal pre-bundle failure, never infer it from no receipt."""
    import datetime
    # Original observer failures are prewrite even when later sibling reruns
    # succeeded. Authenticate each original invocation, never the later outcome.
    if attempt is None and run.get('status') == 'completed':
        attempt = run.get('run_attempt')  # Original job must independently match this exact attempt.
    if attempt is not None and _attested_fast_track_observation_failure(
            repository, run, jobs, attempt, token, current_run=current_run, current_attempt=current_attempt):
        return True
    if (not is_fast_track_producer(repository, run) or run.get('status') != 'completed'
        or run.get('conclusion') != 'failure' or run.get('run_attempt') != 1):
        return False
    owners = [j for j in jobs if j.get('name') == FAST_TRACK_CHILD_STEPS['migrations'][0]]
    publications = [j for j in jobs if j.get('name', '').startswith('Affected App Deploy — ')
                    or j.get('name') == FAST_TRACK_CHILD_STEPS['routing-cloudflare'][0]]
    if (len(owners) != 1 or len(publications) < 2 or
        any(j.get('conclusion') != 'skipped' for j in publications)):
        return False
    job = owners[0]
    steps = [x for x in job.get('steps', []) if x.get('name') == FAST_TRACK_CHILD_STEPS['migrations'][1]]
    if (job.get('status') != 'completed' or job.get('conclusion') != 'failure'
        or len(steps) != 1 or steps[0].get('status') != 'completed'
        or steps[0].get('conclusion') != 'failure'):
        return False
    # This source labels exceptions from journal.before_mutation before entering
    # the bundle subprocess. Its fixed terminal marker is positive nonentry.
    sources = {
        '.github/workflows/legend-fast-track-ui.yml': 'bb46f6d524b073cbd61f84e53361452a3ed2a370273939098b1d18f8b1df4a64',
        'scripts/release-migration.py': '8e35c1834e2107991051fef5153a9e3dd045838668d0118de27e9d76045c36de',
        'scripts/release-operation-evidence.py': '05192d3b67903f7091d5131960572a6d8f7c9bcea82ec0fb833ca27a71f4493b',
        'scripts/release-artifacts/transport.cjs': 'f0958ce152d4afe79e71956fba1519376352c6d44ba3a98366db761b4cbf80a0',
    }
    try:
        for path, digest in sources.items():
            if hashlib.sha256(_release_history_source(repository, run['head_sha'], path, token).encode()).hexdigest() != digest:
                return False
        inventory = api_get(repository, f"actions/runs/{run['id']}/artifacts?per_page=100", token)
        artifacts = inventory.get('artifacts')
        if (not isinstance(artifacts, list) or type(inventory.get('total_count')) is not int
            or inventory['total_count'] != len(artifacts)
            or any(not isinstance(a, dict) or type(a.get('id')) is not int
                   or a['id'] < 1 or not isinstance(a.get('name'), str) for a in artifacts)
            or len({a['id'] for a in artifacts}) != len(artifacts)
            or any(a['name'].startswith(('legend-release-child-intent-', 'legend-release-child-success-'))
                   for a in artifacts)):
            return False
        start = datetime.datetime.fromisoformat(steps[0]['started_at'].replace('Z', '+00:00'))
        end = datetime.datetime.fromisoformat(steps[0]['completed_at'].replace('Z', '+00:00'))
        if start.tzinfo is None or end.tzinfo is None or end < start:
            return False
        log = _release_job_log(repository, job['id'], token)
        clean = '\n'.join(re.sub(r'^\d{4}-\d{2}-\d{2}T[0-9:.]+Z ', '', line) for line in log.splitlines())
        heads = set(re.findall(r'(?m)^\[command\]/usr/bin/git log -1 --format=%H\n([a-f0-9]{40})$', clean))
        if heads != {run['head_sha']}:
            return False
        marker = 'Migration stage unresolved: mutation-admission; preserve prior evidence and reconcile without replay.'
        stamps = []
        for line in log.splitlines():
            date, sep, message = line.partition(' ')
            if sep and message == marker:
                stamps.append(datetime.datetime.fromisoformat(date.replace('Z', '+00:00')))
        return len(stamps) == 1 and start <= stamps[0] <= end + datetime.timedelta(seconds=2)
    except (KeyError, TypeError, ValueError, RuntimeError, OSError, UnicodeError, subprocess.SubprocessError):
        return False


def _fast_track_router_attempt_evidence(repository, run, jobs, token):
    """Authenticate the reviewed router owner and its complete step-local evidence."""
    import datetime
    if (not is_fast_track_producer(repository, run) or run.get('status') != 'completed'
        or run.get('conclusion') != 'failure'):
        return None
    owners = [j for j in jobs if j.get('name') == FAST_TRACK_CHILD_STEPS['routing-cloudflare'][0]]
    if len(owners) != 1:
        return None
    job = owners[0]
    steps = [x for x in job.get('steps', []) if x.get('name') == FAST_TRACK_CHILD_STEPS['routing-cloudflare'][1]]
    if (job.get('status') != 'completed' or job.get('conclusion') != 'failure'
        or len(steps) != 1 or steps[0].get('status') != 'completed'
        or steps[0].get('conclusion') != 'failure'):
        return None
    sources = {
        '.github/workflows/legend-fast-track-ui.yml': {
            'c69d65590746af9a8bea9e55b9e7c58547edd02182821353309e92f5cb27f09d',
            '74e9f65f99818f768f603bedc197b4360cd4c0f66a22dec2bd20319d7120f35c',
            'ae295e382e3c3dd2b8ff646853e851c752fabc33183ade4b6110bdc1f65561b1',
            '89f03c2792d71f00b3d9061eed01a32eb7841bb421d8c557a12ac287a79ce24d',
            'dae82274dface75d1023178b802f4c90c6170aa77a59b251210e829990ed0515'},
        'scripts/release-router.py': {
            '9457386b4e9fc8f71e5c95cf3c0849439191faf877f71e52a2ccc198b07fbe62',
            '696528f86ba40a8a5edf5814e61908b0bd305ed74c4eb9b05d7981e38dd209a3'}}
    digests = {}
    for path, allowed in sources.items():
        digest = hashlib.sha256(_release_history_source(repository, run['head_sha'], path, token).encode()).hexdigest()
        if digest not in allowed:
            return None
        digests[path] = digest
    inventory = api_get(repository, f"actions/runs/{run['id']}/artifacts?per_page=100", token)
    artifacts = inventory.get('artifacts')
    if (not isinstance(artifacts, list) or type(inventory.get('total_count')) is not int
        or inventory['total_count'] != len(artifacts)
        or any(not isinstance(a, dict) or type(a.get('id')) is not int
               or a['id'] < 1 or not isinstance(a.get('name'), str) for a in artifacts)
        or len({a['id'] for a in artifacts}) != len(artifacts)
        or any(a['name'].startswith(('legend-release-child-intent-', 'legend-release-child-success-'))
               for a in artifacts)):
        return None
    start = datetime.datetime.fromisoformat(steps[0]['started_at'].replace('Z', '+00:00'))
    end = datetime.datetime.fromisoformat(steps[0]['completed_at'].replace('Z', '+00:00'))
    if start.tzinfo is None or end.tzinfo is None or end < start:
        return None
    log = _release_job_log(repository, job['id'], token)
    clean = '\n'.join(re.sub(r'^\d{4}-\d{2}-\d{2}T[0-9:.]+Z ', '', line) for line in log.splitlines())
    heads = set(re.findall(r'(?m)^\[command\]/usr/bin/git log -1 --format=%H\n([a-f0-9]{40})$', clean))
    if heads != {run['head_sha']}:
        return None
    timed, bounded = [], []
    for line in log.splitlines():
        date, sep, message = line.partition(' ')
        if sep and re.fullmatch(r'\d{4}-\d{2}-\d{2}T[0-9:.]+Z', date):
            stamp = datetime.datetime.fromisoformat(date.replace('Z', '+00:00'))
            timed.append((stamp, message))
            if start <= stamp <= end + datetime.timedelta(seconds=2):
                bounded.append(message)
    return dict(start=start, end=end, timed=timed, bounded=bounded, digests=digests)


def attested_fast_track_router_prewrite(repository, run, jobs, token):
    """Prove an authenticated renderer/admission failure preceded durable intent and publication."""
    import ast
    import datetime
    try:
        evidence = _fast_track_router_attempt_evidence(repository, run, jobs, token)
        if evidence is None:
            return False
        renderer_markers = {
            'urllib.error.HTTPError: HTTP Error 500: Internal Server Error',
            'urllib.error.HTTPError: HTTP Error 503: Service Unavailable',
            'TimeoutError: The read operation timed out'}
        admission_statements = {
            'RuntimeError: Release child may have written; missing intent cannot authorize replay':
                "raise rejection('Release child may have written; missing intent cannot authorize replay', run['id'], attempt)",
            'RuntimeError: Release child execution detail unavailable':
                'raise rejection("Release child execution detail unavailable", run_id, attempt)'}
        head_changed = 'RuntimeError: FAST_TRACK_RELEASE_HEAD_CHANGED'
        markers = renderer_markers | set(admission_statements) | {head_changed}
        stamps = [(stamp, message) for stamp, message in evidence['timed'] if message in markers]
        bounded = '\n'.join(evidence['bounded'])
        traces = bounded.split('Traceback (most recent call last):\n')[1:]
        if (len(stamps) != 1 or not evidence['start'] <= stamps[0][0] <= evidence['end'] + datetime.timedelta(seconds=2)
            or len(traces) != 1 or 'FAST_TRACK_ROUTER:' in bounded
            or 'FAST_TRACK_DNS_PROXY_RECONCILIATION:' in bounded):
            return False
        marker = stamps[0][1]
        trace, separator, _ = traces[0].partition(marker)
        if not separator or traces[0].count(marker) != 1:
            return False
        frames = re.findall(r'^  File "(?:[^"\n]*/)?([^/"\n]+)", line (\d+), in (\S+)\n', trace, re.M)
        modern = evidence['digests']['scripts/release-router.py'] == '696528f86ba40a8a5edf5814e61908b0bd305ed74c4eb9b05d7981e38dd209a3'
        entry, renderer, reconcile = ('158', '102', '115') if modern else ('122', '98', '111')
        if marker in renderer_markers:
            return (frames[:3] == [('release-router.py', entry, '<module>'),
                    ('release-router.py', renderer, 'fast_track'), ('release-router.py', '68', 'company_render_ready')]
                and '    company_render_ready(authority)\n' in trace
                and not any(file in {'release-operation-evidence.py', 'validation-resume.py'}
                            or function in {'publish', 'reconcile_tagged_router', 'before_mutation'}
                            for file, _, function in frames))
        history_source = _release_history_source(repository, run['head_sha'], 'scripts/validation-resume.py', token)
        tree = ast.parse(history_source)

        def reviewed_function(name, digests):
            nodes = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == name]
            if (len(nodes) != 1 or hashlib.sha256(ast.get_source_segment(history_source, nodes[0]).encode()).hexdigest()
                not in digests):
                return None
            return nodes[0]

        def statement_line(owner, statement):
            nodes = [n for n in ast.walk(owner) if isinstance(n, (ast.Raise, ast.Call))
                     and ast.get_source_segment(history_source, n) == statement]
            return str(nodes[0].lineno) if len(nodes) == 1 else None

        # Body hashes bind the audited execution boundary; AST locates only its
        # exact statements. Unrelated registry growth cannot invalidate a trace.
        if marker == head_changed:
            admission = reviewed_function('fast_track_source_admission', {
                'b942d143fd1df58b61a82ea1a294cc4bf26a74a22ba1ddfb6b5c41cb5837027d',
                '091a73b73b5c2d5b33b6ff41883c3468609893ad2ea18d11661b30b557c5e714'})
            execution = reviewed_function('assert_fast_track_release_execution', {
                '148a6578588727d821fb148315f19d737b86e9262b7b2f80ca08a9ff6d461055',
                'bba7a39610940effe5f06d54feea5aba3d3c269ad90fcc4b7dadc9c4ce24bb3b'})
            if not modern or admission is None or execution is None:
                return False
            call = statement_line(admission, 'assert_fast_track_release_execution()')
            denial = statement_line(execution, "raise RuntimeError('FAST_TRACK_RELEASE_HEAD_CHANGED')")
            return (call is not None and denial is not None
                and frames == [('release-router.py', entry, '<module>'), ('release-router.py', '82', 'fast_track'),
                    ('validation-resume.py', call, 'fast_track_source_admission'),
                    ('validation-resume.py', denial, 'assert_fast_track_release_execution')]
                and "    authority.fast_track_source_admission()\n" in trace
                and "    raise RuntimeError('FAST_TRACK_RELEASE_HEAD_CHANGED')\n" in trace)
        journal_source = _release_history_source(repository, run['head_sha'], 'scripts/release-operation-evidence.py', token)
        if hashlib.sha256(journal_source.encode()).hexdigest() != '05192d3b67903f7091d5131960572a6d8f7c9bcea82ec0fb833ca27a71f4493b':
            return False
        guard = reviewed_function('release_child_first_write_proven', {
            'ffc6a6edca0acea2f81e245a31f28d3a1eebd79ae5907af762f453c3108c2601',
            '5c031a86872fa30715f7b4306584e4795b32f4620e8bc5acd6bbbc6556471f1f',
            '0ce0b9d586728d569399335a80076bea5011ffa5d265f6150c7c85d0d892f2de'})
        if guard is None:
            return False
        denial = statement_line(guard, admission_statements[marker])
        return (denial is not None and frames == [('release-router.py', entry, '<module>'),
                ('release-router.py', reconcile, 'fast_track'), ('release-router.py', '40', 'reconcile_tagged_router'),
                ('release-operation-evidence.py', '262', 'before_mutation'),
                ('validation-resume.py', denial, 'release_child_first_write_proven')]
            and "    journal.before_mutation({'providerVersion': version or 'absent'})\n" in trace
            and '    ' + admission_statements[marker] + '\n' in trace)
    except (KeyError, TypeError, ValueError, RuntimeError, OSError, UnicodeError, SyntaxError, subprocess.SubprocessError):
        return False


def fast_track_child_attempts(repository, child, token, *, current_run=None, current_attempt=None):
    if child not in FAST_TRACK_CHILD_STEPS:
        return []
    owner_name, step_name = FAST_TRACK_CHILD_STEPS[child]

    def inspect_run(run):
        attempts = []
        if run['id'] != current_run and run.get('status') != 'completed':
            raise RuntimeError('FAST_TRACK_RESOURCE_WORKER_ACTIVE')
        source = _release_history_source(repository, run['head_sha'], '.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW, token)
        raw = source.encode()
        blob = hashlib.sha1(b'blob ' + str(len(raw)).encode() + b'\0' + raw).hexdigest()
        if blob in FAST_TRACK_PRE_MIGRATION_SOURCES:
            return []
        if step_name not in named_step_blocks(source):
            raise RuntimeError('FAST_TRACK_CHILD_SOURCE_UNPROVEN')
        count = run.get('run_attempt')
        if type(count) is not int or not 1 <= count <= 100:
            raise RuntimeError('FAST_TRACK_ATTEMPTS_INCOMPLETE')
        saw_owner = False
        for attempt in range(1, count + 1):
            if run['id'] == current_run and attempt == current_attempt:
                continue
            payload = api_get(repository, f"actions/runs/{run['id']}/attempts/{attempt}/jobs?per_page=100", token)
            jobs = payload.get('jobs')
            if not isinstance(jobs, list) or payload.get('total_count') != len(jobs):
                raise RuntimeError('FAST_TRACK_JOBS_INCOMPLETE')
            if not jobs and (run.get('status') == 'completed' or attempt < count):
                continue
            owners = [j for j in jobs if j.get('name') == owner_name]
            if not owners and attempt > 1 and saw_owner:
                continue  # Complete attempt inventory proves this sibling was not rerun.
            if len(owners) != 1:
                raise RuntimeError('FAST_TRACK_CHILD_OWNER_UNPROVEN')
            saw_owner = True
            job = owners[0]
            if job.get('conclusion') == 'skipped' or job.get('status') == 'queued':
                continue
            steps = [s for s in job.get('steps', []) if s.get('name') == step_name]
            if len(steps) != 1:
                raise RuntimeError('FAST_TRACK_CHILD_STEP_UNPROVEN')
            if steps[0].get('conclusion') == 'skipped' or steps[0].get('status') == 'queued':
                continue
            if child == 'migrations' and attested_fast_track_migration_prewrite(
                    repository, run, jobs, token, attempt=attempt,
                    current_run=current_run, current_attempt=current_attempt):
                continue
            if child == 'routing-cloudflare' and attested_fast_track_router_prewrite(repository, run, jobs, token):
                continue
            attempts.append((run, attempt, job, steps[0]))
        return attempts

    # Each worker inherits the caller's single evidence deadline. Historical
    # attempts within a run stay ordered; all producers must finish before any
    # evidence is returned. A failed or timed-out read never becomes absence.
    runs = fast_track_execution_history(repository, token)
    pool = concurrent.futures.ThreadPoolExecutor(max_workers=6)
    futures = []
    try:
        futures = [pool.submit(contextvars.copy_context().run, inspect_run, run) for run in runs]
        groups = [future.result(timeout=evidence_remaining(120)) for future in futures]
    except concurrent.futures.TimeoutError as error:
        raise EvidenceLookupUnavailable("Evidence lookup deadline exhausted; preserve completed receipts") from error
    finally:
        # Never extend the caller deadline by joining unrelated pending reads.
        # api_get independently bounds every active request by that same deadline.
        pool.shutdown(wait=False, cancel_futures=True)
    return [attempt for group in groups for attempt in group]


def release_child_history(repository, child, dependency_identity, token, phase):
    """Read independently durable children even when their release parent failed."""
    import tempfile
    if child not in DIRECT_RELEASE_CHILDREN or phase not in {"intent", "success"}:
        raise ValueError("Unknown release child or phase")
    if not re.fullmatch(r"[a-f0-9]{64}", dependency_identity):
        raise ValueError("Invalid release child identity")
    name = f"legend-release-child-{phase}-{dependency_identity}"
    payload = api_get(repository, "actions/artifacts?name=" + urllib.parse.quote(name, safe="") + "&per_page=100", token)
    rows = payload.get("artifacts")
    if not isinstance(rows, list) or payload.get("total_count", 0) > len(rows):
        raise RuntimeError("Release child history incomplete")
    records = []
    for artifact in rows:
        if artifact.get("expired") or artifact.get("name") != name:
            raise RuntimeError("Release child evidence missing or expired")
        run_id = artifact.get("workflow_run", {}).get("id")
        run = api_get(repository, f"actions/runs/{run_id}", token)
        if not is_fast_track_producer(repository, run) and (run.get("path") != ".github/workflows/" + DIRECT_RELEASE_WORKFLOW or
                run.get("head_branch") != TRUSTED_PR_BASE or run.get("event") != "workflow_dispatch" or
                run.get("head_repository", {}).get("full_name", "").lower() != repository.lower()):
            raise RuntimeError("Untrusted release child producer")
        with tempfile.TemporaryDirectory(prefix="legend-child-") as temporary:
            _download_run_artifact(repository, run_id, name, Path(temporary))
            path = Path(temporary) / "operation.json"
            if path.stat().st_size > 32768:
                raise RuntimeError("Oversized release child evidence")
            record = json.loads(path.read_text())
        if (record.get("schemaVersion") != 1 or record.get("child") != child or
                record.get("dependencyIdentity") != dependency_identity or
                record.get("phase") != phase or record.get("producingRun") != run_id):
            raise RuntimeError("Release child receipt identity mismatch")
        _validate_child_generation(repository, run, artifact, record, child, token, phase)
        records.append(record)
    if records and any(row != records[0] for row in records[1:]):
        raise RuntimeError("Competing release child receipts")
    return records[0] if records else None


def _trusted_child_producer(repository, run):
    if is_fast_track_producer(repository, run):
        return
    if (type(run.get("id")) is not int or run["id"] < 1
            or run.get("path") != ".github/workflows/" + DIRECT_RELEASE_WORKFLOW
            or run.get("head_branch") != TRUSTED_PR_BASE
            or run.get("event") != "workflow_dispatch"
            or run.get("head_repository", {}).get("full_name", "").lower() != repository.lower()
            or not re.fullmatch(r"[a-f0-9]{40}", run.get("head_sha", ""))):
        raise RuntimeError("Release child execution history has an untrusted producer")


def _validate_child_generation(repository, run, artifact, record, child, token, phase="intent"):
    """Bind a retained physical operation to its actual public material identity."""
    _trusted_child_producer(repository, run)
    run_id = run["id"]
    identity = record.get("dependencyIdentity", "")
    material = record.get("materialIdentity", "")
    execution = record.get("executionAuthority", "")
    attempt = record.get("producingAttempt")
    if (record.get("schemaVersion") != 1 or record.get("child") != child
            or record.get("phase") != phase or record.get("producingRun") != run_id
            or type(attempt) is not int or attempt < 1 or attempt > run.get("run_attempt", 1)
            or not re.fullmatch(r"[a-f0-9]{64}", identity)
            or not re.fullmatch(r"[a-f0-9]{64}", material)
            or not re.fullmatch(r"[a-f0-9]{40}", execution)
            or artifact.get("name") != f"legend-release-child-{phase}-" + identity):
        raise RuntimeError("Historical release child generation is malformed")
    if is_fast_track_producer(repository, run):
        if child not in FAST_TRACK_CHILD_STEPS or execution != run['head_sha'] or record.get('applicationRevision') != execution:
            raise RuntimeError('Fast-track child authority does not match its exact producer')
        if direct_child_operation_identity(child, execution, material) != identity:
            raise RuntimeError('Historical release child physical identity is inconsistent')
        if record.get('evidenceIdentity') != direct_child_identity(child, execution, material, workflow=FAST_TRACK_RELEASE_WORKFLOW):
            raise RuntimeError('Fast-track child execution contract is inconsistent')
        return identity
    title = re.fullmatch(r"LEGEND release pr=[0-9]+ candidate=[a-f0-9]{40} authority=([a-f0-9]{40})", run.get("display_title", ""))
    if title is None or title.group(1) != execution:
        raise RuntimeError("Historical release child generation lacks bound checkout authority")
    comparison = api_get(repository, f"compare/{execution}...{run['head_sha']}", token)
    if comparison.get("status") not in {"ahead", "identical"}:
        raise RuntimeError("Historical release child authority is not protected event ancestry")
    if direct_child_operation_identity(child, execution, material) != identity:
        raise RuntimeError("Historical release child physical identity is inconsistent")
    return identity


_RELEASE_CHILD_NOOP_PROOFS = set()

# Retired, content-attested workflow generations with a true early-exit branch.
# This is SOURCE-based, not another run-ID-specific admission bypass. Each
# observed historical step still requires its own trusted Git head, completed
# step timestamps and exact one-line no-migration marker.
LEGACY_NOOP_MARKER = (
    'No candidate migration source changed from the database baseline; '
    'migration receipt gate is not applicable.'
)
LEGACY_OLDER_NOOP_MARKER = (
    'No candidate migration source changed from the proven database baseline.'
)
# Exact original Git blob fingerprints bind marker interpretation to audited
# executable workflow sources; unknown historical generations fail closed.
AUDITED_LEGACY_MIGRATION_NOOP_SOURCE_MARKERS = {
    'ad23c687da0e5dadf7c92a777cf26f71947e9bb0': LEGACY_NOOP_MARKER,
    'd4404ba420b96623602f953b91f3fe1f31911433': LEGACY_NOOP_MARKER,
    'b85c50eadf83daad32b787f2d37f7a32babe67a4': LEGACY_NOOP_MARKER,
    '207f8bc230e35e636e1342e020d2377feadeac2e': LEGACY_OLDER_NOOP_MARKER,
    # Oct 2 immutable generation: migration-step source matches the audited
    # early-exit block above, and original run evidence proves the old marker.
    '0bf49879417aa46c25c5f1a5772912b280efa9ae': LEGACY_OLDER_NOOP_MARKER,
    # Oct 2 successful single-target publication: identical audited early
    # migration exit with original checkout and timestamped no-change output.
    '59887daf87bbbf3be14ade23de17076adb8830ea': LEGACY_OLDER_NOOP_MARKER,
}


def _audited_legacy_migration_noop_marker(source):
    raw = source.encode('utf-8')
    blob = hashlib.sha1(b'blob ' + str(len(raw)).encode() + b'\0' + raw).hexdigest()
    return AUDITED_LEGACY_MIGRATION_NOOP_SOURCE_MARKERS.get(blob)


def _audited_legacy_migration_noop_source(source):
    return _audited_legacy_migration_noop_marker(source) is not None


# Retired EF CLI workflows, observed across the complete original history.
# Every successful step below reported BOTH EF's no-migrations-applied output
# and the original post-update schema-ready marker, within its own timestamps.
# These source identities never establish nonentry or success on their own.
AUDITED_LEGACY_CLI_NOOP_SOURCES = frozenset({
    '517c091a12aff011e7b1950b791ac9ebd6b76798',
    '6623819e2ad64017a3c9f5b86f79ded7a98ffa05',
    'db90757c5f68df5e0117abb7488a38e41b134ac2',
    '8dfce1bde2e261653640203d4c4a8bf24b99a95d',
    '0a6c6363cbc4c8d8507cd08ed4f78eee1d161de0',
    '1a2c68f5964ac0b7d304127937b5c06344a0469f',
    'b7da53d2750d8a5e939683b14439a39bb0c705c6',
    '2c8cea120b80d38e3b02b1c650d960ab520b8b96',
    '2ac40602ed4330a6df1cce78002e4b21ba571064',
})


def _legacy_migration_noop(repository, run, job, step, source, token):
    """Prove the exact retired migration step exited before any schema operation."""
    import datetime
    legacy = 'Apply additive diagnostics migrations before restarting apps'
    raw_source = source.encode('utf-8')
    source_blob = hashlib.sha1(b'blob ' + str(len(raw_source)).encode()
                              + b'\0' + raw_source).hexdigest()
    if source_blob in AUDITED_LEGACY_CLI_NOOP_SOURCES:
        # The CLI was invoked, so a skipped step or successful parent is not
        # sufficient. Prove the original EF command applied zero migrations.
        if (run.get('status') != 'completed'
            or job.get('status') != 'completed'
            or job.get('conclusion') not in {'success', 'failure'}
            or type(job.get('id')) is not int or job['id'] < 1
            or step.get('name') != legacy or step.get('status') != 'completed'
            or step.get('conclusion') != 'success'):
            return False
        try:
            _trusted_child_producer(repository, run)
            start = datetime.datetime.fromisoformat(step['started_at'].replace('Z', '+00:00'))
            end = datetime.datetime.fromisoformat(step['completed_at'].replace('Z', '+00:00'))
            if start.tzinfo is None or end.tzinfo is None or end < start:
                return False
            raw = _release_job_log(repository, job['id'], token)
            clean = '\n'.join(re.sub(r'^\d{4}-\d\d-\d\dT[0-9:.]+Z ', '', line)
                              for line in raw.splitlines())
            heads = set(re.findall(
                r'(?m)^\[command\]/usr/bin/git log -1 --format=%H\n([a-f0-9]{40})$', clean))
            if heads != {run['head_sha']} or 'Post job cleanup.' not in clean:
                return False
            outputs = []
            for line in raw.splitlines():
                stamp, sep, message = line.partition('Z ')
                if not sep or not re.fullmatch(r'\d{4}-\d\d-\d\dT\d\d:\d\d:[0-9.]+', stamp):
                    continue
                observed = datetime.datetime.fromisoformat(stamp + '+00:00')
                if start <= observed <= end + datetime.timedelta(seconds=2):
                    outputs.append(message)
            return (outputs.count('No migrations were applied. The database is already up to date.') == 1
                    and outputs.count('Schema ready. Applied candidate migrations only; no down migrations.') == 1
                    and not any('Applying migration' in value or 'Reverting migration' in value
                                for value in outputs))
        except (KeyError, TypeError, ValueError, AttributeError, RuntimeError,
                OSError, UnicodeError, subprocess.SubprocessError):
            return False
    if step.get('conclusion') == 'failure':
        # Existing first-SQL-write proof, not another authority. This exact
        # retired workflow checks a signed baseline BEFORE calling PYMIGRATE.
        # A failure alone NEVER proves no SQL: authenticate its original
        # immutable source, completed GitHub history and a real denial output.
        import datetime
        marker = ('Live database baseline lacks a successful canonical release '
                  'receipt; no migration authorized.')
        data = source.encode('utf-8')
        blob = hashlib.sha1(
            b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()
        if (blob != '207f8bc230e35e636e1342e020d2377feadeac2e'
            or run.get('status') != 'completed'
            or run.get('conclusion') != 'failure'
            or run.get('run_attempt') != 1
            or job.get('status') != 'completed'
            or job.get('conclusion') != 'failure'
            or (job.get('run_attempt') is not None and job['run_attempt'] != 1)
            or type(job.get('id')) is not int or job['id'] < 1
            or step.get('name') != legacy or step.get('status') != 'completed'):
            return False
        original_step = named_step_blocks(
            _job_blocks(source).get('release', '')).get(legacy, '')
        before = original_step.find('if not module.release_proven(')
        denial = original_step.find(marker)
        execution = original_step.find("python3 - <<'PYMIGRATE'")
        if not (0 <= before < denial < execution):
            return False
        try:
            _trusted_child_producer(repository, run)
            run_id = run['id']
            payload = api_get(
                repository, f'actions/runs/{run_id}/artifacts?per_page=100',
                token)
            rows = payload.get('artifacts')
            if (type(payload.get('total_count')) is not int
                or payload['total_count'] != 1
                or not isinstance(rows, list) or len(rows) != 1
                or rows[0].get('expired') is not False
                or (rows[0].get('workflow_run') or {}).get('id') != run_id
                or not re.fullmatch(
                    rf'legend-release-step-state-[a-f0-9]{{40}}-{run_id}-1',
                    rows[0].get('name', ''))):
                return False
            steps = job.get('steps')
            if not isinstance(steps, list):
                return False
            for label in (
                'Publish selected head as one transaction',
                'Deploy and activate LEGEND Founder Cloudflare baseline',
                'Reconcile public custom-hostname Cloudflare policy',
                'Deploy shared Cloudflare business website router',
                'Verify every deployed target and collect all failures',
                'Retain exact approved release receipt',
            ):
                matches = [x for x in steps if x.get('name') == label]
                if (len(matches) != 1
                    or matches[0].get('conclusion') != 'skipped'):
                    return False
            start = datetime.datetime.fromisoformat(
                step['started_at'].replace('Z', '+00:00'))
            end = datetime.datetime.fromisoformat(
                step['completed_at'].replace('Z', '+00:00'))
            if start.tzinfo is None or end.tzinfo is None or end < start:
                return False
            log = _release_job_log(repository, job['id'], token)
            clean = '\n'.join(
                re.sub(r'^\d{4}-\d{2}-\d{2}T[0-9:.]+Z ', '', line)
                for line in log.splitlines())
            checkouts = re.findall(
                r'(?m)^\[command\]/usr/bin/git log -1 --format=%H\n([a-f0-9]{40})$',
                clean)
            if checkouts != [run['head_sha']]:
                return False
            observed = []
            for line in log.splitlines():
                stamp, sep, output = line.partition('Z ')
                if sep and output == marker:
                    observed.append(
                        datetime.datetime.fromisoformat(stamp + '+00:00'))
            if (len(observed) != 1 or observed[0].tzinfo is None
                or not start <= observed[0] <= end + datetime.timedelta(seconds=2)):
                return False
            completed = ('Schema ready. Executed the exact validated migration '
                         'bundle from the proven live database baseline.')
            # The Actions echo of source code is NOT a successful SQL result.
            if any(row.partition('Z ')[2] == completed
                   for row in log.splitlines()):
                return False
            return True
        except (KeyError, TypeError, ValueError, AttributeError, RuntimeError,
                OSError, UnicodeError, subprocess.SubprocessError):
            return False
    block = named_step_blocks(_job_blocks(source).get('release', '')).get(legacy, '')
    # A release job can fail *after* the migration no-op step; require the
    # exact immutable source and its timestamp-bound no-write proof, rather
    # than mistaking the failed parent for a SQL mutation.
    trusted_source = _audited_legacy_migration_noop_source(source)
    original_block = (hashlib.sha256(block.encode()).hexdigest()
        == '74500e6966d2c198564712b33c93a1a06150d99bdc3e4aacc4e79fe11ad061cc')
    if (not (original_block or trusted_source)
        or job.get('status') != 'completed'
        or job.get('conclusion') not in {'success', 'failure', 'cancelled'}
        or type(job.get('id')) is not int
        or step.get('name') != legacy or step.get('status') != 'completed'
        or step.get('conclusion') != 'success'):
        return False
    release = _job_blocks(source).get('release', '')
    context = {'environment': _workflow_top_level_field(source, 'env'),
               'defaults': _workflow_top_level_field(source, 'defaults'),
               'runtime': release.split('    steps:', 1)[0]}
    if (not trusted_source and
        hashlib.sha256(json.dumps(context, sort_keys=True).encode()).hexdigest() != '0b2c73c15828a62dc7440c79fe6ae3ac072af6a2442f1f5d52f31eeebd79629a'):
        return False
    # A parent CANCELLED during later publication can still have a completely
    # finished migration no-op step. The original immutable workflow exits
    # BEFORE PYMIGRATE on the exact output marker authenticated below.
    # Require the cancelled run's entire original artifact inventory so a
    # retained migration intent never becomes permission to replay SQL.
    if job.get('conclusion') == 'cancelled':
        if (run.get('status') != 'completed' or run.get('conclusion') != 'cancelled'
            or run.get('run_attempt') != 1):
            return False
        try:
            _trusted_child_producer(repository, run)
            inventory = api_get(
                repository, f"actions/runs/{run['id']}/artifacts?per_page=100",
                token)
            rows = inventory.get('artifacts')
            if (type(inventory.get('total_count')) is not int
                or not isinstance(rows, list) or len(rows) != inventory['total_count']
                or len(rows) > 100
                or any(not isinstance(x, dict)
                       or x.get('expired') is not False
                       or (x.get('workflow_run') or {}).get('id') != run['id']
                       or not isinstance(x.get('name'), str) for x in rows)):
                return False
            names = [x['name'] for x in rows]
            if (len(names) != len(set(names))
                or sum(bool(re.fullmatch(
                    rf'legend-release-step-state-[a-f0-9]{{40}}-{run["id"]}-1',
                    name)) for name in names) != 1
                or any(name.startswith((
                    'legend-release-child-intent-',
                    'legend-release-child-success-'))
                       for name in names)):
                return False
        except (KeyError, TypeError, ValueError, AttributeError, RuntimeError,
                OSError, UnicodeError, subprocess.SubprocessError):
            return False
    try:
        start = datetime.datetime.fromisoformat(step['started_at'].replace('Z', '+00:00'))
        end = datetime.datetime.fromisoformat(step['completed_at'].replace('Z', '+00:00'))
        if start.tzinfo is None or end.tzinfo is None or end < start:
            return False
    except (KeyError, TypeError, ValueError, AttributeError):
        return False
    key = (repository, job['id'], run['head_sha'], step['started_at'], step['completed_at'])
    if job.get('conclusion') != 'cancelled' and key in _RELEASE_CHILD_NOOP_PROOFS:
        return True
    raw = _release_job_log(repository, job['id'], token)
    text = '\n'.join(re.sub(r'^\d{4}-\d{2}-\d{2}T[0-9:.]+Z ', '', line) for line in raw.splitlines())
    heads = re.findall(r'(?m)^\[command\]/usr/bin/git log -1 --format=%H\n([a-f0-9]{40})$', text)
    if len(heads) != 1:
        return False
    if heads[0] != run['head_sha']:
        # A historical successful release may use a protected, earlier
        # selected commit than its triggering event head. Authenticate the
        # immutable source and original selected SHA before accepting it.
        selected = set(re.findall(
            r'(?m)^  RELEASE_SHA: ([a-f0-9]{40})$', text))
        # Original GitHub Actions logs repeat the identical selected commit
        # in multiple step environment blocks. Authenticate one DISTINCT
        # selected SHA, the exact checkout and protected ancestry.
        if (not trusted_source or len(selected) != 1
            or heads[0] not in selected):
            return False
        ancestry = subprocess.run(
            ['git', 'merge-base', '--is-ancestor', heads[0], run['head_sha']],
            capture_output=True, text=True, check=False)
        if ancestry.returncode != 0:
            return False
    marker = _audited_legacy_migration_noop_marker(source) or LEGACY_NOOP_MARKER
    hits = 0
    for line in raw.splitlines():
        stamp, separator, message = line.partition(' ')
        if not separator or message != marker:
            continue
        try:
            observed = datetime.datetime.fromisoformat(stamp.replace('Z', '+00:00'))
        except ValueError:
            return False
        if observed.tzinfo is None or not start <= observed < end + datetime.timedelta(seconds=1):
            return False
        hits += 1
    if hits != 1:
        return False
    _RELEASE_CHILD_NOOP_PROOFS.add(key)
    return True



def _applied_migration_prefix_separate(prior, current_application_revision,
                                       first_pending_migration_id, last_applied_migration_id):
    """Only unchanged, fully applied older migration files may precede new SQL."""
    if (not all(re.fullmatch('[a-f0-9]{40}', str(value)) for value in (prior, current_application_revision))
        or prior == current_application_revision
        or not all(re.fullmatch('[0-9]{8,14}_[A-Za-z0-9_]{1,128}', str(value))
                   for value in (first_pending_migration_id, last_applied_migration_id))):
        return False
    try:
        if subprocess.run(['git', 'merge-base', '--is-ancestor', prior, current_application_revision],
                          check=False, capture_output=True).returncode:
            return False
        def migration_files(revision):
            result = subprocess.run(['git', 'ls-tree', '-r', '-z', revision, '--', 'Infrastructure/Migrations'],
                                    check=True, capture_output=True)
            files = {}
            for entry in result.stdout.decode().split('\0'):
                if not entry:
                    continue
                metadata, path = entry.split('\t', 1)
                name = path.removeprefix('Infrastructure/Migrations/')
                if re.fullmatch(r'[0-9]{8,14}_[A-Za-z0-9_]+(?:\.Designer)?\.cs', name):
                    files[name] = metadata.split()[2]
            return files
        before = migration_files(prior)
        after = migration_files(current_application_revision)
        if not before or any(after.get(name) != sha for name, sha in before.items()):
            return False
        prior_ids = sorted(name[:-3].removesuffix('.Designer')
                           for name in before if not name.endswith('.Designer.cs'))
        if not prior_ids:
            return False
        latest_prior = prior_ids[-1]
        additions = sorted(set(after) - set(before))
        pending_source = first_pending_migration_id + '.cs'
        if (pending_source not in additions or not latest_prior < first_pending_migration_id
            or last_applied_migration_id < latest_prior
            or any(name.split('.', 1)[0] <= latest_prior for name in additions)
            or any(not re.fullmatch(r'[0-9]{8,14}_[A-Za-z0-9_]+(?:\.Designer)?\.cs', name)
                   for name in additions)):
            return False
        return True
    except (KeyError, TypeError, ValueError, OSError, RuntimeError, UnicodeError,
            subprocess.SubprocessError):
        return False


def _legacy_completed_bundle_is_separate_from_pending_sql(
        repository, run, job, step, source, attempt, token, *,
        first_pending_migration_id=None, last_applied_migration_id=None,
        current_application_revision=None):
    """Reconcile a COMPLETED historical EF bundle, never reclassify it as no-write.

    Historical validated app bytes, not the release-control SHA, own the old
    migration set. The current *live* read-only EF probe must prove an applied
    prefix through that exact historical migration set, with pending work only
    from new immutable source files. A prior failed/ambiguous release, missing
    successful application receipt, changed migration, or unproven old package
    cannot satisfy this read-only compatibility proof.
    """
    import datetime
    # This retired generation compiled locally and executed EF --no-build. It
    # really mutated SQL; this is completed-history reconciliation, NOT a noop
    # allowlist or permission to replay a missing intent. Pin the audited full
    # workflow rather than trusting a marker or a run ID.
    fingerprint = hashlib.sha256(source.encode()).hexdigest()
    inline_profiles = {
        'e596c919e0aa2f99ede614467d756bf7606f1b343f2fb18d0d1f0c0334181d4e': (
            'e2b2bada659a17265aab97fe91098e69d3399909d6bcce83c112f0a529336b20',
            '66ff9be20166cf9d3fff421bd6f90b277857788cdfb974fe521034322edca727', True, None),
        # September generations compiled locally without any package reuse path.
        '478c67e2c30a5dbac8edb420b13239a12d9979dfdb34cdbb0b7e882adf1dd0bc': (
            '089f4cb690bfe57bd392c76fa05002becea265474423c19ad8bb660bbd1fa5d4',
            'aee39578fced3e70e83e79f76996b0fd7fbb591d60c1aaf1ce9a42724d39cb9b', False,
            'Direct deploy Protect immutable ZIP'),
        '03f186490228dc4a54e6352a1258ba0509c8c952b74744f964a6b2da4fef873c': (
            'c9357daee49cb972e28f6c0db01a6437ede56f64725635d4d881517a17673c58',
            'aee39578fced3e70e83e79f76996b0fd7fbb591d60c1aaf1ce9a42724d39cb9b', False,
            'Direct deploy Protect'),
    }
    inline = fingerprint in inline_profiles
    valid_id = re.compile(r'^[0-9]{8,14}_[A-Za-z0-9_]{1,128}$')
    valid_sha = re.compile(r'^[a-f0-9]{40}$')
    if (run.get('status') != 'completed' or run.get('conclusion') != 'success'
        or run.get('run_attempt') != 1 or attempt != 1
        or job.get('status') != 'completed' or job.get('conclusion') != 'success'
        or step.get('name') != 'Apply additive diagnostics migrations before restarting apps'
        or step.get('status') != 'completed' or step.get('conclusion') != 'success'
        or not (inline or _audited_legacy_migration_noop_source(source))
        or not all(isinstance(value, str) and valid_id.fullmatch(value) for value in
                   (first_pending_migration_id, last_applied_migration_id))
        or not valid_sha.fullmatch(current_application_revision or '')):
        return False
    def exactly(name):
        return sum(x.get('name') == name and x.get('conclusion') == 'success'
                   for x in job.get('steps', [])) == 1
    required = (('Build exact selected release candidate',
                 'Publish exact selected application packages',
                 'Enforce complete direct deployment outcome')
                if inline else ('Verify restored immutable validation package',
                                'Retain exact approved release receipt'))
    if inline and not (exactly('Direct deploy AgentPortal') or
                       (inline_profiles[fingerprint][3] and exactly(inline_profiles[fingerprint][3]))):
        return False
    if not (all(exactly(name) for name in required)
            and exactly('Verify every deployed target and collect all failures')):
        return False
    try:
        if inline:
            _trusted_child_producer(repository, run)
            # Authenticate the original admission semantics, including the
            # reviewed request and exact-head validation reader it imported.
            owners = {
                'scripts/approved-release-baseline.py': inline_profiles[fingerprint][0],
                'scripts/release-lifecycle.py': inline_profiles[fingerprint][1],
                'scripts/release_policy.py': 'cc73125e7147270b19b6c8ed85f6b5ae3f72bf7d28fe576ca0efbea61572e574',
            }
            if inline_profiles[fingerprint][2]:
                owners['scripts/validation-resume.py'] = '99f3c15343a9536f8a6b8edfcab8cbcbf6c9c4202d3d82fe47a17432d0b2dc33'
            for path, digest in owners.items():
                if hashlib.sha256(_release_history_source(repository, run['head_sha'], path, token).encode()).hexdigest() != digest:
                    return False
            inventory = _release_history_api(repository,
                f"actions/runs/{run['id']}/attempts/1/jobs?per_page=100", token)
            jobs = inventory.get('jobs', [])
            admission = [row for row in jobs if row.get('name') == 'discover-live']
            if (inventory.get('total_count') != len(jobs) or len(admission) != 1
                or admission[0].get('status') != 'completed'
                or admission[0].get('conclusion') != 'success'):
                return False
        start = datetime.datetime.fromisoformat(step['started_at'].replace('Z', '+00:00'))
        end = datetime.datetime.fromisoformat(step['completed_at'].replace('Z', '+00:00'))
        if start.tzinfo is None or end.tzinfo is None or end < start:
            return False
        log = _release_job_log(repository, job['id'], token)
        clean = '\n'.join(re.sub(r'^\d{4}-\d{2}-\d{2}T[0-9:.]+Z ', '', line)
                          for line in log.splitlines())
        heads = set(re.findall(r'(?m)^\[command\]/usr/bin/git log -1 --format=%H\n([a-f0-9]{40})$', clean))
        if heads != {run['head_sha']}:
            return False
        marker = ('Schema ready. Applied candidate migrations only; no down migrations.' if inline else
                  'Schema ready. Executed the exact validated migration bundle from the proven live database baseline.')
        if inline:
            # continue-on-error step conclusions and the reuse shortcut do not
            # prove compilation. Require the actual bounded compiler outcome.
            build = next(row for row in job['steps'] if row['name'] == 'Build exact selected release candidate')
            build_start = datetime.datetime.fromisoformat(build['started_at'].replace('Z', '+00:00'))
            build_end = datetime.datetime.fromisoformat(build['completed_at'].replace('Z', '+00:00'))
            build_stamps = []
            for line in log.splitlines():
                date, sep, message = line.partition(' ')
                if sep and message == 'Build succeeded.':
                    build_stamps.append(datetime.datetime.fromisoformat(date.replace('Z', '+00:00')))
            if (len(build_stamps) != 1 or not build_start <= build_stamps[0] <= build_end + datetime.timedelta(seconds=2)
                or build_end > start or '  REUSE_VALIDATED_PACKAGE: true' in clean
                or (inline_profiles[fingerprint][2] and '  REUSE_VALIDATED_PACKAGE: false' not in clean)
                or (not inline_profiles[fingerprint][2] and 'REUSE_VALIDATED_PACKAGE' in source)):
                return False
        stamps = []
        for line in log.splitlines():
            date, sep, message = line.partition(' ')
            if sep and message == marker:
                stamps.append(datetime.datetime.fromisoformat(date.replace('Z', '+00:00')))
        if len(stamps) != 1 or not start <= stamps[0] <= end + datetime.timedelta(seconds=2):
            return False
        prior = _release_attempt_package_revision(repository, run, attempt, job, token, 'portal', aggregate=True)
        if not isinstance(prior, str) or not valid_sha.fullmatch(prior) or prior == current_application_revision:
            return False
        approved_receipt = (('translation-direct-release-' + prior) if inline else
                            ('legend-approved-release-' + prior + '-masterapp-portal'))
        artifacts = api_get(repository, f"actions/runs/{run['id']}/artifacts?per_page=100", token)
        records = artifacts.get('artifacts')
        if (not isinstance(records, list)
            or type(artifacts.get('total_count')) is not int
            or len(records) != artifacts['total_count']
            or len([row for row in records
                    if row.get('name') == approved_receipt and row.get('expired') is False]) != 1):
            return False
        if inline and prior != run['head_sha']:
            return False
        return _applied_migration_prefix_separate(prior, current_application_revision,
                                                 first_pending_migration_id, last_applied_migration_id)
    except (KeyError, TypeError, ValueError, OSError, RuntimeError, UnicodeError,
            subprocess.SubprocessError):
        return False

def _attested_migration_prewrite_failure(repository, run, job, step, attempt, token):
    """Prove ONE historical migration child never reached its first SQL write.

    Reuse the existing trusted release source, complete Actions history and
    bounded job-log authorities. No source SHA, exception text, missing artifact
    or GitHub job conclusion alone is permission to execute a migration.
    """
    if (run.get('status') != 'completed'
        or run.get('conclusion') not in {'failure', 'cancelled'}
        or type(run.get('run_attempt')) is not int
        or attempt < 1 or attempt > run['run_attempt']
        or (job.get('run_attempt') is not None and job['run_attempt'] != attempt)
        or type(job.get('id')) is not int or job['id'] < 1
        or job.get('status') != 'completed' or job.get('conclusion') != 'failure'
        or step.get('name') != DIRECT_RELEASE_CHILDREN['migrations']['step']
        or step.get('status') != 'completed' or step.get('conclusion') != 'failure'):
        return False
    _trusted_child_producer(repository, run)

    # Every accepted source blob was inspected against these historical runs:
    # 37711890752, 37719140124, 37720449533, 37722599537,
    # 37733970018. The migration runner calls before_mutation BEFORE the
    # executable bundle. Its first-write gate has no SQL side effects.
    approved = {
        '.github/workflows/' + DIRECT_RELEASE_WORKFLOW: {
            'bd84c42297a50b29dfa20c2ed926b8233074720e',
        },
        'scripts/release-migration.py': {
            '819fa223f62e6b97fbbdd28092f765b1f57e6f90',
            'd108377faf267915d86c856523a7992a4a6d500f',
            '42efc3425a97f9ba8b35ba2a6dde6e41272032b1',
            '3dc53852fc30df96e9e79779bae89b0cbeb65248',
            '4d04187b13f1212c709237d4632c509e5c9696b9',
        },
        'scripts/release-operation-evidence.py': {
            'ed61e19c3e6f19c433e9fb489c80cd13b7e084b9',
        },
        'scripts/release-prepublication.py': {
            '29bb5e5b5a0c44d4ebc951250a54eb07aee7620b',
            'bd98fb920bfa67eb5e4f7a3ab27f2a46db13e087',
            '2f60d22e05e2917a9c48db0db1ba58632ab57d02',
            '29c23b084d059d5f1663c98631be7557ec86fa67',
            '75b2ca1eaee45852e6f896df5caa5366a709af05',
        },
    }
    sources = {}
    approved_source_blobs = {}
    for path, approved_blobs in approved.items():
        source = _release_history_source(repository, run['head_sha'], path, token)
        data = source.encode('utf-8')
        blob = hashlib.sha1(('blob ' + str(len(data)) + '\0').encode('ascii') + data).hexdigest()
        if blob not in approved_blobs:
            return False
        sources[path] = source
        approved_source_blobs[path] = blob
    # Historical source generations were audited as exact *pairs*.
    # Never combine two individually recognized blobs into an unreviewed runner.
    prepublication_blob = approved_source_blobs['scripts/release-prepublication.py']
    migration_blob = approved_source_blobs['scripts/release-migration.py']
    if (prepublication_blob, migration_blob) not in {
        ('29bb5e5b5a0c44d4ebc951250a54eb07aee7620b',
         '42efc3425a97f9ba8b35ba2a6dde6e41272032b1'),
        ('29bb5e5b5a0c44d4ebc951250a54eb07aee7620b',
         '3dc53852fc30df96e9e79779bae89b0cbeb65248'),
        ('29bb5e5b5a0c44d4ebc951250a54eb07aee7620b',
         '4d04187b13f1212c709237d4632c509e5c9696b9'),
        ('29bb5e5b5a0c44d4ebc951250a54eb07aee7620b',
         'd108377faf267915d86c856523a7992a4a6d500f'),
        ('bd98fb920bfa67eb5e4f7a3ab27f2a46db13e087',
         'd108377faf267915d86c856523a7992a4a6d500f'),
        ('2f60d22e05e2917a9c48db0db1ba58632ab57d02',
         '819fa223f62e6b97fbbdd28092f765b1f57e6f90'),
        ('29c23b084d059d5f1663c98631be7557ec86fa67',
         '819fa223f62e6b97fbbdd28092f765b1f57e6f90'),
        ('75b2ca1eaee45852e6f896df5caa5366a709af05',
         '819fa223f62e6b97fbbdd28092f765b1f57e6f90'),
    }:
        return False
    migration = sources['scripts/release-migration.py']
    if migration_blob == '42efc3425a97f9ba8b35ba2a6dde6e41272032b1':
        before_write = 'journal.before_mutation(observation)'
        execution = 'subprocess.run([str(bundle)'
    else:
        before_write = "migration_stage('mutation-admission', journal.before_mutation, observation)"
        execution = "migration_stage('bundle-execution',"
    if (before_write not in migration or execution not in migration
        or migration.index(before_write) >= migration.index(execution)):
        return False

    # Artifacts are authoritative only if the inventory is COMPLETE, bound
    # to this exact run, unexpired and contains NO child/write intent/success.
    run_id = run['id']
    payload = api_get(repository, f'actions/runs/{run_id}/artifacts?per_page=100', token)
    artifacts = payload.get('artifacts')
    if (not isinstance(artifacts, list) or type(payload.get('total_count')) is not int
        or payload['total_count'] != len(artifacts) or len(artifacts) > 100
        or any(not isinstance(a, dict) or a.get('expired') is not False
               or (a.get('workflow_run') or {}).get('id') != run_id
               or not isinstance(a.get('name'), str) for a in artifacts)):
        return False
    names = [a['name'] for a in artifacts]
    if len(names) != len(set(names)):
        return False
    expected_rollback = {
        f"diagnostics-rollback-{key}-{run['head_sha']}"
        for key in ('portal', 'client', 'protect', 'parfait', 'website')
    }
    retained_here = set(names) & expected_rollback
    if retained_here != expected_rollback:
        # Multiple content-attested historical generations reused retained
        # rollback packages rather than uploading ZIPs to their own run.
        # The SAME canonical first-write witness already verifies the original
        # immutable workflow+writer pair, entire Actions artifact inventory,
        # first-write journal ordering and exact pre-SQL failure marker.
        # Never infer nonentry just from absence: the release job must
        # positively prove preparation+read-only finalizer success, failure
        # at prepublication, and explicit nonentry of all write-capable stages.
        if retained_here:
            return False  # partial locally uploaded rollback inventory
        steps = job.get('steps')
        if not isinstance(steps, list):
            return False
        def exactly(name, conclusion):
            return sum(
                item.get('name') == name and item.get('status') == 'completed'
                and item.get('conclusion') == conclusion for item in steps
            ) == 1
        if not all((
            exactly('Prepare complete immutable release transaction', 'success'),
            exactly('Synchronize canonical pre-publication resource lanes', 'failure'),
            exactly('Reconcile complete immutable release transaction', 'success'),
            exactly('Submit canonical selected targets in parallel', 'skipped'),
            exactly('Run independent auxiliary release fanout', 'skipped'),
            exactly('Audit centralized Cloudflare routing authority', 'skipped'),
            exactly('Deploy and activate LEGEND Founder Cloudflare baseline', 'skipped'),
            exactly('Reconcile public custom-hostname Cloudflare policy', 'skipped'),
            exactly('Deploy shared Cloudflare business website router', 'skipped'),
            exactly('Verify every deployed target and collect all failures', 'skipped'),
            *(exactly(f'Confirm first-pass durable publication receipt ({app})',
                      'skipped') for app in ('portal', 'client', 'protect', 'parfait', 'website')),
            exactly('Retain exact approved release receipt', 'skipped'),
            exactly('Reconcile terminal release resource disposition', 'skipped'),
            exactly('Preserve terminal release resource disposition', 'skipped'),
        )):
            return False
    other = set(names) - retained_here
    admission = [n for n in other if re.fullmatch(r'legend-release-admission-[a-f0-9]{64}', n)]
    state = [n for n in other if re.fullmatch(
        rf'legend-release-step-state-[a-f0-9]{{40}}-{run_id}-[1-9][0-9]*', n)]
    plan = [n for n in other if re.fullmatch(r'legend-release-transaction-plan-[a-f0-9]{64}', n)]
    if (len(admission) != 1 or len(state) < 1 or len(plan) > 1
        or sum(n.endswith(f'-{run_id}-{attempt}') for n in state) != 1
        or len(other) != len(admission) + len(state) + len(plan)):
        return False

    # The prepublication process emits only a fixed redacted failure label.
    # The exact, single runtime log marker must match the bound executable
    # generation. A failed step without this positive evidence remains blocked.
    raw = _release_job_log(repository, job['id'], token)
    # Old wrappers predate the fixed LEGEND prefix. Their exact sanitized
    # labels remain safe only with the paired immutable writer generations,
    # never as a general keyword heuristic.
    if prepublication_blob == '29bb5e5b5a0c44d4ebc951250a54eb07aee7620b':
        marker = {
            '42efc3425a97f9ba8b35ba2a6dde6e41272032b1':
                'Migration child unresolved; preserve prior evidence and reconcile without replay.',
            '3dc53852fc30df96e9e79779bae89b0cbeb65248':
                'Migration stage unresolved: schema-observation; preserve prior evidence and reconcile without replay.',
            '4d04187b13f1212c709237d4632c509e5c9696b9':
                'Schema probe detected migration schema drift; preserve prior evidence and reconcile without replay.',
            'd108377faf267915d86c856523a7992a4a6d500f':
                'Database contains applied migration history absent from validated bundle; preserve prior evidence and reconcile without replay.',
        }[migration_blob]
    else:
        marker = {
            'LEGEND_PREPUBLICATION_MIGRATION:Database contains applied migration history absent from validated bundle',
            'LEGEND_PREPUBLICATION_MIGRATION:Migration stage unresolved: mutation-admission',
        }
    emitted = [line.partition('Z ')[2] for line in raw.splitlines()
               if re.fullmatch(r'20[0-9]{2}-[0-9]{2}-[0-9]{2}T[0-9:.]+Z .+', line)]
    accepted = {marker} if isinstance(marker, str) else marker
    return sum(line in accepted for line in emitted) == 1



def _attested_legacy_ef_factory_nonentry(repository, run, job, step, attempt, token):
    """Attest one historical design-time failure; never assume failed EF means no SQL."""
    import datetime
    head = 'ea53cdbcf7e650cceb9e193965b630dd1da19c16'
    run_id, job_id = 37129696534, 111222377709
    def deny(reason):
        # Evidence diagnostics only. This cannot authorize a database write,
        # bypass the shared journal, or expose a credential-bearing exception.
        if run.get('id') == run_id and os.environ.get('GITHUB_ACTIONS') == 'true':
            print('LEGEND_MIGRATION_LEGACY_EF_PROOF:' + reason, flush=True)
        return False
    if (run.get('id') != run_id or run.get('head_sha') != head
        or run.get('status') != 'completed' or run.get('conclusion') != 'failure'
        or run.get('run_attempt') != 1 or attempt != 1
        or job.get('id') != job_id or job.get('status') != 'completed'
        or job.get('conclusion') != 'failure'
        or (job.get('run_attempt') is not None and job['run_attempt'] != 1)
        or step.get('name') != 'Apply additive diagnostics migrations before restarting apps'
        or step.get('status') != 'completed' or step.get('conclusion') != 'failure'):
        return deny('IDENTITY_OR_STEP_MISMATCH')
    try:
        _trusted_child_producer(repository, run)
        source = _release_history_source(repository, head,
            '.github/workflows/' + DIRECT_RELEASE_WORKFLOW, token)
        raw = source.encode('utf-8')
        blob = hashlib.sha1(b'blob ' + str(len(raw)).encode() + b'\0' + raw).hexdigest()
        # The complete October 3 runner is pinned, not merely the error text.
        if blob != 'b85c50eadf83daad32b787f2d37f7a32babe67a4':
            return deny('IMMUTABLE_SOURCE_MISMATCH')
        receipt = api_get(repository, f'actions/runs/{run_id}/artifacts?per_page=100', token)
        rows = receipt.get('artifacts')
        if (type(receipt.get('total_count')) is not int or receipt['total_count'] != 1
            or not isinstance(rows, list) or len(rows) != 1
            or rows[0].get('name') !=
               'legend-release-step-state-b24ef1ed02ca7a9fb6d0a8c5c423508860b5868a-37129696534-1'
            or rows[0].get('expired') is not False
            or (rows[0].get('workflow_run') or {}).get('id') != run_id):
            return deny('TERMINAL_RECEIPT_INCOMPLETE')
        started = datetime.datetime.fromisoformat(step['started_at'].replace('Z', '+00:00'))
        ended = datetime.datetime.fromisoformat(step['completed_at'].replace('Z', '+00:00'))
        if started.tzinfo is None or ended.tzinfo is None or ended < started:
            return deny('STEP_TIMESTAMPS_UNPROVEN')
        log = _release_job_log(repository, job_id, token)
        clean = '\n'.join(re.sub(r'^\d{4}-\d{2}-\d{2}T[0-9:.]+Z ', '', l)
                          for l in log.splitlines())
        checked_out = re.findall(
            r'(?m)^\[command\]/usr/bin/git log -1 --format=%H\n([a-f0-9]{40})$',
            clean)
        if checked_out != [head]:
            return deny('ORIGINAL_CHECKOUT_UNPROVEN')
        prefix = ("Unable to create a 'DbContext' of type "
                  "'Infrastructure.Data.MasterAppDbContext'. "
                  "The exception 'Missing MasterAppDb connection string "
                  "for EF design-time factory.")
        count = 0
        for line in log.splitlines():
            stamp, sep, msg = line.partition('Z ')
            if sep and msg.startswith(prefix):
                observed = datetime.datetime.fromisoformat(stamp + '+00:00')
                if (observed.tzinfo is None or observed < started
                    or observed > ended + datetime.timedelta(seconds=2)
                    or 'was thrown while attempting to create an instance.' not in msg):
                    return deny('FACTORY_ERROR_TIMESTAMP_OR_MESSAGE_MISMATCH')
                count += 1
        # DbContext construction fails before EF can issue migration SQL.
        if count != 1:
            return deny('FACTORY_ERROR_NOT_UNIQUE')
        # Actions logs echo the source of each `run:` step. The October 3
        # job includes an unexecuted ANSI-colored Python print(...) statement
        # containing this phrase. Treat ONLY an actual, whole timestamped
        # output line as success, never source code or command arguments.
        # Any genuine success marker anywhere in this authenticated job still
        # invalidates historical non-entry; SQL replays remain fail-closed.
        success_marker = ('Schema ready. Executed the exact validated migration bundle '
                          'from the proven live database baseline.')
        for line in log.splitlines():
            stamp, separator, message = line.partition('Z ')
            if separator and message == success_marker:
                try:
                    observed = datetime.datetime.fromisoformat(stamp + '+00:00')
                except ValueError:
                    return deny('MALFORMED_SUCCESS_OUTPUT_TIMESTAMP')
                if observed.tzinfo is None:
                    return deny('MALFORMED_SUCCESS_OUTPUT_TIMESTAMP')
                return deny('SUCCESS_MARKER_CONTRADICTS_FAILURE')
        return True
    except (KeyError, TypeError, ValueError, AttributeError, RuntimeError,
            OSError, UnicodeError, subprocess.SubprocessError) as exc:
        # Only a bounded exception CLASS; never output provider details.
        return deny('EVIDENCE_READ_OR_PARSE_' + type(exc).__name__)


def _attested_migration_noop_success(repository, run, job, step, attempt, token):
    """Prove a historical successful prepublication step skipped the SQL lane.

    Only the audited immutable wrapper that returned before calling the EF
    migration runner qualifies. The actual completed step must emit its one
    bounded 'not-applicable' summary. A success conclusion alone is no proof.
    """
    import datetime

    if (run.get('status') != 'completed'
        or run.get('conclusion') not in {'success', 'failure', 'cancelled'}
        or type(run.get('run_attempt')) is not int or attempt < 1
        or attempt > run['run_attempt']
        or type(job.get('id')) is not int or job['id'] < 1
        or job.get('status') != 'completed'
        or job.get('conclusion') not in {'success', 'failure'}
        or step.get('name') != DIRECT_RELEASE_CHILDREN['migrations']['step']
        or step.get('status') != 'completed' or step.get('conclusion') != 'success'):
        return False
    _trusted_child_producer(repository, run)

    # Bind both members of the exact audited historical wrapper pair.
    # 37527468541 predates five-rollback preservation but already exits the
    # migration lane before invoking any EF bundle on this fixed result.
    blobs = {}
    for path in ('.github/workflows/' + DIRECT_RELEASE_WORKFLOW,
                 'scripts/release-prepublication.py'):
        source = _release_history_source(repository, run['head_sha'], path, token)
        payload = source.encode('utf-8')
        blobs[path] = hashlib.sha1(
            ('blob ' + str(len(payload)) + '\0').encode('ascii') + payload
        ).hexdigest()
    pairing = (blobs['.github/workflows/' + DIRECT_RELEASE_WORKFLOW],
               blobs['scripts/release-prepublication.py'])
    audited_pairs = {
        ('433ce0dcecbfc9ad8129cb108e44e773f96766bc',
         '29bb5e5b5a0c44d4ebc951250a54eb07aee7620b'),
        ('90f16d86254eb0b7433d9500d63bd0b3aaaae4ee',
         '29bb5e5b5a0c44d4ebc951250a54eb07aee7620b'),
        ('4a69290e3462d6fae20dcd75d48b8639c5066c22',
         '4357329104400dc36a59a739d8a823b548838baf'),
        # Current released prepublication returns not-applicable ONLY after
        # upstream fresh physical SQL readiness proves zero EF pending.
        ('8696295389f2b7f6acb2a0b18955580039dd397f',
         '03f3c3508394016b4f2e9f041290355f1616b0e6'),
    }
    if pairing not in audited_pairs:
        return False

    run_id = run['id']
    receipt = api_get(repository, f'actions/runs/{run_id}/artifacts?per_page=100', token)
    artifacts = receipt.get('artifacts')
    if (not isinstance(artifacts, list)
        or type(receipt.get('total_count')) is not int
        or receipt['total_count'] != len(artifacts)
        or len(artifacts) > 100
        or any(not isinstance(a, dict) or a.get('expired') is not False
               or (a.get('workflow_run') or {}).get('id') != run_id
               or not isinstance(a.get('name'), str) for a in artifacts)):
        return False
    names = [a['name'] for a in artifacts]
    if len(names) != len(set(names)):
        return False
    # A retained migration intent can conceal a real write, so even an
    # otherwise valid no-op result cannot discharge the earlier operation.
    if any(name.startswith('legend-release-child-intent-') for name in names):
        return False
    # Rollback artifacts belong to application publication, not SQL.
    # A protected portal-only or early release can have none. The original
    # completed migration step, immutable source and absence of a child
    # write intent are the mandatory no-mutation proof.
    if sum(bool(re.fullmatch(r'legend-release-admission-[a-f0-9]{64}', n))
           for n in names) != 1:
        return False
    state = rf'legend-release-step-state-[a-f0-9]{{40}}-{run_id}-{attempt}'
    if sum(bool(re.fullmatch(state, n)) for n in names) != 1:
        return False

    try:
        start = datetime.datetime.fromisoformat(
            step['started_at'].replace('Z', '+00:00'))
        end = datetime.datetime.fromisoformat(
            step['completed_at'].replace('Z', '+00:00'))
        if start.tzinfo is None or end.tzinfo is None or end < start:
            return False
    except (KeyError, ValueError, AttributeError, TypeError):
        return False
    raw = _release_job_log(repository, job['id'], token)
    matches = 0
    for line in raw.splitlines():
        stamp, sep, value = line.partition('Z ')
        if not sep or len(value) > 1024 or '"migrationStatus": "not-applicable"' not in value:
            continue
        try:
            at = datetime.datetime.fromisoformat(stamp + '+00:00')
            summary = json.loads(value)
        except (ValueError, TypeError, json.JSONDecodeError):
            return False
        if (not start <= at <= end + datetime.timedelta(seconds=1)
            or not isinstance(summary, dict)
            or set(summary) != {'changedTargets', 'configuredTargets', 'migrationStatus'}
            or summary['migrationStatus'] != 'not-applicable'
            or summary['changedTargets'] != []
            or not isinstance(summary['configuredTargets'], list)
            or any(not isinstance(target, str) for target in summary['configuredTargets'])):
            return False
        matches += 1
    return matches == 1


def _historical_migration_prewrite_proven(repository, run, job, step, attempt, token):
    """Positive, content-attested proof that a *previous* migration never wrote.

    This does not authorize current SQL. The caller's canonical child journal
    still must inspect ALL other attempts and publish a durable new intent
    before executing the bundle. A failed job or missing artifact alone is
    NEVER proof of absence.
    """
    if (run.get("status") != "completed"
        or run.get("conclusion") not in {"failure", "cancelled"}
        or job.get("status") != "completed" or job.get("conclusion") != "failure"
        or step.get("name") != "Synchronize canonical pre-publication resource lanes"
        or step.get("status") != "completed" or step.get("conclusion") != "failure"
        or type(run.get("id")) is not int or run["id"] < 1
        or type(run.get("run_attempt")) is not int or not 1 <= run["run_attempt"] <= 20
        or type(attempt) is not int or not 1 <= attempt <= run["run_attempt"]
        or (job.get("run_attempt") is not None and job["run_attempt"] != attempt)
        or not re.fullmatch(
            r"LEGEND release pr=[0-9]+ candidate=[a-f0-9]{40} authority="
            + re.escape(run.get("head_sha", "")),
            run.get("display_title", ""))):
        return False

    # Only audited, immutable writer generations where these terminal messages
    # are raised BEFORE journal intent publication / migration bundle execution.
    # Unknown future code or changed release jobs remain blocked.
    owners = {
        ".github/workflows/" + DIRECT_RELEASE_WORKFLOW: {
            "bd84c42297a50b29dfa20c2ed926b8233074720e",
            # Read-only first gate runs before release; SQL journal remains
            # the canonical write owner and prepublication must precede publish.
            "8696295389f2b7f6acb2a0b18955580039dd397f",
        },
        "scripts/release-prepublication.py": {
            "2f60d22e05e2917a9c48db0db1ba58632ab57d02",
            "29c23b084d059d5f1663c98631be7557ec86fa67",
            "75b2ca1eaee45852e6f896df5caa5366a709af05",
            # Reviewed finite-code prepublication diagnostic generation.
            "87fa8505d8df0b67d6c7d81e9edb452bbf6b1e1c",
            # Same no-intent prewrite marker; source-diff shortcut now also
            # requires fresh upstream SQL readiness from this release run.
            "03f3c3508394016b4f2e9f041290355f1616b0e6",
        },
        "scripts/release-migration.py": {
            "819fa223f62e6b97fbbdd28092f765b1f57e6f90",
            # Same no-replay journal semantics; emits reason codes before failure.
            "4c4bff74892a9924efb45f3968e06a61dffbcab5",
            # Content-reviewed first-pending/last-applied read-only fence;
            # still authorizes NO write before ChildJournal intent readback.
            "f93021971cf0b0362c13ddd3b1f4a6b2cab85ba5",
            # Read-only --preflight invokes first-write history without
            # publishing intent or running a bundle; write path unchanged.
            "6837d13002784fa4894b09e95d7f6bc5d9f5decf",
        },
    }
    try:
        _trusted_child_producer(repository, run)
        for path, expected in owners.items():
            source = _release_history_source(repository, run["head_sha"], path, token)
            raw = source.encode("utf-8")
            observed = hashlib.sha1(b"blob " + str(len(raw)).encode() + b"\0" + raw).hexdigest()
            if observed not in expected:
                return False

        # Authenticate exact release topology and forbid all publication or
        # auxiliary owner entry, not merely a failed migration step label.
        steps = job.get("steps")
        if not isinstance(steps, list):
            return False
        def exactly(name, result):
            matches = [item for item in steps if item.get("name") == name]
            return len(matches) == 1 and matches[0].get("conclusion") == result
        if not (exactly("Submit canonical selected targets in parallel", "skipped")
                and exactly("Run independent auxiliary release fanout", "skipped")):
            return False

        artifacts = api_get(repository,
            f"actions/runs/{run['id']}/artifacts?per_page=100", token)
        rows = artifacts.get("artifacts")
        if (not isinstance(rows, list)
            or type(artifacts.get("total_count")) is not int
            or len(rows) != artifacts["total_count"]
            or any(not isinstance(item, dict) or item.get("expired") is not False
                   or not isinstance(item.get("name"), str)
                   or item.get("workflow_run", {}).get("id") != run["id"]
                   for item in rows)):
            return False
        names = [item["name"] for item in rows]
        if (len(names) != len(set(names))
            or any(name.startswith(("legend-release-child-intent-",
                                    "legend-release-child-success-",
                                    "legend-release-operation-intent-",
                                    "legend-release-operation-success-"))
                   for name in names)):
            return False
        # The terminal GitHub step receipt proves the full failed attempt was
        # observed. An expired/missing or mismatched receipt is not no-write.
        suffix = f"-{run['id']}-{attempt}"
        if len([name for name in names
                if name.startswith("legend-release-step-state-")
                and name.endswith(suffix)]) != 1:
            return False

        import datetime
        begin = datetime.datetime.fromisoformat(
            step["started_at"].replace("Z", "+00:00"))
        end = datetime.datetime.fromisoformat(
            step["completed_at"].replace("Z", "+00:00"))
        if begin.tzinfo is None or end.tzinfo is None or end < begin:
            return False
        # Both markers originate from the exact immutable migration owner:
        # unknown applied history is an observation-only failure; a failed
        # mutation-admission happens before ChildJournal publishes an intent
        # and before the migration bundle is executed.
        allowed = {
            "LEGEND_PREPUBLICATION_MIGRATION:"
            "Database contains applied migration history absent from validated bundle",
            "LEGEND_PREPUBLICATION_MIGRATION:"
            "Migration stage unresolved: mutation-admission",
        }
        matches = []
        for line in _release_job_log(repository, job["id"], token).splitlines():
            stamp, sep, message = line.partition(" ")
            if sep and message in allowed:
                when = datetime.datetime.fromisoformat(
                    stamp.replace("Z", "+00:00"))
                if (when.tzinfo is None or when < begin
                    or when > end + datetime.timedelta(seconds=2)):
                    return False
                matches.append(message)
        return len(matches) == 1
    except (KeyError, TypeError, ValueError, AttributeError, RuntimeError,
            OSError, UnicodeError, subprocess.SubprocessError):
        return False


# Retired release evidence is interpreted only by this canonical first-write gate.
# An entered EF migration is NEVER reclassified as a no-write operation.
_RETIRED_PRE_BUNDLE = {
    '11b3755b66185451547b67b2ae0969504d0df8a6': 'baseline',
    '4b03bbd5b14e09704cb207b46b721d630a984316': 'pending-model',
}
_RETIRED_ENTERED_BUNDLE = {
    'c0690fce1df3405ff72f45b4ca8f124580fb0741': (
        '20261001070000_AddLegendEngineeringControlPlane',),
    '73e74c25982f19e35a6e71f0c8c1e23c17dc6c06': (
        '20260927080618_SyncCanonicalMarketingModel20260927',
        '20260927090000_AddOpenAiProductFeedProjections'),
}


def _attested_retired_legacy_failure(repository, run, job, step, source, attempt,
                                     token, *, first_pending_migration_id=None,
                                     last_applied_migration_id=None,
                                     current_application_revision=None):
    """Attest original pre-bundle nonentry OR a now-applied older EF generation.

    Existing read-only SQL prefix/physical-schema validation is mandatory for
    the latter. This never suppresses the current migration's journal/first write.
    """
    import datetime
    raw_bytes = source.encode('utf-8')
    fingerprint = hashlib.sha1(b'blob ' + str(len(raw_bytes)).encode()
                               + b'\0' + raw_bytes).hexdigest()
    guard = _RETIRED_PRE_BUNDLE.get(fingerprint)
    attempted = _RETIRED_ENTERED_BUNDLE.get(fingerprint)
    if not guard and not attempted:
        return False
    if (run.get('status') != 'completed' or run.get('conclusion') != 'failure'
        or run.get('run_attempt') != 1 or attempt != 1
        or job.get('status') != 'completed' or job.get('conclusion') != 'failure'
        or job.get('run_attempt', 1) != 1 or type(job.get('id')) is not int
        or step.get('name') != 'Apply additive diagnostics migrations before restarting apps'
        or step.get('status') != 'completed' or step.get('conclusion') != 'failure'):
        return False
    block = named_step_blocks(_job_blocks(source).get('release', '')).get(
        'Apply additive diagnostics migrations before restarting apps', '')
    if ("set -euo pipefail" not in block or
        "python3 - <<'PYMIGRATE'" not in block or
        (guard == 'baseline' and
         'No candidate migration source changed from the proven database baseline.' not in block)):
        return False
    try:
        _trusted_child_producer(repository, run)
        start = datetime.datetime.fromisoformat(
            step['started_at'].replace('Z', '+00:00'))
        end = datetime.datetime.fromisoformat(
            step['completed_at'].replace('Z', '+00:00'))
        if start.tzinfo is None or end.tzinfo is None or end < start:
            return False
        raw = _release_job_log(repository, job['id'], token)
        clean = '\n'.join(
            re.sub(r'^\d{4}-\d\d-\d\dT[0-9:.]+Z ', '', line)
            for line in raw.splitlines())
        checkout = re.findall(
            r'(?m)^\[command\]/usr/bin/git log -1 --format=%H\n([a-f0-9]{40})$',
            clean)
        # The protected workflow event head can be newer than the approved
        # source selected by the release. Positively verify the original
        # selected SHA and exact checkout, then their protected ancestry.
        selected = set(re.findall(
            r'(?m)^  RELEASE_SHA: ([a-f0-9]{40})$', clean))
        if (len(checkout) != 1 or len(selected) != 1
            or checkout[0] != next(iter(selected))
            or 'Post job cleanup.' not in clean):
            return False
        lineage = subprocess.run(
            ['git', 'merge-base', '--is-ancestor', checkout[0], run['head_sha']],
            capture_output=True, text=True, check=False)
        if lineage.returncode != 0:
            return False
        observed = []
        for line in raw.splitlines():
            stamp, sep, value = line.partition('Z ')
            if not sep or not re.fullmatch(r'\d{4}-\d\d-\d\dT\d\d:\d\d:[0-9.]+',
                                           stamp):
                continue
            # GitHub prints the workflow source in ANSI-colored command echos.
            # They are NOT authenticated execution output and cannot prove or
            # disprove a migration; only the producer's actual output may do so.
            if '\x1b[' in value:
                continue
            when = datetime.datetime.fromisoformat(stamp + '+00:00')
            if start <= when <= end + datetime.timedelta(seconds=2):
                observed.append(value)
        if '##[error]Process completed with exit code 1.' not in observed:
            return False
        inventory = api_get(
            repository, f"actions/runs/{run['id']}/artifacts?per_page=100", token)
        artifacts = inventory.get('artifacts')
        if (not isinstance(artifacts, list)
            or type(inventory.get('total_count')) is not int
            or inventory['total_count'] != len(artifacts)
            or len(artifacts) > 100
            or any(not isinstance(a, dict) or a.get('expired') is not False
                   or (a.get('workflow_run') or {}).get('id') != run['id']
                   or not isinstance(a.get('name'), str) for a in artifacts)
            or any(a['name'].startswith(('legend-release-child-intent-',
                                          'legend-release-child-success-'))
                   for a in artifacts)):
            return False
        entered = re.findall(
            r"^Applying migration '([0-9]{8,14}_[A-Za-z0-9_]{1,128})'\.$",
            '\n'.join(observed), re.MULTILINE)
        if guard:
            if entered:
                return False
            if guard == 'baseline':
                # Re-run the original strict Git ancestry gate with its
                # immutable logged environment; lack of SQL logs alone does
                # not prove an unexecuted migration.
                baselines = set(re.findall(
                    r'(?m)^  EXPECTED_DB_BASE_SHA: ([a-f0-9]{40})$', clean))
                candidates = set(re.findall(
                    r'(?m)^  APPLICATION_RELEASE_SHA: ([a-f0-9]{40})$', clean))
                gate_position = block.find('git merge-base --is-ancestor')
                bundle_position = block.find("python3 - <<'PYMIGRATE'")
                if (len(baselines) != 1 or len(candidates) != 1
                    or not 0 <= gate_position < bundle_position):
                    return False
                ancestry = subprocess.run(
                    ['git', 'merge-base', '--is-ancestor',
                     next(iter(baselines)), next(iter(candidates))],
                    capture_output=True, text=True, check=False)
                if ancestry.returncode != 1:
                    return False
                return (not any(x.startswith('Database baseline release receipt verified:')
                                for x in observed)
                        and not any(x.startswith('Infrastructure/Migrations/')
                                    for x in observed)
                        and not any(x == 'No candidate migration source changed from the proven database baseline.'
                                    for x in observed)
                        and not any(x == ('Schema ready. Executed the exact validated migration '
                                          'bundle from the proven live database baseline.')
                                    for x in observed))
            return (any('PendingModelChangesWarning' in x for x in observed)
                    and any('System.InvalidOperationException:' in x
                            for x in observed)
                    and not any('Acquiring an exclusive lock for migration'
                                in x for x in observed))
        # The original EF bundle DID enter. Authenticate exact original attempted
        # migration identities, and resolve against the CURRENT observed live
        # EF prefix, not against absent old journal artifacts.
        if tuple(entered) != attempted:
            return False
        if (not re.fullmatch(r'[0-9]{8,14}_[A-Za-z0-9_]{1,128}',
                            first_pending_migration_id or '')
            or not re.fullmatch(r'[0-9]{8,14}_[A-Za-z0-9_]{1,128}',
                                last_applied_migration_id or '')
            or not re.fullmatch(r'[a-f0-9]{40}', current_application_revision or '')
            or any(x > last_applied_migration_id or x >= first_pending_migration_id
                   for x in attempted)):
            return False
        for identifier in attempted:
            migration = git_show_file(
                current_application_revision,
                'Infrastructure/Migrations/' + identifier + '.cs')
            if not migration.strip():
                return False
        if fingerprint == 'c0690fce1df3405ff72f45b4ca8f124580fb0741':
            return any('There is no entity type mapped to the table' in x
                       for x in observed)
        return any('may cause cycles or multiple cascade paths' in x
                   for x in observed)
    except (KeyError, TypeError, ValueError, AttributeError, RuntimeError,
            OSError, UnicodeError, subprocess.SubprocessError):
        return False


def _attested_retired_unscheduled_release(repository, run, job, jobs, attempt, token):
    """Positive scheduler nonentry; missing logs/receipts ALONE are not proof."""
    if (run.get('status') != 'completed' or run.get('conclusion') != 'cancelled'
        or run.get('run_attempt') != 1 or attempt != 1
        or job.get('name') != 'release' or job.get('status') != 'completed'
        or job.get('conclusion') != 'cancelled'
        or job.get('runner_id') is not None or job.get('runner_name') is not None
        or job.get('steps') != [] or job.get('run_attempt', 1) != 1
        or sum(x.get('name') == 'release' for x in jobs) != 1
        or any(x.get('name') == 'migration-readiness' for x in jobs)):
        return False
    try:
        _trusted_child_producer(repository, run)
        original = _release_history_source(
            repository, run['head_sha'],
            '.github/workflows/' + DIRECT_RELEASE_WORKFLOW, token)
        if not named_step_blocks(_job_blocks(original).get('release', '')).get(
                'Apply additive diagnostics migrations before restarting apps'):
            return False
        inv = api_get(
            repository, f"actions/runs/{run['id']}/artifacts?per_page=100",
            token)
        return inv.get('total_count') == 0 and inv.get('artifacts') == []
    except (KeyError, TypeError, ValueError, AttributeError, RuntimeError,
            OSError, UnicodeError, subprocess.SubprocessError):
        return False


def legacy_router_step_never_entered(source, step):
    """Recognize the original publisher, never a later auxiliary-result observer."""
    legacy = 'Deploy shared Cloudflare business website router'
    if (step.get('name') != legacy or step.get('status') != 'completed'
        or step.get('conclusion') != 'skipped'):
        return False
    block = named_step_blocks(_job_blocks(source).get('release', '')).get(legacy, '')
    # Reviewed standalone Wrangler publishers. The later same-named steps that
    # only read auxiliary JSON are deliberately excluded: skipping an observer
    # cannot prove that the auxiliary publisher did not run.
    return hashlib.sha256(block.encode()).hexdigest() in {
        'c2f213e46c4a56342cc651ce97bfbd743ce874c19a1ef313d625dd8ab1ece92c',
        '75ecc7720752358ff63c26c0a90db8654974fe03c1a7091eb7883ad43c0f0c4f',
        '954daf22c88fe327f3aac39e37cc29e8556121190bdef67afe4bda7b8b59b124',
        'f7fe0bba7b379e1396d455271b862e2fce11f45c3d8e95fce5955e929afc074a',
        '57c37e55e5afb70d4fc310f46d1a0605b59e62dd72cff374f7f4b957ee43a852',
        'bf78ad2ad5a5faf0bcf0994c46eb7e3cb8e222faf116134e04dab13c54e4595f',
        '4db28ad9eea20bb9421936bf09e3788990650b41f8703312c4d2c4f4fa7ded19',
        '965580c60c524ac8c333185e61f92d1257b66494a4a607f95f13faf573be8876',
        '70877b6cd18ed3a4e0ea5b4d34e6add9ef71f3aa937b7e35704b6a4e57eb418d'}


def legacy_router_completed_generation(repository, run, job, step, source, token,
                                       dependency_identity, material_identity):
    """Prove an older completed publication, never relabel it as nonentry."""
    import datetime
    if (run.get('status') != 'completed' or job.get('status') != 'completed'
        or step.get('name') != 'Deploy shared Cloudflare business website router'
        or step.get('status') != 'completed' or step.get('conclusion') != 'success'
        or hashlib.sha256(source.encode()).hexdigest() != '2a9363f4b800d06d7af65f6e512d910b6a06fa667414d46f91f8378d01d9ac53'):
        return False
    try:
        # New verifier revisions alone must never authorize the same operation.
        if direct_child_operation_identity('routing-cloudflare', run['head_sha'], material_identity) == dependency_identity:
            return False
        start = datetime.datetime.fromisoformat(step['started_at'].replace('Z', '+00:00'))
        end = datetime.datetime.fromisoformat(step['completed_at'].replace('Z', '+00:00'))
        if start.tzinfo is None or end.tzinfo is None or end < start:
            return False
        log = _release_job_log(repository, job['id'], token)
        clean = '\n'.join(re.sub(r'^\d{4}-\d{2}-\d{2}T[0-9:.]+Z ', '', line) for line in log.splitlines())
        heads = set(re.findall(r'(?m)^\[command\]/usr/bin/git log -1 --format=%H\n([a-f0-9]{40})$', clean))
        if heads != {run['head_sha']}:
            return False
        bounded = []
        for line in log.splitlines():
            date, sep, message = line.partition(' ')
            if sep and re.fullmatch(r'\d{4}-\d{2}-\d{2}T[0-9:.]+Z', date):
                stamp = datetime.datetime.fromisoformat(date.replace('Z', '+00:00'))
                if start <= stamp <= end + datetime.timedelta(seconds=2):
                    bounded.append(message)
        text = '\n'.join(bounded)
        marker = 'Cloudflare website router deployed and LEGEND-owned host pass-through verified.'
        return (bounded.count(marker) == 1
            and len(re.findall(r'(?m)^Uploaded legend-business-website-router \([0-9.]+ sec\)$', text)) == 1
            and len(re.findall(r'(?m)^Deployed legend-business-website-router triggers \([0-9.]+ sec\)$', text)) == 1
            and len(re.findall(r'(?m)^Current Version ID: [a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$', text)) == 1
            and all(re.search(r'(?m)^' + re.escape(host) + r' [1-6] [23][0-9]{2} [0-9]+$', text)
                    for host in ROUTING_PASS_THROUGH_HOSTS)
            and not any(line == 'Restored previous Cloudflare Worker version.'
                        or line.startswith('::warning::Previous Worker version was unavailable; removed the LEGEND Worker route')
                        for line in bounded))
    except (KeyError, TypeError, ValueError, RuntimeError, OSError, UnicodeError, subprocess.SubprocessError):
        return False


def legacy_migration_step_recognized(source, step):
    """Bind a historical step to original source before interpreting its outcome.

    Shared by admission and its read-only audit. Recognition is not execution
    proof: entered steps still require their existing journal/no-write checks.
    """
    legacy = 'Apply additive diagnostics migrations before restarting apps'
    if step.get('name') != legacy:
        return False
    block = named_step_blocks(_job_blocks(source).get('release', '')).get(legacy, '')
    raw = source.encode('utf-8')
    blob = hashlib.sha1(b'blob ' + str(len(raw)).encode() + b'\0' + raw).hexdigest()
    # Original approved 5bc6d539 workflow: bundle preparation failed and GitHub
    # positively reports the SQL step as completed/skipped. This fingerprint
    # grants recognition ONLY for nonentry, never for an executed bundle.
    if (blob in {'f334180a66c08f94e38861ec0877c9580be505bb',
                 'e2d20bcfc6943d4af117553ab565907b2a6e69ac',
                 'e429a7bde047a9bd88c1a80079c9a2555f19cee8'}
            and step.get('status') == 'completed'
            and step.get('conclusion') == 'skipped'):
        return True
    if (blob in AUDITED_LEGACY_CLI_NOOP_SOURCES
            and step.get('status') == 'completed'
            and step.get('conclusion') in {'skipped', 'success'}):
        return True
    return (hashlib.sha256(block.encode()).hexdigest()
            == '74500e6966d2c198564712b33c93a1a06150d99bdc3e4aacc4e79fe11ad061cc'
            or _audited_legacy_migration_noop_source(source)
            or (blob in (_RETIRED_PRE_BUNDLE.keys() | _RETIRED_ENTERED_BUNDLE.keys())
                and "python3 - <<'PYMIGRATE'" in block))


def _reconciled_older_migration_generation(repository, run, job, step, attempt, token,
                                          current_application_revision, observation, current_run, current_attempt):
    """Reconcile an older physical generation without inventing historical success.

    The canonical production observer authenticates all historical envelopes and
    reads the live applied prefix/catalog twice with settled activity. This only
    separates a reviewed older package from strictly newer pending migrations;
    retained intent, changed old SQL, or an unobserved current database still deny.
    """
    import time
    if not isinstance(observation, dict):
        return False
    stamp = observation.get('historicalObservedAt')
    if (run.get('status') != 'completed' or run.get('conclusion') not in {'failure', 'cancelled'}
        or job.get('status') != 'completed' or job.get('conclusion') != 'failure'
        or step.get('name') != DIRECT_RELEASE_CHILDREN['migrations']['step']
        or step.get('status') != 'completed' or step.get('conclusion') != 'failure'
        or observation.get('historicalRunId') != current_run
        or observation.get('historicalRunAttempt') != current_attempt
        or observation.get('historicalApplicationRevision') != current_application_revision
        or observation.get('historicalExecution') != 'outcome-unknown'
        or observation.get('mutationActivity') != 'settled' or observation.get('ready') is not False
        or type(observation.get('pendingCount')) is not int or observation['pendingCount'] < 1
        or type(stamp) not in (int, float) or not 0 <= time.time() - stamp <= 900
        or any(not re.fullmatch('[a-f0-9]{64}', str(observation.get(key, '')))
               for key in ('historicalEvidenceIdentity', 'databaseIdentity', 'schemaIdentity', 'baselineIdentity'))):
        return False
    rows = observation.get('historicalReconciliationSources')
    if not isinstance(rows, list) or any(not isinstance(row, dict) for row in rows):
        return False
    matched = [row for row in rows if row.get('run') == run['id'] and row.get('attempt') == attempt]
    if (len(matched) != 1 or matched[0].get('code') != 'HISTORICAL_EXECUTION_REQUIRES_RECONCILIATION'
        or matched[0].get('migrationRecords') != []
        or type(matched[0].get('stateArtifactId')) is not int or matched[0]['stateArtifactId'] < 1
        or not re.fullmatch('[a-f0-9]{64}', str(matched[0].get('admissionId', '')))):
        return False
    try:
        _trusted_child_producer(repository, run)
        paths = ('.github/workflows/' + DIRECT_RELEASE_WORKFLOW,
                 'scripts/release-prepublication.py', 'scripts/release-migration.py')
        blobs = []
        for path in paths:
            raw = _release_history_source(repository, run['head_sha'], path, token).encode()
            blobs.append(hashlib.sha1(b'blob ' + str(len(raw)).encode() + b'\0' + raw).hexdigest())
        # Original October 9 producers. This is recognition, never nonentry.
        if tuple(blobs) not in {
            ('1b0ae6aa07a3ac05c132f0df4cfa2858531e2999', 'dd2b27981ad9ecec96e38adff176a97385f7eb42', '8314be265893acc2edef0f073da52ab6d1888a31'),
            ('1b0ae6aa07a3ac05c132f0df4cfa2858531e2999', '7587632c2465da5569c0121e2aa11d7c76eab3ff', 'de20411ca5b25a75c7e94f77e762cd8193ae2adc'),
        } or matched[0].get('sourceBlob') != blobs[0]:
            return False
        prior = _release_attempt_package_revision(repository, run, attempt, job, token, 'portal', aggregate=True)
        return _applied_migration_prefix_separate(prior, current_application_revision,
            observation.get('firstPendingMigrationId'), observation.get('lastAppliedMigrationId'))
    except (KeyError, TypeError, ValueError, OSError, RuntimeError, UnicodeError,
            subprocess.SubprocessError):
        return False


def _release_child_original_records(repository, child, token, before, current_run, current_attempt):
    """Inventory original child records from both trusted producer workflows.

    The record's own material identifies its physical operation. The current
    caller's material is deliberately absent from this read-only catalog.
    """
    import datetime
    def stamp(value):
        if not isinstance(value, str):
            raise RuntimeError('Release child original receipt time unproven')
        parsed = datetime.datetime.fromisoformat(value.replace('Z', '+00:00'))
        if parsed.tzinfo is None:
            raise RuntimeError('Release child original receipt time unproven')
        return parsed
    producers = fast_track_execution_history(repository, token)
    branch = urllib.parse.quote(TRUSTED_PR_BASE, safe='')
    seen, expected, ids = 0, None, set()
    for page in range(1, 11):
        payload = api_get(repository, f'actions/runs?branch={branch}&event=workflow_dispatch&per_page=100&page={page}', token)
        rows, total = payload.get('workflow_runs'), payload.get('total_count')
        if (not isinstance(rows, list) or type(total) is not int or total < 0
            or (expected is not None and total != expected)):
            raise RuntimeError('Release child original producer inventory incomplete')
        expected = total
        for run in rows:
            if not isinstance(run, dict) or type(run.get('id')) is not int or run['id'] in ids:
                raise RuntimeError('Release child original producer inventory incomplete')
            ids.add(run['id'])
            if run.get('path', '').split('@')[0] == '.github/workflows/' + DIRECT_RELEASE_WORKFLOW:
                _trusted_child_producer(repository, run)
                producers.append(run)
        seen += len(rows)
        if len(rows) < 100:
            break
    if seen != expected:
        raise RuntimeError('Release child original producer inventory incomplete')
    records = []
    for run in producers:
        _trusted_child_producer(repository, run)
        if stamp(run['created_at']) >= before:
            continue
        if run.get('status') != 'completed' and run['id'] != current_run:
            raise RuntimeError('Release child original producer still active')
        payload = _release_history_api(repository, f"actions/runs/{run['id']}/artifacts?per_page=100", token)
        artifacts = payload.get('artifacts')
        if (not isinstance(artifacts, list) or type(payload.get('total_count')) is not int
            or payload['total_count'] != len(artifacts)
            or any(not isinstance(a, dict) or type(a.get('id')) is not int or a['id'] < 1
                   or not isinstance(a.get('name'), str) for a in artifacts)
            or len({a['id'] for a in artifacts}) != len(artifacts)):
            raise RuntimeError('Release child original generation inventory incomplete')
        for artifact in artifacts:
            phase = re.fullmatch(r'legend-release-child-(intent|success)-[a-f0-9]{64}', artifact['name'])
            if phase is None or stamp(artifact['created_at']) >= before:
                continue
            # An expired generic child archive can conceal this resource.
            if artifact.get('expired') is not False or artifact.get('workflow_run', {}).get('id') != run['id']:
                raise RuntimeError('Release child original receipt unavailable')
            record = _release_history_json(repository, run['id'], artifact, 'operation.json')
            if not isinstance(record, dict):
                raise RuntimeError('Release child original receipt malformed')
            if record.get('child') != child:
                continue
            _validate_child_generation(repository, run, artifact, record, child, token, phase[1])
            if run['id'] == current_run and record['producingAttempt'] == current_attempt:
                continue
            if (record.get('partitionIdentity') != record['materialIdentity']
                or not isinstance(record.get('observation'), dict)
                or set(record['observation']) != {'providerVersion'}
                or not re.fullmatch(r'[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}|absent',
                                    str(record['observation']['providerVersion']))
                or (phase[1] == 'success' and record['observation']['providerVersion'] == 'absent')):
                raise RuntimeError('Release child original router observation unproven')
            records.append((run, artifact, record))
    return records


def attested_fast_track_preserved_router_intent(repository, run, jobs, token, current_run, current_attempt):
    """Recover the ORIGINAL intent of a positively preserved historical Worker.

    DNS reconciliation after the preserved marker is a separate mutation
    boundary. This does not classify the historical step as prewrite, mint a
    receipt, establish current provider state, or authorize an upload.
    """
    try:
        evidence = _fast_track_router_attempt_evidence(repository, run, jobs, token)
        if evidence is None:
            return None
        # These complete owners bind the historical preserved branch, its
        # original receipt reader, and the later DNS-only failure.
        sources = {
            'scripts/release-router.py': '696528f86ba40a8a5edf5814e61908b0bd305ed74c4eb9b05d7981e38dd209a3',
            'scripts/release-operation-evidence.py': '05192d3b67903f7091d5131960572a6d8f7c9bcea82ec0fb833ca27a71f4493b',
            'scripts/validation-resume.py': '54354287a62a79df3c242ced290ec0be7596a4aa9e5b637bbb00225dd4ae0f2d',
            'scripts/cloudflare-routing-authority.py': '5822f8fdbe690ed4b637a6b9b2c1f3a5bc831b0aee8b22d559ee8b4067377d73',
            'scripts/deploy-founder-cloudflare.py': 'ad2a258bc22cf2e3c981838efd02931fa8e8fe9b45bddc73c9fe54e20b3e92f1'}
        for path, digest in sources.items():
            if hashlib.sha256(_release_history_source(repository, run['head_sha'], path, token).encode()).hexdigest() != digest:
                return None
        markers = [
            'FAST_TRACK_COMPANY_RENDERER:VERIFIED_BEFORE_ROUTING',
            'FAST_TRACK_ROUTER:preserved',
            'FAST_TRACK_DNS_PROXY_RECONCILIATION:apex_and_www_only',
            'cloudflare-routing-authority.CloudflareError: Website DNS proxy requires DNS Edit authority or recovery: mylegnd.com']
        bounded = evidence['bounded']
        if (any(bounded.count(marker) != 1 for marker in markers)
            or [bounded.index(marker) for marker in markers] != sorted(bounded.index(marker) for marker in markers)
            or any(line.startswith('FAST_TRACK_ROUTER:') and line != markers[1] for line in bounded)
            or any(sum(message == marker for _, message in evidence['timed']) != 1 for marker in markers)):
            return None
        records = _release_child_original_records(repository, 'routing-cloudflare', token,
            evidence['start'], current_run, current_attempt)
        groups = {}
        for producer, artifact, record in records:
            # Select with each ORIGINAL material, never a recomputed current
            # provider material or the existence of a convenient complete pair.
            if direct_child_operation_identity('routing-cloudflare', run['head_sha'],
                    record['materialIdentity']) != record['dependencyIdentity']:
                continue
            key = (record['dependencyIdentity'], record['materialIdentity'], record['partitionIdentity'])
            groups.setdefault(key, {'intent': [], 'success': []})[record['phase']].append(record)
        if len(groups) != 1:
            raise RuntimeError('Historical preserved router generation is not unique')
        (generation, material, partition), phases = next(iter(groups.items()))
        original = {}
        for phase in ('intent', 'success'):
            rows = phases[phase]
            if not rows or any(row != rows[0] for row in rows[1:]):
                raise RuntimeError('Historical preserved router original pair incomplete or competing')
            record = release_child_history(repository, 'routing-cloudflare', generation, token, phase)
            if record != rows[0] or record.get('materialIdentity') != material or record.get('partitionIdentity') != partition:
                raise RuntimeError('Historical preserved router canonical receipt changed')
            original[phase] = record
        return original['intent']
    except (KeyError, TypeError, ValueError, OSError, UnicodeError, subprocess.SubprocessError) as error:
        raise RuntimeError('Historical preserved router evidence unavailable') from error


def attested_fast_track_no_write_observation(repository, run, attempt, job, step, artifacts,
                                             token, historical_observation):
    """Recognize the canonical observer's original execution, never infer nonentry.

    Successful siblings can be carried into later GitHub attempt inventories
    under new job IDs. Only identical original step evidence binds that carry.
    This is historical no-SQL evidence, not current desired-state readiness.
    """
    from datetime import datetime
    if (not is_fast_track_producer(repository, run) or step.get('conclusion') != 'success'
            or step.get('status') != 'completed' or job.get('conclusion') != 'success'
            or job.get('head_sha') != run['head_sha'] or job.get('run_attempt') != attempt
            or job.get('run_id') != run['id'] or job.get('status') != 'completed'
            or not isinstance(historical_observation, dict)):
        return False
    database = historical_observation.get('databaseIdentity')
    if not isinstance(database, str) or not re.fullmatch('[a-f0-9]{64}', database):
        return False
    if not _audited_fast_track_observer_source(repository, run, token):
        return False

    def jobs_for(number):
        payload = api_get(repository, f"actions/runs/{run['id']}/attempts/{number}/jobs?per_page=100", token)
        rows = payload.get('jobs')
        if not isinstance(rows, list) or payload.get('total_count') != len(rows):
            raise RuntimeError('Historical no-write attempt inventory incomplete')
        return rows

    matches = []
    for artifact in artifacts:
        if not re.fullmatch(r'legend-migration-observation-[a-f0-9]{64}', artifact.get('name', '')):
            continue
        if (artifact.get('workflow_run', {}).get('id') != run['id']
                or artifact.get('workflow_run', {}).get('head_sha') != run['head_sha']
                or artifact.get('expired') is not False):
            raise RuntimeError('Historical no-write artifact producer unproven')
        record = _release_history_json(repository, run['id'], artifact, 'operation.json', verify_digest=True)
        fields = {'candidate', 'executionAuthority', 'databaseIdentity', 'schemaIdentity', 'baselineIdentity',
                  'observedUtc', 'pendingCount', 'mutationActivity', 'historicalExecution',
                  'historicalEvidenceIdentity', 'historicalReconciliationSources', 'schemaVersion', 'phase',
                  'kind', 'bundleDigest', 'sqlExecutionAuthorized', 'producingRun', 'producingAttempt',
                  'observationIdentity'}
        if not isinstance(record, dict) or set(record) != fields:
            raise RuntimeError('Historical no-write observation schema unproven')
        identity = hashlib.sha256(json.dumps({k: v for k, v in record.items() if k != 'observationIdentity'},
                                            sort_keys=True, separators=(',', ':')).encode()).hexdigest()
        if record['observationIdentity'] != identity or artifact['name'] != 'legend-migration-observation-' + identity:
            raise RuntimeError('Historical no-write observation identity mismatch')
        original = record['producingAttempt']
        if (type(original) is not int or not 1 <= original <= attempt
                or type(record['producingRun']) is not int or record['producingRun'] != run['id']
                or record['executionAuthority'] != run['head_sha']):
            continue
        if (type(record['schemaVersion']) is not int or record['schemaVersion'] != 1
                or record['phase'] != 'observation' or record['kind'] != 'current-schema-no-write'
                or type(record['pendingCount']) is not int or record['pendingCount'] != 0
                or record['sqlExecutionAuthorized'] is not False or record['mutationActivity'] != 'settled'
                or record['databaseIdentity'] != database
                or not isinstance(record['historicalReconciliationSources'], list)
                or not isinstance(record['historicalExecution'], str)
                or any(not isinstance(record[key], str) or not re.fullmatch('[a-f0-9]{64}', record[key])
                       for key in ('databaseIdentity', 'schemaIdentity', 'baselineIdentity', 'bundleDigest',
                                   'historicalEvidenceIdentity'))
                or not isinstance(record['candidate'], str) or not re.fullmatch('[a-f0-9]{40}', record['candidate'])):
            continue
        original_jobs = jobs_for(original)
        owners = [row for row in original_jobs if row.get('name') == job['name']]
        if len(owners) != 1:
            continue
        owner = owners[0]
        if (owner.get('head_sha') != run['head_sha'] or owner.get('run_attempt') != original
                or owner.get('run_id') != run['id']
                or owner.get('status') != 'completed' or owner.get('conclusion') != 'success'
                or owner.get('steps') != job.get('steps')):
            continue
        try:
            observed = datetime.fromisoformat(record['observedUtc'].replace('Z', '+00:00'))
            start = datetime.fromisoformat(step['started_at'].replace('Z', '+00:00'))
            end = datetime.fromisoformat(step['completed_at'].replace('Z', '+00:00'))
            if not start <= observed <= end:
                continue
        except (KeyError, TypeError, ValueError):
            continue
        admissions = []
        for evidence in artifacts:
            match = re.fullmatch(r'fast-track-admission-' + str(run['id']) + r'-(\d+)', evidence.get('name', ''))
            if not match or not 1 <= int(match[1]) <= original:
                continue
            if (evidence.get('workflow_run', {}).get('id') != run['id']
                    or evidence.get('workflow_run', {}).get('head_sha') != run['head_sha']
                    or evidence.get('expired') is not False):
                raise RuntimeError('Historical no-write source admission unproven')
            admitted_jobs = [row for row in jobs_for(int(match[1]))
                             if row.get('name') == 'Admit exact validated fast-track source']
            original_admission = [row for row in original_jobs
                                  if row.get('name') == 'Admit exact validated fast-track source']
            if (len(admitted_jobs) != 1 or len(original_admission) != 1
                    or admitted_jobs[0].get('conclusion') != 'success'
                    or admitted_jobs[0].get('status') != 'completed'
                    or admitted_jobs[0].get('run_id') != run['id']
                    or admitted_jobs[0].get('run_attempt') != int(match[1])
                    or admitted_jobs[0].get('head_sha') != run['head_sha']
                    or original_admission[0].get('conclusion') != 'success'
                    or original_admission[0].get('status') != 'completed'
                    or original_admission[0].get('run_id') != run['id']
                    or original_admission[0].get('run_attempt') != original
                    or original_admission[0].get('head_sha') != run['head_sha']
                    or original_admission[0].get('steps') != admitted_jobs[0].get('steps')):
                continue
            proof = _release_history_json(repository, run['id'], evidence, 'fast-track-admission.json', verify_digest=True)
            if proof.get('executionAuthority') == run['head_sha'] and proof.get('candidate') == record['candidate']:
                admissions.append(proof)
        if admissions and all(proof == admissions[0] for proof in admissions):
            matches.append(record)
    return len(matches) == 1


def _reconciled_retained_migration_intent(repository, run, job, step, attempt, record,
                                         artifacts, token, observation, revision,
                                         current_run, current_attempt):
    """Separate the reviewed completed old bundle from a fresh pending suffix.

    This is current physical reconciliation, not an old success receipt. The
    original intent and UNKNOWN history remain immutable. No SQL is executed.
    """
    import datetime
    import time
    original = 'd77869f989b9dd141bb3f02f250e473f43eb607a'
    producer = '86da37ad49b44b0de360102d89ba56793ffb2cac'
    schema = '978bc3150b19da4e1b07c629cba73aec0d67a2ec11a52da0dc69c69491d08428'
    bundle = '313cfb90248ce5892483a8ab8cc026d4d1a56b327eac8920df87bc4032cf3761'
    database = '3a055450807aeaa2c6465702b983dc99968329389d7c0ae480c3c72cc3a9ff3b'
    material = hashlib.sha256(json.dumps(dict(schemaIdentity=schema, bundleDigest=bundle), sort_keys=True).encode()).hexdigest()
    if (not isinstance(observation, dict) or run.get('id') != 39502861878
        or run.get('head_sha') != original or not is_fast_track_producer(repository, run)
        or run.get('status') != 'completed' or run.get('conclusion') != 'failure'
        or attempt != 1 or job.get('id') != 118904885770
        or job.get('run_id') != run['id'] or job.get('head_sha') != original
        or job.get('run_attempt') != attempt or job.get('status') != 'completed'
        or job.get('conclusion') != 'failure'
        or step.get('name') != FAST_TRACK_CHILD_STEPS['migrations'][1]
        or step.get('status') != 'completed' or step.get('conclusion') != 'failure'
        or record.get('producingRun') != run['id'] or record.get('producingAttempt') != attempt
        or record.get('phase') != 'intent' or record.get('child') != 'migrations'
        or record.get('applicationRevision') != original or record.get('executionAuthority') != original
        or record.get('materialIdentity') != material
        or record.get('dependencyIdentity') != '07778a69e437eee4ba8204ac24968b1d0328898e5085057e9141ac25dd87d26d'
        or record.get('partitionIdentity') != '8470695e217fc16f1ac0006417f22309315d65e6d27a22a22913752fdf8f1c44'
        or record.get('observation') != {'schemaIdentity': schema}
        or observation.get('databaseIdentity') != database
        or observation.get('historicalRunId') != current_run
        or observation.get('historicalRunAttempt') != current_attempt
        or observation.get('historicalApplicationRevision') != revision
        or observation.get('historicalExecution') != 'outcome-unknown'
        or observation.get('mutationActivity') != 'settled' or observation.get('ready') is not False
        or type(observation.get('pendingCount')) is not int or observation['pendingCount'] < 1
        or type(observation.get('historicalObservedAt')) not in (int, float)
        or not 0 <= time.time() - observation['historicalObservedAt'] <= 900
        or any(not re.fullmatch('[a-f0-9]{64}', str(observation.get(key, '')))
               for key in ('historicalEvidenceIdentity', 'schemaIdentity', 'baselineIdentity'))):
        return False
    try:
        # Full historical owners were reviewed: schema-verification is reached
        # only after the synchronous bundle subprocess returns exit code zero.
        sources = {
            '.github/workflows/legend-fast-track-ui.yml': '4f551f6cda7b2161abe7d36d5dbba510f081b10fa92ab9a64eb2e3205f6081d2',
            'scripts/release-migration.py': '9eb7a6893a9f83bf08b8f630007d4df6c62ea6cb840f3d1925cc1e4367f1453a',
            'scripts/release-operation-evidence.py': 'd9076f87cf84c33077295d7b892725140fa6f8c33fa46d3cd4b6bf2299c87c30',
            'scripts/migration-probe-package.py': 'ecf41255a13c8536967a3d863d15375a5629776bc3a136870da7b56c55ea95cb'}
        if any(hashlib.sha256(_release_history_source(repository, original, path, token).encode()).hexdigest() != digest
               for path, digest in sources.items()):
            return False
        matching = [a for a in artifacts if a.get('name', '').startswith('legend-release-child-')]
        if (len(matching) != 1 or matching[0].get('id') != 12302898536
            or matching[0].get('digest') != 'sha256:4cc0470265f0233749f4b1d52cbe444d0f76db8d164785571b8ff777f9c86ebd'
            or _release_history_json(repository, run['id'], matching[0], 'operation.json', verify_digest=True) != record):
            return False
        log = _release_job_log(repository, job['id'], token)
        clean = '\n'.join(re.sub(r'^\d{4}-\d\d-\d\dT[0-9:.]+Z ', '', line) for line in log.splitlines())
        if set(re.findall(r'(?m)^\[command\]/usr/bin/git log -1 --format=%H\n([a-f0-9]{40})$', clean)) != {original}:
            return False
        if clean.splitlines().count('FAST_TRACK_REHEARSAL_RESTORED:producer=' + producer) != 1:
            return False
        start = datetime.datetime.fromisoformat(step['started_at'].replace('Z', '+00:00'))
        end = datetime.datetime.fromisoformat(step['completed_at'].replace('Z', '+00:00'))
        if start.tzinfo is None or end.tzinfo is None or end < start:
            return False
        bounded = []
        for line in log.splitlines():
            stamp, sep, message = line.partition(' ')
            if sep and '\x1b[' not in message and re.fullmatch(r'\d{4}-\d\d-\d\dT[0-9:.]+Z', stamp):
                when = datetime.datetime.fromisoformat(stamp.replace('Z', '+00:00'))
                if start <= when <= end + datetime.timedelta(seconds=2):
                    bounded.append(message)
        terminal = [line for line in bounded if line.startswith('Migration stage unresolved:')]
        if (terminal != ['Migration stage unresolved: schema-verification; preserve prior evidence and reconcile without replay.']
            or bounded.count('##[error]Process completed with exit code 1.') != 1):
            return False
        # Exact immutable rehearsal bytes bind original bundle, SQL target and
        # baseline. They authorize no write and are never executed here.
        inventory = api_get(repository, 'actions/runs/39476906913/artifacts?per_page=100', token)
        rows = inventory.get('artifacts')
        if not isinstance(rows, list) or inventory.get('total_count') != len(rows):
            return False
        selected = [a for a in rows if a.get('id') == 12290486085]
        if (len(selected) != 1 or selected[0].get('name') != 'parfait-fast-track-migration-' + producer + '-1'
            or selected[0].get('workflow_run', {}).get('head_sha') != '6ddf83d94a24b5f06c127a366031a9a1f3fba5c6'
            or selected[0].get('digest') != 'sha256:3520e83c67c3a3fd0c50c36c1a8ecf302687b2be9e0b073a527cc02f6096c2dd'):
            return False
        proof = _release_history_json(repository, 39476906913, selected[0], 'rehearsal.json', verify_digest=True)
        baseline = proof.get('baseline', {})
        if (proof.get('candidate') != producer or proof.get('producingRun') != 39476906913
            or proof.get('producingAttempt') != 1 or proof.get('bundleDigest') != bundle
            or proof.get('schemaIdentity') != schema or proof.get('proven') is not True
            or proof.get('authorizesMutation') is not False
            or baseline.get('databaseIdentity') != database or baseline.get('schemaIdentity') != schema
            or baseline.get('pendingCount') != 1
            or baseline.get('firstPendingMigrationId') != '20261020160000_AddReceptionistCoordination'):
            return False
        return _applied_migration_prefix_separate(producer, revision,
            observation.get('firstPendingMigrationId'), observation.get('lastAppliedMigrationId'))
    except (KeyError, TypeError, ValueError, RuntimeError, OSError, UnicodeError, subprocess.SubprocessError):
        return False


def release_child_first_write_proven(repository, child, dependency_identity, material_identity,
                                     current_run, current_attempt, token, *, partition_identity=None,
                                     first_pending_migration_id=None, last_applied_migration_id=None,
                                     current_application_revision=None, historical_observation=None):
    """Authorize first mutation only from complete positive execution evidence.

    Deleted/expired receipts never establish absence. A started historical child
    must have an authenticated different *physical* operation generation. Its
    verifier revision, title or current caller material cannot stand in for the
    original operation's immutable material.
    """
    gate = DIRECT_RELEASE_CHILDREN[child]
    partition_identity = partition_identity or material_identity

    def rejection(message, run_id=None, attempt=None):
        """Keep original denial semantics; attach only bounded GitHub run identity.

        This is not write authorization. No provider exception or artifact body
        is allowed into the diagnostic, and non-migration callers are unchanged.
        """
        if child != 'migrations' or type(run_id) is not int or not 0 < run_id < 10**13:
            return RuntimeError(message)
        suffix = f' [run={run_id}'
        if type(attempt) is int and 0 < attempt <= 100:
            suffix += f';attempt={attempt}'
        return RuntimeError(message + suffix + ']')
    if (not re.fullmatch(r"[a-f0-9]{64}", partition_identity)
            or not re.fullmatch(r"[a-f0-9]{64}", dependency_identity)
            or not re.fullmatch(r"[a-f0-9]{64}", material_identity)
            or type(current_run) is not int or current_run < 1
            or type(current_attempt) is not int or current_attempt < 1):
        raise ValueError("Invalid release child mutation identity")
    def require_distinct_intent(record, run_id, attempt, *, reconciled=False):
        generation = record['dependencyIdentity']
        if generation == dependency_identity:
            raise rejection('Release child physical operation already entered; reconcile without replay', run_id, attempt)
        partition = record.get('partitionIdentity')
        if not re.fullmatch('[a-f0-9]{64}', str(partition)):
            raise rejection('Release child historical partition unproven; no replay authorized', run_id, attempt)
        if partition == partition_identity:
            completed = release_child_history(repository, child, generation, token, 'success')
            if (completed is not None and (completed.get('materialIdentity') != record.get('materialIdentity')
                    or completed.get('partitionIdentity') != partition)) or (completed is None and not reconciled):
                raise rejection('Release child prior partition operation unresolved; no replay authorized', run_id, attempt)

    for run, attempt, job, step in fast_track_child_attempts(repository, child, token,
            current_run=current_run, current_attempt=current_attempt):
        payload = api_get(repository, f"actions/runs/{run['id']}/artifacts?per_page=100", token)
        artifacts = payload.get('artifacts')
        if not isinstance(artifacts, list) or payload.get('total_count') != len(artifacts):
            raise rejection('Release child original generation inventory incomplete', run['id'], attempt)
        intents, observations = [], []
        for artifact in artifacts:
            match = re.fullmatch(r'legend-release-child-(intent|success)-[a-f0-9]{64}', artifact.get('name', ''))
            if not match:
                continue
            if artifact.get('expired') is not False:
                raise rejection('Release child original intent expired; no replay authorized', run['id'], attempt)
            record = _release_history_json(repository, run['id'], artifact, 'operation.json')
            if record.get('child') != child or record.get('producingAttempt') != attempt:
                continue
            generation = _validate_child_generation(repository, run, artifact, record, child, token, match[1])
            if match[1] == 'success' and record.get('observation', {}).get('providerVersion') == 'schema-ready-no-write':
                observations.append(record)
            if match[1] != 'intent':
                continue
            intents.append(record)
        if not intents and child == 'routing-cloudflare':
            original = attested_fast_track_preserved_router_intent(
                repository, run, [job], token, current_run, current_attempt)
            if original is not None:
                intents.append(original)
        for record in intents:
            reconciled = (child == 'migrations' and len(intents) == 1 and not observations
                and _reconciled_retained_migration_intent(repository, run, job, step, attempt,
                    record, artifacts, token, historical_observation, current_application_revision,
                    current_run, current_attempt))
            require_distinct_intent(record, run['id'], attempt, reconciled=reconciled)
            if reconciled:
                print(f"LEGEND_MIGRATION_HISTORY:RETAINED_INTENT_APPLIED_PREFIX:run={run['id']}:attempt={attempt}:historical-outcome-unknown")
        if (not intents and not observations and child == 'migrations'
                and attested_fast_track_no_write_observation(repository, run, attempt, job, step,
                                                            artifacts, token, historical_observation)):
            continue
        if not intents and not (child == 'migrations' and len(observations) == 1 and step.get('conclusion') == 'success'):
            raise rejection('Release child may have written; missing intent cannot authorize replay', run['id'], attempt)
    # The workflow-specific inventory returns 403 to the trusted release token.
    # Query the same canonical Actions authority through its repository run
    # inventory and authenticate the exact workflow on every retained row.
    branch = urllib.parse.quote(TRUSTED_PR_BASE, safe="")
    seen = 0
    for page in range(1, 11):
        payload = api_get(repository, f"actions/runs?branch={branch}&event=workflow_dispatch&per_page=100&page={page}", token)
        runs = payload.get("workflow_runs")
        if not isinstance(runs, list):
            raise rejection("Release child execution history unavailable")
        seen += len(runs)
        for run in runs:
            if (run.get("head_branch") != TRUSTED_PR_BASE
                or run.get("event") != "workflow_dispatch"
                or run.get("path", "").split("@")[0] != ".github/workflows/" + DIRECT_RELEASE_WORKFLOW):
                continue
            _trusted_child_producer(repository, run)
            run_id = run["id"]
            attempts = run.get("run_attempt", 1)
            if type(attempts) is not int or attempts < 1:
                raise rejection("Release child attempt history unavailable", run_id)
            if run_id == current_run and current_attempt == 1:
                continue
            inventory = None
            for attempt in range(1, attempts + 1):
                if run_id == current_run and attempt == current_attempt:
                    continue
                jobs_payload = api_get(repository, f"actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100", token)
                jobs = jobs_payload.get("jobs")
                count = jobs_payload.get("total_count")
                if (not isinstance(jobs, list) or type(count) is not int or count != len(jobs)
                    or any(not isinstance(job, dict) for job in jobs)):
                    raise rejection("Release child execution history incomplete", run_id, attempt)
                if not jobs and count == 0 and (run.get('status') == 'completed' or attempt < attempts):
                    # Match release_operation_history: complete terminal attempt
                    # enumeration proves no job entered, not a missing receipt.
                    continue
                if release_attempt_never_entered(jobs):
                    continue
                if any(job.get('name') == 'admission' for job in jobs):
                    source = _release_history_source(repository, run['head_sha'],
                        '.github/workflows/' + DIRECT_RELEASE_WORKFLOW, token)
                    if release_attempt_never_entered(jobs, source):
                        continue
                owners = [job for job in jobs if job.get("name") == "release"]
                if len(owners) != 1:
                    raise rejection("Release child owner unproven", run_id, attempt)
                job = owners[0]
                if job.get("status") == "queued" or job.get("conclusion") == "skipped":
                    continue
                if (child in {'migrations', 'routing-cloudflare'} and
                    _attested_retired_unscheduled_release(
                        repository, run, job, jobs, attempt, token)):
                    continue
                steps = [step for step in job.get("steps", []) if step.get("name") == gate["step"]]
                if not steps and child == 'routing-cloudflare':
                    source = _release_history_source(repository, run['head_sha'],
                        '.github/workflows/' + DIRECT_RELEASE_WORKFLOW, token)
                    legacy = [step for step in job.get('steps', [])
                              if step.get('name') == 'Deploy shared Cloudflare business website router']
                    if len(legacy) == 1 and legacy_router_step_never_entered(source, legacy[0]):
                        continue
                    if len(legacy) == 1 and legacy_router_completed_generation(
                            repository, run, job, legacy[0], source, token, dependency_identity, material_identity):
                        continue
                if not steps and child == 'migrations':
                    source = _release_history_source(repository, run['head_sha'],
                        '.github/workflows/' + DIRECT_RELEASE_WORKFLOW, token)
                    legacy = 'Apply additive diagnostics migrations before restarting apps'
                    # Recognize the exact historical step, but never authorize
                    # a write from its conclusion alone; _legacy_migration_noop
                    # validates the current run's checkout and no-write marker.
                    steps = [step for step in job.get('steps', []) if step.get('name') == legacy]
                    if len(steps) != 1 or not legacy_migration_step_recognized(source, steps[0]):
                        steps = []
                if len(steps) != 1:
                    raise rejection("Release child execution detail unavailable", run_id, attempt)
                if steps[0].get("status") == "queued" or steps[0].get("conclusion") == "skipped":
                    continue
                if (child == 'migrations' and steps[0].get('name') == gate['step']
                    and steps[0].get('conclusion') == 'failure'
                    and _attested_migration_prewrite_failure(repository, run, job, steps[0], attempt, token)):
                    continue
                if (child == 'migrations' and steps[0].get('name') == gate['step']
                    and steps[0].get('conclusion') == 'success'
                    and _attested_migration_noop_success(
                        repository, run, job, steps[0], attempt, token)):
                    continue
                if child == 'migrations' and steps[0].get('name') != gate['step']:
                    if _attested_legacy_ef_factory_nonentry(
                            repository, run, job, steps[0], attempt, token):
                        continue
                    if _legacy_migration_noop(repository, run, job, steps[0], source, token):
                        continue
                    if _attested_retired_legacy_failure(
                            repository, run, job, steps[0], source, attempt, token,
                            first_pending_migration_id=first_pending_migration_id,
                            last_applied_migration_id=last_applied_migration_id,
                            current_application_revision=current_application_revision):
                        continue
                    if _legacy_completed_bundle_is_separate_from_pending_sql(
                            repository, run, job, steps[0], source, attempt, token,
                            first_pending_migration_id=first_pending_migration_id,
                            last_applied_migration_id=last_applied_migration_id,
                            current_application_revision=current_application_revision):
                        continue
                if (child == 'migrations'
                    and _historical_migration_prewrite_proven(
                        repository, run, job, steps[0], attempt, token)):
                    continue
                if (child == 'migrations' and _reconciled_older_migration_generation(
                        repository, run, job, steps[0], attempt, token,
                        current_application_revision, historical_observation, current_run, current_attempt)):
                    print(f'LEGEND_MIGRATION_HISTORY:OLDER_APPLIED_GENERATION:run={run_id}:attempt={attempt}:historical-outcome-unknown')
                    continue
                if inventory is None:
                    artifact_payload = api_get(repository, f"actions/runs/{run_id}/artifacts?per_page=100", token)
                    inventory = artifact_payload.get("artifacts")
                    if not isinstance(inventory, list) or artifact_payload.get("total_count", 0) > len(inventory):
                        raise rejection("Release child original generation inventory incomplete", run_id, attempt)
                generations = []
                for artifact in inventory:
                    if not re.fullmatch(r"legend-release-child-intent-[a-f0-9]{64}", artifact.get("name", "")):
                        continue
                    # Any expired intent can conceal this child's original write.
                    if artifact.get("expired"):
                        raise rejection("Release child original intent expired; no replay authorized", run_id, attempt)
                    record = _release_history_json(repository, run_id, artifact, "operation.json")
                    if record.get("child") != child:
                        continue
                    generation = _validate_child_generation(repository, run, artifact, record, child, token)
                    if record["producingAttempt"] == attempt:
                        generations.append(generation)
                        require_distinct_intent(record, run_id, attempt)
                if not generations:
                    if child == 'migrations' and steps[0].get('conclusion') == 'success':
                        observed = []
                        for artifact in inventory:
                            if not re.fullmatch(r'legend-release-child-success-[a-f0-9]{64}', artifact.get('name', '')):
                                continue
                            record = _release_history_json(repository, run_id, artifact, 'operation.json')
                            if record.get('child') != child or record.get('producingAttempt') != attempt:
                                continue
                            _validate_child_generation(repository, run, artifact, record, child, token, 'success')
                            if record.get('observation', {}).get('providerVersion') == 'schema-ready-no-write':
                                observed.append(record)
                        if len(observed) == 1:
                            # An authenticated desired-state observation is not
                            # permission to replay a missing historical intent.
                            continue
                    raise rejection(
                        "Release child failed prepublication but the no-write proof was not authenticated"
                        if child == 'migrations' and steps[0].get('name') == gate['step']
                           and steps[0].get('conclusion') == 'failure'
                        else "Release child may have written; missing intent cannot authorize replay",
                        run_id, attempt)
        if len(runs) < 100:
            if payload.get("total_count", seen) > seen:
                raise rejection("Release child history truncated; no mutation authorized")
            return True
    raise rejection("Release child history truncated; no mutation authorized")

# Single canonical web release inventory. Validation, release baseline discovery,
# deployment reconciliation, live-resume probing, package naming and final
# enforcement consume this exact definition instead of maintaining parallel maps.
RELEASE_TARGETS = {
    "portal": {
        "releaseName": "masterapp-portal",
        "host": "portal.mylegnd.com",
        "azureHost": "masterapp-portal.azurewebsites.net",
        "project": "AgentPortal/AgentPortal.csproj",
        "sourceRoot": "AgentPortal",
        "roles": ("database-authority", "database-dependent", "shared-settings-source", "marketing-settings-target"),
        "package": "agentportal.zip",
        "provenancePath": "/api/runtime-provenance",
        "proofHosts": ("portal.mylegnd.com", "masterapp-portal.azurewebsites.net"),
        "static": False,
    },
    "client": {
        "releaseName": "masterapp-client",
        "host": "client.mylegnd.com",
        "azureHost": "masterapp-client.azurewebsites.net",
        "project": "ClientApp/ClientApp.csproj",
        "sourceRoot": "ClientApp",
        "roles": ("browser-entry", "marketing-settings-target"),
        "package": "clientapp.zip",
        "provenancePath": "/api/runtime-provenance",
        "proofHosts": ("client.mylegnd.com", "masterapp-client.azurewebsites.net"),
        "static": False,
    },
    "protect": {
        "releaseName": "masterapp-protect",
        "host": "masterapp-protect.azurewebsites.net",
        "azureHost": "masterapp-protect.azurewebsites.net",
        "project": "Protect-Website/ProtectWebsite.csproj",
        "sourceRoot": "Protect-Website",
        "roles": ("database-dependent", "shared-auth-target", "editor-target", "marketing-settings-target", "routing-target", "routing-primary", "public-release"),
        "routingProbePath": "/",
        "package": "protect.zip",
        "provenancePath": "/api/runtime-provenance",
        "proofHosts": ("masterapp-protect.azurewebsites.net",),
        "static": False,
    },
    "parfait": {
        "releaseName": "masterapp-parfait",
        "host": "masterapp-parfait.azurewebsites.net",
        "azureHost": "masterapp-parfait.azurewebsites.net",
        "project": "ParfaitApp/ParfaitApp.csproj",
        "sourceRoot": "ParfaitApp",
        "roles": ("database-dependent", "editor-target", "marketing-settings-target", "routing-target"),
        "routingProbePath": "/store",
        "package": "parfait.zip",
        "provenancePath": "/api/runtime-provenance",
        "proofHosts": ("masterapp-parfait.azurewebsites.net",),
        "static": False,
    },
    "website": {
        "releaseName": "masterapp-website",
        "host": "masterapp-website.azurewebsites.net",
        "azureHost": "masterapp-website.azurewebsites.net",
        "project": "static",
        "sourceRoot": "Legend-Website",
        "roles": ("static-target", "public-release"),
        "package": "website.zip",
        "provenancePath": "/_deployment-provenance.txt",
        "proofHosts": ("masterapp-website.azurewebsites.net", "mylegnd.com", "www.mylegnd.com"),
        "static": True,
    },
}

def release_name_map():
    return {row["releaseName"]: key for key, row in RELEASE_TARGETS.items()}


def target_keys_with_role(role: str):
    return tuple(
        key for key, row in RELEASE_TARGETS.items()
        if role in row.get("roles", ())
    )


def unique_target_with_role(role: str):
    keys = target_keys_with_role(role)
    if len(keys) != 1:
        raise ValueError(f"Canonical release role {role!r} must resolve to exactly one target")
    return keys[0]


def release_runtime_profile(selected_names):
    keys = selected_release_target_keys(selected_names)
    selected = set(keys)
    def names_for(role):
        return [
            RELEASE_TARGETS[key]["releaseName"]
            for key in target_keys_with_role(role)
        ]
    def selected_has(role):
        return any(key in selected for key in target_keys_with_role(role))

    database_key = unique_target_with_role("database-authority")
    browser_keys = target_keys_with_role("browser-entry")
    static_keys = target_keys_with_role("static-target")
    routing_primary = unique_target_with_role("routing-primary")
    return {
        "resourceGroup": RELEASE_RESOURCE_GROUP,
        "migrationBundle": MIGRATION_BUNDLE_NAME,
        "routingWorker": ROUTING_WORKER_NAME,
        "routingPassThroughHosts": list(ROUTING_PASS_THROUGH_HOSTS),
        "domainRefreshProject": DOMAIN_REFRESH_PROJECT,
        "databaseAuthority": RELEASE_TARGETS[database_key]["releaseName"],
        "browserEntryTargets": names_for("browser-entry"),
        "browserEntryHosts": [
            host
            for key in browser_keys
            for host in RELEASE_TARGETS[key]["proofHosts"]
        ],
        "sharedAuthTargets": names_for("shared-auth-target"),
        "editorTargets": names_for("editor-target"),
        "marketingTargets": names_for("marketing-settings-target"),
        "routingTargets": [
            {
                "releaseName": RELEASE_TARGETS[key]["releaseName"],
                "azureHost": RELEASE_TARGETS[key]["azureHost"],
                "probePath": RELEASE_TARGETS[key]["routingProbePath"],
            }
            for key in target_keys_with_role("routing-target")
        ],
        "routingPrimary": RELEASE_TARGETS[routing_primary]["releaseName"],
        "routingPrimaryHost": RELEASE_TARGETS[routing_primary]["azureHost"],
        "selectedHasBrowserEntry": selected_has("browser-entry"),
        "selectedHasStatic": selected_has("static-target"),
        "selectedHasSharedAuth": selected_has("shared-auth-target"),
        "selectedHasEditor": selected_has("editor-target"),
        "selectedDatabaseDependent": selected_has("database-dependent"),
        "clientOnly": len(keys) == 1 and keys[0] in browser_keys,
        "websiteOnly": len(keys) == 1 and keys[0] in static_keys,
        "portalOnly": len(keys) == 1 and keys[0] == database_key,
        "publicOnly": bool(keys) and all("public-release" in RELEASE_TARGETS[key].get("roles", ()) for key in keys),
    }


def release_ef_metadata_path(path):
    """Canonical EF discovery metadata that can change the compiled migration catalog."""
    return isinstance(path, str) and bool(
        re.fullmatch(r"Infrastructure/Data/[A-Za-z0-9_]*(?:DbContext|MigrationMetadata)\.cs", path)
        or re.fullmatch(r"Infrastructure/Migrations/[0-9]{14}_[A-Za-z0-9_]+\.Designer\.cs", path)
    )


def release_admission_resources(paths, selected_names, *, routing=False):
    """Read/write resource ownership used by every release admission.

    Inventory roles describe actual runtime reads and selected-target settings
    writes. Shared source or unknown ownership already expands target scope in
    release_targets_for_paths; shared schema and Cloudflare writes additionally
    conflict with consumers even when app names alone would be disjoint.
    """
    profile = release_runtime_profile(selected_names)
    keys = selected_release_target_keys(selected_names)
    resources = {'write/app/' + RELEASE_TARGETS[key]['releaseName'] for key in keys}
    if any(not RELEASE_TARGETS[key]['static'] for key in keys):
        resources.add('read/schema/masterapp')
    if profile['selectedHasSharedAuth'] or profile['selectedHasEditor'] or profile['selectedDatabaseDependent']:
        resources.add('read/app/' + profile['databaseAuthority'])
    if any(path.startswith('Infrastructure/Migrations/') or release_ef_metadata_path(path)
           for path in paths):
        # The matching prepublication guard can enter the migration child even
        # for metadata-only EF changes. Reserve its exclusive schema authority
        # BEFORE admitting any future provider write.
        resources.add('write/schema/masterapp')
    if founder_cloudflare_release_required(paths):
        resources.add('write/cloudflare/founder')
        resources.add('write/app/' + profile['databaseAuthority'])
    if routing:
        resources.add('write/cloudflare/router')
        resources.update('write/app/' + row['releaseName'] for row in profile['routingTargets'])
    # A write subsumes its own read, retaining one canonical resource spelling.
    return sorted(value for value in resources
                  if not value.startswith('read/') or 'write/' + value[5:] not in resources)


def release_resources_overlap(left, right):
    def modes(values):
        result = {}
        for value in values:
            mode, separator, resource = value.partition('/')
            if separator != '/' or mode not in {'read', 'write'} or not resource:
                raise ValueError('Malformed release resource ownership')
            result[resource] = 'write' if mode == 'write' or result.get(resource) == 'write' else 'read'
        return result
    lhs, rhs = modes(left), modes(right)
    return any(lhs[key] == 'write' or rhs[key] == 'write' for key in lhs.keys() & rhs.keys())


def selected_release_target_keys(names, *, allow_empty=False):
    by_name = release_name_map()
    if (
        not isinstance(names, list)
        or (not allow_empty and not names)
        or len(names) != len(set(names))
        or any(not isinstance(name, str) or name not in by_name for name in names)
    ):
        raise ValueError("Selected release targets do not match canonical inventory")
    selected = set(names)
    return tuple(
        key for key, row in RELEASE_TARGETS.items()
        if row["releaseName"] in selected
    )


def founder_cloudflare_release_required(paths):
    """Publication depends on physical Worker inputs, never verifier-only edits."""
    return any(direct_child_operation_matches("founder-cloudflare", path)
               for path in dict.fromkeys(paths))


def release_targets_for_paths(paths):
    """Derive publication scope from the validated PR without a parallel scope table.

    A target owns its canonical sourceRoot. Founder Cloudflare runtime/deploy
    authority is activated through the Founder Portal control boundary, so it
    selects only the Portal target rather than expanding to every application.
    Other release-control/test-only changes need no application publication. Any
    remaining application path not owned by exactly one target is treated as
    shared/unknown and expands fail-closed to the complete inventory so a new
    shared source cannot be silently omitted.
    """
    unique_paths = tuple(dict.fromkeys(paths))
    founder_cloudflare = founder_cloudflare_release_required(unique_paths)
    application_paths = [
        path for path in unique_paths
        if not release_control_only_path(path)
        and not (
            path.startswith("Legend-Cloudflare/src/")
            or path == "Legend-Cloudflare/wrangler.founder-baseline.jsonc"
        )
    ]

    selected = {"portal"} if founder_cloudflare else set()
    if not application_paths:
        return tuple(
            row["releaseName"] for key, row in RELEASE_TARGETS.items()
            if key in selected
        )

    for path in application_paths:
        owners = [
            key for key, row in RELEASE_TARGETS.items()
            if path == row["sourceRoot"] or path.startswith(row["sourceRoot"] + "/")
        ]
        if len(owners) != 1:
            return tuple(row["releaseName"] for row in RELEASE_TARGETS.values())
        selected.add(owners[0])
    return tuple(
        row["releaseName"] for key, row in RELEASE_TARGETS.items()
        if key in selected
    )

def fast_track_scope(paths):
    """Exact fast-track impact using the canonical inventory and project links.

    Unknown application paths stop admission; they never silently expand into
    a blanket deployment. This function grants no schema mutation permission.
    """
    import posixpath
    import xml.etree.ElementTree as ET
    paths = tuple(dict.fromkeys(paths))
    dotnet = {key for key, row in RELEASE_TARGETS.items() if not row['static']}
    commerce = {'protect', 'parfait'}
    # These canonical shared server sources reach every managed app through
    # Infrastructure/Domain/Shared ProjectReference dependencies. Keep exact
    # ownership: an unknown sibling is not silently admitted.
    shared_server = {
        'Domain/Billing/BillingEnums.cs',
        'Domain/Billing/ClientSubscriptionScope.cs',
        'Domain/Billing/TrainingPlanTerms.cs',
        'Domain/Entities/ClientSubscription.cs',
        'Infrastructure/Billing/ClientBillingNotificationService.cs',
        'Infrastructure/Billing/ClientPaymentMethodService.cs',
        'Infrastructure/Billing/ClientSubscriptionActivationPolicyOptions.cs',
        'Infrastructure/Data/MasterAppSqliteSchemaBootstrapper.cs',
        'Infrastructure/Households/HouseholdReconciliationService.cs',
        'Infrastructure/Identity/ClientEntraProvisioningRecoveryHostedService.cs',
        'Infrastructure/Messaging/TranslationEntitlementAuthority.cs',
        'Domain/Billing/BillingModels.cs',
        'Domain/Entities/SubscriptionPayment.cs',
        'Domain/Entities/SubscriptionPaymentCompletionEffect.cs',
        'Domain/Entities/SubscriptionPaymentRefund.cs',
        'Infrastructure/Billing/BillingReconciliationService.cs',
        'Domain/Entities/ClientSubscriptionOffer.cs',
        'Domain/Entities/MarketingConnection.cs',
        'Domain/Entities/ReceptionistCoordination.cs',
        'Domain/Entities/ReceptionistManagement.cs',
        'Infrastructure/Billing/BillingEntitlementService.cs',
        'Infrastructure/Billing/BillingStateMapper.cs',
        'Infrastructure/Billing/MasterAppBillingOrchestrator.cs',
        'Infrastructure/Bookings/BusinessBookingAccess.cs',
        'Infrastructure/Bookings/MicrosoftCalendarConnectionAuthority.cs',
        'Infrastructure/Bookings/ReceptionistBookingAuthority.cs',
        'Infrastructure/Bookings/ReceptionistBookingContracts.cs',
        'Infrastructure/Bookings/ReceptionistBookingReadinessReader.cs',
        'Infrastructure/Businesses/BusinessWorkspaceControllerBase.cs',
        'Infrastructure/Businesses/BusinessWorkspaceControllerBase.Receptionist.cs',
        'Infrastructure/Businesses/BusinessWorkspaceControllerBase.ReceptionistManagement.cs',
        'Infrastructure/Businesses/CommerceBusinessProvisioningService.cs',
        'Infrastructure/Data/Configurations/ReceptionistModelConfiguration.cs',
        'Infrastructure/Data/Configurations/ReceptionistModelConfiguration.Management.cs',
        'Infrastructure/Households/HouseholdMembershipService.cs',
        'Infrastructure/Identity/RestrictedAiAccessBoundary.cs',
        'Infrastructure/Infrastructure.csproj',
        'Infrastructure/Diagnostics/RoutineSqlPolling.cs',
        'Infrastructure/Messaging/LegendCallSignalDeliveryHostedService.cs',
        'Infrastructure/Notifications/ApplePushDeliveryHostedService.cs',
        'Infrastructure/Notifications/FirebasePushDeliveryHostedService.cs',
        'Infrastructure/Leads/IWebsiteLifeLeadCaptureService.cs',
        'Infrastructure/Leads/ReceptionistInquiryAuthority.cs',
        'Infrastructure/Leads/ReceptionistInquiryAuthority.Outcomes.cs',
        'Infrastructure/Leads/ReceptionistInquiryContracts.cs',
        'Infrastructure/Leads/WebsiteInquiryAuthority.cs',
        'Infrastructure/Leads/WebsiteIntakeRecipientResolver.cs',
        'Infrastructure/Leads/WebsiteLeadAppNotificationService.cs',
        'Infrastructure/Leads/WebsiteLeadEmailTemplate.cs',
        'Infrastructure/Leads/WebsiteLeadNotificationAuthority.cs',
        'Infrastructure/Leads/WebsiteLeadServiceRegistration.cs',
        'Infrastructure/Leads/WebsiteLeadSubmission.cs',
        'Infrastructure/Leads/WebsiteLifeLeadCaptureService.cs',
        'Infrastructure/Leads/WorkstationLeadBuckets.cs',
        'Infrastructure/Reception/IOpenAiReceptionistClient.cs',
        'Infrastructure/Reception/OpenAiReceptionistClient.cs',
        'Infrastructure/Reception/OpenAiReceptionistProtocol.cs',
        'Infrastructure/Reception/ReceptionistAdmissionService.cs',
        'Infrastructure/Reception/ReceptionistAdmissionService.Accounting.cs',
        'Infrastructure/Reception/ReceptionistAdmissionService.Booking.cs',
        'Infrastructure/Reception/ReceptionistAdmissionService.Browser.cs',
        'Infrastructure/Reception/ReceptionistAdmissionService.Operator.cs',
        'Infrastructure/Reception/ReceptionistAdmissionService.Projections.cs',
        'Infrastructure/Reception/ReceptionistAdmissionService.ProviderWork.cs',
        'Infrastructure/Reception/ReceptionistAdmissionService.Runtime.cs',
        'Infrastructure/Reception/ReceptionistApplicationContracts.cs',
        'Infrastructure/Reception/ReceptionistApplicationServices.cs',
        'Infrastructure/Reception/ReceptionistApplicationSession.Booking.cs',
        'Infrastructure/Reception/ReceptionistContracts.cs',
        'Infrastructure/Reception/ReceptionistContentReview.cs',
        'Infrastructure/Reception/ReceptionistCredentialEnvelope.cs',
        'Infrastructure/Reception/ReceptionistIngressController.cs',
        'Infrastructure/Reception/ReceptionistBrowserController.cs',
        'Infrastructure/Reception/ReceptionistOperatorContracts.cs',
        'Infrastructure/Reception/ReceptionistOperatorService.cs',
        'Infrastructure/Reception/ReceptionistOperatorService.Management.cs',
        'Infrastructure/Reception/ReceptionistOperatorService.Stored.cs',
        'Infrastructure/Reception/ReceptionistRuntimeAuthority.cs',
        'Infrastructure/Reception/ReceptionistRuntimeWorker.cs',
        'Infrastructure/Reception/ReceptionistServiceRegistration.cs',
        'Infrastructure/Reception/ReceptionistSetupForms.cs',
        'Infrastructure/Reception/ReceptionistSidebandProtocol.cs',
        'Infrastructure/Reception/ReceptionistSql.cs',
        'SHARED/Analytics/AiSafeAnalyticsPayload.cs',
        'SHARED/Analytics/GrowthEconomicsAndOpenAiFeedContracts.cs',
        'SHARED/Analytics/MarketingManagerContracts.cs',
        'SHARED/Analytics/ReceptionistConfiguration.cs',
        'SHARED/Analytics/ReceptionistManagement.cs',
        'SHARED/Analytics/ReceptionistContentReview.cs',
        'SHARED/Analytics/ReceptionistSetup.cs',
    }
    linked = {}
    for key, row in RELEASE_TARGETS.items():
        if row['static']:
            continue
        project = Path(row['project'])
        root = ET.fromstring((Path(__file__).resolve().parents[1] / project).read_text())
        items = []
        for node in root.iter():
            if node.tag not in {'Content', 'None', 'Compile'} or not node.get('Include'):
                continue
            def patterns(value):
                return [posixpath.normpath(str(project.parent) + '/' + part.replace('\\', '/'))
                        for part in value.split(';') if part and '$(' not in part and '@(' not in part]
            items.append((patterns(node.get('Include', '')), patterns(node.get('Exclude', ''))))
        linked[key] = items
    def match(path, pattern):
        # MSBuild * stays within one directory; ** spans zero or more.
        expression = re.escape(pattern).replace(r'\*\*/', '(?:.*/)?')
        expression = expression.replace(r'\*\*', '.*').replace(r'\*', '[^/]*').replace(r'\?', '[^/]')
        return re.fullmatch(expression, path) is not None
    def owners(path):
        if path in shared_server:
            return set(dotnet)
        if path.startswith(('CommerceCore/', 'Infrastructure/Commerce/')):
            return set(commerce)
        if path.startswith('Infrastructure/WebsiteRuntime/'):
            return {'protect'}
        if path.startswith('Domain/Entities/Commerce') or path in {
            'Infrastructure/Data/MasterAppDbContext.cs',
            'Infrastructure/Data/Configurations/CommercePaymentModel.cs',
            'Infrastructure/Businesses/BusinessWorkspaceAccess.cs'} or path.startswith('Infrastructure/Migrations/'):
            return set(dotnet)
        if path.startswith(('Legend-Cloudflare/src/website-routing/', 'Legend-Cloudflare/tests/website-routing/')):
            return set()
        if path.startswith('SHARED/wwwroot/js/finance-') or path in {
            'SHARED/wwwroot/js/legend-living-balance-sheet.js',
            'SHARED/wwwroot/js/expense-lens-projection.js',
            'SHARED/wwwroot/css/legend-finance-shared.css'}:
            return {'portal', 'client'}
        if path.startswith('SHARED/wwwroot/'):
            return set(RELEASE_TARGETS)
        if path.startswith('SHARED/WebsitePlatform/'):
            return {'website', 'protect'}
        if path.startswith('SHARED/Views/') or path == 'SHARED/Shared.csproj':
            return {'portal', 'client', 'protect'}
        if path.startswith('SHARED/Branding/'):
            return {'portal', 'client', 'protect', 'website'}
        if path == 'Legend-Design/legend-public-inquiry.js':
            return {'website', 'protect'}
        if path == 'Legend-Design/legend-web-foundation.css':
            # Exact shared-file consumers: Founder public, Protect, Portal, Client.
            # Parfait does not link or publish this foundation asset.
            return {'website', 'protect', 'portal', 'client'}
        if path.startswith('Legend-Design/'):
            return set(RELEASE_TARGETS)
        if path == 'Infrastructure/WebsiteEditing/WebsiteFaviconParity.cs':
            return {'portal', 'client', 'protect'}
        if path == 'Infrastructure/WebsiteEditing/WebsitePlatformController.cs':
            return {'protect'}
        if path.startswith(('Infrastructure/Analytics/', 'Infrastructure/WebsitePublishing/',
                            'Infrastructure/WebsiteEditing/')) or path == 'Domain/Entities/WebsiteContentState.cs':
            # These shared server owners are compiled into all four managed apps.
            return set(dotnet)
        if path.startswith('Legend-Website/'):
            return {'website'} if path == 'Legend-Website/scripts/check.mjs' else {'website', 'protect'}
        direct = {key for key, row in RELEASE_TARGETS.items() if path.startswith(row['sourceRoot'] + '/')}
        if direct:
            return direct
        if release_control_only_path(path) or path.startswith(('docs/',
            'diagnostics/MerchantPaymentCoreTests/', 'diagnostics/MerchantPaymentAdapterChecks/')) or path in {
            'Legend-Cloudflare/tests/runtime/runtime.test.mjs',
            'scripts/recovery-replacement-admission.py',
            'MASTERAPP.sln', 'MasterApp.slnx', 'scripts/test-migration-probe-package.py',
            'scripts/test-parfait-readonly-workflow.py'}:
            return set()
        raise ValueError('Unmapped fast-track source path: ' + path)
    impacts = {}
    for path in paths:
        selected = owners(path)
        for key, items in linked.items():
            if any(any(match(path, pat) for pat in includes) and not any(match(path, pat) for pat in excludes)
                   for includes, excludes in items):
                selected.add(key)
        impacts[path] = sorted(selected)
    selected = set().union(*map(set, impacts.values())) if impacts else set()
    names = dict(portal='AgentPortal', client='ClientApp', protect='Protect', parfait='Parfait', website='Website')
    matrix = [dict(key=key, target=names[key], project=row['project'], azure_app=row['releaseName'], host=row['host'])
              for key, row in RELEASE_TARGETS.items() if key in selected]
    schema = any(path.startswith('Infrastructure/Migrations/') or path in {
        'Infrastructure/Infrastructure.csproj',
        'Infrastructure/Data/MasterAppDbContext.cs', 'Infrastructure/Data/Configurations/CommercePaymentModel.cs',
        'Infrastructure/Data/Configurations/ReceptionistModelConfiguration.cs',
        'Infrastructure/Data/Configurations/ReceptionistModelConfiguration.Management.cs'} for path in paths)
    backend = schema or bool(shared_server.intersection(paths)) or any(path.startswith(('CommerceCore/', 'Infrastructure/Commerce/', 'Infrastructure/WebsiteRuntime/',
        'Infrastructure/Analytics/', 'Infrastructure/WebsitePublishing/', 'Infrastructure/WebsiteEditing/',
        'Domain/Entities/Commerce', 'Legend-Cloudflare/src/website-routing/')) or path in {
        'Domain/Entities/WebsiteContentState.cs', 'Infrastructure/WebsiteEditing/WebsiteEventMapQuery.cs',
        'Infrastructure/WebsiteEditing/WebsitePlatformController.cs',
        'Infrastructure/Businesses/BusinessWorkspaceAccess.cs', 'ParfaitApp/Program.cs', 'Protect-Website/Program.cs',
        'Protect-Website/ProtectWebsite.csproj'} for path in paths)
    routing = any(p.startswith('Legend-Cloudflare/src/website-routing/') or
                  p == 'scripts/release-router.py' for p in paths)
    # Router-only operations still require authenticated source admission and
    # no-SQL schema readiness. The router job must never be silently skipped.
    return dict(matrix={'include': matrix}, selected=bool(matrix), commerce=backend or routing,
                schema=schema, routing=routing, impacts=impacts)


LIFECYCLE_AUTHORITY_PATHS = (
    "AGENTS.md",
    "DEPLOYMENT.md",
    ".claude/settings.local.json",
    "deploy-portal.sh",
    "AgentPortal/deploy-live-zipdeploy.json",
    ".github/workflows/deployment-diagnostics.yml",
    ".github/workflows/legend-production-readonly-diagnostic.yml",
    ".github/workflows/legend-release-lifecycle.yml",
    ".github/workflows/all-intentional-direct-release-20260918.yml",
    ".github/workflows/masterapp-platform-architecture-validation.yml",
    ".github/workflows/approved-release-security-validation.yml",
    ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
    ".github/workflows/step6-openai-ads-execution-validation.yml",
    ".github/workflows/steps7-8-governed-advertising-validation.yml",
    "scripts/release-lifecycle.py",
    "scripts/release_policy.py",
    "scripts/approved-release-baseline.py",
    "scripts/deploy-approved-app.py",
    "scripts/release-package.py",
    "scripts/validation-resume.py",
    "scripts/test-validation-resume.py",
    "scripts/test-release-policy.py",
    "scripts/test-release-lifecycle.py",
    "scripts/test-deploy-approved-app.py",
    "scripts/deploy-founder-cloudflare.py",
    "scripts/wake-release-lifecycle.py",
)

RELEASE_EXECUTION_CONTROL_INPUTS = (
    "scripts/test-fast-track-release.py",
    "scripts/PackageRestoreProbe/PackageRestoreProbe.csproj",
    "scripts/PackageRestoreProbe/Program.cs",
    "scripts/release-migration-history-audit.py",
    "scripts/test-release-migration-history-audit.py",
    "scripts/test-release-migration-probe-retry.py",
    "scripts/test-release-retired-original-evidence.py",
    "scripts/migration-probe-package.py",
    "scripts/release-prepublication.py",
    "scripts/release-child-receipt.py",
    "scripts/release-router.py",
    "scripts/cloudflare-routing-authority.py",
    "scripts/release-auxiliary.py",
    "scripts/test-release-children.py",
    "scripts/MigrationReleaseProbe/**",
    "scripts/release-migration.py",
    "scripts/test-release-migration.py",
    "scripts/release-workflow.py",
    "scripts/test-release-workflow.py",
    "scripts/release-operation-evidence.py",
    "scripts/test-release-operation-evidence.py",
    "scripts/release-artifacts/**",
)

# Application identity excludes release/test/control-only edits. This authority is
# shared by release baseline resolution and package-canary preservation.
RELEASE_CONTROL_ONLY_EXACT = frozenset({
    "LEGEND-REVENUE-LAUNCH-CHECKPOINT.md",
    "scripts/sync-published-checkout.py",
    "scripts/test-sync-published-checkout.py",
    "scripts/DomainReleaseRefresh/DomainReleaseRefresh.csproj",
    "Docs/releases/branch-lifecycle.md",
    "Docs/releases/recovery-correction-20261008.md",
    "AGENTS.md",
    ".github/CODEOWNERS",
    ".github/copilot-instructions.md",
    "DEPLOYMENT.md",
    ".claude/settings.local.json",
    "deploy-portal.sh",
    "AgentPortal/deploy-live-zipdeploy.json",
    "scripts/approved-release-baseline.py",
    "scripts/release-lifecycle.py",
    "scripts/release_policy.py",
    "scripts/deploy-approved-app.py",
    "scripts/release-package.py",
    "scripts/validation-resume.py",
    "scripts/test-validation-resume.py",
    "scripts/test-fast-track-ready-no-sql.py",
    "scripts/test-release-policy.py",
    "scripts/test-release-lifecycle.py",
    "scripts/test-deploy-approved-app.py",
    "scripts/deploy-founder-cloudflare.py",
    "scripts/wake-release-lifecycle.py",
})

PACKAGE_AUTHORITY_PATHS = frozenset({
    "scripts/release-package.py",
    "scripts/PackageRestoreProbe/PackageRestoreProbe.csproj",
    "scripts/PackageRestoreProbe/Program.cs",
    ".config/dotnet-tools.json",
    ".github/workflows/masterapp-platform-architecture-validation.yml",
})


def release_control_authority_path(path: str) -> bool:
    """One canonical predicate for code that can change validation or publication truth."""
    return (
        path in LIFECYCLE_AUTHORITY_PATHS
        or path in PACKAGE_AUTHORITY_PATHS
        or matches(path, RELEASE_EXECUTION_CONTROL_INPUTS)
    )


PACKAGE_BUILD_WORKFLOW = '.github/workflows/masterapp-platform-architecture-validation.yml'
PACKAGE_COMPONENT_BUILD_STEPS = (
    'Checkout exact package component authority',
    'Setup .NET for canonical package component build',
    'Build immutable validated release package component',
)
PACKAGE_ASSEMBLY_BUILD_STEPS = (
    'Checkout exact package authority',
    'Load immutable package components',
    'Build immutable validated release package',
)


def package_builder_workflow_contract(text: str) -> str:
    """Hash the complete fan-out/fan-in byte-production execution envelope.

    Planning and artifact retention are not byte producers. The isolated component
    matrix and the single assembly barrier are: their job configuration, checkout,
    toolchains, component set, download shape and build commands all remain package
    identity inputs so concurrency cannot create an alternate packaging authority.
    """
    jobs = _job_blocks(text)
    component = jobs.get('validated-release-package-components')
    assembly = jobs.get('validated-release-package')
    if component is None or assembly is None:
        raise ValueError('Canonical package fan-out/fan-in jobs missing')

    def through(block, required):
        steps = named_step_blocks(block)
        if any(name not in steps for name in required):
            raise ValueError('Canonical package builder steps missing')
        lines, spans = _named_step_spans(block)
        end = next(finish for name, start, finish in spans if name == required[-1])
        return ''.join(lines[:end]).rstrip() + '\n'

    header = text.split('\njobs:', 1)[0]
    execution = []
    header_lines = header.splitlines(keepends=True)
    for i, line in enumerate(header_lines):
        if re.match(r'^(env|defaults):', line):
            end = i + 1
            while end < len(header_lines) and (not header_lines[end].strip() or header_lines[end][0].isspace()):
                end += 1
            execution.append(''.join(header_lines[i:end]))
    execution.append(through(component, PACKAGE_COMPONENT_BUILD_STEPS))
    execution.append(through(assembly, PACKAGE_ASSEMBLY_BUILD_STEPS))
    return ''.join(execution)


def release_control_only_path(path: str) -> bool:
    return (
        path.startswith(".github/workflows/")
        or path.startswith(".github/agents/")
        or path.startswith("scripts/MigrationReleaseProbe/")
        or path == "scripts/migration-probe-package.py"
        or path.startswith("Docs/")
        or path.startswith("AgentPortal.Tests/")
        or path.startswith("tests/")
        or path in RELEASE_CONTROL_ONLY_EXACT
        or matches(path, RELEASE_EXECUTION_CONTROL_INPUTS)
    )


def package_canary_input_path(path: str) -> bool:
    return path in PACKAGE_AUTHORITY_PATHS or not release_control_only_path(path)


def package_component_manifest(revision, component):
    """Resolve one package's content closure through the canonical target graph.

    This is evidence identity, not an MSBuild interpreter. Literal references and
    linked inputs are followed across projects; conditions conservatively include
    both branches. Unresolved evaluation disables reuse for this consumer, without
    invalidating independent components. Revision is provenance, not a hash input.
    """
    import posixpath
    import xml.etree.ElementTree as ET
    if component not in RELEASE_TARGETS and component != 'migration':
        raise ValueError('Unknown package component')
    raw = subprocess.check_output(['git', 'ls-tree', '-r', '-z', revision]).decode()
    tree = {}
    for entry in raw.split('\0'):
        if entry:
            meta, path = entry.split('\t', 1)
            mode, kind, oid = meta.split()
            tree[path] = dict(mode=mode, kind=kind, oid=oid)
    inputs, reasons, edges, visited = set(), set(), set(), set()

    def subtree(root):
        inputs.update(path for path in tree if path == root or path.startswith(root + '/'))

    def relative(owner, value):
        value = value.replace('\\', '/')
        if value.startswith('$(MSBuildThisFileDirectory)'):
            value = value.removeprefix('$(MSBuildThisFileDirectory)')
        if any(char in value for char in ('$','%','@')) or value.startswith('/'):
            reasons.add('unresolved_msbuild_input:' + owner)
            return None
        path = posixpath.normpath(posixpath.join(posixpath.dirname(owner), value))
        if path == '..' or path.startswith('../'):
            reasons.add('external_msbuild_input:' + owner)
            return None
        return path

    def document(path, project=False):
        if path in visited: return
        visited.add(path)
        inputs.add(path)
        if path not in tree:
            reasons.add('missing_build_input:' + path)
            return
        if project:
            subtree(posixpath.dirname(path))
        try:
            root = ET.fromstring(git_show_file(revision, path))
        except (ET.ParseError, RuntimeError):
            reasons.add('unreadable_build_input:' + path)
            return
        # These reviewed properties affect compiler/output semantics directly;
        # their values are already hashed in the owning document. Unknown
        # properties may introduce unobserved files through SDK evaluation.
        known_properties = {
            'TargetFramework', 'TargetFrameworks', 'OutputType', 'ImplicitUsings',
            'Nullable', 'ImportProjectExtensionProps', 'UserSecretsId',
            'AddRazorSupportForMvc', 'UseRazorSourceGenerator',
            'EnableDefaultEmbeddedResourceItems', 'DefaultItemExcludes',
            'CopyLocalLockFileAssemblies', 'IsPackable', 'IsTestProject',
            'BaseIntermediateOutputPath', 'BaseOutputPath', 'MSBuildProjectExtensionsPath',
            'MasterAppArtifactsRoot', 'AppendRuntimeIdentifierToOutputPath',
            'AppendTargetFrameworkToOutputPath',
        }
        for group in root.iter():
            if group.tag.rsplit('}', 1)[-1] == 'PropertyGroup':
                if any(node.tag.rsplit('}', 1)[-1] not in known_properties for node in group):
                    reasons.add('unresolved_build_property:' + path)
        for node in root.iter():
            tag = node.tag.rsplit('}', 1)[-1]
            if tag in {'Exec', 'UsingTask'}:
                reasons.add('unresolved_build_task:' + path)
            if tag == 'HintPath' and node.text:
                target = relative(path, node.text.strip())
                if target is not None: inputs.add(target)
            for attribute in ('SourceFiles', 'File', 'AssemblyFile'):
                if node.get(attribute):
                    for value in node.get(attribute).split(';'):
                        target = relative(path, value)
                        if target is not None:
                            inputs.add(target)
                            edges.add((path, target))
            if tag == 'Target':
                for task in node:
                    if task.tag.rsplit('}', 1)[-1] not in {'PropertyGroup', 'ItemGroup'}:
                        reasons.add('unresolved_build_task:' + path)
            if tag == 'Import' and node.get('Sdk'):
                continue  # SDK content is governed by pinned toolchain/workflow.
            field = 'Project' if tag == 'Import' else 'Include'
            value = node.get(field) or node.get('Update')
            if not value or tag == 'PackageReference': continue
            for item in value.split(';'):
                target = relative(path, item)
                if target is None: continue
                edges.add((path, target))
                if tag in {'ProjectReference', 'Import'}:
                    if '*' in target or '?' in target:
                        reasons.add('unresolved_build_reference:' + path)
                    else:
                        document(target, project=tag == 'ProjectReference')
                else:
                    # Keep the pattern edge even when it matches no current file:
                    # discovery of a newly linked input changes the next manifest.
                    inputs.update(name for name in tree if fnmatch.fnmatchcase(name, target))

    row = RELEASE_TARGETS.get(component)
    if row and row['static']:
        subtree(row['sourceRoot'])
        builder = row['sourceRoot'] + '/scripts/build.mjs'
        if builder in tree:
            # JavaScript is executable authority, not a declarative manifest.
            # Collect observed literal inputs for impact diagnostics, but do not
            # infer a complete closure from regexes or run candidate code here.
            reasons.add('unresolved_static_build_inputs:' + builder)
            source = git_show_file(revision, builder)
            for value in re.findall(r"['\"]([^'\"\n]+/[^'\"\n]+)['\"]", source):
                candidates = (value, posixpath.normpath(posixpath.join(posixpath.dirname(builder), value)))
                for target in candidates:
                    if target in tree:
                        inputs.add(target)
                        edges.add((builder, target))
    else:
        document('scripts/MigrationReleaseProbe/MigrationReleaseProbe.csproj' if component == 'migration' else row['project'], project=True)
        # Directory build/package props can be inherited at any project ancestor.
        ancestors_checked = set()
        while visited - ancestors_checked:
            project = sorted(visited - ancestors_checked)[0]
            ancestors_checked.add(project)
            parent = posixpath.dirname(project)
            while True:
                for name in ('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props'):
                    path = posixpath.join(parent, name) if parent else name
                    inputs.add(path)  # Absence is an input too.
                    if path in tree: document(path)
                if not parent: break
                parent = posixpath.dirname(parent)
        inputs.update(('global.json', 'NuGet.Config', 'nuget.config', '.config/dotnet-tools.json'))
    inputs.update(PACKAGE_AUTHORITY_PATHS)
    inputs.add('scripts/validation-resume.py')
    for source in tuple(inputs):
        parent = posixpath.dirname(source)
        while True:
            names = ('.gitattributes', '.npmrc') if row and row['static'] else (
                '.gitattributes', '.editorconfig', '.globalconfig',
                'NuGet.Config', 'NuGet.config', 'nuget.config')
            for name in names:
                inputs.add(posixpath.join(parent, name) if parent else name)
            if not (row and row['static']):
                inputs.update(path for path in tree if posixpath.dirname(path) == parent and path.endswith('.globalconfig'))
            if not parent: break
            parent = posixpath.dirname(parent)
    for path in inputs:
        if path in tree and (tree[path]['kind'] != 'blob' or tree[path]['mode'] not in {'100644', '100755'}):
            reasons.add('nonregular_build_input:' + path)
    records = {path: tree.get(path, {'absent': True}) for path in sorted(inputs)}
    # Parent aggregation is not a child input. Bind the component execution
    # envelope separately, retaining setup/toolchain/build/security semantics.
    workflow = git_show_file(revision, PACKAGE_BUILD_WORKFLOW)
    jobs = _job_blocks(workflow)
    execution = jobs.get('validated-release-package-components')
    if execution is None: raise ValueError('Canonical component builder missing')
    records[PACKAGE_BUILD_WORKFLOW] = {'execution': execution,
        'header': workflow.split('\njobs:', 1)[0]}
    def authority_content(path, excluded=(), selected=None):
        parsed = ast.parse(git_show_file(revision, path))
        nodes = []
        for node in parsed.body:
            name = getattr(node, 'name', None)
            if selected is not None:
                assigned = {target.id for target in getattr(node, 'targets', ()) if isinstance(target, ast.Name)}
                if name not in selected and not assigned.intersection(selected): continue
            elif name in excluded:
                continue  # Only the separate assembly/verification bodies.
            nodes.append(ast.dump(node, include_attributes=False))
        return {'semantics': nodes}
    records['scripts/release-package.py'] = authority_content('scripts/release-package.py',
        excluded=('assemble_components', 'verify_all'))
    records['scripts/validation-resume.py'] = authority_content('scripts/validation-resume.py',
        selected=('package_component_manifest', '_job_blocks', 'RELEASE_TARGETS',
                  'PACKAGE_AUTHORITY_PATHS', 'PACKAGE_BUILD_WORKFLOW'))
    material = dict(schemaVersion=1, component=component, target=row,
                    inputs=records, edges=sorted(edges), uncertainty=sorted(reasons))
    identity = hashlib.sha256(json.dumps(material, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    return dict(material, contentIdentity=identity, producerRevision=revision,
                reusable=not reasons, invalidationReasons=sorted(reasons))

WORKFLOW_PATHS = {
    name: ".github/workflows/" + name
    for name in (
        "masterapp-platform-architecture-validation.yml",
        "step5-isolated-conversion-mapping-validation.yml",
        "step6-openai-ads-execution-validation.yml",
        "steps7-8-governed-advertising-validation.yml",
        "approved-release-security-validation.yml",
        "parfait-isolated-validation.yml",
    )
}

GLOBAL_DOTNET_INPUTS = (
    "MASTERAPP.sln",
    "global.json",
    "NuGet.config",
    "Directory.Build.*",
    "Directory.Packages.*",
    "**/*.csproj",
    "**/*.props",
    "**/*.targets",
)

WEB_DOTNET_SOURCE = (
    "AgentPortal/**",
    "ClientApp/**",
    "Protect-Website/**",
    "ParfaitApp/**",
    "Infrastructure/**",
    "Domain/**",
    "SHARED/**",
)

WEBSITE_SOURCE = (
    "Infrastructure/WebsiteEditing/**",
    "Infrastructure/WebsiteRuntime/**",
    "Infrastructure/Businesses/**",
    "AgentPortal/Controllers/**Website*",
    "AgentPortal/Services/**Website*",
    "AgentPortal/Views/**Website*",
    "ClientApp/Controllers/**Website*",
    "ClientApp/Views/**Website*",
    "Protect-Website/**",
    "SHARED/WebsitePlatform/**",
)

MARKETING_SOURCE = (
    "Infrastructure/Analytics/**",
    "SHARED/Analytics/**",
    "AgentPortal/Controllers/WebsiteAnalyticsController.cs",
    "AgentPortal/Views/WebsiteAnalytics/**",
    "AgentPortal/wwwroot/js/website-analytics.js",
    "AgentPortal/wwwroot/css/website-analytics.css",
)

BOOKING_SOURCE = (
    "Infrastructure/**Booking*",
    "AgentPortal/**Booking*",
    "Protect-Website/**Booking*",
    "Domain/**Booking*",
    "SHARED/**Booking*",
)

CRM_SOURCE = (
    "Infrastructure/Businesses/**",
    "AgentPortal/Controllers/LeadsController.cs",
    "AgentPortal/Controllers/ClientsController.cs",
    "AgentPortal/**CRM*",
    "ClientApp/**CRM*",
    "Domain/**Lead*",
    "Domain/**Client*",
    "SHARED/**Lead*",
    "SHARED/**Client*",
)

DIAGNOSTICS_SOURCE = (
    "AgentPortal/Controllers/FounderDiagnosticsController.cs",
    "AgentPortal/Services/FounderSoftwareRemediationService*.cs",
    "AgentPortal/Services/LegendFounderToolAuthority*.cs",
    "AgentPortal/Services/Engineering/**",
    "Domain/Engineering/**",
    "Infrastructure/Diagnostics/**",
    "SHARED/Diagnostics/**",
    "SHARED/Views/Diagnostics/**",
    "SHARED/wwwroot/js/legend-site-tools.js",
)

DIAGNOSTICS_TESTS = (
    "AgentPortal.Tests/LegendSiteToolBridgeTests.cs",
    "AgentPortal.Tests/FounderRepositoryInspectionTests.cs",
    "AgentPortal.Tests/LegendFounderToolAuthorizationTests.cs",
    "AgentPortal.Tests/FounderSoftwareRepairBatchTests.cs",
    "AgentPortal.Tests/FounderSoftwareRepairCompletionTests.cs",
    "AgentPortal.Tests/FounderRemediationRevocationTests.cs",
    "AgentPortal.Tests/LegendEngineeringControlPlaneTests.cs",
    "AgentPortal.Tests/RuntimeDiagnostics*Tests.cs",
    "AgentPortal.Tests/PageHealthContractTests.cs",
    "AgentPortal.Tests/WebDiagnosticPrivacyTests.cs",
)

WORKFLOWS = {
    "parfait-isolated-validation.yml": {
        "resume_owner": "parfait-isolated-validation",
        "resume_planner_sha256": "e4df879e7250a1bc0a665eff022a154cbed88f05736d2b3f0424f5af90844c75",
        "force_all": (),
        "neutral": ("Docs/**", "docs/**", "*.md", ".github/workflows/**", "scripts/validation-resume.py", "scripts/test-validation-resume.py"),
        "gates": {
            "restore": {"step": "Restore isolated commerce and Parfait projects", "paths": GLOBAL_DOTNET_INPUTS},
            "build": {"step": "Compile shared commerce and Parfait host", "paths": WEB_DOTNET_SOURCE + ("CommerceCore/**",) + GLOBAL_DOTNET_INPUTS, "requires": ("restore",)},
            "test-restore": {"step": "Restore contract tests", "paths": GLOBAL_DOTNET_INPUTS},
            "contracts": {"step": "Run focused Parfait commerce and canonical parity tests", "paths": WEB_DOTNET_SOURCE + ("CommerceCore/**", "AgentPortal.Tests/**") + GLOBAL_DOTNET_INPUTS, "requires": ("test-restore",)},
            "routing": {"step": "Verify selective Cloudflare commerce routing without deployment", "paths": ("Legend-Cloudflare/**", "Legend-Website/**", "Legend-Design/**", "SHARED/WebsitePlatform/**", "tests/website/**", "AgentPortal/wwwroot/js/website-analytics.js", "AgentPortal/wwwroot/js/website-analytics-incidents.js")},
            "payments": {"step": "Verify scoped payment ownership, replay safety and provider contracts", "paths": WEB_DOTNET_SOURCE + ("CommerceCore/**", "diagnostics/MerchantPaymentCoreTests/**", "diagnostics/MerchantPaymentAdapterChecks/**") + GLOBAL_DOTNET_INPUTS},
            "migration-contract": {"step": "Verify canonical migration operation contract", "paths": WEB_DOTNET_SOURCE + GLOBAL_DOTNET_INPUTS + ("scripts/MigrationReleaseProbe/**", "scripts/migration-probe-package.py", "scripts/test-migration-probe-package.py", "scripts/test-parfait-readonly-workflow.py", "scripts/release-migration.py", "scripts/test-release-migration-probe-retry.py", "scripts/test-release-children.py", "scripts/test-release-migration-history-audit.py", "scripts/test-fast-track-release.py", "scripts/release-operation-evidence.py", "scripts/release-router.py", "scripts/deploy-approved-app.py", "scripts/release-migration-history-audit.py", "scripts/validation-resume.py", "scripts/test-validation-resume.py", ".github/workflows/**")},
            "sql-contract": {"step": "Exercise payment migration and schema drift on disposable SQL Server", "paths": WEB_DOTNET_SOURCE + GLOBAL_DOTNET_INPUTS + ("scripts/MigrationReleaseProbe/**", "scripts/migration-probe-package.py")},
            "publish": {"step": "Publish and inspect exact Parfait + Protect packages without deployment", "paths": WEB_DOTNET_SOURCE + GLOBAL_DOTNET_INPUTS + ("CommerceCore/**", "Legend-Website/**", "Legend-Design/**"), "requires": ("restore",)},
        },
    },

    "masterapp-platform-architecture-validation.yml": {
        "force_all": (),
        "neutral": (
            "Docs/**",
            "*.md",
            ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
            ".github/workflows/step6-openai-ads-execution-validation.yml",
            ".github/workflows/steps7-8-governed-advertising-validation.yml",
        ),
        "gates": {
            "lifecycle": {
                "step": "Run branch lifecycle safety contracts",
                "paths": (
                    ".github/workflows/legend-release-lifecycle.yml",
                    ".github/workflows/all-intentional-direct-release-20260918.yml",
                    ".github/workflows/approved-release-security-validation.yml",
                    "scripts/release-lifecycle.py",
                    "scripts/validation-resume.py",
                    "scripts/release_policy.py",
                    "scripts/approved-release-baseline.py",
                    "scripts/deploy-approved-app.py",
                    "scripts/release-package.py",
                    "scripts/test-validation-resume.py",
                    "scripts/test-release-lifecycle.py",
                    "scripts/test-release-policy.py",
                    "scripts/test-deploy-approved-app.py",
                ) + RELEASE_EXECUTION_CONTROL_INPUTS,
            },
            "mobile": {
                "step": "Run authenticated mobile authority tests",
                "paths": (
                    "tests/layout/modal-content-region.test.mjs",
                    "tests/layout/app-navigation-contract.test.mjs",
                ),
            },
            "restore-dotnet": {
                "step": "Restore .NET graph",
                "paths": GLOBAL_DOTNET_INPUTS,
            },
            "build-infrastructure": {
                "step": "Build shared infrastructure",
                "paths": ("Infrastructure/**", "Domain/**", "SHARED/**") + GLOBAL_DOTNET_INPUTS,
                "requires": ("restore-dotnet",),
            },
            "build-hosts": {
                "step": "Build AgentPortal and ClientApp hosts",
                "paths": ("AgentPortal/**", "ClientApp/**", "Infrastructure/**", "Domain/**", "SHARED/**") + GLOBAL_DOTNET_INPUTS,
                "requires": ("restore-dotnet",),
            },
            "build-protect": {
                "step": "Build Protect host",
                "paths": ("Protect-Website/**", "Infrastructure/**", "Domain/**", "SHARED/**") + GLOBAL_DOTNET_INPUTS,
                "requires": ("restore-dotnet",),
            },
            "tracking-assets": {
                "step": "Verify Protect serves the exact shared tracking assets",
                "paths": (
                    "SHARED/WebsitePlatform/tracking.js",
                    "SHARED/WebsitePlatform/meta-signal-intelligence.js",
                    "SHARED/WebsitePlatform/openai-measurement.js",
                ),
                "requires": ("restore-dotnet",),
            },
            "renderer-install": {
                "step": "Install shared website renderer dependencies",
                "paths": ("Legend-Website/package.json", "Legend-Website/package-lock.json"),
            },
            "renderer-build": {
                "step": "Build shared website renderer",
                "paths": ("Legend-Website/**", "Legend-Design/**", "SHARED/WebsitePlatform/**"),
                "requires": ("renderer-install",),
            },
            "renderer-parity": {
                "step": "Verify renderer authority parity",
                "paths": ("Legend-Website/**", "Legend-Design/**", "SHARED/WebsitePlatform/**"),
                "requires": ("renderer-build",),
            },
            "renderer-tests": {
                "step": "Run business renderer tests with approved-baseline no-regression proof",
                "paths": (
                    "Legend-Website/scripts/render-business.test.mjs",
                    "Legend-Website/scripts/render-business.mjs",
                    "Legend-Website/package-lock.json",
                    "SHARED/WebsitePlatform/**",
                    "tests/website/legend-public-cms.test.mjs",
                ),
                "requires": ("renderer-install",),
            },
            "cms-install": {
                "step": "Install canonical shared CMS test dependencies",
                "paths": ("tests/website/package.json", "tests/website/package-lock.json"),
            },
            "cms-tests": {
                "step": "Run canonical shared CMS tests",
                "paths": ("tests/website/**", "SHARED/WebsitePlatform/**", "Legend-Design/**"),
                "requires": ("cms-install",),
            },
            "compile-regression": {
                "step": "Compile full regression test project",
                "paths": WEB_DOTNET_SOURCE + ("AgentPortal.Tests/**",) + GLOBAL_DOTNET_INPUTS,
                "requires": ("restore-dotnet",),
                "runtime_file_dependencies": False,
            },
            "founder-diagnostics-regressions": {
                "step": "Run Founder diagnostics and safe GPT Codex regressions",
                "paths": DIAGNOSTICS_SOURCE + DIAGNOSTICS_TESTS,
                "materializes": ("compile-regression",),
            },
            "domain-release": {
                "step": "Compile shared domain release refresh",
                "paths": ("scripts/DomainReleaseRefresh/**", "Domain/**", "Infrastructure/**") + GLOBAL_DOTNET_INPUTS,
            },
            "website-regressions": {
                "step": "Run website ownership and publishing regressions",
                "paths": (
                    "AgentPortal.Tests/WebsitePublishingAuthorityTests.cs",
                    "AgentPortal.Tests/LegendPublishedPageTests.cs",
                    "AgentPortal.Tests/WebsiteLeadNotificationRecoveryTests.cs",
                    "AgentPortal.Tests/MarketingManagerCentralizationTests.cs",
                    "AgentPortal.Tests/WebsiteContentEditorRoundTripTests.cs",
                    "AgentPortal.Tests/WebsiteEditorTicketAuthorityIsolationTests.cs",
                    "AgentPortal.Tests/WebsiteDomainImportTests.cs",
                    "AgentPortal.Tests/WebsiteInquiryIsolationTests.cs",
                    "AgentPortal.Tests/BusinessWorkspaceTests.cs",
                    "AgentPortal.Tests/BusinessAnalyticsCompletionTests.cs",
                    "AgentPortal.Tests/AnalyticsCanonicalReconciliationTests.cs",
                    "AgentPortal.Tests/AnalyticsPageRoutingTruthTests.cs",
                    "AgentPortal.Tests/WebsiteSiteSourceV3Tests.cs",
                ) + WEBSITE_SOURCE + WEB_DOTNET_SOURCE + GLOBAL_DOTNET_INPUTS,
                "exclude_paths": DIAGNOSTICS_SOURCE,
                "materializes": ("compile-regression",),
            },
            "meta-regressions": {
                "step": "Run Meta authority regressions",
                "paths": (
                    "AgentPortal.Tests/LaunchAuditProjectionTests.cs",
                    "AgentPortal.Tests/Meta*Tests.cs",
                    "AgentPortal.Tests/*Analytics*Tests.cs",
                    "AgentPortal.Tests/Marketing*Tests.cs",
                    "AgentPortal.Tests/ExternalAdsCanonicalIntegrationTests.cs",
                    "AgentPortal.Tests/GoogleConversionReceiptTests.cs",
                    "AgentPortal.Tests/OpenAi*Tests.cs",
                    "AgentPortal.Tests/Tracking*Tests.cs",
                    "AgentPortal.Tests/QuoteProductInstrumentationContractTests.cs",
                    "AgentPortal.Tests/ProtectLeadModalInquiryTests.cs",
                    "AgentPortal.Tests/Receptionist*Tests.cs",
                ) + MARKETING_SOURCE + WEB_DOTNET_SOURCE + GLOBAL_DOTNET_INPUTS,
                "exclude_paths": DIAGNOSTICS_SOURCE,
                "materializes": ("compile-regression",),
            },
            "booking-regressions": {
                "step": "Run booking authority regressions",
                "paths": ("AgentPortal.Tests/*Booking*Tests.cs",) + BOOKING_SOURCE + WEB_DOTNET_SOURCE + GLOBAL_DOTNET_INPUTS,
                "exclude_paths": DIAGNOSTICS_SOURCE,
                "materializes": ("compile-regression",),
            },
            "crm-regressions": {
                "step": "Run CRM outcome regressions",
                "paths": (
                    "AgentPortal.Tests/LeadsControllerTests.cs",
                    "AgentPortal.Tests/ProductionControllerTests.cs",
                    "AgentPortal.Tests/WebsiteAnalyticsScopeTests.cs",
                    "AgentPortal.Tests/LaunchAuditRiskAssessmentTests.cs",
                    "AgentPortal.Tests/CanonicalCrmOutcomeLineageTests.cs",
                ) + CRM_SOURCE + WEB_DOTNET_SOURCE + GLOBAL_DOTNET_INPUTS,
                "exclude_paths": DIAGNOSTICS_SOURCE,
                "materializes": ("compile-regression",),
            },
            "form-tracking": {
                "step": "Run canonical form tracking tests",
                "paths": (
                    "tests/analytics/form-tracker.test.cjs",
                    "SHARED/WebsitePlatform/tracking.js",
                    "SHARED/WebsitePlatform/meta-signal-intelligence.js",
                    "SHARED/WebsitePlatform/openai-measurement.js",
                ),
            },
            "release-web-contracts": {
                "step": "Run release web contract regressions",
                "paths": (
                    "tests/analytics/csv-export.test.cjs",
                    "tests/analytics/form-tracker.test.cjs",
                    "tests/legend-connect/**",
                    "tests/messaging/message-presentation.test.mjs",
                    "tests/layout/page-health.test.mjs",
                    "Legend-Cloudflare/tests/runtime/**",
                    "Legend-Cloudflare/tests/security/**",
                    "scripts/test-diagnostic-project-impact.py",
                    "scripts/test-sync-published-checkout.py",
                ),
                "materializes": ("compile-regression",),
                "consumes": ("domain-release",),
            },
            "release-policy": {
                "step": "Verify consolidated release scope and routing policy",
                "paths": (
                    "scripts/release_policy.py",
                    "scripts/deploy-approved-app.py",
                    "scripts/release-package.py",
                    "scripts/approved-release-baseline.py",
                    "scripts/test-release-policy.py",
                    "scripts/test-deploy-approved-app.py",
                    "Docs/releases/direct-release-request.json",
                ) + RELEASE_EXECUTION_CONTROL_INPUTS,
            },
        },
    },
    "step5-isolated-conversion-mapping-validation.yml": {
        "unmatched_neutral": False,
        "force_all": (),
        "neutral": (
            "Docs/**",
            "*.md",
            ".github/workflows/masterapp-platform-architecture-validation.yml",
            ".github/workflows/step6-openai-ads-execution-validation.yml",
            ".github/workflows/steps7-8-governed-advertising-validation.yml",
            ".github/workflows/all-intentional-direct-release-20260918.yml",
            ".github/workflows/legend-release-lifecycle.yml",
            "scripts/approved-release-baseline.py",
            "scripts/release-lifecycle.py",
            "scripts/release-package.py",
            "scripts/deploy-approved-app.py",
            "scripts/test-release-lifecycle.py",
            "scripts/test-release-policy.py",
            "scripts/test-deploy-approved-app.py",
            "Legend-Cloudflare/tests/**",
            "tests/**",
        ) + RELEASE_EXECUTION_CONTROL_INPUTS,
        "gates": {
            "candidate-restore": {
                "step": "Restore AgentPortal tests",
                "paths": GLOBAL_DOTNET_INPUTS,
                "group": "candidate",
                "runtime": "dotnet",
            },
            "candidate-build": {
                "step": "Build affected test graph",
                "paths": WEB_DOTNET_SOURCE + ("AgentPortal.Tests/**",) + GLOBAL_DOTNET_INPUTS,
                "requires": ("candidate-restore",),
                "group": "candidate",
                "runtime": "dotnet",
            },
            "candidate-focused": {
                "step": "Run Step 5 focused tests",
                "paths": (
                    "AgentPortal.Tests/OpenAiMeasurementDeliveryTests.cs",
                    "AgentPortal.Tests/MarketingScopeParityContractTests.cs",
                ) + MARKETING_SOURCE + GLOBAL_DOTNET_INPUTS,
                "requires": ("candidate-build",),
                "group": "candidate",
                "runtime": "dotnet",
            },
            "candidate-full": {
                "step": "Run full AgentPortal candidate suite",
                "paths": WEB_DOTNET_SOURCE + ("AgentPortal.Tests/**",) + GLOBAL_DOTNET_INPUTS,
                "requires": ("candidate-build",),
                "group": "candidate",
                "runtime": "dotnet",
                "artifact": "candidate-trx",
            },
            "comparison": {
                "step": "Prove Step 5 adds no full-suite failures",
                "paths": (
                    ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
                    "scripts/validation-resume.py",
                    "scripts/test-validation-resume.py",
                    "scripts/test-release-policy.py",
                ),
                "consumes": ("candidate-full",),
                "group": "comparison",
            },
        },
    },
    "step6-openai-ads-execution-validation.yml": {
        "unmatched_neutral": True,
        "force_all": (
            "SHARED/Analytics/OpenAiAdsExecutionContracts.cs",
            "Infrastructure/Analytics/OpenAiAdsExecutionService.cs",
            "Infrastructure/Analytics/MarketingConnectionStore.cs",
        ),
        "neutral": ("scripts/validation-resume.py", "scripts/test-validation-resume.py"),
        "gates": {
            "restore": {"step": "Restore affected graph", "paths": ()},
            "build": {"step": "Build affected graph", "paths": (), "requires": ("restore",)},
            "tests": {
                "step": "Run Step 6 execution and standing scope-parity tests",
                "paths": (
                    "AgentPortal.Tests/OpenAiAdsExecutionServiceTests.cs",
                    "AgentPortal.Tests/OpenAiAdsAccountConnectionAuthorityTests.cs",
                    "AgentPortal.Tests/MarketingDestinationLayerTests.cs",
                    "AgentPortal.Tests/MarketingScopeParityContractTests.cs",
                ),
                "requires": ("build",),
            },
        },
    },
    "approved-release-security-validation.yml": {
        "unmatched_neutral": True,
        "force_all": (),
        "neutral": ("Docs/**", "*.md", "scripts/validation-resume.py", "scripts/test-validation-resume.py"),
        "gates": {
            "restore": {
                "step": "Restore security validation graph",
                "paths": GLOBAL_DOTNET_INPUTS,
            },
            "build": {
                "step": "Build migration validation graph",
                "paths": (
                    "scripts/db.sh",
                    "Infrastructure/Migrations/**",
                    "Infrastructure/Data/MasterAppDbContext.cs",
                    "Infrastructure/**DbContext*.cs",
                    "Domain/Entities/**",
                ) + GLOBAL_DOTNET_INPUTS,
                "requires": ("restore",),
            },
            "db-validation": {
                "step": "Validate database migration artifacts",
                "paths": (
                    "scripts/db.sh",
                    "Infrastructure/Migrations/**",
                    "Infrastructure/Data/MasterAppDbContext.cs",
                    "Infrastructure/**DbContext*.cs",
                    "Domain/Entities/**",
                ) + GLOBAL_DOTNET_INPUTS,
                "requires": ("build",),
            },
            "no-skips": {
                "step": "Reject skipped security tests",
                "paths": (
                    "AgentPortal.Tests/*Antiforgery*.cs",
                    "AgentPortal.Tests/Phase3IdentityAuthorityTests.cs",
                    "AgentPortal.Tests/Phase4PlatformSecurityTests.cs",
                    "AgentPortal.Tests/Phase5CrossPlatformSecurityTests.cs",
                    "AgentPortal.Tests/Phase6ArchitectureInvariantTests.cs",
                    "AgentPortal.Tests/CalendarControllerTests.cs",
                ),
            },
            "vulnerabilities": {
                "step": "Audit dependency vulnerabilities",
                "paths": GLOBAL_DOTNET_INPUTS,
            },
            "secret-scan": {
                "step": "Scan committed configuration for secrets",
                "paths": ("**/appsettings*.json",),
            },
            "composition": {
                "step": "Verify shared composition authorities",
                "paths": (
                    "AgentPortal/Program.cs",
                    "ClientApp/Program.cs",
                    "Protect-Website/Program.cs",
                    "ParfaitApp/Program.cs",
                    "AgentPortal/Services/LegendFounderAiConversationService.cs",
                ),
            },
            "keyring": {
                "step": "Reject inline Azure key-ring wiring",
                "paths": (
                    "AgentPortal/Program.cs",
                    "ClientApp/Program.cs",
                    "Protect-Website/Program.cs",
                    "ParfaitApp/Program.cs",
                ),
            },
            "diff-check": {
                "step": "Verify patch whitespace integrity",
                "paths": ("**",),
            },
        },
    },
    "steps7-8-governed-advertising-validation.yml": {
        "unmatched_neutral": True,
        "force_all": (
            "Domain/Entities/AdvertisingActionAuthorization.cs",
            "SHARED/Analytics/AdvertisingActionContracts.cs",
            "Infrastructure/Analytics/AdvertisingActionAuthorizationService.cs",
            "Infrastructure/Analytics/MarketingConnectionStore.cs",
            "Infrastructure/WebsiteEditing/PromotionOrchestrationService.cs",
            "Infrastructure/WebsiteEditing/WebsitePlatformController.cs",
            "Infrastructure/Data/MasterAppDbContext.cs",
            "Infrastructure/Migrations/20260927053000_AddAdvertisingActionAuthorizations.cs",
            "Infrastructure/Analytics/AdvertisingCommandCenterService.cs",
            "AgentPortal/Controllers/WebsiteAnalyticsController.cs",
            "AgentPortal/Views/WebsiteAnalytics/Index.cshtml",
            "AgentPortal/wwwroot/js/website-analytics.js",
            "AgentPortal/wwwroot/css/website-analytics.css",
            "Infrastructure/Businesses/BusinessWorkspaceControllerBase.cs",
        ),
        "neutral": ("scripts/validation-resume.py", "scripts/test-validation-resume.py"),
        "gates": {
            "restore": {"step": "Restore affected graph", "paths": ()},
            "build": {"step": "Build affected graph", "paths": (), "requires": ("restore",)},
            "governance-tests": {
                "step": "Run Steps 7-8 governance, promotion, execution, and scope-parity tests",
                "paths": (
                    "AgentPortal.Tests/AdvertisingActionAuthorizationServiceTests.cs",
                    "AgentPortal.Tests/PromotionOrchestrationTests.cs",
                    "AgentPortal.Tests/MarketingScopeParityContractTests.cs",
                    "AgentPortal.Tests/AdvertisingCommandCenterCentralizationTests.cs",
                ),
                "requires": ("build",),
            },
            "website-ui-tests": {
                "step": "Run shared website management UI tests",
                "paths": (
                    "Legend-Design/legend-website-management.js",
                    "tests/website/**",
                ),
            },
        },
    },
}



RELEASE_WORKFLOWS = frozenset({
    "all-intentional-direct-release-20260918.yml",
    "legend-release-lifecycle.yml",
})


def _named_step_spans(text: str):
    lines = text.splitlines(keepends=True)
    rows = []
    for index, line in enumerate(lines):
        stripped = line.lstrip()
        if not stripped.startswith("- name:"):
            continue
        indent = len(line) - len(stripped)
        raw = stripped[len("- name:"):].strip()
        name = raw.strip("'\"")
        end = len(lines)
        for cursor in range(index + 1, len(lines)):
            candidate = lines[cursor]
            candidate_stripped = candidate.lstrip()
            candidate_indent = len(candidate) - len(candidate_stripped)
            if candidate_indent == indent and candidate_stripped.startswith("- "):
                end = cursor
                break
        rows.append((name, index, end))
    return lines, rows


def named_step_blocks(text: str):
    lines, rows = _named_step_spans(text)
    return {name: "".join(lines[start:end]) for name, start, end in rows}


def _mask_named_steps(text: str, names):
    lines, rows = _named_step_spans(text)
    wanted = set(names)
    spans = {start: (name, end) for name, start, end in rows if name in wanted}
    output = []
    cursor = 0
    while cursor < len(lines):
        row = spans.get(cursor)
        if row is None:
            output.append(lines[cursor])
            cursor += 1
            continue
        name, end = row
        indent = " " * (len(lines[cursor]) - len(lines[cursor].lstrip()))
        output.append(f"{indent}- name: __LEGEND_GATE__{name}\\n")
        cursor = end
    return "".join(output)


def _job_blocks(text: str):
    """Return exact top-level job blocks without treating blank lines as EOF.

    GitHub workflow jobs routinely contain blank separators. The previous parser
    treated a bare newline as a non-indented top-level key, stopped after the
    first job, and falsely reported later jobs as missing. That silently defeated
    content-addressed Step 5 baseline reuse.
    """
    lines = text.splitlines(keepends=True)
    jobs_line = next((i for i, line in enumerate(lines) if line.strip() == "jobs:" and not line.startswith(" ")), None)
    if jobs_line is None:
        return {}
    blocks = {}
    index = jobs_line + 1
    while index < len(lines):
        line = lines[index]
        if not line.strip():
            index += 1
            continue
        if not line.startswith(" "):
            break
        if line.startswith("  ") and not line.startswith("    ") and line.strip().endswith(":"):
            name = line.strip()[:-1]
            end = index + 1
            while end < len(lines):
                candidate = lines[end]
                if not candidate.strip():
                    end += 1
                    continue
                if not candidate.startswith(" "):
                    break
                if candidate.startswith("  ") and not candidate.startswith("    ") and candidate.strip().endswith(":"):
                    break
                end += 1
            blocks[name] = "".join(lines[index:end])
            index = end
            continue
        index += 1
    return blocks


def git_show_file(revision: str, path: str) -> str:
    result = subprocess.run(
        ["git", "show", f"{revision}:{path}"],
        check=True,
        text=True,
        capture_output=True,
    )
    return result.stdout


def _dynamic_release_policy(step_name: str, block: str) -> str:
    """Classify a newly added release step conservatively without a second registry.

    Explicit policies remain useful documentation for established external-effect
    steps, but correctness never depends on remembering to extend that registry.
    New named steps are discovered from the workflow itself and default to
    fail-closed execution unless their action shape is intrinsically reusable.
    """
    if "actions/upload-artifact@" in block:
        return "artifact_receipt"
    if "actions/download-artifact@" in block:
        return "artifact_restore"
    if "actions/setup-" in block or "azure/login@" in block:
        return "ephemeral_runtime"
    return "fail_closed_execute"


def verify_release_policy_coverage(workflow_name: str, workflow_text: str):
    if workflow_name not in RELEASE_WORKFLOWS:
        raise ValueError(f"Unsupported release workflow coverage: {workflow_name}")
    blocks = named_step_blocks(workflow_text)
    if not blocks:
        raise ValueError(f"Release workflow has no discoverable named steps: {workflow_name}")
    return {
        name: _dynamic_release_policy(name, block)
        for name, block in blocks.items()
    }


def matches(path: str, patterns) -> bool:
    return any(fnmatch.fnmatchcase(path, pattern) for pattern in patterns)


def _control_project_is_application_dependency(path):
    """Resolve reverse project ownership instead of treating every tool as an app."""
    import xml.etree.ElementTree as ET
    wanted = Path(path).resolve()
    pending = [Path(row["project"]) for row in RELEASE_TARGETS.values() if row["project"] != "static"]
    pending.append(Path("AgentPortal.Tests/AgentPortal.Tests.csproj"))
    seen = set()
    while pending:
        project = pending.pop().resolve()
        if project == wanted:
            return True
        if project in seen:
            continue
        seen.add(project)
        if not project.exists():
            # A missing graph is not permission to omit application dependencies.
            return True
        root = ET.parse(project).getroot()
        for node in root.iter():
            if node.tag.rsplit("}", 1)[-1] == "ProjectReference":
                include = node.get("Include", "").replace("\\", "/")
                if not include or "$" in include or "*" in include:
                    return True
                pending.append(project.parent / include)
    return False


def gate_matches(path: str, gate) -> bool:
    patterns = gate.get("paths", ())
    if path.endswith(".csproj") and matches(path, RELEASE_EXECUTION_CONTROL_INPUTS):
        # Explicit control gates still own the utility; broad .NET build inputs
        # include it only when an actual application/test ProjectReference does.
        patterns = tuple(pattern for pattern in patterns if pattern not in GLOBAL_DOTNET_INPUTS)
        if not matches(path, patterns) and not _control_project_is_application_dependency(path):
            return False
    return matches(path, gate.get("paths", ())) and not matches(path, gate.get("exclude_paths", ()))


def git_changed(prior: str, current: str) -> list[str]:
    if prior == current:
        return []
    result = subprocess.run(
        ["git", "diff", "--name-only", prior, current, "--"],
        check=True,
        text=True,
        capture_output=True,
    )
    return [line.strip() for line in result.stdout.splitlines() if line.strip()]


class EvidenceLookupUnavailable(RuntimeError):
    """Evidence transport failed; do not infer absence or invalidate proof."""
    def __init__(self, message="Evidence read unavailable", *, status=None, endpoint=None, rate=None):
        super().__init__(message)
        self.code = status
        self.endpoint = endpoint
        self.rate = dict(rate or {})


_EVIDENCE_BUDGET = contextvars.ContextVar('legend_evidence_budget', default=None)


@contextlib.contextmanager
def evidence_lookup_budget(deadline, *, clock=time.monotonic):
    """One deadline across nested GETs, artifact reads, backoff and polling."""
    parent = _EVIDENCE_BUDGET.get()
    if parent is not None:
        deadline = min(deadline, parent[0])
    token = _EVIDENCE_BUDGET.set((deadline, clock))
    try:
        yield
    finally:
        _EVIDENCE_BUDGET.reset(token)


def evidence_remaining(maximum):
    budget = _EVIDENCE_BUDGET.get()
    if budget is None:
        return maximum
    remaining = budget[0] - budget[1]()
    if remaining <= 0:
        raise EvidenceLookupUnavailable('Evidence lookup deadline exhausted; preserve completed receipts')
    return min(maximum, remaining)


def api_get(repository: str, path: str, token: str):
    return _recovery_proof_read(repository, path, token)


def _api_get_uncached(repository: str, path: str, token: str):
    url = f"https://api.github.com/repos/{repository}/{path.lstrip('/')}"
    request = urllib.request.Request(
        url,
        headers={
            "Accept": "application/vnd.github+json",
            "Authorization": f"Bearer {token}",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "legend-validation-resume/1.0",
        },
    )
    for attempt in range(3):
        try:
            with urllib.request.urlopen(request, timeout=evidence_remaining(30)) as response:
                return json.load(response)
        except (TimeoutError, urllib.error.URLError, http.client.RemoteDisconnected) as exc:
            retryable = not isinstance(exc, urllib.error.HTTPError) or exc.code in {408, 429, 500, 502, 503, 504}
            if not retryable or attempt == 2:
                headers = getattr(exc, "headers", None) or {}
                rate = {key: headers.get(key) for key in (
                    "X-RateLimit-Remaining", "X-RateLimit-Reset", "Retry-After")
                    if headers.get(key) is not None}
                if rate:
                    print(json.dumps({"evidenceRateLimit": rate}, sort_keys=True))
                raise EvidenceLookupUnavailable(
                    "GitHub evidence read unavailable",
                    status=getattr(exc, "code", None),
                    endpoint=path.split("?", 1)[0],
                    rate=rate,
                ) from exc
            time.sleep(evidence_remaining(2 ** attempt))



def assert_protected_release_execution():
    """Require live GitHub evidence that this process belongs to the sole protected release workflow."""
    if os.environ.get("GITHUB_ACTIONS") != "true":
        raise RuntimeError("Production release mutation requires GitHub Actions")
    if os.environ.get("GITHUB_EVENT_NAME") != "workflow_dispatch":
        raise RuntimeError("Production release mutation requires canonical workflow_dispatch")
    expected_ref = "refs/heads/" + TRUSTED_PR_BASE
    if os.environ.get("GITHUB_REF") != expected_ref:
        raise RuntimeError("Production release mutation requires the protected approved branch")
    repository = os.environ.get("GITHUB_REPOSITORY", "")
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN") or ""
    workflow_ref = os.environ.get("GITHUB_WORKFLOW_REF", "")
    sha = os.environ.get("GITHUB_SHA", "")
    run_id_raw = os.environ.get("GITHUB_RUN_ID", "")
    if (
        not repository
        or not token
        or not re.fullmatch(r"[a-f0-9]{40}", sha)
        or not run_id_raw.isdigit()
        or not workflow_ref.endswith(
            f"/.github/workflows/{DIRECT_RELEASE_WORKFLOW}@{expected_ref}"
        )
    ):
        raise RuntimeError("Production release execution identity is incomplete")
    run_id = int(run_id_raw)
    run = api_get(repository, f"actions/runs/{run_id}", token)
    if (
        run.get("id") != run_id
        or run.get("path") != ".github/workflows/" + DIRECT_RELEASE_WORKFLOW
        or run.get("head_branch") != TRUSTED_PR_BASE
        or run.get("event") != "workflow_dispatch"
        or run.get("head_sha") != sha
        or run.get("head_repository", {}).get("full_name", "").lower() != repository.lower()
        or run.get("status") not in {"queued", "in_progress"}
    ):
        raise RuntimeError("Production release mutation is not owned by the protected canonical workflow")
    return run


def _fast_track_policy():
    import importlib.util
    spec = importlib.util.spec_from_file_location('fast_track_release_policy', Path(__file__).with_name('release_policy.py'))
    owner = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(owner)
    return owner


def fast_track_protect_supersession(revision, *, request=None):
    """Founder replacement authority, never evidence of old completion.

    First-parent binding prevents reuse by later releases. The exact validated
    merge owns a new operation; retries retain its immutable write-ahead intent.
    """
    request = _fast_track_policy().read_request(revision) if request is None else request
    record = request.get('protectSupersession')
    if record is None:
        return None
    expected = {'authorizationId': 'ENG-FORMS-RELEASE-MANUAL-HANDOFF-20261023:v1:protect-replacement-20261026',
     'authorizedBy': 'Founder',
     'target': 'protect',
     'originalRun': 40095849719,
     'originalOperationId': '1618e118093e091b4eb6852d90514e0d156d8d53a6513d991ff8e4803d8bf1cd',
     'originalRevision': '7dcfe753f90ee559bc80a5350d6012779cb36426',
     'expectedReleaseParent': '95d534023a59fda7d231fca69cafe84d10038520',
     'historicalOutcome': 'UNKNOWN',
     'authorizeOneReplacement': True,
     'rollbackArtifactId': 12561256924,
     'rollbackArtifactDigest': 'sha256:03321a68f9e5f39ff237c93def0d683b633f938710bf35053c2ade48db0803ca'}
    if (record != expected or request.get('requestedBy') != 'Founder'
        or request.get('releaseMode') != 'approved-only'
        or request.get('cloudflareWebsiteRouting') is not False
        or request.get('preserveLiveTargets') is not False
        or request.get('targets') != ['masterapp-portal', 'masterapp-client', 'masterapp-protect', 'masterapp-parfait', 'masterapp-website']):
        raise ValueError('FAST_TRACK_SUPERSESSION_AUTHORIZATION_CHANGED')
    parents = subprocess.check_output(['git', 'rev-list', '--parents', '-n', '1', revision], text=True).split()
    if (len(parents) != 3 or parents[0] != revision or parents[1] != record['expectedReleaseParent']
        or subprocess.run(['git', 'merge-base', '--is-ancestor', record['originalRevision'], revision], capture_output=True).returncode):
        raise ValueError('FAST_TRACK_SUPERSESSION_CANDIDATE_CHANGED')
    return dict(record, candidateRevision=revision)


def fast_track_selection(before, head):
    """Own promotion admission before inherited application/routing recovery."""
    if not re.fullmatch('[a-f0-9]{40}', head) or not re.fullmatch('[a-f0-9]{40}', before):
        raise ValueError('FAST_TRACK_SELECTION_IDENTITY_UNPROVEN')
    policy = _fast_track_policy()
    promotion = policy.tool_promotion_requested(head)
    transition = None
    if promotion:
        if before == '0' * 40:
            raise ValueError('TRUSTED_TOOL_PROMOTION_BASE_UNPROVEN')
        admission = fast_track_source_admission(allow_nonpublishing=True)
        proof = policy.trusted_tool_promotion(before, head, admission)
        return dict(matrix={'include': []}, selected=False, commerce=False, schema=False,
            routing=False, impacts={}, baseline=before, productionEligible=False, promotion=proof)
    if before != '0' * 40 and policy.tool_promotion_requested(before):
        admission = fast_track_source_admission()
        transition = policy.release_after_tool_promotion(before, head, admission)
    if before == '0' * 40:
        changed = subprocess.check_output(['git', 'diff-tree', '--root', '--no-commit-id', '--name-only', '-r', head], text=True).splitlines()
    else:
        supersession = fast_track_protect_supersession(head, request=policy.read_request(head))
        if supersession is not None:
            if before != supersession['expectedReleaseParent']:
                raise ValueError('FAST_TRACK_SUPERSESSION_PARENT_CHANGED')
            fast_track_source_admission()
            before = supersession['originalRevision']
            print('FAST_TRACK_AUTHORIZED_SUPERSESSION:protect:historical-outcome-UNKNOWN')
        else:
            before = fast_track_effective_baseline(before, head)
        changed = subprocess.check_output(['git', 'diff', '--name-only', before, head], text=True).splitlines()
    # The existing request is an authorization/control input, never an app file.
    scope = fast_track_scope(changed)
    if attested_website_only_recovery_scope(before, head, changed):
        scope = website_only_recovery_scope(scope)
    if fast_track_pending_routing(before):
        scope.update(routing=True, commerce=True)
    if transition is not None:
        expected = [row['azure_app'] for row in scope['matrix']['include']]
        if (len(set(transition['targets'])) != len(transition['targets'])
            or set(transition['targets']) != set(expected)
            or transition['cloudflareWebsiteRouting'] != scope['routing']):
            raise ValueError('TRUSTED_TOOL_PROMOTION_RELEASE_SCOPE_CHANGED')
    return dict(scope, baseline=before, productionEligible=True, promotion=None)


def fast_track_promotion_parent(repository, run, token):
    """A successful no-publish promotion is never a shipped recovery boundary."""
    import tempfile
    revision = run.get('head_sha', '')
    policy = _fast_track_policy()
    if not policy.tool_promotion_requested(revision):
        return None
    if (not is_fast_track_producer(repository, run) or run.get('status') != 'completed'
        or run.get('conclusion') != 'success' or type(run.get('run_attempt')) is not int or run['run_attempt'] < 1):
        raise ValueError('TRUSTED_TOOL_PROMOTION_HISTORY_UNPROVEN')
    request = policy.read_request(revision)
    before = request.get('trustedToolPromotion', {}).get('baseRevision')
    admission = fast_track_merged_source_proof(repository, revision, token)
    expected = policy.trusted_tool_promotion(before, revision, admission)
    name = f"fast-track-admission-{run['id']}-{run['run_attempt']}"
    inventory = api_get(repository, f"actions/runs/{run['id']}/artifacts?per_page=100", token)
    rows = inventory.get('artifacts')
    if not isinstance(rows, list) or inventory.get('total_count') != len(rows):
        raise ValueError('TRUSTED_TOOL_PROMOTION_ARTIFACT_HISTORY_INCOMPLETE')
    selected = [r for r in rows if r.get('name') == name and r.get('expired') is False]
    if (len(selected) != 1 or type(selected[0].get('id')) is not int or selected[0]['id'] < 1
        or selected[0].get('workflow_run', {}).get('id') != run['id']):
        raise ValueError('TRUSTED_TOOL_PROMOTION_RECEIPT_UNPROVEN')
    inventory = api_get(repository, f"actions/runs/{run['id']}/attempts/{run['run_attempt']}/jobs?per_page=100", token)
    jobs = inventory.get('jobs')
    if not isinstance(jobs, list) or inventory.get('total_count') != len(jobs):
        raise ValueError('TRUSTED_TOOL_PROMOTION_JOBS_INCOMPLETE')
    if any(j.get('run_id') != run['id'] or j.get('run_attempt') != run['run_attempt']
           or j.get('head_sha') != revision for j in jobs):
        raise ValueError('TRUSTED_TOOL_PROMOTION_JOB_IDENTITY_CHANGED')
    workflow = _release_history_source(repository, revision, '.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW, token)
    if FAST_TRACK_RECOVERY_JOB_ID in _job_blocks(workflow):
        recovery = [job for job in jobs if job.get('name') == FAST_TRACK_RECOVERY_JOB]
        if (len(recovery) != 1 or recovery[0].get('status') != 'completed'
            or recovery[0].get('conclusion') != 'skipped' or recovery[0].get('steps') != []):
            raise ValueError('TRUSTED_TOOL_PROMOTION_RECOVERY_NONENTRY_UNPROVEN')
        jobs = [job for job in jobs if job is not recovery[0]]
    selectors = [j for j in jobs if j.get('name') == 'Select exact affected applications']
    names = {'Admit exact validated fast-track source', 'Reconcile production schema before publication',
             'Publish verified website routing'}
    downstream = [j for j in jobs if j.get('name') in names or j.get('name', '').startswith('Affected App Deploy')]
    if (len(selectors) != 1 or selectors[0].get('conclusion') != 'success'
        or len(downstream) < 4 or {j.get('name') for j in downstream} & names != names
        or any(j.get('conclusion') != 'skipped' for j in downstream)
        or len(jobs) != 1 + len(downstream)):
        raise ValueError('TRUSTED_TOOL_PROMOTION_NONENTRY_UNPROVEN')
    if _RECOVERY_RECEIPT_BYTES.get() is not None:
        value = _release_history_json(repository, run['id'], selected[0],
            'fast-track-admission.json', verify_digest=True, max_bytes=4 * 1024 * 1024)
    else:
        with tempfile.TemporaryDirectory(prefix='legend-tool-promotion-') as temporary:
            root = Path(temporary)
            _download_run_artifact(repository, run['id'], name, root, artifact_id=selected[0]['id'])
            receipt = root / 'fast-track-admission.json'
            if receipt.is_symlink() or receipt.stat().st_size > 4 * 1024 * 1024:
                raise ValueError('TRUSTED_TOOL_PROMOTION_RECEIPT_UNPROVEN')
            value = json.loads(receipt.read_text())
    if value != dict(expected, producingRun=run['id'], producingAttempt=run['run_attempt']):
        raise ValueError('TRUSTED_TOOL_PROMOTION_RECEIPT_IDENTITY_CHANGED')
    return before


def _live_identity_diagnostic(reason, *, run=None, expected_job=None, **details):
    # Log classifications, never the environment, provider payload or credentials.
    print('LEGEND_LIVE_IDENTITY:' + json.dumps(dict(reason=reason,
        status=(run or {}).get('status'), attempt=(run or {}).get('run_attempt'),
        job=os.environ.get('GITHUB_JOB'), target=os.environ.get('TARGET_KEY'),
        expectedJob=expected_job, **details), sort_keys=True), flush=True)

def _exact_active_job(repository, run, token, expected_job, *, error=RuntimeError,
                      code='FAST_TRACK_ACTIVE_JOB_UNPROVEN'):
    """Return positive current-attempt ownership or a specifically pending state."""
    runner = os.environ.get('RUNNER_NAME', '')
    if not runner or not expected_job:
        raise error(code + ':job_identity_missing')
    payload = api_get(repository,
        f"actions/runs/{run['id']}/attempts/{run['run_attempt']}/jobs?per_page=100", token)
    jobs = payload.get('jobs')
    complete = (isinstance(jobs, list) and type(payload.get('total_count')) is int
        and payload['total_count'] == len(jobs))
    if (not complete or not all(isinstance(job, dict) for job in jobs)
        or any(type(job.get('id')) is not int or job['id'] < 1
            or not isinstance(job.get('name'), str) or not job['name']
            or job.get('status') not in {'queued', 'in_progress', 'completed'}
            for job in jobs)
        or len({job['id'] for job in jobs}) != len(jobs)):
        _live_identity_diagnostic('inventory_malformed', run=run, expected_job=expected_job,
            inventoryComplete=complete, inventoryCount=len(jobs) if isinstance(jobs, list) else None)
        raise error(code + ':inventory_malformed')
    for job in jobs:
        mismatch = [key for key, value in dict(run_id=run['id'],
            run_attempt=run['run_attempt'], head_sha=run['head_sha']).items() if job.get(key) != value]
        if mismatch:
            _live_identity_diagnostic('job_identity_mismatch', run=run,
                expected_job=expected_job, fields=mismatch)
            raise error(code + ':job_identity_mismatch')
    candidates = [job for job in jobs if job['name'] == expected_job]
    _live_identity_diagnostic('job_inventory', run=run, expected_job=expected_job,
        inventoryComplete=True, inventoryCount=len(jobs), candidateCount=len(candidates),
        candidateStatus=candidates[0]['status'] if len(candidates) == 1 else None,
        runnerAssigned=bool(candidates[0].get('runner_name')) if len(candidates) == 1 else None)
    if len(candidates) > 1:
        raise error(code + ':ambiguous_job')
    if not candidates:
        return None
    job = candidates[0]
    if job['status'] == 'completed':
        raise error(code + ':terminal_job')
    if job.get('runner_name') not in {None, '', runner}:
        raise error(code + ':runner_mismatch')
    if job.get('conclusion') is not None:
        raise error(code + ':contradictory_job_state')
    if job['status'] == 'queued' or not job.get('runner_name'):
        return None
    return job

def _observe_live_workflow(repository, expected, token, *, queued_job,
                           error=RuntimeError, code='FAST_TRACK_EXECUTION_PRODUCER_UNPROVEN',
                           job_code='FAST_TRACK_ACTIVE_JOB_UNPROVEN', require_job=False,
                           clock=time.monotonic, sleep=time.sleep):
    """Bound only pending lifecycle metadata; immutable disagreement is terminal."""
    deadline = clock() + 30
    with evidence_lookup_budget(deadline, clock=clock):
        for observation in range(7):
            if observation and clock() >= deadline:
                break
            run = api_get(repository, f"actions/runs/{expected['id']}", token)
            mismatch = [key for key, value in expected.items() if run.get(key) != value]
            if run.get('head_repository', {}).get('full_name', '').lower() != repository.lower():
                mismatch.append('head_repository')
            if (mismatch or run.get('status') not in {'in_progress', 'queued'}
                or run.get('conclusion') is not None):
                _live_identity_diagnostic('producer_mismatch', run=run, fields=mismatch,
                    observation=observation + 1)
                raise error(code)
            job = None
            if require_job or run['status'] == 'queued':
                name = queued_job()
                job = _exact_active_job(repository, run, token, name, error=error, code=job_code)
                if job is None:
                    _live_identity_diagnostic('pending', run=run, expected_job=name,
                        observation=observation + 1)
                    if observation < 6 and clock() < deadline:
                        sleep(evidence_remaining(min(5, deadline - clock())))
                        continue
                    break
            evidence_remaining(1)
            _live_identity_diagnostic('proven', run=run, observation=observation + 1)
            return run, job
    _live_identity_diagnostic('deadline_exhausted', run=run)
    raise error(job_code + ':pending_deadline_exhausted')

def _source_validation_job_name(repository, revision, workflow, token):
    key = os.environ.get('GITHUB_JOB', '')
    source = _release_history_source(repository, revision, '.github/workflows/' + workflow, token)
    jobs = _job_blocks(source)
    owners = [name for name, block in jobs.items()
              if 'Plan exact validation resume' in named_step_blocks(block)]
    if (not key or owners != [key] or key not in jobs
        or len(re.findall(r'^  ' + re.escape(key) + r':\s*$', source, re.M)) != 1):
        raise ValueError('FAST_TRACK_SOURCE_VALIDATION_ACTIVE_JOB_UNPROVEN:planner_owner')
    names = re.findall(r'^    name:\s*(.*?)\s*$', jobs[key], re.M)
    if len(names) > 1:
        raise ValueError('FAST_TRACK_SOURCE_VALIDATION_ACTIVE_JOB_UNPROVEN:ambiguous_name')
    name = names[0] if names else key
    if len(name) >= 2 and name[0] == name[-1] and name[0] in {'"', "'"}:
        name = name[1:-1]
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9 _—./:-]{0,199}', name):
        raise ValueError('FAST_TRACK_SOURCE_VALIDATION_ACTIVE_JOB_UNPROVEN:dynamic_name')
    return name


def _fast_track_job_name(env):
    names = {'select-targets': 'Select exact affected applications',
        'commerce-gates': 'Admit exact validated fast-track source',
        'migrations': 'Reconcile production schema before publication',
        'routing-cloudflare': 'Publish verified website routing'}
    if env.get('GITHUB_JOB') == 'deploy-ui':
        labels = {'portal': 'AgentPortal', 'client': 'ClientApp', 'protect': 'Protect',
                  'parfait': 'Parfait', 'website': 'Website'}
        target = env.get('TARGET_KEY')
        if target not in labels:
            raise RuntimeError('FAST_TRACK_ACTIVE_JOB_TARGET_UNPROVEN')
        return 'Affected App Deploy — ' + labels[target]
    if env.get('GITHUB_JOB') not in names:
        raise RuntimeError('FAST_TRACK_ACTIVE_JOB_UNPROVEN')
    return names[env['GITHUB_JOB']]


SELECTOR_VISIBILITY_WAIT_SECONDS = 300
SELECTOR_VISIBILITY_POLL_SECONDS = 5
SELECTOR_VISIBILITY_MAX_OBSERVATIONS = 61


def _await_selector_visibility(repository, expected, token, name, *, clock, sleep):
    """Wait only for selector metadata; these observations confer no admission.

    This read-only phase adds at most 300 seconds of bounded waiting, subject
    to any shorter enclosing evidence budget. The caller must then collect a
    new full identity proof within its existing independent 30-second budget.
    """
    started = clock()
    deadline = started + SELECTOR_VISIBILITY_WAIT_SECONDS
    with evidence_lookup_budget(deadline, clock=clock):
        for observation in range(SELECTOR_VISIBILITY_MAX_OBSERVATIONS):
            if clock() >= deadline:
                break
            evidence_remaining(1)
            run = api_get(repository, f"actions/runs/{expected['id']}", token)
            if (run.get('status') not in {'in_progress', 'queued'} or run.get('conclusion') is not None
                or any(run.get(key) != value for key, value in expected.items())
                or run.get('head_repository', {}).get('full_name', '').lower() != repository.lower()):
                raise RuntimeError('FAST_TRACK_EXECUTION_PRODUCER_UNPROVEN')
            evidence_remaining(1)
            job = _exact_active_job(repository, run, token, name)
            evidence_remaining(1)
            _live_identity_diagnostic('selector_visibility_ready' if job is not None else 'selector_visibility_pending',
                run=run, expected_job=name, observation=observation + 1,
                elapsedSeconds=round(clock() - started, 3))
            if job is not None:
                # Deliberately return no run/job evidence for reuse by admission.
                return
            if observation + 1 < SELECTOR_VISIBILITY_MAX_OBSERVATIONS and clock() < deadline:
                sleep(evidence_remaining(min(SELECTOR_VISIBILITY_POLL_SECONDS, deadline - clock())))
    raise RuntimeError('FAST_TRACK_SELECTOR_VISIBILITY_UNPROVEN:pending_deadline_exhausted')


def assert_fast_track_release_execution(*, allow_nonpublishing=False, target=None, clock=time.monotonic, sleep=time.sleep):
    """One bounded authenticated observation; never a retry of an operation.

    Run identity, exact job/target ownership and current HEAD must all agree.
    Only absent/pending live-job observation may settle within this one budget.
    Terminal, conflicting and malformed identities fail closed immediately.
    """
    env = os.environ
    repository = env.get('GITHUB_REPOSITORY', '')
    token = env.get('GH_TOKEN') or env.get('GITHUB_TOKEN')
    revision = env.get('GITHUB_SHA', '')
    reference = 'refs/heads/' + FAST_TRACK_PR_BASE
    run_id, attempt = env.get('GITHUB_RUN_ID', ''), env.get('GITHUB_RUN_ATTEMPT', '')
    if (env.get('GITHUB_ACTIONS') != 'true' or env.get('GITHUB_EVENT_NAME') != 'push'
        or env.get('GITHUB_REF') != reference or not repository or not token
        or not re.fullmatch('[a-f0-9]{40}', revision) or not run_id.isdigit() or int(run_id) < 1
        or not attempt.isdigit() or int(attempt) < 1
        or env.get('GITHUB_WORKFLOW_REF') != repository + '/.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW + '@' + reference):
        raise RuntimeError('FAST_TRACK_EXECUTION_IDENTITY_INCOMPLETE')
    if target is not None and (target not in RELEASE_TARGETS or env.get('GITHUB_JOB') != 'deploy-ui' or env.get('TARGET_KEY') != target):
        raise RuntimeError('FAST_TRACK_ACTIVE_JOB_TARGET_UNPROVEN')
    name, runner = _fast_track_job_name(env), env.get('RUNNER_NAME', '')
    if not runner:
        raise RuntimeError('FAST_TRACK_ACTIVE_JOB_UNPROVEN')
    if not allow_nonpublishing and _fast_track_policy().tool_promotion_requested(revision):
        raise RuntimeError('FAST_TRACK_TRUSTED_TOOL_PROMOTION_NONPUBLISHING')
    expected = dict(id=int(run_id), run_attempt=int(attempt), path='.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW,
                    head_branch=FAST_TRACK_PR_BASE, event='push', head_sha=revision)
    selector = env.get('GITHUB_JOB') == 'select-targets'
    if selector:
        _await_selector_visibility(repository, expected, token, name, clock=clock, sleep=sleep)
    # No pending-phase evidence is reused: start the unchanged fresh proof.
    deadline = clock() + 30
    with evidence_lookup_budget(deadline, clock=clock):
        for observation in range(16):
            run = api_get(repository, f'actions/runs/{run_id}', token)
            if (run.get('status') not in {'in_progress', 'queued'} or run.get('conclusion') is not None
                or any(run.get(key) != value for key, value in expected.items())
                or run.get('head_repository', {}).get('full_name', '').lower() != repository.lower()):
                raise RuntimeError('FAST_TRACK_EXECUTION_PRODUCER_UNPROVEN')
            job = _exact_active_job(repository, run, token, name)
            owners = [job] if job is not None else []
            evidence_remaining(1)
            # A pending selector has no ownership proof; only its final positive
            # observation needs the mandatory fresh branch-HEAD check. Other
            # callers retain their existing HEAD observation behavior.
            if owners or not selector:
                current = api_get(repository, 'git/ref/heads/' + FAST_TRACK_PR_BASE, token)
                if current.get('object', {}).get('sha') != revision:
                    raise RuntimeError('FAST_TRACK_RELEASE_HEAD_CHANGED')
            # Socket timeouts do not bound a slow response body. Never accept
            # identity after the shared observation deadline has elapsed.
            evidence_remaining(1)
            if owners and owners[0].get('status') == 'in_progress':
                return run
            if observation == 15 or clock() >= deadline:
                break
            sleep(evidence_remaining(2))
    raise RuntimeError('FAST_TRACK_ACTIVE_JOB_UNPROVEN')


def assert_release_execution():
    """Select a configured producer; each lane retains its own exact identity checks."""
    if os.environ.get('GITHUB_REF') == 'refs/heads/' + FAST_TRACK_PR_BASE:
        return assert_fast_track_release_execution()
    return assert_protected_release_execution()


def fast_track_validation_receipt(repository, run, workflow, candidate, pr, token):
    """Use retained source scope: GitHub removes PR links after a PR is merged."""
    import tempfile
    prefixes = {'approved-release-security-validation.yml': 'security',
        'parfait-isolated-validation.yml': 'parfait',
        'masterapp-platform-architecture-validation.yml': 'architecture'}
    if workflow not in prefixes:
        raise RuntimeError('FAST_TRACK_VALIDATION_WORKFLOW_UNSUPPORTED')
    if run.get('event') != 'pull_request':
        raise RuntimeError('FAST_TRACK_VALIDATION_PR_EVENT_REQUIRED')
    prefix = prefixes[workflow]
    name = f"validation-resume-{prefix}-{run['id']}-{run['run_attempt']}"
    inventory = api_get(repository, f"actions/runs/{run['id']}/artifacts?per_page=100", token)
    rows = inventory.get('artifacts')
    if not isinstance(rows, list) or inventory.get('total_count') != len(rows):
        raise RuntimeError('FAST_TRACK_VALIDATION_ARTIFACT_INVENTORY_INCOMPLETE')
    artifacts = [a for a in rows if a.get('name') == name and a.get('expired') is False]
    if len(artifacts) != 1 or artifacts[0].get('workflow_run', {}).get('id') != run['id']:
        raise RuntimeError('FAST_TRACK_VALIDATION_RECEIPT_MISSING')
    if _RECOVERY_RECEIPT_BYTES.get() is not None:
        receipt = _release_history_json(repository, run['id'], artifacts[0], 'validation-resume.json',
                                        verify_digest=True, max_bytes=32 * 1024 * 1024)
    else:
        with tempfile.TemporaryDirectory(prefix='fast-track-source-') as tmp:
            _download_run_artifact(repository, run['id'], name, Path(tmp), artifact_id=artifacts[0]['id'])
            path = Path(tmp) / 'validation-resume.json'
            if path.stat().st_size > 32 * 1024 * 1024:
                raise RuntimeError('FAST_TRACK_VALIDATION_RECEIPT_OVERSIZED')
            receipt = json.loads(path.read_text())
    scope = receipt.get('sourceValidationScope', {})
    if (not isinstance(scope, dict) or 'developmentValidationScope' in receipt
        or receipt.get('workflow') != workflow or receipt.get('currentSha') != candidate
        or receipt.get('validationBase') != FAST_TRACK_PR_BASE
        or scope.get('phase') != 'source-validation-only' or scope.get('releaseAdmission') is not False
        or scope.get('baseBranch') != FAST_TRACK_PR_BASE or scope.get('candidate') != candidate
        or scope.get('pullRequest') != pr):
        raise RuntimeError('FAST_TRACK_VALIDATION_SOURCE_SCOPE_UNPROVEN')
    return artifacts[0]['id']


def fast_track_skipped_matrix(jobs):
    """GitHub represents a wholly skipped matrix by its unexpanded job name."""
    owners = [j for j in jobs if j.get('name', '').startswith('Affected App Deploy — ')]
    return (len(owners) == 1 and owners[0].get('name') == 'Affected App Deploy — ${{ matrix.target }}'
        and owners[0].get('status') == 'completed' and owners[0].get('conclusion') == 'skipped'
        and owners[0].get('steps') == [])


def fast_track_completed_sql_unpublished(repository, run, latest_jobs, token):
    """Carry app scope only; completed SQL remains owned by its durable journal."""
    if (run.get('status') != 'completed' or run.get('conclusion') != 'failure'
        or type(run.get('run_attempt')) is not int or not 1 <= run['run_attempt'] <= 100):
        return False
    migrations = [j for j in latest_jobs if j.get('name') == FAST_TRACK_CHILD_STEPS['migrations'][0]]
    if len(migrations) != 1 or migrations[0].get('conclusion') != 'success':
        return False
    source = _release_history_source(repository, run['head_sha'], '.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW, token)
    if hashlib.sha256(source.encode()).hexdigest() != '5ab1bab0315385c56838c2e0ffdc7cb78da00a34b734e716f25d0f26e5de6f51':
        return False
    expected = {'Affected App Deploy — ' + name for name in ('AgentPortal','ClientApp','Protect','Parfait','Website')}
    for attempt in range(1, run['run_attempt'] + 1):
        payload = api_get(repository, f"actions/runs/{run['id']}/attempts/{attempt}/jobs?per_page=100", token)
        jobs = payload.get('jobs')
        if not isinstance(jobs, list) or payload.get('total_count') != len(jobs):
            return False
        routing = [j for j in jobs if j.get('name') == 'Publish verified website routing']
        if len(routing) != 1 or routing[0].get('status') != 'completed' or routing[0].get('conclusion') != 'skipped':
            return False
        if fast_track_skipped_matrix(jobs):
            continue
        owners = [j for j in jobs if j.get('name', '').startswith('Affected App Deploy — ')]
        if len(owners) != 5 or {j.get('name') for j in owners} != expected:
            return False
        for job in owners:
            steps = [s for s in job.get('steps', []) if s.get('name') == 'Publish or reconcile exact application bytes']
            if (job.get('status') != 'completed' or len(steps) != 1
                or steps[0].get('status') != 'completed' or steps[0].get('conclusion') != 'skipped'):
                return False
    inventory = api_get(repository, f"actions/runs/{run['id']}/artifacts?per_page=100", token)
    artifacts = inventory.get('artifacts')
    if not isinstance(artifacts, list) or inventory.get('total_count') != len(artifacts):
        return False
    intents = [a for a in artifacts if a.get('name', '').startswith('legend-release-child-intent-')]
    if len(intents) != 1 or intents[0].get('expired') is not False:
        return False
    identity = intents[0]['name'].removeprefix('legend-release-child-intent-')
    intent = release_child_history(repository, 'migrations', identity, token, 'intent')
    success = release_child_history(repository, 'migrations', identity, token, 'success')
    keys = ('producingRun','dependencyIdentity','materialIdentity','executionAuthority','applicationRevision','partitionIdentity')
    return bool(intent and success and intent.get('producingRun') == run['id']
        and re.fullmatch('[a-f0-9]{64}', str(intent.get('partitionIdentity', '')))
        and success.get('producingAttempt') == migrations[0].get('run_attempt')
        and type(intent.get('producingAttempt')) is int
        and 1 <= intent['producingAttempt'] <= success['producingAttempt']
        and all(intent.get(key) == success.get(key) for key in keys))


def fast_track_untriggered_repair_parent(repository, revision, token):
    """Recover scope across a merged control-only repair excluded by old paths."""
    parents = subprocess.check_output(['git', 'rev-list', '--parents', '-n', '1', revision], text=True).split()
    if len(parents) != 3:
        return None
    paths = subprocess.check_output(['git', 'diff', '--name-only', parents[1], revision], text=True).splitlines()
    allowed = {'scripts/validation-resume.py', 'scripts/deploy-approved-app.py',
        'scripts/migration-probe-package.py', 'scripts/test-fast-track-release.py',
        'scripts/test-migration-probe-package.py',
        'docs/parfait-canonical-transition/canonical-internal-ownership-and-cutover.md'}
    if not paths or not set(paths) <= allowed:
        return None
    source = _release_history_source(repository, revision, '.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW, token)
    if hashlib.sha256(source.encode()).hexdigest() != '5ab1bab0315385c56838c2e0ffdc7cb78da00a34b734e716f25d0f26e5de6f51':
        return None
    pulls = api_get(repository, f'commits/{revision}/pulls?per_page=100', token)
    merged = [p for p in pulls if p.get('merged_at') and p.get('merge_commit_sha') == revision
        and p.get('base', {}).get('ref') == FAST_TRACK_PR_BASE
        and p.get('head', {}).get('repo', {}).get('full_name') == repository]
    if len(merged) != 1:
        raise RuntimeError('FAST_TRACK_UNTRIGGERED_REPAIR_MERGE_UNPROVEN')
    return parents[1]


def fast_track_completed_apps_pending_routing(repository, run, jobs, token):
    """Keep completed app scope while retaining a proven unentered router job."""
    expected = {'Affected App Deploy — ' + label for label in ('AgentPortal','ClientApp','Protect','Parfait','Website')}
    apps = [j for j in jobs if j.get('name', '').startswith('Affected App Deploy — ')]
    prerequisites = [j for j in jobs if j.get('name') in {
        'Admit exact validated fast-track source','Reconcile production schema before publication'}]
    # The authenticated workflow can enter routing only after its entire selected
    # app matrix succeeds. Later package repairs may select fewer than five apps.
    # Require every visible app to be canonical, unique and successful; the
    # source-bound entered routing step proves no selected sibling is missing.
    completed_apps = (apps and len({j.get('name') for j in apps}) == len(apps)
        and {j.get('name') for j in apps} <= expected
        and all(j.get('status') == 'completed' and j.get('conclusion') == 'success' for j in apps))
    if not ((completed_apps or fast_track_skipped_matrix(jobs)) and len(prerequisites) == 2
        and all(j.get('status') == 'completed' and j.get('conclusion') == 'success' for j in prerequisites)
        and attested_fast_track_router_prewrite(repository, run, jobs, token)):
        return False
    if type(run.get('run_attempt')) is not int or not 1 <= run['run_attempt'] <= 100:
        return False
    for attempt in range(1, run['run_attempt'] + 1):
        payload = api_get(repository, f"actions/runs/{run['id']}/attempts/{attempt}/jobs?per_page=100", token)
        rows = payload.get('jobs')
        if not isinstance(rows, list) or payload.get('total_count') != len(rows):
            return False
        owners = [j for j in rows if j.get('name') == FAST_TRACK_CHILD_STEPS['routing-cloudflare'][0]]
        if len(owners) != 1:
            return False
        if owners[0].get('status') == 'completed' and owners[0].get('conclusion') == 'skipped':
            continue
        if not attested_fast_track_router_prewrite(repository, run, rows, token):
            return False
    return True


def fast_track_historical_producers(repository, revision, token, *, clock=time.monotonic):
    """Resolve an exact head only within the complete canonical branch history."""
    if not isinstance(revision, str) or not re.fullmatch('[a-f0-9]{40}', revision):
        raise ValueError('FAST_TRACK_HISTORICAL_REVISION_UNPROVEN')
    with evidence_lookup_budget(clock() + 30, clock=clock):
        producers = [row for row in fast_track_execution_history(repository, token, clock=clock)
                     if row['head_sha'] == revision]
        if len(producers) > 1:
            raise ValueError('FAST_TRACK_HISTORICAL_PRODUCER_AMBIGUOUS')
        evidence_remaining(30)
        return producers


def fast_track_pending_routing(revision):
    repository = os.environ['GITHUB_REPOSITORY']
    token = os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
    runs = fast_track_historical_producers(repository, revision, token)
    if len(runs) != 1 or runs[0].get('conclusion') != 'failure':
        return False
    payload = api_get(repository, f"actions/runs/{runs[0]['id']}/jobs?filter=latest&per_page=100", token)
    jobs = payload.get('jobs')
    if not isinstance(jobs, list) or payload.get('total_count') != len(jobs):
        raise RuntimeError('FAST_TRACK_ROUTING_JOBS_INCOMPLETE')
    return fast_track_completed_apps_pending_routing(repository, runs[0], jobs, token)


def attested_fast_track_two_attempt_no_sql_preflight(repository, run, token):
    """Authenticate one pinned two-attempt preflight-only failure; never permit SQL replay."""
    source = '910f93fc6bd247f826fffb9d9701676cf4d33112'
    if (run.get('id') != 38178839491 or run.get('head_sha') != source
        or run.get('run_attempt') != 2 or run.get('status') != 'completed'
        or run.get('conclusion') != 'failure'
        or not is_fast_track_producer(repository, run)):
        return False
    names = {'Select exact affected applications',
             'Admit exact validated fast-track source',
             'Reconcile production schema before publication',
             'Affected App Deploy — ${{ matrix.target }}',
             'Publish verified website routing'}
    retained = {'fast-track-admission-38178839491-1': 11716507721,
                'fast-track-database-recovery-38178839491-1': 11716567678,
                'fast-track-database-recovery-38178839491-2': 11716923766}
    try:
        workflow = _release_history_source(repository, source,
            '.github/workflows/legend-fast-track-ui.yml', token)
        if not all(name in workflow for name in (
            'Check migration admission without entering a write step',
            'Apply or reconcile the exact rehearsed migration bundle',
            'Reconcile fresh already-ready schema without SQL bundle or mutation')):
            return False
        response = api_get(repository, 'actions/runs/38178839491/artifacts?per_page=100', token)
        artifacts = response.get('artifacts')
        if (not isinstance(artifacts, list) or response.get('total_count') != 3
            or len(artifacts) != 3
            or {a.get('name'): a.get('id') for a in artifacts if isinstance(a, dict)} != retained
            or any(a.get('expired') is not False or
                   a.get('workflow_run', {}).get('id') != run['id'] for a in artifacts)):
            return False
        for attempt in (1, 2):
            payload = api_get(repository,
                f'actions/runs/{run["id"]}/attempts/{attempt}/jobs?per_page=100', token)
            jobs = payload.get('jobs')
            if not isinstance(jobs, list) or payload.get('total_count') != 5 or len(jobs) != 5:
                return False
            indexed = {j.get('name'): j for j in jobs if isinstance(j, dict)}
            if set(indexed) != names or any(j.get('run_attempt') != attempt or
                j.get('status') != 'completed' for j in jobs):
                return False
            for job_name in ('Select exact affected applications',
                             'Admit exact validated fast-track source'):
                if indexed[job_name].get('conclusion') != 'success':
                    return False
            for job_name in ('Affected App Deploy — ${{ matrix.target }}',
                             'Publish verified website routing'):
                if indexed[job_name].get('conclusion') != 'skipped':
                    return False
            owner = indexed['Reconcile production schema before publication']
            if owner.get('conclusion') != 'failure' or type(owner.get('id')) is not int:
                return False
            steps = owner.get('steps')
            if not isinstance(steps, list):
                return False
            keyed = {step.get('name'): step for step in steps if isinstance(step, dict)}
            required = {
                'Run actions/checkout@v4': 'success',
                'Build current read-only schema observer for source-only release': 'success',
                'Verify current database recovery window before retention readers': 'success',
                'Preserve recovery evidence before SQL or retention publication': 'success',
                'Check migration admission without entering a write step': 'failure',
                'Apply or reconcile the exact rehearsed migration bundle': 'skipped',
                'Reconcile fresh already-ready schema without SQL bundle or mutation': 'skipped'}
            if any(keyed.get(name, {}).get('conclusion') != result
                   for name, result in required.items()):
                return False
            if [step.get('name') for step in steps
                if step.get('conclusion') == 'failure'] != [
                    'Check migration admission without entering a write step']:
                return False
            log = _release_job_log(repository, owner['id'], token)
            marker = 'Database mutation activity remains active; preserve prior evidence and reconcile without replay.'
            matched = [line for line in log.splitlines() if marker in line]
            if (len(matched) != 1 or
                not re.fullmatch(r'\d{4}-\d{2}-\d{2}T[^\n]+Z ' + re.escape(marker), matched[0]) or
                'Schema ready; validated migration child applied.' in log or
                'Schema ready; validated no-SQL migration child preserved.' in log):
                return False
        return True
    except (ValueError, RuntimeError, KeyError, TypeError,
            ReleaseOperationHistoryUnproven, EvidenceLookupUnavailable):
        return False



def attested_fast_track_manual_dns_proxy_completion(repository, prior, jobs, token):
    """One source-pinned recovery: a denied DNS write was completed externally.

    This authorizes only carrying already-validated source scope into the next
    fast-track candidate. It never authorizes replaying SQL, Worker or DNS writes.
    """
    if (prior.get('id') != 38276359453 or
        prior.get('head_sha') != '1f08cd5a2518a4d206ecf4bccdad4024b7cba26e' or
        prior.get('status') != 'completed' or prior.get('conclusion') != 'failure' or
        prior.get('run_attempt') != 2 or not is_fast_track_producer(repository, prior)):
        return False
    names = {'Select exact affected applications': 'success',
             'Admit exact validated fast-track source': 'success',
             'Reconcile production schema before publication': 'success',
             'Affected App Deploy — ${{ matrix.target }}': 'skipped',
             'Publish verified website routing': 'failure'}
    indexed = {row.get('name'): row for row in jobs if isinstance(row, dict)}
    if len(indexed) != len(names) or any(
        indexed.get(name, {}).get('conclusion') != outcome or
        indexed.get(name, {}).get('status') != 'completed'
        for name, outcome in names.items()):
        return False
    try:
        # Attempt 1 proved a preserved Worker and a rejected DNS PATCH, not a
        # successful or ambiguous provider write. Attempt 2 was stopped by the
        # changed release HEAD before any provider operation.
        history = api_get(repository,
            'actions/runs/38276359453/attempts/1/jobs?per_page=100', token)
        h = history.get('jobs')
        if not isinstance(h, list) or history.get('total_count') != len(h):
            return False
        router = [row for row in h if row.get('name') == 'Publish verified website routing']
        if (len(router) != 1 or router[0].get('id') != 114904843387 or
            router[0].get('conclusion') != 'failure'):
            return False
        first_log = _release_job_log(repository, 114904843387, token)
        second_log = _release_job_log(repository, 114926219880, token)
        if (not all(fragment in first_log for fragment in (
                'FAST_TRACK_ROUTER:preserved',
                'FAST_TRACK_DNS_PROXY_RECONCILIATION:apex_and_www_only',
                'returned HTTP 403',
                'Website DNS proxy requires DNS Edit authority or recovery: mylegnd.com'))
            or 'FAST_TRACK_DNS_PROXY_RECONCILIATION:readback_complete' in first_log
            or 'FAST_TRACK_RELEASE_HEAD_CHANGED' not in second_log
            or 'FAST_TRACK_DNS_PROXY_RECONCILIATION:apex_and_www_only' in second_log):
            return False

        # Independent read-only evidence after manual proxy activation proves
        # both hostnames actually returned the same current published HTML.
        diagnostic = api_get(repository,
            'actions/runs/38272435954/attempts/3/jobs?per_page=100', token)
        dj = diagnostic.get('jobs')
        if (not isinstance(dj, list) or diagnostic.get('total_count') != len(dj) or
            not any(row.get('id') == 114910241289 and
                row.get('name') == 'Read live publication routes and proxied DNS without writes' and
                row.get('status') == 'completed' and row.get('conclusion') == 'success'
                for row in dj)):
            return False
        log = _release_job_log(repository, 114910241289, token)
        pages, dns, routes = [], [], []
        for line in log.splitlines():
            for label, rows in (('PUBLIC_ROUTE_DIAGNOSTIC=', pages),
                                ('CLOUDFLARE_PUBLIC_DNS=', dns),
                                ('CLOUDFLARE_WORKER_ROUTES=', routes)):
                if label not in line or line.lstrip().find('print(') >= 0:
                    continue
                rows.append(json.loads(line.split(label, 1)[1]))
        hosts = {'https://mylegnd.com/', 'https://www.mylegnd.com/'}
        observed = [row for row in pages if row.get('url') in hosts]
        if (len(observed) != 2 or {row.get('url') for row in observed} != hosts or
            len({row.get('publicationId') for row in observed}) != 1 or
            not all(row.get('status') == 200 and row.get('publicationId') and
                row.get('containsPublishedDocument') is True and
                row.get('containsLoadingMarker') is False and
                row.get('cfRayPresent') is True for row in observed)):
            return False
        if len(dns) != 1 or len(routes) != 1:
            return False
        address = [row for row in dns[0] if row.get('type') in ('A', 'AAAA', 'CNAME')]
        if (len(address) != 3 or
            sum(row.get('type') == 'A' and row.get('name') == 'mylegnd.com'
                and row.get('proxied') is True for row in address) != 2 or
            sum(row.get('type') == 'CNAME' and row.get('name') == 'www.mylegnd.com'
                and row.get('proxied') is True for row in address) != 1 or
            not any(row.get('pattern') == '*/*' and
                row.get('script') == 'legend-business-website-router' for row in routes[0])):
            return False
        return True
    except (KeyError, TypeError, ValueError, RuntimeError,
            ReleaseOperationHistoryUnproven, EvidenceLookupUnavailable):
        return False



# Exact-source historical Website recovery. This is a narrowly scoped
# non-entry attestation; it does not itself grant an upload permission.
WEBSITE_MISSING_UPLOAD_RUN = 38298499476
WEBSITE_MISSING_UPLOAD_REVISION = "631258bdcdd8c42a64570baef0db49b05e1ea829"
WEBSITE_MISSING_UPLOAD_DIAGNOSTIC = 38302622805
WEBSITE_MISSING_UPLOAD_DIAGNOSTIC_SHA = "1e52f3b245d56710e1c914677ed7c5acf69bfc1e"
WEBSITE_MISSING_UPLOAD_BASELINE = "7140313682a7aba20c0a7abf73fe13b215bda043"
WEBSITE_MISSING_UPLOAD_AFTER = "2026-10-12T19:38:11+00:00"


def attested_website_one_deploy_nonentry(repository, token):
    """Require authenticated original run, retained intent and later Azure proof.

    The one old async ZIP submission was not accompanied by a Kudu operation,
    even after three read-only reconciliations. This source-pinned exception
    NEVER treats an ambiguous success as a replay authorization.
    """
    try:
        old = api_get(repository, f"actions/runs/{WEBSITE_MISSING_UPLOAD_RUN}", token)
        if (old.get("id") != WEBSITE_MISSING_UPLOAD_RUN or
            old.get("head_sha") != WEBSITE_MISSING_UPLOAD_REVISION or
            old.get("status") != "completed" or old.get("conclusion") != "failure" or
            old.get("run_attempt") != 3 or not is_fast_track_producer(repository, old)):
            return False
        expected = {1:114975435448, 2:114979430606, 3:114983198708}
        for attempt, job_id in expected.items():
            inventory = api_get(repository,
                f"actions/runs/{WEBSITE_MISSING_UPLOAD_RUN}/attempts/{attempt}/jobs?per_page=100", token)
            jobs=inventory.get("jobs")
            if (not isinstance(jobs,list) or inventory.get("total_count") != len(jobs)):
                return False
            website=[j for j in jobs if j.get("name") == "Affected App Deploy — Website"]
            if (len(website)!=1 or website[0].get("id") != job_id or
                website[0].get("status") != "completed" or
                website[0].get("conclusion") != "failure"):
                return False
            steps={s.get("name"):s for s in website[0].get("steps",[]) if isinstance(s,dict)}
            if steps.get("Publish or reconcile exact application bytes",{}).get("conclusion")!="failure":
                return False
            if attempt>=2 and (steps.get("Restore existing immutable package on resumed attempts",{}).get("conclusion")!="success"
                or steps.get("Compile application and produce ZIP",{}).get("conclusion")!="skipped"):
                return False
            log=_release_job_log(repository,job_id,token)
            if "Deployment remains unverified at the deadline" not in log:
                return False
            if ("Submitting the verified immutable ZIP once." in log) != (attempt==1):
                return False
        original=api_get(repository,
            f"actions/runs/{WEBSITE_MISSING_UPLOAD_RUN}/artifacts?per_page=100",token)
        artifacts=original.get("artifacts")
        if not isinstance(artifacts,list) or original.get("total_count")!=len(artifacts):
            return False
        expected_package="fast-track-package-"+WEBSITE_MISSING_UPLOAD_REVISION+"-website"
        expected_intent="legend-release-operation-intent-364d3a708feee88353a4c3e1fe1ab5796fcffffc6b9a26280bd9f539766c9873"
        matching={a.get("name"):a for a in artifacts if isinstance(a,dict)}
        if (matching.get(expected_package,{}).get("expired") is not False or
            matching.get(expected_intent,{}).get("expired") is not False or
            any(a.get("name","").startswith("legend-release-operation-success-364d3a708feee88353a4c3e1fe1ab5796fcffffc6b9a26280bd9f539766c9873")
                for a in artifacts)):
            return False
        diagnostic=api_get(repository,f"actions/runs/{WEBSITE_MISSING_UPLOAD_DIAGNOSTIC}",token)
        if (diagnostic.get("id")!=WEBSITE_MISSING_UPLOAD_DIAGNOSTIC or
            diagnostic.get("head_sha")!=WEBSITE_MISSING_UPLOAD_DIAGNOSTIC_SHA or
            diagnostic.get("path")!=".github/workflows/legend-fast-track-website-diagnostic.yml" or
            diagnostic.get("status")!="completed" or diagnostic.get("event")!="push" or
            diagnostic.get("head_branch")!=FAST_TRACK_PR_BASE):
            return False
        detail=api_get(repository,
            f"actions/runs/{WEBSITE_MISSING_UPLOAD_DIAGNOSTIC}/jobs?per_page=100",token)
        rows=detail.get("jobs")
        if not isinstance(rows,list) or detail.get("total_count")!=len(rows):
            return False
        evidence=[j for j in rows if j.get("id")==114988969145 and j.get("name")=="website-provider-evidence"]
        if len(evidence)!=1:return False
        executed={s.get("name"):s.get("conclusion") for s in evidence[0].get("steps",[]) if isinstance(s,dict)}
        if any(executed.get(step)!="success" for step in (
            "Read failed deployment and exact static site without writing",
            "Forensic read of preserved Website upload with no provider mutation",
            "Inspect nested Kudu deployment failure without changes")):
            return False
        log=_release_job_log(repository,114988969145,token)
        observations={}
        for line in log.splitlines():
            for prefix in ("WEBSITE_ORIGINAL_UPLOAD_AZURE_HISTORY=",
                           "WEBSITE_ORIGINAL_UPLOAD_RUNTIME_READBACK=",
                           "WEBSITE_ORIGINAL_UPLOAD_APP_STATE="):
                if prefix not in line or "print(" in line:continue
                observations.setdefault(prefix,[]).append(json.loads(line.split(prefix,1)[1]))
        if any(len(observations.get(p,[]))!=1 for p in (
            "WEBSITE_ORIGINAL_UPLOAD_AZURE_HISTORY=",
            "WEBSITE_ORIGINAL_UPLOAD_RUNTIME_READBACK=",
            "WEBSITE_ORIGINAL_UPLOAD_APP_STATE=")):
            return False
        history=observations["WEBSITE_ORIGINAL_UPLOAD_AZURE_HISTORY="][0]
        live=observations["WEBSITE_ORIGINAL_UPLOAD_RUNTIME_READBACK="][0]
        state=observations["WEBSITE_ORIGINAL_UPLOAD_APP_STATE="][0]
        return bool(history.get("candidate")==WEBSITE_MISSING_UPLOAD_REVISION
            and history.get("providerRowsAfterIntent")==[]
            and type(history.get("totalDeploymentRows")) is int and history["totalDeploymentRows"]>0
            and live.get("revision")==WEBSITE_MISSING_UPLOAD_BASELINE
            and live.get("matchesExactCandidate") is False and live.get("status")==200
            and state.get("state")=="Running" and state.get("enabled") is True)
    except (ValueError,KeyError,TypeError,RuntimeError,OSError,
            ReleaseOperationHistoryUnproven,EvidenceLookupUnavailable):
        return False


def attested_website_only_recovery_scope(before, revision, changed):
    """Restore only the never-published Website target; never reselect Protect."""
    if before != WEBSITE_MISSING_UPLOAD_DIAGNOSTIC_SHA:
        return False
    changed=set(changed)
    required={"scripts/validation-resume.py","scripts/deploy-approved-app.py",
              "scripts/test-fast-track-release.py",".github/workflows/legend-fast-track-ui.yml"}
    if not required <= changed or changed-required:
        return False
    if subprocess.run(["git","merge-base","--is-ancestor",before,revision],
                      capture_output=True).returncode:
        return False
    return attested_website_one_deploy_nonentry(
        os.environ["GITHUB_REPOSITORY"], os.environ.get("GH_TOKEN") or os.environ["GITHUB_TOKEN"])


def website_only_recovery_scope(scope):
    if scope["selected"] or scope["schema"] or scope["commerce"] or scope["routing"]:
        raise RuntimeError("WEBSITE_RECOVERY_SCOPE_NOT_ISOLATED")
    row=RELEASE_TARGETS["website"]
    selected={"key":"website","target":"Website","project":row["project"],
              "azure_app":row["releaseName"],"host":row["host"]}
    return {**scope,"matrix":{"include":[selected]},"selected":True}

def attested_fast_track_brand_preupload(repository, run, jobs, token):
    """Carry source after one authenticated no-SQL run failed before any app package/upload.

    This is not a generic 'failed means safe' rule. The immutable, exact release
    and its retained artifact inventory must positively prove no SQL or app write
    step was entered. Any changed/missing evidence fails closed.
    """
    if (run.get('id') != 38318950445 or
        run.get('head_sha') != 'bdc87f64cb9150bfeac26b26044c826b835cbb13' or
        run.get('run_attempt') != 1 or
        run.get('status') != 'completed' or run.get('conclusion') != 'failure' or
        not is_fast_track_producer(repository, run)):
        return False
    expected_apps = {'Affected App Deploy — ' + key for key in
                     ('AgentPortal', 'ClientApp', 'Protect', 'Website')}
    prerequisites = {'Select exact affected applications',
                     'Admit exact validated fast-track source',
                     'Reconcile production schema before publication'}
    routing = 'Publish verified website routing'
    expected = prerequisites | expected_apps | {routing}
    if (len(jobs) != len(expected) or
        {j.get('name') for j in jobs} != expected or
        any(j.get('status') != 'completed' for j in jobs)):
        return False
    indexed = {j['name']: j for j in jobs}
    if (any(indexed[name].get('conclusion') != 'success' for name in prerequisites) or
        indexed[routing].get('conclusion') != 'skipped'):
        return False
    migration_steps = {s.get('name'): s.get('conclusion') for s in
                       indexed['Reconcile production schema before publication'].get('steps', [])}
    if (migration_steps.get('Apply or reconcile the exact rehearsed migration bundle') != 'skipped' or
        migration_steps.get('Reconcile fresh already-ready schema without SQL bundle or mutation') != 'success'):
        return False
    no_entry_steps = {
        'Restore existing immutable package on resumed attempts',
        'Compile application and produce ZIP',
        'Preserve immutable application bytes before upload',
        'Sign in to Azure Production',
        'Publish or reconcile exact application bytes'
    }
    for name in expected_apps:
        owner = indexed[name]
        steps = owner.get('steps')
        if owner.get('conclusion') != 'failure' or not isinstance(steps, list):
            return False
        by_name = {s.get('name'): s for s in steps}
        if (len(by_name) != len(steps) or
            by_name.get('Verify canonical brand registry and four-app identity', {}).get('conclusion') != 'failure' or
            any(by_name.get(step, {}).get('conclusion') != 'skipped' for step in no_entry_steps)):
            return False
        failed = [s for s in steps if s.get('conclusion') == 'failure']
        if len(failed) != 1 or failed[0].get('name') != 'Verify canonical brand registry and four-app identity':
            return False
    try:
        inventory = api_get(repository, f"actions/runs/{run['id']}/artifacts?per_page=100", token)
        artifacts = inventory.get('artifacts')
        expected_artifacts = {
            'legend-migration-observation-a96af08e6b3f871ff92be084434b4b576278cfe09630fd751ae3a03c6f47c37f': 11778505571,
            'fast-track-database-recovery-38318950445-1': 11776974677,
            'fast-track-admission-38318950445-1': 11776869414
        }
        if (not isinstance(artifacts, list) or inventory.get('total_count') != len(expected_artifacts)
            or len(artifacts) != len(expected_artifacts) or
            {a.get('name'): a.get('id') for a in artifacts if isinstance(a, dict)} != expected_artifacts or
            any(a.get('expired') is not False for a in artifacts)):
            return False
    except (KeyError, TypeError, ValueError, RuntimeError, OSError):
        return False
    return True


def fast_track_selector_attempts_proven_nonentry(repository, run, revision, token,
                                                *, clock=time.monotonic):
    """Carry unpublished scope only after every selector attempt proves nonentry.

    This is not a publication, SQL, replay or completion receipt. Keep the older
    migration-prewrite proof separate: no admitted/entered downstream owner is
    accepted here. Historical evidence is read once under a shared 30s budget.
    """
    if (not isinstance(run, dict) or not isinstance(run.get('head_repository'), dict)
        or not isinstance(run.get('repository'), dict) or not is_fast_track_producer(repository, run)
        or run.get('repository', {}).get('full_name', '').lower() != repository.lower()
        or run.get('status') != 'completed' or run.get('conclusion') != 'failure'
        or type(run.get('run_attempt')) is not int or not 2 <= run['run_attempt'] <= 100
        or type(run.get('workflow_id')) is not int or run['workflow_id'] <= 0):
        return False
    def same_run(value, attempt):
        return (isinstance(value, dict) and isinstance(value.get('head_repository'), dict)
            and isinstance(value.get('repository'), dict) and is_fast_track_producer(repository, value)
            and value['repository'].get('full_name', '').lower() == repository.lower()
            and value.get('id') == run['id'] and value.get('head_sha') == run['head_sha']
            and type(value.get('workflow_id')) is int and value['workflow_id'] == run['workflow_id']
            and type(value.get('run_attempt')) is int and value['run_attempt'] == attempt
            and value.get('status') == 'completed' and value.get('conclusion') == 'failure')
    path = '.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW
    selector = 'Select exact affected applications'
    downstream = {'Admit exact validated fast-track source',
                  'Reconcile production schema before publication',
                  'Affected App Deploy — ${{ matrix.target }}', 'Publish verified website routing'}
    names = downstream | {selector}
    with evidence_lookup_budget(clock() + 30.0, clock=clock):
        def read(endpoint):
            evidence_remaining(1)
            value = api_get(repository, endpoint, token)
            evidence_remaining(1)
            return value
        if not same_run(read(f"actions/runs/{run['id']}"), run['run_attempt']):
            return False
        source = _release_history_source(repository, run['head_sha'], path, token)
        if not source or source != git_show_file(revision, path):
            return False
        evidence_remaining(1)
        seen = set()
        for attempt in range(1, run['run_attempt'] + 1):
            if not same_run(read(f"actions/runs/{run['id']}/attempts/{attempt}"), attempt):
                return False
            payload = read(f"actions/runs/{run['id']}/attempts/{attempt}/jobs?per_page=100")
            if not isinstance(payload, dict):
                return False
            jobs = payload.get('jobs')
            if (not isinstance(jobs, list) or type(payload.get('total_count')) is not int
                or payload['total_count'] != len(jobs) or len(jobs) != len(names)
                or any(not isinstance(job, dict) for job in jobs)):
                return False
            indexed = {job.get('name'): job for job in jobs}
            if (set(indexed) != names or any(type(job.get('id')) is not int or job['id'] <= 0
                or job['id'] in seen or job.get('status') != 'completed'
                or type(job.get('run_id')) is not int or job['run_id'] != run['id']
                or job.get('head_sha') != run['head_sha'] or type(job.get('run_attempt')) is not int
                or job['run_attempt'] != attempt for job in jobs)
                or len({job['id'] for job in jobs}) != len(jobs)):
                return False
            if (indexed[selector].get('conclusion') != 'failure'
                or any(indexed[name].get('conclusion') != 'skipped'
                       or indexed[name].get('steps') != [] for name in downstream)):
                return False
            seen.update(job['id'] for job in jobs)
        # A newly started retry invalidates the complete-attempt snapshot.
        return same_run(read(f"actions/runs/{run['id']}"), run['run_attempt'])


def fast_track_attempts_proven_prewrite(repository, run, revision, token):
    """Carry scope only after positive nonentry from every authenticated attempt."""
    if (not is_fast_track_producer(repository, run) or run.get('status') != 'completed'
        or run.get('conclusion') != 'failure' or type(run.get('run_attempt')) is not int
        or not 2 <= run['run_attempt'] <= 100):
        return False
    path = '.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW
    source = _release_history_source(repository, run['head_sha'], path, token)
    # The current reviewed owner must still execute the identical workflow. A
    # different historical executable is not certified by matching step labels.
    if source != git_show_file(revision, path):
        return False
    names = {'Select exact affected applications', 'Admit exact validated fast-track source',
             'Reconcile production schema before publication',
             'Affected App Deploy — ${{ matrix.target }}', 'Publish verified website routing'}
    nonentered = {'Apply or reconcile the exact rehearsed migration bundle',
                  'Reconcile fresh already-ready schema without SQL bundle or mutation'}
    if not nonentered <= set(named_step_blocks(source)):
        return False
    for attempt in range(1, run['run_attempt'] + 1):
        payload = api_get(repository, f"actions/runs/{run['id']}/attempts/{attempt}/jobs?per_page=100", token)
        jobs = payload.get('jobs')
        if (not isinstance(jobs, list) or payload.get('total_count') != len(jobs)
            or len(jobs) != len(names) or any(not isinstance(job, dict) for job in jobs)):
            return False
        indexed = {job.get('name'): job for job in jobs}
        if set(indexed) != names or any(job.get('status') != 'completed'
            or job.get('run_id') != run['id'] or job.get('head_sha') != run['head_sha']
            or type(job.get('run_attempt')) is not int or job['run_attempt'] != attempt for job in jobs):
            return False
        if any(indexed[name].get('conclusion') != 'success' for name in
            ('Select exact affected applications', 'Admit exact validated fast-track source')):
            return False
        if any(indexed[name].get('conclusion') != 'skipped' for name in
            ('Affected App Deploy — ${{ matrix.target }}', 'Publish verified website routing')):
            return False
        owner = indexed['Reconcile production schema before publication']
        steps = owner.get('steps')
        if owner.get('conclusion') not in {'failure', 'cancelled'} or not isinstance(steps, list):
            return False
        for name in nonentered:
            matches = [step for step in steps if isinstance(step, dict) and step.get('name') == name]
            if len(matches) != 1 or matches[0].get('status') != 'completed':
                return False
            if matches[0].get('conclusion') != 'skipped' and not (
                    name == 'Apply or reconcile the exact rehearsed migration bundle'
                    and _attested_fast_track_observation_failure(repository, run, jobs, attempt, token)):
                return False
    return True


def fast_track_unpublished_apps_with_migration_intent(repository, run, revision, token):
    """Carry app selection only; an entered SQL operation remains unresolved.

    Every original publication owner must positively be skipped. The migration
    intent stays immutable and is still enforced by fresh production observation
    and first-write admission. This predicate grants no SQL replay authority.
    """
    if (not is_fast_track_producer(repository, run) or run.get('status') != 'completed'
        or run.get('conclusion') != 'failure' or type(run.get('run_attempt')) is not int
        or not 1 <= run['run_attempt'] <= 100):
        return False
    path = '.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW
    source = _release_history_source(repository, run['head_sha'], path, token)
    if source != git_show_file(revision, path):
        return False
    names = {'Select exact affected applications', 'Admit exact validated fast-track source',
             'Reconcile production schema before publication',
             'Affected App Deploy — ${{ matrix.target }}', 'Publish verified website routing'}
    mutation = 'Apply or reconcile the exact rehearsed migration bundle'
    no_sql = 'Reconcile fresh already-ready schema without SQL bundle or mutation'
    if not {mutation, no_sql} <= set(named_step_blocks(source)):
        return False
    entered = set()
    for attempt in range(1, run['run_attempt'] + 1):
        payload = api_get(repository, f"actions/runs/{run['id']}/attempts/{attempt}/jobs?per_page=100", token)
        jobs = payload.get('jobs')
        if (not isinstance(jobs, list) or payload.get('total_count') != len(jobs)
            or len(jobs) != len(names) or any(not isinstance(j, dict) for j in jobs)):
            return False
        indexed = {j.get('name'): j for j in jobs}
        if (set(indexed) != names or any(j.get('status') != 'completed'
            or j.get('run_id') != run['id'] or j.get('head_sha') != run['head_sha']
            or type(j.get('run_attempt')) is not int or j['run_attempt'] != attempt for j in jobs)):
            return False
        if any(indexed[n].get('conclusion') != 'success' for n in
            ('Select exact affected applications', 'Admit exact validated fast-track source')):
            return False
        if any(indexed[n].get('conclusion') != 'skipped' for n in
            ('Affected App Deploy — ${{ matrix.target }}', 'Publish verified website routing')):
            return False
        owner = indexed['Reconcile production schema before publication']
        steps = owner.get('steps')
        if owner.get('conclusion') not in {'failure', 'cancelled'} or not isinstance(steps, list):
            return False
        selected = {}
        for name in (mutation, no_sql):
            matches = [s for s in steps if isinstance(s, dict) and s.get('name') == name]
            if len(matches) != 1 or matches[0].get('status') != 'completed':
                return False
            selected[name] = matches[0]
        if selected[no_sql].get('conclusion') != 'skipped':
            return False
        if selected[mutation].get('conclusion') == 'skipped':
            continue
        if selected[mutation].get('conclusion') not in {'failure', 'cancelled'}:
            return False
        entered.add(attempt)
    if not entered:
        return False
    inventory = api_get(repository, f"actions/runs/{run['id']}/artifacts?per_page=100", token)
    artifacts = inventory.get('artifacts')
    if not isinstance(artifacts, list) or inventory.get('total_count') != len(artifacts):
        return False
    accounted = set()
    for artifact in artifacts:
        if not re.fullmatch(r'legend-release-child-intent-[a-f0-9]{64}', artifact.get('name', '')):
            continue
        if artifact.get('expired') is not False:
            return False
        record = _release_history_json(repository, run['id'], artifact, 'operation.json', verify_digest=True)
        if (record.get('child') != 'migrations' or record.get('producingAttempt') not in entered
            or record['producingAttempt'] in accounted):
            return False
        _validate_child_generation(repository, run, artifact, record, 'migrations', token, 'intent')
        if (record.get('producingRun') != run['id'] or record.get('phase') != 'intent'
            or record.get('executionAuthority') != run['head_sha']
            or record.get('applicationRevision') != run['head_sha']
            or not re.fullmatch('[a-f0-9]{64}', str(record.get('partitionIdentity', '')))):
            return False
        accounted.add(record['producingAttempt'])
    return accounted == entered


# Completion uses the existing diagnostics workflow and journal channel. It is
# deliberately not a producer of intent, submission, package or app deployment.
FAST_TRACK_COMPLETION_WORKFLOW = 'deployment-diagnostics.yml'
FAST_TRACK_COMPLETION_JOB = 'Complete existing fast-track deployment receipts'


def _completion_rows(payload, field):
    return _completion_inventory(payload, field)


def _completion_time(value):
    import datetime
    try:
        when = datetime.datetime.fromisoformat(value.replace('Z', '+00:00'))
        if when.tzinfo is None:
            raise ValueError()
        return when
    except (AttributeError, TypeError, ValueError):
        raise ValueError('FAST_TRACK_COMPLETION_TIME_UNPROVEN') from None


def fast_track_target_label(key):
    """One display-name owner for selection and exact matrix-job authentication."""
    return dict(portal='AgentPortal', client='ClientApp', protect='Protect',
                parfait='Parfait', website='Website')[key]

def _completion_inventory(payload, field):
    rows = payload.get(field)
    if (not isinstance(rows, list) or type(payload.get('total_count')) is not int
        or payload['total_count'] != len(rows) or any(not isinstance(row, dict)
            or type(row.get('id')) is not int or row['id'] < 1 for row in rows)
        or len({row['id'] for row in rows}) != len(rows)):
        raise ValueError('FAST_TRACK_COMPLETION_INVENTORY_INCOMPLETE:' + field)
    return rows

def _completion_jobs(repository, run, token):
    count = run.get('run_attempt')
    if type(count) is not int or not 1 <= count <= 100:
        raise ValueError('FAST_TRACK_COMPLETION_ATTEMPT_INVENTORY_UNPROVEN')
    attempts = {}
    for attempt in range(1, count + 1):
        rows = _completion_inventory(api_get(repository,
            f"actions/runs/{run['id']}/attempts/{attempt}/jobs?per_page=100", token), 'jobs')
        if any(row.get('run_id') != run['id'] or row.get('run_attempt') != attempt
            or row.get('head_sha') != run['head_sha'] or not isinstance(row.get('name'), str)
            or row.get('status') not in {'queued', 'in_progress', 'completed'} for row in rows):
            raise ValueError('FAST_TRACK_COMPLETION_JOB_IDENTITY_UNPROVEN')
        attempts[attempt] = rows
    return attempts

def _completion_step(job, name, *, conclusions=('success',)):
    steps = job.get('steps')
    if not isinstance(steps, list) or any(not isinstance(step, dict) for step in steps):
        raise ValueError('FAST_TRACK_COMPLETION_STEP_INVENTORY_UNPROVEN')
    rows = [step for step in steps if step.get('name') == name]
    if len(rows) != 1 or rows[0].get('status') != 'completed' or rows[0].get('conclusion') not in conclusions:
        raise ValueError('FAST_TRACK_COMPLETION_STEP_UNPROVEN:' + name)
    return rows[0]

def _completion_artifact(artifacts, name, run):
    rows = [row for row in artifacts if row.get('name') == name]
    if (len(rows) != 1 or rows[0].get('expired') is not False
        or rows[0].get('workflow_run', {}).get('id') != run['id']
        or rows[0].get('workflow_run', {}).get('head_sha') != run['head_sha']
        or not re.fullmatch('sha256:[a-f0-9]{64}', str(rows[0].get('digest', '')))):
        raise ValueError('FAST_TRACK_COMPLETION_ARTIFACT_MISSING_EXPIRED_OR_AMBIGUOUS')
    return rows[0]

def _completion_artifact_in_step(artifact, step):
    import datetime
    def stamp(value):
        parsed = datetime.datetime.fromisoformat(value.replace('Z', '+00:00'))
        if parsed.utcoffset() is None:
            raise ValueError('Missing artifact execution timezone')
        return parsed
    try:
        start, end = stamp(step['started_at']), stamp(step['completed_at'])
        if '.' not in step['completed_at']:
            end += datetime.timedelta(seconds=1)
        return start <= stamp(artifact['created_at']) < end
    except (KeyError, ValueError, TypeError, AttributeError):
        raise ValueError('FAST_TRACK_COMPLETION_ARTIFACT_EXECUTION_TIME_UNPROVEN') from None

def completion_provider_in_original_publication(material, received_time):
    """Attribute only within the authenticated publish step and after its intent.

    GitHub step times have second precision. The final represented second is
    included as a half-open interval ending one second after completed_at.
    Explicit fractional precision keeps its exact exclusive end, including
    fractional zero and comma notation; job cleanup never widens the interval.
    """
    import datetime
    try:
        step=material['originalPublicationStep']
        if (step.get('name')!='Publish or reconcile exact application bytes'
            or step.get('status')!='completed'
            or step.get('conclusion') not in {'success','failure','cancelled'}):
            return False
        start=_completion_time(step.get('started_at'))
        completed=_completion_time(step.get('completed_at'))
        intent=_completion_time(material['intentArtifact']['created_at'])
        received=_completion_time(received_time)
        end=completed
        if not any(mark in step['completed_at'] for mark in ('.',',')):
            end+=datetime.timedelta(seconds=1)
        return start <= completed and start <= intent < end and intent <= received < end
    except (KeyError,TypeError,ValueError,OverflowError,AttributeError):
        return False


def _completion_original_artifact_owner(context, target, artifact, step_name, conclusions):
    matches = []
    for attempt, jobs in context['attempts'].items():
        _completion_inventory(dict(total_count=len(jobs),jobs=jobs),'jobs')
        if any(job.get('run_id') != context['run']['id'] or job.get('run_attempt') != attempt
            or job.get('head_sha') != context['run']['head_sha'] for job in jobs):
            raise ValueError('FAST_TRACK_COMPLETION_JOB_IDENTITY_UNPROVEN')
    label = 'Affected App Deploy — ' + fast_track_target_label(target)
    for attempt, jobs in context['attempts'].items():
        for job in jobs:
            if job.get('name') != label or job.get('conclusion') == 'skipped':
                continue
            steps = [step for step in job.get('steps', []) if step.get('name') == step_name]
            if (len(steps) == 1 and steps[0].get('status') == 'completed'
                and steps[0].get('conclusion') in conclusions
                and _completion_artifact_in_step(artifact, steps[0])):
                matches.append((attempt, job, steps[0]))
    if not matches:
        raise ValueError('FAST_TRACK_COMPLETION_ORIGINAL_ARTIFACT_OWNER_UNPROVEN')
    # Failed-job retries copy successful siblings with new job IDs/attempts.
    # The earliest genuine interval owns the artifact; copied intervals do not
    # claim a repeated build/upload and cannot change its original producer.
    first = min(matches, key=lambda item: item[0])
    def execution(item):
        return (item[1].get('runner_id'), item[1].get('runner_name'),
            *(item[2].get(key) for key in ('name', 'started_at', 'completed_at', 'conclusion')))
    if any(execution(item) != execution(first) for item in matches):
        raise ValueError('FAST_TRACK_COMPLETION_COMPETING_ARTIFACT_EXECUTIONS')
    return first

def _completion_original_unchanged(repository, run, token):
    current = api_get(repository, f"actions/runs/{run['id']}", token)
    keys = ('id', 'head_sha', 'head_branch', 'path', 'event', 'run_attempt', 'status', 'conclusion', 'head_repository')
    if current.get('status') != 'completed' or any(current.get(key) != run.get(key) for key in keys):
        raise ValueError('FAST_TRACK_COMPLETION_ORIGINAL_RUN_CHANGED')


def fast_track_completion_original(repository, original_run, revision, token):
    """Derive all original obligations; caller target lists have no authority."""
    run = api_get(repository, f'actions/runs/{original_run}', token)
    if (not is_fast_track_producer(repository, run) or run.get('id') != original_run
        or run.get('head_sha') != revision or run.get('status') != 'completed'
        or run.get('conclusion') not in {'success', 'failure', 'cancelled'}):
        raise ValueError('FAST_TRACK_COMPLETION_ORIGINAL_PRODUCER_UNPROVEN')
    producers = fast_track_historical_producers(repository, revision, token)
    if len(producers) != 1 or producers[0].get('id') != original_run:
        raise ValueError('FAST_TRACK_COMPLETION_ORIGINAL_PRODUCER_AMBIGUOUS')
    fast_track_merged_source_proof(repository, revision, token)
    parents = subprocess.check_output(['git', 'rev-list', '--parents', '-n', '1', revision], text=True).split()
    if len(parents) != 3 or parents[0] != revision:
        raise ValueError('FAST_TRACK_COMPLETION_ORIGINAL_BASELINE_UNPROVEN')
    request = json.loads(_release_history_source(repository, revision, RELEASE_REQUEST_PATH, token))
    if 'protectSupersession' in request:
        supersession = fast_track_protect_supersession(revision, request=request)
        if supersession['expectedReleaseParent'] != parents[1]:
            raise ValueError('FAST_TRACK_COMPLETION_ORIGINAL_BASELINE_UNPROVEN')
        selection_baseline = supersession['originalRevision']
    else:
        selection_baseline = fast_track_effective_baseline(parents[1], revision, repository=repository, token=token)
        if selection_baseline != parents[1]:
            raise ValueError('FAST_TRACK_COMPLETION_CARRIED_SCOPE_REQUIRES_ORIGINAL_BASELINE_PROOF')
    baseline = parents[1]
    paths = subprocess.check_output(['git', 'diff', '--name-only', selection_baseline, revision], text=True).splitlines()
    scope = fast_track_scope(paths)
    selected = [row['key'] for row in scope['matrix']['include']]
    names = [RELEASE_TARGETS[key]['releaseName'] for key in selected]
    if (not selected or request.get('releaseMode') != 'approved-only' or request.get('requestedBy') != 'Founder'
        or 'trustedToolPromotion' in request or request.get('preserveLiveTargets') is not False
        or not isinstance(request.get('targets'), list) or len(request['targets']) != len(names)
        or set(request['targets']) != set(names) or request.get('cloudflareWebsiteRouting') is not scope['routing']
        or RELEASE_REQUEST_PATH not in paths):
        raise ValueError('FAST_TRACK_COMPLETION_ORIGINAL_SCOPE_UNPROVEN')
    attempts = _completion_jobs(repository, run, token)
    original_workflow = _release_history_source(repository, revision, '.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW, token)
    attempts = {attempt: _without_skipped_recovery_job(jobs, original_workflow) for attempt, jobs in attempts.items()}
    expected_names = {'Select exact affected applications', 'Admit exact validated fast-track source',
        'Reconcile production schema before publication', 'Publish verified website routing'}
    expected_names |= {'Affected App Deploy — ' + fast_track_target_label(key) for key in selected}
    for jobs in attempts.values():
        if (len(jobs) != len(expected_names) or {job['name'] for job in jobs} != expected_names
            or any(job['status'] != 'completed' for job in jobs)):
            raise ValueError('FAST_TRACK_COMPLETION_ORIGINAL_JOB_SCOPE_UNPROVEN')
    latest = {job['name']: job for job in attempts[run['run_attempt']]}
    for name, step in [('Select exact affected applications', 'Derive application impact exclusively from changed paths'),
        ('Admit exact validated fast-track source', 'Authenticate merged source and preserved required validation')]:
        if latest[name].get('conclusion') != 'success':
            raise ValueError('FAST_TRACK_COMPLETION_ORIGINAL_ADMISSION_UNPROVEN')
        _completion_step(latest[name], step)
    schema = latest['Reconcile production schema before publication']
    if scope['commerce']:
        if schema.get('conclusion') != 'success':
            raise ValueError('FAST_TRACK_COMPLETION_ORIGINAL_SCHEMA_UNPROVEN')
        outcomes = [_completion_step(schema, step, conclusions=('success', 'skipped'))['conclusion'] for step in (
            'Apply or reconcile the exact rehearsed migration bundle',
            'Reconcile fresh already-ready schema without SQL bundle or mutation')]
        if sorted(outcomes) != ['skipped', 'success']:
            raise ValueError('FAST_TRACK_COMPLETION_ORIGINAL_SCHEMA_UNPROVEN')
    elif any(next(job for job in jobs if job['name'] == schema['name']).get('conclusion') != 'skipped'
             for jobs in attempts.values()):
        raise ValueError('FAST_TRACK_COMPLETION_UNEXPECTED_SCHEMA_EXECUTION')
    artifacts = _completion_inventory(api_get(repository,
        f"actions/runs/{original_run}/artifacts?per_page=100", token), 'artifacts')
    context = dict(run=run, attempts=attempts, latest=latest, artifacts=artifacts, scope=scope)
    router = latest['Publish verified website routing']
    if scope['routing']:
        if router.get('conclusion') != 'success':
            raise ValueError('FAST_TRACK_COMPLETION_ROUTING_UNRESOLVED')
        _completion_step(router, FAST_TRACK_CHILD_STEPS['routing-cloudflare'][1])
        records = []
        for artifact in artifacts:
            if not artifact.get('name', '').startswith('legend-release-child-intent-'):
                continue
            intent = _release_history_json(repository, original_run, artifact, 'operation.json', verify_digest=True)
            if intent.get('child') == 'routing-cloudflare':
                _validate_child_generation(repository, run, artifact, intent, 'routing-cloudflare', token, 'intent')
                success = release_child_history(repository, 'routing-cloudflare', intent['dependencyIdentity'], token, 'success')
                if (not success or success.get('producingRun') != original_run
                    or success.get('applicationRevision') != revision or any(success.get(key) != intent.get(key)
                        for key in ('dependencyIdentity', 'materialIdentity', 'partitionIdentity', 'executionAuthority'))):
                    raise ValueError('FAST_TRACK_COMPLETION_ROUTING_RECEIPT_UNPROVEN')
                records.append(success)
        if len(records) != 1:
            raise ValueError('FAST_TRACK_COMPLETION_ROUTING_RECEIPT_UNPROVEN')
    elif any(next(job for job in jobs if job['name'] == router['name']).get('conclusion') != 'skipped'
             for jobs in attempts.values()):
        raise ValueError('FAST_TRACK_COMPLETION_UNEXPECTED_ROUTING_EXECUTION')
    _completion_original_unchanged(repository, run, token)
    return dict(context, targets=selected, baseRevision=baseline)


def _completion_workflow_source(repository, revision, token):
    """Only the reviewed existing observer contract can own a completion receipt."""
    path = '.github/workflows/' + FAST_TRACK_COMPLETION_WORKFLOW
    source = _release_history_source(repository, revision, path, token)
    current = (Path(__file__).resolve().parent.parent / path).read_text()
    # Compare the complete execution envelope, not regex-selected YAML fields.
    # Quoted/duplicate keys, aliases, defaults and unrelated edits all fail closed.
    if source != current:
        raise ValueError('FAST_TRACK_COMPLETION_WORKFLOW_CONTRACT_UNPROVEN')
    return source


def fast_track_completion_observer(repository, run, token, *, live=False, attempt=None, job_id=None):
    """Verify real observer provenance plus successful nonpublishing promotion."""
    revision = run.get('head_sha', '')
    if (run.get('path') != '.github/workflows/' + FAST_TRACK_COMPLETION_WORKFLOW
        or run.get('head_branch') != FAST_TRACK_PR_BASE or run.get('event') != 'workflow_dispatch'
        or run.get('head_repository', {}).get('full_name', '').lower() != repository.lower()
        or type(run.get('id')) is not int or not re.fullmatch('[a-f0-9]{40}', revision)
        or type(attempt) is not int or not 1 <= attempt <= run.get('run_attempt', 0)
        or (live and (run.get('status') not in {'in_progress','queued'} or attempt != run['run_attempt']))):
        raise ValueError('FAST_TRACK_COMPLETION_OBSERVER_UNPROVEN')
    _completion_workflow_source(repository, revision, token)
    promotions = fast_track_historical_producers(repository, revision, token)
    if (len(promotions) != 1 or promotions[0].get('head_sha') != revision
        or fast_track_promotion_parent(repository, promotions[0], token) is None):
        raise ValueError('FAST_TRACK_COMPLETION_TOOL_NOT_PROMOTED')
    jobs = _completion_rows(api_get(repository, f"actions/runs/{run['id']}/attempts/{attempt}/jobs?per_page=100", token), 'jobs')
    owners = [j for j in jobs if j.get('name') == FAST_TRACK_COMPLETION_JOB]
    if (len(owners) != 1 or type(owners[0].get('id')) is not int
        or owners[0].get('run_id') != run['id'] or owners[0].get('run_attempt') != attempt
        or owners[0].get('head_sha') != revision or (job_id is not None and owners[0]['id'] != job_id)
        or (live and (owners[0].get('status') != 'in_progress'
            or owners[0].get('runner_name') != os.environ.get('RUNNER_NAME')))):
        raise ValueError('FAST_TRACK_COMPLETION_OBSERVER_JOB_UNPROVEN')
    if live:
        current = api_get(repository, 'git/ref/heads/' + FAST_TRACK_PR_BASE, token)
        if current.get('object',{}).get('sha') != revision:
            raise ValueError('FAST_TRACK_COMPLETION_TOOL_HEAD_CHANGED')
    return dict(runId=run['id'], runAttempt=attempt, jobId=owners[0]['id'], toolRevision=revision,
                promotionRunId=promotions[0]['id'])


def assert_fast_track_completion_execution(original_run, application_revision, *, clock=time.monotonic, sleep=time.sleep):
    env = os.environ
    repository = env.get('GITHUB_REPOSITORY',''); token = env.get('GH_TOKEN') or env.get('GITHUB_TOKEN')
    ref = 'refs/heads/' + FAST_TRACK_PR_BASE
    if (env.get('GITHUB_ACTIONS') != 'true' or env.get('GITHUB_EVENT_NAME') != 'workflow_dispatch'
        or env.get('GITHUB_REF') != ref or env.get('GITHUB_JOB') != 'complete-fast-track'
        or env.get('GITHUB_WORKFLOW_REF') != repository + '/.github/workflows/' + FAST_TRACK_COMPLETION_WORKFLOW + '@' + ref
        or not token or not env.get('RUNNER_NAME')
        or not env.get('GITHUB_RUN_ID','').isdigit() or not env.get('GITHUB_RUN_ATTEMPT','').isdigit()):
        raise ValueError('FAST_TRACK_COMPLETION_EXECUTION_UNPROVEN')
    run_id, attempt = int(env['GITHUB_RUN_ID']), int(env['GITHUB_RUN_ATTEMPT'])
    revision = env.get('GITHUB_SHA', '')
    if min(run_id, attempt) < 1 or not re.fullmatch('[a-f0-9]{40}', revision):
        raise ValueError('FAST_TRACK_COMPLETION_EXECUTION_UNPROVEN')
    if type(original_run) is not int or original_run < 1 or not re.fullmatch('[a-f0-9]{40}',application_revision or ''):
        raise ValueError('FAST_TRACK_COMPLETION_DISPATCH_UNPROVEN')
    event=json.loads(Path(env['GITHUB_EVENT_PATH']).read_text())
    inputs=event.get('inputs',{})
    if (inputs.get('scope')!='complete_fast_track' or inputs.get('original_run')!=str(original_run)
        or inputs.get('application_revision')!=application_revision or inputs.get('target') or inputs.get('deployment_id')):
        raise ValueError('FAST_TRACK_COMPLETION_DISPATCH_UNPROVEN')
    expected = dict(id=run_id, run_attempt=attempt, event='workflow_dispatch',
        path='.github/workflows/' + FAST_TRACK_COMPLETION_WORKFLOW,
        head_branch=FAST_TRACK_PR_BASE, head_sha=revision)
    with evidence_lookup_budget(clock() + 30, clock=clock):
        run, _ = _observe_live_workflow(repository, expected, token,
            queued_job=lambda: FAST_TRACK_COMPLETION_JOB, require_job=True,
            error=ValueError, code='FAST_TRACK_COMPLETION_EXECUTION_CHANGED',
            job_code='FAST_TRACK_COMPLETION_OBSERVER_JOB_UNPROVEN', clock=clock, sleep=sleep)
        result = fast_track_completion_observer(repository, run, token, live=True, attempt=attempt)
        if (subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip()!=revision
            or subprocess.run(['git','diff','--quiet','HEAD','--',
                '.github/workflows/'+FAST_TRACK_COMPLETION_WORKFLOW,'scripts/validation-resume.py',
                'scripts/deploy-approved-app.py','scripts/release-operation-evidence.py',
                'scripts/release_policy.py','scripts/release-artifacts/transport.cjs'],capture_output=True).returncode
            or subprocess.run(['git','merge-base','--is-ancestor',application_revision,revision],capture_output=True).returncode):
            raise ValueError('FAST_TRACK_COMPLETION_CHECKED_SOURCE_UNPROVEN')
        evidence_remaining(1)
        return result


@functools.lru_cache(maxsize=1)
def _completion_journal_owner():
    import importlib.util
    spec=importlib.util.spec_from_file_location('completion_submission_owner',Path(__file__).with_name('release-operation-evidence.py'))
    owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
    return owner


def fast_track_completion_material(repository, original, key, token):
    """Read genuine original intent and immutable package descriptors, never create them."""
    run = original['run']; revision = run['head_sha']
    if key not in original['targets']:
        raise ValueError('FAST_TRACK_COMPLETION_TARGET_OUTSIDE_SCOPE')
    artifacts = original['artifacts']
    def artifact(name):
        return _completion_artifact(artifacts, name, run)
    package = artifact('fast-track-package-' + revision + '-' + key)
    intents = []
    for a in artifacts:
        if not a.get('name','').startswith('legend-release-operation-intent-'):
            continue
        a = artifact(a['name'])
        value = _release_history_json(repository, run['id'], a, 'operation.json', verify_digest=True)
        if value.get('target') == key:
            intents.append((a,value))
    if len(intents) != 1:
        raise ValueError('FAST_TRACK_COMPLETION_ORIGINAL_INTENT_UNPROVEN')
    item, intent = intents[0]
    required = {'target','applicationRevision','packageDigest','schemaVersion','operationId','baseline',
        'packageProducerRun','producingRun','producingAttempt','baselineDeploymentIds','phase','submissionContract'}
    operation = hashlib.sha256(json.dumps(dict(target=key,applicationRevision=revision,
        packageDigest=intent.get('packageDigest')),sort_keys=True,separators=(',',':')).encode()).hexdigest()
    if (set(intent) != required or intent.get('schemaVersion') != 1 or intent.get('phase') != 'intent'
        or intent.get('applicationRevision') != revision or intent.get('producingRun') != run['id']
        or intent.get('packageProducerRun') != run['id'] or type(intent.get('producingAttempt')) is not int
        or not 1 <= intent['producingAttempt'] <= run['run_attempt']
        or not re.fullmatch('[a-f0-9]{64}', intent.get('packageDigest',''))
        or not re.fullmatch('[a-f0-9]{40}', intent.get('baseline',''))
        or intent.get('operationId') != operation or item['name'] != 'legend-release-operation-intent-' + operation
        or intent.get('submissionContract') != dict(kind='azure_onedeploy_windows_intent_v1',value=operation)
        or not isinstance(intent.get('baselineDeploymentIds'),list)
        or len(intent['baselineDeploymentIds']) > 1000
        or any(not isinstance(x,str) or not re.fullmatch('[a-zA-Z0-9_.:-]{1,256}',x) for x in intent['baselineDeploymentIds'])
        or intent['baselineDeploymentIds'] != sorted(set(intent['baselineDeploymentIds']))):
        raise ValueError('FAST_TRACK_COMPLETION_ORIGINAL_INTENT_IDENTITY_CHANGED')
    intent_attempt, job, publication = _completion_original_artifact_owner(original, key, item,
        'Publish or reconcile exact application bytes', ('success', 'failure', 'cancelled'))
    package_attempt, package_job, _ = _completion_original_artifact_owner(original, key, package,
        'Preserve immutable application bytes before upload', ('success',))
    if intent_attempt != intent['producingAttempt'] or package_attempt > intent_attempt:
        raise ValueError('FAST_TRACK_COMPLETION_ORIGINAL_ARTIFACT_ATTEMPT_CHANGED')
    component_upload_proven(package, package_job, 'Preserve immutable application bytes before upload',
        _release_job_log(repository, package_job['id'], token))
    # Exact canonical journal history detects competing retained intents too.
    retained=release_operation_history(repository,operation,revision,key,run['id'],run['run_attempt'],token)
    if retained != intent:
        raise ValueError('FAST_TRACK_COMPLETION_COMPETING_ORIGINAL_INTENT')
    submitted=release_operation_history(repository,operation,revision,key,run['id'],run['run_attempt'],token,phase='submitted')
    _completion_journal_owner().validate_original_submission_observation(intent,submitted,run)
    if submitted is None and any(a.get('name')=='legend-release-operation-submitted-'+operation for a in artifacts):
        raise ValueError('FAST_TRACK_COMPLETION_SUBMITTED_RECEIPT_MISSING')
    if submitted is not None:
        submitted_artifact = artifact('legend-release-operation-submitted-' + operation)
        submitted_attempt, _, _ = _completion_original_artifact_owner(original, key, submitted_artifact,
            'Publish or reconcile exact application bytes', ('success', 'failure', 'cancelled'))
        if (submitted.get('producingAttempt') != submitted_attempt or
            _release_history_json(repository, run['id'], submitted_artifact, 'operation.json', verify_digest=True) != submitted):
            raise ValueError('FAST_TRACK_COMPLETION_SUBMITTED_OWNER_UNPROVEN')
    _completion_original_unchanged(repository, run, token)
    return dict(intent=intent,intentArtifact=item,packageArtifact=package,originalJob=job,
        packageJob=package_job,packageAttempt=package_attempt,originalPublicationStep=publication,
        submissionObservation=submitted)


def validate_fast_track_completion_record(repository, run, artifact, record, token):
    """A later observer can attest only success and cannot impersonate the original upload."""
    proof=record.get('completionEvidence')
    if not isinstance(proof,dict) or set(proof) != {'originalRun','originalRunAttempt','originalPackageJobId','originalPackageAttempt','intentArtifactId','intentArtifactDigest',
        'packageArtifactId','packageArtifactDigest','originalJobId','observer','provider'}:
        raise ValueError('FAST_TRACK_COMPLETION_RECEIPT_SHAPE_UNPROVEN')
    observer=fast_track_completion_observer(repository,run,token,attempt=record.get('producingAttempt'),
        job_id=proof.get('observer',{}).get('jobId'))
    if observer != proof['observer'] or record.get('producingRun') != run['id'] or record.get('phase') != 'success':
        raise ValueError('FAST_TRACK_COMPLETION_RECEIPT_OBSERVER_CHANGED')
    if (artifact.get('workflow_run',{}).get('id') != run['id']
        or subprocess.run(['git','merge-base','--is-ancestor',record.get('applicationRevision',''),observer['toolRevision']],capture_output=True).returncode):
        raise ValueError('FAST_TRACK_COMPLETION_RECEIPT_ANCESTRY_UNPROVEN')
    observer_jobs=_completion_rows(api_get(repository,f"actions/runs/{run['id']}/attempts/{observer['runAttempt']}/jobs?per_page=100",token),'jobs')
    owner=next(j for j in observer_jobs if j.get('id')==observer['jobId'])
    steps=owner.get('steps')
    if not isinstance(steps,list) or not all(isinstance(step,dict) for step in steps):
        raise ValueError('FAST_TRACK_COMPLETION_OBSERVER_STEPS_UNPROVEN')
    matches=[step for step in steps if step.get('name')=='Reconcile original bytes and record only missing success receipts']
    if len(matches)!=1:
        raise ValueError('FAST_TRACK_COMPLETION_OBSERVER_STEP_UNPROVEN')
    step=matches[0]
    _completion_step(owner,'Authenticate original operations and promoted completion authority')
    live=(owner.get('status')=='in_progress' and step.get('status')=='in_progress'
        and step.get('conclusion') is None and owner.get('conclusion') is None
        and os.environ.get('GITHUB_JOB')=='complete-fast-track'
        and os.environ.get('GITHUB_RUN_ID')==str(run['id'])
        and os.environ.get('GITHUB_RUN_ATTEMPT')==str(observer['runAttempt'])
        and os.environ.get('RUNNER_NAME')==owner.get('runner_name'))
    import datetime
    if live:
        if not _completion_time(step.get('started_at')) <= _completion_time(artifact.get('created_at')) <= datetime.datetime.now(datetime.timezone.utc):
            raise ValueError('FAST_TRACK_COMPLETION_RECEIPT_OUTSIDE_OBSERVER_STEP')
    elif (owner.get('status')!='completed' or owner.get('conclusion') not in {'success','failure','cancelled'}
        or step.get('status')!='completed' or step.get('conclusion') not in {'success','failure','cancelled'}
        or not _completion_artifact_in_step(artifact,step)):
        raise ValueError('FAST_TRACK_COMPLETION_RECEIPT_OWNER_UNRESOLVED')
    original=fast_track_completion_original(repository,proof['originalRun'],record.get('applicationRevision',''),token)
    material=fast_track_completion_material(repository,original,record.get('target'),token)
    expected_proof=dict(originalRun=original['run']['id'],originalRunAttempt=original['run']['run_attempt'],
        originalPackageJobId=material['packageJob']['id'],originalPackageAttempt=material['packageAttempt'],intentArtifactId=material['intentArtifact']['id'],
        intentArtifactDigest=material['intentArtifact']['digest'],packageArtifactId=material['packageArtifact']['id'],
        packageArtifactDigest=material['packageArtifact']['digest'],originalJobId=material['originalJob']['id'],
        observer=observer,provider=proof['provider'])
    provider=proof['provider']; ids=record.get('deploymentIds')
    submitted=material['submissionObservation']
    if submitted is not None and ids != [submitted['deploymentId']]:
        raise ValueError('FAST_TRACK_COMPLETION_CONFLICTS_WITH_ORIGINAL_SUBMISSION')
    if (not isinstance(provider,dict) or set(provider) != {'deploymentId','receivedTime','status','runtimeRevision'}
        or not isinstance(ids,list) or len(ids)!=1 or provider.get('deploymentId')!=ids[0]
        or not isinstance(ids[0],str) or not re.fullmatch('[a-zA-Z0-9_.:-]{1,256}',ids[0])
        or ids[0] in material['intent']['baselineDeploymentIds'] or provider.get('status')!=4
        or provider.get('runtimeRevision')!=record['applicationRevision']
        or not completion_provider_in_original_publication(material,provider.get('receivedTime'))):
        raise ValueError('FAST_TRACK_COMPLETION_PROVIDER_CORRELATION_UNPROVEN')
    expected=dict(material['intent'],phase='success',producingRun=run['id'],producingAttempt=observer['runAttempt'],
        deploymentIds=ids,completionEvidence=expected_proof)
    if record != expected:
        raise ValueError('FAST_TRACK_COMPLETION_RECEIPT_IDENTITY_CHANGED')
    if not live and (owner['conclusion']!='success' or step['conclusion']!='success'):
        return False  # Preserve terminal failed evidence; a new observer must complete it.
    return record


def fast_track_completion_present(repository, revision, token, *, original_run=None):
    # Discovery only: the caller must still authenticate every operation receipt.
    if original_run is not None:
        name = fast_track_recovery_summary_name(original_run, revision)
        artifacts = _completion_inventory(api_get(repository,
            'actions/artifacts?name=' + urllib.parse.quote(name, safe='') + '&per_page=100', token), 'artifacts')
        if any(artifact.get('name') != name or artifact.get('expired') is not False for artifact in artifacts):
            raise ValueError('RECOVERY_SUMMARY_ARTIFACT_UNPROVEN')
        if artifacts:
            return True
    runs=_completion_rows(api_get(repository,
        f'actions/workflows/{FAST_TRACK_COMPLETION_WORKFLOW}/runs?event=workflow_dispatch&per_page=100',token),'workflow_runs')
    for run in runs:
        if run.get('head_branch')!=FAST_TRACK_PR_BASE or run.get('path')!='.github/workflows/'+FAST_TRACK_COMPLETION_WORKFLOW:
            continue
        artifacts=_completion_rows(api_get(repository,f"actions/runs/{run['id']}/artifacts?per_page=100",token),'artifacts')
        for artifact in artifacts:
            if not artifact.get('name','').startswith(('legend-release-operation-success-','legend-release-completion-')):
                continue
            record=_release_history_json(repository,run['id'],artifact,'operation.json',verify_digest=True)
            if record.get('applicationRevision')==revision:
                return True
    return False


def fast_track_completion_summary_name(original_run, revision):
    return 'legend-release-completion-' + hashlib.sha256(json.dumps(
        dict(originalRun=original_run,applicationRevision=revision),sort_keys=True,separators=(',',':')).encode()).hexdigest()


def fast_track_completion_summary(repository, run, token, targets):
    name=fast_track_completion_summary_name(run['id'],run['head_sha'])
    artifacts=_completion_rows(api_get(repository,'actions/artifacts?name='+urllib.parse.quote(name,safe='')+'&per_page=100',token),'artifacts')
    complete=False;seen=set()
    for artifact in artifacts:
        producer_id=artifact.get('workflow_run',{}).get('id')
        if (artifact.get('name')!=name or artifact.get('expired') is not False
            or type(producer_id) is not int or producer_id in seen
            or type(artifact.get('id')) is not int or artifact['id']<1
            or not re.fullmatch('sha256:[a-f0-9]{64}',artifact.get('digest',''))):
            raise ValueError('FAST_TRACK_COMPLETION_SUMMARY_ARTIFACT_UNPROVEN')
        seen.add(producer_id)
        producer=api_get(repository,f"actions/runs/{artifact.get('workflow_run',{}).get('id')}",token)
        if producer.get('id') != producer_id:
            raise ValueError('FAST_TRACK_COMPLETION_SUMMARY_PRODUCER_CHANGED')
        record=_release_history_json(repository,producer['id'],artifact,'operation.json',verify_digest=True)
        observer=fast_track_completion_observer(repository,producer,token,attempt=record.get('producingAttempt'),job_id=record.get('observer',{}).get('jobId'))
        expected=dict(schemaVersion=1,phase='success',originalRun=run['id'],applicationRevision=run['head_sha'],
            producingRun=producer['id'],producingAttempt=observer['runAttempt'],observer=observer,targets=targets)
        if record!=expected or subprocess.run(['git','merge-base','--is-ancestor',run['head_sha'],observer['toolRevision']],capture_output=True).returncode:
            raise ValueError('FAST_TRACK_COMPLETION_SUMMARY_IDENTITY_CHANGED')
        jobs=_completion_rows(api_get(repository,f"actions/runs/{producer['id']}/attempts/{observer['runAttempt']}/jobs?per_page=100",token),'jobs')
        job=next(j for j in jobs if j.get('id')==observer['jobId'])
        steps=[x for x in job.get('steps',[]) if x.get('name')=='Reconcile original bytes and record only missing success receipts']
        if (producer.get('status')=='completed' and producer.get('conclusion')=='success'
            and job.get('status')=='completed' and job.get('conclusion')=='success'
            and len(steps)==1 and steps[0].get('status')=='completed' and steps[0].get('conclusion')=='success'
            and _completion_artifact_in_step(artifact,steps[0])):
            complete=True
    return complete


def validate_original_completion_success(repository, original, material, record, token):
    """Preserve a genuine original success only with its exact successful owner."""
    if 'completionEvidence' in record:
        return record  # Canonical history has authenticated the later observer.
    intent = material['intent']; run = original['run']
    artifact = _completion_artifact(original['artifacts'],
        'legend-release-operation-success-' + intent['operationId'], run)
    attempt, job, _ = _completion_original_artifact_owner(original, intent['target'], artifact,
        'Publish or reconcile exact application bytes', ('success',))
    if (record.get('producingRun') != run['id'] or record.get('producingAttempt') != attempt
        or job.get('conclusion') != 'success'
        or _release_history_json(repository, run['id'], artifact, 'operation.json', verify_digest=True) != record):
        raise ValueError('FAST_TRACK_COMPLETION_ORIGINAL_SUCCESS_OWNER_UNPROVEN')
    if intent['target'] in {'website', 'protect'}:
        _completion_step(job, 'Verify public sitemap XML and robots contract')
    return record


def fast_track_completed_operation_receipts(repository, run, token, *, require_completed_observer=True):
    """Positive all-target receipt proof; missing evidence never clears the predecessor."""
    original=fast_track_completion_original(repository,run['id'],run['head_sha'],token)
    if fast_track_recovery_completed(repository, original, token):
        return True
    completed_targets={}
    for key in original['targets']:
        material=fast_track_completion_material(repository,original,key,token)
        intent=material['intent']
        success=release_operation_history(repository,intent['operationId'],run['head_sha'],key,
            run['id'],run['run_attempt'],token,phase='success')
        if success is None:
            return False
        if (any(success.get(k)!=v for k,v in intent.items() if k not in {'phase','producingRun','producingAttempt'})
            or not isinstance(success.get('deploymentIds'),list) or len(success['deploymentIds'])!=1
            or success['deploymentIds'][0] in intent['baselineDeploymentIds']):
            raise ValueError('FAST_TRACK_COMPLETION_SUCCESS_IDENTITY_CHANGED')
        if material['submissionObservation'] is not None and success['deploymentIds'] != [material['submissionObservation']['deploymentId']]:
            raise ValueError('FAST_TRACK_COMPLETION_SUCCESS_CONFLICTS_WITH_SUBMISSION')
        validate_original_completion_success(repository,original,material,success,token)
        completed_targets[key]=dict(operationId=intent['operationId'],packageDigest=intent['packageDigest'],
            deploymentIds=success['deploymentIds'],intentArtifactId=material['intentArtifact']['id'],
            packageArtifactId=material['packageArtifact']['id'])
    if require_completed_observer:
        return fast_track_completion_summary(repository,run,token,completed_targets)
    return completed_targets



def fast_track_effective_baseline(before, revision, *, repository=None, token=None):
    """Carry unpublished app scope; this never authorizes SQL or publication replay."""
    repository = repository or os.environ['GITHUB_REPOSITORY']
    token = token or os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
    # Keep a bounded chain while carrying successive positively proven prewrite repairs.
    for _ in range(16):
        if not re.fullmatch('[a-f0-9]{40}', before) or subprocess.run(
                ['git', 'merge-base', '--is-ancestor', before, revision], capture_output=True).returncode:
            raise RuntimeError('FAST_TRACK_SCOPE_BASELINE_UNPROVEN')
        runs = fast_track_historical_producers(repository, before, token)
        prior = runs[0] if runs else None
        if prior is None:
            repair_parent = fast_track_untriggered_repair_parent(repository, before, token)
            if repair_parent is None:
                return before
            before = repair_parent
            continue
        if prior.get('conclusion') == 'success':
            if prior.get('head_sha') != before:
                raise ValueError('FAST_TRACK_RECOVERY_RUN_IDENTITY_CHANGED')
            promotion_parent = fast_track_promotion_parent(repository, prior, token)
            if promotion_parent is not None:
                before = promotion_parent
                continue
            return before
        if prior.get('status') != 'completed':
            raise RuntimeError('FAST_TRACK_PREDECESSOR_ACTIVE')
        if len(runs) != 1 or prior.get('head_sha') != before:
            raise RuntimeError('FAST_TRACK_PREDECESSOR_REQUIRES_OPERATION_RECONCILIATION')
        payload = api_get(repository, f"actions/runs/{prior['id']}/jobs?filter=latest&per_page=100", token)
        jobs = payload.get('jobs')
        if not isinstance(jobs, list) or payload.get('total_count') != len(jobs):
            raise RuntimeError('FAST_TRACK_PREDECESSOR_JOBS_INCOMPLETE')
        if fast_track_completion_present(repository, before, token, original_run=prior['id']):
            if fast_track_completed_operation_receipts(repository, prior, token):
                return before
        if fast_track_completed_apps_pending_routing(repository, prior, jobs, token):
            return before
        if attested_fast_track_manual_dns_proxy_completion(repository, prior, jobs, token):
            return before
        selections = [j for j in jobs if j.get('name') == 'Select exact affected applications']
        admissions = [j for j in jobs if j.get('name') == 'Admit exact validated fast-track source']
        writes = [j for j in jobs if j.get('name', '').startswith('Affected App Deploy — ') or
                  j.get('name') in {'Reconcile production schema before publication', 'Publish verified website routing'}]
        migrations = [j for j in writes if j.get('name') == 'Reconcile production schema before publication']
        publications = [j for j in writes if j not in migrations]
        migration_steps = migrations[0].get('steps', []) if len(migrations) == 1 else []
        mutation = [s for s in migration_steps if s.get('name') == 'Apply or reconcile the exact rehearsed migration bundle']
        # A failed selector has no write authority. Carry its unshipped change
        # only when every downstream admission and production owner was skipped.
        selection_stopped = (len(selections) == 1 and selections[0].get('status') == 'completed'
            and selections[0].get('conclusion') == 'failure'
            and len(admissions) == 1 and admissions[0].get('conclusion') == 'skipped'
            and all(j.get('conclusion') == 'skipped' for j in writes))
        admission_stopped = (len(admissions) == 1 and admissions[0].get('conclusion') in {'failure', 'cancelled'}
            and all(j.get('conclusion') == 'skipped' for j in writes))
        recovery_stopped = (len(admissions) == 1 and admissions[0].get('conclusion') == 'success'
            and len(migrations) == 1 and migrations[0].get('conclusion') in {'failure', 'cancelled'}
            and len(mutation) == 1 and (mutation[0].get('conclusion') == 'skipped' or
                attested_fast_track_migration_prewrite(repository, prior, jobs, token))
            and all(j.get('conclusion') == 'skipped' for j in publications))
        preflight_noentry = (attested_fast_track_two_attempt_no_sql_preflight(
            repository, prior, token) if prior.get('id') == 38178839491 else False)
        brand_preupload = attested_fast_track_brand_preupload(repository, prior, jobs, token)
        all_attempts_prewrite = (recovery_stopped and fast_track_attempts_proven_prewrite(
            repository, prior, revision, token))
        selector_nonentry = (selection_stopped and type(prior.get('run_attempt')) is int
            and prior['run_attempt'] > 1
            and fast_track_selector_attempts_proven_nonentry(repository, prior, revision, token))
        prewrite = (prior.get('run_attempt') == 1 and
                    (selection_stopped or admission_stopped or recovery_stopped or brand_preupload)) or preflight_noentry or all_attempts_prewrite or selector_nonentry
        completed_sql = (not prewrite and len(admissions) == 1 and admissions[0].get('conclusion') == 'success'
            and fast_track_completed_sql_unpublished(repository, prior, jobs, token))
        unpublished_with_intent = (not prewrite and not completed_sql and
            fast_track_unpublished_apps_with_migration_intent(repository, prior, revision, token))
        if len(writes) < 3 or not (prewrite or completed_sql or unpublished_with_intent):
            raise RuntimeError('FAST_TRACK_PREDECESSOR_REQUIRES_OPERATION_RECONCILIATION')
        pulls = api_get(repository, f'commits/{before}/pulls?per_page=100', token)
        merged = [p for p in pulls if p.get('merged_at') and p.get('merge_commit_sha') == before
            and p.get('base', {}).get('ref') == FAST_TRACK_PR_BASE
            and p.get('head', {}).get('repo', {}).get('full_name') == repository]
        parents = subprocess.check_output(['git', 'rev-list', '--parents', '-n', '1', before], text=True).split()
        if len(merged) != 1 or len(parents) != 3:
            raise RuntimeError('FAST_TRACK_PREDECESSOR_MERGE_UNPROVEN')
        before = parents[1]
    raise RuntimeError('FAST_TRACK_PREDECESSOR_CHAIN_UNPROVEN')


def fast_track_source_admission(*, allow_nonpublishing=False):
    """Require the merged exact source's existing checks before production access."""
    run = (assert_fast_track_release_execution(allow_nonpublishing=True) if allow_nonpublishing
           else assert_fast_track_release_execution())
    repository = os.environ['GITHUB_REPOSITORY']
    token = os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
    return fast_track_merged_source_proof(repository, run['head_sha'], token)


def fast_track_merged_source_proof(repository, revision, token):
    """Existing merged-tree/three-receipt authority shared by exact promotion history."""
    pulls = api_get(repository, f'commits/{revision}/pulls?per_page=100', token)
    candidates = [p for p in pulls if p.get('merged_at') and p.get('merge_commit_sha') == revision
        and p.get('base', {}).get('ref') == FAST_TRACK_PR_BASE
        and p.get('head', {}).get('repo', {}).get('full_name') == repository]
    if len(candidates) != 1:
        raise RuntimeError('FAST_TRACK_MERGED_SOURCE_UNPROVEN')
    candidate = candidates[0]['head']['sha']
    if subprocess.run(['git', 'diff', '--quiet', candidate, revision], capture_output=True).returncode:
        raise RuntimeError('FAST_TRACK_MERGE_TREE_DIFFERS_FROM_VALIDATED_SOURCE')
    payload = api_get(repository, f'actions/runs?head_sha={candidate}&per_page=100', token)
    rows = payload.get('workflow_runs')
    if not isinstance(rows, list) or payload.get('total_count') != len(rows):
        raise RuntimeError('FAST_TRACK_VALIDATION_INVENTORY_INCOMPLETE')
    required = {'approved-release-security-validation.yml': 'pull_request',
        'masterapp-platform-architecture-validation.yml': 'pull_request', 'parfait-isolated-validation.yml': 'pull_request'}
    evidence = {}
    for workflow, event in required.items():
        matches = [r for r in rows if r.get('path') == '.github/workflows/' + workflow
            and r.get('event') == event and r.get('head_sha') == candidate
            and r.get('head_repository', {}).get('full_name') == repository
            and r.get('head_branch') == candidates[0]['head']['ref']]
        latest = max(matches, key=lambda r: r['id']) if matches else None
        if latest is None or latest.get('status') != 'completed' or latest.get('conclusion') != 'success':
            raise RuntimeError('FAST_TRACK_REQUIRED_VALIDATION_UNPROVEN:' + workflow)
        artifact = fast_track_validation_receipt(repository, latest, workflow, candidate, candidates[0]['number'], token)
        evidence[workflow] = dict(runId=latest['id'], attempt=latest['run_attempt'], artifactId=artifact)
    return dict(candidate=candidate, executionAuthority=revision, sourcePr=candidates[0]['number'], validation=evidence)


def fast_track_inventory_merge_proof(repository, revision, token):
    """Admit only the current trusted merge to its existing read-only inventory.

    This is not a candidate ancestry exception or an approval flag. The protected
    merge, exact reviewed source and canonical CI receipts remain the authority.
    """
    workflow = '.github/workflows/parfait-cutover-live-readonly.yml'
    ref = 'refs/heads/' + FAST_TRACK_PR_BASE
    if (not re.fullmatch('[a-f0-9]{40}', revision or '') or not token
        or os.environ.get('GITHUB_SHA') != revision
        or os.environ.get('GITHUB_REPOSITORY') != repository
        or os.environ.get('GITHUB_REF') != ref
        or os.environ.get('GITHUB_EVENT_NAME') != 'workflow_dispatch'
        or os.environ.get('GITHUB_WORKFLOW_REF') != repository + '/' + workflow + '@' + ref
        or subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip() != revision):
        raise RuntimeError('FAST_TRACK_INVENTORY_EXECUTION_CHANGED')
    endpoint = 'git/ref/heads/' + FAST_TRACK_PR_BASE
    if api_get(repository, endpoint, token).get('object', {}).get('sha') != revision:
        raise RuntimeError('FAST_TRACK_INVENTORY_HEAD_CHANGED')
    commit = api_get(repository, 'git/commits/' + revision, token)
    parents = commit.get('parents')
    if (commit.get('sha') != revision or not isinstance(parents, list) or len(parents) != 2
        or any(not isinstance(p, dict) or not re.fullmatch('[a-f0-9]{40}', p.get('sha', '')) for p in parents)):
        raise RuntimeError('FAST_TRACK_INVENTORY_PARENTS_UNPROVEN')
    baseline, source = [p['sha'] for p in parents]
    local = subprocess.check_output(['git', 'rev-list', '--parents', '-n', '1', revision], text=True).split()
    if len({revision, baseline, source}) != 3 or local != [revision, baseline, source]:
        raise RuntimeError('FAST_TRACK_INVENTORY_PARENTS_CHANGED')
    pulls = api_get(repository, f'commits/{revision}/pulls?per_page=100', token)
    if not isinstance(pulls, list) or len(pulls) >= 100:
        raise RuntimeError('FAST_TRACK_INVENTORY_PR_INCOMPLETE')
    matching = [p for p in pulls if p.get('merged_at') and p.get('merge_commit_sha') == revision
        and p.get('base', {}).get('ref') == FAST_TRACK_PR_BASE
        and p.get('base', {}).get('repo', {}).get('full_name') == repository
        and p.get('head', {}).get('repo', {}).get('full_name') == repository
        and p.get('head', {}).get('sha') == source]
    if len(matching) != 1:
        raise RuntimeError('FAST_TRACK_INVENTORY_SOURCE_UNPROVEN')
    trees = subprocess.check_output(['git', 'rev-parse', revision + '^{tree}', source + '^{tree}'], text=True).split()
    tree = commit.get('tree', {}).get('sha')
    if (not re.fullmatch('[a-f0-9]{40}', tree or '') or trees != [tree, tree]
        or subprocess.run(['git', 'merge-base', '--is-ancestor', baseline, source], capture_output=True).returncode):
        raise RuntimeError('FAST_TRACK_INVENTORY_TREE_OR_BASE_CHANGED')
    proof = fast_track_merged_source_proof(repository, revision, token)
    if (proof.get('candidate') != source or proof.get('executionAuthority') != revision
        or proof.get('sourcePr') != matching[0]['number']):
        raise RuntimeError('FAST_TRACK_INVENTORY_VALIDATED_SOURCE_CHANGED')
    # Reject movement while CI/artifact receipts were being authenticated.
    if api_get(repository, endpoint, token).get('object', {}).get('sha') != revision:
        raise RuntimeError('FAST_TRACK_INVENTORY_HEAD_CHANGED')
    return dict(proof, merge=revision, baseline=baseline, tree=tree, authorizesMutation=False)


def approved_head_preflight(repository: str, current_sha: str, token: str, *, base_branch=TRUSTED_PR_BASE):
    """Prove the candidate already contains the exact current approved head.

    This is intentionally cheaper than validation planning. It is the first
    merge-readiness check and may not infer freshness from the PR event's
    possibly stale base SHA.
    """
    if base_branch not in {TRUSTED_PR_BASE, FAST_TRACK_PR_BASE}:
        raise ValueError("Unsupported release base")
    if not re.fullmatch(r"[0-9a-f]{40}", current_sha or ""):
        raise ValueError("Malformed candidate revision")
    if not token:
        raise EvidenceLookupUnavailable(
            "GitHub token unavailable for approved-head preflight",
            endpoint="approved-head-preflight",
        )
    branch = api_get(
        repository,
        "branches/" + urllib.parse.quote(base_branch, safe=""),
        token,
    )
    approved = (branch.get("commit") or {}).get("sha")
    if not re.fullmatch(r"[0-9a-f]{40}", approved or ""):
        raise EvidenceLookupUnavailable(
            "Current approved head is unavailable",
            endpoint="branches/" + base_branch,
        )
    if current_sha == approved:
        return {
            "schemaVersion": 1,
            "candidateSha": current_sha,
            "approvedHeadSha": approved,
            "mergeBaseSha": approved,
            "compareStatus": "identical",
            "current": True,
        }
    compare = api_get(
        repository,
        "compare/" + urllib.parse.quote(approved, safe="") + "..." +
        urllib.parse.quote(current_sha, safe=""),
        token,
    )
    merge_base = (compare.get("merge_base_commit") or {}).get("sha")
    status = compare.get("status")
    current = (
        merge_base == approved and
        status in {"ahead", "identical"}
    )
    return {
        "schemaVersion": 1,
        "candidateSha": current_sha,
        "approvedHeadSha": approved,
        "mergeBaseSha": merge_base,
        "compareStatus": status,
        "current": current,
    }


def cmd_approved_head_preflight(args):
    base_branch = getattr(args, "base_branch", TRUSTED_PR_BASE)
    if args.event != "pull_request":
        result = {
            "schemaVersion": 1,
            "candidateSha": args.current_sha,
            "approvedHeadSha": None,
            "mergeBaseSha": None,
            "compareStatus": "not_applicable",
            "current": True,
            "reason": "approved_head_preflight_applies_to_pull_requests_only",
        }
    else:
        result = approved_head_preflight(
            args.repository,
            args.current_sha,
            os.environ.get("GITHUB_TOKEN", ""),
            base_branch=base_branch,
        )
    if args.output:
        Path(args.output).write_text(
            json.dumps(result, indent=2, sort_keys=True) + "\n"
        )
    print(json.dumps(result, sort_keys=True))
    if not result["current"]:
        print(
            "::error::Candidate is not based on the current "
            f"{base_branch} head {result['approvedHeadSha']}. "
            "Trusted lifecycle must sync the approved head before validation.",
            file=sys.stderr,
        )
        raise SystemExit(78)


class _StepEvidence(dict):
    def __init__(self):
        super().__init__()
        self.producers = {}


def _step_map(jobs):
    evidence = _StepEvidence()
    for job in jobs:
        for step in job.get("steps") or []:
            name = step.get("name")
            if name:
                evidence[name] = step.get("conclusion")
                evidence.producers[name] = {
                    "result": step.get("conclusion"), "jobId": job.get("id"),
                    "runId": job.get("run_id"), "stepNumber": step.get("number"),
                }
    return evidence


def _effective_steps(newest_to_oldest):
    """Keep the newest executed result; skipped/missing later steps do not erase proof.

    A later failure/cancellation always wins over an older success. A later run
    that never reached a gate may inherit that gate's most recent executed result
    from the exact same source SHA only.
    """
    effective = _StepEvidence()
    for steps in newest_to_oldest:
        for name, outcome in steps.items():
            if name in effective or outcome in {None, "", "skipped"}:
                continue
            effective[name] = outcome
            if name in getattr(steps, "producers", {}):
                effective.producers[name] = steps.producers[name]
    return effective


def prior_evidence(args):
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        return None, {}, "github_token_unavailable"

    if args.run_attempt > 1:
        histories = []
        newest_attempt = args.run_attempt - 1
        for attempt in range(newest_attempt, 0, -1):
            payload = api_get(
                args.repository,
                f"actions/runs/{args.current_run_id}/attempts/{attempt}/jobs?per_page=100",
                token,
            )
            histories.append(_step_map(payload.get("jobs", [])))
        return {
            "id": args.current_run_id,
            "head_sha": args.current_sha,
            "run_attempt": newest_attempt,
            "event": args.event,
            "head_branch": args.head_branch,
        }, _effective_steps(histories), "prior_attempts"

    workflow = urllib.parse.quote(args.workflow, safe="")
    branch = urllib.parse.quote(args.head_branch, safe="")
    event = urllib.parse.quote(args.event, safe="")
    payload = api_get(
        args.repository,
        f"actions/workflows/{workflow}/runs?branch={branch}&event={event}&status=completed&per_page=100",
        token,
    )
    runs = []
    for run in payload.get("workflow_runs", []):
        if (
            int(run.get("id", 0)) == args.current_run_id
            or run.get("head_branch") != args.head_branch
            or run.get("event") != args.event
            or not run.get("head_sha")
        ):
            continue
        if args.event == "pull_request":
            trusted = _trusted_lineage_run(
                args.repository, run, WORKFLOW_PATHS[args.workflow], args.current_sha
            )
            if not trusted:
                try:
                    trusted = _trusted_pr_run(
                        args.repository, run, WORKFLOW_PATHS[args.workflow], token
                    )
                except urllib.error.HTTPError:
                    trusted = False
            if not trusted:
                continue
        runs.append(run)
    if not runs:
        # A new isolated PR can reuse exact executed development checks, never
        # their admission scope. Do not search past a newer PR or push failure.
        if args.workflow == 'parfait-isolated-validation.yml' and args.event == 'pull_request':
            payload = api_get(args.repository,
                f'actions/workflows/{workflow}/runs?branch={branch}&event=push&head_sha={args.current_sha}&status=completed&per_page=100', token)
            candidates = payload.get('workflow_runs')
            if not isinstance(candidates, list) or payload.get('total_count') != len(candidates):
                return None, {}, 'development_history_incomplete'
            matches = [run for run in candidates if run.get('event') == 'push'
                and run.get('path') == WORKFLOW_PATHS[args.workflow]
                and run.get('head_repository', {}).get('full_name') == args.repository
                and run.get('head_sha') == args.current_sha and run.get('head_branch') == args.head_branch
                and run.get('status') == 'completed' and type(run.get('id')) is int
                and run['id'] > 0 and type(run.get('run_attempt')) is int and run['run_attempt'] > 0]
            if matches:
                prior = max(matches, key=lambda run: run['id'])
                steps = _historical_plan_steps(args, prior, token, development=True)
                if steps:
                    return prior, steps, 'exact_development_child_receipts'
        return None, {}, "no_prior_completed_run"

    ordered = sorted(
        runs,
        key=lambda run: (run.get("created_at", ""), int(run.get("id", 0))),
        reverse=True,
    )
    prior = ordered[0]
    exact_head_runs = [run for run in ordered if run.get("head_sha") == prior.get("head_sha")][:10]
    histories = []
    for run in exact_head_runs:
        steps = _historical_plan_steps(args, run, token)
        if steps:
            histories.append(steps)

    return prior, _effective_steps(histories), "prior_exact_head_plan_artifacts"


def _plan_against_prior(workflow, current_sha, prior, prior_steps, evidence_source):
    """Build the same fail-closed gate plan against one exact historical source tree."""
    changed = git_changed(prior["head_sha"], current_sha) if prior else []
    changed_gate_steps = set()
    workflow_structure_changed = False
    workflow_path = WORKFLOW_PATHS.get(workflow)
    if prior and workflow_path and workflow_path in changed:
        prior_text = git_show_file(prior["head_sha"], workflow_path)
        current_text = Path(workflow_path).read_text()
        try:
            prior_config = _historical_workflow_definition(workflow, prior["head_sha"])
            changed_gate_steps = {
                gate["step"] for key, gate in WORKFLOWS[workflow]["gates"].items()
                if key not in prior_config["gates"] or
                _gate_execution_contract(prior_text, prior_config, key) !=
                _gate_execution_contract(current_text, WORKFLOWS[workflow], key)
            }
        except EvidenceLookupUnavailable:
            raise
        except Exception:
            workflow_structure_changed = True
        changed = [path for path in changed if path != workflow_path]
    plan = compute_plan(
        workflow,
        current_sha,
        prior,
        prior_steps,
        changed,
        evidence_source,
        changed_gate_steps,
        workflow_structure_changed,
    )

    if any(not gate.get("run") for gate in plan["gates"].values()):
        try:
            current_ids = gate_dependency_manifests(workflow, current_sha)
            prior_ids = gate_dependency_manifests(workflow, prior["head_sha"],
                _historical_workflow_definition(workflow, prior["head_sha"]))
            for key, gate in plan["gates"].items():
                if gate.get("run"):
                    continue
                if current_ids[key]["contentIdentity"] != prior_ids.get(key, {}).get("contentIdentity"):
                    gate.update({"run": True, "reason": "dependency_identity_changed"})
                else:
                    gate["dependencyIdentity"] = current_ids[key]["contentIdentity"]
        except EvidenceLookupUnavailable:
            raise
        except Exception:
            for gate in plan["gates"].values():
                if not gate.get("run"):
                    gate.update({"run": True, "reason": "dependency_identity_unproven"})
        _enforce_runtime_requirements(plan)
    for gate in plan["gates"].values():
        if not gate.get("run"):
            gate["producerReceipt"] = getattr(prior_steps, "producers", {}).get(gate["step"],
                {"result": prior_steps.get(gate["step"]), "runId": prior["id"]})
    return plan


def _stamp_evidence(plan, prior, source):
    if not prior:
        return
    for gate in plan.get("gates", {}).values():
        if gate.get("run"):
            continue
        gate["evidenceRunId"] = (gate.get("producerReceipt") or {}).get("runId") or prior.get("id")
        gate["evidenceHeadSha"] = prior.get("head_sha")
        gate["evidenceSource"] = source


def _enforce_runtime_requirements(plan):
    """Preserved proof cannot replace a prerequisite needed by a gate executing now."""
    gates = WORKFLOWS[plan["workflow"]]["gates"]
    changed = True
    while changed:
        changed = False
        for key, result in list(plan["gates"].items()):
            invalidated = next((child for child in gates[key].get("consumes", ())
                                if plan["gates"][child].get("run")), None)
            if invalidated and not result.get("run"):
                result.update({"run": True, "reason": f"evidence_dependency_invalidated:{invalidated}"})
                changed = True
            if not result.get("run"):
                continue
            for required in gates[key].get("requires", ()):
                required_result = plan["gates"][required]
                if required_result.get("run"):
                    continue
                required_result["run"] = True
                required_result["reason"] = f"required_by:{key}"
                required_result.pop("evidenceRunId", None)
                required_result.pop("evidenceHeadSha", None)
                required_result.pop("evidenceSource", None)
                changed = True


def merge_content_equivalent_evidence(plan, candidate_plan, run):
    """Reuse only gates whose exact declared inputs and gate definition are unchanged."""
    reused = False
    for key, result in plan["gates"].items():
        if not result.get("run"):
            continue
        candidate = candidate_plan["gates"].get(key)
        if not candidate or candidate.get("run"):
            continue
        result["run"] = False
        result["reason"] = "content_equivalent_success"
        result["evidenceRunId"] = run.get("id")
        result["evidenceHeadSha"] = run.get("head_sha")
        result["evidenceSource"] = "trusted_pr_history"
        result["producerReceipt"] = candidate.get("producerReceipt")
        result["dependencyIdentity"] = candidate.get("dependencyIdentity")
        reused = True
    return reused


def _trusted_lineage_run(repository, run, workflow_path, revision, *, require_completed=True):
    """Authenticate an immutable producer already in the current candidate lineage.

    This avoids a separate commit->pull API lookup for evidence whose producer
    commit is already part of the exact candidate history. Workflow path, event,
    completion, repository ownership and git ancestry must all agree.
    """
    if _VALIDATION_PR_BASE.get() == FAST_TRACK_PR_BASE and not any(
        (pr.get("base") or {}).get("ref") == FAST_TRACK_PR_BASE
        for pr in run.get("pull_requests") or []):
        return False
    head = run.get("head_sha") or ""
    if (
        run.get("path") != workflow_path
        or run.get("event") != "pull_request"
        or (require_completed and run.get("status") != "completed")
        or not re.fullmatch(r"[0-9a-f]{40}", head)
        or (run.get("head_repository") or {}).get("full_name") != repository
        or not re.fullmatch(r"[0-9a-f]{40}", revision or "")
    ):
        return False
    return subprocess.run(
        ["git", "merge-base", "--is-ancestor", head, revision],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    ).returncode == 0


def _trusted_pr_run(repository, run, workflow_path, token):
    """Authenticate a producer independently of its parent workflow conclusion."""
    head = run.get("head_sha") or ""
    if (run.get("path") != workflow_path or run.get("event") != "pull_request"
            or run.get("status") != "completed"
            or not re.fullmatch(r"[0-9a-f]{40}", head)
            or (run.get("head_repository") or {}).get("full_name") != repository):
        return False
    def matches_pull(row):
        return ((row.get("base") or {}).get("ref") == _VALIDATION_PR_BASE.get()
                and (row.get("head") or {}).get("sha") == head
                and ((row.get("head") or {}).get("repo") or {}).get("full_name") == repository)
    if any(matches_pull(row) for row in run.get("pull_requests") or []):
        return True
    # The commit-associated PR endpoint binds the immutable commit even after
    # that PR advances to a repaired head; demanding its current head equal the
    # old producer SHA would discard valid child evidence on every new commit.
    return any((row.get("base") or {}).get("ref") == _VALIDATION_PR_BASE.get()
               and ((row.get("head") or {}).get("repo") or {}).get("full_name") == repository
               for row in api_get(repository, f"commits/{head}/pulls?per_page=100", token))


VALIDATION_RESUME_ARTIFACT_PREFIX = {
    "masterapp-platform-architecture-validation.yml": "validation-resume-architecture",
    "approved-release-security-validation.yml": "validation-resume-security",
    "parfait-isolated-validation.yml": "validation-resume-parfait",
    "step6-openai-ads-execution-validation.yml": "validation-resume-step6",
    "steps7-8-governed-advertising-validation.yml": "validation-resume-step78",
}


def _validation_resume_artifact_name(workflow, run):
    prefix = VALIDATION_RESUME_ARTIFACT_PREFIX.get(workflow)
    if not prefix:
        return None
    run_id = int(run.get("id") or 0)
    attempt = int(run.get("run_attempt") or 1)
    return f"{prefix}-{run_id}-{attempt}" if run_id else None


def _parfait_unconditional_validation_generation(workflow, revision):
    if workflow != 'parfait-isolated-validation.yml':
        return False
    source = git_show_file(revision, WORKFLOW_PATHS[workflow]).encode()
    blob = hashlib.sha1(b'blob ' + str(len(source)).encode() + b'\0' + source).hexdigest()
    # Reviewed original source: every named gate executes unconditionally.
    # Authenticate actual successful steps, never infer proof from this hash.
    return blob == '20b242ae2b01d1091c746801f6f0144428912cda'


def _historical_plan_steps(args, run, token, *, development=False):
    """Recover gate proof from the durable validation-plan artifact.

    Successful parent completion proves gates the plan actually executed.
    A failed parent requires an exact recorded child observation for executed
    gates, or preserved proof from an older successful producer.
    """
    artifact = _validation_resume_artifact_name(args.workflow, run)
    if artifact is None:
        return None
    run_id = int(run.get("id") or 0)
    artifact_id = None
    if development:
        inventory = api_get(args.repository, f'actions/runs/{run_id}/artifacts?per_page=100', token)
        rows = inventory.get('artifacts')
        if not isinstance(rows, list) or inventory.get('total_count') != len(rows):
            return None
        matches = [row for row in rows if row.get('name') == artifact and row.get('expired') is False]
        if len(matches) != 1 or matches[0].get('workflow_run', {}).get('id') != run_id:
            return None
        artifact_id = matches[0]['id']
    elif artifact not in _run_artifact_names(args.repository, run_id, token):
        if (run.get('path') == WORKFLOW_PATHS.get(args.workflow)
            and run.get('head_repository', {}).get('full_name') == args.repository
            and run.get('event') == 'push' and run.get('head_branch') == args.head_branch
            and _parfait_unconditional_validation_generation(args.workflow, run['head_sha'])):
            payload = api_get(args.repository, f"actions/runs/{run_id}/attempts/{run.get('run_attempt', 1)}/jobs?per_page=100", token)
            jobs = payload.get('jobs')
            if not isinstance(jobs, list) or payload.get('total_count') != len(jobs):
                return None
            return _step_map(jobs)
        return None
    import tempfile
    with tempfile.TemporaryDirectory(prefix="validation-plan-") as temporary:
        directory = Path(temporary)
        if development:
            _download_run_artifact(args.repository, run_id, artifact, directory, artifact_id=artifact_id)
        else:
            _download_run_artifact(args.repository, run_id, artifact, directory)
        candidates = list(directory.rglob("validation-resume.json"))
        if len(candidates) != 1:
            return None
        stored = json.loads(candidates[0].read_text())

    if stored.get("workflow") != args.workflow:
        return None
    if development:
        scope = stored.get('developmentValidationScope')
        if (not isinstance(scope, dict) or 'sourceValidationScope' in stored
            or stored.get('currentSha') != args.current_sha or stored.get('validationBase') != FAST_TRACK_PR_BASE
            or scope != dict(phase='development-validation-only', releaseAdmission=False,
                candidate=args.current_sha, branch=args.head_branch, runId=run_id, runAttempt=run['run_attempt'])):
            return None
    gates = stored.get("gates")
    if not isinstance(gates, dict):
        return None

    steps = _StepEvidence()
    parent_success = run.get("conclusion") == "success"
    for gate in gates.values():
        if not isinstance(gate, dict):
            continue
        step = gate.get("step")
        if not isinstance(step, str) or not step:
            continue
        if gate.get("run") is True:
            receipt = gate.get("receipt") or {}
            job_id = receipt.get("producerJobId")
            step_number = receipt.get("producerStepNumber")
            if (
                stored.get("receiptSchemaVersion") == 1
                and stored.get("recordingRunId") == run_id
                and receipt.get("producingRunId") == run_id
                and receipt.get("reused") is False
                and type(job_id) is int and job_id > 0
                and receipt.get("recordingJobId") == job_id
                and type(step_number) is int and step_number > 0
                and receipt.get("stepNumber") == step_number
                and receipt.get("result") in {"success", "failure", "cancelled", "timed_out"}
            ):
                steps[step] = receipt["result"]
                steps.producers[step] = {
                    "result": receipt["result"],
                    "jobId": job_id,
                    "runId": run_id,
                    "stepNumber": step_number,
                    "artifact": artifact,
                }
                continue
            if development or not parent_success:
                continue
            steps[step] = "success"
            steps.producers[step] = {
                "result": "success",
                "jobId": None,
                "runId": run_id,
                "stepNumber": None,
                "artifact": artifact,
            }
            continue

        if development:
            # Cross-event reuse is limited to observed execution in this producer.
            continue
        producer = gate.get("receipt") or gate.get("producerReceipt") or {}
        if producer.get("result") != "success":
            continue
        producer_run = (
            producer.get("producingRunId")
            or producer.get("runId")
            or gate.get("evidenceRunId")
        )
        if not producer_run:
            continue
        steps[step] = "success"
        steps.producers[step] = {
            "result": "success",
            "jobId": producer.get("producerJobId") or producer.get("jobId"),
            "runId": int(producer_run),
            "stepNumber": producer.get("producerStepNumber") or producer.get("stepNumber"),
            "artifact": artifact,
        }
    return steps


def _trusted_historical_runs(args, token):
    """Return trusted completed PR parents; each child proves its own success."""
    if args.event != "pull_request":
        return []
    workflow = urllib.parse.quote(args.workflow, safe="")
    payload = api_get(
        args.repository,
        f"actions/workflows/{workflow}/runs?event=pull_request&status=completed&per_page=100",
        token,
    )
    candidates = sorted(
        payload.get("workflow_runs", []),
        key=lambda run: (run.get("updated_at") or run.get("created_at", ""), int(run.get("id", 0))),
        reverse=True,
    )
    rows = []
    for run in candidates:
        if int(run.get("id", 0)) == args.current_run_id:
            continue
        trusted = _trusted_lineage_run(
            args.repository, run, WORKFLOW_PATHS[args.workflow], args.current_sha
        )
        if not trusted:
            try:
                trusted = _trusted_pr_run(
                    args.repository, run, WORKFLOW_PATHS[args.workflow], token
                )
            except urllib.error.HTTPError:
                trusted = False
        if not trusted:
            continue
        rows.append(run)
        # Newer failed parents may salvage exact successful children. Once the
        # nearest trusted successful parent is reached, it is the canonical
        # content baseline; older generations cannot override newer source truth.
        if run.get("conclusion") == "success":
            break
    return rows


def _successful_parent_steps(workflow, run):
    """A successful PR validator proves every gate in that exact parent envelope.

    Individual artifacts are only needed to salvage green children from failed
    parents. Avoiding artifact downloads for successful parents turns history
    reuse into one content-identity comparison instead of an N-run network scan.
    """
    steps = _StepEvidence()
    run_id = int(run.get("id") or 0)
    if run.get("conclusion") != "success" or run_id < 1:
        return steps
    for gate in WORKFLOWS[workflow]["gates"].values():
        step = gate["step"]
        steps[step] = "success"
        steps.producers[step] = {
            "result": "success",
            "jobId": None,
            "runId": run_id,
            "stepNumber": None,
            "artifact": None,
        }
    return steps


def _apply_content_equivalent_evidence(args, plan):
    """Fill unresolved gates from recent successful runs with identical gate inputs.

    This is intentionally an optimization only. Any lookup/diff uncertainty keeps
    the existing plan unchanged, so historical reuse can never weaken validation.
    """
    if args.event != "pull_request" or not any(row.get("run") for row in plan["gates"].values()):
        return plan
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        return plan

    reused = False
    examined = 0
    try:
        runs = _trusted_historical_runs(args, token)
    except EvidenceLookupUnavailable:
        raise
    except Exception as exc:
        plan["historicalEvidenceError"] = type(exc).__name__
        return plan

    for run in runs:
        head_sha = run["head_sha"]
        successful_parent = run.get("conclusion") == "success"
        try:
            steps = (
                _successful_parent_steps(args.workflow, run)
                if successful_parent
                else _historical_plan_steps(args, run, token)
            )
            if not steps:
                examined += 1
                continue
            prior = {
                "id": run["id"],
                "head_sha": head_sha,
                "run_attempt": run.get("run_attempt", 1),
                "event": run.get("event"),
                "head_branch": run.get("head_branch"),
            }
            candidate_plan = _plan_against_prior(
                args.workflow,
                args.current_sha,
                prior,
                steps,
                "trusted_successful_parent" if successful_parent else "trusted_plan_artifact_history",
            )
        except EvidenceLookupUnavailable:
            raise
        except Exception:
            examined += 1
            continue
        examined += 1
        reused = merge_content_equivalent_evidence(plan, candidate_plan, run) or reused
        if not any(row.get("run") for row in plan["gates"].values()):
            break
        # The nearest trusted successful parent is the canonical comparison
        # baseline. Anything whose content identity changed from it is genuinely
        # invalidated and must execute; older generations cannot make a current
        # source change disappear.
        if successful_parent:
            break

    _enforce_runtime_requirements(plan)
    if reused:
        plan["mode"] = "content-addressed"
        plan["historicalEvidenceRunsExamined"] = examined
    return plan


def compute_plan(
    workflow: str,
    current_sha: str,
    prior,
    prior_steps,
    changed_paths,
    evidence_source,
    changed_gate_steps=None,
    workflow_structure_changed=False,
):
    config = WORKFLOWS[workflow]
    gates = config["gates"]
    plan = {
        "schemaVersion": 1,
        "workflow": workflow,
        "currentSha": current_sha,
        "priorRunId": prior.get("id") if prior else None,
        "priorHeadSha": prior.get("head_sha") if prior else None,
        "evidenceSource": evidence_source,
        "changedPaths": changed_paths,
        "mode": "full",
        "gates": {},
    }

    if prior is None:
        for key, gate in gates.items():
            plan["gates"][key] = {
                "step": gate["step"],
                "run": True,
                "reason": "no_prior_success_evidence",
            }
        return plan

    if workflow_structure_changed:
        for key, gate in gates.items():
            plan["gates"][key] = {
                "step": gate["step"],
                "run": True,
                "reason": "workflow_structure_changed",
            }
        return plan

    changed_gate_steps = set(changed_gate_steps or ())
    force_all = any(matches(path, config.get("force_all", ())) for path in changed_paths)
    known = set()
    neutral = set()
    for path in changed_paths:
        if matches(path, config.get("neutral", ())):
            neutral.add(path)
            continue
        for key, gate in gates.items():
            if gate_matches(path, gate):
                known.add(path)
                break

    unknown = [] if config.get("unmatched_neutral") else [
        path
        for path in changed_paths
        if path not in known
        and path not in neutral
        and not matches(path, config.get("force_all", ()))
    ]
    if force_all or unknown:
        reason = "source_change_requires_full_validation" if force_all else "unclassified_change_requires_full_validation"
        for key, gate in gates.items():
            plan["gates"][key] = {
                "step": gate["step"],
                "run": True,
                "reason": reason,
            }
        plan["unclassifiedPaths"] = unknown
        return plan

    run = set()
    reasons = {}
    for key, gate in gates.items():
        step = gate["step"]
        if step in changed_gate_steps:
            run.add(key)
            reasons[key] = "gate_definition_changed"
            continue
        if prior_steps.get(step) != "success":
            run.add(key)
            reasons[key] = "prior_gate_not_successful"
            continue
        if any(gate_matches(path, gate) for path in changed_paths):
            run.add(key)
            reasons[key] = "gate_inputs_changed"

    # Close only real execution/evidence edges. "requires" is a runtime
    # prerequisite and "consumes" is a semantic evidence dependency: both
    # legitimately propagate invalidation. "materializes" is deliberately
    # different; it means a child needs exact producer bytes locally, but a
    # producer rematerialization alone must not invalidate otherwise-green
    # sibling evidence.
    changed = True
    while changed:
        changed = False
        for key in list(run):
            for required in gates[key].get("requires", ()):
                if required not in run:
                    run.add(required)
                    reasons[required] = f"required_by:{key}"
                    changed = True
        for key, gate in gates.items():
            if key in run:
                continue
            invalidated = next(
                (dependency for dependency in gate.get("consumes", ()) if dependency in run),
                None,
            )
            if invalidated:
                run.add(key)
                reasons[key] = f"evidence_dependency_invalidated:{invalidated}"
                changed = True

    for key, gate in gates.items():
        should_run = key in run
        plan["gates"][key] = {
            "step": gate["step"],
            "run": should_run,
            "reason": reasons.get(key, "preserved_prior_success"),
        }
    plan["mode"] = "incremental"
    return plan


def _workflow_top_level_field(text, field):
    """Include block and inline YAML forms in the execution envelope."""
    lines = text.splitlines(keepends=True)
    starts = [i for i, line in enumerate(lines) if re.match(r"^[A-Za-z_][A-Za-z0-9_-]*:", line)]
    for index, start in enumerate(starts):
        if lines[start].startswith(field + ":"):
            end = starts[index + 1] if index + 1 < len(starts) else len(lines)
            return "".join(lines[start:end])
    return ""


def _gate_execution_contract(workflow_text, config, key):
    """Capture one gate's execution prefix and declared runtime prerequisites."""
    gates = config["gates"]
    step = gates[key]["step"]
    owners = [job for job in _job_blocks(workflow_text).values() if step in named_step_blocks(job)]
    if len(owners) != 1:
        raise ValueError("Gate does not have one executable workflow owner")
    job = owners[0]
    lines, spans = _named_step_spans(job)
    end = next(end for name, start, end in spans if name == step)
    prefix = "".join(lines[:end])
    dependencies = {key}
    pending = [key]
    while pending:
        dependency = pending.pop()
        for required in gates[dependency].get("requires", ()):
            if required not in dependencies:
                dependencies.add(required)
                pending.append(required)
    unrelated_steps = {gate["step"] for name, gate in gates.items() if name not in dependencies}
    # Named independent gates are not execution inputs of this gate. Unknown
    # setup/source-mutating commands and all unnamed steps remain fail closed.
    prefix_lines, prefix_spans = _named_step_spans(prefix)
    omitted = {index for name, start, finish in prefix_spans if name in unrelated_steps
               for index in range(start, finish)}
    prefix = "".join(line for index, line in enumerate(prefix_lines) if index not in omitted)
    if config.get('resume_owner') == 'parfait-isolated-validation':
        blocks = named_step_blocks(prefix)
        planner = blocks.get('Plan exact validation resume', '')
        if hashlib.sha256(planner.encode()).hexdigest() == config.get('resume_planner_sha256'):
            prefix = prefix.replace(planner, '', 1)
        # Fetch depth changes history availability, not the checked-out tree.
        prefix = re.sub(r'^ +fetch-depth: 0\n', '', prefix, flags=re.M)
        for name, definition in gates.items():
            block = named_step_blocks(prefix).get(definition['step'])
            if block is None: continue
            normalized = block.replace('        id: gate_' + name.replace('-', '_') + '\n', '')
            condition = "        if: ${{ fromJSON(steps.resume.outputs.plan).gates['" + name + "'].run }}\n"
            normalized = normalized.replace(condition, '')
            prefix = prefix.replace(block, normalized, 1)
        prefix = prefix.rstrip() + '\n'
    envelope = []
    for field in ("env", "defaults"):
        envelope.append(_workflow_top_level_field(workflow_text, field))
    return "".join(envelope) + prefix


def _historical_workflow_definition(workflow, revision):
    """Read historical declarative gate data without executing historical code."""
    values = {}
    def data(node):
        if isinstance(node, ast.Constant):
            return node.value
        if isinstance(node, ast.Name):
            return values[node.id]
        if isinstance(node, (ast.Tuple, ast.List, ast.Set)):
            rows = [data(child) for child in node.elts]
            return tuple(rows) if isinstance(node, ast.Tuple) else rows
        if isinstance(node, ast.Dict):
            return {data(key): data(value) for key, value in zip(node.keys, node.values)}
        if isinstance(node, ast.BinOp) and isinstance(node.op, ast.Add):
            return data(node.left) + data(node.right)
        raise ValueError("Historical gate definition is not declarative")
    module = ast.parse(git_show_file(revision, "scripts/validation-resume.py"))
    for statement in module.body:
        if isinstance(statement, ast.Assign) and len(statement.targets) == 1 and isinstance(statement.targets[0], ast.Name):
            name = statement.targets[0].id
            try:
                values[name] = data(statement.value)
            except (ValueError, KeyError, TypeError):
                if name == "WORKFLOWS":
                    raise ValueError("Cannot authenticate historical gate definition")
    if workflow not in values.get('WORKFLOWS', {}) and _parfait_unconditional_validation_generation(workflow, revision):
        return WORKFLOWS[workflow]
    return values["WORKFLOWS"][workflow]


def gate_dependency_manifests(workflow, revision, definition=None):
    exact = subprocess.run(["git", "rev-parse", revision + "^{commit}"], check=True, capture_output=True, text=True).stdout.strip()
    return _gate_dependency_manifests(workflow, exact, json.dumps(definition or WORKFLOWS[workflow], sort_keys=True))


@functools.lru_cache(maxsize=128)
def _gate_dependency_manifests(workflow, revision, definition_json):
    """Materialize the existing gate model into auditable content identities.

    These manifests accompany the canonical decision, never substitute a new
    gate registry. Executable workflow/toolchain policy is recorded separately
    from application content; commit identity is provenance, not invalidation.
    """
    config = json.loads(definition_json)
    raw = subprocess.run(["git", "ls-tree", "-r", "-z", revision],
        check=True, capture_output=True).stdout.decode()
    tree = {}
    for entry in raw.split("\0"):
        if entry:
            meta, path = entry.split("\t", 1)
            mode, kind, oid = meta.split()
            tree[path] = {"mode": mode, "kind": kind, "oid": oid}
    workflow_text = git_show_file(revision, WORKFLOW_PATHS[workflow])
    gate_steps = [gate["step"] for gate in config["gates"].values()]
    import io
    import tarfile
    test_tokens = {}
    test_file_patterns = {}
    if any(path.startswith("AgentPortal.Tests/") for path in tree):
        archive = subprocess.run(["git", "archive", revision, "AgentPortal.Tests"], check=True, capture_output=True).stdout
        with tarfile.open(fileobj=io.BytesIO(archive)) as contents:
            for entry in contents.getmembers():
                if entry.isfile() and entry.name.endswith(".cs"):
                    source = contents.extractfile(entry).read().decode("utf-8-sig")
                    test_tokens[entry.name] = set(re.findall(r"[A-Za-z0-9_.-]+", source))
                    test_file_patterns[entry.name] = _test_file_dependency_patterns(source)
    import posixpath
    import xml.etree.ElementTree as ET
    copied_inputs = {}
    project_path = "AgentPortal.Tests/AgentPortal.Tests.csproj"
    if project_path in tree:
        project = ET.fromstring(git_show_file(revision, project_path))
        for node in project.iter():
            include = node.get("Include", "").replace("\\", "/")
            if include and (node.get("CopyToOutputDirectory") or node.find("CopyToOutputDirectory") is not None):
                path = posixpath.normpath("AgentPortal.Tests/" + include)
                link = node.get("Link") or node.findtext("Link") or posixpath.basename(path)
                copied_inputs[path] = posixpath.basename(link)
    result = {}
    for key, gate in config["gates"].items():
        inputs = {path: identity for path, identity in tree.items()
                  if gate_matches(path, gate) or matches(path, config.get("force_all", ()))}
        # Source-contract tests read copied and direct repository files. Preserve
        # those content inputs alongside the C# fixture, even for neutral owners.
        tokens = set()
        dynamic_patterns = set()
        if gate.get("runtime_file_dependencies", True):
            for path in tuple(inputs):
                if path.startswith("AgentPortal.Tests/") and path.endswith(".cs"):
                    tokens.update(test_tokens.get(path, ()))
                    dynamic_patterns.update(test_file_patterns.get(path, ()))
            if tokens:
                inputs.update({path: identity for path, identity in tree.items()
                               if Path(path).name in tokens})
                inputs.update({path: tree[path] for path, alias in copied_inputs.items()
                               if path in tree and alias in tokens})
            if dynamic_patterns:
                inputs.update({path: identity for path, identity in tree.items() if matches(path, dynamic_patterns)})
        source_digest = hashlib.sha256(json.dumps(inputs, sort_keys=True).encode()).hexdigest()
        control = _gate_execution_contract(workflow_text, config, key)
        control_digest = hashlib.sha256(control.encode()).hexdigest()
        definition_digest = hashlib.sha256(json.dumps(gate, sort_keys=True).encode()).hexdigest()
        result[key] = {
            "schemaVersion": 1, "unitId": workflow + ":" + key,
            "parentId": workflow, "sourceRevision": revision,
            "sourceInputs": inputs, "sourceIdentity": source_digest,
            "controlPlanePath": WORKFLOW_PATHS[workflow],
            "executionContractIdentity": control_digest,
            "gateDefinitionIdentity": definition_digest,
            "contentIdentity": hashlib.sha256((source_digest + control_digest + definition_digest).encode()).hexdigest(),
            "requires": list(gate.get("requires", ())),
            "consumes": list(gate.get("consumes", ())),
            "materializes": list(gate.get("materializes", ())),
            "toolchainPolicy": "producer-execution-contract",
        }
    return result


def _gate_cache_payload(plan, workflow, head_sha, run_id, run_attempt):
    if workflow not in WORKFLOWS or plan.get("workflow") != workflow:
        raise ValueError("Validation cache workflow mismatch")
    if not re.fullmatch(r"[0-9a-f]{40}", head_sha or "") or run_id < 1 or run_attempt < 1:
        raise ValueError("Malformed validation cache identity")
    gates = {}
    for key, row in (plan.get("gates") or {}).items():
        if key not in WORKFLOWS[workflow]["gates"] or not isinstance(row, dict):
            continue
        receipt = row.get("receipt") or row.get("producerReceipt") or {}
        if receipt.get("result") == "success":
            gates[key] = {"result": "success"}
    return {
        "schemaVersion": 2,
        "workflow": workflow,
        "headSha": head_sha,
        "runId": run_id,
        "runAttempt": run_attempt,
        "validationBase": plan.get("validationBase", TRUSTED_PR_BASE),
        "gates": gates,
    }


def _write_gate_cache(plan, workflow, head_sha, run_id, run_attempt, output):
    payload = _gate_cache_payload(plan, workflow, head_sha, run_id, run_attempt)
    path = Path(output)
    if not payload["gates"]:
        if path.exists():
            path.unlink()
        return payload
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(payload, indent=2, sort_keys=True) + "\n")
    temporary.replace(path)
    return payload


def cmd_record_evidence(args):
    """Enrich the plan and checkpoint successful children even if the parent later fails."""
    plan = json.loads(Path(args.plan).read_text())
    observed = {}
    local_context = os.environ.get("VALIDATION_STEP_CONTEXT")

    if local_context:
        context = json.loads(local_context)
        if not isinstance(context, dict):
            raise ValueError("Validation step context must be an object")
        for key in WORKFLOWS[plan["workflow"]]["gates"]:
            step_id = "gate_" + key.replace("-", "_")
            row = context.get(step_id) or {}
            outcome = row.get("outcome") or row.get("conclusion")
            if outcome:
                observed[WORKFLOWS[plan["workflow"]]["gates"][key]["step"]] = {
                    "result": outcome,
                    "jobId": None,
                    "stepNumber": None,
                }
    else:
        token = os.environ.get("GITHUB_TOKEN", "")
        if not token:
            raise ValueError("Evidence recording requires authenticated producer observations")
        try:
            jobs = api_get(args.repository, f"actions/runs/{args.run_id}/jobs?filter=latest&per_page=100", token).get("jobs", [])
        except (urllib.error.HTTPError, EvidenceLookupUnavailable) as exc:
            if exc.code != 403:
                raise
            plan["receiptSchemaVersion"] = 1
            plan["recordingRunId"] = args.run_id
            plan["receiptRecordingDeferred"] = "current_run_actions_observation_forbidden"
            Path(args.output).write_text(json.dumps(plan, indent=2, sort_keys=True) + "\n")
            print("Deferred current-run child receipt enrichment; completed-run evidence remains canonical.")
            return
        for job in jobs:
            for step in job.get("steps", []):
                if step.get("conclusion"):
                    observed[step.get("name")] = {
                        "result": step["conclusion"],
                        "jobId": job.get("id"),
                        "stepNumber": step.get("number"),
                    }

    runtime = {"python": sys.version.split()[0]}
    for tool in ("dotnet", "node"):
        try:
            observation = subprocess.run([tool, "--version"], capture_output=True, text=True, timeout=20)
            if observation.returncode == 0:
                runtime[tool] = observation.stdout.strip()
        except (OSError, subprocess.TimeoutExpired):
            pass

    plan["receiptSchemaVersion"] = 1
    plan["recordingRunId"] = args.run_id
    for key, gate in plan.get("gates", {}).items():
        observation = observed.get(gate["step"], {})
        producer = gate.get("producerReceipt") or {}
        gate["receipt"] = {
            "result": observation.get("result", "unproven") if gate.get("run") else producer.get("result", "unproven"),
            "producerJobId": observation.get("jobId") if gate.get("run") else producer.get("jobId"),
            "producerStepNumber": observation.get("stepNumber") if gate.get("run") else producer.get("stepNumber"),
            "producingRunId": args.run_id if gate.get("run") else gate.get("evidenceRunId", plan.get("priorRunId")),
            "recordingJobId": observation.get("jobId"),
            "stepNumber": observation.get("stepNumber"),
            "reused": not gate.get("run"),
            "actualToolchain": runtime if gate.get("run") else None,
            "toolchainPolicy": "observed-producer" if gate.get("run") else "preserved-producer-contract",
        }

    Path(args.output).write_text(json.dumps(plan, indent=2, sort_keys=True) + "\n")
    cache_output = getattr(args, "cache_output", None)
    if cache_output:
        payload = _write_gate_cache(
            plan,
            plan["workflow"],
            plan["currentSha"],
            args.run_id,
            int(os.environ.get("GITHUB_RUN_ATTEMPT", "1")),
            cache_output,
        )
        print(json.dumps({"cachedSuccessfulGates": sorted(payload["gates"])}, sort_keys=True))



def _stop_unresolved_planning(args, exc):
    """Non-evidence planner defects remain blocking; never invent success."""
    record = {"schemaVersion": 2, "mode": "blocked",
              "reason": "planner_unavailable_resume_planning_only",
              "plannerError": type(exc).__name__}
    if isinstance(exc, EvidenceLookupUnavailable):
        record["evidenceHttpStatus"] = exc.code
        record["evidenceEndpoint"] = exc.endpoint
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(record, indent=2, sort_keys=True) + "\n")
    print(json.dumps(record, sort_keys=True))
    raise SystemExit(1)


def _fresh_plan_when_evidence_unavailable(args, exc):
    """Historical evidence is optional optimization; unavailable proof means run fresh."""
    plan = compute_plan(
        args.workflow,
        args.current_sha,
        None,
        {},
        [],
        "evidence_unavailable_fresh_validation",
    )
    plan["schemaVersion"] = 2
    plan["evidenceFallback"] = {
        "reason": "historical_evidence_unavailable_run_fresh",
        "error": type(exc).__name__,
        "httpStatus": exc.code,
        "endpoint": exc.endpoint,
    }
    plan["dependencyManifests"] = gate_dependency_manifests(args.workflow, args.current_sha)
    return plan


def _cached_success_evidence(args):
    """Use PR-local gate evidence before any remote history lookup.

    Schema 1 is the older all-parent-success capsule. Schema 2 is the canonical
    child ledger: only gates explicitly checkpointed successful are reusable.
    Git ancestry plus _plan_against_prior still revalidates source, execution and
    dependency identity before any cached success can suppress work.
    """
    raw = getattr(args, "resume_cache", None)
    if not raw:
        return None
    path = Path(raw)
    if not path.is_file():
        return None
    data = json.loads(path.read_text())
    if (
        data.get("workflow") != args.workflow
        or not re.fullmatch(r"[0-9a-f]{40}", data.get("headSha") or "")
        or type(data.get("runId")) is not int
        or data["runId"] < 1
    ):
        raise ValueError("Malformed PR-local validation cache")
    if data.get("validationBase", TRUSTED_PR_BASE) != _VALIDATION_PR_BASE.get():
        return None
    prior_sha = data["headSha"]
    if subprocess.run(
        ["git", "merge-base", "--is-ancestor", prior_sha, args.current_sha],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    ).returncode:
        raise ValueError("PR-local validation cache is not in the current candidate lineage")

    steps = _StepEvidence()
    schema = data.get("schemaVersion")
    if schema == 1:
        successful = set(WORKFLOWS[args.workflow]["gates"])
    elif schema == 2:
        rows = data.get("gates")
        if not isinstance(rows, dict):
            raise ValueError("Malformed PR-local child gate ledger")
        successful = {
            key for key, row in rows.items()
            if key in WORKFLOWS[args.workflow]["gates"]
            and isinstance(row, dict)
            and row.get("result") == "success"
        }
    else:
        raise ValueError("Unsupported PR-local validation cache schema")

    for key in successful:
        gate = WORKFLOWS[args.workflow]["gates"][key]
        step = gate["step"]
        steps[step] = "success"
        steps.producers[step] = {
            "result": "success",
            "jobId": None,
            "runId": data["runId"],
            "stepNumber": None,
            "artifact": "pr-local-gate-cache",
        }
    if not steps:
        return None
    prior = {
        "id": data["runId"],
        "head_sha": prior_sha,
        "run_attempt": int(data.get("runAttempt") or 1),
        "event": args.event,
        "head_branch": args.head_branch,
    }
    return prior, steps, "pr_local_gate_cache"


def cmd_cache_success(args):
    if args.workflow not in WORKFLOWS:
        raise SystemExit(f"Unsupported validation workflow: {args.workflow}")
    plan = {
        "workflow": args.workflow,
        "currentSha": args.head_sha,
        "gates": {
            key: {"receipt": {"result": "success"}}
            for key in WORKFLOWS[args.workflow]["gates"]
        },
    }
    payload = _write_gate_cache(
        plan,
        args.workflow,
        args.head_sha,
        args.run_id,
        args.run_attempt,
        args.output,
    )
    print(json.dumps(payload, sort_keys=True))


def _compute_validation_plan_once(args):
    cached = _cached_success_evidence(args)
    if cached is not None:
        prior, steps, source = cached
        plan = _plan_against_prior(args.workflow, args.current_sha, prior, steps, source)
        _stamp_evidence(plan, prior, source)
        # A failed parent can checkpoint only the children it observed successful.
        # Missing cache entries are unknown, not invalidated. Backfill only those
        # unresolved gates from trusted content-equivalent historical producers;
        # changed inputs/definitions remain runnable and fail closed.
        plan = _apply_content_equivalent_evidence(args, plan)
    else:
        prior, steps, source = prior_evidence(args)
        if prior:
            plan = _plan_against_prior(args.workflow, args.current_sha, prior, steps, source)
            _stamp_evidence(plan, prior, source)
        else:
            plan = compute_plan(
                args.workflow,
                args.current_sha,
                None,
                {},
                [],
                source,
            )
        # A latest exact development failure must not be replaced by older PR
        # success. Missing children remain runnable rather than inferred green.
        if source != 'exact_development_child_receipts':
            plan = _apply_content_equivalent_evidence(args, plan)
    plan["schemaVersion"] = 2
    plan["dependencyManifests"] = gate_dependency_manifests(args.workflow, args.current_sha)
    return plan


def _evidence_retry_delay(exc, attempt):
    rate = getattr(exc, "rate", {}) or {}
    retry = rate.get("Retry-After")
    reset = rate.get("X-RateLimit-Reset")
    try:
        if retry is not None:
            return max(1, int(retry) + 2)
        if reset is not None:
            return max(1, int(reset) - int(time.time()) + 3)
    except (TypeError, ValueError):
        pass
    return (15, 30, 60, 120)[min(attempt, 3)]


# The lifecycle owns the one trusted production-readiness producer. Validation
# only consumes its bounded data receipts; it never obtains production secrets.
READINESS_SCHEMA = 1
READINESS_MAX_AGE_SECONDS = 900
READINESS_WAIT_SECONDS = 900


def readiness_scope(candidate, approved):
    """Scope production readiness using only the approved impact classifier.

    This never grants release admission. Nonpublishing repairs still need every
    exact-head integrity/security/validation gate. A changed release request,
    application input, unknown impact, or missing baseline remains required.
    """
    result = {'state': 'required', 'reason': 'production_impact_unproven',
              'candidate': candidate, 'approved': approved}
    try:
        if not all(re.fullmatch(r'[a-f0-9]{40}', value or '') for value in (candidate, approved)):
            return result
        if subprocess.run(['git', 'merge-base', '--is-ancestor', approved, candidate],
                          capture_output=True, check=False).returncode:
            return result
        paths = subprocess.check_output(['git', 'diff', '--no-renames', '--name-only', '-z',
                                         approved, candidate, '--']).decode().split('\0')
        paths = sorted(path for path in paths if path)
        if not paths or RELEASE_REQUEST_PATH in paths:
            return dict(result, reason='release_request_or_empty_scope')
        source = git_show_file(approved, 'scripts/validation-resume.py')
        # Execute only this authenticated approved module, never candidate
        # classifier code or candidate imports. It owns the existing path policy.
        trusted = {'__name__': 'approved_readiness_impact', '__file__': __file__}
        exec(compile(source, 'approved-validation-resume', 'exec'), trusted)
        if not all(trusted['release_control_only_path'](path) for path in paths):
            return dict(result, reason='application_or_unknown_input')
        proof = dict(candidate=candidate, approved=approved, paths=paths,
                     authorityDigest=hashlib.sha256(source.encode()).hexdigest())
        return dict(proof, state='not-required', reason='approved_nonpublishing_control_scope',
                    identity=hashlib.sha256(json.dumps(proof, sort_keys=True).encode()).hexdigest())
    except (KeyError, ValueError, TypeError, OSError, UnicodeError, subprocess.SubprocessError):
        return result


def readiness_targets(candidate, approved):
    paths = git_changed(approved, candidate)
    if 'Docs/releases/direct-release-request.json' not in paths:
        return list(release_targets_for_paths(paths))
    request = json.loads(git_show_file(candidate, 'Docs/releases/direct-release-request.json'))
    if not isinstance(request, dict) or request.get('releaseMode') not in {'approved-only', 'validate-only'}:
        raise ValueError('READINESS_RELEASE_REQUEST_INVALID')
    if request['releaseMode'] == 'validate-only':
        return list(release_targets_for_paths(paths))
    targets = request.get('targets')
    selected_release_target_keys(targets)
    if any(type(request.get(key, False)) is not bool for key in ('cloudflareWebsiteRouting', 'preserveLiveTargets')):
        raise ValueError('READINESS_RELEASE_REQUEST_INVALID')
    return targets


def readiness_policy_identity(approved):
    paths = ('scripts/release-migration.py', 'scripts/release-prepublication.py',
             'scripts/release-lifecycle.py', 'scripts/validation-resume.py', 'scripts/release-operation-evidence.py',
             'scripts/MigrationReleaseProbe/Program.cs', 'scripts/MigrationReleaseProbe/CandidateContract.cs',
             '.github/workflows/legend-release-lifecycle.yml', '.github/workflows/approved-release-security-validation.yml')
    return hashlib.sha256(json.dumps({p: git_show_file(approved, p) for p in paths},
                                    sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def readiness_identity(candidate, approved, targets):
    probe = migration_probe_identity(candidate, candidate)
    workflow = git_show_file(candidate, '.github/workflows/' + PACKAGE_VALIDATION_WORKFLOW)
    build = {path: git_show_file(candidate, path) for path in PACKAGE_AUTHORITY_PATHS
             if path != PACKAGE_BUILD_WORKFLOW}
    build['workflow'] = package_builder_workflow_contract(workflow)
    jobs = _job_blocks(workflow)
    rehearsal, prepare = jobs.get('migration-rehearsal'), jobs.get('migration-rehearsal-prepare')
    if not rehearsal or not prepare:
        raise ValueError('READINESS_REHEARSAL_AUTHORITY_MISSING')
    payload = {'schemaVersion': READINESS_SCHEMA, 'migrationInputIdentity': probe['runtimeIdentity'],
               'candidateContractIdentity': probe['toolIdentity'],
               'probeExecutionIdentity': probe['executionIdentity'],
               'rehearsalExecutionIdentity': hashlib.sha256((prepare + '\n' + rehearsal).encode()).hexdigest(),
               'migrationBuildIdentity': hashlib.sha256(json.dumps(build, sort_keys=True).encode()).hexdigest(),
               'policyIdentity': readiness_policy_identity(approved),
               'targets': sorted(targets), 'environment': 'Production',
               'resourceGroup': RELEASE_RESOURCE_GROUP}
    return dict(payload, identity=hashlib.sha256(json.dumps(payload, sort_keys=True,
                                                         separators=(',', ':')).encode()).hexdigest())


def migration_contract_static_styles_only(candidate, approved):
    """Reviewed non-executable delta; retain full identities for build/package reuse."""
    patterns = WORKFLOWS[PACKAGE_VALIDATION_WORKFLOW]['gates']['build-infrastructure']['paths']
    patterns = tuple(p for p in patterns if p not in {'**/*.csproj', 'MASTERAPP.sln'})
    def entries(revision):
        rows = subprocess.check_output(['git', 'ls-tree', '-rz', '--full-tree', revision], text=True).split('\0')
        return {name: metadata for row in rows if row
                for metadata, name in [row.split('\t', 1)] if matches(name, patterns)}
    before, after = entries(approved), entries(candidate)
    changed = [p for p in before.keys() | after.keys() if before.get(p) != after.get(p)]
    return bool(changed) and all(
        p.startswith('SHARED/wwwroot/') and p.endswith('.css') and
        before.get(p, '').startswith('100644 blob ') and after.get(p, '').startswith('100644 blob ')
        for p in changed)


# Exact independently reviewed runtime closures, using the unchanged authority
# algorithm. The entry digest below uses sorted compact Python JSON; the trusted
# C# observer separately pins its System.Text.Json representation (d2da70a...).
REVIEWED_RECEPTIONIST_BASE_RUNTIME = '22534ca70dc760d4c10e2c9726346c84315d2b7179c0acf87db49bf55372bedc'
REVIEWED_RECEPTIONIST_RUNTIME = '4f6153929faff37fd4209636fa912c8e147adc2c96b6644ad084047e9a597a07'
REVIEWED_RECEPTIONIST_ENTRY = 'c457b4d88db0b998b778b2a789e49412b6218e9b87b4f511de0746d739087c8b'
REVIEWED_MANAGEMENT_ENTRY = '5578b38e0ed342ffd24b958f8d2395e13c4933d5ad477fc58ed714393a86a897'
REVIEWED_BROWSER_ENTRY = '19163c8f4ecf7fecb275ed0052dfa6fc161d586e989360b7c2f51c983b7353d8'
# Remains closed until independent acceptance pins the complete candidate closure.
REVIEWED_BROWSER_RUNTIME_PAIRS = frozenset()

REVIEWED_TRAINING_ENTRY = '49ef5a95c19084b799e9a92a5d0c496792dbdcab367e4360795d55e443338434'
# Empty until independent acceptance freezes the exact application dependency closure.
REVIEWED_TRAINING_RUNTIME_PAIRS = frozenset()

REVIEWED_CANONICAL_PAYMENT_ENTRY = '0c16fa4ce2197954982578e1cdbc4291f077d8660b65b769799c58dfbcbc3a1d'
# Frozen recovered application closure. These pins do not promote the changed
# observer: trusted tooling must first be legitimately promoted and inherited.
REVIEWED_CANONICAL_PAYMENT_RUNTIME_PAIRS = frozenset({
    # Live release 7dcfe753: same complete migration/model prefix as d726.
    # Intervening accepted runtime behavior changes do not alter discovery or
    # schema. This exact observation pair does not promote tools or admit apps.
    ('f6ae0f4012706ad346d743fb69dfbff4cc46238aa46a9f60687aede0a8cc57eb',
     'e097be398e774e6226b9a7cb72b41b7eec92eb8a3043af8e16e412381ca0051f'),
    ('3fbe9ce6512a2ebb7db43f0f45a5227786094ac6b287ccc3eca40c59da05f6e0',
     'e097be398e774e6226b9a7cb72b41b7eec92eb8a3043af8e16e412381ca0051f')
})


def reviewed_browser_contract_extension(candidate, approved, contract, approved_contract, current, trusted):
    if ((trusted.get('runtimeIdentity'), current.get('runtimeIdentity')) not in REVIEWED_BROWSER_RUNTIME_PAIRS
        or not current.get('toolIdentity') or current.get('toolIdentity') != trusted.get('toolIdentity')
        or not current.get('executionIdentity') or current.get('executionIdentity') != trusted.get('executionIdentity')):
        return False
    if (not isinstance(contract, dict) or not isinstance(approved_contract, dict)
        or set(contract) != {'schemaVersion', 'migrations'} or set(approved_contract) != set(contract)
        or type(contract['schemaVersion']) is not int or contract['schemaVersion'] != 3
        or type(approved_contract['schemaVersion']) is not int or approved_contract['schemaVersion'] != 3
        or not isinstance(contract['migrations'], list) or not isinstance(approved_contract['migrations'], list)
        or not 1 <= len(approved_contract['migrations']) < len(contract['migrations']) <= 10000
        or contract['migrations'][:len(approved_contract['migrations'])] != approved_contract['migrations']):
        return False
    reviewed = [('20261020160000_AddReceptionistCoordination', REVIEWED_RECEPTIONIST_ENTRY),
        ('20261026000134_AddCanonicalSubscriptionPaymentCompletion', REVIEWED_CANONICAL_PAYMENT_ENTRY),
        ('20261026063902_AddScopedTrainingSubscriptions', REVIEWED_TRAINING_ENTRY),
        ('20261026210738_AddReceptionistManagement', REVIEWED_MANAGEMENT_ENTRY),
        ('20261026223148_ReceptionistBrowserCreation', REVIEWED_BROWSER_ENTRY)]
    suffix = contract['migrations'][len(approved_contract['migrations']):]
    if not 1 <= len(suffix) <= len(reviewed):
        return False
    expected = reviewed[-len(suffix):]
    if len(suffix) < len(reviewed) and approved_contract['migrations'][-1].get('id') != reviewed[-len(suffix)-1][0]:
        return False
    previous = approved_contract['migrations'][-1].get('id')
    for entry, (migration_id, digest) in zip(suffix, expected):
        if (not isinstance(entry, dict) or entry.get('id') != migration_id or entry.get('supported') is not True
            or not isinstance(previous, str) or previous >= migration_id
            or hashlib.sha256(json.dumps(entry, sort_keys=True, separators=(',', ':'),
                ensure_ascii=True, allow_nan=False).encode()).hexdigest() != digest):
            return False
        previous = migration_id
    if subprocess.run(['git', 'merge-base', '--is-ancestor', approved, candidate], capture_output=True).returncode:
        return False
    if subprocess.run(['git', 'diff', '--quiet', approved, candidate, '--', 'scripts', '.github'], capture_output=True).returncode:
        return False
    return True


def reviewed_training_contract_extension(candidate, approved, contract, approved_contract, current, trusted):
    if ((trusted.get('runtimeIdentity'), current.get('runtimeIdentity')) not in REVIEWED_TRAINING_RUNTIME_PAIRS
        or not current.get('toolIdentity') or current.get('toolIdentity') != trusted.get('toolIdentity')
        or not current.get('executionIdentity') or current.get('executionIdentity') != trusted.get('executionIdentity')):
        return False
    if (not isinstance(contract, dict) or not isinstance(approved_contract, dict)
        or set(contract) != {'schemaVersion', 'migrations'} or set(approved_contract) != set(contract)
        or type(contract['schemaVersion']) is not int or contract['schemaVersion'] != 3
        or type(approved_contract['schemaVersion']) is not int or approved_contract['schemaVersion'] != 3
        or not isinstance(contract['migrations'], list) or not isinstance(approved_contract['migrations'], list)
        or not 1 <= len(approved_contract['migrations']) < 10000
        or len(contract['migrations']) != len(approved_contract['migrations']) + 1
        or contract['migrations'][:-1] != approved_contract['migrations']):
        return False
    previous, entry = approved_contract['migrations'][-1], contract['migrations'][-1]
    if (not isinstance(previous, dict) or previous.get('id') != '20261026000134_AddCanonicalSubscriptionPaymentCompletion'
        or not isinstance(entry, dict) or entry.get('id') != '20261026063902_AddScopedTrainingSubscriptions'
        or entry.get('supported') is not True
        or hashlib.sha256(json.dumps(entry, sort_keys=True, separators=(',', ':'),
            ensure_ascii=True, allow_nan=False).encode()).hexdigest() != REVIEWED_TRAINING_ENTRY):
        return False
    if subprocess.run(['git', 'merge-base', '--is-ancestor', approved, candidate], capture_output=True).returncode:
        return False
    if subprocess.run(['git', 'diff', '--quiet', approved, candidate, '--', 'scripts', '.github'], capture_output=True).returncode:
        return False
    return True


def reviewed_canonical_payment_contract_extension(candidate, approved, contract, approved_contract, current, trusted):
    if ((trusted.get('runtimeIdentity'), current.get('runtimeIdentity')) not in REVIEWED_CANONICAL_PAYMENT_RUNTIME_PAIRS
        or not current.get('toolIdentity') or current.get('toolIdentity') != trusted.get('toolIdentity')
        or not current.get('executionIdentity') or current.get('executionIdentity') != trusted.get('executionIdentity')):
        return False
    if (not isinstance(contract, dict) or not isinstance(approved_contract, dict)
        or set(contract) != {'schemaVersion', 'migrations'} or set(approved_contract) != set(contract)
        or type(contract['schemaVersion']) is not int or contract['schemaVersion'] != 3
        or type(approved_contract['schemaVersion']) is not int or approved_contract['schemaVersion'] != 3
        or not isinstance(contract['migrations'], list) or not isinstance(approved_contract['migrations'], list)
        or not 1 <= len(approved_contract['migrations']) < 10000
        or len(contract['migrations']) != len(approved_contract['migrations']) + 1
        or contract['migrations'][:-1] != approved_contract['migrations']):
        return False
    previous, entry = approved_contract['migrations'][-1], contract['migrations'][-1]
    if (not isinstance(previous, dict) or previous.get('id') != '20261020160000_AddReceptionistCoordination'
        or not isinstance(entry, dict) or entry.get('id') != '20261026000134_AddCanonicalSubscriptionPaymentCompletion'
        or entry.get('supported') is not True
        or hashlib.sha256(json.dumps(entry, sort_keys=True, separators=(',', ':'),
            ensure_ascii=True, allow_nan=False).encode()).hexdigest() != REVIEWED_CANONICAL_PAYMENT_ENTRY):
        return False
    if subprocess.run(['git', 'merge-base', '--is-ancestor', approved, candidate], capture_output=True).returncode:
        return False
    if subprocess.run(['git', 'diff', '--quiet', approved, candidate, '--', 'scripts', '.github'], capture_output=True).returncode:
        return False
    return True


def reviewed_receptionist_contract_extension(candidate, approved, contract, approved_contract, current, trusted):
    """One reviewed dependency closure and declarative suffix, never a generic append waiver.

    These are the existing full migration_probe_identity runtime hashes, with all
    paths/modes/blobs retained. The trusted tools are promoted first; the feature
    candidate must inherit them byte-for-byte. Any runtime, model, migration,
    build input or authority change requires a new independent review.
    """
    if (trusted.get('runtimeIdentity') != REVIEWED_RECEPTIONIST_BASE_RUNTIME
        or current.get('runtimeIdentity') != REVIEWED_RECEPTIONIST_RUNTIME
        or not current.get('toolIdentity') or current.get('toolIdentity') != trusted.get('toolIdentity')
        or not current.get('executionIdentity') or current.get('executionIdentity') != trusted.get('executionIdentity')):
        return False
    if (not isinstance(contract, dict) or not isinstance(approved_contract, dict)
        or set(contract) != {'schemaVersion', 'migrations'} or set(approved_contract) != set(contract)
        or type(contract['schemaVersion']) is not int or contract['schemaVersion'] != 3
        or type(approved_contract['schemaVersion']) is not int or approved_contract['schemaVersion'] != 3
        or not isinstance(contract['migrations'], list) or not isinstance(approved_contract['migrations'], list)
        or not 1 <= len(approved_contract['migrations']) < 10000
        or len(contract['migrations']) != len(approved_contract['migrations']) + 1
        or contract['migrations'][:-1] != approved_contract['migrations']):
        return False
    entry = contract['migrations'][-1]
    if (not isinstance(entry, dict) or entry.get('id') != '20261020160000_AddReceptionistCoordination'
        or entry.get('supported') is not True
        or hashlib.sha256(json.dumps(entry, sort_keys=True, separators=(',', ':'),
            ensure_ascii=True, allow_nan=False).encode()).hexdigest() !=
            REVIEWED_RECEPTIONIST_ENTRY):
        return False
    # Only Git objects are inspected. No candidate checkout, script, assembly or
    # build is executed by the trusted credentialed observer.
    if subprocess.run(['git', 'merge-base', '--is-ancestor', approved, candidate], capture_output=True).returncode:
        return False
    if subprocess.run(['git', 'diff', '--quiet', approved, candidate, '--', 'scripts', '.github'], capture_output=True).returncode:
        return False
    return True


def candidate_migration_contract_proven(candidate, approved, contract, approved_contract):
    """Candidate metadata cannot declare its own completeness or safety.

    The initial reviewed rule covers unchanged migration/runtime dependencies.
    Unknown discovery changes (including attributes outside Migrations/) stop
    before database observation. Add new shapes through reviewed extractor rules.
    """
    current = migration_probe_identity(candidate, candidate)
    trusted = migration_probe_identity(approved, approved)
    rule = 'approved-definitions-exact'
    if current['runtimeIdentity'] != trusted['runtimeIdentity']:
        if reviewed_browser_contract_extension(candidate, approved, contract, approved_contract, current, trusted):
            return {'state': 'proven', 'rule': 'approved-prefix-reviewed-browser-entry',
                    'runtimeIdentity': current['runtimeIdentity'],
                    'approvedRuntimeIdentity': trusted['runtimeIdentity']}
        if reviewed_training_contract_extension(candidate, approved, contract, approved_contract, current, trusted):
            return {'state': 'proven', 'rule': 'approved-prefix-reviewed-training-entry',
                    'runtimeIdentity': current['runtimeIdentity'],
                    'approvedRuntimeIdentity': trusted['runtimeIdentity']}
        if reviewed_canonical_payment_contract_extension(candidate, approved, contract, approved_contract, current, trusted):
            return {'state': 'proven', 'rule': 'approved-prefix-reviewed-canonical-payment-entry',
                    'runtimeIdentity': current['runtimeIdentity'],
                    'approvedRuntimeIdentity': trusted['runtimeIdentity']}
        if reviewed_receptionist_contract_extension(candidate, approved, contract, approved_contract, current, trusted):
            # This exact runtime pair must carry the reviewed suffix. It cannot
            # fall back to equality and silently omit the pending migration.
            return {'state': 'proven', 'rule': 'approved-prefix-reviewed-receptionist-entry',
                    'runtimeIdentity': current['runtimeIdentity'],
                    'approvedRuntimeIdentity': trusted['runtimeIdentity']}
        if (current.get('toolIdentity') != trusted.get('toolIdentity') or
            current.get('executionIdentity') != trusted.get('executionIdentity') or
            not migration_contract_static_styles_only(candidate, approved)):
            raise ValueError('CANDIDATE_MIGRATION_CONTRACT_UNPROVEN: migration/runtime dependency content changed; reviewed extraction rule required')
        rule = 'approved-definitions-static-styles-only'
    if not isinstance(contract, dict) or contract != approved_contract:
        raise ValueError('CANDIDATE_MIGRATION_CONTRACT_UNPROVEN: candidate metadata differs from approved definitions')
    return {'state': 'proven', 'rule': rule,
            'runtimeIdentity': current['runtimeIdentity'],
            'approvedRuntimeIdentity': trusted['runtimeIdentity']}


def validate_readiness_receipt(receipt, expected, *, now=None, require_fresh=True):
    import datetime
    now = datetime.datetime.now(datetime.timezone.utc) if now is None else now
    if not isinstance(receipt, dict) or any(receipt.get(k) != v for k, v in expected.items()):
        raise ValueError('READINESS_INPUTS_CHANGED')
    if receipt.get('state') not in {'executed-success', 'reused-success'}:
        raise ValueError('READINESS_NOT_SUCCESSFUL')
    try:
        observed = datetime.datetime.fromisoformat(receipt['observedUtc'].replace('Z', '+00:00'))
        if observed.utcoffset() is None:
            raise ValueError()
    except (KeyError, AttributeError, TypeError, ValueError):
        raise ValueError('READINESS_TIME_UNPROVEN') from None
    age = (now - observed).total_seconds()
    if age < -30 or (require_fresh and age > READINESS_MAX_AGE_SECONDS):
        raise ValueError('READINESS_EXPIRED')
    for key in ('databaseIdentity', 'baselineIdentity', 'schemaIdentity', 'contractDigest'):
        if not isinstance(receipt.get(key), str) or not re.fullmatch('[a-f0-9]{64}', receipt[key]):
            raise ValueError('READINESS_PROOF_INCOMPLETE')
    if type(receipt.get('pendingCount')) is not int or not 0 <= receipt['pendingCount'] <= 10000:
        raise ValueError('READINESS_PROOF_INCOMPLETE')
    if receipt['pendingCount'] > 0:
        rehearsal = receipt.get('rehearsal')
        if (not isinstance(rehearsal, dict) or rehearsal.get('proven') is not True or
            rehearsal.get('contractDigest') != receipt['contractDigest'] or
            rehearsal.get('schemaIdentity') != receipt['schemaIdentity'] or
            rehearsal.get('baselineIdentity') != receipt['baselineIdentity'] or
            not isinstance(rehearsal.get('bundleDigest'), str) or
            not re.fullmatch('[a-f0-9]{64}', rehearsal['bundleDigest'])):
            raise ValueError('MIGRATION_REHEARSAL_MISSING')
    admission = 'proven' if receipt['pendingCount'] else 'not-required'
    if receipt.get('mutationAdmission') != admission or receipt.get('deploymentReadiness') != 'proven':
        raise ValueError('READINESS_PREREQUISITES_UNPROVEN')
    return receipt


def trusted_readiness_run(repository, run, approved):
    """PR-target head is candidate metadata; the base owns executed workflow code."""
    if ((run.get('head_repository') or {}).get('full_name') != repository or
        run.get('path') != '.github/workflows/legend-release-lifecycle.yml'):
        return False
    if run.get('event') == 'workflow_dispatch':
        return run.get('head_branch') == TRUSTED_PR_BASE and run.get('head_sha') == approved
    if run.get('event') != 'pull_request_target':
        return False
    prs = run.get('pull_requests')
    if not isinstance(prs, list) or len(prs) != 1:
        return False
    head, base = prs[0].get('head', {}), prs[0].get('base', {})
    expected_url = 'https://api.github.com/repos/' + repository
    execution = base.get('sha')
    if not isinstance(execution, str) or not re.fullmatch('[a-f0-9]{40}', execution):
        return False
    if execution != approved and subprocess.run(['git', 'merge-base', '--is-ancestor', execution, approved],
                                                capture_output=True, check=False).returncode != 0:
        return False
    return (base.get('ref') == TRUSTED_PR_BASE and
            head.get('sha') == run.get('head_sha') and head.get('ref') == run.get('head_branch') and
            all(part.get('repo', {}).get('url') == expected_url for part in (head, base)))


def readiness_execution_revision(run):
    return run['pull_requests'][0]['base']['sha'] if run['event'] == 'pull_request_target' else run['head_sha']


def trusted_rehearsal_run(repository, run, candidate, expected):
    """Authenticate source/base and recompute inputs, never trust receipt claims."""
    path = '.github/workflows/' + PACKAGE_VALIDATION_WORKFLOW
    if not _trusted_lineage_run(repository, run, path, candidate, require_completed=False):
        return False
    pulls = run.get('pull_requests')
    if pulls == []:
        # Merged PR runs lose this mutable association. GitHub's existing
        # commit-to-PR endpoint binds the immutable producer after merge.
        token = os.environ.get('GITHUB_TOKEN') or os.environ.get('GH_TOKEN') or ''
        if not token:
            raise EvidenceLookupUnavailable('Package producer PR association credential unavailable')
        associated = api_get(repository, f"commits/{run['head_sha']}/pulls?per_page=100", token)
        if not isinstance(associated, list) or len(associated) >= 100:
            raise EvidenceLookupUnavailable('Package producer PR association inventory incomplete')
        pulls = [row for row in associated if
            row.get('base', {}).get('ref') == TRUSTED_PR_BASE and
            all(row.get(side, {}).get('repo', {}).get('url') == 'https://api.github.com/repos/' + repository
                for side in ('head', 'base'))]
    if not isinstance(pulls, list) or len(pulls) != 1:
        return False
    head, base = pulls[0].get('head', {}), pulls[0].get('base', {})
    url = 'https://api.github.com/repos/' + repository
    approved = base.get('sha', '')
    if (base.get('ref') != TRUSTED_PR_BASE or head.get('sha') != run.get('head_sha') or
        not re.fullmatch('[a-f0-9]{40}', approved) or
        any(part.get('repo', {}).get('url') != url for part in (head, base))):
        return False
    if subprocess.run(['git', 'merge-base', '--is-ancestor', approved, run['head_sha']],
                      capture_output=True, check=False).returncode != 0:
        return False
    try:
        producer = readiness_identity(run['head_sha'], approved, expected['targets'])
        approved_owner = readiness_identity(approved, approved, expected['targets'])
    except (ValueError, KeyError):
        return False
    # Compatibility alone cannot authenticate a candidate-modified executor
    # that fabricates counts. Install authority changes through reviewed
    # bootstrap before that code may certify subsequent candidates.
    if any(producer.get(key) != approved_owner.get(key) for key in (
            'candidateContractIdentity', 'probeExecutionIdentity',
            'rehearsalExecutionIdentity', 'migrationBuildIdentity')):
        return False
    return producer == expected


def readiness_child_succeeded(repository, run, name, attempt, token):
    # Parent retries do not invalidate a completed child from an earlier attempt.
    if type(attempt) is not int or not 1 <= attempt <= run.get('run_attempt', 1):
        return False
    jobs = api_get(repository, f"actions/runs/{run['id']}/attempts/{attempt}/jobs?per_page=100", token)
    child = [j for j in jobs.get('jobs', []) if j.get('name') == name]
    return (jobs.get('total_count') == len(jobs.get('jobs', [])) and len(child) == 1 and
            child[0].get('status') == 'completed' and child[0].get('conclusion') == 'success')


def readiness_failure_kind(repository, run, approved, token):
    if not trusted_readiness_run(repository, run, approved):
        return 'untrusted'
    attempt = run.get('run_attempt')
    if type(attempt) is not int or attempt < 1:
        return 'untrusted'
    name = 'legend-readiness-failure-' + run['head_sha'] + '-a' + str(attempt)
    inventory = api_get(repository, f"actions/runs/{run['id']}/artifacts?per_page=100", token)
    artifacts = inventory.get('artifacts', [])
    if inventory.get('total_count') != len(artifacts):
        raise EvidenceLookupUnavailable('Readiness failure inventory incomplete')
    rows = [row for row in artifacts if row.get('name') == name and row.get('expired') is False]
    if len(rows) != 1:
        return 'unclassified'
    artifact = rows[0]
    if type(artifact.get('id')) is not int or artifact['id'] <= 0:
        return 'untrusted'
    jobs = api_get(repository, f"actions/runs/{run['id']}/attempts/{attempt}/jobs?per_page=100", token)
    producers = [row for row in jobs.get('jobs', []) if row.get('name') == 'readiness-observe']
    if jobs.get('total_count') != len(jobs.get('jobs', [])) or len(producers) != 1:
        return 'untrusted'
    producer = producers[0]
    uploads = [step for step in producer.get('steps', []) if step.get('name') == 'Preserve classified readiness failure']
    if (producer.get('status') != 'completed' or producer.get('conclusion') != 'failure' or
            len(uploads) != 1 or uploads[0].get('status') != 'completed' or uploads[0].get('conclusion') != 'success'):
        return 'untrusted'
    failure = _release_history_json(repository, run['id'], rows[0], 'failure.json')
    if (failure.get('schemaVersion') != 1 or failure.get('candidate') != run['head_sha'] or
        failure.get('executionAuthority') != readiness_execution_revision(run) or
        failure.get('producingRun') != run['id'] or failure.get('producingAttempt') != attempt):
        raise ValueError('Readiness failure producer identity mismatch')
    return failure.get('classification', 'unclassified')


def readiness_refresh_intent(repository, operation, approved, token):
    """Exact-name provider index, authenticated back to the approved lifecycle."""
    if not re.fullmatch('[a-f0-9]{64}', operation):
        raise ValueError('Invalid readiness recovery identity')
    name = 'legend-readiness-recovery-' + operation
    data = api_get(repository, 'actions/artifacts?name=' + name + '&per_page=100', token)
    rows = data.get('artifacts')
    if not isinstance(rows, list) or data.get('total_count') != len(rows):
        raise EvidenceLookupUnavailable('Readiness recovery intent inventory incomplete')
    if not rows:
        return None
    if len(rows) != 1 or rows[0].get('expired') is not False or rows[0].get('name') != name:
        raise ValueError('Readiness recovery intent expired or ambiguous; no request authorized')
    artifact = rows[0]
    run_id = artifact.get('workflow_run', {}).get('id')
    run = api_get(repository, f'actions/runs/{run_id}', token)
    trusted = trusted_readiness_run(repository, run, approved)
    if run.get('event') in {'workflow_run', 'schedule', 'push'}:
        trusted = (run.get('head_repository', {}).get('full_name') == repository and
            run.get('path') == '.github/workflows/legend-release-lifecycle.yml' and
            run.get('head_branch') == TRUSTED_PR_BASE and
            re.fullmatch('[a-f0-9]{40}', run.get('head_sha', '')) and
            subprocess.run(['git', 'merge-base', '--is-ancestor', run['head_sha'], approved], capture_output=True).returncode == 0)
    if not trusted:
        raise ValueError('Readiness recovery producer untrusted')
    record = _release_history_json(repository, run_id, artifact, 'operation.json')
    if (record.get('schemaVersion') != 1 or record.get('phase') != 'intent' or
        record.get('operationId') != operation or record.get('producingRun') != run_id or
        type(record.get('producingAttempt')) is not int or not 1 <= record['producingAttempt'] <= run.get('run_attempt', 1)):
        raise ValueError('Readiness recovery producer identity mismatch')
    return record


def readiness_artifact_candidates(repository, runs, prefix, identity, token, trusted):
    for run in runs:
        if not trusted(run):
            continue
        stem = prefix + identity + '-a'
        data = api_get(repository, f"actions/runs/{run['id']}/artifacts?per_page=100", token)
        rows = data.get('artifacts')
        if not isinstance(rows, list) or data.get('total_count') != len(rows):
            raise EvidenceLookupUnavailable('Readiness artifact inventory incomplete')
        compatible = []
        for row in rows:
            name = row.get('name', '')
            if row.get('expired') is not False or not name.startswith(stem):
                continue
            if (type(row.get('id')) is not int or row['id'] < 1 or
                not name[len(stem):].isdigit() or not 1 <= int(name[len(stem):]) <= run.get('run_attempt', 1)):
                raise ValueError('READINESS_ARTIFACT_IDENTITY_INVALID')
            compatible.append(row)
        if len({row['name'] for row in compatible}) != len(compatible):
            raise ValueError('READINESS_ARTIFACT_IDENTITY_AMBIGUOUS')
        for row in sorted(compatible, key=lambda value: int(value['name'][len(stem):]), reverse=True):
            yield run, row


def readiness_evidence(repository, candidate, approved, targets, token, *, require_fresh=True, baseline=None):
    import tempfile
    expected = readiness_identity(candidate, approved, targets)
    workflow = 'legend-release-lifecycle.yml'
    data = api_get(repository, 'actions/workflows/' + workflow + '/runs?per_page=100', token)
    for run, artifact in readiness_artifact_candidates(repository, data.get('workflow_runs', []),
            'legend-release-readiness-', expected['identity'], token, lambda run: trusted_readiness_run(repository, run, approved)):
        with tempfile.TemporaryDirectory() as directory:
            _download_run_artifact(repository, run['id'], artifact['name'], Path(directory), artifact_id=artifact['id'])
            receipt = json.loads((Path(directory) / 'readiness.json').read_text())
        try:
            validate_readiness_receipt(receipt, expected, require_fresh=require_fresh)
        except ValueError as exc:
            if str(exc) == 'READINESS_EXPIRED':
                continue
            raise
        if type(receipt.get('producingAttempt')) is not int or receipt['producingAttempt'] != int(artifact['name'].rsplit('-a', 1)[1]):
            raise ValueError('READINESS_ARTIFACT_ATTEMPT_MISMATCH')
        if (receipt.get('producingRun') != run['id'] or
            receipt.get('executionAuthority') != readiness_execution_revision(run) or receipt.get('candidate') != run.get('head_sha')):
            raise ValueError('READINESS_PRODUCER_MISMATCH')
        if not readiness_child_succeeded(repository, run, 'release-readiness', receipt.get('producingAttempt'), token):
            continue
        if baseline and any(receipt.get(key) != value for key, value in baseline.items()):
            continue
        return {'state': 'reused-success', 'sourceRun': run['id'], 'artifact': artifact['name'], 'artifactId': artifact['id'],
                'compatibilityProof': expected, 'receipt': receipt}
    return None


def readiness_observation_evidence(repository, candidate, approved, targets, token):
    import tempfile
    expected = readiness_identity(candidate, approved, targets)
    data = api_get(repository, 'actions/workflows/legend-release-lifecycle.yml/runs?event=pull_request_target&per_page=100', token)
    for run, artifact in readiness_artifact_candidates(repository, data.get('workflow_runs', []),
            'legend-readiness-observation-', expected['identity'], token, lambda run: trusted_readiness_run(repository, run, approved)):
        with tempfile.TemporaryDirectory() as directory:
            _download_run_artifact(repository, run['id'], artifact['name'], Path(directory), artifact_id=artifact['id'])
            receipt = json.loads((Path(directory) / 'observation.json').read_text())
        if type(receipt.get('producingAttempt')) is not int or receipt['producingAttempt'] != int(artifact['name'].rsplit('-a', 1)[1]):
            raise ValueError('READINESS_ARTIFACT_ATTEMPT_MISMATCH')
        if (any(receipt.get(k) != v for k, v in expected.items()) or
            receipt.get('producingRun') != run['id'] or
            receipt.get('executionAuthority') != readiness_execution_revision(run) or receipt.get('candidate') != run.get('head_sha')):
            raise ValueError('READINESS_OBSERVATION_IDENTITY_CHANGED')
        if not readiness_child_succeeded(repository, run, 'readiness-observe', receipt.get('producingAttempt'), token):
            continue
        return receipt
    return None


def migration_rehearsal_evidence(repository, candidate, expected, token, *, baseline=None):
    import tempfile
    path = '.github/workflows/' + PACKAGE_VALIDATION_WORKFLOW
    data = api_get(repository, 'actions/workflows/' + PACKAGE_VALIDATION_WORKFLOW + '/runs?event=pull_request&per_page=100', token)
    for run, artifact in readiness_artifact_candidates(repository, data.get('workflow_runs', []),
            'legend-migration-rehearsal-', expected['identity'], token, lambda run: trusted_rehearsal_run(repository, run, candidate, expected)):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            _download_run_artifact(repository, run['id'], artifact['name'], root, artifact_id=artifact['id'])
            receipt = json.loads((root / 'rehearsal.json').read_text())
        if type(receipt.get('producingAttempt')) is not int or receipt['producingAttempt'] != int(artifact['name'].rsplit('-a', 1)[1]):
            raise ValueError('READINESS_ARTIFACT_ATTEMPT_MISMATCH')
        if (receipt.get('readinessIdentity') != expected['identity'] or receipt.get('proven') is not True or
            receipt.get('producingRun') != run['id'] or
            receipt.get('candidate') != run.get('head_sha') or
            receipt.get('counts') != {'mutations': 1, 'intents': 1, 'successReceipts': 2}):
            raise ValueError('READINESS_REHEARSAL_IDENTITY_CHANGED')
        if not readiness_child_succeeded(repository, run, 'migration-rehearsal', receipt.get('producingAttempt'), token):
            continue
        if baseline and any(receipt.get(key) != baseline.get(key) for key in ('schemaIdentity', 'baselineIdentity', 'contractDigest')):
            print('LEGEND_REHEARSAL:RETAINED_BUT_NOT_REUSED:observed_baseline_changed:source=' + str(run['id']))
            continue
        source = receipt.get('bundleSource')
        if (not isinstance(source, dict) or source.get('runId') != run['id'] or
            type(source.get('artifactId')) is not int or type(source.get('producingAttempt')) is not int or
            source.get('artifact') != 'legend-rehearsal-bundle-' + expected['identity'] + '-a' + str(source['producingAttempt']) or
            not readiness_child_succeeded(repository, run, 'migration-rehearsal-prepare', source['producingAttempt'], token)):
            raise ValueError('READINESS_REHEARSED_BUNDLE_SOURCE_UNPROVEN')
        # Read only immutable provider metadata here. The package owner downloads
        # and hashes the bytes once when promoting this exact prepared bundle.
        bundle = api_get(repository, f"actions/artifacts/{source['artifactId']}", token)
        if (bundle.get('id') != source['artifactId'] or bundle.get('name') != source['artifact'] or
            bundle.get('expired') is not False or bundle.get('workflow_run', {}).get('id') != run['id']):
            raise ValueError('READINESS_REHEARSED_BUNDLE_SOURCE_UNPROVEN')
        return {'receipt': receipt, 'runId': run['id'], 'artifact': artifact['name'], 'artifactId': artifact['id']}
    return None


def require_readiness(repository, candidate, *, event=None, clock=time.monotonic, sleep=time.sleep):
    if os.environ.get('GITHUB_ACTIONS') != 'true' or (event or os.environ.get('GITHUB_EVENT_NAME')) != 'pull_request':
        return {'state': 'not-required', 'reason': 'outside_pr_validation'}
    token = os.environ.get('GITHUB_TOKEN') or os.environ.get('GH_TOKEN')
    preflight = approved_head_preflight(repository, candidate, token)
    if not preflight['current']:
        raise ValueError('READINESS_APPROVED_BASE_CHANGED')
    approved = preflight['approvedHeadSha']
    # The initial control-plane rollout cannot ask an older approved workflow to
    # produce a receipt it does not implement. This is explicit bootstrap state,
    # not readiness success, and disappears once the reviewed owner is approved.
    approved_source = git_show_file(approved, 'scripts/validation-resume.py')
    if not re.search(r'^READINESS_SCHEMA = 1$', approved_source, re.M):
        return {'state': 'not-required', 'reason': 'initial_approved_authority_rollout', 'approved': approved}
    scope = readiness_scope(candidate, approved)
    if scope['state'] == 'not-required':
        return scope
    targets = readiness_targets(candidate, approved)
    deadline = clock() + READINESS_WAIT_SECONDS
    while True:
        with evidence_lookup_budget(deadline, clock=clock):
            proof = readiness_evidence(repository, candidate, approved, targets, token)
        if proof:
            print('LEGEND_READINESS:REUSED:' + str(proof['sourceRun']))
            return proof
        if clock() >= deadline:
            raise RuntimeError('READINESS_BLOCKED: trusted lifecycle readiness receipt unavailable; no expensive work authorized')
        sleep(min(10, max(0, deadline - clock())))


def fast_track_source_validation_scope(args, *, clock=time.monotonic, sleep=time.sleep):
    """Separate development pushes from authenticated fast-track PR validation.

    Source validation is not production readiness or mutation admission. The
    fast-track release must separately require those proofs before publication.
    No protected-branch source, baseline, or readiness producer is consulted.
    """
    if ((args.workflow, args.event) not in {('approved-release-security-validation.yml', 'pull_request'),
        ('parfait-isolated-validation.yml', 'push'),
        ('parfait-isolated-validation.yml', 'pull_request'),
        ('masterapp-platform-architecture-validation.yml', 'pull_request')} or os.environ.get('GITHUB_ACTIONS') != 'true'):
        raise ValueError('FAST_TRACK_SOURCE_VALIDATION_CONTEXT_INVALID')
    token = os.environ.get('GITHUB_TOKEN') or os.environ.get('GH_TOKEN')
    if not token:
        raise ValueError('FAST_TRACK_SOURCE_VALIDATION_IDENTITY_MISSING')
    if (not re.fullmatch('[a-f0-9]{40}', args.current_sha or '')
        or type(args.current_run_id) is not int or args.current_run_id < 1
        or type(args.run_attempt) is not int or args.run_attempt < 1):
        raise ValueError('FAST_TRACK_SOURCE_VALIDATION_IDENTITY_MISMATCH')
    expected = dict(id=args.current_run_id, run_attempt=args.run_attempt, event=args.event,
        path='.github/workflows/' + args.workflow, head_sha=args.current_sha)
    if args.event == 'push':
        expected['head_branch'] = args.head_branch
    run, _ = _observe_live_workflow(args.repository, expected, token,
        queued_job=lambda: _source_validation_job_name(args.repository, args.current_sha, args.workflow, token),
        error=ValueError, code='FAST_TRACK_SOURCE_VALIDATION_IDENTITY_MISMATCH',
        job_code='FAST_TRACK_SOURCE_VALIDATION_ACTIVE_JOB_UNPROVEN', clock=clock, sleep=sleep)
    if args.event == 'push':
        if not re.fullmatch(r'(?:fix|repair|isolation)/[^\s]+', args.head_branch or ''):
            raise ValueError('FAST_TRACK_DEVELOPMENT_BRANCH_UNAUTHORIZED')
        return dict(phase='development-validation-only', releaseAdmission=False,
            candidate=args.current_sha, branch=args.head_branch,
            runId=args.current_run_id, runAttempt=args.run_attempt)
    pulls = [pr for pr in run.get('pull_requests', []) if
        pr.get('base', {}).get('ref') == FAST_TRACK_PR_BASE and pr.get('head', {}).get('sha') == args.current_sha]
    if len(pulls) != 1:
        raise ValueError('FAST_TRACK_SOURCE_VALIDATION_PR_UNPROVEN')
    pr = api_get(args.repository, f"pulls/{pulls[0]['number']}", token)
    if (pr.get('state') != 'open' or pr.get('base', {}).get('ref') != FAST_TRACK_PR_BASE or
        pr.get('head', {}).get('sha') != args.current_sha or
        pr.get('head', {}).get('repo', {}).get('full_name') != args.repository):
        raise ValueError('FAST_TRACK_SOURCE_VALIDATION_PR_CHANGED')
    preflight = approved_head_preflight(args.repository, args.current_sha, token, base_branch=FAST_TRACK_PR_BASE)
    if not preflight['current']:
        raise ValueError('FAST_TRACK_BASE_CHANGED')
    return dict(phase='source-validation-only', releaseAdmission=False, baseBranch=FAST_TRACK_PR_BASE,
        baseSha=preflight['approvedHeadSha'], candidate=args.current_sha, pullRequest=pr['number'])


def parfait_push_coverage(repository, revision, branch, event, token):
    """Delegate redundant development work only to an observed exact-head PR lane.

    This is scheduling evidence, never a validation receipt. The PR check must
    still finish successfully under the existing release admission rules.
    Missing, stale, malformed or unavailable evidence runs normal push checks.
    """
    result = dict(skip=False, reason='coverage_unproven', validationAccepted=False)
    if event != 'push':
        return dict(result, reason='not_push')
    if (not token or not re.fullmatch('[a-f0-9]{40}', revision or '')
        or not re.fullmatch(r'(?:fix|repair|isolation)/[^\s]+', branch or '')):
        return result
    def matches(pr):
        return (pr.get('state') == 'open'
            and pr.get('head', {}).get('sha') == revision
            and pr.get('head', {}).get('ref') == branch
            and pr.get('head', {}).get('repo', {}).get('full_name') == repository
            and pr.get('base', {}).get('ref') == FAST_TRACK_PR_BASE
            and pr.get('base', {}).get('repo', {}).get('full_name') == repository)
    try:
        pulls = api_get(repository, f'commits/{revision}/pulls?per_page=100', token)
        candidates = [pr for pr in pulls if matches(pr)]
        if len(candidates) != 1:
            return result
        number = candidates[0]['number']
        base_sha = candidates[0]['base'].get('sha')
        base = api_get(repository, 'git/ref/heads/' + FAST_TRACK_PR_BASE, token)
        if not re.fullmatch('[a-f0-9]{40}', base_sha or '') or base['object']['sha'] != base_sha:
            return result
        workflow = 'parfait-isolated-validation.yml'
        runs = api_get(repository, f'actions/workflows/{workflow}/runs?event=pull_request&head_sha={revision}&per_page=100', token)
        matching = [run for run in runs['workflow_runs']
            if run.get('event') == 'pull_request' and run.get('head_sha') == revision
            and run.get('path') == '.github/workflows/' + workflow
            and run.get('head_repository', {}).get('full_name') == repository
            and any(pr.get('number') == number and pr.get('head', {}).get('sha') == revision
                    and pr.get('base', {}).get('ref') == FAST_TRACK_PR_BASE
                    and pr.get('base', {}).get('sha') == base_sha
                    for pr in run.get('pull_requests', []))]
        if not matching:
            return result
        run = max(matching, key=lambda row: (row['id'], row['run_attempt']))
        def covered(row):
            return (row.get('status') in {'queued', 'in_progress'} and row.get('conclusion') is None
                or row.get('status') == 'completed' and row.get('conclusion') == 'success')
        if not covered(run):
            return result
        jobs = api_get(repository, f"actions/runs/{run['id']}/attempts/{run['run_attempt']}/jobs?per_page=100", token)
        owners = [job for job in jobs['jobs'] if job.get('name') == 'build-and-focused-tests']
        if len(owners) != 1 or not covered(owners[0]):
            return result
        # Re-read mutable PR state after observing the replacement lane.
        fresh = api_get(repository, f'pulls/{number}', token)
        if not matches(fresh) or fresh.get('base', {}).get('sha') != base_sha:
            return result
        return dict(result, skip=True, reason='exact_head_pr_lane', pullRequest=number,
            candidate=revision, baseSha=base_sha, sourceJob=owners[0]['id'],
            sourceRun=run['id'], sourceAttempt=run['run_attempt'])
    except (OSError, ValueError, KeyError, TypeError, AttributeError, RuntimeError):
        return dict(result, reason='coverage_lookup_unavailable')


def cmd_parfait_push_coverage(args):
    result = parfait_push_coverage(args.repository, args.revision, args.branch,
        args.event, os.environ.get('GITHUB_TOKEN'))
    Path(args.output).write_text(json.dumps(result, indent=2) + '\n')
    with open(args.github_output, 'a') as output:
        output.write('skip=' + str(result['skip']).lower() + '\n')


def cmd_plan(args):
    base = getattr(args, 'validation_base', TRUSTED_PR_BASE)
    if base not in {TRUSTED_PR_BASE, FAST_TRACK_PR_BASE}:
        raise ValueError('Unsupported validation base')
    scope = fast_track_source_validation_scope(args) if base == FAST_TRACK_PR_BASE else None
    with _validation_base_scope(base):
        return _cmd_plan(args, source_scope=scope)


@contextlib.contextmanager
def _validation_base_scope(base):
    token = _VALIDATION_PR_BASE.set(base)
    try:
        yield
    finally:
        _VALIDATION_PR_BASE.reset(token)


def _cmd_plan(args, *, source_scope=None):
    if source_scope is None:
        require_readiness(args.repository, args.current_sha, event=getattr(args, "event", None))
    if args.workflow not in WORKFLOWS:
        raise SystemExit(f"Unsupported validation workflow: {args.workflow}")

    plan = None
    last_evidence_error = None
    for attempt in range(4):
        try:
            plan = _compute_validation_plan_once(args)
            break
        except EvidenceLookupUnavailable as exc:
            last_evidence_error = exc
            if getattr(args, "event", None) != "pull_request":
                plan = _fresh_plan_when_evidence_unavailable(args, exc)
                break
            if attempt == 3:
                break
            delay = _evidence_retry_delay(exc, attempt)
            # The workflow timeout remains the outer safety bound. Do not turn a
            # long provider reset into broad validation; preserve child proof and
            # retry only the planning boundary.
            if delay > 900:
                break
            print(
                f"Validation evidence lookup unavailable; preserving known child proof "
                f"and retrying planner boundary in {delay}s (attempt {attempt + 1}/4)."
            )
            time.sleep(delay)
        except Exception as exc:
            _stop_unresolved_planning(args, exc)

    if plan is None:
        _stop_unresolved_planning(args, last_evidence_error or RuntimeError("Validation planning unavailable"))

    plan["validationBase"] = _VALIDATION_PR_BASE.get()
    if source_scope is not None:
        plan.pop("sourceValidationScope", None)
        plan.pop("developmentValidationScope", None)
        key = ("developmentValidationScope" if source_scope['phase'] == 'development-validation-only'
               else "sourceValidationScope")
        plan[key] = source_scope
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(plan, indent=2, sort_keys=True) + "\n")
    print(json.dumps({key: value for key, value in plan.items() if key != "dependencyManifests"}, indent=2, sort_keys=True))



def cmd_preserved(args):
    plan = json.loads(Path(args.plan).read_text())
    gate = plan.get("gates", {}).get(args.gate)
    if gate is None:
        print(f"Unknown gate {args.gate}; run it.", file=sys.stderr)
        raise SystemExit(1)
    if gate.get("run"):
        print(f"RUN {args.gate}: {gate.get('reason')}")
        raise SystemExit(1)
    print(
        f"PRESERVED {args.gate}: successful evidence from "
        f"run {gate.get('evidenceRunId', plan.get('priorRunId'))} at "
        f"{gate.get('evidenceHeadSha', plan.get('priorHeadSha'))} "
        f"({gate.get('evidenceSource', plan.get('evidenceSource'))})"
    )
    raise SystemExit(0)



MERGE_VALIDATION_NEUTRAL_PATHS = frozenset({
    ".github/workflows/masterapp-platform-architecture-validation.yml",
    ".github/workflows/approved-release-security-validation.yml",
    ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
    ".github/workflows/all-intentional-direct-release-20260918.yml",
    "Docs/releases/direct-release-request.json",
    "scripts/approved-release-baseline.py",
    "scripts/release-package.py",
    "scripts/validation-resume.py",
    "scripts/test-validation-resume.py",
    "scripts/release-lifecycle.py",
    "scripts/test-release-policy.py",
    "scripts/test-release-lifecycle.py",
    "scripts/test-deploy-approved-app.py",
    "tests/website/legend-public-cms.test.mjs",
    "AgentPortal.Tests/WebsiteStudioV3ContractTests.cs",
})

STEP5_RELEASE_EVIDENCE_PATHS = frozenset({
    ".github/workflows/all-intentional-direct-release-20260918.yml",
    ".github/workflows/masterapp-platform-architecture-validation.yml",
    "scripts/approved-release-baseline.py",
    "scripts/release-package.py",
    "scripts/validation-resume.py",
})

SECURITY_RELEASE_EVIDENCE_PATHS = frozenset({
    "scripts/validation-resume.py",
})

PUBLIC_WEBSITE_EXACT_PATHS = frozenset({
    "AgentPortal.Tests/WebsiteContentEditorRoundTripTests.cs",
    "AgentPortal.Tests/WebsiteSiteSourceV3Tests.cs",
    "Legend-Design/legend-web-foundation.css",
    "Legend-Website/scripts/build.mjs",
    "Legend-Website/public/web.config",
    "Protect-Website/Views/Shared/_Layout.cshtml",
    "SHARED/WebsitePlatform/legend-public-cms.js",
    "SHARED/WebsitePlatform/legend-public-web.css",
    "Infrastructure/WebsiteEditing/WebsiteContentSanitizer.cs",
    "Infrastructure/WebsiteEditing/WebsiteEditorContracts.cs",
    "Infrastructure/WebsiteEditing/WebsitePlatformController.cs",
    "Infrastructure/WebsiteEditing/WebsiteSiteSource.cs",
    "Infrastructure/WebsiteEditing/WebsiteStudioAgentContract.cs",
    "Infrastructure/WebsiteRuntime/BusinessWebsiteMiddleware.cs",
})


def validation_neutral_path(path: str) -> bool:
    return path in MERGE_VALIDATION_NEUTRAL_PATHS or path.startswith("tests/")


def _workflow_affected_by_path(workflow_name: str, path: str) -> bool:
    config = WORKFLOWS[workflow_name]
    if matches(path, config.get("force_all", ())):
        return True
    return any(gate_matches(path, gate) for gate in config["gates"].values())


def required_validation_topology(changed_paths):
    """Return the exact validation workflows required before integration.

    This is the single merge-readiness topology. Release lifecycle consumes the
    answer; it does not maintain subsystem path registries of its own.
    """
    names = tuple(dict.fromkeys(changed_paths))
    architecture = ".github/workflows/masterapp-platform-architecture-validation.yml"
    step5 = ".github/workflows/step5-isolated-conversion-mapping-validation.yml"
    step6 = ".github/workflows/step6-openai-ads-execution-validation.yml"
    step78 = ".github/workflows/steps7-8-governed-advertising-validation.yml"
    security = ".github/workflows/approved-release-security-validation.yml"

    required = {architecture}
    release_control_authority_change = any(release_control_authority_path(name) for name in names)
    step5_release_evidence_change = any(name in STEP5_RELEASE_EVIDENCE_PATHS for name in names)
    security_release_evidence_change = any(name in SECURITY_RELEASE_EVIDENCE_PATHS for name in names)
    release_evidence_change = (
        release_control_authority_change
        or step5_release_evidence_change
        or security_release_evidence_change
    )

    scope_neutral = MERGE_VALIDATION_NEUTRAL_PATHS | {
        "scripts/deploy-approved-app.py",
        "scripts/release-package.py",
    }
    product_names = [
        name for name in names
        if name not in scope_neutral and not name.startswith("tests/")
    ]
    public_website_only = bool(product_names) and all(
        name in PUBLIC_WEBSITE_EXACT_PATHS for name in product_names
    )

    if step5 in names or step5_release_evidence_change:
        required.add(step5)

    shared_resume_authority_change = "scripts/validation-resume.py" in names

    step6_name = "step6-openai-ads-execution-validation.yml"
    if (
        shared_resume_authority_change
        or step6 in names
        or any(_workflow_affected_by_path(step6_name, name) for name in names)
    ):
        required.add(step6)

    step78_name = "steps7-8-governed-advertising-validation.yml"
    if (
        not public_website_only
        and (
            shared_resume_authority_change
            or step78 in names
            or any(_workflow_affected_by_path(step78_name, name) for name in names)
        )
    ):
        required.add(step78)

    broad_product_change = any(
        name.startswith((
            "AgentPortal/",
            "ClientApp/",
            "Protect-Website/",
            "ParfaitApp/",
            "SHARED/",
            "Infrastructure/",
            "Domain/",
        ))
        or name == step5
        for name in product_names
    )
    if broad_product_change and not public_website_only:
        required.add(step5)
        required.add(security)
    if release_control_authority_change or security in names or security_release_evidence_change:
        required.add(security)

    return {
        "required": tuple(sorted(required)),
        "publicWebsiteOnly": public_website_only,
        "releaseEvidenceChange": release_evidence_change,
        "releaseControlAuthorityChange": release_control_authority_change,
        "broadProductChange": broad_product_change,
    }


def _selected_release_targets(raw: str):
    return selected_release_target_keys(json.loads(raw))


def _read_provenance(host: str, target, revision: str):
    path = target["provenancePath"]
    request = urllib.request.Request(
        f"https://{host}{path}?release={revision}",
        headers={"Cache-Control": "no-cache", "User-Agent": "LEGEND-release-proof/1.0"},
    )
    with urllib.request.urlopen(request, timeout=15) as response:
        if target["static"]:
            return response.read().decode().strip()
        payload = json.load(response)
        return payload.get("sourceRevision")


def cmd_gate_identity(args):
    if args.workflow not in WORKFLOWS:
        raise ValueError("Unknown validation workflow: " + args.workflow)
    if args.gate not in WORKFLOWS[args.workflow]["gates"]:
        raise ValueError("Unknown validation gate: " + args.gate)
    identity = gate_dependency_manifests(args.workflow, args.revision)[args.gate]["contentIdentity"]
    print(identity)
    if args.github_output:
        with Path(args.github_output).open("a") as output:
            output.write(f"identity={identity}\n")


def cmd_live_state(args):
    keys = _selected_release_targets(args.selected_targets)
    result = {
        "schemaVersion": 1,
        "revision": args.revision,
        "targets": {},
    }

    def probe(key):
        target = RELEASE_TARGETS[key]
        try:
            actual = _read_provenance(target["host"], target, args.revision)
            return key, actual, actual == args.revision
        except Exception as exc:
            return key, type(exc).__name__, False

    selected_results = {}
    if keys:
        with concurrent.futures.ThreadPoolExecutor(max_workers=min(8, len(keys))) as pool:
            selected_results = {
                key: (actual, live)
                for key, actual, live in pool.map(probe, keys)
            }

    # Preserve the canonical inventory order in receipts/GITHUB_OUTPUT even
    # though independent network reads execute concurrently.
    for key, target in RELEASE_TARGETS.items():
        selected = key in keys
        actual, live = selected_results.get(key, (None, False))
        result["targets"][key] = {
            "releaseName": target["releaseName"],
            "selected": selected,
            "alreadyLive": live,
            "actual": actual,
        }
        print(json.dumps({
            "target": target["releaseName"],
            "selected": selected,
            "alreadyLive": live,
            "actual": actual,
            "expected": args.revision if selected else None,
        }, sort_keys=True))
    # Keep canonical RELEASE_TARGETS insertion order in the persisted receipt.
    # Sorting nested object keys would alphabetize target names and destroy the
    # deterministic inventory order even though the probes themselves are parallel.
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n")
    if args.github_output:
        with Path(args.github_output).open("a") as output:
            for key in RELEASE_TARGETS:
                output.write(f"{key}_live={str(result['targets'][key]['alreadyLive']).lower()}\n")


def cmd_verify_live(args):
    keys = _selected_release_targets(args.selected_targets)
    desired = {key: dict(producerRevision=args.revision) for key in keys}
    if getattr(args, 'transaction_plan', None):
        import importlib.util
        spec = importlib.util.spec_from_file_location('live_deployment_owner', Path(__file__).with_name('deploy-approved-app.py'))
        owner = importlib.util.module_from_spec(spec); spec.loader.exec_module(owner)
        plan = owner.read_transaction_plan(Path(args.transaction_plan), args.revision)
        if set(keys) != {row['app'] for row in plan['targets']}:
            raise ValueError('Live proof scope differs from prepared transaction')
        desired = {row['app']: owner.transaction_target_material(plan, row, args.revision) for row in plan['targets']}
    work = [
        (key, host)
        for key in keys
        for host in RELEASE_TARGETS[key]["proofHosts"]
    ]

    def verify(item):
        key, host = item
        target = RELEASE_TARGETS[key]
        producer = desired[key]['producerRevision']
        end = time.monotonic() + args.timeout_seconds
        actual = None
        while time.monotonic() < end:
            try:
                actual = _read_provenance(host, target, producer)
                if actual == producer:
                    return {
                        "target": target["releaseName"],
                        "host": host,
                        "passed": True,
                        "actual": actual,
                        "candidateRevision": args.revision,
                        "targetMaterial": desired[key],
                    }
            except Exception as exc:
                actual = type(exc).__name__
            time.sleep(args.poll_seconds)
        return {
            "target": target["releaseName"],
            "host": host,
            "passed": False,
            "actual": actual,
        }

    with concurrent.futures.ThreadPoolExecutor(max_workers=min(8, len(work))) as pool:
        rows = list(pool.map(verify, work))
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(rows, indent=2, sort_keys=True) + "\n")
    for row in rows:
        print(json.dumps(row, sort_keys=True))
    if not rows or not all(row["passed"] for row in rows):
        raise SystemExit("Final live provenance proof failed")


def release_target_rows():
    return tuple(
        (
            key,
            row["host"],
            row["project"],
        )
        for key, row in RELEASE_TARGETS.items()
    )


def lifecycle_authority_identity():
    """Hash both present authority bytes and intentionally absent protected paths."""
    digest = hashlib.sha256()
    for path in LIFECYCLE_AUTHORITY_PATHS:
        source = Path(path)
        digest.update(path.encode())
        digest.update(b"\0")
        if source.is_file():
            digest.update(b"present\0")
            digest.update(hashlib.sha256(source.read_bytes()).digest())
        elif source.exists():
            raise RuntimeError(f"Lifecycle authority path is not a regular file: {path}")
        else:
            # Retired production bypasses remain in the protected authority set.
            # Their required absence is therefore part of the canonical identity,
            # and any future reintroduction changes the identity instead of
            # crashing receipt generation or silently escaping protection.
            digest.update(b"absent\0")
    return digest.hexdigest()


def compute_lifecycle_evidence(repository: str):
    result = {
        "schemaVersion": 1,
        "identity": lifecycle_authority_identity(),
        "artifact": None,
        "runId": None,
        "reusable": False,
    }
    result["artifact"] = f"legend-lifecycle-contracts-{result['identity']}"
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        result["reason"] = "github_token_unavailable"
        return result
    workflow_path = ".github/workflows/legend-release-lifecycle.yml"
    try:
        artifacts = _artifact_rows(repository, result["artifact"], token)
        for artifact in artifacts:
            run_id = int((artifact.get("workflow_run") or {}).get("id") or 0)
            if not run_id:
                continue
            run = api_get(repository, f"actions/runs/{run_id}", token)
            if (
                run.get("path") == workflow_path
                and run.get("status") == "completed"
                and run.get("conclusion") == "success"
                and (run.get("head_repository") or {}).get("full_name") == repository
            ):
                result.update({
                    "runId": run_id,
                    "reusable": True,
                    "reason": "exact_lifecycle_authority_receipt",
                })
                return result
    except Exception as exc:
        # Evidence lookup failure must disable reuse, not erase the deterministic
        # local lifecycle identity needed to run and retain fresh safety proof.
        result.update({
            "reason": "lifecycle_evidence_lookup_unavailable",
            "plannerError": type(exc).__name__,
        })
        return result
    result["reason"] = "no_exact_lifecycle_authority_receipt"
    return result


def cmd_lifecycle_evidence(args):
    try:
        result = compute_lifecycle_evidence(args.repository)
    except Exception as exc:
        result = {
            "schemaVersion": 1,
            "identity": None,
            "artifact": None,
            "runId": None,
            "reusable": False,
            "reason": "planner_error_fail_closed",
            "plannerError": type(exc).__name__,
        }
    Path(args.output).write_text(json.dumps(result, indent=2, sort_keys=True) + "\n")
    print(json.dumps(result, indent=2, sort_keys=True))


def package_identity_for_revision(revision: str) -> str:
    completed = subprocess.run(
        [
            sys.executable,
            str(Path(__file__).with_name("release-package.py")),
            "identity",
            "--revision",
            revision,
        ],
        text=True,
        capture_output=True,
    )
    if completed.returncode:
        raise RuntimeError(completed.stderr or "package_identity_failed")
    payload = json.loads(completed.stdout)
    identity = payload.get("identity")
    if not isinstance(identity, str) or not re.fullmatch(r"[0-9a-f]{64}", identity):
        raise RuntimeError("package_identity_invalid")
    return identity


def component_index_name(component, content_identity):
    if component not in RELEASE_TARGETS or not re.fullmatch('[a-f0-9]{64}', content_identity or ''):
        raise ValueError('Invalid component evidence identity')
    return 'validated-component-index-' + component + '-' + content_identity


def component_attempt_name(component, content_identity, run_id, attempt, *, receipt=False):
    component_index_name(component, content_identity)
    if type(run_id) is not int or run_id < 1 or type(attempt) is not int or attempt < 1:
        raise ValueError('Invalid component producer attempt')
    return ('validated-component-receipt-' if receipt else 'validated-component-bytes-') + \
        f'{component}-{content_identity}-{run_id}-a{attempt}'


def trusted_component_run(repository, run, candidate):
    """Use the real PR base as authority; candidate names never grant trust."""
    workflow = '.github/workflows/' + PACKAGE_VALIDATION_WORKFLOW
    if not _trusted_lineage_run(repository, run, workflow, candidate, require_completed=False):
        return False
    pulls = run.get('pull_requests')
    if pulls == []:
        # Merged PR runs lose this mutable association. GitHub's existing
        # commit-to-PR endpoint binds the immutable producer after merge.
        token = os.environ.get('GITHUB_TOKEN') or os.environ.get('GH_TOKEN') or ''
        if not token:
            raise EvidenceLookupUnavailable('Package producer PR association credential unavailable')
        associated = api_get(repository, f"commits/{run['head_sha']}/pulls?per_page=100", token)
        if not isinstance(associated, list) or len(associated) >= 100:
            raise EvidenceLookupUnavailable('Package producer PR association inventory incomplete')
        pulls = [row for row in associated if
            row.get('base', {}).get('ref') == TRUSTED_PR_BASE and
            all(row.get(side, {}).get('repo', {}).get('url') == 'https://api.github.com/repos/' + repository
                for side in ('head', 'base'))]
    if not isinstance(pulls, list) or len(pulls) != 1:
        return False
    head, base = pulls[0].get('head', {}), pulls[0].get('base', {})
    url = 'https://api.github.com/repos/' + repository
    approved, producer = base.get('sha', ''), run['head_sha']
    if (base.get('ref') != TRUSTED_PR_BASE or not re.fullmatch('[a-f0-9]{40}', head.get('sha', '')) or
            not re.fullmatch('[a-f0-9]{40}', approved) or
            any(row.get('repo', {}).get('url') != url for row in (head, base))):
        return False
    # GitHub refreshes pull_requests[].head when the PR advances. The run's
    # head_sha remains the immutable producer; require it in the reported lineage.
    if subprocess.run(['git', 'merge-base', '--is-ancestor', producer, head['sha']],
                      capture_output=True, check=False).returncode:
        return False
    if subprocess.run(['git', 'merge-base', '--is-ancestor', approved, producer],
                      capture_output=True, check=False).returncode:
        # GitHub also refreshes the PR base SHA. Recover only an actual protected
        # first-parent ancestor, never an arbitrary candidate branch ancestor.
        history = subprocess.check_output(['git', 'rev-list', '--first-parent', approved], text=True).splitlines()
        approved = next((sha for sha in history if re.fullmatch('[a-f0-9]{40}', sha) and sha != producer and
            subprocess.run(['git', 'merge-base', '--is-ancestor', sha, producer],
                           capture_output=True, check=False).returncode == 0), None)
        if approved is None:
            return False
    # Includes the entire approved workflow so another candidate-controlled job
    # cannot become an artifact writer. Authority changes use governed bootstrap.
    paths = PACKAGE_AUTHORITY_PATHS | {'scripts/validation-resume.py'}
    try:
        return all(git_show_file(producer, path) == git_show_file(approved, path) for path in paths)
    except (ValueError, RuntimeError):
        return False


def component_upload_proven(artifact, job, step_name, log):
    """The exact producer log binds immutable IDs, even after artifact substitution."""
    import datetime
    def stamp(value):
        parsed = datetime.datetime.fromisoformat(value.replace('Z', '+00:00'))
        if parsed.utcoffset() is None:
            raise ValueError('Component upload timestamp lacks timezone')
        return parsed
    steps = [step for step in job.get('steps', []) if step.get('name') == step_name]
    if len(steps) != 1 or steps[0].get('conclusion') != 'success':
        raise ValueError('Component upload step unproven')
    start, end = stamp(steps[0]['started_at']), stamp(steps[0]['completed_at'])
    # GitHub job step timestamps have second precision; logs retain fractions.
    # Interpret that represented final second as a half-open interval.
    if '.' not in steps[0]['completed_at']:
        end += datetime.timedelta(seconds=1)
    if not start <= stamp(artifact['created_at']) < end:
        raise ValueError('Component artifact outside producing upload step')
    expected = re.compile(r'Artifact ' + re.escape(artifact['name']) +
        r' has been successfully uploaded! Final size is [0-9]+ bytes\. Artifact ID is ' + str(artifact['id']) + r'$')
    matches = []
    for line in log.splitlines():
        parts = line.split(' ', 1)
        if len(parts) != 2 or not expected.fullmatch(parts[1]):
            continue
        if start <= stamp(parts[0]) < end:
            matches.append(line)
    if len(matches) != 1:
        raise ValueError('Component artifact not attested by its producing upload log')


def compatible_component_evidence(repository, revision, component, token, *, current_run_id=None, expected_source=None):
    """Indexed child recovery; parent failure never erases a successful producer.

    Stable index artifacts locate runs only. They confer no success or authority.
    Immutable attempt receipts and the actual successful child establish reuse.
    Provider failure escapes to the existing planner's blocked boundary.
    """
    with evidence_lookup_budget(time.monotonic() + 180):
        import importlib.util
        desired = package_component_manifest(revision, component)
        identity = desired['contentIdentity']
        if expected_source is not None:
            if not isinstance(expected_source, dict) or type(expected_source.get('runId')) is not int or expected_source['runId'] < 1:
                raise ValueError('Exact historical component source required')
            runs = {expected_source['runId']}
        else:
            name = component_index_name(component, identity)
            data = api_get(repository, 'actions/artifacts?name=' + urllib.parse.quote(name, safe='') + '&per_page=100', token)
            rows = data.get('artifacts')
            if not isinstance(rows, list) or data.get('total_count') != len(rows):
                raise EvidenceLookupUnavailable('Component evidence index incomplete')
            runs = set()
            for row in rows:
                if row.get('name') != name:
                    raise ValueError('Component index query identity mismatch')
                run_id = row.get('workflow_run', {}).get('id')
                if type(run_id) is int and run_id > 0 and row.get('expired') is False:
                    runs.add(run_id)
            if current_run_id is not None:
                if type(current_run_id) is not int or current_run_id < 1:
                    raise ValueError('Invalid current component run')
                runs.add(current_run_id)  # Also recover a child whose locator acknowledgment was lost.
            if len(runs) > 100:
                raise EvidenceLookupUnavailable('Component evidence index exceeds reconciliation budget')
        for run_id in sorted(runs, reverse=True):
            run = api_get(repository, f'actions/runs/{run_id}', token)
            if not trusted_component_run(repository, run, revision):
                continue
            if run.get('id') != run_id:
                raise ValueError('Component producer run identity mismatch')
            producer = run['head_sha']
            original = package_component_manifest(producer, component)
            if (original['contentIdentity'] != identity or
                    (producer != revision and (not original['reusable'] or not desired['reusable']))):
                continue
            inventory = api_get(repository, f'actions/runs/{run_id}/artifacts?per_page=100', token)
            artifacts = inventory.get('artifacts')
            if not isinstance(artifacts, list) or inventory.get('total_count') != len(artifacts):
                raise EvidenceLookupUnavailable('Component attempt inventory incomplete')
            if (any(type(row.get('id')) is not int or row['id'] < 1 for row in artifacts) or
                    len({row['id'] for row in artifacts}) != len(artifacts) or
                    len({row.get('name') for row in artifacts}) != len(artifacts)):
                raise ValueError('Component artifact inventory ambiguous')
            pattern = re.compile(re.escape(f'validated-component-receipt-{component}-{identity}-{run_id}-a') + r'([1-9][0-9]*)$')
            receipts = []
            for artifact in artifacts:
                match = pattern.fullmatch(artifact.get('name', ''))
                if match and artifact.get('expired') is False:
                    receipts.append((int(match.group(1)), artifact))
            for attempt, artifact in sorted(receipts, key=lambda row: row[0], reverse=True):
                if expected_source is not None and (attempt != expected_source.get('runAttempt') or artifact['id'] != expected_source.get('receiptArtifactId')):
                    continue
                if attempt > int(run.get('run_attempt') or 0):
                    raise ValueError('Component receipt claims an unobserved attempt')
                jobs = api_get(repository, f'actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100', token)
                if not isinstance(jobs.get('jobs'), list) or jobs.get('total_count') != len(jobs['jobs']):
                    raise EvidenceLookupUnavailable('Component producing jobs incomplete')
                matches = [job for job in jobs['jobs'] if job.get('name') == f'validated-release-package-components ({component})']
                if len(matches) != 1:
                    continue
                job = matches[0]
                if job.get('status') != 'completed' or job.get('conclusion') != 'success':
                    continue
                steps = {step.get('name'): step.get('conclusion') for step in job.get('steps', [])}
                if not all(steps.get(step) == 'success' for step in (
                        'Build immutable validated release package component',
                        'Preserve immutable validated release package component',
                        'Record immutable component source', 'Preserve immutable component source')):
                    continue
                receipt = _release_history_json(repository, run_id, artifact, 'component-source.json')
                expected = dict(schemaVersion=1, component=component, contentIdentity=identity,
                                producerRevision=producer, runId=run_id, runAttempt=attempt)
                if any(receipt.get(key) != value for key, value in expected.items()):
                    raise ValueError('Component source receipt producer mismatch')
                source = receipt.get('artifact')
                if (not isinstance(source, dict) or type(source.get('id')) is not int or source['id'] < 1 or
                        source.get('name') != component_attempt_name(component, identity, run_id, attempt) or
                        not re.fullmatch('sha256:[a-f0-9]{64}', source.get('digest', '')) or
                        not re.fullmatch('[a-f0-9]{64}', receipt.get('sha256', '')) or
                        not re.fullmatch('[a-f0-9]{64}', receipt.get('executionIdentity', ''))):
                    raise ValueError('Component source artifact identity malformed')
                payloads = [row for row in artifacts if row.get('id') == source['id']]
                if len(payloads) != 1:
                    continue  # Confirmed missing bytes invalidate only this component.
                payload = payloads[0]
                if expected_source is not None and (payload['id'] != expected_source.get('artifactId') or job['id'] != expected_source.get('producingJobId') or
                        payload.get('name') != expected_source.get('artifactName') or payload.get('digest') != expected_source.get('artifactDigest')):
                    raise ValueError('Historical component immutable identity mismatch')
                if payload.get('expired') is True:
                    continue
                if (payload.get('expired') is not False or payload.get('name') != source['name'] or
                        payload.get('digest') != source['digest']):
                    raise ValueError('Component artifact substitution detected')
                log = _release_job_log(repository, job['id'], token)
                component_upload_proven(payload, job, 'Preserve immutable validated release package component', log)
                component_upload_proven(artifact, job, 'Preserve immutable component source', log)
                spec = importlib.util.spec_from_file_location('component_package_evidence', Path(__file__).with_name('release-package.py'))
                package = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(package)
                if (package.component_execution_identity(receipt.get('execution'), producer, component) != receipt['executionIdentity'] or
                        receipt['execution']['sha256'] != receipt['sha256']):
                    raise ValueError('Component execution identity mismatch')
                return dict(state='reused-success', sourceReceipt=receipt,
                            receiptArtifactId=artifact['id'], producingJobId=job['id'],
                            compatibilityProof=dict(contentIdentity=identity, candidateRevision=revision,
                                producerRevision=producer, executionIdentity=receipt['executionIdentity']),
                            reason='authenticated_compatible_component_child')
        return dict(state='required', reason='compatible_component_child_missing', contentIdentity=identity)


def compute_package_canary_plan(repository, current_sha, base_sha, current_run_id, head_branch):
    result = {"schemaVersion": 1, "needed": True, "currentSha": current_sha,
              "evidenceRunId": None, "evidenceHeadSha": None, "changedInputs": [],
              "reason": "compatible_immutable_package_missing"}
    # Package production is driven by package-producing inputs, not artifact
    # discovery. A control/test/lifecycle-only head has no new application bytes
    # to stamp with the current commit and therefore must not manufacture a
    # redundant immutable package merely because historical artifact lookup misses.
    result['changedInputs'] = sorted(
        path for path in git_changed(base_sha, current_sha)
        if package_canary_input_path(path)
    )
    if not result['changedInputs']:
        result.update({
            "needed": False,
            "reason": "no_package_producing_inputs_changed",
        })
        return result

    token = os.environ.get('GITHUB_TOKEN') or os.environ.get('GH_TOKEN') or ''
    if token:
        compatible = compatible_package_producer(repository, current_sha, token)
        if compatible:
            result.update({"needed": False, "reason": compatible['reason'],
                           "evidenceRunId": compatible['runId'],
                           "evidenceHeadSha": compatible['revision'],
                           "packageIdentity": compatible['packageIdentity'],
                           "exactPackageRunId": compatible['runId'],
                           "exactPackageArtifact": compatible['artifact']})
            return result
    return result


def component_protocol_ready(base, candidate):
    """Activation requires the installed approved owner, never a rollout exemption."""
    if 'CONTENT_PACKAGE_SCHEMA' not in git_show_file(base, 'scripts/release-package.py'):
        return False
    paths = PACKAGE_AUTHORITY_PATHS | {'scripts/validation-resume.py'}
    return all(git_show_file(base, path) == git_show_file(candidate, path) for path in paths)


def plan_component_children(repository, revision, run_id, token):
    if not token:
        raise EvidenceLookupUnavailable('Component evidence credential unavailable')
    with evidence_lookup_budget(time.monotonic() + 180):
        children = {key: compatible_component_evidence(repository, revision, key, token,
                    current_run_id=run_id) for key in RELEASE_TARGETS}
    return dict(componentProtocol=True, componentSources=children,
                requiredComponents=[key for key, row in children.items() if row['state'] == 'required'],
                reusedComponents=[key for key, row in children.items() if row['state'] == 'reused-success'])


def cmd_component_sources(args):
    """Reconstruct aggregate completion from authenticated successful children."""
    result = plan_component_children(args.repository, args.revision, args.current_run_id,
        os.environ.get('GITHUB_TOKEN') or os.environ.get('GH_TOKEN') or '')
    if result['requiredComponents']:
        raise RuntimeError('COMPONENT_ASSEMBLY_BLOCKED:successful_children_missing:' + ','.join(result['requiredComponents']))
    Path(args.output).write_text(json.dumps(result['componentSources'], sort_keys=True, indent=2) + '\n')
    if getattr(args, 'github_output', None):
        with open(args.github_output, 'a') as stream:
            for key, selected in result['componentSources'].items():
                source = selected['sourceReceipt']
                stream.write(f"{key}_run_id={source['runId']}\n{key}_artifact_id={source['artifact']['id']}\n")


def pinned_package_evidence(repository, candidate, selected, token):
    """Authenticate one rehearsed immutable aggregate; never replace it with newer bytes."""
    producer = selected.get('producerRevision')
    if (not re.fullmatch('[a-f0-9]{40}', producer or '') or
            not re.fullmatch('[a-f0-9]{64}', selected.get('packageIdentity', '')) or
            not re.fullmatch('sha256:[a-f0-9]{64}', selected.get('artifactDigest', '')) or
            any(type(selected.get(key)) is not int or selected[key] < 1 for key in ('runId','artifactId')) or
            (producer != candidate and not package_inputs_compatible(producer, candidate))):
        raise ValueError('Pinned package dependency or source identity invalid')
    match = package_artifact_match(selected.get('artifact',''))
    if not match or match.group(1) != selected['packageIdentity']:
        raise ValueError('Pinned package artifact name disagrees with identity')
    run = api_get(repository, f"actions/runs/{selected['runId']}", token)
    path = '.github/workflows/' + PACKAGE_VALIDATION_WORKFLOW
    if not _trusted_lineage_run(repository, run, path, candidate) or run.get('head_sha') != producer:
        raise ValueError('Pinned package producing authority unproven')
    if match.group(2) is None and not _successful_package_child(repository, selected['runId'], token):
        raise EvidenceLookupUnavailable('Pinned legacy package producing job unavailable')
    artifact = api_get(repository, f"actions/artifacts/{selected['artifactId']}", token)
    if (artifact.get('id') != selected['artifactId'] or artifact.get('expired') is not False or
            artifact.get('name') != selected['artifact'] or artifact.get('digest') != selected['artifactDigest'] or
            artifact.get('workflow_run', {}).get('id') != selected['runId']):
        raise EvidenceLookupUnavailable('Pinned package immutable artifact unavailable or changed')
    result = dict(schemaVersion=1, revision=producer, requestedRevision=candidate,
        runId=selected['runId'], artifact=selected['artifact'], artifactId=selected['artifactId'],
        artifactDigest=selected['artifactDigest'], packageIdentity=selected['packageIdentity'],
        reusable=True, reason='exact_rehearsed_aggregate_preserved')
    return attach_package_descriptor(repository, result, run, token)


def bind_rehearsed_package_plan(repository, candidate, result, readiness, token):
    """A pending migration's retained aggregate is the one actually rehearsed."""
    receipt = readiness.get('receipt', {})
    if not receipt.get('pendingCount'):
        return result
    rehearsal = receipt.get('rehearsal') or {}
    selected = rehearsal.get('selectedPackage')
    if selected is None:
        if not result['needed']:
            raise ValueError('Reused aggregate has no exact rehearsed migration binding')
        return result  # Fresh assembly must promote the exact rehearsal child.
    if (selected.get('schemaVersion') != 1 or not re.fullmatch('[a-f0-9]{40}', selected.get('candidateRevision', '')) or
            selected.get('bundleDigest') != rehearsal.get('bundleDigest') or
            not re.fullmatch('[a-f0-9]{64}', selected.get('bundleDigest', ''))):
        raise ValueError('Rehearsed aggregate migration digest mismatch')
    producer = selected.get('producerRevision')
    if (not re.fullmatch('[a-f0-9]{40}', producer or '') or
            (producer != candidate and not package_inputs_compatible(producer, candidate))):
        raise ValueError('Rehearsed aggregate inputs changed')
    pinned_package_evidence(repository, candidate, selected, token)
    return dict(result, needed=False, reason='exact_rehearsed_aggregate_preserved',
        evidenceRunId=selected['runId'], evidenceHeadSha=producer, packageIdentity=selected['packageIdentity'],
        exactPackageRunId=selected['runId'], exactPackageArtifact=selected['artifact'])


def cmd_package_canary_plan(args):
    readiness = require_readiness(args.repository, args.current_sha)
    try:
        result = compute_package_canary_plan(
            args.repository,
            args.current_sha,
            args.base_sha,
            args.current_run_id,
            args.head_branch,
        )
        result = bind_rehearsed_package_plan(args.repository, args.current_sha, result, readiness,
            os.environ.get('GITHUB_TOKEN') or os.environ.get('GH_TOKEN') or '')
        result.update(componentProtocol=False, requiredComponents=list(RELEASE_TARGETS) + ['migration'])
        if result['needed'] and component_protocol_ready(args.base_sha, args.current_sha):
            result.update(plan_component_children(args.repository, args.current_sha, args.current_run_id,
                os.environ.get('GITHUB_TOKEN') or os.environ.get('GH_TOKEN') or ''))
            result['requiredComponents'].append('migration')
    except Exception as exc:
        # Unavailable or untrusted evidence is not evidence that all children are
        # missing. Preserve any existing plan/receipts and stop this boundary.
        # api_get owns bounded provider retries; never add an outer retry here.
        reason = ('package_evidence_lookup_unavailable' if isinstance(exc, EvidenceLookupUnavailable)
                  else 'package_planner_state_unproven')
        blocked = dict(schemaVersion=1, state='blocked', needed=False,
            currentSha=args.current_sha, approvedBaseSha=args.base_sha,
            runId=str(args.current_run_id), runAttempt=int(os.environ.get('GITHUB_RUN_ATTEMPT', '1')),
            reason=reason, plannerError=type(exc).__name__,
            resumeBoundary='validated-release-package-plan',
            intervention=('Restore evidence-provider access, then rerun package planning.'
                if isinstance(exc, EvidenceLookupUnavailable)
                else 'Resolve the package planner error, then rerun package planning with retained receipts.'))
        Path(str(args.output) + '.blocked.json').write_text(json.dumps(blocked, indent=2, sort_keys=True) + "\n")
        raise RuntimeError('PACKAGE_PLANNING_BLOCKED:' + reason) from None
    result['migrationReused'] = False
    if result['needed'] and readiness.get('receipt', {}).get('rehearsalSource'):
        import importlib.util
        spec = importlib.util.spec_from_file_location('canonical_package', Path(__file__).with_name('release-package.py'))
        package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(package)
        receipt = package.promote_rehearsed_migration(args.current_sha,
            Path(args.output).parent / 'validated-release-migration', readiness)
        if receipt is None:
            raise ValueError('Authenticated rehearsal component could not be promoted')
        result['migrationReused'] = True
        result['migrationSourceReceipt'] = readiness['receipt']['rehearsalSource']
        result['migrationArtifact'] = package.promoted_migration_artifact(args.current_sha,
            int(os.environ['GITHUB_RUN_ATTEMPT']))
    if result['migrationReused'] and result['componentProtocol']:
        result['requiredComponents'].remove('migration')
    Path(args.output).write_text(json.dumps(result, indent=2, sort_keys=True) + "\n")
    print(json.dumps(result, indent=2, sort_keys=True))


def package_backfill_receipt_name(revision: str, package_identity: str) -> str:
    return f"validated-package-backfill-{revision}-{package_identity}"


def compute_package_preparation_plan(repository, revision, current_sha, branch, *, preparation_only=False):
    """Nonpublishing Protect bytes only; never an aggregate or release admission."""
    if (preparation_only is not True or repository != 'MYLEGND/masterapp' or
            branch != 'isolation/receptionist-quality-pro-20261024' or
            not re.fullmatch('[0-9a-f]{40}', revision or '') or revision != current_sha):
        raise ValueError('Exact isolated preparation dispatch required')
    token = os.environ.get('GITHUB_TOKEN') or os.environ.get('GH_TOKEN') or ''
    if not token:
        raise ValueError('Authenticated preparation evidence required')
    pr = api_get(repository, 'pulls/691', token)
    def eligible(row):
        return (row.get('number') == 691 and row.get('state') == 'open' and
            row.get('merged_at') is None and row.get('draft') is True and
            row.get('head', {}).get('sha') == revision and
            row.get('head', {}).get('ref') == branch and
            row.get('head', {}).get('repo', {}).get('full_name') == repository and
            row.get('base', {}).get('ref') == FAST_TRACK_PR_BASE and
            row.get('base', {}).get('repo', {}).get('full_name') == repository)
    if not eligible(pr):
        raise ValueError('Current preparation PR binding invalid')
    baseline = approved_head_preflight(repository, revision, token, base_branch=FAST_TRACK_PR_BASE)
    if baseline.get('current') is not True:
        raise ValueError('Preparation candidate does not contain current release base')
    evidence = []
    for workflow in ('approved-release-security-validation.yml',
                     'masterapp-platform-architecture-validation.yml', 'parfait-isolated-validation.yml'):
        payload = api_get(repository, 'actions/workflows/' + workflow +
            '/runs?head_sha=' + revision + '&event=pull_request&per_page=100', token)
        # A truncated/uncertain listing cannot select a possibly superseded run.
        runs = payload.get('workflow_runs', [])
        if payload.get('total_count') != len(runs) or not runs:
            raise ValueError('Complete bounded exact-head CI evidence required')
        latest = max(runs, key=lambda row: (int(row.get('id', 0)), int(row.get('run_attempt', 0))))
        associations = latest.get('pull_requests', [])
        if (latest.get('head_sha') != revision or latest.get('event') != 'pull_request' or
                latest.get('path') != '.github/workflows/' + workflow or
                latest.get('head_repository', {}).get('full_name') != repository or
                latest.get('status') != 'completed' or latest.get('conclusion') != 'success' or
                len(associations) != 1 or associations[0].get('number') != 691 or
                associations[0].get('head', {}).get('sha') != revision or
                associations[0].get('base', {}).get('ref') != FAST_TRACK_PR_BASE):
            raise ValueError('Latest exact-head preparation CI is not accepted')
        evidence.append(dict(workflow=workflow, runId=latest['id'], runAttempt=latest['run_attempt']))
    if not eligible(api_get(repository, 'pulls/691', token)):
        raise ValueError('Preparation head changed during admission')
    return dict(schema='legend-nonpublishing-package-preparation.v2', candidateRevision=revision,
        controlRevision=current_sha, branch=branch, sourcePr=691, components=['protect', 'parfait'],
        publicationAuthorized=False, releaseAdmission=False, validationRuns=evidence,
        baseEvidence=baseline)


def cmd_package_preparation_plan(args):
    result = compute_package_preparation_plan(args.repository, args.revision, args.current_sha,
        args.branch, preparation_only=args.preparation_only)
    Path(args.output).write_text(json.dumps(result, sort_keys=True, indent=2) + '\n')


def compute_package_backfill_plan(repository: str, revision: str, current_sha: str):
    """Authorize package-only recovery for an already-green historical product head.

    The current approved control plane may package the historical revision only when
    the exact product head has a successful Architecture PR validation and every
    change since that revision is release/test/control-only. Any application-source
    drift fails closed.
    """
    result = {
        "schemaVersion": 1,
        "revision": revision,
        "currentSha": current_sha,
        "allowed": False,
        "requiredComponents": list(RELEASE_TARGETS) + ['migration'],
        "validationRunId": None,
        "changedApplicationInputs": [],
    }
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN") or ""
    if not token:
        result["reason"] = "github_token_unavailable"
        return result

    ancestor = subprocess.run(
        ["git", "merge-base", "--is-ancestor", revision, current_sha],
        text=True,
        capture_output=True,
    )
    if ancestor.returncode != 0:
        result["reason"] = "revision_not_preserved_in_current_approved_history"
        return result

    changed = git_changed(revision, current_sha)
    application_changes = sorted(
        path for path in changed
        if not release_control_only_path(path)
    )
    result["changedApplicationInputs"] = application_changes
    if application_changes:
        result["reason"] = "application_inputs_changed_since_validated_revision"
        return result

    workflow_path = ".github/workflows/" + PACKAGE_VALIDATION_WORKFLOW
    encoded = urllib.parse.quote(revision, safe="")
    payload = api_get(
        repository,
        f"actions/runs?head_sha={encoded}&event=pull_request&status=completed&per_page=100",
        token,
    )
    runs = [
        run for run in payload.get("workflow_runs", [])
        if run.get("path") == workflow_path
        and run.get("event") == "pull_request"
        and run.get("status") == "completed"
        and run.get("conclusion") == "success"
        and run.get("head_sha") == revision
        and (run.get("head_repository") or {}).get("full_name") == repository
    ]
    pulls = api_get(repository, f"commits/{revision}/pulls", token)
    bound = [
        pr for pr in pulls
        if pr.get("merged_at")
        and pr.get("base", {}).get("ref") == TRUSTED_PR_BASE
        and pr.get("head", {}).get("sha") == revision
        and pr.get("head", {}).get("repo", {}).get("full_name") == repository
    ]
    if not runs or len(bound) != 1:
        result["reason"] = "exact_green_architecture_pr_evidence_missing"
        return result

    runs.sort(
        key=lambda run: (run.get("updated_at") or run.get("created_at", ""), int(run.get("id", 0))),
        reverse=True,
    )
    result.update({
        "allowed": True,
        "validationRunId": int(runs[0]["id"]),
        "sourcePr": int(bound[0]["number"]),
        "reason": "exact_green_revision_with_control_only_descendants",
    })
    return result


def cmd_package_backfill_plan(args):
    try:
        result = compute_package_backfill_plan(
            args.repository,
            args.revision,
            args.current_sha,
        )
    except Exception as exc:
        result = {
            "schemaVersion": 1,
            "revision": args.revision,
            "currentSha": args.current_sha,
            "allowed": False,
            "validationRunId": None,
            "changedApplicationInputs": [],
            "reason": "planner_error_fail_closed",
            "plannerError": type(exc).__name__,
        }
    Path(args.output).write_text(json.dumps(result, indent=2, sort_keys=True) + "\n")
    print(json.dumps(result, indent=2, sort_keys=True))




class MigrationProbeAuthorityMissing(ValueError):
    """A revision predates the required probe child and cannot supply its evidence."""


def migration_probe_identity(tool_revision, application_revision):
    """One dependency model: derive the probe runtime from infrastructure build ownership."""
    if not all(re.fullmatch(r'[0-9a-f]{40}', value or '') for value in (tool_revision, application_revision)):
        raise ValueError('Exact probe and application source revisions required')
    paths = WORKFLOWS[PACKAGE_VALIDATION_WORKFLOW]['gates']['build-infrastructure']['paths']
    # Other projects' project files do not change this direct-reference closure.
    runtime_patterns = tuple(path for path in paths if path not in {'**/*.csproj', 'MASTERAPP.sln'})
    def content(revision, patterns):
        rows = subprocess.check_output(['git', 'ls-tree', '-rz', '--full-tree', revision], text=True).split('\0')
        selected = []
        for row in rows:
            if not row:
                continue
            metadata, name = row.split('\t', 1)
            if matches(name, patterns):
                selected.append(row)
        return hashlib.sha256('\0'.join(sorted(selected)).encode()).hexdigest()
    runtime = content(application_revision, runtime_patterns)
    if runtime != content(tool_revision, runtime_patterns):
        raise ValueError('Probe was compiled against different candidate migration/runtime inputs')
    tooling = content(tool_revision, ('scripts/MigrationReleaseProbe/**', 'scripts/migration-probe-package.py'))
    workflow = git_show_file(tool_revision, '.github/workflows/' + PACKAGE_VALIDATION_WORKFLOW)
    job = _job_blocks(workflow).get('validated-migration-probe')
    if not job:
        raise MigrationProbeAuthorityMissing('Validated migration probe child authority missing')
    payload = {'schemaVersion': 1, 'runtimeIdentity': runtime, 'toolIdentity': tooling,
               'executionIdentity': hashlib.sha256(job.rstrip().encode()).hexdigest()}
    identity = hashlib.sha256(json.dumps(payload, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    return dict(payload, identity=identity, artifact='legend-migration-probe-' + identity)


def migration_probe_artifact(repository, run, name, token):
    """Bind a retained probe to its original successful job, not the latest rerun.

    Legacy manifests lack an attempt field. GitHub's immutable artifact creation
    time and complete job inventory supply that boundary without rewriting them.
    """
    import datetime
    data = api_get(repository, f"actions/runs/{run['id']}/artifacts?per_page=100", token)
    rows = data.get('artifacts')
    if not isinstance(rows, list) or data.get('total_count') != len(rows):
        raise EvidenceLookupUnavailable('Probe artifact inventory incomplete')
    artifacts = [row for row in rows if row.get('name') == name and row.get('expired') is False]
    if not artifacts:
        return None
    if len(artifacts) != 1 or type(artifacts[0].get('id')) is not int or artifacts[0]['id'] < 1:
        raise ValueError('Probe immutable artifact identity ambiguous')
    artifact = artifacts[0]
    def stamp(value):
        parsed = datetime.datetime.fromisoformat(value.replace('Z', '+00:00'))
        if parsed.utcoffset() is None:
            raise ValueError('Probe provider timestamp lacks timezone')
        return parsed
    created = stamp(artifact['created_at'])
    matches = []
    seen = 0
    for page in range(1, 11):
        data = api_get(repository, f"actions/runs/{run['id']}/jobs?filter=all&per_page=100&page={page}", token)
        jobs = data.get('jobs')
        if not isinstance(jobs, list) or type(data.get('total_count')) is not int:
            raise EvidenceLookupUnavailable('Probe job inventory incomplete')
        seen += len(jobs)
        for job in jobs:
            if (job.get('name') == 'validated-migration-probe' and job.get('status') == 'completed' and
                job.get('conclusion') == 'success' and type(job.get('run_attempt')) is int and
                1 <= job['run_attempt'] <= run.get('run_attempt', 1) and
                stamp(job['started_at']) <= created <= stamp(job['completed_at'])):
                matches.append(job)
        if seen >= data['total_count']:
            break
        if len(jobs) < 100:
            raise EvidenceLookupUnavailable('Probe job inventory incomplete')
    else:
        raise EvidenceLookupUnavailable('Probe job inventory exceeded bounded lookup')
    if len(matches) != 1:
        return None
    return {'artifactId': artifact['id'], 'producingAttempt': matches[0]['run_attempt'],
            'producerJobId': matches[0]['id']}


def migration_probe_evidence(repository, identity, *, include_active=False, candidate=None):
    token = os.environ.get('GITHUB_TOKEN') or os.environ.get('GH_TOKEN') or ''
    if not token:
        raise ValueError('Probe evidence authentication unavailable')
    workflow_path = '.github/workflows/' + PACKAGE_VALIDATION_WORKFLOW
    workflow = urllib.parse.quote(PACKAGE_VALIDATION_WORKFLOW, safe='')
    payload = api_get(repository,
        f'actions/workflows/{workflow}/runs?event=pull_request&per_page=100' + ('' if include_active else '&status=completed'), token)
    runs = sorted(payload.get('workflow_runs', []),
        key=lambda row: (row.get('updated_at') or row.get('created_at', ''), int(row.get('id', 0))),
        reverse=True)
    for run in runs:
        run_id = int(run.get('id') or 0)
        current_revision = subprocess.check_output(
            ['git', 'rev-parse', candidate or 'HEAD'], text=True).strip()
        if not run_id or not _trusted_lineage_run(repository, run, workflow_path, current_revision, require_completed=not include_active):
            continue
        try:
            producer = migration_probe_identity(run['head_sha'], run['head_sha'])
        except MigrationProbeAuthorityMissing:
            # Historical runs before this child existed cannot prove probe reuse.
            # Continue searching; without a compatible producer the caller builds.
            continue
        if producer != identity:
            continue
        artifact = migration_probe_artifact(repository, run, identity['artifact'], token)
        if not artifact:
            continue
        # The canonical child uploads only after build + manifest verification.
        # Exact execution/runtime/tool identity plus retained artifact presence is
        # the durable success proof; release re-verifies the manifest after load.
        return dict(artifact, reusable=True, runId=run_id, artifact=identity['artifact'], identity=identity['identity'])
    return {'reusable': False, 'artifact': identity['artifact']}

def package_inputs_compatible(prior: str, current: str) -> bool:
    """Compare real package-producing inputs without relabeling producer bytes."""
    import ast
    identity_only = {'contract_hash', 'package_identity', 'artifact_name', 'verify_all'}
    def observer_contract(node):
        # Only the reviewed read/hash/compare grammar may be excluded from byte
        # production. Unknown calls/syntax invalidate reuse; file-mode writes stay
        # in the producing contract rather than being called read-only.
        pure_names = {'normalize_revision', 'validate_revision', 'contract_hash', 'package_identity',
                      'sha256_file', 'embedded_revision', 'ValueError', 'FileNotFoundError',
                      'Path', 'set', 'isinstance', 'str', 'len', 'sorted'}
        pure_methods = {'exists', 'encode', 'read_bytes', 'read_text', 'hexdigest', 'update',
                        'get', 'items', 'split', 'splitlines', 'strip'}
        qualified = {'json.dumps', 'json.loads', 'hashlib.sha256', 're.fullmatch', 'os.access',
                     '_RELEASE_AUTHORITY.package_builder_workflow_contract',
                     '_RELEASE_AUTHORITY.package_inputs_compatible'}
        effects = []
        for child in ast.walk(node):
            if isinstance(child, (ast.Global, ast.Nonlocal, ast.Import, ast.ImportFrom, ast.With,
                                  ast.AsyncWith, ast.Await, ast.Yield, ast.YieldFrom)):
                return None
            if isinstance(child, (ast.Assign, ast.AnnAssign, ast.AugAssign)):
                targets = child.targets if isinstance(child, ast.Assign) else [child.target]
                if any(isinstance(target, ast.Attribute) for target in targets):
                    return None
            if not isinstance(child, ast.Call):
                continue
            function = ast.unparse(child.func)
            if function == 'bundle.chmod':
                effects.append(ast.dump(child, include_attributes=False))
            elif function == 'subprocess.check_output':
                command = child.args[0] if child.args else None
                if (not isinstance(command, ast.List) or len(command.elts) < 2 or
                        not all(isinstance(part, ast.Constant) for part in command.elts[:2]) or
                        [part.value for part in command.elts[:2]] != ['git', 'rev-parse'] or
                        any(keyword.arg not in {'cwd', 'text'} for keyword in child.keywords)):
                    return None
            elif function in pure_names or function in qualified:
                continue
            elif isinstance(child.func, ast.Attribute) and child.func.attr in pure_methods:
                continue
            else:
                return None
        return sorted(effects)

    def builder(text):
        tree = ast.parse(text)
        rows = []
        for node in tree.body:
            if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name in identity_only:
                observer = observer_contract(node)
                if observer is not None:
                    rows.append(('observer', node.name, observer))
                    continue
            if isinstance(node, ast.Expr) and isinstance(node.value, ast.Constant) and isinstance(node.value.value, str):
                continue
            if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == 'CONTRACT_INPUTS' for t in node.targets):
                continue
            rows.append(ast.dump(node, include_attributes=False))
        return rows
    for path in git_changed(prior, current):
        if path == 'scripts/release-package.py':
            if builder(git_show_file(prior, path)) != builder(git_show_file(current, path)):
                return False
        elif path == PACKAGE_BUILD_WORKFLOW:
            try:
                prior_contract = package_builder_workflow_contract(git_show_file(prior, path))
                current_contract = package_builder_workflow_contract(git_show_file(current, path))
            except ValueError:
                # A historical workflow that predates the current canonical
                # builder shape is incompatible evidence, not a planner fault.
                return False
            if prior_contract != current_contract:
                return False
        elif path == 'scripts/validation-resume.py':
            # Topology is literal canonical data, never execute historical code.
            def topology(text):
                wanted = {'RELEASE_TARGETS', 'MIGRATION_BUNDLE_NAME'}
                data = {}
                for node in ast.parse(text).body:
                    if isinstance(node, ast.Assign):
                        for target in node.targets:
                            if isinstance(target, ast.Name) and target.id in wanted:
                                data[target.id] = ast.literal_eval(node.value)
                if set(data) != wanted:
                    raise ValueError('Package topology missing')
                data['RELEASE_TARGETS'] = {key: {field: row[field] for field in ('project', 'package', 'static', 'sourceRoot')}
                                           for key, row in data['RELEASE_TARGETS'].items()}
                return data
            if topology(git_show_file(prior, path)) != topology(git_show_file(current, path)):
                return False
        elif path in PACKAGE_AUTHORITY_PATHS or not release_control_only_path(path):
            return False
    return True


def _successful_package_child(repository, run_id, token):
    jobs = api_get(repository, f'actions/runs/{run_id}/jobs?filter=latest&per_page=100', token).get('jobs', [])
    children = [job for job in jobs if job.get('name') == 'validated-release-package']
    if len(children) != 1:
        return False
    steps = {step.get('name'): step.get('conclusion') for step in children[0].get('steps', [])}
    return all(steps.get(name) == 'success' for name in (
        'Build immutable validated release package',
        'Verify immutable validated release package',
        'Preserve immutable validated release package',
    ))


def package_artifact_match(name):
    return re.fullmatch(r'founder-diagnostics-packages-([a-f0-9]{64})(?:-([1-9][0-9]*)-a([1-9][0-9]*))?', name)


class IncompletePackageAttempt(EvidenceLookupUnavailable):
    """Authenticated terminal assembly failure; earlier children remain eligible."""


def attach_package_descriptor(repository, result, run, token):
    """Authenticate small aggregate metadata; never download application bytes here."""
    match = package_artifact_match(result['artifact'])
    if not match or match.group(1) != result['packageIdentity']:
        raise ValueError('Package artifact identity malformed')
    if match.group(2) is None:
        return result  # Strict historical single-producer contract.
    run_id, attempt = int(match.group(2)), int(match.group(3))
    if (run_id != result['runId'] or run_id != run.get('id') or attempt > run.get('run_attempt', 0) or
            not trusted_component_run(repository, run, result['revision'])):
        raise ValueError('Package descriptor authority unproven')
    with evidence_lookup_budget(time.monotonic() + 180):
        inventory = api_get(repository,f'actions/runs/{run_id}/artifacts?per_page=100',token)
        artifacts = inventory.get('artifacts')
        if not isinstance(artifacts,list) or inventory.get('total_count') != len(artifacts):
            raise EvidenceLookupUnavailable('Package descriptor inventory incomplete')
        name=f"validated-package-source-{result['packageIdentity']}-{run_id}-a{attempt}"
        descriptors=[row for row in artifacts if row.get('name')==name and row.get('expired') is False]
        payloads=[row for row in artifacts if row.get('name')==result['artifact'] and row.get('expired') is False]
        jobs=api_get(repository,f'actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100',token)
        if not isinstance(jobs.get('jobs'),list) or jobs.get('total_count')!=len(jobs['jobs']):
            raise EvidenceLookupUnavailable('Package descriptor job inventory incomplete')
        owners=[row for row in jobs['jobs'] if row.get('name')=='validated-release-package']
        if (len(owners)==1 and owners[0].get('status')=='completed' and
                owners[0].get('conclusion') in {'failure','cancelled','skipped','timed_out'}):
            raise IncompletePackageAttempt('Terminal assembly attempt did not produce successful evidence')
        if len(owners)!=1 or owners[0].get('conclusion')!='success' or owners[0].get('status')!='completed':
            raise EvidenceLookupUnavailable('Successful package descriptor owner unavailable')
        if len(descriptors)!=1 or len(payloads)!=1:
            raise EvidenceLookupUnavailable('Immutable package descriptor or bytes unavailable')
        descriptor,payload=descriptors[0],payloads[0]
        owner=owners[0]
        steps={row.get('name'):row.get('conclusion') for row in owner.get('steps',[])}
        if any(steps.get(name)!='success' for name in ('Build immutable validated release package',
                'Verify immutable validated release package','Record immutable package source')):
            raise ValueError('Package descriptor verification steps unproven')
        log=_release_job_log(repository,owner['id'],token)
        component_upload_proven(payload,owner,'Preserve immutable validated release package',log)
        component_upload_proven(descriptor,owner,'Preserve immutable package source',log)
        source=_release_history_json(repository,run_id,descriptor,'package-source.json')
        import importlib.util
        spec=importlib.util.spec_from_file_location('descriptor_package_owner',Path(__file__).with_name('release-package.py'))
        package=importlib.util.module_from_spec(spec);spec.loader.exec_module(package)
        if (source.get('schemaVersion')!=1 or source.get('packageSchema')!=package.CONTENT_PACKAGE_SCHEMA or
                source.get('revision')!=result['revision'] or source.get('packageIdentity')!=result['packageIdentity'] or
                source.get('runId')!=run_id or source.get('runAttempt')!=attempt or
                source.get('artifact')!={key:payload[key] for key in ('id','name','digest')} or
                not re.fullmatch('sha256:[a-f0-9]{64}',payload.get('digest','')) or
                not re.fullmatch('[a-f0-9]{64}',source.get('manifestSha256','')) or
                set(source.get('targets',{}))!=set(RELEASE_TARGETS)):
            raise ValueError('Package descriptor producer/material mismatch')
        materials={key:package.validate_target_material(key,row) for key,row in source['targets'].items()}
        return dict(result,artifactId=payload['id'],artifactDigest=payload['digest'],
                    descriptorArtifactId=descriptor['id'],manifestSha256=source['manifestSha256'],targetMaterials=materials)


def compatible_package_producer(repository, revision, token, *, exact_revision_only=False):
    """Locate authenticated immutable bytes from a content-equivalent producer.

    Enumerate trusted completed producer runs first, then inspect each run's own
    artifacts. Repository-wide artifact listing is not required for provenance
    and can be unavailable to an active PR token.
    """
    workflow_path = '.github/workflows/' + PACKAGE_VALIDATION_WORKFLOW
    workflow = urllib.parse.quote(PACKAGE_VALIDATION_WORKFLOW, safe='')
    payload = api_get(repository,
        f'actions/workflows/{workflow}/runs?event=pull_request&status=completed&per_page=100', token)
    runs = sorted(payload.get('workflow_runs', []),
        key=lambda row: (row.get('updated_at') or row.get('created_at', ''), int(row.get('id', 0))),
        reverse=True)
    for run in runs:
        run_id = int(run.get('id') or 0)
        if not run_id or not _trusted_lineage_run(repository, run, workflow_path, revision):
            continue
        producer = run['head_sha']
        if exact_revision_only and producer != revision:
            continue
        package_input_changes = [
            path for path in git_changed(producer, revision)
            if package_canary_input_path(path)
        ]
        if package_input_changes and not package_inputs_compatible(producer, revision):
            continue
        names = sorted((name for name in _run_artifact_names(repository, run_id, token)
                       if package_artifact_match(name)), key=lambda name: int(package_artifact_match(name).group(3) or 0))
        if not names:
            continue
        for name in reversed(names):
            match = package_artifact_match(name)
            if match.group(2) is None and not _successful_package_child(repository, run_id, token):
                continue  # Preserve strict legacy evidence interpretation.
            result = {'schemaVersion': 1, 'revision': producer, 'requestedRevision': revision,
                    'packageIdentity': match.group(1),
                    'artifact': name, 'runId': run_id, 'reusable': True,
                    'reason': 'dependency_equivalent_immutable_package_producer'}
            try:
                return attach_package_descriptor(repository,result,run,token)
            except IncompletePackageAttempt:
                continue  # Only proven terminal failure permits earlier-attempt lookup.
    return None

def compute_validated_package_evidence(repository: str, revision: str, package_identity: str, *, allow_equivalent=True):
    result = {
        "schemaVersion": 1,
        "revision": revision,
        "packageIdentity": package_identity,
        "artifact": f"founder-diagnostics-packages-{package_identity}",
        "runId": None,
        "reusable": False,
    }
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN") or ""
    if not token:
        result["reason"] = "github_token_unavailable"
        return result
    workflow_path = ".github/workflows/" + PACKAGE_VALIDATION_WORKFLOW
    for artifact in _artifact_rows(repository, result["artifact"], token):
        run_id = int((artifact.get("workflow_run") or {}).get("id") or 0)
        if not run_id:
            continue
        run = api_get(repository, f"actions/runs/{run_id}", token)
        common = (
            run.get("path") == workflow_path
            and run.get("status") == "completed"
            and (run.get("head_repository") or {}).get("full_name") == repository
        )
        exact_pr = (
            common
            and run.get("event") == "pull_request"
            and run.get("head_sha") == revision
            and _trusted_pr_run(repository, run, workflow_path, token)
        )
        backfill = False
        if (
            common
            and run.get("event") == "workflow_dispatch"
            and run.get("head_branch") == TRUSTED_PR_BASE
        ):
            receipt = package_backfill_receipt_name(revision, package_identity)
            backfill = receipt in _run_artifact_names(repository, run_id, token)
        if (exact_pr or backfill) and _successful_package_child(repository, run_id, token):
            result.update({
                "runId": run_id,
                "reusable": True,
                "reason": (
                    "exact_validated_application_package"
                    if exact_pr
                    else "validated_package_backfill_from_exact_green_revision"
                ),
            })
            return result
    compatible = (compatible_package_producer(repository, revision, token) if allow_equivalent
                  else compatible_package_producer(repository, revision, token, exact_revision_only=True))
    if compatible:
        return compatible
    result["reason"] = "exact_validated_package_missing"
    return result


def cmd_validated_package(args):
    try:
        if getattr(args, 'pinned_source_json', None):
            result = pinned_package_evidence(args.repository, args.revision, json.loads(args.pinned_source_json),
                os.environ.get('GITHUB_TOKEN') or os.environ.get('GH_TOKEN') or '')
        else:
            result = compute_validated_package_evidence(
                args.repository,
                args.revision,
                args.package_identity,
            )
    except Exception as exc:
        result = {
            "schemaVersion": 1,
            "revision": args.revision,
            "packageIdentity": args.package_identity,
            "artifact": f"founder-diagnostics-packages-{args.package_identity}",
            "runId": None,
            "reusable": False,
            "reason": "planner_error_fail_closed",
            "plannerError": type(exc).__name__,
        }
    Path(args.output).write_text(json.dumps(result, indent=2, sort_keys=True) + "\n")
    print(json.dumps(result, indent=2, sort_keys=True))


def _partial_release_rollback_evidence(repository, revision, app, result, token):
    """Recover legacy exact bytes from a durable successful sibling publication.

    A failed parent has no approved-release receipt. Its retained state, canonical
    package verifier and durable operation success still bind the successful
    target. This path never treats upload acceptance or a step label as success.
    """
    for run in _release_history_runs(repository, token):
        if (run.get('path') != '.github/workflows/' + DIRECT_RELEASE_WORKFLOW or
                run.get('head_branch') != TRUSTED_PR_BASE or run.get('event') != 'workflow_dispatch' or
                run.get('status') != 'completed' or run.get('conclusion') not in {'failure', 'cancelled'} or
                (run.get('head_repository') or {}).get('full_name') != repository):
            continue
        run_id = run['id']
        inventory = _release_history_api(repository, f'actions/runs/{run_id}/artifacts?per_page=100', token)
        artifacts = inventory.get('artifacts')
        if not isinstance(artifacts, list) or inventory.get('total_count') != len(artifacts):
            raise EvidenceLookupUnavailable('Partial release rollback artifact inventory incomplete')
        states = [row for row in artifacts if re.fullmatch(
            rf'legend-release-step-state-{revision}-{run_id}-[1-9][0-9]*', row.get('name', ''))]
        for artifact in states:
            state = _release_history_json(repository, run_id, artifact, 'legend-release-step-state.json')
            attempt = state.get('runAttempt')
            if (type(attempt) is not int or attempt < 1 or
                    artifact['name'] != f'legend-release-step-state-{revision}-{run_id}-{attempt}' or
                    state.get('runId') != run_id or state.get('applicationReleaseSha') != revision):
                raise ValueError('Partial release rollback state identity mismatch')
            # Mixed-producer schema 3 requires its existing target-material receipt.
            if state.get('schemaVersion') != 2 or 'targetMaterials' in state:
                continue
            if app not in selected_release_target_keys(state.get('selectedTargets', [])):
                continue
            identity = state.get('packageIdentity')
            if not re.fullmatch('[a-f0-9]{64}', identity or ''):
                raise ValueError('Partial release rollback package identity missing')
            jobs = _release_history_api(repository,
                f'actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100', token)
            rows = jobs.get('jobs')
            if not isinstance(rows, list) or jobs.get('total_count') != len(rows):
                raise EvidenceLookupUnavailable('Partial release rollback job inventory incomplete')
            owners = [job for job in rows if job.get('name') == 'release']
            if len(owners) != 1 or owners[0].get('status') != 'completed':
                raise EvidenceLookupUnavailable('Partial release rollback publication owner unproven')
            if _release_attempt_package_revision(repository, run, attempt, owners[0], token, app) != revision:
                raise ValueError('Partial release rollback producer mismatch')
            for success in artifacts:
                match = re.fullmatch('legend-release-operation-success-([a-f0-9]{64})', success.get('name', ''))
                if not match:
                    continue
                record = _release_history_json(repository, run_id, success, 'operation.json')
                if record.get('target') != app or record.get('producingAttempt') != attempt:
                    continue
                if (record.get('schemaVersion') != 1 or record.get('phase') != 'success' or
                        record.get('operationId') != match.group(1) or record.get('producingRun') != run_id or
                        record.get('applicationRevision') != revision or
                        not re.fullmatch('[a-f0-9]{64}', record.get('packageDigest', ''))):
                    raise ValueError('Partial release rollback durable success mismatch')
                proven = release_operation_history(repository, match.group(1), revision, app,
                    run_id, attempt, token, phase='success')
                if proven != record:
                    raise ValueError('Partial release rollback success authority mismatch')
                package = compute_validated_package_evidence(repository, revision, identity, allow_equivalent=False)
                if (not package.get('reusable') or package.get('revision') != revision or
                        package.get('packageIdentity') != identity or
                        package.get('runId') != record.get('packageProducerRun')):
                    raise EvidenceLookupUnavailable('Exact partial release rollback package unavailable')
                return dict(result, reusable=True, runId=package['runId'], releaseRunId=run_id,
                    packageArtifact=package['artifact'], packageIdentity=identity,
                    packageDigest=record['packageDigest'],
                    reason='exact_package_from_durable_successful_release_target')
    return None


def compute_rollback_evidence(repository: str, revision: str, app: str):
    if app not in RELEASE_TARGETS:
        raise ValueError(f"Unknown release target: {app}")
    result = {
        "schemaVersion": 2,
        "revision": revision,
        "app": app,
        "packageName": RELEASE_TARGETS[app]["package"],
        "runId": None,
        "releaseRunId": None,
        "packageArtifact": None,
        "packageIdentity": None,
        "reusable": False,
    }
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        result["reason"] = "github_token_unavailable"
        return result
    release_name = RELEASE_TARGETS[app]["releaseName"]
    receipt_names = (
        f"legend-approved-release-{revision}-{release_name}",
        f"legend-approved-release-{revision}",
    )
    workflow_path = ".github/workflows/all-intentional-direct-release-20260918.yml"
    seen_runs = set()
    for receipt_name in receipt_names:
        for artifact in _artifact_rows(repository, receipt_name, token):
            run_id = int((artifact.get("workflow_run") or {}).get("id") or 0)
            if not run_id or run_id in seen_runs:
                continue
            seen_runs.add(run_id)
            run = api_get(repository, f"actions/runs/{run_id}", token)
            if not (
                run.get("path") == workflow_path
                and run.get("head_branch") == TRUSTED_PR_BASE
                and run.get("status") == "completed"
                and run.get("conclusion") == "success"
                and (run.get("head_repository") or {}).get("full_name") == repository
            ):
                continue
            if receipt_name.endswith('-' + release_name):
                receipt = _release_history_json(repository, run_id, artifact, 'target-release-receipt.json')
                if receipt.get('schemaVersion') == 3:
                    import importlib.util
                    spec=importlib.util.spec_from_file_location('rollback_package_owner',Path(__file__).with_name('release-package.py'))
                    package=importlib.util.module_from_spec(spec);spec.loader.exec_module(package)
                    material=package.validate_target_material(app,receipt.get('targetMaterial'))
                    if (material['producerRevision']!=revision or receipt.get('target')!=release_name or
                            receipt.get('transaction')!='committed-or-preserved' or receipt.get('liveProven') is not True or
                            receipt.get('authority')!='post-live-verification'):
                        raise ValueError('Historical target receipt does not bind live-proven bytes')
                    component=compatible_component_evidence(repository,revision,app,token,expected_source=material['source'])
                    source=component.get('sourceReceipt',{})
                    if component.get('state') == 'required':
                        aggregate_revision=receipt.get('applicationReleaseSha')
                        aggregate_identity=receipt.get('packageIdentity')
                        if (not re.fullmatch('[a-f0-9]{40}',aggregate_revision or '') or
                                not re.fullmatch('[a-f0-9]{64}',aggregate_identity or '')):
                            raise EvidenceLookupUnavailable('Historical rollback aggregate binding unavailable')
                        aggregate=compute_validated_package_evidence(repository,aggregate_revision,aggregate_identity,allow_equivalent=False)
                        if (not aggregate.get('reusable') or aggregate.get('targetMaterials',{}).get(app)!=material or
                                type(aggregate.get('artifactId')) is not int or aggregate['artifactId']<1):
                            raise EvidenceLookupUnavailable('Identical retained rollback aggregate unavailable')
                        return dict(result,reusable=True,runId=aggregate['runId'],releaseRunId=run_id,
                            packageArtifact=aggregate['artifact'],artifactId=aggregate['artifactId'],
                            packageDigest=material['packageDigest'],componentSource=True,
                            reason='identical_target_bytes_restored_from_authenticated_aggregate')
                    if (component.get('state')!='reused-success' or source.get('sha256')!=material['packageDigest'] or
                            source.get('executionIdentity')!=material['executionIdentity'] or source.get('contentIdentity')!=material['contentIdentity']):
                        raise EvidenceLookupUnavailable('Exact historical rollback component unavailable')
                    return dict(result,reusable=True,runId=source['runId'],releaseRunId=run_id,
                        packageArtifact=source['artifact']['name'],artifactId=source['artifact']['id'],
                        packageDigest=source['sha256'],componentSource=True,reason='exact_live_target_component_receipt')
                if receipt.get('schemaVersion') == 1:
                    # Original target receipts predate component provenance.
                    # Authenticate their exact format and original successful
                    # live verification; do not restamp them as schema 2/3.
                    if (type(receipt.get('schemaVersion')) is not int or
                            receipt != dict(schemaVersion=1, applicationReleaseSha=revision,
                                            target=release_name, transaction='committed-or-preserved')):
                        raise ValueError('Legacy rollback target receipt binding mismatch')
                    attempt = run.get('run_attempt')
                    if type(attempt) is not int or attempt < 1 or run.get('event') != 'workflow_dispatch':
                        raise ValueError('Legacy rollback release attempt unproven')
                    inventory = _release_history_api(repository,
                        f'actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100', token)
                    jobs = inventory.get('jobs')
                    if (not isinstance(jobs, list) or type(inventory.get('total_count')) is not int
                            or inventory['total_count'] != len(jobs)):
                        raise EvidenceLookupUnavailable('Legacy rollback job inventory incomplete')
                    owners = [job for job in jobs if job.get('name') == 'release']
                    if (len(owners) != 1 or owners[0].get('status') != 'completed'
                            or owners[0].get('conclusion') != 'success'):
                        raise ValueError('Legacy rollback successful release unproven')
                    owner = owners[0]
                    for name in ('Verify every deployed target and collect all failures',
                                 'Enforce complete direct deployment outcome'):
                        steps = [step for step in owner.get('steps', []) if step.get('name') == name]
                        if (len(steps) != 1 or steps[0].get('status') != 'completed'
                                or steps[0].get('conclusion') != 'success'):
                            raise ValueError('Legacy rollback live verification unproven')
                    if _release_attempt_package_revision(repository, run, attempt, owner, token, app) != revision:
                        raise ValueError('Legacy rollback original package producer mismatch')
                elif receipt.get('schemaVersion') != 2 or 'targetMaterial' in receipt:
                    raise ValueError('Historical target receipt schema unsupported')
            artifact_names = _run_artifact_names(repository, run_id, token)
            link_prefix = f"legend-approved-package-link-{revision}-"
            package_links = sorted(
                name for name in artifact_names
                if name.startswith(link_prefix)
            )
            for link_name in package_links:
                package_identity = link_name[len(link_prefix):]
                if not re.fullmatch(r"[0-9a-f]{64}", package_identity):
                    continue
                validated = compute_validated_package_evidence(
                    repository,
                    revision,
                    package_identity,
                )
                if not validated.get("reusable"):
                    continue
                result.update({
                    "runId": int(validated["runId"]),
                    "releaseRunId": run_id,
                    "packageArtifact": validated["artifact"],
                    "packageIdentity": package_identity,
                    "reusable": True,
                    "reason": (
                        "exact_target_release_receipt_with_validated_package_link"
                        if receipt_name.endswith("-" + release_name)
                        else "legacy_release_receipt_with_validated_package_link"
                    ),
                })
                return result

            # Backward compatibility for releases created before package-link
            # receipts existed. New releases do not duplicate validated package
            # bytes into the release run.
            names = sorted(
                name for name in artifact_names
                if name.startswith("founder-diagnostics-packages-")
            )
            if names:
                package_identity = names[-1].removeprefix("founder-diagnostics-packages-")
                result.update({
                    "runId": run_id,
                    "releaseRunId": run_id,
                    "packageArtifact": names[-1],
                    "packageIdentity": (
                        package_identity
                        if re.fullmatch(r"[0-9a-f]{64}", package_identity)
                        else None
                    ),
                    "reusable": True,
                    "reason": (
                        "exact_target_release_receipt"
                        if receipt_name.endswith("-" + release_name)
                        else "legacy_exact_successful_release_receipt"
                    ),
                })
                return result
    partial = _partial_release_rollback_evidence(repository, revision, app, result, token)
    if partial is not None:
        return partial
    result["reason"] = "no_exact_successful_release_package"
    return result


def cmd_rollback_evidence(args):
    try:
        result = compute_rollback_evidence(args.repository, args.revision, args.app)
    except Exception as exc:
        result = {
            "schemaVersion": 2,
            "revision": args.revision,
            "app": args.app,
            "packageName": RELEASE_TARGETS.get(args.app, {}).get("package"),
            "runId": None,
            "releaseRunId": None,
            "packageArtifact": None,
            "packageIdentity": None,
            "reusable": False,
            "reason": "planner_error_fail_closed",
            "plannerError": type(exc).__name__,
        }
    Path(args.output).write_text(json.dumps(result, indent=2, sort_keys=True) + "\n")
    print(json.dumps(result, indent=2, sort_keys=True))


def _step5_execution_contract(text):
    """Separate suite execution authority from artifact/planner orchestration."""
    jobs = _job_blocks(text)
    contract = {}
    contract["environment"] = _workflow_top_level_field(text, "env")
    contract["defaults"] = _workflow_top_level_field(text, "defaults")
    for name in ("candidate", "baseline"):
        if name not in jobs:
            raise ValueError("Missing Step 5 execution job")
        job = jobs[name]
        header = job.split("    steps:", 1)[0]
        # Only scheduling dependencies/conditions are outside execution identity.
        header = re.sub(r"(?m)^    (?:if|needs):[^\n]*\n(?:      [^\n]*\n)*", "", header)
        # Retain all executable steps, including unnamed/new steps. An unknown
        # source mutation or environment write must never disappear from proof.
        contract[name] = {"runtime": header, "steps": job.split("    steps:", 1)[1]}
    return contract


def _step5_child_contract(text, child):
    contract = _step5_execution_contract(text)
    return {key: contract[key] for key in ('environment', 'defaults', child)}


def _step5_child_unchanged(prior_sha, workflow_path, child):
    return _step5_child_contract(git_show_file(prior_sha, workflow_path), child) == _step5_child_contract(Path(workflow_path).read_text(), child)


def _step5_merge_fingerprint(source):
    import ast
    module = ast.parse(source)
    names = {'merge_step5_class_results', 'read_step5_results', 'cmd_step5_merge'}
    nodes = [ast.dump(node, include_attributes=False) for node in module.body
             if isinstance(node, ast.FunctionDef) and node.name in names]
    if len(nodes) != len(names):
        return None
    return hashlib.sha256('\n'.join(nodes).encode()).hexdigest()


def _step5_materialization_identity(workflow, source):
    """Bind every validate step that can overwrite candidate artifacts and its parser."""
    import ast
    names = {'merge_step5_class_results', 'read_step5_results', 'cmd_step5_merge'}
    nodes = {node.name: ast.dump(node, include_attributes=False)
             for node in ast.parse(source).body
             if isinstance(node, ast.FunctionDef) and node.name in names}
    contract = {'validate': _job_blocks(workflow).get('validate'), 'helpers': nodes}
    return hashlib.sha256(json.dumps(contract, sort_keys=True).encode()).hexdigest()


def _step5_materialization_compatible(prior_sha, workflow_path):
    prior = _step5_materialization_identity(
        git_show_file(prior_sha, workflow_path), git_show_file(prior_sha, 'scripts/validation-resume.py'))
    current = _step5_materialization_identity(
        Path(workflow_path).read_text(), Path('scripts/validation-resume.py').read_text())
    # Candidate artifacts may have been overwritten with effective repaired TRX.
    # Admit unchanged authority or only the exact reviewed directional upgrade;
    # never infer that an artifact is raw from its name or its parent conclusion.
    return prior == current or (prior, current) == (
        '95fca8be2c095ce5c7720dc6276c09384521930cb6e93d985f1366ec3f8ffcad', '6b3bbec67ebdfa63dc9d05b18ecdfc3e32855fd9946682ea6adb6b708b5a91de')


def _step5_candidate_producer_compatible(prior_sha, workflow_path):
    return (_step5_child_unchanged(prior_sha, workflow_path, 'candidate')
            and _step5_materialization_compatible(prior_sha, workflow_path))


def _step5_baseline_producer_compatible(prior_sha, workflow_path):
    if not _step5_materialization_compatible(prior_sha, workflow_path):
        return False
    prior = _step5_child_contract(git_show_file(prior_sha, workflow_path), 'baseline')
    current = _step5_child_contract(Path(workflow_path).read_text(), 'baseline')
    current_helper = _step5_merge_fingerprint(Path('scripts/validation-resume.py').read_text())
    if prior == current:
        return current_helper == _step5_merge_fingerprint(git_show_file(prior_sha, 'scripts/validation-resume.py'))
    # Directional admission of the reviewed full-suite producer into this exact
    # bounded replacement consumer. Identities remain distinct; unknown edits
    # (including environment, checkout, SDK, restore, build or test) fail closed.
    digest = lambda value: hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest()
    return current_helper == 'de5a902349eb7e3dbf8e2efd010489cba156f08d9424c626d13cb250224f5ed6' and (digest(prior), digest(current)) == (
        '93bb34927ed933497128baeffc1e3bd61f7d692deb621e406a1986a1b9c7dc93', 'ae069422498edc792983b05eea46e9714d4a73bdd2c69ff15370eb195f028a01')


def _step5_jobs_unchanged(prior_sha: str, workflow_path: str) -> bool:
    return (_step5_execution_contract(git_show_file(prior_sha, workflow_path)) == _step5_execution_contract(Path(workflow_path).read_text())
            and _step5_candidate_producer_compatible(prior_sha, workflow_path)
            and _step5_baseline_producer_compatible(prior_sha, workflow_path))


class ReleaseOperationHistoryUnproven(RuntimeError):
    """No first-write proof; exact-candidate read-only reconciliation is allowed."""


_RELEASE_HISTORY_SOURCES = {}
_RELEASE_HISTORY_VERIFIED_PACKAGES = {}
_RELEASE_HISTORY_API = {}
_RELEASE_HISTORY_TERMINAL_RUNS = set()
_RELEASE_HISTORY_RECEIPTS = {}
_RELEASE_HISTORY_LOG_BINDINGS = {}
_RELEASE_HISTORY_EXCLUSIONS = {}


def export_release_history_snapshot(candidate_revision):
    rows = _RELEASE_HISTORY_EXCLUSIONS.get(candidate_revision, {})
    # Bound only the optimization payload, never the history search/proof.
    entries = [dict(row, targets=sorted(row['targets'])) for _, row in sorted(rows.items())[-2000:]]
    body = {'schemaVersion': 1, 'candidateRevision': candidate_revision, 'entries': entries}
    body['digest'] = hashlib.sha256(json.dumps(body, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    return body


def import_release_history_snapshot(snapshot, candidate_revision):
    """Consume only the snapshot in an authenticated canonical transaction plan.

    It excludes immutable terminal history, never active or newly retried runs.
    release_operation_history still reads the fresh complete run inventory.
    """
    if not isinstance(snapshot, dict) or set(snapshot) != {'schemaVersion', 'candidateRevision', 'entries', 'digest'}:
        raise RuntimeError('Malformed transaction history snapshot')
    body = {key: value for key, value in snapshot.items() if key != 'digest'}
    digest = hashlib.sha256(json.dumps(body, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    if snapshot['schemaVersion'] != 1 or snapshot['candidateRevision'] != candidate_revision or snapshot['digest'] != digest:
        raise RuntimeError('Transaction history snapshot identity mismatch')
    if not isinstance(snapshot['entries'], list) or len(snapshot['entries']) > 2000:
        raise RuntimeError('Transaction history snapshot is unbounded')
    rows = {}
    for row in snapshot['entries']:
        if (not isinstance(row, dict) or set(row) != {'runId', 'runAttempt', 'headSha', 'targets'} or
                type(row['runId']) is not int or row['runId'] < 1 or row['runId'] in rows or
                type(row['runAttempt']) is not int or row['runAttempt'] < 1 or
                not re.fullmatch('[a-f0-9]{40}', row['headSha']) or
                not isinstance(row['targets'], list) or not row['targets'] or
                len(row['targets']) != len(set(row['targets'])) or any(key not in RELEASE_TARGETS for key in row['targets'])):
            raise RuntimeError('Invalid terminal history exclusion')
        rows[row['runId']] = dict(row, targets=set(row['targets']))
    existing = _RELEASE_HISTORY_EXCLUSIONS.setdefault(candidate_revision, {})
    for run_id, row in rows.items():
        prior = existing.get(run_id)
        if prior is None or prior['runAttempt'] < row['runAttempt']:
            existing[run_id] = row
        elif prior['runAttempt'] == row['runAttempt'] and prior['headSha'] == row['headSha']:
            prior['targets'].update(row['targets'])


def _remember_release_exclusion(candidate_revision, run, target):
    if run.get('status') != 'completed':
        return
    rows = _RELEASE_HISTORY_EXCLUSIONS.setdefault(candidate_revision, {})
    prior = rows.get(run['id'])
    if prior is None or prior['runAttempt'] != run.get('run_attempt', 1) or prior['headSha'] != run['head_sha']:
        prior = {'runId': run['id'], 'runAttempt': run.get('run_attempt', 1), 'headSha': run['head_sha'], 'targets': set()}
        rows[run['id']] = prior
    prior['targets'].add(target)


def _release_history_api(repository, path, token):
    """Share immutable terminal proof in one preparation process, not active state."""
    if _RECOVERY_PROOF_VIEW.get() is not None:
        return api_get(repository, path, token)
    key = (repository, path)
    if key in _RELEASE_HISTORY_API:
        return _RELEASE_HISTORY_API[key]
    payload = api_get(repository, path, token)
    runs = payload.get('workflow_runs') if isinstance(payload, dict) else None
    if isinstance(runs, list):
        _RELEASE_HISTORY_TERMINAL_RUNS.update((repository, row['id']) for row in runs if row.get('status') == 'completed')
    run_path = re.fullmatch(r'actions/runs/(\d+)', path)
    if run_path and payload.get('status') == 'completed':
        _RELEASE_HISTORY_TERMINAL_RUNS.add((repository, int(run_path.group(1))))
    artifact_path = re.fullmatch(r'actions/runs/(\d+)/artifacts\?per_page=100', path)
    jobs = payload.get('jobs') if isinstance(payload, dict) else None
    immutable = (
        path.startswith('compare/') or
        (run_path is not None and payload.get('status') == 'completed') or
        (isinstance(jobs, list) and all(row.get('status') == 'completed' for row in jobs)) or
        (artifact_path is not None and (repository, int(artifact_path.group(1))) in _RELEASE_HISTORY_TERMINAL_RUNS)
    )
    if immutable:
        _RELEASE_HISTORY_API[key] = payload
    return payload


def _release_history_source(repository, revision, path, token):
    """Read immutable public-safe source, never a runtime payload or log."""
    import base64
    key = (repository, revision, path)
    if key in _RELEASE_HISTORY_SOURCES:
        return _RELEASE_HISTORY_SOURCES[key]
    try:
        source = git_show_file(revision, path)
    except subprocess.SubprocessError:
        row = api_get(repository, f"contents/{path}?ref={revision}", token)
        if row.get("encoding") != "base64" or row.get("size", 0) > 300000:
            raise ReleaseOperationHistoryUnproven("Historical release source is unavailable")
        source = base64.b64decode(row["content"]).decode()
    _RELEASE_HISTORY_SOURCES[key] = source
    return source


def _recovery_receipt_json(repository, run_id, artifact, filename, max_bytes):
    """Reuse authenticated bytes only; every caller still checks fresh authority."""
    import tempfile
    digest = artifact.get('digest')
    if (artifact.get('expired') is not False or type(run_id) is not int or run_id < 1
        or type(artifact.get('id')) is not int or artifact['id'] < 1
        or artifact.get('workflow_run', {}).get('id') != run_id
        or not isinstance(artifact.get('name'), str) or not artifact['name']
        or not isinstance(digest, str) or not re.fullmatch('sha256:[a-f0-9]{64}', digest)
        or type(max_bytes) is not int or not 0 < max_bytes <= 32 * 1024 * 1024):
        raise ReleaseOperationHistoryUnproven('Recovery receipt artifact identity unproven')
    identity = (repository, run_id, artifact['id'], artifact['name'], digest, filename, max_bytes)
    lock, entries = _RECOVERY_RECEIPT_BYTES.get()
    with lock:
        future = entries.get(identity); owns = future is None
        if owns:
            if len(entries) >= 100:raise ValueError('RECOVERY_RECEIPT_CACHE_BOUND')
            future = concurrent.futures.Future(); entries[identity] = future
    if not owns:
        raw = future.result(timeout=evidence_remaining(180))
    else:
        try:
            with tempfile.TemporaryDirectory(prefix='legend-recovery-receipt-') as directory:
                _download_run_artifact(repository, run_id, artifact['name'], Path(directory),
                    artifact_id=artifact['id'], expected_digest=digest)
                path = Path(directory) / filename
                if not path.is_file() or path.is_symlink() or path.stat().st_size > max_bytes:
                    raise RuntimeError('Malformed or oversized recovery receipt')
                raw = path.read_bytes()
            if len(raw) > max_bytes:raise RuntimeError('Oversized recovery receipt')
            json.loads(raw)  # Invalid content is never retained as a successful read.
            future.set_result(raw)
        except BaseException as error:
            future.set_exception(error)
            with lock:
                if entries.get(identity) is future:del entries[identity]
            raise
    if len(raw) > max_bytes:raise RuntimeError('Oversized recovery receipt')
    return json.loads(raw)  # Independent value: consumers cannot mutate shared evidence.


def _release_history_json(repository, run_id, artifact, filename, *, verify_digest=False, max_bytes=131072):
    import tempfile
    if verify_digest and _RECOVERY_RECEIPT_BYTES.get() is not None:
        return _recovery_receipt_json(repository, run_id, artifact, filename, max_bytes)
    if artifact.get("expired"):
        raise ReleaseOperationHistoryUnproven("Historical publication receipt expired")
    digest = artifact.get('digest') if verify_digest else None
    if verify_digest and (not isinstance(digest, str) or not re.fullmatch('sha256:[a-f0-9]{64}', digest)):
        raise RuntimeError('Historical publication artifact digest unproven')
    key = (repository, run_id, artifact.get('id'), filename, digest, max_bytes)
    if artifact.get('id') and key in _RELEASE_HISTORY_RECEIPTS:
        return _RELEASE_HISTORY_RECEIPTS[key]
    with tempfile.TemporaryDirectory(prefix="legend-release-history-") as directory:
        _download_run_artifact(repository, run_id, artifact["name"], Path(directory), artifact_id=artifact.get('id'),
                               **({'expected_digest': digest} if verify_digest else {}))
        file = Path(directory) / filename
        if not file.is_file() or file.stat().st_size > max_bytes:
            raise RuntimeError("Malformed historical publication receipt")
        receipt = json.loads(file.read_text())
        if artifact.get('id'):
            _RELEASE_HISTORY_RECEIPTS[key] = receipt
        return receipt


def _historical_publication_names(target):
    # Read-only recognition of retired workflow formats; current writers still
    # have one canonical owner and never execute these historical actions.
    labels = {'portal': 'AgentPortal', 'client': 'ClientApp', 'protect': 'Protect',
              'parfait': 'Parfait', 'website': 'Website'}
    label = labels[target]
    return {'Publish selected head as one transaction', f'Publish canonical target ({target})',
            f'Direct deploy {label}', f'Direct deploy {label} immutable ZIP'}


def _legacy_inline_package_revision(workflow, release_job, target, checkout):
    """Recognize the retired inline build -> exact local package -> upload path."""
    release = _job_blocks(workflow).get('release', '')
    blocks = named_step_blocks(release)
    steps = {row['name']: row for row in release_job.get('steps', [])}
    publications = [name for name in _historical_publication_names(target)
                    if name.startswith('Direct deploy ') and name in blocks and name in steps]
    if len(publications) != 1:
        return None
    if steps.get('Load exact preserved deployable package', {}).get('conclusion') == 'success':
        return None  # Reused bytes require their independent producer proof.
    package_name = 'Publish exact selected application packages'
    if steps.get(package_name, {}).get('conclusion') != 'success':
        return None
    package = blocks.get(package_name, '')
    publication = blocks[publications[0]]
    if (release.index(package) >= release.index(publication) or
            re.search(r'git (?:checkout|reset|switch)\b', release) or
            re.search(r'(?m)^\s+RELEASE_SHA:', release)):
        return None
    if target == 'website':
        output = 'Legend-Website/dist'
        if ('"$RELEASE_SHA" > Legend-Website/dist/_deployment-provenance.txt' not in package or
                steps.get('Verify selected website catalog and build', {}).get('conclusion') != 'success'):
            return None
        filename = 'website.zip'
    else:
        project = re.escape(RELEASE_TARGETS[target]['project'])
        match = re.search(r'dotnet publish ' + project +
            r' -c Release --no-build --no-restore -o (/tmp/[a-z]+-publish) -p:SourceRevisionId="\$RELEASE_SHA"', package)
        build = blocks.get('Build exact selected release candidate', '')
        if (not match or steps.get('Build exact selected release candidate', {}).get('conclusion') != 'success' or
                '-p:SourceRevisionId="$RELEASE_SHA"' not in build):
            return None
        output = match.group(1)
        alias = output.removeprefix('/tmp/').removesuffix('-publish')
        if 'apps+=(' + alias + ')' not in package:
            return None
        filename = alias + '.zip'
    if (output not in publication and '/tmp/diagnostics-packages/' + filename not in publication and
            'python3 scripts/deploy-approved-app.py --target ' + target not in publication):
        return None
    if ('zip -qr' not in package or 'sha256sum /tmp/diagnostics-packages/*.zip' not in package):
        return None
    return checkout


class _ReleaseLogRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, url):
        if urllib.parse.urlparse(url).scheme != 'https':
            raise ReleaseOperationHistoryUnproven('Historical log redirect must use HTTPS')
        redirected = super().redirect_request(request, response, code, message, headers, url)
        if redirected is not None:
            redirected.remove_header('Authorization')
        return redirected


def _release_job_log(repository, job_id, token):
    state = _RECOVERY_LOG_BYTES.get()
    if state is None or _RECOVERY_PROOF_VIEW.get() is None:
        return _release_job_log_uncached(repository, job_id, token)
    job = api_get(repository, f'actions/jobs/{job_id}', token)
    run_id = job.get('run_id')
    if type(run_id) is not int or run_id < 1:
        raise ReleaseOperationHistoryUnproven('RECOVERY_LOG_OWNER_UNPROVEN')
    run = api_get(repository, f'actions/runs/{run_id}', token)
    attempt = job.get('run_attempt')
    if (job.get('id') != job_id or job.get('status') != 'completed'
        or run.get('id') != run_id or run.get('status') != 'completed'
        or type(attempt) is not int or type(run.get('run_attempt')) is not int
        or not 1 <= attempt <= run['run_attempt']
        or not re.fullmatch('[a-f0-9]{40}', job.get('head_sha', ''))
        or job['head_sha'] != run.get('head_sha')):
        raise ReleaseOperationHistoryUnproven('RECOVERY_LOG_TERMINAL_IDENTITY_UNPROVEN')
    key = (repository, run_id, attempt, job_id, job['head_sha'],
           job.get('started_at'), job.get('completed_at'), job.get('conclusion'))
    with state['lock']:
        future = state['entries'].get(key)
        owns = future is None
        if owns:
            if len(state['entries']) >= 100:
                raise ReleaseOperationHistoryUnproven('RECOVERY_LOG_STORAGE_BOUND')
            future = concurrent.futures.Future()
            state['entries'][key] = future
    if not owns:
        return future.result(timeout=evidence_remaining(30))
    try:
        body = _release_job_log_uncached(repository, job_id, token)
        size = len(body.encode())
        with state['lock']:
            if state['bytes'] + size > 64 * 1024 * 1024:
                raise ReleaseOperationHistoryUnproven('RECOVERY_LOG_STORAGE_BOUND')
            state['bytes'] += size
        future.set_result(body)
        return body
    except BaseException as error:
        future.set_exception(error)
        with state['lock']:
            if state['entries'].get(key) is future:
                del state['entries'][key]
        raise


def _release_job_log_uncached(repository, job_id, token):
    """Read the authenticated log redirect with bounded, credential-safe transport."""
    request = urllib.request.Request(
        f'https://api.github.com/repos/{repository}/actions/jobs/{job_id}/logs',
        headers={'Accept': 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28',
                 'User-Agent': 'legend-validation-resume/1.0'})
    request.add_unredirected_header('Authorization', f'Bearer {token}')
    opener = urllib.request.build_opener(_ReleaseLogRedirect())
    limit = 32 * 1024 * 1024
    for attempt in range(3):
        try:
            with opener.open(request, timeout=evidence_remaining(30)) as response:
                body = response.read(limit + 1)
            if len(body) > limit:
                raise ReleaseOperationHistoryUnproven('Historical checkout log exceeds evidence limit')
            return body.decode('utf-8')
        except (OSError, urllib.error.URLError, UnicodeError) as exc:
            status = getattr(exc, 'code', None)
            retryable = not isinstance(exc, (urllib.error.HTTPError, UnicodeError)) or status in {408, 429, 500, 502, 503, 504}
            if not retryable or attempt == 2:
                reason = f'HTTP {status}' if isinstance(status, int) else type(exc).__name__
                raise ReleaseOperationHistoryUnproven(
                    f'Historical checkout log unavailable (job {job_id}; {reason})') from None
            time.sleep(evidence_remaining(2 ** attempt))


def _release_checkout_from_job_log(repository, release_job, application, token):
    """Recover only revision metadata from authenticated retained Actions logs.

    Raw logs stay in memory and are never returned, printed, or persisted. This
    is checkout evidence only; source and immutable-package proof still follow.
    """
    job_id = release_job.get('id')
    if (type(job_id) is not int or job_id < 1 or
            not any(step.get('name') == 'Run actions/checkout@v4' and
                    step.get('conclusion') == 'success' for step in release_job.get('steps', []))):
        raise ReleaseOperationHistoryUnproven('Historical successful checkout job proof unavailable')
    cache_key = (repository, job_id, application)
    if release_job.get('status') == 'completed' and cache_key in _RELEASE_HISTORY_LOG_BINDINGS:
        return _RELEASE_HISTORY_LOG_BINDINGS[cache_key]
    raw = _release_job_log(repository, job_id, token)
    lines = [re.sub(r'^\d{4}-\d{2}-\d{2}T[0-9:.]+Z ', '', line)
             for line in raw.splitlines()]
    text = '\n'.join(lines)
    heads = set(re.findall(r'(?m)^\[command\]/usr/bin/git log -1 --format=%H\n([a-f0-9]{40})$', text))
    authorities = set(re.findall(r'(?m)^  RELEASE_SHA: ([a-f0-9]{40})$', text))
    packages = set(re.findall(r'(?m)^  APPLICATION_RELEASE_SHA: ([a-f0-9]{40})$', text))
    if len(heads) != 1 or heads != authorities or packages != {application}:
        raise ReleaseOperationHistoryUnproven('Historical checkout log revision binding is missing or contradictory')
    checkout = heads.pop()
    if release_job.get('status') == 'completed':
        _RELEASE_HISTORY_LOG_BINDINGS[cache_key] = checkout
    return checkout


def _historical_publication_failed_before_first_write(repository, release_job, application_revision, target, checkout, token):
    """Prove one historical canonical target attempt failed before any upload intent/write."""
    import ast

    job_id = release_job.get('id')
    if (type(job_id) is not int or job_id < 1 or release_job.get('status') != 'completed'):
        return False

    # This negative proof is valid only for the exact write-ahead uploader
    # contract: deployment reconciliation checks provider state first, then
    # durably publishes/read-backs intent, and only then calls Azure submit.
    historical_deploy = _release_history_source(
        repository, checkout, 'scripts/deploy-approved-app.py', token)
    historical_evidence = _release_history_source(
        repository, checkout, 'scripts/release-operation-evidence.py', token)
    current_deploy = Path(__file__).with_name('deploy-approved-app.py').read_text()
    current_evidence = Path(__file__).with_name('release-operation-evidence.py').read_text()

    def function_dump(source, name):
        tree = ast.parse(source)
        node = next((row for row in tree.body
                     if isinstance(row, (ast.FunctionDef, ast.AsyncFunctionDef)) and row.name == name), None)
        return ast.dump(node) if node is not None else None

    def method_dump(source, class_name, method_name):
        tree = ast.parse(source)
        owner = next((row for row in tree.body if isinstance(row, ast.ClassDef) and row.name == class_name), None)
        node = next((row for row in owner.body
                     if isinstance(row, (ast.FunctionDef, ast.AsyncFunctionDef)) and row.name == method_name), None) if owner else None
        return ast.dump(node) if node is not None else None

    historical_reconcile = next((node for node in ast.parse(historical_deploy).body
                                 if isinstance(node, ast.FunctionDef) and node.name == 'reconcile'), None)
    # Retain the audited pre-upload negative proof from the prior write-ahead
    # generation. Its later SHA-only success semantics are never executed here.
    # Exact source hashing is independent of the reader's Python AST version.
    prior_reconcile = (historical_reconcile is not None and hashlib.sha256(
        ast.get_source_segment(historical_deploy, historical_reconcile).encode()).hexdigest() in {
        'ab0084204d7623f5968fe18fb7b9f844a346e4c7d20a028c9dc350e7264d3502',
        # Approved pre-diagnostic subtype generation; same pre-submit boundary.
        '00dd0d2f2145617c3aa22dc5f44e0d4eef948303c8f691d7a887fb42e0eb2473',
        # Reviewed pre-correlation generation: only the failed-intent exception
        # subtype changed; provider status still precedes intent and upload.
        'f86ffe74930ffdb5c7b6a9797569be4c653a75e861d3ffd05ef8b7900de26260'})
    historical_journal = next((node for node in ast.parse(historical_evidence).body
                               if isinstance(node, ast.ClassDef) and node.name == 'OperationJournal'), None)
    historical_intent = next((node for node in historical_journal.body
                              if isinstance(node, ast.FunctionDef) and node.name == 'before_submit'), None) if historical_journal else None
    # Schema-2 adds provenance fields to the same intent-before-write protocol.
    # Retain the exact approved schema-1 and pre-correlation schema-2 methods
    # for negative execution evidence;
    # this never reconstructs a successful operation or permits ambiguous replay.
    prior_intent = historical_intent is not None and hashlib.sha256(
        ast.get_source_segment(historical_evidence, historical_intent).encode()).hexdigest() in {
        '9fd5781cd54c10c9a5fb74af4bb005f172480583c9da0396387309888e953e26',
        '3fa03ccdb9d20f8df43434e0059888415dd6d11b4d11d3d55872bd6b032c6691',
        # Accepted pre-supersession intent: unchanged intent-before-upload boundary.
        'ba734241c9ae81a7184bd8c19980b9443ade2ae1d4bd8115d98153fbd0c68be2',
        # Reviewed pre-builder extraction; exact unchanged write-ahead method.
        '9614ad2b268bc2d55440c684340432c8ba0c95de7a11c7e8b153fee12741cebf'}
    if ((function_dump(historical_deploy, 'reconcile') != function_dump(current_deploy, 'reconcile')
         and not prior_reconcile) or
            ((method_dump(historical_evidence, 'OperationJournal', 'before_submit') !=
              method_dump(current_evidence, 'OperationJournal', 'before_submit') or
              method_dump(historical_evidence, 'OperationJournal', 'intent_record') !=
              method_dump(current_evidence, 'OperationJournal', 'intent_record')) and not prior_intent)):
        return False

    # Authenticated retained logs are only execution evidence after source
    # contract identity is proven. Require the exact target/revision start,
    # the fail-closed pre-upload exception, and no submit marker in that target
    # execution segment.
    raw = _release_job_log(repository, job_id, token)
    lines = [re.sub(r'^\d{4}-\d{2}-\d{2}T[0-9:.]+Z ', '', line)
             for line in raw.splitlines()]
    text = '\n'.join(lines)
    release_name = RELEASE_TARGETS[target]['releaseName']
    start_marker = f'{release_name}: approved revision {application_revision}, ZIP sha256 '
    starts = [match.start() for match in re.finditer(re.escape(start_marker), text)]
    if len(starts) != 1:
        return False
    start = starts[0]
    end_marker = '\n##[error]Process completed with exit code 1.'
    end = text.find(end_marker, start)
    if end < 0:
        return False
    segment = text[start:end + len(end_marker)]
    expected = (
        'DeploymentStatusUnavailable: Azure deployment status remained unavailable for 3 consecutive reads '
        '(before any upload). No deployment or rollback write was replayed; resume by reconciling the exact revision.'
    )
    return expected in segment and 'Submitting the verified immutable ZIP once.' not in segment


def _historical_parallel_verifier_compatible(functions, current_functions, source=None):
    """Recognize bounded reviewed verifier generations, never historical success."""
    import ast
    names = ('publish_prepared_target', 'publish_prepared_targets_parallel')
    if any(name not in functions or name not in current_functions for name in names):
        return False
    if all(ast.dump(functions[name]) == ast.dump(current_functions[name]) for name in names):
        return True
    # Preserve the jointly reviewed component-aware verifier/worker generation.
    # This recognizes its contract, never its outcome; source, job, artifact and
    # attempt authentication below remain mandatory. Source hashes are stable
    # across Python versions and cannot accept a mix of independently approved parts.
    if source is not None:
        pair = tuple(hashlib.sha256(ast.get_source_segment(source, functions[name]).encode()).hexdigest()
                     for name in names)
        if pair == ('98b0a32ed17d70631bcb153ba6f01e6c7ac80c23ce1821ee2ffc466fe6b8d527',
                    '4e0342f97a7b793a8c1f53b88dab568004b835f0bbf720607436ad402d270cc9'):
            return True
    fingerprints = tuple(hashlib.sha256(ast.dump(functions[name]).encode()).hexdigest() for name in names)
    # Canonical Python 3.12 AST identities: the approved single-producer verifier
    # and its two reviewed first-pass reporting generations. Neither certifies
    # current live success or permits a mutation without durable intent.
    return fingerprints in {
        ('07f76c62c85f378b0db1627dac88be26a0438bb24e4181f455a2ab1b69b506b6',
         'cb3f333f35a82e47bb8e6e2bf178399022451de69550923348d77252a1c35392'),
        ('07f76c62c85f378b0db1627dac88be26a0438bb24e4181f455a2ab1b69b506b6',
         '9c63956c377037597ee1c5480d8066090fce4ded791f7138dc3c05932129b22a'),
    }


def _release_attempt_package_revision(repository, run, attempt, release_job, token, target, *, aggregate=False):
    """Bind legacy publication to the package's verified embedded revision.

    Workflow event SHA is never substituted for checkout/package authority.
    Existing state and package receipts remain the only evidence channel.
    """
    import ast
    run_id = run['id']
    cache_key = (repository, run_id, attempt, run['head_sha'], target, aggregate, json.dumps(release_job, sort_keys=True))
    if cache_key in _RELEASE_HISTORY_VERIFIED_PACKAGES:
        return _RELEASE_HISTORY_VERIFIED_PACKAGES[cache_key]
    inventory = _release_history_api(repository, f"actions/runs/{run_id}/artifacts?per_page=100", token)
    artifacts = inventory.get('artifacts')
    if not isinstance(artifacts, list) or inventory.get('total_count', 0) > len(artifacts):
        raise ReleaseOperationHistoryUnproven('Incomplete historical receipt inventory')
    states = [row for row in artifacts if re.fullmatch(
        rf"legend-release-step-state-[a-f0-9]{{40}}-{run_id}-{attempt}", row.get('name', ''))]
    if len(states) > 1:
        raise ReleaseOperationHistoryUnproven('Historical state receipt names are ambiguous')
    receipt = None
    if len(states) == 1:
        state = _release_history_json(repository, run_id, states[0], 'legend-release-step-state.json')
        if state.get('schemaVersion') not in (2, 3) or state.get('runId') != run_id or state.get('runAttempt') != attempt:
            raise RuntimeError('Historical release state producer identity mismatch')
        # This state file's releaseHeadSha is the workflow event SHA, not checkout.
        # Corroborate its step proof against independently retained Actions data.
        actual = [(row.get('name'), row.get('status'), row.get('conclusion')) for row in release_job.get('steps', [])]
        claimed = [(row.get('name'), row.get('status'), row.get('conclusion')) for row in state.get('steps', [])]
        if actual != claimed or state.get('releaseJobConclusion') != release_job.get('conclusion'):
            raise RuntimeError('Historical release state contradicts Actions execution proof')
        receipt = state
    elif attempt == 1 and run.get('run_attempt', 1) == 1:
        approved = [row for row in artifacts if re.fullmatch(r'legend-approved-release-[a-f0-9]{40}', row.get('name', ''))]
        if len(approved) == 1 and release_job.get('conclusion') == 'success':
            receipt = _release_history_json(repository, run_id, approved[0], 'approved-release-receipt.json')
            if receipt.get('schemaVersion') not in (2, 3):
                # Older receipts are not interpreted as current proof. The
                # independently verified RELEASE_SHA package contract below
                # may still prove which bytes that legacy run published.
                receipt = None
            elif receipt.get('transaction') not in {'committed', 'preserved'}:
                raise RuntimeError('Historical approved receipt contract mismatch')
    if receipt is not None and not re.fullmatch('[a-f0-9]{40}', receipt.get('applicationReleaseSha', '')):
        raise RuntimeError('Historical receipt package revision is malformed')
    application = receipt['applicationReleaseSha'] if receipt else None
    target_application = application
    if receipt is not None and receipt.get('schemaVersion') == 3:
        materials = receipt.get('targetMaterials')
        keys = set(selected_release_target_keys(receipt.get('selectedTargets', [])))
        if not isinstance(materials, dict) or set(materials) != keys or (not aggregate and target not in materials):
            raise RuntimeError('Historical target material scope is incomplete')
        release_transaction_identity(application, {key: row.get('packageDigest') for key,row in materials.items()}, materials)
        target_application = application if aggregate else materials[target]['producerRevision']
    elif receipt is not None and 'targetMaterials' in receipt:
        raise RuntimeError('Legacy receipt has unversioned target material')
    translations = {row['name'].removeprefix('translation-direct-release-') for row in artifacts
                    if not row.get('expired') and re.fullmatch('translation-direct-release-[a-f0-9]{40}', row.get('name', ''))}
    rollbacks = set()
    for artifact in artifacts:
        match = re.fullmatch(r'diagnostics-rollback-([a-z]+)-([a-f0-9]{40})', artifact.get('name', ''))
        if match and not artifact.get('expired') and match.group(1) in RELEASE_TARGETS:
            rollbacks.add(match.group(2))
    checkouts = translations | rollbacks
    from_log = not checkouts and receipt is not None
    if from_log:
        checkout = _release_checkout_from_job_log(repository, release_job, application, token)
    elif len(checkouts) == 1:
        checkout = checkouts.pop()
    else:
        raise ReleaseOperationHistoryUnproven('No authenticated legacy package checkout/producer revision (missing or ambiguous)')
    workflow = _release_history_source(repository, run['head_sha'], '.github/workflows/' + DIRECT_RELEASE_WORKFLOW, token)
    # Both existing artifact families bind the execution authority, not the
    # rollback package's embedded revision. Package proof below remains required.
    rollback_job = _job_blocks(workflow).get('preserve-rollback', '')
    release = _job_blocks(workflow).get('release', '')
    checkout_step = re.search(r'(?m)^      - uses: actions/checkout@v4\n((?:        [^\n]*\n|\n)*)', release)
    event_checkout = (
        'RELEASE_SHA: ${{ github.sha }}' in workflow and checkout == run['head_sha'] and
        checkout_step is not None and (not re.search(r'(?m)^\s+ref:', checkout_step.group(1)) or
            re.findall(r'(?m)^\s+ref: (.*)$', checkout_step.group(1)) == ['${{ github.sha }}']) and
        not re.search(r'(?m)^\s+RELEASE_SHA:', release))
    producer_binding = (
        (event_checkout and bool(translations) and
         'name: translation-direct-release-${{ github.sha }}' in release) or
        (from_log and 'uses: actions/checkout@v4' in release and
         'ref: ${{ env.RELEASE_SHA }}' in release) or
        (bool(translations) and 'name: translation-direct-release-${{ env.RELEASE_SHA }}' in workflow) or
        (bool(rollbacks) and 'name: diagnostics-rollback-${{ matrix.app }}-${{ env.RELEASE_SHA }}' in
         rollback_job and not re.search(r'(?m)^\s+RELEASE_SHA:', rollback_job)))
    if (('RELEASE_SHA: ${{ inputs.merge_sha || github.sha }}' not in workflow and not event_checkout) or
            not producer_binding or re.search(r'(?<![A-Z_])RELEASE_SHA\s*=', workflow)):
        raise ReleaseOperationHistoryUnproven('Historical checkout receipt has no recognized producer binding')
    release = _job_blocks(workflow).get('release', '')
    inline_revision = _legacy_inline_package_revision(workflow, release_job, target, checkout)
    if inline_revision is not None:
        if receipt is not None and application != inline_revision:
            raise RuntimeError('Historical inline package proof contradicts retained receipt')
        if 'python3 scripts/deploy-approved-app.py --target ' + target in release:
            deployment_source = _release_history_source(repository, checkout, 'scripts/deploy-approved-app.py', token)
            tree = ast.parse(deployment_source)
            functions = {node.name: node for node in tree.body if isinstance(node, ast.FunctionDef)}
            current = ast.parse(Path(__file__).with_name('deploy-approved-app.py').read_text())
            verifier = next(node for node in current.body if isinstance(node, ast.FunctionDef) and node.name == 'verify_package')
            legacy_main = ast.parse('''def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--target', choices=TARGETS, required=True)
    args = parser.parse_args()
    app, filename, url = TARGETS[args.target]
    revision = os.environ['RELEASE_SHA']
    package = Path('/tmp/diagnostics-packages') / filename
    digest = verify_package(package, revision, args.target == 'website')
    print(f'{app}: approved revision {revision}, ZIP sha256 {digest}', flush=True)
    result = reconcile(Azure(app, package, url, revision, args.target == 'website'))
    print(f'{app}: {result}; exact revision healthy and no Azure deployment pending.', flush=True)
''').body[0]
            if (not all(name in functions for name in ('main', 'verify_package')) or
                    ast.dump(functions['main']) != ast.dump(legacy_main) or
                    ast.dump(functions['verify_package']) != ast.dump(verifier)):
                raise ReleaseOperationHistoryUnproven('Historical inline uploader contract is incompatible')
        if release_job.get('status') == 'completed':
            _RELEASE_HISTORY_VERIFIED_PACKAGES[cache_key] = inline_revision
        return inline_revision
    if 'APPLICATION_RELEASE_SHA' in release:
        if ('APPLICATION_RELEASE_SHA: ${{ needs.discover-live.outputs.application_release_sha }}' not in release or
                release.count('APPLICATION_RELEASE_SHA:') != 1 or re.search(r'APPLICATION_RELEASE_SHA\s*=', release)):
            raise ReleaseOperationHistoryUnproven('Historical package revision binding is unknown')
        if application is None:
            raise ReleaseOperationHistoryUnproven('No authenticated legacy package producer revision')
        revision_variable = 'APPLICATION_RELEASE_SHA'
    else:
        # Prior to content-equivalent package reuse, the package's embedded SHA
        # was exactly the checked-out RELEASE_SHA. The source + successful verify
        # proof is required; a translation artifact name alone is never enough.
        revision_variable = 'RELEASE_SHA'
        if re.search(r'(?m)^\s+RELEASE_SHA:', release):
            raise ReleaseOperationHistoryUnproven('Historical release overrides its verified checkout revision')
        if application is not None and application != checkout:
            raise RuntimeError('Legacy package receipt contradicts verified checkout revision')
        application = checkout
    lines, spans = _named_step_spans(release)
    steps = {row['name']: row for row in release_job.get('steps', [])}
    target_evidence_names = set(_historical_publication_names(target))
    target_evidence_names.add(f'Confirm first-pass durable publication receipt ({target})')
    publication = next(((name, start, end) for name, start, end in spans
                        if name in target_evidence_names), None)
    if publication is None:
        raise ReleaseOperationHistoryUnproven('Historical publication source is unknown')
    target_body = ''.join(lines[publication[1]:publication[2]])
    publish_body = target_body
    publication_start = publication[1]
    parallel_mode = False
    if 'python3 scripts/deploy-approved-app.py' not in publish_body:
        parallel_names = {
            'Publish canonical selected targets in parallel',
            'Submit canonical selected targets in parallel',
        }
        parallel = next(((name, start, end) for name, start, end in spans
                         if name in parallel_names), None)
        release_name = RELEASE_TARGETS[target]['releaseName']
        if (
            parallel is None or parallel[1] >= publication[1] or
            f'/tmp/release-target-results/{target}.json' not in target_body or
            f"contains(fromJSON(env.SELECTED_TARGETS), '{release_name}')" not in target_body
        ):
            raise ReleaseOperationHistoryUnproven('Historical publication does not use canonical immutable verifier')
        parallel_body = ''.join(lines[parallel[1]:parallel[2]])
        if (
            'python3 scripts/deploy-approved-app.py' not in parallel_body or
            '--publish-prepared-parallel' not in parallel_body or
            '--targets-json "$SELECTED_TARGETS"' not in parallel_body or
            '--transaction-plan /tmp/release-transaction.json' not in parallel_body
        ):
            raise ReleaseOperationHistoryUnproven('Historical parallel publication contract is incompatible')
        publish_body = parallel_body
        publication_start = parallel[1]
        parallel_mode = True
    verified = any(start < publication_start and steps.get(name, {}).get('conclusion') == 'success' and
                   'scripts/release-package.py verify' in ''.join(lines[start:end]) and
                   f'--revision "${revision_variable}"' in ''.join(lines[start:end])
                   for name, start, end in spans)
    if not verified:
        raise ReleaseOperationHistoryUnproven('Legacy package did not pass embedded revision verification')
    deployment_source = _release_history_source(repository, checkout, 'scripts/deploy-approved-app.py', token)
    tree = ast.parse(deployment_source)
    functions = {node.name: node for node in tree.body if isinstance(node, ast.FunctionDef)}
    current = ast.parse(Path(__file__).with_name('deploy-approved-app.py').read_text())
    current_functions = {node.name: node for node in current.body if isinstance(node, ast.FunctionDef)}
    current_verify = current_functions['verify_package']
    if parallel_mode and not _historical_parallel_verifier_compatible(functions, current_functions, deployment_source):
        raise ReleaseOperationHistoryUnproven('Historical parallel publication verifier contract is incompatible')
    if receipt is not None and receipt.get('schemaVersion') == 3:
        for name in ('prepare_transaction', 'read_transaction_plan', 'transaction_target_material', 'publish_prepared_target'):
            if name not in functions or ast.dump(functions[name]) != ast.dump(current_functions[name]):
                raise ReleaseOperationHistoryUnproven('Historical mixed-producer verifier contract is incompatible')
    expected = [ast.parse("revision = os.environ.get('APPLICATION_RELEASE_SHA') or os.environ.get('RELEASE_SHA')").body[0]]
    if revision_variable == 'RELEASE_SHA':
        expected.extend(ast.parse(text).body[0] for text in ("revision = os.environ['RELEASE_SHA']", "revision = os.environ.get('RELEASE_SHA')"))
    main = functions.get('main')
    if ('verify_package' not in functions or ast.dump(functions['verify_package']) != ast.dump(current_verify) or
            main is None or not any(ast.dump(node) == ast.dump(binding) for node in main.body for binding in expected)):
        raise ReleaseOperationHistoryUnproven('Historical deployment verifier contract is incompatible')
    one = functions.get('deploy_one')
    if one is None or not any(isinstance(node, ast.Assign) and isinstance(node.value, ast.Call) and
                             isinstance(node.value.func, ast.Name) and node.value.func.id == 'verify_package'
                             for node in one.body):
        raise ReleaseOperationHistoryUnproven('Historical publication does not reverify immutable bytes')
    resolved = target_application if receipt is not None and receipt.get('schemaVersion') == 3 and not aggregate else application
    if release_job.get('status') == 'completed':
        _RELEASE_HISTORY_VERIFIED_PACKAGES[cache_key] = resolved
    return resolved


def _release_history_runs(repository, token):
    """Fresh complete unfiltered inventory; active/page movement is never cached."""
    workflow = urllib.parse.quote(DIRECT_RELEASE_WORKFLOW, safe="")
    page, seen, total = 1, set(), None
    while True:
        payload = _release_history_api(repository, f"actions/workflows/{workflow}/runs?per_page=100&page={page}", token)
        rows, count = payload.get('workflow_runs'), payload.get('total_count')
        if not isinstance(rows, list) or type(count) is not int or count < 0:
            raise ReleaseOperationHistoryUnproven('Missing release execution history')
        if total is None:
            total = count
        elif total != count:
            raise ReleaseOperationHistoryUnproven('Release inventory changed during pagination; retry read-only discovery')
        for row in rows:
            if type(row.get('id')) is not int or row['id'] in seen:
                raise ReleaseOperationHistoryUnproven('Release inventory pagination repeated a run')
            seen.add(row['id'])
            yield row
        if len(rows) < 100:
            if len(seen) != total:
                raise ReleaseOperationHistoryUnproven('Release execution history is truncated')
            return
        page += 1


def _release_nonentry_observers(jobs, source):
    """Authenticate observers with no capability to publish within this attempt."""
    names = {'release-state-receipt', 'target-release-receipts (${{ matrix.app }})',
             'wake-release-lifecycle-after-terminal-release'}
    present = [job for job in jobs if job.get('name') in names]
    if not present:
        return jobs
    if (not isinstance(source, str) or {job['name'] for job in present} != names
        or len(present) != len(names)):
        return None
    blocks = _job_blocks(source)
    legacy_jobs = {'admission', 'discover-live', 'preserve-rollback', 'release',
                   'release-state-receipt', 'target-release-receipts',
                   'wake-release-lifecycle-after-terminal-release'}
    if (set(blocks) not in (legacy_jobs, legacy_jobs | {'migration-readiness'})
        or any(job.get('status') != 'completed' for job in jobs)):
        return None
    contract = {name: blocks.get(name) for name in (
        'release-state-receipt', 'target-release-receipts',
        'wake-release-lifecycle-after-terminal-release')}
    contract.update(environment=_workflow_top_level_field(source, 'env'),
                    defaults=_workflow_top_level_field(source, 'defaults'))
    # Exact a07afe8 observer capability contract: inline state capture/upload,
    # plus a scheduler wake with contents:read/actions:write only. The wake is
    # not read-only, but has no Azure authentication, secrets or OIDC permission
    # to publish here. Its protected checkout can dispatch another workflow;
    # that separate run remains in complete release history and admission.
    # Unknown permissions, commands or ambient env fail closed. A job name
    # alone never grants this non-publication classification.
    # Both reviewed generations retain receipt-only observer capabilities.
    contracts = {
        'ce5e7dfe3d4100116bae366833687d6055dd51b91357cc268c73d33105b999c7': legacy_jobs,
        'f49cf05a452c901b015e69a58697b108b24694cee0763448d3a3a3672e4d04c2': legacy_jobs | {'migration-readiness'},
    }
    if contracts.get(hashlib.sha256(json.dumps(contract, sort_keys=True).encode()).hexdigest()) != set(blocks):
        return None
    for job in present:
        if job.get('status') != 'completed' or job.get('conclusion') not in {'success', 'failure', 'cancelled', 'skipped'}:
            return None
        steps = job.get('steps')
        if not isinstance(steps, list):
            return None
        if job['name'].startswith('target-release-receipts'):
            if job['conclusion'] != 'skipped' or steps:
                return None
            continue
        permitted = set(named_step_blocks(blocks[job['name']])) | {'Set up job', 'Complete job'}
        if job['name'] == 'wake-release-lifecycle-after-terminal-release':
            permitted.add('Post Checkout protected lifecycle wake authority')
        step_names = []
        for step in steps:
            if (not isinstance(step, dict) or step.get('name') not in permitted
                or step.get('status') != 'completed'
                or step.get('conclusion') not in {'success', 'failure', 'cancelled', 'skipped'}):
                return None
            step_names.append(step['name'])
        if len(step_names) != len(set(step_names)):
            return None
    return [job for job in jobs if job['name'] not in names]


def release_attempt_never_entered(jobs, source=None):
    """Recognize a complete trusted attempt whose downstream jobs never entered.

    Callers own authenticated producer/source and complete attempt enumeration.
    GitHub can omit both impossible downstream jobs after admission. Only its
    exact admission plus skipped rollback wrapper shape proves that omission;
    partial, duplicate, unknown, or entered job shapes remain unproven.
    """
    if not isinstance(jobs, list) or not jobs or any(not isinstance(job, dict) for job in jobs):
        return False
    jobs = _release_nonentry_observers(jobs, source)
    if jobs is None:
        return False
    names = [job.get('name') for job in jobs]
    if ('migration-readiness' in names or
            (isinstance(source, str) and 'migration-readiness' in _job_blocks(source))):
        # This generation requires an explicit complete skipped downstream DAG;
        # never infer omission or classify an entered credentialed observer safe.
        expected = {'admission', 'discover-live', 'migration-readiness', 'release', 'preserve-rollback'}
        if set(names) != expected or len(names) != len(expected):
            return False
        readiness = next(job for job in jobs if job.get('name') == 'migration-readiness')
        if (readiness.get('status') != 'completed' or readiness.get('conclusion') != 'skipped'
                or readiness.get('steps') != []):
            return False
        jobs = [job for job in jobs if job is not readiness]
        names = [job.get('name') for job in jobs]
    allowed = {'admission', 'discover-live', 'release', 'preserve-rollback'}
    if (any(name not in allowed for name in names) or
            len(names) != len(set(names)) or names.count('admission') != 1):
        return False
    downstream = set(names) - {'admission', 'preserve-rollback'}
    if downstream not in (set(), {'discover-live', 'release'}):
        return False
    if not downstream and set(names) != {'admission', 'preserve-rollback'}:
        return False
    if not downstream:
        # Omission is meaningful only for this historical admission/needs
        # generation. An evolved or unavailable source is not absence proof.
        if not isinstance(source, str):
            return False
        blocks = _job_blocks(source)
        if set(blocks) != {'admission', 'discover-live', 'preserve-rollback', 'release',
                           'target-release-receipts', 'release-state-receipt',
                           'wake-release-lifecycle-after-terminal-release'}:
            return False
        gates = {
            'discover-live': {
                'needs': 'admission',
                'if': "needs.admission.outputs.admitted == 'true' && needs.admission.outputs.state == 'RELEASE_READY'",
            },
            'preserve-rollback': {
                'needs': 'discover-live',
                'if': "needs.discover-live.outputs.preserve_live_targets != 'true' && needs.discover-live.outputs.exact_live != 'true'",
            },
            'release': {
                'needs': '[discover-live, preserve-rollback]',
                'if': "${{ always() && needs.discover-live.result == 'success' && (needs.preserve-rollback.result == 'success' || needs.preserve-rollback.result == 'skipped') }}",
            },
        }
        for name, fields in gates.items():
            for field, expected in fields.items():
                values = re.findall(r'^    ' + field + r': (.*)$', blocks[name], re.MULTILINE)
                if values != [expected]:
                    return False
    for job in jobs:
        if job['name'] == 'admission':
            continue
        if job.get('conclusion') != 'skipped':
            return False
        # Contradictory execution detail cannot be treated as non-entry.
        steps = job.get('steps', [])
        if not isinstance(steps, list) or any(
                not isinstance(step, dict) or step.get('conclusion') != 'skipped'
                for step in steps):
            return False
    return True


def release_operation_history(repository, operation_id, application_revision, target, current_run, current_attempt, token, *, phase="intent"):
    """Return retained write-ahead intent, or prove publication never started.

    Absence/expiry is never negative deployment evidence. The independent Actions
    execution history must show the target publication never started. Unknown or
    truncated history is an error, not permission to upload again.
    """
    import tempfile
    def read_api(path):
        try:
            return _release_history_api(repository, path, token)
        except OSError as exc:
            raise ReleaseOperationHistoryUnproven("Release evidence provider unavailable") from exc
    if phase not in {"intent", "success", "submitted"}:
        raise ValueError("Unknown deployment evidence phase")
    name = f"legend-release-operation-{phase}-" + operation_id
    encoded = urllib.parse.quote(name, safe="")
    payload = read_api(f"actions/artifacts?name={encoded}&per_page=100")
    artifacts = payload.get("artifacts")
    if not isinstance(artifacts, list) or payload.get("total_count", 0) > len(artifacts):
        raise ReleaseOperationHistoryUnproven("Incomplete deployment intent artifact inventory")
    records = []
    for artifact in artifacts:
        if artifact.get("name") != name:
            raise RuntimeError("Deployment intent artifact identity mismatch")
        run_id = artifact.get("workflow_run", {}).get("id")
        if not run_id:
            raise RuntimeError("Deployment intent producer is missing")
        run = read_api(f"actions/runs/{run_id}")
        fast_track = is_fast_track_producer(repository, run)
        completion = run.get("path") == ".github/workflows/" + FAST_TRACK_COMPLETION_WORKFLOW
        recovery = (run.get('path') == '.github/workflows/' + FAST_TRACK_RELEASE_WORKFLOW
                    and run.get('event') == 'workflow_dispatch')
        if completion and phase != "success":
            raise RuntimeError("Completion observer cannot produce intent or submission history")
        if not fast_track and not completion and not recovery and (run.get("path") != ".github/workflows/" + DIRECT_RELEASE_WORKFLOW or
                run.get("head_branch") != TRUSTED_PR_BASE or run.get("event") != "workflow_dispatch" or
                run.get("head_repository", {}).get("full_name", "").lower() != repository.lower()):
            raise RuntimeError("Deployment intent has an untrusted producer")
        if artifact.get("expired"):
            raise ReleaseOperationHistoryUnproven("Deployment intent expired; reconciliation required")
        if _RECOVERY_RECEIPT_BYTES.get() is not None:
            record = _release_history_json(repository, run_id, artifact, 'operation.json',
                                          verify_digest=True, max_bytes=32768)
        else:
            with tempfile.TemporaryDirectory(prefix="legend-operation-read-") as directory:
                try:
                    _download_run_artifact(repository, run_id, name, Path(directory))
                except (OSError, subprocess.SubprocessError) as exc:
                    raise ReleaseOperationHistoryUnproven("Deployment intent download unavailable") from exc
                path = Path(directory) / "operation.json"
                if path.stat().st_size > 32768:
                    raise RuntimeError("Oversized deployment intent")
                record = json.loads(path.read_text())
        if (record.get("operationId") != operation_id or record.get("target") != target or
                record.get("applicationRevision") != application_revision or
                record.get("producingRun") != run_id or record.get("phase") != phase):
            raise RuntimeError("Deployment intent does not bind its producer and operation")
        if fast_track and (record.get('applicationRevision') != run['head_sha']
            or type(record.get('producingAttempt')) is not int
            or not 1 <= record['producingAttempt'] <= run.get('run_attempt', 0)):
            raise RuntimeError('Fast-track deployment intent has unbound revision or attempt')
        if recovery:
            _completion_inventory(payload, "artifacts")
            exact = _release_history_json(repository, run_id, artifact, "operation.json", verify_digest=True,
                **({'max_bytes': 32768} if _RECOVERY_RECEIPT_BYTES.get() is not None else {}))
            if exact != record or not validate_fast_track_recovery_record(repository, run, artifact, record, token):
                raise RuntimeError("Recovery receipt artifact identity changed")
        elif "recoveryEvidence" in record:
            raise RuntimeError("Recovery evidence requires its actual dispatch producer")
        if completion:
            _completion_inventory(payload, "artifacts")
            exact = _release_history_json(repository, run_id, artifact, "operation.json", verify_digest=True,
                **({'max_bytes': 32768} if _RECOVERY_RECEIPT_BYTES.get() is not None else {}))
            if exact != record:
                raise RuntimeError("Completion receipt artifact identity changed")
            if validate_fast_track_completion_record(repository, run, artifact, record, token) is False:
                continue
        elif "completionEvidence" in record:
            raise RuntimeError("Completion evidence requires its real observer producer")
        records.append(record)
    if records:
        identity_keys = ("operationId", "applicationRevision", "target", "packageDigest", "baseline")
        if any(any(record.get(key) != records[0].get(key) for key in identity_keys) for record in records[1:]):
            raise RuntimeError("Competing deployment intents require reconciliation")
        if phase == "success" and any("completionEvidence" in r for r in records) and any(r.get("deploymentIds") != records[0].get("deploymentIds") for r in records):
            raise RuntimeError("Competing completion success evidence requires reconciliation")
        if phase in {"intent", "submitted"} and any(record != records[0] for record in records[1:]):
            raise RuntimeError("Competing deployment intent producers require reconciliation")
        return records[0]
    if phase in {"success", "submitted"}:
        return None

    recovery_history_no_unjournaled_entry(repository, application_revision, current_run, current_attempt, token, target=target, operation_id=operation_id)

    for run in fast_track_execution_history(repository, token):
        if run['head_sha'] != application_revision:
            continue
        count = run.get('run_attempt')
        if type(count) is not int or not 1 <= count <= 100:
            raise ReleaseOperationHistoryUnproven('Fast-track deployment attempts incomplete')
        saw_owner = False
        for attempt in range(1, count + 1):
            if run['id'] == current_run and attempt == current_attempt:
                continue
            payload = read_api(f"actions/runs/{run['id']}/attempts/{attempt}/jobs?per_page=100")
            jobs = payload.get('jobs')
            if not isinstance(jobs, list) or payload.get('total_count') != len(jobs):
                raise ReleaseOperationHistoryUnproven('Fast-track deployment jobs incomplete')
            if not jobs and (run.get('status') == 'completed' or attempt < count):
                continue
            label = RELEASE_TARGETS[target]['releaseName']
            display = {'portal': 'AgentPortal', 'client': 'ClientApp', 'protect': 'Protect', 'parfait': 'Parfait', 'website': 'Website'}[target]
            owners = [j for j in jobs if j.get('name') in {'Affected App Deploy — ' + display, 'UI Deploy — ' + display}]
            if not owners and fast_track_skipped_matrix(jobs):
                saw_owner = True
                continue
            if not owners and attempt > 1 and saw_owner:
                continue
            if len(owners) != 1:
                raise ReleaseOperationHistoryUnproven('Fast-track deployment owner unproven: ' + label)
            saw_owner = True
            job = owners[0]
            if job.get('conclusion') == 'skipped' or job.get('status') == 'queued':
                continue
            steps = [s for s in job.get('steps', []) if s.get('name') in {
                'Submit one direct ZIP deployment', 'Publish or reconcile exact application bytes'}]
            if len(steps) != 1 or (steps[0].get('status') != 'queued' and steps[0].get('conclusion') != 'skipped'):
                if attested_fast_track_preupload_failure(repository, run, job, attempt, target, operation_id, token):
                    continue
                raise ReleaseOperationHistoryUnproven('Fast-track publication entered without retained intent: ' + label)

    for run in _release_history_runs(repository, token):
        if run.get("head_branch") != TRUSTED_PR_BASE or run.get("event") != "workflow_dispatch":
            continue
        run_id = run["id"]
        if run_id == current_run and current_attempt == 1:
            continue
        if (run.get('path') != '.github/workflows/' + DIRECT_RELEASE_WORKFLOW or
                run.get('head_repository', {}).get('full_name', '').lower() != repository.lower()):
            raise RuntimeError('Release execution history has an untrusted producer')
        prior_proof = _RELEASE_HISTORY_EXCLUSIONS.get(application_revision, {}).get(run_id)
        if (prior_proof and run.get('status') == 'completed' and prior_proof['headSha'] == run.get('head_sha') and
                prior_proof['runAttempt'] == run.get('run_attempt', 1) and target in prior_proof['targets']):
            continue
        # Modern titles bind actual checkout authority. Legacy receipts below
        # bind the verified package producer instead of guessing event SHA.
        title = re.fullmatch(r"LEGEND release pr=[0-9]+ candidate=[a-f0-9]{40} authority=([a-f0-9]{40})", run.get("display_title", ""))
        if title:
            comparison = read_api(f"compare/{application_revision}...{title.group(1)}")
            if comparison.get("status") in {"behind", "diverged"}:
                _remember_release_exclusion(application_revision, run, target)
                continue
            if comparison.get("status") not in {"ahead", "identical"}:
                raise ReleaseOperationHistoryUnproven("Unknown candidate ancestry in release history")
        attempts = run.get("run_attempt", 1)
        if type(attempts) is not int or attempts < 1:
            raise ReleaseOperationHistoryUnproven("Invalid prior deployment attempt inventory")
        for attempt in range(1, attempts + 1):
            if run_id == current_run and attempt == current_attempt:
                continue
            jobs_payload = read_api(f"actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100")
            jobs = jobs_payload.get("jobs")
            count = jobs_payload.get("total_count")
            if (not isinstance(jobs, list) or type(count) is not int or count != len(jobs)):
                raise ReleaseOperationHistoryUnproven("Incomplete prior deployment job history")
            if (not jobs and jobs_payload.get('total_count') == 0 and
                    (run.get('status') == 'completed' or attempt < attempts)):
                # Complete Actions enumeration proves this attempt never started a job.
                continue
            if release_attempt_never_entered(jobs):
                continue
            if (any(job.get('name') == 'admission' for job in jobs) and
                    not any(job.get('name') in {'release', 'discover-live'} for job in jobs)):
                source = _release_history_source(repository, run['head_sha'], '.github/workflows/' + DIRECT_RELEASE_WORKFLOW, token)
                if release_attempt_never_entered(jobs, source):
                    continue
            owners = [job for job in jobs if job.get("name") in {"release", f"publish-target ({target})"}]
            if len(owners) != 1:
                raise ReleaseOperationHistoryUnproven("Release execution generation lacks one canonical publication owner")
            job = owners[0]
            if (job.get('conclusion') == 'skipped' or job.get('status') == 'queued' or
                    (job.get('status') == 'completed' and job.get('conclusion') == 'cancelled' and
                     job.get('steps') == [])):
                continue
            names = _historical_publication_names(target)
            publication = [step for step in job.get('steps', []) if step.get('name') in names]
            shared_fanout = [
                step for step in job.get('steps', [])
                if step.get('name') in {
                    'Publish canonical selected targets in parallel',
                    'Submit canonical selected targets in parallel',
                }
            ]
            if len(shared_fanout) == 1 and (
                shared_fanout[0].get('conclusion') == 'skipped'
                or shared_fanout[0].get('status') == 'queued'
            ):
                # The shared mutation owner never entered, so none of its selected
                # app targets can have written even if target receipt observers exist.
                continue
            if len(publication) == 1 and (publication[0].get('conclusion') == 'skipped' or publication[0].get('status') == 'queued'):
                # Positive target execution proof works for retired target-owned
                # publication formats even when old receipt artifacts expired.
                continue
            if job.get('name') != 'release':
                raise ReleaseOperationHistoryUnproven('Target publication entered without retained intent')
            try:
                prior_revision = _release_attempt_package_revision(repository, run, attempt, job, token, target)
            except (OSError, subprocess.SubprocessError) as exc:
                raise ReleaseOperationHistoryUnproven('Historical package proof provider unavailable') from exc
            if prior_revision == application_revision:
                if _historical_publication_failed_before_first_write(
                        repository, job, application_revision, target, prior_revision, token):
                    # The exact historical source contract plus authenticated log
                    # prove this target failed before durable intent and before Azure submit.
                    continue
                raise ReleaseOperationHistoryUnproven('Prior publication may have written this immutable package; missing intent is not absence proof')
        _remember_release_exclusion(application_revision, run, target)
    return None



def _failed_transaction_preparation_without_writes(source, owner):
    """A failed preflight is not a lost plan when every later effect was skipped."""
    if owner.get('status') != 'completed' or owner.get('conclusion') != 'failure':
        return False
    blocks = named_step_blocks(_job_blocks(source).get('release', ''))
    prepare = 'Prepare complete immutable release transaction'
    if prepare not in blocks or '--prepare-only' not in blocks[prepare]:
        return False
    steps = owner.get('steps')
    if not isinstance(steps, list):
        return False
    outcomes = {}
    for step in steps:
        outcomes.setdefault(step.get('name'), []).append(step)
    failed = outcomes.get(prepare, [])
    if len(failed) != 1 or failed[0].get('conclusion') != 'failure':
        return False
    # These observers may run after failure. Require their original bodies to
    # match the canonical source before excluding them from execution proof.
    observers = {'Refresh Azure OIDC before transactional publication',
                 'Enforce complete direct deployment outcome'}
    current = named_step_blocks(_job_blocks(
        Path('.github/workflows/' + DIRECT_RELEASE_WORKFLOW).read_text()).get('release', ''))
    later = list(blocks)[list(blocks).index(prepare) + 1:]
    legacy_targets = {f'Publish canonical target ({key})' for key in RELEASE_TARGETS}
    receipt_targets = {
        f'Confirm first-pass durable publication receipt ({key})'
        for key in RELEASE_TARGETS
    }
    legacy_shape = legacy_targets.issubset(set(later))
    current_shape = (
        'Submit canonical selected targets in parallel' in later
        and receipt_targets.issubset(set(later))
    )
    if not (legacy_shape or current_shape):
        return False
    for name in later:
        if name in observers and blocks[name] == current.get(name):
            continue
        matches = outcomes.get(name, [])
        if len(matches) != 1 or matches[0].get('conclusion') != 'skipped':
            return False
    return True


def release_transaction_identity(revision, target_digests, target_materials=None):
    """Canonical plan identity: authorization candidate plus exact target material."""
    if (not isinstance(revision, str) or not re.fullmatch('[a-f0-9]{40}', revision) or
            not isinstance(target_digests, dict) or not target_digests or
            any(key not in RELEASE_TARGETS or not re.fullmatch('[a-f0-9]{64}', digest)
                for key, digest in target_digests.items())):
        raise ValueError('Transaction plan scope/content identity mismatch')
    identity = dict(candidateRevision=revision, packageDigests=dict(sorted(target_digests.items())))
    if target_materials is not None:
        import importlib.util
        if not isinstance(target_materials, dict) or set(target_materials) != set(target_digests):
            raise ValueError('Complete transaction target material required')
        spec = importlib.util.spec_from_file_location('transaction_package_owner', Path(__file__).with_name('release-package.py'))
        owner = importlib.util.module_from_spec(spec); spec.loader.exec_module(owner)
        material = {key: owner.validate_target_material(key, row) for key, row in target_materials.items()}
        if any(row['packageDigest'] != target_digests[key] for key, row in material.items()):
            raise ValueError('Transaction target material digest mismatch')
        identity = dict(candidateRevision=revision, targetMaterials=material)
    return hashlib.sha256(json.dumps(identity, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def release_transaction_plan_history(repository, plan_id, revision, target_digests, run, attempt, token, *, target_materials=None):
    """Restore the complete original transaction, including untouched targets.

    None only means this exact plan artifact is absent. It is never first-write
    authorization; per-target history and canonical admission still must pass.
    """
    expected = release_transaction_identity(revision, target_digests, target_materials)
    if plan_id != expected or not target_digests or any(key not in RELEASE_TARGETS for key in target_digests):
        raise ValueError('Transaction plan scope/content identity mismatch')
    name = 'legend-release-transaction-plan-' + plan_id
    inventory = _release_history_api(repository, f"actions/artifacts?name={name}&per_page=100", token)
    artifacts = inventory.get('artifacts')
    if not isinstance(artifacts, list) or inventory.get('total_count', 0) > len(artifacts):
        raise ReleaseOperationHistoryUnproven('Incomplete transaction plan inventory')
    retained = None
    for artifact in artifacts:
        if artifact.get('name') != name:
            raise RuntimeError('Transaction plan artifact identity mismatch')
        producer = artifact.get('workflow_run', {}).get('id')
        if not producer:
            raise RuntimeError('Transaction plan producer is missing')
        producing_run = _release_history_api(repository, f'actions/runs/{producer}', token)
        if (producing_run.get('path') != '.github/workflows/' + DIRECT_RELEASE_WORKFLOW or
                producing_run.get('head_branch') != TRUSTED_PR_BASE or producing_run.get('event') != 'workflow_dispatch' or
                producing_run.get('head_repository', {}).get('full_name', '').lower() != repository.lower()):
            raise RuntimeError('Transaction plan producer is untrusted')
        plan = _release_history_json(repository, producer, artifact, 'release-transaction.json')
        if (plan.get('schemaVersion') != (2 if target_materials is not None else 1) or plan.get('planId') != plan_id or
                plan.get('candidateRevision') != revision or plan.get('producingRun') != producer or
                type(plan.get('producingAttempt')) is not int or plan['producingAttempt'] < 1 or
                plan['producingAttempt'] > producing_run.get('run_attempt', 1)):
            raise RuntimeError('Transaction plan does not bind immutable producer/scope')
        rows = plan.get('targets')
        if not isinstance(rows, list) or len(rows) != len(target_digests):
            raise RuntimeError('Original transaction cannot be silently split')
        seen = set()
        for row in rows:
            key = row.get('app')
            if (key in seen or key not in target_digests or row.get('packageDigest') != target_digests[key] or
                    not re.fullmatch('[a-f0-9]{40}', row.get('revision', ''))):
                raise RuntimeError('Transaction original baseline/package binding is invalid')
            seen.add(key)
            if target_materials is not None and row.get('targetMaterial') != target_materials[key]:
                raise RuntimeError('Transaction target producer/material binding is invalid')
            rollback = row.get('rollbackEvidence')
            desired = target_materials[key]['producerRevision'] if target_materials is not None else revision
            if row['revision'] == desired:
                if rollback is not None:
                    raise RuntimeError('Candidate target has unexpected rollback evidence')
            elif (not isinstance(rollback, dict) or set(rollback) not in ({'artifact', 'runId', 'revision', 'packageDigest'}, {'artifact', 'runId', 'revision', 'packageDigest', 'artifactId'}) or
                  ('artifactId' in rollback and (type(rollback['artifactId']) is not int or rollback['artifactId'] < 1)) or
                  rollback.get('revision') != row['revision'] or type(rollback.get('runId')) is not int or rollback['runId'] < 1 or
                  not re.fullmatch('[a-zA-Z0-9_.-]{1,256}', rollback.get('artifact', '')) or
                  not re.fullmatch('[a-f0-9]{64}', rollback.get('packageDigest', ''))):
                raise RuntimeError('Transaction rollback package proof is invalid')
        if retained is not None and retained.get('targets') != rows:
            raise RuntimeError('Competing original transaction baselines require reconciliation')
        retained = plan if retained is None else retained
    if retained is not None:
        if 'historySnapshot' not in retained:
            raise RuntimeError('Transaction plan lacks authenticated history snapshot')
        import_release_history_snapshot(retained['historySnapshot'], revision)
        return retained
    # A deleted full plan is not permission to bless reobserved live baselines
    # for an untouched sibling. Prove no prior overlapping original transaction.
    for prior in _release_history_runs(repository, token):
        if prior.get('head_branch') != TRUSTED_PR_BASE or prior.get('event') != 'workflow_dispatch':
            continue
        if (prior.get('path') != '.github/workflows/' + DIRECT_RELEASE_WORKFLOW or
                prior.get('head_repository', {}).get('full_name', '').lower() != repository.lower()):
            raise RuntimeError('Transaction execution history has an untrusted producer')
        if prior['id'] == run and attempt == 1:
            continue
        source = _release_history_source(repository, prior['head_sha'], '.github/workflows/' + DIRECT_RELEASE_WORKFLOW, token)
        if 'Prepare complete immutable release transaction' not in named_step_blocks(_job_blocks(source).get('release', '')):
            # This historical generation had no durable all-target plan to lose.
            # Its writes remain governed by the operation history proof below.
            continue
        attempts = prior.get('run_attempt', 1)
        if type(attempts) is not int or attempts < 1:
            raise ReleaseOperationHistoryUnproven('Invalid original transaction attempt inventory')
        for previous_attempt in range(1, attempts + 1):
            if prior['id'] == run and previous_attempt == attempt:
                continue
            jobs = _release_history_api(repository, f"actions/runs/{prior['id']}/attempts/{previous_attempt}/jobs?per_page=100", token)
            rows = jobs.get('jobs')
            count = jobs.get('total_count')
            if (not isinstance(rows, list) or type(count) is not int or count != len(rows)):
                raise ReleaseOperationHistoryUnproven('Original transaction execution history is incomplete')
            if (not rows and count == 0 and
                    (prior.get('status') == 'completed' or previous_attempt < attempts)):
                # Complete Actions enumeration proves this attempt started no jobs.
                # Preserve the same proof used by release_operation_history.
                continue
            if release_attempt_never_entered(rows, source):
                continue
            owners = [row for row in rows if row.get('name') == 'release']
            if len(owners) != 1:
                raise ReleaseOperationHistoryUnproven(
                    f"Original transaction publication owner is missing: run={prior['id']} attempt={previous_attempt}")
            owner = owners[0]
            if owner.get('conclusion') == 'skipped' or owner.get('status') == 'queued':
                continue
            prepared = [row for row in owner.get('steps', []) if row.get('name') == 'Prepare complete immutable release transaction']
            if len(prepared) != 1:
                raise ReleaseOperationHistoryUnproven('Original transaction preparation history is missing')
            if prepared[0].get('conclusion') == 'skipped' or prepared[0].get('status') == 'queued':
                continue
            prior_revision = _release_attempt_package_revision(repository, prior, previous_attempt, owner, token, next(iter(target_digests)), aggregate=True)
            if prior_revision != revision:
                continue
            artifacts = _release_history_api(repository, f"actions/runs/{prior['id']}/artifacts?per_page=100", token)
            items = artifacts.get('artifacts')
            if not isinstance(items, list) or artifacts.get('total_count', 0) > len(items):
                raise ReleaseOperationHistoryUnproven('Original transaction artifact history is incomplete')
            plans = [row for row in items if re.fullmatch('legend-release-transaction-plan-[a-f0-9]{64}', row.get('name', ''))]
            if not plans and _failed_transaction_preparation_without_writes(source, owner):
                continue
            if not plans:
                raise ReleaseOperationHistoryUnproven('Original transaction plan is missing; untouched target baseline cannot be replaced')
            for item in plans:
                original = _release_history_json(repository, prior['id'], item, 'release-transaction.json')
                if original.get('candidateRevision') != revision:
                    continue
                if original.get('producingRun') != prior['id'] or original.get('producingAttempt') != previous_attempt:
                    raise ReleaseOperationHistoryUnproven('Original transaction plan attempt is not bound')
                previous_targets = {row.get('app') for row in original.get('targets', [])}
                if not previous_targets or previous_targets.intersection(target_digests):
                    raise ReleaseOperationHistoryUnproven('Overlapping original transaction scope cannot be silently split or replaced')
    return None


def _artifact_rows(repository: str, name: str, token: str):
    encoded = urllib.parse.quote(name, safe="")
    payload = api_get(
        repository,
        f"actions/artifacts?name={encoded}&per_page=100",
        token,
    )
    return [
        row for row in payload.get("artifacts", [])
        if not row.get("expired")
    ]


def _run_artifact_names(repository: str, run_id: int, token: str):
    payload = api_get(
        repository,
        f"actions/runs/{run_id}/artifacts?per_page=100",
        token,
    )
    return {
        row.get("name")
        for row in payload.get("artifacts", [])
        if row.get("name") and not row.get("expired")
    }


def _download_run_artifact(repository: str, run_id: int, name: str, directory: Path, *, artifact_id=None, expected_digest=None):
    """Retry only transient GitHub artifact GET failures, never a release mutation.

    CLI stderr may contain signed download URLs. Consume it privately; expose
    only fixed outcomes. Extraction still undergoes the existing caller's
    immutable evidence identity/hash validation before it can grant authority.
    """
    if expected_digest is not None and (
            type(artifact_id) is not int or artifact_id < 1 or not isinstance(expected_digest, str)
            or not re.fullmatch('sha256:[a-f0-9]{64}', expected_digest)):
        raise ValueError('Digest verification requires immutable artifact ID and SHA256')
    directory.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    if env.get("GITHUB_TOKEN") and not env.get("GH_TOKEN"):
        env["GH_TOKEN"] = env["GITHUB_TOKEN"]
    command = [
        "gh", "run", "download", str(run_id),
        "--repo", repository,
        "--name", name,
        "--dir", str(directory),
    ]
    if artifact_id is not None:
        if type(artifact_id) is not int or artifact_id < 1:
            raise ValueError('Immutable artifact ID required')
        command = ['gh', 'api', f'repos/{repository}/actions/artifacts/{artifact_id}/zip']
    for attempt in range(3):
        try:
            if artifact_id is None:
                result = subprocess.run(command, check=False, env=env, capture_output=True, text=True, timeout=evidence_remaining(120))
            else:
                import tempfile
                import zipfile
                import stat
                with tempfile.TemporaryFile() as archive:
                    result = subprocess.run(command, check=False, env=env, stdout=archive,
                                            stderr=subprocess.PIPE, text=True, timeout=evidence_remaining(120))
                    if result.returncode == 0:
                        archive.seek(0)
                        if expected_digest is not None:
                            actual = 'sha256:' + hashlib.file_digest(archive, 'sha256').hexdigest()
                            if actual != expected_digest:
                                raise RuntimeError('Immutable artifact digest mismatch')
                            archive.seek(0)
                        with zipfile.ZipFile(archive) as contents:
                            entries = contents.infolist()
                            root = directory.resolve()
                            if (len(entries) > 10000 or sum(row.file_size for row in entries) > 2 * 1024**3 or
                                len({row.filename for row in entries}) != len(entries) or
                                any(stat.S_ISLNK(row.external_attr >> 16) or Path(row.filename).is_absolute() or
                                    not (root / row.filename).resolve().is_relative_to(root) for row in entries)):
                                raise ValueError('Immutable artifact archive contains unsafe entries')
                            contents.extractall(directory)
        except subprocess.TimeoutExpired:
            raise EvidenceLookupUnavailable("Artifact evidence read unavailable", status=408) from None
        except OSError:
            raise EvidenceLookupUnavailable("Artifact evidence read unavailable") from None
        if result.returncode == 0:
            return
        # HTTP failures are classified without reproducing provider text or URLs.
        # Do not retry authentication, absent/expired artifacts, or unclassified
        # errors. A 503 from the Actions download service is a safe GET retry.
        messages = result.stderr or ""
        transient = bool(re.search(r"\bHTTP (?:408|429|500|502|503|504)\b", messages))
        if transient and attempt < 2:
            time.sleep(evidence_remaining(2 ** attempt))
            continue
        raise EvidenceLookupUnavailable(
            "Transient artifact evidence read exhausted bounded retries"
            if transient else "Artifact evidence read unavailable"
        )


def read_step5_results(path):
    """Reject incomplete, aborted or malformed TRX before using any child proof."""
    import xml.etree.ElementTree as ET
    root = ET.parse(path).getroot()
    rows = [node for node in root.iter() if node.tag.endswith("UnitTestResult")]
    counters = next((node for node in root.iter() if node.tag.endswith("Counters")), None)
    summary = next((node for node in root.iter() if node.tag.endswith("ResultSummary")), None)
    if summary is None or summary.get("outcome") not in {"Completed", "Passed", "Failed"}:
        raise ValueError("Incomplete test evidence: run did not complete")
    if not rows or counters is None or int(counters.get("total", "0")) != len(rows):
        raise ValueError("Incomplete test evidence: missing results or mismatched total")
    if any(int(counters.get(key, "0")) for key in ("error", "timeout", "aborted", "disconnected", "inProgress", "pending")):
        raise ValueError("Incomplete test evidence: execution errors remain")
    if any(node.get("outcome") not in {"Passed", "Failed", "NotExecuted"} or not node.get("testName") for node in rows):
        raise ValueError("Incomplete test evidence: unknown test outcome or identity")
    result = {}
    for node in rows:
        name = node.get("testName")
        outcome = node.get("outcome")
        previous = result.get(name)
        if previous is None:
            result[name] = outcome
        elif previous != outcome:
            # TRX may contain repeated rows for one logical test identity
            # (for example framework retry/reporting duplication). Reusing the
            # evidence is safe only when every terminal observation agrees.
            raise ValueError("Ambiguous duplicate test identity")
    return result


def merge_step5_class_results(prior_path, repair_path, classes, output_path):
    """Replace only complete affected class evidence; retain untouched rows."""
    import copy
    import xml.etree.ElementTree as ET
    classes = sorted(set(classes))
    if not classes:
        raise ValueError('Bounded repair requires affected classes')
    prior = read_step5_results(prior_path)
    repair = read_step5_results(repair_path)
    affected = lambda name: any(name.startswith(value + '.') for value in classes)
    if any(not affected(name) for name in repair):
        raise ValueError('Repair contains tests outside affected classes')
    for value in classes:
        if not any(name.startswith(value + '.') for name in repair):
            raise ValueError('Repair omitted affected class: ' + value)
    if any(affected(name) and name not in repair for name in prior):
        raise ValueError('Repair omitted prior tests from affected class')
    tree = ET.parse(prior_path)
    root = tree.getroot()
    results_node = next((node for node in root.iter() if node.tag.endswith('Results')), None)
    if results_node is None:
        raise ValueError('Prior TRX has no Results node')
    for node in list(results_node):
        if node.tag.endswith('UnitTestResult') and affected(node.get('testName', '')):
            results_node.remove(node)
    repair_root = ET.parse(repair_path).getroot()
    for node in repair_root.iter():
        if node.tag.endswith('UnitTestResult'):
            results_node.append(copy.deepcopy(node))
    rows = [node for node in root.iter() if node.tag.endswith('UnitTestResult')]
    counters = next(node for node in root.iter() if node.tag.endswith('Counters'))
    counters.set('total', str(len(rows)))
    counters.set('executed', str(sum(node.get('outcome') != 'NotExecuted' for node in rows)))
    for outcome in ('Passed', 'Failed', 'NotExecuted'):
        counters.set(outcome[0].lower() + outcome[1:], str(sum(node.get('outcome') == outcome for node in rows)))
    summary = next(node for node in root.iter() if node.tag.endswith('ResultSummary'))
    summary.set('outcome', 'Failed' if any(node.get('outcome') != 'Passed' for node in rows) else 'Passed')
    target = Path(output_path)
    target.parent.mkdir(parents=True, exist_ok=True)
    tree.write(target, encoding='utf-8', xml_declaration=True)
    read_step5_results(target)


def cmd_step5_merge(args):
    merge_step5_class_results(args.prior, args.repair, args.classes.split(';'), args.output)
    Path(args.output + '.provenance.json').write_text(json.dumps({
        'materializationAuthoritySha256': _step5_merge_fingerprint(Path(__file__).read_text()),
        'sourceRunId': args.source_run, 'sourceArtifact': args.source_artifact,
        'sourceRevision': args.source_revision, 'approvedRevision': args.approved_revision,
        'replacedClasses': args.classes.split(';'),
        'priorSha256': hashlib.sha256(Path(args.prior).read_bytes()).hexdigest(),
        'repairSha256': hashlib.sha256(Path(args.repair).read_bytes()).hexdigest(),
        'effectiveSha256': hashlib.sha256(Path(args.output).read_bytes()).hexdigest(),
    }, sort_keys=True) + '\n')


@functools.lru_cache(maxsize=128)
def _step5_artifact_results(repository, run_id, artifact, kind, token):
    """Authenticate the retained child by its artifact bytes, not parent status.

    The canonical workflow uploads this artifact only after the child suite has
    completed. Reuse additionally parses the TRX and rejects incomplete,
    conflicting or malformed evidence, so a later parent failure cannot erase
    the successful child and no jobs-API self-read is required.
    """
    import tempfile
    with tempfile.TemporaryDirectory(prefix="step5-child-") as temporary:
        directory = Path(temporary)
        _download_run_artifact(repository, run_id, artifact, directory)
        return read_step5_results(directory / (kind + ".trx"))


def _step5_artifact_complete(repository, run_id, artifact, kind, token):
    return bool(_step5_artifact_results(repository, run_id, artifact, kind, token))


def _step5_prior_candidate_evidence(
    repository: str,
    current_run_id: int,
    head_branch: str,
    token: str,
    current_sha: str,
):
    """Find the newest compatible child artifact across trusted PR branches.

    Parent failure never erases a completed candidate child. Compatibility is
    checked before selection, so a newer incompatible run cannot hide an older
    equivalent producer.
    """
    workflow_name = "step5-isolated-conversion-mapping-validation.yml"
    workflow_path = WORKFLOW_PATHS[workflow_name]
    workflow = urllib.parse.quote(workflow_name, safe="")
    payload = api_get(repository,
        f"actions/workflows/{workflow}/runs?event=pull_request&status=completed&per_page=100", token)
    runs = sorted(payload.get("workflow_runs", []),
        key=lambda row: (row.get("updated_at") or row.get("created_at", ""), int(row.get("id", 0))),
        reverse=True)
    for run in runs:
        run_id = int(run.get("id") or 0)
        if not run_id or run_id == current_run_id:
            continue
        try:
            head_sha = run["head_sha"]
            if not _trusted_lineage_run(repository, run, workflow_path, current_sha):
                if not _trusted_pr_run(repository, run, workflow_path, token):
                    continue
            impact = step5_dependency_change(head_sha, current_sha)
            if impact is None or not _step5_candidate_producer_compatible(head_sha, workflow_path):
                continue
            artifact = f"step5-candidate-{head_sha}"
            if artifact not in _run_artifact_names(repository, run_id, token):
                continue
            if not _step5_artifact_complete(repository, run_id, artifact, "candidate", token):
                continue
            rows = _step5_artifact_results(repository, run_id, artifact, "candidate", token)
            return {"runId": run_id, "headSha": head_sha, "artifact": artifact,
                    "testNames": tuple(rows)}
        except EvidenceLookupUnavailable:
            raise
        except Exception:
            continue
    return None


def _test_file_dependency_patterns(source):
    """Conservatively bind repository files actually read by source-contract tests.

    Compile proof never consumes these runtime dependencies. For executing tests,
    literal files/directories and copied AppContext fixtures are exact inputs.
    Computed single-file paths are resolved by the existing literal-token and
    copied-alias pass below instead of poisoning the gate with the whole repo.
    Computed directory enumeration remains fail-closed because it can observe an
    arbitrary repository subtree whose members are not statically enumerable.
    """
    calls = re.compile(r"\b(Directory\.(?:Get|Enumerate)(?:Files|Directories|FileSystemEntries)|File\.(?:Read\w*|Open\w*|Exists)|(?:new\s+)?(?:StreamReader|FileStream))\s*\(")
    patterns = set()
    for match in calls.finditer(source):
        tail = source[match.end():]
        literal = re.match(r'\s*(@?)"((?:[^"\\]|\\.)*)"\s*(?=[,)])', tail)
        directory = match.group(1).startswith("Directory.")
        if literal:
            path = literal.group(2).replace("\\\\", "/").replace("\\", "/").strip("/")
            if not path or path == "." or ".." in path.split("/"):
                patterns.add("**")
            else:
                patterns.add(path.rstrip("/") + "/**" if directory else path)
                # A relative runtime file can be a csproj Link alias; retain all
                # matching source basenames as well as exact repository paths.
                if not directory:
                    patterns.add("**/" + Path(path).name)
            continue
        copied = re.match(r'\s*Path\.Combine\(\s*AppContext\.BaseDirectory\s*,\s*"([^"\r\n]+)"\s*\)', tail)
        if copied and not directory:
            patterns.add("**/" + Path(copied.group(1)).name)
            continue
        if directory:
            patterns.add("**")
    return tuple(sorted(patterns))


def _step5_source_classes(source):
    namespace = re.search(r"\bnamespace\s+([\w.]+)\s*[;{]", source)
    classes = re.findall(r"\b(?:public|internal)\s+(?:(?:sealed|partial|abstract|static)\s+)*class\s+(\w+)", source)
    if not namespace or not classes:
        return set()
    return {namespace.group(1) + "." + name for name in classes}


_READONLY_FIXTURE_HELPER = re.compile(
    r'private\s+static\s+string\s+\w+\s*\(\s*\)\s*=>\s*'
    r'File\.ReadAllText\(Path\.Combine\(AppContext\.BaseDirectory,\s*"[^"\r\n]+"\)\);')


def _step5_csharp_structure(source):
    """Mask comments and literals while preserving C# declaration geometry."""
    chars = list(source)
    length = len(source)

    def mask(begin, finish):
        for index in range(begin, min(finish, length)):
            if chars[index] not in "\r\n":
                chars[index] = " "

    i = 0
    while i < length:
        if source.startswith("//", i):
            finish = source.find("\n", i + 2)
            finish = length if finish < 0 else finish
            mask(i, finish)
            i = finish
            continue
        if source.startswith("/*", i):
            finish = source.find("*/", i + 2)
            finish = length if finish < 0 else finish + 2
            mask(i, finish)
            i = finish
            continue
        if source[i] == '"':
            quote_count = 1
            while i + quote_count < length and source[i + quote_count] == '"':
                quote_count += 1
            if quote_count >= 3:
                delimiter = '"' * quote_count
                finish = source.find(delimiter, i + quote_count)
                finish = length if finish < 0 else finish + quote_count
                mask(i, finish)
                i = finish
                continue

            verbatim = i > 0 and source[i - 1] == "@"
            finish = i + 1
            while finish < length:
                if verbatim and source.startswith('""', finish):
                    finish += 2
                    continue
                if source[finish] == '"' and (verbatim or source[finish - 1] != "\\"):
                    finish += 1
                    break
                if not verbatim and source[finish] == "\\":
                    finish += 2
                else:
                    finish += 1
            mask(i, finish)
            i = finish
            continue
        if source[i] == "'":
            finish = i + 1
            while finish < length:
                if source[finish] == "\\":
                    finish += 2
                    continue
                if source[finish] == "'":
                    finish += 1
                    break
                finish += 1
            mask(i, finish)
            i = finish
            continue
        i += 1
    return "".join(chars)


def _step5_isolated_test_source(source):
    """Admit only standalone test classes, never arbitrary C# dependency guesses.

    Comments and literals are masked before structural analysis, so prose cannot
    impersonate exported/static declarations while compact one-line C# remains
    valid input. Exported helpers, inherited/partial fixtures, extension types,
    static state and shared registrations still require full-suite proof.
    """
    source = _READONLY_FIXTURE_HELPER.sub("", source)
    structure = _step5_csharp_structure(source)
    if len(re.findall(r"\bclass\s+\w+", structure)) != 1:
        return False
    if re.search(
        r"\[Collection(?:\(|Attribute)|\b(?:partial|abstract|static)\s+class|"
        r"\bclass\s+\w+\s*[:<]|\b(?:record|struct|interface|enum|delegate)\s+\w+|"
        r"\[\s*(?:assembly|module)\s*:|\bglobal\s+using|ModuleInitializer|"
        r"CollectionDefinition|ICollectionFixture",
        structure,
    ):
        return False

    # Reject every static construct except a private static method declaration.
    for marker in re.finditer(r"\bstatic\b", structure):
        tail = structure[marker.start():]
        line_start = structure.rfind("\n", 0, marker.start()) + 1
        prefix = structure[line_start:marker.start()]
        private_method = (
            re.search(r"\bprivate\s*$", prefix) is not None
            and re.match(
                r"static\s+(?:async\s+)?[\w.<>,?\[\]]+\s+\w+\s*(?:<[^>]+>)?\s*\(",
                tail,
            ) is not None
        )
        if not private_method:
            return False

    # Every exported member must be the test class or an attributed test method.
    for visibility in re.finditer(r"\b(?:public|internal|protected)\b", structure):
        tail = structure[visibility.start():]
        if re.match(r"(?:public|internal)\s+(?:sealed\s+)?class\s+", tail):
            continue
        prefix = structure[:visibility.start()]
        attributes = re.search(r"((?:\[[^\]]+\]\s*)+)$", prefix)
        if not attributes or not re.search(r"\[(?:Fact|Theory)(?:\]|\()", attributes.group(1)):
            return False
        if not re.match(
            r"public\s+(?:async\s+)?(?:void|Task(?:<[^>]+>)?|ValueTask(?:<[^>]+>)?)\s+\w+\s*\(",
            tail,
        ):
            return False
    return bool(re.search(r"\[(?:Fact|Theory)(?:\]|\()", structure))

def _step5_extension_method_names(source):
    # Ordinary calls follow their declaring type through the source closure.
    # Only extension syntax can omit that type at its call site. A shared name
    # on an unrelated override (e.g. SendAsync) is not a dependency edge.
    return set(re.findall(
        r"\b(?:public|internal)\s+static\s+(?:async\s+)?[\w.<>,?\[\]]+\s+"
        r"(\w+)\s*(?:<[^>]+>)?\s*\(\s*this\s+", source))


def step5_dependency_change(prior_sha, current_sha, *, stop_on_change=False):
    """Return bounded invalidated classes, or None when suite proof is required.

    Project-copied data is a real test dependency, even when its owning release
    workflow is neutral to application binaries. Resolve consumers from source,
    including partial files and references between fixture classes. Unknown
    source/build inputs and unbounded helpers deliberately require full proof.
    """
    import posixpath
    import xml.etree.ElementTree as ET
    workflow = "step5-isolated-conversion-mapping-validation.yml"
    config = WORKFLOWS[workflow]
    changed = git_changed(prior_sha, current_sha)

    # Fast path for exact reuse/control-plane continuation. Do not archive and
    # analyze the entire AgentPortal.Tests graph when no Step 5 test/application
    # input changed. Workflow execution compatibility is authenticated separately
    # by _step5_jobs_unchanged before child evidence is reused.
    if not changed:
        return []
    # Narrow control-only fast path. These files govern Step 5 planning and
    # comparison, but are not runtime inputs to the AgentPortal.Tests suite.
    # Candidate/baseline workflow execution compatibility is authenticated
    # separately by _step5_jobs_unchanged. Other nominally "control" files
    # (for example the direct-release workflow/request) may be copied/read by
    # regression tests and therefore MUST continue through consumer discovery.
    fast_neutral = {
        WORKFLOW_PATHS[workflow],
        "scripts/validation-resume.py",
        "scripts/test-validation-resume.py",
        "scripts/test-release-policy.py",
    }
    if all(path in fast_neutral for path in changed):
        return []

    # One archive read avoids hundreds of subprocesses per historical producer.
    import io
    import tarfile
    archive = subprocess.run(["git", "archive", current_sha, "AgentPortal.Tests"],
        check=True, capture_output=True).stdout
    with tarfile.open(fileobj=io.BytesIO(archive)) as tree:
        contents = {entry.name: tree.extractfile(entry).read().decode("utf-8-sig")
                    for entry in tree.getmembers() if entry.isfile()
                    and (entry.name.endswith(".cs") or entry.name.endswith(".csproj"))}
    sources = {path: source for path, source in contents.items() if path.endswith(".cs")}
    classes = {path: _step5_source_classes(source) for path, source in sources.items()}
    project = ET.fromstring(contents["AgentPortal.Tests/AgentPortal.Tests.csproj"])
    copied = {}
    for node in project.iter():
        include = node.get("Include", "").replace("\\", "/")
        if not include or not (node.get("CopyToOutputDirectory") or node.find("CopyToOutputDirectory") is not None):
            continue
        path = posixpath.normpath("AgentPortal.Tests/" + include)
        link = node.get("Link") or node.findtext("Link") or posixpath.basename(path)
        copied[path] = link
    dynamic_inputs = {file: _test_file_dependency_patterns(source) for file, source in sources.items()}
    affected = set()
    for path in changed:
        # These four files govern Step 5 planning/comparison only. They remain
        # neutral even when mixed with a real test correction; otherwise one
        # bounded test edit plus planner maintenance falsely escalates to a full
        # AgentPortal suite. Other control files still flow through consumer
        # discovery because tests may copy/read them directly.
        if path in fast_neutral:
            continue
        if path in sources:
            old = git_show_file(prior_sha, path)
            own = classes[path]
            if (_READONLY_FIXTURE_HELPER.findall(old) != _READONLY_FIXTURE_HELPER.findall(sources[path])
                    or not own or own != _step5_source_classes(old)
                    or not _step5_isolated_test_source(old)
                    or not _step5_isolated_test_source(sources[path])):
                return None
            # A class referenced elsewhere is a shared fixture, not isolated.
            if any(re.search(r"\b" + re.escape(name.rsplit(".", 1)[-1]) + r"\b", source)
                   for file, source in sources.items() if file != path for name in own):
                return None
            affected.update(own)
        elif gate_matches(path, config["gates"]["candidate-full"]):
            return None
        elif path not in copied and not (matches(path, config["neutral"])
                or gate_matches(path, config["gates"]["comparison"])
                or release_control_only_path(path)):
            return None
        # Tests also read repository files directly without csproj copying.
        # Both forms share the same source-derived consumer closure.
        link = copied.get(path, path)
        consumers = [file for file, source in sources.items()
                     if link in source or posixpath.basename(link) in source
                     or matches(path, dynamic_inputs[file])]
        if path in copied and not consumers:
            return None
        if any(not classes[file] for file in consumers):
            return None
        for file in consumers:
            affected.update(classes[file])
        # Baseline equivalence needs only a yes/no answer. Once any consumer is
        # affected, no later path or transitive closure can restore equivalence.
        if stop_on_change and affected:
            return sorted(affected)
    # Follow shared helpers' exported method names too: extension-method users
    # need not spell the declaring static type at the call site.
    test_classes = set().union(*(classes[file] for file, source in sources.items()
        if re.search(r"\[(?:Fact|Theory)(?:\s|\]|\()", source)))
    while True:
        expanded = set(affected)
        referenced = {name.rsplit(".", 1)[-1] for name in affected}
        for file, source in sources.items():
            if classes[file] & affected and not classes[file] & test_classes:
                referenced.update(_step5_extension_method_names(source))
        for file, source in sources.items():
            if any(re.search(r"\b" + re.escape(name) + r"\b", source) for name in referenced):
                if not classes[file]:
                    return None
                expanded.update(classes[file])
        if expanded == affected:
            bounded = affected & test_classes
            return sorted(bounded) if bounded or not affected else None
        affected = expanded


def _step5_discovered_repair_classes(classes, test_names, changed_paths, source_revision=None):
    """Use complete prior discovery to distinguish tests from co-located helpers.

    Prior xUnit discovery is authoritative for unchanged source files, so helper
    classes pulled in through a changed workflow/config consumer can be dropped.
    Fail closed only when an undiscovered proposed class is declared by a C# file
    that actually changed, because that edit could have introduced new tests.
    """
    discovered = [
        name for name in classes
        if any(test.startswith(name + ".") for test in test_names)
    ]
    undiscovered = set(classes) - set(discovered)
    if not undiscovered:
        return discovered

    changed_declared = set()
    for path in changed_paths:
        if not path.endswith((".cs", ".csproj", ".props", ".targets")):
            continue
        if not path.endswith(".cs"):
            # Project/build graph edits can alter discovery globally.
            return None
        source_path = Path(path)
        if source_revision is not None:
            try:
                source = git_show_file(source_revision, path)
            except Exception:
                return None
            changed_declared.update(_step5_source_classes(source))
            continue
        if not source_path.is_file():
            # Added/deleted/renamed C# source cannot be narrowed from prior
            # discovery alone.
            return None
        changed_declared.update(_step5_source_classes(source_path.read_text()))

    if undiscovered & changed_declared:
        return None
    return discovered


def _step5_cached_decision(current_sha: str, base_sha: str, resume_cache: str):
    """Resolve PR-local child evidence through the same canonical Step 5 authority."""
    capsule = Path(resume_cache)
    required = (
        capsule / "metadata.json",
        capsule / "candidate.trx",
        capsule / "baseline.trx",
    )
    if not all(path.is_file() for path in required):
        return None, "resume_cache_incomplete"

    try:
        metadata = json.loads((capsule / "metadata.json").read_text())
    except Exception:
        return None, "resume_cache_metadata_invalid"

    prior = metadata.get("headSha") or ""
    prior_base = metadata.get("baseSha") or ""
    run_id = metadata.get("runId")
    if (
        not re.fullmatch(r"[0-9a-f]{40}", prior)
        or not re.fullmatch(r"[0-9a-f]{40}", prior_base)
        or type(run_id) is not int
        or run_id < 1
    ):
        return None, "resume_cache_identity_invalid"
    if subprocess.run(
        ["git", "merge-base", "--is-ancestor", prior, current_sha],
        capture_output=True,
    ).returncode:
        return None, "resume_cache_not_ancestor"

    workflow_path = WORKFLOW_PATHS["step5-isolated-conversion-mapping-validation.yml"]
    if not _step5_jobs_unchanged(prior, workflow_path):
        return None, "resume_cache_execution_contract_changed"
    if not _step5_baseline_inputs_equivalent(prior_base, base_sha):
        return None, "resume_cache_baseline_inputs_changed"

    try:
        candidate = read_step5_results(capsule / "candidate.trx")
        read_step5_results(capsule / "baseline.trx")
    except Exception:
        return None, "resume_cache_test_evidence_invalid"

    changed = git_changed(prior, current_sha)
    classes = step5_dependency_change(prior, current_sha)
    if classes is None:
        return None, "resume_cache_unbounded_dependency_change"
    classes = _step5_discovered_repair_classes(classes, tuple(candidate), changed)
    if classes is None:
        return None, "resume_cache_test_discovery_changed"

    return {
        "schemaVersion": 4,
        "mode": "repair" if classes else "reuse",
        "priorRunId": run_id,
        "priorHeadSha": prior,
        "baselineEvidenceRunId": run_id,
        "baselineEvidenceArtifact": None,
        "repairClasses": classes,
        "repairFilter": "|".join(f"FullyQualifiedName~{name}" for name in classes) or None,
        "resumeCache": True,
        "parentSuccessReuse": False,
        "reason": "pr_local_cached_child_evidence",
    }, None


def _step5_graphql_parent_success_decision(
    repository: str,
    current_sha: str,
    base_sha: str,
    token: str,
):
    """Cheap control-only success proof using one check-rollup request.

    This is an optimization inside the canonical planner, never a second workflow
    decision implementation. It may prove exact reuse only; repair/full decisions
    continue through retained child artifacts below.
    """
    owner, name = repository.split("/", 1)
    query = r"""
    query($owner:String!,$name:String!,$oid:GitObjectID!){
      repository(owner:$owner,name:$name){
        object(oid:$oid){
          ... on Commit {
            history(first:30) {
              nodes {
                oid
                statusCheckRollup {
                  contexts(first:100) {
                    nodes {
                      __typename
                      ... on CheckRun { name conclusion detailsUrl }
                    }
                  }
                }
              }
            }
          }
        }
      }
    }"""
    payload = json.dumps({
        "query": query,
        "variables": {"owner": owner, "name": name, "oid": current_sha},
    }).encode()
    request = urllib.request.Request(
        "https://api.github.com/graphql",
        data=payload,
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
            "User-Agent": "legend-step5-parent-proof/1.0",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            body = json.load(response)
    except Exception:
        return None

    history = (((((body.get("data") or {}).get("repository") or {}).get("object") or {})
                .get("history") or {}).get("nodes", []))
    workflow_path = WORKFLOW_PATHS["step5-isolated-conversion-mapping-validation.yml"]
    for row in history:
        prior = row.get("oid") or ""
        if prior == current_sha or not re.fullmatch(r"[0-9a-f]{40}", prior):
            continue
        nodes = ((row.get("statusCheckRollup") or {}).get("contexts") or {}).get("nodes", [])
        success = next((
            check for check in nodes
            if check.get("__typename") == "CheckRun"
            and check.get("name") == "step5-validation"
            and check.get("conclusion") == "SUCCESS"
        ), None)
        if not success:
            continue
        if subprocess.run(
            ["git", "merge-base", "--is-ancestor", base_sha, prior],
            capture_output=True,
        ).returncode:
            continue
        try:
            if not _step5_jobs_unchanged(prior, workflow_path):
                continue
            if step5_dependency_change(prior, current_sha) != []:
                continue
        except Exception:
            continue
        run_id = None
        match = re.search(r"/actions/runs/(\d+)", success.get("detailsUrl") or "")
        if match:
            run_id = int(match.group(1))
        return {
            "schemaVersion": 4,
            "mode": "reuse",
            "priorRunId": run_id,
            "priorHeadSha": prior,
            "baselineEvidenceRunId": None,
            "baselineEvidenceArtifact": None,
            "repairClasses": [],
            "repairFilter": None,
            "resumeCache": False,
            "parentSuccessReuse": True,
            "reason": "graphql_parent_success_dependency_equivalent",
        }
    return None


def compute_step5_decision(
    repository: str,
    current_sha: str,
    base_sha: str,
    current_run_id: int,
    head_branch: str,
    resume_cache: str | None = None,
):
    """Choose only Step 5's cross-run comparison mode.

    Candidate full-suite evidence and approved-baseline evidence are independent
    canonical artifacts. A final-comparison repair therefore never requires both
    artifacts to have originated from the same historical run. Bounded dependency
    changes replace only affected test-class rows; all other child rows survive
    comparison failures. A missing baseline is scheduled independently.
    """
    decision = {
        "schemaVersion": 3,
        "mode": "full",
        "priorRunId": None,
        "priorHeadSha": None,
        "baselineEvidenceRunId": None,
        "baselineEvidenceArtifact": None,
        "repairClasses": [],
        "repairFilter": None,
    }
    cache_fallback_reason = None
    if resume_cache:
        cached, cache_fallback_reason = _step5_cached_decision(
            current_sha,
            base_sha,
            resume_cache,
        )
        if cached:
            return cached
        decision["cacheFallbackReason"] = cache_fallback_reason

    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        decision["reason"] = "github_token_unavailable"
        return decision

    workflow_name = "step5-isolated-conversion-mapping-validation.yml"
    workflow_path = WORKFLOW_PATHS[workflow_name]

    parent_success = _step5_graphql_parent_success_decision(
        repository,
        current_sha,
        base_sha,
        token,
    )
    if parent_success:
        if cache_fallback_reason:
            parent_success["cacheFallbackReason"] = cache_fallback_reason
        return parent_success

    candidate_evidence = _step5_prior_candidate_evidence(
        repository,
        current_run_id,
        head_branch,
        token,
        current_sha,
    )
    if not candidate_evidence:
        decision["reason"] = "no_reusable_candidate_evidence"
        return decision

    prior_run_id = int(candidate_evidence["runId"])
    prior_head_sha = candidate_evidence["headSha"]
    candidate_name = candidate_evidence["artifact"]

    decision.update({
        "priorRunId": prior_run_id,
        "priorHeadSha": prior_head_sha,
    })

    # Reject incompatible candidate job definitions before any expensive
    # historical baseline scan. The baseline is an independent child and will
    # be produced by the baseline-evidence job when full proof is required.
    if not _step5_candidate_producer_compatible(prior_head_sha, workflow_path):
        decision["reason"] = "candidate_or_baseline_job_changed"
        return decision

    baseline_evidence = compute_step5_baseline_evidence(repository, base_sha)
    # Candidate proof remains valid even when the independent baseline needs
    # fresh execution. The workflow produces that missing child in this run.
    if baseline_evidence.get("repairClasses"):
        decision["baselineRepair"] = baseline_evidence
    if not baseline_evidence.get("reusable"):
        baseline_evidence = {
            "evidenceRunId": current_run_id,
            "evidenceArtifact": f"step5-baseline-{base_sha}",
        }

    baseline_run_id = int(baseline_evidence["evidenceRunId"])
    baseline_name = baseline_evidence["evidenceArtifact"]

    decision.update({
        "baselineEvidenceRunId": baseline_run_id,
        "baselineEvidenceArtifact": baseline_name,
    })
    classes = step5_dependency_change(prior_head_sha, current_sha)
    if classes is None:
        decision["reason"] = "changed_or_unproven_suite_dependencies"
        return decision
    if "testNames" in candidate_evidence:
        classes = _step5_discovered_repair_classes(
            classes, candidate_evidence["testNames"], git_changed(prior_head_sha, current_sha))
        if classes is None:
            decision["reason"] = "changed_test_discovery_requires_full_proof"
            return decision
    decision.update({
        "mode": "repair" if classes else "reuse",
        "repairClasses": classes,
        "repairFilter": "|".join(f"FullyQualifiedName~{name}" for name in classes) or None,
        "reason": "dependency_equivalent_child_evidence" if not classes else "replace_only_dependency_invalidated_classes",
    })
    return decision


def cmd_step5_decision(args):
    require_readiness(args.repository, args.current_sha)
    try:
        decision = compute_step5_decision(
            args.repository,
            args.current_sha,
            args.base_sha,
            args.current_run_id,
            args.head_branch,
            getattr(args, "resume_cache", None),
        )
    except EvidenceLookupUnavailable as exc:
        decision = {
            "schemaVersion": 3,
            "mode": "full",
            "priorRunId": None,
            "priorHeadSha": None,
            "baselineEvidenceRunId": None,
            "baselineEvidenceArtifact": None,
            "repairClasses": [],
            "repairFilter": None,
            "reason": "historical_evidence_unavailable_run_full_step5",
            "evidenceFallback": {
                "error": type(exc).__name__,
                "httpStatus": exc.code,
                "endpoint": exc.endpoint,
            },
        }
    except Exception as exc:
        _stop_unresolved_planning(args, exc)

    Path(args.output).write_text(json.dumps(decision, indent=2, sort_keys=True) + "\n")
    print(json.dumps(decision, indent=2, sort_keys=True))


def _step5_baseline_inputs_equivalent(prior_base_sha: str, current_base_sha: str) -> bool:
    """Compare only inputs that can change the full Step 5 baseline result."""
    if prior_base_sha == current_base_sha:
        return True
    return step5_dependency_change(prior_base_sha, current_base_sha, stop_on_change=True) == []


def compute_step5_baseline_evidence(repository: str, base_sha: str):
    result = {
        "schemaVersion": 3,
        "approvedBaseSha": base_sha,
        "reusable": False,
        "evidenceRunId": None,
        "evidenceArtifact": None,
        "evidenceBaseSha": None,
    }
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        result["reason"] = "github_token_unavailable"
        return result

    workflow_name = "step5-isolated-conversion-mapping-validation.yml"
    workflow_path = WORKFLOW_PATHS[workflow_name]

    bounded = None

    def accept(run, artifact_name, evidence_base_sha):
        nonlocal bounded
        run_id = int(run.get("id") or 0)
        run_head = run.get("head_sha") or ""
        if not run_id or not re.fullmatch(r"[0-9a-f]{40}", evidence_base_sha):
            return False
        if not _trusted_pr_run(repository, run, workflow_path, token):
            return False
        kind = "candidate" if artifact_name.startswith("step5-candidate-") else "baseline"
        if kind == "candidate":
            if evidence_base_sha != run_head or not _step5_candidate_producer_compatible(run_head, workflow_path):
                return False
        elif not _step5_baseline_producer_compatible(run_head, workflow_path):
            return False
        classes = step5_dependency_change(evidence_base_sha, base_sha)
        if classes is None:
            return False
        rows = _step5_artifact_results(repository, run_id, artifact_name, kind, token)
        if classes:
            classes = _step5_discovered_repair_classes(classes, tuple(rows), git_changed(evidence_base_sha, base_sha), source_revision=base_sha)
        if classes is None:
            return False
        evidence = {
            "reusable": not bool(classes),
            "evidenceRunId": run_id,
            "evidenceArtifact": artifact_name,
            "evidenceBaseSha": evidence_base_sha,
            "repairClasses": classes,
            "repairFilter": "|".join(f"FullyQualifiedName~{name}" for name in classes),
            "reason": "bounded_approved_baseline_repair" if classes else (
                "exact_approved_baseline_evidence" if evidence_base_sha == base_sha else "content_identical_step5_inputs"),
        }
        if classes:
            if bounded is None:
                bounded = evidence
            return False
        result.update(evidence)
        return True

    # Fast path: an artifact already keyed to the exact approved base.
    exact_name = f"step5-baseline-{base_sha}"
    for artifact in _artifact_rows(repository, exact_name, token):
        run_id = int((artifact.get("workflow_run") or {}).get("id") or 0)
        if not run_id:
            continue
        try:
            run = api_get(repository, f"actions/runs/{run_id}", token)
            if accept(run, exact_name, base_sha):
                return result
        except EvidenceLookupUnavailable:
            raise
        except Exception:
            continue

    # Control-only commits must not invalidate a full baseline suite. Search recent
    # completed Step 5 runs for a durable baseline artifact whose declared test
    # inputs are tree-equivalent to the current approved base.
    workflow = urllib.parse.quote(workflow_name, safe="")
    payload = api_get(
        repository,
        f"actions/workflows/{workflow}/runs?event=pull_request&status=completed&per_page=100",
        token,
    )
    runs = sorted(
        payload.get("workflow_runs", []),
        key=lambda row: (row.get("updated_at") or row.get("created_at", ""), int(row.get("id", 0))),
        reverse=True,
    )
    for run in runs:
        run_id = int(run.get("id") or 0)
        if not run_id:
            continue
        try:
            names = _run_artifact_names(repository, run_id, token)
        except EvidenceLookupUnavailable:
            raise
        except Exception:
            continue
        for artifact_name in sorted(names):
            prefix = next((value for value in ("step5-baseline-", "step5-candidate-") if artifact_name.startswith(value)), None)
            if prefix is None:
                continue
            evidence_base_sha = artifact_name[len(prefix):]
            if len(evidence_base_sha) != 40 or any(ch not in "0123456789abcdef" for ch in evidence_base_sha):
                continue
            try:
                if accept(run, artifact_name, evidence_base_sha):
                    return result
            except EvidenceLookupUnavailable:
                raise
            except Exception:
                continue

    result["reason"] = "no_content_identical_baseline_artifact"
    if bounded:
        result.update(bounded)
    return result


def cmd_step5_baseline(args):
    try:
        result = compute_step5_baseline_evidence(args.repository, args.base_sha)
    except EvidenceLookupUnavailable as exc:
        result = {
            "schemaVersion": 3,
            "approvedBaseSha": args.base_sha,
            "reusable": False,
            "evidenceRunId": None,
            "evidenceArtifact": None,
            "evidenceBaseSha": None,
            "reason": "historical_evidence_unavailable_run_fresh_baseline",
            "evidenceFallback": {
                "error": type(exc).__name__,
                "httpStatus": exc.code,
                "endpoint": exc.endpoint,
            },
        }
    except Exception as exc:
        _stop_unresolved_planning(args, exc)

    Path(args.output).write_text(json.dumps(result, indent=2, sort_keys=True) + "\n")
    print(json.dumps(result, indent=2, sort_keys=True))


def cmd_job_unchanged(args):
    prior = git_show_file(args.prior_sha, args.workflow_path)
    current = Path(args.workflow_path).read_text()
    prior_jobs = _job_blocks(prior)
    current_jobs = _job_blocks(current)
    missing = [name for name in args.job if name not in prior_jobs or name not in current_jobs]
    if missing:
        print("Missing workflow jobs: " + ", ".join(missing), file=sys.stderr)
        raise SystemExit(1)
    changed = [name for name in args.job if prior_jobs[name] != current_jobs[name]]
    if changed:
        print("Changed workflow jobs: " + ", ".join(changed), file=sys.stderr)
        raise SystemExit(1)
    print("Preserved workflow job definitions: " + ", ".join(args.job))


def cmd_verify_release_coverage(args):
    text = Path(args.workflow_path).read_text()
    verify_release_policy_coverage(args.workflow, text)
    print(f"Every named release step is classified: {args.workflow}")


def build_parser():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)

    approved_preflight = sub.add_parser("approved-head-preflight")
    approved_preflight.add_argument("--repository", required=True)
    approved_preflight.add_argument("--current-sha", required=True)
    approved_preflight.add_argument("--event", required=True)
    approved_preflight.add_argument("--output")
    approved_preflight.add_argument("--base-branch", choices=[TRUSTED_PR_BASE, FAST_TRACK_PR_BASE], default=TRUSTED_PR_BASE)
    approved_preflight.set_defaults(func=cmd_approved_head_preflight)

    plan = sub.add_parser("plan")
    plan.add_argument("--workflow", required=True)
    plan.add_argument("--current-sha", required=True)
    plan.add_argument("--current-run-id", required=True, type=int)
    plan.add_argument("--run-attempt", required=True, type=int)
    plan.add_argument("--head-branch", required=True)
    plan.add_argument("--event", required=True)
    plan.add_argument("--repository", required=True)
    plan.add_argument("--output", required=True)
    plan.add_argument("--resume-cache")
    plan.add_argument("--validation-base", choices=[TRUSTED_PR_BASE, FAST_TRACK_PR_BASE], default=TRUSTED_PR_BASE)
    plan.set_defaults(func=cmd_plan)

    cache_success = sub.add_parser("cache-success")
    cache_success.add_argument("--workflow", required=True)
    cache_success.add_argument("--head-sha", required=True)
    cache_success.add_argument("--run-id", required=True, type=int)
    cache_success.add_argument("--run-attempt", required=True, type=int)
    cache_success.add_argument("--output", required=True)
    cache_success.set_defaults(func=cmd_cache_success)

    preserved = sub.add_parser("preserved")
    preserved.add_argument("--plan", required=True)
    preserved.add_argument("--gate", required=True)
    preserved.set_defaults(func=cmd_preserved)

    gate_identity = sub.add_parser("gate-identity")
    gate_identity.add_argument("--workflow", required=True)
    gate_identity.add_argument("--revision", required=True)
    gate_identity.add_argument("--gate", required=True)
    gate_identity.add_argument("--github-output")
    gate_identity.set_defaults(func=cmd_gate_identity)

    live_state = sub.add_parser("live-state")
    live_state.add_argument("--revision", required=True)
    live_state.add_argument("--selected-targets", required=True)
    live_state.add_argument("--output", required=True)
    live_state.add_argument("--github-output")
    live_state.set_defaults(func=cmd_live_state)

    verify_live = sub.add_parser("verify-live")
    verify_live.add_argument("--revision", required=True)
    verify_live.add_argument("--selected-targets", required=True)
    verify_live.add_argument("--output", required=True)
    verify_live.add_argument("--transaction-plan", type=Path)
    verify_live.add_argument("--timeout-seconds", type=int, default=480)
    verify_live.add_argument("--poll-seconds", type=int, default=5)
    verify_live.set_defaults(func=cmd_verify_live)

    lifecycle_evidence = sub.add_parser("lifecycle-evidence")
    lifecycle_evidence.add_argument("--repository", required=True)
    lifecycle_evidence.add_argument("--output", required=True)
    lifecycle_evidence.set_defaults(func=cmd_lifecycle_evidence)

    package_canary = sub.add_parser("package-canary-plan")
    package_canary.add_argument("--repository", required=True)
    package_canary.add_argument("--current-sha", required=True)
    package_canary.add_argument("--base-sha", required=True)
    package_canary.add_argument("--current-run-id", required=True, type=int)
    package_canary.add_argument("--head-branch", required=True)
    package_canary.add_argument("--output", required=True)
    package_canary.set_defaults(func=cmd_package_canary_plan)

    component_sources = sub.add_parser('component-sources')
    component_sources.add_argument('--repository', required=True)
    component_sources.add_argument('--revision', required=True)
    component_sources.add_argument('--current-run-id', type=int, required=True)
    component_sources.add_argument('--output', required=True)
    component_sources.add_argument('--github-output')
    component_sources.set_defaults(func=cmd_component_sources)

    push_coverage = sub.add_parser('parfait-push-coverage')
    for flag in ('repository', 'revision', 'branch', 'event', 'output', 'github-output'):
        push_coverage.add_argument('--' + flag, required=True)
    push_coverage.set_defaults(func=cmd_parfait_push_coverage)

    preparation = sub.add_parser('package-preparation-plan')
    preparation.add_argument('--repository', required=True)
    preparation.add_argument('--revision', required=True)
    preparation.add_argument('--current-sha', required=True)
    preparation.add_argument('--branch', required=True)
    preparation.add_argument('--preparation-only', action='store_true')
    preparation.add_argument('--output', required=True)
    preparation.set_defaults(func=cmd_package_preparation_plan)

    package_backfill = sub.add_parser("package-backfill-plan")
    package_backfill.add_argument("--repository", required=True)
    package_backfill.add_argument("--revision", required=True)
    package_backfill.add_argument("--current-sha", required=True)
    package_backfill.add_argument("--output", required=True)
    package_backfill.set_defaults(func=cmd_package_backfill_plan)

    validated_package = sub.add_parser("validated-package")
    validated_package.add_argument("--repository", required=True)
    validated_package.add_argument("--revision", required=True)
    validated_package.add_argument("--package-identity", required=True)
    validated_package.add_argument("--output", required=True)
    validated_package.add_argument("--pinned-source-json")
    validated_package.set_defaults(func=cmd_validated_package)

    rollback_evidence = sub.add_parser("rollback-evidence")
    rollback_evidence.add_argument("--repository", required=True)
    rollback_evidence.add_argument("--revision", required=True)
    rollback_evidence.add_argument("--app", required=True)
    rollback_evidence.add_argument("--output", required=True)
    rollback_evidence.set_defaults(func=cmd_rollback_evidence)

    step5_decision = sub.add_parser("step5-decision")
    step5_decision.add_argument("--current-sha", required=True)
    step5_decision.add_argument("--base-sha", required=True)
    step5_decision.add_argument("--current-run-id", required=True, type=int)
    step5_decision.add_argument("--head-branch", required=True)
    step5_decision.add_argument("--repository", required=True)
    step5_decision.add_argument("--resume-cache")
    step5_decision.add_argument("--output", required=True)
    step5_decision.set_defaults(func=cmd_step5_decision)

    step5_baseline = sub.add_parser("step5-baseline")
    step5_baseline.add_argument("--base-sha", required=True)
    step5_baseline.add_argument("--repository", required=True)
    step5_baseline.add_argument("--output", required=True)
    step5_baseline.set_defaults(func=cmd_step5_baseline)

    merge = sub.add_parser('step5-merge')
    for flag in ('prior', 'repair', 'classes', 'output', 'source-run', 'source-artifact', 'source-revision', 'approved-revision'):
        merge.add_argument('--' + flag, required=True)
    merge.set_defaults(func=cmd_step5_merge)

    job_unchanged = sub.add_parser("job-unchanged")
    job_unchanged.add_argument("--workflow-path", required=True)
    job_unchanged.add_argument("--prior-sha", required=True)
    job_unchanged.add_argument("--job", action="append", required=True)
    job_unchanged.set_defaults(func=cmd_job_unchanged)

    record = sub.add_parser("record-evidence")
    record.add_argument("--plan", required=True)
    record.add_argument("--output", required=True)
    record.add_argument("--repository", required=True)
    record.add_argument("--run-id", required=True, type=int)
    record.add_argument("--cache-output")
    record.set_defaults(func=cmd_record_evidence)

    coverage = sub.add_parser("verify-release-coverage")
    coverage.add_argument("--workflow", required=True)
    coverage.add_argument("--workflow-path", required=True)
    coverage.set_defaults(func=cmd_verify_release_coverage)
    return parser


def main():
    args = build_parser().parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
