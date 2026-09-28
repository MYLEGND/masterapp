using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Analytics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph;
using Shared.Analytics;

namespace Infrastructure.Bookings;

public sealed record MicrosoftCalendarConnectionSnapshot(
    MarketingOwnerScope Owner,
    bool Exists,
    bool Connected,
    Guid Revision,
    string? AccountName,
    string? UserId,
    string? Email,
    string? AuthorizationMethod,
    IReadOnlyList<string> Permissions,
    DateTime? ConnectedUtc,
    DateTime? DisconnectedUtc,
    DateTime? LastVerifiedUtc,
    DateTime? AccessTokenExpiresUtc);

public sealed record MicrosoftCalendarOAuthState(
    MarketingOwnerScope Owner,
    string ReturnUrl,
    string RedirectUri);

public interface IMicrosoftCalendarConnectionAuthority
{
    Task<MicrosoftCalendarConnectionSnapshot> GetAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken = default);

    string BuildConnectUrl(
        MarketingOwnerScope owner,
        string returnUrl,
        string redirectUri);

    MicrosoftCalendarOAuthState InspectState(string stateToken);

    Task<MicrosoftCalendarConnectionSnapshot> CompleteCallbackAsync(
        string code,
        string stateToken,
        CancellationToken cancellationToken = default);

    Task<MicrosoftCalendarConnectionSnapshot> DisconnectAsync(
        MarketingOwnerScope owner,
        Guid expectedRevision,
        CancellationToken cancellationToken = default);

    Task<string> GetAccessTokenAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken = default);

    Task<GraphServiceClient> CreateGraphClientAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Sole durable Microsoft calendar connection authority. Owner identity is the same
/// Founder/Agent/Business authority used by Marketing Setup; the existing
/// MarketingConnections table remains the one credential store. Manual calendar
/// fields select a target calendar/Bookings business but never prove authentication.
/// </summary>
public sealed class MicrosoftCalendarConnectionAuthority : IMicrosoftCalendarConnectionAuthority
{
    public const string Provider = "microsoft-calendar";
    public const string DelegatedAuthorization = "delegated_oauth";
    public const string ApplicationAuthorization = "application";

    private static readonly string[] DefaultScopes =
    [
        "openid",
        "profile",
        "email",
        "offline_access",
        "User.Read",
        "Calendars.ReadWrite",
        "Calendars.ReadWrite.Shared",
        "MailboxSettings.Read",
        "Bookings.Read.All",
        "BookingsAppointment.ReadWrite.All"
    ];

    private readonly MasterAppDbContext _db;
    private readonly MarketingCredentialProtector _protector;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDataProtector _stateProtector;

    public MicrosoftCalendarConnectionAuthority(
        MasterAppDbContext db,
        MarketingCredentialProtector protector,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IDataProtectionProvider dataProtectionProvider)
    {
        _db = db;
        _protector = protector;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _stateProtector = dataProtectionProvider.CreateProtector("LEGEND.MicrosoftCalendar.OAuthState.v1");
    }

