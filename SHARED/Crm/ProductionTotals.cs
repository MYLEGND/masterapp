namespace AgentPortal.Services;

public sealed class ProductionTotals
{
    public decimal Submitted { get; set; }
    public decimal Issued { get; set; }
    public decimal Paid { get; set; }
    public int CountSubmitted { get; set; }
    public int CountIssued { get; set; }
    public int CountPaid { get; set; }
    public decimal Personal { get; set; }
    public int CountPersonal { get; set; }
}
