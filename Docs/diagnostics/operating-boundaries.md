# Founder diagnostics: operating boundaries

## Authorities

Runtime observations use the existing shared Page Health observer, native HTTP clients and server exception pipeline. The shared Infrastructure store owns normalization, classification, deduplication, admission and persistence. The existing Founder authorization protects incident reads and review. The existing software-remediation service owns GitHub writes; the existing protected production workflow owns release.

A reported failure is evidence, not an instruction. Client-supplied identities, revisions and locations are untrusted. Network, cancellation, authorization and dependency failures are not automatically confirmed code defects. No classifier can guarantee zero false positives. Confirmation requires reproduction or equivalent independent evidence.

## Privacy and reliability

Public web ingestion requires antiforgery and bounded admission. The static website uses an exact approved origin and the existing API host with a diagnostics-only credentialed CORS policy. Native ingestion requires the existing delegated mobile identity. Neither endpoint returns incidents. Do not put user messages, cookies, tokens, request bodies or URL query values in events. The server normalizes route metadata and rejects arbitrary source locations. Founder pages render retained content as encoded text.

Collection must never replace an original application exception or interrupt messaging. Reporting failures have bounded retries and cannot recursively report themselves. Counts represent received observations, not exactly-once unique crashes. Limits currently apply per application process. Expired records are hidden immediately; physical deletion is bounded and opportunistic.

## Repair lifecycle

Review distinguishes Observed, ConfirmedDefect, Dismissed and ManuallyClosed. Manual closure is not a verified fix. Recurrence changes the review version so stale browser forms cannot close a newly observed failure. No observation automatically launches a paid model, writes code or promotes a repair.

Prepared source proposals require exact base identity, Founder confirmation, bounded existing source files and the existing configured GitHub App authority. Preview commits stay on hotfix/staging-batch or a cycle-specific child ref after a verified deployment archive. That ref is excluded from the production workflow even if its preview PR is made ready outside LEGEND. Publication must use a separate immutable reviewed publication ref through the same protected workflow. Neither the dashboard nor the model may directly merge or deploy.

An ambiguous GitHub write remains locked. A failed HTTP response is not proof that the write did not occur. Do not retry, delete a branch, or mark a repair deployed without reconciling exact remote evidence. Workflow success alone is not proof that a functional failure is fixed; post-release reproduction and exact running revision are required.

## Deployment and coverage limits

This change needs the generated database migration and normal web deployment. Native changes need new iOS and Android builds. A backend deployment cannot update installed native code.

Project-impact discovery is read-only and derives projects/dependencies from build metadata. Unknown changes broaden the impact set. It does not authorize deployment or invent credentials/hosting for new directories. Read-only impact reporting runs within the existing workflow. Targeted build/deploy skipping remains outstanding; current release gates are preserved. Static Legend-Website reuses the shared observer and protected API bootstrap; the four .NET hosts and existing mobile transports are wired.

Automated evidence-to-patch execution, independent sandbox reproduction, automatic recovery of uncertain writes and verified functional repair closure remain unfinished. Batch archival requires approved configured verification hosts, exact reviewed/merged tree identity, a successful scoped deployment receipt and matching loaded source from every configured host; it never closes a diagnostic incident. Current UI accepts explicitly reviewed proposals, not automatic AI guesses. No external model calls or new paid resources are included.

Handled native failures are supported. Android uncaught capture is best effort and preserves the existing handler. Swift fatal traps, OS kills, offline delivery and physical-device recovery are not claimed complete. Sanitized native capture currently sacrifices arbitrary stack details rather than collecting private information. Source maps/symbolication require a protected artifact path before richer crash context can be trusted.

Live qualification must establish the GitHub App PR author is eligible under the existing release gate. Do not loosen the author-association gate to make this pass. An unsigned/local build does not establish live deployment or real-device capture.

The Founder explicitly authorized the September 18 direct build/deploy exception. That release preserves source-equivalent rollback packages, candidate-only additive migrations, focused checks, and exact live revision verification. Future dashboard publication continues through the existing protected release authority.

## Permanent bounded performance evidence

