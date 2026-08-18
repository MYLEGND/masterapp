using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AgentPortal.Controllers;
using AgentPortal.Security;
using AgentPortal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
                        tool.GetProperty("name")
                            .GetString() ==
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

}
