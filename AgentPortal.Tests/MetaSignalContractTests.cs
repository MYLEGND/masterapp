using System;
using System.Collections.Generic;
using System.IO;
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
    public void MetaSignalServerWrites_OnlyCatalogedSignals()
    {
        var file = Path.Combine(GetRepoRoot(), "Protect-Website", "Services", "MetaSignal", "MetaSignalIntelligenceService.cs");
        var content = File.ReadAllText(file);

        var emittedSignals = Regex.Matches(content, @"EventName\s*=\s*""([A-Za-z][A-Za-z0-9]+)""")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(emittedSignals);
        foreach (var signal in emittedSignals)
        {
            Assert.True(MetaSignalEventCatalog.TryGet(signal, out _), $"Server Meta signal '{signal}' is not cataloged.");
        }
    }

    private static string GetRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "MASTERAPP.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
