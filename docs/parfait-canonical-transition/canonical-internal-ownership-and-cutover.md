# LEGEND canonical commerce internal ownership and Parfait host retirement

Status: **IN PROGRESS — NO DOMAIN CUTOVER OR APP RETIREMENT AUTHORIZED BY THIS DOCUMENT**.
This is the required all-business contract, not a Parfait-specific duplicate.
Use the existing `Legend/fast-track-release` release authority after exact-head validation.

## 2026-10-11 verification checkpoint

- Continue PR #580 on `isolation/canonical-scoped-commerce-internal-20261010`; inspected source head `f8a2aa5933ae97c63b7febdaaedbdd59864f0efa` is based on the current `Legend/fast-track-release` head `640a2fa3e44fe1b36ff9c12e44aa1bbd1a7f21f6`. Do not restart the consolidation.
- `Legend/fast-track-release` is the exclusive integration, final validation-admission, deployment and rollback authority for this assignment. `legend/approved-changes` is excluded as a base, baseline, release source and rollback authority. Do not invoke or repair its lifecycle.
- Existing run `38097642242` recorded 153 tests passed, zero failed/skipped, three builds and matching assets. These receipts apply to that validated source, not subsequent edits. The current analytics-evidence/team-presentation changes pass all 11 focused `CommerceCoreOwnershipContractTests` locally with .NET SDK 10.0.401, plus JavaScript syntax and actual-renderer checks distinguishing unavailable, zero and positive values. Exact-head fast-track CI admission remains required.
- Read-only inventory run `38097602188` failed Azure identity admission with `AADSTS700213` for the isolation-branch subject. It did not establish live production inventory or migration readiness.
- Governed repository inspection classifies `legend-fast-track-ui.yml` and the read-only inventory workflow as `INTEGRITY_PROTECTED`. Their bodies cannot be edited through the current source authority. Legitimate fast-track identity, security and production-mutation controls remain mandatory.
- Aggregate delivery counts do not prove same-event browser/server matching, attribution completeness, or paid-order reconciliation. The shared health snapshot marks these unavailable until identity-based evidence exists; observed counts remain counts. Owner-only team administration is not a delegable checkbox in the scoped team editor.
- The runtime still has legacy commerce fallback, and shared Razor presentation remains authored under `ParfaitApp`. Live migration inventory, client/internal-login parity, durable media transfer, payment/webhook validation, and two-tenant isolation/visual proof remain outstanding. Keep the Parfait domain and App Service functioning.
- Live browser baseline: `https://www.shopparfait.com` redirects to `https://shopparfait.com/`; structural diagnostics identify `ParfaitApp` at revision `97555039d28fdeb4b2afe5c58d395ac70531d302` with empty issues/duplicate-assets lists. Home and Team login link render. This proves the legacy home responds, not checkout or migration.
- Deploy only applications proven affected after fast-track admission; retain valid uploads/artifacts/receipts and resume only unresolved verification. Domain cutover and App Service retirement require independent completion of every acceptance item below.

## Canonical ownership rule

- One `CommerceBusiness.Id` is the permanent tenant identity for catalog, inventory, orders, discounts, Square merchant linkage, analytics attribution, website publications, team/member capabilities, automations, customer lists, media, and billing. Never create a replacement Parfait business row.
- One shared commerce service implementation lives in `CommerceCore` / `Infrastructure`. Controllers in AgentPortal, ClientApp, Protect, and any compatibility host must be adapters, not independent state or payment engines.
- One internal page and modal source serves every store-enabled scoped business, including Parfait. Parfait's original *public* site, typography, media, product visuals, layout, and navigation remain its tenant-owned version. No generic theme replaces it.
- Internal authentication must use the platform's existing verified actor identity and active `CommerceBusinessMember` permissions. It must authorize **both the actor and business** for each action, including page GET, modal reads, save/upload, order/return/refund, provider settings, team changes, and automation sends. No ability to select a target tenant through an untrusted query string, host header, or posted ID.
- Every business that enables the store in its website editor receives the same authorized management experience. The option is not globally turned on for tenants without a store.
- Keep a single immutable published website record per business/domain; do not clone the public page tree, website analytics writer, CRM, payment engine, or authorization rules.

## Current verified code boundaries

