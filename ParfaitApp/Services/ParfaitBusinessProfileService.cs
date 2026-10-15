using System.Text.Json;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;
using Shared.Analytics;

namespace ParfaitApp.Services;

public interface IParfaitBusinessProfileService
{
    Task<ParfaitBusinessProfileViewModel> GetProfileAsync(CancellationToken ct = default);
    Task SaveProfileAsync(ParfaitBusinessProfileViewModel model, CancellationToken ct = default);
    Task<ParfaitMetaAnalyticsSettingsViewModel> GetMetaSettingsAsync(CancellationToken ct = default);
    Task<ParfaitMetaAnalyticsSettingsViewModel> GetMetaSettingsAsync(Guid businessId, CancellationToken ct = default);
    Task SaveMetaSettingsAsync(ParfaitMetaAnalyticsSettingsViewModel model, CancellationToken ct = default);
    Task SaveMetaSettingsAsync(Guid businessId, ParfaitMetaAnalyticsSettingsViewModel model, CancellationToken ct = default);
    Task<ParfaitMetaAdsConnectionStatusDto> GetMetaConnectionStatusAsync(CancellationToken ct = default);
    Task<ParfaitMetaAdsConnectionStatusDto> GetMetaConnectionStatusAsync(Guid businessId, CancellationToken ct = default);
    Task<ParfaitMetaAdsConnectionRecord?> GetMetaConnectionRecordAsync(CancellationToken ct = default);
    Task<ParfaitMetaAdsConnectionRecord?> GetMetaConnectionRecordAsync(Guid businessId, CancellationToken ct = default);
    Task SaveMetaConnectionAsync(ParfaitMetaAdsConnectionRecord record, CancellationToken ct = default);
    Task SaveMetaConnectionAsync(Guid businessId, ParfaitMetaAdsConnectionRecord record, CancellationToken ct = default);
    Task DisconnectMetaAsync(CancellationToken ct = default);
    Task DisconnectMetaAsync(Guid businessId, CancellationToken ct = default);
}

