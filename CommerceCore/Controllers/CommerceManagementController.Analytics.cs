using Microsoft.AspNetCore.Mvc;
using Legend.Commerce;
using Infrastructure.Analytics;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Services;
using Shared.Analytics;

namespace ParfaitApp.Controllers;

public sealed partial class CommerceManagementController
{
    private CommerceStoreContext? analyticsStore;
    private string analyticsTicket = "";

    [HttpGet("analytics/commerce")]
    public async Task<IActionResult> CommerceAnalytics(string ticket, string? preset = null,
        DateTime? fromUtc = null, DateTime? toUtc = null,
        TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic, string? timezoneId = null,
        int? timezoneOffsetMinutes = null, CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        TimeZoneInfo timezone;
        try { timezone = AnalyticsViewerTimeZoneResolver.Resolve(timezoneId, timezoneOffsetMinutes); }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { return BadRequest("Choose a valid time zone."); }
        var model = await internalAnalytics.GetDashboardAsync(store.CommerceBusinessId, ResolveAnalyticsScope(store),
            await ResolveMarketingOwnerAsync(store, ct), preset ?? "30d", fromUtc, toUtc, qualityMode,
            timezone, timezoneId, timezoneOffsetMinutes, ct);
        return Json(ParfaitInternalAnalyticsService.StoreReport(model));
    }

    [HttpGet("analytics/marketing-setup")]
    public async Task<IActionResult> MarketingSetup(string ticket, CancellationToken ct = default)
    {
        analyticsTicket = ticket;
        analyticsStore = await ResolveAsync(ticket, ct);
        if (analyticsStore is null) return Unauthorized();
        return await base.MarketingSetup(analyticsStore.CommerceBusinessId, ct);
    }

