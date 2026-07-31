using Domain.Messaging;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Mobile;

public interface IMobileAccountService
{
    Task<MobileAccountResult> GetAsync(
        MobileResolvedActor actor,
        CancellationToken cancellationToken = default);

    Task<MobileAccountResult> UpdateAsync(
        MobileResolvedActor actor,
        MobileAccountUpdate update,
        CancellationToken cancellationToken = default);

    Task<MobileUsernameAvailability> CheckUsernameAvailabilityAsync(
        MobileResolvedActor actor,
        string? username,
        CancellationToken cancellationToken = default);
}

public sealed record MobileAccountUpdate(
    string DisplayName,
    string? Phone,
    string? Title,
    string? ShortBio,
    string? Username = null,
    string? Bio = null,
    string? Website = null,
    string? Location = null,
    string? PublicEmail = null,
    bool IsEmailVisible = false);

public sealed record MobileAccountSnapshot(
    string ParticipantType,
    Guid ProfileId,
    string DisplayName,
    string? Email,
    string? Phone,
    string? Title,
    string? ShortBio,
    string? Username = null,
    string? Bio = null,
    string? Website = null,
    string? Location = null,
    string? ProfileEmail = null,
    bool IsEmailVisible = false);

public sealed record MobileAccountResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    MobileAccountSnapshot? Account)
{
    public static MobileAccountResult Success(MobileAccountSnapshot account) =>
        new(true, null, null, account);

    public static MobileAccountResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, null);
}

public sealed record MobileUsernameAvailability(bool IsAvailable, string? Message);

/// <summary>
/// Canonical mobile account projection and mutation service.
///
/// This service keeps account-owned details with the existing AgentProfiles and
/// ClientProfiles authorities, while MobileProfileSettings stores the mobile-only
/// social fields without mirroring or overwriting the synced web account.
/// </summary>
public sealed class MobileAccountService : IMobileAccountService
{
    private const int DisplayNameMaximumLength = 160;
    private const int TitleMaximumLength = 160;
    private const int PhoneMaximumLength = 80;
    private const int ShortBioMaximumLength = 1_000;
    private const int UsernameMaximumLength = 64;
    private const int WebsiteMaximumLength = 2_048;
    private const int LocationMaximumLength = 120;
    private const int EmailMaximumLength = 320;

    private readonly MasterAppDbContext _db;

    public MobileAccountService(MasterAppDbContext db)
    {
        _db = db;
    }

    public async Task<MobileAccountResult> GetAsync(
        MobileResolvedActor actor,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(
                actor.Actor.ParticipantType,
                MessagingParticipantTypes.Agent,
                StringComparison.Ordinal))
        {
            var profile = await _db.AgentProfiles
                .AsNoTracking()
                .Where(candidate =>
                    candidate.Id == actor.ProfileId &&
                    candidate.IsActive)
                .Select(candidate => new MobileAccountSnapshot(
                    MessagingParticipantTypes.Agent,
                    candidate.Id,
                    candidate.FullName == null || candidate.FullName == string.Empty
                        ? actor.DisplayName
                        : candidate.FullName,
                    candidate.AgentUpn,
                    candidate.Phone,
                    candidate.Title,
                    candidate.ShortBio))
                .SingleOrDefaultAsync(cancellationToken);

            if (profile is null)
            {
                return MobileAccountResult.Failure(
                    "MOBILE_ACCOUNT_UNAVAILABLE",
                    "Your agent account is not available.");
            }

            return await ApplyMobileSettingsAsync(profile with
            {
                DisplayName = NormalizeDisplayName(profile.DisplayName, actor.DisplayName),
                // A directory or web-account email is never implicitly published
                // in the native profile.
                Email = null
            }, actor, cancellationToken);
        }

        if (string.Equals(
                actor.Actor.ParticipantType,
                MessagingParticipantTypes.Client,
                StringComparison.Ordinal))
        {
            var profile = await _db.ClientProfiles
                .AsNoTracking()
                .Where(candidate => candidate.Id == actor.ProfileId)
                .Select(candidate => new MobileAccountSnapshot(
                    MessagingParticipantTypes.Client,
                    candidate.Id,
                    (candidate.FirstName + " " + candidate.LastName).Trim(),
                    candidate.Email,
                    candidate.Phone,
                    null,
                    null))
                .SingleOrDefaultAsync(cancellationToken);

            if (profile is null)
            {
                return MobileAccountResult.Failure(
                    "MOBILE_ACCOUNT_UNAVAILABLE",
                    "Your client account is not available.");
            }

            return await ApplyMobileSettingsAsync(profile with
            {
                DisplayName = NormalizeDisplayName(profile.DisplayName, actor.DisplayName),
                // Client directory email is private unless the member explicitly
                // creates and enables a mobile-profile contact address.
                Email = null
            }, actor, cancellationToken);
        }

