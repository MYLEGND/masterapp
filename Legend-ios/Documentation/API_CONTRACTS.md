# Mobile API Contracts

## Status

The native API is implemented in `AgentPortal/Mobile` under
`/api/v1/mobile` and is protected by the `LegendMobileApi` bearer policy. It
uses the existing MASTERAPP services for identity, messaging, profile, social,
financial, Journey Circles, and CRM authority; the native client does not
receive a browser cookie or choose a trusted actor.

Live deployment remains configuration-gated. The approved native Entra
registration and the matching `MobileAuth` deployment values must be supplied
outside source control before any environment can authenticate a device. An
unconfigured server fails closed and an unconfigured iOS build stays on its
configuration state.

The routes and DTOs in the Swift feature adapters plus the matching
`AgentPortal/Mobile` controllers are the executable contract. This document
records the transport invariants those implementations must preserve.

## Transport baseline

- HTTPS only.
- `Authorization: Bearer <access token>` only after approved native PKCE flow.
- Audience, scopes, issuer, signature, expiration, and tenant boundary are
  validated on the server.
- JSON uses ISO-8601 UTC timestamps. SwiftUI formats them in the device locale
  and timezone.
- Errors use a stable code, user-safe title, and correlation ID; no stack trace
  or sensitive server detail is returned to the device.
- All mutations use idempotency keys where retry could create duplicate state.

## Required bootstrap contract

`GET /api/mobile/v1/session`

The server must return the authenticated effective actor and authoritative
capabilities. It must not accept actor/profile/role selection from the app.

```json
{
  "actor": {
    "userId": "opaque-server-identifier",
    "participantType": "Agent",
    "profileId": "server-profile-id",
    "displayName": "Server-resolved display name",
    "avatar": { "kind": "remote", "url": "authorized-image-url" }
  },
  "capabilities": ["messaging.read", "messaging.send"],
  "entitlement": { "state": "active" }
}
```

## Required messaging contracts

The mobile API must use the existing `MessagingService` rather than reimplement
recipient or conversation logic.

| Operation | Required server behavior |
| --- | --- |
| `GET /api/mobile/v1/messaging/conversations` | return only conversations for exact logical actor; include server-derived unread count and counterpart profile identity |
| `GET /api/mobile/v1/messaging/conversations/{id}` | verify exact participant before returning messages, sender type, attachment scan state, and counterparty profile |
| `GET /api/mobile/v1/messaging/recipients?scope=` | use server-recognized scope; return only authorized recipients with opaque contact reference |
| `POST /api/mobile/v1/messaging/conversations` | accept opaque recipient contact reference and client idempotency key; reuse existing direct conversation where applicable |
| `POST /api/mobile/v1/messaging/conversations/{id}/messages` | verify participant and send through MessagingService; server assigns sender role and UTC timestamp |
| `POST /api/mobile/v1/messaging/conversations/{id}/read` | apply only to exact actor participant record |
| attachment endpoints | preserve Pending → Scanning → Clean/Rejected lifecycle; only Clean can be downloaded |

The server response must include explicit `participantType`, `profileId`, and
server-resolved avatar data for every participant. It must not depend on native
role inference.

## Realtime contract decision required

The current `/messaginghub` is authenticated for browser sessions. Mobile needs
one documented path:

1. a bearer-token-capable SignalR connection validated by the same backend, or
2. APNs notification plus bounded foreground/background reconciliation using
   the authenticated conversation APIs.

The choice must retain the existing complete messaging identity and SignalR
group semantics. Redis must remain disabled unless platform architecture is
explicitly changed independently.

## Contract acceptance tests

Before Legend iOS enables a live slice, backend tests must prove:

1. invalid, expired, wrong audience, and insufficient-scope tokens fail;
2. an Agent and Client with the same user ID remain distinct;
3. direct conversations are reused without duplicate creation;
4. recipients are restricted by the existing service rules;
5. unread/read state honors sender type;
6. attachments are unavailable before a Clean scan state;
7. error payloads contain no secrets or stack traces.
