using Infrastructure.Analytics;
using Infrastructure.Reception;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Analytics;
using Shared.Auth;

namespace AgentPortal.Controllers;

public partial class WebsiteAnalyticsController
{
    [HttpGet("receptionist")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Receptionist([FromQuery] Guid? agentProfileId = null,
        string? notice = null, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        var snapshot = await ReceptionistStore.GetReceptionistConfigurationAsync(owner, MarketingDestinationKeys.OpenAi, cancellationToken);
        return await ReceptionistPageAsync(snapshot, agentProfileId, message: notice == "saved" ? "Your settings were saved as an inactive version. Your operating configuration and call permission were not changed. Review and apply this version before calls can use it."
            : notice == "content-replayed" ? "This content review was already applied. Its prior result was reused; the current settings and call permission are shown below and were not changed."
            : notice == "content-applied" ? "The reviewed content was applied. Calls remain paused; readiness and call-quality acceptance are separate."
            : notice == "admission" ? "Your receptionist permission was saved. This does not verify that calls are ready." : null, cancellationToken: cancellationToken);
    }

    [HttpPost("receptionist")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [RequestSizeLimit(262144)]
    public async Task<IActionResult> SaveReceptionist([FromForm] ReceptionistSetupInput input,
        [FromQuery] Guid? agentProfileId = null, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        var snapshot = await ReceptionistStore.GetReceptionistConfigurationAsync(owner, MarketingDestinationKeys.OpenAi, cancellationToken);
        if (ModelState.IsValid)
        {
            try
            {
                var configuration = ReceptionistSetupForms.Apply(snapshot, input);
                await ReceptionistStore.SaveReceptionistConfigurationAsync(owner, MarketingDestinationKeys.OpenAi,
                    input.ExpectedRevision, configuration, User.GetCanonicalUserId(), cancellationToken);
                return RedirectToAction(nameof(Receptionist), new { agentProfileId, notice = "saved" });
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
        return await ReceptionistPageAsync(snapshot, agentProfileId, input, cancellationToken: cancellationToken);
    }

    [HttpPost("receptionist/admission")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ReceptionistAdmission([FromForm] ReceptionistAdmissionInput input,
        [FromQuery] Guid? agentProfileId = null, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        if (!ModelState.IsValid) return await ReceptionistSetupFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.InvalidAdmission, 400, cancellationToken, admission: input);
        try
        {
            await ReceptionistStore.SetReceptionistAdmissionAsync(owner, MarketingDestinationKeys.OpenAi,
                input.ExpectedRevision, input.Enabled, cancellationToken);
            return RedirectToAction(nameof(Receptionist), new { agentProfileId, notice = "admission" });
        }
        catch (DbUpdateConcurrencyException) { return await ReceptionistSetupFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.StaleAdmission, 409, cancellationToken, admission: input); }
        catch (InvalidOperationException) { return await ReceptionistSetupFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.InvalidAdmission, 400, cancellationToken, admission: input); }
    }

    [HttpGet("receptionist/content")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ReviewReceptionistContent([FromQuery] Guid? agentProfileId = null, CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        try
        {
            var preview = await ReceptionistStore.PreviewReceptionistContentAsync(owner, MarketingDestinationKeys.OpenAi,
                User.GetCanonicalUserId(), cancellationToken);
            return View("~/Views/Shared/ReceptionistContentReview.cshtml", new ReceptionistContentReviewPage(preview,
                Url.Action(nameof(ApplyReceptionistContent), new { agentProfileId })!,
                Url.Action(nameof(RestoreReceptionistContent), new { agentProfileId })!,
                Url.Action(nameof(Receptionist), new { agentProfileId })!));
        }
        catch (InvalidOperationException) { return await ReceptionistSetupFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.MissingContent, 400, cancellationToken); }
    }

    [HttpPost("receptionist/content/apply")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ApplyReceptionistContent([FromForm] ReceptionistContentApplyInput input, [FromQuery] Guid? agentProfileId = null,
        CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        if (!ModelState.IsValid) return await ReceptionistContentFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.InvalidContent, 400, cancellationToken, apply: input);
        try
        {
            var applied = await ReceptionistStore.ApplyReceptionistContentAsync(owner, MarketingDestinationKeys.OpenAi, input,
                User.GetCanonicalUserId(), cancellationToken);
            return RedirectToAction(nameof(Receptionist), new { agentProfileId, notice = applied.Replayed ? "content-replayed" : "content-applied" });
        }
        catch (DbUpdateConcurrencyException) { return await ReceptionistContentFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.StaleContent, 409, cancellationToken, apply: input); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { return await ReceptionistContentFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.InvalidContent, 400, cancellationToken, apply: input); }
    }

    [HttpPost("receptionist/content/restore")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> RestoreReceptionistContent([FromForm] ReceptionistContentRollbackInput input, [FromQuery] Guid? agentProfileId = null,
        CancellationToken cancellationToken = default)
    {
        var owner = await ResolveAdvertisingOwnerAsync(agentProfileId, cancellationToken);
        if (owner is null) return Forbid();
        if (!ModelState.IsValid) return await ReceptionistContentFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.InvalidContent, 400, cancellationToken, rollback: input);
        try
        {
            await ReceptionistStore.RestoreReceptionistContentDraftAsync(owner, MarketingDestinationKeys.OpenAi, input,
                User.GetCanonicalUserId(), cancellationToken);
            return input.RestoreOperatingConfiguration
                ? RedirectToAction(nameof(ReviewReceptionistContent), new { agentProfileId })
                : RedirectToAction(nameof(Receptionist), new { agentProfileId, notice = "saved" });
        }
        catch (DbUpdateConcurrencyException) { return await ReceptionistContentFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.StaleContent, 409, cancellationToken, rollback: input); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { return await ReceptionistContentFailureAsync(owner, agentProfileId, ReceptionistPlanFailure.InvalidContent, 400, cancellationToken, rollback: input); }
    }

    private async Task<IActionResult> ReceptionistContentFailureAsync(MarketingOwnerScope owner, Guid? scope,
        ReceptionistPlanFailure failure, int status, CancellationToken ct,
        ReceptionistContentApplyInput? apply = null, ReceptionistContentRollbackInput? rollback = null)
    {
        if (!await AuthorizeReceptionistManagementAsync(owner, ct)) return Forbid();
        Response.StatusCode = status;
        ReceptionistContentPreview? preview = null;
        try { preview = await ReceptionistStore.PreviewReceptionistContentAsync(owner, MarketingDestinationKeys.OpenAi, User.GetCanonicalUserId(), ct); }
        catch (InvalidOperationException) { } // Missing draft/baseline: retain the submitted choices without inventing a preview.
        return View("~/Views/Shared/ReceptionistContentReview.cshtml", new ReceptionistContentReviewPage(preview,
            Url.Action(nameof(ApplyReceptionistContent), new { agentProfileId = scope })!,
            Url.Action(nameof(RestoreReceptionistContent), new { agentProfileId = scope })!,
            Url.Action(nameof(Receptionist), new { agentProfileId = scope })!, apply, rollback, failure,
            Url.Action(nameof(ReviewReceptionistContent), new { agentProfileId = scope })));
    }

    private async Task<IActionResult> ReceptionistSetupFailureAsync(MarketingOwnerScope owner, Guid? scope,
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

    private async Task<IActionResult> ReceptionistPageAsync(ReceptionistConfigurationSnapshot snapshot, Guid? agentProfileId,
        ReceptionistSetupInput? input = null, string? message = null, CancellationToken cancellationToken = default, ReceptionistAdmissionInput? admission = null)
    {
        var operating = await ReceptionistStore.GetReceptionistConfigurationAsync(snapshot.Owner, snapshot.Provider, cancellationToken, operating: true);
        var readiness = await HttpContext.RequestServices.GetRequiredService<ReceptionistAdmissionService>()
            .GetSetupReadinessAsync(operating, observeControlPlaneRuntime: true, ct: cancellationToken);
        if (operating.ConnectionRevision != snapshot.ConnectionRevision)
            readiness = new ReceptionistReadinessProjection { AdmissionReason = "snapshot_changed" };
        return View("~/Views/Shared/ReceptionistSetup.cshtml",
            new ReceptionistSetupPage(snapshot, input ?? ReceptionistSetupForms.Read(snapshot),
                Url.Action(nameof(SaveReceptionist), new { agentProfileId })!,
                Url.Action(nameof(ReceptionistAdmission), new { agentProfileId })!,
                Url.Action("Index", new { agentProfileId })!, ReceptionistSetupForms.CrmBuckets(snapshot.Owner), message, readiness, Url.Action(nameof(ReviewReceptionistContent), new { agentProfileId })!, OperatingSnapshot: operating, ManagementUrl: Url.Action(nameof(ReceptionistConnections), new { agentProfileId }),
                SubmittedAdmission: admission, CalendarSetupUrl: Url.Action("Index", new { agentProfileId }) is { } calendarUrl ? calendarUrl + "#receptionist-calendar" : null));
    }
}
