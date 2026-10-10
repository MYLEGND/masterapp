using System;
using System.Reflection;
using Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ParfaitApp.Models;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceOrderMailTenantIsolationTests
{
    [Fact]
    public void SharedReceiptUsesOrderTenantBrandAndNotParfaitNotificationInbox()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var parfait = new CommerceBusiness {
            Key = "parfait", DisplayName = "Parfait", OwnerEmail = "parfait@mylegnd.com",
            IsActive = true, Status = "Active"
        };
        var independent = new CommerceBusiness {
            Key = "gym-one", DisplayName = "One Fitness", OwnerEmail = "owner@fitness.example",
            IsActive = true, Status = "Active"
        };
        db.CommerceBusinesses.AddRange(parfait, independent);
        db.SaveChanges();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new System.Collections.Generic.Dictionary<string, string?> {
                ["Contact:WebsiteName"] = "Shop Parfait",
                ["Commerce:OrdersInbox"] = "parfait@mylegnd.com"
            }).Build();
        var mail = new GraphMailService(configuration, NullLogger<GraphMailService>.Instance, db);

        var scoped = Resolve(mail, independent.Id);
        Assert.Equal("One Fitness", scoped.StoreName);
        Assert.Equal("owner@fitness.example", scoped.OrdersInbox);
        Assert.False(scoped.IsParfait);

        var original = Resolve(mail, parfait.Id);
        Assert.Equal("Shop Parfait", original.StoreName);
        Assert.True(original.IsParfait);
    }

    [Fact]
    public void UnknownBusinessCannotFallBackToParfaitForSensitiveMail()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var mail = new GraphMailService(new ConfigurationBuilder().Build(),
            NullLogger<GraphMailService>.Instance, db);
        var error = Assert.Throws<TargetInvocationException>(() => Resolve(mail, Guid.NewGuid()));
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    private static (string StoreName, string? OrdersInbox, bool IsParfait) Resolve(
        GraphMailService mail, Guid businessId)
    {
        var method = typeof(GraphMailService).GetMethod("ResolveMerchant",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException("Canonical merchant mail identity is missing.");
        var order = new ParfaitOrderRecord {
            CommerceBusinessId = businessId,
            OrderNumber = "TEST-001",
            FirstName = "Example", LastName = "Customer", Email = "buyer@example.com",
            Phone = "000", AddressLine1 = "Line", City = "City", State = "AZ", PostalCode = "85001"
        };
        return ((string StoreName, string? OrdersInbox, bool IsParfait))method.Invoke(mail, [order])!;
    }
}
