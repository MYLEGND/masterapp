using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Commerce;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using ParfaitApp.Controllers;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class StoreCartCommandTests
{
    [Fact]
    public async Task AcceptedCommandReplaysSameReceiptAndRejectsConflictingIdentity()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid();
        var command = new StoreCartController.CartAddItemRequest { EventId = id.ToString("N"), ProductId = "tee", Size = "M", Quantity = 2 };
        Assert.IsType<OkObjectResult>(await fixture.Controller.AddItem(command, default));
        var row = Assert.Single(fixture.Db.AnalyticsEvents);
        var eventId = row.EventId;
        var time = row.EventUtc;
        command.EventId = id.ToString("D");
        Assert.IsType<OkObjectResult>(await fixture.Controller.AddItem(command, default));
        row = Assert.Single(fixture.Db.AnalyticsEvents);
        Assert.Equal(eventId, row.EventId);
        Assert.Equal(time, row.EventUtc);
        Assert.Equal(2, fixture.CartQuantity());
        command.Quantity = 1;
        Assert.IsType<ConflictResult>(await fixture.Controller.AddItem(command, default));
        Assert.Single(fixture.Db.AnalyticsEvents);
        Assert.Equal(2, fixture.CartQuantity());
    }

    [Fact]
    public async Task NearStockLimitRecordsOnlyAcceptedIncrementAndZeroIncrementAddsNoEvent()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < 3; i++)
            Assert.IsType<OkObjectResult>(await fixture.Controller.AddItem(new() { EventId = Guid.NewGuid().ToString(), ProductId = "tee", Size = "M", Quantity = 2 }, default));
        Assert.Equal(3, fixture.CartQuantity());
        Assert.Equal(2, fixture.Db.AnalyticsEvents.Count());
        var quantities = fixture.Db.AnalyticsEvents.AsEnumerable().Select(row =>
            Infrastructure.Analytics.CanonicalAdvertisingEventProjection.ReadInt64(row.MetadataJson, "quantity")).ToArray();
        Assert.Contains(2L, quantities);
        Assert.Contains(1L, quantities);
    }

    [Fact]
    public async Task InvalidScopeAndForeignProductCannotMutateCartOrEmitOutcome()
    {
        using var fixture = new Fixture();
        var command = new StoreCartController.CartAddItemRequest { EventId = Guid.NewGuid().ToString(), ProductId = "foreign", Size = "M", Quantity = 1 };
        Assert.IsType<BadRequestResult>(await fixture.Controller.AddItem(command, default));
        command.ProductId = "tee";
        Assert.IsType<NotFoundResult>(await fixture.Controller.AddScopedItem("other-business", command, default));
        Assert.Empty(fixture.Db.AnalyticsEvents);
        Assert.Equal(0, fixture.CartQuantity());
    }

    private sealed class Fixture : IDisposable
    {
        public MasterAppDbContext Db { get; } = ControllerTestHelpers.BuildDb();
        public StoreCartController Controller { get; }
        private readonly MemorySession session = new();
        private readonly Guid businessId = Guid.NewGuid();
        public Fixture()
        {
            var business = new CommerceBusiness { Id = businessId, Key = "parfait", DisplayName = "Parfait", IsActive = true, Status = "Active" };
            Db.CommerceBusinesses.Add(business);
            Db.CommerceBusinessSettings.Add(new CommerceBusinessSettings { CommerceBusiness = business });
            Db.CommerceProducts.Add(new CommerceProduct { CommerceBusiness = business, ExternalProductKey = "tee", Name = "Tee", Slug = "tee", PriceCents = 1000, IsActive = true,
                InventoryItems = [new CommerceProductInventoryItem { ExternalInventoryKey = "tee-m", Size = "M", IsEnabled = true, StockQuantity = 3 }] });
            Db.SaveChanges();
            var config = new ConfigurationBuilder().Build();
            var environment = new Mock<IWebHostEnvironment>();
            environment.SetupGet(x => x.ContentRootPath).Returns(System.IO.Path.GetTempPath());
            environment.SetupGet(x => x.WebRootPath).Returns(System.IO.Path.GetTempPath());
            var stores = new CommerceStoreContextService(Db, new CommerceBusinessScopeResolver(Db), new ParfaitBusinessScopeService(Db),
                new WebsiteDomainService(Db, Mock.Of<IHttpClientFactory>(), config), config);
            Controller = new StoreCartController(stores, new ParfaitProductService(new ParfaitStoragePaths(environment.Object, config), Db), new CommerceSignalService(Db));
            var http = new DefaultHttpContext { Session = session };
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("shopparfait.com");
            Controller.ControllerContext = new ControllerContext { HttpContext = http };
        }
        public int CartQuantity() => JsonSerializer.Deserialize<Dictionary<string, int>>(session.GetString($"cart:{businessId:N}") ?? "{}")!.Values.Sum();
        public void Dispose() => Db.Dispose();
    }
    private sealed class MemorySession : ISession
    {
        private readonly Dictionary<string, byte[]> values = new();
        public string Id { get; } = Guid.NewGuid().ToString();
        public bool IsAvailable => true;
        public IEnumerable<string> Keys => values.Keys;
        public void Clear() => values.Clear();
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) => values.Remove(key);
        public void Set(string key, byte[] value) => values[key] = value;
        public bool TryGetValue(string key, out byte[] value) => values.TryGetValue(key, out value!);
    }
}
