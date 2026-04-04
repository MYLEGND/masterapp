using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentPortal.Security;

/// <summary>
/// Validates simple timestamped HMAC signatures for ingest endpoints (analytics, lead submit).
/// When disabled via feature flag, callers can bypass without behavior change.
/// </summary>
public sealed class IngestSignatureValidator
{
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _config;
    private readonly ILogger<IngestSignatureValidator> _logger;

    private const string SecretConfigKey = "Analytics:SharedSecret";
    private const string SecretFallbackKey = "LeadIngest:SharedSecret";
    private static readonly TimeSpan AllowedSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NonceTtl = TimeSpan.FromMinutes(10);

    public IngestSignatureValidator(IMemoryCache cache, IConfiguration config, ILogger<IngestSignatureValidator> logger)
    {
        _cache = cache;
        _config = config;
        _logger = logger;
    }

    public bool TryValidate(Guid requestId, DateTimeOffset timestamp, string? providedSignature, out string failureReason)
    {
        var secret = _config[SecretConfigKey] ?? _config[SecretFallbackKey];
        if (string.IsNullOrWhiteSpace(secret))
        {
            failureReason = "missing_secret";
            return false;
        }

        if (string.IsNullOrWhiteSpace(providedSignature))
        {
            failureReason = "missing_signature";
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var age = now - timestamp;
        if (age < -AllowedSkew || age > AllowedSkew)
        {
            failureReason = "stale_timestamp";
            return false;
        }

        var nonceKey = $"ingest-nonce:{requestId}";
        if (_cache.TryGetValue(nonceKey, out _))
        {
            failureReason = "replay_detected";
            return false;
        }

        var payload = $"{requestId:D}:{timestamp:O}";
        var expected = ComputeHmac(secret, payload);

        if (!ConstantTimeEquals(expected, providedSignature))
        {
            failureReason = "bad_signature";
            _logger.LogWarning("Ingest HMAC failure for requestId={RequestId}", requestId);
            return false;
        }

        // Cache the nonce to prevent replays for the TTL window
        _cache.Set(nonceKey, true, NonceTtl);
        failureReason = string.Empty;
        return true;
    }

    private static string ComputeHmac(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a.Length != b.Length)
            return false;

        var diff = 0;
        for (int i = 0; i < a.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
