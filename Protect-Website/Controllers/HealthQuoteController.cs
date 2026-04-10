using Microsoft.AspNetCore.Mvc;
using Domain.Entities;
using Infrastructure.Data;
using Protect_Website.Models;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Azure.Identity;
using System.Text.Json;
using ProtectWebsite.Services.Tracking;

namespace Protect_Website.Controllers
{
    [Route("Quote")]
    public class HealthQuoteController : Controller
    {
        private readonly string tenantId;
        private readonly string clientId;
        private readonly string clientSecret;
        private readonly string senderEmail;
        private readonly string recipientEmail;
        private readonly string websiteName;
        private readonly AgentTrackingResolver _resolver;
        private readonly MasterAppDbContext _db;
        private readonly ILogger<HealthQuoteController> _logger;

        public HealthQuoteController(IConfiguration configuration, AgentTrackingResolver resolver,
            MasterAppDbContext db, ILogger<HealthQuoteController> logger)
        {
            tenantId = configuration["AzureAd:TenantId"]!;
            clientId = configuration["AzureAd:ClientId"]!;
            clientSecret = configuration["AzureAd:ClientSecret"]!;
            senderEmail = configuration["Contact:SenderEmail"] ?? "connect@mylegnd.com";
            recipientEmail = configuration["Contact:RecipientEmail"]!;
            websiteName = configuration["Contact:WebsiteName"] ?? "Legend Legacy Protection";
            _resolver = resolver;
            _db = db;
            _logger = logger;
        }

        // ===================== GET =====================
        [HttpGet("Health")]
        public IActionResult HealthQuote()
        {
            return View("~/Views/Quote/Health.cshtml");
        }
[HttpPost("Health")]
public async Task<IActionResult> SubmitHealthQuote(HealthQuoteFormModel model)
{
    if (!ModelState.IsValid)
        return View("~/Views/Quote/Health.cshtml", model);

    var (leadRecipientEmail, agentProfileId, agentSlug) = await ResolveLeadContextAsync();

    try
    {
        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        var graphClient = new GraphServiceClient(credential);

        // ===================== BUILD EMAIL BODY =====================
        string emailBody = $@"
<h2>Health Insurance Quote Lead</h2>

<h3>Personal Information</h3>
<p><strong>Name:</strong> {model.FirstName} {model.LastName}</p>
<p><strong>Age:</strong> {model.Age}</p>
<p><strong>Email:</strong> {model.Email}</p>
<p><strong>Phone:</strong> {model.Phone}</p>

<hr />

<h3>Coverage Needs</h3>
<p><strong>Household Size:</strong> {model.HouseholdSize}</p>
<p><strong>Primary Concern:</strong> {model.PrimaryConcern}</p>

<hr />

<h3>Contact Preferences</h3>
<p><strong>Preferred Method:</strong> {model.ContactMethod}</p>
<p><strong>Best Time:</strong> {model.BestTimeToContact}</p>

<hr />

<h3>Disclaimer</h3>
<p><strong>Acknowledged Disclaimer:</strong> {(model.AcknowledgedDisclaimer ? "Acknowledged" : "Not Acknowledged")}</p>
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

        // ===================== CREATE GRAPH MESSAGE =====================
        var message = new Message
        {
            Subject = $"[HEALTH QUOTE] New Lead | {model.FirstName} {model.LastName}",
            Body = new ItemBody
            {
                ContentType = BodyType.Html,
                Content = emailBody
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

        var requestBody = new SendMailPostRequestBody
        {
            Message = message,
            SaveToSentItems = true
        };

        await graphClient.Users[senderEmail].SendMail.PostAsync(requestBody);

        // ── Lead persistence (separate try/catch — email success is preserved) ──────
        try
        {
            var lead = new WebsiteLead
            {
                LeadId        = Guid.NewGuid(),
                FirstName     = model.FirstName?.Trim() ?? "",
                LastName      = string.IsNullOrWhiteSpace(model.LastName) ? null : model.LastName.Trim(),
                Email         = model.Email?.Trim() ?? "",
                Phone         = string.IsNullOrWhiteSpace(model.Phone) ? null : model.Phone.Trim(),
                InterestType  = "health_insurance",
                SourcePageKey = "quote_health",
                UtmSource     = string.IsNullOrWhiteSpace(model.UtmSource)   ? null : model.UtmSource.Trim(),
                UtmMedium     = string.IsNullOrWhiteSpace(model.UtmMedium)   ? null : model.UtmMedium.Trim(),
                UtmCampaign   = string.IsNullOrWhiteSpace(model.UtmCampaign) ? null : model.UtmCampaign.Trim(),
                SessionId     = string.IsNullOrWhiteSpace(model.SessionId)   ? null : model.SessionId.Trim(),
                VisitorId     = string.IsNullOrWhiteSpace(model.VisitorId)   ? null : model.VisitorId.Trim(),
                MarketingEmailConsent = model.AcknowledgedDisclaimer,
                CallTextConsent = model.AcknowledgedDisclaimer && !string.IsNullOrWhiteSpace(model.Phone),
                TermsAccepted = true,
                Host          = Request?.Host.ToString(),
                Environment   = "production",
                CreatedUtc    = DateTime.UtcNow,
                Status        = "New",
                AgentTrackingProfileId = agentProfileId,
                AgentSlug     = agentSlug,
                MetadataJson  = JsonSerializer.Serialize(new
                {
                    HouseholdSize  = model.HouseholdSize,
                    PrimaryConcern = model.PrimaryConcern,
                    CoverageType   = model.CoverageType,
                    Fbclid         = model.Fbclid,
                    UtmTerm        = model.UtmTerm,
                    UtmContent     = model.UtmContent,
                    ReferrerUrl    = model.ReferrerUrl,
                    LandingPageUrl = model.LandingPageUrl,
                })
            };
            _db.WebsiteLeads.Add(lead);
            await _db.SaveChangesAsync();
            _logger.LogInformation("HealthQuote: lead {LeadId} persisted for {Email}", lead.LeadId, lead.Email);
        }
        catch (Exception persistEx)
        {
            _logger.LogError(persistEx, "HealthQuote: lead persistence failed for {Email}", model.Email);
        }

        // ===================== SUCCESS REDIRECT =====================
        TempData["QuoteType"] = "Health";
        return RedirectToAction("Index", "ThankYou");
    }
    catch (Exception ex)
    {
        ModelState.AddModelError("", $"Failed to send lead: {ex.Message}");
        return View("~/Views/Quote/Health.cshtml", model);
    }
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
    }
}