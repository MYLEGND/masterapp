using Microsoft.AspNetCore.Mvc;
using Domain.Entities;
using Infrastructure.Data;
using Protect_Website.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Infrastructure.Leads;
using ProtectWebsite.Services.Meta;
using ProtectWebsite.Services;
using ProtectWebsite.Services.Tracking;
using ProtectWebsite.Services.Communication;

namespace Protect_Website.Controllers
{
    [Route("Quote")]
    public class AutoQuoteController : Controller
    {
        private readonly string tenantId;
        private readonly string clientId;
        private readonly string clientSecret;

        private readonly string senderEmail;
        private readonly string recipientEmail;
        private readonly AgentTrackingResolver _resolver;
        private readonly MasterAppDbContext _db;
        private readonly IMetaConversionsApiService _metaConversionsApi;
        private readonly IMetaPixelResolutionService _metaPixelResolution;
        private readonly IWebsiteLifeLeadCaptureService _websiteLeadCapture;
        private readonly ILogger<AutoQuoteController> _logger;
        private readonly IProtectEmailSender _emailSender;

        public AutoQuoteController(IConfiguration configuration, AgentTrackingResolver resolver,
            MasterAppDbContext db, IMetaConversionsApiService metaConversionsApi, IMetaPixelResolutionService metaPixelResolution, IWebsiteLifeLeadCaptureService websiteLeadCapture, IProtectEmailSender emailSender, ILogger<AutoQuoteController> logger)
        {
            tenantId = configuration["AzureAd:TenantId"] ?? throw new ArgumentNullException("AzureAd:TenantId");
            clientId = configuration["AzureAd:ClientId"] ?? throw new ArgumentNullException("AzureAd:ClientId");
            clientSecret = configuration["AzureAd:ClientSecret"] ?? throw new ArgumentNullException("AzureAd:ClientSecret");

            senderEmail = configuration["Contact:SenderEmail"] ?? throw new ArgumentNullException("Contact:SenderEmail");
            recipientEmail = configuration["Contact:RecipientEmail"] ?? throw new ArgumentNullException("Contact:RecipientEmail");
            _resolver = resolver;
            _db = db;
            _metaConversionsApi = metaConversionsApi;
            _metaPixelResolution = metaPixelResolution;
            _websiteLeadCapture = websiteLeadCapture;
            _emailSender = emailSender;
            _logger = logger;
        }

        [HttpGet("Auto")]
        public IActionResult Auto()
        {
            var model = new AutoQuoteFormModel();

            if (model.Drivers.Count == 0) model.Drivers.Add(new Driver());
            if (model.Vehicles.Count == 0) model.Vehicles.Add(new Vehicle());

            return View("~/Views/Quote/Auto.cshtml", model);
        }

