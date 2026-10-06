using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shared.Analytics;

namespace Infrastructure.Analytics;

internal sealed record MarketingExternalOAuthState(
    string Provider,
    string OwnerType,
    Guid? OwnerId,
    string ReturnUrl,
    string RedirectUri,
    DateTime ExpiresUtc,
    string Nonce);

public sealed class MarketingExternalAdsOAuthService(
    IHttpClientFactory clients,
    IConfiguration configuration,
    IDataProtectionProvider dataProtectionProvider,
    MarketingConnectionStore connections,
    ILogger<MarketingExternalAdsOAuthService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _state = dataProtectionProvider.CreateProtector("Marketing.ExternalAds.OAuthState.v1");

    public string BuildConnectUrl(
        MarketingOwnerScope owner,
        string provider,
        string returnUrl,
        string? explicitRedirectUri = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var key = RequireProvider(provider);
        var redirectUri = ResolveRedirectUri(key, explicitRedirectUri);
        var safeReturnUrl = SafeReturnUrl(returnUrl);
        var state = ProtectState(new(
            key,
            owner.OwnerType,
            owner.Workspace.OwnerId,
            safeReturnUrl,
            redirectUri,
            DateTime.UtcNow.AddMinutes(10),
            Guid.NewGuid().ToString("N")));

        return key switch
        {
            MarketingDestinationKeys.Google => BuildGoogleAuthorizationUrl(state, redirectUri),
            MarketingDestinationKeys.TikTok => BuildTikTokAuthorizationUrl(state, redirectUri),
            _ => throw new InvalidOperationException("Unsupported external ads provider.")
        };
    }

    public MarketingOwnerScope InspectOwner(string stateToken, string provider)
    {
        var state = UnprotectState(stateToken);
        if (!string.Equals(state.Provider, RequireProvider(provider), StringComparison.Ordinal))
            throw new InvalidOperationException("Marketing OAuth provider mismatch.");
        return Owner(state);
    }

    public async Task<MarketingProviderOAuthCompletion> CompleteCallbackAsync(
        string provider,
        string code,
        string stateToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Provider authorization code was not returned.");

        var key = RequireProvider(provider);
        var state = UnprotectState(stateToken);
        if (!string.Equals(state.Provider, key, StringComparison.Ordinal))
            throw new InvalidOperationException("Marketing OAuth provider mismatch.");

        var owner = Owner(state);
        IReadOnlyList<MarketingProviderAccountOption> accounts;
        if (key == MarketingDestinationKeys.Google)
        {
            var token = await ExchangeGoogleAsync(code.Trim(), state.RedirectUri, ct);
            var previous = await connections.GetProviderCredentialAsync(owner, key, ct);
            var refreshToken = Clean(token.RefreshToken) ??
                (string.Equals(previous?.AuthorizationMethod,
                    MarketingProviderAuthorizationMethods.GoogleOAuthRefreshToken,
                    StringComparison.Ordinal) ? previous.PrimarySecret : null);
            if (string.IsNullOrWhiteSpace(refreshToken))
                throw new InvalidOperationException("Google Ads did not return a refresh credential. Reconnect with consent enabled.");

            accounts = await ListGoogleAccountsAsync(token.AccessToken, ct);
            var selected = SelectExistingOrSingle(previous?.AccountId, accounts);
            await connections.SaveProviderCredentialAsync(
                owner,
                key,
                refreshToken,
                MarketingProviderAuthorizationMethods.GoogleOAuthRefreshToken,
                JsonSerializer.Serialize(new
                {
                    scopes = token.Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
                    accessibleAccountCount = accounts.Count
                }, JsonOptions),
                null,
                selected?.AccountId,
                selected?.Label,
                ct);
        }
        else
        {
            var token = await ExchangeTikTokAsync(code.Trim(), ct);
            accounts = token.AdvertiserIds
                .Distinct(StringComparer.Ordinal)
                .Select(id => new MarketingProviderAccountOption(id, $"TikTok Ads {id}"))
                .ToArray();
            var previous = await connections.GetProviderCredentialAsync(owner, key, ct);
            var selected = SelectExistingOrSingle(previous?.AccountId, accounts);
            await connections.SaveProviderCredentialAsync(
                owner,
                key,
                token.AccessToken,
                MarketingProviderAuthorizationMethods.TikTokOAuthAccessToken,
                JsonSerializer.Serialize(new
                {
                    authorizedAccountIds = accounts.Select(x => x.AccountId).ToArray(),
                    accessibleAccountCount = accounts.Count
                }, JsonOptions),
                null,
                selected?.AccountId,
                selected?.Label,
                ct);
        }

        var snapshot = await connections.GetProviderConnectionAsync(owner, key, ct);
        return new(owner, key, state.ReturnUrl, snapshot, accounts);
    }

    public async Task<IReadOnlyList<MarketingProviderAccountOption>> GetAccountsAsync(
        MarketingOwnerScope owner,
        string provider,
        CancellationToken ct = default)
    {
        var key = RequireProvider(provider);
        var credential = await connections.GetProviderCredentialAsync(owner, key, ct)
            ?? throw new InvalidOperationException("Connect this provider before loading ad accounts.");

        if (key == MarketingDestinationKeys.Google)
        {
            var access = await RefreshGoogleAsync(credential.PrimarySecret, ct);
            return await ListGoogleAccountsAsync(access.AccessToken, ct);
        }

        return ReadTikTokAuthorizedAccounts(credential.AuthorizationMetadataJson);
    }

    public async Task<MarketingProviderConnectionSnapshot> SelectAccountAsync(
        MarketingOwnerScope owner,
        string provider,
        string accountId,
        CancellationToken ct = default)
    {
        var key = RequireProvider(provider);
        var selectedId = Clean(accountId)
            ?? throw new ArgumentException("Choose an ad account.", nameof(accountId));
        var accounts = await GetAccountsAsync(owner, key, ct);
        var selected = accounts.SingleOrDefault(x =>
            string.Equals(x.AccountId, selectedId, StringComparison.Ordinal));
        if (selected is null)
            throw new InvalidOperationException("The selected ad account is not authorized by the current provider credential.");

        return await connections.SelectProviderAccountAsync(
            owner, key, selected.AccountId, selected.Label, ct);
    }

    internal async Task<GoogleAccessToken> GetGoogleAccessAsync(
        MarketingOwnerScope owner,
        CancellationToken ct = default)
    {
        var credential = await connections.GetProviderCredentialAsync(owner, MarketingDestinationKeys.Google, ct)
            ?? throw new InvalidOperationException("Google Ads is not connected.");
        if (!string.Equals(credential.AuthorizationMethod,
                MarketingProviderAuthorizationMethods.GoogleOAuthRefreshToken,
                StringComparison.Ordinal))
            throw new InvalidOperationException("Google Ads credential type is invalid.");
        return await RefreshGoogleAsync(credential.PrimarySecret, ct);
    }

    internal async Task<string> GetTikTokAccessTokenAsync(
        MarketingOwnerScope owner,
        CancellationToken ct = default)
    {
        var credential = await connections.GetProviderCredentialAsync(owner, MarketingDestinationKeys.TikTok, ct)
            ?? throw new InvalidOperationException("TikTok Ads is not connected.");
        if (!string.Equals(credential.AuthorizationMethod,
                MarketingProviderAuthorizationMethods.TikTokOAuthAccessToken,
                StringComparison.Ordinal))
            throw new InvalidOperationException("TikTok Ads credential type is invalid.");
        return credential.PrimarySecret;
    }

    private string BuildGoogleAuthorizationUrl(string stateToken, string redirectUri)
    {
        var clientId = Required("GoogleAds:ClientId");
        return QueryHelpers.AddQueryString(
            "https://accounts.google.com/o/oauth2/v2/auth",
            new Dictionary<string, string?>
            {
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri,
                ["response_type"] = "code",
                ["scope"] = "https://www.googleapis.com/auth/adwords",
                ["access_type"] = "offline",
                ["prompt"] = "consent",
                ["include_granted_scopes"] = "true",
                ["state"] = stateToken
            });
    }

    private string BuildTikTokAuthorizationUrl(string stateToken, string redirectUri)
    {
        var configured = RequiredAbsoluteHttps("TikTokAds:AdvertiserAuthorizationUrl");
        return QueryHelpers.AddQueryString(configured,
            new Dictionary<string, string?>
            {
                ["state"] = stateToken,
                ["redirect_uri"] = redirectUri
            });
    }

    private async Task<GoogleTokenExchange> ExchangeGoogleAsync(
        string code,
        string redirectUri,
        CancellationToken ct)
    {
        using var response = await clients.CreateClient("MarketingExternalAds").PostAsync(
            "https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = Required("GoogleAds:ClientId"),
                ["client_secret"] = Required("GoogleAds:ClientSecret"),
                ["code"] = code,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri
            }),
            ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw ProviderFailure("Google Ads token exchange failed.", response.StatusCode, json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var access = RequiredJsonString(root, "access_token", "Google Ads access token");
        return new(
            access,
            OptionalJsonString(root, "refresh_token"),
            OptionalJsonString(root, "scope"),
            root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
                ? DateTime.UtcNow.AddSeconds(Math.Max(60, seconds))
                : DateTime.UtcNow.AddHours(1));
    }

    private async Task<GoogleAccessToken> RefreshGoogleAsync(
        string refreshToken,
        CancellationToken ct)
    {
        using var response = await clients.CreateClient("MarketingExternalAds").PostAsync(
            "https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = Required("GoogleAds:ClientId"),
                ["client_secret"] = Required("GoogleAds:ClientSecret"),
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token"
            }),
            ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw ProviderFailure("Google Ads credential refresh failed.", response.StatusCode, json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var access = RequiredJsonString(root, "access_token", "Google Ads access token");
        return new(
            access,
            root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
                ? DateTime.UtcNow.AddSeconds(Math.Max(60, seconds))
                : DateTime.UtcNow.AddHours(1));
    }

    private async Task<IReadOnlyList<MarketingProviderAccountOption>> ListGoogleAccountsAsync(
        string accessToken,
        CancellationToken ct)
    {
        var version = Clean(configuration["GoogleAds:ApiVersion"]) ?? "v25";
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^v\d{1,3}$"))
            throw new InvalidOperationException("GoogleAds:ApiVersion is invalid.");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://googleads.googleapis.com/{version}/customers:listAccessibleCustomers");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("developer-token", Required("GoogleAds:DeveloperToken"));

        using var response = await clients.CreateClient("MarketingExternalAds").SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw ProviderFailure("Google Ads account discovery failed.", response.StatusCode, json);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("resourceNames", out var names) ||
            names.ValueKind != JsonValueKind.Array)
            return [];

        return names.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x) && x!.StartsWith("customers/", StringComparison.Ordinal))
            .Select(x => x!["customers/".Length..].Replace("-", "", StringComparison.Ordinal))
            .Where(x => x.Length is >= 6 and <= 20 && x.All(char.IsDigit))
            .Distinct(StringComparer.Ordinal)
            .Select(x => new MarketingProviderAccountOption(x, $"Google Ads {x}"))
            .ToArray();
    }

    private async Task<TikTokTokenExchange> ExchangeTikTokAsync(
        string code,
        CancellationToken ct)
    {
        var endpoint = Clean(configuration["TikTokAds:TokenEndpoint"])
            ?? "https://business-api.tiktok.com/open_api/v1.3/oauth2/access_token/";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("TikTok Ads token endpoint is invalid.");

        using var response = await clients.CreateClient("MarketingExternalAds").PostAsJsonAsync(
            uri,
            new
            {
                app_id = Required("TikTokAds:AppId"),
                secret = Required("TikTokAds:AppSecret"),
                auth_code = code
            },
            JsonOptions,
            ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw ProviderFailure("TikTok Ads token exchange failed.", response.StatusCode, json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("code", out var codeNode) &&
            codeNode.ValueKind == JsonValueKind.Number &&
            codeNode.TryGetInt32(out var providerCode) &&
            providerCode != 0)
            throw new InvalidOperationException("TikTok Ads rejected the authorization code.");

        var data = root.TryGetProperty("data", out var dataNode) && dataNode.ValueKind == JsonValueKind.Object
            ? dataNode : root;
        var access = RequiredJsonString(data, "access_token", "TikTok Ads access token");
        var advertisers = data.TryGetProperty("advertiser_ids", out var ids) && ids.ValueKind == JsonValueKind.Array
            ? ids.EnumerateArray()
                .Where(x => x.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())
                .Select(Clean)
                .Where(x => x is not null)
                .Cast<string>()
                .Take(200)
                .ToArray()
            : [];
        return new(access, advertisers);
    }

    private static IReadOnlyList<MarketingProviderAccountOption> ReadTikTokAuthorizedAccounts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("authorizedAccountIds", out var ids) ||
                ids.ValueKind != JsonValueKind.Array)
                return [];
            return ids.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => Clean(x.GetString()))
                .Where(x => x is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .Select(x => new MarketingProviderAccountOption(x, $"TikTok Ads {x}"))
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static MarketingProviderAccountOption? SelectExistingOrSingle(
        string? existingAccountId,
        IReadOnlyList<MarketingProviderAccountOption> accounts)
    {
        if (!string.IsNullOrWhiteSpace(existingAccountId))
        {
            var existing = accounts.SingleOrDefault(x =>
                string.Equals(x.AccountId, existingAccountId, StringComparison.Ordinal));
            if (existing is not null) return existing;
        }
        return accounts.Count == 1 ? accounts[0] : null;
    }

    private string ProtectState(MarketingExternalOAuthState state) =>
        _state.Protect(JsonSerializer.Serialize(state, JsonOptions));

    private MarketingExternalOAuthState UnprotectState(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Marketing OAuth state is missing.");
        try
        {
            var state = JsonSerializer.Deserialize<MarketingExternalOAuthState>(
                _state.Unprotect(value), JsonOptions)
                ?? throw new InvalidOperationException("Marketing OAuth state is invalid.");
            if (state.ExpiresUtc <= DateTime.UtcNow)
                throw new InvalidOperationException("Marketing OAuth state expired. Start the connection again.");
            if (state.Nonce.Length != 32)
                throw new InvalidOperationException("Marketing OAuth state is invalid.");
            return state;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            logger.LogWarning(ex, "External marketing OAuth state validation failed.");
            throw new InvalidOperationException("Marketing OAuth state is invalid.");
        }
    }

    private static MarketingOwnerScope Owner(MarketingExternalOAuthState state) =>
        state.OwnerType switch
        {
            "founder" when state.OwnerId is null => MarketingOwnerScope.Founder,
            "agent" when state.OwnerId is { } id && id != Guid.Empty => MarketingOwnerScope.Agent(id),
            "business" when state.OwnerId is { } id && id != Guid.Empty => MarketingOwnerScope.Business(id),
            _ => throw new InvalidOperationException("Marketing OAuth owner is invalid.")
        };

    private static string RequireProvider(string provider)
    {
        var key = MarketingDestinationKeys.Normalize(provider);
        return key is MarketingDestinationKeys.Google or MarketingDestinationKeys.TikTok
            ? key
            : throw new ArgumentException("Provider must be google or tiktok.", nameof(provider));
    }

    private static string ProviderPrefix(string provider) =>
        provider == MarketingDestinationKeys.Google ? "GoogleAds" : "TikTokAds";

    private string Required(string key)
    {
        var value = Clean(configuration[key]);
        return value ?? throw new InvalidOperationException($"{key} is required to connect this provider.");
    }

    private string ResolveRedirectUri(string provider, string? explicitRedirectUri)
    {
        var value = Clean(explicitRedirectUri) ?? Required($"{ProviderPrefix(provider)}:RedirectUri");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"{ProviderPrefix(provider)} redirect URI must be an absolute HTTPS URL.");
        return uri.ToString();
    }

    private string RequiredAbsoluteHttps(string key)
    {
        var value = Required(key);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"{key} must be an absolute HTTPS URL.");
        return uri.ToString();
    }

    private static string SafeReturnUrl(string? value)
    {
        var target = Clean(value) ?? "/WebsiteAnalytics/Index";
        if (!target.StartsWith("/", StringComparison.Ordinal) ||
            target.StartsWith("//", StringComparison.Ordinal) ||
            target.Contains('\r') || target.Contains('\n'))
            return "/WebsiteAnalytics/Index";
        return target;
    }

    private static string RequiredJsonString(JsonElement root, string name, string label) =>
        OptionalJsonString(root, name)
        ?? throw new InvalidOperationException($"{label} was missing from the provider response.");

    private static string? OptionalJsonString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Clean(value.GetString())
            : null;

    private static InvalidOperationException ProviderFailure(
        string message,
        System.Net.HttpStatusCode status,
        string responseBody)
    {
        var detail = responseBody.Length > 400 ? responseBody[..400] : responseBody;
        return new InvalidOperationException($"{message} HTTP {(int)status}. Provider response: {detail}");
    }

    private static string? Clean(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private sealed record GoogleTokenExchange(
        string AccessToken,
        string? RefreshToken,
        string? Scope,
        DateTime ExpiresUtc);

    internal sealed record GoogleAccessToken(
        string AccessToken,
        DateTime ExpiresUtc);

    private sealed record TikTokTokenExchange(
        string AccessToken,
        IReadOnlyList<string> AdvertiserIds);
}
