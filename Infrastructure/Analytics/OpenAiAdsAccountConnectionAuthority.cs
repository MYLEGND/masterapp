using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public interface IOpenAiAdsAccountConnectionAuthority
{
    Task<OpenAiAdsConnectionSnapshot> GetAsync(MarketingOwnerScope owner, CancellationToken cancellationToken = default);

    Task<OpenAiAdsConnectionSecrets> GetSecretsAsync(MarketingOwnerScope owner, CancellationToken cancellationToken = default);

    Task<OpenAiAdsConnectionSnapshot> BindVerifiedAsync(
        MarketingOwnerScope owner,
        VerifiedOpenAiAdsAccount verifiedAccount,
        OpenAiAdsConnectionSecrets secrets,
        Guid? expectedRevision = null,
        CancellationToken cancellationToken = default);

    Task<OpenAiAdsConnectionSnapshot> DisconnectAsync(
        MarketingOwnerScope owner,
        Guid expectedRevision,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Sole MasterApp write authority for owner-scoped OpenAI Ads account bindings.
/// It never creates advertiser accounts, manages billing, or accepts browser identity as authority.
/// </summary>
public sealed class OpenAiAdsAccountConnectionAuthority(
    MasterAppDbContext db,
    MarketingCredentialProtector protector) : IOpenAiAdsAccountConnectionAuthority
{
    private const string Provider = MarketingDestinationKeys.OpenAi;

    public async Task<OpenAiAdsConnectionSnapshot> GetAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var row = await db.MarketingConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerKey == owner.Key && x.Provider == Provider, cancellationToken);
        return row is null ? Empty(owner) : Snapshot(owner, row);
    }

    public async Task<OpenAiAdsConnectionSecrets> GetSecretsAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var row = await db.MarketingConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerKey == owner.Key && x.Provider == Provider, cancellationToken);

        if (row is null || row.DisconnectedUtc.HasValue)
            return new();

        return new(
            protector.Unprotect(owner, Provider, row.AdsAccessTokenCiphertext),
            protector.Unprotect(owner, Provider, row.CapiAccessTokenCiphertext));
    }

    public async Task<OpenAiAdsConnectionSnapshot> BindVerifiedAsync(
        MarketingOwnerScope owner,
        VerifiedOpenAiAdsAccount verifiedAccount,
        OpenAiAdsConnectionSecrets secrets,
        Guid? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(verifiedAccount);
        ArgumentNullException.ThrowIfNull(secrets);

        var accountId = Required(verifiedAccount.AccountId, 100, nameof(verifiedAccount.AccountId));
        var accountName = Required(verifiedAccount.AccountName, 300, nameof(verifiedAccount.AccountName));
        var role = string.IsNullOrWhiteSpace(verifiedAccount.Role)
            ? null
            : OpenAiAdsAccountRoles.Normalize(verifiedAccount.Role);
        var review = OpenAiAdsReviewStatuses.Normalize(verifiedAccount.ReviewStatus);
        var authorization = OpenAiAdsAuthorizationMethods.Normalize(verifiedAccount.AuthorizationMethod);
        var userId = Optional(verifiedAccount.ProviderUserId, 200, nameof(verifiedAccount.ProviderUserId));
        var userEmail = Optional(verifiedAccount.ProviderUserEmail, 320, nameof(verifiedAccount.ProviderUserEmail));
        if (userEmail is not null && !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(userEmail))
            throw new ArgumentException("OpenAI Ads provider user email is invalid.", nameof(verifiedAccount.ProviderUserEmail));

        var permissions = NormalizePermissions(verifiedAccount.Permissions);
        var pixelId = Optional(verifiedAccount.PixelId, 200, nameof(verifiedAccount.PixelId));
        var dataSourceId = Optional(verifiedAccount.ConversionDataSourceId, 200, nameof(verifiedAccount.ConversionDataSourceId));
        var verifiedUtc = verifiedAccount.VerifiedUtc ?? DateTime.UtcNow;
        if (verifiedUtc > DateTime.UtcNow.AddMinutes(5))
            throw new ArgumentException("Provider verification time cannot be in the future.", nameof(verifiedAccount.VerifiedUtc));

        var row = await db.MarketingConnections
            .SingleOrDefaultAsync(x => x.OwnerKey == owner.Key && x.Provider == Provider, cancellationToken);
        var isNew = row is null;

        if (row is null)
        {
            if (expectedRevision.HasValue)
                throw new DbUpdateConcurrencyException("OpenAI Ads connection changed. Reload and try again.");

            row = new MarketingConnection
            {
                OwnerKey = owner.Key,
                OwnerType = owner.OwnerType,
                AgentTrackingProfileId = owner.AgentTrackingProfileId,
                CommerceBusinessId = owner.CommerceBusinessId,
                Provider = Provider,
                CreatedUtc = DateTime.UtcNow
            };
            db.MarketingConnections.Add(row);
        }
        else if (expectedRevision.HasValue && row.Revision != expectedRevision.Value)
        {
            throw new DbUpdateConcurrencyException("OpenAI Ads connection changed. Reload and try again.");
        }

        row.AdAccountId = accountId;
        row.AdAccountName = accountName;
        row.ProviderAccountRole = role;
        row.ProviderReviewStatus = review;
        row.ProviderAuthorizationMethod = authorization;
        row.ProviderUserId = userId;
        row.ProviderUserEmail = userEmail;
        row.ProviderPermissionsJson = JsonSerializer.Serialize(permissions);
        row.ProviderPixelId = pixelId;
        row.ProviderDataSourceId = dataSourceId;
        if (isNew || secrets.ManagementApiKey is not null)
            row.AdsAccessTokenCiphertext = protector.Protect(owner, Provider, secrets.ManagementApiKey);
        if (isNew || secrets.ConversionsApiKey is not null)
            row.CapiAccessTokenCiphertext = protector.Protect(owner, Provider, secrets.ConversionsApiKey);
        row.ConnectedUtc = row.ConnectedUtc ?? DateTime.UtcNow;
        row.DisconnectedUtc = null;
        row.LastVerifiedUtc = verifiedUtc;
        Touch(row);

        await db.SaveChangesAsync(cancellationToken);
        return Snapshot(owner, row);
    }

    public async Task<OpenAiAdsConnectionSnapshot> DisconnectAsync(
        MarketingOwnerScope owner,
        Guid expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var row = await db.MarketingConnections
            .SingleOrDefaultAsync(x => x.OwnerKey == owner.Key && x.Provider == Provider, cancellationToken)
            ?? throw new InvalidOperationException("OpenAI Ads is not connected for this owner.");

        if (row.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("OpenAI Ads connection changed. Reload and try again.");

        row.AdsAccessTokenCiphertext = null;
        row.CapiAccessTokenCiphertext = null;
        row.DisconnectedUtc = DateTime.UtcNow;
        Touch(row);
        await db.SaveChangesAsync(cancellationToken);
        return Snapshot(owner, row);
    }

    private static OpenAiAdsConnectionSnapshot Empty(MarketingOwnerScope owner) =>
        new(owner, false, false, Guid.Empty, null, null, null, null, null, null, null, [], null, null,
            false, false, null, null, null);

    private static OpenAiAdsConnectionSnapshot Snapshot(MarketingOwnerScope owner, MarketingConnection row)
    {
        IReadOnlyList<string> permissions = [];
        if (!string.IsNullOrWhiteSpace(row.ProviderPermissionsJson))
        {
            try
            {
                permissions = JsonSerializer.Deserialize<string[]>(row.ProviderPermissionsJson) ?? [];
            }
            catch (JsonException)
            {
                permissions = [];
            }
        }

        return new(
            owner,
            Exists: true,
            Connected: row.ConnectedUtc.HasValue && !row.DisconnectedUtc.HasValue,
            row.Revision,
            row.AdAccountId,
            row.AdAccountName,
            row.ProviderAccountRole,
            row.ProviderReviewStatus,
            row.ProviderAuthorizationMethod,
            row.ProviderUserId,
            row.ProviderUserEmail,
            permissions,
            row.ProviderPixelId,
            row.ProviderDataSourceId,
            !string.IsNullOrWhiteSpace(row.AdsAccessTokenCiphertext),
            !string.IsNullOrWhiteSpace(row.CapiAccessTokenCiphertext),
            row.ConnectedUtc,
            row.DisconnectedUtc,
            row.LastVerifiedUtc);
    }

    private static string Required(string? value, int max, string parameter)
    {
        var normalized = Optional(value, max, parameter);
        return normalized ?? throw new ArgumentException("A value is required.", parameter);
    }

    private static string? Optional(string? value, int max, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > max || normalized.Any(char.IsControl))
            throw new ArgumentException($"Value exceeds the allowed {max} characters or contains control characters.", parameter);
        return normalized;
    }

    private static string[] NormalizePermissions(IReadOnlyCollection<string>? permissions) =>
        (permissions ?? [])
            .Select(value => Optional(value, 120, nameof(permissions)))
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();

    private static void Touch(MarketingConnection row)
    {
        row.UpdatedUtc = DateTime.UtcNow;
        row.Revision = Guid.NewGuid();
    }
}
