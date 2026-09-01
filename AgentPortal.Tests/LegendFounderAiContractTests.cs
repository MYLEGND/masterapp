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
using AgentPortal.Mobile;
using AgentPortal.Models;
using AgentPortal.Security;
using AgentPortal.Services;
using Azure.Core;
using Domain.Entities;
using Domain.Messaging;
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
    public void MobileController_RetainsAuthenticatedFounderOnlyBoundary()
    {
        var type = typeof(MobileFounderAiController);

        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(type.GetCustomAttribute<FounderOnlyAttribute>());
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
                        .QueueMachineTeachingProposalAsync),
                BindingFlags.Instance |
                BindingFlags.NonPublic));
        Assert.Null(
            type.GetMethod(
                nameof(
                    FounderLegendConnectService
                        .QueueMachineTeachingProposalAsync),
                BindingFlags.Instance |
                BindingFlags.Public));
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
        Assert.Contains("nativeOnly:", script, StringComparison.Ordinal);
        Assert.Contains("sourceLanguageCode: null", script, StringComparison.Ordinal);
        Assert.Contains("result.responseAuthority ||", script, StringComparison.Ordinal);
        Assert.Contains("'Legend® Ai'", script, StringComparison.Ordinal);
        Assert.Contains("'OpenAI'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Verified native LEGEND · OpenAI responder not used", script, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAI Teacher · ${stage || 'provider response'}", script, StringComparison.Ordinal);
        Assert.Contains("OpenAI escalation is blocked for this clean conversation", script, StringComparison.Ordinal);
        Assert.DoesNotContain("progressUrlFor(modalElement.dataset.chatUrl, operationId)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void FounderSourceLanguageContract_IsExplicitAndHasNoEnglishDefault()
    {
        var request = new LegendFounderAiChatRequest();
        var json = JsonSerializer.Serialize(request);

        Assert.Null(request.SourceLanguageCode);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement
                .GetProperty("sourceLanguageCode")
                .ValueKind);
    }

    [Fact]
    public void FounderSourceLanguageWireContract_IsAlignedAcrossWebIosAndAndroid()
    {
        var web = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "legend-founder-ai.js"));
        var ios = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "LegendApplicationShell.swift"));
        var androidContracts = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "LegendMobileContracts.kt"));
        var androidViewModel = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "LegendFeatureViewModels.kt"));

        Assert.Contains("sourceLanguageCode: null", web, StringComparison.Ordinal);
        Assert.Contains("let sourceLanguageCode: String?", ios, StringComparison.Ordinal);
        Assert.Contains("sourceLanguageCode: String? = nil", ios, StringComparison.Ordinal);
        Assert.Contains("sourceLanguageCode: sourceLanguageCode", ios, StringComparison.Ordinal);
        Assert.Contains("let reason: String?", ios, StringComparison.Ordinal);

        Assert.Contains(
            "@SerialName(\"sourceLanguageCode\") val sourceLanguageCode: String? = null",
            androidContracts,
            StringComparison.Ordinal);
        Assert.Contains("val reason: String? = null", androidContracts, StringComparison.Ordinal);
        Assert.Contains("sourceLanguageCode: String? = null", androidViewModel, StringComparison.Ordinal);
        Assert.Contains("sourceLanguageCode = sourceLanguageCode", androidViewModel, StringComparison.Ordinal);

        Assert.DoesNotContain("sourceLanguageCode: 'en'", web, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceLanguageCode: \"en\"", ios, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceLanguageCode: String = \"en\"", androidContracts, StringComparison.Ordinal);
    }

    [Fact]
    public void FounderChatPresentation_UsesOneResponsiveControlTreeAndSharedTokens()
    {
        var script = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "legend-founder-ai.js"));
        var css = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "legend-founder-ai.css"));
        var modal = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "legend-founder-ai-modal.cshtml"));
        var mobile = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "LegendApplicationShell.swift"));
        var androidBuild = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Legend-Android.build.gradle.kts"));
        var androidApi = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "LegendApi.kt"));
        var androidRepository = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "LegendRepositories.kt"));
        var androidPresentation = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "LegendFounderAiConversation.kt"));
        var tokens = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "legend-design.tokens.json"));

        Assert.Contains("DESIGN_TOKEN_URL", script, StringComparison.Ordinal);
        Assert.Contains("applySharedDesignTokens", script, StringComparison.Ordinal);
        Assert.Contains("syncControlPlacement", script, StringComparison.Ordinal);
        Assert.Contains("AbortController", script, StringComparison.Ordinal);
        Assert.Contains("abortActiveRequest", script, StringComparison.Ordinal);
        Assert.Contains("logo.className", script, StringComparison.Ordinal);
        Assert.Contains("legend-founder-ai-logo-image", script, StringComparison.Ordinal);
        Assert.DoesNotContain("mobileNew", script, StringComparison.Ordinal);

        Assert.Contains("legendFounderAiMobileControls", modal, StringComparison.Ordinal);
        Assert.Equal(
            1,
            modal.Split("id=\"legendFounderAiModebar\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            1,
            modal.Split("id=\"legendFounderAiNativeOnly\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            2,
            modal.Split("legend-founder-ai-logo-image", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("legendFounderAiMobileNew", modal, StringComparison.Ordinal);
        Assert.DoesNotContain("Governed intelligence conversation", modal, StringComparison.Ordinal);
        Assert.DoesNotContain("legendFounderAiSubtitle", modal, StringComparison.Ordinal);
        Assert.DoesNotContain("legend-founder-ai-governance-mark", modal, StringComparison.Ordinal);
        Assert.True(
            modal.IndexOf("legendFounderAiMobileMenu", StringComparison.Ordinal) <
            modal.IndexOf("legend-founder-ai-close", StringComparison.Ordinal));

        Assert.DoesNotContain("legend-founder-ai-mobile-actions", css, StringComparison.Ordinal);
        Assert.DoesNotContain("is-reading", css, StringComparison.Ordinal);
        Assert.Contains("--legend-design-midnight", css, StringComparison.Ordinal);
        Assert.Contains(".legend-founder-ai-logo-image", css, StringComparison.Ordinal);
        Assert.Contains("object-fit: cover", css, StringComparison.Ordinal);
        Assert.Contains("grid-template-rows: 80px minmax(0, 1fr)", css, StringComparison.Ordinal);
        Assert.Equal(
            1,
            css.Split("@media (max-width: 820px)", StringSplitOptions.None).Length - 1);
        Assert.Contains("linear-gradient(135deg, #f0c767", css, StringComparison.Ordinal);

        Assert.DoesNotContain("LegendFounderAiMobileWebStyle", mobile, StringComparison.Ordinal);
        Assert.Contains("LegendFounderAiPresentationTokens", mobile, StringComparison.Ordinal);
        Assert.Contains("LegendFounderAiProfileMark", mobile, StringComparison.Ordinal);
        Assert.Contains(".scaledToFill()", mobile, StringComparison.Ordinal);
        Assert.DoesNotContain("scaleEffect(1.62)", mobile, StringComparison.Ordinal);
        Assert.Contains(".frame(height: 84)", mobile, StringComparison.Ordinal);
        Assert.DoesNotContain("Governed intelligence conversation", mobile, StringComparison.Ordinal);
        Assert.Contains("symbol: \"line.3.horizontal\"", mobile, StringComparison.Ordinal);
        Assert.Contains(".fill(LegendNextGradient.gold)", mobile, StringComparison.Ordinal);
        Assert.Equal(
            1,
            mobile.Split("Image(\"LegendAiIcon\")", StringSplitOptions.None).Length - 1);
        Assert.Contains("nativeOnly: Bool", mobile, StringComparison.Ordinal);
        Assert.Contains("stop.fill", mobile, StringComparison.Ordinal);
        Assert.Contains("Keep OpenAI off for this direct LEGEND test.", mobile, StringComparison.Ordinal);
        Assert.Contains("Toggle(\"Native-only\"", mobile, StringComparison.Ordinal);
        Assert.Contains("responseAuthority", mobile, StringComparison.Ordinal);
        Assert.Contains("LegendAiIcon.imageset/legendai.png", androidBuild, StringComparison.Ordinal);
        Assert.Contains("FounderAiRepository", androidRepository, StringComparison.Ordinal);
        Assert.Contains("api/v1/mobile/founder/legend-ai/access", androidApi, StringComparison.Ordinal);
        Assert.Contains("api/v1/mobile/founder/legend-ai/chat", androidApi, StringComparison.Ordinal);
        Assert.Contains("api/v1/mobile/founder/legend-ai/progress", androidRepository, StringComparison.Ordinal);
        Assert.DoesNotContain("openai.com", androidApi, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("openai.com", androidRepository, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LegendColors.Success", androidPresentation, StringComparison.Ordinal);
        Assert.Contains("LegendColors.Royal", androidPresentation, StringComparison.Ordinal);
        Assert.Contains("LegendColors.BrandBlueSurface", androidPresentation, StringComparison.Ordinal);
        Assert.Contains("responseAuthority", androidPresentation, StringComparison.Ordinal);
        Assert.DoesNotContain("Governed intelligence conversation", androidPresentation, StringComparison.Ordinal);
        Assert.Contains("FounderAiHeaderAction(Icons.Default.Menu", androidPresentation, StringComparison.Ordinal);
        Assert.Contains(".background(LegendGradients.Gold)", androidPresentation, StringComparison.Ordinal);
        Assert.Contains("\"midnight\"", tokens, StringComparison.Ordinal);
    }

    [Fact]
    public void FounderCurriculumTool_UsesClosedStrictVariationSchema()
    {
        var buildTools =
            typeof(LegendFounderToolAuthority)
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
            typeof(LegendFounderToolAuthority)
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
        var buildTools = typeof(LegendFounderToolAuthority)
            .GetMethod("BuildFounderTools", BindingFlags.NonPublic | BindingFlags.Static);
        var describe = typeof(LegendFounderToolAuthority)
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
    public void FounderToolSchemas_ExcludeProviderUnsupportedObjectCardinalityKeywords()
    {
        var buildTools = typeof(LegendFounderToolAuthority)
            .GetMethod("BuildFounderTools", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(buildTools);
        var tools = Assert.IsAssignableFrom<IReadOnlyList<object>>(
            buildTools!.Invoke(null, null));
        var serialized = JsonSerializer.Serialize(tools);

        // OpenAI strict function schemas reject minProperties/maxProperties.
        // The closed dimensions array and runtime parser share the 1..12 guard.
        Assert.DoesNotContain("\"minProperties\"", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"maxProperties\"", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void FounderToolCatalog_SerializedContractIsRecursivelyProviderValid()
    {
        var buildTools = typeof(LegendFounderToolAuthority)
            .GetMethod("BuildFounderTools", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(buildTools);
        var tools = Assert.IsAssignableFrom<IReadOnlyList<object>>(
            buildTools!.Invoke(null, null));

        var first = LegendFounderToolAuthority
            .ValidateSerializedToolCatalog(tools);
        var second = LegendFounderToolAuthority
            .ValidateSerializedToolCatalog(tools);

        Assert.Empty(first);
        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Theory]
    [InlineData(
        "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[],\"additionalProperties\":false}",
        "required names must exactly match properties")]
    [InlineData(
        "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":true}",
        "every strict object must be closed with false")]
    [InlineData(
        "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":[\"string\",\"integer\"]}},\"required\":[\"value\"],\"additionalProperties\":false}",
        "nullable schemas must contain exactly one supported non-null type and null")]
    [InlineData(
        "{\"type\":\"object\",\"properties\":{\"values\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[],\"additionalProperties\":false}}},\"required\":[\"values\"],\"additionalProperties\":false}",
        "properties.values.items.required")]
    [InlineData(
        "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false,\"minProperties\":1}",
        "keyword is not supported by the Founder provider schema contract")]
    [InlineData(
        "{\"type\":\"object\",\"properties\":{\"values\":{\"type\":\"array\",\"minItems\":\"1\",\"items\":{\"type\":\"string\"}}},\"required\":[\"values\"],\"additionalProperties\":false}",
        "expected a nonnegative integer")]
    public void FounderToolCatalog_RecursiveValidatorRejectsMalformedStrictSchemas(
        string parametersJson,
        string expectedError)
    {
        using var parametersDocument = JsonDocument.Parse(parametersJson);
        IReadOnlyList<object> tools =
        [
            new
            {
                type = "function",
                name = "legend_contract_probe",
                description = "Validate one deliberately malformed strict schema.",
                parameters = parametersDocument.RootElement.Clone(),
                strict = true
            }
        ];

        var errors = LegendFounderToolAuthority
            .ValidateSerializedToolCatalog(tools);

        Assert.Contains(
            errors,
            error => error.Contains(expectedError, StringComparison.Ordinal));
    }

    [Fact]
    public void FounderToolSemanticFrameSchema_UsesClosedRequiredDimensionValueArray()
    {
        var buildTools = typeof(LegendFounderToolAuthority)
            .GetMethod("BuildFounderTools", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(buildTools);
        var tools = Assert.IsAssignableFrom<IReadOnlyList<object>>(
            buildTools!.Invoke(null, null));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(tools));
        var machineLearningTool = document.RootElement
            .EnumerateArray()
            .Single(tool =>
                tool.TryGetProperty("name", out var name) &&
                name.GetString() == "legend_submit_machine_learning_candidate");
        var transition = machineLearningTool
            .GetProperty("parameters")
            .GetProperty("properties")
            .GetProperty("semantic_transitions")
            .GetProperty("items");

        AssertClosedSemanticFrameSchema(
            transition.GetProperty("properties").GetProperty("source"));
        AssertClosedSemanticFrameSchema(
            transition.GetProperty("properties").GetProperty("result"));
    }

    [Fact]
    public void FounderMachineTeachingSchema_RequiresClosedCapabilityAndCategoryIdentities()
    {
        var buildTools = typeof(LegendFounderToolAuthority)
            .GetMethod("BuildFounderTools", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(buildTools);
        var tools = Assert.IsAssignableFrom<IReadOnlyList<object>>(
            buildTools!.Invoke(null, null));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(tools));
        var parameters = document.RootElement
            .EnumerateArray()
            .Single(tool =>
                tool.TryGetProperty("name", out var name) &&
                name.GetString() == "legend_submit_machine_learning_candidate")
            .GetProperty("parameters");
        var properties = parameters.GetProperty("properties");
        var required = parameters.GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("capability_identity", required);
        Assert.Contains("category_identity", required);
        Assert.Equal(
            new[] { "translation", "same_language_semantic" },
            properties.GetProperty("capability_identity")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
        Assert.Equal(
            new[] { "reusable_semantic" },
            properties.GetProperty("category_identity")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
    }

    [Fact]
    public void FounderToolSemanticFrameParser_ReadsClosedDimensionValueArray()
    {
        const string input =
            """
            {
              "source": {
                "dimensions": [
                  { "dimension": " conversation_function ", "value": " wellbeing_inquiry " },
                  { "dimension": "audience.level", "value": "$Audience" }
                ]
              }
            }
            """;

        Assert.True(TryParseFounderSemanticFrame(input, out var frame));
        Assert.Equal(2, frame.Dimensions.Count);
        Assert.Equal("wellbeing_inquiry", frame.Dimensions["conversation_function"]);
        Assert.Equal("$Audience", frame.Dimensions["audience.level"]);
    }

    [Fact]
    public void FounderToolSemanticFrameParser_RejectsInvalidClosedArrayInputs()
    {
        var invalidFrames = new[]
        {
            """{"source":{"dimensions":[]}}""",
            """{"source":{"dimensions":{"intent":"diagnose"}}}""",
            """{"source":{"dimensions":[{"dimension":"intent","value":"diagnose"},{"dimension":" InTeNt ","value":"plan"}]}}""",
            """{"source":{"dimensions":[{"dimension":" ","value":"diagnose"}]}}""",
            """{"source":{"dimensions":[{"dimension":"intent","value":" "}]}}""",
            """{"source":{"dimensions":[{"dimension":"intent value","value":"diagnose"}]}}""",
            """{"source":{"dimensions":[{"dimension":"intent","value":"$1invalid"}]}}""",
            """{"source":{"dimensions":[{"dimension":"intent","value":"$invalid value"}]}}""",
            """{"source":{"dimensions":[{"dimension":1,"value":"diagnose"}]}}""",
            """{"source":{"dimensions":[{"dimension":"intent","value":false}]}}""",
            """{"source":{"dimensions":[{"dimension":"intent"}]}}""",
            """{"source":{"dimensions":[{"value":"diagnose"}]}}""",
            """{"source":{"dimensions":["intent"]}}""",
            """{"source":{"dimensions":[{"dimension":"intent","value":"diagnose","extra":"rejected"}]}}""",
            """{"source":{"dimensions":[{"dimension":"intent","dimension":"plan","value":"diagnose"}]}}""",
            """{"source":{"dimensions":[{"dimension":"intent","value":"diagnose"}]},"extra":true}""",
            SemanticFrameJson(
                Enumerable.Range(0, 13)
                    .Select(index => ($"dimension_{index}", "value"))),
            SemanticFrameJson(new[] { (new string('d', 81), "value") }),
            SemanticFrameJson(new[] { ("dimension", new string('v', 161)) })
        };

        Assert.All(
            invalidFrames,
            input => Assert.False(
                TryParseFounderSemanticFrame(input, out _),
                input));
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
        Assert.Equal("Evaluated", measured.State);
        Assert.NotNull(language.EvidenceScore);
        Assert.Equal(9, language.EvidenceVolume);
        Assert.Null(measured.LegendSelfAssessment);
        Assert.Null(measured.OpenAiIndependentAssessment);

        var repeated = await service.CreateEvidenceSnapshotAsync("founder-1", CancellationToken.None);
        Assert.Equal(measured.EvaluatedUtc, repeated.EvaluatedUtc);
        Assert.Equal(2, await db.LegendIntelligenceEvaluationSnapshots.CountAsync());
    }

    [Fact]
    public async Task IntelligenceEvaluation_DoesNotRelabelRegressionOrTrafficAsDirectCaseMeasurements()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;
        var family = new LegendCurriculumFamily
        {
            FamilyKey = "evaluation-family",
            SemanticCategory = "causal_reasoning",
            Provenance = "FounderApproved",
            UpdatedUtc = now
        };
        db.LegendCurriculumFamilies.Add(family);

        for (var index = 0; index < 3; index++)
        {
            var sourceUnit = new LegendLanguageTextUnit
            {
                LanguageCode = "en", StoragePartition = "evaluation", NormalizedHash = $"source-{index}",
                Text = $"source {index}", Provenance = "FounderApproved", IsTrainingEligible = true, UpdatedUtc = now
            };
            var resultUnit = new LegendLanguageTextUnit
            {
                LanguageCode = "en", StoragePartition = "evaluation", NormalizedHash = $"result-{index}",
                Text = $"result {index}", Provenance = "FounderApproved", IsTrainingEligible = true, UpdatedUtc = now
            };
            var source = new LegendCurriculumExample
            {
                CurriculumFamilyId = family.Id, TextUnitId = sourceUnit.Id, LanguageCode = "en",
                Provenance = "FounderApproved", UpdatedUtc = now
            };
            var result = new LegendCurriculumExample
            {
                CurriculumFamilyId = family.Id, TextUnitId = resultUnit.Id, LanguageCode = "en",
                Provenance = "FounderApproved", UpdatedUtc = now
            };
            db.LegendLanguageTextUnits.AddRange(sourceUnit, resultUnit);
            db.LegendCurriculumExamples.AddRange(source, result);
            db.LegendSemanticTransitionEvidence.Add(new LegendSemanticTransitionEvidence
            {
                TransitionSignature = "governed-transition",
                SourceSemanticFrameSignature = "source-frame",
                ResultSemanticFrameSignature = "result-frame",
                SourceSemanticFrame = "{\"state\":\"known\"}",
                ResultSemanticFrame = "{\"state\":\"resolved\"}",
                SourceLanguageCode = "en",
                ResultLanguageCode = "en",
                SourceCurriculumExampleId = source.Id,
                ResultCurriculumExampleId = result.Id,
                IndependentSourceIdentity = $"independent-{index}",
                ContributionState = "Supported",
                IsHumanVerifiedSupport = true,
                Provenance = "FounderApproved",
                UpdatedUtc = now
            });
        }

        db.LegendConnectModelTrainingRuns.Add(new LegendConnectModelTrainingRun
        {
            RunKey = "evaluation-run", DatasetIdentity = "evaluation-dataset", TrainingProvider = "test",
            BaseModel = "test", EvaluationState = "Passed", HeldOutScore = 1m, RegressionScore = 1m,
            FailureDetail = "evaluated=1;reference=1.000000;blocking=0;protected=0;leakage=0;prompt_set=test-v1;code_sha=0123456789abcdef0123456789abcdef01234567;runtime_mode=LockedHeldOutEvaluation;response_authority=LegendConnectActiveModelInference;settings=responses-v1,store=false,max_output_tokens=1200;criteria=governed-reference-policy-v1,held_out>=0.950000,regression>=1.000000,protected>=0.980000,blocking=0,leakage=0,runtime_model=exact;proof_set=abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789;latency_us=1;cost_micro=1",
            CompletedUtc = now, UpdatedUtc = now
        });
        db.LegendTranslationPairDemands.Add(new LegendTranslationPairDemand
        {
            PairKey = "en-es", TranslationRequestCount = 10, TranslationMemoryHitCount = 10, LastRequestedUtc = now
        });
        await db.SaveChangesAsync();

        var service = new LegendIntelligenceEvaluationService(db);
        var measured = await service.CreateEvidenceSnapshotAsync("founder-1", CancellationToken.None);
        var language = measured.Domains.Single(domain => domain.Key == "language_linguistic");
        Assert.Null(language.EvidenceScore);
        Assert.Equal(1, language.ProductionEligibleEvidenceCount);
        Assert.Equal(6, language.EvidenceVolume);
        Assert.Contains("transfer", language.OpenKnowledgeGaps);
        Assert.Contains("native_execution", language.OpenKnowledgeGaps);
        Assert.Contains("calibration", language.OpenKnowledgeGaps);

        var repeated = await service.CreateEvidenceSnapshotAsync("founder-1", CancellationToken.None);
        Assert.Equal(measured.EvaluatedUtc, repeated.EvaluatedUtc);
        Assert.Equal(6, await db.LegendIntelligenceEvaluationSignals.CountAsync());
        Assert.Equal(1, await db.LegendIntelligenceEvaluationSnapshots.CountAsync());
    }

    [Fact]
    public async Task IntelligenceEvaluation_ReportsExactMissingFactorsWithoutInventingAScore()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var service = new LegendIntelligenceEvaluationService(db);

        _ = await service.CreateEvidenceSnapshotAsync("founder-1", CancellationToken.None);
        var contract = await db.LegendIntelligenceEvaluationContracts.SingleAsync();
        db.LegendIntelligenceEvaluationSignals.Add(new LegendIntelligenceEvaluationSignal
        {
            ContractId = contract.Id,
            DomainKey = "language_linguistic",
            MetricKey = "coverage",
            Value = 100m,
            EvidenceAuthority = "canonical-evaluator",
            EvidenceReference = "evaluation-proof-coverage-only",
            State = "Current",
            MeasuredUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var partial = await service.CreateEvidenceSnapshotAsync("founder-1", CancellationToken.None);
        var language = partial.Domains.Single(domain => domain.Key == "language_linguistic");

        Assert.Equal("EvidenceIncomplete", partial.State);
        Assert.Null(partial.DemonstratedIntelligence);
        Assert.Null(language.EvidenceScore);
        Assert.Contains("held_out", language.OpenKnowledgeGaps);
        Assert.Contains("native_execution", language.OpenKnowledgeGaps);
        Assert.Contains("Missing required factors:", partial.Detail, StringComparison.Ordinal);
        Assert.Null(partial.LegendSelfAssessment);
        Assert.Null(partial.OpenAiIndependentAssessment);
    }

    [Fact]
    public async Task SyntheticPreLabeledComparativeSignals_CannotClaimTakeoverAuthority()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var service = new LegendIntelligenceEvaluationService(db);
        _ = await service.CreateEvidenceSnapshotAsync("founder-1", CancellationToken.None);
        var contract = await db.LegendIntelligenceEvaluationContracts.SingleAsync();
        const string authority =
            "legend-locked-blind-comparative-evaluator-v1:gpt-5.6-sol@locked-2026-08-28";
        var metrics = new Dictionary<string, decimal>
        {
            ["sample_size"] = 200m,
            ["blind_win_rate"] = 60m,
            ["blind_win_rate_lower_confidence_bound"] = 51m,
            ["non_inferiority_rate"] = 100m,
            ["adversarial_pass_rate"] = 100m,
            ["unsupported_request_integrity"] = 100m,
            ["prompt_holdout_integrity"] = 100m,
            ["assignment_blinding_integrity"] = 100m,
            ["independent_judge_agreement"] = 90m,
            ["latency_efficiency"] = 60m,
            ["cost_efficiency"] = 60m
        };
        const string suiteIdentity =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        foreach (var domain in LegendIntelligenceEvaluationDomainCatalog.All)
        foreach (var metric in metrics)
        {
            db.LegendIntelligenceEvaluationSignals.Add(new LegendIntelligenceEvaluationSignal
            {
                ContractId = contract.Id,
                DomainKey = domain.Key,
                MetricKey = metric.Key,
                Value = metric.Value,
                EvidenceAuthority = authority,
                EvidenceReference = $"{LegendArchitecturalTakeoverGate.SuiteReferencePrefix}{suiteIdentity}:{domain.Key}:{metric.Key}",
                State = "Current",
                MeasuredUtc = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();

        var proven = await service.CreateEvidenceSnapshotAsync("founder-1", CancellationToken.None);
        Assert.False(proven.TakeoverReadiness.Proven);
        Assert.Equal("BLOCKED", proven.TakeoverReadiness.State);
        Assert.Equal(0, proven.TakeoverReadiness.DomainWins);
        Assert.Null(proven.TakeoverReadiness.BaselineIdentity);
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
    public void ProductionDeploymentWorkflow_IsTheSinglePullRequestValidationMergeAndDeployPath()
    {
        var workflow = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "agentportal-production-deploy.yml"));

        Assert.Contains("pull_request:", workflow, StringComparison.Ordinal);
        Assert.Contains("- production", workflow, StringComparison.Ordinal);
        Assert.Contains("- synchronize", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("- opened", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("- reopened", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("- ready_for_review", workflow, StringComparison.Ordinal);
        Assert.Contains("security:", workflow, StringComparison.Ordinal);
        Assert.Contains("build:", workflow, StringComparison.Ordinal);
        Assert.Contains("merge:", workflow, StringComparison.Ordinal);
        Assert.Contains("migrate:", workflow, StringComparison.Ordinal);
        Assert.Contains("deploy:", workflow, StringComparison.Ordinal);
        Assert.Contains("needs: security", workflow, StringComparison.Ordinal);
        Assert.Contains("Test full suite including security regressions", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet test AgentPortal.Tests/AgentPortal.Tests.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("FounderToolCatalog_SerializedContractIsRecursivelyProviderValid", workflow, StringComparison.Ordinal);
        Assert.Contains("ProviderAcceptanceCanary_LiveProviderAcceptsCompleteZeroWriteCatalog", workflow, StringComparison.Ordinal);
        Assert.Contains("LEGEND_FOUNDER_TOOL_CATALOG_PROVIDER_CANARY", workflow, StringComparison.Ordinal);
        Assert.Contains("run: ./scripts/db.sh validate-artifacts", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("run: ./scripts/db.sh validate\n", workflow, StringComparison.Ordinal);
        Assert.Contains("Merge exact validated PR head", workflow, StringComparison.Ordinal);
        Assert.Contains("Deploy immutable merged production tree", workflow, StringComparison.Ordinal);
        Assert.Contains("verify-legend-native:", workflow, StringComparison.Ordinal);
        Assert.Contains("Download exact tested binaries", workflow, StringComparison.Ordinal);
        Assert.Contains("ProductionReadOnlyNativeProofMatrix", workflow, StringComparison.Ordinal);
        Assert.Contains("LEGEND_PRODUCTION_PROOF_MATRIX_VERSION: lai-027-029-v1", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("verify-legend-convergence:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("LegendProductionConvergenceGate", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("push:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow_dispatch:", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionDeploymentWorkflow_ExcludesReplayAndUsesOneBoundedNativeProofMatrix()
    {
        var workflow = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "agentportal-production-deploy.yml"));

        var verifyStart = workflow.IndexOf("  verify-legend-native:", StringComparison.Ordinal);
        Assert.True(verifyStart >= 0);
        var verify = workflow[verifyStart..];
        Assert.Contains("- deploy", verify, StringComparison.Ordinal);
        Assert.Contains("Download exact tested binaries", verify, StringComparison.Ordinal);
        Assert.Contains("./tested/AgentPortal.Tests.dll", verify, StringComparison.Ordinal);
        Assert.Contains("ProductionReadOnlyNativeProofMatrix", verify, StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName=$testName", verify, StringComparison.Ordinal);
        Assert.Contains("LEGEND_PRODUCTION_PROOF_REQUIRED: 'true'", verify, StringComparison.Ordinal);
        Assert.Contains("Required SQL proof reported zero executed matrix cases.", verify, StringComparison.Ordinal);
        Assert.Contains("$executedTests -ne 1", verify, StringComparison.Ordinal);
        Assert.Contains("$matrixCases -lt 1", verify, StringComparison.Ordinal);
        Assert.Contains("legend-production-matrix-result.json", verify, StringComparison.Ordinal);
        Assert.Contains("ProductionWriteCommandCount", verify, StringComparison.Ordinal);
        Assert.Contains("ProviderClientCount", verify, StringComparison.Ordinal);
        Assert.DoesNotContain("LegendProductionConvergenceGate", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("verify-legend-convergence:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("run_full_shadow", verify, StringComparison.Ordinal);
        Assert.DoesNotContain("live replay", verify, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionDeploymentWorkflow_BindsProofArtifactToCandidateTreeAndDeployedSha()
    {
        var workflow = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "agentportal-production-deploy.yml"));

        Assert.Contains("candidate_sha: ${{ steps.tree.outputs.candidate_sha }}", workflow, StringComparison.Ordinal);
        Assert.Contains("deployed_sha: ${{ steps.identity.outputs.deployed_sha }}", workflow, StringComparison.Ordinal);
        Assert.Contains("CandidateSha = $candidateSha", workflow, StringComparison.Ordinal);
        Assert.Contains("CandidateTree = $candidateTree", workflow, StringComparison.Ordinal);
        Assert.Contains("DeployedSha = $deployedSha", workflow, StringComparison.Ordinal);
        Assert.Contains("if ($deployedSha -ne $mergeSha)", workflow, StringComparison.Ordinal);
        Assert.Contains("legend-production-proof-${{ needs.deploy.outputs.deployed_sha }}-${{ github.run_id }}", workflow, StringComparison.Ordinal);
        Assert.Contains("legend-production-proof-identity.json", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void UnifiedProductionMigrationGate_ValidatesBuiltArtifactsWithoutRebuildingOrRetesting()
    {
        var script = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "db.sh"));
        var start = script.IndexOf("command_validate_artifacts()", StringComparison.Ordinal);
        Assert.True(start >= 0);

        var end = script.IndexOf("command_bundle()", start, StringComparison.Ordinal);
        Assert.True(end > start);

        var command = script[start..end];
        Assert.Contains("check_model", command, StringComparison.Ordinal);
        Assert.Contains("check_migration_integrity", command, StringComparison.Ordinal);
        Assert.DoesNotContain("build_backend", command, StringComparison.Ordinal);
        Assert.DoesNotContain("test_backend", command, StringComparison.Ordinal);
    }

    [Fact]
    public void UnifiedProductionFlow_RecognizesBothCurrentDotnetTestSuccessFormats()
    {
        var workflow = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "agentportal-production-deploy.yml"));

        Assert.Contains("Test Run Successful", workflow, StringComparison.Ordinal);
        Assert.Contains("Passed![[:space:]]+-[[:space:]]+Failed:[[:space:]]+0", workflow, StringComparison.Ordinal);
        Assert.Contains("Failed:[[:space:]]*[1-9][0-9]*", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void LegendConnectPage_KeepsFounderIntelligenceOpenAndCollapsesEveryOtherPanel()
    {
        var page = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "legend-connect-index.cshtml"));

        Assert.Contains("<section class=\"lc-hero\"", page, StringComparison.Ordinal);
        Assert.Contains("FOUNDER LANGUAGE INTELLIGENCE", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<details class=\"lc-hero", page, StringComparison.Ordinal);
        Assert.Equal(
            7,
            page.Split("<details class=\"lc-panel lc-panel-collapse", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            7,
            page.Split("<summary class=\"lc-panel-summary\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("<section class=\"lc-panel", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionReadOnlyDiagnosticWorkflow_IsExplicitlyNonAuthoritativeAndCannotMutateProduction()
    {
        var workflow = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "legend-production-readonly-diagnostic.yml"));

        Assert.Contains("contents: read", workflow, StringComparison.Ordinal);
        Assert.Contains("timeout-minutes: 30", workflow, StringComparison.Ordinal);
        Assert.Contains("cancel-in-progress: true", workflow, StringComparison.Ordinal);
        Assert.Contains("ProductionReadOnlyNativeProofMatrix", workflow, StringComparison.Ordinal);
        Assert.Contains("Authority: non-authoritative", workflow, StringComparison.Ordinal);
        Assert.Contains("DeployedSha: unavailable", workflow, StringComparison.Ordinal);
        Assert.Contains("production proof lives only in agentportal-production-deploy.yml", workflow, StringComparison.Ordinal);
        Assert.Contains("$executedTests -ne 1", workflow, StringComparison.Ordinal);
        Assert.Contains("$matrixCases -lt 1", workflow, StringComparison.Ordinal);
        Assert.Contains("legend-production-matrix-result.json", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "Production read-only diagnostic was not completed successfully.",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("Upload sanitized diagnostic transcript", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("run_full_shadow:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("runFullShadow", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("LegendProductionConvergenceGate", workflow, StringComparison.Ordinal);
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

    private static void AssertClosedSemanticFrameSchema(JsonElement frame)
    {
        Assert.Equal("object", frame.GetProperty("type").GetString());
        Assert.False(frame.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            new[] { "dimensions" },
            frame.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());

        var frameProperties = frame.GetProperty("properties");
        Assert.Equal(
            new[] { "dimensions" },
            frameProperties.EnumerateObject().Select(item => item.Name).ToArray());

        var dimensions = frameProperties.GetProperty("dimensions");
        Assert.Equal("array", dimensions.GetProperty("type").GetString());
        Assert.Equal(1, dimensions.GetProperty("minItems").GetInt32());
        Assert.Equal(12, dimensions.GetProperty("maxItems").GetInt32());
        Assert.False(dimensions.TryGetProperty("additionalProperties", out _));

        var item = dimensions.GetProperty("items");
        Assert.Equal("object", item.GetProperty("type").GetString());
        Assert.False(item.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            new[] { "dimension", "value" },
            item.GetProperty("required")
                .EnumerateArray()
                .Select(required => required.GetString()!)
                .ToArray());

        var properties = item.GetProperty("properties");
        Assert.Equal(
            new[] { "dimension", "value" },
            properties.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("string", properties.GetProperty("dimension").GetProperty("type").GetString());
        Assert.Equal(1, properties.GetProperty("dimension").GetProperty("minLength").GetInt32());
        Assert.Equal(80, properties.GetProperty("dimension").GetProperty("maxLength").GetInt32());
        Assert.Equal("string", properties.GetProperty("value").GetProperty("type").GetString());
        Assert.Equal(1, properties.GetProperty("value").GetProperty("minLength").GetInt32());
        Assert.Equal(160, properties.GetProperty("value").GetProperty("maxLength").GetInt32());
    }

    private static bool TryParseFounderSemanticFrame(
        string json,
        out LegendConnectSemanticFrameSubmission frame)
    {
        var parse = typeof(LegendFounderToolAuthority)
            .GetMethod("TryReadSemanticFrame", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(parse);
        using var document = JsonDocument.Parse(json);
        object?[] arguments = [document.RootElement, "source", null];
        var parsed = Assert.IsType<bool>(parse!.Invoke(null, arguments));
        frame = arguments[2] as LegendConnectSemanticFrameSubmission ?? null!;
        return parsed;
    }

    private static string SemanticFrameJson(
        IEnumerable<(string Dimension, string Value)> dimensions) =>
        JsonSerializer.Serialize(new
        {
            source = new
            {
                dimensions = dimensions.Select(item => new
                {
                    dimension = item.Dimension,
                    value = item.Value
                })
            }
        });

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
