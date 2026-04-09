using System.Net;
using System.Text;

namespace ProtectWebsite.Services;

/// <summary>
/// Builds styled lead notification emails matching the dark navy + gold card design
/// used by the website's popup lead modal.
/// </summary>
public static class LeadEmailTemplate
{
    // ── Design tokens (match LeadSubmitController card exactly) ─────────────
    private const string BgOuter       = "#f4f4f6";
    private const string BgCard        = "#0f172a";
    private const string BgHeader      = "#0b1326";
    private const string BorderColor   = "#b08d57";
    private const string HeaderText    = "#f3c980";
    private const string LabelColor    = "#d1b075";
    private const string ValueColor    = "#f9fafb";
    private const string DividerColor  = "rgba(176,141,87,0.3)";
    private const string SectionColor  = "#f3c980";

    /// <summary>
    /// Wraps an inner HTML string (built with <see cref="RowBuilder"/>) in the full card shell.
    /// </summary>
    public static string Wrap(string title, string innerRows) => $@"
<!DOCTYPE html>
<html>
<body style=""margin:0;padding:0;background:{BgOuter};font-family:Arial,sans-serif;"">
  <div style=""width:100%;padding:24px 12px;"">
    <div style=""max-width:640px;margin:0 auto;background:{BgCard};border:1px solid {BorderColor};border-radius:14px;color:{ValueColor};box-shadow:0 16px 38px rgba(0,0,0,0.28);overflow:hidden;"">
      <div style=""background:{BgHeader};padding:14px 18px;border-bottom:1px solid {BorderColor};color:{HeaderText};font-weight:700;letter-spacing:0.5px;font-size:15px;"">
        {WebUtility.HtmlEncode(title)}
      </div>
      <div style=""padding:18px 20px 22px;"">
        {innerRows}
      </div>
    </div>
  </div>
</body>
</html>";

    /// <summary>Builder that accumulates field rows and section dividers.</summary>
    public sealed class RowBuilder
    {
        private readonly StringBuilder _sb = new();

        /// <summary>Adds a label + value row. Skips rows where value is null/whitespace.</summary>
        public RowBuilder Row(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return this;
            _sb.Append($@"<div style=""margin-bottom:10px;""><div style=""color:{LabelColor};font-size:12px;text-transform:uppercase;letter-spacing:0.6px;"">{WebUtility.HtmlEncode(label)}</div><div style=""font-size:15px;font-weight:700;color:{ValueColor};"">{WebUtility.HtmlEncode(value)}</div></div>");
            return this;
        }

        /// <summary>Same as Row but value is already HTML (no encoding). Use sparingly.</summary>
        public RowBuilder RowHtml(string label, string? htmlValue)
        {
            if (string.IsNullOrWhiteSpace(htmlValue)) return this;
            _sb.Append($@"<div style=""margin-bottom:10px;""><div style=""color:{LabelColor};font-size:12px;text-transform:uppercase;letter-spacing:0.6px;"">{WebUtility.HtmlEncode(label)}</div><div style=""font-size:15px;font-weight:700;color:{ValueColor};"">{htmlValue}</div></div>");
            return this;
        }

        /// <summary>Inserts a divider and a gold section heading.</summary>
        public RowBuilder Section(string heading)
        {
            _sb.Append($@"<div style=""border-top:1px solid {DividerColor};margin:14px 0;""></div><div style=""color:{SectionColor};font-weight:700;font-size:13px;text-transform:uppercase;letter-spacing:0.5px;margin-bottom:10px;"">{WebUtility.HtmlEncode(heading)}</div>");
            return this;
        }

        /// <summary>Appends a raw HTML block (e.g. a nested sub-card for drivers/vehicles).</summary>
        public RowBuilder Raw(string html)
        {
            _sb.Append(html);
            return this;
        }

        public override string ToString() => _sb.ToString();
    }

    // ── Convenience helpers ──────────────────────────────────────────────────

    public static string Bool(bool value) => value ? "Yes" : "No";
    public static string Money(decimal? v) => v.HasValue ? v.Value.ToString("C0") : "";
    public static string Date(DateTime? d) => d.HasValue ? d.Value.ToString("MM/dd/yyyy") : "";
    public static string E(string? s) => WebUtility.HtmlEncode(s?.Trim() ?? "");
}
