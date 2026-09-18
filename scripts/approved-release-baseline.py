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
    args = parser.parse_args()
    head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
    if os.environ.get('GITHUB_ACTIONS') == 'true':
        if os.environ.get('GITHUB_REF') != 'refs/heads/legend/approved-changes' or head != os.environ.get('GITHUB_SHA'):
            raise SystemExit('Only the exact approved branch revision can be released')
    with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
        rows = list(pool.map(observe, TARGETS))
    for row in rows:
        subprocess.run(['git', 'cat-file', '-e', row['revision'] + '^{commit}'], check=True)
        subprocess.run(['git', 'merge-base', '--is-ancestor', row['revision'], head], check=True)
    print(json.dumps(rows, indent=2))
    if args.output:
        with args.output.open('a') as out:
            out.write('matrix=' + json.dumps({'include': rows}, separators=(',', ':')) + '\n')
            out.write('baselines=' + json.dumps(rows, separators=(',', ':')) + '\n')
            out.write('portal=' + rows[0]['revision'] + '\n')


if __name__ == '__main__':
    main()
