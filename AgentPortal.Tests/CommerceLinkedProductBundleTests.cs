using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ParfaitApp.Models;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

// These regressions never create/reset a database or use a production fallback.
public sealed class PersistentBundleFactAttribute : FactAttribute
{
    public PersistentBundleFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LEGEND_TEST_DB_IDENTITY")))
            Skip = "Run scripts/db.sh local test --filter FullyQualifiedName~CommerceLinkedProductBundleTests against persistent synthetic SQL.";
    }
}

public sealed class CommerceLinkedProductBundleTests
{
    [PersistentBundleFact]
    public void MultipleProductsExpandNormalizedCommonSizesIncludingZeroStockAndReload()
    {
        using var f = new Fixture();
        var top = f.Add("top", (" s ", 5, true), ("M", 0, true), ("L", 9, true));
        var bottom = f.Add("bottom", ("S", 3, true), (" m ", 7, true), ("XL", 9, true));
        var belt = f.Add("belt", ("s", 8, true), ("M", 4, true), ("L", 8, false));
        var input = f.Bundle("outfit", "top", "bottom", "belt");
        input.InventoryBySize = [new() { Size = "WRONG", StockQuantity = 99999, IsEnabled = true }];
        f.Products.SaveProduct(f.Store.Id, input);
        using var reread = f.Open();
        var saved = Service(reread).GetAllProducts(f.Store.Id).Single(p => p.Id == "outfit");
        Assert.Equal(new[] { "S", "M" }, saved.InventoryBySize.Select(s => s.Size));
        Assert.Equal(6, saved.BundleComponents.Count);
        Assert.All(saved.BundleComponents, p => Assert.Equal(1, p.Units));
        Assert.Equal(3, saved.InventoryBySize.Single(s => s.Size == "S").StockQuantity);
        Assert.Equal(0, saved.InventoryBySize.Single(s => s.Size == "M").StockQuantity);
        Assert.True(saved.InventoryBySize.Single(s => s.Size == "M").IsEnabled);
        Assert.Equal(new[] { "belt", "bottom", "top" }, saved.LinkedProductIds.OrderBy(x => x));
        Assert.False(Service(reread).QuoteCart(f.Store.Id, [Item("outfit", "M", 1)], null).IsValid);
        saved.LinkedProductIdsJson = JsonSerializer.Serialize(saved.LinkedProductIds);
        Service(reread).SaveProduct(f.Store.Id, saved);
        Assert.Equal(6, reread.CommerceProducts.AsNoTracking().Where(p => p.CommerceBusinessId == f.Store.Id && p.ExternalProductKey == "outfit")
            .Select(p => JsonSerializer.Deserialize<ParfaitBundleComponent[]>(p.BundleComponentsJson!)!.Length).Single());
    }

    [PersistentBundleFact]
    public void NoOverlapAmbiguousLabelsAndUnmatchedSizesNeverPairDifferentSizes()
    {
        using var f = new Fixture();
        f.Add("top", ("S", 4, true), ("M", 5, true));
        f.Add("bottom", ("M", 6, true), ("L", 7, true));
        f.Products.SaveProduct(f.Store.Id, f.Bundle("partial", "top", "bottom"));
        Assert.Equal("M", f.Products.GetAllProducts(f.Store.Id).Single(p => p.Id == "partial").InventoryBySize.Single().Size);
        f.Add("no-overlap", ("XL", 10, true));
        Assert.Contains("no common enabled size", Assert.Throws<StoreInventoryException>(() =>
            f.Products.SaveProduct(f.Store.Id, f.Bundle("invalid", "top", "no-overlap"))).Message);
        f.Add("ambiguous", (" s", 4, true), ("S ", 4, true));
        Assert.Contains("ambiguous", Assert.Throws<StoreInventoryException>(() =>
            f.Products.SaveProduct(f.Store.Id, f.Bundle("invalid", "top", "ambiguous"))).Message);
        Assert.False(f.Db.CommerceProducts.Any(p => p.CommerceBusinessId == f.Store.Id && p.ExternalProductKey == "invalid"));
    }

