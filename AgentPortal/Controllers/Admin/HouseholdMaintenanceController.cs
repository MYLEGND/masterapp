using AgentPortal.Security;
using Infrastructure.Households;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers.Admin;

[Authorize(Policy = "FounderOnly")]
[Route("admin/households")]
public sealed class HouseholdMaintenanceController : Controller
{
    private readonly HouseholdReconciliationService _reconciliation;

    public HouseholdMaintenanceController(HouseholdReconciliationService reconciliation)
    {
        _reconciliation = reconciliation;
    }

    [HttpPost("reconcile")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reconcile(
        [FromQuery] bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        var result = await _reconciliation.RunAsync(dryRun, cancellationToken);
        return Json(new
        {
            dryRun,
            result.SubscriptionOwnersScanned,
            result.PrimaryHouseholdsCreated,
            result.ExistingHouseholds,
            result.PartnerInviteCandidates,
            collisions = result.Collisions,
            invalidLegacyRecords = result.InvalidLegacyRecords
        });
    }
}
