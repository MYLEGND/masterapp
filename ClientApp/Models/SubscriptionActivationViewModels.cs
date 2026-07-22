using System.ComponentModel.DataAnnotations;

namespace ClientApp.Models;

public sealed class SubscriptionActivationPageViewModel
{
    public string Token { get; set; } = string.Empty;
    public string ReturnUrl { get; set; } = "/profile";
    public string ClientName { get; set; } = "Client";
    public string ClientEmail { get; set; } = string.Empty;
    public string MonthlyAmountDisplay { get; set; } = "$0.00";
    public string Currency { get; set; } = "USD";
    public string BillingAnchorLabel { get; set; } = "Scheduled monthly";
    public string FirstChargeDateDisplay { get; set; } = string.Empty;
    public string FirstRecurringRenewalDateDisplay { get; set; } = string.Empty;
    public string BillingTimeZoneLabel { get; set; } = "UTC";
    public string? ErrorMessage { get; set; }
    public string? StatusMessage { get; set; }
    public bool BrowserPaymentReady { get; set; }
    public string BrowserPaymentSetupMessage { get; set; } = string.Empty;
    public string SquareApplicationId { get; set; } = string.Empty;
    public string SquareLocationId { get; set; } = string.Empty;
    public string SquareEnvironment { get; set; } = "Sandbox";
}

public sealed class SubscriptionActivationPaymentInput
{
    [Required]
    public string SourceId { get; set; } = string.Empty;

    [Required(ErrorMessage = "Cardholder name is required.")]
    public string CardholderName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Billing address is required.")]
    public string BillingAddressLine1 { get; set; } = string.Empty;

    public string? BillingAddressLine2 { get; set; }

    [Required(ErrorMessage = "Billing city is required.")]
    public string BillingCity { get; set; } = string.Empty;

    [Required(ErrorMessage = "Billing state is required.")]
    public string BillingState { get; set; } = string.Empty;

    [Required(ErrorMessage = "Billing ZIP code is required.")]
    public string BillingPostalCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "Billing country is required.")]
    public string BillingCountryCode { get; set; } = "US";

    public string ReturnUrl { get; set; } = "/profile";

    [Range(typeof(bool), "true", "true", ErrorMessage = "Authorization is required.")]
    public bool BillingAuthorizationAccepted { get; set; }
}

public sealed class SubscriptionActivationNoticeViewModel
{
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string ReturnUrl { get; set; } = "/";
}

public sealed class ClientSubscriptionManagementViewModel
{
    public string ClientName { get; set; } = "Client";
    public string MonthlyAmountDisplay { get; set; } = "$0.00";
    public string SubscriptionStatus { get; set; } = "Unknown";
    public string PaymentStanding { get; set; } = "Unknown";
    public string EntitlementStatus { get; set; } = "Unknown";
    public string NextBillingDateDisplay { get; set; } = "Not scheduled";
    public string CurrentPeriodDisplay { get; set; } = "Not available";
    public string CancellationState { get; set; } = "Active";
    public string PaymentRepairInstructions { get; set; } = "Contact your agent if you need help updating billing.";
    public bool CanCancelAtPeriodEnd { get; set; }
    public string ReturnUrl { get; set; } = "/profile";
}
