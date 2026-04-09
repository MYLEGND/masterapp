using System.ComponentModel.DataAnnotations;

namespace Protect_Website.Models
{
    public class LifeQuoteFormModel
    {
        // ===================== PERSONAL INFO =====================
        [Required(ErrorMessage = "First Name is required")]
        public required string FirstName { get; set; }

        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email address")]
        public required string Email { get; set; }

        [Required(ErrorMessage = "Phone is required")]
        [Phone(ErrorMessage = "Invalid phone number")]
        public required string Phone { get; set; }

        [Required(ErrorMessage = "Age range is required")]
        public required string AgeRange { get; set; }

        [Required(ErrorMessage = "Protect focus is required")]
        public required string ProtectFocus { get; set; }

        [Display(Name = "MarketingEmailConsent")]
        [Required(ErrorMessage = "You must acknowledge the disclaimer.")]
        public bool MarketingEmailConsent { get; set; } = false;

        public string? PageKey { get; set; }
    }
}
