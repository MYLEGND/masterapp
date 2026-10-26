using Infrastructure.Reception;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Analytics;

namespace AgentPortal.Controllers;

public partial class WebsiteAnalyticsController
{
    private ReceptionistOperatorService ReceptionistManagement => HttpContext.RequestServices.GetRequiredService<ReceptionistOperatorService>();
    private async Task<bool> AuthorizeReceptionistManagementAsync(MarketingOwnerScope owner, CancellationToken ct) =>
        User.Identity?.IsAuthenticated == true && owner.CommerceBusinessId is null &&
        await ResolveAdvertisingOwnerAsync(owner.AgentTrackingProfileId, ct) == owner;

    [HttpGet("receptionist/connections")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ReceptionistConnections(Guid? agentProfileId = null, string? notice = null, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        return await ReceptionistManagementPageAsync(owner, agentProfileId,
            notice == "saved" ? "Credential saved securely for review. Calls and accepted configuration were not changed."
                : notice == "revoked" ? "The saved credential was withdrawn from future setup. Existing accepted operation was not changed."
                : notice == "prepared" ? "The proposed operation was saved for review. Calls and provider settings were not changed."
                : notice == "reviewed" ? "The exact operation review was saved. Review and apply the operation separately." : null,
            null, cancellationToken);
    }

    private async Task<IActionResult> ReceptionistManagementPageAsync(MarketingOwnerScope owner, Guid? agentProfileId,
        string? message, ReceptionistOperationReview? review, CancellationToken ct)
    {
        var summary = await ReceptionistManagement.ManagementAsync(owner, AuthorizeReceptionistManagementAsync, ct);
        return View("~/Views/Shared/ReceptionistManagement.cshtml", new ReceptionistManagementPage(summary,
            Url.Action(nameof(SaveReceptionistCredential), new { agentProfileId })!,
            Url.Action(nameof(RevokeReceptionistCredential), new { agentProfileId })!,
            Url.Action(nameof(Receptionist), new { agentProfileId })!, message,
            Url.Action(nameof(PlanReceptionistOperation), new { agentProfileId })!,
            Url.Action(nameof(ApplyReceptionistOperation), new { agentProfileId })!,
            Url.Action(nameof(ReviewReceptionistOperation), new { agentProfileId })!, review, Url.Action(nameof(PrepareReceptionistOperation), new { agentProfileId })!));
    }

    [HttpPost("receptionist/connections/prepare")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(65536)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> PrepareReceptionistOperation([FromForm] ReceptionistOperationPreparationInput input,
        Guid? agentProfileId = null, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        if (!ModelState.IsValid) return BadRequest("Choose valid setup fields and dated evidence.");
        try
        {
            await ReceptionistManagement.PrepareForOwnerAsync(owner, input, AuthorizeReceptionistManagementAsync, cancellationToken);
            return RedirectToAction(nameof(ReceptionistConnections), new { agentProfileId, notice = "prepared" });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DbUpdateConcurrencyException) { return Conflict("Setup changed. Reload before preparing a new operation."); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { return BadRequest("The proposed setup is incomplete or inconsistent. Check its credential, funding and dated evidence, then reload."); }
    }

    [HttpPost("receptionist/connections/plan")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> PlanReceptionistOperation(string key, string sha256, bool pendingReview = false,
        Guid? agentProfileId = null, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(sha256)) return BadRequest("Choose a saved operation.");
        try
        {
            ReceptionistOperationReview review;
            if (pendingReview)
            {
                if (!key.StartsWith("saved-", StringComparison.Ordinal) || !Guid.TryParseExact(key[6..], "N", out var id)) return BadRequest("Choose a saved operation.");
                review = await ReceptionistManagement.PreviewPendingForOwnerAsync(owner, id, sha256, AuthorizeReceptionistManagementAsync, cancellationToken);
            }
            else
            {
                var plan = await ReceptionistManagement.PlanForOwnerAsync(owner, key, sha256, AuthorizeReceptionistManagementAsync, cancellationToken);
                review = new(plan.PackageKey, plan.PackageSha256, plan.Label, plan.Operation, plan.CanApply, plan.Blockers,
                    plan.Facts.Select(x => new ReceptionistOperationFact(x.Label, x.Value)).ToArray(), plan.ExpectedStateToken);
            }
            return await ReceptionistManagementPageAsync(owner, agentProfileId, null, review, cancellationToken);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or DbUpdateConcurrencyException)
        { return BadRequest("This operation is unavailable, expired or no longer matches the saved configuration, credentials or accounting state. Reload setup and prepare a current review."); }
    }

    [HttpPost("receptionist/connections/review")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ReviewReceptionistOperation(Guid id, Guid expectedRevision, string sha256, bool accepted,
        Guid? agentProfileId = null, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        try
        {
            await ReceptionistManagement.ReviewForOwnerAsync(owner, id, expectedRevision, sha256, accepted, AuthorizeReceptionistManagementAsync, cancellationToken);
            return RedirectToAction(nameof(ReceptionistConnections), new { agentProfileId, notice = "reviewed" });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or DbUpdateConcurrencyException)
        { return Conflict("The exact review is no longer current. Reload and review again."); }
    }

    [HttpPost("receptionist/connections/apply")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(16384)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ApplyReceptionistOperation([FromForm] ReceptionistOperatorApplyRequest input,
        Guid? agentProfileId = null, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        if (!ModelState.IsValid) return BadRequest("A current exact-change preview is required.");
        try
        {
            var receipt = await ReceptionistManagement.ApplyForOwnerAsync(owner, input, AuthorizeReceptionistManagementAsync, cancellationToken);
            return await ReceptionistManagementPageAsync(owner, agentProfileId,
                receipt.ReadBackVerified ? "The operation was verified by persisted readback. Live provider and conversational acceptance remain separate."
                    : "The result is not yet verified. Keep the same operation and reconcile its existing result before retrying.", null, cancellationToken);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or DbUpdateConcurrencyException)
        { return Conflict("The preview or its authorization changed. Reload and review the exact operation again."); }
    }

    [HttpPost("receptionist/connections/credential")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(32768)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> SaveReceptionistCredential([FromForm] ReceptionistCredentialInput input,
        Guid? agentProfileId = null, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        if (!ModelState.IsValid) return BadRequest("Choose a valid project and credential type.");
        try
        {
            await ReceptionistManagement.SaveCredentialAsync(owner, input, AuthorizeReceptionistManagementAsync, cancellationToken);
            return RedirectToAction(nameof(ReceptionistConnections), new { agentProfileId, notice = "saved" });
        }
        catch (DbUpdateConcurrencyException) { return Conflict("Setup changed. Reload the connections page before saving."); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return BadRequest("The project or credential is invalid, or receptionist settings have not been saved."); }
    }

    [HttpPost("receptionist/connections/withdraw")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> RevokeReceptionistCredential(Guid credentialId, Guid expectedConnectionRevision,
        Guid? agentProfileId = null, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        try
        {
            await ReceptionistManagement.RevokeCredentialAsync(owner, credentialId, expectedConnectionRevision, AuthorizeReceptionistManagementAsync, cancellationToken);
            return RedirectToAction(nameof(ReceptionistConnections), new { agentProfileId, notice = "revoked" });
        }
        catch (DbUpdateConcurrencyException) { return Conflict("Setup changed. Reload before withdrawing a credential."); }
        catch (InvalidOperationException) { return BadRequest("The saved credential is unavailable in this workspace."); }
    }
}
