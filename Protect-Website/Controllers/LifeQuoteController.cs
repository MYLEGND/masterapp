using Microsoft.AspNetCore.Mvc;
using Domain.Entities;
using Infrastructure.Data;
using Protect_Website.Models;
using static Protect_Website.Models.LifeOfferResolver;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Azure.Identity;
using ProtectWebsite.Services.Tracking;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Globalization;
using ProtectWebsite.Services;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Infrastructure.Leads;
using ProtectWebsite.Services.Meta;
using Shared.Meta;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ProtectWebsite.Services.Booking;
using ProtectWebsite.Services.Communication;

namespace Protect_Website.Controllers
{
    [Route("Quote")]
    public class LifeQuoteController : Controller
    {
        private static readonly IReadOnlyDictionary<string, LifeWizardConfig> WizardConfigs = BuildConfigs();
        private const string WebsitePageVariant = "website";
        private const string LandingPageVariant = "landing";
        private const string ContactFirstEducationLandingVariant = "contact_first_education_v1";
        private const string LowFrictionOptionsLandingVariant = "low_friction_options_v1";

        private readonly string tenantId;
        private readonly string clientId;
        private readonly string clientSecret;
        private readonly string senderEmail;
        private readonly string recipientEmail;
        private readonly string websiteName;
        private readonly string trackingApiBase;
        private readonly AgentTrackingResolver _resolver;
        private readonly MasterAppDbContext _db;
        private readonly IMetaPixelResolutionService _metaPixelResolution;
        private readonly IWebsiteLifeLeadCaptureService _websiteLifeLeadCapture;
        private readonly IPublicBookingResolver _publicBookingResolver;
        private readonly IPublicBookingConfirmationService _publicBookingConfirmationService;
        private readonly IPublicBookingContextProtector _publicBookingContextProtector;
        private readonly ILogger<LifeQuoteController> _logger;
        private readonly IProtectEmailSender _emailSender;

        public LifeQuoteController(IConfiguration configuration, AgentTrackingResolver resolver,
            MasterAppDbContext db, IMetaPixelResolutionService metaPixelResolution, IWebsiteLifeLeadCaptureService websiteLifeLeadCapture, IPublicBookingResolver publicBookingResolver, IPublicBookingConfirmationService publicBookingConfirmationService, IPublicBookingContextProtector publicBookingContextProtector, IProtectEmailSender emailSender, ILogger<LifeQuoteController> logger)
        {
            tenantId = configuration["AzureAd:TenantId"]!;
            clientId = configuration["AzureAd:ClientId"]!;
            clientSecret = configuration["AzureAd:ClientSecret"]!;
            senderEmail = configuration["Contact:SenderEmail"] ?? "connect@mylegnd.com";
            recipientEmail = configuration["Contact:RecipientEmail"]!;
            websiteName = configuration["Contact:WebsiteName"] ?? "Legend Legacy Protection";
            trackingApiBase = (configuration["Tracking:ApiBase"] ?? "https://portal.mylegnd.com").TrimEnd('/');
            _resolver = resolver;
            _db = db;
            _metaPixelResolution = metaPixelResolution;
            _websiteLifeLeadCapture = websiteLifeLeadCapture;
            _publicBookingResolver = publicBookingResolver;
            _publicBookingConfirmationService = publicBookingConfirmationService;
            _publicBookingContextProtector = publicBookingContextProtector;
            _emailSender = emailSender;
            _logger = logger;
        }

        // ===================== GET =====================
        [HttpGet("Life")]
        public Task<IActionResult> LifeQuote([FromQuery] string? offer = null)
        {
            if (!string.IsNullOrWhiteSpace(offer))
            {
                var normalizedOffer = LifeOfferResolver.Normalize(offer);
                return Task.FromResult<IActionResult>(Redirect(BuildCanonicalQuoteUrl(normalizedOffer)));
            }

            return RenderWizard(LifeOfferKeys.Life);
        }
        [HttpGet("Life/landing")]
        public Task<IActionResult> LifeLandingQuote() => RenderWizard(LifeOfferKeys.Life, isLandingPage: true);
        [HttpGet("Term-Life")]
        public Task<IActionResult> TermLifeQuote() => RenderWizard("term");
        [HttpGet("Term-Life/landing")]
        public Task<IActionResult> TermLifeLandingQuote() => RenderWizard(LifeOfferKeys.Term, isLandingPage: true);
        [HttpGet("Whole-Life")]
        public Task<IActionResult> WholeLifeQuote() => RenderWizard("wholelife");
        [HttpGet("Whole-Life/landing")]
        public Task<IActionResult> WholeLifeLandingQuote() => RenderWizard(LifeOfferKeys.WholeLife, isLandingPage: true);
        [HttpGet("Final-Expense")]
        public Task<IActionResult> FinalExpenseQuote() => RenderWizard("finalexpense");
        [HttpGet("Final-Expense/landing")]
        public Task<IActionResult> FinalExpenseLandingQuote() => RenderWizard(LifeOfferKeys.FinalExpense, isLandingPage: true);
        [HttpGet("Mortgage-Protection")]
        public Task<IActionResult> MortgageQuote() => RenderWizard("mortgage");
        [HttpGet("Mortgage-Protection/landing")]
        public Task<IActionResult> MortgageLandingQuote() => RenderWizard(LifeOfferKeys.Mortgage, isLandingPage: true);
        [HttpGet("IUL")]
        public Task<IActionResult> IulQuote() => RenderWizard("iul");
        [HttpGet("IUL/landing")]
        public Task<IActionResult> IulLandingQuote() => RenderWizard(LifeOfferKeys.Iul, isLandingPage: true);

        // ===================== POST =====================
        [HttpPost("Life")]
        public Task<IActionResult> SubmitLifeQuote(LifeQuoteFormModel model) => SubmitInternal(model, model.OfferKey ?? "life");
        [HttpPost("Term-Life")]
        public Task<IActionResult> SubmitTermLifeQuote(LifeQuoteFormModel model) => SubmitInternal(model, "term");
        [HttpPost("Whole-Life")]
        public Task<IActionResult> SubmitWholeLifeQuote(LifeQuoteFormModel model) => SubmitInternal(model, "wholelife");
        [HttpPost("Final-Expense")]
        public Task<IActionResult> SubmitFinalExpenseQuote(LifeQuoteFormModel model) => SubmitInternal(model, "finalexpense");
        [HttpPost("Mortgage-Protection")]
        public Task<IActionResult> SubmitMortgageQuote(LifeQuoteFormModel model) => SubmitInternal(model, "mortgage");
        [HttpPost("IUL")]
        public Task<IActionResult> SubmitIulQuote(LifeQuoteFormModel model) => SubmitInternal(model, "iul");
        [HttpPost("Life/estimate-preview")]
        public IActionResult EstimatePreview(LifeQuoteFormModel model)
        {
            NormalizeDiscoveryAnswers(model);

            var preview = LifeEstimateEngine.BuildPreview(model, model.OfferKey);
            return Json(preview);
        }

        private async Task<IActionResult> RenderWizard(string offerKey, bool isLandingPage = false)
        {
            var cfg = GetWizardConfig(offerKey);
            var mode = ResolvePageMode(
                cfg,
                isLandingPage,
                model: null,
                requestedLandingVariant: isLandingPage ? ResolveLandingVariantFromRequest(cfg) : null);
            var vm = await BuildWizardViewModelAsync(cfg, new LifeQuoteFormModel
            {
                FirstName = "",
                LastName = "",
                Email = "",
                Phone = "",
                OfferKey = cfg.OfferKey,
                ProductType = cfg.ProductType
            }, mode);
            ApplyWizardViewData(vm);
            return View("~/Views/Quote/Life.cshtml", vm);
        }

        private async Task<IActionResult> SubmitInternal(LifeQuoteFormModel model, string offerKey)
        {
            var cfg = GetWizardConfig(offerKey);
            var pageMode = ResolvePageMode(cfg, isLandingPage: false, model, requestedLandingVariant: null);
            var correlationId = Guid.NewGuid();
            NormalizeDiscoveryAnswers(model);
            model.FirstName = model.FirstName?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(model.FirstName))
            {
                model.FirstName = string.Empty;
                ModelState.Remove(nameof(LifeQuoteFormModel.FirstName));
                ModelState.AddModelError(nameof(LifeQuoteFormModel.FirstName), "First name is required.");
            }

            if (string.IsNullOrWhiteSpace(model.LastName))
            {
                model.LastName = null;
                ModelState.Remove(nameof(LifeQuoteFormModel.LastName));
            }

            if (string.IsNullOrWhiteSpace(model.Email))
            {
                model.Email = null;
                ModelState.Remove(nameof(LifeQuoteFormModel.Email));
            }

            if (string.IsNullOrWhiteSpace(model.Phone))
            {
                model.Phone = null;
                ModelState.Remove(nameof(LifeQuoteFormModel.Phone));
            }

            if (string.IsNullOrWhiteSpace(model.Email) && string.IsNullOrWhiteSpace(model.Phone))
            {
                ModelState.AddModelError("ContactMethod", "Add an email or phone so we can keep your estimate connected to you.");
            }

            if (!model.CoverageAmount.HasValue || model.CoverageAmount.Value <= 0)
            {
                ModelState.AddModelError(nameof(LifeQuoteFormModel.CoverageAmountOption), "Coverage amount is required.");
            }

            if (model.Age.HasValue && (model.Age.Value < 18 || model.Age.Value > 85))
            {
                ModelState.AddModelError(nameof(LifeQuoteFormModel.Age), "Age must be between 18 and 85.");
            }

            if (!model.MarketingEmailConsent)
            {
                ModelState.AddModelError(nameof(LifeQuoteFormModel.MarketingEmailConsent), "Please check the box so we can send your estimate and options.");
            }

            
            if (string.IsNullOrWhiteSpace(model.Phone))
            {
                ModelState.AddModelError(nameof(LifeQuoteFormModel.Phone), "Please enter your phone number.");
            }

if (!ModelState.IsValid)
            {
                if (IsAjax())
                {
                    var fieldErrors = CollectModelStateErrors();
                    _logger.LogWarning(
                        "LifeQuote [{CorrelationId}]: invalid ajax submission errors={ValidationErrors}",
                        correlationId,
                        JsonSerializer.Serialize(fieldErrors));
                    return BadRequest(new
                    {
                        error = ResolveAjaxValidationMessage(fieldErrors),
                        fieldErrors,
                        correlationId = correlationId.ToString("D")
                    });
                }

                model.OfferKey = cfg.OfferKey;
                model.ProductType = cfg.ProductType;
                var vmInvalid = await BuildWizardViewModelAsync(cfg, model, pageMode);
                ApplyWizardViewData(vmInvalid);
                return View("~/Views/Quote/Life.cshtml", vmInvalid);
            }

            model.OfferKey = offerKey;
            model.ProductType = cfg.ProductType;
            model.PageKey = pageMode.EffectivePageKey;
            model.PageVariant = pageMode.PageVariant;
            model.PageMode = pageMode.PageMode;
            var offerContent = GetContent(offerKey);

            _logger.LogInformation(
                "LifeQuote [{CorrelationId}]: request received offer={Offer} pageKey={PageKey}",
                correlationId, offerKey, pageMode.EffectivePageKey);

