using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteRuntime;
using Legend.Commerce;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Moq;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommercePreviewMediaTests
{
    [Fact]
    public async Task OnlyVerifiedBusinessCanReadItsOwnCatalogImage()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var tenant = new CommerceBusiness { Key = "parfait", IsActive = true };
        var neighbor = new CommerceBusiness { Key = "another", IsActive = true };
        var product = new CommerceProduct { CommerceBusinessId = tenant.Id, Name = "Parfait" };
        const string url = "/uploads/parfait-products/shirt/cover.png";
        db.CommerceBusinesses.AddRange(tenant, neighbor);
        db.CommerceProducts.Add(product);
        db.CommerceProductImages.Add(new CommerceProductImage { CommerceProductId = product.Id, ImageUrl = url });
        db.SaveChanges();

        var root = Path.Combine(Path.GetTempPath(), "commerce-media-" + Guid.NewGuid().ToString("N"));
        try
        {
            var folder = Path.Combine(root, "uploads", "parfait-products", "shirt");
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, "cover.png"), new byte[]{137, 80, 78, 71, 1, 2, 3});
            var env = new Mock<IWebHostEnvironment>();
            env.SetupGet(e => e.ContentRootPath).Returns(root);
            env.SetupGet(e => e.WebRootPath).Returns(root);
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Parfait:StorageRoot"] = root }).Build();
            var storage = new ParfaitStoragePaths(env.Object, config);
            var mw = new CommercePreviewMediaMiddleware(_ => Task.CompletedTask);

            var allowed = new DefaultHttpContext();
            allowed.Request.Path = url;
            allowed.Request.Method = "GET";
            allowed.Response.Body = new MemoryStream();
            allowed.Items[CommerceSharedHostPreviewGate.PreviewMediaBusinessIdItem] = tenant.Id;
            await mw.InvokeAsync(allowed, db, storage);
            Assert.Equal(200, allowed.Response.StatusCode);
            Assert.Equal("image/png", allowed.Response.ContentType);
            Assert.Equal(7, allowed.Response.Body.Length);

            var wrong = new DefaultHttpContext();
            wrong.Request.Path = url;
            wrong.Items[CommerceSharedHostPreviewGate.PreviewMediaBusinessIdItem] = neighbor.Id;
            await mw.InvokeAsync(wrong, db, storage);
            Assert.Equal(404, wrong.Response.StatusCode);

            var direct = new DefaultHttpContext();
            direct.Request.Path = url;
            await mw.InvokeAsync(direct, db, storage);
            Assert.Equal(404, direct.Response.StatusCode);

            var traversal = new DefaultHttpContext();
            traversal.Request.Path = "/uploads/parfait-products/../cover.png";
            traversal.Items[CommerceSharedHostPreviewGate.PreviewMediaBusinessIdItem] = tenant.Id;
            await mw.InvokeAsync(traversal, db, storage);
            Assert.Equal(404, traversal.Response.StatusCode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
