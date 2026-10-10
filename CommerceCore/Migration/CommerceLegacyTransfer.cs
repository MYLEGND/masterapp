using System.Security.Cryptography;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Services;

namespace Legend.Commerce;

/// <summary>
/// Explicit migration utility for existing commerce files. No writes occur in
/// PlanOnly mode. CopyNoOverwrite must be separately authorized by the caller.
/// Neither mode changes the SQL database, live URLs, or deletes source files.
/// </summary>
public sealed class CommerceLegacyTransfer(MasterAppDbContext db)
{
    private const string ImagePrefix = "/uploads/parfait-products/";
    private const long MaximumFileBytes = 50_000_000;

    public async Task<CommerceTransferReport> ReconcileAsync(
        Guid businessId, string businessKey, ParfaitStoragePaths legacy,
        ParfaitStoragePaths canonical, CommerceTransferMode mode,
        CancellationToken ct = default)
    {
        if (businessId == Guid.Empty || string.IsNullOrWhiteSpace(businessKey))
            throw new ArgumentException("Exact commerce business identity required.");
        if (!await db.CommerceBusinesses.AsNoTracking().AnyAsync(x =>
            x.Id == businessId && x.Key == businessKey && x.IsActive, ct))
            throw new InvalidOperationException("Commerce business not found.");

        // Avoid accidentally treating a shared physical folder as a migration.
        if (string.Equals(Path.GetFullPath(legacy.RootPath), Path.GetFullPath(canonical.RootPath),
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source and destination storage roots must differ.");

        var productIds = await db.CommerceProducts.AsNoTracking()
            .Where(x => x.CommerceBusinessId == businessId).Select(x => x.Id).ToListAsync(ct);
        var ids = productIds.ToHashSet();
        var urls = await db.CommerceProductImages.AsNoTracking()
            .Where(x => ids.Contains(x.CommerceProductId)).Select(x => x.ImageUrl)
            .Distinct().ToListAsync(ct);

        var pairs = new List<(string Source, string Destination)>();
        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !url.StartsWith(ImagePrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var relative = url[ImagePrefix.Length..];
            var segments = relative.Split('/');
            if (segments.Length < 2 || segments.Any(x => x.Length is < 1 or > 255 ||
                x is "." or ".." || x.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')))
                throw new InvalidOperationException("Unsafe commerce media path in stored catalog.");

            var sourcePath = legacy.ResolveImagePhysicalPaths(url).FirstOrDefault(File.Exists)
                ?? throw new InvalidOperationException("A catalog image is missing from legacy storage.");
            var targetPath = Constrain(canonical.UploadRoot, Path.Combine(segments));
            pairs.Add((sourcePath, targetPath));
        }

        var automation = legacy.GetCustomerAutomationsPath(businessKey);
        if (File.Exists(automation))
            pairs.Add((automation, canonical.GetCustomerAutomationsPath(businessKey)));

        if (string.Equals(businessKey, "parfait", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(legacy.TeamAccessPath))
            pairs.Add((legacy.TeamAccessPath, canonical.TeamAccessPath));

        // Perform complete preflight *before* any write. A conflict aborts the
        // entire operation without changing target contents or source files.
        var preflight = new List<(string Source, string Destination, string Hash, long Length, bool Existing)>();
        foreach (var (sourcePath, destinationPath) in pairs)
        {
            ct.ThrowIfCancellationRequested();
            var sourceBytes = await ReadBoundedAsync(sourcePath, ct);
            var hash = Sha256(sourceBytes);
            var existing = File.Exists(destinationPath);
            if (existing && Sha256(await ReadBoundedAsync(destinationPath, ct)) != hash)
                throw new InvalidOperationException("Destination contains different commerce data. No overwrite authorized.");
            preflight.Add((sourcePath, destinationPath, hash, sourceBytes.LongLength, existing));
        }

        if (mode == CommerceTransferMode.CopyNoOverwrite)
        {
            foreach (var item in preflight.Where(x => !x.Existing))
            {
                ct.ThrowIfCancellationRequested();
                var bytes = await ReadBoundedAsync(item.Source, ct);
                if (Sha256(bytes) != item.Hash)
                    throw new InvalidOperationException("Source changed during migration; refusing copy.");
                var folder = Path.GetDirectoryName(item.Destination)!;
                Directory.CreateDirectory(folder);
                var staging = Path.Combine(folder, ".commerce-migration-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    await using (var output = new FileStream(staging, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
                    {
                        await output.WriteAsync(bytes, ct);
                        await output.FlushAsync(ct);
                        output.Flush(flushToDisk: true);
                    }
                    try { File.Move(staging, item.Destination, overwrite: false); }
                    catch (IOException) when (File.Exists(item.Destination))
                    {
                        if (Sha256(await ReadBoundedAsync(item.Destination, ct)) != item.Hash)
                            throw new InvalidOperationException("Concurrent destination conflict.");
                    }
                    if (Sha256(await ReadBoundedAsync(item.Destination, ct)) != item.Hash)
                        throw new InvalidOperationException("Destination failed readback hash verification.");
                }
                finally { if (File.Exists(staging)) File.Delete(staging); }
            }
        }

        // A completed copy is not trusted until every source and destination
        // is reread after all writes. A changed source, even after its own copy,
        // must stop the cutover instead of silently blessing a stale replica.
        var verified = 0;
        foreach (var item in preflight)
        {
            ct.ThrowIfCancellationRequested();
            if (Sha256(await ReadBoundedAsync(item.Source, ct)) != item.Hash)
                throw new InvalidOperationException("Commerce source changed before final migration verification.");
            if (File.Exists(item.Destination))
            {
                if (Sha256(await ReadBoundedAsync(item.Destination, ct)) != item.Hash)
                    throw new InvalidOperationException("Commerce destination failed final migration verification.");
                verified++;
            }
            else if (mode == CommerceTransferMode.CopyNoOverwrite)
            {
                throw new InvalidOperationException("Commerce destination missing after copy.");
            }
        }

        // The digest deliberately excludes raw customer JSON, media bytes,
        // email, physical paths, and provider/payment identities.
        var manifest = Sha256(System.Text.Encoding.UTF8.GetBytes(
            string.Join("\\n", preflight.Select(x => x.Hash + ":" +
                x.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .OrderBy(x => x, StringComparer.Ordinal))));
        return new CommerceTransferReport(
            businessId, preflight.Count,
            preflight.Count(x => x.Existing),
            preflight.Count(x => !x.Existing),
            preflight.Sum(x => x.Length),
            mode == CommerceTransferMode.CopyNoOverwrite,
            verified,
            manifest);
    }

    private static string Constrain(string root, string relative)
    {
        var baseRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(baseRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Commerce path escaped its authorized storage root.");
        return path;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (info.Length < 0 || info.Length > MaximumFileBytes)
            throw new InvalidOperationException("Commerce file exceeds migration file limit.");
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Commerce migration disallows linked files.");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        info.Refresh();
        if (bytes.LongLength != info.Length || bytes.LongLength > MaximumFileBytes)
            throw new InvalidOperationException("Commerce file changed during migration.");
        return bytes;
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public enum CommerceTransferMode { PlanOnly, CopyNoOverwrite }

public sealed record CommerceTransferReport(
    Guid BusinessId, int Files, int ExistingIdenticalFiles,
    int FilesToCopy, long Bytes, bool CopyAttempted,
    int VerifiedDestinationFiles, string ManifestSha256);
