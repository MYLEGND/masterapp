# LEGEND single-branch release lifecycle

`legend/approved-changes` is the sole protected Git release authority. All other branches are temporary work. Production runtime state is represented by immutable deployment receipts and live provenance, not by a second mutable Git branch.

## Canonical flow

1. Create a bounded branch from the exact current approved head and open a same-repository PR to `legend/approved-changes`.
2. Owning validators run for the changed scope. Architecture is always required; Step 5, Step 6, Steps 7–8, and approved-release security are required when their owned inputs change. The resume planner preserves unrelated successful evidence.
3. The lifecycle merges only the exact validated PR head. Protected-branch requirements and conflicts fail closed. Source integration alone never deploys.
4. A validated merged application PR carries publication authorization directly. The canonical inventory derives its affected targets. Historical explicit `Docs/releases/direct-release-request.json` authorizations remain bound to the exact request-changing approved commit; they do not establish a second scheduler.
5. The sole web deploy workflow, `all-intentional-direct-release-20260918.yml`, resolves the exact validated application PR head, restores the immutable package set produced by architecture validation, verifies its manifest/checksums, proves the separate Portal database baseline, applies only the validated migration bundle, deploys selected stale targets, and requires complete final live provenance. It has no production package rebuild fallback.
6. Targets already live at the exact application revision are preserved. A retry publishes only the stale target subset. After shared configuration, migration, rollback, and transaction preparation complete once, independent target-local Azure package publications may overlap inside that one admitted immutable transaction; each target keeps its own durable operation journal and the transaction finalizer remains read-only. Each attempt retains a durable step-state receipt; immutable evidence is reused, while current-state and mutation steps re-reconcile safely. A failed direct release is not blindly replayed. Its completion wakes the durable queue so another eligible candidate can proceed; publication still reconciles durable write intent before any upload.
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

Evidence transport failures retry bounded read-only requests. If the planner still cannot inspect required evidence, it stops at planning with a blocked diagnostic; it never turns a timeout into authorization for a full validation rerun. Resume the failed planning job after access recovers. Positively missing or incompatible evidence still requires its affected checks.

A failed validation or deployment preserves all unaffected successful evidence. Repair the canonical failed source on the retained branch, then resume from the invalidated gate. Full reruns occur only when evidence cannot safely be reused.

If a merged change needs deployment correction, the approved branch remains the source authority and a new explicit release request or corrected approved descendant is used. No branch promotion, merge-back, parity reconciliation, or second release branch exists.

## Validation

Run `python3 scripts/test-release-lifecycle.py` for isolated ancestry, receipt, replay, cleanup, and single-authority tests. `python3 scripts/test-validation-resume.py` verifies per-gate preservation/invalidation behavior. Hosted workflow runs provide the external GitHub and live-runtime proof.

## Durable queue and admission limits

The scheduler derives pending authorization from approved first-parent merged PR history, not a second queue database. It inspects the newest authorization for each affected target and keeps independent candidates in deterministic history order. A newer completed ClientApp release cannot hide an older unreleased Protect candidate. When only part of an older atomic transaction is superseded, that transaction remains explicitly retained until a validated combined successor exists; the scheduler does not split its authorized scope or silently discard the untouched targets.

Fresh integration and recovered candidates share one admission predicate. Active direct-release runs, including legacy runs without a candidate title, block new dispatch. Direct run titles bind source PR, immutable validated candidate, and execution authority, so a failed exact attempt is retained rather than blindly replayed. Both successful and failed completion events wake the queue, and reusable validation/package evidence is consulted without dispatching validation again.

Release transactions remain globally serialized. Shared settings, migration, routing, admission, and transaction finalization never overlap across releases. Inside one already-admitted immutable transaction, target-local Azure package publications may fan out only after all shared prerequisites complete; each target has a distinct operation identity/journal, so one target cannot authorize or replay another. The lifecycle mutex serializes admission decisions and the publisher mutex protects the complete multi-target transaction. This does not authorize concurrent disjoint releases or concurrent shared-resource mutation. An ambiguous dispatch or upload remains a reconciliation boundary, never evidence that retry is safe.
