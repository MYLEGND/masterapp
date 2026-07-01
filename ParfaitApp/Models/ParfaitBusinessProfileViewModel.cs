using System.ComponentModel.DataAnnotations;

namespace ParfaitApp.Models;

public sealed class ParfaitBusinessProfileViewModel
{
    [Display(Name = "Store name")]
    [Required]
    [MaxLength(150)]
    public string StoreName { get; set; } = "Parfait";

    [Display(Name = "Business type")]
    [Required]
    [MaxLength(120)]
    public string BusinessType { get; set; } = "Apparel / Ecommerce";

    [Display(Name = "Global store checkout link")]
    [Url]
    [MaxLength(2048)]
    public string? GlobalStoreCheckoutUrl { get; set; }

    public string DomainStatus { get; set; } = "ParfaitApp domain profile active";
    public string AnalyticsStatus { get; set; } = "Managed in Analytics";
    public string TrustStatus { get; set; } = "Managed in Analytics";
}
