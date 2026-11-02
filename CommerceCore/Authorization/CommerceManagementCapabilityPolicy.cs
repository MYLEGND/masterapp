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
    // Route adapters share the existing action policy below; this projection grants
    // neither a session nor business access and does not register MVC endpoints.
    private static string? InternalAction(string route, string method)
    {
        var read = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        var write = HttpMethods.IsPost(method);
        if (!read && !write) return null;
        return route switch
        {
            "dashboard" when read => "dashboard",
            "settings/business-profile" => "business-profile",
            "settings/team" when read => "team",
            "commerce/products" => read ? "products" : "product",
            "commerce/products/settings" when write => "settings/commerce",
            "commerce/products/delete" when write => "product/delete",
            "commerce/products/images/upload" when write => "product/images/upload",
            "commerce/products/images/delete" when write => "product/images/delete",
            "commerce/products/images/reorder" when write => "product/images/reorder",
            "commerce/products/images/display" when write => "product/images/display",
            "commerce/products/reorder" when write => "products/reorder",
            "commerce/orders" when read => "orders",
            "commerce/orders/update" when write => "order",
            "commerce/orders/receipt" when write => "order/receipt",
            "automations" when read => "automations",
            "automations/workflows" when write => "automations/workflows",
            "automations/workflows/delete" when write => "automations/workflows/delete",
            "analytics" when read => "analytics",
            "analytics/meta-connect" or "analytics/meta-callback" or
            "analytics/meta-connection-status" or "analytics/meta-campaigns" or
            "analytics/health-monitor" or "analytics/marketing-setup" when read => route,
            "analytics/openai-connect" or "analytics/openai-refresh" or
            "analytics/openai-disconnect" or "analytics/meta-disconnect" when write => route,
            "settings/payments" when read => "payments",
            "settings/payments/callback" when read => "payments/callback",
            "settings/payments/connect" or "settings/payments/resume" or
            "settings/payments/confirm" or "settings/payments/default" or
            "settings/payments/disconnect" or "settings/payments/refresh" or
            "settings/payments/reconcile" or "settings/payments/refund" when write => route["settings/".Length..],
            _ => null
        };
    }

}
