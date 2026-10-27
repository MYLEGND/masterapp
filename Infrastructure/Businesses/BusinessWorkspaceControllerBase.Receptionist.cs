using Infrastructure.Analytics;
using Infrastructure.Reception;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Analytics;
using Shared.Auth;

namespace Infrastructure.Businesses;

public abstract partial class BusinessWorkspaceControllerBase
{
    [HttpGet("receptionist")]
    public async Task<IActionResult> Receptionist(Guid businessId, string? notice = null, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var snapshot = await ReceptionistStore.GetReceptionistConfigurationAsync(MarketingOwnerScope.Business(businessId), MarketingDestinationKeys.OpenAi, cancellationToken);
        return await ReceptionistPageAsync(snapshot, businessId, message: notice == "saved" ? "Your settings were saved as an inactive version. Your operating configuration and call permission were not changed. Review and apply this version before calls can use it."
            : notice == "content-replayed" ? "This content review was already applied. Its prior result was reused; the current settings and call permission are shown below and were not changed."
            : notice == "content-applied" ? "The reviewed content was applied. Calls remain paused; readiness and call-quality acceptance are separate."
            : notice == "admission" ? "Your receptionist permission was saved. This does not verify that calls are ready." : null, cancellationToken: cancellationToken);
    }

    [HttpPost("receptionist")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(262144)]
    public async Task<IActionResult> SaveReceptionist(Guid businessId, [FromForm] ReceptionistSetupInput input,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var owner = MarketingOwnerScope.Business(businessId);
        var snapshot = await ReceptionistStore.GetReceptionistConfigurationAsync(owner, MarketingDestinationKeys.OpenAi, cancellationToken);
        if (ModelState.IsValid)
        {
            try
            {
                var configuration = ReceptionistSetupForms.Apply(snapshot, input);
                await ReceptionistStore.SaveReceptionistConfigurationAsync(owner, MarketingDestinationKeys.OpenAi,
                    input.ExpectedRevision, configuration, User.GetCanonicalUserId(), cancellationToken);
                return RedirectToAction(nameof(Receptionist), new { businessId, notice = "saved" });
            }
            catch (DbUpdateConcurrencyException)
            {
                Response.StatusCode = 409;
                ModelState.AddModelError(string.Empty, "These settings changed while you were editing. Keep a copy of your edits and reload before saving.");
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Text.Json.JsonException or OverflowException)
            {
                Response.StatusCode = 400;
                ModelState.AddModelError(string.Empty, "Check the services, answers, hours, currency, time zone and limits. Existing settings have been preserved.");
            }
        }
        else Response.StatusCode = 400;
        return await ReceptionistPageAsync(snapshot, businessId, input, cancellationToken: cancellationToken);
    }

