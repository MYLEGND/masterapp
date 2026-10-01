using System.Text.Json;
using AgentPortal.Models;
using Domain.Engineering;

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
    Task DisconnectChatGptAsync(CancellationToken cancellationToken);
}

internal sealed class FounderEngineeringCommandCenterService(
    ILegendEngineeringContractAuthority contractAuthority,
    ILegendEngineeringOrchestrator orchestrator,
    ILegendChatGptPlanCredentialAuthority credentials,
    ILegendEngineeringAgentAdapter adapter,
    IConfiguration configuration)
    : IFounderEngineeringCommandCenterService
{
    public async Task<FounderEngineeringCommandCenterViewModel> GetAsync(CancellationToken cancellationToken)
    {
        var contract = await contractAuthority.GetCurrentAsync(cancellationToken);
        var history = await contractAuthority.GetHistoryAsync(10, cancellationToken);
        var status = JsonSerializer.SerializeToElement(
            await orchestrator.GetStatusAsync(cancellationToken),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var adapterStatus = JsonSerializer.SerializeToElement(
            await adapter.GetStatusAsync(cancellationToken),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        return new FounderEngineeringCommandCenterViewModel
        {
            Revision = contract.Revision,
            Version = contract.Version,
            ModelExecutionEnabled = contract.ModelExecutionEnabled,
            SharedDirective = contract.SharedDirective,
            HeadGptDirective = contract.HeadGptDirective,
            CodexDirective = contract.CodexDirective,
            ReviewerDirective = contract.ReviewerDirective,
            UpdatedUtc = contract.UpdatedUtc,
            UpdatedBy = contract.UpdatedBy,

            ChatGptPlanReady = ReadBool(adapterStatus, "credentialReady"),
            ChatGptPlanCode = ReadString(adapterStatus, "credentialCode"),
            ChatGptPlanApprovedClient = ReadBool(adapterStatus, "privateClientApproved"),
            ChatGptPlanScopeGranted = ReadBool(adapterStatus, "planUsageScopeGranted"),
            ChatGptPlanExpiresUtc = ReadDateTime(adapterStatus, "expiresUtc"),
            CodexAppServerConfigured = ReadBool(adapterStatus, "codexAppServerConfigured"),
            AdapterEnabled = ReadBool(adapterStatus, "enabled"),
            AutonomousConfigEnabled = configuration.GetValue<bool?>("LegendEngineering:Autonomous:Enabled") == true,
            HeadGptModel = configuration[$"LegendEngineering:ChatGptPlan:Models:{EngineeringModelTier.DeepReasoning}"] ?? "Not configured",
            CodexModel = configuration[$"LegendEngineering:ChatGptPlan:Models:{EngineeringModelTier.CodeImplementation}"] ?? "Not configured",
            ReviewerModel = configuration[$"LegendEngineering:ChatGptPlan:Models:{EngineeringModelTier.IndependentReview}"] ?? "Not configured",

            OpenWorkItems = ReadInt(status, "openWorkItems"),
            LeasedWorkItems = ReadInt(status, "leasedWorkItems"),
            SecurityReviewItems = ReadInt(status, "securityReviewItems"),
            History = history.Select(row => new FounderEngineeringContractHistoryItem(
                row.Revision,
                row.Version,
                row.ModelExecutionEnabled,
                row.UpdatedUtc,
                row.UpdatedBy)).ToArray()
        };
    }

    public async Task<FounderEngineeringContractMutationResult> SaveAsync(
        FounderEngineeringContractInput input,
        CancellationToken cancellationToken)
    {
        var value = await contractAuthority.UpdateAsync(
            input.ExpectedRevision,
            input.ModelExecutionEnabled,
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
