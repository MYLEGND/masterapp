using Domain.Engineering;

namespace AgentPortal.Services.Engineering;

internal interface ILegendEngineeringAgentAdapter
{
    Task<object> GetStatusAsync(CancellationToken cancellationToken);
    Task<object> StartAsync(Guid engineeringContextId, CancellationToken cancellationToken);
}

internal sealed record ChatGptPlanCredentialState(
    bool Ready,
    string Code,
    string? ClientId,
    string? AccessToken,
    IReadOnlyList<string> GrantedScopes,
    DateTime? ExpiresUtc,
    bool PrivateClientApproved);

internal interface ILegendChatGptPlanCredentialAuthority
{
    Task<ChatGptPlanCredentialState> GetAsync(CancellationToken cancellationToken);
}

internal sealed class LegendChatGptPlanCredentialAuthority(IConfiguration configuration)
    : ILegendChatGptPlanCredentialAuthority
{
    public Task<ChatGptPlanCredentialState> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (configuration.GetValue<bool?>("LegendEngineering:ChatGptPlan:PrivateClientApproved") != true)
            return Task.FromResult(Blocked("chatgpt_plan_private_client_eligibility_unverified", false));

        var clientId = configuration["LegendEngineering:ChatGptPlan:ClientId"]?.Trim();
        var scopes = (configuration["LegendEngineering:ChatGptPlan:GrantedScopes"] ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (string.IsNullOrWhiteSpace(clientId))
            return Task.FromResult(new(false, "chatgpt_plan_client_registration_missing", null, null, scopes, null, true));
        if (!scopes.Contains("resource.invoke", StringComparer.Ordinal) ||
            !scopes.Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal))
            return Task.FromResult(new(false, "chatgpt_plan_usage_scope_missing", clientId, null, scopes, null, true));

        var token = configuration["LegendEngineering:ChatGptPlan:AccessToken"]?.Trim();
        if (string.IsNullOrWhiteSpace(token))
            return Task.FromResult(new(false, "chatgpt_plan_sign_in_required", clientId, null, scopes, null, true));
        if (!DateTime.TryParse(configuration["LegendEngineering:ChatGptPlan:AccessTokenExpiresUtc"], null,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var expiresUtc))
            return Task.FromResult(new(false, "chatgpt_plan_token_expiry_missing", clientId, null, scopes, null, true));
        if (expiresUtc <= DateTime.UtcNow.AddMinutes(1))
            return Task.FromResult(new(false, "chatgpt_plan_access_token_expired_reauthorization_required", clientId, null, scopes, expiresUtc, true));

        return Task.FromResult(new ChatGptPlanCredentialState(true, "chatgpt_plan_ready", clientId, token, scopes, expiresUtc, true));
    }

    private static ChatGptPlanCredentialState Blocked(string code, bool approved)
        => new(false, code, null, null, Array.Empty<string>(), null, approved);
}