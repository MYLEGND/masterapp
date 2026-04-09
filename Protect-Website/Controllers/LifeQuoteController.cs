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
using System.Linq;
using System.Collections.Generic;

namespace Protect_Website.Controllers
{
    [Route("Quote")]
    public class LifeQuoteController : Controller
    {
        private static readonly IReadOnlyDictionary<string, LifeWizardConfig> WizardConfigs = BuildConfigs();

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
            var cfg = GetWizardConfig(offerKey);
            var vm = new LifeWizardViewModel
            {
                Config = cfg,
                Form = new LifeQuoteFormModel
                {
                    FirstName = "",
                    LastName = "",
                    Email = "",
                    Phone = "",
                    OfferKey = cfg.OfferKey,
                    ProductType = cfg.ProductType,
                    PageKey = cfg.PageKey
                }
            };
            ViewData["Title"] = cfg.PageTitle;
            return View("~/Views/Quote/Life.cshtml", vm);
        }

        private async Task<IActionResult> SubmitInternal(LifeQuoteFormModel model, string offerKey)
        {
            var cfg = GetWizardConfig(offerKey);
            if (!ModelState.IsValid)
            {
                if (IsAjax())
                    return BadRequest(new { error = "Invalid form data" });

                var vmInvalid = new LifeWizardViewModel { Config = cfg, Form = model };
                ViewData["Title"] = cfg.PageTitle;
                return View("~/Views/Quote/Life.cshtml", vmInvalid);
            }

            model.OfferKey = offerKey;
            model.ProductType = cfg.ProductType;
            var leadRecipientEmail = await ResolveLeadRecipientEmailAsync();
            var offerContent = GetContent(offerKey);
            var isAgentContext = IsAgentContext();

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
                        Content = BuildEmailBody(model, cfg)
                    },
                    ToRecipients = new List<Recipient>()
                };

                // Always send to the resolved agent; also copy founder/owner as safety net
                // Recipient routing: agent if slug/context; founder only for default URLs
                var recipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (isAgentContext && !string.IsNullOrWhiteSpace(leadRecipientEmail) &&
                    !string.Equals(leadRecipientEmail, recipientEmail, StringComparison.OrdinalIgnoreCase))
                {
                    recipients.Add(leadRecipientEmail.Trim());
                }
                else
                {
                    // default / unknown -> founder (or resolved email)
                    var fallback = string.IsNullOrWhiteSpace(leadRecipientEmail) ? recipientEmail : leadRecipientEmail;
                    if (!string.IsNullOrWhiteSpace(fallback)) recipients.Add(fallback.Trim());
                }

                if (recipients.Count == 0) throw new InvalidOperationException("No recipient email resolved.");

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
                if (IsAjax())
                    return StatusCode(500, new { error = "Failed to send lead", detail = ex.Message });

                ModelState.AddModelError("", $"Failed to send lead: {ex.Message}");
                var vmError = new LifeWizardViewModel { Config = cfg, Form = model };
                ViewData["Title"] = cfg.PageTitle;
                return View("~/Views/Quote/Life.cshtml", vmError);
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

        private string BuildEmailBody(LifeQuoteFormModel model, LifeWizardConfig cfg)
        {
            var sb = new StringBuilder();
            sb.Append($"<h2>{cfg.DisplayName} Lead</h2>");
            sb.Append("<h3>Personal Information</h3>");
            sb.Append($"<p><strong>Name:</strong> {model.FirstName} {model.LastName}</p>");
            sb.Append($"<p><strong>Email:</strong> {model.Email}</p>");
            sb.Append($"<p><strong>Phone:</strong> {model.Phone}</p>");

            sb.Append("<hr /><h3>Responses</h3>");
            void addRow(string label, string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                sb.Append($"<p><strong>{label}:</strong> {value}</p>");
            }
            var answers = new[] { model.Answer1, model.Answer2, model.Answer3, model.Answer4 };
            for (var i = 0; i < cfg.Steps.Count && i < answers.Length; i++)
            {
                var step = cfg.Steps[i];
                var code = answers[i];
                if (string.IsNullOrWhiteSpace(code)) continue;
                var label = step.Options.FirstOrDefault(o => o.Code == code)?.Label ?? code;
                addRow(step.Question, label);
            }
            // legacy aliases preserved for downstream consumers
            addRow("Age Range", model.AgeRange);
            addRow("Protect Focus", model.ProtectFocus);

            sb.Append("<hr /><h3>Consent</h3>");
            sb.Append($"<p><strong>Marketing Consent:</strong> {(model.MarketingEmailConsent ? "Yes" : "No")}</p>");
            sb.Append($"<p><strong>Product:</strong> {cfg.DisplayName}</p>");
            sb.Append($"<p><strong>Offer Key:</strong> {model.OfferKey}</p>");
            sb.Append($"<p><strong>Page Key:</strong> {model.PageKey}</p>");
            return sb.ToString();
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

        private static LifeWizardConfig GetWizardConfig(string rawOfferKey)
        {
            var key = LifeOfferResolver.Normalize(rawOfferKey);
            return WizardConfigs.TryGetValue(key, out var cfg) ? cfg : WizardConfigs[LifeOfferKeys.Life];
        }

        private static IReadOnlyDictionary<string, LifeWizardConfig> BuildConfigs()
        {
            return new Dictionary<string, LifeWizardConfig>(StringComparer.OrdinalIgnoreCase)
            {
                [LifeOfferKeys.Life] = new LifeWizardConfig
                {
                    OfferKey = LifeOfferKeys.Life,
                    ProductType = "life_general",
                    DisplayName = "Life Insurance",
                    PageKey = "quote_life",
                    PostAction = "SubmitLifeQuote",
                    Header = "Explore Your Life Insurance Options",
                    Subheader = "Request a personalized review based on your needs, goals, and budget.",
                    PageTitle = "Get Your Personalized Life Insurance Review",
                    StartEvent = "life_general_form_start",
                    SubmitEvent = "life_general_submit",
                    Steps = new List<LifeWizardStep>
                    {
                        new("What are you mainly looking to protect?", new List<LifeWizardOption>
                        {
                            new("family_income","My Family / Income"),
                            new("mortgage_debts","Mortgage / Debts"),
                            new("final_expenses","Final Expenses"),
                            new("business_legacy","Business / Legacy Planning"),
                            new("not_sure","Not Sure Yet"),
                        }, "ProtectFocus"),
                        new("How old are you?", new List<LifeWizardOption>
                        {
                            new("18-29","18–29"),
                            new("30-39","30–39"),
                            new("40-49","40–49"),
                            new("50-59","50–59"),
                            new("60plus","60+"),
                        }, "AgeRange"),
                        new("What monthly budget feels comfortable?", new List<LifeWizardOption>
                        {
                            new("under50","Under $50"),
                            new("50-100","$50–100"),
                            new("100-250","$100–250"),
                            new("250plus","$250+"),
                        }),
                        new("When are you looking to put coverage in place?", new List<LifeWizardOption>
                        {
                            new("asap","ASAP"),
                            new("30days","Within 30 Days"),
                            new("researching","Researching Options"),
                            new("exploring","Just Exploring"),
                        }),
                    }
                },
                [LifeOfferKeys.Term] = new LifeWizardConfig
                {
                    OfferKey = LifeOfferKeys.Term,
                    ProductType = "life_term",
                    DisplayName = "Term Life",
                    PageKey = "quote_term_life",
                    PostAction = "SubmitTermLifeQuote",
                    Header = "Explore Term Life Insurance Options",
                    Subheader = "Review temporary coverage options designed to help protect your income and family.",
                    PageTitle = "Term Life Insurance Review",
                    StartEvent = "life_term_form_start",
                    SubmitEvent = "life_term_submit",
                    Steps = new List<LifeWizardStep>
                    {
                        new("How much coverage are you looking for?", new List<LifeWizardOption>
                        {
                            new("100-250k","$100k–250k"),
                            new("250-500k","$250k–500k"),
                            new("500k-1m","$500k–1M"),
                            new("1mplus","$1M+"),
                        }),
                        new("How long do you want coverage for?", new List<LifeWizardOption>
                        {
                            new("10yr","10 Years"),
                            new("20yr","20 Years"),
                            new("30yr","30 Years"),
                            new("not_sure","Not Sure Yet"),
                        }),
                        new("How old are you?", new List<LifeWizardOption>
                        {
                            new("18-29","18–29"),
                            new("30-39","30–39"),
                            new("40-49","40–49"),
                            new("50-59","50–59"),
                            new("60plus","60+"),
                        }, "AgeRange"),
                        new("Do you currently use tobacco or nicotine products?", new List<LifeWizardOption>
                        {
                            new("yes","Yes"),
                            new("no","No"),
                        }),
                    }
                },
                [LifeOfferKeys.WholeLife] = new LifeWizardConfig
                {
                    OfferKey = LifeOfferKeys.WholeLife,
                    ProductType = "life_whole",
                    DisplayName = "Whole Life",
                    PageKey = "quote_whole_life",
                    PostAction = "SubmitWholeLifeQuote",
                    Header = "Explore Whole Life Insurance Options",
                    Subheader = "Review permanent coverage options built around long-term protection and legacy goals.",
                    PageTitle = "Whole Life Insurance Review",
                    StartEvent = "life_whole_form_start",
                    SubmitEvent = "life_whole_submit",
                    Steps = new List<LifeWizardStep>
                    {
                        new("What interests you most about whole life insurance?", new List<LifeWizardOption>
                        {
                            new("lifetime_protection","Lifetime Protection"),
                            new("guaranteed_db","Guaranteed Death Benefit"),
                            new("cash_value","Build Cash Value"),
                            new("legacy","Legacy / Estate Planning"),
                        }),
                        new("How much coverage are you considering?", new List<LifeWizardOption>
                        {
                            new("under50k","Under $50k"),
                            new("50-100k","$50k–100k"),
                            new("100-250k","$100k–250k"),
                            new("250kplus","$250k+"),
                        }),
                        new("How old are you?", new List<LifeWizardOption>
                        {
                            new("18-29","18–29"),
                            new("30-39","30–39"),
                            new("40-49","40–49"),
                            new("50-59","50–59"),
                            new("60plus","60+"),
                        }, "AgeRange"),
                        new("When are you looking to get coverage in place?", new List<LifeWizardOption>
                        {
                            new("asap","ASAP"),
                            new("30days","Within 30 Days"),
                            new("researching","Researching"),
                            new("exploring","Just Exploring"),
                        }),
                    }
                },
                [LifeOfferKeys.FinalExpense] = new LifeWizardConfig
                {
                    OfferKey = LifeOfferKeys.FinalExpense,
                    ProductType = "life_finalexpense",
                    DisplayName = "Final Expense",
                    PageKey = "quote_final_expense",
                    PostAction = "SubmitFinalExpenseQuote",
                    Header = "Explore Final Expense Coverage Options",
                    Subheader = "Review coverage options designed to help with burial and final expense planning.",
                    PageTitle = "Final Expense Coverage Review",
                    StartEvent = "life_finalexpense_form_start",
                    SubmitEvent = "life_finalexpense_submit",
                    Steps = new List<LifeWizardStep>
                    {
                        new("How old are you?", new List<LifeWizardOption>
                        {
                            new("50-59","50–59"),
                            new("60-69","60–69"),
                            new("70-79","70–79"),
                            new("80plus","80+"),
                        }, "AgeRange"),
                        new("How much coverage are you looking for?", new List<LifeWizardOption>
                        {
                            new("5-10k","$5k–10k"),
                            new("10-25k","$10k–25k"),
                            new("25kplus","$25k+"),
                        }),
                        new("Do you currently use tobacco or nicotine?", new List<LifeWizardOption>
                        {
                            new("yes","Yes"),
                            new("no","No"),
                        }),
                        new("Have you had any major health concerns in the last 5 years?", new List<LifeWizardOption>
                        {
                            new("none","No Major Concerns"),
                            new("diabetes","Diabetes"),
                            new("heart","Heart Condition"),
                            new("cancer","Cancer History"),
                            new("discuss","Prefer to Discuss"),
                        }),
                    }
                },
                [LifeOfferKeys.Mortgage] = new LifeWizardConfig
                {
                    OfferKey = LifeOfferKeys.Mortgage,
                    ProductType = "life_mp",
                    DisplayName = "Mortgage Protection",
                    PageKey = "quote_mortgage_protection",
                    PostAction = "SubmitMortgageQuote",
                    Header = "Protect What You’ve Built",
                    Subheader = "Review coverage options designed to help protect your mortgage and family.",
                    PageTitle = "Mortgage Protection Review",
                    StartEvent = "life_mp_form_start",
                    SubmitEvent = "life_mp_submit",
                    Steps = new List<LifeWizardStep>
                    {
                        new("How much is left on your mortgage?", new List<LifeWizardOption>
                        {
                            new("under100k","Under $100k"),
                            new("100-250k","$100k–250k"),
                            new("250-500k","$250k–500k"),
                            new("500kplus","$500k+"),
                        }),
                        new("How many years remain on your mortgage?", new List<LifeWizardOption>
                        {
                            new("under10","Under 10"),
                            new("10-20","10–20"),
                            new("20-30","20–30"),
                            new("not_sure","Not Sure"),
                        }),
                        new("How old are you?", new List<LifeWizardOption>
                        {
                            new("18-29","18–29"),
                            new("30-39","30–39"),
                            new("40-49","40–49"),
                            new("50-59","50–59"),
                            new("60plus","60+"),
                        }, "AgeRange"),
                        new("When was your home purchased or refinanced?", new List<LifeWizardOption>
                        {
                            new("within12","Within 12 Months"),
                            new("1-5years","1–5 Years Ago"),
                            new("5plus","5+ Years Ago"),
                        }),
                    }
                },
                [LifeOfferKeys.Iul] = new LifeWizardConfig
                {
                    OfferKey = LifeOfferKeys.Iul,
                    ProductType = "life_iul",
                    DisplayName = "Indexed Universal Life",
                    PageKey = "quote_iul",
                    PostAction = "SubmitIulQuote",
                    Header = "Explore Indexed Universal Life Options",
                    Subheader = "Review protection strategies designed for long-term goals and cash value potential.",
                    PageTitle = "Indexed Universal Life Review",
                    StartEvent = "life_iul_form_start",
                    SubmitEvent = "life_iul_submit",
                    Steps = new List<LifeWizardStep>
                    {
                        new("What is your primary goal?", new List<LifeWizardOption>
                        {
                            new("tax_free_income","Tax-Free Retirement Income"),
                            new("cash_growth","Cash Value Growth"),
                            new("legacy","Estate / Legacy Planning"),
                            new("business","Business Strategy"),
                        }),
                        new("What is your annual household income?", new List<LifeWizardOption>
                        {
                            new("under75k","Under $75k"),
                            new("75-150k","$75k–150k"),
                            new("150-250k","$150k–250k"),
                            new("250kplus","$250k+"),
                        }),
                        new("How much could you comfortably contribute monthly?", new List<LifeWizardOption>
                        {
                            new("100-250","$100–250"),
                            new("250-500","$250–500"),
                            new("500-1000","$500–1,000"),
                            new("1000plus","$1,000+"),
                        }),
                        new("How old are you?", new List<LifeWizardOption>
                        {
                            new("18-29","18–29"),
                            new("30-39","30–39"),
                            new("40-49","40–49"),
                            new("50-59","50–59"),
                            new("60plus","60+"),
                        }, "AgeRange"),
                    }
                },
            };
        }
    }
}
