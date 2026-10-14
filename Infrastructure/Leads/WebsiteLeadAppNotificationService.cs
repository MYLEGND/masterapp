using System.Security.Cryptography;
using System.Text;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Businesses;
using Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Leads;

/// <summary>
/// Projects each persisted website lead once into the canonical LEGEND notification ledger.
/// The WebsiteLead remains the source of truth; MobileActivityNotification is only its
/// recipient-scoped delivery projection for app inbox/badge/APNs/FCM.
/// </summary>
public sealed class WebsiteLeadAppNotificationService(
    MasterAppDbContext db,
    IConfiguration configuration,
    INotificationEngine notifications,
    ILogger<WebsiteLeadAppNotificationService> logger)
{
    public async Task DeliverPendingAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var maxId = await db.WebsiteLeads.AsNoTracking().Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.Id).Select(x => (long?)x.Id).FirstOrDefaultAsync(ct);
        if (maxId is null) return;

        long? afterId = null;
        var projected = 0;
        while (projected < 100)
        {
            ct.ThrowIfCancellationRequested();
            var leads = await PendingBatch(db, afterId, maxId.Value).ToListAsync(ct);
            if (leads.Count == 0) return;
            afterId = leads[^1].Id;
            var ids = leads.Select(x => NotificationId(x.LeadId)).ToArray();
            var committed = (await db.MobileActivityNotifications.AsNoTracking()
                .Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct)).ToHashSet();

            foreach (var snapshot in leads)
            {
                ct.ThrowIfCancellationRequested();
                if (projected >= 100) return;
                var notificationId = NotificationId(snapshot.LeadId);
                if (committed.Contains(notificationId) || WebsiteLeadCaptureSafety.ShouldSkipWorkstationCapture(snapshot))
                    continue;

                var actor = await ResolveRecipientAsync(snapshot, ct);
                if (actor is null)
                {
                    logger.LogWarning("Website lead app notification has no scoped LEGEND actor. lead={LeadId}", snapshot.LeadId);
                    continue;
                }

                var title = WebsiteLeadEmailTemplate.SubjectFor(snapshot);
                var name = $"{snapshot.FirstName} {snapshot.LastName}".Trim();
                var detail = string.IsNullOrWhiteSpace(name)
                    ? "A new website lead is ready in CRM."
                    : $"New website lead from {name}. Open CRM to follow up.";

                await notifications.StageAsync(new MobileActivityNotification
                {
                    Id = notificationId,
                    RecipientUserId = actor.UserId,
                    RecipientParticipantType = actor.ParticipantType,
                    Kind = "WebsiteLead",
                    Title = title,
                    Detail = detail,
                    OccurredUtc = snapshot.CreatedUtc
                }, ct);

                try
                {
                    await db.SaveChangesAsync(ct);
                    projected++;
                    notifications.NotifyCommittedMessages();
                }
                catch (DbUpdateException)
                {
                    db.ChangeTracker.Clear();
                }
            }
        }
    }

    private static IQueryable<WebsiteLead> PendingBatch(MasterAppDbContext db, long? afterId, long maxId)
    {
        var query = db.WebsiteLeads.AsNoTracking().Where(x => !x.IsDeleted && x.Id <= maxId);
        if (afterId.HasValue) query = query.Where(x => x.Id > afterId.Value);
        return query.OrderBy(x => x.Id).Take(100);
    }

    private async Task<MessagingActor?> ResolveRecipientAsync(WebsiteLead lead, CancellationToken ct)
    {
        if (lead.AgentTrackingProfileId == Guid.Empty || lead.CommerceBusinessId == Guid.Empty ||
            (lead.AgentTrackingProfileId.HasValue && lead.CommerceBusinessId.HasValue)) return null;

        if (lead.AgentTrackingProfileId is Guid trackingId && trackingId != Guid.Empty)
        {
            var userId = await db.AgentTrackingProfiles.AsNoTracking()
                .Where(x => x.Id == trackingId && x.Status == "active")
                .Select(x => x.AgentUserId)
                .SingleOrDefaultAsync(ct);
            return Actor(userId, MessagingParticipantTypes.Agent);
        }

        if (lead.CommerceBusinessId is Guid businessId && businessId != Guid.Empty)
        {
            var business = await db.CommerceBusinesses.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == businessId && x.IsActive && x.Status == "Active", ct);
            if (business is null) return null;

            var primaryMembers = await db.CommerceBusinessMembers.AsNoTracking()
                .Where(x => x.CommerceBusinessId == businessId && x.Status == "Active" &&
                    (x.RoleKey.ToLower() == "owner" || x.RoleKey.ToLower() == "account" || x.RoleKey.ToLower() == "platform-owner"))
                .ToListAsync(ct);
            if (primaryMembers.Count > 1)
            {
                // OwnerEmail may select an existing primary membership; it cannot establish ownership.
                var ownerEmail = business.OwnerEmail?.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(ownerEmail)) return null;
                primaryMembers = primaryMembers.Where(x =>
                    string.Equals(x.NormalizedEmail?.Trim(), ownerEmail, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.Email?.Trim(), ownerEmail, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (primaryMembers.Count != 1 || primaryMembers[0].ClientProfileId is not Guid profileId || profileId == Guid.Empty)
                return null;

            var profile = await db.ClientProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == profileId, ct);
            var actor = Actor(profile?.ClientUserId, MessagingParticipantTypes.Client);
            if (profile is null || actor is null) return null;
            return await BusinessWorkspaceAccess.ResolveAsync(db, businessId, profileId, actor.UserId, profile.Email, "crm", ct) is null
                ? null
                : actor;
        }

        // Founder-owned mylegnd.com leads intentionally have no agent tracking ID.
        var founderEmail = configuration["Founder:Email"] ?? configuration["Founder:Upn"];
        if (string.IsNullOrWhiteSpace(founderEmail)) return null;
        var founderNormalized = founderEmail.Trim().ToLowerInvariant();
        var founderUserId = await db.AgentProfiles.AsNoTracking()
            .Where(x => x.IsActive && (x.NormalizedEmail == founderNormalized || x.AgentUpn.ToLower() == founderNormalized))
            .Select(x => x.AgentUserId)
            .FirstOrDefaultAsync(ct);
        return Actor(founderUserId, MessagingParticipantTypes.Agent);
    }

    private static MessagingActor? Actor(string? userId, string participantType)
    {
        var normalized = userId?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : new MessagingActor(normalized, participantType);
    }

    private static Guid NotificationId(Guid leadId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("website-lead|" + leadId.ToString("D")));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

public sealed class WebsiteLeadAppNotificationWorker(
    IServiceScopeFactory scopes,
    ILogger<WebsiteLeadAppNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<WebsiteLeadAppNotificationService>()
                    .DeliverPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Website lead app notification projection failed; persisted leads remain retryable.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
