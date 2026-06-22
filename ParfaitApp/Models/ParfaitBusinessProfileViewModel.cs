using System.ComponentModel.DataAnnotations;

namespace ParfaitApp.Models;

public sealed class ParfaitBusinessProfileViewModel
{
    [Required]
    public string StoreName { get; set; } = "Parfait";

    [Required]
    public string BusinessType { get; set; } = "Apparel / Ecommerce";

    [Url]
    public string? GlobalStoreCheckoutUrl { get; set; }

    public string? MetaPixelId { get; set; }
    public string? MetaTestEventCode { get; set; }

    public string DomainStatus { get; set; } = "ParfaitApp domain profile active";
    public string MetaPixelStatus => string.IsNullOrWhiteSpace(MetaPixelId) ? "Pending profile storage" : "Configured";
    public string MetaCapiStatus { get; set; } = "Pending secure Meta OAuth connection";
    public string AnalyticsStatus { get; set; } = "Pending shared analytics integration";
    public string TrustStatus { get; set; } = "Pending visitor intelligence integration";
}
