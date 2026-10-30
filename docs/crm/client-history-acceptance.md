# Client Activity & Case History acceptance

Status: implementation in progress. No release or live acceptance is asserted by this document. Diagnostics and CRM form one application candidate; the nonpublishing tooling prerequisite is separate.

## Submission coverage inventory

| Supported surface | Completion boundary | Canonical evidence | History linkage | Verification still required |
| --- | --- | --- | --- | --- |
| Life, Term Life, Whole Life, Final Expense, Mortgage Protection, IUL | LifeQuoteController POST aliases | WebsiteLeadIntakeLink.SnapshotJson, including submitted model and separate disclosure state | WebsiteLeadSubmission transaction and WebsiteLifeLeadCaptureService | Each alias through actual controller and committed database |
| Auto | AutoQuoteController.Auto POST | Same; includes repeated drivers/vehicles rather than summary counts alone | Same | Actual controller, repeated and conditional fields |
| Home | HomeQuoteController.Home POST | Same | Same | Actual controller and authorization |
| Commercial | CommercialQuoteController.Commercial POST | Same | Same | Actual controller and authorization |
| Disability | DisabilityQuoteController POST | Same | Same | Actual controller and authorization |
| Dental / Vision / Hearing | DentalVisionHearingQuoteController POST | Same | Same | Actual controller and authorization |
| Risk / needs assessment | RiskAssessmentController POST | Same | Same | Actual controller and selections |
| Public and internal inquiries | WebsiteInquiryAuthority | Existing inquiry metadata; not an insurance application or independent authorization | Shared submission boundary | Both authority paths and unresolved ownership |
| AgentPortal lead submission API | API/LeadSubmitController | Existing submitted lead metadata; no invented form wording | Shared submission boundary | API transaction, owner resolution and alternate route regression |
| Confirmed receptionist inquiries | ReceptionistInquiryAuthority | Existing restricted authority and privacy rules | Existing explicit handoff; website fallback intentionally excludes confirmed voice | Original privacy assertions remain mandatory |
| ClientApp | Controller inventory identifies resources, production, profile, subscription, finance and booking operations, not a carrier application completion handler | Existing stores remain authoritative | No inference that profile/payment/booking means insurance application completion | Review inventory against routes/views and supported external integrations |
| External carrier evidence | No accessible carrier submission feed established | Original PDF in existing private document storage; explicit human source/completeness | Immutable ProtectedDocument version receipt and authenticated download | Implemented; actual route, durable production storage and restore acceptance pending |

Snapshots include validated model answers and shared server authorization text. New browser submissions also supply labeled browser-reported question wording, choices, order, repeated fields, and presented/unanswered/unchecked states; this is not independent proof of viewing or a signature. Missing historical evidence stays explicitly unavailable. Full controller/browser route coverage remains unverified.

## Required behavioral acceptance

| ID | Risk | Current evidence / remaining check |
| --- | --- | --- |
| A | Every supported completed form creates a linked tile | Capture boundary implemented; full route matrix not yet executed |
| B | Different forms, repeat submissions and same-day notes remain distinct | Capture/store regression source and synthetic SQLite probe; exact candidate execution pending |
| C | Retry, callback and uncertain-save idempotency | Stable source IDs and immutable retry snapshot regression source; concurrent integration check pending |
| D | Failure recovery without lost answers or duplicates | Same transaction and rollback test source; full submission failure injection pending |
| E | Later changes cannot rewrite original evidence | Immutable history and snapshot retry tests; preserved form-definition test pending |
| F | Multi-select, repeated, conditional, unanswered, false/zero, authorization evidence | Model snapshot and safe rendering implemented; full presentation-state contract pending |
| G | Conversion preserves identity and full history | Reciprocal mappings and conversion event; both conversion route integration tests pending |
| H | Ownership, tenant isolation and audited association correction | Current-owner checks, same-owner audited correction, reassignment provenance and an owned-unlinked-submission queue implemented; direct native-client association and an explicitly verified Founder-only unresolved-owner repair path are implemented; exact route/CI acceptance remains pending |
| I | Direct endpoint human/AI/file/export denial | Server resolution and restricted-AI denial implemented; full controller/access regression pending |
| J | Timezones, date ranges, equal timestamps, unknown date and stable pages | Range/grouping/filter implementation; boundary/pagination regression pending |
| K | Corrections, backfill, holds, export and restore | Append-only store, revision imports, authorized snapshot export, manual evidence references, Founder-controlled preservation instructions and synthetic restore implemented; protected PDF version integration is implemented; current tests and production restore/rehearsal remain required |
| L | Desktop/mobile/keyboard full response access | Tile buttons and scrollable read-only dialog; real browser verification pending |

