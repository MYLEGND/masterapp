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

- [ ] Establish and implement the account-deletion process for every account
      type, including the retention policy for regulated financial and
      insurance records, post/media removal, subscription cancellation, and
      identity-provider lifecycle. The in-app initiation experience and any
      permitted verification/support flow must use this one policy.
- [ ] Replace placeholder privacy pages with the approved privacy policy,
      data-retention/deletion disclosure, and consent/withdrawal instructions.
      Add the required in-app privacy entry point and App Store Connect privacy
      metadata from the actual released data flows.
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
