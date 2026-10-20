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
        return ReceptionistPage(snapshot, businessId, message: notice == "saved" ? "Your settings were saved as an inactive version. Verification is required before calls can use this version."
            : notice == "admission" ? "Your call admission setting was saved." : null);
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
        return ReceptionistPage(snapshot, businessId, input);
    }

    [HttpPost("receptionist/admission")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReceptionistAdmission(Guid businessId, [FromForm] ReceptionistAdmissionInput input,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveBusinessAsync(businessId, "settings", cancellationToken) is null) return Forbid();
        if (!ModelState.IsValid) return BadRequest("Choose a valid call admission setting.");
        try
        {
            await ReceptionistStore.SetReceptionistAdmissionAsync(MarketingOwnerScope.Business(businessId), MarketingDestinationKeys.OpenAi,
                input.ExpectedRevision, input.Enabled, cancellationToken);
            return RedirectToAction(nameof(Receptionist), new { businessId, notice = "admission" });
        }
        catch (DbUpdateConcurrencyException) { return Conflict("These settings changed. Reload before changing call admission."); }
        catch (InvalidOperationException) { return BadRequest("Verified provider and inquiry acceptance are required before enabling calls."); }
    }

    private MarketingConnectionStore ReceptionistStore => HttpContext.RequestServices.GetRequiredService<MarketingConnectionStore>();

    private IActionResult ReceptionistPage(ReceptionistConfigurationSnapshot snapshot, Guid businessId,
        ReceptionistSetupInput? input = null, string? message = null) => View("~/Views/Shared/ReceptionistSetup.cshtml",
            new ReceptionistSetupPage(snapshot, input ?? ReceptionistSetupForms.Read(snapshot),
                Url.Action(nameof(SaveReceptionist), new { businessId })!,
                Url.Action(nameof(ReceptionistAdmission), new { businessId })!,
                Url.Action(nameof(Settings), new { businessId })!, ReceptionistSetupForms.CrmBuckets(snapshot.Owner), message));
}
