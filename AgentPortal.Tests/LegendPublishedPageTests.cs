using System;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Infrastructure.WebsiteEditing.Controllers;
using Infrastructure.WebsitePublishing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendPublishedPageTests
{
    private sealed class Fixture : IDisposable
    {
        public MasterAppDbContext Db { get; } = new(new DbContextOptionsBuilder<MasterAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        private readonly WebsiteEditorTicketProtector tickets = new(new EphemeralDataProtectionProvider());
        private readonly ServiceProvider services;
        public WebsitePlatformController Controller { get; }
        public WebsiteContentState State { get; }

        public Fixture(bool compilerAvailable = true)
        {
            var compilerRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(SourceDirectory(), "..", "Legend-Website"));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
            {
                ["WebsitePublishing:CompilerRoot"] = compilerAvailable ? compilerRoot : System.IO.Path.Combine(compilerRoot, "missing-compiler")
            }).Build();
            var root = compilerAvailable ? AppContext.BaseDirectory : System.IO.Path.Combine(AppContext.BaseDirectory, "missing-compiler");
            var environment = Mock.Of<IWebHostEnvironment>(e => e.ContentRootPath == root);
            services = new ServiceCollection().AddSingleton(new WebsitePageCompiler(environment, config)).BuildServiceProvider();
            Controller = new(Db, tickets, config) { ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestServices = services } } };
            Controller.Request.Method = "GET";
            State = new WebsiteContentState { OwnerKey = WebsiteEditorSiteKeys.GlobalOwnerKey, SiteKey = WebsiteEditorSiteKeys.Legend, DraftJson = Document("Unpublished draft") };
            Db.Add(State);
            Db.SaveChanges();
        }

        public async Task Publish(string headline)
        {
            var version = new WebsiteContentVersion { StateId = State.Id, Revision = ++State.Revision, DocumentJson = Document(headline) };
            Db.Add(version);
            State.PublishedVersionId = version.Id;
            await Db.SaveChangesAsync();
        }

        private static string Document(string headline) => JsonSerializer.Serialize(new WebsiteContentDocument
        {
            Pages = new(StringComparer.Ordinal)
            {
                ["/"] = new WebsitePageDocument
                {
                    Title = "Published page", Description = "Published description",
                    Navigation = new WebsitePageNavigation { Label = "Home" },
                    Composition = [new WebsiteCompositionNode { Id = "home.title", Type = "heading", Tag = "h1", Text = headline }]
                }
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string file = "") => System.IO.Path.GetDirectoryName(file)!;

        public void Dispose() { services.Dispose(); tickets.Dispose(); Db.Dispose(); }
    }

    [Fact]
    public async Task HtmlUsesPublishedVersionAndImmediatelyChangesWhenPublicationChanges()
    {
        using var f = new Fixture();
        await f.Publish("First publication");
        var first = Assert.IsType<ContentResult>(await f.Controller.LegendPage());
        Assert.Contains("First publication", first.Content);
        Assert.DoesNotContain("Unpublished draft", first.Content);
        Assert.DoesNotContain("BUILD A LIFE WORTHY OF YOUR LEGACY", first.Content);
        Assert.Contains("https://www.mylegnd.com/", first.Content);
        Assert.Contains("no-store", f.Controller.Response.Headers.CacheControl.ToString());
        await f.Publish("Second publication");
        var second = Assert.IsType<ContentResult>(await f.Controller.LegendPage());
        Assert.Contains("Second publication", second.Content);
        Assert.DoesNotContain("First publication", second.Content);
    }

    [Fact]
    public async Task MissingAndInvalidRoutesNeverReturnStaticDefaults()
    {
        using var f = new Fixture();
        Assert.IsType<NotFoundResult>(await f.Controller.LegendPage());
        await f.Publish("Published");
        Assert.IsType<NotFoundResult>(await f.Controller.LegendPage("/missing"));
        Assert.IsType<NotFoundResult>(await f.Controller.LegendPage("/../private"));
        Assert.IsType<NotFoundResult>(await f.Controller.LegendPage("//example.com"));
    }

    [Fact]
    public async Task CompilerFailureReturnsUnavailableRatherThanSuccessfulStaleHtml()
    {
        using var f = new Fixture(compilerAvailable: false);
        await f.Publish("Published");
        var result = Assert.IsType<ObjectResult>(await f.Controller.LegendPage());
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("no-store", f.Controller.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task HeadHasHtmlHeadersButNoBody()
    {
        using var f = new Fixture();
        await f.Publish("Published");
        f.Controller.Request.Method = "HEAD";
        var result = Assert.IsType<ContentResult>(await f.Controller.LegendPage());
        Assert.Equal("", result.Content);
        Assert.Equal("text/html; charset=utf-8", result.ContentType);
    }
}
