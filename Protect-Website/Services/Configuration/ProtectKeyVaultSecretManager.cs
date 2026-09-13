using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Security.KeyVault.Secrets;

namespace ProtectWebsite.Services.Configuration;

/// <summary>Local database configuration must not be replaced by the production vault connection.</summary>
public sealed class ProtectKeyVaultSecretManager(bool isDevelopment) : KeyVaultSecretManager
{
    public override bool Load(SecretProperties secret) =>
        !(isDevelopment && string.Equals(GetKey(new KeyVaultSecret(secret.Name, string.Empty)),
            "ConnectionStrings:MasterAppDb", StringComparison.OrdinalIgnoreCase))
        && base.Load(secret);
}
