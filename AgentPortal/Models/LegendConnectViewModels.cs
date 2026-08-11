using Domain.Messaging;

namespace AgentPortal.Models;

public sealed class FounderLegendConnectDashboardVm
{
    public LegendConnectDashboardSnapshot Dashboard { get; init; } = new(
        Array.Empty<LegendConnectLanguageHealthSnapshot>(),
        Array.Empty<LegendConnectPairHealthSnapshot>(),
        0, 0, 0, 0, 0, 0, null, 0, 0, 0, null,
        Array.Empty<LegendConnectOperationalEventSnapshot>());
    public LegendConnectLanguageHealthSnapshot? SelectedLanguage { get; init; }
    public LegendConnectLanguageKnowledgeSnapshot? SelectedLanguageKnowledge { get; init; }
    public LegendConnectPairHealthSnapshot? SelectedPair { get; init; }
    public IReadOnlyList<TranslationFounderAccountUsageSnapshot> AccountUsage { get; init; } =
        Array.Empty<TranslationFounderAccountUsageSnapshot>();
    public IReadOnlyList<TranslationEntitlementPreset> EntitlementPresets { get; init; } =
        Array.Empty<TranslationEntitlementPreset>();
    public TranslationFounderScaleSnapshot AccountScale { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    public LegendConnectRuntimePolicySnapshot RuntimePolicy { get; init; } = new(
        false, 0, 0, 0, false, true, "Shadow", 0.98m, "Automatic", null, null, null, null, null, DateTime.MinValue);
    public LegendConnectProductionReadinessSnapshot ProductionReadiness { get; init; } = new(
        "BLOCKED", false, "Runtime policy authority is unavailable.", Array.Empty<LegendConnectReadinessCheck>(), 0, 0, 0, 0, 0);
    public LegendConnectPriorityProgressSnapshot PriorityProgress { get; init; } = new(
        "AUTOMATIC — DEMAND DRIVEN", 0, 0, 0, 0m, 0, null, null);
    public IReadOnlyList<LegendConnectFounderOperationalAuditSnapshot> RuntimeAudit { get; init; } =
        Array.Empty<LegendConnectFounderOperationalAuditSnapshot>();
}

public class FounderLegendConnectKnowledgeInput
{
    public string SourceLanguageCode { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public string? TargetLanguageCode { get; set; }
    public string? TargetText { get; set; }
    public string? ContextCategory { get; set; }
    public string? UsageRegister { get; set; }
    public string? RegionalVariant { get; set; }
}

public sealed class FounderLegendConnectCorrectionInput : FounderLegendConnectKnowledgeInput
{
    public Guid SupersededAlignmentId { get; set; }
}

public sealed class FounderLegendConnectEntitlementInput
{
    public string TargetUserId { get; set; } = string.Empty;
    public string TargetParticipantType { get; set; } = string.Empty;
    public bool AccessGranted { get; set; }
    public string EntitlementMode { get; set; } = "Custom";
    public string? PresetKey { get; set; }
    public long? CustomCharacterAllowance { get; set; }
}

public sealed record FounderLegendConnectEntitlementResult(
    bool Succeeded,
    string Message);

public sealed class FounderLegendConnectRuntimePolicyInput
{
    public long MonthlyProviderCapacityCharacters { get; set; }
    public long LiveTranslationReserveCharacters { get; set; }
    public long MaximumSafeCorpusConsumptionCharacters { get; set; }
    public bool LearningEnabled { get; set; }
    public string ContextualCompositionMode { get; set; } = "Shadow";
    public decimal ContextualMinimumConfidence { get; set; } = 0.98m;
}

public sealed class FounderLegendConnectPriorityOverrideInput
{
    public string? LanguageCode { get; set; }
    public string? PairKey { get; set; }
}

public sealed record FounderLegendConnectOperationResult(bool Succeeded, string Message);
