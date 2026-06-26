using System.Text.Json;
using ParfaitApp.Models;

namespace ParfaitApp.Services;

public interface IParfaitBusinessProfileService
{
    Task<ParfaitBusinessProfileViewModel> GetProfileAsync(CancellationToken ct = default);
    Task SaveProfileAsync(ParfaitBusinessProfileViewModel model, CancellationToken ct = default);
    Task<ParfaitMetaAdsConnectionStatusDto> GetMetaConnectionStatusAsync(CancellationToken ct = default);
    Task SaveMetaConnectionAsync(ParfaitMetaAdsConnectionRecord record, CancellationToken ct = default);
    Task DisconnectMetaAsync(CancellationToken ct = default);
}

public sealed class ParfaitBusinessProfileService : IParfaitBusinessProfileService
{
    private readonly IWebHostEnvironment _environment;
    private readonly ParfaitMetaCapiCredentialProtector _metaCredentialProtector;
    private readonly object _lock = new();

    public ParfaitBusinessProfileService(
        IWebHostEnvironment environment,
        ParfaitMetaCapiCredentialProtector metaCredentialProtector)
    {
        _environment = environment;
        _metaCredentialProtector = metaCredentialProtector;
    }

    private string DataPath => Path.Combine(_environment.ContentRootPath, "App_Data", "parfait-business-profile.json");

    public Task<ParfaitBusinessProfileViewModel> GetProfileAsync(CancellationToken ct = default)
    {
        var store = LoadStore();
        var connection = BuildConnectionStatus(store);

        return Task.FromResult(new ParfaitBusinessProfileViewModel
        {
            StoreName = store.StoreName,
            BusinessType = store.BusinessType,
            GlobalStoreCheckoutUrl = store.GlobalStoreCheckoutUrl,
            MetaPixelId = store.MetaPixelId,
            MetaTestEventCode = store.MetaTestEventCode,
            HasSecureMetaCapiAccessToken = !string.IsNullOrWhiteSpace(store.MetaCapiAccessTokenCiphertext),
            HasActiveMetaAdsConnection = connection.Connected,
            MetaConnectionLabel = connection.Connected
                ? FormatConnectionLabel(connection)
                : connection.Message ?? "Meta Ads not connected for Parfait."
        });
    }

    public Task SaveProfileAsync(ParfaitBusinessProfileViewModel model, CancellationToken ct = default)
    {
        var store = LoadStore();

        store.StoreName = CleanRequired(model.StoreName, "Parfait");
        store.BusinessType = CleanRequired(model.BusinessType, "Apparel / Ecommerce");
        store.GlobalStoreCheckoutUrl = CleanOptional(model.GlobalStoreCheckoutUrl);
        store.MetaPixelId = CleanOptional(model.MetaPixelId);
        store.MetaTestEventCode = CleanOptional(model.MetaTestEventCode);
        store.UpdatedUtc = DateTime.UtcNow;

        SaveStore(store);
        return Task.CompletedTask;
    }

    public Task<ParfaitMetaAdsConnectionStatusDto> GetMetaConnectionStatusAsync(CancellationToken ct = default)
    {
        return Task.FromResult(BuildConnectionStatus(LoadStore()));
    }

    public Task SaveMetaConnectionAsync(ParfaitMetaAdsConnectionRecord record, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(record.AccessToken))
            return Task.CompletedTask;

        var store = LoadStore();

        store.MetaCapiAccessTokenCiphertext = _metaCredentialProtector.Protect(record.AccessToken);
        store.MetaConnectedUtc = record.ConnectedUtc == default ? DateTime.UtcNow : record.ConnectedUtc;
        store.MetaAccessTokenExpiresUtc = record.AccessTokenExpiresUtc;
        store.MetaAccountId = CleanOptional(record.AccountId);
        store.MetaAccountName = CleanOptional(record.AccountName);
        store.MetaBusinessId = CleanOptional(record.BusinessId);
        store.MetaBusinessName = CleanOptional(record.BusinessName);
        store.MetaUserId = CleanOptional(record.MetaUserId);
        store.MetaUserName = CleanOptional(record.MetaUserName);
        store.UpdatedUtc = DateTime.UtcNow;

