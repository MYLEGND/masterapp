using System.Linq;
using System.Reflection;
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
    public void FounderAdapter_ExposesOnlyExistingGovernedLearningEntryPoints()
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
}
