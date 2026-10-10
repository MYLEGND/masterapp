using System.Reflection;
using System.Text.Json;

namespace Shared.Branding;

/// <summary>
/// One embedded, immutable brand authority shared by all .NET hosts.
/// Static website builds consume the same JSON file at compile time.
/// </summary>
public static class LegendBrand
{
    private static readonly Lazy<JsonDocument> Registry = new(() =>
    {
        var assembly = typeof(LegendBrand).Assembly;
        using var stream = assembly.GetManifestResourceStream("LegendBrand.Canonical")
            ?? throw new InvalidOperationException("Canonical LEGEND brand registry missing.");
        return JsonDocument.Parse(stream);
    }, isThreadSafe: true);

    private static string Field(string key) =>
        Registry.Value.RootElement.GetProperty(key).GetString()
        ?? throw new InvalidOperationException("Canonical LEGEND brand value missing: " + key);

    public static string RegisteredUpper => Field("registeredUpper");
    public static string RegisteredTitle => Field("registeredTitle");
    public static string RegisteredMark => Field("registeredMark");
    public static string LegalOwner => Field("legalOwner");
    public static string ProtectionName => Field("protectionName");
    public static string ProtectionUpper => Field("protectionUpper");
    public static string ProtectionOwnerDisclosure => Field("protectionOwnerDisclosure");
    public static string RegistrationNumber => Field("registrationNumber");
    public static string RegistrationClass => Field("registrationClass");
}
