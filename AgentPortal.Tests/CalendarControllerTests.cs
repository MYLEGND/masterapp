using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Models;
using Domain.Entities;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public class CalendarControllerTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    [Fact]
    public async Task CreateEvent_For_Lead_Persists_LeadAppointment()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var intakeId = Guid.NewGuid();

        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = "L-CALENDAR-1",
            AgentUserId = "agent-1",
            Bucket = "MortgageProtection",
            OriginalLeadType = "MortgageProtection",
            FirstName = "Taylor",
            LastName = "Calendar",
            Email = "taylor@example.com",
            Phone = "6025550188",
            CreatedUtc = new DateTime(2026, 5, 21, 7, 0, 0, DateTimeKind.Utc),
            UpdatedUtc = new DateTime(2026, 5, 21, 7, 0, 0, DateTimeKind.Utc)
        });
        db.WebsiteLeadIntakeLinks.Add(new WebsiteLeadIntakeLink
        {
            Id = intakeId,
            WebsiteLeadRowId = 501,
            WebsiteLeadPublicId = Guid.NewGuid(),
            WorkstationLeadId = "L-CALENDAR-1",
            AgentUserId = "agent-1",
            Bucket = "MortgageProtection",
            SubmittedUtc = new DateTime(2026, 5, 21, 7, 30, 0, DateTimeKind.Utc),
            CapturedUtc = new DateTime(2026, 5, 21, 7, 31, 0, DateTimeKind.Utc),
            SourcePageKey = "mortgage_protection_paid"
        });
        await db.SaveChangesAsync();

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(
                "{\"id\":\"evt-123\",\"webLink\":\"https://outlook.test/events/evt-123\"}",
                Encoding.UTF8,
                "application/json")
        });
        var controller = ControllerTestHelpers.BuildCalendarController(db, ControllerTestHelpers.BuildUser(), handler);

        var result = await controller.CreateEvent(new CalendarController.CreateEventRequest
        {
            ClientUserId = "L-CALENDAR-1",
            Subject = "Mortgage review",
            StartISO = "2026-05-21T09:00:00",
            EndISO = "2026-05-21T09:30:00",
            Body = "Review mortgage protection options.",
            Location = "Phone Call",
            ZoomJoinUrl = "https://zoom.example.com/j/abc",
            ActivityNote = "Calendar event created: Mortgage review"
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var payloadJson = JsonSerializer.Serialize(ok.Value);
        using var payloadDoc = JsonDocument.Parse(payloadJson);
        var latestAppointmentPayload = payloadDoc.RootElement.GetProperty("latestAppointment");

        var appointment = await db.LeadAppointments.SingleAsync();
        var expectedStartUtc = DateTime.ParseExact(
                "2026-05-21T09:00:00",
                "yyyy-MM-dd'T'HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal)
            .ToUniversalTime();
        var expectedEndUtc = DateTime.ParseExact(
                "2026-05-21T09:30:00",
                "yyyy-MM-dd'T'HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal)
            .ToUniversalTime();

        Assert.Equal("L-CALENDAR-1", appointment.WorkstationLeadId);
        Assert.Equal("agent-1", appointment.OwnerAgentUserId);
        Assert.Equal(intakeId, appointment.WebsiteLeadIntakeLinkId);
        Assert.Equal(LeadAppointmentStatus.Booked, appointment.Status);
        Assert.Equal(LeadAppointmentBookingSources.InternalCalendar, appointment.BookingSource);
        Assert.Equal(LeadAppointmentBookingSources.InternalCalendar, appointment.RequestedBookingSource);
        Assert.Equal(LeadAppointmentBookingSources.InternalCalendar, appointment.ConfirmationSource);
        Assert.Equal("evt-123", appointment.CalendarEventId);
        Assert.Equal("https://outlook.test/events/evt-123", appointment.CalendarEventWebLink);
        Assert.Equal(expectedStartUtc, appointment.ScheduledStartUtc);
        Assert.Equal(expectedEndUtc, appointment.ScheduledEndUtc);
        Assert.Equal("https://zoom.example.com/j/abc", appointment.MeetingUrl);
        Assert.NotNull(appointment.RequestedUtc);
        Assert.NotNull(appointment.BookedUtc);

        var lead = await db.WorkstationLeadProfiles.SingleAsync(x => x.LeadId == "L-CALENDAR-1");
        var meta = ClientCrmMetaSerializer.Deserialize(lead.CrmNotes);
        Assert.Equal("Phone Call", meta.MeetingLocation);
        Assert.Equal("https://zoom.example.com/j/abc", meta.ZoomJoinUrl);
        Assert.Equal("evt-123", meta.LastCalendarEventId);
        Assert.Equal("https://outlook.test/events/evt-123", meta.LastCalendarEventWebLink);
        var activity = Assert.Single(meta.Activities);
        Assert.Equal("Meeting", activity.Type);
        Assert.Equal("Calendar event created: Mortgage review", activity.Note);
        Assert.Equal("https://zoom.example.com/j/abc", activity.MeetingLink);
        Assert.Equal("evt-123", activity.CalendarEventId);

        Assert.Equal("Booked", latestAppointmentPayload.GetProperty("status").GetString());
        Assert.Equal("Booked", latestAppointmentPayload.GetProperty("statusLabel").GetString());
        Assert.Equal("internal_calendar", latestAppointmentPayload.GetProperty("bookingSource").GetString());
        Assert.Equal("Internal calendar", latestAppointmentPayload.GetProperty("bookingSourceLabel").GetString());
        Assert.Equal("internal_calendar", latestAppointmentPayload.GetProperty("requestedBookingSource").GetString());
        Assert.Equal("internal_calendar", latestAppointmentPayload.GetProperty("confirmationSource").GetString());
        Assert.True(latestAppointmentPayload.GetProperty("confirmationVerified").GetBoolean());
        Assert.Equal("Booked / verified", latestAppointmentPayload.GetProperty("confirmationStateLabel").GetString());
    }
}
