using Microsoft.AspNetCore.Mvc;

namespace Protect_Website.Controllers
{
    public class ContactController : Controller
    {
        [HttpGet]
        public IActionResult Index() => View();

        // Public inquiries submit only through the canonical JSON authority.
        [HttpPost]
        public IActionResult Index(object _) => RedirectToAction(nameof(Index));
    }
}
