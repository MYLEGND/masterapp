# Release recovery correction — implementation checkpoint

Status: **incomplete; not released**. This record does not certify the comprehensive
validation-to-deployment contract or the 10–15 minute end-to-end target.

## Identity and ownership

- Approved base: `11d902bd196d1f844bbf4ec2d9a920d10033d437`.
- Repair: `repair/migration-skipped-history-20261008`, draft PR #551.
- First correction: `3cb30054cc3d45216411134c5a13c3e0a8e794c8`.
- Receipt/classification checkpoint: `d08489f7000e76ab2a7abec864872c7b9f061cdb`.
- No production migration, merge, publication, or deployment was performed by this repair.

| Responsibility | Existing canonical owner |
| --- | --- |
| Gate dependencies, input identity, evidence reuse, affected targets | `scripts/validation-resume.py` |
| Immutable deployable components and assembly | `scripts/release-package.py` |
| Minimal read-only migration probe artifact | `scripts/migration-probe-package.py` |
| Schema observation, first-write admission, migration reconciliation | `scripts/release-migration.py` |
| Configuration/database prepublication lanes | `scripts/release-prepublication.py` |
| Integration, release admission, queue and recovery dispatch | `scripts/release-lifecycle.py` |
| Durable operation/child/configuration journals | `scripts/release-operation-evidence.py` |
| Synchronous immutable artifact upload/readback | `scripts/release-artifacts/transport.cjs` |
| Target publication, reconciliation, finalization and disposition | `scripts/deploy-approved-app.py` |
| Sole production transaction | `.github/workflows/all-intentional-direct-release-20260918.yml` |
| Trusted integration/recovery entry point | `.github/workflows/legend-release-lifecycle.yml` |
| Diagnostic entry points, observation only | `deployment-diagnostics.yml`, `legend-production-readonly-diagnostic.yml` |

The five owning CI workflows remain architecture, approved-release security,
Step 5, Step 6, and Steps 7–8. The target inventory and workflow generator are
unchanged. No alternate scheduler, migration executor, target registry, or
production release workflow was added.

## Confirmed defects and corrections

1. The original skipped migration step in run `36979837740` was not recognized
   by admission. The historical audit and admission disagreed about this source
   generation. Both now use the same canonical legacy-step recognition.
2. Original successful CLI migration steps that reported zero changes lacked a
   source-bound no-write proof. Narrow exact-source attestations now require
   original checkout identity, completed step boundaries, both unique no-change
   markers, and absence of migration application/reversion output. Unknown,
   active, duplicate, partial, or actually mutating executions remain blocked.
3. Migration-audit control files were absent from impact classification, causing
   all six package components and five applications to be selected. Their
   ownership is now explicit; required security validation remains in force.
4. The deployment finalizer invoked reconciliation without its durable journal.
   It now requires a journal and durable success receipt before commitment.
   Successful siblings are removed from the unresolved set and remain preserved.
5. Admission duplicated transport invocation without a timeout or strict receipt
   parsing. It now delegates to `publish_record`, including its 180-second bound.
6. Lost artifact-upload acknowledgment caused an unresolved receipt even when
   immutable bytes existed. The transport first inspects exact identity, uploads
   at most once, reconciles ambiguous acknowledgment through bounded reads, and
   verifies identical content by immutable artifact ID. Duplicate identities or
   mismatched content fail closed. Delayed visibility gets three observations
   with 1/2-second backoff inside the existing subprocess deadline.
7. Diagnostic entry points could select unapproved source while receiving
   production credentials. Jobs now require the approved ref; the native
   diagnostic also proves requested source is an ancestor of approved history
   before credential-bearing steps. The native-branch push trigger was removed.

Removed duplicated responsibility: admission's private transport invocation and
historical audit's divergent legacy-step recognition. Protected publication
420-second and finalization 90-second/three-attempt timing invariants are unchanged.

## Evidence actually obtained

### Production observation, read-only

The verified approved probe observed 218 applied history entries, 219 recognized
entries including four narrowly audited historical stamps, and exactly one
pending migration: `20261007134500_AddFounderAssistantRules`.

The physical checks passed for the historical engineering/feed schema and agreed
that the Founder rules column was absent. The migration is genuinely pending;
no partial column application was observed. Schema identity:
`6d6ea1c9a13889cf8bbf42469c742d588c4fcfee7a17497149a57365fe6951ec`.

All five canonical runtime-provenance endpoints reported:
`540f900564c4003ed6fa230b9fa8287892e6a1e9`.
These observations are not new deployment receipts or functional acceptance.

