# LEGEND canonical commerce internal ownership and Parfait host retirement

Status: **IN PROGRESS — NO DOMAIN CUTOVER OR APP RETIREMENT AUTHORIZED BY THIS DOCUMENT**.
This is the required all-business contract, not a Parfait-specific duplicate.
Use the existing `Legend/fast-track-release` release authority after exact-head validation.

## 2026-10-11 verification checkpoint

- Continue PR #580 on `isolation/canonical-scoped-commerce-internal-20261010`; inspected source head `f8a2aa5933ae97c63b7febdaaedbdd59864f0efa` is based on the current `Legend/fast-track-release` head `640a2fa3e44fe1b36ff9c12e44aa1bbd1a7f21f6`. Do not restart the consolidation.
- `Legend/fast-track-release` is the exclusive integration, final validation-admission, deployment and rollback authority for this assignment. `legend/approved-changes` is excluded as a base, baseline, release source and rollback authority. Do not invoke or repair its lifecycle.
- Existing run `38097642242` recorded 153 tests passed, zero failed/skipped, three builds and matching assets. These receipts apply to that validated source, not subsequent edits. The current analytics-evidence/team-presentation changes pass all 11 focused `CommerceCoreOwnershipContractTests` locally with .NET SDK 10.0.401, plus JavaScript syntax and actual-renderer checks distinguishing unavailable, zero and positive values. Exact-head fast-track CI admission remains required.
- The Founder corrected the existing login workflow at isolated head `8cceef5ab84caf8c22ff72ecea468921a7bf6794`. Read-only inventory run `38119002842` now succeeds: both apps are running revision `97555039d28fdeb4b2afe5c58d395ac70531d302`; preview, cutover, storage-root and reconciled-manifest configuration are absent/disabled. The earlier Azure login blocker is resolved. This is configuration/public-route evidence, not SQL or file inventory.
- Application/test source at `8cceef5ab84caf8c22ff72ecea468921a7bf6794` is unchanged from validated `d4f9bcc424f2ccb07d5370eeb72d80d979062b14`, permitting that source evidence to be retained. Subsequent manifest-identity changes require fresh focused validation.
- The existing inventory and plan-only transfer utilities have no callable non-workflow runtime adapter identified in inspected source. An admitted invocation of those canonical utilities is still needed to establish SQL/media reconciliation; payment and full cutover readiness remain unverified.
- Governed repository inspection classifies `legend-fast-track-ui.yml` and the read-only inventory workflow as `INTEGRITY_PROTECTED`. Their bodies cannot be edited through the current source authority. Legitimate fast-track identity, security and production-mutation controls remain mandatory.
- Aggregate delivery counts do not prove same-event browser/server matching, attribution completeness, or paid-order reconciliation. The shared health snapshot marks these unavailable until identity-based evidence exists; observed counts remain counts. Owner-only team administration is not a delegable checkbox in the scoped team editor.
- The runtime still has legacy commerce fallback, and shared Razor presentation remains authored under `ParfaitApp`. Live migration inventory, client/internal-login parity, durable media transfer, payment/webhook validation, and two-tenant isolation/visual proof remain outstanding. Keep the Parfait domain and App Service functioning.
- Live browser baseline: `https://www.shopparfait.com` redirects to `https://shopparfait.com/`; structural diagnostics identify `ParfaitApp` at revision `97555039d28fdeb4b2afe5c58d395ac70531d302` with empty issues/duplicate-assets lists. Home and Team login link render. This proves the legacy home responds, not checkout or migration.
- Deploy only applications proven affected after fast-track admission; retain valid uploads/artifacts/receipts and resume only unresolved verification. Domain cutover and App Service retirement require independent completion of every acceptance item below.

## Canonical ownership rule

