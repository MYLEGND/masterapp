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
    private static readonly string[] AnalyticsReports = ["commerce", "event-map", "summary", "traffic", "page-performance", "cta-performance",
        "quote-funnel", "marketing-health", "conversions", "leads", "behavior/summary", "behavior/time-on-page",
        "behavior/exit-analysis", "behavior/journey", "behavior/source-performance", "quote-funnel/abandonment",
        "deviceintelligence", "meta-signal", "meta-signal-health", "kpi-detail", "visitor-timeline", "advertising",
        "marketing-manager/context", "ai-review-snapshot", "marketing-manager/performance", "growth-economics",
        "openai-onboarding", "openai-product-feed"];
    private static readonly string[] AnalyticsSettingsReads = ["external-ads/connect", "external-ads/accounts", "external-ads/callback", "calendar-connect", "calendar-callback"];
    private static readonly string[] AnalyticsSettingsWrites = ["marketing-setup", "external-ads/select-account",
        "external-ads/measurement", "external-ads/disconnect", "calendar-disconnect"];
    private static readonly string[] AnalyticsCommands = ["advertising/promote/draft", "advertising/promote/propose",
        "advertising/campaign/status/propose", "advertising/campaign/budget/propose", "advertising/approve",
        "advertising/execute", "advertising/reject", "marketing-manager/plan", "openai-product-feed/publish"];
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
            if (route is "business-profile" or "payments" or "payments/callback" or "shipping") return "settings";
            if (route is "team") return "team";
            if (route is "products") return "catalog";
            if (route is "orders" or "order/packing-slip" or "order/shipping" or "order/reference-label") return "orders";
            if (route is "automations") return "automations";
            if (route.StartsWith("analytics/", StringComparison.Ordinal))
            {
                var section = route["analytics/".Length..];
                if (AnalyticsReports.Contains(section) || IsCampaignInsights(section)) return "analytics";
                if (AnalyticsSettingsReads.Contains(section)) return "settings";
            }
            if (route is "analytics/meta-connect" or "analytics/meta-callback")
                return "settings";
            if (route is "analytics" or "analytics/marketing-setup" or
                "analytics/meta-connection-status" or "analytics/meta-campaigns" or
                "analytics/health-monitor")
                return "analytics";
            return null;
        }

        if (!HttpMethods.IsPost(method)) return null;
        if (route.StartsWith("analytics/", StringComparison.Ordinal))
        {
            var section = route["analytics/".Length..];
            if (AnalyticsSettingsWrites.Contains(section)) return "settings";
            if (AnalyticsCommands.Contains(section)) return "analytics";
        }
        if (route is "product" or "product/delete" or
            "product/images/upload" or "product/images/delete" or
            "product/images/reorder" or "product/images/display" or
            "products/reorder" or "settings/commerce")
            return "catalog";
        if (route is "shipping/connect" or "business-profile" or "payments/connect" or "payments/confirm" or
            "payments/default" or "payments/disconnect" or "payments/refresh" or "payments/resume" or "payments/refund" or "payments/reconcile") return "settings";
        if (route is "team/permissions" or "team/add" or "team/remove") return "team";
        if (route is "order" or "order/receipt" or "order/shipping/rates" or "order/shipping/buy" or "order/shipping/reconcile")
            return "orders";
        if (route is "automations/workflows" or "automations/workflows/delete" or "automations/email-settings")
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
            ("commerce/orders", "orders"), ("commerce/orders/packing-slip", "order/packing-slip"), ("commerce/orders/reference-label", "order/reference-label"), ("automations", "automations"), ("analytics", "analytics"),
            ("settings/shipping", "shipping"), ("commerce/orders/shipping", "order/shipping"), ("settings/payments", "payments"), ("settings/payments/callback", "payments/callback") })
            Add("GET", pair.Item1, pair.Item2);
        foreach (var pair in new[] {
            ("settings/shipping/connect", "shipping/connect"), ("commerce/orders/shipping/rates", "order/shipping/rates"), ("commerce/orders/shipping/buy", "order/shipping/buy"), ("commerce/orders/shipping/reconcile", "order/shipping/reconcile"),
            ("settings/business-profile", "business-profile"), ("settings/team/permissions", "team/permissions"),
            ("settings/team/add", "team/add"), ("settings/team/remove", "team/remove"),
            ("commerce/products", "product"), ("commerce/products/settings", "settings/commerce"),
            ("commerce/products/delete", "product/delete"), ("commerce/products/reorder", "products/reorder"),
            ("commerce/orders/update", "order"), ("commerce/orders/receipt", "order/receipt"),
            ("automations/email-settings", "automations/email-settings"), ("automations/workflows", "automations/workflows"), ("automations/workflows/delete", "automations/workflows/delete") })
            Add("POST", pair.Item1, pair.Item2);
        foreach (var suffix in new[] { "upload", "delete", "reorder", "display" })
            Add("POST", "commerce/products/images/" + suffix, "product/images/" + suffix);
        foreach (var suffix in new[] { "meta-connect", "meta-callback", "meta-connection-status", "meta-campaigns", "health-monitor", "marketing-setup" })
            Add("GET", "analytics/" + suffix, "analytics/" + suffix);
        foreach (var suffix in new[] { "openai-connect", "openai-refresh", "openai-disconnect", "meta-disconnect" })
            Add("POST", "analytics/" + suffix, "analytics/" + suffix);
        foreach (var suffix in new[] { "connect", "resume", "confirm", "default", "disconnect", "refresh", "reconcile", "refund" })
            Add("POST", "settings/payments/" + suffix, "payments/" + suffix);
        foreach (var suffix in AnalyticsReports.Concat(AnalyticsSettingsReads))
            Add("GET", "analytics/" + suffix, "analytics/" + suffix);
        Add("GET", "analytics/advertising/campaign/{campaignId}/insights", "analytics/advertising/campaign/{campaignId}/insights");
        foreach (var suffix in AnalyticsSettingsWrites.Concat(AnalyticsCommands))
            Add("POST", "analytics/" + suffix, "analytics/" + suffix);
        // MVC adapter only; actual section authorization uses the explicit entries above.
        Add("GET", "analytics/{**section}", "analytics/{**section}");
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
        if (verb == "GET" && route.StartsWith("analytics/", StringComparison.Ordinal) &&
            IsCampaignInsights(route["analytics/".Length..])) return route;
        return InternalRoutes.SingleOrDefault(x => x.Method == verb &&
            string.Equals(x.InternalPath, "/internal/" + route, StringComparison.OrdinalIgnoreCase))?.ActionPath[Prefix.Length..];
    }

    private static bool IsCampaignInsights(string section)
    {
        var segments = section.Split('/');
        return segments.Length == 4 && segments[0] == "advertising" && segments[1] == "campaign" &&
            segments[3] == "insights" && segments[2].Length > 0 && segments[2] is not "." and not ".." &&
            !segments[2].Contains('%') && !segments[2].Contains('\\');
    }
}
