using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace ClientApp.Models
{
    public class HouseholdChildViewModel
    {
        public Guid? Id { get; set; }

        public string? FirstName { get; set; }

        public string? LastName { get; set; }

        [DataType(DataType.Date)]
        public DateTime? DOB { get; set; }

        [EmailAddress(ErrorMessage = "Enter a valid email address.")]
        public string? Email { get; set; }

        public string? Phone { get; set; }
    }

    public class EditClientViewModel : IValidatableObject
    {
        [Required]
        public string ClientUserId { get; set; } = "";

        [Required(ErrorMessage = "First name is required.")]
        public string FirstName { get; set; } = "";

        [Required(ErrorMessage = "Last name is required.")]
        public string LastName { get; set; } = "";

        [Required(ErrorMessage = "Email is required.")]
        [EmailAddress(ErrorMessage = "Enter a valid email address.")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Phone is required.")]
        public string Phone { get; set; } = "";

        [Required(ErrorMessage = "Marital status is required.")]
        public string MaritalStatus { get; set; } = "";

        // DOB is display-only (does not change)
        public DateTime? DOB { get; set; }

        // Optional: keep SO editable later, but not required now.
        public string? SignificantOtherFirstName { get; set; }
        public string? SignificantOtherLastName { get; set; }
        public DateTime? SignificantOtherDOB { get; set; }
        [EmailAddress(ErrorMessage = "Enter a valid email address.")]
        public string? SignificantOtherEmail { get; set; }
        public string? SignificantOtherPhone { get; set; }

        public List<HouseholdChildViewModel> Children { get; set; } = new();

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            // ✅ Valid phone (US-friendly). Accepts (xxx) xxx-xxxx, xxx-xxx-xxxx, +1..., etc.
            // Store whatever you want; this prevents garbage.
            var p = (Phone ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(p))
            {
                // allow digits, spaces, (), -, +, .
                if (!Regex.IsMatch(p, @"^[0-9\+\-\(\)\s\.]{7,25}$"))
                {
                    yield return new ValidationResult("Enter a valid phone number.", new[] { nameof(Phone) });
                }
            }

            // Keep SO validation consistent with create if you ever choose to allow edits:
            bool needsSO =
                string.Equals(MaritalStatus, "Married", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(MaritalStatus, "Domestic Partnership", StringComparison.OrdinalIgnoreCase);

            if (needsSO)
            {
                if (string.IsNullOrWhiteSpace(SignificantOtherFirstName))
                    yield return new ValidationResult("Significant other first name is required for this marital status.",
                        new[] { nameof(SignificantOtherFirstName) });

                if (string.IsNullOrWhiteSpace(SignificantOtherLastName))
                    yield return new ValidationResult("Significant other last name is required for this marital status.",
                        new[] { nameof(SignificantOtherLastName) });

                if (SignificantOtherDOB == null)
                    yield return new ValidationResult("Significant other date of birth is required for this marital status.",
                        new[] { nameof(SignificantOtherDOB) });
            }

            for (var i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                var hasAnyChildData =
                    !string.IsNullOrWhiteSpace(child.FirstName) ||
                    !string.IsNullOrWhiteSpace(child.LastName) ||
                    child.DOB.HasValue ||
                    !string.IsNullOrWhiteSpace(child.Email) ||
                    !string.IsNullOrWhiteSpace(child.Phone);

                if (!hasAnyChildData)
                    continue;

                if (string.IsNullOrWhiteSpace(child.FirstName))
                {
                    yield return new ValidationResult(
                        "Child first name is required when adding a child.",
                        new[] { $"Children[{i}].FirstName" });
                }
            }
        }
    }
}
