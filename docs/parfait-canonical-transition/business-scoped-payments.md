# Business-scoped merchant payments — isolated candidate

Implementation branch: `isolation/business-scoped-payments-20261011`.
Baseline: PR #580 commit `d4f9bcc424f2ccb07d5370eeb72d80d979062b14`.
This is the payment portion of the existing canonical-commerce consolidation. It does not replace the other squad's checkout or modify its branch. Final integration/release authority is `Legend/fast-track-release`; merge and deployment remain on Founder hold. The protected branch is not a release destination.

## Canonical ownership

`CommerceBusinessId` remains the business identity. `CommerceOrder` remains the order authority. `CommerceSignalService` remains the purchase/attribution writer. Platform `IBillingOrchestrator`, subscriptions, entitlements, and payment methods are unchanged.

`MerchantConnectionService` resolves existing business-owner permissions, consumes a one-use protected OAuth state, verifies the processor account, and requires explicit owner confirmation. One configuration row per business/environment selects the default. Connection revisions retain merchant identity, location, credential protection, actor/action, scopes, readiness, and timestamps. Credentials use the existing ASP.NET Data Protection service with business/connection/provider/environment-specific purposes. Production hosts must share the approved durable key-ring configuration; ephemeral keys are unsuitable.

`MerchantCheckoutService` pins order, merchant, provider, environment, location, amount, currency, and connection revision before contacting a processor. Database uniqueness and concurrency tokens arbitrate duplicate submissions. A timeout remains Unknown. Reconciliation reads provider evidence; it does not blindly repeat a charge, capture, or refund. A default switch affects future attempts only. Refresh may use a newer token only for exactly the same pinned connection and merchant; it never substitutes the new default.

`MerchantPaymentCompletionService` commits the existing order and inventory policy in one SQL transaction. Durable effects invoke the existing purchase signal, automation, and merchant-specific email services. Capture, settlement readiness, and payout are distinct. No fee or settlement amounts are estimated. Verified refunds update the original order's refunded amount; they do not create another Purchase.

Email dispatch is claimed before leaving SQL. An ambiguous external send remains Sending/Unknown and is not automatically replayed. Provider/mail evidence is required before an operator resolves it; this avoids duplicate email at the expense of requiring recovery for an ambiguous send.

## Owner flow

ClientApp → Business → Website Studio → Store → Payments opens the existing shared management frame. The same Payments view appears in its internal settings navigation. All changes use the existing owner capability and anti-forgery checks. The UI supports live/sandbox status, hosted authorization, resuming onboarding, merchant/location confirmation, readiness refresh, default selection, processor dashboard links, safe disconnect, and refunds of the most recent captured orders. No provider tokens or bank credentials are sent to the browser.

Local disconnect disables new routing and retains original merchant access for outstanding refunds/reconciliation. It does not claim to revoke processor authorization. Remote revocation is available through the merchant's processor dashboard; Square additionally implements the adapter revoke operation.

## Configuration (all disabled until explicitly configured)

Common server-owned settings:

- `MerchantPayments:CheckoutEnvironment`: `Production` by default, or explicitly `Sandbox` for a test host. Browsers cannot choose checkout environment.
- `MerchantPayments:ParfaitCutoverEnabled`: false by default. Existing Parfait Square checkout remains intact until a verified migration/cutover is approved.
- `MerchantPayments:ReconciliationEnabled`: false by default. Enable on the canonical commerce worker host after schema/provider validation.
- `MerchantPayments:{Square|Stripe|PayPal}:{Sandbox|Production}:Enabled`.
- `MerchantPayments:{provider}:{environment}:CallbackUrl`: registered HTTPS URL ending in `/commerce/manage/payments/callback`, without query/fragment.
- `MerchantPayments:{provider}:{environment}:WebhookUrl`: exact externally registered HTTPS URL `/commerce/payments/webhooks/{provider}/{environment}`. Signature verification never derives this from an arbitrary Host header.

Square: `ApplicationId`, `ApplicationSecret`, `WebhookSignatureKey`. Seller tokens are OAuth-derived, encrypted, and separated from these platform application credentials. OAuth scopes include merchant/location, order, and payment operations. New scoped checkout uses seller-owned hosted checkout rather than platform Web Payments SDK credentials.

