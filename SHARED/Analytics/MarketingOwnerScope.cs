namespace Shared.Analytics;

/// <summary>Server-resolved ownership. This value is not a browser authorization credential.</summary>
public sealed record MarketingOwnerScope
{
    public string OwnerType { get; }
    public Guid? AgentTrackingProfileId { get; }
    public Guid? CommerceBusinessId { get; }
    public string Key => OwnerType switch
    {
        "agent" => $"agent:{AgentTrackingProfileId:N}",
        "business" => $"business:{CommerceBusinessId:N}",
        _ => "founder"
    };

    private MarketingOwnerScope(string type, Guid? agent = null, Guid? business = null)
    {
        OwnerType = type;
        AgentTrackingProfileId = agent;
        CommerceBusinessId = business;
    }

    public static MarketingOwnerScope Founder { get; } = new("founder");
    public static MarketingOwnerScope Agent(Guid id) => id != Guid.Empty
        ? new("agent", agent: id) : throw new ArgumentException("An agent owner is required.", nameof(id));
    public static MarketingOwnerScope Business(Guid id) => id != Guid.Empty
        ? new("business", business: id) : throw new ArgumentException("A business owner is required.", nameof(id));
}
