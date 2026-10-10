using System;
using System.Collections.Generic;
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
