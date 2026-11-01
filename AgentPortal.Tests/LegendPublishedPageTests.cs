using System;
using System.IO;
using System.Data.Common;
using System.Linq;
using System.Threading;
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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendPublishedPageTests
{
    private sealed class PublicationWriteInterceptor : DbCommandInterceptor
    {
        public Action? BeforeWrite { get; set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("CompiledPagesJson", StringComparison.Ordinal))
            {
                var action = BeforeWrite;
                BeforeWrite = null;
                action?.Invoke();
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public MasterAppDbContext Db { get; }
        public PublicationWriteInterceptor Writes { get; } = new();
        private readonly WebsiteEditorTicketProtector tickets = new(new EphemeralDataProtectionProvider());
        private readonly ServiceProvider services;
        public WebsitePlatformController Controller { get; }
        public WebsiteContentState State { get; }

        public Fixture(bool compilerAvailable = true)
        {
            connection.Open();
            Db = new(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).AddInterceptors(Writes).Options);
            Db.Database.EnsureCreated();
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

        public void Dispose() { services.Dispose(); tickets.Dispose(); Db.Dispose(); connection.Dispose(); }
    }

    [Fact]
    public async Task HtmlUsesPublishedVersionAndImmediatelyChangesWhenPublicationChanges()
    {
        using var f = new Fixture();
        await f.Publish("First publication");
        var published = await f.Db.Set<WebsiteContentVersion>().AsNoTracking()
            .SingleAsync(version => version.Id == f.State.PublishedVersionId);
        Assert.Null(published.CompiledPagesJson);
        var first = Assert.IsType<ContentResult>(await f.Controller.LegendPage());
        var materialized = await f.Db.Set<WebsiteContentVersion>().AsNoTracking()
            .SingleAsync(version => version.Id == published.Id);
        Assert.False(string.IsNullOrWhiteSpace(materialized.CompiledPagesJson));
        Assert.Equal(published.DocumentJson, materialized.DocumentJson);
        Assert.Equal(published.Id, await f.Db.Set<WebsiteContentState>().AsNoTracking()
            .Where(state => state.Id == f.State.Id).Select(state => state.PublishedVersionId).SingleAsync());
        Assert.Contains("First publication", first.Content);
        Assert.Equal(f.State.PublishedVersionId.ToString(), f.Controller.Response.Headers["X-Legend-Publication-Id"].ToString());
        Assert.DoesNotContain("Unpublished draft", first.Content);
        Assert.DoesNotContain("BUILD A LIFE WORTHY OF YOUR LEGACY", first.Content);
        Assert.Contains("https://www.mylegnd.com/", first.Content);
        Assert.Contains("no-store", f.Controller.Response.Headers.CacheControl.ToString());
        await f.Publish("Second publication");
        var second = Assert.IsType<ContentResult>(await f.Controller.LegendPage());
        Assert.Contains("Second publication", second.Content);
        Assert.Equal(f.State.PublishedVersionId.ToString(), f.Controller.Response.Headers["X-Legend-Publication-Id"].ToString());
        Assert.DoesNotContain("First publication", second.Content);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("\t\r\n", false)]
    [InlineData("\u2003", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("\t\r\n", true)]
    [InlineData("\u2003", true)]
    public async Task BlankCompiledPublicationIsMaterializedOnceWithoutChangingAuthoredContent(string? blank, bool protect)
    {
        using var f = new Fixture();
        if (protect)
        {
            var profile = new AgentTrackingProfile { AgentUserId = "blank-slot-owner", AgentUpn = "blank-slot@example.test", Slug = "blank-slot" };
            f.Db.Add(profile);
            f.State.OwnerKey = profile.AgentUserId;
            f.State.SiteKey = WebsiteEditorSiteKeys.Protect;
            f.Controller.HttpContext.Items["TrackingProfile"] = profile;
            f.Controller.ModelState.AddModelError("synthetic", "Preserved native validation");
        }
        await f.Publish("Published blank-slot fixture");
        async Task<string?> ReadPage()
        {
            if (!protect) return Assert.IsType<ContentResult>(await f.Controller.LegendPage()).Content;
            var page = await f.Controller.ResolveProtectPageAsync("/", CancellationToken.None);
            Assert.NotNull(page);
            Assert.False(f.Controller.ModelState.IsValid);
            return page.Main;
        }
        var id = f.State.PublishedVersionId!.Value;
        await f.Db.Set<WebsiteContentVersion>().Where(value => value.Id == id)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.CompiledPagesJson, blank));
        var before = await f.Db.Set<WebsiteContentVersion>().AsNoTracking().SingleAsync(value => value.Id == id);

        Assert.Contains("Published blank-slot fixture", await ReadPage());
        var after = await f.Db.Set<WebsiteContentVersion>().AsNoTracking().SingleAsync(value => value.Id == id);
        Assert.False(string.IsNullOrWhiteSpace(after.CompiledPagesJson));
        Assert.Equal(before.DocumentJson, after.DocumentJson);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(id, f.State.PublishedVersionId);

        // A fresh service cannot compile: the second request must use the persisted
        // immutable pages, not the first compiler instance's in-memory cache.
        using var unavailable = new ServiceCollection().AddSingleton(new WebsitePageCompiler(
            Mock.Of<IWebHostEnvironment>(value => value.ContentRootPath == "/missing-fixture-compiler"),
            new ConfigurationBuilder().Build())).BuildServiceProvider();
        f.Controller.HttpContext.RequestServices = unavailable;
        Assert.Contains("Published blank-slot fixture", await ReadPage());
        Assert.Equal(after.CompiledPagesJson, await f.Db.Set<WebsiteContentVersion>().AsNoTracking()
            .Where(value => value.Id == id).Select(value => value.CompiledPagesJson).SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaterializationDoesNotReplaceConcurrentOutputOrRetiredSnapshot(bool retire)
    {
        using var f = new Fixture();
        await f.Publish("Conditional materialization");
        var id = f.State.PublishedVersionId!.Value;
        await f.Db.Set<WebsiteContentVersion>().Where(value => value.Id == id)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.CompiledPagesJson, " "));
        var before = await f.Db.Set<WebsiteContentVersion>().AsNoTracking().SingleAsync(value => value.Id == id);
        var invoked = false;
        f.Writes.BeforeWrite = () =>
        {
            invoked = true;
            if (retire)
                f.Db.Set<WebsiteContentVersion>().Where(value => value.Id == id)
                    .ExecuteUpdate(update => update.SetProperty(value => value.SnapshotRetiredUtc, DateTime.UtcNow));
            else
                f.Db.Set<WebsiteContentVersion>().Where(value => value.Id == id)
                    .ExecuteUpdate(update => update.SetProperty(value => value.CompiledPagesJson, "concurrent nonblank output"));
        };
        Assert.IsType<ContentResult>(await f.Controller.LegendPage());
        Assert.True(invoked);
        var after = await f.Db.Set<WebsiteContentVersion>().AsNoTracking().SingleAsync(value => value.Id == id);
        Assert.Equal(retire ? " " : "concurrent nonblank output", after.CompiledPagesJson);
        Assert.Equal(retire, after.SnapshotRetiredUtc.HasValue);
        Assert.Equal(before.DocumentJson, after.DocumentJson);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(id, f.State.PublishedVersionId);
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

    [Theory]
    [InlineData("valid", "render", "render_timeout")]
    [InlineData("raw", "unknown", "unclassified_process_failure")]
    [InlineData("extra", "unknown", "unclassified_process_failure")]
    [InlineData("unknown_category", "unknown", "unclassified_process_failure")]
    [InlineData("duplicate", "unknown", "unclassified_process_failure")]
    [InlineData("prefixed", "unknown", "unclassified_process_failure")]
    [InlineData("invalid_elapsed", "unknown", "unclassified_process_failure")]
    [InlineData("oversized", "unknown", "unclassified_process_failure")]
    public async Task CompilerFailureLogsOnlyFixedDiagnostics(string kind, string expectedPhase, string expectedCategory)
    {
        const string canary = "private-draft-fixture@example.test";
        var metadata = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["schema"] = "legend-website-compiler-diagnostic/v1",
            ["phase"] = "render", ["category"] = "render_timeout", ["elapsedMs"] = 12
        };
        if (kind == "extra") metadata["message"] = canary;
        if (kind == "unknown_category") metadata["category"] = canary;
        if (kind == "invalid_elapsed") metadata["elapsedMs"] = canary;
        var envelope = JsonSerializer.Serialize(metadata);
        var stderr = kind switch
        {
            "raw" => "Error: " + canary,
            "duplicate" => envelope[..^1] + ",\"category\":" + JsonSerializer.Serialize(canary) + "}",
            "prefixed" => canary + "\n" + envelope,
            "oversized" => new string(' ', 1025) + envelope,
            _ => envelope
        };
        var root = Path.Combine(Path.GetTempPath(), "legend-compiler-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "linkedom"));
        try
        {
            // Controlled process-boundary fixture; it does not compile a website.
            await File.WriteAllTextAsync(Path.Combine(root, "scripts", "render-business-cli.mjs"),
                "for await (const chunk of process.stdin) {} process.stderr.write(" +
                JsonSerializer.Serialize(stderr) + "); process.exitCode=23;");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new System.Collections.Generic.Dictionary<string, string?> { ["WebsitePublishing:CompilerRoot"] = root }).Build();
            var environment = Mock.Of<IWebHostEnvironment>(e => e.ContentRootPath == root);
            var logger = new Mock<ILogger<WebsitePageCompiler>>();
            var compiler = new WebsitePageCompiler(environment, configuration, logger.Object);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                compiler.CompileLegendAsync("{}", CancellationToken.None));
            Assert.Equal("website_compilation_failed", error.Message);
            Assert.Null(error.InnerException);
            var invocation = Assert.Single(logger.Invocations.Where(value => value.Method.Name == "Log"));
            var fields = Assert.IsAssignableFrom<System.Collections.Generic.IEnumerable<
                System.Collections.Generic.KeyValuePair<string, object?>>>(invocation.Arguments[2])
                .ToDictionary(value => value.Key, value => value.Value);
            Assert.Equal(expectedPhase, fields["Phase"]);
            Assert.Equal(expectedCategory, fields["Category"]);
            Assert.Equal(23, Assert.IsType<int>(fields["ExitCode"]));
            Assert.True(Assert.IsType<long>(fields["ElapsedMs"]) >= 0);
            if (kind == "valid") Assert.Equal(12L, Assert.IsType<long>(fields["ChildElapsedMs"]));
            else Assert.Null(fields["ChildElapsedMs"]);
            Assert.Null(invocation.Arguments[3]);
            Assert.DoesNotContain(canary, invocation.Arguments[2].ToString()!);
            Assert.DoesNotContain(root, invocation.Arguments[2].ToString()!);
            Assert.DoesNotContain(canary, error.ToString());
        }
        finally { Directory.Delete(root, recursive: true); }
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
