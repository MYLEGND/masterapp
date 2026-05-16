using Microsoft.AspNetCore.Mvc;
using Domain.Entities;
using Infrastructure.Data;
using Protect_Website.Models;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using ProtectWebsite.Services.Meta;
using ProtectWebsite.Services;
using ProtectWebsite.Services.Tracking;

namespace Protect_Website.Controllers
{
    [Route("Quote")]
    public class CommercialQuoteController : Controller
    {
        private readonly string tenantId;
        private readonly string clientId;
        private readonly string clientSecret;

        private readonly string senderEmail;
        private readonly string recipientEmail;
        private readonly AgentTrackingResolver _resolver;
        private readonly MasterAppDbContext _db;
        private readonly IMetaConversionsApiService _metaConversionsApi;
        private readonly ILogger<CommercialQuoteController> _logger;

        public CommercialQuoteController(IConfiguration configuration, AgentTrackingResolver resolver,
            MasterAppDbContext db, IMetaConversionsApiService metaConversionsApi, ILogger<CommercialQuoteController> logger)
        {
            tenantId = configuration["AzureAd:TenantId"] ?? throw new ArgumentNullException("AzureAd:TenantId");
            clientId = configuration["AzureAd:ClientId"] ?? throw new ArgumentNullException("AzureAd:ClientId");
            clientSecret = configuration["AzureAd:ClientSecret"] ?? throw new ArgumentNullException("AzureAd:ClientSecret");

            senderEmail = configuration["Contact:SenderEmail"] ?? throw new ArgumentNullException("Contact:SenderEmail");
            recipientEmail = configuration["Contact:RecipientEmail"] ?? throw new ArgumentNullException("Contact:RecipientEmail");
            _resolver = resolver;
            _db = db;
            _metaConversionsApi = metaConversionsApi;
            _logger = logger;
        }

        [HttpGet("Commercial")]
        public IActionResult Commercial()
        {
            var model = new CommercialQuoteFormModel();
            return View("~/Views/Quote/Commercial.cshtml", model);
        }

        [HttpPost("Commercial")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Commercial(CommercialQuoteFormModel model)
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

            // Keep user on same step if server-side validation fails
            if (!ModelState.IsValid)
            {
                ViewData["StartStep"] = model.CurrentStep <= 0 ? 1 : model.CurrentStep;
                return View("~/Views/Quote/Commercial.cshtml", model);
            }

            var correlationId = Guid.NewGuid();

            var (leadRecipientEmail, agentProfileId, agentSlug) = await ResolveLeadContextAsync();
            _logger.LogInformation(
                "CommercialQuote [{CorrelationId}]: attribution resolved AgentSlug={Slug} ProfileId={ProfileId} Recipient={Recipient}",
                correlationId, agentSlug, agentProfileId, leadRecipientEmail);

            // ── 1. Persist lead FIRST ─────────────────────────────────────────────
            WebsiteLead lead;
            try
            {
                var now = DateTime.UtcNow;
                lead = new WebsiteLead
                {
                    LeadId        = Guid.NewGuid(),
                    FirstName     = model.InsuredFirstName?.Trim() ?? "",
                    LastName      = string.IsNullOrWhiteSpace(model.InsuredLastName) ? null : model.InsuredLastName.Trim(),
                    Email         = model.BusinessEmail?.Trim() ?? "",
                    Phone         = string.IsNullOrWhiteSpace(model.BusinessPhone) ? null : model.BusinessPhone?.Trim(),
                    InterestType  = "commercial_insurance",
                    SourcePageKey = "quote_commercial",
                    UtmSource     = string.IsNullOrWhiteSpace(model.UtmSource)   ? null : model.UtmSource.Trim(),
                    UtmMedium     = string.IsNullOrWhiteSpace(model.UtmMedium)   ? null : model.UtmMedium.Trim(),
                    UtmCampaign   = string.IsNullOrWhiteSpace(model.UtmCampaign) ? null : model.UtmCampaign.Trim(),
                    Fbclid        = string.IsNullOrWhiteSpace(model.Fbclid)      ? null : model.Fbclid.Trim(),
                    SessionId     = string.IsNullOrWhiteSpace(model.SessionId)   ? null : model.SessionId.Trim(),
                    VisitorId     = string.IsNullOrWhiteSpace(model.VisitorId)   ? null : model.VisitorId.Trim(),
                    MarketingEmailConsent = model.AcknowledgedDisclaimer,
                    CallTextConsent = model.AcknowledgedDisclaimer && !string.IsNullOrWhiteSpace(model.BusinessPhone),
                    TermsAccepted = true,
                    Host          = Request?.Host.ToString(),
                    Environment   = EnvironmentLabelResolver.Resolve(),
                    CreatedUtc    = now,
                    Status        = "New",
                    AgentTrackingProfileId = agentProfileId,
                    AgentSlug     = agentSlug,
                    MetadataJson  = JsonSerializer.Serialize(new
                    {
                        BusinessName  = model.BusinessName,
                        State         = model.State,
                        Fbclid        = model.Fbclid,
                        UtmTerm       = model.UtmTerm,
                        UtmContent    = model.UtmContent,
                        ReferrerUrl   = model.ReferrerUrl,
                        LandingPageUrl = model.LandingPageUrl,
                        CorrelationId = correlationId,
                    })
                };
                _db.WebsiteLeads.Add(lead);
                await _db.SaveChangesAsync();
                _logger.LogInformation(
                    "CommercialQuote [{CorrelationId}]: WebsiteLead {LeadId} saved",
                    correlationId, lead.LeadId);
            }
            catch (Exception persistEx)
            {
                _logger.LogError(persistEx,
                    "CommercialQuote [{CorrelationId}]: lead persistence failed for {Email}",
                    correlationId, model.BusinessEmail);
                ModelState.AddModelError("", $"Failed to save lead: {persistEx.Message}");
                ViewData["StartStep"] = model.CurrentStep <= 0 ? 1 : model.CurrentStep;
                return View("~/Views/Quote/Commercial.cshtml", model);
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
                    state.BrowserPixelStatus = "pending";
                    state.BrowserPixelUpdatedUtc = DateTime.UtcNow;
                    state.BrowserPixelNote = null;
                    state.ServerCapiStatus = "pending";
                    state.ServerCapiUpdatedUtc = DateTime.UtcNow;
                    state.ServerCapiNote = null;
                });