public sealed class ParfaitBusinessProfileService(
    ParfaitStoragePaths storagePaths,
    ParfaitMetaCapiCredentialProtector legacyProtector,
    ParfaitBusinessScopeService businessScope,
    MasterAppDbContext db,
    MarketingConnectionStore connections,
    ILogger<ParfaitBusinessProfileService> logger) : IParfaitBusinessProfileService
{
    private async Task<(CommerceBusiness Business, CommerceBusinessStorefrontSettings Settings)> LoadAsync(CancellationToken ct)
    {
        var business = await businessScope.GetParfaitAsync(ct);
        var owner = MarketingOwnerScope.Business(business.Id);
        var settings = await db.CommerceBusinessStorefrontSettings.SingleOrDefaultAsync(x => x.CommerceBusinessId == business.Id, ct);
        if (settings?.LegacyProfileImportedUtc is not null) return (business, settings);

        // Explicit one-time import. Once committed, the local file is never an active read/write authority.
        var legacy = File.Exists(storagePaths.BusinessProfilePath)
            ? JsonSerializer.Deserialize<ParfaitBusinessProfileStore>(await File.ReadAllTextAsync(storagePaths.BusinessProfilePath, ct))
                ?? throw new InvalidOperationException("The legacy business profile cannot be read.")
            : new ParfaitBusinessProfileStore();
        var token = legacyProtector.Unprotect(legacy.MetaCapiAccessTokenCiphertext, logger);
        if (!string.IsNullOrEmpty(legacy.MetaCapiAccessTokenCiphertext) && string.IsNullOrEmpty(token))
        {
            // An unreadable legacy token is not allowed to hold the entire Parfait app hostage.
            // Import the non-secret profile into the canonical owner-scoped authority and require
            // a fresh Meta reconnect for the credential itself. The legacy file is not retained as
            // an active authority after this migration marker is committed.
            logger.LogWarning(
                "Parfait legacy Meta credential could not be decrypted during canonical migration; " +
                "continuing without the credential so storefront and internal operations remain available.");
        }
        MetaAdsConnectionRecord? record = string.IsNullOrEmpty(token) ? null : new()
        {
            AccessToken = token, AccessTokenExpiresUtc = legacy.MetaAccessTokenExpiresUtc,
            AccountId = legacy.MetaAccountId, AccountName = legacy.MetaAccountName,
            BusinessId = legacy.MetaBusinessId, BusinessName = legacy.MetaBusinessName,
            MetaUserId = legacy.MetaUserId, MetaUserName = legacy.MetaUserName,
            ConnectedUtc = legacy.MetaConnectedUtc ?? DateTime.UtcNow
        };
        await connections.ImportAsync(owner, record, legacy.MetaPixelId, null, legacy.MetaTestEventCode, ct);
        if (settings is null)
        {
            settings = new CommerceBusinessStorefrontSettings { CommerceBusinessId = business.Id };
            db.CommerceBusinessStorefrontSettings.Add(settings);
        }
        settings.GlobalStoreCheckoutUrl = legacy.GlobalStoreCheckoutUrl;
        settings.LegacyProfileImportedUtc = DateTime.UtcNow;
        settings.Revision = Guid.NewGuid();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(settings).State = EntityState.Detached;
            settings = await db.CommerceBusinessStorefrontSettings.SingleOrDefaultAsync(x => x.CommerceBusinessId == business.Id, ct);
            if (settings?.LegacyProfileImportedUtc is null) throw;
        }
        return (business, settings);
    }

    public async Task<ParfaitBusinessProfileViewModel> GetProfileAsync(CancellationToken ct = default)
    {
        var (business, settings) = await LoadAsync(ct);
        return new()
        {
            StoreName = business.DisplayName, BusinessType = business.BusinessType,
            GlobalStoreCheckoutUrl = settings.GlobalStoreCheckoutUrl,
            DomainStatus = "Parfait production profile active", AnalyticsStatus = "Managed in Analytics", TrustStatus = "Managed in Analytics"
        };
    }

    public async Task SaveProfileAsync(ParfaitBusinessProfileViewModel model, CancellationToken ct = default)
    {
        var (_, settings) = await LoadAsync(ct);
        await businessScope.UpdateParfaitIdentityAsync(model.StoreName.Trim(), model.BusinessType.Trim(), ct);
        settings.GlobalStoreCheckoutUrl = model.GlobalStoreCheckoutUrl?.Trim();
        settings.UpdatedUtc = DateTime.UtcNow;
        settings.Revision = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
    }

    private async Task<MarketingConnection> GetOrCreateMarketingConnectionAsync(Guid businessId, CancellationToken ct)
    {
        var owner = MarketingOwnerScope.Business(businessId);
        var row = await connections.GetStatusAsync(owner, ct);
        if (row is not null) return row;

        // The storefront settings import marker and the marketing row are separate durable
        // records. If an interrupted/shared deployment committed the marker first, repair the
        // canonical marketing authority instead of dereferencing a missing row.
        await connections.ImportAsync(owner, null, ct: ct);
        return await connections.GetStatusAsync(owner, ct)
            ?? throw new InvalidOperationException("Parfait marketing connection authority could not be initialized.");
    }

    public async Task<ParfaitMetaAnalyticsSettingsViewModel> GetMetaSettingsAsync(CancellationToken ct = default)
    {
        var (business, _) = await LoadAsync(ct);
        return await GetMetaSettingsAsync(business.Id, ct);
    }

    public async Task<ParfaitMetaAnalyticsSettingsViewModel> GetMetaSettingsAsync(Guid businessId, CancellationToken ct = default)
    {
        await EnsureCanonicalBusinessMarketingAsync(businessId, ct);
        var row = await GetOrCreateMarketingConnectionAsync(businessId, ct);
        var status = Status(row);
        return new()
        {
            MetaPixelId = row.PixelId, MetaTestEventCode = row.TestEventCode,
            HasSecureMetaCapiAccessToken = row.CapiAccessTokenCiphertext != null || row.AdsAccessTokenCiphertext != null,
            HasActiveMetaAdsConnection = status.Connected,
            MetaConnectionLabel = status.Connected ? $"Connected: {row.AdAccountName ?? row.AdAccountId ?? "Meta"}" : status.Message!,
            AccountId = row.AdAccountId, AccountName = row.AdAccountName,
            BusinessId = row.MetaBusinessManagerId, BusinessName = row.MetaBusinessManagerName,
            MetaUserName = row.MetaUserName, ConnectedUtc = row.ConnectedUtc, AccessTokenExpiresUtc = row.AccessTokenExpiresUtc
        };
    }

    public async Task SaveMetaSettingsAsync(ParfaitMetaAnalyticsSettingsViewModel model, CancellationToken ct = default)
    {
        var (business, _) = await LoadAsync(ct);
        await SaveMetaSettingsAsync(business.Id, model, ct);
    }

    public async Task SaveMetaSettingsAsync(Guid businessId, ParfaitMetaAnalyticsSettingsViewModel model, CancellationToken ct = default)
    {
        await EnsureCanonicalBusinessMarketingAsync(businessId, ct);
        var owner = MarketingOwnerScope.Business(businessId);
        var row = await GetOrCreateMarketingConnectionAsync(businessId, ct);
        await connections.SaveSettingsAsync(owner, model.MetaPixelId, model.MetaTestEventCode, null, row.Revision, ct);
    }

    public async Task<ParfaitMetaAdsConnectionStatusDto> GetMetaConnectionStatusAsync(CancellationToken ct = default)
    {
        var (business, _) = await LoadAsync(ct);
        return await GetMetaConnectionStatusAsync(business.Id, ct);
    }

    public async Task<ParfaitMetaAdsConnectionStatusDto> GetMetaConnectionStatusAsync(Guid businessId, CancellationToken ct = default)
    {
        await EnsureCanonicalBusinessMarketingAsync(businessId, ct);
        return Status(await GetOrCreateMarketingConnectionAsync(businessId, ct));
    }

    private static ParfaitMetaAdsConnectionStatusDto Status(MarketingConnection row) => new()
    {
        Connected = row.DisconnectedUtc == null && row.AdsAccessTokenCiphertext != null &&
            (!row.AccessTokenExpiresUtc.HasValue || row.AccessTokenExpiresUtc > DateTime.UtcNow),
        AccountId = row.AdAccountId, AccountName = row.AdAccountName,
        BusinessId = row.MetaBusinessManagerId, BusinessName = row.MetaBusinessManagerName,
        MetaUserName = row.MetaUserName, ConnectedUtc = row.ConnectedUtc, AccessTokenExpiresUtc = row.AccessTokenExpiresUtc,
        Message = "Meta Ads not connected or the connection has expired."
    };

    public async Task<ParfaitMetaAdsConnectionRecord?> GetMetaConnectionRecordAsync(CancellationToken ct = default)
    {
        var (business, _) = await LoadAsync(ct);
        return await GetMetaConnectionRecordAsync(business.Id, ct);
    }

    public async Task<ParfaitMetaAdsConnectionRecord?> GetMetaConnectionRecordAsync(Guid businessId, CancellationToken ct = default)
    {
        await EnsureCanonicalBusinessMarketingAsync(businessId, ct);
        var record = await connections.GetAdsAsync(MarketingOwnerScope.Business(businessId), ct);
        return record is null ? null : new()
        {
            AccessToken = record.AccessToken, AccessTokenExpiresUtc = record.AccessTokenExpiresUtc,
            AccountId = record.AccountId, AccountName = record.AccountName,
            BusinessId = record.BusinessId, BusinessName = record.BusinessName,
            MetaUserId = record.MetaUserId, MetaUserName = record.MetaUserName,
            ConnectedUtc = record.ConnectedUtc, UpdatedUtc = record.UpdatedUtc
        };
    }

    public async Task SaveMetaConnectionAsync(ParfaitMetaAdsConnectionRecord record, CancellationToken ct = default)
    {
        var (business, _) = await LoadAsync(ct);
        await SaveMetaConnectionAsync(business.Id, record, ct);
    }

    public async Task SaveMetaConnectionAsync(Guid businessId, ParfaitMetaAdsConnectionRecord record, CancellationToken ct = default)
    {
        await EnsureCanonicalBusinessMarketingAsync(businessId, ct);
        await connections.SaveAdsAsync(MarketingOwnerScope.Business(businessId), new()
        {
            AccessToken = record.AccessToken, AccessTokenExpiresUtc = record.AccessTokenExpiresUtc,
            AccountId = record.AccountId, AccountName = record.AccountName,
            BusinessId = record.BusinessId, BusinessName = record.BusinessName,
            MetaUserId = record.MetaUserId, MetaUserName = record.MetaUserName,
            ConnectedUtc = record.ConnectedUtc, UpdatedUtc = record.UpdatedUtc
        }, ct);
    }

    public async Task DisconnectMetaAsync(CancellationToken ct = default)
    {
        var (business, _) = await LoadAsync(ct);
        await DisconnectMetaAsync(business.Id, ct);
    }

    public async Task DisconnectMetaAsync(Guid businessId, CancellationToken ct = default)
    {
        await EnsureCanonicalBusinessMarketingAsync(businessId, ct);
        await connections.DisconnectAsync(MarketingOwnerScope.Business(businessId), ct);
    }

    private async Task EnsureCanonicalBusinessMarketingAsync(Guid businessId, CancellationToken ct)
    {
        if (businessId == Guid.Empty ||
            !await db.CommerceBusinesses.AsNoTracking().AnyAsync(
                business => business.Id == businessId && business.IsActive && business.Status == "Active",
                ct))
            throw new InvalidOperationException("An active commerce business is required.");

        var isParfait = await db.CommerceBusinesses.AsNoTracking()
            .AnyAsync(business => business.Id == businessId && business.Key == ParfaitBusinessScopeService.ParfaitBusinessKey, ct);
        if (isParfait)
        {
            await LoadAsync(ct);
            return;
        }

        await connections.ImportAsync(MarketingOwnerScope.Business(businessId), null, ct: ct);
    }

    private sealed class ParfaitBusinessProfileStore
    {
        public string? GlobalStoreCheckoutUrl { get; set; }
        public string? MetaPixelId { get; set; }
        public string? MetaTestEventCode { get; set; }
        public string? MetaCapiAccessTokenCiphertext { get; set; }
        public DateTime? MetaConnectedUtc { get; set; }
        public DateTime? MetaAccessTokenExpiresUtc { get; set; }
        public string? MetaAccountId { get; set; }
        public string? MetaAccountName { get; set; }
        public string? MetaBusinessId { get; set; }
        public string? MetaBusinessName { get; set; }
        public string? MetaUserId { get; set; }
        public string? MetaUserName { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}
