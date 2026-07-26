# Native Authorization Matrix

The iOS application is untrusted for authorization. Every route and mutation
must derive identity and permissions from the access token at the server.

| Capability | Agent | Client | Assistant | Required server enforcement |
| --- | --- | --- | --- | --- |
| Authenticate | Yes, approved native registration | Yes, approved native registration | No | validate issuer/audience/signature/expiry/scope |
| View own account | Yes | Yes | No | derive account/profile from logical actor |
| Search agents | Active agents only | Servicing agents only | No | `AgentProfile` / `AgentClients` / grant rules |
| Search clients | Own active Client or BusinessClient CRM records only | Accepted Journey Circles peers only | No | CRM classification or Journey Circles state |
| Direct messaging | Active company agents, authorized clients | Servicing agents, authorized peers | No | `MessagingService` |
| Read/send messages | Authorized conversation participants only | Authorized conversation participants only | No | complete `(UserId, ParticipantType)` match |
| Mark read/mute/close | Own participant record only | Own participant record only | No | complete logical actor match |
| Download attachment | Authorized participant and `Clean` scan state | Authorized participant and `Clean` scan state | No | conversation authority + malware lifecycle |
| Client billing state | Only when permitted by founder/agent workflow | Own subscription only | No | billing entitlement service |
| Financial intelligence | Scoped, authorized client records | Own permitted data only | No | effective context + client ownership |
| Journey Circles | N/A | Own client profile and accepted peers | No | Journey Circles service |

## Non-negotiable identity rules

1. A bare user ID is never enough for a messaging participant.
2. Profile name, email, avatar, and UI selection are display data only.
3. The server chooses the effective actor from a validated token.
4. An Agent and Client sharing one user ID are two separate logical identities.
5. The server must exclude the exact actor identity from recipient search, not
   every identity with the same user ID.
6. Role scope displayed in iOS is a request filter; the server independently
   verifies every returned recipient and all subsequent mutations.

## Required mobile API permissions

The exact scope strings are intentionally not invented in the app. Before a
mobile endpoint exists, the platform owner must publish a versioned scope map
that grants only the capabilities above. The native app should request the
minimum scopes necessary for a signed-in role and should gracefully handle
consent denial or reduced grants.
