using AgentPortal.Security;
using Domain.Entities;
using Infrastructure.Bookings;
using Infrastructure.Data;
using Infrastructure.Reception;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Controllers;

/// <summary>Restricted projection within the receptionist operations surface; no customer-detail mode.</summary>
[Authorize]
[Route("founder/engineering/receptionist/status")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ReceptionistOperationsController(MasterAppDbContext db, ReceptionistBookingReadinessReader readiness,
    ReceptionistAdmissionService admission) : Controller
{
    [HttpGet]
    [ReceptionistMetadataRead(restrictedOperator: true)]
    public async Task<IActionResult> Status(Guid? qaTicketId, CancellationToken ct)
    {
        if (!ModelState.IsValid || qaTicketId == Guid.Empty) return BadRequest(new { state = "invalid_test_ticket" });
        var grant = ReceptionistMetadataAccess.Require(HttpContext);
        if (!grant.Restricted || grant.Owner is null) return Forbid();
        var current = await (from connection in db.MarketingConnections.AsNoTracking()
            join version in db.Set<ReceptionistConfigurationVersion>().AsNoTracking()
                on connection.ReceptionistActiveConfigurationId equals version.Id
            where connection.OwnerKey == grant.Owner.Key && connection.Provider == "openai" && version.MarketingConnectionId == connection.Id
            select new { version.Id, version.Version, connection.Revision, connection.ReceptionistControlRevision, connection.ReceptionistAdmissionEnabled }).SingleOrDefaultAsync(ct);
        if (current is null) return StatusCode(409, new { state = "configuration_unavailable" });
        var booking = await readiness.ReadAsync(grant.Owner, current.Id, ct);
        // QA-only aggregates cannot masquerade as customer acquisition or revenue.
        var qa = await admission.ReadQaOutcomesAsync(grant.Owner.Key, current.Id, qaTicketId, ct);
        if (!await db.MarketingConnections.AsNoTracking().AnyAsync(x => x.OwnerKey == grant.Owner.Key && x.Provider == "openai" &&
            x.ReceptionistActiveConfigurationId == current.Id && x.Revision == current.Revision &&
            x.ReceptionistControlRevision == current.ReceptionistControlRevision, ct))
            return StatusCode(409, new { state = "configuration_changed" });
        return View("~/Views/FounderEngineering/ReceptionistStatus.cshtml",
            new ReceptionistOperationsStatus(current.Version, current.ReceptionistAdmissionEnabled, qa, booking));
    }
}

public sealed record ReceptionistOperationsStatus(long ConfigurationVersion, bool AdmissionEnabled,
    IReadOnlyList<ReceptionistQaOutcomeStatus> SyntheticOutcomes, Shared.Analytics.ReceptionistBookingReadiness Booking);
