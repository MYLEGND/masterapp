namespace Domain.Enums;

public enum LeadAppointmentStatus
{
    Requested = 0,
    Booked = 1,
    Confirmed = 2,
    Completed = 3,
    NoShow = 4,
    Cancelled = 5,
    Rescheduled = 6,
    SchedulingOffered = 7,
    FailedConfirmation = 8
}
