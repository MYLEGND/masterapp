using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Protect_Website.Models;
using ProtectWebsite.Services;
using ProtectWebsite.Services.Booking;
using ProtectWebsite.Services.Meta;
using ProtectWebsite.Services.Tracking;
using Shared.Meta;
using System.Globalization;
using System.Text.Json;

namespace Protect_Website.Controllers
{
    public class ThankYouController : Controller
    {
        private const string FallbackBookingLink = "https://outlook.office.com/book/LEGEND@mylegnd.com/?ismsaljsauthenabled=true";

        private readonly MasterAppDbContext _db;
        private readonly AgentTrackingResolver _resolver;
        private readonly IPublicBookingResolver _publicBookingResolver;
        private readonly string _defaultBookingLink;
        private readonly string _trackingApiBase;
        private readonly ILogger<ThankYouController> _logger;

        public ThankYouController(
            MasterAppDbContext db,
            IConfiguration configuration,
            AgentTrackingResolver resolver,
            IPublicBookingResolver publicBookingResolver,
            ILogger<ThankYouController> logger)
        {
            _db = db;
            _resolver = resolver;
            _publicBookingResolver = publicBookingResolver;
            _defaultBookingLink = (configuration["Contact:BookingLink"] ?? FallbackBookingLink).Trim();
            _trackingApiBase = (configuration["Tracking:ApiBase"] ?? "https://portal.mylegnd.com").TrimEnd('/');
            _logger = logger;
        }

        // GET: /ThankYou
        public async Task<IActionResult> Index()
        {
            var ct = HttpContext?.RequestAborted ?? CancellationToken.None;
            var quoteKey = TempData.Peek("QuoteType")?.ToString()?.Trim() ?? string.Empty;
            var leadIdRaw = TempData.Peek("MetaLeadLeadId")?.ToString()?.Trim();
            WebsiteLead? lead = null;
            if (Guid.TryParse(leadIdRaw, out var leadId))
            {
                lead = await _db.WebsiteLeads.AsNoTracking().FirstOrDefaultAsync(
                    x => x.LeadId == leadId,
                    ct);

                var metaTracking = MetaLeadTrackingJson.Read(lead?.MetadataJson);
                if (metaTracking != null)
                {
                    ViewData["ResolvedMetaPixelId"] = metaTracking.ResolvedMetaPixelId;
                    ViewData["MetaPixelOwnerType"] = string.IsNullOrWhiteSpace(metaTracking.PixelOwnerType)
                        ? MetaPixelOwnerTypes.None
                        : metaTracking.PixelOwnerType;
                }
            }

            var model = new QuoteThankYouViewModel
            {
                QuoteKey = quoteKey,
                BookingLink = _defaultBookingLink,
                AgentTrustProfile = await BuildAgentTrustProfileAsync(lead, ct)
            };

            var trackingContext = BuildTrackingContext(quoteKey, lead);
            ViewData["PageKey"] = trackingContext.PageKey;
            ViewData["PageVariant"] = trackingContext.PageVariant;
            ViewData["PageMode"] = trackingContext.PageMode;
            ViewData["PageCategory"] = "quote";
            ViewData["QuoteTypeForTracking"] = trackingContext.QuoteTypeForTracking;
            ViewData["IsLandingPage"] = string.Equals(trackingContext.PageMode, "paid_landing", StringComparison.OrdinalIgnoreCase);
            ViewData["LeadId"] = lead?.LeadId.ToString("D") ?? string.Empty;
            ViewData["MetaLeadEventId"] = TempData.Peek("MetaLeadEventId")?.ToString()?.Trim() ?? string.Empty;
            ViewData["MetaLeadLeadId"] = leadIdRaw ?? string.Empty;
            ViewData["Source"] = lead?.UtmSource ?? string.Empty;
            ViewData["Campaign"] = lead?.UtmCampaign ?? lead?.MetaCampaignId ?? string.Empty;
            ViewData["Fbclid"] = lead?.Fbclid ?? string.Empty;
            ViewData["SessionId"] = lead?.SessionId ?? string.Empty;

            // Loads Views/Quote/ThankYou.cshtml
            return View("~/Views/Quote/ThankYou.cshtml", model);
        }

