using Domain.Billing;
using Domain.Entities;
using Domain.Enums;
using Domain.FinancialIntelligence;
using Domain.JourneyCircles;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.DailyScripture;
using DailyScriptureRecord = Infrastructure.DailyScripture.DailyScripture;
using Microsoft.EntityFrameworkCore;
using Shared.Finance;

namespace Infrastructure.Mobile;

public interface IMobileHomeService
{
    Task<MobileHomeResult> GetHomeAsync(MobileResolvedActor actor, CancellationToken cancellationToken = default);

    Task<MobileFinancialResult> GetFinancialAsync(MobileResolvedActor actor, CancellationToken cancellationToken = default);

    Task<MobileAgentClientsResult> GetAgentClientsAsync(MobileResolvedActor actor, CancellationToken cancellationToken = default);

    Task<MobileAgentLeadsResult> GetAgentLeadsAsync(MobileResolvedActor actor, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only composition layer for the native application. It never owns
/// profile, subscription, financial, messaging, or Journey Circles state;
/// every value is projected from an existing authoritative service or table.
/// </summary>
public sealed class MobileHomeService : IMobileHomeService
{
    private readonly MasterAppDbContext _db;
    private readonly IMessagingService _messaging;
    private readonly IJourneyCirclesService _journeyCircles;
    private readonly IFinancialIntelligenceEvaluationService _financialIntelligence;
    private readonly IBillingEntitlementService _entitlements;
    private readonly IMobileFinancialOperatingSystemProjectionService _financialOperatingSystem;
    private readonly IDailyScriptureService _dailyScripture;

    public MobileHomeService(
        MasterAppDbContext db,
        IMessagingService messaging,
        IJourneyCirclesService journeyCircles,
        IFinancialIntelligenceEvaluationService financialIntelligence,
        IBillingEntitlementService entitlements,
        IMobileFinancialOperatingSystemProjectionService financialOperatingSystem,
        IDailyScriptureService dailyScripture)
    {
        _db = db;
        _messaging = messaging;
        _journeyCircles = journeyCircles;
        _financialIntelligence = financialIntelligence;
        _entitlements = entitlements;
        _financialOperatingSystem = financialOperatingSystem;
        _dailyScripture = dailyScripture;
    }

    public async Task<MobileHomeResult> GetHomeAsync(
        MobileResolvedActor actor,
        CancellationToken cancellationToken = default)
    {
        var messaging = await _messaging.ListConversationsAsync(
            actor.Actor,
            new MessagingConversationListQuery(),
            cancellationToken);
        if (!messaging.Succeeded)
        {
            return MobileHomeResult.Failure(
                messaging.ErrorCode ?? "MOBILE_MESSAGING_UNAVAILABLE",
                messaging.ErrorMessage ?? "Your mobile home could not load securely.");
        }

        var messagingSummary = new MobileMessagingSummary(
            messaging.Conversations.Sum(conversation => Math.Max(0, conversation.UnreadCount)),
            messaging.Conversations.Count);

        return string.Equals(actor.Actor.ParticipantType, MessagingParticipantTypes.Client, StringComparison.Ordinal)
            ? MobileHomeResult.Success(await BuildClientHomeAsync(actor, messagingSummary, cancellationToken))
            : MobileHomeResult.Success(await BuildAgentHomeAsync(actor, messagingSummary, cancellationToken));
    }

    public async Task<MobileFinancialResult> GetFinancialAsync(
        MobileResolvedActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(actor.Actor.ParticipantType, MessagingParticipantTypes.Client, StringComparison.Ordinal))
        {
            return MobileFinancialResult.Unavailable(
                "MOBILE_FINANCIAL_UNAVAILABLE",
                "Financial intelligence is available from a client mobile identity.");
        }

        var persistedState = await _db.FinanceToolStates
            .AsNoTracking()
            .Where(state =>
                state.ClientProfileId == actor.ProfileId &&
                state.ToolId == LegendLivingBalanceSheetConstants.ToolId)
            .OrderByDescending(state => state.UpdatedUtc)
            .Select(state => new MobilePersistedFinanceState(state.JsonState, state.UpdatedUtc))
            .FirstOrDefaultAsync(cancellationToken);

        MobileFinancialPosition? position = null;
        if (persistedState is not null)
        {
            var normalizedJson = LegendLivingBalanceSheetCalculator.NormalizeJson(persistedState.JsonState, actor.ProfileId);
            var state = System.Text.Json.JsonSerializer.Deserialize<LegendLivingBalanceSheetState>(
                normalizedJson,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            state = LegendLivingBalanceSheetCalculator.Calculate(state);
            position = new MobileFinancialPosition(
                state.Summary.HealthScore,
                state.Summary.AssetsTotal,
                state.Summary.LiabilitiesTotal,
                state.Summary.NetWorth,
                state.CashFlow.Earnings,
                state.CashFlow.LifestyleRemaining,
                state.Summary.Taxes,
                state.Summary.ProtectionGapTotal,
                state.Summary.PositionStatus,
                state.Summary.PositionSummary,
                state.Summary.EstatePlanningStatus,
                state.Summary.EstatePlanningRiskLevel,
                persistedState.UpdatedUtc);
        }

        var intelligence = await _financialIntelligence.GetSnapshotAsync(
            new FinancialIntelligenceActor(
                actor.ProfileId,
                actor.Actor.UserId,
                FinancialIntelligenceActorTypes.Client),
            cancellationToken);

        var upcomingBills = await _db.RecurringFinancialStreams
            .AsNoTracking()
            .Where(stream =>
                stream.ClientProfileId == actor.ProfileId &&
                stream.NextExpectedDateUtc != null &&
                stream.Status != "Inactive")
            .OrderBy(stream => stream.NextExpectedDateUtc)
            .Take(8)
            .Select(stream => new MobileUpcomingBill(
                stream.Id,
                stream.DisplayName,
                stream.AverageAmountCents,
                stream.Cadence,
                stream.NextExpectedDateUtc!.Value,
                stream.Status))
            .ToListAsync(cancellationToken);

        var operatingSystem = await _financialOperatingSystem.ProjectAsync(
            actor.ProfileId,
            cancellationToken);

        var assignedAgent = await ResolveFinancialAssignedAgentAsync(
            actor.ProfileId,
            cancellationToken);

        var intelligenceSummary = intelligence is null
            ? null
            : new MobileFinancialIntelligenceSummary(
                intelligence.Status,
                intelligence.DataCompletenessScore,
                intelligence.CurrentRiskSummary,
                intelligence.CurrentOpportunitySummary,
                intelligence.CurrentLeakageSummary,
                intelligence.LastEvaluatedUtc,
                intelligence.Findings.Select(finding => new MobileFinancialFinding(
                    finding.Id,
                    finding.Category,
                    finding.Title,
                    finding.Explanation,
                    finding.EstimatedImpact,
                    finding.ImpactUnit,
                    finding.Urgency,
                    finding.Status,
                    finding.LastDetectedUtc)).ToArray());

        var presentation = MobileFinancialPresentationEvaluator.Evaluate(
            position,
            intelligenceSummary,
            upcomingBills,
            operatingSystem,
            assignedAgent);

        return MobileFinancialResult.Success(new MobileFinancialSnapshot(
            position,
            intelligenceSummary,
            upcomingBills,
            operatingSystem,
            presentation));
    }

    private async Task<MobileFinancialAssignedAgentContext>
        ResolveFinancialAssignedAgentAsync(
            Guid clientProfileId,
            CancellationToken cancellationToken)
    {
        var clientUserId = await _db.ClientProfiles
            .AsNoTracking()
            .Where(profile => profile.Id == clientProfileId)
            .Select(profile => profile.ClientUserId)
            .SingleOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(clientUserId))
        {
            return new MobileFinancialAssignedAgentContext(false, null, null);
        }

        var normalizedClientUserId = clientUserId.Trim().ToLowerInvariant();
        var assignedAgentUserId = await _db.AgentClients
            .AsNoTracking()
            .Where(link =>
                link.ClientUserId.ToLower() == normalizedClientUserId &&
                !string.IsNullOrWhiteSpace(link.AgentUserId))
            .OrderByDescending(link => link.CreatedUtc)
            .ThenBy(link => link.Id)
            .Select(link => link.AgentUserId)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(assignedAgentUserId))
        {
            return new MobileFinancialAssignedAgentContext(false, null, null);
        }

        var normalizedAgentUserId = assignedAgentUserId.Trim().ToLowerInvariant();
        var displayName = await _db.AgentProfiles
            .AsNoTracking()
            .Where(profile =>
                profile.IsActive &&
                profile.AgentUserId.ToLower() == normalizedAgentUserId)
            .Select(profile => profile.FullName)
            .FirstOrDefaultAsync(cancellationToken);

        var normalizedDisplayName = string.IsNullOrWhiteSpace(displayName)
            ? null
            : displayName.Trim();
        var firstName = normalizedDisplayName?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return new MobileFinancialAssignedAgentContext(
            true,
            normalizedDisplayName,
            firstName);
    }

