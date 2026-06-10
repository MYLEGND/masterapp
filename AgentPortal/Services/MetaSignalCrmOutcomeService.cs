using System.Text.Json;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

public sealed class MetaSignalCrmOutcomeService
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<MetaSignalCrmOutcomeService> _logger;

    public MetaSignalCrmOutcomeService(
        MasterAppDbContext db,
        ILogger<MetaSignalCrmOutcomeService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RecordAppointmentCompletedAsync(LeadAppointment appointment, CancellationToken cancellationToken = default)
    {
        if (appointment.Status != LeadAppointmentStatus.Completed)
            return;

        var dedupKey = $"AppointmentCompleted:{appointment.Id:N}";
        if (await AlreadyRecordedAsync("AppointmentCompleted", dedupKey, cancellationToken))
            return;

        var websiteLeadId = await ResolveWebsiteLeadIdAsync(appointment.WorkstationLeadId, appointment.WebsiteLeadIntakeLinkId, cancellationToken);

        var row = BuildRow(
            eventName: "AppointmentCompleted",
            eventId: $"appointment_completed_{appointment.Id:N}",
            dedupKey: dedupKey,
            websiteLeadId: websiteLeadId,
            quoteType: "crm",
            funnelStep: 5,
            stepName: "appointment_completed",
            scoreTier: "AppointmentCompleted",
            totalScore: 160,
            metadata: new
            {
                appointmentId = appointment.Id,
                appointment.WorkstationLeadId,
                appointment.OwnerAgentUserId,
                appointment.CalendarEventId,
                appointment.ScheduledStartUtc,
                appointment.ScheduledEndUtc,
                appointment.CompletedUtc,
                appointment.BookingSource,
                appointment.ConfirmationSource
            });

        _db.MetaSignalEvents.Add(row);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "MetaSignal CRM outcome recorded event={EventName} appointmentId={AppointmentId} leadId={LeadId}",
            row.EventName,
            appointment.Id,
            websiteLeadId);
    }

    public async Task RecordProductionOutcomeAsync(
        string agentUserId,
        ProductionSide side,
        ProductionStatus status,
        string? leadId,
        string? clientUserId,
        decimal amount,
        decimal personalAmount,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var eventName = status switch
        {
            ProductionStatus.Submitted => "ApplicationSubmitted",
            ProductionStatus.Issued => "PolicyIssued",
            ProductionStatus.Paid => "PolicyPaid",
            _ => null
        };

        if (eventName == null)
            return;

        var contactKey = side == ProductionSide.Lead ? leadId : clientUserId;
        if (string.IsNullOrWhiteSpace(contactKey))
            return;

        var dedupKey = $"{eventName}:{side}:{contactKey}:{amount:0.00}:{personalAmount:0.00}";
        if (await AlreadyRecordedAsync(eventName, dedupKey, cancellationToken))
            return;

        var websiteLeadId = await ResolveProductionWebsiteLeadIdAsync(
            side,
            leadId,
            clientUserId,
            cancellationToken);

        var row = BuildRow(
            eventName: eventName,
            eventId: $"{eventName.ToLowerInvariant()}_{Guid.NewGuid():N}",
            dedupKey: dedupKey,
            websiteLeadId: websiteLeadId,
            quoteType: "crm",
            funnelStep: status switch
            {
                ProductionStatus.Submitted => 6,
                ProductionStatus.Issued => 7,
                ProductionStatus.Paid => 8,
                _ => 0
            },
            stepName: status switch
            {
                ProductionStatus.Submitted => "application_submitted",
                ProductionStatus.Issued => "policy_issued",
                ProductionStatus.Paid => "policy_paid",
                _ => "production_outcome"
            },
            scoreTier: eventName,
            totalScore: status switch
            {
                ProductionStatus.Submitted => 220,
                ProductionStatus.Issued => 320,
                ProductionStatus.Paid => 500,
                _ => 0
            },
            metadata: new
            {
                agentUserId,
                side = side.ToString(),
                status = status.ToString(),
                leadId,
                clientUserId,
                amount,
                personalAmount,
                notes
            });

        _db.MetaSignalEvents.Add(row);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "MetaSignal CRM outcome recorded event={EventName} side={Side} contact={ContactKey} amount={Amount}",
            row.EventName,
            side,
            contactKey,
            amount);
    }

    private async Task<Guid?> ResolveProductionWebsiteLeadIdAsync(
        ProductionSide side,
        string? leadId,
        string? clientUserId,
        CancellationToken cancellationToken)
    {
        if (side == ProductionSide.Lead)
            return await ResolveWebsiteLeadIdAsync(leadId, null, cancellationToken);

        // Many converted clients preserve the original workstation lead id as ClientUserId.
        // This lets client-side production remain attributable to the original website lead.
        var byClientUserId = await ResolveWebsiteLeadIdAsync(clientUserId, null, cancellationToken);
        if (byClientUserId.HasValue)
            return byClientUserId.Value;

        // Defensive fallback for mixed caller paths where leadId may still be populated.
        return await ResolveWebsiteLeadIdAsync(leadId, null, cancellationToken);
    }

    private async Task<bool> AlreadyRecordedAsync(string eventName, string dedupKey, CancellationToken cancellationToken)
        => await _db.MetaSignalEvents
            .AsNoTracking()
            .AnyAsync(x => x.EventName == eventName && x.MetaDeduplicationKey == dedupKey, cancellationToken);

    private async Task<Guid?> ResolveWebsiteLeadIdAsync(string? workstationLeadId, Guid? intakeLinkId, CancellationToken cancellationToken)
    {
        if (intakeLinkId.HasValue)
        {
            var byIntake = await _db.WebsiteLeadIntakeLinks
                .AsNoTracking()
                .Where(x => x.Id == intakeLinkId.Value)
                .Select(x => (Guid?)x.WebsiteLeadPublicId)
                .FirstOrDefaultAsync(cancellationToken);

            if (byIntake.HasValue)
                return byIntake.Value;
        }

        if (string.IsNullOrWhiteSpace(workstationLeadId))
            return null;

        return await _db.WebsiteLeadIntakeLinks
            .AsNoTracking()
            .Where(x => x.WorkstationLeadId == workstationLeadId)
            .OrderByDescending(x => x.SubmittedUtc)
            .ThenByDescending(x => x.CapturedUtc)
            .Select(x => (Guid?)x.WebsiteLeadPublicId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static MetaSignalEvent BuildRow(
        string eventName,
        string eventId,
        string dedupKey,
        Guid? websiteLeadId,
        string quoteType,
        int funnelStep,
        string stepName,
        string scoreTier,
        int totalScore,
        object metadata)
        => new()
        {
            CreatedUtc = DateTime.UtcNow,
            EventId = eventId,
            EventName = eventName,
            EventCategory = "conversion",
            LeadId = websiteLeadId,
            QuoteType = quoteType,
            TrafficType = "crm",
            FunnelStep = funnelStep,
            StepName = stepName,
            IntentScore = totalScore,
            EngagementScore = totalScore,
            QualificationScore = totalScore,
            FrictionScore = 0,
            TotalSignalScore = totalScore,
            ScoreTier = scoreTier,
            MetaBrowserSent = false,
            MetaServerSent = false,
            MetaDeduplicationKey = dedupKey,
            Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            Host = "AgentPortal",
            MetadataJson = JsonSerializer.Serialize(metadata)
        };
}
