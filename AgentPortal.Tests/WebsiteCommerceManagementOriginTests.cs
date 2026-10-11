using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Infrastructure.WebsiteEditing.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentPortal.Tests;

public sealed class WebsiteCommerceManagementOriginTests
{
    [Theory]
    [InlineData(null, "https://masterapp-protect.azurewebsites.net")]
    [InlineData("https://protect.mylegnd.com/", "https://protect.mylegnd.com")]
    public async Task PrivateManagementUsesSharedApiWhilePublicLinksKeepBusinessDomain(string? apiBase, string expected)
    {
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var businessId = Guid.NewGuid();
        db.Add(new WebsiteDomainBinding { CommerceBusinessId = businessId, Hostname = "store.example.org",
            Status = "active", CertificateStatus = "active", LastCheckedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["WebsiteContentApiBaseUrl"] = apiBase, ["Commerce:PublicBaseUrl"] = "https://legacy.example.org"
        }).Build();
        var controller = new WebsitePlatformController(db, null!, config);
        var method = typeof(WebsitePlatformController).GetMethod("StorePayloadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = await (Task<object>)method.Invoke(controller, new object?[] {
            WebsiteEditorSiteKeys.Business, new WebsiteContentDocument(),
            new WebsiteCommerceScope(businessId, "test-store", "Test Store"), "synthetic+ticket", CancellationToken.None
        })!;
        var payload = JsonSerializer.SerializeToElement(result);
        Assert.Equal("https://store.example.org/store", payload.GetProperty("storefrontUrl").GetString());
        Assert.Equal("https://store.example.org/store/cart", payload.GetProperty("cartUrl").GetString());
        foreach (var (field,path) in new[]{("managerUrl","products"),("paymentsUrl","payments"),("previewUrl","preview")})
            Assert.Equal(expected + "/commerce/manage/" + path + "?ticket=synthetic%2Bticket", payload.GetProperty(field).GetString());
        Assert.Equal(businessId, payload.GetProperty("commerceBusinessId").GetGuid());
    }
}
