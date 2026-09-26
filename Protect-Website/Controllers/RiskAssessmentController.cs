using Infrastructure.Analytics;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Leads;
using Microsoft.EntityFrameworkCore;
using ProtectWebsite.Services.Tracking;
using System.Text.Json;
using Shared.Analytics;
using Microsoft.AspNetCore.Mvc;
using Protect_Website.Models;
using Protect_Website.Services;
using ProtectWebsite.Services;
using ProtectWebsite.Services.Communication;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Text;
using System.Net;

namespace Protect_Website.Controllers
{
    [Route("RiskAssessment")]
    public class RiskAssessmentController : Controller
    {
        private readonly string recipientEmail;
        private readonly IProtectEmailSender _emailSender;

        private readonly MasterAppDbContext _db;
        private readonly AgentTrackingResolver _resolver;
        private readonly IWebsiteLifeLeadCaptureService _capture;
        private readonly ILogger<RiskAssessmentController> _logger;

        public RiskAssessmentController(IConfiguration configuration, IProtectEmailSender emailSender,
            MasterAppDbContext db, AgentTrackingResolver resolver, IWebsiteLifeLeadCaptureService capture,
            ILogger<RiskAssessmentController> logger)
        {
            recipientEmail = configuration["Contact:RecipientEmail"]!;
            _emailSender = emailSender;
            _db = db;
            _resolver = resolver;
            _capture = capture;
            _logger = logger;
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
            if (!model.AcknowledgedDisclaimer)
                ModelState.AddModelError(nameof(model.AcknowledgedDisclaimer), "Please authorize contact before submitting.");
            if (!ModelState.IsValid)
                return View("~/Views/RiskAssessment/Index.cshtml", model);

            try
            {
                var ct = HttpContext.RequestAborted;
                var requestedSlug = Request.Form["AgentSlug"].FirstOrDefault();
                var ownership = await WebsiteLeadOwnerAuthority.ResolveAsync(
                    HttpContext,
                    _resolver,
                    recipientEmail,
                    requestedSlug,
                    ct);
                if (!string.IsNullOrWhiteSpace(requestedSlug) && ownership.ExplicitSlugInvalid)
                {
                    ModelState.AddModelError("", "The advisor link is no longer available.");
                    return View("~/Views/RiskAssessment/Index.cshtml", model);
                }
                var recipient = ownership.RecipientEmail;
                var lead = new WebsiteLead
                {
                    LeadId = Guid.NewGuid(), FirstName = model.FirstName.Trim(), LastName = model.LastName.Trim(),
                    Email = model.Email.Trim(), Phone = model.PhoneNumber, InterestType = "risk_assessment",
                    SourcePageKey = "risk_assessment", TermsAccepted = true,
                    MarketingEmailConsent = model.AcknowledgedDisclaimer,
                    CallTextConsent = model.AcknowledgedDisclaimer && !string.IsNullOrWhiteSpace(model.PhoneNumber),
                    AgentTrackingProfileId = ownership.AgentProfileId, AgentSlug = ownership.AgentSlug,
                    SessionId = Request.Form["SessionId"].FirstOrDefault(), VisitorId = Request.Form["VisitorId"].FirstOrDefault(),
                    UtmSource = Request.Form["UtmSource"].FirstOrDefault(), UtmMedium = Request.Form["UtmMedium"].FirstOrDefault(),
                    UtmCampaign = Request.Form["UtmCampaign"].FirstOrDefault(),
                    Host = Request.Host.ToString(), Environment = EnvironmentLabelResolver.Resolve(),
                    IsInternal = WebsiteLeadCaptureSafety.ShouldMarkAsInternalTest(Request.Host.Host),
                    CreatedUtc = DateTime.UtcNow, Status = "New", MetadataJson = JsonSerializer.Serialize(model)
                };
                WebsiteLifeLeadCaptureResult captured = null!;
                if (!await WebsiteLeadSubmission.TryCreateAsync(_db, lead, Request.Form["SubmissionId"].FirstOrDefault(), ct, async _ =>
                {
                captured = await _capture.UpsertAsync(new WebsiteLifeLeadCaptureRequest
                {
                    WebsiteLeadId = lead.LeadId, SubmittedUtc = lead.CreatedUtc, ProductType = "risk_assessment",
                    OfferKey = "risk_assessment", FirstName = lead.FirstName, LastName = lead.LastName,
                    Email = lead.Email, Phone = lead.Phone, State = model.State, Age = model.Age,
                    AgentTrackingProfileId = lead.AgentTrackingProfileId, AgentSlug = lead.AgentSlug, RecipientEmail = recipient
                }, ct);
                    if (!captured.Captured && captured.Reason != "InternalTestLead")
                        throw new InvalidOperationException("The advisor handoff could not be completed.");
                lead.Status = captured.Captured ? "New" : "InternalTestLead";
                var persistedEvent = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext
                {
                    EventName = "lead_persisted",
                    EventCategory = "lead",
                    EventUtc = lead.CreatedUtc,
                    PageKey = "risk_assessment",
                    FormKey = "risk_assessment",
                    QuoteType = "risk_assessment",
                    SessionId = lead.SessionId,
                    VisitorId = lead.VisitorId,
                    AgentTrackingProfileId = lead.AgentTrackingProfileId,
                    AgentSlug = lead.AgentSlug,
                    Environment = lead.Environment,
                    Host = lead.Host,
                    IsInternal = lead.IsInternal,
                    IsBrowserSignal = false,
                    IsServerAuthority = false,
                    MetaServerAuthorityEligible = true,
                    Metadata = new { LeadId = lead.LeadId, CrmCaptured = captured.Captured }
                });
                persistedEvent.MetadataJson = MetaSignalSingleTruthPolicy.BuildMetadataJson(
                    eventName: "lead_persisted",
                    leadId: lead.LeadId,
                    sessionId: lead.SessionId,
                    payload: new { LeadId = lead.LeadId, CrmCaptured = captured.Captured },
                    isBrowserSignal: false,
                    isServerAuthority: false,
                    metaServerAuthorityEligible: true,
                    metaSingleTruthDispatchEligible: false,
                    metaPipelineOrigin: "risk_assessment");
                UnifiedAnalyticsWriter.Write(_db, persistedEvent);
                await _db.SaveChangesAsync(ct);

                }))
                {
                    lead = await _db.WebsiteLeads.SingleAsync(x => x.LeadId == lead.LeadId, ct);
                    model = JsonSerializer.Deserialize<RiskAssessmentModel>(lead.MetadataJson!)
                        ?? throw new InvalidOperationException("The saved assessment cannot be loaded.");
                }
                if (!await WebsiteLeadSubmission.TryClaimNotificationAsync(_db, lead, ct))
                {
                    await _db.Entry(lead).ReloadAsync(ct);
                    if (lead.NotificationSentUtc != null)
                        return RedirectToAction("Index", "ThankYou");
                    ModelState.AddModelError("", "Your assessment is saved. The advisor notification is still unconfirmed. Please try again in 15 minutes to retry without creating another assessment.");
                    return View("~/Views/RiskAssessment/Index.cshtml", model);
                }

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
                var emailSent = await _emailSender.TrySendAsync(
                    recipient,
                    $"[RISK ASSESSMENT] {model.FirstName} {model.LastName}",
                    finalHtml,
                    replyToEmail: model.Email,
                    saveToSentItems: true,
                    cancellationToken: HttpContext?.RequestAborted ?? CancellationToken.None);

                await WebsiteLeadSubmission.CompleteNotificationAsync(_db, lead, emailSent, ct);
                if (!emailSent)
                {
                    _logger.LogWarning("Risk assessment captured; notification failed for lead {LeadId}.", lead.LeadId);
                    ModelState.AddModelError("", "Your assessment is saved. The advisor email failed. Submit again to retry the notification without creating another assessment.");
                    return View("~/Views/RiskAssessment/Index.cshtml", model);
                }

                // ✅ Thank you routing
                TempData["QuoteType"] = "RiskAssessment";
                return RedirectToAction("Index", "ThankYou");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Risk assessment submission failed.");
                ModelState.AddModelError("", "We could not complete your assessment. Please retry or contact your advisor.");
                return View("~/Views/RiskAssessment/Index.cshtml", model);
            }
        }
    }
}
