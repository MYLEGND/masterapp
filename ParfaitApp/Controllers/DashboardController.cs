using Microsoft.AspNetCore.Mvc;

namespace ParfaitApp.Controllers
{
    public class DashboardController : Controller
    {
        public IActionResult Index()
        {
            return View(); // Will return Views/Dashboard/Index.cshtml
        }
    }
}
