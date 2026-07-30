using System;
using System.IO;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.Security;

/// <summary>
/// The one platform Data Protection authority. Encapsulates the standard key-ring
/// strategy already proven in AgentPortal/Protect-Website:
///  * Production (when both config keys are present): persist the key ring to
///    Azure Blob Storage and protect it at rest with Azure Key Vault, via managed
///    identity (<see cref="DefaultAzureCredential"/>).
///  * Otherwise (local/dev): persist keys to the file system so they survive
///    process restarts.
///
/// <para>Application-name isolation is a required parameter — callers pass their
/// own application name so distinct trust domains keep isolated purpose chains
/// even when they share the same key-ring storage. Apps that intentionally share
/// a ring (AgentPortal + Protect-Website, for cross-decrypting agent-scoped Meta
/// CAPI credentials) pass the SAME application name deliberately.</para>
/// </summary>
public static class PlatformDataProtection
{
    public const string BlobUriConfigKey = "DataProtection:BlobUri";
    public const string KeyVaultKeyIdConfigKey = "DataProtection:KeyVaultKeyId";

    public static IDataProtectionBuilder AddPlatformDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        string applicationName,
        string? developmentKeysDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(applicationName))
            throw new ArgumentException("An application name is required for Data Protection isolation.", nameof(applicationName));

        var blobUri = configuration[BlobUriConfigKey];
        var keyVaultKeyId = configuration[KeyVaultKeyIdConfigKey];

        var builder = services.AddDataProtection().SetApplicationName(applicationName);

        if (!string.IsNullOrWhiteSpace(blobUri) && !string.IsNullOrWhiteSpace(keyVaultKeyId))
        {
            // Production path: Azure Blob + Key Vault via managed identity.
            var credential = new DefaultAzureCredential();
            builder
                .PersistKeysToAzureBlobStorage(new Uri(blobUri), credential)
                .ProtectKeysWithAzureKeyVault(new Uri(keyVaultKeyId), credential);
        }
        else
        {
            // Local/dev fallback: persistent file-system keys.
            var keysDirectory = string.IsNullOrWhiteSpace(developmentKeysDirectory)
                ? Path.Combine(environment.ContentRootPath, "App_Data", "keys")
                : developmentKeysDirectory;
            Directory.CreateDirectory(keysDirectory);
            builder.PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
        }

        return builder;
    }
}
