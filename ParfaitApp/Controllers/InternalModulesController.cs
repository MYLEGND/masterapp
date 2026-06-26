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

    public InternalModulesController(
        ParfaitProductService products,
        ParfaitOrderService orders,
        ParfaitAnalyticsDashboardService analyticsDashboard)
    {
        _products = products;
        _orders = orders;
        _analyticsDashboard = analyticsDashboard;
    }

    [HttpGet("commerce")]
    [ParfaitInternalPage(
        "Commerce",
        "Operations",
        "Products, orders, inventory, and revenue controls for Parfait commerce operations.",
        3,
        1)]
    public IActionResult Commerce() => View();

    [HttpGet("commerce/products")]
    [ParfaitInternalPage(
        "Products",
        "Operations",
        "Catalog management for pricing, product visibility, and storefront presentation.",
        3,
        2)]
    public IActionResult Products()
    {
        return View(new ParfaitProductAdminViewModel
        {
            Products = _products.GetAllProducts().ToList()
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
            return View("Products", new ParfaitProductAdminViewModel
            {
                Products = _products.GetAllProducts().ToList(),
                NewProduct = product
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
        return View(new ParfaitOrderAdminViewModel
        {
            Orders = _orders.GetAllOrders().ToList()
        });
    }

    [HttpGet("customers")]
    [ParfaitInternalPage(
        "Customers",
        "Operations",
        "Customer accounts, order history, support, and engagement controls.",
        3,
        4)]
    public IActionResult Customers() => View();

    [HttpGet("marketing")]
    [ParfaitInternalPage(
        "Marketing",
        "Growth",
        "Campaign planning and attribution controls for Parfait growth.",
        4,
        1)]
    public IActionResult Marketing() => View();

    [HttpGet("content")]
    [ParfaitInternalPage(
        "Content",
        "Growth",
        "Products, creative assets, and storytelling management for the brand.",
        4,
        3)]
    public IActionResult Content() => View();

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
