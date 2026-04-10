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
using System.Linq;
using System.Text;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using ProtectWebsite.Services;
using ProtectWebsite.Services.Tracking;

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
        private readonly ILogger<AutoQuoteController> _logger;

        public AutoQuoteController(IConfiguration configuration, AgentTrackingResolver resolver,
            MasterAppDbContext db, ILogger<AutoQuoteController> logger)
        {
            tenantId = configuration["AzureAd:TenantId"] ?? throw new ArgumentNullException("AzureAd:TenantId");
            clientId = configuration["AzureAd:ClientId"] ?? throw new ArgumentNullException("AzureAd:ClientId");
            clientSecret = configuration["AzureAd:ClientSecret"] ?? throw new ArgumentNullException("AzureAd:ClientSecret");

            senderEmail = configuration["Contact:SenderEmail"] ?? throw new ArgumentNullException("Contact:SenderEmail");
            recipientEmail = configuration["Contact:RecipientEmail"] ?? throw new ArgumentNullException("Contact:RecipientEmail");
            _resolver = resolver;
            _db = db;
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

            var (leadRecipientEmail, agentProfileId, agentSlug) = await ResolveLeadContextAsync();

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

            try
            {
                var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                var graphClient = new GraphServiceClient(credential);

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

                var primary = model.Drivers.FirstOrDefault();
                var subjectName = primary == null ? "Unknown" : $"{primary.FirstName} {primary.LastName}".Trim();

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

                var message = new Message
                {
                    Subject = $"[AUTO] Quote Request | {subjectName}",
                    Body = new ItemBody { ContentType = BodyType.Html, Content = emailBody },
                    ToRecipients = new List<Recipient>
                    {
                        new Recipient { EmailAddress = new EmailAddress { Address = leadRecipientEmail } }
                    }
                };

                await graphClient.Users[senderEmail].SendMail.PostAsync(
                    new SendMailPostRequestBody { Message = message, SaveToSentItems = true }
                );

                // ── Lead persistence (separate try/catch — email success is preserved) ──────
                try
                {
                    var primaryDriver = model.Drivers.FirstOrDefault();
                    var lead = new WebsiteLead
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
                        SessionId     = string.IsNullOrWhiteSpace(model.SessionId)   ? null : model.SessionId.Trim(),
                        VisitorId     = string.IsNullOrWhiteSpace(model.VisitorId)   ? null : model.VisitorId.Trim(),
                        MarketingEmailConsent = model.AcknowledgedDisclaimer,
                        CallTextConsent = model.AcknowledgedDisclaimer && !string.IsNullOrWhiteSpace(model.PhoneNumber),
                        TermsAccepted = true,
                        Host          = Request?.Host.ToString(),
                        Environment   = "production",
                        CreatedUtc    = DateTime.UtcNow,
                        Status        = "New",
                        AgentTrackingProfileId = agentProfileId,
                        AgentSlug     = agentSlug,
                        MetadataJson  = JsonSerializer.Serialize(new
                        {
                            AddressState   = model.AddressState,
                            DriverCount    = model.Drivers.Count,
                            VehicleCount   = model.Vehicles.Count,
                            PriorCarrier   = model.PriorCarrier,
                            Fbclid         = model.Fbclid,
                            UtmTerm        = model.UtmTerm,
                            UtmContent     = model.UtmContent,
                            ReferrerUrl    = model.ReferrerUrl,
                            LandingPageUrl = model.LandingPageUrl,
                        })
                    };
                    _db.WebsiteLeads.Add(lead);
                    await _db.SaveChangesAsync();
                    _logger.LogInformation("AutoQuote: lead {LeadId} persisted for {Email}", lead.LeadId, lead.Email);
                }
                catch (Exception persistEx)
                {
                    _logger.LogError(persistEx, "AutoQuote: lead persistence failed for {Email}", model.EmailAddress);
                }

                TempData["QuoteType"] = "Auto";
                return RedirectToAction("Index", "ThankYou");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", $"Failed to send lead: {ex.Message}");
                EnsureIndexZero(model);
                ViewData["StartStep"] = 5;
                return View("~/Views/Quote/Auto.cshtml", model);
            }
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
