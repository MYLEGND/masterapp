using System;
using System.Text.Json;
using System.Linq;
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

public sealed class WebsitePublishingAuthorityTests
{
    private sealed class Fixture : IDisposable
    {
        public MasterAppDbContext Db { get; } = new(new DbContextOptionsBuilder<MasterAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public WebsiteEditorTicketProtector Tickets { get; } = new(new EphemeralDataProtectionProvider());
        public IConfiguration Config { get; } = new ConfigurationBuilder().AddInMemoryCollection(new[] { new System.Collections.Generic.KeyValuePair<string,string?>("Founder:Oid", "1d43fa52-e36d-4522-9d21-40b3ac260aed") }).Build();
        public WebsiteContentController Controller => new(Db, Tickets, Config);
        public string Token => Tickets.Protect(new(WebsiteEditorSiteKeys.Legend, WebsiteEditorSiteKeys.GlobalOwnerKey, null, true, DateTime.UtcNow.AddMinutes(10), ActorUserId: "1d43fa52-e36d-4522-9d21-40b3ac260aed"));
        public void Dispose() { Db.Dispose(); Tickets.Dispose(); }
    }
    private static JsonElement Body(IActionResult result) => JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static WebsiteContentDocument Document(string text) => new() { Elements = new() { ["title"] = new() { Text = text } } };

    [Fact]
    public async Task DraftRemainsPrivateUntilPublish_AndStaleWriterCannotOverwrite()
    {
        using var f = new Fixture();
        var token = f.Token;
        Assert.Equal(0, Body(await f.Controller.Manage(token)).GetProperty("revision").GetInt64());
        Assert.Equal(1, Body(await f.Controller.Save(new(token, Document("draft"), 0))).GetProperty("revision").GetInt64());
        Assert.False(Body(await f.Controller.Public("legend")).GetProperty("document").GetProperty("elements").TryGetProperty("title", out _));
        Assert.IsType<ConflictObjectResult>(await f.Controller.Save(new(token, Document("stale"), 0)));
        Body(await f.Controller.Publish(new(token, 1)));
        Assert.Equal("draft", Body(await f.Controller.Public("legend")).GetProperty("document").GetProperty("elements").GetProperty("title").GetProperty("text").GetString());
    }

    [Fact]
    public async Task RollbackRestoresPublishedSnapshot_AndMakesItCurrentDraft()
    {
        using var f = new Fixture(); var token = f.Token;
        Body(await f.Controller.Save(new(token, Document("first"), 0)));
        var first = Body(await f.Controller.Publish(new(token, 1))).GetProperty("versionId").GetGuid();
        Body(await f.Controller.Save(new(token, Document("second"), 2)));
        Body(await f.Controller.Publish(new(token, 3)));
        Body(await f.Controller.Save(new(token, Document("unfinished"), 4)));
        Body(await f.Controller.Rollback(new(token, 5, first)));
        Assert.Equal("first", Body(await f.Controller.Public("legend")).GetProperty("document").GetProperty("elements").GetProperty("title").GetProperty("text").GetString());
        Assert.Equal("first", Body(await f.Controller.Manage(token)).GetProperty("document").GetProperty("elements").GetProperty("title").GetProperty("text").GetString());
    }

    [Fact]
    public async Task ForgedFounderFlagAndLegacyActorlessTicketCannotAuthorize()
    {
        using var f = new Fixture();
        var legacy = f.Tickets.Protect(new("legend", WebsiteEditorSiteKeys.GlobalOwnerKey, null, true, DateTime.UtcNow.AddMinutes(5)));
        Assert.IsType<UnauthorizedResult>(await f.Controller.Manage(legacy));
        var other = f.Tickets.Protect(new("legend", WebsiteEditorSiteKeys.GlobalOwnerKey, null, true, DateTime.UtcNow.AddMinutes(5), ActorUserId: Guid.NewGuid().ToString()));
        Assert.IsType<UnauthorizedResult>(await f.Controller.Manage(other));
    }

