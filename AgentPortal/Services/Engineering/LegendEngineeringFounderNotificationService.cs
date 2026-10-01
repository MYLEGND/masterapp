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
        var actionable = workItems.Where(item => item.PriorityClass == "P1" || item.RiskClass == EngineeringRiskClass.TierC ||
            item.State is "FOUNDER_RELEASE_APPROVAL_REQUIRED" or "FOUNDER_ESCALATION" or "CI_FAILED_NEEDS_EVIDENCE" or "RELEASE_BLOCKED").ToArray();

        foreach (var item in actionable)
        {
            var code = NotificationCode(item);
            await StageOnceAsync(founder, item.WorkItemId, code,
                Title(item), Detail(item), cancellationToken);
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
        var detail = $"Open {workItems.Count}; P1 {workItems.Count(item => item.PriorityClass == "P1")}; " +
                     $"waiting on Founder {workItems.Count(item => item.State is "FOUNDER_RELEASE_APPROVAL_REQUIRED" or "FOUNDER_ESCALATION")}; " +
                     $"validated {workItems.Count(item => item.State == "VALIDATED")}; release requested {workItems.Count(item => item.State == "RELEASE_REQUESTED")}.";
        await StageOnceAsync(founder, Guid.Empty, "daily:" + today, "LEGEND Engineering Daily Summary", detail, cancellationToken);
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
        => item.State is "QUEUED" or "NEEDS_SUPERVISOR" or "RECURRED_NEEDS_SUPERVISOR" or "REVIEW_REQUIRED" or "REVIEW_REJECTED";

    private static string NotificationCode(EngineeringWorkItemSnapshot item) => item.State switch
    {
        "FOUNDER_RELEASE_APPROVAL_REQUIRED" => "release-approval:" + item.CandidateSha,
        "FOUNDER_ESCALATION" => "escalation:" + item.EvidenceRevision,
        "CI_FAILED_NEEDS_EVIDENCE" => "ci-failed:" + item.CandidateSha,
        "RELEASE_BLOCKED" => "release-blocked:" + item.CandidateSha,
        _ when item.RiskClass == EngineeringRiskClass.TierC => "tier-c:" + item.EvidenceRevision,
        _ => "p1:" + item.EvidenceRevision
    };

    private static string Title(EngineeringWorkItemSnapshot item) => item.State switch
    {
        "FOUNDER_RELEASE_APPROVAL_REQUIRED" => "LEGEND Engineering release approval required",
        "CI_FAILED_NEEDS_EVIDENCE" => "LEGEND Engineering CI needs review",
        "RELEASE_BLOCKED" => "LEGEND Engineering release blocked",
        "FOUNDER_ESCALATION" => "LEGEND Engineering needs Founder review",
        _ when item.RiskClass == EngineeringRiskClass.TierC => "LEGEND Engineering security review required",
        _ => "LEGEND Engineering P1 incident"
    };

    private static string Detail(EngineeringWorkItemSnapshot item)
        => $"Work item {item.WorkItemId:D}; priority {item.PriorityClass}; failure {item.FailureClass}; risk {item.RiskClass}; state {item.State}. No private customer data or secret values are included.";

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