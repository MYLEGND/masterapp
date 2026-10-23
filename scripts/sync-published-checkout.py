#!/usr/bin/env python3
"""Conservative launcher sync and read-only native checkout verification."""
import argparse
import fcntl
import hashlib
import json
import os
import re
import shlex
from pathlib import Path
import subprocess
import sys
import tempfile


class SyncSkipped(Exception):
    pass


def git(repo, *args, missing_ok=False):
    try:
        result = subprocess.run(
            ['git', '-C', str(repo), *args], text=True, capture_output=True,
            timeout=60, env={**os.environ, 'GIT_TERMINAL_PROMPT': '0', 'GIT_OPTIONAL_LOCKS': '0'})
    except (OSError, subprocess.TimeoutExpired) as error:
        raise SyncSkipped('Git could not complete; using the current local checkout.') from error
    if missing_ok and result.returncode == 1:
        return None
    if result.returncode:
        # Git errors can contain credential-bearing remote URLs. Keep them off launcher output.
        raise SyncSkipped('Git ' + args[0] + ' failed; using the current local checkout. Check Git connectivity and repository state.')
    return result.stdout.strip()


def active_dotnet_process():
    result = subprocess.run(['ps', '-axo', 'args='], text=True, capture_output=True, timeout=10)
    if result.returncode:
        raise SyncSkipped('Cannot verify .NET activity; no source changed.')
    for line in result.stdout.splitlines():
        try:
            args = shlex.split(line)
        except ValueError:
            if 'dotnet' in line:
                return True
            continue
        if not args or Path(args[0]).name.lower() != 'dotnet':
            continue
        parameters = args[1:]
        if parameters[:1] == ['exec']:
            parameters = parameters[1:]
        binary = Path(parameters[0]).name if parameters else ''
        if binary in {'VBCSCompiler.dll', 'Microsoft.VisualStudio.ProjectSystem.Server.BuildHost.dll'}:
            continue
        if binary == 'MSBuild.dll' and any(x.lower().startswith('/nodemode:') for x in parameters[1:]):
            continue
        # Actual CLI builds, running apps and unrecognized dotnet processes block.
        return True
    return False


def active_build_or_app():
    result = subprocess.run(['ps', '-axo', 'comm='], text=True, capture_output=True, timeout=10)
    if result.returncode:
        raise SyncSkipped('Could not verify running apps/builds; no source update attempted.')
    names = {Path(line.strip()).name.lower() for line in result.stdout.splitlines()}
    # Conservative across checkouts: do not modify source while an app or build is running.
    if names & {'agentportal', 'clientapp', 'xcodebuild', 'gradle', 'gradlew', 'legend'}:
        return True
    if 'dotnet' in names and active_dotnet_process():
        return True
    if 'java' in names:
        # Idle Gradle daemons and Android Studio do not imply an active build.
        arguments = subprocess.run(['ps', '-axo', 'args='], text=True, capture_output=True, timeout=10)
        if arguments.returncode:
            raise SyncSkipped('Could not verify active Java builds; no source update attempted.')
        return any(marker in arguments.stdout for marker in
                   ('org.gradle.wrapper.GradleWrapperMain', 'org.gradle.launcher.GradleMain'))
    return False


def native_target(repo):
    target = git(repo, 'config', '--get', 'legend.nativeTestingRef', missing_ok=True)
    if not target or not target.startswith('refs/remotes/origin/'):
        raise SyncSkipped('Native synchronization requires an explicit origin testing ref; no source changed.')
    git(repo, 'check-ref-format', target)
    return target


def native_editor_or_build_active():
    result = subprocess.run(['ps', '-axo', 'comm='], text=True, capture_output=True, timeout=10)
    if result.returncode:
        raise SyncSkipped('Cannot verify native editor activity; no source changed.')
    commands = [line.strip().lower() for line in result.stdout.splitlines()]
    names = {Path(line).name for line in commands}
    # Unsaved IDE buffers cannot be safely merged by Git. Wait until editors
    # close instead of overwriting them or guessing from CPU utilization.
    if names & {'xcode', 'studio',
                'xcodebuild', 'swift-frontend', 'swiftc', 'ibtool', 'actool', 'clang', 'gradle', 'gradlew',
                'agentportal', 'clientapp', 'parfaitapp', 'protectwebsite', 'legend', 'git'}:
        return True
    if any('/android studio.app/' in command for command in commands):
        return True
    if 'dotnet' in names and active_dotnet_process():
        return True
    if 'java' in names:
        result = subprocess.run(['ps', '-axo', 'args='], text=True, capture_output=True, timeout=10)
        if result.returncode:
            raise SyncSkipped('Cannot verify Gradle activity; no source changed.')
        return any(marker in result.stdout for marker in ('org.gradle.wrapper.GradleWrapperMain', 'org.gradle.launcher.GradleMain'))
    return False


def assert_ready(repo, expected_head=None, process_probe=active_build_or_app, native=False,
                 expected_target=None, expected_branch=None):
    target = native_target(repo) if native else 'refs/remotes/origin/Legend/fast-track-release'
    branch = git(repo, 'symbolic-ref', '--quiet', '--short', 'HEAD')
    if expected_target is not None and target != expected_target:
        raise SyncSkipped('The configured target changed during synchronization; no source update attempted.')
    if expected_branch is not None and branch != expected_branch:
        raise SyncSkipped('The checkout branch changed during synchronization; no source update attempted.')
    if not native and branch != 'Legend/fast-track-release':
        raise SyncSkipped('This is a working branch; automatic sync applies only to Legend/fast-track-release.')
    if git(repo, 'rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{upstream}') != target.removeprefix('refs/remotes/'):
        raise SyncSkipped('The checkout must track its intended origin ref; no source update attempted.')
    if not native and git(repo, 'status', '--porcelain=v1', '--untracked-files=all'):
        raise SyncSkipped('Local changes are present (including native build numbers); preserve/review them before syncing.')
    git_dir = Path(git(repo, 'rev-parse', '--absolute-git-dir'))
    if any((git_dir / name).exists() for name in ('MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REVERT_HEAD', 'rebase-apply', 'rebase-merge', 'index.lock')):
        raise SyncSkipped('A Git operation is in progress; no source update attempted.')
    if process_probe():
        raise SyncSkipped('An app or build is running; stop it before updating source.')
    head = git(repo, 'rev-parse', 'HEAD')
    if expected_head is not None and head != expected_head:
        raise SyncSkipped('The checkout changed during fetch; no source update attempted.')
    return head, git_dir


