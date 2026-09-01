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
    private const string StreamMediaType = "application/x-ndjson";
    private static readonly TimeSpan StreamHeartbeatInterval = TimeSpan.FromSeconds(4);
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
        if (request is null)
        {
            return BadRequest(
                LegendFounderAiChatResponse.InvalidMode(
                    "A chat request body is required."));
        }

        var operationId = ReadOperationId();

        // A long-running provider or governed inspection must not leave the
        // only response connection idle.  The production portal is served
        // directly by App Service, but the observed 26-second 504 proves an
        // outer request boundary can still end an otherwise healthy POST.
        // The Founder client explicitly opts into this bounded NDJSON protocol
        // so the existing endpoint can send progress and a final structured
        // result on one connection.  It is not a second chat authority or
        // endpoint; the same conversation service remains authoritative.
        if (AcceptsProgressStream())
        {
            await StreamChatAsync(
                request,
                operationId,
                cancellationToken);
            return new EmptyResult();
        }

        try
        {
            var result = await ExecuteAsync(
                request,
                operationId,
                cancellationToken,
                operationId.HasValue
                    ? (update, token) => _progress.PublishAsync(operationId.Value, update, token)
                    : null);

            if (!result.Succeeded)
                return StatusCode(MapStatus(result), result);

            return Ok(result);
        }
        finally
        {
            if (operationId.HasValue)
                _progress.Complete(operationId.Value);
        }
    }

    private async Task StreamChatAsync(
        LegendFounderAiChatRequest request,
        Guid? operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = StreamMediaType + "; charset=utf-8";
        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        using var writeGate = new SemaphoreSlim(1, 1);
        var started = Stopwatch.GetTimestamp();
        var completed = new List<LegendFounderAiProgressEvent>();

        async ValueTask WriteFrameAsync(object value, CancellationToken token)
        {
            await writeGate.WaitAsync(token);
            try
            {
                await WriteAsync(value, token);
            }
            finally
            {
                writeGate.Release();
            }
        }

        async ValueTask PublishAsync(
            LegendFounderAiProgressEvent update,
            CancellationToken token)
        {
            if (update.Stage is "native_response" or "tool_complete" or "response")
                completed.Add(update);

            await WriteFrameAsync(
                new { type = "progress", progress = update },
                token);
        }

        try
        {
            await WriteFrameAsync(
                new
                {
                    type = "accepted",
                    operationId,
                    responseAuthority = RequestedAuthority(request.Mode)
                },
                cancellationToken);

            var execution = ExecuteAsync(
                request,
                operationId,
                cancellationToken,
                PublishAsync);

            while (!execution.IsCompleted)
            {
                var heartbeat = Task.Delay(
                    StreamHeartbeatInterval,
                    cancellationToken);
                if (await Task.WhenAny(execution, heartbeat) == execution)
                    break;

                await WriteFrameAsync(
                    new
                    {
                        type = "heartbeat",
                        elapsedSeconds = (int)Math.Round(
                            Stopwatch.GetElapsedTime(started).TotalSeconds)
                    },
                    cancellationToken);
            }

            var result = await execution;
            var completedWork = completed
                .Select(update => update.Tool ?? update.Stage)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            result = result with
            {
                OperationId = operationId?.ToString("D"),
                CompletedWork = completedWork,
                RemainingWork = result.Succeeded
                    ? Array.Empty<string>()
                    : [result.Stage ?? "unknown"],
                // This transport repair keeps the original operation alive;
                // it never claims a durable resume that does not exist.
                Resumable = false
            };

            await WriteFrameAsync(
                new
                {
                    type = "result",
                    status = MapStatus(result),
                    result
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The requester disconnected, so no structured frame can be
            // delivered.  ExecuteAsync has already received the cancellation
            // and performs no background mutation after this point.
            _logger.LogInformation(
                "LEGEND Founder AI progress stream was cancelled by the client. OperationId={OperationId}",
                operationId);
        }
        finally
        {
            if (operationId.HasValue)
                _progress.Complete(operationId.Value);
        }
    }

    private async Task<LegendFounderAiChatResponse> ExecuteAsync(
        LegendFounderAiChatRequest request,
        Guid? operationId,
        CancellationToken cancellationToken,
        Func<LegendFounderAiProgressEvent, CancellationToken, ValueTask>? progress)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await _conversation.ReplyAsync(
                User,
                request,
                cancellationToken,
                progress);

            _logger.LogInformation(
                "LEGEND Founder AI conversation completed. OperationId={OperationId} Mode={Mode} Authority={Authority} Stage={Stage} Reason={Reason} Succeeded={Succeeded} ElapsedMs={ElapsedMs}",
                operationId,
                result.Mode,
                result.ResponseAuthority,
                result.Stage ?? "completed",
                result.Reason ?? "none",
                result.Succeeded,
                (long)Math.Ceiling(Stopwatch.GetElapsedTime(started).TotalMilliseconds));

            return result;
        }
        catch (ForbidResultException)
        {
            var mode = string.Equals(
                request?.Mode?.Trim(),
                "teacher",
                StringComparison.OrdinalIgnoreCase)
                ? "teacher"
                : string.Equals(
                    request?.Mode?.Trim(),
                    "legend",
                    StringComparison.OrdinalIgnoreCase)
                    ? "legend"
                    : "invalid";

            return LegendFounderAiChatResponse.ModeFailure(
                mode,
                "Founder authorization is required for this operation.",
                "authorization",
                "authorization",
                "founder_authorization_required");
        }
        catch (OperationCanceledException exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND Founder AI conversation was cancelled before a response could be produced.");

            return LegendFounderAiChatResponse.UnexpectedFailure(
                request?.Mode) with
            {
                FailureKind = "timeout",
                Stage = "request_cancellation",
                Reason = "request_cancelled"
            };
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "LEGEND Founder AI conversation failed.");
            return LegendFounderAiChatResponse.UnexpectedFailure(
                request?.Mode);
        }
    }

    private bool AcceptsProgressStream() =>
        Request.Headers.Accept.Any(value =>
            (value ?? string.Empty).Contains(
                StreamMediaType,
                StringComparison.OrdinalIgnoreCase));

    private static string RequestedAuthority(string? mode) =>
        string.Equals(mode?.Trim(), "teacher", StringComparison.OrdinalIgnoreCase)
            ? "OpenAITeacher"
            : string.Equals(mode?.Trim(), "legend", StringComparison.OrdinalIgnoreCase)
                ? "LegendAi"
                : "NoResponder";

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
            "authorization" => StatusCodes.Status403Forbidden,
            "configuration" => StatusCodes.Status503ServiceUnavailable,
            "language_identification" when result.Reason == "source_language_identification_unavailable" => StatusCodes.Status503ServiceUnavailable,
            "language_identification" => StatusCodes.Status422UnprocessableEntity,
            "timeout" => StatusCodes.Status504GatewayTimeout,
            "provider_http" when result.ProviderStatusCode == StatusCodes.Status429TooManyRequests => StatusCodes.Status429TooManyRequests,
            "provider_http" when result.ProviderStatusCode == StatusCodes.Status503ServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
            "provider_http" when result.ProviderStatusCode is StatusCodes.Status408RequestTimeout or StatusCodes.Status504GatewayTimeout => StatusCodes.Status504GatewayTimeout,
            "provider_http" or "transport" or "provider_json" => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status502BadGateway
        };
}
