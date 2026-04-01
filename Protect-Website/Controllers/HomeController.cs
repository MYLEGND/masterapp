using Microsoft.AspNetCore.Mvc;

namespace ProtectWebsite.Web.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            return View(); // Views/Home/Index.cshtml
        }
    }
}
