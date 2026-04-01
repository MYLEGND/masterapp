using Microsoft.AspNetCore.Mvc;

namespace Protect_Website.Controllers
{
    public class ThankYouController : Controller
    {
        // GET: /ThankYou
        public IActionResult Index()
        {
            // Loads Views/Quote/ThankYou.cshtml
            return View("~/Views/Quote/ThankYou.cshtml");
        }
    }
}
