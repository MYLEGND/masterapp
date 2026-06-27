using Microsoft.AspNetCore.Mvc;

namespace ParfaitApp.Controllers
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
            var contactEmail = (_config["Contact:RecipientEmail"] ?? "parfait@mylegnd.com").Trim();

            ViewData["ContactEmail"] = contactEmail;
            ViewData["SeoTitle"] = "Contact Parfait";
            ViewData["SeoDescription"] = "Contact Parfait for questions about training, subscriptions, orders, and brand support.";

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
