using Microsoft.AspNetCore.Mvc;
using Domain.Entities;
using Infrastructure.Data;
using Protect_Website.Models;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Infrastructure.Leads;
using ProtectWebsite.Services.Meta;
using ProtectWebsite.Services.MetaSignal;
using ProtectWebsite.Services;
using ProtectWebsite.Services.Tracking;
using Microsoft.AspNetCore.WebUtilities;
using ProtectWebsite.Services.Booking;
using ProtectWebsite.Services.Communication;

namespace Protect_Website.Controllers
{
    [Route("Quote")]
    public class DentalVisionHearingQuoteController : Controller
    {
        private const string WebsitePageVariant = "website";
        private const string LandingPageVariant = "landing";
        private const string ContactFirstEducationVariant = "contact_first_education_v1";
        private const string QuotePageKey = "quote_dvh";
        private const string QuoteOfferKey = "dvh";
        private const string QuoteProductType = "dental_vision_hearing";
        private const string QuoteInterestType = "dental_vision_hearing";
        private const string QuoteDisplayName = "Dental, Vision & Hearing Review";

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
        private readonly IPublicBookingResolver _publicBookingResolver;
        private readonly IPublicBookingConfirmationService _publicBookingConfirmationService;
        private readonly IPublicBookingContextProtector _publicBookingContextProtector;
        private readonly ILogger<DentalVisionHearingQuoteController> _logger;
        private readonly IProtectEmailSender _emailSender;

        public DentalVisionHearingQuoteController(IConfiguration configuration, AgentTrackingResolver resolver,
            MasterAppDbContext db, IMetaConversionsApiService metaConversionsApi, IMetaPixelResolutionService metaPixelResolution, IMetaSignalIntelligenceService metaSignalIntelligence, IWebsiteLifeLeadCaptureService websiteLeadCapture, IPublicBookingResolver publicBookingResolver, IPublicBookingConfirmationService publicBookingConfirmationService, IPublicBookingContextProtector publicBookingContextProtector, IProtectEmailSender emailSender, ILogger<DentalVisionHearingQuoteController> logger)
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
            _publicBookingResolver = publicBookingResolver;
            _publicBookingConfirmationService = publicBookingConfirmationService;
            _publicBookingContextProtector = publicBookingContextProtector;
            _emailSender = emailSender;
            _logger = logger;
        }

        // GET: /Quote/Dental-Vision-Hearing
        [HttpGet("Dental-Vision-Hearing")]
        public IActionResult DentalVisionHearingQuote() => RenderDentalVisionHearingQuote(isLandingPage: false);

        // GET: /Quote/Dental-Vision-Hearing/landing
        [HttpGet("Dental-Vision-Hearing/landing")]
        public IActionResult DentalVisionHearingLandingQuote() => RenderDentalVisionHearingQuote(isLandingPage: true);

