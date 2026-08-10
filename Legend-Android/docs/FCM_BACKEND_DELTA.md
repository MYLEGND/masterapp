# FCM backend delta

Backend work is required before Android FCM tokens can be registered or delivered. This is a genuine platform-neutral transport gap, not an Android business rule.

Current `MobilePushDevices` is APNs-specific: one token hash, APNs `Environment`, APNs-only routes, diagnostics, and outbox worker. The minimum safe extension is:

1. Add a provider discriminator (`apns` / `fcm`) to `MobilePushDevice`; scope uniqueness by `(provider, tokenHash)`.
2. Generalize registration/deactivation behind `NotificationEngine` without changing ledger creation, recipient targeting, localization, or badge reconciliation.
3. Add `PUT`/`DELETE /api/v1/mobile/notifications/devices/fcm` with the same typed actor resolution and token redaction as APNs.
4. Add an FCM outbox transport that consumes the existing `MobilePushDelivery` records only for `fcm` devices. It must use the server's recipient-localized notification title/detail and current server badge—not Android-generated notification semantics.
5. Preserve the existing APNs routes and worker unchanged in behavior.

No backend code or schema is changed in this Android bootstrap. The Android `FirebaseMessagingService` receives server messages and triggers REST reconciliation, but intentionally neither logs nor persists a raw FCM token until this contract exists.
