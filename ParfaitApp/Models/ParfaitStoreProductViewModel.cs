using System.ComponentModel.DataAnnotations;

namespace ParfaitApp.Models;

public sealed class ParfaitStoreProductImageViewModel
{
    public required string Id { get; init; }
    public required string ImageUrl { get; init; }
    public string AltText { get; init; } = "";
    public bool IsPrimary { get; init; }
    public int DisplayOrder { get; init; }
}

public sealed class ParfaitStoreProductViewModel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public required string Description { get; init; }
    public required string PriceLabel { get; init; }
    public string Badge { get; init; } = "Parfait";
    public bool IsFeatured { get; init; }
    public IReadOnlyList<ParfaitStoreProductImageViewModel> Images { get; init; } = [];
    public string PrimaryImageUrl => Images.FirstOrDefault(i => i.IsPrimary)?.ImageUrl
        ?? Images.OrderBy(i => i.DisplayOrder).FirstOrDefault()?.ImageUrl
        ?? "/images/favicon/parfait-logo.png";
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
    public bool IsFeatured { get; set; }
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; }
    public List<ParfaitProductImageEditorViewModel> Images { get; set; } = [];
}
