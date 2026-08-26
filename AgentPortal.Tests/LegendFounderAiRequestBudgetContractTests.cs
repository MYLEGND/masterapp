using System;
using System.IO;
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
            "requiresGovernedInspection &&\n                    !governedInspectionCompleted &&",
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
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(path).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join('/', path));
    }
}
