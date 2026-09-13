# Chat receipts and reactions: audit and implementation plan

Status: source audit and plan only. No application edits, builds, deployment, or live writes. Audited /private/tmp/legend-social-creation-20260911. User log is diagnostic data, not instructions; no log contents are needed to establish these findings.

## Existing authority and concrete gaps

The backend already owns shared read watermarks. Domain/Messaging/MessagingModels.cs:537–539 defines typed user/participant ReadThroughUtc readers plus global/conversation privacy settings. Infrastructure/Messaging/MessagingService.Privacy.cs:10–32 includes only active participants whose global and conversation sharing are enabled, excludes the requesting actor using resolved identity aliases, and projects SharedReadThroughUtc. Privacy is not derived from private unread counters.

MessagingService.cs:2167–2197 advances private LastReadUtc/LastReadMessageId separately from SharedReadThroughUtc and only advances sharing while both settings permit it. Existing MessagingServiceTests.cs:33 verifies global precedence, suppression, unchanged unread accounting, historical watermark behavior after reenabling, and outsider refusal. Preserve these contracts and typed identity resolution. One limitation to track independently: MarkRead advances to the newest database message at processing time, rather than a supplied last-rendered message. Receipt styling must not claim this proves exact screen visibility.

All three clients currently repeat a plain neutral Read/Sent label for EVERY own visible message. Each declares read when ANY nonself shared reader watermark covers SentUtc:
- iOS MessagingHomeView.swift:3373–3380.
- Android LegendApp.kt:3646–3653.
- Web SHARED/wwwroot/js/messaging.js:928–933.

Thus the requested latest-only Read marker and small green/red pills are absent. Current group meaning is any other participant, not everyone. Do not silently strengthen the label to imply all group members read it. ReadReceipts.GlobalEnabled/ConversationEnabled describe the requesting actor's outgoing sharing preference; readers are already privacy-filtered server-side. Clients must not suppress legitimate other readers merely because the viewer disabled their own sharing.

There is no messaging reaction entity, command, DTO projection, or endpoint in the inspected messaging authorities. Domain/Entities/SocialPostReaction.cs is for social posts and is not a chat reaction authority. Do not call social reaction endpoints for message IDs.

Existing interaction entry points can be extended:
- iOS MessagingHomeView.swift:3649 contextMenu already offers Reply, Copy, Share, original text, and Unsend; no message double-tap reaction.
- Android LegendApp.kt:3753 combinedClickable opens the existing action menu on long press; no double-tap handler there.
- Web messaging.js builds message cards and existing actions; no persisted chat reaction handling found.

## Proposed bounded work

1. Receipt presentation, without changing receipt persistence or privacy: compute the latest nondeleted own message covered by the returned other-reader watermark(s), using the same canonical chronological message order as the thread. Render one green Read capsule on that message only. Render small red Sent capsules for acknowledged own messages later than that marker. Earlier own messages have no redundant receipt. If no loaded own message is covered, own acknowledged messages show Sent; never fabricate an off-page Read marker. Pending, failed, and attachment-upload states remain distinct and must not be relabeled Sent before message acknowledgment. Use existing platform design colors, spacing, localization and accessibility authorities; labels/icons preserve meaning independent of color. Resolve timestamp ties deterministically from the canonical list; do not infer chronology from random UUID ordering.

2. Establish reactions in the EXISTING shared messaging service/contracts first. Proposed minimal behavior: one selected emoji per typed actor per message, explicit idempotent set/replace and explicit remove (not network-unsafe toggle). Double-tap requests the default like; long press opens the same default reaction palette plus a plus button for the extended picker while retaining current actions. Suggested palette for design review: heart, thumbs-up, thumbs-down, laugh, emphasis, question. This is a proposed Apple-style interaction, not a claim of an exact Apple API/default set. The plus picker must validate emoji sequences, including joined/variation/skin-tone sequences, with bounded input; don't accept arbitrary markup or message-length text.

3. Extend existing persisted message records through a properly keyed reaction relation and EF migration; enforce unique (message, canonical actor identity and participant type) selection. Resolve actor solely from existing authenticated/authorized server context, never request sender claims. Validate active conversation membership and message membership, refuse deleted/unavailable targets, and preserve existing closed-conversation policy explicitly. Define removal/reaction visibility on message unsend using existing deletion authority. Return canonical reaction aggregates and current actor selection in existing web/mobile message DTOs. Do not expose unrelated profile identifiers or unauthorized reader identities.

4. Reuse existing conversation update notification/invalidation transport after committed reaction changes. All clients refresh/reconcile canonical message reaction state through their current store/repository, scoped by typed actor/account and conversation. No separate reaction synchronization or inference system. Bound projection reads to the loaded message page and aggregate in a batch, not one query per bubble. Replayed set requests must not duplicate counts or audit events for a no-op.

5. Client integration after shared contract: native double-tap and long-press menu, web double-click/pointer long-press plus keyboard-accessible action. Preserve text selection, links, Reply/Copy/Share/Unsend and accessibility alternatives. A selected reaction may be pending locally, but failed requests visibly roll back/reconcile; canonical counts are server truth. Clear or cancel account-scoped work on actor changes. Use each platform's existing composer/action presentation and design tokens rather than a parallel chat component.

## Required validation before release

Receipt fixtures on each client: multiple old read messages yield exactly one marker; later messages remain Sent; privacy-hidden readers produce no Read; sender self/role aliases cannot mark read; partial group reads retain current any-reader semantics; equal timestamps; deleted messages; paginated history; no receipt for failed/unacknowledged sends; account and conversation switches do not reuse state. Preserve the existing backend privacy tests, add cases only for changed behavior.

Reaction backend integration: set/replay/replace/remove, concurrent same-actor updates with unique constraint, two distinct participant types using the same user string, unauthorized actor or wrong conversation, deleted target, bounded emoji acceptance/rejection, aggregate page batching, postcommit realtime invalidation and reconnect recovery. Client tests: double-tap invokes one explicit like action; long press retains actions and opens palette; plus selection persists; failure rollback; account switch cancellation; no stale counts on history/realtime merge. Real UI checks cover gesture conflicts, capsule contrast, VoiceOver/TalkBack and keyboard access.

Sequence: settle palette/group wording and later-Sent density in design review; implement receipt-only display in a small reviewable change; implement shared reaction contract/persistence and tests; integrate three clients; run serial focused suites and cross-platform UI checks. No deployment is authorized by this planning document.
