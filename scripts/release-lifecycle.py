#!/usr/bin/env python3
"""Own branch integration, checked production promotion, and lossless branch retirement.

Runs only trusted default-branch code. Never executes source-branch code. GitHub's
merge API and existing release workflows retain validation/deployment authority.
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

APPROVED = 'legend/approved-changes'
PRODUCTION = 'production'
DIRECT = 'all-intentional-direct-release-20260918.yml'
RIGOROUS = 'agentportal-production-deploy.yml'
WEBSITE = 'legend-website-production-deploy.yml'
KEEP = {APPROVED, PRODUCTION}
SHA = re.compile(r'^[0-9a-f]{40}$')


def git(*args, check=True):
    return subprocess.run(['git', *args], check=check, text=True, capture_output=True)


def ancestor(before, after):
    result = git('merge-base', '--is-ancestor', before, after, check=False)
    if result.returncode not in (0, 1):
        raise RuntimeError(result.stderr)
    return result.returncode == 0


def eligible(branch, approved, production, live, open_refs, active_refs, failed_refs):
    name, sha = branch['name'], branch['commit']['sha']
    if name in KEEP or branch.get('protected'):
        return False, 'release/protected branch'
    if name in open_refs:
        return False, 'open pull request uses branch as source or base'
    if name in active_refs:
        return False, 'workflow still running'
    if name in failed_refs:
        return False, 'latest branch workflow failed or was cancelled'
    if not ancestor(sha, approved):
        return False, 'unique history not preserved in approved changes'
    if not ancestor(sha, production):
        return False, 'awaiting checked production promotion'
    if not live or not all(ancestor(sha, row['revision']) for row in live):
        return False, 'not covered by every live web application revision'
    return True, 'preserved in both release paths and every live web revision'


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


def integrate(api, number):
    pr = api.api(f'pulls/{number}')
    if not ready(pr, api.repo, APPROVED):
        raise RuntimeError('Only ready, same-repository collaborator PRs into approved changes can be integrated')
    # GitHub enforces any configured branch requirements. No force update.
    result = api.api(f'pulls/{number}/merge',
        {'merge_method': 'merge', 'sha': pr['head']['sha']}, method='PUT')
    if not result.get('merged'):
        raise RuntimeError('Merge did not complete; source branch retained')
    # GITHUB_TOKEN pushes do not trigger push workflows; explicitly dispatch.
    api.dispatch(DIRECT, {'automatic': 'true'})
    return {'mergedPr': number, 'sha': result['sha'], 'releaseDispatched': True}


def pending_updates(api):
    # Scheduled reconciliation also covers bot-created PR events and corrections
    # pushed to a retained branch after its previous approved PR was merged.
    pulls = api.pages('pulls?state=open&base=' + urllib.parse.quote(APPROVED, safe=''))
    for pr in reversed(pulls):
        if ready(pr, api.repo, APPROVED):
            return integrate(api, pr['number'])
    open_names = {p['head']['ref'] for p in pulls if p['head']['repo'] and p['head']['repo']['full_name'] == api.repo}
    branches = {b['name']: b for b in api.pages('branches')}
    approved = api.ref(APPROVED)
    closed = api.pages('pulls?state=closed&base=' + urllib.parse.quote(APPROVED, safe=''))
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
                    'The direct release and checked production gates will rerun; branch deletion remains gated.'})
        # Creation by GITHUB_TOKEN has author_association NONE; the previous ready
        # collaborator PR plus ancestry is the authorization, not the bot identity.
        result = api.api(f"pulls/{correction['number']}/merge", {'merge_method': 'merge', 'sha': head}, method='PUT')
        if not result.get('merged'):
            raise RuntimeError('Correction merge failed; source branch retained')
        api.dispatch(DIRECT, {'automatic': 'true'})
        return {'correctionPr': correction['number'], 'releaseDispatched': True}
    return {'integration': 'no ready changes or retained-branch corrections'}


def resolve_production(api, number, expected_head, dispatch=False):
    pr = api.api(f'pulls/{number}')
    if (pr['state'] != 'open' or pr['draft'] or pr['base']['ref'] != PRODUCTION
        or not pr['head']['repo'] or pr['head']['repo']['full_name'] != api.repo):
        raise RuntimeError('Not a ready same-repository production PR')
    head = pr['head']['sha']
    if not SHA.fullmatch(expected_head or '') or head != expected_head:
        raise RuntimeError('PR head changed or does not match the triggering workflow source')
    if dispatch and pr['head']['ref'] != APPROVED:
        raise RuntimeError('Dispatched production promotion must use the exact approved workflow revision')
    bot_promotion = pr['head']['ref'] == APPROVED and pr['user']['login'] == 'github-actions[bot]'
    if not bot_promotion and pr['author_association'] not in {'OWNER', 'MEMBER', 'COLLABORATOR'}:
        raise RuntimeError('Untrusted production PR author')
    if pr['head']['ref'] == 'hotfix/staging-batch' or pr['head']['ref'].startswith('hotfix/staging-batch/'):
        raise RuntimeError('Unpublished staging batch is not a release candidate')
    approved, production = api.ref(APPROVED), api.ref(PRODUCTION)
    if pr['base']['sha'] != production or not ancestor(head, approved):
        raise RuntimeError('Production candidate is stale or absent from approved changes')
    if not pr.get('mergeable') or not SHA.fullmatch(pr.get('merge_commit_sha') or ''):
        raise RuntimeError('Production merge candidate not ready; retain branch and retry')
    merge = pr['merge_commit_sha']
    git('fetch', '--no-tags', 'origin', merge)
    # A rigorous release must not roll back ANY currently deployed web app.
    for row in live_revisions():
        if not ancestor(row['revision'], merge):
            raise RuntimeError('Production candidate would omit live history: ' + row['app'])
    return {'sha': merge, 'head': head, 'base': production, 'number': str(number)}


def successful_release(api, run, app=None):
    if run['status'] != 'completed' or run['conclusion'] != 'success':
        return False
    path = run['path'].split('@')[0]
    if path not in {'.github/workflows/' + DIRECT, '.github/workflows/' + RIGOROUS, '.github/workflows/' + WEBSITE}:
        return False
    if run.get('head_repository', {}).get('full_name') != api.repo:
        return False
    if path.endswith(DIRECT) and run.get('head_branch') != APPROVED:
        return False
    if path.endswith(WEBSITE) and (run.get('head_branch') != PRODUCTION or run.get('event') != 'push'):
        return False
    jobs = api.pages(f"actions/runs/{run['id']}/jobs?filter=latest", 'jobs')
    required = {'discover-live', 'release'} if path.endswith(DIRECT) else {
        'security', 'build', 'merge', 'migrate', 'deploy', 'verify-legend-native', 'verify-legend-native-sql'}
    if path.endswith(WEBSITE):
        gate = 'website-security' if any(j['name'] == 'website-security' for j in jobs) else 'security'
        required = {gate, 'deploy'}
    passed = {job['name'] for job in jobs if job['conclusion'] == 'success'}
    if not required <= passed:
        return False
    if app is None:
        return True
    if path.endswith(RIGOROUS):
        return app in {'portal', 'client'}
    if path.endswith(WEBSITE):
        return app == 'website'
    target_steps = {'portal': 'Direct deploy AgentPortal', 'client': 'Direct deploy ClientApp',
                    'protect': 'Direct deploy Protect', 'parfait': 'Direct deploy Parfait',
                    'website': 'Direct deploy Website'}
    release_job = next(job for job in jobs if job['name'] == 'release')
    return any(step['name'] == target_steps.get(app) and step['conclusion'] == 'success'
               for step in release_job.get('steps', []))


def reconcile(api, trigger=None):
    if trigger:
        run = api.api(f'actions/runs/{trigger}')
        if not successful_release(api, run):
            return {'retained': 'No successful applicable release; no promotion or cleanup'}
        if run['path'].split('@')[0].endswith(RIGOROUS):
            # A normal merge preserves newer approved changes; conflicts retain both refs.
            prod, approved = api.ref(PRODUCTION), api.ref(APPROVED)
            if not release_proven(api, prod, production=True):
                return {'retained': 'Current production tip is not bound to successful release proof'}
            if not ancestor(prod, approved):
                api.api('merges', {'base': APPROVED, 'head': prod,
                    'commit_message': 'Preserve successfully validated production release in approved changes'})
                git('fetch', '--no-tags', 'origin')
                # The synchronized merge commit must be deployed before any later
                # candidate can claim it as a live ancestor. Dispatch explicitly.
                api.dispatch(DIRECT, {'automatic': 'true'})
                return {'synchronizedProduction': prod, 'directReleaseDispatched': True}
    # Find exact successful direct release for the CURRENT approved head. Never
    # promote newer unreleased edits based on an older workflow's green result.
    approved, production = api.ref(APPROVED), api.ref(PRODUCTION)
    if ancestor(approved, production):
        return {'promotion': 'already preserved in production'}
    runs = api.api('actions/runs?head_sha=' + approved + '&per_page=100')['workflow_runs']
    direct_runs = [r for r in runs if r['path'].split('@')[0] == '.github/workflows/' + DIRECT
                   and r['head_branch'] == APPROVED]
    if not direct_runs:
        # Recover a merge whose explicit dispatch failed. Arbitrary commits on
        # approved changes are not silently reinterpreted as release requests.
        closed = api.pages('pulls?state=closed&base=' + urllib.parse.quote(APPROVED, safe=''))
        released_pr = next((p for p in closed if p.get('merged_at') and
            p.get('merge_commit_sha') == approved and p['head']['repo'] and
            p['head']['repo']['full_name'] == api.repo), None)
        if released_pr:
            api.dispatch(DIRECT, {'automatic': 'true'})
            return {'directRelease': 'recovered missing dispatch for integrated approved PR'}
        return {'promotion': 'awaiting successful direct release of current approved head'}
    if not successful_release(api, direct_runs[0]):
        return {'promotion': 'awaiting successful direct release of current approved head'}
    pulls = api.pages('pulls?state=open&base=production')
    pr = next((p for p in pulls if p['head']['ref'] == APPROVED and p['head']['repo']['full_name'] == api.repo), None)
    if pr is None:
        pr = api.api('pulls', {'head': APPROVED, 'base': PRODUCTION,
            'title': 'Promote successfully released approved changes through production gates',
            'body': 'Automatically prepared after the exact approved revision passed direct deployment. '
                    'The existing production CI, security, artifact and live proof gates remain required. '
                    'Source branches remain until both release paths preserve them and deployment is proven.'})
    # Dispatch runs the SAME rigorous workflow, avoiding bot-created PR event suppression.
    active = api.api('actions/runs?status=in_progress&per_page=100')['workflow_runs']
    active += api.api('actions/runs?status=queued&per_page=100')['workflow_runs']
    if any(r['path'].split('@')[0] == '.github/workflows/' + RIGOROUS for r in active):
        return {'promotionPr': pr['number'], 'validation': 'already queued or running'}
    # Avoid repeatedly paying for the same failed exact candidate on every schedule.
    previous = [r for r in runs if r['path'].split('@')[0] == '.github/workflows/' + RIGOROUS and (
                         (r.get('event') == 'pull_request' and any(p['number'] == pr['number'] for p in r.get('pull_requests', []))) or
                         (r.get('event') == 'workflow_dispatch' and r.get('display_title') == 'LEGEND rigorous PR ' + str(pr['number'])))]
    if previous:
        return {'promotionPr': pr['number'], 'validation': 'already attempted; corrections or explicit rerun required'}
    api.dispatch(RIGOROUS, {'pull_request': str(pr['number'])})
    return {'promotionPr': pr['number'], 'validation': 'dispatched; no gates bypassed'}


def undeployed_artifact_changes(sha, production):
    # A web receipt cannot certify App Store, Play or Worker publication. Find
    # the branch's first-parent fork; retain native/Worker work conservatively.
    mainline = set(git('rev-list', '--first-parent', production).stdout.splitlines())
    fork = next((c for c in git('rev-list', '--first-parent', sha).stdout.splitlines()
                 if c in mainline), None)
    if not fork:
        return True
    paths = git('diff', '--name-only', '--no-renames', fork, sha).stdout.splitlines()
    return any(p.startswith(('Legend-ios/', 'Legend-Android/', 'Legend-Cloudflare/')) for p in paths)


def website_only_revision(revision):
    # Historical website-only production releases had their own security gate.
    # That evidence is valid only for the exact website-owned first-parent diff.
    paths = git('diff', '--name-only', '--no-renames', revision + '^1', revision).stdout.splitlines()
    return bool(paths) and all(p.startswith('Legend-Website/') or p in {
        'Legend-Design/legend-design.tokens.json',
        'Legend-ios/Legend/Resources/Assets.xcassets/LegendLogo.imageset/legend-logo.png',
        '.github/workflows/legend-website-production-deploy.yml'} for p in paths)


def release_proven(api, revision, production=False, app=None):
    runs = api.pages('actions/runs?head_sha=' + revision, 'workflow_runs')
    # A PR run records its source SHA; provenance records the actual merge SHA.
    for pr in api.pages('commits/' + revision + '/pulls'):
        if (pr.get('merge_commit_sha') == revision and pr.get('merged_at')
            and pr['base']['ref'] == PRODUCTION and pr['head']['repo']
            and pr['head']['repo']['full_name'] == api.repo):
            runs += [r for r in api.pages('actions/runs?head_sha=' + pr['head']['sha'], 'workflow_runs')
                     if r['path'].split('@')[0] == '.github/workflows/' + RIGOROUS and (
                         (r.get('event') == 'pull_request' and any(p['number'] == pr['number'] for p in r.get('pull_requests', []))) or
                         (r.get('event') == 'workflow_dispatch' and r.get('display_title') == 'LEGEND rigorous PR ' + str(pr['number'])))]
    allowed = {'.github/workflows/' + RIGOROUS, '.github/workflows/' + WEBSITE}
    if not production:
        allowed.add('.github/workflows/' + DIRECT)
    runs = [r for r in runs if r['path'].split('@')[0] in allowed]
    runs.sort(key=lambda r: (r.get('updated_at') or r.get('run_started_at') or r['created_at'], r.get('run_attempt', 1)), reverse=True)
    # An unrelated successful workflow never masks a failed release attempt.
    if any(r['status'] == 'completed' and r['conclusion'] != 'success' for r in runs[:1]):
        return False
    if production:
        rigorous = [r for r in runs if r['path'].split('@')[0].endswith(RIGOROUS)]
        if rigorous:
            return successful_release(api, rigorous[0])
        return bool(runs) and website_only_revision(revision) and successful_release(api, runs[0])
    for run in runs:
        if run['path'].split('@')[0].endswith(WEBSITE) and app != 'website':
            continue
        if run['path'].split('@')[0].endswith(RIGOROUS) and app not in {'portal', 'client'}:
            continue
        return successful_release(api, run, app=app)
    return False


def cleanup(api, apply=False):
    branches = api.pages('branches')
    approved, production = api.ref(APPROVED), api.ref(PRODUCTION)
    live = live_revisions()
    if not release_proven(api, production, production=True):
        return {'retained': 'Current production base lacks a successful complete release receipt'}
    for row in live:
        if not release_proven(api, row['revision'], app=row['app']):
            return {'retained': 'Live app lacks an exact successful deployment receipt: ' + row['app']}
    for revision in [approved, production, *(r['revision'] for r in live)]:
        git('cat-file', '-e', revision + '^{commit}')
    pulls = api.pages('pulls?state=open')
    open_refs = {p['base']['ref'] for p in pulls} | {
        p['head']['ref'] for p in pulls if p['head']['repo'] and p['head']['repo']['full_name'] == api.repo}
    # Fetch all active runs, not only the first page of repository activity.
    active = []
    for status in ('queued', 'in_progress', 'waiting', 'requested', 'pending'):
        active.extend(api.pages('actions/runs?status=' + status, 'workflow_runs'))
    if any(r['path'].split('@')[0] in {'.github/workflows/' + DIRECT, '.github/workflows/' + RIGOROUS, '.github/workflows/legend-website-production-deploy.yml'} for r in active):
        return {'retained': 'A release is queued or running; live evidence may change'}
    active_refs = {r['head_branch'] for r in active}
    rows = []
    for branch in branches:
        name, sha = branch['name'], branch['commit']['sha']
        latest = api.pages('actions/runs?branch=' + urllib.parse.quote(name, safe=''), 'workflow_runs')
        latest.sort(key=lambda r: r.get('updated_at') or r['created_at'], reverse=True)
        failed = {name} if latest and latest[0]['conclusion'] not in {'success', 'skipped', None} else set()
        allowed, reason = eligible(branch, approved, production, live, open_refs, active_refs, failed)
        if allowed and undeployed_artifact_changes(sha, production):
            allowed, reason = False, 'native or Worker publication evidence required; web receipt is insufficient'
        row = {'branch': name, 'sha': sha, 'eligible': allowed, 'reason': reason, 'deleted': False}
        if allowed and apply:
            # Recheck repository references and open work immediately before the atomic lease.
            if api.ref(APPROVED) != approved or api.ref(PRODUCTION) != production:
                raise RuntimeError('Release branch moved during cleanup; stop and retry from fresh evidence')
            current = api.api('branches/' + urllib.parse.quote(name, safe=''))
            fresh_pulls = api.pages('pulls?state=open')
            used = any(p['base']['ref'] == name or (p['head']['repo'] and
                p['head']['repo']['full_name'] == api.repo and p['head']['ref'] == name) for p in fresh_pulls)
            recent = api.pages('actions/runs?branch=' + urllib.parse.quote(name, safe=''), 'workflow_runs')
            recent.sort(key=lambda r: r.get('updated_at') or r['created_at'], reverse=True)
            if (used or current['protected'] or current['commit']['sha'] != sha
                or any(r['status'] != 'completed' for r in recent)
                or (recent and recent[0]['conclusion'] not in {'success', 'skipped'})):
                row['reason'] = 'branch gained work or protection during cleanup'
            else:
                # Compare-and-delete: a concurrent push makes the lease fail.
                result = git('push', '--force-with-lease=refs/heads/' + name + ':' + sha,
                    'origin', ':refs/heads/' + name, check=False)
                if result.returncode:
                    raise RuntimeError('Atomic branch deletion failed; branch retained: ' + name)
                row['deleted'] = True
        rows.append(row)
    return {'approvedSha': approved, 'productionSha': production, 'live': live, 'branches': rows}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['integrate', 'pending-updates', 'resolve-production', 'reconcile', 'cleanup'])
    parser.add_argument('--pr', type=int)
    parser.add_argument('--run', type=int)
    parser.add_argument('--expected-head')
    parser.add_argument('--dispatch', choices=['true', 'false'], default='false')
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    api = GitHub()
    if args.command == 'integrate':
        result = integrate(api, args.pr)
    elif args.command == 'pending-updates':
        result = pending_updates(api)
    elif args.command == 'resolve-production':
        result = resolve_production(api, args.pr, args.expected_head, args.dispatch == 'true')
        with open(os.environ['GITHUB_OUTPUT'], 'a') as out:
            for key, value in result.items():
                out.write(key + '=' + value + '\n')
    elif args.command == 'reconcile':
        result = reconcile(api, args.run)
    else:
        result = cleanup(api, args.apply)
    if args.output:
        args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
