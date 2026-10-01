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
import sys
sys.path.insert(0, str(Path(__file__).resolve().parent))
from release_policy import staging_only

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


def commit_tree(revision):
    result = git('rev-parse', revision + '^{tree}')
    value = result.stdout.strip()
    if not SHA.fullmatch(value):
        raise RuntimeError('Cannot establish commit tree for canonical history')
    return value


def merge_base(left, right):
    result = git('merge-base', left, right)
    value = result.stdout.strip()
    if not SHA.fullmatch(value):
        raise RuntimeError('Cannot establish merge base for canonical history')
    return value


def history_only_production_divergence(production, approved):
    """True only when production's unique lineage contributes zero tree changes.

    This is the exact topology created by a protected production synchronization
    merge whose content already came from approved changes. Preserving that merge
    in approved changes repairs ancestry without changing the approved tree.
    """
    if ancestor(production, approved) or ancestor(approved, production):
        return False
    base = merge_base(production, approved)
    return commit_tree(production) == commit_tree(base)


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


PUBLIC_WEBSITE_ARCHITECTURE_STEPS = {
    'Run branch lifecycle safety contracts',
    'Restore .NET graph',
    'Build shared infrastructure',
    'Build Protect host',
    'Verify Protect serves the exact shared tracking assets',
    'Install shared website renderer dependencies',
    'Build shared website renderer',
    'Verify renderer authority parity',
    'Run business renderer tests with approved-baseline no-regression proof',
    'Install canonical shared CMS test dependencies',
    'Run canonical shared CMS tests',
}


def architecture_public_website_validation(api, run):
    """Validate only the canonical public Website Studio/runtime subsystem.

    A public-site-only candidate must prove its own renderer, Protect host, shared
    CMS, and lifecycle contracts. Failures in unrelated app/test projects do not
    invalidate this subsystem evidence.
    """
    if run.get('status') != 'completed':
        return False
    if run.get('conclusion') == 'success':
        return True
    if run.get('conclusion') != 'failure':
        return False

    jobs = api.pages(f"actions/runs/{run['id']}/jobs?filter=latest", 'jobs')
    steps = {step.get('name'): step.get('conclusion')
             for job in jobs for step in (job.get('steps') or [])}
    return all(steps.get(name) == 'success' for name in PUBLIC_WEBSITE_ARCHITECTURE_STEPS)


ARCHITECTURE_PRODUCT_STEPS = {
    'Run branch lifecycle safety contracts',
    'Run authenticated mobile authority tests',
    'Restore .NET graph',
    'Build shared infrastructure',
    'Build AgentPortal and ClientApp hosts',
    'Build Protect host',
    'Verify Protect serves the exact shared tracking assets',
    'Compile full regression test project',
    'Compile shared domain release refresh',
    'Run website ownership and publishing regressions',
    'Run Meta authority regressions',
}


def architecture_product_validation(api, run):
    """Accept a failed architecture run only when product validation is complete.

    The release-policy check is a release-control concern. A product candidate
    remains valid when every product/build/mobile step completed successfully and
    the sole blocking step is the release-policy boundary. This preserves strict
    product evidence without allowing unrelated control-policy drift to erase it.
    """
    if run.get('status') != 'completed':
        return False
    if run.get('conclusion') == 'success':
        return True
    if run.get('conclusion') != 'failure':
        return False

    jobs = api.pages(f"actions/runs/{run['id']}/jobs?filter=latest", 'jobs')
    steps = {step.get('name'): step.get('conclusion')
             for job in jobs for step in (job.get('steps') or [])}
    if any(steps.get(name) != 'success' for name in ARCHITECTURE_PRODUCT_STEPS):
        return False
    if steps.get('Verify consolidated release scope and routing policy') != 'failure':
        return False

    # Fail closed if any step before the release-policy boundary failed/cancelled,
    # or if an unexpected executed step after it failed for a product reason.
    for name, conclusion in steps.items():
        if name in ARCHITECTURE_PRODUCT_STEPS or name == 'Verify consolidated release scope and routing policy':
            continue
        if conclusion in {'failure', 'cancelled'}:
            return False
    return True


