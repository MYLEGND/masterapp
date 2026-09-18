using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Shared.Diagnostics;

namespace Infrastructure.Diagnostics;

internal static partial class RuntimeDiagnosticSanitizer
{
    internal static bool IsBounded(RuntimeDiagnosticEvent value) =>
        new[] { value.AppIdentifier, value.Platform, value.Operation, value.CorrelationId,
            value.Category, value.AppVersion, value.ErrorName, value.GitCommitHash }.All(item => item is null || item.Length <= 128) &&
        (value.Route?.Length ?? 0) <= 1024 && (value.SourceFilePath?.Length ?? 0) <= 1024 &&
        (value.ErrorMessage?.Length ?? 0) <= 2048 && (value.StackTrace?.Length ?? 0) <= 8192 &&
        value.StatusCode is null or >= 100 and <= 599;

    internal static RuntimeDiagnosticIncident Sanitize(RuntimeDiagnosticEvent value, bool server,
        IHostEnvironment environment, HttpContext? context, IReadOnlyList<EndpointDataSource> endpoints, DateTime now)
    {
        // Exception messages, client paths/stacks, operation and correlation strings
        // can contain private user content even when they resemble identifiers.
        // They are never retained verbatim or merely regex-redacted.
        var name = value.ErrorName?.Split('.').Last() switch
        {
            "TypeError" => "TypeError", "ReferenceError" => "ReferenceError", "RangeError" => "RangeError",
            "SyntaxError" => "SyntaxError", "NullReferenceException" => "NullReferenceException",
            "InvalidOperationException" => "InvalidOperationException", "ArgumentException" => "ArgumentException",
            "HttpRequestException" => "HttpRequestException", "TimeoutException" => "TimeoutException",
            "NetworkError" => "NetworkError", "TimeoutError" => "TimeoutError", "OfflineError" => "OfflineError",
            "TaskCanceledException" => "TaskCanceledException", "OperationCanceledException" => "OperationCanceledException",
            "HttpFailure" => "HttpFailure", "TransportFailure" => "TransportFailure",
            "DecodeFailure" => "DecodeFailure", "OperationFailure" => "OperationFailure",
            "UnhandledException" => "UnhandledException", "AuthenticationFailure" => "AuthenticationFailure",
            "SqlException" => "SqlException", "OutOfMemoryException" => "OutOfMemoryException",
            _ => "UnclassifiedError"
        };
        var category = value.StatusCode switch
        {
            401 or 403 => "Authentication",
            408 or 429 or 502 or 503 or 504 => "NetworkOrCapacity",
            >= 400 and < 500 => "ExpectedRequestFailure",
            _ => name switch
            {
                "AuthenticationFailure" => "Authentication",
                "TransportFailure" or "HttpRequestException" or "TimeoutException" or "NetworkError" or "TimeoutError" or "OfflineError" => "Network",
                "TaskCanceledException" or "OperationCanceledException" => "ExpectedCancellation",
                "DecodeFailure" or "UnhandledException" or "TypeError" or "ReferenceError" or "SyntaxError" or "NullReferenceException" or "OutOfMemoryException" => "SuspectedDefect",
                _ => "Observation"
            }
        };
        var incident = new RuntimeDiagnosticIncident
        {
            Id = Guid.NewGuid(), AppIdentifier = ApplicationName(environment.ApplicationName),
            Platform = server ? "Server" : value.Platform?.ToLowerInvariant() switch
                { "ios" => "iOS", "android" => "Android", _ => "Web" },
            Route = SafeRoute(value.Route, context, endpoints, server), ErrorName = name,
            Category = category, StatusCode = value.StatusCode,
            Summary = category switch
            {
                "Authentication" => "Authentication or access was rejected.",
                "NetworkOrCapacity" => "A request encountered a timeout, capacity limit or unavailable service.",
                "Network" => "A transport interruption was observed.",
                "ExpectedCancellation" => "An operation was canceled.",
                "ExpectedRequestFailure" => "A request was rejected; this does not establish a software defect.",
                "SuspectedDefect" => "A runtime error requires investigation; a defect is not confirmed.",
                _ => "An unclassified runtime observation requires review."
            },
            FirstSeenUtc = now, LastSeenUtc = now, ExpiresUtc = now.AddDays(30)
        };
        if (server)
        {
            var assembly = Assembly.GetEntryAssembly();
            var version = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var candidate = version?.Split('+').Last();
            if (candidate is not null && Sha().IsMatch(candidate))
            {
                incident.GitCommitHash = candidate.ToLowerInvariant();
                incident.ReleaseVerified = true;
            }
            incident.AppVersion = assembly?.GetName().Version?.ToString();
            // Only repository-relative file/line locations from server capture;
            // no exception message, local user directory or method arguments.
            incident.SourceFilePath = SafeSource(value.SourceFilePath);
            var frames = (value.StackTrace ?? string.Empty).Split('\n').Take(64)
                .Select(SafeServerFrame)
                .Where(line => line is not null).Distinct().Take(12);
            incident.StackTrace = string.Join('\n', frames);
            if (incident.StackTrace.Length == 0) incident.StackTrace = null;
            var trace = System.Diagnostics.Activity.Current?.TraceId.ToString();
            incident.CorrelationId = trace is not null && TraceId().IsMatch(trace) ? trace : null;
        }
        else
        {
            incident.GitCommitHash = value.GitCommitHash is { } hash && Sha().IsMatch(hash) ? hash.ToLowerInvariant() : null;
            // A native/web advertised build is explicitly unverified, never the API host release.
            incident.AppVersion = value.AppVersion is { Length: <= 40 } appVersion && VersionNumber().IsMatch(appVersion) ? appVersion : null;
            incident.SourceFilePath = SafePublicSource(value.SourceFilePath, environment, context);
        }
        incident.DeduplicationKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            incident.AppIdentifier, incident.Platform, incident.Route, incident.ErrorName, incident.Category,
            incident.StatusCode, incident.GitCommitHash, incident.ReleaseVerified, incident.SourceFilePath,
            incident.StackTrace)))).ToLowerInvariant();
        return incident;
    }

    // Host metadata, never the client's advertised application identity.
    private static string ApplicationName(string value) =>
        value.Length is > 0 and <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')
            ? value : "RegisteredApplication";

    private static string SafeRoute(string? advertised, HttpContext? context, IReadOnlyList<EndpointDataSource> sources, bool server)
    {
        if (server && context?.GetEndpoint() is RouteEndpoint current) return BoundRoute(current.RoutePattern.RawText);
        if (advertised is null || !advertised.StartsWith('/') || advertised.StartsWith("//")) return "/unmatched";
        var path = advertised.Split('?', '#')[0];
        foreach (var endpoint in sources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            var pattern = endpoint.RoutePattern.RawText;
            if (pattern is null) continue;
            try
            {
                var matcher = new Microsoft.AspNetCore.Routing.Template.TemplateMatcher(Microsoft.AspNetCore.Routing.Template.TemplateParser.Parse(pattern), new RouteValueDictionary());
                if (matcher.TryMatch(path, new RouteValueDictionary())) return BoundRoute(pattern);
            }
            catch (ArgumentException) { }
        }
        return "/unmatched";
    }

    private static string BoundRoute(string? value) => value?.TrimStart('/') is { Length: <= 255 } route ? "/" + route : "/unmatched";
    private static string? SafeServerFrame(string line)
    {
        var location = Frame().Match(line);
        var method = MethodFrame().Match(line);
        if (location.Success && SafeSource(location.Groups[1].Value) is { } file)
            return (method.Success && method.Groups[1].Value.Length <= 180 ? method.Groups[1].Value + " " : string.Empty)
                + file + ":" + location.Groups[2].Value;
        // Structural metadata only: never parameters, exception text or locals.
        return method.Success && method.Groups[1].Value.Length <= 180 ? method.Groups[1].Value : null;
    }

    private static string? SafePublicSource(string? value, IHostEnvironment environment, HttpContext? context)
    {
        if (value is null || environment is not IWebHostEnvironment web) return null;
        if (!value.StartsWith('/') && Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (context is null || uri.Scheme != context.Request.Scheme ||
                !string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)) return null;
            value = uri.AbsolutePath;
        }
        if (value.StartsWith("//", StringComparison.Ordinal)) return null;
        var path = value.Split('?', '#')[0].TrimStart('/');
        if (path.Length > 179 || path.Contains("..", StringComparison.Ordinal) || !PublicSource().IsMatch(path)) return null;
        return web.WebRootFileProvider.GetFileInfo(path).Exists ? "/" + path : null;
    }
    private static string? SafeSource(string? value)
    {
        if (value is null) return null;
        var normalized = value.Replace('\\', '/');
        // PDB method identity and filename identify source without a hardcoded
        // app directory registry or exposing the build machine's private path.
        var file = normalized[(normalized.LastIndexOf('/') + 1)..];
        return file.Length <= 180 && SourcePath().IsMatch(file) && !file.Contains("..", StringComparison.Ordinal)
            ? file : null;
    }

    [GeneratedRegex("^[a-fA-F0-9]{40}$")] private static partial Regex Sha();
    [GeneratedRegex("^[a-f0-9]{32}$")] private static partial Regex TraceId();
    [GeneratedRegex(@"^[0-9]+(?:\.[0-9]+){0,3}(?:[+-][0-9]+)?(?: \([0-9]+\))?$")] private static partial Regex VersionNumber();
    [GeneratedRegex("^[A-Za-z0-9_/-]+\\.(?:cs|cshtml)$")] private static partial Regex SourcePath();
    [GeneratedRegex(@"\bin (.+):line ([0-9]{1,7})\s*$")] private static partial Regex Frame();
    [GeneratedRegex(@"^\s*at ([A-Za-z_][A-Za-z0-9_.+`<>]*)\(")] private static partial Regex MethodFrame();
    [GeneratedRegex(@"^(?:js|css)/[A-Za-z0-9_./-]+\.(?:js|css)$")] private static partial Regex PublicSource();
}