- `CommerceCore/Controllers/CommerceManagementController.cs` is a single business-ID-scoped ticket adapter that already reuses dashboard, products, orders, automation, analytics, preview, and original Razor modal sources.
- `ParfaitApp/Controllers/InternalController.cs`, `InternalModulesController.cs`, `InternalSettingsController.cs`, `InternalBusinessesController.cs`, `ParfaitTeamAccessService.cs`, and `ParfaitInternalPageRegistry.cs` remain Parfait-app-local legacy authorities. They are **not** the finished shared client internal login/authorization runtime.
- `ParfaitTeamAccessService` stores Parfait team permissions in JSON and restricts legacy sign-in to approved `@mylegnd.com` emails. That cannot be reused as the login authority for unrelated business customers.
- The shared MVC registration permits `CommerceManagementController` in both preview and cutover modes; the management-only registration excludes public storefront/cart/checkout controllers. Controller registration is not proof of routed production access or completed internal-login migration.
- `Legend-Cloudflare/src/website-routing/bridge.mjs` includes `/commerce/manage/` and `/parfait-analytics/` in selected exact-domain cutover. Legacy commerce fallback remains for non-admitted tenants and platform domains; source routing does not prove those dependencies are retired.
- `CommerceLegacyTransfer` has tested PlanOnly and CopyNoOverwrite behavior, but there is no recorded production cross-host media/JSON manifest readback or durable transfer authorization.
- Source-level Razor/CSS asset preservation is not live desktop/mobile or checkout parity.

## Implementation acceptance (all required before cutover)

1. **Tenant identity and subscription:** Read and pin Parfait's existing active `CommerceBusiness.Id`; verify membership, account/subscription, existing website publication and ownership in ClientApp and AgentPortal. Preserve historical IDs.
2. **One internal runtime:** Move legacy Parfait-only route/page registry and team-page capabilities into one shared business-scoped internal application contract. Ensure each existing internal page/modal has exactly one authored view and corresponding business-scoped action. `ParfaitApp` may only redirect/bridge to it while old sessions are active. Do not copy controllers or views into client-specific implementations.
3. **Login and authorization:** Active membership and per-operation capability grant every store-enabled business owner/team member the right pages, without requiring a LEGEND staff email. Reject inactive/revoked members, cross-tenant IDs, forged/tampered tickets, expired sessions, and unauthorized management POSTs. Confirm existing Parfait team invites and role permissions are preserved through migration.
4. **Shared state:** Put automation/team JSON and product uploads into durable tenant-scoped storage with a reconciled source-and-destination manifest; preserve customer/order history, media URLs, checkout identifiers, idempotency, attribution lineage and product/inventory records. Verify backup and no-overwrite readback.
5. **Commerce correctness:** Shared host completes cart, quote, checkout, Square capture/webhook, order settlement, receipt, fulfillment, refund, and abandonment automation with correct tenant/membership scope. Run sandbox or read-only tests before any specifically approved real payment.
6. **Website and visual parity:** Confirm Parfait's live public pages, store/product detail, cart, checkout, favicon, SEO, form controls, responsive desktop/mobile layout and business-owned theme match exactly. Independently confirm a second non-Parfait business can enable a store, enter the *same* internal management runtime and manage only its own objects.
7. **Cross-host routing:** Verify both `shopparfait.com` and `www.shopparfait.com` bind to the original Parfait business, Cloudflare certificate is active, and all commerce *and private management/analytics/automation* routes have the replacement runtime. Prove the new host before enabling Cloudflare's exact-domain cutover. Other businesses' hosts and platform origins remain unchanged.
8. **Retirement proof:** Prove no authoritative data, session callback, webhook, redirect, management endpoint, automation dispatcher, secrets/key ring, or image file is still dependent on `masterapp-parfait`. Preserve rollback/backups, then stop the legacy service; delete only after the agreed observation and recovery evidence.

## Explicit non-equivalences

- A successful ZIP upload or revision check does not prove live store checkout.
- Source hash parity does not prove live visual parity.
- Existing `CommerceBusiness` row does not prove client membership activation.
- Published code with cutover OFF does not switch domains, files, webhooks, or auth.
- A sitemap timeout does not warrant repeating an already verified Azure upload.
- Business-scoped website-editor tickets are not replacements for per-capability business internal login and team authorization.

## Release scope and protection

Work in an isolated branch; validate and merge through the pre-existing fast-track admission **only after** the above implementation is complete. CI must build and test impacted projects, prove no EF migration replay, preserve live Parfait/Protect revisions until approved cutover, and select only affected apps/Worker. Stop on identity, data, payment, tenant isolation, origin, or visual-parity mismatch. Do not disable any existing gate merely to advance release.
