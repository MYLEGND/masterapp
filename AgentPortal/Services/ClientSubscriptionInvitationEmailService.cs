using System.Globalization;
using Domain.Billing;
using Domain.Entities;

namespace AgentPortal.Services;

public sealed class ClientSubscriptionInvitationEmailService
{
    private readonly IConfiguration _configuration;
    private readonly IEmailSender _emailSender;

    public ClientSubscriptionInvitationEmailService(IConfiguration configuration, IEmailSender emailSender)
    {
        _configuration = configuration;
        _emailSender = emailSender;
    }

    public async Task SendAsync(
        ClientProfile client,
        ClientSubscriptionOffer offer,
        SubscriptionActivationInvitation invitation,
        string plainTextToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var toEmail = invitation.IntendedNormalizedEmail;
        if (string.IsNullOrWhiteSpace(toEmail))
            throw new InvalidOperationException("A subscription invitation email address is required.");

        var clientPortalBaseUrl = ResolveClientPortalBaseUrl();
        var activationUrl = $"{clientPortalBaseUrl}/billing/activate?token={Uri.EscapeDataString(plainTextToken)}";
        var monthlyAmount = (offer.MonthlyAmountCents / 100m).ToString("C2", CultureInfo.GetCultureInfo("en-US"));
        var displayName = string.Join(" ", new[] { client.FirstName, client.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = "there";

        var safeName = System.Net.WebUtility.HtmlEncode(displayName);
        var safeAmount = System.Net.WebUtility.HtmlEncode(monthlyAmount);
        var safeAnchor = System.Net.WebUtility.HtmlEncode(DescribeBillingAnchor(offer));
        var safeActivationUrl = System.Net.WebUtility.HtmlEncode(activationUrl);

        var subject = "Your ClientApp subscription invitation";
        var htmlBody = $"""
<div style="font-family:Inter,Arial,sans-serif;color:#111827;line-height:1.6;">
  <h2 style="margin:0 0 12px;">Hi {safeName},</h2>
  <p style="margin:0 0 12px;">
    Your agent prepared your ClientApp subscription invitation.
  </p>
  <div style="padding:16px;border:1px solid #d4af37;border-radius:14px;background:#fffaf0;max-width:640px;">
    <p style="margin:0 0 8px;"><strong>Monthly amount:</strong> {safeAmount}</p>
    <p style="margin:0 0 8px;"><strong>Billing anchor:</strong> {safeAnchor}</p>
    <p style="margin:0 0 12px;"><strong>Invitation expires:</strong> {invitation.ExpiresUtc.ToLocalTime():MMMM d, yyyy h:mm tt}</p>
    <p style="margin:0;">
      <a href="{safeActivationUrl}" target="_blank" style="color:#8f6b14;font-weight:800;text-decoration:none;">
        Open your secure activation link
      </a>
    </p>
  </div>
  <p style="margin:16px 0 0;color:#4b5563;">
    If you need a new invitation, reply to your agent and they can resend it without recreating your client profile.
  </p>
</div>
""";

        var textBody =
            $"Hi {displayName},\n\n" +
            "Your agent prepared your ClientApp subscription invitation.\n" +
            $"Monthly amount: {monthlyAmount}\n" +
            $"Billing anchor: {DescribeBillingAnchor(offer)}\n" +
            $"Invitation expires: {invitation.ExpiresUtc.ToLocalTime():MMMM d, yyyy h:mm tt}\n" +
            $"Activation link: {activationUrl}\n";

        var sent = await _emailSender.TrySendAsync(
            toEmail,
            subject,
            htmlBody,
            textBody);

        if (!sent)
            throw new InvalidOperationException("The subscription invitation email could not be sent with the current email configuration.");
    }

    private string ResolveClientPortalBaseUrl()
    {
        var value =
            _configuration["ClientPortal:BaseUrl"] ??
            _configuration["ClientPortal__BaseUrl"] ??
            _configuration["Provisioning:ClientPortalBaseUrl"] ??
            _configuration["Provisioning__ClientPortalBaseUrl"] ??
            _configuration["ClientPortalBaseUrl"] ??
            "https://client.mylegnd.com";

        return value.Trim().TrimEnd('/');
    }

    private static string DescribeBillingAnchor(ClientSubscriptionOffer offer)
    {
        return offer.BillingAnchorSelectionMode switch
        {
            BillingAnchorSelectionMode.FirstOfMonth => "1st of the month",
            BillingAnchorSelectionMode.FifteenthOfMonth => "15th of the month",
            BillingAnchorSelectionMode.SpecificDayOfMonth when offer.SelectedBillingAnchorDay.HasValue => $"Day {offer.SelectedBillingAnchorDay.Value} of the month",
            BillingAnchorSelectionMode.ClientSelectedIfAllowed => "Client-selected during activation when policy allows it",
            _ => "Provider default"
        };
    }
}
