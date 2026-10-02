using System;
using System.IO;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ClientAppDeploymentWorkflowTests
{
    private static string DirectRelease() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "all-intentional-direct-release-20260918.yml"));

    private static string SecurityValidation() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "approved-release-security-validation.yml"));

    [Fact]
    public void ApprovedDirectRelease_IsTheOnlyApplicationDeploymentAuthority()
    {
        var workflow = DirectRelease();
        Assert.Contains("name: LEGEND approved direct release", workflow, StringComparison.Ordinal);
        Assert.Contains("github.ref == 'refs/heads/legend/approved-changes'", workflow, StringComparison.Ordinal);
        Assert.Contains("Preserve targets already live at exact candidate", workflow, StringComparison.Ordinal);
        Assert.Contains("Verify every deployed target and collect all failures", workflow, StringComparison.Ordinal);
        Assert.Contains("Enforce complete direct deployment outcome", workflow, StringComparison.Ordinal);
        Assert.Contains("cancel-in-progress: false", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectedTargetsPublishThroughOneCanonicalTransaction()
    {
        var workflow = DirectRelease();
        Assert.Contains("Publish selected head as one transaction", workflow, StringComparison.Ordinal);
        Assert.Contains("--targets-json \"$SELECTED_TARGETS\"", workflow, StringComparison.Ordinal);
        Assert.Contains("--baselines-json \"$LIVE_BASELINES\"", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Direct deploy AgentPortal", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Direct deploy ClientApp", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Direct deploy Protect immutable ZIP", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Direct deploy Parfait", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Direct deploy Website immutable ZIP", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasePackagesAllSelectedDotnetAppsFromOneApprovedCheckout()
    {
        var workflow = DirectRelease();
        Assert.Contains("Load exact preserved deployable package", workflow, StringComparison.Ordinal);
        Assert.Contains("Publish exact selected application packages", workflow, StringComparison.Ordinal);
        Assert.Contains("scripts/release-package.py verify", workflow, StringComparison.Ordinal);
        Assert.Contains("production rebuild is forbidden", workflow, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet publish AgentPortal/AgentPortal.csproj", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet publish ClientApp/ClientApp.csproj", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet publish Protect-Website/ProtectWebsite.csproj", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet publish ParfaitApp/ParfaitApp.csproj", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationGateRunsBeforeTheApplicationTransaction()
    {
        var workflow = DirectRelease();
        var migration = workflow.IndexOf("Apply additive diagnostics migrations before restarting apps", StringComparison.Ordinal);
        var transaction = workflow.IndexOf("Publish selected head as one transaction", StringComparison.Ordinal);
        Assert.True(migration >= 0);
        Assert.True(transaction > migration);
        Assert.Contains("steps.migrate.outcome == 'success' || steps.migrate.outcome == 'skipped'", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticSecurityChecksAreValidationOnlyAndCannotDeploy()
    {
        var workflow = SecurityValidation();
        Assert.Contains("name: LEGEND approved release security validation", workflow, StringComparison.Ordinal);
        Assert.Contains("branches: [legend/approved-changes]", workflow, StringComparison.Ordinal);
        Assert.Contains("Validate database migration artifacts", workflow, StringComparison.Ordinal);
        Assert.Contains("Audit dependency vulnerabilities", workflow, StringComparison.Ordinal);
        Assert.Contains("Scan committed configuration for secrets", workflow, StringComparison.Ordinal);
        Assert.Contains("Verify shared composition authorities", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("webapps-deploy", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet-ef database update", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectReleaseUsesOneTransactionalImmutableZipTransport()
    {
        var workflow = DirectRelease();
        Assert.Contains("python3 scripts/deploy-approved-app.py", workflow, StringComparison.Ordinal);
        Assert.Contains("--rollback-root /tmp/rollback-packages", workflow, StringComparison.Ordinal);
        Assert.Contains("Restore complete application baseline after downstream release failure", workflow, StringComparison.Ordinal);
        Assert.Contains("--rollback-only", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("python3 scripts/deploy-approved-app.py --target", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("azure/webapps-deploy@v3", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishedTargetsAreProvenByRuntimeProvenanceNotBranchPromotion()
    {
        var workflow = DirectRelease();
        Assert.Contains("scripts/validation-resume.py live-state", workflow, StringComparison.Ordinal);
        Assert.Contains("scripts/validation-resume.py verify-live", workflow, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_RELEASE_SHA", workflow, StringComparison.Ordinal);
        Assert.Contains("target-release-receipts", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("refs/heads/production", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("base=production", workflow, StringComparison.Ordinal);
    }
}
