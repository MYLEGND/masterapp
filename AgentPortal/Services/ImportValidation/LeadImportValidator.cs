using System.Text.RegularExpressions;

namespace AgentPortal.Services.ImportValidation;

/// <summary>
/// Lightweight, additive validator for lead imports. When the feature flag is off, controller logic remains unchanged.
/// </summary>
public sealed class LeadImportValidator
{
    private static readonly Regex PhoneDigits = new("[^0-9]", RegexOptions.Compiled);

    public LeadImportValidationResult Validate(string? firstName, string? lastName, string? email, string? phone)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(firstName) || firstName!.Trim().Length < 2)
            errors.Add("First name is required (min 2 chars).");

        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phone))
            errors.Add("Either email or phone is required.");

        if (!string.IsNullOrWhiteSpace(email) && !IsPlausibleEmail(email!))
            errors.Add("Email format looks invalid.");

        if (!string.IsNullOrWhiteSpace(phone) && !IsPlausiblePhone(phone!))
            errors.Add("Phone must have at least 10 digits.");

        return new LeadImportValidationResult(errors.Count == 0, errors);
    }

    private static bool IsPlausibleEmail(string email)
    {
        return email.Contains("@") && email.Contains(".") && email.Length <= 320;
    }

    private static bool IsPlausiblePhone(string phone)
    {
        var digits = PhoneDigits.Replace(phone, "");
        return digits.Length >= 10 && digits.Length <= 15;
    }
}

public sealed record LeadImportValidationResult(bool IsValid, IReadOnlyList<string> Errors);
