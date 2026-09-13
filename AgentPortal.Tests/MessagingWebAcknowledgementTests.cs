using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shared.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MessagingWebAcknowledgementTests
{
    [Theory]
    [InlineData(MessagingParticipantTypes.Agent)]
    [InlineData(MessagingParticipantTypes.Client)]
    public async Task SendForwardsResolvedActorStableRetryIdentityAndReplyWithoutHydratingConversation(string participantType)
    {
        using var fixture = new Fixture(participantType);
        var replyId = Guid.NewGuid();
        var request = new SendMessagingMessageRequest("Independent acknowledgement control", "stable-retry-id", replyId);
        var committed = fixture.Success(request.Body, replyId);
        fixture.Service.Setup(service => service.SendMessageAsync(
                It.Is<SendMessagingMessageCommand>(command =>
                    command.Actor == fixture.Actor && command.ConversationId == fixture.ConversationId &&
                    command.Body == request.Body && command.ClientMessageId == request.ClientMessageId &&
                    command.ReplyToMessageId == replyId), fixture.Token))
            .ReturnsAsync(committed);
        var recipients = new List<MessagingRealtimeRecipient>
        {
            new(fixture.Actor.UserId, participantType), new("permitted-recipient", MessagingParticipantTypes.Client)
        };
        fixture.Service.Setup(service => service.GetConversationRealtimeRecipientsAsync(
            fixture.Actor, fixture.ConversationId, fixture.Token)).ReturnsAsync(recipients);
        var events = new List<MessagingRealtimeEvent>();
        fixture.Publisher.Setup(publisher => publisher.PublishAsync(It.IsAny<MessagingRealtimeEvent>(), fixture.Token))
            .Callback<MessagingRealtimeEvent, CancellationToken>((notification, _) => events.Add(notification))
            .Returns(Task.CompletedTask);

        // A retry carries exactly the same server idempotency key and reply;
        // persistence/deduplication remain the IMessagingService authority.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = Assert.IsType<OkObjectResult>(await fixture.Controller.Send(fixture.ConversationId, request));
            Assert.Same(committed, response.Value);
            Assert.Equal(committed.Message!.Id, ((MessagingMessageResult)response.Value!).Message!.Id);
        }
        Assert.Equal(2, events.Count);
        Assert.All(events, notification =>
        {
            Assert.Equal("messageReceived", notification.EventType);
            Assert.Equal(fixture.ConversationId, notification.ConversationId);
            Assert.Equal(committed.Message!.Id, notification.MessageId);
            Assert.Same(recipients, notification.Recipients);
        });
        fixture.Service.Verify(service => service.SendMessageAsync(It.IsAny<SendMessagingMessageCommand>(), fixture.Token), Times.Exactly(2));
        fixture.Service.Verify(service => service.GetConversationRealtimeRecipientsAsync(fixture.Actor, fixture.ConversationId, fixture.Token), Times.Exactly(2));
        fixture.VerifyNoConversationHydration();
        fixture.Service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingResolvedActorCannotSendOrPublish()
    {
        using var fixture = new Fixture(MessagingParticipantTypes.Client, resolved: false);
        Assert.IsType<ForbidResult>(await fixture.Controller.Send(fixture.ConversationId,
            new SendMessagingMessageRequest("Unauthorized control", "unauthorized-retry")));
        fixture.Service.VerifyNoOtherCalls();
        fixture.Publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AuthoritativeSendDenialCannotReadRecipientsOrPublish()
    {
        using var fixture = new Fixture(MessagingParticipantTypes.Client);
        fixture.Service.Setup(service => service.SendMessageAsync(It.IsAny<SendMessagingMessageCommand>(), fixture.Token))
            .ReturnsAsync(MessagingMessageResult.Failure("MESSAGING_RECIPIENT_FORBIDDEN", "Recipient is unavailable."));
        var response = Assert.IsType<ObjectResult>(await fixture.Controller.Send(fixture.ConversationId,
            new SendMessagingMessageRequest("Denied recipient control", "denied-retry")));
        Assert.Equal(StatusCodes.Status403Forbidden, response.StatusCode);
        fixture.Service.Verify(service => service.SendMessageAsync(It.IsAny<SendMessagingMessageCommand>(), fixture.Token), Times.Once);
        fixture.Service.VerifyNoOtherCalls();
        fixture.Publisher.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulSendRemainsOkWhenAdvisoryRealtimeFails(bool recipientLookupFails)
    {
        using var fixture = new Fixture(MessagingParticipantTypes.Agent);
        var request = new SendMessagingMessageRequest("Persisted success control", "committed-retry");
        var committed = fixture.Success(request.Body);
        fixture.Service.Setup(service => service.SendMessageAsync(It.IsAny<SendMessagingMessageCommand>(), fixture.Token))
            .ReturnsAsync(committed);
        var recipientLookup = fixture.Service.Setup(service => service.GetConversationRealtimeRecipientsAsync(
            fixture.Actor, fixture.ConversationId, fixture.Token));
        if (recipientLookupFails)
        {
            recipientLookup.ThrowsAsync(new InvalidOperationException("Advisory recipients unavailable"));
        }
        else
        {
            recipientLookup.ReturnsAsync(new[] { new MessagingRealtimeRecipient("recipient", MessagingParticipantTypes.Client) });
            fixture.Publisher.Setup(publisher => publisher.PublishAsync(It.IsAny<MessagingRealtimeEvent>(), fixture.Token))
                .ThrowsAsync(new InvalidOperationException("Advisory transport unavailable"));
        }
        var response = Assert.IsType<OkObjectResult>(await fixture.Controller.Send(fixture.ConversationId, request));
        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Same(committed, response.Value);
        Assert.True(((MessagingMessageResult)response.Value!).Succeeded);
        fixture.Service.Verify(service => service.SendMessageAsync(It.IsAny<SendMessagingMessageCommand>(), fixture.Token), Times.Once);
        fixture.Service.Verify(service => service.GetConversationRealtimeRecipientsAsync(fixture.Actor, fixture.ConversationId, fixture.Token), Times.Once);
        fixture.Publisher.Verify(publisher => publisher.PublishAsync(It.IsAny<MessagingRealtimeEvent>(), fixture.Token),
            recipientLookupFails ? Times.Never() : Times.Once());
        fixture.VerifyNoConversationHydration();
        fixture.Service.VerifyNoOtherCalls();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        private readonly CancellationTokenSource _cancellation = new();
        public MessagingActor Actor { get; }
        public Guid ConversationId { get; } = Guid.NewGuid();
        public CancellationToken Token => _cancellation.Token;
        public Mock<IMessagingService> Service { get; } = new(MockBehavior.Strict);
        public Mock<IMessagingRealtimePublisher> Publisher { get; } = new(MockBehavior.Strict);
        public MessagingController Controller { get; }

        public Fixture(string participantType, bool resolved = true)
        {
            Actor = new MessagingActor("resolved-web-actor", participantType);
            var resolver = new Mock<IMessagingActorContextResolver>(MockBehavior.Strict);
            (string UserId, string ParticipantType)? identity = resolved ? (Actor.UserId, Actor.ParticipantType) : null;
            resolver.Setup(value => value.ResolveAsync(It.IsAny<HttpContext>(), Token)).ReturnsAsync(identity);
            Controller = new MessagingController(Service.Object, Mock.Of<IMessageAttachmentStorage>(), Publisher.Object,
                Mock.Of<IMessagingProfileImageResolver>(), Mock.Of<IMessagingContactKeyProtector>(), resolver.Object)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        RequestServices = _services, RequestAborted = Token,
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, "non-authoritative-claim")
                        }, "test"))
                    }
                }
            };
        }

        public MessagingMessageResult Success(string body, Guid? replyId = null) => new(true, null, null,
            new MessagingMessageSummary(Guid.NewGuid(), ConversationId, Actor.UserId, Actor.ParticipantType,
                body, DateTime.UtcNow, null, false, Array.Empty<MessagingAttachmentSummary>(), replyId), ConversationId);

        public void VerifyNoConversationHydration() => Service.Verify(service => service.GetConversationAsync(
            It.IsAny<MessagingActor>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());

        public void Dispose()
        {
            _cancellation.Dispose();
            _services.Dispose();
        }
    }
}
