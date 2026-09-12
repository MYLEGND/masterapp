# Chat navigation and request latency repair — 2026-09-11

## Scope and production evidence

Candidate is the existing isolated `repair/call-experience-20260911` worktree, continuing from `74a8715f`. Main Mac production checkout and its unpublished work remain preserved. These changes have not been deployed. They are not proof of zero network latency or app-wide production performance.

Read-only Application Insights inspection on September 11, using the preceding two-hour window, found:

| Request | Samples | Failures | Median ms | P95/max ms |
|---|---:|---:|---:|---:|
| Web messaging inbox | 17 | 0 | 800 | 38,951 |
| Mobile chat detail | 6 | 1 | 583 | 11,300 |
| Mobile inbox | 5 | 0 | 2,479 | 8,830 |

The sample is small and uncontrolled. This is production baseline evidence, not a candidate benchmark. The slow web inbox had 30 SQL dependencies totaling 31,416 ms (longest 27,433 ms). A completed 3,000 ms mobile detail request had 125 SQL dependencies, with 2,584 ms in translation presentation. The failed 11,300 ms detail request failed in `conversation_query` after 7,844 ms, with `SqlException` whose safe classification indicates cancellation. Whether client navigation, a deadline, or another cancellation source caused it is not established; this is not evidence of a database timeout. No private message bodies, SQL text, credentials, or raw exceptions were exported.

## Confirmed causes and repairs

- Explicit detail endpoints were already bounded, but the default `GetConversationAsync` used by reopen/mutation flows requested unbounded history. It now shares the 60-message default (maximum 80). A one-row lookahead determines older-history availability and is removed before translation, attachment, and social-card projection.
- A redundant shared-post source query has been removed by retaining the source identifier in the existing selected message rows. Visibility still passes through the existing social authority.
- Language-pair eligibility previously performed three database reads. The existing registry now queries current source/target eligibility and the directional pair together. No process-wide policy cache was added. Pages with no incoming text or incoming quoted originals do not perform translation preference/cache work.
- Timestamp-only pagination could omit messages sharing the boundary timestamp. `BeforeMessageId` is additive and sent by web, iOS, and Android. The server preserves database GUID ordering rather than re-sorting SQL Server GUIDs using .NET's different ordering.
- Web restored chat loading waited for inbox and recipient enumeration. Detail now starts independently; available content or a known-identity shell renders synchronously. Obsolete detail requests are canceled and responses remain guarded by navigation/scope/revision.
- The shared external SignalR script blocked parsing and handler registration. It now loads asynchronously; all three existing consumers handle already available, late, and unavailable SignalR. Messaging retains its existing polling fallback. No second loader or library was introduced.
- Android retains a bounded set of details within its existing account-owned messaging ViewModel. Both native apps show known conversation identity before the network completes. Group navigation no longer waits for an inbox refresh.
- iOS pagination overloads were absent from the protocol requirements, allowing existential dispatch to the default implementation that ignored the cursor. The requirements and concrete transport now carry the history/inbox cursor correctly.
- Web middleware repeated assistant identity reads during each authorized request. It reuses the binding result within that request while preserving disabled, guest, impersonation, and route checks; nothing is cached across requests.

## Boundaries

Temporary network errors may retain previously available content. Explicit authorization/removal responses evict inaccessible content. Profile, language, and realtime changes invalidate affected state. Supported translations still use existing entitlement, metering, retained-result, provider, and governed learning authorities. Uncached translations remain real dependency work; the repair does not show untranslated content as a successful translation.

Wi-Fi/cellular latency is not eliminated or combined by assumption. No transport bonding, paid network service, stale authorization shortcut, generic page cache, or timeout-based success substitute was added. Shared backend, registry, and startup changes benefit their existing consumers and future consumers using those authorities; arbitrary future pages still require correct integration.

## Verification

Focused backend regression: 173 passed, 0 failed/skipped (`/private/tmp/legend-chat-latency-focused-v2/focused.trx`). Web behavioral/startup regression: 40 passed (`/private/tmp/legend-chat-web-final.log`). Full iOS MobileNativeContractTests: 63 passed (`/private/tmp/legend-native-contract-full-ios.log`). Android emulator LegendMessagingNavigationTest: 6 passed (`/private/tmp/legend-native-navigation-android.log`).

The first combined backend run exposed two web implementation-string assertions: one assumed restored chat must wait for inbox, and the other prohibited AbortController anywhere in the script despite only governing recipient search. The separate web review updated those implementation assertions while retaining known/returned recipient-scope checks and the non-aborting recipient-search requirement. Behavioral tests explicitly reject cached/uncached out-of-scope details. Native held-out prompts, expected answers, thresholds, and exclusions were not changed.

Full final candidate outcomes are recorded below after execution. Relational SQL generation is checked for SQL Server and SQLite; local SQLite paging execution is not live SQL Server validation. Native held-response tests verify immediate state/navigation and obsolete-response handling, not production latency. Physical-device, weak-network, and authenticated candidate production-equivalent latency measurements remain required before asserting the requested speed target.
