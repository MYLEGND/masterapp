using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using AgentPortal.Services;
using Xunit;

namespace AgentPortal.Tests;

// Locks the one-authority Founder Teacher diagnostic contract against regression.
public sealed class LegendFounderAiComprehensiveDiagnosticContractTests
{
    // Mandatory inspection now depends on admitted meaning and the exact
    // governed result-frame scope, rather than keywords or an arbitrary tool
    // count. ModeIsolation tests execute optional discovery, exact scoped
    // reads, unrelated-read rejection and partial-read disclosure end to end.

    [Theory]
    [InlineData(false, "connectivity_failure", "not_implicated")]
    [InlineData(true, "permission_denied", "denied")]
    public void ReadToolFailure_PreservesStructuredAuthorityAndCorrelation(
        bool permissionDenied, string expectedCategory, string expectedAuthorization)
    {
        var method = typeof(LegendFounderAiConversationService).GetMethod(
            "BuildReadOnlyToolFailureOutput", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Exception failure = permissionDenied
            ? new UnauthorizedAccessException("controlled access denial")
            : new HttpRequestException("controlled transport failure");
        var serialized = Assert.IsType<string>(method!.Invoke(null,
            new object[] { "legend_search_retained_knowledge", failure }));
        using var document = JsonDocument.Parse(serialized);
        var root = document.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("tool_read_failed", root.GetProperty("error").GetString());
        Assert.Equal(expectedCategory, root.GetProperty("failureCategory").GetString());
        Assert.Equal(expectedAuthorization, root.GetProperty("authorizationDecision").GetString());
        Assert.Equal("legend_search_retained_knowledge", root.GetProperty("requestedResource").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("correlationId").GetString()));
        Assert.Contains("Continue any independent governed reads", root.GetProperty("instruction").GetString());
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
        Assert.Contains("lexeme.NormalizedHash == requestHash", source, StringComparison.Ordinal);
        Assert.Contains("candidate.Sum(item => item.MatchedOccurrenceCount)", source, StringComparison.Ordinal);
        Assert.Contains("anchor.ComponentStartTokenIndex != null", source, StringComparison.Ordinal);
        Assert.Contains("anchor.ComponentStartTokenIndex >= 0", source, StringComparison.Ordinal);
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