The canonical RuntimeDiagnosticStore remains the only durable incident sink and
FounderDiagnostics the only management UI. Existing FounderOnly/FounderGuard,
antiforgery, client origin checks, incident review concurrency and 30-day expiry
remain unchanged. No new provider, export, logging switch or paid monitor is used.

Server performance is in-process only (`JsonIgnore` on the public event DTO).
The same incident carries a nullable `PerformanceEvidenceJson` envelope, at most
4 KiB UTF-8, with server sample time, **observed** trace and bounded fixed-vocabulary
metrics. App and deployed revision are derived by the existing server sanitizer.
Incoming traceparent may control the observed trace; it is never identity,
authorization, tenant binding or incident deduplication authority. No SQL, SQL
parameters, URLs, headers, body/content, owner/customer identifiers or credentials
are added. Numeric timing and trace values are excluded from deduplication.

A newer retained sample replaces the previous sample by timestamp-ordered
compare-and-swap on the bounded existing column. Out-of-order/equal-time arrivals
and concurrent losers cannot overwrite a newer winner; they are counted as samples
not retained, without a retry loop. This is source-clock ordering, not proof of
global causal order or capture completeness; a later rate-limited occurrence does not erase it or pretend it is a
new sample. Read `sampledUtc`, not incident LastSeen, for sample freshness. Historical
rows without evidence are explicitly unknown. Application rollback leaves the
nullable column and existing samples in place until normal expiry; Down migration
is destructive schema rollback and is not the ordinary application rollback plan.

| Boundary | Source implementation / limitation | Acceptance owner |
|---|---|---|
| Requests, exceptions, status, cancellation | Existing middleware measures through downstream response serialization; ordinary fast caller cancellation remains non-error. Transport after middleware return and upstream queue wait are not measured. | Shared middleware; response/exception/cancellation parity tests |
| Database | Central DbContext interceptors observe sync and async open, command return and reader disposal. Reader lifetime includes consumer work, not only database time. Pool vs network vs server wait is not distinguished. No reader wrapping or provider behavior changes. | Existing EF authority; real SQLite sync/async tests |
| External HTTP | Factory handler measures sync/async header/handler completion, including failures/cancellation. Response-body consumption and non-factory clients remain unobserved. | Existing HttpClient factory; response identity/cancellation tests |
| Studio/publication | Shared finite stages cover Manage, mutation, save, validation, compilation and commit. Former Manage-only warning emitter is removed. Background publish/retention uses the same scope/store. Other workers can use the same finite scope without creating a telemetry service. | Existing controllers/compiler/worker; stage parity tests |
| Startup/readiness | Existing provider event logs and runtime-provenance remain authoritative. Pre-DI failures, host crashes and fatal process failure cannot be captured reliably by in-process diagnostics. | Existing hosting/deployment owners; explicit platform gap |
| Browser/loading | Existing bounded page-health error/loading observer, exact-origin/antiforgery ingestion and client source disclosure are reused unchanged. Browser claims do not become server timing; Website remains cross-host client evidence. | Existing public website diagnostics privacy tests |
| Capture health | Current-process store counters expose recorded/rate-limited/persistence-failed counts and last successful record. They reset at restart and do not establish other hosts' pipeline health. Diagnostics writes and fallback capture logging are suppressed from dependency collection. | Existing store; failure/recursion/rate tests |
| Founder controls | Same view/layout, read-only filters and coverage/settings disclosure. No new retention/access/logging controls. | Existing FounderOnly/antiforgery tests |

Performance sampling starts at 10 seconds and is capped at one per minute per
process within existing event admission (server100/global300/client20-key per
minute). At most16 distinct stage labels,32 concurrent stage handles and four
dependency kinds are retained; repeated stages aggregate and saturation is marked
truncated. Stage/dependency durations overlap and must not be summed as causal
wall-clock attribution. Process pressure is a snapshot, not a CPU root-cause claim.
Missing metrics are not zero or healthy. Bounded existing database work may have
metered impact; this is not a zero-cost promise.

Source configuration, executed capture and retained live readback are separate
acceptance states. Deployment alone does not prove live diagnostics in every host
or a permanent repair of the earlier Studio timeout. Use representative normal
flows without synthetic production load or customer transactions; preserve exact
revision and report unavailable/gap evidence rather than inventing healthy status.
