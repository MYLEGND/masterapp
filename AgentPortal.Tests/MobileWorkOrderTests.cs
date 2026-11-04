using Microsoft.Extensions.Configuration;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Mobile;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Commerce;
using Infrastructure.Mobile;
using Infrastructure.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileWorkOrderTests
{
    private static Microsoft.Extensions.Options.IOptions<CommerceOrderNotificationOptions> NotificationOptions(string? start = "2026-01-01T00:00:00Z") =>
        Microsoft.Extensions.Options.Options.Create(new CommerceOrderNotificationOptions { StoreOrderPushStartUtc = start });
    [Fact]
    public void FulfillmentUsesSharedRulesWithoutChangingFinancialEvidence()
    {
        var order = new CommerceOrder { PaymentStatus = "Paid", PaymentReference = "verified", TotalCents = 5000, RefundedCents = 0 };
        var shipped = DateTime.UtcNow;
        CommerceOrderFulfillment.Apply(order, "Shipped", " Carrier ", " tracking ", shipped);
        CommerceOrderFulfillment.Apply(order, "Fulfilled", "Carrier", "tracking", shipped.AddHours(1));
        Assert.Equal(shipped, order.ShippedUtc);
        Assert.Equal(shipped.AddHours(1), order.FulfilledUtc);
        Assert.Equal("Fulfilled", order.Status);
        Assert.Equal("verified", order.PaymentReference);
        Assert.Equal("Paid", order.PaymentStatus);
        Assert.Equal(5000, order.TotalCents);
        Assert.Equal(0, order.RefundedCents);
    }

    [Theory]
    [InlineData("Shipped", "", "", 0, false)]
    [InlineData("Shipped", "Carrier", "tracking", 0, true)]
    [InlineData("Packed", "", "", 5000, false)]
    [InlineData("Fulfilled", "", "", 5000, false)]
    public void SharedValidatorProtectsFulfillmentAcrossTransports(string status, string carrier, string tracking, int refunded, bool allowed)
    {
        var order = new CommerceOrder { PaymentStatus = "Paid", TotalCents = 5000, RefundedCents = refunded };
        Assert.Equal(allowed, CommerceOrderFulfillment.Validate(order, status, carrier, tracking) == null);
        Assert.NotNull(CommerceOrderFulfillment.Validate(order, "Shipped", "Carrier", new string('x', 161)));
    }

    [Fact]
    public async Task PaidOrderProjectsOnceOnlyForAuthorizedMembership()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "fixture", DisplayName = "Fixture" };
        var owner = new ClientProfile { ClientUserId = "owner", Email = "owner@example.test" };
        var outsider = new ClientProfile { ClientUserId = "outsider", Email = owner.Email };
        var member = new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = owner.Id, RoleKey = "owner" };
        db.CommerceBusinesses.Add(business);
        db.ClientProfiles.AddRange(owner, outsider);
        db.CommerceBusinessMembers.Add(member);
        db.CommerceOrders.Add(new CommerceOrder { CommerceBusinessId = business.Id, OrderNumber = "FIXTURE-1", PaymentStatus = "Paid", PaidUtc = DateTime.UtcNow, CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var engine = new Mock<INotificationEngine>();
        engine.Setup(x => x.StageAsync(It.IsAny<MobileActivityNotification>(), It.IsAny<CancellationToken>()))
            .Callback<MobileActivityNotification, CancellationToken>((row, _) => db.MobileActivityNotifications.Add(row))
            .Returns(Task.CompletedTask);
        var service = new CommerceOrderAppNotificationService(db, engine.Object, NotificationOptions());
        await service.DeliverPendingAsync(default);
        await service.DeliverPendingAsync(default);
        var notification = Assert.Single(await db.MobileActivityNotifications.ToListAsync());
        Assert.Equal(owner.ClientUserId, notification.RecipientUserId);
        Assert.Equal("StoreOrder", notification.Kind);
        engine.Verify(x => x.StageAsync(It.IsAny<MobileActivityNotification>(), It.IsAny<CancellationToken>()), Times.Once);
        member.CanManageOrders = false;
        db.CommerceOrders.Add(new CommerceOrder { CommerceBusinessId = business.Id, OrderNumber = "FIXTURE-2", PaymentStatus = "Paid", PaidUtc = DateTime.UtcNow, CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await service.DeliverPendingAsync(default);
        Assert.Single(await db.MobileActivityNotifications.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentOrderProducersCommitOneLedgerEntryAndOneDelivery()
    {
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "legend-work-orders-" + Guid.NewGuid() + ".db");
        var options = new DbContextOptionsBuilder<Infrastructure.Data.MasterAppDbContext>()
            .UseSqlite("Data Source=" + file + ";Foreign Keys=False;Pooling=False").Options;
        try
        {
            await using var setup = new Infrastructure.Data.MasterAppDbContext(options);
            await setup.Database.EnsureCreatedAsync();
            var profile = new ClientProfile { ClientUserId = "fixture-owner", Email = "fixture@example.test" };
            var business = new CommerceBusiness { Key = "collision-fixture" };
            setup.ClientProfiles.Add(profile);
            setup.CommerceBusinesses.Add(business);
            setup.CommerceBusinessMembers.Add(new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = profile.Id });
            setup.CommerceOrders.Add(new CommerceOrder { CommerceBusinessId = business.Id, OrderNumber = "COLLISION", PaymentStatus = "Paid", PaidUtc = DateTime.UtcNow, CreatedUtc = DateTime.UtcNow });
            setup.MobilePushDevices.Add(new MobilePushDevice { UserId = profile.ClientUserId, ParticipantType = MessagingParticipantTypes.Client,
                DeviceToken = "fixture-only", TokenHash = "fixture-only", Provider = MobilePushProviders.Apns });
            await setup.SaveChangesAsync();
            var staged = 0;
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Run()
            {
                await using var db = new Infrastructure.Data.MasterAppDbContext(options);
                var real = new NotificationEngine(db, Mock.Of<Infrastructure.Messaging.IMessagingProfileImageResolver>(),
                    Mock.Of<INotificationRealtimePublisher>(), new ApplePushDeliverySignal(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationEngine>.Instance);
                var engine = new Mock<INotificationEngine>();
                engine.Setup(x => x.StageAsync(It.IsAny<MobileActivityNotification>(), It.IsAny<CancellationToken>()))
                    .Returns(async (MobileActivityNotification notification, CancellationToken ct) => {
                        await real.StageAsync(notification, ct);
                        if (Interlocked.Increment(ref staged) == 2) barrier.TrySetResult();
                        await barrier.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
                    });
                await new CommerceOrderAppNotificationService(db, engine.Object, NotificationOptions()).DeliverPendingAsync(default);
            }
            await Task.WhenAll(Run(), Run());
            Assert.Single(await setup.MobileActivityNotifications.AsNoTracking().ToListAsync());
            Assert.Single(await setup.MobilePushDeliveries.AsNoTracking().ToListAsync());
        }
        finally { if (System.IO.File.Exists(file)) System.IO.File.Delete(file); }
    }

    [Fact]
    public async Task SqlFulfillmentAcceptsExactFractionalTokenAndRejectsStaleSave()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        await using var db = new Infrastructure.Data.MasterAppDbContext(new DbContextOptionsBuilder<Infrastructure.Data.MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var profile = new ClientProfile { ClientUserId = "owner-sql", Email = "fixture@example.test" };
        var business = new CommerceBusiness { Key = "sql-fixture" };
        var original = DateTime.Parse("2026-11-03T10:20:30.1234567Z", null, System.Globalization.DateTimeStyles.RoundtripKind);
        var order = new CommerceOrder { CommerceBusinessId = business.Id, OrderNumber = "SQL-FIXTURE", PaymentStatus = "Paid", UpdatedUtc = original };
        db.ClientProfiles.Add(profile);
        db.CommerceBusinesses.Add(business);
        db.CommerceBusinessMembers.Add(new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = profile.Id });
        db.CommerceOrders.Add(order);
        await db.SaveChangesAsync();
        var actor = new MobileResolvedActor(new MessagingActor(profile.ClientUserId, MessagingParticipantTypes.Client), profile.Id, "Fixture");
        var actors = new Mock<IMobileActorResolver>();
        actors.Setup(x => x.ResolveAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MobileActorResolution(true, null, null, new[] { actor }, actor, false));
        var controller = new MobileWorkOrdersController(actors.Object, db, new ParfaitApp.Services.ParfaitOrderService(db)) {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var input = new MobileWorkOrdersController.FulfillmentInput(business.Id, profile.Id, "Packed", null, null, original);
        Assert.IsType<OkObjectResult>(await controller.Fulfill(order.Id, input, default));
        Assert.IsType<ConflictObjectResult>(await controller.Fulfill(order.Id, input with { Status = "Shipped" }, default));
        var saved = await db.CommerceOrders.AsNoTracking().SingleAsync(x => x.Id == order.Id);
        Assert.Equal("Packed", saved.FulfillmentStatus);
        Assert.Equal("Paid", saved.PaymentStatus);
        Assert.IsType<ForbidResult>(await controller.Fulfill(order.Id, input with { ProfileId = Guid.NewGuid() }, default));
    }

    [Fact]
    public async Task UnrelatedActorCannotReadOrFulfillBusinessOrders()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var actor = new MobileResolvedActor(new MessagingActor("outsider", MessagingParticipantTypes.Client), Guid.NewGuid(), "Fixture");
        var actors = new Mock<IMobileActorResolver>();
        actors.Setup(x => x.ResolveAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MobileActorResolution(true, null, null, new[] { actor }, actor, false));
        var controller = new MobileWorkOrdersController(actors.Object, db, new ParfaitApp.Services.ParfaitOrderService(db)) {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var list = Assert.IsType<OkObjectResult>(await controller.List(default));
        Assert.Empty(Assert.IsAssignableFrom<System.Collections.IEnumerable>(list.Value).Cast<object>());
        Assert.IsType<ForbidResult>(await controller.Fulfill(Guid.NewGuid(), new MobileWorkOrdersController.FulfillmentInput(
            Guid.NewGuid(), Guid.NewGuid(), "Fulfilled", null, null, null), default));
    }
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("2030-01-01T00:00:00Z", 0)]
    [InlineData("2026-11-03T00:00:00Z", 2)]
    public async Task OrderPushActivationUsesExplicitPaidTimestampAndDeduplicates(string? start, int expected)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "activation-fixture" };
        var profile = new ClientProfile { ClientUserId = "activation-owner", Email = "fixture@example.test" };
        db.AddRange(business, profile, new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = profile.Id });
        var boundary = DateTime.Parse("2026-11-03T00:00:00Z", null, System.Globalization.DateTimeStyles.RoundtripKind);
        foreach (var paid in new DateTime?[] { null, boundary.AddTicks(-1), boundary, boundary.AddTicks(1) })
            db.Add(new CommerceOrder { CommerceBusinessId = business.Id, OrderNumber = Guid.NewGuid().ToString(), PaymentStatus = "Paid", PaidUtc = paid, CreatedUtc = boundary.AddYears(-1) });
        await db.SaveChangesAsync();
        var engine = new Mock<INotificationEngine>();
        engine.Setup(x => x.StageAsync(It.IsAny<MobileActivityNotification>(), It.IsAny<CancellationToken>()))
            .Callback<MobileActivityNotification, CancellationToken>((row, _) => db.Add(row)).Returns(Task.CompletedTask);
        var service = new CommerceOrderAppNotificationService(db, engine.Object, NotificationOptions(start));
        await service.DeliverPendingAsync(default);
        await service.DeliverPendingAsync(default);
        Assert.Equal(expected, await db.MobileActivityNotifications.CountAsync());
    }

    [Fact]
    public void InvalidOrderPushActivationFailsRegisteredOptionsValidation()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(services,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?> {
                ["Notifications:StoreOrderPushStartUtc"] = "2026-11-03T00:00:00-08:00" }).Build());
        Infrastructure.Leads.WebsiteLeadServiceRegistration.AddWebsiteLeadBackgroundWorkers(services);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.Options.IOptions<CommerceOrderNotificationOptions>>(provider).Value);
    }

    [Theory]
    [InlineData("2026-11-03T00:00:00")]
    [InlineData("2026-11-03T00:00:00-08:00")]
    [InlineData("not-a-time")]
    public void OrderPushActivationRejectsAmbiguousOrInvalidTime(string value) =>
        Assert.False(new CommerceOrderNotificationOptions { StoreOrderPushStartUtc = value }.TryGetStart(out _));

    [Fact]
    public async Task NativeCanonicalUpdateReloadsShippingClaimsAndPreservesPickupAndShipmentEffects()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        await using var db = new Infrastructure.Data.MasterAppDbContext(new DbContextOptionsBuilder<Infrastructure.Data.MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var business = new CommerceBusiness { Key = "canonical-native" };
        var order = new CommerceOrder { CommerceBusinessId = business.Id, OrderNumber = "CANONICAL-NATIVE", PaymentStatus = "Paid",
            TotalCents = 5000, RefundedCents = 100, PaymentReference = "retained", AdminNotes = "retained notes", Email = "fixture@example.invalid", UpdatedUtc = DateTime.UtcNow };
        db.AddRange(business, order); await db.SaveChangesAsync();
        var service = new ParfaitApp.Services.ParfaitOrderService(db);
        var revision = order.UpdatedUtc!.Value.Ticks;
        await db.CommerceOrders.Where(x => x.Id == order.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.IsPaymentProcessing, true));
        Assert.False(service.UpdateFulfillment(business.Id, order.Id, revision, "Packed", null, null, out _));
        Assert.True((await db.CommerceOrders.AsNoTracking().SingleAsync()).IsPaymentProcessing);
        await db.CommerceOrders.Where(x => x.Id == order.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.IsPaymentProcessing, false));
        // A label claim intentionally does not change UpdatedUtc. The tracked order is stale.
        await db.CommerceOrders.Where(x => x.Id == order.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.ShippingLabelStateJson, "{\"Status\":\"PurchasePending\"}"));
        Assert.False(service.UpdateFulfillment(business.Id, order.Id, revision, "Packed", null, null, out _));
        Assert.Equal("Unfulfilled", (await db.CommerceOrders.AsNoTracking().SingleAsync()).FulfillmentStatus);
        await db.CommerceOrders.Where(x => x.Id == order.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.ShippingLabelStateJson, (string?)null));
        Assert.True(service.UpdateFulfillment(business.Id, order.Id, revision, "Fulfilled", null, null, out _));
        var saved = await db.CommerceOrders.AsNoTracking().SingleAsync();
        Assert.Null(saved.ShippedUtc); Assert.NotNull(saved.FulfilledUtc);
        Assert.Equal("retained", saved.PaymentReference); Assert.Equal(100, saved.RefundedCents); Assert.Equal("retained notes", saved.AdminNotes);
        Assert.Single(await db.MarketingDestinationDeliveries.ToListAsync());
        Assert.False(service.UpdateFulfillment(business.Id, order.Id, revision, "Shipped", "Carrier", "tracking", out _));
        Assert.Single(await db.MarketingDestinationDeliveries.ToListAsync());
        await db.CommerceOrders.Where(x => x.Id == order.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.RefundedCents, 5000));
        Assert.False(service.UpdateFulfillment(business.Id, order.Id, saved.UpdatedUtc!.Value.Ticks, "Packed", null, null, out _));
    }
}