VALIDATION_NEUTRAL_PATHS = {
    '.github/workflows/masterapp-platform-architecture-validation.yml',
    'Docs/releases/direct-release-request.json',
    'scripts/approved-release-baseline.py',
    'scripts/release-lifecycle.py',
    'scripts/test-release-policy.py',
    'scripts/test-release-lifecycle.py',
    'scripts/test-deploy-approved-app.py',
    'tests/website/legend-public-cms.test.mjs',
    'AgentPortal.Tests/WebsiteStudioV3ContractTests.cs',
}



STEP6_VALIDATION_PATHS = {
    'SHARED/Analytics/OpenAiAdsExecutionContracts.cs',
    'Infrastructure/Analytics/OpenAiAdsExecutionService.cs',
    'Infrastructure/Analytics/MarketingConnectionStore.cs',
    'AgentPortal.Tests/OpenAiAdsExecutionServiceTests.cs',
    'AgentPortal.Tests/OpenAiAdsAccountConnectionAuthorityTests.cs',
    'AgentPortal.Tests/MarketingDestinationLayerTests.cs',
    'AgentPortal.Tests/MarketingScopeParityContractTests.cs',
    '.github/workflows/step6-openai-ads-execution-validation.yml',
    'scripts/validation-resume.py',
    'scripts/test-validation-resume.py',
}

STEP78_VALIDATION_PATHS = {
    'Domain/Entities/AdvertisingActionAuthorization.cs',
    'SHARED/Analytics/AdvertisingActionContracts.cs',
    'Infrastructure/Analytics/AdvertisingActionAuthorizationService.cs',
    'Infrastructure/Analytics/MarketingConnectionStore.cs',
    'Infrastructure/WebsiteEditing/PromotionOrchestrationService.cs',
    'Infrastructure/WebsiteEditing/WebsitePlatformController.cs',
    'Infrastructure/Data/MasterAppDbContext.cs',
    'Infrastructure/Migrations/20260927053000_AddAdvertisingActionAuthorizations.cs',
    'Legend-Design/legend-website-management.js',
    'AgentPortal.Tests/AdvertisingActionAuthorizationServiceTests.cs',
    'AgentPortal.Tests/PromotionOrchestrationTests.cs',
    'AgentPortal.Tests/MarketingScopeParityContractTests.cs',
    'Infrastructure/Analytics/AdvertisingCommandCenterService.cs',
    'AgentPortal/Controllers/WebsiteAnalyticsController.cs',
    'AgentPortal/Views/WebsiteAnalytics/Index.cshtml',
    'AgentPortal/wwwroot/js/website-analytics.js',
    'AgentPortal/wwwroot/css/website-analytics.css',
    'Infrastructure/Businesses/BusinessWorkspaceControllerBase.cs',
    'AgentPortal.Tests/AdvertisingCommandCenterCentralizationTests.cs',
    '.github/workflows/steps7-8-governed-advertising-validation.yml',
    'scripts/validation-resume.py',
    'scripts/test-validation-resume.py',
}


def validation_neutral_commit(api, sha):
    commit = api.api('commits/' + sha)
    files = [row.get('filename') for row in (commit or {}).get('files', [])]
    return bool(files) and all(
        path in VALIDATION_NEUTRAL_PATHS or path.startswith('tests/')
        for path in files if path)


def architecture_run_for_sha(api, sha, architecture):
    runs = api.pages('actions/runs?head_sha=' + sha, 'workflow_runs')
    candidates = [
        run for run in runs
        if run.get('head_sha') == sha and run.get('event') == 'pull_request'
        and run['path'].split('@')[0] == architecture
    ]
    if not candidates:
        return None
    return sorted(
        candidates,
        key=lambda run: (run.get('created_at', ''), run.get('id', 0)),
        reverse=True)[0]


def inherited_architecture_product_validation(api, pr, architecture):
    """Reuse product evidence only across validation/control-only trailing commits."""
    head = pr['head']['sha']
    commits = api.pages(f"pulls/{pr['number']}/commits")
    shas = [row.get('sha') for row in commits if row.get('sha')]
    if head not in shas:
        shas.append(head)

    for sha in reversed(shas):
        run = architecture_run_for_sha(api, sha, architecture)
        if run is not None and architecture_product_validation(api, run):
            return True
        if not validation_neutral_commit(api, sha):
            return False
    return False


