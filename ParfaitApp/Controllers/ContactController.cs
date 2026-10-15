using Microsoft.AspNetCore.Mvc;

namespace ParfaitApp.Controllers
{
    public class ContactController : Controller
    {
        [HttpGet]
        public IActionResult Index()
        {
            ViewData["SeoTitle"] = "Contact Parfait";
            ViewData["SeoDescription"] = "Contact Parfait for questions about training, subscriptions, orders, and brand support.";
            return View();
        }

        // Public inquiries submit only through the canonical JSON authority.
        [HttpPost]
        public IActionResult Index(object _) => RedirectToAction(nameof(Index));
    }
}
