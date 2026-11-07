using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Models;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Reception;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class ReceptionistOperatorTests
{
    private static ReceptionistSetupGuidePublishInput GuideEdit(ReceptionistSetupGuideProjection current, string suffix = "updated") =>
        new(current.Version, current.Revision,
            current.Business with { Title = "Business setup " + suffix },
            current.Founder! with { Title = "Founder setup " + suffix });

    [Fact]
    public async Task ReceptionistGuidePublicationIsGlobalAppendOnlyAuditedAndDoesNotChangeVoiceAuthority()
    {
        await using var f = await Fixture.CreateAsync();
        var service = f.Operator;
        var original = await service.SetupGuideForFounderAsync();
        Assert.Equal(1, original.Version); Assert.Equal(9, original.Business.Steps.Count);
        Assert.Equal(12, original.Founder!.Steps.Count);
        var before = JsonSerializer.Serialize(await f.Store.GetReceptionistConfigurationAsync(f.Owner, "openai"));
        var budgetBefore = await f.Admission.GetBudgetAccountAsync(f.OwnerBudgetId);
        var published = await service.PublishSetupGuideAsync(GuideEdit(original));
        Assert.Equal(2, published.Version); Assert.NotEqual(original.Revision, published.Revision);
        Assert.Equal(f.Clock.Now, published.PublishedUtc); Assert.Equal(DateTimeKind.Utc, published.PublishedUtc.Kind);
        foreach (var owner in new[] { MarketingOwnerScope.Business(Guid.NewGuid()), MarketingOwnerScope.Business(Guid.NewGuid()), MarketingOwnerScope.Agent(Guid.NewGuid()) })
        {
            var business = await service.SetupGuideForOwnerAsync(owner, (_, _) => Task.FromResult(true));
            Assert.Equal(published.Revision, business.Revision); Assert.Equal("Business setup updated", business.Business.Title);
            Assert.Null(business.Founder); Assert.Null(business.PublishUrl);
            Assert.DoesNotContain("Founder setup updated", JsonSerializer.Serialize(business));
        }
        Assert.Equal(before, JsonSerializer.Serialize(await f.Store.GetReceptionistConfigurationAsync(f.Owner, "openai")));
        Assert.Equal(JsonSerializer.Serialize(budgetBefore), JsonSerializer.Serialize(await f.Admission.GetBudgetAccountAsync(f.OwnerBudgetId)));
        await using var db = f.Database();
        var revisions = await db.Set<ReceptionistSetupGuideRevision>().OrderBy(x => x.Version).ToArrayAsync();
        Assert.Equal(2, revisions.Length); Assert.Equal(original.Revision, revisions[0].Revision);
        Assert.Equal(Fixture.FounderOid, revisions[1].CreatedByActorId);
        Assert.Empty(await db.Set<ReceptionistOperationPackage>().ToArrayAsync());
        Assert.Empty(await db.Set<ReceptionistAcceptanceWindow>().ToArrayAsync());
        var entity = db.Model.FindEntityType(typeof(ReceptionistSetupGuideRevision))!;
        Assert.Empty(entity.GetForeignKeys());
        Assert.Equal(new[] { "Version" }, entity.FindPrimaryKey()!.Properties.Select(x => x.Name));
    }

    [Fact]
    public async Task ReceptionistGuideRejectsStaleVersionRevisionAndReplayWithoutReplacingPublishedContent()
    {
        await using var f = await Fixture.CreateAsync();
        var original = await f.Operator.SetupGuideForFounderAsync();
        var edit = GuideEdit(original);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => f.Operator.PublishSetupGuideAsync(edit with { ExpectedRevision = Guid.NewGuid() }));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => f.Operator.PublishSetupGuideAsync(edit with { ExpectedVersion = original.Version + 1 }));
        var published = await f.Operator.PublishSetupGuideAsync(edit);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => f.Operator.PublishSetupGuideAsync(edit));
        Assert.Equal(published.Revision, (await f.Operator.SetupGuideForFounderAsync()).Revision);
        await using var db = f.Database(); Assert.Equal(2, await db.Set<ReceptionistSetupGuideRevision>().CountAsync());
    }

    [Fact]
    public async Task ReceptionistGuideConcurrentPublishersHaveOneAtomicWinner()
    {
        await using var f = await Fixture.CreateAsync();
        var original = await f.Operator.SetupGuideForFounderAsync();
        var barrier = new GuideReadBarrier();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>(f.Options).AddInterceptors(barrier).Options;
        using var services = new ServiceCollection().AddSingleton(options).BuildServiceProvider();
        var service = StoredService(f, services);
        async Task<Exception?> Publish(string label)
        {
            try { await service.PublishSetupGuideAsync(GuideEdit(original, label)); return null; }
            catch (Exception error) { return error; }
        }
        var outcomes = await Task.WhenAll(Publish("first"), Publish("second"));
        Assert.Single(outcomes.Where(x => x is null));
        Assert.IsType<DbUpdateConcurrencyException>(Assert.Single(outcomes.Where(x => x is not null)));
        await using var db = f.Database(); Assert.Equal(2, await db.Set<ReceptionistSetupGuideRevision>().CountAsync());
        var saved = await f.Operator.SetupGuideForFounderAsync();
        Assert.Equal(saved.Business.Title.Replace("Business", "Founder", StringComparison.Ordinal), saved.Founder!.Title);
    }

    private sealed class GuideReadBarrier : DbCommandInterceptor
    {
        private int reads;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.Ordinal) &&
                command.CommandText.Contains("ReceptionistSetupGuideRevisions", StringComparison.Ordinal) && Interlocked.Increment(ref reads) <= 2)
            {
                if (reads == 2) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceptionistGuideAuthorizationPrecedesStorageAndBusinessNeverReceivesFounderBody(bool anonymous)
    {
        await using var f = await Fixture.CreateAsync();
        var original = await f.Operator.SetupGuideForFounderAsync();
        f.Http.HttpContext!.User = anonymous ? new ClaimsPrincipal(new ClaimsIdentity()) :
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "00000000-0000-0000-0000-000000000099")], "test"));
        f.Commands.RefuseReads = true;
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Operator.SetupGuideForFounderAsync());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Operator.PublishSetupGuideAsync(GuideEdit(original)));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Operator.SetupGuideForOwnerAsync(f.Owner, (_, _) => Task.FromResult(false)));
            if (anonymous) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Operator.SetupGuideForOwnerAsync(f.Owner, (_, _) => Task.FromResult(true)));
        }
        finally { f.Commands.RefuseReads = false; }
        if (!anonymous)
        {
            var business = await f.Operator.SetupGuideForOwnerAsync(MarketingOwnerScope.Business(Guid.NewGuid()), (_, _) => Task.FromResult(true));
            Assert.Null(business.Founder); Assert.Null(business.PublishUrl);
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("title")]
    [InlineData("introduction")]
    [InlineData("step_title")]
    [InlineData("instructions")]
    [InlineData("control")]
    [InlineData("no_steps")]
    [InlineData("too_many_steps")]
    [InlineData("null_step")]
    [InlineData("escaped_size")]
    public async Task ReceptionistGuideInvalidOrOversizedEncodedContentNeverCreatesARevision(string invalid)
    {
        await using var f = await Fixture.CreateAsync();
        var original = await f.Operator.SetupGuideForFounderAsync();
        var document = new ReceptionistSetupGuideDocument("Setup", "Complete these steps.", [new("First step", "Do this.")]);
        document = invalid switch
        {
            "empty" => document with { Title = "  " },
            "title" => document with { Title = new string('t', 121) },
            "introduction" => document with { Introduction = new string('i', 601) },
            "step_title" => document with { Steps = [new(new string('t', 121), "Do this.")] },
            "instructions" => document with { Steps = [new("First", new string('i', 4001))] },
            "control" => document with { Introduction = "Invalid\0control" },
            "no_steps" => document with { Steps = [] },
            "too_many_steps" => document with { Steps = Enumerable.Repeat(new ReceptionistSetupGuideStep("Step", "Do this."), 17).ToArray() },
            "null_step" => document with { Steps = [null!] },
            "escaped_size" => document with { Steps = Enumerable.Repeat(new ReceptionistSetupGuideStep("Step", new string('\u2028', 4000)), 16).ToArray() },
            _ => throw new InvalidOperationException()
        };
        var writes = f.Commands.Writes;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => f.Operator.PublishSetupGuideAsync(GuideEdit(original) with { Business = document }));
        Assert.Equal(writes, f.Commands.Writes);
        Assert.Equal(original.Revision, (await f.Operator.SetupGuideForFounderAsync()).Revision);
    }

    [Fact]
    public async Task ReceptionistGuidePlainTextIsNormalizedWithoutBecomingHtmlAndUnavailableStorageHasNoFallback()
    {
        await using var f = await Fixture.CreateAsync();
        var original = await f.Operator.SetupGuideForFounderAsync();
        const string untrusted = "<img src=x onerror=alert(1)>\n<script>window.fixtureInjected=true</script>";
        var edited = GuideEdit(original) with { Business = new(" Plain text ", " Introduction\r\nsecond line ", [new(" Text ", untrusted)]) };
        var published = await f.Operator.PublishSetupGuideAsync(edited);
        Assert.Equal("Plain text", published.Business.Title); Assert.Equal("Introduction\nsecond line", published.Business.Introduction);
        Assert.Equal(untrusted, published.Business.Steps[0].Instructions);
        f.Commands.RefuseReads = true;
        try { await Assert.ThrowsAsync<TimeoutException>(() => f.Operator.SetupGuideForOwnerAsync(f.Owner, AllowOwner)); }
        finally { f.Commands.RefuseReads = false; }
        await using (var db = f.Database()) await db.Set<ReceptionistSetupGuideRevision>().ExecuteDeleteAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Operator.SetupGuideForOwnerAsync(f.Owner, AllowOwner));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Operator.PublishSetupGuideAsync(GuideEdit(published)));
    }

    [Fact]
    public async Task ReceptionistGuideBusinessEndpointPreservesOwnerScopeAndReturnsUnavailableWithoutAnOlderGuide()
    {
        var businessId = Guid.NewGuid();
        await using var f = await Fixture.CreateAsync(owner: MarketingOwnerScope.Business(businessId));
        using var services = new ServiceCollection().AddSingleton(f.Options)
            .AddSingleton<ReceptionistOperatorService>(sp => StoredService(f, sp)).BuildServiceProvider();
        f.Http.HttpContext!.RequestServices = services;
        var controller = new ScopedReceptionistController(businessId) { ControllerContext = new ControllerContext { HttpContext = f.Http.HttpContext } };
        var result = Assert.IsType<JsonResult>(await controller.ReceptionistGuide(businessId));
        var guide = Assert.IsType<ReceptionistSetupGuideProjection>(result.Value);
        Assert.Null(guide.Founder); Assert.Null(guide.PublishUrl);
        f.Commands.RefuseReads = true;
        try
        {
            Assert.IsType<ForbidResult>(await controller.ReceptionistGuide(Guid.NewGuid()));
            var unavailable = Assert.IsType<ObjectResult>(await controller.ReceptionistGuide(businessId));
            Assert.Equal(503, unavailable.StatusCode);
            Assert.DoesNotContain("Business setup", JsonSerializer.Serialize(unavailable.Value));
        }
        finally { f.Commands.RefuseReads = false; }
    }

    [Fact]
    public async Task ReceptionistGuideCorruptLatestRevisionDoesNotFallBackToAnEarlierPublishedGuide()
    {
        await using var f = await Fixture.CreateAsync();
        var original = await f.Operator.SetupGuideForFounderAsync();
        await f.Operator.PublishSetupGuideAsync(GuideEdit(original));
        await using (var db = f.Database())
        {
            var latest = await db.Set<ReceptionistSetupGuideRevision>().SingleAsync(x => x.Version == 2);
            latest.BusinessGuideJson = "not-json"; await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<JsonException>(() => f.Operator.SetupGuideForOwnerAsync(f.Owner, AllowOwner));
        await Assert.ThrowsAsync<JsonException>(() => f.Operator.SetupGuideForFounderAsync());
    }

    [Fact]
    public async Task ReceptionistGuideCommittedPublicationWithFailedReadbackRemainsUnverifiedAndCannotBeReplayed()
    {
        await using var f = await Fixture.CreateAsync();
        var original = await f.Operator.SetupGuideForFounderAsync(); var edit = GuideEdit(original);
        var options = new DbContextOptionsBuilder<MasterAppDbContext>(f.Options)
            .AddInterceptors(new GuideAfterSave(() => f.Commands.RefuseReads = true)).Options;
        using var services = new ServiceCollection().AddSingleton(options).BuildServiceProvider();
        try { await Assert.ThrowsAsync<TimeoutException>(() => StoredService(f, services).PublishSetupGuideAsync(edit)); }
        finally { f.Commands.RefuseReads = false; }
        var current = await f.Operator.SetupGuideForFounderAsync();
        Assert.Equal(2, current.Version); Assert.Equal(edit.Business.Title, current.Business.Title);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => f.Operator.PublishSetupGuideAsync(edit));
        await using var db = f.Database(); Assert.Equal(2, await db.Set<ReceptionistSetupGuideRevision>().CountAsync());
    }

    private sealed class GuideAfterSave(Action afterSave) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        { afterSave(); return ValueTask.FromResult(result); }
    }

    [Fact]
    public async Task ReceptionistGuideSqliteUpgradeSeedsOnlyANewTableAndPreservesPublishedRevisions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await MasterAppSqliteSchemaBootstrapper.InitializeAsync(db, NullLogger.Instance);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE ReceptionistSetupGuideRevisions;");
        var migrations = await db.Database.GetAppliedMigrationsAsync();
        await MasterAppSqliteSchemaBootstrapper.InitializeAsync(db, NullLogger.Instance);
        var seed = await db.Set<ReceptionistSetupGuideRevision>().AsNoTracking().SingleAsync(); Assert.Equal(1, seed.Version);
        db.Add(new ReceptionistSetupGuideRevision { Version = 2, Revision = Guid.NewGuid(), BusinessGuideJson = seed.BusinessGuideJson,
            FounderGuideJson = seed.FounderGuideJson, CreatedByActorId = "synthetic-published-founder", CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await MasterAppSqliteSchemaBootstrapper.InitializeAsync(db, NullLogger.Instance);
        Assert.Equal(2, await db.Set<ReceptionistSetupGuideRevision>().CountAsync());
        Assert.Equal("synthetic-published-founder", (await db.Set<ReceptionistSetupGuideRevision>().OrderByDescending(x => x.Version).FirstAsync()).CreatedByActorId);
        Assert.Equal(migrations, await db.Database.GetAppliedMigrationsAsync());
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("connections")]
    [InlineData("content")]
    [InlineData("founder")]
    public async Task ReceptionistGuideActualRazorPagesRenderOneCanonicalModalOutsideAllPageForms(string page)
    {
        await using var f = await Fixture.CreateAsync();
        var snapshot = await f.Store.GetReceptionistConfigurationAsync(f.Owner, "openai");
        object model = page switch
        {
            "setup" => new ReceptionistSetupPage(snapshot, ReceptionistSetupForms.Read(snapshot), "/setup", "/admission", "/return", [],
                ContentReviewUrl: "/content", ManagementUrl: "/connections", GuideUrl: "/canonical/guide"),
            "connections" => new ReceptionistManagementPage(new(Guid.NewGuid(), [], []), "/credential", "/revoke", "/setup", GuideUrl: "/canonical/guide"),
            "content" => new ReceptionistContentReviewPage(null, "/apply", "/restore", "/setup", GuideUrl: "/canonical/guide"),
            "founder" => new FounderReceptionistOperatorViewModel([]),
            _ => throw new InvalidOperationException()
        };
        var path = page switch { "setup" => "Shared/ReceptionistSetup", "connections" => "Shared/ReceptionistManagement",
            "content" => "Shared/ReceptionistContentReview", _ => "FounderEngineering/Receptionist" };
        using var host = await new HostBuilder().ConfigureWebHost(builder => builder.UseEnvironment("Testing").UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddRouting(); services.AddDataProtection();
                services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(f.Host);
                services.AddScoped(_ => f.Database());
                services.AddSingleton(new Moq.Mock<Infrastructure.Analytics.IAnalyticsQueryService>(Moq.MockBehavior.Strict).Object);
                services.AddScoped<Infrastructure.Businesses.BusinessWorkspaceService>();
                services.AddSingleton(new Moq.Mock<AgentPortal.Services.Tracking.IAgentTrackingService>(Moq.MockBehavior.Strict).Object);
                var shell = new Moq.Mock<Shared.PortalShell.IPortalShellIdentityResolver>(Moq.MockBehavior.Strict);
                shell.Setup(x => x.ResolveAsync(Moq.It.IsAny<ClaimsPrincipal>(), Moq.It.IsAny<HttpContext>(), Moq.It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new Shared.PortalShell.PortalShellIdentity("Synthetic render account", "Synthetic", "SR", "Synthetic",
                        "", "/store", "/profile", ""));
                services.AddSingleton(shell.Object);
                services.AddControllersWithViews().AddApplicationPart(typeof(FounderEngineeringController).Assembly)
                    .AddApplicationPart(Assembly.Load("AgentPortal.Views")).AddApplicationPart(typeof(ReceptionistSetupGuideLinks).Assembly);
            }).Configure(app => { app.UseRouting(); app.UseEndpoints(endpoints =>
            { endpoints.MapControllers(); endpoints.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}"); }); })).StartAsync();
        using var scope = host.Services.CreateScope(); var services = scope.ServiceProvider;
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Scheme = "https"; http.Request.Host = new HostString("fixture.test");
        var routes = new RouteData(); routes.Values["controller"] = page == "founder" ? "FounderEngineering" : "WebsiteAnalytics";
        routes.Values["action"] = "Receptionist";
        http.SetEndpoint(services.GetRequiredService<EndpointDataSource>().Endpoints.First(endpoint =>
            endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>() is { } action &&
            action.ControllerName == routes.Values["controller"]!.ToString() && action.ActionName == "Receptionist"));
        var context = new ActionContext(http, routes, new ActionDescriptor());
        var found = services.GetRequiredService<IRazorViewEngine>().GetView(null, "~/Views/" + path + ".cshtml", false);
        Assert.True(found.Success, path);
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary()) { Model = model };
        using var writer = new StringWriter();
        await found.View.RenderAsync(new ViewContext(context, found.View, data,
            new TempDataDictionary(http, services.GetRequiredService<ITempDataProvider>()), writer, new HtmlHelperOptions()));
        var html = writer.ToString();
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(html, "id=\"receptionist-setup-guide\"").Count);
        Assert.Contains("data-bs-target=\"#receptionist-setup-guide\"", html);
        Assert.Contains("data-guide-step-select", html); Assert.Contains("receptionist-workspace.js", html);
        Assert.DoesNotContain("founder-engineering.css", html);
        // All page forms must close before the shared guide's own antiforgery-protected editor form.
        var beforeModal = html[..html.IndexOf("id=\"receptionist-setup-guide\"", StringComparison.Ordinal)];
        Assert.Equal(System.Text.RegularExpressions.Regex.Matches(beforeModal, "<form\\b").Count,
            System.Text.RegularExpressions.Regex.Matches(beforeModal, "</form>").Count);
        Assert.Contains("__RequestVerificationToken", html[html.IndexOf("data-guide-editor", StringComparison.Ordinal)..]);
        Assert.DoesNotContain("system:initial-receptionist-setup-guide", html); // Contents are read freshly after opening.
    }
}
