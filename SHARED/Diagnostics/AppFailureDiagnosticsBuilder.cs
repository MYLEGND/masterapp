using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Shared.Diagnostics;

public static class AppFailureDiagnosticsBuilder
{
    internal const string SnapshotItemKey = "__LegendFailureSnapshot";

    public static AppFailureDiagnostics BuildForException(HttpContext context, string appName, Exception? exception)
    {
        var snapshot = GetOrCreateSnapshot(context);
        var statusCode = context.Response.StatusCode >= 400 ? context.Response.StatusCode : StatusCodes.Status500InternalServerError;
        var feature = context.Features.Get<IExceptionHandlerPathFeature>();
        var exceptionToUse = exception ?? feature?.Error;

        var failingPath = feature?.Path;
        var failingQuery = snapshot.QueryString;

        return BuildCore(
            context,
            appName,
            snapshot,
            statusCode,
            failureKind: "UnhandledException",
            summary: exceptionToUse?.Message ?? "An unhandled server exception occurred.",
            failingPath: string.IsNullOrWhiteSpace(failingPath) ? snapshot.Path : failingPath,
            failingQueryString: failingQuery,
            exception: exceptionToUse,
            responseLocation: context.Response.Headers.Location.ToString(),
            diagnosticHint: BuildHint(statusCode, CountReturnUrlDepth(failingQuery), exceptionToUse is not null));
    }

    public static AppFailureDiagnostics BuildForStatusCode(
        HttpContext context,
        string appName,
        int statusCode,
        string? summary = null,
        string? diagnosticHint = null)
    {
        var snapshot = GetOrCreateSnapshot(context);
        var feature = context.Features.Get<IStatusCodeReExecuteFeature>();

        var failingPath = feature?.OriginalPath;
        var failingQuery = feature?.OriginalQueryString;

        if (string.IsNullOrWhiteSpace(failingPath))
            failingPath = snapshot.Path;

        if (string.IsNullOrWhiteSpace(failingQuery))
            failingQuery = snapshot.QueryString;

        var returnUrlDepth = CountReturnUrlDepth(failingQuery);

        return BuildCore(
            context,
            appName,
            snapshot,
            statusCode,
            failureKind: statusCode == StatusCodes.Status403Forbidden ? "AccessDenied" : "HttpStatusCode",
            summary: string.IsNullOrWhiteSpace(summary) ? DefaultSummary(statusCode) : summary.Trim(),
            failingPath: failingPath,
            failingQueryString: failingQuery,
            exception: null,
            responseLocation: context.Response.Headers.Location.ToString(),
            diagnosticHint: string.IsNullOrWhiteSpace(diagnosticHint)
                ? BuildHint(statusCode, returnUrlDepth, hasException: false)
                : diagnosticHint.Trim());
    }

    public static AppFailureDiagnostics BuildForAccessDenied(
        HttpContext context,
        string appName,
        string? returnUrl = null,
        string? summary = null,
        string? diagnosticHint = null)
    {
        var snapshot = GetOrCreateSnapshot(context);
        var failingTarget = NormalizeLocalReturnUrl(returnUrl);
        var failingPath = snapshot.Path;
        var failingQuery = snapshot.QueryString;

        if (!string.IsNullOrWhiteSpace(failingTarget))
        {
            var splitIndex = failingTarget.IndexOf('?', StringComparison.Ordinal);
            if (splitIndex >= 0)
            {
                failingPath = failingTarget[..splitIndex];
                failingQuery = failingTarget[splitIndex..];
            }
            else
            {
                failingPath = failingTarget;
                failingQuery = string.Empty;
            }
        }

        var returnUrlDepth = CountReturnUrlDepth(failingQuery);

        return BuildCore(
            context,
            appName,
            snapshot,
            StatusCodes.Status403Forbidden,
            failureKind: "AccessDenied",
            summary: string.IsNullOrWhiteSpace(summary)
                ? "The current signed-in user does not have permission to open this route."
                : summary.Trim(),
            failingPath: failingPath,
            failingQueryString: failingQuery,
            exception: null,
            responseLocation: context.Response.Headers.Location.ToString(),
            diagnosticHint: string.IsNullOrWhiteSpace(diagnosticHint)
                ? BuildHint(StatusCodes.Status403Forbidden, returnUrlDepth, hasException: false)
                : diagnosticHint.Trim());
    }

