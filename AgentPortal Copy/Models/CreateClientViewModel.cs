using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace AgentPortal.Models
{
    public class CreateClientViewModel : IValidatableObject
    {
        [Required(ErrorMessage = "Choose whether this is a lead, client, or business client.")]
        public string RecordType { get; set; } = "Lead";

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

        // ===================== CRM (PERSISTED TO DB) =====================

        public string CrmStatus { get; set; } = "Lead";   // Lead | Prospect | Active | Dormant

        public string CrmPriority { get; set; } = "Normal"; // Low | Normal | High | Urgent

        // Comma separated tags: "Mortgage, Gym, Referral"
        public string? CrmTags { get; set; }

        [DataType(DataType.Date)]
        public DateTime? CrmLastTouch { get; set; }

        [DataType(DataType.Date)]
        public DateTime? CrmNextDate { get; set; }

        // "Send quote", "Call to schedule review", etc.
        public string? CrmNextText { get; set; }

        // Extra relationship notes collected at creation (optional)
        public string? CrmNotes { get; set; }

        public string PipelineStage { get; set; } = "NewLead";

        // Agent licensing (persisted per-agent and reused)
        public string? AgentNpn { get; set; }
        [Phone(ErrorMessage = "Enter a valid agent phone number.")]
        [StringLength(64)]
        public string? AgentPhone { get; set; }

        public string? OneTimePassword { get; set; }

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

            var allowedPipelineStages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "NewLead", "Opportunities", "Contacted", "Qualified", "MeetingScheduled", "ProposalSent", "ApplicationStarted", "Submitted", "BusinessClient", "ClosedWon", "ClosedLost", "Nurture", "Client" };

            if (string.IsNullOrWhiteSpace(PipelineStage) || !allowedPipelineStages.Contains(PipelineStage.Trim()))
                yield return new ValidationResult(
                    "Pipeline stage is invalid.",
                    new[] { nameof(PipelineStage) });

            // keep Next Action structured
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

            if (!string.IsNullOrWhiteSpace(OneTimePassword) && OneTimePassword.Trim().Length < 8)
                yield return new ValidationResult(
                    "Password must be at least 8 characters.",
                    new[] { nameof(OneTimePassword) });
        }
    }
}
