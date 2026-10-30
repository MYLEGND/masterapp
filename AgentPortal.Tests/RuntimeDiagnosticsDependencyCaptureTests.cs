using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shared.Diagnostics;
using Xunit;
namespace AgentPortal.Tests;
public sealed class RuntimeDiagnosticsDependencyCaptureTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RelationalSyncAndAsyncObserveConnectionCommandAndReaderLifetimeWithoutChangingResults(bool asynchronous)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        using var scope = new RuntimeDiagnosticScope();
        if (asynchronous) await db.Database.OpenConnectionAsync(); else db.Database.OpenConnection();
        var result = asynchronous ? await db.Database.SqlQueryRaw<int>("SELECT 7 AS Value").ToListAsync()
            : db.Database.SqlQueryRaw<int>("SELECT 7 AS Value").ToList();
        Assert.Equal(7, Assert.Single(result));
        var sample = scope.Snapshot("completed");
        Assert.Contains(sample.Dependencies, x => x.Kind == "database_connection" && x.Count == 1 && x.FailedCount == 0);
        Assert.Contains(sample.Dependencies, x => x.Kind == "database_command" && x.Count == 1 && x.FailedCount == 0);
        Assert.Contains(sample.Dependencies, x => x.Kind == "database_reader_lifetime" && x.Count == 1);
        Assert.NotNull(RuntimeDiagnosticSanitizer.SanitizePerformance(sample, true));
        if (asynchronous) await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("INVALID SQL"));
        else Assert.Throws<SqliteException>(() => db.Database.ExecuteSqlRaw("INVALID SQL"));
        Assert.Equal(1, scope.Snapshot("failed").Dependencies.Single(x => x.Kind == "database_command").FailedCount);
    }
    [Theory] [InlineData(200)] [InlineData(503)]
    public async Task HttpCapturePreservesResponseAndNeverCollectsUrlHeadersOrBody(int code)
    {
        var response = new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("private body") };
        using var handler = new RuntimeDiagnosticHttpCapture { InnerHandler = new Stub(_ => Task.FromResult(response)) };
        using var client = new HttpMessageInvoker(handler);
        using var scope = new RuntimeDiagnosticScope();
        var actual = await client.SendAsync(new(HttpMethod.Get, "https://private.example/customer?secret=private"), default);
        Assert.Same(response, actual); Assert.Equal("private body", await actual.Content.ReadAsStringAsync());
        var dependency = Assert.Single(scope.Snapshot("completed").Dependencies);
        Assert.Equal("http_client", dependency.Kind); Assert.Equal(code == 503 ? 1 : 0, dependency.FailedCount);
        Assert.DoesNotContain("private", RuntimeDiagnosticSanitizer.SanitizePerformance(scope.Snapshot("completed"), true)!);
    }
    [Fact]
    public async Task HttpCancellationAndSuppressionPreserveOriginalExceptionAndAvoidRecursion()
    {
        var original = new OperationCanceledException("private");
        using var handler = new RuntimeDiagnosticHttpCapture { InnerHandler = new Stub(_ => Task.FromException<HttpResponseMessage>(original)) };
        using var client = new HttpMessageInvoker(handler); using var scope = new RuntimeDiagnosticScope();
        using (RuntimeDiagnosticScope.Suppress())
            Assert.Same(original, await Assert.ThrowsAsync<OperationCanceledException>(() => client.SendAsync(new(HttpMethod.Get, "https://fixture.test"), default)));
        Assert.Empty(scope.Snapshot("failed").Dependencies);
        Assert.Same(original, await Assert.ThrowsAsync<OperationCanceledException>(() => client.SendAsync(new(HttpMethod.Get, "https://fixture.test"), default)));
        Assert.Equal(1, scope.Snapshot("failed").Dependencies.Single().FailedCount);
    }
    [Fact]
    public void SynchronousHttpCapturePreservesResponseAndFailureClassification()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        using var handler = new RuntimeDiagnosticHttpCapture { InnerHandler = new Stub(_ => Task.FromResult(response)) };
        using var client = new HttpMessageInvoker(handler); using var scope = new RuntimeDiagnosticScope();
        Assert.Same(response, client.Send(new(HttpMethod.Get, "https://fixture.test"), default));
        Assert.Equal(1, scope.Snapshot("completed").Dependencies.Single().FailedCount);
    }
    private sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => action(request).GetAwaiter().GetResult();
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request); }
}
