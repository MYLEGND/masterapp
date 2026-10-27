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
        string? message, ReceptionistOperationReview? review, CancellationToken ct,
        ReceptionistOperationPreparationInput? preparation = null, ReceptionistCredentialDraft? credential = null,
        ReceptionistOperationReconciliation? reconciliation = null, bool allowUnverifiedReceiptFallback = false)
    {
        ReceptionistManagementSummary summary;
        try { summary = await ReceptionistManagement.ManagementAsync(owner, AuthorizeReceptionistManagementAsync, ct); }
        catch (Exception error) when (allowUnverifiedReceiptFallback && reconciliation is not null &&
            error is TimeoutException or System.Data.Common.DbException or DbUpdateException)
        {
            // An unavailable summary cannot erase the exact unresolved receipt. No protected
            // data or enabled controls are fabricated; authorization denials are not caught.
            summary = new(Guid.Empty, [], []);
        }
        return View("~/Views/Shared/ReceptionistManagement.cshtml", new ReceptionistManagementPage(summary,
            Url.Action(nameof(SaveReceptionistCredential), new { agentProfileId })!,
            Url.Action(nameof(RevokeReceptionistCredential), new { agentProfileId })!,
            Url.Action(nameof(Receptionist), new { agentProfileId })!, message,
            Url.Action(nameof(PlanReceptionistOperation), new { agentProfileId })!,
            Url.Action(nameof(ApplyReceptionistOperation), new { agentProfileId })!,
            Url.Action(nameof(ReviewReceptionistOperation), new { agentProfileId })!, review, Url.Action(nameof(PrepareReceptionistOperation), new { agentProfileId })!,
            Url.Action(nameof(GrantReceptionistTest), new { agentProfileId })!, Url.Action(nameof(StartReceptionistTest), new { agentProfileId })!,
            SubmittedPreparation: preparation, SubmittedCredential: credential, Reconciliation: reconciliation, SelfUrl: Url.Action(nameof(ReceptionistConnections), new { agentProfileId }),
            AdvancedUrl: summary.CanReview ? Url.Action("Receptionist", "FounderEngineering") : null));
    }

    private async Task<IActionResult> ReceptionistManagementFailureAsync(MarketingOwnerScope owner, Guid? scope,
        ReceptionistPlanFailure failure, int status, CancellationToken ct,
        ReceptionistOperationPreparationInput? preparation = null, ReceptionistCredentialDraft? credential = null,
        ReceptionistOperationReconciliation? reconciliation = null)
    {
        foreach (var key in ModelState.Keys.Where(k => k.Equals("Secret", StringComparison.OrdinalIgnoreCase) ||
            k.EndsWith(".Secret", StringComparison.OrdinalIgnoreCase)).ToArray()) ModelState.Remove(key);
        if (!await AuthorizeReceptionistManagementAsync(owner, ct)) return Forbid();
        Response.StatusCode = status;
        ViewData["ReceptionistPlanFailure"] = failure;
        try { return await ReceptionistManagementPageAsync(owner, scope, null, null, ct, preparation, credential, reconciliation); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    private ReceptionistBrowserController ReceptionistMedia => new(HttpContext.RequestServices)
        { ControllerContext = ControllerContext };

    [HttpPost("receptionist/connections/test/grant"), ValidateAntiForgeryToken, RequestSizeLimit(4096)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GrantReceptionistTest(Guid? agentProfileId = null, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        return await ReceptionistMedia.GrantForOwnerAsync(owner, AuthorizeReceptionistManagementAsync, cancellationToken);
    }

    [HttpPost("receptionist/connections/test/start"), ValidateAntiForgeryToken, RequestSizeLimit(73728)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> StartReceptionistTest([FromBody] ReceptionistBrowserController.StartRequest request,
        Guid? agentProfileId = null, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        return await ReceptionistMedia.StartForOwnerAsync(owner, request, AuthorizeReceptionistManagementAsync, cancellationToken);
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
        if (!ModelState.IsValid) return await ReceptionistManagementFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.InvalidPreparation, 400, cancellationToken, preparation: input);
        try
        {
            await ReceptionistManagement.PrepareForOwnerAsync(owner, input, AuthorizeReceptionistManagementAsync, cancellationToken);
            return RedirectToAction(nameof(ReceptionistConnections), new { agentProfileId, notice = "prepared" });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DbUpdateConcurrencyException) { return await ReceptionistManagementFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.StalePreparation, 409, cancellationToken, preparation: input); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { return await ReceptionistManagementFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.InvalidPreparation, 400, cancellationToken, preparation: input); }
    }

    [HttpPost("receptionist/connections/plan")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> PlanReceptionistOperation(string key, string sha256, bool pendingReview = false,
        Guid? agentProfileId = null, CancellationToken cancellationToken = default, bool reconcile = false)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        if (!ModelState.IsValid || !ReceptionistOperationReview.IsSavedIdentity(key, sha256)) return await ReceptionistManagementFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.ChooseOperation, 400, cancellationToken);
        var reconciliation = reconcile ? new ReceptionistOperationReconciliation(key, sha256) : null;
        try
        {
            ReceptionistOperationReview review;
            if (pendingReview)
            {
                if (!key.StartsWith("saved-", StringComparison.Ordinal) || !Guid.TryParseExact(key[6..], "N", out var id)) return await ReceptionistManagementFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.ChooseOperation, 400, cancellationToken);
                review = await ReceptionistManagement.PreviewPendingForOwnerAsync(owner, id, sha256, AuthorizeReceptionistManagementAsync, cancellationToken);
            }
            else
            {
                var plan = await ReceptionistManagement.PlanForOwnerAsync(owner, key, sha256, AuthorizeReceptionistManagementAsync, cancellationToken);
                if (!plan.CanApply && plan.Blockers.Contains(ReceptionistPlanFailure.CurrentProjectRequired.Message, StringComparer.Ordinal))
                {
                    Response.StatusCode = 400;
                    ViewData["ReceptionistPlanFailure"] = ReceptionistPlanFailure.CurrentProjectRequired;
                }
                review = new(plan.PackageKey, plan.PackageSha256, plan.Label, plan.Operation, plan.CanApply, plan.Blockers,
                    plan.Facts.Select(x => new ReceptionistOperationFact(x.Label, x.Value)).ToArray(), plan.ExpectedStateToken);
            }
            return await ReceptionistManagementPageAsync(owner, agentProfileId, null, review, cancellationToken, reconciliation: reconciliation);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ReceptionistCurrentProjectRequiredException)
        {
            return await ReceptionistManagementFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.CurrentProjectRequired, 400, cancellationToken, reconciliation: reconciliation);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or DbUpdateConcurrencyException)
        {
            return await ReceptionistManagementFailureAsync(owner, agentProfileId, reconciliation is null ? ReceptionistPlanFailure.StaleOrUnavailable : ReceptionistPlanFailure.UnconfirmedOperation, 409, cancellationToken, reconciliation: reconciliation);
        }
    }

    [HttpPost("receptionist/connections/review")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ReviewReceptionistOperation(Guid id, Guid expectedRevision, string sha256, bool accepted,
        Guid? agentProfileId = null, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        if (!ModelState.IsValid || expectedRevision == Guid.Empty || !ReceptionistOperationReview.IsSavedIdentity("saved-" + id.ToString("N"), sha256))
            return await ReceptionistManagementFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.ChooseOperation, 400, cancellationToken);
        try
        {
            await ReceptionistManagement.ReviewForOwnerAsync(owner, id, expectedRevision, sha256, accepted, AuthorizeReceptionistManagementAsync, cancellationToken);
            return RedirectToAction(nameof(ReceptionistConnections), new { agentProfileId, notice = "reviewed" });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ReceptionistCurrentProjectRequiredException)
        {
            return await ReceptionistManagementFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.CurrentProjectRequired, 400, cancellationToken);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or DbUpdateConcurrencyException)
        {
            return await ReceptionistManagementFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.StaleOrUnavailable, 409, cancellationToken);
        }
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
        if (!ModelState.IsValid || !ReceptionistOperationReview.IsSavedIdentity(input.PackageKey, input.PackageSha256) ||
            string.IsNullOrWhiteSpace(input.ExpectedStateToken) || input.ExpectedStateToken.Length > 4096) return await ReceptionistManagementFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.InvalidApply, 400, cancellationToken);
        try
        {
            var receipt = await ReceptionistManagement.ApplyForOwnerAsync(owner, input, AuthorizeReceptionistManagementAsync, cancellationToken);
            return await ReceptionistManagementPageAsync(owner, agentProfileId,
                receipt.ReadBackVerified ? "The operation was verified by persisted readback. Live provider and conversational acceptance remain separate."
                    : "The result is not yet verified. Keep the same operation and reconcile its existing result before retrying.", null, cancellationToken,
                reconciliation: receipt.ReadBackVerified ? null : new(receipt.PackageKey, receipt.PackageSha256),
                allowUnverifiedReceiptFallback: !receipt.ReadBackVerified);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or DbUpdateConcurrencyException)
        { return await ReceptionistManagementFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.StaleApply, 409, cancellationToken); }
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
        var draft = new ReceptionistCredentialDraft(input.ExpectedConnectionRevision, input.ProviderProjectKey, input.Purpose);
        if (!ModelState.IsValid) return await ReceptionistManagementFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.InvalidCredential, 400, cancellationToken, credential: draft);
        try
        {
            await ReceptionistManagement.SaveCredentialAsync(owner, input, AuthorizeReceptionistManagementAsync, cancellationToken);
            return RedirectToAction(nameof(ReceptionistConnections), new { agentProfileId, notice = "saved" });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DbUpdateConcurrencyException) { return await ReceptionistManagementFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.StaleCredential, 409, cancellationToken, credential: draft); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return await ReceptionistManagementFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.InvalidCredential, 400, cancellationToken, credential: draft); }
    }

    [HttpPost("receptionist/connections/withdraw")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> RevokeReceptionistCredential(Guid credentialId, Guid expectedConnectionRevision,
        Guid? agentProfileId = null, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        if (!ModelState.IsValid || credentialId == Guid.Empty || expectedConnectionRevision == Guid.Empty)
            return await ReceptionistManagementFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.InvalidWithdrawal, 400, cancellationToken);
        try
        {
            await ReceptionistManagement.RevokeCredentialAsync(owner, credentialId, expectedConnectionRevision, AuthorizeReceptionistManagementAsync, cancellationToken);
            return RedirectToAction(nameof(ReceptionistConnections), new { agentProfileId, notice = "revoked" });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DbUpdateConcurrencyException) { return await ReceptionistManagementFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.StaleWithdrawal, 409, cancellationToken); }
        catch (InvalidOperationException) { return await ReceptionistManagementFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.InvalidWithdrawal, 400, cancellationToken); }
    }
}
