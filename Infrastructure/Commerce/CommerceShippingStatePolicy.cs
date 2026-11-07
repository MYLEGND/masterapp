using System.Text.Json;

namespace Infrastructure.Commerce;

public static class CommerceShippingStatePolicy
{
    public static bool HasUnresolvedPurchase(string? state)
    {
        if (state is null) return false;
        using var document = JsonDocument.Parse(state);
        return document.RootElement.TryGetProperty("Status", out var status)
            && status.GetString() == "PurchasePending";
    }
}
