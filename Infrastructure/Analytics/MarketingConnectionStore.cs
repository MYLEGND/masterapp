using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shared.Analytics;

namespace Infrastructure.Analytics;

internal sealed record MarketingProviderCredential(
    string Provider,
    string PrimarySecret,
    string? AccountId,
    string? AccountName,
    string? AuthorizationMethod,
    string? AuthorizationMetadataJson,
    DateTime? CredentialExpiresUtc,
    Guid Revision);

public sealed class MarketingConnectionStore(MasterAppDbContext db, MarketingCredentialProtector protector)
{
    public Task<bool> ExistsAsync(MarketingOwnerScope owner, CancellationToken ct = default) =>
        ExistsAsync(owner, MarketingDestinationKeys.Meta, ct);

    public Task<bool> ExistsAsync(MarketingOwnerScope owner, string provider, CancellationToken ct = default)
    {
        var key = MarketingDestinationKeys.Normalize(provider);
        return db.MarketingConnections.AnyAsync(x => x.OwnerKey == owner.Key && x.Provider == key, ct);
    }

    public Task<MarketingConnection?> GetStatusAsync(MarketingOwnerScope owner, CancellationToken ct = default) =>
        GetStatusAsync(owner, MarketingDestinationKeys.Meta, ct);

    public Task<MarketingConnection?> GetStatusAsync(MarketingOwnerScope owner, string provider, CancellationToken ct = default)
    {
        var key = MarketingDestinationKeys.Normalize(provider);
        return db.MarketingConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerKey == owner.Key && x.Provider == key, ct);
    }

    public async Task<MarketingProviderConnectionSnapshot> GetProviderConnectionAsync(
        MarketingOwnerScope owner,
        string provider,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var key = MarketingDestinationKeys.Normalize(provider);
        if (key is not (MarketingDestinationKeys.Google or MarketingDestinationKeys.TikTok))
            throw new ArgumentException("Unsupported external ads provider.", nameof(provider));

        var row = await GetStatusAsync(owner, key, ct);
        if (row is null)
            return new(owner, key, false, false, false, false, null, null, null,
                null, null, null, Guid.Empty, "not_connected");

        var connected = row.DisconnectedUtc is null && !string.IsNullOrWhiteSpace(row.AdsAccessTokenCiphertext);
        var ready = connected && !string.IsNullOrWhiteSpace(row.AdAccountId);
        return new(
            owner,
            key,
            true,
            connected,
            ready,
            connected && !ready,
            row.AdAccountId,
            row.AdAccountName,
            row.ProviderAuthorizationMethod,
            row.ConnectedUtc,
            row.LastVerifiedUtc,
            row.AccessTokenExpiresUtc,
            row.Revision,
            ready ? "ready" : connected ? "account_selection_required" : "disconnected");
    }

    internal async Task<MarketingProviderCredential?> GetProviderCredentialAsync(
        MarketingOwnerScope owner,
        string provider,
        CancellationToken ct = default)
    {
        var key = MarketingDestinationKeys.Normalize(provider);
        var row = await GetStatusAsync(owner, key, ct);
        if (row is null || row.DisconnectedUtc.HasValue || string.IsNullOrWhiteSpace(row.AdsAccessTokenCiphertext))
            return null;

        return new(
            key,
            protector.Unprotect(owner, key, row.AdsAccessTokenCiphertext)!,
            row.AdAccountId,
            row.AdAccountName,
            row.ProviderAuthorizationMethod,
            row.ProviderPermissionsJson,
            row.AccessTokenExpiresUtc,
            row.Revision);
    }

    internal async Task SaveProviderCredentialAsync(
        MarketingOwnerScope owner,
        string provider,
        string primarySecret,
        string authorizationMethod,
        string? authorizationMetadataJson,
        DateTime? credentialExpiresUtc,
        string? accountId,
        string? accountName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(primarySecret))
            throw new ArgumentException("A provider credential is required.", nameof(primarySecret));
        var key = MarketingDestinationKeys.Normalize(provider);
        if (key is not (MarketingDestinationKeys.Google or MarketingDestinationKeys.TikTok))
            throw new ArgumentException("Unsupported external ads provider.", nameof(provider));

        var row = await db.MarketingConnections.SingleOrDefaultAsync(
            x => x.OwnerKey == owner.Key && x.Provider == key, ct);
        if (row is null)
        {
            row = New(owner, key);
            db.MarketingConnections.Add(row);
        }

        row.AdsAccessTokenCiphertext = protector.Protect(owner, key, primarySecret);
        row.CapiAccessTokenCiphertext = null;
        row.ProviderAuthorizationMethod = authorizationMethod;
        row.ProviderPermissionsJson = string.IsNullOrWhiteSpace(authorizationMetadataJson)
            ? null : authorizationMetadataJson.Trim();
        row.AccessTokenExpiresUtc = credentialExpiresUtc;
        row.AdAccountId = CleanAccountId(accountId);
        row.AdAccountName = CleanAccountName(accountName);
        row.ProviderAccountRole = "advertiser";
        row.ProviderReviewStatus = string.IsNullOrWhiteSpace(row.AdAccountId)
            ? "account_selection_required" : "ready";
        row.ConnectedUtc ??= DateTime.UtcNow;
        row.LastVerifiedUtc = DateTime.UtcNow;
        row.DisconnectedUtc = null;
        Touch(row);
        await db.SaveChangesAsync(ct);
    }

    internal async Task<MarketingProviderConnectionSnapshot> SelectProviderAccountAsync(
        MarketingOwnerScope owner,
        string provider,
        string accountId,
        string? accountName,
        CancellationToken ct = default)
    {
        var key = MarketingDestinationKeys.Normalize(provider);
        var row = await db.MarketingConnections.SingleOrDefaultAsync(
            x => x.OwnerKey == owner.Key && x.Provider == key, ct)
            ?? throw new InvalidOperationException("Connect the provider before selecting an ad account.");
        if (row.DisconnectedUtc.HasValue || string.IsNullOrWhiteSpace(row.AdsAccessTokenCiphertext))
            throw new InvalidOperationException("Connect the provider before selecting an ad account.");

        row.AdAccountId = CleanAccountId(accountId)
            ?? throw new ArgumentException("A real ad account ID is required.", nameof(accountId));
        row.AdAccountName = CleanAccountName(accountName);
        row.ProviderReviewStatus = "ready";
        row.LastVerifiedUtc = DateTime.UtcNow;
        Touch(row);
        await db.SaveChangesAsync(ct);
        return await GetProviderConnectionAsync(owner, key, ct);
    }

    public async Task DisconnectAsync(
        MarketingOwnerScope owner,
        string provider,
        CancellationToken ct = default)
    {
        var key = MarketingDestinationKeys.Normalize(provider);
        if (key == MarketingDestinationKeys.Meta)
        {
            await DisconnectAsync(owner, ct);
            return;
        }

        var row = await db.MarketingConnections.SingleOrDefaultAsync(
            x => x.OwnerKey == owner.Key && x.Provider == key, ct);
        if (row is null) return;
        row.AdsAccessTokenCiphertext = null;
        row.CapiAccessTokenCiphertext = null;
        row.AdAccountId = row.AdAccountName = null;
        row.ProviderAuthorizationMethod = null;
        row.ProviderPermissionsJson = null;
        row.ProviderAccountRole = null;
        row.ProviderReviewStatus = null;
        row.ProviderUserId = row.ProviderUserEmail = null;
        row.ConnectedUtc = row.AccessTokenExpiresUtc = row.LastVerifiedUtc = null;
        row.DisconnectedUtc = DateTime.UtcNow;
        Touch(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<MetaAdsConnectionRecord?> GetAdsAsync(MarketingOwnerScope owner, CancellationToken ct = default)
    {
        var row = await GetStatusAsync(owner, ct);
        if (row is null || row.DisconnectedUtc.HasValue || row.AccessTokenExpiresUtc <= DateTime.UtcNow || string.IsNullOrWhiteSpace(row.AdsAccessTokenCiphertext)) return null;
        return new MetaAdsConnectionRecord
        {
            AgentTrackingProfileId = owner.AgentTrackingProfileId ?? Guid.Empty,
            AccessToken = protector.Unprotect(owner, row.AdsAccessTokenCiphertext)!,
            AccessTokenExpiresUtc = row.AccessTokenExpiresUtc,
            AccountId = row.AdAccountId, AccountName = row.AdAccountName,
            BusinessId = row.MetaBusinessManagerId, BusinessName = row.MetaBusinessManagerName,
            MetaUserId = row.MetaUserId, MetaUserName = row.MetaUserName,
            ConnectedUtc = row.ConnectedUtc ?? row.CreatedUtc, UpdatedUtc = row.UpdatedUtc
        };
    }

    public async Task ImportAsync(MarketingOwnerScope owner, MetaAdsConnectionRecord? record,
        string? pixelId = null, string? capiToken = null, string? testEventCode = null, CancellationToken ct = default)
    {
        var row = await db.MarketingConnections.SingleOrDefaultAsync(
            x => x.OwnerKey == owner.Key && x.Provider == MarketingDestinationKeys.Meta, ct);
        if (row?.LegacyAdsImportedUtc is not null || row?.DisconnectedUtc is not null) return;
        var isNew = row is null;
        row ??= New(owner);
        if (record is not null) SetAds(row, owner, record);
        if (isNew)
        {
            row.PixelId = pixelId;
            row.TestEventCode = testEventCode;
            row.CapiAccessTokenCiphertext = protector.Protect(owner, capiToken);
            db.MarketingConnections.Add(row);
        }
        row.LegacyAdsImportedUtc = DateTime.UtcNow;
        Touch(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            // The unique owner/provider key arbitrates imports across app instances.
            if ((await GetStatusAsync(owner, ct))?.LegacyAdsImportedUtc is null) throw;
        }
    }

    public async Task ImportProfileAsync(MarketingOwnerScope owner, string? pixelId, string? capiToken,
        string? testCode, CancellationToken ct = default)
    {
        var row = await db.MarketingConnections.SingleOrDefaultAsync(
            x => x.OwnerKey == owner.Key && x.Provider == MarketingDestinationKeys.Meta, ct);
        if (row?.LegacyProfileImportedUtc is not null || row?.DisconnectedUtc is not null) return;
        if (row is null) { row = New(owner); db.MarketingConnections.Add(row); }
        row.PixelId = pixelId;
        row.TestEventCode = testCode;
        // Existing OAuth connection supersedes the token copied into legacy profile storage.
        if (row.AdsAccessTokenCiphertext is null) row.CapiAccessTokenCiphertext = protector.Protect(owner, capiToken);
        row.LegacyProfileImportedUtc = DateTime.UtcNow;
        Touch(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            if ((await GetStatusAsync(owner, ct))?.LegacyProfileImportedUtc is null) throw;
        }
    }

    public async Task SaveAdsAsync(MarketingOwnerScope owner, MetaAdsConnectionRecord record, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(record.AccessToken)) throw new ArgumentException("A Meta credential is required.", nameof(record));
        if (owner.AgentTrackingProfileId.HasValue && record.AgentTrackingProfileId != owner.AgentTrackingProfileId)
            throw new InvalidOperationException("Meta connection owner mismatch.");
        await ImportAsync(owner, null, ct: ct);
        var row = await LoadAsync(owner, ct);
        SetAds(row, owner, record);
        Touch(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveSettingsAsync(MarketingOwnerScope owner, string? pixelId, string? testCode,
        string? replacementCapiToken, Guid expectedRevision, CancellationToken ct = default)
    {
        var row = await LoadAsync(owner, ct);
        if (row.Revision != expectedRevision) throw new DbUpdateConcurrencyException("Marketing settings changed. Reload and try again.");
        var pixel = pixelId?.Trim();
        if (!string.IsNullOrEmpty(pixel) && (pixel.Length > 32 || pixel.Any(c => c < '0' || c > '9')))
            throw new ArgumentException("Pixel ID must contain only digits.", nameof(pixelId));
        if (testCode?.Length > 100) throw new ArgumentException("Test code is too long.", nameof(testCode));
        row.PixelId = string.IsNullOrEmpty(pixel) ? null : pixel;
        row.TestEventCode = string.IsNullOrWhiteSpace(testCode) ? null : testCode.Trim();
        if (replacementCapiToken is not null)
        {
            row.CapiAccessTokenCiphertext = protector.Protect(owner, replacementCapiToken);
            if (row.CapiAccessTokenCiphertext is not null) row.DisconnectedUtc = null;
        }
        Touch(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> GetCapiTokenAsync(MarketingOwnerScope owner, CancellationToken ct = default)
    {
        var row = await GetStatusAsync(owner, ct);
        if (row is null || row.DisconnectedUtc.HasValue) return null;
        if (!string.IsNullOrWhiteSpace(row.CapiAccessTokenCiphertext))
            return protector.Unprotect(owner, row.CapiAccessTokenCiphertext);
        return row.AccessTokenExpiresUtc <= DateTime.UtcNow ? null : protector.Unprotect(owner, row.AdsAccessTokenCiphertext);
    }

    public async Task DisconnectAsync(MarketingOwnerScope owner, CancellationToken ct = default)
    {
        await ImportAsync(owner, null, ct: ct);
        var row = await LoadAsync(owner, ct);
        row.AdsAccessTokenCiphertext = row.CapiAccessTokenCiphertext = null;
        row.AdAccountId = row.AdAccountName = row.MetaBusinessManagerId = row.MetaBusinessManagerName = null;
        row.MetaUserId = row.MetaUserName = null;
        row.ConnectedUtc = row.AccessTokenExpiresUtc = null;
        row.DisconnectedUtc = DateTime.UtcNow;
        row.LegacyAdsImportedUtc = row.LegacyProfileImportedUtc = DateTime.UtcNow;
        Touch(row);
        await db.SaveChangesAsync(ct);
    }

    private Task<MarketingConnection> LoadAsync(MarketingOwnerScope owner, CancellationToken ct) =>
        db.MarketingConnections.SingleAsync(
            x => x.OwnerKey == owner.Key && x.Provider == MarketingDestinationKeys.Meta, ct);

    private static MarketingConnection New(MarketingOwnerScope owner) =>
        New(owner, MarketingDestinationKeys.Meta);

    private static MarketingConnection New(MarketingOwnerScope owner, string provider) => new()
    {
        OwnerKey = owner.Key, OwnerType = owner.OwnerType,
        AgentTrackingProfileId = owner.AgentTrackingProfileId, CommerceBusinessId = owner.CommerceBusinessId,
        Provider = MarketingDestinationKeys.Normalize(provider)
    };

    private static string? CleanAccountId(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Length > 100 || text.Any(char.IsControl))
            throw new ArgumentException("Provider account ID is invalid.", nameof(value));
        return text;
    }

    private static string? CleanAccountName(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Length > 300 || text.Any(char.IsControl))
            throw new ArgumentException("Provider account name is invalid.", nameof(value));
        return text;
    }

    private void SetAds(MarketingConnection row, MarketingOwnerScope owner, MetaAdsConnectionRecord record)
    {
        row.AdsAccessTokenCiphertext = protector.Protect(owner, record.AccessToken);
        row.CapiAccessTokenCiphertext = null;
        row.AccessTokenExpiresUtc = record.AccessTokenExpiresUtc;
        row.AdAccountId = record.AccountId; row.AdAccountName = record.AccountName;
        row.MetaBusinessManagerId = record.BusinessId; row.MetaBusinessManagerName = record.BusinessName;
        row.MetaUserId = record.MetaUserId; row.MetaUserName = record.MetaUserName;
        row.ConnectedUtc = record.ConnectedUtc == default ? DateTime.UtcNow : record.ConnectedUtc;
        row.DisconnectedUtc = null;
    }

    private static void Touch(MarketingConnection row)
    {
        row.UpdatedUtc = DateTime.UtcNow;
        row.Revision = Guid.NewGuid();
    }
}

public static class MarketingServiceRegistration
{
    public static IServiceCollection AddMarketingConnections(this IServiceCollection services)
    {
        services.AddSingleton(sp => MarketingCredentialProtector.CreateShared(
            sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>()));
        services.AddScoped<MarketingConnectionStore>();
        services.AddScoped<MarketingExternalAdsOAuthService>();
        services.AddScoped<IMarketingExternalAdsReportingService, MarketingExternalAdsReportingService>();
        services.AddHttpClient("MarketingExternalAds", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        services.TryAddSingleton<MetaCapiCredentialProtector>();
        services.TryAddScoped<AgentTrackingResolver>();
        services.TryAddScoped<IMetaPixelResolutionService, MetaPixelResolutionService>();
        services.TryAddScoped<MarketingBrowserConfigurationService>();
        services.TryAddScoped<MarketingMeasurementEvidenceService>();
        services.TryAddScoped<MarketingProviderSetupProjection>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMarketingDestination, MetaMarketingDestination>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMarketingDestination, OpenAiMarketingDestination>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMarketingDestination, GoogleMarketingDestination>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMarketingDestination, TikTokMarketingDestination>());
        services.TryAddScoped<IMarketingDestinationRegistry, MarketingDestinationRegistry>();
        services.AddScoped<IOpenAiAdsAccountConnectionAuthority, OpenAiAdsAccountConnectionAuthority>();
        services.AddScoped<Infrastructure.Bookings.IMicrosoftCalendarConnectionAuthority, Infrastructure.Bookings.MicrosoftCalendarConnectionAuthority>();
        services.AddHttpClient<IOpenAiAdsDirectConnectionService, OpenAiAdsDirectConnectionService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddHttpClient<IOpenAiAdsExecutionService, OpenAiAdsExecutionService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<IAdvertisingActionAuthorizationService, AdvertisingActionAuthorizationService>();
        services.AddScoped<Infrastructure.WebsiteEditing.IPromotionOrchestrationService, Infrastructure.WebsiteEditing.PromotionOrchestrationService>();
        services.AddScoped<IAdvertisingCommandCenterService, AdvertisingCommandCenterService>();
        services.AddScoped<Infrastructure.WebsiteEditing.IBusinessPublicUrlResolver, Infrastructure.WebsiteEditing.BusinessPublicUrlResolver>();
        services.AddScoped<IUnifiedMarketingPerformanceService, UnifiedMarketingPerformanceService>();
        services.AddScoped<IMarketingManagerService, MarketingManagerService>();
        services.AddScoped<WebsiteAnalyticsAiDataBuilder>();
        services.TryAddScoped<IAnalyticsQueryService, AnalyticsQueryService>();
        services.TryAddScoped<IMetaSignalAnalyticsService, MetaSignalAnalyticsService>();
        services.AddScoped<IBlendedGrowthEconomicsService, BlendedGrowthEconomicsService>();
        services.AddScoped<IOpenAiProductFeedService, OpenAiProductFeedService>();
        services.AddScoped<IOpenAiAdsOnboardingService, OpenAiAdsOnboardingService>();
        services.AddHttpClient<IOpenAiConversionsApiService, OpenAiConversionsApiService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddHttpClient<IOpenAiMeasurementHealthService, OpenAiMeasurementHealthService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddScoped<MarketingMetaAdsOAuthService>();
        services.TryAddScoped<IMetaAdsConnectionStore, CanonicalMetaAdsConnectionStore>();
        services.TryAddScoped<IMetaAdsService, MetaAdsService>();
        services.AddScoped<AgentMarketingProfileService>();
        services.AddScoped<Infrastructure.Leads.WebsiteIntakeRecipientResolver>();
        return services;
    }

    /// <summary>
    /// Runs durable marketing projection/delivery from the platform control-plane
    /// host. Public website hosts must not register these workers.
    /// </summary>
    public static IServiceCollection AddMarketingBackgroundWorkers(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddScoped<IMetaSendAuthority, MetaSendAuthority>();
        services.Configure<MetaOptions>(configuration.GetSection("Meta"));
        services.Configure<MetaSignalIntelligenceOptions>(configuration.GetSection("MetaSignalIntelligence"));
        services.AddHttpClient<IMetaConversionsApiService, MetaConversionsApiService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddHostedService<MetaSignalAnalyticsBridge>();
        services.AddHostedService<MetaSignalOutcomeDispatcherHostedService>();
        services.AddHostedService<OpenAiConversionDispatcherHostedService>();
        return services;
    }
}
