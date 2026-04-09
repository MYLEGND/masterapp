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

        [Required(ErrorMessage = "Age is required")]
        [Range(0, 120, ErrorMessage = "Age must be between 0 and 120")]
        public int Age { get; set; }

        [Required(ErrorMessage = "Marital Status is required")]
        public required string MaritalStatus { get; set; }

        // ===================== ENGAGEMENT / NEED DISCOVERY =====================
        [Required(ErrorMessage = "Primary Reason is required")]
        public required string PrimaryReason { get; set; }

        public string CurrentCoverage { get; set; } = "";

        [Required(ErrorMessage = "Coverage Amount is required")]
        public required string CoverageAmount { get; set; }

        public string PolicyTypeInterest { get; set; } = "";

        public string Timeline { get; set; } = "";

        // ===================== CONTACT PREFERENCES =====================
        [Required(ErrorMessage = "Preferred Contact Method is required")]
        public required string ContactMethod { get; set; }

        [Required(ErrorMessage = "Best Time To Contact is required")]
        public required string BestTimeToContact { get; set; }

                [Display(Name = "Acknowledged Disclaimer")]
        [Required(ErrorMessage = "You must acknowledge the disclaimer.")]
        public bool AcknowledgedDisclaimer { get; set; } = false;
    }
}
