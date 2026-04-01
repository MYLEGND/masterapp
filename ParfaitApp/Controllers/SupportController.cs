using Microsoft.AspNetCore.Mvc;

namespace ParfaitApp.Controllers
{
    public class SupportController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
