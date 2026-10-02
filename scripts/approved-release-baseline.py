#!/usr/bin/env python3
"""Read live source identities and refuse to release a candidate missing live history."""
import argparse
import importlib.util
import hashlib
import concurrent.futures
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.request
from release_policy import read_request

def _validation_resume_module():
    path = Path(__file__).with_name("validation-resume.py")
    spec = importlib.util.spec_from_file_location("validation_resume_authority", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


_validation_authority = _validation_resume_module()

# The validation/release authority owns deployment topology and supported scope.
# Baseline discovery consumes it; it never maintains a second inventory.
TARGETS = _validation_authority.release_target_rows()

def selected_targets(request):
    if not isinstance(request, dict):
        raise ValueError('Release request must be an object')
    if 'targets' not in request:
        return TARGETS
    keys = set(_validation_authority.selected_release_target_keys(request['targets']))
    return tuple(row for row in TARGETS if row[0] in keys)



def release_control_only_path(path):
    """Consume the canonical application-identity classification."""
    return _validation_authority.release_control_only_path(path)


def reusable_live_application_revision(rows, head):
    """Keep exact live provenance across release-control/test-only corrections.

    This prevents a workflow/test/release-policy fix from manufacturing a new
    application identity and needlessly rebuilding/redeploying unchanged product
    code. Any runtime/product/migration change fails closed to the current head.
    """
    revisions = {row["revision"] for row in rows}
    if len(revisions) != 1:
        return None
    live = next(iter(revisions))
    changed = subprocess.check_output(
        ["git", "diff", "--name-only", live, head],
        text=True,
    ).splitlines()
    if all(release_control_only_path(path) for path in changed):
        return live
    return None



def exact_live_release(rows, application_release_sha, release_mode, website_routing):
    """True only when publication cannot change any selected application target."""
    return (
        release_mode == 'approved-only'
        and not website_routing
        and bool(rows)
        and all(row['revision'] == application_release_sha for row in rows)
    )


def _release_package_module():
    path = Path(__file__).with_name("release-package.py")
    spec = importlib.util.spec_from_file_location("release_package", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def release_package_contract_hash():
    return _release_package_module().contract_hash()


def release_package_identity(application_release_sha, targets=None, website_routing=False, website_routing_canary=""):
    """Package identity is source/contract bound, never authorization-scope bound.

    Target/routing authorization remains in direct-release-request.json. The
    immutable package bytes are built once for the exact validated application
    revision and may be selected by any later authorized scoped release without
    recompilation.
    """
    return _release_package_module().package_identity(application_release_sha)


def validated_application_revision(validated_sha, approved_head):
    """Return the exact validated PR head when later commits are control-only."""
    validated_sha = validate_revision(validated_sha)
    approved_head = validate_revision(approved_head)
    subprocess.run(["git", "merge-base", "--is-ancestor", validated_sha, approved_head], check=True)
    changed = subprocess.check_output(
        ["git", "diff", "--name-only", validated_sha, approved_head],
        text=True,
    ).splitlines()
    unexpected = sorted(path for path in changed if not release_control_only_path(path))
    if unexpected:
        raise ValueError(
            "Approved release contains application changes after the validated PR head: "
            + ", ".join(unexpected)
        )
    return validated_sha

def validate_revision(value):
    if not isinstance(value, str) or not re.fullmatch(r'[a-fA-F0-9]{40}', value):
        raise ValueError('Live source identity is missing or invalid')
    return value.lower()


def observe(target):
    app, host, project = target
    path = '/_deployment-provenance.txt' if project == 'static' else '/api/runtime-provenance'
    request = urllib.request.Request('https://' + host + path, headers={'Cache-Control': 'no-cache'})
    with urllib.request.urlopen(request, timeout=30) as response:
        if response.geturl().split('?')[0] != 'https://' + host + path:
            raise ValueError('Unexpected provenance redirect')
        body = response.read(8193)
        if len(body) > 8192:
            raise ValueError('Oversized provenance response')
        revision = body.decode().strip() if project == 'static' else json.loads(body)['sourceRevision']
    canonical = _validation_authority.RELEASE_TARGETS[app]
    return dict(
        app=app,
        releaseName=canonical["releaseName"],
        host=host,
        project=project,
        sourceRoot=canonical["sourceRoot"],
        package=canonical["package"],
        static=canonical["static"],
        path=path,
        revision=validate_revision(revision),
    )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path)
    parser.add_argument('--automatic', action='store_true', help='Compatibility flag; committed target scope remains authoritative')
    args = parser.parse_args()
    head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
    github_release_context = os.environ.get('GITHUB_ACTIONS') == 'true'
    expected_release_sha = os.environ.get('RELEASE_SHA') or os.environ.get('GITHUB_SHA')
    if github_release_context:
        if os.environ.get('GITHUB_REF') != 'refs/heads/legend/approved-changes' or head != expected_release_sha:
            raise SystemExit('Only the exact approved release revision can be released')

    automatic = bool(args.automatic and github_release_context)
    validated_source_sha = head
    if automatic:
        spec = importlib.util.spec_from_file_location('release_lifecycle', Path(__file__).with_name('release-lifecycle.py'))
        lifecycle = importlib.util.module_from_spec(spec); spec.loader.exec_module(lifecycle)
        api = lifecycle.GitHub()
        try:
            pr_number = int(os.environ['AUTOMATIC_SOURCE_PR'])
        except (KeyError, ValueError):
            raise ValueError('Automatic release requires the validated source PR number')
        validated_sha = validate_revision(os.environ.get('AUTOMATIC_VALIDATED_SHA'))
        merge_sha = validate_revision(os.environ.get('AUTOMATIC_MERGE_SHA'))
        if merge_sha != head:
            raise ValueError('Automatic release checkout is not the exact validated merge revision')
        pr = api.api(f'pulls/{pr_number}')
        if (
            not pr.get('merged_at')
            or pr.get('merge_commit_sha') != merge_sha
            or pr.get('base', {}).get('ref') != lifecycle.APPROVED
            or pr.get('head', {}).get('sha') != validated_sha
        ):
            raise ValueError('Automatic release inputs do not bind to one merged validated PR')
        pending = lifecycle.candidate_validation(api, pr)
        if pending:
            raise ValueError(pending)
        names = [row.get('filename') for row in api.pages(f'pulls/{pr_number}/files') if row.get('filename')]
        derived = list(_validation_authority.release_targets_for_paths(names))
        try:
            supplied = json.loads(os.environ['AUTOMATIC_TARGETS_JSON'])
        except (KeyError, json.JSONDecodeError):
            raise ValueError('Automatic release targets are missing or malformed')
        if supplied != derived or not derived:
            raise ValueError('Automatic release target scope does not match canonical validated-PR derivation')
        request = {'releaseMode': 'approved-only', 'targets': derived}
        validated_source_sha = validated_sha
    else:
        request = read_request()

    release_mode = request['releaseMode']
    if release_mode not in {'approved-only', 'validate-only'}:
        raise ValueError('releaseMode must be approved-only or validate-only')
    if release_mode == 'approved-only' and 'targets' not in request:
        raise ValueError('An approved release requires an explicit target list')
    if github_release_context and release_mode == 'approved-only' and not automatic:
        spec = importlib.util.spec_from_file_location('release_lifecycle', Path(__file__).with_name('release-lifecycle.py'))
        lifecycle = importlib.util.module_from_spec(spec); spec.loader.exec_module(lifecycle)
        if not lifecycle.direct_only_request(head):
            raise ValueError('This exact revision has no changed approved release request; no deployment authorized')
        api = lifecycle.GitHub()
        pr = lifecycle.direct_release_approved_pr(api, head)
        if pr is None:
            raise ValueError('Exact release must identify its immediately preceding merged approved PR')
        pending = lifecycle.candidate_validation(api, pr)
        if pending:
            raise ValueError(pending)
        validated_source_sha = validate_revision(pr['head']['sha'])
    website_routing = False if automatic else request.get('cloudflareWebsiteRouting', False)
    if not isinstance(website_routing, bool):
        raise ValueError('cloudflareWebsiteRouting must be a boolean when supplied')
    preserve_live_targets = request.get('preserveLiveTargets', False)
    if not isinstance(preserve_live_targets, bool):
        raise ValueError('preserveLiveTargets must be a boolean when supplied')
    preserve_live_revision = ''
    if preserve_live_targets:
        if release_mode != 'approved-only' or not website_routing:
            raise ValueError('preserveLiveTargets is only valid for an approved Cloudflare routing recovery')
        preserve_live_revision = validate_revision(request.get('preserveLiveRevision'))
    targets = selected_targets(request)
    selected_names = [
        _validation_authority.RELEASE_TARGETS[row[0]]["releaseName"]
        for row in targets
    ]
    runtime_profile = _validation_authority.release_runtime_profile(selected_names)
    if website_routing:
        required_routing = {
            row["releaseName"] for row in runtime_profile["routingTargets"]
        }
        if not required_routing.issubset(set(selected_names)):
            raise ValueError('Cloudflare website routing requires every canonical routing target')
    website_routing_canary = ''
    if website_routing:
        website_routing_canary = str(request.get('websiteRoutingCanaryHost') or '').strip().lower().rstrip('.')
        if (not re.fullmatch(r'(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}', website_routing_canary) or
                website_routing_canary == 'mylegnd.com' or website_routing_canary.endswith('.mylegnd.com')):
            raise ValueError('websiteRoutingCanaryHost must be an external verified business hostname')
    with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
        rows = list(pool.map(observe, targets))
    selected_apps = {row['app'] for row in rows}
    database_baseline = ''
    if release_mode == 'approved-only' and runtime_profile["selectedDatabaseDependent"]:
        database_name = runtime_profile["databaseAuthority"]
        database_row = next((row for row in rows if row['releaseName'] == database_name), None)
        if database_row is None:
            database_key = _validation_authority.release_name_map()[database_name]
            database_target = next(row for row in TARGETS if row[0] == database_key)
            database_row = observe(database_target)
        database_baseline = database_row['revision']
        subprocess.run(['git', 'cat-file', '-e', database_baseline + '^{commit}'], check=True)
        subprocess.run(['git', 'merge-base', '--is-ancestor', database_baseline, head], check=True)
    for row in rows:
        subprocess.run(['git', 'cat-file', '-e', row['revision'] + '^{commit}'], check=True)
        subprocess.run(['git', 'merge-base', '--is-ancestor', row['revision'], head], check=True)
    if preserve_live_targets:
        if any(row['revision'] != preserve_live_revision for row in rows):
            raise ValueError('Preserved targets are not all live at preserveLiveRevision; no recovery deployment authorized')
        allowed_control_files = {
            '.github/workflows/all-intentional-direct-release-20260918.yml',
            'scripts/approved-release-baseline.py',
            'scripts/cloudflare-routing-authority.py',
            'Docs/releases/direct-release-request.json',
        }
        changed = subprocess.check_output(
            ['git', 'diff', '--name-only', preserve_live_revision, head],
            text=True).splitlines()
        unexpected = sorted(path for path in changed if path not in allowed_control_files)
        if unexpected:
            raise ValueError('Preserve-live recovery contains application changes: ' + ', '.join(unexpected))
    if preserve_live_targets:
        application_release_sha = preserve_live_revision
    elif release_mode == 'approved-only' and github_release_context:
        application_release_sha = validated_application_revision(validated_source_sha, head)
        if application_release_sha != head:
            print(
                "Using exact validated PR head as application provenance:",
                application_release_sha,
            )
    elif release_mode == 'approved-only':
        application_release_sha = head
    else:
        application_release_sha = reusable_live_application_revision(rows, head) or head
        if application_release_sha != head:
            print(
                "Preserving exact live application identity across release-control/test-only changes:",
                application_release_sha,
            )
    package_identity = release_package_identity(
        application_release_sha,
        targets,
        website_routing,
        website_routing_canary,
    )
    exact_live = exact_live_release(
        rows,
        application_release_sha,
        release_mode,
        website_routing,
    )
    if exact_live:
        print('All selected application targets already expose the exact approved application revision; publication work is unnecessary.')
    print(json.dumps(rows, indent=2))
    if args.output:
        with args.output.open('a') as out:
            out.write('matrix=' + json.dumps({'include': rows}, separators=(',', ':')) + '\n')
            out.write('baselines=' + json.dumps(rows, separators=(',', ':')) + '\n')
            out.write('portal=' + (database_baseline or rows[0]['revision']) + '\n')
            out.write('database_baseline=' + database_baseline + '\n')
            out.write('targets=' + json.dumps([row['releaseName'] for row in rows], separators=(',', ':')) + '\n')
            out.write('public_only=' + str(runtime_profile['publicOnly']).lower() + '\n')
            out.write('client_only=' + str(runtime_profile['clientOnly']).lower() + '\n')
            out.write('website_only=' + str(runtime_profile['websiteOnly']).lower() + '\n')
            out.write('portal_only=' + str(runtime_profile['portalOnly']).lower() + '\n')
            out.write('resource_group=' + runtime_profile['resourceGroup'] + '\n')
            out.write('migration_bundle=' + runtime_profile['migrationBundle'] + '\n')
            out.write('routing_worker=' + runtime_profile['routingWorker'] + '\n')
            out.write('routing_pass_through_hosts=' + json.dumps(runtime_profile['routingPassThroughHosts'], separators=(',', ':')) + '\n')
            out.write('domain_refresh_project=' + runtime_profile['domainRefreshProject'] + '\n')
            out.write('database_authority=' + runtime_profile['databaseAuthority'] + '\n')
            out.write('browser_entry_hosts=' + json.dumps(runtime_profile['browserEntryHosts'], separators=(',', ':')) + '\n')
            out.write('shared_auth_targets=' + json.dumps(runtime_profile['sharedAuthTargets'], separators=(',', ':')) + '\n')
            out.write('editor_targets=' + json.dumps(runtime_profile['editorTargets'], separators=(',', ':')) + '\n')
            out.write('marketing_targets=' + json.dumps(runtime_profile['marketingTargets'], separators=(',', ':')) + '\n')
            out.write('routing_targets=' + json.dumps(runtime_profile['routingTargets'], separators=(',', ':')) + '\n')
            out.write('routing_primary=' + runtime_profile['routingPrimary'] + '\n')
            out.write('routing_primary_host=' + runtime_profile['routingPrimaryHost'] + '\n')
            out.write('selected_has_browser_entry=' + str(runtime_profile['selectedHasBrowserEntry']).lower() + '\n')
            out.write('selected_has_static=' + str(runtime_profile['selectedHasStatic']).lower() + '\n')
            out.write('selected_has_shared_auth=' + str(runtime_profile['selectedHasSharedAuth']).lower() + '\n')
            out.write('selected_has_editor=' + str(runtime_profile['selectedHasEditor']).lower() + '\n')
            out.write('selected_database_dependent=' + str(runtime_profile['selectedDatabaseDependent']).lower() + '\n')
            out.write('validate_only=' + str(release_mode == 'validate-only').lower() + '\n')
            out.write('website_routing=' + str(website_routing).lower() + '\n')
            out.write('website_routing_canary=' + website_routing_canary + '\n')
            out.write('preserve_live_targets=' + str(preserve_live_targets).lower() + '\n')
            out.write('exact_live=' + str(exact_live).lower() + '\n')
            out.write('application_release_sha=' + application_release_sha + '\n')
            out.write('package_identity=' + package_identity + '\n')


if __name__ == '__main__':
    main()
