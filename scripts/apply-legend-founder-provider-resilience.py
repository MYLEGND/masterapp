from pathlib import Path

SERVICE = Path("AgentPortal/Services/LegendFounderAiConversationService.cs")
CONTROLLER = Path("AgentPortal/Controllers/LegendFounderAiController.cs")
TEST = Path("AgentPortal.Tests/LegendFounderAiProviderResilienceTests.cs")


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, found {count}")
    return text.replace(old, new, 1)


service = SERVICE.read_text()
service = replace_once(
    service,
    "using System.Diagnostics;\nusing System.Net;",
    "using System.Diagnostics;\nusing System.Globalization;\nusing System.Net;\nusing System.Text.RegularExpressions;",
    "service usings",
)
service = replace_once(
    service,
    "    private const int MaximumRetainedContextCharacters = 16_000;\n",
    "    private const int MaximumRetainedContextCharacters = 16_000;\n"
    "    private const int MaximumProviderAttempts = 5;\n"
    "    private const int MinimumProviderAttemptWindowSeconds = 3;\n"
    "    private const int MaximumProviderCooldownSeconds = 300;\n"
    "    private static readonly object ProviderCooldownSync = new();\n"
    "    private static DateTimeOffset _providerNotBeforeUtc = DateTimeOffset.MinValue;\n",
    "provider resilience constants",
)
service = replace_once(
    service,
    "                exception.StatusCode,\n                reference);",
    "                exception.StatusCode,\n                reference,\n                exception.ProviderErrorCode,\n                exception.RetryAfter is { } retryAfter\n                    ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))\n                    : null);",
    "provider failure response metadata",
)

start_marker = "        var providerClock =\n            Stopwatch.StartNew();\n\n        for (var attempt = 1;"
end_marker = "    private static ValueTask ReportProgressAsync("
start = service.find(start_marker)
end = service.find(end_marker, start)
if start < 0 or end < 0:
    raise RuntimeError("provider send loop markers not found")

