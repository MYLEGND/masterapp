using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using ParfaitApp.Models;
using ParfaitApp.Security;

namespace ParfaitApp.Services;

public interface IParfaitInternalPageRegistry
{
    IReadOnlyList<ParfaitInternalPageDefinition> GetAllPages();
    ParfaitInternalPageDefinition? ResolveByKey(string key);
    ParfaitInternalPageDefinition? ResolveByPath(string path);
}

public sealed class ParfaitInternalPageRegistry : IParfaitInternalPageRegistry
{
    private static readonly string[] ExcludedExactRoutes =
    [
        "/internal",
        "/internal/login",
        "/internal/denied",
        "/internal/analytics/meta-connect",
        "/internal/analytics/meta-callback",
        "/internal/analytics/meta-connection-status"
    ];

    private static readonly Dictionary<string, string> RouteAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/internal/analytics/meta-connect"] = "/internal/analytics",
        ["/internal/analytics/meta-callback"] = "/internal/analytics",
        ["/internal/analytics/meta-connection-status"] = "/internal/analytics",
        ["/internal/analytics/meta-disconnect"] = "/internal/analytics",
        ["/internal/analytics/meta-settings"] = "/internal/analytics"
    };

    private readonly IActionDescriptorCollectionProvider _actions;
    private readonly object _lock = new();
    private IReadOnlyList<ParfaitInternalPageDefinition>? _cache;
    private int _cacheVersion = -1;

    public ParfaitInternalPageRegistry(IActionDescriptorCollectionProvider actions)
    {
        _actions = actions;
    }

    public IReadOnlyList<ParfaitInternalPageDefinition> GetAllPages()
    {
        var version = _actions.ActionDescriptors.Version;
        if (_cache is not null && _cacheVersion == version)
            return _cache;

        lock (_lock)
        {
            version = _actions.ActionDescriptors.Version;
            if (_cache is not null && _cacheVersion == version)
                return _cache;

            var pages = BuildPages();
            _cache = pages;
            _cacheVersion = version;
            return _cache;
        }
    }

    public ParfaitInternalPageDefinition? ResolveByKey(string key)
    {
        var normalized = NormalizePath(key);
        return GetAllPages().FirstOrDefault(page =>
            page.Key.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public ParfaitInternalPageDefinition? ResolveByPath(string path)
    {
        var normalized = NormalizePath(path);
        if (!normalized.StartsWith("/internal", StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (var alias in RouteAliases.OrderByDescending(item => item.Key.Length))
        {
            if (MatchesPrefix(normalized, alias.Key))
                return ResolveByKey(alias.Value);
        }

        return GetAllPages()
            .OrderByDescending(page => page.Route.Length)
            .FirstOrDefault(page => MatchesPrefix(normalized, page.Route));
    }

    private IReadOnlyList<ParfaitInternalPageDefinition> BuildPages()
    {
        var pages = new List<ParfaitInternalPageDefinition>();

        foreach (var action in _actions.ActionDescriptors.Items.OfType<ControllerActionDescriptor>())
        {
            var template = action.AttributeRouteInfo?.Template;
            if (string.IsNullOrWhiteSpace(template))
                continue;

            if (!SupportsGet(action))
                continue;

            var route = NormalizePath("/" + template);
            if (!route.StartsWith("/internal", StringComparison.OrdinalIgnoreCase))
                continue;

            if (ExcludedExactRoutes.Any(excluded => route.Equals(excluded, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (route.Contains("/meta-", StringComparison.OrdinalIgnoreCase) ||
                route.EndsWith("/logout", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var pageAttr = action.MethodInfo.GetCustomAttributes(typeof(ParfaitInternalPageAttribute), inherit: true)
                .OfType<ParfaitInternalPageAttribute>()
                .FirstOrDefault();

            pages.Add(new ParfaitInternalPageDefinition
            {
                Key = route,
                Route = route,
                Title = pageAttr?.Title ?? BuildFallbackTitle(route),
                Group = pageAttr?.Group ?? BuildFallbackGroup(route),
                Description = pageAttr?.Description ?? BuildFallbackDescription(route),
                GroupOrder = pageAttr?.GroupOrder ?? BuildFallbackGroupOrder(route),
                Order = pageAttr?.Order ?? 999,
                ShowInNavigation = pageAttr?.ShowInNavigation ?? true,
                FounderOnly = pageAttr?.FounderOnly ?? false
            });
        }

        return pages
            .GroupBy(page => page.Route, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(page => page.Order).First())
            .OrderBy(page => page.GroupOrder)
            .ThenBy(page => page.Order)
            .ThenBy(page => page.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool SupportsGet(ControllerActionDescriptor action)
    {
        var methods = action.ActionConstraints?
            .OfType<HttpMethodActionConstraint>()
            .SelectMany(constraint => constraint.HttpMethods)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return methods is null || methods.Count == 0 || methods.Contains("GET", StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var normalized = path.Trim();
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;

        return normalized.TrimEnd('/').ToLowerInvariant() switch
        {
            "" => "/",
            var value => value
        };
    }

    private static bool MatchesPrefix(string path, string route)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedRoute = NormalizePath(route);

        return normalizedPath.Equals(normalizedRoute, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoute + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFallbackTitle(string route)
    {
        var segment = route.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Page";
        return string.Join(' ', segment.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static string BuildFallbackGroup(string route)
    {
        var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return "Other";

        return segments[1].ToLowerInvariant() switch
        {
            "dashboard" => "Core",
            "settings" => "Settings",
            "commerce" => "Operations",
            "customers" => "Operations",
            "marketing" => "Growth",
            "analytics" => "Growth",
            "content" => "Growth",
            _ => "Other"
        };
    }

    private static int BuildFallbackGroupOrder(string route)
    {
        return BuildFallbackGroup(route) switch
        {
            "Core" => 1,
            "Settings" => 2,
            "Operations" => 3,
            "Growth" => 4,
            _ => 9
        };
    }

    private static string BuildFallbackDescription(string route)
    {
        return $"Internal access to {BuildFallbackTitle(route)}.";
    }
}
