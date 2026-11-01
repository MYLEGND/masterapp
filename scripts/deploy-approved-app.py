#!/usr/bin/env python3
"""One immutable upload, followed by read-only deployment/runtime reconciliation.

A timeout is not a failed Azure operation. Unknown/active operations are never
replayed. Terminal provider failures require bounded repair through the canonical
release lifecycle; no alternate upload path can bypass durable operation intent.
"""
import argparse
import concurrent.futures
import contextvars
import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import signal
import shutil
import stat
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


class DeploymentReadCleanupFailure(DeploymentReconciliationRequired):
    """The owned CLI group was not proven reaped; never start another read."""


DEPLOYMENT_READ_WALL_SECONDS = 90
DEPLOYMENT_READ_ATTEMPT_SECONDS = 40
DEPLOYMENT_READ_BACKOFF_SECONDS = 10
_DEPLOYMENT_READ_CAP = contextvars.ContextVar('deployment_read_cap', default=DEPLOYMENT_READ_ATTEMPT_SECONDS)


def read_deployments_bounded(azure, *, attempts=2, interval=10, sleep=None, phase='before any upload', deadline=None, clock=time.monotonic):
    """At most two reads in one 90s wall budget, clipped to the enclosing phase.

    The allowance includes CLI startup and process cleanup, not a provider SLA.
    Existing callers may request fewer reads/shorter backoff, never a larger cap.
    """
    if type(attempts) is not int or attempts < 1 or interval < 0:
        raise ValueError('Deployment status retry bounds invalid')
    attempts=min(attempts,2);backoff=min(interval,DEPLOYMENT_READ_BACKOFF_SECONDS)
    sleeper=sleep or time.sleep
    end=min(clock()+DEPLOYMENT_READ_WALL_SECONDS, deadline if deadline is not None else float('inf'))
    owner_deadline=getattr(azure,'deadline',None)
    if isinstance(owner_deadline,(int,float)):end=min(end,owner_deadline)
    last=None
    for attempt in range(1,attempts+1):
        cap=min(DEPLOYMENT_READ_ATTEMPT_SECONDS,end-clock())
        if cap<=1:break
        token=_DEPLOYMENT_READ_CAP.set(cap);started=clock();outcome='unavailable'
        try:
            rows=azure.deployments()
            if clock()>end:raise subprocess.TimeoutExpired('deployment-list',cap)
            outcome='read-complete'
            return rows
        except (DeploymentDrift,DeploymentReadCleanupFailure):
            outcome='identity-conflict';raise
        except (RuntimeError,ValueError,OSError,subprocess.TimeoutExpired) as exc:
            last=exc;outcome='timeout' if isinstance(exc,subprocess.TimeoutExpired) else 'unavailable'
        finally:
            _DEPLOYMENT_READ_CAP.reset(token)
            print(json.dumps(dict(deploymentReadAttempt=attempt,budgetSeconds=round(cap,3),
                elapsedSeconds=round(max(0,clock()-started),3),outcome=outcome),sort_keys=True),flush=True)
        if attempt>=attempts or end-clock()<=backoff+1:break
        sleeper(backoff)
    raise DeploymentStatusUnavailable(
        f'Azure deployment status unavailable within the bounded read wall budget deadline ({phase}). '
        'No deployment or rollback write was replayed; reconcile the exact revision.') from last


def deployment_read_process(command, timeout):
    """Own and reap the CLI process group; reserve one second inside its cap."""
    if timeout<=1:raise subprocess.TimeoutExpired('deployment-list',timeout)
    process=subprocess.Popen(command,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,start_new_session=True)
    try:
        stdout,stderr=process.communicate(timeout=timeout-1)
        return subprocess.CompletedProcess(command,process.returncode,stdout,stderr)
    except BaseException:
        # Only the new session we created is addressed. No process discovery or
        # credential/transport fallback, and no surviving child may be retried.
        kill_error=None
        try:os.killpg(process.pid,signal.SIGKILL)
        except ProcessLookupError:pass
        except OSError as exc:kill_error=exc
        try:process.communicate(timeout=1)
        except BaseException as cleanup:
            raise DeploymentReadCleanupFailure('Owned deployment read process cleanup unproven') from cleanup
        if kill_error is not None:
            raise DeploymentReadCleanupFailure('Owned deployment read group termination unproven') from kill_error
        raise


def deployment_read_error_metadata(result):
    """Allowlisted CLI-reported metadata, never a diagnosis or copied error text.

    Accept one anchored ERROR shape within at most 8192 characters / 32 lines.
    A nonzero exit is not a timeout. HTTP status and provider code are separate
    observations; neither is inferred from the other.
    """
    code = result.returncode
    stderr = result.stderr
    metadata = dict(schemaVersion=1, source='azure-cli-reported', category='unknown',
                    returnCode=code if type(code) is int and -255 <= code <= 255 else None,
                    providerCode=None, httpStatus=None,
                    inputTooLarge=type(stderr) is str and len(stderr) > 8192,
                    unknownReason='UNRECOGNIZED_FORMAT', stderrLineCount=None)
    if metadata['returnCode'] is None:
        metadata['unknownReason']='INVALID_RETURN_CODE'
        return metadata
    if type(stderr) is not str:
        metadata['unknownReason']='INVALID_TYPE'
        return metadata
    if metadata['inputTooLarge']:
        metadata['unknownReason']='OVERSIZED'
        return metadata
    provider_codes = {
        'AuthenticationFailed': 'authentication', 'InvalidAuthenticationToken': 'authentication',
        'ExpiredAuthenticationToken': 'authentication', 'AuthorizationFailed': 'authorization',
        'Forbidden': 'authorization', 'ResourceNotFound': 'not_found',
        'TooManyRequests': 'throttling', 'InternalServerError': 'service_error',
        'ServiceUnavailable': 'service_error', 'BadRequest': 'invalid_request',
    }
    statuses = {
        'Bad Request': (400, 'invalid_request'), 'Unauthorized': (401, 'authentication'),
        'Forbidden': (403, 'authorization'), 'Not Found': (404, 'not_found'),
        'Too Many Requests': (429, 'throttling'), 'Internal Server Error': (500, 'service_error'),
        'Bad Gateway': (502, 'service_error'), 'Service Unavailable': (503, 'service_error'),
        'Gateway Timeout': (504, 'service_error'),
    }
    # No partial-input classification: bounds and ambiguity apply to every line.
    text = stderr.replace('\r\n', '\n').removesuffix('\n')
    lines = text.split('\n') if text else []
    metadata['stderrLineCount'] = min(33, len(lines))
    if len(lines) > 32:
        metadata['unknownReason'] = 'LINE_LIMIT'
        return metadata
    if not text:
        metadata['unknownReason'] = 'EMPTY'
        return metadata
    # CRLF is supported; stray CR, ANSI and alternate separators are ambiguous.
    if any(any(ord(c) < 32 and c != '\t' or ord(c) == 127 or
                   c in '\x85\u2028\u2029' for c in line) for line in lines):
        metadata['unknownReason'] = 'UNRECOGNIZED_FORMAT'
        return metadata
    markers = re.compile(r'ERROR\s*:|Code\s*:|HTTP/|Operation returned an invalid status|AADSTS|'
                         r'(?<![A-Za-z0-9_])(?:HTTP status|Status|StatusCode)\s*:', re.I)
    error_lines = [i for i, line in enumerate(lines) if line.startswith('ERROR: ')]
    if len(error_lines) != 1:
        if sum(len(re.findall(r'ERROR\s*:', line, re.I)) for line in lines) > 1:
            metadata['unknownReason'] = 'CONFLICTING_MARKERS'
        return metadata
    index = error_lines[0]
    line = lines[index]
    # Only the selected anchored shape may consume its own markers. Detail lines
    # are never evidence for a positive and must not introduce another marker.
    if any(markers.search(detail) for i, detail in enumerate(lines) if i != index):
        metadata['unknownReason'] = 'CONFLICTING_MARKERS'
        return metadata
    match = re.fullmatch(r'ERROR: \(([A-Za-z]+)\)(?: [^\r\n]*)?', line)
    consumed = 'ERROR: '
    update = {}
    if match:
        if match[1] not in provider_codes:
            metadata['unknownReason'] = 'UNKNOWN_PROVIDER_CODE'
            return metadata
        update = dict(providerCode=match[1], category=provider_codes[match[1]])
    else:
        match = re.fullmatch(r"ERROR: Operation returned an invalid status '([A-Za-z ]+)'", line)
        if match and match[1] in statuses:
            status, category = statuses[match[1]]
            update = dict(httpStatus=status, category=category)
            consumed = line
        else:
            # Explicit finite CLI shapes: option names and tails are never emitted.
            argument_shapes = (
                (r'ERROR: unrecognized arguments: .+', 'UNRECOGNIZED_ARGUMENTS'),
                (r'ERROR: argument --?[A-Za-z][A-Za-z0-9-]{0,63}: expected one argument', 'EXPECTED_ONE_ARGUMENT'),
                (r'ERROR: the following arguments are required: .+', 'REQUIRED_ARGUMENTS'),
            )
            for pattern, kind in argument_shapes:
                if re.fullmatch(pattern, line):
                    update = dict(category='cli_argument', cliArgumentKind=kind)
                    break
            # Literal Microsoft Entra codes only; CLI-reported, not verified causes.
            # https://learn.microsoft.com/en-us/entra/identity-platform/reference-error-codes
            for auth_code in ('AADSTS700016', 'AADSTS7000215', 'AADSTS7000222'):
                prefix = 'ERROR: ' + auth_code + ':'
                if line.startswith(prefix) and (line == prefix or line[len(prefix)] == ' '):
                    update = dict(category='authentication_reported', authenticationCode=auth_code)
                    consumed = prefix
                    break
    if markers.search(line[len(consumed):]):
        metadata['unknownReason'] = 'CONFLICTING_MARKERS'
        return metadata
    if update:
        metadata.update(update, unknownReason=None)
    return metadata


