namespace Domain.Entities;

public class AppointmentSyncLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? AppointmentId { get; set; }
    public string? WorkstationLeadId { get; set; }
    public string? ClientProfileId { get; set; }
    public string? AgentUserId { get; set; }

    public string? CalendarUserId { get; set; }
    public string? CalendarEmail { get; set; }
    public string? GraphSubscriptionId { get; set; }
    public string? GraphEventId { get; set; }

    public string Operation { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? DiagnosticJson { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
