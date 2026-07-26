using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Mobile;

/// <summary>
/// Mobile account settings are limited to profile-owned fields. Directory email,
/// household, entitlement, and subscription changes remain in their established
/// web workflows because they require their existing coordinated services.
/// </summary>
[ApiController]
[Route("api/v1/mobile/account")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileAccountController : MobileApiControllerBase
{
    private const int DisplayNameMaximumLength = 160;
    private const int TitleMaximumLength = 160;
    private const int PhoneMaximumLength = 80;
    private const int ShortBioMaximumLength = 1_000;

    private readonly MasterAppDbContext _db;
    private readonly IMessagingProfileImageResolver _profiles;

    public MobileAccountController(
        IMobileActorResolver actorResolver,
        MasterAppDbContext db,
        IMessagingProfileImageResolver profiles)
        : base(actorResolver)
    {
        _db = db;
        _profiles = profiles;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        return Ok(await ProjectAsync(resolved.Actor!, cancellationToken));
    }

    [HttpPut]
    public async Task<IActionResult> Update(
        [FromBody] MobileAccountUpdateRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;
        if (request is null)
            return Error(StatusCodes.Status400BadRequest, "mobile_account_input_required", "Account details are required.");

        var validationError = Validate(request, resolved.Actor!);
        if (validationError is not null)
            return Error(StatusCodes.Status400BadRequest, "mobile_account_input_invalid", validationError);

        var now = DateTime.UtcNow;
        if (string.Equals(resolved.Actor!.Actor.ParticipantType, MessagingParticipantTypes.Agent, StringComparison.Ordinal))
        {
            var profile = await _db.AgentProfiles.SingleOrDefaultAsync(
                candidate => candidate.Id == resolved.Actor.ProfileId && candidate.IsActive,
                cancellationToken);
            if (profile is null)
                return Error(StatusCodes.Status403Forbidden, "mobile_account_unavailable", "Your agent account is not available.");

            profile.FullName = TrimRequired(request.DisplayName);
            profile.Title = TrimOptional(request.Title);
            profile.Phone = TrimOptional(request.Phone);
            profile.ShortBio = TrimOptional(request.ShortBio);
            profile.UpdatedUtc = now;
        }
        else
        {
            var profile = await _db.ClientProfiles.SingleOrDefaultAsync(
                candidate => candidate.Id == resolved.Actor.ProfileId,
                cancellationToken);
            if (profile is null)
                return Error(StatusCodes.Status403Forbidden, "mobile_account_unavailable", "Your client account is not available.");

            var names = SplitDisplayName(request.DisplayName);
            profile.FirstName = names.FirstName;
            profile.LastName = names.LastName;
            profile.Phone = TrimRequired(request.Phone);
            profile.UpdatedUtc = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(await ProjectAsync(resolved.Actor!, cancellationToken));
    }

    private async Task<MobileAccountProfile> ProjectAsync(
        MobileResolvedActor actor,
        CancellationToken cancellationToken)
    {
        var identities = await _profiles.ResolveIdentitiesAsync(
            [new MessagingParticipantReference(actor.Actor.UserId, actor.Actor.ParticipantType)],
            cancellationToken);
        var avatar = !identities.TryGetValue((actor.Actor.UserId, actor.Actor.ParticipantType), out var identity)
            ? null
            : await MobileAvatarProjection.ResolveAsync(_profiles, identity, cancellationToken);

        if (string.Equals(actor.Actor.ParticipantType, MessagingParticipantTypes.Agent, StringComparison.Ordinal))
        {
            var profile = await _db.AgentProfiles
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == actor.ProfileId, cancellationToken);
            return new MobileAccountProfile(
                actor.Actor.ParticipantType,
                profile.Id,
                DisplayName(profile.FullName, actor.DisplayName),
                profile.AgentUpn,
                profile.Phone,
                profile.Title,
                profile.ShortBio,
                avatar);
        }

        var clientProfile = await _db.ClientProfiles
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == actor.ProfileId, cancellationToken);
        return new MobileAccountProfile(
            actor.Actor.ParticipantType,
            clientProfile.Id,
            DisplayName($"{clientProfile.FirstName} {clientProfile.LastName}", actor.DisplayName),
            clientProfile.Email,
            clientProfile.Phone,
            null,
            null,
            avatar);
    }

    private static string? Validate(MobileAccountUpdateRequest request, MobileResolvedActor actor)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return "Enter your name.";
        if (request.DisplayName.Trim().Length > DisplayNameMaximumLength)
            return "Your name is too long.";
        if (request.Title?.Trim().Length > TitleMaximumLength)
            return "Your title is too long.";
        if (request.Phone?.Trim().Length > PhoneMaximumLength)
            return "Your phone number is too long.";
        if (request.ShortBio?.Trim().Length > ShortBioMaximumLength)
            return "Your introduction is too long.";
        if (string.Equals(actor.Actor.ParticipantType, MessagingParticipantTypes.Client, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(request.Phone))
            return "Enter your phone number.";
        if (string.Equals(actor.Actor.ParticipantType, MessagingParticipantTypes.Client, StringComparison.Ordinal) &&
            SplitDisplayName(request.DisplayName) is { LastName.Length: 0 })
            return "Enter your first and last name.";
        return null;
    }

    private static (string FirstName, string LastName) SplitDisplayName(string? displayName)
    {
        var parts = (displayName ?? string.Empty)
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => (string.Empty, string.Empty),
            1 => (parts[0], string.Empty),
            _ => (parts[0], string.Join(' ', parts.Skip(1)))
        };
    }

    private static string TrimRequired(string? value) => value!.Trim();
    private static string? TrimOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string DisplayName(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

public sealed record MobileAccountUpdateRequest(string DisplayName, string? Phone, string? Title, string? ShortBio);
public sealed record MobileAccountProfile(
    string ParticipantType,
    Guid ProfileId,
    string DisplayName,
    string? Email,
    string? Phone,
    string? Title,
    string? ShortBio,
    MobileAvatarDto? Avatar);
