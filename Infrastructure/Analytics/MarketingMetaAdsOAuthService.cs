using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shared.Analytics;

namespace Infrastructure.Analytics;

/// <summary>
/// Shared Meta OAuth protocol for owner-scoped marketing connections.
/// Ownership is carried in protected server state and must be re-authorized by the caller
/// before the returned credential is persisted.
/// </summary>
public sealed class MarketingMetaAdsOAuthService(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<MarketingMetaAdsOAuthService> logger)
{
    private readonly IDataProtector _stateProtector =
        dataProtectionProvider.CreateProtector("Marketing.MetaAds.OAuthState.v1");

    public string BuildConnectUrl(MarketingOwnerScope owner, string returnUrl, string redirectUri)
    {
        if (owner is null) throw new ArgumentNullException(nameof(owner));
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var callback) ||
            callback.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("A valid Meta OAuth redirect URI is required.");

        var appId = Required("MetaAds:AppId");
        var apiVersion = NormalizeVersion();
        var state = new OAuthState
        {
            OwnerType = owner.OwnerType,
            OwnerId = owner.AgentTrackingProfileId ?? owner.CommerceBusinessId,
            ReturnUrl = LocalReturnUrl(returnUrl),
            RedirectUri = callback.ToString(),
            IssuedUtc = DateTime.UtcNow,
            Nonce = Guid.NewGuid().ToString("N")
        };
        var token = _stateProtector.Protect(JsonSerializer.Serialize(state));
        var scopes = (configuration["MetaAds:Scopes"] ?? "ads_read,business_management").Trim();

