namespace Domain.Entities;

/// <summary>Short-lived, typed profile reachability shared by both web hosts and native devices.</summary>
public sealed class MessagingConnectionLease
{
    public string ConnectionId { get; set; } = string.Empty;
    public Guid ProfileId { get; set; }
    public string ParticipantType { get; set; } = string.Empty;
    public DateTime ExpiresUtc { get; set; }
}
