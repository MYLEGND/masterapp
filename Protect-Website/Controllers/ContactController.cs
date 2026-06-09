using Microsoft.AspNetCore.Mvc;
using ProtectWebsite.Services.Tracking;

namespace Protect_Website.Controllers
{
    public class ContactController : Controller
    {
        private readonly IConfiguration _config;
        private readonly AgentTrackingResolver _resolver;

        public ContactController(IConfiguration config, AgentTrackingResolver resolver)
        {
            _config = config;
            _resolver = resolver;
        }

        // GET: /Contact
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var founderEmail = _config["Founder:Upn"] ?? "zac.owen@mylegnd.com";
            var contactEmail = await ResolveContactEmailAsync(founderEmail);
            ViewData["ContactEmail"] = contactEmail;
            return View();
        }

        // OPTIONAL: Hard-block POSTs to /Contact so bots can't spam
        // (This prevents confusion if someone tries to POST here)
        [HttpPost]
        public IActionResult Index(object _)
        {
            return RedirectToAction(nameof(Index));
        }

        private async Task<string> ResolveContactEmailAsync(string fallbackEmail)
        {
            var profile = HttpContext.Items["TrackingProfile"] as Domain.Entities.AgentTrackingProfile;
            if (!string.IsNullOrWhiteSpace(profile?.AgentUpn))
            {
                return profile.AgentUpn.Trim();
            }

            string? slug = null;

            var querySlug = Request?.Query["agent"].ToString();
            if (!string.IsNullOrWhiteSpace(querySlug))
                slug = querySlug.Trim();

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

            return fallbackEmail;
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
