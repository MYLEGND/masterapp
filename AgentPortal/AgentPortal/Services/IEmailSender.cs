using System.Threading.Tasks;

namespace AgentPortal.Services;

public interface IEmailSender
{
    /// <summary>
    /// Attempts to send an email. Returns false if email is not configured or send fails.
    /// </summary>
    Task<bool> TrySendAsync(string toEmail, string subject, string? htmlBody, string? textBody = null);
}
