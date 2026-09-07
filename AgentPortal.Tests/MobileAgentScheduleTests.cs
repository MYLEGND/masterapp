using System;
using System.Linq;
using System.Threading.Tasks;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Messaging;
using Infrastructure.Mobile;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileAgentScheduleTests
{
    private static MobileResolvedActor Actor(string id = "agent-a", string role = MessagingParticipantTypes.Agent) =>
        new(new MessagingActor(id, role), Guid.NewGuid(), "Agent");

    [Fact]
    public async Task Schedule_UsesCanonicalBookings_ConvertsLeadLinks_AndRefreshesWithoutCopies()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = Guid.NewGuid().ToString(), FirstName = "Converted", LastName = "Client",
            CrmStatus = "Active", CrmNotes = ClientCrmMetaSerializer.Serialize(new ClientCrmMeta { RecordType = "Client", SourceWorkstationLeadId = "original-lead" }) };
        var crmLead = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "crm-lead", FirstName = "CRM", LastName = "Lead", CrmStatus = "Lead" };
        db.ClientProfiles.AddRange(client, crmLead);
        db.AgentClients.AddRange(new AgentClient { AgentUserId = "agent-a", ClientUserId = client.ClientUserId },
            new AgentClient { AgentUserId = "agent-a", ClientUserId = crmLead.ClientUserId });
        db.WorkstationLeadProfiles.AddRange(
            new WorkstationLeadProfile { LeadId = "original-lead", AgentUserId = "agent-a", FirstName = "Old lead" },
            new WorkstationLeadProfile { LeadId = "workstation-lead", AgentUserId = "agent-a", FirstName = "Workstation" });
        var converted = Booking("original-lead");
        var lead = Booking("crm-lead");
        lead.ClientProfileId = crmLead.Id.ToString("N");
        var workstation = Booking("workstation-lead");
        db.LeadAppointments.AddRange(converted, lead, workstation);
        await db.SaveChangesAsync();
        var service = new MobileAgentCrmService(db);
        var result = await service.ScheduleAsync(Actor(), default);
        Assert.Equal(3, result.Count);
        Assert.Contains(result, x => x.Id == converted.Id && x.Kind == "Client" && x.RecordId == client.Id.ToString());
        Assert.Contains(result, x => x.Id == lead.Id && x.Kind == "Lead" && x.RecordId == crmLead.ClientUserId);
        Assert.Contains(result, x => x.Id == workstation.Id && x.Kind == "Lead");
        Assert.All(result, x => Assert.NotNull(x.RecordId));
        // A portal cancellation/reschedule changes the next mobile read directly.
        converted.Status = LeadAppointmentStatus.Cancelled;
        lead.ScheduledStartUtc = DateTime.UtcNow.AddDays(4);
        await db.SaveChangesAsync();
        result = await service.ScheduleAsync(Actor(), default);
        Assert.DoesNotContain(result, x => x.Id == converted.Id);
        Assert.Equal(lead.ScheduledStartUtc, result.Single(x => x.Id == lead.Id).StartUtc);
        Assert.Equal(3, db.LeadAppointments.Count());
    }

    [Fact]
    public async Task Schedule_IsOwnerScoped_OnlyBookedUpcomingOrOngoing_AndNotLimitedToHomePreview()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var future = Enumerable.Range(0, 12).Select(_ => Booking(null)).ToArray();
        db.LeadAppointments.AddRange(future);
        var ongoing = Booking(null); ongoing.ScheduledStartUtc = DateTime.UtcNow.AddMinutes(-10); ongoing.ScheduledEndUtc = DateTime.UtcNow.AddMinutes(20);
        db.LeadAppointments.Add(ongoing);
        foreach (var status in new[] { LeadAppointmentStatus.Cancelled, LeadAppointmentStatus.Completed, LeadAppointmentStatus.NoShow, LeadAppointmentStatus.Requested, LeadAppointmentStatus.FailedConfirmation })
        { var excluded = Booking(null); excluded.Status = status; db.LeadAppointments.Add(excluded); }
        var other = Booking(null); other.OwnerAgentUserId = "agent-b"; db.LeadAppointments.Add(other);
        var past = Booking(null); past.ScheduledStartUtc = DateTime.UtcNow.AddDays(-1); db.LeadAppointments.Add(past);
        await db.SaveChangesAsync();
        var service = new MobileAgentCrmService(db);
        var result = await service.ScheduleAsync(Actor(), default);
        Assert.Equal(13, result.Count);
        Assert.Equal(ongoing.Id, result[0].Id);
        Assert.DoesNotContain(result, x => x.Id == other.Id || x.Id == past.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ScheduleAsync(Actor(role: MessagingParticipantTypes.Client), default));
    }

    [Fact]
    public async Task Record_RejectsUnassignedClientsAndLeads_AndReturnsCanonicalManagementLinks()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = Guid.NewGuid().ToString(), FirstName = "Owned" };
        db.ClientProfiles.Add(profile);
        db.AgentClients.Add(new AgentClient { AgentUserId = "agent-a", ClientUserId = profile.ClientUserId });
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile { LeadId = "owned-lead", AgentUserId = "agent-a" });
        await db.SaveChangesAsync();
        var service = new MobileAgentCrmService(db);
        var record = await service.RecordAsync(Actor(), "clients", profile.Id.ToString(), default);
        Assert.NotNull(record);
        Assert.Equal("/Clients/Edit?clientUserId=" + profile.ClientUserId, record.AccountPath);
        Assert.Null(await service.RecordAsync(Actor("agent-b"), "clients", profile.Id.ToString(), default));
        Assert.Null(await service.RecordAsync(Actor("agent-b"), "leads", "owned-lead", default));
        Assert.NotNull(await service.RecordAsync(Actor(), "leads", "owned-lead", default));
    }

    private static LeadAppointment Booking(string? leadId) => new() {
        Id = Guid.NewGuid(), OwnerAgentUserId = "agent-a", WorkstationLeadId = leadId,
        Status = LeadAppointmentStatus.Booked, ScheduledStartUtc = DateTime.UtcNow.AddDays(1),
        MeetingUrl = "https://example.com/meeting"
    };
}
