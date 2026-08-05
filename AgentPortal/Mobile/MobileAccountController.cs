using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Mobile;

/// <summary>
/// Mobile account transport endpoint.
///
/// Authoritative profile reads, validation, and mutations are delegated to
/// IMobileAccountService. Avatar projection remains delegated to the existing
/// typed messaging profile resolver.
/// </summary>
[ApiController]
[Route("api/v1/mobile/account")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileAccountController : MobileApiControllerBase
{
    // 3 MiB binary profile-image limit encoded as Base64, with no second copy
    // materialized before the shared profile-media validator receives it.
    private const int MaximumAvatarBase64Characters = 4 * 1024 * 1024;
    private readonly IMobileAccountService _accounts;
    private readonly IMessagingProfileImageResolver _profiles;
    private readonly IProfileImageWriter _profileImages;

    public MobileAccountController(
        IMobileActorResolver actorResolver,
        IMobileAccountService accounts,
        IMessagingProfileImageResolver profiles,
        IProfileImageWriter profileImages)
        : base(actorResolver)
    {
        _accounts = accounts;
        _profiles = profiles;
        _profileImages = profileImages;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _accounts.GetAsync(
            resolved.Actor!,
            cancellationToken);

        return result.Succeeded && result.Account is not null
            ? Ok(await ProjectAsync(
                resolved.Actor!,
                result.Account,
                cancellationToken))
            : AccountFailure(result);
    }

    [HttpGet("/api/v1/mobile/profile-images/{participantType}/{profileId:guid}")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)]
    public async Task<IActionResult> ProfileImage(
        string participantType,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        if (profileId == Guid.Empty ||
            participantType is not (
                MessagingParticipantTypes.Agent or
                MessagingParticipantTypes.Client))
        {
            return NotFound();
        }

        var image = await _profiles.ResolveAsync(
            new MessagingParticipantIdentity(
                string.Empty,
                participantType,
                profileId,
                string.Empty,
                null,
                string.Empty),
            cancellationToken);

        return image is null
            ? NotFound()
            : File(
                image.Content,
                image.ContentType);
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
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "mobile_account_input_required",
                "Account details are required.");
        }

        var result = await _accounts.UpdateAsync(
            resolved.Actor!,
            new MobileAccountUpdate(
                request.DisplayName,
                request.Phone,
                request.Title,
                request.ShortBio,
                request.Username,
                request.Bio,
                request.Website,
                request.Location,
                request.PublicEmail,
                request.IsEmailVisible,
                request.IsPhoneVisible,
                request.PreferredCommunicationLanguage,
                request.IsPrivate),
            cancellationToken);

        return result.Succeeded && result.Account is not null
            ? Ok(await ProjectAsync(
                resolved.Actor!,
                result.Account,
                cancellationToken))
            : AccountFailure(result);
    }

    [HttpPut("privacy")]
    public async Task<IActionResult> UpdatePrivacy(
        [FromBody] MobileAccountPrivacyUpdateRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        if (request is null)
            return Error(StatusCodes.Status400BadRequest, "mobile_account_privacy_required", "Account privacy is required.");

        var result = await _accounts.UpdatePrivacyAsync(resolved.Actor!, request.IsPrivate, cancellationToken);
        return result.Succeeded && result.Account is not null
            ? Ok(await ProjectAsync(resolved.Actor!, result.Account, cancellationToken))
            : AccountFailure(result);
    }

    [HttpPut("avatar")]
    public async Task<IActionResult> UpdateAvatar(
        [FromBody] MobileAccountAvatarUpdateRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        if (string.IsNullOrWhiteSpace(request?.Base64Content))
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "mobile_account_avatar_required",
                "Choose a profile picture to upload.");
        }

        if (request.Base64Content.Length > MaximumAvatarBase64Characters)
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "mobile_account_avatar_too_large",
                "Choose a PNG, JPG, or WEBP profile picture under 3 MB.");
        }

        byte[] content;
        try
        {
            content = Convert.FromBase64String(request.Base64Content);
        }
        catch (FormatException)
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "mobile_account_avatar_invalid",
                "Choose a valid profile picture to upload.");
        }

        var actor = resolved.Actor!;
        var imageResult = await _profileImages.UpdateAsync(
            new MessagingParticipantIdentity(
                actor.Actor.UserId,
                actor.Actor.ParticipantType,
                actor.ProfileId,
                actor.DisplayName,
                null,
                string.Empty),
            content,
            cancellationToken);
        if (!imageResult.Succeeded)
        {
            return Error(
                StatusCodes.Status400BadRequest,
                imageResult.ErrorCode?.ToLowerInvariant() ?? "mobile_account_avatar_invalid",
                imageResult.ErrorMessage ?? "Choose a valid PNG, JPG, or WEBP profile picture under 3 MB.");
        }

        var accountResult = await _accounts.GetAsync(actor, cancellationToken);
        return accountResult.Succeeded && accountResult.Account is not null
            ? Ok(await ProjectAsync(actor, accountResult.Account, cancellationToken))
            : AccountFailure(accountResult);
    }

    [HttpGet("username-availability")]
    public async Task<IActionResult> UsernameAvailability(
        [FromQuery] string? username,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        return Ok(await _accounts.CheckUsernameAvailabilityAsync(
            resolved.Actor!,
            username,
            cancellationToken));
    }

    private async Task<MobileAccountProfile> ProjectAsync(
        MobileResolvedActor actor,
        MobileAccountSnapshot account,
        CancellationToken cancellationToken)
    {
        // The resolved actor already carries the authoritative typed profile ID.
        // Resolve from that ID directly so an agent's own image never depends on a
        // second identity lookup with a potentially different user-ID spelling.
        var avatar = await MobileAvatarProjection.ResolveAsync(
            _profiles,
            actor.Actor.ParticipantType,
            actor.ProfileId,
            cancellationToken);

        return new MobileAccountProfile(
            account.ParticipantType,
            account.ProfileId,
            account.DisplayName,
            account.Email,
            account.Phone,
            account.Title,
            account.RoleLabel,
            account.ShortBio,
            account.Username,
            account.Bio,
            account.Website,
            account.Location,
            account.ProfileEmail,
            account.IsEmailVisible,
            account.IsPrivate,
            avatar,
            account.IsVerified,
            account.UsernameChangesRemaining,
            account.IsPhoneVisible,
            new MobileTranslationAccessDto(
                account.TranslationAccess?.State ?? ControlledResourceAccessStates.NotGranted,
                account.TranslationAccess?.CanManage ?? false,
                account.PreferredCommunicationLanguage));
    }

    private IActionResult AccountFailure(MobileAccountResult result)
    {
        var statusCode = result.ErrorCode switch
        {
            "MOBILE_ACCOUNT_INPUT_INVALID" =>
                StatusCodes.Status400BadRequest,
            "MOBILE_ACCOUNT_UNAVAILABLE" =>
                StatusCodes.Status403Forbidden,
            "MOBILE_ACCOUNT_ROLE_INVALID" =>
                StatusCodes.Status403Forbidden,
            _ =>
                StatusCodes.Status403Forbidden
        };

        return Error(
            statusCode,
            result.ErrorCode?.ToLowerInvariant() ??
            "mobile_account_unavailable",
            result.ErrorMessage ??
            "Your mobile account is not available.");
    }
}

public sealed record MobileAccountUpdateRequest(
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

public sealed record MobileAccountPrivacyUpdateRequest(bool IsPrivate);

public sealed record MobileAccountAvatarUpdateRequest(string? Base64Content);

public sealed record MobileAccountProfile(
    string ParticipantType,
    Guid ProfileId,
    string DisplayName,
    string? Email,
    string? Phone,
    string? Title,
    string? RoleLabel,
    string? ShortBio,
    string? Username,
    string? Bio,
    string? Website,
    string? Location,
    string? ProfileEmail,
    bool IsEmailVisible,
    bool IsPrivate,
    MobileAvatarDto? Avatar,
    bool IsVerified,
    int UsernameChangesRemaining,
    bool IsPhoneVisible = false,
    MobileTranslationAccessDto? TranslationAccess = null);

public sealed record MobileTranslationAccessDto(
    string State,
    bool CanManage,
    string? PreferredCommunicationLanguage);
