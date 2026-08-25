using System.Diagnostics;
using System.Text.Json;
using AgentPortal.Security;
using AgentPortal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers;

[Authorize]
[FounderOnly]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
public sealed class LegendFounderAiController : Controller
{
    private const string OperationHeader = "X-Legend-Ai-Operation-Id";
    private static readonly JsonSerializerOptions StreamJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LegendFounderAiConversationService _conversation;
    private readonly LegendFounderAiProgressBroker _progress;
    private readonly ILogger<LegendFounderAiController> _logger;

    public LegendFounderAiController(
        LegendFounderAiConversationService conversation,
        LegendFounderAiProgressBroker progress,
        ILogger<LegendFounderAiController> logger)
    {
        _conversation = conversation;
        _progress = progress;
        _logger = logger;
    }

    [HttpGet]
    [Route("founder/legend-ai/progress/{operationId:guid}")]
    public async Task Progress(Guid operationId, CancellationToken cancellationToken)
    {
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

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("founder/legend-ai/chat")]
    public async Task<IActionResult> Chat(
        [FromBody] LegendFounderAiChatRequest request,
        CancellationToken cancellationToken)
    {
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

            if (!result.Succeeded)
                return StatusCode(MapStatus(result), result);

            return Ok(result);
        }
        catch (ForbidResultException) { return Forbid(); }
        catch (OperationCanceledException exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND Founder AI conversation was cancelled before a response could be produced.");

            var result = LegendFounderAiChatResponse.UnexpectedFailure(
                request?.Mode);
            return StatusCode(
                StatusCodes.Status408RequestTimeout,
                result with
                {
                    FailureKind = "timeout",
                    Stage = "request_cancellation",
                    Reason = "request_cancelled"
                });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "LEGEND Founder AI conversation failed.");
            return StatusCode(
                StatusCodes.Status502BadGateway,
                LegendFounderAiChatResponse.UnexpectedFailure(
                    request?.Mode));
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

    private static int MapStatus(LegendFounderAiChatResponse result) =>
        result.Succeeded ? StatusCodes.Status200OK : result.FailureKind switch
        {
            "validation" => StatusCodes.Status400BadRequest,
            "configuration" => StatusCodes.Status503ServiceUnavailable,
            "timeout" => StatusCodes.Status504GatewayTimeout,
            "provider_http" when result.ProviderStatusCode == StatusCodes.Status429TooManyRequests => StatusCodes.Status429TooManyRequests,
            "provider_http" when result.ProviderStatusCode == StatusCodes.Status503ServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
            "provider_http" when result.ProviderStatusCode is StatusCodes.Status408RequestTimeout or StatusCodes.Status504GatewayTimeout => StatusCodes.Status504GatewayTimeout,
            "provider_http" or "transport" or "provider_json" => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status502BadGateway
        };
}
