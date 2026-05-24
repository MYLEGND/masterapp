using Microsoft.AspNetCore.Mvc;
using Domain.Entities;
using Infrastructure.Data;
using Protect_Website.Models;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using System.Text.Json;
using Infrastructure.Leads;
using ProtectWebsite.Services.Meta;
using ProtectWebsite.Services;
using ProtectWebsite.Services.Tracking;

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
        private readonly IWebsiteLifeLeadCaptureService _websiteLeadCapture;
        private readonly ILogger<HomeQuoteController> _logger;

        public HomeQuoteController(IConfiguration configuration, AgentTrackingResolver resolver,
            MasterAppDbContext db, IMetaConversionsApiService metaConversionsApi, IMetaPixelResolutionService metaPixelResolution, IWebsiteLifeLeadCaptureService websiteLeadCapture, ILogger<HomeQuoteController> logger)
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
                return View("~/Views/Quote/Home.cshtml", model);

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

            // ── 2. Send email (failure does not lose the lead) ────────────────────
            try
            {
                var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                var graphClient = new GraphServiceClient(credential);

                bool hasPrevAddr = !string.IsNullOrWhiteSpace(model.PreviousAddress) ||
                                   !string.IsNullOrWhiteSpace(model.PreviousCity) ||
                                   !string.IsNullOrWhiteSpace(model.PreviousState);

                var rows = new LeadEmailTemplate.RowBuilder()
                    .Section("Applicant Information")
                    .Row("Name",            $"{model.FirstName} {model.LastName}".Trim())
                    .Row("Nickname",        model.Nickname)
                    .Row("Gender",          model.Gender)
                    .Row("Date of Birth",   LeadEmailTemplate.Date(model.DOB))
                    .Row("Marital Status",  model.MaritalStatus)
                    .Row("Driver’s License",model.DriversLicenseNumber)
                    .Row("DL Status",       model.DLStatus)
                    .Row("DL State",        model.DLState)
                    .Row("Education",       model.Education)
                    .Row("Industry",        model.Industry)
                    .Row("Address State",   model.AddressState)
                    .Row("Postal Code",     model.PostalCode)
                    .Section("Primary Address")
                    .Row("Address",         $"{model.PrimaryAddress} {model.PrimaryUnit}".Trim())
                    .Row("City",            model.PrimaryCity)
                    .Row("State",           model.PrimaryState)
                    .Row("Country",         model.PrimaryCountry)
                    .Row("Postal Code",     model.PrimaryPostalCode)
                    .Row("Years at Address",model.PrimaryYearsAtAddress);

                if (hasPrevAddr)
                    rows.Section("Previous Address")
                        .Row("Address",         $"{model.PreviousAddress} {model.PreviousUnit}".Trim())
                        .Row("City",            model.PreviousCity)
                        .Row("State",           model.PreviousState)
                        .Row("Country",         model.PreviousCountry)
                        .Row("Postal Code",     model.PreviousPostalCode)
                        .Row("Years at Address",model.PreviousYearsAtAddress);

                rows.Section("Contact Information")
                    .Row("Phone",                    model.PhoneNumber)
                    .Row("Email",                    model.EmailAddress)
                    .Row("Preferred Contact Method", model.PreferredContactMethod)
                    .Row("Best Time to Contact",     model.BestTimeToContact)
                    .Section("Policy Information")
                    .Row("Policy / Form Type",         model.PolicyFormType)
                    .Row("Prior Carrier",              model.PriorCarrier)
                    .Row("Current Policy Expiration",  LeadEmailTemplate.Date(model.CurrentPolicyExpirationDate))
                    .Row("Prior Policy Premium",       model.PriorPolicyPremium)
                    .Row("Years / Months with Carrier",$"{model.YearsWithPriorCarrier} yrs / {model.MonthsWithPriorCarrier} mo")
                    .Row("Continuous Coverage",        $"{model.YearsContinuousCoverage} yrs / {model.MonthsContinuousCoverage} mo")
                    .Row("Credit Check Authorized",    model.CreditCheckAuthorized)
                    .Row("Quote as Package",           model.QuoteAsPackage)
                    .Row("New Policy Effective Date",  LeadEmailTemplate.Date(model.NewPolicyEffectiveDate))
                    .Section("Underwriting")
                    .Row("Cancelled/Declined (5yr)",   model.CancelledDeclinedNonRenewedLast5Years)
                    .Row("Home Under Construction",    model.HomeUnderConstruction)
                    .Row("Business/Daycare on Premises",model.BusinessOrDaycareOnPremises)
                    .Row("# of Employees",             model.NumberOfEmployees)
                    .Row("Swimming Pool",              model.SwimmingPoolOnPremises)
                    .Row("Dogs on Premises",           model.DogsOnPremises)
                    .Row("Paperless",                  model.Paperless)
                    .Row("# of Animals on Premises",   model.NumberOfAnimalsOnPremises)
                    .Row("Lapse in Coverage (12mo)",   model.LapseInCoveragePast12Months)
                    .Row("Auto Years w/ Prior Carrier", model.AutoYearsWithPriorCarrierOrAgent)
                    .Row("Additional Notes",           model.AdditionalCarrierQuestions)
                    .Section("Dwelling Information")
                    .Row("Dwelling Usage",    model.DwellingUsage)
                    .Row("Occupancy Type",    model.OccupancyType)
                    .Row("Dwelling Type",     model.DwellingType)
                    .Row("# of Occupants",    model.NumberOfOccupants)
                    .Row("# of Stories",      model.NumberOfStories)
                    .Row("Square Footage",    model.SquareFootage)
                    .Row("Year Built",        model.YearBuilt)
                    .Row("Construction Style",model.ConstructionStyle)
                    .Row("Roof Type",         model.RoofTypeMainMaterial)
                    .Row("Foundation Type",   model.FoundationType)
                    .Row("Roof Design",       model.RoofDesign)
                    .Row("Exterior Walls",    model.ExteriorWalls)
                    .Section("Protection & Systems")
                    .Row("Full Baths",        model.FullBaths)
                    .Row("Half Baths",        model.HalfBaths)
                    .Row("Wood Burning Stoves",model.WoodBurningStoves)
                    .Row("Burglar Alarm",     model.BurglarAlarm)
                    .Row("Fire Detection",    model.FireDetection)
                    .Row("Sprinkler System",  model.SprinklerSystem)
                    .Row("Smoke Detector",    model.SmokeDetector)
                    .Section("Geographical Info")
                    .Row("Purchase Price",            model.PurchasePrice)
                    .Row("Purchase Date",             LeadEmailTemplate.Date(model.PurchaseDate))
                    .Row("Distance from Fire Station", model.DistanceFromFireStationMiles)
                    .Row("Feet from Hydrant",         model.FeetFromHydrant)
                    .Section("House Updates")
                    .Row("Heating",   $"{model.HeatingUpdate} ({model.HeatingYearUpdated})")
                    .Row("Electrical",$"{model.ElectricalUpdate} ({model.ElectricalYearUpdated})")
                    .Row("Plumbing",  $"{model.PlumbingUpdate} ({model.PlumbingYearUpdated})")
                    .Row("Roofing",   $"{model.RoofingUpdate} ({model.RoofingYearUpdated})")
                    .Section("General Coverages")
                    .Row("Dwelling Coverage",        model.DwellingCoverage)
                    .Row("Est. Replacement Cost",    model.EstReplacementCost)
                    .Row("Personal Property",        model.PersonalProperty)
                    .Row("Loss of Use",              model.LossOfUse)
                    .Row("Personal Liability",       model.PersonalLiability)
                    .Row("Medical Payments",         model.MedicalPayments)
                    .Row("All Perils Deductible",    model.AllPerilsDeductible)
                    .Row("Theft Deductible",         model.TheftDeductible)
                    .Row("Wind Deductible",          model.WindDeductible)
                    .Section("Financial Interests")
                    .Row("First Mortgagee",          model.FirstMortgagee)
                    .Row("Second Mortgagee",         model.SecondMortgagee)
                    .Row("Third Mortgagee",          model.ThirdMortgagee)
                    .Row("Cosigner",                 model.Cosigner)
                    .Row("Equity Line of Credit",    model.EquityLineOfCredit)
                    .Row("# of Other Interests",     model.NumberOfOtherInterests)
                    .Section("Endorsements")
                    .Row("Building Additions",            model.BuildingAdditionsOrAlterations)
                    .Row("Increased Replacement Cost %",  model.IncreasedReplacementCostDwellingPercentage)
                    .Row("Loss Assessment",               model.LossAssessment)
                    .Row("Ordinance or Law",              model.OrdinanceOrLaw)
                    .Row("Increased Credit Card Coverage",model.IncreasedCoverageOnCreditCard)
                    .Row("Jewelry/Watches/Furs Limit",    model.IncreasedLimitJewelryWatchesFurs)
                    .Row("Water Backup",                  model.WaterBackup)
                    .Row("Mold Property Damage",          model.IncreasedMoldPropertyDamage)
                    .Row("Personal Injury",               model.PersonalInjury)
                    .Row("Special Personal Property",     model.SpecialPersonalProperty)
                    .Row("Sinkhole Collapse",             model.SinkholeCollapse)
                    .Section("Earthquake")
                    .Row("Earthquake Zone",  model.EarthquakeZone)
                    .Row("EQ Deductible",    model.EarthquakeDeductible)
                    .Row("Percent Veneer",   model.PercentVeneer)
                    .Section("Authorization")
                    .Row("Disclaimer Acknowledged", LeadEmailTemplate.Bool(model.AcknowledgedDisclaimer));

                var message = new Message
                {
                    Subject = $"[HOME QUOTE] New Lead | {model.FirstName} {model.LastName}",
                    Body = new ItemBody
                    {
                        ContentType = BodyType.Html,
                        Content = LeadEmailTemplate.Wrap("New Quote — Home Insurance", rows.ToString())
                    },
                    ToRecipients = new List<Recipient>
                    {
                        new Recipient { EmailAddress = new EmailAddress { Address = leadRecipientEmail } }
                    }
                };

                var requestBody = new SendMailPostRequestBody
                {
                    Message = message,
                    SaveToSentItems = true
                };

                await graphClient.Users[senderEmail].SendMail.PostAsync(requestBody);
                _logger.LogInformation(
                    "HomeQuote [{CorrelationId}]: email sent to {Recipient} for lead {LeadId}",
                    correlationId, leadRecipientEmail, lead.LeadId);
            }
            catch (Exception emailEx)
            {
                _logger.LogError(emailEx,
                    "HomeQuote [{CorrelationId}]: email send failed for lead {LeadId} — lead is saved, continuing",
                    correlationId, lead.LeadId);
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
    }
}
