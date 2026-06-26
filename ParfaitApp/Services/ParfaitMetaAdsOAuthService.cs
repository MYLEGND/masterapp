using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ParfaitApp.Models;

namespace ParfaitApp.Services;

public interface IParfaitMetaAdsOAuthService
{
    string BuildConnectUrl(string? returnUrl, string? explicitRedirectUri = null);
    Task<ParfaitMetaAdsConnectionRecord> CompleteCallbackAsync(string code, string stateToken, CancellationToken ct = default);
}

public sealed class ParfaitMetaAdsOAuthService : IParfaitMetaAdsOAuthService
{
    private const string ProfileKey = "parfait-business-profile";

    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDataProtector _stateProtector;
    private readonly ILogger<ParfaitMetaAdsOAuthService> _logger;

    public ParfaitMetaAdsOAuthService(
        IConfiguration config,
        IHttpClientFactory httpClientFactory,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<ParfaitMetaAdsOAuthService> logger)
    {
        _config = config;
        _httpClientFactory = httpClientFactory;
        _stateProtector = dataProtectionProvider.CreateProtector("Parfait.MetaAds.OAuthState.v1");
        _logger = logger;
    }

    public string BuildConnectUrl(string? returnUrl, string? explicitRedirectUri = null)
    {
        var appId = Required("MetaAds:AppId");
        var apiVersion = (_config["MetaAds:ApiVersion"] ?? "v21.0").Trim();
        if (string.IsNullOrWhiteSpace(apiVersion))
            apiVersion = "v21.0";

        var redirectUri = ResolveRedirectUri(explicitRedirectUri);
        var safeReturnUrl = string.IsNullOrWhiteSpace(returnUrl)
            ? "/internal/settings/business-profile"
            : returnUrl.Trim();

        var statePayload = new OAuthState
        {
            ProfileKey = ProfileKey,
            ReturnUrl = safeReturnUrl,
            IssuedUtc = DateTime.UtcNow,
            Nonce = Guid.NewGuid().ToString("N")
        };

        var stateToken = _stateProtector.Protect(JsonSerializer.Serialize(statePayload));
        var scope = (_config["MetaAds:Scopes"] ?? "ads_read,business_management").Trim();

        var url = new StringBuilder();
        url.Append($"https://www.facebook.com/{apiVersion}/dialog/oauth");
        url.Append($"?client_id={Uri.EscapeDataString(appId)}");
        url.Append($"&redirect_uri={Uri.EscapeDataString(redirectUri)}");
        url.Append($"&state={Uri.EscapeDataString(stateToken)}");
        url.Append($"&scope={Uri.EscapeDataString(scope)}");
        return url.ToString();
    }

