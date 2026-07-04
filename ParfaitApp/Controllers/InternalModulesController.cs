using Infrastructure.Analytics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Security;
using ParfaitApp.Services;
using Shared.Analytics;

namespace ParfaitApp.Controllers;

[Authorize]
[Route("internal")]
public sealed class InternalModulesController : Controller
{
    private readonly ParfaitProductService _products;
    private readonly ParfaitOrderService _orders;
    private readonly ParfaitInternalAnalyticsService _internalAnalytics;
    private readonly ParfaitCustomerAutomationService _automations;
    private readonly ParfaitInternalWorkspaceService _workspace;
    private readonly IGraphMailService _mail;
    private readonly IParfaitBusinessProfileService _businessProfile;
    private readonly IParfaitMetaAdsOAuthService _metaAdsOAuth;
    private readonly IMetaAdsService _metaAds;

    public InternalModulesController(
        ParfaitProductService products,
        ParfaitOrderService orders,
        ParfaitInternalAnalyticsService internalAnalytics,
        ParfaitCustomerAutomationService automations,
        ParfaitInternalWorkspaceService workspace,
        IGraphMailService mail,
        IParfaitBusinessProfileService businessProfile,
        IParfaitMetaAdsOAuthService metaAdsOAuth,
        IMetaAdsService metaAds)
    {
        _products = products;
        _orders = orders;
        _internalAnalytics = internalAnalytics;
        _automations = automations;
        _workspace = workspace;
        _mail = mail;
        _businessProfile = businessProfile;
        _metaAdsOAuth = metaAdsOAuth;
        _metaAds = metaAds;
    }


    [HttpGet("commerce/products")]
    [ParfaitInternalPage(
        "Products",
        "Operations",
        "Catalog management for pricing, product visibility, and storefront presentation.",
        3,
        2)]
    public IActionResult Products()
    {
        var products = _products.GetAllProducts().ToList();
        return View(new ParfaitProductAdminViewModel
        {
            Products = products,
            CommerceSettings = _products.GetCommerceSettings(),
            ActiveProductCount = products.Count(product => product.IsActive),
            FeaturedProductCount = products.Count(product => product.IsFeatured),
            TotalImageCount = products.Sum(product => product.Images.Count)
        });
    }

    [HttpPost("commerce/products")]
    [ValidateAntiForgeryToken]
    public IActionResult SaveProduct(ParfaitProductEditorViewModel product)
    {
        product.IsActive = HasCheckedValue(Request.Form, "IsActive");
        product.IsFeatured = HasCheckedValue(Request.Form, "IsFeatured");

        for (var index = 0; index < product.InventoryBySize.Count; index++)
        {
            product.InventoryBySize[index].IsEnabled = HasCheckedValue(Request.Form, $"InventoryBySize[{index}].IsEnabled");
        }

        for (var index = 0; index < product.DiscountCodes.Count; index++)
        {
            product.DiscountCodes[index].IsActive = HasCheckedValue(Request.Form, $"DiscountCodes[{index}].IsActive");
        }

        if (!ModelState.IsValid)
        {
            var products = _products.GetAllProducts().ToList();
            return View("Products", new ParfaitProductAdminViewModel
            {
                Products = products,
                NewProduct = product,
                CommerceSettings = _products.GetCommerceSettings(),
                ActiveProductCount = products.Count(item => item.IsActive),
                FeaturedProductCount = products.Count(item => item.IsFeatured),
                TotalImageCount = products.Sum(item => item.Images.Count)
            });
        }

        _products.SaveProduct(product);
        TempData["ProductStatus"] = product.IsActive
            ? "Product saved and visible."
            : "Product saved and hidden.";
        return RedirectToAction(nameof(Products));
    }

    [HttpPost("commerce/products/settings")]
    [ValidateAntiForgeryToken]
    public IActionResult SaveCommerceSettings(ParfaitCommerceSettingsViewModel settings)
    {
        settings.GlobalDiscount ??= new ParfaitProductDiscountCodeEditorViewModel();
        settings.GlobalDiscount.IsActive = HasCheckedValue(Request.Form, "GlobalDiscount.IsActive");

        _products.SaveCommerceSettings(settings);
        TempData["ProductStatus"] = "Commerce settings saved.";
        return RedirectToAction(nameof(Products));
    }

    [HttpPost("commerce/products/delete")]
    [ValidateAntiForgeryToken]
    public IActionResult DeleteProduct(string id)
    {
        _products.DeleteProduct(id);
        TempData["ProductStatus"] = "Product deleted.";
        return RedirectToAction(nameof(Products));
    }

