using System.Security.Cryptography;
using System.Text;
using Domain.Engineering;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services.Engineering;

internal sealed class LegendEngineeringFounderNotificationService(
    MasterAppDbContext db,
    INotificationEngine notifications)
{
    internal async Task NotifyActionableAsync(
        IReadOnlyList<EngineeringWorkItemSnapshot> workItems,
        string? executorBlocker,
        string? blockerEpisodeId,
        string? recoveredEpisodeId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(AgentPortal.Security.FounderGuard.FounderOid, out _)) return;
        var founder = AgentPortal.Security.FounderGuard.FounderOid.Trim().ToLowerInvariant();
        var actionable = workItems.Where(LegendEngineeringFounderPresentation.ShouldSurface).ToArray();

        foreach (var item in actionable)
        {
            var code = NotificationCode(item);
            var presentation = LegendEngineeringFounderPresentation.Present(item);
            await StageOnceAsync(
                founder,
                item.WorkItemId,
                code,
                "LEGEND Engineering · " + presentation.Title,
                LegendEngineeringFounderPresentation.PushDetail(presentation),
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(executorBlocker) && workItems.Any(IsPendingModelWork))
        {
            var episode = string.IsNullOrWhiteSpace(blockerEpisodeId)
                ? Safe(executorBlocker, 96)
                : Safe(blockerEpisodeId, 48);
            await StageOnceAsync(founder, Guid.Empty, "executor:" + episode,
                "LEGEND Engineering needs attention",
                "Autonomous model execution is paused because the ChatGPT-plan runtime is not ready (" + Safe(executorBlocker, 96) + "). Deterministic monitoring and CI/release reconciliation remain active; no API-billed fallback was used.",
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(recoveredEpisodeId))
            await StageOnceAsync(founder, Guid.Empty, "executor-recovered:" + Safe(recoveredEpisodeId, 48),
                "LEGEND Engineering runtime recovered",
                "The ChatGPT-plan runtime completed a fresh inference readiness check and autonomous model execution can resume under the existing Founder contract.",
                cancellationToken);
    }

    internal async Task NotifyDailyDigestAsync(
        IReadOnlyList<EngineeringWorkItemSnapshot> workItems,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(AgentPortal.Security.FounderGuard.FounderOid, out _)) return;
        var founder = AgentPortal.Security.FounderGuard.FounderOid.Trim().ToLowerInvariant();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var founderActions = workItems.Count(item => item.State is "FOUNDER_RELEASE_APPROVAL_REQUIRED" or "FOUNDER_ESCALATION");
        var highPriority = workItems.Count(item => item.PriorityClass == "P1");
        var detail = founderActions > 0
            ? $"LEGEND has {workItems.Count} open engineering item(s). {founderActions} need your review and {highPriority} are high priority. Action: Open Founder Engineering to review the items that need a decision."
            : $"LEGEND has {workItems.Count} open engineering item(s), including {highPriority} high-priority item(s). Action: No Founder decision is waiting right now.";
        await StageOnceAsync(founder, Guid.Empty, "daily:" + today, "LEGEND Engineering · Daily summary", detail, cancellationToken);
    }

    private async Task StageOnceAsync(string founder, Guid workItemId, string code, string title, string detail, CancellationToken cancellationToken)
    {
        var id = StableGuid("legend-engineering|" + founder + "|" + workItemId.ToString("N") + "|" + code);
        if (await db.MobileActivityNotifications.AsNoTracking().AnyAsync(item => item.Id == id, cancellationToken)) return;
        await notifications.StageAsync(new MobileActivityNotification
        {
            Id = id,
            RecipientUserId = founder,
            RecipientParticipantType = MessagingParticipantTypes.Agent,
            Kind = "Engineering",
            Title = Safe(title, 240),
            Detail = Safe(detail, 1000),
            OccurredUtc = DateTime.UtcNow
        }, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            notifications.NotifyCommittedMessages();
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }
    }

    private static bool IsPendingModelWork(EngineeringWorkItemSnapshot item)
        => item.State is "QUEUED" or "NEEDS_TRIAGE" or "RECURRED_NEEDS_TRIAGE" or
            "NEEDS_SUPERVISOR" or "RECURRED_NEEDS_SUPERVISOR" or "REVIEW_REQUIRED" or
            "REVIEW_REJECTED" or "CI_FAILED_NEEDS_EVIDENCE" or
            "WAITING_PROVIDER_RETRY" or "WAITING_PROVIDER_CONTROL";

    private static string NotificationCode(EngineeringWorkItemSnapshot item) => item.State switch
    {
        "FOUNDER_RELEASE_APPROVAL_REQUIRED" => "release-approval:" + item.CandidateSha,
        "FOUNDER_ESCALATION" => "escalation",
        "CI_FAILED_NEEDS_EVIDENCE" => "ci-failed:" + item.CandidateSha,
        "RELEASE_BLOCKED" => "release-blocked:" + item.CandidateSha,
        "LIVE_FUNCTIONAL_PROOF_REQUIRED" => "live-proof:" + item.MergedSha + ":" + item.ReproducerRoute,
        _ when item.RiskClass == EngineeringRiskClass.TierC => "tier-c:" + item.State,
        _ => "p1:" + item.State
    };

    private static string Safe(string? value, int maximum)
    {
        var text = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= maximum ? text : text[..maximum];
    }

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}