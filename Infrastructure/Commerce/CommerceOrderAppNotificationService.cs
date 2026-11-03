using System.Security.Cryptography;
using System.Text;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Commerce;

/// <summary>Paid orders project into the existing recipient ledger and delivery outbox.</summary>
public sealed class CommerceOrderAppNotificationService(MasterAppDbContext db, INotificationEngine notifications)
{
    public async Task<int> DeliverPendingAsync(CancellationToken ct, int offset = 0)
    {
        // The existing worker retains only a fair-scan offset, not business or delivery state.
        // Each tick has a fixed scan budget; ledger IDs remain the retry/deduplication authority.
        {
            var orders = await db.CommerceOrders.AsNoTracking().Where(x => x.PaymentStatus == "Paid" && (x.TotalCents <= 0 || x.RefundedCents < x.TotalCents) && (x.ReturnStatus == "None" || x.ReturnStatus == "Closed") &&
                x.FulfillmentStatus != "Fulfilled" && x.FulfillmentStatus != "Cancelled" && x.FulfillmentStatus != "Returned")
                .OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id).Skip(offset).Take(50).ToListAsync(ct);
            if (orders.Count == 0) return 0;
            offset += orders.Count;
            foreach (var order in orders)
            {
                var members = await (from member in db.CommerceBusinessMembers.AsNoTracking()
                    join profile in db.ClientProfiles.AsNoTracking() on member.ClientProfileId equals profile.Id
                    where member.CommerceBusinessId == order.CommerceBusinessId && member.Status == "Active"
                    select new { ProfileId = profile.Id, profile.ClientUserId }).ToListAsync(ct);
                var recipients = new Dictionary<string, MessagingActor>();
                foreach (var member in members)
                {
                    var candidates = new List<MessagingActor> { new(member.ClientUserId, MessagingParticipantTypes.Client) };
                    candidates.AddRange(await db.AgentClients.AsNoTracking().Where(x => x.ClientUserId == member.ClientUserId)
                        .Select(x => new MessagingActor(x.AgentUserId, MessagingParticipantTypes.Agent)).ToListAsync(ct));
                    foreach (var actor in candidates)
                        if (await BusinessWorkspaceAccess.ResolveActorMembershipAsync(db, order.CommerceBusinessId,
                            actor.UserId, "orders", member.ProfileId, ct) != null)
                            recipients.TryAdd($"{actor.ParticipantType}:{actor.UserId.ToLowerInvariant()}", actor);
                }
                foreach (var (key, actor) in recipients)
                {
                    var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"commerce-order:{order.Id:D}:{key}"))[..16]);
                    if (await db.MobileActivityNotifications.AnyAsync(x => x.Id == id, ct)) continue;
                    await notifications.StageAsync(new MobileActivityNotification {
                        Id = id, RecipientUserId = actor.UserId, RecipientParticipantType = actor.ParticipantType,
                        Kind = "StoreOrder", Title = $"Order {order.OrderNumber}",
                        Detail = "A paid store order is ready for fulfillment. Open Work → Orders.",
                        OccurredUtc = order.PaidUtc ?? order.CreatedUtc
                    }, ct);
                    try { await db.SaveChangesAsync(ct); }
                    catch (DbUpdateException)
                    {
                        db.ChangeTracker.Clear();
                        if (!await db.MobileActivityNotifications.AnyAsync(x => x.Id == id, ct)) throw;
                    }
                    notifications.NotifyCommittedMessages();
                }
            }
            return orders.Count < 50 ? 0 : offset;
        }
    }
}
