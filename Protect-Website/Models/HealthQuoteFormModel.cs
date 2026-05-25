using System.ComponentModel.DataAnnotations;

namespace Protect_Website.Models
{
    public class HealthQuoteFormModel
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
        public int? Age { get; set; }

        public string AgeRange { get; set; } = "";

        // ===================== COVERAGE CONTEXT =====================
        public string CoverageType { get; set; } = "";

        public string CurrentCoverage { get; set; } = "";

        [Required(ErrorMessage = "Primary Concern is required")]
        public string PrimaryConcern { get; set; } = "";

        [Required(ErrorMessage = "Household Size is required")]
        public string HouseholdSize { get; set; } = "";

        public string Timeline { get; set; } = "";

        // ===================== CONTACT =====================
        public string? ContactMethod { get; set; }

        public string? BestTimeToContact { get; set; }

        // ===================== DISCLAIMER =====================
                [Display(Name = "Acknowledged Disclaimer")]
        [Required(ErrorMessage = "You must acknowledge the disclaimer.")]
        public bool AcknowledgedDisclaimer { get; set; } = false;

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
