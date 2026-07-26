using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AgentPortal.Mobile;

public sealed class MobileApiExceptionFilter : IAsyncExceptionFilter
{
    private readonly ILogger<MobileApiExceptionFilter> _logger;

    public MobileApiExceptionFilter(ILogger<MobileApiExceptionFilter> logger)
    {
        _logger = logger;
    }

    public Task OnExceptionAsync(ExceptionContext context)
    {
        var correlationId = context.HttpContext.TraceIdentifier;
        _logger.LogError(
            context.Exception,
            "Unhandled mobile API request failure. CorrelationId={CorrelationId} Path={Path}",
            correlationId,
            context.HttpContext.Request.Path);

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
        return Task.CompletedTask;
    }
}
