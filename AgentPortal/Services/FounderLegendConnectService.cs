using System.Security.Claims;
using AgentPortal.Models;
using AgentPortal.Security;
using Domain.Messaging;

namespace AgentPortal.Services;

/// <summary>
/// Founder-gated presentation adapter. The server-owned Legend Connect
/// operations facade remains the only language/learning authority.
/// </summary>
public sealed class FounderLegendConnectService
{
    private readonly ILegendConnectOperations _operations;

    public FounderLegendConnectService(ILegendConnectOperations operations) =>
        _operations = operations;

    public async Task<FounderLegendConnectDashboardVm> GetDashboardAsync(
        ClaimsPrincipal user,
        string? language,
        string? pair,
        CancellationToken cancellationToken = default)
    {
        FounderGuard.EnsureFounderOrThrow(user);
        var dashboard = await _operations.GetDashboardAsync(cancellationToken);
        var selectedLanguageKnowledge = string.IsNullOrWhiteSpace(language)
            ? null
            : await _operations.GetLanguageKnowledgeAsync(language, cancellationToken);
        var selectedPair = string.IsNullOrWhiteSpace(pair)
            ? null
            : await _operations.GetPairHealthAsync(pair, cancellationToken);
        return new FounderLegendConnectDashboardVm
        {
            Dashboard = dashboard,
            SelectedLanguage = selectedLanguageKnowledge?.Health,
            SelectedLanguageKnowledge = selectedLanguageKnowledge,
            SelectedPair = selectedPair
        };
    }

    public Task<LegendConnectKnowledgeSubmissionResult> SubmitAsync(
        ClaimsPrincipal user,
        FounderLegendConnectKnowledgeInput input,
        CancellationToken cancellationToken = default)
    {
        FounderGuard.EnsureFounderOrThrow(user);
        return _operations.SubmitFounderKnowledgeAsync(
            FounderId(user),
            ToSubmission(input),
            cancellationToken);
    }

    public Task<LegendConnectKnowledgeSubmissionResult> CorrectAsync(
        ClaimsPrincipal user,
        FounderLegendConnectCorrectionInput input,
        CancellationToken cancellationToken = default)
    {
        FounderGuard.EnsureFounderOrThrow(user);
        return _operations.CorrectFounderKnowledgeAsync(
            FounderId(user),
            input.SupersededAlignmentId,
            ToSubmission(input),
            cancellationToken);
    }

    private static LegendConnectKnowledgeSubmission ToSubmission(FounderLegendConnectKnowledgeInput input) => new(
        input.SourceLanguageCode,
        input.SourceText,
        input.TargetLanguageCode,
        input.TargetText,
        input.ContextCategory,
        input.UsageRegister,
        input.RegionalVariant,
        "FounderApproved");

    private static string FounderId(ClaimsPrincipal user) =>
        user.FindFirst("oid")?.Value?.Trim() ?? string.Empty;
}
