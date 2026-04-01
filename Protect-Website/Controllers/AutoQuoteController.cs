using Microsoft.AspNetCore.Mvc;
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
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;

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

        public AutoQuoteController(IConfiguration configuration)
        {
            tenantId = configuration["AzureAd:TenantId"] ?? throw new ArgumentNullException("AzureAd:TenantId");
            clientId = configuration["AzureAd:ClientId"] ?? throw new ArgumentNullException("AzureAd:ClientId");
            clientSecret = configuration["AzureAd:ClientSecret"] ?? throw new ArgumentNullException("AzureAd:ClientSecret");

            senderEmail = configuration["Contact:SenderEmail"] ?? throw new ArgumentNullException("Contact:SenderEmail");
            recipientEmail = configuration["Contact:RecipientEmail"] ?? throw new ArgumentNullException("Contact:RecipientEmail");
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

                static string E(string? s) => string.IsNullOrWhiteSpace(s) ? "N/A" : WebUtility.HtmlEncode(s.Trim());
                static string D(DateTime? d) => d.HasValue ? WebUtility.HtmlEncode(d.Value.ToString("MM/dd/yyyy")) : "N/A";

                string DriverNameByIndex(int? idx)
                {
                    if (!idx.HasValue) return "N/A";
                    if (idx.Value < 0 || idx.Value >= model.Drivers.Count) return "N/A";
                    var dr = model.Drivers[idx.Value];
                    return $"{dr.FirstName} {dr.LastName}".Trim();
                }

                string VehicleLabelByIndex(int? idx)
                {
                    if (!idx.HasValue) return "N/A";
                    if (idx.Value < 0 || idx.Value >= model.Vehicles.Count) return "N/A";
                    var v = model.Vehicles[idx.Value];
                    var ymm = $"{v.Year} {v.Make} {v.Model}".Trim();
                    return $"{ymm} (VIN: {v.VIN})".Trim();
                }

                string BuildApplicantInfo()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Section 1 — Applicant Info</h3>");

                    sb.AppendLine($"<p><strong>First Name:</strong> {E(model.FirstName)}</p>");
                    sb.AppendLine($"<p><strong>Last Name:</strong> {E(model.LastName)}</p>");
                    sb.AppendLine($"<p><strong>Address State:</strong> {E(model.AddressState)}</p>");
                    sb.AppendLine($"<p><strong>Postal Code:</strong> {E(model.PostalCode)}</p>");
                    sb.AppendLine($"<p><strong>Nickname:</strong> {E(model.Nickname)}</p>");
                    sb.AppendLine($"<p><strong>Gender:</strong> {E(model.Gender)}</p>");
                    sb.AppendLine($"<p><strong>DOB:</strong> {D(model.DOB)}</p>");
                    sb.AppendLine($"<p><strong>Marital Status:</strong> {E(model.MaritalStatus)}</p>");
                    sb.AppendLine($"<p><strong>Driver's License #:</strong> {E(model.DriversLicenseNumber)}</p>");
                    sb.AppendLine($"<p><strong>DL Status:</strong> {E(model.DLStatus)}</p>");
                    sb.AppendLine($"<p><strong>DL State:</strong> {E(model.DLState)}</p>");
                    sb.AppendLine($"<p><strong>Education:</strong> {E(model.Education)}</p>");
                    sb.AppendLine($"<p><strong>Industry:</strong> {E(model.Industry)}</p>");

                    sb.AppendLine("<hr/>");
                    return sb.ToString();
                }

                // ===================== ADDRESS + CONTACT INFO =====================
                string BuildAddress()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Section 1B — Address</h3>");

                    sb.AppendLine("<h4>Primary Address</h4>");
                    sb.AppendLine($"<p><strong>Address:</strong> {E(model.PrimaryAddress)}</p>");
                    sb.AppendLine($"<p><strong>Unit:</strong> {E(model.PrimaryUnit)}</p>");
                    sb.AppendLine($"<p><strong>Address Line 2:</strong> {E(model.PrimaryAddressLine2)}</p>");
                    sb.AppendLine($"<p><strong>City:</strong> {E(model.PrimaryCity)}</p>");
                    sb.AppendLine($"<p><strong>State:</strong> {E(model.PrimaryState)}</p>");
                    sb.AppendLine($"<p><strong>Country:</strong> {E(model.PrimaryCountry)}</p>");
                    sb.AppendLine($"<p><strong>Postal Code:</strong> {E(model.PrimaryPostalCode)}</p>");
                    sb.AppendLine($"<p><strong>Years At Address:</strong> {E(model.PrimaryYearsAtAddress)}</p>");

                    bool hasPrev =
                        !string.IsNullOrWhiteSpace(model.PreviousAddress) ||
                        !string.IsNullOrWhiteSpace(model.PreviousCity) ||
                        !string.IsNullOrWhiteSpace(model.PreviousState) ||
                        !string.IsNullOrWhiteSpace(model.PreviousCountry) ||
                        !string.IsNullOrWhiteSpace(model.PreviousPostalCode) ||
                        !string.IsNullOrWhiteSpace(model.PreviousYearsAtAddress) ||
                        !string.IsNullOrWhiteSpace(model.PreviousUnit) ||
                        !string.IsNullOrWhiteSpace(model.PreviousAddressLine2);

                    if (hasPrev)
                    {
                        sb.AppendLine("<h4>Previous Address</h4>");
                        sb.AppendLine($"<p><strong>Address:</strong> {E(model.PreviousAddress)}</p>");
                        sb.AppendLine($"<p><strong>Unit:</strong> {E(model.PreviousUnit)}</p>");
                        sb.AppendLine($"<p><strong>Address Line 2:</strong> {E(model.PreviousAddressLine2)}</p>");
                        sb.AppendLine($"<p><strong>City:</strong> {E(model.PreviousCity)}</p>");
                        sb.AppendLine($"<p><strong>State:</strong> {E(model.PreviousState)}</p>");
                        sb.AppendLine($"<p><strong>Country:</strong> {E(model.PreviousCountry)}</p>");
                        sb.AppendLine($"<p><strong>Postal Code:</strong> {E(model.PreviousPostalCode)}</p>");
                        sb.AppendLine($"<p><strong>Years At Address:</strong> {E(model.PreviousYearsAtAddress)}</p>");
                    }

                    sb.AppendLine("<hr/>");
                    return sb.ToString();
                }

                string BuildContactInfo()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Section 1C — Contact Info</h3>");

                    sb.AppendLine($"<p><strong>Phone Type:</strong> {E(model.PhoneType)}</p>");
                    sb.AppendLine($"<p><strong>Phone Number:</strong> {E(model.PhoneNumber)}</p>");
                    sb.AppendLine($"<p><strong>Email Type:</strong> {E(model.EmailType)}</p>");
                    sb.AppendLine($"<p><strong>Email Address:</strong> {E(model.EmailAddress)}</p>");
                    sb.AppendLine($"<p><strong>Preferred Contact Method:</strong> {E(model.PreferredContactMethod)}</p>");
                    sb.AppendLine($"<p><strong>Best Time To Contact:</strong> {E(model.BestTimeToContact)}</p>");

                    sb.AppendLine("<hr/>");
                    return sb.ToString();
                }
                // ================================================================

                string BuildPolicy()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Section 1 — Policy Information (Auto)</h3>");
                    sb.AppendLine($"<p><strong>Prior Carrier:</strong> {E(model.PriorCarrier)}</p>");
                    sb.AppendLine($"<p><strong>Prior Policy Expiration Date:</strong> {D(model.PriorPolicyExpirationDate)}</p>");
                    sb.AppendLine($"<p><strong>Prior Liability Limits:</strong> {E(model.PriorLiabilityLimits)}</p>");
                    sb.AppendLine($"<p><strong>Prior Policy Term:</strong> {E(model.PriorPolicyTerm)}</p>");
                    sb.AppendLine($"<p><strong>Prior Policy Premium:</strong> {E(model.PriorPolicyPremium)}</p>");
                    sb.AppendLine($"<p><strong>Years with Prior Carrier:</strong> {E(model.YearsWithPriorCarrier)} &nbsp;&nbsp; <strong>Months:</strong> {E(model.MonthsWithPriorCarrier)}</p>");
                    sb.AppendLine($"<p><strong>Years Continuous Coverage:</strong> {E(model.YearsContinuousCoverage)} &nbsp;&nbsp; <strong>Months:</strong> {E(model.MonthsContinuousCoverage)}</p>");
                    sb.AppendLine($"<p><strong>Credit/Underwriting Reports Authorized:</strong> {E(model.CreditCheckAuthorized)}</p>");
                    sb.AppendLine($"<p><strong>New Policy Term:</strong> {E(model.NewPolicyTerm)}</p>");
                    sb.AppendLine($"<p><strong>Package:</strong> {E(model.PackagePolicy)}</p>");
                    sb.AppendLine($"<p><strong>Effective Date (New Policy):</strong> {D(model.NewPolicyEffectiveDate)}</p>");
                    sb.AppendLine($"<p><strong>Additional Carrier Questions:</strong> {E(model.AdditionalCarrierQuestions)}</p>");
                    sb.AppendLine($"<p><strong>Paperless:</strong> {E(model.Paperless)}</p>");
                    sb.AppendLine($"<p><strong>Multi-Policy Discount:</strong> {E(model.MultiPolicyDiscount)}</p>");
                    sb.AppendLine("<hr/>");
                    return sb.ToString();
                }

                string BuildDrivers()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Section 2 — Drivers</h3>");

                    for (int i = 0; i < model.Drivers.Count; i++)
                    {
                        var d = model.Drivers[i];
                        sb.AppendLine($"<h4>{(i == 0 ? "Primary Insured" : $"Additional Driver {i}")}</h4>");
                        sb.AppendLine($"<p><strong>Name:</strong> {E(d.FirstName)} {E(d.LastName)}</p>");
                        sb.AppendLine($"<p><strong>DOB:</strong> {D(d.DOB)}</p>");
                        sb.AppendLine($"<p><strong>Gender:</strong> {E(d.Gender)} &nbsp;&nbsp; <strong>Marital Status:</strong> {E(d.MaritalStatus)}</p>");
                        sb.AppendLine($"<p><strong>Occupation Industry:</strong> {E(d.OccupationIndustry)} &nbsp;&nbsp; <strong>Occupation Title:</strong> {E(d.OccupationTitle)}</p>");
                        sb.AppendLine($"<p><strong>DL Status:</strong> {E(d.DLStatus)} &nbsp;&nbsp; <strong>Age Licensed:</strong> {E(d.AgeLicensed)}</p>");
                        sb.AppendLine($"<p><strong>DL #:</strong> {E(d.DLNumber)} &nbsp;&nbsp; <strong>DL State:</strong> {E(d.DLState)}</p>");
                        sb.AppendLine($"<p><strong>Defensive Driver Course Date:</strong> {D(d.DefensiveDriverCourseDate)}</p>");
                        sb.AppendLine($"<p><strong>License Suspended/Revoked (Last 5 years):</strong> {E(d.LicenseSuspendedLast5Years)}</p>");
                        sb.AppendLine($"<p><strong>Driver Education:</strong> {E(d.DriverEducation)} &nbsp;&nbsp; <strong>Mature Driver:</strong> {E(d.MatureDriver)} &nbsp;&nbsp; <strong>Good Driver:</strong> {E(d.GoodDriver)}</p>");
                        sb.AppendLine("<p><strong>Carrier Questions</strong></p>");
                        sb.AppendLine($"<p><strong>Telematics Discount:</strong> {E(d.TelematicsDiscount)} &nbsp;&nbsp; <strong>Military Service:</strong> {E(d.MilitaryService)}</p>");
                        sb.AppendLine("<hr/>");
                    }

                    return sb.ToString();
                }

                string BuildVehicles()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Section 3 — Vehicles</h3>");

                    for (int i = 0; i < model.Vehicles.Count; i++)
                    {
                        var v = model.Vehicles[i];
                        sb.AppendLine($"<h4>{(i == 0 ? "Primary Vehicle" : $"Additional Vehicle {i}")}</h4>");
                        sb.AppendLine($"<p><strong>VIN:</strong> {E(v.VIN)}</p>");
                        sb.AppendLine($"<p><strong>Year/Make/Model:</strong> {E(v.Year)} {E(v.Make)} {E(v.Model)}</p>");
                        sb.AppendLine($"<p><strong>Purchase Date:</strong> {D(v.PurchaseDate)}</p>");

                        sb.AppendLine($"<p><strong>Passive Restraints:</strong> {E(v.PassiveRestraints)}</p>");
                        sb.AppendLine($"<p><strong>Anti-Theft:</strong> {E(v.AntiTheft)}</p>");

                        // ✅ MISSING FIELDS (NOW INCLUDED)
                        sb.AppendLine($"<p><strong>Anti-Lock Brakes:</strong> {E(v.AntiLockBrakes)}</p>");
                        sb.AppendLine($"<p><strong>Daytime Running Lights:</strong> {E(v.DaytimeRunningLights)}</p>");
                        sb.AppendLine($"<p><strong>Cost New Value:</strong> {E(v.CostNewValue)}</p>");
                        sb.AppendLine($"<p><strong>Modification Value:</strong> {E(v.ModificationValue)}</p>");
                        sb.AppendLine($"<p><strong>Was New:</strong> {E(v.WasNew)}</p>");
                        sb.AppendLine($"<p><strong>Carpool:</strong> {E(v.Carpool)}</p>");
                        sb.AppendLine($"<p><strong>Telematics:</strong> {E(v.Telematics)}</p>");
                        sb.AppendLine($"<p><strong>TNC:</strong> {E(v.TNC)}</p>");

                        sb.AppendLine($"<p><strong>Vehicle Use:</strong> {E(v.Use)} &nbsp;&nbsp; <strong>Annual Miles:</strong> {E(v.AnnualMiles)}</p>");
                        sb.AppendLine($"<p><strong>Performance:</strong> {E(v.Performance)}</p>");
                        sb.AppendLine($"<p><strong>Ownership Type:</strong> {E(v.OwnershipType)}</p>");
                        sb.AppendLine($"<p><strong>Assigned Driver (100%):</strong> {E(DriverNameByIndex(v.AssignedDriverIndex))}</p>");
                        sb.AppendLine("<hr/>");
                    }

                    return sb.ToString();
                }

                string BuildIncidents()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Section 4 — Incidents</h3>");

                    if (model.Accidents.Any())
                    {
                        sb.AppendLine("<h4>Accidents</h4>");
                        foreach (var a in model.Accidents)
                        {
                            sb.AppendLine($"<p><strong>Date:</strong> {D(a.Date)} &nbsp;&nbsp; <strong>Driver:</strong> {E(DriverNameByIndex(a.DriverIndex))}</p>");
                            sb.AppendLine($"<p><strong>Description:</strong> {E(a.Description)}</p>");
                            sb.AppendLine($"<p><strong>Property Damage:</strong> {E(a.PropertyDamageAmount)} &nbsp;&nbsp; <strong>Bodily Injury:</strong> {E(a.BodilyInjuryAmount)}</p>");
                            sb.AppendLine($"<p><strong>Collision:</strong> {E(a.CollisionAmount)} &nbsp;&nbsp; <strong>Medical Payment:</strong> {E(a.MedicalPaymentAmount)}</p>");

                            // ✅ FIX: Build the vehicle text, then encode ONCE
                            string vehicleText = a.VehicleIndex.HasValue
                                ? VehicleLabelByIndex(a.VehicleIndex)
                                : (a.VehicleInvolvedText ?? "");

                            sb.AppendLine($"<p><strong>Vehicle Involved:</strong> {E(vehicleText)}</p><hr/>");
                        }
                    }

                    if (model.Violations.Any())
                    {
                        sb.AppendLine("<h4>Violations</h4>");
                        foreach (var v in model.Violations)
                        {
                            sb.AppendLine($"<p><strong>Date:</strong> {D(v.Date)} &nbsp;&nbsp; <strong>Driver:</strong> {E(DriverNameByIndex(v.DriverIndex))}</p>");
                            sb.AppendLine($"<p><strong>Description:</strong> {E(v.Description)}</p><hr/>");
                        }
                    }

                    if (model.CompLosses.Any())
                    {
                        sb.AppendLine("<h4>Comp Losses</h4>");
                        foreach (var c in model.CompLosses)
                        {
                            sb.AppendLine($"<p><strong>Date:</strong> {D(c.Date)} &nbsp;&nbsp; <strong>Driver:</strong> {E(DriverNameByIndex(c.DriverIndex))}</p>");
                            sb.AppendLine($"<p><strong>Loss Description:</strong> {E(c.LossDescription)}</p><hr/>");
                        }
                    }

                    return sb.ToString();
                }

                string BuildCoverage()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<h3>Section 5 — General Coverage</h3>");
                    sb.AppendLine($"<p><strong>Bodily Injury:</strong> {E(model.BodilyInjury)}</p>");
                    sb.AppendLine($"<p><strong>Uninsured Motorist:</strong> {E(model.UninsuredMotorist)}</p>");
                    sb.AppendLine($"<p><strong>Underinsured Motorist:</strong> {E(model.UnderinsuredMotorist)}</p>");
                    sb.AppendLine($"<p><strong>Medical Payments:</strong> {E(model.MedicalPayments)}</p>");
                    sb.AppendLine($"<p><strong>Residence is:</strong> {E(model.ResidenceType)}</p>");
                    sb.AppendLine("<hr/>");

                    sb.AppendLine("<h3>Vehicle Coverages</h3>");
                    for (int i = 0; i < model.Vehicles.Count; i++)
                    {
                        var v = model.Vehicles[i];
                        sb.AppendLine($"<h4>Vehicle {i + 1} — {E(v.Year)} {E(v.Make)} {E(v.Model)} (VIN: {E(v.VIN)})</h4>");
                        sb.AppendLine($"<p><strong>Comprehensive:</strong> {E(v.Comprehensive)}</p>");
                        sb.AppendLine($"<p><strong>Collision:</strong> {E(v.Collision)}</p>");
                        sb.AppendLine($"<p><strong>Towing & Labor:</strong> {E(v.Towing)}</p>");
                        sb.AppendLine($"<p><strong>Rental Expense:</strong> {E(v.Rental)}</p>");
                        sb.AppendLine($"<p><strong>Loan/Lease Coverage:</strong> {E(v.LoanLease)}</p>");
                        sb.AppendLine($"<p><strong>Liability (Yes/No):</strong> {E(v.Liability)}</p>");

                        sb.AppendLine("<p><strong>Carrier Questions</strong></p>");
                        sb.AppendLine($"<p><strong>Special Equipment:</strong> {E(v.SpecialEquipment)}</p>");
                        sb.AppendLine($"<p><strong>Branded Title:</strong> {E(v.BrandedTitle)}</p>");
                        sb.AppendLine($"<p><strong>Custom/Additional Equipment:</strong> {E(v.CustomEquipment)}</p>");
                        sb.AppendLine("<hr/>");
                    }

                    return sb.ToString();
                }

                var primary = model.Drivers.FirstOrDefault();
                var subjectName = primary == null ? "Unknown" : $"{primary.FirstName} {primary.LastName}".Trim();

                var emailBody = $@"
<h2>AUTO INSURANCE — New Quote Request</h2>
{BuildApplicantInfo()}
{BuildAddress()}
{BuildContactInfo()}
{BuildPolicy()}
{BuildDrivers()}
{BuildVehicles()}
{BuildIncidents()}
{BuildCoverage()}
<h3>Authorization</h3>
<p><strong>Acknowledged:</strong> {(model.AcknowledgedDisclaimer ? "Yes" : "No")}</p>
";

                // ===================== APPLY HEADING STYLING =====================
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
                    Subject = $"[AUTO] Quote Request | {subjectName}",
                    Body = new ItemBody { ContentType = BodyType.Html, Content = emailBody },
                    ToRecipients = new List<Recipient>
                    {
                        new Recipient { EmailAddress = new EmailAddress { Address = recipientEmail } }
                    }
                };

                await graphClient.Users[senderEmail].SendMail.PostAsync(
                    new SendMailPostRequestBody { Message = message, SaveToSentItems = true }
                );

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
