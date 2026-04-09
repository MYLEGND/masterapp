using Microsoft.AspNetCore.Mvc;
using Protect_Website.Models;
using Protect_Website.Services;
using ProtectWebsite.Services;
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

                string M(decimal? v) => v.HasValue ? v.Value.ToString("C0") : "";

                var rows = new LeadEmailTemplate.RowBuilder()
                    .Section("Personal Information")
                    .Row("Name",           $"{model.FirstName} {model.LastName}".Trim())
                    .Row("Email",          model.Email)
                    .Row("Phone",          model.PhoneNumber)
                    .Row("Age",            model.Age?.ToString())
                    .Row("Marital Status", model.MaritalStatus)
                    .Row("Household Size", model.HouseholdSize?.ToString())
                    .Row("State",          model.State)
                    .Row("Occupation",     model.Occupation)
                    .Section("Income & Work")
                    .Row("Annual Income",       M(model.AnnualIncome))
                    .Row("Retirement Age",      model.RetirementAgeTarget?.ToString())
                    .Row("Working Years Left",  model.WorkingYearsLeft?.ToString())
                    .Row("Self Employed",       model.SelfEmployed)
                    .Row("Employer Benefits",   model.EmployerBenefits)
                    .Section("Cash Flow")
                    .Row("Monthly Income",      M(model.MonthlyIncome))
                    .Row("Other Income",        M(model.OtherIncome))
                    .Row("Taxes",               M(model.Taxes))
                    .Row("Monthly Expenses",    M(model.MonthlyExpenses))
                    .Row("Monthly Debt",        M(model.MonthlyDebt))
                    .Row("Mortgage Payment",    M(model.MortgagePayment))
                    .Row("Emergency Savings",   M(model.EmergencySavings))
                    .Row("Checking Account",    M(model.CheckingAccount))
                    .Row("Savings Account",     M(model.SavingsAccount))
                    .Row("Roth IRA",            M(model.RothIRA))
                    .Row("Traditional IRA",     M(model.TraditionalIRA))
                    .Row("401k",                M(model._401k))
                    .Row("Brokerage Account",   M(model.BrokerageAccount))
                    .Row("HSA",                 M(model.HSA))
                    .Row("Other Assets",        M(model.OtherPropertyAssets))
                    .Row("Business Value",      M(model.BusinessOwnershipValue))
                    .Row("Primary Real Estate", M(model.RealEstateValue))
                    .Row("Rental Property",     M(model.RentalPropertyValue))
                    .Row("Vehicle Value",       M(model.VehicleValue))
                    .Row("Collectibles",        M(model.CollectiblesValue))
                    .Row("Mortgage Balance",    M(model.MortgageBalance))
                    .Row("Student Loans",       M(model.StudentLoans))
                    .Row("Other Liabilities",   M(model.OtherLiabilities))
                    .Row("Net Monthly Cash Flow", result.NetCashFlow.ToString("C0"))
                    .Section("Estate Planning")
                    .Row("Will",              model.HasWill)
                    .Row("Trust",             model.HasTrust)
                    .Row("POA",               model.HasPOA)
                    .Row("Health Directive",  model.HasHealthDirective)
                    .Section("Life Insurance")
                    .Row("Has Life Insurance",     model.HasLifeInsurance)
                    .Row("Individual Coverage",    M(model.LifeCoverageIndividual))
                    .Row("Group Coverage",         M(model.LifeCoverageGroup))
                    .Row("Primary Beneficiaries",  model.PrimaryBeneficiaries)
                    .Row("Secondary Beneficiaries",model.SecondaryBeneficiaries)
                    .Section("Disability Insurance")
                    .Row("Has DI",           model.HasDI)
                    .Row("Monthly Benefit",  M(model.DIBenefitMonthly))
                    .Row("Waiting Period",   model.DIWaitingPeriod?.ToString())
                    .Row("Benefit Period",   model.DIBenefitPeriod)
                    .Section("Health Coverage")
                    .Row("Coverage Type",   model.HealthCoverageType)
                    .Row("Deductible",      M(model.HealthDeductible))
                    .Row("Out-of-Pocket Max", M(model.HealthOutOfPocketMax))
                    .Section("Property & Liability")
                    .Row("Home Insurance",             model.HasHomeInsurance)
                    .Row("Home Coverage Limit",        M(model.HomeCoverageLimit))
                    .Row("Auto Insurance",             model.HasAutoInsurance)
                    .Row("Auto Coverage Limit",        M(model.AutoCoverageLimit))
                    .Row("General Liability",          model.HasGeneralLiability)
                    .Row("General Liability Limit",    M(model.GeneralLiabilityLimit))
                    .Row("Professional Liability",     model.HasProfessionalLiability)
                    .Row("Prof. Liability Limit",      M(model.ProfessionalLiabilityLimit))
                    .Section("Assessment Results")
                    .Row("Life Score",        result.LifeScore.ToString("N0"))
                    .Row("Disability Score",  result.DisabilityScore.ToString("N0"))
                    .Row("Health Score",      result.HealthScore.ToString("N0"))
                    .Row("Property Score",    result.PropertyScore.ToString("N0"))
                    .Row("Cash Flow Score",   result.CashFlowScore.ToString("N0"))
                    .Row("Estate Score",      result.EstateScore.ToString("N0"))
                    .Row("Protection Score",  result.ProtectionScore.ToString("N0"))
                    .Row("Overall Score",     result.OverallScore.ToString("N0"))
                    .Row("Advisor Feedback",  result.FeedbackText)
                    .Section("Authorization")
                    .Row("Disclaimer Acknowledged", LeadEmailTemplate.Bool(model.AcknowledgedDisclaimer));

                var finalHtml = LeadEmailTemplate.Wrap("Risk Assessment — New Submission", rows.ToString());

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
