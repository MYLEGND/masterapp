using System;
using System.Threading.Tasks;
using Domain.Billing;
using Domain.Entities;
using Domain.Messaging;
using Domain.Moderation;
using Infrastructure.Moderation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommunitySafetyServiceTests
{
    [Fact]
    public async Task TypedAgentBlockAndSocialReport_UseOneSafetyAuthorityAndValidateTheActualTarget()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "safety-client",
            ExternalIdentityObjectId = "safety-client",
            FirstName = "Safety",
            LastName = "Client",
            Email = "safety-client@example.test"
        };
        var agent = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "safety-agent",
            AgentUpn = "safety-agent@example.test",
            FullName = "Safety Agent",
            IsActive = true
        };
        var post = new SocialPost
        {
            AuthorUserId = agent.AgentUserId,
            AuthorParticipantType = MessagingParticipantTypes.Agent,
            AuthorProfileId = agent.Id,
            ContentType = "Post",
            Audience = "AuthorizedNetwork",
            Body = "Reportable post"
        };
        db.AddRange(client, agent, post);
        db.ClientEntitlements.Add(new ClientEntitlement
        {
            Id = Guid.NewGuid(),
            ClientProfileId = client.Id,
            EntitlementKey = BillingEntitlementKeys.ClientAppFullAccess,
            Status = ClientEntitlementStatus.Active,
            SourceType = ClientEntitlementSourceType.Subscription,
            SourceId = "safety-client-membership",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = new CommunitySafetyService(db);
        var clientActor = new MessagingActor(client.ClientUserId, MessagingParticipantTypes.Client);
        var agentActor = new MessagingActor(agent.AgentUserId, MessagingParticipantTypes.Agent);

        var blocked = await service.BlockAsync(new CommunitySafetyBlockCommand(clientActor, agentActor));
        var report = await service.ReportAsync(new CommunitySafetyReportCommand(
            clientActor,
            agentActor,
            CommunitySafetyTargetKinds.SocialPost,
            post.Id,
            "Harassment",
            "This violates the community rules."));
        var invalidReport = await service.ReportAsync(new CommunitySafetyReportCommand(
            clientActor,
            agentActor,
            CommunitySafetyTargetKinds.SocialPost,
            Guid.NewGuid(),
            "Harassment",
            "Not a real target."));

        Assert.True(blocked.Succeeded);
        Assert.True(await service.IsInteractionBlockedAsync(clientActor, agentActor));
        var block = await db.JourneyCircleBlocks.SingleAsync();
        Assert.Equal(client.ClientUserId, block.BlockerUserId);
        Assert.Equal(MessagingParticipantTypes.Client, block.BlockerParticipantType);
        Assert.Equal(agent.AgentUserId, block.BlockedUserId);
        Assert.Equal(MessagingParticipantTypes.Agent, block.BlockedParticipantType);
        Assert.Null(block.BlockedClientProfileId);
        Assert.True(report.Succeeded);
        Assert.False(invalidReport.Succeeded);
        var open = Assert.Single(await service.GetOpenReportsAsync(10));
        Assert.Equal(post.Id, open.TargetEntityId);
        Assert.Equal(CommunitySafetyTargetKinds.SocialPost, open.TargetKind);
        var resolved = await service.ResolveReportAsync(open.Id, "founder-oid", CommunitySafetyReviewResolutions.NeedsInvestigation);
        Assert.True(resolved.Succeeded);
        Assert.Empty(await service.GetOpenReportsAsync(10));
    }
}
