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
            : $"""<div style="margin:4px 0 0;color:#66758c;font-size:14px;">{safeAgentTitle}</div>""";
        var agentPhoneHtml = string.IsNullOrWhiteSpace(agent.Phone)
            ? string.Empty
            : $"""<div style="margin:10px 0 0;"><span style="display:inline-block;min-width:56px;color:#2e5fa9;font-weight:700;">Phone</span><a href="tel:{safeAgentPhone}" style="color:#0d2145;text-decoration:none;font-weight:700;">{safeAgentPhone}</a></div>""";
        var agentEmailHtml = string.IsNullOrWhiteSpace(agent.Email)
            ? string.Empty
            : $"""<div style="margin:8px 0 0;"><span style="display:inline-block;min-width:56px;color:#2e5fa9;font-weight:700;">Email</span><a href="mailto:{safeAgentEmail}" style="color:#0d2145;text-decoration:none;font-weight:700;">{safeAgentEmail}</a></div>""";
        var agentNpnHtml = string.IsNullOrWhiteSpace(agent.Npn)
            ? string.Empty
            : $"""<div style="margin:8px 0 0;"><span style="display:inline-block;min-width:56px;color:#2e5fa9;font-weight:700;">NPN</span><span style="color:#0d2145;font-weight:700;">{safeAgentNpn}</span></div>""";
        var replyCopy = string.IsNullOrWhiteSpace(agent.Email)
            ? "If you need help, contact your Legend agent before your invitation expires."
            : "If you need help, reply directly to this email and your agent will pick it up.";

        var subject = "Activate your Legend Client Portal access";
        var htmlBody = $"""
<table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="margin:0;padding:0;background:#ffffff;font-family:Arial,sans-serif;color:#14213a;">
  <tr>
    <td align="center" style="padding:28px 14px;background:#ffffff;">
      <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="max-width:660px;background:#ffffff;border:1px solid #d7e0ee;border-radius:18px;overflow:hidden;">
        <tr>
          <td style="padding:0;background:#0d2145;">
            <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%">
              <tr>
                <td style="padding:24px 28px 8px 28px;">
                  <div style="color:#cbdcff;font-size:12px;font-weight:800;letter-spacing:1.4px;text-transform:uppercase;">
                    Legend Client Portal
                  </div>
                </td>
              </tr>
              <tr>
                <td style="padding:0 28px 24px 28px;color:#ffffff;">
                  <div style="font-size:30px;line-height:1.12;font-weight:800;">Your client access is ready</div>
                  <div style="margin-top:10px;font-size:16px;line-height:1.55;color:#d8e4fa;">
                    Hi {safeName}, activate your subscription using <strong style="color:#ffffff;">{safeSignInEmail}</strong>.
                  </div>
                </td>
              </tr>
            </table>
          </td>
        </tr>

        <tr>
          <td style="padding:22px 28px 0 28px;background:#ffffff;">
            <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="border:1px solid #dbe5f4;border-radius:12px;background:#f6f8fc;">
              <tr>
                <td style="padding:18px 20px;">
                  <div style="font-size:12px;font-weight:800;letter-spacing:1.2px;text-transform:uppercase;color:#2e5fa9;">Subscription</div>
                  <div style="margin-top:8px;font-size:32px;line-height:1;font-weight:800;color:#0d2145;">{safeAmount}<span style="font-size:16px;font-weight:700;color:#66758c;"> / month</span></div>
                  <div style="margin-top:12px;font-size:15px;line-height:1.55;color:#33445f;"><strong>Billing day:</strong> {safeAnchor}</div>
                  <div style="font-size:15px;line-height:1.55;color:#33445f;"><strong>Activation link expires:</strong> {safeInvitationExpires}</div>
                </td>
              </tr>
            </table>
          </td>
        </tr>

        <tr>
          <td align="center" style="padding:22px 28px 0 28px;background:#ffffff;">
            <a href="{safeActivationUrl}" target="_blank" style="display:inline-block;padding:14px 24px;background:#0d2145;border:1px solid #0d2145;border-radius:10px;color:#ffffff;text-decoration:none;font-size:16px;font-weight:800;">
              Activate access
            </a>
          </td>
        </tr>

        <tr>
          <td style="padding:20px 28px 0 28px;background:#ffffff;">
            <div style="font-size:15px;line-height:1.55;color:#506078;">
              After activation, sign in at <a href="{safeSignInUrl}" target="_blank" style="color:#0d2145;font-weight:700;text-decoration:none;">Legend Client Portal</a>.
            </div>
            <div style="margin-top:8px;font-size:15px;line-height:1.55;color:#506078;">{replyCopy}</div>
          </td>
        </tr>

        <tr>
          <td style="padding:22px 28px 26px 28px;background:#ffffff;">
            <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="border-top:1px solid #dbe5f4;">
              <tr>
                <td style="padding-top:18px;">
                  <div style="font-size:12px;font-weight:800;letter-spacing:1.2px;text-transform:uppercase;color:#2e5fa9;">Your Agent</div>
                  <div style="margin-top:8px;font-size:21px;font-weight:800;color:#0d2145;">{safeAgentName}</div>
                  {agentTitleHtml}
                  {agentPhoneHtml}
                  {agentEmailHtml}
                  {agentNpnHtml}
                  <div style="margin-top:16px;font-size:14px;font-weight:800;color:#0d2145;">Legend™</div>
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
            "Your Legend Client Portal access is ready.",
            $"Monthly amount: {monthlyAmount}",
            $"Billing day: {DescribeBillingAnchor(offer)}",
            $"Invitation expires: {invitationExpires}",
            $"Activate subscription: {activationUrl}",
            $"After activation, sign in here: {signInUrl}",
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
            _ => "Scheduled monthly"
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
