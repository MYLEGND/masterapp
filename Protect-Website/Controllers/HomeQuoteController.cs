using Microsoft.AspNetCore.Mvc;
using Domain.Entities;
using Protect_Website.Models;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
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

        public HomeQuoteController(IConfiguration configuration, AgentTrackingResolver resolver)
        {
            tenantId = configuration["AzureAd:TenantId"]!;
            clientId = configuration["AzureAd:ClientId"]!;
            clientSecret = configuration["AzureAd:ClientSecret"]!;
            senderEmail = configuration["Contact:SenderEmail"] ?? "connect@mylegnd.com";
            recipientEmail = configuration["Contact:RecipientEmail"]!;
            websiteName = configuration["Contact:WebsiteName"] ?? "Legend Legacy Protection";
            _resolver = resolver;
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
            // Normalize consent (HTML checkbox may be disabled in earlier steps and re-enabled before submit)
            var ackRaw = Request.Form["AcknowledgedDisclaimer"].ToString();
            if (!string.IsNullOrWhiteSpace(ackRaw))
            {
                model.AcknowledgedDisclaimer = ackRaw.Equals("true", StringComparison.OrdinalIgnoreCase)
                                             || ackRaw.Equals("on", StringComparison.OrdinalIgnoreCase)
                                             || ackRaw.Equals("1");
                if (ModelState.ContainsKey(nameof(model.AcknowledgedDisclaimer)))
                {
                    ModelState[nameof(model.AcknowledgedDisclaimer)].Errors.Clear();
                    ModelState[nameof(model.AcknowledgedDisclaimer)].ValidationState = Microsoft.AspNetCore.Mvc.ModelBinding.ModelValidationState.Valid;
                }
            }

            if (!ModelState.IsValid)
                return View("~/Views/Quote/Home.cshtml", model);

            var leadRecipientEmail = await ResolveLeadRecipientEmailAsync();

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

            // Set the quote type so the Thank You page can display the correct name
            TempData["QuoteType"] = "Home"; // or model.CoverageType if dynamic

                // ✅ Redirect to centralized ThankYouController
                return RedirectToAction("Index", "ThankYou");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", $"Failed to send lead: {ex.Message}");
                return View("~/Views/Quote/Home.cshtml", model);
            }
        }

        private async Task<string> ResolveLeadRecipientEmailAsync()
        {
            if (HttpContext?.Items.TryGetValue("TrackingProfile", out var trackingProfileObj) == true &&
                trackingProfileObj is AgentTrackingProfile trackingProfile &&
                !string.IsNullOrWhiteSpace(trackingProfile.AgentUpn))
            {
                return trackingProfile.AgentUpn.Trim();
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
                {
                    return bySlug.Profile.AgentUpn.Trim();
                }
            }

            return recipientEmail;
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
