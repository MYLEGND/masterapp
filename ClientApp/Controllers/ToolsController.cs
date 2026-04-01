using Microsoft.AspNetCore.Mvc;

namespace ClientApp.Controllers
{
    public class ToolsController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
