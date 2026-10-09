# Canonical repository search evidence

Source-tree checkpoint after canonical closure. Historical documents, negative regression assertions, migrations, and generated deployment copies are not competing source implementations. Live deployed assets remain a separate gated audit.

```text
rg -n Route\("api/tracking/ingest" Infrastructure Protect-Website AgentPortal ParfaitApp
Infrastructure/Analytics/WebsiteTrackingProxyAuthority.cs:48:    [Route("api/tracking/ingest")]
```

```text
rg -n Route\("api/analytics/ingest"|HttpPost\("meta-signal"|HttpPost\("business-page"|Route\("parfait-analytics" Infrastructure Protect-Website AgentPortal ParfaitApp
(no matches)
```

```text
rg -n AnalyticsEvents\.(Add|AddAsync|AddRange)|MetaSignalEvents\.(Add|AddAsync|AddRange) Infrastructure Protect-Website AgentPortal ParfaitApp ClientApp SHARED --glob *.cs --glob !**/Migrations/**
Infrastructure/Analytics/UnifiedMetaSignalWriter.cs:34:        db.MetaSignalEvents.Add(row);
Infrastructure/Analytics/UnifiedAnalyticsWriter.cs:22:        db.AnalyticsEvents.Add(analyticsEvent);
```

```text
rg -n UnifiedMetaSignalWriter\.Write|capi\.SendEventAsync Infrastructure Protect-Website AgentPortal ParfaitApp --glob *.cs
Infrastructure/Analytics/MetaSignalAnalyticsBridge.cs:142:        UnifiedMetaSignalWriter.Write(db, row);
Infrastructure/Analytics/MetaSignalOutcomeDispatcherHostedService.cs:366:            var result = await capi.SendEventAsync(capiRequest, cancellationToken);
```

```text
rg -n AnalyticsTrackingProfileScope Infrastructure/Analytics/AnalyticsQueryService.cs Infrastructure/Analytics/MetaSignalAnalyticsService.cs Infrastructure/Analytics/MetaAdsService.cs
Infrastructure/Analytics/MetaAdsService.cs:128:        AnalyticsTrackingProfileScope.ResolveAsync(_db, scope, ct);
Infrastructure/Analytics/AnalyticsQueryService.cs:413:        AnalyticsTrackingProfileScope.ResolveAsync(_db, scope);
Infrastructure/Analytics/MetaSignalAnalyticsService.cs:1367:        AnalyticsTrackingProfileScope.ResolveAsync(_db, scope, ct);
```

```text
rg -n function (loadDeviceIntelligence|loadMarketingPerformance|loadGrowthEconomics)\( AgentPortal/wwwroot/js
AgentPortal/wwwroot/js/website-analytics.js:5576:  async function loadDeviceIntelligence() {
AgentPortal/wwwroot/js/website-analytics.js:5673:  async function loadMarketingPerformance() {
AgentPortal/wwwroot/js/website-analytics.js:5691:  async function loadGrowthEconomics() {
```

```text
rg -n ParfaitAnalyticsService|ParfaitAnalyticsEventRequest|ParfaitAnalyticsController Infrastructure Protect-Website AgentPortal ParfaitApp --glob *.cs --glob *.cshtml --glob *.csproj
(no matches)
```

```text
rg -n analytics/meta-signal|analytics/business-page|api/analytics/ingest|parfait-analytics/track SHARED/WebsitePlatform Infrastructure/Analytics Protect-Website ParfaitApp --glob *.cs --glob *.cshtml --glob *.js
(no matches)
```

The actual MVC discovery regression includes Infrastructure, Protect, AgentPortal and Parfait assemblies and passed in the 943-case affected run. Compiled IL writer ownership and source guard tests also passed. These structural results supplement, rather than replace, the behavioral tenant and lineage tests.
