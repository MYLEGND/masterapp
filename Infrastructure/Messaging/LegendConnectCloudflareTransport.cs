using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Messaging;

/// <summary>Constructed only after Azure resolves current identity and conversation ownership.</summary>
public sealed record LegendCloudflareRequestScope(
    string RequestId, string TenantId, string UserId, string SessionId,
    string ConversationId, IReadOnlyList<string> Roles, string AuthorizationVersion, DateTime? ExpiresUtc = null);

internal sealed partial class LegendConnectModelInferenceTransport
{
    private async Task<LegendModelEvaluationGenerationResult> SendCloudflareTaskAsync(
        LegendModelTaskRequest task, CancellationToken cancellationToken)
    {
        const string prefix = "LegendConnect:Foundation:";
        if (task.ProviderPolicy is not { AllowExternalProviders: true, AllowCloudflareInference: true })
            return new(false, null, "cloudflare_inference_forbidden_by_policy");
        var scope = task.CloudflareScope;
        if (scope is null || scope.UserId != task.RequestingActorId ||
            new[] { scope.RequestId, scope.TenantId, scope.UserId, scope.SessionId,
                scope.ConversationId, scope.AuthorizationVersion }.Any(string.IsNullOrWhiteSpace) ||
            scope.Roles.Count is < 1 or > 32)
            return new(false, null, "cloudflare_authenticated_scope_required");
        var endpointText = _configuration[prefix + "Endpoint"];
        if (!_configuration.GetValue<bool>(prefix + "Enabled") ||
            !Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != "https" || endpoint.Port != 443 || endpoint.IsLoopback ||
            IPAddress.TryParse(endpoint.Host, out _) || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath != "/v1/legend/respond")
            return new(false, null, "cloudflare_endpoint_unavailable");
        var accountId = _configuration[prefix + "Cloudflare:AccountId"];
        var keyId = _configuration[prefix + "Cloudflare:KeyId"];
        var secret = _configuration[prefix + "Cloudflare:SigningKey"];
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(secret))
            return new(false, null, "cloudflare_service_credentials_unavailable");
        byte[] key;
        try { key = Convert.FromBase64String(secret); }
        catch (FormatException) { return new(false, null, "cloudflare_service_key_invalid"); }
        if (key.Length is < 32 or > 128)
        {
            CryptographicOperations.ZeroMemory(key);
            return new(false, null, "cloudflare_service_key_invalid");
        }
        long? observedCost = null;
        string? observedUsage = null;
        try
        {
            var cost = _configuration.GetValue<long?>(prefix + "Cloudflare:MaxCostMicrousd") ?? 0;
            var seconds = _configuration.GetValue<int?>(prefix + "TimeoutSeconds") ?? 60;
            var tokens = task.MaxOutputTokens ?? _configuration.GetValue<int?>(prefix + "MaxOutputTokens") ?? 2048;
            if (cost is <= 0 or > 1_000_000_000 || seconds is < 1 or > 120 || tokens is < 1 or > 16384)
                return new(false, null, "cloudflare_limits_invalid");
            var toolSchemas = task.Tools ?? JsonSerializer.SerializeToElement(Array.Empty<object>());
            if (toolSchemas.ValueKind != JsonValueKind.Array || toolSchemas.GetArrayLength() > 64)
                return new(false, null, "cloudflare_tool_schema_invalid");
            var hasTools = toolSchemas.GetArrayLength() != 0;
            if ((task.AllowTools || task.RequireToolCall || hasTools) &&
                (!_configuration.GetValue<bool>(prefix + "Cloudflare:ToolCallbackEnabled") || !task.AllowTools || !hasTools))
                return new(false, null, "cloudflare_tool_callback_not_qualified");
            // Mandatory evidence needs an application-verified execution receipt;
            // exposing a tool is not proof that the required read occurred.
            if (task.RequireToolCall)
                return new(false, null, "cloudflare_required_tool_receipt_not_qualified");
            var messages = new List<object> { new { role = "system", content = task.Instructions } };
            if (task.ConversationInput is { } history)
            {
                if (history.ValueKind != JsonValueKind.Array) return new(false, null, "cloudflare_conversation_invalid");
                foreach (var message in history.EnumerateArray())
                {
                    if (!message.TryGetProperty("role", out var role) ||
                        role.GetString() is not ("user" or "assistant") ||
                        !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                        return new(false, null, "cloudflare_conversation_invalid");
                    messages.Add(new { role = role.GetString(), content = content.GetString() });
                }
            }
            else messages.Add(new { role = "user", content = task.Input });
            if (task.EvidenceParts is { Count: > 0 })
                return new(false, null, "cloudflare_evidence_projection_required");
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var expires = Math.Min(now + seconds * 1000L, scope.ExpiresUtc is { } scopeExpiry
                ? new DateTimeOffset(scopeExpiry).ToUnixTimeMilliseconds() : long.MaxValue);
            if (expires <= now) return new(false, null, "cloudflare_scope_expired");
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = "legend-cloudflare.v1", requestId = scope.RequestId, issuedAt = now, expiresAt = expires,
                scope = new { accountId, tenantId = scope.TenantId, userId = scope.UserId, sessionId = scope.SessionId,
                    conversationId = scope.ConversationId, roles = scope.Roles, authorizationVersion = scope.AuthorizationVersion },
                task = new { kind = "general", messages, tools = toolSchemas, requiredCapabilities = hasTools ? new[] { "text", "tools" } : new[] { "text" } },
                limits = new { deadlineUnixMs = expires, maxOutputTokens = tokens, maxIterations = hasTools ? 3 : 1,
                    maxModelCalls = hasTools ? 3 : 1, maxToolCalls = hasTools ? 4 : 0, maxCostMicrousd = cost },
                stream = false
            });
            if (payload.Length > 131072) return new(false, null, "cloudflare_context_too_large");
            var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            var stamp = now.ToString(CultureInfo.InvariantCulture);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.Add("X-Legend-Key-Id", keyId);
            request.Headers.Add("X-Legend-Timestamp", stamp);
            request.Headers.Add("X-Legend-Nonce", nonce);
            request.Headers.Add("X-Legend-Signature", LegendCloudflareServiceSignature.Sign(key, "POST", endpoint.AbsolutePath, keyId, now, nonce, payload));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(expires - now));
            using var response = await _clients.CreateClient("LegendCloudflareFoundation")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            // Bound both success and error bodies before materializing provider output.
            await response.Content.LoadIntoBufferAsync(262144, deadline.Token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token));
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var version) ||
                version.GetString() != "legend-cloudflare.v1" || !root.TryGetProperty("requestId", out var returnedId) ||
                returnedId.GetString() != scope.RequestId)
                return new(false, null, "cloudflare_execution_failed");
            var usage = root.GetProperty("usage");
            if (!usage.TryGetProperty("costMicrousd", out var chargedValue) ||
                chargedValue.ValueKind != JsonValueKind.Number || !chargedValue.TryGetInt64(out var charged) || charged < 0)
                return new(false, null, "cloudflare_usage_unverified");
            observedCost = charged;
            observedUsage = JsonSerializer.Serialize(new { usage = usage.Clone() });
            if (!response.IsSuccessStatusCode || !root.TryGetProperty("status", out var status) || status.GetString() != "completed")
            {
                var code = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String
                        ? codeValue.GetString() : null;
                if (code is null || code.Length > 100 || code.Any(c => c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
                    code = "execution_failed";
                return new(false, null, "cloudflare_" + code, CostMicrounits: charged, InferenceSettings: observedUsage);
            }
            var provider = root.GetProperty("provider");
            var modelId = provider.GetProperty("modelId").GetString();
            if (provider.GetProperty("name").GetString() != "cloudflare-workers-ai" ||
                provider.GetProperty("hosting").GetString() != "cloudflare" || modelId?.StartsWith("@cf/", StringComparison.Ordinal) != true)
                return new(false, null, "cloudflare_provider_receipt_invalid", CostMicrounits: charged, InferenceSettings: observedUsage);
            if (usage.GetProperty("costEvidence").GetString() is not ("provider_usage" or "reserved_upper_bound"))
                return new(false, null, "cloudflare_usage_unverified", CostMicrounits: charged, InferenceSettings: observedUsage);
            var text = root.GetProperty("text").GetString();
            if (string.IsNullOrWhiteSpace(text)) return new(false, null, "cloudflare_empty_response", CostMicrounits: charged, InferenceSettings: observedUsage);
            var output = JsonSerializer.SerializeToElement(new { model = modelId, status = "completed",
                output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text } } } } });
            return new(true, text, CostMicrounits: charged, Output: output, ModelVersion: modelId, Hosting: "CloudflareHosted",
                InferenceSettings: JsonSerializer.Serialize(new
                {
                    usage = usage.Clone(),
                    toolResults = root.TryGetProperty("toolResults", out var toolResults) ? toolResults.Clone() : (JsonElement?)null,
                    modelSettings = root.TryGetProperty("modelSettings", out var settings) ? settings.Clone() : (JsonElement?)null,
                    executionMode = root.TryGetProperty("executionMode", out var executionMode) ? executionMode.GetString() : null
                }));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, null, "cloudflare_deadline_exceeded"); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return new(false, null, "cloudflare_transport_or_receipt_failed", CostMicrounits: observedCost, InferenceSettings: observedUsage); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
