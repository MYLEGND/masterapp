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
    public LegendConnectPairHealthSnapshot? SelectedPair { get; init; }
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
