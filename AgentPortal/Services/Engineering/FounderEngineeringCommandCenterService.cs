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
        CancellationToken cancellationToken);
    Task DisconnectChatGptAsync(CancellationToken cancellationToken);
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
        return new(value.Revision, value.Version);
    }

    public async Task<FounderEngineeringContractMutationResult> RestoreAsync(
        FounderEngineeringRestoreInput input,
        CancellationToken cancellationToken)
    {
        var value = await contractAuthority.RestoreAsync(
            input.ExpectedRevision,
            input.Revision,
            "Founder",
            cancellationToken);
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

    public Task<ChatGptPlanAuthorizationResult> CompleteChatGptAuthorizationAsync(
        string code,
        string state,
        string? responseIssuer,
        CancellationToken cancellationToken) =>
        credentials.CompleteAuthorizationAsync(
            code,
            state,
            responseIssuer,
            cancellationToken);

    public Task DisconnectChatGptAsync(CancellationToken cancellationToken) =>
        credentials.DisconnectAsync(cancellationToken);

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
