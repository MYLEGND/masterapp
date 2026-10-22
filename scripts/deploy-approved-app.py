#!/usr/bin/env python3
"""One immutable upload, followed by read-only deployment/runtime reconciliation.

A timeout is not a failed Azure operation. Unknown/active operations are never
replayed. Terminal provider failures require bounded repair through the canonical
release lifecycle; no alternate upload path can bypass durable operation intent.
"""
import argparse
import concurrent.futures
import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import time
import urllib.request
import urllib.error
import urllib.parse
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


class DeploymentSubmissionNotEntered(DeploymentReconciliationRequired):
    """This invocation stopped before submit; prior operation history remains unresolved."""


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
        except DeploymentDrift:
            # Identity conflicts are terminal evidence, never transient reads.
            raise
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
        self._submission_correlation_supported = False
        self._submission_kind = None
        self.submission_reference = None

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

    def deployment_log(self, deployment_id):
        # One read of an exact operation; never print CLI stdout/stderr directly.
        result = subprocess.run(
            ['az', 'webapp', 'log', 'deployment', 'show', '-g', _RELEASE_AUTHORITY.RELEASE_RESOURCE_GROUP,
             '-n', self.app, '--deployment-id', deployment_id, '--only-show-errors', '-o', 'json'],
            capture_output=True, text=True, timeout=30)
        if result.returncode:
            raise RuntimeError('AZURE_DIAGNOSTIC_READ_FAILED')
        if len(result.stdout.encode('utf-8')) > DIAGNOSTIC_MAX_BYTES:
            raise ValueError('AZURE_DIAGNOSTIC_RESPONSE_TOO_LARGE')
        return json.loads(result.stdout)

    def prepare_submission(self):
        """Select the supported OneDeploy contract before durable write intent."""
        self._submission_correlation_supported = False
        self._submission_kind = None
        if self.app not in {target['releaseName'] for target in TARGETS.values()}:
            raise DeploymentSubmissionNotEntered('Canonical deployment target required for correlation')
        try:
            help_result = subprocess.run(['az', 'webapp', 'deploy', '--help'],
                                         capture_output=True, text=True, timeout=20)
            if (help_result.returncode or len(help_result.stdout.encode('utf-8')) > 131072 or
                    not re.search(r'(?m)^\s*--deploymentTag(?:\s|:|$)', help_result.stdout)):
                raise ValueError('unsupported deployment correlation option')
            host_result = subprocess.run(
                ['az', 'webapp', 'show', '-g', _RELEASE_AUTHORITY.RELEASE_RESOURCE_GROUP,
                 '-n', self.app, '--query', '{name:name,reserved:reserved,kind:kind}', '--only-show-errors', '-o', 'json'],
                capture_output=True, text=True, timeout=20)
            if host_result.returncode or len(host_result.stdout.encode('utf-8')) > 4096:
                raise ValueError('unavailable deployment host metadata')
            host = json.loads(host_result.stdout)
            kind = host.get('kind') if isinstance(host, dict) else None
            # CLI 2.91.0 sends deploymentTag only for Linux web apps. Windows
            # uses the same OneDeploy ZIP API but returns a server status ID.
            linux_webapp = (isinstance(kind, str) and {'app', 'linux'} <= set(kind.split(',')) and
                            not any(marker in kind for marker in ('functionapp', 'workflowapp')))
            windows_webapp = (isinstance(kind, str) and set(kind.split(',')) <= {'app'} and
                              'app' in kind.split(',') and host.get('reserved') is False)
            if (not isinstance(host, dict) or host.get('name') != self.app or
                    not ((host.get('reserved') is True and linux_webapp) or windows_webapp)):
                raise ValueError('unsupported deployment host')
            self._submission_kind = ('azure_onedeploy_deployment_tag_v1' if linux_webapp
                                     else 'azure_onedeploy_windows_intent_v1')
        except (OSError, ValueError, subprocess.TimeoutExpired):
            # Neither provider output nor CLI error details belong in the log.
            raise DeploymentSubmissionNotEntered('Supported OneDeploy host contract could not be confirmed; no intent or upload created') from None
        self._submission_correlation_supported = True
        return self._submission_kind

    def submit(self, correlation=None):
        if (not self._submission_correlation_supported or not isinstance(correlation, dict) or
                set(correlation) != {'kind', 'value'} or
                correlation.get('kind') != self._submission_kind or
                not isinstance(correlation.get('value'), str) or
                not re.fullmatch('[a-f0-9]{64}', correlation['value'])):
            raise DeploymentSubmissionNotEntered('Verified durable deployment correlation required before upload')
        # Async avoids a long synchronous gateway request. CLI runtime tracking is
        # replaced by exact-revision checks below, not waived. Status preflight
        # already warmed SCM; disabling CLI warmup prevents its exception fallback
        # from replaying a POST. A subprocess timeout leaves Azure running untouched.
        # Portal and Website must use provider-tracked publication. Historical
        # Portal async uploads could make exact bytes live without producing the
        # operation ID that the durable receipt gate requires. A synchronous
        # tracked submit does not waive runtime or deployment-history checks.
        # Other apps retain their existing async submission contract.
        tracked = self.app == "masterapp-portal" or (
            self.static and self.app == "masterapp-website")
        command = ['az', 'webapp', 'deploy', '-g', _RELEASE_AUTHORITY.RELEASE_RESOURCE_GROUP, '-n', self.app,
                   '--src-path', str(self.package), '--type', 'zip', '--clean', 'true',
                   '--restart', 'true', '--async', 'false' if tracked else 'true',
                   '--track-status', 'true' if tracked else 'false',
                   '--enable-kudu-warmup', 'true' if tracked else 'false',
                   '--timeout', '120000',
                   '--enriched-errors', 'true', '--only-show-errors', '-o', 'json']
        # Windows ignores deploymentTag. Its local write-ahead identity is not
        # represented as caller metadata accepted by Azure. Both platforms keep
        # one submission and exact unique post-intent provider reconciliation.
        if self._submission_kind == 'azure_onedeploy_deployment_tag_v1':
            command.extend(['--deploymentTag', correlation['value']])
        self.submission_reference = None
        try:
            # Allow the tracked provider operation to return without forcing a
            # local timeout before Azure has completed its deployment. A
            # timeout remains ambiguous and is NEVER replayed.
            result = subprocess.run(command, capture_output=True, text=True,
                                    timeout=300 if tracked else 180)
            if result.returncode:
                print('::warning::Upload response was unsuccessful; reconciling Azure without resubmission.', flush=True)
            if result.returncode == 0:
                # Preserve only a validated, sanitized provider operation ID in
                # the immutable Actions log. Never emit raw CLI output, bearer
                # tokens, publishing credentials, or speculative success.
                try:
                    payload = json.loads(result.stdout)
                    reference = (payload.get('deploymentId') or payload.get('id')
                                 if isinstance(payload, dict) else None)
                    if isinstance(reference, str) and re.fullmatch(r'[a-zA-Z0-9_.:-]{1,256}', reference):
                        self.submission_reference = reference
                        print('AZURE_TRACKED_DEPLOYMENT_REFERENCE:' + self.app + ':' + reference, flush=True)
                except (ValueError, TypeError, AttributeError):
                    pass
            return result.returncode == 0
        except subprocess.TimeoutExpired:
            print('::warning::Upload response timed out; Azure may still be working. No resubmission.', flush=True)
            return False


