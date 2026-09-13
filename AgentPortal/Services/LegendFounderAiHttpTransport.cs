using System.Diagnostics;
using System.Text.Json;
using AgentPortal.Security;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

using System.Security.Claims;

namespace AgentPortal.Services;

// One HTTP framing/execution adapter for web and mobile. Authorization stays
// at each endpoint; inference remains in the registered conversation service.
internal sealed class LegendFounderAiHttpTransport
{
    private const string OperationHeader = "X-Legend-Ai-Operation-Id";
    private const string StreamMediaType = "application/x-ndjson";
    private static readonly TimeSpan StreamHeartbeatInterval = TimeSpan.FromSeconds(4);
    private static readonly JsonSerializerOptions StreamJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LegendFounderAiConversationService _conversation;
    private readonly LegendFounderAiProgressBroker _progress;
    private readonly ILogger _logger;

    public LegendFounderAiHttpTransport(
        HttpContext context,
        LegendFounderAiConversationService conversation,
        LegendFounderAiProgressBroker progress,
        ILogger logger)
    {
        _context = context;
        _conversation = conversation;
        _progress = progress;
        _logger = logger;
    }

    private readonly HttpContext _context;
    private bool _unexpectedExecutionFailure;
    private HttpRequest Request => _context.Request;
    private HttpResponse Response => _context.Response;
    private HttpContext HttpContext => _context;
    private ClaimsPrincipal User => _context.User;

    public async Task ProgressAsync(Guid operationId, CancellationToken cancellationToken)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var reader = _progress.Subscribe(User, operationId);
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

