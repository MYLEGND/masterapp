using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Models;
using AgentPortal.Services;
using AgentPortal.Services.Tracking;
using Domain.Accounts;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shared.Auth;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Stage 1 Identity Hardening protection tests.
/// Verifies that NormalizedEmail is populated on every write path
/// and that duplicate-identity guards function correctly.
/// </summary>
public class IdentityHardeningTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FounderAuthority_EmptyObjectIdCannotGrantAccessThroughAnyBoundary(bool production)
    {
        var empty = Guid.Empty.ToString();
        var user = BuildUserWithEmail(empty, "founder@example.com");
        var fallbackCalls = 0;
        Assert.False(FounderAuthority.Evaluate(user, empty, production, _ =>
        {
            fallbackCalls++;
            return true;
        }));
        Assert.False(FounderAuthority.IsConfiguredAndValid(empty));
        Assert.Null(FounderAuthority.GetConfiguredObjectId(" " + empty + " "));
        Assert.False(FounderAuthority.IsConfiguredFounderIdentity(empty, empty));
        Assert.Equal(0, fallbackCalls);
    }

    [Fact]
    public void FounderAuthority_ValidObjectIdRemainsUsableAcrossIdentityBoundaries()
    {
        const string founder = "dacbaef1-2879-4c4b-bd2e-c7d327f99013";
        var configured = " " + founder.ToUpperInvariant() + " ";
        Assert.True(FounderAuthority.IsConfiguredAndValid(configured));
        Assert.Equal(founder, FounderAuthority.GetConfiguredObjectId(configured));
        Assert.True(FounderAuthority.IsConfiguredFounderIdentity(founder, configured));
        Assert.True(FounderAuthority.Evaluate(BuildUserWithEmail(founder, "founder@example.com"),
            configured, true, _ => throw new InvalidOperationException("Email cannot grant configured Founder access")));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static MasterAppDbContext BuildDb() => ControllerTestHelpers.BuildDb();

    private static AgentRegistryService BuildRegistry(MasterAppDbContext db)
    {
        var tracking = Mock.Of<IAgentTrackingService>();
        return new AgentRegistryService(db, NullLogger<AgentRegistryService>.Instance, tracking);
    }

    private static AgentTrackingService BuildTrackingService(MasterAppDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Protect:PublicBaseUrl", "https://protect.mylegnd.com"),
                new KeyValuePair<string, string?>("Founder:Upn", "zac.owen@mylegnd.com")
            })
            .Build();
        return new AgentTrackingService(db, NullLogger<AgentTrackingService>.Instance, config);
    }

    private static IAccountLifecycleService BuildActiveAccountLifecycle()
    {
        var lifecycle = new Mock<IAccountLifecycleService>();
        lifecycle
            .Setup(service => service.GetAsync(
                It.IsAny<AccountLifecycleSubject>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountLifecycleSnapshot("Active", true, false, null, null, null));
        return lifecycle.Object;
    }

    private static ClientProvisioningService BuildProvisioning(MasterAppDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("GraphProvisioning:TenantId", "test-tenant"),
                new KeyValuePair<string, string?>("GraphProvisioning:ClientId", "test-client"),
                new KeyValuePair<string, string?>("GraphProvisioning:ClientSecret", "test-secret")
            })
            .Build();
        return new ClientProvisioningService(
            config,
            NullLogger<ClientProvisioningService>.Instance,
            db,
            Mock.Of<IClientEntraLifecycleService>());
    }

    private static ClaimsPrincipal BuildUserWithEmail(string oid, string email)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("oid", oid),
            new Claim("preferred_username", email)
        }, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    private static AssistantPanelController BuildAssistantController(MasterAppDbContext db, string agentOid)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("GraphProvisioning:TenantId", "test-tenant"),
                new KeyValuePair<string, string?>("GraphProvisioning:ClientId", "test-client"),
                new KeyValuePair<string, string?>("GraphProvisioning:ClientSecret", "test-secret")
            })
            .Build();
        var provisioning = new ClientProvisioningService(
            config,
            NullLogger<ClientProvisioningService>.Instance,
            db,
            Mock.Of<IClientEntraLifecycleService>());
        var http = new DefaultHttpContext();
        http.Items["EffectiveAgentOid"] = agentOid;
        var tempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        var controller = new AssistantPanelController(db, provisioning, NullLogger<AssistantPanelController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = tempData
        };
        return controller;
    }


    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AgentRegistryService_RelationalRefreshOmitsImageAndPreservesUnselectedColumns(bool oidMatch)
    {
        var original = RegistryProfile(oidMatch ? "AGENT-OID" : "legacy-oid", "OWNER@EXAMPLE.TEST");
        original.NormalizedEmail = null;
        var foreign = RegistryProfile("foreign-oid", "foreign@example.test");
        foreign.ProfileImageContent = new byte[] { 91, 92 };
        await using var fixture = await RegistryFixture.CreateAsync(original, foreign);
        await using var db = fixture.OpenContext();
        var foreignBefore = await db.AgentProfiles.AsNoTracking().SingleAsync(p => p.Id == foreign.Id);
        fixture.Commands.Begin();

        Assert.True(await BuildRegistry(db).UpsertAgentProfileAsync(
            BuildUserWithEmail("agent-oid", "owner@example.test")));

        var commands = fixture.Commands.Text.ToArray();
        fixture.Commands.Stop();
        var reads = commands.Where(sql => sql.StartsWith("SELECT", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, reads.Length);
        Assert.All(reads, sql => Assert.All(RegistryUnselectedColumns,
            column => Assert.DoesNotContain(column, sql, StringComparison.Ordinal)));
        var update = Assert.Single(commands.Where(sql => sql.StartsWith("UPDATE", StringComparison.Ordinal)));
        Assert.All(RegistryUnselectedColumns, column => Assert.DoesNotContain(column, update, StringComparison.Ordinal));
        Assert.Contains("UpdatedUtc", update, StringComparison.Ordinal);
        Assert.Empty(db.ChangeTracker.Entries<AgentProfile>());

        // Find on this same context must load the actual image, not an incomplete
        // update stub left in EF's identity map.
        var saved = await db.AgentProfiles.FindAsync(original.Id);
        Assert.NotNull(saved);
        Assert.Equal(original.AgentUserId, saved!.AgentUserId);
        Assert.Equal("owner@example.test", saved.NormalizedEmail);
        Assert.Equal(original.FullName, saved.FullName);
        AssertRegistryUnselectedValues(original, saved);
        var foreignAfter = await db.AgentProfiles.AsNoTracking().SingleAsync(p => p.Id == foreign.Id);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(foreignBefore),
            System.Text.Json.JsonSerializer.Serialize(foreignAfter));
        Assert.Equal(2, await db.AgentProfiles.CountAsync());
    }

    [Fact]
    public async Task AgentRegistryService_PreservesPretrackedImageNameAndOtherPendingWork()
    {
        var original = RegistryProfile("agent-oid", "owner@example.test");
        var foreign = RegistryProfile("foreign-oid", "foreign@example.test");
        await using var fixture = await RegistryFixture.CreateAsync(original, foreign);
        await using var db = fixture.OpenContext();
        var tracked = await db.AgentProfiles.SingleAsync(p => p.Id == original.Id);
        tracked.FullName = "Pending agent-managed name";
        tracked.ProfileImageContent = new byte[] { 7, 8, 9 };
        tracked.ProfileImageContentType = "image/webp";
        var other = await db.AgentProfiles.SingleAsync(p => p.Id == foreign.Id);
        other.Title = "Pending unrelated edit";
        var unsaved = RegistryProfile("unsaved-oid", "owner@example.test");
        unsaved.Title = "Do not backfill from an unsaved row";
        unsaved.NormalizedEmail = null;
        db.AgentProfiles.Add(unsaved);
        fixture.Commands.Begin();

        await BuildRegistry(db).UpsertAgentProfileAsync(BuildUserWithEmail("agent-oid", "owner@example.test"));

        fixture.Commands.Stop();
        Assert.Same(tracked, await db.AgentProfiles.FindAsync(original.Id));
        Assert.Equal(EntityState.Unchanged, db.Entry(tracked).State);
        Assert.Null(tracked.Title);
        await using var verify = fixture.OpenContext();
        var saved = await verify.AgentProfiles.FindAsync(original.Id);
        Assert.Equal("Pending agent-managed name", saved!.FullName);
        Assert.Equal(new byte[] { 7, 8, 9 }, saved.ProfileImageContent);
        Assert.Equal("image/webp", saved.ProfileImageContentType);
        Assert.Equal("Pending unrelated edit", (await verify.AgentProfiles.FindAsync(foreign.Id))!.Title);
        Assert.Equal(3, await verify.AgentProfiles.CountAsync());
    }

    [Fact]
    public async Task AgentRegistryService_PreservesPendingDeletionAndDoesNotChooseSiblingInstead()
    {
        var original = RegistryProfile("agent-oid", "owner@example.test");
        var sibling = RegistryProfile("legacy-oid", "owner@example.test");
        sibling.Title = "More complete sibling";
        sibling.NormalizedEmail = null;
        await using var fixture = await RegistryFixture.CreateAsync(original, sibling);
        await using var db = fixture.OpenContext();
        var tracked = await db.AgentProfiles.SingleAsync(p => p.Id == original.Id);
        db.AgentProfiles.Remove(tracked);
        var siblingBefore = await db.AgentProfiles.AsNoTracking().SingleAsync(p => p.Id == sibling.Id);
        fixture.Commands.Begin();

        await BuildRegistry(db).UpsertAgentProfileAsync(BuildUserWithEmail("agent-oid", "owner@example.test"));

        fixture.Commands.Stop();
        Assert.Equal(EntityState.Detached, db.Entry(tracked).State);
        Assert.Null(await db.AgentProfiles.FindAsync(original.Id));
        Assert.DoesNotContain(fixture.Commands.Text, sql => sql.StartsWith("INSERT", StringComparison.Ordinal));
        var only = await db.AgentProfiles.AsNoTracking().SingleAsync();
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(siblingBefore),
            System.Text.Json.JsonSerializer.Serialize(only));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AgentRegistryService_PreservesOidPriorityLegacyTieBreakAndDuplicateBackfill(bool oidMatch)
    {
        var current = RegistryProfile("current-oid", "owner@example.test");
        current.Id = Guid.Parse("00000000-0000-0000-0000-000000000003");
        current.Title = "Agent-managed title";
        current.Phone = "Agent-managed phone";
        var preferred = RegistryProfile("legacy-a", "owner@example.test");
        preferred.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        preferred.CreatedUtc = RegistryFixtureTime.AddDays(-1);
        preferred.Title = "Legacy title";
        preferred.Phone = "Legacy phone";
        preferred.BookingEnabled = false;
        preferred.MicrosoftBookingsEmbedUrl = "https://calendar.example.test/preferred";
        preferred.MetaPixelId = "fixture-pixel-a";
        preferred.MetaCapiAccessToken = "fixture-credential-a";
        preferred.CalendarEmail = "calendar@example.test";
        preferred.PreferModalOnMobile = false;
        preferred.DisplayOrder = 3;
        var tied = RegistryProfile("legacy-b", "owner@example.test");
        tied.Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        tied.CreatedUtc = preferred.CreatedUtc;
        tied.Title = "Tied title";
        tied.Phone = "Tied phone";
        tied.BookingEnabled = true;
        tied.MicrosoftBookingsEmbedUrl = "https://calendar.example.test/tied";
        tied.MetaPixelId = "fixture-pixel-b";
        tied.MetaCapiAccessToken = "fixture-credential-b";
        tied.CalendarEmail = "tied@example.test";
        tied.PreferModalOnMobile = true;
        tied.DisplayOrder = 4;
        tied.ProfileImageContent = new byte[] { 43 };
        // Legacy email matches retain null normalized values until the selected
        // identity is reconciled; the database's unique index remains enforced.
        current.NormalizedEmail = null;
        preferred.NormalizedEmail = null;
        tied.NormalizedEmail = null;
        var foreign = RegistryProfile("foreign-oid", "foreign@example.test");
        await using var fixture = await RegistryFixture.CreateAsync(current, preferred, tied, foreign);
        await using var db = fixture.OpenContext();
        var before = await db.AgentProfiles.AsNoTracking().ToDictionaryAsync(p => p.Id);

        await BuildRegistry(db).UpsertAgentProfileAsync(
            BuildUserWithEmail(oidMatch ? "current-oid" : "new-oid", "owner@example.test"));

        var selected = oidMatch ? current : preferred;
        var after = await db.AgentProfiles.AsNoTracking().ToDictionaryAsync(p => p.Id);
        Assert.Equal(4, after.Count);
        var saved = after[selected.Id];
        Assert.Equal(selected.AgentUserId, saved.AgentUserId);
        Assert.Equal(selected.Title, saved.Title);
        Assert.Equal(selected.Phone, saved.Phone);
        Assert.False(saved.BookingEnabled);
        Assert.False(saved.PreferModalOnMobile);
        Assert.Equal(preferred.MicrosoftBookingsEmbedUrl, saved.MicrosoftBookingsEmbedUrl);
        Assert.Equal(preferred.MetaPixelId, saved.MetaPixelId);
        Assert.Equal(preferred.MetaCapiAccessToken, saved.MetaCapiAccessToken);
        Assert.Equal(preferred.CalendarEmail, saved.CalendarEmail);
        Assert.Equal(preferred.DisplayOrder, saved.DisplayOrder);
        AssertRegistryUnselectedValues(selected, saved);
        foreach (var row in before.Values.Where(p => p.Id != selected.Id))
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(row),
                System.Text.Json.JsonSerializer.Serialize(after[row.Id]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AgentRegistryService_CancelsEachReadSaveAndTrackingStageWithoutLeavingStub(int stage)
    {
        var original = RegistryProfile("agent-oid", "owner@example.test");
        await using var fixture = await RegistryFixture.CreateAsync(original);
        await using var db = fixture.OpenContext();
        using var cancel = new CancellationTokenSource();
        var tracking = new Mock<IAgentTrackingService>(MockBehavior.Strict);
        tracking.Setup(service => service.EnsureProfileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string oid, string email, string name, CancellationToken token) =>
            {
                Assert.Equal(cancel.Token, token);
                if (stage == 4) cancel.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new AgentTrackingProfile { AgentUserId = oid, AgentUpn = email, DisplayName = name });
            });
        var service = new AgentRegistryService(db, NullLogger<AgentRegistryService>.Instance, tracking.Object);
        fixture.Commands.Begin(cancel, stage == 4 ? 0 : stage);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UpsertAgentProfileAsync(BuildUserWithEmail("agent-oid", "owner@example.test"), cancel.Token));

        fixture.Commands.Stop();
        Assert.Equal(Math.Min(stage, 3), fixture.Commands.Text.Count);
        Assert.All(fixture.Commands.Tokens, token => Assert.Equal(cancel.Token, token));
        tracking.Verify(s => s.EnsureProfileAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            stage == 4 ? Times.Once() : Times.Never());
        Assert.Empty(db.ChangeTracker.Entries<AgentProfile>());
        var saved = await db.AgentProfiles.FindAsync(original.Id);
        AssertRegistryUnselectedValues(original, saved!);
        if (stage <= 3)
        {
            // The interceptor cancels before dispatching the controlled write.
            Assert.Equal(original.UpdatedUtc, saved!.UpdatedUtc);
        }
        else
        {
            // Tracking cancellation happens after the profile save; it does not
            // establish rollback and must still propagate to the request owner.
            Assert.NotEqual(original.UpdatedUtc, saved!.UpdatedUtc);
        }
    }

    private static readonly DateTime RegistryFixtureTime = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly string[] RegistryUnselectedColumns =
    {
        "ProfileImageContent", "ProfileImageContentType", "IsVerified",
        "IsActive", "DeactivatedUtc", "DeactivationReason"
    };

    private static AgentProfile RegistryProfile(string oid, string email) => new()
    {
        AgentUserId = oid,
        AgentUpn = email,
        NormalizedEmail = email.ToLowerInvariant(),
        FullName = "Agent-managed name",
        ProfileImageContent = Enumerable.Repeat((byte)17, 64 * 1024).ToArray(),
        ProfileImageContentType = "image/png",
        IsVerified = true,
        IsActive = false,
        DeactivatedUtc = RegistryFixtureTime,
        DeactivationReason = "Fixture lifecycle state",
        CreatedUtc = RegistryFixtureTime,
        UpdatedUtc = RegistryFixtureTime
    };

    private static void AssertRegistryUnselectedValues(AgentProfile expected, AgentProfile actual)
    {
        Assert.Equal(expected.ProfileImageContent, actual.ProfileImageContent);
        Assert.Equal(expected.ProfileImageContentType, actual.ProfileImageContentType);
        Assert.Equal(expected.IsVerified, actual.IsVerified);
        Assert.Equal(expected.IsActive, actual.IsActive);
        Assert.Equal(expected.DeactivatedUtc, actual.DeactivatedUtc);
        Assert.Equal(expected.DeactivationReason, actual.DeactivationReason);
    }

    private sealed class RegistryCommands : DbCommandInterceptor
    {
        public List<string> Text { get; } = new();
        public List<CancellationToken> Tokens { get; } = new();
        private bool _record;
        private CancellationTokenSource? _cancel;
        private int _cancelAt;

        public void Begin(CancellationTokenSource? cancel = null, int cancelAt = 0)
        {
            Text.Clear();
            Tokens.Clear();
            _cancel = cancel;
            _cancelAt = cancelAt;
            _record = true;
        }

        public void Stop() => _record = false;

        private void Record(DbCommand command, CancellationToken token)
        {
            if (!_record) return;
            Text.Add(command.CommandText);
            Tokens.Add(token);
            if (Text.Count == _cancelAt) _cancel!.Cancel();
            token.ThrowIfCancellationRequested();
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command, cancellationToken);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command, cancellationToken);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class RegistryFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<MasterAppDbContext> _options;
        public RegistryCommands Commands { get; }

        private RegistryFixture(SqliteConnection connection, RegistryCommands commands)
        {
            _connection = connection;
            Commands = commands;
            _options = new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseSqlite(connection).AddInterceptors(commands).Options;
        }

        public MasterAppDbContext OpenContext() => new(_options);

        public static async Task<RegistryFixture> CreateAsync(params AgentProfile[] profiles)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var fixture = new RegistryFixture(connection, new RegistryCommands());
            await using var seed = fixture.OpenContext();
            await seed.Database.EnsureCreatedAsync();
            seed.AgentProfiles.AddRange(profiles);
            await seed.SaveChangesAsync();
            return fixture;
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // 1. AgentRegistryService_UpsertSetsNormalizedEmail
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AgentRegistryService_UpsertSetsNormalizedEmail()
    {
        using var db = BuildDb();
        var registry = BuildRegistry(db);
        var user = BuildUserWithEmail("oid-agent-1", "  Test.Agent@EXAMPLE.COM  ");

        await registry.UpsertAgentProfileAsync(user);

        var profile = await db.AgentProfiles.SingleAsync(x => x.AgentUserId == "oid-agent-1");
        Assert.Equal("test.agent@example.com", profile.NormalizedEmail);
    }

    // -----------------------------------------------------------------------
    // 2. AgentRegistryService_UpsertDoesNotCreateDuplicate
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AgentRegistryService_UpsertDoesNotCreateDuplicate()
    {
        using var db = BuildDb();
        var registry = BuildRegistry(db);
        var user = BuildUserWithEmail("oid-agent-2", "agent2@example.com");

        await registry.UpsertAgentProfileAsync(user);
        await registry.UpsertAgentProfileAsync(user);

        var count = await db.AgentProfiles.CountAsync(x => x.AgentUserId == "oid-agent-2");
        Assert.Equal(1, count);
    }

    // -----------------------------------------------------------------------
    // 2b. AgentRegistryService_ReusesExistingEmailMatchedProfile
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AgentRegistryService_ReusesExistingEmailMatchedProfile()
    {
        using var db = BuildDb();
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "legacy-oid",
            AgentUpn = "zac.owen@mylegnd.com",
            NormalizedEmail = "zac.owen@mylegnd.com",
            FullName = "Zac Owen",
            CreatedUtc = DateTime.UtcNow.AddDays(-3),
            UpdatedUtc = DateTime.UtcNow.AddDays(-3)
        });
        await db.SaveChangesAsync();

        var registry = BuildRegistry(db);
        var user = BuildUserWithEmail("current-oid", "  Zac.Owen@MyLegnd.com  ");

        await registry.UpsertAgentProfileAsync(user);

        var profiles = await db.AgentProfiles
            .Where(x => x.NormalizedEmail == "zac.owen@mylegnd.com")
            .ToListAsync();

        Assert.Single(profiles);
        Assert.Equal("legacy-oid", profiles[0].AgentUserId);
        Assert.Equal("Zac Owen", profiles[0].FullName);
    }

    [Fact]
    public async Task AccountController_ManageProfileKeepsAgentManagedNameAfterRegistryRefresh()
    {
        using var db = BuildDb();
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "agent-oid-managed-name",
            AgentUpn = "zac.owen@mylegnd.com",
            NormalizedEmail = "zac.owen@mylegnd.com",
            FullName = "Directory Display Name",
            BookingEnabled = true,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("oid", "agent-oid-managed-name"),
            new Claim("preferred_username", "zac.owen@mylegnd.com"),
            new Claim("name", "Directory Display Name")
        }, "TestAuth"));
        var http = new DefaultHttpContext { User = user };
        var controller = new AccountController(db,
            new AgentProfileAccessResolver(db),
            BuildActiveAccountLifecycle(),
            Mock.Of<IAgentTrackingService>(),
            new Infrastructure.WebsiteEditing.WebsiteEditorTicketProtector(new EphemeralDataProtectionProvider()),
            new Infrastructure.Analytics.AgentMarketingProfileService(db,
                new Infrastructure.Analytics.MarketingConnectionStore(db, new Infrastructure.Analytics.MarketingCredentialProtector(new EphemeralDataProtectionProvider())),
                new EphemeralDataProtectionProvider()))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>())
        };

        var result = await controller.ManageProfile(new ManageAgentProfileViewModel
        {
            FullName = "Zac Owen",
            Email = "zac.owen@mylegnd.com",
            Title = "CEO",
            Phone = "480-555-0100",
            ShortBio = "Founder",
            Npn = "1234567",
            BookingEnabled = false
        });

        Assert.Equal(nameof(AccountController.ManageProfile), Assert.IsType<RedirectToActionResult>(result).ActionName);

        // The authenticated-request registry runs before the redirected GET.
        // It must not replace an explicit profile setting with the Azure name claim.
        // Booking is now owned by Website Analytics > Marketing Setup, so an
        // ordinary profile save must preserve the existing canonical booking state.
        await BuildRegistry(db).UpsertAgentProfileAsync(user);

        var profile = await db.AgentProfiles.SingleAsync(x => x.AgentUserId == "agent-oid-managed-name");
        Assert.Equal("Zac Owen", profile.FullName);
        Assert.Equal("CEO", profile.Title);
        Assert.Equal("480-555-0100", profile.Phone);
        Assert.Equal("Founder", profile.ShortBio);
        Assert.Equal("1234567", profile.Npn);
        Assert.True(profile.BookingEnabled);
    }

    // -----------------------------------------------------------------------
    // 2c. AgentRegistryService_BackfillsMissingDataFromDuplicateSibling
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AgentRegistryService_BackfillsMissingDataFromDuplicateSibling()
    {
        using var db = BuildDb();
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "current-oid",
            AgentUpn = "zac.owen@mylegnd.com",
            NormalizedEmail = "zac.owen@mylegnd.com",
            FullName = "Zac Owen",
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            UpdatedUtc = DateTime.UtcNow.AddDays(-1)
        });
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "legacy-oid",
            AgentUpn = "zac.owen@mylegnd.com",
            FullName = "Zac Owen",
            BookingEnabled = true,
            MicrosoftBookingsEmbedUrl = "https://outlook.office.com/book/LEGEND@mylegnd.com/",
            FallbackBookingUrl = "https://outlook.office.com/book/LEGEND@mylegnd.com/",
            CalendarEmail = "zac.owen@mylegnd.com",
            CreatedUtc = DateTime.UtcNow.AddDays(-4),
            UpdatedUtc = DateTime.UtcNow.AddDays(-4)
        });
        await db.SaveChangesAsync();

        var registry = BuildRegistry(db);
        var user = BuildUserWithEmail("current-oid", "zac.owen@mylegnd.com");

        await registry.UpsertAgentProfileAsync(user);

        var current = await db.AgentProfiles.SingleAsync(x => x.AgentUserId == "current-oid");
        Assert.True(current.BookingEnabled);
        Assert.Equal("https://outlook.office.com/book/LEGEND@mylegnd.com/", current.MicrosoftBookingsEmbedUrl);
        Assert.Equal("zac.owen@mylegnd.com", current.CalendarEmail);
    }

    // -----------------------------------------------------------------------
    // 2d. AgentTrackingService_ReusesExistingUpnMatchedProfile
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AgentTrackingService_ReusesExistingUpnMatchedProfile()
    {
        using var db = BuildDb();
        db.AgentTrackingProfiles.Add(new AgentTrackingProfile
        {
            AgentUserId = "legacy-oid",
            AgentUpn = "zac.owen@mylegnd.com",
            Slug = "zac-owen",
            DisplayName = "Zac Owen",
            CreatedUtc = DateTime.UtcNow.AddDays(-4),
            UpdatedUtc = DateTime.UtcNow.AddDays(-4)
        });
        await db.SaveChangesAsync();

        var tracking = BuildTrackingService(db);
        var profile = await tracking.EnsureProfileAsync("current-oid", "Zac.Owen@MyLegnd.com", "Zac Owen");

        Assert.Equal("zac-owen", profile.Slug);
        Assert.Equal("legacy-oid", profile.AgentUserId);
        Assert.Equal(1, await db.AgentTrackingProfiles.CountAsync());
    }

    // -----------------------------------------------------------------------
    // 2e. AccountController_ManageProfileCreatesNormalizedEmail
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AccountController_ManageProfileCreatesNormalizedEmail()
    {
        using var db = BuildDb();
        var controller = new AccountController(db,
            new AgentProfileAccessResolver(db),
            BuildActiveAccountLifecycle(),
            Mock.Of<IAgentTrackingService>(),
            new Infrastructure.WebsiteEditing.WebsiteEditorTicketProtector(new EphemeralDataProtectionProvider()),
            new Infrastructure.Analytics.AgentMarketingProfileService(db,
                new Infrastructure.Analytics.MarketingConnectionStore(db, new Infrastructure.Analytics.MarketingCredentialProtector(new EphemeralDataProtectionProvider())),
                new EphemeralDataProtectionProvider()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildUserWithEmail("oid-agent-3", "  Zac.Owen@MyLegnd.com  ")
                }
            }
        };

        var result = await controller.ManageProfile();

        Assert.IsType<ViewResult>(result);
        var profile = db.AgentProfiles.Single(x => x.AgentUserId == "oid-agent-3");
        Assert.Equal("zac.owen@mylegnd.com", profile.NormalizedEmail);
    }

    [Fact]
    public async Task AccountController_ManageProfileReusesAzureSyncedProfileWhenObjectIdChanges()
    {
        using var db = BuildDb();
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "legacy-agent-oid",
            AgentUpn = "zac.owen@mylegnd.com",
            NormalizedEmail = "zac.owen@mylegnd.com",
            FullName = "Zac Owen",
            Title = "CEO",
            IsActive = true
        });
        await db.SaveChangesAsync();

        var controller = new AccountController(db,
            new AgentProfileAccessResolver(db),
            BuildActiveAccountLifecycle(),
            Mock.Of<IAgentTrackingService>(),
            new Infrastructure.WebsiteEditing.WebsiteEditorTicketProtector(new EphemeralDataProtectionProvider()),
            new Infrastructure.Analytics.AgentMarketingProfileService(db,
                new Infrastructure.Analytics.MarketingConnectionStore(db, new Infrastructure.Analytics.MarketingCredentialProtector(new EphemeralDataProtectionProvider())),
                new EphemeralDataProtectionProvider()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildUserWithEmail("current-azure-oid", "zac.owen@mylegnd.com")
                }
            }
        };

        var result = Assert.IsType<ViewResult>(await controller.ManageProfile());
        var model = Assert.IsType<ManageAgentProfileViewModel>(result.Model);

        Assert.Equal("CEO", model.Title);
        Assert.Equal(1, await db.AgentProfiles.CountAsync());
    }

    // -----------------------------------------------------------------------
    // 3. ClientProvisioningService_EnsureProfileSetsNormalizedEmail
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ClientProvisioningService_EnsureProfileSetsNormalizedEmail()
    {
        using var db = BuildDb();

        // Seed an AgentClient so the FK constraint is satisfied if present;
        // EnsureClientProfileAndLinkAsync creates both the profile and the link.
        var provisioning = BuildProvisioning(db);

        await provisioning.EnsureClientProfileAndLinkAsync(
            agentUserId: "agent-oid-1",
            clientUserId: "client-oid-1",
            firstName: "Jane",
            lastName: "Smith",
            email: "  Jane.Smith@EXAMPLE.COM  ",
            phone: null, dob: null, maritalStatus: null,
            soFirstName: null, soLastName: null, soDob: null, soEmail: null, soPhone: null);

        var profile = await db.ClientProfiles.SingleAsync(x => x.ClientUserId == "client-oid-1");
        Assert.Equal("jane.smith@example.com", profile.NormalizedEmail);
    }

    // -----------------------------------------------------------------------
    // 4. ClientProvisioningService_EnsureProfileBlocksDuplicateEmail
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ClientProvisioningService_EnsureProfileBlocksDuplicateEmail()
    {
        using var db = BuildDb();
        var provisioning = BuildProvisioning(db);

        // First provisioning — succeeds.
        await provisioning.EnsureClientProfileAndLinkAsync(
            agentUserId: "agent-oid-1",
            clientUserId: "client-oid-A",
            firstName: "Alice",
            lastName: "Jones",
            email: "alice@example.com",
            phone: null, dob: null, maritalStatus: null,
            soFirstName: null, soLastName: null, soDob: null, soEmail: null, soPhone: null);

        // Second provisioning — different ClientUserId, same email — must throw.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioning.EnsureClientProfileAndLinkAsync(
                agentUserId: "agent-oid-2",
                clientUserId: "client-oid-B",
                firstName: "Alice2",
                lastName: "Jones2",
                email: "alice@example.com",
                phone: null, dob: null, maritalStatus: null,
                soFirstName: null, soLastName: null, soDob: null, soEmail: null, soPhone: null));

        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // 5. AssistantPanelController_CreateSetsNormalizedEmail
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AssistantPanelController_CreateSetsNormalizedEmail()
    {
        using var db = BuildDb();
        var controller = BuildAssistantController(db, "agent-oid-1");

        var model = new AgentPortal.Models.CreateAssistantViewModel
        {
            FirstName = "Sam",
            LastName = "Helper",
            Email = "  Sam.HELPER@Example.com  "
        };

        // Graph call will fail (fake credentials) but is in a try/catch; the DB record is
        // written before the invite attempt, so it is present regardless.
        await controller.Create(model);

        var assistant = await db.AgentAssistants
            .FirstOrDefaultAsync(a => a.ParentAgentUserId == "agent-oid-1");

        Assert.NotNull(assistant);
        Assert.Equal("sam.helper@example.com", assistant!.NormalizedEmail);
        Assert.Equal("sam.helper@example.com", assistant.Email);
    }

    // -----------------------------------------------------------------------
    // 6. AssistantContextService_BindByNormalizedEmail
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AssistantContextService_BindByNormalizedEmail()
    {
        using var db = BuildDb();

        // Seed an unbound assistant record with NormalizedEmail set.
        db.AgentAssistants.Add(new AgentAssistant
        {
            Id = Guid.NewGuid(),
            ParentAgentUserId = "parent-agent-oid",
            AssistantUserId = null,           // not yet bound
            FirstName = "Kim",
            LastName = "Assist",
            Email = "kim@example.com",
            NormalizedEmail = "kim@example.com",
            IsActive = true,
            InvitedAt = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = new AssistantContextService(db, NullLogger<AssistantContextService>.Instance);

        // Simulate Kim's first login — OID comes in via claims, email matches NormalizedEmail.
        var user = BuildUserWithEmail("kim-oid-123", "KIM@example.com");

        var result = await service.BindAssistantOidIfNeededAsync(user);

        Assert.NotNull(result);
        Assert.Equal("kim-oid-123", result!.AssistantUserId);
    }

    // -----------------------------------------------------------------------
    // 7. OnboardingController_CreateInviteSetsNormalizedEmail
    // -----------------------------------------------------------------------
    [Fact]
    public async Task OnboardingController_CreateInviteSetsNormalizedEmail()
    {
        using var db = BuildDb();
        var emailSender = Mock.Of<IEmailSender>(s =>
            s.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()) ==
            Task.FromResult(true));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Onboarding:PublicBaseUrl", "https://portal.mylegnd.com")
            })
            .Build();
        var provisioning = BuildProvisioning(db);
        var http = new DefaultHttpContext();
        var tempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());

        var piiProtector = new PiiProtector(new ServiceCollection()
            .AddDataProtection().Services
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>());
        var controller = new OnboardingController(
            db, config, provisioning,
            NullLogger<OnboardingController>.Instance,
            emailSender, piiProtector)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = tempData
        };
        // Simulate the request URL so BuildOnboardingLink can fall back safely.
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("portal.mylegnd.com");

        var model = new AgentPortal.Models.OnboardingInviteInputModel
        {
            FirstName = "Tom",
            LastName = "Recruit",
            Email = "  Tom.Recruit@EXAMPLE.COM  ",
            RoleType = "Agent"
        };

        await controller.CreateInvite(model);

        var invite = await db.OnboardingInvites.SingleAsync();
        Assert.Equal("tom.recruit@example.com", invite.NormalizedEmail);
    }

    // -----------------------------------------------------------------------
    // 8. OnboardingController_DuplicateEmailBlockedIfActiveInviteExists
    // -----------------------------------------------------------------------
    [Fact]
    public async Task OnboardingController_DuplicateEmailBlockedIfActiveInviteExists()
    {
        using var db = BuildDb();

        // Seed an active "Invited" invite for this email.
        db.OnboardingInvites.Add(new OnboardingInvite
        {
            Id = Guid.NewGuid(),
            TokenHash = "existing-hash",
            FirstName = "Tom",
            LastName = "Recruit",
            Email = "tom.recruit@example.com",
            NormalizedEmail = "tom.recruit@example.com",
            RoleType = "Agent",
            Status = "Invited",
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddDays(7),
            CreatedBy = "owner@example.com"
        });
        await db.SaveChangesAsync();

        var emailSender = Mock.Of<IEmailSender>(s =>
            s.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()) ==
            Task.FromResult(true));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Onboarding:PublicBaseUrl", "https://portal.mylegnd.com")
            })
            .Build();
        var provisioning = BuildProvisioning(db);
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("portal.mylegnd.com");
        var tempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());

        var piiProtector = new PiiProtector(new ServiceCollection()
            .AddDataProtection().Services
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>());
        var controller = new OnboardingController(
            db, config, provisioning,
            NullLogger<OnboardingController>.Instance,
            emailSender, piiProtector)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = tempData
        };

        var model = new AgentPortal.Models.OnboardingInviteInputModel
        {
            FirstName = "Tom",
            LastName = "Recruit",
            Email = "Tom.Recruit@EXAMPLE.COM",
            RoleType = "Agent"
        };

        var result = await controller.CreateInvite(model);

        // Should return the Index view with a model error — not redirect.
        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.True(controller.ModelState.ContainsKey(nameof(model.Email)));

        // Only the original invite must remain — no second row created.
        var count = await db.OnboardingInvites.CountAsync();
        Assert.Equal(1, count);
    }
}
