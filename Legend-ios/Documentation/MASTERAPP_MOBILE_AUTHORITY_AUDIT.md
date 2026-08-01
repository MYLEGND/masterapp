# MASTERAPP Mobile Authority Audit

> Historical pre-implementation decision record (2026-07-25). This document
> explains why the native API could not safely reuse browser authentication; it
> is no longer the current API-status source. The resulting bearer API is now
> implemented in `AgentPortal/Mobile`. Use
> [API_CONTRACTS.md](API_CONTRACTS.md), the mobile controllers, and their tests
> for the current contract and release assessment.

## Scope and decision record

This document records the verified mobile authority boundary before native
screen implementation. Its architectural rule remains current: native code
presents data and submits user intent; MASTERAPP remains the sole owner of
identity, authorization, billing, messaging, finance calculations, Journey
Circles, and persistence.

Audit date: 2026-07-25
Repository branch: `meta-authority-cleanup`
Mobile source root: `Legend-ios/`

No existing iOS application, Xcode project, mobile API contract, bearer-token
authentication scheme, or native SignalR client integration was found in this
repository. The existing product is two authenticated ASP.NET Core MVC web
applications. That distinction is material: their JSON endpoints currently
depend on browser cookies and anti-forgery validation and are not a safe native
mobile contract.

Full Xcode 26.6 is available in the development workspace and compiles the
native project. The host CoreSimulator service is unavailable in this session,
so runtime simulator execution must be performed on a machine with a working
simulator service or on physical hardware.

## Audited solution topology

| Layer | Verified responsibility | Authoritative implementation |
| --- | --- | --- |
| Agent application | Agent-only MVC UI, founder context, CRM, agent-side finance and messaging entry points | `AgentPortal` |
| Client application | Client MVC UI, subscription gate, client finance and messaging entry points | `ClientApp` |
| Domain | Entities, messaging roles, Journey Circles, billing and financial concepts | `Domain` |
| Infrastructure | EF Core persistence, business services, authorization decisions, external providers | `Infrastructure` |
| Shared UI | Shared web views, scripts and styling; not an iOS transport layer | `SHARED` |
| Database | `MasterAppDbContext` maps the persisted authority model | `Infrastructure/Data/MasterAppDbContext.cs` |

Both portals register Microsoft Identity Web / OpenID Connect and use browser
authentication. `AgentPortal` uses the OpenID Connect web-app scheme and
`ClientApp` uses cookie plus OpenID Connect. Neither application currently
registers a bearer authentication handler intended for a public native client.

## Identity and authorization authority

### Logical messaging identity

Messaging identity is **not** a user ID by itself. The authoritative key is:

```
(Normalize(UserId), ParticipantType)
```

`ParticipantType` is a persisted and enforced part of identity. A physical
identity may legitimately have both Agent and Client logical participants.
The following verified components preserve that distinction:

- `Domain/Entities/MessageConversationParticipant.cs`
- `Domain/Entities/Message.cs`
- `Infrastructure/Data/MasterAppDbContext.cs`
- `Infrastructure/Messaging/MessagingService.cs`
- `Infrastructure/Messaging/MessagingProfileImageResolver.cs`
- `Infrastructure/Messaging/MessagingHub.cs`

The participant index is composite on conversation, user ID, and participant
type. Direct conversation keys include participant type, and SignalR group
names include both normalized role and normalized user ID. A mobile client must
send or receive an opaque participant reference containing the role; it must
never infer role from display name, email, avatar, or a bare user ID.

### Messaging authority

`Infrastructure/Messaging/MessagingService.cs` is the server authority for
recipient visibility and messaging permission.

| Actor | Eligible direct recipients | Server decision |
| --- | --- | --- |
| Agent | Active company agents; own active Client or BusinessClient CRM records | Agent profiles, CRM classification, and `AgentClients` relationship are checked server-side |
| Client | Servicing agents; accepted Journey Circles peers | Active `AgentClients` relationship or active `ClientAgentMessagingGrant`; Journey Circles connection state is checked server-side |
| Assistant | No messaging access | Agent portal resolver and `AssistantBlock` reject the role |

