using ParfaitApp.Models;

namespace ParfaitApp.Services;

public interface IGraphMailService
{
    Task SendOrderReceiptAsync(ParfaitOrderRecord order, CancellationToken ct = default);
    Task SendOrderNotificationAsync(ParfaitOrderRecord order, CancellationToken ct = default);
    Task SendAutomationEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default);
    Task SendParfaitTeamInviteAsync(
        string toEmail,
        string displayName,
        string roleLabel,
        IReadOnlyCollection<string> allowedPageTitles,
        string loginUrl,
        string invitedBy,
        CancellationToken ct = default);
}
