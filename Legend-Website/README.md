# LEGEND® Public Website

This folder owns the public `mylegnd.com` marketing and business-model website.

## Public route contract

Preserved: `/`, `/about`, `/contact`, `/logo`, `/privacy-terms`.

Intentionally excluded from this migration: `/store`, `/team`.

`protect.mylegnd.com` remains the existing Azure-hosted LEGEND Legacy Protection application and is not implemented or modified here.

## Shared brand authority

The build reads `../Legend-Design/legend-design.tokens.json` and copies the existing canonical `LegendLogo` asset from the iOS asset catalog. Do not add a second website-specific copy of the brand tokens or logo.

## Build

```bash
npm run build
npm run check
```

The generated static site is written to `dist/`. The production workflow deploys that immutable output to the dedicated LEGEND Azure Static Web App after changes reach the protected `production` branch.
