using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Locks the single governed Founder diagnostic surface to comprehensive read access,
/// independent-read continuation, bounded mutation authority, and finite provider safety envelopes.
/// </summary>
public sealed class LegendFounderAiFullDiagnosticAccessContractTests
{
    [Fact]
    public void TeacherDiagnosticIntent_CoversArchitectureKnowledgeMemoryAndObservability()
    {
        var source = Read("AgentPortal/Services/LegendFounderAiConversationService.cs");
        foreach (var signal in new[]
                 {
                     "architecture", "system prompt", "tool registry", "retrieval",
                     "memory", "ingestion", "embedding", "configuration",
                     "observability", "logs", "evaluation", "knowledge system"
                 })
            Assert.Contains($"\"{signal}\"", source, StringComparison.Ordinal);
        Assert.Contains("legend_inspect_repository to discover and read the safe repository tree", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadFailures_AreStructuredAndDoNotAbortIndependentInspection()
    {
        var source = Read("AgentPortal/Services/LegendFounderAiConversationService.cs");
        Assert.Contains("SerializeFounderToolFailure", source, StringComparison.Ordinal);
        Assert.Contains("IsSuccessfulFounderToolOutput(toolOutput)", source, StringComparison.Ordinal);
        Assert.Contains("Independent reads may continue", source, StringComparison.Ordinal);
        Assert.DoesNotContain("throw new LegendFounderAiToolExecutionException(\n                call.Name,\n                \"tool_read_failed\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryInspection_HasBroaderReadAuthorityButRepairAuthorityRemainsBounded()
    {
        var source = Read("AgentPortal/Services/FounderSoftwareRemediationService.cs");
        Assert.Contains("IsAllowedReadPath", source, StringComparison.Ordinal);
        Assert.Contains("IsAllowedRepairPath", source, StringComparison.Ordinal);
        Assert.Contains("ReadRepositoryDirectory", source, StringComparison.Ordinal);
        Assert.Contains("MaximumReadFileCharacters = 240_000", source, StringComparison.Ordinal);
        Assert.Contains("path.StartsWith(\".github/\"", source, StringComparison.Ordinal);
        Assert.Contains("path.Contains(\"appsettings\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderWindows_AreExpandedWithoutRemovingFiniteSafetyEnvelope()
    {
        var source = Read("AgentPortal/Services/LegendFounderAiConversationService.cs");
        Assert.Contains("MaximumProviderConversationCharacters = 400_000", source, StringComparison.Ordinal);
        Assert.Contains("MaximumCasualOutputTokens = 8_000", source, StringComparison.Ordinal);
        Assert.Contains("MaximumReadOnlyToolSeconds = 60", source, StringComparison.Ordinal);
        Assert.Contains("MaximumToolOutputCharacters = 180_000", source, StringComparison.Ordinal);
        Assert.Contains("32_000,", source, StringComparison.Ordinal);
        Assert.Contains("64_000);", source, StringComparison.Ordinal);
        Assert.Contains("600);", source, StringComparison.Ordinal);
    }

    private static string Read(
        string relative,
        [CallerFilePath] string sourceFilePath = "")
    {
        var testsDirectory = Path.GetDirectoryName(sourceFilePath);
        if (string.IsNullOrWhiteSpace(testsDirectory))
            throw new DirectoryNotFoundException("Unable to resolve the AgentPortal.Tests source directory.");

        var root = Directory.GetParent(testsDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(root))
            throw new DirectoryNotFoundException("Unable to resolve the MASTERAPP repository root.");

        var path = Path.Combine(
            root,
            relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new FileNotFoundException(relative, path);

        return File.ReadAllText(path);
    }
}
