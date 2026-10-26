using AgentPortal.Security;
using AgentPortal.Services;
using Infrastructure.Data;

namespace AgentPortal.Middleware;

/// <summary>Server-enforced restricted operator boundary before any human workspace initialization.</summary>
public sealed class ReceptionistMetadataAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IConfiguration configuration)
    {
        var marker = context.GetEndpoint()?.Metadata.GetMetadata<ReceptionistMetadataReadAttribute>();
        var restricted = ReceptionistMetadataAccess.Restricted(context.User, configuration);
        if (marker is null && !restricted) { await next(context); return; }
        Infrastructure.Identity.RestrictedAiIdentity.DisableStatusReexecution(context);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (context.User.Identity?.IsAuthenticated != true)
        { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
        if (!HttpMethods.IsGet(context.Request.Method) || marker is null ||
            FounderImpersonationService.HasRequestedContext(context) ||
            restricted != marker.RestrictedOperator ||
            !restricted && !FounderGuard.IsFounder(context.User))
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        try
        {
            var owner = restricted ? ReceptionistMetadataAccess.RegisteredOwner(context.User, configuration) : null;
            if (restricted && owner is null)
            { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            var db = context.RequestServices.GetRequiredService<MasterAppDbContext>();
            if (!await ReceptionistMetadataAccess.ActiveSubjectAsync(db, context.User, context.RequestAborted))
            { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            ReceptionistMetadataAccess.Grant(context, new(owner, restricted));
            await next(context);
        }
        catch (Exception error) when (error is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            // This branch must not enter the human error page or log arbitrary
            // exception text/claims. No downstream error body is returned to AI.
            if (!context.Response.HasStarted)
            { context.Response.Clear(); context.Response.Headers.CacheControl = "no-store"; context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; }
        }
    }
}
