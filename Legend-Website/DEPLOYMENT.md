# LEGEND® Public Website Deployment

This site replaces only the public `mylegnd.com` Squarespace pages that are in scope for migration.

## Included routes

- `/`
- `/about`
- `/contact`
- `/logo`
- `/privacy-terms`

## Explicitly excluded

- `/store`
- `/team`

Those routes are intentionally not generated, linked, redirected, or deployed by this project.

## Brand authority

The build consumes the repository-wide LEGEND design tokens from `Legend-Design/legend-design.tokens.json` and copies the canonical brand logo from `Legend-ios/Legend/Resources/Assets.xcassets/LegendLogo.imageset/legend-logo.png` at build time. No independent website logo or competing color-token source is maintained.

## Azure authority

Production deployment is selected explicitly as `masterapp-website` through the sole approved release authority, `.github/workflows/all-intentional-direct-release-20260918.yml`. The website package is built from the exact approved application revision and verified through `_deployment-provenance.txt`.

The workflow does not deploy, restart, alter, or reconfigure `masterapp-portal`, `masterapp-client`, `masterapp-protect`, mobile applications, databases, LEGEND AI, or unrelated Azure resources.

## DNS boundary

The code deployment does not mutate DNS. Only the apex `mylegnd.com` and `www.mylegnd.com` public-site records should be cut over to the validated Azure Static Web App after its generated hostname and domain-validation requirements are known. Existing `portal`, `client`, `protect`, Microsoft 365 mail, DKIM, DMARC, DNSSEC, and other unrelated records remain outside this workflow.

## Runtime observations

The static build copies `SHARED/wwwroot/js/page-health.js` unchanged and emits its
known public route and actual checkout SHA. It uses the same API base as the
existing public CMS integration. There is one browser observer and one shared
server incident store; the static site has no incident-read or repair endpoint.

The observer lazily obtains an antiforgery request token from
`/api/runtime-diagnostics/bootstrap` on that existing API host, with host-scoped
cookies and credentialed CORS. Protect's existing public website origin array
configures the diagnostics policy; credentials apply only to diagnostics routes.
The editor CORS policy remains unchanged. POST still validates the antiforgery
token/cookie pair. Unconfigured origins receive no token. The bootstrap returns
only a token, never diagnostic or account data; responses are not cacheable.

The frontend accepts only HTTPS transport metadata, without URL credentials,
queries or fragments. Tokens are transient and excluded from telemetry. Its
existing bounded queue/retry mechanism handles bootstrap and upload failures
without recursively collecting them. Neither browser code nor ingestion invokes
models or release actions. Preview origins need explicit server configuration
before ingestion can work; no wildcard origin or cookie-domain expansion is used.

Local verification: `node --test tests/layout/page-health.test.mjs`, followed by
`node Legend-Website/scripts/build.mjs` and `node Legend-Website/scripts/check.mjs`
from the repository root. Server integration coverage lives in
`RuntimeDiagnosticsPublicWebsiteTests`. Deployment and live browser delivery are
separate verification steps.

## Published company HTML

The existing website-routing Worker now sends company document requests on
`mylegnd.com` and `www.mylegnd.com` to Protect's
`/api/website-content/public/legend/page?path=...` authority. That action reads
exactly the public publication envelope and uses the shared website compiler to
return complete HTML. It never reads the draft. The compiler retains at most one
rendered LEGEND payload, keyed by its complete public-content hash; publication
changes cannot reuse an older result. Concurrent compilation is serialized.

The browser receives published content and metadata before JavaScript, then uses
its embedded publication for responsive behavior and tracking. There is no second
public-content fetch. Renderer/network failures return an error, never a successful
old built-in homepage. Existing publications work without republishing or a database
migration. Static assets and signed Studio entry routes retain the static website
origin; Studio still authorizes through its existing API. Business domains,
storefronts, portal and client traffic retain their current authorities.

Release the Protect compiler/API and matching website runtime assets before the
website-routing Worker through the existing approved release lifecycle. The Worker
already owns the configured zone routes; no DNS or new routing service is required.
This source change alone does not deploy any of those components. Verify the public
response with JavaScript disabled, then verify mobile navigation, a test inquiry,
editor access and runtime provenance. Direct Azure static-origin URLs remain build
and editor templates, not the public company's published HTML authority.
