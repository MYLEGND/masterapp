using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AgentPortal.Services;
using ClientApp.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using ClientAvatarController = ClientApp.Controllers.AvatarController;
using AgentAvatarController = AgentPortal.Controllers.AvatarController;

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
        await using var db = ControllerTestHelpers.BuildDb();
            var agent = new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = "agent-identity",
                AgentUpn = "shared@example.test",
                NormalizedEmail = "shared@example.test",
                FullName = "Agent Owner",
                IsActive = true,
                ProfileImageContent = "agent avatar"u8.ToArray(),
                ProfileImageContentType = "image/png"
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

            client.ProfileImageContent = "client avatar"u8.ToArray();
            client.ProfileImageContentType = "image/png";
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
            Assert.Equal("agent avatar"u8.ToArray(), resolvedAgentImage!.Content);
            Assert.Equal("image/png", resolvedAgentImage.ContentType);
            Assert.Equal("image/png", resolvedClientImage!.ContentType);
            Assert.Equal("client avatar"u8.ToArray(), resolvedClientImage.Content);
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

            var resolver = CreateResolver(db);
            var identities = await resolver.ResolveIdentitiesAsync(
                [new MessagingParticipantReference(client.ClientUserId, MessagingParticipantTypes.Client)]);

            var identity = Assert.Single(identities.Values);
            Assert.Equal(client.Id, identity.ProfileId);
            Assert.Equal("Pending Client", identity.DisplayName);
            Assert.Equal("PC", identity.Initials);
            Assert.Null(await resolver.ResolveAsync(identity));
    }

    [Fact]
    public async Task ClientProfileImage_RemainsClientOwnedWhenAgentAndClientShareAUserId()
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
                IsActive = true,
                ProfileImageContent = "agent profile image"u8.ToArray(),
                ProfileImageContentType = "image/png"
            });
            db.ClientProfiles.Add(client);
            await db.SaveChangesAsync();

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

            Assert.Equal("agent profile image"u8.ToArray(), agentImage!.Content);
            Assert.Equal("image/png", agentImage.ContentType);
            Assert.Equal("client profile image"u8.ToArray(), clientImage!.Content);
            Assert.Equal("image/webp", clientImage.ContentType);
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
        var controller = new ClientAvatarController(
            db,
            new EffectiveClientContextService(db),
            CreateResolver(db))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
        // Valid PNG file signature (magic bytes) so the shared upload validator
        // accepts it — the client Content-Type alone is no longer trusted.
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
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
    }

    [Fact]
    public async Task AgentAvatarUpload_PersistsToAgentProfileAndIsReturnedToAnAuthorizedClient()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        const string agentUserId = "agent-avatar-owner";
        var agent = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = agentUserId,
            AgentUpn = "agent.avatar@example.test",
            FullName = "Agent Profile Owner",
            IsActive = true
        };
        db.AgentProfiles.Add(agent);
        await db.SaveChangesAsync();

        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", agentUserId)], "TestAuth"));
        var httpContext = new DefaultHttpContext { User = user };
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(value => value.WebRootPath).Returns(AppContext.BaseDirectory);
        var controller = new AgentAvatarController(
            db,
            environment.Object,
            NullLogger<AgentAvatarController>.Instance,
            new AgentPortal.Services.Tracking.AgentTrackingResolver(
                db,
                NullLogger<AgentPortal.Services.Tracking.AgentTrackingResolver>.Instance),
            new AgentProfileAccessResolver(db),
            CreateResolver(db))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>())
        };
        // Valid WEBP file signature ("RIFF"...."WEBP") so the shared upload
        // validator accepts it — the client Content-Type alone is no longer trusted.
        var imageBytes = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x10, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, 0x00, 0x00 };
        await using var imageStream = new MemoryStream(imageBytes);
        var upload = new FormFile(imageStream, 0, imageBytes.Length, "photo", "avatar.webp")
        {
            Headers = new HeaderDictionary { ["Content-Type"] = "image/webp" }
        };

        Assert.IsType<RedirectToActionResult>(await controller.Upload(upload));

        var persisted = await db.AgentProfiles.SingleAsync(x => x.Id == agent.Id);
        Assert.Equal(imageBytes, persisted.ProfileImageContent);
        Assert.Equal("image/webp", persisted.ProfileImageContentType);

        var resolver = CreateResolver(db);
        var identities = await resolver.ResolveIdentitiesAsync(
            [new MessagingParticipantReference(agentUserId, MessagingParticipantTypes.Agent)]);
        var agentIdentity = identities[(agentUserId, MessagingParticipantTypes.Agent)];
        var image = await resolver.ResolveAsync(agentIdentity);

        Assert.NotNull(image);
        Assert.Equal(imageBytes, image!.Content);
        Assert.Equal("image/webp", image.ContentType);
    }

    [Fact]
    public async Task AgentAvatarUpload_UsesTheAzureSyncedDirectoryEmailForAnExistingProfile()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var agent = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "preproduction-oid",
            AgentUpn = "zac.owen@mylegnd.com",
            NormalizedEmail = "zac.owen@mylegnd.com",
            FullName = "Zac Owen",
            IsActive = true
        };
        db.AgentProfiles.Add(agent);
        await db.SaveChangesAsync();

        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("oid", "azure-production-oid"),
            new Claim("preferred_username", "zac.owen@mylegnd.com")
        ], "TestAuth"));
        var httpContext = new DefaultHttpContext { User = user };
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(value => value.WebRootPath).Returns(AppContext.BaseDirectory);
        var controller = new AgentAvatarController(
            db,
            environment.Object,
            NullLogger<AgentAvatarController>.Instance,
            new AgentPortal.Services.Tracking.AgentTrackingResolver(
                db,
                NullLogger<AgentPortal.Services.Tracking.AgentTrackingResolver>.Instance),
            new AgentProfileAccessResolver(db),
            CreateResolver(db))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>())
        };
        var imageBytes = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x10, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, 0x00, 0x00 };
        await using var imageStream = new MemoryStream(imageBytes);
        var upload = new FormFile(imageStream, 0, imageBytes.Length, "photo", "avatar.webp")
        {
            Headers = new HeaderDictionary { ["Content-Type"] = "image/webp" }
        };

        Assert.IsType<RedirectToActionResult>(await controller.Upload(upload));

        var persisted = await db.AgentProfiles.SingleAsync(profile => profile.Id == agent.Id);
        Assert.Equal(imageBytes, persisted.ProfileImageContent);
    }

    [Fact]
    public async Task ActiveAgentProfiles_ResolveTheirOwnImagesIncludingLegacyJpgContentTypes()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var first = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "first-active-agent",
            AgentUpn = "first.active@example.test",
            FullName = "First Active Agent",
            IsActive = true,
            ProfileImageContent = "first agent image"u8.ToArray(),
            ProfileImageContentType = "image/png"
        };
        var second = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "second-active-agent",
            AgentUpn = "second.active@example.test",
            FullName = "Second Active Agent",
            IsActive = true,
            ProfileImageContent = "second agent image"u8.ToArray(),
            ProfileImageContentType = "image/jpg"
        };
        db.AgentProfiles.AddRange(first, second);
        await db.SaveChangesAsync();

        var resolver = CreateResolver(db);
        var identities = await resolver.ResolveIdentitiesAsync(
        [
            new MessagingParticipantReference(first.AgentUserId, MessagingParticipantTypes.Agent),
            new MessagingParticipantReference(second.AgentUserId, MessagingParticipantTypes.Agent)
        ]);

        var firstImage = await resolver.ResolveAsync(
            identities[(first.AgentUserId, MessagingParticipantTypes.Agent)]);
        var secondImage = await resolver.ResolveAsync(
            identities[(second.AgentUserId, MessagingParticipantTypes.Agent)]);

        Assert.Equal("first agent image"u8.ToArray(), firstImage!.Content);
        Assert.Equal("image/png", firstImage.ContentType);
        Assert.Equal("second agent image"u8.ToArray(), secondImage!.Content);
        Assert.Equal("image/jpeg", secondImage.ContentType);
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
    public async Task LegacyAgentAvatar_IsImportedIntoItsMatchingAgentProfileOnly()
    {
        await UseAvatarRootAsync(async root =>
        {
            await using var db = ControllerTestHelpers.BuildDb();
            var agent = new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = "legacy-agent",
                AgentUpn = "legacy.agent@example.test",
                FullName = "Legacy Agent",
                IsActive = true
            };
            var otherAgent = new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = "other-agent",
                AgentUpn = "other.agent@example.test",
                FullName = "Other Agent",
                IsActive = true
            };
            db.AgentProfiles.AddRange(agent, otherAgent);
            await db.SaveChangesAsync();

            var imageBytes = "legacy agent profile image"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(root, "legacy-agent.png"), imageBytes);
            var environment = new Mock<IWebHostEnvironment>();
            environment.SetupGet(value => value.ContentRootPath).Returns(AppContext.BaseDirectory);
            var importer = new AgentProfileImageLegacyBackfillService(
                db,
                environment.Object,
                NullLogger<AgentProfileImageLegacyBackfillService>.Instance);

            Assert.Equal(1, await importer.BackfillAsync());

            var imported = await db.AgentProfiles.SingleAsync(profile => profile.Id == agent.Id);
            var untouched = await db.AgentProfiles.SingleAsync(profile => profile.Id == otherAgent.Id);
            Assert.Equal(imageBytes, imported.ProfileImageContent);
            Assert.Equal("image/png", imported.ProfileImageContentType);
            Assert.Null(untouched.ProfileImageContent);
            Assert.Null(untouched.ProfileImageContentType);
        });
    }

    [Fact]
    public async Task LegacyAgentAvatarWithAReconciledTrackingIdentity_IsImportedIntoTheCanonicalAgentProfile()
    {
        await UseAvatarRootAsync(async root =>
        {
            await using var db = ControllerTestHelpers.BuildDb();
            var agent = new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = "current-agent-id",
                AgentUpn = "agent.owner@example.test",
                FullName = "Agent Owner",
                IsActive = true
            };
            db.AgentProfiles.Add(agent);
            db.AgentTrackingProfiles.Add(new AgentTrackingProfile
            {
                AgentUserId = "legacy-agent-id",
                AgentUpn = agent.AgentUpn,
                Slug = "agent-owner"
            });
            await db.SaveChangesAsync();

            var imageBytes = "legacy agent profile image"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(root, "legacy-agent-id.webp"), imageBytes);
            var environment = new Mock<IWebHostEnvironment>();
            environment.SetupGet(value => value.ContentRootPath).Returns(AppContext.BaseDirectory);
            var importer = new AgentProfileImageLegacyBackfillService(
                db,
                environment.Object,
                NullLogger<AgentProfileImageLegacyBackfillService>.Instance);

            Assert.Equal(1, await importer.BackfillAsync());

            var persisted = await db.AgentProfiles.SingleAsync(profile => profile.Id == agent.Id);
            Assert.Equal(imageBytes, persisted.ProfileImageContent);
            Assert.Equal("image/webp", persisted.ProfileImageContentType);

            var resolver = CreateResolver(db);
            var identity = (await resolver.ResolveIdentitiesAsync(
                [new MessagingParticipantReference(agent.AgentUserId, MessagingParticipantTypes.Agent)]))
                [(agent.AgentUserId, MessagingParticipantTypes.Agent)];
            var image = await resolver.ResolveAsync(identity);

            Assert.NotNull(image);
            Assert.Equal(imageBytes, image!.Content);
            Assert.Equal("image/webp", image.ContentType);
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
        return new MessagingProfileImageResolver(
            db,
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
