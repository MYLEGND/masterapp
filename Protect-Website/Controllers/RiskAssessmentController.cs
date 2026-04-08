using Microsoft.AspNetCore.Mvc;
using Protect_Website.Models;
using Protect_Website.Services;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Text;
using System.Net;

namespace Protect_Website.Controllers
{
    [Route("RiskAssessment")]
    public class RiskAssessmentController : Controller
    {
        private readonly string tenantId;
        private readonly string clientId;
        private readonly string clientSecret;
        private readonly string senderEmail;
        private readonly string recipientEmail;
        private readonly string websiteName;

        public RiskAssessmentController(IConfiguration configuration)
        {
            tenantId = configuration["AzureAd:TenantId"]!;
            clientId = configuration["AzureAd:ClientId"]!;
            clientSecret = configuration["AzureAd:ClientSecret"]!;
            senderEmail = configuration["Contact:SenderEmail"] ?? "connect@mylegnd.com";
            recipientEmail = configuration["Contact:RecipientEmail"]!;
            websiteName = configuration["Contact:WebsiteName"] ?? "Legend Legacy Protection";
        }

        // GET: /RiskAssessment
        [HttpGet("")]
        public IActionResult Index()
        {
            return View("~/Views/RiskAssessment/Index.cshtml", new RiskAssessmentModel());
        }

