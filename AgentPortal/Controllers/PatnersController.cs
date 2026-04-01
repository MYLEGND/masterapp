using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers
{
    public class PartnersController : Controller
    {
        public IActionResult Index()
        {
            return View(); // Will return Views/Partners/Index.cshtml
        }
    }
}
