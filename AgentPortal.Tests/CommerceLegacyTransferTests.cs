using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Legend.Commerce;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Moq;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceLegacyTransferTests
{
    [Fact]
    public async Task PlanCopiesNothingAndAppliedCopyIsHashVerifiedAndIdempotent()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "parfait", DisplayName = "Parfait", IsActive = true };
        var product = new CommerceProduct { CommerceBusinessId = business.Id, Name = "Test" };
        var image = new CommerceProductImage { CommerceProductId = product.Id,
            ImageUrl = "/uploads/parfait-products/product-1/img.png" };
        db.CommerceBusinesses.Add(business);
        db.CommerceProducts.Add(product);
        db.CommerceProductImages.Add(image);
        await db.SaveChangesAsync();

        var root = Path.Combine(Path.GetTempPath(), "commerce-copy-test-" + Guid.NewGuid().ToString("N"));
        var source = Paths(Path.Combine(root, "old"));
        var target = Paths(Path.Combine(root, "new"));
        try
        {
            var sourceFile = Path.Combine(source.UploadRoot, "product-1", "img.png");
            Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
            await File.WriteAllBytesAsync(sourceFile, new byte[] { 1, 2, 3, 4 });
            Directory.CreateDirectory(source.DataRoot);
            await File.WriteAllTextAsync(source.CustomerAutomationsPath, "{\"workflows\":[]}");
            var migration = new CommerceLegacyTransfer(db);

            var plan = await migration.ReconcileAsync(business.Id, "parfait",
                source, target, CommerceTransferMode.PlanOnly);
            Assert.Equal(2, plan.Files);
            Assert.Equal(2, plan.FilesToCopy);
            Assert.False(plan.CopyAttempted);
            Assert.Equal(0, plan.VerifiedDestinationFiles);
            Assert.Equal(64, plan.ManifestSha256.Length);
            Assert.False(Directory.Exists(target.RootPath));

            var copied = await migration.ReconcileAsync(business.Id, "parfait",
                source, target, CommerceTransferMode.CopyNoOverwrite);
            Assert.True(copied.CopyAttempted);
            Assert.Equal(2, copied.VerifiedDestinationFiles);
            Assert.Equal(plan.ManifestSha256, copied.ManifestSha256);
            Assert.Equal(await File.ReadAllBytesAsync(sourceFile),
                await File.ReadAllBytesAsync(Path.Combine(target.UploadRoot, "product-1", "img.png")));
            Assert.Equal(await File.ReadAllTextAsync(source.CustomerAutomationsPath),
                await File.ReadAllTextAsync(target.CustomerAutomationsPath));

            var repeated = await migration.ReconcileAsync(business.Id, "parfait",
                source, target, CommerceTransferMode.CopyNoOverwrite);
            Assert.Equal(2, repeated.ExistingIdenticalFiles);
            Assert.Equal(0, repeated.FilesToCopy);
            Assert.Equal(2, repeated.VerifiedDestinationFiles);
            Assert.Equal(copied.ManifestSha256, repeated.ManifestSha256);

            await File.WriteAllTextAsync(target.CustomerAutomationsPath, "{\"conflict\":true}");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                migration.ReconcileAsync(business.Id, "parfait", source, target,
                    CommerceTransferMode.CopyNoOverwrite));
            Assert.True(File.Exists(sourceFile));

            // Existing source files must never be modified or removed.
            var original = await File.ReadAllBytesAsync(sourceFile);
            await File.WriteAllBytesAsync(sourceFile, new byte[] { 9, 9, 9, 9 });
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                migration.ReconcileAsync(business.Id, "parfait", source, target,
                    CommerceTransferMode.PlanOnly));
            Assert.True(File.Exists(sourceFile));
            Assert.NotEqual(original, await File.ReadAllBytesAsync(sourceFile));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static ParfaitStoragePaths Paths(string root)
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(x => x.ContentRootPath).Returns(root);
        env.SetupGet(x => x.WebRootPath).Returns(root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Parfait:StorageRoot"] = root }).Build();
        return new ParfaitStoragePaths(env.Object, config);
    }
}