- One `CommerceBusiness.Id` is the permanent tenant identity for catalog, inventory, orders, discounts, Square merchant linkage, analytics attribution, website publications, team/member capabilities, automations, customer lists, media, and billing. Never create a replacement Parfait business row.
- One shared commerce service implementation lives in `CommerceCore` / `Infrastructure`. Controllers in AgentPortal, ClientApp, Protect, and any compatibility host must be adapters, not independent state or payment engines.
- One internal page and modal source serves every store-enabled scoped business, including Parfait. Parfait's original *public* site, typography, media, product visuals, layout, and navigation remain its tenant-owned version. No generic theme replaces it.
- Internal authentication must use the platform's existing verified actor identity and active `CommerceBusinessMember` permissions. It must authorize **both the actor and business** for each action, including page GET, modal reads, save/upload, order/return/refund, provider settings, team changes, and automation sends. No ability to select a target tenant through an untrusted query string, host header, or posted ID.
- Every business that enables the store in its website editor receives the same authorized management experience. The option is not globally turned on for tenants without a store.
- Keep a single immutable published website record per business/domain; do not clone the public page tree, website analytics writer, CRM, payment engine, or authorization rules.

## Current verified code boundaries

- `CommerceCore/Controllers/CommerceManagementController.cs` is a single business-ID-scoped ticket adapter that already reuses dashboard, products, orders, automation, analytics, preview, and original Razor modal sources.
- `ParfaitApp/Controllers/InternalController.cs`, `InternalModulesController.cs`, `InternalSettingsController.cs`, `InternalBusinessesController.cs`, `ParfaitTeamAccessService.cs`, and `ParfaitInternalPageRegistry.cs` remain Parfait-app-local legacy authorities. They are **not** the finished shared client internal login/authorization runtime.
- `ParfaitTeamAccessService` stores Parfait team permissions in JSON and restricts legacy sign-in to approved `@mylegnd.com` emails. That cannot be reused as the login authority for unrelated business customers.
- The shared MVC registration permits `CommerceManagementController` in both preview and cutover modes; the management-only registration excludes public storefront/cart/checkout controllers. Controller registration is not proof of routed production access or completed internal-login migration.
- `Legend-Cloudflare/src/website-routing/bridge.mjs` includes `/commerce/manage/` and `/parfait-analytics/` in selected exact-domain cutover. Legacy commerce fallback remains for non-admitted tenants and platform domains; source routing does not prove those dependencies are retired.
- `CommerceLegacyTransfer` has tested PlanOnly and CopyNoOverwrite behavior, but there is no recorded production cross-host media/JSON manifest readback or durable transfer authorization.
- Source-level Razor/CSS asset preservation is not live desktop/mobile or checkout parity.

## Implementation acceptance (all required before cutover)

1. **Tenant identity and subscription:** Read and pin Parfait's existing active `CommerceBusiness.Id`; verify membership, account/subscription, existing website publication and ownership in ClientApp and AgentPortal. Preserve historical IDs.
2. **One internal runtime:** Move legacy Parfait-only route/page registry and team-page capabilities into one shared business-scoped internal application contract. Ensure each existing internal page/modal has exactly one authored view and corresponding business-scoped action. `ParfaitApp` may only redirect/bridge to it while old sessions are active. Do not copy controllers or views into client-specific implementations.
3. **Login and authorization:** Active membership and per-operation capability grant every store-enabled business owner/team member the right pages, without requiring a LEGEND staff email. Reject inactive/revoked members, cross-tenant IDs, forged/tampered tickets, expired sessions, and unauthorized management POSTs. Confirm existing Parfait team invites and role permissions are preserved through migration.
4. **Shared state:** Put automation/team JSON and product uploads into durable tenant-scoped storage with a reconciled source-and-destination manifest; preserve customer/order history, media URLs, checkout identifiers, idempotency, attribution lineage and product/inventory records. Verify backup and no-overwrite readback.
5. **Commerce correctness:** Shared host completes cart, quote, checkout, Square capture/webhook, order settlement, receipt, fulfillment, refund, and abandonment automation with correct tenant/membership scope. Run sandbox or read-only tests before any specifically approved real payment.
6. **Website and visual parity:** Confirm Parfait's live public pages, store/product detail, cart, checkout, favicon, SEO, form controls, responsive desktop/mobile layout and business-owned theme match exactly. Independently confirm a second non-Parfait business can enable a store, enter the *same* internal management runtime and manage only its own objects.
7. **Cross-host routing:** Verify both `shopparfait.com` and `www.shopparfait.com` bind to the original Parfait business, Cloudflare certificate is active, and all commerce *and private management/analytics/automation* routes have the replacement runtime. Prove the new host before enabling Cloudflare's exact-domain cutover. Other businesses' hosts and platform origins remain unchanged.
8. **Retirement proof:** Prove no authoritative data, session callback, webhook, redirect, management endpoint, automation dispatcher, secrets/key ring, or image file is still dependent on `masterapp-parfait`. Preserve rollback/backups, then stop the legacy service; delete only after the agreed observation and recovery evidence.

