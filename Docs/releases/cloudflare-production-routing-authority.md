# LEGEND Cloudflare Production Routing Authority

This document records the single Cloudflare control-plane authority used for public business custom domains and storefront routing.

## Ownership

- MasterApp `WebsiteDomainService` remains the sole tenant/domain binding and certificate authority.
- `scripts/cloudflare-routing-authority.py` is the sole repository-owned Cloudflare security/routing control-plane authority.
- `CLOUDFLARE_WORKERS_API_TOKEN` is the sole GitHub Actions credential for this authority.
- No legacy Cloudflare credential fallback, per-business routing implementation, or hard-coded customer hostname is permitted.
- The Cloudflare Worker `legend-business-website-router` is the shared transport router.
- Protect is the website origin and Parfait is the commerce origin; the original verified customer hostname remains authoritative for tenant resolution.

## Required Cloudflare capability contract

The production token is expected to provide the narrowly scoped capabilities needed for:

- Zone WAF edit
- Bot Management edit
- Zone Settings edit
- Config Rules edit
- Origin Rules edit
- Analytics read
- Zone read
- Workers Routes edit
- SSL and Certificates edit
- DNS read
- Cache Purge
- Workers Scripts edit
- Workers Tail read
- Account Settings read

The routing release audits these capabilities before Cloudflare mutation.

## Security policy

Cloudflare Bot Fight Mode is intentionally disabled for the SaaS zone because it applies to the entire zone and cannot be bypassed for verified customer custom hostnames. Controllable WAF/configuration security remains authoritative.

Browser Integrity Check remains zone-managed for LEGEND-owned hosts and is disabled through one idempotent configuration rule for customer vanity hostnames.

## Release invariants

A routing recovery must:

1. Preserve live application revisions unless an application release is explicitly authorized.
2. Audit the centralized Cloudflare authority before mutation.
3. Reconcile the generic customer-host policy exactly once.
4. Deploy the shared Worker with rollback preservation.
5. Prove the verified business binding endpoint.
6. Prove the custom-domain homepage returns HTTP 200.
7. Prove the custom-domain `/store` returns HTTP 200.
8. Verify selected live application provenance is unchanged in preserve-live mode.
9. Fail closed on any missing authority, mismatched tenant binding, challenge response, or provenance drift.

Customer domains such as live canaries are evidence only and must never be encoded as application routing logic.
