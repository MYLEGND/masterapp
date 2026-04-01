using System;

namespace Domain.Entities;

public class UnderwritingRecord
{
    public Guid Id { get; set; }
    public string LeadId { get; set; } = string.Empty;
    public string LeadName { get; set; } = string.Empty;
    public string AgentUserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string QueueKey { get; set; } = string.Empty;
    public string ScopeKey { get; set; } = string.Empty;
    public string PageTitle { get; set; } = string.Empty;
    public bool IsDraft { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
