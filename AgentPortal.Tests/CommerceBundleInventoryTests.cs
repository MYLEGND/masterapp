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
        var bottom = Product(store.Id, "Bottom", "L", 2);
        var foreign = Product(otherStore.Id, "Top", "M", 30);
        db.AddRange(store, otherStore, top, bottom, foreign);
        db.SaveChanges();
        var products = Service(db);
        var outfit = new ParfaitProductEditorViewModel { Id = "outfit", Name = "Outfit", Slug = "outfit", PriceCents = 2500 };
        outfit.BundleComponentsJson = Recipe((top.InventoryItems[0].Id, 2), (bottom.InventoryItems[0].Id, 1));
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
        outfit.BundleComponentsJson = Recipe((bottom.InventoryItems[0].Id, 1));
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
        var foreign = Product(otherStore.Id, "Top", "M", 3);
        db.AddRange(store, otherStore, top, foreign);
        db.SaveChanges();
        var products = Service(db);
        var outfit = new ParfaitProductEditorViewModel { Id = "outfit", Name = "Outfit", Slug = "outfit", PriceCents = 2500 };
        outfit.BundleComponentsJson = Recipe((foreign.InventoryItems[0].Id, 1));
        Assert.Throws<StoreInventoryException>(() => products.SaveProduct(store.Id, outfit));
        outfit.BundleComponentsJson = Recipe((top.InventoryItems[0].Id, 1));
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
