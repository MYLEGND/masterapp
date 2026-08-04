using Domain.Accounts;
using Infrastructure.Identity;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Mobile;

/// <summary>
/// Shared controller boundary for bearer-authenticated mobile endpoints.
/// It resolves only an existing, typed server identity and writes the mobile
/// JSON error contract; browser-controller behavior is intentionally absent.
/// </summary>
public abstract class MobileApiControllerBase : ControllerBase
{
    private readonly IMobileActorResolver _actorResolver;
    private readonly IAccountLifecycleService? _accountLifecycle;

    protected MobileApiControllerBase(
        IMobileActorResolver actorResolver,
        IAccountLifecycleService? accountLifecycle = null)
    {
        _actorResolver = actorResolver;
        _accountLifecycle = accountLifecycle;
    }

    protected IMobileActorResolver ActorResolver => _actorResolver;

    protected async Task<MobileActorRequestResolution> ResolveActorAsync(
        CancellationToken cancellationToken,
        bool allowSelectionRequired = false,
        bool allowLifecycleRestricted = false)
    {
        var requestedParticipantType = Request.Headers[MobileApiAuthorization.ParticipantTypeHeader].FirstOrDefault();
        var resolution = await _actorResolver.ResolveAsync(User, requestedParticipantType, cancellationToken);
        if (!resolution.Succeeded)
        {
            return new MobileActorRequestResolution(
                null,
                Array.Empty<MobileResolvedActor>(),
                false,
                Error(
                    StatusCodes.Status403Forbidden,
                    resolution.ErrorCode ?? "mobile_actor_unresolved",
                    resolution.ErrorMessage ?? "Your account could not be resolved for mobile access."));
        }

        if (resolution.RequiresParticipantSelection && !allowSelectionRequired)
        {
            return new MobileActorRequestResolution(
                null,
                resolution.PermittedActors,
                true,
                Error(
                    StatusCodes.Status409Conflict,
                    "mobile_role_selection_required",
                    "Choose one of your authorized mobile roles before continuing."));
        }

        var lifecycleService = _accountLifecycle;
        if (lifecycleService is null && HttpContext.RequestServices is IServiceProvider requestServices)
            lifecycleService = requestServices.GetService<IAccountLifecycleService>();
        if (resolution.SelectedActor is not null &&
            !allowLifecycleRestricted &&
            lifecycleService is not null)
        {
            var lifecycle = await lifecycleService.GetAsync(
                new AccountLifecycleSubject(
                    resolution.SelectedActor.Actor.UserId,
                    resolution.SelectedActor.Actor.ParticipantType,
                    resolution.SelectedActor.ProfileId),
                cancellationToken);
            if (!lifecycle.AllowsFullAccess)
            {
                var isPaused = lifecycle.CanResume;
                return new MobileActorRequestResolution(
                    null,
                    resolution.PermittedActors,
                    false,
                    Error(
                        StatusCodes.Status403Forbidden,
                        isPaused ? "mobile_account_paused" : "mobile_account_closure_in_progress",
                        isPaused
                            ? "Your Legend account is paused. Resume it from Account access to continue."
                            : "Legend access is unavailable while your account closure request is in progress."));
            }
        }

        return new MobileActorRequestResolution(
            resolution.SelectedActor,
            resolution.PermittedActors,
            resolution.RequiresParticipantSelection,
            null);
    }

    protected IActionResult Error(int statusCode, string code, string message)
    {
        var correlationId = CorrelationId();
        Response.Headers["X-Correlation-ID"] = correlationId;
        return StatusCode(
            statusCode,
            new MobileApiErrorResponse(code, message, correlationId, new Dictionary<string, string[]>()));
    }

    protected string CorrelationId() => HttpContext.TraceIdentifier;
}

public sealed record MobileActorRequestResolution(
    MobileResolvedActor? Actor,
    IReadOnlyList<MobileResolvedActor> PermittedActors,
    bool RequiresParticipantSelection,
    IActionResult? Error);