`AgentClients` is the primary servicing authority. An active
`ClientAgentMessagingGrant` is an explicit secondary override for delegated,
temporary, secondary, manual, or historical access. Client code cannot grant
access by constructing a recipient identifier.

### Profile and avatar authority

`MessagingProfileImageResolver` resolves profiles by complete logical
identity only:

- Agent participant → active `AgentProfiles` record.
- Client participant → `ClientProfiles` record.

It rejects ambiguous matches instead of falling back to another role’s image.
The iOS client may render initials only when the server has returned no profile
image for that same logical participant. It must not search a different profile
table or synthesize a fallback URL.

### Billing authority

Billing, entitlement, payment-method metadata, and recurring subscription
state are server-owned. Relevant components include:

- `Infrastructure/Billing/BillingEntitlementService.cs`
- `Infrastructure/Billing/ClientSubscriptionService.cs`
- `Infrastructure/Billing/ClientPaymentMethodService.cs`
- `ClientApp/Infrastructure/ClientSubscriptionAuthorizeFilter.cs`
- `ClientApp/Infrastructure/ClientSubscriptionActiveHandler.cs`

The client app’s browser subscription filter is not portable to iOS. A future
mobile entitlement endpoint must return server-derived lifecycle, access, and
next-action state. The iOS client must not calculate entitlement locally.

### Financial intelligence authority

Financial imports, rules, streams, Expense Lens synchronization, and findings
are registered through `AddMasterAppFinancialIntelligence()` and are persisted
by `MasterAppDbContext`. Existing state controllers are browser web APIs:

- `AgentPortal/Controllers/API/FinanceToolStatesController.cs`
- `ClientApp/Controllers/Api/FinanceToolStatesController.cs`

They use effective web contexts and anti-forgery protection. They are not an
authorization contract for a native client until an authenticated mobile API
surface is explicitly added.

### Journey Circles authority

Journey Circles remains client-profile scoped and server-authorized:

- `Infrastructure/JourneyCircles/JourneyCirclesService.cs`
- `ClientApp/Controllers/JourneyCirclesController.cs`

Peer messaging requires two active opted-in profiles, an accepted connection,
and no relevant block. Recommendations, connection acceptance, blocks, reports,
and profile selections remain server-owned. The current modal endpoint is a
cookie/anti-forgery web workflow rather than a native API contract.

## Existing endpoint assessment

### Messaging web surface

`Infrastructure/Messaging/MessagingControllerBase.cs` is inherited by both
portal messaging controllers. It currently exposes MVC/JSON browser routes
such as:

- `GET /Messaging/Conversations`
- `GET /Messaging/Recipients`
- `GET /Messaging/Conversations/{conversationId}`
- `POST /Messaging/Conversations`
- `POST /Messaging/Conversations/{conversationId}/Messages`
- read, mute, close, and attachment routes

The controller resolves the current actor from `HttpContext`; mutable requests
use anti-forgery validation, and recipient selection is protected with a
server-issued contact key. These are correct for the web applications but
cannot be repurposed by an iOS app through copied cookies or by disabling
anti-forgery validation.

### Realtime web surface

`Infrastructure/Messaging/MessagingHub.cs` is available at `/messaginghub` in
both web applications and requires authenticated server identity. The shared
web client (`SHARED/wwwroot/js/messaging.js`) is a browser SignalR client.
Redis is intentionally disabled; no Redis backplane must be added as part of
mobile work. Azure SignalR is not presently configured.

### Other web APIs

The audited API endpoints are scoped to existing browser surfaces, including
finance state and webhooks. No general `/api/mobile` versioned API or native
OAuth audience/scope is presently registered. A native app must not call a
webhook or scrape an MVC view to obtain operational data.

## Required mobile contract gap

There is no currently configured native token path. The first server-connected
native messaging vertical slice is therefore blocked by a genuine missing
contract, not by a UI implementation detail.

Before a native app can authenticate to production, the platform needs all of
the following server-owned decisions:

1. An approved public-native application registration with a redirect URI
   appropriate for system-browser PKCE and no embedded credentials.
2. A documented audience and minimal delegated scopes for the mobile app.
3. A JWT bearer authentication scheme in the receiving backend that validates
   issuer, audience, signature, expiration, and scope.
