using System;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public class CommitmentControllerTests
{
    [Fact]
    public async Task CreateCommitment_Lead_Succeeds()
    {
        using var db = ControllerTestHelpers.BuildDb();
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = "L-1",
            AgentUserId = "agent-1",
            Bucket = "LifeInsurance",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var exec = Mock.Of<IExecutionEngine>();
        var commitmentSvc = new Mock<ICommitmentService>();
        commitmentSvc.Setup(c => c.CreateCommitmentAsync(It.IsAny<CommitmentCreateRequest>(), default))
            .ReturnsAsync(new Commitment { Id = Guid.NewGuid(), RelatedEntityId = "L-1" });
        var controller = ControllerTestHelpers.BuildLeadsController(db, exec, commitmentSvc.Object, ControllerTestHelpers.BuildUser());

        var result = await controller.CreateCommitment(new LeadsController.CreateCommitmentRequest
        {
            LeadId = "L-1",
            PromiseText = "Send docs",
            DueDateUtc = DateTimeOffset.UtcNow.AddDays(1)
        });

        Assert.IsType<RedirectToActionResult>(result);
        commitmentSvc.Verify(c => c.CreateCommitmentAsync(It.IsAny<CommitmentCreateRequest>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateCommitment_Client_Succeeds()
    {
        using var db = ControllerTestHelpers.BuildDb();
        db.ClientProfiles.Add(new ClientProfile
        {
            ClientUserId = "C-1",
            FirstName = "Client",
            LastName = "One",
            Email = "client@example.com",
            NormalizedEmail = "client@example.com",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-1",
            ClientUserId = "C-1",
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var exec = Mock.Of<IExecutionEngine>();
        var commitmentSvc = new Mock<ICommitmentService>();
        commitmentSvc.Setup(c => c.CreateCommitmentAsync(It.IsAny<CommitmentCreateRequest>(), default))
            .ReturnsAsync(new Commitment { Id = Guid.NewGuid(), RelatedEntityId = "C-1" });
        var controller = ControllerTestHelpers.BuildClientsController(db, exec, commitmentSvc.Object, ControllerTestHelpers.BuildUser());

        var result = await controller.CreateCommitment(new ClientsController.CreateClientCommitmentRequest
        {
            ClientId = "C-1",
            PromiseText = "Renew policy",
            DueDateUtc = DateTimeOffset.UtcNow.AddDays(2)
        });

        Assert.IsType<RedirectToActionResult>(result);
        commitmentSvc.Verify(c => c.CreateCommitmentAsync(It.IsAny<CommitmentCreateRequest>(), default), Times.Once);
    }
}
