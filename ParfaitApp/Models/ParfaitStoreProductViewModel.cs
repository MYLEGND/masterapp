using System.ComponentModel.DataAnnotations;

namespace ParfaitApp.Models;

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

public sealed class ParfaitStoreProductViewModel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public required string Description { get; init; }
    public required string PriceLabel { get; init; }
    public int PriceCents { get; init; }
    public string Badge { get; init; } = "Parfait";
    public bool IsFeatured { get; init; }
    public IReadOnlyList<ParfaitStoreProductImageViewModel> Images { get; init; } = [];

    public ParfaitStoreProductImageViewModel? PrimaryImage => Images.FirstOrDefault(i => i.IsPrimary)
        ?? Images.OrderBy(i => i.DisplayOrder).FirstOrDefault();

    public string PrimaryImageUrl => PrimaryImage?.ImageUrl ?? "/images/favicon/parfait-logo.png";
    public string PrimaryImageObjectFit => PrimaryImage?.ObjectFit ?? "cover";
    public int PrimaryImageObjectPositionX => PrimaryImage?.ObjectPositionX ?? 50;
    public int PrimaryImageObjectPositionY => PrimaryImage?.ObjectPositionY ?? 50;
    public decimal PrimaryImageZoom => PrimaryImage?.Zoom ?? 1.0m;
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
    public bool IsFeatured { get; set; }
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; }
    public List<ParfaitProductImageEditorViewModel> Images { get; set; } = [];
}
