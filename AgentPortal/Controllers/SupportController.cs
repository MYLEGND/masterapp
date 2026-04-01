using Microsoft.AspNetCore.Mvc;
using AgentPortal.Filters;

namespace AgentPortal.Controllers
{
    [AssistantBlock]
public class SupportController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
