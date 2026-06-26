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

    [Display(Name = "Meta Pixel ID")]
    [MaxLength(64)]
    [RegularExpression(@"^\s*\d+\s*$", ErrorMessage = "Enter only the numeric Meta Pixel ID.")]
    public string? MetaPixelId { get; set; }

    [Display(Name = "Meta Test Event Code")]
    [MaxLength(128)]
    public string? MetaTestEventCode { get; set; }

    public bool HasSecureMetaCapiAccessToken { get; set; }
    public bool HasActiveMetaAdsConnection { get; set; }
    public string MetaConnectionLabel { get; set; } = "Meta Ads not connected for Parfait.";

    public string DomainStatus { get; set; } = "ParfaitApp domain profile active";
    public string MetaPixelStatus => string.IsNullOrWhiteSpace(MetaPixelId) ? "Pending profile storage" : "Configured";
    public string MetaCapiStatus => HasSecureMetaCapiAccessToken ? "Configured securely" : "Pending secure Meta OAuth connection";
    public string AnalyticsStatus { get; set; } = "Pending shared analytics integration";
    public string TrustStatus { get; set; } = "Pending visitor intelligence integration";
}
