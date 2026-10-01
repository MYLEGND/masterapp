namespace Domain.Engineering;

public static class LegendEngineeringContract
{
    public const string ContractRevision = "legend-engineering-context.v1";
    public const string PolicyRevision = "legend-engineering-policy.v1";
}

public static class EngineeringFailureClass
{
    public const string CodeDefect = "CODE_DEFECT";
    public const string ConfigurationDefect = "CONFIGURATION_DEFECT";
    public const string DeploymentDrift = "DEPLOYMENT_DRIFT";
    public const string AuthorizationDenial = "AUTHORIZATION_DENIAL";
    public const string NetworkProviderFailure = "NETWORK_PROVIDER_FAILURE";
    public const string ExpectedPolicyBehavior = "EXPECTED_POLICY_BEHAVIOR";
    public const string Unknown = "UNKNOWN";
}

public static class EngineeringRiskClass
{
    public const string TierA = "TIER_A";
    public const string TierB = "TIER_B";
    public const string TierC = "TIER_C";
}

public static class EngineeringRole
{
    public const string Sentinel = "SENTINEL";
    public const string TriageWorker = "TRIAGE_WORKER";
    public const string HeadGpt = "HEAD_GPT";
    public const string CodexImplementer = "CODEX_IMPLEMENTER";
    public const string IndependentReviewer = "INDEPENDENT_REVIEWER";
    public const string LiveVerifier = "LIVE_VERIFIER";
}

public static class EngineeringModelTier
{
    public const string FastTriage = "FAST_TRIAGE";
    public const string StandardReasoning = "STANDARD_REASONING";
    public const string DeepReasoning = "DEEP_REASONING";
    public const string CodeImplementation = "CODE_IMPLEMENTATION";
    public const string IndependentReview = "INDEPENDENT_REVIEW";
}

public sealed record EngineeringPolicyDecision(
    string FailureClass,
    int Severity,
    int RevenueImpact,
    int UserImpact,
    int Frequency,
    int Confidence,
    int DependencyBreadth,
    int PriorityScore,
    string PriorityClass,
    string RiskClass,
    int ComplexityScore,
    string AssignedRole,
    string ModelTier,
    string CanonicalAuthorityKey,
    IReadOnlyList<string> AffectedProjects,
    IReadOnlyList<string> AffectedApplications,
    IReadOnlyList<string> ImpactSet,
    bool CodeRepairEligible,
    bool FounderReleaseApprovalRequired,
    string ReleaseCohort);

public sealed record EngineeringWorkItemSnapshot(
    Guid WorkItemId,
    IReadOnlyList<Guid> IncidentIds,
    string WorkKey,
    string CanonicalAuthorityKey,
    IReadOnlyList<string> AffectedProjects,
    IReadOnlyList<string> AffectedApplications,
    IReadOnlyList<string> ImpactSet,
    string LiveSha,
    string EvidenceRevision,
    string FailureClass,
    int Severity,
    int RevenueImpact,
    int UserImpact,
    int Frequency,
    int Confidence,
    int RiskClassScore,
    string RiskClass,
    int ComplexityScore,
    int PriorityScore,
    string PriorityClass,
    string State,
    string AssignedRole,
    string ModelTier,
    string? LeaseOwner,
    string? LeaseIdentity,
    DateTime? LeaseExpiresUtc,
    int AttemptCount,
    string? RepairBatchId,
    int? PullRequestNumber,
    string? CandidateSha,
    string ValidationState,
    string ReleaseCohort,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    IReadOnlyList<string>? CandidateChangedPaths = null,
    string? AgentSessionId = null,
    Guid? AgentContextId = null,
    string? AgentSessionRole = null,
    DateTime? AgentSessionUpdatedUtc = null,
    int? PublicationPullRequestNumber = null,
    string? PublicationHeadSha = null,
    string? PublicationBatchRevision = null,
    string? MergedSha = null,
    string? DeployedTreeSha = null,
    long? DeploymentRunId = null,
    DateTime? DeploymentVerifiedUtc = null,
    DateTime? FounderReleaseApprovedUtc = null,
    IReadOnlyList<string>? ValidationFailureCodes = null,
    string? StateRevision = null);

public sealed record EngineeringLeaseReceipt(
    bool Acquired,
    string Code,
    Guid WorkItemId,
    string? LeaseOwner,
    string? LeaseIdentity,
    DateTime? LeaseExpiresUtc,
    Guid? ConflictingWorkItemId);

public sealed record EngineeringBudgetEnvelope(
    bool AiWorkPermitted,
    string Mode,
    long? DailyTokenCeiling,
    long? MonthlyTokenCeiling,
    long ObservedDailyTokens,
    long ObservedMonthlyTokens,
    int MaxCodexAttempts,
    int MaxDeepReasoningEscalations,
    bool UsageEvidenceComplete);

public sealed record EngineeringContextSnapshot(
    Guid EngineeringContextId,
    string ContractRevision,
    string PolicyRevision,
    Guid WorkItemId,
    string Role,
    string FailureClass,
    string RiskClass,
    int ComplexityScore,
    string LiveSha,
    string EvidenceRevision,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> AllowedSourceClasses,
    IReadOnlyList<string> ProtectedAreas,
    EngineeringBudgetEnvelope Budget,
    int AttemptLimit,
    IReadOnlyList<string> StopConditions,
    string LeaseIdentity,
    DateTime CreatedUtc,
    DateTime ExpiresUtc,
    string? OperationalContractRevision = null);

public sealed record EngineeringTaskPacket(
    string PacketType,
    Guid WorkItemId,
    string Application,
    string LiveSha,
    string Route,
    string CanonicalAuthority,
    IReadOnlyList<string> ComponentIds,
    IReadOnlyList<string> ActionKeys,
    IReadOnlyList<string> CompositionIds,
    string FailureClass,
    int Severity,
    string RiskClass,
    int Complexity,
    string ExpectedBehavior,
    string ObservedBehavior,
    IReadOnlyList<string> SafeIssueCodes,
    IReadOnlyList<string> PermittedSourcePaths,
    IReadOnlyList<string> ProtectedSourcePaths,
    IReadOnlyList<string> AffectedProjects,
    IReadOnlyList<string> AffectedApplications,
    IReadOnlyList<string> RequiredFocusedTests,
    string DefinitionOfFixed,
    IReadOnlyList<string> ValidationRequirements,
    string ReleasePolicy,
    IReadOnlyList<string> LiveVerificationRequirements,
    string? RepairBaseSha = null,
    IReadOnlyList<string>? ValidationFailureCodes = null);

public sealed record EngineeringUsageObservation(
    Guid UsageId,
    Guid WorkItemId,
    string ModelTier,
    string Role,
    string Provider,
    string? SessionId,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    long? CostMicrousd,
    bool UsageObserved,
    DateTime CreatedUtc);
