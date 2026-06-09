using System;
using AgentPortal.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Unit tests for PiiProtector encrypt/decrypt round-trip and key failure path.
/// </summary>
public class PiiProtectorTests
{
    private static PiiProtector BuildProtector()
    {
        var provider = new ServiceCollection()
            .AddDataProtection()
            .Services
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>();
        return new PiiProtector(provider);
    }

    [Fact]
    public void Encrypt_NullOrEmpty_ReturnsNull()
    {
        var pii = BuildProtector();
        Assert.Null(pii.Encrypt(null));
        Assert.Null(pii.Encrypt(""));
    }

    [Fact]
    public void Decrypt_NullOrEmpty_ReturnsNull()
    {
        var pii = BuildProtector();
        Assert.Null(pii.Decrypt(null));
        Assert.Null(pii.Decrypt(""));
    }

    [Fact]
    public void RoundTrip_SsnLast4_EncryptsThenDecrypts()
    {
        var pii = BuildProtector();
        const string plaintext = "1234";
        var ciphertext = pii.Encrypt(plaintext);

        Assert.NotNull(ciphertext);
        Assert.NotEqual(plaintext, ciphertext); // must not be stored as plaintext

        var decrypted = pii.Decrypt(ciphertext);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void RoundTrip_BankAccountNumber_EncryptsThenDecrypts()
    {
        var pii = BuildProtector();
        const string plaintext = "000123456789";
        var ciphertext = pii.Encrypt(plaintext);

        Assert.NotNull(ciphertext);
        var decrypted = pii.Decrypt(ciphertext);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void RoundTrip_DriverLicense_EncryptsThenDecrypts()
    {
        var pii = BuildProtector();
        const string plaintext = "D1234567";
        var ciphertext = pii.Encrypt(plaintext);

        Assert.NotNull(ciphertext);
        var decrypted = pii.Decrypt(ciphertext);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_TwoCallsWithSamePlaintext_ProduceDifferentCiphertexts()
    {
        // Data Protection payloads include a random nonce; same plaintext must not produce same ciphertext.
        var pii = BuildProtector();
        var c1 = pii.Encrypt("1234");
        var c2 = pii.Encrypt("1234");

        Assert.NotNull(c1);
        Assert.NotNull(c2);
        Assert.NotEqual(c1, c2);
    }

    [Fact]
    public void Decrypt_CorruptedCiphertext_ReturnsNull_DoesNotThrow()
    {
        // Key failure / corruption path: Decrypt must return null, never throw.
        var pii = BuildProtector();
        var result = pii.Decrypt("this-is-not-valid-ciphertext",
            logger: NullLogger.Instance,
            fieldName: "SsnLast4");
        Assert.Null(result);
    }

    [Fact]
    public void Decrypt_WrongKeyProtector_ReturnsNull()
    {
        // Encrypt with one key ring, decrypt with a different one — simulates key rotation mismatch.
        var pii1 = BuildProtector();
        var pii2 = BuildProtector(); // separate key ring instance (ephemeral keys differ per ServiceProvider)

        var ciphertext = pii1.Encrypt("secretvalue");
        Assert.NotNull(ciphertext);

        // pii2 has a different ephemeral key ring and cannot decrypt pii1's ciphertext.
        var decrypted = pii2.Decrypt(ciphertext, logger: NullLogger.Instance, fieldName: "test");
        Assert.Null(decrypted);
    }
}
