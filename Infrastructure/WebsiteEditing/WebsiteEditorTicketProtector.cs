using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Infrastructure.WebsiteEditing;

public sealed class WebsiteEditorTicketProtector
{
    private const string Purpose = "LEGEND.PublicWebsiteEditor.Ticket.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector;

    public WebsiteEditorTicketProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(WebsiteEditorTicket ticket)
        => _protector.Protect(JsonSerializer.Serialize(ticket, JsonOptions));

    public WebsiteEditorTicket? TryUnprotect(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var json = _protector.Unprotect(token);
            var ticket = JsonSerializer.Deserialize<WebsiteEditorTicket>(json, JsonOptions);
            if (ticket is null || ticket.ExpiresUtc <= DateTime.UtcNow) return null;
            if (ticket.SiteKey is not (WebsiteEditorSiteKeys.Protect or WebsiteEditorSiteKeys.Legend)) return null;
            return ticket;
        }
        catch
        {
            return null;
        }
    }
}
