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

public class ClientsQuickViewSecurityTests
{
    private static MasterAppDbContext BuildDb() => ControllerTestHelpers.BuildDb();

    private static ClaimsPrincipal User(string oid) => new(
        new ClaimsIdentity(new[] { new Claim("oid", oid) }, "TestAuth"));

    [Fact]
    public async Task Actions_UnownedClient_ReturnsForbid()
    {
        using var db = BuildDb();
        db.ClientProfiles.Add(new ClientProfile { ClientUserId = "client-1", FirstName = "C", LastName = "One", CreatedUtc = DateTime.UtcNow });
        db.AgentClients.Add(new AgentClient { AgentUserId = "agent-a", ClientUserId = "client-1", CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var execution = new Mock<IExecutionEngine>(MockBehavior.Strict);
        var commitments = Mock.Of<ICommitmentService>();
        var controller = ControllerTestHelpers.BuildClientsController(db, execution.Object, commitments, User("agent-b"));

        var result = await controller.Actions("client-1");

        Assert.IsType<ForbidResult>(result);
        execution.Verify(
            x => x.GetByRelatedAsync(It.IsAny<RelatedEntityType>(), It.IsAny<string>(), It.IsAny<string>(), default),
            Times.Never);
    }

    [Fact]
    public async Task CreateAction_UnownedClient_ReturnsForbid_AndDoesNotCreate()
    {
        using var db = BuildDb();
        db.ClientProfiles.Add(new ClientProfile { ClientUserId = "client-1", FirstName = "C", LastName = "One", CreatedUtc = DateTime.UtcNow });
        db.AgentClients.Add(new AgentClient { AgentUserId = "agent-a", ClientUserId = "client-1", CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var execution = new ExecutionEngine(db);
        var commitments = Mock.Of<ICommitmentService>();
        var controller = ControllerTestHelpers.BuildClientsController(db, execution, commitments, User("agent-b"));

        var result = await controller.CreateAction(new ClientsController.CreateClientActionRequest
        {
            ClientId = "client-1",
            Title = "Should not create"
        });

        Assert.IsType<ForbidResult>(result);
        Assert.False(await db.ActionItems.AnyAsync());
    }

    [Fact]
    public async Task Commitments_UnownedClient_ReturnsForbid()
    {
        using var db = BuildDb();
        db.ClientProfiles.Add(new ClientProfile { ClientUserId = "client-1", FirstName = "C", LastName = "One", CreatedUtc = DateTime.UtcNow });
        db.AgentClients.Add(new AgentClient { AgentUserId = "agent-a", ClientUserId = "client-1", CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var commitments = new Mock<ICommitmentService>(MockBehavior.Strict);
        var controller = ControllerTestHelpers.BuildClientsController(db, Mock.Of<IExecutionEngine>(), commitments.Object, User("agent-b"));

        var result = await controller.Commitments("client-1");

        Assert.IsType<ForbidResult>(result);
        commitments.Verify(
            x => x.GetByEntityForActorAsync(It.IsAny<RelatedEntityType>(), It.IsAny<string>(), It.IsAny<string>(), default),
            Times.Never);
    }

    [Fact]
    public async Task FulfillCommitment_UnownedCommitment_ReturnsNotFound()
    {
        using var db = BuildDb();
        db.ClientProfiles.Add(new ClientProfile { ClientUserId = "client-1", FirstName = "C", LastName = "One", CreatedUtc = DateTime.UtcNow });
        db.AgentClients.Add(new AgentClient { AgentUserId = "agent-a", ClientUserId = "client-1", CreatedUtc = DateTime.UtcNow });

        var commitmentId = Guid.NewGuid();
        db.Commitments.Add(new Commitment
        {
            Id = commitmentId,
            RelatedEntityType = RelatedEntityType.Client,
            RelatedEntityId = "client-1",
            PromisedByType = ActionOwnerType.Agent,
            PromisedById = "agent-a",
            PromisedToType = ActionOwnerType.Client,
            PromisedToId = "client-1",
            PromiseText = "Send docs",
            DueDateUtc = DateTimeOffset.UtcNow.AddDays(1),
            CreatedBy = "agent-a",
            CreatedUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var commitmentService = new CommitmentService(db, new ExecutionEngine(db));
        var controller = ControllerTestHelpers.BuildClientsController(db, new ExecutionEngine(db), commitmentService, User("agent-b"));

        var result = await controller.FulfillCommitment(commitmentId);

        Assert.IsType<NotFoundResult>(result);
        var persisted = await db.Commitments.SingleAsync(x => x.Id == commitmentId);
        Assert.Equal(CommitmentStatus.Open, persisted.Status);
    }
}
