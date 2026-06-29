using Microsoft.AspNetCore.Mvc;

namespace ParfaitApp.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            ViewData["Title"] = "Home";  // Sets the page title
            return View();               // Looks for Views/Home/Index.cshtml automatically
        }

        [HttpGet("Privacy")]
        [HttpGet("Home/Privacy")]
        public IActionResult Privacy()
        {
            return View();
        }

        [HttpGet("Terms")]
        [HttpGet("Home/Terms")]
        public IActionResult Terms()
        {
            return View();
        }
    }
}