    public static bool RequestPrefersJson(HttpRequest request)
    {
        if (request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
            return true;

        var accept = request.Headers.Accept.ToString();
        return accept.Contains("application/json", StringComparison.OrdinalIgnoreCase) ||
               accept.Contains("text/json", StringComparison.OrdinalIgnoreCase);
    }

    public static AppRequestSnapshot CaptureSnapshot(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var routeValues = context.GetRouteData()?.Values;

        return new AppRequestSnapshot(
            Method: context.Request.Method,
            Path: context.Request.Path.ToString(),
            QueryString: context.Request.QueryString.ToString(),
            EndpointDisplayName: endpoint?.DisplayName,
            RouteValuesSummary: FormatRouteValues(routeValues),
            IsAuthenticated: context.User.Identity?.IsAuthenticated == true,
            UserDisplay: ResolveUserDisplay(context));
    }

    public static int CountReturnUrlDepth(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        var decoded = value;
        for (var i = 0; i < 3; i++)
        {
            var unescaped = Uri.UnescapeDataString(decoded);
            if (string.Equals(unescaped, decoded, StringComparison.Ordinal))
                break;

            decoded = unescaped;
        }

        return CountOccurrences(decoded, "returnUrl=");
    }

    private static AppFailureDiagnostics BuildCore(
        HttpContext context,
        string appName,
        AppRequestSnapshot snapshot,
        int statusCode,
        string failureKind,
        string summary,
        string? failingPath,
        string? failingQueryString,
        Exception? exception,
        string? responseLocation,
        string? diagnosticHint)
    {
        var returnUrlDepth = Math.Max(
            CountReturnUrlDepth(failingQueryString),
            CountReturnUrlDepth(responseLocation));

        return new AppFailureDiagnostics
        {
            AppName = appName,
            FailureKind = failureKind,
            StatusCode = statusCode,
            Summary = summary,
            RequestId = context.TraceIdentifier,
            OccurredUtc = DateTime.UtcNow,
            RequestMethod = snapshot.Method,
            CurrentPath = context.Request.Path.ToString(),
            CurrentQueryString = context.Request.QueryString.ToString(),
            FailingPath = string.IsNullOrWhiteSpace(failingPath) ? snapshot.Path : failingPath,
            FailingQueryString = string.IsNullOrWhiteSpace(failingQueryString) ? snapshot.QueryString : failingQueryString,
            EndpointDisplayName = snapshot.EndpointDisplayName,
            RouteValuesSummary = snapshot.RouteValuesSummary,
            IsAuthenticated = snapshot.IsAuthenticated,
            UserDisplay = snapshot.UserDisplay,
            ExceptionType = exception?.GetType().FullName,
            ExceptionMessage = exception?.Message,
            InnerExceptionType = exception?.InnerException?.GetType().FullName,
            InnerExceptionMessage = exception?.InnerException?.Message,
            FailingPoint = FindFailingPoint(exception),
            StackTrace = FormatStackTrace(exception),
            ReturnUrlDepth = returnUrlDepth,
            DiagnosticHint = string.IsNullOrWhiteSpace(diagnosticHint)
                ? BuildHint(statusCode, returnUrlDepth, exception is not null)
                : diagnosticHint,
            ResponseLocation = string.IsNullOrWhiteSpace(responseLocation) ? null : responseLocation
        };
    }

    private static AppRequestSnapshot GetOrCreateSnapshot(HttpContext context)
    {
        if (context.Items.TryGetValue(SnapshotItemKey, out var existing) &&
            existing is AppRequestSnapshot snapshot)
        {
            return snapshot;
        }

        snapshot = CaptureSnapshot(context);
        context.Items[SnapshotItemKey] = snapshot;
        return snapshot;
    }

    private static string? FindFailingPoint(Exception? exception)
    {
        if (exception is null)
            return null;

        var trace = new StackTrace(exception, true);
        var frames = trace.GetFrames();
        if (frames is null || frames.Length == 0)
            return null;

        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            if (method?.DeclaringType is null)
                continue;

            var ns = method.DeclaringType.Namespace ?? string.Empty;
            if (!IsAppNamespace(ns))
                continue;

            return FormatFrame(method, frame);
        }

        var firstMethod = frames[0].GetMethod();
        return firstMethod is null ? null : FormatFrame(firstMethod, frames[0]);
    }

