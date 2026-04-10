using Microsoft.AspNetCore.Mvc;
using Domain.Entities;
using Protect_Website.Models;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.ModelBinding;
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

        public CommercialQuoteController(IConfiguration configuration, AgentTrackingResolver resolver)
        {
            tenantId = configuration["AzureAd:TenantId"] ?? throw new ArgumentNullException("AzureAd:TenantId");
            clientId = configuration["AzureAd:ClientId"] ?? throw new ArgumentNullException("AzureAd:ClientId");
            clientSecret = configuration["AzureAd:ClientSecret"] ?? throw new ArgumentNullException("AzureAd:ClientSecret");

            senderEmail = configuration["Contact:SenderEmail"] ?? throw new ArgumentNullException("Contact:SenderEmail");
            recipientEmail = configuration["Contact:RecipientEmail"] ?? throw new ArgumentNullException("Contact:RecipientEmail");
            _resolver = resolver;
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
            // Normalize consent checkbox in case wizard disabled it earlier
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

            // Keep user on same step if server-side validation fails
            if (!ModelState.IsValid)
            {
                ViewData["StartStep"] = model.CurrentStep <= 0 ? 1 : model.CurrentStep;
                return View("~/Views/Quote/Commercial.cshtml", model);
            }

            var leadRecipientEmail = await ResolveLeadRecipientEmailAsync();

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

                TempData["QuoteType"] = "Commercial";
                return RedirectToAction("Index", "ThankYou");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", $"Failed to send lead: {ex.Message}");
                ViewData["StartStep"] = model.CurrentStep <= 0 ? 1 : model.CurrentStep;
                return View("~/Views/Quote/Commercial.cshtml", model);
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
