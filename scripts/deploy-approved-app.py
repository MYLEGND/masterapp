#!/usr/bin/env python3
"""One immutable upload, followed by read-only deployment/runtime reconciliation.

A timeout is not a failed Azure operation. Unknown/active operations are never
replayed. Terminal provider failures require bounded repair through the canonical
release lifecycle; no alternate upload path can bypass durable operation intent.
"""
import argparse
import concurrent.futures
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


class DeploymentReconciliationRequired(RuntimeError):
    """Outcome is ambiguous: only read-only reconciliation may follow."""


class DeploymentDrift(DeploymentReconciliationRequired):
    """Live revision is neither the preserved baseline nor immutable candidate."""


class DeploymentStatusUnavailable(DeploymentReconciliationRequired):
    """Azure deployment state could not be read within the bounded outage budget."""


PUBLICATION_RECONCILE_TIMEOUT_SECONDS = 420
FINALIZE_RECONCILE_TIMEOUT_SECONDS = 90
FINALIZE_RECONCILE_ATTEMPTS = 3
FINALIZE_RETRY_DELAY_SECONDS = 10


def read_deployments_bounded(azure, *, attempts=3, interval=15, sleep=None, phase='before any upload'):
    """Read Azure deployment state with one canonical bounded read-only retry policy."""
    if attempts < 1:
        raise ValueError('Deployment status attempts must be positive')
    sleeper = sleep or time.sleep
    for attempt in range(1, attempts + 1):
        try:
            return azure.deployments()
        except (RuntimeError, ValueError, OSError, subprocess.TimeoutExpired) as exc:
            if attempt >= attempts:
                raise DeploymentStatusUnavailable(
                    f'Azure deployment status remained unavailable for {attempts} consecutive reads '
                    f'({phase}). No deployment or rollback write was replayed; '
                    'resume by reconciling the exact revision.'
                ) from exc
            print(
                f'Deployment status temporarily unavailable; bounded read-only retry '
                f'{attempt}/{attempts}.',
                flush=True,
            )
            sleeper(interval)
    raise AssertionError('Unreachable Azure deployment read state')