_COMPLETION_DIAGNOSTICS = contextvars.ContextVar('completion_diagnostics', default=None)


def _diagnostic_executable():
    # Resolution is a checkpoint snapshot, not proof of later executed bytes.
    path=os.environ.get('PATH')
    if type(path) is not str or len(path)>8192 or len(path.split(os.pathsep))>64:
        return None
    resolved=shutil.which('az')
    if resolved and os.path.realpath(resolved)=='/opt/az/bin/az':
        return 'OPT_AZ'
    return None


def _diagnostic_package_version(executable):
    # Read installed distribution metadata only. Never run/import CLI code,
    # inspect CLI configuration/token stores, or start another subprocess.
    if executable!='OPT_AZ':
        return None
    root=Path('/opt/az/lib')
    if root.is_symlink() or root.resolve()!=root:return None
    versions=[]
    with os.scandir(root) as entries:
        for count,entry in enumerate(entries):
            if count>=32:return None
            if not re.fullmatch(r'python3\.[0-9]{1,2}',entry.name):continue
            if not entry.is_dir(follow_symlinks=False):return None
            packages=Path(entry.path)/'site-packages'
            if packages.is_symlink() or not packages.is_dir():return None
            with os.scandir(packages) as distributions:
                for index,distribution in enumerate(distributions):
                    if index>=2048:return None
                    if not distribution.name.startswith('azure_cli-'):continue
                    match=re.fullmatch(r'azure_cli-([0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3})\.dist-info',distribution.name)
                    if not match or not distribution.is_dir(follow_symlinks=False):return None
                    metadata=Path(distribution.path)/'METADATA'
                    fd=os.open(metadata,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK)
                    with os.fdopen(fd,'rb') as stream:
                        info=os.fstat(stream.fileno())
                        if not stat.S_ISREG(info.st_mode) or info.st_size>8192:return None
                        raw=stream.read(8193)
                    if len(raw)>8192:return None
                    lines=raw.decode('utf-8').splitlines()
                    if [v for v in lines if v.startswith('Name: ')]!=['Name: azure-cli']:return None
                    if [v for v in lines if v.startswith('Version: ')]!=['Version: '+match[1]]:return None
                    versions.append(match[1])
    return versions[0] if len(versions)==1 else None


def _diagnostic_snapshot():
    # These operational strings stay in invocation-local memory only. Neither
    # their values nor hashes are emitted; no credential variables are read.
    values={}
    for key,limit in (('PATH',8192),('AZURE_CONFIG_DIR',4096),('AZURE_CLOUD_NAME',128)):
        value=os.environ.get(key)
        values[key]=(value is not None, value if type(value) is str and len(value)<=limit else None)
    cwd=os.getcwd()
    values['cwd']=(True,cwd if len(cwd)<=4096 else None)
    return values


def _start_completion_diagnostics():
    try:
        executable=_diagnostic_executable()
        try:version=_diagnostic_package_version(executable)
        except Exception:version=None
        image=os.environ.get('ImageVersion')
        image=image if type(image) is str and re.fullmatch(r'[0-9]{8}\.[0-9]{1,4}\.[0-9]{1,3}',image) else None
        return dict(started=time.monotonic(),baseline=_diagnostic_snapshot(),executable=executable,
                    packageVersion=version,imageVersion=image,count=0)
    except Exception:
        return None


def completion_diagnostic(checkpoint, target=None, command=None):
    """Best-effort bounded observations; never release or provider authority."""
    try:
        state=_COMPLETION_DIAGNOSTICS.get()
        if state is None or state['count']>=24:return
        if checkpoint not in {'completion-entry','target-entry','deployment-read'}:return
        state['count']+=1
        snapshot=_diagnostic_snapshot()
        equality={key:(value[1]==state['baseline'][key][1]
                       if value[1] is not None and state['baseline'][key][1] is not None else None)
                  for key,value in snapshot.items()}
        executable=_diagnostic_executable()
        report=dict(schemaVersion=1,checkpoint=checkpoint,sequence=state['count'],
            elapsedSinceEntrySeconds=min(86400,max(0,round(time.monotonic()-state['started'],3))),
            imageVersion=state['imageVersion'],installedCliPackageVersion=state['packageVersion'],
            cliVersionSource='installed-package-metadata',executable=executable,
            executableEqualsEntry=(executable==state['executable'] if executable and state['executable'] else None),
            contextPresent={key:value[0] for key,value in snapshot.items()},contextEqualsEntry=equality,
            contextIsAuthorizationProof=False)
        if target in TARGETS and re.fullmatch('[a-z]{1,16}',target):
            app=TARGETS[target]['releaseName'];group=_RELEASE_AUTHORITY.RELEASE_RESOURCE_GROUP
            if re.fullmatch('[a-z0-9-]{1,64}',app) and re.fullmatch('[a-z0-9-]{1,64}',group):
                report.update(target=target,app=app,resourceGroup=group,
                    commandKind='AZ_WEBAPP_LOG_DEPLOYMENT_LIST',explicitSubscriptionArgument=False)
                if command is not None:
                    report['commandMatchesCanonical']=(command==['az','webapp','log','deployment','list',
                        '-g',group,'-n',app,'--only-show-errors','-o','json'])
        print('LEGEND_COMPLETION_CONTEXT:'+json.dumps(report,sort_keys=True),flush=True)
    except Exception:
        pass  # No diagnostic error may replace the original operation result.


