using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[Route("store")]
public sealed class StoreController : Controller
{
    private readonly ParfaitProductService _products;
    private readonly IParfaitBusinessProfileService _profileService;

    public StoreController(
        ParfaitProductService products,
        IParfaitBusinessProfileService profileService)
    {
        _products = products;
        _profileService = profileService;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        var model = new ParfaitStorefrontViewModel
        {
            StoreName = "ShopParfait",
            Headline = "Premium Parfait essentials, owned and operated by Parfait.",
            Subheadline = "Products shown here are managed from the internal Parfait commerce system.",
            Products = _products.GetActiveStoreProducts()
        };

        return View(model);
    }

    [HttpGet("cart")]
    public async Task<IActionResult> Cart(CancellationToken ct)
    {
        var profile = await _profileService.GetProfileAsync(ct);
        ViewBag.GlobalStoreCheckoutUrl = profile.GlobalStoreCheckoutUrl;
        return View();
    }

    [HttpGet("product/{slug}")]
    public IActionResult Product(string slug)
    {
        var product = _products.GetActiveStoreProducts()
            .FirstOrDefault(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase));

        if (product is null)
        {
            return NotFound();
        }

        return View(product);
    }
}