    public async Task<MobileAgentClientsResult> GetAgentClientsAsync(
        MobileResolvedActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(actor.Actor.ParticipantType, MessagingParticipantTypes.Agent, StringComparison.Ordinal))
        {
            return MobileAgentClientsResult.Unavailable(
                "MOBILE_AGENT_ROLE_REQUIRED",
                "Client CRM is available from an agent mobile identity.");
        }

        var agentUserId = actor.Actor.UserId.ToLowerInvariant();
        var assignedClientUserIds = await _db.AgentClients
            .AsNoTracking()
            .Where(link => link.AgentUserId.ToLower() == agentUserId)
            .Select(link => link.ClientUserId.ToLower())
            .Distinct()
            .ToListAsync(cancellationToken);

        var profileRows = await _db.ClientProfiles
            .AsNoTracking()
            .Where(profile => assignedClientUserIds.Contains(profile.ClientUserId.ToLower()))
            .Select(profile => new MobileAgentClientProfileRow(
                profile.Id,
                profile.ClientUserId,
                profile.FirstName,
                profile.LastName,
                profile.Email,
                profile.CrmStatus,
                profile.CrmNotes,
                profile.ProfileImageContent,
                profile.ProfileImageContentType))
            .ToListAsync(cancellationToken);

        var clients = profileRows
            .Where(profile => ClientRecordClassification.IsClientOrBusinessClient(profile.ClientUserId, profile.CrmNotes))
            .Select(profile => new MobileAgentClient(
                profile.Id,
                string.Join(" ", new[] { profile.FirstName, profile.LastName }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim(),
                profile.Email,
                profile.CrmStatus ?? "Active",
                profile.ProfileImageContent,
                profile.ProfileImageContentType))
            .OrderBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return MobileAgentClientsResult.Success(clients);
    }

