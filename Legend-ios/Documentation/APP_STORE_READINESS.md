# App Store Readiness

## Current release status

**Not ready for TestFlight or App Store submission.**

This is the current App Store compliance record, audited on 2026-08-03. It
supersedes the earlier pre-mobile-API status in this document; the
bearer-protected mobile API, native PKCE path, and typed feature contracts are
implemented.

## Verified native boundary

- The iOS application contains no StoreKit purchase, subscription, checkout,
  payment-method, pricing, renewal, entitlement-calculation, or external
  purchase-link implementation.
- `MasterAppDbContext` and `Infrastructure/Billing` remain the one commercial
  authority. `ClientApp` is the only client subscription and payment UI.
- The client subscription portal is not linked from iOS. This avoids a global
  external-purchase call to action; it is not a region-specific workaround.
- The remaining agent-profile browser handoff is noncommercial profile
  management only. It must remain so.
- The app has neither a Reader-app entitlement nor StoreKit external-purchase
  entitlements. Do not add either unless the product and App Store Connect
  eligibility are formally reclassified and approved.

## Release blockers

- [ ] Complete the approved deletion fulfillment policy for every account type:
      regulated financial and insurance retention, post/media/blob removal,
      shared-message de-identification, provider-managed subscriptions, and
      external identity/session lifecycle. The in-app initiation and the
      immediate server access block are implemented; completion must not be
      claimed before this policy-backed worker exists.
- [ ] Approve the canonical public privacy policy, data-retention/deletion
      disclosure, consent/withdrawal instructions, and public support
      destination. The in-app privacy entry point is configured to the one
      Legend public policy URL; App Store Connect privacy metadata must match
      the final released data flows.
- [ ] Confirm distribution territories and the submitted binary contain no
      external-purchase CTA outside an expressly approved StoreKit entitlement
      region. Do not infer worldwide permission from the United States
      storefront exception.
- [ ] Supply working App Review credentials or a fully featured approved demo
      mode for every role, including any verification or one-time-code steps.
- [ ] Complete App Store Connect metadata: privacy policy URL, support URL,
      age rating, export-compliance response, organization/legal-entity
      ownership for regulated services, and accurate screenshots/description.
- [ ] Perform a clean archive and on-device validation with release signing,
      production backend access, notification permission changes, sign-in,
      role switching, profile edits, messaging, and error recovery.

## Validation rule

Do not claim App Store readiness from unit tests alone. A release requires the
live App Store Connect configuration, review access, final privacy/legal
decisions, and a signed-device validation to agree with this source boundary.
