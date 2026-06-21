namespace ParfaitApp.Models;

public sealed class ParfaitBusinessProfileViewModel
{
    public string StoreName { get; set; } = "Parfait";
    public string BusinessType { get; set; } = "Apparel / Ecommerce";
    public string DomainStatus { get; set; } = "ParfaitApp domain profile active";
    public string MetaPixelStatus { get; set; } = "Pending profile storage";
    public string MetaCapiStatus { get; set; } = "Pending secure Meta OAuth connection";
    public string AnalyticsStatus { get; set; } = "Pending shared analytics integration";
    public string TrustStatus { get; set; } = "Pending visitor intelligence integration";
}
