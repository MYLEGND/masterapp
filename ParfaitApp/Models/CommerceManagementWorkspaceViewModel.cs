using Domain.Entities;
using ParfaitApp.Services;

namespace ParfaitApp.Models;

public sealed class CommerceManagementWorkspaceViewModel
{
    public required string Ticket { get; init; }
    public required CommerceStoreContext Store { get; init; }
    public required IReadOnlyList<ParfaitProductEditorViewModel> Products { get; init; }
    public required IReadOnlyList<ParfaitOrderRecord> Orders { get; init; }
    public required ParfaitCommerceSettingsViewModel CommerceSettings { get; init; }
    public required CommerceBusinessStorefrontSettings StorefrontSettings { get; init; }
    public string ActiveTab { get; init; } = "products";

    public int ActiveProductCount => Products.Count(x => x.IsActive);
    public int LowStockCount => Products.Sum(x => x.LowStockSizeCount);
    public int PaidOrderCount => Orders.Count(x => x.IsPaid);
    public int OpenFulfillmentCount => Orders.Count(x => x.IsFulfillmentOpen);
    public int NetRevenueCents => Orders.Sum(x => x.NetRevenueCents);
}

public sealed class CommerceStorefrontSettingsInput
{
    public string BrandHeadline { get; set; } = "";
    public string BrandSubheadline { get; set; } = "";
    public string StorefrontStatus { get; set; } = "Active";
}

public sealed class CommerceProductOrderInput
{
    public List<string> ProductIds { get; set; } = [];
}
