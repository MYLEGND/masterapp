using System.Text.Json;
using Infrastructure.Analytics;
using Shared.Analytics;

namespace Infrastructure.Reception;

/// <summary>Uses the existing owner-bound key authority; the immutable binding holds only ciphertext.</summary>
internal static class ReceptionistCredentialEnvelope
{
    private const int Version = 1;
    private const int MaximumSecretCharacters = 16_384;
    private const int MaximumCiphertextCharacters = 262_144;

    internal static string Protect(MarketingCredentialProtector protector, MarketingOwnerScope owner,
        string provider, string providerProjectKey, string kind, string secret)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ValidateIdentity(owner, provider, providerProjectKey, kind);
        if (string.IsNullOrEmpty(secret) || secret.Length > MaximumSecretCharacters)
            throw new ArgumentException("A bounded private voice credential is required.");
        // Protect the serialized envelope, so the existing protector's outer Trim never changes secret bytes.
        return protector.Protect(owner, kind, JsonSerializer.Serialize(new Envelope(Version, owner.Key,
            provider, providerProjectKey, kind, secret), ReceptionistConfigurationCodec.Json))
            ?? throw new InvalidOperationException("The private voice credential could not be protected.");
    }

    internal static string Unprotect(MarketingCredentialProtector protector, MarketingOwnerScope owner,
        string provider, string providerProjectKey, string kind, string protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ValidateIdentity(owner, provider, providerProjectKey, kind);
        if (string.IsNullOrWhiteSpace(protectedValue) || protectedValue.Length > MaximumCiphertextCharacters)
            throw new InvalidOperationException("The pinned private voice credential is unavailable.");
        // Cryptographic failures retain their established exception type; never fall back to a different source.
        var json = protector.Unprotect(owner, kind, protectedValue)
            ?? throw new InvalidOperationException("The pinned private voice credential is unavailable.");
        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(json, ReceptionistConfigurationCodec.Json); }
        catch (JsonException) { throw new InvalidOperationException("The pinned private voice credential is inconsistent."); }
        if (envelope is null || envelope.Version != Version || envelope.OwnerKey != owner.Key ||
            envelope.Provider != provider || envelope.ProviderProjectKey != providerProjectKey || envelope.Kind != kind ||
            string.IsNullOrEmpty(envelope.Secret) || envelope.Secret.Length > MaximumSecretCharacters)
            throw new InvalidOperationException("The pinned private voice credential is inconsistent.");
        return envelope.Secret;
    }

    private static void ValidateIdentity(MarketingOwnerScope owner, string provider, string project, string kind)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (provider != MarketingDestinationKeys.Normalize(provider) || kind is not ("voice-api" or "voice-webhook"))
            throw new ArgumentException("A canonical private voice credential identity is required.");
        ReceptionistConfigurationCodec.Required(project, 200, nameof(project));
    }

    private sealed record Envelope(int Version, string OwnerKey, string Provider,
        string ProviderProjectKey, string Kind, string Secret);
}
