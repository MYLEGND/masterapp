using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace AgentPortal.Models
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

        [Required(ErrorMessage = "Record type is required.")]
        public string RecordType { get; set; } = "Lead";

        public bool HasPortalAccess { get; set; }

        public string FirstName { get; set; } = "";

        public string LastName { get; set; } = "";

        [EmailAddress(ErrorMessage = "Enter a valid email address.")]
        public string Email { get; set; } = "";

        public string Phone { get; set; } = "";

        /// <summary>
        /// Agent NPN (saved on the agent profile and reused on future client edits/creates).
        /// </summary>
        public string? AgentNpn { get; set; }

        [Phone(ErrorMessage = "Enter a valid agent phone number.")]
        [StringLength(64)]
        public string? AgentPhone { get; set; }

        public string MaritalStatus { get; set; } = "";

        public DateTime? DOB { get; set; }
        public bool IsDobLocked { get; set; }

        // ===================== SIGNIFICANT OTHER =====================
        public string? SignificantOtherFirstName { get; set; }
        public string? SignificantOtherLastName { get; set; }

        [DataType(DataType.Date)]
        public DateTime? SignificantOtherDOB { get; set; }

        [EmailAddress(ErrorMessage = "Enter a valid email address.")]
        public string? SignificantOtherEmail { get; set; }

        public string? SignificantOtherPhone { get; set; }

        public List<HouseholdChildViewModel> Children { get; set; } = new();

        // ===================== CRM (DB-BACKED) =====================
        [Required(ErrorMessage = "CRM status is required.")]
        public string CrmStatus { get; set; } = "Active"; // Lead | Prospect | Active | Dormant

        [Required(ErrorMessage = "Priority is required.")]
        public string CrmPriority { get; set; } = "Normal"; // Low | Normal | High | Urgent

        public string? CrmTags { get; set; }

        [DataType(DataType.Date)]
        public DateTime? CrmLastTouch { get; set; }

        [DataType(DataType.Date)]
        public DateTime? CrmNextDate { get; set; }

        public string? CrmNextText { get; set; }

        // Relationship Notes (your UI label)
        public string? CrmNotes { get; set; }

        // Back-compat: if any view/controller still uses AgentNotes, keep it.
        public string? AgentNotes { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            var normalizedRecordType = ClientCrmMetaSerializer.NormalizeRecordType(RecordType);
            var portalEnabledOrRequested = HasPortalAccess || normalizedRecordType is "Client" or "BusinessClient";

            // ✅ Valid phone (US-friendly). Accepts (xxx) xxx-xxxx, xxx-xxx-xxxx, +1..., etc.
            var p = (Phone ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(p))
            {
                if (!Regex.IsMatch(p, @"^[0-9\+\-\(\)\s\.]{7,25}$"))
                    yield return new ValidationResult("Enter a valid phone number.", new[] { nameof(Phone) });
            }

            if (normalizedRecordType is not ("Lead" or "Client" or "BusinessClient"))
            {
                yield return new ValidationResult(
                    "Record type must be Lead, Client, or Business Client.",
                    new[] { nameof(RecordType) });
            }

            if (portalEnabledOrRequested)
            {
                if (string.IsNullOrWhiteSpace(FirstName))
                    yield return new ValidationResult(
                        "First name is required for portal-enabled records.",
                        new[] { nameof(FirstName) });

                if (string.IsNullOrWhiteSpace(LastName))
                    yield return new ValidationResult(
                        "Last name is required for portal-enabled records.",
                        new[] { nameof(LastName) });

                if (string.IsNullOrWhiteSpace(Email))
                    yield return new ValidationResult(
                        "Email is required for portal-enabled records.",
                        new[] { nameof(Email) });
            }

            bool needsSO =
                string.Equals(MaritalStatus, "Married", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(MaritalStatus, "Domestic Partnership", StringComparison.OrdinalIgnoreCase);

            if (needsSO)
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

            // ---- CRM validation ----
            var allowedStatus = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Lead", "Prospect", "Active", "Dormant" };

            if (string.IsNullOrWhiteSpace(CrmStatus) || !allowedStatus.Contains(CrmStatus.Trim()))
                yield return new ValidationResult(
                    "CRM status must be one of: Lead, Prospect, Active, Dormant.",
                    new[] { nameof(CrmStatus) });

            var allowedPriority = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Low", "Normal", "High", "Urgent" };

            if (string.IsNullOrWhiteSpace(CrmPriority) || !allowedPriority.Contains(CrmPriority.Trim()))
                yield return new ValidationResult(
                    "Priority must be one of: Low, Normal, High, Urgent.",
                    new[] { nameof(CrmPriority) });

            var hasNextDate = CrmNextDate.HasValue;
            var hasNextText = !string.IsNullOrWhiteSpace(CrmNextText);

            if (hasNextDate && !hasNextText)
                yield return new ValidationResult(
                    "Next Action text is required when Next Action date is set.",
                    new[] { nameof(CrmNextText) });

            if (hasNextText && !hasNextDate)
                yield return new ValidationResult(
                    "Next Action date is required when Next Action text is set.",
                    new[] { nameof(CrmNextDate) });
        }
    }
}
