using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;
using Shared.Meta;

namespace Infrastructure.Analytics;

/// <summary>Moves legacy agent marketing fields into the common connection authority exactly once.</summary>
public sealed class AgentMarketingProfileService(MasterAppDbContext db, MarketingConnectionStore connections,
    IDataProtectionProvider legacyProvider)
{
    public async Task<AgentProfile?> ResolveProfileAsync(
        AgentTrackingProfile tracking,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        var normalizedUpn = string.IsNullOrWhiteSpace(tracking.AgentUpn) ? null : tracking.AgentUpn.Trim().ToUpperInvariant();
        var profiles = await db.AgentProfiles
            .Where(profile =>
                (!string.IsNullOrWhiteSpace(tracking.AgentUserId) && profile.AgentUserId == tracking.AgentUserId) ||
                (normalizedUpn != null && (profile.NormalizedEmail == normalizedUpn || profile.AgentUpn == tracking.AgentUpn)))
            .OrderByDescending(profile => profile.AgentUserId == tracking.AgentUserId)
            .ThenByDescending(profile => profile.UpdatedUtc)
            .ToListAsync(cancellationToken);

        var profile = profiles.FirstOrDefault();
        if (profile is not null || !createIfMissing) return profile;

        profile = new AgentProfile
        {
            AgentUserId = tracking.AgentUserId ?? string.Empty,
            AgentUpn = tracking.AgentUpn ?? string.Empty,
            NormalizedEmail = normalizedUpn,
            FullName = tracking.DisplayName,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.AgentProfiles.Add(profile);
        return profile;
    }

    public async Task<MarketingConnection> GetAsync(AgentTrackingProfile tracking, MarketingOwnerScope owner, CancellationToken ct = default)
    {
        if (owner == MarketingOwnerScope.Agent(tracking.Id)) return await GetAsync(tracking, ct);
        if (owner != MarketingOwnerScope.Founder)
            throw new InvalidOperationException("The profile does not identify this marketing owner.");
        // Founder initialization never imports another owner's credentials or settings.
        if (await connections.GetStatusAsync(owner, ct) is not { } row)
        {
            await connections.ImportProfileAsync(owner, null, null, null, ct);
            row = (await connections.GetStatusAsync(owner, ct))!;
        }
        return row;
    }

    public async Task SaveMarketingAsync(AgentTrackingProfile tracking, MarketingOwnerScope owner,
        Infrastructure.WebsiteEditing.BusinessWebsiteProfileInput input, CancellationToken cancellationToken = default)
    {
        if (owner != MarketingOwnerScope.Agent(tracking.Id) && owner != MarketingOwnerScope.Founder)
            throw new InvalidOperationException("The profile does not identify this marketing owner.");
        var pixel = string.IsNullOrWhiteSpace(input.MetaPixelId) ? null : input.MetaPixelId.Trim();
        if (pixel is not null && (pixel.Length > 32 || pixel.Any(ch => ch < '0' || ch > '9')))
            throw new System.ComponentModel.DataAnnotations.ValidationException("Meta Pixel ID must contain only digits.");
        var testEventCode = string.IsNullOrWhiteSpace(input.MetaTestEventCode) ? null : input.MetaTestEventCode.Trim();
        if (testEventCode is not null && testEventCode.Length > 100)
            throw new System.ComponentModel.DataAnnotations.ValidationException("Meta Test Event Code is too long.");

        static string? Clean(string? value, int max) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];

        static bool ValidHttpUrl(string? value) =>
            string.IsNullOrWhiteSpace(value) ||
            (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
             (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp));

        var embed = Clean(input.BookingEmbedUrl, 2048);
        var fallback = Clean(input.BookingFallbackUrl, 2048);
        var mailbox = Clean(input.BookingMailboxId, 320);
        var calendarEmail = Clean(input.BookingCalendarEmail, 320);

        if (!ValidHttpUrl(embed) || !ValidHttpUrl(fallback))
            throw new System.ComponentModel.DataAnnotations.ValidationException("Booking URLs must be valid HTTP or HTTPS URLs.");
        if (calendarEmail is not null && !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(calendarEmail))
            throw new System.ComponentModel.DataAnnotations.ValidationException("Enter a valid calendar email.");

        var profile = await ResolveProfileAsync(tracking, createIfMissing: true, cancellationToken)
            ?? throw new InvalidOperationException("Agent profile could not be resolved.");
        var hasBookingValues = embed is not null || fallback is not null || mailbox is not null || calendarEmail is not null;
        profile.BookingEnabled = input.BookingEnabled ? true : hasBookingValues ? false : null;
        profile.MicrosoftBookingsEmbedUrl = embed;
        profile.FallbackBookingUrl = fallback;
        profile.BookingPageIdOrMailbox = mailbox;
        profile.CalendarEmail = calendarEmail;
        profile.PreferModalOnMobile = false;
        profile.UpdatedUtc = DateTime.UtcNow;

        if (owner == MarketingOwnerScope.Founder)
            await connections.SaveSettingsAsync(owner, pixel, testEventCode, null, input.ConnectionRevision, cancellationToken);
        else if (owner == MarketingOwnerScope.Agent(tracking.Id))
            await SavePixelAsync(tracking, pixel, testEventCode, input.ConnectionRevision, cancellationToken);
        else
            throw new InvalidOperationException("The profile does not identify this marketing owner.");
    }

    public async Task SavePixelAsync(AgentTrackingProfile tracking, string? pixelId, string? testEventCode, Guid revision, CancellationToken ct = default)
    {
        await GetAsync(tracking, ct);
        await connections.SaveSettingsAsync(MarketingOwnerScope.Agent(tracking.Id), pixelId, testEventCode, null, revision, ct);
    }

    public async Task<MarketingConnection> GetAsync(AgentTrackingProfile tracking, CancellationToken ct = default)
    {
        var owner = MarketingOwnerScope.Agent(tracking.Id);
        var row = await connections.GetStatusAsync(owner, ct);
        if (row?.LegacyProfileImportedUtc is not null || row?.DisconnectedUtc is not null) return row!;
        var upn = tracking.AgentUpn?.Trim().ToUpperInvariant();
        var profiles = await db.AgentProfiles.AsNoTracking().Where(p =>
            (!string.IsNullOrEmpty(tracking.AgentUserId) && p.AgentUserId == tracking.AgentUserId) ||
            (!string.IsNullOrEmpty(upn) && (p.NormalizedEmail == upn || p.AgentUpn == tracking.AgentUpn))).ToListAsync(ct);
        var profile = profiles.OrderByDescending(p => !string.IsNullOrWhiteSpace(p.MetaPixelId))
            .ThenByDescending(p => !string.IsNullOrWhiteSpace(p.MetaCapiAccessToken))
            .ThenByDescending(p => p.AgentUserId == tracking.AgentUserId).ThenByDescending(p => p.UpdatedUtc).FirstOrDefault();
        string? token = null;
        // A canonical OAuth Meta Ads connection supersedes legacy profile CAPI
        // storage. Never decrypt or re-import an obsolete legacy secret when a
        // durable OAuth credential already owns delivery.
        if (row is null || string.IsNullOrWhiteSpace(row.AdsAccessTokenCiphertext))
        {
            if (!string.IsNullOrWhiteSpace(profile?.MetaCapiAccessToken))
            {
                try
                {
                    token = legacyProvider.CreateProtector(MetaCapiCredentialProtection.Purpose)
                        .Unprotect(profile.MetaCapiAccessToken);
                }
                catch (CryptographicException)
                {
                    // Legacy protected values may be unreadable after key rotation.
                    // Pixel/test-code migration must still complete; CAPI is never
                    // reconstructed from an unreadable legacy secret.
                    token = null;
                }
            }
        }
        await connections.ImportProfileAsync(owner, profile?.MetaPixelId, token, profile?.MetaTestEventCode, ct);
        return (await connections.GetStatusAsync(owner, ct))!;
    }
}
