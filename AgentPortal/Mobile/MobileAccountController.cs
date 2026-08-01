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
    private readonly IMobileAccountService _accounts;
    private readonly IMessagingProfileImageResolver _profiles;

    public MobileAccountController(
        IMobileActorResolver actorResolver,
        IMobileAccountService accounts,
        IMessagingProfileImageResolver profiles)
        : base(actorResolver)
    {
        _accounts = accounts;
        _profiles = profiles;
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
            account.UsernameChangesRemaining);
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
    bool? IsPrivate = null);

public sealed record MobileAccountPrivacyUpdateRequest(bool IsPrivate);

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
    int UsernameChangesRemaining);
