using System.Globalization;
using ClientApp.Models;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Billing.Square;
using Infrastructure.Data;
using Infrastructure.Households;
using Microsoft.EntityFrameworkCore;

namespace ClientApp.Services;

public enum SubscriptionActivationAvailability
{
    Ready = 0,
    Expired = 1,
    Unavailable = 2,
    AlreadyActivated = 3
}

public sealed record SubscriptionActivationContextResult(
    SubscriptionActivationAvailability Availability,
    string? Message,
    SubscriptionActivationInvitation? Invitation = null,
    ClientProfile? Client = null,
    ClientSubscriptionOffer? Offer = null,
    ClientSubscription? Subscription = null,
    ClientSubscriptionActivationSchedule? Schedule = null);

public sealed record SubscriptionActivationExecutionResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedMessage,
    SubscriptionActivationContextResult Context,
    string? ProtectedContinuationState = null,
    DateTime? ContinuationExpiresUtc = null);

public sealed class SubscriptionActivationService
{
    private readonly MasterAppDbContext _db;
    private readonly IBillingOrchestrator _billingOrchestrator;
    private readonly IClientSubscriptionActivationPolicyService _activationPolicyService;
    private readonly SquareBillingOptions _squareOptions;
    private readonly ClientIdentityContinuationService _continuationService;
    private readonly ClientAppReturnUrlNormalizer _returnUrlNormalizer;
    private readonly global::Infrastructure.Identity.IClientEntraLifecycleService _entraLifecycle;
    private readonly IHouseholdMembershipService _households;

    public SubscriptionActivationService(
        MasterAppDbContext db,
        IBillingOrchestrator billingOrchestrator,
        IClientSubscriptionActivationPolicyService activationPolicyService,
        SquareBillingOptions squareOptions,
        ClientIdentityContinuationService continuationService,
        ClientAppReturnUrlNormalizer returnUrlNormalizer,
        global::Infrastructure.Identity.IClientEntraLifecycleService entraLifecycle,
        IHouseholdMembershipService households)
    {
        _db = db;
        _billingOrchestrator = billingOrchestrator;
        _activationPolicyService = activationPolicyService;
        _squareOptions = squareOptions;
        _continuationService = continuationService;
        _returnUrlNormalizer = returnUrlNormalizer;
        _entraLifecycle = entraLifecycle;
        _households = households;
    }

    public bool BrowserPaymentReady => _squareOptions.HasBrowserCredentials();
    public string SquareApplicationId => _squareOptions.ApplicationId ?? string.Empty;
    public string SquareLocationId => _squareOptions.LocationId ?? string.Empty;
    public string SquareEnvironment => _squareOptions.Environment == BillingProviderEnvironment.Production ? "Production" : "Sandbox";

    public async Task<SubscriptionActivationContextResult> GetContextAsync(string token, CancellationToken cancellationToken = default)
    {
        var invitation = await FindInvitationAsync(token, cancellationToken);
        if (invitation is null)
            return new SubscriptionActivationContextResult(SubscriptionActivationAvailability.Unavailable, "This activation link is not available.");

        var nowUtc = DateTime.UtcNow;
        if (invitation.ExpiresUtc <= nowUtc &&
            invitation.Status is not SubscriptionActivationInvitationStatus.Redeemed and not SubscriptionActivationInvitationStatus.Revoked and not SubscriptionActivationInvitationStatus.Superseded)
        {
            invitation.Status = SubscriptionActivationInvitationStatus.Expired;
            await _db.SaveChangesAsync(cancellationToken);
        }

        if (invitation.Status == SubscriptionActivationInvitationStatus.Expired)
            return new SubscriptionActivationContextResult(SubscriptionActivationAvailability.Expired, "This activation link has expired.", invitation, invitation.ClientProfile, invitation.ClientSubscriptionOffer);

        if (invitation.Status is SubscriptionActivationInvitationStatus.Revoked or SubscriptionActivationInvitationStatus.Superseded)
            return new SubscriptionActivationContextResult(SubscriptionActivationAvailability.Unavailable, "This activation link is no longer available.", invitation, invitation.ClientProfile, invitation.ClientSubscriptionOffer);

        var latestSubscription = await _db.ClientSubscriptions
            .Where(x => x.ClientProfileId == invitation.ClientProfileId)
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (invitation.Status == SubscriptionActivationInvitationStatus.Redeemed)
        {
            return new SubscriptionActivationContextResult(
                SubscriptionActivationAvailability.AlreadyActivated,
                "This subscription has already been activated. Use the normal client sign-in page to continue.",
                invitation,
                invitation.ClientProfile,
                invitation.ClientSubscriptionOffer,
                latestSubscription);
        }

        if (invitation.ClientProfile is null || invitation.ClientSubscriptionOffer is null)
            return new SubscriptionActivationContextResult(SubscriptionActivationAvailability.Unavailable, "The activation details could not be loaded.");

        if (invitation.Status is SubscriptionActivationInvitationStatus.Pending or SubscriptionActivationInvitationStatus.Sent)
        {
            invitation.Status = SubscriptionActivationInvitationStatus.Viewed;
            invitation.ViewedUtc ??= nowUtc;
            await _db.SaveChangesAsync(cancellationToken);
        }

        var schedule = _activationPolicyService.ResolveActivationSchedule(invitation.ClientSubscriptionOffer, nowUtc);
        return new SubscriptionActivationContextResult(
            SubscriptionActivationAvailability.Ready,
            null,
            invitation,
            invitation.ClientProfile,
            invitation.ClientSubscriptionOffer,
            latestSubscription,
            schedule);
    }

