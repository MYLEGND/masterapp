using System;
using System.IO;
using Xunit;

namespace AgentPortal.Tests;

public sealed class GrowthEconomicsOpenAiFeedOnboardingTests
{
    [Fact]
    public void SharedMarketingRegistration_OwnsSteps11To13Services()
    {
        var root = Root();
        var registration = Read(root, "Infrastructure", "Analytics", "MarketingConnectionStore.cs");

        Assert.Contains("IBlendedGrowthEconomicsService, BlendedGrowthEconomicsService", registration, StringComparison.Ordinal);
        Assert.Contains("IOpenAiProductFeedService, OpenAiProductFeedService", registration, StringComparison.Ordinal);
        Assert.Contains("IOpenAiAdsOnboardingService, OpenAiAdsOnboardingService", registration, StringComparison.Ordinal);
        Assert.Contains("IBusinessPublicUrlResolver", registration, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductFeed_ProjectsCanonicalCommerceTruthAndBindsPromotions()
    {
        var root = Root();
        var feed = Read(root, "Infrastructure", "Analytics", "OpenAiProductFeedService.cs");
        var promotion = Read(root, "Infrastructure", "WebsiteEditing", "PromotionOrchestrationService.cs");
        var execution = Read(root, "Infrastructure", "Analytics", "OpenAiAdsExecutionService.cs");

        Assert.Contains("db.CommerceProducts", feed, StringComparison.Ordinal);
        Assert.Contains("CanonicalProductId", feed, StringComparison.Ordinal);
        Assert.Contains("CommerceProductId", feed, StringComparison.Ordinal);
        Assert.Contains("ProviderProductId", feed, StringComparison.Ordinal);
        Assert.Contains("IBusinessPublicUrlResolver", feed, StringComparison.Ordinal);
        Assert.Contains("CreateProductFeedAsync", execution, StringComparison.Ordinal);
        Assert.Contains("UpsertProductFeedItemAsync", execution, StringComparison.Ordinal);

        Assert.Contains("IOpenAiProductFeedService", promotion, StringComparison.Ordinal);
        Assert.Contains("OpenAiProductFeedStatuses.Published", promotion, StringComparison.Ordinal);
        Assert.Contains("Mode: providerFeedId is null ? null : \"product_feed\"", promotion, StringComparison.Ordinal);
        Assert.Contains("new OpenAiAdsProductSetFilter(\"external_id\", \"equals\", [source.SourceId])", promotion, StringComparison.Ordinal);
    }

    [Fact]
    public void GrowthEconomics_UsesUnifiedSpendAndCanonicalOutcomeLineage()
    {
        var root = Root();
        var economics = Read(root, "Infrastructure", "Analytics", "BlendedGrowthEconomicsService.cs");
        var projection = Read(root, "Infrastructure", "Analytics", "CanonicalMarketingOutcomeProjection.cs");

        Assert.Contains("IUnifiedMarketingPerformanceService", economics, StringComparison.Ordinal);
        Assert.Contains("LoadScopedMetaEventsAsync", economics, StringComparison.Ordinal);
        Assert.Contains("CostPerCustomer", Read(root, "SHARED", "Analytics", "GrowthEconomicsAndOpenAiFeedContracts.cs"), StringComparison.Ordinal);
        Assert.Contains("BlendedRoas", Read(root, "SHARED", "Analytics", "GrowthEconomicsAndOpenAiFeedContracts.cs"), StringComparison.Ordinal);
        Assert.Contains("PipelineValue", Read(root, "SHARED", "Analytics", "GrowthEconomicsAndOpenAiFeedContracts.cs"), StringComparison.Ordinal);
        Assert.Contains("OpenAiClickReference.Normalize", projection, StringComparison.Ordinal);
        Assert.Contains("TrafficAttribution.IsMetaAttributedPaid", projection, StringComparison.Ordinal);
    }

    [Fact]
    public void Onboarding_ReadinessIsEvidenceBasedAndSharedUiConsumesIt()
    {
        var root = Root();
        var service = Read(root, "Infrastructure", "Analytics", "OpenAiAdsOnboardingService.cs");
        var view = Read(root, "AgentPortal", "Views", "WebsiteAnalytics", "Index.cshtml");
        var js = Read(root, "AgentPortal", "wwwroot", "js", "website-analytics.js");

        Assert.Contains("connection.PixelConfigured", service, StringComparison.Ordinal);
        Assert.Contains("connection.ConversionsApiConfigured", service, StringComparison.Ordinal);
        Assert.Contains("ListConversionEventSettingsAsync", service, StringComparison.Ordinal);
        Assert.Contains("WebsiteVerifiedAsync", service, StringComparison.Ordinal);
        Assert.Contains("ready_to_advertise", service, StringComparison.Ordinal);

        Assert.Contains("ChatGPT Ads onboarding", view, StringComparison.Ordinal);
        Assert.Contains("ChatGPT Ads product feed", view, StringComparison.Ordinal);
        Assert.Contains("Growth Economics", view, StringComparison.Ordinal);
        Assert.Contains("openAiOnboarding: analyticsEndpoint('/openai-onboarding')", js, StringComparison.Ordinal);
        Assert.Contains("growthEconomics: analyticsEndpoint('/growth-economics')", js, StringComparison.Ordinal);
        Assert.Contains("openAiProductFeedPublish: analyticsEndpoint('/openai-product-feed/publish')", js, StringComparison.Ordinal);
    }

    [Fact]
    public void AppHosts_DoNotOwnParallelSteps11To13Implementations()
    {
        var root = Root();
        foreach (var path in new[]
        {
            Path.Combine(root, "AgentPortal", "Services", "BlendedGrowthEconomicsService.cs"),
            Path.Combine(root, "ParfaitApp", "Services", "BlendedGrowthEconomicsService.cs"),
            Path.Combine(root, "AgentPortal", "Services", "OpenAiProductFeedService.cs"),
            Path.Combine(root, "ParfaitApp", "Services", "OpenAiProductFeedService.cs"),
            Path.Combine(root, "AgentPortal", "Services", "OpenAiAdsOnboardingService.cs"),
            Path.Combine(root, "ParfaitApp", "Services", "OpenAiAdsOnboardingService.cs")
        })
        {
            Assert.False(File.Exists(path), $"Parallel Steps 11-13 authority is forbidden: {path}");
        }
    }

    private static string Read(string root, params string[] parts) =>
        File.ReadAllText(Path.Combine(root, Path.Combine(parts)));

    private static string Root()
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace) && File.Exists(Path.Combine(workspace, "MASTERAPP.sln")))
            return workspace;

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MASTERAPP.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
