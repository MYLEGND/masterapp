using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFounderAiComprehensiveInspectionContractTests
{
    [Fact]
    public void TeacherInspection_ReusesFounderAuthoritiesAndContinuesAfterIndependentReadFailure()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "AgentPortal", "Services", "LegendFounderAiConversationService.cs"));

        Assert.Contains("name = \"legend_founder_dashboard\"", source, StringComparison.Ordinal);
        Assert.Contains("name = \"legend_section_page\"", source, StringComparison.Ordinal);
        Assert.Contains("await _legend.GetDashboardAsync(", source, StringComparison.Ordinal);
        Assert.Contains("await _legend.GetSectionPageAsync(", source, StringComparison.Ordinal);
        Assert.Contains("Continuing independent governed inspection", source, StringComparison.Ordinal);
        Assert.Contains("recoverable = true", source, StringComparison.Ordinal);
        Assert.Contains("toolSucceeded && IsReadOnlyFounderTool", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TeacherConversation_UsesExpandedButFiniteProviderEnvelope()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "AgentPortal", "Services", "LegendFounderAiConversationService.cs"));

        Assert.Contains("MaximumProviderConversationCharacters = 320_000", source, StringComparison.Ordinal);
        Assert.Contains("MaximumCasualOutputTokens = 8_000", source, StringComparison.Ordinal);
        Assert.Contains("MaximumToolOutputCharacters = 120_000", source, StringComparison.Ordinal);
        Assert.Contains("24_000,\n                8_000,\n                64_000", source, StringComparison.Ordinal);
        Assert.Contains("300,\n                180,\n                600", source, StringComparison.Ordinal);
        Assert.Contains("truncation = \"auto\"", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "AgentPortal")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
