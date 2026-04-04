using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
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
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "analytics_forward_failed" });

        return await BuildPassThroughResultAsync(response, ct);
    }

    [HttpPost]
    [Route("api/lead/submit")]
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> SubmitLead([FromBody] LeadSubmitRequest req, CancellationToken ct)
    {
        var response = await ForwardAsync("/api/lead/submit", req, ct);
        if (response == null)
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "lead_forward_failed" });

        return await BuildPassThroughResultAsync(response, ct);
    }

    private async Task<HttpResponseMessage?> ForwardAsync(string path, object payload, CancellationToken ct)
    {
        var portalBase = (_config["Tracking:ApiBase"] ?? Environment.GetEnvironmentVariable("TRACKING_API_BASE") ?? string.Empty).Trim();
        var sharedSecret = (_config["Tracking:SharedSecret"] ?? Environment.GetEnvironmentVariable("TRACKING_SHARED_SECRET") ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(portalBase) || string.IsNullOrWhiteSpace(sharedSecret))
        {
            _logger.LogError("Tracking proxy configuration missing. Ensure Tracking:ApiBase and Tracking:SharedSecret are configured.");
            return null;
        }

        var target = $"{portalBase.TrimEnd('/')}{path}";

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("X-Shared-Secret", sharedSecret);
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonPascalCase), Encoding.UTF8, "application/json");

            return await client.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tracking proxy forward failed to {TargetPath}", path);
            return null;
        }
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
        public Guid? AgentTrackingProfileId { get; set; }
        public string? AgentSlug { get; set; }
        public string? Environment { get; set; }
        public string? Host { get; set; }
        public DateTime? EventUtc { get; set; }
        public string? SubmitOutcome { get; set; }
        public string? MetadataJson { get; set; }
        public bool IsInternal { get; set; }
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
        public string? SessionId { get; set; }
        public string? VisitorId { get; set; }
        public bool MarketingEmailConsent { get; set; }
        public bool CallTextConsent { get; set; }
        [Required] public bool TermsAccepted { get; set; }
        public string? Environment { get; set; }
        public string? Host { get; set; }
        public Guid? AgentTrackingProfileId { get; set; }
        public string? AgentSlug { get; set; }
    }
}
