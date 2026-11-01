using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.WebsiteEditing;
using Xunit;

namespace AgentPortal.Tests;

public sealed class WebsiteNamedDraftReaderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[invalid")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[] trailing")]
    public void MalformedOrNonArrayListsPreserveExistingEmptyResult(string? json)
    {
        var state = State(json);
        Assert.Empty(WebsiteNamedDraftReader.Read(state));
        Assert.Empty(WebsiteNamedDraftReader.ReadMetadata(state));
    }

    [Fact]
    public void MetadataAndFullReadsPreserveLegacyIdsOrderingDuplicatePropertiesAndPayloads()
    {
        var repeated = Guid.NewGuid();
        var rows = new List<string> { "null", "4", "[]" };
        foreach (var payload in new[] { "", " ", "\t\r\n", "\u00a0\u2003", "\b", " {\"version\":1}", "{\"version\":2}", "{\"version\":3}", "not-json" })
            rows.Add(JsonSerializer.Serialize(new { id = repeated, name = "  Saved  ", documentJson = payload }));
        rows.Add("{\"id\":\"bad\",\"name\":\"\",\"document\":{\"version\":3}}");
        rows.Add("{\"document\":{},\"documentJson\":\" \"}");
        rows.Add("{\"documentJson\":\" \",\"DOCUMENT\":{},\"NAME\":\"last\",\"updatedUtc\":\"2026-10-01T01:02:03Z\"}");
        rows.Add("{\"documentJson\":\"{}\",\"document\":false,\"name\":\"first\",\"name\":4,\"updatedUtc\":\"invalid\"}");
        rows.Add(JsonSerializer.Serialize(new { name = new string('x', 101), documentJson = "{}" }));
        var state = State("[" + string.Join(",", rows) + "]");
        var original = state.NamedDraftsJson;
        var expected = LegacyRead(state);
        var full = WebsiteNamedDraftReader.Read(state);
        var metadata = WebsiteNamedDraftReader.ReadMetadata(state);
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Select(d => (d.Id, d.Name, d.UpdatedUtc, d.DocumentJson)),
            full.Select(d => (d.Id, d.Name, d.UpdatedUtc, d.DocumentJson)));
        Assert.Equal(expected.Select(d => (d.Id, d.Name, d.UpdatedUtc)),
            metadata.Select(d => (d.Id, d.Name, d.UpdatedUtc)));
        Assert.Equal(metadata, WebsiteNamedDraftReader.ReadMetadata(state));
        Assert.Equal(original, state.NamedDraftsJson);
        Assert.Equal(metadata.Count, metadata.Select(d => d.Id).Distinct().Count());
    }

    [Fact]
    public void MetadataDoesNotAllocateDecodedMegabytePayloadCopies()
    {
        var payload = "{\"version\":3,\"text\":\"" + new string('x', 1024 * 1024) + "\"}";
        var state = State(JsonSerializer.Serialize(new[] { new { id = Guid.NewGuid(), name = "Large", documentJson = payload } }));
        _ = WebsiteNamedDraftReader.Read(state);
        _ = WebsiteNamedDraftReader.ReadMetadata(state);
        var start = GC.GetAllocatedBytesForCurrentThread();
        var full = WebsiteNamedDraftReader.Read(state);
        var fullBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        var metadata = WebsiteNamedDraftReader.ReadMetadata(state);
        var metadataBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(payload, Assert.Single(full).DocumentJson);
        Assert.Equal(Assert.Single(full).Id, Assert.Single(metadata).Id);
        Assert.True(metadataBytes + payload.Length < fullBytes,
            $"Full read allocated {fullBytes}; metadata allocated {metadataBytes}.");
    }

    private static WebsiteContentState State(string? json) => new()
    {
        Id = Guid.Parse("cb10a7be-d895-47e7-adfe-67b361ac7a86"),
        NamedDraftsJson = json!, UpdatedUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    // Frozen original11ec parser is the compatibility oracle, not a production reader.
    private static List<WebsiteNamedDraft> LegacyRead(WebsiteContentState state)
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
                        documentJson = property.Value.GetString();
                    else if (property.Name.Equals("document", StringComparison.OrdinalIgnoreCase) &&
                             property.Value.ValueKind == JsonValueKind.Object)
                        documentJson = property.Value.GetRawText();
                }

                if (string.IsNullOrWhiteSpace(documentJson)) continue;
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
                    DocumentJson = documentJson,
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
