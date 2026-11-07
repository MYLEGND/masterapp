using Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.Commerce.Payments;

/// <summary>One payment-purpose key-ring identity across management, callbacks and checkout.
/// Application authentication providers remain isolated.</summary>
public sealed class MerchantPaymentProtection : IDisposable
{
    private readonly ServiceProvider owner;
    public IDataProtectionProvider Provider { get; }
    private MerchantPaymentProtection(ServiceProvider services)
    {
        owner = services;
        Provider = services.GetRequiredService<IDataProtectionProvider>();
    }
    public static MerchantPaymentProtection CreateShared(IConfiguration configuration, IHostEnvironment environment)
    {
        var blob = configuration["WebsiteEditorDataProtection:BlobUri"] ?? configuration[PlatformDataProtection.BlobUriConfigKey];
        var key = configuration["WebsiteEditorDataProtection:KeyVaultKeyId"] ?? configuration[PlatformDataProtection.KeyVaultKeyIdConfigKey];
        if (environment.IsProduction() && (string.IsNullOrWhiteSpace(blob) || string.IsNullOrWhiteSpace(key)))
            throw new InvalidOperationException("Merchant payments require the configured shared protected key ring.");
        var sharedConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            [PlatformDataProtection.BlobUriConfigKey] = blob,
            [PlatformDataProtection.KeyVaultKeyIdConfigKey] = key
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformDataProtection(sharedConfiguration, environment, "LEGEND.MerchantPayments",
            Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "AgentPortal", "App_Data", "merchant-payment-keys")));
        return new(services.BuildServiceProvider());
    }
    public void Dispose() => owner.Dispose();
}
