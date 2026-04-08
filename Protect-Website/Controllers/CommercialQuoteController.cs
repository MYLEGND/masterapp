using Microsoft.AspNetCore.Mvc;
using Domain.Entities;
using Protect_Website.Models;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.ModelBinding;
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

                static string E(string? s) => string.IsNullOrWhiteSpace(s) ? "N/A" : WebUtility.HtmlEncode(s.Trim());
                static string D(DateTime? d) => d.HasValue ? WebUtility.HtmlEncode(d.Value.ToString("MM/dd/yyyy")) : "N/A";
                static string Money(decimal? v) => v.HasValue ? WebUtility.HtmlEncode(v.Value.ToString("N0")) : "N/A";
                static string Percent(decimal? v) => v.HasValue ? WebUtility.HtmlEncode($"{v.Value:N0}%") : "N/A";
                static string Bool(bool? b) => b.HasValue ? (b.Value ? "Yes" : "No") : "N/A";

                string BuildAccountDetails()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Step 1 — Account Details</h3>");
                    sb.AppendLine($"<p><strong>Risk State:</strong> {E(model.State)}</p>");
                    sb.AppendLine("<hr/>");
                    return sb.ToString();
                }

                string BuildBusinessOperations()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Step 2 — Business Operations</h3>");
                    sb.AppendLine($"<p><strong>Business Description:</strong> {E(model.BusinessDescription)}</p>");
                    sb.AppendLine($"<p><strong>Business Name:</strong> {E(model.BusinessName)}</p>");
                    sb.AppendLine($"<p><strong>Years in Business:</strong> {E(model.YearsInBusiness)}</p>");
                    sb.AppendLine($"<p><strong>Years of Experience:</strong> {E(model.YearsOfExperience)}</p>");
                    sb.AppendLine($"<p><strong>Gross Sales:</strong> {Money(model.GrossSales)}</p>");
                    sb.AppendLine($"<p><strong>Total Payroll:</strong> {Money(model.TotalPayroll)}</p>");
                    sb.AppendLine($"<p><strong># of Employees:</strong> {E(model.NumberOfEmployees)}</p>");
                    sb.AppendLine("<hr/>");
                    return sb.ToString();
                }

                string BuildContact()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Step 3 — Insured &amp; Business Contact</h3>");
                    sb.AppendLine($"<p><strong>Insured Name:</strong> {E(model.InsuredFirstName)} {E(model.InsuredLastName)}</p>");
                    sb.AppendLine($"<p><strong>Business Phone:</strong> {E(model.BusinessPhone)}</p>");
                    sb.AppendLine($"<p><strong>Business Email:</strong> {E(model.BusinessEmail)}</p>");
                    sb.AppendLine($"<p><strong>Website / Facebook:</strong> {E(model.BusinessWebsiteOrFacebook)}</p>");
                    sb.AppendLine("<hr/>");
                    return sb.ToString();
                }

                string BuildAddress()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Step 4 — Physical Address</h3>");
                    sb.AppendLine($"<p><strong>Street Address:</strong> {E(model.StreetAddress)}</p>");
                    sb.AppendLine($"<p><strong>Address Line 2:</strong> {E(model.AddressLine2)}</p>");
                    sb.AppendLine($"<p><strong>City:</strong> {E(model.City)}</p>");
                    sb.AppendLine($"<p><strong>State:</strong> {E(model.State)}</p>");
                    sb.AppendLine($"<p><strong>ZIP Code:</strong> {E(model.ZipCode)}</p>");
                    sb.AppendLine("<hr/>");
                    return sb.ToString();
                }

                string BuildCoverageTiming()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Step 5 — Coverage &amp; Timing</h3>");
                    sb.AppendLine($"<p><strong>Effective Date:</strong> {D(model.EffectiveDate)}</p>");

                    var interested = (model.InterestedIn != null && model.InterestedIn.Count > 0)
                        ? string.Join(", ", model.InterestedIn)
                        : "N/A";

                    sb.AppendLine($"<p><strong>Interested In:</strong> {WebUtility.HtmlEncode(interested)}</p>");
                    sb.AppendLine($"<p><strong>Additional Comments:</strong> {E(model.Comments)}</p>");
                    sb.AppendLine("<hr/>");
                    return sb.ToString();
                }

                string BuildContactPreferences()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Step 6 — Contact Preferences</h3>");
                    sb.AppendLine($"<p><strong>Preferred Contact Method:</strong> {E(model.PreferredContactMethod)}</p>");
                    sb.AppendLine($"<p><strong>Best Time To Contact:</strong> {E(model.BestTimeToContact)}</p>");
                    sb.AppendLine("<hr/>");
                    return sb.ToString();
                }

                string BuildEntityGeneral()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Step 7 — Entity &amp; General Info</h3>");
                    sb.AppendLine($"<p><strong>Entity Type:</strong> {E(model.EntityType)}</p>");
                    sb.AppendLine($"<p><strong>Federal Tax ID:</strong> {E(model.FederalTaxId)}</p>");
                    sb.AppendLine($"<p><strong>Has Active Property Liability Policy:</strong> {Bool(model.HasActivePropertyLiabilityPolicy)}</p>");
                    sb.AppendLine($"<p><strong>Prior Coverage End Date:</strong> {D(model.PriorCoverageEndDate)}</p>");
                    sb.AppendLine($"<p><strong>Officers / Members / Partners:</strong> {E(model.OfficersMembersPartners)}</p>");
                    sb.AppendLine($"<p><strong>Current Renewal Date:</strong> {D(model.CurrentRenewalDate)}</p>");
                    sb.AppendLine($"<p><strong>Owns Other Businesses:</strong> {Bool(model.OwnsOtherBusinesses)}</p>");
                    sb.AppendLine($"<p><strong>Other Business Types:</strong> {E(model.OtherBusinessTypes)}</p>");
                    sb.AppendLine($"<p><strong>Has High Public Profile:</strong> {Bool(model.HasHighPublicProfile)}</p>");
                    sb.AppendLine($"<p><strong>Is Social Media Influencer:</strong> {Bool(model.IsSocialMediaInfluencer)}</p>");
                    sb.AppendLine("<hr/>");
                    return sb.ToString();
                }

                string BuildLiabilityPayrollAuto()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Step 8 — Liability, Payroll &amp; Auto</h3>");
                    sb.AppendLine($"<p><strong>Liability Occurrence Limit:</strong> {Money(model.LiabilityOccurrenceLimit)}</p>");
                    sb.AppendLine($"<p><strong>Medical Expense Limit:</strong> {Money(model.MedicalExpenseLimit)}</p>");
                    sb.AppendLine($"<p><strong>Property Damage Deductible:</strong> {Money(model.PropertyDamageDeductible)}</p>");
                    sb.AppendLine($"<p><strong>Property Damage Deductible Type:</strong> {E(model.PropertyDamageDeductibleType)}</p>");
                    sb.AppendLine($"<p><strong>Bodily Injury Deductible:</strong> {Money(model.BodilyInjuryDeductible)}</p>");
                    sb.AppendLine($"<p><strong>Full Time Employees:</strong> {E(model.FullTimeEmployees?.ToString())}</p>");
                    sb.AppendLine($"<p><strong>Part Time Employees:</strong> {E(model.PartTimeEmployees?.ToString())}</p>");
                    sb.AppendLine($"<p><strong>Hired / Non-Owned Auto Requested:</strong> {Bool(model.HiredNonOwnedAutoRequested)}</p>");
                    sb.AppendLine($"<p><strong>Delivery Percentage:</strong> {Percent(model.DeliveryPercentage)}</p>");
                    sb.AppendLine($"<p><strong>Has Driver Monitoring Program:</strong> {Bool(model.HasDriverMonitoringProgram)}</p>");
                    sb.AppendLine($"<p><strong>Drivers Have 3+ Years Experience:</strong> {Bool(model.DriversHaveThreeYearsExperience)}</p>");
                    sb.AppendLine("<hr/>");
                    return sb.ToString();
                }

                string BuildOptionalProfessional()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Step 9 — Optional &amp; Professional Coverages</h3>");
                    sb.AppendLine($"<p><strong>Damage to Premises Limit:</strong> {Money(model.DamageToPremisesLimit)}</p>");
                    sb.AppendLine($"<p><strong>Data Compromise Requested:</strong> {Bool(model.DataCompromiseRequested)}</p>");
                    sb.AppendLine($"<p><strong>Data Compromise Limit:</strong> {Money(model.DataCompromiseLimit)}</p>");
                    sb.AppendLine($"<p><strong>Had Data Breach Last 12 Months:</strong> {Bool(model.HadDataBreachLast12Months)}</p>");
                    sb.AppendLine($"<p><strong>Electronic Data Limit:</strong> {Money(model.ElectronicDataLimit)}</p>");
                    sb.AppendLine($"<p><strong>Employee Dishonesty Limit:</strong> {Money(model.EmployeeDishonestyLimit)}</p>");
                    sb.AppendLine($"<p><strong>Forgery / Alteration Limit:</strong> {Money(model.ForgeryAlterationLimit)}</p>");
                    sb.AppendLine($"<p><strong>Computer Interruption Limit:</strong> {Money(model.ComputerInterruptionLimit)}</p>");
                    sb.AppendLine($"<p><strong>Off Premises Personal Property Limit:</strong> {Money(model.OffPremisesPersonalPropertyLimit)}</p>");
                    sb.AppendLine($"<p><strong>Terrorism Coverage Requested:</strong> {Bool(model.TerrorismCoverageRequested)}</p>");
                    sb.AppendLine($"<p><strong>Misc Professional Liability Requested:</strong> {Bool(model.MiscProfessionalLiabilityRequested)}</p>");
                    sb.AppendLine($"<p><strong>Misc Professional Liability Limit:</strong> {Money(model.MiscProfessionalLiabilityLimit)}</p>");
                    sb.AppendLine($"<p><strong>Misc Professional Retro Date:</strong> {D(model.MiscProfessionalRetroDate)}</p>");
                    sb.AppendLine($"<p><strong>Misc Professional Claims Last 5 Years:</strong> {Bool(model.MiscProfessionalClaimsLast5Years)}</p>");
                    sb.AppendLine($"<p><strong>Cyber Suite Requested:</strong> {Bool(model.CyberSuiteRequested)}</p>");
                    sb.AppendLine($"<p><strong>Cyber Suite Limit:</strong> {Money(model.CyberSuiteLimit)}</p>");
                    sb.AppendLine("<hr/>");
                    return sb.ToString();
                }

                string BuildHrEpli()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Step 10 — HR, Legal &amp; EPLI</h3>");
                    sb.AppendLine($"<p><strong>Background Checks Performed:</strong> {Bool(model.BackgroundChecksPerformed)}</p>");
                    sb.AppendLine($"<p><strong>Document Retention Policy:</strong> {Bool(model.DocumentRetentionPolicy)}</p>");
                    sb.AppendLine($"<p><strong>Cyber Security Measures In Place:</strong> {Bool(model.CyberSecurityMeasuresInPlace)}</p>");
                    sb.AppendLine($"<p><strong>Records Stored Securely:</strong> {Bool(model.RecordsStoredSecurely)}</p>");
                    sb.AppendLine($"<p><strong>Blanket Additional Insured Requested:</strong> {Bool(model.BlanketAdditionalInsuredRequested)}</p>");
                    sb.AppendLine($"<p><strong>Waiver of Subrogation Requested:</strong> {Bool(model.WaiverOfSubrogationRequested)}</p>");
                    sb.AppendLine($"<p><strong>Employee Benefits Liability Requested:</strong> {Bool(model.EmployeeBenefitsLiabilityRequested)}</p>");
                    sb.AppendLine($"<p><strong>Employee Benefits Limit:</strong> {Money(model.EmployeeBenefitsLimit)}</p>");
                    sb.AppendLine($"<p><strong>Employee Benefits Retro Date:</strong> {D(model.EmployeeBenefitsRetroDate)}</p>");
                    sb.AppendLine($"<p><strong>EPLI Requested:</strong> {Bool(model.EPLIRequested)}</p>");
                    sb.AppendLine($"<p><strong>EPLI Limit:</strong> {Money(model.EPLILimit)}</p>");
                    sb.AppendLine($"<p><strong>EPLI Deductible:</strong> {Money(model.EPLIDeductible)}</p>");
                    sb.AppendLine($"<p><strong>EPLI Retro Date:</strong> {D(model.EPLIRetroDate)}</p>");
                    sb.AppendLine("<hr/>");
                    return sb.ToString();
                }

                string BuildLossHistory()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Step 11 — Loss History</h3>");
                    sb.AppendLine($"<p><strong>Policy Cancelled Last 3 Years:</strong> {Bool(model.PolicyCancelledLast3Years)}</p>");
                    sb.AppendLine($"<p><strong>Losses Last 4 Years:</strong> {Bool(model.LossesLast4Years)}</p>");
                    sb.AppendLine($"<p><strong>Loss History Details:</strong> {E(model.LossHistoryDetails)}</p>");
                    sb.AppendLine($"<p><strong>Past Fraud Convictions:</strong> {Bool(model.PastFraudConvictions)}</p>");
                    sb.AppendLine($"<p><strong>Past Financial Issues:</strong> {Bool(model.PastFinancialIssues)}</p>");
                    sb.AppendLine($"<p><strong>Past Abuse Claims:</strong> {Bool(model.PastAbuseClaims)}</p>");
                    sb.AppendLine("<hr/>");
                    return sb.ToString();
                }

                string BuildBuildingInfo()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Step 12 — Building Information</h3>");
                    sb.AppendLine($"<p><strong>Building Near Fire Station:</strong> {Bool(model.BuildingNearFireStation)}</p>");
                    sb.AppendLine($"<p><strong>Building Near Fire Hydrant:</strong> {Bool(model.BuildingNearFireHydrant)}</p>");
                    sb.AppendLine($"<p><strong>Years in Business at Location:</strong> {E(model.YearsInBusinessAtLocation?.ToString())}</p>");
                    sb.AppendLine($"<p><strong>Occupancy:</strong> {E(model.Occupancy)}</p>");
                    sb.AppendLine($"<p><strong>Building Type:</strong> {E(model.BuildingType)}</p>");
                    sb.AppendLine($"<p><strong>Sole Occupant:</strong> {Bool(model.SoleOccupant)}</p>");
                    sb.AppendLine($"<p><strong>Building Industry:</strong> {E(model.BuildingIndustry)}</p>");
                    sb.AppendLine($"<p><strong>Restaurant Occupied Part:</strong> {Bool(model.RestaurantOccupiedPart)}</p>");
                    sb.AppendLine($"<p><strong>Construction Type:</strong> {E(model.ConstructionType)}</p>");
                    sb.AppendLine($"<p><strong>Year Built:</strong> {E(model.YearBuilt?.ToString())}</p>");
                    sb.AppendLine($"<p><strong>Total Building SF:</strong> {E(model.TotalBuildingSF?.ToString())}</p>");
                    sb.AppendLine($"<p><strong>Occupied SF:</strong> {E(model.OccupiedSF?.ToString())}</p>");
                    sb.AppendLine($"<p><strong>Automatic Sprinkler System:</strong> {Bool(model.AutomaticSprinklerSystem)}</p>");
                    sb.AppendLine($"<p><strong>Burglar Alarm:</strong> {E(model.BurglarAlarm)}</p>");
                    sb.AppendLine($"<p><strong>Fire Alarm:</strong> {E(model.FireAlarm)}</p>");
                    sb.AppendLine("<hr/>");
                    return sb.ToString();
                }

                string BuildClassSpecific()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Step 13 — Class Specific Questions</h3>");
                    sb.AppendLine($"<p><strong>Building Coverage Needed:</strong> {Bool(model.BuildingCoverageNeeded)}</p>");
                    sb.AppendLine($"<p><strong>Building Occupancy Percent:</strong> {E(model.BuildingOccupancyPercent?.ToString())}</p>");
                    sb.AppendLine($"<p><strong>Structural Renovations:</strong> {Bool(model.StructuralRenovations)}</p>");
                    sb.AppendLine("<hr/>");
                    return sb.ToString();
                }

                string BuildBuildingCoverages()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Step 14 — Building &amp; Personal Property Coverages</h3>");
                    sb.AppendLine($"<p><strong>Building Coverage Limit:</strong> {Money(model.BuildingCoverageLimit)}</p>");
                    sb.AppendLine($"<p><strong>Valuation Type:</strong> {E(model.ValuationType)}</p>");
                    sb.AppendLine($"<p><strong>Inflation Guard Percent:</strong> {E(model.InflationGuardPercent?.ToString())}</p>");
                    sb.AppendLine("<hr/>");
                    return sb.ToString();
                }

                var subjectName = $"{model.InsuredFirstName} {model.InsuredLastName}".Trim();
                if (string.IsNullOrWhiteSpace(subjectName)) subjectName = "Unknown";

                var emailBody = $@"
<h2>COMMERCIAL INSURANCE — New Quote Request</h2>
{BuildAccountDetails()}
{BuildBusinessOperations()}
{BuildContact()}
{BuildAddress()}
{BuildCoverageTiming()}
{BuildContactPreferences()}
{BuildEntityGeneral()}
{BuildLiabilityPayrollAuto()}
{BuildOptionalProfessional()}
{BuildHrEpli()}
{BuildLossHistory()}
{BuildBuildingInfo()}
{BuildClassSpecific()}
{BuildBuildingCoverages()}
<h3>Authorization</h3>
<p><strong>Acknowledged:</strong> {(model.AcknowledgedDisclaimer ? "Yes" : "No")}</p>
";

                // ===================== APPLY HEADING STYLING (same as Auto) =====================
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

                var message = new Message
                {
                    Subject = $"[COMMERCIAL] Quote Request | {E(subjectName)} | {E(model.BusinessName)}",
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
