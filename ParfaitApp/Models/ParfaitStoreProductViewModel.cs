namespace ParfaitApp.Models;

public sealed class ParfaitStoreProductViewModel
{
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public required string Description { get; init; }
    public required string ImageUrl { get; init; }
    public required string PriceLabel { get; init; }
    public string Badge { get; init; } = "Parfait";
    public bool IsFeatured { get; init; }
}

public sealed class ParfaitStorefrontViewModel
{
    public required string StoreName { get; init; }
    public required string Headline { get; init; }
    public required string Subheadline { get; init; }
    public required IReadOnlyList<ParfaitStoreProductViewModel> Products { get; init; }
}