## Executed-test authority

The existing architecture-validation `crm_regressions` lane selects `WebsiteLifeLeadCaptureServiceTests` and `CrmHistoryTests` in addition to its existing classes. It emits TRX and checks that both required classes actually produced passing results. The Parfait suite alone is not evidence for these classes. A build, static discovery check or standalone SQLite probe must not be reported as full xUnit execution.

## Migration and recovery

`20261030070000_CrmRelationshipHistory` adds the history table and extends intake associations with an optional native-client foreign key, a nullable Workstation lead key and an exactly-one-association check constraint. Existing serialized CRM data, intake snapshots and legacy self-note tables remain intact. Import uses deterministic original identities and preserves missing historical evidence. Older application revisions assume a required Workstation lead ID: rollback to those revisions is not yet established safe after native-client links exist. App rollback must retain the new table and filed records; the migration refuses a destructive down operation. Exact schema observer admission, rehearsal, concurrent rollout compatibility and synthetic restore verification remain required before release.

## Local implementation checks

- Strict AgentPortal Release build: zero warnings/errors after the evidence and history changes.
- Compiled SQLite probe: distinct submission identity, immutable retries, same-day history, rollback, immutable overwrite/deletion denial, legacy revisions, reciprocal conversion, direct human/AI detail/search/export denial, DST date boundaries, and stable snapshot pagination under historical imports passed.
- SQL Server design-time comparison: no CRM table differences between runtime model and frozen migration snapshot; no database connection or migration execution occurred.
- Submitted-answer probe: false/zero and unchecked consent stay unchanged; browser presentation and server disclosure remain separate.
- JavaScript unit test: false, zero, repeated/unanswered/unpresented controls and exclusion of hidden antiforgery tokens passed.
- TRX guard fixtures: missing required class and failed class results are rejected; a failed dotnet test exit is propagated.

These are focused checks, not full xUnit execution, independent acceptance, migration rehearsal, live route verification, or a release claim. Earlier standalone probe runs emitted five assembly-reference conflict warnings. The current harness pins the already-cached application runtime dependency versions and now builds with zero warnings; it does not replace the actual xUnit runner. The local Protect restore cannot resolve Azure.Extensions.AspNetCore.Configuration.Secrets, MailKit, MimeKit, or Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation from the available package cache. Exact existing CI execution is still required.

## Additional implementation evidence and remaining boundaries

Server-issued disclosure tokens use the application's existing data-protection authority and bind original wording to the form model and authorization field. The token itself is not stored in history. Older submissions without this evidence are explicitly labeled; current wording is not represented as proof of what was originally presented.

Client history navigation uses stable profile IDs. The SQL-based alternate-key conversion explicitly files old metadata and the conversion event in the same existing transaction. Current relationship ownership, rather than historical intake ownership, controls access after reassignment; original owner provenance is retained. Same-owner correction files audit entries on both relationships and does not expose reassigned answers through the old association.

Manual external evidence remains human-attributed, not a connected carrier feed or independently captured signature. Original PDF versions use the existing private agent-document storage configuration, a separate immutable relationship namespace, SHA-256/length receipts, protected downloads, explicit completeness labels and linked later versions. Ordinary agent-document deletion cannot reach these versions. Preservation instructions use the existing FounderOnly authority and never confer deletion/disposition permission. Ordinary deletion of a relationship with filed history is rejected.

The existing validation-planner suite executed 638 tests successfully. The two required CRM xUnit classes are selected in the existing architecture lane, but have not been executed for this candidate. All A–L acceptance remains open until actual route tests, qualified independent review, migration admission/rehearsal and desktop/mobile/live verification complete. The preserved nonpublishing bootstrap must not be expanded to bypass its exact-source controls.

## Native-client and unresolved-owner continuation

