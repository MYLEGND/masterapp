using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Domain.Billing;
using Domain.Enums;

namespace AgentPortal.Models
{
    public class CreateClientViewModel : IValidatableObject
    {
        [Required(ErrorMessage = "Choose whether this is a lead, client, or business client.")]
        public string RecordType { get; set; } = "Lead";

        // Required only for Client and Business Client. Leads stay CRM-only.
        public string? AccountManagementMode { get; set; }

        public string? FirstName { get; set; }

        public string? LastName { get; set; }

        [EmailAddress(ErrorMessage = "Enter a valid email address.")]
        public string? Email { get; set; }

        [Phone(ErrorMessage = "Enter a valid phone number.")]
        public string? Phone { get; set; }

        [DataType(DataType.Date)]
        public DateTime? DOB { get; set; }

        public string? MaritalStatus { get; set; }

        // ===================== SIGNIFICANT OTHER (UNDER SAME PROFILE) =====================
        public string? SignificantOtherFirstName { get; set; }
        public string? SignificantOtherLastName { get; set; }

        [DataType(DataType.Date)]
        public DateTime? SignificantOtherDOB { get; set; }

        [EmailAddress(ErrorMessage = "Enter a valid email address.")]
        public string? SignificantOtherEmail { get; set; }

        public string? SignificantOtherPhone { get; set; }

        // Agent licensing (persisted per-agent and reused)
        public string? AgentNpn { get; set; }
        [Phone(ErrorMessage = "Enter a valid agent phone number.")]
        [StringLength(64)]
        public string? AgentPhone { get; set; }