def candidate_validation(api, pr):
    """Require subsystem-owned validation; neutral trailing commits may reuse product evidence."""
    head = pr['head']['sha']
    runs = api.pages('actions/runs?head_sha=' + head, 'workflow_runs')
    latest = {}
    for run in sorted(runs, key=lambda r: (r.get('created_at', ''), r.get('id', 0)), reverse=True):
        if run.get('head_sha') != head or run.get('event') != 'pull_request':
            continue
        latest.setdefault(run['path'].split('@')[0], run)

    architecture = '.github/workflows/masterapp-platform-architecture-validation.yml'
    step5 = '.github/workflows/step5-isolated-conversion-mapping-validation.yml'
    step6 = '.github/workflows/step6-openai-ads-execution-validation.yml'
    step78 = '.github/workflows/steps7-8-governed-advertising-validation.yml'
    required = {architecture}

    files = api.pages(f"pulls/{pr['number']}/files")
    names = [f['filename'] for f in files]

    if (step5 in names or
        'scripts/validation-resume.py' in names or
        'scripts/test-validation-resume.py' in names):
        required.add(step5)
    if any(name in STEP6_VALIDATION_PATHS for name in names):
        required.add(step6)
    if any(name in STEP78_VALIDATION_PATHS for name in names):
        required.add(step78)

    scope_neutral = VALIDATION_NEUTRAL_PATHS | {
        'scripts/deploy-approved-app.py',
    }
    product_names = [name for name in names
                     if name not in scope_neutral and not name.startswith('tests/')]

    # Website Studio has its own architecture/shared-CMS validation authority.
    # Release/test-control files never convert it into a Step 5 marketing release.
    public_website_exact = {
        'AgentPortal.Tests/WebsiteContentEditorRoundTripTests.cs',
        'AgentPortal.Tests/WebsiteSiteSourceV3Tests.cs',
        'Legend-Design/legend-web-foundation.css',
        'Legend-Website/scripts/build.mjs',
        'Legend-Website/public/web.config',
        'Protect-Website/Views/Shared/_Layout.cshtml',
        'SHARED/WebsitePlatform/legend-public-cms.js',
        'SHARED/WebsitePlatform/legend-public-web.css',
        'Infrastructure/WebsiteEditing/WebsiteContentSanitizer.cs',
        'Infrastructure/WebsiteEditing/WebsiteEditorContracts.cs',
        'Infrastructure/WebsiteEditing/WebsitePlatformController.cs',
        'Infrastructure/WebsiteEditing/WebsiteSiteSource.cs',
        'Infrastructure/WebsiteEditing/WebsiteStudioAgentContract.cs',
        'Infrastructure/WebsiteRuntime/BusinessWebsiteMiddleware.cs',
    }
    public_website_only = bool(product_names) and all(
        name in public_website_exact for name in product_names)

    broad_product_change = any(
        name.startswith(('AgentPortal/', 'ClientApp/', 'Protect-Website/',
                         'ParfaitApp/', 'SHARED/', 'Infrastructure/', 'Domain/'))
        or name == step5
        for name in product_names
    )
    if broad_product_change and not public_website_only:
        required.add(step5)

    failed = []
    for path in sorted(required):
        run = latest.get(path)
        if path == architecture:
            if run is not None:
                if public_website_only and architecture_public_website_validation(api, run):
                    continue
                if not public_website_only and architecture_product_validation(api, run):
                    continue
            if not public_website_only and inherited_architecture_product_validation(api, pr, architecture):
                continue
            failed.append(path)
        elif run is None:
            failed.append(path)
        elif run.get('status') != 'completed' or run.get('conclusion') != 'success':
            failed.append(path)

    if not failed:
        return None
    if architecture in failed and architecture not in latest:
        return 'Exact-head architecture validation has not started'
    if step5 in failed and step5 not in latest:
        return 'Exact-head full-suite comparison has not started'
    return 'Awaiting successful exact-head validation: ' + ', '.join(failed)


