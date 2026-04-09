using Microsoft.AspNetCore.Mvc;
using Domain.Entities;
using Protect_Website.Models;
using static Protect_Website.Models.LifeOfferResolver;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Azure.Identity;
using ProtectWebsite.Services.Tracking;
using System.Text;

namespace Protect_Website.Controllers
{
    [Route("Quote")]
    public class LifeQuoteController : Controller
    {
        private readonly string tenantId;
        private readonly string clientId;
        private readonly string clientSecret;
        private readonly string senderEmail;
        private readonly string recipientEmail;
        private readonly string websiteName;
        private readonly AgentTrackingResolver _resolver;

        public LifeQuoteController(IConfiguration configuration, AgentTrackingResolver resolver)
        {
            tenantId = configuration["AzureAd:TenantId"]!;
            clientId = configuration["AzureAd:ClientId"]!;
            clientSecret = configuration["AzureAd:ClientSecret"]!;
            senderEmail = configuration["Contact:SenderEmail"] ?? "connect@mylegnd.com";
            recipientEmail = configuration["Contact:RecipientEmail"]!;
            websiteName = configuration["Contact:WebsiteName"] ?? "Legend Legacy Protection";
            _resolver = resolver;
        }

        // ===================== GET =====================
        [HttpGet("Life")]
        public IActionResult LifeQuote([FromQuery] string? offer = null) => RenderWizard(string.IsNullOrWhiteSpace(offer) ? "life" : offer);
        [HttpGet("Term-Life")]
        public IActionResult TermLifeQuote() => RenderWizard("term");
        [HttpGet("Whole-Life")]
        public IActionResult WholeLifeQuote() => RenderWizard("wholelife");
        [HttpGet("Final-Expense")]
        public IActionResult FinalExpenseQuote() => RenderWizard("finalexpense");
        [HttpGet("Mortgage-Protection")]
        public IActionResult MortgageQuote() => RenderWizard("mortgage");
        [HttpGet("IUL")]
        public IActionResult IulQuote() => RenderWizard("iul");

        // ===================== POST =====================
        [HttpPost("Life")]
        public Task<IActionResult> SubmitLifeQuote(LifeQuoteFormModel model) => SubmitInternal(model, model.OfferKey ?? "life");
        [HttpPost("Term-Life")]
        public Task<IActionResult> SubmitTermLifeQuote(LifeQuoteFormModel model) => SubmitInternal(model, "term");
        [HttpPost("Whole-Life")]
        public Task<IActionResult> SubmitWholeLifeQuote(LifeQuoteFormModel model) => SubmitInternal(model, "wholelife");
        [HttpPost("Final-Expense")]
        public Task<IActionResult> SubmitFinalExpenseQuote(LifeQuoteFormModel model) => SubmitInternal(model, "finalexpense");
        [HttpPost("Mortgage-Protection")]
        public Task<IActionResult> SubmitMortgageQuote(LifeQuoteFormModel model) => SubmitInternal(model, "mortgage");
        [HttpPost("IUL")]
        public Task<IActionResult> SubmitIulQuote(LifeQuoteFormModel model) => SubmitInternal(model, "iul");

        private IActionResult RenderWizard(string offerKey)
        {
            var cfg = GetContent(offerKey);
            return View("~/Views/Quote/Life.cshtml", cfg);
        }

        private async Task<IActionResult> SubmitInternal(LifeQuoteFormModel model, string offerKey)
        {
            if (!ModelState.IsValid)
            {
                return View("~/Views/Quote/Life.cshtml", GetContent(offerKey));
            }

            model.OfferKey = offerKey;
            var leadRecipientEmail = await ResolveLeadRecipientEmailAsync();
            var offerContent = GetContent(offerKey);

            try
            {
                var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                var graphClient = new GraphServiceClient(credential);

                var message = new Message
                {
                    Subject = $"[LIFE QUOTE — {offerContent.DisplayName.ToUpperInvariant()}] New Lead | {model.FirstName}",
                    Body = new ItemBody
                    {
                        ContentType = BodyType.Html,
                        Content = BuildEmailBody(model, offerContent)
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

                var requestBody = new SendMailPostRequestBody
                {
                    Message = message,
                    SaveToSentItems = true
                };

                await graphClient.Users[senderEmail].SendMail.PostAsync(requestBody);

            // Set the quote type so the Thank You page can display the correct name
            TempData["QuoteType"] = offerContent.DisplayName;

                // ✅ Redirect to centralized ThankYouController
                return RedirectToAction("Index", "ThankYou");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", $"Failed to send lead: {ex.Message}");
                return View("~/Views/Quote/Life.cshtml", offerContent);
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

        private string BuildEmailBody(LifeQuoteFormModel model, LifeOfferContent offer)
        {
            var sb = new StringBuilder();
            sb.Append($"<h2>{offer.DisplayName} Lead</h2>");
            sb.Append("<h3>Personal Information</h3>");
            sb.Append($"<p><strong>Name:</strong> {model.FirstName}</p>");
            sb.Append($"<p><strong>Email:</strong> {model.Email}</p>");
            sb.Append($"<p><strong>Phone:</strong> {model.Phone}</p>");

            sb.Append("<hr /><h3>Responses</h3>");
            void addRow(string label, string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                sb.Append($"<p><strong>{label}:</strong> {value}</p>");
            }
            addRow("Age Range", model.AgeRange);
            addRow("Protect Focus", model.ProtectFocus);
            addRow("Answer 1", model.Answer1);
            addRow("Answer 2", model.Answer2);
            addRow("Answer 3", model.Answer3);
            addRow("Answer 4", model.Answer4);

            sb.Append("<hr /><h3>Consent</h3>");
            sb.Append($"<p><strong>Marketing Consent:</strong> {(model.MarketingEmailConsent ? "Yes" : "No")}</p>");
            sb.Append($"<p><strong>Offer:</strong> {offer.DisplayName}</p>");
            sb.Append($"<p><strong>Offer Key:</strong> {model.OfferKey}</p>");
            return sb.ToString();
        }
    }
}
