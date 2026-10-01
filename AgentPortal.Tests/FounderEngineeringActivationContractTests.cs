using System;
using System.IO;
using System.Reflection;
using AgentPortal.Controllers;
using AgentPortal.Models;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentPortal.Tests;

public sealed class FounderEngineeringActivationContractTests
{
    [Fact]
    public void FounderPage_ExposesCanonicalActivationWithoutRawCredentials()
    {
        var root = SourceRoot();
        var view = File.ReadAllText(Path.Combine(
            root, "AgentPortal", "Views", "FounderEngineering", "Index.cshtml"));
        var model = File.ReadAllText(Path.Combine(
            root, "AgentPortal", "Models", "FounderEngineeringCommandCenterViewModel.cs"));

        Assert.Contains("ConnectChatGpt", view, StringComparison.Ordinal);
        Assert.Contains("SaveChatGptClient", view, StringComparison.Ordinal);
        Assert.Contains("AutonomousEngineeringEnabled", view, StringComparison.Ordinal);
        Assert.Contains("HeadGptModel", view, StringComparison.Ordinal);
        Assert.Contains("CodexModel", view, StringComparison.Ordinal);
        Assert.Contains("ReviewerModel", view, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", view, StringComparison.Ordinal);

        Assert.DoesNotContain("AccessToken", model, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshToken", model, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", model, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientSecretCiphertext", model, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatGptPlanAuthorization_UsesPkceOidcAndRequiredPlanScopes()
    {
        var source = File.ReadAllText(Path.Combine(
            SourceRoot(), "AgentPortal", "Services", "Engineering",
            "LegendChatGptPlanCredentialAuthority.cs"));

        Assert.Contains("code_challenge_method", source, StringComparison.Ordinal);
        Assert.Contains("S256", source, StringComparison.Ordinal);
        Assert.Contains("offline_access resource.invoke chatgpt.tokens.use.direct", source, StringComparison.Ordinal);
        Assert.Contains("authorization_response_iss_parameter_supported", source, StringComparison.Ordinal);
        Assert.Contains("ValidateIdTokenAsync", source, StringComparison.Ordinal);
        Assert.Contains("ValidateAudience = true", source, StringComparison.Ordinal);
        Assert.Contains("ValidateIssuer = true", source, StringComparison.Ordinal);
        Assert.Contains("nonce", source, StringComparison.Ordinal);
        Assert.Contains("IDataProtector", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENAI_API_KEY", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanExecution_UsesResponsesStreamingAndAccountModelCatalog()
    {
        var source = File.ReadAllText(Path.Combine(
            SourceRoot(), "AgentPortal", "Services", "Engineering",
            "ChatGptPlanResponsesAdapter.cs"));

        Assert.Contains("https://api.openai.com/v1/models", source, StringComparison.Ordinal);
        Assert.Contains("https://api.openai.com/v1/responses", source, StringComparison.Ordinal);
        Assert.Contains("store = false", source, StringComparison.Ordinal);
        Assert.Contains("stream = true", source, StringComparison.Ordinal);
        Assert.Contains("type = \"json_schema\"", source, StringComparison.Ordinal);
        Assert.Contains("response.completed", source, StringComparison.Ordinal);
        Assert.Contains("subscription_sharing_usage_limit_exceeded", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENAI_API_KEY", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1/agents", source, StringComparison.Ordinal);
        Assert.DoesNotContain("app-server", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutonomousActivation_IsOwnedByOperationalContract_NotDeploymentFlag()
    {
        var root = SourceRoot();
        var hosted = File.ReadAllText(Path.Combine(
            root, "AgentPortal", "Services", "Engineering",
            "LegendEngineeringHostedService.cs"));
        var orchestrator = File.ReadAllText(Path.Combine(
            root, "AgentPortal", "Services", "Engineering",
            "LegendEngineeringOrchestrator.cs"));
        var authority = File.ReadAllText(Path.Combine(
            root, "AgentPortal", "Services", "Engineering",
            "LegendEngineeringContractAuthority.cs"));

        Assert.DoesNotContain("LegendEngineering:Autonomous:Enabled", hosted, StringComparison.Ordinal);
        Assert.DoesNotContain("LegendEngineering:Autonomous:Enabled", orchestrator, StringComparison.Ordinal);
        Assert.Contains("AutonomousEngineeringEnabled", hosted, StringComparison.Ordinal);
        Assert.Contains("AutonomousEngineeringEnabled", orchestrator, StringComparison.Ordinal);
        Assert.Contains("AutonomousEngineeringEnabled", authority, StringComparison.Ordinal);
        Assert.Contains("HeadGptModel", authority, StringComparison.Ordinal);
        Assert.Contains("CodexModel", authority, StringComparison.Ordinal);
        Assert.Contains("ReviewerModel", authority, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivationMigration_ExtendsExistingAuthoritiesOnly()
    {
        var source = File.ReadAllText(Path.Combine(
            SourceRoot(), "Infrastructure", "Migrations",
            "20261001193000_ExtendFounderEngineeringActivation.cs"));

        Assert.Contains("LegendEngineeringOperationalContract", source, StringComparison.Ordinal);
        Assert.Contains("LegendEngineeringOperationalContractHistory", source, StringComparison.Ordinal);
        Assert.Contains("LegendEngineeringChatGptPlanClientRegistration", source, StringComparison.Ordinal);
        Assert.Contains("LegendEngineeringChatGptPlanOAuthTransactions", source, StringComparison.Ordinal);
        Assert.Contains("AutonomousEngineeringEnabled", source, StringComparison.Ordinal);
        Assert.Contains("HeadGptModel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTable(\n                name: \"LegendEngineeringWorkItems\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FounderActivationMutations_ArePostAndAntiforgery_WhileOAuthCallbackIsGet()
    {
        var type = typeof(FounderEngineeringController);
        foreach (var name in new[]
                 {
                     nameof(FounderEngineeringController.SaveChatGptClient),
                     nameof(FounderEngineeringController.ConnectChatGpt),
                     nameof(FounderEngineeringController.DisconnectChatGpt)
                 })
        {
            var method = type.GetMethod(name)!;
            Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
            Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        }

        var callback = type.GetMethod(nameof(FounderEngineeringController.ChatGptCallback))!;
        Assert.NotNull(callback.GetCustomAttribute<HttpGetAttribute>());
        Assert.Null(callback.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

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
