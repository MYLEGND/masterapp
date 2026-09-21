using System.Security.Cryptography;
using Domain.Entities;
using Domain.Social;
using Infrastructure.Data;
using Infrastructure.Security.UploadValidation;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.WebsiteEditing;

/// <summary>Website ownership metadata over the existing shared media transport and validator.</summary>
public sealed class WebsiteMediaService(MasterAppDbContext db, ISocialMediaStorage storage)
{
    public Task<WebsiteMediaAsset> StoreImageAsync(string ownerKey, string sourceUrl, byte[] bytes, CancellationToken ct = default)
    {
        var validation = UploadValidator.ValidateImageContent(bytes, UploadValidationPolicy.Images(5_000_000));
        if (!validation.IsValid) throw new ArgumentException(validation.ErrorMessage ?? "Unsupported image.");
        var extension = validation.DetectedContentType switch { "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
        return StoreAsync(ownerKey, sourceUrl, "image" + extension, bytes, ct);
    }

    public async Task<WebsiteMediaAsset> StoreAsync(string ownerKey, string sourceUrl, string fileName, byte[] bytes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerKey) || ownerKey.Length > 200) throw new ArgumentException("Website owner required.");
        var validation = UploadValidator.ValidateContent(bytes, fileName, null, new UploadValidationPolicy
        {
            MaxSizeBytes = 25_000_000,
            AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".mp4", ".webm" },
            AllowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/webp", "video/mp4", "video/webm" }
        });
        if (!validation.IsValid) throw new ArgumentException(validation.ErrorMessage ?? "Unsupported media.");
        if (validation.DetectedContentType!.StartsWith("image/", StringComparison.Ordinal) && bytes.Length > 5_000_000) throw new ArgumentException("Image exceeds 5 MB.");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var existing = await db.Set<WebsiteMediaAsset>().SingleOrDefaultAsync(x => x.OwnerKey == ownerKey && x.Sha256 == hash, ct);
        if (existing is not null) return existing;
        var extension = validation.DetectedContentType switch { "image/png" => ".png", "image/webp" => ".webp", "video/mp4" => ".mp4", "video/webm" => ".webm", _ => ".jpg" };
        var asset = new WebsiteMediaAsset { OwnerKey = ownerKey, SourceUrl = sourceUrl, Sha256 = hash, ContentType = validation.DetectedContentType!, SizeBytes = bytes.Length };
        using var content = new MemoryStream(bytes, writable: false);
        var stored = await storage.StoreAsync(asset.Id, hash + extension, bytes.Length, content, ct);
        if (!stored.Succeeded || stored.Media is null) throw new InvalidOperationException("Website image storage is unavailable.");
        asset.StorageKey = stored.Media.StorageKey;
        db.Add(asset);
        try { await db.SaveChangesAsync(ct); }
        catch
        {
            db.Entry(asset).State = EntityState.Detached;
            // Provider transport has no cancellation-independent continuation.
            await storage.DeleteAsync(asset.StorageKey, ct);
            throw;
        }
        return asset;
    }

    public async Task<WebsiteMediaUsage> UsageAsync(string ownerKey, CancellationToken ct = default)
    {
        var rows = db.Set<WebsiteMediaAsset>().AsNoTracking().Where(x => x.OwnerKey == ownerKey);
        return new WebsiteMediaUsage(await rows.CountAsync(ct), await rows.SumAsync(x => (long?)x.SizeBytes, ct) ?? 0, 5_000_000, 25_000_000);
    }

    public async Task<(WebsiteMediaAsset Asset, Stream Content)?> OpenAsync(string ownerKey, Guid id, CancellationToken ct = default)
    {
        var asset = await db.Set<WebsiteMediaAsset>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OwnerKey == ownerKey, ct);
        if (asset is null) return null;
        var result = await storage.OpenReadAsync(asset.StorageKey, ct);
        return result.Status == SocialMediaReadStatus.Available && result.Content is not null ? (asset, result.Content) : null;
    }
}

public sealed record WebsiteMediaUsage(int AssetCount, long StoredBytes, long MaximumImageBytes, long MaximumVideoBytes);