def merge_validated(api, pr):
    pending = candidate_validation(api, pr)
    if pending:
        return {'retained': pending}
    # Merge permission is not deployment permission. Only an exact changed release
    # request authorizes publication; maintenance commits never expand to all apps.
    publish = direct_only_request(pr['head']['sha'])
    result = api.api(f"pulls/{pr['number']}/merge",
        {'merge_method': 'merge', 'sha': pr['head']['sha']}, method='PUT')
    if not result.get('merged'):
        raise RuntimeError('Merge did not complete; source branch retained')
    if publish:
        api.dispatch(DIRECT, {'automatic': 'false'})
    if any(f['filename'] == '.github/workflows/deployment-diagnostics.yml' for f in api.pages(f"pulls/{pr['number']}/files")):
        api.dispatch('deployment-diagnostics.yml')
    return {'mergedPr': pr['number'], 'sha': result['sha'], 'releaseDispatched': publish,
            'automaticRelease': False}


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
    pulls = api.pages('pulls?state=open&base=' + urllib.parse.quote(APPROVED, safe=''))
    closed = api.pages('pulls?state=closed&base=' + urllib.parse.quote(APPROVED, safe=''))
    for pr in reversed(pulls):
        if ready(pr, api.repo, APPROVED):
            return integrate(api, pr['number'])
        if (pr['state'] == 'open' and not pr['draft'] and pr['user']['login'] == 'github-actions[bot]'
            and pr['head']['repo'] and pr['head']['repo']['full_name'] == api.repo
            and pr['head']['ref'] not in KEEP):
            prior = [old for old in closed if old.get('merged_at') and
                old['author_association'] in {'OWNER', 'MEMBER', 'COLLABORATOR'} and
                old['head']['repo'] and old['head']['repo']['full_name'] == api.repo and
                old['head']['ref'] == pr['head']['ref']]
            if any(ancestor(old['head']['sha'], pr['head']['sha']) for old in prior):
                return merge_validated(api, pr)
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
                    'The direct release and checked production gates will rerun; branch deletion remains gated.'})
        # The previous collaborator PR authorizes review, not skipping fresh CI.
        return {'correctionPr': correction['number'], 'retained': 'Fresh exact-head validation required'}
    return {'integration': 'no ready changes or retained-branch corrections'}


def resolve_production(api, number, expected_head, dispatch=False):
    if staging_only():
        raise RuntimeError('Validation-only staging hold blocks production publication')
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
    if path.endswith(DIRECT):
        release_job = next(job for job in jobs if job['name'] == 'release')
        steps = release_job.get('steps', [])
        if not any(step['name'] == 'Verify every deployed target and collect all failures' and step['conclusion'] == 'success' for step in steps):
            return False
        if not any(step['name'].startswith('Direct deploy ') and step['conclusion'] == 'success' for step in steps):
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
    release_control_files = {
        request_path,
        '.github/workflows/masterapp-platform-architecture-validation.yml',
        'scripts/approved-release-baseline.py',
        'scripts/release-lifecycle.py',
        'scripts/test-release-policy.py',
        'scripts/test-release-lifecycle.py',
    }

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
        if names and names <= release_control_files:
            merge_lineage = git('rev-list', '--parents', '-n', '1', merged, check=False)
            merge_parts = merge_lineage.stdout.strip().split() if not merge_lineage.returncode else []
            if len(merge_parts) != 3 or merge_parts[0] != merged:
                return None
            current = merge_parts[1]
            continue
        return pr

    return None


def reconcile_history_only(api, production=None, approved=None):
    """Preserve tree-neutral canonical production lineage in approved history.

    No product content, release request, deployment scope, or application state is
    changed. Safety comes from the current production tip having the exact tree of
    the common merge base, plus a post-merge proof that approved's tree is unchanged.
    Any content-bearing divergence remains blocked for the checked release lifecycle.
    """
    production = production or api.ref(PRODUCTION)
    approved = approved or api.ref(APPROVED)

    if ancestor(production, approved):
        return {'relation': 'production-preserved', 'reconciled': False}
    if ancestor(approved, production):
        return {'relation': 'production-ahead', 'reconciled': False}
    if not history_only_production_divergence(production, approved):
        return {'relation': 'diverged-content', 'reconciled': False}

    before_tree = commit_tree(approved)
    merge = api.api('merges', {
        'base': APPROVED,
        'head': production,
        'commit_message': 'Preserve tree-neutral production synchronization history in approved changes'
    })
    merged_sha = (merge or {}).get('sha')
    if not SHA.fullmatch(merged_sha or ''):
        raise RuntimeError('Canonical history reconciliation did not return a merge revision')

    git('fetch', '--no-tags', 'origin', merged_sha)
    current_approved = api.ref(APPROVED)
    if current_approved != merged_sha:
        raise RuntimeError('Approved branch moved during canonical history reconciliation')
    if commit_tree(current_approved) != before_tree:
        raise RuntimeError('History-only reconciliation changed approved product content')
    if not ancestor(production, current_approved):
        raise RuntimeError('Production history was not preserved by reconciliation')

    return {
        'relation': 'production-history-reconciled',
        'reconciled': True,
        'productionSha': production,
        'approvedSha': current_approved
    }


