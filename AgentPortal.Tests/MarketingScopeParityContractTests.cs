using System;
using System.IO;
using System.Linq;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MarketingScopeParityContractTests
{
    [Fact]
    public void SharedAnalyticsClient_RoutesEveryMarketingActionThroughConfiguredScopeBase()
    {
        var js = Read("AgentPortal", "wwwroot", "js", "website-analytics.js");

        Assert.Contains("const analyticsBase = shell?.dataset.analyticsBase || '/WebsiteAnalytics';", js, StringComparison.Ordinal);
        Assert.Contains("const analyticsEndpoint = path => analyticsBase + path;", js, StringComparison.Ordinal);

        foreach (var endpoint in new[]
        {
            "meta-connect",
            "meta-connection-status",
            "meta-disconnect",
            "marketing-setup",
            "openai-connect",
            "openai-refresh",
            "openai-disconnect"
        })
        {
            Assert.Contains($"analyticsEndpoint('/{endpoint}')", js, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("fetch('/WebsiteAnalytics/marketing-setup", js, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch('/WebsiteAnalytics/openai-", js, StringComparison.Ordinal);
    }

    [Fact]
    public void BusinessScope_HasExplicitMarketingRoutesBeforeGenericAnalyticsCatchAll()
    {
        var controller = Read("Infrastructure", "Businesses", "BusinessWorkspaceControllerBase.cs");

        var explicitRoutes = new[]
        {
            "[HttpGet(\"analytics/marketing-setup\")]",
            "[HttpPost(\"analytics/marketing-setup\")]",
            "[HttpPost(\"analytics/openai-connect\")]",
            "[HttpPost(\"analytics/openai-refresh\")]",
            "[HttpPost(\"analytics/openai-disconnect\")]"
        };

        var catchAllIndex = controller.IndexOf("[HttpGet(\"analytics/{**section}\")]", StringComparison.Ordinal);
        Assert.True(catchAllIndex > 0, "Business analytics catch-all route must remain present.");

        foreach (var route in explicitRoutes)
        {
            var routeIndex = controller.IndexOf(route, StringComparison.Ordinal);
            Assert.True(routeIndex >= 0, $"Missing explicit business marketing route: {route}");
            Assert.True(routeIndex < catchAllIndex, $"{route} must be declared before the generic analytics catch-all.");
        }

        Assert.Contains("MarketingOwnerScope.Business(businessId)", controller, StringComparison.Ordinal);
        Assert.Contains("IOpenAiAdsAccountConnectionAuthority", controller, StringComparison.Ordinal);
        Assert.Contains("IOpenAiAdsDirectConnectionService", controller, StringComparison.Ordinal);
        Assert.Contains("MarketingProviderSetupProjection", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void FounderAgentAndBusiness_UseTypedMarketingOwnerScopes_NotHostOrDisplayNameInference()
    {
        var agent = Read("AgentPortal", "Controllers", "WebsiteAnalyticsController.cs");
        var business = Read("Infrastructure", "Businesses", "BusinessWorkspaceControllerBase.cs");

        Assert.Contains("MarketingOwnerScope.Founder", agent, StringComparison.Ordinal);
        Assert.Contains("CanonicalAdvertisingEventProjection.ResolveOwnerAsync", agent, StringComparison.Ordinal);
        Assert.DoesNotContain("MarketingOwnerScope.Agent(", agent, StringComparison.Ordinal);
        Assert.Contains("MarketingOwnerScope.Business(businessId)", business, StringComparison.Ordinal);

        Assert.DoesNotContain("MarketingOwnerScope.Business(Request.Host", business, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MarketingOwnerScope.Business(business.DisplayName", business, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MarketingOwnerScope.Agent(Request.Host", agent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parfait_RemainsBusinessScopedProjectionOfCanonicalMarketingAndAnalyticsAuthorities()
    {
        var controller = Read("ParfaitApp", "Controllers", "CommerceManagementController.cs");
        var analytics = Read("ParfaitApp", "Services", "ParfaitInternalAnalyticsService.cs");
        var tracking = Read("ParfaitApp", "Views", "Shared", "_ParfaitCommerceTracking.cshtml");
        var program = Read("ParfaitApp", "Program.cs");

        Assert.Contains("CanonicalAdvertisingEventProjection.ResolveOwnerAsync", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("MarketingOwnerScope.Business(store.CommerceBusinessId)", controller, StringComparison.Ordinal);
        Assert.Contains("ScopeContext.ForBusiness(businessId)", analytics, StringComparison.Ordinal);
        Assert.Contains("BrowserMarketing.GetAsync(owner", tracking, StringComparison.Ordinal);
        Assert.Contains("CanonicalAdvertisingEventProjection.ResolveOwnerAsync", tracking, StringComparison.Ordinal);
        Assert.DoesNotContain("MarketingConnections.GetStatusAsync", tracking, StringComparison.Ordinal);
        Assert.DoesNotContain("MarketingOwnerScope.Business(parfait.Id)", tracking, StringComparison.Ordinal);
        Assert.Contains("MarketingServiceRegistration.AddMarketingConnections", program, StringComparison.Ordinal);

        Assert.DoesNotContain("ParfaitMetaAdsConnectionStoreAdapter", program, StringComparison.Ordinal);
        Assert.DoesNotContain("IParfaitMetaAdsOAuthService", program, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Root(), "ParfaitApp", "Services", "ParfaitOpenAiMeasurementEventMapper.cs")));
        Assert.False(File.Exists(Path.Combine(Root(), "ParfaitApp", "Services", "ParfaitMarketingConnectionStore.cs")));
    }

    [Fact]
    public void CanonicalConversionDestinationCatalog_CoversEveryServerConversionForEveryScope()
    {
        var canonical = MetaSignalEventCatalog.Definitions
            .Where(x => string.Equals(x.Category, "conversion", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Name)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var destinations = MarketingConversionDestinationCatalog.Definitions
            .Select(x => x.CanonicalEventName)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(canonical, destinations);

        foreach (var eventName in canonical)
        {
            var definition = Assert.Single(
                MarketingConversionDestinationCatalog.Definitions,
                x => string.Equals(x.CanonicalEventName, eventName, StringComparison.OrdinalIgnoreCase));

            Assert.False(string.IsNullOrWhiteSpace(definition.Meta.EventName));
            Assert.False(string.IsNullOrWhiteSpace(definition.OpenAi.EventName));
        }
    }

    [Fact]
    public void AppSpecificScopes_DoNotOwnChatGptAdsExecutionAdapters()
    {
        var root = Root();

        Assert.True(File.Exists(Path.Combine(root, "Infrastructure", "Analytics", "OpenAiAdsExecutionService.cs")));
        Assert.False(File.Exists(Path.Combine(root, "AgentPortal", "Services", "OpenAiAdsService.cs")));
        Assert.False(File.Exists(Path.Combine(root, "ParfaitApp", "Services", "OpenAiAdsService.cs")));
        Assert.False(File.Exists(Path.Combine(root, "ClientApp", "Services", "OpenAiAdsService.cs")));

        var registration = Read("Infrastructure", "Analytics", "MarketingConnectionStore.cs");
        Assert.Contains("IOpenAiAdsExecutionService, OpenAiAdsExecutionService", registration, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSpecificSurfaces_DoNotOwnProviderConversionMappingLogic()
    {
        var agentController = Read("AgentPortal", "Controllers", "WebsiteAnalyticsController.cs");
        var businessController = Read("Infrastructure", "Businesses", "BusinessWorkspaceControllerBase.cs");
        var parfaitController = Read("ParfaitApp", "Controllers", "CommerceManagementController.cs");

        foreach (var source in new[] { agentController, businessController, parfaitController })
        {
            Assert.DoesNotContain("OpenAiMeasurementEventNames.", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MapToMetaStandardEventName", source, StringComparison.Ordinal);
            Assert.DoesNotContain("=> \"lead_created\"", source, StringComparison.Ordinal);
            Assert.DoesNotContain("=> \"order_created\"", source, StringComparison.Ordinal);
        }
    }

    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine(new[] { Root() }.Concat(path).ToArray()))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Root()
    {
        var github = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(github) && File.Exists(Path.Combine(github, "MASTERAPP.sln")))
            return github;

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MASTERAPP.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
