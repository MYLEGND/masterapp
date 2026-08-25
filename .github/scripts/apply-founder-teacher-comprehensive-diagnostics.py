from pathlib import Path

service = Path('AgentPortal/Services/LegendFounderAiConversationService.cs')
text = service.read_text()

replacements = [
    ('private const int MaximumConversationMessages = 30;', 'private const int MaximumConversationMessages = 60;'),
    ('private const int MaximumMessageCharacters = 500_000;', 'private const int MaximumMessageCharacters = 1_000_000;'),
    ('private const int MaximumConversationCharacters = 750_000;', 'private const int MaximumConversationCharacters = 2_000_000;'),
    ('private const int MinimumProviderConversationCharacters = 60_000;', 'private const int MinimumProviderConversationCharacters = 120_000;'),
    ('private const int MaximumProviderConversationCharacters = 180_000;', 'private const int MaximumProviderConversationCharacters = 600_000;'),
    ('private const int MinimumLatestMessageTailCharacters = 12_000;', 'private const int MinimumLatestMessageTailCharacters = 24_000;'),
    ('private const int MinimumToolRounds = 4;', 'private const int MinimumToolRounds = 6;'),
    ('private const int MaximumToolRounds = 10;', 'private const int MaximumToolRounds = 16;'),
    ('private const int MaximumCasualOutputTokens = 1_200;', 'private const int MaximumCasualOutputTokens = 4_000;'),
    ('private const int MinimumReadOnlyToolSeconds = 8;', 'private const int MinimumReadOnlyToolSeconds = 12;'),
    ('private const int MaximumReadOnlyToolSeconds = 20;', 'private const int MaximumReadOnlyToolSeconds = 45;'),
    ('private const int MinimumToolOutputCharacters = 20_000;', 'private const int MinimumToolOutputCharacters = 40_000;'),
    ('private const int MaximumToolOutputCharacters = 80_000;', 'private const int MaximumToolOutputCharacters = 160_000;'),
    ('private const int MinimumRetainedContextCharacters = 16_000;', 'private const int MinimumRetainedContextCharacters = 32_000;'),
    ('private const int MaximumRetainedContextCharacters = 64_000;', 'private const int MaximumRetainedContextCharacters = 128_000;'),
]
for old, new in replacements:
    assert old in text, f'constant anchor missing: {old}'
    text = text.replace(old, new, 1)

old = '''                    "OpenAI:LegendFounderAiTimeoutSeconds") ??
                    120,
                45,
                240);'''
new = '''                    "OpenAI:LegendFounderAiTimeoutSeconds") ??
                    210,
                60,
                240);'''
assert old in text
text = text.replace(old, new, 1)

old = '''                    "OpenAI:LegendFounderAiMaxOutputTokens") ??
                    8_000,
                1_500,
                16_000);'''
new = '''                    "OpenAI:LegendFounderAiMaxOutputTokens") ??
                    32_000,
                2_000,
                64_000);'''
assert old in text
text = text.replace(old, new, 1)

old = '''        var requiresGovernedInspection =
            RequiresProviderGovernedInspection(
                conversation,
                mode,
                nativeInference,
                nativeFailureDetail);
'''
new = '''        var requiresGovernedInspection =
            RequiresProviderGovernedInspection(
                conversation,
                mode,
                nativeInference,
                nativeFailureDetail);

        var requiresComprehensiveGovernedInspection =
            RequiresComprehensiveGovernedInspection(
                conversation,
                mode);
'''
assert old in text
text = text.replace(old, new, 1)

old = '''            var maximumToolRounds =
                requiresGovernedInspection
                    ? ResolveMaximumToolRounds(conversation)
                    : 1;

            var governedInspectionCompleted =
                !requiresMandatoryGovernedInspection;
'''
new = '''            var maximumToolRounds =
                requiresGovernedInspection
                    ? ResolveMaximumToolRounds(conversation)
                    : 3;

            var requiredGovernedEvidenceReads =
                requiresComprehensiveGovernedInspection
                    ? 3
                    : 1;

            var successfulGovernedEvidenceTools =
                new HashSet<string>(StringComparer.Ordinal);

            var governedReadAttempts = 0;

            var governedInspectionCompleted =
                !requiresMandatoryGovernedInspection;
'''
assert old in text
text = text.replace(old, new, 1)

