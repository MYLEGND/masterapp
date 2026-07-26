# Product Architecture

## Product boundary

Legend iOS is a native SwiftUI presentation client. MASTERAPP services remain
the system of record. The app has no local database for business authority and
does not reproduce messaging, billing, financial-intelligence, or Journey
Circles rules in Swift.

```
SwiftUI views
    ↓ user intent / display state
Feature stores and use cases
    ↓ typed protocol
Authenticated mobile API adapter
    ↓ access token only after approved PKCE contract
MASTERAPP mobile API
    ↓
Existing Domain + Infrastructure services + MasterAppDbContext
```

## Native layers

| Layer | Native responsibility | Must not do |
| --- | --- | --- |
| `App` | app lifecycle, dependency composition, scene routing | decide identity or access |
| `Configuration` | read non-secret environment values and reject incomplete contracts | supply defaults for live authority |
| `Core` | Keychain token storage, browser session, HTTP transport, diagnostics | persist sensitive business caches |
| `Features` | accessibility-first SwiftUI and observable state | duplicate server business rules |
| `Messaging` | render server DTOs, send typed intent, reconcile realtime state | construct participants from names or user IDs |

## Session architecture

The intended sign-in flow is `ASWebAuthenticationSession` with Authorization
Code + PKCE. The app supplies a random state and verifier; the identity
provider redirects to a registered app callback; tokens are stored in Keychain.

The backend must validate those tokens and derive the actor. The app may never
select Agent versus Client authority, impersonate a profile, or send a trusted
actor value. A dual-role physical identity is represented by a server-issued
logical identity containing both normalized user ID and participant type.

## Messaging vertical slice

The native messaging module is built around server DTOs and `MessagingAPI`.
It supports only the following eventual data flow:

1. The approved mobile API returns an actor-selected, authorized conversation
   list.
2. The app requests a selected conversation by server conversation ID.
3. Recipient search uses a server-provided scope and opaque contact reference.
4. Send, read, mute, close, and attachment actions submit server intent only.
5. Realtime events and foreground reconciliation update the local view state.

Until the bearer/mobile API contract exists, the app presents a clear blocked
state. It does not ship seeded conversations, fake recipients, or a simulated
login.

## Eventual feature order

1. Authentication, session restoration, account/profile, messaging.
2. Agent CRM list/detail and client profile views, subject to mobile API
   contracts.
3. Finance dashboards and read-only financial intelligence findings.
4. Explicitly authorized finance updates, attachments, billing management, and
   Journey Circles workflows.

Each phase requires a server-owned contract and authorization review before UI
work begins.
