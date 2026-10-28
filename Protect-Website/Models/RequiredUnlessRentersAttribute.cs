using System;
using System.ComponentModel.DataAnnotations;

namespace Protect_Website.Models;

// These building-owner questions remain visible but optional for the explicit
// renters product. Missing/unknown products retain the existing required rule.
[AttributeUsage(AttributeTargets.Property)]
public sealed class RequiredUnlessRentersAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context)
    {
        var product = context.ObjectType.GetProperty("PolicyFormType")?.GetValue(context.ObjectInstance) as string;
        if (string.Equals(product, "HO4 - Renters", StringComparison.Ordinal))
            return ValidationResult.Success;
        return new RequiredAttribute().IsValid(value)
            ? ValidationResult.Success
            : new ValidationResult($"The {context.DisplayName} field is required.",
                context.MemberName is null ? null : new[] { context.MemberName });
    }
}
