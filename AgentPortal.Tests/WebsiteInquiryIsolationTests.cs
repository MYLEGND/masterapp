using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ProtectWebsite.Controllers;
using Xunit;

namespace AgentPortal.Tests;

// In-process controller/EF coverage; does not substitute for SQL uniqueness or live CORS proof.
public sealed class WebsiteInquiryIsolationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("http://business.example")]
    [InlineData("https://business.example/path")]
    [InlineData("https://business.example:8443")]
    public async Task MalformedOriginCannotCreateInquiry(string origin)
    {
        using var f = new Fixture(origin);
        Assert.IsType<BadRequestObjectResult>(await f.Controller.Submit(f.Request(), CancellationToken.None));
        Assert.Empty(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());
    }

    [Fact]
    public async Task UnknownDomainCannotRouteToFounderOrAnotherBusiness()
    {
        using var f = new Fixture("https://unknown.example");
        await f.SeedPublishedAsync();
        Assert.IsType<NotFoundObjectResult>(await f.Controller.Submit(f.Request(), CancellationToken.None));
        Assert.Empty(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());
    }

    [Fact]
    public async Task VerifiedDomainWithoutPublishedVersionCannotReceiveInquiry()
    {
        using var f = new Fixture();
        await f.SeedPublishedAsync();
        var state = await f.Db.Set<WebsiteContentState>().SingleAsync();
        state.PublishedVersionId = null;
        await f.Db.SaveChangesAsync();
        Assert.IsType<NotFoundObjectResult>(await f.Controller.Submit(f.Request(), CancellationToken.None));
        Assert.Empty(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());
    }

    [Fact]
    public async Task SubmissionStaysWithVerifiedBusinessAndRetriesDoNotDuplicate()
    {
        using var f = new Fixture();
        await f.SeedPublishedAsync();
        var request = f.Request();
        Assert.IsType<OkObjectResult>(await f.Controller.Submit(request, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await f.Controller.Submit(request, CancellationToken.None));
        var row = Assert.Single(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());
        Assert.Equal(f.BusinessId, row.CommerceBusinessId);
        Assert.Equal(f.VersionId, row.PublishedVersionId);
        Assert.Equal("New", row.Status);
        Assert.IsType<ConflictObjectResult>(await f.Controller.Submit(request with { Message = "Different request" }, CancellationToken.None));
    }

    [Fact]
    public async Task ConsentRequiredAndSourceCannotContainPrivateQuery()
    {
        using var f = new Fixture();
        await f.SeedPublishedAsync();
        Assert.IsType<BadRequestObjectResult>(await f.Controller.Submit(f.Request() with { Consent = false }, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await f.Controller.Submit(f.Request() with { SourcePath = "/?ticket=private" }, CancellationToken.None));
        Assert.Empty(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());
    }

    [Fact]
    public async Task MissingManagementAuthorityCannotReadOrMutateInbox()
    {
        using var f = new Fixture();
        Assert.IsType<UnauthorizedResult>(await f.Controller.Manage("invalid", CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await f.Controller.Status(new("invalid", Guid.NewGuid(), "Closed"), CancellationToken.None));
    }

    [Fact]
    public async Task ManagementCannotReadOrChangeAnotherBusinessAndRevocationIsImmediate()
    {
        using var f = new Fixture();
        await f.SeedPublishedAsync();
        var profile = new ClientProfile { ClientUserId = Guid.NewGuid().ToString(), CrmNotes = "{\"recordType\":\"BusinessClient\"}" };
        var member = new CommerceBusinessMember { CommerceBusinessId = f.BusinessId, ClientProfileId = profile.Id };
        f.Db.Add(profile); f.Db.Add(member);
        var own = new CommerceWebsiteInquiry { CommerceBusinessId = f.BusinessId, PublishedVersionId = f.VersionId, SubmissionId = Guid.NewGuid(), Message = "Owned" };
        var other = new CommerceWebsiteInquiry { CommerceBusinessId = Guid.NewGuid(), PublishedVersionId = f.VersionId, SubmissionId = Guid.NewGuid(), Message = "Private other business" };
        f.Db.Add(own); f.Db.Add(other);
        await f.Db.SaveChangesAsync();
        var ticket = f.Ticket(profile);
        var result = Assert.IsType<OkObjectResult>(await f.Controller.Manage(ticket, CancellationToken.None));
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("Owned", json);
        Assert.DoesNotContain("Private other business", json);
        Assert.IsType<NotFoundResult>(await f.Controller.Status(new(ticket, other.Id, "Closed"), CancellationToken.None));
        Assert.IsType<OkObjectResult>(await f.Controller.Status(new(ticket, own.Id, "Contacted"), CancellationToken.None));
        member.Status = "Inactive";
        await f.Db.SaveChangesAsync();
        Assert.IsType<UnauthorizedResult>(await f.Controller.Manage(ticket, CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await f.Controller.Status(new(ticket, own.Id, "Closed"), CancellationToken.None));
        Assert.Equal("Contacted", own.Status);
        Assert.Equal("New", other.Status);
    }

    private sealed class Fixture : IDisposable
    {
        public MasterAppDbContext Db { get; } = new(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid BusinessId { get; } = Guid.NewGuid();
        public Guid VersionId { get; } = Guid.NewGuid();
        public WebsiteInquiriesController Controller { get; }
        private readonly WebsiteEditorTicketProtector _tickets = new(new EphemeralDataProtectionProvider());
        public Fixture(string origin = "https://business.example")
        {
            var config = new ConfigurationBuilder().Build();
            var domains = new WebsiteDomainService(Db, Mock.Of<IHttpClientFactory>(), config);
            Controller = new(Db, _tickets, config, domains) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
            Controller.Request.Headers.Origin = origin;
        }
        public WebsiteInquiriesController.PublicRequest Request() => new(Guid.NewGuid(), "Visitor", "visitor@example.org", "Please contact me.", "/contact", true);
        public async Task SeedPublishedAsync()
        {
            Db.Add(new CommerceBusiness { Id = BusinessId, Key = "business", DisplayName = "Business" });
            Db.Add(new WebsiteDomainBinding { CommerceBusinessId = BusinessId, Hostname = "business.example", Status = "active", CertificateStatus = "active", LastCheckedUtc = DateTime.UtcNow });
            var state = new WebsiteContentState { OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(BusinessId), SiteKey = WebsiteEditorSiteKeys.Business, PublishedVersionId = VersionId };
            Db.Add(state);
            Db.Add(new WebsiteContentVersion { Id = VersionId, StateId = state.Id });
            await Db.SaveChangesAsync();
        }
        public string Ticket(ClientProfile profile) => _tickets.Protect(new WebsiteEditorTicket(
            WebsiteEditorSiteKeys.Business, WebsiteEditorSiteKeys.BusinessOwnerKey(BusinessId), null, false,
            DateTime.UtcNow.AddMinutes(10), BusinessId, ActorUserId: profile.ClientUserId, ActorClientProfileId: profile.Id));
        public void Dispose() { _tickets.Dispose(); Db.Dispose(); }
    }
}