    public async Task<MobileAgentLeadsResult> GetAgentLeadsAsync(
        MobileResolvedActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(actor.Actor.ParticipantType, MessagingParticipantTypes.Agent, StringComparison.Ordinal))
        {
            return MobileAgentLeadsResult.Unavailable(
                "MOBILE_AGENT_ROLE_REQUIRED",
                "Lead CRM is available from an agent mobile identity.");
        }

        var agentUserId = actor.Actor.UserId.ToLowerInvariant();
        var leadRows = await _db.WorkstationLeadProfiles
            .AsNoTracking()
            .Where(lead =>
                lead.AgentUserId.ToLower() == agentUserId &&
                (lead.CrmStage == null || lead.CrmStage.ToLower() != "notinterested"))
            .OrderByDescending(lead => lead.UpdatedUtc)
            .Take(50)
            .Select(lead => new MobileAgentLeadProfileRow(
                lead.LeadId,
                lead.FirstName,
                lead.LastName,
                lead.CrmStage ?? "New",
                lead.UpdatedUtc))
            .ToListAsync(cancellationToken);

        return MobileAgentLeadsResult.Success(leadRows
            .Select(lead => new MobileAgentLead(
                lead.LeadId,
                string.Join(" ", new[] { lead.FirstName, lead.LastName }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim(),
                lead.CrmStage,
                lead.UpdatedUtc))
            .ToArray());
    }

