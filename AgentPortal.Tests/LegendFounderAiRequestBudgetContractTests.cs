using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFounderAiRequestBudgetContractTests
{
    [Fact]
    public void TeacherRequestBudget_IsLongEnoughForStreamedComprehensiveDiagnostics()
    {
        var source = ReadSource("AgentPortal", "Services", "LegendFounderAiConversationService.cs");

        Assert.Contains("??\n                    900,", source, StringComparison.Ordinal);
        Assert.Contains("120,\n                1_800", source, StringComparison.Ordinal);
        Assert.Contains("MinimumFinalizationReserveSeconds = 45", source, StringComparison.Ordinal);
        Assert.Contains("MinimumFinalSynthesisWindowSeconds = 60", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedGovernedInspection_ForcesSynthesisInsteadOfUnboundedToolChurn()
    {
        var source = ReadSource("AgentPortal", "Services", "LegendFounderAiConversationService.cs");

        Assert.Contains(
            "!governedInspectionCompleted ||\n                        (confirmedLearningMutationRequired &&\n                         !learningMutationCompleted) ||\n                        (automaticNativeGapLearningWindow &&\n                         !learningMutationCompleted)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "round < maximumToolRounds - 1",
            source,
            StringComparison.Ordinal);
        Assert.Contains("parallel_tool_calls = allowTools", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LongTeacherRequests_KeepUsingTheSingleStreamingChatEndpoint()
    {
        var controller = ReadSource("AgentPortal", "Controllers", "LegendFounderAiController.cs");

        Assert.Contains("StreamHeartbeatInterval = TimeSpan.FromSeconds(4)", controller, StringComparison.Ordinal);
        Assert.Contains("await StreamChatAsync(", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("BackgroundJob", controller, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] path)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(path).ToArray()))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
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
