using System;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using Domain.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Shared.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ApplicationLocalizationWebContractTests
{
    [Fact]
    public async Task WebCatalog_UsesResolvedActorAndTheSameApplicationService()
    {
        var actors = new Mock<IMessagingActorContextResolver>();
        actors.Setup(x => x.ResolveAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("canonical-user", "Client"));
        var expected = new ApplicationLocalizationCatalog("catalog", "en", "ht", "ht", DateTime.UtcNow, true, []);
        var localization = new Mock<IApplicationLocalizationService>(MockBehavior.Strict);
        localization.Setup(x => x.GetCatalogAsync(new MessagingActor("canonical-user", "Client"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var controller = new ApplicationLocalizationController(actors.Object, localization.Object)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.Request.QueryString = new QueryString("?language=fr&userId=other-user");
        Assert.Same(expected, Assert.IsType<OkObjectResult>(await controller.Catalog(default)).Value);
        localization.VerifyAll();
    }

    [Fact]
    public async Task WebCatalog_RequiresAnAuthorizedActorBeforeTranslation()
    {
        var actors = new Mock<IMessagingActorContextResolver>();
        var localization = new Mock<IApplicationLocalizationService>(MockBehavior.Strict);
        var controller = new ApplicationLocalizationController(actors.Object, localization.Object)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<ForbidResult>(await controller.Catalog(default));
        localization.VerifyNoOtherCalls();
    }
}