Lead-only list and marketing consumers explicitly exclude native-client links. Analytics fallback only maps a lead when exactly one submission link exists; multiple submissions must use exact source identity rather than guessing a latest match. The validation planner selects all four managed consumers for the changed intake entity.

The restricted unresolved-owner queue returns receipt identifiers only. Repair requires an existing canonical tracking profile, explicit human verification and reason, and an immutable actor-attributed audit in the same transaction as canonical capture. It does not infer identity from contact details or overwrite an already-owned source. Original submitted form evidence is protected while the source waits for ownership repair; sibling analytics metadata remains editable.

These additions require fresh exact-source verification. No live or independent acceptance is asserted by this checklist.

Final local continuation evidence: strict AgentPortal build 17 passed with zero warnings/errors; compiled probe run 13 passed all fourteen groups including native-client association, pending evidence overwrite/deletion rejection, explicit unresolved-owner repair/retry/audit and restricted-AI Founder denial. SQL Server design-time comparison covered both changed tables. The validation planner now passes 639 tests, including all-consumer/schema selection for the intake entity. The form-evidence Node test passes. These are not execution of the two required CRM xUnit classes; exact authorized CI and A–L acceptance remain pending.

## Independent review corrections after v85

Association corrections now require an expected version and compare-and-swap under a serializable transaction covering current ownership resolution. Owner repairs use a concurrency token and the same transaction boundary. Existing exact retry receipts remain confirmable when new native-client associations are disabled. Client-access grant/revoke is observed as immutable provenance; former-owner legacy reflections are imported before access is removed without granting cross-owner read access. Detail reads, including moved-association provenance, and document downloads are audited.

Chronology merges ordered instant/date-only/unknown streams in the existing display timezone. Within a known calendar day, known instants appear newest first, followed by date-only records explicitly lacking time; unknown-date records form the final bucket. Recorded-time mode orders and groups by recorded time. Monday remains the week start. No fabricated event instant is assigned to date-only records.

Radio capture separates the radiogroup question from option labels, codes and selection state. The detail view foregrounds validated server answers and labels browser-reported presentation separately. Known UTC event time is now an optional input; unknown time stays unknown. Form tiles carry captured form name, definition version when retained, and actual authorization evidence summary without answer previews.

Existing browser reminders consume a permission-scoped read-only projection of filed open follow-ups/complaints. The original browser notification opt-in and daily deduplication remain in place; there is no new external messaging or independently editable task copy. The projection returns up to 500 reminders and reports truncation. Relationship history remains the complete task authority.

Interaction, association, preservation and evidence uploads retain scoped request identities across uncertain responses/reloads. Binary retries require the same SHA-256, length, filename and metadata; users reselect the original file after reload. Protected PDFs are conditional-create only, integrity-checked on download, and export includes version receipts and authenticated download paths. The JSON export does not embed PDF bytes or attest that each object was re-read at export time. Production uploads fail closed when neither a private document container nor an explicit durable document root is configured.

## Compatible rollout and rollback boundary

1. Keep `CrmHistory:NativeClientAssociationsEnabled` absent/false while applying the reviewed migration. Existing intake rows keep their non-null Workstation IDs; the new association version defaults to zero. Rehearse this exact migration through the existing trusted observer/admission route, including active submissions and backup/restore.
2. Deploy all affected managed consumers (Portal, Client, Protect and Parfait) with nullable-association support, preserving the same combined application release. Verify runtime revisions and current behavior before activation. Record the exact compatible application revision as the rollback floor.
3. Rehearse native-client association, conversion, access denial and file recovery with approved synthetic fixtures. Enable the switch only through the existing governed configuration/release action after these receipts exist. No activation or production configuration change has occurred in this implementation session.
4. After any native-client association exists, rollback only to a verified revision at or above that compatible floor. Turning the switch off blocks new native associations while preserving reads and exact retry confirmation; it does not make pre-CRM binaries compatible. Retain history, snapshots and protected evidence objects. Do not execute destructive Down migration. A failed migration or storage operation must be reconciled before a bounded retry.

These steps are a plan awaiting exact environment admission/rehearsal, not evidence that rollback, migration, production storage, or release has already been accepted.

