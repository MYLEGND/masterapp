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
}
