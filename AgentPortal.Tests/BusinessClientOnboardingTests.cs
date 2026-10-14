using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClientApp.Infrastructure;
using ClientApp.Services;
using Domain.Accounts;
using Domain.Billing;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Billing;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Shared.Crm;
using Infrastructure.Analytics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using ProfileController = ClientApp.Controllers.ProfileController;

namespace AgentPortal.Tests;

public sealed class BusinessClientOnboardingTests
{
    [Theory]
    [InlineData(ClientSubscriptionStatus.Active, null, true)]
    [InlineData(ClientSubscriptionStatus.GracePeriod, 60, true)]
    [InlineData(ClientSubscriptionStatus.GracePeriod, 0, false)]
    [InlineData(ClientSubscriptionStatus.Canceled, 60, false)]
    [InlineData(null, null, false)]
    public async Task SubscriptionPolicy_SetupUsesActualFilterHandlerAndEntitlement(
        ClientSubscriptionStatus? status, int? graceMinutes, bool allowed)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db);
        if (status.HasValue)
        {
            db.ClientSubscriptions.Add(new ClientSubscription
            {
                ClientProfileId = profile.Id, Status = status.Value,
                GracePeriodEndsUtc = graceMinutes.HasValue ? DateTime.UtcNow.AddMinutes(graceMinutes.Value) : null
            });
            await db.SaveChangesAsync();
        }
        var context = FilterContext(profile, nameof(ProfileController.SetupBusinessWebsite));
        await ApplySubscriptionPolicyAsync(db, context);
        Assert.Equal(allowed, context.Result is null);
        if (!allowed) Assert.IsType<ForbidResult>(context.Result);
        Assert.Empty(db.CommerceBusinesses);
    }

    [Theory]
    [InlineData(nameof(ProfileController.BusinessWebsiteSession), false)]
    [InlineData(nameof(ProfileController.SaveBusinessEntityName), false)]
    [InlineData(nameof(ProfileController.BusinessOwners), false)]
    [InlineData(nameof(ProfileController.MyProfile), true)]
    [InlineData(nameof(ProfileController.Save), true)]
    [InlineData(nameof(ProfileController.AccountAccess), true)]
    [InlineData(nameof(ProfileController.ClientProfile), true)]
    public async Task SubscriptionPolicy_OnlyRecoveryActionsBypassWithoutSubscription(string action, bool allowed)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db);
        var context = FilterContext(profile, action);
        await ApplySubscriptionPolicyAsync(db, context);
        Assert.Equal(allowed, context.Result is null);
    }

    [Theory]
    [InlineData(nameof(ProfileController.SetupBusinessWebsite), false)]
    [InlineData(nameof(ProfileController.BusinessWebsiteSession), false)]
    [InlineData(nameof(ProfileController.SaveBusinessEntityName), false)]
    [InlineData(nameof(ProfileController.BusinessOwners), false)]
    [InlineData(nameof(ProfileController.MyProfile), true)]
    [InlineData(nameof(ProfileController.Save), true)]
    [InlineData(nameof(ProfileController.AccountAccess), true)]
    [InlineData(nameof(ProfileController.ClientProfile), true)]
    public async Task LifecyclePolicy_PersistedPauseBlocksBusinessActionsAndPreservesRecovery(string action, bool allowed)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db);
        var billing = new Mock<IBillingOrchestrator>();
        billing.Setup(x => x.PauseClientSubscriptionAsync(It.IsAny<AccountLifecycleSubscriptionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountLifecycleSubscriptionResult(true, null, "Paused.", null, null));
        var lifecycle = new AccountLifecycleService(db, billing.Object);
        var paused = await lifecycle.PauseAsync(new AccountLifecycleSubject(profile.ClientUserId,
            MessagingParticipantTypes.Client, profile.Id));
        Assert.True(paused.Succeeded);
        Assert.Single(db.AccountLifecycleRecords);
        var context = FilterContext(profile, action);
        await new ClientAccountLifecycleAuthorizeFilter(new EffectiveClientContextService(db), lifecycle)
            .OnAuthorizationAsync(context);
        Assert.Equal(allowed, context.Result is null);
        if (!allowed) Assert.IsType<ForbidResult>(context.Result);
    }

    [Theory]
    [InlineData("settings", true)]
    [InlineData("team", true)]
    [InlineData("crm", true)]
    [InlineData("catalog", false)]
    [InlineData("orders", false)]
    public async Task AccountRole_UsesExistingCapabilitiesAndOwnerEquivalentSettings(string capability, bool allowed)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db);
        var business = await AddAccountBusinessAsync(db, profile);
        var result = await BusinessWorkspaceAccess.ResolveAsync(db, business.Id, profile.Id,
            profile.ClientUserId, profile.Email, capability, CancellationToken.None);
        Assert.Equal(allowed, result is not null);
    }

    [Theory]
    [InlineData("team")]
    [InlineData("website")]
    [InlineData("analytics")]
    public async Task AccountRole_DoesNotOverrideCapabilityRevocation(string capability)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db);
        var business = await AddAccountBusinessAsync(db, profile);
        var member = await db.CommerceBusinessMembers.SingleAsync();
        member.CanManageTeam = false;
        member.CanManageStorefront = false;
        member.CanManageAnalytics = false;
        await db.SaveChangesAsync();
        Assert.Null(await BusinessWorkspaceAccess.ResolveAsync(db, business.Id, profile.Id,
            profile.ClientUserId, profile.Email, capability, CancellationToken.None));
    }

    [Fact]
    public async Task AccountRole_RejectsForeignProfileAndActorWithoutChangingTenantSettings()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var first = await AddProfileAsync(db);
        var second = await AddProfileAsync(db, "second");
        var firstBusiness = await AddAccountBusinessAsync(db, first);
        var secondBusiness = await AddAccountBusinessAsync(db, second);
        Assert.Null(await BusinessWorkspaceAccess.ResolveAsync(db, secondBusiness.Id, first.Id,
            first.ClientUserId, first.Email, "settings", CancellationToken.None));
        Assert.Null(await BusinessWorkspaceAccess.ResolveAsync(db, firstBusiness.Id, first.Id,
            second.ClientUserId, second.Email, "settings", CancellationToken.None));
        Assert.Equal("Original site", (await db.CommerceBusinessStorefrontSettings
            .SingleAsync(x => x.CommerceBusinessId == secondBusiness.Id)).BrandHeadline);
        Assert.Equal(2, await db.CommerceBusinesses.CountAsync());
    }

    [Theory]
    [InlineData("account")]
    [InlineData("owner")]
    public async Task AccountProvisioning_ReplayPreservesLegacyIdentitySettingsAndSubscription(string existingRole)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db);
        var business = await AddAccountBusinessAsync(db, profile, existingRole);
        var member = await db.CommerceBusinessMembers.SingleAsync();
        var settings = await db.CommerceBusinessStorefrontSettings.SingleAsync();
        var subscription = await db.CommerceBusinessSubscriptions.SingleAsync();
        var result = await new CommerceBusinessProvisioningService(db).CreateAsync(AccountRequest(profile) with
        {
            DisplayName = "Renamed input", LegalName = "Renamed legal input",
            Storefront = new CommerceBusinessStorefrontProvisioning("Replacement", "Replacement")
        });
        Assert.Equal(business.Id, result.Id);
        Assert.Equal("legacy-" + profile.Id.ToString("N"), result.Key);
        Assert.Equal("Original tenant", result.DisplayName);
        Assert.Equal(member.Id, Assert.Single(await db.CommerceBusinessMembers.ToListAsync()).Id);
        Assert.Equal(settings.Id, Assert.Single(await db.CommerceBusinessStorefrontSettings.ToListAsync()).Id);
        Assert.Equal("Original site", settings.BrandHeadline);
        Assert.Equal(subscription.Id, Assert.Single(await db.CommerceBusinessSubscriptions.ToListAsync()).Id);
        Assert.Equal("Original plan", subscription.PlanName);
        Assert.Single(await db.CommerceBusinesses.ToListAsync());
        Assert.Equal(existingRole, member.RoleKey);
    }

    [Theory]
    [InlineData("Revoked", true, true, true)]
    [InlineData("Active", false, true, true)]
    [InlineData("Active", true, false, true)]
    [InlineData("Active", true, true, false)]
    public async Task AccountProvisioning_UnavailableAssociationCannotCreateAnotherTenant(
        string memberStatus, bool activeBusiness, bool storefront, bool team)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db);
        var business = await AddAccountBusinessAsync(db, profile);
        var member = await db.CommerceBusinessMembers.SingleAsync();
        member.Status = memberStatus;
        member.CanManageStorefront = storefront;
        member.CanManageTeam = team;
        business.IsActive = activeBusiness;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CommerceBusinessProvisioningService(db).CreateAsync(AccountRequest(profile)));
        Assert.Single(db.CommerceBusinesses);
        Assert.Single(db.CommerceBusinessMembers);
        Assert.Single(db.CommerceBusinessStorefrontSettings);
        Assert.Single(db.CommerceBusinessSubscriptions);
    }

    [Fact]
    public async Task AccountProvisioning_RequiresPersistedBusinessProfileAndMatchingEmail()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db);
        var other = await AddProfileAsync(db, "other");
        var service = new CommerceBusinessProvisioningService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(AccountRequest(profile) with
            { OwnerClientProfileId = other.Id }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(AccountRequest(profile) with
            { OwnerClientProfileId = Guid.NewGuid() }));
        profile.CrmNotes = "{\"recordType\":\"Client\"}";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(AccountRequest(profile)));
        Assert.Empty(db.CommerceBusinesses);
    }

    [Fact]
    public async Task AccountProvisioning_ConflictingAssociationsFailClosed()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db);
        await AddAccountBusinessAsync(db, profile);
        var second = new CommerceBusiness { Key = "second-conflict", DisplayName = "Second" };
        db.Add(second);
        db.Add(new CommerceBusinessMember { CommerceBusinessId = second.Id, ClientProfileId = profile.Id,
            Email = profile.Email, NormalizedEmail = profile.Email.ToUpperInvariant(), RoleKey = "account" });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CommerceBusinessProvisioningService(db).CreateAsync(AccountRequest(profile)));
        Assert.Equal(2, db.CommerceBusinesses.Count());
    }

    [Fact]
    public async Task AccountProvisioning_InactiveStatusAndRevokedWorkspaceAccessFailClosed()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db); var business = await AddAccountBusinessAsync(db, profile);
        business.Status = "Inactive"; business.IsActive = true; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CommerceBusinessProvisioningService(db).CreateAsync(AccountRequest(profile)));
        Assert.Null(await BusinessWorkspaceAccess.ResolveAsync(db, business.Id, profile.Id,
            profile.ClientUserId, profile.Email, "settings", CancellationToken.None));
        business.Status = "Active";
        (await db.CommerceBusinessMembers.SingleAsync()).Status = "Revoked";
        await db.SaveChangesAsync();
        foreach (var capability in new[] { "settings", "team", "crm" })
            Assert.Null(await BusinessWorkspaceAccess.ResolveAsync(db, business.Id, profile.Id,
                profile.ClientUserId, profile.Email, capability, CancellationToken.None));
        Assert.Single(db.CommerceBusinesses); Assert.Single(db.CommerceBusinessMembers);
    }

    [Fact]
    public async Task AccountProvisioning_MissingAssociatedBusinessCannotCreateReplacement()
    {
        await using var db = ControllerTestHelpers.BuildDb(); var profile = await AddProfileAsync(db);
        db.Add(new CommerceBusinessMember { CommerceBusinessId = Guid.NewGuid(), ClientProfileId = profile.Id,
            Email = profile.Email, NormalizedEmail = profile.Email.ToUpperInvariant(), RoleKey = "account" });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CommerceBusinessProvisioningService(db).CreateAsync(AccountRequest(profile)));
        Assert.Empty(db.CommerceBusinesses); Assert.Single(db.CommerceBusinessMembers);
        Assert.Empty(db.CommerceBusinessStorefrontSettings); Assert.Empty(db.CommerceBusinessSubscriptions);
    }

    [Theory]
    [InlineData(2601, "IX_CommerceBusinesses_Key", true)]
    [InlineData(2627, "IX_CommerceBusinesses_Key", true)]
    [InlineData(2601, "IX_CommerceBusinessMembers_NormalizedEmail", false)]
    [InlineData(547, "IX_CommerceBusinesses_Key", false)]
    public async Task AccountProvisioning_OnlySqlBusinessKeyUniquenessBecomesRetryConflict(int number, string index, bool conflict)
    {
        var sqlError = (SqlError)typeof(SqlError).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(x => x.GetParameters().Length == 9).Invoke(new object?[]
                { number, (byte)1, (byte)14, "test", $"Duplicate key in index '{index}'.", "", 1, 0, null });
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(errors, [sqlError]);
        var sql = (SqlException)typeof(SqlException).GetMethod("CreateException", BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, [typeof(SqlErrorCollection), typeof(string)], modifiers: null)!.Invoke(null, [errors, "16.0"])!;
        var failure = new DbUpdateException("Fixture SQL failure", sql);
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new SqlFailureOnProvisioning(failure)).Options;
        await using var db = new MasterAppDbContext(options); var profile = await AddProfileAsync(db);
        var task = new CommerceBusinessProvisioningService(db).CreateAsync(AccountRequest(profile));
        if (conflict)
        {
            var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => task);
            Assert.Same(failure, exception.InnerException);
        }
        else Assert.Same(failure, await Assert.ThrowsAsync<DbUpdateException>(() => task));
        Assert.Contains(db.ChangeTracker.Entries<CommerceBusiness>(), x => x.State == EntityState.Added);
    }

    private sealed class SqlFailureOnProvisioning(DbUpdateException exception) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<CommerceBusiness>().Any(x => x.State == EntityState.Added)) throw exception;
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task AccountProvisioning_FreshCreationAndRenamedReplayKeepOriginalTenant()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db);
        var service = new CommerceBusinessProvisioningService(db);
        var first = await service.CreateAsync(AccountRequest(profile));
        Assert.Equal($"account-{profile.Id:N}", first.Key);
        var revision = (await db.CommerceBusinessStorefrontSettings.SingleAsync()).Revision;
        var replay = await service.CreateAsync(AccountRequest(profile) with
            { DisplayName = "Renamed request", LegalName = "Different legal", Key = "different-key" });
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal("New account tenant", replay.DisplayName);
        Assert.Equal(revision, (await db.CommerceBusinessStorefrontSettings.SingleAsync()).Revision);
        Assert.Single(db.CommerceBusinesses); Assert.Single(db.CommerceBusinessMembers);
        Assert.Single(db.CommerceBusinessStorefrontSettings); Assert.Single(db.CommerceBusinessSubscriptions);
    }

    [Fact]
    public async Task PersonalOwnerProvisioning_PreservesMultipleBusinesses()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db);
        profile.CrmNotes = "{\"recordType\":\"Client\"}";
        await db.SaveChangesAsync();
        var service = new CommerceBusinessProvisioningService(db);
        var first = await service.CreateAsync(AccountRequest(profile) with { OwnerRoleKey = "owner" });
        var second = await service.CreateAsync(AccountRequest(profile) with { OwnerRoleKey = "owner" });
        Assert.NotEqual(first.Id, second.Id); Assert.NotEqual(first.Key, second.Key);
        Assert.Equal(2, db.CommerceBusinesses.Count()); Assert.Equal(2, db.CommerceBusinessMembers.Count());
    }

    [Fact]
    public async Task AccountProvisioning_ReservedKeyWithoutMatchingMembershipFailsClosed()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db);
        db.Add(new CommerceBusiness { Key = $"account-{profile.Id:N}", DisplayName = "Reserved" });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CommerceBusinessProvisioningService(db).CreateAsync(AccountRequest(profile)));
        Assert.Single(db.CommerceBusinesses); Assert.Empty(db.CommerceBusinessMembers);
        Assert.Empty(db.CommerceBusinessStorefrontSettings); Assert.Empty(db.CommerceBusinessSubscriptions);
    }

    [Fact]
    public async Task AccountProvisioning_ConcurrentRequestsUseUniqueKeyAndFreshRetryReadsWinner()
    {
        var connectionString = $"Data Source=onboarding-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=False";
        await using var keeper = new SqliteConnection(connectionString); await keeper.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connectionString).Options;
        await using var seed = new MasterAppDbContext(options); await seed.Database.EnsureCreatedAsync();
        var profile = await AddProfileAsync(seed);
        var firstGate = new ProvisioningSaveGate(); var secondGate = new ProvisioningSaveGate();
        await using var firstDb = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connectionString).AddInterceptors(firstGate).Options);
        await using var secondDb = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connectionString).AddInterceptors(secondGate).Options);
        var firstTask = new CommerceBusinessProvisioningService(firstDb).CreateAsync(AccountRequest(profile));
        await firstGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondTask = new CommerceBusinessProvisioningService(secondDb).CreateAsync(AccountRequest(profile) with { DisplayName = "Losing request" });
        await secondGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        firstGate.Release.TrySetResult(); var winner = await firstTask;
        secondGate.Release.TrySetResult();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondTask);
        Assert.Contains(secondDb.ChangeTracker.Entries<CommerceBusiness>(), x => x.State == EntityState.Added);
        await using var readback = new MasterAppDbContext(options);
        Assert.Single(await readback.CommerceBusinesses.ToListAsync()); Assert.Single(await readback.CommerceBusinessMembers.ToListAsync());
        Assert.Single(await readback.CommerceBusinessStorefrontSettings.ToListAsync()); Assert.Single(await readback.CommerceBusinessSubscriptions.ToListAsync());
        var retry = await new CommerceBusinessProvisioningService(readback).CreateAsync(AccountRequest(profile) with { DisplayName = "Retry rename" });
        Assert.Equal(winner.Id, retry.Id); Assert.Equal(winner.Key, retry.Key); Assert.Equal(winner.DisplayName, retry.DisplayName);
        Assert.Single(await readback.CommerceBusinesses.ToListAsync());
    }

    [Fact]
    public async Task AccountProvisioning_RespectsCallerTransactionRollback()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options;
        await using var db = new MasterAppDbContext(options); await db.Database.EnsureCreatedAsync();
        var profile = await AddProfileAsync(db);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await new CommerceBusinessProvisioningService(db).CreateAsync(AccountRequest(profile));
            Assert.Single(await db.CommerceBusinesses.ToListAsync());
            await transaction.RollbackAsync();
        }
        await using var readback = new MasterAppDbContext(options);
        Assert.Single(await readback.ClientProfiles.ToListAsync());
        Assert.Empty(await readback.CommerceBusinesses.ToListAsync()); Assert.Empty(await readback.CommerceBusinessMembers.ToListAsync());
        Assert.Empty(await readback.CommerceBusinessStorefrontSettings.ToListAsync()); Assert.Empty(await readback.CommerceBusinessSubscriptions.ToListAsync());
    }

    [Fact]
    public async Task AccountSettings_CustomizePersistsOnlySelectedTenantAndRejectsStaleRevision()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var first = await AddProfileAsync(db); var second = await AddProfileAsync(db, "second-settings");
        var firstBusiness = await AddAccountBusinessAsync(db, first); var secondBusiness = await AddAccountBusinessAsync(db, second);
        var analytics = new Mock<IAnalyticsQueryService>(MockBehavior.Strict);
        var service = new BusinessWorkspaceService(db, analytics.Object);
        var before = await service.CustomizeAsync(firstBusiness, CancellationToken.None);
        var foreignBefore = await service.CustomizeAsync(secondBusiness, CancellationToken.None);
        var authorized = await BusinessWorkspaceAccess.ResolveAsync(db, firstBusiness.Id, first.Id,
            first.ClientUserId, first.Email, "settings", CancellationToken.None);
        Assert.NotNull(authorized);
        var input = new BusinessWorkspaceSettingsInput { Revision = before.SettingsRevision, LeadLabel = "Prospects",
            ClientLabel = "Customers", Stages = string.Join("\n", before.Preferences.Stages), Metrics = ["visitors", "leads"] };
        await service.CustomizeAsync(authorized!.Id, input, CancellationToken.None);
        var after = await service.CustomizeAsync(firstBusiness, CancellationToken.None);
        Assert.Equal("Prospects", after.Preferences.LeadLabel); Assert.Equal("Customers", after.Preferences.ClientLabel);
        Assert.NotEqual(before.SettingsRevision, after.SettingsRevision);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.CustomizeAsync(firstBusiness.Id, input, CancellationToken.None));
        Assert.Null(await BusinessWorkspaceAccess.ResolveAsync(db, secondBusiness.Id, first.Id,
            first.ClientUserId, first.Email, "settings", CancellationToken.None));
        var foreignAfter = await service.CustomizeAsync(secondBusiness, CancellationToken.None);
        Assert.Equal(foreignBefore.SettingsRevision, foreignAfter.SettingsRevision);
        Assert.Equal(foreignBefore.Preferences.Write(), foreignAfter.Preferences.Write());
        var persisted = await db.CommerceBusinessStorefrontSettings.AsNoTracking().SingleAsync(x => x.CommerceBusinessId == firstBusiness.Id);
        Assert.Equal("Prospects", BusinessWorkspacePreferences.Read(persisted.WorkspacePreferencesJson).LeadLabel);
        analytics.VerifyNoOtherCalls();
    }

    private sealed class ProvisioningSaveGate : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<CommerceBusiness>().Any(x => x.State == EntityState.Added))
            {
                Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    internal static CommerceBusinessProvisioningRequest AccountRequest(ClientProfile profile) => new(
        "New account tenant", "New account legal", "BusinessClient", profile.Email,
        OwnerRoleKey: "account", CanManageCatalog: false, CanManageOrders: false,
        OwnerClientProfileId: profile.Id,
        Subscription: new CommerceBusinessSubscriptionProvisioning("original", "Original plan", "Active", 1000, "Test"),
        Storefront: new CommerceBusinessStorefrontProvisioning("Original site", "Original description"));

    internal static async Task<ClientProfile> AddProfileAsync(MasterAppDbContext db, string suffix = "account")
    {
        var p = new ClientProfile { ClientUserId = "business-" + suffix, ExternalIdentityObjectId = "business-" + suffix,
            Email = suffix + "@example.test", NormalizedEmail = suffix + "@example.test",
            FirstName = "Business", LastName = "Account", CrmNotes = "{\"recordType\":\"BusinessClient\"}" };
        db.Add(p); await db.SaveChangesAsync(); return p;
    }

    private static async Task<CommerceBusiness> AddAccountBusinessAsync(MasterAppDbContext db, ClientProfile profile, string role = "account")
    {
        var b = new CommerceBusiness { Key = "legacy-" + profile.Id.ToString("N"), DisplayName = "Original tenant",
            LegalName = "Original legal", BusinessType = "BusinessClient", OwnerEmail = profile.Email };
        db.Add(b);
        db.Add(new CommerceBusinessMember { CommerceBusinessId = b.Id, ClientProfileId = profile.Id,
            Email = profile.Email, NormalizedEmail = profile.Email.ToUpperInvariant(), RoleKey = role,
            CanManageCatalog = false, CanManageOrders = false, DisplayName = "Account" });
        db.Add(new CommerceBusinessStorefrontSettings { CommerceBusinessId = b.Id, BrandHeadline = "Original site" });
        db.Add(new CommerceBusinessSubscription { CommerceBusinessId = b.Id, PlanName = "Original plan" });
        await db.SaveChangesAsync(); return b;
    }

    internal static AuthorizationFilterContext FilterContext(ClientProfile profile, string action)
    {
        var method = typeof(ProfileController).GetMethod(action)!;
        var metadata = typeof(ProfileController).GetCustomAttributes(true).Concat(method.GetCustomAttributes(true)).ToArray();
        var http = new DefaultHttpContext { User = ControllerTestHelpers.BuildUser(profile.ClientUserId, profile.Email) };
        http.Request.Path = "/profile/business-website/setup";
        http.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), action));
        var routes = new RouteData(); routes.Values["controller"] = "Profile"; routes.Values["action"] = action;
        return new AuthorizationFilterContext(new ActionContext(http, routes, new ControllerActionDescriptor
            { ActionName = action, ControllerName = "Profile", ControllerTypeInfo = typeof(ProfileController).GetTypeInfo(), MethodInfo = method }), new List<IFilterMetadata>());
    }

    private static async Task ApplySubscriptionPolicyAsync(MasterAppDbContext db, AuthorizationFilterContext context)
    {
        var households = ControllerTestHelpers.BuildHouseholdMembershipService(db);
        var client = new EffectiveClientContextService(db, households);
        var accessor = new HttpContextAccessor { HttpContext = context.HttpContext };
        using var services = new ServiceCollection().AddLogging().AddAuthorization(o =>
            o.AddPolicy(ClientAppAuthorizationPolicies.ClientSubscriptionActive, p =>
                p.AddRequirements(new ClientSubscriptionActiveRequirement())))
            .AddSingleton<IAuthorizationHandler>(new ClientSubscriptionActiveHandler(accessor, client,
                new BillingEntitlementService(db, households))).BuildServiceProvider();
        await new ClientSubscriptionAuthorizeFilter(services.GetRequiredService<IAuthorizationService>(),
            new ClientAppReturnUrlNormalizer()).OnAuthorizationAsync(context);
    }
}
