# LEGEND Approved Changes

`legend/approved-changes` is the canonical integration branch. `production` remains the protected release branch; GitHub’s default branch and the local testing checkout point to approved changes. Spaces are invalid in Git branch names, so the display name “Legend® Approved Changes” maps to this portable ref.

1. Start bounded work from approved changes in an isolated task branch. Preserve worktree changes and never reset or auto-stash another session's work.
2. Review and test the change, then merge into approved changes. Both normal and direct releases must retain every currently deployed source revision. A branch name alone is not evidence that a change passed review.
3. Normal release: open the existing production PR from approved changes; the existing synchronize event runs the full protected workflow. Required checks and approvals remain intact.
4. Direct release: use the existing `all-intentional-direct-release-20260918.yml` workflow on approved changes. When manual dispatch is unavailable because the default branch has not yet received the workflow definition, an explicit reviewed update to `Docs/releases/direct-release-request.json` on approved changes triggers the same workflow. Ordinary commits never deploy automatically. Each release-request update is an explicit deployment request; ordinary commits do not change it.
5. Direct release verifies the exact approved SHA, reads all current web deployment revisions, requires all of them to be ancestors, builds rollback packages, runs focused build/tests, applies only candidate additive migrations, and verifies actual running revisions. Both release paths share the production concurrency lock. Native binaries require their existing signed distribution process.
6. After verification, record the run and per-app deployed revisions. A direct release may leave the production ref behind; it never changes which ref is the canonical source for the next candidate.
7. Delete task branches only when their exact tips are contained in approved changes, no open PR uses them as head or base, no worktree depends on them, and no active workflow/configuration references them. Recheck remote tips with a lease. Never infer that unmerged work is noise.

## Workspace synchronization

The existing `com.mylegnd.native-checkout-sync` LaunchAgent runs every 60 seconds. It uses the existing sync script and the configured `legend.nativeTestingRef`; no second background process is needed. MASTERAPP tracks approved changes. Native IDEs and actual builds must be stopped for source updates. The helper uses fast-forward only, checks for concurrent Git/branch/target changes, and never resets, rebases, force-pushes or auto-stashes. Uncommitted colliding changes and divergent history stop synchronization.

This is safe inbound GitHub synchronization, not automatic publishing of unreviewed local files. Review, commit and push local work explicitly. iOS and Android build guards verify the same checkout; a running installed app is not updated by a Git sync.

## Consolidation evidence, 2026-09-18

- Portal and Client actual loaded source: `326865c41244f3945147a793a01e6fda08248298`.
- Protect, Parfait and static website: `83b95aaaf7967c83c3c16a4ca0529c502a11b30f`.
- Both revisions and the existing production head are retained in approved changes.
- Diagnostics direct deployment run `35371291977` succeeded; later messaging hotfix run `35387695820` succeeded and was merged without reverting diagnostics.
- Retired seven one-off direct-deploy/revalidation workflows; retained their history in Git. The reusable direct authority is the existing direct-release workflow, alongside the unchanged substantive checks of the protected release path.
