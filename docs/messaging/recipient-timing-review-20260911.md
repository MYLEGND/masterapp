# Independent review of recipient timing amendments

Decision: **APPROVED for the four bounded amendments below.** This approves a change to when existing assertions run, together with new assertions that no translation occurs before acknowledgement. It does not approve removing final translation, learning, unread-count, identity, or cache requirements.

Reviewer: evaluation_baseline, independently of the implementation owner. Review was read-only; this commit changes only this document. No tests or builds were run by this reviewer. Integration source reviewed at `65146571` (including production changes `87630aba` and `f79f40ee`). The first two prepared amendments were also inspected in `/private/tmp/legend-messaging-deferred-20260911` at `2f9e1ea8` plus its pending recipient-projection addition. The other two amendments were reviewed against their unchanged source and the explicit proposal below.

The integration owner reported that automatic approval review rejected editing the two remaining timing assertions without independent documentary review. This document supplies that review; it does not override the automatic review or itself modify an assertion. Integration must submit the concrete, bounded edits through the normal review mechanism.

## Why the old timing expectations conflict with the authorized behavior

The user authorized nonblocking message acknowledgement. `MessagingService` now commits the original message, authorized recipient notification ledger, and delivery work before translation presentation. Initial direct/group sends use the same arrangement and call the conversation projection with `applyTranslation: false`. An acknowledgement must not wait for an external detector or translator. Ordinary recipient conversation reads and `PrepareNotificationPresentationAsync` continue to use the existing translation and learning authorities.

`NotificationEngine.StageMessageForRecipientsAsync` stages durable recipient notifications; it does not establish a translated presentation or reconciled badge before commit. `GetBadgeSnapshotAsync` calls the existing badge reconciliation authority. `GetSnapshotAsync` prepares recipient presentation and reconciles the badge. These are actual production entry points, not test-only data preparation or substituted authorities.

The captured `/private/tmp/legend-messaging-final-focused.log` reports group detection and consented provider-count failures as expected 1, actual 0 at acknowledgement; source-language failure as expected `fr`, actual null before recipient presentation; and a missing badge projection row before reconciliation. Those observations agree with the timing change. They do **not** establish that later translation, learning, or badge reconciliation succeeds: the original final requirements must remain and must be executed again.

## 1. Group translation and unique-target reuse

Test: `LegendConnectEntitlementTests.GroupMessage_DetectsActualSourceAndTranslatesOncePerUniqueTargetLanguage_WhenRecipientsRead`.

Approved replacement of only the immediate post-`CreateGroupAsync` translation assertions: require zero detections, zero account translations, no target calls, and an empty `MessageTranslations` table. A detector result must not be asserted before detection occurs.

Keep the original recipient-read loop through `GetConversationAsync`. Keep every final requirement: exactly one detection with source `en`, exactly two account translations, one `ht` target call and one `fr` target call, and exactly two persisted translations. No recipient, language, source text, permission, quota, or expected translated result changes are approved.

This strengthens acknowledgement isolation while preserving the original unique-target caching requirement. A later detection/translation/cache failure remains a test failure.

## 2. Consented live translation and retained memory

Test: `MobileMessagingTranslationEndToEndTests.ConsentedLiveTranslation_IsRetainedByTheExistingCorpus_AndLaterServedFromMemoryBeforeAzure`.

Approved change: provider translation count is zero immediately after initial message acknowledgement. Keep the actual first recipient `GetConversationAsync`, after which provider count must be exactly one. Update the timing comments accordingly; the first recipient read now produces the translation rather than reusing send-time external work.

Preserve all learning checks: one event, `Eligible` eligibility, `ConsentedLiveTranslation` provenance, exact source/target text, `Processed` state after the real corpus processor, and the existing alignment and context-relationship counts. Preserve the later send and recipient read, total provider count one, `LegendConnectTranslationMemory` provider, and the same final translated text. Do not insert teaching, seed a translated result, or lower any learning/caching requirement.

## 3. Unread ledger and global badge projection

Test: `MessagingServiceTests.AgentClientRelationship_AllowsMessaging_AndTracksUnreadAndReadState`.

Approved addition: after confirming the actual durable unread notification, invoke the real `NotificationEngine.GetBadgeSnapshotAsync(client)` and require returned unread count one, before the existing persisted `UserGlobalBadges` assertion of one.

Keep the original conversation-list unread count one, subsequent `MarkConversationReadAsync`, unread count zero, notification read state, persisted global badge zero, and audit count. The test must continue to prove both the durable unread source and its canonical projection. Replacing the badge with a fabricated count or omitting the post-read zero checks is not approved.

## 4. Actual source language versus sender preference

Test: `MessagingServiceTests.MessageTranslation_CanonicalSenderPreferenceRoutesEnToEs_WhenRecipientReads`.

Approved addition at acknowledgement: original language is null and translation routes are empty. Then invoke the actual client recipient `GetConversationAsync` and require its Spanish body, followed by `PrepareNotificationPresentationAsync` for that same recipient notification.

Preserve every original final assertion: sender preference `en`; detector-owned source metadata `fr`; exactly one `(fr, es)` route; one Spanish translation cache row; the same Spanish notification detail; and a later recipient read that leaves the route/cache counts at one. The new recipient projection must not be replaced with direct entity mutation or a seeded cache.

This preserves the distinction between a sender's preferred presentation language and the body's detected language. It does not authorize guessing English or any other source language when detection is unavailable.

## Integration and evidence limits

Apply only these timing/setup amendments, retaining all final requirements described above. Rebuild and run the affected tests plus the existing deferred-presentation controls. Record their actual results; this review is not a passing execution result. If a post-recipient assertion fails, investigate it as a possible runtime regression rather than extending this approval.

No held-out prompt, expected answer, capability threshold, exclusion, quota allowance, consent rule, permission grant, production gate, or release authorization is changed. Messaging and translation repairs remain undeployed pending the user's required successful verification.
