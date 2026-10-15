using System.Security.Cryptography;
using Domain.Entities;
using Domain.Social;
using Infrastructure.Data;
using Infrastructure.Security.UploadValidation;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.WebsiteEditing;

public sealed record WebsiteMediaVisualMetadata(
    int? WidthPx,
    int? HeightPx,
    decimal? AspectRatio,
    string? Orientation);

public static class WebsiteMediaVisualMetadataInspector
{
    public const int MaximumProbeBytes = 262_144;

    public static async Task<WebsiteMediaVisualMetadata> InspectAsync(
        Stream stream,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[MaximumProbeBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken);
            if (read <= 0) break;
            length += read;
        }

        var span = buffer.AsSpan(0, length);
        var dimensions = TryPng(span) ?? TryGif(span) ?? TryJpeg(span) ?? TryWebP(span) ??
            (contentType is "image/avif" or "image/heic" or "image/heif" ? TryIspe(span) : null);
        if (dimensions is null) return new(null, null, null, null);

        var (width, height) = dimensions.Value;
        if (width <= 0 || height <= 0) return new(null, null, null, null);
        var ratio = Math.Round((decimal)width / height, 4);
        var orientation = Math.Abs(width - height) <= Math.Max(width, height) * 0.03
            ? "square"
            : width > height ? "landscape" : "portrait";
        return new(width, height, ratio, orientation);
    }

    private static (int Width, int Height)? TryPng(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24 ||
            data[0] != 0x89 || data[1] != 0x50 || data[2] != 0x4e || data[3] != 0x47)
            return null;
        return (ReadInt32BigEndian(data.Slice(16, 4)), ReadInt32BigEndian(data.Slice(20, 4)));
    }

    private static (int Width, int Height)? TryGif(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10 ||
            data[0] != (byte)'G' || data[1] != (byte)'I' || data[2] != (byte)'F')
            return null;
        return (data[6] | data[7] << 8, data[8] | data[9] << 8);
    }

    private static (int Width, int Height)? TryJpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || data[0] != 0xff || data[1] != 0xd8) return null;
        var index = 2;
        while (index + 8 < data.Length)
        {
            while (index < data.Length && data[index] != 0xff) index++;
            while (index < data.Length && data[index] == 0xff) index++;
            if (index >= data.Length) break;
            var marker = data[index++];
            if (marker is 0xd8 or 0xd9) continue;
            if (index + 1 >= data.Length) break;
            var segmentLength = data[index] << 8 | data[index + 1];
            if (segmentLength < 2 || index + segmentLength > data.Length) break;
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or
                0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                if (index + 7 >= data.Length) break;
                var height = data[index + 3] << 8 | data[index + 4];
                var width = data[index + 5] << 8 | data[index + 6];
                return (width, height);
            }
            index += segmentLength;
        }
        return null;
    }

    private static (int Width, int Height)? TryWebP(ReadOnlySpan<byte> data)
    {
        if (data.Length < 30 ||
            data[0] != (byte)'R' || data[1] != (byte)'I' || data[2] != (byte)'F' || data[3] != (byte)'F' ||
            data[8] != (byte)'W' || data[9] != (byte)'E' || data[10] != (byte)'B' || data[11] != (byte)'P')
            return null;

        var chunk = System.Text.Encoding.ASCII.GetString(data.Slice(12, 4));
        if (chunk == "VP8X")
        {
            var width = 1 + data[24] + (data[25] << 8) + (data[26] << 16);
            var height = 1 + data[27] + (data[28] << 8) + (data[29] << 16);
            return (width, height);
        }
        if (chunk == "VP8L" && data.Length >= 25 && data[20] == 0x2f)
        {
            var width = 1 + data[21] + ((data[22] & 0x3f) << 8);
            var height = 1 + ((data[22] & 0xc0) >> 6) + (data[23] << 2) + ((data[24] & 0x0f) << 10);
            return (width, height);
        }
        if (chunk == "VP8 ")
        {
            for (var i = 20; i + 7 < data.Length; i++)
            {
                if (data[i] != 0x9d || data[i + 1] != 0x01 || data[i + 2] != 0x2a) continue;
                var width = (data[i + 3] | data[i + 4] << 8) & 0x3fff;
                var height = (data[i + 5] | data[i + 6] << 8) & 0x3fff;
                return (width, height);
            }
        }
        return null;
    }

    private static (int Width, int Height)? TryIspe(ReadOnlySpan<byte> data)
    {
        for (var index = 4; index + 16 <= data.Length; index++)
        {
            if (data[index] != (byte)'i' || data[index + 1] != (byte)'s' ||
                data[index + 2] != (byte)'p' || data[index + 3] != (byte)'e')
                continue;
            var width = ReadInt32BigEndian(data.Slice(index + 8, 4));
            var height = ReadInt32BigEndian(data.Slice(index + 12, 4));
            if (width > 0 && height > 0) return (width, height);
        }
        return null;
    }

    private static int ReadInt32BigEndian(ReadOnlySpan<byte> value) =>
        value[0] << 24 | value[1] << 16 | value[2] << 8 | value[3];
}

