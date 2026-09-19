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
    # Other partial releases need their own reviewed migration/baseline policy.
    # Absence of targets retains the existing complete-release behavior.
    if names != ['masterapp-portal']:
        raise ValueError('Only the approved portal-only target override is supported')
    return tuple(inventory[name] for name in names)


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
    targets = TARGETS if args.automatic else selected_targets(json.loads(Path('Docs/releases/direct-release-request.json').read_text()))
    with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
        rows = list(pool.map(observe, targets))
    for row in rows:
        subprocess.run(['git', 'cat-file', '-e', row['revision'] + '^{commit}'], check=True)
        subprocess.run(['git', 'merge-base', '--is-ancestor', row['revision'], head], check=True)
    print(json.dumps(rows, indent=2))
    if args.output:
        with args.output.open('a') as out:
            out.write('matrix=' + json.dumps({'include': rows}, separators=(',', ':')) + '\n')
            out.write('baselines=' + json.dumps(rows, separators=(',', ':')) + '\n')
            out.write('portal=' + rows[0]['revision'] + '\n')
            out.write('targets=' + json.dumps(['masterapp-' + row['app'] for row in rows], separators=(',', ':')) + '\n')
            out.write('portal_only=' + str(len(rows) == 1 and rows[0]['app'] == 'portal').lower() + '\n')


if __name__ == '__main__':
    main()
