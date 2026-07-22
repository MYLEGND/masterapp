using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using AgentPortal.Models;
using AgentPortal.Filters;
using Microsoft.AspNetCore.Authorization;
using Shared.Diagnostics;

namespace AgentPortal.Controllers;

[AssistantBlock]
public class HomeController : Controller
{
    public IActionResult Index()
    {
        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        var model = BuildModel(AppFailureDiagnosticsBuilder.BuildForException(HttpContext, "AgentPortal", exception: null));
        return RenderFailure(model);
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult ErrorStatus(int statusCode)
    {
        var model = BuildModel(AppFailureDiagnosticsBuilder.BuildForStatusCode(HttpContext, "AgentPortal", statusCode));
        return RenderFailure(model);
    }

    private ErrorViewModel BuildModel(AppFailureDiagnostics diagnostics)
    {
        return new ErrorViewModel
        {
            RequestId = diagnostics.RequestId,
            Diagnostics = diagnostics
        };
    }

    private IActionResult RenderFailure(ErrorViewModel model)
    {
        var diagnostics = model.Diagnostics;
        var statusCode = diagnostics?.StatusCode ?? StatusCodes.Status500InternalServerError;
        Response.StatusCode = statusCode;

        if (diagnostics is not null)
        {
            Response.Headers["X-Legend-Failure-Kind"] = diagnostics.FailureKind;
            if (!string.IsNullOrWhiteSpace(diagnostics.FailingPoint))
                Response.Headers["X-Legend-Failing-Point"] = diagnostics.FailingPoint;
        }

        if (diagnostics is not null && AppFailureDiagnosticsBuilder.RequestPrefersJson(Request))
            return new ObjectResult(diagnostics) { StatusCode = statusCode };

        return View("Error", model);
    }
}
