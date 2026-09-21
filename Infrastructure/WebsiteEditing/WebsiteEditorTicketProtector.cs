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
    public const string SharedBlobUriConfigKey = "WebsiteEditorDataProtection:BlobUri";
    public const string SharedKeyVaultKeyIdConfigKey = "WebsiteEditorDataProtection:KeyVaultKeyId";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector;
    private readonly ServiceProvider? _ownedProvider;

    public WebsiteEditorTicketProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    private WebsiteEditorTicketProtector(IDataProtectionProvider provider, ServiceProvider ownedProvider)
    {
        _protector = provider.CreateProtector(Purpose);
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

        IConfiguration authorityConfiguration = configuration;
        if (environment.IsProduction())
        {
            var blobUri = configuration[SharedBlobUriConfigKey];
            var keyVaultKeyId = configuration[SharedKeyVaultKeyIdConfigKey];
            if (string.IsNullOrWhiteSpace(blobUri) || string.IsNullOrWhiteSpace(keyVaultKeyId))
                throw new InvalidOperationException(
                    $"Website editor ticket authority is not configured. Set both '{SharedBlobUriConfigKey}' and '{SharedKeyVaultKeyIdConfigKey}'.");

            authorityConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [PlatformDataProtection.BlobUriConfigKey] = blobUri,
                    [PlatformDataProtection.KeyVaultKeyIdConfigKey] = keyVaultKeyId
                })
                .Build();
        }

        services.AddPlatformDataProtection(
            authorityConfiguration,
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

    public void Dispose() => _ownedProvider?.Dispose();
}
