using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Moq;
using ParfaitApp.Models;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceBundleInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnclaimedHoldCanBeReleasedButRecoveryMustReserveTheSavedVariantsAgain(bool legacy)
    {
        using var sqlite = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        sqlite.Open();
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(sqlite).Options);
        db.Database.EnsureCreated();
        var store = new CommerceBusiness { Key = "recovery-store", DisplayName = "Recovery Store" };
        var product = Product(store.Id, "Top", "M", 1);
        db.AddRange(store, product);
        db.SaveChanges();
        var products = Service(db);
        var quote = products.QuoteCart(store.Id, [Item("Top", "M", 1)], null);
        var orders = new ParfaitOrderService(db);
        var first = Prepare(db, store.Id, "first", quote).Order;
        if (legacy)
        {
            db.CommerceOrderLines.Single(line => line.CommerceOrderId == first.CommerceOrderId).InventoryRequirementsJson = null;
            db.SaveChanges();
            first = orders.GetOrder(store.Id, first.OrderNumber)!;
        }
        using var competingDb = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(sqlite).Options);
        var competingOrders = new ParfaitOrderService(competingDb);
        competingOrders.ReleaseUnclaimedCheckoutPayment(store.Id, first.CommerceOrderId);
        // Each owner must refresh its tracked flags after the other context writes.
        Assert.True(orders.ResumeCheckoutPayment(store.Id, first.CommerceOrderId).IsPaymentProcessing);
        Assert.True(competingDb.CommerceOrders.AsNoTracking().Single(x => x.Id == first.CommerceOrderId).IsPaymentProcessing);
        competingOrders.ReleaseUnclaimedCheckoutPayment(store.Id, first.CommerceOrderId);
        Assert.False(db.CommerceOrders.AsNoTracking().Single(x => x.Id == first.CommerceOrderId).IsPaymentProcessing);
        Assert.True(products.QuoteCart(store.Id, [Item("Top", "M", 1)], null).IsValid);
        var second = Prepare(db, store.Id, "second", quote).Order;
        Assert.Throws<StoreInventoryException>(() => orders.ResumeCheckoutPayment(store.Id, first.CommerceOrderId));
        orders.ReleaseUnclaimedCheckoutPayment(store.Id, second.CommerceOrderId);
        var resumed = orders.ResumeCheckoutPayment(store.Id, first.CommerceOrderId);
        Assert.True(resumed.IsPaymentProcessing);
        Assert.Equal(first.TotalCents, resumed.TotalCents);
        Assert.Equal(first.Items.Single().InventoryRequirementsJson, resumed.Items.Single().InventoryRequirementsJson);
        db.CommercePaymentAttempts.Add(new() { CommerceBusinessId = store.Id, CommerceOrderId = first.CommerceOrderId,
            State = "Unknown", IdempotencyKey = "synthetic-unknown" });
        db.SaveChanges();
        orders.ReleaseUnclaimedCheckoutPayment(store.Id, first.CommerceOrderId);
        Assert.True(orders.GetOrder(store.Id, first.OrderNumber)!.IsPaymentProcessing);
        Assert.False(products.QuoteCart(store.Id, [Item("Top", "M", 1)], null).IsValid);
        Assert.Equal(1, db.CommerceProductInventoryItems.Single().StockQuantity);
    }

    [Theory]
    [InlineData("Failed", "Unfulfilled")]
    [InlineData("Pending", "Cancelled")]
    public void RecoveryRejectsNonpayableOrderWithoutAcquiringAHold(string paymentStatus, string fulfillmentStatus)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var order = new CommerceOrder { CommerceBusinessId = Guid.NewGuid(), TotalCents = 1000,
            PaymentStatus = paymentStatus, FulfillmentStatus = fulfillmentStatus };
        db.Add(order);
        db.SaveChanges();
        Assert.Throws<Infrastructure.Commerce.Payments.MerchantPaymentException>(() =>
            new ParfaitOrderService(db).ResumeCheckoutPayment(order.CommerceBusinessId, order.Id));
        Assert.False(db.CommerceOrders.AsNoTracking().Single().IsPaymentProcessing);
    }

    [Fact]
    public void MixedCartAndPendingCheckoutShareComponentsAndKeepOriginalRecipe()
    {
        using var sqlite = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        sqlite.Open();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(sqlite).Options;
        using var db = new MasterAppDbContext(options);
        db.Database.EnsureCreated();
        var store = new CommerceBusiness { Key = "bundle-store", DisplayName = "Bundle Store" };
        var otherStore = new CommerceBusiness { Key = "other-store", DisplayName = "Other Store" };
        var top = Product(store.Id, "Top", "M", 5);
        var bottom = Product(store.Id, "Bottom", "M", 2);
        var foreign = Product(otherStore.Id, "Top", "M", 30);
        db.AddRange(store, otherStore, top, bottom, foreign);
        db.SaveChanges();
        var products = Service(db);
        var outfit = new ParfaitProductEditorViewModel { Id = "outfit", Name = "Outfit", Slug = "outfit", PriceCents = 2500 };
        // An existing saved recipe keeps its non-default units during automatic expansion.
        var savedBundle = Product(store.Id, "outfit", "M", 0);
        savedBundle.BundleComponentsJson = Recipe((top.InventoryItems[0].Id, 2), (bottom.InventoryItems[0].Id, 1));
        db.Add(savedBundle);
        db.SaveChanges();
        outfit.LinkedProductIdsJson = JsonSerializer.Serialize(new[] { "Top", "Bottom" });
        products.SaveProduct(store.Id, outfit);
        Assert.Equal(2, products.GetActiveStoreProductById(store.Id, "outfit")!.Sizes.Single(size => size.Size == "M").StockQuantity);
        Assert.False(products.QuoteCart(store.Id, [Item("outfit", "M", 2), Item("Top", "M", 2)], null).IsValid);

        var legacy = new CommerceOrder { CommerceBusinessId = store.Id, OrderNumber = "legacy-pending",
            PaymentStatus = "Pending", IsPaymentProcessing = true, PaymentProcessingStartedUtc = DateTime.UtcNow,
            Lines = [new() { ProductExternalKey = "Top", Size = "M", Quantity = 2 }] };
        db.Add(legacy);
        db.SaveChanges();
        Assert.Equal(1, products.GetActiveStoreProductById(store.Id, "outfit")!.Sizes.Single(size => size.Size == "M").StockQuantity);
        new ParfaitOrderService(db).MarkPaymentFailed(store.Id, legacy.OrderNumber, "Declined");
        Assert.Equal(2, products.GetActiveStoreProductById(store.Id, "outfit")!.Sizes.Single(size => size.Size == "M").StockQuantity);

        var cart = new[] { Item("outfit", "M", 1), Item("Top", "M", 2) };
        var quote = products.QuoteCart(store.Id, cart, null);
        Assert.True(quote.IsValid);
        using var competingDb = new MasterAppDbContext(options);
        var competingQuote = Service(competingDb).QuoteCart(store.Id, cart, null);
        var unprepared = new CommerceOrder { CommerceBusinessId = store.Id, OrderNumber = "unprepared",
            CheckoutAttemptId = "failed-save", PaymentStatus = "Pending" };
        db.Add(unprepared);
        db.SaveChanges();
        using (var failingDb = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(sqlite).AddInterceptors(new RejectCheckoutSave()).Options))
        {
            Assert.Throws<DbUpdateException>(() => Prepare(failingDb, store.Id, "failed-save", quote));
        }
        Assert.False(competingDb.CommerceOrders.AsNoTracking().Single(order => order.Id == unprepared.Id).IsPaymentProcessing);

        var prepared = Prepare(db, store.Id, "first", quote);
        Assert.Equal(CheckoutPaymentStartState.AlreadyProcessing, Prepare(db, store.Id, "first", quote).State);
        Assert.Throws<StoreInventoryException>(() => Prepare(competingDb, store.Id, "second", competingQuote));
        Assert.False(products.QuoteCart(store.Id, [Item("outfit", "M", 1)], null).IsValid);
        Assert.True(new ParfaitOrderService(db).GetOrder(store.Id, prepared.Order.OrderNumber)!.PaymentManagedByProcessor);
        Assert.Throws<StoreInventoryException>(() => products.DeleteProduct(store.Id, "Bottom"));

        outfit = products.GetAllProducts(store.Id).Single(product => product.Id == "outfit");
        outfit.LinkedProductIdsJson = JsonSerializer.Serialize(new[] { "Bottom" });
        products.SaveProduct(store.Id, outfit);
        var saved = new ParfaitOrderService(db).GetOrder(store.Id, prepared.Order.OrderNumber)!;
        Assert.Equal(2, saved.Items.Single(item => item.Id == "outfit").BundlePickList.Count);
        products.CommitPaidInventory(store.Id, saved.Items);
        Assert.Equal(1, db.CommerceProductInventoryItems.Find(top.InventoryItems[0].Id)!.StockQuantity);
        Assert.Equal(1, db.CommerceProductInventoryItems.Find(bottom.InventoryItems[0].Id)!.StockQuantity);
        Assert.Equal(30, db.CommerceProductInventoryItems.Find(foreign.InventoryItems[0].Id)!.StockQuantity);
    }

    [Fact]
    public void HiddenComponentBlocksNewQuotesAndCrossStoreLinksAreRejected()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var store = new CommerceBusiness { Key = "bundle-store", DisplayName = "Bundle Store" };
        var otherStore = new CommerceBusiness { Key = "other-store", DisplayName = "Other Store" };
        var top = Product(store.Id, "Top", "M", 3);
        var foreign = Product(otherStore.Id, "Foreign", "M", 3);
        db.AddRange(store, otherStore, top, foreign);
        db.SaveChanges();
        var products = Service(db);
        var outfit = new ParfaitProductEditorViewModel { Id = "outfit", Name = "Outfit", Slug = "outfit", PriceCents = 2500 };
        outfit.LinkedProductIdsJson = JsonSerializer.Serialize(new[] { "Foreign" });
        Assert.Throws<StoreInventoryException>(() => products.SaveProduct(store.Id, outfit));
        outfit.LinkedProductIdsJson = JsonSerializer.Serialize(new[] { "Top" });
        products.SaveProduct(store.Id, outfit);
        top.InventoryItems[0].IsEnabled = false;
        db.SaveChanges();
        Assert.False(products.QuoteCart(store.Id, [Item("outfit", "M", 1)], null).IsValid);
        Assert.Equal(0, products.GetActiveStoreProductById(store.Id, "outfit")!.Sizes.Single(size => size.Size == "M").StockQuantity);
    }

    [Fact]
    public void MigrationAddsNullableBundleAndOrderSnapshots()
    {
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer("Server=fixture.invalid;Database=fixture;Integrated Security=true;TrustServerCertificate=true").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var script = db.GetService<IMigrator>().GenerateScript("20261107153025_AddReceptionistSetupGuide", "20261110000000_AddCommerceBundleInventory");
        Assert.Contains("ADD [BundleComponentsJson] nvarchar(max) NULL", script);
        Assert.Contains("ADD [InventoryRequirementsJson] nvarchar(max) NULL", script);
    }

    [Fact]
    public async System.Threading.Tasks.Task SqliteBootstrapPatchesExistingTables()
    {
        using var sqlite = new SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(sqlite).Options);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        await MasterAppSqliteSchemaBootstrapper.InitializeAsync(db, logger);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE CommerceProducts DROP COLUMN BundleComponentsJson;");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE CommerceOrderLines DROP COLUMN InventoryRequirementsJson;");
        await MasterAppSqliteSchemaBootstrapper.InitializeAsync(db, logger);
        Assert.Empty(await db.CommerceProducts.Select(product => product.BundleComponentsJson).ToListAsync());
        Assert.Empty(await db.CommerceOrderLines.Select(line => line.InventoryRequirementsJson).ToListAsync());
    }

    private sealed class RejectCheckoutSave : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
            throw new DbUpdateException("Simulated failure before the checkout hold is saved.");
    }

    private static ParfaitProductService Service(MasterAppDbContext db)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(x => x.ContentRootPath).Returns(Path.GetTempPath());
        environment.SetupGet(x => x.WebRootPath).Returns(Path.GetTempPath());
        return new(new ParfaitStoragePaths(environment.Object, new ConfigurationBuilder().Build()), db);
    }

    private static CommerceProduct Product(Guid store, string name, string size, int stock) =>
        new() { CommerceBusinessId = store, ExternalProductKey = name, Name = name, Slug = name.ToLowerInvariant(),
            PriceCents = 1000, InventoryItems = [new() { ExternalInventoryKey = size, Size = size, StockQuantity = stock, IsEnabled = true }] };

    private static string Recipe(params (Guid Variant, int Units)[] parts) =>
        JsonSerializer.Serialize(parts.Select(part => new ParfaitBundleComponent("M", part.Variant, part.Units)));

    private static ParfaitCheckoutItemRequest Item(string id, string size, int quantity) =>
        new() { Id = id, Size = size, Quantity = quantity };

    private static CheckoutPaymentStartResult Prepare(MasterAppDbContext db, Guid store, string attempt, ParfaitCartQuoteResponse quote) =>
        new ParfaitOrderService(db).BeginCheckoutPayment(store, attempt, new(),
            quote.Items.Select(item => new ParfaitValidatedCartItem
            {
                Id = item.Id, Name = item.Name, Slug = item.Slug, Size = item.Size, Quantity = item.Quantity,
                UnitPriceCents = item.UnitPriceCents, InventoryRequirementsJson = item.InventoryRequirementsJson
            }).ToArray(), quote.SubtotalCents, null, null, 0, 0, 0, new DefaultHttpContext());
}
