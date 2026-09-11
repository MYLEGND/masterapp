using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Domain.Messaging;
using Domain.Social;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Infrastructure.Social;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shared.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class SharedSocialHostTests
{
    [Fact]
    public void ClientReadRegistration_ResolvesBothAuthoritiesWithoutAnotherMediaWorker()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(value => value.ContentRootPath).Returns(AppContext.BaseDirectory);
        services.AddSingleton(environment.Object);
        services.AddDbContext<MasterAppDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        var configuration = new ConfigurationBuilder().Build();
        services.AddMasterAppMessaging(configuration);
        services.AddMasterAppSocial(configuration, enableMediaProcessing: false);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsAssignableFrom<IMessagingService>(scope.ServiceProvider.GetRequiredService<IMessagingService>());
        Assert.IsType<SocialFeedService>(scope.ServiceProvider.GetRequiredService<ISocialFeedService>());
        Assert.Null(scope.ServiceProvider.GetService<ISocialMediaProcessingQueue>());
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(SocialMediaProcessingWorker));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothWebHosts_NormalizeIdentityBeforeResolvingSharedSource(bool clientHost)
    {
        var postId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var actors = new Mock<IMessagingActorContextResolver>();
        actors.Setup(value => value.ResolveAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((" MIXED-IDENTITY ", MessagingParticipantTypes.Agent));
        var identities = new Mock<IMessagingProfileImageResolver>();
        identities.Setup(value => value.ResolveIdentitiesAsync(It.IsAny<IEnumerable<MessagingParticipantReference>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<(string, string), MessagingParticipantIdentity>
            {
                [("mixed-identity", MessagingParticipantTypes.Agent)] = new("mixed-identity",
                    MessagingParticipantTypes.Agent, profileId, "Member", null, "M")
            });
        var social = new Mock<ISocialFeedService>();
        social.Setup(value => value.GetPostAsync(It.Is<SocialFeedActor>(actor => actor.ProfileId == profileId),
                postId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SocialOperationResult<SocialPostView>.Failure("social_post_unavailable", "Unavailable"));
        SocialSharedContentControllerBase controller = clientHost
            ? new ClientApp.Controllers.SocialSharedContentController(actors.Object, identities.Object, social.Object)
            : new AgentPortal.Controllers.SocialSharedContentController(actors.Object, identities.Object, social.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        Assert.IsType<NotFoundResult>(await controller.Open(postId, CancellationToken.None));
        social.Verify(value => value.GetPostAsync(It.Is<SocialFeedActor>(actor => actor.ProfileId == profileId),
            postId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