    [HttpPost("analytics/openai-connect"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ConnectOpenAi(string ticket, [FromBody] MarketingProviderApiKeyRequest request, CancellationToken ct = default)
    {
        analyticsTicket = ticket;
        analyticsStore = await ResolveAsync(ticket, ct);
        if (analyticsStore is null) return Unauthorized();
        return await base.ConnectOpenAi(analyticsStore.CommerceBusinessId, new(request.AdvertiserApiKey, request.ExpectedRevision), ct);
    }

    [HttpPost("analytics/openai-refresh"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshOpenAi(string ticket, [FromBody] MarketingProviderRevisionRequest request, CancellationToken ct = default)
    {
        analyticsTicket = ticket;
        analyticsStore = await ResolveAsync(ticket, ct);
        if (analyticsStore is null) return Unauthorized();
        return await base.RefreshOpenAi(analyticsStore.CommerceBusinessId, new(request.ConnectionRevision), ct);
    }

    [HttpPost("analytics/openai-disconnect"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DisconnectOpenAi(string ticket, [FromBody] MarketingProviderRevisionRequest request, CancellationToken ct = default)
    {
        analyticsTicket = ticket;
        analyticsStore = await ResolveAsync(ticket, ct);
        if (analyticsStore is null) return Unauthorized();
        return await base.DisconnectOpenAi(analyticsStore.CommerceBusinessId, new(request.ConnectionRevision), ct);
    }

    protected override async Task<CommerceBusiness?> ResolveBusinessAsync(Guid id, string capability, CancellationToken ct)
    {
        if (analyticsStore is null || id != analyticsStore.CommerceBusinessId ||
            await ResolveAsync(analyticsTicket, ct, capability) is not { } authorized || authorized.CommerceBusinessId != id)
            return null;
        return await HttpContext.RequestServices.GetRequiredService<MasterAppDbContext>().CommerceBusinesses
            .AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    }

    protected override ScopeContext ResolveAnalyticsScope(Guid businessId) =>
        analyticsStore?.CommerceBusinessId == businessId ? ResolveAnalyticsScope(analyticsStore)
            : throw new InvalidOperationException("Store scope is unavailable.");

    protected override Task<MarketingOwnerScope> ResolveAnalyticsMarketingOwnerAsync(Guid businessId, CancellationToken ct) =>
        analyticsStore?.CommerceBusinessId == businessId ? ResolveMarketingOwnerAsync(analyticsStore, ct)
            : throw new InvalidOperationException("Store owner is unavailable.");

    protected override string AnalyticsExternalReturnUrl(string target, string provider, string status, string? reason = null)
    {
        // target has already passed the Store continuation's destination and membership validation.
        // Generate only the provider's safe status keys; never relax its local-return validator.
        var suffix = MarketingExternalAdsOAuthService.BuildReturnUrl("/", provider, status, reason);
        var parameters = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri("https://local" + suffix).Query);
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(target,
            parameters.Select(pair => new KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>(pair.Key, pair.Value)));
    }

    protected override string AnalyticsCallbackUri(string callback)
    {
        // The private adapter is routable on both the transport origin and verified
        // custom domains; public CMS publication/cutover never admits callbacks.
        var path = CommerceManagementCapabilityPolicy.InternalRoutes.Single(x =>
            x.Method == "GET" && x.ActionPath == $"/commerce/manage/analytics/{callback}").InternalPath;
        return $"{Request.Scheme}://{Request.Host}{Request.PathBase}{path}";
    }

    protected override async Task<string> AnalyticsProviderReturnAsync(Guid businessId, string? returnUrl, string provider, CancellationToken ct)
    {
        var target = LocalManagerReturnUrl(returnUrl, analyticsTicket);
        return HttpContext.Items.ContainsKey("LegendStoreSession")
            ? Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(target, "storeContinuation",
                await ProviderContinuationAsync(analyticsTicket, "settings", provider == "calendar" ? 20 : 10, ct)) : target;
    }

    protected override async Task<(Guid BusinessId, string ReturnUrl)?> ResolveAnalyticsCallbackAsync(
        MarketingOwnerScope owner, string returnUrl, CancellationToken ct)
    {
        if (!returnUrl.StartsWith('/') || returnUrl.StartsWith("//")) return null;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri("https://local" + returnUrl).Query);
        var continuation = query.TryGetValue("storeContinuation", out var token)
            ? await ResolveProviderContinuationAsync(token.ToString(), "settings", "/internal/analytics", ct) : null;
        var ticket = TicketFromReturnUrl(returnUrl);
        // Invalid explicit continuation cannot fall back to another authentication path.
        if (query.ContainsKey("storeContinuation") && continuation is null) return null;
        var store = continuation?.Store ?? (string.IsNullOrWhiteSpace(ticket) ? null : await ResolveAsync(ticket, ct, "settings"));
        if (store is null || await ResolveMarketingOwnerAsync(store, ct) != owner) return null;
        return (store.CommerceBusinessId, continuation?.ReturnUrl ?? LocalManagerReturnUrl(returnUrl, ticket!));
    }

    [Microsoft.AspNetCore.Authorization.AllowAnonymous, HttpGet("analytics/external-ads/callback")]
    public new Task<IActionResult> ExternalAdsCallback(string? code = null, [FromQuery(Name = "auth_code")] string? authCode = null,
        string? state = null, string? error = null, [FromQuery(Name = "error_description")] string? errorDescription = null,
        CancellationToken cancellationToken = default) =>
        base.ExternalAdsCallback(code, authCode, state, error, errorDescription, cancellationToken);

    [Microsoft.AspNetCore.Authorization.AllowAnonymous, HttpGet("analytics/calendar-callback")]
    public new Task<IActionResult> CalendarCallback(string? code = null, string? state = null, string? error = null,
        [FromQuery(Name = "error_description")] string? errorDescription = null, CancellationToken cancellationToken = default) =>
        base.CalendarCallback(code, state, error, errorDescription, cancellationToken);

    private async Task<AgentTrackingProfile> MarketingTrackingAsync(MarketingOwnerScope owner, CancellationToken ct)
    {
        var resolver = new AgentTrackingResolver(HttpContext.RequestServices.GetRequiredService<MasterAppDbContext>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentTrackingResolver>.Instance);
        var resolved = await ProtectWebsiteOwnerResolver.ResolveLeadAsync(resolver, configuration["Founder:Upn"],
            analyticsStore?.AgentTrackingProfileId, null, owner == MarketingOwnerScope.Founder, ct);
        if (resolved is null || (resolved.IsFounder ? MarketingOwnerScope.Founder : MarketingOwnerScope.Agent(resolved.Profile.Id)) != owner)
            throw new InvalidOperationException("The website marketing profile could not be verified.");
        return resolved.Profile;
    }

    protected override async Task<Infrastructure.WebsiteEditing.BusinessWebsiteProfileInput> ReadMarketingProfileAsync(Guid businessId, CancellationToken ct)
    {
        var owner = await ResolveAnalyticsMarketingOwnerAsync(businessId, ct);
        if (owner.CommerceBusinessId.HasValue) return await base.ReadMarketingProfileAsync(businessId, ct);
        var tracking = await MarketingTrackingAsync(owner, ct);
        var service = HttpContext.RequestServices.GetRequiredService<AgentMarketingProfileService>();
        var profile = await service.ResolveProfileAsync(tracking, false, ct);
        var settings = await service.GetAsync(tracking, owner, ct);
        return new()
        {
            ConnectionRevision = settings.Revision, MetaPixelId = settings.PixelId, MetaTestEventCode = settings.TestEventCode,
            BookingEnabled = profile?.BookingEnabled == true, BookingEmbedUrl = profile?.MicrosoftBookingsEmbedUrl,
            BookingFallbackUrl = profile?.FallbackBookingUrl, BookingMailboxId = profile?.BookingPageIdOrMailbox,
            BookingCalendarEmail = profile?.CalendarEmail
        };
    }

    protected override async Task SaveMarketingProfileAsync(Guid businessId, Infrastructure.WebsiteEditing.BusinessWebsiteProfileInput input, CancellationToken ct)
    {
        var owner = await ResolveAnalyticsMarketingOwnerAsync(businessId, ct);
        if (owner.CommerceBusinessId.HasValue) { await base.SaveMarketingProfileAsync(businessId, input, ct); return; }
        await HttpContext.RequestServices.GetRequiredService<AgentMarketingProfileService>()
            .SaveMarketingAsync(await MarketingTrackingAsync(owner, ct), owner, input, ct);
    }

    protected override void ConfigureAnalyticsView()
    {
        ViewData["AnalyticsMode"] = "store";
        ViewData["AnalyticsLayout"] = "~/Views/Shared/_InternalLayout.cshtml";
        ViewData["Title"] = "Store Analytics";
        ViewData["PersonalLink"] = analyticsStore is null ? null :
            $"{Request.Scheme}://{Request.Host}{Request.PathBase}{analyticsStore.StoreRootPath}";
    }

    protected override string AnalyticsActorId() =>
        tickets.TryUnprotect(analyticsTicket)?.ActorUserId
            ?? throw new InvalidOperationException("The authenticated Store actor is unavailable.");

    protected override string AnalyticsRoute(Guid businessId) =>
        Legend.Commerce.CommerceManagementCapabilityPolicy.ManagementUrl(HttpContext, "/commerce/manage/analytics",
            Request.Query["ticket"].ToString());
}
