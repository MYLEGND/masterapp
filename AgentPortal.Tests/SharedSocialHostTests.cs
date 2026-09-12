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
        Assert.IsType<NotFoundResult>(await controller.AuthorImage(postId, CancellationToken.None));
        identities.Verify(value => value.ResolveAsync(It.IsAny<MessagingParticipantIdentity>(),
            It.IsAny<CancellationToken>()), Times.Never);
        social.Verify(value => value.GetPostAsync(It.Is<SocialFeedActor>(actor => actor.ProfileId == profileId),
            postId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedSourceActionsUseExistingAuthorityAndExcludeInternalContactData(bool clientHost)
    {
        var postId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var actor = new SocialFeedActor(new MessagingActor("member", MessagingParticipantTypes.Client), profileId, "Member");
        var author = new SocialAuthor("author", MessagingParticipantTypes.Agent, Guid.NewGuid(), "Author", Phone: "internal-only");
        var post = new SocialPostView(postId, author, SocialPostContentTypes.Post, "Caption", SocialPostAudiences.AuthorizedNetwork,
            null, true, DateTime.UtcNow, null, 1, 1, true, false, false, true, true,
            new SocialPostMetrics(0, 0, 1, 1, 0, 0, 1, 1, 0, 0, null, null, 0, 0, 0), null,
            Array.Empty<SocialMediaAssetView>(), new[] { new SocialCommentView(Guid.NewGuid(), author, null, "Comment", DateTime.UtcNow) });
        var social = new Mock<ISocialFeedService>(MockBehavior.Strict);
        social.Setup(x => x.GetPostAsync(actor, postId, It.IsAny<CancellationToken>())).ReturnsAsync(SocialOperationResult<SocialPostView>.Success(post));
        var command = new SocialPostMutationCommand(actor, postId);
        social.Setup(x => x.ToggleReactionAsync(command, It.IsAny<CancellationToken>())).ReturnsAsync(SocialOperationResult<SocialPostView>.Success(post));
        social.Setup(x => x.ToggleSaveAsync(command, It.IsAny<CancellationToken>())).ReturnsAsync(SocialOperationResult<bool>.Success(true));
        social.Setup(x => x.ToggleRepostAsync(command, It.IsAny<CancellationToken>())).ReturnsAsync(SocialOperationResult<bool>.Success(true));
        social.Setup(x => x.RecordShareAsync(command, It.IsAny<CancellationToken>())).ReturnsAsync(SocialOperationResult<bool>.Success(true));
        social.Setup(x => x.AddCommentAsync(new CreateSocialCommentCommand(actor, postId, "Comment", null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SocialOperationResult<SocialCommentView>.Success(post.Comments[0]));
        var controller = ForActor(clientHost, actor, social.Object);
        foreach (var result in new[] {
            await controller.Reaction(postId, CancellationToken.None),
            await controller.Save(postId, CancellationToken.None),
            await controller.Repost(postId, CancellationToken.None),
            await controller.Share(postId, CancellationToken.None),
            await controller.Comment(postId, "Comment", null, CancellationToken.None) })
        {
            var value = Assert.IsType<SocialPostView>(Assert.IsType<OkObjectResult>(result).Value);
            Assert.Equal(postId, value.Id);
            Assert.Null(value.Author.Phone);
            Assert.Null(Assert.Single(value.Comments).Author.Phone);
        }
        Assert.IsType<ViewResult>(await controller.Open(postId, CancellationToken.None));
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
        social.VerifyAll();
    }

    [Fact]
    public async Task SharedSourceCommentRetainsModerationFailureAndAllMutationsRequireAntiforgery()
    {
        var actor = new SocialFeedActor(new MessagingActor("member", MessagingParticipantTypes.Client), Guid.NewGuid(), "Member");
        var postId = Guid.NewGuid();
        var social = new Mock<ISocialFeedService>(MockBehavior.Strict);
        social.Setup(x => x.AddCommentAsync(new CreateSocialCommentCommand(actor, postId, "rejected", null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SocialOperationResult<SocialCommentView>.Failure("social_comment_blocked", "Not permitted"));
        var controller = ForActor(false, actor, social.Object);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<ObjectResult>(await controller.Comment(postId, "rejected", null, CancellationToken.None)).StatusCode);
        foreach (var name in new[] { "Reaction", "Comment", "Save", "Repost", "Share" })
            Assert.NotEmpty(typeof(SocialSharedContentControllerBase).GetMethod(name)!.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        social.VerifyAll();
    }

    [Theory]
    [InlineData("social_actor_invalid", 403)]
    [InlineData("social_post_unavailable", 404)]
    public async Task SharedSourceDenialHasAuthoritativeStatusForContentEviction(string code, int status)
    {
        var actor = new SocialFeedActor(new MessagingActor("member", MessagingParticipantTypes.Client), Guid.NewGuid(), "Member");
        var postId = Guid.NewGuid();
        var social = new Mock<ISocialFeedService>(MockBehavior.Strict);
        social.Setup(x => x.ToggleReactionAsync(new SocialPostMutationCommand(actor, postId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SocialOperationResult<SocialPostView>.Failure(code, "Unavailable"));
        var result = await ForActor(false, actor, social.Object).Reaction(postId, CancellationToken.None);
        Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }

    private static SocialSharedContentControllerBase ForActor(bool clientHost, SocialFeedActor actor, ISocialFeedService social)
    {
        var actors = new Mock<IMessagingActorContextResolver>();
        actors.Setup(x => x.ResolveAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((actor.Identity.UserId, actor.Identity.ParticipantType));
        var identities = new Mock<IMessagingProfileImageResolver>();
        identities.Setup(x => x.ResolveIdentitiesAsync(It.IsAny<IEnumerable<MessagingParticipantReference>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<(string, string), MessagingParticipantIdentity>
            {
                [(actor.Identity.UserId, actor.Identity.ParticipantType)] = new(actor.Identity.UserId, actor.Identity.ParticipantType, actor.ProfileId, actor.DisplayName, null, "M")
            });
        SocialSharedContentControllerBase controller = clientHost
            ? new ClientApp.Controllers.SocialSharedContentController(actors.Object, identities.Object, social)
            : new AgentPortal.Controllers.SocialSharedContentController(actors.Object, identities.Object, social);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

}
