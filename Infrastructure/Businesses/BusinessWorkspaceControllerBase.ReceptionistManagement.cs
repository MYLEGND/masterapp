using Infrastructure.Reception;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Analytics;

namespace Infrastructure.Businesses;

public abstract partial class BusinessWorkspaceControllerBase
{
    private ReceptionistOperatorService ReceptionistManagement => HttpContext.RequestServices.GetRequiredService<ReceptionistOperatorService>();
    private async Task<bool> AuthorizeReceptionistManagementAsync(MarketingOwnerScope owner, CancellationToken ct) =>
        User.Identity?.IsAuthenticated == true && owner.CommerceBusinessId is { } businessId &&
        await ResolveBusinessAsync(businessId, "settings", ct) is not null;

    [HttpGet("receptionist/connections")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ReceptionistConnections(Guid businessId = default, string? notice = null, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var owner = MarketingOwnerScope.Business(businessId);
        return await ReceptionistManagementPageAsync(owner, businessId,
            notice == "saved" ? "Credential saved securely for review. Calls and accepted configuration were not changed."
                : notice == "revoked" ? "The saved credential was withdrawn from future setup. Existing accepted operation was not changed."
                : notice == "prepared" ? "The proposed operation was saved for review. Calls and provider settings were not changed."
                : notice == "reviewed" ? "The exact operation review was saved. Review and apply the operation separately." : null,
            null, cancellationToken);
    }

    private async Task<IActionResult> ReceptionistManagementPageAsync(MarketingOwnerScope owner, Guid businessId,
        string? message, ReceptionistOperationReview? review, CancellationToken ct,
        ReceptionistOperationPreparationInput? preparation = null, ReceptionistCredentialDraft? credential = null,
        ReceptionistOperationReconciliation? reconciliation = null, bool allowUnverifiedReceiptFallback = false)
    {
        ViewData["ReceptionistBusinessId"] = businessId;
        ReceptionistManagementSummary summary;
        try { summary = await ReceptionistManagement.ManagementAsync(owner, AuthorizeReceptionistManagementAsync, ct); }
        catch (Exception error) when (allowUnverifiedReceiptFallback && reconciliation is not null &&
            error is TimeoutException or System.Data.Common.DbException or DbUpdateException)
        {
            // An unavailable summary cannot erase the exact unresolved receipt. No protected
            // data or enabled controls are fabricated; authorization denials are not caught.
            summary = new(Guid.Empty, [], []);
        }
        var founderRouteAvailable = HttpContext.RequestServices.GetService<IActionDescriptorCollectionProvider>()?.ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>().Any(x => x.ControllerName == "FounderEngineering" && x.ActionName == "Receptionist") == true;
        return View("~/Views/Shared/ReceptionistManagement.cshtml", new ReceptionistManagementPage(summary,
            Url.Action(nameof(SaveReceptionistCredential), new { businessId })!,
            Url.Action(nameof(RevokeReceptionistCredential), new { businessId })!,
            Url.Action(nameof(Receptionist), new { businessId })!, message,
            Url.Action(nameof(PlanReceptionistOperation), new { businessId })!,
            Url.Action(nameof(ApplyReceptionistOperation), new { businessId })!,
            Url.Action(nameof(ReviewReceptionistOperation), new { businessId })!, review, Url.Action(nameof(PrepareReceptionistOperation), new { businessId })!,
            Url.Action(nameof(GrantReceptionistTest), new { businessId })!, Url.Action(nameof(StartReceptionistTest), new { businessId })!,
            summary.CanRunVoiceTest ? null : $"https://portal.mylegnd.com/business/{businessId:D}/receptionist/connections",
            SubmittedPreparation: preparation, SubmittedCredential: credential, Reconciliation: reconciliation, SelfUrl: Url.Action(nameof(ReceptionistConnections), new { businessId }),
            AdvancedUrl: summary.CanReview && founderRouteAvailable ? Url.Action("Receptionist", "FounderEngineering") : null));
    }

    private async Task<IActionResult> ReceptionistManagementFailureAsync(MarketingOwnerScope owner, Guid scope,
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
    public async Task<IActionResult> GrantReceptionistTest(Guid businessId = default, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var owner = MarketingOwnerScope.Business(businessId);
        return await ReceptionistMedia.GrantForOwnerAsync(owner, AuthorizeReceptionistManagementAsync, cancellationToken);
    }

    [HttpPost("receptionist/connections/test/start"), ValidateAntiForgeryToken, RequestSizeLimit(73728)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> StartReceptionistTest([FromBody] ReceptionistBrowserController.StartRequest request,
        Guid businessId = default, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var owner = MarketingOwnerScope.Business(businessId);
        return await ReceptionistMedia.StartForOwnerAsync(owner, request, AuthorizeReceptionistManagementAsync, cancellationToken);
    }

    [HttpPost("receptionist/connections/prepare")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(65536)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> PrepareReceptionistOperation([FromForm] ReceptionistOperationPreparationInput input,
        Guid businessId = default, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var owner = MarketingOwnerScope.Business(businessId);
        if (!ModelState.IsValid) return await ReceptionistManagementFailureAsync(owner, businessId, ReceptionistPlanFailure.InvalidPreparation, 400, cancellationToken, preparation: input);
        try
        {
            await ReceptionistManagement.PrepareForOwnerAsync(owner, input, AuthorizeReceptionistManagementAsync, cancellationToken);
            return RedirectToAction(nameof(ReceptionistConnections), new { businessId, notice = "prepared" });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DbUpdateConcurrencyException) { return await ReceptionistManagementFailureAsync(owner, businessId, ReceptionistPlanFailure.StalePreparation, 409, cancellationToken, preparation: input); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { return await ReceptionistManagementFailureAsync(owner, businessId, ReceptionistPlanFailure.InvalidPreparation, 400, cancellationToken, preparation: input); }
    }

    [HttpPost("receptionist/connections/plan")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> PlanReceptionistOperation(string key, string sha256, bool pendingReview = false,
        Guid businessId = default, CancellationToken cancellationToken = default, bool reconcile = false)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var owner = MarketingOwnerScope.Business(businessId);
        if (!ModelState.IsValid || !ReceptionistOperationReview.IsSavedIdentity(key, sha256)) return await ReceptionistManagementFailureAsync(owner, businessId, ReceptionistPlanFailure.ChooseOperation, 400, cancellationToken);
        var reconciliation = reconcile ? new ReceptionistOperationReconciliation(key, sha256) : null;
        try
        {
            ReceptionistOperationReview review;
            if (pendingReview)
            {
                if (!key.StartsWith("saved-", StringComparison.Ordinal) || !Guid.TryParseExact(key[6..], "N", out var id)) return await ReceptionistManagementFailureAsync(owner, businessId, ReceptionistPlanFailure.ChooseOperation, 400, cancellationToken);
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
            return await ReceptionistManagementPageAsync(owner, businessId, null, review, cancellationToken, reconciliation: reconciliation);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ReceptionistCurrentProjectRequiredException)
        {
            return await ReceptionistManagementFailureAsync(owner, businessId, ReceptionistPlanFailure.CurrentProjectRequired, 400, cancellationToken, reconciliation: reconciliation);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or DbUpdateConcurrencyException)
        {
            return await ReceptionistManagementFailureAsync(owner, businessId, reconciliation is null ? ReceptionistPlanFailure.StaleOrUnavailable : ReceptionistPlanFailure.UnconfirmedOperation, 409, cancellationToken, reconciliation: reconciliation);
        }
    }

    [HttpPost("receptionist/connections/review")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ReviewReceptionistOperation(Guid id, Guid expectedRevision, string sha256, bool accepted,
        Guid businessId = default, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var owner = MarketingOwnerScope.Business(businessId);
        if (!ModelState.IsValid || expectedRevision == Guid.Empty || !ReceptionistOperationReview.IsSavedIdentity("saved-" + id.ToString("N"), sha256))
            return await ReceptionistManagementFailureAsync(owner, businessId, ReceptionistPlanFailure.ChooseOperation, 400, cancellationToken);
        try
        {
            await ReceptionistManagement.ReviewForOwnerAsync(owner, id, expectedRevision, sha256, accepted, AuthorizeReceptionistManagementAsync, cancellationToken);
            return RedirectToAction(nameof(ReceptionistConnections), new { businessId, notice = "reviewed" });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ReceptionistCurrentProjectRequiredException)
        {
            return await ReceptionistManagementFailureAsync(owner, businessId, ReceptionistPlanFailure.CurrentProjectRequired, 400, cancellationToken);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or DbUpdateConcurrencyException)
        {
            return await ReceptionistManagementFailureAsync(owner, businessId, ReceptionistPlanFailure.StaleOrUnavailable, 409, cancellationToken);
        }
    }

    [HttpPost("receptionist/connections/apply")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(16384)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ApplyReceptionistOperation([FromForm] ReceptionistOperatorApplyRequest input,
        Guid businessId = default, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var owner = MarketingOwnerScope.Business(businessId);
        if (!ModelState.IsValid || !ReceptionistOperationReview.IsSavedIdentity(input.PackageKey, input.PackageSha256) ||
            string.IsNullOrWhiteSpace(input.ExpectedStateToken) || input.ExpectedStateToken.Length > 4096) return await ReceptionistManagementFailureAsync(owner, businessId, ReceptionistPlanFailure.InvalidApply, 400, cancellationToken);
        try
        {
            var receipt = await ReceptionistManagement.ApplyForOwnerAsync(owner, input, AuthorizeReceptionistManagementAsync, cancellationToken);
            return await ReceptionistManagementPageAsync(owner, businessId,
                receipt.ReadBackVerified ? "The operation was verified by persisted readback. Live provider and conversational acceptance remain separate."
                    : "The result is not yet verified. Keep the same operation and reconcile its existing result before retrying.", null, cancellationToken,
                reconciliation: receipt.ReadBackVerified ? null : new(receipt.PackageKey, receipt.PackageSha256),
                allowUnverifiedReceiptFallback: !receipt.ReadBackVerified);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or DbUpdateConcurrencyException)
        { return await ReceptionistManagementFailureAsync(owner, businessId, ReceptionistPlanFailure.StaleApply, 409, cancellationToken); }
    }

    [HttpPost("receptionist/connections/credential")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(32768)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> SaveReceptionistCredential([FromForm] ReceptionistCredentialInput input,
        Guid businessId = default, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var owner = MarketingOwnerScope.Business(businessId);
        var draft = new ReceptionistCredentialDraft(input.ExpectedConnectionRevision, input.ProviderProjectKey, input.Purpose);
        if (!ModelState.IsValid) return await ReceptionistManagementFailureAsync(owner, businessId, ReceptionistPlanFailure.InvalidCredential, 400, cancellationToken, credential: draft);
        try
        {
            await ReceptionistManagement.SaveCredentialAsync(owner, input, AuthorizeReceptionistManagementAsync, cancellationToken);
            return RedirectToAction(nameof(ReceptionistConnections), new { businessId, notice = "saved" });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DbUpdateConcurrencyException) { return await ReceptionistManagementFailureAsync(owner, businessId, ReceptionistPlanFailure.StaleCredential, 409, cancellationToken, credential: draft); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return await ReceptionistManagementFailureAsync(owner, businessId, ReceptionistPlanFailure.InvalidCredential, 400, cancellationToken, credential: draft); }
    }

    [HttpPost("receptionist/connections/withdraw")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> RevokeReceptionistCredential(Guid credentialId, Guid expectedConnectionRevision,
        Guid businessId = default, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var owner = MarketingOwnerScope.Business(businessId);
        if (!ModelState.IsValid || credentialId == Guid.Empty || expectedConnectionRevision == Guid.Empty)
            return await ReceptionistManagementFailureAsync(owner, businessId, ReceptionistPlanFailure.InvalidWithdrawal, 400, cancellationToken);
        try
        {
            await ReceptionistManagement.RevokeCredentialAsync(owner, credentialId, expectedConnectionRevision, AuthorizeReceptionistManagementAsync, cancellationToken);
            return RedirectToAction(nameof(ReceptionistConnections), new { businessId, notice = "revoked" });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DbUpdateConcurrencyException) { return await ReceptionistManagementFailureAsync(owner, businessId, ReceptionistPlanFailure.StaleWithdrawal, 409, cancellationToken); }
        catch (InvalidOperationException) { return await ReceptionistManagementFailureAsync(owner, businessId, ReceptionistPlanFailure.InvalidWithdrawal, 400, cancellationToken); }
    }
}
