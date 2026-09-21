using System;
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
    public async Task RollbackRestoresPublishedSnapshot_AndPreservesUnpublishedDraft()
    {
        using var f = new Fixture(); var token = f.Token;
        Body(await f.Controller.Save(new(token, Document("first"), 0)));
        var first = Body(await f.Controller.Publish(new(token, 1))).GetProperty("versionId").GetGuid();
        Body(await f.Controller.Save(new(token, Document("second"), 2)));
        Body(await f.Controller.Publish(new(token, 3)));
        Body(await f.Controller.Save(new(token, Document("unfinished"), 4)));
        Body(await f.Controller.Rollback(new(token, 5, first)));
        Assert.Equal("first", Body(await f.Controller.Public("legend")).GetProperty("document").GetProperty("elements").GetProperty("title").GetProperty("text").GetString());
        Assert.Equal("unfinished", Body(await f.Controller.Manage(token)).GetProperty("document").GetProperty("elements").GetProperty("title").GetProperty("text").GetString());
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
