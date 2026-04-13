using Microsoft.AspNetCore.Mvc;
using Domain.Entities;
using Infrastructure.Data;
using Protect_Website.Models;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Azure.Identity;
using System.Text.Json;
using ProtectWebsite.Services;
using ProtectWebsite.Services.Tracking;

namespace Protect_Website.Controllers
{
    [Route("Quote")]
    public class DisabilityQuoteController : Controller
    {
        private readonly string tenantId;
        private readonly string clientId;
        private readonly string clientSecret;
        private readonly string senderEmail;
        private readonly string recipientEmail;
        private readonly AgentTrackingResolver _resolver;
        private readonly MasterAppDbContext _db;
        private readonly ILogger<DisabilityQuoteController> _logger;

        public DisabilityQuoteController(IConfiguration configuration, AgentTrackingResolver resolver,
            MasterAppDbContext db, ILogger<DisabilityQuoteController> logger)
        {
            tenantId = configuration["AzureAd:TenantId"]!;
            clientId = configuration["AzureAd:ClientId"]!;
            clientSecret = configuration["AzureAd:ClientSecret"]!;
            senderEmail = configuration["Contact:SenderEmail"] ?? "connect@mylegnd.com";
            recipientEmail = configuration["Contact:RecipientEmail"]!;
            _resolver = resolver;
            _db = db;
            _logger = logger;
        }

        // GET: /Quote/Disability
        [HttpGet("Disability")]
        public IActionResult DisabilityQuote()
        {
            return View("~/Views/Quote/Disability.cshtml", new DisabilityQuoteFormModel());
        }

