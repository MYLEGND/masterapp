using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Legend.Commerce;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceCutoverStartupProofTests
{
    [Fact]
    public async Task CutoverAdmissionChecksOriginalManifestAndFailsOnWrongFiles()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var tenant = new CommerceBusiness { Key = "parfait", DisplayName = "Parfait", IsActive = true, Status = "Active" };
        var product = new CommerceProduct { CommerceBusinessId = tenant.Id, Name = "Shirt" };
        db.CommerceBusinesses.Add(tenant);
        db.CommerceProducts.Add(product);
        db.CommerceProductImages.Add(new CommerceProductImage {
            CommerceProductId = product.Id, ImageUrl = "/uploads/parfait-products/shirt/cover.png"
        });
        db.CommerceProductImages.Add(new CommerceProductImage {
            CommerceProductId = product.Id, ImageUrl = "/uploads/parfait-products/shirt/detail.png"
        });
        db.SaveChanges();

        var root = Path.Combine(Path.GetTempPath(), "commerce-startup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var target = Path.Combine(root, "uploads", "parfait-products", "shirt");
            Directory.CreateDirectory(target);
            var image = Path.Combine(target, "cover.png");
            await File.WriteAllBytesAsync(image, new byte[]{ 1, 2, 3, 4 });
            var detail = Path.Combine(target, "detail.png");
            await File.WriteAllBytesAsync(detail, new byte[]{ 4, 3, 2, 1 });
            var env = new Mock<IWebHostEnvironment>();
            env.SetupGet(e => e.ContentRootPath).Returns(root);
            env.SetupGet(e => e.WebRootPath).Returns(root);
            var cfgBase = new Dictionary<string,string?> {
                ["Parfait:StorageRoot"] = root,
                ["Commerce:SharedHostCutover:Enabled"] = "true",
                ["Commerce:SharedHostCutover:BusinessId"] = tenant.Id.ToString(),
                ["Commerce:SharedHostCutover:Hostname"] = "shopparfait.com"
            };
            var storage = new ParfaitStoragePaths(env.Object,
                new ConfigurationBuilder().AddInMemoryCollection(cfgBase).Build());
            var digest = await new CommerceLegacyTransfer(db)
                .ReadManifestAsync(tenant.Id, "parfait", storage);
            cfgBase["Commerce:SharedHostCutover:ReconciledManifestSha256"] = digest;
            var config = new ConfigurationBuilder().AddInMemoryCollection(cfgBase).Build();
            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddSingleton(storage);
            using var provider = services.BuildServiceProvider();
            var gate = new CommerceCutoverStartupProof(provider.GetRequiredService<IServiceScopeFactory>(), config);
            await gate.StartAsync(default);

            // A pre-v2 receipt proved only an unordered collection of bytes.
            var oldEntries = new[] { image, detail }.Select(path =>
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() + ":4")
                .OrderBy(x => x, StringComparer.Ordinal);
            config["Commerce:SharedHostCutover:ReconciledManifestSha256"] =
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\\n", oldEntries)))).ToLowerInvariant();
            await Assert.ThrowsAsync<InvalidOperationException>(() => gate.StartAsync(default));
            config["Commerce:SharedHostCutover:ReconciledManifestSha256"] = digest;

            // The same file count, lengths and content-hash multiset must not
            // admit images whose bytes have moved to the wrong catalog URLs.
            await File.WriteAllBytesAsync(image, new byte[]{ 4, 3, 2, 1 });
            await File.WriteAllBytesAsync(detail, new byte[]{ 1, 2, 3, 4 });
            await Assert.ThrowsAsync<InvalidOperationException>(() => gate.StartAsync(default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
