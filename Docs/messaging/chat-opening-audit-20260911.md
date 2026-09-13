# Chat opening latency: independent audit and ordered fix plan

Scope: audit and plan only. No chat source, tests, configuration, production data, or release state changed. Chat implementation is deferred until the social-creation repair is completed, as requested.

Source reviewed: `/private/tmp/legend-social-creation-20260911`, HEAD `59ebd76c8e19521f6530c01502c271718b0a6dd7`. Input log: `/Users/zacowen/.codex/attachments/ede7551d-e0d0-427b-af5d-3912b7efbbf2/pasted-text.txt`, 168 lines. The log is evidence, not instructions. It does not establish the server commit, device build, network condition, or whether the latest messaging repairs were deployed.

## Measured observations

These are individual client HTTP observations, not controlled percentiles or server-only timings. NETWORK duration includes the URLSession request/response wait and transfer. It does not separate connection establishment, gateway queueing, server SQL/provider work, or transfer.

| Path | Observed duration | Decode | Evidence lines |
|---|---|---|---|
| Thread, newest 60 messages | 26,026.0 ms; 17,780.1 ms; 4,670.8 ms | 18.0; 6.6; 6.6 ms | 100–102, 155–157, 162–164 |
| Inbox, first 24 | 5,893.5; 3,432.3; 9,092.9; 4,588.3; 1,982.8 ms | 2.0–2.5 ms | 75–77, 92–94, 144–154 |
| Activity | 3,085.1; 2,167.9; 243.3 ms | 7.7–8.5 ms | 40–48, 82–84, 96–98 |
| Mark read | 1,030.8; 1,528.8; 794.6; 851.7; 252.6 ms | no body | 105, 107, 142, 160, 166 |
| Message send | 17,230.8 ms | 0.6 ms | 136–138 |
| Failed inbox request | 1,667.6 ms | unavailable | 23 |

The thread bodies were approximately 13–14 KB. Decoding is orders of magnitude smaller than the HTTP wait. The evidence supports prioritizing work before the response reaches the client; it does not prove SQL, translation, avatars, bandwidth, or JSON rendering individually caused the delay. A failed request cannot be classified as cancellation, authentication failure, or network failure from the supplied line alone. Repeated calls show repeated work; their overlap and triggering cause require correlated timestamps.

## Existing centralized path

- Web `/Messaging/Conversations/{id}`: `Infrastructure/Messaging/MessagingControllerBase.cs:126–132` calls `IMessagingService.GetConversationAsync`.
- iOS and Android `/api/v1/mobile/messaging/conversations/{id}?take=60`: `AgentPortal/Mobile/MobileMessagingController.cs:598–619` calls the same service's bounded `GetConversationPageAsync`, then the existing mobile DTO projection.
- `Infrastructure/Messaging/MessagingService.cs:437` owns authorization and conversation projection. It reads the message window, older-history existence, attachments, participant identities/display names, review state and read state, then recipient translation. Web currently uses the unpaged service entry point; mobile requests a bounded newest page.
- `ApplyTranslationPresentationAsync` (`MessagingService.cs:4467`) awaits body translation and reply translation in a per-message loop. `GetOrCreateMessageTranslationAsync` (`:4540`) resolves original language, checks the existing tracked/persisted message-target cache, then calls the existing account-scoped translation router on a miss. Source-language detection and persistence occur through the same authority (`:4732` onward).
- Inbox previews also invoke `ApplyTranslationPresentationAsync` after loading the list (`MessagingService.cs:365–375`). Activity reads invoke recipient presentation before badge reconciliation (`Infrastructure/Notifications/NotificationEngine.cs:391–426`). Thus inbox, thread, and activity can all touch the same translation evidence/cache.
- Mobile participant/avatar projection is already batched: `MobileMessagingController.cs:877–889` resolves the distinct identities and then invokes the canonical bulk avatar resolver. Do not add a second avatar authority or assume an avatar query per message without a trace. Repeated whole-request identity/avatar work remains a measurable possibility.

The latest local send repairs defer external presentation until after durable acknowledgement. That is a source fact, not proof that the logged 17.2-second send ran this code or that its production latency is repaired.

## Platform observations and concrete opportunities

### iOS

`MessagingStore.openConversation` (`:545`) already renders its existing authorized detail cache synchronously and refreshes in place. `conversationDetail(for:)` (`:1503`) coalesces requests by conversation while in flight. These mechanisms should be retained, not replaced with another thread cache.

`refreshConversation` (`:1459`) presents the thread before asynchronous mark-read. However, multiple callers awaiting one coalesced detail task can each schedule mark-read; the detail request is coalesced, the read acknowledgement is not. The log's nearby repeated mark-read calls are consistent with this but do not prove that trigger.

`reconcileRealtimeEvent` (`:1692`) requests inbox and selected-thread refresh together. Full resync clears localized projections. `refresh()` (`:522`) awaits inbox and then activity. Actor ownership and presentation revisions must remain intact. Cancelling a foreground consumer does not automatically cancel the unstructured shared detail task; cancellation should respect remaining consumers rather than cancelling a request another active reader owns.

### Android

`MessagingViewModel.open` (`LegendFeatureViewModels.kt:327`) fetches detail, applies it, then marks read and refreshes the inbox. A different selected thread replaces the current detail with Loading. A revision prevents an old response from becoming visible, but does not cancel the old request. No per-thread request coalescing is present in this path.

