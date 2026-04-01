using Microsoft.AspNetCore.Mvc;

namespace ParfaitApp.Controllers
{
    public class AboutController : Controller
    {
        public IActionResult About()
        {
            ViewData["Title"] = "About";
            return View(); // Looks for Views/About/About.cshtml automatically
        }
    }
}

