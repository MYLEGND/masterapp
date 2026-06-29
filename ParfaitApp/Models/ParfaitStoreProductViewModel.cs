using System.ComponentModel.DataAnnotations;

namespace ParfaitApp.Models;

public static class ParfaitProductCatalogDefaults
{
    public static readonly string[] StandardSizes = ["XS", "S", "M", "L", "XL"];

    public static List<ParfaitProductSizeInventoryEditorViewModel> CreateDefaultInventory()
    {
        return StandardSizes
            .Select((size, index) => new ParfaitProductSizeInventoryEditorViewModel
            {
                Size = size,
                DisplayOrder = (index + 1) * 10,
                IsEnabled = true,
                StockQuantity = 0,
                LowStockThreshold = 20
            })
            .ToList();
    }

    public static string NormalizeSize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? ""
            : value.Trim().ToUpperInvariant();
    }

    public static string NormalizeDiscountCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return string.Concat(value
                .Trim()
                .ToUpperInvariant()
                .Select(character => char.IsLetterOrDigit(character) ? character : '-'))
            .Replace("--", "-")
            .Trim('-');
    }
}

public sealed class ParfaitStoreProductImageViewModel
{
    public required string Id { get; init; }
    public required string ImageUrl { get; init; }
    public string AltText { get; init; } = "";
    public bool IsPrimary { get; init; }
    public int DisplayOrder { get; init; }
    public string ObjectFit { get; init; } = "cover";
    public int ObjectPositionX { get; init; } = 50;
    public int ObjectPositionY { get; init; } = 50;
    public decimal Zoom { get; init; } = 1.0m;
}

public sealed class ParfaitStoreProductSizeViewModel
{
    public required string Id { get; init; }
    public required string Size { get; init; }
    public bool IsEnabled { get; init; }
    public int StockQuantity { get; init; }
    public int LowStockThreshold { get; init; }
    public int DisplayOrder { get; init; }

    public bool IsSoldOut => IsEnabled && StockQuantity <= 0;
    public bool IsLowStock => IsEnabled && StockQuantity > 0 && StockQuantity <= Math.Max(1, LowStockThreshold);
    public bool CanPurchase => IsEnabled && StockQuantity > 0;

    public string StatusTone => !IsEnabled
        ? "muted"
        : IsSoldOut
            ? "danger"
            : IsLowStock
                ? "warning"
                : "success";

    public string StatusLabel => !IsEnabled
        ? "Hidden"
        : IsSoldOut
            ? "Sold Out"
            : IsLowStock
                ? $"{StockQuantity} Left"
                : $"{StockQuantity} In Stock";
}

public sealed class ParfaitStoreProductViewModel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public required string Description { get; init; }
    public required string PriceLabel { get; init; }
    public int PriceCents { get; init; }
    public int CompareAtPriceCents { get; init; }
    public int DisplayPriceCents { get; init; }
    public int DisplayCompareAtPriceCents { get; init; }
    public string DisplayDiscountLabel { get; init; } = "";
    public string Badge { get; init; } = "Parfait";
    public bool IsFeatured { get; init; }
    public IReadOnlyList<ParfaitStoreProductImageViewModel> Images { get; init; } = [];
    public IReadOnlyList<ParfaitStoreProductSizeViewModel> Sizes { get; init; } = [];

    public ParfaitStoreProductImageViewModel? PrimaryImage => Images.FirstOrDefault(i => i.IsPrimary)
        ?? Images.OrderBy(i => i.DisplayOrder).FirstOrDefault();

    public ParfaitStoreProductSizeViewModel? DefaultSize => Sizes.FirstOrDefault(size => size.CanPurchase)
        ?? Sizes.FirstOrDefault(size => size.IsEnabled)
        ?? Sizes.OrderBy(size => size.DisplayOrder).FirstOrDefault();

    public string PrimaryImageUrl => PrimaryImage?.ImageUrl ?? "/images/favicon/parfait-logo.png";
    public string PrimaryImageObjectFit => PrimaryImage?.ObjectFit ?? "cover";
    public int PrimaryImageObjectPositionX => PrimaryImage?.ObjectPositionX ?? 50;
    public int PrimaryImageObjectPositionY => PrimaryImage?.ObjectPositionY ?? 50;
    public decimal PrimaryImageZoom => PrimaryImage?.Zoom ?? 1.0m;
    public bool HasSalePrice => CompareAtPriceCents > PriceCents && PriceCents > 0;
    public int SavingsCents => HasSalePrice ? CompareAtPriceCents - PriceCents : 0;
    public string CompareAtPriceLabel => HasSalePrice ? $"${CompareAtPriceCents / 100m:0.00}" : "";
    public string SavingsLabel => HasSalePrice ? $"Save ${SavingsCents / 100m:0.00}" : "";
    public bool HasDisplaySalePrice => DisplayCompareAtPriceCents > DisplayPriceCents && DisplayPriceCents >= 0;
    public bool HasDisplayDiscountLabel => !string.IsNullOrWhiteSpace(DisplayDiscountLabel);
    public int DisplaySavingsCents => HasDisplaySalePrice ? DisplayCompareAtPriceCents - DisplayPriceCents : 0;
    public string DisplayPriceLabel => DisplayPriceCents > 0 || PriceCents > 0
        ? $"${DisplayPriceCents / 100m:0.00}"
        : PriceLabel;
    public string DisplayCompareAtPriceLabel => HasDisplaySalePrice ? $"${DisplayCompareAtPriceCents / 100m:0.00}" : "";
    public string DisplaySavingsLabel => HasDisplaySalePrice ? $"Save ${DisplaySavingsCents / 100m:0.00}" : "";
    public bool HasTrackedInventory => Sizes.Any(size => size.IsEnabled);
    public int TotalTrackedStock => Sizes.Where(size => size.IsEnabled).Sum(size => Math.Max(size.StockQuantity, 0));
    public bool IsSoldOut => Sizes.Count > 0 && Sizes.Where(size => size.IsEnabled).All(size => !size.CanPurchase);
    public bool IsLowStock => Sizes.Any(size => size.IsLowStock);
    public string AvailabilityTone => IsSoldOut ? "danger" : IsLowStock ? "warning" : "success";
    public string AvailabilityLabel => IsSoldOut
        ? "Sold Out"
        : IsLowStock
            ? "Low Stock"
            : $"{TotalTrackedStock} Units Ready";
}