        [HttpPost("/ThankYou/meta-browser-ack")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> AckBrowserPixel([FromBody] ThankYouMetaBrowserPixelAckRequest? request)
        {
            if (request == null ||
                request.LeadId == Guid.Empty ||
                string.IsNullOrWhiteSpace(request.EventId))
            {
                return BadRequest(new { error = "Invalid browser pixel acknowledgment." });
            }

            var lead = await _db.WebsiteLeads.FirstOrDefaultAsync(
                x => x.LeadId == request.LeadId,
                HttpContext?.RequestAborted ?? CancellationToken.None);

            if (lead == null)
                return NotFound();

            var currentState = MetaLeadTrackingJson.Read(lead.MetadataJson);
            if (!string.IsNullOrWhiteSpace(currentState?.EventId) &&
                !string.Equals(currentState.EventId, request.EventId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Conflict();
            }

            var normalizedStatus = MetaLeadTrackingWorkflow.NormalizeBrowserPixelStatus(request.Status);
            var normalizedNote = MetaLeadTrackingWorkflow.NormalizeBrowserPixelNote(request.Note);

            lead.MetadataJson = MetaLeadTrackingJson.Upsert(
                lead.MetadataJson,
                state =>
                {
                    state.EventId ??= request.EventId.Trim();
                    state.BrowserPixelStatus = normalizedStatus;
                    state.BrowserPixelUpdatedUtc = DateTime.UtcNow;
                    state.BrowserPixelNote = normalizedNote;
                });

try
{
    var analyticsMetadata = new
    {
        LeadId = lead.LeadId,
        EventId = request.EventId.Trim(),
        Status = normalizedStatus,
        Note = normalizedNote,
        Route = "/ThankYou/meta-browser-ack"
    };

    var trackingContext = BuildTrackingContext(lead);
    var ctx = BuildTrackingContext(trackingContext, lead, "meta_browser_event_attempt", analyticsMetadata);
    var analyticsEvent = UnifiedEventMapper.ToAnalytics(ctx);
    UnifiedAnalyticsWriter.Write(_db, analyticsEvent);

    if (string.Equals(normalizedStatus, "sent", StringComparison.OrdinalIgnoreCase))
    {
        var ctxSuccess = BuildTrackingContext(trackingContext, lead, "meta_browser_event_success", analyticsMetadata);
        var analyticsEventSuccess = UnifiedEventMapper.ToAnalytics(ctxSuccess);
        UnifiedAnalyticsWriter.Write(_db, analyticsEventSuccess);
    }

    await _db.SaveChangesAsync(HttpContext?.RequestAborted ?? CancellationToken.None);
    _logger.LogInformation(
        "ThankYou browser pixel ack lead={LeadId} status={Status} eventId={EventId}",
        request.LeadId, normalizedStatus, request.EventId);
}
            catch (Exception ackEx)
            {
                _logger.LogError(
                    ackEx,
                    "ThankYou browser pixel ack save failed lead={LeadId} status={Status} eventId={EventId}",
                    request.LeadId, normalizedStatus, request.EventId);
            }

            return NoContent();
        }

        private async Task<LifeWizardAgentTrustProfile?> BuildAgentTrustProfileAsync(WebsiteLead? lead, CancellationToken ct)
        {
            var resolved = await ResolveAgentContextAsync(lead, ct);
            if (resolved == null)
            {
                return null;
            }

            var agentProfile = await ResolveAgentProfileAsync(resolved.Profile, ct);
            var displayName = ResolveAgentDisplayName(agentProfile, resolved.Profile);

            return new LifeWizardAgentTrustProfile
            {
                AgentTrackingProfileId = resolved.Profile.Id,
                AgentSlug = resolved.Slug,
                DisplayName = displayName,
                FirstName = ResolveAgentFirstName(displayName),
                Npn = string.IsNullOrWhiteSpace(agentProfile?.Npn) ? null : agentProfile.Npn.Trim(),
                ShortBio = string.IsNullOrWhiteSpace(agentProfile?.ShortBio)
                    ? null
                    : agentProfile.ShortBio.Trim(),
                ProfileImageUrl = BuildAgentAvatarUrl(resolved.Slug),
                SchedulingLink = await ResolveSchedulingLinkAsync(lead, resolved, ct)
            };
        }

        private async Task<string?> ResolveSchedulingLinkAsync(WebsiteLead? lead, ResolvedAgentContext resolved, CancellationToken ct)
        {
            var bookingResolution = await _publicBookingResolver.ResolveAsync(
                new PublicBookingResolveContext(
                    WebsiteLeadId: lead?.LeadId,
                    AgentTrackingProfileId: resolved.Profile.Id,
                    AgentUserId: resolved.Profile.AgentUserId,
                    AgentSlug: resolved.Slug),
                ct);
            if (bookingResolution.HasFallback)
            {
                return bookingResolution.FallbackUrl;
            }

            return bookingResolution.HasEmbed
                ? bookingResolution.EmbedUrl
                : null;
        }

        private async Task<ResolvedAgentContext?> ResolveAgentContextAsync(WebsiteLead? lead, CancellationToken ct)
        {
            if (lead?.AgentTrackingProfileId is Guid profileId && profileId != Guid.Empty)
            {
                var trackingProfile = await _db.AgentTrackingProfiles.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == profileId, ct);

                if (trackingProfile != null)
                {
                    var leadSlug = string.IsNullOrWhiteSpace(lead.AgentSlug)
                        ? trackingProfile.Slug
                        : lead.AgentSlug.Trim();
                    return new ResolvedAgentContext(trackingProfile, leadSlug);
                }
            }

            if (!string.IsNullOrWhiteSpace(lead?.AgentSlug))
            {
                var resolvedByLeadSlug = await _resolver.ResolveBySlugAsync(lead.AgentSlug.Trim(), ct);
                if (resolvedByLeadSlug.Found && resolvedByLeadSlug.Profile != null)
                {
                    return new ResolvedAgentContext(
                        resolvedByLeadSlug.Profile,
                        resolvedByLeadSlug.CanonicalSlug ?? lead.AgentSlug.Trim());
                }
            }

            if (HttpContext?.Items["TrackingProfile"] is AgentTrackingProfile trackingProfileFromRequest)
            {
                var requestSlug = (HttpContext.Items["TrackingSlug"] as string)?.Trim();
                return new ResolvedAgentContext(
                    trackingProfileFromRequest,
                    string.IsNullOrWhiteSpace(requestSlug) ? trackingProfileFromRequest.Slug : requestSlug);
            }

            var extractedSlug = ExtractSlugFromPath(Request?.Path.Value);
            if (string.IsNullOrWhiteSpace(extractedSlug))
            {
                extractedSlug = ExtractSlugFromPath(Request?.Headers["Referer"].ToString());
            }

            if (!string.IsNullOrWhiteSpace(extractedSlug))
            {
                var resolvedBySlug = await _resolver.ResolveBySlugAsync(extractedSlug, ct);
                if (resolvedBySlug.Found && resolvedBySlug.Profile != null)
                {
                    return new ResolvedAgentContext(
                        resolvedBySlug.Profile,
                        resolvedBySlug.CanonicalSlug ?? extractedSlug);
                }
            }

            return null;
        }

        private async Task<AgentProfile?> ResolveAgentProfileAsync(AgentTrackingProfile trackingProfile, CancellationToken ct)
        {
            var hasAgentUserId = !string.IsNullOrWhiteSpace(trackingProfile.AgentUserId);
            var hasAgentUpn = !string.IsNullOrWhiteSpace(trackingProfile.AgentUpn);
            var normalizedUpn = hasAgentUpn ? trackingProfile.AgentUpn.Trim().ToUpperInvariant() : string.Empty;

            if (!hasAgentUserId && !hasAgentUpn)
            {
                return null;
            }

            var candidates = await _db.AgentProfiles.AsNoTracking()
                .Where(x =>
                    (hasAgentUserId && x.AgentUserId == trackingProfile.AgentUserId) ||
                    (hasAgentUpn && (x.NormalizedEmail == normalizedUpn || x.AgentUpn == trackingProfile.AgentUpn)))
                .ToListAsync(ct);

            return candidates
                .OrderByDescending(x => !string.IsNullOrWhiteSpace(x.Npn))
                .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.ShortBio))
                .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.FullName))
                .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.Title))
                .ThenByDescending(x => hasAgentUserId && x.AgentUserId == trackingProfile.AgentUserId)
                .ThenByDescending(x => x.UpdatedUtc)
                .FirstOrDefault();
        }

        private static string ResolveAgentDisplayName(AgentProfile? agentProfile, AgentTrackingProfile trackingProfile)
        {
            if (!string.IsNullOrWhiteSpace(agentProfile?.FullName))
            {
                return agentProfile.FullName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(trackingProfile.DisplayName))
            {
                return trackingProfile.DisplayName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(trackingProfile.AgentUpn))
            {
                var emailName = trackingProfile.AgentUpn.Split('@', 2)[0]
                    .Replace('.', ' ')
                    .Replace('-', ' ')
                    .Replace('_', ' ')
                    .Trim();
                if (!string.IsNullOrWhiteSpace(emailName))
                {
                    return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(emailName);
                }
            }

            return trackingProfile.Slug;
        }

        private static ThankYouTrackingContext BuildTrackingContext(string quoteKey, WebsiteLead? lead)
        {
            var sourcePageKey = (lead?.SourcePageKey ?? string.Empty).Trim();
            var fallbackPageKey = quoteKey.Trim().ToLowerInvariant() switch
            {
                "auto" => "quote_auto",
                "home" => "quote_home",
                "dvh" => "quote_dvh",
                "disability" => "quote_disability",
                "commercial" => "quote_commercial",
                "life" or "life insurance" => "quote_life",
                "term life" => "quote_term_life",
                "whole life" => "quote_whole_life",
                "final expense" => "quote_final_expense",
                "mortgage protection" => "quote_mortgage_protection",
                "indexed universal life" => "quote_iul",
                _ => "quote_thank_you"
            };

            var basePageKey = string.IsNullOrWhiteSpace(sourcePageKey) ? fallbackPageKey : sourcePageKey;
            var pageKey = basePageKey.EndsWith("_thank_you", StringComparison.OrdinalIgnoreCase)
                ? basePageKey
                : $"{basePageKey}_thank_you";
            var pageVariant = ReadTrackingMetadataValue(lead?.MetadataJson, "pageVariant")
                ?? (basePageKey.Contains("_landing", StringComparison.OrdinalIgnoreCase) ? "landing" : "website");
            var pageMode = ReadTrackingMetadataValue(lead?.MetadataJson, "pageMode")
                ?? (basePageKey.Contains("_landing", StringComparison.OrdinalIgnoreCase) ? "paid_landing" : "site_mode");

            return new ThankYouTrackingContext(
                PageKey: pageKey,
                PageVariant: pageVariant,
                PageMode: pageMode,
                QuoteTypeForTracking: ResolveQuoteTypeForTracking(quoteKey));
        }

        private static ThankYouTrackingContext BuildTrackingContext(WebsiteLead? lead)
        {
            var quoteKey = (lead?.SourcePageKey ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "quote_auto" => "auto",
                "quote_home" => "home",
                "quote_dvh" => "dvh",
                "quote_disability" => "disability",
                "quote_commercial" => "commercial",
                "quote_term_life" => "term life",
                "quote_whole_life" => "whole life",
                "quote_final_expense" => "final expense",
                "quote_mortgage_protection" => "mortgage protection",
                "quote_iul" => "indexed universal life",
                _ => string.IsNullOrWhiteSpace(lead?.InterestType)
                    ? "life insurance"
                    : lead!.InterestType!.Trim().ToLowerInvariant() switch
                    {
                        "auto_insurance" => "auto",
                        "home_insurance" => "home",
                        "dental_vision_hearing" => "dvh",
                        "disability_insurance" => "disability",
                        "commercial_insurance" => "commercial",
                        "life" => "life insurance",
                        _ => "life insurance"
                    }
            };

            return BuildTrackingContext(quoteKey, lead);
        }

        private UnifiedEventContext BuildTrackingContext(
            ThankYouTrackingContext trackingContext,
            WebsiteLead lead,
            string eventType,
            object metadata,
            DateTime? eventUtc = null)
        {
            return UnifiedEventContextBuilder.Build(
                httpContext: HttpContext,
                eventName: eventType,
                eventUtc: eventUtc,
                sessionId: lead.SessionId,
                visitorId: lead.VisitorId,
                pageKey: trackingContext.PageKey,
                effectivePageKey: trackingContext.PageKey,
                pageVariant: trackingContext.PageVariant,
                pageMode: trackingContext.PageMode,
                utmSource: lead.UtmSource,
                utmMedium: lead.UtmMedium,
                utmCampaign: lead.UtmCampaign,
                utmId: lead.UtmId,
                metaCampaignId: lead.MetaCampaignId,
                metaAdSetId: lead.MetaAdSetId,
                metaAdId: lead.MetaAdId,
                fbclid: lead.Fbclid,
                agentSlug: lead.AgentSlug,
                agentTrackingProfileId: lead.AgentTrackingProfileId,
                isInternal: lead.IsInternal,
                environment: lead.Environment,
                host: lead.Host,
                quoteType: trackingContext.QuoteTypeForTracking,
                metadata: metadata);
        }

        private static string ResolveQuoteTypeForTracking(string quoteKey)
        {
            return quoteKey.Trim().ToLowerInvariant() switch
            {
                "life insurance" => "life",
                "term life" => "term",
                "whole life" => "wholelife",
                "final expense" => "finalexpense",
                "mortgage protection" => "mortgage",
                "indexed universal life" => "iul",
                _ => quoteKey
            };
        }

        private static string? ReadTrackingMetadataValue(string? metadataJson, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(metadataJson) || string.IsNullOrWhiteSpace(propertyName))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(metadataJson);
                if (!doc.RootElement.TryGetProperty(propertyName, out var property) ||
                    property.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                var value = property.GetString()?.Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch
            {
                return null;
            }
        }

        private sealed record ThankYouTrackingContext(
            string PageKey,
            string PageVariant,
            string PageMode,
            string QuoteTypeForTracking);

        private static string ResolveAgentFirstName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return "there";
            }

            var firstName = displayName
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
                ?.Trim();

            return string.IsNullOrWhiteSpace(firstName) ? "there" : firstName;
        }

        private string BuildAgentAvatarUrl(string slug)
        {
            var safeSlug = Uri.EscapeDataString(slug.Trim());
            return $"{_trackingApiBase}/avatar/agent/{safeSlug}";
        }

        private static string? ExtractSlugFromPath(string? pathOrUrl)
        {
            if (string.IsNullOrWhiteSpace(pathOrUrl))
            {
                return null;
            }

            var value = pathOrUrl.Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
            {
                value = absoluteUri.AbsolutePath;
            }

            var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && string.Equals(segments[0], "a", StringComparison.OrdinalIgnoreCase))
            {
                var slug = segments[1].Trim();
                return string.IsNullOrWhiteSpace(slug) ? null : slug;
            }

            return null;
        }

        private sealed record ResolvedAgentContext(AgentTrackingProfile Profile, string Slug);
    }
}