new_provider_block = '''        var providerClock =\n            Stopwatch.StartNew();\n\n        for (var attempt = 1;\n             attempt <= MaximumProviderAttempts;\n             attempt++)\n        {\n            await WaitForProviderCooldownAsync(\n                providerBudget,\n                providerClock,\n                cancellationToken);\n\n            var attemptRemaining =\n                providerBudget -\n                providerClock.Elapsed;\n\n            if (attemptRemaining <=\n                TimeSpan.FromSeconds(\n                    MinimumProviderAttemptWindowSeconds))\n            {\n                throw new OperationCanceledException();\n            }\n\n            using var providerAttempt =\n                CancellationTokenSource\n                    .CreateLinkedTokenSource(\n                        cancellationToken);\n\n            providerAttempt.CancelAfter(\n                attemptRemaining);\n\n            var clientRequestId =\n                Guid.NewGuid().ToString("D");\n\n            using var request =\n                new HttpRequestMessage(\n                    HttpMethod.Post,\n                    "v1/responses")\n                {\n                    Content =\n                        JsonContent.Create(payload)\n                };\n\n            request.Headers.TryAddWithoutValidation(\n                "X-Client-Request-Id",\n                clientRequestId);\n\n            request.Headers.Authorization =\n                new AuthenticationHeaderValue(\n                    "Bearer",\n                    apiKey);\n\n            using var response =\n                await client.SendAsync(\n                    request,\n                    HttpCompletionOption.ResponseHeadersRead,\n                    providerAttempt.Token);\n\n            if (response.IsSuccessStatusCode)\n            {\n                ClearExpiredProviderCooldown();\n\n                await using var stream =\n                    await response.Content\n                        .ReadAsStreamAsync(\n                            providerAttempt.Token);\n\n                return await JsonDocument.ParseAsync(\n                    stream,\n                    cancellationToken:\n                        providerAttempt.Token);\n            }\n\n            var transient =\n                IsTransientOpenAiStatus(\n                    response.StatusCode);\n\n            var retryDelay =\n                transient\n                    ? ResolveProviderRetryDelay(\n                        response,\n                        attempt)\n                    : TimeSpan.Zero;\n\n            if (transient)\n            {\n                ExtendProviderCooldown(\n                    retryDelay);\n            }\n\n            if (transient &&\n                attempt < MaximumProviderAttempts)\n            {\n                var remainingAfterResponse =\n                    providerBudget -\n                    providerClock.Elapsed;\n\n                var maximumDelay =\n                    remainingAfterResponse -\n                    TimeSpan.FromSeconds(\n                        MinimumProviderAttemptWindowSeconds);\n\n                if (maximumDelay > TimeSpan.Zero)\n                {\n                    var boundedDelay =\n                        retryDelay <= maximumDelay\n                            ? retryDelay\n                            : maximumDelay;\n\n                    if (boundedDelay > TimeSpan.Zero)\n                    {\n                        _logger.LogWarning(\n                            "LEGEND Founder AI provider transient rejection. " +\n                            "HTTP={StatusCode} Attempt={Attempt}/{MaximumAttempts} " +\n                            "RetryDelayMs={RetryDelayMs} RequestReset={RequestReset} " +\n                            "TokenReset={TokenReset}",\n                            (int)response.StatusCode,\n                            attempt,\n                            MaximumProviderAttempts,\n                            (long)Math.Ceiling(boundedDelay.TotalMilliseconds),\n                            GetProviderHeader(\n                                response,\n                                "x-ratelimit-reset-requests") ??\n                                "unavailable",\n                            GetProviderHeader(\n                                response,\n                                "x-ratelimit-reset-tokens") ??\n                                "unavailable");\n\n                        await Task.Delay(\n                            boundedDelay,\n                            cancellationToken);\n\n                        continue;\n                    }\n                }\n            }\n\n            var errorBody =\n                await response.Content\n                    .ReadAsStringAsync(\n                        providerAttempt.Token);\n\n            if (errorBody.Length > 1_000)\n            {\n                errorBody =\n                    errorBody[..1_000];\n            }\n\n            var providerRequestId =\n                GetProviderHeader(\n                    response,\n                    "x-request-id");\n\n            var providerErrorCode =\n                ExtractProviderErrorCode(\n                    errorBody);\n\n            _logger.LogError(\n                "LEGEND Founder AI provider rejected request. " +\n                "HTTP={StatusCode} ClientRequestId={ClientRequestId} " +\n                "ProviderRequestId={ProviderRequestId} ProviderErrorCode={ProviderErrorCode} " +\n                "RetryAfterMs={RetryAfterMs} RequestLimit={RequestLimit} " +\n                "RequestRemaining={RequestRemaining} RequestReset={RequestReset} " +\n                "TokenLimit={TokenLimit} TokenRemaining={TokenRemaining} " +\n                "TokenReset={TokenReset} Body={Body}",\n                (int)response.StatusCode,\n                clientRequestId,\n                providerRequestId ?? "unavailable",\n                providerErrorCode ?? "unavailable",\n                retryDelay > TimeSpan.Zero\n                    ? (long)Math.Ceiling(retryDelay.TotalMilliseconds)\n                    : 0L,\n                GetProviderHeader(response, "x-ratelimit-limit-requests") ?? "unavailable",\n                GetProviderHeader(response, "x-ratelimit-remaining-requests") ?? "unavailable",\n                GetProviderHeader(response, "x-ratelimit-reset-requests") ?? "unavailable",\n                GetProviderHeader(response, "x-ratelimit-limit-tokens") ?? "unavailable",\n                GetProviderHeader(response, "x-ratelimit-remaining-tokens") ?? "unavailable",\n                GetProviderHeader(response, "x-ratelimit-reset-tokens") ?? "unavailable",\n                errorBody);\n\n            throw new LegendFounderAiProviderException(\n                (int)response.StatusCode,\n                clientRequestId,\n                providerRequestId,\n                providerErrorCode,\n                retryDelay > TimeSpan.Zero\n                    ? retryDelay\n                    : null);\n        }\n\n        return null;\n    }\n\n    private static async Task WaitForProviderCooldownAsync(\n        TimeSpan providerBudget,\n        Stopwatch providerClock,\n        CancellationToken cancellationToken)\n    {\n        TimeSpan delay;\n\n        lock (ProviderCooldownSync)\n        {\n            delay =\n                _providerNotBeforeUtc -\n                DateTimeOffset.UtcNow;\n        }\n\n        if (delay <= TimeSpan.Zero)\n            return;\n\n        var maximumDelay =\n            providerBudget -\n            providerClock.Elapsed -\n            TimeSpan.FromSeconds(\n                MinimumProviderAttemptWindowSeconds);\n\n        if (maximumDelay <= TimeSpan.Zero)\n            throw new OperationCanceledException();\n\n        if (delay > maximumDelay)\n            delay = maximumDelay;\n\n        if (delay > TimeSpan.Zero)\n        {\n            await Task.Delay(\n                delay,\n                cancellationToken);\n        }\n    }\n\n    private static void ExtendProviderCooldown(\n        TimeSpan retryDelay)\n    {\n        if (retryDelay <= TimeSpan.Zero)\n            return;\n\n        var bounded =\n            retryDelay >\n                TimeSpan.FromSeconds(\n                    MaximumProviderCooldownSeconds)\n                ? TimeSpan.FromSeconds(\n                    MaximumProviderCooldownSeconds)\n                : retryDelay;\n\n        var candidate =\n            DateTimeOffset.UtcNow +\n            bounded;\n\n        lock (ProviderCooldownSync)\n        {\n            if (candidate > _providerNotBeforeUtc)\n                _providerNotBeforeUtc = candidate;\n        }\n    }\n\n    private static void ClearExpiredProviderCooldown()\n    {\n        lock (ProviderCooldownSync)\n        {\n            if (_providerNotBeforeUtc <= DateTimeOffset.UtcNow)\n                _providerNotBeforeUtc = DateTimeOffset.MinValue;\n        }\n    }\n\n    private static TimeSpan ResolveProviderRetryDelay(\n        HttpResponseMessage response,\n        int attempt)\n    {\n        var providerHint =\n            ReadRetryAfter(\n                response) ??\n            ReadRateLimitReset(\n                response);\n\n        if (providerHint is { } hinted &&\n            hinted > TimeSpan.Zero)\n        {\n            return hinted >\n                    TimeSpan.FromSeconds(\n                        MaximumProviderCooldownSeconds)\n                ? TimeSpan.FromSeconds(\n                    MaximumProviderCooldownSeconds)\n                : hinted;\n        }\n\n        var exponent =\n            Math.Min(\n                Math.Max(attempt - 1, 0),\n                4);\n\n        var seconds =\n            Math.Pow(2, exponent) +\n            Random.Shared.NextDouble() * 0.35;\n\n        return TimeSpan.FromSeconds(seconds);\n    }\n\n    private static TimeSpan? ReadRetryAfter(\n        HttpResponseMessage response)\n    {\n        if (response.Headers.RetryAfter?.Delta is { } delta &&\n            delta > TimeSpan.Zero)\n        {\n            return delta;\n        }\n\n        if (response.Headers.RetryAfter?.Date is { } date)\n        {\n            var delay =\n                date -\n                DateTimeOffset.UtcNow;\n\n            if (delay > TimeSpan.Zero)\n                return delay;\n        }\n\n        return null;\n    }\n\n    private static TimeSpan? ReadRateLimitReset(\n        HttpResponseMessage response)\n    {\n        TimeSpan? longest = null;\n\n        foreach (var headerName in new[]\n                 {\n                     "x-ratelimit-reset-requests",\n                     "x-ratelimit-reset-tokens"\n                 })\n        {\n            var raw =\n                GetProviderHeader(\n                    response,\n                    headerName);\n\n            if (!TryParseProviderDuration(\n                    raw,\n                    out var parsed))\n            {\n                continue;\n            }\n\n            if (longest is null ||\n                parsed > longest.Value)\n            {\n                longest = parsed;\n            }\n        }\n\n        return longest;\n    }\n\n    private static bool TryParseProviderDuration(\n        string? raw,\n        out TimeSpan duration)\n    {\n        duration = TimeSpan.Zero;\n\n        if (string.IsNullOrWhiteSpace(raw))\n            return false;\n\n        var matches =\n            Regex.Matches(\n                raw.Trim(),\n                @"(?<value>\\d+(?:\\.\\d+)?)(?<unit>ms|s|m|h)",\n                RegexOptions.CultureInvariant |\n                RegexOptions.IgnoreCase);\n\n        if (matches.Count == 0)\n            return false;\n\n        double totalMilliseconds = 0;\n        var consumedCharacters = 0;\n\n        foreach (Match match in matches)\n        {\n            if (match.Index != consumedCharacters ||\n                !double.TryParse(\n                    match.Groups["value"].Value,\n                    NumberStyles.Float,\n                    CultureInfo.InvariantCulture,\n                    out var value))\n            {\n                return false;\n            }\n\n            totalMilliseconds +=\n                match.Groups["unit"].Value.ToLowerInvariant() switch\n                {\n                    "ms" => value,\n                    "s" => value * 1_000d,\n                    "m" => value * 60_000d,\n                    "h" => value * 3_600_000d,\n                    _ => 0d\n                };\n\n            consumedCharacters +=\n                match.Length;\n        }\n\n        if (consumedCharacters != raw.Trim().Length ||\n            totalMilliseconds <= 0 ||\n            double.IsNaN(totalMilliseconds) ||\n            double.IsInfinity(totalMilliseconds))\n        {\n            return false;\n        }\n\n        duration =\n            TimeSpan.FromMilliseconds(\n                Math.Min(\n                    totalMilliseconds,\n                    TimeSpan.FromSeconds(\n                        MaximumProviderCooldownSeconds)\n                        .TotalMilliseconds));\n\n        return true;\n    }\n\n    private static string? GetProviderHeader(\n        HttpResponseMessage response,\n        string headerName) =>\n        response.Headers.TryGetValues(\n            headerName,\n            out var values)\n            ? values.FirstOrDefault()\n            : null;\n\n    private static string? ExtractProviderErrorCode(\n        string errorBody)\n    {\n        if (string.IsNullOrWhiteSpace(errorBody))\n            return null;\n\n        try\n        {\n            using var document =\n                JsonDocument.Parse(\n                    errorBody);\n\n            if (!document.RootElement.TryGetProperty(\n                    "error",\n                    out var error) ||\n                error.ValueKind != JsonValueKind.Object)\n            {\n                return null;\n            }\n\n            if (error.TryGetProperty(\n                    "code",\n                    out var code) &&\n                code.ValueKind == JsonValueKind.String &&\n                !string.IsNullOrWhiteSpace(\n                    code.GetString()))\n            {\n                return code.GetString();\n            }\n\n            if (error.TryGetProperty(\n                    "type",\n                    out var type) &&\n                type.ValueKind == JsonValueKind.String)\n            {\n                return type.GetString();\n            }\n        }\n        catch (JsonException)\n        {\n            // The provider body is diagnostic only; invalid JSON must not\n            // replace the authoritative HTTP failure classification.\n        }\n\n        return null;\n    }\n\n'''
service = service[:start] + new_provider_block + service[end:]

