using System.Security.Cryptography;
using System.Text;

namespace Infrastructure.Billing;

internal static class BillingIdempotency
{
    public static string CreateDeterministic(string scope, params string?[] parts)
    {
        var normalized = string.Join("|", new[] { scope }.Concat(parts.Select(Normalize)));
        return Hash(normalized);
    }

    public static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string CreateOpaqueToken(int byteLength = 32)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
