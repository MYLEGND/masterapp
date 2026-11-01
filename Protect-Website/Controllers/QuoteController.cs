using Microsoft.AspNetCore.Mvc;

namespace Protect_Website.Controllers
{
    public class QuoteController : Controller
    {
        // Optional: you can keep website name if you want
        private readonly string websiteName;

        public QuoteController(IConfiguration configuration)
        {
            websiteName = configuration["Contact:WebsiteName"] ?? "Legend Legacy Protection";
        }

        public IActionResult Index()
        {
            // This view can render your 6 insurance buttons
            return View();
        }

        // Optional: You can add actions for each button if needed
        public IActionResult Auto() => View("Auto");
        public IActionResult Home() => View("Home");
        [HttpGet]
        public LocalRedirectResult Life() => CanonicalQuote("Life");
        [HttpGet]
        public LocalRedirectResult DentalVisionHearing() => CanonicalQuote("Dental-Vision-Hearing");

        private LocalRedirectResult CanonicalQuote(string page)
        {
            // This legacy GET is an alias, never a second editable form runtime.
            // Reuse the middleware-owned scope and keep the same-origin query,
            // including normal Studio authorization and attribution.
            var slug = HttpContext.Items["TrackingSlug"] as string;
            var isFounder = HttpContext.Items["IsFounderPath"] as bool? == true;
            var scope = !isFounder && !string.IsNullOrWhiteSpace(slug)
                ? "/a/" + Uri.EscapeDataString(slug.Trim()) : string.Empty;
            return LocalRedirect($"{Request.PathBase}{scope}/Quote/{page}{Request.QueryString}");
        }
        public IActionResult Commercial() => View("Commercial");
        public IActionResult Other() => View("Other");
    }
}
