using System;
using System.ComponentModel.DataAnnotations;

namespace Protect_Website.Models;

// An explicit unlicensed answer has no license identifiers. All other statuses,
// including missing or unknown values, retain the existing required-field rule.
[AttributeUsage(AttributeTargets.Property)]
public sealed class RequiredWhenLicensedAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context)
    {
        var status = context.ObjectType.GetProperty("DLStatus")?.GetValue(context.ObjectInstance) as string;
        if (string.Equals(status, "Not Licensed", StringComparison.Ordinal))
            return ValidationResult.Success;
        return new RequiredAttribute().IsValid(value)
            ? ValidationResult.Success
            : new ValidationResult($"The {context.DisplayName} field is required.",
                context.MemberName is null ? null : new[] { context.MemberName });
    }
}
