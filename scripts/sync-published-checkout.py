#!/usr/bin/env python3
"""Conservative launcher sync and read-only native checkout verification."""
import argparse
import fcntl
import json
import os
import re
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


def active_build_or_app():
    result = subprocess.run(['ps', '-axo', 'comm='], text=True, capture_output=True, timeout=10)
    if result.returncode:
        raise SyncSkipped('Could not verify running apps/builds; no source update attempted.')
    names = {Path(line.strip()).name.lower() for line in result.stdout.splitlines()}
    # Conservative across checkouts: do not modify source while an app or build is running.
    if names & {'dotnet', 'agentportal', 'clientapp', 'xcodebuild', 'gradle', 'gradlew', 'legend'}:
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
                'dotnet', 'agentportal', 'clientapp', 'parfaitapp', 'protectwebsite', 'legend', 'git'}:
        return True
    if any('/android studio.app/' in command for command in commands):
        return True
    if 'java' in names:
        result = subprocess.run(['ps', '-axo', 'args='], text=True, capture_output=True, timeout=10)
        if result.returncode:
            raise SyncSkipped('Cannot verify Gradle activity; no source changed.')
        return any(marker in result.stdout for marker in ('org.gradle.wrapper.GradleWrapperMain', 'org.gradle.launcher.GradleMain'))
    return False


def assert_ready(repo, expected_head=None, process_probe=active_build_or_app, native=False,
                 expected_target=None, expected_branch=None):
    target = native_target(repo) if native else 'refs/remotes/origin/production'
    branch = git(repo, 'symbolic-ref', '--quiet', '--short', 'HEAD')
    if expected_target is not None and target != expected_target:
        raise SyncSkipped('The configured target changed during synchronization; no source update attempted.')
    if expected_branch is not None and branch != expected_branch:
        raise SyncSkipped('The checkout branch changed during synchronization; no source update attempted.')
    if not native and branch != 'production':
        raise SyncSkipped('This is a working branch; automatic sync applies only to production.')
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
    target = native_target(repo) if native else 'refs/remotes/origin/production'
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
            return ('Native testing' if native else 'Published production') + ' checkout is current (' + head[:12] + ').'
        try:
            git(repo, 'merge-base', '--is-ancestor', head, published)
        except SyncSkipped as error:
            raise SyncSkipped('The local checkout is ahead or divergent; no merge/reset/rebase was attempted.') from error
        # Explicitly protect ignored local configuration if a published path collides with it.
        git(repo, 'merge', '--ff-only', '--no-autostash', '--no-overwrite-ignore', published)
        return 'Updated to ' + ('native testing ' if native else 'published production ') + published[:12] + '.'


def check_native_checkout(repo, provenance_output=None):
    """Verify the build source against the existing locally known testing ref.

    This never fetches, updates the index, modifies source, or rejects current
    uncommitted work. The explicit shared Git setting selects the intended test
    branch; without it, locally known published production remains the target.
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
    target = configured if configured is not None else 'refs/remotes/origin/production'
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
    # fall back to production. The existing validator reports invalid settings.
    return git(repo, 'config', '--get', 'legend.nativeTestingRef', missing_ok=True) is not None


def sync_status(repo, native=False, process_probe=None):
    """Read-only readiness, not a claim that the checkout matches remote GitHub."""
    repo = Path(git(repo, 'rev-parse', '--show-toplevel'))
    process_probe = process_probe or (native_editor_or_build_active if native else active_build_or_app)
    try:
        head, _ = assert_ready(repo, process_probe=process_probe, native=native)
        return {'status': 'ready', 'mode': 'configured-native' if native else 'production',
                'head': head, 'target': native_target(repo) if native else 'refs/remotes/origin/production',
                'remoteFreshness': 'not_checked'}
    except SyncSkipped as error:
        return {'status': 'blocked', 'mode': 'configured-native' if native else 'production',
                'reason': str(error), 'remoteFreshness': 'not_checked'}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument('--sync-native', action='store_true', help='Synchronize the configured native tracking checkout when editors/builds are closed; preserve conflicting work.')
    modes.add_argument('--sync-workspace', action='store_true', help='Use the explicit native testing ref when configured; otherwise preserve production-only synchronization.')
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
