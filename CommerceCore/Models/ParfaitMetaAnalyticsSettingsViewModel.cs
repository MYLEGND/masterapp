using System.ComponentModel.DataAnnotations;

namespace ParfaitApp.Models;

public sealed class ParfaitMetaAnalyticsSettingsViewModel
{
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
    public string? AccountId { get; set; }
    public string? AccountName { get; set; }
    public string? BusinessId { get; set; }
    public string? BusinessName { get; set; }
    public string? MetaUserName { get; set; }
    public DateTime? ConnectedUtc { get; set; }
    public DateTime? AccessTokenExpiresUtc { get; set; }

    public string MetaPixelStatus => string.IsNullOrWhiteSpace(MetaPixelId) ? "Pending profile storage" : "Configured";
    public string MetaCapiStatus => HasSecureMetaCapiAccessToken ? "Configured securely" : "Pending secure Meta OAuth connection";
    public string DatasetStatus => HasActiveMetaAdsConnection ? "Connected" : "Pending";
    public string LastSyncLabel => ConnectedUtc.HasValue
        ? ConnectedUtc.Value.ToLocalTime().ToString("MMM d, yyyy h:mm tt")
        : "Not synced yet";
}
