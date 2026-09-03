using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class ApplicationLocalizationNativeContractTests
{
    private const string VisualContext = "visual interface copy";
    private const string AccessibilityContext = "accessibility copy";

    [Fact]
    public void CanonicalManifest_CoversNativeLiteralPresentation_WithoutProviderBypasses()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "Legend-Design", "legend-application-copy.json")));
        var entries = document.RootElement.GetProperty("entries").EnumerateArray()
            .Select(item => new
            {
                Id = item.GetProperty("id").GetString()!,
                Source = item.GetProperty("source").GetString()!,
                Context = item.GetProperty("context").GetString()!,
                Revision = item.GetProperty("sourceRevision").GetString()!,
                Placeholders = item.GetProperty("placeholders").EnumerateArray()
                    .Select(value => value.GetString()!).ToArray()
            })
            .ToArray();

        Assert.True(entries.Length >= 1_450, $"Expected broad native/server copy coverage; found {entries.Length} entries.");
        Assert.Equal(entries.Length, entries.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Source));
            Assert.Contains(entry.Context, new[] { VisualContext, AccessibilityContext });
            Assert.Matches("^[0-9a-f]{16}$", entry.Revision);
            Assert.Equal(entry.Placeholders.Order(StringComparer.Ordinal), entry.Placeholders);
        });

        var manifestPairs = entries
            .Select(item => (item.Source, item.Context))
            .ToHashSet();
        var iosFiles = Directory.GetFiles(
            Path.Combine(root, "Legend-ios", "Legend"), "*.swift", SearchOption.AllDirectories);
        var androidFiles = Directory.GetFiles(
            Path.Combine(root, "Legend-Android", "app", "src", "main", "java"), "*.kt", SearchOption.AllDirectories);
        var ios = string.Join('\n', iosFiles.Select(File.ReadAllText));
        var android = string.Join('\n', androidFiles.Select(File.ReadAllText));
        var sharedServer = string.Join('\n', new[] { "Domain", "Infrastructure", "AgentPortal/Mobile" }
            .SelectMany(relative => Directory.GetFiles(
                Path.Combine(root, relative), "*.cs", SearchOption.AllDirectories))
            .Select(File.ReadAllText));

        Assert.DoesNotMatch(SwiftDirectVisualLiteral(), ios);
        Assert.DoesNotMatch(SwiftDirectAccessibilityLiteral(), ios);
        Assert.DoesNotMatch(SwiftConditionalPresentationLiteral(), ios);
        Assert.DoesNotMatch(AndroidDirectTextLiteral(), android);
        Assert.DoesNotMatch(AndroidDirectAccessibilityLiteral(), android);
        Assert.DoesNotMatch(AndroidConditionalPresentationLiteral(), android);
        Assert.DoesNotContain("AzureTranslator", ios, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AzureTranslator", android, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cognitive.microsofttranslator", ios, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cognitive.microsofttranslator", android, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(NativeHaitianSpecialCase(), ios);
        Assert.DoesNotMatch(NativeHaitianSpecialCase(), android);

        AssertWrappedLiteralsExist(ios, SwiftWrappedLiteral(), manifestPairs);
        AssertWrappedLiteralsExist(android, AndroidWrappedLiteral(), manifestPairs);
        AssertWrappedLiteralsExist(sharedServer, SharedServerCopyLiteral(), manifestPairs);
        Assert.Contains(("Assets", VisualContext), manifestPairs);
        Assert.Contains(("Data needing attention", VisualContext), manifestPairs);
        Assert.Contains(("Personal Property", VisualContext), manifestPairs);
        Assert.Contains(("Legend® Ai", VisualContext), manifestPairs);
        Assert.Contains(("OpenAI", VisualContext), manifestPairs);

        var gradle = File.ReadAllText(Path.Combine(root, "Legend-Android", "app", "build.gradle.kts"));
        var xcode = File.ReadAllText(Path.Combine(root, "Legend-ios", "Legend.xcodeproj", "project.pbxproj"));
        Assert.Contains("legend-application-copy.json", gradle, StringComparison.Ordinal);
        Assert.Contains("legend-application-copy.json", xcode, StringComparison.Ordinal);
    }

    [Fact]
    public void BothNativeApps_UseCanonicalPreference_OnStartupRestoreChangeAndOfflineFallback()
    {
        var root = FindRepositoryRoot();
        var iosRoot = File.ReadAllText(Path.Combine(
            root, "Legend-ios", "Legend", "Features", "Root", "RootView.swift"));
        var iosLocalization = File.ReadAllText(Path.Combine(
            root, "Legend-ios", "Legend", "DesignSystem", "NextGen", "LegendSharedDesign.swift"));
        var iosAccount = File.ReadAllText(Path.Combine(
            root, "Legend-ios", "Legend", "Features", "Account", "MobileAccountStore.swift"));
        var androidRoot = File.ReadAllText(Path.Combine(
            root, "Legend-Android", "app", "src", "main", "java", "com", "mylegnd", "legend", "registered", "ui", "LegendApp.kt"));
        var androidLocalization = File.ReadAllText(Path.Combine(
            root, "Legend-Android", "app", "src", "main", "java", "com", "mylegnd", "legend", "registered", "core", "design", "LegendApplicationLocalization.kt"));
        var androidSession = File.ReadAllText(Path.Combine(
            root, "Legend-Android", "app", "src", "main", "java", "com", "mylegnd", "legend", "registered", "core", "session", "LegendSession.kt"));

        Assert.Contains("session.preferredLanguageCode", iosLocalization, StringComparison.Ordinal);
        Assert.Contains("launchCache.readPayload(.localization", iosLocalization, StringComparison.Ordinal);
        Assert.Contains("applicationLocalizationCatalog", iosLocalization, StringComparison.Ordinal);
        Assert.Contains("installSource(actorKey:", iosLocalization, StringComparison.Ordinal);
        Assert.Contains("legendPreferredLanguageDidChange", iosAccount, StringComparison.Ordinal);
        Assert.Contains("localization.revision", iosRoot, StringComparison.Ordinal);

        Assert.Contains("preferredLanguageCode", androidSession, StringComparison.Ordinal);
        Assert.Contains("cache.localizationCatalog(actorKey)", androidLocalization, StringComparison.Ordinal);
        Assert.Contains("repository.catalog(participantType)", androidLocalization, StringComparison.Ordinal);
        Assert.Contains("installSource(actorKey)", androidLocalization, StringComparison.Ordinal);
        Assert.Contains("localization.refresh", androidRoot, StringComparison.Ordinal);
        Assert.Contains("localization.revision", androidRoot, StringComparison.Ordinal);
        Assert.Contains("Text(legendLocalized(message)", File.ReadAllText(Path.Combine(
            root, "Legend-Android", "app", "src", "main", "java", "com", "mylegnd", "legend", "registered", "ui", "LegendComponents.kt")), StringComparison.Ordinal);
    }

    private static void AssertWrappedLiteralsExist(
        string source,
        Regex regex,
        HashSet<(string Source, string Context)> manifestPairs)
    {
        foreach (Match match in regex.Matches(source))
        {
            var text = JsonSerializer.Deserialize<string>(match.Groups["source"].Value)!;
            if (!text.Any(char.IsLetter))
                continue;
            var context = match.Groups["context"].Success
                ? JsonSerializer.Deserialize<string>(match.Groups["context"].Value)!
                : VisualContext;
            Assert.Contains((text, context), manifestPairs);
        }
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("GITHUB_WORKSPACE"),
            Path.GetDirectoryName(Path.GetDirectoryName(sourceFilePath)),
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };
        foreach (var candidate in candidates.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var directory = new DirectoryInfo(candidate!);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MASTERAPP.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new DirectoryNotFoundException("Unable to locate the MASTERAPP repository root.");
    }

    [GeneratedRegex("\\b(?:Text|Button|Label|TextField|SecureField|ProgressView|Menu|Picker|Toggle|Section)\\(\\s*\"[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex SwiftDirectVisualLiteral();

    [GeneratedRegex("\\.accessibility(?:Label|Hint)\\(\\s*\"[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex SwiftDirectAccessibilityLiteral();

    [GeneratedRegex("\\bText\\(\\s*(?:(?!LegendLocalized).){0,240}(?:\\?|\\?\\?)\\s*\"[A-Za-z]", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SwiftConditionalPresentationLiteral();

    [GeneratedRegex("\\bText\\(\\s*\"[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex AndroidDirectTextLiteral();

    [GeneratedRegex("(?:contentDescription\\s*=|\\b(?:Icon|Image)\\s*\\([^\\n,]+,)\\s*\"[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex AndroidDirectAccessibilityLiteral();

    [GeneratedRegex("\\b(?:Text|Icon|LegendEmptyState|LegendPrimaryButton)\\(\\s*(?:(?!legendLocalized).){0,260}\\bif\\s*\\([^)]*\\)\\s*\"[A-Za-z]", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex AndroidConditionalPresentationLiteral();

    [GeneratedRegex("(?:case\\s+\"ht\"|==\\s*\"ht\"|\"ht\"\\s*->)", RegexOptions.CultureInvariant)]
    private static partial Regex NativeHaitianSpecialCase();

    [GeneratedRegex("LegendLocalized\\(\\s*(?<source>\"(?:\\\\.|[^\"\\\\])*\")(?:\\s*,\\s*context:\\s*(?<context>\"(?:\\\\.|[^\"\\\\])*\"))?", RegexOptions.CultureInvariant)]
    private static partial Regex SwiftWrappedLiteral();

    [GeneratedRegex("legendLocalized\\(\\s*(?<source>\"(?:\\\\.|[^\"\\\\])*\")(?:\\s*,\\s*(?<context>\"(?:\\\\.|[^\"\\\\])*\"))?", RegexOptions.CultureInvariant)]
    private static partial Regex AndroidWrappedLiteral();

    [GeneratedRegex("ApplicationCopyText\\.Source\\(\\s*(?<source>\"(?:\\\\.|[^\"\\\\])*\")", RegexOptions.CultureInvariant)]
    private static partial Regex SharedServerCopyLiteral();
}