service = replace_once(
    service,
    '''        public LegendFounderAiProviderException(\n            int statusCode,\n            string clientRequestId,\n            string? providerRequestId)\n            : base(\n                $"Legend Founder AI provider returned HTTP {statusCode}.")\n        {\n            StatusCode = statusCode;\n            ClientRequestId = clientRequestId;\n            ProviderRequestId = providerRequestId;\n        }\n\n        public int StatusCode { get; }\n\n        public string ClientRequestId { get; }\n\n        public string? ProviderRequestId { get; }\n''',
    '''        public LegendFounderAiProviderException(\n            int statusCode,\n            string clientRequestId,\n            string? providerRequestId,\n            string? providerErrorCode,\n            TimeSpan? retryAfter)\n            : base(\n                $"Legend Founder AI provider returned HTTP {statusCode}.")\n        {\n            StatusCode = statusCode;\n            ClientRequestId = clientRequestId;\n            ProviderRequestId = providerRequestId;\n            ProviderErrorCode = providerErrorCode;\n            RetryAfter = retryAfter;\n        }\n\n        public int StatusCode { get; }\n\n        public string ClientRequestId { get; }\n\n        public string? ProviderRequestId { get; }\n\n        public string? ProviderErrorCode { get; }\n\n        public TimeSpan? RetryAfter { get; }\n''',
    "provider exception metadata",
)
service = replace_once(
    service,
    '''    string? FailureKind = null,\n    int? ProviderStatusCode = null,\n    string? Reference = null)\n{\n    public static LegendFounderAiChatResponse Failure(\n        string error,\n        string? failureKind = null,\n        int? providerStatusCode = null,\n        string? reference = null) =>\n        new(\n            false,\n            "legend",\n            null,\n            error,\n            failureKind,\n            providerStatusCode,\n            reference);\n''',
    '''    string? FailureKind = null,\n    int? ProviderStatusCode = null,\n    string? Reference = null,\n    string? ProviderErrorCode = null,\n    int? RetryAfterSeconds = null)\n{\n    public static LegendFounderAiChatResponse Failure(\n        string error,\n        string? failureKind = null,\n        int? providerStatusCode = null,\n        string? reference = null,\n        string? providerErrorCode = null,\n        int? retryAfterSeconds = null) =>\n        new(\n            false,\n            "legend",\n            null,\n            error,\n            failureKind,\n            providerStatusCode,\n            reference,\n            providerErrorCode,\n            retryAfterSeconds);\n''',
    "chat response metadata",
)
SERVICE.write_text(service)

