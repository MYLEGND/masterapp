using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Domain.Entities;
using Legend.Commerce;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceCutoverInventoryTests
{
    [Fact]
    public async Task AtomicAutomationStoragePreservesExistingBytesAndSerializesScopedWriters()
    {
        var root = Path.Combine(Path.GetTempPath(), "commerce-atomic-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "parfait-customer-automations.json");
        try
        {
            var first = CommerceAutomationFileStore.ReadOrCreate(path, "{\"version\":1}");
            Assert.Equal("{\"version\":1}", first);
            Assert.Equal("{\"version\":1}", CommerceAutomationFileStore.ReadOrCreate(path, "{\"version\":2}"));

            var writes = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
                CommerceAutomationFileStore.WriteAtomic(path, "{\"version\":" + i + "}"))).ToArray();
            await Task.WhenAll(writes);
            var actual = await File.ReadAllTextAsync(path);
            using var json = System.Text.Json.JsonDocument.Parse(actual);
            var value = json.RootElement.GetProperty("version").GetInt32();
            Assert.InRange(value, 0, 19);
            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
            Assert.Same(CommerceAutomationFileStore.SyncRoot(path), CommerceAutomationFileStore.SyncRoot(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ReadsOnlyOneTenantAndDoesNotProvisionMissingAutomationFiles()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var first = new CommerceBusiness { Id = Guid.NewGuid(), Key = "parfait", IsActive = true };
        var second = new CommerceBusiness { Id = Guid.NewGuid(), Key = "other", IsActive = true };
        db.CommerceBusinesses.AddRange(first, second);
        db.CommerceProducts.AddRange(
            new CommerceProduct { Id = Guid.NewGuid(), CommerceBusinessId = first.Id, Name = "First" },
            new CommerceProduct { Id = Guid.NewGuid(), CommerceBusinessId = second.Id, Name = "Other" });
        db.CommerceOrders.AddRange(
            new CommerceOrder { Id = Guid.NewGuid(), CommerceBusinessId = first.Id, OrderNumber = "one" },
            new CommerceOrder { Id = Guid.NewGuid(), CommerceBusinessId = second.Id, OrderNumber = "two" });
        await db.SaveChangesAsync();

        var root = Path.Combine(Path.GetTempPath(), "commerce-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var env = new Mock<IWebHostEnvironment>();
            env.SetupGet(x => x.ContentRootPath).Returns(root);
            env.SetupGet(x => x.WebRootPath).Returns(root);
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new[] {
                    new KeyValuePair<string, string?>("Parfait:StorageRoot", root)
                }).Build();
            var storage = new ParfaitApp.Services.ParfaitStoragePaths(env.Object, config);
            var probe = new CommerceCutoverInventory(db, storage);

            var a = await probe.ReadAsync(first.Id, first.Key);
            var b = await probe.ReadAsync(second.Id, second.Key);

            Assert.Equal(1, a.Products);
            Assert.Equal(1, b.Products);
            Assert.Equal(1, a.Orders);
            Assert.Equal(1, b.Orders);
            Assert.False(a.AutomationFileExists);
            Assert.False(b.AutomationFileExists);
            Assert.False(Directory.Exists(root));

            await Assert.ThrowsAsync<InvalidOperationException>(() => probe.ReadAsync(first.Id, second.Key));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImageManifestMatchesExactFileBytesAndRejectsTraversal()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var tenant = new CommerceBusiness { Key = "parfait", IsActive = true };
        var product = new CommerceProduct { CommerceBusinessId = tenant.Id, Name = "Test" };
        var image = new CommerceProductImage
        {
            CommerceProductId = product.Id,
            ImageUrl = "/uploads/parfait-products/product-1/original.png"
        };
        db.CommerceBusinesses.Add(tenant);
        db.CommerceProducts.Add(product);
        db.CommerceProductImages.Add(image);
        await db.SaveChangesAsync();

        var root = Path.Combine(Path.GetTempPath(), "commerce-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var folder = Path.Combine(root, "uploads", "parfait-products", "product-1");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "original.png");
            await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4, 5 });

            var env = new Mock<IWebHostEnvironment>();
            env.SetupGet(x => x.ContentRootPath).Returns(root);
            env.SetupGet(x => x.WebRootPath).Returns(root);
            var storage = new ParfaitApp.Services.ParfaitStoragePaths(env.Object,
                new ConfigurationBuilder().AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string?>("Parfait:StorageRoot", root)
                }).Build());
            var inventory = new CommerceCutoverInventory(db, storage);
            var before = await inventory.ReadAsync(tenant.Id, "parfait");
            Assert.Equal(1, before.LocalProductImages);
            Assert.Equal(1, before.VerifiedLocalProductImages);
            Assert.Equal(0, before.MissingLocalProductImages);
            Assert.Equal(64, before.ProductMediaSha256!.Length);

            await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4, 6 });
            var after = await inventory.ReadAsync(tenant.Id, "parfait");
            Assert.NotEqual(before.ProductMediaSha256, after.ProductMediaSha256);
            Assert.Empty(storage.ResolveImagePhysicalPaths("/uploads/parfait-products/../secret.png"));
            Assert.Empty(storage.ResolveImagePhysicalPaths("/uploads/parfait-products/%2e%2e/secret.png"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IncludesExistingWorkflowCountsAndDigestWithoutWritingToFile()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var tenant = new CommerceBusiness { Key = "parfait", IsActive = true };
        db.CommerceBusinesses.Add(tenant);
        await db.SaveChangesAsync();

        var root = Path.Combine(Path.GetTempPath(), "commerce-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var folder = Path.Combine(root, "data");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "parfait-customer-automations.json");
            var value = "{\"workflows\":[{\"name\":\"Synthetic\"}],\"cartLeads\":[],\"dispatches\":[]}";
            await File.WriteAllTextAsync(path, value);
            var originalBytes = await File.ReadAllBytesAsync(path);

            var env = new Mock<IWebHostEnvironment>();
            env.SetupGet(x => x.ContentRootPath).Returns(root);
            env.SetupGet(x => x.WebRootPath).Returns(root);
            var storage = new ParfaitApp.Services.ParfaitStoragePaths(env.Object,
                new ConfigurationBuilder().AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string?>("Parfait:StorageRoot", root)
                }).Build());
            var snapshot = await new CommerceCutoverInventory(db, storage).ReadAsync(tenant.Id, "parfait");

            Assert.True(snapshot.AutomationFileExists);
            Assert.True(snapshot.AutomationFileReadable);
            Assert.Equal(1, snapshot.AutomationWorkflows);
            Assert.Equal(0, snapshot.AutomationDispatches);
            Assert.Equal(64, snapshot.AutomationSha256!.Length);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
            Assert.True(snapshot.RequiresFileMigration);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
