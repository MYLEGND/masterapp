using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text.RegularExpressions;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public class MetaSignalContractTests
{
    [Fact]
    public void MetaSignalFrontendEmits_OnlyCatalogedSignals()
    {
        var file = Path.Combine(GetRepoRoot(), "Protect-Website", "wwwroot", "js", "meta-signal-intelligence.js");
        var content = File.ReadAllText(file);

        var emittedSignals = Regex.Matches(content, @"emitSignal\('([A-Za-z][A-Za-z0-9]+)'")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(emittedSignals);
        foreach (var signal in emittedSignals)
        {
            Assert.True(MetaSignalEventCatalog.TryGet(signal, out _), $"Frontend Meta signal '{signal}' is not cataloged.");
        }
    }

    [Fact]
    public void MetaSignalBrowserPixelAllowlist_StaysCatalogAligned()
    {
        var file = Path.Combine(GetRepoRoot(), "Protect-Website", "wwwroot", "js", "meta-signal-intelligence.js");
        var content = File.ReadAllText(file);
        var match = Regex.Match(content, @"const DEFAULT_META_BROWSER_EVENTS = \[(.*?)\];", RegexOptions.Singleline);
        Assert.True(match.Success, "Could not locate DEFAULT_META_BROWSER_EVENTS.");

        var browserSignals = Regex.Matches(match.Groups[1].Value, @"'([A-Za-z][A-Za-z0-9]+)'")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(browserSignals);
        foreach (var signal in browserSignals)
        {
            Assert.True(MetaSignalEventCatalog.TryGet(signal, out var definition), $"Browser Meta signal '{signal}' is not cataloged.");
            Assert.True(definition.AllowBrowserPixel, $"Browser Meta signal '{signal}' must remain browser-pixel allowed.");
        }
    }

    [Fact]
    public void MetaSignalFrontend_ContainsLearningEnrichmentFields()
    {
        var file = Path.Combine(GetRepoRoot(), "Protect-Website", "wwwroot", "js", "meta-signal-intelligence.js");
        var content = File.ReadAllText(file);

        Assert.Contains("engagementIntensityScore", content, StringComparison.Ordinal);
        Assert.Contains("sessionConfidenceScore", content, StringComparison.Ordinal);
        Assert.Contains("funnelDepthIndex", content, StringComparison.Ordinal);
        Assert.Contains("trafficQualityHint", content, StringComparison.Ordinal);
        Assert.Contains("eventKey", content, StringComparison.Ordinal);
        Assert.Contains("isBrowserSignal", content, StringComparison.Ordinal);
        Assert.Contains("isServerAuthority", content, StringComparison.Ordinal);
        Assert.Contains("serverAuthorityWinsConflictResolution", content, StringComparison.Ordinal);
        Assert.Contains("browserPayloadCanOverrideServer", content, StringComparison.Ordinal);
        Assert.Contains("metaSignalBoost", content, StringComparison.Ordinal);
        Assert.Contains("sessionFingerprint", content, StringComparison.Ordinal);
        Assert.Contains("midIntentCandidate", content, StringComparison.Ordinal);
        Assert.Contains("highIntentCandidate", content, StringComparison.Ordinal);
        Assert.Contains("ctaIntentBoost", content, StringComparison.Ordinal);
        Assert.Contains("conversionIntentObserved", content, StringComparison.Ordinal);
    }

    [Fact]
    public void MetaSignalServerAuthorityCatalog_StaysCatalogAligned()
    {
        var emittedSignals = MetaSignalEventCatalog.ServerAuthorityEventNames
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(emittedSignals);
        foreach (var signal in emittedSignals)
        {
            Assert.True(MetaSignalEventCatalog.TryGet(signal, out var definition), $"Server Meta signal '{signal}' is not cataloged.");
            Assert.True(definition.AllowServerForward, $"Server Meta signal '{signal}' must remain server-forwardable.");
            Assert.True(MetaSignalEventCatalog.IsServerAuthorityEvent(signal), $"Server Meta signal '{signal}' must remain authority-classified.");
        }
    }

    private static string GetRepoRoot([CallerFilePath] string currentFile = "")
    {
        var directory = Path.GetDirectoryName(currentFile)
            ?? throw new DirectoryNotFoundException("Could not resolve test file path.");
        return Path.GetFullPath(Path.Combine(directory, ".."));
    }
}
