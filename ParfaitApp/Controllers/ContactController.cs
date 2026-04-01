using Microsoft.AspNetCore.Mvc;

namespace ParfaitApp.Controllers
{
    public class ContactController : Controller
    {
        // GET: /Contact
        [HttpGet]
        public IActionResult Index()
        {
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
