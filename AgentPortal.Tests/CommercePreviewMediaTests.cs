using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Businesses;
using Infrastructure.WebsiteEditing;
using System.Net.Http;
using System.Text.Json;
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
        var tenant = new CommerceBusiness { Key = "synthetic-catalog", IsActive = true, Status = "Active" };
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
                new Dictionary<string, string?> { ["Parfait:StorageRoot"] = root, ["WebsiteRouting:BridgeSecret"] = new string('s', 40) }).Build();
            var storage = new ParfaitStoragePaths(env.Object, config);
            var stores = new CommerceStoreContextService(db, new CommerceBusinessScopeResolver(db), new ParfaitBusinessScopeService(db),
                new WebsiteDomainService(db, Mock.Of<IHttpClientFactory>(), config), config);
            var mw = new CommercePreviewMediaMiddleware(_ => Task.CompletedTask);

            var allowed = new DefaultHttpContext();
            allowed.Request.Path = url;
            allowed.Request.Method = "GET";
            allowed.Response.Body = new MemoryStream();
            allowed.Items[CommerceSharedHostPreviewGate.PreviewMediaBusinessIdItem] = tenant.Id;
            await mw.InvokeAsync(allowed, db, storage, stores);
            Assert.Equal(200, allowed.Response.StatusCode);
            Assert.Equal("image/png", allowed.Response.ContentType);
            Assert.Equal(7, allowed.Response.Body.Length);

            var wrong = new DefaultHttpContext();
            wrong.Request.Path = url;
            wrong.Items[CommerceSharedHostPreviewGate.PreviewMediaBusinessIdItem] = neighbor.Id;
            await mw.InvokeAsync(wrong, db, storage, stores);
            Assert.Equal(404, wrong.Response.StatusCode);

            var direct = new DefaultHttpContext();
            direct.Request.Path = url;
            await mw.InvokeAsync(direct, db, storage, stores);
            Assert.Equal(404, direct.Response.StatusCode);

            var state = new WebsiteContentState { SiteKey = WebsiteEditorSiteKeys.Legend, OwnerKey = WebsiteEditorSiteKeys.GlobalOwnerKey,
                CommerceBusinessId = tenant.Id, ProvisionedStoreBusinessId = tenant.Id };
            var version = new WebsiteContentVersion { StateId = state.Id, DocumentJson = JsonSerializer.Serialize(new WebsiteContentDocument
                { Store = new WebsiteStoreSettings { Enabled = true } }) };
            state.PublishedVersionId = version.Id;
            db.AddRange(state, version);
            await db.SaveChangesAsync();
            DefaultHttpContext LegendRequest(string secret)
            {
                var request = new DefaultHttpContext();
                request.Request.Host = new HostString("masterapp-protect.azurewebsites.net");
                request.Request.Headers[WebsiteRequestHostResolver.OriginalHostHeader] = "mylegnd.com";
                request.Request.Headers[WebsiteRequestHostResolver.BridgeSecretHeader] = secret;
                request.Request.Path = url;
                request.Request.Method = "GET";
                request.Response.Body = new MemoryStream();
                return request;
            }
            var legend = LegendRequest(new string('s', 40));
            await mw.InvokeAsync(legend, db, storage, stores);
            Assert.Equal(200, legend.Response.StatusCode);
            Assert.Equal(7, legend.Response.Body.Length);
            var www = LegendRequest(new string('s', 40));
            www.Request.Headers[WebsiteRequestHostResolver.OriginalHostHeader] = "www.mylegnd.com";
            www.Request.Method = "HEAD";
            await mw.InvokeAsync(www, db, storage, stores);
            Assert.Equal(200, www.Response.StatusCode);
            Assert.Equal("image/png", www.Response.ContentType);
            Assert.Equal(0, www.Response.Body.Length);
            var wrongOrigin = LegendRequest(new string('s', 40));
            wrongOrigin.Request.Host = new HostString("other.azurewebsites.net");
            await mw.InvokeAsync(wrongOrigin, db, storage, stores);
            Assert.Equal(404, wrongOrigin.Response.StatusCode);
            var spoofed = LegendRequest("wrong-secret");
            await mw.InvokeAsync(spoofed, db, storage, stores);
            Assert.Equal(404, spoofed.Response.StatusCode);
            product.CommerceBusinessId = neighbor.Id;
            await db.SaveChangesAsync();
            var foreignImage = LegendRequest(new string('s', 40));
            await mw.InvokeAsync(foreignImage, db, storage, stores);
            Assert.Equal(404, foreignImage.Response.StatusCode);
            product.CommerceBusinessId = tenant.Id;
            state.PublishedVersionId = null;
            await db.SaveChangesAsync();
            var unpublished = LegendRequest(new string('s', 40));
            await mw.InvokeAsync(unpublished, db, storage, stores);
            Assert.Equal(404, unpublished.Response.StatusCode);

            var traversal = new DefaultHttpContext();
            traversal.Request.Path = "/uploads/parfait-products/../cover.png";
            traversal.Items[CommerceSharedHostPreviewGate.PreviewMediaBusinessIdItem] = tenant.Id;
            await mw.InvokeAsync(traversal, db, storage, stores);
            Assert.Equal(404, traversal.Response.StatusCode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