public sealed class ParfaitStorefrontViewModel
{
    public required string StoreName { get; init; }
    public required string Headline { get; init; }
    public required string Subheadline { get; init; }
    public required IReadOnlyList<ParfaitStoreProductViewModel> Products { get; init; }
}

public sealed class ParfaitProductAdminViewModel
{
    public List<ParfaitProductEditorViewModel> Products { get; init; } = [];
    public ParfaitProductEditorViewModel NewProduct { get; init; } = new();
    public ParfaitCommerceSettingsViewModel CommerceSettings { get; init; } = new();
    public int ActiveProductCount { get; init; }
    public int FeaturedProductCount { get; init; }
    public int TotalImageCount { get; init; }
}

public sealed class ParfaitCommerceSettingsViewModel
{
    public int ShippingFeeCents { get; set; }
    public decimal TaxPercent { get; set; }
    public ParfaitProductDiscountCodeEditorViewModel GlobalDiscount { get; set; } = new();

    public bool HasActiveGlobalDiscount =>
        GlobalDiscount.IsActive
        && !string.IsNullOrWhiteSpace(GlobalDiscount.Code)
        && GlobalDiscount.Amount > 0;
}

public sealed class ParfaitProductImageEditorViewModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ImageUrl { get; set; } = "";
    public string FileName { get; set; } = "";
    public string AltText { get; set; } = "";
    public bool IsPrimary { get; set; }
    public int DisplayOrder { get; set; }
    public string ObjectFit { get; set; } = "cover";
    public int ObjectPositionX { get; set; } = 50;
    public int ObjectPositionY { get; set; } = 50;
    public decimal Zoom { get; set; } = 1.0m;
}

public sealed class ParfaitProductSizeInventoryEditorViewModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Required]
    public string Size { get; set; } = "";

    public bool IsEnabled { get; set; } = true;
    public int StockQuantity { get; set; }
    public int LowStockThreshold { get; set; } = 20;
    public int DisplayOrder { get; set; }

    public bool IsSoldOut => IsEnabled && StockQuantity <= 0;
    public bool IsLowStock => IsEnabled && StockQuantity > 0 && StockQuantity <= Math.Max(1, LowStockThreshold);
    public string StatusTone => !IsEnabled ? "muted" : IsSoldOut ? "danger" : IsLowStock ? "warning" : "success";
    public string StatusLabel => !IsEnabled
        ? "Hidden"
        : IsSoldOut
            ? "Sold Out"
            : IsLowStock
                ? $"{StockQuantity} Left"
                : $"{StockQuantity} Ready";
}

public sealed class ParfaitProductDiscountCodeEditorViewModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Code { get; set; } = "";

    public string DiscountType { get; set; } = "Percent";
    public decimal Amount { get; set; }
    public bool IsActive { get; set; } = true;

    public string SummaryLabel => string.Equals(DiscountType, "Fixed", StringComparison.OrdinalIgnoreCase)
        ? $"${Amount:0.00} Off"
        : $"{Amount:0.#}% Off";
}

public sealed class ParfaitProductEditorViewModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Required]
    public string Name { get; set; } = "";

    [Required]
    public string Slug { get; set; } = "";

    [Required]
    public string Description { get; set; } = "";

    [Required]
    public string PriceLabel { get; set; } = "Coming Soon";

    public string Badge { get; set; } = "Parfait";
    public int PriceCents { get; set; }
    public int CompareAtPriceCents { get; set; }
    public bool IsFeatured { get; set; }
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; }
    public List<ParfaitProductImageEditorViewModel> Images { get; set; } = [];
    public List<ParfaitProductSizeInventoryEditorViewModel> InventoryBySize { get; set; } = ParfaitProductCatalogDefaults.CreateDefaultInventory();
    public List<ParfaitProductDiscountCodeEditorViewModel> DiscountCodes { get; set; } = [];

    public bool HasSalePrice => CompareAtPriceCents > PriceCents && PriceCents > 0;
    public int TotalTrackedStock => InventoryBySize
        .Where(size => size.IsEnabled)
        .Sum(size => Math.Max(size.StockQuantity, 0));
    public int LowStockSizeCount => InventoryBySize.Count(size => size.IsLowStock);
    public int SoldOutSizeCount => InventoryBySize.Count(size => size.IsSoldOut);
    public int ActiveDiscountCount => DiscountCodes.Count(code =>
        code.IsActive
        && !string.IsNullOrWhiteSpace(code.Code)
        && code.Amount > 0);
    public string InventoryTone => SoldOutSizeCount > 0 && TotalTrackedStock == 0
        ? "danger"
        : LowStockSizeCount > 0
            ? "warning"
            : "success";
    public string InventoryLabel => TotalTrackedStock > 0
        ? $"{TotalTrackedStock} Units"
        : "Needs Stock";
}
