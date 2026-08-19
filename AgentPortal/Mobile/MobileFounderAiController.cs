using System.Diagnostics;
using System.Text.Json;
using AgentPortal.Security;
using AgentPortal.Services;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Mobile;

[ApiController]
[Route("api/v1/mobile/founder/legend-ai")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[FounderOnly]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileFounderAiController : MobileApiControllerBase
{
    private const string OperationHeader = "X-Legend-Ai-Operation-Id";
    private static readonly JsonSerializerOptions StreamJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly LegendFounderAiConversationService _conversation;
    private readonly LegendFounderAiProgressBroker _progress;
    private readonly ILogger<MobileFounderAiController> _logger;

    public MobileFounderAiController(
        IMobileActorResolver actorResolver,
        LegendFounderAiConversationService conversation,
        LegendFounderAiProgressBroker progress,
        ILogger<MobileFounderAiController> logger) : base(actorResolver)
    {
        _conversation = conversation;
        _progress = progress;
        _logger = logger;
    }

    [HttpGet("access")]
    public async Task<IActionResult> Access(CancellationToken cancellationToken)
    {
        if (!FounderGuard.IsFounder(User)) return Forbid();
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null) return resolved.Error!;
        return Ok(new MobileFounderAiAccessResponse(Available: true));
    }

    [HttpGet("progress/{operationId:guid}")]
    public async Task Progress(Guid operationId, CancellationToken cancellationToken)
    {
        if (!FounderGuard.IsFounder(User)) { Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null) { Response.StatusCode = StatusCodes.Status401Unauthorized; return; }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var reader = _progress.Subscribe(operationId);
        var started = Stopwatch.GetTimestamp();
        LegendFounderAiProgressEvent? last = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var wait = reader.WaitToReadAsync(cancellationToken).AsTask();
            var heartbeat = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            var completed = await Task.WhenAny(wait, heartbeat);
            if (completed == heartbeat)
            {
                if (last is not null)
                    await WriteAsync(new { type = "heartbeat", elapsedSeconds = (int)Math.Round(Stopwatch.GetElapsedTime(started).TotalSeconds), progress = last }, cancellationToken);
                continue;
            }
            if (!await wait) break;
            while (reader.TryRead(out var update))
            {
                last = update;
                await WriteAsync(new { type = "progress", progress = update }, cancellationToken);
            }
        }
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] LegendFounderAiChatRequest request, CancellationToken cancellationToken)
    {
        if (!FounderGuard.IsFounder(User)) return Forbid();
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null || resolved.Actor is null) return resolved.Error!;
        var operationId = ReadOperationId();
        try
        {
            var result = await _conversation.ReplyAsync(
                User,
                request,
                cancellationToken,
                operationId.HasValue
                    ? (update, token) => _progress.PublishAsync(operationId.Value, update, token)
                    : null);
            return Ok(result);
        }
        catch (ForbidResultException) { return Forbid(); }
        catch (Exception exception)
        {
            _logger.LogError(exception, "LEGEND Founder AI mobile conversation failed.");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new MobileApiErrorResponse(
                    "legend_founder_ai_failed",
                    "LEGEND® Ai encountered an unexpected server error.",
                    CorrelationId(),
                    new Dictionary<string, string[]>()));
        }
        finally
        {
            if (operationId.HasValue) _progress.Complete(operationId.Value);
        }
    }

    private Guid? ReadOperationId()
    {
        if (!Request.Headers.TryGetValue(OperationHeader, out var values)) return null;
        return Guid.TryParse(values.ToString(), out var parsed) && parsed != Guid.Empty ? parsed : null;
    }

    private async ValueTask WriteAsync(object value, CancellationToken cancellationToken)
    {
        await Response.WriteAsync(JsonSerializer.Serialize(value, StreamJsonOptions) + "\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}

public sealed record MobileFounderAiAccessResponse(bool Available);
