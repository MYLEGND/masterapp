using System.Security.Cryptography;
using System.Text;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
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
        var cutoff = DateTime.UtcNow.AddDays(-2);
        var leads = await db.WebsiteLeads.AsNoTracking()
            .Where(x => !x.IsDeleted && x.CreatedUtc >= cutoff)
            .OrderBy(x => x.CreatedUtc)
            .Take(100)
            .ToListAsync(ct);

        foreach (var snapshot in leads)
        {
            var notificationId = NotificationId(snapshot.LeadId);
            if (await db.MobileActivityNotifications.AsNoTracking().AnyAsync(x => x.Id == notificationId, ct))
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
                notifications.NotifyCommittedMessages();
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<MessagingActor?> ResolveRecipientAsync(WebsiteLead lead, CancellationToken ct)
    {
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
            var ownerEmail = await db.CommerceBusinesses.AsNoTracking()
                .Where(x => x.Id == businessId && x.IsActive && x.Status == "Active")
                .Select(x => x.OwnerEmail)
                .SingleOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(ownerEmail)) return null;

            var normalized = ownerEmail.Trim().ToLowerInvariant();
            var clientUserId = await db.ClientProfiles.AsNoTracking()
                .Where(x => x.Email.ToLower() == normalized)
                .Select(x => x.ClientUserId)
                .FirstOrDefaultAsync(ct);
            var client = Actor(clientUserId, MessagingParticipantTypes.Client);
            if (client is not null) return client;

            var agentUserId = await db.AgentProfiles.AsNoTracking()
                .Where(x => x.IsActive && (x.NormalizedEmail == normalized || x.AgentUpn.ToLower() == normalized))
                .Select(x => x.AgentUserId)
                .FirstOrDefaultAsync(ct);
            return Actor(agentUserId, MessagingParticipantTypes.Agent);
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
