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
            "Legend-Android", "app", "src", "main", "res", "mipmap-anydpi",
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
        Assert.Contains(
            "<monochrome android:drawable=\"@drawable/ic_legend_notification\" />",
            adaptiveIcon,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidPush_UsesFirebaseInstallationRegistrationWithoutDeprecatedTokenApis()
    {
        var manifest = Read("Legend-Android", "app", "src", "main", "AndroidManifest.xml");
        var coordinator = Read(
            "Legend-Android", "app", "src", "main", "java", "com", "mylegnd", "legend",
            "registered", "core", "push", "FcmPushRegistrationCoordinator.kt");
        var service = Read(
            "Legend-Android", "app", "src", "main", "java", "com", "mylegnd", "legend",
            "registered", "core", "push", "LegendFirebaseMessagingService.kt");

        Assert.Contains("firebase_messaging_installation_id_enabled", manifest, StringComparison.Ordinal);
        Assert.Contains("FirebaseMessaging.getInstance().register()", coordinator, StringComparison.Ordinal);
        Assert.Contains("FirebaseMessaging.getInstance().unregister()", coordinator, StringComparison.Ordinal);
        Assert.Contains("FirebaseInstallations.getInstance().id", coordinator, StringComparison.Ordinal);
        Assert.Contains("override fun onRegistered(installationId: String)", service, StringComparison.Ordinal);
        Assert.DoesNotContain(".deleteToken()", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain(".token.awaitResult()", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("override fun onNewToken", service, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileSignIn_UsesOneSecureActionForStandardAndProvidedCredentials()
    {
        var ios = Read("Legend-ios", "Legend", "Features", "Root", "RootView.swift");
        var android = Read(
            "Legend-Android", "app", "src", "main", "java", "com", "mylegnd", "legend",
            "registered", "ui", "LegendApp.kt");

        var iosSignIn = Between(
            ios,
            "private struct SignInView: View",
            "private struct SessionFailureView: View");
        Assert.Contains("Button(\"Sign in securely\", action: signIn)", iosSignIn, StringComparison.Ordinal);
        Assert.Contains("session.signInForAppReview(", iosSignIn, StringComparison.Ordinal);
        Assert.Contains("session.signIn()", iosSignIn, StringComparison.Ordinal);
        Assert.Contains("ScrollView", iosSignIn, StringComparison.Ordinal);
        Assert.Contains("Were you given sign-in credentials?", iosSignIn, StringComparison.Ordinal);
        Assert.Contains("showsProvidedCredentials && hasCompleteProvidedCredentials", iosSignIn, StringComparison.Ordinal);
        Assert.DoesNotContain("App Review Sign In", iosSignIn, StringComparison.Ordinal);
        Assert.DoesNotContain("AppReviewSignInView", iosSignIn, StringComparison.Ordinal);
        Assert.DoesNotContain(".sheet(", iosSignIn, StringComparison.Ordinal);

        var androidSignIn = Between(
            android,
            "private fun SignInScreen(",
            "private fun RoleSelectionScreen(");
        Assert.Contains("LegendPrimaryButton(\n                \"Sign in securely\"", androidSignIn, StringComparison.Ordinal);
        Assert.Contains(
            "onAppReviewSignIn(normalizedUsername, submittedPassword)",
            androidSignIn,
            StringComparison.Ordinal);
        Assert.Contains("activity?.let(onSignIn)", androidSignIn, StringComparison.Ordinal);
        Assert.Contains(".imePadding()", androidSignIn, StringComparison.Ordinal);
        Assert.Contains("Were you given sign-in credentials?", androidSignIn, StringComparison.Ordinal);
        Assert.Contains(
            "showsProvidedCredentials && hasCompleteProvidedCredentials",
            androidSignIn,
            StringComparison.Ordinal);
        Assert.DoesNotContain("App Review Sign In", androidSignIn, StringComparison.Ordinal);
        Assert.DoesNotContain("AppReviewSignInDialog", androidSignIn, StringComparison.Ordinal);
        Assert.DoesNotContain("AlertDialog(", androidSignIn, StringComparison.Ordinal);
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
