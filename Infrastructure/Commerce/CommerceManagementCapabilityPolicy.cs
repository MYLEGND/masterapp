using Microsoft.AspNetCore.Http;

namespace Legend.Commerce;

/// <summary>
/// Exactly one capability map for every action in the shared commerce-management
/// controller. Unknown routes and verbs are denied, never implicitly upgraded to
/// store-owner access. The actual grant is checked against the active
/// CommerceBusinessMember on *every* request by the shared controller.
/// </summary>
public static class CommerceManagementCapabilityPolicy
{
    private const string Prefix = "/commerce/manage/";

    public static string? ForRequest(PathString path, string method)
    {
        var raw = path.Value ?? "";
        if (raw == "/internal") raw = "/internal/";
        if (raw.StartsWith("/internal/", StringComparison.OrdinalIgnoreCase))
        {
            var alias = InternalAction(raw["/internal/".Length..].TrimEnd('/').ToLowerInvariant(), method);
            if (alias is null) return null;
            raw = Prefix + alias;
        }
        if (!raw.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var route = raw[Prefix.Length..].TrimEnd('/').ToLowerInvariant();
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
        {
            if (route is "workspace" or "dashboard" or "preview" ||
                route.StartsWith("preview/product/", StringComparison.Ordinal))
                return "website";
            if (route is "business-profile" or "payments" or "payments/callback") return "settings";
            if (route is "team") return "team";
            if (route is "products") return "catalog";
            if (route is "orders") return "orders";
            if (route is "automations") return "automations";
            if (route is "analytics/meta-connect" or "analytics/meta-callback")
                return "settings";
            if (route is "analytics" or "analytics/marketing-setup" or
                "analytics/meta-connection-status" or "analytics/meta-campaigns" or
                "analytics/health-monitor")
                return "analytics";
            return null;
        }

        if (!HttpMethods.IsPost(method)) return null;
        if (route is "product" or "product/delete" or
            "product/images/upload" or "product/images/delete" or
            "product/images/reorder" or "product/images/display" or
            "products/reorder" or "settings/commerce")
            return "catalog";
        if (route is "business-profile" or "payments/connect" or "payments/confirm" or
            "payments/default" or "payments/disconnect" or "payments/refresh" or "payments/resume" or "payments/refund" or "payments/reconcile") return "settings";
        if (route is "team/permissions") return "team";
        if (route is "order" or "order/receipt")
            return "orders";
        if (route is "automations/workflows" or "automations/workflows/delete")
            return "automations";
        if (route is "analytics/openai-connect" or "analytics/openai-refresh" or
            "analytics/openai-disconnect" or "analytics/meta-disconnect")
            return "settings";

        return null;
    }
    // The same adapter table drives authorization, MVC registration and generated links.
    public sealed record RouteAdapter(string Method, string InternalPath, string ActionPath);
    public static IReadOnlyList<RouteAdapter> InternalRoutes { get; } = BuildInternalRoutes();

    public static IReadOnlyList<RouteAdapter> AuthenticationRoutes { get; } = new[] {
        new RouteAdapter("GET", "/internal/login", "/commerce/manage/session/login"),
        new RouteAdapter("POST", "/internal/login", "/commerce/manage/session/login"),
        new RouteAdapter("GET", "/internal/login/complete", "/commerce/manage/session/complete"),
        new RouteAdapter("POST", "/internal/login/complete", "/commerce/manage/session/complete"),
        new RouteAdapter("GET", "/internal/denied", "/commerce/manage/session/denied"),
        new RouteAdapter("POST", "/internal/logout", "/commerce/manage/session/logout")
    };

    private static IReadOnlyList<RouteAdapter> BuildInternalRoutes()
    {
        var routes = new List<RouteAdapter>();
        void Add(string method, string path, string action) => routes.Add(new(method, "/internal/" + path, Prefix + action));
        foreach (var pair in new[] {
            ("", "workspace"), ("dashboard", "dashboard"), ("preview", "preview"), ("preview/product/{slug}", "preview/product/{slug}"), ("settings/business-profile", "business-profile"),
            ("settings/team", "team"), ("commerce/products", "products"),
            ("commerce/orders", "orders"), ("automations", "automations"), ("analytics", "analytics"),
            ("settings/payments", "payments"), ("settings/payments/callback", "payments/callback") })
            Add("GET", pair.Item1, pair.Item2);
        foreach (var pair in new[] {
            ("settings/business-profile", "business-profile"), ("settings/team/permissions", "team/permissions"),
            ("commerce/products", "product"), ("commerce/products/settings", "settings/commerce"),
            ("commerce/products/delete", "product/delete"), ("commerce/products/reorder", "products/reorder"),
            ("commerce/orders/update", "order"), ("commerce/orders/receipt", "order/receipt"),
            ("automations/workflows", "automations/workflows"), ("automations/workflows/delete", "automations/workflows/delete") })
            Add("POST", pair.Item1, pair.Item2);
        foreach (var suffix in new[] { "upload", "delete", "reorder", "display" })
            Add("POST", "commerce/products/images/" + suffix, "product/images/" + suffix);
        foreach (var suffix in new[] { "meta-connect", "meta-callback", "meta-connection-status", "meta-campaigns", "health-monitor", "marketing-setup" })
            Add("GET", "analytics/" + suffix, "analytics/" + suffix);
        foreach (var suffix in new[] { "openai-connect", "openai-refresh", "openai-disconnect", "meta-disconnect" })
            Add("POST", "analytics/" + suffix, "analytics/" + suffix);
        foreach (var suffix in new[] { "connect", "resume", "confirm", "default", "disconnect", "refresh", "reconcile", "refund" })
            Add("POST", "settings/payments/" + suffix, "payments/" + suffix);
        return routes.AsReadOnly();
    }

    public static string ManagementUrl(HttpContext context, string actionPath, string? ticket, string method = "GET")
    {
        var target = actionPath;
        if (context.Request.Path.StartsWithSegments("/internal"))
            target = InternalRoutes.SingleOrDefault(x => x.Method == method && x.ActionPath == actionPath)?.InternalPath ?? throw new InvalidOperationException("No canonical internal route exists for this action and method.");
        var query = new Dictionary<string, string?>();
        if (!context.Items.ContainsKey("LegendStoreSession") && !string.IsNullOrEmpty(ticket)) query["ticket"] = ticket;
        var businessKey = context.Request.Query["businessKey"].ToString();
        if (!string.IsNullOrEmpty(businessKey)) query["businessKey"] = businessKey;
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(target, query);
    }

    private static string? InternalAction(string route, string method)
    {
        var verb = HttpMethods.IsHead(method) ? "GET" : method;
        if (verb == "GET" && route.StartsWith("preview/product/", StringComparison.Ordinal) && route.Length > "preview/product/".Length) return route;
        return InternalRoutes.SingleOrDefault(x => x.Method == verb &&
            string.Equals(x.InternalPath, "/internal/" + route, StringComparison.OrdinalIgnoreCase))?.ActionPath[Prefix.Length..];
    }
}
