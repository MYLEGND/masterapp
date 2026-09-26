using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Services;

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
    IGraphMailService mail) : Controller
{
    [HttpGet("workspace")]
    public async Task<IActionResult> Workspace(
        [FromQuery] string ticket,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();
        return RedirectToAction(nameof(Products), new { ticket });
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
        [FromForm] string ticket,
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
        [FromForm] string ticket,
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
        [FromForm] string ticket,
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
        [FromForm] string ticket,
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
        [FromForm] string ticket,
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
        [FromForm] string ticket,
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
        [FromForm] string ticket,
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
        [FromForm] string ticket,
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
        [FromForm] string ticket,
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
        [FromForm] string ticket,
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
        [FromForm] string ticket,
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
        [FromForm] string ticket,
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
            "frame-ancestors 'self' https://mylegnd.com https://www.mylegnd.com https://protect.mylegnd.com https://masterapp-protect.azurewebsites.net";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        Response.Headers.Remove("X-Frame-Options");
    }

    private static bool HasCheckedValue(IFormCollection form, string key) =>
        form[key].Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
}