def branch_parity(api):
    """Canonical verifier/reconciler used by the branch-parity workflow."""
    production, approved = api.ref(PRODUCTION), api.ref(APPROVED)
    history = reconcile_history_only(api, production, approved)
    if history.get('retained'):
        raise RuntimeError(history['retained'])
    if history.get('reconciled'):
        production, approved = api.ref(PRODUCTION), api.ref(APPROVED)

    if production == approved:
        return {'relation': 'identical', 'productionSha': production, 'approvedSha': approved}
    if ancestor(production, approved):
        return {'relation': 'approved-ahead', 'productionSha': production, 'approvedSha': approved}
    if ancestor(approved, production):
        return {'relation': 'production-ahead', 'productionSha': production, 'approvedSha': approved}
    raise RuntimeError('Canonical branches contain content-bearing divergent history')


def reconcile(api, trigger=None):
    if staging_only():
        return {'promotion': 'disabled while validation-only staging hold is active'}
    if trigger:
        run = api.api(f'actions/runs/{trigger}')
        if not successful_release(api, run):
            return {'retained': 'No successful applicable release; no promotion or cleanup'}
    # Recover missed workflow events and transient merge-back failures on every
    # schedule. A failed production attempt never authorizes this synchronization.
    production, approved = api.ref(PRODUCTION), api.ref(APPROVED)

    # Protected production synchronization creates a merge commit that can be
    # topologically unique while contributing no content beyond the shared base.
    # Preserve only that proven, tree-neutral lineage before evaluating an exact
    # approved-only release request; this repairs ancestry without expanding scope.
    history = reconcile_history_only(api, production, approved)
    if history.get('retained'):
        return history
    if history.get('reconciled'):
        return history

    if direct_only_request(approved):
        runs = api.pages('actions/runs?head_sha=' + approved, 'workflow_runs')
        if not any(r['path'].split('@')[0] == '.github/workflows/' + DIRECT for r in runs):
            pr = direct_release_approved_pr(api, approved)
            if pr is not None:
                pending = candidate_validation(api, pr)
                if pending: return {'retained': pending}
                api.dispatch(DIRECT, {'automatic': 'false'})
                return {'directRelease': 'recovered exact scoped request', 'promotion': 'disabled for approved-only release'}
        return {'promotion': 'disabled for this exact approved-only release'}
    if not ancestor(production, approved):
        if not release_proven(api, production, production=True):
            return {'retained': 'Current production tip is not bound to successful release proof'}
        api.api('merges', {'base': APPROVED, 'head': production,
            'commit_message': 'Preserve successfully validated production release in approved changes'})
        git('fetch', '--no-tags', 'origin')
        return {'synchronizedProduction': production, 'directReleaseDispatched': False}
    # Never promote newer unreleased edits using an older workflow's green result.
    if ancestor(approved, production):
        return {'promotion': 'already preserved in production'}
    runs = api.api('actions/runs?head_sha=' + approved + '&per_page=100')['workflow_runs']
    direct_runs = [r for r in runs if r['path'].split('@')[0] == '.github/workflows/' + DIRECT
                   and r['head_branch'] == APPROVED]
    if not direct_runs:
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
    if staging_only():
        return {'retained': 'Validation-only staging hold; no integration, dispatch or cleanup'}
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
    parser.add_argument('command', choices=['integrate', 'pending-updates', 'resolve-production', 'reconcile', 'branch-parity', 'cleanup'])
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
    elif args.command == 'branch-parity':
        result = branch_parity(api)
    else:
        result = cleanup(api, args.apply)
    if args.output:
        args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
