# Account Lifecycle and Privacy Authority

## Scope and authority

`AccountLifecycleRecord` is the one persisted authority for the access state of
one resolved, typed Legend account: canonical Entra object ID, participant type,
and profile ID. It is not a profile or billing mirror.

`AccountLifecycleService` is the only lifecycle service. Native and browser
clients resolve their subject from the authenticated principal and existing
typed profile; they never submit an authoritative user, profile, subscription,
or identity identifier.

The legacy Agent Portal CRM delete action no longer deletes a sole client
profile, finance data, or Entra identity. It preserves the existing safe
multi-agent unlink behavior, and otherwise directs the member to Account
access. This prevents a second hard-delete path from bypassing the lifecycle.

The current lifecycle states are `Active`, `Paused`, `DeletionRequested`, and
`Closed`.

- Pausing immediately denies normal mobile and web workspace access. The only
  allowed web destination is the member's profile management route, where the
  account can be resumed.
- For a client, the lifecycle delegates the membership transition to the
  existing billing orchestrator. Only a Legend platform-managed membership is
  paused; a provider-managed membership fails safely without changing a
  provider record.
- A deletion request immediately denies Legend access and is deliberately
  non-reversible in the client. It is **not** a claim that all records have
  already been erased.

## Data graph and deletion disposition

This is the implementation inventory. `Policy required` means no retention
period or irreversible action has been invented in code.

| Data authority | Account relationship | Disposition for a confirmed closure | Current status |
| --- | --- | --- | --- |
| Entra external identity and app sessions | Canonical object ID | External provider action required: revoke sessions/access, then delete only when permitted | Policy/workflow required |
| `ClientProfile`, `AgentProfile`, `MobileProfileSettings` | Typed profile ID | Delete or de-identify profile fields after identity/retention decision | Policy/workflow required |
| Follows, saves, views, shares, reposts, reactions | Social author or actor identity | Delete account-owned activity | Policy/workflow required |
| Posts, Hacs, stories, post media | Social author/profile and media metadata | Delete account-owned content and its blob through the canonical media worker | Policy/workflow required |
| Comments | Comment author; shared post history | Delete or de-identify under approved policy | Policy required |
| Conversations, participants, messages, attachments, translations | Typed message participant; shared conversation | Preserve other participants' history and de-identify the departed participant where allowed | Policy/workflow required |
| Mobile notifications, global badges, push-device registrations, delivery records | Recipient identity/device | Delete device registrations and clear active delivery state; retain audit only if policy requires | Policy required |
| Journey circles, connections, blocks, reports, moderation | Profile and other shared members | Preserve shared/safety records with de-identification where required | Policy required |
| Households, invitations, appointments, actions, commitments, CRM/workstation records | Client profile or shared household | Shared record: preserve only what policy requires and detach/de-identify where allowed | Policy required |
| Financial connections, encrypted provider tokens, imported accounts, transactions, plans, streams, findings, feedback | Client profile/household | Retain or delete only under financial/insurance policy; revoke external provider access separately | Legal/policy and provider action required |
| Verification/access grants and language-translation access | Profile or participant | Revoke active access; retain a minimum audit record only if policy requires | Policy required |
| `ClientSubscription`, payment methods, payments, entitlements, provider events, billing audit | Client profile/subscription/provider | Billing authority decides cancellation/retention; provider action where externally managed | Legal/policy and provider action required |
| Compliance, security, operational and audit records | Account or event actor | Retain only under explicit legal/business policy and minimize identifiers where allowed | Legal/policy required |

No deletion worker, broad cascade, provider cancellation, or blob purge has been
added until each policy-required disposition is approved. This prevents a UI
request from deleting shared conversations, regulated records, or media
metadata without its blob counterpart.

## Native and web UX

The mobile control is in **Profile → Settings → Account access**. It presents a
reversible pause first; account closure is a separate screen that requires the
member to type `DELETE`. It is not placed on the home screen or in primary
navigation.

When paused, the iOS shell presents only the account-resume screen. Mobile API
controllers reject all normal feature calls before their feature logic runs.
Client and agent web portals redirect paused accounts to their profile
management route, where only profile management, resume, and sign-out are
available.

## Privacy/legal surface

The native app reads one configured public policy destination:
`https://www.mylegnd.com/privacy-terms`. The URL is supplied through the existing
build configuration and Info.plist, not copied into the app or duplicated in a
feature view.

The public policy's final app-specific disclosures, the retention schedule,
the deletion fulfillment procedure, and a public support destination still
require legal/operations approval. Until then, account closure may be initiated
and access terminated, but a completed deletion must not be represented as
verified.