        return MobileAccountResult.Failure(
            "MOBILE_ACCOUNT_ROLE_INVALID",
            "The selected mobile account type is not supported.");
    }

    public async Task<MobileAccountResult> UpdateAsync(
        MobileResolvedActor actor,
        MobileAccountUpdate update,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(update, actor);
        if (validationError is not null)
        {
            return MobileAccountResult.Failure(
                "MOBILE_ACCOUNT_INPUT_INVALID",
                validationError);
        }

        var mobileSettingsValidationError = await ValidateUsernameAsync(
            actor,
            update.Username,
            cancellationToken);
        if (mobileSettingsValidationError is not null)
        {
            return MobileAccountResult.Failure(
                "MOBILE_ACCOUNT_INPUT_INVALID",
                mobileSettingsValidationError);
        }

        var now = DateTime.UtcNow;

        if (string.Equals(
                actor.Actor.ParticipantType,
                MessagingParticipantTypes.Agent,
                StringComparison.Ordinal))
        {
            var profile = await _db.AgentProfiles.SingleOrDefaultAsync(
                candidate =>
                    candidate.Id == actor.ProfileId &&
                    candidate.IsActive,
                cancellationToken);

            if (profile is null)
            {
                return MobileAccountResult.Failure(
                    "MOBILE_ACCOUNT_UNAVAILABLE",
                    "Your agent account is not available.");
            }

            profile.FullName = TrimRequired(update.DisplayName);
            profile.Title = TrimOptional(update.Title);
            profile.Phone = TrimOptional(update.Phone);
            profile.ShortBio = TrimOptional(update.ShortBio);
            profile.UpdatedUtc = now;
        }
        else if (string.Equals(
                     actor.Actor.ParticipantType,
                     MessagingParticipantTypes.Client,
                     StringComparison.Ordinal))
        {
            var profile = await _db.ClientProfiles.SingleOrDefaultAsync(
                candidate => candidate.Id == actor.ProfileId,
                cancellationToken);

            if (profile is null)
            {
                return MobileAccountResult.Failure(
                    "MOBILE_ACCOUNT_UNAVAILABLE",
                    "Your client account is not available.");
            }

            var names = SplitDisplayName(update.DisplayName);
            profile.FirstName = names.FirstName;
            profile.LastName = names.LastName;
            profile.Phone = TrimRequired(update.Phone);
            profile.UpdatedUtc = now;
        }
        else
        {
            return MobileAccountResult.Failure(
                "MOBILE_ACCOUNT_ROLE_INVALID",
                "The selected mobile account type is not supported.");
        }

        var mobileSettings = await _db.MobileProfileSettings.SingleOrDefaultAsync(
            candidate => candidate.ProfileId == actor.ProfileId &&
                         candidate.ParticipantType == actor.Actor.ParticipantType,
            cancellationToken);
        if (mobileSettings is null)
        {
            mobileSettings = new MobileProfileSettings
            {
                Id = Guid.NewGuid(),
                ProfileId = actor.ProfileId,
                ParticipantType = actor.Actor.ParticipantType,
                CreatedUtc = now
            };
            _db.MobileProfileSettings.Add(mobileSettings);
        }

        mobileSettings.Username = DisplayUsername(update.Username);
        mobileSettings.NormalizedUsername = NormalizeUsername(update.Username);
        mobileSettings.Bio = TrimOptional(update.Bio);
        mobileSettings.Website = TrimOptional(update.Website);
        mobileSettings.Location = TrimOptional(update.Location);
        mobileSettings.PublicEmail = TrimOptional(update.PublicEmail);
        mobileSettings.IsEmailVisible = update.IsEmailVisible;
        mobileSettings.UpdatedUtc = now;

        await _db.SaveChangesAsync(cancellationToken);
        return await GetAsync(actor, cancellationToken);
    }

    public async Task<MobileUsernameAvailability> CheckUsernameAvailabilityAsync(
        MobileResolvedActor actor,
        string? username,
        CancellationToken cancellationToken = default)
    {
        var validationError = await ValidateUsernameAsync(actor, username, cancellationToken);
        return validationError is null
            ? new MobileUsernameAvailability(true, null)
            : new MobileUsernameAvailability(false, validationError);
    }

    private async Task<MobileAccountResult> ApplyMobileSettingsAsync(
        MobileAccountSnapshot account,
        MobileResolvedActor actor,
        CancellationToken cancellationToken)
    {
        var settings = await _db.MobileProfileSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate =>
                candidate.ProfileId == actor.ProfileId &&
                candidate.ParticipantType == actor.Actor.ParticipantType,
                cancellationToken);

        if (settings is null)
            return MobileAccountResult.Success(account);

        var profileEmail = TrimOptional(settings.PublicEmail);
        return MobileAccountResult.Success(account with
        {
            Email = settings.IsEmailVisible ? profileEmail : null,
            Username = TrimOptional(settings.Username),
            Bio = TrimOptional(settings.Bio) ?? account.ShortBio,
            Website = TrimOptional(settings.Website),
            Location = TrimOptional(settings.Location),
            ProfileEmail = profileEmail,
            IsEmailVisible = settings.IsEmailVisible
        });
    }

    private static string? Validate(
        MobileAccountUpdate update,
        MobileResolvedActor actor)
    {
        if (string.IsNullOrWhiteSpace(update.DisplayName))
            return "Enter your name.";

        if (update.DisplayName.Trim().Length > DisplayNameMaximumLength)
            return "Your name is too long.";

        if (update.Title?.Trim().Length > TitleMaximumLength)
            return "Your title is too long.";

        if (update.Phone?.Trim().Length > PhoneMaximumLength)
            return "Your phone number is too long.";

        if (update.ShortBio?.Trim().Length > ShortBioMaximumLength)
            return "Your introduction is too long.";

        if (update.Bio?.Trim().Length > ShortBioMaximumLength)
            return "Your mobile profile bio is too long.";

        if (update.Website?.Trim().Length > WebsiteMaximumLength)
            return "Your website link is too long.";

        if (update.Location?.Trim().Length > LocationMaximumLength)
            return "Your location is too long.";

        if (update.PublicEmail?.Trim().Length > EmailMaximumLength)
            return "Your profile email is too long.";

        if (update.IsEmailVisible && string.IsNullOrWhiteSpace(update.PublicEmail))
            return "Add the email you want to show before enabling it on your profile.";

        if (string.Equals(
                actor.Actor.ParticipantType,
                MessagingParticipantTypes.Client,
                StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(update.Phone))
        {
            return "Enter your phone number.";
        }

        if (string.Equals(
                actor.Actor.ParticipantType,
                MessagingParticipantTypes.Client,
                StringComparison.Ordinal) &&
            SplitDisplayName(update.DisplayName) is { LastName.Length: 0 })
        {
            return "Enter your first and last name.";
        }

        return null;
    }

    private async Task<string?> ValidateUsernameAsync(
        MobileResolvedActor actor,
        string? requestedUsername,
        CancellationToken cancellationToken)
    {
        var username = NormalizeUsername(requestedUsername);
        if (username is null)
            return null;

        if (username.Length > UsernameMaximumLength)
            return "Your username is too long.";

        if (username.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '_' and not '.'))
        {
            return "Usernames can use letters, numbers, underscores, and periods.";
        }

        var isTaken = await _db.MobileProfileSettings.AsNoTracking().AnyAsync(
            candidate => candidate.NormalizedUsername == username &&
                         (candidate.ProfileId != actor.ProfileId ||
                          candidate.ParticipantType != actor.Actor.ParticipantType),
            cancellationToken);
        return isTaken ? "That username is already in use." : null;
    }

    private static (string FirstName, string LastName) SplitDisplayName(
        string? displayName)
    {
        var parts = (displayName ?? string.Empty)
            .Trim()
            .Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);

        return parts.Length switch
        {
            0 => (string.Empty, string.Empty),
            1 => (parts[0], string.Empty),
            _ => (parts[0], string.Join(' ', parts.Skip(1)))
        };
    }

    private static string TrimRequired(string? value) => value!.Trim();

    private static string? TrimOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeUsername(string? value)
    {
        var normalized = DisplayUsername(value)?.ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? DisplayUsername(string? value) =>
        TrimOptional(TrimOptional(value)?.TrimStart('@'));

    private static string NormalizeDisplayName(
        string? displayName,
        string fallback) =>
        string.IsNullOrWhiteSpace(displayName)
            ? fallback
            : displayName.Trim();
}
