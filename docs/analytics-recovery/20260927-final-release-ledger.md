# PR 249 final validation ledger

Authorization: the attached 2026-09-27 command permits merge into legend/approved-changes and affected-only deployment after required gates pass. Earlier draft-hold statements in historical reports are superseded by this authorization. Gates are not waived.

Base: df4bae6f9a175ecc04992235cfaf834a0095a7bf. Preserved checkpoint: 4e43225fe5373d078f1134854e973c340f456ac6.

## Canonical closure

| Responsibility | Active authority | Classification |
|---|---|---|
| Public ingest | Protect TrackingProxyController → WebsiteTrackingProxyAuthority / WebsiteAnalyticsIngestAuthority | CANONICAL endpoint and shared authority |
| Analytics persistence | UnifiedAnalyticsWriter | CANONICAL; only AnalyticsEvents.Add production site |
| Meta persistence | UnifiedMetaSignalWriter | CANONICAL; only MetaSignalEvents.Add production site |
| Analytics to Meta | MetaSignalAnalyticsBridge + canonical catalog/projection | CANONICAL |
| Meta dispatch | MetaSignalOutcomeDispatcherHostedService → MetaConversionsApiService | CANONICAL; one hosted registration in Protect |
| OpenAI dispatch | AnalyticsEvents → CanonicalAdvertisingEventProjection → OpenAiMeasurementEventMapper → OpenAiConversionDispatcherHostedService → MarketingDestinationDelivery | CANONICAL; one hosted registration in Protect |
| Old OpenAI receipts | FindHistoricalReceiptAsync | THIN ADAPTER; read-only resend fence, no independent event writer |
| Behavior / immutable action identity | AnalyticsEventCatalog + WebsiteEditorContracts / WebsiteSignalBinding | CANONICAL; editable labels do not determine identity |
| Event Map | WebsiteEventMapQuery | CANONICAL; editor/business/dashboard delegate |
| Advertising ownership | CanonicalAdvertisingEventProjection | CANONICAL; source adapters resolve permanent profile/business identity, Global is not credential owner |
| Public provider configuration | MarketingBrowserConfigurationService | CANONICAL read-only projection |
| Attribution | tracking.js owner/session contract → CommerceSignalAttribution / TrafficAttribution | CANONICAL; adapter rejects legacy unscoped cookies |
| CRM / commerce | canonical first-party writers and confirmed-outcome projection | CANONICAL; provider failures cannot gate first-party persistence |
| Marketing health | MarketingMeasurementEvidenceService | CANONICAL read-only source/destination-linked evidence |
| Channel outcomes / economics | UnifiedMarketingPerformanceService / BlendedGrowthEconomicsService → CanonicalMarketingOutcomeProjection | CANONICAL; OpenAI outcomes do not require Meta rows |

Linked storefront CMS reuses commerce runtime and retains action binding setup. Regression proves no second tracker or provider initialization and one page observation. Receipt health requires current OpenAI account/pixel/data-source and Meta pixel. Legacy unknown receipts cannot supply acceptance proof. HTTP/timeout provider failures return explicit data-quality notes without suppressing canonical outcomes; caller cancellation still propagates.

## Affected targets and migration

All five existing web targets are affected: AgentPortal and ClientApp consume shared analytics/UI/controllers; Protect owns public ingestion and provider dispatch; Parfait consumes commerce and provider setup; LEGEND static source/build/runtime changed. The release request selects exactly these five existing targets, with Cloudflare routing changes disabled. No mobile deployment is requested.

Migration: 20260927190000_CanonicalOpenAiAnalyticsDelivery adds AnalyticsEventId, MetaSignalEventId, AdvertiserAccountId, ConversionDataSourceId and source index to existing MarketingDestinationDeliveries. It creates no competing table. Production application is NOT PROVEN until deployment evidence is available.

## Preserved and current evidence

- Historical exact 4e43225f: four CI workflows passed; 3,793 passed / 36 baseline failures / 5 skipped; no introduced identities.
- Earlier local 29/29 scoped and 160/160 JavaScript are retained as historical evidence.
- Current browser suite: 162/162 passed. Current renderer: 9/9 passed. Release-policy tests: 7/7 passed.
- Release build, focused/full .NET, final exact-SHA CI, merge, deployment, publication inventory and live reconciliation: pending.
- Initial parallel local MSBuild failed during project coordination with no compiler diagnostic. Serial build progressed and exposed one stale evidence-service test constructor; fixture corrected to use the existing scoped connection services. No test assertion was weakened.