        SaveStore(store);
        return Task.CompletedTask;
    }

    public Task DisconnectMetaAsync(CancellationToken ct = default)
    {
        var store = LoadStore();

        store.MetaConnectedUtc = null;
        store.MetaAccessTokenExpiresUtc = null;
        store.MetaAccountId = null;
        store.MetaAccountName = null;
        store.MetaBusinessId = null;
        store.MetaBusinessName = null;
        store.MetaUserId = null;
        store.MetaUserName = null;
        store.UpdatedUtc = DateTime.UtcNow;

        SaveStore(store);
        return Task.CompletedTask;
    }

    private ParfaitMetaAdsConnectionStatusDto BuildConnectionStatus(ParfaitBusinessProfileStore store)
    {
        if (!HasActiveConnection(store))
        {
            return new ParfaitMetaAdsConnectionStatusDto
            {
                Connected = false,
                Message = "Meta Ads not connected for Parfait."
            };
        }

        return new ParfaitMetaAdsConnectionStatusDto
        {
            Connected = true,
            AccountId = store.MetaAccountId,
            AccountName = store.MetaAccountName,
            BusinessId = store.MetaBusinessId,
            BusinessName = store.MetaBusinessName,
            MetaUserName = store.MetaUserName,
            ConnectedUtc = store.MetaConnectedUtc,
            AccessTokenExpiresUtc = store.MetaAccessTokenExpiresUtc
        };
    }

    private static bool HasActiveConnection(ParfaitBusinessProfileStore store)
    {
        return store.MetaConnectedUtc.HasValue ||
               !string.IsNullOrWhiteSpace(store.MetaAccountId) ||
               !string.IsNullOrWhiteSpace(store.MetaAccountName) ||
               !string.IsNullOrWhiteSpace(store.MetaUserName);
    }

    private static string FormatConnectionLabel(ParfaitMetaAdsConnectionStatusDto status)
    {
        var account = status.AccountName ?? status.AccountId ?? "Meta account connected";
        var user = string.IsNullOrWhiteSpace(status.MetaUserName) ? string.Empty : $" as {status.MetaUserName}";
        var expiry = status.AccessTokenExpiresUtc.HasValue
            ? $" · expires {status.AccessTokenExpiresUtc.Value.ToLocalTime():MMM d, yyyy h:mm tt}"
            : string.Empty;

        return $"Connected: {account}{user}{expiry}";
    }

    private ParfaitBusinessProfileStore LoadStore()
    {
        EnsureDataFile();

        lock (_lock)
        {
            var json = File.ReadAllText(DataPath);
            return JsonSerializer.Deserialize<ParfaitBusinessProfileStore>(json) ?? CreateDefaultStore();
        }
    }

    private void SaveStore(ParfaitBusinessProfileStore store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);

        lock (_lock)
        {
            File.WriteAllText(
                DataPath,
                JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private void EnsureDataFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);

        if (File.Exists(DataPath))
            return;

        SaveStore(CreateDefaultStore());
    }

    private static ParfaitBusinessProfileStore CreateDefaultStore()
    {
        return new ParfaitBusinessProfileStore
        {
            StoreName = "Parfait",
            BusinessType = "Apparel / Ecommerce"
        };
    }

    private static string CleanRequired(string? value, string fallback)
    {
        var cleaned = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private static string? CleanOptional(string? value)
    {
        var cleaned = value?.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private sealed class ParfaitBusinessProfileStore
    {
        public string StoreName { get; set; } = "Parfait";
        public string BusinessType { get; set; } = "Apparel / Ecommerce";
        public string? GlobalStoreCheckoutUrl { get; set; }
        public string? MetaPixelId { get; set; }
        public string? MetaTestEventCode { get; set; }
        public string? MetaCapiAccessTokenCiphertext { get; set; }
        public DateTime? MetaConnectedUtc { get; set; }
        public DateTime? MetaAccessTokenExpiresUtc { get; set; }
        public string? MetaAccountId { get; set; }
        public string? MetaAccountName { get; set; }
        public string? MetaBusinessId { get; set; }
        public string? MetaBusinessName { get; set; }
        public string? MetaUserId { get; set; }
        public string? MetaUserName { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}
