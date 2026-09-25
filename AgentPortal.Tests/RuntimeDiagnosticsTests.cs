using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Security;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Diagnostics;
using Xunit;

namespace AgentPortal.Tests;

[Collection("LegendConnectFounderEnvironment")]
public sealed class RuntimeDiagnosticsTests
{
    [Theory]
    [InlineData("DecodeFailure", "SuspectedDefect", "1.0 (25)")]
    [InlineData("TransportFailure", "Network", "1.0.0 (7)")]
    [InlineData("AuthenticationFailure", "Authentication", "2.1")]
    public void NativeContract_PreservesSafeFailureTypeAndBuildWithoutConfirmingDefect(string name, string category, string version)
    {
        var row = RuntimeDiagnosticSanitizer.Sanitize(new() { Platform = "ios", ErrorName = name, AppVersion = version },
            false, new EnvironmentStub(), null, Routes(), DateTime.UtcNow);
        Assert.Equal(name, row.ErrorName);
        Assert.Equal(category, row.Category);
        Assert.Equal(version, row.AppVersion);
        Assert.Equal("Observed", row.Disposition);
        Assert.False(row.ReleaseVerified);
        var hostile = RuntimeDiagnosticSanitizer.Sanitize(new() { AppVersion = "1.0 (private@example.com)" },
            false, new EnvironmentStub(), null, Routes(), DateTime.UtcNow);
        Assert.Null(hostile.AppVersion);
    }

    [Fact]
    public void ClientPayload_RemovesPrivateContent_UsesRouteTemplate_AndNeverConfirmsReleaseOrDefect()
    {
        const string privateText = "private-person@example.test bearer-secret message-body";
        var result = RuntimeDiagnosticSanitizer.Sanitize(new RuntimeDiagnosticEvent
        {
            AppIdentifier = privateText, Platform = "iOS", Route = "/messages/private-account?token=secret",
            ErrorName = "TypeError", ErrorMessage = privateText, SourceFilePath = privateText,
            StackTrace = privateText, Operation = privateText, CorrelationId = privateText,
            GitCommitHash = new string('a', 40), Category = "ConfirmedDefect", AppVersion = "2.3.4"
        }, false, new EnvironmentStub(), new DefaultHttpContext(), Routes(), DateTime.UtcNow);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(privateText, json);
        Assert.DoesNotContain("private-account", json);
        Assert.DoesNotContain("secret", json);
        Assert.Equal("/messages/{id}", result.Route);
        Assert.Equal("AgentPortal", result.AppIdentifier);
        Assert.Equal("SuspectedDefect", result.Category);
        Assert.Equal("Observed", result.Disposition);
        Assert.False(result.ReleaseVerified);
        Assert.Equal(new string('a', 40), result.GitCommitHash);
        Assert.Null(result.StackTrace);
        Assert.Null(result.CorrelationId);
    }

    [Theory]
    [InlineData(403, "Authentication")]
    [InlineData(429, "NetworkOrCapacity")]
    [InlineData(503, "NetworkOrCapacity")]
    [InlineData(400, "ExpectedRequestFailure")]
    public void ExpectedFailures_AreNotConfirmedDefects(int status, string category)
    {
        var result = RuntimeDiagnosticSanitizer.Sanitize(new() { StatusCode = status, ErrorName = "TypeError" },
            false, new EnvironmentStub(), null, Routes(), DateTime.UtcNow);
        Assert.Equal(category, result.Category);
        Assert.Equal("Observed", result.Disposition);
    }

    [Theory]
    [InlineData("NetworkError")]
    [InlineData("TimeoutError")]
    [InlineData("OfflineError")]
    public void BrowserTransportNames_DoNotMasqueradeAsScriptDefects(string name)
    {
        var row = RuntimeDiagnosticSanitizer.Sanitize(new() { ErrorName = name }, false,
            new EnvironmentStub(), null, Routes(), DateTime.UtcNow);
        Assert.Equal("Network", row.Category);
        Assert.Equal("Observed", row.Disposition);
    }

