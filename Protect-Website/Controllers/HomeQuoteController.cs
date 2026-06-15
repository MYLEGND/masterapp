using Microsoft.AspNetCore.Mvc;
using Domain.Entities;
using Infrastructure.Data;
using Protect_Website.Models;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Infrastructure.Leads;
using ProtectWebsite.Services.Meta;
using ProtectWebsite.Services.MetaSignal;
using ProtectWebsite.Services;
using ProtectWebsite.Services.Tracking;
using ProtectWebsite.Services.Communication;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Protect_Website.Controllers
{
    [Route("Quote")]
    public class HomeQuoteController : Controller
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
        private readonly IMetaSignalIntelligenceService _metaSignalIntelligence;
        private readonly IWebsiteLifeLeadCaptureService _websiteLeadCapture;
        private readonly ILogger<HomeQuoteController> _logger;
        private readonly IProtectEmailSender _emailSender;

        public HomeQuoteController(IConfiguration configuration, AgentTrackingResolver resolver,
            MasterAppDbContext db, IMetaConversionsApiService metaConversionsApi, IMetaPixelResolutionService metaPixelResolution, IMetaSignalIntelligenceService metaSignalIntelligence, IWebsiteLifeLeadCaptureService websiteLeadCapture, IProtectEmailSender emailSender, ILogger<HomeQuoteController> logger)
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
            _metaSignalIntelligence = metaSignalIntelligence;
            _websiteLeadCapture = websiteLeadCapture;
            _emailSender = emailSender;
            _logger = logger;
        }


        // GET: /Quote/Home
        [HttpGet("Home")]
        public IActionResult HomeQuote()
        {
            return View("~/Views/Quote/Home.cshtml");
        }

        // POST: /Quote/Home
        [HttpPost("Home")]
        public async Task<IActionResult> SubmitHomeQuote(HomeQuoteFormModel model)
        {
            // Normalize disclaimer — wizard steps toggle disabled; read raw value directly
            var ackRaw = Request?.Form?["AcknowledgedDisclaimer"].FirstOrDefault();
            bool ack = !string.IsNullOrWhiteSpace(ackRaw) &&
                       (ackRaw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                        ackRaw.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                        ackRaw.Equals("1"));
            model.AcknowledgedDisclaimer = ack;
            ModelState.Remove(nameof(model.AcknowledgedDisclaimer));
	            if (!ack)
	                ModelState.AddModelError(nameof(model.AcknowledgedDisclaimer),
	                    "Please check the authorization box so we can contact you about this quote.");
	
	            if (!ModelState.IsValid)
	            {
	                ViewData["StartStep"] = ResolveStartStep(ModelState);
	                return View("~/Views/Quote/Home.cshtml", model);
	            }

            var correlationId = Guid.NewGuid();
            _logger.LogInformation(
                "HomeQuote [{CorrelationId}]: request received Email={Email}",
                correlationId, model.EmailAddress);

            var (leadRecipientEmail, agentProfileId, agentSlug, isFounderPath) = await ResolveLeadContextAsync();
            _logger.LogInformation(
                "HomeQuote [{CorrelationId}]: attribution resolved AgentSlug={Slug} ProfileId={ProfileId} Recipient={Recipient}",
                correlationId, agentSlug, agentProfileId, leadRecipientEmail);
            var resolvedMetaPixel = await _metaPixelResolution.ResolveForLeadAsync(
                agentProfileId,
                agentSlug,
                isFounderPath,
                HttpContext?.RequestAborted ?? CancellationToken.None);

            // ── 1. Persist lead FIRST — never lost even if email or analytics fails ───
            WebsiteLead lead;
            try
            {
                var now = DateTime.UtcNow;
                lead = new WebsiteLead
                {
                    LeadId        = Guid.NewGuid(),
                    FirstName     = model.FirstName?.Trim() ?? "",
                    LastName      = string.IsNullOrWhiteSpace(model.LastName) ? null : model.LastName?.Trim(),
                    Email         = model.EmailAddress?.Trim() ?? "",
                    Phone         = string.IsNullOrWhiteSpace(model.PhoneNumber) ? null : model.PhoneNumber?.Trim(),
                    InterestType  = "home_insurance",
                    SourcePageKey = "quote_home",
                    UtmSource     = string.IsNullOrWhiteSpace(model.UtmSource)   ? null : model.UtmSource.Trim(),
                    UtmMedium     = string.IsNullOrWhiteSpace(model.UtmMedium)   ? null : model.UtmMedium.Trim(),
                    UtmCampaign   = string.IsNullOrWhiteSpace(model.UtmCampaign) ? null : model.UtmCampaign.Trim(),
                    UtmId         = string.IsNullOrWhiteSpace(model.UtmId) ? null : model.UtmId.Trim(),
                    MetaCampaignId = string.IsNullOrWhiteSpace(model.MetaCampaignId) ? null : model.MetaCampaignId.Trim(),
                    MetaAdSetId   = string.IsNullOrWhiteSpace(model.MetaAdSetId) ? null : model.MetaAdSetId.Trim(),
                    MetaAdId      = string.IsNullOrWhiteSpace(model.MetaAdId) ? null : model.MetaAdId.Trim(),
                    Fbclid        = string.IsNullOrWhiteSpace(model.Fbclid)      ? null : model.Fbclid.Trim(),
                    ClientIpAddress = !string.IsNullOrWhiteSpace(Request?.Headers["CF-Connecting-IP"].ToString())
                        ? Request!.Headers["CF-Connecting-IP"].ToString()
                        : (!string.IsNullOrWhiteSpace(Request?.Headers["X-Forwarded-For"].ToString())
                            ? Request!.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim()
                            : HttpContext?.Connection?.RemoteIpAddress?.ToString()),
                    ClientUserAgent = Request?.Headers["User-Agent"].ToString(),
                    Fbp = Request?.Cookies.TryGetValue("_fbp", out var fbp) == true ? fbp : null,
                    Fbc = Request?.Cookies.TryGetValue("_fbc", out var fbc) == true ? fbc : null,
                    SessionId     = string.IsNullOrWhiteSpace(model.SessionId)   ? null : model.SessionId.Trim(),
                    VisitorId     = string.IsNullOrWhiteSpace(model.VisitorId)   ? null : model.VisitorId.Trim(),
                    MarketingEmailConsent = model.AcknowledgedDisclaimer,
                    CallTextConsent = model.AcknowledgedDisclaimer && !string.IsNullOrWhiteSpace(model.PhoneNumber),
                    TermsAccepted = true,
                    IsInternal    = WebsiteLeadCaptureSafety.ShouldMarkAsInternalTest(Request?.Host.Host),
                    Host          = Request?.Host.ToString(),
                    Environment   = EnvironmentLabelResolver.Resolve(),
                    CreatedUtc    = now,
                    Status        = "New",
                    AgentTrackingProfileId = agentProfileId,
                    AgentSlug     = agentSlug,
                    MetadataJson  = JsonSerializer.Serialize(new
                    {
                        PolicyFormType = model.PolicyFormType,
                        DwellingType   = model.DwellingType,
                        AddressState   = model.AddressState,
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
                    "HomeQuote [{CorrelationId}]: WebsiteLead {LeadId} saved",
                    correlationId, lead.LeadId);
            }
	            catch (Exception persistEx)
	            {
	                _logger.LogError(persistEx,
	                    "HomeQuote [{CorrelationId}]: lead persistence failed for {Email}",
	                    correlationId, model.EmailAddress);
	                ModelState.AddModelError("", $"Failed to save lead: {persistEx.Message}");
	                ViewData["StartStep"] = ResolveStartStep(ModelState);
	                return View("~/Views/Quote/Home.cshtml", model);
	            }

            async Task TryWriteLeadEventAsync(string eventType, object metadata, DateTime? eventUtc = null)
            {
                AnalyticsEvent? analyticsEvent = null;
                try
                {
                    analyticsEvent = WebsiteLeadAnalyticsWriter.CreateEvent(
                        lead,
                        eventType,
                        "quote_home",
                        "home_insurance",
                        metadata,
                        eventUtc);
                    _db.AnalyticsEvents.Add(analyticsEvent);
                    await _db.SaveChangesAsync(HttpContext?.RequestAborted ?? CancellationToken.None);
                }
                catch (Exception analyticsEx)
                {
                    if (analyticsEvent != null)
                    {
                        var entry = _db.Entry(analyticsEvent);
                        if (entry.State != EntityState.Detached)
                            entry.State = EntityState.Detached;
                    }

                    _logger.LogWarning(
                        analyticsEx,
                        "HomeQuote [{CorrelationId}]: analytics event write failed for {EventType} lead {LeadId}",
                        correlationId,
                        eventType,
                        lead.LeadId);
                }
            }

            await TryWriteLeadEventAsync(
                "lead_persisted",
                new { LeadId = lead.LeadId, CorrelationId = correlationId, QuoteType = "home_insurance" },
                lead.CreatedUtc);

            try
            {
                await TryWriteLeadEventAsync(
                    "workstation_capture_attempt",
                    new { LeadId = lead.LeadId, CorrelationId = correlationId, ProductType = "home", OfferKey = "home" });

                var captureResult = await _websiteLeadCapture.UpsertAsync(
                    new WebsiteLifeLeadCaptureRequest
                    {
                        WebsiteLeadId = lead.LeadId,
                        SubmittedUtc = lead.CreatedUtc,
                        ProductType = "home",
                        OfferKey = "home",
                        FirstName = lead.FirstName,
                        LastName = lead.LastName,
                        Email = lead.Email,
                        Phone = lead.Phone,
                        State = model.AddressState,
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
                    "HomeQuote [{CorrelationId}]: workstation capture failed for lead {LeadId}",
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
                    QuoteType = "Home",
                    PageKey = "quote_home",
                    OfferKey = "home",
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

            try
            {
                var signalMetadata = JsonSerializer.SerializeToElement(new
                {
                    productType = "home",
                    pageVariant = "website",
                    pageMode = "site_mode",
                    pagePath = Request?.Path.Value,
                    requiredContactFieldsComplete = true,
                    contactStepReached = true,
                    phoneCompleted = !string.IsNullOrWhiteSpace(lead.Phone)
                });

                await _metaSignalIntelligence.RecordConfirmedLeadAsync(
                    new MetaSignalConfirmedLeadRequest
                    {
                        LeadId = lead.LeadId,
                        QuoteType = "home",
                        PageKey = "quote_home",
                        EffectivePageKey = "quote_home",
                        PageVariant = "website",
                        PageMode = "site_mode",
                        Url = model.LandingPageUrl,
                        Referrer = model.ReferrerUrl,
                        SessionId = lead.SessionId,
                        VisitorId = lead.VisitorId,
                        AgentTrackingProfileId = lead.AgentTrackingProfileId,
                        AgentSlug = lead.AgentSlug,
                        UtmSource = lead.UtmSource,
                        UtmMedium = lead.UtmMedium,
                        UtmCampaign = lead.UtmCampaign,
                        UtmId = lead.UtmId,
                        UtmContent = model.UtmContent,
                        Fbclid = lead.Fbclid,
                        Email = lead.Email,
                        Phone = lead.Phone,
                        AllowHashedContactData = lead.TermsAccepted && lead.MarketingEmailConsent,
                        CreatedUtc = lead.CreatedUtc,
                        LeadEventId = metaLeadEventId,
                        LeadMetaServerSent = metaCapiResult.Sent,
                        LeadMetaServerStatus = metaCapiResult.Status,
                        LeadMetaServerNote = metaCapiResult.Note,
                        PixelId = resolvedMetaPixel.PixelId,
                        AccessToken = resolvedMetaPixel.AccessToken,
                        TestEventCode = resolvedMetaPixel.TestEventCode,
                        PixelOwnerType = resolvedMetaPixel.PixelOwnerType,
                        Metadata = signalMetadata
                    },
                    HttpContext,
                    HttpContext?.RequestAborted ?? CancellationToken.None);
            }
            catch (Exception signalEx)
            {
                _logger.LogError(signalEx,
                    "HomeQuote [{CorrelationId}]: meta signal lead recording failed for lead {LeadId} — lead is saved, continuing",
                    correlationId, lead.LeadId);
            }


            
            var rows = new LeadEmailTemplate.RowBuilder();

// ── 2. Send email through unified sender ───────────────────────────────
            var emailSent = await _emailSender.TrySendAsync(
                leadRecipientEmail,
                $"[HOME QUOTE] New Lead | {model.FirstName} {model.LastName}",
                LeadEmailTemplate.Wrap("New Quote — Home Insurance", rows.ToString()),
                saveToSentItems: true,
                cancellationToken: HttpContext?.RequestAborted ?? CancellationToken.None);

            if (emailSent)
            {
                _logger.LogInformation(
                    "HomeQuote [{CorrelationId}]: email sent to {Recipient} for lead {LeadId}",
                    correlationId, leadRecipientEmail, lead.LeadId);
            }
            else
            {
                _logger.LogError(
                    "HomeQuote [{CorrelationId}]: email failed to {Recipient} for lead {LeadId} - lead is saved, continuing",
                    correlationId, leadRecipientEmail, lead.LeadId);
            }

            // ── 3. Write analytics event (failure does not lose the lead or email) ─
            await TryWriteLeadEventAsync(
                "website_lead_submitted",
                new { LeadId = lead.LeadId, CorrelationId = correlationId },
                lead.CreatedUtc);

            TempData["QuoteType"] = "Home";
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
            if (HttpContext?.Items.TryGetValue("TrackingProfile", out var trackingProfileObj) == true &&
                trackingProfileObj is AgentTrackingProfile trackingProfile)
            {
                var trackingSlug = HttpContext?.Items["TrackingSlug"] as string;
                var trackingRecipient = !string.IsNullOrWhiteSpace(trackingProfile.AgentUpn)
                    ? trackingProfile.AgentUpn.Trim()
                    : recipientEmail;

                return (
                    isFounderPath ? recipientEmail : trackingRecipient,
                    trackingProfile.Id,
                    string.IsNullOrWhiteSpace(trackingSlug) ? trackingProfile.Slug : trackingSlug,
                    isFounderPath);
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

	        private int ResolveStartStep(ModelStateDictionary modelState)
	        {
	            var postedStep = ResolvePostedStep(1);

	            bool HasKey(string key) =>
	                modelState.TryGetValue(key, out var entry) && entry.Errors.Count > 0;

	            if (HasKey(nameof(HomeQuoteFormModel.AcknowledgedDisclaimer)))
	                return 6;

	            if (HasKey(nameof(HomeQuoteFormModel.DwellingCoverage)) ||
	                HasKey(nameof(HomeQuoteFormModel.EstReplacementCost)) ||
	                HasKey(nameof(HomeQuoteFormModel.PersonalLiability)) ||
	                HasKey(nameof(HomeQuoteFormModel.MedicalPayments)))
	                return 5;
	
	            if (HasKey(nameof(HomeQuoteFormModel.DwellingUsage)) ||
	                HasKey(nameof(HomeQuoteFormModel.DwellingType)) ||
	                HasKey(nameof(HomeQuoteFormModel.YearBuilt)) ||
	                HasKey(nameof(HomeQuoteFormModel.RoofingYearUpdated)))
	                return 4;

	            if (HasKey(nameof(HomeQuoteFormModel.PolicyFormType)) ||
	                HasKey(nameof(HomeQuoteFormModel.CurrentPolicyExpirationDate)) ||
	                HasKey(nameof(HomeQuoteFormModel.NewPolicyEffectiveDate)))
	                return 3;

	            if (HasKey(nameof(HomeQuoteFormModel.PrimaryAddress)) ||
	                HasKey(nameof(HomeQuoteFormModel.PrimaryCity)) ||
	                HasKey(nameof(HomeQuoteFormModel.PrimaryPostalCode)) ||
	                HasKey(nameof(HomeQuoteFormModel.EmailAddress)) ||
	                HasKey(nameof(HomeQuoteFormModel.PhoneNumber)))
	                return 2;

	            return postedStep;
	        }

	        private int ResolvePostedStep(int fallbackStep)
	        {
	            var rawStep = Request?.Form["CurrentStep"].FirstOrDefault();
	            return int.TryParse(rawStep, out var step)
	                ? Math.Clamp(step, 1, 6)
	                : fallbackStep;
	        }
	    }
	}
