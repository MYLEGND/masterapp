using Microsoft.AspNetCore.Mvc;
using Domain.Entities;
using Protect_Website.Models;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Azure.Identity;
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
                return IsAjax() ? BadRequest(new { error = "Invalid form data" })
                                : View("~/Views/Quote/Disability.cshtml", model);

            var leadRecipientEmail = await ResolveLeadRecipientEmailAsync();
            var isAgentContext = IsAgentContext();

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

        // ===================== REDIRECT =====================
        TempData["QuoteType"] = "Disability";
        return IsAjax() ? Ok(new { success = true }) : RedirectToAction("Index", "ThankYou");
    }
    catch (Exception ex)
    {
        if (IsAjax())
            return StatusCode(500, new { error = "Failed to send lead", detail = ex.Message });

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
