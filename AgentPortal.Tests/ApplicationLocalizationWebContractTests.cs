using System;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Messaging;
using Domain.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Shared.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ApplicationLocalizationWebContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebCatalog_UsesResolvedActorAndTheSameApplicationService(bool clientHost)
    {
        var actors = new Mock<IMessagingActorContextResolver>();
        actors.Setup(x => x.ResolveAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("canonical-user", "Client"));
        var expected = new ApplicationLocalizationCatalog("catalog", "en", "ht", "ht", DateTime.UtcNow, true, []);
        var localization = new Mock<IApplicationLocalizationService>(MockBehavior.Strict);
        localization.Setup(x => x.GetCatalogAsync(new MessagingActor("canonical-user", "Client"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        ApplicationLocalizationControllerBase controller = clientHost
            ? new ClientApp.Controllers.ApplicationLocalizationController(actors.Object, localization.Object)
            : new AgentPortal.Controllers.ApplicationLocalizationController(actors.Object, localization.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Request.QueryString = new QueryString("?language=fr&userId=other-user");
        Assert.Same(expected, Assert.IsType<OkObjectResult>(await controller.Catalog(default)).Value);
        localization.VerifyAll();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebCatalog_RequiresAnAuthorizedActorBeforeTranslation(bool clientHost)
    {
        var actors = new Mock<IMessagingActorContextResolver>();
        var localization = new Mock<IApplicationLocalizationService>(MockBehavior.Strict);
        ApplicationLocalizationControllerBase controller = clientHost
            ? new ClientApp.Controllers.ApplicationLocalizationController(actors.Object, localization.Object)
            : new AgentPortal.Controllers.ApplicationLocalizationController(actors.Object, localization.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        Assert.IsType<ForbidResult>(await controller.Catalog(default));
        localization.VerifyNoOtherCalls();
    }
}
