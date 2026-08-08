using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;

namespace Shared.Messaging;

public static class MessagingHubEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the shared messaging hub with the authentication schemes registered
    /// by the hosting application. The hub stays shared; each host remains the
    /// authority for the credentials it supports.
    /// </summary>
    public static IHubEndpointConventionBuilder MapLegendMessagingHub(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        string authenticationSchemes)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationSchemes);

        return endpoints
            .MapHub<MessagingHub>(pattern)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = authenticationSchemes
            });
    }
}