        public string? OneTimePassword { get; set; }
        public string? SourceLeadClientUserId { get; set; }
        public string? SourceWorkstationLeadId { get; set; }
        public string? SubscriptionPriceType { get; set; }
        public decimal? SubscriptionCustomMonthlyAmount { get; set; }
        public string SubscriptionCurrency { get; set; } = "USD";
        public string SubscriptionBillingAnchorMode { get; set; } = nameof(BillingAnchorSelectionMode.FirstOfMonth);
        public int? SubscriptionBillingAnchorDay { get; set; }
        public bool? SubscriptionHasFreeTrial { get; set; }
        public int? SubscriptionFreeTrialDays { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            var recordType = (RecordType ?? "").Trim();
            var isPortalClient =
                string.Equals(recordType, "Client", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(recordType, "BusinessClient", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(recordType, "Business Client", StringComparison.OrdinalIgnoreCase);

            if (!string.Equals(recordType, "Lead", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(recordType, "Client", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(recordType, "BusinessClient", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(recordType, "Business Client", StringComparison.OrdinalIgnoreCase))
            {
                yield return new ValidationResult(
                    "Record type must be Lead, Client, or Business Client.",
                    new[] { nameof(RecordType) });
            }

            if (isPortalClient)
            {
                if (!ClientAccountManagementModes.IsValid(AccountManagementMode))
                    yield return new ValidationResult(
                        "Choose Shared Account or Self Managed for this client.",
                        new[] { nameof(AccountManagementMode) });

                if (string.IsNullOrWhiteSpace(FirstName))
                    yield return new ValidationResult(
                        "First name is required for a client.",
                        new[] { nameof(FirstName) });

                if (string.IsNullOrWhiteSpace(LastName))
                    yield return new ValidationResult(
                        "Last name is required for a client.",
                        new[] { nameof(LastName) });

                if (string.IsNullOrWhiteSpace(Email))
                    yield return new ValidationResult(
                        "Email is required for a client or business client.",
                        new[] { nameof(Email) });
            }

            bool needsSO =
                string.Equals(MaritalStatus, "Married", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(MaritalStatus, "Domestic Partnership", StringComparison.OrdinalIgnoreCase);

            if (isPortalClient && needsSO)
            {
                if (string.IsNullOrWhiteSpace(SignificantOtherFirstName))
                    yield return new ValidationResult(
                        "Significant other first name is required for this marital status.",
                        new[] { nameof(SignificantOtherFirstName) });

                if (string.IsNullOrWhiteSpace(SignificantOtherLastName))
                    yield return new ValidationResult(
                        "Significant other last name is required for this marital status.",
                        new[] { nameof(SignificantOtherLastName) });

                if (SignificantOtherDOB == null)
                    yield return new ValidationResult(
                        "Significant other date of birth is required for this marital status.",
                        new[] { nameof(SignificantOtherDOB) });
            }

            if (!string.IsNullOrWhiteSpace(OneTimePassword) && OneTimePassword.Trim().Length < 8)
                yield return new ValidationResult(
                    "Password must be at least 8 characters.",
                    new[] { nameof(OneTimePassword) });

            if (isPortalClient)
            {
                if (!Enum.TryParse<ClientSubscriptionOfferPriceType>(SubscriptionPriceType?.Trim(), ignoreCase: true, out var priceType))
                {
                    yield return new ValidationResult(
                        "Choose a valid subscription tier.",
                        new[] { nameof(SubscriptionPriceType) });
                }
                else if (priceType == ClientSubscriptionOfferPriceType.Custom)
                {
                    if (!SubscriptionCustomMonthlyAmount.HasValue)
                    {
                        yield return new ValidationResult(
                            "Custom monthly amount is required when Custom is selected.",
                            new[] { nameof(SubscriptionCustomMonthlyAmount) });
                    }
                    else
                    {
                        var customAmount = SubscriptionCustomMonthlyAmount.Value;
                        if (decimal.Round(customAmount, 2) != customAmount)
                        {
                            yield return new ValidationResult(
                                "Custom monthly amount must use no more than 2 decimal places.",
                                new[] { nameof(SubscriptionCustomMonthlyAmount) });
                        }

                        var customCents = decimal.ToInt32(decimal.Round(customAmount * 100m, 0, MidpointRounding.AwayFromZero));
                        if (customCents < ClientSubscriptionOfferPricing.FounderCustomMinimumCents ||
                            customCents > ClientSubscriptionOfferPricing.CustomMaximumCents)
                        {
                            yield return new ValidationResult(
                                $"Custom monthly amount must be between {(ClientSubscriptionOfferPricing.FounderCustomMinimumCents / 100m):0.00} and {(ClientSubscriptionOfferPricing.CustomMaximumCents / 100m):0.00}.",
                                new[] { nameof(SubscriptionCustomMonthlyAmount) });
                        }
                    }
                }

                if (!string.Equals((SubscriptionCurrency ?? string.Empty).Trim(), "USD", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new ValidationResult(
                        "Subscriptions must use USD.",
                        new[] { nameof(SubscriptionCurrency) });
                }

                if (!Enum.TryParse<BillingAnchorSelectionMode>(SubscriptionBillingAnchorMode?.Trim(), ignoreCase: true, out var anchorMode))
                {
                    yield return new ValidationResult(
                        "Choose a valid billing anchor mode.",
                        new[] { nameof(SubscriptionBillingAnchorMode) });
                }
                else if (anchorMode == BillingAnchorSelectionMode.SpecificDayOfMonth)
                {
                    if (!SubscriptionBillingAnchorDay.HasValue || SubscriptionBillingAnchorDay.Value < 1 || SubscriptionBillingAnchorDay.Value > 31)
                    {
                        yield return new ValidationResult(
                            "Agent-selected billing anchors must use a day between 1 and 31.",
                            new[] { nameof(SubscriptionBillingAnchorDay) });
                    }
                }

                if (SubscriptionHasFreeTrial is true &&
                    (!SubscriptionFreeTrialDays.HasValue ||
                     SubscriptionFreeTrialDays.Value < 1 ||
                     SubscriptionFreeTrialDays.Value > ClientSubscriptionTrialPolicy.MaximumFreeTrialDays))
                {
                    yield return new ValidationResult(
                        $"Free trial days must be between 1 and {ClientSubscriptionTrialPolicy.MaximumFreeTrialDays}.",
                        new[] { nameof(SubscriptionFreeTrialDays) });
                }
            }
        }
    }
}
