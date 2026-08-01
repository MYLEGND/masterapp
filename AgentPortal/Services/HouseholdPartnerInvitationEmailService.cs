using System.Net;
using Domain.Entities;

namespace AgentPortal.Services;

/// <summary>
/// Delivery adapter for an already-authoritative household invitation. It has
/// no membership, billing, or Entra logic; those remain in Infrastructure.
/// </summary>
public sealed class HouseholdPartnerInvitationEmailService
{
    private readonly IConfiguration _configuration;
    private readonly IEmailSender _emailSender;

    public HouseholdPartnerInvitationEmailService(
        IConfiguration configuration,
        IEmailSender emailSender)
    {
        _configuration = configuration;
        _emailSender = emailSender;
    }

    public async Task SendAsync(
        HouseholdMemberInvitation invitation,
        string plainTextToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(invitation.IntendedNormalizedEmail) ||
            string.IsNullOrWhiteSpace(plainTextToken))
        {
            throw new InvalidOperationException("A partner email and invitation token are required.");
        }

        var baseUrl = (_configuration["ClientPortal:BaseUrl"] ??
                       _configuration["ClientPortal__BaseUrl"] ??
                       _configuration["Provisioning:ClientPortalBaseUrl"] ??
                       _configuration["Provisioning__ClientPortalBaseUrl"] ??
                       "https://client.mylegnd.com").TrimEnd('/');
        var acceptanceUrl = $"{baseUrl}/household/partner/accept?token={Uri.EscapeDataString(plainTextToken)}";
        var name = WebUtility.HtmlEncode(
            $"{invitation.InvitedFirstName} {invitation.InvitedLastName}".Trim());
        var safeUrl = WebUtility.HtmlEncode(acceptanceUrl);
        var expires = invitation.ExpiresUtc.ToString("MMMM d, yyyy");
        var html = $"""
<p>Hi {name},</p>
<p>You have been invited to join a Legend household account. Your own sign-in, profile, photos, social activity, and Journey settings remain private to you. Shared household financial information is available only while the household subscription is active.</p>
<p><a href="{safeUrl}">Accept your household invitation</a></p>
<p>This invitation expires {WebUtility.HtmlEncode(expires)}. You will not be asked to start a separate subscription.</p>
""";
        var plainText = $"Hi {invitation.InvitedFirstName}, accept your Legend household invitation: {acceptanceUrl}. This invitation expires {expires}. You will not start a separate subscription.";

        var sent = await _emailSender.TrySendAsync(
            invitation.IntendedNormalizedEmail,
            "Join your Legend household account",
            html,
            plainText);
        if (!sent)
            throw new InvalidOperationException("The partner invitation email could not be sent.");
    }
}
