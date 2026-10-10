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
        if (!raw.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var route = raw[Prefix.Length..].TrimEnd('/').ToLowerInvariant();
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
        {
            if (route is "workspace" or "dashboard" or "preview" ||
                route.StartsWith("preview/product/", StringComparison.Ordinal))
                return "website";
            if (route is "business-profile") return "settings";
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
        if (route is "business-profile") return "settings";
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
}
