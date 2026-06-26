using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Security;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[Authorize]
[Route("internal")]
public sealed class InternalModulesController : Controller
{
    private readonly ParfaitProductService _products;
    private readonly ParfaitOrderService _orders;
    private readonly ParfaitAnalyticsDashboardService _analyticsDashboard;
    private readonly ParfaitInternalWorkspaceService _workspace;

    public InternalModulesController(
        ParfaitProductService products,
        ParfaitOrderService orders,
        ParfaitAnalyticsDashboardService analyticsDashboard,
        ParfaitInternalWorkspaceService workspace)
    {
        _products = products;
        _orders = orders;
        _analyticsDashboard = analyticsDashboard;
        _workspace = workspace;
    }

    [HttpGet("commerce")]
    [ParfaitInternalPage(
        "Commerce",
        "Operations",
        "Products, orders, inventory, and revenue controls for Parfait commerce operations.",
        3,
        1)]
    public async Task<IActionResult> Commerce(CancellationToken ct) => View(await _workspace.GetSnapshotAsync(ct));

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
            ActiveProductCount = products.Count(product => product.IsActive),
            FeaturedProductCount = products.Count(product => product.IsFeatured),
            TotalImageCount = products.Sum(product => product.Images.Count)
        });
    }

    [HttpPost("commerce/products")]
    [ValidateAntiForgeryToken]
    public IActionResult SaveProduct(ParfaitProductEditorViewModel product)
    {
        product.IsActive = Request.Form["IsActive"].Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
        product.IsFeatured = Request.Form["IsFeatured"].Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

        if (!ModelState.IsValid)
        {
            var products = _products.GetAllProducts().ToList();
            return View("Products", new ParfaitProductAdminViewModel
            {
                Products = products,
                NewProduct = product,
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

    [HttpPost("commerce/products/images/primary")]
    [ValidateAntiForgeryToken]
    public IActionResult SetPrimaryProductImage(string productId, string imageId)
    {
        _products.SetPrimaryImage(productId, imageId);
        TempData["ProductStatus"] = "Primary image updated.";
        return RedirectToAction(nameof(Products));
    }

    [HttpPost("commerce/products/images/move")]
    [ValidateAntiForgeryToken]
    public IActionResult MoveProductImage(string productId, string imageId, string direction)
    {
        _products.MoveImage(productId, imageId, direction);
        TempData["ProductStatus"] = "Image order updated.";
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
            .Where(order => string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return View(new ParfaitOrderAdminViewModel
        {
            Orders = orders,
            PaidOrderCount = paidOrders.Count,
            PendingOrderCount = orders.Count(order => string.Equals(order.PaymentStatus, "Pending", StringComparison.OrdinalIgnoreCase)),
            FailedOrderCount = orders.Count(order => string.Equals(order.PaymentStatus, "Failed", StringComparison.OrdinalIgnoreCase)),
            OpenFulfillmentCount = orders.Count(order => !string.Equals(order.FulfillmentStatus, "Fulfilled", StringComparison.OrdinalIgnoreCase)),
            RevenueCents = paidOrders.Sum(order => order.TotalCents),
            AverageOrderValueCents = paidOrders.Count == 0 ? 0 : (int)Math.Round(paidOrders.Average(order => order.TotalCents))
        });
    }

    [HttpGet("customers")]
    [ParfaitInternalPage(
        "Customers",
        "Operations",
        "Customer accounts, order history, support, and engagement controls.",
        3,
        4)]
    public async Task<IActionResult> Customers(CancellationToken ct) => View(await _workspace.GetSnapshotAsync(ct));

    [HttpGet("marketing")]
    [ParfaitInternalPage(
        "Marketing",
        "Growth",
        "Campaign planning and attribution controls for Parfait growth.",
        4,
        1)]
    public async Task<IActionResult> Marketing(CancellationToken ct) => View(await _workspace.GetSnapshotAsync(ct));

    [HttpGet("content")]
    [ParfaitInternalPage(
        "Content",
        "Growth",
        "Products, creative assets, and storytelling management for the brand.",
        4,
        3)]
    public async Task<IActionResult> Content(CancellationToken ct) => View(await _workspace.GetSnapshotAsync(ct));

    [HttpGet("analytics")]
    [ParfaitInternalPage(
        "Analytics",
        "Growth",
        "Internal funnel reporting and ecommerce event intelligence.",
        4,
        2)]
    public async Task<IActionResult> Analytics(CancellationToken ct)
    {
        return View(await _analyticsDashboard.GetDashboardAsync(ct));
    }
}
