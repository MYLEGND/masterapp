using System.Text.Json;
using AgentPortal.Models;

namespace AgentPortal.Services.Engineering;

public interface IFounderEngineeringCommandCenterService
{
    Task<FounderEngineeringCommandCenterViewModel> GetAsync(CancellationToken cancellationToken);
    Task<FounderEngineeringContractMutationResult> SaveAsync(
        FounderEngineeringContractInput input,
        CancellationToken cancellationToken);
    Task<FounderEngineeringContractMutationResult> RestoreAsync(
        FounderEngineeringRestoreInput input,
        CancellationToken cancellationToken);
    Task<ChatGptPlanClientRegistrationState> SaveClientRegistrationAsync(
        FounderEngineeringClientRegistrationInput input,
        CancellationToken cancellationToken);
    Task<ChatGptPlanAuthorizationStart> BeginChatGptAuthorizationAsync(
        CancellationToken cancellationToken);
    Task<ChatGptPlanAuthorizationResult> CompleteChatGptAuthorizationAsync(
        string code,
        string state,
        string? responseIssuer,
        string? callbackClientId,
        CancellationToken cancellationToken);
    Task AbortChatGptAuthorizationAsync(
        string state,
        CancellationToken cancellationToken);
    Task DisconnectChatGptAsync(CancellationToken cancellationToken);
    Task<ChatGptPlanAuthorizationResult> RetryChatGptRuntimeAsync(
        CancellationToken cancellationToken);
}