        // POST: /Quote/Disability
        [HttpPost("Disability")]
        public async Task<IActionResult> SubmitDisabilityQuote(DisabilityQuoteFormModel model)
        {
            if (!ModelState.IsValid)
                return IsAjax() ? BadRequest(new { error = "Invalid form data" })
                                : View("~/Views/Quote/Disability.cshtml", model);

            var correlationId = Guid.NewGuid();
            _logger.LogInformation(
                "DisabilityQuote [{CorrelationId}]: request received Email={Email}",
                correlationId, model.Email);

            var (leadRecipientEmail, agentProfileId, agentSlug) = await ResolveLeadContextAsync();
            var isAgentContext = IsAgentContext();
            _logger.LogInformation(
                "DisabilityQuote [{CorrelationId}]: attribution resolved AgentSlug={Slug} ProfileId={ProfileId} Recipient={Recipient}",
                correlationId, agentSlug, agentProfileId, leadRecipientEmail);

            // ── 1. Persist lead FIRST ─────────────────────────────────────────────
            WebsiteLead lead;
            try
            {
                var now = DateTime.UtcNow;
                lead = new WebsiteLead
                {
                    LeadId        = Guid.NewGuid(),
                    FirstName     = model.FirstName?.Trim() ?? "",
                    LastName      = string.IsNullOrWhiteSpace(model.LastName) ? null : model.LastName.Trim(),
                    Email         = model.Email?.Trim() ?? "",
                    Phone         = string.IsNullOrWhiteSpace(model.Phone) ? null : model.Phone.Trim(),
                    InterestType  = "disability_insurance",
                    SourcePageKey = "quote_disability",
                    UtmSource     = string.IsNullOrWhiteSpace(model.UtmSource)   ? null : model.UtmSource.Trim(),
                    UtmMedium     = string.IsNullOrWhiteSpace(model.UtmMedium)   ? null : model.UtmMedium.Trim(),
                    UtmCampaign   = string.IsNullOrWhiteSpace(model.UtmCampaign) ? null : model.UtmCampaign.Trim(),
                    Fbclid        = string.IsNullOrWhiteSpace(model.Fbclid)      ? null : model.Fbclid.Trim(),
                    SessionId     = string.IsNullOrWhiteSpace(model.SessionId)   ? null : model.SessionId.Trim(),
                    VisitorId     = string.IsNullOrWhiteSpace(model.VisitorId)   ? null : model.VisitorId.Trim(),
                    MarketingEmailConsent = model.AcknowledgedDisclaimer,
                    CallTextConsent = model.AcknowledgedDisclaimer && !string.IsNullOrWhiteSpace(model.Phone),
                    TermsAccepted = true,
                    Host          = Request?.Host.ToString(),
                    Environment   = EnvironmentLabelResolver.Resolve(),
                    CreatedUtc    = now,
                    Status        = "New",
                    AgentTrackingProfileId = agentProfileId,
                    AgentSlug     = agentSlug,
                    MetadataJson  = JsonSerializer.Serialize(new
                    {
                        EmploymentType = model.EmploymentType,
                        Occupation     = model.Occupation,
                        Fbclid         = model.Fbclid,
                        UtmTerm        = model.UtmTerm,
                        UtmContent     = model.UtmContent,
                        ReferrerUrl    = model.ReferrerUrl,
                        LandingPageUrl = model.LandingPageUrl,
                        CorrelationId  = correlationId,
                    })
                };
                _db.WebsiteLeads.Add(lead);
                await _db.SaveChangesAsync();
                _logger.LogInformation(
                    "DisabilityQuote [{CorrelationId}]: WebsiteLead {LeadId} saved",
                    correlationId, lead.LeadId);
            }
            catch (Exception persistEx)
            {
                _logger.LogError(persistEx,
                    "DisabilityQuote [{CorrelationId}]: lead persistence failed for {Email}",
                    correlationId, model.Email);
                return IsAjax()
                    ? StatusCode(500, new { error = "Failed to save lead", detail = persistEx.Message })
                    : View("~/Views/Quote/Disability.cshtml", model);
            }

            // ── 2. Send email ─────────────────────────────────────────────────────
            try
            {
                var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                var graphClient = new GraphServiceClient(credential);

                var emailBody = LeadEmailTemplate.Wrap(
                    $"New Lead — Disability Insurance",
                    new LeadEmailTemplate.RowBuilder()
                        .Row("Name",  $"{model.FirstName} {model.LastName}".Trim())
                        .Row("Age",   model.Age?.ToString())
                        .Row("Email", model.Email)
                        .Row("Phone", model.Phone)
                        .Section("Employment")
                        .Row("Employment Type", model.EmploymentType)
                        .Row("Occupation",      model.Occupation)
                        .Section("Coverage")
                        .Row("Income Protection Priority", model.IncomeProtectionImportance)
                        .Section("Contact Preferences")
                        .Row("Preferred Method", model.ContactMethod)
                        .Row("Best Time",        model.BestTimeToContact)
                        .Section("Authorization")
                        .Row("Disclaimer Acknowledged", LeadEmailTemplate.Bool(model.AcknowledgedDisclaimer))
                        .ToString());

                var message = new Message
                {
                    Subject = $"[DISABILITY QUOTE] New Lead | {model.FirstName} {model.LastName}",
                    Body = new ItemBody { ContentType = BodyType.Html, Content = emailBody },
                    ToRecipients = new List<Recipient>()
                };

                // Send to agent plus founder/owner as fallback
                string? primary = null;
                if (isAgentContext && !string.IsNullOrWhiteSpace(leadRecipientEmail))
                    primary = leadRecipientEmail.Trim();
                else if (!isAgentContext && !string.IsNullOrWhiteSpace(recipientEmail))
                    primary = recipientEmail.Trim();
                else if (!string.IsNullOrWhiteSpace(recipientEmail))
                    primary = recipientEmail.Trim();
                else if (!string.IsNullOrWhiteSpace(senderEmail))
                    primary = senderEmail.Trim();

                if (string.IsNullOrWhiteSpace(primary))
                    throw new InvalidOperationException("No recipient email resolved.");

                message.ToRecipients.Add(new Recipient
                {
                    EmailAddress = new EmailAddress { Address = primary }
                });

        // ===================== SEND EMAIL =====================
        var requestBody = new SendMailPostRequestBody
        {
            Message = message,
            SaveToSentItems = true
        };

        await graphClient.Users[senderEmail].SendMail.PostAsync(requestBody);
        _logger.LogInformation(
            "DisabilityQuote [{CorrelationId}]: email sent to {Recipient} for lead {LeadId}",
            correlationId, leadRecipientEmail, lead.LeadId);
            }
            catch (Exception emailEx)
            {
                _logger.LogError(emailEx,
                    "DisabilityQuote [{CorrelationId}]: email send failed for lead {LeadId} — lead is saved, continuing",
                    correlationId, lead.LeadId);
            }

            // ── 3. Write analytics event ─────────────────────────────────────────
            try
            {
                var evt = new AnalyticsEvent
                {
                    EventId    = Guid.NewGuid(),
                    EventType  = "website_lead_submitted",
                    PageKey    = "quote_disability",
                    FormKey    = "quote_disability_form",
                    QuoteType  = "disability_insurance",
                    SessionId  = lead.SessionId,
                    VisitorId  = lead.VisitorId,
                    UtmSource  = lead.UtmSource,
                    UtmMedium  = lead.UtmMedium,
                    UtmCampaign= lead.UtmCampaign,
                    Fbclid     = lead.Fbclid,
                    AgentTrackingProfileId = lead.AgentTrackingProfileId,
                    AgentSlug  = lead.AgentSlug,
                    Environment= lead.Environment,
                    Host       = lead.Host,
                    EventUtc   = lead.CreatedUtc,
                    ReceivedUtc= DateTime.UtcNow,
                    MetadataJson = JsonSerializer.Serialize(new { LeadId = lead.LeadId, CorrelationId = correlationId })
                };
                _db.AnalyticsEvents.Add(evt);
                await _db.SaveChangesAsync();
                _logger.LogInformation(
                    "DisabilityQuote [{CorrelationId}]: analytics event {EventId} written for lead {LeadId}",
                    correlationId, evt.EventId, lead.LeadId);
            }
            catch (Exception analyticsEx)
            {
                _logger.LogError(analyticsEx,
                    "DisabilityQuote [{CorrelationId}]: analytics event write failed for lead {LeadId} — lead is saved, continuing",
                    correlationId, lead.LeadId);
            }

            TempData["QuoteType"] = "Disability";
            return IsAjax() ? Ok(new { success = true }) : RedirectToAction("Index", "ThankYou");
}

