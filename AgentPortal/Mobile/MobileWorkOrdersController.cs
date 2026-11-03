using Infrastructure.Businesses;
using Infrastructure.Commerce;
using Infrastructure.Data;
using Infrastructure.Mobile;
using Domain.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Mobile;

/// <summary>Native transport over the website's business membership and order authorities.</summary>
[ApiController]
[Route("api/v1/mobile/work/orders")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileWorkOrdersController(IMobileActorResolver actors, MasterAppDbContext db, ParfaitApp.Services.ParfaitOrderService orders)
    : MobileApiControllerBase(actors)
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var resolved = await ResolveActorAsync(ct);
        if (resolved.Error != null) return resolved.Error;
        var actor = resolved.Actor!;
        var profiles = db.ClientProfiles.AsNoTracking().Where(p => p.Id == actor.ProfileId);
        if (actor.Actor.ParticipantType == MessagingParticipantTypes.Agent)
            profiles = db.ClientProfiles.AsNoTracking().Where(p => db.AgentClients.Any(link =>
                link.AgentUserId == actor.Actor.UserId && link.ClientUserId == p.ClientUserId));
        var candidates = await (from member in db.CommerceBusinessMembers.AsNoTracking()
            join profile in profiles on member.ClientProfileId equals profile.Id
            where member.Status == "Active"
            select new { member.CommerceBusinessId, ProfileId = profile.Id }).Distinct().ToListAsync(ct);
        var allowed = new Dictionary<Guid, Guid>();
        foreach (var candidate in candidates)
            if (await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, candidate.CommerceBusinessId,
                actor.Actor.UserId, "orders", candidate.ProfileId, ct) != null)
                allowed.TryAdd(candidate.CommerceBusinessId, candidate.ProfileId);
        var rows = await db.CommerceOrders.AsNoTracking().Include(x => x.Lines)
            .Where(x => allowed.Keys.Contains(x.CommerceBusinessId) && x.PaymentStatus == "Paid" && (x.TotalCents <= 0 || x.RefundedCents < x.TotalCents) && (x.ReturnStatus == "None" || x.ReturnStatus == "Closed") &&
                x.FulfillmentStatus != "Fulfilled" && x.FulfillmentStatus != "Cancelled" && x.FulfillmentStatus != "Returned")
            .OrderBy(x => x.PaidUtc).ThenBy(x => x.Id).Take(100).ToListAsync(ct);
        var names = await db.CommerceBusinesses.AsNoTracking().Where(x => allowed.Keys.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        return Ok(rows.Select(x => new {
            x.Id, profileId = allowed[x.CommerceBusinessId], businessId = x.CommerceBusinessId, businessName = names.GetValueOrDefault(x.CommerceBusinessId),
            x.OrderNumber, x.FulfillmentStatus, allowedStatuses = CommerceOrderFulfillment.States, x.TrackingCarrier, x.TrackingNumber, x.UpdatedUtc,
            x.FirstName, x.LastName, x.AddressLine1, x.AddressLine2, x.City, x.State, x.PostalCode,
            items = x.Lines.Select(line => new { line.ProductName, line.Size, line.Quantity })
        }));
    }

    public sealed record FulfillmentInput(Guid BusinessId, Guid ProfileId, string Status,
        string? TrackingCarrier, string? TrackingNumber, DateTime? ExpectedUpdatedUtc);

    [HttpPut("{id:guid}/fulfillment")]
    public async Task<IActionResult> Fulfill(Guid id, FulfillmentInput input, CancellationToken ct)
    {
        var resolved = await ResolveActorAsync(ct);
        if (resolved.Error != null) return resolved.Error;
        var actor = resolved.Actor!;
        if (await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, input.BusinessId, actor.Actor.UserId,
            "orders", input.ProfileId, ct) == null) return Forbid();
        var order = await db.CommerceOrders.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == id && x.CommerceBusinessId == input.BusinessId, ct);
        if (order == null) return NotFound();
        if (order.UpdatedUtc != input.ExpectedUpdatedUtc) return OrderChanged();
        if (order.PaymentStatus != "Paid" || order.ReturnStatus is not ("None" or "Closed"))
            return Conflict(new { code = "order_not_ready", message = "This order needs payment or return review before fulfillment." });
        var validation = CommerceOrderFulfillment.Validate(order, input.Status, input.TrackingCarrier, input.TrackingNumber);
        if (validation != null) return Error(400, "mobile_order_validation", validation);
        if (!orders.UpdateFulfillment(input.BusinessId, id, input.ExpectedUpdatedUtc?.Ticks ?? 0,
                input.Status, input.TrackingCarrier, input.TrackingNumber, out var error))
            return Conflict(new { code = "order_changed", message = error ?? "Order changed. Refresh before saving." });
        var updated = await db.CommerceOrders.AsNoTracking().SingleAsync(x => x.Id == id && x.CommerceBusinessId == input.BusinessId, ct);
        return Ok(new { updated.UpdatedUtc });
    }

    private ConflictObjectResult OrderChanged() => Conflict(new { code = "order_changed", message = "Order changed. Refresh before saving." });
}