    [Fact]
    public void EditorContentPreservesIntentionalWhitespaceAndBusinessCards()
    {
        var doc = Document("  First\tline\r\nSecond  line  ");
        doc.Extras.Add(new() { Id = "service-one", SectionId = "services", Type = "card", Title = "  Window\tCleaning  ", Text = "Line one\r\n\tLine two" });
        var result = WebsiteContentSanitizer.Sanitize(doc);
        Assert.Equal("  First\tline\nSecond  line  ", result.Elements["title"].Text);
        Assert.Equal("card", result.Extras.Single().Type);
        Assert.Equal("  Window\tCleaning  ", result.Extras.Single().Title);
        Assert.Equal("Line one\n\tLine two", result.Extras.Single().Text);
    }

    [Fact]
    public void ScopeCtaCatalogOnlyOffersConfiguredDynamicActions()
    {
        var business = WebsiteCallToActionCatalog.Build(
            WebsiteEditorSiteKeys.Business,
            phone: "(602) 555-0199",
            email: "hello@example.test",
            bookingUrl: "https://book.example.test/meeting");
        Assert.Contains(business, option => option.Key == "business_call" && option.Href == "tel:6025550199" &&
            option.AnalyticsEventName == "cta_click" && option.MetaIntentEventName == "ContactStepReached");
        Assert.Contains(business, option => option.Key == "business_email" && option.Href == "mailto:hello@example.test" &&
            option.MetaIntentEventName == "ContactStepReached");
        Assert.Contains(business, option => option.Key == "business_schedule" && option.Href.StartsWith("https://book.example.test/") &&
            option.MetaIntentEventName == "ContactStepReached");
        Assert.Contains(business, option => option.Key == "business_quote" && option.Href == "/contact" &&
            option.AnalyticsEventName == "cta_click" && option.MetaIntentEventName == "ContactStepReached");

        var protect = WebsiteCallToActionCatalog.Build(WebsiteEditorSiteKeys.Protect);
        Assert.Contains(protect, option => option.Key == "protect_quote" && option.Href == "/Quote" &&
            option.AnalyticsEventName == "quote_click");
        Assert.Contains(protect, option => option.Href == "/Quote/Life" && option.AnalyticsEventName == "quote_click");
        Assert.DoesNotContain(protect, option => option.Key == "protect_call");
        Assert.DoesNotContain(protect, option => option.Key == "protect_schedule");
    }

    [Fact]
    public async Task PublishRejectsDeadAddedButton_AndResolvesManagedAction()
    {
        using var f = new Fixture();
        var token = f.Token;
        var dead = new WebsiteContentDocument
        {
            Extras = [new() { Id = "dead", SectionId = "home.section.1", Type = "button", Text = "Dead", Href = "#" }]
        };
        Assert.Equal(1, Body(await f.Controller.Save(new(token, dead, 0))).GetProperty("revision").GetInt32());
        Assert.IsType<BadRequestObjectResult>(await f.Controller.Publish(new(token, 1)));

        var managed = new WebsiteContentDocument
        {
            Extras = [new() { Id = "managed", SectionId = "home.section.1", Type = "button", Text = "Talk", ActionKey = "legend_contact", Href = "#" }]
        };
        Assert.Equal(2, Body(await f.Controller.Save(new(token, managed, 1))).GetProperty("revision").GetInt32());
        Assert.IsType<OkObjectResult>(await f.Controller.Publish(new(token, 2)));
        var published = Body(await f.Controller.Public("legend"));
        Assert.Equal("/contact", published.GetProperty("document").GetProperty("extras")[0].GetProperty("href").GetString());
        Assert.Equal("legend_contact", published.GetProperty("document").GetProperty("extras")[0].GetProperty("actionKey").GetString());
    }

    [Fact]
    public void ControlsRejectExecutableUrlsAndClampPlacementWithinGrid()
    {
        var doc = Document("safe");
        doc.Elements["title"].Href = "javascript:alert(1)";
        doc.Elements["title"].Placement = new() { SectionId = "hero", Column = 12, Span = 12 };
        doc.Elements["title"].Style.BackgroundColor = "url(https://evil.invalid)";
        var result = WebsiteContentSanitizer.Sanitize(doc).Elements["title"];
        Assert.Null(result.Href); Assert.Null(result.Style.BackgroundColor);
        Assert.Equal(1, result.Placement!.Span);
        Assert.Null(WebsiteContentSanitizer.SanitizeUrl("https://example.com/?legendEdit=secret"));
    }
}