## Explicit non-equivalences

- A successful ZIP upload or revision check does not prove live store checkout.
- Source hash parity does not prove live visual parity.
- Existing `CommerceBusiness` row does not prove client membership activation.
- Published code with cutover OFF does not switch domains, files, webhooks, or auth.
- A sitemap timeout does not warrant repeating an already verified Azure upload.
- Business-scoped website-editor tickets are not replacements for per-capability business internal login and team authorization.

## Release scope and protection

Work in an isolated branch; validate and merge through the pre-existing fast-track admission **only after** the above implementation is complete. CI must build and test impacted projects, prove no EF migration replay, preserve live Parfait/Protect revisions until approved cutover, and select only affected apps/Worker. Stop on identity, data, payment, tenant isolation, origin, or visual-parity mismatch. Do not disable any existing gate merely to advance release.

## Combined-candidate migration verifier checkpoint — 2026-10-11

PR #580 incorporates payment PR #581 and the Founder-approved operating contract at
`ddb03578cf26c2e8076e0e34f03c271df5166bc6`. Continue on the same isolation branch;
`Legend/fast-track-release` remains the only release authority for this task.

The canonical migration contract now represents additive tables, primary and foreign
keys, indexes, and the exact read-only duplicate-checkout precondition. The observer
checks physical SQL metadata and rejects missing, disabled, untrusted, or mismatched
constraints. Arbitrary SQL, destructive operations, unsupported annotations, and
stale contract versions remain unsupported. No production observer executes SQL
from the declarative candidate contract.

Credential-free contract tests inspect the actual payment migration: three added
columns, eight tables, fifteen indexes, and its checkout guard. They cover the
complete historical contract round trip and negative operation/contract cases.
Both probe projects compile locally; the contract tests and 14 existing migration
probe retry tests pass. The existing isolated CI workflow now runs these checks.
The existing SQL rehearsal fixture has been extended with two synthetic tenants
sharing a checkout identity and preservation checks for their historical orders.
That SQL fixture still requires execution against disposable SQL Server; local
contract tests are not a claim that the SQL rehearsal passed.

Remaining: disposable SQL rehearsal and adversarial schema checks; reviewed
fast-track security/admission and migration orchestration; production plan-only
identity/data/media reconciliation; selective deployment and live payment/runtime
verification. No migration, Azure deployment, DNS cutover, or retirement is admitted
by this source checkpoint. Preserve existing DNS preparation and live legacy service.

The isolated workflow also exercises SQL Server through the existing disposable
SQL owner. It applies the actual EF payment migration to synthetic legacy orders,
checks preservation across two tenants, rejects duplicate checkout identities
before mutation, and deliberately damages an index, foreign key, and column to
verify physical-drift rejection. This test emits no production admission receipt.
The SQL provider lifecycle has four passing local tests for loopback binding,
cleanup on fixture failure, unsafe binding rejection, and failed startup.

