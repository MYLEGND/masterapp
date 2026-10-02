#!/usr/bin/env python3
"""One immutable upload, followed by read-only deployment/runtime reconciliation.

A timeout is not a failed Azure operation. Unknown/active operations are never
replayed. A static Website OneDeploy that is proven terminal-failed while exact
provenance remains old may use one bounded Kudu ZipDeploy recovery with the same
verified immutable ZIP. The approved release workflow remains the only authority.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import time
import urllib.request
import zipfile

def _release_authority_module():
    path = Path(__file__).with_name("validation-resume.py")
    spec = importlib.util.spec_from_file_location("validation_resume_authority", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


_RELEASE_AUTHORITY = _release_authority_module()
TARGETS = _RELEASE_AUTHORITY.RELEASE_TARGETS


def target_url(target):
    return "https://" + target["host"] + target["provenancePath"]


def target_azure(key, package, revision):
    target = TARGETS[key]
    return Azure(
        target["releaseName"],
        package,
        target_url(target),
        revision,
        target["static"],
    )


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

    def submit_static_recovery(self):
        if not self.static:
            raise RuntimeError('Static deployment recovery is valid only for the Website target')
        # This is not a retry of an ambiguous operation. It is authorized only
        # after OneDeploy has reached terminal failure and exact provenance proves
        # the candidate is still not live. Reuse the same verified immutable ZIP
        # through Kudu ZipDeploy once, then return to read-only reconciliation.
        command = ['az', 'webapp', 'deployment', 'source', 'config-zip',
                   '-g', 'masterapp-rg', '-n', self.app, '--src', str(self.package),
                   '--only-show-errors', '-o', 'json']
        try:
            result = subprocess.run(command, capture_output=True, text=True, timeout=300)
            if result.returncode:
                print('::warning::Static recovery response was unsuccessful; reconciling Azure without another submission.', flush=True)
                print(result.stderr[-4000:], flush=True)
            return result.returncode == 0
        except subprocess.TimeoutExpired:
            print('::warning::Static recovery response timed out; Azure may still be working. No further submission.', flush=True)
            return False


def reconcile(azure, *, clock=time.monotonic, sleep=time.sleep, timeout=1200, interval=15):
    started = clock()
    submitted = False
    static_recovery_submitted = False
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
        live = azure.revision_live()
        if new and new[0]['status'] == 3:
            failed = new[0]
            if azure.static and not static_recovery_submitted and live is False and not active:
                baseline_ids.add(failed['id'])
                static_recovery_submitted = True
                stable = 0
                print(
                    f"Static Website OneDeploy {failed['id']} failed terminally and the approved revision is not live; "
                    "submitting the same verified immutable ZIP once through Kudu ZipDeploy.",
                    flush=True)
                azure.submit_static_recovery()
                sleep(interval)
                continue
            raise RuntimeError(
                f"Azure deployment {failed['id']} failed. "
                "Inspect its deployment log; no automatic restart.")
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


def _rollback_package(root: Path, key: str):
    matches = sorted(root.glob(f"diagnostics-rollback-{key}-*/package.zip"))
    if len(matches) != 1:
        raise RuntimeError(
            f"Expected exactly one preserved rollback package for {key}; found {len(matches)}"
        )
    return matches[0]


def _baseline_map(raw: str):
    rows = json.loads(raw)
    if not isinstance(rows, list):
        raise ValueError("Release baselines must be a list")
    result = {}
    for row in rows:
        if not isinstance(row, dict):
            raise ValueError("Malformed release baseline")
        key = row.get("app")
        revision = row.get("revision")
        if key not in TARGETS or key in result or not re.fullmatch(r"[0-9a-f]{40}", revision or ""):
            raise ValueError("Release baselines do not match canonical target inventory")
        result[key] = revision
    return result


def deploy_one(key: str, revision: str, package_root: Path):
    target = TARGETS[key]
    package = package_root / target["package"]
    digest = verify_package(package, revision, target["static"])
    print(
        f'{target["releaseName"]}: approved revision {revision}, ZIP sha256 {digest}',
        flush=True,
    )
    result = reconcile(target_azure(key, package, revision))
    print(
        f'{target["releaseName"]}: {result}; exact revision healthy and no Azure deployment pending.',
        flush=True,
    )
    return result


def rollback_transaction(keys, baselines, rollback_root: Path):
    failures = []
    for key in reversed(keys):
        revision = baselines[key]
        target = TARGETS[key]
        try:
            package = _rollback_package(rollback_root, key)
            verify_package(package, revision, target["static"])
            reconcile(target_azure(key, package, revision))
        except Exception as exc:
            failures.append(f"{target['releaseName']}:{type(exc).__name__}:{exc}")
    if failures:
        raise RuntimeError(
            "Automatic rollback could not restore the complete pre-release state: "
            + "; ".join(failures)
        )


def deploy_transaction(target_names, baselines_raw: str, package_root: Path, rollback_root: Path, revision: str):
    keys = _RELEASE_AUTHORITY.selected_release_target_keys(target_names)
    baselines = _baseline_map(baselines_raw)
    missing = [key for key in keys if key not in baselines]
    if missing:
        raise ValueError("Missing rollback baseline for canonical targets: " + ", ".join(missing))

    # Prove every candidate and every compensation package before the first write.
    for key in keys:
        target = TARGETS[key]
        verify_package(package_root / target["package"], revision, target["static"])
        baseline = baselines[key]
        if baseline != revision:
            verify_package(_rollback_package(rollback_root, key), baseline, target["static"])

    try:
        for key in keys:
            deploy_one(key, revision, package_root)

        # Commit only after the complete selected set is stable at one revision.
        for key in keys:
            result = reconcile(
                target_azure(key, package_root / TARGETS[key]["package"], revision)
            )
            if result not in {"preserved", "deployed"}:
                raise RuntimeError("Unrecognized deployment reconciliation result")
    except Exception as release_error:
        rollback_keys = [key for key in keys if baselines[key] != revision]
        try:
            rollback_transaction(rollback_keys, baselines, rollback_root)
        except Exception as rollback_error:
            raise RuntimeError(
                f"Release transaction failed ({release_error}); rollback also failed ({rollback_error})"
            ) from rollback_error
        raise RuntimeError(
            f"Release transaction failed and every changed target was restored to its preserved baseline: {release_error}"
        ) from release_error

    print(json.dumps({
        "revision": revision,
        "targets": [TARGETS[key]["releaseName"] for key in keys],
        "transaction": "committed",
    }, sort_keys=True))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--target', choices=TARGETS)
    mode.add_argument('--targets-json')
    parser.add_argument('--baselines-json')
    parser.add_argument('--package-root', default='/tmp/diagnostics-packages')
    parser.add_argument('--rollback-root', default='/tmp/rollback-packages')
    args = parser.parse_args()

    revision = os.environ.get('APPLICATION_RELEASE_SHA') or os.environ.get('RELEASE_SHA')
    if not revision:
        raise SystemExit('APPLICATION_RELEASE_SHA is required')

    if args.target:
        deploy_one(args.target, revision, Path(args.package_root))
        return

    if args.baselines_json is None:
        raise SystemExit('--baselines-json is required for transactional deployment')
    names = json.loads(args.targets_json)
    deploy_transaction(
        names,
        args.baselines_json,
        Path(args.package_root),
        Path(args.rollback_root),
        revision,
    )


if __name__ == '__main__':
    main()
