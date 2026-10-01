# LEGEND single-branch release lifecycle

`legend/approved-changes` is the sole protected Git release authority. All other branches are temporary work. Production runtime state is represented by immutable deployment receipts and live provenance, not by a second mutable Git branch.

## Canonical flow

1. Create a bounded branch from the exact current approved head and open a same-repository PR to `legend/approved-changes`.
2. Owning validators run for the changed scope. Architecture is always required; Step 5, Step 6, Steps 7–8, and approved-release security are required when their owned inputs change. The resume planner preserves unrelated successful evidence.
3. The lifecycle merges only the exact validated PR head. Protected-branch requirements and conflicts fail closed. Source integration alone never deploys.
4. An application release requires an exact changed `Docs/releases/direct-release-request.json` with `releaseMode: approved-only` and explicit targets.
5. The sole web deploy workflow, `all-intentional-direct-release-20260918.yml`, verifies the approved source, current live baselines, rollback artifacts, reusable package identity, expected migrations, exact target provenance, and complete final outcome.
6. Targets already live at the exact application revision are preserved. A retry deploys only targets that are not already proven live. A failed direct release is not automatically replayed without a correction or explicit rerun.
7. After successful applicable releases, cleanup evaluates temporary branches against fresh approved-history, live-provenance, direct-release-receipt, PR, workflow, and protection evidence.

## Mandatory branch deletion conditions

A temporary branch is deleted only when all conditions hold:

- It is not `legend/approved-changes` and is not protected.
- Its entire head is an ancestor of the approved branch.
- Its entire head is covered by every relevant observed live web revision.
- Every live application revision has an exact successful direct-release receipt for that application.
- No open PR uses the branch as source or base.
- No workflow for the branch is active, and its most recent workflow did not fail or cancel.
- No direct release is queued or running while cleanup evaluates live evidence.
- Immediately before deletion the source SHA, approved SHA, PR use, protection, and workflow state are checked again. Git deletion uses an expected-SHA lease.

The GitHub `delete_branch_on_merge` setting remains false. Merge completion is not deployment proof.

## Failure and repair

A failed validation or deployment preserves all unaffected successful evidence. Repair the canonical failed source on the retained branch, then resume from the invalidated gate. Full reruns occur only when evidence cannot safely be reused.

If a merged change needs deployment correction, the approved branch remains the source authority and a new explicit release request or corrected approved descendant is used. No branch promotion, merge-back, parity reconciliation, or second release branch exists.

## Validation

Run `python3 scripts/test-release-lifecycle.py` for isolated ancestry, receipt, replay, cleanup, and single-authority tests. `python3 scripts/test-validation-resume.py` verifies per-gate preservation/invalidation behavior. Hosted workflow runs provide the external GitHub and live-runtime proof.
