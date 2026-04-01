using Microsoft.AspNetCore.Authorization;
using AgentPortal.Filters;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers;

[Authorize]
[AssistantBlock]
public class ToolsController : Controller
{
    // GET: /Tools  (and /Tools/Index)
    [HttpGet]
    public IActionResult Index()
    {
        return View(); // renders Views/Tools/Index.cshtml
    }
}
