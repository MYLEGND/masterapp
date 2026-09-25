using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Infrastructure.Messaging;

/// <summary>Exact-byte service wire signature shared by Azure send and callback boundaries.</summary>
public static class LegendCloudflareServiceSignature
{
    public static string Sign(byte[] key, string method, string path, string keyId,
        long timestamp, string nonce, ReadOnlySpan<byte> body)
    {
        var value = string.Join('\n', "legend-service.v1", method, path, keyId,
            timestamp.ToString(CultureInfo.InvariantCulture), nonce, Convert.ToHexStringLower(SHA256.HashData(body)));
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value)));
    }

    public static bool Verify(byte[] key, string method, string path, string keyId,
        long timestamp, string nonce, byte[] body, string supplied)
    {
        if (supplied.Length != 64 || supplied.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Sign(key, method, path, keyId, timestamp, nonce, body)),
            Encoding.ASCII.GetBytes(supplied));
    }
}