            var metaCapiResult = await _metaConversionsApi.SendLeadAsync(
                new MetaLeadConversionRequest
                {
                    LeadId = lead.LeadId,
                    CorrelationId = correlationId,
                    EventId = metaLeadEventId,
                    QuoteType = "Commercial",
                    PageKey = "quote_commercial",
                    OfferKey = "commercial",
                    EventSourceUrl = MetaLeadTrackingWorkflow.ResolveEventSourceUrl(model.LandingPageUrl, Request),
                    ClientIpAddress = MetaLeadTrackingWorkflow.ResolveClientIpAddress(Request),
                    ClientUserAgent = Request?.Headers["User-Agent"].ToString(),
                    Fbp = MetaLeadTrackingWorkflow.ResolveCookieValue(Request, "_fbp"),
                    Fbc = MetaLeadTrackingWorkflow.ResolveCookieValue(Request, "_fbc"),
                    Fbclid = lead.Fbclid,
                    Email = lead.Email,
                    Phone = lead.Phone,
                    AllowHashedContactData = lead.TermsAccepted && lead.MarketingEmailConsent,
                    EventUtc = lead.CreatedUtc
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
                    state.ServerCapiStatus = metaCapiResult.Status;
                    state.ServerCapiUpdatedUtc = DateTime.UtcNow;
                    state.ServerCapiNote = metaCapiResult.Note;
                });

