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

# Existing deployment topology, not application-discovery or diagnostics policy.
TARGETS = (
    ('portal', 'portal.mylegnd.com', 'AgentPortal/AgentPortal.csproj'),
    ('client', 'client.mylegnd.com', 'ClientApp/ClientApp.csproj'),
    ('protect', 'masterapp-protect.azurewebsites.net', 'Protect-Website/ProtectWebsite.csproj'),
    ('parfait', 'masterapp-parfait.azurewebsites.net', 'ParfaitApp/ParfaitApp.csproj'),
    ('website', 'masterapp-website.azurewebsites.net', 'static'),
)


def selected_targets(request):
    if not isinstance(request, dict):
        raise ValueError('Release request must be an object')
    if 'targets' not in request:
        return TARGETS
    names = request['targets']
    inventory = {'masterapp-' + row[0]: row for row in TARGETS}
    if (not isinstance(names, list) or not names or
            any(not isinstance(name, str) or name not in inventory for name in names) or
            len(set(names)) != len(names)):
        raise ValueError('Release targets must be unique names from the existing deployment inventory')
    # Static-only releases have no database or .NET app changes. Other scoped
    # releases retain Portal as the shared migration baseline.
    if set(names) not in ({'masterapp-website'}, {'masterapp-protect'}, {'masterapp-client'}, {'masterapp-client', 'masterapp-protect'}, {'masterapp-protect', 'masterapp-website'}, {'masterapp-parfait'}, {'masterapp-protect', 'masterapp-parfait'}, {'masterapp-protect', 'masterapp-parfait', 'masterapp-website'}, {'masterapp-portal'}, {'masterapp-portal', 'masterapp-protect'}, {'masterapp-portal', 'masterapp-client'}, {'masterapp-portal', 'masterapp-client', 'masterapp-protect'}, {'masterapp-portal', 'masterapp-client', 'masterapp-parfait'}, {'masterapp-portal', 'masterapp-protect', 'masterapp-website'}, {'masterapp-portal', 'masterapp-client', 'masterapp-protect', 'masterapp-website'}, {'masterapp-portal', 'masterapp-client', 'masterapp-protect', 'masterapp-parfait'}, {'masterapp-portal', 'masterapp-protect', 'masterapp-parfait', 'masterapp-website'}, {'masterapp-portal', 'masterapp-client', 'masterapp-protect', 'masterapp-parfait', 'masterapp-website'}):
        raise ValueError('Unsupported scoped release; migration and packaging policy must be reviewed')
    return tuple(row for row in TARGETS if 'masterapp-' + row[0] in names)



def release_control_only_path(path):
    """Paths that can change release control/evidence without changing app bits."""
    return (
        path.startswith(".github/workflows/")
        or path.startswith("Docs/")
        or path.startswith("AgentPortal.Tests/")
        or path.startswith("tests/")
        or path in {
            "scripts/approved-release-baseline.py",
            "scripts/release-lifecycle.py",
            "scripts/release_policy.py",
            "scripts/deploy-approved-app.py",
            "scripts/release-package.py",
            "scripts/validation-resume.py",
            "scripts/test-validation-resume.py",
            "scripts/test-release-policy.py",
            "scripts/test-release-lifecycle.py",
            "scripts/test-deploy-approved-app.py",
        }
    )


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
    return dict(app=app, host=host, project=project, path=path, revision=validate_revision(revision))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path)
    parser.add_argument('--automatic', action='store_true', help='Compatibility flag; committed target scope remains authoritative')
    args = parser.parse_args()
    head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
    if os.environ.get('GITHUB_ACTIONS') == 'true':
        if os.environ.get('GITHUB_REF') != 'refs/heads/legend/approved-changes' or head != os.environ.get('GITHUB_SHA'):
            raise SystemExit('Only the exact approved branch revision can be released')
    request = read_request()
    validated_source_sha = head
    release_mode = request['releaseMode']
    if release_mode not in {'approved-only', 'validate-only'}:
        raise ValueError('releaseMode must be approved-only or validate-only')
    if release_mode == 'approved-only' and 'targets' not in request:
        raise ValueError('An approved release requires an explicit target list')
    if os.environ.get('GITHUB_ACTIONS') == 'true' and release_mode == 'approved-only':
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
    website_routing = request.get('cloudflareWebsiteRouting', False)
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
    if website_routing:
        routing_apps = {row[0] for row in targets}
        if not {'protect', 'parfait'}.issubset(routing_apps):
            raise ValueError('Cloudflare website commerce routing releases must include masterapp-protect and masterapp-parfait')
        if not (routing_apps.issubset({'portal', 'client', 'protect', 'parfait'}) or routing_apps == {row[0] for row in TARGETS}):
            raise ValueError('Cloudflare website routing requires the reviewed commerce scope or the complete web release inventory')
    website_routing_canary = ''
    if website_routing:
        website_routing_canary = str(request.get('websiteRoutingCanaryHost') or '').strip().lower().rstrip('.')
        if (not re.fullmatch(r'(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}', website_routing_canary) or
                website_routing_canary == 'mylegnd.com' or website_routing_canary.endswith('.mylegnd.com')):
            raise ValueError('websiteRoutingCanaryHost must be an external verified business hostname')
    with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
        rows = list(pool.map(observe, targets))
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
    elif release_mode == 'approved-only':
        application_release_sha = validated_application_revision(validated_source_sha, head)
        if application_release_sha != head:
            print(
                "Using exact validated PR head as application provenance:",
                application_release_sha,
            )
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
            out.write('portal=' + rows[0]['revision'] + '\n')
            out.write('targets=' + json.dumps(['masterapp-' + row['app'] for row in rows], separators=(',', ':')) + '\n')
            out.write('public_only=' + str(all(row['app'] in {'protect', 'website'} for row in rows)).lower() + '\n')
            out.write('client_only=' + str(len(rows) == 1 and rows[0]['app'] == 'client').lower() + '\n')
            out.write('website_only=' + str(len(rows) == 1 and rows[0]['app'] == 'website').lower() + '\n')
            out.write('portal_only=' + str(len(rows) == 1 and rows[0]['app'] == 'portal').lower() + '\n')
            out.write('validate_only=' + str(release_mode == 'validate-only').lower() + '\n')
            out.write('website_routing=' + str(website_routing).lower() + '\n')
            out.write('website_routing_canary=' + website_routing_canary + '\n')
            out.write('preserve_live_targets=' + str(preserve_live_targets).lower() + '\n')
            out.write('exact_live=' + str(exact_live).lower() + '\n')
            out.write('application_release_sha=' + application_release_sha + '\n')
            out.write('package_identity=' + package_identity + '\n')


if __name__ == '__main__':
    main()
