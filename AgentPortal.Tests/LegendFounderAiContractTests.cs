using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Models;
using AgentPortal.Security;
using AgentPortal.Services;
using Azure.Core;
using Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFounderAiContractTests
{
    [Fact]
    public void Controller_IsAuthenticatedAndFounderOnly()
    {
        var type = typeof(LegendFounderAiController);

        Assert.NotNull(
            type.GetCustomAttribute<AuthorizeAttribute>());

        Assert.NotNull(
            type.GetCustomAttribute<FounderOnlyAttribute>());

        var cache =
            type.GetCustomAttribute<ResponseCacheAttribute>();

        Assert.NotNull(cache);
        Assert.True(cache!.NoStore);
    }

    [Fact]
    public void Chat_IsPostAntiforgeryProtected()
    {
        var method =
            typeof(LegendFounderAiController)
                .GetMethod(
                    nameof(LegendFounderAiController.Chat));

        Assert.NotNull(method);

        Assert.NotNull(
            method!.GetCustomAttribute<HttpPostAttribute>());

        Assert.NotNull(
            method.GetCustomAttribute<
                ValidateAntiForgeryTokenAttribute>());

        var route =
            method.GetCustomAttribute<RouteAttribute>();

        Assert.NotNull(route);
        Assert.Equal(
            "founder/legend-ai/chat",
            route!.Template);
    }

    [Fact]
    public void FounderAdapter_ExposesOnlyGovernedLearningEntryPoints()
    {
        var type = typeof(FounderLegendConnectService);

        Assert.NotNull(
            type.GetMethod(
                nameof(
                    FounderLegendConnectService
                        .QueueFounderLearningSeedAsync)));

        Assert.NotNull(
            type.GetMethod(
                nameof(
                    FounderLegendConnectService
                        .QueueFounderCurriculumAsync)));

        Assert.NotNull(
            type.GetMethod(
                nameof(
                    FounderLegendConnectService
                        .EnsureAutonomousLearningActiveAsync)));


        Assert.NotNull(
            type.GetMethod(
                nameof(
                    FounderLegendConnectService
                        .SearchRetainedKnowledgeAsync)));

        Assert.NotNull(
            type.GetMethod(
                nameof(
                    FounderLegendConnectService
                        .QueueMachineTeachingProposalAsync)));
    }

    [Fact]
    public void ConversationService_IsPresentationOrchestrationOnly()
    {
        var type =
            typeof(LegendFounderAiConversationService);

        Assert.True(type.IsSealed);

        var reply =
            type.GetMethod(
                nameof(
                    LegendFounderAiConversationService
                        .ReplyAsync));

        Assert.NotNull(reply);

        // No public mutation surface belongs on this interface.
        var publicMethods =
            type.GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Select(method => method.Name)
                .ToArray();

        Assert.Equal(
            new[]
            {
                nameof(
                    LegendFounderAiConversationService
                        .ReplyAsync)
            },
            publicMethods);
    }

    [Fact]
    public void ConversationFailure_PreservesProviderClassification()
    {
        var failure =
            LegendFounderAiChatResponse.Failure(
                "provider rejected request",
                "provider_http",
                400,
                "req_test");

        Assert.False(failure.Succeeded);
        Assert.Equal(
            "provider_http",
            failure.FailureKind);
        Assert.Equal(
            400,
            failure.ProviderStatusCode);
        Assert.Equal(
            "req_test",
            failure.Reference);
    }

    [Fact]
    public void FounderCurriculumTool_UsesClosedStrictVariationSchema()
    {
        var buildTools =
            typeof(LegendFounderAiConversationService)
                .GetMethod(
                    "BuildFounderTools",
                    BindingFlags.NonPublic |
                    BindingFlags.Static);

        Assert.NotNull(buildTools);

        var tools =
            Assert.IsAssignableFrom<
                IReadOnlyList<object>>(
                buildTools!.Invoke(
                    null,
                    null));

        using var document =
            JsonDocument.Parse(
                JsonSerializer.Serialize(
                    tools));

        var curriculum =
            document.RootElement
                .EnumerateArray()
                .Single(
                    tool =>
                        tool.TryGetProperty(
                            "name",
                            out var name) &&
                        name.GetString() ==
                            "legend_submit_founder_curriculum");

        Assert.True(
            curriculum.GetProperty("strict")
                .GetBoolean());

        var variations =
            curriculum
                .GetProperty("parameters")
                .GetProperty("properties")
                .GetProperty("families")
                .GetProperty("items")
                .GetProperty("properties")
                .GetProperty("examples")
                .GetProperty("items")
                .GetProperty("properties")
                .GetProperty("variations");

        Assert.Equal(
            "array",
            variations.GetProperty("type")
                .GetString());

        var variationItem =
            variations.GetProperty("items");

        Assert.False(
            variationItem
                .GetProperty(
                    "additionalProperties")
                .GetBoolean());

        var required =
            variationItem
                .GetProperty("required")
                .EnumerateArray()
                .Select(
                    item =>
                        item.GetString())
                .ToArray();

        Assert.Contains(
            "dimension",
            required);

        Assert.Contains(
            "value",
            required);
    }


    [Fact]
    public void FounderTools_IncludeNativeWebResearchWithoutReplacingGovernedTools()
    {
        var buildTools =
            typeof(LegendFounderAiConversationService)
                .GetMethod(
                    "BuildFounderTools",
                    BindingFlags.NonPublic |
                    BindingFlags.Static);

        Assert.NotNull(buildTools);

        var tools =
            Assert.IsAssignableFrom<IReadOnlyList<object>>(
                buildTools!.Invoke(
                    null,
                    null));

        using var document =
            JsonDocument.Parse(
                JsonSerializer.Serialize(
                    tools));

        var toolArray =
            document.RootElement
                .EnumerateArray()
                .ToArray();

        Assert.Single(
            toolArray.Where(
                tool =>
                    tool.TryGetProperty(
                        "type",
                        out var type) &&
                    type.GetString() ==
                        "web_search"));

        Assert.Contains(
            toolArray,
            tool =>
                tool.TryGetProperty(
                    "name",
                    out var name) &&
                name.GetString() ==
                    "legend_system_overview");

        Assert.Contains(
            toolArray,
            tool =>
                tool.TryGetProperty(
                    "name",
                    out var name) &&
                name.GetString() ==
                    "legend_search_retained_knowledge");

        Assert.Contains(
            toolArray,
            tool =>
                tool.TryGetProperty(
                    "name",
                    out var name) &&
                name.GetString() ==
                    "legend_submit_machine_learning_candidate");
    }

    [Fact]
    public void FounderCapabilities_AreDiscoveredFromTheExecutableToolRegistry()
    {
        var buildTools = typeof(LegendFounderAiConversationService)
            .GetMethod("BuildFounderTools", BindingFlags.NonPublic | BindingFlags.Static);
        var describe = typeof(LegendFounderAiConversationService)
            .GetMethod("DescribeFounderCapabilities", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(buildTools);
        Assert.NotNull(describe);

        var tools = Assert.IsAssignableFrom<IReadOnlyList<object>>(
            buildTools!.Invoke(null, null));
        using var toolsDocument = JsonDocument.Parse(JsonSerializer.Serialize(tools));
        var executableNames = toolsDocument.RootElement.EnumerateArray()
            .Where(tool => tool.TryGetProperty("type", out var type) &&
                type.GetString() == "function")
            .Select(tool => tool.GetProperty("name").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(System.StringComparer.Ordinal);

        var capabilities = Assert.IsAssignableFrom<IReadOnlyList<object>>(
            describe!.Invoke(null, null));
        using var capabilityDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(capabilities));
        var discovered = capabilityDocument.RootElement.EnumerateArray().ToArray();
        var discoveredNames = discovered
            .Select(item => item.GetProperty("name").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(System.StringComparer.Ordinal);

        Assert.Equal(executableNames, discoveredNames);
        Assert.Contains("legend_capabilities", discoveredNames);
        Assert.Contains("legend_operational_diagnostics", discoveredNames);
        Assert.Contains("legend_submit_machine_learning_candidate", discoveredNames);
        Assert.Contains("legend_software_remediation_status", discoveredNames);
        Assert.Contains("legend_inspect_repository", discoveredNames);
        Assert.Contains("legend_prepare_software_repair", discoveredNames);
        Assert.Contains("legend_inspect_repair_validation", discoveredNames);
        Assert.Contains("legend_request_repair_release", discoveredNames);
        Assert.Contains("legend_release_approved_repair", discoveredNames);
        Assert.Contains("legend_verify_repair_deployment", discoveredNames);
        Assert.All(discovered, item =>
        {
            var mutation = item.GetProperty("access").GetString() ==
                "founder_governed_mutation";
            Assert.Equal(mutation,
                item.GetProperty("requiresExplicitFounderCommand").GetBoolean());
            Assert.False(item.GetProperty("canOverrideAuthorities").GetBoolean());
            Assert.False(item.GetProperty("canDeploy").GetBoolean());
            Assert.False(item.GetProperty("arbitrarySql").GetBoolean());
            Assert.False(item.GetProperty("arbitraryShell").GetBoolean());
            Assert.False(item.GetProperty("arbitraryCodeExecution").GetBoolean());
        });

        var repairPreparation = discovered.Single(item =>
            item.GetProperty("name").GetString() == "legend_prepare_software_repair");
        Assert.True(repairPreparation.GetProperty("canModifyRepository").GetBoolean());
        Assert.True(repairPreparation.GetProperty("canCreateIsolatedRepairBranch").GetBoolean());
        Assert.False(repairPreparation.GetProperty("canMergeExactApprovedRepair").GetBoolean());

        var release = discovered.Single(item =>
            item.GetProperty("name").GetString() == "legend_release_approved_repair");
        Assert.False(release.GetProperty("canModifyRepository").GetBoolean());
        Assert.True(release.GetProperty("canMergeExactApprovedRepair").GetBoolean());

        Assert.DoesNotContain("run_shell", discoveredNames);
        Assert.DoesNotContain("execute_sql", discoveredNames);
        Assert.DoesNotContain("git", discoveredNames);
        Assert.DoesNotContain("azure_cli", discoveredNames);
    }

    [Fact]
    public async Task SoftwareRemediation_FailsClosedWithoutCanonicalConfiguration()
    {
        var factory = new ThrowingHttpClientFactory();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var service = new FounderSoftwareRemediationService(
            factory,
            configuration,
            NullLogger<FounderSoftwareRemediationService>.Instance);

        var status = await service.GetStatusAsync(CancellationToken.None);
        var prepare = await service.PrepareAsync(
            "teacher",
            new FounderSoftwareRepairProposal(
                new string('a', 40),
                "Bounded repair",
                "Validate fail-closed configuration.",
                [new FounderSoftwareRepairChange("AgentPortal/Services/Example.cs", "namespace Example;")]),
            CancellationToken.None);

        using var statusDocument = JsonDocument.Parse(JsonSerializer.Serialize(status));
        using var prepareDocument = JsonDocument.Parse(JsonSerializer.Serialize(prepare));
        Assert.Equal("software_remediation_not_configured", statusDocument.RootElement.GetProperty("error").GetString());
        Assert.Equal("software_remediation_not_configured", prepareDocument.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task SoftwareRemediation_LegendPreparationIsCompetencyGatedBeforeGitHubAccess()
    {
        var factory = new ThrowingHttpClientFactory();
        var service = new FounderSoftwareRemediationService(
            factory,
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<FounderSoftwareRemediationService>.Instance);

        var result = await service.PrepareAsync(
            "legend",
            new FounderSoftwareRepairProposal(
                new string('b', 40),
                "Bounded repair",
                "Legend must not fabricate an engineering competency.",
                [new FounderSoftwareRepairChange("AgentPortal/Services/Example.cs", "namespace Example;")]),
            CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.Equal("prepare_software_repair", document.RootElement.GetProperty("capability").GetString());
        Assert.Equal("insufficient", document.RootElement.GetProperty("knowledge").GetString());
        Assert.False(document.RootElement.GetProperty("executed").GetBoolean());
        Assert.Equal("OpenAI Teacher", document.RootElement.GetProperty("escalation").GetString());
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task SoftwareRemediation_RevocationFailsClosedWithoutARepositoryCall()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var factory = new ThrowingHttpClientFactory();
        var service = new FounderSoftwareRemediationService(
            factory,
            CreateRemediationConfiguration(),
            NullLogger<FounderSoftwareRemediationService>.Instance,
            db);

        var revoked = await service.RevokeAsync("founder-1", CancellationToken.None);
        var status = Assert.IsType<FounderSoftwareRemediationStatusSnapshot>(revoked);
        Assert.True(status.Revoked);
        Assert.False(status.RepairPreparationReady);
        Assert.False(status.ProductionDirectWriteEnabled);

        var preparation = await service.PrepareAsync(
            "teacher",
            new FounderSoftwareRepairProposal(
                new string('a', 40),
                "Repair bounded authority",
                "The durable kill switch must preempt every GitHub operation.",
                [new FounderSoftwareRepairChange("AgentPortal/Services/Example.cs", "namespace Example;")]),
            CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(preparation));
        Assert.Equal("software_remediation_not_configured", document.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task IntelligenceEvaluation_UsesCitedSignalsAndReusesAnIdenticalSnapshot()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var service = new LegendIntelligenceEvaluationService(db);

        var first = await service.CreateEvidenceSnapshotAsync("founder-1", CancellationToken.None);
        Assert.Equal("InsufficientEvidence", first.State);
        Assert.All(first.Domains, domain => Assert.Null(domain.EvidenceScore));
        Assert.Equal(1, await db.LegendIntelligenceEvaluationSnapshots.CountAsync());
        Assert.Equal(LegendIntelligenceEvaluationDomainCatalog.All.Count,
            await db.LegendIntelligenceEvaluationDomainSnapshots.CountAsync());

        var contract = await db.LegendIntelligenceEvaluationContracts.SingleAsync();
        var metrics = new Dictionary<string, decimal>
        {
            ["coverage"] = 75m,
            ["quality"] = 80m,
            ["diversity"] = 70m,
            ["validation_maturity"] = 85m,
            ["held_out"] = 90m,
            ["transfer"] = 88m,
            ["native_execution"] = 82m,
            ["calibration"] = 76m,
            ["contradiction_rate"] = 4m
        };
        foreach (var metric in metrics)
        {
            db.LegendIntelligenceEvaluationSignals.Add(new LegendIntelligenceEvaluationSignal
            {
                ContractId = contract.Id,
                DomainKey = "language_linguistic",
                MetricKey = metric.Key,
                Value = metric.Value,
                EvidenceAuthority = "canonical-evaluator",
                EvidenceReference = "evaluation-proof-" + metric.Key,
                State = "Current",
                MeasuredUtc = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();

        var measured = await service.CreateEvidenceSnapshotAsync("founder-1", CancellationToken.None);
        var language = measured.Domains.Single(domain => domain.Key == "language_linguistic");
        Assert.NotNull(language.EvidenceScore);
        Assert.Equal(9, language.EvidenceVolume);
        Assert.Null(measured.LegendSelfAssessment);
        Assert.Null(measured.OpenAiIndependentAssessment);

        var repeated = await service.CreateEvidenceSnapshotAsync("founder-1", CancellationToken.None);
        Assert.Equal(measured.EvaluatedUtc, repeated.EvaluatedUtc);
        Assert.Equal(2, await db.LegendIntelligenceEvaluationSnapshots.CountAsync());
    }

    [Fact]
    public async Task SoftwareRemediation_InvalidReleaseIdentityCannotReachGitHub()
    {
        var factory = new ThrowingHttpClientFactory();
        var service = new FounderSoftwareRemediationService(
            factory,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FounderSoftwareRemediation:Enabled"] = "true",
                ["FounderSoftwareRemediation:RepositoryOwner"] = "MYLEGND",
                ["FounderSoftwareRemediation:RepositoryName"] = "masterapp",
                ["FounderSoftwareRemediation:BaseBranch"] = "production",
                ["FounderSoftwareRemediation:GitHubAppId"] = "1",
                ["FounderSoftwareRemediation:GitHubInstallationId"] = "1",
                ["FounderSoftwareRemediation:GitHubAppPrivateKeySecretUri"] = "https://example.vault.azure.net/secrets/github-app",
                ["FounderSoftwareRemediation:GitHubApiBaseUri"] = "https://api.github.com/"
            }).Build(),
            NullLogger<FounderSoftwareRemediationService>.Instance);

        var result = await service.ReleaseApprovedAsync(0, "not-a-sha", CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.Equal("invalid_release_identity", document.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task SoftwareRemediation_TeacherPreparationCreatesOnlyBoundedBranchCommitAndPullRequest()
    {
        using var key = RSA.Create(2048);
        var handler = new GitHubRemediationScenarioHandler(
            key.ExportPkcs8PrivateKeyPem(),
            includePullRequestReviews: true);
        var service = new FounderSoftwareRemediationService(
            new ScenarioHttpClientFactory(handler),
            CreateRemediationConfiguration(),
            NullLogger<FounderSoftwareRemediationService>.Instance,
            new StaticTokenCredential());

        var result = await service.PrepareAsync(
            "teacher",
            new FounderSoftwareRepairProposal(
                new string('a', 40),
                "Repair bounded authority",
                "Exercise the canonical GitHub-App repair preparation path.",
                [new FounderSoftwareRepairChange("AgentPortal/Services/Example.cs", "namespace Example;")]),
            CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.True(document.RootElement.GetProperty("prepared").GetBoolean());
        Assert.Equal(new string('c', 40), document.RootElement.GetProperty("repairCommitSha").GetString());
        Assert.Contains(handler.RequestPaths, path => path.EndsWith("/git/refs", StringComparison.Ordinal));
        Assert.Contains(handler.RequestPaths, path => path.EndsWith("/pulls", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.RequestPaths, path => path.Contains("/merge", StringComparison.Ordinal));
        Assert.DoesNotContain("private key", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("installation-token", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SoftwareRemediation_RefusesMergeWhenProtectedBranchReviewRequirementIsMissing()
    {
        using var key = RSA.Create(2048);
        var handler = new GitHubRemediationScenarioHandler(
            key.ExportPkcs8PrivateKeyPem(),
            includePullRequestReviews: false);
        var service = new FounderSoftwareRemediationService(
            new ScenarioHttpClientFactory(handler),
            CreateRemediationConfiguration(),
            NullLogger<FounderSoftwareRemediationService>.Instance,
            new StaticTokenCredential());

        var result = await service.ReleaseApprovedAsync(123, new string('c', 40), CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.Equal("protected_branch_requirements_not_verified", document.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain(handler.RequestPaths, path => path.Contains("/merge", StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionDeploymentWorkflow_HasNoManualDispatchBypass()
    {
        var workflow = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "agentportal-production-deploy.yml"));

        Assert.Contains("push:", workflow, StringComparison.Ordinal);
        Assert.Contains("- production", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow_dispatch:", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void SecurityCi_RecognizesBothCurrentDotnetTestSuccessFormats()
    {
        var workflow = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "security-ci.yml"));

        Assert.Contains("Test Run Successful", workflow, StringComparison.Ordinal);
        Assert.Contains("Passed![[:space:]]+-[[:space:]]+Failed:[[:space:]]+0", workflow, StringComparison.Ordinal);
        Assert.Contains("Failed:[[:space:]]*[1-9][0-9]*", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionReadOnlyDiagnosticWorkflow_CannotPushOrCommitToProduction()
    {
        var workflow = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "legend-production-readonly-diagnostic.yml"));

        Assert.Contains("contents: read", workflow, StringComparison.Ordinal);
        Assert.Contains("Upload sanitized diagnostic transcript", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("git push", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git commit", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contents: write", workflow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FounderMutationConfirmation_DefaultsToFalse()
    {
        var request = new LegendFounderAiChatRequest();

        Assert.False(request.FounderCommandConfirmed);
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public int Calls { get; private set; }

        public HttpClient CreateClient(string name)
        {
            Calls++;
            throw new InvalidOperationException("No GitHub or Key Vault request is allowed in this fail-closed regression.");
        }
    }

    private static IConfiguration CreateRemediationConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FounderSoftwareRemediation:Enabled"] = "true",
            ["FounderSoftwareRemediation:RepositoryOwner"] = "MYLEGND",
            ["FounderSoftwareRemediation:RepositoryName"] = "masterapp",
            ["FounderSoftwareRemediation:BaseBranch"] = "production",
            ["FounderSoftwareRemediation:GitHubAppId"] = "1",
            ["FounderSoftwareRemediation:GitHubInstallationId"] = "2",
            ["FounderSoftwareRemediation:GitHubAppPrivateKeySecretUri"] = "https://example.vault.azure.net/secrets/github-app",
            ["FounderSoftwareRemediation:GitHubApiBaseUri"] = "https://api.github.com/"
        }).Build();

    private sealed class ScenarioHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StaticTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("managed-identity-token", DateTimeOffset.UtcNow.AddMinutes(10));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class GitHubRemediationScenarioHandler(
        string privateKey,
        bool includePullRequestReviews) : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            RequestPaths.Add(path);
            var response = request.RequestUri.Host.Equals("example.vault.azure.net", StringComparison.OrdinalIgnoreCase)
                ? Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { value = privateKey }))
                : RespondToGitHub(path, request.Method);
            return Task.FromResult(response);
        }

        private HttpResponseMessage RespondToGitHub(string path, HttpMethod method)
        {
            if (path == "/app/installations/2/access_tokens" && method == HttpMethod.Post)
                return Json(HttpStatusCode.Created, "{\"token\":\"installation-token\"}");
            if (path == "/repos/MYLEGND/masterapp/git/ref/heads/production")
                return Json(HttpStatusCode.OK, $"{{\"object\":{{\"sha\":\"{new string('a', 40)}\"}}}}");
            if (path == $"/repos/MYLEGND/masterapp/git/commits/{new string('a', 40)}")
                return Json(HttpStatusCode.OK, $"{{\"tree\":{{\"sha\":\"{new string('b', 40)}\"}}}}");
            if (path == "/repos/MYLEGND/masterapp/git/blobs" && method == HttpMethod.Post)
                return Json(HttpStatusCode.Created, $"{{\"sha\":\"{new string('d', 40)}\"}}");
            if (path == "/repos/MYLEGND/masterapp/git/trees" && method == HttpMethod.Post)
                return Json(HttpStatusCode.Created, $"{{\"sha\":\"{new string('e', 40)}\"}}");
            if (path == "/repos/MYLEGND/masterapp/git/commits" && method == HttpMethod.Post)
                return Json(HttpStatusCode.Created, $"{{\"sha\":\"{new string('c', 40)}\"}}");
            if (path == "/repos/MYLEGND/masterapp/git/refs" && method == HttpMethod.Post)
                return Json(HttpStatusCode.Created, "{}");
            if (path == "/repos/MYLEGND/masterapp/pulls" && method == HttpMethod.Post)
                return Json(HttpStatusCode.Created, "{\"number\":123,\"html_url\":\"https://github.example/pull/123\"}");
            if (path == "/repos/MYLEGND/masterapp/pulls/123" && method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, $"{{\"head\":{{\"sha\":\"{new string('c', 40)}\"}},\"base\":{{\"ref\":\"production\"}},\"state\":\"open\"}}");
            if (path == $"/repos/MYLEGND/masterapp/commits/{new string('c', 40)}/check-runs" && method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, "{\"check_runs\":[{\"name\":\"security\",\"conclusion\":\"success\"}]}");
            if (path == "/repos/MYLEGND/masterapp/branches/production/protection" && method == HttpMethod.Get)
            {
                var reviews = includePullRequestReviews ? ",\"required_pull_request_reviews\":{}" : string.Empty;
                return Json(HttpStatusCode.OK, $"{{\"required_status_checks\":{{\"strict\":true,\"contexts\":[\"security\"]}},\"enforce_admins\":{{\"enabled\":true}}{reviews}}}");
            }

            return Json(HttpStatusCode.NotFound, "{\"message\":\"unexpected request\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) =>
            new(statusCode) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    }

}
