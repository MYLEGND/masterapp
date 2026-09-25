# Website platform repair and staging record

Base: `7190820b` on `legend/approved-changes`.
Authorization: implement and stage only; no deployment, live migrations, DNS changes, certificate activation, or production promotion.

## Confirmed audit findings

- Founder main LEGEND management was removed with the analytics editor endpoint; replacement profile flow only exposed Protect.
- Business content used the permanent CommerceBusiness ID, but membership checks depended on mutable contact email and profile updates rewrote ownership records.
- Business preview used a separately authored page, not the existing LEGEND template.
- Save replaced the same document served publicly; there was no draft/published boundary or immutable history.
- Signed editor tickets were accepted without checking current membership or business activity.
- Domains were unverified strings. Imports, publishing readiness, revision history, and custom-host routing were not implemented.
- Shared editor contracts discarded links, buttons, video, placement, and section background edits.
- Profile management was a plain edit/open modal.
- Automatic release dispatch ignored validation-only mode; branch parity independently promoted staged history.

## Required ownership boundaries

| Website | Permanent owner | Management entry |
| --- | --- | --- |
| Main LEGEND | Founder global website authority | Founder profile |
| Protect agent | Existing agent tracking owner | Agent profile |
| Business | Existing CommerceBusiness ID and stable membership | Business Client profile |

Domains identify public sites, never authorize management. Protect remains agent scoped within LEGEND. Business content and inquiries never fall back to the Founder insurance pipeline.

## Acceptance ledger

All entries below require executed verification; implementation alone is not a pass.

- Profile Founder/agent/business authorization, revocation, inactive business, cross-business rejection.
- Existing content retained; draft save does not alter published content; atomic publication, conflicts, version history, rollback, export.
- Import preserves source inventory and route mapping, reports unsupported integrations, avoids invented claims and repeated-import overwrite.
- Shared editor persists actual template links/labels, buttons, text, images, video, container colors, theme, responsive placement and deletion.
- Drag placement exposes alignment grid, remains within page frame, supports keyboard movement and undo.
- Business pages reuse canonical LEGEND rendering with business facts, verified host routing, correct metadata and public sitemap.
- Domain ownership verification, TLS readiness, failure/retry states and health use one configured provider authority; never fabricate active status.
- Scoped inquiries persist against the owning business, with replay protection and authorized management inbox.
- Shared assets are identical between consumers; no duplicate editor, stylesheet override, or competing tenant store.
- Validation-only staging survives descendants and automatic dispatch; no Azure login, migration, deployment, production merge, or branch cleanup.

## Verification boundaries

Local in-process tests, renderer tests, browser interaction, SQL transaction tests, configured provider tests, and deployed production proof are distinct. This staging authorization does not permit live writes. Missing provider or live database configuration must remain explicitly unverified.
