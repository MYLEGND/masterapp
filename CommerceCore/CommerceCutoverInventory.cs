using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;
using ParfaitApp.Services;

namespace Legend.Commerce;

/// <summary>
/// Read-only inventory of one existing commerce tenant before any host/data
/// cutover. Reports aggregate counts and file digests, never customer records,
/// payment data, access tokens or raw workflow contents.
/// Does not provision a business, initialize directories or write migrations.
/// </summary>
public sealed class CommerceCutoverInventory(MasterAppDbContext db, ParfaitStoragePaths storage)
{
    private const long MaxAutomationFileBytes = 25_000_000;
    private const string LocalImagePrefix = "/uploads/parfait-products/";

    public async Task<CommerceCutoverSnapshot> ReadAsync(Guid businessId, string businessKey, CancellationToken ct = default)
    {
        if (businessId == Guid.Empty || string.IsNullOrWhiteSpace(businessKey))
            throw new ArgumentException("An exact business identity is required.");

        var key = businessKey.Trim();
        var business = await db.CommerceBusinesses.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == businessId && x.Key == key && x.IsActive, ct)
            ?? throw new InvalidOperationException("Active commerce business identity was not found.");

        var products = await db.CommerceProducts.AsNoTracking()
            .Where(x => x.CommerceBusinessId == business.Id)
            .Select(x => x.Id)
            .ToListAsync(ct);
        var productIds = products.ToHashSet();
        var imageRows = await db.CommerceProductImages.AsNoTracking()
            .Where(x => productIds.Contains(x.CommerceProductId))
            .Select(x => new { x.Id, x.ImageUrl })
            .ToListAsync(ct);
        var inventoryCount = await db.CommerceProductInventoryItems.AsNoTracking()
            .CountAsync(x => productIds.Contains(x.CommerceProductId), ct);
        var discountCount = await db.CommerceProductDiscounts.AsNoTracking()
            .CountAsync(x => productIds.Contains(x.CommerceProductId), ct);
        var orders = await db.CommerceOrders.AsNoTracking()
            .Where(x => x.CommerceBusinessId == business.Id)
            .Select(x => x.Id)
            .ToListAsync(ct);
        var orderIds = orders.ToHashSet();
        var linesCount = await db.CommerceOrderLines.AsNoTracking()
            .CountAsync(x => orderIds.Contains(x.CommerceOrderId), ct);

        var missingLocalImages = 0;
        var localImages = 0;
        var mediaHashes = new List<(Guid Id, string Sha256)>();
        foreach (var image in imageRows)
        {
            var url = image.ImageUrl;
            if (string.IsNullOrWhiteSpace(url) ||
                !url.StartsWith(LocalImagePrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            localImages++;
            if (!IsSafeLocalImageUrl(url))
            {
                missingLocalImages++;
                continue;
            }

            var existingFile = storage.ResolveImagePhysicalPaths(url).FirstOrDefault(File.Exists);
            if (existingFile is null)
            {
                missingLocalImages++;
                continue;
            }

            try
            {
                var before = new FileInfo(existingFile);
                await using var content = File.OpenRead(existingFile);
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(content, ct)).ToLowerInvariant();
                var after = new FileInfo(existingFile);
                if (before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc)
                {
                    missingLocalImages++;
                    continue;
                }
                mediaHashes.Add((image.Id, digest));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                missingLocalImages++;
            }
        }
        // Stable across machines and roots: hashes refer to database image IDs and
        // exact original file bytes, not host-dependent filesystem paths.
        var mediaDigest = mediaHashes.Count == 0 ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join("\n", mediaHashes.OrderBy(x => x.Id)
                    .Select(x => x.Id.ToString("N") + ":" + x.Sha256))))).ToLowerInvariant();

        var automation = await ReadAutomationAsync(storage.GetCustomerAutomationsPath(business.Key), ct);
        var legacyTeamPresent = string.Equals(business.Key, "parfait", StringComparison.OrdinalIgnoreCase)
            && File.Exists(storage.TeamAccessPath);

        return new CommerceCutoverSnapshot(
            business.Id, business.Key, products.Count, inventoryCount, discountCount,
            imageRows.Count, localImages, missingLocalImages,
            mediaHashes.Count, mediaDigest,
            orders.Count, linesCount, automation.Exists, automation.Readable,
            automation.Sha256, automation.Workflows, automation.CartLeads,
            automation.Dispatches, legacyTeamPresent);
    }

    private static bool IsSafeLocalImageUrl(string url)
    {
        var relative = url[LocalImagePrefix.Length..];
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('?') || relative.Contains('#'))
            return false;
        var parts = relative.Split('/');
        return parts.Length >= 2 &&
               parts.All(part => part.Length is > 0 and <= 255 &&
                   part is not "." and not ".." &&
                   part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
    }

    private static async Task<AutomationInventory> ReadAutomationAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return new(false, false, null, 0, 0, 0);

        try
        {
            var before = new FileInfo(path);
            if (before.Length > MaxAutomationFileBytes)
                return new(true, false, null, 0, 0, 0);

            var bytes = await File.ReadAllBytesAsync(path, ct);
            var after = new FileInfo(path);
            if (before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc ||
                bytes.LongLength != after.Length)
                return new(true, false, null, 0, 0, 0);

            var data = JsonSerializer.Deserialize<ParfaitAutomationStoreRecord>(bytes,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (data is null || data.Workflows is null || data.CartLeads is null || data.Dispatches is null)
                return new(true, false, null, 0, 0, 0);

            return new(true, true,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                data.Workflows.Count, data.CartLeads.Count, data.Dispatches.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(true, false, null, 0, 0, 0);
        }
    }

    private sealed record AutomationInventory(
        bool Exists, bool Readable, string? Sha256, int Workflows, int CartLeads, int Dispatches);
}

public sealed record CommerceCutoverSnapshot(
    Guid CommerceBusinessId,
    string BusinessKey,
    int Products,
    int InventoryVariants,
    int Discounts,
    int ProductImages,
    int LocalProductImages,
    int MissingLocalProductImages,
    int VerifiedLocalProductImages,
    string? ProductMediaSha256,
    int Orders,
    int OrderLines,
    bool AutomationFileExists,
    bool AutomationFileReadable,
    string? AutomationSha256,
    int AutomationWorkflows,
    int AutomationCartLeads,
    int AutomationDispatches,
    bool LegacyTeamFileExists)
{
    // A clean file inventory is necessary but NOT sufficient for live cutover:
    // payment replay, visual parity, routes, and distributed state need proof.
    public bool RequiresFileMigration => MissingLocalProductImages > 0 ||
        (AutomationFileExists && !AutomationFileReadable) ||
        LegacyTeamFileExists || AutomationFileExists || LocalProductImages > 0;
}
