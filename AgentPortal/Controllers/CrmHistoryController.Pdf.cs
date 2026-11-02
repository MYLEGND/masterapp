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

    private static byte[] RenderCaseHistoryPdf(JsonElement report, string name, string kind, string zone, string? sourceBase = null)
    {
        const string navy = "#081A3A", gold = "#8A6514", muted = "#526078";
        var entries = report.GetProperty("entries").EnumerateArray().ToArray();
        var evidence = report.GetProperty("evidenceIndex").EnumerateArray().ToArray();
        // Never silently coalesce distinct events or contradictory copies of an identity.
        foreach (var items in new[] { entries, evidence })
            if (items.Select(x => x.GetProperty("Id").GetString()).Distinct(StringComparer.Ordinal).Count() != items.Length)
                throw new InvalidOperationException("Duplicate record identity in case export; no report produced.");
        bool Audit(JsonElement x) => x.GetProperty("Source").GetString() is "HistoryAccess" or "HistoryExport";
        var cases = entries.Where(x => !Audit(x)).ToArray();
        var audits = entries.Where(Audit).ToArray();
        var submissions = cases.Where(x => x.GetProperty("Type").GetString() == "FormSubmission").ToArray();
        string Value(JsonElement x, string key) => x.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "Not recorded / unavailable";
        string When(JsonElement x)
        {
            var utc = Value(x, "OccurredUtc");
            if (DateTimeOffset.TryParse(utc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var instant))
                return TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(zone)).ToString("MMM d, yyyy HH:mm zzz") + " (" + zone + ")";
            return x.TryGetProperty("OccurredDate", out var date) && date.ValueKind == JsonValueKind.String
                ? date.GetString() + " (date only; time not recorded)" : "Event date not recorded";
        }
        string Identity(JsonElement x)
        {
            var value = Value(x, "RecordedBy");
            return Guid.TryParse(value, out _) ? "Recorder display name unavailable; stable ID: " + value : value;
        }
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.Letter); page.Margin(32);
            page.DefaultTextStyle(x => x.FontFamily("Lato").FontSize(9).FontColor(navy));
            page.Header().Column(header =>
            {
                header.Item().Text("LEGEND | CASE HISTORY").FontSize(10).Bold().FontColor(gold);
                header.Item().Text($"{kind}: {name}").FontSize(17).Bold();
                header.Item().PaddingBottom(8).Text("Confidential relationship record - authorized use only").FontSize(8).FontColor(muted);
            });
            page.Content().Column(body =>
            {
                body.Spacing(4);
                body.Item().Section("overview").Text("Case summary").FontSize(15).Bold();
                body.Item().Text($"{cases.Length} customer / case records, including {submissions.Length} form submissions. {audits.Length} access / export records retained separately in Appendix A.");
                var forms=submissions.GroupBy(x=>Value(x,"FormName")).ToArray();
                foreach(var form in forms.Take(8))body.Item().Text($"Submitted form: {form.Key} ({form.Count()})");
                if(forms.Length>8)body.Item().Text($"{forms.Length-8} additional form labels appear in the full records below.");
                body.Item().Text($"{report.GetProperty("protectedDocuments").GetArrayLength()} protected document references. Binary files are not embedded.");
                var noteCount = cases.Count(x => Value(x,"Type") is "Note" or "Call" or "AttemptedCall" or "Meeting" or "Email" or "Message" or "Addendum");
                body.Item().Text($"{noteCount} note / communication records included. Counts describe this export, not activity that may exist elsewhere.");
                body.Item().Background("#FFF3D5").Padding(10).Column(gaps =>
                {
                    gaps.Item().Text("Recorded evidence status and limitations").Bold().FontColor(gold);
                    foreach (var group in submissions.GroupBy(x => Value(x,"EvidenceStatus")))
                        gaps.Item().Text($"{group.Count()} submission(s): {group.Key}");
                    gaps.Item().Text($"{report.GetProperty("omittedSubmissions").GetArrayLength()} submission snapshot(s) unavailable under current association/permissions; see Appendix B.");
                    if (submissions.Length == 0) gaps.Item().Text("No form submission records included.");
                    gaps.Item().Text("Unavailable historical wording cannot establish exactly what was authorized. It does not prove consent never existed. Current wording is not substituted for historical evidence.");
                });
                body.Item().Text($"Export: {Value(report,"exportId")} | Generated UTC: {Value(report,"generatedUtc")}").FontSize(8);
                body.Item().Text($"Relationship: {Value(report,"relationship")}").FontSize(8);
                body.Item().Text("Complete authorized history; screen filters do not limit this report. Each entry and each submission snapshot is printed once. Similar-looking events with different IDs remain separate.").FontSize(8);
                body.Item().Text(Value(report,"limitations")).FontSize(8);
                body.Item().Text("Navigate this report").Bold();
                foreach (var link in new[] { ("cases","Customer activity and case records"), ("evidence","Submitted evidence"), ("audit","Appendix A - access and export audit"), ("references","Appendix B - retrieval and limitations") })
                    body.Item().SectionLink(link.Item1).Text(t => { t.Span(link.Item2 + " - page ").FontColor(gold); t.BeginPageNumberOfSection(link.Item1); });
                void Heading(string id, string title)
                {
                    if(id == "cases")body.Item().PageBreak();
                    body.Item().Section(id).EnsureSpace(120).PaddingTop(12).Text(title).FontSize(14).Bold().FontColor(gold);
                }
                void Records(JsonElement[] records)
                {
                    if(records.Length == 0) body.Item().Text("No records in this section.");
                    foreach(var entry in records)
                    {
                        var recordId=Value(entry,"Id");
                        // A repeated record heading travels with its first fields and identifies continuation pages.
                        body.Item().Section("record-"+recordId).Table(table =>
                        {
                            table.ColumnsDefinition(cols => cols.RelativeColumn());
                            table.Header(h => h.Cell().PaddingTop(8).Background("#F2F4F8").Padding(6).Column(head =>
                            {
                                head.Item().Text(Label(Value(entry,"Type"))+" | "+When(entry)).FontSize(11).Bold();
                                head.Item().Text("Recorded by: "+Identity(entry)).FontSize(8);
                                head.Item().Text("Status: "+Value(entry,"Status")+" | Evidence: "+Value(entry,"EvidenceStatus")).FontSize(8);
                                head.Item().Text("Recorded fields / continued - "+recordId).FontSize(7).FontColor(muted);
                            }));
                            table.Cell().DefaultTextStyle(x=>x.FontSize(8)).Column(fields=>WritePdfFields(fields,entry,"",true));
                        });
                        var submissionId=Value(entry,"SubmissionId");
                        if(evidence.Any(x=>Value(x,"Id")==submissionId)) body.Item().SectionLink("evidence-"+submissionId).Text("Open retained submission evidence").FontColor(gold);
                        var link=report.GetProperty("detailLinks").EnumerateArray().FirstOrDefault(x=>Value(x,"Id")==recordId);
                        if(link.ValueKind==JsonValueKind.Object && sourceBase is not null)
                            body.Item().Hyperlink(sourceBase+Value(link,"path")).Text("Open authenticated source record").FontColor(gold);
                    }
                }
                Heading("cases","Customer activity and case records"); Records(cases);
                Heading("evidence","Submitted evidence");
                body.Item().Text("Recorded snapshot values are not proof of what a person saw, understood or accepted. Original field names are retained; repeated values under different names are not silently removed.");
                foreach(var item in evidence)
                {
                    var evidenceId=Value(item,"Id");
                    body.Item().Section("evidence-"+evidenceId).EnsureSpace(80).Text("Submission "+evidenceId).Bold();
                    body.Item().Table(table=>
                    {
                        table.ColumnsDefinition(cols=>cols.RelativeColumn());
                        table.Header(h=>h.Cell().Background("#F2F4F8").Padding(5).Text("Retained submission / continued - "+evidenceId).FontSize(7));
                        table.Cell().DefaultTextStyle(x=>x.FontSize(8)).Column(fields=>WriteSubmissionFields(fields,item));
                    });
                }
                if(evidence.Length==0)body.Item().Text("No authorized submission snapshots included.");
                Heading("audit","Appendix A - access and export audit"); Records(audits);
                Heading("references","Appendix B - retrieval and limitations");
                foreach(var section in new[]{"omittedSubmissions"})
                {
                    body.Item().EnsureSpace(60).PaddingTop(8).Text(Label(section)).Bold();
                    WritePdfFields(body,report.GetProperty(section),"",false);
                }
                body.Item().EnsureSpace(60).Text("Protected document retrieval").Bold();
                foreach(var document in report.GetProperty("protectedDocuments").EnumerateArray())
                {
                    body.Item().SectionLink("record-"+Value(document,"Id")).Text("Integrity receipt: see record "+Value(document,"Id")).FontColor(gold);
                    foreach(var field in document.EnumerateObject().Where(x=>x.Name != "receipt"))WritePdfFields(body,field.Value,field.Name);
                    if(sourceBase is not null)body.Item().Hyperlink(sourceBase+Value(document,"downloadPath")).Text("Retrieve original document (authentication required)").FontColor(gold);
                }
                if(report.GetProperty("protectedDocuments").GetArrayLength()==0)body.Item().Text("No protected documents listed.");
                body.Item().EnsureSpace(60).Text("Authenticated source paths").Bold();
                if(sourceBase is null)body.Item().Text("Trusted AgentPortal base URL is not configured; paths below require opening within your authenticated portal. No origin was inferred from this request.");
                foreach(var link in report.GetProperty("detailLinks").EnumerateArray())
                {
                    body.Item().SectionLink("record-"+Value(link,"Id")).Text("Report record: "+Value(link,"Id")).FontSize(8).FontColor(gold);
                    body.Item().Text(Value(link,"path")).FontSize(7);
                }

            });
            page.Footer().PaddingTop(10).Row(row =>
            {
                row.RelativeItem().SectionLink("overview").Text("LEGEND | Back to case summary").FontSize(8).FontColor(muted);
                row.AutoItem().Text(t=>{t.Span("Page ");t.CurrentPageNumber();t.Span(" of ");t.TotalPages();});
            });
        })).GeneratePdf();
    }

    private static void WriteSubmissionFields(ColumnDescriptor column, JsonElement submission)
    {
        if(!submission.TryGetProperty("SnapshotJson",out var snapshotValue) || snapshotValue.ValueKind!=JsonValueKind.String){WritePdfFields(column,submission,"",true);return;}
        JsonDocument? snapshot=null;
        try { snapshot=JsonDocument.Parse(submission.GetProperty("SnapshotJson").GetString()!); } catch(JsonException) { }
        if(snapshot is null) { WritePdfFields(column,submission,"",true);return; }
        using(snapshot)
        {
            var root=snapshot.RootElement;
            if(root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Any())WritePdfFields(column,root,"Original snapshot");
            else
            {
                bool Context(string key)=>new[]{"UtmId","UtmSource","UtmMedium","UtmCampaign","UtmTerm","UtmContent","MetaCampaignId","MetaAdSetId","MetaAdId","FbcId","Fbp","Opref","Obref","CorrelationId","LandingPageUrl","ReferrerUrl","PageVariant","PageMode","PagePath","RecommendationPrimaryKey","RecommendationPrimaryTitle","RecommendationSecondaryKey","RecommendationSecondaryTitle"}.Contains(key,StringComparer.OrdinalIgnoreCase) || Regex.IsMatch(key,"^Answer[0-9]+$",RegexOptions.IgnoreCase);
                column.Item().EnsureSpace(60).Text("Recorded answers and retained form evidence").Bold();
                column.Item().Text("Field labels below retain their original identity. Historical question wording may be unavailable; browser-reported context is not independently verified authorization.").FontSize(8);
                foreach(var field in root.EnumerateObject().Where(x=>!Context(x.Name)))WritePdfFields(column,field.Value,field.Name);
                column.Item().EnsureSpace(60).PaddingTop(8).Text("Original tracking, recommendation and legacy answer fields").Bold();
                column.Item().Text("Retained for completeness. Recommendation values do not establish that they were shown or accepted; legacy numbered answers are not additional submissions.").FontSize(8);
                foreach(var field in root.EnumerateObject().Where(x=>Context(x.Name)))WritePdfFields(column,field.Value,field.Name);
            }
            column.Item().EnsureSpace(60).PaddingTop(8).Text("Submission identity and capture metadata").Bold();
            foreach(var field in submission.EnumerateObject().Where(x=>x.Name!="SnapshotJson"))WritePdfFields(column,field.Value,field.Name);
        }
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
        column.Item().EnsureSpace(28).PaddingTop(2).Text(t => { t.Span(Label(label) + ": ").SemiBold(); t.Span(text); });
    }
}
