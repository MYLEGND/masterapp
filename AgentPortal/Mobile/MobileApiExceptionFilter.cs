using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Shared.Diagnostics;

namespace AgentPortal.Mobile;

public sealed class MobileApiExceptionFilter : IAsyncExceptionFilter
{
    private readonly ILogger<MobileApiExceptionFilter> _logger;

    public MobileApiExceptionFilter(ILogger<MobileApiExceptionFilter> logger)
    {
        _logger = logger;
    }

    public async Task OnExceptionAsync(ExceptionContext context)
    {
        var correlationId = context.HttpContext.TraceIdentifier;
        await LegendFailureDiagnosticsExtensions.CaptureExceptionAsync(
            context.HttpContext, context.Exception, logger: _logger);

        context.Result = new ObjectResult(new MobileApiErrorResponse(
            "mobile_request_failed",
            "The mobile service could not complete this request.",
            correlationId,
            new Dictionary<string, string[]>()))
        {
            StatusCode = StatusCodes.Status500InternalServerError,
            ContentTypes = { "application/json" }
        };
        context.HttpContext.Response.Headers["X-Correlation-ID"] = correlationId;
        context.ExceptionHandled = true;
    }
}