## Working tree before final validation

Generated exclusions: scripts/__pycache__/ and tests/website/node_modules/. Neither belongs in the candidate. Historical docs remain as dated evidence, not current readiness claims.

```text
 M AgentPortal.Tests/AdvertisingCommandCenterCentralizationTests.cs
 M AgentPortal.Tests/AnalyticsCanonicalArchitectureTests.cs
 M AgentPortal.Tests/AnalyticsEventCatalogTests.cs
 M AgentPortal.Tests/AnalyticsQueryServiceQuoteFunnelTests.cs
 M AgentPortal.Tests/BusinessAnalyticsCompletionTests.cs
 M AgentPortal.Tests/BusinessAnalyticsDetailTests.cs
 M AgentPortal.Tests/BusinessMetaAdsScopeTests.cs
 M AgentPortal.Tests/CanonicalOpenAiProjectionTests.cs
 M AgentPortal.Tests/CanonicalOutcomeBridgeTests.cs
 M AgentPortal.Tests/GrowthEconomicsOpenAiFeedOnboardingTests.cs
 M AgentPortal.Tests/MarketingSetupCentralizationTests.cs
 M AgentPortal.Tests/MetaSignalOutcomeDispatcherHostedServiceTests.cs
 M AgentPortal.Tests/OpenAiClickReferenceLineageTests.cs
 M AgentPortal.Tests/TrafficAttributionTests.cs
 M AgentPortal.Tests/WebsiteAnalyticsInitialQualityModeTests.cs
 M AgentPortal.Tests/WebsiteContentEditorRoundTripTests.cs
 M AgentPortal.Tests/WebsitePublishingAuthorityTests.cs
 M AgentPortal.Tests/WebsiteSignalBindingTests.cs
 M AgentPortal/Controllers/WebsiteAnalyticsController.cs
 M AgentPortal/Services/Analytics/MetaAdsConnectionStore.cs
 M AgentPortal/Views/WebsiteAnalytics/Index.cshtml
 M AgentPortal/wwwroot/js/website-analytics.js
 M Docs/releases/direct-release-request.json
 M Infrastructure/Analytics/AnalyticsQueryService.cs
 M Infrastructure/Analytics/BlendedGrowthEconomicsService.cs
 M Infrastructure/Analytics/CanonicalAdvertisingEventProjection.cs
 M Infrastructure/Analytics/CanonicalMarketingOutcomeProjection.cs
 M Infrastructure/Analytics/CanonicalMetaAdsConnectionStore.cs
 M Infrastructure/Analytics/IMetaAdsConnectionStore.cs
 M Infrastructure/Analytics/MarketingBrowserConfigurationService.cs
 M Infrastructure/Analytics/MarketingConnectionStore.cs
 M Infrastructure/Analytics/MetaAdsService.cs
 M Infrastructure/Analytics/MetaSignalAnalyticsBridgeMetadata.cs
 M Infrastructure/Analytics/MetaSignalOutcomeDispatcherHostedService.cs
 M Infrastructure/Analytics/OpenAiAdsOnboardingService.cs
 M Infrastructure/Analytics/OpenAiMeasurementDelivery.cs
 M Infrastructure/Analytics/UnifiedAnalyticsWriter.cs
 M Infrastructure/Analytics/UnifiedEventContext.cs
 M Infrastructure/Analytics/UnifiedEventMapper.cs
 M Infrastructure/Analytics/UnifiedMarketingPerformanceService.cs
 M Infrastructure/Analytics/WebsiteTrackingProxyAuthority.cs
 M Infrastructure/Businesses/BusinessWorkspaceControllerBase.cs
 M Infrastructure/Businesses/BusinessWorkspaceService.cs
 M Infrastructure/Commerce/CommerceSignalAttribution.cs
 M Infrastructure/Commerce/CommerceStoreContextService.cs
 M Infrastructure/Leads/WebsiteInquiryAuthority.cs
 M Infrastructure/WebsiteEditing/PromotionOrchestrationService.cs
 M Infrastructure/WebsiteEditing/WebsiteContentSanitizer.cs
 M Infrastructure/WebsiteEditing/WebsiteEditorContracts.cs
 M Infrastructure/WebsiteEditing/WebsitePlatformController.cs
 M Infrastructure/WebsiteEditing/WebsiteSignalBinding.cs
 M Legend-Website/scripts/build.mjs
 M Legend-Website/src/business-content.mjs
 M ParfaitApp/Controllers/CommerceManagementController.cs
 M ParfaitApp/Controllers/InternalModulesController.cs
 M ParfaitApp/Services/ParfaitOrderService.cs
 M ParfaitApp/Views/InternalModules/Analytics.cshtml
 M ParfaitApp/Views/Shared/_ParfaitCommerceTracking.cshtml
 M Protect-Website/Services/Tracking/TrackingViewDataFilter.cs
 M Protect-Website/Views/Contact/Index.cshtml
 M Protect-Website/Views/Home/Index.cshtml
 M Protect-Website/Views/Quote/Index.cshtml
 M Protect-Website/Views/Shared/_Layout.cshtml
 M SHARED/Analytics/AnalyticsEventCatalog.cs
 M SHARED/Analytics/TrafficAttribution.cs
 M SHARED/Crm/BusinessWorkspaceModels.cs
 M SHARED/WebsitePlatform/legend-public-cms.js
 M SHARED/WebsitePlatform/meta-signal-intelligence.js
 M SHARED/WebsitePlatform/openai-measurement.js
 M SHARED/WebsitePlatform/tracking.js
 M docs/analytics-recovery/20260927-baseline-failures.md
 M docs/analytics-recovery/20260927-event-parity.md
 M tests/website/legend-public-cms.test.mjs
 M tests/website/tracking-startup.test.mjs
?? AgentPortal.Tests/CanonicalWebsiteBehaviorTests.cs
?? AgentPortal.Tests/CommerceSignalAttributionTests.cs
?? AgentPortal.Tests/ParfaitProviderSetupTests.cs
?? AgentPortal.Tests/ScopedMarketingMeasurementTests.cs
?? AgentPortal.Tests/UnifiedMarketingPerformanceIsolationTests.cs
?? AgentPortal.Tests/WebsiteEventMapTests.cs
?? AgentPortal/Views/WebsiteAnalytics/EventMap.cshtml
?? Infrastructure/Analytics/MarketingMeasurementEvidenceService.cs
?? Infrastructure/Analytics/MarketingProviderSetupProjection.cs
?? Infrastructure/WebsiteEditing/WebsiteEventMapQuery.cs
?? ParfaitApp/Views/Shared/_MarketingProviderSetup.cshtml
?? ParfaitApp/wwwroot/js/marketing-provider-setup.js
?? docs/analytics-recovery/20260927-normalized-behavior-parity.md
?? scripts/__pycache__/
?? tests/website/node_modules/
?? tests/website/template-action-contract.test.mjs
?? tests/website/tracking-attribution.test.mjs
```

