using System.ComponentModel.DataAnnotations;

namespace Protect_Website.Models
{
    public class LifeQuoteFormModel
    {
        // ===================== PERSONAL INFO =====================
        [Required(ErrorMessage = "First Name is required")]
        public required string FirstName { get; set; }

        [Required(ErrorMessage = "Last Name is required")]
        public required string LastName { get; set; }

        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email address")]
        public required string Email { get; set; }

        [Required(ErrorMessage = "Phone is required")]
        [Phone(ErrorMessage = "Invalid phone number")]
        public required string Phone { get; set; }

        [Required(ErrorMessage = "State is required")]
        public string? State { get; set; }

        public string? AgeRange { get; set; }

        public string? ProtectFocus { get; set; }

        public string? Answer1 { get; set; }
        public string? Answer2 { get; set; }
        public string? Answer3 { get; set; }
        public string? Answer4 { get; set; }

        [Display(Name = "MarketingEmailConsent")]
        [Required(ErrorMessage = "You must acknowledge the disclaimer.")]
        public bool MarketingEmailConsent { get; set; } = false;

        public string? PageKey { get; set; }

        /// <summary>Product type for downstream routing/analytics (life/term/wholelife/finalexpense/mortgage/iul).</summary>
        [Required]
        public string ProductType { get; set; } = "life";

        /// <summary>Canonical offer key (life/term/wholelife/finalexpense/mortgage/iul).</summary>
        [Required]
        public string OfferKey { get; set; } = "life";

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