    private static string? FormatStackTrace(Exception? exception)
    {
        if (exception is null)
            return null;

        var trace = new StackTrace(exception, true);
        var frames = trace.GetFrames();
        if (frames is null || frames.Length == 0)
            return exception.StackTrace;

        var appFrames = frames
            .Where(frame => frame.GetMethod()?.DeclaringType?.Namespace is string ns && IsAppNamespace(ns))
            .Take(12)
            .Select(frame => frame.GetMethod() is MethodBase method ? FormatFrame(method, frame) : null)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Cast<string>()
            .ToList();

        if (appFrames.Count == 0)
            return exception.StackTrace;

        return string.Join(Environment.NewLine, appFrames);
    }

    private static string FormatFrame(MethodBase method, StackFrame frame)
    {
        var declaringType = method.DeclaringType?.FullName ?? "<unknown>";
        var fileName = frame.GetFileName();
        var sourceFile = string.IsNullOrWhiteSpace(fileName) ? null : Path.GetFileName(fileName);
        var lineNumber = frame.GetFileLineNumber();

        if (!string.IsNullOrWhiteSpace(sourceFile) && lineNumber > 0)
            return $"{declaringType}.{method.Name} ({sourceFile}:{lineNumber})";

        return $"{declaringType}.{method.Name}";
    }

    private static bool IsAppNamespace(string value)
    {
        return value.StartsWith("AgentPortal", StringComparison.Ordinal) ||
               value.StartsWith("ClientApp", StringComparison.Ordinal) ||
               value.StartsWith("Infrastructure", StringComparison.Ordinal) ||
               value.StartsWith("Shared", StringComparison.Ordinal) ||
               value.StartsWith("Domain", StringComparison.Ordinal);
    }

    private static string DefaultSummary(int statusCode)
    {
        return statusCode switch
        {
            StatusCodes.Status401Unauthorized => "Authentication is required before this route can be opened.",
            StatusCodes.Status403Forbidden => "The current session was denied access to this route.",
            StatusCodes.Status404NotFound => "The requested route was not matched by the application.",
            StatusCodes.Status429TooManyRequests => "The request was rate limited before it could complete.",
            >= 500 => "The server returned an internal error while handling this request.",
            _ => $"HTTP {statusCode} was returned for this request."
        };
    }

    private static string BuildHint(int statusCode, int returnUrlDepth, bool hasException)
    {
        if (returnUrlDepth > 1)
            return $"Detected nested returnUrl depth of {returnUrlDepth}. This usually means a redirect loop or repeated reuse of an already-wrapped returnUrl.";

        if (hasException)
            return "Review the exception type, message, and failing point below. This report is generated from the exact production failure context.";

        return statusCode switch
        {
            StatusCodes.Status401Unauthorized => "The request was blocked before authentication completed.",
            StatusCodes.Status403Forbidden => "The request reached the app but did not satisfy the route's access rules.",
            StatusCodes.Status404NotFound => "The request completed without a matching route or response body.",
            StatusCodes.Status429TooManyRequests => "The request hit a rate limit policy before the page could render.",
            >= 500 => "The request reached the app, but the server failed while trying to finish it.",
            _ => "The request returned a non-success HTTP status without throwing an unhandled exception."
        };
    }

    private static string? ResolveUserDisplay(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return null;

        return context.User.Identity?.Name ??
               context.User.FindFirst("preferred_username")?.Value ??
               context.User.FindFirst("email")?.Value ??
               context.User.FindFirst("oid")?.Value;
    }

    private static string? FormatRouteValues(RouteValueDictionary? routeValues)
    {
        if (routeValues is null || routeValues.Count == 0)
            return null;

        var parts = routeValues
            .Where(pair => pair.Value is not null)
            .Select(pair => $"{pair.Key}={pair.Value}")
            .ToArray();

        return parts.Length == 0 ? null : string.Join(", ", parts);
    }

    private static string? NormalizeLocalReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
            return null;

        return returnUrl.StartsWith("/", StringComparison.Ordinal) ? returnUrl : null;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(value, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}

public sealed record AppRequestSnapshot(
    string Method,
    string Path,
    string QueryString,
    string? EndpointDisplayName,
    string? RouteValuesSummary,
    bool IsAuthenticated,
    string? UserDisplay);
