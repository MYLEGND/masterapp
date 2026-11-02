using System;
using System.IO;
using System.Linq;
using Domain.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ParfaitApp.Models;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ParfaitProductPersistenceTests
{
    [Fact]
    public void EditingExistingProduct_PreservesChildIdentity_AndPersistsAddedAndRemovedRows()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "synthetic-store", DisplayName = "Synthetic Store" };
        var other = new CommerceBusiness { Key = "other-store", DisplayName = "Other Store" };
        db.AddRange(business, other);
        var product = new CommerceProduct { CommerceBusinessId = business.Id, ExternalProductKey = "top", Name = "Top", Slug = "top", PriceCents = 4900 };
        var image = new CommerceProductImage { ExternalImageKey = "photo", ImageUrl = "/synthetic.png", IsPrimary = true };
        product.Images.Add(image);
        var otherProduct = new CommerceProduct { CommerceBusinessId = other.Id, ExternalProductKey = "top", Name = "Other Top", Slug = "top" };
        db.AddRange(product, otherProduct);
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(x => x.ContentRootPath).Returns(Path.GetTempPath());
        environment.SetupGet(x => x.WebRootPath).Returns(Path.GetTempPath());
        var paths = new ParfaitStoragePaths(environment.Object, new ConfigurationBuilder().Build());
        var service = new ParfaitProductService(paths, db);
        var edit = Assert.Single(service.GetAllProducts(business.Id));
        edit.Description = "Updated description";
        edit.InventoryBySize[0].StockQuantity = 12;
        edit.DiscountCodes.Add(new ParfaitProductDiscountCodeEditorViewModel { Id = "launch", Code = "LAUNCH", Amount = 10 });
        service.SaveProduct(business.Id, edit);
        db.ChangeTracker.Clear();

        var saved = db.CommerceProducts.Include(x => x.Images).Include(x => x.InventoryItems).Include(x => x.Discounts).Single(x => x.Id == product.Id);
        Assert.Equal("Updated description", saved.Description);
        Assert.Equal(image.Id, Assert.Single(saved.Images).Id);
        var stock = saved.InventoryItems.Single(x => x.ExternalInventoryKey == edit.InventoryBySize[0].Id);
        Assert.Equal(12, stock.StockQuantity);
        var discount = Assert.Single(saved.Discounts);
        Assert.Equal("LAUNCH", discount.Code);
        var stockId = stock.Id;
        db.ChangeTracker.Clear();

        edit = Assert.Single(service.GetAllProducts(business.Id));
        edit.InventoryBySize[0].StockQuantity = 7;
        edit.DiscountCodes.Clear();
        service.SaveProduct(business.Id, edit);
        db.ChangeTracker.Clear();
        Assert.Equal(7, db.CommerceProductInventoryItems.Single(x => x.Id == stockId).StockQuantity);
        Assert.False(db.CommerceProductDiscounts.Any(x => x.Id == discount.Id));
        Assert.Equal(image.Id, db.CommerceProductImages.Single(x => x.CommerceProductId == product.Id).Id);
        Assert.Equal("Other Top", db.CommerceProducts.Single(x => x.Id == otherProduct.Id).Name);

        edit = Assert.Single(service.GetAllProducts(business.Id));
        edit.Name = "Must not persist";
        edit.InventoryBySize[1].Id = edit.InventoryBySize[0].Id;
        Assert.Throws<InvalidOperationException>(() => service.SaveProduct(business.Id, edit));
        db.SaveChanges();
        Assert.Equal("Top", db.CommerceProducts.AsNoTracking().Single(x => x.Id == product.Id).Name);

        edit = Assert.Single(service.GetAllProducts(business.Id));
        edit.DiscountCodes = [new() { Id = "duplicate", Code = "ONE", Amount = 10 }, new() { Id = "duplicate", Code = "TWO", Amount = 20 }];
        Assert.Throws<InvalidOperationException>(() => service.SaveProduct(business.Id, edit));
        Assert.Empty(db.CommerceProductDiscounts.Where(x => x.CommerceProductId == product.Id));
    }
}