    [HttpPost("receptionist/admission")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReceptionistAdmission(Guid businessId, [FromForm] ReceptionistAdmissionInput input,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        if (!ModelState.IsValid) return await ReceptionistSetupFailureAsync(MarketingOwnerScope.Business(businessId), businessId, ReceptionistPlanFailure.InvalidAdmission, 400, cancellationToken, admission: input);
        try
        {
            await ReceptionistStore.SetReceptionistAdmissionAsync(MarketingOwnerScope.Business(businessId), MarketingDestinationKeys.OpenAi,
                input.ExpectedRevision, input.Enabled, cancellationToken);
            return RedirectToAction(nameof(Receptionist), new { businessId, notice = "admission" });
        }
        catch (DbUpdateConcurrencyException) { return await ReceptionistSetupFailureAsync(MarketingOwnerScope.Business(businessId), businessId, ReceptionistPlanFailure.StaleAdmission, 409, cancellationToken, admission: input); }
        catch (InvalidOperationException) { return await ReceptionistSetupFailureAsync(MarketingOwnerScope.Business(businessId), businessId, ReceptionistPlanFailure.InvalidAdmission, 400, cancellationToken, admission: input); }
    }

    [HttpGet("receptionist/content")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ReviewReceptionistContent(Guid businessId, CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var owner = MarketingOwnerScope.Business(businessId);
        try
        {
            var preview = await ReceptionistStore.PreviewReceptionistContentAsync(owner, MarketingDestinationKeys.OpenAi,
                User.GetCanonicalUserId(), cancellationToken);
            ViewData["ReceptionistBusinessId"] = businessId;
            return View("~/Views/Shared/ReceptionistContentReview.cshtml", new ReceptionistContentReviewPage(preview,
                Url.Action(nameof(ApplyReceptionistContent), new { businessId })!,
                Url.Action(nameof(RestoreReceptionistContent), new { businessId })!,
                Url.Action(nameof(Receptionist), new { businessId })!));
        }
        catch (InvalidOperationException) { return await ReceptionistSetupFailureAsync(owner, businessId, ReceptionistPlanFailure.MissingContent, 400, cancellationToken); }
    }

    [HttpPost("receptionist/content/apply")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ApplyReceptionistContent([FromForm] ReceptionistContentApplyInput input, Guid businessId,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var owner = MarketingOwnerScope.Business(businessId);
        if (!ModelState.IsValid) return await ReceptionistContentFailureAsync(owner, businessId, ReceptionistPlanFailure.InvalidContent, 400, cancellationToken, apply: input);
        try
        {
            var applied = await ReceptionistStore.ApplyReceptionistContentAsync(owner, MarketingDestinationKeys.OpenAi, input,
                User.GetCanonicalUserId(), cancellationToken);
            return RedirectToAction(nameof(Receptionist), new { businessId, notice = applied.Replayed ? "content-replayed" : "content-applied" });
        }
        catch (DbUpdateConcurrencyException) { return await ReceptionistContentFailureAsync(owner, businessId, ReceptionistPlanFailure.StaleContent, 409, cancellationToken, apply: input); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { return await ReceptionistContentFailureAsync(owner, businessId, ReceptionistPlanFailure.InvalidContent, 400, cancellationToken, apply: input); }
    }

    [HttpPost("receptionist/content/restore")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> RestoreReceptionistContent([FromForm] ReceptionistContentRollbackInput input, Guid businessId,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        var owner = MarketingOwnerScope.Business(businessId);
        if (!ModelState.IsValid) return await ReceptionistContentFailureAsync(owner, businessId, ReceptionistPlanFailure.InvalidContent, 400, cancellationToken, rollback: input);
        try
        {
            await ReceptionistStore.RestoreReceptionistContentDraftAsync(owner, MarketingDestinationKeys.OpenAi, input,
                User.GetCanonicalUserId(), cancellationToken);
            return input.RestoreOperatingConfiguration
                ? RedirectToAction(nameof(ReviewReceptionistContent), new { businessId })
                : RedirectToAction(nameof(Receptionist), new { businessId, notice = "saved" });
        }
        catch (DbUpdateConcurrencyException) { return await ReceptionistContentFailureAsync(owner, businessId, ReceptionistPlanFailure.StaleContent, 409, cancellationToken, rollback: input); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { return await ReceptionistContentFailureAsync(owner, businessId, ReceptionistPlanFailure.InvalidContent, 400, cancellationToken, rollback: input); }
    }

    private async Task<IActionResult> ReceptionistContentFailureAsync(MarketingOwnerScope owner, Guid scope,
        ReceptionistPlanFailure failure, int status, CancellationToken ct,
        ReceptionistContentApplyInput? apply = null, ReceptionistContentRollbackInput? rollback = null)
    {
        if (!await AuthorizeReceptionistManagementAsync(owner, ct)) return Forbid();
        Response.StatusCode = status;
        ReceptionistContentPreview? preview = null;
        try { preview = await ReceptionistStore.PreviewReceptionistContentAsync(owner, MarketingDestinationKeys.OpenAi, User.GetCanonicalUserId(), ct); }
        catch (InvalidOperationException) { } // Missing draft/baseline: retain the submitted choices without inventing a preview.
        ViewData["ReceptionistBusinessId"] = scope;
        return View("~/Views/Shared/ReceptionistContentReview.cshtml", new ReceptionistContentReviewPage(preview,
            Url.Action(nameof(ApplyReceptionistContent), new { businessId = scope })!,
            Url.Action(nameof(RestoreReceptionistContent), new { businessId = scope })!,
            Url.Action(nameof(Receptionist), new { businessId = scope })!, apply, rollback, failure,
            Url.Action(nameof(ReviewReceptionistContent), new { businessId = scope })));
    }

    private async Task<IActionResult> ReceptionistSetupFailureAsync(MarketingOwnerScope owner, Guid scope,
        ReceptionistPlanFailure failure, int status, CancellationToken ct, ReceptionistAdmissionInput? admission = null)
    {
        // Retain the submitted choice and original revision; do not rebase stale requests.
        if (!await AuthorizeReceptionistManagementAsync(owner, ct)) return Forbid();
        Response.StatusCode = status;
        ViewData["ReceptionistSetupFailure"] = failure;
        var snapshot = await ReceptionistStore.GetReceptionistConfigurationAsync(owner, MarketingDestinationKeys.OpenAi, ct);
        return await ReceptionistPageAsync(snapshot, scope, cancellationToken: ct, admission: admission);
    }

    private MarketingConnectionStore ReceptionistStore => HttpContext.RequestServices.GetRequiredService<MarketingConnectionStore>();

    private async Task<IActionResult> ReceptionistPageAsync(ReceptionistConfigurationSnapshot snapshot, Guid businessId,
        ReceptionistSetupInput? input = null, string? message = null, CancellationToken cancellationToken = default, ReceptionistAdmissionInput? admission = null)
    {
        ViewData["ReceptionistBusinessId"] = businessId;
        var operating = await ReceptionistStore.GetReceptionistConfigurationAsync(snapshot.Owner, snapshot.Provider, cancellationToken, operating: true);
        var readiness = await HttpContext.RequestServices.GetRequiredService<ReceptionistAdmissionService>()
            .GetSetupReadinessAsync(operating, observeControlPlaneRuntime: false, ct: cancellationToken);
        if (operating.ConnectionRevision != snapshot.ConnectionRevision)
            readiness = new ReceptionistReadinessProjection { AdmissionReason = "snapshot_changed" };
        return View("~/Views/Shared/ReceptionistSetup.cshtml",
            new ReceptionistSetupPage(snapshot, input ?? ReceptionistSetupForms.Read(snapshot),
                Url.Action(nameof(SaveReceptionist), new { businessId })!,
                Url.Action(nameof(ReceptionistAdmission), new { businessId })!,
                Url.Action(nameof(Settings), new { businessId })!, ReceptionistSetupForms.CrmBuckets(snapshot.Owner), message, readiness, Url.Action(nameof(ReviewReceptionistContent), new { businessId })!, OperatingSnapshot: operating, ManagementUrl: Url.Action(nameof(ReceptionistConnections), new { businessId }), SubmittedAdmission: admission));
    }
}
