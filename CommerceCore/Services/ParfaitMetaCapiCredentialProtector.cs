using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace ParfaitApp.Services;

public sealed class ParfaitMetaCapiCredentialProtector
{
    private const string Purpose = "AgentProfile.MetaCapiCredentials.v1";

    private readonly IDataProtector _protector;

    public ParfaitMetaCapiCredentialProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
            return null;

        return _protector.Protect(plaintext.Trim());
    }

    public string? Unprotect(string? ciphertext, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(ciphertext))
            return null;

        try
        {
            return _protector.Unprotect(ciphertext);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to decrypt stored Parfait Meta CAPI credential.");
            return null;
        }
    }
}