The existing security validator is extended to accept fast-track PRs. Its preflight
reads the current fast-track head, and source-validation planning authenticates the
live GitHub run, exact candidate, repository, open PR, and target branch. Its result
explicitly says `source-validation-only` and `releaseAdmission=false`; it cannot
replace production readiness. Fast-track planning excludes protected-branch caches
and historical producers. Existing production mutation guards are unchanged.
The validation-resume regression suite passed 256 tests, with two additional
fast-track cache/fork tests passing afterward. Source validation can proceed before
production reconciliation, but no publication or migration is authorized by it.

GitHub run `38126624681` passed the actual disposable SQL migration and adversarial
schema tests at candidate `c84a97230c45e94d34a3ec277481129c326f3265`.
Security run `38126987829` passed its fast-track head preflight, validation plan,
and security/database gates at `00693123cfc92ddfdf0d0b403f307e9e397ba306`.

The existing production read-only workflow now waits for successful exact-candidate
fast-track source/security checks, builds the canonical observer in a credential-free
job, and observes both live applications using the existing Production Azure identity.
It reports only sanitized schema/database fingerprints and pending migration metadata,
with a durable read-only receipt and explicit `authorizesMutation=false`. Failed
physical proof remains failed even when a fallback metadata inventory is available.
Private provider responses are excluded from uploads and removed afterward.
This adds SQL schema evidence only; tenant ownership and file reconciliation remain
separate required stages. The source gate has five passing local rejection/identity tests.

Production read-only run `38127401607` authenticated successfully and preserved the
healthy Parfait/Protect runtime at `97555039d28fdeb4b2afe5c58d395ac70531d302`.
Both hosts remain running with HTTPS and cutover/preview disabled. Canonical SQL
metadata inventory succeeded for both hosts and reports exactly one pending
migration: `20261011064911_AddBusinessScopedMerchantPayments`. The four historical
manual migration stamps remain the existing audited compatibility set; they were
not rewritten. Full physical-schema observation did not yield usable proof.

The observer now reads column metadata once per table and avoids redundant column
and index queries for pending new tables already proven absent. This preserves
all physical checks while reducing remote SQL round trips. Read deadlines remain
bounded and now return explicit timeout classifications; they are never silently
retried. Full live physical proof must still pass on the corrected observer.

### Fast-track production observation and impact checkpoint

- Candidate `0b01da40f528f812a027611b4ce0f0091ca1f686` passed source validation, security validation, and read-only production workflow run `38128215802`.
- Physical SQL observation proves Parfait and Protect share the same database, baseline, and schema identities. Exactly one migration is pending: `20261011064911_AddBusinessScopedMerchantPayments`. No SQL was applied.
- The fast-track selector now derives linked-file consumers from existing application projects and uses the canonical target inventory. Shared business authorization/schema changes affect all four .NET hosts; the changed shared CMS bundle also affects Website. This is a dependency-derived scope, not blanket application selection.
- Unknown source paths still stop selection. Schema publication remains blocked until canonical migration admission, exact bundle rehearsal/readiness, durable mutation receipts, and safe deployment resume are integrated. Passing read-only observation is not mutation admission.
- No domain traffic cutover, data transfer, application deployment, or legacy retirement has occurred at this checkpoint.

### Exact bundle and resume checkpoint

- Candidate `3c1277a9b14c9ebc583ae890ef24f18c90a9bb05` passed source/security validation and production read-only workflow `38129417384`.
- The same workflow built the canonical migration component and rehearsed those exact bytes against the observed schema baseline in disposable SQL. The recovery test proves one SQL execution, one intent, and recovery after a lost success receipt without a second SQL execution.
- Retain artifact `11698628566` (`parfait-fast-track-migration-3c1277a9b14c9ebc583ae890ef24f18c90a9bb05-1`), digest `sha256:45fc755e24d09f01aaf3043e375633ccaee44853a8fb7e6198400f5ffda96127`. Do not rebuild compatible bytes or repeat completed production operations. This is rehearsal evidence, not production mutation admission.
- The isolated workflow now participates in the existing `validation-resume.py` gate model. Reuse requires authenticated successful producer steps and matching source/execution dependencies. The previously unconditional workflow generation is content-attested; its actual GitHub step results remain mandatory. Unknown planner commands, changed checkout refs, incorrect skip conditions, failed checks, or dependency changes invalidate reuse.
- Fast-track execution identity now has an explicit exact-run/current-head verifier. It does not independently authorize deployment or SQL. Production admission, migration journal integration, tenant/media reconciliation, deployment, live functional verification, and cutover remain incomplete.

