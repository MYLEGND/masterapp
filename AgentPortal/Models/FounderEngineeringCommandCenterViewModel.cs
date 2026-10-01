namespace AgentPortal.Models;

public sealed record FounderEngineeringModelOption(string Slug, string DisplayName);

public sealed record FounderEngineeringContractHistoryItem(
    string Revision,
    long Version,
    bool ModelExecutionEnabled,
    bool AutonomousEngineeringEnabled,
    DateTime UpdatedUtc,
    string UpdatedBy);

public sealed class FounderEngineeringCommandCenterViewModel
{
    public string Revision { get; init; } = string.Empty;
    public long Version { get; init; }
    public bool ModelExecutionEnabled { get; init; }
    public bool AutonomousEngineeringEnabled { get; init; }
    public string HeadGptModel { get; init; } = string.Empty;
    public string CodexModel { get; init; } = string.Empty;
    public string ReviewerModel { get; init; } = string.Empty;
    public string ResolvedHeadGptModel { get; init; } = string.Empty;
    public string ResolvedCodexModel { get; init; } = string.Empty;
    public string ResolvedReviewerModel { get; init; } = string.Empty;
    public string SharedDirective { get; init; } = string.Empty;
    public string HeadGptDirective { get; init; } = string.Empty;
    public string CodexDirective { get; init; } = string.Empty;
    public string ReviewerDirective { get; init; } = string.Empty;
    public DateTime UpdatedUtc { get; init; }
    public string UpdatedBy { get; init; } = string.Empty;

    public bool ChatGptPlanReady { get; init; }
    public string ChatGptPlanCode { get; init; } = string.Empty;
    public bool ChatGptPlanClientConfigured { get; init; }
    public string ChatGptPlanClientId { get; init; } = string.Empty;
    public string ChatGptPlanClientAuthenticationMethod { get; init; } = "none";
    public bool ChatGptPlanClientSecretConfigured { get; init; }
    public bool ChatGptPlanApprovedClient { get; init; }
    public bool ChatGptPlanScopeGranted { get; init; }
    public DateTime? ChatGptPlanExpiresUtc { get; init; }
    public bool ModelRuntimeReady { get; init; }
    public string ModelRuntimeCode { get; init; } = string.Empty;
    public string ModelRuntimeLabel { get; init; } = string.Empty;
    public string ProviderBlockerClass { get; init; } = string.Empty;
    public string ProviderBlockerCode { get; init; } = string.Empty;
    public string ProviderRequestId { get; init; } = string.Empty;
    public DateTime? ProviderRetryNotBeforeUtc { get; init; }
    public string ProviderCircuitEpisodeId { get; init; } = string.Empty;
    public string ReadinessState { get; init; } = string.Empty;
    public DateTime? ReadinessCheckedUtc { get; init; }
    public bool ShowManageUsage { get; init; }
    public bool ShowReconnectChatGpt { get; init; }
    public bool ShowRetryRuntime { get; init; }
    public bool AutonomousRuntimeActive { get; init; }
    public IReadOnlyList<FounderEngineeringModelOption> AvailableModels { get; init; } =
        Array.Empty<FounderEngineeringModelOption>();

    public int OpenWorkItems { get; init; }
    public int LeasedWorkItems { get; init; }
    public int SecurityReviewItems { get; init; }
    public IReadOnlyList<FounderEngineeringContractHistoryItem> History { get; init; } =
        Array.Empty<FounderEngineeringContractHistoryItem>();
}

public sealed record FounderEngineeringContractMutationResult(
    string Revision,
    long Version);

public sealed class FounderEngineeringContractInput
{
    public string ExpectedRevision { get; set; } = string.Empty;
    public bool ModelExecutionEnabled { get; set; }
    public bool AutonomousEngineeringEnabled { get; set; }
    public string? HeadGptModel { get; set; }
    public string? CodexModel { get; set; }
    public string? ReviewerModel { get; set; }
    public string? SharedDirective { get; set; }
    public string? HeadGptDirective { get; set; }
    public string? CodexDirective { get; set; }
    public string? ReviewerDirective { get; set; }
}

public sealed class FounderEngineeringRestoreInput
{
    public string ExpectedRevision { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
}

public sealed class FounderEngineeringClientRegistrationInput
{
    public string ClientId { get; set; } = string.Empty;
    public string AuthenticationMethod { get; set; } = "none";
    public string? ClientSecret { get; set; }
}
