using ParfaitApp.Models;

namespace Legend.Commerce;

/// <summary>
/// Business-scoped, read-only catalog projection. Consumers must always
/// supply the authorized CommerceBusinessId; no implicit Parfait provisioning.
/// </summary>
public interface ICommerceCatalogReader
{
    IReadOnlyList<ParfaitStoreProductViewModel> GetActiveStoreProducts(Guid commerceBusinessId);
    ParfaitStoreProductViewModel? GetActiveStoreProductBySlug(Guid commerceBusinessId, string slug);
    ParfaitStoreProductViewModel? GetActiveStoreProductById(Guid commerceBusinessId, string id);
}
