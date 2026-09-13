# Chat presentation and sender notifications — 2026-09-11

The candidate starts from production `5fd2fdafc69d77e5c43b6142c2e12b682ff20808`, which includes PR #116 and runtime `c785675100adaa464f748c48c494fe4f8fb0d2b7`. Prior translation, message acknowledgment, shared-content, social-media ingestion, blob-range and dual-web-host release fixes are retained by ancestry. No files are deleted.

## Shared behavior

- Conversation detail and list projections resolve direct-chat display titles from the current counterparty identity; named groups retain their subjects. Native clients and both web hosts consume that authority.
- One design-token document defines blue chat timestamps. iOS, Android and web consume it; web conditionally revalidates the document instead of forcing stale cached tokens.
- Reaction indicators sit at the message’s top-right. Double-tap sets a red heart. Choosing a palette reaction dismisses its menu. Provisional feedback appears immediately; mutations use the existing endpoint, serialize per message, and reconcile with server acknowledgments. Failed requests restore confirmed state and expose an error.
- The existing notification engine resolves message sender identity and issues a one-day, purpose-bound photo capability. Reads recheck message existence, recipient membership and the existing conversation authorization authority. No profile-photo database or image copy is created.
- iOS uses communication notifications and the OS-provided application badge. Android uses MessagingStyle and Person icons. A persisted capability on the existing push-device row preserves legacy Firebase background alerts; upgraded clients receive data messages and render the shared payload. Missing images preserve text delivery. Image reads are bounded and redirects are refused.
- The additive `AddCommunicationNotificationCapability` migration defaults old registrations to false. No business-data rewrite is needed.
- iOS project build number is 30. Android retains the previously requested automatic release-version allocator and Keychain signing integration. Existing CI signing authority is unchanged.

## Verification

- Full backend suite: 2,456 total, 2,443 passed, nine failed, four skipped (6m13s). All nine exact failure identities/message hashes and all four skipped identities match the previous baseline. Five new regressions pass. These results do not authorize another baseline exception.
- iOS final unit/contract suite: 151 passed, zero failed.
- Android focused messaging tests: 12 passed, zero failed; final Kotlin compilation succeeded.
- Web interaction tests: 19 passed, zero failed, including rapid selections, immediate feedback, rollback and menu dismissal.
- Sender-photo tests cover tampering, expiry, wrong notification/recipient, deleted message, revoked membership and failed conversation authorization. Firebase tests cover both old and capable registrations.
- The physical iPhone user confirmed build 29’s top-right heart, immediate feedback and dismissal. Build 30 adds the verified rapid-selection queue correction; this later build still requires artifact handoff.
- The first full backend run used an incorrect isolated-run repository lookup and was cancelled. The rerun supplied GITHUB_WORKSPACE; no source assertions or held-out expectations were changed. The new title test was corrected to seed a message because the existing inbox deliberately excludes empty conversations.

## Release and live evidence still required

The protected release manifest must identify the corrected runtime commit and production base while retaining exactly the authorized nine failures and four skipped tests. The last production run deployed both web hosts but failed its native SQL proof; this candidate does not repair or waive that proof.

End-to-end APNs/FCM sender-photo delivery against the new server revision, including locked/background physical devices, remains unexecuted. OS layout and badge position vary by OS. The repository has no established browser push/service-worker delivery path, so browser lock-screen/background delivery is not claimed by this change; shared web chat presentation is covered.

A backend deployment does not replace installed native apps. Distribute the newly signed iOS and Android artifacts from this candidate. An existing bundle with an old version code or an older installed iOS build is not evidence of this source. The final Android handoff remains Legend-Android/app/build/outputs/bundle/release/app-release.aab after signature and embedded-version verification.

## Release regression correction

Protected run 34641325370 stopped before build, merge, migrations or deployment. The unchanged localization contract reproduced a missing `(You, visual interface copy)` entry introduced by the Android communication notification's localized self label. Regenerating `Legend-Design/legend-application-copy.json` with `scripts/generate-application-copy-manifest.rb` adds exactly that source entry and updates the catalog version; all 5,186 existing entries remain unchanged. No test assertion or failure allowance is changed. The earlier full-suite result did not cover this final label addition and is superseded by the corrected candidate's rerun.
