using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[Route("commerce/manage")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[IgnoreAntiforgeryToken]
public sealed class CommerceManagementController(
    MasterAppDbContext db,
    WebsiteEditorTicketProtector tickets,
    IConfiguration configuration,
    CommerceStoreContextService stores,
    ParfaitProductService products,
    ParfaitOrderService orders) : Controller
{
    [HttpGet("workspace")]
    public async Task<IActionResult> Workspace(
        [FromQuery] string ticket,
        [FromQuery] string? tab = null,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();

        ApplyFramePolicy();
        var model = await BuildWorkspaceAsync(ticket, store, tab, ct);
        return View("~/Views/CommerceManagement/Workspace.cshtml", model);
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
        return RedirectWorkspace(ticket, "products", "Product saved.");
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
        return RedirectWorkspace(ticket, "products", "Product deleted.");
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
        return RedirectWorkspace(ticket, "products", "Product images uploaded.");
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
        return RedirectWorkspace(ticket, "products", "Product image removed.");
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
        products.SaveImageDisplaySettings(
            store.CommerceBusinessId,
            productId,
            imageId,
            objectFit,
            objectPositionX,
            objectPositionY,
            zoom);
        return RedirectWorkspace(ticket, "products", "Image presentation saved.");
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
        return RedirectWorkspace(ticket, "products", "Product order saved.");
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
        return RedirectWorkspace(ticket, "orders", "Order updated.");
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
        products.SaveCommerceSettings(store.CommerceBusinessId, settings);
        return RedirectWorkspace(ticket, "settings", "Commerce settings saved.");
    }

    [HttpPost("settings/storefront")]
    public async Task<IActionResult> SaveStorefrontSettings(
        [FromForm] string ticket,
        [FromForm] CommerceStorefrontSettingsInput input,
        CancellationToken ct = default)
    {
        var store = await ResolveAsync(ticket, ct);
        if (store is null) return Unauthorized();

        var row = await db.CommerceBusinessStorefrontSettings
            .SingleOrDefaultAsync(x => x.CommerceBusinessId == store.CommerceBusinessId, ct);
        if (row is null)
        {
            row = new Domain.Entities.CommerceBusinessStorefrontSettings
            {
                CommerceBusinessId = store.CommerceBusinessId
            };
            db.CommerceBusinessStorefrontSettings.Add(row);
        }

        row.BrandHeadline = Clean(input.BrandHeadline, 180, store.StoreName);
        row.BrandSubheadline = Clean(input.BrandSubheadline, 300, store.StoreName + " storefront.");
        row.StorefrontStatus = Clean(input.StorefrontStatus, 40, "Active");
        row.Revision = Guid.NewGuid();
        row.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return RedirectWorkspace(ticket, "settings", "Store presentation saved.");
    }

    private async Task<CommerceStoreContext?> ResolveAsync(string ticket, CancellationToken ct) =>
        await stores.ResolveForWebsiteTicketAsync(ticket, tickets, configuration, ct);

    private async Task<CommerceManagementWorkspaceViewModel> BuildWorkspaceAsync(
        string ticket,
        CommerceStoreContext store,
        string? tab,
        CancellationToken ct)
    {
        var storefront = await db.CommerceBusinessStorefrontSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CommerceBusinessId == store.CommerceBusinessId, ct)
            ?? new Domain.Entities.CommerceBusinessStorefrontSettings
            {
                CommerceBusinessId = store.CommerceBusinessId,
                BrandHeadline = store.Headline,
                BrandSubheadline = store.Subheadline,
                StorefrontStatus = "Active"
            };

        return new CommerceManagementWorkspaceViewModel
        {
            Ticket = ticket,
            Store = store,
            Products = products.GetAllProducts(store.CommerceBusinessId),
            Orders = orders.GetAllOrders(store.CommerceBusinessId),
            CommerceSettings = products.GetCommerceSettings(store.CommerceBusinessId),
            StorefrontSettings = storefront,
            ActiveTab = NormalizeTab(tab)
        };
    }

    private IActionResult RedirectWorkspace(string ticket, string tab, string message)
    {
        TempData["CommerceStatus"] = message;
        return RedirectToAction(nameof(Workspace), new { ticket, tab });
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

    private static string NormalizeTab(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "orders" => "orders",
            "settings" => "settings",
            _ => "products"
        };

    private static string Clean(string? value, int maximum, string fallback)
    {
        var cleaned = (value ?? "").Trim();
        if (cleaned.Length == 0) cleaned = fallback;
        return cleaned.Length <= maximum ? cleaned : cleaned[..maximum];
    }
}
