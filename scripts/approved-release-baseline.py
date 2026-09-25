#!/usr/bin/env python3
"""Read live source identities and refuse to release a candidate missing live history."""
import argparse
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
    if set(names) not in ({'masterapp-website'}, {'masterapp-protect'}, {'masterapp-client'}, {'masterapp-client', 'masterapp-protect'}, {'masterapp-protect', 'masterapp-website'}, {'masterapp-parfait'}, {'masterapp-protect', 'masterapp-parfait'}, {'masterapp-protect', 'masterapp-parfait', 'masterapp-website'}, {'masterapp-portal'}, {'masterapp-portal', 'masterapp-client'}, {'masterapp-portal', 'masterapp-client', 'masterapp-protect'}, {'masterapp-portal', 'masterapp-client', 'masterapp-parfait'}, {'masterapp-portal', 'masterapp-client', 'masterapp-protect', 'masterapp-website'}, {'masterapp-portal', 'masterapp-client', 'masterapp-protect', 'masterapp-parfait'}, {'masterapp-portal', 'masterapp-client', 'masterapp-protect', 'masterapp-parfait', 'masterapp-website'}):
        raise ValueError('Unsupported scoped release; migration and packaging policy must be reviewed')
    return tuple(row for row in TARGETS if 'masterapp-' + row[0] in names)


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
    parser.add_argument('--automatic', action='store_true', help='Conservatively release every existing web target')
    args = parser.parse_args()
    head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
    if os.environ.get('GITHUB_ACTIONS') == 'true':
        if os.environ.get('GITHUB_REF') != 'refs/heads/legend/approved-changes' or head != os.environ.get('GITHUB_SHA'):
            raise SystemExit('Only the exact approved branch revision can be released')
    request = read_request()
    release_mode = request['releaseMode']
    if release_mode not in {'approved-only', 'validate-only'}:
        raise ValueError('releaseMode must be approved-only or validate-only')
    website_routing = request.get('cloudflareWebsiteRouting', False)
    if not isinstance(website_routing, bool):
        raise ValueError('cloudflareWebsiteRouting must be a boolean when supplied')
    preserve_live_targets = request.get('preserveLiveTargets', False)
    if not isinstance(preserve_live_targets, bool):
        raise ValueError('preserveLiveTargets must be a boolean when supplied')
    if args.automatic:
        website_routing = False
        preserve_live_targets = False
    preserve_live_revision = ''
    if preserve_live_targets:
        if release_mode != 'approved-only' or not website_routing:
            raise ValueError('preserveLiveTargets is only valid for an approved Cloudflare routing recovery')
        preserve_live_revision = validate_revision(request.get('preserveLiveRevision'))
    targets = TARGETS if args.automatic else selected_targets(request)
    if website_routing:
        routing_apps = {row[0] for row in targets}
        if not {'protect', 'parfait'}.issubset(routing_apps):
            raise ValueError('Cloudflare website commerce routing releases must include masterapp-protect and masterapp-parfait')
        if not routing_apps.issubset({'portal', 'client', 'protect', 'parfait'}):
            raise ValueError('Cloudflare website routing may be combined only with AgentPortal, ClientApp, Protect, and Parfait in one reviewed release')
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
            'Docs/releases/direct-release-request.json',
        }
        changed = subprocess.check_output(
            ['git', 'diff', '--name-only', preserve_live_revision, head],
            text=True).splitlines()
        unexpected = sorted(path for path in changed if path not in allowed_control_files)
        if unexpected:
            raise ValueError('Preserve-live recovery contains application changes: ' + ', '.join(unexpected))
    application_release_sha = preserve_live_revision if preserve_live_targets else head
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
            out.write('application_release_sha=' + application_release_sha + '\n')


if __name__ == '__main__':
    main()
