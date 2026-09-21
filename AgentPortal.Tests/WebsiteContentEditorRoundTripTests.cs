using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ProtectWebsite.Services;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProtectWebsite.Controllers;
using Xunit;

namespace AgentPortal.Tests;

// In-process controller + JSON + EF InMemory coverage. These tests do not prove
// SQL behavior, HTTP middleware/model binding, or authenticated live deployment.
public sealed class WebsiteContentEditorRoundTripTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ElementId = "home.h1.title";

    [Theory]
    [InlineData(WebsiteEditorSiteKeys.Legend)]
    [InlineData(WebsiteEditorSiteKeys.Protect)]
    [InlineData(WebsiteEditorSiteKeys.Business)]
    public async Task LargeFractionalAdjustments_SurviveSaveAndReloadThroughBothReadPaths(string siteKey)
    {
        using var fixture = new Fixture(siteKey);
        // Deserialize the actual camel-case payload shape; old integer contract
        // properties would reject the fractional dimensions at this boundary.
        var document = JsonSerializer.Deserialize<WebsiteContentDocument>("""
            {"elements":{"home.h1.title":{"text":"Updated title","style":{
              "fontScale":12.75,"widthPercent":250.25,
              "paddingTop":500.5,"paddingBottom":800.125,"textAlign":"start"}}}}
            """, JsonOptions)!;
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var saved = ReadDocument(await fixture.Controller.Save(new(ticket, document, 0)));
        AssertLargeStyle(saved.Elements[ElementId].Style);

        fixture.Db.ChangeTracker.Clear();
        // A fresh controller and database reads prevent tracked entity state
        // from standing in for a persisted JSON round trip.
        var reloaded = fixture.CreateController();
        AssertLargeStyle(ReadDocument(await reloaded.Manage(ticket)).Elements[ElementId].Style);
        var unpublished = await reloaded.Public(siteKey, siteKey == WebsiteEditorSiteKeys.Protect ? Fixture.AgentSlug : null, fixture.BusinessId);
        if (siteKey == WebsiteEditorSiteKeys.Business) Assert.IsType<NotFoundObjectResult>(unpublished);
        else Assert.IsType<OkObjectResult>(unpublished);
        Assert.IsType<OkObjectResult>(await reloaded.Publish(new(ticket, 1)));
        fixture.Db.ChangeTracker.Clear();
        AssertLargeStyle(ReadDocument(await fixture.CreateController().Public(
            siteKey,
            siteKey == WebsiteEditorSiteKeys.Protect ? Fixture.AgentSlug : null,
            fixture.BusinessId)).Elements[ElementId].Style);
        var row = Assert.Single(await fixture.Db.Set<WebsiteContentState>().ToListAsync());
        Assert.Equal(siteKey, row.SiteKey);
        Assert.NotNull(row.PublishedVersionId);
        Assert.Single(await fixture.Db.Set<WebsiteContentVersion>().ToListAsync());
        Assert.Empty(await fixture.Db.AgentFinanceToolStates.ToListAsync());
        document.Elements[ElementId].Text = "Unpublished revision";
        ReadDocument(await fixture.CreateController().Save(new(ticket, document, 2)));
        Assert.IsType<ConflictObjectResult>(await fixture.CreateController().Save(new(ticket, new WebsiteContentDocument(), 2)));
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("Updated title", ReadDocument(await fixture.CreateController().Public(
            siteKey, siteKey == WebsiteEditorSiteKeys.Protect ? Fixture.AgentSlug : null, fixture.BusinessId)).Elements[ElementId].Text);
        Assert.Equal("Unpublished revision", ReadDocument(await fixture.CreateController().Manage(ticket)).Elements[ElementId].Text);
    }

    [Fact]
    public async Task InvalidNumericDomains_AreDiscardedWithoutInventingReplacementStyles()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var document = new WebsiteContentDocument();
        document.Elements[ElementId] = new WebsiteElementOverride
        {
            Style = new WebsiteStyleOverride
            {
                FontScale = 0, WidthPercent = -1, PaddingTop = -2, PaddingBottom = -3
            }
        };
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        ReadDocument(await fixture.Controller.Save(new(ticket, document, 0)));
        fixture.Db.ChangeTracker.Clear();
        AssertNoAdjustments(ReadDocument(await fixture.CreateController().Manage(ticket)).Elements[ElementId].Style);

        // Zero spacing remains legitimate; small positive scales/widths are
        // permitted instead of the former artificial minimums.
        document.Elements[ElementId].Style = new WebsiteStyleOverride
        {
            FontScale = 0.05m, WidthPercent = 0.25m, PaddingTop = 0, PaddingBottom = 0
        };
        var accepted = ReadDocument(await fixture.Controller.Save(new(ticket, document, 1))).Elements[ElementId].Style;
        Assert.Equal(0.05m, accepted.FontScale);
        Assert.Equal(0.25m, accepted.WidthPercent);
        Assert.Equal(0m, accepted.PaddingTop);
        Assert.Equal(0m, accepted.PaddingBottom);
    }

    [Fact]
    public async Task TextOnlyEdit_LeavesEveryStyleUnsetAfterPersistence()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var document = JsonSerializer.Deserialize<WebsiteContentDocument>("""
            {"elements":{"home.h1.title":{"text":"New heading without resizing"}}}
            """, JsonOptions)!;
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        ReadDocument(await fixture.Controller.Save(new(ticket, document, 0)));
        fixture.Db.ChangeTracker.Clear();
        var element = ReadDocument(await fixture.CreateController().Manage(ticket)).Elements[ElementId];
        Assert.Equal("New heading without resizing", element.Text);
        Assert.Null(element.Hidden);
        AssertNoAdjustments(element.Style);
        Assert.Null(element.Style.TextAlign);
        Assert.Null(element.Style.ObjectPosition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrExpiredTicket_CannotReadOrMutateSavedContent(bool expired)
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Legend);
        var initial = new WebsiteContentDocument();
        initial.Elements[ElementId] = new WebsiteElementOverride { Text = "Preserved content" };
        var validTicket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        ReadDocument(await fixture.Controller.Save(new(validTicket, initial, 0)));
        var originalJson = (await fixture.Db.Set<WebsiteContentState>().SingleAsync()).DraftJson;
        initial.Elements[ElementId].Text = "Unauthorized replacement";
        var rejectedTicket = expired ? fixture.Ticket(DateTime.UtcNow.AddMinutes(-1)) : "not-a-protected-ticket";

        Assert.IsType<UnauthorizedResult>(await fixture.Controller.Manage(rejectedTicket));
        Assert.IsType<UnauthorizedResult>(await fixture.Controller.Save(new(rejectedTicket, initial, 1)));
        fixture.Db.ChangeTracker.Clear();
        var row = Assert.Single(await fixture.Db.Set<WebsiteContentState>().ToListAsync());
        Assert.Equal(originalJson, row.DraftJson);
    }

    private static WebsiteContentDocument ReadDocument(IActionResult result)
    {
        var value = Assert.IsType<OkObjectResult>(result).Value;
        var envelope = JsonSerializer.SerializeToElement(value, JsonOptions);
        return envelope.GetProperty("document").Deserialize<WebsiteContentDocument>(JsonOptions)!;
    }

    private static void AssertLargeStyle(WebsiteStyleOverride style)
    {
        Assert.Equal(12.75m, style.FontScale);
        Assert.Equal(250.25m, style.WidthPercent);
        Assert.Equal(500.5m, style.PaddingTop);
        Assert.Equal(800.125m, style.PaddingBottom);
        Assert.Equal("start", style.TextAlign);
    }

    private static void AssertNoAdjustments(WebsiteStyleOverride style)
    {
        Assert.Null(style.FontScale);
        Assert.Null(style.WidthPercent);
        Assert.Null(style.PaddingTop);
        Assert.Null(style.PaddingBottom);
    }

    private sealed class Fixture : IDisposable
    {
        public const string AgentSlug = "editor-test-agent";
        public Guid? BusinessId { get; private set; }
        private readonly string _siteKey;
        private readonly string _owner;
        private readonly WebsiteEditorTicketProtector _tickets = new(new EphemeralDataProtectionProvider());
        private readonly IConfiguration _configuration;
        private readonly string _actor = Guid.NewGuid().ToString();
        private Guid? _clientProfileId;
        private ServiceProvider? _services;
        public MasterAppDbContext Db { get; }
        public WebsiteContentController Controller { get; }

        public Fixture(string siteKey)
        {
            _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["Founder:Oid"] = _actor,
                ["WebsitePublishing:CompilerRoot"] = Path.GetFullPath(Path.Combine(SourceDirectory(), "..", "Legend-Website"))
            }).Build();
            _siteKey = siteKey;
            _owner = siteKey == WebsiteEditorSiteKeys.Legend ? WebsiteEditorSiteKeys.GlobalOwnerKey : "editor-test-owner";
            Db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
            if (siteKey == WebsiteEditorSiteKeys.Protect) _owner = _actor;
            if (siteKey != WebsiteEditorSiteKeys.Business)
            {
                Db.AgentTrackingProfiles.Add(new AgentTrackingProfile
                {
                    AgentUserId = _actor, AgentUpn = "founder@example.test", Slug = AgentSlug, Status = "Active"
                });
                Db.SaveChanges();
            }
            if (siteKey == WebsiteEditorSiteKeys.Business)
            {
                var businessId = Guid.NewGuid();
                BusinessId = businessId;
                _owner = WebsiteEditorSiteKeys.BusinessOwnerKey(businessId);
                Db.CommerceBusinesses.Add(new CommerceBusiness
                {
                    Id = businessId,
                    Key = "editor-test-business",
                    DisplayName = "Editor Test Business",
                    LegalName = "Editor Test Business LLC",
                    BusinessType = "BusinessClient",
                    OwnerEmail = "owner@example.test",
                    Status = "Active",
                    IsActive = true
                });
                var profile = new ClientProfile { ClientUserId = _actor, CrmNotes = "{\"recordType\":\"BusinessClient\"}" };
                _clientProfileId = profile.Id;
                Db.ClientProfiles.Add(profile);
                Db.CommerceBusinessMembers.Add(new CommerceBusinessMember { CommerceBusinessId = businessId, ClientProfileId = profile.Id, RoleKey = "owner" });
                Db.SaveChanges();
            }
            var environment = Mock.Of<IWebHostEnvironment>(e => e.ContentRootPath == AppContext.BaseDirectory);
            _services = new ServiceCollection().AddSingleton(new WebsitePageCompiler(environment, _configuration)).BuildServiceProvider();
            Controller = CreateController();
        }

        public WebsiteContentController CreateController() => new(Db, _tickets, _configuration) { ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestServices = _services! } } };
        public string Ticket(DateTime expiresUtc) => _tickets.Protect(new WebsiteEditorTicket(
            _siteKey,
            _owner,
            _siteKey == WebsiteEditorSiteKeys.Protect ? AgentSlug : null,
            true,
            expiresUtc,
            BusinessId, ActorUserId: _actor, ActorEmail: "founder@example.test", ActorClientProfileId: _clientProfileId));
        public void Dispose() { _services?.Dispose(); _tickets.Dispose(); Db.Dispose(); }
        private static string SourceDirectory([CallerFilePath] string sourcePath = "") => Path.GetDirectoryName(sourcePath)!;
    }
}
