# Legend direct calling

Release status: backend release authorized; native distribution requires new signed builds and device verification. No paid calling resource or TURN relay is configured.

## Architecture

- Swift and Kotlin use native WebRTC. Calls target the authenticated participants of an existing direct conversation, not telephone numbers or an external dialer.
- `MessagingHub.Call` resolves the signed-in actor and delegates to the existing `MessagingService`. The same conversation, membership, block and account authorities authorize calls. Client identity aliases come from the existing profile records.
- `LegendCallSessions` holds lifecycle metadata, an answering device binding and an optimistic concurrency token. Only one device can answer; an expired or unauthorized session cannot send signaling.
- `LegendCallSignals` is the shared, short-lived signaling outbox. Each Azure host delivers rows to its own authenticated SignalR groups. Signals expire after 60 seconds; the delivery worker removes expired rows. SDP and ICE data are transient transport data, not message history. Database backups follow the existing database retention policy.
- APNs VoIP and FCM reuse the existing push credentials, gateways and device registry. Push wakes the receiving app; it does not grant access to a call.
- Media is encrypted by WebRTC and travels directly between devices. Public STUN is configured centrally in `DirectCallPolicy`; there is no automatic paid fallback. Existing hosting, database and network usage still consume existing infrastructure capacity.

## Native behavior

- iOS uses CallKit, PushKit and the system audio session. Android uses a self-managed Telecom connection, foreground call service and incoming-call notifications.
- Microphone and camera access require user permission. Camera capture pauses in the background; an accepted audio call can continue under the native call lifecycle.
- Voice/video controls include mute, speaker, camera, camera switch, an explicit one-frame snapshot/share action, and end/decline. Ending or switching accounts immediately releases media; teardown uses only the old authenticated connection.
- Trickle ICE, queued early candidates and negotiation epochs handle offer/answer races. Network changes initiate bounded ICE recovery.
- Shared policy: 45-second ringing, 20-second initial connection deadline, three recovery attempts, 720p/30fps Wi-Fi and 480p/24fps cellular ceilings, 1 Mbps video and 64 kbps audio ceilings. Native network adaptation can lower these.
- A network that requires TURN will fail clearly. Direct-only calling cannot promise universal connectivity across carriers, firewalls or countries.

## Screen sharing and snapshots

- Android requests fresh MediaProjection consent for each sharing session and starts the existing call service with the mediaProjection type before capture. Revocation and call teardown release the projection. The shared video sender carries the screen in place of the camera.
- iOS uses ReplayKit app capture: **Legend® content only**, not other apps or the entire device. Sharing collapses the call into a Return/Stop sharing bar; ending, stopping sharing, or leaving the app ends capture. Sharing other apps on iOS would require a separately provisioned broadcast extension, which this change does not add.
- Snapshots capture one remote decoded frame only on request, then open the platform share UI. No continuous recording is created. Android shares through a private cache-only FileProvider; old snapshot files are cleaned on subsequent captures. iOS keeps the snapshot in memory until the share UI closes.
- Verify screen consent denied/accepted/revoked, stopping/restarting sharing, device rotation, muted camera restoration, and snapshot sharing on physical devices before publishing native builds.

## Release and verification

1. Apply the reviewed `AddLegendDirectCalling` migration through the existing backend release workflow, then deploy the backend. Do not publish new clients before their backend is available.
2. Build new iOS and Android releases through the existing signing paths. This feature cannot appear through backend deployment alone.
3. Verify the existing APNs key can send the app's `.voip` topic and the existing FCM configuration accepts high-priority call data messages. No new provider resource is required by the code.
4. Review Android foreground-service/full-screen-intent declarations in Play Console and the camera/microphone/background-call descriptions in the store submissions.
5. Before release, test iOS ↔ Android on physical devices: voice and video in both directions, locked/background/terminated receiving app, denied permissions, Bluetooth/headset changes, native phone interruptions, Wi-Fi ↔ cellular transitions, account switching, two devices answering, and an intentionally blocked direct connection. Check that no call or capture survives End/sign-out.

Automated coverage includes backend authorization, legacy identity aliases, relational answer concurrency, call expiration, bounded signaling, push payloads, native WebRTC peer connection/renegotiation, and the existing mobile/backend regression suites. Simulator peer tests do not establish physical-device cross-platform or carrier-network reliability.

The legacy backend contact-address endpoint remains available for already-published clients. New mobile builds no longer use it for calling. Web messaging continues using the shared messaging authority; this change adds native calling UI, not a browser calling interface.

## iOS archive symbols

The WebRTC 152.0.0 Swift package omits debug symbols. Its publisher distributes them separately as `WebRTC-M152-dSYM.zip`. The shared Legend scheme prepares that checksum-pinned download in the local Library cache before archiving. An archive-only build phase verifies the embedded framework and publisher dSYM have identical UUIDs, then includes the dSYM in the archive. Build-script sandboxing stays enabled; no signing settings change.

The first archive needs network access for the approximately 389 MiB download; subsequent archives use the cached symbols. A package upgrade must update the publisher symbol version/checksum and cache path together. Missing or mismatched symbols fail the archive rather than silently producing another incomplete upload. Generate the project from `Legend-ios/project.yml` to retain the shared archive action.

## Confirmed delivery and ringing

`ReceivedUtc` in the existing `LegendCallSessions` row is the single delivery acknowledgement for all clients. Only the authorized recipient may send `received`, after native incoming-call presentation succeeds. Push dispatch and SignalR sends do not count as receipt. Repeated receipts are idempotent, do not extend expiry, and do not claim the answering device. The shared snapshot supplies terminal failure text distinguishing an unconfirmed delivery from an unanswered or declined call.

Both platforms display the outgoing screen immediately. Before receipt it says Calling and explains that it is waiting for the recipient’s device. After receipt it says Ringing and plays the shared ringback asset. iOS CallKit and Android’s call notification channel use the shared incoming sound; system volume, silent mode, notification permission, and Do Not Disturb remain respected. No app can guarantee that a person heard a sound. Sound sources live once in `SHARED/Calling/Resources/raw` and are packaged by both platform builds.

Both active clients reconcile against the existing authorized `get` action every three seconds, so a missed lifecycle event cannot leave an indefinite stale screen. Network/status failures are visible. Local cancellation retires the call ID, stops sounds and media, and rejects late responses. iOS fulfills the local CallKit start action before waiting for the invite network round trip. Incoming presentation failure on one device does not decline other devices on the receiving account.

Verification for this correction includes backend authorization/idempotency/expiry/concurrency tests, iOS native-peer and packaged-audio tests, and Android build/unit/lint and instrumented native-peer/audio checks. These checks do not prove that two physical devices can ring and exchange audible media. Only THE C.E.O is available; the user has no second physical phone. That end-to-end acceptance remains unverified. Existing direct-only network limitations above still apply.

Cancellation uses the same call record and serializable invite transaction. An authenticated caller can create an ended record before an in-flight invite is committed; replaying that invite returns the terminal record and cannot ring the recipient. Cancellation does not use a second queue or state authority. Regression coverage includes cancellation arriving both before and after invitation, replay, and unauthorized device/account attempts.
