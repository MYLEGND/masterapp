using AgentPortal.Models;
using AgentPortal.Security;
using Infrastructure.Reception;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPortal.Controllers;

public sealed partial class FounderEngineeringController
{
    [HttpGet("receptionist/preparation")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ReceptionistPreparation(string expectedOwnerKey, Guid configurationId,
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        try
        {
            var observation = await ReceptionistOperator.PrepareAsync(expectedOwnerKey, configurationId, cancellationToken);
            return Json(observation);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
        {
            return BadRequest(new { error = "The exact current configuration and scoped metadata could not be verified. Reload the setup and check its owner and configuration identity." });
        }
    }

    [HttpGet("receptionist")]
    public async Task<IActionResult> Receptionist(CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        return await ReceptionistPageAsync(cancellationToken);
    }

    [HttpPost("receptionist/plan")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> PlanReceptionist(
        [FromForm] FounderReceptionistPlanInput input, CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        if (!ModelState.IsValid)
        {
            Response.StatusCode = 400;
            return await ReceptionistPageAsync(cancellationToken,
                error: "Select a current reviewed operation before requesting its preview.");
        }
        try
        {
            var plan = await ReceptionistOperator.PlanAsync(input.PackageKey, input.PackageSha256, cancellationToken);
            return await ReceptionistPageAsync(cancellationToken, plan: plan);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DbUpdateConcurrencyException)
        {
            Response.StatusCode = 409;
            return await ReceptionistPageAsync(cancellationToken,
                error: "This operation or its current state changed. Select it again to review a fresh preview.");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or OverflowException)
        {
            Response.StatusCode = 400;
            return await ReceptionistPageAsync(cancellationToken,
                error: "This operation cannot be previewed with the available configuration and evidence. Review its setup and reload.");
        }
    }

    [HttpPost("receptionist/apply")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> ApplyReceptionist(
        [FromForm] FounderReceptionistApplyInput input, CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        if (!ModelState.IsValid)
        {
            Response.StatusCode = 400;
            return await ReceptionistPageAsync(cancellationToken,
                error: "A complete current preview is required before applying an operation.");
        }
        try
        {
            var receipt = await ReceptionistOperator.ApplyAsync(new(
                input.PackageKey, input.PackageSha256, input.ExpectedStateToken), cancellationToken);
            return await ReceptionistPageAsync(cancellationToken, receipt: receipt);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DbUpdateConcurrencyException)
        {
            Response.StatusCode = 409;
            return await ReceptionistPageAsync(cancellationToken,
                error: "The reviewed state changed. Review the current durable state in a fresh preview before making another change.");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or OverflowException)
        {
            Response.StatusCode = 400;
            return await ReceptionistPageAsync(cancellationToken,
                error: "The operation result could not be confirmed. Review the current state and evidence before making another change.");
        }
    }

    private ReceptionistOperatorService ReceptionistOperator =>
        HttpContext.RequestServices.GetRequiredService<ReceptionistOperatorService>();

    private async Task<IActionResult> ReceptionistPageAsync(CancellationToken cancellationToken,
        ReceptionistOperatorPlan? plan = null, ReceptionistOperatorReceipt? receipt = null, string? error = null)
    {
        IReadOnlyList<ReceptionistOperatorPackageSummary> packages = [];
        try { packages = await ReceptionistOperator.CatalogAsync(cancellationToken); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or OverflowException)
        {
            if (plan is null && receipt is null && Response.StatusCode < 400) Response.StatusCode = 503;
            error ??= "Reviewed operations are unavailable. Verify the existing operator configuration and reload this page.";
        }
        return View("~/Views/FounderEngineering/Receptionist.cshtml",
            new FounderReceptionistOperatorViewModel(packages, plan, receipt, error));
    }
}
