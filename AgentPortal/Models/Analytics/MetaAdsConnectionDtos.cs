using System;

namespace AgentPortal.Models.Analytics;

public sealed class MetaAdsConnectionRecord
{
    public Guid AgentTrackingProfileId { get; set; }
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

public sealed class MetaAdsConnectionStatusDto
{
    public bool Connected { get; set; }
    public bool RequiresAgentScope { get; set; }
    public Guid? AgentTrackingProfileId { get; set; }
    public string? AccountId { get; set; }
    public string? AccountName { get; set; }
    public string? BusinessId { get; set; }
    public string? BusinessName { get; set; }
    public string? MetaUserName { get; set; }
    public DateTime? ConnectedUtc { get; set; }
    public DateTime? AccessTokenExpiresUtc { get; set; }
    public string? Message { get; set; }
}
