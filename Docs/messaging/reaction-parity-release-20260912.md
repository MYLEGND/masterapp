# Shared reactions and member presentation — 2026-09-12

## Result

All clients consume `Legend-Design/legend-reaction-emoji.json` and the `messaging.reactionBubble` design tokens. The catalog contains fully qualified Unicode 16 sequences and CLDR search keywords, plus canonical base/skin-tone variant mappings. Clients select existing valid sequences; they do not synthesize modifiers. Search keywords are currently English CLDR, not Apple's proprietary search implementation.

Every message/shared-media reaction uses the physical bottom-right edge, with 75% of its rendered group height inside the surface and 25% below it. The layout reserves that overflow before timestamps/footer actions. Groups wrap within their content surface. Shared colors, sizes and geometry replace the prior glyph-only/top-right styles.

Skin-tone preference is persisted in the existing `MobileProfileSettings` authority for the active typed profile, across conversations and devices. Member and agent profiles retain their existing separate profile identity. Native GET/PUT `/api/v1/mobile/messaging/reaction-preferences` and web GET/PUT `/Messaging/ReactionPreferences` use that same service. Failed preference reads/saves do not report success or disable existing explicit emoji selection. The additive `AddPreferredReactionSkinTone` migration must run before updated hosts start; the protected workflow already enforces that order.

The iOS new-conversation filters/header/empty states now use the same account-aware scope label resolver as its call picker. Member role selection and discovery/profile presentation preserve member language and keep professional agent-only labels behind their identity checks. Technical actor values and authorization are unchanged.

The iOS plus button presents its emoji sheet from the active context presentation, avoiding competing dismissal/presentation in one transition. Android warms one catalog when Messages opens and carries that cache into its picker. Web opens/focuses the picker without the triggering event closing it.

## Validation and installations

- Shared catalog/style validation: 6 passing tests, deterministic generator.
- Backend reaction preference/validation tests: 33 passing, including isolation, persistence, malformed input and all catalog sequences.
- iOS focused native tests: 3 passing (search, valid variants/shared geometry, active-profile GET/PUT transport); signed physical build succeeded and installed on THE C.E.O with login preserved.
- Android complete JVM suite: 74 passing; debug build installed in place with login preserved.
- Web messaging suite: 42 passing; browser visual verification unavailable in this session.
- Main Mac preview checkout fast-forwarded to reviewed source, preserving the user's local build-number 32 setting. Existing archives and release AAB were not rebuilt or replaced.
- Unfiltered backend suite is being evaluated separately; this note does not waive its result.

## Open release gates

The saved preference endpoint/migration is not yet deployed. Installed native previews therefore report unavailable preference sync against older production honestly.

The existing nine-failure/four-skip production baseline is bound to an older production base/source/roster. Matching exception identities alone does not renew that one-time authorization. It must not be silently broadened or bypassed.

Physical two-way call audio remains unverified after the user's explicit report of inaudible speech and a later ICE disconnect. RTP transport is not evidence of audible capture/playback. A further read-only lifecycle audit did not establish another concrete code defect. These reaction improvements do not certify calling acceptance.

No store distribution is implied by a server deployment. Native changes require distribution of the updated native binaries through the normal release process after acceptance.