            // ── 2. Send email ─────────────────────────────────────────────────────
            try
            {
                var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                var graphClient = new GraphServiceClient(credential);

                var subjectName = $"{model.InsuredFirstName} {model.InsuredLastName}".Trim();
                if (string.IsNullOrWhiteSpace(subjectName)) subjectName = "Unknown";

                var interested = (model.InterestedIn != null && model.InterestedIn.Count > 0)
                    ? string.Join(", ", model.InterestedIn)
                    : null;

                var rows = new LeadEmailTemplate.RowBuilder()
                    .Section("Account Details")
                    .Row("Risk State", model.State)
                    .Section("Business Operations")
                    .Row("Business Name",       model.BusinessName)
                    .Row("Business Description",model.BusinessDescription)
                    .Row("Years in Business",   model.YearsInBusiness)
                    .Row("Years of Experience", model.YearsOfExperience)
                    .Row("Gross Sales",         LeadEmailTemplate.Money(model.GrossSales))
                    .Row("Total Payroll",       LeadEmailTemplate.Money(model.TotalPayroll))
                    .Row("# of Employees",      model.NumberOfEmployees)
                    .Section("Insured & Business Contact")
                    .Row("Insured Name",        $"{model.InsuredFirstName} {model.InsuredLastName}".Trim())
                    .Row("Business Phone",      model.BusinessPhone)
                    .Row("Business Email",      model.BusinessEmail)
                    .Row("Website / Facebook",  model.BusinessWebsiteOrFacebook)
                    .Section("Physical Address")
                    .Row("Street Address",      model.StreetAddress)
                    .Row("Address Line 2",      model.AddressLine2)
                    .Row("City",                model.City)
                    .Row("State",               model.State)
                    .Row("ZIP Code",            model.ZipCode)
                    .Section("Coverage & Timing")
                    .Row("Effective Date",      LeadEmailTemplate.Date(model.EffectiveDate))
                    .Row("Interested In",       interested)
                    .Row("Additional Comments", model.Comments)
                    .Section("Contact Preferences")
                    .Row("Preferred Contact Method", model.PreferredContactMethod)
                    .Row("Best Time To Contact",     model.BestTimeToContact)
                    .Section("Entity & General Info")
                    .Row("Entity Type",                     model.EntityType)
                    .Row("Federal Tax ID",                  model.FederalTaxId)
                    .Row("Active Property Liability Policy", LeadEmailTemplate.Bool(model.HasActivePropertyLiabilityPolicy ?? false))
                    .Row("Prior Coverage End Date",         LeadEmailTemplate.Date(model.PriorCoverageEndDate))
                    .Row("Officers / Members / Partners",   model.OfficersMembersPartners)
                    .Row("Current Renewal Date",            LeadEmailTemplate.Date(model.CurrentRenewalDate))
                    .Row("Owns Other Businesses",           model.OwnsOtherBusinesses.HasValue ? LeadEmailTemplate.Bool(model.OwnsOtherBusinesses.Value) : null)
                    .Row("Other Business Types",            model.OtherBusinessTypes)
                    .Row("Has High Public Profile",         model.HasHighPublicProfile.HasValue ? LeadEmailTemplate.Bool(model.HasHighPublicProfile.Value) : null)
                    .Row("Is Social Media Influencer",      model.IsSocialMediaInfluencer.HasValue ? LeadEmailTemplate.Bool(model.IsSocialMediaInfluencer.Value) : null)
                    .Section("Liability, Payroll & Auto")
                    .Row("Liability Occurrence Limit",      LeadEmailTemplate.Money(model.LiabilityOccurrenceLimit))
                    .Row("Medical Expense Limit",           LeadEmailTemplate.Money(model.MedicalExpenseLimit))
                    .Row("Property Damage Deductible",      LeadEmailTemplate.Money(model.PropertyDamageDeductible))
                    .Row("Property Damage Deductible Type", model.PropertyDamageDeductibleType)
                    .Row("Bodily Injury Deductible",        LeadEmailTemplate.Money(model.BodilyInjuryDeductible))
                    .Row("Full Time Employees",             model.FullTimeEmployees?.ToString())
                    .Row("Part Time Employees",             model.PartTimeEmployees?.ToString())
                    .Row("Hired / Non-Owned Auto",          model.HiredNonOwnedAutoRequested.HasValue ? LeadEmailTemplate.Bool(model.HiredNonOwnedAutoRequested.Value) : null)
                    .Row("Delivery Percentage",             model.DeliveryPercentage.HasValue ? $"{model.DeliveryPercentage.Value:N0}%" : null)
                    .Row("Driver Monitoring Program",       model.HasDriverMonitoringProgram.HasValue ? LeadEmailTemplate.Bool(model.HasDriverMonitoringProgram.Value) : null)
                    .Row("Drivers 3+ Years Experience",     model.DriversHaveThreeYearsExperience.HasValue ? LeadEmailTemplate.Bool(model.DriversHaveThreeYearsExperience.Value) : null)
                    .Section("Optional & Professional Coverages")
                    .Row("Damage to Premises Limit",        LeadEmailTemplate.Money(model.DamageToPremisesLimit))
                    .Row("Data Compromise Requested",       model.DataCompromiseRequested.HasValue ? LeadEmailTemplate.Bool(model.DataCompromiseRequested.Value) : null)
                    .Row("Data Compromise Limit",           LeadEmailTemplate.Money(model.DataCompromiseLimit))
                    .Row("Data Breach Last 12 Months",      model.HadDataBreachLast12Months.HasValue ? LeadEmailTemplate.Bool(model.HadDataBreachLast12Months.Value) : null)
                    .Row("Electronic Data Limit",           LeadEmailTemplate.Money(model.ElectronicDataLimit))
                    .Row("Employee Dishonesty Limit",       LeadEmailTemplate.Money(model.EmployeeDishonestyLimit))
                    .Row("Forgery / Alteration Limit",      LeadEmailTemplate.Money(model.ForgeryAlterationLimit))
                    .Row("Computer Interruption Limit",     LeadEmailTemplate.Money(model.ComputerInterruptionLimit))
                    .Row("Off-Premises Property Limit",     LeadEmailTemplate.Money(model.OffPremisesPersonalPropertyLimit))
                    .Row("Terrorism Coverage",              model.TerrorismCoverageRequested.HasValue ? LeadEmailTemplate.Bool(model.TerrorismCoverageRequested.Value) : null)
                    .Row("Misc Prof. Liability Requested",  model.MiscProfessionalLiabilityRequested.HasValue ? LeadEmailTemplate.Bool(model.MiscProfessionalLiabilityRequested.Value) : null)
                    .Row("Misc Prof. Liability Limit",      LeadEmailTemplate.Money(model.MiscProfessionalLiabilityLimit))
                    .Row("Misc Prof. Retro Date",           LeadEmailTemplate.Date(model.MiscProfessionalRetroDate))
                    .Row("Misc Prof. Claims Last 5 Years",  model.MiscProfessionalClaimsLast5Years.HasValue ? LeadEmailTemplate.Bool(model.MiscProfessionalClaimsLast5Years.Value) : null)
                    .Row("Cyber Suite Requested",           model.CyberSuiteRequested.HasValue ? LeadEmailTemplate.Bool(model.CyberSuiteRequested.Value) : null)
                    .Row("Cyber Suite Limit",               LeadEmailTemplate.Money(model.CyberSuiteLimit))
                    .Section("HR, Legal & EPLI")
                    .Row("Background Checks Performed",     model.BackgroundChecksPerformed.HasValue ? LeadEmailTemplate.Bool(model.BackgroundChecksPerformed.Value) : null)
                    .Row("Document Retention Policy",       model.DocumentRetentionPolicy.HasValue ? LeadEmailTemplate.Bool(model.DocumentRetentionPolicy.Value) : null)
                    .Row("Cyber Security Measures",         model.CyberSecurityMeasuresInPlace.HasValue ? LeadEmailTemplate.Bool(model.CyberSecurityMeasuresInPlace.Value) : null)
                    .Row("Records Stored Securely",         model.RecordsStoredSecurely.HasValue ? LeadEmailTemplate.Bool(model.RecordsStoredSecurely.Value) : null)
                    .Row("Blanket Additional Insured",      model.BlanketAdditionalInsuredRequested.HasValue ? LeadEmailTemplate.Bool(model.BlanketAdditionalInsuredRequested.Value) : null)
                    .Row("Waiver of Subrogation",           model.WaiverOfSubrogationRequested.HasValue ? LeadEmailTemplate.Bool(model.WaiverOfSubrogationRequested.Value) : null)
                    .Row("Employee Benefits Liability",     model.EmployeeBenefitsLiabilityRequested.HasValue ? LeadEmailTemplate.Bool(model.EmployeeBenefitsLiabilityRequested.Value) : null)
                    .Row("Employee Benefits Limit",         LeadEmailTemplate.Money(model.EmployeeBenefitsLimit))
                    .Row("Employee Benefits Retro Date",    LeadEmailTemplate.Date(model.EmployeeBenefitsRetroDate))
                    .Row("EPLI Requested",                  model.EPLIRequested.HasValue ? LeadEmailTemplate.Bool(model.EPLIRequested.Value) : null)
                    .Row("EPLI Limit",                      LeadEmailTemplate.Money(model.EPLILimit))
                    .Row("EPLI Deductible",                 LeadEmailTemplate.Money(model.EPLIDeductible))
                    .Row("EPLI Retro Date",                 LeadEmailTemplate.Date(model.EPLIRetroDate))
                    .Section("Loss History")
                    .Row("Policy Cancelled Last 3 Years",   model.PolicyCancelledLast3Years.HasValue ? LeadEmailTemplate.Bool(model.PolicyCancelledLast3Years.Value) : null)
                    .Row("Losses Last 4 Years",             model.LossesLast4Years.HasValue ? LeadEmailTemplate.Bool(model.LossesLast4Years.Value) : null)
                    .Row("Loss History Details",            model.LossHistoryDetails)
                    .Row("Past Fraud Convictions",          model.PastFraudConvictions.HasValue ? LeadEmailTemplate.Bool(model.PastFraudConvictions.Value) : null)
                    .Row("Past Financial Issues",           model.PastFinancialIssues.HasValue ? LeadEmailTemplate.Bool(model.PastFinancialIssues.Value) : null)
                    .Row("Past Abuse Claims",               model.PastAbuseClaims.HasValue ? LeadEmailTemplate.Bool(model.PastAbuseClaims.Value) : null)
                    .Section("Building Information")
                    .Row("Near Fire Station",               model.BuildingNearFireStation.HasValue ? LeadEmailTemplate.Bool(model.BuildingNearFireStation.Value) : null)
                    .Row("Near Fire Hydrant",               model.BuildingNearFireHydrant.HasValue ? LeadEmailTemplate.Bool(model.BuildingNearFireHydrant.Value) : null)
                    .Row("Years at Location",               model.YearsInBusinessAtLocation?.ToString())
                    .Row("Occupancy",                       model.Occupancy)
                    .Row("Building Type",                   model.BuildingType)
                    .Row("Sole Occupant",                   model.SoleOccupant.HasValue ? LeadEmailTemplate.Bool(model.SoleOccupant.Value) : null)
                    .Row("Building Industry",               model.BuildingIndustry)
                    .Row("Restaurant Occupied Part",        model.RestaurantOccupiedPart.HasValue ? LeadEmailTemplate.Bool(model.RestaurantOccupiedPart.Value) : null)
                    .Row("Construction Type",               model.ConstructionType)
                    .Row("Year Built",                      model.YearBuilt?.ToString())
                    .Row("Total Building SF",               model.TotalBuildingSF?.ToString())
                    .Row("Occupied SF",                     model.OccupiedSF?.ToString())
                    .Row("Automatic Sprinkler System",      model.AutomaticSprinklerSystem.HasValue ? LeadEmailTemplate.Bool(model.AutomaticSprinklerSystem.Value) : null)
                    .Row("Burglar Alarm",                   model.BurglarAlarm)
                    .Row("Fire Alarm",                      model.FireAlarm)
                    .Section("Class Specific Questions")
                    .Row("Building Coverage Needed",        model.BuildingCoverageNeeded.HasValue ? LeadEmailTemplate.Bool(model.BuildingCoverageNeeded.Value) : null)
                    .Row("Building Occupancy Percent",      model.BuildingOccupancyPercent?.ToString())
                    .Row("Structural Renovations",          model.StructuralRenovations.HasValue ? LeadEmailTemplate.Bool(model.StructuralRenovations.Value) : null)
                    .Section("Building & Personal Property Coverages")
                    .Row("Building Coverage Limit",         LeadEmailTemplate.Money(model.BuildingCoverageLimit))
                    .Row("Valuation Type",                  model.ValuationType)
                    .Row("Inflation Guard Percent",         model.InflationGuardPercent?.ToString())
                    .Section("Authorization")
                    .Row("Disclaimer Acknowledged",         LeadEmailTemplate.Bool(model.AcknowledgedDisclaimer));

                var emailBody = LeadEmailTemplate.Wrap("New Quote — Commercial Insurance", rows.ToString());

                var message = new Message
                {
                    Subject = $"[COMMERCIAL] Quote Request | {LeadEmailTemplate.E(subjectName)} | {LeadEmailTemplate.E(model.BusinessName)}",
                    Body = new ItemBody { ContentType = BodyType.Html, Content = emailBody },
                    ToRecipients = new List<Recipient>
                    {
                        new Recipient { EmailAddress = new EmailAddress { Address = leadRecipientEmail } }
                    }
                };

                await graphClient.Users[senderEmail].SendMail.PostAsync(
                    new SendMailPostRequestBody { Message = message, SaveToSentItems = true }
                );
                _logger.LogInformation(
                    "CommercialQuote [{CorrelationId}]: email sent to {Recipient} for lead {LeadId}",
                    correlationId, leadRecipientEmail, lead.LeadId);
            }
            catch (Exception emailEx)
            {
                _logger.LogError(emailEx,
                    "CommercialQuote [{CorrelationId}]: email send failed for lead {LeadId} — lead is saved, continuing",
                    correlationId, lead.LeadId);
            }