            var (leadRecipientEmail, agentProfileId, agentSlug, isFounderPath) = await ResolveLeadContextAsync();
            var isAgentContext = agentProfileId.HasValue || !string.IsNullOrWhiteSpace(agentSlug);
            _logger.LogInformation(
                "LifeQuote [{CorrelationId}]: attribution resolved AgentSlug={Slug} ProfileId={ProfileId} Recipient={Recipient}",
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
                    InterestType  = cfg.ProductType,
                    SourcePageKey = pageMode.EffectivePageKey,
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
                    MarketingEmailConsent = model.MarketingEmailConsent,
                    CallTextConsent = model.MarketingEmailConsent && !string.IsNullOrWhiteSpace(model.Phone),
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
                        OfferKey       = model.OfferKey,
                        ProductType    = model.ProductType,
                        ProtectingWho  = model.ProtectingWho,
                        CoverageGoal   = model.CoverageGoal,
                        CoverageAmountOption = model.CoverageAmountOption,
                        CoverageAmount = model.CoverageAmount,
                        TobaccoUse     = model.TobaccoUse,
                        Age            = model.Age,
                        Answer1        = model.Answer1,
                        Answer2        = model.Answer2,
                        Answer3        = model.Answer3,
                        Answer4        = model.Answer4,
                        State          = model.State,
                        AgeRange       = model.AgeRange,
                        UtmId          = model.UtmId,
                        Fbclid         = model.Fbclid,
                        UtmTerm        = model.UtmTerm,
                        UtmContent     = model.UtmContent,
                        MetaCampaignId = model.MetaCampaignId,
                        MetaAdSetId    = model.MetaAdSetId,
                        MetaAdId       = model.MetaAdId,
                        ReferrerUrl    = model.ReferrerUrl,
                        LandingPageUrl = model.LandingPageUrl,
                        PageVariant    = pageMode.PageVariant,
                        PageMode       = pageMode.PageMode,
                        PagePath       = Request?.Path.Value,
                        CorrelationId  = correlationId,
                        RecommendationPrimaryKey       = model.RecommendationPrimaryKey,
                        RecommendationPrimaryTitle     = model.RecommendationPrimaryTitle,
                        RecommendationSecondaryKey     = model.RecommendationSecondaryKey,
                        RecommendationSecondaryTitle   = model.RecommendationSecondaryTitle,
                    })
                };
                _db.WebsiteLeads.Add(lead);
                await _db.SaveChangesAsync();
                _logger.LogInformation(
                    "LifeQuote [{CorrelationId}]: WebsiteLead {LeadId} saved offer={Offer}",
                    correlationId, lead.LeadId, model.OfferKey);
            }
            catch (Exception persistEx)
            {
                _logger.LogError(persistEx,
                    "LifeQuote [{CorrelationId}]: lead persistence failed offer={Offer} pageKey={PageKey}",
                    correlationId, model.OfferKey, pageMode.EffectivePageKey);
                if (IsAjax())
                    return StatusCode(500, new { error = "Failed to save lead", detail = persistEx.Message });
                ModelState.AddModelError("", $"Failed to save lead: {persistEx.Message}");
                var vmPersistErr = await BuildWizardViewModelAsync(cfg, model, pageMode);
                ApplyWizardViewData(vmPersistErr);
                return View("~/Views/Quote/Life.cshtml", vmPersistErr);
            }

            try
            {
                await TryWriteLeadPipelineEventAsync(
                    lead,
                    cfg.ProductType,
                    pageMode,
                    correlationId,
                    "workstation_capture_attempt",
                    new
                    {
                        LeadId = lead.LeadId,
                        OfferKey = model.OfferKey,
                        PageVariant = pageMode.PageVariant,
                        PageMode = pageMode.PageMode,
                        CaptureStage = "attempt"
                    });

                var captureResult = await _websiteLifeLeadCapture.UpsertAsync(
                    new WebsiteLifeLeadCaptureRequest
                    {
                        WebsiteLeadId = lead.LeadId,
                        SubmittedUtc = lead.CreatedUtc,
                        ProductType = model.ProductType,
                        OfferKey = model.OfferKey,
                        FirstName = lead.FirstName,
                        LastName = lead.LastName,
                        Email = lead.Email,
                        Phone = lead.Phone,
                        State = model.State,
                        Age = model.Age,
                        AgeRange = model.AgeRange,
                        CoverageAmount = model.CoverageAmount,
                        CoverageAmountOption = model.CoverageAmountOption,
                        AgentTrackingProfileId = agentProfileId,
                        AgentSlug = agentSlug,
                        RecipientEmail = leadRecipientEmail
                    },
                    HttpContext?.RequestAborted ?? CancellationToken.None);

                if (captureResult.Captured)
                {
                    await _db.SaveChangesAsync(HttpContext?.RequestAborted ?? CancellationToken.None);
                    _logger.LogInformation(
                        "LifeQuote [{CorrelationId}]: workstation lead {WorkstationLeadId} {CaptureMode} bucket={Bucket} owner={AgentUserId}",
                        correlationId,
                        captureResult.WorkstationLeadId,
                        captureResult.Created ? "created" : "updated",
                        captureResult.Bucket,
                        captureResult.AgentUserId);

                    await TryWriteLeadPipelineEventAsync(
                        lead,
                        cfg.ProductType,
                        pageMode,
                        correlationId,
                        "workstation_capture_success",
                        new
                        {
                            LeadId = lead.LeadId,
                            OfferKey = model.OfferKey,
                            PageVariant = pageMode.PageVariant,
                            PageMode = pageMode.PageMode,
                            WorkstationLeadId = captureResult.WorkstationLeadId,
                            Bucket = captureResult.Bucket,
                            AgentUserId = captureResult.AgentUserId,
                            CaptureMode = captureResult.Created ? "created" : "updated"
                        });
                }
                else
                {
                    await TryWriteLeadPipelineEventAsync(
                        lead,
                        cfg.ProductType,
                        pageMode,
                        correlationId,
                        "workstation_capture_failure",
                        new
                        {
                            LeadId = lead.LeadId,
                            OfferKey = model.OfferKey,
                            PageVariant = pageMode.PageVariant,
                            PageMode = pageMode.PageMode,
                            Reason = captureResult.Reason ?? "unknown",
                            Bucket = captureResult.Bucket,
                            AgentUserId = captureResult.AgentUserId
                        });

                    _logger.LogWarning(
                        "LifeQuote [{CorrelationId}]: workstation lead capture skipped for WebsiteLead {LeadId}. reason={Reason}",
                        correlationId,
                        lead.LeadId,
                        captureResult.Reason ?? "unknown");

                    if (IsAjax() && string.Equals(captureResult.Reason, "InternalTestLead", StringComparison.OrdinalIgnoreCase))
                    {
                        return Json(new
                        {
                            success = true,
                            skippedWorkstationCapture = true,
                            reason = captureResult.Reason,
                            bucket = captureResult.Bucket,
                            agentUserId = captureResult.AgentUserId,
                            leadId = lead.LeadId
                        });
                    }

                    if (IsAjax())
                        return StatusCode(500, new { error = "Workstation capture skipped", reason = captureResult.Reason ?? "unknown", bucket = captureResult.Bucket, agentUserId = captureResult.AgentUserId });
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
                    "LifeQuote [{CorrelationId}]: workstation lead capture failed for WebsiteLead {LeadId}. Continuing with saved website lead.",
                    correlationId,
                    lead.LeadId);

                await TryWriteLeadPipelineEventAsync(
                    lead,
                    cfg.ProductType,
                    pageMode,
                    correlationId,
                    "workstation_capture_failure",
                    new
                    {
                        LeadId = lead.LeadId,
                        OfferKey = model.OfferKey,
                        PageVariant = pageMode.PageVariant,
                        PageMode = pageMode.PageMode,
                        Reason = "capture_exception",
                        ErrorMessage = captureEx.Message
                    });
            }

            var metaLeadEventId = Guid.NewGuid().ToString("N");
            await TryPersistMetaTrackingAsync(
                lead,
                correlationId,
                "meta_tracking_initialized",
                state =>
                {
                    state.EventId = metaLeadEventId;
                    state.ResolvedMetaPixelId = resolvedMetaPixel.PixelId;
                    state.PixelOwnerType = resolvedMetaPixel.PixelOwnerType;
                    state.BrowserPixelStatus = "pending";
                    state.BrowserPixelUpdatedUtc = DateTime.UtcNow;
                    state.BrowserPixelNote = null;
                    state.ServerCapiStatus = "queued_for_bridge";
                    state.ServerCapiUpdatedUtc = DateTime.UtcNow;
                    state.ServerCapiNote = "analytics_events_source_of_truth";
                });

            // ── 2. Send agent/founder notification email ───────────────────────────
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
                    $"[LIFE QUOTE — {offerContent.DisplayName.ToUpperInvariant()}] New Lead | {model.FirstName}",
                    BuildEmailBody(model, cfg),
                    replyToEmail: model.Email,
                    saveToSentItems: true,
                    cancellationToken: HttpContext?.RequestAborted ?? CancellationToken.None);

                if (agentEmailSent)
                {
                    _logger.LogInformation(
                        "LifeQuote [{CorrelationId}]: agent notification email sent to {Recipient} for lead {LeadId} offer={Offer}",
                        correlationId, primary, lead.LeadId, model.OfferKey);
                }
                else
                {
                    _logger.LogError(
                        "LifeQuote [{CorrelationId}]: agent notification email failed to {Recipient} for lead {LeadId} offer={Offer} — lead is saved, continuing",
                        correlationId, primary, lead.LeadId, model.OfferKey);
                }
            }
            else
            {
                _logger.LogWarning(
                    "LifeQuote [{CorrelationId}]: no recipient resolved for lead {LeadId} offer={Offer} — email skipped",
                    correlationId, lead.LeadId, model.OfferKey);
            }

            // ── 2b. Send recommendation summary to user (only when email provided) ──
            if (!string.IsNullOrWhiteSpace(model.Email?.Trim()))
            {
                var attachedAgentProfile = await BuildAgentTrustProfileAsync(HttpContext?.RequestAborted ?? CancellationToken.None);
                var attachedAgentFirstName = attachedAgentProfile?.FirstName;
                var attachedAgentBookingUrl = ResolveAgentBookingUrl(attachedAgentProfile);

                var userEmailSent = await _emailSender.TrySendAsync(
                    model.Email.Trim(),
                    $"Your protection review is ready — {websiteName}",
                    BuildUserSummaryEmailBody(model, cfg, attachedAgentFirstName, attachedAgentBookingUrl),
                    saveToSentItems: false,
                    cancellationToken: HttpContext?.RequestAborted ?? CancellationToken.None);

                if (userEmailSent)
                {
                    _logger.LogInformation(
                        "LifeQuote [{CorrelationId}]: user summary email sent to {Email} for lead {LeadId} offer={Offer}",
                        correlationId, model.Email.Trim(), lead.LeadId, model.OfferKey);
                }
                else
                {
                    _logger.LogError(
                        "LifeQuote [{CorrelationId}]: user summary email failed for lead {LeadId} offer={Offer} — lead is saved, continuing",
                        correlationId, lead.LeadId, model.OfferKey);
                }
            }

            // ── 3. Write analytics event ─────────────────────────────────────────
            try
            {
                var eventMetadata = new
                {
                    LeadId        = lead.LeadId,
                    CorrelationId = correlationId,
                    OfferKey      = model.OfferKey,
                    PageVariant   = pageMode.PageVariant,
                    PageMode      = pageMode.PageMode,
                    PagePath      = Request?.Path.Value,
                    RecommendationPrimaryKey       = model.RecommendationPrimaryKey,
                    RecommendationPrimaryTitle     = model.RecommendationPrimaryTitle,
                    RecommendationSecondaryKey     = model.RecommendationSecondaryKey,
                    RecommendationSecondaryTitle   = model.RecommendationSecondaryTitle,
                };

                var submittedCtx = BuildTrackingContext(
                    pageMode.EffectivePageKey,
                    lead,
                    "website_lead_submitted",
                    eventMetadata,
                    pageMode.PageVariant,
                    pageMode.PageMode,
                    lead.CreatedUtc);
                var submittedAnalyticsEvent = UnifiedEventMapper.ToAnalytics(submittedCtx);
                UnifiedAnalyticsWriter.Write(_db, submittedAnalyticsEvent);

                var persistedCtx = BuildTrackingContext(
                    pageMode.EffectivePageKey,
                    lead,
                    "lead_persisted",
                    eventMetadata,
                    pageMode.PageVariant,
                    pageMode.PageMode,
                    lead.CreatedUtc);
                var persistedAnalyticsEvent = UnifiedEventMapper.ToAnalytics(persistedCtx);
                UnifiedAnalyticsWriter.Write(_db, persistedAnalyticsEvent);
                await _db.SaveChangesAsync();
                _logger.LogInformation(
                    "LifeQuote [{CorrelationId}]: lead persistence analytics written for lead {LeadId} offer={Offer}",
                    correlationId, lead.LeadId, model.OfferKey);
            }
            catch (Exception analyticsEx)
            {
                _logger.LogError(analyticsEx,
                    "LifeQuote [{CorrelationId}]: analytics event write failed for lead {LeadId} offer={Offer} — lead is saved, continuing",
                    correlationId, lead.LeadId, model.OfferKey);
            }

            var publicBookingHint = await BuildPublicBookingAjaxHintAsync(
                lead.LeadId,
                lead.AgentTrackingProfileId,
                agentSlug,
                pageMode.EffectivePageKey,
                cfg.OfferKey,
                HttpContext?.RequestAborted ?? CancellationToken.None);

            // Set the quote type so the Thank You page can display the correct name
            TempData["QuoteType"] = offerContent.DisplayName;
            TempData["MetaLeadEventId"] = metaLeadEventId;
            TempData["MetaLeadLeadId"] = lead.LeadId.ToString("D");

            // AJAX: return 200 OK so JS can navigate client-side (preserves TempData for subsequent GET)
            if (IsAjax())
                return Ok(new
                {
                    success = true,
                    leadId = lead.LeadId.ToString("D"),
                    metaLeadEventId,
                    metaCapiStatus = "queued_for_bridge",
                    booking = publicBookingHint
                });

            return RedirectToAction("Index", "ThankYou");
        }

        [HttpPost("Life/meta-browser-ack")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> AckLifeBrowserPixel([FromBody] MetaBrowserPixelAckRequest? request)
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

            var normalizedStatus = NormalizeBrowserPixelStatus(request.Status);
            var normalizedNote = NormalizeBrowserPixelNote(request.Note);

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
                    Route = "/Quote/Life/meta-browser-ack"
                };

                var pageKey = string.IsNullOrWhiteSpace(lead.SourcePageKey) ? "quote_life" : lead.SourcePageKey!;
                var pageVariant = pageKey.Contains("_landing", StringComparison.OrdinalIgnoreCase)
                    ? LandingPageVariant
                    : WebsitePageVariant;
                var pageMode = pageKey.Contains("_landing", StringComparison.OrdinalIgnoreCase)
                    ? "paid_landing"
                    : "site_mode";

                var ctx = BuildTrackingContext(
                    pageKey,
                    lead,
                    "meta_browser_event_attempt",
                    analyticsMetadata,
                    pageVariant,
                    pageMode);
                var analyticsEvent = UnifiedEventMapper.ToAnalytics(ctx);
                UnifiedAnalyticsWriter.Write(_db, analyticsEvent);

                if (string.Equals(normalizedStatus, "sent", StringComparison.OrdinalIgnoreCase))
                {
                    var ctxSuccess = BuildTrackingContext(
                        pageKey,
                        lead,
                        "meta_browser_event_success",
                        analyticsMetadata,
                        pageVariant,
                        pageMode);
                    var analyticsEventSuccess = UnifiedEventMapper.ToAnalytics(ctxSuccess);
                    UnifiedAnalyticsWriter.Write(_db, analyticsEventSuccess);
                }

                await _db.SaveChangesAsync(HttpContext?.RequestAborted ?? CancellationToken.None);
                _logger.LogInformation(
                    "LifeQuote browser pixel ack lead={LeadId} status={Status} eventId={EventId}",
                    request.LeadId, normalizedStatus, request.EventId);
            }
            catch (Exception ackEx)
            {
                _logger.LogError(
                    ackEx,
                    "LifeQuote browser pixel ack save failed lead={LeadId} status={Status} eventId={EventId}",
                    request.LeadId, normalizedStatus, request.EventId);
            }

            return NoContent();
        }

        [HttpPost("Life/booking-experience")]
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
                        "LifeQuote public booking activation failed for WebsiteLead {LeadId}. Returning booking UI without appointment linkage.",
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

        [HttpPost("Life/booking-confirmation")]
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

        // ── Server-side product content (mirrors JS PRODUCT_CONTENT) ─────────────
        private sealed record RecContent(string Title, string Description, string[] Bullets);

        private static readonly IReadOnlyDictionary<string, RecContent> RecContentMap =
            new Dictionary<string, RecContent>(StringComparer.OrdinalIgnoreCase)
            {
                ["term"] = new(
                    "Term Life Insurance",
                    "A lower-cost protection path commonly reviewed when the goal is covering temporary responsibilities like income, family needs, or major bills.",
                    new[] {
                        "Often the most affordable way to get more coverage",
                        "Commonly reviewed for income protection and family needs",
                        "A strong option when straightforward protection is the priority",
                    }),
                ["wholelife"] = new(
                    "Whole Life Insurance",
                    "A simple permanent coverage path often reviewed when lifelong protection and steady guarantees matter.",
                    new[] {
                        "Designed to stay in place for the long term",
                        "Builds cash value over time",
                        "Often reviewed when permanent protection matters more than lowest cost",
                    }),
                ["finalexpense"] = new(
                    "Final Expense Coverage",
                    "A smaller permanent coverage path commonly reviewed to help with burial and end-of-life expenses.",
                    new[] {
                        "Focused on funeral and final-cost needs",
                        "Usually reviewed when the coverage purpose is narrow and specific",
                        "Often more relevant in older-age planning conversations",
                    }),
                ["mortgage"] = new(
                    "Mortgage Protection Review",
                    "A protection path commonly reviewed when the priority is helping protect the home and major monthly obligations.",
                    new[] {
                        "Focused on mortgage and household bill protection",
                        "Often reviewed alongside term coverage",
                        "A strong fit when keeping the home is the main concern",
                    }),
                ["iul"] = new(
                    "Indexed Universal Life",
                    "A more flexible permanent coverage option that may be worth reviewing in select long-term planning situations.",
                    new[] {
                        "Permanent protection with flexible structure",
                        "More advanced than basic term or whole life",
                        "Typically worth reviewing only when long-term flexibility is a priority",
                    }),
            };

        // Builds a styled HTML rec card block for use inside LeadEmailTemplate.RowHtml.
        private static string BuildRecCardHtml(string? key, string? fallbackTitle, string badgeLabel, bool isPrimary)
        {
            RecContentMap.TryGetValue(key ?? "", out var content);
            var title   = content?.Title       ?? fallbackTitle ?? key ?? "—";
            var desc    = content?.Description ?? "";
            var bullets = content?.Bullets     ?? Array.Empty<string>();

            var borderColor   = isPrimary ? "rgba(199,141,49,0.70)" : "rgba(199,153,49,0.30)";
            var bgColor       = isPrimary ? "rgba(199,153,49,0.09)" : "rgba(15,29,53,0.70)";
            var badgeBg       = isPrimary ? "#c79931"               : "rgba(199,153,49,0.16)";
            var badgeColor    = isPrimary ? "#0f172a"               : "#f3d78f";

            var sb = new StringBuilder();
            sb.Append($@"<div style=""background:{bgColor};border:1.4px solid {borderColor};border-radius:10px;padding:14px 14px 12px;margin-bottom:10px;"">
  <div style=""display:inline-block;font-size:11px;font-weight:800;text-transform:uppercase;letter-spacing:.05em;border-radius:999px;padding:2px 10px;margin-bottom:8px;background:{badgeBg};color:{badgeColor};"">{WebUtility.HtmlEncode(badgeLabel)}</div>
  <div style=""font-size:15px;font-weight:900;color:#f8fafc;margin-bottom:5px;"">{WebUtility.HtmlEncode(title)}</div>");

            if (!string.IsNullOrWhiteSpace(desc))
                sb.Append($@"<div style=""font-size:13px;color:rgba(248,250,252,0.72);margin-bottom:8px;line-height:1.5;"">{WebUtility.HtmlEncode(desc)}</div>");

            if (bullets.Length > 0)
            {
                sb.Append(@"<ul style=""list-style:none;padding:0;margin:0;"">");
                foreach (var b in bullets)
                    sb.Append($@"<li style=""font-size:13px;font-weight:700;color:rgba(248,250,252,0.82);padding-left:1.3em;position:relative;line-height:1.4;margin-bottom:4px;""><span style=""position:absolute;left:0;color:#c79931;font-weight:900;"">✓</span>{WebUtility.HtmlEncode(b)}</li>");
                sb.Append("</ul>");
            }

            sb.Append("</div>");
            return sb.ToString();
        }

        private static string BuildEmailBody(LifeQuoteFormModel model, LifeWizardConfig cfg)
        {
            static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

            static string? ResolveLabel(IReadOnlyList<LifeWizardOption>? options, string? code)
            {
                if (string.IsNullOrWhiteSpace(code) || options == null) return null;
                return options.FirstOrDefault(o => string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase))?.Label ?? code;
            }

            static string? ResolveCoverageAmountLabel(int? coverageAmount)
            {
                if (!coverageAmount.HasValue || coverageAmount.Value <= 0) return null;
                return coverageAmount.Value >= 2_000_000
                    ? "$2,000,000+"
                    : $"${coverageAmount.Value.ToString("N0", CultureInfo.InvariantCulture)}";
            }

            var protectStep = cfg.Steps.FirstOrDefault(step => string.Equals(step.FieldAlias, "ProtectingWho", StringComparison.OrdinalIgnoreCase));
            var goalStep = cfg.Steps.FirstOrDefault(step => string.Equals(step.FieldAlias, "CoverageGoal", StringComparison.OrdinalIgnoreCase));
            var coverageStep = cfg.Steps.FirstOrDefault(step => string.Equals(step.FieldAlias, "CoverageAmountOption", StringComparison.OrdinalIgnoreCase));
            var tobaccoStep = cfg.Steps.FirstOrDefault(step => string.Equals(step.FieldAlias, "TobaccoUse", StringComparison.OrdinalIgnoreCase));

            var fullName = $"{model.FirstName} {model.LastName}".Trim();
            if (string.IsNullOrWhiteSpace(fullName)) fullName = "New prospect";

            var protectingLabel = ResolveLabel(protectStep?.Options, model.ProtectingWho) ?? model.ProtectingWho ?? "Not provided";
            var goalLabel = ResolveLabel(goalStep?.Options, model.CoverageGoal) ?? model.CoverageGoal ?? "Not provided";
            var coverageLabel = ResolveLabel(coverageStep?.Options, model.CoverageAmountOption) ?? ResolveCoverageAmountLabel(model.CoverageAmount) ?? "Review needed";
            var tobaccoLabel = ResolveLabel(tobaccoStep?.Options, model.TobaccoUse) ?? model.TobaccoUse ?? "Not provided";
            var ageLabel = !string.IsNullOrWhiteSpace(model.AgeRange) &&
                (model.AgeRange.Contains('-') || model.AgeRange.Contains('+'))
                ? model.AgeRange
                : model.Age?.ToString(CultureInfo.InvariantCulture) ?? "Not provided";

            var recommendationTitle =
                !string.IsNullOrWhiteSpace(model.RecommendationPrimaryTitle)
                    ? model.RecommendationPrimaryTitle.Trim()
                    : cfg.DisplayName;

            var secondaryTitle =
                !string.IsNullOrWhiteSpace(model.RecommendationSecondaryTitle)
                    ? model.RecommendationSecondaryTitle.Trim()
                    : null;

            var reviewHeadline = recommendationTitle switch
            {
                var t when t.Contains("Whole", StringComparison.OrdinalIgnoreCase)
                    => "Whole life estimate is worth confirming.",
                var t when t.Contains("Term", StringComparison.OrdinalIgnoreCase)
                    => "Term life estimate is worth confirming.",
                var t when t.Contains("Final", StringComparison.OrdinalIgnoreCase)
                    => "Final expense estimate is worth confirming.",
                var t when t.Contains("Mortgage", StringComparison.OrdinalIgnoreCase)
                    => "Mortgage protection estimate is worth confirming.",
                var t when t.Contains("IUL", StringComparison.OrdinalIgnoreCase)
                    => "IUL estimate is worth confirming.",
                _ => "Protection review is ready for agent follow-up."
            };

            var surfacedCopy = recommendationTitle switch
            {
                var t when t.Contains("Whole", StringComparison.OrdinalIgnoreCase)
                    => "Prospect may be looking for lifelong protection, stable structure, and long-term family support.",
                var t when t.Contains("Term", StringComparison.OrdinalIgnoreCase)
                    => "Prospect may be looking to protect income, major responsibilities, and the years their family depends on most.",
                var t when t.Contains("Final", StringComparison.OrdinalIgnoreCase)
                    => "Prospect may be looking to reduce the final-cost burden left behind for loved ones.",
                var t when t.Contains("Mortgage", StringComparison.OrdinalIgnoreCase)
                    => "Prospect may be looking to protect the home, payment stability, and household continuity.",
                var t when t.Contains("IUL", StringComparison.OrdinalIgnoreCase)
                    => "Prospect may be exploring long-term protection with flexibility and future planning potential.",
                _ => "Prospect answers point toward a protection gap worth reviewing."
            };

            var timingCopy = recommendationTitle switch
            {
                var t when t.Contains("Whole", StringComparison.OrdinalIgnoreCase)
                    => "Permanent coverage is sensitive to age, health, design, and funding. Structure matters more than the quick estimate.",
                var t when t.Contains("Term", StringComparison.OrdinalIgnoreCase)
                    => "Rates and approval flexibility can shift as age and health change.",
                var t when t.Contains("Final", StringComparison.OrdinalIgnoreCase)
                    => "Final expense eligibility and pricing can tighten as age and health change.",
                var t when t.Contains("Mortgage", StringComparison.OrdinalIgnoreCase)
                    => "Mortgage risk stays active until a protection decision is made.",
                var t when t.Contains("IUL", StringComparison.OrdinalIgnoreCase)
                    => "IUL structure, funding, and assumptions need careful review before positioning.",
                _ => "Rates and approval options can shift depending on age and health."
            };

            string estimatedRange =
                recommendationTitle.Contains("Whole", StringComparison.OrdinalIgnoreCase) ? "$57-$66/mo" :
                recommendationTitle.Contains("Term", StringComparison.OrdinalIgnoreCase) ? "$28-$46/mo" :
                recommendationTitle.Contains("Final", StringComparison.OrdinalIgnoreCase) ? "$41-$63/mo" :
                recommendationTitle.Contains("Mortgage", StringComparison.OrdinalIgnoreCase) ? "$49-$81/mo" :
                recommendationTitle.Contains("IUL", StringComparison.OrdinalIgnoreCase) ? "$110-$220/mo" :
                "$40-$85/mo";

            var recommendationHtml = !string.IsNullOrWhiteSpace(secondaryTitle)
                ? $@"<div style=""margin-top:8px;color:rgba(248,250,252,.80);font-size:13px;line-height:1.4;font-weight:700;""><strong style=""color:#f3d688;"">Also consider:</strong> {H(secondaryTitle)}</div>"
                : "";

            return $@"
<!DOCTYPE html>
<html>
<body style=""margin:0;padding:0;background:#f3f4f6;font-family:Arial,Helvetica,sans-serif;"">
<div style=""width:100%;padding:18px 10px;background:#f3f4f6;"">
<div style=""max-width:680px;margin:0 auto;background:#071d3d;border:1px solid rgba(212,175,55,.55);border-radius:18px;overflow:hidden;box-shadow:0 18px 42px rgba(0,0,0,.24);"">

<div style=""padding:18px;background:#061832;border-bottom:1px solid rgba(243,214,136,.24);"">
<div style=""color:#f3d688;font-size:12px;font-weight:900;letter-spacing:.12em;text-transform:uppercase;margin-bottom:8px;"">New lead ready for review</div>
<div style=""color:#fff8e7;font-size:24px;line-height:1.12;font-weight:900;"">{H(fullName)} submitted a protection review.</div>
<div style=""color:rgba(248,250,252,.82);font-size:14px;line-height:1.45;font-weight:650;margin-top:10px;"">
The prospect received the review-style estimate email. Use the contact details below to follow up with continuity, not a cold reset.
</div>
</div>

<div style=""padding:16px 18px 18px;"">

<div style=""background:#092955;border:1px solid rgba(243,214,136,.36);border-radius:16px;padding:15px;margin-bottom:14px;"">
<div style=""color:#f3d688;font-size:11px;font-weight:900;letter-spacing:.10em;text-transform:uppercase;margin-bottom:8px;"">Prospect contact</div>
<div style=""color:#fff8e7;font-size:20px;line-height:1.2;font-weight:900;margin-bottom:10px;"">{H(fullName)}</div>
<div style=""color:#f8fafc;font-size:14px;line-height:1.55;font-weight:750;"">
<strong style=""color:#f3d688;"">Phone:</strong> {H(model.Phone)}<br/>
<strong style=""color:#f3d688;"">Email:</strong> {H(model.Email)}<br/>
<strong style=""color:#f3d688;"">State:</strong> {H(model.State)}<br/>
<strong style=""color:#f3d688;"">Product:</strong> {H(cfg.DisplayName)}<br/>
<strong style=""color:#f3d688;"">Offer key:</strong> {H(model.OfferKey)}
</div>
</div>

<div style=""margin-bottom:14px;"">
<span style=""display:inline-block;padding:7px 10px;border-radius:999px;border:1px solid rgba(243,214,136,.35);background:#071932;color:#fff7de;font-size:12px;font-weight:900;margin:0 5px 7px 0;"">Lead submitted</span>
<span style=""display:inline-block;padding:7px 10px;border-radius:999px;border:1px solid rgba(243,214,136,.35);background:#071932;color:#fff7de;font-size:12px;font-weight:900;margin:0 5px 7px 0;"">Prospect email sent</span>
<span style=""display:inline-block;padding:7px 10px;border-radius:999px;border:1px solid rgba(243,214,136,.35);background:#071932;color:#fff7de;font-size:12px;font-weight:900;margin:0 5px 7px 0;"">Follow-up needed</span>
</div>

<div style=""background:#08254d;border:1px solid rgba(243,214,136,.24);border-radius:16px;padding:15px;margin-bottom:12px;"">
<div style=""color:#f3d688;font-size:11px;font-weight:900;letter-spacing:.08em;text-transform:uppercase;margin-bottom:7px;"">Recommendation sent to prospect</div>
<div style=""color:#fff8e7;font-size:20px;line-height:1.15;font-weight:900;margin-bottom:7px;"">{H(reviewHeadline)}</div>
<div style=""color:rgba(248,250,252,.84);font-size:14px;line-height:1.42;font-weight:700;"">{H(surfacedCopy)}</div>
{recommendationHtml}
</div>

<div style=""background:#08254d;border:1px solid rgba(243,214,136,.24);border-radius:16px;padding:15px;margin-bottom:12px;"">
<div style=""color:#f3d688;font-size:11px;font-weight:900;letter-spacing:.08em;text-transform:uppercase;margin-bottom:7px;"">Why timing matters</div>
<div style=""color:rgba(248,250,252,.86);font-size:14px;line-height:1.42;font-weight:700;"">{H(timingCopy)}</div>
</div>

<table width=""100%"" cellpadding=""0"" cellspacing=""0"" role=""presentation"" style=""border-collapse:collapse;margin-bottom:12px;"">
<tr>
<td style=""display:block;width:100%;padding:0 0 8px 0;vertical-align:top;"">
<div style=""background:#061a36;border:1px solid rgba(243,214,136,.18);border-radius:15px;padding:13px;text-align:center;"">
<div style=""color:rgba(248,250,252,.55);font-size:10px;font-weight:900;letter-spacing:.12em;text-transform:uppercase;margin-bottom:7px;"">Estimated monthly range shown</div>
<div style=""color:#fff8e7;font-size:23px;font-weight:900;"">{H(estimatedRange)}</div>
</div>
</td>
<td style=""display:block;width:100%;padding:0 0 8px 0;vertical-align:top;"">
<div style=""background:#061a36;border:1px solid rgba(243,214,136,.18);border-radius:15px;padding:13px;text-align:center;"">
<div style=""color:rgba(248,250,252,.55);font-size:10px;font-weight:900;letter-spacing:.12em;text-transform:uppercase;margin-bottom:7px;"">Coverage range to review</div>
<div style=""color:#fff8e7;font-size:21px;font-weight:900;"">{H(coverageLabel)}</div>
</div>
</td>
</tr>
</table>

<div style=""background:#061a36;border:1px solid rgba(243,214,136,.18);border-radius:16px;padding:15px;margin-bottom:12px;"">
<div style=""color:#f3d688;font-size:11px;font-weight:900;letter-spacing:.08em;text-transform:uppercase;margin-bottom:8px;"">Discovery answers</div>
<div style=""color:#f8fafc;font-size:14px;line-height:1.55;font-weight:700;"">
<strong style=""color:#f3d688;"">Protecting:</strong> {H(protectingLabel)}<br/>
<strong style=""color:#f3d688;"">Goal:</strong> {H(goalLabel)}<br/>
<strong style=""color:#f3d688;"">Coverage:</strong> {H(coverageLabel)}<br/>
<strong style=""color:#f3d688;"">Age:</strong> {H(ageLabel)}<br/>
<strong style=""color:#f3d688;"">Tobacco:</strong> {H(tobaccoLabel)}
</div>
</div>

<div style=""background:#092955;border:1px solid rgba(243,214,136,.34);border-radius:16px;padding:16px;"">
<div style=""color:#f3d688;font-size:11px;font-weight:900;letter-spacing:.10em;text-transform:uppercase;margin-bottom:8px;"">Agent next step</div>
<div style=""color:#fff8e7;font-size:22px;line-height:1.13;font-weight:900;margin-bottom:9px;"">Follow up from the review they already started.</div>
<div style=""color:rgba(248,250,252,.84);font-size:14px;line-height:1.45;font-weight:650;"">
Open by referencing the estimate they received. Confirm coverage amount, monthly fit, carrier direction, health fit, and whether the recommendation should move forward.
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

        private static string BuildUserSummaryEmailBody(LifeQuoteFormModel model, LifeWizardConfig cfg, string? attachedAgentFirstName, string? attachedAgentBookingUrl)
        {
            // Resolve a human-readable label for a code value using the step options.
            static string? ResolveLabel(IReadOnlyList<LifeWizardOption>? options, string? code)
            {
                if (string.IsNullOrWhiteSpace(code) || options == null) return null;
                return options.FirstOrDefault(o => string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase))?.Label ?? code;
            }

            var protectStep = cfg.Steps.FirstOrDefault(step => string.Equals(step.FieldAlias, "ProtectingWho", StringComparison.OrdinalIgnoreCase));
            var goalStep = cfg.Steps.FirstOrDefault(step => string.Equals(step.FieldAlias, "CoverageGoal", StringComparison.OrdinalIgnoreCase));
            var coverageStep = cfg.Steps.FirstOrDefault(step => string.Equals(step.FieldAlias, "CoverageAmountOption", StringComparison.OrdinalIgnoreCase));
            var tobaccoStep = cfg.Steps.FirstOrDefault(step => string.Equals(step.FieldAlias, "TobaccoUse", StringComparison.OrdinalIgnoreCase));

            static string? ResolveCoverageAmountLabel(int? coverageAmount)
            {
                if (!coverageAmount.HasValue || coverageAmount.Value <= 0) return null;
                return coverageAmount.Value >= 2_000_000
                    ? "$2,000,000+"
                    : coverageAmount.Value >= 1_000_000
                    ? $"${coverageAmount.Value.ToString("N0", CultureInfo.InvariantCulture)}"
                    : $"${coverageAmount.Value.ToString("N0", CultureInfo.InvariantCulture)}";
            }

            var protectingLabel = ResolveLabel(protectStep?.Options, model.ProtectingWho);
            var goalLabel       = ResolveLabel(goalStep?.Options,    model.CoverageGoal);
            var coverageLabel   = ResolveLabel(coverageStep?.Options, model.CoverageAmountOption) ?? ResolveCoverageAmountLabel(model.CoverageAmount);
            var tobaccoLabel    = ResolveLabel(tobaccoStep?.Options,  model.TobaccoUse);

            
            var ageLabel = !string.IsNullOrWhiteSpace(model.AgeRange) &&
                (model.AgeRange.Contains('-') || model.AgeRange.Contains('+'))
                ? model.AgeRange
                : model.Age?.ToString(CultureInfo.InvariantCulture);

            var recommendationTitle =
                !string.IsNullOrWhiteSpace(model.RecommendationPrimaryTitle)
                    ? model.RecommendationPrimaryTitle.Trim()
                    : "Protection Review";

            var reviewHeadline = recommendationTitle switch
            {
                var t when t.Contains("Whole", StringComparison.OrdinalIgnoreCase)
                    => "Your whole life estimate is worth confirming.",
                var t when t.Contains("Term", StringComparison.OrdinalIgnoreCase)
                    => "Your term life estimate is worth confirming.",
                var t when t.Contains("Final", StringComparison.OrdinalIgnoreCase)
                    => "Your final expense estimate is worth confirming.",
                var t when t.Contains("Mortgage", StringComparison.OrdinalIgnoreCase)
                    => "Your mortgage protection estimate is worth confirming.",
                var t when t.Contains("IUL", StringComparison.OrdinalIgnoreCase)
                    => "Your IUL estimate is worth confirming.",
                _ => "Your protection review is ready."
            };

            var surfacedCopy = recommendationTitle switch
            {
                var t when t.Contains("Whole", StringComparison.OrdinalIgnoreCase)
                    => "You may be looking for lifelong protection, stable structure, and long-term family support.",
                var t when t.Contains("Term", StringComparison.OrdinalIgnoreCase)
                    => "You may be looking to protect income, major responsibilities, and the years your family depends on most.",
                var t when t.Contains("Final", StringComparison.OrdinalIgnoreCase)
                    => "You may be looking to reduce the financial pressure left behind for loved ones.",
                var t when t.Contains("Mortgage", StringComparison.OrdinalIgnoreCase)
                    => "You may be looking to help protect the home, payment stability, and household continuity.",
                var t when t.Contains("IUL", StringComparison.OrdinalIgnoreCase)
                    => "You may be exploring long-term protection with flexibility and future planning potential.",
                _ => "Your answers point toward a protection gap worth reviewing."
            };

            var timingCopy = recommendationTitle switch
            {
                var t when t.Contains("Whole", StringComparison.OrdinalIgnoreCase)
                    => "Permanent coverage is sensitive to age, health, design, and funding. The right structure matters more than just seeing a number.",
                var t when t.Contains("Term", StringComparison.OrdinalIgnoreCase)
                    => "Rates and approval flexibility can shift over time as age and health change.",
                var t when t.Contains("Final", StringComparison.OrdinalIgnoreCase)
                    => "Waiting rarely improves pricing or eligibility flexibility for final expense coverage.",
                var t when t.Contains("Mortgage", StringComparison.OrdinalIgnoreCase)
                    => "The mortgage payment does not pause if life changes unexpectedly.",
                var t when t.Contains("IUL", StringComparison.OrdinalIgnoreCase)
                    => "IUL structure, funding, and long-term assumptions should be reviewed carefully before deciding.",
                _ => "Rates and approval options can shift over time depending on age and health."
            };

            var nextStepName =
                !string.IsNullOrWhiteSpace(attachedAgentFirstName)
                    ? attachedAgentFirstName.Trim()
                    : "your licensed agent";

            coverageLabel ??= ResolveCoverageAmountLabel(model.CoverageAmount);

            string estimatedRange =
                recommendationTitle.Contains("Whole", StringComparison.OrdinalIgnoreCase) ? "$57-$66/mo" :
                recommendationTitle.Contains("Term", StringComparison.OrdinalIgnoreCase) ? "$28-$46/mo" :
                recommendationTitle.Contains("Final", StringComparison.OrdinalIgnoreCase) ? "$41-$63/mo" :
                recommendationTitle.Contains("Mortgage", StringComparison.OrdinalIgnoreCase) ? "$49-$81/mo" :
                recommendationTitle.Contains("IUL", StringComparison.OrdinalIgnoreCase) ? "$110-$220/mo" :
                "$40-$85/mo";

            var bookingUrl = !string.IsNullOrWhiteSpace(attachedAgentBookingUrl)
                ? attachedAgentBookingUrl.Trim()
                : string.Empty;

            var bookingButtonHtml = !string.IsNullOrWhiteSpace(bookingUrl)
                ? $@"<a href=""{WebUtility.HtmlEncode(bookingUrl)}"" style=""display:block;text-align:center;background:#f3d688;color:#111827;text-decoration:none;font-size:16px;font-weight:900;padding:14px 16px;border-radius:14px;border:1px solid #f7dc96;"">Complete My Protection Review</a>"
                : @"<div style=""text-align:center;background:#071932;color:#fff8e7;font-size:15px;font-weight:800;padding:14px 16px;border-radius:14px;border:1px solid rgba(243,214,136,.35);"">Your estimate is saved. Your agent can help confirm the next step.</div>";

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
{reviewHeadline}
</div>

<div style=""margin-top:16px;color:rgba(248,250,252,.84);font-size:14px;line-height:1.45;font-weight:650;max-width:580px;"">
Your answers point toward protection designed around your situation. The estimate is saved, but the review still needs to confirm the structure, monthly fit, carrier direction, and next best step before making a decision.
</div>

<div style=""margin-top:18px;padding-top:16px;border-top:1px solid rgba(255,255,255,.10);"">
<span style=""display:inline-block;padding:9px 12px;border-radius:999px;border:1px solid rgba(243,214,136,.35);background:#071932;color:#fff7de;font-size:12px;font-weight:900;margin:0 6px 8px 0;"">Estimate saved</span>
<span style=""display:inline-block;padding:9px 12px;border-radius:999px;border:1px solid rgba(243,214,136,.35);background:#071932;color:#fff7de;font-size:12px;font-weight:900;margin:0 6px 8px 0;"">25+ carriers</span>
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
{surfacedCopy}
</div>
</div>
</td>

<td style=""display:block;width:100%;padding:0 0 10px 0;vertical-align:top;"">
<div style=""background:#08254d;border:1px solid rgba(243,214,136,.24);border-radius:18px;padding:16px;"">
<div style=""color:#f3d688;font-size:11px;font-weight:900;letter-spacing:.08em;text-transform:uppercase;margin-bottom:8px;"">
Why timing matters
</div>
<div style=""color:#f8fafc;font-size:14px;line-height:1.38;font-weight:800;"">
{timingCopy}
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
Estimated Monthly Range
</div>
<div style=""color:#fff8e7;font-size:24px;font-weight:900;"">
{estimatedRange}
</div>
</div>
</td>

<td style=""display:block;width:100%;padding:0 0 10px 0;vertical-align:top;"">
<div style=""background:#061a36;border:1px solid rgba(243,214,136,.18);border-radius:16px;padding:15px;text-align:center;"">
<div style=""color:rgba(248,250,252,.55);font-size:11px;font-weight:900;letter-spacing:.12em;text-transform:uppercase;margin-bottom:8px;"">
Coverage Range To Review
</div>
<div style=""color:#fff8e7;font-size:23px;font-weight:900;"">
{coverageLabel}
</div>
</div>
</td>
</tr>
</table>

<div style=""background:#08254d;border:1px solid rgba(243,214,136,.20);border-radius:18px;padding:18px;margin-bottom:18px;"">
<div style=""color:#fff8e7;font-size:14px;line-height:1.42;font-weight:700;"">
We compare coverage across top-rated carriers to help match your age, health profile, and protection goal.
</div>

<div style=""margin-top:10px;color:rgba(248,250,252,.84);font-size:15px;line-height:1.55;font-weight:600;"">
Rates and approval options can shift based on age, health, and underwriting changes, which is why reviewing sooner usually gives you more flexibility.
</div>
</div>

<div style=""margin-bottom:18px;color:#f8fafc;font-size:16px;line-height:1.58;font-weight:650;"">
This review needs to confirm whether the premium, coverage amount, and structure actually align with your goals, household needs, and long-term financial direction.
</div>

<table width=""100%"" cellpadding=""0"" cellspacing=""0"" role=""presentation"" style=""border-collapse:collapse;margin-bottom:18px;"">
<tr>
<td style=""display:block;width:100%;padding:0 0 10px 0;vertical-align:top;"">
<div style=""background:#061a36;border:1px solid rgba(243,214,136,.18);border-radius:16px;padding:15px;text-align:center;"">
<div style=""color:rgba(248,250,252,.55);font-size:11px;font-weight:900;letter-spacing:.12em;text-transform:uppercase;margin-bottom:8px;"">
Built To Protect
</div>
<div style=""color:#fff8e7;font-size:15px;font-weight:900;line-height:1.25;"">
{protectingLabel}
</div>
</div>
</td>

<td style=""display:block;width:100%;padding:0 0 10px 0;vertical-align:top;"">
<div style=""background:#061a36;border:1px solid rgba(243,214,136,.18);border-radius:16px;padding:15px;text-align:center;"">
<div style=""color:rgba(248,250,252,.55);font-size:11px;font-weight:900;letter-spacing:.12em;text-transform:uppercase;margin-bottom:8px;"">
Profile Used
</div>
<div style=""color:#fff8e7;font-size:15px;font-weight:900;line-height:1.25;"">
Age {ageLabel} • {tobaccoLabel}
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
{nextStepName} can help confirm the carrier fit, monthly comfort, protection structure, and next best step based on your review.
</div>

{bookingButtonHtml}
</div>

<div style=""margin-top:18px;color:rgba(248,250,252,.55);font-size:12px;line-height:1.5;text-align:center;"">
Illustrative estimate only. Final eligibility, pricing, underwriting approval, and carrier availability vary by age, health, and state.
</div>

</div>
</div>
</div>
</body>
</html>";

        }


        private static string? ResolveAgentBookingUrl(object? agentProfile)
        {
            if (agentProfile == null) return null;

            var possibleNames = new[]
            {
                "BookingUrl",
                "BookingsUrl",
                "BookingLink",
                "CalendarUrl",
                "CalendarLink",
                "SchedulingUrl",
                "SchedulingLink",
                "MicrosoftBookingsUrl",
                "MicrosoftBookingUrl",
                "AppointmentBookingUrl",
                "PublicBookingUrl"
            };

            var type = agentProfile.GetType();
            foreach (var name in possibleNames)
            {
                var prop = type.GetProperty(name);
                var value = prop?.GetValue(agentProfile)?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value) &&
                    (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    return value;
                }
            }

            return null;
        }

        private static void NormalizeDiscoveryAnswers(LifeQuoteFormModel model)
        {
            static string? Clean(string? value) =>
                string.IsNullOrWhiteSpace(value) ? null : value.Trim();

            model.ProtectingWho = Clean(model.ProtectingWho) ?? Clean(model.Answer1);
            model.CoverageGoal = Clean(model.CoverageGoal) ?? Clean(model.Answer2) ?? Clean(model.ProtectFocus);
            model.CoverageAmountOption = Clean(model.CoverageAmountOption);
            model.TobaccoUse = Clean(model.TobaccoUse) ?? Clean(model.Answer3);

            if (!model.CoverageAmount.HasValue &&
                int.TryParse(model.CoverageAmountOption, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCoverageAmount))
            {
                model.CoverageAmount = parsedCoverageAmount;
            }

            if (!model.Age.HasValue)
            {
                if (int.TryParse(Clean(model.Answer4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAnswerAge))
                    model.Age = parsedAnswerAge;
                else if (int.TryParse(Clean(model.AgeRange), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRangeAge))
                    model.Age = parsedRangeAge;
            }

            model.Answer1 = Clean(model.Answer1) ?? model.ProtectingWho;
            model.Answer2 = Clean(model.Answer2) ?? model.CoverageGoal;
            model.Answer3 = Clean(model.Answer3) ?? model.TobaccoUse;
            model.Answer4 = Clean(model.Answer4) ?? (model.Age.HasValue ? model.Age.Value.ToString(CultureInfo.InvariantCulture) : null);
            model.CoverageAmountOption = model.CoverageAmountOption ?? (model.CoverageAmount.HasValue ? model.CoverageAmount.Value.ToString(CultureInfo.InvariantCulture) : null);

            model.ProtectFocus = Clean(model.ProtectFocus) ?? model.CoverageGoal;
            model.AgeRange = Clean(model.AgeRange) ?? (model.Age.HasValue ? model.Age.Value.ToString(CultureInfo.InvariantCulture) : null);
        }

        private bool HasExplicitAgentContext()
        {
            if (HttpContext?.Items["TrackingProfile"] is AgentTrackingProfile)
            {
                return true;
            }

            var requestMethod = Request?.Method;
            if (string.IsNullOrWhiteSpace(requestMethod) || !Microsoft.AspNetCore.Http.HttpMethods.IsPost(requestMethod))
            {
                return false;
            }

            string? slug = null;
            var formSlug = Request?.Form["AgentSlug"].ToString();
            if (!string.IsNullOrWhiteSpace(formSlug)) slug = formSlug.Trim();
            if (string.IsNullOrWhiteSpace(slug)) slug = ExtractSlugFromPath(Request?.Path.Value);
            if (string.IsNullOrWhiteSpace(slug)) slug = ExtractSlugFromPath(Request?.Headers["Referer"].ToString());
            return !string.IsNullOrWhiteSpace(slug);
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

            var first = displayName
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
                ?.Trim();

            return string.IsNullOrWhiteSpace(first) ? "there" : first;
        }

        private string BuildAgentAvatarUrl(string slug)
        {
            var safeSlug = Uri.EscapeDataString(slug.Trim());
            return $"{trackingApiBase}/avatar/agent/{safeSlug}";
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

        private async Task<LifeWizardAgentTrustProfile?> BuildAgentTrustProfileAsync(CancellationToken ct)
        {
            if (!HasExplicitAgentContext())
            {
                return null;
            }

            var trackingProfile = HttpContext?.Items["TrackingProfile"] as AgentTrackingProfile;
            string? agentSlug = HttpContext?.Items["TrackingSlug"] as string;

            if ((trackingProfile == null || string.IsNullOrWhiteSpace(agentSlug)))
            {
                string? requestedSlug = null;
                var formSlug = Request?.Form["AgentSlug"].ToString();
                if (!string.IsNullOrWhiteSpace(formSlug))
                {
                    requestedSlug = formSlug.Trim();
                }
                if (string.IsNullOrWhiteSpace(requestedSlug))
                {
                    requestedSlug = ExtractSlugFromPath(Request?.Path.Value);
                }
                if (string.IsNullOrWhiteSpace(requestedSlug))
                {
                    requestedSlug = ExtractSlugFromPath(Request?.Headers["Referer"].ToString());
                }

                if (!string.IsNullOrWhiteSpace(requestedSlug))
                {
                    var resolved = await _resolver.ResolveBySlugAsync(requestedSlug, ct);
                    if (resolved.Found && resolved.Profile != null)
                    {
                        trackingProfile = resolved.Profile;
                        agentSlug = resolved.CanonicalSlug ?? requestedSlug;
                    }
                }
            }

            if (trackingProfile == null || string.IsNullOrWhiteSpace(agentSlug))
            {
                return null;
            }

            var agentProfile = await ResolveAgentProfileAsync(trackingProfile, ct);

            var displayName = ResolveAgentDisplayName(agentProfile, trackingProfile);
            var resolvedBooking = await _publicBookingResolver.ResolveAsync(
                new PublicBookingResolveContext(
                    AgentTrackingProfileId: trackingProfile.Id,
                    AgentUserId: trackingProfile.AgentUserId,
                    AgentSlug: agentSlug),
                ct);
            var schedulingLink = resolvedBooking.HasFallback
                ? resolvedBooking.FallbackUrl
                : resolvedBooking.EmbedUrl;

            return new LifeWizardAgentTrustProfile
            {
                AgentTrackingProfileId = trackingProfile.Id,
                AgentSlug = agentSlug,
                DisplayName = displayName,
                FirstName = ResolveAgentFirstName(displayName),
                Npn = string.IsNullOrWhiteSpace(agentProfile?.Npn) ? null : agentProfile.Npn.Trim(),
                ShortBio = string.IsNullOrWhiteSpace(agentProfile?.ShortBio) ? null : agentProfile.ShortBio.Trim(),
                ProfileImageUrl = BuildAgentAvatarUrl(agentSlug),
                SchedulingLink = schedulingLink
            };
        }

        private bool IsAjax()
        {
            var hdr = Request?.Headers["X-Requested-With"].ToString();
            return !string.IsNullOrWhiteSpace(hdr) &&
                   (hdr.Contains("fetch", StringComparison.OrdinalIgnoreCase) ||
                    hdr.Contains("xmlhttprequest", StringComparison.OrdinalIgnoreCase));
        }

        private Dictionary<string, string[]> CollectModelStateErrors()
        {
            return ModelState
                .Where(entry => entry.Value?.Errors.Count > 0)
                .ToDictionary(
                    entry => string.IsNullOrWhiteSpace(entry.Key) ? "Form" : entry.Key,
                    entry => entry.Value!.Errors
                        .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage) ? "Invalid value." : error.ErrorMessage.Trim())
                        .Where(message => !string.IsNullOrWhiteSpace(message))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static string ResolveAjaxValidationMessage(IReadOnlyDictionary<string, string[]> fieldErrors)
        {
            if (fieldErrors.Count == 1)
            {
                var onlyError = fieldErrors.FirstOrDefault().Value?.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(onlyError))
                {
                    return onlyError;
                }
            }

            return "Please review the highlighted fields and try again.";
        }

        private async Task TryPersistMetaTrackingAsync(
            WebsiteLead lead,
            Guid correlationId,
            string stage,
            Action<MetaLeadTrackingState> mutate)
        {
            try
            {
                lead.MetadataJson = MetaLeadTrackingJson.Upsert(lead.MetadataJson, mutate);
                await _db.SaveChangesAsync(HttpContext?.RequestAborted ?? CancellationToken.None);
            }
            catch (Exception metaPersistEx)
            {
                _logger.LogError(
                    metaPersistEx,
                    "LifeQuote [{CorrelationId}]: meta tracking persistence failed stage={Stage} lead={LeadId}",
                    correlationId, stage, lead.LeadId);
            }
        }

        private static string NormalizeBrowserPixelStatus(string? status)
        {
            var normalized = status?.Trim().ToLowerInvariant();
            return normalized switch
            {
                "sent" => "sent",
                "unavailable" => "unavailable",
                "error" => "error",
                _ => "unknown"
            };
        }

        private static string? NormalizeBrowserPixelNote(string? note)
        {
            var normalized = note?.Trim().ToLowerInvariant();
            return normalized switch
            {
                "fbq_unavailable" => "fbq_unavailable",
                "fbq_exception" => "fbq_exception",
                _ => null
            };
        }

        private string? ResolveLandingVariantFromRequest(LifeWizardConfig cfg)
        {
            var requestedVariant = Request?.Query["variant"].ToString();
            return NormalizeLandingVariantToken(requestedVariant, cfg.OfferKey);
        }

        private string BuildCanonicalQuoteUrl(string offerKey)
        {
            var path = GetQuoteRoutePath(offerKey);
            var pathBase = Request?.PathBase.Value ?? string.Empty;
            var trackingSlug = HttpContext?.Items["TrackingSlug"] as string;
            var isFounderPath = HttpContext?.Items["IsFounderPath"] as bool? == true;
            var slugPrefix = !isFounderPath && !string.IsNullOrWhiteSpace(trackingSlug)
                ? $"/a/{trackingSlug.Trim()}"
                : string.Empty;

            var queryPairs = Request?.Query
                .Where(kvp => !string.Equals(kvp.Key, "offer", StringComparison.OrdinalIgnoreCase))
                .SelectMany(
                    kvp => kvp.Value,
                    (kvp, value) => new KeyValuePair<string, string?>(kvp.Key, value))
                .ToArray() ?? [];

            var queryString = queryPairs.Length > 0
                ? QueryString.Create(queryPairs).ToUriComponent()
                : string.Empty;

            return $"{pathBase}{slugPrefix}{path}{queryString}";
        }

        private static string BuildVariantPageKey(string basePageKey, bool isLandingPage, string? landingVariant = null)
        {
            if (!isLandingPage)
                return basePageKey;

            var normalizedLandingVariant = NormalizeLandingVariantKey(landingVariant);
            var isControlLanding =
                string.IsNullOrWhiteSpace(normalizedLandingVariant) ||
                string.Equals(normalizedLandingVariant, LandingPageVariant, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedLandingVariant, ContactFirstEducationLandingVariant, StringComparison.OrdinalIgnoreCase);

            return isControlLanding
                ? $"{basePageKey}_landing"
                : $"{basePageKey}_landing_{normalizedLandingVariant}";
        }

        private static WizardPageMode ResolvePageMode(
            LifeWizardConfig cfg,
            bool isLandingPage,
            LifeQuoteFormModel? model,
            string? requestedLandingVariant)
        {
            var postedVariant = NormalizeLandingVariantToken(model?.PageVariant, cfg.OfferKey);
            var requestedMode = model?.PageMode?.Trim();
            var postedPageKey = model?.PageKey?.Trim();
            var landingRoutePath = GetLandingRoutePath(cfg.OfferKey);
            var inferredLandingVariant = InferLandingVariantFromPageKey(cfg.PageKey, postedPageKey, cfg.OfferKey);
            var resolvedLandingVariant =
                NormalizeLandingVariantToken(requestedLandingVariant, cfg.OfferKey) ??
                postedVariant ??
                inferredLandingVariant;
            var resolvedPaidLandingVariant = string.Equals(resolvedLandingVariant, WebsitePageVariant, StringComparison.OrdinalIgnoreCase)
                ? null
                : resolvedLandingVariant;

            var isLandingRequested =
                isLandingPage ||
                string.Equals(requestedMode, "paid_landing", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(inferredLandingVariant) ||
                IsLandingRouteForOffer(model?.LandingPageUrl, landingRoutePath);

            var usesContactFirstSiteControl = !isLandingRequested;

            var pageVariant = isLandingRequested
                ? resolvedPaidLandingVariant ?? ContactFirstEducationLandingVariant
                : usesContactFirstSiteControl
                    ? ContactFirstEducationLandingVariant
                    : WebsitePageVariant;

            return new WizardPageMode(
                IsLandingPage: isLandingRequested,
                PageVariant: pageVariant,
                PageMode: isLandingRequested ? "paid_landing" : "site_mode",
                EffectivePageKey: BuildVariantPageKey(cfg.PageKey, isLandingRequested, pageVariant)
            );
        }

        private static string? InferLandingVariantFromPageKey(string basePageKey, string? pageKey, string offerKey)
        {
            if (string.IsNullOrWhiteSpace(pageKey))
                return null;

            var normalizedPageKey = pageKey.Trim();
            var landingPrefix = $"{basePageKey}_landing";
            if (string.Equals(normalizedPageKey, landingPrefix, StringComparison.OrdinalIgnoreCase))
                return ContactFirstEducationLandingVariant;

            if (!normalizedPageKey.StartsWith(landingPrefix + "_", StringComparison.OrdinalIgnoreCase))
                return null;

            var suffix = normalizedPageKey[(landingPrefix.Length + 1)..];
            return NormalizeLandingVariantToken(suffix, offerKey);
        }

        private static string? NormalizeLandingVariantToken(string? variant, string offerKey)
        {
            var normalizedVariant = NormalizeLandingVariantKey(variant);
            if (string.IsNullOrWhiteSpace(normalizedVariant))
                return null;

            if (string.Equals(normalizedVariant, WebsitePageVariant, StringComparison.OrdinalIgnoreCase))
                return WebsitePageVariant;

            if (string.Equals(normalizedVariant, LandingPageVariant, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedVariant, ContactFirstEducationLandingVariant, StringComparison.OrdinalIgnoreCase))
                return ContactFirstEducationLandingVariant;

            if (string.Equals(normalizedVariant, LowFrictionOptionsLandingVariant, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(LifeOfferResolver.Normalize(offerKey), LifeOfferKeys.Life, StringComparison.OrdinalIgnoreCase))
            {
                return LowFrictionOptionsLandingVariant;
            }

            return null;
        }

        private static string? NormalizeLandingVariantKey(string? variant)
        {
            if (string.IsNullOrWhiteSpace(variant))
                return null;

            var normalized = variant.Trim().ToLowerInvariant();
            return normalized.Length == 0 ? null : normalized;
        }

        private static bool IsLandingRouteForOffer(string? landingPageUrl, string landingRoutePath)
        {
            if (string.IsNullOrWhiteSpace(landingPageUrl) || string.IsNullOrWhiteSpace(landingRoutePath))
                return false;

            if (Uri.TryCreate(landingPageUrl, UriKind.Absolute, out var absolute))
                return absolute.AbsolutePath.Contains(landingRoutePath, StringComparison.OrdinalIgnoreCase);

            if (Uri.TryCreate(landingPageUrl, UriKind.Relative, out var relative))
                return relative.OriginalString.Contains(landingRoutePath, StringComparison.OrdinalIgnoreCase);

            return landingPageUrl.Contains(landingRoutePath, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetLandingRoutePath(string offerKey)
        {
            var normalized = LifeOfferResolver.Normalize(offerKey);
            return normalized switch
            {
                LifeOfferKeys.Life => "/Quote/Life/landing",
                LifeOfferKeys.Mortgage => "/Quote/Mortgage-Protection/landing",
                LifeOfferKeys.FinalExpense => "/Quote/Final-Expense/landing",
                LifeOfferKeys.Term => "/Quote/Term-Life/landing",
                LifeOfferKeys.WholeLife => "/Quote/Whole-Life/landing",
                LifeOfferKeys.Iul => "/Quote/IUL/landing",
                _ => "/Quote/Life/landing"
            };
        }

        private static string GetQuoteRoutePath(string offerKey)
        {
            var normalized = LifeOfferResolver.Normalize(offerKey);
            return normalized switch
            {
                LifeOfferKeys.Mortgage => "/Quote/Mortgage-Protection",
                LifeOfferKeys.FinalExpense => "/Quote/Final-Expense",
                LifeOfferKeys.Term => "/Quote/Term-Life",
                LifeOfferKeys.WholeLife => "/Quote/Whole-Life",
                LifeOfferKeys.Iul => "/Quote/IUL",
                _ => "/Quote/Life"
            };
        }

        private UnifiedEventContext BuildTrackingContext(
            string quoteKey,
            WebsiteLead lead,
            string eventType,
            object metadata,
            string pageVariant,
            string pageMode,
            DateTime? eventUtc = null,
            string? quoteType = null)
        {
            return UnifiedEventContextBuilder.Build(
                httpContext: HttpContext,
                eventName: eventType,
                eventUtc: eventUtc,
                sessionId: lead.SessionId,
                visitorId: lead.VisitorId,
                pageKey: quoteKey,
                effectivePageKey: quoteKey,
                pageVariant: pageVariant,
                pageMode: pageMode,
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
                quoteType: string.IsNullOrWhiteSpace(quoteType) ? lead.InterestType : quoteType,
                metadata: metadata);
        }

        private async Task TryWriteLeadPipelineEventAsync(
            WebsiteLead lead,
            string quoteType,
            WizardPageMode pageMode,
            Guid correlationId,
            string eventType,
            object metadata)
        {
            AnalyticsEvent? analyticsEvent = null;
            try
            {
                var ctx = BuildTrackingContext(
                    pageMode.EffectivePageKey,
                    lead,
                    eventType,
                    metadata,
                    pageMode.PageVariant,
                    pageMode.PageMode,
                    DateTime.UtcNow,
                    quoteType);
                analyticsEvent = UnifiedEventMapper.ToAnalytics(ctx);
                UnifiedAnalyticsWriter.Write(_db, analyticsEvent);

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
                    "LifeQuote [{CorrelationId}]: failed to write lead pipeline event {EventType} for lead {LeadId}",
                    correlationId,
                    eventType,
                    lead.LeadId);
            }
        }

        private async Task<LifeWizardViewModel> BuildWizardViewModelAsync(LifeWizardConfig cfg, LifeQuoteFormModel model, WizardPageMode pageMode)
        {
            model.OfferKey = string.IsNullOrWhiteSpace(model.OfferKey) ? cfg.OfferKey : model.OfferKey;
            model.ProductType = string.IsNullOrWhiteSpace(model.ProductType) ? cfg.ProductType : model.ProductType;
            model.PageKey = pageMode.EffectivePageKey;
            model.PageVariant = pageMode.PageVariant;
            model.PageMode = pageMode.PageMode;

            return new LifeWizardViewModel
            {
                Config = cfg,
                Form = model,
                IsLandingPage = pageMode.IsLandingPage,
                PageVariant = pageMode.PageVariant,
                PageMode = pageMode.PageMode,
                EffectivePageKey = pageMode.EffectivePageKey,
                AgentTrustProfile = await BuildAgentTrustProfileAsync(HttpContext?.RequestAborted ?? CancellationToken.None)
            };
        }

        private void ApplyWizardViewData(LifeWizardViewModel vm)
        {
            ViewData["Title"] = vm.Config.PageTitle;
            ViewData["PageKey"] = vm.EffectivePageKey;
            ViewData["IsLandingPage"] = vm.IsLandingPage;
            ViewData["PageVariant"] = vm.PageVariant;
            ViewData["PageMode"] = vm.PageMode;
            ViewData["PageCategory"] = "quote";
            ViewData["QuoteTypeForTracking"] = vm.Config.OfferKey;
        }

        private static LifeWizardConfig GetWizardConfig(string rawOfferKey)
        {
            var key = LifeOfferResolver.Normalize(rawOfferKey);
            return WizardConfigs.TryGetValue(key, out var cfg) ? cfg : WizardConfigs[LifeOfferKeys.Life];
        }

        private readonly record struct WizardPageMode(
            bool IsLandingPage,
            string PageVariant,
            string PageMode,
            string EffectivePageKey);

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

        public sealed class MetaBrowserPixelAckRequest
        {
            public Guid LeadId { get; set; }
            public string? EventId { get; set; }
            public string? Status { get; set; }
            public string? Note { get; set; }
        }

        private static IReadOnlyDictionary<string, LifeWizardConfig> BuildConfigs()
        {
            return new Dictionary<string, LifeWizardConfig>(StringComparer.OrdinalIgnoreCase)
            {
                [LifeOfferKeys.Life] = new LifeWizardConfig
                {
                    OfferKey = LifeOfferKeys.Life,
                    ProductType = "life_general",
                    DisplayName = "Life Insurance",
                    PageKey = "quote_life",
                    PostAction = "SubmitLifeQuote",
                    Header = "Protect the people who count on you",
                    Subheader = "Get a clear picture of coverage options that match your needs, goals, and budget — before making any decisions.",
                    PageTitle = "Protect the People Who Count on You",
                    SubmitButtonText = "Show Me What Fits Me",
                    StartEvent = "life_general_form_start",
                    SubmitEvent = "life_general_submit",
                    Steps = BuildDiscoveryStepsForOffer(LifeOfferKeys.Life),
                },
                [LifeOfferKeys.Term] = new LifeWizardConfig
                {
                    OfferKey = LifeOfferKeys.Term,
                    ProductType = "life_term",
                    DisplayName = "Term Life",
                    PageKey = "quote_term_life",
                    PostAction = "SubmitTermLifeQuote",
                    Header = "Protect the years your family depends on most.",
                    Subheader = "Let’s help you compare term life protection based on your needs, goals, and budget.",
                    PageTitle = "Term Life Protection Review",
                    SubmitButtonText = "Show Me What Fits Me",
                    StartEvent = "life_term_form_start",
                    SubmitEvent = "life_term_submit",
                    Steps = BuildDiscoveryStepsForOffer(LifeOfferKeys.Term),
                },
                [LifeOfferKeys.WholeLife] = new LifeWizardConfig
                {
                    OfferKey = LifeOfferKeys.WholeLife,
                    ProductType = "life_whole",
                    DisplayName = "Whole Life",
                    PageKey = "quote_whole_life",
                    PostAction = "SubmitWholeLifeQuote",
                    Header = "Build lifelong protection for the people you love.",
                    Subheader = "Let’s shape whole life protection around your needs, goals, and long-term legacy plans.",
                    PageTitle = "Whole Life Protection Review",
                    SubmitButtonText = "Show Me What Fits Me",
                    StartEvent = "life_whole_form_start",
                    SubmitEvent = "life_whole_submit",
                    Steps = BuildDiscoveryStepsForOffer(LifeOfferKeys.WholeLife),
                },
                [LifeOfferKeys.FinalExpense] = new LifeWizardConfig
                {
                    OfferKey = LifeOfferKeys.FinalExpense,
                    ProductType = "life_finalexpense",
                    DisplayName = "Final Expense",
                    PageKey = "quote_final_expense",
                    PostAction = "SubmitFinalExpenseQuote",
                    Header = "Protect your loved ones from final expense stress.",
                    Subheader = "Let’s review final expense protection based on your needs, goals, and budget comfort.",
                    PageTitle = "Final Expense Protection Review",
                    SubmitButtonText = "Show Me What Fits Me",
                    StartEvent = "life_finalexpense_form_start",
                    SubmitEvent = "life_finalexpense_submit",
                    Steps = BuildDiscoveryStepsForOffer(LifeOfferKeys.FinalExpense),
                },
                [LifeOfferKeys.Mortgage] = new LifeWizardConfig
                {
                    OfferKey = LifeOfferKeys.Mortgage,
                    ProductType = "life_mp",
                    DisplayName = "Mortgage Protection",
                    PageKey = "quote_mortgage_protection",
                    PostAction = "SubmitMortgageQuote",
                    Header = "Protect what you’ve built for your family.",
                    Subheader = "Let’s map mortgage protection options around your needs, goals, and monthly reality.",
                    PageTitle = "Mortgage Protection Options Review",
                    SubmitButtonText = "Show Me What Fits Me",
                    StartEvent = "life_mp_form_start",
                    SubmitEvent = "life_mp_submit",
                    Steps = BuildDiscoveryStepsForOffer(LifeOfferKeys.Mortgage),
                },
                [LifeOfferKeys.Iul] = new LifeWizardConfig
                {
                    OfferKey = LifeOfferKeys.Iul,
                    ProductType = "life_iul",
                    DisplayName = "Indexed Universal Life",
                    PageKey = "quote_iul",
                    PostAction = "SubmitIulQuote",
                    Header = "Protect now while building for tomorrow.",
                    Subheader = "Let’s explore indexed universal life options based on your needs, goals, and long-term plans.",
                    PageTitle = "Indexed Universal Life Options Review",
                    SubmitButtonText = "Show Me What Fits Me",
                    StartEvent = "life_iul_form_start",
                    SubmitEvent = "life_iul_submit",
                    Steps = BuildDiscoveryStepsForOffer(LifeOfferKeys.Iul),
                },
            };
        }

        private static List<LifeWizardStep> BuildDiscoveryStepsForOffer(string offerKey)
        {
            var normalizedOfferKey = LifeOfferResolver.Normalize(offerKey);
            var protectOptions = normalizedOfferKey switch
            {
                LifeOfferKeys.FinalExpense => new List<LifeWizardOption>
                {
                    new("spouse_or_partner","My spouse or partner"),
                    new("children","My children or grandchildren"),
                    new("family","My loved ones"),
                },
                LifeOfferKeys.Mortgage => new List<LifeWizardOption>
                {
                    new("spouse_or_partner","My spouse or partner"),
                    new("children","My children"),
                    new("family","My household"),
                    new("just_me","Just me"),
                },
                LifeOfferKeys.WholeLife => new List<LifeWizardOption>
                {
                    new("spouse_or_partner","My spouse or partner"),
                    new("children","My children"),
                    new("family","My family or legacy"),
                },
                LifeOfferKeys.Iul => new List<LifeWizardOption>
                {
                    new("spouse_or_partner","My spouse or partner"),
                    new("children","My children"),
                    new("family","My family or legacy"),
                },
                _ => new List<LifeWizardOption>
                {
                    new("spouse_or_partner","My spouse or partner"),
                    new("children","My children"),
                    new("family","My family"),
                }
            };

            var goalOptions = normalizedOfferKey switch
            {
                LifeOfferKeys.Term => new List<LifeWizardOption>
                {
                    new("replace_income","Replace income for my family"),
                    new("mortgage_or_bills","Cover mortgage or major bills"),
                    new("protect_term_years","Protect the years my family needs it most"),
                    new("keep_costs_affordable","Keep coverage more affordable"),
                },
                LifeOfferKeys.WholeLife => new List<LifeWizardOption>
                {
                    new("lifelong_protection","Build lifelong protection"),
                    new("cash_value_growth","Build cash value over time"),
                    new("leave_legacy","Leave something behind"),
                    new("final_expenses","Cover final expenses permanently"),
                },
                LifeOfferKeys.FinalExpense => new List<LifeWizardOption>
                {
                    new("burial_costs","Cover burial and funeral costs"),
                    new("final_bills","Handle final medical or household bills"),
                    new("ease_family_burden","Reduce financial stress on my loved ones"),
                    new("leave_small_benefit","Leave a small benefit behind"),
                },
                LifeOfferKeys.Mortgage => new List<LifeWizardOption>
                {
                    new("mortgage_balance","The mortgage balance"),
                    new("monthly_payment","The monthly mortgage payment"),
                    new("stay_in_home","My spouse or family staying in the home"),
                    new("household_bills","The home plus key bills"),
                },
                LifeOfferKeys.Iul => new List<LifeWizardOption>
                {
                    new("cash_value_growth","Build cash value with long-term flexibility"),
                    new("lifelong_protection","Keep lifelong protection in place"),
                    new("future_access","Create future access to cash value"),
                    new("leave_legacy","Leave a legacy for my family"),
                },
                _ => new List<LifeWizardOption>
                {
                    new("replace_income","Replace income for my family"),
                    new("final_expenses","Cover final expenses"),
                    new("mortgage_or_bills","Help with mortgage or bills"),
                    new("leave_something","Leave something behind"),
                }
            };

            var coverageOptions = normalizedOfferKey switch
            {
                LifeOfferKeys.FinalExpense => new List<LifeWizardOption>
                {
                    new("25000","$25,000"),
                    new("50000","$50,000"),
                    new("75000","$75,000"),
                    new("100000","$100,000"),
                },
                LifeOfferKeys.WholeLife => new List<LifeWizardOption>
                {
                    new("50000","$50,000"),
                    new("100000","$100,000"),
                    new("250000","$250,000"),
                    new("500000","$500,000"),
                },
                LifeOfferKeys.Term => new List<LifeWizardOption>
                {
                    new("250000","$250,000"),
                    new("500000","$500,000"),
                    new("1000000","$1,000,000"),
                    new("2000000","$2,000,000+"),
                },
                LifeOfferKeys.Mortgage => new List<LifeWizardOption>
                {
                    new("150000","$150,000"),
                    new("250000","$250,000"),
                    new("500000","$500,000"),
                    new("750000","$750,000+"),
                },
                LifeOfferKeys.Iul => new List<LifeWizardOption>
                {
                    new("250000","$250,000"),
                    new("500000","$500,000"),
                    new("1000000","$1,000,000"),
                    new("2000000","$2,000,000+"),
                },
                _ => new List<LifeWizardOption>
                {
                    new("100000","$100,000"),
                    new("250000","$250,000"),
                    new("500000","$500,000"),
                    new("1000000","$1,000,000+"),
                }
            };

            var goalQuestion = normalizedOfferKey switch
            {
                LifeOfferKeys.Term => "What would you want term life to help protect first?",
                LifeOfferKeys.WholeLife => "What would you want whole life to help with most?",
                LifeOfferKeys.FinalExpense => "What would you want final expense coverage to help with first?",
                LifeOfferKeys.Mortgage => "What would you want protected if something happened to you?",
                LifeOfferKeys.Iul => "What would you want this IUL coverage to help with most?",
                _ => "What would you want this coverage to help with first?"
            };

            var protectQuestion = normalizedOfferKey switch
            {
                LifeOfferKeys.WholeLife => "Who would you want that protection to support?",
                LifeOfferKeys.FinalExpense => "Who could be left carrying those costs?",
                LifeOfferKeys.Mortgage => "Who depends on the home?",
                LifeOfferKeys.Iul => "Who would you want that protection to support?",
                _ => "Who depends on you most?"
            };

            var coverageQuestion = normalizedOfferKey switch
            {
                LifeOfferKeys.Term => "About how much term coverage would you like to explore?",
                LifeOfferKeys.WholeLife => "About how much whole life coverage would you like to explore?",
                LifeOfferKeys.FinalExpense => "About how much final expense coverage would you like to explore?",
                LifeOfferKeys.Mortgage => "Approximate mortgage balance",
                LifeOfferKeys.Iul => "About how much IUL coverage would you like to explore?",
                _ => "About how much coverage would you like to explore?"
            };

            return
            new()
            {
                new(goalQuestion, goalOptions, "CoverageGoal"),
                new(protectQuestion, protectOptions, "ProtectingWho"),
                new(coverageQuestion, coverageOptions, "CoverageAmountOption"),
                new("Your age", new List<LifeWizardOption>(), "Age"),
                new("Tobacco use", new List<LifeWizardOption>
                {
                    new("non_smoker","Non-smoker"),
                    new("smoker","Smoker"),
                }, "TobaccoUse"),
            };
        }
    }
}
