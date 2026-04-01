using Microsoft.AspNetCore.Mvc;

namespace Protect_Website.Controllers
{
    public class TrainingPackagesController : Controller
    {
        [HttpGet]
        public IActionResult Index()
        {
            // =========================
            // STANDARD (Fitness Only) — SAME PRICING AS PARFAIT
            // =========================
            ViewBag.SquarePrelaunchLink = "https://square.link/u/A8TwXFYE";  // $250/mo
            ViewBag.SquareTrainingLink  = "https://square.link/u/NqNKtFWN";  // $400/mo
            ViewBag.SquareFullLink      = "https://square.link/u/N1Nl1GHz";  // $550/mo
            ViewBag.SquareGymLink       = "https://square.link/u/JeV2isT9";  // Gym/whatever you used

            // =========================
            // LEGACY BUNDLE (Health + Wealth) — 50% OFF
            // IMPORTANT:
            // These MUST be separate Square links created at the discounted price.
            // If you haven't created them yet, leave "#" temporarily.
            // =========================
            ViewBag.SquarePrelaunchLinkWealth = "https://square.link/u/FdCWRDkc"; // $125/mo link
            ViewBag.SquareTrainingLinkWealth  = "https://square.link/u/K96zXK6X"; // $200/mo link
            ViewBag.SquareFullLinkWealth      = "https://square.link/u/8y6kVS01"; // $275/mo link
            ViewBag.SquareGymLinkWealth       = "#"; // if needed discounted version

            return View();
        }
    }
}
