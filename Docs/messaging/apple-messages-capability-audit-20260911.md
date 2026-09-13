# Apple Messages capability expansion and simulator correction

## Actual status

Full Apple Messages parity is **not implemented**. The earlier receipt/reaction/share changes are a subset. This audit is not an implementation or a live pass. Production deployment remains prohibited.

The visible user simulator is iPhone 17 Pro Max `5917CE4B-AF0C-4886-95BD-2FD282E77E2D`; previous tests used LegendCreatorVerification `E6375EE1-8473-4142-AA5E-880F8923C86C`. Install the candidate into the former for user review. The first candidate installation exposed absent ignored local iOS configuration. Reused the existing original `Legend.local.xcconfig` without printing or committing its contents, then rebuilt. Do not uninstall or clear user data. Building the original production project again will replace this simulator app with its older implementation. No archive is necessary.

## Capability inventory

| Capability | Evidence/status | Required continuation |
|---|---|---|
| Latest read boundary, newer sent pills | Candidate iOS/Android/web implementations and contract tests | Visually verify authenticated user conversation; production backend still old |
| Double-tap reaction, palette, custom emoji, remove | Candidate canonical reaction endpoints and native/web controls | Deploy matching additive schema/backend only after authorization; current production does not supply new palette |
| Reply, copy, original text, own-message unsend | Existing service contract and iOS context menu | Do not equate current delete behavior with Apple's time-limited undo/edit history |
| Attachments and source content | Candidate authenticated media/file rendering | Existing shared-storage release prerequisites remain open |
| Group profile/membership, pin/mute/privacy | Existing IMessagingService methods | Complete per-platform behavioral comparison before parity claim |
| Edit with visible history | No edit command in current IMessagingService | Extend existing message authority with versioned edits, stale-write handling and translation-version invalidation; update all clients |
| Send later and cancel/reschedule | No scheduling command in current IMessagingService | Extend existing durable delivery worker/authority; govern cancellation, participant access at execution and duplicate prevention |
| Polls and voting | No poll/vote contract in current messaging model | Canonical message subtype and authorized idempotent votes; no client-owned totals |
| In-conversation search, forwarding and multiselect | Not established by recipient/inbox search or external text sharing | Authorized server search and reference-preserving forwarding; attachment access must be re-evaluated for recipients |
| Rich formatting, text/bubble effects, backgrounds, stickers | Not established by plain-text message contracts | Shared bounded content schema, rendering rules and accessible reduced-motion behavior |
| Voice transcription, location/live location, Check In | Not verified as messaging capabilities | Inspect existing call/media/location authorities; consent, expiry and revocation; no fabricated tracking |
| Notification reply, typing state, unread recovery, deleted recovery, attachment browser | Not fully audited or proven | Inspect existing push/realtime/read/deletion authorities and finish explicit contract coverage |
| Apple platform services and integrations | No claim of equivalence | iMessage transport, satellite messaging, Apple Cash, system intelligence, iMessage extensions and Apple-device continuity require separate feasibility review; do not label a LEGEND substitute as the Apple service |

## Integration rules

Extend `IMessagingService` in `Domain/Messaging/MessagingInterfaces.cs` and the existing `MessagingService` implementation. Use its existing authorization, actor identity, durable messaging/outbox and translation authorities. Add versioned contracts only where necessary. All iOS, Android and web clients must consume the same persisted state. No menu item counts as implemented unless its authorized operation, persistence, realtime propagation and negative tests work. No production writes or releases are authorized by this audit.

## Sources checked 2026-09-11

- Apple Messages overview: https://support.apple.com/en-ie/104982
- Edit and unsend: https://support.apple.com/en-ae/guide/iphone/iphe67195653/ios
- Send later: https://support.apple.com/en-ie/guide/iphone/iph5ae9a7be6/ios

These establish that the requested scope includes much more than reactions and receipts. Unlisted features remain unaudited; this inventory must not be presented as exhaustive parity certification.
