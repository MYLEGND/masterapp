using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Razor;
using Shared.Analytics;

namespace ProtectWebsite.Services;

/// <summary>The editor reads the public route authority and discovers conventional MVC pages.
/// It does not maintain a second list of website pages.</summary>
public static class WebsiteEditorPageCatalog
{
    public sealed record Page(string Path, string Label);

    public static IReadOnlyList<Page> Discover(IActionDescriptorCollectionProvider actions, IRazorViewEngine views)
    {
        var pages = ProtectRouteCatalog.Routes.SelectMany(route => route.HasPaidLanding
            ? new[] { new Page(route.Path, route.DisplayName), new Page(route.Path + "/landing", route.DisplayName + " — Landing page") }
            : new[] { new Page(route.Path, route.DisplayName) }).ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var action in actions.ActionDescriptors.Items.OfType<ControllerActionDescriptor>())
        {
            if (!typeof(Controller).IsAssignableFrom(action.ControllerTypeInfo)) continue;
            var methods = action.ActionConstraints?.OfType<HttpMethodActionConstraint>().SelectMany(c => c.HttpMethods).ToArray();
            if (methods is { Length: > 0 } && !methods.Contains("GET", StringComparer.OrdinalIgnoreCase)) continue;
            if (!views.GetView(null, $"~/Views/{action.ControllerName}/{action.ActionName}.cshtml", false).Success) continue;
            var template = action.AttributeRouteInfo?.Template;
            if (template?.Contains('{') == true) continue;
            var path = template is not null ? "/" + template.TrimStart('~', '/')
                : "/" + action.ControllerName + (action.ActionName == "Index" ? "" : "/" + action.ActionName);
            path = ProtectRouteCatalog.CanonicalPath(path);
            if (action.ControllerName == "Home" && action.ActionName == "Index") path = "/";
            pages.TryAdd(path, new Page(path, action.ControllerName + (action.ActionName == "Index" ? "" : " · " + action.ActionName)));
        }
        return pages.Values.ToArray();
    }
}