    private async Task<MobileHome> BuildClientHomeAsync(
        MobileResolvedActor actor,
        MobileMessagingSummary messaging,
        CancellationToken cancellationToken)
    {
        var subscription = await _db.ClientSubscriptions
            .AsNoTracking()
            .Where(item => item.ClientProfileId == actor.ProfileId)
            .OrderByDescending(item => item.UpdatedUtc)
            .Select(item => new MobileSubscription(
                item.Id,
                item.Status.ToString(),
                item.PaymentStanding.ToString(),
                item.MonthlyAmountCents,
                item.Currency,
                item.NextBillingDateUtc,
                item.CurrentPeriodStartUtc,
                item.CurrentPeriodEndUtc,
                item.CancelAtPeriodEnd))
            .FirstOrDefaultAsync(cancellationToken);

        var entitlement = await _entitlements.EvaluateAsync(
            new BillingEntitlementEvaluationRequest(
                actor.ProfileId,
                BillingEntitlementKeys.ClientAppFullAccess,
                DateTime.UtcNow),
            cancellationToken);

        var journey = await _journeyCircles.GetDashboardAsync(actor.Actor.UserId, cancellationToken);
        var financial = await GetFinancialAsync(actor, cancellationToken);
        var appointments = await QueryAppointmentsForClientAsync(actor.ProfileId, cancellationToken);
        var notifications = await _db.ClientBillingNotifications
            .AsNoTracking()
            .Where(notification => notification.ClientProfileId == actor.ProfileId)
            .OrderByDescending(notification => notification.SentUtc ?? notification.NotBeforeUtc)
            .Take(8)
            .Select(notification => new MobileBillingNotification(
                notification.Id,
                notification.Kind.ToString(),
                notification.Subject,
                notification.SentUtc ?? notification.NotBeforeUtc))
            .ToListAsync(cancellationToken);

        return new MobileHome(
            MobileHomeIdentity.From(actor),
            messaging,
            subscription,
            new MobileEntitlement(
                entitlement.Status.ToString(),
                entitlement.EffectiveUtc,
                entitlement.ExpirationUtc,
                entitlement.GraceOrSuspensionUtc,
                entitlement.ReasonCode,
                entitlement.Summary),
            new MobileJourneySummary(
                journey.Profile is not null,
                journey.Recommendations.Count,
                journey.Connections.Count(connection => string.Equals(connection.Status, JourneyCircleConnectionStatuses.Accepted, StringComparison.Ordinal)),
                journey.Requests.Count),
            financial.Snapshot,
            appointments,
            Array.Empty<MobileActionItem>(),
            notifications,
            ToMobileDailyScripture(_dailyScripture.GetTodayUtc()),
            0);
    }

    private async Task<MobileHome> BuildAgentHomeAsync(
        MobileResolvedActor actor,
        MobileMessagingSummary messaging,
        CancellationToken cancellationToken)
    {
        var normalizedAgentUserId = actor.Actor.UserId.ToLower();
        var activeClientCount = await _db.AgentClients
            .AsNoTracking()
            .CountAsync(link => link.AgentUserId.ToLower() == normalizedAgentUserId, cancellationToken);
        var actions = await _db.ActionItems
            .AsNoTracking()
            .Where(item =>
                item.EffectiveAgentOid.ToLower() == normalizedAgentUserId &&
                item.Status != ActionStatus.Completed &&
                item.Status != ActionStatus.Dismissed)
            .OrderBy(item => item.DueDateUtc == null)
            .ThenBy(item => item.DueDateUtc)
            .Take(8)
            .Select(item => new MobileActionItem(
                item.Id,
                item.Title,
                item.Status.ToString(),
                item.Priority.ToString(),
                item.DueDateUtc))
            .ToListAsync(cancellationToken);
        var appointments = await _db.LeadAppointments
            .AsNoTracking()
            .Where(appointment =>
                appointment.OwnerAgentUserId.ToLower() == normalizedAgentUserId &&
                appointment.ScheduledStartUtc != null &&
                appointment.ScheduledStartUtc >= DateTime.UtcNow &&
                appointment.Status != LeadAppointmentStatus.Cancelled)
            .OrderBy(appointment => appointment.ScheduledStartUtc)
            .Take(8)
            .Select(appointment => new MobileUpcomingAppointment(
                appointment.Id,
                appointment.ScheduledStartUtc!.Value,
                appointment.ScheduledEndUtc,
                appointment.Status.ToString()))
            .ToListAsync(cancellationToken);

        return new MobileHome(
            MobileHomeIdentity.From(actor),
            messaging,
            null,
            null,
            null,
            null,
            appointments,
            actions,
            Array.Empty<MobileBillingNotification>(),
            ToMobileDailyScripture(_dailyScripture.GetTodayUtc()),
            activeClientCount);
    }


