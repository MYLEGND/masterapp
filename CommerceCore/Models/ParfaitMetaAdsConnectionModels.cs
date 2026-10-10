namespace ParfaitApp.Models;

public sealed class ParfaitMetaAdsConnectionRecord
{
    public string AccessToken { get; set; } = "";
    public DateTime? AccessTokenExpiresUtc { get; set; }
    public string? AccountId { get; set; }
    public string? AccountName { get; set; }
    public string? BusinessId { get; set; }
    public string? BusinessName { get; set; }
    public string? MetaUserId { get; set; }
    public string? MetaUserName { get; set; }
    public DateTime ConnectedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ParfaitMetaAdsConnectionStatusDto
{
    public bool Connected { get; set; }
    public string? AccountId { get; set; }
    public string? AccountName { get; set; }
    public string? BusinessId { get; set; }
    public string? BusinessName { get; set; }
    public string? MetaUserName { get; set; }
    public DateTime? ConnectedUtc { get; set; }
    public DateTime? AccessTokenExpiresUtc { get; set; }
    public string? Message { get; set; }
}