DIAGNOSTIC_MAX_BYTES = 262144
DIAGNOSTIC_MAX_ROWS = 128
DIAGNOSTIC_MAX_NESTED = 2
DIAGNOSTIC_REASON_PATTERNS = (
    ('DISK_SPACE_ERROR', r'no space left on device|not enough space on the disk|disk quota exceeded'),
    ('FILE_LOCK_ERROR', r'being used by another process|file.+is locked|sharing violation'),
    ('PERMISSION_ERROR', r'permission denied|access is denied|unauthorizedaccessexception'),
    ('ARCHIVE_ERROR', r'invalid zip|badzipfile|end.of.central.directory|archive.+corrupt'),
    ('DEPLOYMENT_CONFLICT', r'another deployment.+in progress|deployment lock|409 conflict'),
    ('STARTUP_ERROR', r'failed to start|container.+did not respond|application startup exception'),
    ('TIMEOUT_ERROR', r'timed out|timeout expired|operation.+timeout'),
    ('CANCELLED_OPERATION', r'operation.+cancelled|operation.+canceled|taskcanceledexception'),
    ('NONZERO_COMMAND_EXIT', r'exit code [1-9][0-9]*|exited with code [1-9][0-9]*'),
    ('PROVIDER_FAILURE_MARKER', r'deployment failed|deploy failed'),
)


