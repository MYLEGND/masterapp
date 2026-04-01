using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AgentPortal.Filters;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public class AssistantBlockAttribute : Attribute, IAsyncActionFilter
{
    private static bool WantsHtml(ActionExecutingContext context)
    {
        if (context.HttpContext.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            return false;

        var accept = context.HttpContext.Request.Headers.Accept.ToString();
        return string.IsNullOrWhiteSpace(accept)
            || accept.Contains("text/html", StringComparison.OrdinalIgnoreCase)
            || accept.Contains("*/*", StringComparison.OrdinalIgnoreCase);
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.Items.TryGetValue("IsAssistant", out var isAssistantObj)
            && isAssistantObj is bool isAssistant && isAssistant)
        {
            if (WantsHtml(context))
            {
                var returnUrl = $"{context.HttpContext.Request.Path}{context.HttpContext.Request.QueryString}";
                context.Result = new RedirectToActionResult("Limited", "Access", new
                {
                    reason = "restricted",
                    returnUrl
                });
            }
            else
            {
                context.Result = new ForbidResult();
            }
            return;
        }

        await next();
    }
}
