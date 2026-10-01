namespace AgentPortal.Models;

public sealed record FounderEngineeringContractHistoryItem(
    string Revision,
    long Version,
    bool ModelExecutionEnabled,
    DateTime UpdatedUtc,
    string UpdatedBy);

public sealed class FounderEngineeringCommandCenterViewModel
{
    public string Revision { get; init; } = string.Empty;
    public long Version { get; init; }
    public bool ModelExecutionEnabled { get; init; }
    public string SharedDirective { get; init; } = string.Empty;
    public string HeadGptDirective { get; init; } = string.Empty;
    public string CodexDirective { get; init; } = string.Empty;
    public string ReviewerDirective { get; init; } = string.Empty;
    public DateTime UpdatedUtc { get; init; }
    public string UpdatedBy { get; init; } = string.Empty;

    public bool ChatGptPlanReady { get; init; }
    public string ChatGptPlanCode { get; init; } = string.Empty;
    public bool ChatGptPlanApprovedClient { get; init; }
    public bool ChatGptPlanScopeGranted { get; init; }
    public DateTime? ChatGptPlanExpiresUtc { get; init; }
    public bool CodexAppServerConfigured { get; init; }
    public bool AdapterEnabled { get; init; }
    public bool AutonomousConfigEnabled { get; init; }
    public string HeadGptModel { get; init; } = string.Empty;
    public string CodexModel { get; init; } = string.Empty;
    public string ReviewerModel { get; init; } = string.Empty;

    public int OpenWorkItems { get; init; }
    public int LeasedWorkItems { get; init; }
    public int SecurityReviewItems { get; init; }
    public IReadOnlyList<FounderEngineeringContractHistoryItem> History { get; init; } =
        Array.Empty<FounderEngineeringContractHistoryItem>();
}

public sealed class FounderEngineeringContractInput
{
    public string ExpectedRevision { get; set; } = string.Empty;
    public bool ModelExecutionEnabled { get; set; }
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
