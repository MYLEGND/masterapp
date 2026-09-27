#!/usr/bin/env python3
"""One immutable upload, followed by read-only deployment/runtime reconciliation.

A timeout is not a failed Azure operation. Never replay a deployment here: a
terminal failure remains failed, and an unknown/active operation remains pending.
The approved release workflow remains the only deployment authority.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import time
import urllib.request
import zipfile

TARGETS = {
    'portal': ('masterapp-portal', 'agentportal.zip', 'https://portal.mylegnd.com/api/runtime-provenance'),
    'client': ('masterapp-client', 'clientapp.zip', 'https://client.mylegnd.com/api/runtime-provenance'),
    'protect': ('masterapp-protect', 'protect.zip', 'https://masterapp-protect.azurewebsites.net/api/runtime-provenance'),
    'parfait': ('masterapp-parfait', 'parfait.zip', 'https://masterapp-parfait.azurewebsites.net/api/runtime-provenance'),
    'website': ('masterapp-website', 'website.zip', 'https://masterapp-website.azurewebsites.net/_deployment-provenance.txt'),
}


def verify_package(package, revision, static=False):
    if not re.fullmatch(r'[0-9a-f]{40}', revision):
        raise ValueError('Expected an exact approved commit SHA')
    rows = [line.split() for line in (package.parent / 'SHA256SUMS').read_text().splitlines()]
    expected = [row[0] for row in rows if len(row) == 2 and Path(row[1]).name == package.name]
    with package.open('rb') as stream:
        actual = hashlib.file_digest(stream, 'sha256').hexdigest()
    if expected != [actual]:
        raise ValueError('Retained package SHA256 mismatch')
    with zipfile.ZipFile(package) as archive:
        if archive.testzip() is not None:
            raise ValueError('Corrupt immutable ZIP')
        entry = '_deployment-provenance.txt' if static else 'wwwroot/_deployment-provenance.json'
        payload = archive.read(entry).decode().strip()
        embedded = payload if static else json.loads(payload)['releaseSha']
        if embedded != revision:
            raise ValueError('Package does not contain the approved revision')
    return actual


class Azure:
    def __init__(self, app, package, url, revision, static=False):
        self.app, self.package, self.url, self.revision = app, package, url, revision
        self.static = static

    def deployments(self):
        # Uses the existing OIDC session; no publishing credentials or new authority.
        result = subprocess.run(
            ['az', 'webapp', 'log', 'deployment', 'list', '-g', 'masterapp-rg', '-n', self.app,
             '--only-show-errors', '-o', 'json'], capture_output=True, text=True, timeout=45)
        if result.returncode:
            raise RuntimeError('Azure deployment status unavailable')
        rows = json.loads(result.stdout)
        if not isinstance(rows, list) or any(
                not isinstance(row, dict) or not row.get('id') or
                type(row.get('status')) is not int or row['status'] not in range(5) for row in rows):
            raise RuntimeError('Unrecognized Azure deployment status; no upload authorized')
        return rows

    def revision_live(self):
        # A cache-busted runtime assembly revision, not just an uploaded file (.NET).
        request = urllib.request.Request(self.url + '?release=' + self.revision + '&probe=' + str(time.time_ns()),
                                         headers={'Cache-Control': 'no-cache'})
        try:
            with urllib.request.urlopen(request, timeout=15) as response:
                if response.status != 200 or response.url.split('?')[0] != self.url:
                    return None
                body = response.read(8192).decode().strip()
                return (body if self.static else json.loads(body).get('sourceRevision')) == self.revision
        except (OSError, ValueError):
            return None

    def submit(self):
        # Async avoids a long synchronous gateway request. CLI runtime tracking is
        # replaced by exact-revision checks below, not waived. Status preflight
        # already warmed SCM; disabling CLI warmup prevents its exception fallback
        # from replaying a POST. A subprocess timeout leaves Azure running untouched.
        command = ['az', 'webapp', 'deploy', '-g', 'masterapp-rg', '-n', self.app,
                   '--src-path', str(self.package), '--type', 'zip', '--clean', 'true',
                   '--restart', 'true', '--async', 'true', '--track-status', 'false',
                   '--enable-kudu-warmup', 'false', '--timeout', '120000',
                   '--enriched-errors', 'true', '--only-show-errors', '-o', 'json']
        try:
            result = subprocess.run(command, capture_output=True, text=True, timeout=180)
            if result.returncode:
                print('::warning::Upload response was unsuccessful; reconciling Azure without resubmission.', flush=True)
                print(result.stderr[-4000:], flush=True)
            return result.returncode == 0
        except subprocess.TimeoutExpired:
            print('::warning::Upload response timed out; Azure may still be working. No resubmission.', flush=True)
            return False


def reconcile(azure, *, clock=time.monotonic, sleep=time.sleep, timeout=1200, interval=15):
    started = clock()
    submitted = False
    baseline_ids = set()
    stable = 0
    previous = None
    while clock() - started < timeout:
        try:
            rows = azure.deployments()
        except (RuntimeError, ValueError, OSError, subprocess.TimeoutExpired):
            stable = 0
            print('Deployment status temporarily unavailable; read-only retry.', flush=True)
            sleep(interval)
            continue
        active = [row for row in rows if row['status'] in (0, 1, 2)]
        new = [row for row in rows if row['id'] not in baseline_ids] if submitted else []
        state = (submitted, tuple(sorted((row['id'], row['status']) for row in (new if submitted else active))))
        if state != previous:
            print(f'Deployment state after {int(clock() - started)}s: {state}', flush=True)
            previous = state
        if len(new) > 1:
            raise RuntimeError('Multiple new Azure deployments detected; refusing to hide a concurrent publication')
        if new and new[0]['status'] == 3:
            raise RuntimeError(f"Azure deployment {new[0]['id']} failed. Inspect its deployment log; no automatic restart.")
        live = azure.revision_live()
        if active:
            # Even exact provenance cannot authorize success while another upload
            # may still replace/restart that revision. Wait for Azure to settle.
            stable = 0
        elif live:
            # Before upload, preserve exact live candidate. After upload require
            # terminal Azure success AND two consecutive healthy revision reads.
            if not submitted or (new and new[0]['status'] == 4):
                stable += 1
                if stable >= 2:
                    return 'deployed' if submitted else 'preserved'
        else:
            stable = 0
            if not submitted and live is False:
                baseline_ids = {row['id'] for row in rows}
                submitted = True  # Set before I/O: ambiguous responses never replay.
                print('Submitting the verified immutable ZIP once.', flush=True)
                azure.submit()
        sleep(interval)
    raise RuntimeError('Deployment remains unverified at the deadline. Azure was not cancelled or restarted; inspect status before resuming.')


def main():
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


if __name__ == '__main__':
    main()
