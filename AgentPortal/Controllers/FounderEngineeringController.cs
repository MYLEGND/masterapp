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
    IFounderEngineeringCommandCenterService commandCenter) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        return View(await commandCenter.GetAsync(cancellationToken));
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
            var updated = await commandCenter.SaveAsync(input, cancellationToken);
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
            var restored = await commandCenter.RestoreAsync(input, cancellationToken);
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
        await commandCenter.DisconnectChatGptAsync(cancellationToken);
        TempData["FounderEngineeringSuccess"] =
            "ChatGPT-plan authorization was disconnected. Deterministic monitoring continues; no new model execution can start until reconnected.";
        return RedirectToAction(nameof(Index));
    }

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
