using System.Globalization;
using Infrastructure.Analytics;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using ParfaitApp.Models;
using ParfaitApp.Services;
using Shared.Analytics;

namespace ParfaitApp.Controllers;

/// <summary>
/// Website-editor ticket adapter into the canonical Parfait commerce experience.
/// It owns no parallel catalog/order/automation model or UI: every action resolves
/// the authorized CommerceBusinessId and renders the same InternalModules views
/// through the same Parfait services used by the Parfait internal console.
/// </summary>
[Route("commerce/manage")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[IgnoreAntiforgeryToken]
public sealed class CommerceManagementController(
    WebsiteEditorTicketProtector tickets,
    IConfiguration configuration,
    CommerceStoreContextService stores,
    ParfaitProductService products,
    ParfaitOrderService orders,
    ParfaitCustomerAutomationService automations,
    ParfaitInternalAnalyticsService internalAnalytics,
    ParfaitInternalWorkspaceService workspace,
    IParfaitBusinessProfileService businessProfile,
    MarketingConnectionStore marketingConnections,
    MarketingMetaAdsOAuthService metaAdsOAuth,
    IMetaAdsService metaAds,
    IGraphMailService mail) : Controller
{
    [HttpGet("workspace")]
    public async Task<IActionResult> Workspace(
        [FromQuery] string ticket,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        return RedirectToAction(nameof(Dashboard), new { ticket });
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(
        [FromQuery] string ticket,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();

        ApplyManagementViewData(store, ticket, "dashboard");
        return View("~/Views/Dashboard/Index.cshtml", await workspace.GetSnapshotAsync(
            store.CommerceBusinessId,
            ResolveAnalyticsScope(store),
            ResolveMarketingOwner(store),
            ct));
    }

    [HttpGet("analytics")]
    public async Task<IActionResult> Analytics(
        [FromQuery] string ticket,
        [FromQuery] string? preset = "30d",
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] string? qualityMode = null,
        [FromQuery] string? timezoneId = null,
        [FromQuery] int? timezoneOffsetMinutes = null,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();

        var resolvedQualityMode = ResolveAnalyticsQualityMode(qualityMode);
        var timezoneContext = ResolveViewerTimeZoneContext(timezoneId, timezoneOffsetMinutes);
        ApplyManagementViewData(store, ticket, "analytics");
        var analyticsScope = ResolveAnalyticsScope(store);
        var marketingOwner = ResolveMarketingOwner(store);
        return View("~/Views/InternalModules/Analytics.cshtml", await internalAnalytics.GetDashboardAsync(
            store.CommerceBusinessId,
            analyticsScope,
            marketingOwner,
            preset,
            fromUtc,
            toUtc,
            resolvedQualityMode,
            timezoneContext.ViewerTimeZone,
            timezoneContext.TimezoneId,
            timezoneContext.TimezoneOffsetMinutes,
            ct));
    }

    [HttpGet("analytics/meta-connect")]
    public async Task<IActionResult> MetaConnect(
        [FromQuery] string ticket,
        [FromQuery] string? returnUrl = null,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();

        var target = LocalManagerReturnUrl(returnUrl, ticket);
        var redirectUri = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/commerce/manage/analytics/meta-callback";
        try
        {
            return Redirect(metaAdsOAuth.BuildConnectUrl(
                ResolveMarketingOwner(store),
                target,
                redirectUri));
        }
        catch (InvalidOperationException ex)
        {
            return Redirect(AppendMetaStatus(target, "error", ex.Message));
        }
    }

    [AllowAnonymous]
    [HttpGet("analytics/meta-callback")]
    public async Task<IActionResult> MetaCallback(
        [FromQuery] string? code = null,
        [FromQuery] string? state = null,
        [FromQuery] string? error = null,
        [FromQuery(Name = "error_description")] string? errorDescription = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            var fallback = "/commerce/manage/analytics";
            var message = string.IsNullOrWhiteSpace(errorDescription) ? error : errorDescription;
            return Redirect(AppendMetaStatus(fallback, "error", message));
        }

        try
        {
            var result = await metaAdsOAuth.CompleteCallbackAsync(code ?? string.Empty, state ?? string.Empty, ct);
            var ticket = TicketFromReturnUrl(result.ReturnUrl);
            if (string.IsNullOrWhiteSpace(ticket)) return Unauthorized();

            var store = await ResolveAsync(ticket, ct);
            if (store is null ||
                !string.Equals(result.Owner.Key, ResolveMarketingOwner(store).Key, StringComparison.Ordinal))
                return Unauthorized();

            await marketingConnections.SaveAdsAsync(result.Owner, result.Connection, ct);
            internalAnalytics.InvalidateCache();
            return Redirect(AppendMetaStatus(result.ReturnUrl, "connected"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("analytics/meta-connection-status")]
    public async Task<IActionResult> MetaConnectionStatus([FromQuery] string ticket, CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        var owner = ResolveMarketingOwner(store);
        var row = await marketingConnections.GetStatusAsync(owner, ct);
        return Json(MarketingStatusPayload(row));
    }

    [HttpGet("analytics/meta-campaigns")]
    public async Task<IActionResult> MetaCampaigns(
        [FromQuery] string ticket,
        [FromQuery] string? preset = "30d",
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] string? qualityMode = null,
        [FromQuery] string? timezoneId = null,
        [FromQuery] int? timezoneOffsetMinutes = null,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();

        try
        {
            var timezoneContext = ResolveViewerTimeZoneContext(timezoneId, timezoneOffsetMinutes);
            var range = TimeRangeRequest.FromPreset(
                string.IsNullOrWhiteSpace(preset) ? "30d" : preset,
                fromUtc,
                toUtc,
                viewerTz: timezoneContext.ViewerTimeZone,
                qualityMode: ResolveAnalyticsQualityMode(qualityMode));
            return Json(await metaAds.GetCampaignsAsync(range, ResolveAnalyticsScope(store), ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("analytics/health-monitor")]
    public async Task<IActionResult> AnalyticsHealthMonitor(
        [FromQuery] string ticket,
        [FromQuery] string? preset = "30d",
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] string? qualityMode = null,
        [FromQuery] string? timezoneId = null,
        [FromQuery] int? timezoneOffsetMinutes = null,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();

        var timezoneContext = ResolveViewerTimeZoneContext(timezoneId, timezoneOffsetMinutes);
        var dashboard = await internalAnalytics.GetDashboardAsync(
            store.CommerceBusinessId,
            ResolveAnalyticsScope(store),
            ResolveMarketingOwner(store),
            preset,
            fromUtc,
            toUtc,
            ResolveAnalyticsQualityMode(qualityMode),
            timezoneContext.ViewerTimeZone,
            timezoneContext.TimezoneId,
            timezoneContext.TimezoneOffsetMinutes,
            ct);
        return Json(BuildHealthPayload(dashboard, store.StoreName));
    }

    [HttpPost("analytics/meta-disconnect")]
    public async Task<IActionResult> MetaDisconnect([FromQuery] string ticket, CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        await marketingConnections.DisconnectAsync(ResolveMarketingOwner(store), ct);
        internalAnalytics.InvalidateCache();
        return Json(new { ok = true });
    }

    [HttpGet("products")]
    public async Task<IActionResult> Products(
        [FromQuery] string ticket,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();

        ApplyManagementViewData(store, ticket, "products");
        var scopedProducts = products.GetAllProducts(store.CommerceBusinessId).ToList();
        return View("~/Views/InternalModules/Products.cshtml", new ParfaitProductAdminViewModel
        {
            Products = scopedProducts,
            CommerceSettings = products.GetCommerceSettings(store.CommerceBusinessId),
            ActiveProductCount = scopedProducts.Count(product => product.IsActive),
            FeaturedProductCount = scopedProducts.Count(product => product.IsFeatured),
            TotalImageCount = scopedProducts.Sum(product => product.Images.Count)
        });
    }

    [HttpGet("orders")]
    public async Task<IActionResult> Orders(
        [FromQuery] string ticket,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();

        ApplyManagementViewData(store, ticket, "orders");
        var scopedOrders = orders.GetAllOrders(store.CommerceBusinessId).ToList();
        var paidOrders = scopedOrders
            .Where(order => string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(order.PaymentStatus, "Refunded", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return View("~/Views/InternalModules/Orders.cshtml", new ParfaitOrderAdminViewModel
        {
            Orders = scopedOrders,
            PaidOrderCount = paidOrders.Count(order => string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase)),
            PendingOrderCount = scopedOrders.Count(order => string.Equals(order.PaymentStatus, "Pending", StringComparison.OrdinalIgnoreCase)),
            FailedOrderCount = scopedOrders.Count(order => string.Equals(order.PaymentStatus, "Failed", StringComparison.OrdinalIgnoreCase)),
            RefundedOrderCount = orders.CountRefunded(scopedOrders),
            OpenFulfillmentCount = orders.CountOpenFulfillment(scopedOrders),
            ReturnQueueCount = orders.CountReturnQueue(scopedOrders),
            RevenueCents = orders.SumNetRevenueCents(scopedOrders),
            AverageOrderValueCents = orders.CalculateAverageNetOrderValueCents(scopedOrders)
        });
    }

    [HttpGet("automations")]
    public async Task<IActionResult> Automations(
        [FromQuery] string ticket,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();

        ApplyManagementViewData(store, ticket, "automations");
        return View(
            "~/Views/InternalModules/Automations.cshtml",
            automations.GetWorkspaceViewModel(store.CommerceBusinessId));
    }

    [HttpGet("preview")]
    public async Task<IActionResult> Preview(
        [FromQuery] string ticket,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();

        ApplyFramePolicy();
        ApplyStoreViewData(store);
        ViewData["StoreProductBasePath"] = "/commerce/manage/preview/product";
        ViewData["StorePreviewQuery"] = "?ticket=" + Uri.EscapeDataString(ticket);
        return View("~/Views/Store/Index.cshtml", new ParfaitStorefrontViewModel
        {
            StoreName = store.StoreName,
            Headline = store.Headline,
            Subheadline = store.Subheadline,
            Products = products.GetActiveStoreProducts(store.CommerceBusinessId)
        });
    }

    [HttpGet("preview/product/{slug}")]
    public async Task<IActionResult> PreviewProduct(
        string slug,
        [FromQuery] string ticket,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();

        var product = products.GetActiveStoreProductBySlug(store.CommerceBusinessId, slug);
        if (product is null) return NotFound();

        ApplyFramePolicy();
        ApplyStoreViewData(store);
        ViewData["StoreRootPath"] = "/commerce/manage/preview?ticket=" + Uri.EscapeDataString(ticket);
        ViewData["StorePreviewMode"] = true;
        return View("~/Views/Store/Product.cshtml", product);
    }

    [HttpPost("product")]
    public async Task<IActionResult> SaveProduct(
        string ticket,
        [FromForm] ParfaitProductEditorViewModel product,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();

        product.IsActive = HasCheckedValue(Request.Form, "IsActive");
        product.IsFeatured = HasCheckedValue(Request.Form, "IsFeatured");

        for (var index = 0; index < product.InventoryBySize.Count; index++)
            product.InventoryBySize[index].IsEnabled = HasCheckedValue(Request.Form, $"InventoryBySize[{index}].IsEnabled");
        for (var index = 0; index < product.DiscountCodes.Count; index++)
            product.DiscountCodes[index].IsActive = HasCheckedValue(Request.Form, $"DiscountCodes[{index}].IsActive");

        var existing = products.GetAllProducts(store.CommerceBusinessId)
            .FirstOrDefault(x => string.Equals(x.Id, product.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (product.InventoryBySize is null || product.InventoryBySize.Count == 0)
                product.InventoryBySize = existing.InventoryBySize;
            if (product.DiscountCodes is null || product.DiscountCodes.Count == 0)
                product.DiscountCodes = existing.DiscountCodes;
        }

        product.InventoryBySize ??= ParfaitProductCatalogDefaults.CreateDefaultInventory();
        product.DiscountCodes ??= [];
        products.SaveProduct(store.CommerceBusinessId, product);
        TempData["ProductStatus"] = product.IsActive ? "Product saved and visible." : "Product saved and hidden.";
        return RedirectToAction(nameof(Products), new { ticket });
    }

    [HttpPost("product/delete")]
    public async Task<IActionResult> DeleteProduct(
        string ticket,
        [FromForm] string id,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        products.DeleteProduct(store.CommerceBusinessId, id);
        TempData["ProductStatus"] = "Product deleted.";
        return RedirectToAction(nameof(Products), new { ticket });
    }

    [HttpPost("product/images/upload")]
    [RequestSizeLimit(26_000_000)]
    public async Task<IActionResult> UploadImages(
        string ticket,
        [FromForm] string productId,
        [FromForm] List<IFormFile> images,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        await products.UploadImagesAsync(store.CommerceBusinessId, productId, images);
        TempData["ProductStatus"] = "Images uploaded.";
        return RedirectToAction(nameof(Products), new { ticket });
    }

    [HttpPost("product/images/delete")]
    public async Task<IActionResult> DeleteImage(
        string ticket,
        [FromForm] string productId,
        [FromForm] string imageId,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        products.DeleteImage(store.CommerceBusinessId, productId, imageId);
        TempData["ProductStatus"] = "Image deleted.";
        return RedirectToAction(nameof(Products), new { ticket });
    }

    [HttpPost("product/images/reorder")]
    public async Task<IActionResult> ReorderImages(
        string ticket,
        [FromForm] string productId,
        [FromForm] List<string> imageIds,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        products.ReorderImages(store.CommerceBusinessId, productId, imageIds ?? []);
        TempData["ProductStatus"] = "Image order updated.";
        return RedirectToAction(nameof(Products), new { ticket });
    }

    [HttpPost("product/images/display")]
    public async Task<IActionResult> SaveImageDisplay(
        string ticket,
        [FromForm] string productId,
        [FromForm] string imageId,
        [FromForm] string objectFit,
        [FromForm] int objectPositionX,
        [FromForm] int objectPositionY,
        [FromForm] decimal zoom,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        products.SaveImageDisplaySettings(store.CommerceBusinessId, productId, imageId, objectFit, objectPositionX, objectPositionY, zoom);
        TempData["ProductStatus"] = "Image display settings saved.";
        return RedirectToAction(nameof(Products), new { ticket });
    }

    [HttpPost("products/reorder")]
    public async Task<IActionResult> ReorderProducts(
        string ticket,
        [FromForm] List<string> productIds,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        products.ReorderProducts(store.CommerceBusinessId, productIds ?? []);
        TempData["ProductStatus"] = "Store order updated.";
        return RedirectToAction(nameof(Products), new { ticket });
    }

    [HttpPost("settings/commerce")]
    public async Task<IActionResult> SaveCommerceSettings(
        string ticket,
        [FromForm] ParfaitCommerceSettingsViewModel settings,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        settings.GlobalDiscount ??= new ParfaitProductDiscountCodeEditorViewModel();
        settings.GlobalDiscount.IsActive = HasCheckedValue(Request.Form, "GlobalDiscount.IsActive");
        products.SaveCommerceSettings(store.CommerceBusinessId, settings);
        TempData["ProductStatus"] = "Commerce settings saved.";
        return RedirectToAction(nameof(Products), new { ticket });
    }

    [HttpPost("order")]
    public async Task<IActionResult> UpdateOrder(
        string ticket,
        [FromForm] ParfaitOrderAdminUpdateRequest request,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        if (!orders.UpdateOrder(store.CommerceBusinessId, request))
            return NotFound();
        TempData["OrderStatus"] = $"Order {request.OrderNumber} updated.";
        return RedirectToAction(nameof(Orders), new { ticket });
    }

    [HttpPost("order/receipt")]
    public async Task<IActionResult> ResendOrderReceipt(
        string ticket,
        [FromForm] string orderNumber,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        var order = orders.GetOrder(store.CommerceBusinessId, orderNumber);
        if (order is null) return NotFound();

        try
        {
            await mail.SendOrderReceiptAsync(order, ct);
            TempData["OrderStatus"] = $"Receipt resent for {order.OrderNumber}.";
        }
        catch
        {
            TempData["OrderStatus"] = $"Receipt resend failed for {order.OrderNumber}.";
        }

        return RedirectToAction(nameof(Orders), new { ticket });
    }

    [HttpPost("automations/workflows")]
    public async Task<IActionResult> SaveAutomationWorkflow(
        string ticket,
        [FromForm] ParfaitAutomationWorkflowEditorInput input,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        automations.SaveWorkflow(store.CommerceBusinessId, input);
        TempData["AutomationStatus"] = "Automation saved.";
        TempData["AutomationStatusTone"] = "success";
        return RedirectToAction(nameof(Automations), new { ticket });
    }

    [HttpPost("automations/workflows/delete")]
    public async Task<IActionResult> DeleteAutomationWorkflow(
        string ticket,
        [FromForm] Guid id,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        automations.DeleteWorkflow(store.CommerceBusinessId, id);
        TempData["AutomationStatus"] = "Automation deleted.";
        TempData["AutomationStatusTone"] = "success";
        return RedirectToAction(nameof(Automations), new { ticket });
    }

    private static ScopeContext ResolveAnalyticsScope(CommerceStoreContext store)
    {
        if (string.Equals(store.WebsiteSiteKey, WebsiteEditorSiteKeys.Protect, StringComparison.OrdinalIgnoreCase))
        {
            if (!store.AgentTrackingProfileId.HasValue)
                throw new InvalidOperationException("Protect commerce analytics requires the canonical agent owner.");
            return ScopeContext.ForAgent(store.AgentTrackingProfileId.Value);
        }

        if (string.Equals(store.WebsiteSiteKey, WebsiteEditorSiteKeys.Legend, StringComparison.OrdinalIgnoreCase))
            return ScopeContext.ForSite(store.WebsiteSiteKey, store.BusinessKey);

        return ScopeContext.ForBusiness(store.CommerceBusinessId);
    }

    private static MarketingOwnerScope ResolveMarketingOwner(CommerceStoreContext store)
    {
        if (string.Equals(store.WebsiteSiteKey, WebsiteEditorSiteKeys.Protect, StringComparison.OrdinalIgnoreCase))
        {
            if (!store.AgentTrackingProfileId.HasValue)
                throw new InvalidOperationException("Protect commerce marketing requires the canonical agent owner.");
            return MarketingOwnerScope.Agent(store.AgentTrackingProfileId.Value);
        }

        if (string.Equals(store.WebsiteSiteKey, WebsiteEditorSiteKeys.Legend, StringComparison.OrdinalIgnoreCase))
            return MarketingOwnerScope.Founder;

        return MarketingOwnerScope.Business(store.CommerceBusinessId);
    }

    private static object MarketingStatusPayload(MarketingConnection? row)
    {
        var connected = row is not null &&
            row.DisconnectedUtc == null &&
            row.AdsAccessTokenCiphertext != null &&
            (!row.AccessTokenExpiresUtc.HasValue || row.AccessTokenExpiresUtc > DateTime.UtcNow);
        return new
        {
            connected,
            accountId = row?.AdAccountId,
            accountName = row?.AdAccountName,
            businessId = row?.MetaBusinessManagerId,
            businessName = row?.MetaBusinessManagerName,
            metaUserName = row?.MetaUserName,
            connectedUtc = row?.ConnectedUtc,
            accessTokenExpiresUtc = row?.AccessTokenExpiresUtc,
            message = connected ? null : "Meta Ads not connected or the connection has expired."
        };
    }

    private static TrafficQualityMode ResolveAnalyticsQualityMode(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? TrafficQualityMode.RealHumanTraffic
            : TrafficQualityBucketFilters.ParseClientOrEnumValue(value);

    private (string? TimezoneId, int? TimezoneOffsetMinutes, TimeZoneInfo ViewerTimeZone) ResolveViewerTimeZoneContext(
        string? timezoneId,
        int? timezoneOffsetMinutes)
    {
        var id = !string.IsNullOrWhiteSpace(timezoneId)
            ? timezoneId.Trim()
            : Request.Cookies["ParfaitAnalyticsViewerTimeZone"]?.Trim();
        var offset = timezoneOffsetMinutes;
        if (!offset.HasValue &&
            int.TryParse(Request.Cookies["ParfaitAnalyticsViewerOffsetMinutes"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            offset = parsed;

        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return (id, offset, TimeZoneInfo.FindSystemTimeZoneById(id)); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        if (offset is >= -840 and <= 840)
        {
            try
            {
                return (id, offset, TimeZoneInfo.CreateCustomTimeZone(
                    $"viewer-offset-{offset.Value}",
                    TimeSpan.FromMinutes(-offset.Value),
                    "Viewer Local",
                    "Viewer Local"));
            }
            catch { }
        }
        return (id, offset, TimeZoneInfo.Utc);
    }

    private static object BuildHealthPayload(ParfaitInternalAnalyticsViewModel dashboard, string storeName)
    {
        var actions = dashboard.ActionBreakdowns.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        int Sessions(string key) => actions.TryGetValue(key, out var action) ? action.UniqueSessions : 0;
        int Count(string key) => actions.TryGetValue(key, out var action) ? action.Count : 0;
        decimal Rate(int value, int total) => total <= 0 ? 0m : Math.Round(value * 100m / total, 1);

        var viewSessions = Sessions("view-content");
        var productSessions = Sessions("product-viewed");
        var cartSessions = Sessions("add-to-cart");
        var checkoutSessions = Sessions("checkout-started");
        var purchaseSessions = Sessions("purchase");
        var browser = dashboard.MetaHealth.PipelineHealth.MetaBrowserSentCount;
        var server = dashboard.MetaHealth.PipelineHealth.MetaServerSentCount;
        var eligible = browser + server;
        var matched = Math.Min(browser, server);
        var missingFailures = dashboard.MetaHealth.FailureDetection.Sum(x => x.Count);

        return new
        {
            dashboard.RangeLabel,
            summary = $"{storeName} ecommerce health snapshot loaded.",
            focusMetrics = new[]
            {
                new { key = "product-viewed", label = "Product Viewed", currentValue = Count("product-viewed"), deltaPercent = Rate(productSessions, viewSessions) },
                new { key = "add-to-cart", label = "Add To Cart", currentValue = Count("add-to-cart"), deltaPercent = Rate(cartSessions, productSessions) },
                new { key = "checkout-started", label = "Checkout Started", currentValue = Count("checkout-started"), deltaPercent = Rate(checkoutSessions, cartSessions) },
                new { key = "purchase", label = "Purchase", currentValue = Count("purchase"), deltaPercent = Rate(purchaseSessions, checkoutSessions) }
            },
            attributionHealth = new
            {
                eligibleEvents = eligible,
                browserSentEvents = browser,
                serverSentEvents = server,
                matchedEvents = matched,
                serverBrowserMatchRate = Rate(matched, eligible),
                missingAttributionEvents = missingFailures,
                missingAttributionRate = Rate(missingFailures, eligible)
            },
            reconciliation = new
            {
                paidOrders = dashboard.PaidOrders,
                purchaseEvents = Count("purchase"),
                unmatchedPaidOrders = Math.Max(0, dashboard.PaidOrders - Count("purchase")),
                revenueCents = dashboard.RevenueCents
            },
            funnel = new[]
            {
                new { label = "View Content", sessions = viewSessions, conversionRate = 100m },
                new { label = "Product Viewed", sessions = productSessions, conversionRate = Rate(productSessions, viewSessions) },
                new { label = "Add To Cart", sessions = cartSessions, conversionRate = Rate(cartSessions, productSessions) },
                new { label = "Checkout Started", sessions = checkoutSessions, conversionRate = Rate(checkoutSessions, cartSessions) },
                new { label = "Purchase", sessions = purchaseSessions, conversionRate = Rate(purchaseSessions, checkoutSessions) }
            },
            recentEvents = dashboard.MetaHealth.RecentEvents.Take(20).Select(row => new
            {
                row.CreatedUtc,
                severity = string.Equals(row.MetaServerStatus, "Failed", StringComparison.OrdinalIgnoreCase) ? "Warning" : "Info",
                row.EventName,
                summary = $"{row.SourceLabel} · {row.DispatcherStatus} / {row.MetaServerStatus}"
            })
        };
    }

    private string LocalManagerReturnUrl(string? returnUrl, string ticket)
    {
        var fallback = Url.Action(nameof(Analytics), new { ticket }) ?? $"/commerce/manage/analytics?ticket={Uri.EscapeDataString(ticket)}";
        if (string.IsNullOrWhiteSpace(returnUrl) || !Url.IsLocalUrl(returnUrl)) return fallback;
        var parsedTicket = TicketFromReturnUrl(returnUrl);
        return string.Equals(parsedTicket, ticket, StringComparison.Ordinal) ? returnUrl : fallback;
    }

    private static string? TicketFromReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return null;
        if (!Uri.TryCreate("https://local" + (returnUrl.StartsWith('/') ? returnUrl : "/" + returnUrl), UriKind.Absolute, out var uri))
            return null;
        return QueryHelpers.ParseQuery(uri.Query).TryGetValue("ticket", out var ticket)
            ? ticket.ToString()
            : null;
    }

    private static string AppendMetaStatus(string target, string meta, string? message = null)
    {
        var separator = target.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var url = $"{target}{separator}meta={Uri.EscapeDataString(meta)}";
        if (!string.IsNullOrWhiteSpace(message))
            url += $"&message={Uri.EscapeDataString(message)}";
        return url;
    }

    private async Task<CommerceStoreContext?> ResolveAsync(string ticket, CancellationToken ct) =>
        await stores.ResolveForWebsiteTicketAsync(ticket, tickets, configuration, ct);

    private void ApplyManagementViewData(CommerceStoreContext store, string ticket, string activePage)
    {
        ApplyFramePolicy();
        ApplyStoreViewData(store);
        ViewData["CommerceManagerTicket"] = ticket;
        ViewData["CommerceManagerActivePage"] = activePage;
        ViewData["CommerceManagerScoped"] = true;
    }

    private void ApplyStoreViewData(CommerceStoreContext store)
    {
        ViewData["CommerceStoreContext"] = store;
        ViewData["StoreRootPath"] = store.StoreRootPath;
        ViewData["StoreCartPath"] = store.CartPath;
        ViewData["StoreCheckoutPath"] = store.CheckoutPath;
        ViewData["StoreSuccessPath"] = store.SuccessPath;
    }

    private void ApplyFramePolicy()
    {
        Response.Headers["Content-Security-Policy"] =
            "frame-ancestors 'self' https://mylegnd.com https://www.mylegnd.com https://protect.mylegnd.com https://portal.mylegnd.com https://client.mylegnd.com https://masterapp-protect.azurewebsites.net";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        Response.Headers.Remove("X-Frame-Options");
    }

    private static bool HasCheckedValue(IFormCollection form, string key) =>
        form[key].Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
}
