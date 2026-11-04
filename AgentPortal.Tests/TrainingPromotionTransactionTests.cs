using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Data.Common;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Billing;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class TrainingPromotionTransactionTests
{
    [Fact]
    public async Task ConcurrentDifferentMembersCannotBothTakeFinalPlace()
    {
        using var fixture = new Fixture();
        await fixture.Initialize();
        await using (var seed = fixture.Open())
            foreach (var id in fixture.Clients.Take(4)) await Service(seed).CreateClientSubscriptionOfferAsync(fixture.Command(id));
        using var start = new Barrier(2);
        async Task<bool> Reserve(Guid client) => await Task.Run(async () =>
        {
            await using var db = fixture.Open();
            start.SignalAndWait(TimeSpan.FromSeconds(30));
            try { await Service(db).CreateClientSubscriptionOfferAsync(fixture.Command(client)); return true; }
            catch (TrainingPromotionCapacityException) { return false; }
        });
        var outcomes = await Task.WhenAll(Reserve(fixture.Clients[4]), Reserve(fixture.Clients[5]));
        Assert.Single(outcomes.Where(x => x));
        await using var read = fixture.Open();
        Assert.Equal(5, (await TrainingPromotionReservations.ReservedClientIdsAsync(read, fixture.Discount, DateTime.UtcNow)).Count);
        Assert.Equal(5, await read.ClientSubscriptionOffers.CountAsync());
        Assert.Empty(read.SubscriptionPayments);
    }

    [Fact]
    public async Task RolledBackReservationDoesNotConsumeCapacityAndNestedAdmissionDoesNotOpenAnotherTransaction()
    {
        using var fixture = new Fixture(); await fixture.Initialize();
        var fault = new BeforeCommitFault();
        await using (var db = fixture.Open(fault))
        {
            fault.Armed = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).CreateClientSubscriptionOfferAsync(fixture.Command(fixture.Clients[0])));
        }
        await using var fresh = fixture.Open();
        Assert.Empty(await TrainingPromotionReservations.ReservedClientIdsAsync(fresh, fixture.Discount, DateTime.UtcNow));
        await using (var transaction = await fresh.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
        {
            await Service(fresh).CreateClientSubscriptionOfferAsync(fixture.Command(fixture.Clients[0]));
            await transaction.CommitAsync();
        }
        Assert.Single(await TrainingPromotionReservations.ReservedClientIdsAsync(fresh, fixture.Discount, DateTime.UtcNow));
    }

    [Theory]
    [InlineData("failed", false)]
    [InlineData("never-started", false)]
    [InlineData("unknown-started", true)]
    [InlineData("accepted", true)]
    [InlineData("completed", true)]
    [InlineData("refunded", true)]
    [InlineData("pending", true)]
    [InlineData("retryable", true)]
    [InlineData("activated", true)]
    [InlineData("redeemed", true)]
    [InlineData("live", true)]
    [InlineData("mixed-subscriptions", true)]
    public async Task ExpiredDefinitiveUnpaidFailureReleasesOnlyWithCompleteEvidence(string scenario, bool held)
    {
        using var fixture = new Fixture(); await fixture.Initialize();
        await using var db = fixture.Open();
        var offer = await Service(db).CreateClientSubscriptionOfferAsync(fixture.Command(fixture.Clients[0]));
        offer.ExpiresUtc = DateTime.UtcNow.AddDays(scenario == "live" ? 1 : -1);
        offer.Status = scenario == "accepted" ? ClientSubscriptionOfferStatus.Accepted : ClientSubscriptionOfferStatus.Offered;
        var subscription = new ClientSubscription { ClientProfileId = offer.ClientProfileId, AcceptedOfferId = offer.Id,
            Status = ClientSubscriptionStatus.ActivationFailed,
            ActivatedUtc = scenario == "activated" ? DateTime.UtcNow.AddDays(-2) : null };
        db.Add(subscription);
        if (scenario != "never-started") db.Add(new SubscriptionActivationInvitation {
            ClientSubscriptionOfferId = offer.Id, ClientProfileId = offer.ClientProfileId,
            Status = scenario == "redeemed" ? SubscriptionActivationInvitationStatus.Redeemed : SubscriptionActivationInvitationStatus.Expired, PaymentStartedUtc = DateTime.UtcNow.AddDays(-2) });
        if (scenario != "never-started" && scenario != "unknown-started") db.Add(new SubscriptionPayment {
            ClientSubscriptionId = subscription.Id, Status = scenario switch {
                "completed" => SubscriptionPaymentStatus.Completed, "refunded" => SubscriptionPaymentStatus.Refunded,
                "pending" => SubscriptionPaymentStatus.Pending, _ => SubscriptionPaymentStatus.Failed }, Retryable = scenario == "retryable" });
        if (scenario == "mixed-subscriptions") db.Add(new ClientSubscription { ClientProfileId = offer.ClientProfileId,
            AcceptedOfferId = offer.Id, Status = ClientSubscriptionStatus.ReconciliationRequired });
        await db.SaveChangesAsync();
        var reserved = await TrainingPromotionReservations.ReservedClientIdsAsync(db, fixture.Discount, DateTime.UtcNow);
        Assert.Equal(held, reserved.Contains(offer.ClientProfileId));
    }

    private static MasterAppBillingOrchestrator Service(MasterAppDbContext db) => new(db, Mock.Of<IBillingGateway>(),
        Mock.Of<IBillingEntitlementService>(), Mock.Of<IClientSubscriptionActivationPolicyService>(), rollout: new() { TrainingSubscriptionsEnabled = true });
    private sealed class Fixture : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"training-cap-{Guid.NewGuid():N}.db");
        public readonly Guid Business = Guid.NewGuid(), Product = Guid.NewGuid(), Discount = Guid.NewGuid();
        public readonly Guid[] Clients = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        public MasterAppDbContext Open(IInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite($"Data Source={path};Foreign Keys=False;Default Timeout=30;Pooling=False");
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new(options.Options);
        }
        public async Task Initialize()
        {
            await using var db = Open(); await db.Database.EnsureCreatedAsync();
            db.Add(new CommerceBusiness { Id = Business, Key = "relational-training" });
            db.Add(new CommerceProduct { Id = Product, CommerceBusinessId = Business, Name = "Training", Description = "Approved training benefits",
                BillingKind = ClientSubscriptionScope.MonthlyTraining, PriceCents = 35000, Discounts = [new CommerceProductDiscount {
                    Id = Discount, CommerceProductId = Product, ExternalDiscountKey = "intro", Code = "INTRO", DiscountType = "Fixed", Amount = 100, UsageLimit = 5 }] });
            foreach (var id in Clients) db.Add(new ClientProfile { Id = id, ClientUserId = $"member-{id:N}", Email = $"{id:N}@example.test" });
            await db.SaveChangesAsync();
        }
        public CreateClientSubscriptionOfferCommand Command(Guid client) => new(client, "training-test", ClientSubscriptionOfferPriceType.Custom,
            null, "USD", BillingAnchorSelectionMode.FirstOfMonth, null, null, DateTime.UtcNow.AddHours(24),
            TrainingBusinessId: Business, TrainingProductId: Product, TrainingPromotionCode: "INTRO");
        public void Dispose() { if (File.Exists(path)) File.Delete(path); }
    }
    private sealed class BeforeCommitFault : DbTransactionInterceptor
    {
        public bool Armed;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        { if (Armed) { Armed = false; throw new InvalidOperationException("Synthetic precommit failure"); } return ValueTask.FromResult(result); }
    }
}
