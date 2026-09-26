using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public sealed class MarketingConnectionStore(MasterAppDbContext db, MarketingCredentialProtector protector)
{
    public Task<bool> ExistsAsync(MarketingOwnerScope owner, CancellationToken ct = default) =>
        db.MarketingConnections.AnyAsync(x => x.OwnerKey == owner.Key && x.Provider == "meta", ct);

    public Task<MarketingConnection?> GetStatusAsync(MarketingOwnerScope owner, CancellationToken ct = default) =>
        db.MarketingConnections.AsNoTracking().SingleOrDefaultAsync(x => x.OwnerKey == owner.Key && x.Provider == "meta", ct);

    public async Task<MetaAdsConnectionRecord?> GetAdsAsync(MarketingOwnerScope owner, CancellationToken ct = default)
    {
        var row = await GetStatusAsync(owner, ct);
        if (row is null || row.DisconnectedUtc.HasValue || string.IsNullOrWhiteSpace(row.AdsAccessTokenCiphertext)) return null;
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
        var row = await db.MarketingConnections.SingleOrDefaultAsync(x => x.OwnerKey == owner.Key && x.Provider == "meta", ct);
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
        var row = await db.MarketingConnections.SingleOrDefaultAsync(x => x.OwnerKey == owner.Key && x.Provider == "meta", ct);
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
        return row is null || row.DisconnectedUtc.HasValue ? null : protector.Unprotect(owner,
            row.CapiAccessTokenCiphertext ?? row.AdsAccessTokenCiphertext);
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
        db.MarketingConnections.SingleAsync(x => x.OwnerKey == owner.Key && x.Provider == "meta", ct);

    private static MarketingConnection New(MarketingOwnerScope owner) => new()
    {
        OwnerKey = owner.Key, OwnerType = owner.OwnerType,
        AgentTrackingProfileId = owner.AgentTrackingProfileId, CommerceBusinessId = owner.CommerceBusinessId
    };

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
        services.AddScoped<MarketingMetaAdsOAuthService>();
        services.AddScoped<AgentMarketingProfileService>();
        services.AddScoped<Infrastructure.Leads.WebsiteIntakeRecipientResolver>();
        return services;
    }
}
