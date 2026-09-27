using Domain.Entities;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Auth;
using Shared.Crm;
using Shared.Analytics;
using System.Text.Json;

namespace Infrastructure.Businesses;

[Authorize]
[Route("business/{businessId:guid}")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public abstract partial class BusinessWorkspaceControllerBase(BusinessWorkspaceService workspace) : Controller
{
    protected abstract Task<CommerceBusiness?> ResolveBusinessAsync(Guid id, string capability, CancellationToken ct);

    [HttpGet("website-session")]
    public async Task<IActionResult> WebsiteSession(Guid businessId, CancellationToken cancellationToken)
    {
        if (await ResolveBusinessAsync(businessId, "website", cancellationToken) is null) return Forbid();
        return await CreateWebsiteSessionAsync(businessId, cancellationToken);
    }

    protected virtual Task<IActionResult> CreateWebsiteSessionAsync(Guid businessId, CancellationToken ct) =>
        Task.FromResult<IActionResult>(Forbid());

    [HttpGet("crm/api/Clients/QuickView")]
    public async Task<IActionResult> ClientQuickView(Guid businessId, string clientUserId, CancellationToken cancellationToken)
    {
        if (await ResolveBusinessAsync(businessId, "crm", cancellationToken) is null) return Forbid();
        var result = await workspace.QuickViewAsync(businessId, clientUserId, "Client", cancellationToken);
        return result is null ? NotFound() : Json(result);
    }

    [HttpGet("crm/api/Leads/Lead")]
    public async Task<IActionResult> LeadQuickView(Guid businessId, string id, CancellationToken cancellationToken)
    {
        if (await ResolveBusinessAsync(businessId, "crm", cancellationToken) is null) return Forbid();
        var result = await workspace.QuickViewAsync(businessId, id, "Lead", cancellationToken);
        return result is null ? NotFound() : Json(result);
    }

    [HttpPost("crm/api/{recordSet:regex(^(Clients|Leads)$)}/SaveQuickView")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SaveQuickView(Guid businessId, string recordSet, [FromBody] BusinessCrmQuickViewRequest input,
        CancellationToken cancellationToken) => ExecuteCrmWrite(businessId, () => workspace.SaveQuickViewAsync(businessId,
            recordSet == "Clients" ? "Client" : "Lead", input, User.GetCanonicalUserId(), cancellationToken), cancellationToken);

    [HttpPost("crm/api/Clients/AddActivity")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> AddActivity(Guid businessId, [FromBody] BusinessCrmActivityRequest input,
        CancellationToken cancellationToken) => ExecuteCrmWrite(businessId, () => workspace.AddActivityAsync(businessId,
            input, User.GetCanonicalUserId(), cancellationToken), cancellationToken);

    [HttpPost("crm/api/{recordSet:regex(^(Clients|Leads)$)}/Reorder")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Reorder(Guid businessId, string recordSet, [FromBody] BusinessCrmReorderRequest input,
        CancellationToken cancellationToken) => ExecuteCrmWrite(businessId, () => workspace.ReorderAsync(businessId,
            recordSet == "Clients" ? "Client" : "Lead", input, User.GetCanonicalUserId(), cancellationToken), cancellationToken);

    private async Task<IActionResult> ExecuteCrmWrite(Guid businessId, Func<Task<object?>> write, CancellationToken ct)
    {
        if (await ResolveBusinessAsync(businessId, "crm", ct) is null) return Forbid();
        if (!ModelState.IsValid) return BadRequest(ModelState);
        try
        {
            var result = await write();
            return result is null ? NotFound() : Json(result);
        }
        catch (DbUpdateConcurrencyException) { return Conflict("This contact changed in another session. Reload before saving."); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("crm/api/Clients/BulkUpdate")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> BulkUpdate(Guid businessId, [FromBody] BusinessCrmBulkRequest input,
        CancellationToken cancellationToken) => ExecuteCrmWrite(businessId, () => workspace.BulkUpdateAsync(businessId,
            input, User.GetCanonicalUserId(), cancellationToken), cancellationToken);

    [HttpGet("clients")]
    [HttpGet("clients/{contactId}")]
    public Task<IActionResult> Clients(Guid businessId, string? contactId, string? search = null, int page = 1,
        CancellationToken cancellationToken = default) =>
        RenderCrmAsync(businessId, contactId, "Client", search, page, cancellationToken);

    [HttpGet("leads")]
    [HttpGet("leads/{contactId}")]
    public Task<IActionResult> Leads(Guid businessId, string? contactId, string? search = null, int page = 1,
        CancellationToken cancellationToken = default) =>
        RenderCrmAsync(businessId, contactId, "Lead", search, page, cancellationToken);

    // Preserve existing bookmarks without maintaining a third CRM page.
    [HttpGet("crm")]
    [HttpGet("crm/{contactId}")]
    public async Task<IActionResult> Crm(Guid businessId, string? contactId, string kind = "Lead", string? search = null,
        int page = 1, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "crm", cancellationToken) is null) return Forbid();
        return RedirectToAction(kind == "Client" ? nameof(Clients) : nameof(Leads),
            new { businessId, contactId, search, page });
    }

    private async Task<IActionResult> RenderCrmAsync(Guid businessId, string? contactId, string kind, string? search,
        int page, CancellationToken cancellationToken)
    {
        var business = await ResolveBusinessAsync(businessId, "crm", cancellationToken);
        if (business is null) return Forbid();
        var model = await workspace.CrmAsync(business, kind, search, page, contactId, cancellationToken);
        model.CanCustomize = await ResolveBusinessAsync(businessId, "settings", cancellationToken) is not null;
        if (contactId is not null && model.Selected is null) return NotFound();
        ViewData["BusinessWorkspace"] = model;
        ViewData["Title"] = model.Kind == "Client" ? model.Preferences.ClientLabel : model.Preferences.LeadLabel;
        ViewBag.Search = model.Search;
        ViewBag.TotalClients = model.Total;
        return View(model.Kind == "Client" ? "~/Views/Clients/Index.cshtml" : "~/Views/Leads/Index.cshtml", model.CanonicalContacts);
    }

    [HttpPost("crm/{contactId}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveContact(Guid businessId, string contactId, BusinessCrmEdit input, CancellationToken cancellationToken)
    {
        if (await ResolveBusinessAsync(businessId, "crm", cancellationToken) is null) return Forbid();
        if (!ModelState.IsValid) return BadRequest(ModelState);
        try { if (!await workspace.UpdateAsync(businessId, contactId, input, User.GetCanonicalUserId(), cancellationToken)) return NotFound(); }
        catch (DbUpdateConcurrencyException) { return Conflict("This contact changed in another session. Reload before saving."); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        return RedirectToAction(input.Kind == "Client" ? nameof(Clients) : nameof(Leads), new { businessId, contactId });
    }

    [HttpGet("analytics")]
    public async Task<IActionResult> Analytics(Guid businessId, int days = 30, CancellationToken cancellationToken = default)
    {
        var business = await ResolveBusinessAsync(businessId, "analytics", cancellationToken);
        if (business is null) return Forbid();
        var model = await workspace.AnalyticsAsync(business, days, cancellationToken);
        model.CanCustomize = await ResolveBusinessAsync(businessId, "settings", cancellationToken) is not null;
        ViewData["AnalyticsBase"] = $"/business/{businessId}/analytics";
        ViewData["InitialScopeLabel"] = business.DisplayName;
        ViewData["InitialRangePreset"] = days == 7 ? "7d" : days == 90 ? "90d" : "30d";
        ViewData["InitialRangeLabel"] = $"Last {days} days";
        ViewData["InitialSummaryJson"] = JsonSerializer.Serialize(model.Summary);
        ViewData["BusinessWorkspace"] = model;
        ViewData["AnalyticsCanMetaAds"] = true;
        ViewData["AnalyticsCanAiReview"] = false;
        ViewData["AnalyticsCanAgentPerformance"] = false;
        ViewData["AnalyticsCanIncidentMonitor"] = false;
        return View("~/Views/WebsiteAnalytics/Index.cshtml");
    }

    [HttpGet("analytics/meta-campaigns")]
    public async Task<IActionResult> MetaCampaigns(
        Guid businessId,
        string? preset = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic,
        string? timezoneId = null,
        int? timezoneOffsetMinutes = null,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        try
        {
            var timezone = AnalyticsViewerTimeZoneResolver.Resolve(timezoneId, timezoneOffsetMinutes);
            var range = TimeRangeRequest.FromPreset(preset ?? "30d", fromUtc, toUtc, timezone, qualityMode);
            var service = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IMetaAdsService>();
            return Json(await service.GetCampaignsAsync(range, ScopeContext.ForBusiness(businessId), cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or
                                   TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("analytics/meta-connection-status")]
    public async Task<IActionResult> MetaConnectionStatus(Guid businessId, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        var store = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingConnectionStore>();
        var row = await store.GetStatusAsync(MarketingOwnerScope.Business(businessId), cancellationToken);
        var connected = row is not null &&
            !row.DisconnectedUtc.HasValue &&
            !string.IsNullOrWhiteSpace(row.AdsAccessTokenCiphertext) &&
            (!row.AccessTokenExpiresUtc.HasValue || row.AccessTokenExpiresUtc > DateTime.UtcNow);
        return Json(new
        {
            connected,
            requiresAgentScope = false,
            accountId = row?.AdAccountId,
            accountName = row?.AdAccountName,
            businessId = row?.MetaBusinessManagerId,
            businessName = row?.MetaBusinessManagerName,
            metaUserName = row?.MetaUserName,
            connectedUtc = row?.ConnectedUtc,
            accessTokenExpiresUtc = row?.AccessTokenExpiresUtc,
            message = connected ? null : "Meta Ads not connected for this business."
        });
    }

    [HttpGet("analytics/meta-connect")]
    public async Task<IActionResult> MetaConnect(
        Guid businessId,
        [FromQuery] string? returnUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        var fallback = $"/business/{businessId:D}/analytics";
        try
        {
            var callback = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/business/meta-callback";
            var oauth = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingMetaAdsOAuthService>();
            var connect = oauth.BuildConnectUrl(
                MarketingOwnerScope.Business(businessId),
                string.IsNullOrWhiteSpace(returnUrl) ? fallback : returnUrl,
                callback);
            return Redirect(connect);
        }
        catch (InvalidOperationException ex)
        {
            return Redirect($"{fallback}?meta=error&message={Uri.EscapeDataString(ex.Message)}");
        }
    }

    [HttpGet("/business/meta-callback")]
    public async Task<IActionResult> MetaCallback(
        [FromQuery] string? code = null,
        [FromQuery] string? state = null,
        [FromQuery] string? error = null,
        [FromQuery(Name = "error_description")] string? errorDescription = null,
        CancellationToken cancellationToken = default)
    {
        var fallback = "/";
        try
        {
            var oauth = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingMetaAdsOAuthService>();
            var inspected = oauth.InspectState(state ?? string.Empty);
            var businessId = inspected.Owner.CommerceBusinessId
                ?? throw new InvalidOperationException("Meta OAuth state is not business-scoped.");
            fallback = $"/business/{businessId:D}/analytics";

            if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
            if (!string.IsNullOrWhiteSpace(error))
            {
                var message = string.IsNullOrWhiteSpace(errorDescription) ? error : errorDescription;
                return Redirect($"{fallback}?meta=error&message={Uri.EscapeDataString(message)}");
            }

            var result = await oauth.CompleteCallbackAsync(code ?? string.Empty, state ?? string.Empty, cancellationToken);
            if (result.Owner.CommerceBusinessId != businessId || result.Owner.AgentTrackingProfileId.HasValue)
                throw new InvalidOperationException("Meta OAuth owner scope does not match this business.");

            var store = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingConnectionStore>();
            await store.SaveAdsAsync(result.Owner, result.Connection, cancellationToken);
            return Redirect($"{result.ReturnUrl}{(result.ReturnUrl.Contains('?') ? '&' : '?')}meta=connected");
        }
        catch (InvalidOperationException ex)
        {
            return Redirect($"{fallback}?meta=error&message={Uri.EscapeDataString(ex.Message)}");
        }
    }

    [HttpPost("analytics/meta-disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MetaDisconnect(Guid businessId, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        var store = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingConnectionStore>();
        await store.DisconnectAsync(MarketingOwnerScope.Business(businessId), cancellationToken);
        return Json(new { ok = true });
    }

    public sealed record BusinessMarketingSetupUpdateRequest(
        Guid MarketingRevision,
        Guid ProfileRevision,
        string? MetaPixelId,
        string? MetaTestEventCode,
        bool BookingEnabled,
        string? MicrosoftBookingsEmbedUrl,
        string? FallbackBookingUrl,
        string? BookingPageIdOrMailbox,
        string? CalendarEmail);

    public sealed record BusinessOpenAiConnectRequest(string AdvertiserApiKey, Guid? ExpectedRevision);
    public sealed record BusinessOpenAiRevisionRequest(Guid ConnectionRevision);

    [HttpGet("analytics/marketing-setup")]
    public async Task<IActionResult> MarketingSetup(Guid businessId, CancellationToken cancellationToken = default)
    {
        var business = await ResolveBusinessAsync(businessId, "analytics", cancellationToken);
        if (business is null) return Forbid();

        var owner = MarketingOwnerScope.Business(businessId);
        var profileService = ActivatorUtilities.CreateInstance<Infrastructure.WebsiteEditing.BusinessWebsiteProfileService>(HttpContext.RequestServices);
        var profile = await profileService.GetAsync(businessId, cancellationToken);

        var openAiConnections = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IOpenAiAdsAccountConnectionAuthority>();
        var openAiHealthService = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IOpenAiMeasurementHealthService>();
        var openAiDirect = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IOpenAiAdsDirectConnectionService>();
        var openAiConnection = await openAiConnections.GetAsync(owner, cancellationToken);
        var openAiHealth = await openAiHealthService.GetAsync(owner, cancellationToken);

        Infrastructure.Analytics.OpenAiAdsProviderAccountSnapshot? openAiProvider = null;
        string? openAiProviderError = null;
        if (openAiConnection.Connected)
        {
            try { openAiProvider = await openAiDirect.InspectAsync(owner, cancellationToken); }
            catch (Exception ex) when (ex is HttpRequestException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            { openAiProviderError = "Provider status could not be refreshed."; }
        }

        var db = HttpContext.RequestServices.GetRequiredService<Infrastructure.Data.MasterAppDbContext>();
        var paidTrafficEvents = await db.AnalyticsEvents.AsNoTracking()
            .CountAsync(x => x.CommerceBusinessId == businessId && x.Oppref != null && x.Oppref != "", cancellationToken);
        var leads = await db.WebsiteLeads.AsNoTracking()
            .CountAsync(x => x.CommerceBusinessId == businessId && x.Oppref != null && x.Oppref != "", cancellationToken);
        var crmLeadIds = db.WebsiteLeadIntakeLinks.AsNoTracking()
            .Where(x => x.CommerceBusinessId == businessId && x.Oppref != null && x.Oppref != "")
            .Select(x => x.WorkstationLeadId);
        var crm = await crmLeadIds.CountAsync(cancellationToken);
        var appointments = await db.LeadAppointments.AsNoTracking()
            .CountAsync(x => x.CommerceBusinessId == businessId && x.Oppref != null && x.Oppref != "", cancellationToken);
        var orders = await db.CommerceOrders.AsNoTracking()
            .CountAsync(x => x.CommerceBusinessId == businessId && x.Oppref != null && x.Oppref != "", cancellationToken);
        var productionRows = db.ProductionRecords.AsNoTracking()
            .Where(x => x.LeadId != null && crmLeadIds.Contains(x.LeadId) && x.Oppref != null && x.Oppref != "");
        var production = await productionRows.CountAsync(cancellationToken);
        var paidRevenue = await productionRows.Where(x => x.Status == ProductionStatus.Paid)
            .SumAsync(x => (decimal?)x.Amount, cancellationToken) ?? 0m;

        var accountReady =
            string.Equals(openAiProvider?.Status, "active", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(openAiProvider?.ReviewStatus, OpenAiAdsReviewStatuses.Approved, StringComparison.Ordinal) &&
            openAiConnection.PixelConfigured &&
            openAiConnection.ConversionsApiConfigured &&
            openAiHealth.Status == "ready";

        return Json(new
        {
            source = "canonical_business_marketing_setup",
            agentProfileId = (Guid?)null,
            agentName = business.DisplayName,
            status = new
            {
                publicReady = business.IsActive && string.Equals(business.Status, "Active", StringComparison.OrdinalIgnoreCase),
                metaCustomPixel = !string.IsNullOrWhiteSpace(profile.Settings.MetaPixelId),
                openAiReady = accountReady,
                bookingPersonalLive = profile.Settings.BookingEnabled &&
                    (!string.IsNullOrWhiteSpace(profile.Settings.BookingEmbedUrl) || !string.IsNullOrWhiteSpace(profile.Settings.BookingFallbackUrl)),
                calendarLinked = !string.IsNullOrWhiteSpace(profile.Settings.BookingCalendarEmail)
            },
            marketing = new
            {
                revision = profile.Settings.ConnectionRevision,
                metaPixelId = profile.Settings.MetaPixelId,
                metaTestEventCode = profile.Settings.MetaTestEventCode,
                metaTestEventsConfigured = !string.IsNullOrWhiteSpace(profile.Settings.MetaTestEventCode),
                metaAdsConnected = profile.AdsConnected,
                metaAccount = profile.ConnectedAccount,
                metaCapiConfiguredSecurely = profile.HasSecureCapiToken,
                metaCapiManagedAutomatically = true
            },
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
                    lastSentUtc = openAiHealth.LastSentUtc,
                    providerMonitoringAvailable = openAiHealth.ProviderMonitoringAvailable,
                    recentProviderEvents = openAiHealth.RecentProviderEvents
                }
            },
            attribution = new
            {
                source = "canonical_oppref_lineage",
                paidTrafficEvents,
                leads,
                crm,
                appointments,
                orders,
                production,
                paidRevenue,
                complete = paidTrafficEvents > 0 && leads > 0 && crm > 0 && (appointments > 0 || orders > 0 || production > 0)
            },
            booking = new
            {
                revision = profile.Settings.ProfileRevision,
                enabled = profile.Settings.BookingEnabled,
                microsoftBookingsEmbedUrl = profile.Settings.BookingEmbedUrl,
                fallbackBookingUrl = profile.Settings.BookingFallbackUrl,
                bookingPageIdOrMailbox = profile.Settings.BookingMailboxId,
                calendarEmail = profile.Settings.BookingCalendarEmail
            }
        });
    }

    [HttpPost("analytics/marketing-setup")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveMarketingSetup(
        Guid businessId,
        [FromBody] BusinessMarketingSetupUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();

        var profileService = ActivatorUtilities.CreateInstance<Infrastructure.WebsiteEditing.BusinessWebsiteProfileService>(HttpContext.RequestServices);
        try
        {
            await profileService.SaveAsync(businessId, new Infrastructure.WebsiteEditing.BusinessWebsiteProfileInput
            {
                ProfileRevision = request.ProfileRevision,
                ConnectionRevision = request.MarketingRevision,
                BookingEnabled = request.BookingEnabled,
                BookingEmbedUrl = request.MicrosoftBookingsEmbedUrl,
                BookingFallbackUrl = request.FallbackBookingUrl,
                BookingMailboxId = request.BookingPageIdOrMailbox,
                BookingCalendarEmail = request.CalendarEmail,
                MetaPixelId = request.MetaPixelId,
                MetaTestEventCode = request.MetaTestEventCode
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "Marketing Setup changed. Reload and try again." });
        }
        catch (ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        return await MarketingSetup(businessId, cancellationToken);
    }

    [HttpPost("analytics/openai-connect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConnectOpenAi(
        Guid businessId,
        [FromBody] BusinessOpenAiConnectRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var connector = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IOpenAiAdsDirectConnectionService>();
        try
        {
            await connector.ConnectAsync(MarketingOwnerScope.Business(businessId), request.AdvertiserApiKey, request.ExpectedRevision, cancellationToken);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        { return BadRequest(new { message = ex.Message }); }
        catch (DbUpdateConcurrencyException)
        { return Conflict(new { message = "ChatGPT Ads connection changed. Reload Marketing Setup and try again." }); }
        catch (HttpRequestException)
        { return StatusCode(StatusCodes.Status502BadGateway, new { message = "OpenAI Ads could not be verified right now." }); }

        return await MarketingSetup(businessId, cancellationToken);
    }

    [HttpPost("analytics/openai-refresh")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshOpenAi(
        Guid businessId,
        [FromBody] BusinessOpenAiRevisionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var connector = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IOpenAiAdsDirectConnectionService>();
        try
        {
            await connector.RefreshAsync(MarketingOwnerScope.Business(businessId), request.ConnectionRevision, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        { return Conflict(new { message = "ChatGPT Ads connection changed. Reload Marketing Setup and try again." }); }
        catch (InvalidOperationException ex)
        { return BadRequest(new { message = ex.Message }); }
        catch (HttpRequestException)
        { return StatusCode(StatusCodes.Status502BadGateway, new { message = "OpenAI Ads could not be refreshed right now." }); }

        return await MarketingSetup(businessId, cancellationToken);
    }

    [HttpPost("analytics/openai-disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisconnectOpenAi(
        Guid businessId,
        [FromBody] BusinessOpenAiRevisionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var authority = HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.IOpenAiAdsAccountConnectionAuthority>();
        try
        {
            await authority.DisconnectAsync(MarketingOwnerScope.Business(businessId), request.ConnectionRevision, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        { return Conflict(new { message = "ChatGPT Ads connection changed. Reload Marketing Setup and try again." }); }
        catch (InvalidOperationException ex)
        { return BadRequest(new { message = ex.Message }); }

        return await MarketingSetup(businessId, cancellationToken);
    }

    [HttpGet("analytics/{**section}")]
    public async Task<IActionResult> AnalyticsData(Guid businessId, string section, string? preset = null,
        DateTime? fromUtc = null, DateTime? toUtc = null, TrafficType trafficType = TrafficType.All,
        TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic, string? timezoneId = null,
        int? timezoneOffsetMinutes = null, string? metric = null, string? visitorId = null, string? sessionId = null,
        string? quoteType = null, string? campaign = null, string? pageMode = null, string? scoreTier = null,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "analytics", cancellationToken) is null) return Forbid();
        TimeRangeRequest range;
        try
        {
            var timezone = AnalyticsViewerTimeZoneResolver.Resolve(timezoneId, timezoneOffsetMinutes);
            range = TimeRangeRequest.FromPreset(preset ?? "30d", fromUtc, toUtc, timezone, qualityMode);
        }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { return BadRequest("Choose a valid time range and time zone."); }
        try
        {
            var result = await workspace.AnalyticsDataAsync(businessId, section, range, trafficType,
                metric, visitorId, sessionId, quoteType, campaign, pageMode, scoreTier, cancellationToken);
            return result is null ? NotFound() : Json(result);
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    [HttpGet("settings")]
    public async Task<IActionResult> Settings(Guid businessId, CancellationToken cancellationToken)
    {
        var business = await ResolveBusinessAsync(businessId, "settings", cancellationToken);
        if (business is null) return Forbid();
        return View("~/Views/Shared/BusinessWorkspaceSettings.cshtml", await workspace.CustomizeAsync(business, cancellationToken));
    }

    [HttpPost("settings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSettings(Guid businessId, BusinessWorkspaceSettingsInput input, CancellationToken cancellationToken)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        if (!ModelState.IsValid) return BadRequest(ModelState);
        try { await workspace.CustomizeAsync(businessId, input, cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict("Settings changed in another session. Reload before saving."); }
        catch (ValidationException ex) { return BadRequest(ex.Message); }
        return RedirectToAction(nameof(Settings), new { businessId });
    }
}