    public async Task<ParfaitMetaAdsConnectionRecord> CompleteCallbackAsync(string code, string stateToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Missing Meta OAuth code.");

        var state = ReadState(stateToken);
        if (!string.Equals(state.ProfileKey, ProfileKey, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid Meta OAuth state.");

        var appId = Required("MetaAds:AppId");
        var appSecret = Required("MetaAds:AppSecret");
        var apiVersion = (_config["MetaAds:ApiVersion"] ?? "v21.0").Trim();
        if (string.IsNullOrWhiteSpace(apiVersion))
            apiVersion = "v21.0";

        var redirectUri = ResolveRedirectUri(null);
        var client = _httpClientFactory.CreateClient();

        var shortToken = await ExchangeCodeForTokenAsync(client, apiVersion, appId, appSecret, code, redirectUri, ct);
        var longToken = await ExchangeForLongLivedTokenAsync(client, apiVersion, appId, appSecret, shortToken.AccessToken, ct);
        var me = await FetchCurrentUserAsync(client, apiVersion, longToken.AccessToken, ct);
        var account = await FetchBestAccountAsync(client, apiVersion, longToken.AccessToken, ct);

        return new ParfaitMetaAdsConnectionRecord
        {
            AccessToken = longToken.AccessToken,
            AccessTokenExpiresUtc = longToken.ExpiresUtc,
            AccountId = account.AccountId,
            AccountName = account.AccountName,
            BusinessId = account.BusinessId,
            BusinessName = account.BusinessName,
            MetaUserId = me.UserId,
            MetaUserName = me.UserName,
            ConnectedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
    }

    private OAuthState ReadState(string stateToken)
    {
        if (string.IsNullOrWhiteSpace(stateToken))
            throw new InvalidOperationException("Missing Meta OAuth state.");

        try
        {
            var json = _stateProtector.Unprotect(stateToken);
            var state = JsonSerializer.Deserialize<OAuthState>(json) ?? new OAuthState();
            if (state.IssuedUtc < DateTime.UtcNow.AddMinutes(-20))
                throw new InvalidOperationException("Meta OAuth state expired. Retry connect.");

            return state;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Invalid Meta OAuth state.");
        }
    }

    private async Task<(string AccessToken, DateTime? ExpiresUtc)> ExchangeCodeForTokenAsync(
        HttpClient client,
        string version,
        string appId,
        string appSecret,
        string code,
        string redirectUri,
        CancellationToken ct)
    {
        var url = $"https://graph.facebook.com/{version}/oauth/access_token"
                  + $"?client_id={Uri.EscapeDataString(appId)}"
                  + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
                  + $"&client_secret={Uri.EscapeDataString(appSecret)}"
                  + $"&code={Uri.EscapeDataString(code)}";

        using var res = await client.GetAsync(url, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            _logger.LogWarning("Parfait Meta OAuth code exchange failed. status={Status} body={Body}", (int)res.StatusCode, TrimForLog(body));
            throw new InvalidOperationException("Meta OAuth code exchange failed.");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var err))
            throw new InvalidOperationException($"Meta OAuth error: {err.GetProperty("message").GetString()}");

        var token = doc.RootElement.TryGetProperty("access_token", out var tokEl) ? tokEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Meta OAuth returned no access token.");

        DateTime? expiresUtc = null;
        if (doc.RootElement.TryGetProperty("expires_in", out var exEl))
        {
            var seconds = exEl.ValueKind == JsonValueKind.Number
                ? exEl.GetInt32()
                : int.TryParse(exEl.GetString(), out var parsed)
                    ? parsed
                    : 0;
            if (seconds > 0)
                expiresUtc = DateTime.UtcNow.AddSeconds(seconds);
        }

        return (token, expiresUtc);
    }

    private async Task<(string AccessToken, DateTime? ExpiresUtc)> ExchangeForLongLivedTokenAsync(
        HttpClient client,
        string version,
        string appId,
        string appSecret,
        string shortToken,
        CancellationToken ct)
    {
        var url = $"https://graph.facebook.com/{version}/oauth/access_token"
                  + "?grant_type=fb_exchange_token"
                  + $"&client_id={Uri.EscapeDataString(appId)}"
                  + $"&client_secret={Uri.EscapeDataString(appSecret)}"
                  + $"&fb_exchange_token={Uri.EscapeDataString(shortToken)}";

        using var res = await client.GetAsync(url, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            _logger.LogWarning("Parfait Meta OAuth long-lived exchange failed. status={Status} body={Body}", (int)res.StatusCode, TrimForLog(body));
            throw new InvalidOperationException("Meta OAuth long-lived token exchange failed.");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var err))
            throw new InvalidOperationException($"Meta OAuth error: {err.GetProperty("message").GetString()}");

        var token = doc.RootElement.TryGetProperty("access_token", out var tokEl) ? tokEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Meta OAuth returned no long-lived access token.");

        DateTime? expiresUtc = null;
        if (doc.RootElement.TryGetProperty("expires_in", out var exEl))
        {
            var seconds = exEl.ValueKind == JsonValueKind.Number
                ? exEl.GetInt32()
                : int.TryParse(exEl.GetString(), out var parsed)
                    ? parsed
                    : 0;
            if (seconds > 0)
                expiresUtc = DateTime.UtcNow.AddSeconds(seconds);
        }

        return (token, expiresUtc);
    }

    private static async Task<(string UserId, string UserName)> FetchCurrentUserAsync(
        HttpClient client,
        string version,
        string accessToken,
        CancellationToken ct)
    {
        var url = $"https://graph.facebook.com/{version}/me?fields=id,name&access_token={Uri.EscapeDataString(accessToken)}";
        using var res = await client.GetAsync(url, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException("Unable to read Meta profile.");

        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var name = doc.RootElement.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
        return (id, name);
    }

    private static async Task<(string AccountId, string AccountName, string? BusinessId, string? BusinessName)> FetchBestAccountAsync(
        HttpClient client,
        string version,
        string accessToken,
        CancellationToken ct)
    {
        var fields = "id,name,account_status,business{id,name},campaigns.limit(1){id}";
        var url = $"https://graph.facebook.com/{version}/me/adaccounts?fields={Uri.EscapeDataString(fields)}&limit=200&access_token={Uri.EscapeDataString(accessToken)}";

        using var res = await client.GetAsync(url, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException("Unable to read Meta ad accounts.");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("No Meta ad accounts returned.");

        var accounts = new List<(string AccountId, string AccountName, string? BusinessId, string? BusinessName, bool HasBusiness, bool HasCampaigns, bool IsActive)>();

        foreach (var item in data.EnumerateArray())
        {
            var idRaw = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(idRaw))
                continue;

            var accountId = idRaw.StartsWith("act_", StringComparison.OrdinalIgnoreCase) ? idRaw[4..] : idRaw;
            var accountName = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : accountId;

            var hasBusiness = item.TryGetProperty("business", out var businessEl) && businessEl.ValueKind == JsonValueKind.Object;
            string? businessId = null;
            string? businessName = null;
            if (hasBusiness)
            {
                businessId = businessEl.TryGetProperty("id", out var businessIdEl) ? businessIdEl.GetString() : null;
                businessName = businessEl.TryGetProperty("name", out var businessNameEl) ? businessNameEl.GetString() : null;
            }

            var hasCampaigns =
                item.TryGetProperty("campaigns", out var campaignsEl) &&
                campaignsEl.TryGetProperty("data", out var campaignData) &&
                campaignData.ValueKind == JsonValueKind.Array &&
                campaignData.GetArrayLength() > 0;

            var status = item.TryGetProperty("account_status", out var statusEl) && statusEl.TryGetInt32(out var parsedStatus)
                ? parsedStatus
                : 0;

            accounts.Add((accountId, accountName, businessId, businessName, hasBusiness, hasCampaigns, status == 1));
        }

        var selected = accounts
            .OrderByDescending(x => x.HasBusiness)
            .ThenByDescending(x => x.HasCampaigns)
            .ThenByDescending(x => x.IsActive)
            .ThenBy(x => x.AccountName)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(selected.AccountId))
            throw new InvalidOperationException("No Meta ad account available for this user.");

        return (selected.AccountId, selected.AccountName, selected.BusinessId, selected.BusinessName);
    }

    private string ResolveRedirectUri(string? explicitRedirectUri)
    {
        var fromArg = (explicitRedirectUri ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromArg))
            return fromArg;

        var fromConfig = (_config["MetaAds:RedirectUri"] ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromConfig))
            return fromConfig;

        throw new InvalidOperationException("MetaAds:RedirectUri is required.");
    }

    private string Required(string key)
    {
        var value = (_config[key] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{key} is required.");

        return value;
    }

    private static string TrimForLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "(empty)";

        return text.Length <= 600 ? text : text[..600];
    }

    private sealed class OAuthState
    {
        public string ProfileKey { get; set; } = "parfait-business-profile";
        public string ReturnUrl { get; set; } = "/internal/settings/business-profile";
        public DateTime IssuedUtc { get; set; }
        public string Nonce { get; set; } = "";
    }
}