            // ── 3. Write analytics event ─────────────────────────────────────────
            try
            {
                var evt = new AnalyticsEvent
                {
                    EventId    = Guid.NewGuid(),
                    EventType  = "website_lead_submitted",
                    PageKey    = "quote_commercial",
                    FormKey    = "quote_commercial_form",
                    QuoteType  = "commercial_insurance",
                    SessionId  = lead.SessionId,
                    VisitorId  = lead.VisitorId,
                    UtmSource  = lead.UtmSource,
                    UtmMedium  = lead.UtmMedium,
                    UtmCampaign= lead.UtmCampaign,
                    Fbclid     = lead.Fbclid,
                    AgentTrackingProfileId = lead.AgentTrackingProfileId,
                    AgentSlug  = lead.AgentSlug,
                    Environment= lead.Environment,
                    Host       = lead.Host,
                    EventUtc   = lead.CreatedUtc,
                    ReceivedUtc= DateTime.UtcNow,
                    MetadataJson = JsonSerializer.Serialize(new { LeadId = lead.LeadId, CorrelationId = correlationId })
                };
                _db.AnalyticsEvents.Add(evt);
                await _db.SaveChangesAsync();
                _logger.LogInformation(
                    "CommercialQuote [{CorrelationId}]: analytics event {EventId} written for lead {LeadId}",
                    correlationId, evt.EventId, lead.LeadId);
            }
            catch (Exception analyticsEx)
            {
                _logger.LogError(analyticsEx,
                    "CommercialQuote [{CorrelationId}]: analytics event write failed for lead {LeadId} — lead is saved, continuing",
                    correlationId, lead.LeadId);
            }