controller = CONTROLLER.read_text()
controller = replace_once(
    controller,
    '''            if (!result.Succeeded)\n                return StatusCode(MapStatus(result), result);\n''',
    '''            if (!result.Succeeded)\n            {\n                if (result.RetryAfterSeconds is > 0)\n                {\n                    Response.Headers["Retry-After"] =\n                        result.RetryAfterSeconds.Value.ToString();\n                }\n\n                return StatusCode(MapStatus(result), result);\n            }\n''',
    "controller retry-after response",
)
controller = replace_once(
    controller,
    '''            "timeout" => StatusCodes.Status504GatewayTimeout,\n            "provider_http" or "transport" or "provider_json" => StatusCodes.Status502BadGateway,\n            _ => StatusCodes.Status502BadGateway\n''',
    '''            "timeout" => StatusCodes.Status504GatewayTimeout,\n            "provider_http" when result.ProviderStatusCode == StatusCodes.Status429TooManyRequests\n                => StatusCodes.Status429TooManyRequests,\n            "provider_http" when result.ProviderStatusCode == StatusCodes.Status503ServiceUnavailable\n                => StatusCodes.Status503ServiceUnavailable,\n            "provider_http" when result.ProviderStatusCode is StatusCodes.Status408RequestTimeout or StatusCodes.Status504GatewayTimeout\n                => StatusCodes.Status504GatewayTimeout,\n            "provider_http" or "transport" or "provider_json" => StatusCodes.Status502BadGateway,\n            _ => StatusCodes.Status502BadGateway\n''',
    "controller provider status mapping",
)
CONTROLLER.write_text(controller)

