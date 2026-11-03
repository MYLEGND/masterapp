using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class CommerceManagementCapabilityPolicyTests
{
    [Fact]
    public async Task OrderSearchFindsOlderRecordsWithinTenantAndKeepsFullStoreMetrics()
    {
        await using var f = new NavigationFixture("owner", "Active", true, relational: true);
        var old = new CommerceOrder {
            CommerceBusinessId = f.Business.Id, OrderNumber = "ORDER-OLDER-001",
            FirstName = "Ada", LastName = "Lovelace", Email = "ada@example.test", Phone = "+1 (602) 555-0188",
            PaymentStatus = "Paid", TotalCents = 100, CreatedUtc = DateTime.UtcNow.AddYears(-2)
        };
        f.Db.CommerceOrders.Add(old);
        f.Db.CommerceOrders.AddRange(Enumerable.Range(0, 300).Select(i => new CommerceOrder {
            CommerceBusinessId = f.Business.Id, OrderNumber = "RECENT-" + i,
            PaymentStatus = "Paid", TotalCents = 200, CreatedUtc = DateTime.UtcNow.AddMinutes(-i)
        }));
        var foreign = new CommerceBusiness { Key = "foreign-search", DisplayName = "Foreign", IsActive = true };
        f.Db.CommerceBusinesses.Add(foreign);
        f.Db.CommerceOrders.Add(new CommerceOrder {
            CommerceBusinessId = foreign.Id, OrderNumber = "FOREIGN-001", FirstName = "Ada",
            LastName = "Lovelace", Email = "ada@example.test", Phone = "+1 (602) 555-0188"
        });
        await f.Db.SaveChangesAsync();
        var service = new ParfaitOrderService(f.Db);
        foreach (var query in new[] { "ORDER-OLDER-001", " older- ", "ADA LOVE", "ADA@EXAMPLE", "6025550188", "+1 (602) 555-0188", "0016025550188", "602.555.0188", "555-0188" })
            Assert.Equal(old.Id, Assert.Single(service.SearchOrders(f.Business.Id, query)).CommerceOrderId);
        Assert.Empty(service.SearchOrders(f.Business.Id, "FOREIGN-001"));
        Assert.Empty(service.SearchOrders(f.Business.Id, "not-an-order"));
        Assert.Empty(service.SearchOrders(f.Business.Id, "9995550199"));
        Assert.Equal(301, service.SearchOrders(f.Business.Id, " ").Count);

        f.Controller.Request.Path = "/commerce/manage/orders";
        var view = Assert.IsType<ViewResult>(await f.Controller.Orders(f.Ticket, default, "ORDER-OLDER-001"));
        var model = Assert.IsType<ParfaitOrderAdminViewModel>(view.Model);
        Assert.Equal(old.Id, Assert.Single(model.SearchResults!).CommerceOrderId);
        Assert.Equal(301, model.Orders.Count);
        Assert.Equal(301, model.PaidOrderCount);
        Assert.Equal(60100, model.RevenueCents);
        Assert.Equal("ORDER-OLDER-001", model.SearchQuery);
        f.Member.Status = "Revoked";
        await f.Db.SaveChangesAsync();
        Assert.IsType<UnauthorizedResult>(await f.Controller.Orders(f.Ticket, default, "ORDER-OLDER-001"));
    }
}
