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
                request.Pronouns,
                request.PublicEmail,
                request.IsEmailVisible),
            cancellationToken);

        return result.Succeeded && result.Account is not null
            ? Ok(await ProjectAsync(
                resolved.Actor!,
                result.Account,
                cancellationToken))
            : AccountFailure(result);
    }

    private async Task<MobileAccountProfile> ProjectAsync(
        MobileResolvedActor actor,
        MobileAccountSnapshot account,
        CancellationToken cancellationToken)
    {
        var identities = await _profiles.ResolveIdentitiesAsync(
            [
                new MessagingParticipantReference(
                    actor.Actor.UserId,
                    actor.Actor.ParticipantType)
            ],
            cancellationToken);

        var avatar =
            !identities.TryGetValue(
                (actor.Actor.UserId, actor.Actor.ParticipantType),
                out var identity)
                ? null
                : await MobileAvatarProjection.ResolveAsync(
                    _profiles,
                    identity,
                    cancellationToken);

        return new MobileAccountProfile(
            account.ParticipantType,
            account.ProfileId,
            account.DisplayName,
            account.Email,
            account.Phone,
            account.Title,
            account.ShortBio,
            account.Username,
            account.Bio,
            account.Website,
            account.Location,
            account.Pronouns,
            account.ProfileEmail,
            account.IsEmailVisible,
            avatar);
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
    string? Pronouns = null,
    string? PublicEmail = null,
    bool IsEmailVisible = false);

public sealed record MobileAccountProfile(
    string ParticipantType,
    Guid ProfileId,
    string DisplayName,
    string? Email,
    string? Phone,
    string? Title,
    string? ShortBio,
    string? Username,
    string? Bio,
    string? Website,
    string? Location,
    string? Pronouns,
    string? ProfileEmail,
    bool IsEmailVisible,
    MobileAvatarDto? Avatar);
