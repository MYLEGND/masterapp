using Microsoft.Extensions.FileProviders;

namespace ParfaitApp.Services;

public sealed class ParfaitStoragePaths
{
    private const string BusinessProfileFileName = "parfait-business-profile.json";
    private const string TeamAccessFileName = "parfait-team-access.json";
    private const string CustomerAutomationsFileName = "parfait-customer-automations.json";

    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly object _lock = new();
    private bool _initialized;

    public ParfaitStoragePaths(IWebHostEnvironment environment, IConfiguration configuration)
    {
        _environment = environment;
        _configuration = configuration;
    }

    public string RootPath => ResolveRootPath();
    public string DataRoot => Path.Combine(RootPath, "data");
    public string UploadRoot => Path.Combine(RootPath, "uploads", "parfait-products");

    public string LegacyDataRoot => Path.Combine(_environment.ContentRootPath, "App_Data");
    public string LegacyUploadRoot => Path.Combine(_environment.WebRootPath, "uploads", "parfait-products");

    public string BusinessProfilePath => Path.Combine(DataRoot, BusinessProfileFileName);
    public string TeamAccessPath => Path.Combine(DataRoot, TeamAccessFileName);
    public string CustomerAutomationsPath => Path.Combine(DataRoot, CustomerAutomationsFileName);

    public IFileProvider BuildUploadFileProvider()
    {
        EnsureInitialized();

        return new CompositeFileProvider(
            new PhysicalFileProvider(UploadRoot),
            new PhysicalFileProvider(LegacyUploadRoot));
    }

    public void EnsureInitialized()
    {
        lock (_lock)
        {
            if (_initialized)
                return;

            Directory.CreateDirectory(DataRoot);
            Directory.CreateDirectory(UploadRoot);
            Directory.CreateDirectory(LegacyDataRoot);
            Directory.CreateDirectory(LegacyUploadRoot);

            MigrateFileIfMissing(Path.Combine(LegacyDataRoot, BusinessProfileFileName), BusinessProfilePath);
            MigrateFileIfMissing(Path.Combine(LegacyDataRoot, TeamAccessFileName), TeamAccessPath);
            MigrateFileIfMissing(Path.Combine(LegacyDataRoot, CustomerAutomationsFileName), CustomerAutomationsPath);
            MigrateUploads();

            _initialized = true;
        }
    }

    public string GetImageUrl(string productId, string fileName)
        => $"/uploads/parfait-products/{productId}/{fileName}";

    public string GetUploadDirectory(string productId)
        => Path.Combine(UploadRoot, productId);

    public IEnumerable<string> ResolveImagePhysicalPaths(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            yield break;

        const string prefix = "/uploads/parfait-products/";
        var normalized = imageUrl.Replace('\\', '/');
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            yield break;

        var relative = normalized[prefix.Length..].TrimStart('/');
        if (string.IsNullOrWhiteSpace(relative))
            yield break;

        var segments = relative
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        if (segments.Length == 0)
            yield break;

        yield return Path.Combine(UploadRoot, Path.Combine(segments));
        yield return Path.Combine(LegacyUploadRoot, Path.Combine(segments));
    }

    private string ResolveRootPath()
    {
        var configured = _configuration["Parfait:StorageRoot"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured.Trim());

        var explicitRoot = Environment.GetEnvironmentVariable("PARFAIT_STORAGE_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return Path.GetFullPath(explicitRoot.Trim());

        var home = Environment.GetEnvironmentVariable("HOME");
        var siteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
        if (!string.IsNullOrWhiteSpace(siteName) && !string.IsNullOrWhiteSpace(home))
            return Path.Combine(home.Trim(), "data", "parfaitapp");

        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData))
            return Path.Combine(localAppData.Trim(), "MasterApp", "ParfaitApp");

        if (!string.IsNullOrWhiteSpace(home))
            return Path.Combine(home.Trim(), ".masterapp", "parfaitapp");

        return Path.Combine(_environment.ContentRootPath, ".parfait-storage");
    }

    private static void MigrateFileIfMissing(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath) || File.Exists(destinationPath))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite: false);
    }

    private void MigrateUploads()
    {
        if (!Directory.Exists(LegacyUploadRoot))
            return;

        foreach (var sourcePath in Directory.EnumerateFiles(LegacyUploadRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(LegacyUploadRoot, sourcePath);
            if (string.IsNullOrWhiteSpace(relative))
                continue;

            var destinationPath = Path.Combine(UploadRoot, relative);
            if (File.Exists(destinationPath))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }
    }
}
