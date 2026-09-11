using System;
using System.IO;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ClientAppDeploymentWorkflowTests
{
    [Fact]
    public void BothHostsUseOneValidatedBuildAndMigrationGateWithMatchedSharedAssemblies()
    {
        var workflow = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "agentportal-production-deploy.yml"));
        var build = workflow[workflow.IndexOf("  build:", StringComparison.Ordinal)..workflow.IndexOf("  merge:", StringComparison.Ordinal)];
        var deploy = workflow[workflow.IndexOf("  deploy:", StringComparison.Ordinal)..workflow.IndexOf("  verify-legend-native:", StringComparison.Ordinal)];
        Assert.Contains("needs: security", build, StringComparison.Ordinal);
        Assert.Contains("Publish ClientApp from the same validated checkout", build, StringComparison.Ordinal);
        Assert.Contains("@('Infrastructure.dll', 'Domain.dll')", build, StringComparison.Ordinal);
        Assert.Contains("if ($portalHash -ne $clientHash)", build, StringComparison.Ordinal);
        Assert.Contains("- migrate", deploy, StringComparison.Ordinal);
        Assert.Contains("name: Production", deploy, StringComparison.Ordinal);
        Assert.Contains("id-token: write", deploy, StringComparison.Ordinal);
        Assert.Contains("$manifest.CandidateSha -ne '${{ needs.build.outputs.candidate_sha }}'", deploy, StringComparison.Ordinal);
        Assert.Contains("$manifest.TestedTree -ne '${{ needs.build.outputs.tested_tree }}'", deploy, StringComparison.Ordinal);
        Assert.Contains("$clientHash -ne $manifest.SharedAssemblySha256.$assembly", deploy, StringComparison.Ordinal);
        Assert.Contains("package: ./package", deploy, StringComparison.Ordinal);
        Assert.Contains("package: ./client-package", deploy, StringComparison.Ordinal);
        Assert.True(deploy.IndexOf("Verify downloaded ClientApp provenance", StringComparison.Ordinal) <
            deploy.IndexOf("Deploy immutable merged production tree", StringComparison.Ordinal));
        Assert.True(deploy.IndexOf("Deploy ClientApp from the same immutable", StringComparison.Ordinal) <
            deploy.IndexOf("id: identity", StringComparison.Ordinal));
        Assert.DoesNotContain("dotnet publish", deploy, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/checkout", deploy, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientSmokeIsNonMutatingAndDoesNotClaimAuthenticatedContentProof()
    {
        var workflow = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "agentportal-production-deploy.yml"));
        var start = workflow.IndexOf("      - name: Verify ClientApp shared-content authentication routing", StringComparison.Ordinal);
        var end = workflow.IndexOf("      - name: Bind deployed artifact to immutable production SHA", start, StringComparison.Ordinal);
        var smoke = workflow[start..end];
        Assert.Contains("--request GET", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("--request POST", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("--location", smoke, StringComparison.Ordinal);
        Assert.Contains("--output NUL", smoke, StringComparison.Ordinal);
        Assert.Contains("$status -ne '302'", smoke, StringComparison.Ordinal);
        Assert.Contains("AuthenticatedContentVerified = $false", smoke, StringComparison.Ordinal);
        Assert.Contains("DeployedSha = '${{ needs.merge.outputs.merge_sha }}'", smoke, StringComparison.Ordinal);
        Assert.Contains("path: ./client-receipt/deployment.json", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.", smoke, StringComparison.Ordinal);
        // The previous portal ingress smoke assertions remain part of the same release.
        Assert.Contains("/api/v1/mobile/social/posts/media/stage' -ExpectedStatus '401'", workflow, StringComparison.Ordinal);
    }
}
