using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Entities;

namespace Infrastructure.WebsiteEditing;

internal sealed record WebsiteNamedDraftMetadata(Guid Id, string Name, DateTime UpdatedUtc);

/// <summary>The existing named-draft compatibility reader; metadata never retains payload copies.</summary>
internal static class WebsiteNamedDraftReader
{
    public static List<WebsiteNamedDraft> Read(WebsiteContentState state) => ReadCore(state, true);

    public static IReadOnlyList<WebsiteNamedDraftMetadata> ReadMetadata(WebsiteContentState state) =>
        ReadCore(state, false).Select(draft => new WebsiteNamedDraftMetadata(draft.Id, draft.Name, draft.UpdatedUtc)).ToArray();

    private static readonly JsonSerializerOptions PayloadPresenceOptions = new()
    {
        Converters = { new PayloadPresenceConverter() }
    };

    private sealed class PayloadPresenceConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // Persisted documents normally begin with '{'. JsonElement passes its
            // existing UTF-8 token to this converter, avoiding GetString's full
            // decoded payload allocation. Preserve exact Unicode/escape whitespace
            // semantics for historical values that do not have this safe prefix.
            var raw = reader.ValueSpan;
            if (raw.Length > 0 && raw[0] > 0x20 && raw[0] < 0x7f && raw[0] != (byte)'\\') return true;
            return !string.IsNullOrWhiteSpace(reader.GetString());
        }

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
            throw new NotSupportedException("Named-draft payload presence is read-only.");
    }

    private static List<WebsiteNamedDraft> ReadCore(WebsiteContentState state, bool includeDocuments)
    {
        if (string.IsNullOrWhiteSpace(state.NamedDraftsJson)) return [];

        try
        {
            using var parsed = JsonDocument.Parse(state.NamedDraftsJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Array) return [];
            var result = new List<WebsiteNamedDraft>();
            var identities = new HashSet<Guid>();
            var ordinal = -1;

            foreach (var item in parsed.RootElement.EnumerateArray())
            {
                ordinal++;
                if (item.ValueKind != JsonValueKind.Object) continue;
                Guid id = Guid.Empty;
                string name = "Website draft";
                var updatedUtc = state.UpdatedUtc;
                string? documentJson = null;
                var hasDocument = false;

                foreach (var property in item.EnumerateObject())
                {
                    if (property.Name.Equals("id", StringComparison.OrdinalIgnoreCase) &&
                        Guid.TryParse(property.Value.ToString(), out var parsedId))
                        id = parsedId;
                    else if (property.Name.Equals("name", StringComparison.OrdinalIgnoreCase) &&
                             property.Value.ValueKind == JsonValueKind.String)
                        name = property.Value.GetString() ?? name;
                    else if (property.Name.Equals("updatedUtc", StringComparison.OrdinalIgnoreCase) &&
                             property.Value.ValueKind == JsonValueKind.String &&
                             property.Value.TryGetDateTime(out var parsedUtc))
                        updatedUtc = parsedUtc;
                    else if (property.Name.Equals("documentJson", StringComparison.OrdinalIgnoreCase) &&
                             property.Value.ValueKind == JsonValueKind.String)
                    {
                        if (includeDocuments)
                        {
                            documentJson = property.Value.GetString();
                            hasDocument = !string.IsNullOrWhiteSpace(documentJson);
                        }
                        else hasDocument = property.Value.Deserialize<bool>(PayloadPresenceOptions);
                    }
                    else if (property.Name.Equals("document", StringComparison.OrdinalIgnoreCase) &&
                             property.Value.ValueKind == JsonValueKind.Object)
                    {
                        if (includeDocuments) documentJson = property.Value.GetRawText();
                        hasDocument = true;
                    }
                }

                if (!hasDocument) continue;
                // Missing, malformed, empty, or repeated historical IDs must identify
                // the same saved entry on every read. Read paths never rewrite the row.
                if (id == Guid.Empty || !identities.Add(id))
                {
                    var attempt = 0;
                    do
                    {
                        var identity = $"legend-named-draft/v1:{state.Id:N}:{ordinal}:{attempt++}";
                        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
                        id = new Guid(bytes.AsSpan(0, 16));
                    } while (id == Guid.Empty || !identities.Add(id));
                }
                result.Add(new WebsiteNamedDraft
                {
                    Id = id,
                    Name = name.Trim().Length is > 0 and <= 100 ? name.Trim() : "Website draft",
                    DocumentJson = documentJson ?? string.Empty,
                    UpdatedUtc = updatedUtc
                });
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

}