def validate_deployment_diagnostic(target, revision, deployment_id=None):
    if target not in TARGETS or not re.fullmatch('[a-f0-9]{40}', revision or ''):
        raise ValueError('AZURE_DIAGNOSTIC_IDENTITY_INVALID')
    if deployment_id is not None and not isinstance(deployment_id, str):
        raise ValueError('AZURE_DIAGNOSTIC_OPERATION_INVALID')
    if deployment_id and (not re.fullmatch('[A-Za-z0-9][A-Za-z0-9_-]{3,127}', deployment_id)
                          or deployment_id.lower() in {'latest', 'active', 'current'}):
        raise ValueError('AZURE_DIAGNOSTIC_OPERATION_INVALID')
    if os.environ.get('GITHUB_ACTIONS') == 'true':
        reference = 'refs/heads/' + _RELEASE_AUTHORITY.FAST_TRACK_PR_BASE
        repository = os.environ.get('GITHUB_REPOSITORY', '')
        if (os.environ.get('GITHUB_EVENT_NAME') != 'workflow_dispatch'
                or os.environ.get('GITHUB_REF') != reference
                or not re.fullmatch('[a-f0-9]{40}', os.environ.get('GITHUB_SHA', ''))
                or not re.fullmatch('[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository)
                or os.environ.get('GITHUB_WORKFLOW_REF') != repository +
                    '/.github/workflows/deployment-diagnostics.yml@' + reference):
            raise ValueError('AZURE_DIAGNOSTIC_EXECUTION_INVALID')


def _diagnostic_time(value):
    if not isinstance(value, str) or not re.fullmatch(r'[0-9T:.Z+\-]{19,40}', value):
        return None
    try:
        parsed = datetime.datetime.fromisoformat(value.replace('Z', '+00:00'))
        return parsed.astimezone(datetime.timezone.utc).isoformat() if parsed.tzinfo is not None else None
    except ValueError:
        return None


def _diagnostic_log_entries(rows, level):
    if not isinstance(rows, list) or len(rows) > DIAGNOSTIC_MAX_ROWS:
        raise ValueError('AZURE_DIAGNOSTIC_LOG_INVALID')
    entries, links = [], []
    for row in rows:
        if not isinstance(row, dict) or not isinstance(row.get('message', ''), str):
            raise ValueError('AZURE_DIAGNOSTIC_LOG_INVALID')
        message = row.get('message', '')
        if len(message.encode('utf-8')) > 32768:
            raise ValueError('AZURE_DIAGNOSTIC_LOG_INVALID')
        # Only fixed classifications and fingerprints cross the log boundary.
        # Provider text, paths, actors, credentials and URLs are never echoed.
        entries.append(dict(level=level,
            type=row.get('type') if type(row.get('type')) is int and row['type'] in (0, 1, 2) else None,
            time=_diagnostic_time(row.get('log_time')),
            reasonCodes=[code for code, pattern in DIAGNOSTIC_REASON_PATTERNS
                         if re.search(pattern, message, re.I)],
            messageSha256=hashlib.sha256(message.encode('utf-8')).hexdigest()))
        if row.get('details_url') is not None:
            links.append(row['details_url'])
    return entries, links


def _diagnostic_child_url(value, app, deployment_id):
    if not isinstance(value, str) or len(value) > 512:
        return None
    try:
        parsed = urllib.parse.urlsplit(value)
    except ValueError:
        return None
    host = app + '.scm.azurewebsites.net'
    prefix = '/api/deployments/' + deployment_id + '/log/'
    if (parsed.geturl() != value or parsed.scheme != 'https' or parsed.netloc != host or parsed.query or parsed.fragment
            or not parsed.path.startswith(prefix)
            or not re.fullmatch('[A-Za-z0-9_-]{1,128}', parsed.path[len(prefix):])):
        return None
    return value


class _DiagnosticNoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def _diagnostic_nested_logs(urls, app, deployment_id):
    # Existing OIDC Azure authority already supports this GET-only Kudu path.
    # Never request publishing credentials; the bearer exists only in memory.
    if (len(urls) > DIAGNOSTIC_MAX_NESTED or
            any(_diagnostic_child_url(url, app, deployment_id) is None for url in urls)):
        return [], ['DETAIL_SCOPE_REJECTED']
    try:
        result = subprocess.run(['az', 'account', 'get-access-token', '--resource',
            'https://management.azure.com/', '--query', 'accessToken', '-o', 'tsv'],
            capture_output=True, text=True, timeout=15)
        token = result.stdout.strip()
        if result.returncode or len(token) > 16384 or not re.fullmatch(r'[A-Za-z0-9_.~+/-]+={0,2}', token):
            return [], ['AUTHENTICATED_READ_UNAVAILABLE']
    except (OSError, subprocess.TimeoutExpired):
        return [], ['AUTHENTICATED_READ_UNAVAILABLE']
    opener = urllib.request.build_opener(_DiagnosticNoRedirect())
    entries, states = [], []
    for url in urls:
        try:
            request = urllib.request.Request(url, method='GET',
                headers={'Authorization': 'Bearer ' + token, 'Accept': 'application/json'})
            with opener.open(request, timeout=15) as response:
                if response.status != 200 or response.geturl() != url:
                    states.append('RESPONSE_IDENTITY_UNPROVEN')
                    continue
                body = response.read(DIAGNOSTIC_MAX_BYTES + 1)
            if len(body) > DIAGNOSTIC_MAX_BYTES:
                states.append('RESPONSE_TOO_LARGE')
                continue
            observed, ignored_links = _diagnostic_log_entries(json.loads(body), 'detail')
            entries.extend(observed)
            states.append('OBSERVED_WITH_UNFOLLOWED_DETAILS' if ignored_links else 'OBSERVED')
        except urllib.error.HTTPError as exc:
            states.append('REDIRECT_REFUSED' if exc.code in (301, 302, 303, 307, 308) else 'HTTP_READ_FAILED')
        except (OSError, ValueError, TypeError):
            states.append('READ_UNAVAILABLE_OR_INVALID')
    return entries, states


def observe_azure_deployment(target, revision, deployment_id=None, *, validate_only=False):
    validate_deployment_diagnostic(target, revision, deployment_id)
    report = dict(kind='AZURE_DEPLOYMENT_READ_ONLY', target=target, applicationRevision=revision,
                  applicationRevisionIsCorrelationOnly=True, reasonCodesAreTextMatches=True,
                  authorizesMutation=False, releaseProof=False)
    if validate_only:
        return dict(report, readStatus='INPUTS_VALIDATED')
    azure = target_azure(target, None, revision)
    if not deployment_id:
        report.update(mode='history', historyIsComplete=False, emptyHistoryProvesNonEntry=False)
        try:
            rows = azure.deployments()
            safe = [dict(id=row['id'], status=row['status'],
                         received=_diagnostic_time(row.get('received_time')),
                         started=_diagnostic_time(row.get('start_time')),
                         ended=_diagnostic_time(row.get('end_time')))
                    for row in rows[:20]
                    if re.fullmatch('[A-Za-z0-9][A-Za-z0-9_.:-]{0,255}', row['id'])]
            report.update(readStatus='OBSERVED', deployments=safe,
                          outputTruncated=len(rows) != len(safe))
        except subprocess.TimeoutExpired:
            report.update(readStatus='READ_TIMEOUT', deployments=[])
        except (OSError, ValueError, RuntimeError, TypeError):
            report.update(readStatus='READ_UNAVAILABLE_OR_INVALID', deployments=[])
        report['observedRuntimeRevision'] = azure.observed_revision()
        return report
    report.update(mode='exact_operation', deploymentId=deployment_id)
    try:
        entries, links = _diagnostic_log_entries(azure.deployment_log(deployment_id), 'root')
        urls = list(dict.fromkeys(url for value in links
                    if (url := _diagnostic_child_url(value, azure.app, deployment_id)) is not None))
        report.update(readStatus='OBSERVED', rejectedDetailLinks=sum(
            _diagnostic_child_url(value, azure.app, deployment_id) is None for value in links),
            detailsTruncated=len(urls) > DIAGNOSTIC_MAX_NESTED)
        nested, states = _diagnostic_nested_logs(urls[:DIAGNOSTIC_MAX_NESTED], azure.app, deployment_id) if urls else ([], [])
        report.update(entries=entries + nested, nestedReadStates=states)
        report['reasonCodes'] = sorted({code for entry in report['entries'] for code in entry['reasonCodes']})
        if not report['reasonCodes']:
            report['reasonCodes'] = ['NO_CLASSIFIED_FAILURE_DETAIL']
    except subprocess.TimeoutExpired:
        report.update(readStatus='READ_TIMEOUT')
    except (OSError, ValueError, RuntimeError, TypeError):
        report.update(readStatus='READ_UNAVAILABLE_OR_INVALID')
    return report


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
        if failed is not None:
            raise DeploymentReconciliationRequired(
                f"Azure deployment {failed['id']} failed; matching runtime SHA does not prove "
                "the requested artifact. Preserve live state; no automatic replay.")
        if active:
            # Even exact provenance cannot authorize success while another upload
            # may still replace/restart that revision. Wait for Azure to settle.
            stable = 0
        elif live:
            # Matching source alone cannot attach this package's digest to an
            # unrelated earlier deployment. An exact intent (on resume) plus a
            # unique successful post-intent operation establishes artifact proof.
            if not submitted:
                raise DeploymentReconciliationRequired(
                    'Live source matches but exact artifact publication intent is unproven; no upload authorized')
            if new and new[0]['status'] == 4:
                stable += 1
                if stable >= 2:
                    if journal is not None:
                        try:
                            journal.record_success([row['id'] for row in new if row['status'] == 4])
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
                correlation = None
                if journal is not None:
                    try:
                        submission_kind = azure.prepare_submission()
                        allow_recovered_baseline = (
                            getattr(journal, 'history_error', None) is not None and
                            baseline is not None and
                            observed == baseline and
                            not active
                        )
                        submission_options = ({'submission_kind': submission_kind}
                                              if submission_kind == 'azure_onedeploy_windows_intent_v1' else {})
                        allowed = journal.before_submit(
                            baseline_ids,
                            allow_recovered_baseline=allow_recovered_baseline,
                            **submission_options,
                        )
                        if allowed:
                            correlation = journal.submission_correlation()
                    except Exception as exc:
                        raise DeploymentSubmissionNotEntered('Durable upload intent could not be proven; no write authorized') from exc
                    if not allowed:
                        raise DeploymentReconciliationRequired('Existing upload intent requires read-only reconciliation')
                submitted = True  # Set before I/O: ambiguous responses never replay.
                print('Submitting the verified immutable ZIP once.', flush=True)
                azure.submit(correlation)
                reference = getattr(azure, 'submission_reference', None)
                if (journal is not None and reference is not None and
                        correlation.get('kind') == 'azure_onedeploy_windows_intent_v1'):
                    # This is a returned status observation, not idempotency or
                    # success. Lost receipt readback leaves the original intent
                    # intact and the next invocation reconciliation-only.
                    journal.record_submission_observation(reference)
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


def operation_journal(key, revision, digest, baseline, *, candidate_revision=None, component_source=None):
    if os.environ.get('GITHUB_ACTIONS') != 'true':
        return None
    if baseline is None:
        raise ValueError('Preserved transaction baseline required')
    spec = importlib.util.spec_from_file_location('release_operation_evidence', Path(__file__).with_name('release-operation-evidence.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.OperationJournal(target=key, application_revision=revision, package_digest=digest, baseline=baseline, authority=_RELEASE_AUTHORITY,
                                   candidate_revision=candidate_revision, component_source=component_source)


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
    evidence = ({'reusable': True, 'runId': retained['runId'], 'packageArtifact': retained['artifact'],
                 **({'artifactId': retained['artifactId']} if 'artifactId' in retained else {})}
                if retained is not None else _RELEASE_AUTHORITY.compute_rollback_evidence(repository, revision, key))
    if not evidence.get('reusable'):
        raise DeploymentReconciliationRequired('Original preserved rollback package evidence unavailable')
    import shutil
    import tempfile
    with tempfile.TemporaryDirectory(prefix='legend-rollback-') as temporary:
        folder = Path(temporary)
        args = dict(artifact_id=evidence['artifactId']) if evidence.get('artifactId') is not None else {}
        _RELEASE_AUTHORITY._download_run_artifact(repository, evidence['runId'], evidence['packageArtifact'], folder, **args)
        candidate = folder / ('package.zip' if (folder / 'package.zip').exists() else target['package'])
        if evidence.get('artifactId') is not None:
            expected = retained['packageDigest'] if retained is not None else evidence.get('packageDigest')
            if not re.fullmatch('[a-f0-9]{64}', expected or ''):
                raise ValueError('Rollback component digest missing')
            (candidate.parent/'SHA256SUMS').write_text(expected+'  '+candidate.name+'\n')
        digest = verify_package(candidate, revision, target['static'])
        if retained is not None and digest != retained['packageDigest']:
            raise ValueError('Preserved rollback artifact digest changed')
        destination = root / f'diagnostics-rollback-{key}-preserved'
        destination.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(candidate, destination / 'package.zip')
        (destination / 'SHA256SUMS').write_text(digest + '  package.zip\n')
        receipt = {'artifact': evidence['packageArtifact'], 'runId': evidence['runId'],
                   'revision': revision, 'packageDigest': digest}
        if evidence.get('artifactId') is not None:
            receipt['artifactId'] = evidence['artifactId']
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
    if observed == revision:
        raise DeploymentReconciliationRequired('Live source matches but requested artifact is unproven before production mutation')
    if journal is not None and getattr(journal, 'history_error', None) is not None and observed != revision:
        if observed != baseline:
            raise DeploymentReconciliationRequired('Original publication history unproven; no release mutation authorized') from journal.history_error
        print(
            f'{TARGETS[key]["releaseName"]}: prior immutable publication history is incomplete, '
            'but Azure is terminal at the exact preserved baseline with no active deployment; '
            'one fresh upload of the verified candidate is authorized.',
            flush=True,
        )


def transaction_target_material(plan, row, revision):
    """Consume a validated plan without conflating authorization and byte producer."""
    if plan.get('schemaVersion') == 1:
        if 'targetMaterial' in row:
            raise ValueError('Legacy transaction contains unversioned target material')
        return dict(producerRevision=revision, packageDigest=row['packageDigest'])
    if plan.get('schemaVersion') != 2:
        raise ValueError('Unsupported transaction schema')
    spec = importlib.util.spec_from_file_location('transaction_package_owner', Path(__file__).with_name('release-package.py'))
    owner = importlib.util.module_from_spec(spec); spec.loader.exec_module(owner)
    material = owner.validate_target_material(row['app'], row.get('targetMaterial'))
    if material['packageDigest'] != row['packageDigest']:
        raise ValueError('Transaction target material digest mismatch')
    return material


def transaction_target_journal(plan, row, revision, material):
    args = {}
    if plan['schemaVersion'] == 2:
        args = dict(candidate_revision=revision, component_source=material['source'])
    return operation_journal(row['app'], material['producerRevision'], material['packageDigest'], row['revision'], **args)


def prepare_transaction(target_names, baselines_raw, package_root, rollback_root, revision, output):
    keys = _RELEASE_AUTHORITY.selected_release_target_keys(target_names)
    baselines = _baseline_map(baselines_raw)
    materials = None
    manifest_path = package_root / 'manifest.json'
    if manifest_path.exists():
        spec = importlib.util.spec_from_file_location('transaction_package_owner', Path(__file__).with_name('release-package.py'))
        owner = importlib.util.module_from_spec(spec); spec.loader.exec_module(owner)
        manifest = owner.verify_all(revision, package_root)
        if manifest['schema'] == owner.CONTENT_PACKAGE_SCHEMA:
            materials = {key: {name: value for name, value in owner.package_target_material(manifest, key).items()
                               if name != 'legacy'} for key in keys}
            observed_rows = {row['app']: row for row in json.loads(baselines_raw)}
            if any(observed_rows.get(key, {}).get('targetMaterial') != material for key, material in materials.items()):
                raise ValueError('Package target material differs from authenticated baseline descriptor')
    digests = {key: verify_package(package_root / TARGETS[key]['package'],
        materials[key]['producerRevision'] if materials is not None else revision, TARGETS[key]['static']) for key in keys}
    plan_id = _RELEASE_AUTHORITY.release_transaction_identity(revision, digests, materials)
    prior = None
    if os.environ.get('GITHUB_ACTIONS') == 'true':
        material_args = dict(target_materials=materials) if materials is not None else {}
        prior = _RELEASE_AUTHORITY.release_transaction_plan_history(
            os.environ['GITHUB_REPOSITORY'], plan_id, revision, digests,
            int(os.environ['GITHUB_RUN_ID']), int(os.environ['GITHUB_RUN_ATTEMPT']),
            os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN'], **material_args)
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
        desired = materials[key]['producerRevision'] if materials is not None else revision
        journal_args = dict(candidate_revision=revision, component_source=materials[key]['source']) if materials is not None else {}
        journal = operation_journal(key, desired, digest, baselines[key], **journal_args)
        baseline = journal.baseline if journal is not None else baselines[key]
        if baseline != baselines[key]:
            raise DeploymentDrift('Target operation disagrees with original transaction baseline')
        preserved = prior_targets.get(key, {}).get('rollbackEvidence')
        rollback = retained_rollback_package(rollback_root, key, baseline, preserved) if baseline != desired else None
        preflight_target(key, package_root / target['package'], desired, baseline, journal)
        row = {'app': key, 'revision': baseline, 'packageDigest': digest, 'rollbackEvidence': rollback}
        if materials is not None:
            row['targetMaterial'] = materials[key]
        return row

    prepared = {}
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, len(keys))) as executor:
        futures = {executor.submit(prepare_target, key): key for key in keys}
        for future in concurrent.futures.as_completed(futures):
            key = futures[future]
            prepared[key] = future.result()
    entries = [prepared[key] for key in keys]
    plan = {'schemaVersion': 2 if materials is not None else 1, 'planId': plan_id, 'candidateRevision': revision, 'targets': entries,
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
    if plan.get('schemaVersion') not in (1, 2) or plan.get('candidateRevision') != revision:
        raise ValueError('Transaction plan does not bind immutable candidate')
    baselines = _baseline_map(json.dumps(plan.get('targets')))
    if not baselines or (key is not None and key not in baselines):
        raise ValueError('Target outside prepared transaction')
    for row in plan['targets']:
        if not re.fullmatch(r'[0-9a-f]{64}', row.get('packageDigest', '')):
            raise ValueError('Transaction package identity missing')
    material = {row['app']: transaction_target_material(plan, row, revision) for row in plan['targets']}
    expected_id = _RELEASE_AUTHORITY.release_transaction_identity(revision,
        {row['app']: row['packageDigest'] for row in plan['targets']}, material if plan['schemaVersion'] == 2 else None)
    if plan.get('planId') != expected_id:
        raise ValueError('Transaction plan content identity mismatch')
    if plan.get('historySnapshot') is not None:
        _RELEASE_AUTHORITY.import_release_history_snapshot(plan['historySnapshot'], revision)
    return plan


def publish_prepared_target(key, revision, package_root, plan, *, reconcile_only=False):
    row = next(row for row in plan['targets'] if row['app'] == key)
    target = TARGETS[key]
    material = transaction_target_material(plan, row, revision)
    desired = material['producerRevision']
    digest = verify_package(package_root / target['package'], desired, target['static'])
    if digest != row['packageDigest']:
        raise ValueError('Prepared immutable package changed')
    args = dict(journal=transaction_target_journal(plan, row, revision, material)) if plan['schemaVersion'] == 2 else {}
    return deploy_one(key, desired, package_root, baseline=row['revision'], reconcile_only=reconcile_only, **args)


def publish_prepared_targets_parallel(target_names, revision, package_root, plan, results_root):
    """Publish independent prepared targets concurrently and record non-terminal evidence.

    The all-target preflight transaction is still the write barrier. Each worker
    owns a different canonical app, immutable package digest, Azure deployment
    stream, and durable operation journal. First-pass results are deliberately
    not deployment-success receipts: exact-live observation can precede durable
    receipt transport. Only final transaction reconciliation plus canonical live
    verification may establish deployed-success truth.
    """
    keys = _RELEASE_AUTHORITY.selected_release_target_keys(target_names)
    if set(keys) != {row['app'] for row in plan['targets']}:
        raise ValueError('Parallel publication target scope changed')
    results_root.mkdir(parents=True, exist_ok=True)
    live_outcomes = {
        'deployed',
        'preserved',
        'deployed-receipt-pending',
        'preserved-receipt-pending',
    }
    durable_outcomes = {'deployed', 'preserved'}

    def record(key, payload):
        path = results_root / (key + '.json')
        temporary = path.with_suffix('.json.tmp')
        temporary.write_text(json.dumps(payload, sort_keys=True) + '\n')
        temporary.replace(path)

    def worker(key):
        row = next(row for row in plan['targets'] if row['app'] == key)
        material = transaction_target_material(plan, row, revision)
        try:
            outcome = publish_prepared_target(key, revision, package_root, plan)
            if outcome not in live_outcomes:
                raise RuntimeError('Unrecognized canonical first-pass publication outcome')
            durable = outcome in durable_outcomes
            payload = {
                'schemaVersion': 2,
                'phase': 'publication',
                'target': key,
                'candidateRevision': revision,
                'targetMaterial': material,
                'liveProven': True,
                'durableReceiptProven': durable,
                'outcome': outcome,
            }
            record(key, payload)
            return payload
        except Exception as exc:
            payload = {
                'schemaVersion': 2,
                'phase': 'publication',
                'target': key,
                'candidateRevision': revision,
                'targetMaterial': material,
                'liveProven': False,
                'durableReceiptProven': False,
                'errorType': type(exc).__name__,
            }
            # Positive same-invocation evidence only. A retained/unknown prior
            # intent is never converted into proof that the operation never ran.
            payload['failure'] = {
                'code': ('INTENT_READBACK_UNPROVEN' if isinstance(exc, DeploymentSubmissionNotEntered)
                         else 'PUBLICATION_OUTCOME_UNRESOLVED'),
                'invocationSubmission': ('not-entered' if isinstance(exc, DeploymentSubmissionNotEntered)
                                         else 'may-have-entered'),
                'operationOutcome': 'unresolved',
                'replayAuthorized': False,
            }
            record(key, payload)
            # Bound to immutable target material; no exception strings/provider
            # payloads. The generated workflow retains these files on failure.
            print('LEGEND_PUBLICATION_FAILURE=' + json.dumps(payload, sort_keys=True), flush=True)
            return payload

    results = {}
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, len(keys))) as executor:
        futures = {executor.submit(worker, key): key for key in keys}
        for future in concurrent.futures.as_completed(futures):
            key = futures[future]
            results[key] = future.result()

    failed = [key for key in keys if not results.get(key, {}).get('liveProven')]
    if failed:
        raise RuntimeError('Canonical target publication failed before exact-live observation: ' + ', '.join(failed))
    return results

