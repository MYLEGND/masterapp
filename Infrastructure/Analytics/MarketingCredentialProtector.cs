using Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shared.Analytics;

namespace Infrastructure.Analytics;

/// <summary>Shared credential purpose without changing any application's cookie protection identity.</summary>
public sealed class MarketingCredentialProtector : IDisposable
{
    private readonly IDataProtectionProvider _provider;
    private readonly ServiceProvider? _ownedProvider;

    public MarketingCredentialProtector(IDataProtectionProvider provider) => _provider = provider;
    private MarketingCredentialProtector(ServiceProvider provider)
    {
        _provider = provider.GetRequiredService<IDataProtectionProvider>();
        _ownedProvider = provider;
    }

    public static MarketingCredentialProtector CreateShared(IConfiguration configuration, IHostEnvironment environment)
    {
        var services = new ServiceCollection();
        services.AddPlatformDataProtection(configuration, environment, "LEGEND.MarketingConnections",
            Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "AgentPortal", "App_Data", "marketing-keys")));
        return new MarketingCredentialProtector(services.BuildServiceProvider());
    }

    private IDataProtector For(MarketingOwnerScope owner) =>
        _provider.CreateProtector("LEGEND.MarketingConnection.v1", owner.Key);

    public string? Protect(MarketingOwnerScope owner, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : For(owner).Protect(value.Trim());

    // A decryption failure must surface; it must never select a different owner's credential.
    public string? Unprotect(MarketingOwnerScope owner, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : For(owner).Unprotect(value);

    public void Dispose() => _ownedProvider?.Dispose();
}