    [HttpPost("commerce/products/images/upload")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadProductImages(string productId, List<IFormFile> images)
    {
        await _products.UploadImagesAsync(productId, images);
        TempData["ProductStatus"] = "Images uploaded.";
        return RedirectToAction(nameof(Products));
    }

    [HttpPost("commerce/products/images/delete")]
    [ValidateAntiForgeryToken]
    public IActionResult DeleteProductImage(string productId, string imageId)
    {
        _products.DeleteImage(productId, imageId);
        TempData["ProductStatus"] = "Image deleted.";
        return RedirectToAction(nameof(Products));
    }

    [HttpPost("commerce/products/images/reorder")]
    [ValidateAntiForgeryToken]
    public IActionResult ReorderProductImages(string productId, List<string> imageIds)
    {
        _products.ReorderImages(productId, imageIds);

        TempData["ProductStatus"] = "Image order updated.";
        return RedirectToAction(nameof(Products));
    }

    [HttpPost("commerce/products/reorder")]
    [ValidateAntiForgeryToken]
    public IActionResult ReorderProducts(List<string> productIds)
    {
        _products.ReorderProducts(productIds);
        TempData["ProductStatus"] = "Store order updated.";
        return RedirectToAction(nameof(Products));
    }

    [HttpPost("commerce/products/images/display")]
    [ValidateAntiForgeryToken]
    public IActionResult SaveProductImageDisplay(string productId, string imageId, string objectFit, int objectPositionX, int objectPositionY, decimal zoom)
    {
        _products.SaveImageDisplaySettings(productId, imageId, objectFit, objectPositionX, objectPositionY, zoom);
        TempData["ProductStatus"] = "Image display settings saved.";
        return RedirectToAction(nameof(Products));
    }

    [HttpGet("commerce/orders")]
    [ParfaitInternalPage(
        "Orders",
        "Operations",
        "Purchase, payment, and fulfillment tracking for the Parfait store.",
        3,
        3)]
    public IActionResult Orders()
    {
        var orders = _orders.GetAllOrders().ToList();
        var paidOrders = orders
            .Where(order => string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(order.PaymentStatus, "Refunded", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return View(new ParfaitOrderAdminViewModel
        {
            Orders = orders,
            PaidOrderCount = paidOrders.Count(order => string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase)),
            PendingOrderCount = orders.Count(order => string.Equals(order.PaymentStatus, "Pending", StringComparison.OrdinalIgnoreCase)),
            FailedOrderCount = orders.Count(order => string.Equals(order.PaymentStatus, "Failed", StringComparison.OrdinalIgnoreCase)),
            RefundedOrderCount = _orders.CountRefunded(orders),
            OpenFulfillmentCount = _orders.CountOpenFulfillment(orders),
            ReturnQueueCount = _orders.CountReturnQueue(orders),
            RevenueCents = _orders.SumNetRevenueCents(orders),
            AverageOrderValueCents = _orders.CalculateAverageNetOrderValueCents(orders)
        });
    }

    [HttpPost("commerce/orders/update")]
    [ValidateAntiForgeryToken]
    public IActionResult UpdateOrder(ParfaitOrderAdminUpdateRequest request)
    {
        if (!_orders.UpdateOrder(request))
        {
            TempData["OrderStatus"] = "Order could not be updated.";
            return RedirectToAction(nameof(Orders));
        }

        TempData["OrderStatus"] = $"Order {request.OrderNumber} updated.";
        return RedirectToAction(nameof(Orders));
    }

    [HttpPost("commerce/orders/receipt")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResendOrderReceipt(string orderNumber, CancellationToken ct)
    {
        var order = _orders.GetOrder(orderNumber);
        if (order is null)
        {
            TempData["OrderStatus"] = "Order was not found.";
            return RedirectToAction(nameof(Orders));
        }

        try
        {
            await _mail.SendOrderReceiptAsync(order, ct);
            TempData["OrderStatus"] = $"Receipt resent for {order.OrderNumber}.";
        }
        catch
        {
            TempData["OrderStatus"] = $"Receipt resend failed for {order.OrderNumber}.";
        }

        return RedirectToAction(nameof(Orders));
    }



    [HttpGet("automations")]
    [ParfaitInternalPage(
        "Automations",
        "Growth",
        "Customer automation, cart recovery, and lifecycle messaging controls.",
        4,
        2)]
    public IActionResult Automations()
    {
        return View(_automations.GetWorkspaceViewModel());
    }

    [HttpPost("automations/workflows")]
    [ValidateAntiForgeryToken]
    public IActionResult SaveAutomationWorkflow(ParfaitAutomationWorkflowEditorInput input)
    {
        if (!ModelState.IsValid)
        {
            TempData["AutomationStatus"] = "Workflow needs the required details before it can be saved.";
            TempData["AutomationStatusTone"] = "danger";
            return RedirectToAction(nameof(Automations));
        }

        _automations.SaveWorkflow(input);
        TempData["AutomationStatus"] = "Automation saved.";
        TempData["AutomationStatusTone"] = "success";
        return RedirectToAction(nameof(Automations));
    }

    [HttpPost("automations/workflows/delete")]
    [ValidateAntiForgeryToken]
    public IActionResult DeleteAutomationWorkflow(Guid id)
    {
        _automations.DeleteWorkflow(id);
        TempData["AutomationStatus"] = "Automation deleted.";
        TempData["AutomationStatusTone"] = "success";
        return RedirectToAction(nameof(Automations));
    }


    [HttpGet("analytics")]
    [ParfaitInternalPage(
        "Analytics",
        "Growth",
        "Shared ecommerce analytics, Meta diagnostics, and funnel intelligence.",
        4,
        4)]
    public async Task<IActionResult> Analytics(
        [FromQuery] string? preset = "30d",
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] string? qualityMode = null,
        [FromQuery] string? timezoneId = null,
        [FromQuery] int? timezoneOffsetMinutes = null,
        CancellationToken ct = default)
    {
        var resolvedQualityMode = ResolveParfaitAnalyticsQualityMode(qualityMode);
        var viewerTimeZone = ResolveViewerTimeZone(timezoneId, timezoneOffsetMinutes);
        return View(await _internalAnalytics.GetDashboardAsync(
            preset,
            fromUtc,
            toUtc,
            resolvedQualityMode,
            viewerTimeZone,
            timezoneId,
            timezoneOffsetMinutes,
            ct));
    }

    [HttpPost("analytics/meta-settings")]
    [ParfaitInternalPageAccess("/internal/analytics")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAnalyticsMetaSettings(
        ParfaitMetaAnalyticsSettingsViewModel model,
        [FromQuery] string? preset = "30d",
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] string? qualityMode = null,
        [FromQuery] string? timezoneId = null,
        [FromQuery] int? timezoneOffsetMinutes = null,
        CancellationToken ct = default)
    {
        var resolvedQualityMode = ResolveParfaitAnalyticsQualityMode(qualityMode);
        var viewerTimeZone = ResolveViewerTimeZone(timezoneId, timezoneOffsetMinutes);
        if (!ModelState.IsValid)
        {
            var dashboard = await _internalAnalytics.GetDashboardAsync(
                preset,
                fromUtc,
                toUtc,
                resolvedQualityMode,
                viewerTimeZone,
                timezoneId,
                timezoneOffsetMinutes,
                ct);
            dashboard.MetaSettings.MetaPixelId = model.MetaPixelId;
            dashboard.MetaSettings.MetaTestEventCode = model.MetaTestEventCode;
            return View("Analytics", dashboard);
        }

        await _businessProfile.SaveMetaSettingsAsync(model, ct);
        _internalAnalytics.InvalidateCache();
        TempData["AnalyticsStatus"] = "Meta settings saved.";
        return RedirectToAction(nameof(Analytics), new { preset, fromUtc, toUtc, qualityMode, timezoneId, timezoneOffsetMinutes });
    }

    [HttpGet("analytics/meta-connect")]
    [ParfaitInternalPageAccess("/internal/analytics")]
    public IActionResult MetaConnect([FromQuery] string? returnUrl = null)
    {
        var target = ResolveAnalyticsReturnUrl(returnUrl);

        try
        {
            return Redirect(_metaAdsOAuth.BuildConnectUrl(target));
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
        [FromQuery(Name = "error_description")] string? errorDescription = null)
    {
        var target = Url.Action(nameof(Analytics), "InternalModules") ?? "/internal/analytics";

        if (!string.IsNullOrWhiteSpace(error))
        {
            var message = string.IsNullOrWhiteSpace(errorDescription) ? error : errorDescription;
            return Redirect(AppendMetaStatus(target, "error", message));
        }

        try
        {
            var record = await _metaAdsOAuth.CompleteCallbackAsync(code ?? string.Empty, state ?? string.Empty, HttpContext.RequestAborted);
            await _businessProfile.SaveMetaConnectionAsync(record, HttpContext.RequestAborted);
            _internalAnalytics.InvalidateCache();
            return Redirect(AppendMetaStatus(target, "connected"));
        }
        catch (InvalidOperationException ex)
        {
            return Redirect(AppendMetaStatus(target, "error", ex.Message));
        }
        catch
        {
            return Redirect(AppendMetaStatus(target, "error", "Meta connection failed unexpectedly. Please try again."));
        }
    }

    [HttpGet("analytics/meta-connection-status")]
    [ParfaitInternalPageAccess("/internal/analytics")]
    public async Task<IActionResult> MetaConnectionStatus(CancellationToken ct)
    {
        return Json(await _businessProfile.GetMetaConnectionStatusAsync(ct));
    }

    [HttpGet("analytics/meta-campaigns")]
    [ParfaitInternalPageAccess("/internal/analytics")]
    public async Task<IActionResult> MetaCampaigns(
        [FromQuery] string? preset = "30d",
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] string? qualityMode = null,
        [FromQuery] string? timezoneId = null,
        [FromQuery] int? timezoneOffsetMinutes = null,
        CancellationToken ct = default)
    {
        try
        {
            var resolvedQualityMode = ResolveParfaitAnalyticsQualityMode(qualityMode);
            var viewerTimeZone = ResolveViewerTimeZone(timezoneId, timezoneOffsetMinutes);
            var range = TimeRangeRequest.FromPreset(
                string.IsNullOrWhiteSpace(preset) ? "30d" : preset,
                fromUtc,
                toUtc,
                viewerTz: viewerTimeZone,
                qualityMode: resolvedQualityMode);
            var scope = ScopeContext.ForSite(ParfaitMetaAdsConnectionStoreAdapter.SiteKey, ParfaitMetaAdsConnectionStoreAdapter.SiteKey);
            var result = await _metaAds.GetCampaignsAsync(range, scope, ct);
            return Json(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }


    [HttpGet("analytics/health-monitor")]
    [ParfaitInternalPageAccess("/internal/analytics")]
    public async Task<IActionResult> AnalyticsHealthMonitor(
        [FromQuery] string? preset = "30d",
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] string? qualityMode = null,
        [FromQuery] string? timezoneId = null,
        [FromQuery] int? timezoneOffsetMinutes = null,
        CancellationToken ct = default)
    {
        var resolvedQualityMode = ResolveParfaitAnalyticsQualityMode(qualityMode);
        var viewerTimeZone = ResolveViewerTimeZone(timezoneId, timezoneOffsetMinutes);

        var dashboard = await _internalAnalytics.GetDashboardAsync(
            preset,
            fromUtc,
            toUtc,
            resolvedQualityMode,
            viewerTimeZone,
            timezoneId,
            timezoneOffsetMinutes,
            ct);

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

        return Json(new
        {
            rangeLabel = dashboard.RangeLabel,
            summary = "Parfait ecommerce health snapshot loaded.",
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
                createdUtc = row.CreatedUtc,
                severity = string.Equals(row.MetaServerStatus, "Failed", StringComparison.OrdinalIgnoreCase) ? "Warning" : "Info",
                eventName = row.EventName,
                summary = $"{row.SourceLabel} · {row.DispatcherStatus} / {row.MetaServerStatus}"
            })
        });
    }

    [HttpPost("analytics/meta-disconnect")]
    [ParfaitInternalPageAccess("/internal/analytics")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MetaDisconnect(CancellationToken ct)
    {
        await _businessProfile.DisconnectMetaAsync(ct);
        _internalAnalytics.InvalidateCache();
        return Json(new { ok = true });
    }

    private static bool HasCheckedValue(IFormCollection form, string key)
    {
        return form[key].Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }

    private string ResolveAnalyticsReturnUrl(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return returnUrl;

        return Url.Action(nameof(Analytics), "InternalModules") ?? "/internal/analytics";
    }

    private static TrafficQualityMode ResolveParfaitAnalyticsQualityMode(string? qualityMode)
    {
        if (string.IsNullOrWhiteSpace(qualityMode))
            return TrafficQualityMode.RealHumanTraffic;

        return TrafficQualityBucketFilters.ParseClientOrEnumValue(qualityMode);
    }

    private static TimeZoneInfo ResolveViewerTimeZone(string? timezoneId, int? timezoneOffsetMinutes)
    {
        if (!string.IsNullOrWhiteSpace(timezoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timezoneId.Trim());
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        if (timezoneOffsetMinutes is >= -840 and <= 840)
        {
            try
            {
                return TimeZoneInfo.CreateCustomTimeZone(
                    $"viewer-offset-{timezoneOffsetMinutes.Value}",
                    TimeSpan.FromMinutes(-timezoneOffsetMinutes.Value),
                    "Viewer Local",
                    "Viewer Local");
            }
            catch
            {
            }
        }

        return TimeZoneInfo.Utc;
    }

    private static string AppendMetaStatus(string target, string meta, string? message = null)
    {
        var separator = target.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var url = $"{target}{separator}meta={Uri.EscapeDataString(meta)}";

        if (!string.IsNullOrWhiteSpace(message))
            url += $"&message={Uri.EscapeDataString(message)}";

        return url;
    }
}
