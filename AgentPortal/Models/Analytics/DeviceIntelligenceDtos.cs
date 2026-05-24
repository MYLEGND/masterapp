using AgentPortal.Services.Analytics;

namespace AgentPortal.Models.Analytics;

public sealed class DeviceIntelligenceDto
{
    public string RangeLabel { get; set; } = "";
    public TrafficType TrafficType { get; set; } = TrafficType.All;
    public int Sessions { get; set; }
    public int Events { get; set; }
    public int FormStarts { get; set; }
    public int ConfirmedLeads { get; set; }
    public List<DeviceIntelligenceRowDto> Devices { get; set; } = new();
    public List<DeviceIntelligenceRowDto> Browsers { get; set; } = new();
    public List<DeviceIntelligenceRowDto> OperatingSystems { get; set; } = new();
    public List<DeviceIntelligenceRowDto> Viewports { get; set; } = new();
    public List<DeviceIntelligenceRowDto> Screens { get; set; } = new();
    public List<DeviceIntelligenceRowDto> TimeZones { get; set; } = new();
    public List<DeviceIntelligenceRowDto> Languages { get; set; } = new();
}

public sealed class DeviceIntelligenceRowDto
{
    public string Label { get; set; } = "Unknown";
    public int Sessions { get; set; }
    public int Events { get; set; }
    public int CtaClicks { get; set; }
    public int FormStarts { get; set; }
    public int SubmitAttempts { get; set; }
    public int ConfirmedLeads { get; set; }
    public decimal StartRate { get; set; }
    public decimal LeadRate { get; set; }
}
