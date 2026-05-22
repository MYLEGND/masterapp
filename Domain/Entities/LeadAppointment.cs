using System;
using Domain.Enums;

namespace Domain.Entities;

public class LeadAppointment
{
    public Guid Id { get; set; }

    public string WorkstationLeadId { get; set; } = "";
    public WorkstationLeadProfile? WorkstationLead { get; set; }

    public string OwnerAgentUserId { get; set; } = "";

    public Guid? WebsiteLeadIntakeLinkId { get; set; }
    public WebsiteLeadIntakeLink? WebsiteLeadIntakeLink { get; set; }

    public LeadAppointmentStatus Status { get; set; } = LeadAppointmentStatus.Requested;
    public string BookingSource { get; set; } = LeadAppointmentBookingSources.InternalManual;
    public string RequestedBookingSource { get; set; } = LeadAppointmentBookingSources.InternalManual;
    public string? ConfirmationSource { get; set; }
    public string? BookingConfigurationSource { get; set; }
    public Guid? BookingTrackingProfileId { get; set; }
    public string? BookingAgentSlug { get; set; }
    public string? BookingAgentUserId { get; set; }
    public string? BookingCalendarUserId { get; set; }
    public string? BookingCalendarEmail { get; set; }
    public string? BookingPageIdOrMailbox { get; set; }

    public string? CalendarEventId { get; set; }
    public string? CalendarEventWebLink { get; set; }
    public DateTime? ScheduledStartUtc { get; set; }
    public DateTime? ScheduledEndUtc { get; set; }
    public string? MeetingUrl { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastStatusChangedUtc { get; set; }

    public DateTime? RequestedUtc { get; set; }
    public DateTime? BookedUtc { get; set; }
    public DateTime? ConfirmedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public DateTime? NoShowUtc { get; set; }
    public DateTime? CancelledUtc { get; set; }
    public DateTime? RescheduledUtc { get; set; }

    public void ApplyStatus(LeadAppointmentStatus status, DateTime utcNow)
    {
        Status = status;
        LastStatusChangedUtc = utcNow;
        UpdatedUtc = utcNow;

        switch (status)
        {
            case LeadAppointmentStatus.Requested:
                RequestedUtc = utcNow;
                break;
            case LeadAppointmentStatus.Booked:
                BookedUtc = utcNow;
                break;
            case LeadAppointmentStatus.Confirmed:
                ConfirmedUtc = utcNow;
                break;
            case LeadAppointmentStatus.Completed:
                CompletedUtc = utcNow;
                break;
            case LeadAppointmentStatus.NoShow:
                NoShowUtc = utcNow;
                break;
            case LeadAppointmentStatus.Cancelled:
                CancelledUtc = utcNow;
                break;
            case LeadAppointmentStatus.Rescheduled:
                RescheduledUtc = utcNow;
                break;
        }
    }
}

public static class LeadAppointmentBookingSources
{
    public const string InternalManual = "internal_manual";
    public const string InternalCalendar = "internal_calendar";
    public const string WorkstationCalendar = InternalCalendar;
    public const string WebsiteEmbed = "website_embed";
    public const string WebsiteModal = "website_modal";
    public const string ExternalRedirectFallback = "external_redirect_fallback";
    public const string MicrosoftGraphConfirmation = "microsoft_graph_confirmation";
    public const string ManualVerified = "manual_verified";
}
