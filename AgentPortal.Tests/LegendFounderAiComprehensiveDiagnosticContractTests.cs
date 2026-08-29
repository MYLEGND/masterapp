using System;
using System.IO;
using Xunit;

namespace AgentPortal.Tests;

// Locks the one-authority Founder Teacher diagnostic contract against regression.
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
        Assert.Contains("failureCategory", source, StringComparison.Ordinal);
        Assert.Contains("requestedResource", source, StringComparison.Ordinal);
        Assert.Contains("authorizationDecision", source, StringComparison.Ordinal);
        Assert.Contains("correlationId", source, StringComparison.Ordinal);
        Assert.Contains("successfulGovernedEvidenceTools", source, StringComparison.Ordinal);
        Assert.Contains("IsSuccessfulFounderToolOutput", source, StringComparison.Ordinal);
        Assert.Contains("_toolAuthority.IsGovernedEvidence", source, StringComparison.Ordinal);
        var authority = ReadRepositoryFile(
            "AgentPortal",
            "Services",
            "LegendFounderToolAuthority.cs");
        Assert.Contains("IsGovernedEvidenceTool", authority, StringComparison.Ordinal);
        Assert.Contains("ExecuteAsync", authority, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteFounderToolAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Independent broad inspection was not requested",
            source,
            StringComparison.Ordinal);
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
        Assert.Contains("MergeProviderAnswerSegment", source, StringComparison.Ordinal);
        Assert.Contains("accumulatedProviderAnswer", source, StringComparison.Ordinal);
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

    [Fact]
    public void NativeMeaningGraph_QueryIsScopedToLexemesInTheCurrentInput()
    {
        var source = ReadRepositoryFile(
            "Infrastructure",
            "Messaging",
            "LegendConnectCurriculum.cs");

        Assert.Contains("inputLexemeHashes", source, StringComparison.Ordinal);
        Assert.Contains("join lexeme in _db.Set<LegendLanguageLexeme>()", source, StringComparison.Ordinal);
        Assert.Contains("inputLexemeHashes.Contains(lexeme.NormalizedHash)", source, StringComparison.Ordinal);
        Assert.Contains("anchor.ComponentStartTokenIndex != null", source, StringComparison.Ordinal);
    }

    private static string ReadService() =>
        ReadRepositoryFile(
            "AgentPortal",
            "Services",
            "LegendFounderAiConversationService.cs");

    private static string ReadRepositoryFile(params string[] path)
    {
        var segments = new string[path.Length + 1];
        segments[0] = FindRepositoryRoot();
        Array.Copy(path, 0, segments, 1, path.Length);
        return File.ReadAllText(Path.Combine(segments));
    }

    private static string FindRepositoryRoot()
    {
        var githubWorkspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (IsRepositoryRoot(githubWorkspace))
            return Path.GetFullPath(githubWorkspace!);

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (IsRepositoryRoot(directory.FullName))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Repository root was not found from GITHUB_WORKSPACE, the working directory, or the test base directory.");
    }

    private static bool IsRepositoryRoot(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(Path.Combine(path, "MASTERAPP.sln")) &&
        Directory.Exists(Path.Combine(path, "AgentPortal")) &&
        Directory.Exists(Path.Combine(path, "AgentPortal.Tests"));
}
