using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace AgentPortal.Tests;

public class WebsiteAnalyticsAiContractTests
{
    [Fact]
    public void AiReviewContract_SourceFilesExposeScaleReadinessAndDataTrustFields()
    {
        var repoRoot = GetRepoRoot();
        var dtoFile = File.ReadAllText(Path.Combine(repoRoot, "AgentPortal", "Models", "Analytics", "AiInsightsDtos.cs"));
        var controllerFile = File.ReadAllText(Path.Combine(repoRoot, "AgentPortal", "Controllers", "WebsiteAnalyticsAiController.cs"));
        var serviceFile = File.ReadAllText(Path.Combine(repoRoot, "AgentPortal", "Services", "Analytics", "OpenAiWebsiteAnalyticsReviewService.cs"));
        var uiFile = File.ReadAllText(Path.Combine(repoRoot, "AgentPortal", "wwwroot", "js", "website-analytics-ai.js"));

        Assert.Contains("public int? GrowthOperatorScore { get; set; }", dtoFile, StringComparison.Ordinal);
        Assert.Contains("public string? ScaleReadinessVerdict { get; set; }", dtoFile, StringComparison.Ordinal);
        Assert.Contains("public string? DataTrustWarning { get; set; }", dtoFile, StringComparison.Ordinal);
        Assert.Contains("public List<string> DoNotScaleBecause { get; set; } = new();", dtoFile, StringComparison.Ordinal);
        Assert.Contains("public List<string> NextThreeActions { get; set; } = new();", dtoFile, StringComparison.Ordinal);
        Assert.Contains("public MarketingHealthAiPayload? MarketingHealth { get; set; }", dtoFile, StringComparison.Ordinal);

        Assert.Contains("ScaleReadinessVerdict = \"DoNotScale\"", controllerFile, StringComparison.Ordinal);
        Assert.Contains("DataTrustWarning = message", controllerFile, StringComparison.Ordinal);
        Assert.Contains("DoNotScaleBecause = new List<string> { message }", controllerFile, StringComparison.Ordinal);
        Assert.Contains("NextThreeActions = new List<string>()", controllerFile, StringComparison.Ordinal);

        Assert.Contains("STEP 3 — TRACKING / PIPELINE HEALTH", serviceFile, StringComparison.Ordinal);
        Assert.Contains("scaleReadinessVerdict MUST be either DoNotScale or StabilizeFirst", serviceFile, StringComparison.Ordinal);
        Assert.Contains("dataTrustWarning should be a short blunt statement", serviceFile, StringComparison.Ordinal);

        Assert.Contains("result.scaleReadinessVerdict", uiFile, StringComparison.Ordinal);
        Assert.Contains("result.growthOperatorScore", uiFile, StringComparison.Ordinal);
        Assert.Contains("result.dataTrustWarning", uiFile, StringComparison.Ordinal);
        Assert.Contains("result.doNotScaleBecause", uiFile, StringComparison.Ordinal);
        Assert.Contains("result.nextThreeActions", uiFile, StringComparison.Ordinal);
    }

    private static string GetRepoRoot([CallerFilePath] string currentFile = "")
    {
        var directory = Path.GetDirectoryName(currentFile)
            ?? throw new DirectoryNotFoundException("Could not resolve test file path.");
        return Path.GetFullPath(Path.Combine(directory, ".."));
    }
}
