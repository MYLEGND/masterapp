using System.Text.Json;
using NuGet.Packaging;

// Internal format adapter for release-package.py. No networking, extraction,
// mutation, credential access, or release admission takes place here.
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    var buffer = new char[1024 * 1024 + 1];
    var length = await Console.In.ReadBlockAsync(buffer.AsMemory(), deadline.Token);
    if (length > 1024 * 1024) throw new InvalidDataException();
    var paths = JsonSerializer.Deserialize<string[]>(buffer.AsSpan(0, length));
    if (paths is null || paths.Length is < 1 or > 1000 || paths.Distinct().Count() != paths.Length)
        throw new InvalidDataException();
    var results = new List<object>();
    foreach (var path in paths)
    {
        deadline.Token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".nupkg", StringComparison.Ordinal))
            throw new InvalidDataException();
        using var reader = new PackageArchiveReader(path);
        var signature = await reader.GetPrimarySignatureAsync(deadline.Token);
        if (signature is not null)
            await reader.ValidateIntegrityAsync(signature.SignatureContent, deadline.Token);
        results.Add(new { index = results.Count, contentHash = reader.GetContentHash(deadline.Token) });
    }
    Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, packages = results }));
    return 0;
}
catch
{
    // Do not echo file paths, archive metadata, or provider payloads.
    Console.Error.WriteLine("PACKAGE_RESTORE_CONTENT_UNPROVEN");
    return 1;
}
