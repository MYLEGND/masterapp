using System;
using Microsoft.Extensions.Configuration;

namespace Shared.Security;

/// <summary>
/// Shared startup configuration validation. Fails fast in production for
/// critical, security-sensitive misconfiguration instead of surfacing it as a
/// silent runtime fallback. Never reads, prints, or exposes secret values — it
/// validates only presence/consistency, and messages reference setting NAMES.
/// All checks are no-ops outside production so development convenience is kept.
/// </summary>
public static class PlatformConfigValidation
{
    public const string BlobUriConfigKey = "DataProtection:BlobUri";
    public const string KeyVaultKeyIdConfigKey = "DataProtection:KeyVaultKeyId";

    /// <summary>
    /// Rejects a PARTIAL Data Protection configuration in production (exactly one
    /// of the Blob URI / Key Vault key id set), which would silently fall back to
    /// non-persistent/local keys and quietly break cross-instance cookie
    /// decryption. Both-set (production key ring) and both-unset (local keys) are
    /// permitted, so a correctly configured deployment is never rejected.
    /// </summary>
    public static void ValidateDataProtection(IConfiguration configuration, bool isProduction)
    {
        if (!isProduction) return;

        var blobSet = !string.IsNullOrWhiteSpace(configuration[BlobUriConfigKey]);
        var keyVaultSet = !string.IsNullOrWhiteSpace(configuration[KeyVaultKeyIdConfigKey]);

        if (blobSet ^ keyVaultSet)
        {
            throw new InvalidOperationException(
                "STARTUP BLOCKED: Data Protection is only partially configured. Set BOTH " +
                $"'{BlobUriConfigKey}' and '{KeyVaultKeyIdConfigKey}' to use the production " +
                "Azure Blob + Key Vault key ring, or set neither to use local keys. A partial " +
                "configuration silently falls back to non-persistent/local keys and breaks " +
                "cross-instance cookie decryption.");
        }
    }

    /// <summary>
    /// Throws in production when a required, non-secret setting is missing. The
    /// value is never printed. No-op outside production.
    /// </summary>
    public static void RequireInProduction(string? value, bool isProduction, string settingName)
    {
        if (!isProduction) return;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"STARTUP BLOCKED: required production setting '{settingName}' is missing or empty.");
        }
    }
}
