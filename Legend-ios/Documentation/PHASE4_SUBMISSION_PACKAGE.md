# Legend Phase 4 submission package

This is a pre-submission working package. It is not a public Privacy Policy,
Terms of Use, App Store Connect submission, or legal approval. It records only
the facts verified in the current implementation and identifies the exact
owner decisions needed before public release.

## App Store Connect metadata

| Field | Prepared value | Status |
| --- | --- | --- |
| App name | Legend | Derived from the application target |
| Subtitle | Community and progress | Draft; within the 30-character App Store limit |
| Promotional text | Discover people, share updates, and stay connected throughout your Legend journey. | Draft; within the 170-character App Store limit |
| Description | Legend brings together a personal home, community discovery, Hacs, messaging, Journey Circles, profile tools, and account controls. Access and available workspace features depend on the authenticated Client or Agent role. Members can share supported updates and media, connect with their community, manage notifications and settings, and report or block unsafe interactions. Account access includes an in-app account-closure request that immediately restricts normal Legend access while the server completes the approved lifecycle work. | Factual draft; approve before publication |
| Keywords | community,messaging,discover,journey,goals,profile,updates | Draft; no competitor names |
| Primary category | Social Networking | Recommendation based on community, messaging, discovery, and UGC surfaces |
| Secondary category | Lifestyle | Recommendation based on personal progress and Journey Circles |
| Privacy Policy URL | https://protect.mylegnd.com/Privacy | Public URL exists; app-specific approved content is required |
| Support URL | https://protect.mylegnd.com/Contact | Public URL and support email exist |
| Terms URL | https://protect.mylegnd.com/Terms | Public URL exists; app-specific approved content is required |
| Copyright | Confirm the legal copyright owner and first publication year before entry. | Owner confirmation required |
| Release notes | Initial public release: leave "What’s New" blank if this is the first App Store version; otherwise describe only approved, user-visible changes. | App Store Connect record confirmation required |

## App Privacy declaration

The checked-in privacy manifest declares the following data as collected,
linked to the user, not used for tracking, and used for App Functionality:

| App Store data type |
| --- |
| Name |
| Email Address |
| Phone Number |
| Other Financial Info |
| Contacts |
| Emails or Text Messages |
| Photos or Videos |
| Audio Data |
| Other User Content |
| User ID |
| Device ID |
| Product Interaction |

Final App Store Connect entry must reconcile these declarations against the
Distribution-signed archive and the owner-approved service-provider inventory.
The tracked iOS project has no advertising or analytics SDK and declares no
tracking domains.

## Export-compliance evidence

`ITSAppUsesNonExemptEncryption` is `false`. The native implementation uses
system HTTPS/TLS and CryptoKit SHA-256 for PKCE/cache fingerprints. It contains
no proprietary encryption implementation, VPN, or third-party cryptography
SDK in the tracked iOS project. The Account Holder must make the final App
Store Connect export-compliance certification for the Distribution archive.

## App-specific Privacy Policy factual clauses

The final owner-approved policy must accurately state all of the following:

1. **Account and identity information.** Legend receives account/profile
   information used for authentication, role resolution, and account operation,
   including name, email address, phone number, account identifiers, and
   configured profile information.
2. **Community and communications.** Legend processes posts, Hacs, comments,
   messages, reactions, reports, blocks, and related user-generated content.
   Photos, videos, and audio are processed only when a member elects to create
   or upload supported media.
3. **Device and permissions.** The app may request notification, camera,
   photo-library, microphone, calendar, reminders, and Face ID permissions only
   when the member elects to use the related feature. Push-device registrations
   support delivery and are deactivated during supported sign-out/closure flows.
4. **Financial and service data.** The service processes the financial/service
   information represented in the member’s enabled workspace. Native iOS does
   not collect payment-card data or provide a native checkout path.
5. **Service providers.** The owner must name or accurately describe the
   identity, hosting/storage, messaging/push, translation, and billing providers
   actually used in production, and must approve any required disclosures.
6. **Account closure.** A member can request closure from Profile > Settings >
   Account access by typing `DELETE`. The request immediately blocks normal
   Legend access; the server’s lifecycle worker performs the approved closure
   work and records operational audit evidence.
7. **Retention and deletion.** The owner must approve a category-specific
   schedule for profile data, UGC/media, messages/shared content, device records,
   lifecycle/audit evidence, billing/payment evidence, financial/insurance data,
   and legally required records. Do not publish a retention duration until it is
   approved.
8. **Safety.** Members can report and block supported participants/content.
   Founder moderation uses the canonical report workflow. Human visual-UGC
   review is not represented as automated.
9. **Privacy requests.** Provide the final privacy-contact method, legal entity,
   and jurisdiction-specific request process only after owner/legal approval.

## App-specific Terms factual clauses

