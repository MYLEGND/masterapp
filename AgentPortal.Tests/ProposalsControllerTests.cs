using System;
using System.Linq;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Services;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public class ProposalsControllerTests
{
    [Fact]
    public async Task CaptureDecision_NullDto_ReturnsBadRequest()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var decisions = Mock.Of<IDecisionService>();
        var playbook = Mock.Of<IPlaybookEngine>();
        var controller = ControllerTestHelpers.BuildProposalsController(db, decisions, playbook, ControllerTestHelpers.BuildUser());

        var result = await controller.CaptureDecision(Guid.NewGuid(), null!);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Decision payload required", bad.Value?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task CaptureDecision_Saves_WhenProposalOwned()
    {
        using var db = ControllerTestHelpers.BuildDb();
        db.Proposals.Add(new Proposal { Id = Guid.NewGuid(), AgentUserId = "agent-1", Name = "P1" });
        await db.SaveChangesAsync();

        var decisions = new Mock<IDecisionService>();
        decisions.Setup(d => d.CreateDecisionAsync(It.IsAny<DecisionRecord>(), default)).ReturnsAsync(new DecisionRecord());
        var playbook = new Mock<IPlaybookEngine>();
        playbook.Setup(p => p.HandleAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), default)).Returns(Task.CompletedTask);

        var controller = ControllerTestHelpers.BuildProposalsController(db, decisions.Object, playbook.Object, ControllerTestHelpers.BuildUser("agent-1"));
        var proposalId = await db.Proposals.AsNoTracking().Select(p => p.Id).FirstAsync();

        var result = await controller.CaptureDecision(proposalId, new ProposalsController.DecisionDto
        {
            Title = "Approve",
            Rationale = "Fits budget"
        });

        Assert.IsType<OkObjectResult>(result);
        decisions.Verify(d => d.CreateDecisionAsync(It.Is<DecisionRecord>(r => r.Title == "Approve" && r.RelatedEntityId == proposalId.ToString()), default), Times.Once);
    }
}
