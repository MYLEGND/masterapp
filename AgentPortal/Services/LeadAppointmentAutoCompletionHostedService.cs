using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

public sealed class LeadAppointmentAutoCompletionHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CompletionGracePeriod = TimeSpan.FromMinutes(15);
    private const int BatchSize = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LeadAppointmentAutoCompletionHostedService> _logger;

    public LeadAppointmentAutoCompletionHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<LeadAppointmentAutoCompletionHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CompleteEndedAppointmentsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lead appointment auto-completion failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task CompleteEndedAppointmentsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var metaSignalOutcomes = scope.ServiceProvider.GetRequiredService<MetaSignalCrmOutcomeService>();

        var nowUtc = DateTime.UtcNow;
        var cutoffUtc = nowUtc.Subtract(CompletionGracePeriod);

        var appointments = await db.LeadAppointments
            .Where(x =>
                (x.Status == LeadAppointmentStatus.Booked || x.Status == LeadAppointmentStatus.Confirmed) &&
                x.ScheduledEndUtc != null &&
                x.ScheduledEndUtc <= cutoffUtc &&
                x.CalendarEventId != null &&
                x.CalendarEventId != "")
            .OrderBy(x => x.ScheduledEndUtc)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (appointments.Count == 0)
            return;

        foreach (var appointment in appointments)
        {
            appointment.ApplyStatus(LeadAppointmentStatus.Completed, nowUtc);
            appointment.UpdatedUtc = nowUtc;
            appointment.LastSyncStatus = "auto_completed_after_scheduled_end";
            appointment.LastSyncError = null;

            await metaSignalOutcomes.RecordAppointmentCompletedAsync(appointment, cancellationToken);

            _logger.LogInformation(
                "Lead appointment auto-completed appointmentId={AppointmentId} workstationLeadId={WorkstationLeadId} scheduledEndUtc={ScheduledEndUtc}",
                appointment.Id,
                appointment.WorkstationLeadId,
                appointment.ScheduledEndUtc);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
