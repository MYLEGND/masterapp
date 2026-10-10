# Isolated shared-host preview admission

The isolated consolidation can compile a **GET-only** storefront preview using the verified custom-domain binding, an existing active business record and its immutable published website version. It is **disabled** without all three settings:

- Commerce:SharedHostPreview:Enabled = true
- Commerce:SharedHostPreview:BusinessId = the exact pre-verified CommerceBusiness.Id
- Commerce:SharedHostPreview:Hostname = the verified customer-owned HTTPS hostname

The allowed routes are read-only store/catalog/cart/legal presentation and exact static assets. Checkout, payment capture, cart commands, administration, automation mutations and redirects from the production Cloudflare commerce origin remain blocked. Parfait business resolution in preview is read-only: it will not auto-seed an absent business.

This is not production readiness. The existing Parfait custom domain must have its binding and published content proved separately. Dynamic uploaded product assets, automation state, Square orchestration, SEO parity, and hosting/rollback evidence must be reconciled before a future full cutover. The original Parfait host and existing Cloudflare commerce transport remain unchanged. No fast-track push or release is authorized.
