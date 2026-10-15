# LEGEND single-branch release lifecycle

`legend/approved-changes` is the sole protected Git release authority. All other branches are temporary work. Production runtime state is represented by immutable deployment receipts and live provenance, not by a second mutable Git branch.

## Canonical flow

1. Create a bounded branch from the exact current approved head and open a same-repository PR to `legend/approved-changes`.
2. The lifecycle admits the exact candidate and approved baseline, then observes migration and publication readiness using approved code. The candidate's minimal probe and any required isolated migration rehearsal precede expensive validation. Only a compatible, fresh, successful readiness receipt opens the resume planner. Owning validators then run for the changed scope. Architecture is always required; Step 5, Step 6, Steps 7–8, and approved-release security are required when their owned inputs change. The resume planner preserves unrelated successful evidence.
3. The lifecycle merges only the exact validated PR head. Protected-branch requirements and conflicts fail closed. Source integration alone never deploys.
4. A validated merged application PR carries publication authorization directly. The canonical inventory derives its affected targets. Historical explicit `Docs/releases/direct-release-request.json` authorizations remain bound to the exact request-changing approved commit; they do not establish a second scheduler.
5. The sole web deploy workflow, `all-intentional-direct-release-20260918.yml`, resolves the exact validated application PR head, restores the immutable package set produced by architecture validation, verifies its manifest/checksums, proves the separate Portal database baseline, applies only the validated migration bundle, deploys selected stale targets, and requires complete final live provenance. It has no production package rebuild fallback.
6. Targets already live at the exact application revision are preserved. A retry deploys only targets that are not already proven live. Each attempt retains a durable step-state receipt; immutable evidence is reused, while current-state and mutation steps re-reconcile safely. A failed direct release is not blindly replayed. Its completion wakes the durable queue so another eligible candidate can proceed; publication still reconciles durable write intent before any upload.
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

Package backfill uses explicit job status conditions so intentionally skipped PR-only probe/rehearsal ancestors cannot suppress required component builds. Assembly accepts a skipped component matrix only when the authenticated planner explicitly requires no component execution. A completed backfill with unavailable compatible package evidence blocks repeated dispatch under the same approved authority and retains its run/attempt identity. Historical dispatch records do not expose the requested package revision, so this guard conservatively covers that authority's backfill scope. Reconcile retained artifacts and the exact failure before resuming failed jobs; a reviewed authority correction receives a fresh eligibility assessment. This does not clear production ownership or authorize a mutation retry.

If a merged change needs deployment correction, the approved branch remains the source authority and a new explicit release request or corrected approved descendant is used. No branch promotion, merge-back, parity reconciliation, or second release branch exists.

## Historical migration uncertainty and current-state reconciliation

`release-migration.py:resolve_migration_boundary` distinguishes invalid evidence,
active execution, proven nonentry, and an unknown historical outcome. An unknown
outcome is never proof that SQL did not execute and never authorizes replay.

The existing migration-history audit authenticates the exact terminal attempt,
original workflow, complete artifact inventory, retained execution state, original
admission, and any child operation records before classifying an otherwise unknown
modern failure as requiring runtime reconciliation. Missing, expired required, or
contradictory evidence remains blocked. The original uncertainty stays in the audit.

At the production boundary, the same audit is consumed under the canonical release
ownership. Other release workers must be terminal; the current worker must match its
API run attempt. The approved read-only probe checks database activity visibility
before and after schema observation. Two observations must agree on database,
migration catalog, applied baseline, and pending state. Insufficient visibility,
active transactions, or drift stops this boundary without executing a bundle.
Only positively classified internal engine requests and system transactions are
excluded; missing DMV mappings remain blocking. A held EF migration session lock
also blocks, including between transactions. Ordinary user requests/writes still
require a quiet interval: unknown historical executors are not assumed to follow
the current EF locking protocol. Sustained traffic may safely block publication.
Only the fixed active-activity classification retries the observation pair, at
most six times under one 90-second deadline covering inner SQL-read retries,
process timeouts and backoff. Historical lookup, compilation, rehearsal and valid
packages are preserved. Permission, unknown, and schema failures never enter that
retry path.

When the current desired schema is complete, this new current-policy proof allows
preservation without SQL, even if a premerge readiness receipt used an older policy.
It does not relabel old execution as successful or replay an ambiguous operation.
Before preservation, the existing operation-evidence channel retains a current-state
observation bound to the run/attempt, candidate, execution authority, bundle, database,
baseline, activity check, and historical report digest. A missing upload acknowledgment
stops the boundary; a later attempt may reobserve without executing SQL.
Pending migrations retain the existing authenticated readiness/rehearsal and strict
first-write journal requirements. The no-write path is not a policy-transition
exception for pending SQL. Successful application siblings and immutable packages
remain under their existing independent authorities.

## Validation

Run `python3 scripts/test-release-lifecycle.py` for isolated ancestry, receipt, replay, cleanup, and single-authority tests. `python3 scripts/test-validation-resume.py` verifies per-gate preservation/invalidation behavior. Hosted workflow runs provide the external GitHub and live-runtime proof.

## Durable queue and admission limits

The scheduler derives both automatic and explicit pending authorization from one approved first-parent history frontier, not a second queue database. A merged explicit request binds its own validated PR head while package producer identity remains separate. Completion of an explicit transaction requires proof bound to its exact authorization; older live package bytes alone cannot discharge a new migration/configuration request. Legacy reader/dispatcher entry points delegate to this same frontier and admission path. It inspects the newest authorization for each affected target and keeps independent candidates in deterministic history order. A newer completed ClientApp release cannot hide an older unreleased Protect candidate. When only part of an older atomic transaction is superseded, that transaction remains explicitly retained until a validated combined successor exists; the scheduler does not split its authorized scope or silently discard the untouched targets.

Fresh integration and recovered candidates share one admission predicate. Active direct-release runs, including legacy runs without a candidate title, block new dispatch. Direct run titles bind source PR, immutable validated candidate, and execution authority, so a failed exact attempt is retained rather than blindly replayed. Both successful and failed completion events wake the queue, and reusable validation/package evidence is consulted without dispatching validation again.

Publication remains globally serialized. Current shared settings, migration, routing, and upload reconciliation have not yet been proven safe under concurrent resource reservations. The lifecycle mutex serializes admission decisions; the publisher mutex protects the current multi-target transaction. These controls do not constitute completed support for concurrent disjoint publication. An ambiguous dispatch or upload remains a reconciliation boundary, never evidence that retry is safe.

### Publication evidence recovery

The immutable operation transport retries only recognized transient downloads of
an already selected artifact ID, at most three times within the existing publisher
process deadline. It never returns to upload after a readback failure. Authorization
errors, unknown errors, missing content and mismatched bytes remain blocked.

Parallel publication retains bounded, attempt-qualified per-target diagnostics even
when a child fails. An `invocationSubmission=not-entered` result proves only that
invocation stopped at intent authorization/readback; it does not erase prior intent or authorize
replay. Unknown failures remain `may-have-entered`. These diagnostic records do not
replace durable deployment receipts, historical authorization or live acceptance.
Older successful receipts retain their exact reviewed verifier compatibility.

Legacy attempts that did not retain the submission boundary still require positive
original transport or provider evidence. Empty provider history and old runtime
provenance alone do not establish nonentry. New diagnostics cannot retroactively
certify those attempts.
