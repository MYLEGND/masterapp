using AgentPortal.Security;
using Infrastructure.Identity;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Mobile;

/// <summary>
/// Founder-only mobile account administration. The route is intentionally
/// separate from public discovery and CRM endpoints: listing here never changes
/// the subscription-scoped visibility rules used by ordinary members.
/// </summary>
[ApiController]
[Route("api/v1/mobile/founder/accounts")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[FounderOnly]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileFounderAccountController : MobileApiControllerBase
{
    private const string RemovalConfirmation = "DELETE";

    private readonly IFounderAccountRemovalService _accounts;

    public MobileFounderAccountController(
        IMobileActorResolver actorResolver,
        IAccountLifecycleService lifecycle,
        IFounderAccountRemovalService accounts)
        : base(actorResolver, lifecycle)
    {
        _accounts = accounts;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] int? take,
        CancellationToken cancellationToken)
    {
        if (!FounderGuard.IsFounder(User))
            return Forbid();

        var founder = await ResolveActorAsync(cancellationToken);
        if (founder.Error is not null || founder.Actor is null)
            return founder.Error!;

        var accounts = await _accounts.ListAsync(search, take ?? 50, cancellationToken);
        return Ok(accounts.Select(ToDto));
    }

    [HttpPost("remove")]
    public async Task<IActionResult> Remove(
        [FromBody] MobileFounderAccountRemovalRequest? request,
        CancellationToken cancellationToken)
    {
        if (!FounderGuard.IsFounder(User))
            return Forbid();
        if (!string.Equals(request?.Confirmation?.Trim(), RemovalConfirmation, StringComparison.Ordinal))
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "founder_account_removal_confirmation_required",
                "Type DELETE to confirm permanent Legend account removal.");
        }

        var founder = await ResolveActorAsync(cancellationToken);
        if (founder.Error is not null || founder.Actor is null)
            return founder.Error!;

        var result = await _accounts.RemoveAsync(
            new FounderAccountRemovalCommand(
                request!.ProfileId,
                request.ParticipantType ?? string.Empty,
                founder.Actor.Actor.UserId,
                CorrelationId()),
            cancellationToken);

        if (!result.Succeeded)
        {
            return Error(
                result.ErrorCode == "founder_account_removal_not_found"
                    ? StatusCodes.Status404NotFound
                    : result.ErrorCode == "founder_account_removal_self_forbidden"
                        ? StatusCodes.Status403Forbidden
                        : StatusCodes.Status409Conflict,
                result.ErrorCode ?? "founder_account_removal_unavailable",
                result.Message);
        }

        return Ok(new MobileFounderAccountRemovalResponse(
            result.Completed,
            result.Message,
            result.LifecycleState));
    }

    private static MobileFounderManagedAccountDto ToDto(FounderManagedAccount account) =>
        new(
            account.ProfileId,
            account.UserId,
            account.ParticipantType,
            account.DisplayName,
            account.Email,
            account.LifecycleState,
            account.HasCancelableSubscription,
            account.IsActive);
}

public sealed record MobileFounderAccountRemovalRequest(
    Guid ProfileId,
    string? ParticipantType,
    string? Confirmation);

public sealed record MobileFounderManagedAccountDto(
    Guid ProfileId,
    string UserId,
    string ParticipantType,
    string DisplayName,
    string? Email,
    string LifecycleState,
    bool HasCancelableSubscription,
    bool IsActive);

public sealed record MobileFounderAccountRemovalResponse(
    bool Completed,
    string Message,
    string LifecycleState);
