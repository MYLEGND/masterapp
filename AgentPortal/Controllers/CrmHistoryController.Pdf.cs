using System.Text.Json;
using System.Text.RegularExpressions;
using AgentPortal.Services;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AgentPortal.Controllers;

// Presentation for the existing authorized Export action; no data access authority.
public sealed partial class CrmHistoryController
{
    private static string HistoryDisplayName(CrmHistoryRelationship relationship, bool isLead)
    {
        var parts = isLead
            ? new[] { relationship.Lead!.FirstName, relationship.Lead.LastName }
            : new[] { relationship.Client!.FirstName, relationship.Client.LastName };
        var name = string.Join(" ", parts.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));
        return name.Length == 0 ? "Name unavailable" : name;
    }

    private static byte[] RenderCaseHistoryPdf(JsonElement report, string name, string kind, string zone)
    {
        const string navy = "#081A3A", gold = "#9A751D", muted = "#526078";
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(32);
            page.DefaultTextStyle(x => x.FontFamily("Lato").FontSize(9).FontColor(navy));
            page.Header().Column(header =>
            {
                header.Item().Text("LEGEND | CASE HISTORY").FontSize(10).Bold().FontColor(gold);
                header.Item().Text($"{kind}: {name}").FontSize(17).Bold();
                header.Item().PaddingBottom(8).Text("Confidential relationship record - authorized use only").FontSize(8).FontColor(muted);
            });
            page.Content().Column(body =>
            {
                body.Spacing(8);
                body.Item().Text("Report overview").FontSize(14).Bold();
                body.Item().Text($"Export ID: {report.GetProperty("exportId").GetString()}");
                body.Item().Text($"Generated (UTC): {report.GetProperty("generatedUtc").GetString()}");
                body.Item().Text($"Relationship: {report.GetProperty("relationship").GetString()}");
                body.Item().Text($"Chronology time zone: {zone}. Exact timestamps retain their recorded offset; date-only and unknown dates are not estimated.");
                var entries = report.GetProperty("entries");
                body.Item().Text($"{entries.GetArrayLength()} records. Complete authorized relationship history; screen filters do not limit this report.").SemiBold();
                body.Item().Background("#F2F4F8").Padding(10).Text(report.GetProperty("limitations").GetString());
                body.Item().Text("Chronology at a glance").FontSize(13).Bold();
                var index = 0;
                foreach (var entry in entries.EnumerateArray())
                {
                    index++;
                    var date = entry.GetProperty("OccurredUtc").GetString() ?? entry.GetProperty("OccurredDate").GetString() ?? "Unknown event date";
                    body.Item().BorderBottom(0.5f).BorderColor("#D8DFE9").PaddingVertical(5)
                        .Text($"{index}. {Label(entry.GetProperty("Type").GetString() ?? "Record")} | {date}\nSource: {entry.GetProperty("Source").GetString()} | Status: {entry.GetProperty("Status").GetString()}\nEvidence: {entry.GetProperty("EvidenceStatus").GetString()}");
                }
                body.Item().PageBreak();
                body.Item().Text("Complete record details").FontSize(14).Bold();
                index = 0;
                foreach (var entry in entries.EnumerateArray())
                {
                    body.Item().PaddingTop(12).Text($"Record {++index} - {Label(entry.GetProperty("Type").GetString() ?? "Record")}").FontSize(12).Bold().FontColor(gold);
                    WritePdfFields(body, entry, "", true);
                }
                foreach (var section in new[] { "evidenceIndex", "omittedSubmissions", "protectedDocuments", "detailLinks" })
                {
                    body.Item().PaddingTop(14).Text(Label(section)).FontSize(13).Bold().FontColor(gold);
                    WritePdfFields(body, report.GetProperty(section), "", section == "evidenceIndex");
                }
                body.Item().PaddingTop(12).Text("Scope and limitations").FontSize(13).Bold();
                body.Item().Text(report.GetProperty("limitations").GetString());
            });
            page.Footer().PaddingTop(10).Row(row =>
            {
                row.RelativeItem().Text("LEGEND | Confidential case history").FontSize(8).FontColor(muted);
                row.AutoItem().Text(t => { t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages(); });
            });
        })).GeneratePdf();
    }

    private static string Label(string value) => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
        Regex.Replace(Regex.Replace(value, "([a-z0-9])([A-Z])", "$1 $2"), "[_-]+", " ").Trim());

    private static void WritePdfFields(ColumnDescriptor column, JsonElement value, string label, bool canonicalContainer = false)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var count = 0;
            foreach (var field in value.EnumerateObject())
            {
                count++;
                var path = string.IsNullOrEmpty(label) ? field.Name : label + " / " + field.Name;
                if (canonicalContainer && field.Name is "ContentJson" or "SnapshotJson" && field.Value.ValueKind == JsonValueKind.String)
                {
                    JsonDocument? parsed = null;
                    try { parsed = JsonDocument.Parse(field.Value.GetString()!); }
                    catch (JsonException) { }
                    if (parsed is not null)
                    {
                        using (parsed) WritePdfFields(column, parsed.RootElement, path[..^4]);
                    }
                    else WritePdfFields(column, field.Value, path[..^4] + " (unstructured original text)");
                }
                else WritePdfFields(column, field.Value, path);
            }
            if (count == 0) column.Item().Text($"{Label(label)}: Empty recorded object");
            return;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in value.EnumerateArray()) WritePdfFields(column, item,
                (string.IsNullOrEmpty(label) ? "Item " : label + " / item ") + ++i, canonicalContainer);
            if (i == 0) column.Item().Text(string.IsNullOrEmpty(label) ? "No items recorded" : $"{Label(label)}: No items recorded").FontColor("#526078");
            return;
        }
        var text = value.ValueKind switch
        {
            JsonValueKind.Null => "Not recorded / unavailable",
            JsonValueKind.String => value.GetString() is "" ? "Empty submitted value" : value.GetString()!,
            JsonValueKind.True => "Yes (true)",
            JsonValueKind.False => "No (false)",
            _ => value.ToString()
        };
        column.Item().PaddingTop(3).Text(t => { t.Span(Label(label) + ": ").SemiBold(); t.Span(text); });
    }
}
