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

Production deployment is isolated to the dedicated Azure Static Web App `legend-public-mylegnd` in resource group `masterapp-rg` through `.github/workflows/legend-website-production-deploy.yml`.

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