## Tracked release diff against approved base

```text
M	.github/workflows/step5-isolated-conversion-mapping-validation.yml
M	AgentPortal.Tests/AdvertisingCommandCenterCentralizationTests.cs
A	AgentPortal.Tests/AnalyticsCanonicalArchitectureTests.cs
M	AgentPortal.Tests/AnalyticsEventCatalogTests.cs
M	AgentPortal.Tests/AnalyticsPageRoutingTruthTests.cs
M	AgentPortal.Tests/AnalyticsQueryServiceQuoteFunnelTests.cs
A	AgentPortal.Tests/AnalyticsTrackingProfileScopeTests.cs
M	AgentPortal.Tests/AntiforgeryPolicyTests.cs
M	AgentPortal.Tests/BusinessAnalyticsCompletionTests.cs
M	AgentPortal.Tests/BusinessAnalyticsDetailTests.cs
M	AgentPortal.Tests/BusinessMetaAdsScopeTests.cs
M	AgentPortal.Tests/BusinessMetaPixelResolutionTests.cs
M	AgentPortal.Tests/CanonicalCrmOutcomeLineageTests.cs
A	AgentPortal.Tests/CanonicalOpenAiProjectionTests.cs
A	AgentPortal.Tests/CanonicalOutcomeBridgeTests.cs
M	AgentPortal.Tests/CentralizedWebsiteCommerceTests.cs
M	AgentPortal.Tests/GrowthEconomicsOpenAiFeedOnboardingTests.cs
M	AgentPortal.Tests/LaunchAuditReliabilityTests.cs
M	AgentPortal.Tests/LegendConnectOperationalProofTests.cs
M	AgentPortal.Tests/LegendFounderAiContractTests.cs
M	AgentPortal.Tests/LegendFounderCurriculumSqlServerE2ETests.cs
A	AgentPortal.Tests/MarketingBrowserConfigurationTests.cs
M	AgentPortal.Tests/MarketingScopeParityContractTests.cs
M	AgentPortal.Tests/MarketingSetupCentralizationTests.cs
M	AgentPortal.Tests/MessagingProfileImageResolverTests.cs
M	AgentPortal.Tests/MessagingServiceTests.cs
M	AgentPortal.Tests/MetaSignalAnalyticsBridgeTests.cs
M	AgentPortal.Tests/MetaSignalOutcomeDispatcherHostedServiceTests.cs
M	AgentPortal.Tests/OpenAiClickReferenceLineageTests.cs
M	AgentPortal.Tests/OpenAiMeasurementDeliveryTests.cs
M	AgentPortal.Tests/ParfaitAnalyticsTrafficQualityTests.cs
M	AgentPortal.Tests/ProtectLeadModalInquiryTests.cs
M	AgentPortal.Tests/QuoteProductInstrumentationContractTests.cs
M	AgentPortal.Tests/ScopedParfaitCommerceAuthorityTests.cs
A	AgentPortal.Tests/StoreCartCommandTests.cs
M	AgentPortal.Tests/TrackingFailLoudContractTests.cs
M	AgentPortal.Tests/TrackingProxyContractTests.cs
M	AgentPortal.Tests/TrafficAttributionTests.cs
M	AgentPortal.Tests/UnifiedEventMapperTests.cs
M	AgentPortal.Tests/WebsiteAnalyticsDeleteLeadTests.cs
M	AgentPortal.Tests/WebsiteAnalyticsInitialQualityModeTests.cs
M	AgentPortal.Tests/WebsiteAnalyticsScopeResolverTests.cs
M	AgentPortal.Tests/WebsiteAnalyticsScopeTests.cs
M	AgentPortal.Tests/WebsiteContentEditorRoundTripTests.cs
M	AgentPortal.Tests/WebsiteInquiryIsolationTests.cs
M	AgentPortal.Tests/WebsitePublishingAuthorityTests.cs
M	AgentPortal.Tests/WebsiteSignalBindingTests.cs
R064	AgentPortal.Tests/AnalyticsIngestControllerTests.cs	AgentPortal.Tests/WebsiteTrackingIngestTests.cs
D	AgentPortal/Controllers/API/AnalyticsIngestController.cs
M	AgentPortal/Controllers/API/LeadSubmitController.cs
M	AgentPortal/Controllers/AvatarController.cs
M	AgentPortal/Controllers/WebsiteAnalyticsController.cs
M	AgentPortal/Health/IngestHealthCheck.cs
M	AgentPortal/Program.cs
M	AgentPortal/Services/Analytics/AnalyticsIncidentQueryService.cs
M	AgentPortal/Services/Analytics/IMetaAdsOAuthService.cs
M	AgentPortal/Services/Analytics/MetaAdsConnectionStore.cs
M	AgentPortal/Services/Analytics/MetaAdsOAuthService.cs
M	AgentPortal/Services/Analytics/WebsiteAnalyticsScopeResolver.cs
D	AgentPortal/Services/Tracking/AgentTrackingResolver.cs
M	AgentPortal/Views/WebsiteAnalytics/Index.cshtml
M	AgentPortal/wwwroot/js/website-analytics.js
M	Docs/releases/direct-release-request.json
M	Domain/Entities/MarketingDestinationDelivery.cs
M	Infrastructure/Analytics/AnalyticsQueryService.cs
A	Infrastructure/Analytics/AnalyticsTrackingProfileScope.cs
M	Infrastructure/Analytics/BlendedGrowthEconomicsService.cs
A	Infrastructure/Analytics/CanonicalAdvertisingEventProjection.cs
M	Infrastructure/Analytics/CanonicalMarketingOutcomeProjection.cs
M	Infrastructure/Analytics/CanonicalMetaAdsConnectionStore.cs
M	Infrastructure/Analytics/IMetaAdsConnectionStore.cs
A	Infrastructure/Analytics/MarketingBrowserConfigurationService.cs
M	Infrastructure/Analytics/MarketingConnectionStore.cs
M	Infrastructure/Analytics/MetaAdsService.cs
M	Infrastructure/Analytics/MetaPixelResolutionService.cs
M	Infrastructure/Analytics/MetaSignalAnalyticsBridge.cs
M	Infrastructure/Analytics/MetaSignalAnalyticsBridgeMetadata.cs
M	Infrastructure/Analytics/MetaSignalAnalyticsService.cs
M	Infrastructure/Analytics/MetaSignalCrmOutcomeService.cs
M	Infrastructure/Analytics/MetaSignalOutcomeDispatcherHostedService.cs
M	Infrastructure/Analytics/OpenAiAdsOnboardingService.cs
M	Infrastructure/Analytics/OpenAiMeasurementDelivery.cs
A	Infrastructure/Analytics/ProtectWebsiteOwnerResolver.cs
M	Infrastructure/Analytics/PublicAgentTrackingResolver.cs
M	Infrastructure/Analytics/UnifiedAnalyticsWriter.cs
M	Infrastructure/Analytics/UnifiedEventContext.cs
M	Infrastructure/Analytics/UnifiedEventContextBuilder.cs
M	Infrastructure/Analytics/UnifiedEventMapper.cs
M	Infrastructure/Analytics/UnifiedMarketingPerformanceService.cs
M	Infrastructure/Analytics/WebsiteAnalyticsIngestAuthority.cs
M	Infrastructure/Analytics/WebsiteTrackingProxyAuthority.cs
M	Infrastructure/Businesses/BusinessWorkspaceControllerBase.cs
M	Infrastructure/Businesses/BusinessWorkspaceService.cs
A	Infrastructure/Commerce/CommerceSignalAttribution.cs
M	Infrastructure/Commerce/CommerceSignalService.cs
R090	ParfaitApp/Services/CommerceStoreContextService.cs	Infrastructure/Commerce/CommerceStoreContextService.cs
R100	ParfaitApp/Services/ParfaitBusinessScopeService.cs	Infrastructure/Commerce/ParfaitBusinessScopeService.cs
M	Infrastructure/Data/MasterAppDbContext.cs
A	Infrastructure/Leads/CanonicalLeadEventIdentity.cs
M	Infrastructure/Leads/WebsiteInquiryAuthority.cs
A	Infrastructure/Migrations/20260927190000_CanonicalOpenAiAnalyticsDelivery.Designer.cs
A	Infrastructure/Migrations/20260927190000_CanonicalOpenAiAnalyticsDelivery.cs
M	Infrastructure/Migrations/MasterAppDbContextModelSnapshot.cs
M	Infrastructure/WebsiteEditing/PromotionOrchestrationService.cs
M	Infrastructure/WebsiteEditing/PublicWebsiteRuntimeScopeResolver.cs
M	Infrastructure/WebsiteEditing/WebsiteContentSanitizer.cs
M	Infrastructure/WebsiteEditing/WebsiteEditorContracts.cs
M	Infrastructure/WebsiteEditing/WebsitePlatformController.cs
M	Infrastructure/WebsiteEditing/WebsiteSignalBinding.cs
M	Infrastructure/WebsiteRuntime/BusinessWebsiteMiddleware.cs
M	Legend-Website/scripts/build.mjs
M	Legend-Website/src/business-content.mjs
M	ParfaitApp/Controllers/CommerceManagementController.cs
M	ParfaitApp/Controllers/InternalModulesController.cs
D	ParfaitApp/Controllers/ParfaitAnalyticsController.cs
A	ParfaitApp/Controllers/StoreCartController.cs
M	ParfaitApp/Controllers/StoreCheckoutController.cs
D	ParfaitApp/Models/ParfaitAnalyticsModels.cs
M	ParfaitApp/ParfaitApp.csproj
M	ParfaitApp/Program.cs
D	ParfaitApp/Services/ParfaitAnalyticsService.cs
M	ParfaitApp/Services/ParfaitOrderService.cs
M	ParfaitApp/Views/InternalModules/Analytics.cshtml
M	ParfaitApp/Views/Shared/_Layout.cshtml
M	ParfaitApp/Views/Shared/_ParfaitCommerceTracking.cshtml
M	ParfaitApp/Views/Shared/_ScopedWebsiteStoreLayout.cshtml
M	ParfaitApp/Views/Store/Product.cshtml
M	ParfaitApp/wwwroot/js/storefront.js
D	Protect-Website/Controllers/AnalyticsController.cs
M	Protect-Website/Controllers/AutoQuoteController.cs
M	Protect-Website/Controllers/CommercialQuoteController.cs
M	Protect-Website/Controllers/DentalVisionHearingQuoteController.cs
M	Protect-Website/Controllers/DisabilityQuoteController.cs
M	Protect-Website/Controllers/HomeQuoteController.cs
M	Protect-Website/Controllers/LifeQuoteController.cs
M	Protect-Website/Controllers/RiskAssessmentController.cs
M	Protect-Website/Program.cs
M	Protect-Website/Services/Tracking/TrackingViewDataFilter.cs
M	Protect-Website/Views/Contact/Index.cshtml
M	Protect-Website/Views/Home/Index.cshtml
M	Protect-Website/Views/Quote/Index.cshtml
M	Protect-Website/Views/Quote/Life.cshtml
M	Protect-Website/Views/Shared/_Layout.cshtml
M	Protect-Website/Views/Shared/_QuoteMetaSignalBootstrap.cshtml
M	SHARED/Analytics/AnalyticsEventCatalog.cs
M	SHARED/Analytics/MetaSignalAnalyticsAliasCatalog.cs
M	SHARED/Analytics/MetaSignalSingleTruthPolicy.cs
M	SHARED/Analytics/TrafficAttribution.cs
M	SHARED/Crm/BusinessWorkspaceModels.cs
M	SHARED/WebsitePlatform/legend-public-cms.js
M	SHARED/WebsitePlatform/meta-signal-intelligence.js
M	SHARED/WebsitePlatform/openai-measurement.js
M	SHARED/WebsitePlatform/tracking.js
A	docs/analytics-recovery/20260927-baseline-failures.md
A	docs/analytics-recovery/20260927-canonical-cleanup.md
A	docs/analytics-recovery/20260927-event-parity.md
A	docs/analytics-recovery/20260927-evidence.md
A	docs/analytics-recovery/20260927-repository-proof.md
A	docs/analytics-recovery/20260927-runtime-migration-gate.md
A	docs/analytics-recovery/20260927-status-1854.md
M	scripts/verify-marketing-platform-health.sh
M	tests/website/legend-public-cms.test.mjs
A	tests/website/store-cart-command.test.mjs
A	tests/website/tracking-startup.test.mjs
A	tests/website/website-analytics-runtime.test.mjs
```

