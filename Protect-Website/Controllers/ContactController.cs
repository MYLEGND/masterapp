using Microsoft.AspNetCore.Mvc;

namespace Protect_Website.Controllers
{
    public class ContactController : Controller
    {
        private readonly IConfiguration _config;

        public ContactController(IConfiguration config)
        {
            _config = config;
        }

        // GET: /Contact
        [HttpGet]
        public IActionResult Index()
        {
            var founderEmail = _config["Founder:Upn"] ?? "zac.owen@mylegnd.com";
            var profile = HttpContext.Items["TrackingProfile"] as Domain.Entities.AgentTrackingProfile;
            var contactEmail = profile?.AgentUpn ?? founderEmail;
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
    }
}
