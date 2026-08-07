using Infrastructure.DailyScripture;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Mobile;

/// <summary>
/// Native management surface for the server-owned Daily Scripture schedule.
/// All writes resolve a typed mobile actor on the server and are authorized by
/// the established Founder-controlled ScriptureManagement resource.
/// </summary>
[ApiController]
[Route("api/v1/mobile/daily-scripture")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileDailyScriptureController : MobileApiControllerBase
{
    private readonly IDailyScriptureManagementService _management;

    public MobileDailyScriptureController(
        IMobileActorResolver actorResolver,
        IDailyScriptureManagementService management)
        : base(actorResolver)
    {
        _management = management;
    }

    [HttpGet("management")]
    public async Task<IActionResult> Management(CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _management.GetSnapshotAsync(resolved.Actor!.Actor, cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(result.Value)
            : ManagementFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("overrides")]
    public async Task<IActionResult> Create(
        [FromBody] MobileDailyScriptureOverrideRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _management.CreateAsync(
            resolved.Actor!.Actor,
            ToDraft(request),
            cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Created($"/api/v1/mobile/daily-scripture/overrides/{result.Value.Id:D}", result.Value)
            : ManagementFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPut("overrides/{id:guid}")]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] MobileDailyScriptureOverrideRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _management.UpdateAsync(
            resolved.Actor!.Actor,
            id,
            ToDraft(request),
            cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(result.Value)
            : ManagementFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpDelete("overrides/{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _management.RemoveAsync(resolved.Actor!.Actor, id, cancellationToken);
        return result.Succeeded
            ? NoContent()
            : ManagementFailure(result.ErrorCode, result.ErrorMessage);
    }

    private static DailyScriptureOverrideDraft ToDraft(MobileDailyScriptureOverrideRequest? request) => new(
        request?.DisplayDate ?? default,
        request?.Reference ?? string.Empty,
        request?.Translation ?? string.Empty,
        request?.PassageText ?? string.Empty);

    private IActionResult ManagementFailure(string? errorCode, string? errorMessage)
    {
        var statusCode = errorCode switch
        {
            "DAILY_SCRIPTURE_INVALID" => StatusCodes.Status400BadRequest,
            "DAILY_SCRIPTURE_NOT_FOUND" => StatusCodes.Status404NotFound,
            "DAILY_SCRIPTURE_CONFLICT" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status403Forbidden
        };
        return Error(
            statusCode,
            (errorCode ?? "DAILY_SCRIPTURE_UNAVAILABLE").ToLowerInvariant(),
            errorMessage ?? "This Daily Scripture action is not available.");
    }
}

public sealed record MobileDailyScriptureOverrideRequest(
    DateOnly? DisplayDate,
    string? Reference,
    string? Translation,
    string? PassageText);