def finalize_prepared_transaction(plan, package_root, revision, *, sleep=time.sleep):
    """Finalize durable receipts with one canonical bounded read-only retry policy."""
    pending = {row['app']: row for row in plan['targets']}
    last_errors = {}

    def finalize(row):
        target = TARGETS[row['app']]
        material = transaction_target_material(plan, row, revision)
        desired = material['producerRevision']
        digest = verify_package(package_root / target['package'], desired, target['static'])
        if digest != row['packageDigest']:
            raise ValueError('Prepared immutable package changed')
        journal = transaction_target_journal(plan, row, revision, material)
        if journal is None:
            raise ValueError('Finalization requires the durable deployment journal')
        reconcile(
            target_azure(row['app'], package_root / target['package'], desired),
            baseline=row['revision'],
            reconcile_only=True,
            journal=journal,
            require_receipt=True,
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
        material = transaction_target_material(plan, row, revision)
        desired = material['producerRevision']
        digest = verify_package(package_root / target['package'], desired, target['static'])
        if digest != row['packageDigest']:
            raise ValueError('Disposition package differs from prepared transaction')
        journal = transaction_target_journal(plan, row, revision, material)
        if journal is not None and getattr(journal, 'history_error', None) is not None:
            raise DeploymentReconciliationRequired('Deployment history incomplete; lease must remain held')
        azure = target_azure(key, package_root / target['package'], desired)
        rows = read_deployments_bounded(
            azure,
            phase='terminal disposition after committed transaction',
        )
        observed = azure.observed_revision()
        if any(item['status'] in (0, 1, 2) for item in rows) or observed not in {row['revision'], desired}:
            raise DeploymentReconciliationRequired('Provider target not terminal at preserved baseline/candidate')
        if journal is not None and journal.intent is not None:
            original = set(journal.intent['baselineDeploymentIds'])
            published = [item for item in rows if item['id'] not in original]
            if len(published) != 1 or published[0]['status'] not in (3, 4):
                raise DeploymentReconciliationRequired('Original upload outcome remains ambiguous; lease must remain held')
        result = {'target': key, 'revision': observed, 'idle': True}
        if plan['schemaVersion'] == 2:
            result['targetMaterial'] = material
        return result

    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, len(plan['targets']))) as executor:
        futures = {executor.submit(observe, row): row['app'] for row in plan['targets']}
        by_target = {futures[future]: future.result() for future in concurrent.futures.as_completed(futures)}
    observations = [by_target[row['app']] for row in plan['targets']]
    return {'schemaVersion': 1, 'candidateRevision': revision, 'terminal': True, 'targets': observations}