        [HttpPost("Auto")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Auto(AutoQuoteFormModel model)
        {
            NormalizeLists(model);

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

            var correlationId = Guid.NewGuid();

            var (leadRecipientEmail, agentProfileId, agentSlug, isFounderPath) = await ResolveLeadContextAsync();
            _logger.LogInformation(
                "AutoQuote [{CorrelationId}]: attribution resolved AgentSlug={Slug} ProfileId={ProfileId} Recipient={Recipient}",
                correlationId, agentSlug, agentProfileId, leadRecipientEmail);
            var resolvedMetaPixel = await _metaPixelResolution.ResolveForLeadAsync(
                agentProfileId,
                agentSlug,
                isFounderPath,
                HttpContext?.RequestAborted ?? CancellationToken.None);

            // business rule
            if (model.Drivers.Count == 0)
                ModelState.AddModelError(nameof(model.Drivers), "At least one driver is required.");
            if (model.Vehicles.Count == 0)
                ModelState.AddModelError(nameof(model.Vehicles), "At least one vehicle is required.");
        if (!ModelState.IsValid)
            {
                EnsureIndexZero(model);
                ViewData["StartStep"] = GetFirstErrorStep(ModelState);
                return View("~/Views/Quote/Auto.cshtml", model);
            }

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
                    Email         = model.EmailAddress?.Trim() ?? "",
                    Phone         = string.IsNullOrWhiteSpace(model.PhoneNumber) ? null : model.PhoneNumber?.Trim(),
                    InterestType  = "auto_insurance",
                    SourcePageKey = "quote_auto",
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
                    CallTextConsent = model.AcknowledgedDisclaimer && !string.IsNullOrWhiteSpace(model.PhoneNumber),
                    TermsAccepted = true,
                    Host          = Request?.Host.ToString(),
                    Environment   = EnvironmentLabelResolver.Resolve(),
                    CreatedUtc    = now,
                    Status        = "New",
                    AgentTrackingProfileId = agentProfileId,
                    AgentSlug     = agentSlug,
                    MetadataJson  = JsonSerializer.Serialize(new
                    {
                        AddressState   = model.AddressState,
                        DriverCount    = model.Drivers.Count,
                        VehicleCount   = model.Vehicles.Count,
                        PriorCarrier   = model.PriorCarrier,
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
                    "AutoQuote [{CorrelationId}]: WebsiteLead {LeadId} saved",
                    correlationId, lead.LeadId);
            }
            catch (Exception persistEx)
            {
                _logger.LogError(persistEx,
                    "AutoQuote [{CorrelationId}]: lead persistence failed for {Email}",
                    correlationId, model.EmailAddress);
                ModelState.AddModelError("", $"Failed to save lead: {persistEx.Message}");
                EnsureIndexZero(model);
                ViewData["StartStep"] = 5;
                return View("~/Views/Quote/Auto.cshtml", model);
            }

            async Task TryWriteLeadEventAsync(string eventType, object metadata, DateTime? eventUtc = null)
            {
                AnalyticsEvent? analyticsEvent = null;
                try
                {
                    analyticsEvent = WebsiteLeadAnalyticsWriter.CreateEvent(
                        lead,
                        eventType,
                        "quote_auto",
                        "auto_insurance",
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
                        "AutoQuote [{CorrelationId}]: analytics event write failed for {EventType} lead {LeadId}",
                        correlationId,
                        eventType,
                        lead.LeadId);
                }
            }

            await TryWriteLeadEventAsync(
                "lead_persisted",
                new { LeadId = lead.LeadId, CorrelationId = correlationId, QuoteType = "auto_insurance" },
                lead.CreatedUtc);

            try
            {
                await TryWriteLeadEventAsync(
                    "workstation_capture_attempt",
                    new { LeadId = lead.LeadId, CorrelationId = correlationId, ProductType = "auto", OfferKey = "auto" });

                var captureResult = await _websiteLeadCapture.UpsertAsync(
                    new WebsiteLifeLeadCaptureRequest
                    {
                        WebsiteLeadId = lead.LeadId,
                        SubmittedUtc = lead.CreatedUtc,
                        ProductType = "auto",
                        OfferKey = "auto",
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
                    "AutoQuote [{CorrelationId}]: workstation capture failed for lead {LeadId}",
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
                    QuoteType = "Auto",
                    PageKey = "quote_auto",
                    OfferKey = "auto",
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

            
            var primary = model.Drivers.FirstOrDefault();
            var subjectName = primary == null ? "Unknown" : $"{primary.FirstName} {primary.LastName}".Trim();

                string Nv(string? s) => string.IsNullOrWhiteSpace(s) ? "N/A" : s.Trim();
                string Nd(DateTime? d) => d.HasValue ? d.Value.ToString("MM/dd/yyyy") : "N/A";

                string DriverNameByIndex(int? idx)
                {
                    if (!idx.HasValue || idx.Value < 0 || idx.Value >= model.Drivers.Count) return "N/A";
                    var dr = model.Drivers[idx.Value];
                    return $"{dr.FirstName} {dr.LastName}".Trim();
                }

                string VehicleLabelByIndex(int? idx)
                {
                    if (!idx.HasValue || idx.Value < 0 || idx.Value >= model.Vehicles.Count) return "N/A";
                    var v = model.Vehicles[idx.Value];
                    return $"{v.Year} {v.Make} {v.Model} (VIN: {v.VIN})".Trim();
                }

            var rows = new LeadEmailTemplate.RowBuilder()
                .Section("Applicant Info")
                .Row("Name",              $"{model.FirstName} {model.LastName}".Trim())
                .Row("Address State",     Nv(model.AddressState))
                .Row("Postal Code",       Nv(model.PostalCode))
                .Row("Nickname",          model.Nickname)
                .Row("Gender",            model.Gender)
                .Row("Date of Birth",     Nd(model.DOB))
                .Row("Marital Status",    model.MaritalStatus)
                .Row("Driver's License",  model.DriversLicenseNumber)
                .Row("DL Status",         model.DLStatus)
                .Row("DL State",          model.DLState)
                .Row("Education",         model.Education)
                .Row("Industry",          model.Industry)
                .Section("Primary Address")
                .Row("Address",       model.PrimaryAddress)
                .Row("Unit",          model.PrimaryUnit)
                .Row("City",          model.PrimaryCity)
                .Row("State",         model.PrimaryState)
                .Row("Country",       model.PrimaryCountry)
                .Row("Postal Code",   model.PrimaryPostalCode)
                .Row("Years at Address", model.PrimaryYearsAtAddress);

            bool hasPrev = !string.IsNullOrWhiteSpace(model.PreviousAddress) ||
                           !string.IsNullOrWhiteSpace(model.PreviousCity) ||
                           !string.IsNullOrWhiteSpace(model.PreviousState);
            if (hasPrev)
            {
                rows.Section("Previous Address")
                    .Row("Address",       model.PreviousAddress)
                    .Row("Unit",          model.PreviousUnit)
                    .Row("City",          model.PreviousCity)
                    .Row("State",         model.PreviousState)
                    .Row("Country",       model.PreviousCountry)
                    .Row("Postal Code",   model.PreviousPostalCode)
                    .Row("Years at Address", model.PreviousYearsAtAddress);
            }

            rows.Section("Contact Info")
                .Row("Phone Type",               model.PhoneType)
                .Row("Phone Number",             model.PhoneNumber)
                .Row("Email",                    model.EmailAddress)
                .Row("Preferred Contact Method", model.PreferredContactMethod)
                .Row("Best Time to Contact",     model.BestTimeToContact)
                .Section("Policy Info")
                .Row("Prior Carrier",             model.PriorCarrier)
                .Row("Prior Policy Expiration",   Nd(model.PriorPolicyExpirationDate))
                .Row("Prior Liability Limits",    model.PriorLiabilityLimits)
                .Row("Prior Policy Term",         model.PriorPolicyTerm)
                .Row("Prior Policy Premium",      model.PriorPolicyPremium)
                .Row("Years / Months with Carrier", $"{Nv(model.YearsWithPriorCarrier)} yrs / {Nv(model.MonthsWithPriorCarrier)} mo")
                .Row("Continuous Coverage",       $"{Nv(model.YearsContinuousCoverage)} yrs / {Nv(model.MonthsContinuousCoverage)} mo")
                .Row("Credit Reports Authorized", model.CreditCheckAuthorized)
                .Row("New Policy Term",           model.NewPolicyTerm)
                .Row("Package Policy",            model.PackagePolicy)
                .Row("New Policy Effective Date", Nd(model.NewPolicyEffectiveDate))
                .Row("Additional Notes",          model.AdditionalCarrierQuestions)
                .Row("Paperless",                 model.Paperless)
                .Row("Multi-Policy Discount",     model.MultiPolicyDiscount);

            rows.Section("Drivers");
            for (int i = 0; i < model.Drivers.Count; i++)
            {
                var d = model.Drivers[i];
                rows.Section(i == 0 ? "Driver 1 — Primary Insured" : $"Driver {i + 1}")
                    .Row("Name",             $"{d.FirstName} {d.LastName}".Trim())
                    .Row("Date of Birth",    Nd(d.DOB))
                    .Row("Gender",           d.Gender)
                    .Row("Marital Status",   d.MaritalStatus)
                    .Row("Occupation",       $"{d.OccupationIndustry} / {d.OccupationTitle}".Trim(' ', '/'))
                    .Row("DL Status",        d.DLStatus)
                    .Row("Age Licensed",     d.AgeLicensed)
                    .Row("DL # / State",     $"{d.DLNumber} / {d.DLState}".Trim(' ', '/'))
                    .Row("Defensive Driver Course", Nd(d.DefensiveDriverCourseDate))
                    .Row("License Suspended (5yr)", d.LicenseSuspendedLast5Years)
                    .Row("Driver Education", d.DriverEducation)
                    .Row("Mature Driver",    d.MatureDriver)
                    .Row("Good Driver",      d.GoodDriver)
                    .Row("Telematics Discount", d.TelematicsDiscount)
                    .Row("Military Service", d.MilitaryService);
            }

            rows.Section("Vehicles");
            for (int i = 0; i < model.Vehicles.Count; i++)
            {
                var v = model.Vehicles[i];
                rows.Section(i == 0 ? "Vehicle 1 — Primary" : $"Vehicle {i + 1}")
                    .Row("Year / Make / Model", $"{v.Year} {v.Make} {v.Model}".Trim())
                    .Row("VIN",                 v.VIN)
                    .Row("Purchase Date",       Nd(v.PurchaseDate))
                    .Row("Vehicle Use",         v.Use)
                    .Row("Annual Miles",        v.AnnualMiles)
                    .Row("Passive Restraints",  v.PassiveRestraints)
                    .Row("Anti-Theft",          v.AntiTheft)
                    .Row("Anti-Lock Brakes",    v.AntiLockBrakes)
                    .Row("Daytime Running Lights", v.DaytimeRunningLights)
                    .Row("Cost New Value",      v.CostNewValue)
                    .Row("Modification Value",  v.ModificationValue)
                    .Row("Was New",             v.WasNew)
                    .Row("Carpool",             v.Carpool)
                    .Row("Telematics",          v.Telematics)
                    .Row("TNC",                 v.TNC)
                    .Row("Performance",         v.Performance)
                    .Row("Ownership Type",      v.OwnershipType)
                    .Row("Assigned Driver",     DriverNameByIndex(v.AssignedDriverIndex))
                    .Row("Comprehensive",       v.Comprehensive)
                    .Row("Collision",           v.Collision)
                    .Row("Towing & Labor",      v.Towing)
                    .Row("Rental Expense",      v.Rental)
                    .Row("Loan/Lease Coverage", v.LoanLease)
                    .Row("Liability",           v.Liability)
                    .Row("Special Equipment",   v.SpecialEquipment)
                    .Row("Branded Title",       v.BrandedTitle)
                    .Row("Custom Equipment",    v.CustomEquipment);
            }

            if (model.Accidents.Any())
            {
                rows.Section("Accidents");
                foreach (var a in model.Accidents)
                {
                    string veh = a.VehicleIndex.HasValue ? VehicleLabelByIndex(a.VehicleIndex) : (a.VehicleInvolvedText ?? "");
                    rows.Row("Date / Driver", $"{Nd(a.Date)} / {DriverNameByIndex(a.DriverIndex)}")
                        .Row("Description",   a.Description)
                        .Row("Property Damage / Bodily Injury", $"{Nv(a.PropertyDamageAmount)} / {Nv(a.BodilyInjuryAmount)}")
                        .Row("Collision / Medical", $"{Nv(a.CollisionAmount)} / {Nv(a.MedicalPaymentAmount)}")
                        .Row("Vehicle Involved", veh);
                }
            }

            if (model.Violations.Any())
            {
                rows.Section("Violations");
                foreach (var v in model.Violations)
                    rows.Row("Date / Driver", $"{Nd(v.Date)} / {DriverNameByIndex(v.DriverIndex)}")
                        .Row("Description", v.Description);
            }

            if (model.CompLosses.Any())
            {
                rows.Section("Comp Losses");
                foreach (var c in model.CompLosses)
                    rows.Row("Date / Driver", $"{Nd(c.Date)} / {DriverNameByIndex(c.DriverIndex)}")
                        .Row("Description", c.LossDescription);
            }

            rows.Section("General Coverage")
                .Row("Bodily Injury",          model.BodilyInjury)
                .Row("Uninsured Motorist",     model.UninsuredMotorist)
                .Row("Underinsured Motorist",  model.UnderinsuredMotorist)
                .Row("Medical Payments",       model.MedicalPayments)
                .Row("Residence Type",         model.ResidenceType)
                .Section("Authorization")
                .Row("Disclaimer Acknowledged", LeadEmailTemplate.Bool(model.AcknowledgedDisclaimer));

            var emailBody = LeadEmailTemplate.Wrap("New Quote — Auto Insurance", rows.ToString());

// ── 2. Send email through unified sender ───────────────────────────────
            var emailSent = await _emailSender.TrySendAsync(
                leadRecipientEmail,
                $"[AUTO] Quote Request | {subjectName}",
                emailBody,
                saveToSentItems: true,
                cancellationToken: HttpContext?.RequestAborted ?? CancellationToken.None);

            if (emailSent)
            {
                _logger.LogInformation(
                    "AutoQuote [{CorrelationId}]: email sent to {Recipient} for lead {LeadId}",
                    correlationId, leadRecipientEmail, lead.LeadId);
            }
            else
            {
                _logger.LogError(
                    "AutoQuote [{CorrelationId}]: email failed to {Recipient} for lead {LeadId} - lead is saved, continuing",
                    correlationId, leadRecipientEmail, lead.LeadId);
            }

            // ── 3. Write analytics event ─────────────────────────────────────────
            await TryWriteLeadEventAsync(
                "website_lead_submitted",
                new { LeadId = lead.LeadId, CorrelationId = correlationId },
                lead.CreatedUtc);

            TempData["QuoteType"] = "Auto";
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

        private static void NormalizeLists(AutoQuoteFormModel model)
        {
            model.Drivers ??= new List<Driver>();
            model.Vehicles ??= new List<Vehicle>();
            model.Accidents ??= new List<Accident>();
            model.Violations ??= new List<Violation>();
            model.CompLosses ??= new List<CompLoss>();
        }

        private static void EnsureIndexZero(AutoQuoteFormModel model)
        {
            if (model.Drivers.Count == 0) model.Drivers.Add(new Driver());
            if (model.Vehicles.Count == 0) model.Vehicles.Add(new Vehicle());
        }

        private static int GetFirstErrorStep(ModelStateDictionary ms)
        {
            bool HasPrefix(string p) =>
                ms.Where(k => k.Key != null && k.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                  .Any(v => v.Value?.Errors?.Count > 0);

            bool HasKey(string k) =>
                ms.TryGetValue(k, out var v) && v.Errors.Count > 0;

            // Step 5
            if (HasKey(nameof(AutoQuoteFormModel.BodilyInjury)) ||
                HasKey(nameof(AutoQuoteFormModel.UninsuredMotorist)) ||
                HasKey(nameof(AutoQuoteFormModel.UnderinsuredMotorist)) ||
                HasKey(nameof(AutoQuoteFormModel.MedicalPayments)) ||
                HasKey(nameof(AutoQuoteFormModel.ResidenceType)) ||
                HasKey(nameof(AutoQuoteFormModel.AcknowledgedDisclaimer)) ||
                HasPrefix("Vehicles[") && (HasPrefix("Vehicles[0].Comprehensive") || HasPrefix("Vehicles[0].Collision") || HasPrefix("Vehicles[0].Towing") || HasPrefix("Vehicles[0].Rental") || HasPrefix("Vehicles[0].SpecialEquipment") || HasPrefix("Vehicles[0].BrandedTitle")))
                return 5;

            // Step 4
            if (HasPrefix("Accidents[") || HasPrefix("Violations[") || HasPrefix("CompLosses["))
                return 4;

            // Step 3
            if (HasPrefix("Vehicles["))
                return 3;

            // Step 2
            if (HasPrefix("Drivers["))
                return 2;

            return 1;
        }
    }
}
