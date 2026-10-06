using Shared.Analytics;

namespace Infrastructure.Analytics;

public sealed record MarketingProviderApiKeyRequest(string AdvertiserApiKey, Guid? ExpectedRevision);
public sealed record MarketingProviderRevisionRequest(Guid ConnectionRevision);
public sealed record MarketingMetaSetupSnapshot(bool Available, bool Connected, string? AccountId, string? AccountName,
    string? PixelId, bool CapiConfigured, string? TestEventCode, string? Error);
public sealed record MarketingProviderSetupSnapshot(
    MarketingMetaSetupSnapshot Meta,
    MarketingProviderConnectionSnapshot Google,
    MarketingProviderConnectionSnapshot TikTok,
    MarketingProviderMeasurementConfiguration GoogleMeasurement,
    MarketingProviderMeasurementConfiguration TikTokMeasurement,
    OpenAiAdsConnectionSnapshot Connection, OpenAiAdsProviderAccountSnapshot? Account,
    OpenAiAdsMeasurementCapabilitySnapshot? Capability, OpenAiMeasurementHealthSnapshot Health,
    MarketingMeasurementEvidenceSnapshot? Evidence, string? OpenAiError, string? EvidenceError);

/// <summary>Public-safe, independently available projections of canonical provider authorities.</summary>
public sealed class MarketingProviderSetupProjection(MarketingConnectionStore connections,
    IMetaPixelResolutionService pixels, IOpenAiAdsAccountConnectionAuthority openAi,
    IOpenAiAdsDirectConnectionService direct, IOpenAiMeasurementHealthService health,
    MarketingMeasurementEvidenceService evidence)
{
    public async Task<MarketingProviderSetupSnapshot> ReadAsync(MarketingOwnerScope owner, CancellationToken ct = default)
    {
        var meta = new MarketingMetaSetupSnapshot(false, false, null, null, null, false, null, "Meta status unavailable.");
        try
        {
            var ads = await connections.GetAdsAsync(owner, ct);
            var pixel = await pixels.ResolveForOwnerAsync(owner, ct);
            meta = new(true, ads is not null, ads?.AccountId, ads?.AccountName, pixel.PixelId,
                pixel.HasServerCapiCredentials, pixel.TestEventCode, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { /* One unavailable destination does not hide the other. */ }
        var google = await SafeExternalAsync(owner, MarketingDestinationKeys.Google, ct);
        var tiktok = await SafeExternalAsync(owner, MarketingDestinationKeys.TikTok, ct);
        var googleMeasurement = await SafeExternalMeasurementAsync(owner, MarketingDestinationKeys.Google, google.Revision, ct);
        var tiktokMeasurement = await SafeExternalMeasurementAsync(owner, MarketingDestinationKeys.TikTok, tiktok.Revision, ct);
        var connection = new OpenAiAdsConnectionSnapshot(owner, false, false, Guid.Empty, null, null, null, null, null, null, null, [], null, null, false, false, null, null, null);
        OpenAiAdsProviderAccountSnapshot? account = null;
        OpenAiAdsMeasurementCapabilitySnapshot? capability = null;
        var measurement = new OpenAiMeasurementHealthSnapshot(owner, false, false, false, null, 0, 0, 0, 0, null, false, 0, "unavailable");
        string? openAiError = null;
        try { connection = await openAi.GetAsync(owner, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { openAiError = "OpenAI connection status unavailable."; }
        if (connection.Connected)
        {
            try
            {
                account = await direct.InspectAsync(owner, ct);
                capability = await direct.InspectMeasurementAsync(owner, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { openAiError = "Provider status unavailable; stored connection shown."; }
        }
        try { measurement = await health.GetAsync(owner, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { openAiError = "OpenAI delivery health unavailable; stored connection shown."; }
        MarketingMeasurementEvidenceSnapshot? observed = null;
        string? evidenceError = null;
        try { observed = await evidence.GetAsync(owner, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { evidenceError = "Measurement evidence unavailable."; }
        return new(meta, google, tiktok, googleMeasurement, tiktokMeasurement,
            connection, account, capability, measurement, observed, openAiError, evidenceError);
    }

    public async Task<object> GetAsync(MarketingOwnerScope owner, CancellationToken ct = default)
    {
        var setup = await ReadAsync(owner, ct);
        var connection = setup.Connection;
        return new
        {
            ownerKey = owner.Key,
            meta = new { setup.Meta.Available, setup.Meta.Connected, setup.Meta.AccountId, setup.Meta.AccountName,
                setup.Meta.PixelId, setup.Meta.CapiConfigured, testModeConfigured = !string.IsNullOrWhiteSpace(setup.Meta.TestEventCode), setup.Meta.Error },
            google = External(setup.Google, setup.GoogleMeasurement),
            tiktok = External(setup.TikTok, setup.TikTokMeasurement),
            openAi = new { connection.Exists, connection.Connected, connection.Revision, connection.AccountId, connection.AccountName,
                connection.PixelId, connection.ConversionDataSourceId, connection.PixelConfigured,
                connection.ConversionsApiConfigured, connection.LastVerifiedUtc,
                accountStatus = setup.Account?.Status, reviewStatus = setup.Account?.ReviewStatus ?? connection.ReviewStatus,
                providerStatusFresh = setup.Account is not null, providerError = setup.OpenAiError,
                capabilityStatus = setup.Capability?.Status, health = setup.Health },
            evidence = setup.Evidence, evidenceError = setup.EvidenceError
        };
    }

    private async Task<MarketingProviderConnectionSnapshot> SafeExternalAsync(
        MarketingOwnerScope owner,
        string provider,
        CancellationToken ct)
    {
        try
        {
            return await connections.GetProviderConnectionAsync(owner, provider, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return new(owner, provider, false, false, false, false, null, null, null,
                null, null, null, Guid.Empty, "status_unavailable");
        }
    }

    private async Task<MarketingProviderMeasurementConfiguration> SafeExternalMeasurementAsync(
        MarketingOwnerScope owner,
        string provider,
        Guid revision,
        CancellationToken ct)
    {
        try
        {
            return await connections.GetProviderMeasurementConfigurationAsync(owner, provider, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return new(owner, provider, null,
                provider == MarketingDestinationKeys.Google ? "click" : "web",
                [], revision, false, "measurement_status_unavailable");
        }
    }

    public static object External(
        MarketingProviderConnectionSnapshot connection,
        MarketingProviderMeasurementConfiguration measurement) => new
    {
        connection.Provider,
        connection.Exists,
        connection.Connected,
        connection.Ready,
        connection.RequiresAccountSelection,
        connection.AccountId,
        connection.AccountName,
        connection.AuthorizationMethod,
        connection.ConnectedUtc,
        connection.LastVerifiedUtc,
        connection.CredentialExpiresUtc,
        connection.Revision,
        connection.Status,
        optimizationReady = measurement.MappingReady,
        measurementStatus = measurement.Status,
        measurementEventSourceId = measurement.EventSourceId,
        measurementEventSourceType = measurement.EventSourceType,
        measurementMappings = measurement.Mappings
    };
}
