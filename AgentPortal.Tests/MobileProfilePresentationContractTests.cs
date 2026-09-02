using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileProfilePresentationContractTests
{
    [Fact]
    public void FinancialOutlookPresentation_IsOwnedAboveTheSwipePager()
    {
        var ios = Read("Legend-ios", "Legend", "Features", "Home", "LegendApplicationShell.swift");
        var android = Read(
            "Legend-Android", "app", "src", "main", "java", "com", "mylegnd", "legend",
            "registered", "ui", "LegendApp.kt");

        Assert.Contains(
            "@State private var selectedFinancialOutlook: LegendFinancialOutlookSelection?",
            ios,
            StringComparison.Ordinal);
        Assert.Contains(".sheet(item: $selectedFinancialOutlook)", ios, StringComparison.Ordinal);
        Assert.Contains("openOutlook: { selectedFinancialOutlook = $0 }", ios, StringComparison.Ordinal);
        Assert.DoesNotContain("@State private var selectedOutlook", ios, StringComparison.Ordinal);

        Assert.Contains(
            "var financialOutlook by remember { mutableStateOf<FinancialOutlookSelection?>(null) }",
            android,
            StringComparison.Ordinal);
        Assert.Contains("isActive = profilePagerState.currentPage == 1", android, StringComparison.Ordinal);
        Assert.DoesNotContain("var financial by remember", android, StringComparison.Ordinal);
        Assert.DoesNotContain("LaunchedEffect(financial)", android, StringComparison.Ordinal);

        var financialScreen = Between(
            android,
            "private fun FinancialScreen(",
            "private fun FinancialCashFlowLanding(");
        Assert.Contains("openOutlook: (FinancialOutlookSelection) -> Unit", financialScreen, StringComparison.Ordinal);
        Assert.DoesNotContain("outlookDetail", financialScreen, StringComparison.Ordinal);
        Assert.DoesNotContain("FinancialOutlookDialog(", financialScreen, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneralPublicSettings_ExcludeOperationalDiagnosticsAndDuplicateFinanceEntry()
    {
        var ios = Read("Legend-ios", "Legend", "Features", "Home", "LegendApplicationShell.swift");
        var android = Read(
            "Legend-Android", "app", "src", "main", "java", "com", "mylegnd", "legend",
            "registered", "ui", "LegendApp.kt");

        var iosSettings = Between(
            ios,
            "private func profileSettingsSheet(_ profile: MobileAccountProfile)",
            "private func submitControlledResourceRequest(");
        var iosPublicProfile = Between(
            iosSettings,
            "LegendProfileSettingsSection(title: \"Profile\")",
            "if currentSession.actor.identity.participantType == .agent");
        var iosFounderDiagnostics = Between(
            iosSettings,
            "LegendProfileSettingsSection(title: \"Founder diagnostics\")",
            "if currentSession.capabilities.contains(\"scripture-management\")");
        var iosPublicSecurity = Between(
            iosSettings,
            "LegendProfileSettingsSection(title: \"Security\")",
            "LegendProfileSettingsSection(title: \"Account access\")");

        Assert.Contains("Edit profile", iosPublicProfile, StringComparison.Ordinal);
        Assert.DoesNotContain("Creator insights", iosPublicProfile, StringComparison.Ordinal);
        Assert.DoesNotContain("Refresh profile", iosPublicProfile, StringComparison.Ordinal);
        Assert.Contains("Push notification status", iosFounderDiagnostics, StringComparison.Ordinal);
        Assert.Contains("Security checkpoint", iosFounderDiagnostics, StringComparison.Ordinal);
        Assert.Contains("Token storage", iosFounderDiagnostics, StringComparison.Ordinal);
        Assert.Contains("Face ID", iosPublicSecurity, StringComparison.Ordinal);
        Assert.DoesNotContain("Push notification status", iosPublicSecurity, StringComparison.Ordinal);
        Assert.DoesNotContain("Security checkpoint", iosPublicSecurity, StringComparison.Ordinal);
        Assert.DoesNotContain("Token storage", iosPublicSecurity, StringComparison.Ordinal);

        var androidSettings = Between(
            android,
            "private fun LegendAccountSettingsSheet(",
            "/** The same server-projected creator intelligence");
        Assert.DoesNotContain(
            "AccountSettingsRow(\"Financial Intelligence\"",
            androidSettings,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Security checkpoint", androidSettings, StringComparison.Ordinal);
        Assert.Contains(
            "if (isFounder) item { AccountSettingsRow(\"Creator insights\"",
            androidSettings,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidChrome_UsesAdaptiveArtworkAndSafeResponsiveInsets()
    {
        var android = Read(
            "Legend-Android", "app", "src", "main", "java", "com", "mylegnd", "legend",
            "registered", "ui", "LegendApp.kt");
        var founderAi = Read(
            "Legend-Android", "app", "src", "main", "java", "com", "mylegnd", "legend",
            "registered", "ui", "LegendFounderAiConversation.kt");
        var manifest = Read("Legend-Android", "app", "src", "main", "AndroidManifest.xml");
        var adaptiveIcon = Read(
            "Legend-Android", "app", "src", "main", "res", "mipmap-anydpi-v26",
            "ic_legend_launcher.xml");

        Assert.Contains(".navigationBarsPadding()", android, StringComparison.Ordinal);
        Assert.Contains(".imePadding()", android, StringComparison.Ordinal);
        Assert.Contains("contentWindowInsets = {", android, StringComparison.Ordinal);
        Assert.Contains(".consumeWindowInsets(padding)", android, StringComparison.Ordinal);
        Assert.Contains("isAppearanceLightStatusBars", android, StringComparison.Ordinal);
        Assert.Contains("modifier = Modifier.align(Alignment.Center)", android, StringComparison.Ordinal);
        var sharedBrandArtwork = Between(
            android,
            "private fun LegendBrandArtwork(",
            "private fun RoleSelectionScreen(");
        Assert.Contains("ContentScale.Crop", sharedBrandArtwork, StringComparison.Ordinal);
        Assert.Contains(".clip(CircleShape)", sharedBrandArtwork, StringComparison.Ordinal);
        Assert.Contains(
            "FounderAiMark(LegendSize.MinimumTapTarget)",
            founderAi,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FounderAiMark(LegendSize.CompactControlHeight - LegendSpacing.Tiny)",
            founderAi,
            StringComparison.Ordinal);

        Assert.Contains(
            "android:icon=\"@mipmap/ic_legend_launcher\"",
            manifest,
            StringComparison.Ordinal);
        Assert.Contains(
            "android:roundIcon=\"@mipmap/ic_legend_launcher\"",
            manifest,
            StringComparison.Ordinal);
        Assert.Contains(
            "android:resource=\"@drawable/ic_legend_notification\"",
            manifest,
            StringComparison.Ordinal);
        Assert.Contains(
            "android:drawable=\"@drawable/ic_legend_launcher_foreground\"",
            adaptiveIcon,
            StringComparison.Ordinal);
    }

    private static string Between(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing start marker: {start}");
        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Missing end marker: {end}");
        return source[startIndex..endIndex];
    }

    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine([RepoRoot, .. path]));

    private static string RepoRoot => ResolveRepoRoot();

    private static string ResolveRepoRoot([CallerFilePath] string sourceFile = "")
    {
        var candidates = new List<string> { Directory.GetCurrentDirectory() };
        var sourceDirectory = Path.GetDirectoryName(sourceFile);
        if (!string.IsNullOrWhiteSpace(sourceDirectory))
        {
            candidates.Add(Path.Combine(sourceDirectory, ".."));
        }

        return candidates
            .Select(Path.GetFullPath)
            .First(candidate => File.Exists(Path.Combine(candidate, "MASTERAPP.sln")));
    }
}