old = '''                if (requiresMandatoryGovernedInspection &&
                    !governedInspectionCompleted &&
                    !allowTools)
                {
                    return LegendFounderAiChatResponse.ModeFailure(
                        mode,
                        FailureMessageForMode(
                            mode,
                            "The remaining request window is too small to complete the required governed LEGEND inspection safely."),
                        "governed_inspection",
                        "governed_tool",
                        "required_governed_inspection_budget_unavailable");
                }

                var requireToolCall =
                    requiresMandatoryGovernedInspection &&
                    !governedInspectionCompleted;
'''
new = '''                if (requiresMandatoryGovernedInspection &&
                    !governedInspectionCompleted &&
                    !allowTools &&
                    governedReadAttempts == 0)
                {
                    return LegendFounderAiChatResponse.ModeFailure(
                        mode,
                        FailureMessageForMode(
                            mode,
                            "The remaining request window is too small to begin the required governed LEGEND inspection safely."),
                        "governed_inspection",
                        "governed_tool",
                        "required_governed_inspection_budget_unavailable");
                }

                var requireToolCall =
                    requiresMandatoryGovernedInspection &&
                    !governedInspectionCompleted &&
                    allowTools;
'''
assert old in text
text = text.replace(old, new, 1)

old = '''                if (responseState == "incomplete")
                {
                    if (requiresMandatoryGovernedInspection &&
                        !governedInspectionCompleted)
                    {
                        return LegendFounderAiChatResponse.ModeFailure(
                            mode,
                            FailureMessageForMode(
                                mode,
                                "The provider output ended before the required governed LEGEND inspection completed."),
                            "governed_inspection",
                            "provider_response",
                            "required_governed_inspection_missing");
                    }

                    var partial =
                        ExtractOutputText(root);

                    if (!string.IsNullOrWhiteSpace(
                            partial))
                    {
                        return new LegendFounderAiChatResponse(
                            true,
                            mode,
                            partial.Trim() +
                            "\\n\\n[This response reached the provider output window. Ask the OpenAI Teacher to continue if you want the remainder.]",
                            null,
                            ResponseAuthority: "OpenAITeacher",
                            Stage: "provider_response");
                    }

                    return LegendFounderAiChatResponse.ModeFailure(
                        mode,
                        FailureMessageForMode(
                            mode,
                            "The provider output window ended before usable text was produced."),
                        "provider_incomplete",
                        "provider_response",
                        "provider_output_incomplete");
                }
'''
new = '''                if (responseState == "incomplete")
                {
                    if (requiresMandatoryGovernedInspection &&
                        !governedInspectionCompleted)
                    {
                        return LegendFounderAiChatResponse.ModeFailure(
                            mode,
                            FailureMessageForMode(
                                mode,
                                "The provider output ended before the required governed LEGEND inspection completed."),
                            "governed_inspection",
                            "provider_response",
                            "required_governed_inspection_missing");
                    }

                    var partial =
                        ExtractOutputText(root);

                    var remainingAfterProvider =
                        TimeSpan.FromSeconds(_timeoutSeconds) -
                        executionClock.Elapsed;

                    if (!string.IsNullOrWhiteSpace(partial) &&
                        round < maximumToolRounds - 1 &&
                        remainingAfterProvider > TimeSpan.FromSeconds(8))
                    {
                        input.Add(new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = partial.Trim()
                        });
                        input.Add(new Dictionary<string, object?>
                        {
                            ["role"] = "user",
                            ["content"] = "Continue the same answer exactly where it stopped. Do not restart, summarize, or repeat completed material."
                        });
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(partial))
                    {
                        return new LegendFounderAiChatResponse(
                            true,
                            mode,
                            partial.Trim(),
                            null,
                            ResponseAuthority: "OpenAITeacher",
                            Stage: "provider_response");
                    }

                    return LegendFounderAiChatResponse.ModeFailure(
                        mode,
                        FailureMessageForMode(
                            mode,
                            "The provider output window ended before usable text was produced."),
                        "provider_incomplete",
                        "provider_response",
                        "provider_output_incomplete");
                }
'''
assert old in text
text = text.replace(old, new, 1)

