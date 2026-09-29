using System;
using System.IO;
using Xunit;

namespace AgentPortal.Tests;

public sealed class PlatformProviderHealthHardeningTests
{
    [Fact]
    public void OneAuthority_OwnsLiveProviderAndSignalHealth()
    {
        var authority = Read("Infrastructure", "Analytics", "PlatformConnectionHealthAuthority.cs");
        var projection = Read("Infrastructure", "Analytics", "MarketingProviderSetupProjection.cs");
        var registration = Read("Infrastructure", "Analytics", "MarketingConnectionStore.cs");

        Assert.Contains("IPlatformConnectionHealthAuthority", authority, StringComparison.Ordinal);
        Assert.Contains("VerifyMetaAsync", authority, StringComparison.Ordinal);
        Assert.Contains("act_{accountId}/campaigns", authority, StringComparison.Ordinal);
        Assert.Contains("openAiDirect.InspectAsync", authority, StringComparison.Ordinal);
        Assert.Contains("bookingBusinesses", authority, StringComparison.Ordinal);
        Assert.Contains("evidence.GetAsync", authority, StringComparison.Ordinal);

        Assert.Contains("IPlatformConnectionHealthAuthority runtimeHealth", projection, StringComparison.Ordinal);
        Assert.Contains("runtime.Meta.ProviderVerified", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("connections.GetAdsAsync", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("openAi.GetAsync", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("direct.InspectAsync", projection, StringComparison.Ordinal);

        Assert.Contains(
            "TryAddScoped<IPlatformConnectionHealthAuthority, PlatformConnectionHealthAuthority>()",
            registration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MarketingSetup_NeverPromotesStoredConnectionStateToLiveHealth()
    {
        var controller = Read("AgentPortal", "Controllers", "WebsiteAnalyticsController.cs");
        var business = Read("Infrastructure", "Businesses", "BusinessWorkspaceControllerBase.cs");

        Assert.Contains("var runtime = setup.RuntimeHealth;", controller, StringComparison.Ordinal);
        Assert.Contains("var openAiProviderVerified = runtime.OpenAi.ProviderVerified;", controller, StringComparison.Ordinal);
        Assert.Contains("connected = openAiProviderVerified", controller, StringComparison.Ordinal);
        Assert.Contains("calendarLinked = runtime.Calendar.ProviderVerified", controller, StringComparison.Ordinal);
        Assert.Contains("connected = runtime.Calendar.ProviderVerified", controller, StringComparison.Ordinal);
        Assert.Contains("storedConnected = openAiConnection.Connected", controller, StringComparison.Ordinal);
        Assert.Contains("storedConnected = calendarConnection.Connected", controller, StringComparison.Ordinal);

        Assert.Contains("var runtime = setup.RuntimeHealth;", business, StringComparison.Ordinal);
        Assert.Contains("var openAiProviderVerified = runtime.OpenAi.ProviderVerified;", business, StringComparison.Ordinal);
        Assert.Contains("connected = openAiProviderVerified", business, StringComparison.Ordinal);
        Assert.Contains("calendarLinked = runtime.Calendar.ProviderVerified", business, StringComparison.Ordinal);
        Assert.Contains("connected = runtime.Calendar.ProviderVerified", business, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderCanary_IsAggregateOnlyAndDoesNotExposeCredentialOrAccountData()
    {
        var health = Read("AgentPortal", "Health", "ProviderRuntimeHealthCheck.cs");
        var program = Read("AgentPortal", "Program.cs");

        Assert.Contains("snapshot.Meta.StoredConnected && !snapshot.Meta.ProviderVerified", health, StringComparison.Ordinal);
        Assert.Contains("snapshot.OpenAi.Connection.Connected && !snapshot.OpenAi.ProviderVerified", health, StringComparison.Ordinal);
        Assert.Contains("snapshot.Calendar.Connection.Connected && !snapshot.Calendar.ProviderVerified", health, StringComparison.Ordinal);
        Assert.Contains("HealthCheckResult.Unhealthy", health, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountId", health, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessToken", health, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", health, StringComparison.Ordinal);

        Assert.Contains("AddCheck<AgentPortal.Health.ProviderRuntimeHealthCheck>(\"providers\"", program, StringComparison.Ordinal);
        Assert.Contains("app.MapHealthChecks(\"/providerz\"", program, StringComparison.Ordinal);
        Assert.Contains("check.Tags.Contains(\"provider\")", program, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectRelease_UsesPortalAsProviderCanaryBeforeDownstreamTargets()
    {
        var workflow = Read(".github", "workflows", "all-intentional-direct-release-20260918.yml");

        var portal = workflow.IndexOf("- name: Direct deploy AgentPortal", StringComparison.Ordinal);
        var canary = workflow.IndexOf("- name: Verify canonical provider canary after AgentPortal", StringComparison.Ordinal);
        var client = workflow.IndexOf("- name: Direct deploy ClientApp", StringComparison.Ordinal);
        var protect = workflow.IndexOf("- name: Direct deploy Protect immutable ZIP", StringComparison.Ordinal);
        var parfait = workflow.IndexOf("- name: Direct deploy Parfait", StringComparison.Ordinal);
        var website = workflow.IndexOf("- name: Direct deploy Website immutable ZIP", StringComparison.Ordinal);

        Assert.True(portal >= 0 && canary > portal && client > canary && protect > canary && parfait > canary && website > canary);
        Assert.Contains("https://portal.mylegnd.com/providerz", workflow, StringComparison.Ordinal);
        Assert.Contains("steps.providercanary.outcome == 'success'", workflow, StringComparison.Ordinal);
        Assert.Contains("Canonical provider canary failed after AgentPortal publication", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void SignalHealth_ComesFromDurableDeliveryEvidenceNotConfigurationFlags()
    {
        var authority = Read("Infrastructure", "Analytics", "PlatformConnectionHealthAuthority.cs");
        var evidence = Read("Infrastructure", "Analytics", "MarketingMeasurementEvidenceService.cs");

        Assert.Contains("ReadSignalHealthAsync", authority, StringComparison.Ordinal);
        Assert.Contains("snapshot.Meta.Failed + snapshot.OpenAi.Failed", authority, StringComparison.Ordinal);
        Assert.Contains("snapshot.Meta.Retrying + snapshot.OpenAi.Retrying", authority, StringComparison.Ordinal);
        Assert.Contains("snapshot.Meta.Pending + snapshot.OpenAi.Pending", authority, StringComparison.Ordinal);
        Assert.Contains("MarketingDestinationDelivery", evidence, StringComparison.Ordinal);
        Assert.Contains("MetaSignalEvents", evidence, StringComparison.Ordinal);
        Assert.Contains("AnalyticsQueryService", evidence, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(Root(), Path.Combine(parts)));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "AgentPortal")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
