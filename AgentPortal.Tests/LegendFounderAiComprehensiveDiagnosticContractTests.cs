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
            if (File.Exists(Path.Combine(directory.FullName, "MASTERAPP.sln")) &&
                Directory.Exists(Path.Combine(directory.FullName, "AgentPortal")) &&
                Directory.Exists(Path.Combine(directory.FullName, "AgentPortal.Tests")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
