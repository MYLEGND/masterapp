using System.Text.Json;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;

namespace ParfaitApp.Services;

public sealed class StoreInventoryException(string message) : InvalidOperationException(message);

public sealed partial class ParfaitProductService
{
    private static List<ParfaitBundleComponent> ReadComponents(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize<List<ParfaitBundleComponent>>(json) ?? [];

    private static IReadOnlyList<ParfaitInventoryRequirement> InventoryFor(
        ParfaitProductEditorViewModel product, ParfaitProductSizeInventoryEditorViewModel size) =>
        product.BundleComponents.Count > 0
            ? product.BundleComponents.Where(part => part.BundleSize == size.Size)
                .Select(part => new ParfaitInventoryRequirement(part.InventoryItemId, part.Units)).ToArray()
            : size.InventoryItemId == Guid.Empty ? [] : [new(size.InventoryItemId, 1)];

    private static void ProjectBundleInventory(IReadOnlyList<ParfaitProductEditorViewModel> products,
        Dictionary<Guid, int> reserved, bool availableOnly)
    {
        var available = new Dictionary<Guid, int>();
        foreach (var product in products.Where(product => product.BundleComponents.Count == 0))
        {
            foreach (var size in product.InventoryBySize)
            {
                size.AvailableStockQuantity = Math.Max(0, size.StockQuantity - reserved.GetValueOrDefault(size.InventoryItemId));
                if (availableOnly)
                    size.StockQuantity = size.AvailableStockQuantity;
                if (size.InventoryItemId != Guid.Empty)
                    available.Add(size.InventoryItemId, product.IsActive && size.IsEnabled &&
                        product.BillingKind == ClientSubscriptionScope.OneTimeProduct ? size.AvailableStockQuantity : 0);
            }
        }

        foreach (var product in products.Where(product => product.BundleComponents.Count > 0))
        {
            foreach (var size in product.InventoryBySize)
            {
                var parts = InventoryFor(product, size);
                size.StockQuantity = parts.Count == 0 ? 0 :
                    parts.Min(part => available.GetValueOrDefault(part.InventoryItemId) / part.Units);
            }
        }
    }

    private List<ParfaitBundleComponent> ValidateBundle(Guid businessId, ParfaitProductEditorViewModel product,
        CommerceProduct? existing)
    {
        List<ParfaitBundleComponent> parts;
        try { parts = ReadComponents(product.BundleComponentsJson ?? existing?.BundleComponentsJson); }
        catch (JsonException) { throw new StoreInventoryException("The bundle links are invalid. Reopen the bundle picker."); }
        if (parts.Count == 0) return parts;
        if (product.BillingKind != ClientSubscriptionScope.OneTimeProduct || parts.Count > 200 ||
            parts.Any(part => part is null || part.Units is < 1 or > 99 || part.InventoryItemId == Guid.Empty))
            throw new StoreInventoryException("A bundle must contain individual products with 1–99 units per link.");

        parts = parts.Select(part => part with
        {
            BundleSize = ParfaitProductCatalogDefaults.NormalizeSize(part.BundleSize)
        }).ToList();
        var sizes = NormalizeInventory(product.InventoryBySize)
            .Select(size => size.Size).ToHashSet(StringComparer.Ordinal);
        if (parts.Any(part => !sizes.Contains(part.BundleSize)) ||
            parts.GroupBy(part => (part.BundleSize, part.InventoryItemId)).Any(group => group.Count() > 1))
            throw new StoreInventoryException("Link each piece once per bundle size. Use units for repeated pieces.");

        var ids = parts.Select(part => part.InventoryItemId).Distinct().ToArray();
        var eligible = _db.CommerceProductInventoryItems.Count(row => ids.Contains(row.Id) &&
            row.CommerceProduct!.CommerceBusinessId == businessId &&
            row.CommerceProduct.ExternalProductKey != product.Id && row.CommerceProduct.BundleComponentsJson == null &&
            row.CommerceProduct.BillingKind == ClientSubscriptionScope.OneTimeProduct);
        if (eligible != ids.Length)
            throw new StoreInventoryException("Choose sizes of individual products in this store. Bundles cannot contain themselves or other bundles.");
        return parts;
    }

    private void ValidateInventoryEdits(Guid businessId, CommerceProduct existing, ParfaitProductEditorViewModel product,
        List<ParfaitBundleComponent> components)
    {
        var retained = product.InventoryBySize.ToDictionary(size => size.Id, size => size.Size, StringComparer.Ordinal);
        EnsureInventoryCanBeRemoved(businessId, existing.InventoryItems
            .Where(row => !retained.TryGetValue(row.ExternalInventoryKey, out var size) ||
                size != row.Size || components.Count > 0 || product.BillingKind != ClientSubscriptionScope.OneTimeProduct)
            .Select(row => row.Id));

        var reserved = ReadReservedInventory(_db, businessId);
        foreach (var size in product.InventoryBySize)
        {
            var previous = existing.InventoryItems.SingleOrDefault(row => row.ExternalInventoryKey == size.Id);
            if (components.Count > 0)
                size.StockQuantity = 0;
            else if (previous is not null && size.OriginalStockQuantity is int original)
            {
                if (original == size.StockQuantity)
                    size.StockQuantity = previous.StockQuantity;
                else if (original != previous.StockQuantity)
                    throw new StoreInventoryException("Stock changed while you were editing. Refresh before changing the quantity.");
            }
            if (previous is not null && size.StockQuantity < reserved.GetValueOrDefault(previous.Id))
                throw new StoreInventoryException("Stock cannot be reduced below units held by pending checkouts.");
        }
    }

    private void EnsureInventoryCanBeRemoved(Guid businessId, IEnumerable<Guid> inventoryIds)
    {
        var ids = inventoryIds.ToHashSet();
        if (ids.Count == 0) return;
        var recipes = _db.CommerceProducts.AsNoTracking()
            .Where(product => product.CommerceBusinessId == businessId && product.BundleComponentsJson != null)
            .Select(product => product.BundleComponentsJson).ToList();
        if (recipes.SelectMany(ReadComponents).Any(part => ids.Contains(part.InventoryItemId)))
            throw new StoreInventoryException("Remove this piece from its bundles before deleting, renaming its size, or changing its product type.");
        if (ReadReservedInventory(_db, businessId).Keys.Any(ids.Contains))
            throw new StoreInventoryException("This stock is held by a pending checkout.");
    }

    private static Dictionary<Guid, int> SumRequirements(IEnumerable<ParfaitValidatedCartItem> items) =>
        items.Where(item => item.InventoryRequirementsJson is not null)
            .SelectMany(item => JsonSerializer.Deserialize<ParfaitInventoryRequirement[]>(item.InventoryRequirementsJson!)!
                .Select(part => new ParfaitInventoryRequirement(part.InventoryItemId, checked(part.Units * item.Quantity))))
            .GroupBy(part => part.InventoryItemId).ToDictionary(group => group.Key, group => group.Sum(part => part.Units));

    private static Dictionary<Guid, int> ReadReservedInventory(MasterAppDbContext db, Guid businessId, Guid? excludedOrder = null)
    {
        var pending = db.CommerceOrderLines.AsNoTracking().Where(line =>
            line.CommerceOrder!.CommerceBusinessId == businessId && line.CommerceOrder.PaymentStatus == "Pending" &&
            line.CommerceOrder.IsPaymentProcessing && line.CommerceOrderId != excludedOrder);
        var lines = pending.Where(line => line.InventoryRequirementsJson != null)
            .Select(line => new ParfaitValidatedCartItem
            {
                InventoryRequirementsJson = line.InventoryRequirementsJson, Quantity = line.Quantity
            }).ToList();
        var reserved = SumRequirements(lines);

        // Orders prepared before bundle inventory shipped still hold their original size.
        var legacy = from line in pending
                     where line.InventoryRequirementsJson == null
                     join row in db.CommerceProductInventoryItems.AsNoTracking()
                         on new { Key = line.ProductExternalKey, line.Size }
                         equals new { Key = row.CommerceProduct!.ExternalProductKey, row.Size }
                     where row.CommerceProduct!.CommerceBusinessId == businessId
                     group line by row.Id into inventory
                     select new { Id = inventory.Key, Quantity = inventory.Sum(line => line.Quantity) };
        foreach (var row in legacy)
            reserved[row.Id] = checked(reserved.GetValueOrDefault(row.Id) + row.Quantity);
        return reserved;
    }

    internal static void ValidateInventoryReservation(MasterAppDbContext db, Guid businessId, Guid orderId,
        IReadOnlyList<ParfaitValidatedCartItem> items)
    {
        var demand = SumRequirements(items);
        var reserved = ReadReservedInventory(db, businessId, orderId);
        var ids = demand.Keys.ToArray();
        var stock = db.CommerceProductInventoryItems.AsNoTracking()
            .Where(row => ids.Contains(row.Id) && row.IsEnabled && row.CommerceProduct!.IsActive &&
                row.CommerceProduct.CommerceBusinessId == businessId &&
                row.CommerceProduct.BillingKind == ClientSubscriptionScope.OneTimeProduct &&
                row.CommerceProduct.BundleComponentsJson == null)
            .ToDictionary(row => row.Id, row => row.StockQuantity);
        if (demand.Any(part => stock.GetValueOrDefault(part.Key) - reserved.GetValueOrDefault(part.Key) < part.Value))
            throw new StoreInventoryException("Stock changed. Review your cart before paying.");
    }
}