The source-bound historical audit inspected 202 release runs / 217 attempts:
zero unproven steps; 14 historical operations still require the live physical
schema fence. This read-only audit grants no production mutation permission.

### Regression and failure injection

- Canonical lifecycle: 182 tests passed after diagnostic-boundary correction.
- Deployment owner: 65 tests passed, including unresolved-only finalization,
  receipt loss, preserved sibling counts, and zero repeated uploads.
- Operation evidence: 45 tests passed.
- Migration/release children: 71 tests passed with CI repository environment.
- Historical audit: 8 tests passed.
- Artifact transport: 12 tests passed, including lost acknowledgment, worker
  restart, duplicate events, mismatched producer, tampering, inventory failure,
  missing outcome, and overlapping publishers. The concurrency model asserts one
  accepted create under the provider's immutable-name constraint; it does not
  promise universal exactly-once external execution.
- Focused impact test: zero requested package work and zero application targets
  for migration-audit-only changes, while security remains required.
- `release-workflow.py --check` and `git diff --check` passed.

SQL-backed failure injection used a disposable loopback SQL Server 2022, synthetic
model/data, the original engineering-schema migration for unmapped tables, and
explicit synthetic legacy-history fixture entries. It copied no production data.
The exact validated migration bundle was mounted read-only and invoked once:

- Producer run: `37869228334`, revision `3cb30054cc3d45216411134c5a13c3e0a8e794c8`.
- Component artifact: `11589558395`.
- SHA-256: `565148f9a894582de076efbdc8f43b7b74136005575534ac16d2dff6157cd97c`.
- Pending before/after: 1 / 0.
- Mutation executions: **1**; intents: **1**; success-receipt attempts: **2**.
- First receipt deliberately failed; resumed canonical reconciliation observed
  correct physical schema/history and completed without invoking the bundle again.
- Synthetic existing row and default `[]` were preserved.
- The container was removed afterward.

That checkpoint's rehearsal was local. The subsequent candidate implementation
places the fixture and failure injection in the canonical early readiness flow.
It has not yet been proved by the deployed approved authority.

### Early-readiness implementation checkpoint

The same repair branch now implements approved lifecycle observation, a bounded
declarative candidate contract, independent minimal bundle preparation, isolated
rehearsal, trusted admission completion, and readiness consumption before the
owning validation/package planners. The existing production release remains the
only mutation authority. No candidate executable receives production credentials.

The minimal migration bundle now starts from `MigrationReleaseProbe`, without
building AgentPortal. Local synthetic SQL proof of these new bytes:

- Bundle SHA-256: `a302211da463a2fdc24f100d32a5cfa8a4d7eef31392887253399d46277b5ec8`.
- One intent, one bundle execution, two success-receipt attempts.
- Pending migration cleared; synthetic row/default and declarative postconditions passed.
- Migration plus injected acknowledgment-loss recovery: 8.412 seconds, excluding
  bundle build, fixture build, database startup, and queue time.
- This is local candidate proof, not a validated CI package or production receipt.

Readiness artifacts have immutable attempt-specific names. Consumers authenticate
the exact successful child attempt, so a failed parent or later attempt cannot
erase compatible completed proof. Prepared migration bundles are retained before
the dependent rehearsal; promotion verifies the exact bundle digest.

The subsequent local delta closes artifact-attempt substitution, binds rehearsal
code to the approved owner, and compares declarative metadata against the approved
migration runtime. Changed runtime semantics currently stop at
`CANDIDATE_MIGRATION_CONTRACT_UNPROVEN`; support for new migration semantics requires
a reviewed approved extraction rule. This conservative boundary is not general
support for arbitrary candidate migrations.

Admission now delegates to `release-migration.py`. Rehearsal receipts reference the
original prepared bundle by immutable artifact ID instead of copying its bytes.
Selection filters the observed baseline; an old successful rehearsal cannot mask
a newly required one. Production reobserves physical state and binds database,
baseline, and exact bundle before a new intent. Zero-pending success is explicitly
`schema-ready-no-write`, preserving the distinction from historical SQL execution.

The existing lifecycle refreshes expired authenticated readiness or classified
transient observation failures with one durable intent before the exact job rerun.
Lost acknowledgment is reconciled without another POST. Unknown finalizer failures
stop; failed/skipped/cancelled child outcomes do not wait for an active parent.
Attempt and shared elapsed-time budgets bound recovery. General historical lookup
indexing and complete recovery-composition coverage remain unfinished.

