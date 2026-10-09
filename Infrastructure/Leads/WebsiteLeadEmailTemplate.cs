using System.Net;
using System.Text;
using Domain.Entities;

namespace Infrastructure.Leads;

/// <summary>
/// Canonical presentation for every website lead/inquiry notification.
/// Transport, owner resolution and retry state remain separate authorities.
/// </summary>
public static class WebsiteLeadEmailTemplate
{
    private const string BgOuter = "#f4f4f6";
    private const string BgCard = "#0f172a";
    private const string BgHeader = "#0b1326";
    private const string BorderColor = "#b08d57";
    private const string HeaderText = "#f3c980";
    private const string LabelColor = "#d1b075";
    private const string ValueColor = "#f9fafb";
    private const string DividerColor = "rgba(176,141,87,0.3)";

    public static string Build(
        string title,
        string? firstName,
        string? lastName,
        string? email,
        string? phone,
        string? message,
        string? sourcePath)
    {
        var rows = new StringBuilder();
        Row(rows, "Name", $"{firstName} {lastName}".Trim());
        Row(rows, "Email", email);
        Row(rows, "Phone", phone);
        Row(rows, "Page", sourcePath);
        if (!string.IsNullOrWhiteSpace(message))
        {
            rows.Append($"<div style=\"height:1px;background:{DividerColor};margin:14px 0;\"></div>");
            rows.Append($"<div style=\"color:{LabelColor};font-size:12px;text-transform:uppercase;letter-spacing:.6px;margin-bottom:6px;\">Message</div>");
            rows.Append($"<div style=\"color:{ValueColor};font-size:15px;line-height:1.55;\">{WebUtility.HtmlEncode(message).Replace("\n", "<br>")}</div>");
        }

        return $@"<!DOCTYPE html>
<html>
<body style=""margin:0;padding:0;background:{BgOuter};font-family:Arial,sans-serif;"">
  <div style=""width:100%;padding:24px 12px;"">
    <div style=""max-width:640px;margin:0 auto;background:{BgCard};border:1px solid {BorderColor};border-radius:14px;color:{ValueColor};box-shadow:0 16px 38px rgba(0,0,0,0.28);overflow:hidden;"">
      <div style=""background:{BgHeader};padding:14px 18px;border-bottom:1px solid {BorderColor};color:{HeaderText};font-weight:700;letter-spacing:.5px;font-size:15px;"">
        {WebUtility.HtmlEncode(title)}
      </div>
      <div style=""padding:18px 20px 22px;"">{rows}</div>
    </div>
  </div>
</body>
</html>";
    }

    public static string Build(string title, WebsiteLead lead) =>
        Build(title, lead.FirstName, lead.LastName, lead.Email, lead.Phone, lead.Notes, lead.SourcePageKey);

    public static string SubjectFor(WebsiteLead lead) =>
        string.Equals(lead.InterestType, "ProtectionInquiry", StringComparison.OrdinalIgnoreCase)
            ? "New LEGEND Legacy Protection inquiry"
            : string.Equals(lead.InterestType, "BusinessInquiry", StringComparison.OrdinalIgnoreCase)
                ? "New website inquiry"
                : "New LEGEND® website inquiry";

    private static void Row(StringBuilder rows, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        rows.Append($"<div style=\"margin-bottom:12px;\"><div style=\"color:{LabelColor};font-size:12px;text-transform:uppercase;letter-spacing:.6px;margin-bottom:3px;\">{WebUtility.HtmlEncode(label)}</div><div style=\"color:{ValueColor};font-size:15px;line-height:1.4;\">{WebUtility.HtmlEncode(value)}</div></div>");
    }
}
