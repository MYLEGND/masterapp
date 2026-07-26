using Domain.Messaging;
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
}

public sealed record MobileAccountUpdate(
    string DisplayName,
    string? Phone,
    string? Title,
    string? ShortBio);

public sealed record MobileAccountSnapshot(
    string ParticipantType,
    Guid ProfileId,
    string DisplayName,
    string? Email,
    string? Phone,
    string? Title,
    string? ShortBio);

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

/// <summary>
/// Canonical mobile account projection and mutation service.
///
/// This service reads and updates the existing AgentProfiles and ClientProfiles
/// authorities. It does not create mobile profile entities, mirrored persistence,
/// fallback identity matching, or a competing account store.
/// </summary>
public sealed class MobileAccountService : IMobileAccountService
{
    private const int DisplayNameMaximumLength = 160;
    private const int TitleMaximumLength = 160;
    private const int PhoneMaximumLength = 80;
    private const int ShortBioMaximumLength = 1_000;

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

            return profile is null
                ? MobileAccountResult.Failure(
                    "MOBILE_ACCOUNT_UNAVAILABLE",
                    "Your agent account is not available.")
                : MobileAccountResult.Success(profile with
                {
                    DisplayName = NormalizeDisplayName(profile.DisplayName, actor.DisplayName)
                });
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

            return profile is null
                ? MobileAccountResult.Failure(
                    "MOBILE_ACCOUNT_UNAVAILABLE",
                    "Your client account is not available.")
                : MobileAccountResult.Success(profile with
                {
                    DisplayName = NormalizeDisplayName(profile.DisplayName, actor.DisplayName)
                });
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

        await _db.SaveChangesAsync(cancellationToken);
        return await GetAsync(actor, cancellationToken);
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

    private static string NormalizeDisplayName(
        string? displayName,
        string fallback) =>
        string.IsNullOrWhiteSpace(displayName)
            ? fallback
            : displayName.Trim();
}
