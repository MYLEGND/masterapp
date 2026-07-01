using System.Security.Cryptography;
using System.Text;

namespace AgentPortal.Models.Analytics;

public static class MetaAdsScopeKey
{
    private static readonly Guid SiteNamespace = new("65f3f0df-c871-4eca-9148-0bb8fc8ba944");

    public static Guid ForSite(string siteKey)
    {
        if (string.IsNullOrWhiteSpace(siteKey))
            throw new ArgumentException("Site key is required.", nameof(siteKey));

        var normalized = siteKey.Trim().ToLowerInvariant();
        var namespaceBytes = SiteNamespace.ToByteArray();
        var valueBytes = Encoding.UTF8.GetBytes(normalized);
        var input = new byte[namespaceBytes.Length + valueBytes.Length];

        Buffer.BlockCopy(namespaceBytes, 0, input, 0, namespaceBytes.Length);
        Buffer.BlockCopy(valueBytes, 0, input, namespaceBytes.Length, valueBytes.Length);

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(input);
        var guidBytes = hash[..16];

        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return new Guid(guidBytes);
    }
}
