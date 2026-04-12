using Microsoft.AspNetCore.Mvc;
using Domain.Entities;
using Infrastructure.Data;
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
using System.Text.Json;
using ProtectWebsite.Services;

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
        private readonly MasterAppDbContext _db;
        private readonly ILogger<LifeQuoteController> _logger;

        public LifeQuoteController(IConfiguration configuration, AgentTrackingResolver resolver,
            MasterAppDbContext db, ILogger<LifeQuoteController> logger)
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
        [HttpGet("Life")]
        public IActionResult LifeQuote([FromQuery] string? offer = null) => RenderWizard(string.IsNullOrWhiteSpace(offer) ? "life" : offer);
        [HttpGet("Life/landing")]
        public IActionResult LifeLandingQuote() => RenderWizard(LifeOfferKeys.Life, isLandingPage: true);
        [HttpGet("Term-Life")]
        public IActionResult TermLifeQuote() => RenderWizard("term");
        [HttpGet("Term-Life/landing")]
        public IActionResult TermLifeLandingQuote() => RenderWizard(LifeOfferKeys.Term, isLandingPage: true);
        [HttpGet("Whole-Life")]
        public IActionResult WholeLifeQuote() => RenderWizard("wholelife");
        [HttpGet("Whole-Life/landing")]
        public IActionResult WholeLifeLandingQuote() => RenderWizard(LifeOfferKeys.WholeLife, isLandingPage: true);
        [HttpGet("Final-Expense")]
        public IActionResult FinalExpenseQuote() => RenderWizard("finalexpense");
        [HttpGet("Final-Expense/landing")]
        public IActionResult FinalExpenseLandingQuote() => RenderWizard(LifeOfferKeys.FinalExpense, isLandingPage: true);
        [HttpGet("Mortgage-Protection")]
        public IActionResult MortgageQuote() => RenderWizard("mortgage");
        [HttpGet("Mortgage-Protection/landing")]
        public IActionResult MortgageLandingQuote() => RenderWizard(LifeOfferKeys.Mortgage, isLandingPage: true);
        [HttpGet("IUL")]
        public IActionResult IulQuote() => RenderWizard("iul");
        [HttpGet("IUL/landing")]
        public IActionResult IulLandingQuote() => RenderWizard(LifeOfferKeys.Iul, isLandingPage: true);

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

        private IActionResult RenderWizard(string offerKey, bool isLandingPage = false)
        {
            var cfg = GetWizardConfig(offerKey);
            var mode = ResolvePageMode(cfg, isLandingPage, model: null);
            var vm = BuildWizardViewModel(cfg, new LifeQuoteFormModel
            {
                FirstName = "",
                LastName = "",
                Email = "",
                Phone = "",
                OfferKey = cfg.OfferKey,
                ProductType = cfg.ProductType
            }, mode);
            ApplyWizardViewData(vm);
            return View("~/Views/Quote/Life.cshtml", vm);
        }

        private async Task<IActionResult> SubmitInternal(LifeQuoteFormModel model, string offerKey)
        {
            var cfg = GetWizardConfig(offerKey);
            var pageMode = ResolvePageMode(cfg, isLandingPage: false, model);
            // Shared intake across all life product types:
            // First/Last/Phone required, Email/State optional.
            var requiresEmailAndState = false;
            if (requiresEmailAndState)
            {
                if (string.IsNullOrWhiteSpace(model.Email))
                {
                    ModelState.AddModelError(nameof(LifeQuoteFormModel.Email), "Email is required");
                }

                if (string.IsNullOrWhiteSpace(model.State))
                {
                    ModelState.AddModelError(nameof(LifeQuoteFormModel.State), "State is required");
                }
            }

            if (!ModelState.IsValid)
            {
                if (IsAjax())
                    return BadRequest(new { error = "Invalid form data" });

                model.OfferKey = cfg.OfferKey;
                model.ProductType = cfg.ProductType;
                var vmInvalid = BuildWizardViewModel(cfg, model, pageMode);
                ApplyWizardViewData(vmInvalid);
                return View("~/Views/Quote/Life.cshtml", vmInvalid);
            }

            model.OfferKey = offerKey;
            model.ProductType = cfg.ProductType;
            model.PageKey = pageMode.EffectivePageKey;
            model.PageVariant = pageMode.PageVariant;
            model.PageMode = pageMode.PageMode;
            var offerContent = GetContent(offerKey);
            var isAgentContext = IsAgentContext();

            var correlationId = Guid.NewGuid();
            _logger.LogInformation(
                "LifeQuote [{CorrelationId}]: request received offer={Offer} Email={Email}",
                correlationId, offerKey, model.Email);

            var (leadRecipientEmail, agentProfileId, agentSlug) = await ResolveLeadContextAsync();
            _logger.LogInformation(
                "LifeQuote [{CorrelationId}]: attribution resolved AgentSlug={Slug} ProfileId={ProfileId} Recipient={Recipient}",
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
                    InterestType  = cfg.ProductType,
                    SourcePageKey = pageMode.EffectivePageKey,
                    UtmSource     = string.IsNullOrWhiteSpace(model.UtmSource)   ? null : model.UtmSource.Trim(),
                    UtmMedium     = string.IsNullOrWhiteSpace(model.UtmMedium)   ? null : model.UtmMedium.Trim(),
                    UtmCampaign   = string.IsNullOrWhiteSpace(model.UtmCampaign) ? null : model.UtmCampaign.Trim(),
                    SessionId     = string.IsNullOrWhiteSpace(model.SessionId)   ? null : model.SessionId.Trim(),
                    VisitorId     = string.IsNullOrWhiteSpace(model.VisitorId)   ? null : model.VisitorId.Trim(),
                    MarketingEmailConsent = model.MarketingEmailConsent,
                    CallTextConsent = model.MarketingEmailConsent && !string.IsNullOrWhiteSpace(model.Phone),
                    TermsAccepted = true,
                    Host          = Request?.Host.ToString(),
                    Environment   = EnvironmentLabelResolver.Resolve(),
                    CreatedUtc    = now,
                    Status        = "New",
                    AgentTrackingProfileId = agentProfileId,
                    AgentSlug     = agentSlug,
                    MetadataJson  = JsonSerializer.Serialize(new
                    {
                        OfferKey       = model.OfferKey,
                        ProductType    = model.ProductType,
                        Answer1        = model.Answer1,
                        Answer2        = model.Answer2,
                        Answer3        = model.Answer3,
                        Answer4        = model.Answer4,
                        State          = model.State,
                        AgeRange       = model.AgeRange,
                        Fbclid         = model.Fbclid,
                        UtmTerm        = model.UtmTerm,
                        UtmContent     = model.UtmContent,
                        ReferrerUrl    = model.ReferrerUrl,
                        LandingPageUrl = model.LandingPageUrl,
                        PageVariant    = pageMode.PageVariant,
                        PageMode       = pageMode.PageMode,
                        PagePath       = Request?.Path.Value,
                        CorrelationId  = correlationId,
                    })
                };
                _db.WebsiteLeads.Add(lead);
                await _db.SaveChangesAsync();
                _logger.LogInformation(
                    "LifeQuote [{CorrelationId}]: WebsiteLead {LeadId} saved offer={Offer}",
                    correlationId, lead.LeadId, model.OfferKey);
            }
            catch (Exception persistEx)
            {
                _logger.LogError(persistEx,
                    "LifeQuote [{CorrelationId}]: lead persistence failed for {Email} offer={Offer}",
                    correlationId, model.Email, model.OfferKey);
                if (IsAjax())
                    return StatusCode(500, new { error = "Failed to save lead", detail = persistEx.Message });
                ModelState.AddModelError("", $"Failed to save lead: {persistEx.Message}");
                var vmPersistErr = BuildWizardViewModel(cfg, model, pageMode);
                ApplyWizardViewData(vmPersistErr);
                return View("~/Views/Quote/Life.cshtml", vmPersistErr);
            }

            // ── 2. Send email ─────────────────────────────────────────────────────
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

                // Recipient routing: agent slug -> agent; default URL -> founder; if slug missing email, fall back to founder
                string? primary = null;
                if (isAgentContext && !string.IsNullOrWhiteSpace(leadRecipientEmail))
                    primary = leadRecipientEmail.Trim();
                else if (!isAgentContext && !string.IsNullOrWhiteSpace(recipientEmail))
                    primary = recipientEmail.Trim();
                else if (!string.IsNullOrWhiteSpace(recipientEmail))
                    primary = recipientEmail.Trim();
                else if (!string.IsNullOrWhiteSpace(senderEmail))
                    primary = senderEmail.Trim();

                if (!string.IsNullOrWhiteSpace(primary))
                {
                    message.ToRecipients.Add(new Recipient { EmailAddress = new EmailAddress { Address = primary } });
                    await graphClient.Users[senderEmail].SendMail.PostAsync(
                        new SendMailPostRequestBody { Message = message, SaveToSentItems = true });
                    _logger.LogInformation(
                        "LifeQuote [{CorrelationId}]: email sent to {Recipient} for lead {LeadId} offer={Offer}",
                        correlationId, primary, lead.LeadId, model.OfferKey);
                }
                else
                {
                    _logger.LogWarning(
                        "LifeQuote [{CorrelationId}]: no recipient resolved for lead {LeadId} offer={Offer} — email skipped",
                        correlationId, lead.LeadId, model.OfferKey);
                }
            }
            catch (Exception emailEx)
            {
                _logger.LogError(emailEx,
                    "LifeQuote [{CorrelationId}]: email send failed for lead {LeadId} offer={Offer} — lead is saved, continuing",
                    correlationId, lead.LeadId, model.OfferKey);
            }

            // ── 3. Write analytics event ─────────────────────────────────────────
            try
            {
                var evt = new AnalyticsEvent
                {
                    EventId    = Guid.NewGuid(),
                    EventType  = "website_lead_submitted",
                    PageKey    = pageMode.EffectivePageKey,
                    FormKey    = pageMode.EffectivePageKey + "_form",
                    QuoteType  = cfg.ProductType,
                    SessionId  = lead.SessionId,
                    VisitorId  = lead.VisitorId,
                    UtmSource  = lead.UtmSource,
                    UtmMedium  = lead.UtmMedium,
                    UtmCampaign= lead.UtmCampaign,
                    AgentTrackingProfileId = lead.AgentTrackingProfileId,
                    AgentSlug  = lead.AgentSlug,
                    Environment= lead.Environment,
                    Host       = lead.Host,
                    EventUtc   = lead.CreatedUtc,
                    ReceivedUtc= DateTime.UtcNow,
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        LeadId        = lead.LeadId,
                        CorrelationId = correlationId,
                        OfferKey      = model.OfferKey,
                        PageVariant   = pageMode.PageVariant,
                        PageMode      = pageMode.PageMode,
                        PagePath      = Request?.Path.Value
                    })
                };
                _db.AnalyticsEvents.Add(evt);
                await _db.SaveChangesAsync();
                _logger.LogInformation(
                    "LifeQuote [{CorrelationId}]: analytics event {EventId} written for lead {LeadId} offer={Offer}",
                    correlationId, evt.EventId, lead.LeadId, model.OfferKey);
            }
            catch (Exception analyticsEx)
            {
                _logger.LogError(analyticsEx,
                    "LifeQuote [{CorrelationId}]: analytics event write failed for lead {LeadId} offer={Offer} — lead is saved, continuing",
                    correlationId, lead.LeadId, model.OfferKey);
            }

            // Set the quote type so the Thank You page can display the correct name
            TempData["QuoteType"] = offerContent.DisplayName;

            // AJAX: return 200 OK so JS can navigate client-side (preserves TempData for subsequent GET)
            if (IsAjax())
                return Ok(new { success = true });

            return RedirectToAction("Index", "ThankYou");
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

        private static string BuildEmailBody(LifeQuoteFormModel model, LifeWizardConfig cfg)
        {
            var rows = new LeadEmailTemplate.RowBuilder()
                .Row("Name",  $"{model.FirstName} {model.LastName}".Trim())
                .Row("Phone", model.Phone)
                .Row("Email", model.Email);

            // Add step responses for variants that still collect them
            var answers = new[] { model.Answer1, model.Answer2, model.Answer3, model.Answer4 };
            if (cfg.Steps.Any())
            {
                rows.Section("Responses");
                for (var i = 0; i < cfg.Steps.Count && i < answers.Length; i++)
                {
                    var step = cfg.Steps[i];
                    var code = answers[i];
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    var label = step.Options.FirstOrDefault(o => o.Code == code)?.Label ?? code;
                    rows.Row(step.Question, label);
                }
            }

            rows.Section("Details")
                .Row("Product",          cfg.DisplayName)
                .Row("Offer Key",        model.OfferKey)
                .Row("State",            model.State)
                .Row("Contact Consent",  LeadEmailTemplate.Bool(model.MarketingEmailConsent));

            return LeadEmailTemplate.Wrap($"New Lead — {cfg.DisplayName}", rows.ToString());
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

        private static string BuildVariantPageKey(string basePageKey, bool isLandingPage) =>
            isLandingPage ? $"{basePageKey}_landing" : basePageKey;

        private static WizardPageMode ResolvePageMode(LifeWizardConfig cfg, bool isLandingPage, LifeQuoteFormModel? model)
        {
            var requestedVariant = model?.PageVariant?.Trim();
            var requestedMode = model?.PageMode?.Trim();
            var postedPageKey = model?.PageKey?.Trim();
            var landingRoutePath = GetLandingRoutePath(cfg.OfferKey);

            var isLandingRequested =
                isLandingPage ||
                string.Equals(requestedVariant, "landing", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(requestedMode, "paid_landing", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(postedPageKey, BuildVariantPageKey(cfg.PageKey, true), StringComparison.OrdinalIgnoreCase) ||
                IsLandingRouteForOffer(model?.LandingPageUrl, landingRoutePath);

            return new WizardPageMode(
                IsLandingPage: isLandingRequested,
                PageVariant: isLandingRequested ? "landing" : "website",
                PageMode: isLandingRequested ? "paid_landing" : "site_mode",
                EffectivePageKey: BuildVariantPageKey(cfg.PageKey, isLandingRequested)
            );
        }

        private static bool IsLandingRouteForOffer(string? landingPageUrl, string landingRoutePath)
        {
            if (string.IsNullOrWhiteSpace(landingPageUrl) || string.IsNullOrWhiteSpace(landingRoutePath))
                return false;

            if (Uri.TryCreate(landingPageUrl, UriKind.Absolute, out var absolute))
                return absolute.AbsolutePath.Contains(landingRoutePath, StringComparison.OrdinalIgnoreCase);

            if (Uri.TryCreate(landingPageUrl, UriKind.Relative, out var relative))
                return relative.OriginalString.Contains(landingRoutePath, StringComparison.OrdinalIgnoreCase);

            return landingPageUrl.Contains(landingRoutePath, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetLandingRoutePath(string offerKey)
        {
            var normalized = LifeOfferResolver.Normalize(offerKey);
            return normalized switch
            {
                LifeOfferKeys.Life => "/Quote/Life/landing",
                LifeOfferKeys.Mortgage => "/Quote/Mortgage-Protection/landing",
                LifeOfferKeys.FinalExpense => "/Quote/Final-Expense/landing",
                LifeOfferKeys.Term => "/Quote/Term-Life/landing",
                LifeOfferKeys.WholeLife => "/Quote/Whole-Life/landing",
                LifeOfferKeys.Iul => "/Quote/IUL/landing",
                _ => "/Quote/Life/landing"
            };
        }

        private static LifeWizardViewModel BuildWizardViewModel(LifeWizardConfig cfg, LifeQuoteFormModel model, WizardPageMode pageMode)
        {
            model.OfferKey = string.IsNullOrWhiteSpace(model.OfferKey) ? cfg.OfferKey : model.OfferKey;
            model.ProductType = string.IsNullOrWhiteSpace(model.ProductType) ? cfg.ProductType : model.ProductType;
            model.PageKey = pageMode.EffectivePageKey;
            model.PageVariant = pageMode.PageVariant;
            model.PageMode = pageMode.PageMode;

            return new LifeWizardViewModel
            {
                Config = cfg,
                Form = model,
                IsLandingPage = pageMode.IsLandingPage,
                PageVariant = pageMode.PageVariant,
                PageMode = pageMode.PageMode,
                EffectivePageKey = pageMode.EffectivePageKey
            };
        }

        private void ApplyWizardViewData(LifeWizardViewModel vm)
        {
            ViewData["Title"] = vm.Config.PageTitle;
            ViewData["PageKey"] = vm.EffectivePageKey;
            ViewData["IsLandingPage"] = vm.IsLandingPage;
            ViewData["PageVariant"] = vm.PageVariant;
            ViewData["PageMode"] = vm.PageMode;
            ViewData["PageCategory"] = "quote";
            ViewData["QuoteTypeForTracking"] = vm.Config.OfferKey;
        }

        private static LifeWizardConfig GetWizardConfig(string rawOfferKey)
        {
            var key = LifeOfferResolver.Normalize(rawOfferKey);
            return WizardConfigs.TryGetValue(key, out var cfg) ? cfg : WizardConfigs[LifeOfferKeys.Life];
        }

        private readonly record struct WizardPageMode(
            bool IsLandingPage,
            string PageVariant,
            string PageMode,
            string EffectivePageKey);

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
                    Header = "Make sure the people you love are protected.",
                    Subheader = "We’ll help you understand what protection could look like for your situation.",
                    PageTitle = "Protect the People Who Count on You",
                    SubmitButtonText = "SEE MY OPTIONS",
                    StartEvent = "life_general_form_start",
                    SubmitEvent = "life_general_submit",
                    Steps = new List<LifeWizardStep>() // stripped to contact-only for faster lead capture
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
