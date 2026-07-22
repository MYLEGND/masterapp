using System.Globalization;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

public sealed class ClientSubscriptionInvitationEmailService
{
    private sealed record AgentInvitationContact(
        string? FullName,
        string? Title,
        string? Email,
        string? Phone,
        string? Npn);

    private readonly MasterAppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly IEmailSender _emailSender;

    public ClientSubscriptionInvitationEmailService(
        MasterAppDbContext db,
        IConfiguration configuration,
        IEmailSender emailSender)
    {
        _db = db;
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
        var activationUrl = $"{clientPortalBaseUrl}/activate/{Uri.EscapeDataString(plainTextToken)}";
        var signInUrl = $"{clientPortalBaseUrl}/Account/Login";
        var monthlyAmount = (offer.MonthlyAmountCents / 100m).ToString("C2", CultureInfo.GetCultureInfo("en-US"));
        var displayName = BuildClientDisplayName(client);
        var signInEmail = FirstNonEmpty(toEmail, client.NormalizedEmail, client.Email) ?? toEmail;
        var invitationExpires = invitation.ExpiresUtc.ToString("MMMM d, yyyy", CultureInfo.GetCultureInfo("en-US"));
        var agent = await ResolveAgentContactAsync(
            offer.OwnerAgentUserId,
            invitation.CreatedByAgentUserId,
            cancellationToken);

        var safeName = HtmlEncode(displayName);
        var safeAmount = HtmlEncode(monthlyAmount);
        var safeAnchor = HtmlEncode(DescribeBillingAnchor(offer));
        var safeInvitationExpires = HtmlEncode(invitationExpires);
        var safeActivationUrl = HtmlEncode(activationUrl);
        var safeSignInUrl = HtmlEncode(signInUrl);
        var safeSignInEmail = HtmlEncode(signInEmail);
        var safeAgentName = HtmlEncode(FirstNonEmpty(agent.FullName, agent.Email, "Legend") ?? "Legend");
        var safeAgentTitle = HtmlEncode(agent.Title ?? string.Empty);
        var safeAgentPhone = HtmlEncode(agent.Phone ?? string.Empty);
        var safeAgentEmail = HtmlEncode(agent.Email ?? string.Empty);
        var safeAgentNpn = HtmlEncode(agent.Npn ?? string.Empty);

        var agentTitleHtml = string.IsNullOrWhiteSpace(agent.Title)
            ? string.Empty
            : $"""<div style="margin:4px 0 0;color:#5b6475;font-size:14px;">{safeAgentTitle}</div>""";
        var agentPhoneHtml = string.IsNullOrWhiteSpace(agent.Phone)
            ? string.Empty
            : $"""<div style="margin:10px 0 0;"><span style="display:inline-block;min-width:56px;color:#7a5f12;font-weight:700;">Phone</span><a href="tel:{safeAgentPhone}" style="color:#0f274d;text-decoration:none;font-weight:700;">{safeAgentPhone}</a></div>""";
        var agentEmailHtml = string.IsNullOrWhiteSpace(agent.Email)
            ? string.Empty
            : $"""<div style="margin:8px 0 0;"><span style="display:inline-block;min-width:56px;color:#7a5f12;font-weight:700;">Email</span><a href="mailto:{safeAgentEmail}" style="color:#0f274d;text-decoration:none;font-weight:700;">{safeAgentEmail}</a></div>""";
        var agentNpnHtml = string.IsNullOrWhiteSpace(agent.Npn)
            ? string.Empty
            : $"""<div style="margin:8px 0 0;"><span style="display:inline-block;min-width:56px;color:#7a5f12;font-weight:700;">NPN</span><span style="color:#0f274d;font-weight:700;">{safeAgentNpn}</span></div>""";
        var replyCopy = string.IsNullOrWhiteSpace(agent.Email)
            ? "If you need help, contact your Legend agent before your invitation expires."
            : "If you need help, reply directly to this email and your agent will pick it up.";

        var subject = "Activate your Legend ClientApp access";
        var htmlBody = $"""
<table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="margin:0;padding:0;background:#f4efe2;font-family:Arial,sans-serif;color:#132238;">
  <tr>
    <td align="center" style="padding:28px 14px;">
      <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="max-width:700px;background:#ffffff;border:1px solid #d4af37;border-radius:24px;overflow:hidden;">
        <tr>
          <td style="padding:0;background:#0f2347;">
            <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%">
              <tr>
                <td style="padding:28px 30px 10px 30px;">
                  <div style="display:inline-block;padding:7px 14px;border:1px solid #d4af37;border-radius:999px;color:#f0d991;font-size:12px;font-weight:800;letter-spacing:1.5px;text-transform:uppercase;">
                    Legend ClientApp
                  </div>
                </td>
              </tr>
              <tr>
                <td style="padding:0 30px 30px 30px;color:#ffffff;">
                  <div style="font-size:34px;line-height:1.1;font-weight:800;">Your private client access is ready</div>
                  <div style="margin-top:14px;font-size:18px;line-height:1.6;color:#dce6fb;">
                    Hi {safeName}, your agent prepared your secure ClientApp activation. Use the button below to confirm billing and finish sign-in with <strong style="color:#ffffff;">{safeSignInEmail}</strong>.
                  </div>
                </td>
              </tr>
            </table>
          </td>
        </tr>

        <tr>
          <td style="padding:28px 30px 0 30px;background:#fffdfa;">
            <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="border:1px solid #e1c46c;border-radius:20px;background:#fbf5e7;">
              <tr>
                <td style="padding:22px 24px;">
                  <div style="font-size:12px;font-weight:800;letter-spacing:1.4px;text-transform:uppercase;color:#8a6a13;">Subscription Summary</div>
                  <div style="margin-top:12px;font-size:40px;line-height:1;font-weight:800;color:#10224a;">{safeAmount}<span style="font-size:18px;font-weight:700;color:#5f6b7d;"> / month</span></div>
                  <div style="margin-top:14px;font-size:16px;line-height:1.7;color:#273652;"><strong>Billing day:</strong> {safeAnchor}</div>
                  <div style="font-size:16px;line-height:1.7;color:#273652;"><strong>Secure link expires:</strong> {safeInvitationExpires}</div>
                </td>
              </tr>
            </table>
          </td>
        </tr>

        <tr>
          <td align="center" style="padding:28px 30px 0 30px;background:#fffdfa;">
            <a href="{safeActivationUrl}" target="_blank" style="display:inline-block;padding:16px 28px;background:#ddb457;border:1px solid #b68922;border-radius:999px;color:#10224a;text-decoration:none;font-size:17px;font-weight:800;">
              Activate Subscription
            </a>
          </td>
        </tr>

        <tr>
          <td style="padding:26px 30px 0 30px;background:#fffdfa;">
            <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="border:1px solid #ead7a1;border-radius:18px;background:#f7f1e2;">
              <tr>
                <td style="padding:20px 22px;">
                  <div style="font-size:12px;font-weight:800;letter-spacing:1.2px;text-transform:uppercase;color:#8a6a13;">What To Expect</div>
                  <div style="margin-top:12px;font-size:16px;line-height:1.75;color:#243246;">1. Confirm your recurring billing details and payment method.</div>
                  <div style="font-size:16px;line-height:1.75;color:#243246;">2. Complete Microsoft sign-in using <strong>{safeSignInEmail}</strong>.</div>
                  <div style="font-size:16px;line-height:1.75;color:#243246;">3. Open your private client portal and manage everything from there.</div>
                </td>
              </tr>
            </table>
          </td>
        </tr>

        <tr>
          <td style="padding:24px 30px 0 30px;background:#fffdfa;">
            <div style="font-size:15px;line-height:1.7;color:#4d5768;">
              After activation, your normal client sign-in page is:
              <a href="{safeSignInUrl}" target="_blank" style="color:#0f274d;font-weight:700;text-decoration:none;">{safeSignInUrl}</a>
            </div>
            <div style="margin-top:10px;font-size:15px;line-height:1.7;color:#4d5768;">{replyCopy}</div>
          </td>
        </tr>

        <tr>
          <td style="padding:28px 30px 32px 30px;background:#fffdfa;">
            <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="border-top:1px solid #eadfbe;">
              <tr>
                <td style="padding-top:22px;">
                  <div style="font-size:12px;font-weight:800;letter-spacing:1.4px;text-transform:uppercase;color:#8a6a13;">Your Agent</div>
                  <div style="margin-top:10px;font-size:24px;font-weight:800;color:#10224a;">{safeAgentName}</div>
                  {agentTitleHtml}
                  {agentPhoneHtml}
                  {agentEmailHtml}
                  {agentNpnHtml}
                  <div style="margin-top:18px;font-size:16px;font-weight:800;color:#7a5f12;">Legend™</div>
                  <div style="margin-top:4px;font-size:14px;line-height:1.6;color:#5b6475;font-style:italic;">Where Your Faith Fuels Your Future &amp; Wellness Meets Wealth</div>
                </td>
              </tr>
            </table>
          </td>
        </tr>
      </table>
    </td>
  </tr>
</table>
""";

        var textLines = new List<string>
        {
            $"Hi {displayName},",
            string.Empty,
            "Your private Legend ClientApp access is ready.",
            $"Monthly amount: {monthlyAmount}",
            $"Billing day: {DescribeBillingAnchor(offer)}",
            $"Invitation expires: {invitationExpires}",
            $"Activate subscription: {activationUrl}",
            $"After activation, sign in here: {signInUrl}",
            $"Sign-in email: {signInEmail}",
            string.Empty,
            "What happens next:",
            "1. Confirm your recurring billing details and consents.",
            $"2. Finish Microsoft sign-in using {signInEmail}.",
            "3. Open your private client portal.",
            string.Empty,
            string.IsNullOrWhiteSpace(agent.FullName) ? "Your agent:" : $"Your agent: {agent.FullName}"
        };

        if (!string.IsNullOrWhiteSpace(agent.Title))
            textLines.Add(agent.Title);
        if (!string.IsNullOrWhiteSpace(agent.Phone))
            textLines.Add($"Phone: {agent.Phone}");
        if (!string.IsNullOrWhiteSpace(agent.Email))
            textLines.Add($"Email: {agent.Email}");
        if (!string.IsNullOrWhiteSpace(agent.Npn))
            textLines.Add($"NPN: {agent.Npn}");

        textLines.Add("Legend");

        var textBody = string.Join("\n", textLines);

        var sent = await _emailSender.TrySendAsync(
            toEmail,
            subject,
            htmlBody,
            textBody,
            fromDisplayName: BuildFromDisplayName(agent),
            replyToEmail: agent.Email);

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

    private async Task<AgentInvitationContact> ResolveAgentContactAsync(
        string? ownerAgentUserId,
        string? createdByAgentUserId,
        CancellationToken cancellationToken)
    {
        var agentIds = new[] { ownerAgentUserId, createdByAgentUserId }
            .Select(TrimToNull)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (agentIds.Length == 0)
            return new AgentInvitationContact(null, null, null, null, null);

        var profile = await _db.AgentProfiles
            .AsNoTracking()
            .Where(x => agentIds.Contains(x.AgentUserId))
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (profile is null)
            return new AgentInvitationContact(null, null, null, null, null);

        return new AgentInvitationContact(
            TrimToNull(profile.FullName),
            TrimToNull(profile.Title),
            FirstNonEmpty(TrimToNull(profile.NormalizedEmail), TrimToNull(profile.AgentUpn)),
            TrimToNull(profile.Phone),
            TrimToNull(profile.Npn));
    }

    private static string BuildClientDisplayName(ClientProfile client)
    {
        var displayName = string.Join(" ", new[] { client.FirstName, client.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        return string.IsNullOrWhiteSpace(displayName) ? "there" : displayName;
    }

    private static string BuildFromDisplayName(AgentInvitationContact agent)
    {
        var name = FirstNonEmpty(agent.FullName, agent.Email);
        return string.IsNullOrWhiteSpace(name) ? "Legend Client Access" : $"{name} | Legend";
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var trimmed = TrimToNull(value);
            if (!string.IsNullOrWhiteSpace(trimmed))
                return trimmed;
        }

        return null;
    }

    private static string? TrimToNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string HtmlEncode(string value) => System.Net.WebUtility.HtmlEncode(value);
}
