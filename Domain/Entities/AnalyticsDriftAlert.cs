using System;

namespace Domain.Entities;

public class AnalyticsDriftAlert
{
    public long Id { get; set; }
    public string IncidentKey { get; set; } = string.Empty;
    public string MetricKey { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Severity { get; set; } = "Low";
    public string MetricUnit { get; set; } = "count";
    public decimal CurrentValue { get; set; }
    public decimal BaselineValue { get; set; }
    public decimal DeviationPercent { get; set; }
    public string ScopeKey { get; set; } = "global";
    public bool IsActive { get; set; }
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndUtc { get; set; }
    public DateTime FirstDetectedUtc { get; set; }
    public DateTime LastDetectedUtc { get; set; }
    public DateTime ObservedUtc { get; set; }
    public DateTime? ResolvedUtc { get; set; }
    public DateTime? LastNotifiedUtc { get; set; }
    public string? Summary { get; set; }
    public string? DetailsJson { get; set; }
}
