using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Protect_Website.Models;
using ProtectWebsite.Services.Meta;
using ProtectWebsite.Services.Tracking;
using Shared.Meta;
using System.Globalization;

namespace Protect_Website.Controllers
{
    public class ThankYouController : Controller
    {
        private const string FallbackBookingLink = "https://outlook.office.com/book/LEGEND@mylegnd.com/?ismsaljsauthenabled=true";

        private readonly MasterAppDbContext _db;
        private readonly AgentTrackingResolver _resolver;
        private readonly string _defaultBookingLink;
        private readonly string _trackingApiBase;
        private readonly ILogger<ThankYouController> _logger;

        public ThankYouController(
            MasterAppDbContext db,
            IConfiguration configuration,
            AgentTrackingResolver resolver,
            ILogger<ThankYouController> logger)
        {
            _db = db;
            _resolver = resolver;
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
                SchedulingLink = null
            };
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
