using Microsoft.AspNetCore.DataProtection;
using Shared.Meta;

namespace AgentPortal.Services;

public sealed class MetaCapiCredentialProtector
{
    private readonly IDataProtector _protector;

    public MetaCapiCredentialProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(MetaCapiCredentialProtection.Purpose);
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
            logger?.LogWarning(ex, "Failed to decrypt stored Meta CAPI credential.");
            return null;
        }
    }
}
