using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

[CollectionDefinition("MessagingAvatarEnvironment", DisableParallelization = true)]
public sealed class MessagingAvatarEnvironmentCollection
{
}

[Collection("MessagingAvatarEnvironment")]
public sealed class MessagingProfileImageResolverTests
{
    [Fact]
    public async Task SharedEmail_ResolvesDistinctTypedProfilesAndTheirOwnAvatarKeys()
    {
        await UseAvatarRootAsync(async root =>
        {
            await using var db = ControllerTestHelpers.BuildDb();
            var agent = new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = "agent-identity",
                AgentUpn = "shared@example.test",
                NormalizedEmail = "shared@example.test",
                FullName = "Agent Owner",
                IsActive = true
            };
            var client = new ClientProfile
            {
                Id = Guid.NewGuid(),
                ClientUserId = "client-identity",
                ExternalIdentityObjectId = "client-identity",
                Email = "shared@example.test",
                NormalizedEmail = "shared@example.test",
                FirstName = "Client",
                LastName = "Owner"
            };
            db.AgentProfiles.Add(agent);
            db.ClientProfiles.Add(client);
            await db.SaveChangesAsync();

            var agentAvatar = Path.Combine(root, "agent-identity.png");
            var clientAvatar = Path.Combine(root, $"{client.Id:D}.png");
            await File.WriteAllTextAsync(agentAvatar, "agent avatar");
            await File.WriteAllTextAsync(clientAvatar, "client avatar");

            var resolver = CreateResolver(db);
            var identities = await resolver.ResolveIdentitiesAsync(
            [
                new MessagingParticipantReference("agent-identity", MessagingParticipantTypes.Agent),
                new MessagingParticipantReference("client-identity", MessagingParticipantTypes.Client)
            ]);

            var resolvedAgent = Assert.Single(identities.Values.Where(value => value.ParticipantType == MessagingParticipantTypes.Agent));
            var resolvedClient = Assert.Single(identities.Values.Where(value => value.ParticipantType == MessagingParticipantTypes.Client));
            Assert.Equal(agent.Id, resolvedAgent.ProfileId);
            Assert.Equal(client.Id, resolvedClient.ProfileId);
            Assert.Equal("Agent Owner", resolvedAgent.DisplayName);
            Assert.Equal("Client Owner", resolvedClient.DisplayName);
            Assert.Equal(agentAvatar, (await resolver.ResolveAsync(resolvedAgent))!.PhysicalPath);
            Assert.Equal(clientAvatar, (await resolver.ResolveAsync(resolvedClient))!.PhysicalPath);
        });
    }

    [Fact]
    public async Task ClientWithoutActivatedIdentity_UsesItsOwnInitialsAndNeverTheServicingAgentAvatar()
    {
        await UseAvatarRootAsync(async root =>
        {
            await using var db = ControllerTestHelpers.BuildDb();
            var client = new ClientProfile
            {
                Id = Guid.NewGuid(),
                ClientUserId = "pending-client",
                Email = "shared@example.test",
                FirstName = "Pending",
                LastName = "Client"
            };
            db.AgentProfiles.Add(new AgentProfile
            {
                AgentUserId = "servicing-agent",
                AgentUpn = "shared@example.test",
                FullName = "Servicing Agent",
                IsActive = true
            });
            db.AgentClients.Add(new AgentClient
            {
                AgentUserId = "servicing-agent",
                AgentUpn = "shared@example.test",
                ClientUserId = client.ClientUserId
            });
            db.ClientProfiles.Add(client);
            await db.SaveChangesAsync();
            await File.WriteAllTextAsync(Path.Combine(root, "servicing-agent.png"), "agent avatar");

            var resolver = CreateResolver(db);
            var identities = await resolver.ResolveIdentitiesAsync(
                [new MessagingParticipantReference(client.ClientUserId, MessagingParticipantTypes.Client)]);

            var identity = Assert.Single(identities.Values);
            Assert.Equal(client.Id, identity.ProfileId);
            Assert.Equal("Pending Client", identity.DisplayName);
            Assert.Equal("PC", identity.Initials);
            Assert.Null(await resolver.ResolveAsync(identity));
        });
    }

    [Fact]
    public async Task AmbiguousTypedClientIdentity_FailsClosedInsteadOfSelectingTheFirstProfile()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.ClientProfiles.AddRange(
            new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "first-client", ExternalIdentityObjectId = "ambiguous-client", FirstName = "First", LastName = "Client", Email = "first@example.test" },
            new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "ambiguous-client", FirstName = "Second", LastName = "Client", Email = "second@example.test" });
        await db.SaveChangesAsync();

        var resolver = CreateResolver(db);
        var identities = await resolver.ResolveIdentitiesAsync(
            [new MessagingParticipantReference("ambiguous-client", MessagingParticipantTypes.Client)]);

        Assert.Empty(identities);
    }

    [Fact]
    public void ContactKey_IsOpaqueAndBoundToTheAuthenticatedViewerAndParticipantType()
    {
        var protector = new MessagingContactKeyProtector(new EphemeralDataProtectionProvider());
        var agent = new MessagingActor("agent-identity", MessagingParticipantTypes.Agent);
        var recipient = new MessagingRecipientSummary(
            "client-identity",
            MessagingParticipantTypes.Client,
            "Client Owner",
            "shared@example.test");

        var key = protector.Protect(agent, recipient);

        Assert.DoesNotContain("client-identity", key, StringComparison.Ordinal);
        Assert.True(protector.TryUnprotect(agent, key, out var participant));
        Assert.Equal("client-identity", participant.UserId);
        Assert.Equal(MessagingParticipantTypes.Client, participant.ParticipantType);
        Assert.False(protector.TryUnprotect(
            new MessagingActor("other-agent", MessagingParticipantTypes.Agent),
            key,
            out _));
    }

    private static MessagingProfileImageResolver CreateResolver(Infrastructure.Data.MasterAppDbContext db)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(value => value.ContentRootPath).Returns(AppContext.BaseDirectory);
        return new MessagingProfileImageResolver(
            db,
            environment.Object,
            NullLogger<MessagingProfileImageResolver>.Instance);
    }

    private static async Task UseAvatarRootAsync(Func<string, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), $"masterapp-avatar-tests-{Guid.NewGuid():N}");
        var originalRoot = Environment.GetEnvironmentVariable("LEGEND_AVATAR_ROOT");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("LEGEND_AVATAR_ROOT", root);
        try
        {
            await test(root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LEGEND_AVATAR_ROOT", originalRoot);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
