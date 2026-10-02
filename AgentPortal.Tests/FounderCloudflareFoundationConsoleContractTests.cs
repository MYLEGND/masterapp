using System.Reflection;
using AgentPortal.Controllers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentPortal.Tests;

public sealed class FounderCloudflareFoundationConsoleContractTests
{
    [Fact]
    public void FounderLegendConnect_ExposesOneCanonicalCloudflareConsoleWithoutBrowserSecrets()
    {
        var root = SourceRoot();
        var layout = Read(root, "AgentPortal", "Views", "Shared", "_Layout.cshtml");
        var view = Read(root, "AgentPortal", "Views", "LegendConnect", "Index.cshtml");
        var script = Read(root, "AgentPortal", "wwwroot", "js", "legend-connect.js");
        var model = Read(root, "AgentPortal", "Models", "LegendConnectViewModels.cs");

        Assert.Contains("asp-controller=\"LegendConnect\"", layout, StringComparison.Ordinal);
        Assert.Contains("Cloudflare Foundation Console", view, StringComparison.Ordinal);
        Assert.Contains("Run no-inference connection check", view, StringComparison.Ordinal);
        Assert.Contains("data-cf-control=\"set_spend_cap\"", view, StringComparison.Ordinal);
        Assert.Contains("data-cf-control=\"pause\"", view, StringComparison.Ordinal);
        Assert.Contains("data-cf-control=\"resume\"", view, StringComparison.Ordinal);
        Assert.Contains("/founder/legend-connect/cloudflare/status", script, StringComparison.Ordinal);
        Assert.Contains("/founder/legend-connect/cloudflare/control", script, StringComparison.Ordinal);
        Assert.Contains("/founder/legend-connect/cloudflare/canary", script, StringComparison.Ordinal);
        Assert.Contains("Cloudflare Workers AI", model, StringComparison.Ordinal);

        foreach (var source in new[] { view, script, model })
        {
            Assert.DoesNotContain("CLOUDFLARE_API_TOKEN", source, StringComparison.Ordinal);
            Assert.DoesNotContain("LEGEND_SERVICE_KEYS_JSON", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Cloudflare:SigningKey", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CloudflareControls_AreFounderPostAntiforgeryAndStatusIsReadOnlyGet()
    {
        var type = typeof(LegendConnectController);
        var status = type.GetMethod(nameof(LegendConnectController.GetCloudflareFoundationStatus))!;
        Assert.NotNull(status.GetCustomAttribute<HttpGetAttribute>());

        var control = type.GetMethod(nameof(LegendConnectController.UpdateCloudflareFoundationControl))!;
        Assert.NotNull(control.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(control.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());

        var canary = type.GetMethod(nameof(LegendConnectController.RunCloudflareFoundationInferenceCanary))!;
        Assert.NotNull(canary.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(canary.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

    [Fact]
    public void CloudflareControlPlane_ReusesSignedFoundationAuthorityAndBoundsSpend()
    {
        var root = SourceRoot();
        var service = Read(root, "AgentPortal", "Services", "FounderLegendConnectService.cs");
        var worker = Read(root, "Legend-Cloudflare", "src", "index.mjs");
        var governance = Read(root, "Legend-Cloudflare", "src", "security", "governance.mjs");
        var canary = Read(root, "Legend-Cloudflare", "scripts", "founder-canary.mjs");

        Assert.Contains("LegendCloudflareServiceSignature.Sign", service, StringComparison.Ordinal);
        Assert.Contains("AuthenticatedRequestBinding.Resolve", service, StringComparison.Ordinal);
        Assert.Contains("FounderAuthority.Evaluate", service, StringComparison.Ordinal);
        Assert.Contains("LegendCloudflareFoundation", service, StringComparison.Ordinal);
        Assert.Contains("/v1/legend/status", service, StringComparison.Ordinal);
        Assert.Contains("/v1/legend/control", service, StringComparison.Ordinal);
        Assert.Contains("ILegendConnectModelInferenceTransport", service, StringComparison.Ordinal);
        Assert.Contains("LegendConnectExternalProviderPolicy.CloudflareFoundation", service, StringComparison.Ordinal);
        Assert.Contains("MaxOutputTokens: 32", service, StringComparison.Ordinal);
        Assert.DoesNotContain("CLOUDFLARE_API_TOKEN", service, StringComparison.Ordinal);

        Assert.Contains("STATUS_PATH", worker, StringComparison.Ordinal);
        Assert.Contains("CONTROL_PATH", worker, StringComparison.Ordinal);
        Assert.Contains("policyPersistence: 'persistent'", worker, StringComparison.Ordinal);
        Assert.Contains("governance.status()", worker, StringComparison.Ordinal);
        Assert.Contains("governance.control(control)", worker, StringComparison.Ordinal);

        Assert.Contains("founder_inference_paused", governance, StringComparison.Ordinal);
        Assert.Contains("founder_spend_cap_invalid", governance, StringComparison.Ordinal);
        Assert.Contains("Math.min(policy.accountMicrousd, founderControl.spendCapMicrousd)", governance, StringComparison.Ordinal);
        Assert.Contains("remainingMicrousd", governance, StringComparison.Ordinal);

        Assert.Contains("controlPlaneVerified: true", canary, StringComparison.Ordinal);
        Assert.Contains("/v1/legend/status", canary, StringComparison.Ordinal);
        Assert.Contains("FOUNDER_BASELINE_MODEL_IDS", canary, StringComparison.Ordinal);
    }

    private static string Read(string root, params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string SourceRoot()
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace) &&
            File.Exists(Path.Combine(workspace, "MASTERAPP.sln")))
            return workspace;

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null &&
                   !File.Exists(Path.Combine(directory.FullName, "MASTERAPP.sln")))
                directory = directory.Parent;
            if (directory is not null)
                return directory.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