        return $"https://www.facebook.com/{apiVersion}/dialog/oauth" +
               $"?client_id={Uri.EscapeDataString(appId)}" +
               $"&redirect_uri={Uri.EscapeDataString(state.RedirectUri)}" +
               $"&state={Uri.EscapeDataString(token)}" +
               $"&scope={Uri.EscapeDataString(scopes)}";
    }

    public MarketingMetaOAuthState InspectState(string stateToken)
    {
        var state = ReadState(stateToken);
        return new MarketingMetaOAuthState(ResolveOwner(state), state.ReturnUrl, state.RedirectUri);
    }

    public async Task<MarketingMetaOAuthResult> CompleteCallbackAsync(
        string code,
        string stateToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Missing Meta OAuth code.");

        var state = ReadState(stateToken);
        var owner = ResolveOwner(state);
        var appId = Required("MetaAds:AppId");
        var appSecret = Required("MetaAds:AppSecret");
        var version = NormalizeVersion();
        var client = httpClientFactory.CreateClient("ResilientDefault");

        var shortToken = await ExchangeCodeAsync(
            client, version, appId, appSecret, code.Trim(), state.RedirectUri, cancellationToken);
        var longToken = await ExchangeLongLivedAsync(
            client, version, appId, appSecret, shortToken.AccessToken, cancellationToken);
        var user = await FetchCurrentUserAsync(client, version, longToken.AccessToken, cancellationToken);
        var account = await FetchBestAccountAsync(client, version, longToken.AccessToken, cancellationToken);

        return new MarketingMetaOAuthResult(
            owner,
            state.ReturnUrl,
            new MetaAdsConnectionRecord
            {
                AgentTrackingProfileId = owner.AgentTrackingProfileId ?? Guid.Empty,
                AccessToken = longToken.AccessToken,
                AccessTokenExpiresUtc = longToken.ExpiresUtc,
                AccountId = account.AccountId,
                AccountName = account.AccountName,
                BusinessId = account.BusinessId,
                BusinessName = account.BusinessName,
                MetaUserId = user.UserId,
                MetaUserName = user.UserName,
                ConnectedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
    }

    private OAuthState ReadState(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Missing Meta OAuth state.");
        try
        {
            var state = JsonSerializer.Deserialize<OAuthState>(_stateProtector.Unprotect(token))
                ?? throw new InvalidOperationException("Invalid Meta OAuth state.");
            if (state.IssuedUtc < DateTime.UtcNow.AddMinutes(-20))
                throw new InvalidOperationException("Meta OAuth state expired. Retry connect.");
            if (string.IsNullOrWhiteSpace(state.RedirectUri) ||
                !Uri.TryCreate(state.RedirectUri, UriKind.Absolute, out _))
                throw new InvalidOperationException("Invalid Meta OAuth state.");
            return state;
        }
        catch (InvalidOperationException) { throw; }
        catch
        {
            throw new InvalidOperationException("Invalid Meta OAuth state.");
        }
    }

    private static MarketingOwnerScope ResolveOwner(OAuthState state) =>
        state.OwnerType switch
        {
            "agent" when state.OwnerId.HasValue && state.OwnerId != Guid.Empty =>
                MarketingOwnerScope.Agent(state.OwnerId.Value),
            "business" when state.OwnerId.HasValue && state.OwnerId != Guid.Empty =>
                MarketingOwnerScope.Business(state.OwnerId.Value),
            "founder" when !state.OwnerId.HasValue => MarketingOwnerScope.Founder,
            _ => throw new InvalidOperationException("Invalid Meta OAuth owner scope.")
        };

    private async Task<(string AccessToken, DateTime? ExpiresUtc)> ExchangeCodeAsync(
        HttpClient client, string version, string appId, string appSecret, string code,
        string redirectUri, CancellationToken ct)
    {
        var url = $"https://graph.facebook.com/{version}/oauth/access_token" +
                  $"?client_id={Uri.EscapeDataString(appId)}" +
                  $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                  $"&client_secret={Uri.EscapeDataString(appSecret)}" +
                  $"&code={Uri.EscapeDataString(code)}";
        using var response = await client.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Meta OAuth code exchange failed. status={Status} body={Body}",
                (int)response.StatusCode, TrimForLog(body));
            throw new InvalidOperationException("Meta OAuth code exchange failed.");
        }
        return ReadToken(body, "Meta OAuth returned no access token.");
    }

    private async Task<(string AccessToken, DateTime? ExpiresUtc)> ExchangeLongLivedAsync(
        HttpClient client, string version, string appId, string appSecret, string shortToken,
        CancellationToken ct)
    {
        var url = $"https://graph.facebook.com/{version}/oauth/access_token" +
                  "?grant_type=fb_exchange_token" +
                  $"&client_id={Uri.EscapeDataString(appId)}" +
                  $"&client_secret={Uri.EscapeDataString(appSecret)}" +
                  $"&fb_exchange_token={Uri.EscapeDataString(shortToken)}";
        using var response = await client.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Meta OAuth long-lived exchange failed. status={Status} body={Body}",
                (int)response.StatusCode, TrimForLog(body));
            throw new InvalidOperationException("Meta OAuth long-lived token exchange failed.");
        }
        return ReadToken(body, "Meta OAuth returned no long-lived access token.");
    }

    private static (string AccessToken, DateTime? ExpiresUtc) ReadToken(string body, string missingMessage)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException(
                $"Meta OAuth error: {(error.TryGetProperty("message", out var message) ? message.GetString() : "unknown")}");
        var token = doc.RootElement.TryGetProperty("access_token", out var tokenElement)
            ? tokenElement.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException(missingMessage);
        DateTime? expires = null;
        if (doc.RootElement.TryGetProperty("expires_in", out var expiry))
        {
            var seconds = expiry.ValueKind == JsonValueKind.Number
                ? expiry.GetInt32()
                : int.TryParse(expiry.GetString(), out var parsed) ? parsed : 0;
            if (seconds > 0) expires = DateTime.UtcNow.AddSeconds(seconds);
        }
        return (token, expires);
    }

    private static async Task<(string UserId, string UserName)> FetchCurrentUserAsync(
        HttpClient client, string version, string accessToken, CancellationToken ct)
    {
        var url = $"https://graph.facebook.com/{version}/me?fields=id,name&access_token={Uri.EscapeDataString(accessToken)}";
        using var response = await client.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Unable to read Meta profile.");
        using var doc = JsonDocument.Parse(body);
        return (
            doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
            doc.RootElement.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty);
    }

    private static async Task<(string AccountId, string AccountName, string? BusinessId, string? BusinessName)> FetchBestAccountAsync(
        HttpClient client, string version, string accessToken, CancellationToken ct)
    {
        var fields = "id,name,account_status,business{id,name},campaigns.limit(1){id}";
        var url = $"https://graph.facebook.com/{version}/me/adaccounts?fields={Uri.EscapeDataString(fields)}&limit=200&access_token={Uri.EscapeDataString(accessToken)}";
        using var response = await client.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Unable to read Meta ad accounts.");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("No Meta ad accounts returned.");

        var accounts = new List<AccountCandidate>();
        foreach (var item in data.EnumerateArray())
        {
            var rawId = item.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(rawId)) continue;
            var accountId = rawId.StartsWith("act_", StringComparison.OrdinalIgnoreCase) ? rawId[4..] : rawId;
            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? accountId : accountId;
            var hasBusiness = item.TryGetProperty("business", out var business) && business.ValueKind == JsonValueKind.Object;
            var businessId = hasBusiness && business.TryGetProperty("id", out var bid) ? bid.GetString() : null;
            var businessName = hasBusiness && business.TryGetProperty("name", out var bname) ? bname.GetString() : null;
            var hasCampaigns = item.TryGetProperty("campaigns", out var campaigns) &&
                campaigns.TryGetProperty("data", out var campaignData) &&
                campaignData.ValueKind == JsonValueKind.Array && campaignData.GetArrayLength() > 0;
            var active = item.TryGetProperty("account_status", out var status) &&
                status.TryGetInt32(out var statusValue) && statusValue == 1;
            accounts.Add(new(accountId, name, businessId, businessName, hasBusiness, hasCampaigns, active));
        }

        var selected = accounts.OrderByDescending(x => x.HasBusiness)
            .ThenByDescending(x => x.HasCampaigns)
            .ThenByDescending(x => x.IsActive)
            .ThenBy(x => x.AccountName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (selected is null) throw new InvalidOperationException("No Meta ad account available for this user.");
        return (selected.AccountId, selected.AccountName, selected.BusinessId, selected.BusinessName);
    }

    private string NormalizeVersion()
    {
        var version = (configuration["MetaAds:ApiVersion"] ?? "v21.0").Trim();
        return string.IsNullOrWhiteSpace(version) ? "v21.0" : version;
    }

    private string Required(string key)
    {
        var value = (configuration[key] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{key} is required.");
        return value;
    }

    private static string LocalReturnUrl(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim();
        if (!trimmed.StartsWith("/", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            Uri.TryCreate(trimmed, UriKind.Absolute, out _))
            throw new InvalidOperationException("Meta OAuth return URL must be local.");
        return trimmed;
    }

    private static string TrimForLog(string text) =>
        string.IsNullOrWhiteSpace(text) ? "(empty)" : text.Length <= 600 ? text : text[..600];

    private sealed class OAuthState
    {
        public string OwnerType { get; set; } = string.Empty;
        public Guid? OwnerId { get; set; }
        public string ReturnUrl { get; set; } = "/";
        public string RedirectUri { get; set; } = string.Empty;
        public DateTime IssuedUtc { get; set; }
        public string Nonce { get; set; } = string.Empty;
    }

    private sealed record AccountCandidate(
        string AccountId,
        string AccountName,
        string? BusinessId,
        string? BusinessName,
        bool HasBusiness,
        bool HasCampaigns,
        bool IsActive);
}

public sealed record MarketingMetaOAuthState(
    MarketingOwnerScope Owner,
    string ReturnUrl,
    string RedirectUri);

public sealed record MarketingMetaOAuthResult(
    MarketingOwnerScope Owner,
    string ReturnUrl,
    MetaAdsConnectionRecord Connection);