    [PersistentBundleFact]
    public void ServerRejectsForeignSelfNestedDuplicateSubscriptionAndTamperedRecipes()
    {
        using var f = new Fixture();
        using var other = new Fixture();
        var top = f.Add("top", ("M", 10, true));
        other.Add("foreign-only", ("M", 10, true));
        foreach (var ids in new[] { new[] { "foreign-only" }, new[] { "invalid" }, new[] { "top", "top" }, new[] { "top", "TOP" } })
            Assert.Throws<StoreInventoryException>(() => f.Products.SaveProduct(f.Store.Id, f.Bundle("invalid", ids)));
        f.Products.SaveProduct(f.Store.Id, f.Bundle("nested", "top"));
        Assert.Throws<StoreInventoryException>(() => f.Products.SaveProduct(f.Store.Id, f.Bundle("invalid", "nested")));
        var subscription = f.Add("subscription", ("M", 5, true));
        subscription.BillingKind = Domain.Billing.ClientSubscriptionScope.MonthlyTraining;
        f.Db.SaveChanges();
        Assert.Throws<StoreInventoryException>(() => f.Products.SaveProduct(f.Store.Id, f.Bundle("invalid", "subscription")));
        var tampered = f.Bundle("invalid", "top");
        tampered.BundleComponentsJson = JsonSerializer.Serialize(new[] { new ParfaitBundleComponent("M", top.InventoryItems.Single().Id, 99) });
        Assert.Throws<StoreInventoryException>(() => f.Products.SaveProduct(f.Store.Id, tampered));
        Assert.False(f.Db.CommerceProducts.Any(p => p.CommerceBusinessId == f.Store.Id && p.ExternalProductKey == "invalid"));
    }

    [PersistentBundleFact]
    public void ExistingQuantitiesHoldsConcurrencyAndOrderSnapshotsRetainExactRequirements()
    {
        using var f = new Fixture();
        var top = f.Add("top", ("M", 5, true));
        var bottom = f.Add("bottom", ("M", 4, true));
        var outfit = f.Add("outfit", ("M", 0, true));
        outfit.BundleComponentsJson = JsonSerializer.Serialize(new[] {
            new ParfaitBundleComponent("M", top.InventoryItems.Single().Id, 2),
            new ParfaitBundleComponent("M", bottom.InventoryItems.Single().Id, 1) });
        f.Db.SaveChanges();
        var edit = f.Products.GetAllProducts(f.Store.Id).Single(p => p.Id == "outfit");
        edit.LinkedProductIdsJson = JsonSerializer.Serialize(edit.LinkedProductIds);
        f.Products.SaveProduct(f.Store.Id, edit);
        Assert.Equal(2, f.Products.GetAllProducts(f.Store.Id).Single(p => p.Id == "outfit").BundleComponents.Single(p => p.InventoryItemId == top.InventoryItems.Single().Id).Units);
        Assert.False(f.Products.QuoteCart(f.Store.Id, [Item("outfit", "M", 2), Item("top", "M", 2)], null).IsValid);
        var quote = f.Products.QuoteCart(f.Store.Id, [Item("outfit", "M", 2)], null);
        Assert.True(quote.IsValid);
        using var competitor = f.Open();
        var staleQuote = Service(competitor).QuoteCart(f.Store.Id, [Item("outfit", "M", 2)], null);
        var held = Prepare(f.Db, f.Store.Id, "first", quote).Order;
        Assert.Throws<StoreInventoryException>(() => Prepare(competitor, f.Store.Id, "competing", staleQuote));
        Assert.False(f.Products.QuoteCart(f.Store.Id, [Item("outfit", "M", 1)], null).IsValid);
        var before = held.Items.Single().InventoryRequirementsJson;
        edit.LinkedProductIdsJson = JsonSerializer.Serialize(new[] { "bottom" });
        f.Products.SaveProduct(f.Store.Id, edit);
        var savedOrder = new ParfaitOrderService(f.Db).GetOrder(f.Store.Id, held.OrderNumber)!;
        Assert.Equal(before, savedOrder.Items.Single().InventoryRequirementsJson);
        f.Products.CommitPaidInventory(f.Store.Id, savedOrder.Items);
        using var verify = f.Open();
        Assert.Equal(1, verify.CommerceProductInventoryItems.Single(p => p.Id == top.InventoryItems.Single().Id).StockQuantity);
        Assert.Equal(2, verify.CommerceProductInventoryItems.Single(p => p.Id == bottom.InventoryItems.Single().Id).StockQuantity);
    }

