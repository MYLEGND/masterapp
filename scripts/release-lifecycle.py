#!/usr/bin/env python3
"""Own approved-branch integration, direct-release recovery, and lossless branch retirement.

Runs only trusted default-branch code. Never executes source-branch code. The
protected approved branch is the sole Git release authority; immutable release
receipts and live provenance replace the retired production-branch promotion path.
"""
import argparse
import ast
import base64
import importlib.util
import hashlib
import tempfile
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.error
import urllib.parse
import urllib.request
import sys
sys.path.insert(0, str(Path(__file__).resolve().parent))
from release_policy import staging_only

SHA = re.compile(r'^[0-9a-f]{40}$')


def _validation_authority_module():
    path = Path(__file__).with_name("validation-resume.py")
    spec = importlib.util.spec_from_file_location("validation_resume_authority", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

VALIDATION_AUTHORITY = _validation_authority_module()


def _release_package_module():
    path = Path(__file__).with_name("release-package.py")
    spec = importlib.util.spec_from_file_location("release_package_authority", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


PACKAGE_AUTHORITY = _release_package_module()
APPROVED = VALIDATION_AUTHORITY.TRUSTED_PR_BASE
DIRECT = VALIDATION_AUTHORITY.DIRECT_RELEASE_WORKFLOW
PACKAGE_VALIDATION = VALIDATION_AUTHORITY.PACKAGE_VALIDATION_WORKFLOW
KEEP = {APPROVED}
RELEASE_QUEUE_CONTEXT = 'legend-release-queue'
RELEASE_QUEUE_REQUEST_CONTEXT = 'legend-release-queue-request'
RELEASE_QUEUE_OWNER = re.compile(r'^owner-pr=([0-9]+) validation-to-production
def git(*args, check=True):
    return subprocess.run(['git', *args], check=check, text=True, capture_output=True)


def ancestor(before, after):
    result = git('merge-base', '--is-ancestor', before, after, check=False)
    if result.returncode not in (0, 1):
        raise RuntimeError(result.stderr)
    return result.returncode == 0


def eligible(branch, approved, live, open_refs, active_refs, failed_refs):
    name, sha = branch['name'], branch['commit']['sha']
    if name in KEEP or branch.get('protected'):
        return False, 'canonical/protected branch'
    if name in open_refs:
        return False, 'open pull request uses branch as source or base'
    if name in active_refs:
        return False, 'workflow still running'
    if name in failed_refs:
        return False, 'latest branch workflow failed or was cancelled'
    if not ancestor(sha, approved):
        return False, 'unique history not preserved in approved changes'
    if not live or not all(ancestor(sha, row['revision']) for row in live):
        return False, 'not covered by every live web application revision'
    return True, 'preserved in approved changes and every live web revision'


class GitHub:
    def __init__(self):
        self.repo = os.environ['GITHUB_REPOSITORY']
        self.token = os.environ['GH_TOKEN']
        self.root = 'https://api.github.com/repos/' + self.repo

    def api(self, path, data=None, method=None):
        payload = json.dumps(data).encode() if data is not None else None
        request = urllib.request.Request(self.root + '/' + path, data=payload,
            method=method or ('POST' if payload is not None else 'GET'), headers={
                'Authorization': 'Bearer ' + self.token,
                'Accept': 'application/vnd.github+json',
                'Content-Type': 'application/json', 'User-Agent': 'legend-release-lifecycle'})
        try:
            with urllib.request.urlopen(request, timeout=45) as response:
                body = response.read()
                return json.loads(body) if body else None
        except urllib.error.HTTPError as error:
            # Do not log tokens, response bodies or environment dumps.
            raise RuntimeError(f'GitHub {request.method} {path}: HTTP {error.code}') from None

    def pages(self, path, key=None):
        rows = []
        for page in range(1, 101):
            result = self.api(path + ('&' if '?' in path else '?') + f'per_page=100&page={page}')
            values = result[key] if key else result
            rows.extend(values)
            if len(values) < 100:
                return rows
        raise RuntimeError('Pagination limit reached; refusing incomplete branch evidence')

    def ref(self, name):
        branch = self.api('branches/' + urllib.parse.quote(name, safe=''))
        value = (branch.get('commit') or {}).get('sha')
        if not SHA.fullmatch(value or ''):
            raise RuntimeError('Malformed branch revision')
        return value

    def dispatch(self, workflow, inputs=None):
        self.api('actions/workflows/' + workflow + '/dispatches',
                 {'ref': APPROVED, 'inputs': inputs or {}})

    def text(self, revision, path):
        row = self.api(
            'contents/' + urllib.parse.quote(path, safe='/') +
            '?ref=' + urllib.parse.quote(revision, safe='')
        )
        if row.get('encoding') != 'base64' or not isinstance(row.get('content'), str):
            raise RuntimeError('Candidate control-plane source is unavailable')
        try:
            return base64.b64decode(row['content'], validate=False).decode('utf-8')
        except (ValueError, UnicodeError):
            raise RuntimeError('Candidate control-plane source is malformed') from None

    def context_status(self, revision, context, state, description):
        if (not SHA.fullmatch(revision or '')
            or not isinstance(context, str) or not context
            or state not in {'pending', 'success', 'failure', 'error'}):
            raise ValueError('Malformed trusted validation status')
        self.api(
            'statuses/' + revision,
            {
                'state': state,
                'context': context,
                'description': description[:140],
            },
            method='POST',
        )

    def status(self, revision, state, description):
        self.context_status(revision, 'architecture-validation', state, description)


def live_revisions():
    spec = importlib.util.spec_from_file_location('release_baseline', Path(__file__).with_name('approved-release-baseline.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    # A missing/unreachable app blocks deletion of ALL branches, not just that app.
    return [module.observe(target) for target in module.TARGETS]


def ready(pr, repo, base):
    return (pr['state'] == 'open' and not pr['draft'] and pr['base']['ref'] == base
        and pr['head']['repo'] and pr['head']['repo']['full_name'] == repo
        and pr['head']['ref'] not in KEEP
        and pr['author_association'] in {'OWNER', 'MEMBER', 'COLLABORATOR'})



def approved_head_state(api, pr):
    """Return whether a PR head already contains the exact current approved head."""
    approved = api.ref(APPROVED)
    head = (pr.get("head") or {}).get("sha")
    if not SHA.fullmatch(head or ""):
        raise RuntimeError("Malformed candidate revision")
    if head == approved:
        return {
            "current": True,
            "approved": approved,
            "candidate": head,
            "mergeBase": approved,
            "status": "identical",
        }
    compare = api.api(
        "compare/" + urllib.parse.quote(approved, safe="") + "..." +
        urllib.parse.quote(head, safe="")
    )
    merge_base = (compare.get("merge_base_commit") or {}).get("sha")
    status = compare.get("status")
    return {
        "current": merge_base == approved and status in {"ahead", "identical"},
        "approved": approved,
        "candidate": head,
        "mergeBase": merge_base,
        "status": status,
    }


def sync_candidate_to_current_approved(api, pr):
    """Fast-forward a trusted same-repo candidate by merging approved into it.

    The operation is additive only: no reset, rebase, force-push, or source
    rewrite. A new PR head causes normal exact-head validation to restart.
    """
    state = approved_head_state(api, pr)
    if state["current"]:
        return None
    if not ready(pr, api.repo, APPROVED):
        return {
            "state": "BASE_SYNC_REQUIRED",
            "retained": "Candidate is stale but is not eligible for trusted automatic base sync",
            "pr": pr.get("number"),
            **state,
        }
    try:
        result = api.api(
            "merges",
            {
                "base": pr["head"]["ref"],
                "head": state["approved"],
                "commit_message": (
                    f"Sync current {APPROVED} into PR #{pr['number']} before validation"
                ),
            },
            method="POST",
        )
    except RuntimeError as exc:
        if any(f"HTTP {code}" in str(exc) for code in (409, 422)):
            return {
                "state": "BASE_SYNC_REQUIRED",
                "retained": "Current approved head could not be merged cleanly into candidate",
                "pr": pr["number"],
                **state,
            }
        raise
    fresh = api.api(f"pulls/{pr['number']}")
    synced = (fresh.get("head") or {}).get("sha")
    if not SHA.fullmatch(synced or ""):
        raise RuntimeError("Approved-head synchronization did not produce a candidate revision")
    return {
        "state": "BASE_SYNCED",
        "pr": pr["number"],
        "previousHead": state["candidate"],
        "approvedHead": state["approved"],
        "head": synced,
        "validation": "new synchronize event must validate the synced exact head",
    }



def _latest_commit_status(api, revision, context):
    payload = api.api('commits/' + revision + '/status')
    rows = payload.get('statuses') or []
    matches = [row for row in rows if row.get('context') == context]
    if not matches:
        return None
    return max(matches, key=lambda row: (
        row.get('updated_at') or row.get('created_at') or '',
        int(row.get('id') or 0),
    ))


def release_queue_lease(api):
    approved = api.ref(APPROVED)
    status = _latest_commit_status(api, approved, RELEASE_QUEUE_CONTEXT)
    if not status or status.get('state') != 'pending':
        return {'approved': approved, 'ownerPr': None, 'status': status}
    match = RELEASE_QUEUE_OWNER.fullmatch(status.get('description') or '')
    if match is None:
        raise RuntimeError('Release queue lease on approved head is malformed')
    return {'approved': approved, 'ownerPr': int(match.group(1)), 'status': status}


def _request_release_queue(api, pr):
    head = pr.get('head', {}).get('sha')
    if not SHA.fullmatch(head or ''):
        raise RuntimeError('Release queue request has malformed candidate head')
    api.context_status(
        head,
        RELEASE_QUEUE_REQUEST_CONTEXT,
        'success',
        f"requested-pr={pr['number']}",
    )


def claim_release_queue(api, pr):
    """Acquire one validation-to-production lease; every later PR stays queued."""
    _request_release_queue(api, pr)
    lease = release_queue_lease(api)
    owner = lease['ownerPr']
    if owner is not None:
        if owner == pr['number']:
            return {
                'state': 'RELEASE_QUEUE_OWNER',
                'pr': pr['number'],
                'approved': lease['approved'],
            }
        return {
            'state': 'RELEASE_QUEUED',
            'retained': f"Queued behind active release PR #{owner}",
            'pr': pr['number'],
            'ownerPr': owner,
        }

    active = [row for row in direct_release_runs(api) if row.get('status') != 'completed']
    if active:
        return {
            'state': 'RELEASE_QUEUED',
            'retained': 'Queued until the active direct release reaches terminal provenance',
            'pr': pr['number'],
            'blockingRuns': [row['id'] for row in active],
        }

    api.context_status(
        lease['approved'],
        RELEASE_QUEUE_CONTEXT,
        'pending',
        f"owner-pr={pr['number']} validation-to-production",
    )
    return {
        'state': 'RELEASE_QUEUE_OWNER',
        'pr': pr['number'],
        'approved': lease['approved'],
    }


def _requested_release_queue(api):
    pulls = api.pages('pulls?state=open&base=' + urllib.parse.quote(APPROVED, safe=''))
    queued = []
    for pr in pulls:
        if not ready(pr, api.repo, APPROVED):
            continue
        head = pr.get('head', {}).get('sha')
        if not SHA.fullmatch(head or ''):
            continue
        status = _latest_commit_status(api, head, RELEASE_QUEUE_REQUEST_CONTEXT)
        match = RELEASE_QUEUE_REQUEST.fullmatch((status or {}).get('description') or '')
        if ((status or {}).get('state') == 'success'
            and match is not None
            and int(match.group(1)) == pr['number']):
            queued.append(pr)
    return sorted(queued, key=lambda row: row['number'])


def _rerun_required_validations(api, pr):
    files = api.pages(f"pulls/{pr['number']}/files")
    names = [row.get('filename') for row in files if row.get('filename')]
    required = set(VALIDATION_AUTHORITY.required_validation_topology(names)['required'])
    runs = api.pages('actions/runs?head_sha=' + pr['head']['sha'], 'workflow_runs')
    latest = {}
    for run in sorted(
        runs,
        key=lambda row: (row.get('created_at', ''), row.get('id', 0)),
        reverse=True,
    ):
        path = run.get('path', '').split('@')[0]
        if (run.get('head_sha') == pr['head']['sha']
            and run.get('event') == 'pull_request'
            and path in required):
            latest.setdefault(path, run)
    rerun = []
    missing = []
    for path in sorted(required):
        run = latest.get(path)
        if run is None:
            missing.append(path)
            continue
        if run.get('status') == 'completed' and run.get('conclusion') != 'success':
            api.api(f"actions/runs/{run['id']}/rerun", {}, method='POST')
            rerun.append(run['id'])
    return {'rerun': rerun, 'missing': missing}


def promote_next_release_queue(api):
    lease = release_queue_lease(api)
    if lease['ownerPr'] is not None:
        return None
    if any(row.get('status') != 'completed' for row in direct_release_runs(api)):
        return {'state': 'RELEASE_QUEUE_WAITING', 'retained': 'Active direct release still owns publication'}
    candidates = _requested_release_queue(api)
    if not candidates:
        return None
    pr = api.api(f"pulls/{candidates[0]['number']}")
    if not ready(pr, api.repo, APPROVED):
        return None
    approved = api.ref(APPROVED)
    api.context_status(
        approved,
        RELEASE_QUEUE_CONTEXT,
        'pending',
        f"owner-pr={pr['number']} validation-to-production",
    )
    synced = sync_candidate_to_current_approved(api, pr)
    if synced is not None:
        return {**synced, 'queueOwnerPr': pr['number']}
    wake = _rerun_required_validations(api, pr)
    return {
        'state': 'RELEASE_QUEUE_PROMOTED',
        'pr': pr['number'],
        'head': pr['head']['sha'],
        'rerun': wake['rerun'],
        'missingValidationRuns': wake['missing'],
    }


def _release_queue_guard(api, pr):
    lease = release_queue_lease(api)
    if lease['ownerPr'] != pr['number']:
        return {
            'state': 'RELEASE_QUEUED',
            'retained': (
                f"Queued behind active release PR #{lease['ownerPr']}"
                if lease['ownerPr'] is not None
                else 'Candidate has not acquired the validation-to-production release lease'
            ),
            'pr': pr['number'],
            'ownerPr': lease['ownerPr'],
        }
    return None


def _carry_release_queue(api, revision, pr_number):
    api.context_status(
        revision,
        RELEASE_QUEUE_CONTEXT,
        'pending',
        f"owner-pr={pr_number} validation-to-production",
    )


def _release_release_queue(api, revision, pr_number, reason):
    api.context_status(
        revision,
        RELEASE_QUEUE_CONTEXT,
        'success',
        f"released-pr={pr_number} {reason}"[:140],
    )


def _assignment_strings(tree, name):
    for node in tree.body:
        if isinstance(node, ast.Assign) and any(
            isinstance(target, ast.Name) and target.id == name
            for target in node.targets
        ):
            if isinstance(node.value, (ast.Tuple, ast.List, ast.Set)):
                return {
                    item.value for item in node.value.elts
                    if isinstance(item, ast.Constant) and isinstance(item.value, str)
                }
    return set()


def _function_source(source, tree, name):
    for node in tree.body:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name == name:
            return ast.get_source_segment(source, node) or ''
    return ''


def repository_ruleset_integrity(api):
    """The protected branch must retain the external safety rails the lifecycle assumes."""
    rows = api.api('rulesets')
    if not isinstance(rows, list):
        return 'Repository ruleset inventory unavailable'
    for row in rows:
        if row.get('enforcement') != 'active' or row.get('target') != 'branch':
            continue
        ruleset_id = row.get('id')
        if type(ruleset_id) is not int:
            continue
        detail = api.api(f'rulesets/{ruleset_id}')
        refs = (detail.get('conditions') or {}).get('ref_name') or {}
        if 'refs/heads/' + APPROVED not in (refs.get('include') or []):
            continue
        if detail.get('bypass_actors'):
            return 'Protected approved branch gained a ruleset bypass actor'
        if detail.get('current_user_can_bypass') not in {None, 'never'}:
            return 'Protected approved branch permits ruleset bypass'
        rules = {rule.get('type'): rule for rule in detail.get('rules') or []}
        required_types = {'deletion', 'non_fast_forward', 'pull_request', 'required_status_checks'}
        missing = required_types - set(rules)
        if missing:
            return 'Protected approved branch ruleset lost: ' + ', '.join(sorted(missing))
        checks = (rules['required_status_checks'].get('parameters') or {})
        contexts = {
            row.get('context') for row in checks.get('required_status_checks') or []
            if row.get('context')
        }
        if checks.get('strict_required_status_checks_policy') is not True:
            return 'Protected approved branch no longer requires strict up-to-date checks'
        if 'architecture-validation' not in contexts:
            return 'Protected approved branch lost trusted architecture-validation requirement'
        merge = rules['pull_request'].get('parameters') or {}
        if merge.get('allowed_merge_methods') != ['merge']:
            return 'Protected approved branch merge method drifted from canonical merge-only policy'
        return None
    return 'Active approved-branch protection ruleset is missing'


def candidate_control_plane_integrity(api, pr, names):
    """Read candidate control files as data from trusted base code; never execute them."""
    settings = repository_ruleset_integrity(api)
    if settings:
        return settings
    if not any(VALIDATION_AUTHORITY.release_control_authority_path(name) for name in names):
        return None

    head = pr['head']['sha']
    paths = {
        'validation': 'scripts/validation-resume.py',
        'lifecycle': 'scripts/release-lifecycle.py',
        'lifecycle_workflow': '.github/workflows/legend-release-lifecycle.yml',
        'direct_workflow': '.github/workflows/all-intentional-direct-release-20260918.yml',
        'architecture_workflow': '.github/workflows/masterapp-platform-architecture-validation.yml',
        'step5_workflow': '.github/workflows/step5-isolated-conversion-mapping-validation.yml',
        'step6_workflow': '.github/workflows/step6-openai-ads-execution-validation.yml',
        'step78_workflow': '.github/workflows/steps7-8-governed-advertising-validation.yml',
        'security_workflow': '.github/workflows/approved-release-security-validation.yml',
    }
    try:
        source = {key: api.text(head, path) for key, path in paths.items()}
        validation_tree = ast.parse(source['validation'])
        lifecycle_tree = ast.parse(source['lifecycle'])
    except (RuntimeError, SyntaxError):
        return 'Candidate release-control authority cannot be parsed from exact head'

    assignments = {}
    for node in validation_tree.body:
        if isinstance(node, ast.Assign):
            for target in node.targets:
                if isinstance(target, ast.Name) and isinstance(node.value, ast.Constant):
                    assignments[target.id] = node.value.value
    if assignments.get('TRUSTED_PR_BASE') != APPROVED:
        return 'Candidate changed the sole approved release branch authority'
    if assignments.get('DIRECT_RELEASE_WORKFLOW') != DIRECT:
        return 'Candidate changed the sole approved direct-release workflow authority'

    candidate_paths = _assignment_strings(validation_tree, 'LIFECYCLE_AUTHORITY_PATHS')
    required_paths = set(VALIDATION_AUTHORITY.LIFECYCLE_AUTHORITY_PATHS)
    if not required_paths <= candidate_paths:
        return 'Candidate removed protected lifecycle authority paths: ' + ', '.join(sorted(required_paths - candidate_paths))

    predicate = _function_source(source['validation'], validation_tree, 'release_control_authority_path')
    if not all(token in predicate for token in (
        'LIFECYCLE_AUTHORITY_PATHS', 'PACKAGE_AUTHORITY_PATHS', 'RELEASE_EXECUTION_CONTROL_INPUTS'
    )):
        return 'Candidate weakened canonical release-control authority classification'

    topology = _function_source(source['validation'], validation_tree, 'required_validation_topology')
    if not all(token in topology for token in (
        'release_control_authority_change',
        'release_control_authority_path',
        'required.add(security)',
    )):
        return 'Candidate release-control changes no longer require canonical security validation'

    candidate_validation_source = _function_source(source['lifecycle'], lifecycle_tree, 'candidate_validation')
    if not all(token in candidate_validation_source for token in (
        'VALIDATION_AUTHORITY.required_validation_topology(names)',
        "run.get('event') != 'pull_request'",
        "latest[path].get('status') != 'completed'",
        "latest[path].get('conclusion') != 'success'",
    )):
        return 'Candidate weakened exact-head merge validation'

    guard_source = _function_source(source['lifecycle'], lifecycle_tree, 'candidate_control_plane_integrity')
    sync_source = _function_source(source['lifecycle'], lifecycle_tree, 'sync_candidate_to_current_approved')
    merge_source = _function_source(source['lifecycle'], lifecycle_tree, 'merge_validated')
    if not guard_source or 'candidate_control_plane_integrity(api, pr, names)' not in merge_source:
        return 'Candidate removed trusted control-plane integrity enforcement'
    if merge_source.find('candidate_control_plane_integrity(api, pr, names)') > merge_source.find("pulls/{pr['number']}/merge"):
        return 'Candidate moved control-plane integrity enforcement after merge'
    if not sync_source or not all(token in sync_source for token in (
        'approved_head_state(api, pr)',
        '"merges"',
        '"base": pr["head"]["ref"]',
        '"head": state["approved"]',
        '"state": "BASE_SYNCED"',
    )):
        return 'Candidate weakened automatic current-approved-head synchronization'
    if 'base_state = approved_head_state(api, pr)' not in merge_source:
        return 'Candidate removed final approved-head freshness guard before merge'

    lifecycle_workflow = source['lifecycle_workflow']
    if not all(token in lifecycle_workflow for token in (
        'pull_request_target:',
        'ref: legend/approved-changes',
        'contents: write',
        'pull-requests: write',
        'actions: write',
        'statuses: write',
        'python3 scripts/release-lifecycle.py integrate --pr "$PR_NUMBER"',
    )):
        return 'Candidate weakened trusted protected-branch lifecycle execution'

    direct_workflow = source['direct_workflow']
    for token in (
        'cancel-in-progress: false',
        "if: github.ref == 'refs/heads/legend/approved-changes'",
        'Verify selected authority belongs to protected event history',
        'Prepare complete immutable release transaction',
        'Reconcile complete immutable release transaction',
        'Verify every deployed target and collect all failures',
        'Enforce complete direct deployment outcome',
        'Retain exact approved release receipt',
        'Reconcile terminal release resource disposition',
        'Preserve terminal release resource disposition',
        'release-state-receipt:',
    ):
        if token not in direct_workflow:
            return 'Candidate direct-release workflow lost required invariant: ' + token

    architecture = source['architecture_workflow']
    if 'name: architecture-validation' not in architecture and 'name: candidate-architecture-validation' not in architecture:
        return 'Candidate architecture workflow lost its canonical validation job'
    if 'Run branch lifecycle safety contracts' not in architecture:
        return 'Candidate architecture workflow stopped exercising lifecycle contracts'

    validation_workflows = {
        'architecture': architecture,
        'step5': source['step5_workflow'],
        'step6': source['step6_workflow'],
        'step78': source['step78_workflow'],
        'security': source['security_workflow'],
    }
    for label, workflow in validation_workflows.items():
        if (
            'approved-head-preflight:' not in workflow
            or 'Verify candidate contains current approved head' not in workflow
            or 'ref: ${{ github.event.pull_request.head.sha || github.sha }}' not in workflow
            or 'persist-credentials: false' not in workflow
            or 'approved-head-preflight \\' not in workflow
            or 'needs: approved-head-preflight' not in workflow
        ):
            return f'Candidate {label} validator lost canonical approved-head preflight'
    if architecture.count('needs: approved-head-preflight') < 3:
        return 'Candidate architecture validator allows package/probe work before approved-head preflight'

    security_trigger = source['security_workflow'].split('concurrency:', 1)[0]
    if 'pull_request:' not in security_trigger or 'branches: [legend/approved-changes]' not in security_trigger:
        return 'Candidate security validation no longer covers approved-branch pull requests'
    if '\n    paths:' in security_trigger or '\n    paths-ignore:' in security_trigger:
        return 'Candidate security validation can be skipped by release-control path filtering'
    return None


def publish_trusted_validation_status(api, revision, state, detail):
    descriptions = {
        'pending': 'Trusted release authority is waiting for exact-head validation',
        'success': 'Trusted release authority and exact-head validation passed',
        'failure': 'Trusted release authority blocked unsafe control-plane drift',
    }
    api.status(revision, state, descriptions[state] if not detail else detail)


def candidate_validation(api, pr):
    """Require every workflow selected by the canonical topology to be green.

    Child-level preservation belongs to validation-resume.py. Lifecycle never
    reinterprets a failed parent workflow, carries a second step list, or accepts
    partial validation as merge-ready.
    """
    head = pr['head']['sha']
    runs = api.pages('actions/runs?head_sha=' + head, 'workflow_runs')
    latest = {}
    for run in sorted(
        runs,
        key=lambda row: (row.get('created_at', ''), row.get('id', 0)),
        reverse=True,
    ):
        if run.get('head_sha') != head or run.get('event') != 'pull_request':
            continue
        latest.setdefault(run['path'].split('@')[0], run)

    files = api.pages(f"pulls/{pr['number']}/files")
    names = [row['filename'] for row in files if row.get('filename')]
    required = VALIDATION_AUTHORITY.required_validation_topology(names)['required']

    failed = [
        path for path in required
        if path not in latest
        or latest[path].get('status') != 'completed'
        or latest[path].get('conclusion') != 'success'
    ]
    if not failed:
        return None
    return 'Awaiting successful exact-head validation: ' + ', '.join(sorted(failed))


def automatic_release_targets(api, pr):
    files = api.pages(f"pulls/{pr['number']}/files")
    names = [row.get('filename') for row in files if row.get('filename')]
    return VALIDATION_AUTHORITY.release_targets_for_paths(names)


def automatic_release_inputs(pr, release_sha, targets, *, source_merge_sha=None):
    return {
        'automatic': 'true',
        'source_pr': str(pr['number']),
        'validated_sha': pr['head']['sha'],
        'source_merge_sha': source_merge_sha or release_sha,
        'merge_sha': release_sha,
        'targets_json': json.dumps(list(targets), separators=(',', ':')),
    }


def merge_validated(api, pr):
    queued = _release_queue_guard(api, pr)
    if queued:
        return queued
    base_state = approved_head_state(api, pr)
    if not base_state["current"]:
        publish_trusted_validation_status(
            api, pr["head"]["sha"], "pending",
            "Candidate must contain the current approved head before validation can authorize merge",
        )
        return {
            "state": "BASE_SYNC_REQUIRED",
            "retained": "Candidate does not contain current approved head",
            "pr": pr["number"],
            **base_state,
        }

    files = api.pages(f"pulls/{pr['number']}/files")
    names = [row.get('filename') for row in files if row.get('filename')]
    head = pr['head']['sha']

    integrity = candidate_control_plane_integrity(api, pr, names)
    if integrity:
        publish_trusted_validation_status(api, head, 'failure', integrity)
        return {'state': 'VALIDATING', 'retained': integrity}

    pending = candidate_validation(api, pr)
    if pending:
        publish_trusted_validation_status(api, head, 'pending', '')
        return {'state': 'VALIDATING', 'retained': pending}

    publish_trusted_validation_status(api, head, 'success', '')
    targets = VALIDATION_AUTHORITY.release_targets_for_paths(names)
    control_only = bool(names) and all(
        VALIDATION_AUTHORITY.release_control_only_path(name)
        for name in names
    )
    try:
        result = api.api(f"pulls/{pr['number']}/merge",
            {'merge_method': 'merge', 'sha': pr['head']['sha']}, method='PUT')
    except RuntimeError as exc:
        if any(f"HTTP {code}" in str(exc) for code in (405, 409, 422)):
            return {
                'retained': 'Validated PR is not currently mergeable; source branch retained',
                'pr': pr['number'],
            }
        raise
    if not result.get('merged'):
        return {
            'retained': 'Merge did not complete; source branch retained',
            'pr': pr['number'],
        }

    _carry_release_queue(api, result['sha'], pr['number'])

    # Validation success is the publication handoff. Application-affecting merges
    # immediately enter the sole direct-release workflow with scope derived from
    # the validated PR. No second authorization command or hand-maintained target
    # table exists between merge and deployment.
    release_result = None
    if targets and not control_only:
        # Integration and dispatch are separate phases of the same serialized
        # lifecycle invocation. The refresh/reconcile phase dispatches once;
        # do not dispatch here then rediscover an eventually-visible run below.
        release_result = {'releaseRecovery': 'queued for the refreshed reconciliation phase', 'targets': list(targets)}
    else:
        # The merge commit does not exist in this runner's local checkout yet.
        # Historical recovery is intentionally deferred to the workflow's
        # refresh -> reconcile phase, which fetches and resets to the newly
        # approved commit before inspecting first-parent authorization history.
        release_result = {
            'releaseRecovery': 'deferred until refreshed approved checkout',
        }

    if any(row.get('filename') == '.github/workflows/deployment-diagnostics.yml' for row in files):
        api.dispatch('deployment-diagnostics.yml')
    return {
        'state': 'MERGED',
        'transitions': ['MERGE_READY', 'MERGED'],
        'mergedPr': pr['number'],
        'sha': result['sha'],
        'releaseDispatched': bool(release_result and 'directRelease' in release_result),
        'automaticRelease': bool(targets and not control_only),
        'targets': list(targets),
        'release': release_result,
    }


def integrate(api, number):
    if staging_only():
        return {'retained': 'Validation-only staging hold; no integration, dispatch or cleanup'}
    pr = api.api(f'pulls/{number}')

    # pull_request_target events can queue behind another lifecycle run. If that
    # earlier run already merged this exact trusted PR, the delayed event is a
    # replay, not a new integration failure. Prove the recorded merge is in the
    # current approved lineage and return without dispatching anything again.
    merged_sha = pr.get('merge_commit_sha')
    already_integrated = (
        pr.get('state') == 'closed'
        and pr.get('merged_at')
        and pr.get('base', {}).get('ref') == APPROVED
        and pr.get('head', {}).get('repo')
        and pr['head']['repo'].get('full_name') == api.repo
        and pr.get('author_association') in {'OWNER', 'MEMBER', 'COLLABORATOR'}
        and SHA.fullmatch(merged_sha or '')
    )
    if already_integrated:
        approved = api.ref(APPROVED)
        if ancestor(merged_sha, approved):
            return {
                'integration': 'already merged exact PR event preserved as no-op',
                'mergedPr': number,
                'sha': merged_sha,
                'replayed': True,
                'releaseDispatched': False,
            }

    if not ready(pr, api.repo, APPROVED):
        raise RuntimeError('Only ready, same-repository collaborator PRs into approved changes can be integrated')
    queue = claim_release_queue(api, pr)
    if queue['state'] != 'RELEASE_QUEUE_OWNER':
        return queue
    synced = sync_candidate_to_current_approved(api, pr)
    if synced is not None:
        return {**synced, 'queueOwnerPr': pr['number']}
    return merge_validated(api, pr)


def pending_updates(api):
    if staging_only():
        return {'retained': 'Validation-only staging hold; no integration, dispatch or cleanup'}
    # GitHub state may advance while a serialized lifecycle run is waiting.
    # Refresh the canonical approved ref before any ancestry decision so a newly
    # merged trusted PR can never appear as an unknown local commit.
    refreshed = git('fetch', '--no-tags', '--prune', 'origin',
                    f'+refs/heads/{APPROVED}:refs/remotes/origin/{APPROVED}', check=False)
    if refreshed.returncode:
        raise RuntimeError(refreshed.stderr)

    lease = release_queue_lease(api)
    owner = lease['ownerPr']
    if owner is not None:
        current = api.api(f"pulls/{owner}")
        if current.get('state') == 'open':
            if not ready(current, api.repo, APPROVED):
                _release_release_queue(api, lease['approved'], owner, 'owner-no-longer-ready')
                promoted = promote_next_release_queue(api)
                return promoted or {
                    'state': 'RELEASE_QUEUE_READY',
                    'retained': 'Previous queue owner is no longer ready; queue released',
                }
            synced = sync_candidate_to_current_approved(api, current)
            if synced is not None:
                return {**synced, 'queueOwnerPr': owner}
            return merge_validated(api, current)
        if current.get('merged_at'):
            return {
                'state': 'RELEASE_QUEUE_WAITING_FOR_PRODUCTION',
                'pr': owner,
                'retained': 'Merged queue owner retains lease until terminal production provenance',
            }
        _release_release_queue(api, lease['approved'], owner, 'owner-closed-without-merge')
        promoted = promote_next_release_queue(api)
        return promoted or {
            'state': 'RELEASE_QUEUE_READY',
            'retained': 'Closed queue owner released without merge',
        }

    promoted = promote_next_release_queue(api)
    if promoted:
        return promoted

    # Scheduled reconciliation also covers bot-created PR events and corrections
    # pushed to a retained branch after its previous approved PR was merged.
    #
    # A retained/unvalidated PR must never starve another fully validated PR.
    # Preserve its reason and continue scanning; stop only after a mutation
    # actually succeeds.
    pulls = api.pages('pulls?state=open&base=' + urllib.parse.quote(APPROVED, safe=''))
    closed = api.pages('pulls?state=closed&base=' + urllib.parse.quote(APPROVED, safe=''))
    retained_candidates = []
    for pr in reversed(pulls):
        if ready(pr, api.repo, APPROVED):
            retained_candidates.append({
                'pr': pr['number'],
                'reason': 'Ready PR is waiting for canonical release-queue admission',
            })
            continue
        if (pr['state'] == 'open' and not pr['draft'] and pr['user']['login'] == 'github-actions[bot]'
            and pr['head']['repo'] and pr['head']['repo']['full_name'] == api.repo
            and pr['head']['ref'] not in KEEP):
            prior = [old for old in closed if old.get('merged_at') and
                old['author_association'] in {'OWNER', 'MEMBER', 'COLLABORATOR'} and
                old['head']['repo'] and old['head']['repo']['full_name'] == api.repo and
                old['head']['ref'] == pr['head']['ref']]
            if any(ancestor(old['head']['sha'], pr['head']['sha']) for old in prior):
                result = merge_validated(api, pr)
                if 'retained' not in result:
                    return result
                retained_candidates.append({
                    'pr': pr['number'],
                    'reason': result['retained'],
                })
    open_names = {p['head']['ref'] for p in pulls if p['head']['repo'] and p['head']['repo']['full_name'] == api.repo}
    branches = {b['name']: b for b in api.pages('branches')}
    approved = api.ref(APPROVED)
    for pr in closed:
        name = pr['head']['ref']
        if (not pr.get('merged_at') or name in KEEP or name in open_names or name not in branches
            or not pr['head']['repo'] or pr['head']['repo']['full_name'] != api.repo
            or pr['author_association'] not in {'OWNER', 'MEMBER', 'COLLABORATOR'}):
            continue
        head = branches[name]['commit']['sha']
        if ancestor(head, approved):
            continue
        # Force-rewritten/unrelated work never inherits the prior PR's readiness.
        if not ancestor(pr['head']['sha'], head):
            continue
        try:
            correction = api.api('pulls', {'head': name, 'base': APPROVED,
                'title': 'Continue approved release corrections from ' + name,
                'body': 'Automatically carries new commits on the retained source branch after its previous approved PR. '
                        'Owning validation and the approved direct-release authority will re-evaluate only invalidated evidence; branch deletion remains gated.'})
        except RuntimeError as exc:
            if 'HTTP 403' not in str(exc):
                raise
            retained_candidates.append({
                'pr': pr['number'],
                'branch': name,
                'reason': 'Correction PR creation blocked; unique branch history retained without integration',
            })
            continue
        # The previous collaborator PR authorizes review, not skipping fresh CI.
        return {'correctionPr': correction['number'], 'retained': 'Fresh exact-head validation required'}
    result = {'integration': 'no validated ready changes or retained-branch corrections'}
    if retained_candidates:
        result['retainedCandidates'] = retained_candidates
    return result


def release_targets(revision):
    """Read the immutable target scope authorized by an approved release commit."""
    result = git('show', revision + ':Docs/releases/direct-release-request.json', check=False)
    if result.returncode:
        return set()
    try:
        request = json.loads(result.stdout)
    except (TypeError, ValueError, json.JSONDecodeError):
        return set()
    if request.get('releaseMode') != 'approved-only':
        return set()
    if 'targets' not in request:
        return set(VALIDATION_AUTHORITY.release_name_map())
    targets = request.get('targets')
    if not isinstance(targets, list) or any(not isinstance(item, str) for item in targets):
        return set()
    return set(targets)


def successful_release(api, run, app=None):
    """Accept only complete approved direct-release receipts.

    Validation workflows may authorize a merge, but only the single direct-release
    workflow can establish deployed application provenance.
    """
    if run['status'] != 'completed' or run['conclusion'] != 'success':
        return False
    path = run['path'].split('@')[0]
    if path != '.github/workflows/' + DIRECT:
        return False
    if run.get('head_repository', {}).get('full_name') != api.repo:
        return False
    if run.get('head_branch') != APPROVED:
        return False
    jobs = api.pages(f"actions/runs/{run['id']}/jobs?filter=latest", 'jobs')
    required = {'discover-live', 'release'}
    passed = {job['name'] for job in jobs if job['conclusion'] == 'success'}
    if not required <= passed:
        return False
    release_job = next(job for job in jobs if job['name'] == 'release')
    steps = release_job.get('steps', [])
    if not any(step['name'] == 'Verify every deployed target and collect all failures'
               and step['conclusion'] == 'success' for step in steps):
        return False
    if not any(step['name'] == 'Enforce complete direct deployment outcome'
               and step['conclusion'] == 'success' for step in steps):
        return False
    if app is None:
        return True
    # A selected target may have been intentionally preserved because it was
    # already live at APPLICATION_RELEASE_SHA. Final live proof + enforcement is
    # authoritative; requiring the deploy step itself would reject safe retries.
    release_name = _canonical_release_name(app)
    return bool(release_name and release_name in release_targets(run.get('head_sha', '')))

def direct_only_request(sha):
    # A one-release exception bound to the exact commit that changes the request.
    # Two authorized shapes exist:
    # 1) a validated PR merge that carries the request in the merge itself; or
    # 2) a single-parent control-only authorization immediately after that merge.
    path = VALIDATION_AUTHORITY.RELEASE_REQUEST_PATH
    lineage = git('rev-list', '--parents', '-n', '1', sha, check=False)
    parts = lineage.stdout.strip().split() if not lineage.returncode else []
    if not parts or parts[0] != sha or len(parts) not in {2, 3}:
        return False

    result = git('show', sha + ':' + path, check=False)
    if result.returncode:
        return False
    try:
        request = json.loads(result.stdout)
    except (TypeError, ValueError, json.JSONDecodeError):
        return False
    if request.get('releaseMode') != 'approved-only':
        return False

    if len(parts) == 2:
        changed = git('diff-tree', '--no-commit-id', '--name-only', '-r', sha + '^1', sha, check=False)
        if changed.returncode or changed.stdout.splitlines() != [path]:
            return False
        return True

    # Product merge authorization: compare the request object directly against
    # the first parent. This avoids merge diff simplification hiding a request
    # change when the same merge also carries application/migration files.
    prior = git('show', sha + '^1:' + path, check=False)
    if prior.returncode:
        return True
    return prior.stdout != result.stdout


def direct_release_approved_pr(api, sha):
    """Resolve an exact approved-only release revision to its validated product PR.

    Release authorization may be rewritten several times, and release-control-only
    PRs may be merged between the last product PR and the final authorization.
    Walk backward through a bounded chain of request-only commits and release-control
    PR merges until the nearest product-changing merged PR is reached. Any malformed
    lineage, non-control single-parent commit, excessive chain, or ambiguous PR map
    fails closed.
    """
    if not direct_only_request(sha):
        return None

    current = sha
    request_path = VALIDATION_AUTHORITY.RELEASE_REQUEST_PATH
    for _ in range(16):
        lineage = git('rev-list', '--parents', '-n', '1', current, check=False)
        parts = lineage.stdout.strip().split() if not lineage.returncode else []
        if not parts or parts[0] != current:
            return None

        if len(parts) == 3:
            merged = current
        elif len(parts) == 2:
            parent = parts[1]
            changed = git('diff-tree', '--no-commit-id', '--name-only', '-r',
                          current + '^1', current, check=False)
            if changed.returncode or changed.stdout.splitlines() != [request_path]:
                return None
            if direct_only_request(parent):
                current = parent
                continue
            merged = parent
        else:
            return None

        pulls = api.pages('commits/' + merged + '/pulls')
        matches = [p for p in pulls if p.get('merged_at') and p.get('merge_commit_sha') == merged
                   and p.get('base', {}).get('ref') == APPROVED]
        if len(matches) != 1:
            return None
        pr = matches[0]
        files = api.pages(f"pulls/{pr['number']}/files")
        names = {row.get('filename') for row in files}
        if names and all(
            VALIDATION_AUTHORITY.release_control_only_path(name)
            for name in names if name
        ):
            merge_lineage = git('rev-list', '--parents', '-n', '1', merged, check=False)
            merge_parts = merge_lineage.stdout.strip().split() if not merge_lineage.returncode else []
            if len(merge_parts) != 3 or merge_parts[0] != merged:
                return None
            current = merge_parts[1]
            continue
        return pr

    return None


def authorization_release_proven(api, authorization_sha, targets):
    """Accept a successful direct release bound to the exact authorization commit.

    Older release workflow generations wrote a generic receipt keyed by the
    approved merge/authorization SHA rather than the application source SHA.
    The workflow run itself is durable proof only when it is the sole canonical
    direct-release workflow, targets this approved authorization, and its final
    live verification and enforcement both succeeded.
    """
    runs = api.pages(
        'actions/runs?head_sha=' + urllib.parse.quote(authorization_sha, safe=''),
        'workflow_runs',
    )
    for run in runs:
        if run.get('head_sha') != authorization_sha:
            continue
        if not successful_release(api, run):
            continue
        authorized = release_targets(authorization_sha)
        if targets <= authorized:
            return True
    return False


def pending_legacy_release_authorization(api, approved):
    """Resolve only the newest valid explicit authorization on first-parent history.

    Automatic application PRs do not use this path. It exists only to carry a
    previously authorized release across release-control-only correction merges.

    Scan the literal first-parent commit chain rather than path-filtered history:
    Git path simplification must never hide a merge that imports a new release
    request from its second parent. Once the newest valid authorization is found,
    it is authoritative. If already released, stop; never resurrect an older
    superseded authorization.
    """
    history = git('rev-list', '--first-parent', approved, check=False)
    if history.returncode:
        raise RuntimeError('Unable to inspect approved first-parent release authorization history')

    for sha in history.stdout.splitlines():
        if not SHA.fullmatch(sha) or not direct_only_request(sha):
            continue

        pr = direct_release_approved_pr(api, sha)
        if pr is None:
            return {
                'retained': 'Historical release authorization cannot be bound to one validated approved PR'
            }

        pending = candidate_validation(api, pr)
        if pending:
            return {'retained': pending}

        targets = release_targets(sha)
        if not targets:
            return {'retained': 'Historical release authorization has no canonical target scope'}

        revision = pr['head']['sha']
        if (
            all(release_proven(api, revision, app=target) for target in targets)
            or authorization_release_proven(api, sha, targets)
        ):
            return None

        return {
            'authorizationSha': sha,
            'applicationRevision': revision,
            'targets': sorted(targets),
            'sourcePr': pr['number'],
        }

    return None


def _validated_package_evidence(api, revision):
    identity = PACKAGE_AUTHORITY.package_identity(revision)
    return VALIDATION_AUTHORITY.compute_validated_package_evidence(
        api.repo,
        revision,
        identity,
    )


def _package_backfill_running(api, approved):
    runs = api.pages(
        'actions/runs?head_sha=' + urllib.parse.quote(approved, safe=''),
        'workflow_runs',
    )
    return any(
        run.get('path', '').split('@')[0] == '.github/workflows/' + PACKAGE_VALIDATION
        and run.get('event') == 'workflow_dispatch'
        and run.get('status') != 'completed'
        for run in runs
    )


def _package_backfill_preflight(api, revision, approved):
    """Use the package builder's canonical eligibility proof before dispatch."""
    return VALIDATION_AUTHORITY.compute_package_backfill_plan(
        api.repo,
        revision,
        approved,
    )


def pending_automatic_releases(api, approved):
    """Derive the durable queue from approved first-parent PR authorization history.

    Keep the newest authorization for each target; never roll a newer target back
    to an older queued head. A satisfied or failed newest head suppresses only its
    own targets, so it cannot erase another application's pending publication.
    No mutable queue or second authorization store is introduced.
    """
    history = git('rev-list', '--first-parent', approved, check=False)
    if history.returncode:
        raise RuntimeError('Unable to inspect approved first-parent automatic release history')
    pending = []
    covered = set()
    merges = {}
    for pr in api.pages('pulls?state=closed&base=' + urllib.parse.quote(APPROVED, safe='')):
        if pr.get('merged_at') and pr.get('base', {}).get('ref') == APPROVED:
            merges.setdefault(pr.get('merge_commit_sha'), []).append(pr)
    all_targets = {row['releaseName'] for row in VALIDATION_AUTHORITY.RELEASE_TARGETS.values()}
    for sha in history.stdout.splitlines():
        if covered == all_targets:
            break  # older authorizations cannot change any target's frontier
        if not SHA.fullmatch(sha):
            continue
        matches = merges.get(sha, [])
        if len(matches) != 1:
            # Closed-PR collection snapshots can lag immediately after a merge.
            # Resolve the exact first-parent commit directly before allowing an
            # older queued candidate to become the apparent frontier.
            associated = api.pages('commits/' + sha + '/pulls')
            matches = [
                candidate for candidate in associated
                if candidate.get('merged_at')
                and candidate.get('merge_commit_sha') == sha
                and candidate.get('base', {}).get('ref') == APPROVED
            ]
        if len(matches) != 1:
            continue
        pr = matches[0]
        files = api.pages(f"pulls/{pr['number']}/files")
        names = [row['filename'] for row in files if row.get('filename')]
        targets = VALIDATION_AUTHORITY.release_targets_for_paths(names)
        if not targets:
            continue
        revision = pr.get('head', {}).get('sha')
        if not SHA.fullmatch(revision or ''):
            raise RuntimeError('Automatic release source PR has invalid validated head identity')
        control_only = bool(names) and all(
            VALIDATION_AUTHORITY.release_control_only_path(name) for name in names)
        if control_only and not _validated_package_evidence(api, revision).get('reusable'):
            continue
        overlap = covered.intersection(targets)
        covered.update(targets)
        if overlap == set(targets):
            continue
        if overlap:
            # Do not silently drop the untouched portion of an older atomic
            # transaction, and do not invent authorization to split it either.
            pending.append({'authorizationSha': sha, 'applicationRevision': revision,
                            'targets': list(targets), 'sourcePr': pr['number'],
                            'retained': 'Partially superseded atomic release needs a validated combined successor',
                            'supersededTargets': sorted(overlap)})
            continue
        package_evidence = _validated_package_evidence(api, revision)
        publication_revision = package_evidence.get('revision', revision) if package_evidence.get('reusable') else revision
        if all(release_proven(api, publication_revision, app=target) for target in targets):
            continue
        row = {'authorizationSha': sha, 'applicationRevision': revision,
               'targets': list(targets), 'sourcePr': pr['number']}
        validation = candidate_validation(api, pr)
        if validation:
            row['retained'] = validation
        pending.append(row)
    # Stable FIFO among the independent frontier, irrespective of API ordering.
    return list(reversed(pending))


def direct_release_runs(api):
    return [row for row in api.pages(
        'actions/workflows/' + DIRECT + '/runs?branch=' + urllib.parse.quote(APPROVED, safe=''),
        'workflow_runs')
        if row.get('head_branch') == APPROVED
        and row.get('path', '').split('@')[0] == '.github/workflows/' + DIRECT]


def release_dispatch_identity(pr_number, revision, execution_sha):
    if not SHA.fullmatch(revision or '') or not SHA.fullmatch(execution_sha or ''):
        raise ValueError('Malformed immutable release dispatch identity')
    return f"LEGEND release pr={int(pr_number)} candidate={revision} authority={execution_sha}"


def automatic_release_admission(pr, approved, runs):
    """Single fail-closed admission predicate; no independent workflow gate map."""
    active = [row for row in runs if row.get('status') != 'completed']
    if active:
        return {'state': 'WAITING_FOR_CONFLICTING_RELEASE', 'retained': 'Queued in approved PR history until active transaction completes',
                'blockingRuns': [row['id'] for row in active]}
    identity = release_dispatch_identity(pr['number'], pr['head']['sha'], approved)
    attempted = [row for row in runs if row.get('display_title') == identity
                 or (row.get('head_sha') == approved
                     and not row.get('display_title', '').startswith('LEGEND release pr='))]
    if attempted:
        return {'state': 'FAILED_NEEDS_REPAIR', 'retained': 'Exact candidate/authority release already attempted; reconcile or repair without upload replay'}
    return None


def admit_automatic_release(api, pr, approved, targets, *, source_merge_sha=None, runs=None):
    """One admission path for a freshly merged head and a recovered queued head.

    Called under the lifecycle workflow mutex. The publisher retains its global
    transaction mutex until durable resource reservations cover every mutation.
    """
    runs = direct_release_runs(api) if runs is None else runs
    blocked = automatic_release_admission(pr, approved, runs)
    if blocked:
        return blocked
    identity = release_dispatch_identity(pr['number'], pr['head']['sha'], approved)
    dispatched = getattr(api, '_dispatch_handoffs', set())
    if identity in dispatched:
        return {'state': 'RELEASE_DISPATCHED', 'retained': 'This lifecycle invocation already handed off the exact candidate'}
    # Retain the handoff even if GitHub's dispatch response is ambiguous. Only
    # the later workflow/lease reconciliation may decide whether it executed.
    dispatched.add(identity)
    api._dispatch_handoffs = dispatched
    api.dispatch(DIRECT, automatic_release_inputs(pr, approved, targets, source_merge_sha=source_merge_sha))
    return {'state': 'RELEASE_DISPATCHED', 'directRelease': 'automatic validated-merge release', 'targets': list(targets)}


def dispatch_pending_automatic_release(api, approved):
    queue = pending_automatic_releases(api, approved)
    if not queue:
        return None
    runs = direct_release_runs(api)
    retained = []
    for pending in queue:
        if 'retained' in pending:
            retained.append(pending)
            continue
        pr_identity = {'number': pending['sourcePr'], 'head': {'sha': pending['applicationRevision']}}
        blocked = automatic_release_admission(pr_identity, approved, runs)
        if blocked:
            if 'blockingRuns' in blocked:
                return {**blocked, 'pendingCandidates': queue}
            retained.append({**pending, **blocked})
            continue
        package = _validated_package_evidence(api, pending['applicationRevision'])
        if not package.get('reusable'):
            preflight = _package_backfill_preflight(api, pending['applicationRevision'], approved)
            if not preflight.get('allowed'):
                retained.append({
                    **pending,
                    'packageBackfill': 'not dispatched',
                    'packageReason': preflight.get('reason') or package.get('reason'),
                    'retained': 'Historical package backfill is ineligible under current approved application lineage',
                })
                continue
            if _package_backfill_running(api, approved):
                retained.append({**pending, 'packageBackfill': 'already queued or running'})
                continue
            api.dispatch(PACKAGE_VALIDATION, {'package_revision': pending['applicationRevision']})
            return {'state': 'WAITING_FOR_DEPENDENCY', 'packageBackfill': 'dispatched for exact green automatic application revision',
                    'packageReason': package.get('reason'), **pending}
        pr = api.api(f"pulls/{pending['sourcePr']}")
        if not pr:
            retained.append({**pending, 'retained': 'Automatic release recovery could not reload source PR'})
            continue
        if pr.get('head', {}).get('sha') != pending['applicationRevision']:
            retained.append({**pending, 'retained': 'Source PR head changed after queue discovery'})
            continue
        admission = admit_automatic_release(api, pr, approved, tuple(pending['targets']),
                                             source_merge_sha=pending['authorizationSha'], runs=runs)
        return {**admission, **pending}
    return {'state': 'WAITING_FOR_DEPENDENCY', 'retained': 'Pending candidates require proof or repair', 'pendingCandidates': retained}


def dispatch_pending_legacy_release(api, approved):
    pending = pending_legacy_release_authorization(api, approved)
    if not pending:
        return None
    if 'retained' in pending:
        return pending

    package = _validated_package_evidence(api, pending['applicationRevision'])
    if not package.get('reusable'):
        preflight = _package_backfill_preflight(api, pending['applicationRevision'], approved)
        if not preflight.get('allowed'):
            return {
                'state': 'SUPERSEDED' if preflight.get('reason') == 'application_inputs_changed_since_validated_revision' else 'WAITING_FOR_DEPENDENCY',
                'packageBackfill': 'not dispatched',
                'packageReason': preflight.get('reason') or package.get('reason'),
                'retained': 'Historical release package backfill is ineligible under current approved application lineage',
                **pending,
            }
        if _package_backfill_running(api, approved):
            return {
                'packageBackfill': 'already queued or running',
                **pending,
            }
        api.dispatch(PACKAGE_VALIDATION, {
            'package_revision': pending['applicationRevision'],
        })
        return {
            'packageBackfill': 'dispatched for exact green historical application revision',
            'packageReason': package.get('reason'),
            **pending,
        }

    api.dispatch(DIRECT, {
        'automatic': 'false',
        'merge_sha': pending['authorizationSha'],
    })
    return {
        'directRelease': 'recovered nearest still-unreleased historical authorization',
        'packageEvidenceRunId': package.get('runId'),
        **pending,
    }


def release_execution_state(api, run):
    """Derive lifecycle state from the actual canonical worker and child evidence."""
    if successful_release(api, run):
        return 'COMPLETE'
    jobs = api.pages(f"actions/runs/{run['id']}/jobs?filter=latest", 'jobs')
    release_jobs = [job for job in jobs if job.get('name') == 'release']
    steps = release_jobs[0].get('steps', []) if release_jobs else []
    publications = [step for step in steps if step.get('name', '').startswith('Publish canonical target (')]
    if run.get('status') == 'completed':
        artifacts = api.pages(f"actions/runs/{run['id']}/artifacts", 'artifacts')
        names = {item.get('name', '') for item in artifacts if not item.get('expired')}
        intents = {name.removeprefix('legend-release-operation-intent-') for name in names
                   if name.startswith('legend-release-operation-intent-')}
        successes = {name.removeprefix('legend-release-operation-success-') for name in names
                     if name.startswith('legend-release-operation-success-')}
        settled = any(name.startswith('legend-release-disposition-') for name in names)
        if intents - successes and not settled:
            return 'DEPLOYMENT_RECONCILIATION'
        return 'WAITING_FOR_CONFLICTING_RELEASE' if _never_admitted(api, run) else 'FAILED_NEEDS_REPAIR'
    if any(step.get('status') == 'in_progress' for step in publications):
        return 'DEPLOYING'
    if any(step.get('name') == 'Reconcile complete immutable release transaction'
           and step.get('conclusion') == 'success' for step in steps):
        return 'LIVE_PROOF_REQUIRED'
    return 'RELEASE_DISPATCHED'


def reconcile(api, trigger=None):
    """Wake the durable queue on every completion, including failed siblings.

    A failed run is evidence about that candidate, never a veto of all other
    approved work. Dispatch admission still refuses replay of that exact attempt.
    """
    if staging_only():
        return {'release': 'disabled while validation-only staging hold is active'}
    approved = api.ref(APPROVED)
    automatic = dispatch_pending_automatic_release(api, approved)
    if automatic:
        return automatic
    runs = direct_release_runs(api)
    if any(row.get('status') != 'completed' for row in runs):
        return {'release': 'already queued or running'}
    # Legacy explicit authorization remains a separately authorized request shape,
    # not another scheduler. Preserve failed attempts, without blocking the
    # automatic queue above or replaying an ambiguous historical upload.
    if trigger:
        run = api.api(f'actions/runs/{trigger}')
        path = run.get('path', '').split('@')[0]
        if path in {'.github/workflows/' + DIRECT, '.github/workflows/' + PACKAGE_VALIDATION}:
            if run.get('conclusion') != 'success':
                return {'retained': 'Triggered release or package attempt needs reconciliation or repair; no automatic replay'}
    current = [row for row in runs if row.get('head_sha') == approved]
    if current:
        latest = max(current, key=lambda row: (row.get('id', 0), row.get('run_attempt', 1)))
        return {'state': release_execution_state(api, latest),
                'retained': 'Exact approved release already attempted; correction or reconciliation required'}
    historical = dispatch_pending_legacy_release(api, approved)
    if historical:
        return historical
    return {'state': 'READY', 'release': 'no application publication required for exact approved head'}


def _admission_identity(record):
    payload = {key: value for key, value in record.items() if key != 'admissionId'}
    return hashlib.sha256(json.dumps(payload, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def _admission_records(api, run):
    artifacts = api.pages(f"actions/runs/{run['id']}/artifacts", 'artifacts')
    records = []
    for artifact in artifacts:
        name = artifact.get('name', '')
        if not name.startswith('legend-release-admission-'):
            continue
        if artifact.get('expired'):
            raise RuntimeError('Release admission evidence expired; resource disposition must be reconciled')
        with tempfile.TemporaryDirectory(prefix='legend-admission-') as directory:
            VALIDATION_AUTHORITY._download_run_artifact(api.repo, run['id'], name, Path(directory))
            path = Path(directory) / 'operation.json'
            if path.stat().st_size > 32768:
                raise RuntimeError('Oversized admission record')
            record = json.loads(path.read_text())
        if (record.get('schemaVersion') != 1 or record.get('phase') != 'admission'
            or record.get('producingRun') != run['id']
            or record.get('admissionId') != _admission_identity(record)
            or name != 'legend-release-admission-' + record['admissionId']):
            raise RuntimeError('Release admission identity does not match its durable producer')
        VALIDATION_AUTHORITY.selected_release_target_keys(record.get('selectedTargets'))
        if (run.get('event') != 'workflow_dispatch'
            or (run.get('head_repository') or {}).get('full_name', '').lower() != api.repo.lower()
            or not isinstance(record.get('producingAttempt'), int)
            or not 1 <= record['producingAttempt'] <= run.get('run_attempt', 1)
            or record.get('authorizationMode') not in {'automatic', 'explicit'}):
            raise RuntimeError('Untrusted release admission producer or authorization mode')
        pr = api.api(f"pulls/{record['sourcePr']}")
        if (not pr.get('merged_at') or pr.get('base', {}).get('ref') != APPROVED
            or pr.get('head', {}).get('sha') != record.get('authorizedSourceRevision')
            or pr.get('merge_commit_sha') != record.get('sourceMergeSha')
            or not ancestor(record['sourceMergeSha'], record['executionAuthority'])):
            raise RuntimeError('Admission source no longer binds its validated approved PR')
        # Reused immutable bytes retain their producer revision and identity.
        # Recompute compatibility of the actual producing inputs, not an identity
        # relabeled with the newer authorized PR revision. The exact retained
        # producer/artifact binding is independently verified below.
        if not VALIDATION_AUTHORITY.package_inputs_compatible(
                record['applicationRevision'], record['authorizedSourceRevision']):
            raise RuntimeError('Admission authorized source is not equivalent to its immutable package inputs')
        package = VALIDATION_AUTHORITY.compute_validated_package_evidence(
            api.repo, record['applicationRevision'], record['packageIdentity'], allow_equivalent=False)
        if (not package.get('reusable') or package.get('revision') != record.get('applicationRevision')
            or package.get('packageIdentity') != record.get('packageIdentity')):
            raise RuntimeError('Admission immutable package binding is missing or changed')
        paths = [row['filename'] for row in api.pages(f"pulls/{pr['number']}/files") if row.get('filename')]
        routing = False
        if record['authorizationMode'] == 'automatic':
            expected_targets = list(VALIDATION_AUTHORITY.release_targets_for_paths(paths))
        else:
            if not direct_only_request(record['executionAuthority']):
                raise RuntimeError('Admission lacks exact explicit release authorization')
            expected_targets = sorted(release_targets(record['executionAuthority']))
            request = json.loads(git('show', record['executionAuthority'] + ':' + VALIDATION_AUTHORITY.RELEASE_REQUEST_PATH).stdout)
            routing = request.get('cloudflareWebsiteRouting', False)
        expected_resources = VALIDATION_AUTHORITY.release_admission_resources(paths, expected_targets, routing=routing)
        if record['selectedTargets'] != expected_targets or record.get('resources') != expected_resources:
            raise RuntimeError('Admission resource ownership differs from canonical authorized dependencies')
        records.append(record)
    return records


def _never_admitted(api, run):
    # A skipped latest retry cannot erase an earlier entered publication. Every
    # recorded attempt must independently prove downstream jobs never executed.
    for attempt in range(1, run.get('run_attempt', 1) + 1):
        jobs = api.pages(f"actions/runs/{run['id']}/attempts/{attempt}/jobs", 'jobs')
        admission = [job for job in jobs if job.get('name') == 'admission']
        downstream = [job for job in jobs if job.get('name') in {'discover-live', 'release'}]
        if not (admission and len(downstream) == 2
                and all(job.get('conclusion') == 'skipped' for job in downstream)):
            return False
    return True


def _admission_nonmutating_terminal(api, run):
    """Prove a completed admitted run never crossed into a mutable release phase.

    This is intentionally stricter than workflow failure. Every attempt must show
    either a skipped release job, or a transaction-preparation failure with every
    downstream mutation-capable step skipped. Any durable operation intent keeps
    the lease blocking.
    """
    if run.get('status') != 'completed':
        return False
    artifacts = api.pages(f"actions/runs/{run['id']}/artifacts", 'artifacts')
    names = {item.get('name', '') for item in artifacts}
    if any(name.startswith('legend-release-operation-intent-') for name in names):
        return False
    attempts = run.get('run_attempt', 1)
    if type(attempts) is not int or attempts < 1:
        return False
    workflow_path = '.github/workflows/' + DIRECT
    revision = run.get('head_sha', '')
    if not SHA.fullmatch(revision):
        return False
    original = git('show', revision + ':' + workflow_path, check=False)
    if original.returncode or original.stdout != Path(workflow_path).read_text():
        return False  # unknown execution generation cannot prove non-mutation
    for attempt in range(1, attempts + 1):
        jobs = api.pages(f"actions/runs/{run['id']}/attempts/{attempt}/jobs", 'jobs')
        release_jobs = [job for job in jobs if job.get('name') == 'release']
        if len(release_jobs) != 1:
            return False
        release = release_jobs[0]
        if release.get('conclusion') == 'skipped':
            continue
        if not VALIDATION_AUTHORITY._failed_transaction_preparation_without_writes(
                original.stdout, release):
            return False
    return True


def _admission_settled(api, run, record):
    if run.get('status') != 'completed':
        return False
    name = 'legend-release-disposition-' + record['admissionId'] + '-' + str(run.get('run_attempt', 1))
    artifacts = api.pages(f"actions/runs/{run['id']}/artifacts", 'artifacts')
    matching = [row for row in artifacts if row.get('name') == name and not row.get('expired')]
    if len(matching) != 1:
        return False
    with tempfile.TemporaryDirectory(prefix='legend-disposition-') as directory:
        VALIDATION_AUTHORITY._download_run_artifact(api.repo, run['id'], name, Path(directory))
        value = json.loads((Path(directory) / 'release-disposition.json').read_text())
    targets = {row.get('target') for row in value.get('targets', []) if row.get('idle') is True}
    expected = set(VALIDATION_AUTHORITY.selected_release_target_keys(record['selectedTargets']))
    return (value.get('schemaVersion') == 1 and value.get('terminal') is True
            and value.get('mutableChildrenSettled') is True
            and value.get('admissionId') == record['admissionId']
            and value.get('candidateRevision') == record['applicationRevision']
            and value.get('producingRun') == run['id']
            and value.get('producingAttempt') == run.get('run_attempt', 1)
            and value.get('resources') == record['resources'] and targets == expected)


def admission_conflicts(api, candidate, *, current_run):
    """Called only while holding the shared scheduler/admission workflow mutex."""
    conflicts = []
    for run in direct_release_runs(api):
        own_run = run['id'] == current_run
        if run.get('status') == 'completed' and successful_release(api, run):
            continue  # exact terminal live proof discharges this publication lease
        # A completed run that provably never entered any mutation phase owns no
        # live release resource. Discharge it before interpreting historical
        # admission scope through the current target/path inventory.
        if not own_run and _admission_nonmutating_terminal(api, run):
            continue
        records = _admission_records(api, run)
        if own_run:
            current_attempt = int(os.environ.get('GITHUB_RUN_ATTEMPT', '1'))
            records = [record for record in records if record['producingAttempt'] < current_attempt]
            if not records:
                # The active admission cannot prove its own downstream jobs skipped.
                # Inspect every earlier attempt; only proven non-entry permits retry.
                prior_attempts = dict(run, run_attempt=current_attempt - 1)
                if current_attempt == 1 or _never_admitted(api, prior_attempts):
                    continue
        if not records:
            if run.get('status') == 'completed' and _never_admitted(api, run):
                continue
            jobs = api.pages(f"actions/runs/{run['id']}/jobs?filter=latest", 'jobs')
            introduced = any(job.get('name') == 'admission' for job in jobs)
            if introduced or run.get('status') != 'completed':
                conflicts.append({'runId': run['id'], 'reason': 'Unknown or missing resource admission evidence'})
            # Historical terminal workflows predate the resource lease contract.
            # Their upload history is still reconciled by the operation journal;
            # active legacy workflows always block new admission globally.
            continue
        for record in records:
            if _admission_settled(api, run, record) or _admission_nonmutating_terminal(api, run):
                continue
            if not VALIDATION_AUTHORITY.release_resources_overlap(candidate['resources'], record['resources']):
                continue
            continuation = ((run.get('status') == 'completed' or own_run)
                            and record['applicationRevision'] == candidate['applicationRevision']
                            and record['selectedTargets'] == candidate['selectedTargets']
                            and record['resources'] == candidate['resources'])
            if continuation:
                # Same immutable transaction resumes under a new control revision.
                # Original per-target intent still forbids every ambiguous replay.
                continue
            conflicts.append({'runId': run['id'], 'admissionId': record['admissionId'],
                              'reason': 'Conflicting release lacks terminal exact-live disposition'})
    return conflicts


def admit_worker(api):
    """Common resource admission for manual and automatic direct workers."""
    if staging_only():
        return {'state': 'WAITING_FOR_DEPENDENCY', 'admitted': False, 'retained': 'Validation-only staging hold'}
    authority = os.environ.get('RELEASE_SHA', '')
    if not SHA.fullmatch(authority) or git('rev-parse', 'HEAD').stdout.strip() != authority:
        raise RuntimeError('Admission checkout is not the exact approved execution authority')
    automatic = os.environ.get('AUTOMATIC_RELEASE') == 'true'
    if automatic:
        pr = api.api('pulls/' + str(int(os.environ['AUTOMATIC_SOURCE_PR'])))
        revision = os.environ.get('AUTOMATIC_VALIDATED_SHA')
        source_merge = os.environ.get('AUTOMATIC_SOURCE_MERGE_SHA') or authority
        if (not pr.get('merged_at') or pr.get('base', {}).get('ref') != APPROVED
            or pr.get('merge_commit_sha') != source_merge or pr.get('head', {}).get('sha') != revision
            or not ancestor(source_merge, authority)):
            raise RuntimeError('Automatic admission does not bind one merged validated PR')
        paths = [row['filename'] for row in api.pages(f"pulls/{pr['number']}/files") if row.get('filename')]
        targets = list(VALIDATION_AUTHORITY.release_targets_for_paths(paths))
        if json.loads(os.environ['AUTOMATIC_TARGETS_JSON']) != targets:
            raise RuntimeError('Admission target scope differs from canonical PR ownership')
        routing = False
    else:
        if not direct_only_request(authority):
            raise RuntimeError('Manual worker has no exact approved release authorization')
        pr = direct_release_approved_pr(api, authority)
        if pr is None:
            raise RuntimeError('Manual release does not bind one validated source PR')
        revision = pr['head']['sha']
        source_merge = pr.get('merge_commit_sha') or authority
        paths = [row['filename'] for row in api.pages(f"pulls/{pr['number']}/files") if row.get('filename')]
        request = json.loads(git('show', authority + ':' + VALIDATION_AUTHORITY.RELEASE_REQUEST_PATH).stdout)
        targets = sorted(release_targets(authority))
        routing = request.get('cloudflareWebsiteRouting', False)
        if not isinstance(routing, bool):
            raise RuntimeError('Malformed routing release authorization')
    pending = candidate_validation(api, pr)
    if pending:
        return {'state': 'VALIDATING', 'admitted': False, 'retained': pending}
    package = _validated_package_evidence(api, revision)
    if not package.get('reusable'):
        return {'state': 'WAITING_FOR_DEPENDENCY', 'admitted': False,
                'retained': 'Immutable validated package proof is required before resource admission'}
    run_id = int(os.environ['GITHUB_RUN_ID'])
    record = {'schemaVersion': 1, 'phase': 'admission', 'authorizationMode': 'automatic' if automatic else 'explicit', 'sourcePr': pr['number'],
              'authorizedSourceRevision': revision, 'packageIdentity': package['packageIdentity'],
              'applicationRevision': package['revision'], 'executionAuthority': authority,
              'sourceMergeSha': source_merge, 'selectedTargets': targets,
              'resources': VALIDATION_AUTHORITY.release_admission_resources(paths, targets, routing=routing),
              'producingRun': run_id, 'producingAttempt': int(os.environ['GITHUB_RUN_ATTEMPT'])}
    record['admissionId'] = _admission_identity(record)
    conflicts = admission_conflicts(api, record, current_run=run_id)
    if conflicts:
        return {'state': 'WAITING_FOR_CONFLICTING_RELEASE', 'admitted': False, 'blockers': conflicts}
    transport = Path(__file__).with_name('release-artifacts') / 'transport.cjs'
    result = subprocess.run(['node', str(transport)], input=json.dumps({
        'name': 'legend-release-admission-' + record['admissionId'], 'record': record}),
        text=True, capture_output=True)
    if result.returncode or 'LEGEND_OPERATION_RESULT=' not in result.stdout:
        raise RuntimeError('Durable release admission readback failed; no mutation authorized')
    return {'state': 'RELEASE_READY', 'admitted': True, 'admission': record}


def _canonical_release_name(app):
    if app is None:
        return None
    if app in VALIDATION_AUTHORITY.RELEASE_TARGETS:
        return VALIDATION_AUTHORITY.RELEASE_TARGETS[app]["releaseName"]
    if app in VALIDATION_AUTHORITY.release_name_map():
        return app
    return None


def release_proven(api, revision, app=None):
    """Require a durable successful direct-release receipt for a live app revision.

    The workflow head can be a release-control-only descendant while the deployed
    application identity intentionally remains an earlier source revision. The
    artifact name binds proof to APPLICATION_RELEASE_SHA instead of guessing from
    the workflow head.
    """
    release_name = _canonical_release_name(app)
    if app is not None and release_name is None:
        return False
    name = 'legend-approved-release-' + revision + (('-' + release_name) if release_name else '')
    artifacts = api.pages(
        'actions/artifacts?name=' + urllib.parse.quote(name, safe=''),
        'artifacts',
    )
    run_ids = []
    for artifact in artifacts:
        if artifact.get('expired'):
            continue
        run_id = (artifact.get('workflow_run') or {}).get('id')
        if isinstance(run_id, int) and run_id not in run_ids:
            run_ids.append(run_id)
    for run_id in run_ids:
        run = api.api(f'actions/runs/{run_id}')
        # Target-specific artifact names are the scope proof for modern
        # transactional releases. Legacy generic receipts still fall back to the
        # committed request check below.
        if successful_release(api, run):
            return True

    # Bootstrap durable proof for exact-head direct releases that completed before
    # the application-release receipt artifact existed. This is intentionally
    # narrower than receipt reuse: the workflow run itself must be for this exact
    # live revision, and successful_release still requires the sole direct-release
    # workflow, approved branch, target scope, final live proof and enforcement.
    # Control-only descendants therefore still require the receipt artifact above.
    exact_runs = api.pages(
        'actions/runs?head_sha=' + urllib.parse.quote(revision, safe=''),
        'workflow_runs',
    )
    exact_runs.sort(
        key=lambda row: (
            row.get('updated_at') or row.get('run_started_at') or row.get('created_at', ''),
            row.get('run_attempt', 1),
        ),
        reverse=True,
    )
    for run in exact_runs:
        if run.get('head_sha') != revision:
            continue
        if successful_release(api, run, app=app):
            return True
    return False


def cleanup(api, apply=False):
    if staging_only():
        return {'retained': 'Validation-only staging hold; no integration, dispatch or cleanup'}
    branches = api.pages('branches')
    approved = api.ref(APPROVED)
    live = live_revisions()
    for row in live:
        if not release_proven(api, row['revision'], app=row['app']):
            return {'retained': 'Live app lacks an exact successful direct-release receipt: ' + row['app']}
    for revision in [approved, *(row['revision'] for row in live)]:
        git('cat-file', '-e', revision + '^{commit}')

    pulls = api.pages('pulls?state=open')
    open_refs = {pull['base']['ref'] for pull in pulls} | {
        pull['head']['ref'] for pull in pulls
        if pull['head']['repo'] and pull['head']['repo']['full_name'] == api.repo
    }
    active = []
    for status in ('queued', 'in_progress', 'waiting', 'requested', 'pending'):
        active.extend(api.pages('actions/runs?status=' + status, 'workflow_runs'))
    if any(row.get('path', '').split('@')[0] == '.github/workflows/' + DIRECT for row in active):
        return {'retained': 'A direct release is queued or running; live evidence may change'}
    active_refs = {row['head_branch'] for row in active}

    rows = []
    for branch in branches:
        name, sha = branch['name'], branch['commit']['sha']
        latest = api.pages('actions/runs?branch=' + urllib.parse.quote(name, safe=''), 'workflow_runs')
        latest.sort(key=lambda row: row.get('updated_at') or row['created_at'], reverse=True)
        failed = {name} if latest and latest[0]['conclusion'] not in {'success', 'skipped', None} else set()
        allowed, reason = eligible(branch, approved, live, open_refs, active_refs, failed)
        row = {'branch': name, 'sha': sha, 'eligible': allowed, 'reason': reason, 'deleted': False}
        if allowed and apply:
            if api.ref(APPROVED) != approved:
                raise RuntimeError('Approved branch moved during cleanup; stop and retry from fresh evidence')
            current = api.api('branches/' + urllib.parse.quote(name, safe=''))
            fresh_pulls = api.pages('pulls?state=open')
            used = any(
                pull['base']['ref'] == name or (
                    pull['head']['repo']
                    and pull['head']['repo']['full_name'] == api.repo
                    and pull['head']['ref'] == name
                )
                for pull in fresh_pulls
            )
            recent = api.pages('actions/runs?branch=' + urllib.parse.quote(name, safe=''), 'workflow_runs')
            recent.sort(key=lambda item: item.get('updated_at') or item['created_at'], reverse=True)
            if (used or current['protected'] or current['commit']['sha'] != sha
                or any(item['status'] != 'completed' for item in recent)
                or (recent and recent[0]['conclusion'] not in {'success', 'skipped'})):
                row['reason'] = 'branch gained work or protection during cleanup'
            else:
                result = git(
                    'push', '--force-with-lease=refs/heads/' + name + ':' + sha,
                    'origin', ':refs/heads/' + name, check=False
                )
                if result.returncode:
                    raise RuntimeError('Atomic branch deletion failed; branch retained: ' + name)
                row['deleted'] = True
        rows.append(row)
    return {'approvedSha': approved, 'live': live, 'branches': rows}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['integrate', 'pending-updates', 'reconcile', 'cleanup', 'admit-worker'])
    parser.add_argument('--pr', type=int)
    parser.add_argument('--run', type=int)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    api = GitHub()
    if args.command == 'admit-worker':
        result = admit_worker(api)
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            output.write('admitted=' + str(result['admitted']).lower() + '\n')
            output.write('state=' + result['state'] + '\n')
            if result.get('admitted'):
                output.write('admission_id=' + result['admission']['admissionId'] + '\n')
                output.write('resources=' + json.dumps(result['admission']['resources'], separators=(',', ':')) + '\n')
    elif args.command == 'integrate':
        result = integrate(api, args.pr)
    elif args.command == 'pending-updates':
        result = pending_updates(api)
    elif args.command == 'reconcile':
        result = reconcile(api, args.run)
    else:
        result = cleanup(api, args.apply)
    if args.output:
        args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
)
RELEASE_QUEUE_REQUEST = re.compile(r'^requested-pr=([0-9]+)
def git(*args, check=True):
    return subprocess.run(['git', *args], check=check, text=True, capture_output=True)


def ancestor(before, after):
    result = git('merge-base', '--is-ancestor', before, after, check=False)
    if result.returncode not in (0, 1):
        raise RuntimeError(result.stderr)
    return result.returncode == 0


def eligible(branch, approved, live, open_refs, active_refs, failed_refs):
    name, sha = branch['name'], branch['commit']['sha']
    if name in KEEP or branch.get('protected'):
        return False, 'canonical/protected branch'
    if name in open_refs:
        return False, 'open pull request uses branch as source or base'
    if name in active_refs:
        return False, 'workflow still running'
    if name in failed_refs:
        return False, 'latest branch workflow failed or was cancelled'
    if not ancestor(sha, approved):
        return False, 'unique history not preserved in approved changes'
    if not live or not all(ancestor(sha, row['revision']) for row in live):
        return False, 'not covered by every live web application revision'
    return True, 'preserved in approved changes and every live web revision'


class GitHub:
    def __init__(self):
        self.repo = os.environ['GITHUB_REPOSITORY']
        self.token = os.environ['GH_TOKEN']
        self.root = 'https://api.github.com/repos/' + self.repo

    def api(self, path, data=None, method=None):
        payload = json.dumps(data).encode() if data is not None else None
        request = urllib.request.Request(self.root + '/' + path, data=payload,
            method=method or ('POST' if payload is not None else 'GET'), headers={
                'Authorization': 'Bearer ' + self.token,
                'Accept': 'application/vnd.github+json',
                'Content-Type': 'application/json', 'User-Agent': 'legend-release-lifecycle'})
        try:
            with urllib.request.urlopen(request, timeout=45) as response:
                body = response.read()
                return json.loads(body) if body else None
        except urllib.error.HTTPError as error:
            # Do not log tokens, response bodies or environment dumps.
            raise RuntimeError(f'GitHub {request.method} {path}: HTTP {error.code}') from None

    def pages(self, path, key=None):
        rows = []
        for page in range(1, 101):
            result = self.api(path + ('&' if '?' in path else '?') + f'per_page=100&page={page}')
            values = result[key] if key else result
            rows.extend(values)
            if len(values) < 100:
                return rows
        raise RuntimeError('Pagination limit reached; refusing incomplete branch evidence')

    def ref(self, name):
        branch = self.api('branches/' + urllib.parse.quote(name, safe=''))
        value = (branch.get('commit') or {}).get('sha')
        if not SHA.fullmatch(value or ''):
            raise RuntimeError('Malformed branch revision')
        return value

    def dispatch(self, workflow, inputs=None):
        self.api('actions/workflows/' + workflow + '/dispatches',
                 {'ref': APPROVED, 'inputs': inputs or {}})

    def text(self, revision, path):
        row = self.api(
            'contents/' + urllib.parse.quote(path, safe='/') +
            '?ref=' + urllib.parse.quote(revision, safe='')
        )
        if row.get('encoding') != 'base64' or not isinstance(row.get('content'), str):
            raise RuntimeError('Candidate control-plane source is unavailable')
        try:
            return base64.b64decode(row['content'], validate=False).decode('utf-8')
        except (ValueError, UnicodeError):
            raise RuntimeError('Candidate control-plane source is malformed') from None

    def status(self, revision, state, description):
        if not SHA.fullmatch(revision or '') or state not in {'pending', 'success', 'failure', 'error'}:
            raise ValueError('Malformed trusted validation status')
        self.api(
            'statuses/' + revision,
            {
                'state': state,
                'context': 'architecture-validation',
                'description': description[:140],
            },
            method='POST',
        )


def live_revisions():
    spec = importlib.util.spec_from_file_location('release_baseline', Path(__file__).with_name('approved-release-baseline.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    # A missing/unreachable app blocks deletion of ALL branches, not just that app.
    return [module.observe(target) for target in module.TARGETS]


def ready(pr, repo, base):
    return (pr['state'] == 'open' and not pr['draft'] and pr['base']['ref'] == base
        and pr['head']['repo'] and pr['head']['repo']['full_name'] == repo
        and pr['head']['ref'] not in KEEP
        and pr['author_association'] in {'OWNER', 'MEMBER', 'COLLABORATOR'})



def approved_head_state(api, pr):
    """Return whether a PR head already contains the exact current approved head."""
    approved = api.ref(APPROVED)
    head = (pr.get("head") or {}).get("sha")
    if not SHA.fullmatch(head or ""):
        raise RuntimeError("Malformed candidate revision")
    if head == approved:
        return {
            "current": True,
            "approved": approved,
            "candidate": head,
            "mergeBase": approved,
            "status": "identical",
        }
    compare = api.api(
        "compare/" + urllib.parse.quote(approved, safe="") + "..." +
        urllib.parse.quote(head, safe="")
    )
    merge_base = (compare.get("merge_base_commit") or {}).get("sha")
    status = compare.get("status")
    return {
        "current": merge_base == approved and status in {"ahead", "identical"},
        "approved": approved,
        "candidate": head,
        "mergeBase": merge_base,
        "status": status,
    }


def sync_candidate_to_current_approved(api, pr):
    """Fast-forward a trusted same-repo candidate by merging approved into it.

    The operation is additive only: no reset, rebase, force-push, or source
    rewrite. A new PR head causes normal exact-head validation to restart.
    """
    state = approved_head_state(api, pr)
    if state["current"]:
        return None
    if not ready(pr, api.repo, APPROVED):
        return {
            "state": "BASE_SYNC_REQUIRED",
            "retained": "Candidate is stale but is not eligible for trusted automatic base sync",
            "pr": pr.get("number"),
            **state,
        }
    try:
        result = api.api(
            "merges",
            {
                "base": pr["head"]["ref"],
                "head": state["approved"],
                "commit_message": (
                    f"Sync current {APPROVED} into PR #{pr['number']} before validation"
                ),
            },
            method="POST",
        )
    except RuntimeError as exc:
        if any(f"HTTP {code}" in str(exc) for code in (409, 422)):
            return {
                "state": "BASE_SYNC_REQUIRED",
                "retained": "Current approved head could not be merged cleanly into candidate",
                "pr": pr["number"],
                **state,
            }
        raise
    fresh = api.api(f"pulls/{pr['number']}")
    synced = (fresh.get("head") or {}).get("sha")
    if not SHA.fullmatch(synced or ""):
        raise RuntimeError("Approved-head synchronization did not produce a candidate revision")
    return {
        "state": "BASE_SYNCED",
        "pr": pr["number"],
        "previousHead": state["candidate"],
        "approvedHead": state["approved"],
        "head": synced,
        "validation": "new synchronize event must validate the synced exact head",
    }


def _assignment_strings(tree, name):
    for node in tree.body:
        if isinstance(node, ast.Assign) and any(
            isinstance(target, ast.Name) and target.id == name
            for target in node.targets
        ):
            if isinstance(node.value, (ast.Tuple, ast.List, ast.Set)):
                return {
                    item.value for item in node.value.elts
                    if isinstance(item, ast.Constant) and isinstance(item.value, str)
                }
    return set()


def _function_source(source, tree, name):
    for node in tree.body:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name == name:
            return ast.get_source_segment(source, node) or ''
    return ''


def repository_ruleset_integrity(api):
    """The protected branch must retain the external safety rails the lifecycle assumes."""
    rows = api.api('rulesets')
    if not isinstance(rows, list):
        return 'Repository ruleset inventory unavailable'
    for row in rows:
        if row.get('enforcement') != 'active' or row.get('target') != 'branch':
            continue
        ruleset_id = row.get('id')
        if type(ruleset_id) is not int:
            continue
        detail = api.api(f'rulesets/{ruleset_id}')
        refs = (detail.get('conditions') or {}).get('ref_name') or {}
        if 'refs/heads/' + APPROVED not in (refs.get('include') or []):
            continue
        if detail.get('bypass_actors'):
            return 'Protected approved branch gained a ruleset bypass actor'
        if detail.get('current_user_can_bypass') not in {None, 'never'}:
            return 'Protected approved branch permits ruleset bypass'
        rules = {rule.get('type'): rule for rule in detail.get('rules') or []}
        required_types = {'deletion', 'non_fast_forward', 'pull_request', 'required_status_checks'}
        missing = required_types - set(rules)
        if missing:
            return 'Protected approved branch ruleset lost: ' + ', '.join(sorted(missing))
        checks = (rules['required_status_checks'].get('parameters') or {})
        contexts = {
            row.get('context') for row in checks.get('required_status_checks') or []
            if row.get('context')
        }
        if checks.get('strict_required_status_checks_policy') is not True:
            return 'Protected approved branch no longer requires strict up-to-date checks'
        if 'architecture-validation' not in contexts:
            return 'Protected approved branch lost trusted architecture-validation requirement'
        merge = rules['pull_request'].get('parameters') or {}
        if merge.get('allowed_merge_methods') != ['merge']:
            return 'Protected approved branch merge method drifted from canonical merge-only policy'
        return None
    return 'Active approved-branch protection ruleset is missing'


def candidate_control_plane_integrity(api, pr, names):
    """Read candidate control files as data from trusted base code; never execute them."""
    settings = repository_ruleset_integrity(api)
    if settings:
        return settings
    if not any(VALIDATION_AUTHORITY.release_control_authority_path(name) for name in names):
        return None

    head = pr['head']['sha']
    paths = {
        'validation': 'scripts/validation-resume.py',
        'lifecycle': 'scripts/release-lifecycle.py',
        'lifecycle_workflow': '.github/workflows/legend-release-lifecycle.yml',
        'direct_workflow': '.github/workflows/all-intentional-direct-release-20260918.yml',
        'architecture_workflow': '.github/workflows/masterapp-platform-architecture-validation.yml',
        'step5_workflow': '.github/workflows/step5-isolated-conversion-mapping-validation.yml',
        'step6_workflow': '.github/workflows/step6-openai-ads-execution-validation.yml',
        'step78_workflow': '.github/workflows/steps7-8-governed-advertising-validation.yml',
        'security_workflow': '.github/workflows/approved-release-security-validation.yml',
    }
    try:
        source = {key: api.text(head, path) for key, path in paths.items()}
        validation_tree = ast.parse(source['validation'])
        lifecycle_tree = ast.parse(source['lifecycle'])
    except (RuntimeError, SyntaxError):
        return 'Candidate release-control authority cannot be parsed from exact head'

    assignments = {}
    for node in validation_tree.body:
        if isinstance(node, ast.Assign):
            for target in node.targets:
                if isinstance(target, ast.Name) and isinstance(node.value, ast.Constant):
                    assignments[target.id] = node.value.value
    if assignments.get('TRUSTED_PR_BASE') != APPROVED:
        return 'Candidate changed the sole approved release branch authority'
    if assignments.get('DIRECT_RELEASE_WORKFLOW') != DIRECT:
        return 'Candidate changed the sole approved direct-release workflow authority'

    candidate_paths = _assignment_strings(validation_tree, 'LIFECYCLE_AUTHORITY_PATHS')
    required_paths = set(VALIDATION_AUTHORITY.LIFECYCLE_AUTHORITY_PATHS)
    if not required_paths <= candidate_paths:
        return 'Candidate removed protected lifecycle authority paths: ' + ', '.join(sorted(required_paths - candidate_paths))

    predicate = _function_source(source['validation'], validation_tree, 'release_control_authority_path')
    if not all(token in predicate for token in (
        'LIFECYCLE_AUTHORITY_PATHS', 'PACKAGE_AUTHORITY_PATHS', 'RELEASE_EXECUTION_CONTROL_INPUTS'
    )):
        return 'Candidate weakened canonical release-control authority classification'

    topology = _function_source(source['validation'], validation_tree, 'required_validation_topology')
    if not all(token in topology for token in (
        'release_control_authority_change',
        'release_control_authority_path',
        'required.add(security)',
    )):
        return 'Candidate release-control changes no longer require canonical security validation'

    candidate_validation_source = _function_source(source['lifecycle'], lifecycle_tree, 'candidate_validation')
    if not all(token in candidate_validation_source for token in (
        'VALIDATION_AUTHORITY.required_validation_topology(names)',
        "run.get('event') != 'pull_request'",
        "latest[path].get('status') != 'completed'",
        "latest[path].get('conclusion') != 'success'",
    )):
        return 'Candidate weakened exact-head merge validation'

    guard_source = _function_source(source['lifecycle'], lifecycle_tree, 'candidate_control_plane_integrity')
    sync_source = _function_source(source['lifecycle'], lifecycle_tree, 'sync_candidate_to_current_approved')
    merge_source = _function_source(source['lifecycle'], lifecycle_tree, 'merge_validated')
    if not guard_source or 'candidate_control_plane_integrity(api, pr, names)' not in merge_source:
        return 'Candidate removed trusted control-plane integrity enforcement'
    if merge_source.find('candidate_control_plane_integrity(api, pr, names)') > merge_source.find("pulls/{pr['number']}/merge"):
        return 'Candidate moved control-plane integrity enforcement after merge'
    if not sync_source or not all(token in sync_source for token in (
        'approved_head_state(api, pr)',
        '"merges"',
        '"base": pr["head"]["ref"]',
        '"head": state["approved"]',
        '"state": "BASE_SYNCED"',
    )):
        return 'Candidate weakened automatic current-approved-head synchronization'
    if 'base_state = approved_head_state(api, pr)' not in merge_source:
        return 'Candidate removed final approved-head freshness guard before merge'

    lifecycle_workflow = source['lifecycle_workflow']
    if not all(token in lifecycle_workflow for token in (
        'pull_request_target:',
        'ref: legend/approved-changes',
        'contents: write',
        'pull-requests: write',
        'actions: write',
        'statuses: write',
        'python3 scripts/release-lifecycle.py integrate --pr "$PR_NUMBER"',
    )):
        return 'Candidate weakened trusted protected-branch lifecycle execution'

    direct_workflow = source['direct_workflow']
    for token in (
        'cancel-in-progress: false',
        "if: github.ref == 'refs/heads/legend/approved-changes'",
        'Verify selected authority belongs to protected event history',
        'Prepare complete immutable release transaction',
        'Reconcile complete immutable release transaction',
        'Verify every deployed target and collect all failures',
        'Enforce complete direct deployment outcome',
        'Retain exact approved release receipt',
        'Reconcile terminal release resource disposition',
        'Preserve terminal release resource disposition',
        'release-state-receipt:',
    ):
        if token not in direct_workflow:
            return 'Candidate direct-release workflow lost required invariant: ' + token

    architecture = source['architecture_workflow']
    if 'name: architecture-validation' not in architecture and 'name: candidate-architecture-validation' not in architecture:
        return 'Candidate architecture workflow lost its canonical validation job'
    if 'Run branch lifecycle safety contracts' not in architecture:
        return 'Candidate architecture workflow stopped exercising lifecycle contracts'

    validation_workflows = {
        'architecture': architecture,
        'step5': source['step5_workflow'],
        'step6': source['step6_workflow'],
        'step78': source['step78_workflow'],
        'security': source['security_workflow'],
    }
    for label, workflow in validation_workflows.items():
        if (
            'approved-head-preflight:' not in workflow
            or 'Verify candidate contains current approved head' not in workflow
            or 'ref: ${{ github.event.pull_request.head.sha || github.sha }}' not in workflow
            or 'persist-credentials: false' not in workflow
            or 'approved-head-preflight \\' not in workflow
            or 'needs: approved-head-preflight' not in workflow
        ):
            return f'Candidate {label} validator lost canonical approved-head preflight'
    if architecture.count('needs: approved-head-preflight') < 3:
        return 'Candidate architecture validator allows package/probe work before approved-head preflight'

    security_trigger = source['security_workflow'].split('concurrency:', 1)[0]
    if 'pull_request:' not in security_trigger or 'branches: [legend/approved-changes]' not in security_trigger:
        return 'Candidate security validation no longer covers approved-branch pull requests'
    if '\n    paths:' in security_trigger or '\n    paths-ignore:' in security_trigger:
        return 'Candidate security validation can be skipped by release-control path filtering'
    return None


def publish_trusted_validation_status(api, revision, state, detail):
    descriptions = {
        'pending': 'Trusted release authority is waiting for exact-head validation',
        'success': 'Trusted release authority and exact-head validation passed',
        'failure': 'Trusted release authority blocked unsafe control-plane drift',
    }
    api.status(revision, state, descriptions[state] if not detail else detail)


def candidate_validation(api, pr):
    """Require every workflow selected by the canonical topology to be green.

    Child-level preservation belongs to validation-resume.py. Lifecycle never
    reinterprets a failed parent workflow, carries a second step list, or accepts
    partial validation as merge-ready.
    """
    head = pr['head']['sha']
    runs = api.pages('actions/runs?head_sha=' + head, 'workflow_runs')
    latest = {}
    for run in sorted(
        runs,
        key=lambda row: (row.get('created_at', ''), row.get('id', 0)),
        reverse=True,
    ):
        if run.get('head_sha') != head or run.get('event') != 'pull_request':
            continue
        latest.setdefault(run['path'].split('@')[0], run)

    files = api.pages(f"pulls/{pr['number']}/files")
    names = [row['filename'] for row in files if row.get('filename')]
    required = VALIDATION_AUTHORITY.required_validation_topology(names)['required']

    failed = [
        path for path in required
        if path not in latest
        or latest[path].get('status') != 'completed'
        or latest[path].get('conclusion') != 'success'
    ]
    if not failed:
        return None
    return 'Awaiting successful exact-head validation: ' + ', '.join(sorted(failed))


def automatic_release_targets(api, pr):
    files = api.pages(f"pulls/{pr['number']}/files")
    names = [row.get('filename') for row in files if row.get('filename')]
    return VALIDATION_AUTHORITY.release_targets_for_paths(names)


def automatic_release_inputs(pr, release_sha, targets, *, source_merge_sha=None):
    return {
        'automatic': 'true',
        'source_pr': str(pr['number']),
        'validated_sha': pr['head']['sha'],
        'source_merge_sha': source_merge_sha or release_sha,
        'merge_sha': release_sha,
        'targets_json': json.dumps(list(targets), separators=(',', ':')),
    }


def merge_validated(api, pr):
    base_state = approved_head_state(api, pr)
    if not base_state["current"]:
        publish_trusted_validation_status(
            api, pr["head"]["sha"], "pending",
            "Candidate must contain the current approved head before validation can authorize merge",
        )
        return {
            "state": "BASE_SYNC_REQUIRED",
            "retained": "Candidate does not contain current approved head",
            "pr": pr["number"],
            **base_state,
        }

    files = api.pages(f"pulls/{pr['number']}/files")
    names = [row.get('filename') for row in files if row.get('filename')]
    head = pr['head']['sha']

    integrity = candidate_control_plane_integrity(api, pr, names)
    if integrity:
        publish_trusted_validation_status(api, head, 'failure', integrity)
        return {'state': 'VALIDATING', 'retained': integrity}

    pending = candidate_validation(api, pr)
    if pending:
        publish_trusted_validation_status(api, head, 'pending', '')
        return {'state': 'VALIDATING', 'retained': pending}

    publish_trusted_validation_status(api, head, 'success', '')
    targets = VALIDATION_AUTHORITY.release_targets_for_paths(names)
    control_only = bool(names) and all(
        VALIDATION_AUTHORITY.release_control_only_path(name)
        for name in names
    )
    try:
        result = api.api(f"pulls/{pr['number']}/merge",
            {'merge_method': 'merge', 'sha': pr['head']['sha']}, method='PUT')
    except RuntimeError as exc:
        if any(f"HTTP {code}" in str(exc) for code in (405, 409, 422)):
            return {
                'retained': 'Validated PR is not currently mergeable; source branch retained',
                'pr': pr['number'],
            }
        raise
    if not result.get('merged'):
        return {
            'retained': 'Merge did not complete; source branch retained',
            'pr': pr['number'],
        }

    # Validation success is the publication handoff. Application-affecting merges
    # immediately enter the sole direct-release workflow with scope derived from
    # the validated PR. No second authorization command or hand-maintained target
    # table exists between merge and deployment.
    release_result = None
    if targets and not control_only:
        # Integration and dispatch are separate phases of the same serialized
        # lifecycle invocation. The refresh/reconcile phase dispatches once;
        # do not dispatch here then rediscover an eventually-visible run below.
        release_result = {'releaseRecovery': 'queued for the refreshed reconciliation phase', 'targets': list(targets)}
    else:
        # The merge commit does not exist in this runner's local checkout yet.
        # Historical recovery is intentionally deferred to the workflow's
        # refresh -> reconcile phase, which fetches and resets to the newly
        # approved commit before inspecting first-parent authorization history.
        release_result = {
            'releaseRecovery': 'deferred until refreshed approved checkout',
        }

    if any(row.get('filename') == '.github/workflows/deployment-diagnostics.yml' for row in files):
        api.dispatch('deployment-diagnostics.yml')
    return {
        'state': 'MERGED',
        'transitions': ['MERGE_READY', 'MERGED'],
        'mergedPr': pr['number'],
        'sha': result['sha'],
        'releaseDispatched': bool(release_result and 'directRelease' in release_result),
        'automaticRelease': bool(targets and not control_only),
        'targets': list(targets),
        'release': release_result,
    }


def integrate(api, number):
    if staging_only():
        return {'retained': 'Validation-only staging hold; no integration, dispatch or cleanup'}
    pr = api.api(f'pulls/{number}')

    # pull_request_target events can queue behind another lifecycle run. If that
    # earlier run already merged this exact trusted PR, the delayed event is a
    # replay, not a new integration failure. Prove the recorded merge is in the
    # current approved lineage and return without dispatching anything again.
    merged_sha = pr.get('merge_commit_sha')
    already_integrated = (
        pr.get('state') == 'closed'
        and pr.get('merged_at')
        and pr.get('base', {}).get('ref') == APPROVED
        and pr.get('head', {}).get('repo')
        and pr['head']['repo'].get('full_name') == api.repo
        and pr.get('author_association') in {'OWNER', 'MEMBER', 'COLLABORATOR'}
        and SHA.fullmatch(merged_sha or '')
    )
    if already_integrated:
        approved = api.ref(APPROVED)
        if ancestor(merged_sha, approved):
            return {
                'integration': 'already merged exact PR event preserved as no-op',
                'mergedPr': number,
                'sha': merged_sha,
                'replayed': True,
                'releaseDispatched': False,
            }

    if not ready(pr, api.repo, APPROVED):
        raise RuntimeError('Only ready, same-repository collaborator PRs into approved changes can be integrated')
    synced = sync_candidate_to_current_approved(api, pr)
    if synced is not None:
        return synced
    return merge_validated(api, pr)


def pending_updates(api):
    if staging_only():
        return {'retained': 'Validation-only staging hold; no integration, dispatch or cleanup'}
    # GitHub state may advance while a serialized lifecycle run is waiting.
    # Refresh the canonical approved ref before any ancestry decision so a newly
    # merged trusted PR can never appear as an unknown local commit.
    refreshed = git('fetch', '--no-tags', '--prune', 'origin',
                    f'+refs/heads/{APPROVED}:refs/remotes/origin/{APPROVED}', check=False)
    if refreshed.returncode:
        raise RuntimeError(refreshed.stderr)
    # Scheduled reconciliation also covers bot-created PR events and corrections
    # pushed to a retained branch after its previous approved PR was merged.
    #
    # A retained/unvalidated PR must never starve another fully validated PR.
    # Preserve its reason and continue scanning; stop only after a mutation
    # actually succeeds.
    pulls = api.pages('pulls?state=open&base=' + urllib.parse.quote(APPROVED, safe=''))
    closed = api.pages('pulls?state=closed&base=' + urllib.parse.quote(APPROVED, safe=''))
    retained_candidates = []
    for pr in reversed(pulls):
        if ready(pr, api.repo, APPROVED):
            # The open-PR collection is only a discovery snapshot. A serialized
            # lifecycle can wait behind another merge long enough for that PR's
            # draft/state/association/base/head readiness to change. Re-read the
            # exact PR before mutation; stale discovery must retain and continue,
            # never abort reconciliation or starve a later validated candidate.
            fresh = api.api(f"pulls/{pr['number']}")
            if not ready(fresh, api.repo, APPROVED):
                retained_candidates.append({
                    'pr': pr['number'],
                    'reason': 'PR readiness changed after discovery; retained without mutation',
                })
                continue
            synced = sync_candidate_to_current_approved(api, fresh)
            if synced is not None:
                return synced
            result = merge_validated(api, fresh)
            if 'retained' not in result:
                return result
            retained_candidates.append({
                'pr': pr['number'],
                'reason': result['retained'],
            })
            continue
        if (pr['state'] == 'open' and not pr['draft'] and pr['user']['login'] == 'github-actions[bot]'
            and pr['head']['repo'] and pr['head']['repo']['full_name'] == api.repo
            and pr['head']['ref'] not in KEEP):
            prior = [old for old in closed if old.get('merged_at') and
                old['author_association'] in {'OWNER', 'MEMBER', 'COLLABORATOR'} and
                old['head']['repo'] and old['head']['repo']['full_name'] == api.repo and
                old['head']['ref'] == pr['head']['ref']]
            if any(ancestor(old['head']['sha'], pr['head']['sha']) for old in prior):
                result = merge_validated(api, pr)
                if 'retained' not in result:
                    return result
                retained_candidates.append({
                    'pr': pr['number'],
                    'reason': result['retained'],
                })
    open_names = {p['head']['ref'] for p in pulls if p['head']['repo'] and p['head']['repo']['full_name'] == api.repo}
    branches = {b['name']: b for b in api.pages('branches')}
    approved = api.ref(APPROVED)
    for pr in closed:
        name = pr['head']['ref']
        if (not pr.get('merged_at') or name in KEEP or name in open_names or name not in branches
            or not pr['head']['repo'] or pr['head']['repo']['full_name'] != api.repo
            or pr['author_association'] not in {'OWNER', 'MEMBER', 'COLLABORATOR'}):
            continue
        head = branches[name]['commit']['sha']
        if ancestor(head, approved):
            continue
        # Force-rewritten/unrelated work never inherits the prior PR's readiness.
        if not ancestor(pr['head']['sha'], head):
            continue
        try:
            correction = api.api('pulls', {'head': name, 'base': APPROVED,
                'title': 'Continue approved release corrections from ' + name,
                'body': 'Automatically carries new commits on the retained source branch after its previous approved PR. '
                        'Owning validation and the approved direct-release authority will re-evaluate only invalidated evidence; branch deletion remains gated.'})
        except RuntimeError as exc:
            if 'HTTP 403' not in str(exc):
                raise
            retained_candidates.append({
                'pr': pr['number'],
                'branch': name,
                'reason': 'Correction PR creation blocked; unique branch history retained without integration',
            })
            continue
        # The previous collaborator PR authorizes review, not skipping fresh CI.
        return {'correctionPr': correction['number'], 'retained': 'Fresh exact-head validation required'}
    result = {'integration': 'no validated ready changes or retained-branch corrections'}
    if retained_candidates:
        result['retainedCandidates'] = retained_candidates
    return result


def release_targets(revision):
    """Read the immutable target scope authorized by an approved release commit."""
    result = git('show', revision + ':Docs/releases/direct-release-request.json', check=False)
    if result.returncode:
        return set()
    try:
        request = json.loads(result.stdout)
    except (TypeError, ValueError, json.JSONDecodeError):
        return set()
    if request.get('releaseMode') != 'approved-only':
        return set()
    if 'targets' not in request:
        return set(VALIDATION_AUTHORITY.release_name_map())
    targets = request.get('targets')
    if not isinstance(targets, list) or any(not isinstance(item, str) for item in targets):
        return set()
    return set(targets)


def successful_release(api, run, app=None):
    """Accept only complete approved direct-release receipts.

    Validation workflows may authorize a merge, but only the single direct-release
    workflow can establish deployed application provenance.
    """
    if run['status'] != 'completed' or run['conclusion'] != 'success':
        return False
    path = run['path'].split('@')[0]
    if path != '.github/workflows/' + DIRECT:
        return False
    if run.get('head_repository', {}).get('full_name') != api.repo:
        return False
    if run.get('head_branch') != APPROVED:
        return False
    jobs = api.pages(f"actions/runs/{run['id']}/jobs?filter=latest", 'jobs')
    required = {'discover-live', 'release'}
    passed = {job['name'] for job in jobs if job['conclusion'] == 'success'}
    if not required <= passed:
        return False
    release_job = next(job for job in jobs if job['name'] == 'release')
    steps = release_job.get('steps', [])
    if not any(step['name'] == 'Verify every deployed target and collect all failures'
               and step['conclusion'] == 'success' for step in steps):
        return False
    if not any(step['name'] == 'Enforce complete direct deployment outcome'
               and step['conclusion'] == 'success' for step in steps):
        return False
    if app is None:
        return True
    # A selected target may have been intentionally preserved because it was
    # already live at APPLICATION_RELEASE_SHA. Final live proof + enforcement is
    # authoritative; requiring the deploy step itself would reject safe retries.
    release_name = _canonical_release_name(app)
    return bool(release_name and release_name in release_targets(run.get('head_sha', '')))

def direct_only_request(sha):
    # A one-release exception bound to the exact commit that changes the request.
    # Two authorized shapes exist:
    # 1) a validated PR merge that carries the request in the merge itself; or
    # 2) a single-parent control-only authorization immediately after that merge.
    path = VALIDATION_AUTHORITY.RELEASE_REQUEST_PATH
    lineage = git('rev-list', '--parents', '-n', '1', sha, check=False)
    parts = lineage.stdout.strip().split() if not lineage.returncode else []
    if not parts or parts[0] != sha or len(parts) not in {2, 3}:
        return False

    result = git('show', sha + ':' + path, check=False)
    if result.returncode:
        return False
    try:
        request = json.loads(result.stdout)
    except (TypeError, ValueError, json.JSONDecodeError):
        return False
    if request.get('releaseMode') != 'approved-only':
        return False

    if len(parts) == 2:
        changed = git('diff-tree', '--no-commit-id', '--name-only', '-r', sha + '^1', sha, check=False)
        if changed.returncode or changed.stdout.splitlines() != [path]:
            return False
        return True

    # Product merge authorization: compare the request object directly against
    # the first parent. This avoids merge diff simplification hiding a request
    # change when the same merge also carries application/migration files.
    prior = git('show', sha + '^1:' + path, check=False)
    if prior.returncode:
        return True
    return prior.stdout != result.stdout


def direct_release_approved_pr(api, sha):
    """Resolve an exact approved-only release revision to its validated product PR.

    Release authorization may be rewritten several times, and release-control-only
    PRs may be merged between the last product PR and the final authorization.
    Walk backward through a bounded chain of request-only commits and release-control
    PR merges until the nearest product-changing merged PR is reached. Any malformed
    lineage, non-control single-parent commit, excessive chain, or ambiguous PR map
    fails closed.
    """
    if not direct_only_request(sha):
        return None

    current = sha
    request_path = VALIDATION_AUTHORITY.RELEASE_REQUEST_PATH
    for _ in range(16):
        lineage = git('rev-list', '--parents', '-n', '1', current, check=False)
        parts = lineage.stdout.strip().split() if not lineage.returncode else []
        if not parts or parts[0] != current:
            return None

        if len(parts) == 3:
            merged = current
        elif len(parts) == 2:
            parent = parts[1]
            changed = git('diff-tree', '--no-commit-id', '--name-only', '-r',
                          current + '^1', current, check=False)
            if changed.returncode or changed.stdout.splitlines() != [request_path]:
                return None
            if direct_only_request(parent):
                current = parent
                continue
            merged = parent
        else:
            return None

        pulls = api.pages('commits/' + merged + '/pulls')
        matches = [p for p in pulls if p.get('merged_at') and p.get('merge_commit_sha') == merged
                   and p.get('base', {}).get('ref') == APPROVED]
        if len(matches) != 1:
            return None
        pr = matches[0]
        files = api.pages(f"pulls/{pr['number']}/files")
        names = {row.get('filename') for row in files}
        if names and all(
            VALIDATION_AUTHORITY.release_control_only_path(name)
            for name in names if name
        ):
            merge_lineage = git('rev-list', '--parents', '-n', '1', merged, check=False)
            merge_parts = merge_lineage.stdout.strip().split() if not merge_lineage.returncode else []
            if len(merge_parts) != 3 or merge_parts[0] != merged:
                return None
            current = merge_parts[1]
            continue
        return pr

    return None


def authorization_release_proven(api, authorization_sha, targets):
    """Accept a successful direct release bound to the exact authorization commit.

    Older release workflow generations wrote a generic receipt keyed by the
    approved merge/authorization SHA rather than the application source SHA.
    The workflow run itself is durable proof only when it is the sole canonical
    direct-release workflow, targets this approved authorization, and its final
    live verification and enforcement both succeeded.
    """
    runs = api.pages(
        'actions/runs?head_sha=' + urllib.parse.quote(authorization_sha, safe=''),
        'workflow_runs',
    )
    for run in runs:
        if run.get('head_sha') != authorization_sha:
            continue
        if not successful_release(api, run):
            continue
        authorized = release_targets(authorization_sha)
        if targets <= authorized:
            return True
    return False


def pending_legacy_release_authorization(api, approved):
    """Resolve only the newest valid explicit authorization on first-parent history.

    Automatic application PRs do not use this path. It exists only to carry a
    previously authorized release across release-control-only correction merges.

    Scan the literal first-parent commit chain rather than path-filtered history:
    Git path simplification must never hide a merge that imports a new release
    request from its second parent. Once the newest valid authorization is found,
    it is authoritative. If already released, stop; never resurrect an older
    superseded authorization.
    """
    history = git('rev-list', '--first-parent', approved, check=False)
    if history.returncode:
        raise RuntimeError('Unable to inspect approved first-parent release authorization history')

    for sha in history.stdout.splitlines():
        if not SHA.fullmatch(sha) or not direct_only_request(sha):
            continue

        pr = direct_release_approved_pr(api, sha)
        if pr is None:
            return {
                'retained': 'Historical release authorization cannot be bound to one validated approved PR'
            }

        pending = candidate_validation(api, pr)
        if pending:
            return {'retained': pending}

        targets = release_targets(sha)
        if not targets:
            return {'retained': 'Historical release authorization has no canonical target scope'}

        revision = pr['head']['sha']
        if (
            all(release_proven(api, revision, app=target) for target in targets)
            or authorization_release_proven(api, sha, targets)
        ):
            return None

        return {
            'authorizationSha': sha,
            'applicationRevision': revision,
            'targets': sorted(targets),
            'sourcePr': pr['number'],
        }

    return None


def _validated_package_evidence(api, revision):
    identity = PACKAGE_AUTHORITY.package_identity(revision)
    return VALIDATION_AUTHORITY.compute_validated_package_evidence(
        api.repo,
        revision,
        identity,
    )


def _package_backfill_running(api, approved):
    runs = api.pages(
        'actions/runs?head_sha=' + urllib.parse.quote(approved, safe=''),
        'workflow_runs',
    )
    return any(
        run.get('path', '').split('@')[0] == '.github/workflows/' + PACKAGE_VALIDATION
        and run.get('event') == 'workflow_dispatch'
        and run.get('status') != 'completed'
        for run in runs
    )


def _package_backfill_preflight(api, revision, approved):
    """Use the package builder's canonical eligibility proof before dispatch."""
    return VALIDATION_AUTHORITY.compute_package_backfill_plan(
        api.repo,
        revision,
        approved,
    )


def pending_automatic_releases(api, approved):
    """Derive the durable queue from approved first-parent PR authorization history.

    Keep the newest authorization for each target; never roll a newer target back
    to an older queued head. A satisfied or failed newest head suppresses only its
    own targets, so it cannot erase another application's pending publication.
    No mutable queue or second authorization store is introduced.
    """
    history = git('rev-list', '--first-parent', approved, check=False)
    if history.returncode:
        raise RuntimeError('Unable to inspect approved first-parent automatic release history')
    pending = []
    covered = set()
    merges = {}
    for pr in api.pages('pulls?state=closed&base=' + urllib.parse.quote(APPROVED, safe='')):
        if pr.get('merged_at') and pr.get('base', {}).get('ref') == APPROVED:
            merges.setdefault(pr.get('merge_commit_sha'), []).append(pr)
    all_targets = {row['releaseName'] for row in VALIDATION_AUTHORITY.RELEASE_TARGETS.values()}
    for sha in history.stdout.splitlines():
        if covered == all_targets:
            break  # older authorizations cannot change any target's frontier
        if not SHA.fullmatch(sha):
            continue
        matches = merges.get(sha, [])
        if len(matches) != 1:
            # Closed-PR collection snapshots can lag immediately after a merge.
            # Resolve the exact first-parent commit directly before allowing an
            # older queued candidate to become the apparent frontier.
            associated = api.pages('commits/' + sha + '/pulls')
            matches = [
                candidate for candidate in associated
                if candidate.get('merged_at')
                and candidate.get('merge_commit_sha') == sha
                and candidate.get('base', {}).get('ref') == APPROVED
            ]
        if len(matches) != 1:
            continue
        pr = matches[0]
        files = api.pages(f"pulls/{pr['number']}/files")
        names = [row['filename'] for row in files if row.get('filename')]
        targets = VALIDATION_AUTHORITY.release_targets_for_paths(names)
        if not targets:
            continue
        revision = pr.get('head', {}).get('sha')
        if not SHA.fullmatch(revision or ''):
            raise RuntimeError('Automatic release source PR has invalid validated head identity')
        control_only = bool(names) and all(
            VALIDATION_AUTHORITY.release_control_only_path(name) for name in names)
        if control_only and not _validated_package_evidence(api, revision).get('reusable'):
            continue
        overlap = covered.intersection(targets)
        covered.update(targets)
        if overlap == set(targets):
            continue
        if overlap:
            # Do not silently drop the untouched portion of an older atomic
            # transaction, and do not invent authorization to split it either.
            pending.append({'authorizationSha': sha, 'applicationRevision': revision,
                            'targets': list(targets), 'sourcePr': pr['number'],
                            'retained': 'Partially superseded atomic release needs a validated combined successor',
                            'supersededTargets': sorted(overlap)})
            continue
        package_evidence = _validated_package_evidence(api, revision)
        publication_revision = package_evidence.get('revision', revision) if package_evidence.get('reusable') else revision
        if all(release_proven(api, publication_revision, app=target) for target in targets):
            continue
        row = {'authorizationSha': sha, 'applicationRevision': revision,
               'targets': list(targets), 'sourcePr': pr['number']}
        validation = candidate_validation(api, pr)
        if validation:
            row['retained'] = validation
        pending.append(row)
    # Stable FIFO among the independent frontier, irrespective of API ordering.
    return list(reversed(pending))


def direct_release_runs(api):
    return [row for row in api.pages(
        'actions/workflows/' + DIRECT + '/runs?branch=' + urllib.parse.quote(APPROVED, safe=''),
        'workflow_runs')
        if row.get('head_branch') == APPROVED
        and row.get('path', '').split('@')[0] == '.github/workflows/' + DIRECT]


def release_dispatch_identity(pr_number, revision, execution_sha):
    if not SHA.fullmatch(revision or '') or not SHA.fullmatch(execution_sha or ''):
        raise ValueError('Malformed immutable release dispatch identity')
    return f"LEGEND release pr={int(pr_number)} candidate={revision} authority={execution_sha}"


def automatic_release_admission(pr, approved, runs):
    """Single fail-closed admission predicate; no independent workflow gate map."""
    active = [row for row in runs if row.get('status') != 'completed']
    if active:
        return {'state': 'WAITING_FOR_CONFLICTING_RELEASE', 'retained': 'Queued in approved PR history until active transaction completes',
                'blockingRuns': [row['id'] for row in active]}
    identity = release_dispatch_identity(pr['number'], pr['head']['sha'], approved)
    attempted = [row for row in runs if row.get('display_title') == identity
                 or (row.get('head_sha') == approved
                     and not row.get('display_title', '').startswith('LEGEND release pr='))]
    if attempted:
        return {'state': 'FAILED_NEEDS_REPAIR', 'retained': 'Exact candidate/authority release already attempted; reconcile or repair without upload replay'}
    return None


def admit_automatic_release(api, pr, approved, targets, *, source_merge_sha=None, runs=None):
    """One admission path for a freshly merged head and a recovered queued head.

    Called under the lifecycle workflow mutex. The publisher retains its global
    transaction mutex until durable resource reservations cover every mutation.
    """
    runs = direct_release_runs(api) if runs is None else runs
    blocked = automatic_release_admission(pr, approved, runs)
    if blocked:
        return blocked
    identity = release_dispatch_identity(pr['number'], pr['head']['sha'], approved)
    dispatched = getattr(api, '_dispatch_handoffs', set())
    if identity in dispatched:
        return {'state': 'RELEASE_DISPATCHED', 'retained': 'This lifecycle invocation already handed off the exact candidate'}
    # Retain the handoff even if GitHub's dispatch response is ambiguous. Only
    # the later workflow/lease reconciliation may decide whether it executed.
    dispatched.add(identity)
    api._dispatch_handoffs = dispatched
    api.dispatch(DIRECT, automatic_release_inputs(pr, approved, targets, source_merge_sha=source_merge_sha))
    return {'state': 'RELEASE_DISPATCHED', 'directRelease': 'automatic validated-merge release', 'targets': list(targets)}


def dispatch_pending_automatic_release(api, approved):
    queue = pending_automatic_releases(api, approved)
    if not queue:
        return None
    runs = direct_release_runs(api)
    retained = []
    for pending in queue:
        if 'retained' in pending:
            retained.append(pending)
            continue
        pr_identity = {'number': pending['sourcePr'], 'head': {'sha': pending['applicationRevision']}}
        blocked = automatic_release_admission(pr_identity, approved, runs)
        if blocked:
            if 'blockingRuns' in blocked:
                return {**blocked, 'pendingCandidates': queue}
            retained.append({**pending, **blocked})
            continue
        package = _validated_package_evidence(api, pending['applicationRevision'])
        if not package.get('reusable'):
            preflight = _package_backfill_preflight(api, pending['applicationRevision'], approved)
            if not preflight.get('allowed'):
                retained.append({
                    **pending,
                    'packageBackfill': 'not dispatched',
                    'packageReason': preflight.get('reason') or package.get('reason'),
                    'retained': 'Historical package backfill is ineligible under current approved application lineage',
                })
                continue
            if _package_backfill_running(api, approved):
                retained.append({**pending, 'packageBackfill': 'already queued or running'})
                continue
            api.dispatch(PACKAGE_VALIDATION, {'package_revision': pending['applicationRevision']})
            return {'state': 'WAITING_FOR_DEPENDENCY', 'packageBackfill': 'dispatched for exact green automatic application revision',
                    'packageReason': package.get('reason'), **pending}
        pr = api.api(f"pulls/{pending['sourcePr']}")
        if not pr:
            retained.append({**pending, 'retained': 'Automatic release recovery could not reload source PR'})
            continue
        if pr.get('head', {}).get('sha') != pending['applicationRevision']:
            retained.append({**pending, 'retained': 'Source PR head changed after queue discovery'})
            continue
        admission = admit_automatic_release(api, pr, approved, tuple(pending['targets']),
                                             source_merge_sha=pending['authorizationSha'], runs=runs)
        return {**admission, **pending}
    return {'state': 'WAITING_FOR_DEPENDENCY', 'retained': 'Pending candidates require proof or repair', 'pendingCandidates': retained}


def dispatch_pending_legacy_release(api, approved):
    pending = pending_legacy_release_authorization(api, approved)
    if not pending:
        return None
    if 'retained' in pending:
        return pending

    package = _validated_package_evidence(api, pending['applicationRevision'])
    if not package.get('reusable'):
        preflight = _package_backfill_preflight(api, pending['applicationRevision'], approved)
        if not preflight.get('allowed'):
            return {
                'state': 'SUPERSEDED' if preflight.get('reason') == 'application_inputs_changed_since_validated_revision' else 'WAITING_FOR_DEPENDENCY',
                'packageBackfill': 'not dispatched',
                'packageReason': preflight.get('reason') or package.get('reason'),
                'retained': 'Historical release package backfill is ineligible under current approved application lineage',
                **pending,
            }
        if _package_backfill_running(api, approved):
            return {
                'packageBackfill': 'already queued or running',
                **pending,
            }
        api.dispatch(PACKAGE_VALIDATION, {
            'package_revision': pending['applicationRevision'],
        })
        return {
            'packageBackfill': 'dispatched for exact green historical application revision',
            'packageReason': package.get('reason'),
            **pending,
        }

    api.dispatch(DIRECT, {
        'automatic': 'false',
        'merge_sha': pending['authorizationSha'],
    })
    return {
        'directRelease': 'recovered nearest still-unreleased historical authorization',
        'packageEvidenceRunId': package.get('runId'),
        **pending,
    }


def release_execution_state(api, run):
    """Derive lifecycle state from the actual canonical worker and child evidence."""
    if successful_release(api, run):
        return 'COMPLETE'
    jobs = api.pages(f"actions/runs/{run['id']}/jobs?filter=latest", 'jobs')
    release_jobs = [job for job in jobs if job.get('name') == 'release']
    steps = release_jobs[0].get('steps', []) if release_jobs else []
    publications = [step for step in steps if step.get('name', '').startswith('Publish canonical target (')]
    if run.get('status') == 'completed':
        artifacts = api.pages(f"actions/runs/{run['id']}/artifacts", 'artifacts')
        names = {item.get('name', '') for item in artifacts if not item.get('expired')}
        intents = {name.removeprefix('legend-release-operation-intent-') for name in names
                   if name.startswith('legend-release-operation-intent-')}
        successes = {name.removeprefix('legend-release-operation-success-') for name in names
                     if name.startswith('legend-release-operation-success-')}
        settled = any(name.startswith('legend-release-disposition-') for name in names)
        if intents - successes and not settled:
            return 'DEPLOYMENT_RECONCILIATION'
        return 'WAITING_FOR_CONFLICTING_RELEASE' if _never_admitted(api, run) else 'FAILED_NEEDS_REPAIR'
    if any(step.get('status') == 'in_progress' for step in publications):
        return 'DEPLOYING'
    if any(step.get('name') == 'Reconcile complete immutable release transaction'
           and step.get('conclusion') == 'success' for step in steps):
        return 'LIVE_PROOF_REQUIRED'
    return 'RELEASE_DISPATCHED'


def reconcile(api, trigger=None):
    """Wake the durable queue on every completion, including failed siblings.

    A failed run is evidence about that candidate, never a veto of all other
    approved work. Dispatch admission still refuses replay of that exact attempt.
    """
    if staging_only():
        return {'release': 'disabled while validation-only staging hold is active'}
    approved = api.ref(APPROVED)
    automatic = dispatch_pending_automatic_release(api, approved)
    if automatic:
        return automatic
    runs = direct_release_runs(api)
    if any(row.get('status') != 'completed' for row in runs):
        return {'release': 'already queued or running'}
    # Legacy explicit authorization remains a separately authorized request shape,
    # not another scheduler. Preserve failed attempts, without blocking the
    # automatic queue above or replaying an ambiguous historical upload.
    if trigger:
        run = api.api(f'actions/runs/{trigger}')
        path = run.get('path', '').split('@')[0]
        if path in {'.github/workflows/' + DIRECT, '.github/workflows/' + PACKAGE_VALIDATION}:
            if run.get('conclusion') != 'success':
                return {'retained': 'Triggered release or package attempt needs reconciliation or repair; no automatic replay'}
    current = [row for row in runs if row.get('head_sha') == approved]
    if current:
        latest = max(current, key=lambda row: (row.get('id', 0), row.get('run_attempt', 1)))
        return {'state': release_execution_state(api, latest),
                'retained': 'Exact approved release already attempted; correction or reconciliation required'}
    historical = dispatch_pending_legacy_release(api, approved)
    if historical:
        return historical
    return {'state': 'READY', 'release': 'no application publication required for exact approved head'}


def _admission_identity(record):
    payload = {key: value for key, value in record.items() if key != 'admissionId'}
    return hashlib.sha256(json.dumps(payload, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def _admission_records(api, run):
    artifacts = api.pages(f"actions/runs/{run['id']}/artifacts", 'artifacts')
    records = []
    for artifact in artifacts:
        name = artifact.get('name', '')
        if not name.startswith('legend-release-admission-'):
            continue
        if artifact.get('expired'):
            raise RuntimeError('Release admission evidence expired; resource disposition must be reconciled')
        with tempfile.TemporaryDirectory(prefix='legend-admission-') as directory:
            VALIDATION_AUTHORITY._download_run_artifact(api.repo, run['id'], name, Path(directory))
            path = Path(directory) / 'operation.json'
            if path.stat().st_size > 32768:
                raise RuntimeError('Oversized admission record')
            record = json.loads(path.read_text())
        if (record.get('schemaVersion') != 1 or record.get('phase') != 'admission'
            or record.get('producingRun') != run['id']
            or record.get('admissionId') != _admission_identity(record)
            or name != 'legend-release-admission-' + record['admissionId']):
            raise RuntimeError('Release admission identity does not match its durable producer')
        VALIDATION_AUTHORITY.selected_release_target_keys(record.get('selectedTargets'))
        if (run.get('event') != 'workflow_dispatch'
            or (run.get('head_repository') or {}).get('full_name', '').lower() != api.repo.lower()
            or not isinstance(record.get('producingAttempt'), int)
            or not 1 <= record['producingAttempt'] <= run.get('run_attempt', 1)
            or record.get('authorizationMode') not in {'automatic', 'explicit'}):
            raise RuntimeError('Untrusted release admission producer or authorization mode')
        pr = api.api(f"pulls/{record['sourcePr']}")
        if (not pr.get('merged_at') or pr.get('base', {}).get('ref') != APPROVED
            or pr.get('head', {}).get('sha') != record.get('authorizedSourceRevision')
            or pr.get('merge_commit_sha') != record.get('sourceMergeSha')
            or not ancestor(record['sourceMergeSha'], record['executionAuthority'])):
            raise RuntimeError('Admission source no longer binds its validated approved PR')
        # Reused immutable bytes retain their producer revision and identity.
        # Recompute compatibility of the actual producing inputs, not an identity
        # relabeled with the newer authorized PR revision. The exact retained
        # producer/artifact binding is independently verified below.
        if not VALIDATION_AUTHORITY.package_inputs_compatible(
                record['applicationRevision'], record['authorizedSourceRevision']):
            raise RuntimeError('Admission authorized source is not equivalent to its immutable package inputs')
        package = VALIDATION_AUTHORITY.compute_validated_package_evidence(
            api.repo, record['applicationRevision'], record['packageIdentity'], allow_equivalent=False)
        if (not package.get('reusable') or package.get('revision') != record.get('applicationRevision')
            or package.get('packageIdentity') != record.get('packageIdentity')):
            raise RuntimeError('Admission immutable package binding is missing or changed')
        paths = [row['filename'] for row in api.pages(f"pulls/{pr['number']}/files") if row.get('filename')]
        routing = False
        if record['authorizationMode'] == 'automatic':
            expected_targets = list(VALIDATION_AUTHORITY.release_targets_for_paths(paths))
        else:
            if not direct_only_request(record['executionAuthority']):
                raise RuntimeError('Admission lacks exact explicit release authorization')
            expected_targets = sorted(release_targets(record['executionAuthority']))
            request = json.loads(git('show', record['executionAuthority'] + ':' + VALIDATION_AUTHORITY.RELEASE_REQUEST_PATH).stdout)
            routing = request.get('cloudflareWebsiteRouting', False)
        expected_resources = VALIDATION_AUTHORITY.release_admission_resources(paths, expected_targets, routing=routing)
        if record['selectedTargets'] != expected_targets or record.get('resources') != expected_resources:
            raise RuntimeError('Admission resource ownership differs from canonical authorized dependencies')
        records.append(record)
    return records


def _never_admitted(api, run):
    # A skipped latest retry cannot erase an earlier entered publication. Every
    # recorded attempt must independently prove downstream jobs never executed.
    for attempt in range(1, run.get('run_attempt', 1) + 1):
        jobs = api.pages(f"actions/runs/{run['id']}/attempts/{attempt}/jobs", 'jobs')
        admission = [job for job in jobs if job.get('name') == 'admission']
        downstream = [job for job in jobs if job.get('name') in {'discover-live', 'release'}]
        if not (admission and len(downstream) == 2
                and all(job.get('conclusion') == 'skipped' for job in downstream)):
            return False
    return True


def _admission_nonmutating_terminal(api, run):
    """Prove a completed admitted run never crossed into a mutable release phase.

    This is intentionally stricter than workflow failure. Every attempt must show
    either a skipped release job, or a transaction-preparation failure with every
    downstream mutation-capable step skipped. Any durable operation intent keeps
    the lease blocking.
    """
    if run.get('status') != 'completed':
        return False
    artifacts = api.pages(f"actions/runs/{run['id']}/artifacts", 'artifacts')
    names = {item.get('name', '') for item in artifacts}
    if any(name.startswith('legend-release-operation-intent-') for name in names):
        return False
    attempts = run.get('run_attempt', 1)
    if type(attempts) is not int or attempts < 1:
        return False
    workflow_path = '.github/workflows/' + DIRECT
    revision = run.get('head_sha', '')
    if not SHA.fullmatch(revision):
        return False
    original = git('show', revision + ':' + workflow_path, check=False)
    if original.returncode or original.stdout != Path(workflow_path).read_text():
        return False  # unknown execution generation cannot prove non-mutation
    for attempt in range(1, attempts + 1):
        jobs = api.pages(f"actions/runs/{run['id']}/attempts/{attempt}/jobs", 'jobs')
        release_jobs = [job for job in jobs if job.get('name') == 'release']
        if len(release_jobs) != 1:
            return False
        release = release_jobs[0]
        if release.get('conclusion') == 'skipped':
            continue
        if not VALIDATION_AUTHORITY._failed_transaction_preparation_without_writes(
                original.stdout, release):
            return False
    return True


def _admission_settled(api, run, record):
    if run.get('status') != 'completed':
        return False
    name = 'legend-release-disposition-' + record['admissionId'] + '-' + str(run.get('run_attempt', 1))
    artifacts = api.pages(f"actions/runs/{run['id']}/artifacts", 'artifacts')
    matching = [row for row in artifacts if row.get('name') == name and not row.get('expired')]
    if len(matching) != 1:
        return False
    with tempfile.TemporaryDirectory(prefix='legend-disposition-') as directory:
        VALIDATION_AUTHORITY._download_run_artifact(api.repo, run['id'], name, Path(directory))
        value = json.loads((Path(directory) / 'release-disposition.json').read_text())
    targets = {row.get('target') for row in value.get('targets', []) if row.get('idle') is True}
    expected = set(VALIDATION_AUTHORITY.selected_release_target_keys(record['selectedTargets']))
    return (value.get('schemaVersion') == 1 and value.get('terminal') is True
            and value.get('mutableChildrenSettled') is True
            and value.get('admissionId') == record['admissionId']
            and value.get('candidateRevision') == record['applicationRevision']
            and value.get('producingRun') == run['id']
            and value.get('producingAttempt') == run.get('run_attempt', 1)
            and value.get('resources') == record['resources'] and targets == expected)


def admission_conflicts(api, candidate, *, current_run):
    """Called only while holding the shared scheduler/admission workflow mutex."""
    conflicts = []
    for run in direct_release_runs(api):
        own_run = run['id'] == current_run
        if run.get('status') == 'completed' and successful_release(api, run):
            continue  # exact terminal live proof discharges this publication lease
        # A completed run that provably never entered any mutation phase owns no
        # live release resource. Discharge it before interpreting historical
        # admission scope through the current target/path inventory.
        if not own_run and _admission_nonmutating_terminal(api, run):
            continue
        records = _admission_records(api, run)
        if own_run:
            current_attempt = int(os.environ.get('GITHUB_RUN_ATTEMPT', '1'))
            records = [record for record in records if record['producingAttempt'] < current_attempt]
            if not records:
                # The active admission cannot prove its own downstream jobs skipped.
                # Inspect every earlier attempt; only proven non-entry permits retry.
                prior_attempts = dict(run, run_attempt=current_attempt - 1)
                if current_attempt == 1 or _never_admitted(api, prior_attempts):
                    continue
        if not records:
            if run.get('status') == 'completed' and _never_admitted(api, run):
                continue
            jobs = api.pages(f"actions/runs/{run['id']}/jobs?filter=latest", 'jobs')
            introduced = any(job.get('name') == 'admission' for job in jobs)
            if introduced or run.get('status') != 'completed':
                conflicts.append({'runId': run['id'], 'reason': 'Unknown or missing resource admission evidence'})
            # Historical terminal workflows predate the resource lease contract.
            # Their upload history is still reconciled by the operation journal;
            # active legacy workflows always block new admission globally.
            continue
        for record in records:
            if _admission_settled(api, run, record) or _admission_nonmutating_terminal(api, run):
                continue
            if not VALIDATION_AUTHORITY.release_resources_overlap(candidate['resources'], record['resources']):
                continue
            continuation = ((run.get('status') == 'completed' or own_run)
                            and record['applicationRevision'] == candidate['applicationRevision']
                            and record['selectedTargets'] == candidate['selectedTargets']
                            and record['resources'] == candidate['resources'])
            if continuation:
                # Same immutable transaction resumes under a new control revision.
                # Original per-target intent still forbids every ambiguous replay.
                continue
            conflicts.append({'runId': run['id'], 'admissionId': record['admissionId'],
                              'reason': 'Conflicting release lacks terminal exact-live disposition'})
    return conflicts


def admit_worker(api):
    """Common resource admission for manual and automatic direct workers."""
    if staging_only():
        return {'state': 'WAITING_FOR_DEPENDENCY', 'admitted': False, 'retained': 'Validation-only staging hold'}
    authority = os.environ.get('RELEASE_SHA', '')
    if not SHA.fullmatch(authority) or git('rev-parse', 'HEAD').stdout.strip() != authority:
        raise RuntimeError('Admission checkout is not the exact approved execution authority')
    automatic = os.environ.get('AUTOMATIC_RELEASE') == 'true'
    if automatic:
        pr = api.api('pulls/' + str(int(os.environ['AUTOMATIC_SOURCE_PR'])))
        revision = os.environ.get('AUTOMATIC_VALIDATED_SHA')
        source_merge = os.environ.get('AUTOMATIC_SOURCE_MERGE_SHA') or authority
        if (not pr.get('merged_at') or pr.get('base', {}).get('ref') != APPROVED
            or pr.get('merge_commit_sha') != source_merge or pr.get('head', {}).get('sha') != revision
            or not ancestor(source_merge, authority)):
            raise RuntimeError('Automatic admission does not bind one merged validated PR')
        paths = [row['filename'] for row in api.pages(f"pulls/{pr['number']}/files") if row.get('filename')]
        targets = list(VALIDATION_AUTHORITY.release_targets_for_paths(paths))
        if json.loads(os.environ['AUTOMATIC_TARGETS_JSON']) != targets:
            raise RuntimeError('Admission target scope differs from canonical PR ownership')
        routing = False
    else:
        if not direct_only_request(authority):
            raise RuntimeError('Manual worker has no exact approved release authorization')
        pr = direct_release_approved_pr(api, authority)
        if pr is None:
            raise RuntimeError('Manual release does not bind one validated source PR')
        revision = pr['head']['sha']
        source_merge = pr.get('merge_commit_sha') or authority
        paths = [row['filename'] for row in api.pages(f"pulls/{pr['number']}/files") if row.get('filename')]
        request = json.loads(git('show', authority + ':' + VALIDATION_AUTHORITY.RELEASE_REQUEST_PATH).stdout)
        targets = sorted(release_targets(authority))
        routing = request.get('cloudflareWebsiteRouting', False)
        if not isinstance(routing, bool):
            raise RuntimeError('Malformed routing release authorization')
    pending = candidate_validation(api, pr)
    if pending:
        return {'state': 'VALIDATING', 'admitted': False, 'retained': pending}
    package = _validated_package_evidence(api, revision)
    if not package.get('reusable'):
        return {'state': 'WAITING_FOR_DEPENDENCY', 'admitted': False,
                'retained': 'Immutable validated package proof is required before resource admission'}
    run_id = int(os.environ['GITHUB_RUN_ID'])
    record = {'schemaVersion': 1, 'phase': 'admission', 'authorizationMode': 'automatic' if automatic else 'explicit', 'sourcePr': pr['number'],
              'authorizedSourceRevision': revision, 'packageIdentity': package['packageIdentity'],
              'applicationRevision': package['revision'], 'executionAuthority': authority,
              'sourceMergeSha': source_merge, 'selectedTargets': targets,
              'resources': VALIDATION_AUTHORITY.release_admission_resources(paths, targets, routing=routing),
              'producingRun': run_id, 'producingAttempt': int(os.environ['GITHUB_RUN_ATTEMPT'])}
    record['admissionId'] = _admission_identity(record)
    conflicts = admission_conflicts(api, record, current_run=run_id)
    if conflicts:
        return {'state': 'WAITING_FOR_CONFLICTING_RELEASE', 'admitted': False, 'blockers': conflicts}
    transport = Path(__file__).with_name('release-artifacts') / 'transport.cjs'
    result = subprocess.run(['node', str(transport)], input=json.dumps({
        'name': 'legend-release-admission-' + record['admissionId'], 'record': record}),
        text=True, capture_output=True)
    if result.returncode or 'LEGEND_OPERATION_RESULT=' not in result.stdout:
        raise RuntimeError('Durable release admission readback failed; no mutation authorized')
    return {'state': 'RELEASE_READY', 'admitted': True, 'admission': record}


def _canonical_release_name(app):
    if app is None:
        return None
    if app in VALIDATION_AUTHORITY.RELEASE_TARGETS:
        return VALIDATION_AUTHORITY.RELEASE_TARGETS[app]["releaseName"]
    if app in VALIDATION_AUTHORITY.release_name_map():
        return app
    return None


def release_proven(api, revision, app=None):
    """Require a durable successful direct-release receipt for a live app revision.

    The workflow head can be a release-control-only descendant while the deployed
    application identity intentionally remains an earlier source revision. The
    artifact name binds proof to APPLICATION_RELEASE_SHA instead of guessing from
    the workflow head.
    """
    release_name = _canonical_release_name(app)
    if app is not None and release_name is None:
        return False
    name = 'legend-approved-release-' + revision + (('-' + release_name) if release_name else '')
    artifacts = api.pages(
        'actions/artifacts?name=' + urllib.parse.quote(name, safe=''),
        'artifacts',
    )
    run_ids = []
    for artifact in artifacts:
        if artifact.get('expired'):
            continue
        run_id = (artifact.get('workflow_run') or {}).get('id')
        if isinstance(run_id, int) and run_id not in run_ids:
            run_ids.append(run_id)
    for run_id in run_ids:
        run = api.api(f'actions/runs/{run_id}')
        # Target-specific artifact names are the scope proof for modern
        # transactional releases. Legacy generic receipts still fall back to the
        # committed request check below.
        if successful_release(api, run):
            return True

    # Bootstrap durable proof for exact-head direct releases that completed before
    # the application-release receipt artifact existed. This is intentionally
    # narrower than receipt reuse: the workflow run itself must be for this exact
    # live revision, and successful_release still requires the sole direct-release
    # workflow, approved branch, target scope, final live proof and enforcement.
    # Control-only descendants therefore still require the receipt artifact above.
    exact_runs = api.pages(
        'actions/runs?head_sha=' + urllib.parse.quote(revision, safe=''),
        'workflow_runs',
    )
    exact_runs.sort(
        key=lambda row: (
            row.get('updated_at') or row.get('run_started_at') or row.get('created_at', ''),
            row.get('run_attempt', 1),
        ),
        reverse=True,
    )
    for run in exact_runs:
        if run.get('head_sha') != revision:
            continue
        if successful_release(api, run, app=app):
            return True
    return False


def cleanup(api, apply=False):
    if staging_only():
        return {'retained': 'Validation-only staging hold; no integration, dispatch or cleanup'}
    branches = api.pages('branches')
    approved = api.ref(APPROVED)
    live = live_revisions()
    for row in live:
        if not release_proven(api, row['revision'], app=row['app']):
            return {'retained': 'Live app lacks an exact successful direct-release receipt: ' + row['app']}
    for revision in [approved, *(row['revision'] for row in live)]:
        git('cat-file', '-e', revision + '^{commit}')

    pulls = api.pages('pulls?state=open')
    open_refs = {pull['base']['ref'] for pull in pulls} | {
        pull['head']['ref'] for pull in pulls
        if pull['head']['repo'] and pull['head']['repo']['full_name'] == api.repo
    }
    active = []
    for status in ('queued', 'in_progress', 'waiting', 'requested', 'pending'):
        active.extend(api.pages('actions/runs?status=' + status, 'workflow_runs'))
    if any(row.get('path', '').split('@')[0] == '.github/workflows/' + DIRECT for row in active):
        return {'retained': 'A direct release is queued or running; live evidence may change'}
    active_refs = {row['head_branch'] for row in active}

    rows = []
    for branch in branches:
        name, sha = branch['name'], branch['commit']['sha']
        latest = api.pages('actions/runs?branch=' + urllib.parse.quote(name, safe=''), 'workflow_runs')
        latest.sort(key=lambda row: row.get('updated_at') or row['created_at'], reverse=True)
        failed = {name} if latest and latest[0]['conclusion'] not in {'success', 'skipped', None} else set()
        allowed, reason = eligible(branch, approved, live, open_refs, active_refs, failed)
        row = {'branch': name, 'sha': sha, 'eligible': allowed, 'reason': reason, 'deleted': False}
        if allowed and apply:
            if api.ref(APPROVED) != approved:
                raise RuntimeError('Approved branch moved during cleanup; stop and retry from fresh evidence')
            current = api.api('branches/' + urllib.parse.quote(name, safe=''))
            fresh_pulls = api.pages('pulls?state=open')
            used = any(
                pull['base']['ref'] == name or (
                    pull['head']['repo']
                    and pull['head']['repo']['full_name'] == api.repo
                    and pull['head']['ref'] == name
                )
                for pull in fresh_pulls
            )
            recent = api.pages('actions/runs?branch=' + urllib.parse.quote(name, safe=''), 'workflow_runs')
            recent.sort(key=lambda item: item.get('updated_at') or item['created_at'], reverse=True)
            if (used or current['protected'] or current['commit']['sha'] != sha
                or any(item['status'] != 'completed' for item in recent)
                or (recent and recent[0]['conclusion'] not in {'success', 'skipped'})):
                row['reason'] = 'branch gained work or protection during cleanup'
            else:
                result = git(
                    'push', '--force-with-lease=refs/heads/' + name + ':' + sha,
                    'origin', ':refs/heads/' + name, check=False
                )
                if result.returncode:
                    raise RuntimeError('Atomic branch deletion failed; branch retained: ' + name)
                row['deleted'] = True
        rows.append(row)
    return {'approvedSha': approved, 'live': live, 'branches': rows}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['integrate', 'pending-updates', 'reconcile', 'cleanup', 'admit-worker'])
    parser.add_argument('--pr', type=int)
    parser.add_argument('--run', type=int)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    api = GitHub()
    if args.command == 'admit-worker':
        result = admit_worker(api)
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            output.write('admitted=' + str(result['admitted']).lower() + '\n')
            output.write('state=' + result['state'] + '\n')
            if result.get('admitted'):
                output.write('admission_id=' + result['admission']['admissionId'] + '\n')
                output.write('resources=' + json.dumps(result['admission']['resources'], separators=(',', ':')) + '\n')
    elif args.command == 'integrate':
        result = integrate(api, args.pr)
    elif args.command == 'pending-updates':
        result = pending_updates(api)
    elif args.command == 'reconcile':
        result = reconcile(api, args.run)
    else:
        result = cleanup(api, args.apply)
    if args.output:
        args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
)


def git(*args, check=True):
    return subprocess.run(['git', *args], check=check, text=True, capture_output=True)


def ancestor(before, after):
    result = git('merge-base', '--is-ancestor', before, after, check=False)
    if result.returncode not in (0, 1):
        raise RuntimeError(result.stderr)
    return result.returncode == 0


def eligible(branch, approved, live, open_refs, active_refs, failed_refs):
    name, sha = branch['name'], branch['commit']['sha']
    if name in KEEP or branch.get('protected'):
        return False, 'canonical/protected branch'
    if name in open_refs:
        return False, 'open pull request uses branch as source or base'
    if name in active_refs:
        return False, 'workflow still running'
    if name in failed_refs:
        return False, 'latest branch workflow failed or was cancelled'
    if not ancestor(sha, approved):
        return False, 'unique history not preserved in approved changes'
    if not live or not all(ancestor(sha, row['revision']) for row in live):
        return False, 'not covered by every live web application revision'
    return True, 'preserved in approved changes and every live web revision'


class GitHub:
    def __init__(self):
        self.repo = os.environ['GITHUB_REPOSITORY']
        self.token = os.environ['GH_TOKEN']
        self.root = 'https://api.github.com/repos/' + self.repo

    def api(self, path, data=None, method=None):
        payload = json.dumps(data).encode() if data is not None else None
        request = urllib.request.Request(self.root + '/' + path, data=payload,
            method=method or ('POST' if payload is not None else 'GET'), headers={
                'Authorization': 'Bearer ' + self.token,
                'Accept': 'application/vnd.github+json',
                'Content-Type': 'application/json', 'User-Agent': 'legend-release-lifecycle'})
        try:
            with urllib.request.urlopen(request, timeout=45) as response:
                body = response.read()
                return json.loads(body) if body else None
        except urllib.error.HTTPError as error:
            # Do not log tokens, response bodies or environment dumps.
            raise RuntimeError(f'GitHub {request.method} {path}: HTTP {error.code}') from None

    def pages(self, path, key=None):
        rows = []
        for page in range(1, 101):
            result = self.api(path + ('&' if '?' in path else '?') + f'per_page=100&page={page}')
            values = result[key] if key else result
            rows.extend(values)
            if len(values) < 100:
                return rows
        raise RuntimeError('Pagination limit reached; refusing incomplete branch evidence')

    def ref(self, name):
        branch = self.api('branches/' + urllib.parse.quote(name, safe=''))
        value = (branch.get('commit') or {}).get('sha')
        if not SHA.fullmatch(value or ''):
            raise RuntimeError('Malformed branch revision')
        return value

    def dispatch(self, workflow, inputs=None):
        self.api('actions/workflows/' + workflow + '/dispatches',
                 {'ref': APPROVED, 'inputs': inputs or {}})

    def text(self, revision, path):
        row = self.api(
            'contents/' + urllib.parse.quote(path, safe='/') +
            '?ref=' + urllib.parse.quote(revision, safe='')
        )
        if row.get('encoding') != 'base64' or not isinstance(row.get('content'), str):
            raise RuntimeError('Candidate control-plane source is unavailable')
        try:
            return base64.b64decode(row['content'], validate=False).decode('utf-8')
        except (ValueError, UnicodeError):
            raise RuntimeError('Candidate control-plane source is malformed') from None

    def status(self, revision, state, description):
        if not SHA.fullmatch(revision or '') or state not in {'pending', 'success', 'failure', 'error'}:
            raise ValueError('Malformed trusted validation status')
        self.api(
            'statuses/' + revision,
            {
                'state': state,
                'context': 'architecture-validation',
                'description': description[:140],
            },
            method='POST',
        )


def live_revisions():
    spec = importlib.util.spec_from_file_location('release_baseline', Path(__file__).with_name('approved-release-baseline.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    # A missing/unreachable app blocks deletion of ALL branches, not just that app.
    return [module.observe(target) for target in module.TARGETS]


def ready(pr, repo, base):
    return (pr['state'] == 'open' and not pr['draft'] and pr['base']['ref'] == base
        and pr['head']['repo'] and pr['head']['repo']['full_name'] == repo
        and pr['head']['ref'] not in KEEP
        and pr['author_association'] in {'OWNER', 'MEMBER', 'COLLABORATOR'})



def approved_head_state(api, pr):
    """Return whether a PR head already contains the exact current approved head."""
    approved = api.ref(APPROVED)
    head = (pr.get("head") or {}).get("sha")
    if not SHA.fullmatch(head or ""):
        raise RuntimeError("Malformed candidate revision")
    if head == approved:
        return {
            "current": True,
            "approved": approved,
            "candidate": head,
            "mergeBase": approved,
            "status": "identical",
        }
    compare = api.api(
        "compare/" + urllib.parse.quote(approved, safe="") + "..." +
        urllib.parse.quote(head, safe="")
    )
    merge_base = (compare.get("merge_base_commit") or {}).get("sha")
    status = compare.get("status")
    return {
        "current": merge_base == approved and status in {"ahead", "identical"},
        "approved": approved,
        "candidate": head,
        "mergeBase": merge_base,
        "status": status,
    }


def sync_candidate_to_current_approved(api, pr):
    """Fast-forward a trusted same-repo candidate by merging approved into it.

    The operation is additive only: no reset, rebase, force-push, or source
    rewrite. A new PR head causes normal exact-head validation to restart.
    """
    state = approved_head_state(api, pr)
    if state["current"]:
        return None
    if not ready(pr, api.repo, APPROVED):
        return {
            "state": "BASE_SYNC_REQUIRED",
            "retained": "Candidate is stale but is not eligible for trusted automatic base sync",
            "pr": pr.get("number"),
            **state,
        }
    try:
        result = api.api(
            "merges",
            {
                "base": pr["head"]["ref"],
                "head": state["approved"],
                "commit_message": (
                    f"Sync current {APPROVED} into PR #{pr['number']} before validation"
                ),
            },
            method="POST",
        )
    except RuntimeError as exc:
        if any(f"HTTP {code}" in str(exc) for code in (409, 422)):
            return {
                "state": "BASE_SYNC_REQUIRED",
                "retained": "Current approved head could not be merged cleanly into candidate",
                "pr": pr["number"],
                **state,
            }
        raise
    fresh = api.api(f"pulls/{pr['number']}")
    synced = (fresh.get("head") or {}).get("sha")
    if not SHA.fullmatch(synced or ""):
        raise RuntimeError("Approved-head synchronization did not produce a candidate revision")
    return {
        "state": "BASE_SYNCED",
        "pr": pr["number"],
        "previousHead": state["candidate"],
        "approvedHead": state["approved"],
        "head": synced,
        "validation": "new synchronize event must validate the synced exact head",
    }


def _assignment_strings(tree, name):
    for node in tree.body:
        if isinstance(node, ast.Assign) and any(
            isinstance(target, ast.Name) and target.id == name
            for target in node.targets
        ):
            if isinstance(node.value, (ast.Tuple, ast.List, ast.Set)):
                return {
                    item.value for item in node.value.elts
                    if isinstance(item, ast.Constant) and isinstance(item.value, str)
                }
    return set()


def _function_source(source, tree, name):
    for node in tree.body:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name == name:
            return ast.get_source_segment(source, node) or ''
    return ''


def repository_ruleset_integrity(api):
    """The protected branch must retain the external safety rails the lifecycle assumes."""
    rows = api.api('rulesets')
    if not isinstance(rows, list):
        return 'Repository ruleset inventory unavailable'
    for row in rows:
        if row.get('enforcement') != 'active' or row.get('target') != 'branch':
            continue
        ruleset_id = row.get('id')
        if type(ruleset_id) is not int:
            continue
        detail = api.api(f'rulesets/{ruleset_id}')
        refs = (detail.get('conditions') or {}).get('ref_name') or {}
        if 'refs/heads/' + APPROVED not in (refs.get('include') or []):
            continue
        if detail.get('bypass_actors'):
            return 'Protected approved branch gained a ruleset bypass actor'
        if detail.get('current_user_can_bypass') not in {None, 'never'}:
            return 'Protected approved branch permits ruleset bypass'
        rules = {rule.get('type'): rule for rule in detail.get('rules') or []}
        required_types = {'deletion', 'non_fast_forward', 'pull_request', 'required_status_checks'}
        missing = required_types - set(rules)
        if missing:
            return 'Protected approved branch ruleset lost: ' + ', '.join(sorted(missing))
        checks = (rules['required_status_checks'].get('parameters') or {})
        contexts = {
            row.get('context') for row in checks.get('required_status_checks') or []
            if row.get('context')
        }
        if checks.get('strict_required_status_checks_policy') is not True:
            return 'Protected approved branch no longer requires strict up-to-date checks'
        if 'architecture-validation' not in contexts:
            return 'Protected approved branch lost trusted architecture-validation requirement'
        merge = rules['pull_request'].get('parameters') or {}
        if merge.get('allowed_merge_methods') != ['merge']:
            return 'Protected approved branch merge method drifted from canonical merge-only policy'
        return None
    return 'Active approved-branch protection ruleset is missing'


def candidate_control_plane_integrity(api, pr, names):
    """Read candidate control files as data from trusted base code; never execute them."""
    settings = repository_ruleset_integrity(api)
    if settings:
        return settings
    if not any(VALIDATION_AUTHORITY.release_control_authority_path(name) for name in names):
        return None

    head = pr['head']['sha']
    paths = {
        'validation': 'scripts/validation-resume.py',
        'lifecycle': 'scripts/release-lifecycle.py',
        'lifecycle_workflow': '.github/workflows/legend-release-lifecycle.yml',
        'direct_workflow': '.github/workflows/all-intentional-direct-release-20260918.yml',
        'architecture_workflow': '.github/workflows/masterapp-platform-architecture-validation.yml',
        'step5_workflow': '.github/workflows/step5-isolated-conversion-mapping-validation.yml',
        'step6_workflow': '.github/workflows/step6-openai-ads-execution-validation.yml',
        'step78_workflow': '.github/workflows/steps7-8-governed-advertising-validation.yml',
        'security_workflow': '.github/workflows/approved-release-security-validation.yml',
    }
    try:
        source = {key: api.text(head, path) for key, path in paths.items()}
        validation_tree = ast.parse(source['validation'])
        lifecycle_tree = ast.parse(source['lifecycle'])
    except (RuntimeError, SyntaxError):
        return 'Candidate release-control authority cannot be parsed from exact head'

    assignments = {}
    for node in validation_tree.body:
        if isinstance(node, ast.Assign):
            for target in node.targets:
                if isinstance(target, ast.Name) and isinstance(node.value, ast.Constant):
                    assignments[target.id] = node.value.value
    if assignments.get('TRUSTED_PR_BASE') != APPROVED:
        return 'Candidate changed the sole approved release branch authority'
    if assignments.get('DIRECT_RELEASE_WORKFLOW') != DIRECT:
        return 'Candidate changed the sole approved direct-release workflow authority'

    candidate_paths = _assignment_strings(validation_tree, 'LIFECYCLE_AUTHORITY_PATHS')
    required_paths = set(VALIDATION_AUTHORITY.LIFECYCLE_AUTHORITY_PATHS)
    if not required_paths <= candidate_paths:
        return 'Candidate removed protected lifecycle authority paths: ' + ', '.join(sorted(required_paths - candidate_paths))

    predicate = _function_source(source['validation'], validation_tree, 'release_control_authority_path')
    if not all(token in predicate for token in (
        'LIFECYCLE_AUTHORITY_PATHS', 'PACKAGE_AUTHORITY_PATHS', 'RELEASE_EXECUTION_CONTROL_INPUTS'
    )):
        return 'Candidate weakened canonical release-control authority classification'

    topology = _function_source(source['validation'], validation_tree, 'required_validation_topology')
    if not all(token in topology for token in (
        'release_control_authority_change',
        'release_control_authority_path',
        'required.add(security)',
    )):
        return 'Candidate release-control changes no longer require canonical security validation'

    candidate_validation_source = _function_source(source['lifecycle'], lifecycle_tree, 'candidate_validation')
    if not all(token in candidate_validation_source for token in (
        'VALIDATION_AUTHORITY.required_validation_topology(names)',
        "run.get('event') != 'pull_request'",
        "latest[path].get('status') != 'completed'",
        "latest[path].get('conclusion') != 'success'",
    )):
        return 'Candidate weakened exact-head merge validation'

    guard_source = _function_source(source['lifecycle'], lifecycle_tree, 'candidate_control_plane_integrity')
    sync_source = _function_source(source['lifecycle'], lifecycle_tree, 'sync_candidate_to_current_approved')
    merge_source = _function_source(source['lifecycle'], lifecycle_tree, 'merge_validated')
    if not guard_source or 'candidate_control_plane_integrity(api, pr, names)' not in merge_source:
        return 'Candidate removed trusted control-plane integrity enforcement'
    if merge_source.find('candidate_control_plane_integrity(api, pr, names)') > merge_source.find("pulls/{pr['number']}/merge"):
        return 'Candidate moved control-plane integrity enforcement after merge'
    if not sync_source or not all(token in sync_source for token in (
        'approved_head_state(api, pr)',
        '"merges"',
        '"base": pr["head"]["ref"]',
        '"head": state["approved"]',
        '"state": "BASE_SYNCED"',
    )):
        return 'Candidate weakened automatic current-approved-head synchronization'
    if 'base_state = approved_head_state(api, pr)' not in merge_source:
        return 'Candidate removed final approved-head freshness guard before merge'

    lifecycle_workflow = source['lifecycle_workflow']
    if not all(token in lifecycle_workflow for token in (
        'pull_request_target:',
        'ref: legend/approved-changes',
        'contents: write',
        'pull-requests: write',
        'actions: write',
        'statuses: write',
        'python3 scripts/release-lifecycle.py integrate --pr "$PR_NUMBER"',
    )):
        return 'Candidate weakened trusted protected-branch lifecycle execution'

    direct_workflow = source['direct_workflow']
    for token in (
        'cancel-in-progress: false',
        "if: github.ref == 'refs/heads/legend/approved-changes'",
        'Verify selected authority belongs to protected event history',
        'Prepare complete immutable release transaction',
        'Reconcile complete immutable release transaction',
        'Verify every deployed target and collect all failures',
        'Enforce complete direct deployment outcome',
        'Retain exact approved release receipt',
        'Reconcile terminal release resource disposition',
        'Preserve terminal release resource disposition',
        'release-state-receipt:',
    ):
        if token not in direct_workflow:
            return 'Candidate direct-release workflow lost required invariant: ' + token

    architecture = source['architecture_workflow']
    if 'name: architecture-validation' not in architecture and 'name: candidate-architecture-validation' not in architecture:
        return 'Candidate architecture workflow lost its canonical validation job'
    if 'Run branch lifecycle safety contracts' not in architecture:
        return 'Candidate architecture workflow stopped exercising lifecycle contracts'

    validation_workflows = {
        'architecture': architecture,
        'step5': source['step5_workflow'],
        'step6': source['step6_workflow'],
        'step78': source['step78_workflow'],
        'security': source['security_workflow'],
    }
    for label, workflow in validation_workflows.items():
        if (
            'approved-head-preflight:' not in workflow
            or 'Verify candidate contains current approved head' not in workflow
            or 'ref: ${{ github.event.pull_request.head.sha || github.sha }}' not in workflow
            or 'persist-credentials: false' not in workflow
            or 'approved-head-preflight \\' not in workflow
            or 'needs: approved-head-preflight' not in workflow
        ):
            return f'Candidate {label} validator lost canonical approved-head preflight'
    if architecture.count('needs: approved-head-preflight') < 3:
        return 'Candidate architecture validator allows package/probe work before approved-head preflight'

    security_trigger = source['security_workflow'].split('concurrency:', 1)[0]
    if 'pull_request:' not in security_trigger or 'branches: [legend/approved-changes]' not in security_trigger:
        return 'Candidate security validation no longer covers approved-branch pull requests'
    if '\n    paths:' in security_trigger or '\n    paths-ignore:' in security_trigger:
        return 'Candidate security validation can be skipped by release-control path filtering'
    return None


def publish_trusted_validation_status(api, revision, state, detail):
    descriptions = {
        'pending': 'Trusted release authority is waiting for exact-head validation',
        'success': 'Trusted release authority and exact-head validation passed',
        'failure': 'Trusted release authority blocked unsafe control-plane drift',
    }
    api.status(revision, state, descriptions[state] if not detail else detail)


def candidate_validation(api, pr):
    """Require every workflow selected by the canonical topology to be green.

    Child-level preservation belongs to validation-resume.py. Lifecycle never
    reinterprets a failed parent workflow, carries a second step list, or accepts
    partial validation as merge-ready.
    """
    head = pr['head']['sha']
    runs = api.pages('actions/runs?head_sha=' + head, 'workflow_runs')
    latest = {}
    for run in sorted(
        runs,
        key=lambda row: (row.get('created_at', ''), row.get('id', 0)),
        reverse=True,
    ):
        if run.get('head_sha') != head or run.get('event') != 'pull_request':
            continue
        latest.setdefault(run['path'].split('@')[0], run)

    files = api.pages(f"pulls/{pr['number']}/files")
    names = [row['filename'] for row in files if row.get('filename')]
    required = VALIDATION_AUTHORITY.required_validation_topology(names)['required']

    failed = [
        path for path in required
        if path not in latest
        or latest[path].get('status') != 'completed'
        or latest[path].get('conclusion') != 'success'
    ]
    if not failed:
        return None
    return 'Awaiting successful exact-head validation: ' + ', '.join(sorted(failed))


def automatic_release_targets(api, pr):
    files = api.pages(f"pulls/{pr['number']}/files")
    names = [row.get('filename') for row in files if row.get('filename')]
    return VALIDATION_AUTHORITY.release_targets_for_paths(names)


def automatic_release_inputs(pr, release_sha, targets, *, source_merge_sha=None):
    return {
        'automatic': 'true',
        'source_pr': str(pr['number']),
        'validated_sha': pr['head']['sha'],
        'source_merge_sha': source_merge_sha or release_sha,
        'merge_sha': release_sha,
        'targets_json': json.dumps(list(targets), separators=(',', ':')),
    }


def merge_validated(api, pr):
    base_state = approved_head_state(api, pr)
    if not base_state["current"]:
        publish_trusted_validation_status(
            api, pr["head"]["sha"], "pending",
            "Candidate must contain the current approved head before validation can authorize merge",
        )
        return {
            "state": "BASE_SYNC_REQUIRED",
            "retained": "Candidate does not contain current approved head",
            "pr": pr["number"],
            **base_state,
        }

    files = api.pages(f"pulls/{pr['number']}/files")
    names = [row.get('filename') for row in files if row.get('filename')]
    head = pr['head']['sha']

    integrity = candidate_control_plane_integrity(api, pr, names)
    if integrity:
        publish_trusted_validation_status(api, head, 'failure', integrity)
        return {'state': 'VALIDATING', 'retained': integrity}

    pending = candidate_validation(api, pr)
    if pending:
        publish_trusted_validation_status(api, head, 'pending', '')
        return {'state': 'VALIDATING', 'retained': pending}

    publish_trusted_validation_status(api, head, 'success', '')
    targets = VALIDATION_AUTHORITY.release_targets_for_paths(names)
    control_only = bool(names) and all(
        VALIDATION_AUTHORITY.release_control_only_path(name)
        for name in names
    )
    try:
        result = api.api(f"pulls/{pr['number']}/merge",
            {'merge_method': 'merge', 'sha': pr['head']['sha']}, method='PUT')
    except RuntimeError as exc:
        if any(f"HTTP {code}" in str(exc) for code in (405, 409, 422)):
            return {
                'retained': 'Validated PR is not currently mergeable; source branch retained',
                'pr': pr['number'],
            }
        raise
    if not result.get('merged'):
        return {
            'retained': 'Merge did not complete; source branch retained',
            'pr': pr['number'],
        }

    # Validation success is the publication handoff. Application-affecting merges
    # immediately enter the sole direct-release workflow with scope derived from
    # the validated PR. No second authorization command or hand-maintained target
    # table exists between merge and deployment.
    release_result = None
    if targets and not control_only:
        # Integration and dispatch are separate phases of the same serialized
        # lifecycle invocation. The refresh/reconcile phase dispatches once;
        # do not dispatch here then rediscover an eventually-visible run below.
        release_result = {'releaseRecovery': 'queued for the refreshed reconciliation phase', 'targets': list(targets)}
    else:
        # The merge commit does not exist in this runner's local checkout yet.
        # Historical recovery is intentionally deferred to the workflow's
        # refresh -> reconcile phase, which fetches and resets to the newly
        # approved commit before inspecting first-parent authorization history.
        release_result = {
            'releaseRecovery': 'deferred until refreshed approved checkout',
        }

    if any(row.get('filename') == '.github/workflows/deployment-diagnostics.yml' for row in files):
        api.dispatch('deployment-diagnostics.yml')
    return {
        'state': 'MERGED',
        'transitions': ['MERGE_READY', 'MERGED'],
        'mergedPr': pr['number'],
        'sha': result['sha'],
        'releaseDispatched': bool(release_result and 'directRelease' in release_result),
        'automaticRelease': bool(targets and not control_only),
        'targets': list(targets),
        'release': release_result,
    }


def integrate(api, number):
    if staging_only():
        return {'retained': 'Validation-only staging hold; no integration, dispatch or cleanup'}
    pr = api.api(f'pulls/{number}')

    # pull_request_target events can queue behind another lifecycle run. If that
    # earlier run already merged this exact trusted PR, the delayed event is a
    # replay, not a new integration failure. Prove the recorded merge is in the
    # current approved lineage and return without dispatching anything again.
    merged_sha = pr.get('merge_commit_sha')
    already_integrated = (
        pr.get('state') == 'closed'
        and pr.get('merged_at')
        and pr.get('base', {}).get('ref') == APPROVED
        and pr.get('head', {}).get('repo')
        and pr['head']['repo'].get('full_name') == api.repo
        and pr.get('author_association') in {'OWNER', 'MEMBER', 'COLLABORATOR'}
        and SHA.fullmatch(merged_sha or '')
    )
    if already_integrated:
        approved = api.ref(APPROVED)
        if ancestor(merged_sha, approved):
            return {
                'integration': 'already merged exact PR event preserved as no-op',
                'mergedPr': number,
                'sha': merged_sha,
                'replayed': True,
                'releaseDispatched': False,
            }

    if not ready(pr, api.repo, APPROVED):
        raise RuntimeError('Only ready, same-repository collaborator PRs into approved changes can be integrated')
    synced = sync_candidate_to_current_approved(api, pr)
    if synced is not None:
        return synced
    return merge_validated(api, pr)


def pending_updates(api):
    if staging_only():
        return {'retained': 'Validation-only staging hold; no integration, dispatch or cleanup'}
    # GitHub state may advance while a serialized lifecycle run is waiting.
    # Refresh the canonical approved ref before any ancestry decision so a newly
    # merged trusted PR can never appear as an unknown local commit.
    refreshed = git('fetch', '--no-tags', '--prune', 'origin',
                    f'+refs/heads/{APPROVED}:refs/remotes/origin/{APPROVED}', check=False)
    if refreshed.returncode:
        raise RuntimeError(refreshed.stderr)
    # Scheduled reconciliation also covers bot-created PR events and corrections
    # pushed to a retained branch after its previous approved PR was merged.
    #
    # A retained/unvalidated PR must never starve another fully validated PR.
    # Preserve its reason and continue scanning; stop only after a mutation
    # actually succeeds.
    pulls = api.pages('pulls?state=open&base=' + urllib.parse.quote(APPROVED, safe=''))
    closed = api.pages('pulls?state=closed&base=' + urllib.parse.quote(APPROVED, safe=''))
    retained_candidates = []
    for pr in reversed(pulls):
        if ready(pr, api.repo, APPROVED):
            # The open-PR collection is only a discovery snapshot. A serialized
            # lifecycle can wait behind another merge long enough for that PR's
            # draft/state/association/base/head readiness to change. Re-read the
            # exact PR before mutation; stale discovery must retain and continue,
            # never abort reconciliation or starve a later validated candidate.
            fresh = api.api(f"pulls/{pr['number']}")
            if not ready(fresh, api.repo, APPROVED):
                retained_candidates.append({
                    'pr': pr['number'],
                    'reason': 'PR readiness changed after discovery; retained without mutation',
                })
                continue
            synced = sync_candidate_to_current_approved(api, fresh)
            if synced is not None:
                return synced
            result = merge_validated(api, fresh)
            if 'retained' not in result:
                return result
            retained_candidates.append({
                'pr': pr['number'],
                'reason': result['retained'],
            })
            continue
        if (pr['state'] == 'open' and not pr['draft'] and pr['user']['login'] == 'github-actions[bot]'
            and pr['head']['repo'] and pr['head']['repo']['full_name'] == api.repo
            and pr['head']['ref'] not in KEEP):
            prior = [old for old in closed if old.get('merged_at') and
                old['author_association'] in {'OWNER', 'MEMBER', 'COLLABORATOR'} and
                old['head']['repo'] and old['head']['repo']['full_name'] == api.repo and
                old['head']['ref'] == pr['head']['ref']]
            if any(ancestor(old['head']['sha'], pr['head']['sha']) for old in prior):
                result = merge_validated(api, pr)
                if 'retained' not in result:
                    return result
                retained_candidates.append({
                    'pr': pr['number'],
                    'reason': result['retained'],
                })
    open_names = {p['head']['ref'] for p in pulls if p['head']['repo'] and p['head']['repo']['full_name'] == api.repo}
    branches = {b['name']: b for b in api.pages('branches')}
    approved = api.ref(APPROVED)
    for pr in closed:
        name = pr['head']['ref']
        if (not pr.get('merged_at') or name in KEEP or name in open_names or name not in branches
            or not pr['head']['repo'] or pr['head']['repo']['full_name'] != api.repo
            or pr['author_association'] not in {'OWNER', 'MEMBER', 'COLLABORATOR'}):
            continue
        head = branches[name]['commit']['sha']
        if ancestor(head, approved):
            continue
        # Force-rewritten/unrelated work never inherits the prior PR's readiness.
        if not ancestor(pr['head']['sha'], head):
            continue
        try:
            correction = api.api('pulls', {'head': name, 'base': APPROVED,
                'title': 'Continue approved release corrections from ' + name,
                'body': 'Automatically carries new commits on the retained source branch after its previous approved PR. '
                        'Owning validation and the approved direct-release authority will re-evaluate only invalidated evidence; branch deletion remains gated.'})
        except RuntimeError as exc:
            if 'HTTP 403' not in str(exc):
                raise
            retained_candidates.append({
                'pr': pr['number'],
                'branch': name,
                'reason': 'Correction PR creation blocked; unique branch history retained without integration',
            })
            continue
        # The previous collaborator PR authorizes review, not skipping fresh CI.
        return {'correctionPr': correction['number'], 'retained': 'Fresh exact-head validation required'}
    result = {'integration': 'no validated ready changes or retained-branch corrections'}
    if retained_candidates:
        result['retainedCandidates'] = retained_candidates
    return result


def release_targets(revision):
    """Read the immutable target scope authorized by an approved release commit."""
    result = git('show', revision + ':Docs/releases/direct-release-request.json', check=False)
    if result.returncode:
        return set()
    try:
        request = json.loads(result.stdout)
    except (TypeError, ValueError, json.JSONDecodeError):
        return set()
    if request.get('releaseMode') != 'approved-only':
        return set()
    if 'targets' not in request:
        return set(VALIDATION_AUTHORITY.release_name_map())
    targets = request.get('targets')
    if not isinstance(targets, list) or any(not isinstance(item, str) for item in targets):
        return set()
    return set(targets)


def successful_release(api, run, app=None):
    """Accept only complete approved direct-release receipts.

    Validation workflows may authorize a merge, but only the single direct-release
    workflow can establish deployed application provenance.
    """
    if run['status'] != 'completed' or run['conclusion'] != 'success':
        return False
    path = run['path'].split('@')[0]
    if path != '.github/workflows/' + DIRECT:
        return False
    if run.get('head_repository', {}).get('full_name') != api.repo:
        return False
    if run.get('head_branch') != APPROVED:
        return False
    jobs = api.pages(f"actions/runs/{run['id']}/jobs?filter=latest", 'jobs')
    required = {'discover-live', 'release'}
    passed = {job['name'] for job in jobs if job['conclusion'] == 'success'}
    if not required <= passed:
        return False
    release_job = next(job for job in jobs if job['name'] == 'release')
    steps = release_job.get('steps', [])
    if not any(step['name'] == 'Verify every deployed target and collect all failures'
               and step['conclusion'] == 'success' for step in steps):
        return False
    if not any(step['name'] == 'Enforce complete direct deployment outcome'
               and step['conclusion'] == 'success' for step in steps):
        return False
    if app is None:
        return True
    # A selected target may have been intentionally preserved because it was
    # already live at APPLICATION_RELEASE_SHA. Final live proof + enforcement is
    # authoritative; requiring the deploy step itself would reject safe retries.
    release_name = _canonical_release_name(app)
    return bool(release_name and release_name in release_targets(run.get('head_sha', '')))

def direct_only_request(sha):
    # A one-release exception bound to the exact commit that changes the request.
    # Two authorized shapes exist:
    # 1) a validated PR merge that carries the request in the merge itself; or
    # 2) a single-parent control-only authorization immediately after that merge.
    path = VALIDATION_AUTHORITY.RELEASE_REQUEST_PATH
    lineage = git('rev-list', '--parents', '-n', '1', sha, check=False)
    parts = lineage.stdout.strip().split() if not lineage.returncode else []
    if not parts or parts[0] != sha or len(parts) not in {2, 3}:
        return False

    result = git('show', sha + ':' + path, check=False)
    if result.returncode:
        return False
    try:
        request = json.loads(result.stdout)
    except (TypeError, ValueError, json.JSONDecodeError):
        return False
    if request.get('releaseMode') != 'approved-only':
        return False

    if len(parts) == 2:
        changed = git('diff-tree', '--no-commit-id', '--name-only', '-r', sha + '^1', sha, check=False)
        if changed.returncode or changed.stdout.splitlines() != [path]:
            return False
        return True

    # Product merge authorization: compare the request object directly against
    # the first parent. This avoids merge diff simplification hiding a request
    # change when the same merge also carries application/migration files.
    prior = git('show', sha + '^1:' + path, check=False)
    if prior.returncode:
        return True
    return prior.stdout != result.stdout


def direct_release_approved_pr(api, sha):
    """Resolve an exact approved-only release revision to its validated product PR.

    Release authorization may be rewritten several times, and release-control-only
    PRs may be merged between the last product PR and the final authorization.
    Walk backward through a bounded chain of request-only commits and release-control
    PR merges until the nearest product-changing merged PR is reached. Any malformed
    lineage, non-control single-parent commit, excessive chain, or ambiguous PR map
    fails closed.
    """
    if not direct_only_request(sha):
        return None

    current = sha
    request_path = VALIDATION_AUTHORITY.RELEASE_REQUEST_PATH
    for _ in range(16):
        lineage = git('rev-list', '--parents', '-n', '1', current, check=False)
        parts = lineage.stdout.strip().split() if not lineage.returncode else []
        if not parts or parts[0] != current:
            return None

        if len(parts) == 3:
            merged = current
        elif len(parts) == 2:
            parent = parts[1]
            changed = git('diff-tree', '--no-commit-id', '--name-only', '-r',
                          current + '^1', current, check=False)
            if changed.returncode or changed.stdout.splitlines() != [request_path]:
                return None
            if direct_only_request(parent):
                current = parent
                continue
            merged = parent
        else:
            return None

        pulls = api.pages('commits/' + merged + '/pulls')
        matches = [p for p in pulls if p.get('merged_at') and p.get('merge_commit_sha') == merged
                   and p.get('base', {}).get('ref') == APPROVED]
        if len(matches) != 1:
            return None
        pr = matches[0]
        files = api.pages(f"pulls/{pr['number']}/files")
        names = {row.get('filename') for row in files}
        if names and all(
            VALIDATION_AUTHORITY.release_control_only_path(name)
            for name in names if name
        ):
            merge_lineage = git('rev-list', '--parents', '-n', '1', merged, check=False)
            merge_parts = merge_lineage.stdout.strip().split() if not merge_lineage.returncode else []
            if len(merge_parts) != 3 or merge_parts[0] != merged:
                return None
            current = merge_parts[1]
            continue
        return pr

    return None


def authorization_release_proven(api, authorization_sha, targets):
    """Accept a successful direct release bound to the exact authorization commit.

    Older release workflow generations wrote a generic receipt keyed by the
    approved merge/authorization SHA rather than the application source SHA.
    The workflow run itself is durable proof only when it is the sole canonical
    direct-release workflow, targets this approved authorization, and its final
    live verification and enforcement both succeeded.
    """
    runs = api.pages(
        'actions/runs?head_sha=' + urllib.parse.quote(authorization_sha, safe=''),
        'workflow_runs',
    )
    for run in runs:
        if run.get('head_sha') != authorization_sha:
            continue
        if not successful_release(api, run):
            continue
        authorized = release_targets(authorization_sha)
        if targets <= authorized:
            return True
    return False


def pending_legacy_release_authorization(api, approved):
    """Resolve only the newest valid explicit authorization on first-parent history.

    Automatic application PRs do not use this path. It exists only to carry a
    previously authorized release across release-control-only correction merges.

    Scan the literal first-parent commit chain rather than path-filtered history:
    Git path simplification must never hide a merge that imports a new release
    request from its second parent. Once the newest valid authorization is found,
    it is authoritative. If already released, stop; never resurrect an older
    superseded authorization.
    """
    history = git('rev-list', '--first-parent', approved, check=False)
    if history.returncode:
        raise RuntimeError('Unable to inspect approved first-parent release authorization history')

    for sha in history.stdout.splitlines():
        if not SHA.fullmatch(sha) or not direct_only_request(sha):
            continue

        pr = direct_release_approved_pr(api, sha)
        if pr is None:
            return {
                'retained': 'Historical release authorization cannot be bound to one validated approved PR'
            }

        pending = candidate_validation(api, pr)
        if pending:
            return {'retained': pending}

        targets = release_targets(sha)
        if not targets:
            return {'retained': 'Historical release authorization has no canonical target scope'}

        revision = pr['head']['sha']
        if (
            all(release_proven(api, revision, app=target) for target in targets)
            or authorization_release_proven(api, sha, targets)
        ):
            return None

        return {
            'authorizationSha': sha,
            'applicationRevision': revision,
            'targets': sorted(targets),
            'sourcePr': pr['number'],
        }

    return None


def _validated_package_evidence(api, revision):
    identity = PACKAGE_AUTHORITY.package_identity(revision)
    return VALIDATION_AUTHORITY.compute_validated_package_evidence(
        api.repo,
        revision,
        identity,
    )


def _package_backfill_running(api, approved):
    runs = api.pages(
        'actions/runs?head_sha=' + urllib.parse.quote(approved, safe=''),
        'workflow_runs',
    )
    return any(
        run.get('path', '').split('@')[0] == '.github/workflows/' + PACKAGE_VALIDATION
        and run.get('event') == 'workflow_dispatch'
        and run.get('status') != 'completed'
        for run in runs
    )


def _package_backfill_preflight(api, revision, approved):
    """Use the package builder's canonical eligibility proof before dispatch."""
    return VALIDATION_AUTHORITY.compute_package_backfill_plan(
        api.repo,
        revision,
        approved,
    )


def pending_automatic_releases(api, approved):
    """Derive the durable queue from approved first-parent PR authorization history.

    Keep the newest authorization for each target; never roll a newer target back
    to an older queued head. A satisfied or failed newest head suppresses only its
    own targets, so it cannot erase another application's pending publication.
    No mutable queue or second authorization store is introduced.
    """
    history = git('rev-list', '--first-parent', approved, check=False)
    if history.returncode:
        raise RuntimeError('Unable to inspect approved first-parent automatic release history')
    pending = []
    covered = set()
    merges = {}
    for pr in api.pages('pulls?state=closed&base=' + urllib.parse.quote(APPROVED, safe='')):
        if pr.get('merged_at') and pr.get('base', {}).get('ref') == APPROVED:
            merges.setdefault(pr.get('merge_commit_sha'), []).append(pr)
    all_targets = {row['releaseName'] for row in VALIDATION_AUTHORITY.RELEASE_TARGETS.values()}
    for sha in history.stdout.splitlines():
        if covered == all_targets:
            break  # older authorizations cannot change any target's frontier
        if not SHA.fullmatch(sha):
            continue
        matches = merges.get(sha, [])
        if len(matches) != 1:
            # Closed-PR collection snapshots can lag immediately after a merge.
            # Resolve the exact first-parent commit directly before allowing an
            # older queued candidate to become the apparent frontier.
            associated = api.pages('commits/' + sha + '/pulls')
            matches = [
                candidate for candidate in associated
                if candidate.get('merged_at')
                and candidate.get('merge_commit_sha') == sha
                and candidate.get('base', {}).get('ref') == APPROVED
            ]
        if len(matches) != 1:
            continue
        pr = matches[0]
        files = api.pages(f"pulls/{pr['number']}/files")
        names = [row['filename'] for row in files if row.get('filename')]
        targets = VALIDATION_AUTHORITY.release_targets_for_paths(names)
        if not targets:
            continue
        revision = pr.get('head', {}).get('sha')
        if not SHA.fullmatch(revision or ''):
            raise RuntimeError('Automatic release source PR has invalid validated head identity')
        control_only = bool(names) and all(
            VALIDATION_AUTHORITY.release_control_only_path(name) for name in names)
        if control_only and not _validated_package_evidence(api, revision).get('reusable'):
            continue
        overlap = covered.intersection(targets)
        covered.update(targets)
        if overlap == set(targets):
            continue
        if overlap:
            # Do not silently drop the untouched portion of an older atomic
            # transaction, and do not invent authorization to split it either.
            pending.append({'authorizationSha': sha, 'applicationRevision': revision,
                            'targets': list(targets), 'sourcePr': pr['number'],
                            'retained': 'Partially superseded atomic release needs a validated combined successor',
                            'supersededTargets': sorted(overlap)})
            continue
        package_evidence = _validated_package_evidence(api, revision)
        publication_revision = package_evidence.get('revision', revision) if package_evidence.get('reusable') else revision
        if all(release_proven(api, publication_revision, app=target) for target in targets):
            continue
        row = {'authorizationSha': sha, 'applicationRevision': revision,
               'targets': list(targets), 'sourcePr': pr['number']}
        validation = candidate_validation(api, pr)
        if validation:
            row['retained'] = validation
        pending.append(row)
    # Stable FIFO among the independent frontier, irrespective of API ordering.
    return list(reversed(pending))


def direct_release_runs(api):
    return [row for row in api.pages(
        'actions/workflows/' + DIRECT + '/runs?branch=' + urllib.parse.quote(APPROVED, safe=''),
        'workflow_runs')
        if row.get('head_branch') == APPROVED
        and row.get('path', '').split('@')[0] == '.github/workflows/' + DIRECT]


def release_dispatch_identity(pr_number, revision, execution_sha):
    if not SHA.fullmatch(revision or '') or not SHA.fullmatch(execution_sha or ''):
        raise ValueError('Malformed immutable release dispatch identity')
    return f"LEGEND release pr={int(pr_number)} candidate={revision} authority={execution_sha}"


def automatic_release_admission(pr, approved, runs):
    """Single fail-closed admission predicate; no independent workflow gate map."""
    active = [row for row in runs if row.get('status') != 'completed']
    if active:
        return {'state': 'WAITING_FOR_CONFLICTING_RELEASE', 'retained': 'Queued in approved PR history until active transaction completes',
                'blockingRuns': [row['id'] for row in active]}
    identity = release_dispatch_identity(pr['number'], pr['head']['sha'], approved)
    attempted = [row for row in runs if row.get('display_title') == identity
                 or (row.get('head_sha') == approved
                     and not row.get('display_title', '').startswith('LEGEND release pr='))]
    if attempted:
        return {'state': 'FAILED_NEEDS_REPAIR', 'retained': 'Exact candidate/authority release already attempted; reconcile or repair without upload replay'}
    return None


def admit_automatic_release(api, pr, approved, targets, *, source_merge_sha=None, runs=None):
    """One admission path for a freshly merged head and a recovered queued head.

    Called under the lifecycle workflow mutex. The publisher retains its global
    transaction mutex until durable resource reservations cover every mutation.
    """
    runs = direct_release_runs(api) if runs is None else runs
    blocked = automatic_release_admission(pr, approved, runs)
    if blocked:
        return blocked
    identity = release_dispatch_identity(pr['number'], pr['head']['sha'], approved)
    dispatched = getattr(api, '_dispatch_handoffs', set())
    if identity in dispatched:
        return {'state': 'RELEASE_DISPATCHED', 'retained': 'This lifecycle invocation already handed off the exact candidate'}
    # Retain the handoff even if GitHub's dispatch response is ambiguous. Only
    # the later workflow/lease reconciliation may decide whether it executed.
    dispatched.add(identity)
    api._dispatch_handoffs = dispatched
    api.dispatch(DIRECT, automatic_release_inputs(pr, approved, targets, source_merge_sha=source_merge_sha))
    return {'state': 'RELEASE_DISPATCHED', 'directRelease': 'automatic validated-merge release', 'targets': list(targets)}


def dispatch_pending_automatic_release(api, approved):
    queue = pending_automatic_releases(api, approved)
    if not queue:
        return None
    runs = direct_release_runs(api)
    retained = []
    for pending in queue:
        if 'retained' in pending:
            retained.append(pending)
            continue
        pr_identity = {'number': pending['sourcePr'], 'head': {'sha': pending['applicationRevision']}}
        blocked = automatic_release_admission(pr_identity, approved, runs)
        if blocked:
            if 'blockingRuns' in blocked:
                return {**blocked, 'pendingCandidates': queue}
            retained.append({**pending, **blocked})
            continue
        package = _validated_package_evidence(api, pending['applicationRevision'])
        if not package.get('reusable'):
            preflight = _package_backfill_preflight(api, pending['applicationRevision'], approved)
            if not preflight.get('allowed'):
                retained.append({
                    **pending,
                    'packageBackfill': 'not dispatched',
                    'packageReason': preflight.get('reason') or package.get('reason'),
                    'retained': 'Historical package backfill is ineligible under current approved application lineage',
                })
                continue
            if _package_backfill_running(api, approved):
                retained.append({**pending, 'packageBackfill': 'already queued or running'})
                continue
            api.dispatch(PACKAGE_VALIDATION, {'package_revision': pending['applicationRevision']})
            return {'state': 'WAITING_FOR_DEPENDENCY', 'packageBackfill': 'dispatched for exact green automatic application revision',
                    'packageReason': package.get('reason'), **pending}
        pr = api.api(f"pulls/{pending['sourcePr']}")
        if not pr:
            retained.append({**pending, 'retained': 'Automatic release recovery could not reload source PR'})
            continue
        if pr.get('head', {}).get('sha') != pending['applicationRevision']:
            retained.append({**pending, 'retained': 'Source PR head changed after queue discovery'})
            continue
        admission = admit_automatic_release(api, pr, approved, tuple(pending['targets']),
                                             source_merge_sha=pending['authorizationSha'], runs=runs)
        return {**admission, **pending}
    return {'state': 'WAITING_FOR_DEPENDENCY', 'retained': 'Pending candidates require proof or repair', 'pendingCandidates': retained}


def dispatch_pending_legacy_release(api, approved):
    pending = pending_legacy_release_authorization(api, approved)
    if not pending:
        return None
    if 'retained' in pending:
        return pending

    package = _validated_package_evidence(api, pending['applicationRevision'])
    if not package.get('reusable'):
        preflight = _package_backfill_preflight(api, pending['applicationRevision'], approved)
        if not preflight.get('allowed'):
            return {
                'state': 'SUPERSEDED' if preflight.get('reason') == 'application_inputs_changed_since_validated_revision' else 'WAITING_FOR_DEPENDENCY',
                'packageBackfill': 'not dispatched',
                'packageReason': preflight.get('reason') or package.get('reason'),
                'retained': 'Historical release package backfill is ineligible under current approved application lineage',
                **pending,
            }
        if _package_backfill_running(api, approved):
            return {
                'packageBackfill': 'already queued or running',
                **pending,
            }
        api.dispatch(PACKAGE_VALIDATION, {
            'package_revision': pending['applicationRevision'],
        })
        return {
            'packageBackfill': 'dispatched for exact green historical application revision',
            'packageReason': package.get('reason'),
            **pending,
        }

    api.dispatch(DIRECT, {
        'automatic': 'false',
        'merge_sha': pending['authorizationSha'],
    })
    return {
        'directRelease': 'recovered nearest still-unreleased historical authorization',
        'packageEvidenceRunId': package.get('runId'),
        **pending,
    }


def release_execution_state(api, run):
    """Derive lifecycle state from the actual canonical worker and child evidence."""
    if successful_release(api, run):
        return 'COMPLETE'
    jobs = api.pages(f"actions/runs/{run['id']}/jobs?filter=latest", 'jobs')
    release_jobs = [job for job in jobs if job.get('name') == 'release']
    steps = release_jobs[0].get('steps', []) if release_jobs else []
    publications = [step for step in steps if step.get('name', '').startswith('Publish canonical target (')]
    if run.get('status') == 'completed':
        artifacts = api.pages(f"actions/runs/{run['id']}/artifacts", 'artifacts')
        names = {item.get('name', '') for item in artifacts if not item.get('expired')}
        intents = {name.removeprefix('legend-release-operation-intent-') for name in names
                   if name.startswith('legend-release-operation-intent-')}
        successes = {name.removeprefix('legend-release-operation-success-') for name in names
                     if name.startswith('legend-release-operation-success-')}
        settled = any(name.startswith('legend-release-disposition-') for name in names)
        if intents - successes and not settled:
            return 'DEPLOYMENT_RECONCILIATION'
        return 'WAITING_FOR_CONFLICTING_RELEASE' if _never_admitted(api, run) else 'FAILED_NEEDS_REPAIR'
    if any(step.get('status') == 'in_progress' for step in publications):
        return 'DEPLOYING'
    if any(step.get('name') == 'Reconcile complete immutable release transaction'
           and step.get('conclusion') == 'success' for step in steps):
        return 'LIVE_PROOF_REQUIRED'
    return 'RELEASE_DISPATCHED'


def reconcile(api, trigger=None):
    """Wake the durable queue on every completion, including failed siblings.

    A failed run is evidence about that candidate, never a veto of all other
    approved work. Dispatch admission still refuses replay of that exact attempt.
    """
    if staging_only():
        return {'release': 'disabled while validation-only staging hold is active'}
    approved = api.ref(APPROVED)
    automatic = dispatch_pending_automatic_release(api, approved)
    if automatic:
        return automatic
    runs = direct_release_runs(api)
    if any(row.get('status') != 'completed' for row in runs):
        return {'release': 'already queued or running'}
    # Legacy explicit authorization remains a separately authorized request shape,
    # not another scheduler. Preserve failed attempts, without blocking the
    # automatic queue above or replaying an ambiguous historical upload.
    if trigger:
        run = api.api(f'actions/runs/{trigger}')
        path = run.get('path', '').split('@')[0]
        if path in {'.github/workflows/' + DIRECT, '.github/workflows/' + PACKAGE_VALIDATION}:
            if run.get('conclusion') != 'success':
                return {'retained': 'Triggered release or package attempt needs reconciliation or repair; no automatic replay'}
    current = [row for row in runs if row.get('head_sha') == approved]
    if current:
        latest = max(current, key=lambda row: (row.get('id', 0), row.get('run_attempt', 1)))
        return {'state': release_execution_state(api, latest),
                'retained': 'Exact approved release already attempted; correction or reconciliation required'}
    historical = dispatch_pending_legacy_release(api, approved)
    if historical:
        return historical
    return {'state': 'READY', 'release': 'no application publication required for exact approved head'}


def _admission_identity(record):
    payload = {key: value for key, value in record.items() if key != 'admissionId'}
    return hashlib.sha256(json.dumps(payload, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def _admission_records(api, run):
    artifacts = api.pages(f"actions/runs/{run['id']}/artifacts", 'artifacts')
    records = []
    for artifact in artifacts:
        name = artifact.get('name', '')
        if not name.startswith('legend-release-admission-'):
            continue
        if artifact.get('expired'):
            raise RuntimeError('Release admission evidence expired; resource disposition must be reconciled')
        with tempfile.TemporaryDirectory(prefix='legend-admission-') as directory:
            VALIDATION_AUTHORITY._download_run_artifact(api.repo, run['id'], name, Path(directory))
            path = Path(directory) / 'operation.json'
            if path.stat().st_size > 32768:
                raise RuntimeError('Oversized admission record')
            record = json.loads(path.read_text())
        if (record.get('schemaVersion') != 1 or record.get('phase') != 'admission'
            or record.get('producingRun') != run['id']
            or record.get('admissionId') != _admission_identity(record)
            or name != 'legend-release-admission-' + record['admissionId']):
            raise RuntimeError('Release admission identity does not match its durable producer')
        VALIDATION_AUTHORITY.selected_release_target_keys(record.get('selectedTargets'))
        if (run.get('event') != 'workflow_dispatch'
            or (run.get('head_repository') or {}).get('full_name', '').lower() != api.repo.lower()
            or not isinstance(record.get('producingAttempt'), int)
            or not 1 <= record['producingAttempt'] <= run.get('run_attempt', 1)
            or record.get('authorizationMode') not in {'automatic', 'explicit'}):
            raise RuntimeError('Untrusted release admission producer or authorization mode')
        pr = api.api(f"pulls/{record['sourcePr']}")
        if (not pr.get('merged_at') or pr.get('base', {}).get('ref') != APPROVED
            or pr.get('head', {}).get('sha') != record.get('authorizedSourceRevision')
            or pr.get('merge_commit_sha') != record.get('sourceMergeSha')
            or not ancestor(record['sourceMergeSha'], record['executionAuthority'])):
            raise RuntimeError('Admission source no longer binds its validated approved PR')
        # Reused immutable bytes retain their producer revision and identity.
        # Recompute compatibility of the actual producing inputs, not an identity
        # relabeled with the newer authorized PR revision. The exact retained
        # producer/artifact binding is independently verified below.
        if not VALIDATION_AUTHORITY.package_inputs_compatible(
                record['applicationRevision'], record['authorizedSourceRevision']):
            raise RuntimeError('Admission authorized source is not equivalent to its immutable package inputs')
        package = VALIDATION_AUTHORITY.compute_validated_package_evidence(
            api.repo, record['applicationRevision'], record['packageIdentity'], allow_equivalent=False)
        if (not package.get('reusable') or package.get('revision') != record.get('applicationRevision')
            or package.get('packageIdentity') != record.get('packageIdentity')):
            raise RuntimeError('Admission immutable package binding is missing or changed')
        paths = [row['filename'] for row in api.pages(f"pulls/{pr['number']}/files") if row.get('filename')]
        routing = False
        if record['authorizationMode'] == 'automatic':
            expected_targets = list(VALIDATION_AUTHORITY.release_targets_for_paths(paths))
        else:
            if not direct_only_request(record['executionAuthority']):
                raise RuntimeError('Admission lacks exact explicit release authorization')
            expected_targets = sorted(release_targets(record['executionAuthority']))
            request = json.loads(git('show', record['executionAuthority'] + ':' + VALIDATION_AUTHORITY.RELEASE_REQUEST_PATH).stdout)
            routing = request.get('cloudflareWebsiteRouting', False)
        expected_resources = VALIDATION_AUTHORITY.release_admission_resources(paths, expected_targets, routing=routing)
        if record['selectedTargets'] != expected_targets or record.get('resources') != expected_resources:
            raise RuntimeError('Admission resource ownership differs from canonical authorized dependencies')
        records.append(record)
    return records


def _never_admitted(api, run):
    # A skipped latest retry cannot erase an earlier entered publication. Every
    # recorded attempt must independently prove downstream jobs never executed.
    for attempt in range(1, run.get('run_attempt', 1) + 1):
        jobs = api.pages(f"actions/runs/{run['id']}/attempts/{attempt}/jobs", 'jobs')
        admission = [job for job in jobs if job.get('name') == 'admission']
        downstream = [job for job in jobs if job.get('name') in {'discover-live', 'release'}]
        if not (admission and len(downstream) == 2
                and all(job.get('conclusion') == 'skipped' for job in downstream)):
            return False
    return True


def _admission_nonmutating_terminal(api, run):
    """Prove a completed admitted run never crossed into a mutable release phase.

    This is intentionally stricter than workflow failure. Every attempt must show
    either a skipped release job, or a transaction-preparation failure with every
    downstream mutation-capable step skipped. Any durable operation intent keeps
    the lease blocking.
    """
    if run.get('status') != 'completed':
        return False
    artifacts = api.pages(f"actions/runs/{run['id']}/artifacts", 'artifacts')
    names = {item.get('name', '') for item in artifacts}
    if any(name.startswith('legend-release-operation-intent-') for name in names):
        return False
    attempts = run.get('run_attempt', 1)
    if type(attempts) is not int or attempts < 1:
        return False
    workflow_path = '.github/workflows/' + DIRECT
    revision = run.get('head_sha', '')
    if not SHA.fullmatch(revision):
        return False
    original = git('show', revision + ':' + workflow_path, check=False)
    if original.returncode or original.stdout != Path(workflow_path).read_text():
        return False  # unknown execution generation cannot prove non-mutation
    for attempt in range(1, attempts + 1):
        jobs = api.pages(f"actions/runs/{run['id']}/attempts/{attempt}/jobs", 'jobs')
        release_jobs = [job for job in jobs if job.get('name') == 'release']
        if len(release_jobs) != 1:
            return False
        release = release_jobs[0]
        if release.get('conclusion') == 'skipped':
            continue
        if not VALIDATION_AUTHORITY._failed_transaction_preparation_without_writes(
                original.stdout, release):
            return False
    return True


def _admission_settled(api, run, record):
    if run.get('status') != 'completed':
        return False
    name = 'legend-release-disposition-' + record['admissionId'] + '-' + str(run.get('run_attempt', 1))
    artifacts = api.pages(f"actions/runs/{run['id']}/artifacts", 'artifacts')
    matching = [row for row in artifacts if row.get('name') == name and not row.get('expired')]
    if len(matching) != 1:
        return False
    with tempfile.TemporaryDirectory(prefix='legend-disposition-') as directory:
        VALIDATION_AUTHORITY._download_run_artifact(api.repo, run['id'], name, Path(directory))
        value = json.loads((Path(directory) / 'release-disposition.json').read_text())
    targets = {row.get('target') for row in value.get('targets', []) if row.get('idle') is True}
    expected = set(VALIDATION_AUTHORITY.selected_release_target_keys(record['selectedTargets']))
    return (value.get('schemaVersion') == 1 and value.get('terminal') is True
            and value.get('mutableChildrenSettled') is True
            and value.get('admissionId') == record['admissionId']
            and value.get('candidateRevision') == record['applicationRevision']
            and value.get('producingRun') == run['id']
            and value.get('producingAttempt') == run.get('run_attempt', 1)
            and value.get('resources') == record['resources'] and targets == expected)


def admission_conflicts(api, candidate, *, current_run):
    """Called only while holding the shared scheduler/admission workflow mutex."""
    conflicts = []
    for run in direct_release_runs(api):
        own_run = run['id'] == current_run
        if run.get('status') == 'completed' and successful_release(api, run):
            continue  # exact terminal live proof discharges this publication lease
        # A completed run that provably never entered any mutation phase owns no
        # live release resource. Discharge it before interpreting historical
        # admission scope through the current target/path inventory.
        if not own_run and _admission_nonmutating_terminal(api, run):
            continue
        records = _admission_records(api, run)
        if own_run:
            current_attempt = int(os.environ.get('GITHUB_RUN_ATTEMPT', '1'))
            records = [record for record in records if record['producingAttempt'] < current_attempt]
            if not records:
                # The active admission cannot prove its own downstream jobs skipped.
                # Inspect every earlier attempt; only proven non-entry permits retry.
                prior_attempts = dict(run, run_attempt=current_attempt - 1)
                if current_attempt == 1 or _never_admitted(api, prior_attempts):
                    continue
        if not records:
            if run.get('status') == 'completed' and _never_admitted(api, run):
                continue
            jobs = api.pages(f"actions/runs/{run['id']}/jobs?filter=latest", 'jobs')
            introduced = any(job.get('name') == 'admission' for job in jobs)
            if introduced or run.get('status') != 'completed':
                conflicts.append({'runId': run['id'], 'reason': 'Unknown or missing resource admission evidence'})
            # Historical terminal workflows predate the resource lease contract.
            # Their upload history is still reconciled by the operation journal;
            # active legacy workflows always block new admission globally.
            continue
        for record in records:
            if _admission_settled(api, run, record) or _admission_nonmutating_terminal(api, run):
                continue
            if not VALIDATION_AUTHORITY.release_resources_overlap(candidate['resources'], record['resources']):
                continue
            continuation = ((run.get('status') == 'completed' or own_run)
                            and record['applicationRevision'] == candidate['applicationRevision']
                            and record['selectedTargets'] == candidate['selectedTargets']
                            and record['resources'] == candidate['resources'])
            if continuation:
                # Same immutable transaction resumes under a new control revision.
                # Original per-target intent still forbids every ambiguous replay.
                continue
            conflicts.append({'runId': run['id'], 'admissionId': record['admissionId'],
                              'reason': 'Conflicting release lacks terminal exact-live disposition'})
    return conflicts


def admit_worker(api):
    """Common resource admission for manual and automatic direct workers."""
    if staging_only():
        return {'state': 'WAITING_FOR_DEPENDENCY', 'admitted': False, 'retained': 'Validation-only staging hold'}
    authority = os.environ.get('RELEASE_SHA', '')
    if not SHA.fullmatch(authority) or git('rev-parse', 'HEAD').stdout.strip() != authority:
        raise RuntimeError('Admission checkout is not the exact approved execution authority')
    automatic = os.environ.get('AUTOMATIC_RELEASE') == 'true'
    if automatic:
        pr = api.api('pulls/' + str(int(os.environ['AUTOMATIC_SOURCE_PR'])))
        revision = os.environ.get('AUTOMATIC_VALIDATED_SHA')
        source_merge = os.environ.get('AUTOMATIC_SOURCE_MERGE_SHA') or authority
        if (not pr.get('merged_at') or pr.get('base', {}).get('ref') != APPROVED
            or pr.get('merge_commit_sha') != source_merge or pr.get('head', {}).get('sha') != revision
            or not ancestor(source_merge, authority)):
            raise RuntimeError('Automatic admission does not bind one merged validated PR')
        paths = [row['filename'] for row in api.pages(f"pulls/{pr['number']}/files") if row.get('filename')]
        targets = list(VALIDATION_AUTHORITY.release_targets_for_paths(paths))
        if json.loads(os.environ['AUTOMATIC_TARGETS_JSON']) != targets:
            raise RuntimeError('Admission target scope differs from canonical PR ownership')
        routing = False
    else:
        if not direct_only_request(authority):
            raise RuntimeError('Manual worker has no exact approved release authorization')
        pr = direct_release_approved_pr(api, authority)
        if pr is None:
            raise RuntimeError('Manual release does not bind one validated source PR')
        revision = pr['head']['sha']
        source_merge = pr.get('merge_commit_sha') or authority
        paths = [row['filename'] for row in api.pages(f"pulls/{pr['number']}/files") if row.get('filename')]
        request = json.loads(git('show', authority + ':' + VALIDATION_AUTHORITY.RELEASE_REQUEST_PATH).stdout)
        targets = sorted(release_targets(authority))
        routing = request.get('cloudflareWebsiteRouting', False)
        if not isinstance(routing, bool):
            raise RuntimeError('Malformed routing release authorization')
    pending = candidate_validation(api, pr)
    if pending:
        return {'state': 'VALIDATING', 'admitted': False, 'retained': pending}
    package = _validated_package_evidence(api, revision)
    if not package.get('reusable'):
        return {'state': 'WAITING_FOR_DEPENDENCY', 'admitted': False,
                'retained': 'Immutable validated package proof is required before resource admission'}
    run_id = int(os.environ['GITHUB_RUN_ID'])
    record = {'schemaVersion': 1, 'phase': 'admission', 'authorizationMode': 'automatic' if automatic else 'explicit', 'sourcePr': pr['number'],
              'authorizedSourceRevision': revision, 'packageIdentity': package['packageIdentity'],
              'applicationRevision': package['revision'], 'executionAuthority': authority,
              'sourceMergeSha': source_merge, 'selectedTargets': targets,
              'resources': VALIDATION_AUTHORITY.release_admission_resources(paths, targets, routing=routing),
              'producingRun': run_id, 'producingAttempt': int(os.environ['GITHUB_RUN_ATTEMPT'])}
    record['admissionId'] = _admission_identity(record)
    conflicts = admission_conflicts(api, record, current_run=run_id)
    if conflicts:
        return {'state': 'WAITING_FOR_CONFLICTING_RELEASE', 'admitted': False, 'blockers': conflicts}
    transport = Path(__file__).with_name('release-artifacts') / 'transport.cjs'
    result = subprocess.run(['node', str(transport)], input=json.dumps({
        'name': 'legend-release-admission-' + record['admissionId'], 'record': record}),
        text=True, capture_output=True)
    if result.returncode or 'LEGEND_OPERATION_RESULT=' not in result.stdout:
        raise RuntimeError('Durable release admission readback failed; no mutation authorized')
    return {'state': 'RELEASE_READY', 'admitted': True, 'admission': record}


def _canonical_release_name(app):
    if app is None:
        return None
    if app in VALIDATION_AUTHORITY.RELEASE_TARGETS:
        return VALIDATION_AUTHORITY.RELEASE_TARGETS[app]["releaseName"]
    if app in VALIDATION_AUTHORITY.release_name_map():
        return app
    return None


def release_proven(api, revision, app=None):
    """Require a durable successful direct-release receipt for a live app revision.

    The workflow head can be a release-control-only descendant while the deployed
    application identity intentionally remains an earlier source revision. The
    artifact name binds proof to APPLICATION_RELEASE_SHA instead of guessing from
    the workflow head.
    """
    release_name = _canonical_release_name(app)
    if app is not None and release_name is None:
        return False
    name = 'legend-approved-release-' + revision + (('-' + release_name) if release_name else '')
    artifacts = api.pages(
        'actions/artifacts?name=' + urllib.parse.quote(name, safe=''),
        'artifacts',
    )
    run_ids = []
    for artifact in artifacts:
        if artifact.get('expired'):
            continue
        run_id = (artifact.get('workflow_run') or {}).get('id')
        if isinstance(run_id, int) and run_id not in run_ids:
            run_ids.append(run_id)
    for run_id in run_ids:
        run = api.api(f'actions/runs/{run_id}')
        # Target-specific artifact names are the scope proof for modern
        # transactional releases. Legacy generic receipts still fall back to the
        # committed request check below.
        if successful_release(api, run):
            return True

    # Bootstrap durable proof for exact-head direct releases that completed before
    # the application-release receipt artifact existed. This is intentionally
    # narrower than receipt reuse: the workflow run itself must be for this exact
    # live revision, and successful_release still requires the sole direct-release
    # workflow, approved branch, target scope, final live proof and enforcement.
    # Control-only descendants therefore still require the receipt artifact above.
    exact_runs = api.pages(
        'actions/runs?head_sha=' + urllib.parse.quote(revision, safe=''),
        'workflow_runs',
    )
    exact_runs.sort(
        key=lambda row: (
            row.get('updated_at') or row.get('run_started_at') or row.get('created_at', ''),
            row.get('run_attempt', 1),
        ),
        reverse=True,
    )
    for run in exact_runs:
        if run.get('head_sha') != revision:
            continue
        if successful_release(api, run, app=app):
            return True
    return False


def cleanup(api, apply=False):
    if staging_only():
        return {'retained': 'Validation-only staging hold; no integration, dispatch or cleanup'}
    branches = api.pages('branches')
    approved = api.ref(APPROVED)
    live = live_revisions()
    for row in live:
        if not release_proven(api, row['revision'], app=row['app']):
            return {'retained': 'Live app lacks an exact successful direct-release receipt: ' + row['app']}
    for revision in [approved, *(row['revision'] for row in live)]:
        git('cat-file', '-e', revision + '^{commit}')

    pulls = api.pages('pulls?state=open')
    open_refs = {pull['base']['ref'] for pull in pulls} | {
        pull['head']['ref'] for pull in pulls
        if pull['head']['repo'] and pull['head']['repo']['full_name'] == api.repo
    }
    active = []
    for status in ('queued', 'in_progress', 'waiting', 'requested', 'pending'):
        active.extend(api.pages('actions/runs?status=' + status, 'workflow_runs'))
    if any(row.get('path', '').split('@')[0] == '.github/workflows/' + DIRECT for row in active):
        return {'retained': 'A direct release is queued or running; live evidence may change'}
    active_refs = {row['head_branch'] for row in active}

    rows = []
    for branch in branches:
        name, sha = branch['name'], branch['commit']['sha']
        latest = api.pages('actions/runs?branch=' + urllib.parse.quote(name, safe=''), 'workflow_runs')
        latest.sort(key=lambda row: row.get('updated_at') or row['created_at'], reverse=True)
        failed = {name} if latest and latest[0]['conclusion'] not in {'success', 'skipped', None} else set()
        allowed, reason = eligible(branch, approved, live, open_refs, active_refs, failed)
        row = {'branch': name, 'sha': sha, 'eligible': allowed, 'reason': reason, 'deleted': False}
        if allowed and apply:
            if api.ref(APPROVED) != approved:
                raise RuntimeError('Approved branch moved during cleanup; stop and retry from fresh evidence')
            current = api.api('branches/' + urllib.parse.quote(name, safe=''))
            fresh_pulls = api.pages('pulls?state=open')
            used = any(
                pull['base']['ref'] == name or (
                    pull['head']['repo']
                    and pull['head']['repo']['full_name'] == api.repo
                    and pull['head']['ref'] == name
                )
                for pull in fresh_pulls
            )
            recent = api.pages('actions/runs?branch=' + urllib.parse.quote(name, safe=''), 'workflow_runs')
            recent.sort(key=lambda item: item.get('updated_at') or item['created_at'], reverse=True)
            if (used or current['protected'] or current['commit']['sha'] != sha
                or any(item['status'] != 'completed' for item in recent)
                or (recent and recent[0]['conclusion'] not in {'success', 'skipped'})):
                row['reason'] = 'branch gained work or protection during cleanup'
            else:
                result = git(
                    'push', '--force-with-lease=refs/heads/' + name + ':' + sha,
                    'origin', ':refs/heads/' + name, check=False
                )
                if result.returncode:
                    raise RuntimeError('Atomic branch deletion failed; branch retained: ' + name)
                row['deleted'] = True
        rows.append(row)
    return {'approvedSha': approved, 'live': live, 'branches': rows}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['integrate', 'pending-updates', 'reconcile', 'cleanup', 'admit-worker'])
    parser.add_argument('--pr', type=int)
    parser.add_argument('--run', type=int)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    api = GitHub()
    if args.command == 'admit-worker':
        result = admit_worker(api)
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            output.write('admitted=' + str(result['admitted']).lower() + '\n')
            output.write('state=' + result['state'] + '\n')
            if result.get('admitted'):
                output.write('admission_id=' + result['admission']['admissionId'] + '\n')
                output.write('resources=' + json.dumps(result['admission']['resources'], separators=(',', ':')) + '\n')
    elif args.command == 'integrate':
        result = integrate(api, args.pr)
    elif args.command == 'pending-updates':
        result = pending_updates(api)
    elif args.command == 'reconcile':
        result = reconcile(api, args.run)
    else:
        result = cleanup(api, args.apply)
    if args.output:
        args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
