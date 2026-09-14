#!/usr/bin/env python3
"""Conservative launcher sync and read-only native checkout verification."""
import argparse
import fcntl
import os
import re
from pathlib import Path
import subprocess
import sys


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
    names = {Path(line.strip()).name.lower() for line in result.stdout.splitlines()}
    # Unsaved IDE buffers cannot be safely merged by Git. Wait until editors
    # close instead of overwriting them or guessing from CPU utilization.
    if names & {'xcode', 'studio', 'xcodebuild', 'swift-frontend', 'swiftc', 'ibtool', 'actool', 'clang', 'gradle', 'gradlew'}:
        return True
    if 'java' in names:
        result = subprocess.run(['ps', '-axo', 'args='], text=True, capture_output=True, timeout=10)
        if result.returncode:
            raise SyncSkipped('Cannot verify Gradle activity; no source changed.')
        return any(marker in result.stdout for marker in ('org.gradle.wrapper.GradleWrapperMain', 'org.gradle.launcher.GradleMain'))
    return False


def assert_ready(repo, expected_head=None, process_probe=active_build_or_app, native=False):
    target = native_target(repo) if native else 'refs/remotes/origin/production'
    if not native and git(repo, 'symbolic-ref', '--quiet', '--short', 'HEAD') != 'production':
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


def sync_checkout(repo, process_probe=active_build_or_app, native=False):
    repo = Path(git(repo, 'rev-parse', '--show-toplevel'))
    target = native_target(repo) if native else 'refs/remotes/origin/production'
    head, git_dir = assert_ready(repo, process_probe=process_probe, native=native)
    with (git_dir / 'legend-published-sync.lock').open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise SyncSkipped('Another local sync is running; no source update attempted.') from error
        git(repo, 'fetch', '--no-tags', '--no-recurse-submodules', 'origin',
            'refs/heads/' + target.removeprefix('refs/remotes/origin/') + ':' + target)
        assert_ready(repo, expected_head=head, process_probe=process_probe, native=native)
        published = git(repo, 'rev-parse', target)
        if head == published:
            return ('Native testing' if native else 'Published production') + ' checkout is current (' + head[:12] + ').'
        try:
            git(repo, 'merge-base', '--is-ancestor', head, published)
        except SyncSkipped as error:
            raise SyncSkipped('Local production is ahead or divergent; no merge/reset/rebase was attempted.') from error
        # Explicitly protect ignored local configuration if a published path collides with it.
        git(repo, 'merge', '--ff-only', '--no-overwrite-ignore', published)
        return 'Updated to ' + ('native testing ' if native else 'published production ') + published[:12] + '.'


def check_native_checkout(repo):
    """Verify the build source against the existing locally known testing ref.

    This never fetches, updates the index, modifies source, or rejects current
    uncommitted work. The explicit shared Git setting selects the intended test
    branch; without it, locally known published production remains the target.
    """
    repo = Path(git(repo, 'rev-parse', '--show-toplevel')).resolve()
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
    verified = 'existing workflow candidate' if workflow_candidate else 'locally known ref'
    return (context + f'; localChanges={len(dirty.splitlines())}. '
            f'Verified against the {verified}; no remote refresh or source mutation occurs during builds.')


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sync-native', action='store_true', help='Synchronize the configured native tracking checkout when editors/builds are closed; preserve conflicting work.')
    parser.add_argument('--check-native', action='store_true', help='Fail if this checkout lacks the configured locally known native testing revision; never fetch or mutate source.')
    parser.add_argument('--strict', action='store_true', help='Return a failing exit status if synchronization is skipped.')
    args = parser.parse_args(argv)
    if args.check_native:
        try:
            print('[LEGEND native checkout] ' + check_native_checkout(Path(__file__).resolve().parent.parent))
            return 0
        except (SyncSkipped, OSError, subprocess.TimeoutExpired) as error:
            message = str(error) if isinstance(error, SyncSkipped) else 'Native checkout could not be verified.'
            print('error: [LEGEND native checkout] ' + message, file=sys.stderr)
            return 1
    try:
        print('[LEGEND sync] ' + sync_checkout(Path(__file__).resolve().parent.parent,
            process_probe=native_editor_or_build_active if args.sync_native else active_build_or_app, native=args.sync_native))
        return 0
    except (SyncSkipped, OSError, subprocess.TimeoutExpired) as error:
        message = str(error) if isinstance(error, SyncSkipped) else 'Local sync could not safely complete.'
        print('[LEGEND sync] SKIPPED: ' + message, file=sys.stderr)
        print('[LEGEND sync] Local preview uses existing files; no automatic push or deployment.', file=sys.stderr)
        return 1 if args.strict else 0


if __name__ == '__main__':
    sys.exit(main())
