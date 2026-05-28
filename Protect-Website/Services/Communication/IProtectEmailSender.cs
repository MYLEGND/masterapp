namespace ProtectWebsite.Services.Communication;

public interface IProtectEmailSender
{
    Task<bool> TrySendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string? textBody = null,
        string? replyToEmail = null,
        bool saveToSentItems = false,
        CancellationToken cancellationToken = default);
}
