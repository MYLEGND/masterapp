# LEGEND release branch lifecycle

`legend/approved-changes` is the default and the source for quick releases.
`production` is the release base that has passed the rigorous CI/security path.
All other branches hold temporary work; unique or failed work stays until proven safe.

## Autonomous flow

1. Work on a temporary branch based on current approved changes. Open a same-repository PR to `legend/approved-changes`; draft means work in progress, ready means approved for integration and release. Only repository owners, members and collaborators qualify. Forks do not.
2. The lifecycle workflow merges the exact ready PR head using GitHub's merge API. Conflicts or branch requirements retain the source branch. It explicitly dispatches the existing direct-release workflow, because a merge made with `GITHUB_TOKEN` does not fire push workflows.
3. Automatic releases conservatively include **all five existing web targets**, including the backend serving iOS/Android. They reuse the existing build, focused tests, migration, rollback and exact live-provenance checks. The existing manually requested portal-only release remains available.
4. Only a successful complete direct release of the **current** approved head creates/advances the PR to `production`. The same rigorous workflow resolves that PR's exact merge candidate, checks it includes all live web histories, executes existing CI/security gates, merges, deploys, and verifies. No direct push or protection bypass is used to advance production. A failure is not retried repeatedly without corrections or an explicit workflow rerun.
5. Successful production history is merged back into approved changes without overwriting newer work. The resulting approved commit receives an explicit direct release so provenance cannot stall. Conflicts retain both histories for repair.
6. After a successful applicable release, and hourly to recover missed events, cleanup evaluates every branch against fresh evidence. Failed releases do not authorize cleanup. Ready PRs created by automation and new commits on previously approved, retained branches are also reconciled hourly.

## Mandatory deletion conditions

A branch is deleted only when all conditions hold:

- It is neither release branch and is not protected.
- Its **entire head history** is an ancestor of both release branches and all five observed live web revisions.
- The current production base and every observed live web revision have successful authoritative release evidence, including actual successful deployment/proof jobs. A green workflow with skipped jobs is insufficient; a newer failed attempt invalidates an older success.
- No open PR uses it as source or base, no run is active, and its most recent workflow did not fail or cancel.
- No release is queued or running during the audit.
- Its changes do not include native iOS, native Android, or Cloudflare Worker publication that only a web receipt would incorrectly certify. Such branches remain until their separate publication evidence is integrated. The repository currently has no automated iOS store release or Worker release workflow; this policy does not invent one.
- Immediately before deletion, protection, PR use, branch runs, both release tips and the source head are checked again. Git's expected-SHA lease makes deletion fail if a concurrent push changes the source branch.

The standard GitHub `delete_branch_on_merge` setting must remain **false**: merge completion is not deployment success. Cleanup writes an artifact listing decisions and exact SHAs. It never uses branch age, naming, patch similarity, or a stale baseline as proof of safety.

## Failure and repair

A failing deployment may already have a commit merged into a release branch. That is source preservation, not a successful deployed baseline. Temporary branches remain. Push corrections to the retained branch; the hourly reconciler carries forward new descendants of the previously approved head. Drafts, force-rewritten histories, unknown authors and conflicting merges are retained for inspection. Source code cannot autonomously fix arbitrary application errors; this workflow resumes the release when the correction exists.

Two permanent release paths do not imply deleting unfinished or unmerged work. Old branches with unique history stay until their changes are individually reconciled; merging abandoned experiments indiscriminately would reintroduce regressions. See `branch-audit-20260919.json` for the initial snapshot. This file is historical evidence; all runtime decisions use fresh API and live endpoint reads.

## Validation and references

Run `python3 scripts/test-release-lifecycle.py` for isolated Git ancestry, race and negative-evidence tests. GitHub workflow execution supplies the actual repository mutation/deployment proof.

GitHub documents [GITHUB_TOKEN event behavior](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow) and [workflow_run trust boundaries](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#workflow_run). Privileged lifecycle steps check out only trusted approved code, never a PR head.
