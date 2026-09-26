using Infrastructure.Analytics;
using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Analytics;

public class WebsiteTrackingProxyAuthority : ControllerBase
{
    private static readonly JsonSerializerOptions JsonPascalCase = new()
    {
        PropertyNamingPolicy = null
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger _logger;
    private readonly Infrastructure.Analytics.AgentTrackingResolver _resolver;
    private readonly string _founderUpn;

    public WebsiteTrackingProxyAuthority(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger logger,
        Infrastructure.Analytics.AgentTrackingResolver resolver)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
        _resolver = resolver;
        _founderUpn = config["Founder:Upn"] ?? throw new InvalidOperationException("Founder:Upn configuration is required");
    }

    [HttpPost]
    [Route("api/tracking/ingest")]
    [Route("api/analytics/ingest")] // Compat alias for older tracking.js builds.
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(32 * 1024)]
    public async Task<IActionResult> Ingest([FromBody] AnalyticsEventRequest req, CancellationToken ct)
    {
        if (req.ClientEventId == Guid.Empty)
            return BadRequest(new { error = "client_event_id_required" });

        if (string.IsNullOrWhiteSpace(req.EventType))
            return BadRequest(new { error = "event_type_required" });

        if (req.SiteKey is WebsiteEditorSiteKeys.Legend or WebsiteEditorSiteKeys.Business)
        {
            var scopeResolver = HttpContext.RequestServices.GetService<PublicWebsiteRuntimeScopeResolver>();
            if (scopeResolver is null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "website_runtime_scope_unavailable" });
            var scope = await scopeResolver.ResolveAsync(HttpContext, req.SiteKey, ct);
            if (scope is null || !PublicWebsiteRuntimeScopeResolver.IsPublishedPath(scope, req.Path))
                return BadRequest(new { error = "published_website_scope_required" });
            return await PersistPublicWebsiteEventAsync(req, scope, ct);
        }

        EnsureClientContextFallback(req);
        var isFounderOwner = await EnsureAgentAttributionAsync(req, ct);

        // Protect telemetry writes directly through the shared canonical database
        // authority. Do not depend on a second application/network hop merely to
        // record browser analytics.
        return await PersistProtectEventAsync(req, isFounderOwner, ct);
    }

    [HttpPost]
    [Route("api/lead/submit")]
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> SubmitLead([FromBody] LeadSubmitRequest req, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid();
        EnsureLeadContextFallback(req);

        var attribution = await EnsureLeadAttributionAsync(req, ct);
        if (!attribution.Succeeded)
        {
            _logger.LogWarning(
                "LeadProxy [{CorrelationId}]: attribution rejected reason={Reason} SourcePath={SourcePath} PayloadSlug={Slug} PayloadProfileId={ProfileId}",
                correlationId, attribution.Error, req.SourcePath, req.AgentSlug, req.AgentTrackingProfileId);
            return BadRequest(new { error = attribution.Error ?? "lead_attribution_invalid", correlationId });
        }

        _logger.LogInformation(
            "LeadProxy [{CorrelationId}]: request received InterestType={InterestType} SourcePageKey={SourcePageKey} AgentSlug={Slug} ProfileId={ProfileId} Host={Host}",
            correlationId, req.InterestType, req.SourcePageKey, req.AgentSlug, req.AgentTrackingProfileId, req.Host);

        var response = await ForwardAsync("/api/lead/submit", req, ct, correlationId);

        if (response == null)
        {
            _logger.LogError(
                "LeadProxy [{CorrelationId}]: forward failed — no response from AgentPortal (proxy configuration or connectivity issue)",
                correlationId);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "lead_forward_failed",
                captured = false,
                notificationSent = false,
                correlationId
            });
        }

        _logger.LogInformation(
            "LeadProxy [{CorrelationId}]: downstream AgentPortal responded {StatusCode}",
            correlationId, (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "LeadProxy [{CorrelationId}]: downstream non-success {StatusCode} for InterestType={InterestType}",
                correlationId, (int)response.StatusCode, req.InterestType);
        }

        return await BuildPassThroughResultAsync(response, ct);
    }

    private async Task<IActionResult> PersistProtectEventAsync(
        AnalyticsEventRequest req,
        bool isFounderOwner,
        CancellationToken cancellationToken)
    {
        if (!AnalyticsEventCatalog.TryGet(req.EventType, out var definition) || !definition.AllowBrowser)
            return BadRequest(new { error = "invalid_event_type" });

        if (!req.AgentTrackingProfileId.HasValue || req.AgentTrackingProfileId == Guid.Empty)
            return BadRequest(new { error = "tracking_owner_required" });

        var db = HttpContext.RequestServices.GetRequiredService<MasterAppDbContext>();
        var existing = await db.AnalyticsEvents.AsNoTracking()
            .FirstOrDefaultAsync(row => row.ClientEventId == req.ClientEventId, cancellationToken);
        if (existing is not null)
        {
            var sameOwner =
                existing.CommerceBusinessId == null &&
                existing.AgentTrackingProfileId == req.AgentTrackingProfileId &&
                string.Equals(existing.EventType, req.EventType, StringComparison.OrdinalIgnoreCase);
            return sameOwner ? Ok(new { status = "duplicate_ignored" }) : Conflict(new { error = "event_id_owner_conflict" });
        }

        var context = new UnifiedEventContext
        {
            SiteKey = WebsiteEditorSiteKeys.Protect,
            AgentTrackingProfileId = req.AgentTrackingProfileId,
            AgentSlug = Clean(req.AgentSlug),
            EventId = req.ClientEventId.ToString("N"),
            EventName = req.EventType.Trim(),
            EventCategory = definition.Category,
            EventUtc = req.EventUtc ?? DateTime.UtcNow,
            SessionId = Clean(req.SessionId),
            VisitorId = Clean(req.VisitorId),
            Url = Clean(req.Url),
            Referrer = Clean(req.Referrer),
            PageKey = Clean(req.PageKey),
            ElementKey = Clean(req.ElementKey),
            ButtonLabel = Clean(req.ButtonLabel),
            FormKey = Clean(req.FormKey),
            QuoteType = Clean(req.QuoteType),
            DeviceType = Clean(req.DeviceType),
            Browser = Clean(req.Browser),
            OperatingSystem = Clean(req.OperatingSystem),
            UserAgent = Clean(req.UserAgent) ?? Request.Headers.UserAgent.ToString(),
            IpAddress = Clean(req.IpAddress) ?? ResolveClientIp(),
            TimeZone = Clean(req.TimeZone),
            Language = Clean(req.Language),
            WebDriver = req.WebDriver,
            IsHeadless = req.IsHeadless,
            MouseMoveCount = req.MouseMoveCount,
            HumanInteractionCount = req.HumanInteractionCount,
            VisibilityChangeCount = req.VisibilityChangeCount,
            ScreenWidth = req.ScreenWidth,
            ScreenHeight = req.ScreenHeight,
            ViewportWidth = req.ViewportWidth,
            ViewportHeight = req.ViewportHeight,
            ScrollPercent = req.ScrollPercent,
            DwellMilliseconds = req.DwellMilliseconds,
            EngagedMilliseconds = req.EngagedMilliseconds,
            IsBounceCandidate = req.IsBounceCandidate,
            IsExitPage = req.IsExitPage,
            UtmSource = Clean(req.UtmSource),
            UtmMedium = Clean(req.UtmMedium),
            UtmCampaign = Clean(req.UtmCampaign),
            UtmId = Clean(req.UtmId),
            UtmContent = Clean(req.UtmContent),
            Fbclid = Clean(req.Fbclid),
            MetaCampaignId = Clean(req.MetaCampaignId),
            MetaAdSetId = Clean(req.MetaAdSetId),
            MetaAdId = Clean(req.MetaAdId),
            IsInternal = req.IsInternal,
            Environment = EnvironmentLabelResolver.Resolve(),
            Host = Request.Host.Host,
            IsBrowserSignal = true,
            IsServerAuthority = false,
            MetaServerAuthorityEligible = false,
            Metadata = new
            {
                source = "protect_shared_tracking",
                siteKey = WebsiteEditorSiteKeys.Protect,
                reportingOwner = isFounderOwner ? "founder" : "agent",
                analyticsMetadata = Clean(req.MetadataJson)
            }
        };

        var row = UnifiedEventMapper.ToAnalytics(context);
        row.ClientEventId = req.ClientEventId;
        row.SchemaVersion = req.SchemaVersion ?? 1;
        row.TrackingVersion = Clean(req.TrackingVersion);
        row.Path = Clean(req.Path);
        row.SubmitOutcome = Clean(req.SubmitOutcome);
        row.UtmTerm = Clean(req.UtmTerm);
        row.MetaCampaignName = Clean(req.MetaCampaignName);
        row.MetaAdSetName = Clean(req.MetaAdSetName);
        row.MetaAdName = Clean(req.MetaAdName);
        row.Placement = Clean(req.Placement);
        row.FormId = Clean(req.FormId);
        row.FieldName = Clean(req.FieldName);
        row.ElementId = Clean(req.ElementId);
        UnifiedAnalyticsWriter.Write(db, row);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            existing = await db.AnalyticsEvents.AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.ClientEventId == req.ClientEventId, cancellationToken);
            if (existing is null) throw;
            if (existing.CommerceBusinessId != null ||
                existing.AgentTrackingProfileId != req.AgentTrackingProfileId)
                return Conflict(new { error = "event_id_owner_conflict" });
        }

        return Ok(new { status = "ok", eventId = row.EventId });
    }

    private async Task<IActionResult> PersistPublicWebsiteEventAsync(
        AnalyticsEventRequest req,
        PublicWebsiteRuntimeScope scope,
        CancellationToken cancellationToken)
    {
        if (!AnalyticsEventCatalog.TryGet(req.EventType, out var definition) || !definition.AllowBrowser)
            return BadRequest(new { error = "invalid_event_type" });

        var db = HttpContext.RequestServices.GetRequiredService<MasterAppDbContext>();
        var existing = await db.AnalyticsEvents.AsNoTracking()
            .FirstOrDefaultAsync(row => row.ClientEventId == req.ClientEventId, cancellationToken);
        if (existing is not null)
        {
            var sameOwner = existing.CommerceBusinessId == scope.CommerceBusinessId &&
                existing.WebsiteContentVersionId == scope.PublishedVersion?.Id &&
                string.Equals(existing.EventType, req.EventType, StringComparison.OrdinalIgnoreCase);
            return sameOwner ? Ok(new { status = "duplicate_ignored" }) : Conflict(new { error = "event_id_owner_conflict" });
        }

        var context = new UnifiedEventContext
        {
            SiteKey = scope.SiteKey,
            CommerceBusinessId = scope.CommerceBusinessId,
            WebsiteContentVersionId = scope.PublishedVersion?.Id,
            WebsiteBindingId = string.IsNullOrWhiteSpace(req.WebsiteBindingId) ? null : req.WebsiteBindingId.Trim(),
            EventId = req.ClientEventId.ToString("N"),
            EventName = req.EventType.Trim(),
            EventCategory = definition.Category,
            EventUtc = req.EventUtc ?? DateTime.UtcNow,
            SessionId = Clean(req.SessionId),
            VisitorId = Clean(req.VisitorId),
            Url = Clean(req.Url),
            Referrer = Clean(req.Referrer),
            PageKey = Clean(req.PageKey),
            ElementKey = Clean(req.ElementKey),
            ButtonLabel = Clean(req.ButtonLabel),
            FormKey = Clean(req.FormKey),
            QuoteType = Clean(req.QuoteType),
            DeviceType = Clean(req.DeviceType),
            Browser = Clean(req.Browser),
            OperatingSystem = Clean(req.OperatingSystem),
            UserAgent = Clean(req.UserAgent) ?? Request.Headers.UserAgent.ToString(),
            IpAddress = Request.HttpContext.Connection.RemoteIpAddress?.ToString(),
            TimeZone = Clean(req.TimeZone),
            Language = Clean(req.Language),
            WebDriver = req.WebDriver,
            IsHeadless = req.IsHeadless,
            MouseMoveCount = req.MouseMoveCount,
            HumanInteractionCount = req.HumanInteractionCount,
            VisibilityChangeCount = req.VisibilityChangeCount,
            ScreenWidth = req.ScreenWidth,
            ScreenHeight = req.ScreenHeight,
            ViewportWidth = req.ViewportWidth,
            ViewportHeight = req.ViewportHeight,
            ScrollPercent = req.ScrollPercent,
            DwellMilliseconds = req.DwellMilliseconds,
            EngagedMilliseconds = req.EngagedMilliseconds,
            IsBounceCandidate = req.IsBounceCandidate,
            IsExitPage = req.IsExitPage,
            UtmSource = Clean(req.UtmSource),
            UtmMedium = Clean(req.UtmMedium),
            UtmCampaign = Clean(req.UtmCampaign),
            UtmId = Clean(req.UtmId),
            UtmContent = Clean(req.UtmContent),
            Fbclid = Clean(req.Fbclid),
            MetaCampaignId = Clean(req.MetaCampaignId),
            MetaAdSetId = Clean(req.MetaAdSetId),
            MetaAdId = Clean(req.MetaAdId),
            IsInternal = false,
            Environment = EnvironmentLabelResolver.Resolve(),
            Host = scope.OriginHost,
            IsBrowserSignal = true,
            IsServerAuthority = false,
            MetaServerAuthorityEligible = false,
            Metadata = new
            {
                Source = "public_website_shared_tracking",
                Scope = scope.SiteKey,
                WebsiteBindingId = Clean(req.WebsiteBindingId),
                AnalyticsMetadata = Clean(req.MetadataJson)
            }
        };

        var row = UnifiedEventMapper.ToAnalytics(context);
        row.ClientEventId = req.ClientEventId;
        row.SchemaVersion = req.SchemaVersion ?? 1;
        row.TrackingVersion = Clean(req.TrackingVersion);
        row.Path = Clean(req.Path);
        row.SubmitOutcome = Clean(req.SubmitOutcome);
        row.UtmTerm = Clean(req.UtmTerm);
        row.MetaCampaignName = Clean(req.MetaCampaignName);
        row.MetaAdSetName = Clean(req.MetaAdSetName);
        row.MetaAdName = Clean(req.MetaAdName);
        row.Placement = Clean(req.Placement);
        row.FormId = Clean(req.FormId);
        row.FieldName = Clean(req.FieldName);
        row.ElementId = Clean(req.ElementId);
        UnifiedAnalyticsWriter.Write(db, row);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            existing = await db.AnalyticsEvents.AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.ClientEventId == req.ClientEventId, cancellationToken);
            if (existing is null) throw;
            if (existing.CommerceBusinessId != scope.CommerceBusinessId ||
                existing.WebsiteContentVersionId != scope.PublishedVersion?.Id)
                return Conflict(new { error = "event_id_owner_conflict" });
        }

        return Ok(new { status = "ok", eventId = row.EventId });
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void EnsureLeadContextFallback(LeadSubmitRequest req)
    {
        req.Host = FirstNonBlank(req.Host, Request.Host.Value);
        req.SourcePath = FirstNonBlank(ResolveLeadSourcePathFromReferrer(), req.SourcePath);

        if (string.IsNullOrWhiteSpace(req.Environment))
        {
            req.Environment = _config["ASPNETCORE_ENVIRONMENT"]
                ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                ?? "Production";
        }
    }

    private async Task<(bool Succeeded, string? Error)> EnsureLeadAttributionAsync(LeadSubmitRequest req, CancellationToken ct)
    {
        // The browser POST goes to /api/lead/submit, so scoped route middleware no longer
        // has /a/{slug}. Recover that scope from the same-origin referrer/source path and
        // validate it against the existing tracking-profile authority before forwarding.
        var sourceSlug = ExtractAgentSlug(req.SourcePath);
        if (!string.IsNullOrWhiteSpace(sourceSlug))
        {
            var bySourcePath = await _resolver.ResolveBySlugAsync(sourceSlug, ct);
            if (!bySourcePath.Found || bySourcePath.Profile == null)
            {
                return (false, "invalid_agent_scope");
            }

            req.AgentTrackingProfileId = bySourcePath.Profile.Id;
            req.AgentSlug = bySourcePath.CanonicalSlug ?? bySourcePath.Profile.Slug;
            return (true, null);
        }

        if (!string.IsNullOrWhiteSpace(req.AgentSlug))
        {
            var bySlug = await _resolver.ResolveBySlugAsync(req.AgentSlug.Trim(), ct);
            if (!bySlug.Found || bySlug.Profile == null)
            {
                return (false, "invalid_agent_slug");
            }

            req.AgentTrackingProfileId = bySlug.Profile.Id;
            req.AgentSlug = bySlug.CanonicalSlug ?? bySlug.Profile.Slug;
            return (true, null);
        }

        if (req.AgentTrackingProfileId.HasValue)
        {
            var byId = await _resolver.ResolveByIdAsync(req.AgentTrackingProfileId.Value, ct);
            if (!byId.Found || byId.Profile == null)
            {
                return (false, "invalid_agent_profile");
            }

            req.AgentTrackingProfileId = byId.Profile.Id;
            req.AgentSlug = byId.CanonicalSlug ?? byId.Profile.Slug;
            return (true, null);
        }

        // Root-domain Home belongs to Founder. Preserve the existing founder fallback,
        // but resolve it here so the central lead endpoint receives explicit attribution.
        var founder = await _resolver.ResolveByUpnAsync(_founderUpn, ct);
        if (founder.Found && founder.Profile != null)
        {
            req.AgentTrackingProfileId = founder.Profile.Id;
            req.AgentSlug = founder.CanonicalSlug ?? founder.Profile.Slug;
        }

        return (true, null);
    }

    private string? ResolveLeadSourcePathFromReferrer()
    {
        var raw = Request.Headers["Referer"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            if (!string.Equals(uri.Host, Request.Host.Host, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return uri.AbsolutePath;
        }

        return raw.Trim();
    }

    private static string? ExtractAgentSlug(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;

        var path = sourcePath.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute))
        {
            path = absolute.AbsolutePath;
        }

        path = path.Split('?', '#')[0];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2 || !string.Equals(segments[0], "a", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Uri.UnescapeDataString(segments[1]);
    }

    private void EnsureClientContextFallback(AnalyticsEventRequest req)
    {
        var unified = UnifiedEventContextBuilder.Build(HttpContext);

        req.UserAgent = FirstMeaningful(req.UserAgent, unified.UserAgent, Request.Headers.UserAgent.ToString());
        req.IpAddress = FirstMeaningful(req.IpAddress, unified.IpAddress, ResolveClientIp());
        req.DeviceType = FirstMeaningful(req.DeviceType, unified.DeviceType);
        req.Browser = FirstMeaningful(req.Browser, unified.Browser);
        req.OperatingSystem = FirstMeaningful(req.OperatingSystem, unified.OperatingSystem);
        req.Language = FirstNonBlank(req.Language, unified.Language, NormalizeAcceptLanguage(Request.Headers.AcceptLanguage.ToString()));
        req.Host = FirstNonBlank(req.Host, Request.Host.Value);

        if (!req.EventUtc.HasValue)
        {
            req.EventUtc = DateTime.UtcNow;
        }

        if (string.IsNullOrWhiteSpace(req.Environment))
        {
            req.Environment = _config["ASPNETCORE_ENVIRONMENT"]
                ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                ?? "Production";
        }
    }

    private string? ResolveClientIp()
    {
        var forwardedFor = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            var first = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(first))
            {
                return first;
            }
        }

        var azureClientIp = Request.Headers["X-Azure-ClientIP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(azureClientIp))
        {
            return azureClientIp.Trim();
        }

        return Request.HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? FirstMeaningful(params string?[] values)
    {
        foreach (var value in values)
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed) &&
                !string.Equals(trimmed, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
        }

        return FirstNonBlank(values);
    }

    private static string? NormalizeAcceptLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var first = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(first)) return null;

        var semi = first.IndexOf(';');
        return semi >= 0 ? first[..semi].Trim() : first.Trim();
    }

    private static (string DeviceType, string Browser, string OperatingSystem) ParseUserAgent(string? userAgent)
    {
        var ua = userAgent ?? string.Empty;

        var deviceType = "desktop";
        if (ContainsAny(ua, "Mobi", "Android", "iPhone", "iPod")) deviceType = "mobile";
        else if (ContainsAny(ua, "iPad", "Tablet")) deviceType = "tablet";

        var browser = "unknown";
        if (ContainsAny(ua, "Edg/", "EdgiOS", "EdgA")) browser = "edge";
        else if (ContainsAny(ua, "CriOS", "Chrome/", "Chromium/")) browser = "chrome";
        else if (ContainsAny(ua, "FxiOS", "Firefox/")) browser = "firefox";
        else if (ContainsAny(ua, "FBAN", "FBAV", "Instagram")) browser = "in_app";
        else if (ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase)) browser = "safari";

        var os = "unknown";
        if (ua.Contains("Windows", StringComparison.OrdinalIgnoreCase)) os = "windows";
        else if (ContainsAny(ua, "Android")) os = "android";
        else if (ContainsAny(ua, "iPhone", "iPad", "iPod")) os = "ios";
        else if (ContainsAny(ua, "Mac OS X", "Macintosh")) os = "macos";
        else if (ua.Contains("Linux", StringComparison.OrdinalIgnoreCase)) os = "linux";

        return (deviceType, browser, os);
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<HttpResponseMessage?> ForwardAsync(string path, object payload, CancellationToken ct, Guid? callerCorrelationId = null)
    {
        var portalBase = (_config["Tracking:ApiBase"] ?? Environment.GetEnvironmentVariable("TRACKING_API_BASE") ?? string.Empty).Trim();
        var sharedSecret = Shared.Analytics.AnalyticsIngestConfiguration.ResolveSecret(key => _config[key]);

        if (string.IsNullOrWhiteSpace(portalBase) || string.IsNullOrWhiteSpace(sharedSecret))
        {
            _logger.LogError("Tracking proxy configuration missing. Ensure Tracking:ApiBase and Tracking:SharedSecret are configured.");
            return null;
        }

        var client = _httpClientFactory.CreateClient();
        Exception? lastError = null;
        var isDevelopment = string.Equals(
            _config["ASPNETCORE_ENVIRONMENT"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase);

        var allCandidates = BuildForwardBaseCandidates(portalBase, isDevelopment).ToList();
        var candidates = allCandidates
            .Where(baseUrl =>
            {
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return false;
                var isLocalHost = IsLocalHost(uri.Host);
                return isDevelopment ? isLocalHost : !isLocalHost;
            })
            .ToList();

        if (candidates.Count == 0)
        {
            _logger.LogError(
                "Tracking proxy target rejected by environment isolation. Environment={Environment}; ConfiguredApiBase={ApiBase}; Candidates={Candidates}",
                isDevelopment ? "Development" : "NonDevelopment",
                portalBase,
                string.Join(",", allCandidates));
            return null;
        }

        foreach (var baseUrl in candidates)
        {
            var target = $"{baseUrl}{path}";
            try
            {
                // Use callerCorrelationId if provided so X-Request-Id matches the caller's log context
                var requestId = callerCorrelationId ?? Guid.NewGuid();
                var timestamp = DateTimeOffset.UtcNow;

                _logger.LogInformation("Tracking proxy forwarding to {Target} requestId={RequestId}", target, requestId);

                using var request = new HttpRequestMessage(HttpMethod.Post, target);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Add("X-Shared-Secret", sharedSecret);
                request.Headers.Add("X-Request-Id", requestId.ToString("D"));
                request.Headers.Add("X-Timestamp", timestamp.ToString("O"));
                request.Headers.Add("X-Signature", ComputeSignature(sharedSecret, requestId, timestamp));
                request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonPascalCase), Encoding.UTF8, "application/json");

                return await client.SendAsync(request, ct);
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(ex, "Tracking proxy forward attempt failed to {Target}", target);
            }
        }

        _logger.LogError(lastError, "Tracking proxy could not reach portal ingest endpoint. ConfiguredApiBase={ApiBase}", portalBase);
        return null;
    }

    private static string ComputeSignature(string secret, Guid requestId, DateTimeOffset timestamp)
    {
        var payload = $"{requestId:D}:{timestamp:O}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    private static async Task<IActionResult> BuildPassThroughResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        var body = await response.Content.ReadAsStringAsync(ct);
        return new ContentResult
        {
            StatusCode = (int)response.StatusCode,
            ContentType = contentType,
            Content = body
        };
    }

    private static IEnumerable<string> BuildForwardBaseCandidates(string configuredBase, bool includeLocalDevFallbacks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var normalized = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                seen.Add(normalized);
            }
        }

        Add(configuredBase);

        if (Uri.TryCreate(configuredBase, UriKind.Absolute, out var configuredUri))
        {
            var host = configuredUri.Host;
            var isLocalHost =
                string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);

            if (isLocalHost)
            {
                if (string.Equals(configuredUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    var httpFallback = new UriBuilder(configuredUri)
                    {
                        Scheme = Uri.UriSchemeHttp,
                        Port = configuredUri.Port == 6205 ? 6206 : configuredUri.Port
                    };
                    Add(httpFallback.Uri.ToString());
                }
                else if (string.Equals(configuredUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                {
                    var httpsFallback = new UriBuilder(configuredUri)
                    {
                        Scheme = Uri.UriSchemeHttps,
                        Port = configuredUri.Port == 6206 ? 6205 : configuredUri.Port
                    };
                    Add(httpsFallback.Uri.ToString());
                }

            }
        }

        // Local dev defaults are added in Development regardless of configured API base
        // so stale shell overrides do not break local tracking proxy forwarding.
        if (includeLocalDevFallbacks)
        {
            Add("http://localhost:6206");
            Add("https://localhost:6205");
        }

        return seen;
    }

    private static bool IsLocalHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ensures analytics events carry attribution even if client-side globals are missing.
    /// Priority:
    /// 1) Existing explicit payload values
    /// 2) Slug parsed from /a/{slug}/... path
    /// 3) Founder fallback for default-domain pages
    /// </summary>
    private async Task<bool> EnsureAgentAttributionAsync(AnalyticsEventRequest req, CancellationToken ct)
    {
        // Analytics ownership is server-authoritative. Ignore browser-supplied owner
        // IDs/slugs and derive the scope from the same-origin public page/referrer.
        req.AgentTrackingProfileId = null;
        req.AgentSlug = null;

        var sourcePath = ResolveLeadSourcePathFromReferrer();
        var sourceSlug = ExtractAgentSlug(sourcePath) ?? ExtractAgentSlug(req.Path);
        if (!string.IsNullOrWhiteSpace(sourceSlug))
        {
            var bySlug = await _resolver.ResolveBySlugAsync(sourceSlug, ct);
            if (bySlug.Found && bySlug.Profile != null &&
                !string.Equals(bySlug.Profile.AgentUpn, _founderUpn, StringComparison.OrdinalIgnoreCase))
            {
                req.AgentTrackingProfileId = bySlug.Profile.Id;
                req.AgentSlug = bySlug.CanonicalSlug ?? bySlug.Profile.Slug;
                return false;
            }
        }

        // The canonical root Protect site belongs to the Founder owner. The actual
        // Founder profile is resolved dynamically; no user/profile ID is hardcoded.
        var founder = await _resolver.ResolveByUpnAsync(_founderUpn, ct);
        if (founder.Found && founder.Profile != null)
        {
            req.AgentTrackingProfileId = founder.Profile.Id;
            req.AgentSlug = founder.CanonicalSlug ?? founder.Profile.Slug;
            return true;
        }

        return false;
    }

    public sealed class AnalyticsEventRequest
    {
        public int? SchemaVersion { get; set; }
        public string? TrackingVersion { get; set; }
        public string? SiteKey { get; set; }
        public string? WebsiteBindingId { get; set; }

        [Required] public Guid ClientEventId { get; set; }
        [Required] public string EventType { get; set; } = string.Empty;
        public string? PageKey { get; set; }
        public string? SectionKey { get; set; }
        public string? ElementKey { get; set; }
        public string? ButtonLabel { get; set; }
        public string? FormKey { get; set; }
        public string? QuoteType { get; set; }
        public string? Url { get; set; }
        public string? Path { get; set; }
        public string? Referrer { get; set; }
        public string? SessionId { get; set; }
        public string? VisitorId { get; set; }
        public string? UtmSource { get; set; }
        public string? UtmMedium { get; set; }
        public string? UtmCampaign { get; set; }
        public string? UtmId { get; set; }
        public string? UtmTerm { get; set; }
        public string? UtmContent { get; set; }
        public string? Fbclid { get; set; }
        public Guid? AgentTrackingProfileId { get; set; }
        public string? AgentSlug { get; set; }
        public string? Environment { get; set; }
        public string? Host { get; set; }
        public DateTime? EventUtc { get; set; }
        public string? SubmitOutcome { get; set; }
        public string? MetadataJson { get; set; }
        public bool IsInternal { get; set; }
        // Behavior Intelligence fields — must mirror AnalyticsIngestController.AnalyticsEventRequest exactly
        public string? ReferrerHost { get; set; }
        public string? DeviceType { get; set; }
        public string? Browser { get; set; }
        public string? OperatingSystem { get; set; }
        public string? UserAgent { get; set; }
        public string? IpAddress { get; set; }
        public string? TimeZone { get; set; }
        public string? Language { get; set; }

        public bool? WebDriver { get; set; }
        public bool? IsHeadless { get; set; }
        public int? MouseMoveCount { get; set; }
        public int? HumanInteractionCount { get; set; }
        public int? VisibilityChangeCount { get; set; }
        public int? ScreenWidth { get; set; }
        public int? ScreenHeight { get; set; }
        public int? ViewportWidth { get; set; }
        public int? ViewportHeight { get; set; }
        public int? ScrollPercent { get; set; }
        public long? DwellMilliseconds { get; set; }
        public long? EngagedMilliseconds { get; set; }
        public bool? IsBounceCandidate { get; set; }
        public bool? IsExitPage { get; set; }
        public string? MetaCampaignId { get; set; }
        public string? MetaCampaignName { get; set; }
        public string? MetaAdSetId { get; set; }
        public string? MetaAdSetName { get; set; }
        public string? MetaAdId { get; set; }
        public string? MetaAdName { get; set; }
        public string? Placement { get; set; }
        public string? FormId { get; set; }
        public string? FieldName { get; set; }
        public string? ElementId { get; set; }
    }

    public sealed class LeadSubmitRequest
    {
        public string? SubmissionId { get; set; }
        [Required] public string FirstName { get; set; } = string.Empty;
        public string? LastName { get; set; }
        [Required, EmailAddress] public string Email { get; set; } = string.Empty;
        public string? Phone { get; set; }
        public string? PreferredContactMethod { get; set; }
        [Required] public string InterestType { get; set; } = string.Empty;
        public string? Notes { get; set; }
        public string? SourcePageKey { get; set; }
        public string? SourceCtaKey { get; set; }
        public string? SourcePath { get; set; }
        public string? UtmSource { get; set; }
        public string? UtmMedium { get; set; }
        public string? UtmCampaign { get; set; }
        public string? UtmId { get; set; }
        public string? UtmTerm { get; set; }
        public string? UtmContent { get; set; }
        public string? MetaCampaignId { get; set; }
        public string? MetaAdSetId { get; set; }
        public string? MetaAdId { get; set; }
        public string? Fbclid { get; set; }
        public string? SessionId { get; set; }
        public string? VisitorId { get; set; }
        public bool MarketingEmailConsent { get; set; }
        public bool CallTextConsent { get; set; }
        [Required] public bool TermsAccepted { get; set; }
        public string? Environment { get; set; }
        public string? Host { get; set; }
        public Guid? AgentTrackingProfileId { get; set; }
        public string? AgentSlug { get; set; }
        /// <summary>Product-specific metadata JSON forwarded transparently to AgentPortal.</summary>
        public string? MetadataJson { get; set; }
    }
}
