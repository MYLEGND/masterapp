using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Infrastructure.Security.UploadValidation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ProtectWebsite.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class WebsiteDomainImportTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("example.com/path")]
    [InlineData("127.0.0.1")]
    [InlineData("mylegnd.com")]
    [InlineData("portal.mylegnd.com")]
    [InlineData("*.example.com")]
    public void DomainRejectsAddressesOutsideBusinessHostnameContract(string host) =>
        Assert.ThrowsAny<ArgumentException>(() => WebsiteDomainService.NormalizeHostname(host));

    [Fact]
    public void DomainNormalizesCaseAndTrailingDot() => Assert.Equal("example.com", WebsiteDomainService.NormalizeHostname(" EXAMPLE.COM. "));

    [Fact]
    public async Task PendingDomainProofBypassesActiveRoutingGate()
    {
        await using var db = new MasterAppDbContext(
            new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var configuration = new ConfigurationBuilder().Build();
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(value => value.EnvironmentName).Returns("Production");
        var nextCalled = false;
        var middleware = new BusinessWebsiteMiddleware(
            next: context =>
            {
                nextCalled = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            environment.Object,
            configuration);
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("example.com");
        context.Request.Path = "/.well-known/legend-website";
        var domains = new WebsiteDomainService(db, Mock.Of<IHttpClientFactory>(), configuration);

        await middleware.InvokeAsync(context, db, domains);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Fact]
    public void ProviderActiveWithoutActiveCertificateNeverActivates()
    {
        var binding = new WebsiteDomainBinding { Hostname = "example.com", CommerceBusinessId = Guid.NewGuid() };
        using var receipt = JsonDocument.Parse(JsonSerializer.Serialize(new { id = "provider-id", hostname = binding.Hostname, status = "active", ssl = new { status = "pending_validation" }, custom_metadata = new { legend_binding_id = binding.Id.ToString("N"), legend_business_id = binding.CommerceBusinessId.ToString("N") } }));
        WebsiteDomainService.ApplyReceipt(binding, receipt.RootElement);
        Assert.Equal("pending", binding.Status);
    }

    [Fact]
    public void ProviderReceiptForAnotherBusinessIsRejected()
    {
        var binding = new WebsiteDomainBinding { Hostname = "example.com", CommerceBusinessId = Guid.NewGuid() };
        using var receipt = JsonDocument.Parse("{\"id\":\"provider\",\"hostname\":\"example.com\",\"status\":\"active\",\"ssl\":{\"status\":\"active\"},\"custom_metadata\":{\"legend_binding_id\":\"other\",\"legend_business_id\":\"other\"}}");
        Assert.Throws<InvalidOperationException>(() => WebsiteDomainService.ApplyReceipt(binding, receipt.RootElement));
        Assert.Equal("pending", binding.Status);
    }

    [Fact]
    public async Task ExportDoesNotOverwriteExistingPage()
    {
        var service = new WebsiteImportService(null!); // No media operations in this document.
        var existing = new WebsiteContentDocument();
        existing.Pages["/about"] = new WebsitePageDocument { Title = "Client edited title" };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"pages\":{\"/about\":{\"title\":\"Old imported title\"},\"/contact\":{\"title\":\"Contact\"}}}"));
        var result = await service.PrepareExportAsync(stream, false, existing, true, "business:test", "https://protect.mylegnd.com");
        Assert.Equal("Client edited title", result.Document.Pages["/about"].Title);
        Assert.Equal("Contact", result.Document.Pages["/contact"].Title);
        Assert.Equal(1, result.Report.PreservedComponents);
        Assert.Single(existing.Pages);
    }

    [Fact]
    public async Task UnauthorizedImportPerformsNoRead()
    {
        var service = new WebsiteImportService(null!);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PrepareExportAsync(Stream.Null, false, new(), false, "owner", "https://example.com"));
    }

    [Fact]
    public async Task ExportRejectsTraversalArchive()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        { zip.CreateEntry("../document.json"); }
        stream.Position = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => new WebsiteImportService(null!).PrepareExportAsync(stream, true, new(), true, "owner", "https://example.com"));
    }
    [Fact]
    public void WebmRequiresEbmlAndActualDocTypeMarker()
    {
        var valid = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x42, 0x82, 0x84, 0x77, 0x65, 0x62, 0x6D, 0x00 };
        Assert.Equal("video/webm", UploadValidator.DetectContentType(valid));
        var forged = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0x77, 0x65, 0x62, 0x6D, 0 };
        Assert.Null(UploadValidator.DetectContentType(forged));
        Assert.False(UploadValidator.ValidateContent(valid, "fake.png", "image/png", UploadValidationPolicy.Images(1000)).IsValid);
    }

    [Fact]
    public async Task FreshExportImportsThemeAndOrder()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"theme\":{\"gold\":\"#123456\"},\"sectionOrder\":{\"section\":2}}"));
        var result = await new WebsiteImportService(null!).PrepareExportAsync(stream, false, new(), true, "owner", "https://example.com");
        Assert.Equal("#123456", result.Document.Theme.Gold);
        Assert.Equal(2, result.Document.SectionOrder["section"]);
    }

    [Fact]
    public async Task LegacyExportWrapperCanBeImported()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"format\":\"legend-website-v1\",\"draft\":{\"pages\":{\"/contact\":{\"title\":\"Contact\"}}}}"));
        var result = await new WebsiteImportService(null!).PrepareExportAsync(stream, false, new(), true, "owner", "https://example.com");
        Assert.Equal("Contact", result.Document.Pages["/contact"].Title);
    }

}
