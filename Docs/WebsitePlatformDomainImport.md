# Business website domains and imports

This extends the shared website authority. The permanent owner is CommerceBusiness.Id. A verified domain is a removable address, never an ownership credential. Protect agent websites remain in their existing LEGEND agent scope.

## Single provider

Customer domains use Cloudflare for SaaS custom hostnames. Existing Cloudflare infrastructure is present in the repository; no existing Front Door/custom-hostname authority was found. This implementation does not provision a competing Azure domain system. Cloudflare account entitlement, production routing, billing, and credentials must be checked before enabling this feature; source configuration alone is not activation proof.

Server configuration:

- `WebsiteDomains:CloudflareZoneId`: SaaS zone identifier.
- `WebsiteDomains:ApiToken`: secret with custom-hostname read/write access to that zone. Never expose it to clients or logs.
- `WebsiteDomains:CnameTarget`: actual SaaS CNAME target configured with an origin serving the shared business renderer.

The SaaS zone must support custom hostname metadata. Each provider record is bound to the exact local domain binding ID and business ID. Existing unrelated provider hostnames are rejected, not adopted. A hostname is active only when the provider confirms hostname ownership and an active certificate, and an HTTPS request to `/.well-known/legend-website` returns the exact binding and business IDs. The probe uses the existing public-network socket policy, disallows redirects, and uses normal TLS certificate verification.

The management response exposes provider-issued TXT ownership/certificate records and the configured CNAME target. It never alters a customer's registrar or email DNS. Apex domains require registrar-supported alias/flattening or provider apex support; do not promise unsupported apex setup. Apex and www are separate verified bindings. Provider errors retain pending state. Missing configuration fails explicitly.

A recurring worker checks bounded batches every ten minutes. Routing rejects evidence older than 24 hours. Removal disables local routing before provider deletion; a failed deletion remains retryable.

References: https://developers.cloudflare.com/cloudflare-for-platforms/cloudflare-for-saas/domain-support/hostname-validation/ and https://developers.cloudflare.com/api/resources/custom_hostnames/methods/create/

## Imports

Imports require authenticated website management permission and explicit owner authorization. They produce a draft and provenance report; the caller applies the existing optimistic revision check. They never publish.

The public importer uses the existing `LegendConnectResearchNetworkPolicy` socket guard, including DNS-time private-address rejection. It reads public HTML only with no cookies, credentials, script execution, or form submission. Limits are 20 pages, two MB per page, five MB per image, and a 90-second aggregate deadline. Source links are restricted to the same origin for crawling. Redirect responses require the user to submit the final public address. Failed pages and assets appear in the report.

Original route paths become page keys. Original source URLs, content hashes, timestamps, source text, links, image URLs, forms, and missing integrations are recorded. Source link labels are retained. Stable import IDs preserve already edited components on repeat imports. The importer does not infer prices, credentials, services, or testimonials. Scripts and backend functionality are not migrated.

Uploaded MP4/WebM videos use the same storage authority with byte-signature validation and a 25 MB per-file limit; images have a five MB limit. Scoped usage returns actual stored bytes and asset counts.

Images are copied through the existing shared media storage transport and upload-byte validator, with separate website ownership metadata. Content hashes deduplicate within an owner. Draft media delivery requires an owner ticket; public media requires published-document reference validation. No external source image is silently substituted on storage failure.

Portable export produces a ZIP containing `document.json` and owned root/page image/video assets, up to 20 MB of media. Portable import accepts a LEGEND document JSON (including the older draft wrapper) or a ZIP containing `document.json` and referenced `media/` assets. Archive references are remapped to the destination owner; another website’s private media references cannot be reused through a plain JSON import. Archives are read in memory, never extracted. Absolute paths, traversal, duplicate names, excessive entry count, and expanded size are rejected. Existing destination pages/elements survive import. A platform-specific export must first be mapped into this documented shared document contract; arbitrary proprietary export formats are not falsely reported as supported.

## Release verification

Before release, verify database mapping/migration, domain uniqueness and concurrency, revoked membership rejection, draft/public isolation, media authorization, hostile archive/URL rejection, imported route rendering, real provider verification failures, successful HTTPS routing, and export/reimport against the exact staged SHA. No live domain activation or deployment is authorized by this document.
