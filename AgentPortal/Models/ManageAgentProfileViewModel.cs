using System.ComponentModel.DataAnnotations;

namespace AgentPortal.Models
{
    public class ManageAgentProfileViewModel
    {
        [Display(Name = "Full name")]
        [Required]
        [MaxLength(150)]
        public string FullName { get; set; } = "";

        [Display(Name = "Title")]
        [MaxLength(120)]
        public string? Title { get; set; }

        [Display(Name = "Email (from Azure AD)")]
        public string Email { get; set; } = "";

        [Display(Name = "Phone")]
        [Phone]
        [MaxLength(30)]
        public string? Phone { get; set; }

        [Display(Name = "Short bio")]
        [MaxLength(280)]
        public string? ShortBio { get; set; }

        [Display(Name = "NPN")]
        [MaxLength(30)]
        public string? Npn { get; set; }

        [Display(Name = "Meta Pixel ID")]
        [MaxLength(64)]
        [RegularExpression(@"^\s*\d+\s*$", ErrorMessage = "Enter only the numeric Meta Pixel ID.")]
        public string? MetaPixelId { get; set; }

        [Display(Name = "Enable agent booking")]
        public bool BookingEnabled { get; set; }

        [Display(Name = "Microsoft Bookings embed URL")]
        [MaxLength(2048)]
        [Url]
        public string? MicrosoftBookingsEmbedUrl { get; set; }

        [Display(Name = "Fallback booking URL")]
        [MaxLength(2048)]
        [Url]
        public string? FallbackBookingUrl { get; set; }

        [Display(Name = "Booking mailbox or page ID")]
        [MaxLength(320)]
        public string? BookingPageIdOrMailbox { get; set; }

        [Display(Name = "Calendar email")]
        [MaxLength(320)]
        [EmailAddress]
        public string? CalendarEmail { get; set; }

        [Display(Name = "Prefer modal on mobile")]
        public bool PreferModalOnMobile { get; set; } = true;

        public bool HasSecureMetaCapiAccessToken { get; set; }
    }
}