        // POST: /Quote/Dental-Vision-Hearing
        [HttpPost("Dental-Vision-Hearing")]
        public async Task<IActionResult> SubmitDentalVisionHearingQuote(DentalVisionHearingQuoteFormModel model)
        {
            var correlationId = Guid.NewGuid();
            NormalizeContactFields(model);
            var isLandingMode = ShouldUseLandingMode(model);
            ApplyPageMode(model, isLandingMode);
            var effectivePageKey = ResolveEffectivePageKey(model, isLandingMode);
            model.PageKey = effectivePageKey;

            if (!ModelState.IsValid)
            {
                if (IsAjax())
                {
                    var fieldErrors = CollectModelStateErrors();
                    _logger.LogWarning(
                        "DentalVisionHearingQuote [{CorrelationId}]: invalid ajax submission errors={ValidationErrors}",
                        correlationId,
                        JsonSerializer.Serialize(fieldErrors));
                    return BadRequest(new
                    {
                        error = ResolveAjaxValidationMessage(fieldErrors),
                        fieldErrors,
                        correlationId = correlationId.ToString("D")
                    });
                }

                return RenderDentalVisionHearingQuote(isLandingMode, model);
            }

            _logger.LogInformation(
                "DentalVisionHearingQuote [{CorrelationId}]: request received Email={Email}",
                correlationId, model.Email);

            var (leadRecipientEmail, agentProfileId, agentSlug, isFounderPath) = await ResolveLeadContextAsync();
            var isAgentContext = IsAgentContext();
            _logger.LogInformation(
                "DentalVisionHearingQuote [{CorrelationId}]: attribution resolved AgentSlug={Slug} ProfileId={ProfileId} Recipient={Recipient}",
                correlationId, agentSlug, agentProfileId, leadRecipientEmail);
            var resolvedMetaPixel = await _metaPixelResolution.ResolveForLeadAsync(
                agentProfileId,
                agentSlug,
                isFounderPath,
                HttpContext?.RequestAborted ?? CancellationToken.None);

            // ── 1. Persist lead FIRST ─────────────────────────────────────────────
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
                    InterestType  = QuoteInterestType,
                    SourcePageKey = effectivePageKey,
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
                    CallTextConsent = model.AcknowledgedDisclaimer && !string.IsNullOrWhiteSpace(model.Phone),
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
                        OfferKey       = QuoteOfferKey,
                        ProductType    = QuoteProductType,
                        PageKey        = effectivePageKey,
                        PageVariant    = model.PageVariant,
                        PageMode       = model.PageMode,
                        PagePath       = Request?.Path.Value,
                        Age            = model.Age,
                        AgeRange       = model.AgeRange,
                        State          = model.State,
                        HouseholdSize  = model.HouseholdSize,
                        CurrentCoverage = model.CurrentCoverage,
                        CoverageType   = model.CoverageType,
                        PrimaryConcern = model.PrimaryConcern,
                        ContactMethod  = model.ContactMethod,
                        BestTimeToContact = model.BestTimeToContact,
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
                    "DentalVisionHearingQuote [{CorrelationId}]: WebsiteLead {LeadId} saved",
                    correlationId, lead.LeadId);
            }
            catch (Exception persistEx)
            {
                _logger.LogError(persistEx,
                    "DentalVisionHearingQuote [{CorrelationId}]: lead persistence failed for {Email}",
                    correlationId, model.Email);
                return IsAjax()
                    ? StatusCode(500, new { error = "Failed to save lead", detail = persistEx.Message, correlationId = correlationId.ToString("D") })
                    : RenderDentalVisionHearingQuote(isLandingMode, model);
            }

            async Task TryWriteLeadEventAsync(string eventType, object metadata, DateTime? eventUtc = null)
            {
                AnalyticsEvent? analyticsEvent = null;
                try
                {
                    analyticsEvent = WebsiteLeadAnalyticsWriter.CreateEvent(
                        lead,
                        eventType,
                        effectivePageKey,
                        QuoteInterestType,
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
                        "DentalVisionHearingQuote [{CorrelationId}]: analytics event write failed for {EventType} lead {LeadId}",
                        correlationId,
                        eventType,
                        lead.LeadId);
                }
            }

            await TryWriteLeadEventAsync(
                    "lead_persisted",
                    new
                    {
                        LeadId = lead.LeadId,
                        CorrelationId = correlationId,
                        QuoteType = QuoteInterestType,
                        OfferKey = QuoteOfferKey,
                        ProductType = QuoteProductType,
                        PageVariant = string.IsNullOrWhiteSpace(model.PageVariant) ? WebsitePageVariant : model.PageVariant.Trim(),
                        PageMode = string.IsNullOrWhiteSpace(model.PageMode) ? "site_mode" : model.PageMode.Trim(),
                        PagePath = Request?.Path.Value
                    },
                    lead.CreatedUtc);

            try
            {
                await TryWriteLeadEventAsync(
                    "workstation_capture_attempt",
                    new
                    {
                        LeadId = lead.LeadId,
                        CorrelationId = correlationId,
                        ProductType = QuoteProductType,
                        OfferKey = QuoteOfferKey,
                        PageVariant = string.IsNullOrWhiteSpace(model.PageVariant) ? WebsitePageVariant : model.PageVariant.Trim(),
                        PageMode = string.IsNullOrWhiteSpace(model.PageMode) ? "site_mode" : model.PageMode.Trim(),
                        PagePath = Request?.Path.Value
                    });

                var captureResult = await _websiteLeadCapture.UpsertAsync(
                    new WebsiteLifeLeadCaptureRequest
                    {
                        WebsiteLeadId = lead.LeadId,
                        SubmittedUtc = lead.CreatedUtc,
                        ProductType = QuoteProductType,
                        OfferKey = QuoteOfferKey,
                        FirstName = lead.FirstName,
                        LastName = lead.LastName,
                        Email = lead.Email,
                        Phone = lead.Phone,
                        Age = model.Age,
                        AgeRange = model.AgeRange,
                        AgentTrackingProfileId = agentProfileId,
                        AgentSlug = agentSlug,
                        RecipientEmail = leadRecipientEmail
                    },
                    HttpContext?.RequestAborted ?? CancellationToken.None);

                if (captureResult.Captured)
                {
                    await _db.SaveChangesAsync(HttpContext?.RequestAborted ?? CancellationToken.None);
                    _logger.LogInformation(
                        "DentalVisionHearingQuote [{CorrelationId}]: workstation lead {WorkstationLeadId} {CaptureMode} bucket={Bucket} owner={AgentUserId}",
                        correlationId,
                        captureResult.WorkstationLeadId,
                        captureResult.Created ? "created" : "updated",
                        captureResult.Bucket,
                        captureResult.AgentUserId);
                    await TryWriteLeadEventAsync(
                        "workstation_capture_success",
                        new
                        {
                            LeadId = lead.LeadId,
                            CorrelationId = correlationId,
                            ProductType = QuoteProductType,
                            OfferKey = QuoteOfferKey,
                            PageVariant = string.IsNullOrWhiteSpace(model.PageVariant) ? WebsitePageVariant : model.PageVariant.Trim(),
                            PageMode = string.IsNullOrWhiteSpace(model.PageMode) ? "site_mode" : model.PageMode.Trim(),
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
                            ProductType = QuoteProductType,
                            OfferKey = QuoteOfferKey,
                            PageVariant = string.IsNullOrWhiteSpace(model.PageVariant) ? WebsitePageVariant : model.PageVariant.Trim(),
                            PageMode = string.IsNullOrWhiteSpace(model.PageMode) ? "site_mode" : model.PageMode.Trim(),
                            Reason = captureResult.Reason ?? "unknown",
                            Bucket = captureResult.Bucket,
                            AgentUserId = captureResult.AgentUserId
                        });

                    _logger.LogWarning(
                        "DentalVisionHearingQuote [{CorrelationId}]: workstation lead capture skipped for WebsiteLead {LeadId}. reason={Reason}",
                        correlationId,
                        lead.LeadId,
                        captureResult.Reason ?? "unknown");

                    if (IsAjax())
                    {
                        return StatusCode(500, new
                        {
                            error = "Workstation capture skipped",
                            reason = captureResult.Reason ?? "unknown",
                            bucket = captureResult.Bucket,
                            agentUserId = captureResult.AgentUserId,
                            correlationId = correlationId.ToString("D")
                        });
                    }
                }
            }
            catch (Exception captureEx)
            {
                foreach (var entry in _db.ChangeTracker.Entries<WorkstationLeadProfile>()
                    .Where(x => x.State == EntityState.Added || x.State == EntityState.Modified))
                {
                    entry.State = EntityState.Detached;
                }

                foreach (var entry in _db.ChangeTracker.Entries<WebsiteLeadIntakeLink>()
                    .Where(x => x.State == EntityState.Added || x.State == EntityState.Modified))
                {
                    entry.State = EntityState.Detached;
                }

                _logger.LogError(
                    captureEx,
                    "DentalVisionHearingQuote [{CorrelationId}]: workstation capture failed for lead {LeadId}",
                    correlationId,
                    lead.LeadId);

                await TryWriteLeadEventAsync(
                    "workstation_capture_failure",
                    new
                    {
                        LeadId = lead.LeadId,
                        CorrelationId = correlationId,
                        ProductType = QuoteProductType,
                        OfferKey = QuoteOfferKey,
                        PageVariant = string.IsNullOrWhiteSpace(model.PageVariant) ? WebsitePageVariant : model.PageVariant.Trim(),
                        PageMode = string.IsNullOrWhiteSpace(model.PageMode) ? "site_mode" : model.PageMode.Trim(),
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
                    QuoteType = "DVH",
                    PageKey = effectivePageKey,
                    OfferKey = QuoteOfferKey,
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
                    productType = QuoteProductType,
                    pageVariant = model.PageVariant,
                    pageMode = model.PageMode,
                    pagePath = Request?.Path.Value,
                    requiredContactFieldsComplete = true,
                    contactStepReached = true,
                    phoneCompleted = !string.IsNullOrWhiteSpace(lead.Phone)
                });

                await _metaSignalIntelligence.RecordConfirmedLeadAsync(
                    new MetaSignalConfirmedLeadRequest
                    {
                        LeadId = lead.LeadId,
                        QuoteType = QuoteOfferKey,
                        PageKey = effectivePageKey,
                        EffectivePageKey = effectivePageKey,
                        PageVariant = string.IsNullOrWhiteSpace(model.PageVariant) ? WebsitePageVariant : model.PageVariant.Trim(),
                        PageMode = string.IsNullOrWhiteSpace(model.PageMode) ? "site_mode" : model.PageMode.Trim(),
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
                    "DentalVisionHearingQuote [{CorrelationId}]: meta signal lead recording failed for lead {LeadId} — lead is saved, continuing",
                    correlationId, lead.LeadId);
            }


            var attachedAgentContact = await ResolveAttachedAgentContactAsync(
                agentProfileId,
                agentSlug,
                HttpContext?.RequestAborted ?? CancellationToken.None);

            // ── 2. Send agent/prospect emails through unified sender ───────────────
            string? primary = null;
            if (isAgentContext && !string.IsNullOrWhiteSpace(leadRecipientEmail))
                primary = leadRecipientEmail.Trim();
            else if (!isAgentContext && !string.IsNullOrWhiteSpace(recipientEmail))
                primary = recipientEmail.Trim();
            else if (!string.IsNullOrWhiteSpace(recipientEmail))
                primary = recipientEmail.Trim();
            else if (!string.IsNullOrWhiteSpace(senderEmail))
                primary = senderEmail.Trim();

            if (!string.IsNullOrWhiteSpace(primary))
            {
                var agentEmailSent = await _emailSender.TrySendAsync(
                    primary,
                    $"[DVH QUOTE - {QuoteDisplayName.ToUpperInvariant()}] New Lead | {model.FirstName}",
                    BuildLeadNotificationEmailBody(model),
                    replyToEmail: model.Email,
                    saveToSentItems: true,
                    cancellationToken: HttpContext?.RequestAborted ?? CancellationToken.None);

                if (agentEmailSent)
                {
                    _logger.LogInformation(
                        "DentalVisionHearingQuote [{CorrelationId}]: agent notification email sent to {Recipient} for lead {LeadId}",
                        correlationId, primary, lead.LeadId);
                }
                else
                {
                    _logger.LogError(
                        "DentalVisionHearingQuote [{CorrelationId}]: agent notification email failed to {Recipient} for lead {LeadId} - lead is saved, continuing",
                        correlationId, primary, lead.LeadId);
                }
            }
            else
            {
                _logger.LogWarning(
                    "DentalVisionHearingQuote [{CorrelationId}]: no recipient resolved for lead {LeadId} - email skipped",
                    correlationId, lead.LeadId);
            }

            if (!string.IsNullOrWhiteSpace(model.Email?.Trim()))
            {
                var userEmailSent = await _emailSender.TrySendAsync(
                    model.Email.Trim(),
                    $"Your dental, vision, and hearing review is ready - {websiteName}",
                    BuildUserSummaryEmailBody(model, attachedAgentContact?.FirstName, attachedAgentContact?.BookingUrl),
                    saveToSentItems: false,
                    cancellationToken: HttpContext?.RequestAborted ?? CancellationToken.None);

                if (userEmailSent)
                {
                    _logger.LogInformation(
                        "DentalVisionHearingQuote [{CorrelationId}]: user summary email sent to {Email} for lead {LeadId}",
                        correlationId, model.Email.Trim(), lead.LeadId);
                }
                else
                {
                    _logger.LogError(
                        "DentalVisionHearingQuote [{CorrelationId}]: user summary email failed for lead {LeadId} - lead is saved, continuing",
                        correlationId, lead.LeadId);
                }
            }

            // ── 3. Write analytics event ─────────────────────────────────────────
            await TryWriteLeadEventAsync(
                "website_lead_submitted",
                new
                {
                    LeadId = lead.LeadId,
                    CorrelationId = correlationId,
                    OfferKey = QuoteOfferKey,
                    ProductType = QuoteProductType,
                    PageKey = effectivePageKey,
                    PageVariant = string.IsNullOrWhiteSpace(model.PageVariant) ? WebsitePageVariant : model.PageVariant.Trim(),
                    PageMode = string.IsNullOrWhiteSpace(model.PageMode) ? "site_mode" : model.PageMode.Trim(),
                    PagePath = Request?.Path.Value
                },
                lead.CreatedUtc);

            var publicBookingHint = await BuildPublicBookingAjaxHintAsync(
                lead.LeadId,
                lead.AgentTrackingProfileId,
                agentSlug,
                effectivePageKey,
                QuoteOfferKey,
                HttpContext?.RequestAborted ?? CancellationToken.None);

            if (IsAjax())
            {
                return Ok(new
                {
                    success = true,
                    leadId = lead.LeadId.ToString("D"),
                    metaLeadEventId,
                    metaCapiStatus = metaCapiResult.Status,
                    agentFirstName = attachedAgentContact?.FirstName,
                    bookingUrl = attachedAgentContact?.BookingUrl,
                    booking = publicBookingHint
                });
            }

            TempData["QuoteType"] = "Dental, Vision & Hearing";
            TempData["MetaLeadEventId"] = metaLeadEventId;
            TempData["MetaLeadLeadId"] = lead.LeadId.ToString("D");
            return RedirectToAction("Index", "ThankYou");
        }

        [HttpPost("Dental-Vision-Hearing/booking-experience")]
        public async Task<IActionResult> ActivatePublicBookingExperience([FromBody] PublicBookingExperienceRequest? request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ContextToken))
            {
                return BadRequest(new { error = "Invalid booking context." });
            }

            if (!_publicBookingContextProtector.TryUnprotect(request.ContextToken, out var bookingContext) || bookingContext == null)
            {
                return BadRequest(new { error = "Booking context has expired." });
            }

            var resolution = await _publicBookingResolver.ResolveAsync(
                new PublicBookingResolveContext(
                    WebsiteLeadId: bookingContext.WebsiteLeadId,
                    AgentTrackingProfileId: bookingContext.AgentTrackingProfileId,
                    AgentUserId: bookingContext.AgentUserId,
                    AgentSlug: bookingContext.AgentSlug),
                HttpContext?.RequestAborted ?? CancellationToken.None);
            var requestedSource = NormalizePublicBookingSurface(request.Surface, resolution.HasEmbed);
            var enabled = resolution.Enabled && resolution.HasAnyExperience;
            var embedUrl = enabled && resolution.HasEmbed
                ? AppendBookingContextToken(resolution.EmbedUrl, request.ContextToken)
                : null;
            var fallbackUrl = enabled && resolution.HasFallback
                ? AppendBookingContextToken(resolution.FallbackUrl, request.ContextToken)
                : null;

            LeadAppointment? appointment = null;
            if (enabled)
            {
                try
                {
                    appointment = await UpsertRequestedPublicAppointmentAsync(
                        bookingContext,
                        resolution,
                        requestedSource,
                        HttpContext?.RequestAborted ?? CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "DentalVisionHearingQuote public booking activation failed for WebsiteLead {LeadId}. Returning booking UI without appointment linkage.",
                        bookingContext.WebsiteLeadId);
                }
            }

            return Ok(new
            {
                enabled,
                canEmbed = enabled && resolution.HasEmbed,
                canFallback = enabled && resolution.HasFallback,
                preferModalOnMobile = resolution.PreferModalOnMobile,
                embedUrl,
                fallbackUrl,
                surface = requestedSource,
                linkedToLead = appointment != null,
                appointmentId = appointment?.Id,
                appointmentStatus = appointment?.Status.ToString(),
                requestedBookingSource = appointment?.RequestedBookingSource,
                confirmationSource = appointment?.ConfirmationSource,
                confirmationVerified = IsTrustedBookedAppointment(appointment),
                pendingConfirmation = appointment != null && appointment.Status == Domain.Enums.LeadAppointmentStatus.Requested,
                reason = resolution.Reason,
                bookingConfigSource = resolution.ConfigurationSource,
                bookingConfigAgentSlug = resolution.AgentSlug,
                bookingTrackingProfileId = resolution.AgentTrackingProfileId
            });
        }

        [HttpPost("Dental-Vision-Hearing/booking-confirmation")]
        public async Task<IActionResult> ConfirmPublicBookingExperience([FromBody] PublicBookingExperienceRequest? request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ContextToken))
            {
                return BadRequest(new { error = "Invalid booking context." });
            }

            if (!_publicBookingContextProtector.TryUnprotect(request.ContextToken, out var bookingContext) || bookingContext == null)
            {
                return BadRequest(new { error = "Booking context has expired." });
            }

            var result = await _publicBookingConfirmationService.TryConfirmAsync(
                bookingContext,
                HttpContext?.RequestAborted ?? CancellationToken.None);

            return Ok(new
            {
                linkedToLead = result.LinkedToLead,
                appointmentId = result.AppointmentId,
                appointmentStatus = result.AppointmentStatus,
                bookingSource = result.BookingSource,
                confirmationSource = result.ConfirmationSource,
                confirmationVerified = result.Verified,
                pendingConfirmation = result.PendingConfirmation,
                reason = result.Reason,
                calendarEventId = result.CalendarEventId,
                calendarEventWebLink = result.CalendarEventWebLink,
                scheduledStartUtc = result.ScheduledStartUtc,
                scheduledEndUtc = result.ScheduledEndUtc
            });
        }

        private Dictionary<string, string[]> CollectModelStateErrors()
        {
            return ModelState
                .Where(entry => entry.Value?.Errors.Count > 0)
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value!.Errors
                        .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage) ? "Invalid value." : error.ErrorMessage.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static string ResolveAjaxValidationMessage(IReadOnlyDictionary<string, string[]> fieldErrors)
        {
            if (fieldErrors.Count == 1)
            {
                var onlyError = fieldErrors.FirstOrDefault().Value?.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(onlyError))
                    return onlyError.Trim();
            }

            return "Please complete the highlighted fields and try again.";
        }

        private static string BuildLeadNotificationEmailBody(DentalVisionHearingQuoteFormModel model)
        {
            static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

            var fullName = $"{model.FirstName} {model.LastName}".Trim();
            if (string.IsNullOrWhiteSpace(fullName))
                fullName = "New prospect";

            var ageLabel = !string.IsNullOrWhiteSpace(model.AgeRange)
                ? model.AgeRange.Trim()
                : model.Age?.ToString(CultureInfo.InvariantCulture) ?? "Not provided";

            var householdLabel = string.IsNullOrWhiteSpace(model.HouseholdSize) ? "Not provided" : model.HouseholdSize.Trim();
            var currentCoverageLabel = string.IsNullOrWhiteSpace(model.CurrentCoverage) ? "Not provided" : model.CurrentCoverage.Trim();
            var coverageTypeLabel = string.IsNullOrWhiteSpace(model.CoverageType) ? "Not provided" : model.CoverageType.Trim();
            var primaryConcernLabel = string.IsNullOrWhiteSpace(model.PrimaryConcern) ? "Not provided" : model.PrimaryConcern.Trim();
            var contactMethodLabel = string.IsNullOrWhiteSpace(model.ContactMethod) ? "Not provided" : model.ContactMethod.Trim();
            var bestTimeLabel = string.IsNullOrWhiteSpace(model.BestTimeToContact) ? "Not provided" : model.BestTimeToContact.Trim();
            var stateLine = string.IsNullOrWhiteSpace(model.State)
                ? string.Empty
                : $@"<strong style=""color:#f3d688;"">State:</strong> {H(model.State.Trim())}<br/>";

            var surfacedCopy = currentCoverageLabel switch
            {
                "No current coverage" => "This prospect may be trying to close a dental, vision, and hearing coverage gap before dental, vision, or hearing costs or unexpected dental, vision, or hearing needs hit the household.",
                "Employer plan" => "This prospect may already have an employer option, but still needs help reviewing affordability, provider fit, and uncovered gaps.",
                "Medical only / need DVH" => "This prospect may need a closer DVH benefits review around benefit fit, plan design, and whether a stronger option is available.",
                "Spouse / family plan" => "This prospect may be reviewing whether the current family arrangement still fits dependents, network access, and cost exposure.",
                _ => "This prospect's answers point toward a dental, vision, and hearing coverage decision that still needs guidance around cost, access, and plan fit."
            };

            var timingCopy = primaryConcernLabel switch
            {
                "Lower Monthly Premium" => "Monthly affordability is already front of mind, so waiting can keep the household exposed to the same premium and deductible pressure without more clarity.",
                "Lower Deductible" => "Out-of-pocket exposure matters here, and a delayed review can leave the household carrying the same deductible risk longer than necessary.",
                "Better Doctor Network" => "Doctor and specialist access usually becomes more important once care is needed, so network fit is worth confirming before that pressure increases.",
                "Prescription Coverage" => "Prescription costs rarely get easier by waiting, so medication access and pharmacy fit are worth reviewing now.",
                _ => "Dental, vision, and hearing coverage decisions usually become more urgent once dental, vision, or hearing needs, provider access, or monthly affordability pressure increase."
            };

            return $@"
<!DOCTYPE html>
<html>
<body style=""margin:0;padding:0;background:#f3f4f6;font-family:Arial,Helvetica,sans-serif;"">
<div style=""width:100%;padding:18px 10px;background:#f3f4f6;"">
<div style=""max-width:680px;margin:0 auto;background:#071d3d;border:1px solid rgba(212,175,55,.55);border-radius:18px;overflow:hidden;box-shadow:0 18px 42px rgba(0,0,0,.24);"">

<div style=""padding:18px;background:#061832;border-bottom:1px solid rgba(243,214,136,.24);"">
<div style=""color:#f3d688;font-size:12px;font-weight:900;letter-spacing:.12em;text-transform:uppercase;margin-bottom:8px;"">New lead ready for review</div>
                <div style=""color:#fff8e7;font-size:24px;line-height:1.12;font-weight:900;"">{H(fullName)} submitted a dental, vision, and hearing review.</div>
                <div style=""color:rgba(248,250,252,.82);font-size:14px;line-height:1.45;font-weight:650;margin-top:10px;"">
                The prospect received the review-style follow-up email. Use the answers below to keep the conversation focused on affordability, provider access, and the plan fit this household needs most instead of restarting cold.
                </div>
</div>

<div style=""padding:16px 18px 18px;"">

<div style=""background:#092955;border:1px solid rgba(243,214,136,.36);border-radius:16px;padding:15px;margin-bottom:14px;"">
<div style=""color:#f3d688;font-size:11px;font-weight:900;letter-spacing:.10em;text-transform:uppercase;margin-bottom:8px;"">Prospect contact</div>
<div style=""color:#fff8e7;font-size:20px;line-height:1.2;font-weight:900;margin-bottom:10px;"">{H(fullName)}</div>
<div style=""color:#f8fafc;font-size:14px;line-height:1.55;font-weight:750;"">
<strong style=""color:#f3d688;"">Phone:</strong> {H(model.Phone)}<br/>
<strong style=""color:#f3d688;"">Email:</strong> {H(model.Email)}<br/>
{stateLine}
<strong style=""color:#f3d688;"">Product:</strong> Dental, Vision & Hearing Review<br/>
<strong style=""color:#f3d688;"">Preferred contact:</strong> {H(contactMethodLabel)}
</div>
</div>

<div style=""margin-bottom:14px;"">
<span style=""display:inline-block;padding:7px 10px;border-radius:999px;border:1px solid rgba(243,214,136,.35);background:#071932;color:#fff7de;font-size:12px;font-weight:900;margin:0 5px 7px 0;"">Lead submitted</span>
<span style=""display:inline-block;padding:7px 10px;border-radius:999px;border:1px solid rgba(243,214,136,.35);background:#071932;color:#fff7de;font-size:12px;font-weight:900;margin:0 5px 7px 0;"">Prospect email sent</span>
<span style=""display:inline-block;padding:7px 10px;border-radius:999px;border:1px solid rgba(243,214,136,.35);background:#071932;color:#fff7de;font-size:12px;font-weight:900;margin:0 5px 7px 0;"">Follow-up needed</span>
</div>

<table width=""100%"" cellpadding=""0"" cellspacing=""0"" role=""presentation"" style=""border-collapse:collapse;margin-bottom:12px;"">
<tr>
<td style=""display:block;width:100%;padding:0 0 8px 0;vertical-align:top;"">
<div style=""background:#08254d;border:1px solid rgba(243,214,136,.24);border-radius:16px;padding:15px;"">
<div style=""color:#f3d688;font-size:11px;font-weight:900;letter-spacing:.08em;text-transform:uppercase;margin-bottom:7px;"">What surfaced</div>
<div style=""color:rgba(248,250,252,.84);font-size:14px;line-height:1.42;font-weight:700;"">{H(surfacedCopy)}</div>
</div>
</td>
<td style=""display:block;width:100%;padding:0 0 8px 0;vertical-align:top;"">
<div style=""background:#08254d;border:1px solid rgba(243,214,136,.24);border-radius:16px;padding:15px;"">
<div style=""color:#f3d688;font-size:11px;font-weight:900;letter-spacing:.08em;text-transform:uppercase;margin-bottom:7px;"">Why timing matters</div>
<div style=""color:rgba(248,250,252,.84);font-size:14px;line-height:1.42;font-weight:700;"">{H(timingCopy)}</div>
</div>
</td>
</tr>
</table>

<table width=""100%"" cellpadding=""0"" cellspacing=""0"" role=""presentation"" style=""border-collapse:collapse;margin-bottom:12px;"">
<tr>
<td style=""display:block;width:100%;padding:0 0 8px 0;vertical-align:top;"">
<div style=""background:#061a36;border:1px solid rgba(243,214,136,.18);border-radius:15px;padding:13px;text-align:center;"">
<div style=""color:rgba(248,250,252,.55);font-size:10px;font-weight:900;letter-spacing:.12em;text-transform:uppercase;margin-bottom:7px;"">Age range</div>
<div style=""color:#fff8e7;font-size:23px;font-weight:900;"">{H(ageLabel)}</div>
</div>
</td>
<td style=""display:block;width:100%;padding:0 0 8px 0;vertical-align:top;"">
<div style=""background:#061a36;border:1px solid rgba(243,214,136,.18);border-radius:15px;padding:13px;text-align:center;"">
<div style=""color:rgba(248,250,252,.55);font-size:10px;font-weight:900;letter-spacing:.12em;text-transform:uppercase;margin-bottom:7px;"">Household</div>
<div style=""color:#fff8e7;font-size:21px;font-weight:900;"">{H(householdLabel)}</div>
</div>
</td>
</tr>
</table>

<div style=""background:#061a36;border:1px solid rgba(243,214,136,.18);border-radius:16px;padding:15px;margin-bottom:12px;"">
<div style=""color:#f3d688;font-size:11px;font-weight:900;letter-spacing:.08em;text-transform:uppercase;margin-bottom:8px;"">Review answers</div>
<div style=""color:#f8fafc;font-size:14px;line-height:1.55;font-weight:700;"">
<strong style=""color:#f3d688;"">Current coverage:</strong> {H(currentCoverageLabel)}<br/>
<strong style=""color:#f3d688;"">Coverage goal:</strong> {H(coverageTypeLabel)}<br/>
<strong style=""color:#f3d688;"">Primary concern:</strong> {H(primaryConcernLabel)}<br/>
<strong style=""color:#f3d688;"">Best time to reach:</strong> {H(bestTimeLabel)}<br/>
<strong style=""color:#f3d688;"">Disclaimer acknowledged:</strong> {(model.AcknowledgedDisclaimer ? "Yes" : "No")}
</div>
</div>

<div style=""background:#092955;border:1px solid rgba(243,214,136,.34);border-radius:16px;padding:16px;"">
<div style=""color:#f3d688;font-size:11px;font-weight:900;letter-spacing:.10em;text-transform:uppercase;margin-bottom:8px;"">Agent next step</div>
<div style=""color:#fff8e7;font-size:22px;line-height:1.13;font-weight:900;margin-bottom:9px;"">Follow up from the review they already started.</div>
<div style=""color:rgba(248,250,252,.84);font-size:14px;line-height:1.45;font-weight:650;"">
Start by confirming household fit, provider access, out-of-pocket comfort, and whether the current coverage path still makes sense before discussing plan options.
</div>
</div>

<div style=""margin-top:14px;color:rgba(248,250,252,.55);font-size:12px;line-height:1.5;text-align:center;"">
                Internal agent notification. Prospect-facing email was generated separately from this submission.
                </div>

</div>
</div>
</div>
</body>
        </html>";
        }

        private static string BuildUserSummaryEmailBody(DentalVisionHearingQuoteFormModel model, string? attachedAgentFirstName, string? attachedAgentBookingUrl)
        {
            static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

            var ageLabel = !string.IsNullOrWhiteSpace(model.AgeRange)
                ? model.AgeRange.Trim()
                : model.Age?.ToString(CultureInfo.InvariantCulture) ?? "Not provided";

            var householdLabel = string.IsNullOrWhiteSpace(model.HouseholdSize) ? "Not provided" : model.HouseholdSize.Trim();
            var currentCoverageLabel = string.IsNullOrWhiteSpace(model.CurrentCoverage) ? "Not provided" : model.CurrentCoverage.Trim();
            var coverageTypeLabel = string.IsNullOrWhiteSpace(model.CoverageType) ? "Not provided" : model.CoverageType.Trim();
            var primaryConcernLabel = string.IsNullOrWhiteSpace(model.PrimaryConcern) ? "Not provided" : model.PrimaryConcern.Trim();

            var surfacedCopy = currentCoverageLabel switch
            {
                "No current coverage" => "You may be trying to close a coverage gap before dental, vision, or hearing needs or unexpected costs put pressure on the household.",
                "Employer plan" => "You may already have an employer option, but still need help checking affordability, doctor access, and uncovered gaps.",
                "Medical only / need DVH" => "You may need a closer DVH benefits review around benefit fit, out-of-pocket costs, and whether a stronger option is available.",
                "Spouse / family plan" => "You may be reviewing whether the current family setup still fits dependents, providers, and out-of-pocket exposure.",
                _ => "Your answers point toward a dental, vision, and hearing coverage decision that still needs a clearer side-by-side review."
            };

            var timingCopy = primaryConcernLabel switch
            {
                "Lower Monthly Premium" => "Monthly affordability matters now, so reviewing sooner can help you avoid carrying the same premium pressure without better clarity.",
                "Lower Deductible" => "If out-of-pocket exposure matters most, it helps to confirm the deductible tradeoff before a care need makes that decision feel urgent.",
                "Better Doctor Network" => "Provider access becomes more important once appointments and care are already in motion, so network fit is worth confirming now.",
                "Prescription Coverage" => "Prescription costs rarely get easier by waiting, so it helps to review medication access and pharmacy fit before needs increase.",
                _ => "Dental, vision, and hearing coverage decisions usually feel more urgent once dental, vision, or hearing needs, provider access, or monthly cost pressure start to build."
            };

            var nextStepName = !string.IsNullOrWhiteSpace(attachedAgentFirstName)
                ? attachedAgentFirstName.Trim()
                : "your licensed agent";

            var bookingUrl = !string.IsNullOrWhiteSpace(attachedAgentBookingUrl)
                ? attachedAgentBookingUrl.Trim()
                : string.Empty;

            var bookingButtonHtml = !string.IsNullOrWhiteSpace(bookingUrl)
                ? $@"<a href=""{WebUtility.HtmlEncode(bookingUrl)}"" style=""display:block;text-align:center;background:#f3d688;color:#111827;text-decoration:none;font-size:16px;font-weight:900;padding:14px 16px;border-radius:14px;border:1px solid #f7dc96;"">Complete My DVH Review</a>"
                : @"<div style=""text-align:center;background:#071932;color:#fff8e7;font-size:15px;font-weight:800;padding:14px 16px;border-radius:14px;border:1px solid rgba(243,214,136,.35);"">Your review is saved. Your licensed agent can help confirm the next step.</div>";

            return $@"
<!DOCTYPE html>
<html>
<body style=""margin:0;padding:0;background:#f3f4f6;font-family:Arial,Helvetica,sans-serif;"">
<div style=""width:100%;padding:24px 12px;"">
<div style=""max-width:680px;margin:0 auto;background:#071d3d;border:1px solid rgba(212,175,55,.55);border-radius:16px;overflow:hidden;box-shadow:0 20px 48px rgba(0,0,0,.25);"">

<div style=""padding:18px 18px 16px;background:linear-gradient(180deg,#10284f 0%,#071d3d 100%);border-bottom:1px solid rgba(212,175,55,.22);"">
<div style=""color:#f3d688;font-size:12px;font-weight:900;letter-spacing:.12em;text-transform:uppercase;margin-bottom:10px;"">
Review Ready
</div>

<div style=""color:#fff8e7;font-size:24px;line-height:1.14;font-weight:900;max-width:560px;"">
Your dental, vision, and hearing review is ready.
</div>

<div style=""margin-top:16px;color:rgba(248,250,252,.84);font-size:14px;line-height:1.45;font-weight:650;max-width:580px;"">
Your answers have been saved so the next conversation can start with your household needs, your current coverage, and the tradeoffs that matter most instead of starting from scratch.
</div>

<div style=""margin-top:18px;padding-top:16px;border-top:1px solid rgba(255,255,255,.10);"">
<span style=""display:inline-block;padding:9px 12px;border-radius:999px;border:1px solid rgba(243,214,136,.35);background:#071932;color:#fff7de;font-size:12px;font-weight:900;margin:0 6px 8px 0;"">Review saved</span>
<span style=""display:inline-block;padding:9px 12px;border-radius:999px;border:1px solid rgba(243,214,136,.35);background:#071932;color:#fff7de;font-size:12px;font-weight:900;margin:0 6px 8px 0;"">Licensed guidance</span>
<span style=""display:inline-block;padding:9px 12px;border-radius:999px;border:1px solid rgba(243,214,136,.35);background:#071932;color:#fff7de;font-size:12px;font-weight:900;margin:0 6px 8px 0;"">No obligation</span>
</div>
</div>

<div style=""padding:16px 18px 18px;"">

<table width=""100%"" cellpadding=""0"" cellspacing=""0"" role=""presentation"" style=""border-collapse:collapse;margin-bottom:16px;"">
<tr>
<td style=""display:block;width:100%;padding:0 0 10px 0;vertical-align:top;"">
<div style=""background:#08254d;border:1px solid rgba(243,214,136,.24);border-radius:18px;padding:16px;"">
<div style=""color:#f3d688;font-size:11px;font-weight:900;letter-spacing:.08em;text-transform:uppercase;margin-bottom:8px;"">
What surfaced
</div>
<div style=""color:#f8fafc;font-size:14px;line-height:1.38;font-weight:800;"">
{H(surfacedCopy)}
</div>
</div>
</td>

<td style=""display:block;width:100%;padding:0 0 10px 0;vertical-align:top;"">
<div style=""background:#08254d;border:1px solid rgba(243,214,136,.24);border-radius:18px;padding:16px;"">
<div style=""color:#f3d688;font-size:11px;font-weight:900;letter-spacing:.08em;text-transform:uppercase;margin-bottom:8px;"">
Why timing matters
</div>
<div style=""color:#f8fafc;font-size:14px;line-height:1.38;font-weight:800;"">
{H(timingCopy)}
</div>
</div>
</td>
</tr>
</table>

<table width=""100%"" cellpadding=""0"" cellspacing=""0"" role=""presentation"" style=""border-collapse:collapse;margin-bottom:16px;"">
<tr>
<td style=""display:block;width:100%;padding:0 0 10px 0;vertical-align:top;"">
<div style=""background:#061a36;border:1px solid rgba(243,214,136,.18);border-radius:16px;padding:15px;text-align:center;"">
<div style=""color:rgba(248,250,252,.55);font-size:11px;font-weight:900;letter-spacing:.12em;text-transform:uppercase;margin-bottom:8px;"">
Age Range
</div>
<div style=""color:#fff8e7;font-size:24px;font-weight:900;"">
{H(ageLabel)}
</div>
</div>
</td>

<td style=""display:block;width:100%;padding:0 0 10px 0;vertical-align:top;"">
<div style=""background:#061a36;border:1px solid rgba(243,214,136,.18);border-radius:16px;padding:15px;text-align:center;"">
<div style=""color:rgba(248,250,252,.55);font-size:11px;font-weight:900;letter-spacing:.12em;text-transform:uppercase;margin-bottom:8px;"">
Household
</div>
<div style=""color:#fff8e7;font-size:23px;font-weight:900;"">
{H(householdLabel)}
</div>
</div>
</td>
</tr>
</table>

<div style=""background:#08254d;border:1px solid rgba(243,214,136,.20);border-radius:18px;padding:18px;margin-bottom:18px;"">
<div style=""color:#fff8e7;font-size:14px;line-height:1.42;font-weight:700;"">
This review is meant to narrow the plans worth discussing first, based on your doctors, prescriptions, household setup, and budget pressure.
</div>

<div style=""margin-top:10px;color:rgba(248,250,252,.84);font-size:15px;line-height:1.55;font-weight:600;"">
The next step is confirming whether the current path still fits, where the biggest tradeoffs are, and what coverage options deserve a closer look.
</div>
</div>

<table width=""100%"" cellpadding=""0"" cellspacing=""0"" role=""presentation"" style=""border-collapse:collapse;margin-bottom:18px;"">
<tr>
<td style=""display:block;width:100%;padding:0 0 10px 0;vertical-align:top;"">
<div style=""background:#061a36;border:1px solid rgba(243,214,136,.18);border-radius:16px;padding:15px;text-align:center;"">
<div style=""color:rgba(248,250,252,.55);font-size:11px;font-weight:900;letter-spacing:.12em;text-transform:uppercase;margin-bottom:8px;"">
Current Coverage
</div>
<div style=""color:#fff8e7;font-size:15px;font-weight:900;line-height:1.25;"">
{H(currentCoverageLabel)}
</div>
</div>
</td>

<td style=""display:block;width:100%;padding:0 0 10px 0;vertical-align:top;"">
<div style=""background:#061a36;border:1px solid rgba(243,214,136,.18);border-radius:16px;padding:15px;text-align:center;"">
<div style=""color:rgba(248,250,252,.55);font-size:11px;font-weight:900;letter-spacing:.12em;text-transform:uppercase;margin-bottom:8px;"">
Review Focus
</div>
<div style=""color:#fff8e7;font-size:15px;font-weight:900;line-height:1.25;"">
{H(coverageTypeLabel)}<br/>{H(primaryConcernLabel)}
</div>
</div>
</td>
</tr>
</table>

<div style=""background:#092955;border:1px solid rgba(243,214,136,.32);border-radius:16px;padding:20px;"">
<div style=""color:#f3d688;font-size:11px;font-weight:900;letter-spacing:.10em;text-transform:uppercase;margin-bottom:8px;"">
Finish The Review
</div>

<div style=""color:#fff8e7;font-size:24px;line-height:1.14;font-weight:900;margin-bottom:12px;"">
Choose a time to confirm the best fit.
</div>

<div style=""color:rgba(248,250,252,.84);font-size:15px;line-height:1.55;font-weight:650;margin-bottom:18px;"">
{H(nextStepName)} can help confirm affordability, out-of-pocket comfort, provider access, and which dental, vision, and hearing coverage path makes the most sense next.
</div>

{bookingButtonHtml}
</div>

<div style=""margin-top:18px;color:rgba(248,250,252,.55);font-size:12px;line-height:1.5;text-align:center;"">
Review summary only. Final plan availability, pricing, provider networks, and eligibility vary by location, carrier, and underwriting rules.
</div>

</div>
</div>
</div>
</body>
</html>";
        }

        private sealed record AttachedAgentContactInfo(string? FirstName, string? BookingUrl);

        private async Task<AttachedAgentContactInfo?> ResolveAttachedAgentContactAsync(Guid? agentProfileId, string? agentSlug, CancellationToken cancellationToken)
        {
            AgentTrackingProfile? trackingProfile = HttpContext?.Items["TrackingProfile"] as AgentTrackingProfile;

            if (trackingProfile == null && agentProfileId.HasValue)
            {
                trackingProfile = await _db.AgentTrackingProfiles.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == agentProfileId.Value, cancellationToken);
            }

            if (trackingProfile == null && !string.IsNullOrWhiteSpace(agentSlug))
            {
                var resolved = await _resolver.ResolveBySlugAsync(agentSlug.Trim(), cancellationToken);
                if (resolved.Found)
                {
                    trackingProfile = resolved.Profile;
                }
            }

            var defaultFirstName = ResolveAgentFirstName(trackingProfile?.DisplayName ?? trackingProfile?.Slug);
            if (trackingProfile == null)
            {
                return new AttachedAgentContactInfo(defaultFirstName, null);
            }

            var agentProfile = await ResolveAgentProfileAsync(trackingProfile, cancellationToken);
            var firstName = ResolveAgentFirstName(agentProfile?.FullName ?? trackingProfile.DisplayName ?? trackingProfile.Slug);
            var bookingUrl = ResolveAgentBookingUrl(agentProfile);

            return new AttachedAgentContactInfo(firstName, bookingUrl);
        }

        private async Task<AgentProfile?> ResolveAgentProfileAsync(AgentTrackingProfile trackingProfile, CancellationToken cancellationToken)
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
                .ToListAsync(cancellationToken);

            return candidates
                .OrderByDescending(x => !string.IsNullOrWhiteSpace(x.FullName))
                .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.FallbackBookingUrl))
                .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.MicrosoftBookingsEmbedUrl))
                .ThenByDescending(x => x.UpdatedUtc)
                .FirstOrDefault();
        }

        private static string? ResolveAgentBookingUrl(AgentProfile? agentProfile)
        {
            if (agentProfile == null)
            {
                return null;
            }

            var candidates = new[]
            {
                agentProfile.FallbackBookingUrl,
                agentProfile.MicrosoftBookingsEmbedUrl
            };

            return candidates
                .Select(value => value?.Trim())
                .FirstOrDefault(value =>
                    !string.IsNullOrWhiteSpace(value) &&
                    (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)));
        }

        private static string ResolveAgentFirstName(string? displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return "our licensed team";
            }

            var first = displayName
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
                ?.Trim();

            return string.IsNullOrWhiteSpace(first) ? "our licensed team" : first;
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
            if (!string.IsNullOrWhiteSpace(formSlug)) return formSlug.Trim();
            return ExtractSlugFromPath(Request?.Path.Value)
                ?? ExtractSlugFromPath(Request?.Headers["Referer"].ToString());
        }

        private bool IsAgentContext()
        {
            string? slug = null;
            var formSlug = Request?.Form["AgentSlug"].ToString();
            if (!string.IsNullOrWhiteSpace(formSlug)) slug = formSlug.Trim();
            if (string.IsNullOrWhiteSpace(slug)) slug = ExtractSlugFromPath(Request?.Path.Value);
            if (string.IsNullOrWhiteSpace(slug)) slug = ExtractSlugFromPath(Request?.Headers["Referer"].ToString());
            return !string.IsNullOrWhiteSpace(slug);
        }

        private bool IsAjax()
        {
            var hdr = Request?.Headers["X-Requested-With"].ToString();
            return !string.IsNullOrWhiteSpace(hdr) &&
                   (hdr.Contains("fetch", StringComparison.OrdinalIgnoreCase) ||
                    hdr.Contains("xmlhttprequest", StringComparison.OrdinalIgnoreCase));
        }

        private static void NormalizeContactFields(DentalVisionHearingQuoteFormModel model)
        {
            model.FirstName = model.FirstName?.Trim() ?? string.Empty;
            model.LastName = string.IsNullOrWhiteSpace(model.LastName) ? null : model.LastName.Trim();
            model.Email = string.IsNullOrWhiteSpace(model.Email) ? null : model.Email.Trim();
            model.Phone = model.Phone?.Trim() ?? string.Empty;
            model.State = string.IsNullOrWhiteSpace(model.State) ? null : model.State.Trim();
            model.ContactMethod = ResolveDerivedContactMethod(model.Phone, model.Email, model.ContactMethod);
            model.BestTimeToContact = string.IsNullOrWhiteSpace(model.BestTimeToContact)
                ? "Not specified"
                : model.BestTimeToContact.Trim();
        }

        private static string ResolveDerivedContactMethod(string? phone, string? email, string? existingValue)
        {
            if (!string.IsNullOrWhiteSpace(existingValue))
            {
                return existingValue.Trim();
            }

            if (!string.IsNullOrWhiteSpace(phone))
            {
                return "Phone";
            }

            if (!string.IsNullOrWhiteSpace(email))
            {
                return "Email";
            }

            return "Phone";
        }

        private async Task<LeadAppointment?> UpsertRequestedPublicAppointmentAsync(
            PublicBookingContext bookingContext,
            PublicBookingResolution resolution,
            string bookingSource,
            CancellationToken ct)
        {
            var intakeLink = await _db.WebsiteLeadIntakeLinks
                .FirstOrDefaultAsync(
                    x => x.WebsiteLeadPublicId == bookingContext.WebsiteLeadId,
                    ct);

            if (intakeLink == null ||
                string.IsNullOrWhiteSpace(intakeLink.WorkstationLeadId) ||
                string.IsNullOrWhiteSpace(intakeLink.AgentUserId))
            {
                return null;
            }

            var appointment = await _db.LeadAppointments
                .Where(x =>
                    x.WorkstationLeadId == intakeLink.WorkstationLeadId &&
                    (x.WebsiteLeadIntakeLinkId == intakeLink.Id ||
                     x.BookingSource == LeadAppointmentBookingSources.WebsiteEmbed ||
                     x.BookingSource == LeadAppointmentBookingSources.WebsiteModal ||
                     x.BookingSource == LeadAppointmentBookingSources.ExternalRedirectFallback))
                .OrderByDescending(x => x.UpdatedUtc)
                .ThenByDescending(x => x.CreatedUtc)
                .FirstOrDefaultAsync(ct);

            if (appointment != null &&
                (appointment.Status == Domain.Enums.LeadAppointmentStatus.Booked ||
                 appointment.Status == Domain.Enums.LeadAppointmentStatus.Confirmed ||
                 appointment.Status == Domain.Enums.LeadAppointmentStatus.Completed))
            {
                return appointment;
            }

            var nowUtc = DateTime.UtcNow;
            if (appointment == null)
            {
                appointment = new LeadAppointment
                {
                    Id = Guid.NewGuid(),
                    WorkstationLeadId = intakeLink.WorkstationLeadId,
                    OwnerAgentUserId = intakeLink.AgentUserId,
                    WebsiteLeadIntakeLinkId = intakeLink.Id,
                    BookingSource = bookingSource,
                    RequestedBookingSource = bookingSource,
                    CreatedUtc = nowUtc,
                    UpdatedUtc = nowUtc
                };
                ApplyResolvedBookingConfig(appointment, resolution);
                appointment.ApplyStatus(Domain.Enums.LeadAppointmentStatus.Requested, nowUtc);
                _db.LeadAppointments.Add(appointment);
            }
            else
            {
                appointment.WorkstationLeadId = intakeLink.WorkstationLeadId;
                appointment.OwnerAgentUserId = intakeLink.AgentUserId;
                appointment.WebsiteLeadIntakeLinkId = intakeLink.Id;
                appointment.BookingSource = bookingSource;
                appointment.RequestedBookingSource = bookingSource;
                appointment.ConfirmationSource = null;
                ApplyResolvedBookingConfig(appointment, resolution);
                appointment.ApplyStatus(Domain.Enums.LeadAppointmentStatus.Requested, nowUtc);
            }

            await _db.SaveChangesAsync(ct);
            return appointment;
        }

        private async Task<PublicBookingAjaxHint> BuildPublicBookingAjaxHintAsync(
            Guid websiteLeadId,
            Guid? agentTrackingProfileId,
            string? agentSlug,
            string pageKey,
            string quoteType,
            CancellationToken ct)
        {
            var resolution = await _publicBookingResolver.ResolveAsync(
                new PublicBookingResolveContext(
                    WebsiteLeadId: websiteLeadId,
                    AgentTrackingProfileId: agentTrackingProfileId,
                    AgentSlug: agentSlug),
                ct);
            var eligible = resolution.Enabled && resolution.HasAnyExperience;
            var contextToken = eligible
                ? _publicBookingContextProtector.Protect(new PublicBookingContext(
                    WebsiteLeadId: websiteLeadId,
                    AgentSlug: resolution.AgentSlug ?? agentSlug,
                    QuoteType: quoteType,
                    PageKey: pageKey,
                    IssuedUtc: DateTime.UtcNow,
                    AgentTrackingProfileId: resolution.AgentTrackingProfileId,
                    AgentUserId: resolution.AgentUserId))
                : null;
            var embedUrl = eligible && resolution.HasEmbed
                ? AppendBookingContextToken(resolution.EmbedUrl, contextToken)
                : null;
            var fallbackUrl = eligible && resolution.HasFallback
                ? AppendBookingContextToken(resolution.FallbackUrl, contextToken)
                : null;

            return new PublicBookingAjaxHint(
                Eligible: eligible,
                CanEmbed: eligible && resolution.HasEmbed,
                CanFallback: eligible && resolution.HasFallback,
                PreferModalOnMobile: resolution.PreferModalOnMobile,
                ContextToken: contextToken,
                EmbedUrl: embedUrl,
                FallbackUrl: fallbackUrl,
                BookingConfigSource: resolution.ConfigurationSource,
                BookingConfigAgentSlug: resolution.AgentSlug,
                BookingTrackingProfileId: resolution.AgentTrackingProfileId);
        }

        private static string NormalizePublicBookingSurface(string? surface, bool hasEmbed)
        {
            var normalized = surface?.Trim();
            if (string.Equals(normalized, LeadAppointmentBookingSources.WebsiteModal, StringComparison.OrdinalIgnoreCase))
                return LeadAppointmentBookingSources.WebsiteModal;
            if (string.Equals(normalized, LeadAppointmentBookingSources.ExternalRedirectFallback, StringComparison.OrdinalIgnoreCase))
                return LeadAppointmentBookingSources.ExternalRedirectFallback;
            if (string.Equals(normalized, LeadAppointmentBookingSources.WebsiteEmbed, StringComparison.OrdinalIgnoreCase))
                return LeadAppointmentBookingSources.WebsiteEmbed;

            return hasEmbed
                ? LeadAppointmentBookingSources.WebsiteEmbed
                : LeadAppointmentBookingSources.ExternalRedirectFallback;
        }

        private static void ApplyResolvedBookingConfig(LeadAppointment appointment, PublicBookingResolution resolution)
        {
            appointment.BookingConfigurationSource = resolution.ConfigurationSource;
            appointment.BookingTrackingProfileId = resolution.AgentTrackingProfileId;
            appointment.BookingAgentSlug = resolution.AgentSlug;
            appointment.BookingAgentUserId = resolution.AgentUserId;
            appointment.BookingCalendarUserId = resolution.CalendarUserId;
            appointment.BookingCalendarEmail = resolution.CalendarEmail;
            appointment.BookingPageIdOrMailbox = resolution.BookingPageIdOrMailbox;
        }

        private static bool IsTrustedBookedAppointment(LeadAppointment? appointment)
        {
            if (appointment == null)
            {
                return false;
            }

            var trustedSource = appointment.ConfirmationSource ?? appointment.BookingSource;
            return appointment.Status is Domain.Enums.LeadAppointmentStatus.Booked or Domain.Enums.LeadAppointmentStatus.Confirmed or Domain.Enums.LeadAppointmentStatus.Completed &&
                   (string.Equals(trustedSource, LeadAppointmentBookingSources.InternalCalendar, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(trustedSource, LeadAppointmentBookingSources.MicrosoftGraphConfirmation, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(trustedSource, LeadAppointmentBookingSources.ManualVerified, StringComparison.OrdinalIgnoreCase));
        }

        private static string? AppendBookingContextToken(string? url, string? contextToken)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(contextToken))
            {
                return url;
            }

            var trimmedUrl = url.Trim();

            // Do not append internal tracking/context parameters to external booking providers.
            // Microsoft Bookings/Outlook iframe URLs can fail or refuse to render when unknown query params are added.
            if (Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var parsedUri))
            {
                var host = parsedUri.Host.ToLowerInvariant();
                if (host.EndsWith("outlook.office.com", StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith("book.ms", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmedUrl;
                }
            }

            try
            {
                return QueryHelpers.AddQueryString(trimmedUrl, "booking_ctx", contextToken.Trim());
            }
            catch
            {
                return trimmedUrl;
            }
        }

        private IActionResult RenderDentalVisionHearingQuote(bool isLandingPage, DentalVisionHearingQuoteFormModel? model = null)
        {
            var viewModel = model ?? new DentalVisionHearingQuoteFormModel();
            ApplyPageMode(viewModel, isLandingPage);
            viewModel.PageKey = ResolveEffectivePageKey(viewModel, isLandingPage);
            return View("~/Views/Quote/DentalVisionHearing.cshtml", viewModel);
        }

        private static string ResolveEffectivePageKey(DentalVisionHearingQuoteFormModel model, bool isLandingPage) =>
            BuildVariantPageKey(QuotePageKey, isLandingPage, model.PageVariant);

        private static string BuildVariantPageKey(string basePageKey, bool isLandingPage, string? pageVariant = null)
        {
            if (!isLandingPage)
                return basePageKey;

            var normalizedVariant = string.IsNullOrWhiteSpace(pageVariant)
                ? null
                : pageVariant.Trim().ToLowerInvariant();
            var isControlLanding =
                string.IsNullOrWhiteSpace(normalizedVariant) ||
                string.Equals(normalizedVariant, LandingPageVariant, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedVariant, ContactFirstEducationVariant, StringComparison.OrdinalIgnoreCase);

            return isControlLanding
                ? $"{basePageKey}_landing"
                : $"{basePageKey}_landing_{normalizedVariant}";
        }

        private static void ApplyPageMode(DentalVisionHearingQuoteFormModel model, bool isLandingPage)
        {
            model.PageVariant = string.IsNullOrWhiteSpace(model.PageVariant)
                ? (isLandingPage ? ContactFirstEducationVariant : WebsitePageVariant)
                : model.PageVariant.Trim();

            model.PageMode = string.IsNullOrWhiteSpace(model.PageMode)
                ? (isLandingPage ? "paid_landing" : "site_mode")
                : model.PageMode.Trim();
        }

        private bool ShouldUseLandingMode(DentalVisionHearingQuoteFormModel? model)
        {
            if (string.Equals(model?.PageMode, "paid_landing", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrWhiteSpace(model?.PageKey) &&
                model.PageKey.Contains($"{QuotePageKey}_landing", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var requestPath = Request?.Path.Value;
            if (!string.IsNullOrWhiteSpace(requestPath) &&
                requestPath.Contains("/landing", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var referer = Request?.Headers["Referer"].ToString();
            return !string.IsNullOrWhiteSpace(referer) &&
                   referer.Contains("/Quote/Dental-Vision-Hearing/landing", StringComparison.OrdinalIgnoreCase);
        }

        public sealed class PublicBookingExperienceRequest
        {
            public string? ContextToken { get; set; }
            public string? Surface { get; set; }
        }

        private sealed record PublicBookingAjaxHint(
            bool Eligible,
            bool CanEmbed,
            bool CanFallback,
            bool PreferModalOnMobile,
            string? ContextToken,
            string? EmbedUrl,
            string? FallbackUrl,
            string? BookingConfigSource,
            string? BookingConfigAgentSlug,
            Guid? BookingTrackingProfileId);
    }
}
