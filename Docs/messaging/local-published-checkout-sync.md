# Local workspace synchronization

`scripts/sync-published-checkout.py` remains the single pull-only synchronization
authority. It fetches the selected GitHub branch and permits only a fast-forward
of the existing checkout. It never pushes, deploys, switches branches, resets,
rebases, cleans, or stashes work. Even a user's `merge.autoStash=true` setting is
overridden with `--no-autostash`.

The existing `AgentPortal/run.sh` and `ClientApp/run.sh` launchers invoke
`--sync-workspace` before starting the application. That mode uses the explicit
`legend.nativeTestingRef` setting when present; without a setting it preserves the
original production-only behavior. An empty or invalid explicit setting is an
error, never permission to fall back to a different branch.

## Existing automatic schedule

The user's existing macOS LaunchAgent,
`~/Library/LaunchAgents/com.mylegnd.native-checkout-sync.plist`, invokes this same
helper with `--sync-native --strict` at login and every 60 seconds. There is no
second daemon, synchronization implementation, or scheduler to install. The
September 18 read-only inspection found this job loaded with `RunAtLoad=true`,
`StartInterval=60`, and output directed to `/dev/null`; its last recorded exit was
1. That exit alone does not identify a particular skip reason.

Automatic native synchronization requires both:

- `legend.nativeTestingRef` is a full `refs/remotes/origin/<branch>` reference.
- The current local branch already tracks that exact origin branch.

The helper keeps that target and local branch pinned across the fetch. If either
changes during synchronization, it refuses to update source. It does not infer
that a newly deployed release is the user's intended test branch. Choosing a new
upstream or checking out a new branch remains an explicit setup action, separate
from this automatic helper. Preserve any unique historical/local branch before
changing the workspace's branch; do not reset it to make synchronization pass.

## Preservation and idle requirements

Xcode, Android Studio, native compiler/build processes, and running .NET builds or
apps block native synchronization before fetching and are checked again before
merging. Idle Java/Gradle daemons do not block; active Gradle wrappers do. An open
Code/Codex application alone is not a blocker. Process-inspection failure blocks
synchronization instead of guessing that the workspace is idle.

Native mode permits nonconflicting local tracked/untracked edits and leaves them
in place. Git refuses incoming changes that conflict with local work, including
ignored configuration collisions. Production-only mode requires a completely
clean tracked/untracked checkout. In-progress Git operations, changing HEAD,
local commits ahead of/divergent from the remote, and concurrent sync attempts
also prevent updates. The existing per-checkout lock coordinates helper runs;
it does not lock arbitrary external editors or Git commands.

When a run skips, the periodic job can retry at the next interval. The startup
launchers print the reason and continue using existing local files; `--strict`
makes skipped synchronization return a failing exit code. Closing the protected
native IDE/build and reconciling conflicting work allows a later automatic run.
A source update does not restart apps or install native binaries.

## Read-only readiness

From the intended checkout:

```sh
python3 -B scripts/sync-published-checkout.py --sync-workspace --status
```

This reports whether local preconditions permit synchronization. It performs no
fetch, merge, source write, lock-file write, or Git configuration change. Its
`remoteFreshness: not_checked` field is intentional: local readiness cannot prove
that the checkout matches current GitHub. Add `--strict` for a nonzero exit when
blocked. The native Xcode/Gradle build checks remain read-only and do not fetch.

Validation: `python3 -B scripts/test-sync-published-checkout.py` uses disposable
real Git repositories to cover fast-forward/current states, local work and
ignored-file preservation, configured autostash refusal, branch/target changes
during fetch, divergence, missing upstreams, blocked processes, and read-only
status. It also retains the build-provenance and native-checkout tests.