The minimal executable dependency graphs selected obsolete vulnerable
`System.Security.Cryptography.Xml` 8.0.2 because transitive framework references
do not participate in NuGet pruning. The first Infrastructure 8.0.4 pin failed
the required warnings-as-errors gate with NU1510. The corrected probe and fixture
explicitly reference their required `Microsoft.AspNetCore.App` framework; the
obsolete package is absent from both restored graphs. The probe builds with zero
warnings/errors and no suppression. This follows the
[documented NuGet framework pruning behavior](https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu1510).
The prior pin-based minimal bundle SHA-256 was
`990bb197aa5a3ca5058713f3ce1d040c029284baac0288d85f550ab9c9ecb224`.
Disposable SQL verification observed one intent, one actual bundle invocation,
two success-receipt attempts, preserved synthetic row/default, and verified physical
postconditions. The measured operation/recovery interval was 9.383 seconds.
The journal failure was modeled in memory; SQL execution was real. The representative
baseline used retained approved inventory, not a fresh production observation.
Build, queue, and SQL startup are excluded; this is not end-to-end timing.
That bundle's proof is preserved historical evidence; the framework-reference
correction changes artifact inputs and requires new exact bundle proof.

Checkpoint `cefb044402afe90f33a9c91ba18c614a6703fec9` passed the untouched approved
integrity guard and was pushed to PR #551. Exact CI exposed the pruning defect
above and a missing evidence-read token in the candidate migration build. The
correction moves authenticated rehearsal promotion into the existing planning
stage. The candidate build performs no online evidence lookup and receives no
added token. Promoted artifacts have attempt-specific identities; assembly selects
the exact planned artifact, preventing same-name conflicts after planner reruns.

Current focused proof includes 189 lifecycle tests, 187 validation-resume tests
on isolated CI-compatible Python 3.12, 22 readiness trust tests after the latest
producer checks, 48 operation-evidence tests, and 13 artifact-transport tests.
The 78-test migration suite passed except one missing local repository environment;
that case passed with the required environment and now declares its own fixture.
Python 3.13 historical AST fingerprints differ; matching Python 3.12 resolved the
historical-source failures without changing expected fingerprints.

Remaining release blockers include independent content-based package component
reuse and mixed-producer live provenance, broader reviewed migration shapes,
complete failure-boundary proof, submission-to-live measurements, and exact final
approved integrity/security/release/live acceptance. Existing green CI certifies
only its recorded revision.

### Exact-head CI and timings

Checkpoint `d08489f7…` passed the untouched approved-branch integrity guard with live
repository rulesets and all five owning CI workflows. Later changes require fresh
exact-head evidence; this checkpoint is not approval of future commits.

| Workflow | Run | Created to completion |
| --- | --- | --- |
| Architecture | 37870403174 | 3m29s |
| Security | 37870403188 | 54s |
| Step 5 | 37870403286 | 6m12s |
| Step 6 | 37870403182 | 42s |
| Steps 7–8 | 37870403186 | 61s |

All started at 2026-10-09 01:34:10 UTC. Six unnecessary component builds requested
by the first checkpoint became zero at this checkpoint. The SQL migration plus
injected receipt recovery took 11.132 seconds, excluding container preparation,
fixture construction and harness compilation. These are component measurements,
not submission-to-live timing. No end-to-end percentile, cold-release duration,
or verified 10–15 minute release result is established.

## Outstanding comprehensive requirements

- Early trusted readiness is implemented in this candidate but still requires
  exact-head CI, review, approved-authority installation, and a live release proof.
- The representative fixture deliberately covers the actual pending Founder
  migration. Unsupported migration shapes stop for a reviewed fixture.
- Independent package-component compatibility/promotion remains broader than the
  requested smallest-unit reuse; package assembly still binds one producer SHA.
- A complete submitted-to-live timing ledger and requested scenario matrix remain
  unfinished. Existing case reuse is bounded by framework isolation; no universal
  individual-case reuse is claimed.
- Independent verification and release review are required by
  `.github/copilot-instructions.md`; implementer tests do not substitute for them.
- The session does not expose `legend_verify_current_page_repair` or the other
  governed live LEGEND tools. Plugin discovery returned no LEGEND integration.
- Explicit affected-target release request, governed merge/admission, production
  migration, target receipts, deployed provenance, and structural live acceptance
  remain outstanding. No release identity or new per-target receipt exists.

Preserve the draft branch and completed receipts. Resume from its exact head;
do not restart or discard successful compatible validation. Complete the remaining
canonical implementation, independently review that exact candidate, then use the
existing protected lifecycle. Do not apply the production migration locally.
