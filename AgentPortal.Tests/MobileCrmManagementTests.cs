using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using AgentPortal.Controllers;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Accounts;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileCrmManagementTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ContactValidation_AllowsMissingOptionalEmail(string? email)
    {
        var input = new CrmContactUpdate { FirstName = "Contact", Email = email, UpdatedUtc = DateTime.UtcNow };
        Assert.Null(input.Email);
        Assert.True(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(input,
            new System.ComponentModel.DataAnnotations.ValidationContext(input),
            new System.Collections.Generic.List<System.ComponentModel.DataAnnotations.ValidationResult>(), true));
    }

    [Fact]
    public async Task MobileContactSave_UsesResolvedAgentAndRoundTripsTheFullCanonicalContact()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.AgentProfiles.Add(new AgentProfile { Id = Guid.NewGuid(), AgentUserId = "agent-a", IsActive = true });
        var profile = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "crm-lead-contact", FirstName = "Before",
            Email = "before@example.com", NormalizedEmail = "before@example.com", UpdatedUtc = DateTime.UtcNow };
        db.ClientProfiles.Add(profile);
        db.AgentClients.Add(new AgentClient { AgentUserId = "agent-a", ClientUserId = profile.ClientUserId });
        await db.SaveChangesAsync();
        var writer = ControllerTestHelpers.BuildClientsController(db, Mock.Of<IExecutionEngine>(), Mock.Of<ICommitmentService>(), ControllerTestHelpers.BuildUser("different-web-context"));
        writer.ObjectValidator = Mock.Of<IObjectModelValidator>();
        using var services = new ServiceCollection().AddSingleton(writer).BuildServiceProvider();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = ControllerTestHelpers.BuildUser("agent-a"), RequestServices = services };
        http.Request.Headers[AgentPortal.Mobile.MobileApiAuthorization.ParticipantTypeHeader] = "Agent";
        var service = new MobileAgentCrmService(db);
        var controller = new AgentPortal.Mobile.MobileAgentCrmController(new MobileActorResolver(db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MobileActorResolver>.Instance), service) {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        var input = new CrmContactUpdate { FirstName = "Saved", LastName = "Contact", Email = "saved@example.com",
            Phone = "6025550101", Phone2 = "6025550102", AddressLine = "123 Main Street", City = "Phoenix", State = "AZ", ZipCode = "85001", UpdatedUtc = profile.UpdatedUtc };
        Assert.IsType<JsonResult>(await controller.Contact("clients", profile.Id.ToString(), input, default));
        db.ChangeTracker.Clear();
        var record = await service.RecordAsync(new MobileResolvedActor(new MessagingActor("agent-a", MessagingParticipantTypes.Agent), Guid.NewGuid(), "Agent"), "clients", profile.Id.ToString(), default);
        Assert.Equal("Saved Contact", record!.DisplayName);
        Assert.Equal("saved@example.com", record.Email);
        Assert.Equal("6025550101", record.Phone);
        Assert.Equal("6025550102", record.Phone2);
        Assert.Equal("123 Main Street", record.AddressLine);
        Assert.Equal("Phoenix", record.City);
        Assert.Equal("AZ", record.State);
        Assert.Equal("85001", record.ZipCode);
        Assert.IsType<ConflictObjectResult>(await controller.Contact("clients", profile.Id.ToString(), input, default));
    }

    [Fact]
    public async Task ContactEdits_UpdateCanonicalProfile_PreserveWorkflow_AndRejectStaleOrUnownedWrites()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "crm-lead-a", FirstName = "Before", Email = "before@example.com", NormalizedEmail = "before@example.com", CrmStatus = "Lead", UpdatedUtc = DateTime.UtcNow,
            CrmNotes = ClientCrmMetaSerializer.Serialize(new ClientCrmMeta { PipelineStage = "MeetingScheduled", ZoomJoinUrl = "https://zoom.us/j/123", AgentNotes = "Retain notes" }) };
        db.ClientProfiles.Add(profile);
        db.AgentClients.Add(new AgentClient { AgentUserId = "agent-a", ClientUserId = profile.ClientUserId });
        await db.SaveChangesAsync();
        var controller = ControllerTestHelpers.BuildClientsController(db, Mock.Of<IExecutionEngine>(), Mock.Of<ICommitmentService>(), ControllerTestHelpers.BuildUser("agent-a"));
        controller.ObjectValidator = Mock.Of<IObjectModelValidator>();
        var input = new CrmContactUpdate { FirstName = "After", LastName = "Name", Email = "after@example.com", Phone = "6025550100", City = "Phoenix", UpdatedUtc = profile.UpdatedUtc };
        Assert.IsType<JsonResult>(await controller.SaveContact(profile.ClientUserId, input));
        var record = await new MobileAgentCrmService(db).RecordAsync(new MobileResolvedActor(new MessagingActor("agent-a", MessagingParticipantTypes.Agent), Guid.NewGuid(), "Agent"), "leads", profile.ClientUserId, default);
        Assert.Equal("After Name", record!.DisplayName);
        Assert.Equal("after@example.com", record.Email);
        Assert.Equal("Phoenix", record.City);
        Assert.Equal("https://zoom.us/j/123", record.MeetingUrl);
        Assert.Equal("Retain notes", ClientCrmMetaSerializer.Deserialize(profile.CrmNotes)!.AgentNotes);
        Assert.IsType<ConflictObjectResult>(await controller.SaveContact(profile.ClientUserId, input));
        controller.HttpContext.User = ControllerTestHelpers.BuildUser("agent-b");
        Assert.IsType<NotFoundResult>(await controller.SaveContact(profile.ClientUserId, input));
    }

    [Fact]
    public async Task MobileMutations_RejectUnassignedAndArchivedRecordsBeforeInvokingCanonicalWriters()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.AgentProfiles.Add(new AgentProfile { Id = Guid.NewGuid(), AgentUserId = "agent-a", IsActive = true });
        var lead = new WorkstationLeadProfile { LeadId = "owned-lead", AgentUserId = "agent-b", CrmStage = "NewLead" };
        db.WorkstationLeadProfiles.Add(lead); await db.SaveChangesAsync();
        var controller = new AgentPortal.Mobile.MobileAgentCrmController(
            new MobileActorResolver(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<MobileActorResolver>.Instance), new MobileAgentCrmService(db)) {
            ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = ControllerTestHelpers.BuildUser("agent-a") } }
        };
        controller.Request.Headers[AgentPortal.Mobile.MobileApiAuthorization.ParticipantTypeHeader] = "Agent";
        Assert.IsType<NotFoundResult>(await controller.Contact("leads", lead.LeadId, new CrmContactUpdate(), default));
        Assert.IsType<NotFoundResult>(await controller.Outcome("leads", lead.LeadId, new("Contacted", null), default));
        Assert.IsType<NotFoundResult>(await controller.Cancel(Guid.NewGuid(), default));
        lead.AgentUserId = "agent-a"; lead.CrmStatus = "Deleted"; await db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>(await controller.Contact("leads", lead.LeadId, new CrmContactUpdate(), default));
    }

    [Fact]
    public async Task LeadContactEdits_PreserveCanonicalPipelineAndNotes()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var lead = new WorkstationLeadProfile { LeadId = "lead-a", AgentUserId = "agent-a", FirstName = "Before", CrmStage = "FollowUp", CrmNotes = "{\"agentNotes\":\"Keep\"}", UpdatedUtc = DateTime.UtcNow };
        db.WorkstationLeadProfiles.Add(lead); await db.SaveChangesAsync();
        var controller = ControllerTestHelpers.BuildLeadsController(db, Mock.Of<IExecutionEngine>(), Mock.Of<ICommitmentService>(), ControllerTestHelpers.BuildUser("agent-a"));
        controller.ObjectValidator = Mock.Of<IObjectModelValidator>();
        Assert.IsType<JsonResult>(await controller.SaveContact(lead.LeadId, new CrmContactUpdate { FirstName = "Updated", LastName = "Lead", Email = "lead@example.com", Phone = "6025550199", UpdatedUtc = lead.UpdatedUtc }));
        Assert.Equal("Updated", lead.FirstName); Assert.Equal("lead@example.com", lead.Email);
        Assert.Equal("FollowUp", lead.CrmStage); Assert.Equal("{\"agentNotes\":\"Keep\"}", lead.CrmNotes);
    }

    [Fact]
    public async Task ArchiveUsesLifecycleAuthority_AndMeetingLinkFallsBackToCrmMetadata()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "archived-lead", FirstName = "Retained", CrmStatus = "Lead", CrmNotes = ClientCrmMetaSerializer.Serialize(new ClientCrmMeta { ZoomJoinUrl = "https://zoom.us/j/456" }) };
        db.ClientProfiles.Add(profile); db.AgentClients.Add(new AgentClient { AgentUserId = "agent-a", ClientUserId = profile.ClientUserId });
        db.AccountLifecycleRecords.Add(new AccountLifecycleRecord { ProfileId = profile.Id, UserId = profile.ClientUserId, ParticipantType = MessagingParticipantTypes.Client, State = AccountLifecycleStates.Closed });
        var meeting = new LeadAppointment { Id = Guid.NewGuid(), OwnerAgentUserId = "agent-a", ClientProfileId = profile.Id.ToString(), ScheduledStartUtc = DateTime.UtcNow.AddHours(-2), Status = Domain.Enums.LeadAppointmentStatus.Booked };
        db.LeadAppointments.Add(meeting); await db.SaveChangesAsync();
        var service = new MobileAgentCrmService(db);
        var actor = new MobileResolvedActor(new MessagingActor("agent-a", MessagingParticipantTypes.Agent), Guid.NewGuid(), "Agent");
        Assert.True((await service.RecordAsync(actor, "leads", profile.ClientUserId, default))!.Archived);
        Assert.Equal("https://zoom.us/j/456", Assert.Single(await service.ScheduleAsync(actor, default)).MeetingUrl);
        meeting.MeetingUrl = "https://zoom.us/j/789"; await db.SaveChangesAsync();
        Assert.Equal("https://zoom.us/j/789", Assert.Single(await service.ScheduleAsync(actor, default)).MeetingUrl);
    }
}
