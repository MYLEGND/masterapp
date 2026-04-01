using Shared.ClientExperience;

namespace ClientApp.Models;

public sealed class ProtectionSnapshotViewModel
{
    public Guid ClientProfileId { get; init; }
    public string ClientUserId { get; init; } = string.Empty;
    public string ClientDisplayName { get; init; } = "Client";
    public string MaritalStatus { get; init; } = string.Empty;
    public int? Age { get; init; }
    public bool IsAgentView { get; init; }
    public bool IsBusinessClient { get; init; }
    public ProtectionSnapshotState DefaultState { get; init; } = new();
}