    public async Task<MicrosoftCalendarConnectionSnapshot> GetAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var row = await _db.MarketingConnections.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.OwnerKey == owner.Key && x.Provider == Provider,
                cancellationToken);

        if (row is null && owner == MarketingOwnerScope.Founder)
        {
            row = await TryImportFounderApplicationConnectionAsync(cancellationToken);
        }

        return row is null ? Empty(owner) : Snapshot(owner, row);
    }

    public string BuildConnectUrl(
        MarketingOwnerScope owner,
        string returnUrl,
        string redirectUri)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var callback) ||
            callback.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("A valid Microsoft calendar OAuth redirect URI is required.");

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
        var tenant = AuthorityTenant();
        var scopes = ResolveScopes();

        return $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/authorize" +
               $"?client_id={Uri.EscapeDataString(RequiredClientId())}" +
               $"&response_type=code" +
               $"&redirect_uri={Uri.EscapeDataString(state.RedirectUri)}" +
               $"&response_mode=query" +
               $"&scope={Uri.EscapeDataString(string.Join(' ', scopes))}" +
               $"&state={Uri.EscapeDataString(token)}" +
               $"&prompt=select_account";
    }

    public MicrosoftCalendarOAuthState InspectState(string stateToken)
    {
        var state = ReadState(stateToken);
        return new MicrosoftCalendarOAuthState(
            ResolveOwner(state),
            state.ReturnUrl,
            state.RedirectUri);
    }

    public async Task<MicrosoftCalendarConnectionSnapshot> CompleteCallbackAsync(
        string code,
        string stateToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Missing Microsoft calendar OAuth code.");

        var state = ReadState(stateToken);
        var owner = ResolveOwner(state);
        var token = await ExchangeAuthorizationCodeAsync(
            code.Trim(),
            state.RedirectUri,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(token.AccessToken) ||
            string.IsNullOrWhiteSpace(token.RefreshToken))
            throw new InvalidOperationException("Microsoft did not return a durable delegated calendar authorization.");

        var account = await ReadMeAsync(token.AccessToken, cancellationToken);
        var email = FirstNotEmpty(account.Mail, account.UserPrincipalName);
        if (string.IsNullOrWhiteSpace(account.Id) || string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException("Microsoft calendar authorization did not resolve a mailbox identity.");

        var scopes = NormalizePermissions(token.Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? ResolveScopes());
        var expiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn));

        var row = await _db.MarketingConnections
            .SingleOrDefaultAsync(
                x => x.OwnerKey == owner.Key && x.Provider == Provider,
                cancellationToken);
        if (row is null)
        {
            row = New(owner);
            _db.MarketingConnections.Add(row);
        }

        row.AdsAccessTokenCiphertext = _protector.Protect(owner, Provider, token.AccessToken);
        row.CapiAccessTokenCiphertext = _protector.Protect(owner, Provider, token.RefreshToken);
        row.AccessTokenExpiresUtc = expiresUtc;
        row.AdAccountName = Clean(account.DisplayName, 300);
        row.ProviderUserId = Clean(account.Id, 200);
        row.ProviderUserEmail = Clean(email, 320);
        row.ProviderAuthorizationMethod = DelegatedAuthorization;
        row.ProviderPermissionsJson = JsonSerializer.Serialize(scopes);
        row.ConnectedUtc = DateTime.UtcNow;
        row.DisconnectedUtc = null;
        row.LastVerifiedUtc = DateTime.UtcNow;
        Touch(row);
        await _db.SaveChangesAsync(cancellationToken);

        return Snapshot(owner, row);
    }

    public async Task<MicrosoftCalendarConnectionSnapshot> DisconnectAsync(
        MarketingOwnerScope owner,
        Guid expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var row = await _db.MarketingConnections
            .SingleOrDefaultAsync(
                x => x.OwnerKey == owner.Key && x.Provider == Provider,
                cancellationToken)
            ?? throw new InvalidOperationException("Microsoft Calendar is not connected for this owner.");

        if (row.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("Microsoft Calendar connection changed. Reload and try again.");

        row.AdsAccessTokenCiphertext = null;
        row.CapiAccessTokenCiphertext = null;
        row.AccessTokenExpiresUtc = null;
        row.DisconnectedUtc = DateTime.UtcNow;
        row.LastVerifiedUtc = DateTime.UtcNow;
        Touch(row);
        await _db.SaveChangesAsync(cancellationToken);
        return Snapshot(owner, row);
    }

    public async Task<string> GetAccessTokenAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var row = await _db.MarketingConnections
            .SingleOrDefaultAsync(
                x => x.OwnerKey == owner.Key && x.Provider == Provider,
                cancellationToken);

        if (row is null && owner == MarketingOwnerScope.Founder)
            row = await TryImportFounderApplicationConnectionAsync(cancellationToken);

        if (row is null || row.DisconnectedUtc.HasValue)
            throw new InvalidOperationException("Microsoft Calendar is not connected for this owner.");

        if (string.Equals(row.ProviderAuthorizationMethod, ApplicationAuthorization, StringComparison.OrdinalIgnoreCase))
        {
            var token = await AcquireApplicationTokenAsync(cancellationToken);
            row.AccessTokenExpiresUtc = token.ExpiresOn.UtcDateTime;
            row.LastVerifiedUtc = DateTime.UtcNow;
            Touch(row);
            await _db.SaveChangesAsync(cancellationToken);
            return token.Token;
        }

        if (!string.Equals(row.ProviderAuthorizationMethod, DelegatedAuthorization, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Microsoft Calendar connection authorization method is invalid.");

        var accessToken = _protector.Unprotect(owner, Provider, row.AdsAccessTokenCiphertext);
        if (!string.IsNullOrWhiteSpace(accessToken) &&
            row.AccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(5))
            return accessToken;

        var refreshToken = _protector.Unprotect(owner, Provider, row.CapiAccessTokenCiphertext);
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException("Microsoft Calendar authorization expired. Reconnect the calendar.");

        var refreshed = await RefreshAsync(refreshToken, cancellationToken);
        if (string.IsNullOrWhiteSpace(refreshed.AccessToken))
            throw new InvalidOperationException("Microsoft Calendar authorization could not be refreshed. Reconnect the calendar.");

        row.AdsAccessTokenCiphertext = _protector.Protect(owner, Provider, refreshed.AccessToken);
        if (!string.IsNullOrWhiteSpace(refreshed.RefreshToken))
            row.CapiAccessTokenCiphertext = _protector.Protect(owner, Provider, refreshed.RefreshToken);
        row.AccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, refreshed.ExpiresIn));
        row.LastVerifiedUtc = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(refreshed.Scope))
            row.ProviderPermissionsJson = JsonSerializer.Serialize(
                NormalizePermissions(refreshed.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        Touch(row);
        await _db.SaveChangesAsync(cancellationToken);
        return refreshed.AccessToken;
    }

    public async Task<GraphServiceClient> CreateGraphClientAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(owner, cancellationToken);
        var expires = DateTimeOffset.UtcNow.AddMinutes(45);
        return new GraphServiceClient(new StaticTokenCredential(token, expires));
    }

    private async Task<MarketingConnection?> TryImportFounderApplicationConnectionAsync(
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ApplicationTenantId()) ||
            string.IsNullOrWhiteSpace(ApplicationClientId()) ||
            string.IsNullOrWhiteSpace(ApplicationClientSecret()))
            return null;

        try
        {
            var token = await AcquireApplicationTokenAsync(cancellationToken);
            var founderEmail = FirstNotEmpty(
                _configuration["Founder:Upn"],
                _configuration["Founder:Email"],
                Environment.GetEnvironmentVariable("OWNER_EMAIL"));
            if (string.IsNullOrWhiteSpace(founderEmail))
                return null;

            var account = await ReadUserAsync(founderEmail, token.Token, cancellationToken);
            if (string.IsNullOrWhiteSpace(account.Id))
                return null;

            var existing = await _db.MarketingConnections
                .SingleOrDefaultAsync(
                    x => x.OwnerKey == MarketingOwnerScope.Founder.Key && x.Provider == Provider,
                    cancellationToken);
            if (existing is not null)
                return existing;

            var row = New(MarketingOwnerScope.Founder);
            row.AdAccountName = Clean(account.DisplayName, 300) ?? "LEGEND Microsoft 365";
            row.ProviderUserId = Clean(account.Id, 200);
            row.ProviderUserEmail = Clean(FirstNotEmpty(account.Mail, account.UserPrincipalName, founderEmail), 320);
            row.ProviderAuthorizationMethod = ApplicationAuthorization;
            row.ProviderPermissionsJson = JsonSerializer.Serialize(new[] { "https://graph.microsoft.com/.default" });
            row.AccessTokenExpiresUtc = token.ExpiresOn.UtcDateTime;
            row.ConnectedUtc = DateTime.UtcNow;
            row.LastVerifiedUtc = DateTime.UtcNow;
            Touch(row);
            _db.MarketingConnections.Add(row);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return row;
            }
            catch (DbUpdateException)
            {
                _db.Entry(row).State = EntityState.Detached;
                return await _db.MarketingConnections
                    .SingleOrDefaultAsync(
                        x => x.OwnerKey == MarketingOwnerScope.Founder.Key && x.Provider == Provider,
                        cancellationToken);
            }
        }
        catch
        {
            // Existing founder booking remains configured, but Marketing Setup must
            // not claim provider connectivity unless Graph verification succeeds.
            return null;
        }
    }

    private async Task<AccessToken> AcquireApplicationTokenAsync(CancellationToken cancellationToken)
    {
        var credential = new Azure.Identity.ClientSecretCredential(
            ApplicationTenantId(),
            ApplicationClientId(),
            ApplicationClientSecret());
        return await credential.GetTokenAsync(
            new TokenRequestContext(["https://graph.microsoft.com/.default"]),
            cancellationToken);
    }

    private async Task<TokenResponse> ExchangeAuthorizationCodeAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = RequiredClientId(),
            ["client_secret"] = RequiredClientSecret(),
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["scope"] = string.Join(' ', ResolveScopes())
        };
        return await RequestTokenAsync(form, cancellationToken);
    }

    private async Task<TokenResponse> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = RequiredClientId(),
            ["client_secret"] = RequiredClientSecret(),
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = string.Join(' ', ResolveScopes())
        };
        return await RequestTokenAsync(form, cancellationToken);
    }

    private async Task<TokenResponse> RequestTokenAsync(
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var response = await client.PostAsync(
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(AuthorityTenant())}/oauth2/v2.0/token",
            new FormUrlEncodedContent(form),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = TokenError(body);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"Microsoft calendar authorization failed with HTTP {(int)response.StatusCode}."
                    : detail);
        }

        return JsonSerializer.Deserialize<TokenResponse>(
                   body,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("Microsoft calendar authorization returned an invalid token response.");
    }

    private async Task<GraphUser> ReadMeAsync(string accessToken, CancellationToken cancellationToken)
    {
        return await ReadGraphUserAsync(
            "https://graph.microsoft.com/v1.0/me?$select=id,displayName,mail,userPrincipalName",
            accessToken,
            cancellationToken);
    }

    private async Task<GraphUser> ReadUserAsync(
        string identity,
        string accessToken,
        CancellationToken cancellationToken)
    {
        return await ReadGraphUserAsync(
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(identity)}?$select=id,displayName,mail,userPrincipalName",
            accessToken,
            cancellationToken);
    }

    private async Task<GraphUser> ReadGraphUserAsync(
        string url,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Microsoft Calendar verification failed with HTTP {(int)response.StatusCode}.");

        return JsonSerializer.Deserialize<GraphUser>(
                   body,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("Microsoft Calendar verification returned an invalid account.");
    }

    private OAuthState ReadState(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Missing Microsoft calendar OAuth state.");
        try
        {
            var state = JsonSerializer.Deserialize<OAuthState>(_stateProtector.Unprotect(token))
                ?? throw new InvalidOperationException("Invalid Microsoft calendar OAuth state.");
            if (state.IssuedUtc < DateTime.UtcNow.AddMinutes(-20))
                throw new InvalidOperationException("Microsoft calendar OAuth state expired. Retry connect.");
            if (!Uri.TryCreate(state.RedirectUri, UriKind.Absolute, out _))
                throw new InvalidOperationException("Microsoft calendar OAuth state has an invalid redirect URI.");
            state.ReturnUrl = LocalReturnUrl(state.ReturnUrl);
            return state;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException("Invalid Microsoft calendar OAuth state.");
        }
    }

    private static MarketingOwnerScope ResolveOwner(OAuthState state)
    {
        return state.OwnerType switch
        {
            "founder" when !state.OwnerId.HasValue => MarketingOwnerScope.Founder,
            "agent" when state.OwnerId is Guid agent && agent != Guid.Empty => MarketingOwnerScope.Agent(agent),
            "business" when state.OwnerId is Guid business && business != Guid.Empty => MarketingOwnerScope.Business(business),
            _ => throw new InvalidOperationException("Microsoft calendar OAuth owner scope is invalid.")
        };
    }

    private string[] ResolveScopes()
    {
        var configured = _configuration["MicrosoftCalendar:Scopes"]
            ?? _configuration["MicrosoftCalendar__Scopes"];
        return NormalizePermissions(
            string.IsNullOrWhiteSpace(configured)
                ? DefaultScopes
                : configured.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private string AuthorityTenant() =>
        FirstNotEmpty(
            _configuration["MicrosoftCalendar:AuthorityTenant"],
            _configuration["MicrosoftCalendar__AuthorityTenant"])
        ?? "organizations";

    private string RequiredClientId() =>
        FirstNotEmpty(
            _configuration["MicrosoftCalendar:ClientId"],
            _configuration["MicrosoftCalendar__ClientId"],
            _configuration["AzureAd:ClientId"],
            _configuration["AzureAd__ClientId"])
        ?? throw new InvalidOperationException("Microsoft calendar OAuth client ID is not configured.");

    private string RequiredClientSecret() =>
        FirstNotEmpty(
            _configuration["MicrosoftCalendar:ClientSecret"],
            _configuration["MicrosoftCalendar__ClientSecret"],
            _configuration["AzureAd:ClientSecret"],
            _configuration["AzureAd__ClientSecret"])
        ?? throw new InvalidOperationException("Microsoft calendar OAuth client secret is not configured.");

    private string ApplicationTenantId() =>
        FirstNotEmpty(
            _configuration["GraphProvisioning:TenantId"],
            _configuration["GraphProvisioning__TenantId"],
            _configuration["AzureAd:TenantId"],
            _configuration["AzureAd__TenantId"]) ?? string.Empty;

    private string ApplicationClientId() =>
        FirstNotEmpty(
            _configuration["GraphProvisioning:ClientId"],
            _configuration["GraphProvisioning__ClientId"],
            _configuration["AzureAd:ClientId"],
            _configuration["AzureAd__ClientId"]) ?? string.Empty;

    private string ApplicationClientSecret() =>
        FirstNotEmpty(
            _configuration["GraphProvisioning:ClientSecret"],
            _configuration["GraphProvisioning__ClientSecret"],
            _configuration["AzureAd:ClientSecret"],
            _configuration["AzureAd__ClientSecret"]) ?? string.Empty;

    private static MarketingConnection New(MarketingOwnerScope owner) => new()
    {
        OwnerKey = owner.Key,
        OwnerType = owner.OwnerType,
        AgentTrackingProfileId = owner.AgentTrackingProfileId,
        CommerceBusinessId = owner.CommerceBusinessId,
        Provider = Provider,
        CreatedUtc = DateTime.UtcNow,
        Revision = Guid.NewGuid()
    };

    private static MicrosoftCalendarConnectionSnapshot Snapshot(
        MarketingOwnerScope owner,
        MarketingConnection row)
    {
        IReadOnlyList<string> permissions = [];
        if (!string.IsNullOrWhiteSpace(row.ProviderPermissionsJson))
        {
            try
            {
                permissions = JsonSerializer.Deserialize<string[]>(row.ProviderPermissionsJson) ?? [];
            }
            catch (JsonException)
            {
                permissions = [];
            }
        }

        return new MicrosoftCalendarConnectionSnapshot(
            owner,
            Exists: true,
            Connected: row.ConnectedUtc.HasValue && !row.DisconnectedUtc.HasValue,
            row.Revision,
            row.AdAccountName,
            row.ProviderUserId,
            row.ProviderUserEmail,
            row.ProviderAuthorizationMethod,
            permissions,
            row.ConnectedUtc,
            row.DisconnectedUtc,
            row.LastVerifiedUtc,
            row.AccessTokenExpiresUtc);
    }

    private static MicrosoftCalendarConnectionSnapshot Empty(MarketingOwnerScope owner) =>
        new(owner, false, false, Guid.Empty, null, null, null, null, [], null, null, null, null);

    private static void Touch(MarketingConnection row)
    {
        row.UpdatedUtc = DateTime.UtcNow;
        row.Revision = Guid.NewGuid();
    }

    private static string LocalReturnUrl(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        !value.StartsWith("/", StringComparison.Ordinal) ||
        value.StartsWith("//", StringComparison.Ordinal)
            ? "/WebsiteAnalytics/Index"
            : value;

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > max || trimmed.Any(char.IsControl))
            throw new ArgumentException($"Microsoft calendar value exceeds {max} characters or contains control characters.");
        return trimmed;
    }

    private static string? FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string[] NormalizePermissions(IEnumerable<string> values) =>
        values
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();

    private static string? TokenError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error_description", out var detail))
                return detail.GetString();
            if (doc.RootElement.TryGetProperty("error", out var error))
                return error.GetString();
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private sealed class OAuthState
    {
        public string OwnerType { get; set; } = string.Empty;
        public Guid? OwnerId { get; set; }
        public string ReturnUrl { get; set; } = "/";
        public string RedirectUri { get; set; } = string.Empty;
        public DateTime IssuedUtc { get; set; }
        public string Nonce { get; set; } = string.Empty;
    }

    private sealed class TokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("scope")]
        public string? Scope { get; set; }
    }

    private sealed class GraphUser
    {
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? Mail { get; set; }
        public string? UserPrincipalName { get; set; }
    }

    private sealed class StaticTokenCredential(string token, DateTimeOffset expiresOn) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(token, expiresOn);

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessToken(token, expiresOn));
    }
}
