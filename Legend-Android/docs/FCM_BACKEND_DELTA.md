# FCM backend delta

The required backend transport extension is implemented. It is a platform-neutral
delivery change—not an Android business rule or a second notification system.

Current `MobilePushDevices` is APNs-specific: one token hash, APNs `Environment`, APNs-only routes, diagnostics, and outbox worker. The minimum safe extension is:

1. `MobilePushDevice.Provider` distinguishes `apns` and `fcm`; uniqueness is `(provider, tokenHash)`. Existing rows migrate as `apns`.
2. `NotificationEngine` owns both registration paths while preserving its existing ledger creation, recipient targeting, localization, and badge reconciliation.
3. Authenticated typed-actor endpoints exist at `PUT`/`DELETE /api/v1/mobile/notifications/devices/fcm`; opaque tokens are never returned or logged.
4. The FCM HTTP v1 worker consumes only existing `MobilePushDelivery` records for `fcm` devices. It forwards server-created recipient-localized title/detail, current badge, notification id, and conversation id.
5. APNs routes and APNs worker behavior remain unchanged; the APNs worker filters `Provider = apns`.

FCM delivery remains configuration-gated until a LEGEND Firebase project supplies its Android `google-services.json`, FCM project id, and a server-side service-account JSON through the deployment secret store. Neither artifact belongs in source control. Without those values, Android does not manufacture an FCM identity and the server safely suppresses FCM delivery while its notification ledger remains authoritative.
