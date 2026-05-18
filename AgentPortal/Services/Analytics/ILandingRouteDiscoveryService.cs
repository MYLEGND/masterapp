using AgentPortal.Models.Analytics;

namespace AgentPortal.Services.Analytics;

public interface ILandingRouteDiscoveryService
{
    string GetBaseUrl();
    IReadOnlyList<LandingRouteDefinition> GetAllRoutes();
    IReadOnlyList<LandingRouteDefinition> GetActiveRoutes();
}