    private static MobileDailyScripture ToMobileDailyScripture(
        DailyScriptureRecord scripture) => new(
        scripture.Date,
        scripture.Reference,
        scripture.Translation,
        scripture.Verses,
        scripture.Text);

    private Task<List<MobileUpcomingAppointment>> QueryAppointmentsForClientAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken)
    {
        var compactId = clientProfileId.ToString("N");
        var canonicalId = clientProfileId.ToString("D");
        return _db.LeadAppointments
            .AsNoTracking()
            .Where(appointment =>
                (appointment.ClientProfileId == compactId || appointment.ClientProfileId == canonicalId) &&
                appointment.ScheduledStartUtc != null &&
                appointment.ScheduledStartUtc >= DateTime.UtcNow &&
                appointment.Status != LeadAppointmentStatus.Cancelled)
            .OrderBy(appointment => appointment.ScheduledStartUtc)
            .Take(8)
            .Select(appointment => new MobileUpcomingAppointment(
                appointment.Id,
                appointment.ScheduledStartUtc!.Value,
                appointment.ScheduledEndUtc,
                appointment.Status.ToString()))
            .ToListAsync(cancellationToken);
    }

    private sealed record MobilePersistedFinanceState(string JsonState, DateTime UpdatedUtc);

    private sealed record MobileAgentClientProfileRow(
        Guid Id,
        string ClientUserId,
        string FirstName,
        string LastName,
        string Email,
        string? CrmStatus,
        string? CrmNotes,
        byte[]? ProfileImageContent,
        string? ProfileImageContentType);

    private sealed record MobileAgentLeadProfileRow(
        string LeadId,
        string FirstName,
        string LastName,
        string CrmStage,
        DateTime UpdatedUtc);
}

public sealed record MobileHomeResult(bool Succeeded, string? ErrorCode, string? ErrorMessage, MobileHome? Home)
{
    public static MobileHomeResult Success(MobileHome home) => new(true, null, null, home);
    public static MobileHomeResult Failure(string errorCode, string errorMessage) => new(false, errorCode, errorMessage, null);
}

public sealed record MobileFinancialResult(bool Succeeded, string? ErrorCode, string? ErrorMessage, MobileFinancialSnapshot? Snapshot)
{
    public static MobileFinancialResult Success(MobileFinancialSnapshot snapshot) => new(true, null, null, snapshot);
    public static MobileFinancialResult Unavailable(string errorCode, string errorMessage) => new(false, errorCode, errorMessage, null);
}

public sealed record MobileAgentClientsResult(bool Succeeded, string? ErrorCode, string? ErrorMessage, IReadOnlyList<MobileAgentClient> Clients)
{
    public static MobileAgentClientsResult Success(IReadOnlyList<MobileAgentClient> clients) => new(true, null, null, clients);
    public static MobileAgentClientsResult Unavailable(string errorCode, string errorMessage) => new(false, errorCode, errorMessage, Array.Empty<MobileAgentClient>());
}

public sealed record MobileAgentLeadsResult(bool Succeeded, string? ErrorCode, string? ErrorMessage, IReadOnlyList<MobileAgentLead> Leads)
{
    public static MobileAgentLeadsResult Success(IReadOnlyList<MobileAgentLead> leads) => new(true, null, null, leads);
    public static MobileAgentLeadsResult Unavailable(string errorCode, string errorMessage) => new(false, errorCode, errorMessage, Array.Empty<MobileAgentLead>());
}

