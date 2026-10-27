using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Infrastructure.Data.Configurations;
using Infrastructure.Reception;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>Real relational transactions over the exact reception mapping. Full owner authorization remains covered by its existing tests.</summary>
public sealed class ReceptionistCoordinationTests
{
    [Fact]
    public async Task SharedContextSqliteCreatesSchemaWithNativeCaseSensitiveIdentity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var model = db.GetService<IDesignTimeModel>().Model;
        var marketing = model.FindEntityType(typeof(MarketingConnection))!;
        Assert.Equal("BINARY", marketing.FindProperty(nameof(MarketingConnection.ReceptionistIngressKey))!.GetCollation());
        Assert.Equal("BINARY", marketing.FindProperty(nameof(MarketingConnection.ReceptionistProviderProjectKey))!.GetCollation());
        db.AddRange(Account("Owner-A"), Account("owner-a"));
        await db.SaveChangesAsync();
        Assert.Single(await db.Set<ReceptionistBudgetAccount>().Where(x => x.ScopeKey == "Owner-A").ToListAsync());
        Assert.Single(await db.Set<ReceptionistBudgetAccount>().Where(x => x.ScopeKey == "owner-a").ToListAsync());
        db.Add(Account("Owner-A"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        static ReceptionistBudgetAccount Account(string scope) => new()
        { Kind = "owner", ScopeKey = scope, Currency = "USD", TimeZoneId = "UTC" };
    }

    [Fact]
    public void SharedContextSqlServerRetainsTheReviewedBinaryIdentityModel()
    {
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer("Server=localhost;Database=ReceptionistModelOnly;Integrated Security=true;TrustServerCertificate=true").Options);
        var model = db.GetService<IDesignTimeModel>().Model;
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);
        var expected = snapshot.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()
            .Where(property => property.GetCollation() == ReceptionistModelConfiguration.IdentityCollation)
            .Select(property => (Entity: entity.Name, Property: property.Name))).ToArray();
        Assert.NotEmpty(expected);
        foreach (var identity in expected)
            Assert.Equal(ReceptionistModelConfiguration.IdentityCollation,
                model.FindEntityType(identity.Entity)!.FindProperty(identity.Property)!.GetCollation());
        Assert.Equal(expected.Length, model.GetEntityTypes().Sum(entity => entity.GetProperties()
            .Count(property => property.GetCollation() == ReceptionistModelConfiguration.IdentityCollation)));
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task SettingsAreIsolatedVersionedAndCannotChangeAdsAuthority()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding);
        var b = await f.OwnerAsync(MarketingOwnerScope.Business(Guid.NewGuid()), "b", funding);
        await using (var db = f.Database())
        {
            var row = await db.MarketingConnections.SingleAsync(x => x.OwnerKey == a.Owner.Key);
            row.AdsAccessTokenCiphertext = "independent-ads-ciphertext";
            row.ProviderPermissionsJson = "{\"ads\":true}";
            row.DisconnectedUtc = f.Clock.Now.AddDays(-1);
            await db.SaveChangesAsync();
        }
        var admitted = await f.Service.ReserveAsync(f.Ingress(a, "pinned"));
        Assert.True(admitted.Reserved);
        Assert.False(admitted.MayAcceptProvider);
        var before = await f.SettingsAsync(a);
        var otherBefore = await f.SettingsAsync(b);
        await using var storeDb = f.Database();
        var store = new MarketingConnectionStore(storeDb, null!);
        var edited = await store.SaveReceptionistConfigurationAsync(a.Owner, a.Provider, before.ConnectionRevision,
            before.Configuration! with { BusinessPolicy = "Updated private owner policy", MaximumCallSeconds = before.Configuration!.MaximumCallSeconds + 60 }, "authorized-actor");
        Assert.Equal(before.AdmissionEnabled, edited.AdmissionEnabled);
        Assert.False(edited.BindingAccepted);
        Assert.NotEqual(before.ConfigurationId, edited.ConfigurationId);
        Assert.Equal(before.ConfigurationVersion + 1, edited.ConfigurationVersion);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => store.SaveReceptionistConfigurationAsync(
            a.Owner, a.Provider, before.ConnectionRevision, before.Configuration!, "authorized-actor"));
        var otherAfter = await f.SettingsAsync(b);
        Assert.Equal(otherBefore.ConnectionRevision, otherAfter.ConnectionRevision);
        Assert.Equal(otherBefore.ConfigurationId, otherAfter.ConfigurationId);
        Assert.Equal(otherBefore.ConfigurationSha256, otherAfter.ConfigurationSha256);
        Assert.Equal(otherBefore.AdmissionEnabled, otherAfter.AdmissionEnabled);
        Assert.Equal(otherBefore.Capabilities, otherAfter.Capabilities);
        var pinned = await f.Service.GetCallContextAsync(admitted.CallId!.Value);
        Assert.Equal(before.Configuration!.BusinessPolicy, pinned!.Configuration.BusinessPolicy);
        Assert.Equal(before.ConfigurationId, pinned.ConfigurationId);
        var ads = await storeDb.MarketingConnections.AsNoTracking().SingleAsync(x => x.OwnerKey == a.Owner.Key);
        Assert.Equal("independent-ads-ciphertext", ads.AdsAccessTokenCiphertext);
        Assert.Equal("{\"ads\":true}", ads.ProviderPermissionsJson);
        Assert.Equal(f.Clock.Now.AddDays(-1), ads.DisconnectedUtc);
        Assert.Empty(await f.Service.FindStopRequestsAsync(10));
    }

    [Fact]
    public async Task ConcurrentProjectClaimsHaveOneOwnerEvenWithDifferentIngressSecretsAndFundingAccounts()
    {
        await using var f = await Fixture.CreateAsync();
        var fundingA = await f.FundingAsync();
        var fundingB = await f.OpenAsync("funding", "independent-provider-funding", 40m, 80m);
        var a = await f.DraftOwnerAsync(MarketingOwnerScope.Founder, "route-a", projectKey: "exclusive-project");
        var b = await f.DraftOwnerAsync(MarketingOwnerScope.Business(Guid.NewGuid()), "route-b", projectKey: "exclusive-project");
        using var start = new ManualResetEventSlim(false);
        async Task<OwnerFixture?> AttemptAsync(OwnerFixture draft, Guid funding, string webhook)
        {
            start.Wait();
            try { return await f.AcceptDraftAsync(draft, funding, webhookReference: webhook); }
            catch (InvalidOperationException) { return null; }
            catch (DbUpdateException) { return null; }
        }
        var attemptA = Task.Run(() => AttemptAsync(a, fundingA, "secret-store:webhook-a"));
        var attemptB = Task.Run(() => AttemptAsync(b, fundingB, "secret-store:webhook-b"));
        start.Set();
        var attempts = await Task.WhenAll(attemptA, attemptB);
        var winner = Assert.Single(attempts.Where(x => x is not null))!;
        var loser = winner.ConnectionId == a.ConnectionId ? b : a;
        Assert.Null(await f.Service.GetIngressBindingAsync(loser.Provider, loser.Ingress));
        var admitted = await f.Service.ReserveAsync(f.Ingress(winner, "one-provider-session"));
        Assert.True(admitted.Reserved);
        var misrouted = await f.Service.ReserveAsync(f.Ingress(loser, "one-provider-session"));
        Assert.False(misrouted.Reserved); Assert.Null(misrouted.CallId); Assert.Equal("authenticated_binding_changed", misrouted.Reason);
        await using var db = f.Database();
        var claims = await db.MarketingConnections.AsNoTracking().Where(x => x.ReceptionistProviderProjectKey == "exclusive-project").ToListAsync();
        Assert.Equal(winner.ConnectionId, Assert.Single(claims).Id);
        Assert.Null((await db.MarketingConnections.AsNoTracking().SingleAsync(x => x.Id == loser.ConnectionId)).ReceptionistProviderProjectKey);
        Assert.Equal(winner.ConnectionId, (await db.Set<ReceptionistCall>().SingleAsync()).MarketingConnectionId);
        Assert.Equal(winner.AccountId, (await db.Set<ReceptionistCostReservation>().SingleAsync()).OwnerBudgetAccountId);
        Assert.Single(await db.Set<ReceptionistSessionDecision>().ToListAsync());
        // The database also refuses a competing claim if an application caller bypasses the ordinary acceptance check.
        await Assert.ThrowsAsync<SqliteException>(() => db.MarketingConnections.Where(x => x.Id == loser.ConnectionId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ReceptionistProviderProjectKey, "exclusive-project")));
    }

    [Fact]
    public async Task ExclusiveProjectClaimSurvivesInactiveEditsAndCannotMoveThroughOrdinaryAcceptance()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "route-a", funding);
        var original = (await f.Service.GetIngressBindingAsync(a.Provider, a.Ingress))!;
        var b = await f.DraftOwnerAsync(MarketingOwnerScope.Business(Guid.NewGuid()), "route-b", projectKey: a.ProjectKey);
        await using var db = f.Database();
        var store = new MarketingConnectionStore(db, null!);
        var settings = await f.SettingsAsync(a);
        settings = await store.SetReceptionistAdmissionAsync(a.Owner, a.Provider, settings.ConnectionRevision, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.AcceptDraftAsync(b, funding));
        settings = await store.SaveReceptionistConfigurationAsync(a.Owner, a.Provider, settings.ConnectionRevision,
            settings.Configuration! with { BusinessPolicy = "New inactive owner policy" }, "authorized-actor");
        Assert.False(settings.BindingAccepted); Assert.False(settings.AdmissionEnabled);
        Assert.Equal(original.ConfigurationId, (await f.Service.GetIngressBindingAsync(a.Provider, a.Ingress))!.ConfigurationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.AcceptDraftAsync(b, funding));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AcceptReceptionistBindingAsync(a.Owner, a.Provider,
            settings.ConnectionRevision, original.Binding with { ProviderProjectKey = "replacement-project" }, "verified-operator"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AcceptReceptionistBindingAsync(a.Owner, a.Provider,
            settings.ConnectionRevision, original.Binding with { ExclusiveProjectVerificationReceipt = "" }, "verified-operator"));
        var unchanged = await db.MarketingConnections.AsNoTracking().SingleAsync(x => x.Id == a.ConnectionId);
        Assert.Equal(a.ProjectKey, unchanged.ReceptionistProviderProjectKey); Assert.Equal(original.ConfigurationId, unchanged.ReceptionistActiveConfigurationId);
        settings = await store.AcceptReceptionistBindingAsync(a.Owner, a.Provider, settings.ConnectionRevision,
            original.Binding with { IngressKey = "rotated-owner-route", WebhookSecretReference = "secret-store:rotated-owner-webhook" }, "verified-operator");
        Assert.True(settings.BindingAccepted); Assert.False(settings.AdmissionEnabled);
        var rebound = (await f.Service.GetIngressBindingAsync(a.Provider, "rotated-owner-route"))!;
        Assert.Equal(a.ProjectKey, rebound.Binding.ProviderProjectKey); Assert.Equal(a.Owner, rebound.Owner);
        Assert.NotEqual(original.ConfigurationId, rebound.ConfigurationId);
    }

    [Fact]
    public async Task DurableProjectMismatchDisablesIngressAdmissionAndPinnedDialogue()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "route-a", funding);
        var call = (await f.Service.ReserveAsync(f.Ingress(a, "accepted"))).CallId!.Value;
        await f.Service.RecordProviderStateAsync(call, "accepted", "actual-acceptance");
        var lease = (await f.Service.AcquireSidebandLeaseAsync(call, "sideband-worker", TimeSpan.FromSeconds(20)))!;
        await using var db = f.Database();
        await db.MarketingConnections.Where(x => x.Id == a.ConnectionId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ReceptionistProviderProjectKey, "inconsistent-project"));
        Assert.Null(await f.Service.GetIngressBindingAsync(a.Provider, a.Ingress));
        Assert.Equal("authenticated_binding_changed", (await f.Service.ReserveAsync(f.Ingress(a, "new-delivery"))).Reason);
        Assert.False(await f.Service.ValidateSidebandLeaseAsync(call, lease.Token));
        Assert.Equal(call, Assert.Single(await f.Service.FindStopRequestsAsync(10)).CallId);
        var pinned = (await f.Service.GetCallContextAsync(call))!;
        Assert.Equal(a.ProjectKey, pinned.ProviderProjectKey); Assert.Equal(a.ProjectKey, pinned.Binding.ProviderProjectKey);
        Assert.Equal(a.Owner, pinned.Owner);
        Assert.False((await f.Service.ClaimActionAsync(call, "forbidden-project-drift-effect", ReceptionistCapabilities.CanonicalIntake,
            new string('a', 64), "caller-confirmed-facts", lease.Token)).MayExecute);
        Assert.True((await f.Service.ClaimProviderStopAsync(call, "watchdog")).MayExecute);
        var drain = (await f.Service.ConvertSidebandToObservationAsync(call, lease.WorkerId, lease.Token, TimeSpan.FromSeconds(5)))!;
        Assert.True(drain.ObservationOnly); Assert.True(await f.Service.ValidateObservationLeaseAsync(call, drain.Token));
        Assert.False(await f.Service.ValidateSidebandLeaseAsync(call, drain.Token));
        var settings = await f.SettingsAsync(a);
        Assert.False(settings.BindingAccepted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MarketingConnectionStore(db, null!)
            .SetReceptionistAdmissionAsync(a.Owner, a.Provider, settings.ConnectionRevision, true));
        Assert.Single(await db.Set<ReceptionistCall>().ToListAsync());
        Assert.Single(await db.Set<ReceptionistSessionDecision>().ToListAsync());
    }

    [Fact]
    public async Task TwoOwnersCannotSpendTheSameSharedFundingAllowanceConcurrently()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync(cash: 2m, consumption: 3m);
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding);
        var b = await f.OwnerAsync(MarketingOwnerScope.Agent(Guid.NewGuid()), "b", funding);
        using var start = new ManualResetEventSlim(false);
        var attempts = new[] { a, b }.Select((owner, i) => Task.Run(async () =>
        { start.Wait(); return await f.Service.ReserveAsync(f.Ingress(owner, "parallel-" + i)); })).ToArray();
        start.Set();
        var results = await Task.WhenAll(attempts);
        Assert.Single(results.Where(x => x.Reserved));
        Assert.All(results, x => Assert.False(x.MayAcceptProvider));
        Assert.Single(results.Where(x => !x.Reserved && x.Reason.StartsWith("funding_", StringComparison.Ordinal)));
        await using var db = f.Database();
        Assert.Single(await db.Set<ReceptionistCall>().ToListAsync());
        Assert.Equal(2, await db.Set<ReceptionistSessionDecision>().CountAsync());
        Assert.Equal(2m, (await db.Set<ReceptionistCostReservation>().SingleAsync()).CashReserved);
    }

    [Fact]
    public async Task ProviderSessionDecisionAndDenialAreStableAcrossRetries()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding, Fixture.Configuration() with { MaximumConcurrentCalls = 1 });
        var ingress = f.Ingress(a, "same-session");
        var first = await f.Service.ReserveAsync(ingress);
        var restart = new ReceptionistAdmissionService(f.Options, f.Clock);
        var replay = await restart.ReserveAsync(ingress with { EventId = "a-later-delivery" });
        Assert.True(first.Reserved); Assert.True(replay.Reserved);
        Assert.False(first.MayAcceptProvider); Assert.False(replay.MayAcceptProvider); Assert.Equal(first.CallId, replay.CallId);
        var deniedIngress = f.Ingress(a, "denied-session");
        var denied = await restart.ReserveAsync(deniedIngress);
        Assert.Equal("owner_concurrency_limit", denied.Reason);
        await restart.RecordProviderStateAsync(first.CallId!.Value, "rejected", "verified-provider-rejection", f.Clock.Now);
        var stillDenied = await restart.ReserveAsync(deniedIngress with { EventId = "denied-redelivery" });
        Assert.False(stillDenied.MayAcceptProvider); Assert.Equal(denied.Reason, stillDenied.Reason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restart.ReserveAsync(f.Ingress(a, "other-session") with { EventId = ingress.EventId }));
        await using var db = f.Database();
        Assert.Equal(2, await db.Set<ReceptionistSessionDecision>().CountAsync());
        Assert.Equal(4, await db.Set<ReceptionistIngressEvent>().CountAsync());
    }

    [Fact]
    public async Task AuthenticatedIngressCannotFollowAReassignedRouteOrChangeItsProjectAuthority()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "shared-route", funding);
        var b = await f.OwnerAsync(MarketingOwnerScope.Business(Guid.NewGuid()), "b-route", funding);
        var verifiedA = f.Ingress(a, "verified-before-transfer");
        var existingA = await f.Service.ReserveAsync(f.Ingress(a, "existing-a-session"));
        Assert.True(existingA.Reserved);
        await f.RebindAsync(a, "a-new-route");
        b = await f.RebindAsync(b, "shared-route");
        var currentRoute = (await f.Service.GetIngressBindingAsync(b.Provider, b.Ingress))!;
        Assert.Equal(b.Owner, currentRoute.Owner);
        var stale = await f.Service.ReserveAsync(verifiedA);
        Assert.False(stale.Reserved); Assert.Null(stale.CallId); Assert.Equal("authenticated_binding_changed", stale.Reason);
        var staleReplay = await f.Service.ReserveAsync(f.Ingress(a, "existing-a-session"));
        Assert.Null(staleReplay.CallId); Assert.Equal("authenticated_binding_changed", staleReplay.Reason);
        var crossOwnerReplay = await f.Service.ReserveAsync(f.Ingress(b, "existing-a-session") with
            { EventId = "b-event-for-a-session", ProviderProjectKey = verifiedA.ProviderProjectKey });
        Assert.False(crossOwnerReplay.Reserved); Assert.Null(crossOwnerReplay.CallId); Assert.Equal("authenticated_binding_changed", crossOwnerReplay.Reason);
        await using (var db = f.Database())
        {
            Assert.Single(await db.Set<ReceptionistCall>().ToListAsync());
            Assert.Single(await db.Set<ReceptionistCostReservation>().ToListAsync());
            Assert.Single(await db.Set<ReceptionistSessionDecision>().ToListAsync());
            Assert.Single(await db.Set<ReceptionistIngressEvent>().ToListAsync());
        }
        var freshB = await f.Service.ReserveAsync(f.Ingress(b, "fresh-b-session"));
        Assert.True(freshB.Reserved);
        Assert.Equal(b.Owner, (await f.Service.GetCallContextAsync(freshB.CallId!.Value))!.Owner);
    }

    [Fact]
    public async Task SameOwnerBindingRotationInvalidatesPreviouslyAuthenticatedIngress()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "same-route", funding);
        var staleIngress = f.Ingress(a, "pending-delivery");
        var updated = await f.RebindAsync(a, a.Ingress, "secret-store:rotated-webhook");
        Assert.Equal(a.ConnectionId, updated.ConnectionId); Assert.NotEqual(a.ConfigurationId, updated.ConfigurationId);
        var stale = await f.Service.ReserveAsync(staleIngress);
        Assert.Equal("authenticated_binding_changed", stale.Reason); Assert.Null(stale.CallId);
        var forgedOwner = await f.Service.ReserveAsync(f.Ingress(updated, "pending-delivery") with { AuthenticatedOwnerKey = "different-owner" });
        Assert.Equal("authenticated_binding_changed", forgedOwner.Reason); Assert.Null(forgedOwner.CallId);
        await using (var db = f.Database())
        {
            Assert.Empty(await db.Set<ReceptionistCall>().ToListAsync());
            Assert.Empty(await db.Set<ReceptionistSessionDecision>().ToListAsync());
            Assert.Empty(await db.Set<ReceptionistIngressEvent>().ToListAsync());
            Assert.Empty(await db.Set<ReceptionistCostReservation>().ToListAsync());
        }
        Assert.True((await f.Service.ReserveAsync(f.Ingress(updated, "pending-delivery"))).Reserved);
    }

    [Fact]
    public async Task CashAndConsumptionAreSeparateAndPrepaidUsageDoesNotChargeCashAgain()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync(cash: 0m, consumption: 5m);
        var configuration = Fixture.Configuration() with { MonthlyCashLimit = 0m, MonthlyConsumptionLimit = 5m };
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding, configuration,
            new ReceptionistCostComponent("prepaid_usage", funding, 0m, 0m, 3m, 0m));
        var first = await f.Service.ReserveAsync(f.Ingress(a, "one"));
        Assert.True(first.Reserved);
        var second = await f.Service.ReserveAsync(f.Ingress(a, "two"));
        Assert.False(second.Reserved); Assert.Contains("consumption_limit", second.Reason);
        await using var db = f.Database();
        var reservation = await db.Set<ReceptionistCostReservation>().SingleAsync();
        Assert.Equal(0m, reservation.CashReserved); Assert.Equal(3m, reservation.ConsumptionReserved);
        Assert.All(await db.Set<ReceptionistBudgetPeriod>().ToListAsync(), x => Assert.Equal(0m, x.CashCommitted));
    }

    [Fact]
    public async Task ClosureAndFinalUsageCommitAtomicallyAndTheExactReceiptCanReplayAfterFailure()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding);
        var call = (await f.Service.ReserveAsync(f.Ingress(a, "atomic-closure"))).CallId!.Value;
        await f.Service.RecordProviderStateAsync(call, "accepted", "actual-provider-acceptance");
        await f.Service.ReconcileVoiceUsageAsync(call, "partial-duration", 4, false);
        f.Clock.Now = f.Clock.Now.AddSeconds(30);
        var observedClosed = f.Clock.Now;
        await using (var db = f.Database())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_atomic_closure BEFORE INSERT ON ReceptionistUsageReceipts WHEN NEW.ComponentKey = 'provider_closure' BEGIN SELECT RAISE(ABORT, 'synthetic receipt storage failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => f.Service.RecordProviderClosureAndUsageAsync(call, "authenticated-session-closed", observedClosed, 30));
        await using (var db = f.Database())
        {
            var state = await db.Set<ReceptionistCall>().SingleAsync();
            Assert.Equal("accepted", state.State); Assert.Null(state.ProviderClosedUtc); Assert.False(state.VoiceUsageFinal);
            Assert.Equal(4, state.ObservedVoiceSeconds);
            Assert.Equal(4, (await db.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.BudgetAccountId == a.AccountId)).VoiceSecondsCommitted);
            Assert.Equal(4, (await db.Set<ReceptionistVoiceUsagePosting>().SingleAsync()).CumulativeSeconds);
            Assert.Single(await db.Set<ReceptionistUsageReceipt>().ToListAsync());
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_atomic_closure;");
        }
        var restarted = new ReceptionistAdmissionService(f.Options, f.Clock);
        var result = await restarted.RecordProviderClosureAndUsageAsync(call, "authenticated-session-closed", observedClosed, 30);
        Assert.True(result.VoiceUsageFinal); Assert.False(result.VoiceAllocationUncertain);
        f.Clock.Now = f.Clock.Now.AddSeconds(15);
        var replay = await restarted.RecordProviderClosureAndUsageAsync(call, "authenticated-session-closed", f.Clock.Now, 30);
        Assert.Equal(result, replay);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RecordProviderClosureAndUsageAsync(call, "authenticated-session-closed", f.Clock.Now, 31));
        await using (var db = f.Database())
        {
            var state = await db.Set<ReceptionistCall>().SingleAsync();
            Assert.Equal("closed", state.State); Assert.Equal(observedClosed, state.ProviderClosedUtc); Assert.True(state.VoiceUsageFinal);
            Assert.Equal(30, state.ObservedVoiceSeconds);
            Assert.Equal(30, (await db.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.BudgetAccountId == a.AccountId)).VoiceSecondsCommitted);
            Assert.Equal(30, (await db.Set<ReceptionistVoiceUsagePosting>().SingleAsync()).CumulativeSeconds);
            Assert.Equal(2, await db.Set<ReceptionistUsageReceipt>().CountAsync());
            Assert.False((await db.Set<ReceptionistCostReservation>().SingleAsync()).CashFinal);
        }
    }

    [Fact]
    public async Task ClosureWithoutFinalDurationPersistsEvidenceAndKeepsUnknownMinutesReserved()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding,
            Fixture.Configuration() with { MonthlyVoiceSecondsLimit = 120 });
        var call = (await f.Service.ReserveAsync(f.Ingress(a, "unknown-usage"))).CallId!.Value;
        var result = await f.Service.RecordProviderClosureAndUsageAsync(call, "closure-without-duration", f.Clock.Now, null);
        Assert.False(result.VoiceUsageFinal); Assert.False(result.VoiceAllocationUncertain);
        await using (var db = f.Database())
        {
            var state = await db.Set<ReceptionistCall>().SingleAsync();
            Assert.Equal("closed", state.State); Assert.NotNull(state.ProviderClosedUtc); Assert.False(state.VoiceUsageFinal);
            Assert.Equal(0, state.ObservedVoiceSeconds); Assert.Equal(120, state.ReservedVoiceSeconds);
            Assert.Empty(await db.Set<ReceptionistVoiceUsagePosting>().ToListAsync());
            Assert.Single(await db.Set<ReceptionistUsageReceipt>().ToListAsync());
        }
        Assert.Equal("owner_voice_minutes_limit", (await f.Service.ReserveAsync(f.Ingress(a, "still-encumbered"))).Reason);
        var reconciled = await f.Service.RecordProviderClosureAndUsageAsync(call, "verified-later-duration", f.Clock.Now, 0);
        Assert.True(reconciled.VoiceUsageFinal);
        Assert.True((await f.Service.ReserveAsync(f.Ingress(a, "released-by-actual-final"))).Reserved);
    }

    [Fact]
    public async Task AtomicClosureWithBoundaryOverrunPersistsTheAggregateAndDurableAllocationHold()
    {
        await using var f = await Fixture.CreateAsync(new DateTime(2026, 1, 31, 23, 59, 58, DateTimeKind.Utc));
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding, Fixture.BoundaryConfiguration());
        var call = (await f.Service.ReserveAsync(f.Ingress(a, "boundary-atomic"))).CallId!.Value;
        var januaryEvidence = f.Clock.Now;
        await f.Service.RecordProviderStateAsync(call, "accepted", "actual-accepted");
        f.Clock.Now = new DateTime(2026, 2, 1, 0, 0, 1, DateTimeKind.Utc);
        var closed = await f.Service.RecordProviderClosureAndUsageAsync(call, "authenticated-boundary-closure", f.Clock.Now, 3);
        Assert.False(closed.VoiceUsageFinal); Assert.True(closed.VoiceAllocationUncertain);
        await using (var db = f.Database())
        {
            var state = await db.Set<ReceptionistCall>().SingleAsync();
            Assert.Equal("closed", state.State); Assert.Equal(f.Clock.Now, state.ProviderClosedUtc);
            Assert.Equal(3, state.ObservedVoiceSeconds); Assert.True(state.VoiceAllocationUncertain); Assert.False(state.VoiceUsageFinal);
            Assert.False((await db.Set<ReceptionistBudgetAccount>().SingleAsync(x => x.Id == a.AccountId)).AdmissionEnabled);
            Assert.Equal(0, (await db.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.BudgetAccountId == a.AccountId)).VoiceSecondsCommitted);
            Assert.Empty(await db.Set<ReceptionistVoiceUsagePosting>().ToListAsync());
            Assert.Equal("provider_closure", (await db.Set<ReceptionistUsageReceipt>().SingleAsync()).ComponentKey);
        }
        await f.OpenAsync("owner", a.Owner.Key, 40m, 80m, enable: false,
            reconciliationCutoff: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        var ownerState = (await f.Service.GetBudgetAccountAsync(a.AccountId))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.SetBudgetAdmissionAsync(a.AccountId, ownerState.Revision, true));
        await f.Service.ReconcileDatedVoiceUsageAsync(call, "verified-service-period-allocation", [new(januaryEvidence, 2), new(f.Clock.Now, 1)], 3);
        var replay = await f.Service.RecordProviderClosureAndUsageAsync(call, "authenticated-boundary-closure", f.Clock.Now, 3);
        Assert.True(replay.VoiceUsageFinal); Assert.False(replay.VoiceAllocationUncertain);
        await using (var db = f.Database())
        {
            Assert.Equal(1, (await db.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.BudgetAccountId == a.AccountId && x.StartUtc.Month == 2)).VoiceSecondsCommitted);
            Assert.False((await db.Set<ReceptionistBudgetAccount>().SingleAsync(x => x.Id == a.AccountId)).AdmissionEnabled);
        }
    }

    [Fact]
    public async Task CumulativeReceiptsAreIdempotentAndLowerSnapshotsCannotReleaseAllowance()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding);
        var call = (await f.Service.ReserveAsync(f.Ingress(a, "one"))).CallId!.Value;
        await f.Service.RecordProviderStateAsync(call, "accepted", "provider-accepted");
        await f.Service.ReconcileCostAsync(call, "provider_cost", "bill-1", new(f.Clock.Now, 1m, f.Clock.Now, 1.5m));
        await f.Service.ReconcileCostAsync(call, "provider_cost", "bill-1", new(f.Clock.Now, 1m, f.Clock.Now, 1.5m));
        await f.Service.ReconcileCostAsync(call, "provider_cost", "bill-stale", new(f.Clock.Now, .5m, f.Clock.Now, .75m));
        await f.Service.ReconcileVoiceUsageAsync(call, "duration-1", 30, false);
        await f.Service.ReconcileVoiceUsageAsync(call, "duration-1", 30, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReconcileCostAsync(call, "provider_cost", "bill-1", new(f.Clock.Now, 2m, f.Clock.Now, 2m)));
        await f.Service.RecordProviderStateAsync(call, "closed", "provider-closed", f.Clock.Now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReconcileCostAsync(call, "provider_cost", "bill-lower-final", new(f.Clock.Now, .5m, f.Clock.Now, .75m, .5m, .75m)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReconcileVoiceUsageAsync(call, "duration-lower-final", 20, true));
        await f.Service.ReconcileCostAsync(call, "provider_cost", "bill-final", new(f.Clock.Now, 2m, f.Clock.Now, 2m, 2m, 2m));
        await f.Service.ReconcileCostAsync(call, "provider_cost", "bill-final-redelivery", new(f.Clock.Now, 2m, f.Clock.Now, 2m, 2m, 2m));
        await f.Service.ReconcileVoiceUsageAsync(call, "duration-final", 60, true);
        await using (var db = f.Database())
        {
            Assert.All(await db.Set<ReceptionistBudgetPeriod>().ToListAsync(), x => { Assert.Equal(2m, x.CashCommitted); Assert.Equal(2m, x.ConsumptionCommitted); });
            Assert.Equal(60, (await db.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.BudgetAccountId == a.AccountId)).VoiceSecondsCommitted);
        }
        var next = (await f.Service.ReserveAsync(f.Ingress(a, "two"))).CallId!.Value;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReconcileCostAsync(next, "provider_cost", "bill-1", new(f.Clock.Now, 1m, f.Clock.Now, 1.5m)));
    }

    [Fact]
    public async Task UnknownEffectsSurviveRestartAndMonthRolloverUntilVerified()
    {
        await using var f = await Fixture.CreateAsync(new DateTime(2026, 1, 31, 23, 59, 58, DateTimeKind.Utc));
        var funding = await f.FundingAsync(cash: 2m, consumption: 3m);
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding,
            Fixture.Configuration() with { MonthlyCashLimit = 2m, MonthlyConsumptionLimit = 3m });
        var call = (await f.Service.ReserveAsync(f.Ingress(a, "old-month"))).CallId!.Value;
        await f.Service.RecordProviderStateAsync(call, "accepted", "accepted-receipt");
        var lease = (await f.Service.AcquireSidebandLeaseAsync(call, "worker-a", TimeSpan.FromSeconds(10)))!;
        var claim = await f.Service.ClaimActionAsync(call, "confirmed-inquiry", ReceptionistCapabilities.CanonicalIntake,
            new string('a', 64), "caller-confirmed-facts", lease.Token);
        Assert.True(claim.MayExecute);
        var restarted = new ReceptionistAdmissionService(f.Options, f.Clock);
        var pending = await restarted.ClaimActionAsync(call, "confirmed-inquiry", ReceptionistCapabilities.CanonicalIntake,
            new string('a', 64), "caller-confirmed-facts", lease.Token);
        Assert.False(pending.MayExecute); Assert.Equal("pending", pending.State);
        await restarted.RecordActionOutcomeAsync(claim.ActionId!.Value, "uncertain", null, "transport-timeout");
        var uncertain = await restarted.ClaimActionAsync(call, "confirmed-inquiry", ReceptionistCapabilities.CanonicalIntake,
            new string('a', 64), "caller-confirmed-facts", lease.Token);
        Assert.False(uncertain.MayExecute); Assert.Equal("uncertain", uncertain.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ClaimActionAsync(call, "confirmed-inquiry", ReceptionistCapabilities.CanonicalIntake,
            new string('b', 64), "different-facts", lease.Token));
        await restarted.RecordProviderStateAsync(call, "closed", "closed-receipt", f.Clock.Now);
        f.Clock.Now = new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);
        await f.OpenAsync("funding", "shared-provider-account", 2m, 3m);
        await f.OpenAsync("owner", a.Owner.Key, 2m, 3m);
        a = await f.RebindAsync(a, a.Ingress);
        var blocked = await restarted.ReserveAsync(f.Ingress(a, "new-month-blocked"));
        Assert.False(blocked.Reserved); Assert.Contains("cash_limit", blocked.Reason);
        await restarted.ReconcileCostAsync(call, "provider_cost", "actual-zero-cost", new(null, 0m, null, 0m, 0m, 0m));
        await restarted.ReconcileVoiceUsageAsync(call, "actual-zero-duration", 0, true);
        Assert.True((await restarted.ReserveAsync(f.Ingress(a, "new-month-available"))).Reserved);
        await restarted.RecordActionOutcomeAsync(claim.ActionId.Value, "verified", "canonical-record-id", "independent-owner-readback");
        var verified = await restarted.ClaimActionAsync(call, "confirmed-inquiry", ReceptionistCapabilities.CanonicalIntake,
            new string('a', 64), "caller-confirmed-facts", lease.Token);
        Assert.False(verified.MayExecute); Assert.Equal("canonical-record-id", verified.ActualRecordReference);
    }

    [Fact]
    public async Task RebindingProviderCannotResetOwnerConcurrencyOrOldReservations()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var configuration = Fixture.Configuration() with { MaximumConcurrentCalls = 1 };
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding, configuration);
        var first = await f.Service.ReserveAsync(f.Ingress(a, "one"));
        await f.Service.RecordProviderStateAsync(first.CallId!.Value, "uncertain", "accept-timeout");
        var secondProvider = await f.OwnerAsync(a.Owner, "b", funding, configuration, provider: "another-provider");
        Assert.Equal(a.AccountId, secondProvider.AccountId);
        var denied = await f.Service.ReserveAsync(f.Ingress(secondProvider, "two"));
        Assert.Equal("owner_concurrency_limit", denied.Reason);
        await using var db = f.Database();
        Assert.Single(await db.Set<ReceptionistBudgetAccount>().Where(x => x.Kind == "owner").ToListAsync());
        Assert.Single(await db.Set<ReceptionistCostReservation>().ToListAsync());
    }

    [Fact]
    public async Task CurrentKillAndClosingTimeStopPinnedCallsWithoutReleasingUnknownUsage()
    {
        await using var f = await Fixture.CreateAsync(new DateTime(2026, 2, 3, 17, 59, 50, DateTimeKind.Utc));
        var funding = await f.FundingAsync();
        var configuration = Fixture.Configuration() with { Hours = [new(DayOfWeek.Tuesday, new TimeOnly(9, 0), new TimeOnly(18, 0))] };
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding, configuration);
        var first = await f.Service.ReserveAsync(f.Ingress(a, "deadline"));
        Assert.Equal(new DateTime(2026, 2, 3, 18, 0, 0, DateTimeKind.Utc), first.MustStopUtc);
        await f.Service.RecordProviderStateAsync(first.CallId!.Value, "accepted", "accepted");
        var lease = (await f.Service.AcquireSidebandLeaseAsync(first.CallId.Value, "worker-a", TimeSpan.FromSeconds(10)))!;
        var fundingState = (await f.Service.GetBudgetAccountAsync(funding))!;
        await f.Service.SetBudgetAdmissionAsync(funding, fundingState.Revision, false);
        Assert.Equal("kill_switch", Assert.Single(await f.Service.FindStopRequestsAsync(10)).Reason);
        Assert.False((await f.Service.ClaimActionAsync(first.CallId.Value, "inquiry", ReceptionistCapabilities.CanonicalIntake,
            new string('a', 64), "confirmed", lease.Token)).MayExecute);
        f.Clock.Now = first.MustStopUtc!.Value;
        Assert.Equal("deadline", Assert.Single(await f.Service.FindStopRequestsAsync(10)).Reason);
        await using var db = f.Database();
        Assert.False((await db.Set<ReceptionistCall>().SingleAsync()).VoiceUsageFinal);
        Assert.False((await db.Set<ReceptionistCostReservation>().SingleAsync()).ConsumptionFinal);
    }

    [Fact]
    public async Task UnacceptedPublicTrafficIsDurablyRefusedAndOpaqueProviderIdsAreCaseSensitive()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding, Fixture.Configuration() with { MaximumConcurrentCalls = 3 });
        Assert.Equal("controlled_acceptance_only", (await f.Service.ReserveAsync(f.Ingress(a, "public") with { ControlledTest = false })).Reason);
        Assert.True((await f.Service.ReserveAsync(f.Ingress(a, "Session-A"))).Reserved);
        Assert.True((await f.Service.ReserveAsync(f.Ingress(a, "session-a"))).Reserved);
        Assert.Equal("authenticated_binding_changed", (await f.Service.ReserveAsync(f.Ingress(a, "wrong-project") with { ProviderProjectKey = "foreign-project" })).Reason);
    }

    [Fact]
    public async Task UnexpectedNonCallChargesAreRecordedOnceAndPauseFurtherAdmission()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding);
        var receipt = new ReceptionistVerifiedAccountCharge(a.AccountId, funding, "rental-charge", "rental",
            "rental-receipt", f.Clock.Now, 1.25m, f.Clock.Now, 1.25m);
        await f.Service.ReconcileAccountChargeAsync(receipt);
        await f.Service.ReconcileAccountChargeAsync(receipt);
        Assert.False((await f.Service.GetBudgetAccountAsync(funding))!.AdmissionEnabled);
        Assert.False((await f.Service.ReserveAsync(f.Ingress(a, "after-charge"))).Reserved);
        await using var db = f.Database();
        Assert.Single(await db.Set<ReceptionistAccountCharge>().ToListAsync());
        Assert.All(await db.Set<ReceptionistBudgetPeriod>().ToListAsync(), x => Assert.Equal(1.25m, x.CashCommitted));
    }

    [Fact]
    public async Task RelationalEvidenceCannotMoveBetweenOwnersOrCascadeAway()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding);
        var b = await f.OwnerAsync(MarketingOwnerScope.Business(Guid.NewGuid()), "b", funding);
        var call = (await f.Service.ReserveAsync(f.Ingress(a, "one"))).CallId!.Value;
        await using (var db = f.Database())
        {
            var otherPeriodId = (await db.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.BudgetAccountId == b.AccountId)).Id;
            // Execute at the database boundary, beyond EF's additional immutable-key protection.
            await Assert.ThrowsAsync<SqliteException>(() => db.Set<ReceptionistCostReservation>().Where(x => x.CallId == call)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.OwnerBudgetAccountId, b.AccountId)
                    .SetProperty(x => x.OwnerBudgetPeriodId, otherPeriodId)));
        }
        await using (var db = f.Database())
        {
            db.Remove(await db.Set<ReceptionistCall>().SingleAsync(x => x.Id == call));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task DelayedCashIsChargedInItsDebitMonthWhileConsumptionKeepsItsServiceMonth()
    {
        await using var f = await Fixture.CreateAsync(new DateTime(2026, 1, 31, 23, 59, 58, DateTimeKind.Utc));
        var serviceDate = f.Clock.Now;
        // The same actual date can belong to different months in the two independently accepted calendars.
        var funding = await f.FundingAsync(2m, 3m, "America/Los_Angeles");
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding,
            Fixture.Configuration() with { MonthlyCashLimit = 2m, MonthlyConsumptionLimit = 3m });
        var call = (await f.Service.ReserveAsync(f.Ingress(a, "service-in-january"))).CallId!.Value;
        await f.Service.RecordProviderStateAsync(call, "closed", "actual-closed", f.Clock.Now);
        f.Clock.Now = new DateTime(2026, 2, 1, 0, 0, 1, DateTimeKind.Utc);
        await f.OpenAsync("owner", a.Owner.Key, 2m, 3m);
        Assert.False((await f.Service.ReserveAsync(f.Ingress(a, "before-debit"))).Reserved);
        var partial = new ReceptionistDatedCostObservation(f.Clock.Now, 1m, serviceDate, 1.5m);
        await f.Service.ReconcileCostAsync(call, "provider_cost", "dated-partial", partial);
        await f.Service.ReconcileCostAsync(call, "provider_cost", "dated-partial", partial);
        await f.Service.ReconcileCostAsync(call, "provider_cost", "dated-partial-redelivery", partial);
        await using (var db = f.Database())
        {
            var ownerPeriods = await db.Set<ReceptionistBudgetPeriod>().Where(x => x.BudgetAccountId == a.AccountId).OrderBy(x => x.StartUtc).ToListAsync();
            Assert.Equal(0m, ownerPeriods[0].CashCommitted); Assert.Equal(1.5m, ownerPeriods[0].ConsumptionCommitted);
            Assert.Equal(1m, ownerPeriods[1].CashCommitted); Assert.Equal(0m, ownerPeriods[1].ConsumptionCommitted);
            var providerPeriod = await db.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.BudgetAccountId == funding);
            Assert.Equal(1m, providerPeriod.CashCommitted); Assert.Equal(1.5m, providerPeriod.ConsumptionCommitted);
            var reservation = await db.Set<ReceptionistCostReservation>().SingleAsync();
            Assert.False(reservation.CashFinal); Assert.False(reservation.ConsumptionFinal);
            Assert.Equal(2m, reservation.CashReserved); Assert.Equal(1m, reservation.CashObserved);
            Assert.Equal(2, await db.Set<ReceptionistCostPosting>().CountAsync());
        }
        Assert.False((await f.Service.ReserveAsync(f.Ingress(a, "partial-still-encumbered"))).Reserved);
        await f.Service.ReconcileCostAsync(call, "provider_cost", "dated-final", new(f.Clock.Now, 2m, serviceDate, 1.5m, 2m, 1.5m));
        await f.Service.ReconcileVoiceUsageAsync(call, "dated-final-duration", 0, true);
        // Finality releases only unused reservation. The actual February cash remains spent.
        Assert.False((await f.Service.ReserveAsync(f.Ingress(a, "after-final-cash-spent"))).Reserved);
        await using var finalDb = f.Database();
        var february = await finalDb.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.BudgetAccountId == a.AccountId && x.StartUtc.Month == 2);
        Assert.Equal(2m, february.CashCommitted); Assert.Equal(0m, february.ConsumptionCommitted);
        Assert.All(await finalDb.Set<ReceptionistCostReservation>().ToListAsync(), x => { Assert.True(x.CashFinal); Assert.True(x.ConsumptionFinal); });
    }

    [Fact]
    public async Task AReceiptOverlappingReconciledOpeningCannotBeChargedAgainOrReleaseItsReservation()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding);
        var call = (await f.Service.ReserveAsync(f.Ingress(a, "one"))).CallId!.Value;
        await f.Service.RecordProviderStateAsync(call, "closed", "closed", f.Clock.Now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReconcileCostAsync(call, "provider_cost", "opening-overlap",
            new(f.Clock.Now.AddTicks(-1), 1m, f.Clock.Now, 1m, 1m, 1m)));
        await using var db = f.Database();
        Assert.Empty(await db.Set<ReceptionistCostPosting>().ToListAsync());
        Assert.Empty(await db.Set<ReceptionistUsageReceipt>().ToListAsync());
        var reservation = await db.Set<ReceptionistCostReservation>().SingleAsync();
        Assert.False(reservation.CashFinal); Assert.Equal(0m, reservation.CashObserved);
        Assert.All(await db.Set<ReceptionistBudgetPeriod>().ToListAsync(), x => Assert.Equal(0m, x.CashCommitted));
    }

    [Fact]
    public async Task NonCallCashAndConsumptionUseIndependentDatesAndUnknownAmountsKeepAdmissionPaused()
    {
        await using var f = await Fixture.CreateAsync(new DateTime(2026, 1, 31, 23, 59, 58, DateTimeKind.Utc));
        var serviceDate = f.Clock.Now;
        var funding = await f.FundingAsync(timeZoneId: "America/Los_Angeles");
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding);
        var serviceOnly = new ReceptionistVerifiedAccountCharge(a.AccountId, funding, "actual-provider-rental", "rental",
            "service-observation", null, 0m, serviceDate, 1.25m, ConsumptionFinal: true);
        await f.Service.ReconcileAccountChargeAsync(serviceOnly);
        var ownerState = (await f.Service.GetBudgetAccountAsync(a.AccountId))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.SetBudgetAdmissionAsync(a.AccountId, ownerState.Revision, true));
        f.Clock.Now = new DateTime(2026, 2, 1, 0, 0, 1, DateTimeKind.Utc);
        await f.OpenAsync("owner", a.Owner.Key, 40m, 80m, enable: false);
        var paid = serviceOnly with { ProviderReceiptKey = "actual-cash-debit", CashDebitedUtc = f.Clock.Now, CumulativeCash = 1.25m, CashFinal = true };
        await f.Service.ReconcileAccountChargeAsync(paid);
        await f.Service.ReconcileAccountChargeAsync(paid);
        await f.Service.ReconcileAccountChargeAsync(paid with { ProviderReceiptKey = "paid-redelivery" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReconcileAccountChargeAsync(
            paid with { ProviderReceiptKey = "stale-lower-final", CumulativeCash = .5m }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReconcileAccountChargeAsync(
            paid with { ProviderReceiptKey = "changed-actual-date", CashDebitedUtc = serviceDate }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReconcileAccountChargeAsync(
            paid with { ProviderChargeKey = "different-actual-charge", ProviderReceiptKey = "cash-opening-overlap", CashDebitedUtc = f.Clock.Now.AddTicks(-1) }));
        await using (var db = f.Database())
        {
            var charge = Assert.Single(await db.Set<ReceptionistAccountCharge>().ToListAsync());
            Assert.Equal(f.Clock.Now, charge.CashDebitedUtc); Assert.Equal(serviceDate, charge.ConsumptionIncurredUtc);
            Assert.NotEqual(charge.CashOwnerBudgetPeriodId, charge.ConsumptionOwnerBudgetPeriodId);
            Assert.Equal(charge.CashFundingBudgetPeriodId, charge.ConsumptionFundingBudgetPeriodId);
            Assert.True(charge.CashFinal); Assert.True(charge.ConsumptionFinal);
            var ownerPeriods = await db.Set<ReceptionistBudgetPeriod>().Where(x => x.BudgetAccountId == a.AccountId).OrderBy(x => x.StartUtc).ToListAsync();
            Assert.Equal(0m, ownerPeriods[0].CashCommitted); Assert.Equal(1.25m, ownerPeriods[0].ConsumptionCommitted);
            Assert.Equal(1.25m, ownerPeriods[1].CashCommitted); Assert.Equal(0m, ownerPeriods[1].ConsumptionCommitted);
            var providerPeriod = await db.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.BudgetAccountId == funding);
            Assert.Equal(1.25m, providerPeriod.CashCommitted); Assert.Equal(1.25m, providerPeriod.ConsumptionCommitted);
        }
        ownerState = (await f.Service.GetBudgetAccountAsync(a.AccountId))!;
        await f.Service.SetBudgetAdmissionAsync(a.AccountId, ownerState.Revision, true);
        var fundingState = (await f.Service.GetBudgetAccountAsync(funding))!;
        await f.Service.SetBudgetAdmissionAsync(funding, fundingState.Revision, true);
        Assert.True((await f.Service.ReserveAsync(f.Ingress(a, "after-actual-reconciliation"))).Reserved);
    }

    [Fact]
    public async Task ARealMonthBoundaryOverrunKeepsMinutesPausedUntilEveryServicePeriodIsVerified()
    {
        await using var f = await Fixture.CreateAsync(new DateTime(2026, 1, 31, 23, 59, 58, DateTimeKind.Utc));
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding, Fixture.BoundaryConfiguration());
        var admitted = await f.Service.ReserveAsync(f.Ingress(a, "cross-month"));
        var call = admitted.CallId!.Value;
        var midnight = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(midnight, admitted.MustStopUtc);
        await f.Service.RecordProviderStateAsync(call, "accepted", "actual-accepted");
        f.Clock.Now = f.Clock.Now.AddSeconds(1);
        var januaryEvidence = f.Clock.Now;
        await f.Service.ReconcileVoiceUsageAsync(call, "january-partial", 1, false);
        f.Clock.Now = midnight.AddSeconds(1);
        await f.Service.RecordProviderStateAsync(call, "closed", "actual-late-closure", f.Clock.Now);
        await f.OpenAsync("owner", a.Owner.Key, 40m, 80m, enable: false, reconciliationCutoff: midnight);
        await f.OpenAsync("funding", "shared-provider-account", 40m, 80m, reconciliationCutoff: midnight);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReconcileVoiceUsageAsync(call, "aggregate-final-without-split", 3, true));
        // The refusal occurs after committing the hold and the unallocated aggregate receipt.
        await using (var db = f.Database())
        {
            var row = await db.Set<ReceptionistCall>().SingleAsync();
            Assert.True(row.VoiceAllocationUncertain); Assert.False(row.VoiceUsageFinal); Assert.Equal(3, row.ObservedVoiceSeconds);
            Assert.False((await db.Set<ReceptionistBudgetAccount>().SingleAsync(x => x.Id == a.AccountId)).AdmissionEnabled);
            Assert.Equal(1, (await db.Set<ReceptionistVoiceUsagePosting>().SingleAsync()).CumulativeSeconds);
            Assert.Contains(await db.Set<ReceptionistUsageReceipt>().ToListAsync(), x => x.ComponentKey == "voice_duration_unallocated");
        }
        var paused = (await f.Service.GetBudgetAccountAsync(a.AccountId))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.SetBudgetAdmissionAsync(a.AccountId, paused.Revision, true));
        Assert.False((await f.Service.ReserveAsync(f.Ingress(a, "while-unallocated"))).Reserved);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReconcileDatedVoiceUsageAsync(call, "missing-february-proof",
            [new(januaryEvidence, 3)], 3));
        ReceptionistDatedVoiceUsage[] allocated = [new(januaryEvidence, 2), new(f.Clock.Now, 1)];
        await f.Service.ReconcileDatedVoiceUsageAsync(call, "verified-period-allocation", allocated, 3);
        await f.Service.ReconcileDatedVoiceUsageAsync(call, "verified-period-allocation", allocated, 3);
        await f.Service.ReconcileDatedVoiceUsageAsync(call, "verified-allocation-redelivery", allocated, 3);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReconcileDatedVoiceUsageAsync(call, "lower-period-final",
            [new(januaryEvidence, 2), new(f.Clock.Now, 0)], 2));
        await using (var db = f.Database())
        {
            var row = await db.Set<ReceptionistCall>().SingleAsync();
            Assert.False(row.VoiceAllocationUncertain); Assert.True(row.VoiceUsageFinal); Assert.Equal(3, row.ObservedVoiceSeconds);
            var periods = await db.Set<ReceptionistBudgetPeriod>().Where(x => x.BudgetAccountId == a.AccountId).OrderBy(x => x.StartUtc).ToListAsync();
            Assert.Equal(2, periods[0].VoiceSecondsCommitted); Assert.Equal(1, periods[1].VoiceSecondsCommitted);
            Assert.Equal(2, await db.Set<ReceptionistVoiceUsagePosting>().CountAsync());
        }
        paused = (await f.Service.GetBudgetAccountAsync(a.AccountId))!;
        await f.Service.SetBudgetAdmissionAsync(a.AccountId, paused.Revision, true);
        // The unused reservation is released, while the actual February minute allowance stays consumed.
        Assert.Equal("owner_voice_minutes_limit", (await f.Service.ReserveAsync(f.Ingress(a, "after-verified-allocation"))).Reason);
    }

    [Fact]
    public async Task AdmissionDetectsUnclosedBoundaryUsageBeforeAnyFinalReceiptArrives()
    {
        await using var f = await Fixture.CreateAsync(new DateTime(2026, 1, 31, 23, 59, 57, DateTimeKind.Utc));
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding, Fixture.BoundaryConfiguration());
        var admitted = await f.Service.ReserveAsync(f.Ingress(a, "still-unclosed"));
        await f.Service.RecordProviderStateAsync(admitted.CallId!.Value, "accepted", "actual-accepted");
        f.Clock.Now = new DateTime(2026, 1, 31, 23, 59, 59, DateTimeKind.Utc);
        await f.Service.ReconcileVoiceUsageAsync(admitted.CallId.Value, "aggregate-before-boundary", 3, false);
        f.Clock.Now = new DateTime(2026, 2, 1, 0, 0, 1, DateTimeKind.Utc);
        await f.OpenAsync("owner", a.Owner.Key, 40m, 80m, reconciliationCutoff: f.Clock.Now.Date);
        await f.OpenAsync("funding", "shared-provider-account", 40m, 80m, reconciliationCutoff: f.Clock.Now.Date);
        var next = await f.Service.ReserveAsync(f.Ingress(a, "new-month-cannot-spend-unverified-minutes"));
        Assert.Equal("voice_allocation_unreconciled", next.Reason); Assert.False(next.Reserved);
        await using var db = f.Database();
        var row = await db.Set<ReceptionistCall>().SingleAsync();
        Assert.True(row.VoiceAllocationUncertain); Assert.Null(row.ProviderClosedUtc); Assert.False(row.VoiceUsageFinal);
        Assert.False((await db.Set<ReceptionistBudgetAccount>().SingleAsync(x => x.Id == a.AccountId)).AdmissionEnabled);
    }

    [Fact]
    public async Task AProviderRevisionCannotKeepOldFinalityOrReenableAnUnboundedCostReservation()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding);
        var call = (await f.Service.ReserveAsync(f.Ingress(a, "revised-bill"))).CallId!.Value;
        await f.Service.RecordProviderStateAsync(call, "closed", "actual-closed", f.Clock.Now);
        await f.Service.ReconcileCostAsync(call, "provider_cost", "first-final", new(f.Clock.Now, 1m, f.Clock.Now, 1m, 1m, 1m));
        await f.Service.ReconcileCostAsync(call, "provider_cost", "higher-partial-revision", new(f.Clock.Now, 3m, f.Clock.Now, 4m));
        await using (var db = f.Database())
        {
            var reservation = await db.Set<ReceptionistCostReservation>().SingleAsync();
            Assert.False(reservation.CashFinal); Assert.False(reservation.ConsumptionFinal);
            Assert.Equal(3m, reservation.CashObserved); Assert.Equal(4m, reservation.ConsumptionObserved);
            Assert.All(await db.Set<ReceptionistBudgetPeriod>().ToListAsync(), x => { Assert.Equal(3m, x.CashCommitted); Assert.Equal(4m, x.ConsumptionCommitted); });
        }
        var account = (await f.Service.GetBudgetAccountAsync(funding))!;
        Assert.False(account.AdmissionEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.SetBudgetAdmissionAsync(funding, account.Revision, true));
        await f.Service.ReconcileCostAsync(call, "provider_cost", "revised-actual-final", new(f.Clock.Now, 3m, f.Clock.Now, 4m, 3m, 4m));
        Assert.False((await f.Service.GetBudgetAccountAsync(funding))!.AdmissionEnabled);
    }

    [Fact]
    public async Task ApprovedCeilingChangesPreserveOpeningFactsAndRequireCurrentRevision()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding);
        var call = (await f.Service.ReserveAsync(f.Ingress(a, "one"))).CallId!.Value;
        await f.Service.ReconcileCostAsync(call, "provider_cost", "partial", new(f.Clock.Now, 1m, f.Clock.Now, 1m));
        var before = (await f.Service.GetBudgetAccountAsync(a.AccountId))!;
        await f.Service.ApproveBudgetCeilingsAsync(a.AccountId, before.Revision, 1m, 2m, "authorized-lower-ceilings");
        var lower = (await f.Service.GetBudgetAccountAsync(a.AccountId))!;
        Assert.False(lower.AdmissionEnabled); Assert.NotEqual(before.ControlRevision, lower.ControlRevision);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => f.Service.ApproveBudgetCeilingsAsync(a.AccountId, before.Revision, 3m, 4m, "stale-approval"));
        await f.Service.ApproveBudgetCeilingsAsync(a.AccountId, lower.Revision, 3m, 4m, "authorized-higher-ceilings");
        Assert.False((await f.Service.GetBudgetAccountAsync(a.AccountId))!.AdmissionEnabled);
        await using var db = f.Database();
        var period = await db.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.BudgetAccountId == a.AccountId);
        Assert.Equal(40m, period.OpeningCashCeiling); Assert.Equal(80m, period.OpeningConsumptionCeiling);
        Assert.Equal(0m, period.OpeningCashCommitted); Assert.Equal(0m, period.OpeningConsumptionCommitted);
        Assert.Equal(3m, period.CashLimit); Assert.Equal(4m, period.ConsumptionLimit);
        Assert.Equal(1m, period.CashCommitted); Assert.Equal(1m, period.ConsumptionCommitted);
        Assert.Equal("authorized-higher-ceilings", period.CeilingApprovalReceipt);
        var reservation = await db.Set<ReceptionistCostReservation>().SingleAsync();
        Assert.Equal(2m, reservation.CashReserved); Assert.Equal(3m, reservation.ConsumptionReserved);
        Assert.False(reservation.CashFinal); Assert.False(reservation.ConsumptionFinal);
    }

    [Fact]
    public async Task ProviderAcceptWorkIsClaimedOnceAndLostResponsesRemainUncertainAfterRestart()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding);
        var admission = await f.Service.ReserveAsync(f.Ingress(a, "one"));
        Assert.True(admission.Reserved); Assert.False(admission.MayAcceptProvider);
        Assert.Equal(admission.CallId, Assert.Single(await f.Service.FindPendingProviderAcceptsAsync(10)).CallId);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(() =>
            f.Service.ClaimProviderAcceptAsync(admission.CallId!.Value, "worker-" + i))));
        var granted = Assert.Single(attempts.Where(x => x.MayExecute));
        Assert.NotNull(granted.AttemptId); Assert.Equal("uncertain", granted.State);
        Assert.All(attempts, x => Assert.Equal(granted.AttemptId, x.AttemptId));
        var restarted = new ReceptionistAdmissionService(f.Options, f.Clock);
        Assert.False((await restarted.ClaimProviderAcceptAsync(admission.CallId!.Value, "replacement-worker")).MayExecute);
        Assert.Empty(await restarted.FindPendingProviderAcceptsAsync(10));
        Assert.Equal(admission.CallId, Assert.Single(await restarted.FindUncertainProviderCallsAsync(10)).CallId);
        var pinned = (await restarted.GetCallContextAsync(admission.CallId.Value))!;
        Assert.Equal("application_services", pinned.Binding.BackendMode);
        Assert.Throws<ArgumentException>(() => ReceptionistConfigurationCodec.ValidateBinding(pinned.Binding with { BackendMode = "" }, pinned.Configuration));
        Assert.Throws<ArgumentException>(() => ReceptionistConfigurationCodec.ValidateBinding(pinned.Binding with { Voice = "" }, pinned.Configuration));
        var route = (await restarted.GetIngressBindingAsync(a.Provider, a.Ingress))!;
        Assert.Equal(a.Owner, route.Owner); Assert.Equal(pinned.ConfigurationId, route.ConfigurationId);
        Assert.Equal(pinned.Binding.WebhookSecretReference, route.Binding.WebhookSecretReference);
        Assert.Null(await restarted.GetIngressBindingAsync(a.Provider, "caller-invented-route"));
    }

    [Fact]
    public async Task SidebandLeasesFenceNewEffectsButClaimedEffectsCanFinishAfterDisconnect()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding);
        var call = (await f.Service.ReserveAsync(f.Ingress(a, "one"))).CallId!.Value;
        await f.Service.ClaimProviderAcceptAsync(call, "accept-worker");
        await f.Service.RecordProviderStateAsync(call, "accepted", "provider-accepted");
        var leases = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(() =>
            f.Service.AcquireSidebandLeaseAsync(call, "sideband-" + i, TimeSpan.FromSeconds(10)))));
        var first = Assert.Single(leases.Where(x => x is not null))!;
        Assert.True(await f.Service.ValidateSidebandLeaseAsync(call, first.Token));
        var action = await f.Service.ClaimActionAsync(call, "confirmed-1", ReceptionistCapabilities.CanonicalIntake,
            new string('a', 64), "confirmed", first.Token);
        Assert.True(action.MayExecute);
        f.Clock.Now = f.Clock.Now.AddSeconds(11);
        Assert.False(await f.Service.ValidateSidebandLeaseAsync(call, first.Token));
        Assert.Null(await f.Service.RenewSidebandLeaseAsync(call, first.WorkerId, first.Token, TimeSpan.FromSeconds(10)));
        Assert.False((await f.Service.ClaimActionAsync(call, "stale-new-effect", ReceptionistCapabilities.CanonicalIntake,
            new string('b', 64), "confirmed", first.Token)).MayExecute);
        var second = (await f.Service.AcquireSidebandLeaseAsync(call, "replacement-worker", TimeSpan.FromSeconds(10)))!;
        Assert.True(second.Fence > first.Fence); Assert.NotEqual(first.Token, second.Token);
        Assert.Null(await f.Service.AcquireSidebandLeaseAsync(call, "other-worker", TimeSpan.FromSeconds(10)));
        await f.Service.RecordActionOutcomeAsync(action.ActionId!.Value, "verified", "actual-record", "independent-readback");
        var replay = await f.Service.ClaimActionAsync(call, "confirmed-1", ReceptionistCapabilities.CanonicalIntake,
            new string('a', 64), "confirmed", second.Token);
        Assert.False(replay.MayExecute); Assert.Equal("actual-record", replay.ActualRecordReference);
        Assert.True((await f.Service.ClaimActionAsync(call, "confirmed-2", ReceptionistCapabilities.CanonicalIntake,
            new string('b', 64), "confirmed", second.Token)).MayExecute);
        await using var db = f.Database();
        var persisted = await db.Set<ReceptionistActionIntent>().SingleAsync(x => x.Id == action.ActionId);
        Assert.Equal(first.Token, persisted.ExecutionLeaseToken); Assert.Equal(first.Fence, persisted.ExecutionFence);
        Assert.Equal(first.WorkerId, persisted.WorkerId); Assert.NotEqual(Guid.Empty, persisted.AttemptId);
    }

    [Fact]
    public async Task ObservationUsesTheSameFenceAndCannotGrantDialogueOrBusinessEffects()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding);
        var admission = await f.Service.ReserveAsync(f.Ingress(a, "uncertain"));
        var call = admission.CallId!.Value;
        await f.Service.ClaimProviderAcceptAsync(call, "accept-worker");
        var observation = (await f.Service.AcquireObservationLeaseAsync(call, "observer-a", TimeSpan.FromSeconds(5)))!;
        Assert.True(observation.ObservationOnly); Assert.True(await f.Service.ValidateObservationLeaseAsync(call, observation.Token));
        Assert.False(await f.Service.ValidateSidebandLeaseAsync(call, observation.Token));
        Assert.Null(await f.Service.AcquireObservationLeaseAsync(call, "observer-b", TimeSpan.FromSeconds(5)));
        Assert.Null(await f.Service.AcquireSidebandLeaseAsync(call, "dialogue-worker", TimeSpan.FromSeconds(5)));
        Assert.False((await f.Service.ClaimActionAsync(call, "forbidden-observation-effect", ReceptionistCapabilities.CanonicalIntake,
            new string('a', 64), "confirmed", observation.Token)).MayExecute);
        Assert.Null(await f.Service.PromoteObservationLeaseAsync(call, observation.WorkerId, observation.Token, TimeSpan.FromSeconds(10)));
        f.Clock.Now = f.Clock.Now.AddSeconds(6);
        Assert.Null(await f.Service.RenewObservationLeaseAsync(call, observation.WorkerId, observation.Token, TimeSpan.FromSeconds(5)));
        Assert.Null(await f.Service.ConvertSidebandToObservationAsync(call, observation.WorkerId, observation.Token, TimeSpan.FromSeconds(5)));
        var replacement = (await f.Service.AcquireObservationLeaseAsync(call, "observer-b", TimeSpan.FromSeconds(10)))!;
        Assert.True(replacement.Fence > observation.Fence); Assert.NotEqual(replacement.Token, observation.Token);
        await f.Service.RecordProviderStateAsync(call, "accepted", "actual-accepted-event");
        Assert.False(await f.Service.ValidateSidebandLeaseAsync(call, replacement.Token));
        var dialogue = (await f.Service.PromoteObservationLeaseAsync(call, replacement.WorkerId, replacement.Token, TimeSpan.FromMinutes(5)))!;
        Assert.False(dialogue.ObservationOnly); Assert.True(dialogue.Fence > replacement.Fence); Assert.NotEqual(dialogue.Token, replacement.Token);
        Assert.False(await f.Service.ValidateObservationLeaseAsync(call, replacement.Token));
        Assert.True(await f.Service.ValidateSidebandLeaseAsync(call, dialogue.Token));
        // At the deadline, retain this receive socket under an observation role to obtain actual terminal receipts.
        f.Clock.Now = admission.MustStopUtc!.Value;
        Assert.False(await f.Service.ValidateSidebandLeaseAsync(call, dialogue.Token));
        var drain = (await f.Service.ConvertSidebandToObservationAsync(call, dialogue.WorkerId, dialogue.Token, TimeSpan.FromSeconds(5)))!;
        Assert.Equal(dialogue.Token, drain.Token); Assert.True(drain.ObservationOnly); Assert.True(drain.Fence > dialogue.Fence);
        Assert.True(await f.Service.ValidateObservationLeaseAsync(call, drain.Token));
        Assert.Null(await f.Service.PromoteObservationLeaseAsync(call, drain.WorkerId, drain.Token, TimeSpan.FromSeconds(5)));
        Assert.False((await f.Service.ClaimActionAsync(call, "forbidden-late-effect", ReceptionistCapabilities.CanonicalIntake,
            new string('a', 64), "confirmed", drain.Token)).MayExecute);
        Assert.NotNull(await f.Service.RenewObservationLeaseAsync(call, drain.WorkerId, drain.Token, TimeSpan.FromSeconds(5)));
        await f.Service.RecordProviderStateAsync(call, "closed", "actual-terminal-event", f.Clock.Now);
        Assert.False(await f.Service.ValidateObservationLeaseAsync(call, drain.Token));
        Assert.Null(await f.Service.RenewObservationLeaseAsync(call, drain.WorkerId, drain.Token, TimeSpan.FromSeconds(5)));
        await using var db = f.Database();
        Assert.False((await db.Set<ReceptionistCall>().SingleAsync()).VoiceUsageFinal);
        Assert.False((await db.Set<ReceptionistCostReservation>().SingleAsync()).CashFinal);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("owner")]
    [InlineData("funding")]
    public async Task DisablingAndReenablingCannotResurrectEarlierQueuedOrActiveCalls(string dimension)
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding,
            Fixture.Configuration() with { MaximumConcurrentCalls = 3 });
        var queued = (await f.Service.ReserveAsync(f.Ingress(a, "queued"))).CallId!.Value;
        var active = (await f.Service.ReserveAsync(f.Ingress(a, "active"))).CallId!.Value;
        await f.Service.RecordProviderStateAsync(active, "accepted", "provider-accepted");
        var lease = (await f.Service.AcquireSidebandLeaseAsync(active, "sideband-worker", TimeSpan.FromSeconds(30)))!;
        if (dimension == "connection")
        {
            await using var db = f.Database();
            var store = new MarketingConnectionStore(db, null!);
            var settings = await f.SettingsAsync(a);
            settings = await store.SetReceptionistAdmissionAsync(a.Owner, a.Provider, settings.ConnectionRevision, false);
            await store.SetReceptionistAdmissionAsync(a.Owner, a.Provider, settings.ConnectionRevision, true);
        }
        else
        {
            var accountId = dimension == "owner" ? a.AccountId : funding;
            var state = (await f.Service.GetBudgetAccountAsync(accountId))!;
            await f.Service.SetBudgetAdmissionAsync(accountId, state.Revision, false);
            state = (await f.Service.GetBudgetAccountAsync(accountId))!;
            await f.Service.SetBudgetAdmissionAsync(accountId, state.Revision, true);
        }
        Assert.False((await f.Service.ClaimProviderAcceptAsync(queued, "late-worker")).MayExecute);
        Assert.Null(await f.Service.RenewSidebandLeaseAsync(active, lease.WorkerId, lease.Token, TimeSpan.FromSeconds(30)));
        Assert.False(await f.Service.ValidateSidebandLeaseAsync(active, lease.Token));
        Assert.Equal(2, (await f.Service.FindStopRequestsAsync(10)).Count);
        Assert.True((await f.Service.ReserveAsync(f.Ingress(a, "new-generation"))).Reserved);
    }

    [Fact]
    public async Task FirstStopWorkAdvancesPastPreviouslyClaimedUnclosedCalls()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding,
            Fixture.Configuration() with { MaximumConcurrentCalls = 3 });
        for (var i = 0; i < 3; i++) Assert.True((await f.Service.ReserveAsync(f.Ingress(a, "stop-" + i))).Reserved);
        f.Clock.Now = f.Clock.Now.AddSeconds(121);
        var selected = new HashSet<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var next = Assert.Single(await f.Service.FindStopRequestsAsync(1));
            Assert.True(selected.Add(next.CallId));
            var claim = await f.Service.ClaimProviderStopAsync(next.CallId, "watchdog");
            Assert.True(claim.MayExecute);
            await f.Service.RecordActionOutcomeAsync(claim.ActionId!.Value, "uncertain", null, "stop-response-unconfirmed");
        }
        Assert.Empty(await f.Service.FindStopRequestsAsync(1));
        Assert.False((await f.Service.ClaimProviderStopAsync(selected.First(), "replacement-watchdog")).MayExecute);
        await using var db = f.Database();
        Assert.Equal(3, await db.Set<ReceptionistCall>().CountAsync(x => x.ProviderClosedUtc == null && !x.VoiceUsageFinal));
        Assert.Equal(3, await db.Set<ReceptionistCostReservation>().CountAsync(x => !x.CashFinal && !x.ConsumptionFinal));
        Assert.Equal(3, await db.Set<ReceptionistActionIntent>().CountAsync(x => x.CapabilityKey == "__provider_stop"));
    }

    [Fact]
    public async Task WorkScansExcludeHistoricalUnresolvedCallsAndCurrentlyOwnedObservations()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding,
            Fixture.Configuration() with { MaximumConcurrentCalls = 10 });
        var historical = (await f.Service.ReserveAsync(f.Ingress(a, "historical"))).CallId!.Value;
        await f.Service.ClaimProviderAcceptAsync(historical, "previous-worker");
        f.Clock.Now = f.Clock.Now.AddMinutes(5);
        var accepted = (await f.Service.ReserveAsync(f.Ingress(a, "recent-accepted"))).CallId!.Value;
        await f.Service.RecordProviderStateAsync(accepted, "accepted", "actual-acceptance");
        var uncertain = (await f.Service.ReserveAsync(f.Ingress(a, "recent-uncertain"))).CallId!.Value;
        await f.Service.ClaimProviderAcceptAsync(uncertain, "accept-worker");
        var pending = (await f.Service.ReserveAsync(f.Ingress(a, "recent-pending"))).CallId!.Value;
        f.Clock.Now = f.Clock.Now.AddSeconds(121);
        Assert.NotNull(await f.Service.AcquireObservationLeaseAsync(accepted, "current-observer", TimeSpan.FromSeconds(20)));
        var observations = await f.Service.FindObservationCandidatesAsync(10, TimeSpan.FromMinutes(2));
        Assert.Equal(2, observations.Count); Assert.Contains(observations, x => x.CallId == uncertain); Assert.Contains(observations, x => x.CallId == pending);
        Assert.DoesNotContain(observations, x => x.CallId == historical || x.CallId == accepted);
        Assert.Empty(await f.Service.FindUncertainProviderCallsAsync(10));
        Assert.Empty(await f.Service.FindPendingProviderAcceptsAsync(10));
        Assert.Empty(await f.Service.FindSidebandCandidatesAsync(10));
        var fresh = (await f.Service.ReserveAsync(f.Ingress(a, "fresh"))).CallId!.Value;
        await f.Service.ClaimProviderAcceptAsync(fresh, "fresh-worker");
        Assert.Equal(fresh, Assert.Single(await f.Service.FindUncertainProviderCallsAsync(1)).CallId);
        Assert.Equal(fresh, Assert.Single(await f.Service.FindObservationCandidatesAsync(1, TimeSpan.FromMinutes(2))).CallId);
        Assert.NotNull(await f.Service.AcquireObservationLeaseAsync(fresh, "fresh-observer", TimeSpan.FromSeconds(20)));
        Assert.Empty(await f.Service.FindUncertainProviderCallsAsync(10));
        await using var db = f.Database();
        var held = await db.Set<ReceptionistCall>().SingleAsync(x => x.Id == historical);
        Assert.Equal("uncertain", held.State); Assert.Null(held.ProviderClosedUtc); Assert.False(held.VoiceUsageFinal);
        var costs = await db.Set<ReceptionistCostReservation>().SingleAsync(x => x.CallId == historical);
        Assert.False(costs.CashFinal); Assert.False(costs.ConsumptionFinal);
    }

    [Fact]
    public async Task DatabaseRepresentsAnImmediateStopAtReservationWithoutInventingExtraProviderTime()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding);
        var admission = await f.Service.ReserveAsync(f.Ingress(a, "immediate-stop"));
        await using (var db = f.Database())
            await db.Set<ReceptionistCall>().Where(x => x.Id == admission.CallId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.MustStopUtc, f.Clock.Now));
        Assert.False((await f.Service.ClaimProviderAcceptAsync(admission.CallId!.Value, "late-accept-worker")).MayExecute);
        Assert.Equal(admission.CallId, Assert.Single(await f.Service.FindStopRequestsAsync(10)).CallId);
        Assert.Equal(admission.CallId, Assert.Single(await f.Service.FindObservationCandidatesAsync(10, TimeSpan.FromMinutes(2))).CallId);
        await using (var db = f.Database())
        {
            var call = await db.Set<ReceptionistCall>().SingleAsync();
            Assert.Equal(call.ReservedUtc, call.MustStopUtc); Assert.True(call.ReservedVoiceSeconds > 0);
            Assert.Null(call.ProviderAcceptAttemptId); Assert.Null(call.ProviderClosedUtc);
        }
    }

    [Fact]
    public async Task WatchdogStopRetriesRequireNewProviderEvidenceAndPreserveUncertainAttempts()
    {
        await using var f = await Fixture.CreateAsync();
        var funding = await f.FundingAsync();
        var a = await f.OwnerAsync(MarketingOwnerScope.Founder, "a", funding);
        var admission = await f.Service.ReserveAsync(f.Ingress(a, "one"));
        var call = admission.CallId!.Value;
        await f.Service.RecordProviderStateAsync(call, "accepted", "provider-accepted");
        Assert.False((await f.Service.ClaimProviderStopAsync(call, "watchdog")).MayExecute);
        f.Clock.Now = admission.MustStopUtc!.Value;
        var first = await f.Service.ClaimProviderStopAsync(call, "watchdog");
        Assert.True(first.MayExecute); Assert.NotNull(first.AttemptId);
        Assert.False((await f.Service.ClaimProviderStopAsync(call, "competing-watchdog")).MayExecute);
        await f.Service.RecordActionOutcomeAsync(first.ActionId!.Value, "uncertain", null, "stop-response-lost");
        Assert.False((await f.Service.ClaimProviderStopAsync(call, "replacement-watchdog",
            new("not-newer-observation", f.Clock.Now, true))).MayExecute);
        f.Clock.Now = f.Clock.Now.AddSeconds(1);
        var observation = new ReceptionistVerifiedProviderObservation("provider-still-active-readback", f.Clock.Now, true);
        var second = await f.Service.ClaimProviderStopAsync(call, "replacement-watchdog", observation);
        Assert.True(second.MayExecute); Assert.NotEqual(first.ActionId, second.ActionId); Assert.NotEqual(first.AttemptId, second.AttemptId);
        Assert.False((await f.Service.ClaimProviderStopAsync(call, "other-watchdog", observation)).MayExecute);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RecordActionOutcomeAsync(second.ActionId!.Value,
            "verified", "provider-session", "unverified-closure"));
        await f.Service.RecordProviderStateAsync(call, "closed", "provider-closed-readback", f.Clock.Now);
        await f.Service.RecordActionOutcomeAsync(second.ActionId!.Value, "verified", "provider-session", "provider-closed-readback");
        Assert.Empty(await f.Service.FindStopRequestsAsync(10));
        await using var db = f.Database();
        var attempts = await db.Set<ReceptionistActionIntent>().OrderBy(x => x.AttemptNumber).ToListAsync();
        Assert.Equal(2, attempts.Count); Assert.Equal("uncertain", attempts[0].State); Assert.Equal("verified", attempts[1].State);
        Assert.False((await db.Set<ReceptionistCostReservation>().SingleAsync()).CashFinal);
    }

    private sealed record OwnerFixture(MarketingOwnerScope Owner, string Provider, string Ingress, Guid AccountId,
        Guid ConnectionId, Guid ConfigurationId, string ProjectKey);

    private sealed class TestClock(DateTime now) : TimeProvider
    {
        internal DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "reception-tests-" + Guid.NewGuid().ToString("N") + ".sqlite");
        internal DbContextOptions<MasterAppDbContext> Options { get; private set; } = null!;
        internal TestClock Clock { get; private set; } = null!;
        internal ReceptionistAdmissionService Service => new(Options, Clock);
        internal MasterAppDbContext Database() => new(Options);

        internal static async Task<Fixture> CreateAsync(DateTime? now = null)
        {
            var fixture = new Fixture { Clock = new(now ?? new DateTime(2026, 2, 3, 12, 0, 0, DateTimeKind.Utc)) };
            var connection = "Data Source=" + fixture.path + ";Pooling=False;Default Timeout=20";
            await using var schema = new ReceptionSchemaContext(new DbContextOptionsBuilder().UseSqlite(connection).Options);
            fixture.Options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).UseModel(schema.Model).Options;
            await schema.Database.EnsureCreatedAsync();
            return fixture;
        }

        internal static ReceptionistBusinessConfiguration Configuration() => new()
        {
            BusinessName = "Owner-authored test business", ConversationInstructions = "Use the approved tenant instructions.",
            BusinessPolicy = "Use only confirmed facts and accepted capabilities.", TimeZoneId = "UTC", Currency = "USD",
            MonthlyCashLimit = 40m, MonthlyConsumptionLimit = 80m, MonthlyVoiceSecondsLimit = 10000,
            MaximumConcurrentCalls = 2, MaximumCallSeconds = 120, AdmissionWindowSeconds = 60, MaximumCallsPerWindow = 20,
            Hours = Enum.GetValues<DayOfWeek>().Select(day => new ReceptionistHoursWindow(day, TimeOnly.MinValue, new TimeOnly(23, 59, 59))).ToArray(),
            RequestedCapabilities = [ReceptionistCapabilities.CanonicalIntake]
        };

        internal static ReceptionistBusinessConfiguration BoundaryConfiguration() => Configuration() with
        {
            MaximumCallSeconds = 3, MonthlyVoiceSecondsLimit = 3, MaximumConcurrentCalls = 3,
            Hours = Enum.GetValues<DayOfWeek>().SelectMany(day => new ReceptionistHoursWindow[]
            {
                new(day, TimeOnly.MinValue, new TimeOnly(12, 0)),
                new(day, new TimeOnly(12, 0), TimeOnly.MinValue)
            }).ToArray()
        };

        internal Task<Guid> FundingAsync(decimal cash = 40m, decimal consumption = 80m, string timeZoneId = "UTC") => OpenAsync("funding", "shared-provider-account", cash, consumption, timeZoneId);

        internal async Task<Guid> OpenAsync(string kind, string scope, decimal cash, decimal consumption, string timeZoneId = "UTC", bool enable = true, DateTime? reconciliationCutoff = null)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(Clock.Now, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
            var id = await Service.OpenBudgetPeriodAsync(new(kind, scope, "USD", timeZoneId, local.Year, local.Month,
                cash, consumption, 0m, 0m, 0, "verified-opening:" + scope + ":" + local.ToString("yyyyMM"), reconciliationCutoff ?? Clock.Now.AddTicks(-1)));
            var state = (await Service.GetBudgetAccountAsync(id))!;
            if (enable && !state.AdmissionEnabled) await Service.SetBudgetAdmissionAsync(id, state.Revision, true);
            return id;
        }

        internal async Task<OwnerFixture> OwnerAsync(MarketingOwnerScope owner, string ingress, Guid funding,
            ReceptionistBusinessConfiguration? configuration = null, ReceptionistCostComponent? component = null, string provider = "openai")
        {
            var draft = await DraftOwnerAsync(owner, ingress, configuration, provider);
            return await AcceptDraftAsync(draft, funding, component);
        }

        internal async Task<OwnerFixture> DraftOwnerAsync(MarketingOwnerScope owner, string ingress,
            ReceptionistBusinessConfiguration? configuration = null, string provider = "openai", string? projectKey = null)
        {
            configuration ??= Configuration();
            await using var db = Database();
            if (owner.AgentTrackingProfileId is { } agent && !await db.Set<AgentTrackingProfile>().AnyAsync(x => x.Id == agent)) db.Add(new AgentTrackingProfile { Id = agent });
            if (owner.CommerceBusinessId is { } business && !await db.Set<CommerceBusiness>().AnyAsync(x => x.Id == business)) db.Add(new CommerceBusiness { Id = business });
            await db.SaveChangesAsync();
            var store = new MarketingConnectionStore(db, null!);
            var snapshot = await store.SaveReceptionistConfigurationAsync(owner, provider, Guid.Empty, configuration, "authorized-actor");
            var account = await OpenAsync("owner", owner.Key, configuration.MonthlyCashLimit, configuration.MonthlyConsumptionLimit);
            var connection = await db.MarketingConnections.AsNoTracking().SingleAsync(x => x.OwnerKey == owner.Key && x.Provider == provider);
            return new(owner, provider, ingress, account, connection.Id, snapshot.ConfigurationId!.Value,
                projectKey ?? "verified-project-" + Guid.NewGuid().ToString("N"));
        }

        internal async Task<OwnerFixture> AcceptDraftAsync(OwnerFixture draft, Guid funding,
            ReceptionistCostComponent? component = null, string webhookReference = "secret-store:webhook")
        {
            await using var db = Database();
            var store = new MarketingConnectionStore(db, null!);
            var snapshot = await SettingsAsync(draft);
            var configuration = snapshot.Configuration!;
            var binding = new ReceptionistProviderBinding
            {
                IngressKey = draft.Ingress, ProviderProjectKey = draft.ProjectKey, Model = "approved-test-model", Voice = "explicit-test-voice", BackendMode = "application_services", Transport = "sip",
                CredentialReference = "secret-store:provider", WebhookSecretReference = webhookReference,
                ProviderVerificationReceipt = "provider-acceptance", CostVerificationReceipt = "complete-cost-acceptance",
                ExclusiveProjectVerificationReceipt = "operator-verified-exclusive-project:" + draft.Owner.Key,
                DisclosureConfigurationReceipt = "actual-disclosure", RetentionConfigurationReceipt = "actual-retention",
                CarrierFallbackReceipt = "verified-carrier-fallback", MaximumSupportedCallSeconds = configuration.MaximumCallSeconds,
                Costs = [component ?? new("provider_cost", funding, 2m, 0m, 3m, 0m)],
                Capabilities = [new(ReceptionistCapabilities.CanonicalIntake, "leads.confirmed_inquiry.v1", ReceptionistCapabilityStates.Accepted, "canonical-intake-acceptance")]
            };
            snapshot = await store.AcceptReceptionistBindingAsync(draft.Owner, draft.Provider, snapshot.ConnectionRevision, binding, "verified-operator");
            await store.SetReceptionistAdmissionAsync(draft.Owner, draft.Provider, snapshot.ConnectionRevision, true);
            var verified = (await Service.GetIngressBindingAsync(draft.Provider, draft.Ingress))!;
            await ReceptionistTestWindows.SeedAsync(db, verified.MarketingConnectionId, verified.ConfigurationId, verified.Binding, Clock.Now);
            return draft with { ConnectionId = verified.MarketingConnectionId, ConfigurationId = verified.ConfigurationId };
        }

        internal TrustedReceptionistIngress Ingress(OwnerFixture owner, string session) =>
            new(owner.Provider, owner.Ingress, owner.ProjectKey, session, owner.Ingress + ":event:" + session, true,
                owner.ConnectionId, owner.ConfigurationId, owner.Owner.Key);

        internal async Task<OwnerFixture> RebindAsync(OwnerFixture owner, string ingress, string? webhookReference = null)
        {
            var verified = (await Service.GetIngressBindingAsync(owner.Provider, owner.Ingress))!;
            await using var db = Database();
            var store = new MarketingConnectionStore(db, null!);
            var settings = await SettingsAsync(owner);
            settings = await store.AcceptReceptionistBindingAsync(owner.Owner, owner.Provider, settings.ConnectionRevision,
                verified.Binding with { IngressKey = ingress, WebhookSecretReference = webhookReference ?? verified.Binding.WebhookSecretReference }, "verified-operator");
            await store.SetReceptionistAdmissionAsync(owner.Owner, owner.Provider, settings.ConnectionRevision, true);
            var rebound = (await Service.GetIngressBindingAsync(owner.Provider, ingress))!;
            await ReceptionistTestWindows.SeedAsync(db, rebound.MarketingConnectionId, rebound.ConfigurationId, rebound.Binding, Clock.Now);
            return owner with { Ingress = ingress, ConnectionId = rebound.MarketingConnectionId, ConfigurationId = rebound.ConfigurationId };
        }

        internal async Task<ReceptionistConfigurationSnapshot> SettingsAsync(OwnerFixture owner)
        { await using var db = Database(); return await new MarketingConnectionStore(db, null!).GetReceptionistConfigurationAsync(owner.Owner, owner.Provider); }

        public ValueTask DisposeAsync() { File.Delete(path); return ValueTask.CompletedTask; }
    }

    private sealed class ReceptionSchemaContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.ApplyConfiguration(new MarketingConnectionConfiguration());
            ReceptionistModelConfiguration.Configure(model, Database.ProviderName);
            // These existing identities participate by their real keys; their unrelated application graphs are outside this focused schema.
            foreach (var type in new[] { typeof(AgentTrackingProfile), typeof(CommerceBusiness) })
            {
                var entity = model.Entity(type);
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(x => x.Name != "Id")) entity.Ignore(property.Name);
                entity.HasKey("Id");
            }
            var allowed = new HashSet<Type>(typeof(ReceptionistCall).Assembly.GetTypes().Where(x => x.Namespace == "Domain.Entities" && x.Name.StartsWith("Receptionist", StringComparison.Ordinal)))
            { typeof(MarketingConnection), typeof(AgentTrackingProfile), typeof(CommerceBusiness) };
            foreach (var entity in model.Model.GetEntityTypes().ToArray())
                if (!allowed.Contains(entity.ClrType)) model.Ignore(entity.ClrType);
        }
    }
}

internal static class ReceptionistTestWindows
{
    internal static async Task SeedAsync(MasterAppDbContext db, Guid connection, Guid configuration,
        ReceptionistProviderBinding binding, DateTime now)
    {
        if (!binding.ControlledTestOnly) return;
        var version = await db.Set<ReceptionistConfigurationVersion>().AsNoTracking().SingleAsync(x => x.Id == configuration);
        db.Add(new ReceptionistAcceptanceWindow
        {
            MarketingConnectionId = connection, ConfigurationVersionId = configuration, ConfigurationSha256 = version.ConfigurationSha256,
            IngressKey = binding.IngressKey, ProviderProjectKey = binding.ProviderProjectKey,
            RestrictedRouteEvidenceSha256 = new string('a', 64), CreatedByActorId = "synthetic-owner", ReviewedByActorId = "synthetic-reviewer",
            StartsUtc = now, ExpiresUtc = now.AddHours(1)
        });
        await db.SaveChangesAsync();
    }
}
