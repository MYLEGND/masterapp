using Microsoft.AspNetCore.Mvc;
using Domain.Entities;
using Infrastructure.Data;
using Protect_Website.Models;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Azure.Identity;
using System.Text.Json;
using Infrastructure.Leads;
using ProtectWebsite.Services.Meta;
using ProtectWebsite.Services;
using ProtectWebsite.Services.Tracking;

namespace Protect_Website.Controllers
{
    [Route("Quote")]
    public class HealthQuoteController : Controller
    {
        private readonly string tenantId;
        private readonly string clientId;
        private readonly string clientSecret;
        private readonly string senderEmail;
        private readonly string recipientEmail;
        private readonly string websiteName;
        private readonly AgentTrackingResolver _resolver;
        private readonly MasterAppDbContext _db;
        private readonly IMetaConversionsApiService _metaConversionsApi;
        private readonly IMetaPixelResolutionService _metaPixelResolution;
        private readonly IWebsiteLifeLeadCaptureService _websiteLeadCapture;
        private readonly ILogger<HealthQuoteController> _logger;

        public HealthQuoteController(IConfiguration configuration, AgentTrackingResolver resolver,
            MasterAppDbContext db, IMetaConversionsApiService metaConversionsApi, IMetaPixelResolutionService metaPixelResolution, IWebsiteLifeLeadCaptureService websiteLeadCapture, ILogger<HealthQuoteController> logger)
        {
            tenantId = configuration["AzureAd:TenantId"]!;
            clientId = configuration["AzureAd:ClientId"]!;
            clientSecret = configuration["AzureAd:ClientSecret"]!;
            senderEmail = configuration["Contact:SenderEmail"] ?? "connect@mylegnd.com";
            recipientEmail = configuration["Contact:RecipientEmail"]!;
            websiteName = configuration["Contact:WebsiteName"] ?? "Legend Legacy Protection";
            _resolver = resolver;
            _db = db;
            _metaConversionsApi = metaConversionsApi;
            _metaPixelResolution = metaPixelResolution;
            _websiteLeadCapture = websiteLeadCapture;
            _logger = logger;
        }