old = '''                if (toolCalls.Count == 0)
                {
                    if (requiresMandatoryGovernedInspection &&
                        !governedInspectionCompleted)
                    {
                        return LegendFounderAiChatResponse.ModeFailure(
                            mode,
                            FailureMessageForMode(
                                mode,
                                "The provider did not perform the required governed LEGEND inspection, so no current-state answer was accepted."),
                            "governed_inspection",
                            "governed_tool",
                            "required_governed_inspection_missing");
                    }
'''
new = '''                if (toolCalls.Count == 0)
                {
                    if (requiresMandatoryGovernedInspection &&
                        !governedInspectionCompleted &&
                        governedReadAttempts == 0)
                    {
                        return LegendFounderAiChatResponse.ModeFailure(
                            mode,
                            FailureMessageForMode(
                                mode,
                                "The provider did not perform the required governed LEGEND inspection, so no current-state answer was accepted."),
                            "governed_inspection",
                            "governed_tool",
                            "required_governed_inspection_missing");
                    }
'''
assert old in text
text = text.replace(old, new, 1)

old = '''                    if (IsReadOnlyFounderTool(call.Name))
                    {
                        governedInspectionCompleted = true;
                    }
'''
new = '''                    if (IsReadOnlyFounderTool(call.Name))
                    {
                        governedReadAttempts++;

                        if (IsGovernedEvidenceTool(call.Name) &&
                            IsSuccessfulFounderToolOutput(toolOutput))
                        {
                            successfulGovernedEvidenceTools.Add(call.Name);
                            governedInspectionCompleted =
                                successfulGovernedEvidenceTools.Count >=
                                requiredGovernedEvidenceReads;
                        }
                    }
'''
assert old in text
text = text.replace(old, new, 1)

old = '''        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND Founder AI read-only tool {Tool} failed before a response could be produced.",
                call.Name);

            throw new LegendFounderAiToolExecutionException(
                call.Name,
                "tool_read_failed",
                "governed_tool");
        }
    }

    private static bool IsReadOnlyFounderTool(
'''
new = '''        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND Founder AI read-only tool {Tool} failed; preserving the exact tool failure for OpenAI and continuing independent governed reads.",
                call.Name);

            return BuildReadOnlyToolFailureOutput(
                call.Name,
                exception);
        }
    }

    private static string BuildReadOnlyToolFailureOutput(
        string tool,
        Exception exception) =>
        JsonSerializer.Serialize(
            new
            {
                ok = false,
                error = "tool_read_failed",
                tool,
                exceptionType = exception.GetType().Name,
                detail = NormalizeToolFailureDetail(exception.Message),
                instruction = "This read failed. Continue any independent governed reads that can still execute, then report this exact failed authority without inventing unavailable state."
            },
            JsonOptions);

    private static string NormalizeToolFailureDetail(string? value)
    {
        var detail = NormalizeFailureDetail(value);
        foreach (var sensitiveName in new[]
                 {
                     "password=", "pwd=", "user id=", "uid=",
                     "api_key=", "apikey=", "access_token=", "connectionstring="
                 })
        {
            var index = detail.IndexOf(sensitiveName, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
                return detail[..index] + "[REDACTED SENSITIVE CONFIGURATION DETAIL]";
        }

        return detail;
    }

    private static bool IsSuccessfulFounderToolOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;

        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.ValueKind != JsonValueKind.Object ||
                   !document.RootElement.TryGetProperty("error", out _);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool IsGovernedEvidenceTool(string name) =>
        IsReadOnlyFounderTool(name) &&
        !string.Equals(name, "legend_capabilities", StringComparison.Ordinal);

    private static bool IsReadOnlyFounderTool(
'''
assert old in text
text = text.replace(old, new, 1)

