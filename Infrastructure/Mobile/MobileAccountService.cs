using Domain.Messaging;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Messaging;
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

    Task<MobileAccountResult> UpdatePrivacyAsync(
        MobileResolvedActor actor,
        bool isPrivate,
        CancellationToken cancellationToken = default);

    Task<MobileAccountResult> UpdateTranslationLearningConsentAsync(
        MobileResolvedActor actor,
        bool allowsConsentedTranslationLearning,
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
    bool IsEmailVisible = false,
    bool IsPhoneVisible = false,
    string? PreferredCommunicationLanguage = null,
    bool? IsPrivate = null);

public sealed record MobileAccountSnapshot(
    string ParticipantType,
    Guid ProfileId,
    string DisplayName,
    string? Email,
    string? Phone,
    string? Title,
    string? RoleLabel,
    string? ShortBio,
    bool IsVerified = false,
    string? Username = null,
    string? Bio = null,
    string? Website = null,
    string? Location = null,
    string? ProfileEmail = null,
    bool IsEmailVisible = false,
    bool IsPhoneVisible = false,
    bool IsPrivate = false,
    int UsernameChangesRemaining = 2,
    ControlledResourceAccess? TranslationAccess = null,
    string? PreferredCommunicationLanguage = null,
    TranslationAccountEntitlementSnapshot? TranslationEntitlement = null,
    bool AllowsConsentedTranslationLearning = false);

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
    private const int MaximumUsernameChangesPerCalendarMonth = 2;

    private readonly MasterAppDbContext _db;
    private readonly IControlledResourceAccessService _controlledResources;
    private readonly ILegendLanguageRegistry _languages;
    private readonly ITranslationEntitlementAuthority? _translationEntitlements;

    public MobileAccountService(
        MasterAppDbContext db,
        IControlledResourceAccessService controlledResources,
        ILegendLanguageRegistry? languages = null,
        ITranslationEntitlementAuthority? translationEntitlements = null)
    {
        _db = db;
        _controlledResources = controlledResources;
        _languages = languages ?? new LegendLanguageRegistry(_db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        _translationEntitlements = translationEntitlements;
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
                    null,
                    candidate.ShortBio,
                    candidate.IsVerified))
                .SingleOrDefaultAsync(cancellationToken);

            if (profile is null)
            {
                return MobileAccountResult.Failure(
                    "MOBILE_ACCOUNT_UNAVAILABLE",
                    "Your agent account is not available.");
            }

            profile = profile with
            {
                IsVerified = profile.IsVerified || LegendVerifiedIdentity.IsVerifiedAgentEmail(profile.Email),
                RoleLabel = AgentProfileIdentity.LegendRoleLabel(profile.Title)
            };

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
                    null,
                    null,
                    candidate.IsVerified))
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

        var preferredLanguage = TrimOptional(update.PreferredCommunicationLanguage);
        if (update.PreferredCommunicationLanguage is not null &&
            preferredLanguage is not null)
        {
            preferredLanguage = await _languages.NormalizeEnabledTranslationLanguageAsync(preferredLanguage, cancellationToken);
            if (preferredLanguage is null)
            {
                return MobileAccountResult.Failure(
                    "MOBILE_ACCOUNT_INPUT_INVALID",
                    "Choose a supported communication language.");
            }
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

        var mobileSettings = await GetOrCreateMobileSettingsAsync(actor, now, cancellationToken);
        var requestedUsername = NormalizeUsername(update.Username);
        var usernameChangeError = ApplyUsernameChange(
            mobileSettings,
            requestedUsername,
            now);
        if (usernameChangeError is not null)
        {
            return MobileAccountResult.Failure(
                "MOBILE_ACCOUNT_INPUT_INVALID",
                usernameChangeError);
        }

        mobileSettings.Username = DisplayUsername(update.Username);
        mobileSettings.NormalizedUsername = requestedUsername;
        mobileSettings.Bio = TrimOptional(update.Bio);
        mobileSettings.Website = TrimOptional(update.Website);
        mobileSettings.Location = TrimOptional(update.Location);
        mobileSettings.PublicEmail = TrimOptional(update.PublicEmail);
        mobileSettings.IsEmailVisible = update.IsEmailVisible;
        mobileSettings.IsPhoneVisible = update.IsPhoneVisible;
        if (update.PreferredCommunicationLanguage is not null)
            mobileSettings.PreferredCommunicationLanguage = preferredLanguage;
        if (update.IsPrivate.HasValue)
            mobileSettings.IsPrivate = update.IsPrivate.Value;
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

    public async Task<MobileAccountResult> UpdatePrivacyAsync(
        MobileResolvedActor actor,
        bool isPrivate,
        CancellationToken cancellationToken = default)
    {
        var account = await GetAsync(actor, cancellationToken);
        if (!account.Succeeded || account.Account is null)
            return account;

        var now = DateTime.UtcNow;
        var settings = await GetOrCreateMobileSettingsAsync(actor, now, cancellationToken);
        settings.IsPrivate = isPrivate;
        settings.UpdatedUtc = now;
        await _db.SaveChangesAsync(cancellationToken);

        return await GetAsync(actor, cancellationToken);
    }

    public async Task<MobileAccountResult> UpdateTranslationLearningConsentAsync(
        MobileResolvedActor actor,
        bool allowsConsentedTranslationLearning,
        CancellationToken cancellationToken = default)
    {
        var account = await GetAsync(actor, cancellationToken);
        if (!account.Succeeded || account.Account is null)
            return account;

        var now = DateTime.UtcNow;
        var settings = await GetOrCreateMobileSettingsAsync(actor, now, cancellationToken);
        settings.AllowsConsentedTranslationLearning = allowsConsentedTranslationLearning;
        settings.UpdatedUtc = now;
        await _db.SaveChangesAsync(cancellationToken);

        return await GetAsync(actor, cancellationToken);
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

        var translationAccess = await _controlledResources.GetAccessAsync(
            actor.Actor,
            ControlledResourceTypes.LanguageTranslation,
            cancellationToken);
        var translationEntitlement = _translationEntitlements is null
            ? null
            : await _translationEntitlements.GetSnapshotAsync(actor.Actor, cancellationToken);

        if (settings is null)
            return MobileAccountResult.Success(account with
            {
                TranslationAccess = translationAccess,
                TranslationEntitlement = translationEntitlement
            });

        var profileEmail = TrimOptional(settings.PublicEmail);
        return MobileAccountResult.Success(account with
        {
            Email = settings.IsEmailVisible ? profileEmail : null,
            Username = TrimOptional(settings.Username),
            Bio = TrimOptional(settings.Bio) ?? account.ShortBio,
            Website = TrimOptional(settings.Website),
            Location = TrimOptional(settings.Location),
            ProfileEmail = profileEmail,
            IsEmailVisible = settings.IsEmailVisible,
            IsPhoneVisible = settings.IsPhoneVisible,
            IsPrivate = settings.IsPrivate,
            AllowsConsentedTranslationLearning = settings.AllowsConsentedTranslationLearning,
            UsernameChangesRemaining = UsernameChangesRemaining(settings),
            TranslationAccess = translationAccess,
            TranslationEntitlement = translationEntitlement,
            // Application language is an account preference for every member.
            // Message-translation entitlement remains a separate capability.
            PreferredCommunicationLanguage = await _languages.NormalizeEnabledTranslationLanguageAsync(
                settings.PreferredCommunicationLanguage,
                cancellationToken)
        });
    }

    private async Task<MobileProfileSettings> GetOrCreateMobileSettingsAsync(
        MobileResolvedActor actor,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var settings = await _db.MobileProfileSettings.SingleOrDefaultAsync(
            candidate => candidate.ProfileId == actor.ProfileId &&
                         candidate.ParticipantType == actor.Actor.ParticipantType,
            cancellationToken);
        if (settings is not null)
            return settings;

        settings = new MobileProfileSettings
        {
            Id = Guid.NewGuid(),
            ProfileId = actor.ProfileId,
            ParticipantType = actor.Actor.ParticipantType,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        _db.MobileProfileSettings.Add(settings);
        return settings;
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

        if (update.IsPhoneVisible && string.IsNullOrWhiteSpace(update.Phone))
            return "Add the phone number you want to show before enabling it on your profile.";

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

    private static string? ApplyUsernameChange(
        MobileProfileSettings settings,
        string? requestedUsername,
        DateTime now)
    {
        if (string.Equals(
                settings.NormalizedUsername,
                requestedUsername,
                StringComparison.Ordinal))
        {
            return null;
        }

        // Reserving a username for the first time is not a change. Every later
        // rename (including clearing a username) counts against the calendar month.
        if (string.IsNullOrWhiteSpace(settings.NormalizedUsername))
            return null;

        var currentMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        if (settings.UsernameChangeMonthUtc != currentMonth)
        {
            settings.UsernameChangeMonthUtc = currentMonth;
            settings.UsernameChangeCount = 0;
        }

        if (settings.UsernameChangeCount >= MaximumUsernameChangesPerCalendarMonth)
        {
            return "Your username can be changed only twice per calendar month. You can update it again next month.";
        }

        settings.UsernameChangeCount += 1;
        return null;
    }

    private static int UsernameChangesRemaining(MobileProfileSettings settings)
    {
        var currentMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        if (settings.UsernameChangeMonthUtc != currentMonth)
            return MaximumUsernameChangesPerCalendarMonth;

        return Math.Max(0, MaximumUsernameChangesPerCalendarMonth - settings.UsernameChangeCount);
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
