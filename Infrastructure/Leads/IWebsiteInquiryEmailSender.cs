namespace Infrastructure.Leads;

public interface IWebsiteInquiryEmailSender
{
    Task<bool> TrySendAsync(string toEmail, string subject, string htmlBody,
        string? textBody = null, string? replyToEmail = null,
        bool saveToSentItems = false, CancellationToken cancellationToken = default);
}
