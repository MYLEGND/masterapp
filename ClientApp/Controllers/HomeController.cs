using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using AgentPortal.Models;
using Microsoft.AspNetCore.Authorization;
using Shared.Diagnostics;

namespace ClientApp.Controllers;

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
        var model = BuildModel(AppFailureDiagnosticsBuilder.BuildForException(HttpContext, "ClientApp", exception: null));
        return RenderFailure(model);
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult ErrorStatus(int statusCode)
    {
        var model = BuildModel(AppFailureDiagnosticsBuilder.BuildForStatusCode(HttpContext, "ClientApp", statusCode));
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

        // Detailed capture belongs to the central server authority, not the response.
        model.Diagnostics = null;
        Response.Headers.Remove("X-Legend-Failure-Kind");
        Response.Headers.Remove("X-Legend-Failing-Point");
        Response.Headers.Remove("X-Legend-Redirect-Depth");
        Response.Headers["X-Legend-Request-Id"] = model.RequestId;
        Response.Headers.CacheControl = "no-store";

        if (AppFailureDiagnosticsBuilder.RequestPrefersJson(Request))
            return new ObjectResult(new
            {
                error = "request_failed",
                message = "We couldn't complete this request. Please try again.",
                requestId = model.RequestId
            }) { StatusCode = statusCode };

        return View("Error", model);
    }
}
