using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Models;

namespace ParfaitApp.Controllers;

[Route("store")]
public sealed class StoreController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        var model = new ParfaitStorefrontViewModel
        {
            StoreName = "ShopParfait",
            Headline = "Premium Parfait essentials, owned and operated by Parfait.",
            Subheadline = "A clean storefront foundation ready to be managed from the internal Parfait business profile and commerce system.",
            Products =
            [
                new()
                {
                    Name = "Parfait Signature Package",
                    Slug = "parfait-signature-package",
                    Description = "A premium starter package for the Parfait brand experience.",
                    ImageUrl = "/images/favicon/parfait-logo.png",
                    PriceLabel = "Coming Soon",
                    Badge = "Featured",
                    IsFeatured = true
                },
                new()
                {
                    Name = "Parfait Training Package",
                    Slug = "parfait-training-package",
                    Description = "Training-focused offer placeholder for the owned Parfait store.",
                    ImageUrl = "/images/favicon/parfait-logo.png",
                    PriceLabel = "Coming Soon",
                    Badge = "Training"
                },
                new()
                {
                    Name = "Parfait Lifestyle Package",
                    Slug = "parfait-lifestyle-package",
                    Description = "Lifestyle product placeholder managed by Parfait internal commerce.",
                    ImageUrl = "/images/favicon/parfait-logo.png",
                    PriceLabel = "Coming Soon",
                    Badge = "Lifestyle"
                }
            ]
        };

        return View(model);
    }
}
