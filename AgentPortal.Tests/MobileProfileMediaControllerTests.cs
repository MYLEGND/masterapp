using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Mobile;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileProfileMediaControllerTests
{
    [Fact]
    public async Task Get_ReturnsCanonicalProfileImage_ForAgent()
    {
        var profileId = Guid.NewGuid();
        var content = new byte[] { 1, 2, 3, 4 };
        const string contentType = "image/jpeg";

        var profiles = new Mock<IMessagingProfileImageResolver>(
            MockBehavior.Strict);

        profiles
            .Setup(x => x.ResolveAsync(
                It.Is<MessagingParticipantIdentity>(identity =>
                    identity.ParticipantType == MessagingParticipantTypes.Agent &&
                    identity.ProfileId == profileId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MessagingProfileImage(
                content,
                contentType));

        var controller = new MobileProfileMediaController(
            profiles.Object);

        var result = await controller.Get(
            MessagingParticipantTypes.Agent,
            profileId,
            CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);

        Assert.Equal(contentType, file.ContentType);
        Assert.Equal(content, file.FileContents);

        profiles.VerifyAll();
    }

    [Fact]
    public async Task Get_ReturnsCanonicalProfileImage_ForClient()
    {
        var profileId = Guid.NewGuid();
        var content = new byte[] { 5, 6, 7, 8 };
        const string contentType = "image/png";

        var profiles = new Mock<IMessagingProfileImageResolver>(
            MockBehavior.Strict);

        profiles
            .Setup(x => x.ResolveAsync(
                It.Is<MessagingParticipantIdentity>(identity =>
                    identity.ParticipantType == MessagingParticipantTypes.Client &&
                    identity.ProfileId == profileId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MessagingProfileImage(
                content,
                contentType));

        var controller = new MobileProfileMediaController(
            profiles.Object);

        var result = await controller.Get(
            MessagingParticipantTypes.Client,
            profileId,
            CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);

        Assert.Equal(contentType, file.ContentType);
        Assert.Equal(content, file.FileContents);

        profiles.VerifyAll();
    }

    [Fact]
    public async Task Get_ReturnsNotFound_WhenCanonicalImageDoesNotExist()
    {
        var profileId = Guid.NewGuid();

        var profiles = new Mock<IMessagingProfileImageResolver>(
            MockBehavior.Strict);

        profiles
            .Setup(x => x.ResolveAsync(
                It.Is<MessagingParticipantIdentity>(identity =>
                    identity.ParticipantType == MessagingParticipantTypes.Agent &&
                    identity.ProfileId == profileId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((MessagingProfileImage?)null);

        var controller = new MobileProfileMediaController(
            profiles.Object);

        var result = await controller.Get(
            MessagingParticipantTypes.Agent,
            profileId,
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);

        profiles.VerifyAll();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("agent")]
    [InlineData("client")]
    public async Task Get_RejectsNonCanonicalParticipantTypes(
        string participantType)
    {
        var profiles = new Mock<IMessagingProfileImageResolver>(
            MockBehavior.Strict);

        var controller = new MobileProfileMediaController(
            profiles.Object);

        var result = await controller.Get(
            participantType,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);

        profiles.Verify(
            x => x.ResolveAsync(
                It.IsAny<MessagingParticipantIdentity>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void RouteAndAuthorization_MatchMobileResourceContract()
    {
        var controllerType = typeof(MobileProfileMediaController);

        var route = Assert.Single(
            controllerType
                .GetCustomAttributes(
                    typeof(RouteAttribute),
                    inherit: true)
                .Cast<RouteAttribute>());

        Assert.Equal(
            "api/v1/mobile/profile-images",
            route.Template);

        var authorize = Assert.Single(
            controllerType
                .GetCustomAttributes(
                    typeof(AuthorizeAttribute),
                    inherit: true)
                .Cast<AuthorizeAttribute>());

        Assert.Equal(
            MobileApiAuthorization.PolicyName,
            authorize.Policy);

        var method = controllerType.GetMethod(
            nameof(MobileProfileMediaController.Get));

        Assert.NotNull(method);

        var get = Assert.Single(
            method!
                .GetCustomAttributes(
                    typeof(HttpGetAttribute),
                    inherit: true)
                .Cast<HttpGetAttribute>());

        Assert.Equal(
            "{participantType}/{profileId:guid}",
            get.Template);
    }
}
