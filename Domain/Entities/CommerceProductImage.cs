namespace Domain.Entities;

public sealed class CommerceProductImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommerceProductId { get; set; }
    public CommerceProduct? CommerceProduct { get; set; }

    public string ExternalImageKey { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string AltText { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public int DisplayOrder { get; set; }
    public string ObjectFit { get; set; } = "cover";
    public int ObjectPositionX { get; set; } = 50;
    public int ObjectPositionY { get; set; } = 50;
    public decimal Zoom { get; set; } = 1.0m;
}
