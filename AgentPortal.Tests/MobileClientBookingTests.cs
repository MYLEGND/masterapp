using System.Net.Http;
using AgentPortal.Controllers;
using Domain.Enums;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using Moq;
using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using AgentPortal.Mobile;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileClientBookingTests
{
    [Fact]
    public async Task BookingTicket_IsClientScoped_AndRevokedByReassignmentOrInactiveAgent()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = Guid.NewGuid().ToString(), FirstName = "Assigned" };
        var other = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = Guid.NewGuid().ToString() };
        var agent = new AgentProfile { Id = Guid.NewGuid(), AgentUserId = "agent-a", IsActive = true };
        db.AgentProfiles.Add(agent); db.ClientProfiles.AddRange(profile, other);
        var assignment = new AgentClient { AgentUserId = "agent-a", ClientUserId = profile.ClientUserId };
        db.AgentClients.Add(assignment); await db.SaveChangesAsync();
        var controller = new MobileClientBookingController(new MobileActorResolver(db, NullLogger<MobileActorResolver>.Instance),
            db, new EphemeralDataProtectionProvider(), new AgentTimeZoneResolver(),
            ControllerTestHelpers.BuildCalendarController(db, ControllerTestHelpers.BuildUser(),
                new HttpClientHandler(), new GraphServiceClient(Mock.Of<IRequestAdapter>()))) {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", "agent-a") }, "test")),
                RequestServices = new ServiceCollection().BuildServiceProvider()
            } }
        };
        controller.Request.Headers[MobileApiAuthorization.ParticipantTypeHeader] = "Agent";
        var denied = Assert.IsType<ObjectResult>(await controller.Launch(other.Id, default));
        Assert.Equal(403, denied.StatusCode);
        var launch = Assert.IsType<OkObjectResult>(await controller.Launch(profile.Id, default));
        var path = JsonSerializer.SerializeToElement(launch.Value).GetProperty("launchPath").GetString()!;
        var ticket = Uri.UnescapeDataString(path.Split("?ticket=")[1]);
        var page = Assert.IsType<ViewResult>(await controller.Page(ticket, default));
        Assert.Equal(profile.Id, Assert.IsType<MobileClientBookingPage>(page.Model).ProfileId);
        Assert.IsType<UnauthorizedResult>(await controller.Page(ticket + "tampered", default));
        controller.Request.Headers["X-Legend-Booking-Ticket"] = ticket;
        var createRequest = new CalendarController.CreateEventRequest { ClientProfileId = other.Id, ClientUserId = other.ClientUserId };
        // Missing booking fields reach the canonical validation only after the
        // bridge has replaced caller-supplied client IDs with the ticket scope.
        Assert.IsType<BadRequestObjectResult>(await controller.Create(createRequest, default));
        Assert.Equal(profile.Id, createRequest.ClientProfileId);
        Assert.Equal(profile.ClientUserId, createRequest.ClientUserId);
        db.AgentClients.Add(new AgentClient { AgentUserId = "agent-a", ClientUserId = other.ClientUserId });
        var otherBooking = new LeadAppointment { Id = Guid.NewGuid(), OwnerAgentUserId = "agent-a", ClientProfileId = other.Id.ToString(),
            Status = LeadAppointmentStatus.Booked, ScheduledStartUtc = DateTime.UtcNow.AddDays(1), ScheduledEndUtc = DateTime.UtcNow.AddDays(1).AddMinutes(30) };
        db.LeadAppointments.Add(otherBooking); await db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>(await controller.Update(new CalendarController.UpdateAppointmentRequest { AppointmentId = otherBooking.Id }, default));
        Assert.IsType<NotFoundResult>(await controller.Cancel(new CalendarController.CancelAppointmentRequest { AppointmentId = otherBooking.Id }, default));
        db.AgentClients.Remove(assignment); await db.SaveChangesAsync();
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await controller.Page(ticket, default)).StatusCode);
        db.AgentClients.Add(assignment); agent.IsActive = false; await db.SaveChangesAsync();
        var inactive = Assert.IsType<ObjectResult>(await controller.Page(ticket, default));
        Assert.Equal(403, inactive.StatusCode);
    }

    [Fact]
    public async Task ClientRole_CannotLaunchBookingEvenWhenAnAgentAssignmentExists()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "client-a" };
        db.ClientProfiles.Add(client); await db.SaveChangesAsync();
        var controller = new MobileClientBookingController(new MobileActorResolver(db, NullLogger<MobileActorResolver>.Instance),
            db, new EphemeralDataProtectionProvider(), new AgentTimeZoneResolver(), null!) {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", "client-a") }, "test")),
                RequestServices = new ServiceCollection().BuildServiceProvider()
            } }
        };
        controller.Request.Headers[MobileApiAuthorization.ParticipantTypeHeader] = MessagingParticipantTypes.Client;
        var result = Assert.IsType<ObjectResult>(await controller.Launch(client.Id, default));
        Assert.Equal(403, result.StatusCode);
    }
}
