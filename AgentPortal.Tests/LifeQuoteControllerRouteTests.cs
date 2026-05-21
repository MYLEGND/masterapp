using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Infrastructure.Leads;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Protect_Website.Controllers;
using ProtectWebsite.Services.Meta;
using ProtectWebsite.Services.MetaSignal;
using ProtectWebsite.Services.Tracking;
using Xunit;

namespace AgentPortal.Tests;

public class LifeQuoteControllerRouteTests
{
    [Fact]
    public async Task LifeQuote_WithOfferOverride_RedirectsToCanonicalOfferRouteAndPreservesAttribution()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var resolver = new AgentTrackingResolver(db, NullLogger<AgentTrackingResolver>.Instance);
        var controller = new LifeQuoteController(
            BuildConfig(),
            resolver,
            db,
            Mock.Of<IMetaConversionsApiService>(),
            Mock.Of<IMetaPixelResolutionService>(),
            Mock.Of<IMetaSignalIntelligenceService>(),
            Mock.Of<IWebsiteLifeLeadCaptureService>(),
            NullLogger<LifeQuoteController>.Instance);

        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString("?offer=mortgage&utm_source=facebook&utm_campaign=life_leads_wa_v2");
        http.Items["TrackingSlug"] = "legend";
        http.Items["IsFounderPath"] = false;
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        var result = await controller.LifeQuote("mortgage");

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.False(redirect.Permanent);
        Assert.NotNull(redirect.Url);

        var redirectUri = new Uri($"https://example.test{redirect.Url}");
        Assert.Equal("/a/legend/Quote/Mortgage-Protection", redirectUri.AbsolutePath);

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(redirectUri.Query);
        Assert.False(query.ContainsKey("offer"));
        Assert.Equal("facebook", query["utm_source"]);
        Assert.Equal("life_leads_wa_v2", query["utm_campaign"]);
    }

    private static IConfiguration BuildConfig()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureAd:TenantId"] = "tenant",
                ["AzureAd:ClientId"] = "client",
                ["AzureAd:ClientSecret"] = "secret",
                ["Contact:RecipientEmail"] = "team@example.test",
                ["Tracking:ApiBase"] = "https://portal.example.test"
            })
            .Build();
    }
}