### Combined published website/reporting release and recovery wiring

PR #580 now includes the entire `fix/legend-published-first-response-20261011`
branch at `4085e87ae637564cdeaa670b57412a501f48035e`, integrated at
`5157b57cdb782cc021245c9daa6e01e54d19ab10`. The additional snapshot-retirement
migration invalidates the payment-only bundle above; that artifact remains preserved.
Commit `a862a6c303b46ecf538a8c3e234222a68ab357bc` corrects the disposable SQL
fixture to exercise payment and all successor migrations together.

The existing fast-track workflow now admits exact validated merged source, restores
compatible rehearsed migration bytes and their matching observer, verifies a fresh
managed SQL restore window, and invokes the canonical migration journal. A managed
restore-window check is not a performed restore. Application ZIPs are retained before
upload; retries restore those bytes and reconcile durable provider operations.
Successful siblings remain intact. Unknown or missing mutation history never permits
blind replay. Historical protected-workflow reads reconcile shared-resource activity;
they do not make that branch a release source or execution authority.

Routing runs only after all affected applications succeed. It preserves the existing
zone/routes, variables and bridge secret; tags Worker publication with its durable
operation identity and reconciles the exact live version after ambiguous responses.
Runtime provenance, favicon and sitemap checks remain required. No DNS or commerce
cutover flags are changed by this publication path.

Local focused recovery checks: 99 release-child, 54 operation-journal, 122 application
reconciliation, 16 history-audit, 30 fast-track validation, 12 probe-package, 5 read-only
workflow and 13 new fast-track rejection/recovery tests passed. Earlier combined
browser/rendering/routing checks passed 226 tests. These are source tests, not live
lead attribution evidence. Production deployment and the controlled website → CRM →
scoped analytics → correct Meta destination → reporting reconciliation remain pending.

### SQL access repair and terminal-attempt recovery (2026-10-11)

The existing GitHub Production service principal now has Reader on the canonical
SQL server in MasterApp-Data-RG. Read-only run `38135862488`, attempt 2, verified
the observed database and recovery window. PR #585 merged at
`3fefd3ca5dd1a4431b1238f65ba0803d9aa0ef51` on `Legend/fast-track-release`.
Its production run `38149343837` admitted the exact source, restored the preserved
bundle from successful rehearsal `38132299439`, and verified recovery. The bundle
digest remains `5aff41a0bfcf1a731eef424315501c8e74318bdcc12ac0704d0eb50f050148a7`;
the fresh observed database/schema/baseline identities match the rehearsal.

Production still stopped before writes: migration admission rejected historical
run `37960039867`, attempt 2, although authenticated GitHub enumeration reports a
completed cancelled attempt with exactly zero jobs. Child admission now uses the
same complete terminal-empty-attempt proof as application operation history.
Incomplete counts, an active latest attempt, and entered earlier attempts remain
blocking. No migration, app, or routing write occurred in `38149343837`.

Validate this repair through exact-source security and isolated checks, then merge
through the same fast-track branch. Preserve the original rehearsal; the source
read-only workflow's redundant new rehearsal rejects a repair-only delta without
migrations, while production's existing compatibility verifier restores the proven
original bytes. Monitor migration admission and all five affected apps plus routing,
then verify runtime revisions and published HTML. Custom-host cutover and retirement
remain separate; controlled lead/reporting reconciliation remains outstanding.

### Approved reconciliation of older applied generations

