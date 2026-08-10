# Mobile contract audit

`AgentPortal/Mobile` is the source of truth. All routes below require the existing mobile bearer policy; actor-sensitive routes use the server-resolved `X-Legend-Participant-Type` header. The client never supplies an authorization decision. Errors are the mobile problem envelope (`code`, user-safe `message`, `correlationId`), mapped by `LegendApiClient` to `LegendApiException` and then to a concise presentation state.

## Session and account

| Method | Route | Contract family |
| --- | --- | --- |
| GET | `/api/v1/mobile/session` | `MobileSessionResponse` |
| POST | `/api/v1/mobile/session/select-role` | `SelectRoleRequest` → role response |
| GET/PUT | `/api/v1/mobile/account` | account projection/update |
| PUT | `/api/v1/mobile/account/privacy` | privacy projection |
| PUT | `/api/v1/mobile/account/avatar` | base64 profile-media request |
| GET | `/api/v1/mobile/account/username-availability` | username projection |
| GET/POST | `/api/v1/mobile/account/lifecycle`, `/pause`, `/resume`, `/deletion-request` | lifecycle/confirmation |

## Home, scripture, and finance

| Method | Route | Contract family |
| --- | --- | --- |
| GET | `/api/v1/mobile/home` | `MobileHomeResponse` |
| GET | `/api/v1/mobile/financial` | `FinancialSnapshot` |
| GET | `/api/v1/mobile/agent/clients`, `/agent/leads` | server agent projections |
| GET | `/api/v1/mobile/daily-scripture/management` | scripture management projection |
| POST/PUT/DELETE | `/api/v1/mobile/daily-scripture/overrides[/{id}]` | scripture override DTOs |

## Messaging

`GET /messaging/conversations` is paged (`take`, `skip`); message lists use `take`. Attachments are multipart and server scanned. Recipient-facing bodies and optional `originalBody` are server projections; translation is never an Android operation.

| Method | Route family |
| --- | --- |
| GET/POST | `/messaging/conversations`, `/messaging/conversations/{id}`, `/messages`, `/read`, `/image` |
| POST | `/messaging/conversations/{id}/messages/{messageId}/attachments` |
| PUT/DELETE | conversation pin, mute, close, and message delete |
| GET/POST/PUT/DELETE | group, participant, collaborator, promotion, join, recipients, verification, controlled-resource, activity, and call-options endpoints under `/messaging` |

## Social, discovery, and Journey Circles

| Method | Route family | Server authority retained |
| --- | --- | --- |
| GET/POST/PUT/DELETE | `/social/feed`, profile posts/follows/requests, post create/edit/delete, media, staged media, publish | feed visibility, moderation, media processing |
| GET | protected `/social/media/{id}` and `/preview` | access checks and media bytes |
| POST | reaction, comments, follows, saves, reposts, shares, views, visits | social rules and counters |
| GET | creator/post/profile insights and music search | server projections |
| GET | `/discovery/search`, `/discovery/profiles/{id}` | consent-aware ranking and relationships |
| GET/PUT/POST | `/journey-circles`, profile, connections, response/disconnect, block/report | matching, connection and safety rules |

## Notifications and safety

| Method | Route family | Notes |
| --- | --- | --- |
| GET/POST | `/notifications`, unread count, mark read, clear badges | server notification ledger is authoritative |
| GET/PUT/DELETE | `/notifications/devices/apns[/status]` | existing APNs-only platform transport contract |
| POST/GET | `/community-safety/blocks`, reports, report resolution | server owns enforcement and reviewer permissions |
| GET/POST | `/founder/accounts`, remove, batch remove, archive/purge | Founder-only server enforcement |

## Android coverage in this foundation

Implemented Android transport/vertical slices cover session/role, home, financial, account/lifecycle/privacy/language, conversations/send/read, social feed/create/media/reaction/comment, protected media, discovery, Journey connection requests, and member block/report.

The remaining inventoried endpoints are documented server contracts for the next feature slices; they have deliberately not been recreated with guessed Android DTOs or local business rules.
