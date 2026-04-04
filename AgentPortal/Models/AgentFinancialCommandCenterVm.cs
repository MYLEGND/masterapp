namespace AgentPortal.Models;

public sealed class AgentFinancialCommandCenterVm
{
    public int BusinessClientCount { get; set; }
    public int ConnectedClientCount { get; set; }
    public decimal AggregateRevenueYtd { get; set; }
    public decimal AggregateNetProfitYtd { get; set; }
    public DateTime? LatestSyncUtc { get; set; }
    public List<AgentFinancialClientRowVm> Clients { get; set; } = new();
}

public sealed class AgentFinancialClientRowVm
{
    public string ClientUserId { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public bool IsQuickBooksConnected { get; set; }
    public string? RealmId { get; set; }
    public DateTime? LastSyncUtc { get; set; }
    public string? LastSyncStatus { get; set; }
    public decimal RevenueYtd { get; set; }
    public decimal NetProfitYtd { get; set; }
}
