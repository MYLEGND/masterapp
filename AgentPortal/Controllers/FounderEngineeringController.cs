using System.Text.Json;
using AgentPortal.Models;
using AgentPortal.Security;
using AgentPortal.Services.Engineering;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers;

[Authorize]
[FounderOnly]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
[Route("founder/engineering")]
public sealed class FounderEngineeringController(
    ILegendEngineeringContractAuthority contractAuthority,
    ILegendEngineeringOrchestrator orchestrator,
    ILegendChatGptPlanCredentialAuthority credentials,
    IConfiguration configuration) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);

        var contract = await contractAuthority.GetCurrentAsync(cancellationToken);
        var history = await contractAuthority.GetHistoryAsync(10, cancellationToken);
        var credential = await credentials.GetAsync(cancellationToken);
        var status = JsonSerializer.SerializeToElement(
            await orchestrator.GetStatusAsync(cancellationToken),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var model = new FounderEngineeringCommandCenterViewModel
        {
            Revision = contract.Revision,
            Version = contract.Version,
            ModelExecutionEnabled = contract.ModelExecutionEnabled,
            SharedDirective = contract.SharedDirective,
            HeadGptDirective = contract.HeadGptDirective,
            CodexDirective = contract.CodexDirective,
            ReviewerDirective = contract.ReviewerDirective,
            UpdatedUtc = contract.UpdatedUtc,
            UpdatedBy = contract.UpdatedBy,

            ChatGptPlanReady = credential.Ready,
            ChatGptPlanCode = credential.Code,
            ChatGptPlanApprovedClient = credential.PrivateClientApproved,
            ChatGptPlanScopeGranted = credential.GrantedScopes.Contains(
                "chatgpt.tokens.use.direct", StringComparer.Ordinal),
            ChatGptPlanExpiresUtc = credential.ExpiresUtc,
            CodexAppServerConfigured = !string.IsNullOrWhiteSpace(
                configuration["LegendEngineering:ChatGptPlan:CodexExecutable"]),
            AdapterEnabled = configuration.GetValue<bool?>("LegendEngineering:ChatGptPlan:Enabled") == true,
            AutonomousConfigEnabled = configuration.GetValue<bool?>("LegendEngineering:Autonomous:Enabled") == true,
            HeadGptModel = configuration[$"LegendEngineering:ChatGptPlan:Models:{Domain.Engineering.EngineeringModelTier.DeepReasoning}"] ?? "Not configured",
            CodexModel = configuration[$"LegendEngineering:ChatGptPlan:Models:{Domain.Engineering.EngineeringModelTier.CodeImplementation}"] ?? "Not configured",
            ReviewerModel = configuration[$"LegendEngineering:ChatGptPlan:Models:{Domain.Engineering.EngineeringModelTier.IndependentReview}"] ?? "Not configured",

            OpenWorkItems = ReadInt(status, "openWorkItems"),
            LeasedWorkItems = ReadInt(status, "leasedWorkItems"),
            SecurityReviewItems = ReadInt(status, "securityReviewItems"),
            History = history.Select(row => new FounderEngineeringContractHistoryItem(
                row.Revision,
                row.Version,
                row.ModelExecutionEnabled,
                row.UpdatedUtc,
                row.UpdatedBy)).ToArray()
        };

        return View(model);
    }

    [HttpPost("contract")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveContract(
        FounderEngineeringContractInput input,
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        try
        {
            var updated = await contractAuthority.UpdateAsync(
                input.ExpectedRevision,
                input.ModelExecutionEnabled,
                input.SharedDirective,
                input.HeadGptDirective,
                input.CodexDirective,
                input.ReviewerDirective,
                "Founder",
                cancellationToken);
            TempData["FounderEngineeringSuccess"] =
                $"Operational contract v{updated.Version} is live. New agent contexts are bound to revision {Short(updated.Revision)}.";
        }
        catch (InvalidOperationException exception)
        {
            TempData["FounderEngineeringError"] = ErrorMessage(exception.Message);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("contract/restore")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RestoreContract(
        FounderEngineeringRestoreInput input,
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        try
        {
            var restored = await contractAuthority.RestoreAsync(
                input.ExpectedRevision,
                input.Revision,
                "Founder",
                cancellationToken);
            TempData["FounderEngineeringSuccess"] =
                $"Revision {Short(input.Revision)} was restored as new live contract v{restored.Version}.";
        }
        catch (InvalidOperationException exception)
        {
            TempData["FounderEngineeringError"] = ErrorMessage(exception.Message);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("chatgpt/disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisconnectChatGpt(CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        await credentials.DisconnectAsync(cancellationToken);
        TempData["FounderEngineeringSuccess"] =
            "ChatGPT-plan authorization was disconnected. Deterministic monitoring continues; no new model execution can start until reconnected.";
        return RedirectToAction(nameof(Index));
    }

    private static int ReadInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var result)
            ? result
            : 0;

    private static string Short(string value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" :
        value.Length <= 10 ? value : value[..10];

    private static string ErrorMessage(string code) => code switch
    {
        "engineering_operational_contract_changed" =>
            "The live contract changed before this save completed. Reload and apply your edits to the newest revision.",
        "engineering_contract_too_large" =>
            "The combined operating directives exceed the safe contract size.",
        "engineering_contract_directive_invalid" =>
            "One directive contains unsupported control characters or exceeds its safe size.",
        "engineering_contract_restore_revision_not_found" =>
            "That historical revision is no longer available.",
        _ => "The contract change was rejected by the canonical engineering authority."
    };
}
