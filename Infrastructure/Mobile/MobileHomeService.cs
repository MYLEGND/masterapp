using Domain.Entities;
using Domain.Enums;
using Domain.FinancialIntelligence;
using Domain.JourneyCircles;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.DailyScripture;
using Infrastructure.Households;
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
/// profile, financial, messaging, or Journey Circles state;
/// every value is projected from an existing authoritative service or table.
/// </summary>
public sealed class MobileHomeService : IMobileHomeService
{
    private readonly MasterAppDbContext _db;
    private readonly IMessagingService _messaging;
    private readonly IJourneyCirclesService _journeyCircles;
    private readonly IFinancialIntelligenceEvaluationService _financialIntelligence;
    private readonly IMobileFinancialOperatingSystemProjectionService _financialOperatingSystem;
    private readonly IHouseholdMembershipService _households;
    private readonly IDailyScriptureService _dailyScripture;

    public MobileHomeService(
        MasterAppDbContext db,
        IMessagingService messaging,
        IJourneyCirclesService journeyCircles,
        IFinancialIntelligenceEvaluationService financialIntelligence,
        IMobileFinancialOperatingSystemProjectionService financialOperatingSystem,
        IHouseholdMembershipService households,
        IDailyScriptureService dailyScripture)
    {
        _db = db;
        _messaging = messaging;
        _journeyCircles = journeyCircles;
        _financialIntelligence = financialIntelligence;
        _financialOperatingSystem = financialOperatingSystem;
        _households = households;
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
        if (string.Equals(
                actor.Actor.ParticipantType,
                MessagingParticipantTypes.Client,
                StringComparison.Ordinal))
        {
            return await GetClientFinancialAsync(actor, cancellationToken);
        }

        if (string.Equals(
                actor.Actor.ParticipantType,
                MessagingParticipantTypes.Agent,
                StringComparison.Ordinal))
        {
            return await GetAgentFinancialAsync(actor, cancellationToken);
        }

        return MobileFinancialResult.Unavailable(
            "MOBILE_FINANCIAL_UNAVAILABLE",
            "Financial intelligence is not available for this mobile identity.");
    }