def verify_fast_track_favicon(key):
    paths = {'website': ('/favicon.jpg', 'image/jpeg', bytes([255,216,255])),
             'protect': ('/images/favicon/legend-favicon.jpg', 'image/jpeg', bytes([255,216,255])),
             'parfait': ('/images/favicon/parfait-logo.png', 'image/png', bytes([137,80,78,71]))}
    if key not in paths:
        return
    path, mime, signature = paths[key]
    request = urllib.request.Request('https://' + TARGETS[key]['host'] + path,
        headers={'Cache-Control': 'no-cache'})
    with urllib.request.urlopen(request, timeout=20) as response:
        if response.status != 200 or mime not in response.headers.get('Content-Type', '') or not response.read(64).startswith(signature):
            raise ValueError('FAST_TRACK_LIVE_FAVICON_UNPROVEN:' + key)
    print('FAST_TRACK_LIVE_FAVICON_VERIFIED:' + key)


def fast_track_restore_package(key, directory):
    # The canonical identity owner supplies the single observation budget.
    run = _RELEASE_AUTHORITY.assert_fast_track_release_execution(target=key)
    repository = os.environ['GITHUB_REPOSITORY']
    token = os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
    name = 'fast-track-package-' + run['head_sha'] + '-' + key
    payload = _RELEASE_AUTHORITY.api_get(repository, f"actions/runs/{run['id']}/artifacts?per_page=100", token)
    rows = payload.get('artifacts')
    if not isinstance(rows, list) or payload.get('total_count') != len(rows):
        raise ValueError('FAST_TRACK_PACKAGE_INVENTORY_UNPROVEN')
    matches = [r for r in rows if r.get('name') == name]
    if len(matches) > 1 or any(r.get('expired') is not False for r in matches):
        raise ValueError('FAST_TRACK_PACKAGE_EXPIRED_OR_AMBIGUOUS')
    if matches:
        _RELEASE_AUTHORITY._download_run_artifact(repository, run['id'], name, directory, artifact_id=matches[0]['id'])
        verify_package(directory / 'deploy.zip', run['head_sha'], TARGETS[key]['static'])
        return True
    if run['run_attempt'] > 1:
        display = TARGETS[key]['sourceRoot'] if key in {'portal', 'client'} else key.title()
        saw_owner = False
        for attempt in range(1, run['run_attempt']):
            jobs = _RELEASE_AUTHORITY.api_get(repository,
                f"actions/runs/{run['id']}/attempts/{attempt}/jobs?per_page=100", token)
            if jobs.get('total_count') != len(jobs.get('jobs', [])):
                raise ValueError('FAST_TRACK_PACKAGE_PRIOR_ATTEMPT_UNPROVEN')
            owners = [j for j in jobs.get('jobs', []) if j.get('name') == 'Affected App Deploy — ' + display]
            if not owners and _RELEASE_AUTHORITY.fast_track_skipped_matrix(jobs.get('jobs', [])):
                saw_owner = True
                continue
            if not owners and attempt > 1 and saw_owner:
                continue
            if len(owners) != 1:
                raise ValueError('FAST_TRACK_PACKAGE_PRIOR_OWNER_UNPROVEN')
            saw_owner = True
            if owners[0].get('conclusion') == 'skipped' or owners[0].get('status') == 'queued':
                continue
            build = [s for s in owners[0].get('steps', []) if s.get('name') == 'Compile application and produce ZIP']
            steps = [s for s in owners[0].get('steps', []) if s.get('name') == 'Publish or reconcile exact application bytes']
            if (len(build) != 1 or build[0].get('conclusion') not in {'skipped', 'failure'}
                or len(steps) != 1 or steps[0].get('conclusion') != 'skipped'):
                raise ValueError('FAST_TRACK_RETAINED_PACKAGE_MISSING_NO_REBUILD')
    return False


