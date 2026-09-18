using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using AgentPortal.Security;
using AgentPortal.Services;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;

namespace AgentPortal.Controllers;

/// <summary>Service-authenticated transport into the existing Azure tool authority.</summary>
[ApiController]
public sealed class LegendCloudflareToolController(
    IConfiguration configuration, IMessagingService messaging, FounderLegendConnectService legend,
    IFounderSoftwareRemediationService remediation, IServiceScopeFactory scopes,
    AgencyCommandService? agencyCommand = null) : ControllerBase
{
    public const string CallbackPath = "/api/founder/legend-ai/cloudflare-tools";

    [AllowAnonymous] // This endpoint authenticates the distinct Cloudflare service signature, never a browser cookie.
    [HttpPost(CallbackPath)]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> Execute(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store, private";
        const string prefix = "LegendConnect:Foundation:Cloudflare:";
        if (!configuration.GetValue<bool>(prefix + "ToolCallbackEnabled")) return StatusCode(503);
        if (Request.QueryString.HasValue || Request.Headers.ContainsKey("Content-Encoding") ||
            !string.Equals(Request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
            return BadRequest();
        var keyId = configuration[prefix + "CallbackKeyId"];
        var suppliedKeyId = Request.Headers["X-Legend-Key-Id"].ToString();
        var nonce = Request.Headers["X-Legend-Nonce"].ToString();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (string.IsNullOrWhiteSpace(keyId) || suppliedKeyId != keyId || nonce.Length is < 22 or > 128 ||
            nonce.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') ||
            !long.TryParse(Request.Headers["X-Legend-Timestamp"], out var timestamp) ||
            timestamp > now + 5000 || timestamp < now - 30000) return Unauthorized();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, timestamp + 30000 - now)));
        cancellationToken = deadline.Token;
        var buffer = new byte[65537];
        var length = await Request.Body.ReadAtLeastAsync(buffer, buffer.Length, false, cancellationToken);
        if (length > 65536) return StatusCode(413);
        var bytes = buffer.AsSpan(0, length).ToArray();
        byte[] key;
        try { key = Convert.FromBase64String(configuration[prefix + "CallbackSigningKey"] ?? ""); }
        catch (FormatException) { return StatusCode(503); }
        bool authenticated;
        try
        {
            authenticated = key.Length is >= 32 and <= 128 && LegendCloudflareServiceSignature.Verify(key,
                "POST", CallbackPath, keyId, timestamp, nonce, bytes, Request.Headers["X-Legend-Signature"].ToString());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        if (!authenticated) return Unauthorized();
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
            var root = document.RootElement;
            if (!root.TryGetProperty("maxCostMicrousd", out var budgetValue) ||
                !budgetValue.TryGetInt64(out var reservedCost) || reservedCost is <= 0 or > 1_000_000_000)
                return BadRequest(new { error = "invalid_cloudflare_tool_budget" });
            now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (root.GetProperty("version").GetString() != "legend-tool-callback.v1" ||
                root.GetProperty("issuedAt").GetInt64() != timestamp ||
                root.GetProperty("expiresAt").GetInt64() <= now || root.GetProperty("expiresAt").GetInt64() > timestamp + 30000)
                return Unauthorized();
            var scope = root.GetProperty("scope");
            if (!Guid.TryParse(root.GetProperty("requestId").GetString(), out var operationId) ||
                !Guid.TryParse(scope.GetProperty("conversationId").GetString(), out var conversationId)) return BadRequest();
            var actor = new MessagingActor(scope.GetProperty("userId").GetString() ?? "", MessagingParticipantTypes.Agent);
            // The signed callback provides only lookup keys. Authority comes from the persisted Azure request.
            var active = await messaging.GetFounderAiOperationDelegationAsync(actor, conversationId, operationId, cancellationToken);
            if (active is null) return StatusCode(403);
            var delegation = active.Delegation;
            var remainingMs = Math.Min(root.GetProperty("expiresAt").GetInt64(),
                new DateTimeOffset(delegation.ExpiresUtc).ToUnixTimeMilliseconds()) - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (remainingMs <= 0) return Unauthorized();
            deadline.CancelAfter(TimeSpan.FromMilliseconds(remainingMs));
            if (delegation.AccountId != configuration[prefix + "AccountId"] ||
                delegation.Environment != configuration[prefix + "Environment"] ||
                root.GetProperty("environment").GetString() != delegation.Environment ||
                scope.GetProperty("accountId").GetString() != delegation.AccountId ||
                scope.GetProperty("tenantId").GetString() != delegation.TenantId ||
                scope.GetProperty("userId").GetString() != delegation.UserId ||
                scope.GetProperty("sessionId").GetString() != delegation.SessionId ||
                scope.GetProperty("authorizationVersion").GetString() != delegation.AuthorizationVersion ||
                !scope.GetProperty("roles").EnumerateArray().Select(x => x.GetString()).SequenceEqual(delegation.Roles)) return StatusCode(403);
            var principal = AuthenticatedRequestBinding.CreatePersistedDelegationPrincipal(
                delegation.UserId, delegation.TenantId, delegation.SessionId, delegation.ExpiresUtc);
            if (!FounderAuthority.Evaluate(principal, FounderGuard.FounderOid, true, _ => false)) return StatusCode(403);
            var actionScope = new FounderAiActionScope(delegation.AccountId, delegation.TenantId, delegation.UserId,
                delegation.SessionId, conversationId.ToString("D"), operationId.ToString("D"), delegation.Roles,
                delegation.AuthorizationVersion, delegation.Environment, delegation.ExpiresUtc, active.RequestFingerprint);
            var call = root.GetProperty("call");
            var callId = call.GetProperty("id").GetString()!;
            var name = call.GetProperty("name").GetString()!;
            var arguments = call.GetProperty("arguments").GetRawText();
            var digest = LegendFounderToolAuthority.ComputeCloudActionDigest(actionScope, name, arguments);
            var idempotency = LegendFounderToolAuthority.ComputeCloudToolIdempotencyKey(actionScope.RequestId, callId);
            if (root.GetProperty("actionDigest").GetString() != digest || root.GetProperty("idempotencyKey").GetString() != idempotency)
                return StatusCode(403);
            var authority = new LegendFounderToolAuthority(legend, remediation, agencyCommand, authorizationScopes: scopes);
            var result = await authority.ExecuteAsync(principal,
                new FounderAiToolCall(callId, name, arguments, IdempotencyKey: idempotency), "legend", cancellationToken,
                LegendConnectExternalProviderPolicy.CloudflareFoundation, serverDerivedScope: actionScope);
            if (System.Text.Encoding.UTF8.GetByteCount(result) > 32768) return StatusCode(502);
            using var output = JsonDocument.Parse(result);
            if (output.RootElement.ValueKind == JsonValueKind.Object &&
                output.RootElement.TryGetProperty("error", out var toolError) && toolError.ValueKind == JsonValueKind.String)
            {
                var code = toolError.GetString();
                if (code == "cloud_action_outcome_unknown")
                    return StatusCode(503, new { error = code });
                if (code?.StartsWith("cloud_action_", StringComparison.Ordinal) == true)
                    return StatusCode(403, new { error = code });
            }
            return Ok(new
            {
                version = "legend-tool-receipt.v1", requestId = actionScope.RequestId,
                contextDigest = root.GetProperty("contextDigest").GetString(), toolCallId = callId,
                actionDigest = digest, idempotencyKey = idempotency, authorizationVersion = delegation.AuthorizationVersion,
                reauthorized = true, output = output.RootElement.Clone(),
                // This is a conservative ledger debit, not measured provider
                // usage or proof of zero infrastructure cost. Never refund it.
                usage = new { known = false, costMicrousd = reservedCost, costEvidence = "reserved_upper_bound" }
            });
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        { return BadRequest(new { error = "invalid_cloudflare_tool_request" }); }
    }
}
