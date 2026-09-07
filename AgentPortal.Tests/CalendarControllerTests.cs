using System;
using System.Collections.Generic;
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
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Solutions.BookingBusinesses.Item.GetStaffAvailability;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Serialization.Json;
using Moq;
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
            CreatedUtc = new DateTime(2027, 5, 21, 7, 0, 0, DateTimeKind.Utc),
            UpdatedUtc = new DateTime(2027, 5, 21, 7, 0, 0, DateTimeKind.Utc)
        });
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "agent-1",
            AgentUpn = "agent@example.test",
            BookingPageIdOrMailbox = "booking-business-1"
        });
        db.WebsiteLeadIntakeLinks.Add(new WebsiteLeadIntakeLink
        {
            Id = intakeId,
            WebsiteLeadRowId = 501,
            WebsiteLeadPublicId = Guid.NewGuid(),
            WorkstationLeadId = "L-CALENDAR-1",
            AgentUserId = "agent-1",
            Bucket = "MortgageProtection",
            SubmittedUtc = new DateTime(2027, 5, 21, 7, 30, 0, DateTimeKind.Utc),
            CapturedUtc = new DateTime(2027, 5, 21, 7, 31, 0, DateTimeKind.Utc),
            SourcePageKey = "mortgage_protection_paid"
        });
        await db.SaveChangesAsync();

        var requestAdapter = new Mock<IRequestAdapter>();
        requestAdapter.SetupGet(adapter => adapter.BaseUrl).Returns("https://graph.microsoft.com/v1.0");
        requestAdapter.SetupGet(adapter => adapter.SerializationWriterFactory).Returns(new JsonSerializationWriterFactory());
        requestAdapter
            .Setup(adapter => adapter.SendAsync(
                It.Is<RequestInformation>(request => request.HttpMethod == Method.GET),
                It.IsAny<ParsableFactory<BookingServiceCollectionResponse>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingServiceCollectionResponse
            {
                Value = new List<BookingService>
                {
                    new()
                    {
                        Id = "booking-service-30",
                        DisplayName = "30-minute review",
                        DefaultDuration = TimeSpan.FromMinutes(30),
                        IsHiddenFromCustomers = false,
                        StaffMemberIds = new List<string> { "staff-1" }
                    }
                }
            });
        requestAdapter
            .Setup(adapter => adapter.SendAsync(
                It.Is<RequestInformation>(request => request.HttpMethod == Method.POST),
                It.IsAny<ParsableFactory<BookingAppointment>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingAppointment
            {
                Id = "evt-123",
                SelfServiceAppointmentId = "https://outlook.test/events/evt-123"
            });

        requestAdapter.Setup(adapter => adapter.SendAsync(
            It.Is<RequestInformation>(request => request.HttpMethod == Method.POST),
            It.IsAny<ParsableFactory<GetStaffAvailabilityPostResponse>>(),
            It.IsAny<Dictionary<string, ParsableFactory<IParsable>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetStaffAvailabilityPostResponse { Value = new List<StaffAvailabilityItem> {
                new() { AvailabilityItems = new List<AvailabilityItem> { new() {
                    Status = BookingsAvailabilityStatus.Available,
                    StartDateTime = new DateTimeTimeZone { DateTime = "2027-05-21T07:00:00", TimeZone = "UTC" },
                    EndDateTime = new DateTimeTimeZone { DateTime = "2027-05-21T19:00:00", TimeZone = "UTC" }
                } } }
            } });
        requestAdapter.Setup(adapter => adapter.SendAsync(
            It.Is<RequestInformation>(request => request.HttpMethod == Method.GET),
            It.IsAny<ParsableFactory<BookingAppointmentCollectionResponse>>(),
            It.IsAny<Dictionary<string, ParsableFactory<IParsable>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingAppointmentCollectionResponse { Value = new List<BookingAppointment>() });

        var graphClient = new GraphServiceClient(requestAdapter.Object);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var controller = ControllerTestHelpers.BuildCalendarController(db, ControllerTestHelpers.BuildUser(), handler, graphClient);

        var result = await controller.CreateEvent(new CalendarController.CreateEventRequest
        {
            ClientUserId = "L-CALENDAR-1",
            Subject = "Mortgage review",
            StartISO = "2027-05-21T09:00:00",
            EndISO = "2027-05-21T09:30:00",
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
        var expectedStartUtc = new DateTime(2027, 5, 21, 9, 0, 0, DateTimeKind.Utc);
        var expectedEndUtc = new DateTime(2027, 5, 21, 9, 30, 0, DateTimeKind.Utc);

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

        var availability = Assert.IsType<OkObjectResult>(await controller.DayAvailability("2027-05-21"));
        var live = JsonSerializer.SerializeToElement(availability.Value);
        Assert.DoesNotContain(live.GetProperty("freeSlots").EnumerateArray(), slot =>
            DateTime.Parse(slot.GetProperty("startIso").GetString()!, CultureInfo.InvariantCulture) <= expectedStartUtc &&
            DateTime.Parse(slot.GetProperty("endIso").GetString()!, CultureInfo.InvariantCulture) >= expectedEndUtc);
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile { LeadId = "second-client", AgentUserId = "agent-1", FirstName = "Second" });
        await db.SaveChangesAsync();
        var staleBooking = await controller.CreateEvent(new CalendarController.CreateEventRequest {
            ClientUserId = "second-client", Subject = "Conflicting booking", StartISO = "2027-05-21T09:00:00", EndISO = "2027-05-21T09:30:00"
        });
        Assert.IsType<ConflictObjectResult>(staleBooking);
        Assert.Single(await db.LeadAppointments.ToListAsync());

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
        requestAdapter.Setup(adapter => adapter.SendAsync(
            It.Is<RequestInformation>(request => request.HttpMethod == Method.PATCH),
            It.IsAny<ParsableFactory<BookingAppointment>>(),
            It.IsAny<Dictionary<string, ParsableFactory<IParsable>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingAppointment { Id = "evt-123" });
        var changed = await controller.UpdateAppointment(new CalendarController.UpdateAppointmentRequest {
            AppointmentId = appointment.Id, ClientUserId = "L-CALENDAR-1",
            StartISO = "2027-05-21T10:00:00", EndISO = "2027-05-21T10:30:00"
        });
        Assert.IsType<OkObjectResult>(changed);
        Assert.Single(await db.LeadAppointments.ToListAsync());
        Assert.Equal(LeadAppointmentStatus.Rescheduled, appointment.Status);
        Assert.Equal(new DateTime(2027, 5, 21, 10, 0, 0, DateTimeKind.Utc), appointment.ScheduledStartUtc);
        requestAdapter.Setup(adapter => adapter.SendNoContentAsync(
            It.Is<RequestInformation>(request => request.HttpMethod == Method.DELETE),
            It.IsAny<Dictionary<string, ParsableFactory<IParsable>>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Assert.IsType<OkObjectResult>(await controller.CancelAppointment(new CalendarController.CancelAppointmentRequest {
            AppointmentId = appointment.Id, ClientUserId = "L-CALENDAR-1"
        }));
        Assert.Equal(LeadAppointmentStatus.Cancelled, appointment.Status);
        var reopened = Assert.IsType<OkObjectResult>(await controller.DayAvailability("2027-05-21"));
        var released = JsonSerializer.SerializeToElement(reopened.Value);
        Assert.Contains(released.GetProperty("freeSlots").EnumerateArray(), slot =>
            DateTime.Parse(slot.GetProperty("startIso").GetString()!, CultureInfo.InvariantCulture) <= expectedStartUtc &&
            DateTime.Parse(slot.GetProperty("endIso").GetString()!, CultureInfo.InvariantCulture) >= appointment.ScheduledEndUtc);
    }

    private static GraphServiceClient BuildGraphClient()
    {
        var requestAdapter = new Mock<IRequestAdapter>();
        requestAdapter.SetupGet(adapter => adapter.BaseUrl).Returns("https://graph.microsoft.com/v1.0");
        requestAdapter.SetupGet(adapter => adapter.SerializationWriterFactory).Returns(new JsonSerializationWriterFactory());
        return new GraphServiceClient(requestAdapter.Object);

    }

    // SECURITY REGRESSION (Calendar appointment IDOR):
    // An authenticated agent must not be able to mutate another agent's
    // appointment by supplying their OWN (unrelated) client to satisfy the
    // ownership gate. Fixed code rejects at the authorization gate (before any
    // Microsoft Bookings/Graph call), so the attacker receives the gate's
    // "context not found" NotFound and the victim's appointment is unchanged.
    [Fact]
    public async Task CancelAppointment_Rejects_ForeignAppointment_WhenAgentSuppliesOwnClient()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        var victimProfileId = Guid.NewGuid();
        var attackerProfileId = Guid.NewGuid();

        db.ClientProfiles.Add(new ClientProfile
        {
            Id = victimProfileId,
            ClientUserId = "client-victim",
            FirstName = "Vic",
            LastName = "Tim",
            Email = "victim@example.com",
            NormalizedEmail = "victim@example.com",
            CreatedUtc = DateTime.UtcNow
        });
        db.ClientProfiles.Add(new ClientProfile
        {
            Id = attackerProfileId,
            ClientUserId = "client-attacker",
            FirstName = "At",
            LastName = "Tacker",
            Email = "attacker@example.com",
            NormalizedEmail = "attacker@example.com",
            CreatedUtc = DateTime.UtcNow
        });
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-victim",
            ClientUserId = "client-victim",
            CreatedUtc = DateTime.UtcNow
        });
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-attacker",
            ClientUserId = "client-attacker",
            CreatedUtc = DateTime.UtcNow
        });

        var appointmentId = Guid.NewGuid();
        db.LeadAppointments.Add(new LeadAppointment
        {
            Id = appointmentId,
            OwnerAgentUserId = "agent-victim",
            ClientProfileId = victimProfileId.ToString(),
            CalendarEventId = "evt-victim",
            Status = LeadAppointmentStatus.Booked,
            ScheduledStartUtc = new DateTime(2026, 5, 21, 9, 0, 0, DateTimeKind.Utc),
            ScheduledEndUtc = new DateTime(2026, 5, 21, 9, 30, 0, DateTimeKind.Utc),
            CreatedUtc = DateTime.UtcNow
        });
        // Give the attacker a booking profile so that, on vulnerable code, the
        // flow would have proceeded past configuration checks toward mutation.
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "agent-attacker",
            AgentUpn = "attacker@example.test",
            BookingPageIdOrMailbox = "attacker-booking"
        });
        await db.SaveChangesAsync();

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var controller = ControllerTestHelpers.BuildCalendarController(
            db,
            ControllerTestHelpers.BuildUser("agent-attacker"),
            handler,
            BuildGraphClient());

        var result = await controller.CancelAppointment(new CalendarController.CancelAppointmentRequest
        {
            AppointmentId = appointmentId,
            ClientProfileId = attackerProfileId // attacker's OWN client, unrelated to the appointment
        });

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains("context", notFound.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var unchanged = await db.LeadAppointments.SingleAsync(x => x.Id == appointmentId);
        Assert.Equal("agent-victim", unchanged.OwnerAgentUserId);
        Assert.Equal(LeadAppointmentStatus.Booked, unchanged.Status);
    }

    // No-regression companion: the legitimate owner still clears the
    // authorization gate. With no live Bookings event linked, the request is
    // then rejected downstream with a BadRequest, proving the gate was passed
    // without requiring any Graph interaction.
    [Fact]
    public async Task CancelAppointment_Allows_Owner_PastAuthorizationGate()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        var clientProfileId = Guid.NewGuid();
        db.ClientProfiles.Add(new ClientProfile
        {
            Id = clientProfileId,
            ClientUserId = "client-owned",
            FirstName = "Owned",
            LastName = "Client",
            Email = "owned@example.com",
            NormalizedEmail = "owned@example.com",
            CreatedUtc = DateTime.UtcNow
        });
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-owner",
            ClientUserId = "client-owned",
            CreatedUtc = DateTime.UtcNow
        });

        var appointmentId = Guid.NewGuid();
        db.LeadAppointments.Add(new LeadAppointment
        {
            Id = appointmentId,
            OwnerAgentUserId = "agent-owner",
            ClientProfileId = clientProfileId.ToString(),
            CalendarEventId = null, // not linked to a live Bookings event
            Status = LeadAppointmentStatus.Booked,
            ScheduledStartUtc = new DateTime(2026, 5, 21, 9, 0, 0, DateTimeKind.Utc),
            ScheduledEndUtc = new DateTime(2026, 5, 21, 9, 30, 0, DateTimeKind.Utc),
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var controller = ControllerTestHelpers.BuildCalendarController(
            db,
            ControllerTestHelpers.BuildUser("agent-owner"),
            handler,
            BuildGraphClient());

        var result = await controller.CancelAppointment(new CalendarController.CancelAppointmentRequest
        {
            AppointmentId = appointmentId,
            ClientProfileId = clientProfileId
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("not linked", badRequest.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
