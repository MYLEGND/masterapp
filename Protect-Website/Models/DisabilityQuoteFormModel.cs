using System.ComponentModel.DataAnnotations;

namespace Protect_Website.Models
{
    public class DisabilityQuoteFormModel
    {
        // ===================== PERSONAL INFO =====================
        [Required(ErrorMessage = "First Name is required")]
        public string FirstName { get; set; } = "";

        [Required(ErrorMessage = "Last Name is required")]
        public string LastName { get; set; } = "";

        [Required(ErrorMessage = "Email is required"), EmailAddress]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Phone is required"), Phone]
        public string Phone { get; set; } = "";

        [Required(ErrorMessage = "Age is required")]
        [Range(0, 120, ErrorMessage = "Age must be between 0 and 120")]
        public int? Age { get; set; } // nullable int, no default

        // ===================== INCOME & WORK =====================
        [Required(ErrorMessage = "Employment Type is required")]
        public string EmploymentType { get; set; } = "";

        [Required(ErrorMessage = "Occupation is required")]
        public string Occupation { get; set; } = "";

        public string IncomeRange { get; set; } = "";

        // ===================== COVERAGE AWARENESS =====================
        public string CurrentCoverage { get; set; } = "";

        [Required(ErrorMessage = "Income Protection Importance is required")]
        public string IncomeProtectionImportance { get; set; } = "";

        public string Timeline { get; set; } = "";

        // ===================== CONTACT PREFERENCES =====================
        [Required(ErrorMessage = "Preferred contact method is required")]
        public string ContactMethod { get; set; } = "";

        [Required(ErrorMessage = "Best time to contact is required")]
        public string BestTimeToContact { get; set; } = "";

                [Display(Name = "Acknowledged Disclaimer")]
        [Required(ErrorMessage = "You must acknowledge the disclaimer.")]
        public bool AcknowledgedDisclaimer { get; set; } = false;

        // ── Attribution (populated by JS before submit, persisted server-side) ──
        public string? SessionId { get; set; }
        public string? VisitorId { get; set; }
        public string? UtmSource { get; set; }
        public string? UtmMedium { get; set; }
        public string? UtmCampaign { get; set; }
        public string? UtmTerm { get; set; }
        public string? UtmContent { get; set; }
        public string? Fbclid { get; set; }
        public string? ReferrerUrl { get; set; }
        public string? LandingPageUrl { get; set; }
    }
}