    [Fact]
    public void PublicClientSource_MustExistInHostStaticFiles_AndCannotCarryAnExternalUrlOrPrivatePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "legend-diagnostic-static-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "js"));
        File.WriteAllText(Path.Combine(directory, "js", "application.js"), "// static fixture");
        try
        {
            using var files = new PhysicalFileProvider(directory);
            var environment = new WebEnvironmentStub { WebRootFileProvider = files };
            var context = new DefaultHttpContext();
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("example.test");
            RuntimeDiagnosticIncident Sanitize(string source) => RuntimeDiagnosticSanitizer.Sanitize(
                new() { SourceFilePath = source }, false, environment, context, Routes(), DateTime.UtcNow);
            Assert.Equal("/js/application.js", Sanitize("https://example.test/js/application.js?token=private").SourceFilePath);
            Assert.Equal("/js/application.js", Sanitize("/js/application.js").SourceFilePath);
            Assert.Null(Sanitize("https://evil.test/js/application.js").SourceFilePath);
            Assert.Null(Sanitize("/uploads/private-user.js").SourceFilePath);
            Assert.Null(Sanitize("/js/not-installed.js").SourceFilePath);
            Assert.Null(Sanitize("/js/../private.js").SourceFilePath);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ServerFrames_RetainStructuralContextWithoutArgumentsOrAbsoluteDirectories()
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(Routes()[0].Endpoints[0]);
        var result = RuntimeDiagnosticSanitizer.Sanitize(new()
        {
            Route = "/private-user", ErrorMessage = "private-message", GitCommitHash = new string('f', 40),
            StackTrace = " at Infrastructure.Service.Read(secret-value) in /Users/private-user/repo/Infrastructure/Service.cs:line 123\n" +
                         " at Infrastructure.Service.Run(secret-value)\nBearer private-token\n"
        }, true, new EnvironmentStub(), context, Routes(), DateTime.UtcNow);
        Assert.Equal("Server", result.Platform);
        Assert.Equal("/messages/{id}", result.Route);
        Assert.Equal("Infrastructure.Service.Read Service.cs:123\nInfrastructure.Service.Run", result.StackTrace);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result));
        Assert.NotEqual(new string('f', 40), result.GitCommitHash);
    }

    [Fact]
    public void Fingerprint_RemainsStableAcrossHours_ButSeparatesAdvertisedReleases()
    {
        var now = DateTime.UtcNow;
        var input = new RuntimeDiagnosticEvent { ErrorName = "TypeError", GitCommitHash = new string('a', 40) };
        var original = RuntimeDiagnosticSanitizer.Sanitize(input, false, new EnvironmentStub(), null, Routes(), now);
        var later = RuntimeDiagnosticSanitizer.Sanitize(input, false, new EnvironmentStub(), null, Routes(), now.AddHours(2));
        var nextRelease = RuntimeDiagnosticSanitizer.Sanitize(input with { GitCommitHash = new string('b', 40) }, false,
            new EnvironmentStub(), null, Routes(), now.AddHours(2));
        Assert.Equal(original.DeduplicationKey, later.DeduplicationKey);
        Assert.NotEqual(original.DeduplicationKey, nextRelease.DeduplicationKey);
        Assert.False(nextRelease.ReleaseVerified);
    }

    [Fact]
    public async Task RelationalIngestion_Deduplicates_BoundsAdmission_PrunesExpired_AndCannotSaveCallerChanges()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await using var callerScope = fixture.Services.CreateAsyncScope();
        var caller = callerScope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        caller.RuntimeDiagnosticIncidents.Add(new RuntimeDiagnosticIncident
        {
            Id = Guid.NewGuid(), DeduplicationKey = new string('e', 64), FirstSeenUtc = DateTime.UtcNow.AddDays(-31),
            LastSeenUtc = DateTime.UtcNow.AddDays(-31), ExpiresUtc = DateTime.UtcNow.AddDays(-1)
        });
        await caller.SaveChangesAsync();
        var unsaved = new RuntimeDiagnosticIncident { Id = Guid.NewGuid(), DeduplicationKey = new string('d', 64) };
        caller.RuntimeDiagnosticIncidents.Add(unsaved);
        var observation = new RuntimeDiagnosticEvent { Route = "/messages/person-one", ErrorName = "TypeError" };
        for (var index = 0; index < 20; index++)
            Assert.Equal(RuntimeDiagnosticAdmissionResult.Recorded, await fixture.Store.RecordClientAsync(observation, new ClaimsPrincipal()));
        Assert.Equal(RuntimeDiagnosticAdmissionResult.RateLimited, await fixture.Store.RecordClientAsync(observation, new ClaimsPrincipal()));
        await using var checkScope = fixture.Services.CreateAsyncScope();
        var check = checkScope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var row = Assert.Single(await check.RuntimeDiagnosticIncidents.ToListAsync());
        Assert.Equal(20, row.Occurrences);
        Assert.Equal("Observed", row.Disposition);
        Assert.Equal("/messages/{id}", row.Route);
        Assert.Equal(EntityState.Added, caller.Entry(unsaved).State);
        Assert.NotEqual(unsaved.Id, row.Id);
    }

    [Fact]
    public async Task Ingestion_RejectsOversize_AndNeverAcknowledgesFailedPersistence()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var controller = new RuntimeDiagnosticsController(fixture.Store, NullLogger<RuntimeDiagnosticsController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<BadRequestResult>(await controller.Post(new() { ErrorMessage = new string('x', 2049) }, default));
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        Assert.Empty(await db.RuntimeDiagnosticIncidents.ToListAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TABLE RuntimeDiagnosticIncidents");
        var failed = Assert.IsType<StatusCodeResult>(await controller.Post(new() { ErrorName = "TypeError" }, default));
        Assert.Equal(503, failed.StatusCode);
    }

    [Fact]
    public async Task CallerCancellation_IsNotConvertedIntoAcceptedOrBackgroundPersistence()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.RecordClientAsync(new(), new ClaimsPrincipal(), cancellation.Token));
        await using var scope = fixture.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<MasterAppDbContext>().RuntimeDiagnosticIncidents.ToListAsync());
    }

    [Fact]
    public async Task AnonymousHttpIngestion_RequiresSameOriginAntiforgery_AndReturnsNoDiagnosticContent()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<MasterAppDbContext>(options => options.UseSqlite(fixture.Connection));
        builder.Services.AddRuntimeDiagnostics(new ConfigurationBuilder().Build(), new EnvironmentStub());
        builder.Services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
        await using var app = builder.Build();
        app.MapControllers();
        app.MapGet("/fixture-token", async context =>
        {
            var tokens = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
            await context.Response.WriteAsJsonAsync(new { token = tokens.RequestToken });
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var rejected = await client.PostAsJsonAsync("/api/runtime-diagnostics", new RuntimeDiagnosticEvent { ErrorName = "TypeError" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var tokenResponse = await client.GetAsync("/fixture-token");
        tokenResponse.EnsureSuccessStatusCode();
        using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("Cookie", tokenResponse.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
        client.DefaultRequestHeaders.Add("RequestVerificationToken", tokenJson.RootElement.GetProperty("token").GetString());
        using var accepted = await client.PostAsJsonAsync("/api/runtime-diagnostics", new RuntimeDiagnosticEvent { ErrorName = "TypeError" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(string.Empty, await accepted.Content.ReadAsStringAsync());
        await using var scope = fixture.Services.CreateAsyncScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<MasterAppDbContext>().RuntimeDiagnosticIncidents.ToListAsync());
    }

    [Fact]
    public async Task FounderManagement_FailsClosedBeforeQuery_AndMutationsRequireAntiforgery()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var controller = new FounderDiagnosticsController(scope.ServiceProvider.GetRequiredService<MasterAppDbContext>())
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        await Assert.ThrowsAsync<ForbidResultException>(() => controller.Index());
        await Assert.ThrowsAsync<ForbidResultException>(() => controller.Details(Guid.NewGuid(), default));
        await Assert.ThrowsAsync<ForbidResultException>(() => controller.Disposition(Guid.NewGuid(), "ConfirmedDefect", 0, true, default));
        Assert.NotNull(typeof(FounderDiagnosticsController).GetCustomAttribute<FounderOnlyAttribute>());
        Assert.NotNull(typeof(FounderDiagnosticsController).GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(typeof(FounderDiagnosticsController).GetMethod(nameof(FounderDiagnosticsController.Disposition))!
            .GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.NotNull(typeof(RuntimeDiagnosticsController).GetMethod(nameof(RuntimeDiagnosticsController.Post))!
            .GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.NotNull(typeof(RuntimeDiagnosticsController).GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public async Task FounderReview_RequiresExplicitConfirmation_AndRejectsStaleVersions()
    {
        var previous = Environment.GetEnvironmentVariable("FOUNDER_OID");
        const string oid = "005f0d0e-cc5c-4b60-93f6-bb9b1b9f9588";
        Environment.SetEnvironmentVariable("FOUNDER_OID", oid);
        try
        {
            await using var fixture = await StoreFixture.CreateAsync();
            await fixture.Store.RecordClientAsync(new() { ErrorName = "TypeError" }, new ClaimsPrincipal());
            await using var scope = fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            var row = await db.RuntimeDiagnosticIncidents.SingleAsync();
            var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", oid) }, "test")) };
            var controller = new FounderDiagnosticsController(db) { ControllerContext = new() { HttpContext = context } };
            Assert.IsType<BadRequestResult>(await controller.Disposition(row.Id, "ConfirmedDefect", 0, false, default));
            Assert.Equal("Observed", row.Disposition);
            Assert.IsType<RedirectToActionResult>(await controller.Disposition(row.Id, "ConfirmedDefect", 0, true, default));
            Assert.Equal(1, row.ReviewVersion);
            Assert.Equal("ConfirmedDefect", row.Disposition);
            Assert.IsType<ConflictResult>(await controller.Disposition(row.Id, "Dismissed", 0, false, default));
            Assert.Equal("ConfirmedDefect", row.Disposition);
            Assert.IsType<RedirectToActionResult>(await controller.Disposition(row.Id, "ManuallyClosed", 1, false, default));
            await fixture.Store.RecordClientAsync(new() { ErrorName = "TypeError" }, new ClaimsPrincipal());
            // This context still tracks the pre-observation version. The SQL
            // concurrency predicate must reject it, not only the form check.
            Assert.IsType<ConflictResult>(await controller.Disposition(row.Id, "ManuallyClosed", 2, false, default));
            await db.Entry(row).ReloadAsync();
            Assert.True(row.Recurred);
            Assert.Equal("ManuallyClosed", row.Disposition);
            Assert.Equal(2, row.Occurrences);
            Assert.Equal(3, row.ReviewVersion);
            Assert.IsType<ConflictResult>(await controller.Disposition(row.Id, "ManuallyClosed", 2, false, default));
            Assert.True(row.Recurred);
        }
        finally { Environment.SetEnvironmentVariable("FOUNDER_OID", previous); }
    }

    private static EndpointDataSource[] Routes() => new EndpointDataSource[]
    {
        new DefaultEndpointDataSource(new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("messages/{id}"), 0, EndpointMetadataCollection.Empty, "fixture"))
    };

    private class EnvironmentStub : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "AgentPortal";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class WebEnvironmentStub : EnvironmentStub, IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "/";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StoreFixture(SqliteConnection connection, ServiceProvider services) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection;
        public ServiceProvider Services { get; } = services;
        public RuntimeDiagnosticStore Store => Services.GetRequiredService<RuntimeDiagnosticStore>();
        public static async Task<StoreFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<MasterAppDbContext>(options => options.UseSqlite(connection));
            services.AddSingleton<EndpointDataSource>(Routes()[0]);
            services.AddRuntimeDiagnostics(new ConfigurationBuilder().Build(), new EnvironmentStub());
            var provider = services.BuildServiceProvider();
            provider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            // SQLite exercises atomic relational deduplication and concurrency,
            // without materializing unrelated SQL Server application schema.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE RuntimeDiagnosticIncidents (
                Id TEXT PRIMARY KEY, DeduplicationKey TEXT NOT NULL UNIQUE, AppIdentifier TEXT NOT NULL,
                Platform TEXT NOT NULL, Route TEXT NOT NULL, ErrorName TEXT NOT NULL, Summary TEXT NOT NULL,
                Category TEXT NOT NULL, StatusCode INTEGER NULL, GitCommitHash TEXT NULL, ReleaseVerified INTEGER NOT NULL,
                AppVersion TEXT NULL, SourceFilePath TEXT NULL, StackTrace TEXT NULL, CorrelationId TEXT NULL,
                Disposition TEXT NOT NULL, ReviewVersion INTEGER NOT NULL, Recurred INTEGER NOT NULL, ReviewedUtc TEXT NULL,
                FirstSeenUtc TEXT NOT NULL, LastSeenUtc TEXT NOT NULL, ExpiresUtc TEXT NOT NULL, Occurrences INTEGER NOT NULL);
                """);
            return new StoreFixture(connection, provider);
        }
        public async ValueTask DisposeAsync() { await Services.DisposeAsync(); await Connection.DisposeAsync(); }
    }
}
