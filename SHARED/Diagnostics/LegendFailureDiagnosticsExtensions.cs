using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shared.Diagnostics;

public static class LegendFailureDiagnosticsExtensions
{
    public static IApplicationBuilder UseLegendFailureDiagnostics(this IApplicationBuilder app, string appName)
    {
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
                var logger = context.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger($"{appName}.FailureDiagnostics");

                var diagnostics = AppFailureDiagnosticsBuilder.BuildForException(context, appName, ex);

                logger.LogError(
                    ex,
                    "{AppName} unhandled exception. requestId={RequestId} method={Method} failingTarget={FailingTarget} failingPoint={FailingPoint}",
                    diagnostics.AppName,
                    diagnostics.RequestId,
                    diagnostics.RequestMethod,
                    diagnostics.FailingRequestTarget,
                    diagnostics.FailingPoint ?? "<unknown>");

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
                    "{AppName} suspicious redirect. requestId={RequestId} method={Method} path={Path} status={StatusCode} redirectDepth={RedirectDepth} location={Location}",
                    appName,
                    context.TraceIdentifier,
                    context.Request.Method,
                    context.Request.Path,
                    context.Response.StatusCode,
                    redirectDepth,
                    location);
            }
        });
    }
}
