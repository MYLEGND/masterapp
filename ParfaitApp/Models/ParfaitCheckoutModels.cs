namespace ParfaitApp.Models;

public sealed class ParfaitCheckoutItemRequest
{
    public string? Id { get; set; }
    public string? Size { get; set; }
    public int Quantity { get; set; }
}

public sealed class ParfaitCheckoutPayRequest
{
    public string? SourceId { get; set; }
    public List<ParfaitCheckoutItemRequest> Items { get; set; } = [];
}

public sealed class ParfaitCheckoutPayResponse
{
    public bool Success { get; set; }
    public string? RedirectUrl { get; set; }
    public string? Error { get; set; }
}

public sealed class ParfaitValidatedCartItem
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Size { get; set; }
    public int Quantity { get; set; }
    public int UnitPriceCents { get; set; }
    public int LineTotalCents => UnitPriceCents * Quantity;
}