def sync_checkout(repo, process_probe=None, native=False):
    repo = Path(git(repo, 'rev-parse', '--show-toplevel'))
    target = native_target(repo) if native else 'refs/remotes/origin/Legend/fast-track-release'
    branch = git(repo, 'symbolic-ref', '--quiet', '--short', 'HEAD')
    process_probe = process_probe or (native_editor_or_build_active if native else active_build_or_app)
    head, git_dir = assert_ready(repo, process_probe=process_probe, native=native,
                                expected_target=target, expected_branch=branch)
    with (git_dir / 'legend-published-sync.lock').open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise SyncSkipped('Another local sync is running; no source update attempted.') from error
        git(repo, 'fetch', '--no-tags', '--no-recurse-submodules', 'origin',
            'refs/heads/' + target.removeprefix('refs/remotes/origin/') + ':' + target)
        assert_ready(repo, expected_head=head, process_probe=process_probe, native=native,
                     expected_target=target, expected_branch=branch)
        published = git(repo, 'rev-parse', target)
        if head == published:
            return ('Native testing' if native else 'Published approved') + ' checkout is current (' + head[:12] + ').'
        try:
            git(repo, 'merge-base', '--is-ancestor', head, published)
        except SyncSkipped as error:
            raise SyncSkipped('The local checkout is ahead or divergent; no merge/reset/rebase was attempted.') from error
        # Explicitly protect ignored local configuration if a published path collides with it.
        git(repo, 'merge', '--ff-only', '--no-autostash', '--no-overwrite-ignore', published)
        return 'Updated to ' + ('native testing ' if native else 'published approved ') + published[:12] + '.'


def check_native_checkout(repo, provenance_output=None):
    """Verify the build source against the existing locally known testing ref.

    This never fetches, updates the index, modifies source, or rejects current
    uncommitted work. The explicit shared Git setting selects the intended test
    branch; without it, locally known published approved remains the target.
    """
    repo = Path(git(repo, 'rev-parse', '--show-toplevel')).resolve()
    output = None
    if provenance_output is not None:
        output = Path(provenance_output).resolve()
        git_dir = Path(git(repo, 'rev-parse', '--absolute-git-dir')).resolve()
        common = Path(git(repo, 'rev-parse', '--git-common-dir'))
        common = (repo / common).resolve() if not common.is_absolute() else common.resolve()
        protected = (repo, git_dir, common, common.parent) if common.name == '.git' else (repo, git_dir, common)
        if any(output == root or root in output.parents for root in protected):
            raise SyncSkipped('Native provenance must be a build artifact outside the source checkout and Git directories.')
        # Scheme pre-actions are not guaranteed build vetoes. Remove the prior
        # artifact before validation so a later bundle phase cannot copy stale identity.
        output.unlink(missing_ok=True)
    configured = git(repo, 'config', '--get', 'legend.nativeTestingRef', missing_ok=True)
    workflow_candidate = configured is None and os.environ.get('GITHUB_ACTIONS') == 'true'
    target = configured if configured is not None else 'refs/remotes/origin/legend/approved-changes'
    try:
        head = git(repo, 'rev-parse', '--verify', 'HEAD^{commit}')
        branch = git(repo, 'rev-parse', '--abbrev-ref', 'HEAD')
        if workflow_candidate:
            published = os.environ.get('GITHUB_SHA', '')
            workspace = os.environ.get('GITHUB_WORKSPACE', '')
            if not re.fullmatch(r'[0-9a-f]{40}', published) or not workspace or Path(workspace).resolve() != repo:
                raise SyncSkipped('GitHub workflow candidate identity is invalid.')
            if head != published:
                raise SyncSkipped('GitHub workflow HEAD does not match its exact candidate SHA.')
        else:
            git(repo, 'check-ref-format', target)
            published = git(repo, 'rev-parse', '--verify', target + '^{commit}')
    except SyncSkipped as error:
        if workflow_candidate:
            raise SyncSkipped('Cannot verify native checkout against the existing GitHub workflow: require exact GITHUB_SHA/HEAD and matching GITHUB_WORKSPACE; no source was changed.') from error
        raise SyncSkipped('Cannot verify native checkout: the configured local testing ref is missing or invalid. '
                          'Fetch/update the intended ref outside the build, then retry. No source was changed.') from error
    authority = 'workflow=GITHUB_SHA' if workflow_candidate else f'testingRef={target}'
    context = f'checkout={repo}; branch={branch}; HEAD={head}; {authority}; target={published}'
    try:
        git(repo, 'merge-base', '--is-ancestor', published, head)
    except SyncSkipped as error:
        raise SyncSkipped('Stale or divergent native checkout: ' + context +
                          '. Open the current test checkout or reconcile it outside the build; no automatic overwrite/fetch was attempted.') from error
    dirty = git(repo, 'status', '--porcelain=v1', '--untracked-files=all')
    if git(repo, 'rev-parse', 'HEAD') != head or (not workflow_candidate and git(repo, 'rev-parse', '--verify', target + '^{commit}') != published):
        raise SyncSkipped('Checkout or testing ref changed during verification; retry after the source update completes.')
    if output is not None:
        output.parent.mkdir(parents=True, exist_ok=True)
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', dir=output.parent,
                                             prefix='.legend-provenance-', delete=False) as artifact:
                temporary = Path(artifact.name)
                json.dump({'schemaVersion': 1, 'gitCommitHash': head,
                           'hasLocalChanges': bool(dirty)}, artifact, separators=(',', ':'))
                artifact.write('\n')
                artifact.flush()
                os.fsync(artifact.fileno())
            os.replace(temporary, output)
        finally:
            if temporary is not None:
                temporary.unlink(missing_ok=True)
    verified = 'existing workflow candidate' if workflow_candidate else 'locally known ref'
    return (context + f'; localChanges={len(dirty.splitlines())}. '
            f'Verified against the {verified}; no remote refresh or source mutation occurs during builds.')


def workspace_uses_native_target(repo):
    # An explicit setting, including an invalid/empty one, must never silently
    # fall back to the approved branch. The existing validator reports invalid settings.
    return git(repo, 'config', '--get', 'legend.nativeTestingRef', missing_ok=True) is not None


def sync_status(repo, native=False, process_probe=None):
    """Read-only readiness, not a claim that the checkout matches remote GitHub."""
    repo = Path(git(repo, 'rev-parse', '--show-toplevel'))
    process_probe = process_probe or (native_editor_or_build_active if native else active_build_or_app)
    try:
        head, _ = assert_ready(repo, process_probe=process_probe, native=native)
        return {'status': 'ready', 'mode': 'configured-native' if native else 'approved',
                'head': head, 'target': native_target(repo) if native else 'refs/remotes/origin/Legend/fast-track-release',
                'remoteFreshness': 'not_checked'}
    except SyncSkipped as error:
        return {'status': 'blocked', 'mode': 'configured-native' if native else 'approved',
                'reason': str(error), 'remoteFreshness': 'not_checked'}


# Session continuity uses native Git and the existing conservative transport wrapper.
# It is not a release authority; acceptance and storage durability are external facts.
CANONICAL_REMOTES = {
    'https://github.com/MYLEGND/masterapp.git', 'https://github.com/MYLEGND/masterapp',
    'git@github.com:MYLEGND/masterapp.git', 'ssh://git@github.com/MYLEGND/masterapp.git',
}


