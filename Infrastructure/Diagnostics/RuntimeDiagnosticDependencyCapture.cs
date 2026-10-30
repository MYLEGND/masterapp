using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Http;
using Shared.Diagnostics;

namespace Infrastructure.Diagnostics;

// Stateless observers: never inspect commands/connections, change results,
// suppress provider execution, wrap readers, or change cancellation/disposal.
internal sealed class RuntimeDiagnosticConnectionCapture : DbConnectionInterceptor
{
    internal static readonly RuntimeDiagnosticConnectionCapture Instance = new();
    private static void Record(TimeSpan duration, bool failed) => RuntimeDiagnosticScope.Current?.Dependency(RuntimeDiagnosticDependency.DatabaseConnection, duration, failed);
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData data) => Record(data.Duration, false);
    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData data, CancellationToken cancellationToken = default) { Record(data.Duration, false); return Task.CompletedTask; }
    public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData data) => Record(data.Duration, true);
    public override Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData data, CancellationToken cancellationToken = default) { Record(data.Duration, true); return Task.CompletedTask; }
    public override void ConnectionCanceled(DbConnection connection, ConnectionEndEventData data) => Record(data.Duration, true);
    public override Task ConnectionCanceledAsync(DbConnection connection, ConnectionEndEventData data, CancellationToken cancellationToken = default) { Record(data.Duration, true); return Task.CompletedTask; }
}
internal sealed class RuntimeDiagnosticCommandCapture : DbCommandInterceptor
{
    internal static readonly RuntimeDiagnosticCommandCapture Instance = new();
    private static void Record(TimeSpan duration, bool failed) => RuntimeDiagnosticScope.Current?.Dependency(RuntimeDiagnosticDependency.DatabaseCommand, duration, failed);
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData data, DbDataReader result) { Record(data.Duration, false); return result; }
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken cancellationToken = default) { Record(data.Duration, false); return ValueTask.FromResult(result); }
    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData data, object? result) { Record(data.Duration, false); return result; }
    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData data, object? result, CancellationToken cancellationToken = default) { Record(data.Duration, false); return ValueTask.FromResult(result); }
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData data, int result) { Record(data.Duration, false); return result; }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data, int result, CancellationToken cancellationToken = default) { Record(data.Duration, false); return ValueTask.FromResult(result); }
    public override void CommandFailed(DbCommand command, CommandErrorEventData data) => Record(data.Duration, true);
    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData data, CancellationToken cancellationToken = default) { Record(data.Duration, true); return Task.CompletedTask; }
    public override void CommandCanceled(DbCommand command, CommandEndEventData data) => Record(data.Duration, true);
    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData data, CancellationToken cancellationToken = default) { Record(data.Duration, true); return Task.CompletedTask; }
    // EF raises this for synchronous and asynchronous reader disposal. Lifetime
    // includes consumer reading/idle time; it is not exclusively database CPU.
    public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData data, InterceptionResult result)
    {
        RuntimeDiagnosticScope.Current?.Dependency(RuntimeDiagnosticDependency.DatabaseReaderLifetime, data.Duration, false);
        return result;
    }
}
internal sealed class RuntimeDiagnosticHttpCapture : DelegatingHandler
{
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var scope = RuntimeDiagnosticScope.Current;
        if (scope is null) return base.Send(request, cancellationToken);
        var start = Stopwatch.GetTimestamp(); var failed = true;
        try { var response = base.Send(request, cancellationToken); failed = !response.IsSuccessStatusCode; return response; }
        finally { try { scope.Dependency(RuntimeDiagnosticDependency.HttpClient, Stopwatch.GetElapsedTime(start), failed); } catch { } }
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var scope = RuntimeDiagnosticScope.Current;
        if (scope is null) return await base.SendAsync(request, cancellationToken);
        var start = Stopwatch.GetTimestamp();
        var failed = true;
        try { var response = await base.SendAsync(request, cancellationToken); failed = !response.IsSuccessStatusCode; return response; }
        finally { try { scope.Dependency(RuntimeDiagnosticDependency.HttpClient, Stopwatch.GetElapsedTime(start), failed); } catch { } }
    }
}
internal sealed class RuntimeDiagnosticHttpCaptureFilter : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        builder.AdditionalHandlers.Insert(0, new RuntimeDiagnosticHttpCapture());
    };
}
