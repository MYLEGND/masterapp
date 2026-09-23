using System.ComponentModel.DataAnnotations;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.WebsiteEditing;

public sealed class BusinessWebsiteProfileInput
{
    public Guid ProfileRevision { get; set; }
    public Guid ConnectionRevision { get; set; }
    [MaxLength(2000)] public string? ShortBio { get; set; }
    public bool BookingEnabled { get; set; }
    [MaxLength(2048)] public string? BookingEmbedUrl { get; set; }
    [MaxLength(2048)] public string? BookingFallbackUrl { get; set; }
    [MaxLength(320)] public string? BookingMailboxId { get; set; }
    [MaxLength(320), EmailAddress] public string? BookingCalendarEmail { get; set; }
    [MaxLength(32)] public string? MetaPixelId { get; set; }
    [MaxLength(100)] public string? MetaTestEventCode { get; set; }
    [MaxLength(8192)] public string? ReplacementCapiToken { get; set; }
}

public sealed record BusinessWebsiteProfileView(BusinessWebsiteProfileInput Settings,
    bool HasSecureCapiToken, string? ConnectedAccount, bool AdsConnected);

/// <summary>Business profile controls use the existing storefront row and central Meta connection.</summary>
public sealed class BusinessWebsiteProfileService(MasterAppDbContext db, MarketingConnectionStore connections)
{
    public async Task<BusinessWebsiteProfileView> GetAsync(Guid businessId, CancellationToken ct = default)
    {
        var settings = await SettingsAsync(businessId, ct);
        if (settings.LegacyProfileImportedUtc is null &&
            await db.CommerceBusinesses.AnyAsync(x => x.Id == businessId && x.Key == "parfait", ct))
            throw new InvalidOperationException("Existing marketing settings must finish migration before they can be edited here.");
        var owner = MarketingOwnerScope.Business(businessId);
        await connections.ImportAsync(owner, null, ct: ct);
        var meta = (await connections.GetStatusAsync(owner, ct))!;
        return new(new()
        {
            ProfileRevision = settings.Revision, ConnectionRevision = meta.Revision,
            ShortBio = settings.ShortBio, BookingEnabled = settings.BookingEnabled,
            BookingEmbedUrl = settings.BookingEmbedUrl, BookingFallbackUrl = settings.BookingFallbackUrl,
            BookingMailboxId = settings.BookingMailboxId, BookingCalendarEmail = settings.BookingCalendarEmail,
            MetaPixelId = meta.PixelId, MetaTestEventCode = meta.TestEventCode
        }, meta.CapiAccessTokenCiphertext != null || meta.AdsAccessTokenCiphertext != null,
            meta.AdAccountName ?? meta.AdAccountId,
            meta.DisconnectedUtc == null && meta.AdsAccessTokenCiphertext != null &&
            (!meta.AccessTokenExpiresUtc.HasValue || meta.AccessTokenExpiresUtc > DateTime.UtcNow));
    }

    public async Task SaveAsync(Guid businessId, BusinessWebsiteProfileInput input, CancellationToken ct = default)
    {
        Validator.ValidateObject(input, new ValidationContext(input), validateAllProperties: true);
        ValidateUrl(input.BookingEmbedUrl);
        ValidateUrl(input.BookingFallbackUrl);
        if (input.BookingEnabled && string.IsNullOrWhiteSpace(input.BookingEmbedUrl) && string.IsNullOrWhiteSpace(input.BookingFallbackUrl))
            throw new ValidationException("Add a booking URL before enabling your scheduler.");
        var settings = await SettingsAsync(businessId, ct);
        if (settings.Revision != input.ProfileRevision) throw new DbUpdateConcurrencyException("The business profile changed. Reload and try again.");
        settings.ShortBio = Clean(input.ShortBio);
        settings.BookingEnabled = input.BookingEnabled;
        settings.BookingEmbedUrl = Clean(input.BookingEmbedUrl);
        settings.BookingFallbackUrl = Clean(input.BookingFallbackUrl);
        settings.BookingMailboxId = Clean(input.BookingMailboxId);
        settings.BookingCalendarEmail = Clean(input.BookingCalendarEmail);
        settings.Revision = Guid.NewGuid();
        settings.UpdatedUtc = DateTime.UtcNow;
        // Both tracked profile and connection changes commit together in the same SaveChanges transaction.
        await connections.SaveSettingsAsync(MarketingOwnerScope.Business(businessId), input.MetaPixelId,
            input.MetaTestEventCode, input.ReplacementCapiToken, input.ConnectionRevision, ct);
    }

    private async Task<CommerceBusinessStorefrontSettings> SettingsAsync(Guid businessId, CancellationToken ct)
    {
        if (!await db.CommerceBusinesses.AnyAsync(x => x.Id == businessId && x.IsActive && x.Status == "Active", ct))
            throw new InvalidOperationException("An active business is required.");
        var row = await db.CommerceBusinessStorefrontSettings.SingleOrDefaultAsync(x => x.CommerceBusinessId == businessId, ct);
        if (row is not null) return row;
        row = new() { CommerceBusinessId = businessId };
        db.CommerceBusinessStorefrontSettings.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            row = await db.CommerceBusinessStorefrontSettings.SingleOrDefaultAsync(x => x.CommerceBusinessId == businessId, ct);
            if (row is null) throw;
        }
        return row;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void ValidateUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.HostNameType != UriHostNameType.Dns ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || !uri.Host.Contains('.') ||
            value.Any(char.IsControl)) throw new ValidationException("Booking URLs must use a public HTTPS address.");
    }
}