## Local release gate updates

- Final production Release graph: zero errors, zero warnings (`release-final-build.log`). Subsequent fixture-only builds also zero errors/warnings.
- Focused set: 428 passed / 6 failed in `final-focused.trx`; all six selectively repaired cases passed in `focused-repaired.trx`. No production changes were needed for these six; superseded source-string expectations now assert delegation, permanent Founder fixture exists, and test DI uses the shared provider projection. Aggregate 434/434.
- EF `has-pending-model-changes` in Release/no-build mode reports no pending model changes. This is schema design parity, not proof of production migration.
- Full suite is in progress. Four further stale expectations identified so far are updated in test source; their selective verification is pending. Team/global disconnect must fail closed and leave credentials intact; normal authorized owner disconnect remains covered.
- Browser session is authenticated as Founder Personal; existing production UI still shows the prior runtime (0 page views, 1 visitor/session, unloaded Channel Outcomes). This is a predeployment observation, not a result for this candidate.

## Local gates closed; exact-SHA CI next

Full Release execution: 3,861 passed / 40 failed / 5 skipped, 3,906 total, 8m32s. Exact comparison with preserved approved-base identities found 36 baseline failures and four introduced stale fixture/source-contract expectations. Those four were corrected without production source changes and all four passed selective recheck. Remaining candidate-only identities = 0; ten baseline failures stay fixed. Effective verified outcomes: 3,865 passed / 36 known unrelated baseline failures / 5 skipped. This is explicitly a full execution plus selective test-only corrections, not a claim that the original full TRX had only 36 failures. Final committed-SHA CI must execute its full candidate comparison before ready/merge.

The 36 retained identities are 35 local LEGEND model configuration/inference failures and the existing messaging translation provider/cache failure; no remaining scope isolation, analytics, advertising, or website-runtime failure. Exact identities and evidence hashes are in 20260927-final-validation.json.

The four corrections protect: canonical permanent owner delegation, shared provider health projection, configured versus accepted evidence, and team-scope credential mutation rejection. The valid owner disconnect remains tested. Final fixture build: zero warnings/errors. Browser 162/162, renderer 9/9, release policy 7/7 and EF model parity remain valid; no production changes followed these successful checks. Generated LEGEND tracker/CMS/Meta/OpenAI bytes match SHARED sources exactly.

Merge, deployment, production migration and provider acceptance remain NOT PROVEN until subsequent exact-SHA CI and production evidence.