def fast_track_migration_gate(run):
    # Failed-jobs reruns inherit successful siblings from their original attempt.
    # GitHub's latest inventory retains those jobs without executing them again.
    jobs = _RELEASE_AUTHORITY.api_get(os.environ['GITHUB_REPOSITORY'],
        f"actions/runs/{run['id']}/jobs?filter=latest&per_page=100",
        os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN'])
    owners = [j for j in jobs.get('jobs', []) if j.get('name') == 'Reconcile production schema before publication']
    if (jobs.get('total_count') != len(jobs.get('jobs', [])) or len(owners) != 1
        or owners[0].get('conclusion') != 'success' or owners[0].get('status') != 'completed'
        or owners[0].get('run_id') != run['id'] or owners[0].get('head_sha') != run['head_sha']
        or not 1 <= owners[0].get('run_attempt', 0) <= run['run_attempt']):
        raise ValueError('FAST_TRACK_SCHEMA_RELEASE_GATE_UNPROVEN')
    return owners[0]


def fast_track_deploy(key, package_root):
    """Use the canonical one-upload reconciler from the existing fast-track lane."""
    run = _RELEASE_AUTHORITY.assert_fast_track_release_execution(target=key)
    revision = run['head_sha']
    before = os.environ.get('BEFORE', '')
    if not re.fullmatch('[a-f0-9]{40}', before):
        raise ValueError('FAST_TRACK_BASELINE_UNPROVEN')
    changed = subprocess.check_output(['git', 'diff', '--name-only', before, revision], text=True).splitlines()
    scope = _RELEASE_AUTHORITY.fast_track_scope(changed)
    recovering = _RELEASE_AUTHORITY.attested_website_only_recovery_scope(before,revision,changed)
    if recovering:
        scope = _RELEASE_AUTHORITY.website_only_recovery_scope(scope)
    if TARGETS[key]['releaseName'] not in {r['azure_app'] for r in scope['matrix']['include']}:
        raise ValueError('FAST_TRACK_TARGET_OUTSIDE_SCOPE')
    _RELEASE_AUTHORITY.fast_track_source_admission()
    if scope['commerce']:
        fast_track_migration_gate(run)
    package = package_root / 'deploy.zip'
    digest = verify_package(package, revision, TARGETS[key]['static'])
    azure = target_azure(key, package, revision)
    baseline = azure.observed_revision()
    if recovering:
        # Fresh Azure readback, after the source-pinned GitHub diagnosis. Never
        # authorize a second write when any newer deployment is even possible.
        if key!='website' or baseline != _RELEASE_AUTHORITY.WEBSITE_MISSING_UPLOAD_BASELINE:
            raise DeploymentReconciliationRequired('WEBSITE_RECOVERY_LIVE_BASELINE_DRIFT')
        from datetime import datetime, timezone
        started=datetime.fromisoformat(_RELEASE_AUTHORITY.WEBSITE_MISSING_UPLOAD_AFTER)
        previous=read_deployments_bounded(azure,phase='Website historical no-entry preflight')
        for row in previous:
            try:
                received=datetime.fromisoformat(str(row['received_time']).replace('Z','+00:00'))
            except (KeyError,TypeError,ValueError) as exc:
                raise DeploymentReconciliationRequired('WEBSITE_RECOVERY_AZURE_HISTORY_UNPROVEN') from exc
            if received.tzinfo is None or received>=started:
                raise DeploymentReconciliationRequired('WEBSITE_RECOVERY_PRIOR_OPERATION_MAY_HAVE_ENTERED')
        print('WEBSITE_SOURCE_PINNED_NONENTRY_RECONFIRMED: live original revision, no post-intent Azure deployment',flush=True)
    if baseline is None:
        raise DeploymentReconciliationRequired('FAST_TRACK_LIVE_BASELINE_UNAVAILABLE')
    os.environ['PACKAGE_PRODUCER_RUN'] = str(run['id'])
    journal = operation_journal(key, revision, digest, baseline)
    # Raw current provenance alone cannot erase an unknown earlier upload.
    if journal.history_error is not None:
        raise journal.history_error
    result = reconcile(azure, baseline=journal.baseline, journal=journal,
        timeout=PUBLICATION_RECONCILE_TIMEOUT_SECONDS, require_receipt=True)
    verify_fast_track_favicon(key)
    print('FAST_TRACK_CANONICAL_PUBLICATION:' + key + ':' + result)
    return result


def verify_fast_track_public_metadata(key):
    """Same public favicon/sitemap/robots obligations as the original release."""
    verify_fast_track_favicon(key)
    if key not in {'website','protect'}:
        return
    import xml.etree.ElementTree as ET
    origin='https://www.mylegnd.com' if key=='website' else 'https://protect.mylegnd.com'
    def read(path,accept):
        request=urllib.request.Request(origin+path,headers={
            'Accept':accept,
            'Cache-Control':'no-cache',
            'User-Agent':'LegendSitemapReleaseVerification/1.0',
        })
        with urllib.request.urlopen(request,timeout=22) as response:
            if response.status!=200 or response.url!=origin+path:
                raise ValueError('FAST_TRACK_COMPLETION_PUBLIC_METADATA_UNPROVEN')
            return response.headers.get('Content-Type','').lower(),response.read(1048577)
    mime,raw=read('/sitemap.xml','application/xml,text/xml')
    if len(raw)>1048576 or not any(t in mime for t in ('application/xml','text/xml')):
        raise ValueError('FAST_TRACK_COMPLETION_SITEMAP_UNPROVEN')
    root=ET.fromstring(raw);ns='{http://www.sitemaps.org/schemas/sitemap/0.9}'
    locations=[e.text for e in root.findall(ns+'url/'+ns+'loc')]
    if root.tag!=ns+'urlset' or not locations or len(locations)!=len(set(locations)) or any(not x or not x.startswith(origin+'/') for x in locations):
        raise ValueError('FAST_TRACK_COMPLETION_SITEMAP_UNPROVEN')
    mime,raw=read('/robots.txt','text/plain')
    if len(raw)>1048576 or 'text/plain' not in mime or 'Sitemap: '+origin+'/sitemap.xml' not in raw.decode().splitlines():
        raise ValueError('FAST_TRACK_COMPLETION_ROBOTS_UNPROVEN')


class CompletionObservation:
    """Read-only provider facade; an accidental submit remains impossible here."""
    def __init__(self, azure, journal):
        self.azure=azure;self.journal=journal;self.revision=azure.revision
        self.observed_deployment_id=None;self.identity_conflict=False
    def observed_revision(self):
        return self.azure.observed_revision()
    def deployments(self):
        if self.identity_conflict:
            raise DeploymentDrift('Original provider identity previously became ambiguous')
        rows=self.azure.deployments()
        new=[r for r in rows if r['id'] not in self.journal.intent['baselineDeploymentIds']]
        self.journal.provider=None
        if len(new)>1:
            self.identity_conflict=True
            raise DeploymentDrift('Multiple original provider operations require reconciliation')
        observed_id=new[0]['id'] if new else None
        if self.observed_deployment_id is not None and observed_id!=self.observed_deployment_id:
            self.identity_conflict=True
            raise DeploymentDrift('Original provider operation changed or disappeared during observation')
        if observed_id is not None:
            self.observed_deployment_id=observed_id
        if len(new)==1 and new[0]['status']==4:
            row=new[0];a=_RELEASE_AUTHORITY
            received=row.get('received_time')
            if not a.completion_provider_in_original_publication(self.journal.material,received):
                raise DeploymentDrift('Original provider operation does not correlate to retained intent')
            self.journal.provider=dict(deploymentId=row['id'],receivedTime=received,status=4,runtimeRevision=self.revision)
        return rows
    def submit(self, *args, **kwargs):
        raise DeploymentSubmissionNotEntered('Completion observer has no submission authority')
    def prepare_submission(self):
        raise DeploymentSubmissionNotEntered('Completion observer has no submission authority')


def complete_fast_track(original_run, revision, package_root, *, validate_only=False):
    """Finish genuine original receipts; never rebuild, submit or restart apps."""
    authority=_RELEASE_AUTHORITY
    observer=authority.assert_fast_track_completion_execution(original_run,revision)
    repository=os.environ['GITHUB_REPOSITORY'];token=os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
    original=authority.fast_track_completion_original(repository,original_run,revision,token)
    if subprocess.run(['git','merge-base','--is-ancestor',revision,observer['toolRevision']],capture_output=True).returncode:
        raise ValueError('FAST_TRACK_COMPLETION_ORIGINAL_NOT_ANCESTOR')
    materials={key:authority.fast_track_completion_material(repository,original,key,token) for key in original['targets']}
    # Validate all original bytes before even the first completion record.
    for key,material in materials.items():
        artifact=material['packageArtifact'];directory=package_root/key
        if validate_only:
            authority._download_run_artifact(repository,original_run,artifact['name'],directory,
                artifact_id=artifact['id'],expected_digest=artifact['digest'])
        if any(not (directory/name).is_file() or (directory/name).is_symlink() for name in ('deploy.zip','SHA256SUMS')):
            raise ValueError('FAST_TRACK_COMPLETION_PREPARED_PACKAGE_MISSING')
        if verify_package(directory/'deploy.zip',revision,TARGETS[key]['static'])!=material['intent']['packageDigest']:
            raise ValueError('FAST_TRACK_COMPLETION_ORIGINAL_PACKAGE_CHANGED')
    if validate_only:
        return {'state':'COMPLETION_PACKAGES_VERIFIED','targets':original['targets']}
    spec=importlib.util.spec_from_file_location('completion_journal',Path(__file__).with_name('release-operation-evidence.py'))
    owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
    results={}
    for key,material in materials.items():
        journal=owner.CompletionJournal(original,material,observer,authority=authority)
        azure=CompletionObservation(target_azure(key,package_root/key/'deploy.zip',revision),journal)
        # Public checks precede the success record as well as follow reconciliation.
        verify_fast_track_public_metadata(key)
        results[key]=reconcile(azure,baseline=journal.baseline,reconcile_only=True,journal=journal,
            timeout=FINALIZE_RECONCILE_TIMEOUT_SECONDS,require_receipt=True)
        verify_fast_track_public_metadata(key)
    completed=authority.fast_track_completed_operation_receipts(repository,original['run'],token,require_completed_observer=False)
    if not completed:
        raise ValueError('FAST_TRACK_COMPLETION_RECEIPTS_STILL_MISSING')
    if authority.assert_fast_track_completion_execution(original_run,revision)!=observer:
        raise ValueError('FAST_TRACK_COMPLETION_OBSERVER_CHANGED')
    summary=dict(schemaVersion=1,phase='success',originalRun=original_run,applicationRevision=revision,
        producingRun=observer['runId'],producingAttempt=observer['runAttempt'],observer=observer,targets=completed)
    owner.publish_record(authority.fast_track_completion_summary_name(original_run,revision),summary)
    return {'state':'ORIGINAL_COMPLETIONS_VERIFIED','targets':results,'originalRun':original_run}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--target', choices=TARGETS)
    mode.add_argument('--targets-json')
    mode.add_argument('--fast-track-target', choices=TARGETS)
    mode.add_argument('--observe-azure-deployment')
    parser.add_argument('--observation-revision')
    parser.add_argument('--deployment-id')
    mode.add_argument('--complete-fast-track', type=int)
    parser.add_argument('--completion-revision')
    parser.add_argument('--validate-completion-only', action='store_true')
    parser.add_argument('--validate-observation-only', action='store_true')
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
    if args.complete_fast_track is not None:
        if (args.observe_azure_deployment or args.fast_track_target or args.target or args.targets_json
            or args.prepare_only or args.finalize_only or args.disposition_only or args.transaction_plan
            or args.reconcile_only or args.publish_prepared_parallel or args.observation_revision
            or args.deployment_id or args.validate_observation_only or args.baselines_json):
            raise SystemExit('Completion mode cannot be combined with other release modes')
        print(json.dumps(complete_fast_track(args.complete_fast_track,args.completion_revision,
            Path(args.package_root),validate_only=args.validate_completion_only),sort_keys=True))
        return
    if args.completion_revision or args.validate_completion_only:
        raise SystemExit('Completion inputs require completion-only mode')
    if args.observe_azure_deployment is not None:
        try:
            result = observe_azure_deployment(args.observe_azure_deployment,
                args.observation_revision, args.deployment_id, validate_only=args.validate_observation_only)
        except (ValueError, TypeError):
            raise SystemExit('AZURE_DEPLOYMENT_OBSERVATION:INVALID_IDENTITY') from None
        print('AZURE_DEPLOYMENT_OBSERVATION:' + json.dumps(result, sort_keys=True), flush=True)
        if result['readStatus'] not in {'OBSERVED', 'INPUTS_VALIDATED'}:
            raise SystemExit('AZURE_DEPLOYMENT_OBSERVATION:READ_INCOMPLETE')
        return
    if args.observation_revision or args.deployment_id or args.validate_observation_only:
        raise SystemExit('Observation inputs require the read-only observation mode')
    if args.fast_track_target:
        fast_track_deploy(args.fast_track_target, Path(args.package_root))
        return
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
