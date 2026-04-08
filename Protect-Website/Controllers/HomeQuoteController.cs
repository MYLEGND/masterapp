using Microsoft.AspNetCore.Mvc;
using Domain.Entities;
using Protect_Website.Models;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;

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

        public HomeQuoteController(IConfiguration configuration)
        {
            tenantId = configuration["AzureAd:TenantId"]!;
            clientId = configuration["AzureAd:ClientId"]!;
            clientSecret = configuration["AzureAd:ClientSecret"]!;
            senderEmail = configuration["Contact:SenderEmail"] ?? "connect@mylegnd.com";
            recipientEmail = configuration["Contact:RecipientEmail"]!;
            websiteName = configuration["Contact:WebsiteName"] ?? "Legend Legacy Protection";
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
            if (!ModelState.IsValid)
                return View("~/Views/Quote/Home.cshtml", model);

            var leadRecipientEmail = ResolveLeadRecipientEmail();

            try
            {
                var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                var graphClient = new GraphServiceClient(credential);

                // Email body uses `model` for all values
                var message = new Message
                {
                    Subject = $"[HOME QUOTE] New Lead | {model.FirstName} {model.LastName}",
                    Body = new ItemBody
                    {
                        ContentType = BodyType.Html,
                        Content = $@"
<h2>Home Insurance Quote Lead</h2>

<h3>Section 1 — Applicant Information</h3>
<p><strong>First Name:</strong> {model.FirstName}</p>
<p><strong>Last Name:</strong> {model.LastName}</p>
<p><strong>Nickname:</strong> {model.Nickname}</p>
<p><strong>Gender:</strong> {model.Gender}</p>
<p><strong>Date of Birth:</strong> {(model.DOB.HasValue ? model.DOB.Value.ToString("MM/dd/yyyy") : "")}</p>
<p><strong>Marital Status:</strong> {model.MaritalStatus}</p>
<p><strong>Driver’s License #:</strong> {model.DriversLicenseNumber}</p>
<p><strong>DL Status:</strong> {model.DLStatus}</p>
<p><strong>DL State:</strong> {model.DLState}</p>
<p><strong>Education:</strong> {model.Education}</p>
<p><strong>Industry:</strong> {model.Industry}</p>
<p><strong>Address State:</strong> {model.AddressState}</p>
<p><strong>Postal Code:</strong> {model.PostalCode}</p>

<hr />

<h3>Section 2 — Primary Address</h3>
<p><strong>Address:</strong> {model.PrimaryAddress}
    {(string.IsNullOrWhiteSpace(model.PrimaryUnit) ? "" : " Unit " + model.PrimaryUnit)}
    {(string.IsNullOrWhiteSpace(model.PrimaryAddressLine2) ? "" : " " + model.PrimaryAddressLine2)}
</p>
<p><strong>City:</strong> {model.PrimaryCity}</p>
<p><strong>State:</strong> {model.PrimaryState}</p>
<p><strong>Country:</strong> {model.PrimaryCountry}</p>
<p><strong>Postal Code:</strong> {model.PrimaryPostalCode}</p>
<p><strong>Years at Address:</strong> {model.PrimaryYearsAtAddress}</p>

{(string.IsNullOrWhiteSpace(model.PreviousAddress) &&
  string.IsNullOrWhiteSpace(model.PreviousCity) &&
  string.IsNullOrWhiteSpace(model.PreviousState) &&
  string.IsNullOrWhiteSpace(model.PreviousPostalCode)
? ""
: $@"
<hr />

<h3>Previous Address (Only if Primary Address < 3 years)</h3>
<p><strong>Address:</strong> {model.PreviousAddress}
    {(string.IsNullOrWhiteSpace(model.PreviousUnit) ? "" : " Unit " + model.PreviousUnit)}
    {(string.IsNullOrWhiteSpace(model.PreviousAddressLine2) ? "" : " " + model.PreviousAddressLine2)}
</p>
<p><strong>City:</strong> {model.PreviousCity}</p>
<p><strong>State:</strong> {model.PreviousState}</p>
<p><strong>Country:</strong> {model.PreviousCountry}</p>
<p><strong>Postal Code:</strong> {model.PreviousPostalCode}</p>
<p><strong>Years at Address:</strong> {model.PreviousYearsAtAddress}</p>
")}

<hr />

<h3>Contact Information</h3>
<p><strong>Phone Type:</strong> {model.PhoneType}</p>
<p><strong>Phone Number:</strong> {model.PhoneNumber}</p>
<p><strong>Email Type:</strong> {model.EmailType}</p>
<p><strong>Email Address:</strong> {model.EmailAddress}</p>
<p><strong>Preferred Contact Method:</strong> {model.PreferredContactMethod}</p>
<p><strong>Best Time to Contact:</strong> {model.BestTimeToContact}</p>

<hr />

<h3>Section 3 — Policy Information</h3>
<p><strong>Policy / Form Type:</strong> {model.PolicyFormType}</p>
<p><strong>Prior Carrier:</strong> {model.PriorCarrier}</p>
<p><strong>Expiration Date (Current Policy):</strong> {(model.CurrentPolicyExpirationDate.HasValue ? model.CurrentPolicyExpirationDate.Value.ToString("MM/dd/yyyy") : "")}</p>
<p><strong>Prior Policy Premium:</strong> {model.PriorPolicyPremium}</p>
<p><strong>Years With Prior Carrier:</strong> {model.YearsWithPriorCarrier}</p>
<p><strong>Months With Prior Carrier:</strong> {model.MonthsWithPriorCarrier}</p>
<p><strong>Years With Continuous Coverage:</strong> {model.YearsContinuousCoverage}</p>
<p><strong>Months With Continuous Coverage:</strong> {model.MonthsContinuousCoverage}</p>
<p><strong>Credit Check Authorized:</strong> {model.CreditCheckAuthorized}</p>
<p><strong>Quote as Package:</strong> {model.QuoteAsPackage}</p>
<p><strong>Effective Date (New Policy):</strong> {(model.NewPolicyEffectiveDate.HasValue ? model.NewPolicyEffectiveDate.Value.ToString("MM/dd/yyyy") : "")}</p>

<hr />

<h3>Underwriting Information</h3>
<p><strong>Cancelled/Declined/Non-Renewed last 5 years:</strong> {model.CancelledDeclinedNonRenewedLast5Years}</p>
<p><strong>Home Under Construction:</strong> {model.HomeUnderConstruction}</p>
<p><strong>Business/Daycare On Premises:</strong> {model.BusinessOrDaycareOnPremises}</p>
<p><strong># of Employees:</strong> {model.NumberOfEmployees}</p>
<p><strong>Swimming Pool On Premises:</strong> {model.SwimmingPoolOnPremises}</p>
<p><strong>Dogs On Premises:</strong> {model.DogsOnPremises}</p>

<hr />

<h3>Additional Carrier Questions</h3>
<p><strong>Paperless:</strong> {model.Paperless}</p>
<p><strong>Number of Animals on Premises:</strong> {model.NumberOfAnimalsOnPremises}</p>
<p><strong>Lapse in Coverage Past 12 Months:</strong> {model.LapseInCoveragePast12Months}</p>
<p><strong>Auto Years With Prior Carrier/Agent:</strong> {model.AutoYearsWithPriorCarrierOrAgent}</p>
<p><strong>Additional Notes:</strong> {model.AdditionalCarrierQuestions}</p>

<hr />

<h3>Section 4 — Dwelling Information</h3>
<p><strong>Dwelling Usage:</strong> {model.DwellingUsage}</p>
<p><strong>Occupancy Type:</strong> {model.OccupancyType}</p>
<p><strong>Dwelling Type:</strong> {model.DwellingType}</p>
<p><strong>Number of Occupants:</strong> {model.NumberOfOccupants}</p>
<p><strong>Number of Stories:</strong> {model.NumberOfStories}</p>
<p><strong>Square Footage:</strong> {model.SquareFootage}</p>
<p><strong>Year Built:</strong> {model.YearBuilt}</p>
<p><strong>Construction Style:</strong> {model.ConstructionStyle}</p>

<hr />

<h3>Construction & Exterior</h3>
<p><strong>Roof Type (Main Material):</strong> {model.RoofTypeMainMaterial}</p>
<p><strong>Foundation Type:</strong> {model.FoundationType}</p>
<p><strong>Roof Design:</strong> {model.RoofDesign}</p>
<p><strong>Exterior Walls:</strong> {model.ExteriorWalls}</p>

<hr />

<h3>Protection & Systems</h3>
<p><strong>Full Baths:</strong> {model.FullBaths}</p>
<p><strong>Half Baths:</strong> {model.HalfBaths}</p>
<p><strong>Wood Burning Stoves:</strong> {model.WoodBurningStoves}</p>
<p><strong>Burglar Alarm:</strong> {model.BurglarAlarm}</p>
<p><strong>Fire Detection:</strong> {model.FireDetection}</p>
<p><strong>Sprinkler System:</strong> {model.SprinklerSystem}</p>
<p><strong>Smoke Detector:</strong> {model.SmokeDetector}</p>

<hr />

<h3>Geographical Info</h3>
<p><strong>Purchase Price:</strong> {model.PurchasePrice}</p>
<p><strong>Purchase Date:</strong> {(model.PurchaseDate.HasValue ? model.PurchaseDate.Value.ToString("MM/dd/yyyy") : "")}</p>
<p><strong>Distance From Fire Station (miles):</strong> {model.DistanceFromFireStationMiles}</p>
<p><strong>Feet From Hydrant:</strong> {model.FeetFromHydrant}</p>

<hr />

<h3>Updates to the House</h3>
<p><strong>Heating Update:</strong> {model.HeatingUpdate}</p>
<p><strong>Heating Year Updated:</strong> {model.HeatingYearUpdated}</p>
<p><strong>Electrical Update:</strong> {model.ElectricalUpdate}</p>
<p><strong>Electrical Year Updated:</strong> {model.ElectricalYearUpdated}</p>
<p><strong>Plumbing Update:</strong> {model.PlumbingUpdate}</p>
<p><strong>Plumbing Year Updated:</strong> {model.PlumbingYearUpdated}</p>
<p><strong>Roofing Update:</strong> {model.RoofingUpdate}</p>
<p><strong>Roofing Year Updated:</strong> {model.RoofingYearUpdated}</p>

<hr />

<h3>Section 5 — General Coverages</h3>
<p><strong>Dwelling Coverage:</strong> {model.DwellingCoverage}</p>
<p><strong>Estimated Replacement Cost:</strong> {model.EstReplacementCost}</p>
<p><strong>Personal Property:</strong> {model.PersonalProperty}</p>
<p><strong>Loss of Use:</strong> {model.LossOfUse}</p>
<p><strong>Personal Liability:</strong> {model.PersonalLiability}</p>
<p><strong>Medical Payments:</strong> {model.MedicalPayments}</p>
<p><strong>All Perils Deductible:</strong> {model.AllPerilsDeductible}</p>
<p><strong>Theft Deductible:</strong> {model.TheftDeductible}</p>
<p><strong>Wind Deductible:</strong> {model.WindDeductible}</p>

<hr />

<h3>Financial Interests</h3>
<p><strong>First Mortgagee:</strong> {model.FirstMortgagee}</p>
<p><strong>Second Mortgagee:</strong> {model.SecondMortgagee}</p>
<p><strong>Third Mortgagee:</strong> {model.ThirdMortgagee}</p>
<p><strong>Cosigner:</strong> {model.Cosigner}</p>
<p><strong>Equity Line of Credit:</strong> {model.EquityLineOfCredit}</p>
<p><strong># of Other Interests:</strong> {model.NumberOfOtherInterests}</p>

<hr />

<h3>Section 6 — Endorsements</h3>
<p><strong>Building Additions/Alterations:</strong> {model.BuildingAdditionsOrAlterations}</p>
<p><strong>Increased Replacement Cost Dwelling %:</strong> {model.IncreasedReplacementCostDwellingPercentage}</p>
<p><strong>Loss Assessment:</strong> {model.LossAssessment}</p>
<p><strong>Ordinance or Law:</strong> {model.OrdinanceOrLaw}</p>
<p><strong>Increased Coverage on Credit Card:</strong> {model.IncreasedCoverageOnCreditCard}</p>
<p><strong>Increased Limit Jewelry/Watches/Furs:</strong> {model.IncreasedLimitJewelryWatchesFurs}</p>
<p><strong>Water Backup:</strong> {model.WaterBackup}</p>
<p><strong>Increased Mold Property Damage:</strong> {model.IncreasedMoldPropertyDamage}</p>
<p><strong>Personal Injury:</strong> {model.PersonalInjury}</p>
<p><strong>Special Personal Property:</strong> {model.SpecialPersonalProperty}</p>
<p><strong>Sinkhole Collapse:</strong> {model.SinkholeCollapse}</p>

<hr />

<h3>Earthquake</h3>
<p><strong>Earthquake Zone:</strong> {model.EarthquakeZone}</p>
<p><strong>Deductible:</strong> {model.EarthquakeDeductible}</p>
<p><strong>Percent Veneer:</strong> {model.PercentVeneer}</p>

<hr />

<h3>Authorization</h3>
<p><strong>Acknowledged Disclaimer:</strong> {(model.AcknowledgedDisclaimer ? "Acknowledged" : "Not Acknowledged")}</p>
"
                    },
                    ToRecipients = new List<Recipient>
                    {
                        new Recipient
                        {
                            EmailAddress = new EmailAddress
                            {
                                Address = leadRecipientEmail
                            }
                        }
                    }
                };

                        // ===================== HEADING STYLING =====================
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

        message.Body.Content = ApplyHeadingHighlighting(message.Body.Content);

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

        private string ResolveLeadRecipientEmail()
        {
            if (HttpContext?.Items.TryGetValue("TrackingProfile", out var trackingProfileObj) == true &&
                trackingProfileObj is AgentTrackingProfile trackingProfile &&
                !string.IsNullOrWhiteSpace(trackingProfile.AgentUpn))
            {
                return trackingProfile.AgentUpn.Trim();
            }

            return recipientEmail;
        }
    }
}
