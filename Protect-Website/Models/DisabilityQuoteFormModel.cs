using System.ComponentModel.DataAnnotations;

namespace Protect_Website.Models
{
    public class DisabilityQuoteFormModel
    {
        // ===================== PERSONAL INFO =====================
        [Required(ErrorMessage = "First Name is required")]
        public string FirstName { get; set; } = "";

        public string? LastName { get; set; }

        [EmailAddress]
        public string? Email { get; set; }

        [Required(ErrorMessage = "Phone is required"), Phone]
        public string Phone { get; set; } = "";

        public string? State { get; set; }

        [Required(ErrorMessage = "Age is required")]
        [Range(0, 120, ErrorMessage = "Age must be between 0 and 120")]
        public int? Age { get; set; } // nullable int, no default

        public string AgeRange { get; set; } = "";

        // ===================== INCOME & WORK =====================
        [Required(ErrorMessage = "Employment Type is required")]
        public string EmploymentType { get; set; } = "";

        public string? Occupation { get; set; }

        public string IncomeRange { get; set; } = "";

        // ===================== COVERAGE AWARENESS =====================
        public string CurrentCoverage { get; set; } = "";

        [Required(ErrorMessage = "Income Protection Importance is required")]
        public string IncomeProtectionImportance { get; set; } = "";

        public string Timeline { get; set; } = "";

        // ===================== CONTACT PREFERENCES =====================
        public string? ContactMethod { get; set; }

        public string? BestTimeToContact { get; set; }

                [Display(Name = "Acknowledged Disclaimer")]
        [Required(ErrorMessage = "You must acknowledge the disclaimer.")]
        public bool AcknowledgedDisclaimer { get; set; } = false;

        public string? PageKey { get; set; }
        public string? PageVariant { get; set; }
        public string? PageMode { get; set; }

        // ── Attribution (populated by JS before submit, persisted server-side) ──
        public string? SessionId { get; set; }
        public string? VisitorId { get; set; }
        public string? UtmSource { get; set; }
        public string? UtmMedium { get; set; }
        public string? UtmCampaign { get; set; }
        public string? UtmId { get; set; }
        public string? UtmTerm { get; set; }
        public string? UtmContent { get; set; }
        public string? MetaCampaignId { get; set; }
        public string? MetaAdSetId { get; set; }
        public string? MetaAdId { get; set; }
        public string? Fbclid { get; set; }
        public string? ReferrerUrl { get; set; }
        public string? LandingPageUrl { get; set; }
    }
}
