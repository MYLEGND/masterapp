# Parfait → LEGEND fast-track release handoff (NOT authorized)

Verified 2026-10-10 from current GitHub branches and workflow definitions.

## Approval invariant

User intends the completed Parfait consolidation to deploy through the existing Legend/fast-track-release branch. This is an eventual delivery requirement, not permission to merge, deploy, write production data, change DNS or retire an app. Keep work on isolation/parfait-canonical-commerce-20261010 until explicit Founder authorization.

## Exact branch topology at inspection

- Protected baseline legend/approved-changes: 5fcc93fb4279ec7c875f9c3fb27bc3a31a1cca96
- Fast-track Legend/fast-track-release: b685ec4203d3c96952db50f3fdf37e14bcefece1
- Fast-track is 38 commits ahead of protected head, with 73 changed paths spanning AgentPortal, ClientApp, Protect, Infrastructure, SHARED, Website, design and workflow files; only one changed ParfaitApp file.
- The Parfait isolation branch has a distinct lineage from the same protected baseline. Never assume conflict-free transfer. Re-read all exact heads and impact lists immediately before any handoff.
- Do not unintentionally include the preexisting fast-track changes in Parfait's app publication or merge them into the protected branch as part of this task.

## Verified fast-track runner limitations

Source: .github/workflows/legend-fast-track-ui.yml on Legend/fast-track-release.

- Runs automatically on PUSH to Legend/fast-track-release when listed paths change.
- Current path filter includes ParfaitApp/Views/Shared/_InternalLayout.cshtml, but excludes ParfaitApp/Services, ParfaitApp/Controllers, ParfaitApp/Views/Store and Infrastructure/Commerce.
- Its select-targets impact calculation fails closed on unknown source paths; Infrastructure/Commerce is not mapped.
- Upload uses one Azure ZIP per selected app with --clean true --restart true.
- Workflow identifies itself as UI/source-only: no migration admission, database migration, rollback rebuild, or full test suite.
- This is NOT suitable for a shared commerce/backend/payment/data-cutover as configured. Do not bypass protected canonical security and migration admission.

## Fast-track-compatible sequence

1. Finish isolated implementation before transfer. Lift existing Parfait product/order/checkout/automation execution into one shared CommerceBusinessId-scoped Infrastructure/Commerce owner, reusing SQL commerce tables, the existing Square billing orchestrator, marketing analytics and existing server routes via adapters. Do not create competing state stores or duplicated payment services.
2. Verify Parfait's original design and business state. Capture browser desktop/mobile screenshots and DOM/CSS parity for the real Parfait site; verify product IDs, media, inventory, orders, JSON automation/team records, legal pages, SEO and URLs. Existing Git blob baselines are source-only, not pixel-level assurance.
3. Develop the commerce-aware FAST-TRACK ADMISSION in a separate reviewable change without touching the live fast-track branch prematurely. Derive selected artifacts from dependencies and changed paths, including ParfaitApp/Services, Controllers and Store views; Infrastructure/Commerce, Billing and WebsiteRuntime; relevant shared website/editor components and routing. Unknown paths must fail closed. Do not create an alternative release authority.
4. The backend lane must demand explicit user-approved release intent, exact candidate and artifact identities, focused .NET 10 checkout/order/automation/tenant isolation tests, payment idempotency and webhook checks, shared migration schema-admission proof, rollback readiness and live readback. The UI-only lane can remain for UI-only diffs; it must not be used for this backend migration unchanged.
5. Before copying source to Legend/fast-track-release, remove accidental auto-publish risk: its current push-trigger is consequential. Confirm proposed workflow gates are safe without exposing unvalidated backend changes to automatic production. Separate controlled release admission from mere branch synchronization.
6. Select only actually impacted apps. Tentative candidates are Parfait (during migration) and Protect (website runtime/compiler), AgentPortal and ClientApp only if changed shared management/auth UI/contracts require them, static Website only if compiler/build/assets changed, and Cloudflare worker only for routing cutover. Never hardcode a five-app redeploy.
7. Perform read-only rehearsal and build/test proof first. Do not execute live database changes, move/delete files, replace production domains, process real payments, or submit a deploy ZIP for a rehearsal. Real-charge and data-cutover operations need their own explicit authority.
8. AFTER EXPLICIT USER APPROVAL, transfer the reviewed exact candidate through a secured fast-track lane, deploy in resumable dependency order with precise runtime readback; preserve the live Parfait host and rollback until all other tenant storefronts are healthy. Production migration and eventual Parfait host retirement require separate authorization and verification.

## Stop conditions

- No merge, production deploy, fast-track push, release workflow dispatch, DNS change or data mutation without explicit approval.
- No weakening protected release control or historical migration evidence.
- No unverified checkout charges or automatic schema replay.
- No replacement of original Parfait visuals with generic scoped LEGEND styling.
- No application publication merely because it is included in the monorepo.

Status at writing: Release route preparation only; current Parfait isolated code is NOT feature-complete and its new .NET tests have not run. This document is guidance and not an executable authority.
