using System.Collections.Generic;
using Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Protect_Website.Controllers;
using ProtectWebsite.Services.Tracking;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Analytics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MarketingBrowserConfigurationTests
{
    [Fact]
    public async Task UnresolvedOwnerDoesNotInheritFounderOrCallEitherProvider()
    {
        var meta = new Mock<IMetaPixelResolutionService>(MockBehavior.Strict);
        var openAi = new Mock<IOpenAiAdsAccountConnectionAuthority>(MockBehavior.Strict);
        var result = await new MarketingBrowserConfigurationService(meta.Object, openAi.Object,
            NullLogger<MarketingBrowserConfigurationService>.Instance).GetAsync(null);
        Assert.Null(result.MetaPixelId);
        Assert.Null(result.OpenAiPixelId);
        meta.VerifyNoOtherCalls();
        openAi.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProviderFailureDoesNotBlockOtherBrowserDestinationAndCredentialsAreNotExposed(bool metaFails)
    {
        var owner = MarketingOwnerScope.Agent(Guid.NewGuid());
        var meta = new Mock<IMetaPixelResolutionService>(MockBehavior.Strict);
        var openAi = new Mock<IOpenAiAdsAccountConnectionAuthority>(MockBehavior.Strict);
        var metaCall = meta.Setup(x => x.ResolveForOwnerAsync(owner, It.IsAny<CancellationToken>()));
        if (metaFails) metaCall.ThrowsAsync(new InvalidOperationException("Meta unavailable"));
        else metaCall.ReturnsAsync(new ResolvedMetaPixelContext { PixelId = "meta-pixel", AccessToken = "must-stay-server-side", PixelOwnerType = MetaPixelOwnerTypes.Agent });
        var openAiCall = openAi.Setup(x => x.GetAsync(owner, It.IsAny<CancellationToken>()));
        if (!metaFails) openAiCall.ThrowsAsync(new InvalidOperationException("OpenAI unavailable"));
        else openAiCall.ReturnsAsync(new OpenAiAdsConnectionSnapshot(owner, true, true, Guid.NewGuid(), "account", "Account",
            "admin", "approved", "api_key", null, null, Array.Empty<string>(), "openai-pixel", "datasource", true, true,
            DateTime.UtcNow, null, DateTime.UtcNow));
        var result = await new MarketingBrowserConfigurationService(meta.Object, openAi.Object,
            NullLogger<MarketingBrowserConfigurationService>.Instance).GetAsync(owner);
        Assert.Equal(metaFails ? null : "meta-pixel", result.MetaPixelId);
        Assert.Equal(metaFails ? "openai-pixel" : null, result.OpenAiPixelId);
        var browserJson = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("must-stay-server-side", browserJson, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessToken", browserJson, StringComparison.Ordinal);
        openAi.Verify(x => x.GetSecretsAsync(It.IsAny<MarketingOwnerScope>(), It.IsAny<CancellationToken>()), Times.Never);
        meta.VerifyAll();
        openAi.VerifyAll();
    }

    [Theory]
    [InlineData("?legendEdit=synthetic-ticket", true)]
    [InlineData("?legendEdit=expired-or-invalid", true)]
    [InlineData("?legendMaterialize=1", true)]
    [InlineData("?legendMaterialize=", true)]
    [InlineData("?unrelated=legendEdit", false)]
    [InlineData("", false)]
    public async Task ProtectTrackingFilter_StudioSkipsDiscardedAdvertisingButPreservesResolvedOwner(string query, bool studio)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = new AgentTrackingProfile
        {
            AgentUserId = "synthetic-agent", AgentUpn = "agent@example.test", Slug = "synthetic-agent", Status = "Active"
        };
        db.AgentTrackingProfiles.Add(profile);
        await db.SaveChangesAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Founder:Upn"] = "founder@example.test"
        }).Build();
        var http = new DefaultHttpContext();
        http.Request.Path = "/a/synthetic-agent/Quote/Auto";
        http.Request.QueryString = new QueryString(query);
        var owner = MarketingOwnerScope.Agent(profile.Id);
        var meta = new Mock<IMetaPixelResolutionService>(MockBehavior.Strict);
        var openAi = new Mock<IOpenAiAdsAccountConnectionAuthority>(MockBehavior.Strict);
        if (!studio)
        {
            meta.Setup(x => x.ResolveForOwnerAsync(owner, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedMetaPixelContext { PixelId = "public-meta", PixelOwnerType = MetaPixelOwnerTypes.Agent });
            openAi.Setup(x => x.GetAsync(owner, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OpenAiAdsConnectionSnapshot(owner, true, true, Guid.NewGuid(), "account", "Account",
                    "admin", "approved", "api_key", null, null, Array.Empty<string>(), "public-openai", "datasource", true, true,
                    DateTime.UtcNow, null, DateTime.UtcNow));
        }
        var controller = new QuoteController(configuration);
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor(), new ModelStateDictionary());
        var context = new ActionExecutingContext(action, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller);
        var filter = new TrackingViewDataFilter(new HttpContextAccessor { HttpContext = http },
            new AgentTrackingResolver(db, NullLogger<AgentTrackingResolver>.Instance), configuration, db,
            new MarketingBrowserConfigurationService(meta.Object, openAi.Object, NullLogger<MarketingBrowserConfigurationService>.Instance));
        var invoked = 0;
        await filter.OnActionExecutionAsync(context, () =>
        {
            invoked++;
            return Task.FromResult(new ActionExecutedContext(action, new List<IFilterMetadata>(), controller));
        });
        Assert.Equal(1, invoked);
        Assert.Equal(profile.Id, controller.ViewData["TrackingProfileId"]);
        Assert.Equal("synthetic-agent", controller.ViewData["TrackingSlug"]);
        Assert.Equal(false, controller.ViewData["IsFounderPath"]);
        Assert.Equal(studio ? null : "public-meta", controller.ViewData["ResolvedMetaPixelId"]);
        Assert.Equal(studio ? null : "public-openai", controller.ViewData["ResolvedOpenAiPixelId"]);
        meta.VerifyAll(); openAi.VerifyAll();
        meta.VerifyNoOtherCalls(); openAi.VerifyNoOtherCalls();
        // Query presence changes presentation only; it never issues editor authority.
        Assert.Empty(http.Response.Headers);
    }

}
