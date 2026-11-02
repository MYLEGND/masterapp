using System.Text.Json;
using Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.WebsiteEditing;

public sealed class WebsiteEditorTicketProtector : IDisposable
{
    private const string Purpose = "LEGEND.PublicWebsiteEditor.Ticket.v1";
    private const string SharedApplicationName = "LEGEND.PublicWebsiteEditor";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector;
    private readonly IDataProtector _storeLoginProtector;
    private readonly IDataProtector _storeProviderProtector;
    private readonly ServiceProvider? _ownedProvider;

    public WebsiteEditorTicketProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
        _storeLoginProtector = provider.CreateProtector("LEGEND.StoreManagement.LoginRequest.v1");
        _storeProviderProtector = provider.CreateProtector("LEGEND.StoreManagement.ProviderContinuation.v1");
    }

    private WebsiteEditorTicketProtector(IDataProtectionProvider provider, ServiceProvider ownedProvider)
    {
        _protector = provider.CreateProtector(Purpose);
        _storeLoginProtector = provider.CreateProtector("LEGEND.StoreManagement.LoginRequest.v1");
        _storeProviderProtector = provider.CreateProtector("LEGEND.StoreManagement.ProviderContinuation.v1");
        _ownedProvider = ownedProvider;
    }

    public static WebsiteEditorTicketProtector CreateShared(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var services = new ServiceCollection();
        var sharedDevelopmentKeys = Path.GetFullPath(Path.Combine(
            environment.ContentRootPath,
            "..",
            "AgentPortal",
            "App_Data",
            "website-editor-keys"));

        var editorBlobUri =
            configuration["WebsiteEditorDataProtection:BlobUri"] ??
            configuration["DataProtection:BlobUri"];
        var editorKeyVaultKeyId =
            configuration["WebsiteEditorDataProtection:KeyVaultKeyId"] ??
            configuration["DataProtection:KeyVaultKeyId"];

        var editorConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:BlobUri"] = editorBlobUri,
                ["DataProtection:KeyVaultKeyId"] = editorKeyVaultKeyId
            })
            .Build();

        services.AddPlatformDataProtection(
            editorConfiguration,
            environment,
            SharedApplicationName,
            sharedDevelopmentKeys);

        var provider = services.BuildServiceProvider();
        return new WebsiteEditorTicketProtector(
            provider.GetRequiredService<IDataProtectionProvider>(),
            provider);
    }

    public string Protect(WebsiteEditorTicket ticket)
        => _protector.Protect(JsonSerializer.Serialize(ticket, JsonOptions));

    public WebsiteEditorTicket? TryUnprotect(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var json = _protector.Unprotect(token);
            var ticket = JsonSerializer.Deserialize<WebsiteEditorTicket>(json, JsonOptions);
            if (ticket is null || ticket.ExpiresUtc <= DateTime.UtcNow) return null;
            if (ticket.SiteKey is not (
                WebsiteEditorSiteKeys.Protect or
                WebsiteEditorSiteKeys.Legend or
                WebsiteEditorSiteKeys.Business)) return null;

            if (ticket.SiteKey == WebsiteEditorSiteKeys.Business)
            {
                if (!ticket.CommerceBusinessId.HasValue ||
                    ticket.CommerceBusinessId == Guid.Empty ||
                    !string.Equals(
                        ticket.OwnerUserId,
                        WebsiteEditorSiteKeys.BusinessOwnerKey(ticket.CommerceBusinessId.Value),
                        StringComparison.Ordinal))
                    return null;
            }

            return ticket;
        }
        catch
        {
            return null;
        }
    }

    public string ProtectStoreLoginRequest(StoreManagementLoginRequest request)
    {
        if (!IsValidStoreLoginRequest(request))
            throw new ArgumentException("A complete, short-lived scoped store login request is required.", nameof(request));
        return _storeLoginProtector.Protect(JsonSerializer.Serialize(request, JsonOptions));
    }

    public StoreManagementLoginRequest? TryUnprotectStoreLoginRequest(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 8192) return null;
        try
        {
            var request = JsonSerializer.Deserialize<StoreManagementLoginRequest>(
                _storeLoginProtector.Unprotect(token), JsonOptions);
            return request is not null && IsValidStoreLoginRequest(request) ? request : null;
        }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
        catch (JsonException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static bool IsValidStoreLoginRequest(StoreManagementLoginRequest request)
    {
        var now = DateTime.UtcNow;
        if (request.CommerceBusinessId == Guid.Empty || request.ExpiresUtc.Kind != DateTimeKind.Utc ||
            request.ExpiresUtc <= now || request.ExpiresUtc > now.AddMinutes(5) ||
            string.IsNullOrWhiteSpace(request.OwnerKey) || request.OwnerKey.Length > 256 ||
            request.BrowserCorrelationHash is not { Length: 64 } ||
            request.BrowserCorrelationHash.Any(c => !char.IsAsciiHexDigit(c))) return false;
        if (request.SiteKey is not (WebsiteEditorSiteKeys.Business or WebsiteEditorSiteKeys.Legend or WebsiteEditorSiteKeys.Protect))
            return false;
        if (request.SiteKey == WebsiteEditorSiteKeys.Business &&
            request.OwnerKey != WebsiteEditorSiteKeys.BusinessOwnerKey(request.CommerceBusinessId)) return false;
        if (request.SiteKey == WebsiteEditorSiteKeys.Legend && request.OwnerKey != WebsiteEditorSiteKeys.GlobalOwnerKey)
            return false;
        if (!Uri.TryCreate(request.DestinationOrigin, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || !origin.IsDefaultPort || origin.HostNameType != UriHostNameType.Dns ||
            !string.IsNullOrEmpty(origin.UserInfo) || origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment) ||
            request.DestinationOrigin != origin.GetLeftPart(UriPartial.Authority)) return false;
        // Only a local internal destination is carried across the provider callback.
        // Reject ambiguous path encoding instead of relying on browser normalization.
        var path = request.ReturnPath;
        if (string.IsNullOrEmpty(path) || path.Length > 2048 || path.Any(char.IsControl) ||
            path.Contains('\\') || path.Contains('#')) return false;
        var route = path.Split('?', 2)[0];
        return (route == "/internal" || route.StartsWith("/internal/", StringComparison.Ordinal)) &&
            !route.Contains('%') && !route.Contains("//", StringComparison.Ordinal) &&
            !route.Split('/').Any(segment => segment is "." or "..");
    }

    public string ProtectStoreProviderContinuation(StoreProviderContinuation context)
    {
        if (!ValidStoreProviderContinuation(context)) throw new ArgumentException("Invalid store provider continuation.", nameof(context));
        return _storeProviderProtector.Protect(JsonSerializer.Serialize(context, JsonOptions));
    }

    private static bool ValidStoreProviderContinuation(StoreProviderContinuation? context)
    {
        if (context?.Actor is not { } actor || actor.CommerceBusinessId is not Guid businessId || businessId == Guid.Empty ||
            string.IsNullOrWhiteSpace(actor.ActorUserId) || context.Capability != "settings" ||
            actor.ExpiresUtc.Kind != DateTimeKind.Utc || actor.ExpiresUtc <= DateTime.UtcNow || actor.ExpiresUtc > DateTime.UtcNow.AddMinutes(20)) return false;
        return IsValidStoreLoginRequest(new(businessId, actor.SiteKey, actor.OwnerUserId, context.DestinationOrigin,
            "/internal", new string('0', 64), DateTime.UtcNow.AddMinutes(1)));
    }

    public StoreProviderContinuation? TryUnprotectStoreProviderContinuation(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var context = JsonSerializer.Deserialize<StoreProviderContinuation>(_storeProviderProtector.Unprotect(token), JsonOptions);
            return ValidStoreProviderContinuation(context) ? context : null;
        }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
        catch (JsonException) { return null; }
    }

    public void Dispose() => _ownedProvider?.Dispose();
}
