# Runtime assets and publication migration gate

Source inspection, 2026-09-27. No production database export, asset deployment, cache purge, or publication occurred.

## Actual loading contracts

| Host | Entry and assets | Update authority |
|---|---|---|
| Protect | `_Layout.cshtml` loads one `/js/tracking.js`, optional `/js/openai-measurement.js`; quote partial or Life view loads `/js/meta-signal-intelligence.js`; shared CMS is deferred | ProtectWebsite.csproj links the SHARED files into output; deploy the complete host output |
| Static LEGEND | build.mjs copies shared tracker, Meta, OpenAI, CMS and web scripts into `dist`; generated HTML carries content-hash query versions | Run `npm --prefix Legend-Website run build`, then deploy complete dist with generated HTML |
| Published Business | immutable CompiledPagesJson contains HTML and runtime asset references; middleware serves script bytes from the **current** `WebsiteCompiler/dist` | Deploy current compiler dist alongside host; existing publication references normally receive current script bytes |

Business publications do not generally contain self-hosted copies of old tracker code. `BusinessWebsiteMiddleware` resolves seven fixed public asset paths to current compiler dist independently of the publication HTML. The business renderer uses runtime-config paths `/legend-public-tracking.js`, `/legend-public-meta-signal-intelligence.js`, and `/legend-public-openai-measurement.js`. Thus blanket republishing is unnecessary for normal snapshots. Old/custom HTML that pins alternate assets or embeds retired endpoint code must be inventoried and selectively recompiled.

## Required predeployment evidence

1. Run `npm --prefix Legend-Website run build` and `npm --prefix Legend-Website run test:renderer`. The renderer suite asserts runtime paths and no inline executable script in business publications. Run `node --test tests/website/tracking-startup.test.mjs tests/website/legend-public-cms.test.mjs` for startup/order/event identity.
2. Compare output asset SHA256 with the corresponding SHARED source files: tracker, Meta, OpenAI and CMS. Verify Protect published wwwroot and deployed compiler dist independently. Merely rebuilding .NET does not prove static LEGEND assets were regenerated/deployed.
3. Search deployment artifacts and read-only exports of active CompiledPagesJson for `analytics/meta-signal`, `analytics/business-page`, external alternate tracker URLs, inline old runtime bodies and repeated tracker/provider script tags. Parse each HTML page; inspect embedded application/json runtime references rather than treating every repeated asset string as another executable include.
4. For ordinary snapshots with the fixed current asset paths, deploy updated assets and invalidate stale CDN/browser caches. For exceptional snapshots, recompile into a new reviewed version through the existing publication authority with current DocumentJson/business facts/collections. Do not mutate immutable versions in place. Rollback can restore old compiled HTML, so inventory versions eligible for rollback too.
5. On each rendered page, verify one general tracker execution, optional providers after initialization, one page source ID, and only `/api/tracking/ingest` for browser analytics. Check a managed CTA, form start and unique engagement signal, including analytics-only bindings. Provider projections must reuse source IDs. No compatibility endpoint should be reopened.

Protect quote templates must include either the Life-specific Meta bootstrap or shared quote bootstrap once. Browser script globals do not substitute for checking rendered script counts. Existing source references alone cannot prove production output, publication compatibility, or provider receipt.

## Parfait consolidation follow-up

The Parfait telemetry controller and service are retired. The storefront loads SHARED tracking.js and posts browser observations to the same Protect `/api/tracking/ingest`; ProductViewed retains product metadata. Protect CORS includes the two verified Parfait public origins. `CommerceStoreContextService` and `ParfaitBusinessScopeService` now physically belong to Infrastructure/Commerce while retaining their existing namespace for source compatibility; neither references a host assembly. Protect and Parfait use that same owner resolver. Scoped public keys are accepted only on the Protect shared host; custom-domain ownership remains verified-domain based.

`POST /store/cart/items` (and scoped store path) is a domain cart command, not analytics ingest. It validates active product/size/stock, mutates accepted session cart state, then emits one canonical CommerceSignalService AddToCart using the command identity. Cart receipts preserve the original accepted increment, timestamp and price; repeated IDs with conflicting product/size/quantity are rejected. Zero increments emit no conversion. Browser local cart updates only after acceptance. The browser no longer emits an AddToCart telemetry copy.

The current ASP.NET session store and striped in-process lock do not establish distributed transactional exactly-once cart mutation across multiple app instances. Canonical stable-ID persistence protects analytics duplication. Multi-instance cart command concurrency remains a deployment validation item; use the application's session affinity/store guarantees or a shared transactional command store before claiming distributed exactly-once semantics. No new datastore was introduced.

First-touch campaign fields are bounded in the `pf_attribution` URL-encoded JSON cookie, retained across storefront navigation, and adapted through CommerceSignalAttribution for cart/checkout outcomes. Product views remain observations; confirmed conversion outcomes remain server-authoritative.