    private async Task<MobileFinancialResult> GetClientFinancialAsync(
        MobileResolvedActor actor,
        CancellationToken cancellationToken)
    {
        var financialScope = await _households.ResolveActiveAccessAsync(
            actor.ProfileId,
            cancellationToken);
        if (!financialScope.HasActiveMembership ||
            !financialScope.HouseholdAccountId.HasValue ||
            !financialScope.SubscriptionOwnerClientProfileId.HasValue)
        {
            return MobileFinancialResult.Unavailable(
                "MOBILE_FINANCIAL_HOUSEHOLD_ACCESS_REQUIRED",
                "Financial Health Snapshot is available through your active household account.");
        }

        var persistedState = await _db.FinanceToolStates
            .AsNoTracking()
            .Where(state =>
                state.HouseholdAccountId == financialScope.HouseholdAccountId.Value &&
                state.ToolId == LegendLivingBalanceSheetConstants.ToolId)
            .Select(state => new MobilePersistedFinanceState(state.JsonState, state.UpdatedUtc))
            .FirstOrDefaultAsync(cancellationToken);
        MobileFinancialHealthProjection? healthProjection = null;
        if (persistedState is not null)
        {
            var protectionPeople = await ResolveClientProtectionPeopleAsync(
                financialScope.HouseholdAccountId.Value,
                financialScope.SubscriptionOwnerClientProfileId.Value,
                actor.DisplayName,
                cancellationToken);
            healthProjection = TryProjectFinancialHealth(
                persistedState,
                financialScope.SubscriptionOwnerClientProfileId.Value,
                protectionPeople);
        }
        var position = healthProjection?.Position;

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
            presentation,
            healthProjection?.HealthSnapshot));
    }

    private async Task<MobileFinancialResult> GetAgentFinancialAsync(
        MobileResolvedActor actor,
        CancellationToken cancellationToken)
    {
        var normalizedAgentUserId = actor.Actor.UserId.Trim().ToLowerInvariant();
        var persistedState = await _db.AgentFinanceToolStates
            .AsNoTracking()
            .Where(state =>
                state.AgentUserId.ToLower() == normalizedAgentUserId &&
                state.ToolId == LegendLivingBalanceSheetConstants.ToolId)
            .OrderByDescending(state => state.UpdatedUtc)
            .Select(state => new MobilePersistedFinanceState(
                state.JsonState,
                state.UpdatedUtc))
            .FirstOrDefaultAsync(cancellationToken);

        var healthProjection = persistedState is null
            ? null
            : TryProjectFinancialHealth(
                persistedState,
                clientProfileId: null,
                new MobileFinancialProtectionPeople(
                    FirstName(actor.DisplayName),
                    PartnerFirstName: null));
        var position = healthProjection?.Position;

        var operatingSystem = await _financialOperatingSystem.ProjectAgentAsync(
            actor.Actor.UserId,
            cancellationToken);
        var presentation = MobileFinancialPresentationEvaluator.Evaluate(
            position,
            intelligence: null,
            upcomingBills: Array.Empty<MobileUpcomingBill>(),
            operatingSystem: operatingSystem,
            assignedAgent: new MobileFinancialAssignedAgentContext(
                HasAssignedAgent: false,
                DisplayName: null,
                FirstName: null));

        return MobileFinancialResult.Success(new MobileFinancialSnapshot(
            position,
            Intelligence: null,
            UpcomingBills: Array.Empty<MobileUpcomingBill>(),
            OperatingSystem: operatingSystem,
            Presentation: presentation,
            HealthSnapshot: healthProjection?.HealthSnapshot));
    }

    /// <summary>
    /// Rejects missing, malformed, or non-object persisted state instead of
    /// presenting a fabricated empty balance sheet. Valid state is normalized
    /// and calculated only by the established shared calculator.
    /// </summary>
    private static MobileFinancialHealthProjection? TryProjectFinancialHealth(
        MobilePersistedFinanceState persistedState,
        Guid? clientProfileId,
        MobileFinancialProtectionPeople protectionPeople)
    {
        if (string.IsNullOrWhiteSpace(persistedState.JsonState))
            return null;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                persistedState.JsonState);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;

            var normalizedJson = LegendLivingBalanceSheetCalculator.NormalizeJson(
                persistedState.JsonState,
                clientProfileId);
            var state = System.Text.Json.JsonSerializer.Deserialize<
                LegendLivingBalanceSheetState>(
                normalizedJson,
                new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web));
            if (state is null)
                return null;

            state = LegendLivingBalanceSheetCalculator.Calculate(state);
            return new MobileFinancialHealthProjection(
                new MobileFinancialPosition(
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
                    persistedState.UpdatedUtc),
                MobileFinancialHealthSnapshotProjection.Create(
                    state,
                    persistedState.UpdatedUtc,
                    protectionPeople));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private async Task<MobileFinancialProtectionPeople>
        ResolveClientProtectionPeopleAsync(
            Guid householdAccountId,
            Guid subscriptionOwnerClientProfileId,
            string actorDisplayName,
            CancellationToken cancellationToken)
    {
        var owner = await _db.ClientProfiles
            .AsNoTracking()
            .Where(profile => profile.Id == subscriptionOwnerClientProfileId)
            .Select(profile => new MobileFinancialProfileNames(
                profile.FirstName,
                profile.SignificantOtherFirstName))
            .SingleOrDefaultAsync(cancellationToken);

        var householdPeople = await (
                from membership in _db.HouseholdMemberships.AsNoTracking()
                join profile in _db.ClientProfiles.AsNoTracking()
                    on membership.ClientProfileId equals profile.Id
                where membership.HouseholdAccountId == householdAccountId &&
                    membership.Status == HouseholdMembershipStatus.Active
                select new MobileFinancialHouseholdPerson(
                    membership.Role,
                    profile.FirstName))
            .ToListAsync(cancellationToken);

        var primaryFirstName = householdPeople
            .Where(person => person.Role == HouseholdMemberRole.PrimaryOwner)
            .Select(person => FirstName(person.FirstName))
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? FirstName(owner?.FirstName)
            ?? FirstName(actorDisplayName);
        var partnerFirstName = householdPeople
            .Where(person => person.Role == HouseholdMemberRole.Partner)
            .Select(person => FirstName(person.FirstName))
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? FirstName(owner?.SignificantOtherFirstName);

        return new MobileFinancialProtectionPeople(
            primaryFirstName,
            partnerFirstName);
    }

    private static string? FirstName(string? displayName) => displayName?
        .Trim()
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault();

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

        var profiles = await LegendMemberDirectory.ActiveSubscribedProfiles(_db)
            .Where(profile => assignedClientUserIds.Contains(profile.ClientUserId.ToLower()))
            .ToListAsync(cancellationToken);

        var clients = LegendMemberDirectory.Collapse(profiles)
            .Select(profile => new MobileAgentClient(
                profile.Id,
                string.Join(" ", new[] { profile.FirstName, profile.LastName }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim(),
                profile.Email,
                profile.CrmStatus ?? "Active"))
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
        var journey = await _journeyCircles.GetDashboardAsync(actor.Actor.UserId, cancellationToken);
        var appointments = await QueryAppointmentsForClientAsync(actor.ProfileId, cancellationToken);

        return new MobileHome(
            MobileHomeIdentity.From(actor),
            messaging,
            new MobileJourneySummary(
                journey.Profile is not null,
                journey.Recommendations.Count,
                journey.Connections.Count(connection => string.Equals(connection.Status, JourneyCircleConnectionStatuses.Accepted, StringComparison.Ordinal)),
                journey.Requests.Count),
            appointments,
            Array.Empty<MobileActionItem>(),
            ToMobileDailyScripture(await _dailyScripture.GetTodayAsync(cancellationToken)),
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
            appointments,
            actions,
            ToMobileDailyScripture(await _dailyScripture.GetTodayAsync(cancellationToken)),
            activeClientCount);
    }


    private static MobileDailyScripture ToMobileDailyScripture(
        DailyScriptureRecord scripture) => new(
        scripture.Date,
        scripture.Reference,
        scripture.Translation,
        scripture.Verses,
        scripture.Text,
        scripture.Source,
        scripture.PassageText ?? scripture.Text);

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

    private sealed record MobileFinancialProfileNames(
        string? FirstName,
        string? SignificantOtherFirstName);

    private sealed record MobileFinancialHouseholdPerson(
        HouseholdMemberRole Role,
        string? FirstName);

    private sealed record MobileFinancialHealthProjection(
        MobileFinancialPosition Position,
        MobileFinancialHealthSnapshot HealthSnapshot);

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
    MobileJourneySummary? Journey,
    IReadOnlyList<MobileUpcomingAppointment> UpcomingAppointments,
    IReadOnlyList<MobileActionItem> Actions,
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
public sealed record MobileJourneySummary(bool HasProfile, int RecommendationCount, int ConnectedPeerCount, int PendingRequestCount);
public sealed record MobileUpcomingAppointment(Guid Id, DateTime StartUtc, DateTime? EndUtc, string Status);
public sealed record MobileActionItem(Guid Id, string Title, string Status, string Priority, DateTime? DueDateUtc);
public sealed record MobileDailyScripture(
    string Date,
    string Reference,
    string Translation,
    IReadOnlyList<string> Verses,
    string Text,
    string Source,
    string PassageText);
