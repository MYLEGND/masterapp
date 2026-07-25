using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using ClientApp.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using ClientAvatarController = ClientApp.Controllers.AvatarController;

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
            client.ProfileImageContent = "client avatar"u8.ToArray();
            client.ProfileImageContentType = "image/png";
            await File.WriteAllTextAsync(agentAvatar, "agent avatar");
            await db.SaveChangesAsync();

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
            var resolvedAgentImage = await resolver.ResolveAsync(resolvedAgent);
            var resolvedClientImage = await resolver.ResolveAsync(resolvedClient);
            Assert.Equal(agentAvatar, resolvedAgentImage!.PhysicalPath);
            Assert.Null(resolvedAgentImage.Content);
            Assert.Equal("image/png", resolvedClientImage!.ContentType);
            Assert.Equal("client avatar"u8.ToArray(), resolvedClientImage.Content);
            Assert.Null(resolvedClientImage.PhysicalPath);
        });
    }

    [Fact]
    public async Task SameCanonicalUserIdInAgentAndClientProfiles_ResolvesBothExplicitParticipantTypesIndependently()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        const string canonicalUserId = "dual-role-user";

        var agent = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = canonicalUserId,
            AgentUpn = "dual.role@example.test",
            NormalizedEmail = "dual.role@example.test",
            FullName = "Dual Role Agent",
            IsActive = true
        };
        var client = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = canonicalUserId,
            ExternalIdentityObjectId = canonicalUserId,
            Email = "dual.role@example.test",
            NormalizedEmail = "dual.role@example.test",
            FirstName = "Dual Role",
            LastName = "Client"
        };

        db.AgentProfiles.Add(agent);
        db.ClientProfiles.Add(client);
        await db.SaveChangesAsync();

        var resolver = CreateResolver(db);
        var identities = await resolver.ResolveIdentitiesAsync(
        [
            new MessagingParticipantReference(canonicalUserId, MessagingParticipantTypes.Agent),
            new MessagingParticipantReference(canonicalUserId, MessagingParticipantTypes.Client)
        ]);

        Assert.Equal(2, identities.Count);

        Assert.True(
            identities.TryGetValue(
                (canonicalUserId, MessagingParticipantTypes.Agent),
                out var resolvedAgent));
        Assert.NotNull(resolvedAgent);
        Assert.Equal(agent.Id, resolvedAgent.ProfileId);
        Assert.Equal(MessagingParticipantTypes.Agent, resolvedAgent.ParticipantType);
        Assert.Equal("Dual Role Agent", resolvedAgent.DisplayName);

        Assert.True(
            identities.TryGetValue(
                (canonicalUserId, MessagingParticipantTypes.Client),
                out var resolvedClient));
        Assert.NotNull(resolvedClient);
        Assert.Equal(client.Id, resolvedClient.ProfileId);
        Assert.Equal(MessagingParticipantTypes.Client, resolvedClient.ParticipantType);
        Assert.Equal("Dual Role Client", resolvedClient.DisplayName);
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
    public async Task ClientProfileImage_RemainsClientOwnedWhenAgentAndClientShareAUserId()
    {
        await UseAvatarRootAsync(async root =>
        {
            await using var db = ControllerTestHelpers.BuildDb();
            const string userId = "dual-role-user";
            var client = new ClientProfile
            {
                Id = Guid.NewGuid(),
                ClientUserId = userId,
                ExternalIdentityObjectId = userId,
                FirstName = "Client",
                LastName = "Owner",
                Email = "client@example.test",
                ProfileImageContent = "client profile image"u8.ToArray(),
                ProfileImageContentType = "image/webp"
            };
            db.AgentProfiles.Add(new AgentProfile
            {
                AgentUserId = userId,
                AgentUpn = "agent@example.test",
                FullName = "Agent Owner",
                IsActive = true
            });
            db.ClientProfiles.Add(client);
            await db.SaveChangesAsync();
            await File.WriteAllTextAsync(Path.Combine(root, $"{userId}.png"), "agent avatar");

            var resolver = CreateResolver(db);
            var identities = await resolver.ResolveIdentitiesAsync(
            [
                new MessagingParticipantReference(userId, MessagingParticipantTypes.Agent),
                new MessagingParticipantReference(userId, MessagingParticipantTypes.Client)
            ]);

            var agent = identities[(userId, MessagingParticipantTypes.Agent)];
            var clientIdentity = identities[(userId, MessagingParticipantTypes.Client)];
            var agentImage = await resolver.ResolveAsync(agent);
            var clientImage = await resolver.ResolveAsync(clientIdentity);

            Assert.Equal(Path.Combine(root, $"{userId}.png"), agentImage!.PhysicalPath);
            Assert.Null(agentImage.Content);
            Assert.Null(clientImage!.PhysicalPath);
            Assert.Equal("client profile image"u8.ToArray(), clientImage.Content);
            Assert.Equal("image/webp", clientImage.ContentType);
        });
    }

    [Fact]
    public async Task ClientAvatarUpload_PersistsToClientProfileAndIsReturnedToAnAuthorizedAgent()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        const string clientUserId = "client-avatar-owner";
        var client = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = clientUserId,
            ExternalIdentityObjectId = clientUserId,
            FirstName = "Profile",
            LastName = "Owner",
            Email = "profile.owner@example.test"
        };
        db.ClientProfiles.Add(client);
        await db.SaveChangesAsync();

        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("oid", clientUserId),
            new Claim("preferred_username", client.Email)
        ],
        "TestAuth"));
        var httpContext = new DefaultHttpContext { User = user };
        var controller = new ClientAvatarController(db, new EffectiveClientContextService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
        var imageBytes = "profile owned image"u8.ToArray();
        await using var imageStream = new MemoryStream(imageBytes);
        var upload = new FormFile(imageStream, 0, imageBytes.Length, "photo", "avatar.png")
        {
            Headers = new HeaderDictionary { ["Content-Type"] = "image/png" }
        };

        Assert.IsType<OkObjectResult>(await controller.Upload(upload));

        var persisted = await db.ClientProfiles.SingleAsync(x => x.Id == client.Id);
        Assert.Equal(imageBytes, persisted.ProfileImageContent);
        Assert.Equal("image/png", persisted.ProfileImageContentType);

        var resolver = CreateResolver(db);
        var identities = await resolver.ResolveIdentitiesAsync(
            [new MessagingParticipantReference(clientUserId, MessagingParticipantTypes.Client)]);
        var clientIdentity = identities[(clientUserId, MessagingParticipantTypes.Client)];
        var image = await resolver.ResolveAsync(clientIdentity);

        Assert.NotNull(image);
        Assert.Equal(imageBytes, image!.Content);
        Assert.Equal("image/png", image.ContentType);
        Assert.Null(image.PhysicalPath);
    }

    [Fact]
    public async Task LegacyClientAvatar_IsImportedIntoItsMatchingClientProfileOnly()
    {
        await UseAvatarRootAsync(async root =>
        {
            await using var db = ControllerTestHelpers.BuildDb();
            var client = new ClientProfile
            {
                Id = Guid.NewGuid(),
                ClientUserId = "legacy-client",
                FirstName = "Legacy",
                LastName = "Client",
                Email = "legacy.client@example.test"
            };
            var otherClient = new ClientProfile
            {
                Id = Guid.NewGuid(),
                ClientUserId = "other-client",
                FirstName = "Other",
                LastName = "Client",
                Email = "other.client@example.test"
            };
            db.ClientProfiles.AddRange(client, otherClient);
            await db.SaveChangesAsync();

            var imageBytes = "legacy profile image"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(root, $"{client.Id:D}.webp"), imageBytes);
            var environment = new Mock<IWebHostEnvironment>();
            environment.SetupGet(value => value.ContentRootPath).Returns(AppContext.BaseDirectory);
            var importer = new ClientProfileImageLegacyBackfillService(
                db,
                environment.Object,
                NullLogger<ClientProfileImageLegacyBackfillService>.Instance);

            Assert.Equal(1, await importer.BackfillAsync());

            var imported = await db.ClientProfiles.SingleAsync(profile => profile.Id == client.Id);
            var untouched = await db.ClientProfiles.SingleAsync(profile => profile.Id == otherClient.Id);
            Assert.Equal(imageBytes, imported.ProfileImageContent);
            Assert.Equal("image/webp", imported.ProfileImageContentType);
            Assert.Null(untouched.ProfileImageContent);
            Assert.Null(untouched.ProfileImageContentType);
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
