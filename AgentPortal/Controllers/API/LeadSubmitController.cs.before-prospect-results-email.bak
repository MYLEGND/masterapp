using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AgentPortal.Security;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.EntityFrameworkCore;
using AgentPortal.Services;
using Microsoft.AspNetCore.RateLimiting;

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
            IsInternal = FounderGuard.IsFounder(User),
            Environment = ResolveEnvironment(req.Environment),
            Host = string.IsNullOrWhiteSpace(req.Host) ? Request.Host.ToString() : req.Host,
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
                MetadataJson = JsonSerializer.Serialize(new
                {
                    LeadId        = lead.LeadId,
                    CorrelationId = correlationId
                })
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
