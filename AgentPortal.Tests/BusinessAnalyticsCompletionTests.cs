using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Analytics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class BusinessAnalyticsCompletionTests
{
    [Fact]
    public async Task BusinessCtaMetricsUseStableBackendKey_NotEditablePresentationLabel()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var businessId = Guid.NewGuid();
        var foreignBusinessId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            Event(businessId, now.AddMinutes(-3), "s1", "contact_primary", "Call Us"),
            Event(businessId, now.AddMinutes(-2), "s2", "contact_primary", "Get My Free Estimate"),
            Event(foreignBusinessId, now.AddMinutes(-1), "s3", "contact_primary", "Foreign Label"));
        await db.SaveChangesAsync();

        var analytics = new AnalyticsQueryService(db, new ConfigurationBuilder().Build());
        var range = new TimeRangeRequest
        {
            FromUtc = now.AddHours(-1),
            ToUtc = now.AddHours(1),
            QualityMode = TrafficQualityMode.AllTraffic,
            Label = "test",
            Preset = "custom"
        };
        var scope = ScopeContext.ForBusiness(businessId);

        var ctas = await analytics.GetCtaPerformanceAsync(range, scope);
        var row = Assert.Single(ctas.Rows);
        Assert.Equal("contact_primary", row.ElementKey);
        Assert.Equal(2, row.Clicks);

        var summary = await analytics.GetSummaryAsync(range, scope);
        Assert.Equal("contact_primary", summary.TopCta);
    }

    [Fact]
    public void BusinessAnalyticsUiUsesBackendCapabilitiesAndStableAnalyticsBase()
    {
        var root = RepoRoot();
        var view = File.ReadAllText(Path.Combine(root, "AgentPortal", "Views", "WebsiteAnalytics", "Index.cshtml"));
        var js = File.ReadAllText(Path.Combine(root, "AgentPortal", "wwwroot", "js", "website-analytics.js"));
        var controller = File.ReadAllText(Path.Combine(root, "Infrastructure", "Businesses", "BusinessWorkspaceControllerBase.cs"));

        Assert.Contains("data-can-meta-ads", view, StringComparison.Ordinal);
        Assert.Contains("data-can-ai-review", view, StringComparison.Ordinal);
        Assert.Contains("const analyticsEndpoint = path => analyticsBase + path;", js, StringComparison.Ordinal);
        Assert.Contains("if (!capabilities.aiReview) return;", js, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"analytics/meta-campaigns\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"analytics/meta-connection-status\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"analytics/meta-connect\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"/business/meta-callback\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"analytics/meta-disconnect\")]", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("/business/{businessId:D}/analytics/meta-callback", controller, StringComparison.Ordinal);
        Assert.Contains("ScopeContext.ForBusiness(businessId)", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("MetaAds:DefaultAccountId", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("MetaAds:AccessToken", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedTrackingTreatsButtonTextAsPresentationOnly()
    {
        var tracking = File.ReadAllText(Path.Combine(RepoRoot(), "SHARED", "WebsitePlatform", "tracking.js"));
        Assert.Contains("ElementKey: actionKey", tracking, StringComparison.Ordinal);
        Assert.Contains("WebsiteBindingId: target.dataset.websiteBindingId || actionKey", tracking, StringComparison.Ordinal);
        Assert.Contains("ButtonLabel: target.textContent?.trim() || null", tracking, StringComparison.Ordinal);
        var elementIndex = tracking.IndexOf("ElementKey: actionKey", StringComparison.Ordinal);
        var labelIndex = tracking.IndexOf("ButtonLabel: target.textContent?.trim() || null", StringComparison.Ordinal);
        Assert.True(elementIndex >= 0 && labelIndex > elementIndex);
    }

    [Fact]
    public void BusinessMetaOauthStateUsesOwnerIdentityAndLocalReturnUrl()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MetaAds:AppId"] = "123",
            ["MetaAds:ApiVersion"] = "v21.0"
        }).Build();
        var provider = new EphemeralDataProtectionProvider();
        var oauth = new MarketingMetaAdsOAuthService(
            configuration,
            Mock.Of<IHttpClientFactory>(),
            provider,
            NullLogger<MarketingMetaAdsOAuthService>.Instance);
        var businessId = Guid.NewGuid();
        var url = new Uri(oauth.BuildConnectUrl(
            MarketingOwnerScope.Business(businessId),
            $"/business/{businessId:D}/analytics",
            $"https://client.example.com/business/{businessId:D}/analytics/meta-callback"));
        var parsed = QueryHelpers.ParseQuery(url.Query);
        var stateToken = parsed["state"].ToString();
        Assert.False(string.IsNullOrWhiteSpace(stateToken));

        var protector = provider.CreateProtector("Marketing.MetaAds.OAuthState.v1");
        using var state = JsonDocument.Parse(protector.Unprotect(stateToken!));
        Assert.Equal("business", state.RootElement.GetProperty("OwnerType").GetString());
        Assert.Equal(businessId, state.RootElement.GetProperty("OwnerId").GetGuid());
        Assert.Equal($"/business/{businessId:D}/analytics", state.RootElement.GetProperty("ReturnUrl").GetString());
        Assert.DoesNotContain("display", state.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BusinessMetaOauthRejectsExternalReturnUrl()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MetaAds:AppId"] = "123"
        }).Build();
        var oauth = new MarketingMetaAdsOAuthService(
            configuration,
            Mock.Of<IHttpClientFactory>(),
            new EphemeralDataProtectionProvider(),
            NullLogger<MarketingMetaAdsOAuthService>.Instance);

        Assert.Throws<InvalidOperationException>(() => oauth.BuildConnectUrl(
            MarketingOwnerScope.Business(Guid.NewGuid()),
            "https://evil.example/",
            "https://client.example.com/business/callback"));
    }

    private static AnalyticsEvent Event(Guid businessId, DateTime utc, string sessionId, string elementKey, string label) => new()
    {
        EventId = Guid.NewGuid(),
        ClientEventId = Guid.NewGuid(),
        CommerceBusinessId = businessId,
        EventType = "cta_click",
        EventUtc = utc,
        ReceivedUtc = utc,
        SessionId = sessionId,
        VisitorId = sessionId,
        PageKey = "/",
        ElementKey = elementKey,
        ButtonLabel = label,
        Environment = "production",
        Host = "business.example.com"
    };

    private static string RepoRoot()
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace) && Directory.Exists(Path.Combine(workspace, "Infrastructure")))
            return workspace;

        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "masterapp.sln")) ||
                Directory.Exists(Path.Combine(current, "Infrastructure")))
                return current;
            current = Directory.GetParent(current)?.FullName;
        }
        throw new InvalidOperationException("Repository root was not found.");
    }
}
