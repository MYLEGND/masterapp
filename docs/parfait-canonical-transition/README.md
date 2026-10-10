# Parfait canonical-commerce migration: isolated authority

Working branch: isolation/parfait-canonical-commerce-20261010
Protected base: 5fcc93fb4279ec7c875f9c3fb27bc3a31a1cca96
Release authority: NONE. No merge or deployment without explicit Founder approval.

Future deployment route: Legend/fast-track-release, **only after commerce-capable fast-track admission is validated and explicitly authorized**. The current fast-track is UI-only and auto-publishes certain changed paths on push. See fast-track-handoff.md for exact branch/divergence, affected-app planning, blocked triggers and stop conditions.

## Invariants

Parfait's original business-specific design, layout, styles, navigation, products, images, URLs, business and payment identities must be preserved. The default LEGEND business-site template may never replace Parfait's presentation. Non-Parfait scoped websites retain their own published business website shells.

## Existing common owners

Domain + Infrastructure: business identities, SQL commerce tables, billing orchestrator, analytics and business scoping. WebsiteEditing/WebsitePublishing/WebsiteRuntime: shared domain/CMS and publication. Cloudflare bridge currently routes /store, /commerce/manage, and product uploads to Parfait App Service, so ParfaitApp cannot safely be removed yet.

## Remaining Parfait-local owners

ParfaitProductService, ParfaitOrderService, ParfaitCustomerAutomationService, cart/checkout controllers, custom Razor views, team/internal console, Graph email adapter, JSON automation/team state, product uploads, legacy profile and media.

## First isolated milestone

CommerceStorefrontPresentation is infrastructure-owned and preserves the existing exact layout selection. Its selection is consumed by Parfait ViewStart. A regression suite pins 16 original visual assets by Git blob SHA including CSS, JS, storefront views, home view and original favicon. No baseline asset has been modified. Hash checks are source integrity, not live visual parity.

## Sequenced migration, with mandatory gates

1. Read-only inventory of current production data/URLs plus desktop and mobile screenshot, DOM and CSS baselines.
2. Lift product, order, checkout and automation execution to one business-scoped shared commerce owner. Preserve current routes as adapters during transition; do not create duplicate state or payment engines.
3. Reconcile automation/team JSON and product uploads to durable business-scoped storage, preserve identities and existing URLs, and verify backups/readback.
4. Establish a tenant-owned, versioned Parfait presentation based on the original assets, without applying the generic scoped default style.
5. Repoint transport and domain only after equivalent isolated runtime passes tests; keep rollback and old Parfait host until proven.
6. Obtain explicit approval before any merge, deployment, DNS modification, data migration or application retirement.

### Known blockers

- Customer automations still use local JSON records.
- Product images still use Parfait-specific uploads and legacy paths.
- Cloudflare directs all public tenant commerce to Parfait origin.
- Non-Parfait draft previews currently fall back to the Parfait legacy layout if no published website shell exists. This behavior has been preserved for compatibility and requires a separately tested canonical preview replacement.
- Builds and live behavior are not yet validated for this isolated branch. Do not call the migration ready for cutover.
