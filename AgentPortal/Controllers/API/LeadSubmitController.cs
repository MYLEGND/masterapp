using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AgentPortal.Security;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Leads;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.EntityFrameworkCore;
using AgentPortal.Services;
using Microsoft.AspNetCore.RateLimiting;
using Shared.Analytics;

namespace AgentPortal.Controllers.Api;

[ApiController]
[Route("api/lead/submit")]
[EnableCors("TrackingCors")]
[AllowAnonymous]
public class LeadSubmitController : ControllerBase
{
    private readonly MasterAppDbContext _db;
    private readonly IConfiguration _config;
    private readonly IEmailSender _emailSender;
    private readonly Services.Tracking.AgentTrackingResolver _resolver;
    private readonly ILogger<LeadSubmitController> _logger;
    private readonly string _founderUpn;
    private readonly AgentPortal.Models.AppFeatureFlags _flags;
    private readonly IngestSignatureValidator _signatureValidator;

    public LeadSubmitController(MasterAppDbContext db, IConfiguration config, IEmailSender emailSender, Services.Tracking.AgentTrackingResolver resolver, ILogger<LeadSubmitController> logger, Microsoft.Extensions.Options.IOptions<AgentPortal.Models.AppFeatureFlags> flags, IngestSignatureValidator signatureValidator)
    {
        _db = db;
        _config = config;
        _emailSender = emailSender;
        _resolver = resolver;
        _logger = logger;
        _flags = flags.Value;
        _signatureValidator = signatureValidator;
        _founderUpn = _config["Founder:Upn"] ?? throw new InvalidOperationException("Founder:Upn configuration is required");
    }

    public sealed class LeadSubmitRequest
    {
        [Required]
        public string FirstName { get; set; } = null!;
        public string? LastName { get; set; }
        [Required, EmailAddress]
        public string Email { get; set; } = null!;
        public string? Phone { get; set; }
        public string? PreferredContactMethod { get; set; }
        [Required]
        public string InterestType { get; set; } = null!;
        public string? Notes { get; set; }
        public string? SourcePageKey { get; set; }
        public string? SourceCtaKey { get; set; }
        public string? UtmSource { get; set; }
        public string? UtmMedium { get; set; }
        public string? UtmCampaign { get; set; }
        public string? UtmId { get; set; }
        public string? UtmTerm { get; set; }
        public string? UtmContent { get; set; }
        public string? MetaCampaignId { get; set; }
        public string? MetaAdSetId { get; set; }
        public string? MetaAdId { get; set; }
        public string? Fbclid { get; set; }
        public string? SessionId { get; set; }
        public string? VisitorId { get; set; }
        public bool MarketingEmailConsent { get; set; }
        public bool CallTextConsent { get; set; }
        [Required]
        public bool TermsAccepted { get; set; }
        public string? Environment { get; set; }
        public string? Host { get; set; }
        public Guid? AgentTrackingProfileId { get; set; }
        public string? AgentSlug { get; set; }
        /// <summary>Product-specific metadata JSON (e.g. offer key, product type). Passed through from website proxy.</summary>
        public string? MetadataJson { get; set; }
    }

    [HttpPost]
    [IgnoreAntiforgeryToken]
    [EnableRateLimiting("ingest")]
    [RequestSizeLimit(16 * 1024)] // 16 KB — well above any valid lead submission
    public async Task<IActionResult> Submit([FromBody] LeadSubmitRequest req)
    {
        // Extract or generate correlation ID — used in all logs for this submit
        var correlationId = Guid.TryParse(Request.Headers["X-Request-Id"].FirstOrDefault(), out var parsedId)
            ? parsedId
            : Guid.NewGuid();

        // Shared secret check
        var expected = _config["Analytics:SharedSecret"] ?? _config["LeadIngest:SharedSecret"];
        var provided = Request.Headers["X-Shared-Secret"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected, provided, StringComparison.Ordinal))
        {
            _logger.LogWarning("LeadSubmit [{CorrelationId}]: rejected — invalid shared secret", correlationId);
            return Unauthorized(new { error = "invalid_secret" });
        }

