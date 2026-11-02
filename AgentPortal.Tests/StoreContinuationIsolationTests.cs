using System;
using System.Threading.Tasks;
using ClientApp.Services;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Infrastructure.WebsiteEditing.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Infrastructure.Migrations;
using Xunit;

namespace AgentPortal.Tests;

public sealed class StoreContinuationIsolationTests
{
    [Theory]
    [InlineData(ClientIdentityContinuationPurpose.Activation)]
    [InlineData(ClientIdentityContinuationPurpose.SignIn)]
    [InlineData(ClientIdentityContinuationPurpose.TrainingBilling)]
    public async Task Existing_account_purposes_round_trip(ClientIdentityContinuationPurpose purpose)
    {
        await using var fixture = await Fixture.CreateAsync();
        var profile = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "synthetic-client" };
        fixture.Db.ClientProfiles.Add(profile);
        await fixture.Db.SaveChangesAsync();
        var (state, _) = await fixture.Service.CreateProtectedStateAsync(profile.Id, "test@example.invalid", "/profile", purpose);
        var result = await fixture.Service.ValidateProtectedStateAsync(state);
        Assert.True(result.Success);
        Assert.Equal(profile.Id, result.Continuation!.ClientProfileId);
        await fixture.Service.ConsumeAsync(result.Continuation);
        Assert.Equal("USED_CONTINUATION", (await fixture.Service.ValidateProtectedStateAsync(state)).SafeErrorCode);
    }

    [Theory]
    [InlineData(ClientIdentityContinuationPurpose.WebsiteEditor)]
    [InlineData(ClientIdentityContinuationPurpose.StoreManagement)]
    [InlineData(ClientIdentityContinuationPurpose.BusinessMembership)]
    [InlineData((ClientIdentityContinuationPurpose)99)]
    public async Task Account_issuer_rejects_other_purposes(ClientIdentityContinuationPurpose purpose)
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateProtectedStateAsync(
            Guid.NewGuid(), "test@example.invalid", "/profile", purpose));
        Assert.Empty(await fixture.Db.ClientIdentityContinuations.ToListAsync());
    }

    [Theory]
    [InlineData("legend", "global", false)]
    [InlineData("business", "synthetic-business", true)]
    [InlineData("protect", "synthetic-agent", false)]
    public async Task Profileless_store_binding_persists_but_cannot_be_used_for_account_signin(string site, string owner, bool hasProfile)
    {
        await using var fixture = await Fixture.CreateAsync();
        var business = new CommerceBusiness { Key = "synthetic-store" };
        fixture.Db.CommerceBusinesses.Add(business);
        var token = WebsiteEditorHandoffToken.Create();
        var continuation = Store(business.Id, token);
        if (hasProfile)
        {
            var profile = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "synthetic-owner" };
            fixture.Db.ClientProfiles.Add(profile);
            continuation.ClientProfileId = profile.Id;
        }
        continuation.WebsiteSiteKey = site;
        continuation.WebsiteOwnerKey = owner;
        fixture.Db.ClientIdentityContinuations.Add(continuation);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.ClientIdentityContinuations.SingleAsync();
        Assert.Equal(hasProfile, persisted.ClientProfileId.HasValue);
        Assert.Equal(owner, persisted.WebsiteOwnerKey);
        var state = fixture.Protection.CreateProtector("MasterApp.ClientApp.IdentityContinuation.v1").Protect(token);
        var result = await fixture.Service.ValidateProtectedStateAsync(state);
        Assert.False(result.Success);
        Assert.Equal("INVALID_CONTINUATION", result.SafeErrorCode);
        Assert.Null(persisted.ConsumedUtc);
    }

    [Theory]
    [InlineData("business")]
    [InlineData("actor")]
    [InlineData("origin")]
    [InlineData("site")]
    [InlineData("owner")]
    [InlineData("browser")]
    public async Task Database_rejects_incomplete_store_binding(string missing)
    {
        await using var fixture = await Fixture.CreateAsync();
        var business = new CommerceBusiness { Key = "synthetic-store" };
        fixture.Db.CommerceBusinesses.Add(business);
        await fixture.Db.SaveChangesAsync();
        var row = Store(business.Id, WebsiteEditorHandoffToken.Create());
        switch (missing)
        {
            case "business": row.CommerceBusinessId = null; break;
            case "actor": row.ActorUserId = null; break;
            case "origin": row.DestinationOrigin = null; break;
            case "site": row.WebsiteSiteKey = null; break;
            case "owner": row.WebsiteOwnerKey = null; break;
            case "browser": row.BrowserCorrelationHash = null; break;
        }
        fixture.Db.ClientIdentityContinuations.Add(row);
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Db.SaveChangesAsync());
    }

    [Theory]
    [InlineData(ClientIdentityContinuationPurpose.Activation)]
    [InlineData(ClientIdentityContinuationPurpose.SignIn)]
    [InlineData(ClientIdentityContinuationPurpose.WebsiteEditor)]
    [InlineData(ClientIdentityContinuationPurpose.TrainingBilling)]
    [InlineData((ClientIdentityContinuationPurpose)99)]
    public async Task Database_rejects_profileless_legacy_and_unknown_purpose(ClientIdentityContinuationPurpose purpose)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.ClientIdentityContinuations.Add(new ClientIdentityContinuation
        {
            Purpose = purpose, TokenHash = Guid.NewGuid().ToString(), ExpiresUtc = DateTime.UtcNow.AddMinutes(1)
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Empty_profile_cannot_be_issued_for_account_signin()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateProtectedStateAsync(
            Guid.Empty, "test@example.invalid", "/profile", ClientIdentityContinuationPurpose.SignIn));
        Assert.Empty(await fixture.Db.ClientIdentityContinuations.ToListAsync());
    }

    [Fact]
    public async Task Existing_editor_handoff_exchanges_once_and_rejects_account_signin()
    {
        await using var fixture = await Fixture.CreateAsync();
        var profile = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "synthetic-owner" };
        var business = new CommerceBusiness { Key = "synthetic-editor", IsActive = true, Status = "Active" };
        fixture.Db.AddRange(profile, business, new CommerceBusinessMember
        {
            CommerceBusinessId = business.Id, ClientProfileId = profile.Id,
            RoleKey = "owner", Status = "Active", CanManageStorefront = true
        });
        await fixture.Db.SaveChangesAsync();
        var handoff = await WebsiteEditorHandoffService.CreateAsync(fixture.Db, profile.Id, business.Id,
            profile.ClientUserId, "test@example.invalid");
        var protectedState = fixture.Protection.CreateProtector("MasterApp.ClientApp.IdentityContinuation.v1")
            .Protect(handoff.OpaqueState);
        Assert.False((await fixture.Service.ValidateProtectedStateAsync(protectedState)).Success);
        using var tickets = new WebsiteEditorTicketProtector(fixture.Protection);
        var controller = new WebsitePlatformController(fixture.Db, tickets, new ConfigurationBuilder().Build());
        Assert.IsType<OkObjectResult>(await controller.ExchangeHandoff(new(handoff.OpaqueState)));
        Assert.IsType<UnauthorizedResult>(await controller.ExchangeHandoff(new(handoff.OpaqueState)));
        fixture.Db.ChangeTracker.Clear();
        Assert.NotNull((await fixture.Db.ClientIdentityContinuations.SingleAsync()).ConsumedUtc);
    }

    [Fact]
    public async Task Existing_relational_rows_survive_canonical_migration()
    {
        await using var fixture = await Fixture.CreateAsync();
        var profile = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "synthetic-upgrade" };
        fixture.Db.ClientProfiles.Add(profile);
        await fixture.Db.SaveChangesAsync();
        await fixture.Db.Database.ExecuteSqlRawAsync("DROP TABLE ClientIdentityContinuations");
        await fixture.Db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE ClientIdentityContinuations (
              Id TEXT NOT NULL PRIMARY KEY, ClientProfileId TEXT NOT NULL,
              SubscriptionActivationInvitationId TEXT NULL, ClientSubscriptionId TEXT NULL,
              CommerceBusinessId TEXT NULL, ActorUserId TEXT NULL, ActorEmail TEXT NULL,
              Purpose TEXT NOT NULL, TokenHash TEXT NOT NULL, IntendedNormalizedEmail TEXT NOT NULL,
              ReturnUrl TEXT NOT NULL, ExpiresUtc TEXT NOT NULL, CreatedUtc TEXT NOT NULL,
              ConsumedUtc TEXT NULL, RowVersion BLOB NOT NULL DEFAULT X'',
              FOREIGN KEY (ClientProfileId) REFERENCES ClientProfiles(Id) ON DELETE CASCADE,
              FOREIGN KEY (CommerceBusinessId) REFERENCES CommerceBusinesses(Id) ON DELETE RESTRICT,
              FOREIGN KEY (SubscriptionActivationInvitationId) REFERENCES SubscriptionActivationInvitations(Id) ON DELETE SET NULL,
              FOREIGN KEY (ClientSubscriptionId) REFERENCES ClientSubscriptions(Id) ON DELETE SET NULL);
            CREATE UNIQUE INDEX IX_ClientIdentityContinuations_TokenHash ON ClientIdentityContinuations(TokenHash);
            """);
        var id = Guid.NewGuid();
        var expiry = DateTime.UtcNow.AddMinutes(10);
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ClientIdentityContinuations
            (Id,ClientProfileId,Purpose,TokenHash,IntendedNormalizedEmail,ReturnUrl,ExpiresUtc,CreatedUtc)
            VALUES ({id},{profile.Id},'SignIn','retained-hash','test@example.invalid','/profile',{expiry},{DateTime.UtcNow})
            """);
        var migration = new ScopedStoreContinuations { ActiveProvider = "Microsoft.EntityFrameworkCore.Sqlite" };
        var membershipMigration = new ScopedStoreMembershipInvitations { ActiveProvider = "Microsoft.EntityFrameworkCore.Sqlite" };
        var operations = new System.Collections.Generic.List<Microsoft.EntityFrameworkCore.Migrations.Operations.MigrationOperation>(migration.UpOperations);
        operations.AddRange(membershipMigration.UpOperations);
        var commands = fixture.Db.GetService<IMigrationsSqlGenerator>().Generate(operations,
            fixture.Db.GetService<IDesignTimeModel>().Model);
        foreach (var command in commands)
            await fixture.Db.Database.ExecuteSqlRawAsync(command.CommandText);
        fixture.Db.ChangeTracker.Clear();
        var retained = await fixture.Db.ClientIdentityContinuations.SingleAsync();
        Assert.Equal(id, retained.Id);
        Assert.Equal(profile.Id, retained.ClientProfileId);
        Assert.Equal("retained-hash", retained.TokenHash);
        Assert.Equal(expiry, retained.ExpiresUtc);
        Assert.Null(retained.ConsumedUtc);
        Assert.Null(retained.DestinationOrigin);
        var business = new CommerceBusiness { Key = "synthetic-upgraded-store" };
        fixture.Db.CommerceBusinesses.Add(business);
        fixture.Db.ClientIdentityContinuations.Add(Store(business.Id, WebsiteEditorHandoffToken.Create()));
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(2, await fixture.Db.ClientIdentityContinuations.CountAsync());
    }

    [Fact]
    public void Unsupported_rollback_cannot_drop_retained_bindings()
    {
        var migration = new ScopedStoreContinuations { ActiveProvider = "Microsoft.EntityFrameworkCore.Sqlite" };
        Assert.Throws<NotSupportedException>(() => _ = migration.DownOperations);
    }

    private static ClientIdentityContinuation Store(Guid businessId, string token) => new()
    {
        Purpose = ClientIdentityContinuationPurpose.StoreManagement, CommerceBusinessId = businessId,
        ActorUserId = "synthetic-actor", DestinationOrigin = "https://store.example.invalid",
        WebsiteSiteKey = "business", WebsiteOwnerKey = businessId.ToString(),
        BrowserCorrelationHash = WebsiteEditorHandoffToken.Hash("synthetic-browser"),
        TokenHash = WebsiteEditorHandoffToken.Hash(token), ReturnUrl = "/internal/dashboard",
        ExpiresUtc = DateTime.UtcNow.AddMinutes(2)
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        public MasterAppDbContext Db { get; private set; } = null!;
        public EphemeralDataProtectionProvider Protection { get; } = new();
        public ClientIdentityContinuationService Service => new(Db, Protection, new ClientAppReturnUrlNormalizer());
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture._connection.OpenAsync();
            fixture.Db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseSqlite(fixture._connection).Options);
            await fixture.Db.Database.EnsureCreatedAsync();
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