        // ===================== GET =====================
        [HttpGet("Health")]
        public IActionResult HealthQuote()
        {
            return View("~/Views/Quote/Health.cshtml");
        }
[HttpPost("Health")]
public async Task<IActionResult> SubmitHealthQuote(HealthQuoteFormModel model)
{
    if (!ModelState.IsValid)
        return View("~/Views/Quote/Health.cshtml", model);

    var correlationId = Guid.NewGuid();
    _logger.LogInformation(
        "HealthQuote [{CorrelationId}]: request received Email={Email}",
        correlationId, model.Email);

    var (leadRecipientEmail, agentProfileId, agentSlug, isFounderPath) = await ResolveLeadContextAsync();
    _logger.LogInformation(
        "HealthQuote [{CorrelationId}]: attribution resolved AgentSlug={Slug} ProfileId={ProfileId} Recipient={Recipient}",
        correlationId, agentSlug, agentProfileId, leadRecipientEmail);
    var resolvedMetaPixel = await _metaPixelResolution.ResolveForLeadAsync(
        agentProfileId,
        agentSlug,
        isFounderPath,
        HttpContext?.RequestAborted ?? CancellationToken.None);

    // ── 1. Persist lead FIRST ──────────────────────────────────────────────────
    WebsiteLead lead;
    try
    {
        var now = DateTime.UtcNow;
        lead = new WebsiteLead
        {
            LeadId        = Guid.NewGuid(),
            FirstName     = model.FirstName?.Trim() ?? "",
            LastName      = string.IsNullOrWhiteSpace(model.LastName) ? null : model.LastName.Trim(),
            Email         = model.Email?.Trim() ?? "",
            Phone         = string.IsNullOrWhiteSpace(model.Phone) ? null : model.Phone.Trim(),
            InterestType  = "health_insurance",
            SourcePageKey = "quote_health",
            UtmSource     = string.IsNullOrWhiteSpace(model.UtmSource)   ? null : model.UtmSource.Trim(),
            UtmMedium     = string.IsNullOrWhiteSpace(model.UtmMedium)   ? null : model.UtmMedium.Trim(),
            UtmCampaign   = string.IsNullOrWhiteSpace(model.UtmCampaign) ? null : model.UtmCampaign.Trim(),
            UtmId         = string.IsNullOrWhiteSpace(model.UtmId) ? null : model.UtmId.Trim(),
            MetaCampaignId = string.IsNullOrWhiteSpace(model.MetaCampaignId) ? null : model.MetaCampaignId.Trim(),
            MetaAdSetId   = string.IsNullOrWhiteSpace(model.MetaAdSetId) ? null : model.MetaAdSetId.Trim(),
            MetaAdId      = string.IsNullOrWhiteSpace(model.MetaAdId) ? null : model.MetaAdId.Trim(),
            Fbclid        = string.IsNullOrWhiteSpace(model.Fbclid)      ? null : model.Fbclid.Trim(),
            SessionId     = string.IsNullOrWhiteSpace(model.SessionId)   ? null : model.SessionId.Trim(),
            VisitorId     = string.IsNullOrWhiteSpace(model.VisitorId)   ? null : model.VisitorId.Trim(),
            MarketingEmailConsent = model.AcknowledgedDisclaimer,
            CallTextConsent = model.AcknowledgedDisclaimer && !string.IsNullOrWhiteSpace(model.Phone),
            TermsAccepted = true,
            Host          = Request?.Host.ToString(),
            Environment   = EnvironmentLabelResolver.Resolve(),
            CreatedUtc    = now,
            Status        = "New",
            AgentTrackingProfileId = agentProfileId,
            AgentSlug     = agentSlug,
            MetadataJson  = JsonSerializer.Serialize(new
            {
                HouseholdSize  = model.HouseholdSize,
                PrimaryConcern = model.PrimaryConcern,
                CoverageType   = model.CoverageType,
                UtmId          = model.UtmId,
                Fbclid         = model.Fbclid,
                UtmTerm        = model.UtmTerm,
                UtmContent     = model.UtmContent,
                MetaCampaignId = model.MetaCampaignId,
                MetaAdSetId    = model.MetaAdSetId,
                MetaAdId       = model.MetaAdId,
                ReferrerUrl    = model.ReferrerUrl,
                LandingPageUrl = model.LandingPageUrl,
                CorrelationId  = correlationId,
            })
        };
        _db.WebsiteLeads.Add(lead);
        await _db.SaveChangesAsync();
        _logger.LogInformation(
            "HealthQuote [{CorrelationId}]: WebsiteLead {LeadId} saved",
            correlationId, lead.LeadId);
    }
    catch (Exception persistEx)
    {
        _logger.LogError(persistEx,
            "HealthQuote [{CorrelationId}]: lead persistence failed for {Email}",
            correlationId, model.Email);
        ModelState.AddModelError("", $"Failed to save lead: {persistEx.Message}");
        return View("~/Views/Quote/Health.cshtml", model);
    }

    async Task TryWriteLeadEventAsync(string eventType, object metadata, DateTime? eventUtc = null)
    {
        try
        {
            _db.AnalyticsEvents.Add(WebsiteLeadAnalyticsWriter.CreateEvent(
                lead,
                eventType,
                "quote_health",
                "health_insurance",
                metadata,
                eventUtc));
            await _db.SaveChangesAsync(HttpContext?.RequestAborted ?? CancellationToken.None);
        }
        catch (Exception analyticsEx)
        {
            _logger.LogWarning(
                analyticsEx,
                "HealthQuote [{CorrelationId}]: analytics event write failed for {EventType} lead {LeadId}",
                correlationId,
                eventType,
                lead.LeadId);
        }
    }

    await TryWriteLeadEventAsync(
        "lead_persisted",
        new { LeadId = lead.LeadId, CorrelationId = correlationId, QuoteType = "health_insurance" },
        lead.CreatedUtc);

    try
    {
        await TryWriteLeadEventAsync(
            "workstation_capture_attempt",
            new { LeadId = lead.LeadId, CorrelationId = correlationId, ProductType = "health", OfferKey = "health" });

        var captureResult = await _websiteLeadCapture.UpsertAsync(
            new WebsiteLifeLeadCaptureRequest
            {
                WebsiteLeadId = lead.LeadId,
                SubmittedUtc = lead.CreatedUtc,
                ProductType = "health",
                OfferKey = "health",
                FirstName = lead.FirstName,
                LastName = lead.LastName,
                Email = lead.Email,
                Phone = lead.Phone,
                Age = model.Age,
                AgentTrackingProfileId = agentProfileId,
                AgentSlug = agentSlug,
                RecipientEmail = leadRecipientEmail
            },
            HttpContext?.RequestAborted ?? CancellationToken.None);

        if (captureResult.Captured)
        {
            await _db.SaveChangesAsync(HttpContext?.RequestAborted ?? CancellationToken.None);
            await TryWriteLeadEventAsync(
                "workstation_capture_success",
                new
                {
                    LeadId = lead.LeadId,
                    CorrelationId = correlationId,
                    WorkstationLeadId = captureResult.WorkstationLeadId,
                    Bucket = captureResult.Bucket,
                    AgentUserId = captureResult.AgentUserId,
                    CaptureMode = captureResult.Created ? "created" : "updated"
                });
        }
        else
        {
            await TryWriteLeadEventAsync(
                "workstation_capture_failure",
                new
                {
                    LeadId = lead.LeadId,
                    CorrelationId = correlationId,
                    Reason = captureResult.Reason ?? "unknown",
                    Bucket = captureResult.Bucket,
                    AgentUserId = captureResult.AgentUserId
                });
        }
    }
    catch (Exception captureEx)
    {
        _logger.LogError(
            captureEx,
            "HealthQuote [{CorrelationId}]: workstation capture failed for lead {LeadId}",
            correlationId,
            lead.LeadId);

        await TryWriteLeadEventAsync(
            "workstation_capture_failure",
            new
            {
                LeadId = lead.LeadId,
                CorrelationId = correlationId,
                Reason = "capture_exception",
                ErrorMessage = captureEx.Message
            });
    }

    var metaLeadEventId = Guid.NewGuid().ToString("N");
    await MetaLeadTrackingWorkflow.TryPersistAsync(
        lead,
        _db,
        correlationId,
        "meta_tracking_initialized",
        _logger,
        HttpContext?.RequestAborted ?? CancellationToken.None,
        state =>
        {
            state.EventId = metaLeadEventId;
            state.ResolvedMetaPixelId = resolvedMetaPixel.PixelId;
            state.PixelOwnerType = resolvedMetaPixel.PixelOwnerType;
            state.BrowserPixelStatus = "pending";
            state.BrowserPixelUpdatedUtc = DateTime.UtcNow;
            state.BrowserPixelNote = null;
            state.ServerCapiStatus = "pending";
            state.ServerCapiUpdatedUtc = DateTime.UtcNow;
            state.ServerCapiNote = null;
        });

    await TryWriteLeadEventAsync(
        "capi_event_attempt",
        new
        {
            LeadId = lead.LeadId,
            CorrelationId = correlationId,
            EventId = metaLeadEventId,
            PixelId = resolvedMetaPixel.PixelId,
            PixelOwnerType = resolvedMetaPixel.PixelOwnerType
        });

    var metaCapiResult = await _metaConversionsApi.SendLeadAsync(
        new MetaLeadConversionRequest
        {
            LeadId = lead.LeadId,
            CorrelationId = correlationId,
            EventId = metaLeadEventId,
            QuoteType = "Health",
            PageKey = "quote_health",
            OfferKey = "health",
            EventSourceUrl = MetaLeadTrackingWorkflow.ResolveEventSourceUrl(model.LandingPageUrl, Request),
            ClientIpAddress = MetaLeadTrackingWorkflow.ResolveClientIpAddress(Request),
            ClientUserAgent = Request?.Headers["User-Agent"].ToString(),
            Fbp = MetaLeadTrackingWorkflow.ResolveCookieValue(Request, "_fbp"),
            Fbc = MetaLeadTrackingWorkflow.ResolveCookieValue(Request, "_fbc"),
            Fbclid = lead.Fbclid,
            Email = lead.Email,
            Phone = lead.Phone,
            AllowHashedContactData = lead.TermsAccepted && lead.MarketingEmailConsent,
            EventUtc = lead.CreatedUtc,
            PixelId = resolvedMetaPixel.PixelId,
            AccessToken = resolvedMetaPixel.AccessToken,
            TestEventCode = resolvedMetaPixel.TestEventCode,
            PixelOwnerType = resolvedMetaPixel.PixelOwnerType
        },
        HttpContext?.RequestAborted ?? CancellationToken.None);

    await MetaLeadTrackingWorkflow.TryPersistAsync(
        lead,
        _db,
        correlationId,
        "meta_capi_result",
        _logger,
        HttpContext?.RequestAborted ?? CancellationToken.None,
        state =>
        {
            state.EventId ??= metaLeadEventId;
            state.ResolvedMetaPixelId ??= resolvedMetaPixel.PixelId;
            state.PixelOwnerType = resolvedMetaPixel.PixelOwnerType;
            state.ServerCapiStatus = metaCapiResult.Status;
            state.ServerCapiUpdatedUtc = DateTime.UtcNow;
            state.ServerCapiNote = metaCapiResult.Note;
        });

    await TryWriteLeadEventAsync(
        metaCapiResult.Sent ? "capi_event_success" : "capi_event_failure",
        new
        {
            LeadId = lead.LeadId,
            CorrelationId = correlationId,
            EventId = metaLeadEventId,
            Status = metaCapiResult.Status,
            Note = metaCapiResult.Note,
            PixelId = metaCapiResult.PixelId ?? resolvedMetaPixel.PixelId,
            PixelOwnerType = metaCapiResult.PixelOwnerType ?? resolvedMetaPixel.PixelOwnerType
        });

    // ── 2. Send email ──────────────────────────────────────────────────────────
    try
    {
        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        var graphClient = new GraphServiceClient(credential);

        // ===================== BUILD EMAIL BODY =====================
        string emailBody = $@"
<h2>Health Insurance Quote Lead</h2>

<h3>Personal Information</h3>
<p><strong>Name:</strong> {model.FirstName} {model.LastName}</p>
<p><strong>Age:</strong> {model.Age}</p>
<p><strong>Email:</strong> {model.Email}</p>
<p><strong>Phone:</strong> {model.Phone}</p>

<hr />

<h3>Coverage Needs</h3>
<p><strong>Household Size:</strong> {model.HouseholdSize}</p>
<p><strong>Primary Concern:</strong> {model.PrimaryConcern}</p>

<hr />

<h3>Contact Preferences</h3>
<p><strong>Preferred Method:</strong> {model.ContactMethod}</p>
<p><strong>Best Time:</strong> {model.BestTimeToContact}</p>

<hr />

<h3>Disclaimer</h3>
<p><strong>Acknowledged Disclaimer:</strong> {(model.AcknowledgedDisclaimer ? "Acknowledged" : "Not Acknowledged")}</p>
";

        // ===================== APPLY HEADING STYLING =====================
        string headingColor = "#cca134f1";
        string headingFontSize = "1.2em";
        string headingPadding = "4px 6px";

        string ApplyHeadingHighlighting(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return html;

            return System.Text.RegularExpressions.Regex.Replace(
                html,
                @"<\s*(h[34])\s*>(.*?)<\s*/\s*\1\s*>",
                m =>
                {
                    var tag = m.Groups[1].Value;
                    var content = m.Groups[2].Value.Trim();
                    return $"<{tag} style=\"background-color:{headingColor}; font-size:{headingFontSize}; padding:{headingPadding};\">{content}</{tag}>";
                },
                System.Text.RegularExpressions.RegexOptions.Singleline |
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }

        emailBody = ApplyHeadingHighlighting(emailBody);

        // ===================== CREATE GRAPH MESSAGE =====================
        var message = new Message
        {
            Subject = $"[HEALTH QUOTE] New Lead | {model.FirstName} {model.LastName}",
            Body = new ItemBody
            {
                ContentType = BodyType.Html,
                Content = emailBody
            },
            ToRecipients = new List<Recipient>
            {
                new Recipient
                {
                    EmailAddress = new EmailAddress
                    {
                        Address = leadRecipientEmail
                    }
                }
            }
        };

        var requestBody = new SendMailPostRequestBody
        {
            Message = message,
            SaveToSentItems = true
        };

        await graphClient.Users[senderEmail].SendMail.PostAsync(requestBody);
        _logger.LogInformation(
            "HealthQuote [{CorrelationId}]: email sent to {Recipient} for lead {LeadId}",
            correlationId, leadRecipientEmail, lead.LeadId);
    }
    catch (Exception emailEx)
    {
        _logger.LogError(emailEx,
            "HealthQuote [{CorrelationId}]: email send failed for lead {LeadId} — lead is saved, continuing",
            correlationId, lead.LeadId);
    }

    // ── 3. Write analytics event ───────────────────────────────────────────────
    await TryWriteLeadEventAsync(
        "website_lead_submitted",
        new { LeadId = lead.LeadId, CorrelationId = correlationId },
        lead.CreatedUtc);

    TempData["QuoteType"] = "Health";
    TempData["MetaLeadEventId"] = metaLeadEventId;
    TempData["MetaLeadLeadId"] = lead.LeadId.ToString("D");
    return RedirectToAction("Index", "ThankYou");
}

        private async Task<(string RecipientEmail, Guid? AgentProfileId, string? AgentSlug, bool IsFounderPath)> ResolveLeadContextAsync()
        {
            var slug = ResolveExplicitAgentSlugFromRequest();

            if (!string.IsNullOrWhiteSpace(slug))
            {
                var bySlug = await _resolver.ResolveBySlugAsync(slug, HttpContext?.RequestAborted ?? CancellationToken.None);
                if (bySlug.Found && bySlug.Profile != null && !string.IsNullOrWhiteSpace(bySlug.Profile.AgentUpn))
                    return (bySlug.Profile.AgentUpn.Trim(), bySlug.Profile.Id, bySlug.CanonicalSlug, false);
            }

            var isFounderPath = HttpContext?.Items["IsFounderPath"] as bool? == true;
            if (!isFounderPath &&
                HttpContext?.Items.TryGetValue("TrackingProfile", out var trackingProfileObj) == true &&
                trackingProfileObj is AgentTrackingProfile trackingProfile &&
                !string.IsNullOrWhiteSpace(trackingProfile.AgentUpn))
            {
                return (trackingProfile.AgentUpn.Trim(), trackingProfile.Id, trackingProfile.Slug, false);
            }

            return (recipientEmail, null, null, isFounderPath);
        }

        private static string? ExtractSlugFromPath(string? pathOrUrl)
        {
            if (string.IsNullOrWhiteSpace(pathOrUrl)) return null;

            var value = pathOrUrl.Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                value = uri.AbsolutePath;
            }

            var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && string.Equals(segments[0], "a", StringComparison.OrdinalIgnoreCase))
            {
                return segments[1];
            }

            return null;
        }

        private string? ResolveExplicitAgentSlugFromRequest()
        {
            var formSlug = Request?.Form["AgentSlug"].ToString();
            if (!string.IsNullOrWhiteSpace(formSlug))
                return formSlug.Trim();

            return ExtractSlugFromPath(Request?.Path.Value)
                ?? ExtractSlugFromPath(Request?.Headers["Referer"].ToString());
        }
    }
}
