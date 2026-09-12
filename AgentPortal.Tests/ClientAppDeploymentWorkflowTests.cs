using System;
using System.IO;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ClientAppDeploymentWorkflowTests
{
    [Fact]
    public void MigrationReusesCandidateBoundStartupBinariesWithoutWeakeningReleaseGates()
    {
        var workflow = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "agentportal-production-deploy.yml"));
        var build = workflow[workflow.IndexOf("  build:", StringComparison.Ordinal)..workflow.IndexOf("  merge:", StringComparison.Ordinal)];
        var merge = workflow[workflow.IndexOf("  merge:", StringComparison.Ordinal)..workflow.IndexOf("  migrate:", StringComparison.Ordinal)];
        var migrate = workflow[workflow.IndexOf("  migrate:", StringComparison.Ordinal)..workflow.IndexOf("  deploy:", StringComparison.Ordinal)];
        var proof = workflow[workflow.IndexOf("  verify-legend-native-sql:", StringComparison.Ordinal)..];
        Assert.Contains("needs: security", build, StringComparison.Ordinal);
        Assert.Contains("- security", merge, StringComparison.Ordinal);
        Assert.Contains("- build", merge, StringComparison.Ordinal);
        Assert.Contains("Prove merged tree equals validated tree", merge, StringComparison.Ordinal);
        Assert.Contains("- build", migrate, StringComparison.Ordinal);
        Assert.Contains("- merge", migrate, StringComparison.Ordinal);
        Assert.Contains("name: Production", migrate, StringComparison.Ordinal);
        Assert.Contains("MIGRATION_ARTIFACT_NAME: agentportal-migration-binaries-${{ github.run_id }}", workflow, StringComparison.Ordinal);
        Assert.Contains("name: ${{ env.MIGRATION_ARTIFACT_NAME }}", build, StringComparison.Ordinal);
        Assert.Contains("name: ${{ env.MIGRATION_ARTIFACT_NAME }}", migrate, StringComparison.Ordinal);
        Assert.Contains("path: ${{ steps.migration-binaries.outputs.path }}", build, StringComparison.Ordinal);
        Assert.Contains("if ($builtHash -ne $publishedHash)", build, StringComparison.Ordinal);
        foreach (var section in new[] { build, migrate })
        {
            Assert.Contains("@('AgentPortal.dll', 'Infrastructure.dll', 'Domain.dll', 'Shared.dll')", section, StringComparison.Ordinal);
            Assert.Contains("-getProperty:TargetDir -property:Configuration=Release", section, StringComparison.Ordinal);
            Assert.Contains("_migration-provenance.json", section, StringComparison.Ordinal);
        }
        Assert.Contains("$manifest.CandidateSha -ne '${{ needs.build.outputs.candidate_sha }}'", migrate, StringComparison.Ordinal);
        Assert.Contains("$manifest.TestedTree -ne '${{ needs.build.outputs.tested_tree }}'", migrate, StringComparison.Ordinal);
        Assert.Contains("$manifest.AssemblySha256.$assembly", migrate, StringComparison.Ordinal);
        Assert.True(migrate.IndexOf("Verify and restore immutable EF startup binaries", StringComparison.Ordinal) <
            migrate.IndexOf("Login to Azure with OIDC", StringComparison.Ordinal));
        Assert.Contains("-Recurse -Force -ErrorAction Stop", migrate, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet build", migrate, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet publish", migrate, StringComparison.Ordinal);
        Assert.Contains("Apply production EF migrations", migrate, StringComparison.Ordinal);
        Assert.Contains("Verify zero pending production migrations", migrate, StringComparison.Ordinal);
        Assert.Contains("--configuration Release --no-build", migrate, StringComparison.Ordinal);
        Assert.Contains("LEGEND_PRODUCTION_PROOF_REQUIRED: 'true'", proof, StringComparison.Ordinal);
        Assert.Contains("if ($matrixStatus -ne 'passed')", proof, StringComparison.Ordinal);
        Assert.Contains("if ($validationErrors.Count -gt 0) { throw", proof, StringComparison.Ordinal);
        Assert.DoesNotContain("legend-baseline-regression.py", proof, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionReleasesShareOneNonCancellingConcurrencyGroup()
    {
        var workflow = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "agentportal-production-deploy.yml"));
        var start = workflow.IndexOf("\nconcurrency:", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = workflow.IndexOf("\npermissions:", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var concurrency = workflow[start..end];
        Assert.Contains("group: agentportal-production\n", concurrency, StringComparison.Ordinal);
        Assert.Contains("cancel-in-progress: false", concurrency, StringComparison.Ordinal);
        Assert.DoesNotContain("${{", concurrency, StringComparison.Ordinal);
    }

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
