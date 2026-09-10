# Legend direct calling

Release status: local implementation; deployment is on hold. No paid calling resource or TURN relay is configured.

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
- Voice/video controls include mute, speaker, camera, camera switch and end/decline. Ending or switching accounts immediately releases media; teardown uses only the old authenticated connection.
- Trickle ICE, queued early candidates and negotiation epochs handle offer/answer races. Network changes initiate bounded ICE recovery.
- Shared policy: 45-second ringing, 20-second initial connection deadline, three recovery attempts, 720p/30fps Wi-Fi and 480p/24fps cellular ceilings, 1 Mbps video and 64 kbps audio ceilings. Native network adaptation can lower these.
- A network that requires TURN will fail clearly. Direct-only calling cannot promise universal connectivity across carriers, firewalls or countries.

## Release and verification

1. Apply the reviewed `AddLegendDirectCalling` migration through the existing backend release workflow, then deploy the backend. Do not publish new clients before their backend is available.
2. Build new iOS and Android releases through the existing signing paths. This feature cannot appear through backend deployment alone.
3. Verify the existing APNs key can send the app's `.voip` topic and the existing FCM configuration accepts high-priority call data messages. No new provider resource is required by the code.
4. Review Android foreground-service/full-screen-intent declarations in Play Console and the camera/microphone/background-call descriptions in the store submissions.
5. Before release, test iOS ↔ Android on physical devices: voice and video in both directions, locked/background/terminated receiving app, denied permissions, Bluetooth/headset changes, native phone interruptions, Wi-Fi ↔ cellular transitions, account switching, two devices answering, and an intentionally blocked direct connection. Check that no call or capture survives End/sign-out.

Automated coverage includes backend authorization, legacy identity aliases, relational answer concurrency, call expiration, bounded signaling, push payloads, native WebRTC peer connection/renegotiation, and the existing mobile/backend regression suites. Simulator peer tests do not establish physical-device cross-platform or carrier-network reliability.

The legacy backend contact-address endpoint remains available for already-published clients. New mobile builds no longer use it for calling. Web messaging continues using the shared messaging authority; this change adds native calling UI, not a browser calling interface.
