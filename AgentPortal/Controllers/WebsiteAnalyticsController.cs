using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Data;
using System.Data.Common;
using System.Text;
using AgentPortal.Models.Analytics;
using AgentPortal.Services.Analytics;
using AgentPortal.Security;
using AgentPortal.Services;
using Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace AgentPortal.Controllers;

[Authorize]
[Route("WebsiteAnalytics")]
[Route("website-analytics")]
    public class WebsiteAnalyticsController : Controller
    {
        private readonly IAnalyticsQueryService _analytics;
        private readonly IMetaAdsService _metaAds;
        private readonly IMetaAdsOAuthService _metaAdsOAuth;
        private readonly Services.Tracking.IAgentTrackingService _tracking;
        private readonly IMetaSignalAnalyticsService _metaSignalAnalytics;
        private readonly ILandingRouteDiscoveryService _landingRouteDiscovery;
        private readonly ILogger<WebsiteAnalyticsController> _logger;
        private readonly Infrastructure.Data.MasterAppDbContext _db;
        private readonly string _founderUpn;
        private readonly IConfiguration _config;
        private readonly EffectiveAgentContext _effectiveContext;
        private readonly WebsiteAnalyticsAiDataBuilder _aiDataBuilder;
        private readonly IVisitorConcentrationService _visitorConcentrationService;
        private readonly IKpiDetailBreakdownService _kpiDetailBreakdownService;
        private readonly IVisitorTrustScoringService _visitorTrustScoringService;
        private readonly Infrastructure.Analytics.MetaCapiCredentialProtector _metaCapiCredentialProtector;
        private readonly IAnalyticsIncidentQueryService _incidentMonitor;

        public WebsiteAnalyticsController(IAnalyticsQueryService analytics, IMetaAdsService metaAds, IMetaAdsOAuthService metaAdsOAuth, Services.Tracking.IAgentTrackingService tracking, IMetaSignalAnalyticsService metaSignalAnalytics, ILandingRouteDiscoveryService landingRouteDiscovery, WebsiteAnalyticsAiDataBuilder aiDataBuilder, IVisitorConcentrationService visitorConcentrationService, IKpiDetailBreakdownService kpiDetailBreakdownService, IVisitorTrustScoringService visitorTrustScoringService, IAnalyticsIncidentQueryService incidentMonitor, ILogger<WebsiteAnalyticsController> logger, Infrastructure.Data.MasterAppDbContext db, IConfiguration config, EffectiveAgentContext effectiveContext, Infrastructure.Analytics.MetaCapiCredentialProtector metaCapiCredentialProtector)
        {
            _analytics = analytics;
            _metaAds = metaAds;
            _metaAdsOAuth = metaAdsOAuth;
            _tracking = tracking;
            _metaSignalAnalytics = metaSignalAnalytics;
            _landingRouteDiscovery = landingRouteDiscovery;
            _aiDataBuilder = aiDataBuilder;
            _visitorConcentrationService = visitorConcentrationService;
            _kpiDetailBreakdownService = kpiDetailBreakdownService;
            _visitorTrustScoringService = visitorTrustScoringService;
            _incidentMonitor = incidentMonitor;
            _logger = logger;
            _db = db;
            _founderUpn = config["Founder:Upn"] ?? throw new InvalidOperationException("Founder:Upn configuration is required");
            _config = config;
            _effectiveContext = effectiveContext;
            _metaCapiCredentialProtector = metaCapiCredentialProtector;
        }

    [HttpGet("event-map")]
    public async Task<IActionResult> EventMap([FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var entries = await new Infrastructure.WebsiteEditing.WebsiteEventMapQuery(_db, _config).ReadAsync(scope, cancellationToken);
        return View("EventMap", entries);
    }

    [HttpGet("")]
    [HttpGet("Index")]
    public async Task<IActionResult> Index([FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] string? preset = null, [FromQuery] DateTime? fromUtc = null, [FromQuery] DateTime? toUtc = null, [FromQuery] TrafficQualityMode? qualityMode = null)
    {
        var viewerTimeZone = GetViewerTimeZone();
        preset = string.IsNullOrWhiteSpace(preset) ? "today" : preset;
        TimeRangeRequest range;
        try
        {
            range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, viewerTimeZone);
        }
        catch (ArgumentException)
        {
            range = TimeRangeRequest.FromPreset("today", null, null, viewerTimeZone);
        }

        var scope = await ResolveScopeAsync(agentProfileId, team);
        var initialQualityMode = ResolveInitialQualityMode(qualityMode);
        range = CloneRangeWithQualityMode(range, initialQualityMode);
        var summary = await LoadSummarySafelyAsync(range, scope);
        summary.ScopeLabel = await ResolveScopeLabelAsync(scope, team);
        ViewData["InitialRangePreset"] = range.Preset;
        ViewData["InitialRangeLabel"] = range.Label;
        ViewData["InitialRangeFrom"] = range.Preset == "custom"
            ? TimeZoneInfo.ConvertTimeFromUtc(range.FromUtc, range.ViewerTimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;
        ViewData["InitialRangeTo"] = range.Preset == "custom"
            ? TimeZoneInfo.ConvertTimeFromUtc(range.ToUtc, range.ViewerTimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;
        ViewData["InitialQualityMode"] = ToClientQualityMode(initialQualityMode);
        ViewData["InitialSummaryJson"] = System.Text.Json.JsonSerializer.Serialize(summary, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        ViewData["InitialScopeLabel"] = summary.ScopeLabel;
        ViewData["InitialSiteKey"] = scope.ScopeType == ScopeType.Founder ? scope.SiteKey : null;
        // Founder Personal must hydrate with the permanent Founder tracking-profile id
        // so the browser preserves the same canonical scope on every AJAX refresh.
        ViewData["InitialScopeProfileId"] =
            scope.ScopeType is ScopeType.Founder or ScopeType.Agent
                ? scope.AgentTrackingProfileId
                : null;
        var landingRoutes = _landingRouteDiscovery.GetAllRoutes();
        ViewData["LandingRoutesBaseUrl"] = _landingRouteDiscovery.GetBaseUrl();
        ViewData["LandingRoutesJson"] = System.Text.Json.JsonSerializer.Serialize(
            landingRoutes,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

        var callerProfile = await GetCallerProfileAsync();
        if (callerProfile != null)
        {
            var urls = await _tracking.GetPersonalUrlsAsync(callerProfile);
            ViewData["PersonalLink"] = urls.PrimaryUrl;
            ViewData["PersonalLinkAlt"] = urls.AlternateSlugUrl;
            ViewData["CallerProfileId"] = callerProfile.Id;
        }

        var canViewFounderTeamUi = FounderGuard.IsFounder(User);
        var canDeleteAnalyticsLeads = CanDeleteAnalyticsLeads();
        ViewData["CanViewFounderTeamUi"] = canViewFounderTeamUi;
        ViewData["CanDeleteAnalyticsLeads"] = canDeleteAnalyticsLeads;
        if (canViewFounderTeamUi)
        {
            // Ensure founder personal link is root
            var rootBase = _landingRouteDiscovery.GetBaseUrl();
            ViewData["PersonalLink"] = rootBase.EndsWith("/") ? rootBase : rootBase + "/";
            ViewData["PersonalLinkAlt"] = null;

            var agents = await _tracking.GetAllProfilesAsync();
            var agentOptions = new List<object>();
            foreach (var agent in agents)
            {
                var urls = await _tracking.GetPersonalUrlsAsync(agent);
                // Founder should surface root as primary
                var primaryOverride = string.Equals(agent.AgentUpn, _founderUpn, StringComparison.OrdinalIgnoreCase)
                    ? (rootBase.EndsWith("/") ? rootBase : rootBase + "/")
                    : urls.PrimaryUrl;
                agentOptions.Add(new { id = agent.Id, name = agent.DisplayName ?? agent.AgentUpn ?? agent.Slug, slug = agent.Slug, primaryUrl = primaryOverride, altUrl = urls.AlternateSlugUrl });
            }
            ViewData["AgentOptionsJson"] = System.Text.Json.JsonSerializer.Serialize(agentOptions);
        }

        return View();
    }

    [Authorize(Policy = "FounderOnly")]
    [HttpGet("incident-monitor")]
    public async Task<IActionResult> IncidentMonitor(CancellationToken cancellationToken)
    {
        var result = await _incidentMonitor.GetSystemMonitorAsync(cancellationToken);
        return Json(result);
    }

    public sealed record MarketingSetupUpdateRequest(
        Guid? AgentProfileId,
        Guid MarketingRevision,
        string? MetaPixelId,
        string? MetaTestEventCode,
        bool BookingEnabled,
        string? MicrosoftBookingsEmbedUrl,
        string? FallbackBookingUrl,
        string? BookingPageIdOrMailbox,
        string? CalendarEmail);

    [HttpGet("marketing-setup")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> MarketingSetup([FromQuery] Guid? agentProfileId = null, CancellationToken cancellationToken = default)
    {
        var tracking = await ResolveMarketingSetupTrackingAsync(agentProfileId, cancellationToken);
        if (tracking is null) return Forbid();

        var profile = await ResolveMarketingSetupAgentProfileAsync(tracking, createIfMissing: false, cancellationToken);
        var owner = await ResolveMarketingOwnerAsync(tracking, cancellationToken);
        var calendarAuthority = HttpContext.RequestServices.GetRequiredService<Infrastructure.Bookings.IMicrosoftCalendarConnectionAuthority>();
        var calendarConnection = await calendarAuthority.GetAsync(owner, cancellationToken);
        var marketing = await GetMarketingSettingsAsync(tracking, owner, cancellationToken);
        var setup = await HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingProviderSetupProjection>()
            .ReadAsync(owner, cancellationToken);
        var adsConnected = setup.Meta.Connected;
        var secureCapi = setup.Meta.CapiConfigured;
        var evidence = setup.Evidence;
        var bookingLive = profile?.BookingEnabled == true &&
            (!string.IsNullOrWhiteSpace(profile.MicrosoftBookingsEmbedUrl) || !string.IsNullOrWhiteSpace(profile.FallbackBookingUrl));
        var openAiConnection = setup.Connection;
        var openAiHealth = setup.Health;
        var openAiProvider = setup.Account;
        var openAiMeasurement = setup.Capability;
        var openAiProviderError = setup.OpenAiError;
        var openAiAccountReady =
            string.Equals(openAiProvider?.Status, "active", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(openAiProvider?.ReviewStatus, Shared.Analytics.OpenAiAdsReviewStatuses.Approved, StringComparison.Ordinal) &&
            openAiConnection.PixelConfigured &&
            openAiConnection.ConversionsApiConfigured && !string.IsNullOrWhiteSpace(openAiConnection.ConversionDataSourceId);

        return Json(new
        {
            source = "canonical_marketing_setup",
            evidence,
            evidenceError = setup.EvidenceError,
            agentProfileId = tracking.Id,
            agentName = tracking.DisplayName ?? tracking.AgentUpn ?? tracking.Slug,
            status = new
            {
                publicReady = !string.IsNullOrWhiteSpace(profile?.FullName) && !string.IsNullOrWhiteSpace(profile?.Phone),
                metaCustomPixel = !string.IsNullOrWhiteSpace(marketing.PixelId),
                openAiReady = openAiAccountReady,
                googleReady = setup.Google.Ready,
                tiktokReady = setup.TikTok.Ready,
                bookingPersonalLive = bookingLive,
                calendarLinked = calendarConnection.Connected
            },
            marketing = new
            {
                revision = marketing.Revision,
                metaPixelId = marketing.PixelId,
                metaTestEventCode = marketing.TestEventCode,
                metaTestEventsConfigured = !string.IsNullOrWhiteSpace(marketing.TestEventCode),
                metaAdsConnected = adsConnected,
                metaAccount = adsConnected ? setup.Meta.AccountName ?? setup.Meta.AccountId ?? "Meta Ads" : null,
                available = setup.Meta.Available,
                error = setup.Meta.Error,
                metaCapiConfiguredSecurely = secureCapi,
                metaCapiManagedAutomatically = true
            },
            google = setup.Google,
            tiktok = setup.TikTok,
            openAi = new
            {
                revision = openAiConnection.Revision,
                exists = openAiConnection.Exists,
                connected = openAiConnection.Connected,
                accountId = openAiConnection.AccountId,
                accountName = openAiConnection.AccountName,
                role = openAiConnection.Role,
                permissions = openAiConnection.Permissions,
                authorizationMethod = openAiConnection.AuthorizationMethod,
                connectionMethod = openAiConnection.Connected ? "Advertiser API key verified" : null,
                providerRole = openAiConnection.Role,
                accountStatus = openAiProvider?.Status,
                accountUrl = openAiProvider?.AccountUrl,
                previewUrl = openAiProvider?.PreviewUrl,
                timezone = openAiProvider?.Timezone,
                currencyCode = openAiProvider?.CurrencyCode,
                reviewStatus = openAiProvider?.ReviewStatus ?? openAiConnection.ReviewStatus,
                reviewReason = openAiProvider?.ReviewReason,
                providerStatusFresh = openAiProvider is not null,
                providerStatusError = openAiProviderError,
                measurementCapabilityStatus = openAiMeasurement?.Status,
                measurementCapabilityHttpStatus = openAiMeasurement?.HttpStatusCode,
                measurementCapabilityDetail = openAiMeasurement?.Detail,
                pixelId = openAiConnection.PixelId,
                pixelConfigured = openAiConnection.PixelConfigured,
                conversionsApiConfigured = openAiConnection.ConversionsApiConfigured,
                conversionDataSourceId = openAiConnection.ConversionDataSourceId,
                lastVerifiedUtc = openAiConnection.LastVerifiedUtc,
                connectedUtc = openAiConnection.ConnectedUtc,
                health = new
                {
                    status = openAiHealth.Status,
                    pending = openAiHealth.PendingDeliveries,
                    retrying = openAiHealth.RetryableDeliveries,
                    failed = openAiHealth.FailedDeliveries,
                    sent = openAiHealth.SentDeliveries,
                    otherDestinationReceipts = openAiHealth.OtherDestinationReceipts,
                    otherDestinationUnresolved = openAiHealth.OtherDestinationUnresolved,
                    lastSentUtc = openAiHealth.LastSentUtc,
                    providerMonitoringAvailable = openAiHealth.ProviderMonitoringAvailable,
                    recentProviderEvents = openAiHealth.RecentProviderEvents
                }
            },
            calendar = new
            {
                connected = calendarConnection.Connected,
                revision = calendarConnection.Revision,
                accountName = calendarConnection.AccountName,
                email = calendarConnection.Email,
                authorizationMethod = calendarConnection.AuthorizationMethod,
                permissions = calendarConnection.Permissions,
                connectedUtc = calendarConnection.ConnectedUtc,
                lastVerifiedUtc = calendarConnection.LastVerifiedUtc,
                accessTokenExpiresUtc = calendarConnection.AccessTokenExpiresUtc
            },
            booking = new
            {
                enabled = profile?.BookingEnabled == true,
                microsoftBookingsEmbedUrl = profile?.MicrosoftBookingsEmbedUrl,
                fallbackBookingUrl = profile?.FallbackBookingUrl,
                bookingPageIdOrMailbox = profile?.BookingPageIdOrMailbox,
                calendarEmail = profile?.CalendarEmail
            }
        });
    }

    public sealed record ExternalAdsAccountRequest(
        Guid? AgentProfileId,
        string Provider,
        string AccountId);

    public sealed record ExternalAdsDisconnectRequest(
        Guid? AgentProfileId,
        string Provider);

    [HttpGet("external-ads/connect")]
    public async Task<IActionResult> ExternalAdsConnect(
        [FromQuery] string provider,
        [FromQuery] Guid? agentProfileId = null,
        [FromQuery] string? returnUrl = null,
        CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null || owner.CommerceBusinessId.HasValue) return Forbid();
        var target = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/WebsiteAnalytics/Index";
        try
        {
            var callback = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/WebsiteAnalytics/external-ads/callback";
            var oauth = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingExternalAdsOAuthService>();
            return Redirect(oauth.BuildConnectUrl(owner, provider, target, callback));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            var key = Uri.EscapeDataString((provider ?? "provider").Trim().ToLowerInvariant());
            return Redirect($"{target}?provider={key}&status=error&message={Uri.EscapeDataString(ex.Message)}");
        }
    }

    [HttpGet("external-ads/callback")]
    public async Task<IActionResult> ExternalAdsCallback(
        [FromQuery] string? code = null,
        [FromQuery(Name = "auth_code")] string? authCode = null,
        [FromQuery] string? state = null,
        [FromQuery] string? error = null,
        [FromQuery(Name = "error_description")] string? errorDescription = null,
        CancellationToken cancellationToken = default)
    {
        var target = "/WebsiteAnalytics/Index";
        try
        {
            var oauth = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingExternalAdsOAuthService>();
            var inspected = oauth.InspectState(state ?? string.Empty);
            if (!await IsAuthorizedExternalAdsOwnerAsync(inspected.Owner, cancellationToken)) return Forbid();
            target = Url.IsLocalUrl(inspected.ReturnUrl) ? inspected.ReturnUrl : target;
            if (!string.IsNullOrWhiteSpace(error))
            {
                var message = string.IsNullOrWhiteSpace(errorDescription) ? error : errorDescription;
                return Redirect($"{target}?provider={Uri.EscapeDataString(inspected.Provider)}&status=error&message={Uri.EscapeDataString(message)}");
            }

            var result = await oauth.CompleteCallbackAsync(
                inspected.Provider,
                authCode ?? code ?? string.Empty,
                state ?? string.Empty,
                cancellationToken);
            if (result.Owner != inspected.Owner ||
                !await IsAuthorizedExternalAdsOwnerAsync(result.Owner, cancellationToken))
                return Forbid();

            var separator = target.Contains('?') ? '&' : '?';
            return Redirect($"{target}{separator}provider={Uri.EscapeDataString(result.Provider)}&status=connected");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Redirect($"{target}{(target.Contains('?') ? '&' : '?')}provider=external&status=error&message={Uri.EscapeDataString(ex.Message)}");
        }
    }

    [HttpGet("external-ads/accounts")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ExternalAdsAccounts(
        [FromQuery] string provider,
        [FromQuery] Guid? agentProfileId = null,
        CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null || owner.CommerceBusinessId.HasValue) return Forbid();
        try
        {
            var oauth = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingExternalAdsOAuthService>();
            return Json(new { provider, accounts = await oauth.GetAccountsAsync(owner, provider, cancellationToken) });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("external-ads/select-account")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExternalAdsSelectAccount(
        [FromBody] ExternalAdsAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(request.AgentProfileId, cancellationToken);
        if (owner is null || owner.CommerceBusinessId.HasValue) return Forbid();
        try
        {
            var oauth = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingExternalAdsOAuthService>();
            await oauth.SelectAccountAsync(owner, request.Provider, request.AccountId, cancellationToken);
            return await MarketingSetup(request.AgentProfileId, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("external-ads/disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExternalAdsDisconnect(
        [FromBody] ExternalAdsDisconnectRequest request,
        CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(request.AgentProfileId, cancellationToken);
        if (owner is null || owner.CommerceBusinessId.HasValue) return Forbid();
        try
        {
            await MarketingConnections.DisconnectAsync(owner, request.Provider, cancellationToken);
            return await MarketingSetup(request.AgentProfileId, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    public sealed record CalendarConnectionRevisionRequest(Guid? AgentProfileId, Guid ConnectionRevision);

    [HttpGet("calendar-connect")]
    public async Task<IActionResult> CalendarConnect(
        [FromQuery] Guid? agentProfileId = null,
        [FromQuery] string? returnUrl = null,
        CancellationToken cancellationToken = default)
    {
        var tracking = await ResolveMarketingSetupTrackingAsync(agentProfileId, cancellationToken);
        if (tracking is null) return Forbid();
        var owner = await ResolveMarketingOwnerAsync(tracking, cancellationToken);
        var callback = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/website-analytics/calendar-callback";
        var target = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/WebsiteAnalytics/Index";
        try
        {
            var authority = HttpContext.RequestServices.GetRequiredService<Infrastructure.Bookings.IMicrosoftCalendarConnectionAuthority>();
            return Redirect(authority.BuildConnectUrl(owner, target, callback));
        }
        catch (InvalidOperationException ex)
        {
            return Redirect($"{target}?calendar=error&message={Uri.EscapeDataString(ex.Message)}");
        }
    }

    [HttpGet("calendar-callback")]
    public async Task<IActionResult> CalendarCallback(
        [FromQuery] string? code = null,
        [FromQuery] string? state = null,
        [FromQuery] string? error = null,
        [FromQuery(Name = "error_description")] string? errorDescription = null,
        CancellationToken cancellationToken = default)
    {
        var target = "/WebsiteAnalytics/Index";
        try
        {
            var authority = HttpContext.RequestServices.GetRequiredService<Infrastructure.Bookings.IMicrosoftCalendarConnectionAuthority>();
            var inspected = authority.InspectState(state ?? string.Empty);
            target = Url.IsLocalUrl(inspected.ReturnUrl) ? inspected.ReturnUrl : target;
            if (!await IsAuthorizedCalendarOwnerAsync(inspected.Owner, cancellationToken)) return Forbid();

            if (!string.IsNullOrWhiteSpace(error))
            {
                var message = string.IsNullOrWhiteSpace(errorDescription) ? error : errorDescription;
                return Redirect($"{target}{(target.Contains('?') ? '&' : '?')}calendar=error&message={Uri.EscapeDataString(message)}");
            }

            var connected = await authority.CompleteCallbackAsync(code ?? string.Empty, state ?? string.Empty, cancellationToken);
            if (connected.Owner != inspected.Owner || !connected.Connected)
                throw new InvalidOperationException("Microsoft Calendar authorization could not be verified for this owner.");

            return Redirect($"{target}{(target.Contains('?') ? '&' : '?')}calendar=connected");
        }
        catch (InvalidOperationException ex)
        {
            return Redirect($"{target}{(target.Contains('?') ? '&' : '?')}calendar=error&message={Uri.EscapeDataString(ex.Message)}");
        }
    }

    [HttpPost("calendar-disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CalendarDisconnect(
        [FromBody] CalendarConnectionRevisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var tracking = await ResolveMarketingSetupTrackingAsync(request.AgentProfileId, cancellationToken);
        if (tracking is null) return Forbid();
        var owner = await ResolveMarketingOwnerAsync(tracking, cancellationToken);
        var authority = HttpContext.RequestServices.GetRequiredService<Infrastructure.Bookings.IMicrosoftCalendarConnectionAuthority>();
        try
        {
            await authority.DisconnectAsync(owner, request.ConnectionRevision, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "Microsoft Calendar connection changed. Reload and try again." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        return await MarketingSetup(tracking.Id, cancellationToken);
    }

    [HttpPost("marketing-setup")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveMarketingSetup(
        [FromBody] MarketingSetupUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var tracking = await ResolveMarketingSetupTrackingAsync(request.AgentProfileId, cancellationToken);
        if (tracking is null) return Forbid();

        var pixel = string.IsNullOrWhiteSpace(request.MetaPixelId) ? null : request.MetaPixelId.Trim();
        if (pixel is not null && (pixel.Length > 32 || pixel.Any(ch => ch < '0' || ch > '9')))
            return BadRequest(new { message = "Meta Pixel ID must contain only digits." });
        var testEventCode = string.IsNullOrWhiteSpace(request.MetaTestEventCode) ? null : request.MetaTestEventCode.Trim();
        if (testEventCode is not null && testEventCode.Length > 100)
            return BadRequest(new { message = "Meta Test Event Code is too long." });

        static string? Clean(string? value, int max) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];

        static bool ValidHttpUrl(string? value) =>
            string.IsNullOrWhiteSpace(value) ||
            (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
             (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp));

        var embed = Clean(request.MicrosoftBookingsEmbedUrl, 2048);
        var fallback = Clean(request.FallbackBookingUrl, 2048);
        var mailbox = Clean(request.BookingPageIdOrMailbox, 320);
        var calendarEmail = Clean(request.CalendarEmail, 320);

        if (!ValidHttpUrl(embed) || !ValidHttpUrl(fallback))
            return BadRequest(new { message = "Booking URLs must be valid HTTP or HTTPS URLs." });
        if (calendarEmail is not null && !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(calendarEmail))
            return BadRequest(new { message = "Enter a valid calendar email." });

        var profile = await ResolveMarketingSetupAgentProfileAsync(tracking, createIfMissing: true, cancellationToken)
            ?? throw new InvalidOperationException("Agent profile could not be resolved.");
        var hasBookingValues = embed is not null || fallback is not null || mailbox is not null || calendarEmail is not null;
        profile.BookingEnabled = request.BookingEnabled ? true : hasBookingValues ? false : null;
        profile.MicrosoftBookingsEmbedUrl = embed;
        profile.FallbackBookingUrl = fallback;
        profile.BookingPageIdOrMailbox = mailbox;
        profile.CalendarEmail = calendarEmail;
        profile.PreferModalOnMobile = false;
        profile.UpdatedUtc = DateTime.UtcNow;

        var marketingService = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.AgentMarketingProfileService>();
        try
        {
            // This writes the existing MarketingConnection and the tracked AgentProfile
            // through the same scoped DbContext SaveChanges transaction. No CAPI secret
            // is accepted here; OAuth-owned Meta Ads credentials remain the only active
            // secure CAPI authority.
            var owner = await ResolveMarketingOwnerAsync(tracking, cancellationToken);
            if (owner == Shared.Analytics.MarketingOwnerScope.Founder)
                await MarketingConnections.SaveSettingsAsync(owner, pixel, testEventCode, null, request.MarketingRevision, cancellationToken);
            else
                await marketingService.SavePixelAsync(tracking, pixel, testEventCode, request.MarketingRevision, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "Marketing setup changed. Reload the setup and try again." });
        }

        return await MarketingSetup(tracking.Id, cancellationToken);
    }

    public sealed record OpenAiConnectRequest(Guid? AgentProfileId, string AdvertiserApiKey, Guid? ExpectedRevision);

    [HttpPost("openai-connect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConnectOpenAi([FromBody] OpenAiConnectRequest request, CancellationToken cancellationToken = default)
    {
        var tracking = await ResolveMarketingSetupTrackingAsync(request.AgentProfileId, cancellationToken);
        if (tracking is null) return Forbid();

        var connector = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IOpenAiAdsDirectConnectionService>();
        try
        {
            await connector.ConnectAsync(
                await ResolveMarketingOwnerAsync(tracking, cancellationToken),
                request.AdvertiserApiKey,
                request.ExpectedRevision,
                cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "ChatGPT Ads connection changed. Reload Marketing Setup and try again." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "OpenAI Ads could not be verified right now. Try again without changing your saved setup." });
        }
        catch (System.Text.Json.JsonException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "OpenAI Ads returned an invalid verification response." });
        }

        return await MarketingCommandReceiptAsync(tracking.Id, cancellationToken);
    }

    public sealed record OpenAiRefreshRequest(Guid? AgentProfileId, Guid ConnectionRevision);

    [HttpPost("openai-refresh")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshOpenAi([FromBody] OpenAiRefreshRequest request, CancellationToken cancellationToken = default)
    {
        var tracking = await ResolveMarketingSetupTrackingAsync(request.AgentProfileId, cancellationToken);
        if (tracking is null) return Forbid();

        var connector = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IOpenAiAdsDirectConnectionService>();
        Infrastructure.Analytics.OpenAiAdsRefreshResult refresh;
        try
        {
            refresh = await connector.RefreshAsync(
                await ResolveMarketingOwnerAsync(tracking, cancellationToken),
                request.ConnectionRevision,
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "ChatGPT Ads connection changed. Reload Marketing Setup and try again." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "OpenAI Ads could not be refreshed right now. The current saved connection was not replaced." });
        }
        catch (System.Text.Json.JsonException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "OpenAI Ads returned an invalid refresh response." });
        }

        return await MarketingCommandReceiptAsync(tracking.Id, cancellationToken, refresh.PixelProvisioning);
    }

    public sealed record OpenAiDisconnectRequest(Guid? AgentProfileId, Guid ConnectionRevision);

    [HttpPost("openai-disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisconnectOpenAi([FromBody] OpenAiDisconnectRequest request, CancellationToken cancellationToken = default)
    {
        var tracking = await ResolveMarketingSetupTrackingAsync(request.AgentProfileId, cancellationToken);
        if (tracking is null) return Forbid();

        var authority = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IOpenAiAdsAccountConnectionAuthority>();
        try
        {
            await authority.DisconnectAsync(await ResolveMarketingOwnerAsync(tracking, cancellationToken), request.ConnectionRevision, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "ChatGPT Ads connection changed. Reload Marketing Setup and try again." });
        }

        return await MarketingCommandReceiptAsync(tracking.Id, cancellationToken);
    }

    private async Task<IActionResult> MarketingCommandReceiptAsync(Guid profileId, CancellationToken cancellationToken, object? pixelProvisioning = null)
    {
        // The mutation has committed. A read failure must not turn its receipt into a failed command.
        try
        {
            var refreshed = await MarketingSetup(profileId, cancellationToken);
            return Json(new { ok = true, setup = (refreshed as JsonResult)?.Value,
                setupStatus = refreshed is JsonResult ? "available" : "unavailable", pixelProvisioning });
        }
        catch (Exception)
        { return Json(new { ok = true, setup = (object?)null, setupStatus = "unavailable", pixelProvisioning }); }
    }

    private async Task<Shared.Analytics.MarketingOwnerScope> ResolveMarketingOwnerAsync(AgentTrackingProfile tracking, CancellationToken cancellationToken) =>
        await Infrastructure.Analytics.CanonicalAdvertisingEventProjection.ResolveOwnerAsync(_db, _config, tracking, cancellationToken)
            ?? throw new InvalidOperationException("The selected website owner could not be resolved.");

    private async Task<AgentTrackingProfile?> ResolveMarketingSetupTrackingAsync(Guid? requestedAgentId, CancellationToken cancellationToken)
    {
        // Aggregate reports are not credential owners, even if a profile is also supplied.
        if (bool.TryParse(Request.Query["team"], out var team) && team) return null;
        if (!requestedAgentId.HasValue || requestedAgentId.Value == Guid.Empty)
            return await GetCallerProfileAsync();

        var scope = await ResolveScopeAsync(requestedAgentId, team: false);
        if (scope.ScopeType is not (ScopeType.Agent or ScopeType.Founder) || scope.AgentTrackingProfileId != requestedAgentId.Value)
            return null;

        return await _db.AgentTrackingProfiles.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == requestedAgentId.Value, cancellationToken);
    }

    private async Task<AgentProfile?> ResolveMarketingSetupAgentProfileAsync(
        AgentTrackingProfile tracking,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        var normalizedUpn = string.IsNullOrWhiteSpace(tracking.AgentUpn) ? null : tracking.AgentUpn.Trim().ToUpperInvariant();
        var profiles = await _db.AgentProfiles
            .Where(profile =>
                (!string.IsNullOrWhiteSpace(tracking.AgentUserId) && profile.AgentUserId == tracking.AgentUserId) ||
                (normalizedUpn != null && (profile.NormalizedEmail == normalizedUpn || profile.AgentUpn == tracking.AgentUpn)))
            .OrderByDescending(profile => profile.AgentUserId == tracking.AgentUserId)
            .ThenByDescending(profile => profile.UpdatedUtc)
            .ToListAsync(cancellationToken);

        var profile = profiles.FirstOrDefault();
        if (profile is not null || !createIfMissing) return profile;

        profile = new AgentProfile
        {
            AgentUserId = tracking.AgentUserId ?? string.Empty,
            AgentUpn = tracking.AgentUpn ?? string.Empty,
            NormalizedEmail = normalizedUpn,
            FullName = tracking.DisplayName,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        _db.AgentProfiles.Add(profile);
        return profile;
    }

    // JSON endpoints -------------------------------------------------
    private TimeZoneInfo GetViewerTimeZone()
    {
        string? timezoneId = null;
        int? timezoneOffsetMinutes = null;

        if (Request.Query.TryGetValue("timezoneId", out var tzIdRaw))
            timezoneId = tzIdRaw.ToString();

        if (Request.Query.TryGetValue("timezoneOffsetMinutes", out var offsetRaw) &&
            int.TryParse(offsetRaw, out var parsedOffset))
        {
            timezoneOffsetMinutes = parsedOffset;
        }

        return AnalyticsViewerTimeZoneResolver.Resolve(timezoneId, timezoneOffsetMinutes);
    }

    private static TrafficQualityMode ResolveInitialQualityMode(TrafficQualityMode? requestedQualityMode = null) =>
        requestedQualityMode ?? TrafficQualityMode.RealHumanTraffic;

    private static TimeRangeRequest CloneRangeWithQualityMode(TimeRangeRequest range, TrafficQualityMode qualityMode)
    {
        return new TimeRangeRequest
        {
            FromUtc = range.FromUtc,
            ToUtc = range.ToUtc,
            Grouping = range.Grouping,
            Label = range.Label,
            Preset = range.Preset,
            ViewerTimeZone = range.ViewerTimeZone,
            QualityMode = qualityMode
        };
    }

    private async Task<SummaryKpiDto> LoadSummarySafelyAsync(
        TimeRangeRequest range,
        ScopeContext scope,
        TrafficType trafficType = TrafficType.All)
    {
        try
        {
            return await _analytics.GetSummaryAsync(range, scope, trafficType);
        }
        catch (Exception ex) when (IsAnalyticsTimeout(ex))
        {
            _logger.LogWarning(
                ex,
                "Website analytics summary timed out for scope {ScopeType} profile {AgentProfileId}. Returning empty summary fallback.",
                scope.ScopeType,
                scope.AgentTrackingProfileId);

            return new SummaryKpiDto
            {
                IsAvailable = false,
                UnavailableReason = "Summary query timed out.",
                RangeLabel = range.Label,
                EnvironmentLabel = "Summary temporarily unavailable"
            };
        }
        catch (DbException ex)
        {
            _logger.LogError(
                ex,
                "Website analytics summary database query failed for scope {ScopeType} profile {AgentProfileId}.",
                scope.ScopeType,
                scope.AgentTrackingProfileId);

            return new SummaryKpiDto
            {
                IsAvailable = false,
                UnavailableReason = "Summary database query failed.",
                RangeLabel = range.Label,
                EnvironmentLabel = "Summary temporarily unavailable"
            };
        }
    }

    private async Task<List<VisitorConcentrationDto>> LoadVisitorConcentrationSafelyAsync(
        TimeRangeRequest range,
        ScopeContext scope,
        TrafficType trafficType,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _visitorConcentrationService.GetVisitorConcentrationAsync(range, scope, trafficType, cancellationToken);
        }
        catch (Exception ex) when (IsAnalyticsTimeout(ex))
        {
            _logger.LogWarning(
                ex,
                "Visitor concentration drill-in timed out for scope {ScopeType} profile {AgentProfileId}. Returning empty concentration set.",
                scope.ScopeType,
                scope.AgentTrackingProfileId);
            return new List<VisitorConcentrationDto>();
        }
    }

    private static bool IsAnalyticsTimeout(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is TimeoutException)
                return true;

            var message = current.Message ?? string.Empty;
            if (message.Contains("execution timeout", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timeout expired", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("command timeout", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ToClientQualityMode(TrafficQualityMode qualityMode)
        => TrafficQualityBucketFilters.ToClientValue(qualityMode);

    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await LoadSummarySafelyAsync(range, scope, trafficType);
        result.ScopeLabel = await ResolveScopeLabelAsync(scope, team);
        return Json(result);
    }

    [HttpGet("DeviceIntelligence")]
    public async Task<IActionResult> DeviceIntelligence([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetDeviceIntelligenceAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("traffic")]
    public async Task<IActionResult> Traffic([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetTrafficAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("page-performance")]
    public async Task<IActionResult> PagePerformance([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetPagePerformanceAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("cta-performance")]
    public async Task<IActionResult> CtaPerformance([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetCtaPerformanceAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("quote-funnel")]
    public async Task<IActionResult> QuoteFunnel([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetQuoteFunnelAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("marketing-health")]
    public async Task<IActionResult> MarketingHealth([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await MarketingHealthProjection.LoadAsync(_analytics, _metaSignalAnalytics,
            range, scope, trafficType, _logger, HttpContext.RequestAborted);
        return Json(result);
    }

    [HttpGet("conversions")]
    public async Task<IActionResult> Conversions([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic, [FromQuery] int recentTake = 100)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetConversionsAsync(range, scope, trafficType, recentTake);
        return Json(result);
    }

    [HttpGet("leads")]
    public async Task<IActionResult> Leads([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic, [FromQuery] int limit = 200)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetLeadsAsync(range, scope, trafficType, limit);
        return Json(result);
    }

    [HttpGet("meta-signal")]
    public async Task<IActionResult> MetaSignal(
        [FromQuery] string? preset,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] Guid? agentProfileId = null,
        [FromQuery] bool team = false,
        [FromQuery] TrafficType trafficType = TrafficType.All,
        [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic,
        [FromQuery] string? quoteType = null,
        [FromQuery] string? campaign = null,
        [FromQuery] string? pageMode = null,
        [FromQuery] string? scoreTier = null)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _metaSignalAnalytics.GetDashboardAsync(range, scope, trafficType, quoteType, campaign, pageMode, scoreTier, HttpContext.RequestAborted);
        result.ScopeLabel = await ResolveScopeLabelAsync(scope, team);
        return Json(result);
    }

    [HttpGet("meta-signal-health")]
    public async Task<IActionResult> MetaSignalHealth(
        [FromQuery] string? preset,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] Guid? agentProfileId = null,
        [FromQuery] bool team = false,
        [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _metaSignalAnalytics.GetHealthDashboardAsync(range, scope, HttpContext.RequestAborted);
        result.ScopeLabel = await ResolveScopeLabelAsync(scope, team);
        return Json(result);
    }

    [HttpPost("DeleteLead")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteLead([FromBody] DeleteLeadRequest? request)
    {
        if (!CanDeleteAnalyticsLeads())
            return Forbid();

        if (request == null || request.LeadId == Guid.Empty)
            return BadRequest(new { message = "A valid leadId is required." });

        try
        {
            var actorId = (User.GetStableUserId() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(actorId))
            {
                actorId = (User.FindFirstValue(ClaimTypes.Email)
                    ?? User.FindFirstValue("preferred_username")
                    ?? User.FindFirstValue("upn")
                    ?? User.Identity?.Name
                    ?? "unknown").Trim();
            }

            if (actorId.Length > 200)
                actorId = actorId[..200];

            var reason = string.IsNullOrWhiteSpace(request.Reason)
                ? "Test lead cleanup"
                : request.Reason.Trim();
            if (reason.Length > 500)
                reason = reason[..500];

            var cancellationToken = HttpContext.RequestAborted;
            var leadColumns = await GetWebsiteLeadColumnSetAsync(cancellationToken);
            var supportsSoftDelete = leadColumns.Contains("IsDeleted");
            var provider = _db.Database.ProviderName ?? string.Empty;
            var isSqlite = provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
            var conn = _db.Database.GetDbConnection();
            var shouldClose = conn.State != ConnectionState.Open;
            if (shouldClose)
                await conn.OpenAsync(cancellationToken);

            try
            {
                var lead = await FindLeadAsync(conn, request.LeadId, supportsSoftDelete, isSqlite, cancellationToken);
                if (lead == null)
                    return NotFound(new { message = "Lead not found." });

                var cleanupSafety = await ReadCleanupSafetyAsync(
                    conn,
                    request.LeadId,
                    leadColumns,
                    isSqlite,
                    cancellationToken);
                if (!cleanupSafety.IsInternal &&
                    !Infrastructure.Leads.WebsiteLeadCaptureSafety.IsLocalHost(cleanupSafety.Host))
                {
                    return BadRequest(new
                    {
                        message = "Only explicitly internal/local test leads can be removed from analytics. Production lead history must be retained."
                    });
                }

                var crmLineage = await HasCrmLineageAsync(conn, request.LeadId, isSqlite, cancellationToken);
                if (crmLineage != false)
                {
                    return Conflict(new
                    {
                        message = crmLineage == true
                            ? "This test lead is linked to CRM and cannot be removed from Analytics independently."
                            : "CRM lineage could not be verified, so cleanup was blocked."
                    });
                }

                if (lead.IsDeleted)
                {
                    return Json(new
                    {
                        ok = true,
                        alreadyDeleted = true,
                        leadId = lead.LeadId
                    });
                }

                DateTime? deletedAtUtc = null;
                string? deleteReason = null;

                if (supportsSoftDelete)
                {
                    await using var updateCmd = conn.CreateCommand();
                    var assignments = new List<string>
                    {
                        $"{QuoteIdentifier("IsDeleted", isSqlite)} = @isDeleted"
                    };
                    AddParameter(updateCmd, "@isDeleted", isSqlite ? 1 : true);

                    if (leadColumns.Contains("DeletedAtUtc"))
                    {
                        deletedAtUtc = DateTime.UtcNow;
                        assignments.Add($"{QuoteIdentifier("DeletedAtUtc", isSqlite)} = @deletedAtUtc");
                        AddParameter(updateCmd, "@deletedAtUtc", deletedAtUtc.Value);
                    }

                    if (leadColumns.Contains("DeletedByUserId"))
                    {
                        assignments.Add($"{QuoteIdentifier("DeletedByUserId", isSqlite)} = @deletedByUserId");
                        AddParameter(updateCmd, "@deletedByUserId", actorId);
                    }

                    if (leadColumns.Contains("DeleteReason"))
                    {
                        deleteReason = reason;
                        assignments.Add($"{QuoteIdentifier("DeleteReason", isSqlite)} = @deleteReason");
                        AddParameter(updateCmd, "@deleteReason", reason);
                    }

                    updateCmd.CommandText = $"""
                        UPDATE {QuoteIdentifier("WebsiteLeads", isSqlite)}
                        SET {string.Join(", ", assignments)}
                        WHERE {QuoteIdentifier("Id", isSqlite)} = @id
                        """;
                    AddParameter(updateCmd, "@id", lead.Id);
                    await updateCmd.ExecuteNonQueryAsync(cancellationToken);
                }
                else
                {
                    await using var deleteCmd = conn.CreateCommand();
                    deleteCmd.CommandText = $"""
                        DELETE FROM {QuoteIdentifier("WebsiteLeads", isSqlite)}
                        WHERE {QuoteIdentifier("Id", isSqlite)} = @id
                        """;
                    AddParameter(deleteCmd, "@id", lead.Id);
                    await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
                    _logger.LogWarning(
                        "Hard deleting website lead {LeadId} because WebsiteLeads.IsDeleted is unavailable in the current database schema.",
                        lead.LeadId);
                }

                return Json(new
                {
                    ok = true,
                    leadId = lead.LeadId,
                    deletedAtUtc,
                    deleteReason,
                    usedHardDelete = !supportsSoftDelete
                });
            }
            finally
            {
                if (shouldClose)
                    await conn.CloseAsync();
            }
        }
        catch (AntiforgeryValidationException ex)
        {
            _logger.LogWarning(ex, "DeleteLead antiforgery validation failed for lead {LeadId}.", request.LeadId);
            return BadRequest(new { message = "Your session expired. Refresh the page and try again." });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "DeleteLead failed for lead {LeadId}. Request={@Request}",
                request?.LeadId,
                request);
            return StatusCode(500, new { message = "Unable to delete lead right now." });
        }

        static async Task<(bool IsInternal, string? Host)> ReadCleanupSafetyAsync(
            DbConnection conn,
            Guid leadId,
            IReadOnlySet<string> columns,
            bool isSqlite,
            CancellationToken cancellationToken)
        {
            var hasInternal = columns.Contains("IsInternal");
            var hasHost = columns.Contains("Host");
            if (!hasInternal && !hasHost)
                return (false, null);

            await using var cmd = conn.CreateCommand();
            var internalSql = hasInternal
                ? (isSqlite ? "COALESCE(\"IsInternal\", 0)" : "CASE WHEN [IsInternal] = 1 THEN 1 ELSE 0 END")
                : "0";
            var hostSql = hasHost ? QuoteIdentifier("Host", isSqlite) : "NULL";
            cmd.CommandText = isSqlite
                ? $"""SELECT {internalSql} AS "IsInternal", {hostSql} AS "Host" FROM "WebsiteLeads" WHERE lower("LeadId") = lower(@leadId) LIMIT 1"""
                : $"""SELECT TOP (1) {internalSql} AS [IsInternal], {hostSql} AS [Host] FROM [WebsiteLeads] WHERE [LeadId] = @leadId""";
            AddParameter(cmd, "@leadId", isSqlite ? leadId.ToString() : leadId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return (false, null);
            return (
                ReadBoolean(reader, "IsInternal"),
                reader.IsDBNull(reader.GetOrdinal("Host")) ? null : Convert.ToString(reader.GetValue(reader.GetOrdinal("Host")), CultureInfo.InvariantCulture));
        }

        static async Task<bool?> HasCrmLineageAsync(
            DbConnection conn,
            Guid leadId,
            bool isSqlite,
            CancellationToken cancellationToken)
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = isSqlite
                    ? """SELECT COUNT(1) FROM "WebsiteLeadIntakeLinks" WHERE lower("WebsiteLeadPublicId") = lower(@leadId)"""
                    : """SELECT COUNT(1) FROM [WebsiteLeadIntakeLinks] WHERE [WebsiteLeadPublicId] = @leadId""";
                AddParameter(cmd, "@leadId", isSqlite ? leadId.ToString() : leadId);
                var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
                return Convert.ToInt64(scalar ?? 0, CultureInfo.InvariantCulture) > 0;
            }
            catch (Exception ex) when (
                ex is Microsoft.Data.Sqlite.SqliteException ||
                ex is Microsoft.Data.SqlClient.SqlException)
            {
                return null;
            }
        }

        static async Task<WebsiteLeadDeleteLookup?> FindLeadAsync(
            DbConnection conn,
            Guid leadId,
            bool supportsSoftDelete,
            bool isSqlite,
            CancellationToken cancellationToken)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = isSqlite
                ? $"""
                    SELECT "Id", "LeadId", {(supportsSoftDelete ? "COALESCE(\"IsDeleted\", 0)" : "0")} AS "IsDeleted"
                    FROM "WebsiteLeads"
                    WHERE lower("LeadId") = lower(@leadId)
                    LIMIT 1
                    """
                : $"""
                    SELECT TOP (1) [Id], [LeadId], {(supportsSoftDelete ? "CASE WHEN [IsDeleted] = 1 THEN 1 ELSE 0 END" : "0")} AS [IsDeleted]
                    FROM [WebsiteLeads]
                    WHERE [LeadId] = @leadId
                    """;
            AddParameter(cmd, "@leadId", isSqlite ? leadId.ToString() : leadId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new WebsiteLeadDeleteLookup
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                LeadId = ReadGuid(reader, "LeadId"),
                IsDeleted = ReadBoolean(reader, "IsDeleted")
            };
        }

        static string QuoteIdentifier(string identifier, bool isSqlite)
            => isSqlite ? $"\"{identifier}\"" : $"[{identifier}]";

        static void AddParameter(DbCommand cmd, string name, object? value)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = name;
            param.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(param);
        }

        static Guid ReadGuid(DbDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            if (reader.IsDBNull(ordinal))
                return Guid.Empty;

            var value = reader.GetValue(ordinal);
            return value switch
            {
                Guid guidValue => guidValue,
                string stringValue when Guid.TryParse(stringValue, out var parsed) => parsed,
                byte[] bytes when bytes.Length == 16 => new Guid(bytes),
                _ => Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var fallback)
                    ? fallback
                    : Guid.Empty
            };
        }

        static bool ReadBoolean(DbDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            if (reader.IsDBNull(ordinal))
                return false;

            var value = reader.GetValue(ordinal);
            return value switch
            {
                bool boolValue => boolValue,
                byte byteValue => byteValue != 0,
                short shortValue => shortValue != 0,
                int intValue => intValue != 0,
                long longValue => longValue != 0,
                string stringValue when bool.TryParse(stringValue, out var parsedBool) => parsedBool,
                string stringValue when long.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong) => parsedLong != 0,
                _ => false
            };
        }
    }

    [HttpGet("agent-performance")]
    public async Task<IActionResult> AgentPerformance([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic, [FromQuery] string? orderBy = null, [FromQuery] bool desc = true, [FromQuery] int? take = null, [FromQuery] int? skip = null)
    {
        if (!FounderGuard.IsFounder(User)) return Forbid();
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var options = new AnalyticsQueryOptions { OrderBy = orderBy ?? "leads", Desc = desc, Take = take, Skip = skip };
        var result = await _analytics.GetAgentPerformanceAsync(range, await ResolveScopeAsync(null, false), trafficType, options);
        return Json(result);
    }

    // ── Behavior Intelligence ─────────────────────────────────────
    [HttpGet("behavior/summary")]
    public async Task<IActionResult> BehaviorSummary([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetEngagementSummaryAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("behavior/time-on-page")]
    public async Task<IActionResult> BehaviorTimeOnPage([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetTimeOnPageAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("behavior/exit-analysis")]
    public async Task<IActionResult> BehaviorExit([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetExitAnalysisAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("behavior/journey")]
    public async Task<IActionResult> BehaviorJourney([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetJourneyAnalysisAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("behavior/source-performance")]
    public async Task<IActionResult> BehaviorSourcePerformance([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetSourcePerformanceAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("quote-funnel/abandonment")]
    public async Task<IActionResult> QuoteFunnelAbandonment([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetFormAbandonmentAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("marketing-manager/context")]
    public async Task<IActionResult> MarketingAnalyticsContext([FromQuery] Guid? agentProfileId = null,
        [FromQuery] string? preset = "30d", [FromQuery] DateTime? fromUtc = null, [FromQuery] DateTime? toUtc = null,
        [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, HttpContext.RequestAborted);
        if (owner is null) return Forbid();
        var scope = await ResolveScopeAsync(agentProfileId, team: false);
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        Response.Headers.CacheControl = "no-store";
        return Json(await _aiDataBuilder.BuildAsync(range, scope, range.Label, owner.OwnerType,
            "All Traffic", TrafficType.All, HttpContext.RequestAborted));
    }

    [HttpGet("ai-review-snapshot")]
    public async Task<IActionResult> AiReviewSnapshot([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        try
        {
            var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
            var scope = await ResolveScopeAsync(agentProfileId, team);

            var payload = await _aiDataBuilder.BuildAsync(range, scope, range.Label, "Current Scope",
                TrafficAttribution.BucketLabel(trafficType), trafficType, HttpContext.RequestAborted);
            return Json(new AiReviewSnapshotDto {
                SnapshotText = WebsiteAnalyticsAiDataBuilder.FormatSnapshot(payload),
                GeneratedAtLocal = payload.GeneratedUtc.ToString("o"),
                ScopeLabel = payload.ScopeLabel,
                RangeLabel = payload.RangeLabel,
                TrafficFilterLabel = payload.TrafficFilter,
                Warnings = payload.Warnings
            });
        }
        catch (Exception ex)
        {
            var requestId = HttpContext.TraceIdentifier;
            TimeRangeRequest fallbackRange;
            try
            {
                fallbackRange = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
            }
            catch
            {
                fallbackRange = TimeRangeRequest.FromPreset("today", viewerTz: GetViewerTimeZone(), qualityMode: qualityMode);
            }
            var fallbackUtc = DateTime.UtcNow;
            var fallbackGenerated = fallbackUtc.ToString("MM/dd/yyyy h:mm tt") + " UTC";
            var warnings = new List<string>
            {
                $"Snapshot generation failed. requestId={requestId}",
                "Check Azure Log Stream / Application Logs for the full exception."
            };
            _logger.LogError(ex, "AI snapshot endpoint failed. requestId={RequestId}", requestId);

            return Json(new AiReviewSnapshotDto
            {
                SnapshotText = BuildAiReviewSnapshotFailureText(fallbackGenerated, fallbackRange.Label, warnings),
                GeneratedAtLocal = fallbackUtc.ToString("o"),
                ScopeLabel = "Current Scope",
                RangeLabel = fallbackRange.Label,
                TrafficFilterLabel = TrafficAttribution.BucketLabel(trafficType),
                Warnings = warnings
            });
        }
    }

    // ── KPI Detail Modal Endpoint ─────────────────────────────────────────────


    [HttpGet("visitor-timeline")]
    public async Task<IActionResult> VisitorTimeline(
        string visitorId,
        string? sessionId = null,
        string preset = "today", DateTime? fromUtc = null, DateTime? toUtc = null,
        Guid? agentProfileId = null, bool team = false,
        TrafficType trafficType = TrafficType.All, TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        visitorId = (visitorId ?? "").Trim();
        sessionId = (sessionId ?? "").Trim();

        if (string.IsNullOrWhiteSpace(visitorId) &&
            string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest(new
            {
                error = "visitorId or sessionId required"
            });
        }

        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var projection = new AnalyticsDetailProjection(_analytics, _kpiDetailBreakdownService);
        return Ok(await projection.VisitorTimelineAsync(visitorId, sessionId, range, scope, trafficType,
            _visitorTrustScoringService, HttpContext.RequestAborted));
    }


    [HttpGet("kpi-detail")]
    public async Task<IActionResult> KpiDetail(
        [FromQuery] string metric,
        [FromQuery] string? preset,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] Guid? agentProfileId = null,
        [FromQuery] bool team = false,
        [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        if (string.IsNullOrWhiteSpace(metric))
            return BadRequest(new { message = "metric is required" });

        metric = metric.ToLowerInvariant().Trim();
        if (metric != "pageviews" && metric != "visitors" && metric != "sessions" && metric != "leads")
            return BadRequest(new { message = $"Unknown metric: {metric}" });

        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);

        var projection = new AnalyticsDetailProjection(_analytics, _kpiDetailBreakdownService);
        return Json(await projection.KpiAsync(metric, range, scope, trafficType,
            LoadVisitorConcentrationSafelyAsync, HttpContext.RequestAborted));
    }

    public sealed record AdvertisingPromotionRequest(
        Guid? AgentProfileId,
        Shared.Analytics.PromotionProposalRequest Promotion);

    public sealed record AdvertisingProposalActionRequest(
        Guid? AgentProfileId,
        Guid ProposalId,
        string Revision);

    public sealed record AdvertisingCampaignStatusRequest(
        Guid? AgentProfileId,
        string CampaignId,
        string CampaignName,
        string Status);

    public sealed record AdvertisingCampaignBudgetRequest(
        Guid? AgentProfileId,
        string CampaignId,
        string CampaignName,
        Shared.Analytics.OpenAiAdsBudget Budget);

    [HttpGet("advertising")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Advertising(
        [FromQuery] Guid? agentProfileId = null,
        CancellationToken cancellationToken = default)
    {
        var tracking = await ResolveMarketingSetupTrackingAsync(agentProfileId, cancellationToken);
        if (tracking is null) return Forbid();
        var owner = await ResolveMarketingOwnerAsync(tracking, cancellationToken);
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingCommandCenterService>();
        try
        {
            return Json(await service.GetAsync(owner, cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or
                                   Infrastructure.Analytics.OpenAiAdsExecutionException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("advertising/promote/draft")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdvertisingPromotionDraft(
        [FromBody] AdvertisingPromotionRequest request,
        CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(request.AgentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingCommandCenterService>();
        try { return Json(await service.DraftPromotionAsync(owner, request.Promotion, cancellationToken)); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Infrastructure.Analytics.OpenAiAdsExecutionException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("advertising/promote/propose")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdvertisingPromotionPropose(
        [FromBody] AdvertisingPromotionRequest request,
        CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(request.AgentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingCommandCenterService>();
        try { return Json(await service.ProposePromotionAsync(owner, request.Promotion, User.GetCanonicalUserId(), cancellationToken)); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Infrastructure.Analytics.OpenAiAdsExecutionException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("advertising/campaign/status/propose")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdvertisingCampaignStatusPropose(
        [FromBody] AdvertisingCampaignStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(request.AgentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingCommandCenterService>();
        try
        {
            return Json(await service.ProposeCampaignStatusAsync(
                owner,
                new Infrastructure.Analytics.AdvertisingCampaignStatusProposalRequest(
                    request.CampaignId, request.CampaignName, request.Status),
                User.GetCanonicalUserId(),
                cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("advertising/campaign/budget/propose")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdvertisingCampaignBudgetPropose(
        [FromBody] AdvertisingCampaignBudgetRequest request,
        CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(request.AgentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingCommandCenterService>();
        try
        {
            return Json(await service.ProposeCampaignBudgetAsync(
                owner,
                new Infrastructure.Analytics.AdvertisingCampaignBudgetProposalRequest(
                    request.CampaignId, request.CampaignName, request.Budget),
                User.GetCanonicalUserId(),
                cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("advertising/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdvertisingApprove(
        [FromBody] AdvertisingProposalActionRequest request,
        CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(request.AgentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingCommandCenterService>();
        try { return Json(await service.ApproveAsync(owner, request.ProposalId, User.GetCanonicalUserId(), request.Revision, cancellationToken)); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { message = "Advertising proposal changed. Reload before approving." }); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("advertising/execute")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdvertisingExecute(
        [FromBody] AdvertisingProposalActionRequest request,
        CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(request.AgentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingCommandCenterService>();
        try { return Json(await service.ExecuteAsync(owner, request.ProposalId, request.Revision, cancellationToken)); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { message = "Advertising proposal changed. Reload before execution." }); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("advertising/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdvertisingReject(
        [FromBody] AdvertisingProposalActionRequest request,
        CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(request.AgentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingCommandCenterService>();
        try { return Json(await service.RejectAsync(owner, request.ProposalId, User.GetCanonicalUserId(), request.Revision, cancellationToken)); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { message = "Advertising proposal changed. Reload before rejecting." }); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("advertising/campaign/{campaignId}/insights")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> AdvertisingCampaignInsights(
        string campaignId,
        [FromQuery] Guid? agentProfileId = null,
        [FromQuery] string? preset = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        try
        {
            var range = TimeRangeRequest.FromPreset(preset ?? "7d", fromUtc, toUtc, GetViewerTimeZone());
            var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IAdvertisingCommandCenterService>();
            return Json(await service.CampaignInsightsAsync(
                owner,
                campaignId,
                new Shared.Analytics.OpenAiAdsInsightsQuery(range.FromUtc, range.ToUtc, "daily"),
                cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Infrastructure.Analytics.OpenAiAdsExecutionException)
        { return BadRequest(new { message = ex.Message }); }
    }

    private async Task<Shared.Analytics.MarketingOwnerScope?> ResolveAdvertisingOwnerAsync(
        Guid? agentProfileId,
        CancellationToken cancellationToken)
    {
        var tracking = await ResolveMarketingSetupTrackingAsync(agentProfileId, cancellationToken);
        return tracking is null ? null : await ResolveMarketingOwnerAsync(tracking, cancellationToken);
    }

    public sealed record MarketingManagerPlanHttpRequest(
        Guid? AgentProfileId,
        string? Preset,
        DateTime? FromUtc,
        DateTime? ToUtc,
        Shared.Analytics.TrafficQualityMode QualityMode,
        Shared.Analytics.MarketingManagerGoalRequest Goal);

    [HttpPost("marketing-manager/plan")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarketingManagerPlan(
        [FromBody] MarketingManagerPlanHttpRequest request,
        CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(request.AgentProfileId, cancellationToken);
        if (owner is null) return Forbid();

        try
        {
            var range = TimeRangeRequest.FromPreset(
                string.IsNullOrWhiteSpace(request.Preset) ? "30d" : request.Preset,
                request.FromUtc,
                request.ToUtc,
                GetViewerTimeZone(),
                request.QualityMode);
            var scope = await ResolveScopeAsync(request.AgentProfileId, team: false);
            var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IMarketingManagerService>();
            return Json(await service.PlanAsync(owner, scope, range, request.Goal, cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Infrastructure.Analytics.OpenAiAdsExecutionException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("marketing-manager/performance")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> MarketingManagerPerformance(
        [FromQuery] Guid? agentProfileId = null,
        [FromQuery] string? preset = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] Shared.Analytics.TrafficQualityMode qualityMode = Shared.Analytics.TrafficQualityMode.RealHumanTraffic,
        CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();

        try
        {
            var range = TimeRangeRequest.FromPreset(
                string.IsNullOrWhiteSpace(preset) ? "30d" : preset,
                fromUtc,
                toUtc,
                GetViewerTimeZone(),
                qualityMode);
            var scope = await ResolveScopeAsync(agentProfileId, team: false);
            var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IUnifiedMarketingPerformanceService>();
            return Json(await service.GetAsync(owner, scope, range, cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Infrastructure.Analytics.OpenAiAdsExecutionException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("growth-economics")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GrowthEconomics(
        [FromQuery] Guid? agentProfileId = null,
        [FromQuery] string? preset = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] Shared.Analytics.TrafficQualityMode qualityMode = Shared.Analytics.TrafficQualityMode.RealHumanTraffic,
        CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        try
        {
            var range = TimeRangeRequest.FromPreset(
                string.IsNullOrWhiteSpace(preset) ? "30d" : preset,
                fromUtc,
                toUtc,
                GetViewerTimeZone(),
                qualityMode);
            var scope = await ResolveScopeAsync(agentProfileId, team: false);
            var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IBlendedGrowthEconomicsService>();
            return Json(await service.GetAsync(owner, scope, range, cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Infrastructure.Analytics.OpenAiAdsExecutionException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("openai-onboarding")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> OpenAiOnboarding(
        [FromQuery] Guid? agentProfileId = null,
        CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IOpenAiAdsOnboardingService>();
        try { return Json(await service.GetAsync(owner, cancellationToken)); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Infrastructure.Analytics.OpenAiAdsExecutionException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("meta-campaigns")]
    public async Task<IActionResult> MetaCampaigns([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        try
        {
            var selectedAgentId = await ResolveMetaConnectionAgentIdAsync(agentProfileId, team);
            if (!selectedAgentId.HasValue || selectedAgentId.Value == Guid.Empty)
                throw new InvalidOperationException("Select an agent scope to view Meta campaigns.");

            var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
            var scope = await ResolveScopeAsync(agentProfileId, team);
            var result = await _metaAds.GetCampaignsAsync(range, scope, HttpContext.RequestAborted);
            return Json(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Meta campaigns request failed due to configuration/scope constraints.");
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("meta-connect")]
    public async Task<IActionResult> MetaConnect([FromQuery] string? returnUrl = null, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false)
    {
        var target = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/WebsiteAnalytics/Index";
        try
        {
            var agentId = await ResolveMetaConnectionAgentIdAsync(agentProfileId, team);
            if (!agentId.HasValue || agentId.Value == Guid.Empty)
                return Redirect($"{target}?meta=error&message={Uri.EscapeDataString("Select an agent scope to connect Meta Ads.")}");

            var owner = await ResolveAdvertisingOwnerAsync(agentId.Value, HttpContext.RequestAborted);
            if (owner is null) return Forbid();
            var connectUrl = _metaAdsOAuth.BuildConnectUrl(owner, target);
            return Redirect(connectUrl);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Meta connect request failed.");
            return Redirect($"{target}?meta=error&message={Uri.EscapeDataString(ex.Message)}");
        }
    }

    [HttpGet("meta-callback")]
    public async Task<IActionResult> MetaCallback([FromQuery] string? code = null, [FromQuery] string? state = null, [FromQuery] string? error = null, [FromQuery(Name = "error_description")] string? errorDescription = null)
    {
        var target = "/WebsiteAnalytics/Index";
        if (!string.IsNullOrWhiteSpace(error))
        {
            var msg = string.IsNullOrWhiteSpace(errorDescription) ? error : errorDescription;
            return Redirect($"{target}?meta=error&message={Uri.EscapeDataString(msg)}");
        }

        try
        {
            var pending = _metaAdsOAuth.InspectState(state ?? string.Empty);
            if (!await IsAuthorizedMetaOwnerAsync(pending.Owner)) return Forbid();
            var result = await _metaAdsOAuth.CompleteCallbackAsync(code ?? string.Empty, state ?? string.Empty, HttpContext.RequestAborted);
            if (result.Owner != pending.Owner || !await IsAuthorizedMetaOwnerAsync(result.Owner)) return Forbid();
            await MarketingConnections.SaveAdsAsync(result.Owner, result.Connection, HttpContext.RequestAborted);
            target = Url.IsLocalUrl(result.ReturnUrl) ? result.ReturnUrl : target;
            return Redirect($"{target}?meta=connected");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Meta callback failed.");
            return Redirect($"{target}?meta=error&message={Uri.EscapeDataString(ex.Message)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Meta callback failed with unexpected error.");
            return Redirect($"{target}?meta=error&message={Uri.EscapeDataString("Meta connection failed unexpectedly. Please try again.")}");
        }
    }

    [HttpGet("meta-connection-status")]
    public async Task<IActionResult> MetaConnectionStatus([FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false)
    {
        var agentId = await ResolveMetaConnectionAgentIdAsync(agentProfileId, team);
        if (!agentId.HasValue || agentId.Value == Guid.Empty)
        {
            return Json(new MetaAdsConnectionStatusDto
            {
                Connected = false,
                AgentTrackingProfileId = null,
                RequiresAgentScope = true,
                Message = "Select an agent scope to view Meta Ads status."
            });
        }

        var owner = await ResolveAdvertisingOwnerAsync(agentId.Value, HttpContext.RequestAborted);
        if (owner is null) return Forbid();
        var record = await MarketingConnections.GetAdsAsync(owner, HttpContext.RequestAborted);
        if (record == null)
        {
            return Json(new MetaAdsConnectionStatusDto
            {
                Connected = false,
                AgentTrackingProfileId = agentId,
                Message = "Meta Ads not connected for the selected agent."
            });
        }

        return Json(new MetaAdsConnectionStatusDto
        {
            Connected = true,
            AgentTrackingProfileId = agentId,
            AccountId = record.AccountId,
            AccountName = record.AccountName,
            BusinessId = record.BusinessId,
            BusinessName = record.BusinessName,
            MetaUserName = record.MetaUserName,
            ConnectedUtc = record.ConnectedUtc,
            AccessTokenExpiresUtc = record.AccessTokenExpiresUtc
        });
    }

    [HttpPost("meta-disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MetaDisconnect([FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false)
    {
        var agentId = await ResolveMetaConnectionAgentIdAsync(agentProfileId, team);
        if (!agentId.HasValue || agentId.Value == Guid.Empty)
            return BadRequest(new { message = "Select an agent scope to disconnect Meta Ads." });

        var owner = await ResolveAdvertisingOwnerAsync(agentId.Value, HttpContext.RequestAborted);
        if (owner is null) return Forbid();
        await MarketingConnections.DisconnectAsync(owner, HttpContext.RequestAborted);
        return Json(new { ok = true });
    }

    private Infrastructure.Analytics.MarketingConnectionStore MarketingConnections =>
        HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingConnectionStore>();

    private async Task<MarketingConnection> GetMarketingSettingsAsync(AgentTrackingProfile tracking,
        Shared.Analytics.MarketingOwnerScope owner, CancellationToken ct)
    {
        if (owner != Shared.Analytics.MarketingOwnerScope.Founder)
            return await HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.AgentMarketingProfileService>().GetAsync(tracking, ct);
        // Initialize the Founder owner without importing any agent-owned credentials or settings.
        if (await MarketingConnections.GetStatusAsync(owner, ct) is not { } row)
        {
            await MarketingConnections.ImportProfileAsync(owner, null, null, null, ct);
            row = (await MarketingConnections.GetStatusAsync(owner, ct))!;
        }
        return row;
    }

    private async Task<bool> IsAuthorizedMetaOwnerAsync(Shared.Analytics.MarketingOwnerScope owner)
    {
        if (User.Identity?.IsAuthenticated != true || owner.CommerceBusinessId.HasValue) return false;
        var resolved = await ResolveAdvertisingOwnerAsync(owner.AgentTrackingProfileId, HttpContext.RequestAborted);
        return resolved == owner;
    }

    private async Task<bool> IsAuthorizedExternalAdsOwnerAsync(
        Shared.Analytics.MarketingOwnerScope owner,
        CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated != true || owner.CommerceBusinessId.HasValue)
            return false;
        var resolved = await ResolveAdvertisingOwnerAsync(owner.AgentTrackingProfileId, cancellationToken);
        return resolved == owner;
    }

    private async Task<bool> IsAuthorizedCalendarOwnerAsync(
        Shared.Analytics.MarketingOwnerScope owner,
        CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated != true || owner.CommerceBusinessId.HasValue) return false;
        var tracking = await ResolveMarketingSetupTrackingAsync(owner.AgentTrackingProfileId, cancellationToken);
        if (tracking is null) return false;
        return await ResolveMarketingOwnerAsync(tracking, cancellationToken) == owner;
    }

    private async Task<ScopeContext> ResolveScopeAsync(Guid? requestedAgentId, bool team = false)
    {
        var resolved = await new WebsiteAnalyticsScopeResolver(_effectiveContext, _tracking, _db, _logger)
            .ResolveAsync(HttpContext, requestedAgentId, team);

        if (resolved.ScopeType != ScopeType.Founder)
            return resolved;

        var requestedSite = Request.Query["siteKey"].ToString().Trim().ToLowerInvariant();
        if (requestedSite is not ("legend" or "protect"))
            return resolved;

        return new ScopeContext
        {
            ScopeType = resolved.ScopeType,
            AgentTrackingProfileId = resolved.AgentTrackingProfileId,
            CommerceBusinessId = resolved.CommerceBusinessId,
            ReportingOwner = resolved.ReportingOwner,
            SiteKey = requestedSite
        };
    }

    private Task<Domain.Entities.AgentTrackingProfile?> GetCallerProfileAsync() =>
        new WebsiteAnalyticsScopeResolver(_effectiveContext, _tracking, _db, _logger).GetCallerProfileAsync();

    private async Task<Guid?> ResolveMetaConnectionAgentIdAsync(Guid? requestedAgentId = null, bool team = false)
    {
        if (team) return null;
        var scope = await ResolveScopeAsync(requestedAgentId, team);
        return scope.ScopeType is (ScopeType.Agent or ScopeType.Founder) && scope.AgentTrackingProfileId != Guid.Empty
            ? scope.AgentTrackingProfileId : null;
    }

    private async Task<string> ResolveScopeLabelAsync(ScopeContext scope, bool team)
    {
        if (scope.ScopeType == ScopeType.Global)
            return "Global";

        var agentId = scope.AgentTrackingProfileId;
        if (!agentId.HasValue || agentId.Value == Guid.Empty)
            return "Agent Scope";

        var profile = await _db.AgentTrackingProfiles.AsNoTracking()
            .Where(p => p.Id == agentId.Value)
            .Select(p => new { p.DisplayName, p.AgentUpn, p.Slug })
            .FirstOrDefaultAsync();

        if (profile == null)
            return "Agent Scope";

        var agentName = profile.DisplayName ?? profile.AgentUpn ?? profile.Slug;
        if (FounderGuard.IsFounder(User) &&
            !string.IsNullOrWhiteSpace(profile.AgentUpn) &&
            string.Equals(profile.AgentUpn, _founderUpn, StringComparison.OrdinalIgnoreCase))
        {
            return "Founder Personal";
        }
        return string.IsNullOrWhiteSpace(agentName) ? "Agent Scope" : $"Agent: {agentName}";
    }

    private bool CanDeleteAnalyticsLeads()
    {
        if (FounderGuard.IsFounder(User))
            return true;

        var oid = (User?.FindFirst("oid")?.Value ?? string.Empty).Trim();
        var upn = (User?.FindFirstValue(ClaimTypes.Email)
            ?? User?.FindFirstValue("preferred_username")
            ?? User?.FindFirstValue("upn")
            ?? User?.Identity?.Name
            ?? string.Empty).Trim();

        return MatchesConfiguredUser(oid, Environment.GetEnvironmentVariable("LEGEND_ADMIN_OIDS"))
            || MatchesConfiguredUser(upn, Environment.GetEnvironmentVariable("LEGEND_ADMIN_UPNS"));
    }

    private static bool MatchesConfiguredUser(string value, string? configuredList)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(configuredList))
            return false;

        return configuredList
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidate => candidate.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<HashSet<string>> GetWebsiteLeadColumnSetAsync(CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conn = _db.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose)
            await conn.OpenAsync(cancellationToken);

        try
        {
            await using var cmd = conn.CreateCommand();
            var provider = _db.Database.ProviderName ?? string.Empty;

            if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                cmd.CommandText = "PRAGMA table_info(\"WebsiteLeads\")";
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                var nameOrdinal = reader.GetOrdinal("name");
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (!reader.IsDBNull(nameOrdinal))
                        columns.Add(reader.GetString(nameOrdinal));
                }

                return columns;
            }

            cmd.CommandText = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @tableName";
            var tableName = cmd.CreateParameter();
            tableName.ParameterName = "@tableName";
            tableName.Value = "WebsiteLeads";
            cmd.Parameters.Add(tableName);

            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (!reader.IsDBNull(0))
                        columns.Add(reader.GetString(0));
                }
            }

            return columns;
        }
        finally
        {
            if (shouldClose)
                await conn.CloseAsync();
        }
    }

    private static List<string> BuildSnapshotWarnings(SummaryKpiDto summary)
    {
        var warnings = new List<string>();
        if (summary.SessionLowSample)
            warnings.Add("Session conversion is based on a low sample size.");
        if (summary.IntentLowSample)
            warnings.Add("Intent conversion is based on a low sample size.");
        if (RequiresEnvironmentCaution(summary.EnvironmentLabel))
            warnings.Add("Snapshot includes non-production, localhost, or mixed analytics data; confirm production-only filtering before high-stakes decisions.");
        return warnings;
    }

    private static bool RequiresEnvironmentCaution(string? environmentLabel)
    {
        if (string.IsNullOrWhiteSpace(environmentLabel))
            return false;

        return environmentLabel.Contains("Mixed", StringComparison.OrdinalIgnoreCase) ||
               environmentLabel.Contains("Development", StringComparison.OrdinalIgnoreCase) ||
               environmentLabel.Contains("Internal", StringComparison.OrdinalIgnoreCase) ||
               environmentLabel.Contains("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildAiReviewSnapshotFailureText(string generatedAtLocal, string rangeLabel, IReadOnlyCollection<string> warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SECTION A — HEADER");
        sb.AppendLine("WEBSITE ANALYTICS AI REVIEW SNAPSHOT");
        sb.AppendLine($"Generated: {generatedAtLocal}");
        sb.AppendLine($"Range: {rangeLabel}");
        sb.AppendLine("Scope: Current Scope");
        sb.AppendLine();
        sb.AppendLine("SECTION B — ACTIVE CAMPAIGN PERFORMANCE");
        sb.AppendLine("No active campaigns in range.");
        sb.AppendLine();
        sb.AppendLine("SECTION I — DATA QUALITY / CONTEXT NOTES");
        sb.AppendLine("- Snapshot generation encountered an internal error.");
        if (warnings.Any())
        {
            sb.AppendLine("- Current warnings:");
            foreach (var warning in warnings)
                sb.AppendLine($"  - {warning}");
        }
        sb.AppendLine();
        sb.AppendLine("SECTION J — CHATGPT COPY PROMPT FOOTER");
        sb.AppendLine("CHATGPT ANALYSIS REQUEST");
        sb.AppendLine("Analyze this snapshot and identify likely causes of tracking/reporting issues.");
        return sb.ToString().TrimEnd();
    }


    /// <summary>
    /// Developer-only diagnostic endpoint. Returns traffic bucket counts and attribution
    /// distribution for the requested range/scope. Safe to call; never modifies data.
    /// To hide from normal users, add [Authorize(Policy = "FounderOnly")] or restrict by role.
    /// </summary>
    [HttpGet("debug/traffic-buckets")]
    public async Task<IActionResult> DebugTrafficBuckets(
        [FromQuery] string? preset,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] Guid? agentProfileId = null,
        [FromQuery] bool team = false,
        [FromQuery] TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        if (!FounderGuard.IsFounder(User))
            return Forbid();

        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone(), qualityMode);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var total = await _analytics.GetSummaryAsync(range, scope, TrafficType.All);
        var buckets = new List<object>();

        foreach (var trafficType in new[]
        {
            TrafficType.PaidAds,
            TrafficType.Organic,
            TrafficType.Direct,
            TrafficType.Referral,
            TrafficType.Unknown
        })
        {
            var summary = await _analytics.GetSummaryAsync(range, scope, trafficType);
            buckets.Add(new
            {
                Bucket = trafficType.ToString(),
                summary.Sessions,
                summary.VerifiedLeads
            });
        }

        return Json(new
        {
            Range = range.Label,
            Scope = await ResolveScopeLabelAsync(scope, team),
            QualityMode = TrafficQualityBucketFilters.ToClientValue(range.QualityMode),
            TotalSessions = total.Sessions,
            TotalLeads = total.VerifiedLeads,
            Buckets = buckets,
            Note = "Uses the same scoped attribution, session/visitor fallback and traffic-quality authority as production analytics."
        });
    }

    public sealed class DeleteLeadRequest
    {
        public Guid LeadId { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class WebsiteLeadDeleteLookup
    {
        public long Id { get; set; }
        public Guid LeadId { get; set; }
        public bool IsDeleted { get; set; }
    }
}
