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
    }
}
