using Microsoft.AspNetCore.DataProtection;

namespace AgentPortal.Services;

/// <summary>
/// Application-layer encryption for high-risk PII fields stored in OnboardingSubmission.
/// Wraps IDataProtector so the same key ring used for cookies protects sensitive columns.
///
/// Fields protected: BankAccountNumber, BankRoutingNumber, DriverLicenseNumber, SsnLast4.
///
/// Column values in the database are always ciphertext. Plaintext only exists in process
/// memory during the duration of a request. Never log or return raw values from these fields.
/// </summary>
public sealed class PiiProtector
{
    private readonly IDataProtector _protector;

    public PiiProtector(IDataProtectionProvider provider)
    {
        // Distinct purpose string scopes these keys away from cookie/impersonation protectors.
        _protector = provider.CreateProtector("OnboardingPii.v1");
    }

    /// <summary>
    /// Encrypts a plaintext PII value. Returns null if input is null or empty (no sentinel stored).
    /// </summary>
    public string? Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;
        return _protector.Protect(plaintext);
    }

    /// <summary>
    /// Decrypts a ciphertext PII value. Returns null if input is null or empty.
    /// Returns null and logs a warning if decryption fails (e.g. key rotation, corruption).
    /// Never throws — callers receive null and must handle missing data gracefully.
    /// </summary>
    public string? Decrypt(string? ciphertext, ILogger? logger = null, string? fieldName = null)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;
        try
        {
            return _protector.Unprotect(ciphertext);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "PiiProtector: failed to decrypt field '{FieldName}'. " +
                "This may indicate a key rotation event or data corruption. " +
                "Returning null — the field will appear blank.",
                fieldName ?? "unknown");
            return null;
        }
    }
}