public sealed record MobileHome(
    MobileHomeIdentity Identity,
    MobileMessagingSummary Messaging,
    MobileSubscription? Subscription,
    MobileEntitlement? Entitlement,
    MobileJourneySummary? Journey,
    MobileFinancialSnapshot? Financial,
    IReadOnlyList<MobileUpcomingAppointment> UpcomingAppointments,
    IReadOnlyList<MobileActionItem> Actions,
    IReadOnlyList<MobileBillingNotification> Notifications,
    MobileDailyScripture DailyScripture,
    int ActiveClientCount);

public sealed record MobileHomeIdentity(string UserId, string ParticipantType, Guid ProfileId, string DisplayName)
{
    public static MobileHomeIdentity From(MobileResolvedActor actor) => new(
        actor.Actor.UserId,
        actor.Actor.ParticipantType,
        actor.ProfileId,
        actor.DisplayName);
}

public sealed record MobileMessagingSummary(int UnreadCount, int ConversationCount);
public sealed record MobileSubscription(Guid Id, string Status, string PaymentStanding, int MonthlyAmountCents, string Currency, DateTime? NextBillingDateUtc, DateTime? CurrentPeriodStartUtc, DateTime? CurrentPeriodEndUtc, bool CancelAtPeriodEnd);
public sealed record MobileEntitlement(string Status, DateTime? EffectiveUtc, DateTime? ExpirationUtc, DateTime? GraceOrSuspensionUtc, string? ReasonCode, string Summary);
public sealed record MobileJourneySummary(bool HasProfile, int RecommendationCount, int ConnectedPeerCount, int PendingRequestCount);
public sealed record MobileUpcomingAppointment(Guid Id, DateTime StartUtc, DateTime? EndUtc, string Status);
public sealed record MobileActionItem(Guid Id, string Title, string Status, string Priority, DateTime? DueDateUtc);
public sealed record MobileBillingNotification(Guid Id, string Kind, string Subject, DateTime OccurredUtc);
public sealed record MobileDailyScripture(string Date, string Reference, string Translation, IReadOnlyList<string> Verses, string Text);
public sealed record MobileFinancialSnapshot(
    MobileFinancialPosition? Position,
    MobileFinancialIntelligenceSummary? Intelligence,
    IReadOnlyList<MobileUpcomingBill> UpcomingBills,
    MobileFinancialOperatingSystemSnapshot? OperatingSystem = null,
    MobileFinancialPresentation? Presentation = null);
public sealed record MobileFinancialPosition(int HealthScore, decimal AssetsTotal, decimal LiabilitiesTotal, decimal NetWorth, decimal AnnualEarnings, decimal AnnualLifestyleRemaining, decimal AnnualTaxes, decimal ProtectionGapTotal, string PositionStatus, string PositionSummary, string EstatePlanningStatus, string EstatePlanningRiskLevel, DateTime UpdatedUtc);
public sealed record MobileFinancialIntelligenceSummary(string Status, decimal DataCompletenessScore, string CurrentRiskSummary, string CurrentOpportunitySummary, string CurrentLeakageSummary, DateTime? LastEvaluatedUtc, IReadOnlyList<MobileFinancialFinding> Findings);
public sealed record MobileFinancialFinding(Guid Id, string Category, string Title, string Explanation, decimal? EstimatedImpact, string? ImpactUnit, string Urgency, string Status, DateTime LastDetectedUtc);
public sealed record MobileUpcomingBill(Guid Id, string DisplayName, long AverageAmountCents, string Cadence, DateTime NextExpectedDateUtc, string Status);
public sealed record MobileAgentClient(Guid ProfileId, string DisplayName, string Email, string CrmStatus, byte[]? ProfileImageContent, string? ProfileImageContentType);
public sealed record MobileAgentLead(string LeadId, string DisplayName, string CrmStage, DateTime UpdatedUtc);
