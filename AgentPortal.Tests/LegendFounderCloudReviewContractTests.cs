using System.Text.Json;
using AgentPortal.Controllers;
using AgentPortal.Security;
using AgentPortal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFounderCloudReviewContractTests
{
    [Fact]
    public void Existing_staging_receipt_is_recognized_without_fabricating_ok()
    {
        using var receipt = JsonDocument.Parse(JsonSerializer.Serialize(new {
            capability = "prepare_software_repair", prepared = true, state = "STAGED_UNPUBLISHED",
            deployment = "not_requested", branch = "hotfix/staging-batch",
            repairCommitSha = new string('a',40), baseSha = new string('b',40), pullRequestNumber = 42
        }));
        Assert.True(LegendFounderAiConversationService.IsVerifiedRepairStagingReceipt(receipt.RootElement));
    }

    [Theory]
    [InlineData("{\"ok\":true}")]
    [InlineData("{\"prepared\":true,\"state\":\"STAGED_UNPUBLISHED\"}")]
    [InlineData("{\"capability\":42}")]
    [InlineData("[]")]
    public void Model_success_flags_and_incomplete_receipts_do_not_prove_staging(string json)
    {
        using var receipt = JsonDocument.Parse(json);
        Assert.False(LegendFounderAiConversationService.IsVerifiedRepairStagingReceipt(receipt.RootElement));
    }

    [Fact]
    public void Approval_boundary_requires_authenticated_founder_and_antiforgery()
    {
        var controller = typeof(LegendFounderAiController);
        Assert.NotEmpty(controller.GetCustomAttributes(typeof(AuthorizeAttribute), true));
        Assert.NotEmpty(controller.GetCustomAttributes(typeof(FounderOnlyAttribute), true));
        var action = controller.GetMethod(nameof(LegendFounderAiController.ApproveAction))!;
        Assert.NotEmpty(action.GetCustomAttributes(typeof(HttpPostAttribute), true));
        Assert.NotEmpty(action.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        Assert.DoesNotContain(action.GetParameters(), p => p.Name is "arguments" or "toolName" or "confirmed");
    }

    [Fact]
    public void Hosted_instructions_disclose_authority_and_exact_action_requirements()
    {
        var instructions = LegendFounderAiConversationService.BuildInstructions("legend", "en", "en", true);
        Assert.Contains("Cloudflare Workers AI", instructions);
        Assert.Contains("GitHub owns approved code changes and hosted validation", instructions);
        Assert.Contains("Model-generated confirmation flags never authorize writes", instructions);
        Assert.Contains("without Markdown fences", instructions);
    }
}