old = '''            "canonical", "retained knowledge", "retained evidence",
            "governed inspection", "current authority",
            "curriculum", "train legend", "training status",
            "model readiness", "readiness", "alignment", "provenance",
            "evidence", "system state", "system status", "metrics", "metric",
            "provider capacity", "azure", "corpus", "production",
            "deployment", "repository", "github", "pull request",
            "branch", "commit", "workflow", "ci", "coverage"
'''
new = '''            "canonical", "retained knowledge", "retained evidence",
            "governed inspection", "current authority",
            "curriculum", "train legend", "training status",
            "model readiness", "readiness", "alignment", "provenance",
            "evidence", "system state", "system status", "metrics", "metric",
            "provider capacity", "azure", "corpus", "production",
            "deployment", "repository", "github", "pull request",
            "branch", "commit", "workflow", "ci", "coverage",
            "architecture", "database", "data model", "schema", "configuration",
            "config", "observability", "logs", "logging", "telemetry", "trace",
            "prompt", "system prompt", "routing", "fallback", "tool registry",
            "tooling", "permission", "retrieval", "memory", "ingestion", "index",
            "embedding", "evaluation", "validator", "critic", "promotion",
            "learning pipeline", "reasoning", "respond", "reuse knowledge"
'''
assert old in text
text = text.replace(old, new, 1)

anchor = '''    private static bool ShouldAttemptNativeInference(string mode) =>
'''
insert = '''    private static bool RequiresComprehensiveGovernedInspection(
        IReadOnlyList<LegendFounderAiChatMessage> conversation,
        string mode)
    {
        if (!IsTeacherMode(mode))
            return false;

        var latest = conversation
            .Last(message => string.Equals(message.Role, "user", StringComparison.Ordinal))
            .Content?.ToLowerInvariant() ?? string.Empty;

        var broadSignals = new[]
        {
            "everything", "entire", "full system", "complete system",
            "all of legend", "how legend works", "how legend is set up",
            "architecture", "diagnose the system", "inspect the system",
            "learn, reason", "learn reason", "reuse knowledge",
            "curriculum and", "repository and", "database and"
        };

        return broadSignals.Any(signal =>
            latest.Contains(signal, StringComparison.Ordinal));
    }

'''
assert anchor in text
text = text.replace(anchor, insert + anchor, 1)

old = '''        var target = totalCharacters <= 120_000
            ? 120_000
            : MaximumProviderConversationCharacters;
'''
new = '''        var target = totalCharacters <= 300_000
            ? 300_000
            : MaximumProviderConversationCharacters;
'''
assert old in text
text = text.replace(old, new, 1)

old = '''- You can inspect LEGEND through read tools.
- Native OpenAI web search is available for current external research, verification, trusted linguistic references, standards, documentation and other information that is not already established by LEGEND.
'''
new = '''- You can inspect LEGEND through the read tools exposed in this session. Those tools are real capabilities; never tell the Founder that repository, LEGEND data, deployment, curriculum, configuration, or diagnostic access must be manually provided when an exposed governed tool can read the required evidence.
- If you are uncertain which inspection capabilities exist, call legend_capabilities and then continue with the relevant evidence tools. Capability discovery alone is not evidence that the requested system state was inspected.
- A failure in one read authority must not end a broad inspection. Preserve that tool's structured failure, continue every independent governed read that can still execute, and distinguish successful evidence from unavailable evidence in the final answer.
- For broad architecture/training/knowledge diagnostics, inspect enough independent evidence categories to support the requested claims rather than stopping after one tool call.
- Native OpenAI web search is available for current external research, verification, trusted linguistic references, standards, documentation and other information that is not already established by LEGEND.
'''
assert old in text
text = text.replace(old, new, 1)

