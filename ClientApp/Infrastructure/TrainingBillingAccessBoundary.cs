using ClientApp.Services;

namespace ClientApp.Infrastructure;

/// <summary>Only explicitly designated billing/authentication endpoints admit training-only identities.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class TrainingBillingRouteAttribute(bool authentication = false) : Attribute
{
    public bool Authentication { get; } = authentication;
}

public sealed class TrainingBillingAccessBoundary(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ClientIdentityAccessService identities)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var profile = await identities.ResolveTrainingBillingOnlyProfileAsync(context.User, context.RequestAborted);
            var marker = context.User.FindFirst(ClientIdentityAccessService.TrainingBillingProfileClaim);
            if (marker is not null || profile is not null)
            {
                var route = context.GetEndpoint()?.Metadata.GetMetadata<TrainingBillingRouteAttribute>();
                if (route is null || (!route.Authentication &&
                    (profile is null || (marker is not null &&
                        (!Guid.TryParse(marker.Value, out var boundProfileId) || boundProfileId != profile.Id)) ||
                    await identities.FindTrainingBillingSubscriptionAsync(profile.Id, cancellationToken: context.RequestAborted) is null)))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.Headers.CacheControl = "no-store";
                    return;
                }
            }
        }
        await next(context);
    }
}
