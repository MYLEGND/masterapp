using Microsoft.AspNetCore.Authorization;
using AgentPortal.Filters;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers;

[Authorize]
[AssistantBlock]
public class DashboardController : Controller
{
    // GET: /Dashboard  (and /Dashboard/Index)
    [HttpGet]
    public IActionResult Index()
    {
        return View(); // renders Views/Dashboard/Index.cshtml
    }
}