    [PersistentBundleFact]
    public void DisabledOrIncompleteComponentsBlockPurchasesAndStockEditsDetectConcurrency()
    {
        using var f = new Fixture();
        var top = f.Add("top", ("M", 5, true));
        var bottom = f.Add("bottom", ("M", 5, true));
        f.Products.SaveProduct(f.Store.Id, f.Bundle("outfit", "top", "bottom"));
        var stale = f.Products.GetAllProducts(f.Store.Id).Single(p => p.Id == "top");
        using (var writer = f.Open())
        {
            writer.CommerceProductInventoryItems.Single(p => p.Id == top.InventoryItems.Single().Id).StockQuantity = 3;
            writer.SaveChanges();
        }
        stale.InventoryBySize.Single(p => p.Size == "M").StockQuantity = 4;
        Assert.Throws<StoreInventoryException>(() => f.Products.SaveProduct(f.Store.Id, stale));
        f.Db.ChangeTracker.Clear();
        f.Db.CommerceProductInventoryItems.Single(p => p.Id == bottom.InventoryItems.Single().Id).IsEnabled = false;
        f.Db.SaveChanges();
        Assert.False(f.Products.QuoteCart(f.Store.Id, [Item("outfit", "M", 1)], null).IsValid);
        Assert.Throws<StoreInventoryException>(() => f.Products.DeleteProduct(f.Store.Id, "top"));
        var bundle = f.Db.CommerceProducts.Single(p => p.CommerceBusinessId == f.Store.Id && p.ExternalProductKey == "outfit");
        bundle.BundleComponentsJson = JsonSerializer.Serialize(new[] {
            new ParfaitBundleComponent("M", top.InventoryItems.Single().Id, 1),
            new ParfaitBundleComponent("S", bottom.InventoryItems.Single().Id, 1) });
        f.Db.SaveChanges();
        Assert.False(f.Products.QuoteCart(f.Store.Id, [Item("outfit", "M", 1)], null).IsValid);
    }

    [PersistentBundleFact]
    public void OrdinaryProductsKeepTheirStockAndRemovingLinksDoesNotInventLooseStock()
    {
        using var f = new Fixture();
        f.Add("top", ("M", 7, true));
        var edit = f.Products.GetAllProducts(f.Store.Id).Single(p => p.Id == "top");
        edit.Name = "Renamed individual";
        edit.LinkedProductIdsJson = "[]";
        f.Products.SaveProduct(f.Store.Id, edit);
        Assert.Equal(7, f.Products.GetAllProducts(f.Store.Id).Single(p => p.Id == "top").InventoryBySize.Single(s => s.Size == "M").StockQuantity);
        f.Products.SaveProduct(f.Store.Id, f.Bundle("outfit", "top"));
        var bundle = f.Products.GetAllProducts(f.Store.Id).Single(p => p.Id == "outfit");
        bundle.LinkedProductIdsJson = "[]";
        bundle.InventoryBySize.Single(size => size.Size == "M").StockQuantity = 999;
        f.Products.SaveProduct(f.Store.Id, bundle);
        var unlinked = f.Products.GetAllProducts(f.Store.Id).Single(p => p.Id == "outfit");
        Assert.Empty(unlinked.BundleComponents);
        Assert.All(unlinked.InventoryBySize, size => Assert.Equal(0, size.StockQuantity));
    }

    [PersistentBundleFact]
    public void OmittedSelectionStillDerivesSizesAndRejectsBillingChanges()
    {
        using var f = new Fixture();
        f.Add("top", ("M", 4, true));
        f.Products.SaveProduct(f.Store.Id, f.Bundle("outfit", "top"));
        var edit = f.Products.GetAllProducts(f.Store.Id).Single(p => p.Id == "outfit");
        edit.InventoryBySize = [new() { Size = "WRONG", StockQuantity = 999, IsEnabled = true }];
        Assert.Null(edit.LinkedProductIdsJson);
        f.Products.SaveProduct(f.Store.Id, edit);
        Assert.Equal("M", f.Products.GetAllProducts(f.Store.Id).Single(p => p.Id == "outfit").InventoryBySize.Single().Size);
        edit.LinkedProductIdsJson = null;
        edit.BillingKind = "InvalidBillingKind";
        Assert.Throws<StoreInventoryException>(() => f.Products.SaveProduct(f.Store.Id, edit));
        Assert.Equal(Domain.Billing.ClientSubscriptionScope.OneTimeProduct,
            f.Db.CommerceProducts.AsNoTracking().Single(p => p.CommerceBusinessId == f.Store.Id && p.ExternalProductKey == "outfit").BillingKind);
    }