/// <summary>Website ownership metadata over the existing shared media transport and validator.</summary>
public sealed class WebsiteMediaService(MasterAppDbContext db, ISocialMediaStorage storage)
{
    public Task<WebsiteMediaAsset> StoreImageAsync(string ownerKey, string sourceUrl, byte[] bytes, CancellationToken ct = default)
    {
        var validation = UploadValidator.ValidateImageContent(bytes, UploadValidationPolicy.Images(5_000_000));
        if (!validation.IsValid) throw new ArgumentException(validation.ErrorMessage ?? "Unsupported image.");
        var extension = UploadValidator.CanonicalExtensionForContentType(validation.DetectedContentType)
            ?? throw new ArgumentException("The uploaded image container is not recognized.");
        return StoreAsync(ownerKey, sourceUrl, "image" + extension, bytes, ct);
    }

    public async Task<WebsiteMediaAsset> StoreAsync(string ownerKey, string sourceUrl, string fileName, byte[] bytes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerKey) || ownerKey.Length > 200) throw new ArgumentException("Website owner required.");
        var validation = UploadValidator.ValidateContent(
            bytes,
            fileName,
            null,
            UploadValidationPolicy.Media(25_000_000));
        if (!validation.IsValid) throw new ArgumentException(validation.ErrorMessage ?? "Unsupported media.");
        if (validation.DetectedContentType!.StartsWith("image/", StringComparison.Ordinal) && bytes.Length > 5_000_000) throw new ArgumentException("Image exceeds 5 MB.");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var existing = await db.Set<WebsiteMediaAsset>().SingleOrDefaultAsync(x => x.OwnerKey == ownerKey && x.Sha256 == hash, ct);
        if (existing is not null) return existing;
        var extension = UploadValidator.CanonicalExtensionForContentType(validation.DetectedContentType)
            ?? throw new ArgumentException("The uploaded media container is not recognized.");
        var asset = new WebsiteMediaAsset { OwnerKey = ownerKey, SourceUrl = sourceUrl, Sha256 = hash, ContentType = validation.DetectedContentType!, SizeBytes = bytes.Length };
        using var content = new MemoryStream(bytes, writable: false);
        var stored = await storage.StoreAsync(asset.Id, hash + extension, bytes.Length, content, ct);
        if (!stored.Succeeded || stored.Media is null) throw new InvalidOperationException("Website media storage is unavailable.");
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

    public async Task<WebsiteMediaVisualMetadata> InspectVisualMetadataAsync(
        WebsiteMediaAsset asset,
        CancellationToken ct = default)
    {
        if (!asset.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return new(null, null, null, null);
        var result = await storage.OpenReadAsync(asset.StorageKey, ct);
        if (result.Status != SocialMediaReadStatus.Available || result.Content is null)
            return new(null, null, null, null);
        await using var stream = result.Content;
        return await WebsiteMediaVisualMetadataInspector.InspectAsync(stream, asset.ContentType, ct);
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