        // POST: /RiskAssessment
        [HttpPost("")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitRiskAssessment(RiskAssessmentModel model)
        {
            if (!ModelState.IsValid)
                return View("~/Views/RiskAssessment/Index.cshtml", model);

            try
            {
                // ------------------ CALCULATE RESULTS ------------------
                var result = RiskAssessmentCalculator.Calculate(model);

                // ------------------ SAFE ENCODE ------------------
                static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
                static string Money(decimal? v) => v.HasValue ? v.Value.ToString("C0") : "";

                // ------------------ BUILD QUESTION + ANSWER SECTION ------------------
                var qa = new StringBuilder();

                qa.Append("<h2>Risk Assessment – Questions & Answers</h2>");

                // Personal Information
                qa.Append("<h3>Personal Information</h3>");
                qa.Append($"<p><strong>First Name:</strong> {E(model.FirstName)}</p>");
                qa.Append($"<p><strong>Last Name:</strong> {E(model.LastName)}</p>");
                qa.Append($"<p><strong>Email:</strong> {E(model.Email)}</p>");
                qa.Append($"<p><strong>Phone:</strong> {E(model.PhoneNumber)}</p>");
                qa.Append($"<p><strong>Age:</strong> {E(model.Age?.ToString() ?? "")}</p>");
                qa.Append($"<p><strong>Marital Status:</strong> {E(model.MaritalStatus)}</p>");
                qa.Append($"<p><strong>Household Size:</strong> {E(model.HouseholdSize?.ToString() ?? "")}</p>");
                qa.Append($"<p><strong>State:</strong> {E(model.State)}</p>");
                qa.Append($"<p><strong>Occupation:</strong> {E(model.Occupation)}</p>");

                // Income & Work
                qa.Append("<hr /><h3>Income & Work</h3>");
                qa.Append($"<p><strong>Annual Income:</strong> {Money(model.AnnualIncome)}</p>");
                qa.Append($"<p><strong>Retirement Age Target:</strong> {E(model.RetirementAgeTarget?.ToString() ?? "")}</p>");
                qa.Append($"<p><strong>Working Years Left:</strong> {E(model.WorkingYearsLeft?.ToString() ?? "")}</p>");
                qa.Append($"<p><strong>Self Employed:</strong> {E(model.SelfEmployed)}</p>");
                qa.Append($"<p><strong>Employer Benefits:</strong> {E(model.EmployerBenefits)}</p>");

                // Cash Flow
                qa.Append("<hr /><h3>Cash Flow</h3>");
                qa.Append($"<p><strong>Monthly Income:</strong> {Money(model.MonthlyIncome)}</p>");
                qa.Append($"<p><strong>Other Income:</strong> {Money(model.OtherIncome)}</p>");
                qa.Append($"<p><strong>Taxes:</strong> {Money(model.Taxes)}</p>");
                qa.Append($"<p><strong>Monthly Expenses:</strong> {Money(model.MonthlyExpenses)}</p>");
                qa.Append($"<p><strong>Monthly Debt:</strong> {Money(model.MonthlyDebt)}</p>");
                qa.Append($"<p><strong>Mortgage Payment:</strong> {Money(model.MortgagePayment)}</p>");
                qa.Append($"<p><strong>Emergency Savings:</strong> {Money(model.EmergencySavings)}</p>");
                qa.Append($"<p><strong>Checking Account:</strong> {Money(model.CheckingAccount)}</p>");
                qa.Append($"<p><strong>Savings Account:</strong> {Money(model.SavingsAccount)}</p>");
                qa.Append($"<p><strong>Roth IRA:</strong> {Money(model.RothIRA)}</p>");
                qa.Append($"<p><strong>Traditional IRA:</strong> {Money(model.TraditionalIRA)}</p>");
                qa.Append($"<p><strong>401k:</strong> {Money(model._401k)}</p>");
                qa.Append($"<p><strong>Brokerage Account:</strong> {Money(model.BrokerageAccount)}</p>");
                qa.Append($"<p><strong>HSA:</strong> {Money(model.HSA)}</p>");
                qa.Append($"<p><strong>Other Assets:</strong> {Money(model.OtherPropertyAssets)}</p>");
                qa.Append($"<p><strong>Business Ownership Value:</strong> {Money(model.BusinessOwnershipValue)}</p>");
                qa.Append($"<p><strong>Primary Real Estate Value:</strong> {Money(model.RealEstateValue)}</p>");
                qa.Append($"<p><strong>Rental Property Value:</strong> {Money(model.RentalPropertyValue)}</p>");
                qa.Append($"<p><strong>Vehicle Value:</strong> {Money(model.VehicleValue)}</p>");
                qa.Append($"<p><strong>Collectibles Value:</strong> {Money(model.CollectiblesValue)}</p>");
                qa.Append($"<p><strong>Mortgage Balance:</strong> {Money(model.MortgageBalance)}</p>");
                qa.Append($"<p><strong>Student Loans:</strong> {Money(model.StudentLoans)}</p>");
                qa.Append($"<p><strong>Other Liabilities:</strong> {Money(model.OtherLiabilities)}</p>");

                // ✅ MUST MATCH CALCULATOR (result)
                qa.Append($"<p><strong>Net Monthly Cash Flow:</strong> {result.NetCashFlow.ToString("C0")}</p>");

                // Estate Planning
                qa.Append("<hr /><h3>Estate Planning</h3>");
                qa.Append($"<p><strong>Will:</strong> {E(model.HasWill)}</p>");
                qa.Append($"<p><strong>Trust:</strong> {E(model.HasTrust)}</p>");
                qa.Append($"<p><strong>POA:</strong> {E(model.HasPOA)}</p>");
                qa.Append($"<p><strong>Health Directive:</strong> {E(model.HasHealthDirective)}</p>");

                // Life Insurance
                qa.Append("<hr /><h3>Life Insurance</h3>");
                qa.Append($"<p><strong>Has Life Insurance:</strong> {E(model.HasLifeInsurance)}</p>");
                qa.Append($"<p><strong>Individual Coverage:</strong> {Money(model.LifeCoverageIndividual)}</p>");
                qa.Append($"<p><strong>Group Coverage:</strong> {Money(model.LifeCoverageGroup)}</p>");
                qa.Append($"<p><strong>Primary Beneficiaries:</strong> {E(model.PrimaryBeneficiaries)}</p>");
                qa.Append($"<p><strong>Secondary Beneficiaries:</strong> {E(model.SecondaryBeneficiaries)}</p>");

                // Disability Insurance
                qa.Append("<hr /><h3>Disability Insurance</h3>");
                qa.Append($"<p><strong>Has Disability Insurance:</strong> {E(model.HasDI)}</p>");
                qa.Append($"<p><strong>Monthly Benefit:</strong> {Money(model.DIBenefitMonthly)}</p>");
                qa.Append($"<p><strong>Waiting Period (Months):</strong> {E(model.DIWaitingPeriod?.ToString() ?? "")}</p>");
                qa.Append($"<p><strong>Benefit Period:</strong> {E(model.DIBenefitPeriod)}</p>");

                // Health Coverage
                qa.Append("<hr /><h3>Health Coverage</h3>");
                qa.Append($"<p><strong>Coverage Type:</strong> {E(model.HealthCoverageType)}</p>");
                qa.Append($"<p><strong>Deductible:</strong> {Money(model.HealthDeductible)}</p>");
                qa.Append($"<p><strong>Out-of-Pocket Max:</strong> {Money(model.HealthOutOfPocketMax)}</p>");

                // Property & Liability
                qa.Append("<hr /><h3>Property & Liability</h3>");
                qa.Append($"<p><strong>Home Insurance:</strong> {E(model.HasHomeInsurance)}</p>");
                qa.Append($"<p><strong>Home Coverage Limit:</strong> {Money(model.HomeCoverageLimit)}</p>");
                qa.Append($"<p><strong>Auto Insurance:</strong> {E(model.HasAutoInsurance)}</p>");
                qa.Append($"<p><strong>Auto Coverage Limit:</strong> {Money(model.AutoCoverageLimit)}</p>");
                qa.Append($"<p><strong>General Liability:</strong> {E(model.HasGeneralLiability)}</p>");
                qa.Append($"<p><strong>General Liability Limit:</strong> {Money(model.GeneralLiabilityLimit)}</p>");
                qa.Append($"<p><strong>Professional Liability:</strong> {E(model.HasProfessionalLiability)}</p>");
                qa.Append($"<p><strong>Professional Liability Limit:</strong> {Money(model.ProfessionalLiabilityLimit)}</p>");

                // ------------------ RESULTS SECTION ------------------
               var resultsHtml = $@"
<hr />
<h2>Risk Assessment Results</h2>

<p><strong>Life Score:</strong> {result.LifeScore:N0}</p>
<p><strong>Disability Score:</strong> {result.DisabilityScore:N0}</p>
<p><strong>Health Score:</strong> {result.HealthScore:N0}</p>
<p><strong>Property Score:</strong> {result.PropertyScore:N0}</p>
<p><strong>Cash Flow Score:</strong> {result.CashFlowScore:N0}</p>
<p><strong>Estate Score:</strong> {result.EstateScore:N0}</p>
<p><strong>Protection Score:</strong> {result.ProtectionScore:N0}</p>
<p><strong>Overall Score:</strong> {result.OverallScore:N0}</p>

<h3>Advisor Feedback</h3>
<p>{E(result.FeedbackText)}</p>

<hr />
<p><strong>Disclaimer Acknowledged:</strong> {(model.AcknowledgedDisclaimer ? "Yes" : "No")}</p>
";

// ===================== HEADING STYLING =====================
string headingColor = "#cca134f1";
string headingFontSize = "1.2em";
string headingPadding = "4px 6px";

string ApplyHeadingHighlighting(string html)
{
    if (string.IsNullOrWhiteSpace(html)) return html;

    return System.Text.RegularExpressions.Regex.Replace(
        html,
        @"<\s*(h[234])\s*>(.*?)<\s*/\s*\1\s*>",
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

// Build FINAL html first
var finalHtml = ApplyHeadingHighlighting(qa.ToString() + resultsHtml);

                // ------------------ SEND EMAIL ------------------
                var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                var graphClient = new GraphServiceClient(credential);

                var message = new Message
                {
                    Subject = $"[RISK ASSESSMENT] {model.FirstName} {model.LastName}",
                    Body = new ItemBody
                    {
                        ContentType = BodyType.Html,
                        Content = finalHtml
                    },
                    ToRecipients =
                    [
                        new Recipient
                        {
                            EmailAddress = new EmailAddress { Address = recipientEmail }
                        }
                    ]
                };

                var requestBody = new SendMailPostRequestBody
                {
                    Message = message,
                    SaveToSentItems = true
                };

                await graphClient.Users[senderEmail].SendMail.PostAsync(requestBody);

                // ✅ Thank you routing
                TempData["QuoteType"] = "RiskAssessment";
                return RedirectToAction("Index", "ThankYou");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", $"Failed to send risk assessment: {ex.Message}");
                return View("~/Views/RiskAssessment/Index.cshtml", model);
            }
        }
    }
}
