#!/usr/bin/env python3
"""Conservative pull-only update before an existing local app launcher starts."""
import argparse
import fcntl
import os
from pathlib import Path
import subprocess
import sys


class SyncSkipped(Exception):
    pass


def git(repo, *args):
    try:
        result = subprocess.run(
            ['git', '-C', str(repo), *args], text=True, capture_output=True,
            timeout=60, env={**os.environ, 'GIT_TERMINAL_PROMPT': '0'})
    except (OSError, subprocess.TimeoutExpired) as error:
        raise SyncSkipped('Git could not complete; using the current local checkout.') from error
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


def assert_ready(repo, expected_head=None, process_probe=active_build_or_app):
    if git(repo, 'symbolic-ref', '--quiet', '--short', 'HEAD') != 'production':
        raise SyncSkipped('This is a working branch; automatic sync applies only to production.')
    if git(repo, 'rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{upstream}') != 'origin/production':
        raise SyncSkipped('Production must track origin/production; no source update attempted.')
    if git(repo, 'status', '--porcelain=v1', '--untracked-files=all'):
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


def sync_checkout(repo, process_probe=active_build_or_app):
    repo = Path(git(repo, 'rev-parse', '--show-toplevel'))
    head, git_dir = assert_ready(repo, process_probe=process_probe)
    with (git_dir / 'legend-published-sync.lock').open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise SyncSkipped('Another local sync is running; no source update attempted.') from error
        git(repo, 'fetch', '--no-tags', '--no-recurse-submodules', 'origin',
            'refs/heads/production:refs/remotes/origin/production')
        assert_ready(repo, expected_head=head, process_probe=process_probe)
        published = git(repo, 'rev-parse', 'refs/remotes/origin/production')
        if head == published:
            return 'Published production checkout is current (' + head[:12] + ').'
        try:
            git(repo, 'merge-base', '--is-ancestor', head, published)
        except SyncSkipped as error:
            raise SyncSkipped('Local production is ahead or divergent; no merge/reset/rebase was attempted.') from error
        # Explicitly protect ignored local configuration if a published path collides with it.
        git(repo, 'merge', '--ff-only', '--no-overwrite-ignore', published)
        return 'Updated to published production ' + published[:12] + '.'


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--strict', action='store_true', help='Return a failing exit status if synchronization is skipped.')
    args = parser.parse_args(argv)
    try:
        print('[LEGEND sync] ' + sync_checkout(Path(__file__).resolve().parent.parent))
        return 0
    except (SyncSkipped, OSError, subprocess.TimeoutExpired) as error:
        message = str(error) if isinstance(error, SyncSkipped) else 'Local sync could not safely complete.'
        print('[LEGEND sync] SKIPPED: ' + message, file=sys.stderr)
        print('[LEGEND sync] Local preview uses existing files; no automatic push or deployment.', file=sys.stderr)
        return 1 if args.strict else 0


if __name__ == '__main__':
    sys.exit(main())
