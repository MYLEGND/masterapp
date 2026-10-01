using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Shared.Diagnostics;

public sealed record LegendSitePageIssue(
    string? ErrorName,
    int? StatusCode,
    string? Category,
    string? Operation,
    string? SourcePath);

public sealed record LegendRouteAuthority(
    string Route,
    string? Controller,
    string? Action,
    string? Assembly);

public sealed record LegendSitePageSnapshot(
    string? Path,
    int? ViewportWidth,
    int? ViewportHeight,
    double? DevicePixelRatio,
    string? Breakpoint,
    IReadOnlyList<string>? ComponentIds,
    IReadOnlyList<string>? ActionKeys,
    IReadOnlyList<string>? ModalIds,
    IReadOnlyList<string>? AssetPaths,
    IReadOnlyList<LegendSitePageIssue>? Issues);

/// <summary>
/// Server-enforced disclosure boundary for browser/site-tool page observations.
/// Browser values are untrusted and may only survive as bounded structural facts.
/// No DOM text, form values, URL query/fragment, headers, cookies, storage, request
/// bodies, response bodies, account identifiers or free-form exception text exist
/// in this contract.
/// </summary>
public static class LegendSiteToolDisclosureAuthority
{
    public const string CurrentPageToolName = "legend_current_page_diagnostics";

    private static readonly HashSet<string> ErrorNames = new(StringComparer.Ordinal)
    {
        "Error", "TypeError", "ReferenceError", "SyntaxError", "RangeError",
        "URIError", "EvalError", "AggregateError", "TimeoutError", "NetworkError",
        "OfflineError", "SecurityError", "NotAllowedError", "NotFoundError",
        "HttpError", "HttpFailure", "TransportFailure", "DecodeFailure",
        "OperationFailure", "UnhandledException", "AuthenticationFailure",
        "TaskCanceledException", "OperationCanceledException", "UnclassifiedError"
    };

    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    {
        "Authentication", "NetworkOrCapacity", "Network", "ExpectedCancellation",
        "ExpectedRequestFailure", "SuspectedDefect", "Observation"
    };

    public static object CurrentPageTool => new
    {
        type = "function",
        name = CurrentPageToolName,
        description = "Read the authenticated current page's privacy-safe structural diagnostics: live application/revision, canonical server route, viewport breakpoint, canonical component/action/modal identifiers, loaded same-origin JS/CSS assets, and bounded sanitized runtime issue codes. Never returns DOM text, field values, query strings, bodies, cookies, tokens, customer data, or private messages.",
        parameters = new
        {
            type = "object",
            properties = new { },
            required = Array.Empty<string>(),
            additionalProperties = false
        },
        strict = true
    };

    public static object SanitizePage(
        LegendSitePageSnapshot? snapshot,
        string applicationName,
        string scopeClassification,
        string? sourceRevision,
        LegendRouteAuthority routeAuthority)
    {
        snapshot ??= new LegendSitePageSnapshot(null, null, null, null, null, null, null, null, null, null);
        var width = snapshot.ViewportWidth is >= 240 and <= 10000 ? snapshot.ViewportWidth : null;
        var height = snapshot.ViewportHeight is >= 240 and <= 10000 ? snapshot.ViewportHeight : null;
        double? dpr = snapshot.DevicePixelRatio is >= 0.5 and <= 8 && double.IsFinite(snapshot.DevicePixelRatio.Value)
            ? Math.Round(snapshot.DevicePixelRatio.Value, 2) : null;

        var issues = (snapshot.Issues ?? Array.Empty<LegendSitePageIssue>())
            .Take(18)
            .Select(issue => new
            {
                errorName = issue.ErrorName is not null && ErrorNames.Contains(issue.ErrorName) ? issue.ErrorName : "UnclassifiedError",
                statusCode = issue.StatusCode is >= 100 and <= 599 ? issue.StatusCode : null,
                category = issue.Category is not null && Categories.Contains(issue.Category) ? issue.Category : "Observation",
                operation = SafeOperation(issue.Operation),
                sourcePath = SafeAsset(issue.SourcePath)
            })
            .ToArray();

        return new
        {
            schemaVersion = 1,
            disclosureAuthority = nameof(LegendSiteToolDisclosureAuthority),
            application = SafeApplication(applicationName),
            scope = SafeScope(scopeClassification),
            sourceRevision = IsSha(sourceRevision) ? sourceRevision!.ToLowerInvariant() : null,
            route = routeAuthority.Route,
            routeAuthority = new
            {
                controller = SafeSymbol(routeAuthority.Controller, 96),
                action = SafeSymbol(routeAuthority.Action, 96),
                assembly = SafeSymbol(routeAuthority.Assembly, 96)
            },
            viewport = new
            {
                width,
                height,
                devicePixelRatio = dpr,
                breakpoint = snapshot.Breakpoint is "xs" or "sm" or "md" or "lg" or "xl" or "xxl"
                    ? snapshot.Breakpoint : "unknown"
            },
            componentIds = SafeSymbols(snapshot.ComponentIds, 96),
            actionKeys = SafeSymbols(snapshot.ActionKeys, 96),
            modalIds = SafeSymbols(snapshot.ModalIds, 96),
            loadedAssets = (snapshot.AssetPaths ?? Array.Empty<string>())
                .Select(SafeAsset).Where(value => value is not null).Cast<string>().Distinct(StringComparer.Ordinal).Take(64).ToArray(),
            duplicateAssets = (snapshot.AssetPaths ?? Array.Empty<string>())
                .Select(SafeAsset).Where(value => value is not null).Cast<string>()
                .GroupBy(value => value, StringComparer.Ordinal).Where(group => group.Count() > 1)
                .Select(group => group.Key).Take(32).ToArray(),
            issues,
            privacy = new
            {
                domTextIncluded = false,
                inputValuesIncluded = false,
                requestBodiesIncluded = false,
                responseBodiesIncluded = false,
                queryStringsIncluded = false,
                cookiesIncluded = false,
                authorizationMaterialIncluded = false,
                privateCustomerDataIncluded = false
            }
        };
    }