The final owner-approved Terms must cover these implementation facts without
adding unsupported legal, insurance, financial, medical, or regulatory claims:

1. Access uses Microsoft Entra authentication and the assigned Client or Agent
   role; self-registration is not the App Review path.
2. Community, messaging, media, Hacs, and Journey Circles include user-generated
   interactions subject to the approved community/safety rules.
3. Members may report/block supported content or participants; Founder-only
   moderation actions follow the implemented report workflow.
4. Account closure requests restrict normal app access immediately and are
   completed according to the approved retention/disposition policy.
5. Any paid-service, cancellation, refund, chargeback, and web-account terms
   must match the owner-approved billing policy and Apple billing classification.
6. The owner must approve governing law, dispute terms, eligibility, content
   rights/license, prohibited conduct, suspension/termination, appeal process,
   and contact/legal-entity clauses before publication.

## App Review information template

**Sign-in required:** Yes — Microsoft Entra.

**Client reviewer account:** Supply a stable, disposable Entra account mapped to
exactly one active ClientProfile. Enter its credentials only in App Store
Connect’s private review fields; never commit them.

**Agent reviewer account:** Supply a stable Entra account mapped to exactly one
active AgentProfile. It must remain available throughout review.

**MFA:** Provide Apple a legitimate, stable review method in private Review
Notes. Do not disable production MFA or create a bypass.

**Review Notes draft:**

1. Install Legend and select Sign in.
2. Authenticate with the supplied Client reviewer account and approved MFA
   method. Select the Client role if prompted.
3. Review Home, Discover/Hacs, messaging, Journey Circles, Profile, reporting,
   and blocking.
4. Account closure is at Profile > Settings > Account access. Type `DELETE` to
   request closure. Use only the supplied disposable Client review account.
5. Authenticate with the supplied Agent reviewer account to review the Agent
   workspace, Discover/Hacs, messaging, Journey Circles where available, and
   Profile.

## Screenshot capture plan

After the Distribution-signed TestFlight build passes on designated test
resources, capture one to ten redacted PNG/JPEG screenshots per size:

1. Home / core experience
2. Discover / community
3. Hacs or supported social media
4. Messaging
5. Journey Circles
6. Profile and account controls

Recommended source sizes: 1290 x 2796 portrait for a 6.9-inch iPhone and
2064 x 2752 portrait for a 13-inch iPad. The app supports iPad; no real member
data may appear in any capture.

## Owner decision form

### 1. Retention and disposition

**Current behavior:** Closure immediately blocks access. The canonical worker
cancels through the existing billing authority, removes/deactivates supported
social and push state, performs the client Entra lifecycle action, redacts the
client profile at the correct stage, and retains lifecycle/audit evidence.

**Recommended launch default:** Preservation-first for legally sensitive,
financial, shared, and provider-managed records; remove access and direct
app-owned profile/device/social state only through the existing lifecycle
authority until an approved category schedule exists.

**Decision:** `YES / NO — Approve this default pending a category-specific
retention schedule.`

### 2. External Agent Entra identity

**Current behavior:** Deactivate Legend Agent access/profile; retain the
organization-managed external Entra identity.

**Recommended launch default:** Retain the external identity and deactivate
only Legend access.

**Decision:** `YES / NO — Approve retained external Entra identity on Agent
closure.`

### 3. Refunds and chargebacks

**Current behavior:** Client closure invokes the existing billing cancellation
authority. Native iOS has no billing path.

**Recommended launch default:** Cancellation stops future platform-managed
charges; refunds and chargebacks follow the approved provider/business process
and are not automatically issued by closure.

**Decision:** `YES / NO — Approve this default.`

### 4. Apple billing classification

**Current evidence:** No StoreKit, native pricing, checkout, upgrade,
subscription-management, or external-purchase CTA exists in the tracked iOS
source. Web-managed paid access can unlock service functionality.

**Required decision:** `A / B — A: obtain owner/legal approval for the intended
Apple guideline classification and territories before submission; B: change the
business model and return for an authoritative product review.`

### 5. Visual UGC moderation

**Current behavior:** User report/block and Founder moderation exist; automated
visual scanning does not.

**Recommended launch default:** Name a primary and backup human reviewer; review
routine reports within one business day; define an urgent severe-abuse path;
retain moderation resolution evidence; document removal/restriction and appeal
handling.

**Decision:** `YES / NO — Approve this operating default and name the two
reviewers.`

### 6. Privacy Policy approval

**Decision:** `YES / NO — Approve the factual clauses above after inserting the
legal entity, contact method, service-provider inventory, and retention
schedule.`

### 7. Terms approval

**Decision:** `YES / NO — Approve the factual clauses above after inserting
governing law, eligibility, content rights, prohibited conduct, billing terms,
and dispute/appeal terms.`
