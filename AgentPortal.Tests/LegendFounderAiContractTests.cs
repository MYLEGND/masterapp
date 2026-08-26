using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
    public void FounderChatClient_RequestsSingleProgressResultProtocolAndRendersStructuredFailure()
    {
        var script = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "legend-founder-ai.js"));

        Assert.Contains("'Accept': 'application/x-ndjson'", script, StringComparison.Ordinal);
        Assert.Contains("consumeChatResultStream", script, StringComparison.Ordinal);
        Assert.Contains("structuredFailureMessage", script, StringComparison.Ordinal);
        Assert.Contains("result.responseAuthority", script, StringComparison.Ordinal);
        Assert.Contains("result.stage", script, StringComparison.Ordinal);
        Assert.Contains("result.reason", script, StringComparison.Ordinal);
        Assert.DoesNotContain("progressUrlFor(modalElement.dataset.chatUrl, operationId)", script, StringComparison.Ordinal);
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
                [new FounderSoftwareRepairChange("AgentPortal/Services/Example.cs", "namespace Example;", new string('d', 40))]),
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
    public void SoftwareRemediationToolContract_SeparatesCommitAndBlobIdentityAndBoundsPatchInput()
    {
        var buildTools = typeof(LegendFounderAiConversationService)
            .GetMethod("BuildFounderTools", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(buildTools);

        var tools = Assert.IsAssignableFrom<IReadOnlyList<object>>(buildTools!.Invoke(null, null));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(tools));
        var functions = document.RootElement.EnumerateArray()
            .Where(tool => tool.TryGetProperty("type", out var type) && type.GetString() == "function")
            .ToArray();
        var inspect = functions.Single(tool => tool.GetProperty("name").GetString() == "legend_inspect_repository");
        var prepare = functions.Single(tool => tool.GetProperty("name").GetString() == "legend_prepare_software_repair");

        var inspectionProperties = inspect.GetProperty("parameters").GetProperty("properties");
        Assert.True(inspectionProperties.TryGetProperty("start_line", out _));
        Assert.True(inspectionProperties.TryGetProperty("line_count", out _));
        Assert.True(inspectionProperties.TryGetProperty("search_text", out _));
        Assert.True(inspectionProperties.TryGetProperty("search_context_lines", out _));

        var preparationProperties = prepare.GetProperty("parameters").GetProperty("properties");
        var fullChangeProperties = preparationProperties.GetProperty("full_file_changes")
            .GetProperty("items").GetProperty("properties");
        Assert.True(fullChangeProperties.TryGetProperty("expected_blob_sha", out _));
        Assert.True(fullChangeProperties.TryGetProperty("content", out _));

        var patchProperties = preparationProperties.GetProperty("patches")
            .GetProperty("items").GetProperty("properties");
        Assert.True(patchProperties.TryGetProperty("expected_blob_sha", out _));
        Assert.False(patchProperties.TryGetProperty("content", out _));
        var editProperties = patchProperties.GetProperty("edits")
            .GetProperty("items").GetProperty("properties");
        Assert.True(editProperties.TryGetProperty("expected_text", out _));
        Assert.True(editProperties.TryGetProperty("replacement_text", out _));

        var description = prepare.GetProperty("description").GetString();
        Assert.Contains("commit SHA", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blob SHA", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SoftwareRemediation_InspectsSmallAndLargeUtf8FilesThroughBoundedViews()
    {
        using var key = RSA.Create(2048);
        var smallHandler = new GitHubRemediationScenarioHandler(
            key.ExportPkcs8PrivateKeyPem(),
            includePullRequestReviews: true,
            repositoryText: "namespace Example;\n");
        var smallService = CreateRemediationService(smallHandler);

        var small = await smallService.InspectRepositoryAsync(
            new FounderSoftwareRepositoryInspectionRequest(
                "AgentPortal/Services/Example.cs",
                GitHubRemediationScenarioHandler.BaseCommitSha),
            CancellationToken.None);
        using var smallDocument = JsonDocument.Parse(JsonSerializer.Serialize(small));
        Assert.True(smallDocument.RootElement.GetProperty("fullFileReturned").GetBoolean());
        Assert.Equal(GitHubRemediationScenarioHandler.BaseBlobSha, smallDocument.RootElement.GetProperty("blobSha").GetString());
        Assert.Equal("namespace Example;\n", smallDocument.RootElement.GetProperty("content").GetString());

        var largeText = string.Join("\n", Enumerable.Range(1, 500)
            .Select(line => $"line-{line:D4} target-{line:D4} {new string('x', 160)}"));
        var largeHandler = new GitHubRemediationScenarioHandler(
            key.ExportPkcs8PrivateKeyPem(),
            includePullRequestReviews: true,
            repositoryText: largeText);
        var largeService = CreateRemediationService(largeHandler);

        var metadata = await largeService.InspectRepositoryAsync(
            new FounderSoftwareRepositoryInspectionRequest(
                "AgentPortal/Services/Example.cs",
                GitHubRemediationScenarioHandler.BaseCommitSha),
            CancellationToken.None);
        using var metadataDocument = JsonDocument.Parse(JsonSerializer.Serialize(metadata));
        Assert.False(metadataDocument.RootElement.GetProperty("fullFileReturned").GetBoolean());
        Assert.True(metadataDocument.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal("metadata_only", metadataDocument.RootElement.GetProperty("search").GetProperty("mode").GetString());

        var range = await largeService.InspectRepositoryAsync(
            new FounderSoftwareRepositoryInspectionRequest(
                "AgentPortal/Services/Example.cs",
                GitHubRemediationScenarioHandler.BaseCommitSha,
                StartLine: 20,
                LineCount: 5),
            CancellationToken.None);
        using var rangeDocument = JsonDocument.Parse(JsonSerializer.Serialize(range));
        var rangeValue = rangeDocument.RootElement.GetProperty("lineRange");
        Assert.Equal(5, rangeValue.GetProperty("returnedLineCount").GetInt32());
        Assert.True(rangeValue.GetProperty("beforeTruncated").GetBoolean());
        Assert.True(rangeValue.GetProperty("afterTruncated").GetBoolean());

        var search = await largeService.InspectRepositoryAsync(
            new FounderSoftwareRepositoryInspectionRequest(
                "AgentPortal/Services/Example.cs",
                GitHubRemediationScenarioHandler.BaseCommitSha,
                SearchText: "target-0042",
                SearchContextLines: 2),
            CancellationToken.None);
        using var searchDocument = JsonDocument.Parse(JsonSerializer.Serialize(search));
        var searchValue = searchDocument.RootElement.GetProperty("search");
        Assert.Equal(1, searchValue.GetProperty("returnedMatchCount").GetInt32());
        Assert.Equal(2, searchValue.GetProperty("contextLines").GetInt32());
        Assert.True(searchDocument.RootElement.GetProperty("truncated").GetBoolean());

        var manyMatches = await largeService.InspectRepositoryAsync(
            new FounderSoftwareRepositoryInspectionRequest(
                "AgentPortal/Services/Example.cs",
                GitHubRemediationScenarioHandler.BaseCommitSha,
                SearchText: "target-",
                SearchContextLines: 1),
            CancellationToken.None);
        using var manyMatchesDocument = JsonDocument.Parse(JsonSerializer.Serialize(manyMatches));
        var manyMatchesValue = manyMatchesDocument.RootElement.GetProperty("search");
        Assert.Equal(12, manyMatchesValue.GetProperty("returnedMatchCount").GetInt32());
        Assert.True(manyMatchesValue.GetProperty("matchesTruncated").GetBoolean());
        Assert.Empty(largeHandler.RepositoryWritePaths);
    }

    [Fact]
    public async Task SoftwareRemediation_RejectsInvalidUtf8BeforeRepositoryMutation()
    {
        using var key = RSA.Create(2048);
        var handler = new GitHubRemediationScenarioHandler(
            key.ExportPkcs8PrivateKeyPem(),
            includePullRequestReviews: true,
            repositoryBytes: [0xff, 0xfe, 0xfd]);
        var service = CreateRemediationService(handler);

        var result = await service.InspectRepositoryAsync(
            new FounderSoftwareRepositoryInspectionRequest(
                "AgentPortal/Services/Example.cs",
                GitHubRemediationScenarioHandler.BaseCommitSha),
            CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.Equal("repository_content_not_utf8", document.RootElement.GetProperty("error").GetString());
        Assert.Empty(handler.RepositoryWritePaths);

        var binaryHandler = new GitHubRemediationScenarioHandler(
            key.ExportPkcs8PrivateKeyPem(),
            includePullRequestReviews: true,
            repositoryBytes: [0x00, 0x01, 0x02]);
        var binaryResult = await CreateRemediationService(binaryHandler).InspectRepositoryAsync(
            new FounderSoftwareRepositoryInspectionRequest(
                "AgentPortal/Services/Example.cs",
                GitHubRemediationScenarioHandler.BaseCommitSha),
            CancellationToken.None);
        AssertFailure(binaryResult, "repository_content_not_utf8");
        Assert.Empty(binaryHandler.RepositoryWritePaths);
    }

    [Fact]
    public async Task SoftwareRemediation_AppliesExactBlobBoundPatchesAtomicallyInSourceOrder()
    {
        using var key = RSA.Create(2048);
        const string source = "prefix\nfirst-target\nmiddle\nsecond-target\nsuffix\n";
        var handler = new GitHubRemediationScenarioHandler(
            key.ExportPkcs8PrivateKeyPem(),
            includePullRequestReviews: true,
            repositoryText: source);
        var service = CreateRemediationService(handler);

        var result = await service.PrepareAsync(
            "teacher",
            PatchProposal(
                [new FounderSoftwarePatchChange(
                    "AgentPortal/Services/Example.cs",
                    GitHubRemediationScenarioHandler.BaseBlobSha,
                    [
                        new FounderSoftwarePatchEdit("first-target", "first-repaired"),
                        new FounderSoftwarePatchEdit("second-target", "second-repaired")
                    ])]),
            CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.True(document.RootElement.GetProperty("prepared").GetBoolean());
        Assert.Single(handler.UploadedBlobBodies);
        using var blob = JsonDocument.Parse(handler.UploadedBlobBodies.Single());
        var resultingText = blob.RootElement.GetProperty("content").GetString();
        Assert.Equal("prefix\nfirst-repaired\nmiddle\nsecond-repaired\nsuffix\n", resultingText);
        Assert.Contains(handler.RepositoryWritePaths, path => path.EndsWith("/git/trees", StringComparison.Ordinal));
        Assert.Contains(handler.RepositoryWritePaths, path => path.EndsWith("/git/commits", StringComparison.Ordinal));
        Assert.Contains(handler.RepositoryWritePaths, path => path.EndsWith("/git/refs", StringComparison.Ordinal));
        Assert.Contains(handler.RepositoryWritePaths, path => path.EndsWith("/pulls", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SoftwareRemediation_PatchValidationRejectsStaleMissingAmbiguousOverlappingAndReorderedEditsWithoutWrites()
    {
        await AssertPatchFailureWithoutRepositoryWritesAsync(
            "only-target",
            new string('e', 40),
            [new FounderSoftwarePatchEdit("only-target", "changed")],
            "expected_blob_sha_stale");
        await AssertPatchFailureWithoutRepositoryWritesAsync(
            "only-target",
            GitHubRemediationScenarioHandler.BaseBlobSha,
            [new FounderSoftwarePatchEdit("missing-target", "changed")],
            "patch_expected_text_missing");
        await AssertPatchFailureWithoutRepositoryWritesAsync(
            "same same",
            GitHubRemediationScenarioHandler.BaseBlobSha,
            [new FounderSoftwarePatchEdit("same", "changed")],
            "patch_expected_text_ambiguous");
        await AssertPatchFailureWithoutRepositoryWritesAsync(
            "abcdef",
            GitHubRemediationScenarioHandler.BaseBlobSha,
            [
                new FounderSoftwarePatchEdit("abc", "one"),
                new FounderSoftwarePatchEdit("bcd", "two")
            ],
            "patch_edits_overlap");
        await AssertPatchFailureWithoutRepositoryWritesAsync(
            "first second",
            GitHubRemediationScenarioHandler.BaseBlobSha,
            [
                new FounderSoftwarePatchEdit("second", "two"),
                new FounderSoftwarePatchEdit("first", "one")
            ],
            "patch_edits_reordered");
    }

    [Fact]
    public async Task SoftwareRemediation_PatchLimitsBaseMovementAndMultiFileFailureLeaveNoGitHubWrites()
    {
        using var key = RSA.Create(2048);
        var sixPaths = Enumerable.Range(1, 7)
            .Select(index => new FounderSoftwarePatchChange(
                $"AgentPortal/Services/Example{index}.cs",
                GitHubRemediationScenarioHandler.BaseBlobSha,
                [new FounderSoftwarePatchEdit("target", "changed")]))
            .ToArray();
        var maximumHandler = new GitHubRemediationScenarioHandler(key.ExportPkcs8PrivateKeyPem(), true, repositoryText: "target");
        var maximumResult = await CreateRemediationService(maximumHandler).PrepareAsync("teacher", PatchProposal(sixPaths), CancellationToken.None);
        AssertFailure(maximumResult, "invalid_repair_proposal");
        Assert.Empty(maximumHandler.RepositoryWritePaths);

        var inputHandler = new GitHubRemediationScenarioHandler(key.ExportPkcs8PrivateKeyPem(), true, repositoryText: "target");
        var inputResult = await CreateRemediationService(inputHandler).PrepareAsync(
            "teacher",
            PatchProposal([new FounderSoftwarePatchChange(
                "AgentPortal/Services/Example.cs",
                GitHubRemediationScenarioHandler.BaseBlobSha,
                [new FounderSoftwarePatchEdit(new string('q', 12_001), "changed")])]),
            CancellationToken.None);
        AssertFailure(inputResult, "patch_limit_exceeded");
        Assert.Empty(inputHandler.RepositoryWritePaths);

        var cumulativeHandler = new GitHubRemediationScenarioHandler(key.ExportPkcs8PrivateKeyPem(), true, repositoryText: "target");
        var cumulativeEdits = Enumerable.Range(1, 11)
            .Select(_ => new FounderSoftwarePatchEdit(new string('q', 11_000), string.Empty))
            .ToArray();
        var cumulativeResult = await CreateRemediationService(cumulativeHandler).PrepareAsync(
            "teacher",
            PatchProposal([new FounderSoftwarePatchChange(
                "AgentPortal/Services/Example.cs",
                GitHubRemediationScenarioHandler.BaseBlobSha,
                cumulativeEdits)]),
            CancellationToken.None);
        AssertFailure(cumulativeResult, "patch_limit_exceeded");
        Assert.Empty(cumulativeHandler.RepositoryWritePaths);

        var baseMovementHandler = new GitHubRemediationScenarioHandler(
            key.ExportPkcs8PrivateKeyPem(),
            true,
            repositoryText: "target",
            branchSha: new string('b', 40));
        var baseMovementResult = await CreateRemediationService(baseMovementHandler).PrepareAsync(
            "teacher",
            PatchProposal([new FounderSoftwarePatchChange(
                "AgentPortal/Services/Example.cs",
                GitHubRemediationScenarioHandler.BaseBlobSha,
                [new FounderSoftwarePatchEdit("target", "changed")])]),
            CancellationToken.None);
        AssertFailure(baseMovementResult, "base_sha_stale");
        Assert.Empty(baseMovementHandler.RepositoryWritePaths);

        var atomicHandler = new GitHubRemediationScenarioHandler(key.ExportPkcs8PrivateKeyPem(), true, repositoryText: "first-target");
        var atomicResult = await CreateRemediationService(atomicHandler).PrepareAsync(
            "teacher",
            PatchProposal(
            [
                new FounderSoftwarePatchChange(
                    "AgentPortal/Services/ExampleOne.cs",
                    GitHubRemediationScenarioHandler.BaseBlobSha,
                    [new FounderSoftwarePatchEdit("first-target", "changed")]),
                new FounderSoftwarePatchChange(
                    "AgentPortal/Services/ExampleTwo.cs",
                    GitHubRemediationScenarioHandler.BaseBlobSha,
                    [new FounderSoftwarePatchEdit("missing-target", "changed")])
            ]),
            CancellationToken.None);
        AssertFailure(atomicResult, "patch_expected_text_missing");
        Assert.Empty(atomicHandler.RepositoryWritePaths);

        var sourceAtLimit = new string('x', 399_999) + "@";
        var resultLimitHandler = new GitHubRemediationScenarioHandler(key.ExportPkcs8PrivateKeyPem(), true, repositoryText: sourceAtLimit);
        var resultLimit = await CreateRemediationService(resultLimitHandler).PrepareAsync(
            "teacher",
            PatchProposal([new FounderSoftwarePatchChange(
                "AgentPortal/Services/Example.cs",
                GitHubRemediationScenarioHandler.BaseBlobSha,
                [new FounderSoftwarePatchEdit("@", "@@@@")])]),
            CancellationToken.None);
        AssertFailure(resultLimit, "resulting_file_limit_exceeded");
        Assert.Empty(resultLimitHandler.RepositoryWritePaths);
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
                [new FounderSoftwareRepairChange("AgentPortal/Services/Example.cs", "namespace Example;", new string('d', 40))]),
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

    private static FounderSoftwareRemediationService CreateRemediationService(
        GitHubRemediationScenarioHandler handler) =>
        new(
            new ScenarioHttpClientFactory(handler),
            CreateRemediationConfiguration(),
            NullLogger<FounderSoftwareRemediationService>.Instance,
            new StaticTokenCredential());

    private static FounderSoftwareRepairProposal PatchProposal(
        IReadOnlyList<FounderSoftwarePatchChange> patches) =>
        new(
            GitHubRemediationScenarioHandler.BaseCommitSha,
            "Bounded patch repair",
            "Exercise exact immutable blob-bound patch preparation.",
            Changes: null,
            Patches: patches);

    private static void AssertFailure(object result, string error)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.Equal(error, document.RootElement.GetProperty("error").GetString());
    }

    private static async Task AssertPatchFailureWithoutRepositoryWritesAsync(
        string source,
        string expectedBlobSha,
        IReadOnlyList<FounderSoftwarePatchEdit> edits,
        string error)
    {
        using var key = RSA.Create(2048);
        var handler = new GitHubRemediationScenarioHandler(
            key.ExportPkcs8PrivateKeyPem(),
            includePullRequestReviews: true,
            repositoryText: source);
        var result = await CreateRemediationService(handler).PrepareAsync(
            "teacher",
            PatchProposal([new FounderSoftwarePatchChange(
                "AgentPortal/Services/Example.cs",
                expectedBlobSha,
                edits)]),
            CancellationToken.None);

        AssertFailure(result, error);
        Assert.Empty(handler.RepositoryWritePaths);
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

    private sealed class GitHubRemediationScenarioHandler : HttpMessageHandler
    {
        public const string BaseCommitSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public const string BaseTreeSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        public const string BaseBlobSha = "dddddddddddddddddddddddddddddddddddddddd";
        public const string RepairBlobSha = "ffffffffffffffffffffffffffffffffffffffff";
        public const string RepairTreeSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        public const string RepairCommitSha = "cccccccccccccccccccccccccccccccccccccccc";

        private readonly string _privateKey;
        private readonly bool _includePullRequestReviews;
        private readonly byte[] _repositoryBytes;
        private readonly string _branchSha;

        public GitHubRemediationScenarioHandler(
            string privateKey,
            bool includePullRequestReviews,
            string? repositoryText = null,
            byte[]? repositoryBytes = null,
            string? branchSha = null)
        {
            _privateKey = privateKey;
            _includePullRequestReviews = includePullRequestReviews;
            _repositoryBytes = repositoryBytes ?? Encoding.UTF8.GetBytes(repositoryText ?? "namespace Example;\n");
            _branchSha = branchSha ?? BaseCommitSha;
        }

        public List<string> RequestPaths { get; } = [];
        public List<string> RepositoryWritePaths { get; } = [];
        public List<string> UploadedBlobBodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            RequestPaths.Add(path);
            if (request.RequestUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) &&
                request.Method != HttpMethod.Get &&
                path.StartsWith("/repos/MYLEGND/masterapp/", StringComparison.Ordinal))
            {
                RepositoryWritePaths.Add(path);
                if (path == "/repos/MYLEGND/masterapp/git/blobs" && request.Content is not null)
                    UploadedBlobBodies.Add(request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            }

            var response = request.RequestUri.Host.Equals("example.vault.azure.net", StringComparison.OrdinalIgnoreCase)
                ? Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { value = _privateKey }))
                : RespondToGitHub(path, request.Method);
            return Task.FromResult(response);
        }

        private HttpResponseMessage RespondToGitHub(string path, HttpMethod method)
        {
            if (path == "/app/installations/2/access_tokens" && method == HttpMethod.Post)
                return Json(HttpStatusCode.Created, "{\"token\":\"installation-token\"}");
            if (path == "/repos/MYLEGND/masterapp" && method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, "{}");
            if (path == "/repos/MYLEGND/masterapp/git/ref/heads/production" && method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, $"{{\"object\":{{\"sha\":\"{_branchSha}\"}}}}");
            if (path == $"/repos/MYLEGND/masterapp/git/commits/{BaseCommitSha}" && method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, $"{{\"tree\":{{\"sha\":\"{BaseTreeSha}\"}}}}");
            if (path.StartsWith("/repos/MYLEGND/masterapp/contents/", StringComparison.Ordinal) && method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { type = "file", sha = BaseBlobSha, size = _repositoryBytes.Length }));
            if (path == $"/repos/MYLEGND/masterapp/git/blobs/{BaseBlobSha}" && method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { encoding = "base64", content = Convert.ToBase64String(_repositoryBytes) }));
            if (path == "/repos/MYLEGND/masterapp/git/blobs" && method == HttpMethod.Post)
                return Json(HttpStatusCode.Created, $"{{\"sha\":\"{RepairBlobSha}\"}}");
            if (path == "/repos/MYLEGND/masterapp/git/trees" && method == HttpMethod.Post)
                return Json(HttpStatusCode.Created, $"{{\"sha\":\"{RepairTreeSha}\"}}");
            if (path == "/repos/MYLEGND/masterapp/git/commits" && method == HttpMethod.Post)
                return Json(HttpStatusCode.Created, $"{{\"sha\":\"{RepairCommitSha}\"}}");
            if (path == "/repos/MYLEGND/masterapp/git/refs" && method == HttpMethod.Post)
                return Json(HttpStatusCode.Created, "{}");
            if (path == "/repos/MYLEGND/masterapp/pulls" && method == HttpMethod.Post)
                return Json(HttpStatusCode.Created, "{\"number\":123,\"html_url\":\"https://github.example/pull/123\"}");
            if (path == "/repos/MYLEGND/masterapp/pulls/123" && method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, $"{{\"head\":{{\"sha\":\"{RepairCommitSha}\"}},\"base\":{{\"ref\":\"production\"}},\"state\":\"open\"}}");
            if (path == $"/repos/MYLEGND/masterapp/commits/{RepairCommitSha}/check-runs" && method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, "{\"check_runs\":[{\"name\":\"security\",\"conclusion\":\"success\"}]}");
            if (path == "/repos/MYLEGND/masterapp/branches/production/protection" && method == HttpMethod.Get)
            {
                var reviews = _includePullRequestReviews ? ",\"required_pull_request_reviews\":{}" : string.Empty;
                return Json(HttpStatusCode.OK, $"{{\"required_status_checks\":{{\"strict\":true,\"contexts\":[\"security\"]}},\"enforce_admins\":{{\"enabled\":true}}{reviews}}}");
            }

            return Json(HttpStatusCode.NotFound, "{\"message\":\"unexpected request\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) =>
            new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

}