class Azure:
    def __init__(self, app, package, url, revision, static=False):
        self.app, self.package, self.url, self.revision = app, package, url, revision
        self.static = static
        self._submission_correlation_supported = False
        self._submission_kind = None
        self.submission_reference = None

    def deployments(self):
        # Uses the existing OIDC session; no publishing credentials or new authority.
        command=['az', 'webapp', 'log', 'deployment', 'list', '-g', _RELEASE_AUTHORITY.RELEASE_RESOURCE_GROUP, '-n', self.app,
                 '--only-show-errors', '-o', 'json']
        cap=_DEPLOYMENT_READ_CAP.get()
        if _COMPLETION_DIAGNOSTICS.get() is not None:
            diagnostic_started=time.monotonic()
            try:
                completion_diagnostic('deployment-read',next((key for key,row in TARGETS.items()
                    if row['releaseName']==self.app),None),command)
            except Exception:
                pass
            # Diagnostics consume, never extend, the caller's existing allowance.
            cap-=max(0,time.monotonic()-diagnostic_started)
        result = deployment_read_process(command, timeout=cap)
        if result.returncode:
            try:
                print('LEGEND_DEPLOYMENT_READ_ERROR:' + json.dumps(
                    deployment_read_error_metadata(result), sort_keys=True), flush=True)
            except Exception:
                pass  # Diagnostic failure must not replace the original fail-closed result.
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
        tracked = self.app == "masterapp-portal" or getattr(self, "authorized_replacement", False) or (
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


def reconcile(azure, *, clock=time.monotonic, sleep=time.sleep, timeout=PUBLICATION_RECONCILE_TIMEOUT_SECONDS, interval=15, max_status_failures=3, baseline=None, reconcile_only=False, journal=None, require_receipt=True, before_intent_read_failure=None):
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
        try:
            rows = read_deployments_bounded(
                azure,
                attempts=max_status_failures,
                interval=interval,
                sleep=sleep,
                phase='after immutable upload' if submitted else 'before any upload',
                deadline=started+timeout, clock=clock,
            )
        except DeploymentStatusUnavailable as failure:
            if not submitted and journal is not None and journal.intent is None and before_intent_read_failure is not None:
                try:
                    before_intent_read_failure(failure)
                except Exception:
                    # Ambiguous evidence publication cannot authorize a retry or
                    # replace the original provider-read failure.
                    print('RECOVERY_TARGET_READ_HOLD_PUBLICATION_UNPROVEN', flush=True)
            raise
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


def operation_journal(key, revision, digest, baseline, *, candidate_revision=None, component_source=None, supersession=None, before_new_intent=None):
    if os.environ.get('GITHUB_ACTIONS') != 'true':
        return None
    if baseline is None:
        raise ValueError('Preserved transaction baseline required')
    spec = importlib.util.spec_from_file_location('release_operation_evidence', Path(__file__).with_name('release-operation-evidence.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.OperationJournal(target=key, application_revision=revision, package_digest=digest, baseline=baseline, authority=_RELEASE_AUTHORITY,
                                   candidate_revision=candidate_revision, component_source=component_source, supersession=supersession,
                                   before_new_intent=before_new_intent)


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


def prepare_protect_replacement(revision, baseline, azure):
    """Validate retained rollback bytes for the explicitly superseded baseline."""
    proof = _RELEASE_AUTHORITY.fast_track_protect_supersession(revision)
    if proof is None:
        return None
    if baseline != proof['originalRevision']:
        raise DeploymentDrift('FAST_TRACK_SUPERSESSION_LIVE_BASELINE_CHANGED')
    # This is a fresh no-active observation, not proof about the old missing reply.
    rows = read_deployments_bounded(azure, phase='authorized replacement preflight')
    if any(row['status'] in (0, 1, 2) for row in rows):
        raise DeploymentReconciliationRequired('FAST_TRACK_SUPERSESSION_PROVIDER_ACTIVE')
    import tempfile
    with tempfile.TemporaryDirectory(prefix='protect-retained-rollback-') as directory:
        root = Path(directory)
        _RELEASE_AUTHORITY._download_run_artifact(os.environ['GITHUB_REPOSITORY'],
            proof['originalRun'], 'fast-track-package-' + baseline + '-protect', root,
            artifact_id=proof['rollbackArtifactId'], expected_digest=proof['rollbackArtifactDigest'])
        rollback_digest = verify_package(root / 'deploy.zip', baseline, False)
    print('FAST_TRACK_SUPERSESSION_ROLLBACK_VERIFIED:' + rollback_digest)
    provider = azure
    while isinstance(provider, RecoveryDeadlineAzure):
        provider = provider.azure
    provider.authorized_replacement = True
    return proof


def fast_track_crm_portal_gate(key, run, before, changed, scope, *, clock=time.monotonic, sleep=time.sleep):
    """First CRM writers wait for the retention-safe Portal in this release."""
    migration = 'Infrastructure/Migrations/20261030070000_CrmRelationshipHistory.cs'
    if key not in {'client', 'protect', 'parfait'} or migration not in changed:
        return
    if subprocess.check_output(['git', 'ls-tree', '--name-only', before, '--', migration], text=True).strip():
        return
    if TARGETS['portal']['releaseName'] not in {r['azure_app'] for r in scope['matrix']['include']}:
        raise DeploymentReconciliationRequired('FAST_TRACK_CRM_PORTAL_OUTSIDE_SCOPE')
    repository = os.environ['GITHUB_REPOSITORY']
    token = os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
    deadline = clock() + 900
    while True:
        if clock() >= deadline:
            raise DeploymentReconciliationRequired('FAST_TRACK_CRM_PORTAL_WAIT_EXHAUSTED')
        with _RELEASE_AUTHORITY.evidence_lookup_budget(deadline, clock=clock):
            payload = _RELEASE_AUTHORITY.api_get(repository,
                f"actions/runs/{run['id']}/jobs?filter=latest&per_page=100", token)
        if clock() >= deadline:
            raise DeploymentReconciliationRequired('FAST_TRACK_CRM_PORTAL_WAIT_EXHAUSTED')
        jobs = payload.get('jobs', [])
        owners = [j for j in jobs if j.get('name') == 'Affected App Deploy — AgentPortal']
        if (payload.get('total_count') != len(jobs) or len(owners) != 1
            or owners[0].get('run_id') != run['id'] or owners[0].get('head_sha') != run['head_sha']
            or type(owners[0].get('run_attempt')) is not int
            or not 1 <= owners[0]['run_attempt'] <= run['run_attempt']):
            raise DeploymentReconciliationRequired('FAST_TRACK_CRM_PORTAL_OWNER_UNPROVEN')
        owner = owners[0]
        if owner.get('status') == 'completed':
            publication = [step for step in owner.get('steps', []) if step.get('name') == 'Publish or reconcile exact application bytes']
            if (owner.get('conclusion') != 'success' or len(publication) != 1
                or publication[0].get('status') != 'completed' or publication[0].get('conclusion') != 'success'):
                raise DeploymentReconciliationRequired('FAST_TRACK_CRM_PORTAL_NOT_SUCCESSFUL')
            # Portal publication requires its durable success receipt. Confirm its
            # current runtime and provider are still exact and idle before intent.
            portal = target_azure('portal', None, run['head_sha'])
            rows = read_deployments_bounded(portal, phase='CRM Portal-first barrier',
                deadline=deadline, clock=clock, sleep=sleep)
            if any(r['status'] in (0, 1, 2) for r in rows) or portal.observed_revision() != run['head_sha']:
                raise DeploymentReconciliationRequired('FAST_TRACK_CRM_PORTAL_LIVE_UNPROVEN')
            with _RELEASE_AUTHORITY.evidence_lookup_budget(deadline, clock=clock):
                fresh = _RELEASE_AUTHORITY.assert_fast_track_release_execution(target=key)
            if (clock() >= deadline or any(fresh.get(k) != run.get(k) for k in ('id', 'head_sha', 'run_attempt'))):
                raise DeploymentReconciliationRequired('FAST_TRACK_CRM_PORTAL_AUTHORITY_CHANGED')
            print('FAST_TRACK_CRM_PORTAL_RETENTION_READY:' + run['head_sha'], flush=True)
            return
        if owner.get('status') not in {'queued', 'in_progress', 'waiting', 'pending'} or clock() >= deadline:
            raise DeploymentReconciliationRequired('FAST_TRACK_CRM_PORTAL_WAIT_EXHAUSTED')
        sleep(min(30, max(0, deadline - clock())))


def fast_track_deploy(key, package_root):
    """Use the canonical one-upload reconciler from the existing fast-track lane."""
    run = _RELEASE_AUTHORITY.assert_fast_track_release_execution(target=key)
    revision = run['head_sha']
    forward = _RELEASE_AUTHORITY.completed_resource_execution(key,revision)
    before = os.environ.get('BEFORE', '')
    if not re.fullmatch('[a-f0-9]{40}', before):
        raise ValueError('FAST_TRACK_BASELINE_UNPROVEN')
    changed = subprocess.check_output(['git', 'diff', '--name-only', before, revision], text=True).splitlines()
    scope = _RELEASE_AUTHORITY.fast_track_scope(changed)
    if forward is not None:
        if before != forward['proof']['reference']['sourceRevision']:
            raise ValueError('RESOURCE_BASELINE_DEPLOY_INPUT_CHANGED')
        scope = forward['selection']
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
    fast_track_crm_portal_gate(key, run, before, changed, scope)
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
    supersession = _RELEASE_AUTHORITY.fast_track_protect_supersession(revision) if key == 'protect' else None
    journal_args = {'supersession': supersession} if supersession is not None else {}
    journal = operation_journal(key, revision, digest, baseline, **journal_args)
    if forward is not None and journal.intent is None:
        proof = forward['proof']
        if baseline != proof['reference']['sourceRevision']:
            raise DeploymentReconciliationRequired('RESOURCE_BASELINE_LIVE_REVISION_CHANGED')
        provider_rows = read_deployments_bounded(azure,phase='completed resource baseline')
        initial = recovery_current_provider_state(provider_rows,baseline)
        accepted_provider = [row for row in provider_rows if row['id'] == proof['record']['deploymentIds'][0]]
        if len(accepted_provider) != 1 or accepted_provider[0].get('status') != 4 or accepted_provider[0].get('complete') is not True:
            raise DeploymentReconciliationRequired('RESOURCE_BASELINE_PROVIDER_SUCCESS_MISSING')
        expected_ids=sorted(set(proof['record']['baselineDeploymentIds']+proof['record']['deploymentIds']))
        if initial['deploymentIds']!=expected_ids:
            raise DeploymentReconciliationRequired('RESOURCE_BASELINE_UNRECONCILED_PROVIDER_EFFECT')
        rollback = prepare_completed_resource_rollback(key,initial,package_root)
        if rollback['packageDigest'] != proof['record']['packageDigest']:
            raise DeploymentReconciliationRequired('RESOURCE_BASELINE_ROLLBACK_BYTES_CHANGED')
        def before_new_intent(ids):
            current = _RELEASE_AUTHORITY.completed_resource_execution(key,revision)
            if current != forward:
                raise DeploymentReconciliationRequired('RESOURCE_BASELINE_PROOF_DRIFT')
            closing = recovery_current_provider_state(read_deployments_bounded(azure,phase='completed resource closing'),azure.observed_revision())
            if closing != initial or sorted(ids) != initial['deploymentIds']:
                raise DeploymentReconciliationRequired('RESOURCE_BASELINE_PROVIDER_DRIFT')
            prepare_completed_resource_rollback(key,closing,package_root,expected=rollback)
        journal.forward_before_new_intent = before_new_intent
    elif forward is not None and journal.baseline != forward['proof']['reference']['sourceRevision']:
        raise DeploymentReconciliationRequired('RESOURCE_BASELINE_RETAINED_INTENT_CHANGED')
    if supersession is not None and journal.intent is None:
        prepare_protect_replacement(revision, baseline, azure)
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
    # Cache immutable authenticated bytes only for this invocation. Mutable proof
    # snapshots are separately closed before any receipt can be published.
    diagnostic_token=_COMPLETION_DIAGNOSTICS.set(_start_completion_diagnostics() if not validate_only else None)
    try:
        return _complete_fast_track_with_metrics(original_run,revision,package_root,validate_only=validate_only)
    finally:
        _COMPLETION_DIAGNOSTICS.reset(diagnostic_token)


def _complete_fast_track_with_metrics(original_run, revision, package_root, *, validate_only=False):
    completion_diagnostic('completion-entry')
    with _RELEASE_AUTHORITY.recovery_material_reuse():
        outcome='failure'
        try:
            result=_complete_fast_track(original_run, revision, package_root, validate_only=validate_only)
            outcome='success'
            return result
        finally:
            try:
                _RELEASE_AUTHORITY.report_recovery_efficiency(outcome)
            except BaseException:
                pass  # Measurements must not affect completion semantics.


def _complete_fast_track(original_run, revision, package_root, *, validate_only=False):
    authority=_RELEASE_AUTHORITY
    print('FAST_TRACK_COMPLETION_STAGE:original-authority:start', flush=True)
    repository=os.environ['GITHUB_REPOSITORY'];token=os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
    with authority.recovery_admission_proof():
        observer=authority.assert_fast_track_completion_execution(original_run,revision)
        original=authority.fast_track_completion_original(repository,original_run,revision,token)
        if subprocess.run(['git','merge-base','--is-ancestor',revision,observer['toolRevision']],capture_output=True).returncode:
            raise ValueError('FAST_TRACK_COMPLETION_ORIGINAL_NOT_ANCESTOR')
        materials={key:authority.fast_track_completion_material(repository,original,key,token) for key in original['targets']}
    authority.assert_fast_track_completion_live_execution(original_run,revision,observer)
    print('FAST_TRACK_COMPLETION_STAGE:original-authority:verified', flush=True)
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
        print('FAST_TRACK_COMPLETION_STAGE:target:'+key+':start', flush=True)
        completion_diagnostic('target-entry',key)
        journal=owner.CompletionJournal(original,material,observer,authority=authority)
        azure=CompletionObservation(target_azure(key,package_root/key/'deploy.zip',revision),journal)
        # Public checks precede the success record as well as follow reconciliation.
        verify_fast_track_public_metadata(key)
        results[key]=reconcile(azure,baseline=journal.baseline,reconcile_only=True,journal=journal,
            timeout=FINALIZE_RECONCILE_TIMEOUT_SECONDS,require_receipt=True)
        verify_fast_track_public_metadata(key)
        print('FAST_TRACK_COMPLETION_STAGE:target:'+key+':verified', flush=True)
    print('FAST_TRACK_COMPLETION_STAGE:summary-proof:start', flush=True)
    with authority.recovery_admission_proof():
        if authority.assert_fast_track_completion_execution(original_run,revision)!=observer:
            raise ValueError('FAST_TRACK_COMPLETION_OBSERVER_CHANGED')
        completed=authority.fast_track_completed_operation_receipts(repository,original['run'],token,require_completed_observer=False)
    print('FAST_TRACK_COMPLETION_STAGE:summary-proof:verified', flush=True)
    if not completed:
        raise ValueError('FAST_TRACK_COMPLETION_RECEIPTS_STILL_MISSING')
    summary=dict(schemaVersion=1,phase='success',originalRun=original_run,applicationRevision=revision,
        producingRun=observer['runId'],producingAttempt=observer['runAttempt'],observer=observer,targets=completed)
    print('FAST_TRACK_COMPLETION_STAGE:summary-publish:start', flush=True)
    authority.assert_fast_track_completion_live_execution(original_run,revision,observer)
    owner.publish_record(authority.fast_track_completion_summary_name(original_run,revision),summary)
    print('FAST_TRACK_COMPLETION_STAGE:summary-publish:verified', flush=True)
    return {'state':'ORIGINAL_COMPLETIONS_VERIFIED','targets':results,'originalRun':original_run}


RECOVERY_PREFLIGHT_NONENTRY_CONTRACT = 'retained-recovery-preflight/v1'
RECOVERY_PREFLIGHT_REASONS = frozenset({
    'provider-read-timeout','provider-read-unavailable','history-empty','provider-active',
    'runtime-unavailable','runtime-candidate-unproven','baseline-not-ancestor',
    'original-window-unproven','provider-history-time-unproven','newer-provider-operation',
    'baseline-drift','preflight-authority-unproven','phase-deadline'})


class RecoveryPreflightFailure(DeploymentReconciliationRequired):
    def __init__(self, reason, observation=None):
        if reason not in RECOVERY_PREFLIGHT_REASONS:
            raise ValueError('Unknown recovery preflight reason')
        super().__init__('RECOVERY_PREFLIGHT_' + reason.upper().replace('-', '_'))
        self.reason = reason
        self.observation = observation or dict(historyReadComplete=False, historyCount=None, activeCount=None, runtimeRevision=None)


class RecoveryPreflightDrift(RecoveryPreflightFailure, DeploymentDrift):
    """Preserve the existing terminal identity-conflict classification."""


def recovery_preflight_nonentry_record(proof, failed_target, failure):
    """A terminal no-worker receipt, NOT idle history or publication authority."""
    observer = proof['observer']
    observation = failure.observation if isinstance(failure, RecoveryPreflightFailure) else dict(
        historyReadComplete=False, historyCount=None, activeCount=None, runtimeRevision=None)
    reason = failure.reason if isinstance(failure, RecoveryPreflightFailure) else ('phase-deadline' if isinstance(failure, RecoveryPhaseDeadline) else 'preflight-authority-unproven')
    record = dict(schemaVersion=1, phase='preflight-nonentry', **proof,
        producingRun=observer['runId'], producingAttempt=observer['runAttempt'], failedTarget=failed_target,
        stage='all-target-preflight', authorizesMutation=False, writePhaseEntered=False,
        reason=reason, observation=observation)
    identity = {key: record[key] for key in ('applicationRevision','originalRun','producingRun','producingAttempt')}
    name = 'legend-retained-preflight-nonentry-' + hashlib.sha256(
        json.dumps(identity,sort_keys=True,separators=(',',':')).encode()).hexdigest()
    return name, record


def recovery_target_preintent_record(proof, target, failure):
    """Only this target/attempt stopped before its intent publication."""
    _, record = recovery_preflight_nonentry_record(proof, target, failure)
    record.pop('writePhaseEntered')
    record.update(phase='target-preintent-hold', stage='before-new-intent', targetWritePhaseEntered=False)
    identity={key:record[key] for key in ('applicationRevision','originalRun','producingRun','producingAttempt','failedTarget')}
    name='legend-retained-target-preintent-'+hashlib.sha256(json.dumps(identity,sort_keys=True,separators=(',',':')).encode()).hexdigest()
    return name,record


def recovery_current_provider_state(rows, observed):
    observation=dict(historyReadComplete=True,historyCount=len(rows),runtimeRevision=observed)
    if not rows:raise RecoveryPreflightFailure('history-empty',observation)
    if len({row['id'] for row in rows}) != len(rows):
        raise RecoveryPreflightFailure('preflight-authority-unproven',observation)
    normalized=[]
    for row in rows:
        if (row.get('status') not in (3,4) or row.get('complete') is not True
            or not all(isinstance(row.get(k),str) and row[k] for k in ('received_time','start_time','end_time'))):
            raise RecoveryPreflightFailure('preflight-authority-unproven',observation)
        received,start,end=(_RELEASE_AUTHORITY._completion_time(row[k]) for k in ('received_time','start_time','end_time'))
        if not received<=start<=end:
            raise RecoveryPreflightFailure('provider-history-time-unproven',observation)
        normalized.append({k:row.get(k) for k in ('id','status','received_time','start_time','end_time','complete','active')})
    state=dict(revision=observed,deploymentIds=sorted(row['id'] for row in rows),
        providerInventoryIdentity=hashlib.sha256(json.dumps(sorted(normalized,key=lambda row:row['id']),sort_keys=True,separators=(',',':')).encode()).hexdigest())
    return state


def recovery_preupload_baseline(azure, original, key, *, expected=None):
    """Fresh idle provider state cannot overtake the attested original no-entry."""
    # This function only establishes a NEW-write baseline; inherited intents
    # reconcile separately with the full publication phase, without this reserve.
    if isinstance(azure, RecoveryDeadlineAzure):
        azure=RecoveryDeadlineAzure(azure,azure.deadline-RECOVERY_WRITE_RESERVE_SECONDS)
        recovery_phase_remaining(azure.deadline,1)
    try:
        rows = read_deployments_bounded(azure, phase='retained recovery before any upload')
    except DeploymentStatusUnavailable as exc:
        if isinstance(azure,RecoveryDeadlineAzure) and azure.deadline-time.monotonic()<=1:
            raise RecoveryPhaseDeadline('RECOVERY_PREFLIGHT_READ_PHASE_EXHAUSTED') from exc
        reason = 'provider-read-timeout' if isinstance(exc.__cause__, subprocess.TimeoutExpired) else 'provider-read-unavailable'
        raise RecoveryPreflightFailure(reason) from exc
    observed = azure.observed_revision()
    observation = dict(historyReadComplete=True, historyCount=len(rows),
        activeCount=sum(row['status'] in (0,1,2) for row in rows), runtimeRevision=observed)
    if not rows: raise RecoveryPreflightFailure('history-empty',observation)
    if observation['activeCount']: raise RecoveryPreflightFailure('provider-active',observation)
    if observed is None: raise RecoveryPreflightFailure('runtime-unavailable',observation)
    if observed == original['run']['head_sha']: raise RecoveryPreflightFailure('runtime-candidate-unproven',observation)
    current_root = ('baselineAdoption' in original and _RELEASE_AUTHORITY.stable_baseline_current_root(
        os.environ['GITHUB_REPOSITORY']) is not None)
    if 'resourceBaselines' in original:
        repository=os.environ['GITHUB_REPOSITORY'];token=os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
        reference=original['resourceBaselines']['resources'][key]
        with _RELEASE_AUTHORITY.recovery_admission_proof():
            accepted=_RELEASE_AUTHORITY.completed_resource_receipt(repository,key,reference,token)
        state=recovery_current_provider_state(rows,observed)
        expected_ids=sorted(set(accepted['record']['baselineDeploymentIds']+accepted['record']['deploymentIds']))
        provider=[row for row in rows if row['id'] in accepted['record']['deploymentIds']]
        if (observed!=reference['sourceRevision'] or state['deploymentIds']!=expected_ids
            or len(provider)!=1 or provider[0].get('status')!=4 or provider[0].get('complete') is not True):
            raise RecoveryPreflightDrift('baseline-drift',observation)
        if expected is not None and state!=expected:raise RecoveryPreflightDrift('baseline-drift',observation)
        return state
    if current_root:
        # The approved current-state root does not require a predecessor SHA or
        # the old upload window. It does require complete known terminal provider
        # state, an actual serving revision, and separately verified rollback.
        state=recovery_current_provider_state(rows,observed)
        if expected is not None and state!=expected:
            raise RecoveryPreflightDrift('baseline-drift',observation)
        return state
    if subprocess.run(['git', 'merge-base', '--is-ancestor', observed, original['run']['head_sha']], capture_output=True).returncode:
        raise RecoveryPreflightDrift('baseline-not-ancestor',observation)
    starts = []
    for jobs in original['attempts'].values():
        for job in jobs:
            if job['name'] == 'Affected App Deploy — ' + _RELEASE_AUTHORITY.fast_track_target_label(key):
                starts.extend(_RELEASE_AUTHORITY._completion_time(step['started_at']) for step in job.get('steps', [])
                              if step['name'] == 'Publish or reconcile exact application bytes' and step.get('started_at'))
    if not starts:
        raise RecoveryPreflightFailure('original-window-unproven',observation)
    cutoff = min(starts)
    try:
        if any(_RELEASE_AUTHORITY._completion_time(row['received_time']) >= cutoff for row in rows):
            raise RecoveryPreflightDrift('newer-provider-operation',observation)
    except (KeyError, TypeError, ValueError) as exc:
        raise RecoveryPreflightFailure('provider-history-time-unproven',observation) from exc
    state = dict(revision=observed, deploymentIds=sorted(row['id'] for row in rows))
    if expected is not None and state != expected:
        raise RecoveryPreflightDrift('baseline-drift',observation)
    return state


def prepare_stable_baseline_rollback(original, key, state, package_root, *, expected=None):
    """Recover exact currently serving bytes, without rebuilding or replaying SQL."""
    if 'baselineAdoption' not in original and 'resourceBaselines' not in original:
        return None
    return prepare_completed_resource_rollback(key,state,package_root,expected=expected)


def prepare_completed_resource_rollback(key, state, package_root, *, expected=None):
    """Verify exact recoverable bytes; no source-history reconstruction or rebuild."""
    authority = _RELEASE_AUTHORITY
    repository = os.environ['GITHUB_REPOSITORY']; token = os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
    revision = state['revision']
    name = 'fast-track-package-' + revision + '-' + key
    rows = authority._completion_inventory(authority.api_get(repository,
        'actions/artifacts?name=' + authority.urllib.parse.quote(name, safe='') + '&per_page=100', token), 'artifacts')
    if len(rows) != 1 or rows[0].get('name') != name or rows[0].get('expired') is not False:
        raise DeploymentReconciliationRequired('STABLE_BASE_ROLLBACK_UNAVAILABLE')
    artifact = rows[0]; producer_id = artifact.get('workflow_run', {}).get('id')
    run = authority.api_get(repository, f'actions/runs/{producer_id}', token)
    if (not authority.is_fast_track_producer(repository, run) or run.get('head_sha') != revision
        or run.get('status') != 'completed' or artifact.get('workflow_run', {}).get('head_sha') != revision
        or not re.fullmatch('sha256:[a-f0-9]{64}', artifact.get('digest', ''))):
        raise DeploymentReconciliationRequired('STABLE_BASE_ROLLBACK_PRODUCER_UNPROVEN')
    attempts = authority._completion_jobs(repository, run, token)
    authority._completion_original_artifact_owner(dict(run=run, attempts=attempts), key, artifact,
        'Preserve immutable application bytes before upload', ('success',))
    directory = package_root / ('stable-rollback-' + key)
    if not (directory / 'deploy.zip').is_file():
        authority._download_run_artifact(repository, producer_id, name, directory,
            artifact_id=artifact['id'], expected_digest=artifact['digest'])
    if (directory / 'deploy.zip').is_symlink():raise ValueError('STABLE_BASE_ROLLBACK_SYMLINK')
    digest = verify_package(directory / 'deploy.zip', revision, TARGETS[key]['static'])
    proof = dict(revision=revision, artifactId=artifact['id'], artifactDigest=artifact['digest'],
                 artifact=name, runId=producer_id, packageDigest=digest)
    if expected is not None and proof != expected:
        raise DeploymentDrift('STABLE_BASE_ROLLBACK_CHANGED')
    return proof


def prepare_protect_retained_replacement(azure, original, material, state, package_root):
    """Validate retained rollback bytes and the corrected live baseline before intent."""
    proof = material.get('unknownReplacement') or material.get('protectBaselineCorrection')
    if proof is not None and 'unknownReplacement' in material:
        proof = dict(proof, rollbackArtifactName='fast-track-package-' + proof['baselineRevision'] + '-protect')
    if proof is None:
        provider = azure
        while isinstance(provider, RecoveryDeadlineAzure):
            provider = provider.azure
        if (provider.app == TARGETS['protect']['releaseName']
            and _RELEASE_AUTHORITY.fresh_protect_retained_supersession(original, material, 'protect') is not None):
            prepare_protect_replacement(original['run']['head_sha'], state['revision'], azure)
        return
    if (original['run']['id'] != proof['originalRun']
        or original['run']['head_sha'] != proof['applicationRevision']
        or state['revision'] != proof['baselineRevision']):
        raise DeploymentDrift('PROTECT_RECOVERY_LIVE_BASELINE_CHANGED')
    directory = package_root / 'protect-rollback'
    directory.mkdir(exist_ok=True)
    if not (directory / 'deploy.zip').is_file():
        _RELEASE_AUTHORITY._download_run_artifact(os.environ['GITHUB_REPOSITORY'], proof['rollbackRun'],
            proof['rollbackArtifactName'], directory, artifact_id=proof['rollbackArtifactId'],
            expected_digest=proof['rollbackArtifactDigest'])
    if verify_package(directory / 'deploy.zip', proof['baselineRevision'], False) != proof['rollbackPackageDigest']:
        raise DeploymentDrift('PROTECT_RECOVERY_ROLLBACK_BYTES_CHANGED')
    recovery_host_flags('protect')
    provider = azure
    while isinstance(provider, RecoveryDeadlineAzure):
        provider = provider.azure
    provider.authorized_replacement = True


def recovery_host_flags(key):
    # Same immutable-business-host gate as ordinary publication. Never change it.
    if key not in {'protect', 'parfait'}:
        return
    result = subprocess.run(['az', 'webapp', 'config', 'appsettings', 'list', '--resource-group',
        _RELEASE_AUTHORITY.RELEASE_RESOURCE_GROUP, '--name', TARGETS[key]['releaseName'], '--only-show-errors', '-o', 'json'],
        capture_output=True, text=True, timeout=20)
    if result.returncode:
        raise DeploymentReconciliationRequired('RECOVERY_HOST_FLAGS_UNAVAILABLE')
    values = json.loads(result.stdout)
    flags = {'commerce__sharedhostcutover__enabled', 'commerce:sharedhostcutover:enabled',
             'commerce__sharedhostpreview__enabled', 'commerce:sharedhostpreview:enabled', 'legend_commerce_cutover_enabled'}
    if (not isinstance(values, list) or any(not isinstance(row, dict) or not isinstance(row.get('name'), str)
        or (row['name'].lower() in flags and str(row.get('value') or '').strip().lower() not in ('', 'false', '0')) for row in values)):
        raise DeploymentReconciliationRequired('RECOVERY_HOST_FLAGS_NOT_OFF')


RECOVERY_FINAL_RESERVE_SECONDS = 420  # metadata64 + authority90 + summary180 + cleanup86
RECOVERY_WRITE_RESERVE_SECONDS = 660  # intent180 + provider ACK300 + submitted180


class RecoveryPhaseDeadline(DeploymentReconciliationRequired):
    """A finite recovery phase has no authority to consume the next reserve."""


def recovery_phase_remaining(deadline, minimum=0, *, clock=time.monotonic):
    remaining = deadline - clock()
    if remaining <= 0 or remaining < minimum:
        raise RecoveryPhaseDeadline('RECOVERY_PHASE_DEADLINE_HOLD_RECONCILE_ONLY')
    return remaining


def recovery_job_deadline(authority, repository, observer, token, *, now=None, clock=time.monotonic):
    job = authority.api_get(repository, 'actions/jobs/' + str(observer['jobId']), token)
    if (job.get('run_id') != observer['runId'] or job.get('run_attempt') != observer['runAttempt']
        or job.get('head_sha') != observer['toolRevision'] or job.get('status') != 'in_progress'
        or job.get('name') != authority.FAST_TRACK_RECOVERY_JOB):
        raise DeploymentReconciliationRequired('RECOVERY_BUDGET_OWNER_UNPROVEN')
    elapsed = ((now or datetime.datetime.now(datetime.timezone.utc)) -
               authority._completion_time(job.get('started_at'))).total_seconds()
    if elapsed < 0 or elapsed >= 1800:
        raise DeploymentReconciliationRequired('RECOVERY_JOB_DEADLINE_EXHAUSTED')
    return clock() + 1800 - elapsed


class RecoveryDeadlineAzure:
    """Clip canonical read allowance to this finite publication phase."""
    def __init__(self, azure, deadline):
        self.azure, self.deadline = azure, deadline
    def __getattr__(self, name):
        return getattr(self.azure, name)
    def deployments(self):
        remaining=recovery_phase_remaining(self.deadline)
        cap=min(_DEPLOYMENT_READ_CAP.get(),remaining)
        if cap<=1:raise DeploymentStatusUnavailable('Recovery deployment read deadline exhausted')
        token=_DEPLOYMENT_READ_CAP.set(cap)
        try:return self.azure.deployments()
        finally:_DEPLOYMENT_READ_CAP.reset(token)
    def observed_revision(self):
        recovery_phase_remaining(self.deadline, 15)
        return self.azure.observed_revision()
    def prepare_submission(self):
        recovery_phase_remaining(self.deadline, 40)
        return self.azure.prepare_submission()
    def submit(self, correlation=None):
        recovery_phase_remaining(self.deadline, 300)
        return self.azure.submit(correlation)


def recover_fast_track(original_run, revision, package_root, *, prepare_only=False):
    authority = _RELEASE_AUTHORITY
    repository = os.environ['GITHUB_REPOSITORY']; token = os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
    # Authentication receipts belong to the same byte-only invocation cache.
    with authority.recovery_material_reuse():
        outcome = 'failure'
        try:
            with authority.recovery_admission_proof():
                observer = authority.assert_fast_track_recovery_execution(original_run, revision)
            authority.assert_fast_track_recovery_live_execution(original_run, revision, observer)
            deadline = recovery_job_deadline(authority, repository, observer, token)
            # Nested authority reads share the original finite job budget.
            with authority.evidence_lookup_budget(deadline - 60):
                result = _recover_fast_track(original_run, revision, package_root, observer, deadline, prepare_only=prepare_only)
            outcome = 'success'
            return result
        finally:
            try:
                authority.report_recovery_efficiency(outcome)
            except BaseException:
                pass  # Diagnostics never replace the original result or failure.


def recovery_summary_record(original_run, original, revision, observer, records):
    """One constructor for preflight shape validation and durable completion."""
    return dict(schemaVersion=1, phase='success', originalRun=original_run,
                originalAttempt=original['run']['run_attempt'], applicationRevision=revision,
                producingRun=observer['runId'], producingAttempt=observer['runAttempt'],
                observer=observer, targets=records)



def completed_resource_acceptance(original, key, observer, azure, operation_id, rollback, publisher, *, schema_proof=None):
    """A target advances only after its provider and public obligations succeeded.

    The immutable success journal remains effect evidence. This additional record
    binds postverification acceptance, including when another target later fails.
    """
    authority=_RELEASE_AUTHORITY
    repository=os.environ['GITHUB_REPOSITORY'];token=os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
    revision=original['run']['head_sha']
    with authority.recovery_admission_proof():
        success=authority.release_operation_history(repository,operation_id,revision,key,
            observer['runId'],observer['runAttempt'],token,phase='success')
        if success is None:raise DeploymentReconciliationRequired('RESOURCE_ACCEPTANCE_SUCCESS_MISSING')
        producing_run=success['producingRun']
        rows=authority._completion_inventory(authority.api_get(repository,
            f'actions/runs/{producing_run}/artifacts?per_page=100',token),'artifacts')
        name='legend-release-operation-success-'+success['operationId']
        matches=[a for a in rows if a.get('name')==name and a.get('expired') is False]
        if len(matches)!=1:raise DeploymentReconciliationRequired('RESOURCE_ACCEPTANCE_SUCCESS_ARTIFACT_UNPROVEN')
        artifact=matches[0]
        actual=authority._release_history_json(repository,producing_run,artifact,'operation.json',verify_digest=True)
        if actual!=success:raise DeploymentDrift('RESOURCE_ACCEPTANCE_SUCCESS_CHANGED')
    provider_rows=read_deployments_bounded(azure,phase='target acceptance')
    state=recovery_current_provider_state(provider_rows,azure.observed_revision())
    accepted=[r for r in provider_rows if r['id'] in success['deploymentIds']]
    if (state['revision']!=revision or len(accepted)!=1 or accepted[0].get('status')!=4
        or accepted[0].get('complete') is not True):
        raise DeploymentReconciliationRequired('RESOURCE_ACCEPTANCE_SERVING_UNPROVEN')
    if original['scope']['commerce']:
        if schema_proof is None:raise DeploymentReconciliationRequired('RESOURCE_ACCEPTANCE_SCHEMA_MISSING')
        crm_retained_schema_fresh(schema_proof,original,observer)
    from datetime import datetime,timezone
    record=dict(schemaVersion=1,phase='target-acceptance',target=key,applicationRevision=revision,
        originalRun=original['run']['id'],originalAttempt=success.get('recoveryEvidence',{}).get('originalAttempt',success['producingAttempt']),
        producingRun=observer['runId'],producingAttempt=observer['runAttempt'],observer=observer,
        successReference=dict(sourceRevision=revision,runId=producing_run,runAttempt=success['producingAttempt'],
            artifactId=artifact['id'],artifactDigest=artifact['digest'],artifactName=artifact['name']),
        baselineReference=original['resourceBaselines']['resources'][key],rollbackEvidence=rollback,
        schemaProof=None if schema_proof is None else {k:schema_proof[k] for k in ('artifactId','contentIdentity')},
        observation=dict(deploymentId=accepted[0]['id'],packageDigest=success['packageDigest'],runtimeRevision=revision,
            providerInventoryIdentity=state['providerInventoryIdentity'],observedUtc=datetime.now(timezone.utc).isoformat(),
            publicMetadataVerified=True))
    identity=hashlib.sha256(json.dumps(record,sort_keys=True,separators=(',',':')).encode()).hexdigest()
    result=publisher('legend-release-target-acceptance-'+identity,record)
    return dict(record=record,artifact=result)


def crm_retained_portal_gate(original, inventory, observer, deadline, *, expected=None):
    """Retained recovery uses its durable receipt, not the failed original job."""
    authority = _RELEASE_AUTHORITY
    repository = os.environ['GITHUB_REPOSITORY']; token = os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
    revision = original['run']['head_sha']; material = inventory['portal']
    with authority.evidence_lookup_budget(deadline):
        # Close immutable receipt proofs before fresh provider/live observations.
        with authority.recovery_admission_proof():
            success = authority.release_operation_history(repository, material['operationId'], revision, 'portal',
                observer['runId'], observer['runAttempt'], token, phase='success')
            if (success is None or success.get('packageDigest') != material['packageDigest']
                or success.get('applicationRevision') != revision or success.get('target') != 'portal'
                or len(success.get('deploymentIds', [])) != 1):
                raise DeploymentReconciliationRequired('CRM_RECOVERY_PORTAL_RECEIPT_UNPROVEN')
        azure = RecoveryDeadlineAzure(target_azure('portal', None, revision), deadline)
        rows = read_deployments_bounded(azure, phase='retained CRM Portal-first barrier', deadline=deadline)
        if (any(row['status'] in (0, 1, 2) for row in rows)
            or azure.observed_revision() != revision
            or not any(row['id'] in success['deploymentIds'] and row['status'] == 4 for row in rows)):
            raise DeploymentReconciliationRequired('CRM_RECOVERY_PORTAL_LIVE_UNPROVEN')
        authority.assert_fast_track_recovery_live_execution(original['run']['id'], revision, observer)
        if authority.stable_baseline_partial_marker(repository,observer.get('toolRevision')) is not None:
            from datetime import datetime, timezone
            current=dict(recovery_current_provider_state(rows,revision),observer=observer,observedUtc=datetime.now(timezone.utc).isoformat())
            if expected is not None and {k:v for k,v in current.items() if k!='observedUtc'}!={k:v for k,v in expected.items() if k!='observedUtc'}:
                raise DeploymentDrift('STABLE_CARRIED_PORTAL_CURRENT_STATE_CHANGED')
            return current


def crm_retained_schema_fresh(proof, original, observer):
    """No earlier read can silently outlive the canonical 900-second window."""
    from datetime import datetime, timezone
    record = proof['record']
    if (record['originalRun'] != original['run']['id'] or record['applicationRevision'] != original['run']['head_sha']
        or record['observer'] != observer or type(proof.get('artifactId')) is not int or proof['artifactId'] < 1):
        raise DeploymentReconciliationRequired('CRM_RECOVERY_SCHEMA_PROOF_CHANGED')
    for value in (record['observation']['observedUtc'], record['recovery']['observedUtc']):
        age = (datetime.now(timezone.utc) - _RELEASE_AUTHORITY._completion_time(value)).total_seconds()
        if not 0 <= age <= 900:
            raise DeploymentReconciliationRequired('CRM_RECOVERY_SCHEMA_PROOF_STALE')


def crm_retained_schema_gate(original, observer, deadline, publisher):
    """Read-only schema/recovery proof; no bundle or migration executor exists."""
    authority = _RELEASE_AUTHORITY
    repository = os.environ['GITHUB_REPOSITORY']; token = os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
    run = original['run']; revision = run['head_sha']
    forward='resourceBaselines' in original
    if not forward and (repository.lower() != 'mylegnd/masterapp' or run['id'] != 41131223973
        or revision != authority.CRM_PREINTENT_REVISION or set(original['targets']) != {'portal','client','protect','parfait'}):
        raise DeploymentReconciliationRequired('CRM_RECOVERY_SCHEMA_SCOPE_UNPROVEN')
    authority.assert_fast_track_recovery_live_execution(run['id'], revision, observer)
    fast_track_migration_gate(run)
    schema_reference=None
    if forward:
        receipts=[]
        for artifact in original['artifacts']:
            if not artifact.get('name','').startswith('legend-release-child-success-'):continue
            value=authority._release_history_json(repository,run['id'],artifact,'operation.json',verify_digest=True)
            if value.get('child')!='migrations':continue
            reference=dict(sourceRevision=revision,runId=run['id'],runAttempt=value['producingAttempt'],
                artifactId=artifact['id'],artifactDigest=artifact['digest'],artifactName=artifact['name'])
            receipts.append((reference,authority.completed_resource_receipt(repository,'schema',reference,token)['record']))
        if len(receipts)!=1:raise DeploymentReconciliationRequired('RESOURCE_RECOVERY_SCHEMA_RECEIPT_UNPROVEN')
        schema_reference,receipt=receipts[0]
    else:
        # Bind to the independently reviewed attempt3 physical schema success, not
        # the older SQL intent whose original failed outcome remains preserved.
        rows = [row for row in original['artifacts'] if row.get('id') == 13017640415]
        if (len(rows) != 1 or rows[0].get('expired') is not False
            or rows[0].get('digest') != 'sha256:cbe0bd54e2e652908a19d84779ec10f51246cf3e01e3920f8e59d50c70b2a508'):
            raise DeploymentReconciliationRequired('CRM_RECOVERY_SCHEMA_RECEIPT_UNPROVEN')
        receipt = authority._release_history_json(repository, run['id'], rows[0], 'operation.json', verify_digest=True)
        if (receipt.get('producingRun') != run['id'] or receipt.get('producingAttempt') != 3
            or receipt.get('applicationRevision') != revision or receipt.get('child') != 'migrations'
            or receipt.get('phase') != 'success' or receipt.get('observation', {}).get('providerVersion') != 'schema-ready-no-write'):
            raise DeploymentReconciliationRequired('CRM_RECOVERY_SCHEMA_RECEIPT_UNPROVEN')
    spec = importlib.util.spec_from_file_location('retained_schema_observer', Path(__file__).with_name('release-migration.py'))
    migration = importlib.util.module_from_spec(spec); spec.loader.exec_module(migration)
    audit = migration.history_audit()
    # No current migration is excluded: recovery is not a SQL owner. All normal
    # migration producers must be settled, including the complete original run.
    if forward:
        report=authority.completed_resource_recovery_currentness(repository,original,observer,token)
    elif authority.stable_baseline_current_root(repository,observer['toolRevision']) is not None:
        # Reuse the explicit retired-provenance cut, not 241 old migration
        # attempts. Current/changed writers still stop before physical SQL reads.
        with authority.recovery_admission_proof():
            current=authority.stable_baseline_current_inventory(repository,observer,token)
        report=dict(scope='approved-current-state-root',inventory=current,sqlExecutionAuthorized=False)
    else:
        with authority.evidence_lookup_budget(min(deadline, time.monotonic() + 120)):
            report = audit.audit(authority, repository, token)
        if (report.get('completeHistory') is not True or any(
            row.get('parent') not in {'success','failure','cancelled','skipped'}
            or row['code'] in audit.BLOCKED_CODES or row['code'] not in audit.STATUS_CODES
            for row in report['records'])):
            raise DeploymentReconciliationRequired('CRM_RECOVERY_SCHEMA_HISTORY_UNPROVEN')
    probe = Path(os.environ['MIGRATION_PROBE_DLL']); connection = migration.connection_string()
    physical_deadline = min(deadline, time.monotonic() + 90)
    first = migration.observe(probe, connection, schema_only_activity=True, deadline=physical_deadline)
    second = migration.observe(probe, connection, schema_only_activity=True, deadline=physical_deadline)
    for key in ('databaseIdentity','schemaIdentity','baselineIdentity','ready','pendingCount','mutationActivity'):
        if key not in first or first[key] != second.get(key):
            raise DeploymentReconciliationRequired('CRM_RECOVERY_SCHEMA_DRIFT')
    if (second['ready'] is not True or type(second['pendingCount']) is not int or second['pendingCount'] != 0
        or second['mutationActivity'] != 'settled' or second['schemaIdentity'] != receipt.get('observation', {}).get('schemaIdentity')
        or second['databaseIdentity'] != '3a055450807aeaa2c6465702b983dc99968329389d7c0ae480c3c72cc3a9ff3b'):
        raise DeploymentReconciliationRequired('CRM_RECOVERY_SCHEMA_NOT_READY')
    recovery_phase_remaining(deadline, 420)
    recovery = migration.production_recovery(second['databaseIdentity'])
    if (recovery.get('databaseIdentity') != second['databaseIdentity']
        or recovery.get('restoreWindowVerified') is not True
        or recovery.get('restoreExecuted') is not False or recovery.get('authorizesMutation') is not False):
        raise DeploymentReconciliationRequired('CRM_RECOVERY_SCHEMA_NOT_READY')
    authority.assert_fast_track_recovery_live_execution(run['id'], revision, observer)
    record = dict(schemaVersion=2 if forward else 1, phase='retained-schema-read-only',
        applicationRevision=revision, producingRun=observer['runId'], producingAttempt=observer['runAttempt'],
        observation={key:second[key] for key in ('databaseIdentity','schemaIdentity','baselineIdentity',
            'observedUtc','pendingCount','ready','mutationActivity')}, recovery=recovery,
        historyIdentity=hashlib.sha256(json.dumps(report,sort_keys=True,separators=(',',':')).encode()).hexdigest(),
        originalRun=run['id'], originalSuccessArtifact=schema_reference['artifactId'] if forward else 13017640415, observer=observer,
        sqlExecutionAuthorized=False)
    if forward:record['schemaReference']=schema_reference
    digest = hashlib.sha256(json.dumps(record,sort_keys=True,separators=(',',':')).encode()).hexdigest()
    name = 'legend-retained-schema-proof-' + digest
    recovery_phase_remaining(deadline, 180)
    artifact = publisher(name, record)
    proof = dict(record=record, artifactId=artifact['artifactId'], name=name, contentIdentity=digest)
    crm_retained_schema_fresh(proof, original, observer)
    print('CRM_RETAINED_SCHEMA_VERIFIED:' + json.dumps({key:proof[key] for key in
        ('artifactId','name','contentIdentity')},sort_keys=True),flush=True)
    return proof


def _recover_fast_track(original_run, revision, package_root, observer, deadline, *, prepare_only=False):
    """Resume retained original materials under a separately promoted tool owner."""
    authority = _RELEASE_AUTHORITY
    repository = os.environ['GITHUB_REPOSITORY']; token = os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
    # Repeated original/material proofs share one read-only snapshot and the
    # existing authenticated byte cache. Close with uncached readback before
    # schema evidence, live provider checks or any publication can begin.
    with authority.recovery_admission_proof():
        original = authority.fast_track_completion_original(repository, original_run, revision, token)
        materials = {key: authority.fast_track_recovery_material(repository, original, key, token) for key in original['targets']}
    if 'resourceBaselines' in original:
        authority.completed_resource_recovery_currentness(repository,original,observer,token)
        if 'protect' in original['targets']:
            raise DeploymentReconciliationRequired('RESOURCE_BASELINE_PROTECT_68EF_DISPOSITION_REQUIRED')
    inventory = {}
    for key, material in materials.items():
        artifact = material['packageArtifact']; directory = package_root / key
        if prepare_only:
            authority._download_run_artifact(repository, original_run, artifact['name'], directory,
                                            artifact_id=artifact['id'], expected_digest=artifact['digest'])
        if any(not (directory / name).is_file() or (directory / name).is_symlink() for name in ('deploy.zip', 'SHA256SUMS')):
            raise ValueError('RECOVERY_RETAINED_PACKAGE_MISSING_NO_REBUILD')
        digest = verify_package(directory / 'deploy.zip', revision, TARGETS[key]['static'])
        identity = dict(target=key, applicationRevision=revision, packageDigest=digest)
        operation = hashlib.sha256(json.dumps(identity, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
        intent = material['originalIntent']
        if intent is not None and any(intent.get(name) != value for name, value in dict(identity, operationId=operation, packageProducerRun=original_run).items()):
            raise ValueError('RECOVERY_ORIGINAL_INTENT_MATERIAL_CHANGED')
        if 'unknownReplacement' in material:
            operation = authority.retained_operation_id(key, revision, digest, material['unknownReplacement'])
        inventory[key] = dict(operationId=operation, packageDigest=digest, packageArtifactId=artifact['id'], packageArtifactDigest=artifact['digest'])
    proof = dict(originalRun=original_run, originalAttempt=original['run']['run_attempt'], applicationRevision=revision,
                 observer=observer, targets=inventory)
    path = package_root / 'retained-recovery-plan.json'
    if prepare_only:
        path.write_text(json.dumps(proof, sort_keys=True) + '\n')
        return dict(state='ORIGINAL_RECOVERY_MATERIALS_VERIFIED', **proof)
    if path.is_symlink() or json.loads(path.read_text()) != proof:
        raise ValueError('RECOVERY_PREPARED_AUTHORITY_OR_MATERIAL_CHANGED')
    spec = importlib.util.spec_from_file_location('recovery_journal', Path(__file__).with_name('release-operation-evidence.py'))
    owner = importlib.util.module_from_spec(spec); spec.loader.exec_module(owner)
    os.environ['PACKAGE_PRODUCER_RUN'] = str(original_run)
    publication_deadline = deadline - RECOVERY_FINAL_RESERVE_SECONDS
    crm = original_run == 41131223973 and revision == authority.CRM_PREINTENT_REVISION
    journals = {}; states = {}; rollback = {}; carried_portal = None; key = 'portal' if crm else None
    schema_proof=None
    needs_schema=crm or ('resourceBaselines' in original and original['scope']['commerce'])
    def publish_phase_record(name, record):
        return owner.publish_record(name, record, timeout=min(180, recovery_phase_remaining(publication_deadline)))
    # Versioned terminal receipt boundary: every enclosed operation is read-only.
    try:
        if needs_schema:
            with authority.evidence_lookup_budget(publication_deadline):
                schema_proof = crm_retained_schema_gate(original, observer, publication_deadline, publish_phase_record)
        with authority.recovery_admission_proof():
            # All targets are validated before the first possible new intent.
            for key, material in materials.items():
                azure = RecoveryDeadlineAzure(target_azure(key, package_root / key / 'deploy.zip', revision), publication_deadline)
                prior = authority.release_operation_history(repository, inventory[key]['operationId'], revision, key,
                    observer['runId'], observer['runAttempt'], token,
                    **({'replacement': materials[key]['unknownReplacement']} if 'unknownReplacement' in materials[key] else {}))
                if prior is None:
                    if authority.stable_baseline_current_root(repository,observer['toolRevision']) is not None:
                        for phase in ('submitted','success'):
                            orphan=authority.release_operation_history(repository,inventory[key]['operationId'],revision,key,
                                observer['runId'],observer['runAttempt'],token,phase=phase)
                            if orphan is not None:
                                raise DeploymentReconciliationRequired('STABLE_ROOT_UNMATCHED_OPERATION_RECEIPT')
                    states[key] = recovery_preupload_baseline(azure, original, key)
                    prepare_protect_retained_replacement(azure, original, material, states[key], package_root)
                    rollback[key] = prepare_stable_baseline_rollback(original, key, states[key], package_root)
                    baseline = states[key]['revision']
                else:
                    baseline = prior['baseline']
                    states[key] = dict(revision=baseline, deploymentIds=prior['baselineDeploymentIds'])
                    rollback[key] = prepare_stable_baseline_rollback(original, key, states[key], package_root)
                    if authority.stable_baseline_partial_marker(repository,observer['toolRevision']) is not None:
                        if key!='portal':raise DeploymentReconciliationRequired('STABLE_PARTIAL_UNEXPECTED_INHERITED_INTENT')
                        carried=authority.stable_baseline_carried_portal(repository,original,observer,token)
                        if prior!=carried['intent'] or rollback[key]!=carried['target']['rollbackEvidence']:
                            raise DeploymentReconciliationRequired('STABLE_CARRIED_BASELINE_CHANGED')
                        carried_portal=dict(carried['binding'])
                        states[key]['providerInventoryIdentity']=carried['target']['providerInventoryIdentity']
                evidence = authority.fast_track_recovery_record_proof(original, material, observer)
                def before_write(ids, *, key=key, azure=azure):
                    try:
                        if crm and key != 'portal':
                            crm_retained_portal_gate(original, inventory, observer, publication_deadline,
                                expected=carried_portal.get('currentServingObservation') if carried_portal else None)
                        # A separate historical proof closes with uncached readback
                        # before fresh live ownership/provider checks and any intent.
                        with authority.recovery_admission_proof():
                            authority._completion_original_unchanged(repository, original['run'], token)
                            existing = authority.release_operation_history(repository, inventory[key]['operationId'], revision, key,
                                observer['runId'], observer['runAttempt'], token,
                                **({'replacement': materials[key]['unknownReplacement']} if 'unknownReplacement' in materials[key] else {}))
                            if existing is not None:
                                raise DeploymentReconciliationRequired('RECOVERY_INTENT_APPEARED_RECONCILE_ONLY')
                            if authority.assert_fast_track_recovery_execution(original_run, revision) != observer:
                                raise ValueError('RECOVERY_EXECUTION_CHANGED_BEFORE_INTENT')
                        authority.assert_fast_track_recovery_live_execution(original_run, revision, observer)
                        if verify_package(package_root / key / 'deploy.zip', revision, TARGETS[key]['static']) != inventory[key]['packageDigest']:
                            raise ValueError('RECOVERY_PACKAGE_CHANGED_BEFORE_INTENT')
                        fresh = recovery_preupload_baseline(azure, original, key, expected=states[key])
                        if sorted(ids) != fresh['deploymentIds']:
                            raise DeploymentDrift('RECOVERY_PROVIDER_BASELINE_CHANGED_BEFORE_INTENT')
                        prepare_protect_retained_replacement(azure, original, materials[key], fresh, package_root)
                        prepare_stable_baseline_rollback(original, key, fresh, package_root, expected=rollback.get(key))
                        recovery_host_flags(key)
                        if needs_schema:
                            crm_retained_schema_fresh(schema_proof, original, observer)
                        recovery_phase_remaining(publication_deadline, RECOVERY_WRITE_RESERVE_SECONDS)
                    except Exception as failure:
                        # This callback has not returned to journal.before_submit:
                        # no target intent publication/readback or submit is reachable.
                        name, record = recovery_target_preintent_record(proof, key, failure)
                        owner.publish_record(name, record, timeout=min(180, recovery_phase_remaining(deadline - 60)))
                        raise
                journal = owner.RecoveryJournal(target=key, application_revision=revision, package_digest=inventory[key]['packageDigest'],
                    baseline=baseline, authority=authority, recovery_evidence=evidence, before_new_intent=before_write, publisher=publish_phase_record)
                if journal.history_error is not None:
                    raise journal.history_error
                if prior is not None and journal.intent != prior:
                    raise DeploymentDrift('RECOVERY_EXISTING_INTENT_CHANGED')
                journal.validate_contract(states[key]['deploymentIds'] if prior is None else prior['baselineDeploymentIds'],
                    validator=lambda name, record: owner.validate_record(name, record,
                        timeout=min(30, recovery_phase_remaining(publication_deadline))))
                journals[key] = (azure, journal)
            preview = {key: dict(row, deploymentIds=['contract-preview']) for key, row in inventory.items()}
            owner.validate_record(authority.fast_track_recovery_summary_name(original_run, revision),
                recovery_summary_record(original_run, original, revision, observer, preview),
                timeout=min(30, recovery_phase_remaining(publication_deadline)))
    except Exception as failure:
        # No executor, provider preparation, intent or upload is reachable here.
        # Failed/ambiguous receipt publication also terminates; it never retries a write.
        name, record = recovery_preflight_nonentry_record(proof, key, failure)
        owner.publish_record(name, record, timeout=min(180, recovery_phase_remaining(deadline - 60)))
        raise
    if carried_portal is not None:
        # Historical preflight has closed; this is a separate fresh live observation.
        carried_portal['currentServingObservation']=crm_retained_portal_gate(original,inventory,observer,publication_deadline)
    if 'baselineAdoption' in original:
        record = dict(schemaVersion=1, phase='baseline-admission', authorizesMutation=False, liveStable=False,
            originalRun=original_run, applicationRevision=revision, observer=observer,
            baselineAdoption=authority.stable_baseline_identity(original['baselineAdoption']),
            schemaProofIdentity=schema_proof['contentIdentity'],
            targets={key: dict(inventory[key], baseline=states[key]['revision'],
                baselineDeploymentIds=states[key]['deploymentIds'], rollbackEvidence=rollback[key]) for key in original['targets']})
        if authority.stable_baseline_current_root(repository,observer['toolRevision']) is not None:
            record['currentStateRoot']=authority.STABLE_BASE_CURRENT_ROOT
            for key in record['targets']:
                record['targets'][key]['providerInventoryIdentity']=states[key].get('providerInventoryIdentity')
        if carried_portal is not None:record['carriedPortalSuccess']=carried_portal
        with authority.recovery_admission_proof():
            authority.validate_stable_baseline_admission(record, original, observer)
        publish_phase_record(authority.stable_baseline_admission_name(observer), record)
    def recover_target(key):
        if needs_schema:
            crm_retained_schema_fresh(schema_proof, original, observer)
        if crm and key != 'portal':
            crm_retained_portal_gate(original, inventory, observer, publication_deadline,
                                expected=carried_portal.get('currentServingObservation') if carried_portal else None)
        azure, journal = journals[key]
        def read_failure(failure):
            reason = 'provider-read-timeout' if isinstance(failure.__cause__, subprocess.TimeoutExpired) else 'provider-read-unavailable'
            name, record = recovery_target_preintent_record(proof, key, RecoveryPreflightFailure(reason))
            owner.publish_record(name, record, timeout=min(180, recovery_phase_remaining(deadline - 60)))
        with authority.evidence_lookup_budget(publication_deadline):
            authority._completion_original_unchanged(repository, original['run'], token)
            result = reconcile(azure, baseline=journal.baseline, journal=journal,
                reconcile_only=journal.intent is not None, require_receipt=True, before_intent_read_failure=read_failure,
                timeout=min(PUBLICATION_RECONCILE_TIMEOUT_SECONDS, recovery_phase_remaining(publication_deadline)))
        recovery_phase_remaining(deadline - 60, 64)
        verify_fast_track_public_metadata(key)
        if 'resourceBaselines' in original:
            completed_resource_acceptance(original,key,observer,azure,inventory[key]['operationId'],
                rollback[key],publish_phase_record,schema_proof=schema_proof)
        return result
    # Same all-target barrier and bounded parallel composition as normal release;
    # five independent 420s observers must not consume five sequential budgets.
    results = {}
    if crm:
        results['portal'] = recover_target('portal')
        crm_retained_portal_gate(original, inventory, observer, publication_deadline,
                                expected=carried_portal.get('currentServingObservation') if carried_portal else None)
        with authority.evidence_lookup_budget(publication_deadline):
            schema_proof = crm_retained_schema_gate(original, observer, publication_deadline, publish_phase_record)
    remaining = [key for key in journals if key not in results]
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, len(remaining))) as executor:
        futures = {executor.submit(contextvars.copy_context().run, recover_target, key): key for key in remaining}
        results.update({futures[future]: future.result() for future in concurrent.futures.as_completed(futures)})
    with authority.evidence_lookup_budget(min(deadline - 240, time.monotonic() + 90)):
        with authority.recovery_admission_proof():
            authority._completion_original_unchanged(repository, original['run'], token)
            if authority.assert_fast_track_recovery_execution(original_run, revision) != observer:
                raise ValueError('RECOVERY_EXECUTION_CHANGED_BEFORE_SUCCESS')
            records = {}
            for key in original['targets']:
                success = authority.release_operation_history(repository, inventory[key]['operationId'], revision, key,
                    observer['runId'], observer['runAttempt'], token, phase='success')
                if (success is None or success.get('packageDigest') != inventory[key]['packageDigest']
                    or len(success.get('deploymentIds', [])) != 1):
                    raise DeploymentReconciliationRequired('RECOVERY_DURABLE_SUCCESS_UNPROVEN')
                records[key] = dict(inventory[key], deploymentIds=success['deploymentIds'])
        authority.assert_fast_track_recovery_live_execution(original_run, revision, observer)
    summary = recovery_summary_record(original_run, original, revision, observer, records)
    owner.publish_record(authority.fast_track_recovery_summary_name(original_run, revision), summary,
        timeout=min(180, recovery_phase_remaining(deadline - 60)))
    return dict(state='RETAINED_RECOVERY_VERIFIED', results=results, summary=summary)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--target', choices=TARGETS)
    mode.add_argument('--targets-json')
    mode.add_argument('--fast-track-target', choices=TARGETS)
    mode.add_argument('--observe-azure-deployment')
    parser.add_argument('--observation-revision')
    parser.add_argument('--deployment-id')
    mode.add_argument('--recover-fast-track', type=int)
    parser.add_argument('--recovery-revision')
    parser.add_argument('--prepare-recovery-only', action='store_true')
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
    if args.recover_fast_track is not None:
        if (args.prepare_only or args.finalize_only or args.disposition_only or args.transaction_plan
            or args.reconcile_only or args.publish_prepared_parallel or args.observation_revision
            or args.deployment_id or args.validate_observation_only or args.baselines_json
            or args.completion_revision or args.validate_completion_only):
            raise SystemExit('Retained recovery cannot be combined with other modes')
        print(json.dumps(recover_fast_track(args.recover_fast_track, args.recovery_revision,
            Path(args.package_root), prepare_only=args.prepare_recovery_only), sort_keys=True))
        return
    if args.recovery_revision or args.prepare_recovery_only:
        raise SystemExit('Recovery inputs require retained recovery mode')
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
