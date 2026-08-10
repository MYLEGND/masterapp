using AgentPortal.Security;
using Domain.Accounts;
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
    private const string PurgeConfirmation = "ERASE";

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
        [FromQuery] string? scope,
        CancellationToken cancellationToken)
    {
        if (!FounderGuard.IsFounder(User))
            return Forbid();

        var founder = await ResolveActorAsync(cancellationToken);
        if (founder.Error is not null || founder.Actor is null)
            return founder.Error!;

        var directoryScope = string.Equals(scope, "archive", StringComparison.OrdinalIgnoreCase)
            ? FounderAccountDirectoryScope.Archive
            : FounderAccountDirectoryScope.Active;
        var accounts = await _accounts.ListAsync(search, take ?? 50, directoryScope, cancellationToken);
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
                "Type DELETE to confirm account closure and archive.");
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

    [HttpPost("remove-batch")]
    public async Task<IActionResult> RemoveBatch(
        [FromBody] MobileFounderAccountBatchRequest? request,
        CancellationToken cancellationToken)
    {
        if (!FounderGuard.IsFounder(User))
            return Forbid();
        if (!string.Equals(request?.Confirmation?.Trim(), RemovalConfirmation, StringComparison.Ordinal))
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "founder_account_removal_confirmation_required",
                "Type DELETE to confirm account removal.");
        }

        var founder = await ResolveActorAsync(cancellationToken);
        if (founder.Error is not null || founder.Actor is null)
            return founder.Error!;

        var result = await _accounts.RemoveManyAsync(
            new FounderAccountRemovalBatchCommand(
                ToTargets(request?.Accounts),
                founder.Actor.Actor.UserId,
                CorrelationId()),
            cancellationToken);
        return Ok(new MobileFounderAccountBatchResponse(
            result.CompletedCount,
            result.FailedCount,
            result.Results.Select(item => new MobileFounderAccountBatchItemResponse(
                item.Succeeded,
                item.Completed,
                item.ErrorCode,
                item.Message,
                item.LifecycleState)).ToArray()));
    }

    [HttpPost("archive/purge")]
    public async Task<IActionResult> PurgeArchive(
        [FromBody] MobileFounderAccountBatchRequest? request,
        CancellationToken cancellationToken)
    {
        if (!FounderGuard.IsFounder(User))
            return Forbid();
        if (!string.Equals(request?.Confirmation?.Trim(), PurgeConfirmation, StringComparison.Ordinal))
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "founder_account_purge_confirmation_required",
                "Type ERASE to permanently remove archived accounts from the Legend application database.");
        }

        var founder = await ResolveActorAsync(cancellationToken);
        if (founder.Error is not null || founder.Actor is null)
            return founder.Error!;

        var result = await _accounts.PurgeArchivedManyAsync(
            new FounderAccountRemovalBatchCommand(
                ToTargets(request?.Accounts),
                founder.Actor.Actor.UserId,
                CorrelationId()),
            cancellationToken);
        return Ok(new MobileFounderAccountBatchResponse(
            result.CompletedCount,
            result.FailedCount,
            result.Results.Select(item => new MobileFounderAccountBatchItemResponse(
                item.Succeeded,
                item.Succeeded,
                item.ErrorCode,
                item.Message,
                AccountLifecycleStates.Closed)).ToArray()));
    }

    private static IReadOnlyCollection<FounderAccountTarget> ToTargets(
        IReadOnlyCollection<MobileFounderAccountTargetRequest>? accounts) =>
        (accounts ?? Array.Empty<MobileFounderAccountTargetRequest>())
        .Select(item => new FounderAccountTarget(item.ProfileId, item.ParticipantType ?? string.Empty))
        .ToArray();

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

public sealed record MobileFounderAccountTargetRequest(
    Guid ProfileId,
    string? ParticipantType);

public sealed record MobileFounderAccountBatchRequest(
    IReadOnlyCollection<MobileFounderAccountTargetRequest>? Accounts,
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

public sealed record MobileFounderAccountBatchItemResponse(
    bool Succeeded,
    bool Completed,
    string? ErrorCode,
    string Message,
    string LifecycleState);

public sealed record MobileFounderAccountBatchResponse(
    int CompletedCount,
    int FailedCount,
    IReadOnlyList<MobileFounderAccountBatchItemResponse> Results);
