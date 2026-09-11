using System;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Mobile;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class MessagingServiceTests
{
    [Theory]
    [InlineData("Clean", true)]
    [InlineData("Pending", false)]
    [InlineData("Rejected", false)]
    public async Task MobileAttachmentDownload_UsesExistingCleanScanAuthority(string scanStatus, bool allowed)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, true, false);
        var service = CreateService(db);
        var sender = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var recipient = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var conversation = await service.StartConversationAsync(new StartMessagingConversationCommand(sender,
            recipient.UserId, recipient.ParticipantType, InitialMessageBody: "Document"));
        var message = Assert.Single(await db.InternalMessages.ToListAsync());
        var attachment = new MessageAttachment
        {
            Id = Guid.NewGuid(), InternalMessageId = message.Id, OriginalFileName = "document.pdf",
            StoredFileName = "internal.pdf", StoragePath = "private-document", ContentType = "application/pdf",
            SizeBytes = 4, ScanStatus = scanStatus, CreatedUtc = DateTime.UtcNow
        };
        db.MessageAttachments.Add(attachment);
        await db.SaveChangesAsync();
        var client = await db.ClientProfiles.SingleAsync(profile => profile.ClientUserId == recipient.UserId);
        var resolved = new MobileResolvedActor(recipient, client.Id, "Client");
        var actors = new Mock<IMobileActorResolver>();
        actors.Setup(actor => actor.ResolveAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MobileActorResolution(true, null, null, [resolved], resolved, false));
        var storage = new Mock<IMessageAttachmentStorage>();
        storage.Setup(value => value.OpenReadAsync(attachment.StoragePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream([1, 2, 3, 4]));
        var controller = new MobileMessagingController(actors.Object, service, storage.Object,
            Mock.Of<IMessagingRealtimePublisher>(), Mock.Of<IMessagingProfileImageResolver>(),
            Mock.Of<IControlledResourceAccessService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var result = await controller.DownloadAttachment(attachment.Id, CancellationToken.None);
        if (allowed)
        {
            var file = Assert.IsType<FileStreamResult>(result);
            Assert.Equal("application/pdf", file.ContentType);
            Assert.Equal("document.pdf", file.FileDownloadName);
            Assert.True(file.EnableRangeProcessing);
            Assert.Equal(4, file.FileStream.Length);
        }
        else Assert.IsNotType<FileStreamResult>(result);
        storage.Verify(value => value.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            allowed ? Times.Once() : Times.Never());
    }
}
