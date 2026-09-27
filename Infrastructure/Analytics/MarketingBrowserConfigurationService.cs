using Microsoft.Extensions.Logging;
using Shared.Analytics;

namespace Infrastructure.Analytics;

/// <summary>Public-safe projection only. No token, API key or credential-bearing context can leave this service.</summary>
public sealed record MarketingBrowserConfiguration(string? MetaPixelId = null, string? OpenAiPixelId = null,
    bool OpenAiAccountApproved = false, bool OpenAiConversionsApiConfigured = false, string MetaPixelOwnerType = "none");

public sealed class MarketingBrowserConfigurationService(IMetaPixelResolutionService meta,
    IOpenAiAdsAccountConnectionAuthority openAi, ILogger<MarketingBrowserConfigurationService> logger)
{
    public async Task<MarketingBrowserConfiguration> GetAsync(MarketingOwnerScope? owner, CancellationToken ct = default)
    {
        if (owner is null) return new();
        string? metaPixel = null;
        var metaOwnerType = MetaPixelOwnerTypes.None;
        OpenAiAdsConnectionSnapshot? openAiConnection = null;
        try
        {
            var pixel = await meta.ResolveForOwnerAsync(owner, ct);
            metaPixel = pixel.PixelId;
            metaOwnerType = pixel.PixelOwnerType;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "Meta browser projection unavailable for {Owner}; first-party tracking continues.", owner.Key); }
        try { openAiConnection = await openAi.GetAsync(owner, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "OpenAI browser projection unavailable for {Owner}; first-party tracking continues.", owner.Key); }
        var connected = openAiConnection?.Owner == owner && openAiConnection.Connected;
        return new(metaPixel, connected ? openAiConnection!.PixelId : null,
            connected && openAiConnection!.AccountApproved, connected && openAiConnection!.ConversionsApiConfigured, metaOwnerType);
    }
}