PR #586 merged at `b724b2276b5de63d7e2fa2be8a45eb2e54156b61`; run
`38150112834` passed source admission and recovery verification, then stopped
before SQL writes on historical run `37954422758`. The whole-history audit also
identified `37926554448`. Both have authenticated retained admission/state evidence
but unresolved historical migration outcomes. Neither is reclassified as success
or nonentry. The Founder explicitly approved repairing this reconciliation flow.

The canonical child-admission owner now permits only a positively separated older
physical generation: exact reviewed original source, authenticated original package,
no retained migration intent/success, unchanged older migration/Designer files,
and fresh paired live prefix/catalog observations with settled activity, bound to
the current run, attempt, and application revision. All original migrations must
be applied and every pending migration must be strictly newer. Existing migration
intent, missing evidence, changed old SQL, active or stale observations still block.
The existing release journal continues to own first-write admission and receipts.

Focused validation: 107 release-child, 54 operation-journal, 25 fast-track,
16 history-audit, and 12 probe-package tests pass. Independent review found no
blocking source issues; integration coverage proves observation forwarding and
retained-intent rejection. Exact-head security and isolated CI remain required.
Continue the same fast-track release with the preserved rehearsal bundle, verify
SQL and all five apps plus routing, then verify live revision and public HTML.
No production writes have occurred in the failed runs named above.

The source read-only workflow also restores a compatible original rehearsal via
the same `restore_fast_track` owner and compares it with the authenticated fresh
observation. It creates a new rehearsal only when no compatible proof exists.
Corrupt evidence or a changed database baseline never triggers fallback. Reused
bytes are not uploaded under the new candidate's rehearsal name. This prevents
repair-only PRs from failing merely because their delta adds no migration source,
without relabelling old evidence or skipping the source recovery checks.

PR #587 merged at `960486d501fdf63ef8aa06659b1c3190f15dafeb` after all three
source workflows passed. Production run `38152614831` stopped before writes:
the historical admission reader required `GH_TOKEN`, while the production job
supplies `GITHUB_TOKEN`. The same whole-history diagnostic with the CLI alias
found zero unproven entries. The canonical GitHub client now accepts the Actions
token as a fallback, preserving explicit CLI token precedence and existing
permissions. Regression tests cover both aliases and absent credentials.
Both uncertain historical envelopes now authenticate using only `GITHUB_TOKEN`
in a read-only reproduction. The two new credential tests pass. The broader
lifecycle suite has 21 failures and two errors; restoring the original constructor
produces the identical pre-existing failure set. Independent source review approved
the credential fallback without changes to admission or permissions.
Resume through fast-track after exact-head validation; the preserved rehearsal
and all five pending application targets remain unchanged.

### Completed September migration generations

PR #588 merged at `e67e81b3ad9311858c48e731d2406da0ba3a8059` after exact-head
security, isolated and read-only workflows passed. Production run `38154021328`
authenticated the history with the existing Actions token and reconciled both
October unknown generations, then stopped before writes on successful September
run `36332699867`. The completed-history compatibility owner recognized only the
later inline workflow, not its two earlier source generations.

A batch inspection of every remaining possible SQL execution found nine successful
September releases requiring these two exact profiles. The existing completed-history
proof now pins each workflow to its original baseline, lifecycle and policy owners.
Those original workflows had no package-reuse path or validation-resume reader;
their timestamped local build, migration completion, exact package and aggregate
receipt remain mandatory. Two were successful Protect/Website-only releases, so
their exact original Protect deployment step is accepted instead of requiring a
Portal deployment outside their scope. Website-only success cannot satisfy this.

All nine original execution/package proofs pass the read-only reproduction with
unchanged older migration sources and strictly newer pending work. Fresh production
physical prefix/activity checks remain mandatory; no run is reclassified as nonentry.
The existing proofs for the two failed historical EF executions remain unchanged.
No production writes occurred in `38154021328`. Continue with the preserved bundle,
all five app targets and routing after source validation and independent review.

