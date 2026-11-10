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
            ? product.BundleComponents.Where(part => part.BundleSize == size.Size && part.Units is >= 1 and <= 99)
                .Select(part => new ParfaitInventoryRequirement(part.InventoryItemId, part.Units)).ToArray()
            : size.InventoryItemId == Guid.Empty ? [] : [new(size.InventoryItemId, 1)];

    private static void ProjectBundleInventory(IReadOnlyList<ParfaitProductEditorViewModel> products,
        Dictionary<Guid, int> reserved, bool availableOnly)
    {
        var available = new Dictionary<Guid, int>();
        var owners = products.Where(product => product.BundleComponents.Count == 0)
            .SelectMany(product => product.InventoryBySize.Where(size => size.InventoryItemId != Guid.Empty)
                .Select(size => (size.InventoryItemId, Product: product, Size: size)))
            .ToDictionary(row => row.InventoryItemId);
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
            var allResolved = product.BundleComponents.All(part => owners.ContainsKey(part.InventoryItemId) && part.Units is >= 1 and <= 99);
            product.LinkedProductIds = product.BundleComponents.Where(part => owners.ContainsKey(part.InventoryItemId))
                .Select(part => owners[part.InventoryItemId].Product.Id).Distinct(StringComparer.Ordinal).ToList();
            if (!allResolved) product.BundleMessages.Add("A linked product is unavailable. Review the links before saving.");
            foreach (var size in product.InventoryBySize)
            {
                var parts = InventoryFor(product, size);
                var complete = allResolved && parts.Count > 0 && parts.Count == product.LinkedProductIds.Count &&
                    parts.Select(part => owners[part.InventoryItemId].Product.Id).Distinct(StringComparer.Ordinal).Count() == parts.Count &&
                    parts.All(part => ParfaitProductCatalogDefaults.NormalizeSize(owners[part.InventoryItemId].Size.Size) == size.Size);
                size.StockQuantity = !complete ? 0 : parts.Min(part => available.GetValueOrDefault(part.InventoryItemId) / part.Units);
                size.AvailableStockQuantity = size.StockQuantity;
                if (!complete) product.BundleMessages.Add($"{size.Size} does not include the same size from every linked product and cannot be purchased.");
            }
        }
    }

    private List<ParfaitBundleComponent> ValidateBundle(Guid businessId, ParfaitProductEditorViewModel product,
        CommerceProduct? existing)
    {
        List<ParfaitBundleComponent> saved;
        try
        {
            saved = ReadComponents(existing?.BundleComponentsJson);
            // Legacy transport may echo a recipe, but may never author inventory mappings.
            if (product.BundleComponentsJson is not null &&
                !ReadComponents(product.BundleComponentsJson).SequenceEqual(saved))
                throw new StoreInventoryException("Bundle mappings are managed by the store. Use Link products to select individual products.");
        }
        catch (JsonException) { throw new StoreInventoryException("The saved bundle links are invalid. Review Link products."); }
        if (product.LinkedProductIdsJson is null)
        {
            if (saved.Count == 0) return saved;
            if (product.BillingKind != ClientSubscriptionScope.OneTimeProduct)
                throw new StoreInventoryException("Only one-time products can link individual products.");
            // An ordinary field edit preserves the saved recipe exactly. Only an explicit
            // selection applies a regenerated recipe; disabled sizes remain safely unavailable.
            var savedIds = saved.Select(part => part.InventoryItemId).Distinct().ToArray();
            var savedRows = _db.CommerceProductInventoryItems.AsNoTracking()
                .Where(row => savedIds.Contains(row.Id) && row.CommerceProduct!.CommerceBusinessId == businessId &&
                    row.CommerceProduct.ExternalProductKey != product.Id && row.CommerceProduct.BundleComponentsJson == null &&
                    row.CommerceProduct.BillingKind == ClientSubscriptionScope.OneTimeProduct)
                .Select(row => new { row.Id, row.Size, Product = row.CommerceProduct!.ExternalProductKey }).ToList();
            var owners = savedRows.ToDictionary(row => row.Id);
            if (savedRows.Count != savedIds.Length || saved.Count > 200 || saved.Any(part => part.Units is < 1 or > 99))
                throw new StoreInventoryException("A saved link is unavailable. Review Link products; no links were removed.");
            var selectedCount = savedRows.Select(row => row.Product).Distinct(StringComparer.Ordinal).Count();
            if (saved.GroupBy(part => ParfaitProductCatalogDefaults.NormalizeSize(part.BundleSize)).Any(group =>
                group.Count() != selectedCount || group.Select(part => owners[part.InventoryItemId].Product).Distinct(StringComparer.Ordinal).Count() != selectedCount ||
                group.Any(part => ParfaitProductCatalogDefaults.NormalizeSize(owners[part.InventoryItemId].Size) != group.Key)))
                throw new StoreInventoryException("A saved bundle size does not include the same size from every product. Review and apply Link products to repair it.");
            product.InventoryBySize = existing!.InventoryItems.Select(MapInventory).ToList();
            return saved;
        }

        List<string>? selected;
        try { selected = JsonSerializer.Deserialize<List<string>>(product.LinkedProductIdsJson); }
        catch (JsonException) { throw new StoreInventoryException("Choose valid products using Link products."); }
        if (selected is null || selected.Count > 200 || selected.Any(string.IsNullOrWhiteSpace) ||
            selected.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Count ||
            selected.Contains(product.Id, StringComparer.OrdinalIgnoreCase))
            throw new StoreInventoryException("Select each individual product once. A product cannot link to itself.");
        if (selected.Count == 0)
        {
            if (saved.Count > 0)
                product.InventoryBySize = existing!.InventoryItems.Select(row => new ParfaitProductSizeInventoryEditorViewModel
                {
                    Id = row.ExternalInventoryKey, Size = row.Size, IsEnabled = row.IsEnabled,
                    StockQuantity = 0, OriginalStockQuantity = 0, LowStockThreshold = row.LowStockThreshold,
                    DisplayOrder = row.DisplayOrder
                }).ToList();
            return [];
        }
        if (product.BillingKind != ClientSubscriptionScope.OneTimeProduct)
            throw new StoreInventoryException("Only one-time products can link individual products.");

        var pieces = _db.CommerceProducts.AsNoTracking().Include(row => row.InventoryItems)
            .Where(row => row.CommerceBusinessId == businessId && selected.Contains(row.ExternalProductKey)).ToList();
        if (pieces.Count != selected.Count || pieces.Any(row => row.BundleComponentsJson != null ||
            row.BillingKind != ClientSubscriptionScope.OneTimeProduct || row.ExternalProductKey == product.Id))
            throw new StoreInventoryException("Choose individual one-time products from this store. Nested bundles are not allowed.");
        var variants = pieces.Select(piece => piece.InventoryItems.Where(row => row.IsEnabled)
            .GroupBy(row => ParfaitProductCatalogDefaults.NormalizeSize(row.Size), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal)).ToArray();
        if (variants.Any(rows => rows.ContainsKey("") || rows.Values.Any(rows => rows.Length != 1)))
            throw new StoreInventoryException("A selected product has ambiguous size labels. Give each size a unique label first.");
        var common = variants[0].Keys.Where(size => variants.All(rows => rows.ContainsKey(size)))
            .OrderBy(size => Array.IndexOf(ParfaitProductCatalogDefaults.StandardSizes, size) is var index && index >= 0 ? index : 100)
            .ThenBy(size => size, StringComparer.Ordinal).ToArray();
        if (common.Length == 0)
            throw new StoreInventoryException("These products have no common enabled size. Enable the same size on every selected product, or change the selection.");
        if (common.Length * pieces.Count > 200)
            throw new StoreInventoryException("This selection exceeds 200 size links. Select fewer products or sizes.");
        if (saved.Any(part => part.Units is < 1 or > 99) ||
            saved.GroupBy(part => (ParfaitProductCatalogDefaults.NormalizeSize(part.BundleSize), part.InventoryItemId)).Any(group => group.Count() > 1))
            throw new StoreInventoryException("The saved quantities need review before these links can be changed.");
        var quantities = saved.ToDictionary(part => (ParfaitProductCatalogDefaults.NormalizeSize(part.BundleSize), part.InventoryItemId), part => part.Units);
        var result = common.SelectMany(size => variants.Select(rows => rows[size][0])
            .Select(row => new ParfaitBundleComponent(size, row.Id, quantities.GetValueOrDefault((size, row.Id), 1)))).ToList();
        // Disregard client size IDs, mappings and stock. Keep stable saved bundle size identities.
        product.InventoryBySize = common.Select((size, index) =>
        {
            var previous = existing?.InventoryItems.SingleOrDefault(row => ParfaitProductCatalogDefaults.NormalizeSize(row.Size) == size);
            return new ParfaitProductSizeInventoryEditorViewModel
            {
                Id = previous?.ExternalInventoryKey ?? Guid.NewGuid().ToString("N"),
                Size = size, IsEnabled = true, StockQuantity = 0,
                LowStockThreshold = previous?.LowStockThreshold ?? 20, DisplayOrder = (index + 1) * 10
            };
        }).ToList();
        return result;
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
            if (previous is not null && _db.Database.IsRelational()) _db.Entry(previous).Reload();
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
        // Pre-snapshot orders retain their saved loose-product/size identity.
        // Resolve that original demand before reacquiring a released legacy hold.
        foreach (var item in items.Where(item => item.InventoryRequirementsJson is null))
        {
            var variant = db.CommerceProductInventoryItems.AsNoTracking()
                .Where(row => row.CommerceProduct!.CommerceBusinessId == businessId
                    && row.CommerceProduct.ExternalProductKey == item.Id && row.Size == item.Size)
                .Select(row => (Guid?)row.Id).SingleOrDefault();
            if (variant is null)
                throw new StoreInventoryException("The saved item is no longer available. Review your cart before paying.");
            demand[variant.Value] = checked(demand.GetValueOrDefault(variant.Value) + item.Quantity);
        }
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