    public async Task<IActionResult> ChatAsync(
        LegendFounderAiChatRequest request,
        CancellationToken cancellationToken,
        bool legacyMobileJson = false)
    {
        if (request is null)
        {
            return new BadRequestObjectResult(
                LegendFounderAiChatResponse.InvalidMode(
                    "A chat request body is required."));
        }

        using var transportActivity = Activity.Current is null
            ? new Activity("LegendFounderAiHttpTransport.ChatAsync").SetIdFormat(ActivityIdFormat.W3C).Start()
            : null;
        var transportTraceId = Activity.Current?.TraceId.ToString();
        var transportStarted = Stopwatch.GetTimestamp();
        var operationId = ReadOperationId();
        Response.OnCompleted(() =>
        {
            // Server response completion is observable here; browser receipt
            // and rendering still require an authenticated end-to-end check.
            _logger.LogInformation(
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} TraceId={TraceId} OperationId={OperationId} ElapsedMs={ElapsedMs} StatusCode={StatusCode}",
                "ResponseCompleted", "LegendFounderAiHttpTransport.ChatAsync", "response_transport",
                transportTraceId, operationId, Stopwatch.GetElapsedTime(transportStarted).TotalMilliseconds,
                Response.StatusCode);
            return Task.CompletedTask;
        });

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
                    ? (update, token) => _progress.PublishAsync(User, operationId.Value, update, token)
                    : null);

            if (legacyMobileJson)
            {
                if (cancellationToken.IsCancellationRequested)
                    return new EmptyResult();
                if (result.FailureKind == "authorization")
                    return new ForbidResult();
                if (_unexpectedExecutionFailure)
                    return new ObjectResult(new AgentPortal.Mobile.MobileApiErrorResponse(
                        "legend_founder_ai_failed", "LEGEND® Ai encountered an unexpected server error.",
                        HttpContext.TraceIdentifier, new Dictionary<string, string[]>()))
                    { StatusCode = StatusCodes.Status500InternalServerError };
            }

            if (!result.Succeeded && (!legacyMobileJson || result.FailureKind == "authorization"))
                return new ObjectResult(result) { StatusCode = MapStatus(result) };

            return new OkObjectResult(result);
        }
        finally
        {
            if (operationId.HasValue)
                _progress.Complete(User, operationId.Value);
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

        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = streamCancellation.Token;
        Task<LegendFounderAiChatResponse>? execution = null;
        using var writeGate = new SemaphoreSlim(1, 1);
        var started = Stopwatch.GetTimestamp();
        var observations = new Dictionary<string, LegendFounderAiProgressEvent>(StringComparer.Ordinal);

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
            RecordWorkObservation(observations, update);

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

            execution = ExecuteAsync(
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
            var completedWork = observations
                .Where(item => item.Value.Stage != "tool_unavailable")
                .Select(item => item.Key)
                .ToArray();
            var remainingWork = observations
                .Where(item => item.Value.Stage == "tool_unavailable")
                .Select(item => item.Key)
                .ToList();
            if (!result.Succeeded)
                remainingWork.Add(result.Stage ?? "unknown");

            result = result with
            {
                OperationId = operationId?.ToString("D"),
                CompletedWork = completedWork,
                RemainingWork = remainingWork.Distinct(StringComparer.Ordinal).ToArray(),
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
            // Join the original operation before disposing the write gate.
            // Disconnect/Stop must not leave an unobserved responder running.
            streamCancellation.Cancel();
            if (execution is not null)
                await execution;
            if (operationId.HasValue)
                _progress.Complete(User, operationId.Value);
        }
    }

    internal static void RecordWorkObservation(
        IDictionary<string, LegendFounderAiProgressEvent> observations,
        LegendFounderAiProgressEvent update)
    {
        if (update.Stage is not ("native_response" or "tool_complete" or "tool_unavailable" or "response"))
            return;
        var identity = update.Tool is null
            ? update.Stage
            : update.ScopeIdentity ?? update.Tool;
        // Latest evidence owns its effective scope. An unrelated successful
        // read cannot remove this scope's failed observation.
        observations[identity] = update;
    }

    private async Task<LegendFounderAiChatResponse> ExecuteAsync(
        LegendFounderAiChatRequest request,
        Guid? operationId,
        CancellationToken cancellationToken,
        Func<LegendFounderAiProgressEvent, CancellationToken, ValueTask>? progress)
    {
        // Reuse the server trace when present. Direct controller execution
        // still gets one identity shared by all nested runtime stages.
        using var requestActivity = Activity.Current is null
            ? new Activity("LegendFounderAiHttpTransport.ExecuteAsync").SetIdFormat(ActivityIdFormat.W3C).Start()
            : null;
        using var requestScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["LegendRequestTraceId"] = Activity.Current?.TraceId.ToString(),
            ["LegendOperationId"] = operationId
        });
        var started = Stopwatch.GetTimestamp();
        var executionOutcome = "failed";
        var domainOutcome = "not_observed";
        var reasonCode = "unexpected_execution_failure";
        _logger.LogInformation(
            "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} StartedUtc={StartedUtc}",
            "StageStarted", "LegendFounderAiHttpTransport.ExecuteAsync", "request", DateTimeOffset.UtcNow);
        try
        {
            var result = await _conversation.ReplyAsync(
                User,
                request,
                cancellationToken,
                progress);

            executionOutcome = "completed";
            domainOutcome = result.Succeeded ? "succeeded" : "failed";
            reasonCode = LegendConnectTelemetry.NormalizeDiagnosticReason(result.Reason);
            _logger.LogInformation(
                "LEGEND Founder AI conversation completed. OperationId={OperationId} Mode={Mode} Authority={Authority} Stage={Stage} Reason={Reason} Succeeded={Succeeded} ElapsedMs={ElapsedMs}",
                operationId,
                result.Mode,
                result.ResponseAuthority,
                result.Stage ?? "completed",
                reasonCode,
                result.Succeeded,
                (long)Math.Ceiling(Stopwatch.GetElapsedTime(started).TotalMilliseconds));

            return result;
        }
        catch (ForbidResultException)
        {
            domainOutcome = "denied";
            reasonCode = "founder_authorization_required";
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
        catch (OperationCanceledException)
        {
            executionOutcome = "cancelled";
            reasonCode = "request_cancelled";
            _logger.LogWarning(
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
            _unexpectedExecutionFailure = true;
            _logger.LogError("LEGEND Founder AI conversation failed. ExceptionType={ExceptionType}", exception.GetType().Name);
            return LegendFounderAiChatResponse.UnexpectedFailure(
                request?.Mode);
        }
        finally
        {
            _logger.LogInformation(
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} ExecutionOutcome={ExecutionOutcome} DomainOutcome={DomainOutcome} ReasonCode={ReasonCode} ElapsedMs={ElapsedMs} CancellationRequested={CancellationRequested}",
                "StageEnded", "LegendFounderAiHttpTransport.ExecuteAsync", "request", executionOutcome,
                domainOutcome, reasonCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                cancellationToken.IsCancellationRequested);
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

    internal static int MapStatus(LegendFounderAiChatResponse result) =>
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