4. A versioned, least-privilege mobile API surface that resolves the server
   actor from the validated access token and preserves
   `(Normalize(UserId), ParticipantType)`.
5. Equivalent server-side authorization and entitlement enforcement for every
   mobile operation; no mobile endpoint may accept an actor, profile, tenant,
   or role as trusted input.
6. A decided realtime policy: bearer-authenticated SignalR handshake or a
   separate server-owned notification/reconciliation approach.

This gap will be represented in the app as an explicit unavailable state. No
fake login, mock token, local entitlement, copied web cookie, or hard-coded
production URL will be used to conceal it.

## Mobile authorization matrix

| Mobile capability | Eligible actor | Server authority required | Current status |
| --- | --- | --- | --- |
| Sign in | Agent or Client | Native PKCE registration, token validation, actor resolution | Missing mobile contract |
| View self | Agent or Client | Server derives one permitted logical identity | Missing mobile contract |
| Message recipient search | Agent | `MessagingService.ListRecipientsAsync`, selected role scope | Browser-only contract today |
| Message recipient search | Client | Servicing relationship / grant / accepted Journey Circles connection | Browser-only contract today |
| Read/send message | Agent or Client | `MessagingService`; opaque contact key or equivalent server-issued reference | Browser-only contract today |
| Realtime message receipt | Agent or Client | `MessagingHub` with bearer-compatible authentication or reconciliation endpoint | Missing mobile transport contract |
| Attachment upload/download | Authorized participant | Malware state and conversation authorization | Browser-only contract today |
| Billing management | Client | Billing entitlement and payment services | Browser-only contract today |
| Finance data/read state | Agent or Client | Effective context and client ownership rules | Browser-only contract today |
| Journey Circles connections | Client | `JourneyCirclesService` | Browser-only contract today |

## Security and privacy findings

The iOS app must treat the following as sensitive:

- identity and profile data (name, email, phone, image, role);
- conversation content, unread state, read receipts, attachment metadata;
- billing state and payment-method metadata;
- financial accounts, transactions, findings, and financial tool state;
- Journey Circles profile preferences, matches, blocks, and reports.

Minimum client rules:

- store only short-lived authentication material in Keychain;
- do not log message content, tokens, payment metadata, raw financial values,
  or full identifiers;
- redact support diagnostics by default;
- use server-issued opaque identifiers for navigation and actions where
  available;
- do not persist downloaded attachments outside the app’s protected storage;
- clear in-memory sensitive state on logout and app lock;
- show server-returned authorization failures without exposing internal
  infrastructure detail.

## Implementation boundary for Phase 0

Permitted now:

- create an isolated native Xcode project under `Legend-ios`;
- create protocol-driven, testable native foundation code;
- create environment and observability templates with placeholders only;
- document the exact server contract that is missing;
- build native presentation and deterministic state tests that do not claim
  live authorization works.

Not permitted without an approved server contract:

- bypassing web authentication with cookies, a WebView, or copied tokens;
- adding unauthenticated endpoints;
- treating display attributes as authorization;
- locally calculating billing, finance, recipient access, or subscription
  status;
- adding duplicate backend business logic in Swift;
- production deployment, production migration, or app-store submission.

## Environment/tooling audit

Swift 6.2.3 is installed through the Command Line Tools. Full Xcode is not
installed or selected: `xcodebuild` reports that the active developer directory
is `/Library/Developer/CommandLineTools`; `simctl` is unavailable. This is an
environmental blocker to running an Xcode build, iOS simulator, UI tests, and
archive validation in this workspace. The native project and tests can still
be created, but their final Xcode validation requires a Mac with full Xcode.

## Next implementation sequence

1. Create the native project, debug-safe environment templates, and core
   architecture without production values.
2. Add deterministic tests for configuration, identity parsing, routing,
   privacy redaction, and offline/recovery state.
3. Implement the messaging vertical slice behind an explicit
   `MobileServerContractUnavailable` boundary.
4. Once the platform owner supplies the approved PKCE/bearer/mobile API
   contract, replace that boundary with an audited server-backed adapter and
   validate sign-in, recipient search, send/read, and reconnect behavior.