    public async Task<SubscriptionActivationExecutionResult> ActivateAsync(string token, SubscriptionActivationPaymentInput input, CancellationToken cancellationToken = default)
    {
        var context = await GetContextAsync(token, cancellationToken);
        if (context.Availability != SubscriptionActivationAvailability.Ready ||
            context.Invitation is null ||
            context.Client is null ||
            context.Offer is null ||
            context.Schedule is null)
        {
            return new SubscriptionActivationExecutionResult(false, "ACTIVATION_UNAVAILABLE", context.Message ?? "This activation flow is not available.", context);
        }

        var isZeroDollarSubscription = context.Offer.MonthlyAmountCents == 0;
        if (!input.BillingAuthorizationAccepted)
            return new SubscriptionActivationExecutionResult(false, "ACKNOWLEDGEMENT_REQUIRED", "Please acknowledge the membership activation before continuing.", context);

        if (!isZeroDollarSubscription && !HasCompletePaymentInput(input))
            return new SubscriptionActivationExecutionResult(false, "PAYMENT_DETAILS_REQUIRED", "Complete the required payment details before continuing.", context);

        var activationResult = await _billingOrchestrator.ActivateClientSubscriptionAsync(
            new ActivateClientSubscriptionCommand(
                context.Client.Id,
                context.Offer.Id,
                context.Offer.OwnerAgentUserId,
                input.SourceId?.Trim() ?? string.Empty,
                context.Offer.Currency,
                context.Schedule.BillingAnchorDay,
                context.Schedule.BillingTimeZoneId,
                context.Schedule.FirstChargeUtc,
                context.Schedule.FirstRecurringRenewalUtc,
                context.Schedule.FirstRecurringRenewalLocalDate,
                input.BillingAuthorizationAccepted,
                input.BillingAuthorizationAccepted,
                input.BillingAuthorizationAccepted,
                context.Invitation.IntendedNormalizedEmail,
                context.Invitation.Id,
                context.Subscription?.ProviderCustomerId,
                input.CardholderName?.Trim() ?? string.Empty,
                BillingCorrelationId(context.Invitation.Id, context.Schedule.BillingAnchorDay),
                BillingIdempotencyKey(context.Invitation.Id, context.Schedule.BillingAnchorDay),
                BuildBillingAddress(input)),
            cancellationToken);

        if (!activationResult.Success || activationResult.Subscription is null)
        {
            return new SubscriptionActivationExecutionResult(
                false,
                activationResult.SafeErrorCode,
                activationResult.SanitizedSummary ?? "The subscription could not be activated yet.",
                context);
        }

        try
        {
            await _entraLifecycle.EnsureClientIdentityAsync(
                context.Client.Id,
                cancellationToken);
            await _households.EnsurePrimaryHouseholdActiveAsync(
                context.Client.Id,
                cancellationToken);
        }
        catch (Exception ex)
        {
            return new SubscriptionActivationExecutionResult(
                false,
                "ENTRA_PROVISIONING_FAILED",
                $"The subscription is active, but identity provisioning could not complete: {ex.Message}",
                context with { Subscription = activationResult.Subscription });
        }

        var continuation = await _continuationService.CreateProtectedStateAsync(
            context.Client.Id,
            context.Invitation.IntendedNormalizedEmail,
            _returnUrlNormalizer.Normalize(input.ReturnUrl),
            ClientIdentityContinuationPurpose.Activation,
            context.Invitation.Id,
            activationResult.Subscription.Id,
            cancellationToken);

        var completedContext = context with { Subscription = activationResult.Subscription };
        return new SubscriptionActivationExecutionResult(
            true,
            null,
            "Subscription activated successfully.",
            completedContext,
            continuation.ProtectedState,
            continuation.ExpiresUtc);
    }

