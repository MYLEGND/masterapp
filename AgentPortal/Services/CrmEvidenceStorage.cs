using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs.Models;

namespace AgentPortal.Services;

/// <summary>Immutable versions in the existing protected document store. No public URLs or ordinary delete API.</summary>
public sealed class CrmEvidenceStorage(IConfiguration configuration, IWebHostEnvironment environment)
{
    private string Key(string relationship, string hash) => "crm-evidence/" +
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(relationship))).ToLowerInvariant() + "/" + hash + ".pdf";

    public async Task PutAsync(string relationship, string hash, byte[] content, CancellationToken ct)
    {
        var key = Key(relationship, hash);
        var container = AgentDocumentStorageLocation.BuildBlobContainerClient(configuration);
        if (container is not null)
        {
            if ((await container.GetPropertiesAsync(cancellationToken: ct)).Value.PublicAccess != PublicAccessType.None)
                throw new InvalidOperationException("Case evidence requires the existing private document container.");
            try
            {
                await container.GetBlobClient(key).UploadAsync(new BinaryData(content), new BlobUploadOptions {
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    HttpHeaders = new BlobHttpHeaders { ContentType = "application/pdf" } }, ct);
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412)
            {
                var existing = await ReadAsync(relationship, hash, ct);
                if (!content.AsSpan().SequenceEqual(existing)) throw new InvalidOperationException("Existing evidence differs; it was not overwritten.");
            }
            return;
        }
        var path = LocalPath(key); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Atomic rename after flush prevents readers or retries seeing a partial version.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await stream.WriteAsync(content, ct); stream.Flush(flushToDisk: true); }
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path))
            { var existing = await File.ReadAllBytesAsync(path, ct); if (!content.AsSpan().SequenceEqual(existing)) throw new InvalidOperationException("Existing evidence differs; it was not overwritten."); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<byte[]> ReadAsync(string relationship, string hash, CancellationToken ct)
    {
        if (hash.Length != 64 || hash.Any(x => !char.IsAsciiHexDigit(x))) throw new InvalidOperationException("Invalid evidence identity.");
        var key = Key(relationship, hash);
        var container = AgentDocumentStorageLocation.BuildBlobContainerClient(configuration);
        byte[] bytes;
        if (container is not null)
        {
            if ((await container.GetPropertiesAsync(cancellationToken: ct)).Value.PublicAccess != PublicAccessType.None)
                throw new InvalidOperationException("Case evidence requires the existing private document container.");
            bytes = (await container.GetBlobClient(key).DownloadContentAsync(ct)).Value.Content.ToArray();
        }
        else bytes = await File.ReadAllBytesAsync(LocalPath(key), ct);
        if (bytes.Length > 20 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != hash)
            throw new InvalidOperationException("Evidence integrity verification failed.");
        return bytes;
    }

    private string LocalPath(string key)
    {
        if (!environment.IsDevelopment() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LEGEND_AGENT_DOCUMENTS_ROOT")))
            throw new InvalidOperationException("Durable protected document storage is not configured; no evidence was filed.");
        return Path.Combine(AgentDocumentStorageLocation.GetBaseRoot(environment), key.Replace('/', Path.DirectorySeparatorChar));
    }
}
