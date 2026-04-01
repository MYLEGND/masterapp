using System;
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

public class DashboardControllerTests
{
    [Fact]
    public async Task CompleteAction_EmptyId_ReturnsBadRequest()
    {
        var exec = Mock.Of<IExecutionEngine>();
        var controller = ControllerTestHelpers.BuildDashboardController(exec, ControllerTestHelpers.BuildUser());

        var result = await controller.CompleteAction(new DashboardController.CompleteActionRequest(Guid.Empty));

        Assert.IsType<BadRequestResult>(result);
    }

    [Fact]
    public async Task CompleteAction_HappyPath_ReturnsOk()
    {
        var db = ControllerTestHelpers.BuildDb();
        var engine = new ExecutionEngine(db);
        var controller = ControllerTestHelpers.BuildDashboardController(engine, ControllerTestHelpers.BuildUser("agent-1"));

        var action = new ActionItem
        {
            RelatedEntityType = RelatedEntityType.Lead,
            RelatedEntityId = "L1",
            Title = "Task",
            OwnerId = "agent-1",
            CreatedBy = "agent-1"
        };
        db.ActionItems.Add(action);
        await db.SaveChangesAsync();

        var result = await controller.CompleteAction(new DashboardController.CompleteActionRequest(action.Id));

        Assert.IsType<OkObjectResult>(result);
    }
}