Stripe: `SecretKey`, `WebhookSecret`. The implemented model is Standard connected accounts with direct charges using `Stripe-Account`. No transfers, application fees, custody, or fund splitting are introduced. Actual account eligibility and agreements require provider confirmation.

PayPal: `ClientId`, `ClientSecret`, `PartnerMerchantId`, `PartnerAttributionId`, `WebhookId`, and `PartnerApproved=true` for production. Configuration is not proof of approval. The provider must actually approve the partner integration. Sandbox and production remain separate. No raw merchant-key workaround exists.

Secrets belong in the approved secret configuration system, not repository JSON or website forms. Provider-hosted collection avoids application handling of card numbers/CVV; applicable compliance scope still requires the business/provider review.

The existing storefront prices in USD. Adapters expose their supported/readiness currencies; that does not introduce a new cart currency authority or guarantee a merchant can accept every currency. A provider-pending payment produces no purchase revenue.

## Additive migration and Parfait cutover

`20261011064911_AddBusinessScopedMerchantPayments` adds eight merchant/payment tables and three neutral order columns. Legacy Square fields remain. No transaction identifier, merchant account, or token is fabricated/backfilled. A preflight stops on duplicate existing business/checkout-attempt identities; it never deletes or rewrites them. Apply only through the authorized fast-track migration gate after production inventory review.

Before enabling Parfait cutover, verify its actual business/account/location, connect through supported authorization, preserve old transaction references/callback routes, validate shared-host sandbox checkout and historical refunds, and prove rollback. Legacy Parfait transactions remain Square transactions. This branch does not disconnect, migrate, or overwrite the current live merchant.

## Evidence boundaries and recovery

Local mocked/relational tests prove code behavior, not provider approval or live production readiness. Production configuration, provider sandbox end-to-end behavior, tenant ownership, key-ring persistence, deployed callback/webhook reachability, and production schema inventory must be verified before enabling live routing.

Unknown checkout creation can be recovered by bounded read-only Square/Stripe discovery or a verified webhook carrying the original attempt identity (including PayPal). If the provider cannot supply conclusive evidence, the attempt stays Unknown; no automatic fresh charge is issued. Unknown refunds without a provider reference similarly retain their reservation until processor evidence resolves the result. The system must not turn absence of evidence into a retry authorization.

Webhook receipts retry up to ten times, then retain a DeadLetter state. The canonical worker also checks captured payments periodically for missed refund/dispute updates. Optional dispute capabilities remain accurately disabled where a complete dispute API is not implemented; Stripe's verified disputed flag is recorded when available.

## Validation commands

Use isolated build outputs. In constrained environments, `DOTNET_PROCESSOR_COUNT=2`, `DOTNET_GCHeapHardLimit=0xC0000000`, `-m:1`, and `-p:UseSharedCompilation=false` bound compiler memory without excluding source.

- `dotnet run --project diagnostics/MerchantPaymentAdapterChecks/MerchantPaymentAdapterChecks.csproj`
- `dotnet test diagnostics/MerchantPaymentCoreTests/MerchantPaymentCoreTests.csproj`
- Build `CommerceCore/LegendCommerce.csproj` and the shared Razor hosts.
- Generate the additive migration SQL and verify EF reports no pending model changes. Do not apply it to production as part of local validation.

## Candidate validation — 2026-10-11

- 83 focused core/regression tests passed, zero failed or skipped. Includes SQLite transaction/concurrency cases, tenant routing, owner authorization, replay rejection, provider switching, ambiguous financial operations, verified completion, refund reservations, and legacy billing coverage.
- 37 synthetic provider-adapter checks passed, including signature verification, authoritative refund evidence, original hosted-link recovery, and production webhook configuration requirements.
- EF reported no pending model changes. The generated additive SQL is retained alongside this document; no database was migrated.
- ProtectWebsite and ParfaitApp final builds passed with zero warnings and zero errors (including CommerceCore and Infrastructure dependencies). Local builds disabled analyzers to bound resources; required repository CI/security gates remain required before release.
- Shared CMS JavaScript syntax and Git whitespace checks passed.
- Independent source/security review completed; external production validation remains pending.

These are focused local checks, not a claim that all repository CI gates or actual provider end-to-end tests have passed. Release remains blocked pending the Founder's explicit green light and the provider/configuration/runtime checks above.
