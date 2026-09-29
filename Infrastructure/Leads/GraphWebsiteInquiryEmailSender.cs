using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using System.Net.Mail;

namespace Infrastructure.Leads;

/// <summary>
/// Canonical website inquiry/lead email transport shared by every web host.
/// Recipient ownership is resolved elsewhere; this class only sends to the
/// already-authorized destination.
/// </summary>
public sealed class GraphWebsiteInquiryEmailSender(
    IConfiguration configuration,
    ILogger<GraphWebsiteInquiryEmailSender> logger) : IWebsiteInquiryEmailSender
{
    public async Task<bool> TrySendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string? textBody = null,
        string? replyToEmail = null,
        bool saveToSentItems = false,
        CancellationToken cancellationToken = default)
    {
        var recipient = (toEmail ?? string.Empty).Trim();
        if (recipient.Length == 0)
        {
            logger.LogWarning("Website notification skipped because the canonical recipient was empty. subject={Subject}", subject);
            return false;
        }

        var tenantId = configuration["GraphMail:TenantId"]
            ?? configuration["AzureAd:TenantId"];
        var clientId = configuration["GraphMail:ClientId"]
            ?? configuration["AzureAd:ClientId"];
        var clientSecret = configuration["GraphMail:ClientSecret"]
            ?? configuration["AzureAd:ClientSecret"];
        var senderEmail = (configuration["WebsiteNotifications:SenderEmail"]
            ?? configuration["GraphMail:SenderUpn"]
            ?? configuration["Contact:SenderEmail"]
            ?? "connect@mylegnd.com").Trim();

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret) ||
            string.IsNullOrWhiteSpace(senderEmail))
        {
            logger.LogError(
                "Website notification transport is not configured. hasTenant={HasTenant} hasClient={HasClient} hasSecret={HasSecret} senderConfigured={SenderConfigured}",
                !string.IsNullOrWhiteSpace(tenantId),
                !string.IsNullOrWhiteSpace(clientId),
                !string.IsNullOrWhiteSpace(clientSecret),
                !string.IsNullOrWhiteSpace(senderEmail));
            return false;
        }

        try
        {
            _ = new MailAddress(recipient);
            if (!string.IsNullOrWhiteSpace(replyToEmail))
                _ = new MailAddress(replyToEmail.Trim());

            var graph = new GraphServiceClient(new ClientSecretCredential(tenantId, clientId, clientSecret),
                new[] { "https://graph.microsoft.com/.default" });

            var message = new Message
            {
                Subject = subject,
                Body = new ItemBody { ContentType = BodyType.Html, Content = htmlBody },
                ToRecipients =
                [
                    new Recipient { EmailAddress = new EmailAddress { Address = recipient } }
                ]
            };

            if (!string.IsNullOrWhiteSpace(replyToEmail))
            {
                message.ReplyTo =
                [
                    new Recipient { EmailAddress = new EmailAddress { Address = replyToEmail.Trim() } }
                ];
            }

            await graph.Users[senderEmail].SendMail.PostAsync(
                new SendMailPostRequestBody
                {
                    Message = message,
                    SaveToSentItems = saveToSentItems
                },
                cancellationToken: cancellationToken);

            logger.LogInformation(
                "Website notification accepted by Graph. to={Recipient} sender={Sender} subject={Subject}",
                recipient,
                senderEmail,
                subject);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Website notification failed. to={Recipient} sender={Sender} subject={Subject}",
                recipient,
                senderEmail,
                subject);
            return false;
        }
    }
}
