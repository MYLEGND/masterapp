using Domain.Accounts;
using Infrastructure.Identity;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Mobile;

/// <summary>
/// The native client may initiate lifecycle operations for its resolved account
/// only. It never accepts a target user, profile, subscription, or identity ID.
/// </summary>
[ApiController]
[Route("api/v1/mobile/account/lifecycle")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileAccountLifecycleController : MobileApiControllerBase
{
    private readonly IAccountLifecycleService _lifecycle;

    public MobileAccountLifecycleController(
        IMobileActorResolver actorResolver,
        IAccountLifecycleService lifecycle)
        : base(actorResolver, lifecycle)
    {
        _lifecycle = lifecycle;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var subject = await ResolveSubjectAsync(cancellationToken);
        if (subject.Error is not null)
            return subject.Error;

        return Ok(ToResponse(await _lifecycle.GetAsync(subject.Subject!, cancellationToken)));
    }

    [HttpPost("pause")]
    public async Task<IActionResult> Pause(
        [FromBody] MobileAccountLifecycleConfirmationRequest? request,
        CancellationToken cancellationToken)
    {
        if (!HasConfirmation(request, "PAUSE"))
            return Error(StatusCodes.Status400BadRequest, "mobile_account_pause_confirmation_required", "Type PAUSE to confirm that you want to pause your Legend account.");

        var subject = await ResolveSubjectAsync(cancellationToken);
        if (subject.Error is not null)
            return subject.Error;

        var result = await _lifecycle.PauseAsync(subject.Subject!, CorrelationId(), cancellationToken);
        return OperationResult(result);
    }

    [HttpPost("resume")]
    public async Task<IActionResult> Resume(CancellationToken cancellationToken)
    {
        var subject = await ResolveSubjectAsync(cancellationToken);
        if (subject.Error is not null)
            return subject.Error;

        var result = await _lifecycle.ResumeAsync(subject.Subject!, CorrelationId(), cancellationToken);
        return OperationResult(result);
    }

    [HttpPost("deletion-request")]
    public async Task<IActionResult> RequestDeletion(
        [FromBody] MobileAccountLifecycleConfirmationRequest? request,
        CancellationToken cancellationToken)
    {
        if (!HasConfirmation(request, "DELETE"))
            return Error(StatusCodes.Status400BadRequest, "mobile_account_deletion_confirmation_required", "Type DELETE to confirm that you want to start closing your Legend account.");

        var subject = await ResolveSubjectAsync(cancellationToken);
        if (subject.Error is not null)
            return subject.Error;

        var result = await _lifecycle.RequestDeletionAsync(subject.Subject!, CorrelationId(), cancellationToken);
        return OperationResult(result);
    }

    private async Task<LifecycleSubjectResolution> ResolveSubjectAsync(CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(
            cancellationToken,
            allowLifecycleRestricted: true);
        if (resolved.Error is not null || resolved.Actor is null)
        {
            return new LifecycleSubjectResolution(
                null,
                resolved.Error ?? Error(
                    StatusCodes.Status403Forbidden,
                    "mobile_account_unavailable",
                    "Your account could not be resolved."));
        }

        return new LifecycleSubjectResolution(
            new AccountLifecycleSubject(
                resolved.Actor.Actor.UserId,
                resolved.Actor.Actor.ParticipantType,
                resolved.Actor.ProfileId),
            null);
    }

    private IActionResult OperationResult(AccountLifecycleOperationResult result)
    {
        if (result.Succeeded)
            return Ok(ToResponse(result.Snapshot, result.Message));

        return Error(
            result.ErrorCode == "ACCOUNT_CLOSURE_IN_PROGRESS"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status403Forbidden,
            result.ErrorCode?.ToLowerInvariant() ?? "mobile_account_lifecycle_unavailable",
            result.Message ?? "Your account lifecycle request could not be completed.");
    }

    private static bool HasConfirmation(MobileAccountLifecycleConfirmationRequest? request, string expected) =>
        string.Equals(request?.Confirmation?.Trim(), expected, StringComparison.Ordinal);

    private static MobileAccountLifecycleResponse ToResponse(
        AccountLifecycleSnapshot snapshot,
        string? message = null) =>
        new(
            snapshot.State,
            snapshot.AllowsFullAccess,
            snapshot.CanResume,
            snapshot.PausedUtc,
            snapshot.DeletionRequestedUtc,
            message);

    private sealed record LifecycleSubjectResolution(
        AccountLifecycleSubject? Subject,
        IActionResult? Error);
}

public sealed record MobileAccountLifecycleConfirmationRequest(string? Confirmation);

public sealed record MobileAccountLifecycleResponse(
    string State,
    bool AllowsFullAccess,
    bool CanResume,
    DateTime? PausedUtc,
    DateTime? DeletionRequestedUtc,
    string? Message);