    public SubscriptionActivationPageViewModel BuildPageViewModel(SubscriptionActivationContextResult context, string token, string returnUrl, string? errorMessage = null)
    {
        if (context.Client is null || context.Offer is null || context.Schedule is null)
            throw new InvalidOperationException("Activation page models can only be created from a ready activation context.");

        return new SubscriptionActivationPageViewModel
        {
            Token = token,
            ReturnUrl = _returnUrlNormalizer.Normalize(returnUrl),
            ClientName = $"{context.Client.FirstName} {context.Client.LastName}".Trim(),
            ClientEmail = context.Client.Email,
            MonthlyAmountCents = context.Schedule.MonthlyAmountCents,
            MonthlyAmountDisplay = (context.Schedule.MonthlyAmountCents / 100m).ToString("C", CultureInfo.GetCultureInfo("en-US")),
            Currency = context.Schedule.Currency,
            BillingAnchorLabel = BuildAnchorLabel(context.Schedule.BillingAnchorDay),
            FirstChargeDateDisplay = FormatDate(context.Schedule.FirstChargeUtc, context.Schedule.BillingTimeZoneId),
            FirstRecurringRenewalDateDisplay = FormatDate(context.Schedule.FirstRecurringRenewalUtc, context.Schedule.BillingTimeZoneId),
            BillingTimeZoneLabel = context.Schedule.BillingTimeZoneId,
            ErrorMessage = errorMessage,
            StatusMessage = context.Message,
            BrowserPaymentReady = BrowserPaymentReady,
            BrowserPaymentSetupMessage = BuildBrowserPaymentSetupMessage(),
            SquareApplicationId = SquareApplicationId,
            SquareLocationId = SquareLocationId,
            SquareEnvironment = SquareEnvironment
        };
    }

    private async Task<SubscriptionActivationInvitation?> FindInvitationAsync(string token, CancellationToken cancellationToken)
    {
        var tokenHash = Hash(token);
        return await _db.SubscriptionActivationInvitations
            .Include(x => x.ClientProfile)
            .Include(x => x.ClientSubscriptionOffer)
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);
    }

    private static string FormatDate(DateTime utc, string timeZoneId)
    {
        var timeZone = ResolveTimeZone(timeZoneId);
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone)
            .ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
    }

    private static string BuildAnchorLabel(int? day)
    {
        return day switch
        {
            1 => "1st of each month",
            15 => "15th of each month",
            int value => $"Day {value} of each month",
            _ => "Scheduled monthly"
        };
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static string Hash(string value)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string BillingCorrelationId(Guid invitationId, int? billingAnchorDay)
    {
        return $"client-activation-{invitationId:N}-{billingAnchorDay?.ToString(CultureInfo.InvariantCulture) ?? "default"}";
    }

    private static string BillingIdempotencyKey(Guid invitationId, int? billingAnchorDay)
    {
        return $"client-activation-{invitationId:N}-{billingAnchorDay?.ToString(CultureInfo.InvariantCulture) ?? "default"}";
    }

    private static BillingPostalAddress BuildBillingAddress(SubscriptionActivationPaymentInput input)
    {
        return new BillingPostalAddress(
            TrimToNull(input.BillingAddressLine1),
            TrimToNull(input.BillingAddressLine2),
            TrimToNull(input.BillingCity),
            TrimToNull(input.BillingState),
            TrimToNull(input.BillingPostalCode),
            NormalizeCountry(input.BillingCountryCode));
    }

    private static bool HasCompletePaymentInput(SubscriptionActivationPaymentInput input)
    {
        return !string.IsNullOrWhiteSpace(input.SourceId) &&
               !string.IsNullOrWhiteSpace(input.CardholderName) &&
               !string.IsNullOrWhiteSpace(input.BillingAddressLine1) &&
               !string.IsNullOrWhiteSpace(input.BillingCity) &&
               !string.IsNullOrWhiteSpace(input.BillingState) &&
               !string.IsNullOrWhiteSpace(input.BillingPostalCode) &&
               !string.IsNullOrWhiteSpace(input.BillingCountryCode);
    }

    private string BuildBrowserPaymentSetupMessage()
    {
        if (BrowserPaymentReady)
            return string.Empty;

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(_squareOptions.ApplicationId))
            missing.Add("Square application ID");
        if (string.IsNullOrWhiteSpace(_squareOptions.LocationId))
            missing.Add("Square location ID");

        return missing.Count == 0
            ? "Secure card setup is not available yet."
            : $"Secure card setup is missing: {string.Join(", ", missing)}.";
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeCountry(string? value)
    {
        var normalized = TrimToNull(value);
        return string.IsNullOrWhiteSpace(normalized) ? "US" : normalized.ToUpperInvariant();
    }
}
