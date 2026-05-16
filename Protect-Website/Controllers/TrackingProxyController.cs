using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ProtectWebsite.Controllers;

[ApiController]
[AllowAnonymous]
public sealed class TrackingProxyController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonPascalCase = new()
    {
        PropertyNamingPolicy = null
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<TrackingProxyController> _logger;
    private readonly Services.Tracking.AgentTrackingResolver _resolver;
    private readonly string _founderUpn;

    public TrackingProxyController(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<TrackingProxyController> logger,
        Services.Tracking.AgentTrackingResolver resolver)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
        _resolver = resolver;
        _founderUpn = config["Founder:Upn"] ?? "zac.owen@mylegnd.com";
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

        await EnsureAgentAttributionAsync(req, ct);

        var response = await ForwardAsync("/api/analytics/ingest", req, ct);
        if (response == null)
        {
            _logger.LogError("Analytics forward unavailable. Returning 503 so upstream ingest failures are visible.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "tracking_forward_unavailable" });
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Analytics forward returned non-success status {StatusCode}. Passing through upstream status/body.", (int)response.StatusCode);
            return await BuildPassThroughResultAsync(response, ct);
        }

        return await BuildPassThroughResultAsync(response, ct);
    }

    [HttpPost]
    [Route("api/lead/submit")]
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> SubmitLead([FromBody] LeadSubmitRequest req, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid();

        _logger.LogInformation(
            "LeadProxy [{CorrelationId}]: request received InterestType={InterestType} SourcePageKey={SourcePageKey} AgentSlug={Slug} Host={Host}",
            correlationId, req.InterestType, req.SourcePageKey, req.AgentSlug, req.Host);

        var response = await ForwardAsync("/api/lead/submit", req, ct, correlationId);

        if (response == null)
        {
            _logger.LogError(
                "LeadProxy [{CorrelationId}]: forward failed — no response from AgentPortal (proxy configuration or connectivity issue)",
                correlationId);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "lead_forward_failed", correlationId });
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

    private async Task<HttpResponseMessage?> ForwardAsync(string path, object payload, CancellationToken ct, Guid? callerCorrelationId = null)
    {
        var portalBase = (_config["Tracking:ApiBase"] ?? Environment.GetEnvironmentVariable("TRACKING_API_BASE") ?? string.Empty).Trim();
        var sharedSecret = (_config["Tracking:SharedSecret"] ?? Environment.GetEnvironmentVariable("TRACKING_SHARED_SECRET") ?? string.Empty).Trim();

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
    private async Task EnsureAgentAttributionAsync(AnalyticsEventRequest req, CancellationToken ct)
    {
        // If a slug is provided but profile id is missing, resolve it.
        if (!req.AgentTrackingProfileId.HasValue && !string.IsNullOrWhiteSpace(req.AgentSlug))
        {
            var bySlug = await _resolver.ResolveBySlugAsync(req.AgentSlug.Trim(), ct);
            if (bySlug.Found && bySlug.Profile != null)
            {
                req.AgentTrackingProfileId = bySlug.Profile.Id;
                req.AgentSlug = bySlug.CanonicalSlug ?? bySlug.Profile.Slug;
                return;
            }
        }

        // Already fully attributed.
        if (req.AgentTrackingProfileId.HasValue && !string.IsNullOrWhiteSpace(req.AgentSlug))
            return;

        // Attempt slug extraction from page path ("/a/{slug}/...").
        var path = (req.Path ?? string.Empty).Trim();
        if (path.StartsWith("/a/", StringComparison.OrdinalIgnoreCase))
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                var slug = segments[1];
                var bySlug = await _resolver.ResolveBySlugAsync(slug, ct);
                if (bySlug.Found && bySlug.Profile != null)
                {
                    req.AgentTrackingProfileId = bySlug.Profile.Id;
                    req.AgentSlug = bySlug.CanonicalSlug ?? bySlug.Profile.Slug;
                    return;
                }
            }
        }

        // Founder default-domain fallback.
        if (!req.AgentTrackingProfileId.HasValue && string.IsNullOrWhiteSpace(req.AgentSlug))
        {
            var founder = await _resolver.ResolveByUpnAsync(_founderUpn, ct);
            if (founder.Found && founder.Profile != null)
            {
                req.AgentTrackingProfileId = founder.Profile.Id;
                req.AgentSlug = founder.CanonicalSlug ?? founder.Profile.Slug;
            }
        }
    }

    public sealed class AnalyticsEventRequest
    {
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
        [Required] public string FirstName { get; set; } = string.Empty;
        public string? LastName { get; set; }
        [Required, EmailAddress] public string Email { get; set; } = string.Empty;
        public string? Phone { get; set; }
        public string? PreferredContactMethod { get; set; }
        [Required] public string InterestType { get; set; } = string.Empty;
        public string? Notes { get; set; }
        public string? SourcePageKey { get; set; }
        public string? SourceCtaKey { get; set; }
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