def session_identity(repo, branch):
    git(repo, 'check-ref-format', 'refs/heads/' + branch)
    if not branch.startswith('isolation/'):
        raise SyncSkipped('Session continuity requires an explicitly authorized isolation branch.')
    for direction in ((), ('--push',)):
        urls = git(repo, 'remote', 'get-url', *direction, '--all', 'origin').splitlines()
        if len(urls) != 1 or urls[0] not in CANONICAL_REMOTES:
            raise SyncSkipped('Origin must have one credential-free canonical MYLEGND/masterapp fetch/push destination.')
    if git(repo, 'symbolic-ref', '--quiet', '--short', 'HEAD') != branch:
        raise SyncSkipped('Wrong working branch; select the authorized existing branch without discarding work.')
    if git(repo, 'rev-parse', '--is-shallow-repository') != 'false':
        raise SyncSkipped('Full ancestry is required; shallow history cannot establish preservation.')
    directory = Path(git(repo, 'rev-parse', '--absolute-git-dir'))
    if any((directory / name).exists() for name in
           ('MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REVERT_HEAD', 'rebase-apply', 'rebase-merge', 'index.lock')):
        raise SyncSkipped('A Git operation is in progress; preserve the workspace and finish it explicitly.')


def exact_commit(repo, value):
    if not re.fullmatch(r'[0-9a-f]{40}', value or ''):
        raise SyncSkipped('An exact full accepted commit SHA is required.')
    if git(repo, 'rev-parse', '--verify', value + '^{commit}') != value:
        raise SyncSkipped('Accepted commit is unavailable.')
    return value


def ancestor(repo, older, newer):
    return git(repo, 'merge-base', '--is-ancestor', older, newer, missing_ok=True) is not None


def remote_head(repo, branch):
    lines = git(repo, 'ls-remote', '--exit-code', 'origin', 'refs/heads/' + branch).splitlines()
    if len(lines) != 1 or lines[0].split()[1] != 'refs/heads/' + branch:
        raise SyncSkipped('The exact existing remote branch could not be identified.')
    return lines[0].split()[0]


def session_snapshot(repo, branch, base):
    session_identity(repo, branch)
    head = git(repo, 'rev-parse', 'HEAD')
    remote = remote_head(repo, branch)
    # Fetch original objects only; do not rewrite local or tracking branches.
    git(repo, 'fetch', '--no-tags', '--no-recurse-submodules', 'origin', 'refs/heads/' + branch)
    if git(repo, 'rev-parse', 'FETCH_HEAD') != remote or remote_head(repo, branch) != remote:
        raise SyncSkipped('Remote moved during inspection; repeat preflight.')
    exact_commit(repo, base)
    if not ancestor(repo, base, head) or not ancestor(repo, base, remote):
        raise SyncSkipped('Accepted prior ancestry is missing from local or remote history; preserve both and investigate.')
    local_only = git(repo, 'rev-list', remote + '..' + head).splitlines()
    remote_only = git(repo, 'rev-list', head + '..' + remote).splitlines()
    dirty = git(repo, 'status', '--porcelain=v1', '--untracked-files=all')
    state = 'diverged' if local_only and remote_only else 'ahead' if local_only else 'behind' if remote_only else 'current'
    result = dict(head=head, remote=remote, state=state, localOnly=local_only,
                  remoteOnly=remote_only, uncommitted=bool(dirty))
    print(json.dumps(result, sort_keys=True))
    session_identity(repo, branch)
    if git(repo, 'rev-parse', 'HEAD') != head:
        raise SyncSkipped('Local HEAD changed during inspection.')
    if state == 'diverged':
        raise SyncSkipped('Diverged history: exact local-only and remote-only SHAs reported above. No automatic reconciliation.')
    return result


def bundle_digest(path):
    with Path(path).open('rb') as source:
        digest = hashlib.sha256()
        for block in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def verify_bundle(repo, path, digest, branch, head, base, *, implementation=None, required_commits=()):
    if not Path(path).is_file() or bundle_digest(path) != digest:
        raise SyncSkipped('Bundle missing or SHA-256 mismatch; no restoration attempted.')
    git(repo, 'bundle', 'verify', str(path))
    if git(repo, 'bundle', 'list-heads', str(path)) != head + ' refs/heads/' + branch:
        raise SyncSkipped('Bundle must contain the exact accepted branch identity only.')
    # A full bundle must restore into an empty repository, without local prerequisites.
    with tempfile.TemporaryDirectory(prefix='legend-bundle-verify-') as scratch:
        git(scratch, 'init', '--bare')
        git(scratch, 'fetch', '--no-tags', str(Path(path).resolve()), 'refs/heads/' + branch)
        exact_commit(scratch, head)
        exact_commit(scratch, base)
        if not ancestor(scratch, base, head):
            raise SyncSkipped('Bundle does not preserve accepted ancestry.')
        if implementation is not None:
            exact_commit(scratch, implementation)
            if not ancestor(scratch, implementation, head):
                raise SyncSkipped('Bundle does not preserve accepted implementation ancestry.')
        for required in required_commits:
            exact_commit(scratch, required)
            if not ancestor(scratch, required, head):
                raise SyncSkipped('Bundle does not preserve required checkpoint ancestry.')
        git(scratch, 'fsck', '--strict', '--no-reflogs', '--no-dangling', head)