        private async Task<(string RecipientEmail, Guid? AgentProfileId, string? AgentSlug)> ResolveLeadContextAsync()
        {
            if (HttpContext?.Items.TryGetValue("TrackingProfile", out var trackingProfileObj) == true &&
                trackingProfileObj is AgentTrackingProfile trackingProfile &&
                !string.IsNullOrWhiteSpace(trackingProfile.AgentUpn))
            {
                return (trackingProfile.AgentUpn.Trim(), trackingProfile.Id, trackingProfile.Slug);
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
                    return (bySlug.Profile.AgentUpn.Trim(), bySlug.Profile.Id, bySlug.CanonicalSlug);
            }

            return (recipientEmail, null, null);
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

        private bool IsAgentContext()
        {
            string? slug = null;
            var formSlug = Request?.Form["AgentSlug"].ToString();
            if (!string.IsNullOrWhiteSpace(formSlug)) slug = formSlug.Trim();
            if (string.IsNullOrWhiteSpace(slug)) slug = ExtractSlugFromPath(Request?.Path.Value);
            if (string.IsNullOrWhiteSpace(slug)) slug = ExtractSlugFromPath(Request?.Headers["Referer"].ToString());
            return !string.IsNullOrWhiteSpace(slug);
        }

        private bool IsAjax()
        {
            var hdr = Request?.Headers["X-Requested-With"].ToString();
            return !string.IsNullOrWhiteSpace(hdr) &&
                   (hdr.Contains("fetch", StringComparison.OrdinalIgnoreCase) ||
                    hdr.Contains("xmlhttprequest", StringComparison.OrdinalIgnoreCase));
        }
    }
}
