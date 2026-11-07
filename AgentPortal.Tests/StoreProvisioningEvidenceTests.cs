using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Infrastructure.Migrations;
using Infrastructure.WebsiteEditing;
using Infrastructure.WebsiteEditing.Controllers;
using Infrastructure.WebsitePublishing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class StoreProvisioningEvidenceTests
{
    [Fact]
    public async Task Explicit_enable_is_required_and_remove_and_snapshot_retirement_preserve_exact_binding()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var founder = Guid.NewGuid().ToString();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Founder:Oid"] = founder }).Build();
        using var protector = new WebsiteEditorTicketProtector(new EphemeralDataProtectionProvider());
        var business = new CommerceBusiness { Key = "synthetic-foundation", IsActive = true, Status = "Active" };
        var state = new WebsiteContentState { SiteKey = "legend", OwnerKey = WebsiteEditorSiteKeys.GlobalOwnerKey,
            CommerceBusinessId = business.Id, DraftJson = JsonSerializer.Serialize(new WebsiteContentDocument(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        db.AddRange(business, state);
        await db.SaveChangesAsync();
        var stores = new CommerceStoreContextService(db, new CommerceBusinessScopeResolver(db), new ParfaitBusinessScopeService(db),
            new WebsiteDomainService(db, Mock.Of<IHttpClientFactory>(), config), config);
        var scope = new CommerceManagementScope(business.Id, business.Key, state.Id, state.SiteKey, state.OwnerKey, "https://mylegnd.com");
        Assert.False(await stores.HasProvisionedStoreAsync(scope));
        var services = new ServiceCollection().AddSingleton(new WebsiteCommerceScopeService(db, Mock.Of<ICommerceBusinessProvisioningService>())).BuildServiceProvider();
        var controller = new WebsitePlatformController(db, protector, config) { ControllerContext = new ControllerContext {
            HttpContext = new DefaultHttpContext { RequestServices = services } } };
        var ticket = protector.Protect(new WebsiteEditorTicket("legend", state.OwnerKey, null, true, DateTime.UtcNow.AddMinutes(2), business.Id, founder));
        Assert.IsType<ConflictObjectResult>(await controller.EnableStore(new(ticket, 99)));
        Assert.False(await stores.HasProvisionedStoreAsync(scope));
        Assert.IsType<OkObjectResult>(await controller.EnableStore(new(ticket, state.Revision)));
        Assert.True(await stores.HasProvisionedStoreAsync(scope));
        Assert.Null(state.PublishedVersionId);
        Assert.IsType<OkObjectResult>(await controller.RemoveStore(new(ticket, state.Revision)));
        for (var i = 0; i < 60; i++) db.Add(new WebsiteContentVersion { StateId = state.Id, Revision = i });
        await db.SaveChangesAsync();
        Assert.Equal(10, await WebsiteSnapshotRetention.StageAsync(db, state));
        await db.SaveChangesAsync();
        Assert.True(await stores.HasProvisionedStoreAsync(scope));
        state.CommerceBusinessId = Guid.NewGuid();
        await db.SaveChangesAsync();
        Assert.False(await stores.HasProvisionedStoreAsync(scope));
        Assert.False(await stores.HasProvisionedStoreAsync(scope with { CommerceBusinessId = state.CommerceBusinessId.Value }));
    }

    [Fact]
    public async Task Legacy_migration_requires_positive_boolean_evidence_and_preserves_unknowns()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var business = new CommerceBusiness { Key = "synthetic-backfill" };
        db.Add(business);
        var cases = new[] { "{\"store\":{\"enabled\":true}}", "{\"store\":{\"enabled\":false}}", "{}", "malformed", "{\"store\":{\"enabled\":\"true\"}}", "{\"store\":{\"enabled\":true,\"enabled\":false}}", "{\"store\":{\"enabled\":true},\"Store\":{\"enabled\":false}}", "{\"store\":{\"enabled\":true,\"Enabled\":false}}", "{\"Store\":{\"enabled\":true}}", "{\"store\":{\"enabled \":true}}", "{\"store \":{\"enabled\":true}}", "{\"store\":{\"enabled\\t\":true}}" };
        var ids = new List<Guid>();
        foreach (var json in cases)
        {
            var state = new WebsiteContentState { CommerceBusinessId = business.Id, SiteKey = "business", OwnerKey = Guid.NewGuid().ToString(), DraftJson = json };
            ids.Add(state.Id); db.Add(state);
        }
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE WebsiteContentState DROP COLUMN ProvisionedStoreBusinessId");
        var migration = new ScopedStoreProvisioningEvidence { ActiveProvider = "Microsoft.EntityFrameworkCore.Sqlite" };
        foreach (var command in db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, db.GetService<IDesignTimeModel>().Model))
        {
            await using var sql = connection.CreateCommand();
            sql.CommandText = command.CommandText;
            await sql.ExecuteNonQueryAsync();
        }
        db.ChangeTracker.Clear();
        for (var i = 0; i < ids.Count; i++)
            Assert.Equal(i == 0 ? business.Id : (Guid?)null, (await db.Set<WebsiteContentState>().SingleAsync(x => x.Id == ids[i])).ProvisionedStoreBusinessId);
        Assert.Throws<NotSupportedException>(() => _ = migration.DownOperations);
    }
}
