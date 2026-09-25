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
using Domain.Enums;
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
    public async Task NamedDrafts_CreateUpdateLoadDelete_KeepPublishedContentIsolated(string siteKey)
    {
        using var fixture = new Fixture(siteKey);
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var document = new WebsiteContentDocument();
        document.Elements[ElementId] = new() { Text = "Black variation" };
        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 0, null, "Black")));
        var state = Assert.Single(await fixture.Db.Set<WebsiteContentState>().ToListAsync());
        var draft = Assert.Single(JsonSerializer.Deserialize<List<WebsiteNamedDraft>>(state.NamedDraftsJson, JsonOptions)!);
        Assert.Null(state.PublishedVersionId);
        document.Elements[ElementId].Text = "Second variation";
        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 1, null, "Second")));
        Assert.Equal(2, JsonSerializer.Deserialize<List<WebsiteNamedDraft>>(state.NamedDraftsJson, JsonOptions)!.Count);
        Assert.IsType<ConflictObjectResult>(await fixture.Controller.LoadDraft(new(ticket, 1, draft.Id)));
        Assert.IsType<OkObjectResult>(await fixture.Controller.LoadDraft(new(ticket, 2, draft.Id)));
        Assert.Equal("Black variation", ReadDocument(await fixture.Controller.Manage(ticket)).Elements[ElementId].Text);
        document.Elements[ElementId].Text = "Updated black";
        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 3, draft.Id, "Black")));
        Assert.IsType<NotFoundResult>(await fixture.Controller.DeleteDraft(new(ticket, 4, Guid.NewGuid())));
        Assert.IsType<OkObjectResult>(await fixture.Controller.DeleteDraft(new(ticket, 4, draft.Id)));
        Assert.Single(JsonSerializer.Deserialize<List<WebsiteNamedDraft>>(state.NamedDraftsJson, JsonOptions)!);
        Assert.Equal("Updated black", ReadDocument(await fixture.Controller.Manage(ticket)).Elements[ElementId].Text);
        Assert.Null(state.PublishedVersionId);
    }

    [Theory]
    [InlineData(WebsiteEditorSiteKeys.Legend)]
    [InlineData(WebsiteEditorSiteKeys.Protect)]
    [InlineData(WebsiteEditorSiteKeys.Business)]
    public async Task RouteKeyedEditorPage_PreservesContentAndMetadataAcrossSaveAndReload(string siteKey)
    {
        using var fixture = new Fixture(siteKey);
        var document = JsonSerializer.Deserialize<WebsiteContentDocument>("""
            {"pages":{"/":{"title":"Our business","description":"Our services",
              "elements":{"home.h1.title":{"text":"Saved page content"}},
              "extras":[{"id":"new-section","type":"section","sectionId":"home.root"}]},
              "/about":{"title":"About us","elements":{}}}}
            """, JsonOptions)!;
        document.FaviconImageDataUrl = "https://masterapp-protect.azurewebsites.net/api/website-content/media/11111111-1111-1111-1111-111111111111";
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var saved = ReadDocument(await fixture.Controller.Save(new(ticket, document, 0)));
        Assert.Equal("Saved page content", saved.Pages["/"].Elements[ElementId].Text);
        Assert.Equal(document.FaviconImageDataUrl, saved.FaviconImageDataUrl);
        fixture.Db.ChangeTracker.Clear();
        var reloaded = ReadDocument(await fixture.CreateController().Manage(ticket));
        Assert.Equal("Our business", reloaded.Pages["/"].Title);
        Assert.Equal("Our services", reloaded.Pages["/"].Description);
        Assert.Equal("About us", reloaded.Pages["/about"].Title);
        Assert.Equal(document.FaviconImageDataUrl, reloaded.FaviconImageDataUrl);
        Assert.Single(reloaded.Pages["/"].Extras);
    }

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
    public async Task BusinessPublication_PreservesUtf8AcrossDotNetNodeCompilerTransport()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Business);
        const string unicode = "Locally Owned · Lynden, WA — Café ® “clean”";
        var document = new WebsiteContentDocument();
        document.Pages["/"] = new WebsitePageDocument { Title = unicode, Description = unicode };
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));

        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 0)));
        Assert.IsType<OkObjectResult>(await fixture.Controller.Publish(new(ticket, 1)));

        fixture.Db.ChangeTracker.Clear();
        var version = Assert.Single(await fixture.Db.Set<WebsiteContentVersion>().AsNoTracking().ToListAsync());
        Assert.False(string.IsNullOrWhiteSpace(version.CompiledPagesJson));
        using var compiled = JsonDocument.Parse(version.CompiledPagesJson!);
        var html = compiled.RootElement.GetProperty("pages").GetProperty("/").GetProperty("html").GetString();
        Assert.NotNull(html);
        Assert.Contains(unicode, version.CompiledPagesJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("Â", version.CompiledPagesJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Ã", version.CompiledPagesJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WebsiteEditorSiteKeys.Legend)]
    [InlineData(WebsiteEditorSiteKeys.Protect)]
    public async Task PublicFavicon_FollowsPublishedWebsiteOnly(string siteKey)
    {
        using var fixture = new Fixture(siteKey);
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        const string first = "https://masterapp-protect.azurewebsites.net/api/website-content/media/11111111-1111-1111-1111-111111111111";
        const string second = "https://masterapp-protect.azurewebsites.net/api/website-content/media/22222222-2222-2222-2222-222222222222";
        var document = new WebsiteContentDocument { FaviconImageDataUrl = first };
        var slug = siteKey == WebsiteEditorSiteKeys.Protect ? Fixture.AgentSlug : null;

        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 0)));
        var unpublished = Assert.IsType<RedirectResult>(await fixture.Controller.PublicFavicon(siteKey, slug));
        Assert.EndsWith("/images/favicon/legend-favicon.svg", unpublished.Url, StringComparison.Ordinal);

        Assert.IsType<OkObjectResult>(await fixture.Controller.Publish(new(ticket, 1)));
        fixture.Db.ChangeTracker.Clear();
        var published = Assert.IsType<RedirectResult>(await fixture.CreateController().PublicFavicon(siteKey, slug));
        Assert.Equal(first, published.Url);

        document.FaviconImageDataUrl = second;
        Assert.IsType<OkObjectResult>(await fixture.CreateController().Save(new(ticket, document, 2)));
        fixture.Db.ChangeTracker.Clear();
        var stillPublished = Assert.IsType<RedirectResult>(await fixture.CreateController().PublicFavicon(siteKey, slug));
        Assert.Equal(first, stillPublished.Url);
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

    [Fact]
    public async Task BusinessTicket_AllowsLinkedAgentOnlyWhileClientRemainsShared()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Business);
        var profile = Assert.Single(await fixture.Db.ClientProfiles.ToListAsync());
        profile.AccountManagementMode = ClientAccountManagementModes.SharedAccount;
        fixture.Db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-shared-website",
            AgentUpn = "agent-shared@mylegnd.com",
            ClientUserId = profile.ClientUserId
        });
        await fixture.Db.SaveChangesAsync();

        var ticket = fixture.TicketForActor(
            "agent-shared-website",
            "agent-shared@mylegnd.com",
            DateTime.UtcNow.AddMinutes(10));
        Assert.IsType<OkObjectResult>(await fixture.Controller.Manage(ticket));

        profile.AccountManagementMode = ClientAccountManagementModes.SelfManaged;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        Assert.IsType<UnauthorizedResult>(await fixture.CreateController().Manage(ticket));
    }


    [Fact]
    public async Task MediaLibrary_IsOwnerScopedSearchableAndRejectsInvalidTickets()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Business);
        var ownerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(fixture.BusinessId!.Value);
        var ownImage = new WebsiteMediaAsset
        {
            OwnerKey = ownerKey,
            SourceUrl = "team-logo.png",
            Sha256 = new string('a', 64),
            StorageKey = "website/team-logo.png",
            ContentType = "image/png",
            SizeBytes = 1200,
            CreatedUtc = DateTime.UtcNow
        };
        var ownVideo = new WebsiteMediaAsset
        {
            OwnerKey = ownerKey,
            SourceUrl = "welcome-video.mp4",
            Sha256 = new string('b', 64),
            StorageKey = "website/welcome-video.mp4",
            ContentType = "video/mp4",
            SizeBytes = 2200,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        fixture.Db.AddRange(ownImage, ownVideo, new WebsiteMediaAsset
        {
            OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(Guid.NewGuid()),
            SourceUrl = "other-logo.png",
            Sha256 = new string('c', 64),
            StorageKey = "website/other-logo.png",
            ContentType = "image/png",
            SizeBytes = 900
        });
        await fixture.Db.SaveChangesAsync();
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));

        var imageResult = Assert.IsType<OkObjectResult>(await fixture.Controller.MediaLibrary(ticket, "logo", "image", CancellationToken.None));
        var imageJson = JsonSerializer.SerializeToElement(imageResult.Value, JsonOptions);
        var assets = imageJson.GetProperty("assets").EnumerateArray().ToArray();
        var image = Assert.Single(assets);
        Assert.Equal(ownImage.Id, image.GetProperty("id").GetGuid());
        Assert.Equal("team-logo.png", image.GetProperty("name").GetString());
        Assert.Equal("image/png", image.GetProperty("contentType").GetString());

        var videoResult = Assert.IsType<OkObjectResult>(await fixture.Controller.MediaLibrary(ticket, null, "video", CancellationToken.None));
        var videoJson = JsonSerializer.SerializeToElement(videoResult.Value, JsonOptions);
        Assert.Equal(ownVideo.Id, Assert.Single(videoJson.GetProperty("assets").EnumerateArray()).GetProperty("id").GetGuid());

        Assert.IsType<BadRequestObjectResult>(await fixture.Controller.MediaLibrary(ticket, null, "audio", CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await fixture.Controller.MediaLibrary("invalid-ticket", null, "all", CancellationToken.None));
    }

    [Fact]
    public async Task DraftQuality_ReadsOnlyAuthorizedPersistedDraftAndReportsServerSource()
    {
        using var fixture = new Fixture(WebsiteEditorSiteKeys.Business);
        var ticket = fixture.Ticket(DateTime.UtcNow.AddMinutes(10));
        var document = new WebsiteContentDocument();
        document.Pages["/"] = new WebsitePageDocument
        {
            Navigation = new WebsitePageNavigation { ShowInNavigation = true },
            DynamicBinding = new WebsiteDynamicPageBinding { CollectionId = "missing", ItemKeyField = "id" },
            Extras =
            [
                new WebsiteExtraComponent
                {
                    Id = "photo",
                    Type = "image",
                    SectionId = "home.section.1",
                    ImageDataUrl = "https://images.example/photo.png"
                }
            ]
        };

        Assert.IsType<OkObjectResult>(await fixture.Controller.Save(new(ticket, document, 0)));

        var quality = Assert.IsType<OkObjectResult>(await fixture.CreateController().DraftQuality(ticket, CancellationToken.None));
        var json = JsonSerializer.SerializeToElement(quality.Value, JsonOptions);
        Assert.Equal("saved_draft_server", json.GetProperty("source").GetString());
        Assert.Equal(1, json.GetProperty("revision").GetInt64());
        var codes = json.GetProperty("checks").EnumerateArray()
            .Select(value => value.GetProperty("code").GetString())
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("page_title_missing", codes);
        Assert.Contains("navigation_label_missing", codes);
        Assert.Contains("dynamic_collection_missing", codes);
        Assert.Contains("image_alt_missing", codes);

        Assert.IsType<UnauthorizedResult>(await fixture.CreateController().DraftQuality("invalid-ticket", CancellationToken.None));
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
        public string Ticket(DateTime expiresUtc) => TicketForActor(_actor, "founder@example.test", expiresUtc);

        public string TicketForActor(string actorUserId, string actorEmail, DateTime expiresUtc) =>
            _tickets.Protect(new WebsiteEditorTicket(
                _siteKey,
                _owner,
                _siteKey == WebsiteEditorSiteKeys.Protect ? AgentSlug : null,
                true,
                expiresUtc,
                BusinessId,
                ActorUserId: actorUserId,
                ActorEmail: actorEmail,
                ActorClientProfileId: _clientProfileId));
        public void Dispose() { _services?.Dispose(); _tickets.Dispose(); Db.Dispose(); }
        private static string SourceDirectory([CallerFilePath] string sourcePath = "") => Path.GetDirectoryName(sourcePath)!;
    }
}