public sealed record MobileFinancialSnapshot(
    MobileFinancialPosition? Position,
    MobileFinancialIntelligenceSummary? Intelligence,
    IReadOnlyList<MobileUpcomingBill> UpcomingBills,
    MobileFinancialOperatingSystemSnapshot? OperatingSystem = null,
    MobileFinancialPresentation? Presentation = null,
    MobileFinancialHealthSnapshot? HealthSnapshot = null);
public sealed record MobileFinancialPosition(int HealthScore, decimal AssetsTotal, decimal LiabilitiesTotal, decimal NetWorth, decimal AnnualEarnings, decimal AnnualLifestyleRemaining, decimal AnnualTaxes, decimal ProtectionGapTotal, string PositionStatus, string PositionSummary, string EstatePlanningStatus, string EstatePlanningRiskLevel, DateTime UpdatedUtc);
public sealed record MobileFinancialIntelligenceSummary(string Status, decimal DataCompletenessScore, string CurrentRiskSummary, string CurrentOpportunitySummary, string CurrentLeakageSummary, DateTime? LastEvaluatedUtc, IReadOnlyList<MobileFinancialFinding> Findings);
public sealed record MobileFinancialFinding(Guid Id, string Category, string Title, string Explanation, decimal? EstimatedImpact, string? ImpactUnit, string Urgency, string Status, DateTime LastDetectedUtc);
public sealed record MobileUpcomingBill(Guid Id, string DisplayName, long AverageAmountCents, string Cadence, DateTime NextExpectedDateUtc, string Status);
public sealed record MobileAgentClient(Guid ProfileId, string DisplayName, string Email, string CrmStatus);
public sealed record MobileAgentLead(string LeadId, string DisplayName, string CrmStage, DateTime UpdatedUtc);
