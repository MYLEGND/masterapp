using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPortal.Services;

internal sealed partial class LegendFounderToolAuthority
{
    private const int MaximumCloudActionBytes = 32768;

    /// <summary>
    /// Called only after an authenticated Founder reviews this exact action.
    /// A model confirmation, a request boolean or a legacy correlation ID is
    /// never an input to approval issuance. The callback cannot issue approvals.
    /// </summary>
    internal async Task<FounderAiActionApprovalReceipt> IssueCloudActionApprovalAsync(
        ClaimsPrincipal founder, FounderAiActionScope scope, string toolName,
        string argumentsJson, DateTime expiresUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        scope = scope with { Roles = scope.Roles?.ToArray() ?? [] };
        var now = DateTime.UtcNow;
        if (expiresUtc.Kind != DateTimeKind.Utc || expiresUtc <= now || expiresUtc > scope.ExpiresUtc ||
            IsReadOnlyFounderTool(toolName))
            return new(false, "cloud_action_approval_invalid");
        if (!TryPrepareCloudAction(scope, toolName, argumentsJson, out var arguments, out var digest))
            return new(false, "cloud_action_arguments_invalid");
        if (!await ReauthorizeCloudScopeAsync(founder, scope, cancellationToken))
            return new(false, "cloud_action_scope_denied");

        using var services = _authorizationScopes!.CreateScope();
        var db = services.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var existing = await db.FounderAiActionAuthorizations.AsNoTracking()
            .SingleOrDefaultAsync(row => row.ActionDigest == digest, cancellationToken);
        if (existing is not null)
            return MatchesCloudApproval(existing, scope, toolName, arguments) && existing.State == "Approved" &&
                   existing.ExpiresUtc > DateTime.UtcNow
                ? new(true, null, existing.Id, existing.ActionDigest, existing.ExpiresUtc)
                : new(false, "cloud_action_approval_already_consumed");

        var approval = new FounderAiActionAuthorization
        {
            ActionDigest = digest, ScopeDigest = ComputeCloudScopeDigest(scope), AccountId = scope.AccountId,
            TenantId = scope.TenantId, UserId = scope.UserId, SessionId = scope.SessionId,
            ConversationId = Guid.Parse(scope.ConversationId), RequestId = Guid.Parse(scope.RequestId),
            Environment = scope.Environment, AuthorizationVersion = scope.AuthorizationVersion,
            ToolName = toolName, CanonicalArgumentsJson = arguments, ApprovedUtc = now, ExpiresUtc = expiresUtc
        };
        db.FounderAiActionAuthorizations.Add(approval);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new(true, null, approval.Id, approval.ActionDigest, approval.ExpiresUtc);
        }
        catch (DbUpdateException)
        {
            // A database unique constraint is the final concurrent issuance
            // fence. Never renew, replace or consume another issuance here.
            return new(false, "cloud_action_approval_not_created");
        }
    }

    private async Task<string> ExecuteCloudScopedAsync(
        ClaimsPrincipal founder, FounderAiActionScope scope, FounderAiToolCall call,
        string mode, CancellationToken cancellationToken, LegendConnectExternalProviderPolicy? providerPolicy)
    {
        scope = scope with { Roles = scope.Roles?.ToArray() ?? [] };
        if (!string.Equals(mode, "legend", StringComparison.Ordinal) ||
            !ValidCloudIdentifier(call.CallId) ||
            !string.Equals(call.IdempotencyKey, ComputeCloudToolIdempotencyKey(scope.RequestId, call.CallId), StringComparison.Ordinal))
            return CloudActionFailure("cloud_action_identity_invalid");
        if (!TryPrepareCloudAction(scope, call.Name, call.Arguments, out var arguments, out var digest))
            return CloudActionFailure("cloud_action_arguments_invalid");
        if (!ValidCloudScope(scope)) return CloudActionFailure("cloud_action_scope_denied");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(0, (scope.ExpiresUtc - DateTime.UtcNow).TotalMilliseconds)));
        cancellationToken = deadline.Token;
        if (!await ReauthorizeCloudScopeAsync(founder, scope, cancellationToken))
            return CloudActionFailure("cloud_action_scope_denied");

        // Reads still pass current authorization. Never let a supplied legacy
        // correlation authorize restricted research from a cloud callback.
        if (IsReadOnlyFounderTool(call.Name))
            return await ExecuteAuthorizedCoreAsync(founder,
                call with { Arguments = arguments, MutationAuthorization = null }, mode, cancellationToken, providerPolicy);

        Guid approvalId;
        string claimRevision;
        DateTime approvalExpiresUtc;
        using (var services = _authorizationScopes!.CreateScope())
        {
            var db = services.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            var approval = await db.FounderAiActionAuthorizations.SingleOrDefaultAsync(
                row => row.ActionDigest == digest, cancellationToken);
            if (approval is null || !MatchesCloudApproval(approval, scope, call.Name, arguments) ||
                approval.ExpiresUtc <= DateTime.UtcNow)
                return CloudActionFailure("cloud_action_approval_required");
            if (approval.State == "Completed" &&
                string.Equals(approval.IdempotencyKey, call.IdempotencyKey, StringComparison.Ordinal) &&
                approval.ResultJson is not null)
                return approval.ResultJson;
            if (approval.State != "Approved")
                return CloudActionFailure("cloud_action_approval_consumed");

            approval.State = "Executing";
            approval.IdempotencyKey = call.IdempotencyKey;
            approval.ExecutionStartedUtc = DateTime.UtcNow;
            approvalExpiresUtc = approval.ExpiresUtc;
            approval.Revision = claimRevision = Guid.NewGuid().ToString("N");
            approvalId = approval.Id;
            try
            {
                // Revision is an EF concurrency token. Exactly one SQL update
                // from Approved succeeds across instances and process restarts.
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return CloudActionFailure("cloud_action_approval_claim_failed");
            }
        }

        try
        {
            // Recheck the canonical pending operation immediately before the
            // existing service authority sees any side-effecting request.
            if (approvalExpiresUtc <= DateTime.UtcNow || !await ReauthorizeCloudScopeAsync(founder, scope, cancellationToken))
            {
                await RecordCloudActionOutcomeAsync(approvalId, claimRevision, call.IdempotencyKey!, null);
                return CloudActionFailure("cloud_action_scope_denied");
            }
            deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(0, (approvalExpiresUtc - DateTime.UtcNow).TotalMilliseconds)));
            var output = await ExecuteAuthorizedCoreAsync(founder, call with
            {
                Arguments = arguments,
                MutationAuthorization = new FounderAiMutationAuthorization(approvalId.ToString("N"), scope, digest, call.IdempotencyKey)
            }, mode, cancellationToken, providerPolicy);
            if (Encoding.UTF8.GetByteCount(output) > MaximumCloudActionBytes ||
                !await RecordCloudActionOutcomeAsync(approvalId, claimRevision, call.IdempotencyKey!, output))
            {
                await RecordCloudActionOutcomeAsync(approvalId, claimRevision, call.IdempotencyKey!, null);
                return CloudActionFailure("cloud_action_outcome_unknown");
            }
            return output;
        }
        catch (OperationCanceledException)
        {
            await RecordCloudActionOutcomeAsync(approvalId, claimRevision, call.IdempotencyKey!, null);
            throw;
        }
        catch (Exception)
        {
            await RecordCloudActionOutcomeAsync(approvalId, claimRevision, call.IdempotencyKey!, null);
            return CloudActionFailure("cloud_action_outcome_unknown");
        }
    }

    private async Task<bool> RecordCloudActionOutcomeAsync(Guid approvalId, string claimRevision,
        string idempotencyKey, string? result)
    {
        // Independent bounded recovery is needed after request cancellation.
        // If storage is unavailable the row stays Executing, which also denies
        // replay. No recovery path changes a consumed row back to Approved.
        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var services = _authorizationScopes!.CreateScope();
            var db = services.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            var row = await db.FounderAiActionAuthorizations.SingleOrDefaultAsync(
                value => value.Id == approvalId, recovery.Token);
            if (row is null || row.State != "Executing" || row.Revision != claimRevision || row.IdempotencyKey != idempotencyKey)
                return false;
            row.State = result is null ? "OutcomeUnknown" : "Completed";
            row.ResultJson = result;
            row.CompletedUtc = DateTime.UtcNow;
            row.Revision = Guid.NewGuid().ToString("N");
            await db.SaveChangesAsync(recovery.Token);
            return true;
        }
        catch (Exception) { return false; }
    }

    private async Task<bool> ReauthorizeCloudScopeAsync(
        ClaimsPrincipal founder, FounderAiActionScope scope, CancellationToken cancellationToken)
    {
        if (_authorizationScopes is null || founder.Identity?.IsAuthenticated != true || !ValidCloudScope(scope) ||
            !string.Equals(founder.FindFirst("sid")?.Value, scope.SessionId, StringComparison.Ordinal))
            return false;
        try
        {
            var userId = await _legend.ResolveFounderActorAsync(founder, cancellationToken);
            if (!string.Equals(userId, scope.UserId, StringComparison.Ordinal)) return false;
            using var services = _authorizationScopes.CreateScope();
            var messaging = services.ServiceProvider.GetRequiredService<IMessagingService>();
            var operation = await messaging.GetFounderAiOperationDelegationAsync(
                new MessagingActor(userId, MessagingParticipantTypes.Agent), Guid.Parse(scope.ConversationId),
                Guid.Parse(scope.RequestId), cancellationToken);
            var persisted = operation?.Delegation;
            return operation is not null && persisted is not null &&
                   operation.ConversationId == Guid.Parse(scope.ConversationId) && operation.OperationId == Guid.Parse(scope.RequestId) &&
                   operation.ExecutionDeadlineUtc >= scope.ExpiresUtc && operation.RequestFingerprint == scope.RequestFingerprint &&
                   persisted.AccountId == scope.AccountId && persisted.TenantId == scope.TenantId &&
                   persisted.UserId == scope.UserId && persisted.SessionId == scope.SessionId &&
                   persisted.AuthorizationVersion == scope.AuthorizationVersion && persisted.Environment == scope.Environment &&
                   persisted.ExpiresUtc == scope.ExpiresUtc && persisted.Roles.SequenceEqual(scope.Roles, StringComparer.Ordinal);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return false; }
    }

    private static bool ValidCloudScope(FounderAiActionScope scope)
    {
        var now = DateTime.UtcNow;
        return new[] { scope.AccountId, scope.TenantId, scope.UserId, scope.SessionId, scope.AuthorizationVersion, scope.Environment }
                   .All(ValidCloudIdentifier) &&
               Guid.TryParseExact(scope.ConversationId, "D", out var conversation) && conversation != Guid.Empty &&
               Guid.TryParseExact(scope.RequestId, "D", out var request) && request != Guid.Empty &&
               scope.Roles is { Count: > 0 and <= 32 } && scope.Roles.All(ValidCloudIdentifier) &&
               scope.Roles.Distinct(StringComparer.Ordinal).Count() == scope.Roles.Count &&
               Regex.IsMatch(scope.RequestFingerprint ?? string.Empty, "\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant) &&
               scope.ExpiresUtc.Kind == DateTimeKind.Utc && scope.ExpiresUtc > now && scope.ExpiresUtc <= now.AddSeconds(120);
    }

    private static bool ValidCloudIdentifier(string? value) => value is not null &&
        Regex.IsMatch(value, "\\A[A-Za-z0-9_.:@-]{1,128}\\z", RegexOptions.CultureInvariant);

    private static bool MatchesCloudApproval(FounderAiActionAuthorization approval, FounderAiActionScope scope,
        string name, string arguments) => approval.ScopeDigest == ComputeCloudScopeDigest(scope) &&
        approval.AccountId == scope.AccountId && approval.TenantId == scope.TenantId && approval.UserId == scope.UserId &&
        approval.SessionId == scope.SessionId && approval.ConversationId == Guid.Parse(scope.ConversationId) &&
        approval.RequestId == Guid.Parse(scope.RequestId) && approval.Environment == scope.Environment &&
        approval.AuthorizationVersion == scope.AuthorizationVersion && approval.ToolName == name &&
        approval.CanonicalArgumentsJson == arguments && approval.ExpiresUtc <= scope.ExpiresUtc;

    private static bool TryPrepareCloudAction(FounderAiActionScope scope, string name, string argumentsJson,
        out string canonicalArguments, out string digest)
    {
        canonicalArguments = digest = string.Empty;
        if (argumentsJson is null || Encoding.UTF8.GetByteCount(argumentsJson) > MaximumCloudActionBytes ||
            !TryResolveFounderFunctionParameters(name, out var schema)) return false;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !IsStrictSchemaInstance(schema, document.RootElement))
                return false;
            canonicalArguments = CanonicalCloudJson(document.RootElement);
            digest = ComputeCloudActionDigest(scope, name, canonicalArguments);
            return Encoding.UTF8.GetByteCount(canonicalArguments) <= MaximumCloudActionBytes;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or OverflowException)
        { return false; }
    }

    internal static string ComputeCloudToolIdempotencyKey(string requestId, string callId) =>
        CloudHash(requestId + ":tool:" + callId);

    internal static string ComputeCloudActionDigest(FounderAiActionScope scope, string name, string argumentsJson)
    {
        using var arguments = JsonDocument.Parse(argumentsJson, new JsonDocumentOptions { MaxDepth = 32 });
        var action = JsonSerializer.SerializeToElement(new
        {
            version = "legend-tool-action.v1", scope = CloudWireScope(scope), requestId = scope.RequestId,
            environment = scope.Environment, name, arguments = arguments.RootElement
        });
        return CloudHash(CanonicalCloudJson(action));
    }

    private static object CloudWireScope(FounderAiActionScope scope) => new
    {
        accountId = scope.AccountId, tenantId = scope.TenantId, userId = scope.UserId, sessionId = scope.SessionId,
        conversationId = scope.ConversationId, roles = scope.Roles, authorizationVersion = scope.AuthorizationVersion
    };

    private static string ComputeCloudScopeDigest(FounderAiActionScope scope) => CloudHash(CanonicalCloudJson(
        JsonSerializer.SerializeToElement(new
        {
            scope = CloudWireScope(scope), requestId = scope.RequestId, environment = scope.Environment,
            expiresAt = new DateTimeOffset(scope.ExpiresUtc).ToUnixTimeMilliseconds(), requestFingerprint = scope.RequestFingerprint
        })));

    private static string CloudHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string CloudActionFailure(string code) => MutationFailure(code,
        "The existing Founder authority did not authorize another execution of this action.");

    /// <summary>Matches the bounded Worker canonical JSON grammar and frozen interoperability vectors.</summary>
    internal static string CanonicalCloudJson(JsonElement value)
    {
        var output = new StringBuilder();
        var nodes = 0;
        AppendCloudJson(output, value, 0, ref nodes);
        return output.ToString();
    }

    private static void AppendCloudJson(StringBuilder output, JsonElement value, int depth, ref int nodes)
    {
        if (depth > 32 || ++nodes > 20000) throw new ArgumentException("Cloud JSON exceeds bounds.");
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
                if (properties.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                    throw new ArgumentException("Duplicate cloud JSON property.");
                output.Append('{');
                for (var index = 0; index < properties.Length; index++)
                {
                    if (index > 0) output.Append(',');
                    AppendCloudString(output, properties[index].Name);
                    output.Append(':');
                    AppendCloudJson(output, properties[index].Value, depth + 1, ref nodes);
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var first = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!first) output.Append(',');
                    first = false;
                    AppendCloudJson(output, item, depth + 1, ref nodes);
                }
                output.Append(']');
                break;
            case JsonValueKind.String: AppendCloudString(output, value.GetString()!); break;
            case JsonValueKind.Number: output.Append(CanonicalCloudNumber(value.GetDouble())); break;
            case JsonValueKind.True: output.Append("true"); break;
            case JsonValueKind.False: output.Append("false"); break;
            case JsonValueKind.Null: output.Append("null"); break;
            default: throw new ArgumentException("Unsupported cloud JSON value.");
        }
    }

    private static void AppendCloudString(StringBuilder output, string value)
    {
        output.Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (character < 0x20 || (char.IsSurrogate(character) &&
                        !(char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))))
                        output.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else
                    {
                        output.Append(character);
                        if (char.IsHighSurrogate(character)) output.Append(value[++index]);
                    }
                    break;
            }
        }
        output.Append('"');
    }

    private static string CanonicalCloudNumber(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("Nonfinite cloud JSON number.");
        if (value == 0) return "0";
        var negative = value < 0;
        var text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponentIndex = text.IndexOfAny(['E', 'e']);
        var exponent = exponentIndex >= 0 ? int.Parse(text[(exponentIndex + 1)..], CultureInfo.InvariantCulture) : 0;
        var mantissa = exponentIndex >= 0 ? text[..exponentIndex] : text;
        var decimalIndex = mantissa.IndexOf('.');
        var digits = mantissa.Replace(".", string.Empty, StringComparison.Ordinal);
        var point = (decimalIndex >= 0 ? decimalIndex : mantissa.Length) + exponent;
        while (digits.Length > 1 && digits[0] == '0') { digits = digits[1..]; point--; }
        digits = digits.TrimEnd('0');
        var scientificExponent = point - 1;
        var sign = negative ? "-" : string.Empty;
        if (scientificExponent is >= -6 and < 21)
        {
            if (point <= 0) return sign + "0." + new string('0', -point) + digits;
            if (point >= digits.Length) return sign + digits + new string('0', point - digits.Length);
            return sign + digits[..point] + "." + digits[point..];
        }
        return sign + digits[0] + (digits.Length > 1 ? "." + digits[1..] : string.Empty) + "e" +
               (scientificExponent >= 0 ? "+" : string.Empty) + scientificExponent.ToString(CultureInfo.InvariantCulture);
    }
}

// Only constructed from authenticated server context and the canonical pending
// messaging delegation. Roles are a bound snapshot, never a ClaimsPrincipal.
internal sealed record FounderAiActionScope(
    string AccountId, string TenantId, string UserId, string SessionId, string ConversationId, string RequestId,
    IReadOnlyList<string> Roles, string AuthorizationVersion, string Environment, DateTime ExpiresUtc, string RequestFingerprint);

internal sealed record FounderAiActionApprovalReceipt(
    bool Succeeded, string? Error = null, Guid? ApprovalId = null, string? ActionDigest = null, DateTime? ExpiresUtc = null);
