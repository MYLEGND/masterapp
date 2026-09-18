using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Routing;

namespace Shared.Diagnostics;

public static class LegendFailureDiagnosticsExtensions
{
    public static IApplicationBuilder UseLegendFailureDiagnostics(this IApplicationBuilder app, string? appName = null)
    {
        appName ??= app.ApplicationServices.GetRequiredService<IHostEnvironment>().ApplicationName;
        return app.Use(async (context, next) =>
        {
            if (!context.Items.ContainsKey(AppFailureDiagnosticsBuilder.SnapshotItemKey))
                context.Items[AppFailureDiagnosticsBuilder.SnapshotItemKey] = AppFailureDiagnosticsBuilder.CaptureSnapshot(context);

            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Legend-Request-Id"] = context.TraceIdentifier;
                return Task.CompletedTask;
            });

            try
            {
                await next();
            }
            catch (Exception ex)
            {
                await CaptureExceptionAsync(context, ex, appName);
                throw;
            }

            var location = context.Response.Headers.Location.ToString();
            var redirectDepth = AppFailureDiagnosticsBuilder.CountReturnUrlDepth(location);
            if (context.Response.StatusCode is >= 300 and < 400 &&
                !string.IsNullOrWhiteSpace(location) &&
                (redirectDepth > 1 || location.Length > 2048))
            {
                context.Response.Headers["X-Legend-Redirect-Depth"] = redirectDepth.ToString();

                var logger = context.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger($"{appName}.FailureDiagnostics");

                logger.LogWarning(
                    "{AppName} suspicious redirect. requestId={RequestId} method={Method} status={StatusCode} redirectDepth={RedirectDepth}",
                    appName,
                    context.TraceIdentifier,
                    context.Request.Method,
                    context.Response.StatusCode,
                    redirectDepth);
            }
        });
    }

    // MVC exception filters can handle a fault before it reaches middleware.
    // Both boundaries use the same bounded capture; classification and durable
    // sanitization remain the responsibility of the existing diagnostic sink.
    public static async Task CaptureExceptionAsync(HttpContext context, Exception exception,
        string? appName = null, ILogger? logger = null)
    {
        // Deliberate request cancellation is expected and must not become a
        // server-defect report. The caller still owns its original response.
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested) return;
        try
        {
            // Resolution and logging are inside the guard as well: a broken
            // telemetry dependency must never replace the application fault.
            appName ??= context.RequestServices.GetService<IHostEnvironment>()?.ApplicationName ?? "Application";
            logger ??= context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger($"{appName}.FailureDiagnostics");
            logger?.LogError("{AppName} application {ExceptionType}; requestId={RequestId}",
                appName, exception.GetType().FullName, context.TraceIdentifier);
            var path = (context.Request.Path.Value ?? string.Empty).TrimEnd('/');
            if (string.Equals(path, "/api/runtime-diagnostics", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/api/v1/mobile/runtime-diagnostics", StringComparison.OrdinalIgnoreCase)) return;
            var sink = context.RequestServices.GetService<IRuntimeDiagnosticSink>();
            if (sink is null) return;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await sink.RecordAsync(new RuntimeDiagnosticEvent
            {
                AppIdentifier = appName,
                Platform = "server",
                Route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
                Operation = context.GetEndpoint()?.DisplayName is { } operation ? operation[..Math.Min(operation.Length, 128)] : null,
                ErrorName = exception.GetType().FullName is { } name ? name[..Math.Min(name.Length, 128)] : null,
                ErrorMessage = "Application exception",
                StackTrace = exception.StackTrace is { } stack ? stack[..Math.Min(stack.Length, 8192)] : null,
                CorrelationId = context.TraceIdentifier,
                Category = exception is OperationCanceledException ? "cancellation" : "unhandled_exception",
                StatusCode = 500,
                Timestamp = DateTimeOffset.UtcNow
            }, deadline.Token);
        }
        catch (Exception captureFailure)
        {
            try
            {
                logger?.LogWarning("Runtime diagnostic persistence failed ({FailureType}); requestId={RequestId}",
                    captureFailure.GetType().Name, context.TraceIdentifier);
            }
            catch (Exception) { /* Logging is optional; the original fault must survive. */ }
        }
    }
}