### Durable transport initialization and pre-write recovery

PR #589 merged at `3bae3fd25ce92f2140b23230f9ef94e45888c5e9` after all three
source workflows passed. Production run `38155548986` passed the full SQL
preflight. Its migration step then stopped at `mutation-admission`, before the
bundle subprocess, because fast-track installed the artifact SDK without invoking
the canonical JavaScript action that exports the runner's artifact credentials.
The retained inventory contains admission/recovery artifacts and no child intent.

Fast-track now invokes `./scripts/release-artifacts` in migration, app publication
and routing jobs. The shared historical reader recognizes only the audited
workflow/migration/journal/transport source generation with an authenticated exact
checkout, a single timestamped terminal pre-bundle marker in its failed step,
complete artifact inventory, no retained child records and skipped app/routing
publication. Both child history and unpublished-scope recovery use this same proof;
absence of an intent by itself remains insufficient. Real run `38155548986` passes
that read-only attestation. The repair chain bound is 16 rather than 8, retaining
every per-hop check and covered by a nine-hop regression.

Desired-state observation receipts also strip internal `migrationRecords` from
their public source summaries, matching the existing transport schema without
changing the internal admission evidence. Focused validation: 31 fast-track and
108 release-child tests pass. Independent review approved these changes. Resume
only through exact-head source validation and the canonical fast-track workflow;
all five app targets, the preserved bundle and routing remain pending.

### Completed SQL and first application entry

PR #590 merged at `39fc10572cb8ffc7de812d3b50eb26992367c2d1` with all three
required source workflows successful. Production run `38157422917` attempt 2
executed the preserved migration bundle. Attempt 3 reconciled and verified the
completed schema without replay. Its durable intent artifact `11709737329`
(attempt 2) and success artifact `11709078139` (attempt 3) share operation
`8e240717f3ec653f6613fc9a22edf81922d1307ea8e66fb0a9e38045cf8e1fb4`.
Both pending migrations are now applied. SQL is complete; apps and routing are not.

All five app jobs stopped in package restore before compilation or publication.
GitHub represented earlier skipped matrices with their unexpanded name; the
restore/history readers now recognize only the exact completed/skipped empty
placeholder. Missing artifacts after a successful build or entered publication
still prohibit rebuilding. Read-only package identity refresh is bounded; mutation
identity checks are unchanged.

A separate scope-recovery predicate authenticates the original workflow, every
attempt's complete app/routing nonentry inventory, and the canonical SQL
intent/success pair, including physical partition and reconciliation attempt.
It preserves unpublished app scope without claiming SQL nonentry or granting
migration replay. The real run passes this predicate and retains original scope
baseline `640a2fa3e44fe1b36ff9c12e44aa1bbd1a7f21f6`.
Fresh authenticated zero-pending source observations require no new rehearsal or
artifact; original rehearsal provenance remains unchanged. Production still
performs its fresh schema/activity verification. Focused checks: 41 fast-track,
17 migration-probe, and 108 release-child tests pass. Remaining work is exact-head
source validation, canonical deployment of all five apps/routing, and live verification.

### Trigger-only repair after PR #591

PR #591 source `1562bc1a26829c4d3db75c548c2d756cadf71ba1` passed security
`38159832216`, isolated `38159813212`, and read-only `38159813209`, then merged
at `c325428a4159bc4948bf396f81f938b6d53069c4`. Its script-only changes were
excluded by the existing push path filter, so no production run was created.
The filter now includes canonical deployment/recovery owners. Scope recovery
can traverse only an authenticated control-only merge with that exact old
workflow and a complete inventory proving no matching run. All existing
completed-SQL and unpublished-app proofs remain required for the preceding run.
44 focused fast-track tests pass; independent review approved the scope proof
and identified the corrected canonical journal filename in the trigger filter.
No SQL execution or rehearsal is restarted. All five unpublished apps and routing
remain the only production work; required changed-source CI precedes the merge.
