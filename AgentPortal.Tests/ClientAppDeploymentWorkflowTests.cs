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
        Assert.Contains("branches: [legend/approved-changes]", workflow, StringComparison.Ordinal);
        Assert.Contains("Preserve targets already live at exact candidate", workflow, StringComparison.Ordinal);
        Assert.Contains("Verify every deployed target and collect all failures", workflow, StringComparison.Ordinal);
        Assert.Contains("Enforce complete direct deployment outcome", workflow, StringComparison.Ordinal);
        Assert.Contains("cancel-in-progress: false", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientAndPortalRemainIndependentTargetsInsideOneReleaseAuthority()
    {
        var workflow = DirectRelease();
        Assert.Contains("Direct deploy AgentPortal", workflow, StringComparison.Ordinal);
        Assert.Contains("Direct deploy ClientApp", workflow, StringComparison.Ordinal);
        Assert.Contains("contains(fromJSON(env.SELECTED_TARGETS), 'masterapp-portal')", workflow, StringComparison.Ordinal);
        Assert.Contains("contains(fromJSON(env.SELECTED_TARGETS), 'masterapp-client')", workflow, StringComparison.Ordinal);
        Assert.Contains("steps.resumestate.outputs.portal_live != 'true'", workflow, StringComparison.Ordinal);
        Assert.Contains("steps.resumestate.outputs.client_live != 'true'", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasePackagesAllSelectedDotnetAppsFromOneApprovedCheckout()
    {
        var workflow = DirectRelease();
        Assert.Contains("Publish exact selected application packages", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet publish AgentPortal/AgentPortal.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet publish ClientApp/ClientApp.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet publish Protect-Website/ProtectWebsite.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet publish ParfaitApp/ParfaitApp.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("SourceRevisionId=\"$APPLICATION_RELEASE_SHA\"", workflow, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationGateRunsBeforeAnyDatabaseDependentDeployment()
    {
        var workflow = DirectRelease();
        var migration = workflow.IndexOf("Apply additive diagnostics migrations before restarting apps", StringComparison.Ordinal);
        var portal = workflow.IndexOf("Direct deploy AgentPortal", StringComparison.Ordinal);
        var protect = workflow.IndexOf("Direct deploy Protect immutable ZIP", StringComparison.Ordinal);
        var parfait = workflow.IndexOf("Direct deploy Parfait", StringComparison.Ordinal);
        Assert.True(migration >= 0);
        Assert.True(portal > migration);
        Assert.True(protect > migration);
        Assert.True(parfait > migration);
        Assert.Contains("steps.migrate.outcome == 'success'", workflow, StringComparison.Ordinal);
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
    public void ProtectDirectReleaseUsesOneImmutableZipTransportAndNoDirectoryDeploy()
    {
        var workflow = DirectRelease();
        Assert.Contains("Direct deploy Protect immutable ZIP", workflow, StringComparison.Ordinal);
        Assert.Contains("python3 scripts/deploy-approved-app.py --target protect", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("azure/webapps-deploy@v3", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishedTargetsAreProvenByRuntimeProvenanceNotBranchPromotion()
    {
        var workflow = DirectRelease();
        Assert.Contains("api/runtime-provenance", workflow, StringComparison.Ordinal);
        Assert.Contains("_deployment-provenance.txt", workflow, StringComparison.Ordinal);
        Assert.Contains("preservedExactLiveTargets", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("refs/heads/production", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("base=production", workflow, StringComparison.Ordinal);
    }
}
