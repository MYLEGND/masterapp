using System;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Moq;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceTenantBrandIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrainingProductWritesRemainParfaitOnlyEvenWhenRolloutIsEnabled(bool active)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var parfait = new CommerceBusiness { Key = "parfait", DisplayName = "Parfait", IsActive = true };
        var other = new CommerceBusiness { Key = "other-store", DisplayName = "Other", IsActive = true };
        db.AddRange(parfait, other);
        db.SaveChanges();
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(x => x.ContentRootPath).Returns(System.IO.Path.GetTempPath());
        env.SetupGet(x => x.WebRootPath).Returns(System.IO.Path.GetTempPath());
        var products = new ParfaitProductService(new ParfaitStoragePaths(env.Object, new ConfigurationBuilder().Build()), db,
            new Infrastructure.Billing.ClientSubscriptionActivationPolicyOptions { TrainingSubscriptionsEnabled = true });
        var plan = new ParfaitApp.Models.ParfaitProductEditorViewModel
        { Name = "Synthetic training", Slug = "training", PriceCents = 2500, BillingKind = "MonthlyTraining", IsActive = active };
        Assert.Throws<InvalidOperationException>(() => products.SaveProduct(other.Id, plan));
        Assert.Empty(db.CommerceProducts);
        products.SaveProduct(parfait.Id, plan);
        Assert.Equal(parfait.Id, Assert.Single(db.CommerceProducts).CommerceBusinessId);
    }

    [Fact]
    public void MissingBadgeUsesTenantBrandWithoutChangingParfaitBrand()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var parfait = new CommerceBusiness { Key = "parfait", DisplayName = "Parfait", IsActive = true };
        var second = new CommerceBusiness { Key = "gym-one", DisplayName = "One Fitness", IsActive = true };
        db.CommerceBusinesses.AddRange(parfait, second);
        db.CommerceProducts.AddRange(
            new CommerceProduct { CommerceBusinessId = parfait.Id, ExternalProductKey = "p", Name = "Parfait Shirt", Badge = "" },
            new CommerceProduct { CommerceBusinessId = second.Id, ExternalProductKey = "g", Name = "Gym Shirt", Badge = "" },
            new CommerceProduct { CommerceBusinessId = second.Id, ExternalProductKey = "custom", Name = "Custom", Badge = "Limited Edition" });
        db.SaveChanges();

        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(x => x.ContentRootPath).Returns(System.IO.Path.GetTempPath());
        env.SetupGet(x => x.WebRootPath).Returns(System.IO.Path.GetTempPath());
        var storage = new ParfaitStoragePaths(env.Object, new ConfigurationBuilder().Build());
        var products = new ParfaitProductService(storage, db);

        Assert.Equal("Parfait", Assert.Single(products.GetAllProducts(parfait.Id)).Badge);
        var other = products.GetAllProducts(second.Id);
        Assert.Equal("One Fitness", Assert.Single(other, x => x.Id == "g").Badge);
        Assert.Equal("Limited Edition", Assert.Single(other, x => x.Id == "custom").Badge);
        Assert.Throws<InvalidOperationException>(() => products.GetAllProducts(Guid.NewGuid()));
    }
}