            TempData["QuoteType"] = "Commercial";
            TempData["MetaLeadEventId"] = metaLeadEventId;
            TempData["MetaLeadLeadId"] = lead.LeadId.ToString("D");
            return RedirectToAction("Index", "ThankYou");
        }

        private async Task<(string RecipientEmail, Guid? AgentProfileId, string? AgentSlug)> ResolveLeadContextAsync()
        {
            if (HttpContext?.Items.TryGetValue("TrackingProfile", out var trackingProfileObj) == true &&
                trackingProfileObj is AgentTrackingProfile trackingProfile &&
                !string.IsNullOrWhiteSpace(trackingProfile.AgentUpn))
            {
                return (trackingProfile.AgentUpn.Trim(), trackingProfile.Id, trackingProfile.Slug);
            }

            string? slug = null;

            var formSlug = Request?.Form["AgentSlug"].ToString();
            if (!string.IsNullOrWhiteSpace(formSlug))
                slug = formSlug.Trim();

            if (string.IsNullOrWhiteSpace(slug))
                slug = ExtractSlugFromPath(Request?.Path.Value);

            if (string.IsNullOrWhiteSpace(slug))
                slug = ExtractSlugFromPath(Request?.Headers["Referer"].ToString());

            if (!string.IsNullOrWhiteSpace(slug))
            {
                var bySlug = await _resolver.ResolveBySlugAsync(slug, HttpContext?.RequestAborted ?? CancellationToken.None);
                if (bySlug.Found && bySlug.Profile != null && !string.IsNullOrWhiteSpace(bySlug.Profile.AgentUpn))
                    return (bySlug.Profile.AgentUpn.Trim(), bySlug.Profile.Id, bySlug.CanonicalSlug);
            }

            return (recipientEmail, null, null);
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
    }
}
