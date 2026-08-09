using System;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileClientCreationPortalTests
{
    [Fact]
    public async Task LaunchTicket_RendersTheAuthoritativeCreateViewInsideTheMobilePortal()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.AgentProfiles.Add(new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "agent-1",
            AgentUpn = "agent@example.test",
            FullName = "Agent One",
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            ControllerTestHelpers.BuildUser("agent-1", "agent@example.test"),
            mobileActorResolver: new MobileActorResolver(
                db,
                NullLogger<MobileActorResolver>.Instance));

        var routes = new Mock<IUrlHelper>();
        routes
            .Setup(helper => helper.RouteUrl(It.IsAny<UrlRouteContext>()))
            .Returns((UrlRouteContext route) =>
            {
                Assert.Equal("MobileClientCreationPortal", route.RouteName);
                var values = new RouteValueDictionary(route.Values);
                var ticket = Assert.IsType<string>(values["ticket"]);
                return $"/mobile/agent/clients/create?ticket={Uri.EscapeDataString(ticket)}";
            });
        controller.Url = routes.Object;

        var launchResult = await controller.MobileClientCreationPortalLaunchRequest(CancellationToken.None);

        var launch = Assert.IsType<ClientsController.MobileClientCreationPortalLaunch>(
            Assert.IsType<OkObjectResult>(launchResult).Value);
        var ticket = Uri.UnescapeDataString(launch.LaunchPath.Split("?ticket=", StringSplitOptions.None)[1]);

        var portalResult = await controller.MobileClientCreationPortal(ticket, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(portalResult);
        Assert.Equal(nameof(ClientsController.Create), view.ViewName);
        Assert.Equal(ticket, view.ViewData["MobileClientCreationPortalTicket"]);

        var validationResult = await controller.MobileClientCreationPortalSubmit(
            ticket,
            new CreateClientViewModel { RecordType = "Client" },
            "/mobile/agent/clients/create-complete",
            CancellationToken.None);

        Assert.Equal(
            nameof(ClientsController.Create),
            Assert.IsType<ViewResult>(validationResult).ViewName);
    }
}