`reconcileRealtime` (`:670`) awaits inbox refresh before refreshing the selected thread. `refreshInboxSilently` has revision ordering but no shared in-flight request. This creates unnecessary sequential delay and permits overlapping list reads from navigation, send completion, and realtime. The recently repaired actor-owned ViewModel lifetime must be preserved.

### Web

`SHARED/wwwroot/js/messaging.js:1153–1175` waits for detail, renders it, then awaits mark-read and a list refresh. Realtime (`:1339`) first awaits inbox refresh and then reloads the active thread; if the event is considered viewed, `loadConversation` also performs mark-read and another list refresh. This is a concrete duplicate list-read path for a viewed incoming message. The 45-second list poll (`:1319`) can overlap realtime/list work.

The same function has no request generation or AbortController check before replacing `state.active`. Rapid A→B navigation can allow A's slower response to replace B. It also clears `state.pendingSubmission` on every conversation refresh, including same-thread realtime refresh, which can discard a lost-ack retry ID. These are source-level correctness defects to address alongside request ownership, without moving message authority into the browser.

## Ordered implementation plan after social creation

1. **Establish a reproducible, version-bound trace.** Capture client build, server SHA, actor type, warm/cold state, message count, language pair, translation-cache state, and request correlation IDs. Add spans inside the existing request/service path for authorization, conversation/message SQL, attachments/read receipts, identity/avatar projection, source-language resolution, translation cache hits/misses, provider calls, serialization, and client first paint. Record counts and durations without message bodies/tokens. Run one thread tap with background refresh disabled in the controlled harness, then the normal foreground/realtime path. This separates one slow request from request amplification.
2. **Repair client request ownership and amplification first.** Within existing stores, coalesce in-flight inbox/detail work by typed actor, conversation/page and presentation revision. Cancel obsolete unshared navigation requests and reject late results. Do not gate selected-thread hydration behind inbox completion. Coalesce mark-read by conversation and last visible message/read revision; a failed read must remain retryable. Preserve already authorized content during refresh. Web refresh must not discard pending send identity unless its acknowledged transaction completes or its actual payload changes.
3. **Remove repeated server reads from the same projection.** Measure SQL counts first, then batch requested message-target cache rows and reuse source-language/recipient policy observations within the existing service request. Resolve reply sources once when multiple messages reference the same original. Retain current quota reservation, cancellation, consent and cache provenance checks. Avoid parallel EF operations on the same DbContext. Reuse existing batched avatar interfaces and versioned media references; only optimize them if their trace is material.
4. **Address cold translation separately from warm-cache opening.** A page of uncached messages currently permits serial detector/provider work before its response. First ensure cached messages need no provider and no repeated per-message identity/registry queries. Then evaluate presenting the server-authorized original plus available retained translations while unfinished presentation uses an existing deferred presentation authority. This requires an explicit API/UI contract and independent tests: never label untranslated text as translated, never guess source language, never bypass quota/consent, and keep eventual translated body/reply correctness. Do not introduce a new inference service or unowned background queue. If current deferred contracts cannot schedule it safely, stop at a reviewable contract proposal rather than silently dropping translations.
5. **Bring web history onto the same bounded query contract.** Use the existing message-page authority and stable history cursor, preserving scrolling, replies, read receipts and older-history access. Do not fetch an entire long thread solely because the caller is web.
6. **Validate contention and failure behavior, then tune.** Re-run with concurrent inbox/activity/thread reads, realtime bursts, provider slowness/outage, rapid navigation, language changes, actor switches, and connection loss. Choose indexes or query changes only from actual execution plans and measured query timings; no speculative database writes during this audit.

## Proposed performance targets, not achieved claims

Measure distributions on an agreed device/network and representative thread sizes; the supplied three thread samples cannot establish p95.

- Cached reopen: visible existing authorized thread within 100 ms at p95, with refresh independent of presentation.
- Warm server/cache thread fetch: p95 under 1 second; cold original-message first usable projection: target under 1.5 seconds. These are engineering acceptance proposals, subject to the trace and deployment topology.
- An individual thread tap: at most one foreground detail request and one necessary mark-read; no compulsory inbox refresh before first paint.
- A coalesced realtime burst: at most one in-flight refresh per actor/projection key, with one trailing refresh when a newer event arrives during flight so events are not lost.
- Warm retained translations: zero provider calls; final translated content, quota accounting, original-language truth and consented learning unchanged.
- Provider failure must not clear an already displayed authorized thread or turn a committed send into failure. No timeout increases to disguise latency.

## Required tests and evidence

Retain the existing bounded history, original-message preservation, cache reuse, provider-failure, recipient-language, consented learning, quota/alias, send acknowledgement and badge tests. Extend behavior tests rather than static source-string assertions:

- Delayed A then fast B navigation; A never replaces B and obsolete requests are cancelled where not shared.
- Two simultaneous opens/refreshes share one detail request and one necessary read acknowledgement.
- Actor/language changes invalidate the correct presentation only; no cross-actor cached thread or retry state.
- Realtime burst plus foreground tap does not double-refresh the inbox, block thread paint, lose a newer event, or discard pending idempotency state.
- Cached, uncached, partially translated and provider-unavailable pages preserve every body/reply, ordering, history continuation, and typed authority; measure provider and SQL counts.
- Measured client paint/HTTP/server-stage artifacts across web, iOS and Android, with sample size and p50/p95/p99 reported independently from correctness results.

This audit does not establish parity with Instagram or Apple Messages, and does not authorize implementation or deployment during the social repair. It identifies concrete bottlenecks and correctness risks to validate through the existing centralized path once that work is complete.
