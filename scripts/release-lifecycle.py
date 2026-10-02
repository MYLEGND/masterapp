#!/usr/bin/env python3
"""Own approved-branch integration, direct-release recovery, and lossless branch retirement.

Runs only trusted default-branch code. Never executes source-branch code. The
protected approved branch is the sole Git release authority; immutable release
receipts and live provenance replace the retired production-branch promotion path.
"""
import argparse
import importlib.util
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

SHA = re.compile(r'^[0-9a-f]{40}
    path = Path(__file__).with_name("validation-resume.py")
    spec = importlib.util.spec_from_file_location("validation_resume_authority", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


VALIDATION_AUTHORITY = _validation_authority_module()
APPROVED = VALIDATION_AUTHORITY.TRUSTED_PR_BASE
DIRECT = VALIDATION_AUTHORITY.DIRECT_RELEASE_WORKFLOW
KEEP = {APPROVED}


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
        value = self.api('git/ref/heads/' + urllib.parse.quote(name, safe=''))['object']['sha']
        if not SHA.fullmatch(value):
            raise RuntimeError('Malformed branch revision')
        return value

    def dispatch(self, workflow, inputs=None):
        self.api('actions/workflows/' + workflow + '/dispatches',
                 {'ref': APPROVED, 'inputs': inputs or {}})


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


def automatic_release_inputs(pr, merge_sha, targets):
    return {
        'automatic': 'true',
        'source_pr': str(pr['number']),
        'validated_sha': pr['head']['sha'],
        'merge_sha': merge_sha,
        'targets_json': json.dumps(list(targets), separators=(',', ':')),
    }


def merge_validated(api, pr):
    pending = candidate_validation(api, pr)
    if pending:
        return {'retained': pending}

    targets = automatic_release_targets(api, pr)
    result = api.api(f"pulls/{pr['number']}/merge",
        {'merge_method': 'merge', 'sha': pr['head']['sha']}, method='PUT')
    if not result.get('merged'):
        raise RuntimeError('Merge did not complete; source branch retained')

    # Validation success is the publication handoff. Application-affecting merges
    # immediately enter the sole direct-release workflow with scope derived from
    # the validated PR. No second authorization command or hand-maintained target
    # table exists between merge and deployment.
    if targets:
        api.dispatch(DIRECT, automatic_release_inputs(pr, result['sha'], targets))

    if any(f['filename'] == '.github/workflows/deployment-diagnostics.yml' for f in api.pages(f"pulls/{pr['number']}/files")):
        api.dispatch('deployment-diagnostics.yml')
    return {
        'mergedPr': pr['number'],
        'sha': result['sha'],
        'releaseDispatched': bool(targets),
        'automaticRelease': bool(targets),
        'targets': list(targets),
    }


def integrate(api, number):
    if staging_only():
        return {'retained': 'Validation-only staging hold; no integration, dispatch or cleanup'}
    pr = api.api(f'pulls/{number}')
    if not ready(pr, api.repo, APPROVED):
        raise RuntimeError('Only ready, same-repository collaborator PRs into approved changes can be integrated')
    return merge_validated(api, pr)


def pending_updates(api):
    if staging_only():
        return {'retained': 'Validation-only staging hold; no integration, dispatch or cleanup'}
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
            result = integrate(api, pr['number'])
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
        correction = api.api('pulls', {'head': name, 'base': APPROVED,
            'title': 'Continue approved release corrections from ' + name,
            'body': 'Automatically carries new commits on the retained source branch after its previous approved PR. '
                    'Owning validation and the approved direct-release authority will re-evaluate only invalidated evidence; branch deletion remains gated.'})
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
    path = 'Docs/releases/direct-release-request.json'
    lineage = git('rev-list', '--parents', '-n', '1', sha, check=False)
    parts = lineage.stdout.strip().split() if not lineage.returncode else []
    if not parts or parts[0] != sha or len(parts) not in {2, 3}:
        return False

    changed = git('diff-tree', '--no-commit-id', '--name-only', '-r', sha + '^1', sha, check=False)
    if changed.returncode:
        return False
    names = changed.stdout.splitlines()
    if path not in names:
        return False
    if len(parts) == 2 and names != [path]:
        return False

    result = git('show', sha + ':' + path, check=False)
    if result.returncode:
        return False
    return json.loads(result.stdout).get('releaseMode') == 'approved-only'


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
    request_path = 'Docs/releases/direct-release-request.json'
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


def reconcile(api, trigger=None):
    """Recover only the exact approved direct release; never create a second branch path."""
    if staging_only():
        return {'release': 'disabled while validation-only staging hold is active'}
    if trigger:
        run = api.api(f'actions/runs/{trigger}')
        if run.get('path', '').split('@')[0] == '.github/workflows/' + DIRECT:
            if successful_release(api, run):
                return {'release': 'exact approved direct release already successful'}
            return {'retained': 'Triggered direct release did not complete successfully; no automatic replay'}

    approved = api.ref(APPROVED)

    runs = api.pages('actions/runs?head_sha=' + approved, 'workflow_runs')
    direct_runs = [
        row for row in runs
        if row.get('path', '').split('@')[0] == '.github/workflows/' + DIRECT
        and row.get('head_branch') == APPROVED
    ]
    direct_runs.sort(
        key=lambda row: (row.get('updated_at') or row.get('run_started_at') or row.get('created_at', ''),
                         row.get('run_attempt', 1)),
        reverse=True,
    )
    if direct_runs:
        latest = direct_runs[0]
        if latest.get('status') != 'completed':
            return {'release': 'already queued or running'}
        if successful_release(api, latest):
            return {'release': 'exact approved direct release already successful'}
        return {'retained': 'Exact approved release already attempted; correction or explicit rerun required'}

    merged_prs = [
        pr for pr in api.pages('commits/' + approved + '/pulls')
        if pr.get('merged_at')
        and pr.get('merge_commit_sha') == approved
        and pr.get('base', {}).get('ref') == APPROVED
    ]
    if len(merged_prs) == 1:
        pr = merged_prs[0]
        pending = candidate_validation(api, pr)
        if pending:
            return {'retained': pending}
        targets = automatic_release_targets(api, pr)
        if targets:
            api.dispatch(DIRECT, automatic_release_inputs(pr, approved, targets))
            return {'directRelease': 'recovered automatic validated-merge release', 'targets': list(targets)}

    if not direct_only_request(approved):
        return {'release': 'no application publication required for exact approved head'}

    pr = direct_release_approved_pr(api, approved)
    if pr is None:
        return {'retained': 'Exact release authorization cannot be bound to a validated approved PR'}
    pending = candidate_validation(api, pr)
    if pending:
        return {'retained': pending}
    api.dispatch(DIRECT, {'automatic': 'false'})
    return {'directRelease': 'recovered exact scoped approved request'}


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
    parser.add_argument('command', choices=['integrate', 'pending-updates', 'reconcile', 'cleanup'])
    parser.add_argument('--pr', type=int)
    parser.add_argument('--run', type=int)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    api = GitHub()
    if args.command == 'integrate':
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


def _validation_authority_module():
    path = Path(__file__).with_name("validation-resume.py")
    spec = importlib.util.spec_from_file_location("validation_resume_authority", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


VALIDATION_AUTHORITY = _validation_authority_module()


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
        value = self.api('git/ref/heads/' + urllib.parse.quote(name, safe=''))['object']['sha']
        if not SHA.fullmatch(value):
            raise RuntimeError('Malformed branch revision')
        return value

    def dispatch(self, workflow, inputs=None):
        self.api('actions/workflows/' + workflow + '/dispatches',
                 {'ref': APPROVED, 'inputs': inputs or {}})


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


def automatic_release_inputs(pr, merge_sha, targets):
    return {
        'automatic': 'true',
        'source_pr': str(pr['number']),
        'validated_sha': pr['head']['sha'],
        'merge_sha': merge_sha,
        'targets_json': json.dumps(list(targets), separators=(',', ':')),
    }


def merge_validated(api, pr):
    pending = candidate_validation(api, pr)
    if pending:
        return {'retained': pending}

    targets = automatic_release_targets(api, pr)
    result = api.api(f"pulls/{pr['number']}/merge",
        {'merge_method': 'merge', 'sha': pr['head']['sha']}, method='PUT')
    if not result.get('merged'):
        raise RuntimeError('Merge did not complete; source branch retained')

    # Validation success is the publication handoff. Application-affecting merges
    # immediately enter the sole direct-release workflow with scope derived from
    # the validated PR. No second authorization command or hand-maintained target
    # table exists between merge and deployment.
    if targets:
        api.dispatch(DIRECT, automatic_release_inputs(pr, result['sha'], targets))

    if any(f['filename'] == '.github/workflows/deployment-diagnostics.yml' for f in api.pages(f"pulls/{pr['number']}/files")):
        api.dispatch('deployment-diagnostics.yml')
    return {
        'mergedPr': pr['number'],
        'sha': result['sha'],
        'releaseDispatched': bool(targets),
        'automaticRelease': bool(targets),
        'targets': list(targets),
    }


def integrate(api, number):
    if staging_only():
        return {'retained': 'Validation-only staging hold; no integration, dispatch or cleanup'}
    pr = api.api(f'pulls/{number}')
    if not ready(pr, api.repo, APPROVED):
        raise RuntimeError('Only ready, same-repository collaborator PRs into approved changes can be integrated')
    return merge_validated(api, pr)


def pending_updates(api):
    if staging_only():
        return {'retained': 'Validation-only staging hold; no integration, dispatch or cleanup'}
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
            result = integrate(api, pr['number'])
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
        correction = api.api('pulls', {'head': name, 'base': APPROVED,
            'title': 'Continue approved release corrections from ' + name,
            'body': 'Automatically carries new commits on the retained source branch after its previous approved PR. '
                    'Owning validation and the approved direct-release authority will re-evaluate only invalidated evidence; branch deletion remains gated.'})
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
    path = 'Docs/releases/direct-release-request.json'
    lineage = git('rev-list', '--parents', '-n', '1', sha, check=False)
    parts = lineage.stdout.strip().split() if not lineage.returncode else []
    if not parts or parts[0] != sha or len(parts) not in {2, 3}:
        return False

    changed = git('diff-tree', '--no-commit-id', '--name-only', '-r', sha + '^1', sha, check=False)
    if changed.returncode:
        return False
    names = changed.stdout.splitlines()
    if path not in names:
        return False
    if len(parts) == 2 and names != [path]:
        return False

    result = git('show', sha + ':' + path, check=False)
    if result.returncode:
        return False
    return json.loads(result.stdout).get('releaseMode') == 'approved-only'


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
    request_path = 'Docs/releases/direct-release-request.json'
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


def reconcile(api, trigger=None):
    """Recover only the exact approved direct release; never create a second branch path."""
    if staging_only():
        return {'release': 'disabled while validation-only staging hold is active'}
    if trigger:
        run = api.api(f'actions/runs/{trigger}')
        if run.get('path', '').split('@')[0] == '.github/workflows/' + DIRECT:
            if successful_release(api, run):
                return {'release': 'exact approved direct release already successful'}
            return {'retained': 'Triggered direct release did not complete successfully; no automatic replay'}

    approved = api.ref(APPROVED)

    runs = api.pages('actions/runs?head_sha=' + approved, 'workflow_runs')
    direct_runs = [
        row for row in runs
        if row.get('path', '').split('@')[0] == '.github/workflows/' + DIRECT
        and row.get('head_branch') == APPROVED
    ]
    direct_runs.sort(
        key=lambda row: (row.get('updated_at') or row.get('run_started_at') or row.get('created_at', ''),
                         row.get('run_attempt', 1)),
        reverse=True,
    )
    if direct_runs:
        latest = direct_runs[0]
        if latest.get('status') != 'completed':
            return {'release': 'already queued or running'}
        if successful_release(api, latest):
            return {'release': 'exact approved direct release already successful'}
        return {'retained': 'Exact approved release already attempted; correction or explicit rerun required'}

    merged_prs = [
        pr for pr in api.pages('commits/' + approved + '/pulls')
        if pr.get('merged_at')
        and pr.get('merge_commit_sha') == approved
        and pr.get('base', {}).get('ref') == APPROVED
    ]
    if len(merged_prs) == 1:
        pr = merged_prs[0]
        pending = candidate_validation(api, pr)
        if pending:
            return {'retained': pending}
        targets = automatic_release_targets(api, pr)
        if targets:
            api.dispatch(DIRECT, automatic_release_inputs(pr, approved, targets))
            return {'directRelease': 'recovered automatic validated-merge release', 'targets': list(targets)}

    if not direct_only_request(approved):
        return {'release': 'no application publication required for exact approved head'}

    pr = direct_release_approved_pr(api, approved)
    if pr is None:
        return {'retained': 'Exact release authorization cannot be bound to a validated approved PR'}
    pending = candidate_validation(api, pr)
    if pending:
        return {'retained': pending}
    api.dispatch(DIRECT, {'automatic': 'false'})
    return {'directRelease': 'recovered exact scoped approved request'}


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
    parser.add_argument('command', choices=['integrate', 'pending-updates', 'reconcile', 'cleanup'])
    parser.add_argument('--pr', type=int)
    parser.add_argument('--run', type=int)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    api = GitHub()
    if args.command == 'integrate':
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
