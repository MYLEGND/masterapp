# Local preview and published checkout synchronization

The existing `AgentPortal/run.sh` and `ClientApp/run.sh` launchers first invoke
`scripts/sync-published-checkout.py`, then run the local application normally.
There is one shared pull-only sync implementation and no background daemon,
automatic push, deployment, branch switching, or native artifact replacement.

The helper fetches `origin/production` only when the current branch is
`production`, its upstream is `origin/production`, and staged, unstaged, and
untracked changes are all absent. It checks for an in-progress Git operation
and running applications/builds. After fetching, it checks the checkout again
and permits only a fast-forward. Ignored configuration files are protected from
incoming tracked-file collisions. Concurrent helper invocations use one lock
per checkout. Local commits that are ahead or divergent require manual review.
It never stashes, resets, rebases, cleans, or discards local changes.

A skipped update or connectivity failure prints an explicit status and allows
the launcher to preview the existing local files. `python3
scripts/sync-published-checkout.py --strict` instead exits unsuccessfully when
synchronization cannot safely complete. Do not edit or start another build while
syncing: its lock coordinates these launchers, not arbitrary editors or external
Git commands. Active .NET apps/builds, Xcode builds, and Gradle wrapper builds
prevent updates; idle Java/Gradle daemons do not.

Plain `dotnet run` does **not** invoke a launcher or fetch GitHub. It uses the
files already present in its current checkout. Unpublished work in a repair
worktree likewise cannot appear in the main checkout through this helper.
Review, commit, and publish through the existing protected delivery workflow
first, then explicitly preserve/reconcile local main-checkout changes before
syncing. Updating source does not restart an already-running application.

At the September 12, 2026 audit, `/Users/zacowen/MASTERAPP` was on `production`
at `5cf3879b`, with the user's iOS project build-number changes (build 32).
The helper deliberately skips that dirty checkout. Pending UI/metrics work was
in `repair/modal-content-region-20260911`; nothing was copied into main or
published by this change.

Validation: `python3 -B scripts/test-sync-published-checkout.py` exercises real,
disposable local Git repositories for clean fast-forward/current state,
unstaged/staged/untracked preservation, divergence, branch/upstream checks,
fetch failure, active-app protection, changes during fetch, and ignored-file
collisions. Additional process tests distinguish idle Java from active builds.
