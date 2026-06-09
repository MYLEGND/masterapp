using System;
using System.Security.Claims;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public class LeadsQuickViewSecurityTests
{
    private static MasterAppDbContext BuildDb() => ControllerTestHelpers.BuildDb();

    private static ClaimsPrincipal User(string oid) => new(
        new ClaimsIdentity(new[] { new Claim("oid", oid) }, "TestAuth"));

    [Fact]
    public async Task Actions_UnownedLead_ReturnsForbid()
    {
        using var db = BuildDb();
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = "lead-1",
            AgentUserId = "agent-owner",
            Bucket = "New",
            CrmStage = "New",
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var execution = new Mock<IExecutionEngine>(MockBehavior.Strict);
        var commitments = Mock.Of<ICommitmentService>();
        var controller = ControllerTestHelpers.BuildLeadsController(db, execution.Object, commitments, User("agent-other"));

        var result = await controller.Actions("lead-1");

        Assert.IsType<ForbidResult>(result);
        execution.Verify(x => x.GetByRelatedAsync(It.IsAny<RelatedEntityType>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task CreateCommitment_UnownedLead_ReturnsForbid()
    {
        using var db = BuildDb();
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = "lead-2",
            AgentUserId = "agent-owner",
            Bucket = "New",
            CrmStage = "New",
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var execution = Mock.Of<IExecutionEngine>();
        var commitments = new Mock<ICommitmentService>(MockBehavior.Strict);
        var controller = ControllerTestHelpers.BuildLeadsController(db, execution, commitments.Object, User("agent-other"));

        var result = await controller.CreateCommitment(new LeadsController.CreateCommitmentRequest
        {
            LeadId = "lead-2",
            PromiseText = "call",
            DueDateUtc = DateTimeOffset.UtcNow.AddDays(1)
        });

        Assert.IsType<ForbidResult>(result);
        commitments.Verify(x => x.CreateCommitmentAsync(It.IsAny<CommitmentCreateRequest>(), default), Times.Never);
    }

    [Fact]
    public async Task FulfillCommitment_Unowned_ReturnsNotFound()
    {
        using var db = BuildDb();
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = "lead-3",
            AgentUserId = "agent-owner",
            Bucket = "New",
            CrmStage = "New",
            CreatedUtc = DateTime.UtcNow
        });
        var commitment = new Commitment
        {
            Id = Guid.NewGuid(),
            RelatedEntityType = RelatedEntityType.Lead,
            RelatedEntityId = "lead-3",
            PromisedByType = ActionOwnerType.Agent,
            PromisedById = "agent-owner",
            PromisedToType = ActionOwnerType.Client,
            PromisedToId = "lead-3",
            PromiseText = "Follow up",
            DueDateUtc = DateTimeOffset.UtcNow.AddDays(1),
            Status = CommitmentStatus.Open,
            CreatedBy = "agent-owner",
            CreatedUtc = DateTimeOffset.UtcNow
        };
        db.Commitments.Add(commitment);
        await db.SaveChangesAsync();

        var execution = new ExecutionEngine(db);
        var commitmentSvc = new CommitmentService(db, execution);
        var controller = ControllerTestHelpers.BuildLeadsController(db, execution, commitmentSvc, User("agent-other"));

        var result = await controller.FulfillCommitment(commitment.Id);

        Assert.IsType<NotFoundResult>(result);
        var persisted = await db.Commitments.SingleAsync(x => x.Id == commitment.Id);
        Assert.Equal(CommitmentStatus.Open, persisted.Status);
    }

    [Fact]
    public async Task BreakCommitment_Unowned_ReturnsNotFound()
    {
        using var db = BuildDb();
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = "lead-5",
            AgentUserId = "agent-owner",
            Bucket = "New",
            CrmStage = "New",
            CreatedUtc = DateTime.UtcNow
        });
        var commitment = new Commitment
        {
            Id = Guid.NewGuid(),
            RelatedEntityType = RelatedEntityType.Lead,
            RelatedEntityId = "lead-5",
            PromisedByType = ActionOwnerType.Agent,
            PromisedById = "agent-owner",
            PromisedToType = ActionOwnerType.Client,
            PromisedToId = "lead-5",
            PromiseText = "Follow up",
            DueDateUtc = DateTimeOffset.UtcNow.AddDays(1),
            Status = CommitmentStatus.Open,
            CreatedBy = "agent-owner",
            CreatedUtc = DateTimeOffset.UtcNow
        };
        db.Commitments.Add(commitment);
        await db.SaveChangesAsync();

        var execution = new ExecutionEngine(db);
        var commitmentSvc = new CommitmentService(db, execution);
        var controller = ControllerTestHelpers.BuildLeadsController(db, execution, commitmentSvc, User("agent-other"));

        var result = await controller.BreakCommitment(commitment.Id);

        Assert.IsType<NotFoundResult>(result);
        var persisted = await db.Commitments.SingleAsync(x => x.Id == commitment.Id);
        Assert.Equal(CommitmentStatus.Open, persisted.Status);
    }

    [Fact]
    public async Task Commitments_UnownedLead_ReturnsForbid()
    {
        using var db = BuildDb();
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = "lead-4",
            AgentUserId = "agent-owner",
            Bucket = "New",
            CrmStage = "New",
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var commitments = new Mock<ICommitmentService>(MockBehavior.Strict);
        var controller = ControllerTestHelpers.BuildLeadsController(db, Mock.Of<IExecutionEngine>(), commitments.Object, User("agent-other"));

        var result = await controller.Commitments("lead-4");

        Assert.IsType<ForbidResult>(result);
        commitments.Verify(x => x.GetByEntityForActorAsync(It.IsAny<RelatedEntityType>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }
}
