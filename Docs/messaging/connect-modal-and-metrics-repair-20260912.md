# Legend Connect modal and live-metrics repair

## Candidate and boundaries

Worktree: `/private/tmp/legend-modal-content-region-20260911`, branch `repair/modal-content-region-20260911`, based on production `5cf3879b320a5e8bf4cd3670c7e651a40fa3523d`. Changes remain unpublished. Native artifacts built by the user are preserved; no additional mobile release build is required for these web/backend changes.

## UI repair evidence

All 13 retained inspection sections now have direct modal launchers. Shared modal positioning measures the visible header/footer and constrains the dialog to the content region. Translation account cards use a responsive grid; nested dialogs retain their parent, scroll, search, and focus. Existing translation, authorization, form, and inspection contracts remain in place. Static UI copy uses the existing canonical manifest generator.

Executed before the backend metrics repair: 16 Node navigation/layout tests passed; 46 focused .NET tests passed. Full suite: 2,520 total, 2,507 passed, exactly nine existing failures with identical failure-message hashes, and the same four skipped tests. Evidence: `/private/tmp/legend-modal-followup-full/full.trx`. This is regression equivalence, not a fully passing native AI suite. Browser visual verification remains unexecuted because no browser connection is available.

## Production metrics reproduction

User Page Health recorded browser TimeoutError at 20,001–20,002 ms on GET `/founder/legend-connect/live-metrics`. Azure Application Insights independently recorded six requests between 05:15:06 and 05:18:27 UTC September 12, 2026, each returning 503 after approximately 19.8–21.6 seconds.

Representative operation `374f1083ba38e54d6edca0d4951f0366` began at 05:15:58.804 UTC, lasted 20,608.94 ms, and recorded TaskCanceledException in `Infrastructure.Messaging.LegendConnectOperations.LoadStateAsync`. The controller maps the exception to 503. The browser aborts before receiving that response.

First demonstrated excessive-work boundary: `FounderLegendConnectService.GetLiveMetricsAsync` calls the historical full dashboard operation, which materializes corpus state through `LoadStateAsync`, even though the browser needs counters. The initial page uses the narrower Founder shell, but recurring metrics still use the historical dashboard path. SQL dependencies confirm reads of text units, alignments, and candidates. This establishes the affected stage; it does not establish every underlying SQL/CPU timing individually.

The retained learnedPatterns list includes older incidents and must not be interpreted as concurrent current failures. This export identifies Production; Page Health uses Local for localhost.

## Verification still required

Backend repair tests and same-route authenticated production timing must follow implementation. No increased browser timeout, fabricated zero metrics, hidden errors, provider-policy bypass, or release-gate waiver counts as repair.

## Implemented backend repair and focused verification

The existing LegendConnect operations authority now exposes scalar dashboard counters; the Founder live endpoint uses these instead of the full dashboard. SQL aggregates replace corpus/relationship/alignment materialization. Failed candidate normalization retains the existing Unicode semantics via a narrow streamed projection. Duplicate normalized identities preserve the prior failure behavior. Provider capacity continues through the existing policy-bound capacity authority. Translation quality summary reuses the existing counting implementation without creating review text/evidence rows.

Focused result: 7/7 passed, zero skipped (`/private/tmp/legend-live-counters-tests/live-counters.trx`). Coverage includes relational SQLite equivalence with the full dashboard, bounded projection shape, native-only registry access, cancellation, duplicate identity behavior, quality counts and review limits, and the existing live metric formatter. This proves local projection behavior, not production SQL execution or post-deployment latency.

The full combined regression run is recorded separately at `/private/tmp/legend-modal-metrics-full/full.trx` when complete. Browser timeout remains 20 seconds and unavailable dependencies remain visible.

## Shared diagnostic lifecycle hardening

The existing shared Page Health owner no longer opens the drawer for each polling failure. The live badge and report still retain real failures. Caller cancellation is distinct from TimeoutError; the translation-limits deadline now supplies an explicit TimeoutError reason. A successful retry clears only an older matching method/path/query failure. Overlapping request outcomes retain their ordering state until every request for that scope settles, so unrelated requests cannot erase or resurrect a failure. HTTP history remains retained after current recovery.

The AgentPortal Explore menu now invokes that existing owner instead of scanning the entire document, relocating its root, and overriding its placement. Both web layouts and the client workspace already reference the same shared partial before page content. Future pages using those layouts receive the behavior automatically; this is not proof that every endpoint is defect-free.

Additional backend work replaces full candidate materialization with readiness aggregates and distinguishes Azure authentication, authorization, missing-resource, throttling, and service-outage responses without exposing response bodies. Focused backend tests: 42 passed, including a 1,000-candidate relational fixture. Diagnostics behavior tests: 13 passed; safe checkout synchronization: 10 passed.

The full hardening run recorded 2,534 tests: 2,520 passed, 10 failed, 4 skipped. One additional failure was an implementation-string assertion referring to the old transient-only resolver name; both resolver assertions were updated to the sequence-aware method without removing requirements. The 13 behavioral tests cover the expanded behavior. The other nine are compared against the retained baseline; the corrected contract is verified separately before publication. No native expected answers were changed.

## Local execution coverage and outstanding access

AgentPortal is running from this worktree on https://localhost:6205. Anonymous home and messaging requests redirect 302 to existing Microsoft sign-in. Shared JavaScript/CSS return 200 with correct content types. These checks do not count as authenticated page validation. Browser selection reports “No browser is available”; connecting the browser is required for the agent to exercise the signed-in Founder pages and collect the two current Local diagnostics directly.

ClientApp's configured localhost port 5221 is unused. Its Development startup automatically invokes migrations; startup has not been launched against SQL Server until its pending-migration state is verified. Current local legacy-avatar storage contains no matching GUID-named image inputs. No application database credentials were substituted for SELECT-only validation. No deployment occurred.

Corrected diagnostics contract verification: 9/9 passed, zero skipped (`/private/tmp/legend-diagnostics-contract-final/page-health.trx`). The full-run comparison confirmed that the only extra failure was the corrected resolver-name assertion; the nine native failure messages and four skipped cases are unchanged.