class Azure:
    def __init__(self, app, package, url, revision, static=False):
        self.app, self.package, self.url, self.revision = app, package, url, revision
        self.static = static

    def deployments(self):
        # Uses the existing OIDC session; no publishing credentials or new authority.
        result = subprocess.run(
            ['az', 'webapp', 'log', 'deployment', 'list', '-g', _RELEASE_AUTHORITY.RELEASE_RESOURCE_GROUP, '-n', self.app,
             '--only-show-errors', '-o', 'json'], capture_output=True, text=True, timeout=20)
        if result.returncode:
            raise RuntimeError('Azure deployment status unavailable')
        rows = json.loads(result.stdout)
        if not isinstance(rows, list) or any(
                not isinstance(row, dict) or not row.get('id') or
                type(row.get('status')) is not int or row['status'] not in range(5) for row in rows):
            raise RuntimeError('Unrecognized Azure deployment status; no upload authorized')
        return rows

    def observed_revision(self):
        # A cache-busted runtime assembly revision, not just an uploaded file (.NET).
        request = urllib.request.Request(self.url + '?release=' + self.revision + '&probe=' + str(time.time_ns()),
                                         headers={'Cache-Control': 'no-cache'})
        try:
            with urllib.request.urlopen(request, timeout=15) as response:
                if response.status != 200 or response.url.split('?')[0] != self.url:
                    return None
                body = response.read(8192).decode().strip()
                revision = body if self.static else json.loads(body).get('sourceRevision')
                return revision if isinstance(revision, str) and re.fullmatch(r'[0-9a-f]{40}', revision) else None
        except (OSError, ValueError):
            return None

    def revision_live(self):
        revision = self.observed_revision()
        return None if revision is None else revision == self.revision

    def submit(self):
        # Async avoids a long synchronous gateway request. CLI runtime tracking is
        # replaced by exact-revision checks below, not waived. Status preflight
        # already warmed SCM; disabling CLI warmup prevents its exception fallback
        # from replaying a POST. A subprocess timeout leaves Azure running untouched.
        command = ['az', 'webapp', 'deploy', '-g', _RELEASE_AUTHORITY.RELEASE_RESOURCE_GROUP, '-n', self.app,
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


def reconcile(azure, *, clock=time.monotonic, sleep=time.sleep, timeout=PUBLICATION_RECONCILE_TIMEOUT_SECONDS, interval=15, max_status_failures=3, baseline=None, reconcile_only=False, journal=None, require_receipt=True):
    started = clock()
    submitted = False
    baseline_ids = set()
    if journal is not None:
        baseline = journal.baseline
        if journal.intent is not None:
            submitted = True
            reconcile_only = True
            baseline_ids = set(journal.intent['baselineDeploymentIds'])
    stable = 0
    previous = None
    while clock() - started < timeout:
        rows = read_deployments_bounded(
            azure,
            attempts=max_status_failures,
            interval=interval,
            sleep=sleep,
            phase='after immutable upload' if submitted else 'before any upload',
        )
        active = [row for row in rows if row['status'] in (0, 1, 2)]
        new = [row for row in rows if row['id'] not in baseline_ids] if submitted else []
        state = (submitted, tuple(sorted((row['id'], row['status']) for row in (new if submitted else active))))
        if state != previous:
            print(f'Deployment state after {int(clock() - started)}s: {state}', flush=True)
            previous = state
        if len(new) > 1:
            raise DeploymentDrift('Multiple new Azure deployments detected; refusing to hide a concurrent publication')
        if baseline is not None:
            observed = azure.observed_revision()
            if observed is not None and observed not in {baseline, azure.revision}:
                raise DeploymentDrift('Live revision is neither the preserved baseline nor exact candidate; no write authorized')
            live = None if observed is None else observed == azure.revision
        else:
            live = azure.revision_live()
        failed = new[0] if new and new[0]['status'] == 3 else None
        retained_failed_candidate = (
            failed is not None and
            reconcile_only and
            journal is not None and
            journal.intent is not None and
            live is True and
            not active
        )
        if failed is not None and not retained_failed_candidate:
            raise RuntimeError(
                f"Azure deployment {failed['id']} failed. "
                "Inspect its deployment log; no automatic restart.")
        if retained_failed_candidate:
            # The original write remains truthfully provider-failed. A later
            # recovery may only preserve the runtime when the retained immutable
            # intent binds this candidate and Azure is terminal/idle at that exact
            # candidate. Never resubmit and never relabel the failed provider row.
            stable += 1
            if stable >= 2:
                try:
                    journal.record_success([])
                except Exception as exc:
                    if require_receipt:
                        raise DeploymentReconciliationRequired(
                            'Exact candidate live after terminal provider failure but durable reconciliation receipt unavailable; preserve publication'
                        ) from exc
                    print(
                        '::warning::Exact candidate is live and idle after terminal provider failure; '
                        'durable success receipt is pending final read-only transaction reconciliation.',
                        flush=True,
                    )
                    return 'preserved-receipt-pending'
                print(
                    f'Azure deployment {failed["id"]} is terminal-failed, but the retained immutable candidate '
                    'is repeatedly proven live and idle; preserving without replay.',
                    flush=True,
                )
                return 'preserved'
        elif active:
            # Even exact provenance cannot authorize success while another upload
            # may still replace/restart that revision. Wait for Azure to settle.
            stable = 0
        elif live:
            # Before upload, preserve exact live candidate. After upload require
            # terminal Azure success AND two consecutive healthy revision reads.
            if not submitted or (new and new[0]['status'] == 4):
                stable += 1
                if stable >= 2:
                    if journal is not None:
                        try:
                            journal.record_success([row['id'] for row in rows if row['status'] == 4])
                        except Exception as exc:
                            if require_receipt:
                                raise DeploymentReconciliationRequired(
                                    'Exact candidate live but durable success receipt unavailable; preserve publication'
                                ) from exc
                            print(
                                '::warning::Exact candidate is live and idle; durable success receipt is pending '
                                'final read-only transaction reconciliation.',
                                flush=True,
                            )
                            return 'deployed-receipt-pending' if submitted and not reconcile_only else 'preserved-receipt-pending'
                    return 'deployed' if submitted and not reconcile_only else 'preserved'
        else:
            stable = 0
            if not submitted and live is False and not reconcile_only:
                baseline_ids = {row['id'] for row in rows}
                if journal is not None:
                    try:
                        allow_recovered_baseline = (
                            getattr(journal, 'history_error', None) is not None and
                            baseline is not None and
                            observed == baseline and
                            not active
                        )
                        allowed = journal.before_submit(
                            baseline_ids,
                            allow_recovered_baseline=allow_recovered_baseline,
                        )
                    except Exception as exc:
                        raise DeploymentReconciliationRequired('Durable upload intent could not be proven; no write authorized') from exc
                    if not allowed:
                        raise DeploymentReconciliationRequired('Existing upload intent requires read-only reconciliation')
                submitted = True  # Set before I/O: ambiguous responses never replay.
                print('Submitting the verified immutable ZIP once.', flush=True)
                azure.submit()
        sleep(interval)
    raise DeploymentReconciliationRequired('Deployment remains unverified at the deadline. Azure was not cancelled or restarted; inspect status before resuming.')


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


def operation_journal(key, revision, digest, baseline):
    if os.environ.get('GITHUB_ACTIONS') != 'true':
        return None
    if baseline is None:
        raise ValueError('Preserved transaction baseline required')
    spec = importlib.util.spec_from_file_location('release_operation_evidence', Path(__file__).with_name('release-operation-evidence.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.OperationJournal(target=key, application_revision=revision, package_digest=digest, baseline=baseline, authority=_RELEASE_AUTHORITY)


def deploy_one(key: str, revision: str, package_root: Path, *, baseline=None, reconcile_only=False, journal=None):
    target = TARGETS[key]
    package = package_root / target["package"]
    digest = verify_package(package, revision, target["static"])
    print(
        f'{target["releaseName"]}: approved revision {revision}, ZIP sha256 {digest}',
        flush=True,
    )
    journal = journal or operation_journal(key, revision, digest, baseline)
    result = reconcile(
        target_azure(key, package, revision),
        baseline=baseline,
        reconcile_only=reconcile_only,
        journal=journal,
        # Publication owns the provider mutation once. A transient receipt-channel
        # failure after exact-live proof must not mark the target failed and force
        # sibling replay. Final transaction reconciliation is read-only and requires
        # the durable receipt before the release can become terminal-successful.
        require_receipt=reconcile_only,
    )
    print(
        f'{target["releaseName"]}: {result}; exact revision healthy and no Azure deployment pending.',
        flush=True,
    )
    return result


def retained_rollback_package(root, key, revision, retained=None):
    target = TARGETS[key]
    for package in sorted(root.glob(f'diagnostics-rollback-{key}-*/package.zip')):
        try:
            digest = verify_package(package, revision, target['static'])
            if retained is not None and digest != retained['packageDigest']:
                continue
            if retained is not None:
                return retained
            receipt_path = package.parent / 'receipt.json'
            receipt = json.loads(receipt_path.read_text()) if receipt_path.exists() else {
                'artifact': package.parent.name, 'runId': int(os.environ.get('GITHUB_RUN_ID', '0')),
                'revision': revision, 'packageDigest': digest}
            return receipt
        except ValueError:
            continue
    # A resumed partial transaction may have observed the candidate during the
    # rollback-capture job. Restore the original baseline package from its exact
    # authenticated release receipt; never rebuild or relabel those bytes.
    repository = os.environ.get('GITHUB_REPOSITORY')
    if not repository:
        raise ValueError('Original immutable rollback package unavailable')
    evidence = ({'reusable': True, 'runId': retained['runId'], 'packageArtifact': retained['artifact']}
                if retained is not None else _RELEASE_AUTHORITY.compute_rollback_evidence(repository, revision, key))
    if not evidence.get('reusable'):
        raise DeploymentReconciliationRequired('Original preserved rollback package evidence unavailable')
    import shutil
    import tempfile
    with tempfile.TemporaryDirectory(prefix='legend-rollback-') as temporary:
        folder = Path(temporary)
        _RELEASE_AUTHORITY._download_run_artifact(repository, evidence['runId'], evidence['packageArtifact'], folder)
        candidate = folder / ('package.zip' if (folder / 'package.zip').exists() else target['package'])
        digest = verify_package(candidate, revision, target['static'])
        if retained is not None and digest != retained['packageDigest']:
            raise ValueError('Preserved rollback artifact digest changed')
        destination = root / f'diagnostics-rollback-{key}-preserved'
        destination.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(candidate, destination / 'package.zip')
        (destination / 'SHA256SUMS').write_text(digest + '  package.zip\n')
        receipt = {'artifact': evidence['packageArtifact'], 'runId': evidence['runId'],
                   'revision': revision, 'packageDigest': digest}
        (destination / 'receipt.json').write_text(json.dumps(receipt, sort_keys=True))
        return receipt


def preflight_target(key, package, revision, baseline, journal):
    azure = target_azure(key, package, revision)
    if journal is not None and journal.intent is not None:
        # Resolve earlier immutable publication before settings/migration writes.
        reconcile(azure, baseline=baseline, reconcile_only=True, journal=journal)
        return
    try:
        rows = read_deployments_bounded(azure, phase='before any upload')
        observed = azure.observed_revision()
    except DeploymentStatusUnavailable:
        raise
    except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired) as exc:
        raise DeploymentReconciliationRequired('Pre-publication Azure state unavailable') from exc
    if observed is not None and observed not in {baseline, revision}:
        raise DeploymentDrift('Pre-publication live target differs from original baseline and candidate')
    active = any(row['status'] in (0, 1, 2) for row in rows)
    if observed is None or active:
        raise DeploymentReconciliationRequired('Pre-publication runtime or active deployment remains unverified')
    if journal is not None and getattr(journal, 'history_error', None) is not None and observed != revision:
        if observed != baseline:
            raise DeploymentReconciliationRequired('Original publication history unproven; no release mutation authorized') from journal.history_error
        print(
            f'{TARGETS[key]["releaseName"]}: prior immutable publication history is incomplete, '
            'but Azure is terminal at the exact preserved baseline with no active deployment; '
            'one fresh upload of the verified candidate is authorized.',
            flush=True,
        )


def prepare_transaction(target_names, baselines_raw, package_root, rollback_root, revision, output):
    keys = _RELEASE_AUTHORITY.selected_release_target_keys(target_names)
    baselines = _baseline_map(baselines_raw)
    digests = {key: verify_package(package_root / TARGETS[key]['package'], revision, TARGETS[key]['static']) for key in keys}
    identity = {'candidateRevision': revision, 'packageDigests': digests}
    plan_id = hashlib.sha256(json.dumps(identity, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    prior = None
    if os.environ.get('GITHUB_ACTIONS') == 'true':
        prior = _RELEASE_AUTHORITY.release_transaction_plan_history(
            os.environ['GITHUB_REPOSITORY'], plan_id, revision, digests,
            int(os.environ['GITHUB_RUN_ID']), int(os.environ['GITHUB_RUN_ATTEMPT']),
            os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN'])
        if prior is not None:
            baselines = _baseline_map(json.dumps(prior['targets']))
            if prior.get('historySnapshot') is not None:
                _RELEASE_AUTHORITY.import_release_history_snapshot(prior['historySnapshot'], revision)
    prior_targets = {row['app']: row for row in prior['targets']} if prior else {}

    def prepare_target(key):
        if key not in baselines:
            raise ValueError('Missing preserved transaction baseline')
        target = TARGETS[key]
        digest = digests[key]
        journal = operation_journal(key, revision, digest, baselines[key])
        baseline = journal.baseline if journal is not None else baselines[key]
        if baseline != baselines[key]:
            raise DeploymentDrift('Target operation disagrees with original transaction baseline')
        preserved = prior_targets.get(key, {}).get('rollbackEvidence')
        rollback = retained_rollback_package(rollback_root, key, baseline, preserved) if baseline != revision else None
        preflight_target(key, package_root / target['package'], revision, baseline, journal)
        return {'app': key, 'revision': baseline, 'packageDigest': digest, 'rollbackEvidence': rollback}

    prepared = {}
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, len(keys))) as executor:
        futures = {executor.submit(prepare_target, key): key for key in keys}
        for future in concurrent.futures.as_completed(futures):
            key = futures[future]
            prepared[key] = future.result()
    entries = [prepared[key] for key in keys]
    plan = {'schemaVersion': 1, 'planId': plan_id, 'candidateRevision': revision, 'targets': entries,
            'producingRun': int(os.environ.get('GITHUB_RUN_ID', '0')),
            'producingAttempt': int(os.environ.get('GITHUB_RUN_ATTEMPT', '1')),
            'historySnapshot': _RELEASE_AUTHORITY.export_release_history_snapshot(revision)}
    if os.environ.get('GITHUB_ACTIONS') == 'true':
        if prior is not None:
            plan = prior
        else:
            spec = importlib.util.spec_from_file_location('release_operation_evidence', Path(__file__).with_name('release-operation-evidence.py'))
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            module.publish_record('legend-release-transaction-plan-' + plan_id, plan)
    output.write_text(json.dumps(plan, sort_keys=True) + '\n')
    if os.environ.get('GITHUB_ENV'):
        with open(os.environ['GITHUB_ENV'], 'a') as stream:
            stream.write('TRANSACTION_PLAN_ID=' + plan_id + '\n')
            stream.write('REUSE_TRANSACTION_PLAN=true\n')
    return plan


def read_transaction_plan(path, revision, key=None):
    plan = json.loads(path.read_text())
    if plan.get('schemaVersion') != 1 or plan.get('candidateRevision') != revision:
        raise ValueError('Transaction plan does not bind immutable candidate')
    baselines = _baseline_map(json.dumps(plan.get('targets')))
    if not baselines or (key is not None and key not in baselines):
        raise ValueError('Target outside prepared transaction')
    for row in plan['targets']:
        if not re.fullmatch(r'[0-9a-f]{64}', row.get('packageDigest', '')):
            raise ValueError('Transaction package identity missing')
    identity = {'candidateRevision': revision, 'packageDigests': {row['app']: row['packageDigest'] for row in plan['targets']}}
    expected_id = hashlib.sha256(json.dumps(identity, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    if plan.get('planId') != expected_id:
        raise ValueError('Transaction plan content identity mismatch')
    if plan.get('historySnapshot') is not None:
        _RELEASE_AUTHORITY.import_release_history_snapshot(plan['historySnapshot'], revision)
    return plan


def publish_prepared_target(key, revision, package_root, plan, *, reconcile_only=False):
    row = next(row for row in plan['targets'] if row['app'] == key)
    target = TARGETS[key]
    digest = verify_package(package_root / target['package'], revision, target['static'])
    if digest != row['packageDigest']:
        raise ValueError('Prepared immutable package changed')
    return deploy_one(key, revision, package_root, baseline=row['revision'], reconcile_only=reconcile_only)


def publish_prepared_targets_parallel(target_names, revision, package_root, plan, results_root):
    """Publish independent prepared targets concurrently, then report each child.

    The all-target preflight transaction is still the write barrier. Each worker
    owns a different canonical app, immutable package digest, Azure deployment
    stream, and durable operation journal. A sibling failure never authorizes a
    replay of a successful target; final transaction reconciliation remains the
    sole commit decision after all workers settle.
    """
    keys = _RELEASE_AUTHORITY.selected_release_target_keys(target_names)
    if set(keys) != {row['app'] for row in plan['targets']}:
        raise ValueError('Parallel publication target scope changed')
    results_root.mkdir(parents=True, exist_ok=True)

    def record(key, payload):
        path = results_root / (key + '.json')
        temporary = path.with_suffix('.json.tmp')
        temporary.write_text(json.dumps(payload, sort_keys=True) + '\n')
        temporary.replace(path)

    def worker(key):
        try:
            outcome = publish_prepared_target(key, revision, package_root, plan)
            payload = {'schemaVersion': 1, 'target': key, 'success': True, 'outcome': outcome}
            record(key, payload)
            return payload
        except Exception as exc:
            payload = {
                'schemaVersion': 1,
                'target': key,
                'success': False,
                'errorType': type(exc).__name__,
            }
            record(key, payload)
            return payload

    results = {}
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, len(keys))) as executor:
        futures = {executor.submit(worker, key): key for key in keys}
        for future in concurrent.futures.as_completed(futures):
            key = futures[future]
            results[key] = future.result()

    failed = [key for key in keys if not results.get(key, {}).get('success')]
    if failed:
        raise RuntimeError('Canonical target publication failed: ' + ', '.join(failed))
    return results


def finalize_prepared_transaction(plan, package_root, revision, *, sleep=time.sleep):
    """Finalize durable receipts with one canonical bounded read-only retry policy."""
    pending = {row['app']: row for row in plan['targets']}
    last_errors = {}

    def finalize(row):
        target = TARGETS[row['app']]
        digest = verify_package(package_root / target['package'], revision, target['static'])
        if digest != row['packageDigest']:
            raise ValueError('Prepared immutable package changed')
        reconcile(
            target_azure(row['app'], package_root / target['package'], revision),
            baseline=row['revision'],
            reconcile_only=True,
            timeout=FINALIZE_RECONCILE_TIMEOUT_SECONDS,
            max_status_failures=1,
        )
        return row['app']

    for attempt in range(1, FINALIZE_RECONCILE_ATTEMPTS + 1):
        retryable = {}
        hard_errors = {}
        rows = list(pending.values())
        with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, len(rows))) as executor:
            futures = {executor.submit(finalize, row): row['app'] for row in rows}
            for future in concurrent.futures.as_completed(futures):
                key = futures[future]
                try:
                    future.result()
                    pending.pop(key, None)
                    last_errors.pop(key, None)
                except DeploymentDrift as exc:
                    hard_errors[key] = exc
                except DeploymentReconciliationRequired as exc:
                    retryable[key] = exc
                    last_errors[key] = exc
                except Exception as exc:
                    hard_errors[key] = exc

        if hard_errors:
            key = sorted(hard_errors)[0]
            raise hard_errors[key]
        if not pending:
            print(json.dumps({'revision': revision, 'transaction': 'committed',
                              'targets': [TARGETS[row['app']]['releaseName'] for row in plan['targets']]}, sort_keys=True))
            return
        if set(pending) != set(retryable):
            raise DeploymentReconciliationRequired('Final transaction proof is incomplete')
        if attempt < FINALIZE_RECONCILE_ATTEMPTS:
            unresolved = ', '.join(sorted(pending))
            print(
                f'::warning::Durable receipt proof remains unresolved for {unresolved}; '
                f'bounded read-only finalization retry {attempt}/{FINALIZE_RECONCILE_ATTEMPTS}.',
                flush=True,
            )
            sleep(attempt * FINALIZE_RETRY_DELAY_SECONDS)

    details = '; '.join(
        f'{key}: {last_errors[key]}' for key in sorted(pending) if key in last_errors
    )
    raise DeploymentReconciliationRequired(
        'Durable receipt proof remains unresolved after the canonical bounded finalization budget'
        + (f': {details}' if details else '')
    )

def transaction_disposition(plan, package_root, revision):
    def observe(row):
        key = row['app']
        target = TARGETS[key]
        digest = verify_package(package_root / target['package'], revision, target['static'])
        if digest != row['packageDigest']:
            raise ValueError('Disposition package differs from prepared transaction')
        journal = operation_journal(key, revision, digest, row['revision'])
        if journal is not None and getattr(journal, 'history_error', None) is not None:
            raise DeploymentReconciliationRequired('Deployment history incomplete; lease must remain held')
        azure = target_azure(key, package_root / target['package'], revision)
        rows = read_deployments_bounded(
            azure,
            phase='terminal disposition after committed transaction',
        )
        observed = azure.observed_revision()
        if any(item['status'] in (0, 1, 2) for item in rows) or observed not in {row['revision'], revision}:
            raise DeploymentReconciliationRequired('Provider target not terminal at preserved baseline/candidate')
        if journal is not None and journal.intent is not None:
            original = set(journal.intent['baselineDeploymentIds'])
            published = [item for item in rows if item['id'] not in original]
            if len(published) != 1 or published[0]['status'] not in (3, 4):
                raise DeploymentReconciliationRequired('Original upload outcome remains ambiguous; lease must remain held')
        return {'target': key, 'revision': observed, 'idle': True}

    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, len(plan['targets']))) as executor:
        futures = {executor.submit(observe, row): row['app'] for row in plan['targets']}
        by_target = {futures[future]: future.result() for future in concurrent.futures.as_completed(futures)}
    observations = [by_target[row['app']] for row in plan['targets']]
    return {'schemaVersion': 1, 'candidateRevision': revision, 'terminal': True, 'targets': observations}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--target', choices=TARGETS)
    mode.add_argument('--targets-json')
    parser.add_argument('--baselines-json')
    parser.add_argument('--package-root', default='/tmp/diagnostics-packages')
    parser.add_argument('--rollback-root', default='/tmp/rollback-packages')
    parser.add_argument('--prepare-only', action='store_true')
    parser.add_argument('--finalize-only', action='store_true')
    parser.add_argument('--disposition-only', action='store_true')
    parser.add_argument('--transaction-plan', type=Path)
    parser.add_argument('--reconcile-only', action='store_true', help='Never issue deployment writes; reconcile preserved immutable candidate')
    parser.add_argument('--publish-prepared-parallel', action='store_true',
                        help='Publish all prepared canonical targets concurrently after the all-target preflight barrier')
    parser.add_argument('--target-results-dir', type=Path, default=Path('/tmp/release-target-results'))
    args = parser.parse_args()
    _RELEASE_AUTHORITY.assert_protected_release_execution()

    revision = os.environ.get('APPLICATION_RELEASE_SHA') or os.environ.get('RELEASE_SHA')
    if not revision:
        raise SystemExit('APPLICATION_RELEASE_SHA is required')

    reconcile_only = args.reconcile_only
    if args.target:
        if args.transaction_plan is None:
            raise ValueError('Target publication requires an all-target preflight plan')
        plan = read_transaction_plan(args.transaction_plan, revision, args.target)
        publish_prepared_target(args.target, revision, Path(args.package_root), plan, reconcile_only=reconcile_only)
        return
    if args.publish_prepared_parallel:
        if args.transaction_plan is None:
            raise ValueError('Parallel target publication requires an all-target preflight plan')
        plan = read_transaction_plan(args.transaction_plan, revision)
        names = json.loads(args.targets_json)
        publish_prepared_targets_parallel(
            names,
            revision,
            Path(args.package_root),
            plan,
            args.target_results_dir,
        )
        return
    if args.disposition_only:
        plan = read_transaction_plan(args.transaction_plan, revision)
        print(json.dumps(transaction_disposition(plan, Path(args.package_root), revision), sort_keys=True))
        return
    if args.finalize_only:
        plan = read_transaction_plan(args.transaction_plan, revision)
        names = json.loads(args.targets_json)
        if set(_RELEASE_AUTHORITY.selected_release_target_keys(names)) != {row['app'] for row in plan['targets']}:
            raise ValueError('Finalization target scope changed')
        finalize_prepared_transaction(
            plan,
            Path(args.package_root),
            revision,
        )
        return

    if args.baselines_json is None:
        raise SystemExit('--baselines-json is required for transactional deployment')
    names = json.loads(args.targets_json)
    if args.prepare_only:
        if args.transaction_plan is None:
            raise ValueError('Prepared transaction output path required')
        prepare_transaction(names, args.baselines_json, Path(args.package_root), Path(args.rollback_root), revision, args.transaction_plan)
        return
    raise ValueError('Select canonical prepare, target publication, or finalization mode')


if __name__ == '__main__':
    main()