old = '''Your job is to:
- reason deeply about language acquisition, semantics, discourse, grammar, morphology, translation quality and curriculum strategy;
- inspect current LEGEND state when useful;
'''
new = '''Your job is to:
- reason deeply about language acquisition, semantics, discourse, grammar, morphology, translation quality and curriculum strategy;
- act as the Founder's comprehensive diagnostic machine for LEGEND through the existing governed read authorities;
- inspect current LEGEND state whenever the Founder's request depends on current architecture, data, curriculum, retained knowledge, retrieval, training, evaluation, provider, repository, deployment, configuration, or operational evidence;
'''
assert old in text
text = text.replace(old, new, 1)

service.write_text(text)

test = Path('AgentPortal.Tests/LegendFounderAiComprehensiveDiagnosticContractTests.cs')
test.write_text(r'''using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFounderAiComprehensiveDiagnosticContractTests
{
    [Fact]
    public void Teacher_CurrentSystemDiagnostics_ExposeBroadNaturalLanguageSignals()
    {
        var source = ReadService();
        foreach (var signal in new[]
                 {
                     "architecture", "database", "observability", "configuration",
                     "tool registry", "retrieval", "memory", "ingestion", "evaluation",
                     "reuse knowledge"
                 })
        {
            Assert.Contains($"\"{signal}\"", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReadToolFailure_IsReturnedToTeacher_AndDoesNotAbortIndependentReads()
    {
        var source = ReadService();
        Assert.Contains("BuildReadOnlyToolFailureOutput", source, StringComparison.Ordinal);
        Assert.Contains("Continue any independent governed reads", source, StringComparison.Ordinal);
        Assert.Contains("successfulGovernedEvidenceTools", source, StringComparison.Ordinal);
        Assert.Contains("IsSuccessfulFounderToolOutput", source, StringComparison.Ordinal);
        Assert.Contains("IsGovernedEvidenceTool", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LEGEND Founder AI read-only tool {Tool} failed before a response could be produced.",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ComprehensiveInspection_RequiresMultipleDistinctEvidenceAuthorities()
    {
        var source = ReadService();
        Assert.Contains("requiresComprehensiveGovernedInspection", source, StringComparison.Ordinal);
        Assert.Contains("? 3", source, StringComparison.Ordinal);
        Assert.Contains("new HashSet<string>(StringComparer.Ordinal)", source, StringComparison.Ordinal);
        Assert.Contains("!string.Equals(name, \"legend_capabilities\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FounderProviderWindows_AreLargeAndAutomaticallyContinueIncompleteAnswers()
    {
        var source = ReadService();
        Assert.Contains("MaximumProviderConversationCharacters = 600_000", source, StringComparison.Ordinal);
        Assert.Contains("MaximumConversationCharacters = 2_000_000", source, StringComparison.Ordinal);
        Assert.Contains("MaximumToolOutputCharacters = 160_000", source, StringComparison.Ordinal);
        Assert.Contains("32_000", source, StringComparison.Ordinal);
        Assert.Contains("64_000", source, StringComparison.Ordinal);
        Assert.Contains("Continue the same answer exactly where it stopped", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Ask the OpenAI Teacher to continue if you want the remainder", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Teacher_IsToldExistingGovernedAccessIsReal_NotToRequestManualExports()
    {
        var source = ReadService();
        Assert.Contains("Those tools are real capabilities", source, StringComparison.Ordinal);
        Assert.Contains("never tell the Founder", source, StringComparison.Ordinal);
        Assert.Contains("Capability discovery alone is not evidence", source, StringComparison.Ordinal);
    }

    private static string ReadService()
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root,
            "AgentPortal",
            "Services",
            "LegendFounderAiConversationService.cs"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "AgentPortal")) &&
                Directory.Exists(Path.Combine(directory.FullName, "AgentPortal.Tests")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
''')

Path('.github/workflows/apply-founder-teacher-comprehensive-diagnostics.yml').unlink(missing_ok=True)
Path('.github/scripts/apply-founder-teacher-comprehensive-diagnostics.py').unlink(missing_ok=True)