    [Fact]
    public void BundleSelectionExistsOnlyInsideTheCreateAndEditForms()
    {
        var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? throw new InvalidOperationException("Repository fixture root required.");
        var view = File.ReadAllText(Path.Combine(root, "ParfaitApp/Views/InternalModules/Products.cshtml"));
        Assert.DoesNotContain("pfBundlePicker", view);
        Assert.DoesNotContain("data-pf-bundle-open", view);
        Assert.DoesNotContain("data-bundle-size", view);
        Assert.Equal(2, view.Split("Html.PartialAsync(\"~/Views/InternalModules/_LinkedProducts.cshtml\"").Length - 1);
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(view, "Html.PartialAsync\\(\"~/Views/InternalModules/_LinkedProducts.cshtml"))
        {
            var start = view.LastIndexOf("<form", match.Index, StringComparison.Ordinal);
            Assert.True(start >= 0);
            Assert.Contains("data-pf-product-form", view[start..view.IndexOf('>', start)]);
            Assert.True(view.IndexOf("</form>", start, StringComparison.Ordinal) > match.Index);
        }
        var partial = File.ReadAllText(Path.Combine(root, "ParfaitApp/Views/InternalModules/_LinkedProducts.cshtml"));
        Assert.Contains("Link products", partial);
        Assert.Contains("data-pf-link-choice", partial);
        Assert.Contains("data-pf-link-discard", partial);
        Assert.DoesNotContain("<dialog", partial);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string connection;
        public MasterAppDbContext Db { get; }
        public CommerceBusiness Store { get; }
        public ParfaitProductService Products { get; }
        public Fixture()
        {
            connection = Environment.GetEnvironmentVariable("MASTERAPP_LOCAL_DB_CONNECTION") ?? throw new InvalidOperationException("Use the local test owner.");
            var target = new SqlConnectionStringBuilder(connection);
            Assert.Equal("127.0.0.1,14339", target.DataSource);
            Assert.Equal("MasterAppLocalTest", target.InitialCatalog);
            using var sql = new SqlConnection(connection);
            sql.Open();
            using var command = sql.CreateCommand();
            command.CommandText = "SELECT CONVERT(nvarchar(100),value) FROM sys.extended_properties WHERE class=0 AND name=N'LegendLocalTestIdentity'";
            Assert.Equal(Environment.GetEnvironmentVariable("LEGEND_TEST_DB_IDENTITY"), command.ExecuteScalar());
            Db = Open();
            Store = new() { Key = "synthetic-bundle-" + Guid.NewGuid().ToString("N"), DisplayName = "Synthetic bundle regression" };
            Db.Add(Store); Db.SaveChanges();
            Products = Service(Db);
        }
        public MasterAppDbContext Open() => new(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlServer(connection).Options);
        public CommerceProduct Add(string id, params (string Size, int Stock, bool Enabled)[] sizes)
        {
            var product = new CommerceProduct { CommerceBusinessId = Store.Id, ExternalProductKey = id, Name = id,
                Slug = id, PriceCents = 1000, InventoryItems = sizes.Select(size => new CommerceProductInventoryItem {
                    ExternalInventoryKey = Guid.NewGuid().ToString("N"), Size = size.Size, StockQuantity = size.Stock, IsEnabled = size.Enabled }).ToList() };
            Db.Add(product); Db.SaveChanges(); return product;
        }
        public ParfaitProductEditorViewModel Bundle(string id, params string[] products) => new() {
            Id = id, Name = id, Slug = id, PriceCents = 2000, LinkedProductIdsJson = JsonSerializer.Serialize(products) };
        public void Dispose() => Db.Dispose(); // Keep this uniquely scoped synthetic scenario; never reset shared test data.
    }
    private static ParfaitProductService Service(MasterAppDbContext db)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());
        environment.SetupGet(e => e.WebRootPath).Returns(Path.GetTempPath());
        return new(new ParfaitStoragePaths(environment.Object, new ConfigurationBuilder().Build()), db);
    }
    private static ParfaitCheckoutItemRequest Item(string id, string size, int quantity) => new() { Id = id, Size = size, Quantity = quantity };
    private static CheckoutPaymentStartResult Prepare(MasterAppDbContext db, Guid store, string attempt, ParfaitCartQuoteResponse quote) =>
        new ParfaitOrderService(db).BeginCheckoutPayment(store, attempt, new(), quote.Items.Select(item => new ParfaitValidatedCartItem {
            Id = item.Id, Name = item.Name, Slug = item.Slug, Size = item.Size, Quantity = item.Quantity,
            UnitPriceCents = item.UnitPriceCents, InventoryRequirementsJson = item.InventoryRequirementsJson
        }).ToArray(), quote.SubtotalCents, null, null, 0, 0, 0, new DefaultHttpContext());
}