internal sealed class FounderEngineeringCommandCenterService(
    ILegendEngineeringContractAuthority contractAuthority,
    ILegendEngineeringOrchestrator orchestrator,
    ILegendChatGptPlanCredentialAuthority credentials,
    ILegendEngineeringAgentAdapter adapter)
    : IFounderEngineeringCommandCenterService
{
    public async Task<FounderEngineeringCommandCenterViewModel> GetAsync(
        CancellationToken cancellationToken)
    {
        var contract = await contractAuthority.GetCurrentAsync(cancellationToken);
        var history = await contractAuthority.GetHistoryAsync(10, cancellationToken);
        var registration = await credentials.GetClientRegistrationAsync(cancellationToken);
        var status = JsonSerializer.SerializeToElement(
            await orchestrator.GetStatusAsync(cancellationToken),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var adapterStatus = JsonSerializer.SerializeToElement(
            await adapter.GetStatusAsync(cancellationToken),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var catalog = await adapter.GetModelCatalogAsync(cancellationToken);

        return new FounderEngineeringCommandCenterViewModel
        {
            Revision = contract.Revision,
            Version = contract.Version,
            ModelExecutionEnabled = contract.ModelExecutionEnabled,
            AutonomousEngineeringEnabled = contract.AutonomousEngineeringEnabled,
            HeadGptModel = contract.HeadGptModel,
            CodexModel = contract.CodexModel,
            ReviewerModel = contract.ReviewerModel,
            ResolvedHeadGptModel = ReadString(adapterStatus, "resolvedHeadGptModel"),
            ResolvedCodexModel = ReadString(adapterStatus, "resolvedCodexModel"),
            ResolvedReviewerModel = ReadString(adapterStatus, "resolvedReviewerModel"),
            SharedDirective = contract.SharedDirective,
            HeadGptDirective = contract.HeadGptDirective,
            CodexDirective = contract.CodexDirective,
            ReviewerDirective = contract.ReviewerDirective,
            UpdatedUtc = contract.UpdatedUtc,
            UpdatedBy = contract.UpdatedBy,

            ChatGptPlanReady = ReadBool(adapterStatus, "credentialReady"),
            ChatGptPlanCode = ReadString(adapterStatus, "credentialCode"),
            ChatGptPlanClientConfigured = registration.Configured,
            ChatGptPlanClientId = registration.ClientId ?? string.Empty,
            ChatGptPlanClientAuthenticationMethod = registration.AuthenticationMethod,
            ChatGptPlanClientSecretConfigured = registration.ClientSecretConfigured,
            ChatGptPlanApprovedClient = ReadBool(adapterStatus, "privateClientApproved"),
            ChatGptPlanScopeGranted = ReadBool(adapterStatus, "planUsageScopeGranted"),
            ChatGptPlanExpiresUtc = ReadDateTime(adapterStatus, "expiresUtc"),
            ModelRuntimeReady = ReadBool(adapterStatus, "runtimeReady"),
            ModelRuntimeCode = ReadString(adapterStatus, "eligibility"),
            ModelRuntimeLabel = RuntimeLabel(adapterStatus, contract.ModelExecutionEnabled),
            ProviderBlockerClass = ReadString(adapterStatus, "providerBlockerClass"),
            ProviderBlockerCode = ReadString(adapterStatus, "providerBlockerCode"),
            ProviderRequestId = ReadString(adapterStatus, "providerRequestId"),
            ProviderRetryNotBeforeUtc = ReadDateTime(adapterStatus, "providerRetryNotBeforeUtc"),
            ProviderCircuitEpisodeId = ReadString(adapterStatus, "providerCircuitEpisodeId"),
            ReadinessState = ReadString(adapterStatus, "readinessState"),
            ReadinessCheckedUtc = ReadDateTime(adapterStatus, "readinessCheckedUtc"),
            ShowManageUsage = string.Equals(
                ReadString(adapterStatus, "providerBlockerClass"),
                "USAGE_LIMIT",
                StringComparison.Ordinal),
            ShowReconnectChatGpt =
                ReadString(adapterStatus, "providerBlockerClass") is "AUTHENTICATION" or "PLAN_ELIGIBILITY" ||
                ReadString(adapterStatus, "eligibility") == "chatgpt_plan_reauthorization_required",
            ShowRetryRuntime =
                !ReadBool(adapterStatus, "runtimeReady") &&
                ReadString(adapterStatus, "providerBlockerClass") != "USAGE_LIMIT",
            AutonomousRuntimeActive =
                ReadBool(adapterStatus, "runtimeReady") &&
                contract.ModelExecutionEnabled &&
                contract.AutonomousEngineeringEnabled,
            AvailableModels = catalog.Models
                .Select(model => new FounderEngineeringModelOption(model.Slug, model.DisplayName))
                .ToArray(),

            OpenWorkItems = ReadInt(status, "openWorkItems"),
            LeasedWorkItems = ReadInt(status, "leasedWorkItems"),
            SecurityReviewItems = ReadInt(status, "securityReviewItems"),
            History = history.Select(row => new FounderEngineeringContractHistoryItem(
                row.Revision,
                row.Version,
                row.ModelExecutionEnabled,
                row.AutonomousEngineeringEnabled,
                row.UpdatedUtc,
                row.UpdatedBy)).ToArray()
        };
    }

    public async Task<FounderEngineeringContractMutationResult> SaveAsync(
        FounderEngineeringContractInput input,
        CancellationToken cancellationToken)
    {
        var before = await contractAuthority.GetCurrentAsync(cancellationToken);
        var value = await contractAuthority.UpdateAllAsync(
            input.ExpectedRevision,
            input.ModelExecutionEnabled,
            input.AutonomousEngineeringEnabled,
            input.HeadGptModel,
            input.CodexModel,
            input.ReviewerModel,
            input.SharedDirective,
            input.HeadGptDirective,
            input.CodexDirective,
            input.ReviewerDirective,
            "Founder",
            cancellationToken);

        if (ModelBindingsChanged(before, value))
        {
            await credentials.MarkReadinessUnverifiedAsync(
                "chatgpt_plan_readiness_canary_required",
                cancellationToken);
            var credential = await credentials.GetAsync(cancellationToken);
            if (credential.Ready)
                await adapter.ReconcileRuntimeAsync(force: true, cancellationToken);
        }
        return new(value.Revision, value.Version);
    }

    public async Task<FounderEngineeringContractMutationResult> RestoreAsync(
        FounderEngineeringRestoreInput input,
        CancellationToken cancellationToken)
    {
        var before = await contractAuthority.GetCurrentAsync(cancellationToken);
        var value = await contractAuthority.RestoreAsync(
            input.ExpectedRevision,
            input.Revision,
            "Founder",
            cancellationToken);
        if (ModelBindingsChanged(before, value))
        {
            await credentials.MarkReadinessUnverifiedAsync(
                "chatgpt_plan_readiness_canary_required",
                cancellationToken);
            var credential = await credentials.GetAsync(cancellationToken);
            if (credential.Ready)
                await adapter.ReconcileRuntimeAsync(force: true, cancellationToken);
        }
        return new(value.Revision, value.Version);
    }

    public Task<ChatGptPlanClientRegistrationState> SaveClientRegistrationAsync(
        FounderEngineeringClientRegistrationInput input,
        CancellationToken cancellationToken) =>
        credentials.SaveClientRegistrationAsync(
            input.ClientId,
            input.AuthenticationMethod,
            input.ClientSecret,
            cancellationToken);

    public Task<ChatGptPlanAuthorizationStart> BeginChatGptAuthorizationAsync(
        CancellationToken cancellationToken) =>
        credentials.BeginAuthorizationAsync(cancellationToken);

    public async Task<ChatGptPlanAuthorizationResult> CompleteChatGptAuthorizationAsync(
        string code,
        string state,
        string? responseIssuer,
        string? callbackClientId,
        CancellationToken cancellationToken)
    {
        var authorization = await credentials.CompleteAuthorizationAsync(
            code,
            state,
            responseIssuer,
            callbackClientId,
            cancellationToken);
        if (!authorization.Ready)
            return authorization;

        var readiness = JsonSerializer.SerializeToElement(
            await adapter.ReconcileRuntimeAsync(force: true, cancellationToken),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var ready = ReadBool(readiness, "runtimeReady") || ReadBool(readiness, "ok");
        return new(
            ready,
            ready
                ? "chatgpt_plan_inference_ready"
                : ReadString(readiness, "error") is { Length: > 0 } error
                    ? error
                    : "chatgpt_plan_readiness_canary_failed");
    }

    public Task AbortChatGptAuthorizationAsync(
        string state,
        CancellationToken cancellationToken) =>
        credentials.AbortAuthorizationAsync(state, cancellationToken);

    public Task DisconnectChatGptAsync(CancellationToken cancellationToken) =>
        credentials.DisconnectAsync(cancellationToken);

    public async Task<ChatGptPlanAuthorizationResult> RetryChatGptRuntimeAsync(
        CancellationToken cancellationToken)
    {
        var result = JsonSerializer.SerializeToElement(
            await adapter.ReconcileRuntimeAsync(force: true, cancellationToken),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var status = JsonSerializer.SerializeToElement(
            await adapter.GetStatusAsync(cancellationToken),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var ready = ReadBool(status, "runtimeReady");
        return new(
            ready,
            ready
                ? "chatgpt_plan_inference_ready"
                : ReadString(result, "error") is { Length: > 0 } error
                    ? error
                    : ReadString(status, "eligibility"));
    }

    private static bool ModelBindingsChanged(
        LegendEngineeringOperationalContract before,
        LegendEngineeringOperationalContract after) =>
        !string.Equals(before.HeadGptModel, after.HeadGptModel, StringComparison.Ordinal) ||
        !string.Equals(before.CodexModel, after.CodexModel, StringComparison.Ordinal) ||
        !string.Equals(before.ReviewerModel, after.ReviewerModel, StringComparison.Ordinal);

    private static string RuntimeLabel(
        JsonElement status,
        bool modelExecutionEnabled)
    {
        if (!modelExecutionEnabled) return "Paused by Founder";
        var blockerClass = ReadString(status, "providerBlockerClass");
        if (blockerClass == "USAGE_LIMIT") return "Waiting for ChatGPT usage";
        if (blockerClass == "TEMPORARY_PROVIDER") return "ChatGPT temporarily unavailable";
        if (blockerClass is "AUTHENTICATION" or "PLAN_ELIGIBILITY")
            return "Reconnect ChatGPT";
        if (blockerClass == "MODEL_BINDING" ||
            ReadString(status, "eligibility") == "chatgpt_plan_model_binding_unavailable")
            return "Selected model unavailable";
        if (blockerClass == "GRANT_CONFIGURATION")
            return "Client/grant configuration required";
        return ReadBool(status, "runtimeReady") ? "Inference ready" : "Readiness check required";
    }

    private static int ReadInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var result)
            ? result
            : 0;

    private static bool ReadBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static DateTime? ReadDateTime(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.TryGetDateTime(out var result)
            ? result
            : null;
}
