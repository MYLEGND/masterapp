using Microsoft.AspNetCore.Mvc;
using Domain.Entities;
using Protect_Website.Models;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Azure.Identity;

namespace Protect_Website.Controllers
{
    [Route("Quote")]
    public class DisabilityQuoteController : Controller
    {
        private readonly string tenantId;
        private readonly string clientId;
        private readonly string clientSecret;
        private readonly string recipientEmail;

        public DisabilityQuoteController(IConfiguration configuration)
        {
            tenantId = configuration["AzureAd:TenantId"]!;
            clientId = configuration["AzureAd:ClientId"]!;
            clientSecret = configuration["AzureAd:ClientSecret"]!;
            recipientEmail = configuration["Contact:RecipientEmail"]!;
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

            var leadRecipientEmail = ResolveLeadRecipientEmail();

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

        // ===================== SEND EMAIL =====================
        var requestBody = new SendMailPostRequestBody
        {
            Message = message,
            SaveToSentItems = true
        };

        await graphClient.Users[recipientEmail].SendMail.PostAsync(requestBody);

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