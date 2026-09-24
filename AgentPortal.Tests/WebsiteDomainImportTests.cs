using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
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
    [Fact]
    public async Task SitemapUsesOnlyVerifiedHostsPublishedPagesIncludingUnlinkedCustomRoutes()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var config = new ConfigurationBuilder().Build();
        var business = new CommerceBusiness { Key = "sitemap-owner" };
        var other = new CommerceBusiness { Key = "other-owner" };
        var state = new WebsiteContentState { SiteKey = WebsiteEditorSiteKeys.Business,
            OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(business.Id), DraftJson = "{\"pages\":{\"/draft-only\":{}}}" };
        var version = new WebsiteContentVersion { StateId = state.Id,
            CompiledPagesJson = "{\"pages\":{\"/\":{},\"/services/custom\":{}}}" };
        state.PublishedVersionId = version.Id;
        var otherState = new WebsiteContentState { SiteKey = WebsiteEditorSiteKeys.Business,
            OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(other.Id) };
        var otherVersion = new WebsiteContentVersion { StateId = otherState.Id,
            CompiledPagesJson = "{\"pages\":{\"/other-private-route\":{}}}" };
        otherState.PublishedVersionId = otherVersion.Id;
        var binding = new WebsiteDomainBinding { CommerceBusinessId = business.Id, Hostname = "business.example.com",
            Status = "active", CertificateStatus = "active", LastCheckedUtc = DateTime.UtcNow };
        db.AddRange(business, other, state, version, otherState, otherVersion, binding);
        await db.SaveChangesAsync();
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns("Production");
        var middleware = new BusinessWebsiteMiddleware(_ => throw new InvalidOperationException("Must not fall through to another site."), environment.Object, config);
        var domains = new WebsiteDomainService(db, Mock.Of<IHttpClientFactory>(), config);
        async Task<DefaultHttpContext> Request(string host)
        {
            var context = new DefaultHttpContext();
            context.Request.Host = new HostString(host);
            context.Request.Method = "GET";
            context.Request.Path = "/sitemap.xml";
            context.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(context, db, domains);
            return context;
        }
        var response = await Request(binding.Hostname);
        response.Response.Body.Position = 0;
        var xml = System.Xml.Linq.XDocument.Parse(await new StreamReader(response.Response.Body).ReadToEndAsync());
        System.Xml.Linq.XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        Assert.Equal(new[] { "https://business.example.com/", "https://business.example.com/services/custom" },
            System.Linq.Enumerable.Select(xml.Descendants(ns + "loc"), x => x.Value));
        Assert.Equal(404, (await Request("unknown.example.com")).Response.StatusCode);
        binding.LastCheckedUtc = DateTime.UtcNow.AddDays(-2);
        await db.SaveChangesAsync();
        Assert.Equal(404, (await Request(binding.Hostname)).Response.StatusCode);
    }

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
    public void WebsiteBridgeOriginalHostRequiresMatchingSecretAndProtectOrigin()
    {
        const string secret = "0123456789abcdef0123456789abcdef";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WebsiteRouting:BridgeSecret"] = secret,
            ["WebsiteContentApiBaseUrl"] = "https://masterapp-protect.azurewebsites.net"
        }).Build();

        var accepted = new DefaultHttpContext();
        accepted.Request.Host = new HostString("masterapp-protect.azurewebsites.net");
        accepted.Request.Headers[WebsiteRequestHostResolver.OriginalHostHeader] = "Business.Example.Com";
        accepted.Request.Headers[WebsiteRequestHostResolver.BridgeSecretHeader] = secret;
        Assert.Equal("business.example.com", WebsiteRequestHostResolver.Resolve(accepted, configuration));

        var rejectedSecret = new DefaultHttpContext();
        rejectedSecret.Request.Host = new HostString("masterapp-protect.azurewebsites.net");
        rejectedSecret.Request.Headers[WebsiteRequestHostResolver.OriginalHostHeader] = "business.example.com";
        rejectedSecret.Request.Headers[WebsiteRequestHostResolver.BridgeSecretHeader] = "wrong";
        Assert.Equal("masterapp-protect.azurewebsites.net", WebsiteRequestHostResolver.Resolve(rejectedSecret, configuration));

        var rejectedOrigin = new DefaultHttpContext();
        rejectedOrigin.Request.Host = new HostString("protect.mylegnd.com");
        rejectedOrigin.Request.Headers[WebsiteRequestHostResolver.OriginalHostHeader] = "business.example.com";
        rejectedOrigin.Request.Headers[WebsiteRequestHostResolver.BridgeSecretHeader] = secret;
        Assert.Equal("protect.mylegnd.com", WebsiteRequestHostResolver.Resolve(rejectedOrigin, configuration));
    }

    [Fact]
    public async Task BusinessWebsiteMiddlewareServesVerifiedBindingThroughAuthenticatedBridgeHost()
    {
        const string secret = "0123456789abcdef0123456789abcdef";
        await using var db = new MasterAppDbContext(
            new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var business = new CommerceBusiness { Key = "bridged-owner", IsActive = true, Status = "Active" };
        var state = new WebsiteContentState
        {
            SiteKey = WebsiteEditorSiteKeys.Business,
            OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(business.Id)
        };
        var version = new WebsiteContentVersion
        {
            StateId = state.Id,
            CompiledPagesJson = "{\"pages\":{\"/\":{\"html\":\"<a href='__LEGEND_CANONICAL_URL__'>Home</a>\"}}}"
        };
        state.PublishedVersionId = version.Id;
        var binding = new WebsiteDomainBinding
        {
            CommerceBusinessId = business.Id,
            Hostname = "business.example.com",
            Status = "active",
            CertificateStatus = "active",
            LastCheckedUtc = DateTime.UtcNow
        };
        db.AddRange(business, state, version, binding);
        await db.SaveChangesAsync();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WebsiteRouting:BridgeSecret"] = secret,
            ["WebsiteContentApiBaseUrl"] = "https://masterapp-protect.azurewebsites.net"
        }).Build();
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns("Production");
        var middleware = new BusinessWebsiteMiddleware(
            _ => throw new InvalidOperationException("Bridged business traffic must not fall through."),
            environment.Object,
            configuration);
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("masterapp-protect.azurewebsites.net");
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/";
        context.Request.Headers[WebsiteRequestHostResolver.OriginalHostHeader] = binding.Hostname;
        context.Request.Headers[WebsiteRequestHostResolver.BridgeSecretHeader] = secret;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(
            context,
            db,
            new WebsiteDomainService(db, Mock.Of<IHttpClientFactory>(), configuration));

        context.Response.Body.Position = 0;
        var html = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("https://business.example.com/", html, StringComparison.Ordinal);
        Assert.DoesNotContain("masterapp-protect.azurewebsites.net", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderActiveWithoutActiveCertificateNeverActivates()
    {
        var binding = new WebsiteDomainBinding { Hostname = "example.com", CommerceBusinessId = Guid.NewGuid() };
        using var receipt = JsonDocument.Parse(JsonSerializer.Serialize(new { id = "provider-id", hostname = binding.Hostname, status = "active", ssl = new { status = "pending_validation" } }));
        WebsiteDomainService.ApplyReceipt(binding, receipt.RootElement);
        Assert.Equal("provider-id", binding.ProviderHostnameId);
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
    public void DomainDiagnosticExplainsProviderPendingWhenRoutingAlreadyMatches()
    {
        var binding = new WebsiteDomainBinding { Hostname = "example.com", CommerceBusinessId = Guid.NewGuid() };
        using var receipt = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = "provider-id",
            hostname = binding.Hostname,
            status = "pending",
            ssl = new { status = "active", method = "http" }
        }));

        WebsiteDomainService.ApplyReceipt(binding, receipt.RootElement);
        WebsiteDomainService.ApplyVerificationDiagnostic(
            binding,
            receipt.RootElement,
            new WebsiteDomainService.WebsiteDomainRoutingProof(
                true,
                "routing_confirmed",
                "LEGEND routing proof matched this business and domain binding.",
                200),
            "sites.mylegnd.com");

        using var diagnostic = JsonDocument.Parse(binding.VerificationJson);
        Assert.Equal("pending", diagnostic.RootElement.GetProperty("providerStatus").GetString());
        Assert.Equal("active", diagnostic.RootElement.GetProperty("certificateStatus").GetString());
        Assert.Equal("confirmed", diagnostic.RootElement.GetProperty("routing").GetProperty("status").GetString());
        Assert.Contains("Cloudflare hostname status is pending", diagnostic.RootElement.GetProperty("summary").GetString());
        Assert.Contains("No LEGEND routing change is required", diagnostic.RootElement.GetProperty("requiredAction").GetString());
    }

    [Fact]
    public void DomainDiagnosticExplainsRoutingHttpFailureAndRequiredAction()
    {
        var binding = new WebsiteDomainBinding { Hostname = "example.com", CommerceBusinessId = Guid.NewGuid() };
        using var receipt = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = "provider-id",
            hostname = binding.Hostname,
            status = "active",
            ssl = new { status = "active", method = "http" }
        }));

        WebsiteDomainService.ApplyReceipt(binding, receipt.RootElement);
        WebsiteDomainService.ApplyVerificationDiagnostic(
            binding,
            receipt.RootElement,
            new WebsiteDomainService.WebsiteDomainRoutingProof(
                false,
                "routing_http_status",
                "LEGEND routing proof reached the hostname, but /.well-known/legend-website returned HTTP 404.",
                404),
            "sites.mylegnd.com");

        using var diagnostic = JsonDocument.Parse(binding.VerificationJson);
        Assert.Equal("routing_http_status", diagnostic.RootElement.GetProperty("routing").GetProperty("code").GetString());
        Assert.Equal(404, diagnostic.RootElement.GetProperty("routing").GetProperty("httpStatus").GetInt32());
        Assert.Contains("HTTP 404", diagnostic.RootElement.GetProperty("summary").GetString());
        Assert.Contains("sites.mylegnd.com", diagnostic.RootElement.GetProperty("requiredAction").GetString());
    }

    [Fact]
    public async Task RefreshCreatesHostnameWithoutCloudflareCustomMetadata()
    {
        await using var db = new MasterAppDbContext(
            new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var binding = new WebsiteDomainBinding
        {
            CommerceBusinessId = Guid.NewGuid(),
            Hostname = "example.com"
        };
        db.Add(binding);
        await db.SaveChangesAsync();

        var handler = new SequenceHttpMessageHandler(
            "{\"success\":true,\"result\":[]}",
            "{\"success\":true,\"result\":{\"id\":\"provider-id\",\"hostname\":\"example.com\",\"status\":\"pending\",\"ssl\":{\"status\":\"pending_validation\",\"method\":\"http\"}}}",
            "{\"success\":true,\"result\":{\"id\":\"provider-id\",\"hostname\":\"example.com\",\"status\":\"pending\",\"ssl\":{\"status\":\"pending_validation\",\"method\":\"http\"}}}");
        var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("WebsiteDomains")).Returns(client);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WebsiteDomains:CloudflareZoneId"] = "zone-id",
            ["WebsiteDomains:ApiToken"] = "token",
            ["WebsiteDomains:CnameTarget"] = "sites.mylegnd.com"
        }).Build();

        var service = new WebsiteDomainService(db, factory.Object, configuration);
        var refreshed = await service.RefreshAsync(binding.CommerceBusinessId, binding.Id);

        Assert.Equal("provider-id", refreshed.ProviderHostnameId);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Patch, handler.Requests[2].Method);
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.False(body.RootElement.TryGetProperty("custom_metadata", out _));
        Assert.Equal("example.com", body.RootElement.GetProperty("hostname").GetString());
    }

    [Fact]
    public async Task RefreshDoesNotAdoptProviderHostnameWithoutTrustedLegacyReceipt()
    {
        await using var db = new MasterAppDbContext(
            new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var binding = new WebsiteDomainBinding
        {
            CommerceBusinessId = Guid.NewGuid(),
            Hostname = "example.com"
        };
        db.Add(binding);
        await db.SaveChangesAsync();

        var handler = new SequenceHttpMessageHandler(
            "{\"success\":true,\"result\":[{\"id\":\"untrusted-provider-id\",\"hostname\":\"example.com\",\"status\":\"pending\",\"ssl\":{\"status\":\"pending_validation\",\"method\":\"http\"}}]}");
        var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("WebsiteDomains")).Returns(client);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WebsiteDomains:CloudflareZoneId"] = "zone-id",
            ["WebsiteDomains:ApiToken"] = "token",
            ["WebsiteDomains:CnameTarget"] = "sites.mylegnd.com"
        }).Build();

        var service = new WebsiteDomainService(db, factory.Object, configuration);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RefreshAsync(binding.CommerceBusinessId, binding.Id));

        Assert.Single(handler.Requests);
        Assert.True(string.IsNullOrEmpty(binding.ProviderHostnameId));
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

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public SequenceHttpMessageHandler(params string[] responses) =>
            _responses = new Queue<string>(responses);

        public List<(HttpMethod Method, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, body));
            if (_responses.Count == 0) throw new InvalidOperationException("Unexpected provider request.");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json")
            };
        }
    }

}
