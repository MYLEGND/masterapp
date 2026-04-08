using Microsoft.AspNetCore.Mvc;
using Protect_Website.Models;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Azure.Identity;

namespace Protect_Website.Controllers
{
    [Route("Quote")]
    public class HealthQuoteController : Controller
    {
        private readonly string tenantId;
        private readonly string clientId;
        private readonly string clientSecret;
        private readonly string recipientEmail;
        private readonly string websiteName;

        public HealthQuoteController(IConfiguration configuration)
        {
            tenantId = configuration["AzureAd:TenantId"]!;
            clientId = configuration["AzureAd:ClientId"]!;
            clientSecret = configuration["AzureAd:ClientSecret"]!;
            recipientEmail = configuration["Contact:RecipientEmail"]!;
            websiteName = configuration["Contact:WebsiteName"] ?? "Legend Legacy Protection";
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
<p><strong>Requested Coverage Amount:</strong> {model.CoverageType}</p>
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
                        Address = recipientEmail
                    }
                }
            }
        };

        var requestBody = new SendMailPostRequestBody
        {
            Message = message,
            SaveToSentItems = true
        };

        await graphClient.Users[recipientEmail].SendMail.PostAsync(requestBody);

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
    }
}