Current frozen-source local evidence: strict AgentPortal build 21 passed (zero warnings/errors); expanded compiled probe run 19 passed all 18 groups, including protected PDF retry/conflict/access and storage-success/database-rollback recovery. The probe build also has zero warnings/errors after aligning cached runtime dependencies. Two form-evidence JavaScript tests and five focused validation-planner mapping tests passed. Prior 639-test planner success is preserved; full CRM xUnit and end-to-end A–L acceptance remain pending.


## Follow-up after executed candidate 70872488

Architecture run 41066834355 actually selected and executed the required CRM classes: 59 total, 56 passed, 3 failed. The failures were a missing synthetic Email, ambient test identity contamination, and deletion of retained evidence reaching the immutable guard. Parfait run 41066834418 executed 1116 tests (1114 passed, 2 failed); it remains insufficient proof for the two required CRM classes. Prior statements above about unexecuted xUnit describe earlier checkpoints, not this baseline.

The follow-up repairs those fixtures, retains original privacy assertions, and makes protected single/bulk/bucket deletion return conflict before staging removals. Restricted receptionist fixtures now include the history entity; same-email independent submissions retain distinct identities. The native disclosure fixture uses explicit synthetic non-authoritative tokens and still rejects unknown expressions.

New controller-boundary coverage targets all 12 insurance POST handlers with real SQLite persistence/capture, original disclosure snapshots and retry identity. These tests are not yet executed, and do not establish hosted routing, model binding, antiforgery or browser acceptance. Actual conversion-route failure injection and authenticated desktop/mobile acceptance remain open.

The detail dialog now rejects stale responses after close/reopen and has an accessible name. Three Node regressions passed. The exact migration observer and disposable synthetic rehearsal cover the ordered diagnostics-plus-CRM suffix; offline compatibility/descriptor checks passed, while actual SQL rehearsal and release admission remain pending. The proposed CRM runtime compatibility pair pins the exact base and candidate infrastructure closures; inherited tools, execution identity, independent review and protected promotion remain required. No combined application deployment or live verification is asserted.

The follow-up also adds actual shared-submission failure/retry and three conversion-handler paths, with failures injected before creation commit. These C# cases remain unexecuted. Strict AgentPortal build23 passed with zero warnings/errors; six Node checks passed; migration descriptor and compatibility harnesses passed. Package orchestration tests (53) mock the SQL/provider boundary and are not SQL rehearsal evidence. Release-policy tests passed206 cases with pinned historical bootstrap blobs unchanged.

## Executed candidate 2c693e57 and next corrections

Architecture run 41074246575 compiled the full test project and actually executed 80 CRM-lane cases: 65 passed, 15 failed. The new conversion and submission rollback/recovery cases passed. Twelve form-handler cases failed in test setup/assertion plumbing (missing URL helper and default Pascal-case serialization); three protected bucket-deletion cases selected no rows because their fixtures retained a different default original product. The next source corrects those fixtures while retaining the expected conflict and immutable evidence assertions. The Meta lane passed 781 cases and failed one readback-corruption fixture, which now simulates out-of-band SQL deletion instead of asking normal SaveChanges to delete immutable evidence. Website regressions passed 511 cases. These failures have not yet been rerun for the corrected source.

Parfait run 41074246593 passed all 1116 application cases and 149 payment-core cases. Its disposable SQL fixture failed with SQL5074 while restoring the old required lead column. The fixture repair temporarily drops and recreates the exact original foreign key/index and preserves cascade semantics. Production migration operations remain unchanged. The repaired rehearsal source builds with zero warnings/errors; actual SQL execution remains required.

The next source adds a hosted synthetic browser regression through the existing CRM lane. It uses actual CRM controllers, authorization checks and the compiled feature view, rendered without unrelated application-shell dependencies. Desktop/mobile viewport, keyboard open/close/focus, long answer/disclosure scrolling and a two-record Today filter are asserted. This test has not yet executed and is not full-shell or live acceptance. Discovery now requires exact passing result counts for all twelve form-handler cases, five conversion cases, one submission recovery case and the hosted browser case. Unit tests of that discovery guard use synthetic TRX and are not application execution evidence. A tool-only baseline may omit the complete CRM feature, but any partial presence fails closed and the existing WebsiteLifeLeadCaptureServiceTests remains mandatory.
