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

public class ClientsControllerTests
{
    [Fact]
    public async Task CreateAction_Redirects_ForClient()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var execMock = new Mock<IExecutionEngine>();
        ActionItem? captured = null;
        execMock.Setup(x => x.CreateActionAsync(It.IsAny<ActionItem>(), default))
            .Callback<ActionItem, System.Threading.CancellationToken>((a, _) => captured = a)
            .ReturnsAsync(new ActionItem());
        var commitments = Mock.Of<ICommitmentService>();
        var controller = ControllerTestHelpers.BuildClientsController(db, execMock.Object, commitments, ControllerTestHelpers.BuildUser());

        var result = await controller.CreateAction(new ClientsController.CreateClientActionRequest
        {
            ClientId = "C-1",
            Title = "Prep review",
            ShowInCommandCenter = true
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ClientsController.Actions), redirect.ActionName);
        Assert.NotNull(captured);
        Assert.Equal(ActionSurface.CommandCenter, captured!.ActionSurface);
    }
}
