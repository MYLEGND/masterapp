using Microsoft.AspNetCore.Mvc;

namespace ProtectWebsite.Web.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            return View(); // Views/Home/Index.cshtml
        }

        [HttpGet("Privacy")]
        [HttpGet("Home/Privacy")]
        public IActionResult Privacy()
        {
            return View(); // Views/Home/Privacy.cshtml
        }

        [HttpGet("Terms")]
        [HttpGet("Home/Terms")]
        public IActionResult Terms()
        {
            return View(); // Views/Home/Terms.cshtml
        }
    }
}
