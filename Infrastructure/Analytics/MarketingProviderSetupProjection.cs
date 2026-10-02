using Shared.Analytics;

namespace Infrastructure.Analytics;

public sealed record MarketingProviderApiKeyRequest(string AdvertiserApiKey, Guid? ExpectedRevision);
public sealed record MarketingProviderRevisionRequest(Guid ConnectionRevision);
public sealed record MarketingMetaSetupSnapshot(bool Available, bool Connected, string? AccountId, string? AccountName,
    string? PixelId, bool CapiConfigured, string? TestEventCode, string? Error);
public sealed record MarketingProviderSetupSnapshot(MarketingMetaSetupSnapshot Meta,
    OpenAiAdsConnectionSnapshot Connection, OpenAiAdsProviderAccountSnapshot? Account,
    OpenAiAdsMeasurementCapabilitySnapshot? Capability, OpenAiMeasurementHealthSnapshot Health,
    MarketingMeasurementEvidenceSnapshot? Evidence, string? OpenAiError, string? EvidenceError,
    PlatformConnectionHealthSnapshot RuntimeHealth);

/// <summary>
/// Public-safe projection of the canonical provider health authority.
///
/// Configuration and stored credentials are never promoted to "connected" on their own.
/// Meta, OpenAI, and Microsoft status come from live provider verification; analytics
/// evidence comes from durable canonical delivery/source records.
/// </summary>
public sealed class MarketingProviderSetupProjection(
    IMetaPixelResolutionService pixels,
    IPlatformConnectionHealthAuthority runtimeHealth)
{
    public async Task<MarketingProviderSetupSnapshot> ReadAsync(
        MarketingOwnerScope owner,
        CancellationToken ct = default)
    {
        var runtime = await runtimeHealth.ReadAsync(owner, ct);

        ResolvedMetaPixelContext pixel;
        string? pixelError = null;
        try
        {
            pixel = await pixels.ResolveForOwnerAsync(owner, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            pixel = new ResolvedMetaPixelContext();
            pixelError = "Meta measurement configuration is unavailable.";
        }

        var metaError = runtime.Meta.Error ?? pixelError;
        var meta = new MarketingMetaSetupSnapshot(
            Available: runtime.Meta.Exists || runtime.Meta.Status == "not_configured",
            Connected: runtime.Meta.ProviderVerified,
            AccountId: runtime.Meta.AccountId,
            AccountName: runtime.Meta.AccountName,
            PixelId: pixel.PixelId,
            CapiConfigured: pixel.HasServerCapiCredentials,
            TestEventCode: pixel.TestEventCode,
            Error: metaError);

        var openAi = runtime.OpenAi;
        var signals = runtime.Signals;
        return new(
            meta,
            openAi.Connection,
            openAi.Account,
            openAi.Capability,
            openAi.Delivery,
            signals.Evidence,
            openAi.Error,
            signals.Status == "evidence_unavailable"
                ? "Measurement evidence unavailable."
                : null,
            runtime);
    }

    public async Task<object> GetAsync(MarketingOwnerScope owner, CancellationToken ct = default)
    {
        var setup = await ReadAsync(owner, ct);
        var connection = setup.Connection;
        var runtime = setup.RuntimeHealth;

        return new
        {
            ownerKey = owner.Key,
            meta = new
            {
                setup.Meta.Available,
                setup.Meta.Connected,
                setup.Meta.AccountId,
                setup.Meta.AccountName,
                setup.Meta.PixelId,
                setup.Meta.CapiConfigured,
                testModeConfigured = !string.IsNullOrWhiteSpace(setup.Meta.TestEventCode),
                setup.Meta.Error,
                providerVerified = runtime.Meta.ProviderVerified,
                providerStatus = runtime.Meta.Status,
                checkedUtc = runtime.Meta.CheckedUtc,
                providerHttpStatus = runtime.Meta.HttpStatusCode
            },
            openAi = new
            {
                connection.Exists,
                connected = runtime.OpenAi.ProviderVerified,
                storedConnected = connection.Connected,
                connection.Revision,
                connection.AccountId,
                connection.AccountName,
                connection.PixelId,
                connection.ConversionDataSourceId,
                connection.PixelConfigured,
                connection.ConversionsApiConfigured,
                connection.LastVerifiedUtc,
                accountStatus = setup.Account?.Status,
                reviewStatus = setup.Account?.ReviewStatus ?? connection.ReviewStatus,
                providerStatusFresh = runtime.OpenAi.ProviderVerified,
                providerStatus = runtime.OpenAi.Status,
                providerCheckedUtc = runtime.OpenAi.CheckedUtc,
                providerError = setup.OpenAiError,
                capabilityStatus = setup.Capability?.Status,
                health = setup.Health
            },
            calendar = new
            {
                connected = runtime.Calendar.ProviderVerified,
                storedConnected = runtime.Calendar.Connection.Connected,
                runtime.Calendar.Connection.Revision,
                runtime.Calendar.Connection.AccountName,
                runtime.Calendar.Connection.Email,
                runtime.Calendar.Connection.AuthorizationMethod,
                runtime.Calendar.Connection.Permissions,
                runtime.Calendar.Connection.ConnectedUtc,
                runtime.Calendar.Connection.LastVerifiedUtc,
                runtime.Calendar.Connection.AccessTokenExpiresUtc,
                providerStatus = runtime.Calendar.Status,
                providerCheckedUtc = runtime.Calendar.CheckedUtc,
                providerHttpStatus = runtime.Calendar.HttpStatusCode,
                providerError = runtime.Calendar.Error
            },
            signals = new
            {
                operational = runtime.Signals.Operational,
                status = runtime.Signals.Status,
                runtime.Signals.FailedDeliveries,
                runtime.Signals.RetryableDeliveries,
                runtime.Signals.PendingDeliveries,
                runtime.Signals.CheckedUtc
            },
            evidence = setup.Evidence,
            evidenceError = setup.EvidenceError
        };
    }
}
