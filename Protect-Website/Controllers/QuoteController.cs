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
        public IActionResult Life() => View("Life");
        public IActionResult Health() => View("Health");
        public IActionResult Commercial() => View("Commercial");
        public IActionResult Other() => View("Other");
    }
}