def export_session(repo, branch, head, base, destination, external_confirmed):
    if destination is None:
        raise SyncSkipped('BLOCKED: no persistent recovery storage supplied. Preserve the original workspace.')
    destination = Path(destination).resolve()
    root = Path(git(repo, 'rev-parse', '--show-toplevel')).resolve()
    temporary_roots = (Path('/tmp').resolve(), Path('/private/tmp'), Path(tempfile.gettempdir()).resolve())
    if destination == root or root in destination.parents or any(p == destination or p in destination.parents for p in temporary_roots):
        raise SyncSkipped('BLOCKED: recovery destination is inside the workspace or temporary storage.')
    if not external_confirmed or not destination.is_dir():
        raise SyncSkipped('BLOCKED: require an existing externally accessible persistent directory and explicit storage confirmation.')
    if git(repo, 'rev-parse', 'refs/heads/' + branch) != head:
        raise SyncSkipped('Branch moved before recovery export.')
    # A fresh subdirectory prevents overwriting an existing recovery artifact.
    output = Path(tempfile.mkdtemp(prefix='legend-recovery-', dir=destination)) / 'LEGEND.bundle'
    git(repo, 'bundle', 'create', str(output), 'refs/heads/' + branch)
    digest = bundle_digest(output)
    verify_bundle(repo, output, digest, branch, head, base)
    with output.open('rb') as artifact:
        os.fsync(artifact.fileno())
    with output.with_suffix('.bundle.sha256').open('w') as checksum:
        checksum.write(digest + '  LEGEND.bundle\n')
        checksum.flush()
        os.fsync(checksum.fileno())
    for directory in (output.parent, destination):
        descriptor = os.open(directory, os.O_RDONLY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
    print(json.dumps(dict(recoveryBundle=str(output), sha256=digest, head=head,
                         storage='operator-confirmed persistent external storage; full restore verified',
                         coverage='committed branch only; excludes uncommitted, ignored and editor-buffer work')))
    return output


def stage_local_session(repo, args):
    """Transport preparation only: never satisfy a durable-recovery gate."""
    head, base = exact_commit(repo, args.accepted), exact_commit(repo, args.base)
    implementation = exact_commit(repo, args.implementation)
    required = [exact_commit(repo, value) for value in args.required_commit]
    if not required or len(set(required)) != len(required):
        raise SyncSkipped('Exact distinct previously accepted checkpoint commits required.')
    if (git(repo, 'rev-parse', 'HEAD') != head
        or git(repo, 'status', '--porcelain=v1', '--untracked-files=all')
        or not ancestor(repo, base, head) or not ancestor(repo, implementation, head)
        or any(not ancestor(repo, value, head) for value in required)):
        raise SyncSkipped('Staging requires clean exact HEAD and accepted base/implementation ancestry.')
    codespace = os.environ.get('CODESPACE_NAME', '')
    if os.environ.get('CODESPACES') != 'true' or not re.fullmatch(r'[a-z0-9-]+', codespace):
        raise SyncSkipped('Staged transfer requires the authenticated existing Codespace identity.')
    directory = Path(tempfile.mkdtemp(prefix='STAGED_LOCAL_NOT_DURABLE-'))
    output = directory / 'LEGEND.bundle'
    git(repo, 'bundle', 'create', str(output), 'refs/heads/' + args.branch)
    digest = bundle_digest(output)
    verify_bundle(repo, output, digest, args.branch, head, base, implementation=implementation, required_commits=required)
    script = directory / 'sync-published-checkout.py'
    script.write_bytes(Path(__file__).read_bytes())
    (directory / 'LEGEND.bundle.sha256').write_text(digest + '  LEGEND.bundle\n')
    if git(repo, 'rev-parse', 'HEAD') != head or git(repo, 'status', '--porcelain=v1', '--untracked-files=all'):
        raise SyncSkipped('Checkout changed while staging; retain workspace and repeat exact review.')
    result = dict(state='STAGED_LOCAL_NOT_DURABLE', durable=False, continuationAllowed=False,
        recoveryOnly=True, remotePreserved=False, codespace=codespace, stagedDirectory=str(directory),
        recoveryBundle=str(output), sha256=digest, verifierSha256=bundle_digest(script),
        branch=args.branch, head=head, base=base, implementation=implementation, requiredCommits=required,
        coverage='committed branch only; excludes uncommitted, ignored and editor-buffer work')
    print(json.dumps(result, sort_keys=True))
    return result


def external_gh(*args):
    """Capture authenticated transport output; never echo credentials/errors."""
    try:
        result = subprocess.run(['gh', *args], text=True, capture_output=True,
            timeout=600, env={**os.environ, 'GH_HOST':'github.com', 'GH_PROMPT_DISABLED':'1'})
    except (OSError, subprocess.TimeoutExpired) as error:
        raise SyncSkipped('Authenticated external transfer unavailable; retain original Codespace.') from error
    if result.returncode:
        raise SyncSkipped('Authenticated external transfer failed; retain original Codespace.')
    return result.stdout


def verify_external_session(args):
    """Independent Mac copy/restore verification, not remote Git preservation."""
    if (sys.platform != 'darwin' or os.environ.get('CODESPACES') or os.environ.get('CODESPACE_NAME')
        or os.environ.get('GITHUB_ACTIONS') == 'true'):
        raise SyncSkipped('External verification requires the independent Mac outside Codespaces/Actions.')
    if (not re.fullmatch(r'[a-z0-9-]+', args.codespace or '')
        or not re.fullmatch(r'[a-f0-9]{64}', args.sha256 or '')
        or not re.fullmatch(r'[a-f0-9]{64}', args.verifier_sha256 or '')
        or any(not re.fullmatch(r'[a-f0-9]{40}', value or '') for value in
            (args.accepted, args.base, args.implementation, *args.required_commit))
        or not args.required_commit or len(set(args.required_commit)) != len(args.required_commit)):
        raise SyncSkipped('Independent exact Codespace, bundle/script digests and commit identities required.')
    git(Path.cwd(), 'check-ref-format', 'refs/heads/' + args.branch)
    if not args.branch.startswith('isolation/'):
        raise SyncSkipped('External recovery requires the exact authorized isolation branch.')
    source = Path(args.staged_dir or '')
    if (not source.is_absolute() or '..' in source.parts
        or not re.fullmatch(r'STAGED_LOCAL_NOT_DURABLE-[A-Za-z0-9_-]+', source.name)):
        raise SyncSkipped('Exact absolute staged transfer directory required.')
    if bundle_digest(Path(__file__)) != args.verifier_sha256:
        raise SyncSkipped('Canonical verifier script digest mismatch.')
    if args.recovery_dir is None or not args.external_storage_confirmed:
        raise SyncSkipped('Existing persistent Mac destination and explicit retention attestation required.')
    supplied = Path(args.recovery_dir).expanduser()
    destination = supplied.resolve()
    temporary_roots = (Path('/tmp').resolve(), Path('/private/tmp'), Path(tempfile.gettempdir()).resolve())
    if (supplied.is_symlink() or not destination.is_dir()
        or any(p == destination or p in destination.parents for p in temporary_roots)
        or Path('/workspaces') == destination or Path('/workspaces') in destination.parents):
        raise SyncSkipped('External destination cannot be temporary, a symlink or a Codespace workspace.')
    external_gh('auth', 'status', '--hostname', 'github.com')
    try:
        user = json.loads(external_gh('api', '--hostname', 'github.com', 'user'))
        machine = json.loads(external_gh('api', '--hostname', 'github.com', 'user/codespaces/' + args.codespace))
    except (ValueError, TypeError) as error:
        raise SyncSkipped('Authenticated external transfer identity unavailable.') from error
    if (not isinstance(user, dict) or not isinstance(machine, dict)
        or not re.fullmatch(r'[A-Za-z0-9-]+', str(user.get('login', '')))
        or not isinstance(machine.get('repository'), dict) or not isinstance(machine.get('owner'), dict)
        or machine.get('name') != args.codespace or machine.get('state') != 'Available'
        or machine.get('repository', {}).get('full_name') != 'MYLEGND/masterapp'
        or machine.get('owner', {}).get('login') != user['login']):
        raise SyncSkipped('Authenticated Codespace ownership/repository identity mismatch.')
    output = Path(tempfile.mkdtemp(prefix='legend-external-recovery-', dir=destination))
    bundle = output / 'LEGEND.bundle'
    for filename in ('LEGEND.bundle', 'LEGEND.bundle.sha256'):
        external_gh('codespace', 'cp', '-c', args.codespace,
            'remote:' + str(source / filename), str(output / filename))
    if (bundle.is_symlink() or not bundle.is_file()
        or (output / 'LEGEND.bundle.sha256').is_symlink()
        or (output / 'LEGEND.bundle.sha256').read_text() != args.sha256 + '  LEGEND.bundle\n'):
        raise SyncSkipped('External copied bundle/checksum identity mismatch.')
    # The verification repository is independent and initially contains no objects.
    with tempfile.TemporaryDirectory(prefix='legend-external-empty-') as temporary:
        git(temporary, 'init', '--bare')
        verify_bundle(temporary, bundle, args.sha256, args.branch, args.accepted,
            args.base, implementation=args.implementation, required_commits=args.required_commit)
    (output / 'sync-published-checkout.py').write_bytes(Path(__file__).read_bytes())
    receipt = dict(state='VERIFIED_EXTERNAL', recoveryOnly=True, remotePreserved=False,
        automaticContinuationAllowed=False, codespace=args.codespace, authenticatedLogin=user['login'],
        transfer='authenticated gh codespace cp from canonical MYLEGND/masterapp',
        recoveryBundle=str(bundle), sha256=args.sha256, verifierSha256=args.verifier_sha256,
        branch=args.branch, head=args.accepted, base=args.base, implementation=args.implementation,
        requiredCommits=args.required_commit,
        emptyRepositoryRestore=True, ancestryVerified=True,
        storage='operator-attested persistent Mac directory; retention/replication not machine-proven',
        coverage='committed branch only; excludes uncommitted, ignored and editor-buffer work')
    for path in output.iterdir():
        with path.open('rb') as stream:
            os.fsync(stream.fileno())
    for path in (output, destination):
        descriptor = os.open(path, os.O_RDONLY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
    pending = output / '.external-verification.pending'
    final = output / 'external-verification.json'
    try:
        with pending.open('x') as stream:
            stream.write(json.dumps(receipt, sort_keys=True) + '\n')
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(pending, final)
        descriptor = os.open(output, os.O_RDONLY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
    except OSError:
        pending.unlink(missing_ok=True)
        final.unlink(missing_ok=True)
        raise
    print(json.dumps(receipt, sort_keys=True))
    return receipt


def session_fast_forward(repo, branch, expected, target):
    if active_build_or_app():
        raise SyncSkipped('Close active apps/builds before explicit fast-forward recovery.')
    session_identity(repo, branch)
    if git(repo, 'rev-parse', 'HEAD') != expected or git(repo, 'status', '--porcelain=v1', '--untracked-files=all'):
        raise SyncSkipped('Checkout changed before fast-forward; preserve work and repeat preflight.')
    git(repo, 'merge', '--ff-only', '--no-autostash', '--no-overwrite-ignore', target)
    session_identity(repo, branch)
    if git(repo, 'rev-parse', 'HEAD') != target:
        raise SyncSkipped('Checkout changed during recovery; inspect exact identities before proceeding.')


def validate_completion(repo, args):
    if not args.review_accepted or not args.authorize_push:
        raise SyncSkipped('Completion requires independent acceptance and explicit push authorization; never infer either from tests.')
    head = exact_commit(repo, args.accepted)
    base = exact_commit(repo, args.base)
    if head != git(repo, 'rev-parse', 'HEAD') or not ancestor(repo, base, head) or not ancestor(repo, exact_commit(repo, args.implementation), head):
        raise SyncSkipped('Reviewed implementation/checkpoint identity or prior ancestry mismatch.')
    if git(repo, 'status', '--porcelain=v1', '--untracked-files=all'):
        raise SyncSkipped('Uncommitted work exists; preserve it explicitly before completion.')
    changed = set(git(repo, 'diff', '--name-only', '--no-renames', base, head).splitlines())
    if not args.scope or changed != set(args.scope):
        raise SyncSkipped('Affected files do not exactly match the explicitly reviewed --scope paths.')
    return head


def cloud_git_bytes(repo, *args):
    result = subprocess.run(['git', '-C', str(repo), *args], capture_output=True,
                            timeout=120, env={**os.environ, 'GIT_TERMINAL_PROMPT': '0', 'GIT_OPTIONAL_LOCKS': '0'})
    if result.returncode:
        raise SyncSkipped('Recovery Git operation failed; original files retained.')
    return result.stdout


def cloud_safe_path(value):
    from pathlib import PurePosixPath
    p = PurePosixPath(value)
    if not value or p.is_absolute() or '..' in p.parts or '.git' in p.parts or str(p) != value or '\\' in value:
        raise SyncSkipped('Unsafe recovery path; no files restored.')
    if any(part.lower() in {'.env', '.azure', '.ssh', '.config', '.npmrc', '.pypirc'} for part in p.parts) or p.suffix.lower() in {'.key', '.pfx', '.p12', '.pem'}:
        raise SyncSkipped('Credential-like path requires a separate secure preservation process.')
    return p


def cloud_state(repo, include):
    import base64
    if b'160000 ' in cloud_git_bytes(repo, 'ls-files', '--stage'):
        raise SyncSkipped('Submodule content needs explicit separate preservation; cloud capture refused.')
    head = git(repo, 'rev-parse', 'HEAD')
    index = cloud_git_bytes(repo, 'diff', '--cached', '--binary', '--full-index', '--no-ext-diff', '--no-textconv', '--no-renames', head)
    work = cloud_git_bytes(repo, 'diff', '--binary', '--full-index', '--no-ext-diff', '--no-textconv', '--no-renames')
    untracked = set(filter(None, cloud_git_bytes(repo, 'ls-files', '--others', '--exclude-standard', '-z').decode('utf-8').split('\0')))
    permitted = set(include or [])
    if untracked - permitted:
        raise SyncSkipped('Unclassified untracked files: supply each reviewed --capture-path; nothing uploaded.')
    extra = {}
    root = Path(repo).resolve()
    for name in sorted(permitted):
        relative = cloud_safe_path(name)
        path = root / relative
        if path.is_symlink() or not path.is_file() or root not in path.resolve().parents:
            raise SyncSkipped('Capture path must be an existing regular file inside the checkout.')
        data = path.read_bytes()
        if len(data) > 16 * 1024 * 1024:
            raise SyncSkipped('Explicit capture file exceeds reviewed 16 MiB limit.')
        extra[name] = dict(data=base64.b64encode(data).decode(), executable=bool(path.stat().st_mode & 0o111))
    for data in [index, work] + [base64.b64decode(x['data']) for x in extra.values()]:
        if re.search(rb'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|AccountKey=[A-Za-z0-9+/]{20}|gh[pousr]_[A-Za-z0-9]{30}', data):
            raise SyncSkipped('Potential secret in pending capture; no cloud upload. Preserve securely and review.')
    return dict(head=head, index=base64.b64encode(index).decode(), work=base64.b64encode(work).decode(), extra=extra)


def cloud_restore_archive(path, digest, branch, base, head):
    import base64
    import zipfile
    if bundle_digest(path) != digest:
        raise SyncSkipped('Cloud archive SHA-256 mismatch; restoration refused.')
    with tempfile.TemporaryDirectory(prefix='legend-cloud-restore-') as directory:
        root = Path(directory)
        with zipfile.ZipFile(path) as archive:
            names = archive.namelist()
            if len(names) != len(set(names)) or any(x not in {'LEGEND.bundle','state.json'} and not re.fullmatch(r'lfs/[0-9a-f]{64}',x) for x in names):
                raise SyncSkipped('Unexpected recovery archive members.')
            if any(x.file_size > 512 * 1024 * 1024 for x in archive.infolist()):
                raise SyncSkipped('Recovery archive exceeds verification limits.')
            manifest = json.loads(archive.read('state.json'))
            if (manifest.get('schema'), manifest.get('branch'), manifest.get('base'), manifest.get('state', {}).get('head')) != (1, branch, base, head):
                raise SyncSkipped('Recovery identities do not match independently supplied identities.')
            bundle = root / 'LEGEND.bundle'
            with archive.open('LEGEND.bundle') as source, bundle.open('wb') as target:
                import shutil
                shutil.copyfileobj(source, target)
        repo = root / 'fixture'
        repo.mkdir()
        git(repo, 'init')
        for name, value in [('filter.lfs.process',''),('filter.lfs.smudge',''),('filter.lfs.clean',''),('filter.lfs.required','false')]:
            git(repo,'config','--local',name,value)
        verify_bundle(repo, bundle, manifest['bundleSha256'], branch, head, base)
        git(repo, 'fetch', '--no-tags', str(bundle), 'refs/heads/' + branch)
        git(repo, '-c', 'core.hooksPath=/dev/null', 'checkout', '--detach', head)
        state = manifest['state']
        for key, options in [('index', ['--index']), ('work', [])]:
            patch = base64.b64decode(state[key], validate=True)
            if patch:
                patchfile = root / (key + '.patch')
                patchfile.write_bytes(patch)
                git(repo, 'apply', '--binary', *options, str(patchfile))
        for name, entry in state['extra'].items():
            relative = cloud_safe_path(name)
            target = repo / relative
            # Never follow a symlink restored from committed objects or a patch.
            if any(x.is_symlink() for x in [target, *target.parents]) or target.exists():
                raise SyncSkipped('Recovery extra file collides with restored content.')
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(base64.b64decode(entry['data'], validate=True))
            target.chmod(0o700 if entry['executable'] else 0o600)
        restored = cloud_state(repo, list(state['extra']))
        if restored != state:
            raise SyncSkipped('Restored index/worktree/untracked state differs from captured bytes.')
        lfs = manifest.get('lfs', {})
        if sorted(x for x in names if x.startswith('lfs/')) != sorted('lfs/'+oid for oid in lfs):
            raise SyncSkipped('LFS inventory mismatch.')
        with zipfile.ZipFile(path) as archive:
            for oid, size in lfs.items():
                target=repo/'.git/lfs/objects'/oid[:2]/oid[2:4]/oid
                target.parent.mkdir(parents=True,exist_ok=True)
                with archive.open('lfs/'+oid) as source,target.open('xb') as out:
                    shutil.copyfileobj(source,out)
                if target.stat().st_size != size or bundle_digest(target) != oid:
                    raise SyncSkipped('LFS object integrity failure.')
        hydrated=0
        for raw in cloud_git_bytes(repo,'ls-files','-z').split(b'\0'):
            if not raw: continue
            target=repo/raw.decode('utf-8')
            if target.is_symlink() or not target.is_file() or target.stat().st_size>1024: continue
            data=target.read_bytes()
            if not data.startswith(b'version https://git-lfs.github.com/spec/v1\n'): continue
            match=re.search(rb'oid sha256:([0-9a-f]{64})\nsize ([0-9]+)',data)
            if not match: raise SyncSkipped('Invalid LFS pointer.')
            oid=match[1].decode()
            if oid not in lfs or lfs[oid] != int(match[2]): raise SyncSkipped('Missing LFS payload.')
            shutil.copyfile(repo/'.git/lfs/objects'/oid[:2]/oid[2:4]/oid,target)
            if bundle_digest(target)!=oid: raise SyncSkipped('LFS hydration mismatch.')
            hydrated+=1
        git(repo, 'fsck', '--strict', '--no-reflogs', '--no-dangling', head)
        return dict(head=head, branch=branch, base=base, sha256=digest,
                    status='FRESH_DISPOSABLE_RESTORATION_VERIFIED', untrackedOrIgnoredFiles=len(state['extra']),
                    staged=bool(state['index']), unstaged=bool(state['work']), lfsObjects=len(lfs), lfsFilesHydrated=hydrated,
                    unsavedMemoryCaptured=False, automaticContinuationAllowed=False)


class RecoveryBlobClient:
    """Certificate-only fixed-destination transport; never uses account keys or redirects."""
    def __init__(self):
        import base64
        import time
        import uuid
        import urllib.request
        import urllib.parse
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, req, fp, code, msg, headers, newurl):
                raise SyncSkipped('Recovery redirect refused.')
        self.open = urllib.request.build_opener(NoRedirect()).open
        self.base = 'https://masterappstorage1221.blob.core.windows.net/legend-engineering-recovery/'
        endpoint = 'https://login.microsoftonline.com/3fd90b17-12b1-4572-8cab-b0ceee317a30/oauth2/v2.0/token'
        client = '1028dcad-214f-4226-9ac3-37cff0f43b39'
        pem = os.environ.pop('LEGEND_RECOVERY_CERTIFICATE_PEM', None)
        if pem is None:
            folder = Path.home() / '.config/legend-recovery'
            key = folder / 'recovery-20261016.key'
            cert = folder / 'recovery-20261016.crt'
            if key.is_symlink() or key.stat().st_mode & 0o077 or folder.stat().st_mode & 0o077:
                raise SyncSkipped('Recovery private key requires protected owner-only storage.')
            pem = key.read_text() + cert.read_text()
        b64 = lambda data: base64.urlsafe_b64encode(data).rstrip(b'=').decode()
        with tempfile.TemporaryDirectory(prefix='legend-recovery-credential-') as d:
            key = Path(d) / 'credential.pem'
            descriptor = os.open(key, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(descriptor, 'w') as f:
                f.write(pem)
            der = subprocess.run(['openssl', 'x509', '-in', str(key), '-outform', 'DER'], capture_output=True, check=True).stdout
            if hashlib.sha256(der).hexdigest() != '7c8be1b8df201142ca1e5e0aea6ba2eddc614fa86d36b2f4dc32f5b01d49630e':
                raise SyncSkipped('Unreviewed recovery certificate; explicit rotation review required.')
            now = int(time.time())
            header = dict(alg='RS256', typ='JWT', x5t=b64(hashlib.sha1(der).digest()))
            claims = dict(aud=endpoint, iss=client, sub=client, jti=str(uuid.uuid4()), nbf=now-30, exp=now+300)
            unsigned = '.'.join(b64(json.dumps(x, separators=(',', ':')).encode()) for x in (header, claims))
            signature = subprocess.run(['openssl', 'dgst', '-sha256', '-sign', str(key)], input=unsigned.encode(), capture_output=True, check=True).stdout
        form = urllib.parse.urlencode(dict(client_id=client, scope='https://storage.azure.com/.default', grant_type='client_credentials', client_assertion_type='urn:ietf:params:oauth:client-assertion-type:jwt-bearer', client_assertion=unsigned+'.'+b64(signature))).encode()
        with self.open(urllib.request.Request(endpoint, data=form), timeout=30) as response:
            self.token = json.load(response)['access_token']

    def transfer(self, name, path, upload=False):
        import urllib.request
        import urllib.parse
        import shutil
        headers = {'Authorization': 'Bearer '+self.token, 'x-ms-version': '2023-11-03'}
        url = self.base + urllib.parse.quote(name, safe='/')
        if upload:
            headers.update({'If-None-Match': '*', 'x-ms-blob-type': 'BlockBlob', 'Content-Length': str(Path(path).stat().st_size)})
            with Path(path).open('rb') as data:
                with self.open(urllib.request.Request(url, data=data, headers=headers, method='PUT'), timeout=300) as response:
                    return response.headers['ETag']
        with self.open(urllib.request.Request(url, headers=headers), timeout=120) as response, Path(path).open('xb') as target:
            shutil.copyfileobj(response, target)


def cloud_export_session(repo, args):
    import uuid
    import zipfile
    from datetime import datetime, timezone
    if os.environ.get('CODESPACE_NAME') != 'bookish-fiesta-97pj77j5vpwj3pjv6':
        raise SyncSkipped('Live cloud capture is restricted to the canonical Codespace.')
    session_identity(repo, args.branch)
    exact_commit(repo, args.base)
    state = cloud_state(repo, args.capture_path)
    if not ancestor(repo, args.base, state['head']):
        raise SyncSkipped('Accepted base is not an ancestor of captured HEAD.')
    with tempfile.TemporaryDirectory(prefix='legend-cloud-capture-') as directory:
        root = Path(directory)
        bundle = root / 'LEGEND.bundle'
        lfs={}
        lfsroot=Path(git(repo,'rev-parse','--absolute-git-dir'))/'lfs/objects'
        attributes=Path(repo)/'.gitattributes'
        if attributes.exists() and b'filter=lfs' in attributes.read_bytes():
            inventory=json.loads(cloud_git_bytes(repo,'lfs','ls-files','--all','--json'))['files']
            for entry in inventory:
                oid=entry['oid']
                obj=lfsroot/oid[:2]/oid[2:4]/oid
                if not obj.is_file() or obj.stat().st_size!=entry['size'] or bundle_digest(obj)!=oid:
                    raise SyncSkipped('Missing or corrupt historical LFS object; retain workspace.')
            for obj in lfsroot.glob('*/*/*'):
                if obj.is_file() and re.fullmatch(r'[0-9a-f]{64}',obj.name):
                    if bundle_digest(obj)!=obj.name: raise SyncSkipped('LFS cache integrity failure.')
                    lfs[obj.name]=obj.stat().st_size
        git(repo, 'bundle', 'create', str(bundle), 'refs/heads/'+args.branch)
        manifest = dict(schema=1, branch=args.branch, base=args.base, state=state, lfs=lfs, bundleSha256=bundle_digest(bundle),
                        created=datetime.now(timezone.utc).isoformat(), coverage='Committed ancestry, index/worktree patches and explicitly reviewed files only; no inaccessible editor memory; ignored files excluded unless listed.')
        if cloud_state(repo, args.capture_path) != state:
            raise SyncSkipped('Files changed during capture; no cloud upload, retain original workspace.')
        archive = root / 'recovery.zip'
        with zipfile.ZipFile(archive, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=1) as output:
            output.write(bundle, 'LEGEND.bundle', compress_type=zipfile.ZIP_STORED)
            for oid in lfs: output.write(lfsroot/oid[:2]/oid[2:4]/oid,'lfs/'+oid)
            output.writestr('state.json', json.dumps(manifest, sort_keys=True))
        if archive.stat().st_size > 1024 * 1024 * 1024:
            raise SyncSkipped('Recovery payload exceeds reviewed single-upload limit.')
        digest = bundle_digest(archive)
        cloud_restore_archive(archive, digest, args.branch, args.base, state['head'])
        client = RecoveryBlobClient()
        prefix = 'continuity-v1/' + str(uuid.uuid4())
        name = prefix + '/recovery.zip'
        # Ambiguous PUT never retries or advances a success receipt. Originals remain.
        etag = client.transfer(name, archive, upload=True)
        downloaded = root / 'readback.zip'
        client.transfer(name, downloaded)
        result = cloud_restore_archive(downloaded, digest, args.branch, args.base, state['head'])
        result.update(archiveBytes=archive.stat().st_size, object=name, etag=etag, independentHostVerified=False, created=manifest['created'])
        verifier = Path(__file__).resolve()
        result['verifierSha256'] = bundle_digest(verifier)
        client.transfer(prefix+'/sync-published-checkout.py', verifier, upload=True)
        receipt = root / 'receipt.json'
        receipt.write_text(json.dumps(result, sort_keys=True, indent=2)+'\n')
        client.transfer(prefix+'/receipt.json', receipt, upload=True)
        receipt_readback = root / 'receipt-readback.json'
        client.transfer(prefix+'/receipt.json', receipt_readback)
        if receipt.read_bytes() != receipt_readback.read_bytes():
            raise SyncSkipped('Cloud receipt readback mismatch; no verified receipt advanced.')
        if cloud_state(repo, args.capture_path) != state:
            raise SyncSkipped('Snapshot preserved but current files advanced; repeat capture before handoff.')
        print(json.dumps(result, sort_keys=True))



def session_command(repo, args):
    branch, base = args.branch, args.base
    if args.session == 'cloud-export':
        cloud_export_session(repo, args)
        return
    session_identity(repo, branch)
    if args.session == 'stage-local':
        stage_local_session(repo, args)
        return
    if args.session == 'restore':
        exact_commit(repo, base)
        verify_bundle(repo, args.bundle or '', args.sha256, branch, args.accepted, base)
        # Import only original objects. FETCH_HEAD keeps recovered work reachable;
        # retain the verified external bundle until remote readback succeeds.
        git(repo, 'fetch', '--no-tags', str(Path(args.bundle).resolve()), 'refs/heads/' + branch)
        current = git(repo, 'rev-parse', 'HEAD')
        if not ancestor(repo, current, args.accepted):
            print(json.dumps(dict(localOnly=git(repo, 'rev-list', args.accepted + '..' + current).splitlines(),
                                  recoveredOnly=git(repo, 'rev-list', current + '..' + args.accepted).splitlines())))
            raise SyncSkipped('Recovered objects imported; local branch preserved. Explicit reconciliation required; retain bundle.')
        session_fast_forward(repo, branch, current, args.accepted)
        print('Restored exact original commits; run remote preflight before implementation.')
        return
    if args.session == 'export':
        head = exact_commit(repo, args.accepted)
        export_session(repo, branch, head, base, args.recovery_dir, args.external_storage_confirmed)
        return
    if args.session == 'finish':
        accepted = validate_completion(repo, args)
        try:
            snapshot = session_snapshot(repo, branch, base)
        except SyncSkipped:
            export_session(repo, branch, accepted, base, args.recovery_dir, args.external_storage_confirmed)
            raise
    else:
        snapshot = session_snapshot(repo, branch, base)
    head = snapshot['head']
    if snapshot['uncommitted']:
        raise SyncSkipped('Uncommitted work exists; preserve/review it explicitly. A Git bundle cannot preserve uncommitted or ignored files.')
    if args.session == 'recover':
        if snapshot['state'] == 'behind':
            session_fast_forward(repo, branch, head, snapshot['remote'])
            print('Fast-forwarded to observed remote checkpoint; rerun preflight.')
            return
    if snapshot['state'] == 'behind':
        raise SyncSkipped('Remote is ahead; use explicit --session recover with a clean idle workspace, then repeat preflight.')
    if args.session != 'finish':
        if snapshot['localOnly']:
            export_session(repo, branch, head, base, args.recovery_dir, args.external_storage_confirmed)
        git(repo, 'push', '--no-follow-tags', '--recurse-submodules=no', '--dry-run', 'origin', head + ':refs/heads/' + branch)
        print('Preflight PASS: native Git transport available. Dry-run is not proof of future push success or branch-policy acceptance.')
        return
    validate_completion(repo, args)
    session_identity(repo, branch)
    if git(repo, 'rev-parse', 'HEAD') != head:
        raise SyncSkipped('Local HEAD changed before push; repeat review/preflight.')
    try:
        if remote_head(repo, branch) != snapshot['remote']:
            raise SyncSkipped('Remote HEAD changed before push; repeat review/preflight.')
    except SyncSkipped:
        export_session(repo, branch, head, base, args.recovery_dir, args.external_storage_confirmed)
        raise
    push_failed = False
    try:
        git(repo, 'push', '--no-follow-tags', '--recurse-submodules=no', 'origin', head + ':refs/heads/' + branch)
    except SyncSkipped:
        push_failed = True
    # Always reconcile an ambiguous write before considering recovery; never retry.
    try:
        published = remote_head(repo, branch)
    except SyncSkipped:
        published = None
    if published != head:
        export_session(repo, branch, head, base, args.recovery_dir, args.external_storage_confirmed)
        raise SyncSkipped('BLOCKED: push/readback did not establish exact remote HEAD. Verified recovery exported; no retry performed.')
    print('PRESERVED: exact GitHub remote HEAD=' + head + (' (ambiguous push reconciled).' if push_failed else '.'))


def session_main(argv):
    parser = argparse.ArgumentParser(description='Explicit engineering continuity through native Git; no deployment or acceptance authority.')
    parser.add_argument('--session', required=True, choices=('preflight', 'recover', 'restore', 'export', 'finish', 'stage-local', 'verify-external', 'cloud-export', 'verify-cloud'))
    parser.add_argument('--repo', type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument('--branch', required=True)
    parser.add_argument('--base', required=True, help='Previously accepted durable checkpoint SHA')
    parser.add_argument('--accepted', help='Exact accepted completion or recovered SHA')
    parser.add_argument('--implementation', help='Exact reviewed implementation SHA')
    parser.add_argument('--scope', action='append', help='One exact reviewed affected file; repeat for every path')
    parser.add_argument('--review-accepted', action='store_true', help='Attest acceptance by the authorized independent reviewer')
    parser.add_argument('--authorize-push', action='store_true', help='Attest explicit Founder authorization for this exact push')
    parser.add_argument('--bundle', type=Path)
    parser.add_argument('--sha256')
    parser.add_argument('--recovery-dir', type=Path)
    parser.add_argument('--external-storage-confirmed', action='store_true', help='Operator attests directory persists outside this execution environment and is accessible from the host/account')
    parser.add_argument('--required-commit', action='append', default=[], help='Exact prior checkpoint to preserve; repeat for each')
    parser.add_argument('--codespace', help='Exact authenticated source Codespace name')
    parser.add_argument('--staged-dir', help='Exact source staging directory reported by stage-local')
    parser.add_argument('--verifier-sha256', help='Independently recorded canonical script SHA-256')
    parser.add_argument('--capture-path', action='append', default=[], help='Exact reviewed untracked/ignored saved file; never credentials')
    args = parser.parse_args(argv)
    if args.session in {'stage-local', 'verify-external'} and (args.authorize_push or args.review_accepted or args.scope or args.bundle is not None):
        parser.error('Transfer modes cannot accept push/review/scope/restore flags')
    if args.session == 'stage-local' and (args.external_storage_confirmed or args.recovery_dir is not None or args.sha256 or args.verifier_sha256 or args.codespace or args.staged_dir):
        parser.error('Staging cannot claim external verification/storage authority')
    if args.session == 'verify-external' and any(arg == '--repo' or arg.startswith('--repo=') for arg in argv):
        parser.error('External verification does not use a repository checkout')
    try:
        if args.session == 'verify-cloud':
            print(json.dumps(cloud_restore_archive(args.bundle, args.sha256, args.branch, args.base, args.accepted), sort_keys=True))
            return 0
        if args.session == 'verify-external':
            verify_external_session(args)
            return 0
        directory = Path(git(args.repo, 'rev-parse', '--absolute-git-dir'))
        with (directory / 'legend-published-sync.lock').open('a') as lock:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError as error:
                raise SyncSkipped('Another continuity/sync operation is running.') from error
            session_command(args.repo, args)
        return 0
    except (SyncSkipped, OSError, subprocess.TimeoutExpired) as error:
        message = str(error) if isinstance(error, SyncSkipped) else 'Filesystem/transport verification failed; preserve original workspace.'
        print('BLOCKED: ' + message, file=sys.stderr)
        return 1


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    if '--session' in argv or any(arg.startswith('--session=') for arg in argv):
        return session_main(argv)
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument('--sync-native', action='store_true', help='Synchronize the configured native tracking checkout when editors/builds are closed; preserve conflicting work.')
    modes.add_argument('--sync-workspace', action='store_true', help='Use the explicit native testing ref when configured; otherwise synchronize the clean Legend/fast-track-release checkout.')
    modes.add_argument('--check-native', action='store_true', help='Fail if this checkout lacks the configured locally known native testing revision; never fetch or mutate source.')
    parser.add_argument('--status', action='store_true', help='Report local readiness only; do not fetch, merge, or write a lock/artifact.')
    parser.add_argument('--strict', action='store_true', help='Return a failing exit status if synchronization is skipped.')
    parser.add_argument('--native-provenance-output', type=Path, help='With --check-native, write verified checkout metadata to a generated build artifact outside source/Git.')
    args = parser.parse_args(argv)
    if args.status and (args.check_native or args.native_provenance_output is not None):
        parser.error('--status is read-only and cannot emit build provenance')
    if args.native_provenance_output is not None and not args.check_native:
        parser.error('--native-provenance-output requires --check-native')
    if args.check_native:
        try:
            print('[LEGEND native checkout] ' + check_native_checkout(Path(__file__).resolve().parent.parent, args.native_provenance_output))
            return 0
        except (SyncSkipped, OSError, subprocess.TimeoutExpired) as error:
            message = str(error) if isinstance(error, SyncSkipped) else 'Native checkout could not be verified.'
            print('error: [LEGEND native checkout] ' + message, file=sys.stderr)
            return 1
    try:
        repo = Path(__file__).resolve().parent.parent
        native = args.sync_native or (args.sync_workspace and workspace_uses_native_target(repo))
        if args.status:
            status = sync_status(repo, native=native)
            print(json.dumps(status, sort_keys=True))
            return 1 if args.strict and status['status'] != 'ready' else 0
        print('[LEGEND sync] ' + sync_checkout(repo, native=native))
        return 0
    except (SyncSkipped, OSError, subprocess.TimeoutExpired) as error:
        message = str(error) if isinstance(error, SyncSkipped) else 'Local sync could not safely complete.'
        print('[LEGEND sync] SKIPPED: ' + message, file=sys.stderr)
        print('[LEGEND sync] Local preview uses existing files; no automatic push or deployment.', file=sys.stderr)
        return 1 if args.strict else 0


if __name__ == '__main__':
    sys.exit(main())