TEST.write_text(r'''using System.Net;
using System.Reflection;
using AgentPortal.Controllers;
using AgentPortal.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFounderAiProviderResilienceTests
{
    [Theory]
    [InlineData(429, StatusCodes.Status429TooManyRequests)]
    [InlineData(503, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(504, StatusCodes.Status504GatewayTimeout)]
    [InlineData(408, StatusCodes.Status504GatewayTimeout)]
    [InlineData(400, StatusCodes.Status502BadGateway)]
    public void Controller_MapsProviderFailuresTruthfully(
        int providerStatus,
        int expectedStatus)
    {
        var mapStatus = typeof(LegendFounderAiController)
            .GetMethod(
                "MapStatus",
                BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(mapStatus);

        var result = LegendFounderAiChatResponse.Failure(
            "provider rejected request",
            "provider_http",
            providerStatus,
            "req_test");

        Assert.Equal(
            expectedStatus,
            Assert.IsType<int>(mapStatus!.Invoke(null, new object[] { result })));
    }

    [Fact]
    public void RetryPolicy_HonorsRetryAfterBeyondLegacyFiveSecondCap()
    {
        var resolveDelay = typeof(LegendFounderAiConversationService)
            .GetMethod(
                "ResolveProviderRetryDelay",
                BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(resolveDelay);

        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            TimeSpan.FromSeconds(12));

        var delay = Assert.IsType<TimeSpan>(
            resolveDelay!.Invoke(null, new object[] { response, 1 }));

        Assert.True(delay >= TimeSpan.FromSeconds(11.9));
    }

    [Theory]
    [InlineData("250ms", 0.25)]
    [InlineData("2s", 2.0)]
    [InlineData("1m30s", 90.0)]
    public void RetryPolicy_ParsesProviderRateLimitResetDurations(
        string raw,
        double expectedSeconds)
    {
        var parseDuration = typeof(LegendFounderAiConversationService)
            .GetMethod(
                "TryParseProviderDuration",
                BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(parseDuration);

        var args = new object?[] { raw, null };
        var parsed = Assert.IsType<bool>(parseDuration!.Invoke(null, args));

        Assert.True(parsed);
        var duration = Assert.IsType<TimeSpan>(args[1]);
        Assert.InRange(
            duration.TotalSeconds,
            expectedSeconds - 0.001,
            expectedSeconds + 0.001);
    }

    [Fact]
    public void FailureContract_PreservesSafeProviderRecoveryMetadata()
    {
        var failure = LegendFounderAiChatResponse.Failure(
            "provider rejected request",
            "provider_http",
            429,
            "req_test",
            "rate_limit_exceeded",
            17);

        Assert.Equal(429, failure.ProviderStatusCode);
        Assert.Equal("req_test", failure.Reference);
        Assert.Equal("rate_limit_exceeded", failure.ProviderErrorCode);
        Assert.Equal(17, failure.RetryAfterSeconds);
    }
}
''')

print("LEGEND Founder AI provider resilience patch applied.")
