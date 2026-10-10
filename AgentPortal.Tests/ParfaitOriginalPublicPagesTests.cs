using System;
using System.IO;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ParfaitOriginalPublicPagesTests
{
    [Fact]
    public void SharedHostUsesOriginalParfaitPublicPagesAndPrivatePreviewController()
    {
        var root = FindRoot();
        var protect = File.ReadAllText(Path.Combine(root, "Protect-Website", "ProtectWebsite.csproj"));
        foreach (var name in new[] { "Home", "About", "Contact", "TrainingPackages", "Training", "Resources", "Support" })
            Assert.Contains("Views/ParfaitOriginal/" + name + ".cshtml", protect, StringComparison.Ordinal);

        var preview = File.ReadAllText(Path.Combine(root, "CommerceCore", "Controllers", "ParfaitPublicPreviewController.cs"));
        Assert.Contains("CommerceSharedHostPreviewGate.OriginalPagePathItem", preview, StringComparison.Ordinal);
        Assert.Contains("ParfaitHostLayout", preview, StringComparison.Ordinal);
        Assert.Contains("ViewBag.SquarePrelaunchLink = \"#\"", preview, StringComparison.Ordinal);
        Assert.Contains("return NotFound()", preview, StringComparison.Ordinal);

        var home = File.ReadAllText(Path.Combine(root, "ParfaitApp", "Views", "Home", "Index.cshtml"));
        Assert.Contains("ViewData[\"ParfaitHostLayout\"] as string ??", home, StringComparison.Ordinal);
        Assert.Contains("Welcome to Parfait", home, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
            ?? Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MASTERAPP.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root unavailable.");
    }
}
