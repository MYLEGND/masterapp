namespace Domain.Entities;

public sealed class CommerceBusinessStorefrontSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CommerceBusinessId { get; set; }
    public CommerceBusiness? CommerceBusiness { get; set; }

    public string BrandHeadline { get; set; } = string.Empty;
    public string BrandSubheadline { get; set; } = string.Empty;
    public string AccentColor { get; set; } = "#926950";
    public string LogoUrl { get; set; } = string.Empty;
    public string StorefrontStatus { get; set; } = "Draft";

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