        if (_flags.IngestHmacEnabled)
        {
            if (!Guid.TryParse(Request.Headers["X-Request-Id"].FirstOrDefault(), out var requestId))
                return Unauthorized(new { error = "missing_request_id" });
            if (!DateTimeOffset.TryParse(Request.Headers["X-Timestamp"].FirstOrDefault(), out var ts))
                return Unauthorized(new { error = "invalid_timestamp" });

            if (!_signatureValidator.TryValidate(requestId, ts, Request.Headers["X-Signature"].FirstOrDefault(), out var reason))
                return Unauthorized(new { error = reason });
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (!req.TermsAccepted)
        {
            _logger.LogWarning("LeadSubmit [{CorrelationId}]: rejected — terms not accepted", correlationId);
            return BadRequest(new { error = "terms_not_accepted" });
        }

        _logger.LogInformation(
            "LeadSubmit [{CorrelationId}]: request received InterestType={InterestType} SourcePageKey={SourcePageKey} Host={Host}",
            correlationId, req.InterestType, req.SourcePageKey, req.Host);

        _logger.LogInformation(
            "LeadSubmit [{CorrelationId}]: resolving attribution slug={Slug} profileId={Id}",
            correlationId, req.AgentSlug, req.AgentTrackingProfileId);

        var resolved = await _resolver.ResolveAsync(req.AgentSlug, req.AgentTrackingProfileId, HttpContext.RequestAborted);
        if (!resolved.Found)
        {
            _logger.LogInformation(
                "LeadSubmit [{CorrelationId}]: attribution not found for slug={Slug} id={Id} — using founder fallback",
                correlationId, req.AgentSlug, req.AgentTrackingProfileId);
        }
        else
        {
            _logger.LogInformation(
                "LeadSubmit [{CorrelationId}]: attribution resolved to profileId={AttributedId} slug={Slug}",
                correlationId, resolved.Profile.Id, resolved.CanonicalSlug);
        }

        var now = DateTime.UtcNow;
        var requestHost = string.IsNullOrWhiteSpace(req.Host) ? Request.Host.ToString() : req.Host;
        var lead = new WebsiteLead
        {
            LeadId = Guid.NewGuid(),
            FirstName = req.FirstName.Trim(),
            LastName = string.IsNullOrWhiteSpace(req.LastName) ? null : req.LastName.Trim(),
            Email = req.Email.Trim(),
            Phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim(),
            PreferredContactMethod = string.IsNullOrWhiteSpace(req.PreferredContactMethod) ? null : req.PreferredContactMethod.Trim(),
            InterestType = req.InterestType.Trim(),
            Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(),
            SourcePageKey = string.IsNullOrWhiteSpace(req.SourcePageKey) ? null : req.SourcePageKey.Trim(),
            SourceCtaKey = string.IsNullOrWhiteSpace(req.SourceCtaKey) ? null : req.SourceCtaKey.Trim(),
            UtmSource = string.IsNullOrWhiteSpace(req.UtmSource) ? null : req.UtmSource.Trim(),
            UtmMedium = string.IsNullOrWhiteSpace(req.UtmMedium) ? null : req.UtmMedium.Trim(),
            UtmCampaign = string.IsNullOrWhiteSpace(req.UtmCampaign) ? null : req.UtmCampaign.Trim(),
            UtmId = string.IsNullOrWhiteSpace(req.UtmId) ? null : req.UtmId.Trim(),
            MetaCampaignId = string.IsNullOrWhiteSpace(req.MetaCampaignId) ? null : req.MetaCampaignId.Trim(),
            MetaAdSetId = string.IsNullOrWhiteSpace(req.MetaAdSetId) ? null : req.MetaAdSetId.Trim(),
            MetaAdId = string.IsNullOrWhiteSpace(req.MetaAdId) ? null : req.MetaAdId.Trim(),
            Fbclid = string.IsNullOrWhiteSpace(req.Fbclid) ? null : req.Fbclid.Trim(),
            SessionId = string.IsNullOrWhiteSpace(req.SessionId) ? null : req.SessionId.Trim(),
            VisitorId = string.IsNullOrWhiteSpace(req.VisitorId) ? null : req.VisitorId.Trim(),
            MarketingEmailConsent = req.MarketingEmailConsent,
            CallTextConsent = req.CallTextConsent && !string.IsNullOrWhiteSpace(req.Phone),
            TermsAccepted = req.TermsAccepted,
            IsInternal = FounderGuard.IsFounder(User) || WebsiteLeadCaptureSafety.ShouldMarkAsInternalTest(requestHost),
            Environment = ResolveEnvironment(req.Environment),
            Host = requestHost,
            CreatedUtc = now,
            Status = "New",
            AgentTrackingProfileId = resolved.Found ? resolved.Profile.Id : null,
            AgentSlug = resolved.Found ? resolved.CanonicalSlug : null,
            MetadataJson = string.IsNullOrWhiteSpace(req.MetadataJson) ? null : req.MetadataJson.Trim()
        };

        _db.WebsiteLeads.Add(lead);
        await _db.SaveChangesAsync();
        _logger.LogInformation(
            "LeadSubmit [{CorrelationId}]: WebsiteLead {LeadId} saved attributedTo={AttributedId}",
            correlationId, lead.LeadId, lead.AgentTrackingProfileId);

        // ── Analytics event (server-side lead submission record) ──────────────────
        try
        {
            var evt = new AnalyticsEvent
            {
                EventId    = Guid.NewGuid(),
                EventType  = "website_lead_submitted",
                PageKey    = lead.SourcePageKey,
                FormKey    = lead.SourcePageKey,
                QuoteType  = lead.InterestType,
                SessionId  = lead.SessionId,
                VisitorId  = lead.VisitorId,
                UtmSource  = lead.UtmSource,
                UtmMedium  = lead.UtmMedium,
                UtmCampaign= lead.UtmCampaign,
                UtmId      = lead.UtmId,
                Fbclid     = lead.Fbclid,
                MetaCampaignId = lead.MetaCampaignId,
                MetaAdSetId = lead.MetaAdSetId,
                MetaAdId = lead.MetaAdId,
                AgentTrackingProfileId = lead.AgentTrackingProfileId,
                AgentSlug  = lead.AgentSlug,
                Environment= lead.Environment,
                Host       = lead.Host,
                EventUtc   = now,
                ReceivedUtc= now,
                MetadataJson = MetaSignalSingleTruthPolicy.BuildMetadataJson(
                    eventName: "website_lead_submitted",
                    leadId: lead.LeadId,
                    sessionId: lead.SessionId,
                    payload: new
                    {
                        LeadId = lead.LeadId,
                        CorrelationId = correlationId
                    },
                    isBrowserSignal: false,
                    isServerAuthority: false,
                    metaServerAuthorityEligible: true,
                    metaSingleTruthDispatchEligible: false,
                    metaPipelineOrigin: "lead_submit_controller")
            };
            _db.AnalyticsEvents.Add(evt);
            await _db.SaveChangesAsync();
            _logger.LogInformation(
                "LeadSubmit [{CorrelationId}]: analytics event {EventId} written for lead {LeadId}",
                correlationId, evt.EventId, lead.LeadId);
        }
        catch (Exception analyticsEx)
        {
            _logger.LogError(analyticsEx,
                "LeadSubmit [{CorrelationId}]: analytics event write failed for lead {LeadId} — lead is saved, continuing",
                correlationId, lead.LeadId);
        }

        // Determine recipient: agent if resolved, otherwise founder
        var recipient = resolved.Found && !string.IsNullOrWhiteSpace(resolved.Profile.AgentUpn)
            ? resolved.Profile.AgentUpn
            : _founderUpn;

        // Notify agent (or founder fallback)
        var subject = $"New website lead: {lead.FirstName} {lead.LastName}".Trim();

        var createdLocal = TimeZoneInfo.ConvertTimeFromUtc(lead.CreatedUtc, TimeZoneInfo.Local).ToString("MM/dd/yyyy hh:mm tt");

        // Plain text fallback (no session/visitor)
        var textBody = $@"A new website lead was captured.

Name: {lead.FirstName} {lead.LastName}
Email: {lead.Email}
Phone: {lead.Phone}
Interest: {lead.InterestType}
Source: {lead.SourcePageKey}
Created: {createdLocal}
Contact Authorization: {(lead.MarketingEmailConsent ? "Yes" : "No")}
Terms Accepted: {(lead.TermsAccepted ? "Yes" : "No")}
Notes: {lead.Notes}";

        // Styled HTML contact card (dark navy + gold)
        var htmlBody = $@"
<!DOCTYPE html>
<html>
<body style=""margin:0;padding:0;background:#f4f4f6;font-family:Arial,sans-serif;"">
  <div style=""width:100%;padding:24px 12px;"">
    <div style=""max-width:640px;margin:0 auto;background:#0f172a;border:1px solid #b08d57;border-radius:14px;color:#f9fafb;box-shadow:0 16px 38px rgba(0,0,0,0.28);overflow:hidden;"">
      <div style=""background:#0b1326;padding:14px 18px;border-bottom:1px solid #b08d57;color:#f3c980;font-weight:700;letter-spacing:0.5px;font-size:15px;"">
        New Website Lead
      </div>
      <div style=""padding:18px 20px 22px;"">
        <div style=""margin-bottom:10px;"">
          <div style=""color:#d1b075;font-size:12px;text-transform:uppercase;letter-spacing:0.6px;"">Name</div>
          <div style=""font-size:15px;font-weight:700;color:#f9fafb;"">{lead.FirstName} {lead.LastName}</div>
        </div>
        <div style=""margin-bottom:10px;"">
          <div style=""color:#d1b075;font-size:12px;text-transform:uppercase;letter-spacing:0.6px;"">Email</div>
          <div style=""font-size:15px;font-weight:700;color:#f9fafb;"">{lead.Email}</div>
        </div>
        <div style=""margin-bottom:10px;"">
          <div style=""color:#d1b075;font-size:12px;text-transform:uppercase;letter-spacing:0.6px;"">Phone</div>
          <div style=""font-size:15px;font-weight:700;color:#f9fafb;"">{lead.Phone}</div>
        </div>
        <div style=""margin-bottom:10px;"">
          <div style=""color:#d1b075;font-size:12px;text-transform:uppercase;letter-spacing:0.6px;"">Interest</div>
          <div style=""font-size:15px;font-weight:700;color:#f9fafb;"">{lead.InterestType}</div>
        </div>
        <div style=""margin-bottom:10px;"">
          <div style=""color:#d1b075;font-size:12px;text-transform:uppercase;letter-spacing:0.6px;"">Source</div>
          <div style=""font-size:15px;font-weight:700;color:#f9fafb;"">{lead.SourcePageKey}</div>
        </div>
        <div style=""margin-bottom:10px;"">
          <div style=""color:#d1b075;font-size:12px;text-transform:uppercase;letter-spacing:0.6px;"">Created</div>
          <div style=""font-size:15px;font-weight:700;color:#f9fafb;"">{createdLocal}</div>
        </div>
        <div style=""margin-bottom:10px;"">
          <div style=""color:#d1b075;font-size:12px;text-transform:uppercase;letter-spacing:0.6px;"">Contact Authorization</div>
          <div style=""font-size:15px;font-weight:700;color:#f9fafb;"">{(lead.MarketingEmailConsent ? "Yes" : "No")}</div>
        </div>
        <div style=""margin-bottom:10px;"">
          <div style=""color:#d1b075;font-size:12px;text-transform:uppercase;letter-spacing:0.6px;"">Terms Accepted</div>
          <div style=""font-size:15px;font-weight:700;color:#f9fafb;"">{(lead.TermsAccepted ? "Yes" : "No")}</div>
        </div>
        <div style=""margin-bottom:6px;"">
          <div style=""color:#d1b075;font-size:12px;text-transform:uppercase;letter-spacing:0.6px;"">Notes</div>
          <div style=""font-size:15px;font-weight:700;color:#f9fafb;white-space:pre-wrap;"">{lead.Notes}</div>
        </div>
      </div>
    </div>
  </div>
</body>
</html>";


        static string H(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

        static string NormalizeProspectQuoteType(string? value)
        {
            var raw = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (raw.Contains("mortgage")) return "mortgage";
            if (raw.Contains("final")) return "finalexpense";
            if (raw.Contains("whole")) return "wholelife";
            if (raw.Contains("term")) return "term";
            if (raw.Contains("iul") || raw.Contains("indexed")) return "iul";
            return "life";
        }

        static (string Title, string Copy, string Diagnosis, string Timing, string NextStep, string BookingTitle) BuildProspectResultCopy(string? interestType)
        {
            return NormalizeProspectQuoteType(interestType) switch
            {
                "term" => (
                    "Your term life estimate is worth confirming.",
                    "Your answers point to the years your family would feel the pressure first: income, mortgage, bills, children, and daily stability. The estimate is saved. The review is where the range gets checked against real carrier fit, monthly comfort, and the term length that actually makes sense.",
                    "A defined protection window may be needed around income, bills, and major family responsibilities.",
                    "Age, health, and underwriting drive the real options. Waiting usually does not create better pricing flexibility.",
                    "Confirm coverage amount, monthly fit, term length, and whether this is the right first direction.",
                    "Choose a time to confirm the term fit"
                ),
                "wholelife" => (
                    "Your whole life estimate is worth confirming.",
                    "Your answers point toward protection designed to stay in place long term. The estimate is saved, but whole life should not be judged from a quick number alone. The structure, monthly fit, and long-term purpose need to be reviewed before deciding.",
                    "You may be looking for lifelong protection, stable structure, and lasting family support.",
                    "Permanent coverage is sensitive to age, health, design, and funding. The right structure matters more than just seeing a number.",
                    "Confirm whether whole life is truly the right fit, how it should be structured, and whether alternatives should be compared.",
                    "Choose a time to review the long-term fit"
                ),
                "finalexpense" => (
                    "Your final expense estimate is worth confirming.",
                    "Your answers point to a simple but serious goal: keeping loved ones from carrying funeral costs, final bills, and added pressure. The estimate is saved. The review confirms whether the amount and monthly fit can actually protect your family from scrambling.",
                    "Loved ones could be left handling final costs without a clear plan in place.",
                    "Final expense pricing and eligibility can tighten as age and health change, so waiting rarely creates more options.",
                    "Confirm the benefit amount, monthly fit, and whether this gives your family enough breathing room.",
                    "Choose a time to confirm the family relief plan"
                ),
                "mortgage" => (
                    "Your mortgage protection estimate is worth confirming.",
                    "Your answers point to the home, the payment, and the people who depend on both. The estimate is saved. The review confirms whether this range actually lines up with the mortgage pressure and household stability you want protected.",
                    "The home may need a protection layer if income suddenly disappears.",
                    "The mortgage does not pause because life changes. The longer this sits, the easier it is for the same home risk to stay uncovered.",
                    "Confirm the mortgage balance, monthly fit, household need, and best protection structure.",
                    "Choose a time to confirm the home-protection fit"
                ),
                "iul" => (
                    "Your IUL estimate is worth confirming.",
                    "Your answers point toward long-term protection, flexibility, and legacy planning. The estimate is saved, but IUL depends heavily on structure. The review is where the design, funding, expectations, and alternatives need to be checked.",
                    "You may be exploring protection with long-term flexibility and future planning room.",
                    "IUL depends on age, health, funding, policy design, and long-term assumptions. Guessing here creates bad decisions.",
                    "Confirm whether IUL is appropriate, how it would be structured, and what alternatives should be compared.",
                    "Choose a time to review the long-term structure"
                ),
                _ => (
                    "Your personalized estimate is worth confirming.",
                    "Your answers created a starting range and a likely protection path. The estimate is saved. The review confirms whether it actually fits your family, budget, carrier options, and next best step before the details get pushed aside.",
                    "There may be a real protection gap worth reviewing.",
                    "Rates and eligibility are tied to age and health, so reviewing sooner usually gives more flexibility.",
                    "Confirm the coverage amount, monthly fit, carrier direction, and whether this should move forward.",
                    "Choose a time to confirm the fit"
                )
            };
        }

        static string BuildProspectResultsEmailText(WebsiteLead lead, string agentName, string bookingUrl)
        {
            var result = BuildProspectResultCopy(lead.InterestType);

            return $@"{result.Title}

Hi {lead.FirstName},

{result.Copy}

Estimate saved
25+ carrier comparison
No obligation

What surfaced:
{result.Diagnosis}

Why timing matters:
{result.Timing}

What the review confirms:
{result.NextStep}

{(string.IsNullOrWhiteSpace(bookingUrl) ? $"A licensed agent will help review the next step." : $"Choose a time here: {bookingUrl}")}

— {agentName}";
        }

        static string BuildProspectResultsEmailHtml(WebsiteLead lead, string agentName, string bookingUrl)
        {
            var result = BuildProspectResultCopy(lead.InterestType);
            var hasBookingUrl = !string.IsNullOrWhiteSpace(bookingUrl);

            var bookingButton = hasBookingUrl
                ? $@"<a href=""{H(bookingUrl)}"" style=""display:block;text-align:center;background:linear-gradient(180deg,#f3d688 0%,#d9ad4f 100%);color:#1f1605;text-decoration:none;font-weight:900;border-radius:14px;padding:15px 18px;margin-top:14px;border:1px solid #f6d98d;"">Choose My Review Time</a>"
                : @"<div style=""margin-top:14px;padding:13px 14px;border-radius:14px;border:1px solid rgba(243,214,136,.28);background:#061832;color:#f8fafc;font-weight:800;text-align:center;"">A licensed agent will help review the next step.</div>";

            return $@"
<!DOCTYPE html>
<html>
<body style=""margin:0;padding:0;background:#f4f4f6;font-family:Arial,Helvetica,sans-serif;"">
  <div style=""width:100%;padding:24px 12px;background:#f4f4f6;"">
    <div style=""max-width:680px;margin:0 auto;background:#071d3d;border:1px solid rgba(199,153,49,.55);border-radius:22px;color:#f8fafc;overflow:hidden;box-shadow:0 18px 42px rgba(2,12,27,.28);"">
      <div style=""background:#061832;padding:18px 20px;border-bottom:1px solid rgba(243,214,136,.24);"">
        <div style=""color:#f3d688;font-size:12px;font-weight:900;letter-spacing:.12em;text-transform:uppercase;"">Estimate saved</div>
        <div style=""color:#fff8e7;font-size:26px;line-height:1.08;font-weight:900;margin-top:8px;"">{H(result.Title)}</div>
        <div style=""color:rgba(248,250,252,.84);font-size:15px;line-height:1.45;font-weight:600;margin-top:10px;"">{H(result.Copy)}</div>
      </div>

      <div style=""padding:18px 20px 20px;"">
        <div style=""margin-bottom:14px;"">
          <span style=""display:inline-block;background:#041020;border:1px solid rgba(243,214,136,.30);border-radius:999px;color:#fff6d8;font-size:12px;font-weight:900;padding:7px 10px;margin:0 5px 6px 0;"">Estimate saved</span>
          <span style=""display:inline-block;background:#041020;border:1px solid rgba(243,214,136,.30);border-radius:999px;color:#fff6d8;font-size:12px;font-weight:900;padding:7px 10px;margin:0 5px 6px 0;"">25+ carriers</span>
          <span style=""display:inline-block;background:#041020;border:1px solid rgba(243,214,136,.30);border-radius:999px;color:#fff6d8;font-size:12px;font-weight:900;padding:7px 10px;margin:0 5px 6px 0;"">No obligation</span>
        </div>

        <table width=""100%"" cellpadding=""0"" cellspacing=""0"" role=""presentation"" style=""border-collapse:collapse;margin-bottom:14px;"">
          <tr>
            <td style=""width:50%;padding:0 5px 0 0;vertical-align:top;"">
              <div style=""border:1px solid rgba(199,153,49,.25);background:#08254d;border-radius:16px;padding:13px 14px;"">
                <div style=""color:#f3d688;font-size:11px;font-weight:900;letter-spacing:.08em;text-transform:uppercase;margin-bottom:6px;"">What surfaced</div>
                <div style=""color:#f8fafc;font-size:14px;line-height:1.35;font-weight:700;"">{H(result.Diagnosis)}</div>
              </div>
            </td>
            <td style=""width:50%;padding:0 0 0 5px;vertical-align:top;"">
              <div style=""border:1px solid rgba(243,214,136,.40);background:#082a58;border-radius:16px;padding:13px 14px;"">
                <div style=""color:#f3d688;font-size:11px;font-weight:900;letter-spacing:.08em;text-transform:uppercase;margin-bottom:6px;"">Why timing matters</div>
                <div style=""color:#f8fafc;font-size:14px;line-height:1.35;font-weight:700;"">{H(result.Timing)}</div>
              </div>
            </td>
          </tr>
        </table>

        <div style=""border:1px solid rgba(243,214,136,.38);background:#061832;border-radius:18px;padding:15px 16px;margin-bottom:14px;"">
          <div style=""color:#f3d688;font-size:11px;font-weight:900;letter-spacing:.08em;text-transform:uppercase;margin-bottom:6px;"">What the review confirms</div>
          <div style=""color:#fff8e7;font-size:17px;line-height:1.22;font-weight:900;margin-bottom:7px;"">{H(agentName)} helps turn this estimate into a clear decision.</div>
          <div style=""color:rgba(248,250,252,.84);font-size:14px;line-height:1.4;font-weight:650;"">{H(result.NextStep)}</div>
          <div style=""margin-top:11px;"">
            <span style=""display:inline-block;border:1px solid rgba(243,214,136,.22);border-radius:12px;background:#041020;color:#f8fafc;font-size:12px;font-weight:800;padding:8px 9px;margin:0 5px 6px 0;"">Coverage amount</span>
            <span style=""display:inline-block;border:1px solid rgba(243,214,136,.22);border-radius:12px;background:#041020;color:#f8fafc;font-size:12px;font-weight:800;padding:8px 9px;margin:0 5px 6px 0;"">Monthly fit</span>
            <span style=""display:inline-block;border:1px solid rgba(243,214,136,.22);border-radius:12px;background:#041020;color:#f8fafc;font-size:12px;font-weight:800;padding:8px 9px;margin:0 5px 6px 0;"">Carrier direction</span>
          </div>
        </div>

        <div style=""border:1px solid rgba(243,214,136,.40);background:#092955;border-radius:18px;padding:15px 16px;"">
          <div style=""color:#f3d688;font-size:11px;font-weight:900;letter-spacing:.08em;text-transform:uppercase;margin-bottom:6px;"">Finish the review</div>
          <div style=""color:#fff8e7;font-size:18px;line-height:1.2;font-weight:900;margin-bottom:7px;"">{H(result.BookingTitle)}</div>
          <div style=""color:rgba(248,250,252,.84);font-size:14px;line-height:1.4;font-weight:650;"">Pick the soonest time that works so {H(agentName)} can compare the range, confirm carrier fit, and help you decide the next step.</div>
          {bookingButton}
        </div>

        <div style=""color:rgba(248,250,252,.62);font-size:12px;line-height:1.45;margin-top:16px;text-align:center;"">
          This is not a final underwriting offer. Final eligibility, pricing, and coverage depend on carrier review, age, health, underwriting, and state availability.
        </div>
      </div>
    </div>
  </div>
</body>
</html>";
        }

        var emailSent = false;
        try
        {
            emailSent = await _emailSender.TrySendAsync(
                recipient,
                subject,
                htmlBody,
                textBody);
            _logger.LogInformation(
                "LeadSubmit [{CorrelationId}]: email sent={EmailSent} for lead {LeadId} to {Recipient} attributedId={AttributedId}",
                correlationId, emailSent, lead.LeadId, recipient, lead.AgentTrackingProfileId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "LeadSubmit [{CorrelationId}]: email send failed for lead {LeadId} to {Recipient} — lead is saved, continuing",
                correlationId, lead.LeadId, recipient);
            // lead is already persisted; do not fail the request
        }

        // Prospect-facing result email: mirrors the post-submit results/calendar page in email-safe HTML.
        var prospectEmailSent = false;
        var prospectEmail = (lead.Email ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(prospectEmail))
        {
            var prospectAgentName = resolved.Found && !string.IsNullOrWhiteSpace(resolved.Profile.DisplayName)
                ? resolved.Profile.DisplayName.Trim()
                : "your licensed agent";

            var bookingUrl =
                (_config["PublicBooking:FallbackUrl"] ??
                 _config["Booking:FallbackUrl"] ??
                 _config["Booking:PublicUrl"] ??
                 string.Empty).Trim();

            var prospectSubject = "Your protection estimate is saved";
            var prospectTextBody = BuildProspectResultsEmailText(lead, prospectAgentName, bookingUrl);
            var prospectHtmlBody = BuildProspectResultsEmailHtml(lead, prospectAgentName, bookingUrl);

            try
            {
                var prospectFromEmail = (resolved.Found && !string.IsNullOrWhiteSpace(resolved.Profile.AgentUpn))
                    ? resolved.Profile.AgentUpn.Trim()
                    : "connect@mylegnd.com";

                var prospectFromName = (resolved.Found && !string.IsNullOrWhiteSpace(resolved.Profile.DisplayName))
                    ? resolved.Profile.DisplayName.Trim()
                    : "Legend";

                prospectEmailSent = await _emailSender.TrySendAsync(
                    prospectEmail,
                    prospectSubject,
                    prospectHtmlBody,
                    prospectTextBody,
                    prospectFromEmail,
                    prospectFromName,
                    prospectFromEmail);

                _logger.LogInformation(
                    "LeadSubmit [{CorrelationId}]: prospect result email sent={EmailSent} for lead {LeadId} to {ProspectEmail}",
                    correlationId,
                    prospectEmailSent,
                    lead.LeadId,
                    prospectEmail);
            }
            catch (Exception prospectEmailEx)
            {
                _logger.LogError(
                    prospectEmailEx,
                    "LeadSubmit [{CorrelationId}]: prospect result email failed for lead {LeadId} to {ProspectEmail} — lead is saved, continuing",
                    correlationId,
                    lead.LeadId,
                    prospectEmail);
            }
        }



        return Ok(new
        {
            status = "ok",
            leadId = lead.LeadId,
            correlationId,
            attributedAgentTrackingProfileId = lead.AgentTrackingProfileId,
            recipient = recipient,
            emailSent
        });
    }

    private string builderEnvironment() =>
        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";

    private string ResolveEnvironment(string? incoming)
    {
        var raw = string.IsNullOrWhiteSpace(incoming) ? builderEnvironment() : incoming!;
        var normalized = raw.Trim();
        if (normalized.StartsWith("prod", StringComparison.OrdinalIgnoreCase)) return "production";
        if (normalized.StartsWith("dev", StringComparison.OrdinalIgnoreCase)) return "development";
        return normalized;
    }

    [HttpOptions]
    [IgnoreAntiforgeryToken]
    public IActionResult Options() => Ok();
}
