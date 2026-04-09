using Microsoft.AspNetCore.Mvc;
using Domain.Entities;
using Protect_Website.Models;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Azure.Identity;
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

        public DisabilityQuoteController(IConfiguration configuration, AgentTrackingResolver resolver)
        {
            tenantId = configuration["AzureAd:TenantId"]!;
            clientId = configuration["AzureAd:ClientId"]!;
            clientSecret = configuration["AzureAd:ClientSecret"]!;
            senderEmail = configuration["Contact:SenderEmail"] ?? "connect@mylegnd.com";
            recipientEmail = configuration["Contact:RecipientEmail"]!;
            _resolver = resolver;
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
                return View("~/Views/Quote/Disability.cshtml", model);

            var leadRecipientEmail = await ResolveLeadRecipientEmailAsync();

            try
            {
                var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                var graphClient = new GraphServiceClient(credential);

                var message = new Message
                {
                    Subject = $"[DISABILITY QUOTE] New Lead | {model.FirstName} {model.LastName}",
                    Body = new ItemBody
                    {
                        ContentType = BodyType.Html,
                        Content = $@"
                            <h2>Disability Insurance Quote Lead</h2>

                            <h3>Personal Info</h3>
                            <p><strong>Name:</strong> {model.FirstName} {model.LastName}</p>
                            <p><strong>Age:</strong> {model.Age}</p>
                            <p><strong>Email:</strong> {model.Email}</p>
                            <p><strong>Phone:</strong> {model.Phone}</p>

                            <hr />

                            <h3>Employment</h3>
                            <p><strong>Employment Type:</strong> {model.EmploymentType}</p>
                            <p><strong>Occupation:</strong> {model.Occupation}</p>

                            <hr />

                            <h3>Coverage Awareness</h3>
                            <p><strong>Income Protection Importance:</strong> {model.IncomeProtectionImportance}</p>

                            <hr />

                            <h3>Contact Preferences</h3>
                            <p><strong>Preferred Method:</strong> {model.ContactMethod}</p>
                            <p><strong>Best Time:</strong> {model.BestTimeToContact}</p>

                                 <hr />

                          
<h3>Disclaimer</h3>
<p><strong>Acknowledged Disclaimer:</strong> {(model.AcknowledgedDisclaimer ? "Acknowledged" : "Not Acknowledged")}</p>
"
                    },
                    ToRecipients = new List<Recipient>()
                };

                // Send to agent plus founder/owner as fallback
                var recipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    leadRecipientEmail,
                    recipientEmail
                };
                foreach (var addr in recipients)
                {
                    message.ToRecipients.Add(new Recipient
                    {
                        EmailAddress = new EmailAddress { Address = addr }
                    });
                }

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

        // ===================== SEND EMAIL =====================
        var requestBody = new SendMailPostRequestBody
        {
            Message = message,
            SaveToSentItems = true
        };

        await graphClient.Users[senderEmail].SendMail.PostAsync(requestBody);

        // ===================== REDIRECT =====================
        TempData["QuoteType"] = "Disability";
        return RedirectToAction("Index", "ThankYou");
    }
    catch (Exception ex)
    {
        ModelState.AddModelError("", $"Failed to send lead: {ex.Message}");
        return View("~/Views/Quote/Disability.cshtml", model);
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