    public static LegendRouteAuthority ResolveRouteAuthority(string? path, IEnumerable<EndpointDataSource> endpointSources)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || !path.StartsWith('/') ||
            path.StartsWith("//", StringComparison.Ordinal) || path.Contains('?') || path.Contains('#'))
            return new("/unmatched", null, null, null);

        foreach (var endpoint in endpointSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            var pattern = endpoint.RoutePattern.RawText;
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try
            {
                var matcher = new Microsoft.AspNetCore.Routing.Template.TemplateMatcher(
                    Microsoft.AspNetCore.Routing.Template.TemplateParser.Parse(pattern),
                    new RouteValueDictionary());
                if (!matcher.TryMatch(path, new RouteValueDictionary())) continue;
                var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
                return new(
                    SafeRoutePattern(pattern),
                    action?.ControllerTypeInfo.Name,
                    action?.ActionName,
                    action?.ControllerTypeInfo.Assembly.GetName().Name);
            }
            catch (ArgumentException) { }
        }

        return new("/unmatched", null, null, null);
    }

    public static string? EntryAssemblyRevision()
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var candidate = version?.Split('+').LastOrDefault();
        return IsSha(candidate) ? candidate!.ToLowerInvariant() : null;
    }

    private static string SafeApplication(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
            ? value : "RegisteredApplication";

    private static string SafeScope(string value) =>
        value is "founder_system" or "authenticated_client" ? value : "authenticated_limited";

    private static string SafeRoutePattern(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Contains('?') || value.Contains('#'))
            return "/unmatched";
        return Regex.IsMatch(value, @"\A/?[A-Za-z0-9_{}:./-]*\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
            ? "/" + value.TrimStart('/') : "/unmatched";
    }

    private static string? SafeOperation(string? value) =>
        value is not null && value.Length <= 64 &&
        Regex.IsMatch(value, @"\A[A-Za-z][A-Za-z0-9_-]*\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
            ? value : null;

    private static string[] SafeSymbols(IReadOnlyList<string>? values, int maximumLength) =>
        (values ?? Array.Empty<string>())
            .Select(value => SafeSymbol(value, maximumLength))
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Take(64)
            .ToArray();

    private static string? SafeSymbol(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            !Regex.IsMatch(value, @"\A[A-Za-z][A-Za-z0-9_.:-]*\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)) ||
            Guid.TryParse(value, out _) ||
            Regex.IsMatch(value, @"[0-9]{8,}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)) ||
            Regex.IsMatch(value, @"\A[a-fA-F0-9]{24,}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
            return null;
        return value;
    }

    private static string? SafeAsset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 220 || !value.StartsWith('/') ||
            value.StartsWith("//", StringComparison.Ordinal) || value.Contains('?') || value.Contains('#') ||
            value.Contains("..", StringComparison.Ordinal))
            return null;
        return Regex.IsMatch(value, @"\A/[A-Za-z0-9_./-]+\.(?:js|mjs|css)\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
            ? value : null;
    }

    private static bool IsSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);
}
